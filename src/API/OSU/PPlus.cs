using System.Text.Json.Nodes;
using System.Net;
using KanonBot.Serializer;
using System.IO;
using KanonBot.Database;

namespace KanonBot.API.OSU
{
    public partial class Client
    {
        public static class PPlus
        {
            private static readonly Config.Base config = Config.inner!;
            private static string Token = "";
            private static long TokenExpireTime = 0;
            private static readonly string pppEndPoint = "http://localhost:9001/";
            private static readonly SemaphoreSlim tokenLock = new SemaphoreSlim(1, 1);

            static IFlurlRequest pplus()
            {
                var ep = config.osu?.pppEndPoint ?? pppEndPoint;
                return ep.AllowHttpStatus(404, 401);
            }

            // 检查token是否有效（基于时间）
            private static bool IsTokenValid()
            {
                return !string.IsNullOrEmpty(Token) && 
                       DateTimeOffset.UtcNow.ToUnixTimeSeconds() < TokenExpireTime;
            }

            // 确保有有效的token（double-check 模式降低锁竞争）
            private static async Task<bool> EnsureValidToken()
            {
                if (IsTokenValid())
                    return true;

                return await RefreshToken();
            }

            // 刷新token
            private static async Task<bool> RefreshToken()
            {
                try
                {
                    var result = await pplus()
                        .AppendPathSegments("auth", "token")
                        .SetQueryParam("clientId", config.osu?.pppClientId)
                        .SetQueryParam("clientSecret", config.osu?.pppClientSecret)
                        .PostAsync();

                    var body = await result.GetJsonAsync<JsonObject>();
                    
                    if (body["data"] != null)
                    {
                        // 在异步锁内更新共享状态
                        await tokenLock.WaitAsync();
                        try
                        {
                            Token = body["data"]?.ToString() ?? "";
                            // 设置token过期时间（假设token有效期1小时，提前5分钟刷新）
                            TokenExpireTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3300; // 55分钟
                        }
                        finally
                        {
                            tokenLock.Release();
                        }
                        return true;
                    }
                    else
                    {
                        Log.Error("获取token失败, 返回Body: \n{0}", body.ToString());
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("刷新token时发生异常: {0}", ex.Message);
                    return false;
                }
            }

            // 执行带token的请求，自动处理401重试
            private static async Task<IFlurlResponse> ExecuteRequestWithToken(
                Func<IFlurlRequest> requestBuilder,
                bool isPost = false
            )
            {
                // 确保有有效token
                if (!await EnsureValidToken())
                {
                    throw new InvalidOperationException("无法获取有效的访问token");
                }

                var request = requestBuilder();
                if (!string.IsNullOrEmpty(Token))
                {
                    request = request.WithHeader("Authorization", $"Bearer {Token}");
                }

                var response = isPost ? await request.PostAsync() : await request.GetAsync();

                // 如果收到401，尝试刷新token并重试一次
                if (response.StatusCode == 401)
                {
                    Log.Warning("收到401响应，尝试刷新token并重试");
                    
                    if (await RefreshToken())
                    {
                        var retryRequest = requestBuilder();
                        if (!string.IsNullOrEmpty(Token))
                        {
                            retryRequest = retryRequest.WithHeader("Authorization", $"Bearer {Token}");
                        }
                        response = isPost
                            ? await retryRequest.PostAsync()
                            : await retryRequest.GetAsync();
                    }
                    else
                    {
                        throw new InvalidOperationException("token刷新失败，无法完成请求");
                    }
                }

                return response;
            }

            public static async Task<Models.PPlusData.UserDataNext?> GetUserPlusDataNext(long uid)
            {
                const int maxAttempts = 3;
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        var response = await ExecuteRequestWithToken(() =>
                            pplus()
                                .AppendPathSegments("player", "info")
                                .SetQueryParam("id", uid)
                        );

                        if (response.StatusCode == 404)
                        {
                            return null;
                        }

                        var s = await response.GetJsonAsync<JsonObject>();
                        return s["data"]?.ToObject<Models.PPlusData.UserDataNext>();
                    }
                    catch (FlurlHttpException ex) when (
                        attempt < maxAttempts
                        && ex.Call.Response?.StatusCode >= 500
                    )
                    {
                        Log.Warning(
                            "PP+ service temporarily failed for osu! user {OsuId}; retrying ({Attempt}/{MaxAttempts})",
                            uid,
                            attempt,
                            maxAttempts
                        );
                        await Task.Delay(TimeSpan.FromSeconds(attempt));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to get PP+ data for osu! user {OsuId}", uid);
                        return null;
                    }
                }

                return null;
            }

            public static async Task<Models.PPlusData.UserDataNext?> UpdateUserPlusDataNext(long uid)
            {
                try
                {
                    var response = await ExecuteRequestWithToken(() => 
                        pplus()
                            .AppendPathSegments("player", "update")
                            .SetQueryParam("id", uid)
                            // 首次初始化要全量拉分，实测重玩家可达 3-5 分钟；
                            // !ppvs 需要"等到算完再发"，所以这里给足 360s。
                            .WithTimeout(TimeSpan.FromSeconds(360)),
                        isPost: true
                    );

                    if (response.StatusCode == 404)
                    {
                        return null;
                    }

                    var s = await response.GetJsonAsync<JsonObject>();
                    var data = s["data"]?.ToObject<Models.PPlusData.UserDataNext>();
                    return data;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to update PP+ data for osu! user {OsuId}", uid);
                    return null;
                }
            }
        }
    }
}

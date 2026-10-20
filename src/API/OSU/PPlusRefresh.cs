using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace KanonBot.API.OSU;

/// <summary>
/// PP+ 缓存策略（2026-10 需求）：
///
/// - <c>!info</c> 只读缓存出图，绝不等待 PP+；缓存缺失或超过 24h 时，
///   在后台发起一次 <c>player/update</c>，成功后写回缓存。
/// - 后台刷新失败进入 10 分钟冷却，避免每次查询都去打 9001。
/// - 同一玩家同一时刻只允许一个刷新任务（并发合并）。
/// - <c>!ppvs</c> 需要"等到算完再发"，走 <see cref="GetOrCompute"/>。
/// </summary>
public static class PPlusRefresh
{
    /// <summary>缓存有效期。</summary>
    public const long TtlSeconds = 24 * 60 * 60;

    /// <summary>一次刷新失败后的冷却时间。</summary>
    public const long CooldownSeconds = 10 * 60;

    private static readonly ConcurrentDictionary<long, byte> Inflight = new();
    private static readonly ConcurrentDictionary<long, long> CooldownUntil = new();

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>updatedAt 为 Unix 秒，0 或超过 24h 即视为过期。</summary>
    public static bool IsStale(long updatedAt) =>
        updatedAt <= 0 || Now() - updatedAt >= TtlSeconds;

    public static bool IsRefreshing(long uid) => Inflight.ContainsKey(uid);

    public static bool InCooldown(long uid) =>
        CooldownUntil.TryGetValue(uid, out var until) && until > Now();

    /// <summary>
    /// 在后台刷新该玩家的 PP+。已经有任务在跑、或处于冷却中时直接返回。
    /// </summary>
    public static void RequestRefresh(long uid)
    {
        if (uid <= 0 || InCooldown(uid))
            return;
        if (!Inflight.TryAdd(uid, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var fresh = await Client.PPlus.UpdateUserPlusDataNext(uid);
                if (fresh is not null)
                {
                    await Database.Client.UpdateOsuPPlusDataNext(fresh);
                    CooldownUntil.TryRemove(uid, out _);
                }
                else
                {
                    CooldownUntil[uid] = Now() + CooldownSeconds;
                }
            }
            catch
            {
                CooldownUntil[uid] = Now() + CooldownSeconds;
            }
            finally
            {
                Inflight.TryRemove(uid, out _);
            }
        });
    }

    /// <summary>
    /// 给需要"算完再发"的命令用（两人对比）：缓存新鲜就直接用，
    /// 否则阻塞等待 PP+ 计算完成，并把结果写回缓存。失败时退回旧缓存。
    /// </summary>
    public static async Task<Models.PPlusData.UserDataNext?> GetOrCompute(long uid)
    {
        var (cached, updatedAt) = await Database.Client.GetOsuPPlusDataNextWithTime(uid);
        if (cached is not null && !IsStale(updatedAt))
            return cached;

        var fresh = await Client.PPlus.UpdateUserPlusDataNext(uid);
        if (fresh is not null)
        {
            await Database.Client.UpdateOsuPPlusDataNext(fresh);
            return fresh;
        }
        return cached;
    }
}

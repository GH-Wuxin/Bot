using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Flurl.Http.Testing;
using KanonBot;
using KanonBot.Functions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using OsuMode = KanonBot.API.OSU.Mode;
using OsuUser = KanonBot.API.OSU.Models.UserExtended;

static class Contracts
{
    static int passed;
    static readonly string root = Path.Combine(Environment.GetEnvironmentVariable("KANON_TEST_DATA_DIR") ?? Path.GetTempPath(), "kanon-contracts-" + Guid.NewGuid().ToString("N"));
    static void Check(bool condition, string label) {
        if (!condition) throw new InvalidOperationException("FAIL: " + label);
        passed++; Console.WriteLine("PASS " + label);
    }
    static byte[] Png(byte red) {
        using var image = new Image<Rgba32>(8, 8, new Rgba32(red, 20, 30));
        using var stream = new MemoryStream(); image.Save(stream, new PngEncoder()); return stream.ToArray();
    }
    static async Task WaitFor(Func<bool> predicate) {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Background refresh did not finish"); await Task.Delay(10); }
    }
    static string Metadata(string folder) => Directory.GetFiles(folder, "*.cache.json").Single();
    static void Expire(string folder) {
        var file = Metadata(folder); var value = JsonNode.Parse(File.ReadAllText(file))!;
        value["ValidatedAt"] = DateTimeOffset.UtcNow.AddHours(-1); value["RetryAfter"] = DateTimeOffset.MinValue;
        File.WriteAllText(file, value.ToJsonString());
    }
    static bool Updated(string folder) {
        try { return JsonNode.Parse(File.ReadAllText(Metadata(folder)))!["ValidatedAt"]!.GetValue<DateTimeOffset>() > DateTimeOffset.UtcNow.AddMinutes(-1); } catch { return false; }
    }
    public static async Task Run() {
        Directory.CreateDirectory(root);
        Console.WriteLine("TEST_ROOT " + root);
        using (var http = new HttpTest()) {
            await KanonBot.API.IAM.Client.GetIamUserIdByExternalId("qq", "123");
            await KanonBot.API.IAM.Client.GetIamUserIdByOsuUid(42);
            await KanonBot.API.IAM.Client.GetIamUserIdsByPpysbUid(42);
            await KanonBot.API.IAM.Client.GetUserBindings("fixture");
            await KanonBot.API.IAM.Client.GetOsuBindings();
            await KanonBot.API.IAM.Client.GetPpySbBindings();
            await KanonBot.API.Kagami.Client.GetPublicKanonBotProfile("fixture");
            await KanonBot.API.Kagami.Client.GetKanonImages("fixture");
            await KanonBot.API.Kagami.Client.GetUserWearBadges("fixture");
            await KanonBot.API.Kagami.Client.GetUserBadges("fixture");
            await KanonBot.API.Kagami.Client.GetUserPermissions("fixture");
            Check(http.CallLog.Count == 0, "disabled IAM and linked profile reads perform no HTTP calls");
        }
        Config.inner!.iam!.apiKey = "TEST_CONFIGURED_KEY";
        using (var http = new HttpTest()) {
            http.RespondWithJson(new { userId = "test-identity" });
            var identity = await KanonBot.API.IAM.Client.GetIamUserIdByOsuUid(42);
            Check(identity == "test-identity" && http.CallLog.Count == 1, "enabled IAM still resolves the configured identity");
        }
        Config.inner.iam.apiKey = "CHANGE_ME_TEST_ONLY";
        Config.inner.kagami!.apiKey = "TEST_KAGAMI_KEY";
        using (var http = new HttpTest()) {
            http.RespondWithJson(new { });
            var profile = await KanonBot.API.Kagami.Client.GetPublicKanonBotProfile("fixture");
            Check(profile != null && http.CallLog.Count == 1, "independently configured Kagami remains available when IAM is disabled");
        }
        Config.inner.kagami.apiKey = "CHANGE_ME_TEST_ONLY";
        var original = new OsuUser { Id = 42, Mode = OsuMode.Mania, Username = "fixture" };
        var resolved = new Accounts.CommandUserResult { OsuId = 42, Mode = OsuMode.OSU, OsuProfile = original, OsuProfileMode = OsuMode.OSU };
        using (var http = new HttpTest()) {
            var (user, _) = await Utils.ResolveOsuUser(resolved);
            Check(ReferenceEquals(original, user) && http.CallLog.Count == 0, "same ID and queried mode reuse the exact profile object");
            Check(user!.Mode == OsuMode.Mania, "preferred mode is not mistaken for the queried statistics mode");
        }
        using (var http = new HttpTest()) {
            http.ForCallsTo("https://osu.ppy.sh/oauth/token").RespondWithJson(new { access_token = new string('t', Math.Max(256, Utils.TryGetConsoleWidth())), expires_in = 3600 });
            http.ForCallsTo("https://osu.test/api/v2/users/42/mania").RespondWithJson(new { id = 42, playmode = "mania", username = "fixture", statistics = new { pp = 1234.5 } });
            resolved.Mode = OsuMode.Mania;
            var (user, _) = await Utils.ResolveOsuUser(resolved);
            Check(!ReferenceEquals(original, user) && user!.Statistics.PP == 1234.5, "different queried mode fetches correct fresh statistics");
            Check(http.CallLog.Count(c => c.Request.Url.ToString().Contains("/users/42/mania")) == 1, "mode change makes exactly one profile fetch");
            var count = http.CallLog.Count; await Utils.ResolveOsuUser(resolved);
            Check(http.CallLog.Count == count, "newly fetched mode is reused within the same request");
        }
        var a = Png(80); var b = Png(190);
        var fresh = Path.Combine(root,"fresh");
        string first;
        using (var http = new HttpTest()) {
            http.RespondWith(() => new ByteArrayContent(a), headers: new { ETag = "\"a\"" });
            first = (await Utils.CacheRemoteImage(fresh,"https://image.test/a?v=1","avatar"))!;
            var again = await Utils.CacheRemoteImage(fresh,"https://image.test/a?v=1","avatar");
            Check(first == again && http.CallLog.Count == 1, "fresh cache returns locally without ETag HTTP validation");
            Check(File.ReadAllBytes(first).SequenceEqual(a), "image bytes are unchanged, with no resizing or recompression");
        }
        using (var http = new HttpTest()) {
            http.RespondWith(() => new ByteArrayContent(b), headers: new { ETag = "\"b\"" });
            var changed = await Utils.CacheRemoteImage(fresh,"https://image.test/a?v=2","avatar");
            Check(http.CallLog.Count == 1 && changed != first && File.ReadAllBytes(changed!).SequenceEqual(b), "URL/version change fetches a distinct current asset immediately");
            Check(File.ReadAllBytes(first).SequenceEqual(a), "new asset never replaces bytes held by an in-flight render");
        }
        var concurrency = Path.Combine(root,"concurrency");
        using (var http = new HttpTest()) {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            http.RespondWith(() => new DelayedContent(a, gate.Task));
            var tasks = Enumerable.Range(0,24).Select(_ => Utils.CacheRemoteImage(concurrency,"https://image.test/shared","asset")).ToArray();
            gate.SetResult(); var paths = await Task.WhenAll(tasks);
            Check(http.CallLog.Count == 1 && paths.Distinct().Count() == 1, "24 concurrent cold requests share one download");
            Check(File.ReadAllBytes(paths[0]!).SequenceEqual(a), "concurrent cache output retains the exact source pixels");
            Check(Directory.GetFiles(concurrency,"*.tmp").Length == 0, "atomic writes leave no partial files");
        }
        Expire(concurrency);
        using (var http = new HttpTest()) {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            http.RespondWith(() => new DelayedContent(b,gate.Task),headers:new {ETag="\"b\""});
            var staleTask = Utils.CacheRemoteImage(concurrency,"https://image.test/shared","asset");
            Check(staleTask.IsCompletedSuccessfully && File.ReadAllBytes((await staleTask)!).SequenceEqual(a), "expired cached asset returns immediately while refresh is waiting");
            gate.SetResult(); await WaitFor(()=>Updated(concurrency));
            var updated=await Utils.CacheRemoteImage(concurrency,"https://image.test/shared","asset");
            Check(File.ReadAllBytes(updated!).SequenceEqual(b), "completed background refresh supplies updated original bytes");
        }
        Expire(concurrency);
        using (var http = new HttpTest()) {
            http.SimulateTimeout();
            var preserved=await Utils.CacheRemoteImage(concurrency,"https://image.test/shared","asset");
            await WaitFor(()=> {try {return JsonNode.Parse(File.ReadAllText(Metadata(concurrency)))!["RetryAfter"]!.GetValue<DateTimeOffset>()>DateTimeOffset.UtcNow;}catch{return false;}});
            await Utils.CacheRemoteImage(concurrency,"https://image.test/shared","asset");
            Check(File.ReadAllBytes(preserved!).SequenceEqual(b) && http.CallLog.Count==1, "refresh timeout preserves the good asset and backs off retries");
        }
        var legacy=Path.Combine(root,"legacy"); Directory.CreateDirectory(legacy);
        File.WriteAllBytes(Path.Combine(legacy,"asset.png"),a); File.WriteAllText(Path.Combine(legacy,"asset.etag"),"\"legacy\"|png");
        using(var http=new HttpTest()) {
            http.RespondWith(status:304);
            var migrated=await Utils.CacheRemoteImage(legacy,"https://image.test/legacy","asset");
            await Utils.CacheRemoteImage(legacy,"https://image.test/legacy","asset");
            Check(migrated!=null&&File.ReadAllBytes(migrated).SequenceEqual(a)&&http.CallLog.Count==1,"legacy ETag cache migrates after validation without losing or altering the image");
        }
        var corrupt=Path.Combine(root,"corrupt");
        using(var http=new HttpTest()) {
            http.RespondWith(()=>new ByteArrayContent(a));
            var file=await Utils.CacheRemoteImage(corrupt,"https://image.test/corrupt","asset");
            File.WriteAllBytes(file!,new byte[]{1});
            var repaired=await Utils.CacheRemoteImage(corrupt,"https://image.test/corrupt","asset");
            Check(http.CallLog.Count==2&&File.ReadAllBytes(repaired!).SequenceEqual(a),"truncated cached file is downloaded again instead of reaching the renderer");
        }
        using(var http=new HttpTest()) {
            http.RespondWith("not an image");
            var invalid=await Utils.CacheRemoteImage(Path.Combine(root,"invalid"),"https://image.test/invalid","asset");
            Check(invalid==null,"invalid download is not accepted as an image");
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{passed,root}));
    }
    sealed class DelayedContent(byte[] bytes, Task gate) : HttpContent {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) {await gate; await stream.WriteAsync(bytes);}
        protected override bool TryComputeLength(out long length){length=bytes.Length;return true;}
    }
}

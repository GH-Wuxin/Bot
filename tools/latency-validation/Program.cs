using Flurl.Http;
using Flurl.Http.Configuration;
using KanonBot;

Config.inner = Config.Base.Default();
Config.inner.osu!.v2EndPoint = "https://osu.test/api/v2/";
Config.inner.iam!.baseUrl = "https://iam.test";
Config.inner.iam.apiKey = "CHANGE_ME_TEST_ONLY";
Config.inner.kagami!.baseUrl = "https://kagami.test";
FlurlHttp.Clients.WithDefaults(c => c.Settings.JsonSerializer = new DefaultJsonSerializer(KanonBot.Serializer.Json.Options));
try { await Contracts.Run(); }
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }

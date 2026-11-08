using CommandSystem;
using CommandSystem.Definition;
using CommandSystem.Parsing;
using KanonBot.Drivers;

namespace KanonBot.Functions
{
    public class BindCommand : ICommand
    {
        public CommandDef Definition =>
            new()
            {
                Name = "bind",
                Description = "Bind your account",
                Args =
                [
                    new() { Name = "code", Description = "Binding verification code or local osu! username", Prefix = ArgPrefix.None, Strategy = ParseStrategy.Simple },
                ],
                Flags =  [ ]
            };

        public async Task Execute(Target target, ParsedCommand cmd)
        {
            var input = cmd.RawArgs.Trim();
            var accInfo = Accounts.GetAccInfo(target);
            if (accInfo.platform == Platform.Unknown)
            {
                await target.Treply("account.platform_unknown");
                return;
            }

            if (!Config.IamEnabled)
            {
                await BindLocally(target, input, accInfo);
                return;
            }

            string provider;
            try
            {
                provider = API.IAM.Client.PlatformToProvider(accInfo.platform);
            }
            catch (NotSupportedException)
            {
                await target.Treply("bind.platform_unsupported");
                return;
            }

            if (provider is not ("qq" or "discord"))
            {
                await target.Treply("bind.platform_unsupported");
                return;
            }

            if (!string.IsNullOrWhiteSpace(input))
            {
                if (provider != "qq")
                {
                    return;
                }

                var code = input.Split(' ', 2, StringSplitOptions.TrimEntries)[0];
                if (code.Length != 6)
                {
                    await target.Treply("bind.code_format_error");
                    return;
                }

                var verifyResult = await API.IAM.Client.SubmitQqCode(code, accInfo.uid);
                switch (verifyResult)
                {
                    case API.IAM.VerifyResult.Success:
                        await target.Treply("bind.code_submitted");
                        return;
                    case API.IAM.VerifyResult.InvalidCode:
                        await target.Treply("bind.code_invalid");
                        return;
                    case API.IAM.VerifyResult.AlreadyBound:
                        await target.Treply("bind.already_bound_qq");
                        return;
                    case API.IAM.VerifyResult.InvalidApiKey:
                        Log.Error("IAM API Key is invalid for QQ submit-code");
                        await target.Treply("bind.service_error");
                        return;
                    case API.IAM.VerifyResult.Misconfigured:
                        Log.Error("IAM integration is misconfigured for QQ submit-code");
                        await target.Treply("bind.service_error");
                        return;
                    default:
                        await target.Treply("bind.submit_failed");
                        return;
                }
            }

            var sessionResult = await API.IAM.Client.StartBindSession(provider, accInfo.uid);
            switch (sessionResult.Type)
            {
                case API.IAM.BindSessionResultType.Success:
                    if (
                        sessionResult.Session == null
                        || string.IsNullOrWhiteSpace(sessionResult.Session.Url)
                    )
                    {
                        await target.Treply("bind.session_failed");
                        return;
                    }

                    await target.TprivateReply("bind.session_link", sessionResult.Session.Url);
                    return;
                case API.IAM.BindSessionResultType.AlreadyBound:
                    await target.Treply("bind.already_bound");
                    return;
                case API.IAM.BindSessionResultType.InvalidApiKey:
                    Log.Error(
                        "IAM API Key is invalid for provider {Provider} bind-session",
                        provider
                    );
                    await target.Treply("bind.service_error");
                    return;
                case API.IAM.BindSessionResultType.Misconfigured:
                    Log.Error(
                        "IAM integration is misconfigured for provider {Provider} bind-session",
                        provider
                    );
                    await target.Treply("bind.service_error");
                    return;
                default:
                    await target.Treply("bind.session_failed");
                    return;
            }
        }
        private static async Task BindLocally(Target target, string input, Accounts.AccInfo accInfo)
        {
            if (accInfo.platform != Platform.OneBot)
            {
                await target.Treply("bind.platform_unsupported");
                return;
            }

            if (!long.TryParse(accInfo.uid, out var qqId))
            {
                await target.Treply("bind.service_error");
                return;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                await target.Treply("bind.local_usage");
                return;
            }

            var username = input.Trim();

            API.OSU.Models.UserExtended? osuUser;
            try
            {
                osuUser = await API.OSU.Client.GetUser(username);
            }
            catch (Exception)
            {
                await target.Treply("bind.service_error");
                return;
            }

            if (osuUser == null)
            {
                await target.Treply("bind.user_not_found");
                return;
            }

            var user = await Database.Client.CreateOrUpdateQQUser(qqId);
            if (user == null)
            {
                await target.Treply("bind.service_error");
                return;
            }

            var existingBind = await Database.Client.GetOsuUserByUID(user.uid);
            if (existingBind != null)
            {
                if (existingBind.osu_uid == osuUser.Id)
                {
                    await target.Treply("bind.already_bound");
                }
                else
                {
                    await target.Treply("bind.already_bound_other");
                }
                return;
            }

            var success = await Database.Client.InsertOsuUser(user.uid, osuUser.Id);
            if (success)
            {
                await target.Treply("bind.success", osuUser.Username);
            }
            else
            {
                await target.Treply("bind.service_error");
            }
        }
    }
}

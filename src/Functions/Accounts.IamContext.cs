using KanonBot.Drivers;

namespace KanonBot.Functions
{
    public static partial class Accounts
    {
        /// <summary>
        /// Resolve a platform user to IAM UUID + osu UID + local osu settings.
        /// When IAM is disabled, resolves from local database.
        /// </summary>
        public static async Task<UserContext?> ResolveIamUser(Target target)
        {
            var accInfo = GetAccInfo(target);
            if (accInfo.platform == Platform.Unknown)
                return null;

            if (!Config.IamEnabled)
                return await ResolveLocalUser(accInfo);

            var provider = API.IAM.Client.PlatformToProvider(accInfo.platform);
            var iamUserId = await API.IAM.Client.GetIamUserIdByExternalId(provider, accInfo.uid);
            if (iamUserId == null)
                return null;

            var bindings = await API.IAM.Client.GetUserBindings(iamUserId);
            if (bindings == null)
                return new UserContext { IamUserId = iamUserId };

            var osuUid = API.IAM.Client.ExtractOsuUid(bindings);

            return new UserContext
            {
                IamUserId = iamUserId,
                OsuUid = osuUid,
                DisplayName = bindings.DisplayName ?? bindings.UserName,
            };
        }

        /// <summary>
        /// Resolve IAM user by IAM User ID directly (for @osu:xxx lookups).
        /// When IAM is disabled, resolves from local database.
        /// </summary>
        public static async Task<UserContext?> ResolveIamUserById(string iamUserId)
        {
            if (!Config.IamEnabled)
            {
                if (long.TryParse(iamUserId, out var qqId))
                    return await ResolveLocalUser(new AccInfo { platform = Platform.OneBot, uid = iamUserId });
                return null;
            }

            var bindings = await API.IAM.Client.GetUserBindings(iamUserId);
            if (bindings == null)
                return null;

            var osuUid = API.IAM.Client.ExtractOsuUid(bindings);

            return new UserContext
            {
                IamUserId = iamUserId,
                OsuUid = osuUid,
                DisplayName = bindings.DisplayName ?? bindings.UserName,
            };
        }

        private static async Task<UserContext?> ResolveLocalUser(AccInfo accInfo)
        {
            if (accInfo.platform != Platform.OneBot)
                return null;

            if (!long.TryParse(accInfo.uid, out var qqId))
                return null;

            var osuBind = await Database.Client.GetOsuUserByQQ(qqId);
            if (osuBind == null)
                return null;

            var user = await Database.Client.GetUsersByUID(accInfo.uid, Platform.OneBot);
            if (user == null)
                return null;

            return new UserContext
            {
                IamUserId = user.uid.ToString(),
                OsuUid = osuBind.osu_uid,
            };
        }

        internal static API.OSU.Mode? ParseModeFromOsuUser(Database.Model.UserOSU? osuUser)
        {
            if (osuUser == null) return null;
            return osuUser.osu_mode switch
            {
                "osu" => API.OSU.Mode.OSU,
                "taiko" => API.OSU.Mode.Taiko,
                "fruits" => API.OSU.Mode.Fruits,
                "mania" => API.OSU.Mode.Mania,
                _ => API.OSU.Mode.OSU,
            };
        }
    }
}

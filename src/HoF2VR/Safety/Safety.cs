using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Defiant.BugReporter;
using HarmonyLib;
using Steamworks;

namespace HoF2VR
{
    /// Always: bug reports never upload (they carry the save and logs; a modded game has no
    /// business sending those to the developers).
    /// Test mode: Steam Cloud file calls and the local save mirror go to TestMode.Dir, and
    /// achievements / stats / leaderboard uploads are swallowed, so automated runs can never
    /// touch the player's real save or Steam profile.
    /// The Scenarios' dev profile (DevProfile): the same guards; while it is in use saves go to its own folder (also in
    /// normal play, where the guards otherwise pass everything through).
    internal static class Safety
    {
        static readonly HashSet<string> s_logged = new HashSet<string>();

        public static void Apply(Harmony h)
        {
            try
            {
                S3BugReportService.SkipBugReportUpload = true;
                h.Patch(AccessTools.PropertyGetter(typeof(S3BugReportService), "SkipBugReportUpload"),
                    postfix: new HarmonyMethod(typeof(Safety), nameof(ForceTrue)));
            }
            catch (Exception e) { Plugin.L.LogWarning("bug report guard failed: " + e); }

            if (!TestMode.Enabled) return;
            Guard(h);
            Plugin.L.LogWarning("TEST MODE: saves -> " + TestMode.Dir + ", achievements/stats/leaderboards blocked");
        }

        /// Normal play with [Dev] Scenarios on: the same guards, passing everything through to Steam until the dev profile
        /// is in use (DevProfile.InUse); then saves go to its folder and achievements/stats are blocked.
        public static void ApplyForScenarios(Harmony h)
        {
            if (TestMode.Enabled || s_guarded.Count > 0) return;
            Guard(h);
            Plugin.L.LogInfo("Scenarios: save guards in place (pass-through until the dev profile is in use)");
        }

        /// Saves and achievements are redirected / blocked now.
        static bool Redirect => TestMode.Enabled || DevProfile.InUse;

        static void Guard(Harmony h)
        {
            Type rs = typeof(SteamRemoteStorage);
            Prefix(h, AccessTools.Method(rs, "FileWrite"), nameof(FileWrite));
            Prefix(h, AccessTools.Method(rs, "FileRead"), nameof(FileRead));
            Prefix(h, AccessTools.Method(rs, "FileExists"), nameof(FileExists));
            Prefix(h, AccessTools.Method(rs, "FilePersisted"), nameof(FileExists));
            Prefix(h, AccessTools.Method(rs, "GetFileSize"), nameof(GetFileSize));
            Prefix(h, AccessTools.Method(rs, "FileDelete"), nameof(FileDelete));
            Prefix(h, AccessTools.Method(rs, "FileForget"), nameof(FileDelete));
            Prefix(h, AccessTools.Method(rs, "FileWriteAsync"), nameof(BlockCall));
            Prefix(h, AccessTools.Method(rs, "FileShare"), nameof(BlockCall));
            Prefix(h, AccessTools.Method(rs, "SetSyncPlatforms"), nameof(BlockBool));
            Prefix(h, AccessTools.Method(rs, "FileWriteStreamOpen"), nameof(BlockStream));

            Type us = typeof(SteamUserStats);
            Prefix(h, AccessTools.Method(us, "SetAchievement"), nameof(BlockBool));
            Prefix(h, AccessTools.Method(us, "ClearAchievement"), nameof(BlockBool));
            Prefix(h, AccessTools.Method(us, "StoreStats"), nameof(BlockBool));
            Prefix(h, AccessTools.Method(us, "SetStat", new[] { typeof(string), typeof(int) }), nameof(BlockBool));
            Prefix(h, AccessTools.Method(us, "SetStat", new[] { typeof(string), typeof(float) }), nameof(BlockBool));
            Prefix(h, AccessTools.Method(us, "UpdateAvgRateStat"), nameof(BlockBool));
            Prefix(h, AccessTools.Method(us, "IndicateAchievementProgress"), nameof(BlockBool));
            Prefix(h, AccessTools.Method(us, "ResetAllStats"), nameof(BlockBool));
            Prefix(h, AccessTools.Method(us, "UploadLeaderboardScore"), nameof(BlockCall));

            h.Patch(AccessTools.Method(typeof(ProfileUtils), "GetPlayerProfilePath", new[] { typeof(string), typeof(Game.Platform) }),
                postfix: new HarmonyMethod(typeof(Safety), nameof(LocalProfilePath)));
            DevProfile.Apply(h);
        }

        static readonly List<MethodBase> s_guarded = new List<MethodBase>();
        /// The save and Steam guards are in place (test mode, or [Dev] Scenarios at the start of the game).
        public static bool Guarded => s_guarded.Count > 0;

        /// Test mode: throws unless every guarded Steam method really carries our prefix now.
        public static void Verify()
        {
            if (!TestMode.Enabled) return;
            if (s_guarded.Count < 20) throw new Exception("Safety: only " + s_guarded.Count + " methods guarded");
            foreach (MethodBase m in s_guarded)
            {
                Patches p = Harmony.GetPatchInfo(m);
                bool ours = false;
                if (p != null) foreach (Patch x in p.Prefixes) if (x.PatchMethod.DeclaringType == typeof(Safety)) ours = true;
                if (!ours) throw new Exception("Safety: guard missing on " + m.DeclaringType.Name + "." + m.Name);
            }
            Plugin.L.LogInfo("[test isolation] verified " + s_guarded.Count + " guarded Steam methods");
        }

        static void Prefix(Harmony h, MethodInfo m, string handler)
        {
            if (m == null) throw new Exception("Safety: method to guard not found for " + handler);
            h.Patch(m, prefix: new HarmonyMethod(typeof(Safety), handler));
            s_guarded.Add(m);
        }

        static void Once(string what)
        {
            if (s_logged.Add(what)) Plugin.L.LogInfo("[test isolation] " + what);
        }

        static string RemotePath(string name)
        {
            string dir = DevProfile.RemoteDir;
            if (DevProfile.Switching) { Directory.CreateDirectory(dir); DevProfile.SetAside++; }
            return Path.Combine(dir, Path.GetFileName(name));
        }

        static void ForceTrue(ref bool __result) => __result = true;

        static bool FileWrite(string pchFile, byte[] pvData, int cubData, ref bool __result)
        {
            if (!Redirect) return true;
            byte[] data = new byte[cubData];
            Array.Copy(pvData, data, cubData);
            File.WriteAllBytes(RemotePath(pchFile), data);
            Once("cloud write -> " + RemotePath(pchFile) + " (" + cubData + " bytes)");
            __result = true;
            return false;
        }

        static bool FileRead(string pchFile, byte[] pvData, int cubDataToRead, ref int __result)
        {
            if (!Redirect) return true;
            string p = RemotePath(pchFile);
            __result = 0;
            if (File.Exists(p))
            {
                byte[] data = File.ReadAllBytes(p);
                int n = Math.Min(data.Length, cubDataToRead);
                Array.Copy(data, pvData, n);
                __result = n;
            }
            Once("cloud read <- " + p + " (" + __result + " bytes)");
            return false;
        }

        static bool FileExists(string pchFile, ref bool __result)
        {
            if (!Redirect) return true;
            __result = File.Exists(RemotePath(pchFile));
            return false;
        }

        static bool GetFileSize(string pchFile, ref int __result)
        {
            if (!Redirect) return true;
            string p = RemotePath(pchFile);
            __result = File.Exists(p) ? (int)new FileInfo(p).Length : 0;
            return false;
        }

        static bool FileDelete(string pchFile, ref bool __result)
        {
            if (!Redirect) return true;
            string p = RemotePath(pchFile);
            if (File.Exists(p)) File.Delete(p);
            Once("cloud delete -> " + p);
            __result = true;
            return false;
        }

        static bool BlockBool(MethodBase __originalMethod, ref bool __result)
        {
            if (!Redirect) return true;
            Once("blocked " + __originalMethod.Name);
            __result = true;
            return false;
        }

        static bool BlockCall(MethodBase __originalMethod, ref SteamAPICall_t __result)
        {
            if (!Redirect) return true;
            Once("blocked " + __originalMethod.Name);
            __result = SteamAPICall_t.Invalid;
            return false;
        }

        static bool BlockStream(ref UGCFileWriteStreamHandle_t __result)
        {
            if (!Redirect) return true;
            Once("blocked FileWriteStreamOpen");
            __result = UGCFileWriteStreamHandle_t.Invalid;
            return false;
        }

        static void LocalProfilePath(Game.Platform a_platform, ref string __result)
        {
            if (!Redirect || a_platform == Game.Platform.Steam || a_platform == Game.Platform.GOG) return;
            string dir = DevProfile.LocalDir;
            if (DevProfile.InUse) Directory.CreateDirectory(dir);
            __result = Path.Combine(dir, Path.GetFileName(__result));
        }
    }
}

using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// The dev profile of the Scenarios menu (Dev.Scenarios; docs/scenarios.md): while it is in use, every save the game
    /// makes goes to its own folder - `<game>/hof2vr_dev/` in normal play, `<test dir>/dev/` in test runs - and the player's
    /// own save (the Steam Cloud file and its local mirror; in test runs the harness's isolated save) is never written.
    /// Achievements and stats are blocked meanwhile (Safety).
    ///
    /// Switching is the game's own sign-out (the pause menu's "Quit to title": Game.ClearScene, the Loader, GameState_Init
    /// loads the profile again; the mod survives it, Fixes/ResetGuard). The switch completes at that profile load
    /// (ProfileManager.LoadAsync): until then the game may still hold the other profile in memory, so whatever it saves in
    /// between goes to a set-aside folder (`switching/`) - neither the player's save nor the dev save.
    ///  * Real -> ToDev (Enter: the base profile is copied in at the load) -> Dev -> ToReal (Leave) -> Real.
    /// The dev save starts from a base profile shipped in the DLL (a run on the dungeon map, or the challenge map) each time
    /// a scenario asks for a fresh one.
    internal static class DevProfile
    {
        public enum M { Real, ToDev, Dev, ToReal }
        public static M Mode = M.Real;
        /// The base the next switch to the dev profile loads ("mid" = a run on the dungeon map, "hub" = the challenge map).
        public static string NextBase = "mid", Base = "-";
        public static int Loads, SetAside;
        public const string FileName = "player_data.profile";

        /// Saves go to the dev folder.
        public static bool Active => Mode == M.Dev;
        /// Saves go to the set-aside folder (a switch is under way).
        public static bool Switching => Mode == M.ToDev || Mode == M.ToReal;
        /// Not the player's own profile: saves redirected, achievements and stats blocked.
        public static bool InUse => Mode != M.Real;

        public static string Dir => TestMode.Enabled ? Path.Combine(TestMode.Dir, "dev") : Path.Combine(TestMode.GameDir ?? ".", "hof2vr_dev");
        public static string Remote => Path.Combine(Dir, "remote");
        public static string Local => Path.Combine(Dir, "local");
        public static string Aside => Path.Combine(Dir, "switching");

        /// Where a cloud file goes now (only asked while Safety redirects).
        public static string RemoteDir => Active ? Remote : Switching ? Aside : TestMode.Remote;
        public static string LocalDir => Active ? Local : Switching ? Aside : TestMode.Local;

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(ProfileManager), "LoadAsync"), prefix: new HarmonyMethod(typeof(DevProfile), nameof(LoadAsyncPrefix)));
        }

        public static string Describe() => Mode + ", base " + Base + " (next " + NextBase + "), loads " + Loads + ", set aside " + SetAside + ", dir " + Dir;

        /// The profile is about to be loaded: the switch completes here.
        static void LoadAsyncPrefix()
        {
            try
            {
                if (Mode == M.ToDev)
                {
                    Fresh(NextBase);
                    Base = NextBase;
                    Mode = M.Dev;
                    Loads++;
                    Plugin.L.LogWarning("DEV PROFILE: loading the dev save (base '" + Base + "') from " + Remote + " - the player's own save is not written while it is in use");
                }
                else if (Mode == M.ToReal)
                {
                    Mode = M.Real;
                    Base = "-";
                    Plugin.L.LogWarning("DEV PROFILE: back to the player's own save" + (TestMode.Enabled ? " (the test run's: " + TestMode.Remote + ")" : ""));
                }
            }
            catch (Exception e)
            {
                // never fall back to the player's save with dev data in memory: stay set aside, the game loads an empty profile
                Plugin.L.LogError("DEV PROFILE: switch failed: " + e);
            }
        }

        /// The dev folder gets a fresh copy of a base profile (shipped in the DLL as HoF2VR.dev_<base>.profile).
        static void Fresh(string baseName)
        {
            Directory.CreateDirectory(Remote);
            Directory.CreateDirectory(Local);
            Directory.CreateDirectory(Aside);
            byte[] data;
            using (Stream s = typeof(DevProfile).Assembly.GetManifestResourceStream("HoF2VR.dev_" + baseName + ".profile"))
            {
                if (s == null) throw new Exception("no base profile '" + baseName + "' in the mod");
                data = new byte[s.Length];
                int n = 0;
                while (n < data.Length) { int r = s.Read(data, n, data.Length - n); if (r <= 0) break; n += r; }
            }
            File.WriteAllBytes(Path.Combine(Remote, FileName), data);
            string local = Path.Combine(Local, FileName);
            if (File.Exists(local)) File.Delete(local);
        }
    }
}

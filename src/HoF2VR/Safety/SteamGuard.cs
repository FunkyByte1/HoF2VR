using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Defiant.Platform;
using Defiant.Platform.Models;
using Defiant.Settings.Models;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace HoF2VR
{
    /// Test runs never touch the user's Steam client (the user, 2026-10-18: "Is there a way we can make it so your playtesting
    /// doesn't count towards my steam playtime? ... over 120 hours of this game on steam instead of a handful"). Every test instance
    /// called SteamAPI.Init (run.sh gave it SteamAppId / SteamGameId), so Steam counted it as the user playing.
    /// In test mode (HOF2VR_TEST=1, the harness's launches) only:
    ///  * SteamManager.Start is skipped: no RestartAppIfNecessary, no SteamAPI.Init (and no Application.Quit for its failure), so
    ///    the process never registers with Steam - no playtime, no "in game" status; SteamManager.Initialised stays false, so the
    ///    game's own Steam code (achievements, leaderboards, the overlay, Steam's DLC check) keeps out of Steam by itself.
    ///  * SteamManager.Update (RunCallbacks) and OnDestroy (Shutdown) are skipped; SteamPlatform.Start's wait for Steam is replaced
    ///    (no workshop mods are installed, no callbacks registered); AchievementManager_Steam.Start runs without its Steam part.
    ///  * Every other method of Steamworks.NET's Steam* classes answers locally with its default and never calls the native
    ///    library (the cloud file calls keep Safety's redirect to the test folder); each one reached is logged once
    ///    ("[steam] reached ...").
    ///  * The DLC: tests see what the user's game saw. The user's own launches (Steam running) record every DLC answer in
    ///    BepInEx/hof2vr_dlc.txt (in the game folder, the mod's own file); tests answer from it, and without it "owned" - the user's
    ///    game showed every add-on set available (their session of 2026-10-18).
    ///  * The language is read (read-only) from Steam's appmanifest_456670.acf ("language"), default english.
    /// The user's own launches are unchanged (only the DLC recorder runs there).
    internal static class SteamGuard
    {
        public static int Reached, Patched;
        static readonly HashSet<string> s_logged = new HashSet<string>();
        static readonly Dictionary<int, bool> s_dlc = new Dictionary<int, bool>();
        static bool s_dlcLoaded;
        static string DlcFile => Path.Combine(BepInEx.Paths.BepInExRootPath, "hof2vr_dlc.txt");

        public static void Apply(Harmony h)
        {
            // always: the DLC answers of a real Steam session are recorded (the user's launches); in tests they are answered here
            try
            {
                h.Patch(AccessTools.Method(typeof(SteamPlatform), "IsAdditionalContentAvailable"),
                    prefix: new HarmonyMethod(typeof(SteamGuard), nameof(DlcPrefix)), postfix: new HarmonyMethod(typeof(SteamGuard), nameof(DlcPostfix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("steam guard: DLC patch: " + e.Message); }
            if (!TestMode.Enabled) return;

            Skip(h, AccessTools.Method(typeof(SteamManager), "Start"), nameof(SkipStart));
            Skip(h, AccessTools.Method(typeof(SteamManager), "Update"), nameof(SkipQuiet));
            Skip(h, AccessTools.Method(typeof(SteamManager), "OnDestroy"), nameof(SkipDestroy));
            Skip(h, AccessTools.Method(typeof(SteamPlatform), "Start"), nameof(PlatformStart));
            Skip(h, AccessTools.Method(typeof(SteamPlatform), "GetTextLanguage"), nameof(TextLanguage));
            Skip(h, AccessTools.Method(typeof(AchievementManager_Steam), "Start"), nameof(AchievementsStart));
            // (its call results were never made: the workshop query and the leaderboards answer here - the first test run without
            // Steam threw in RequestTaggedMods at the mods dialog)
            Skip(h, AccessTools.Method(typeof(SteamPlatform), "RequestTaggedMods"), nameof(NoWorkshop));
            Skip(h, AccessTools.Method(typeof(SteamPlatform), "SetScore"), nameof(NoScore));

            // every other Steam call: answered locally (its default), logged once
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            foreach (Type t in typeof(SteamAPI).Assembly.GetTypes())
            {
                if (t == null || t.Namespace != "Steamworks" || !t.IsAbstract || !t.IsSealed || !t.Name.StartsWith("Steam", StringComparison.Ordinal)) continue;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.IsGenericMethodDefinition || m.ContainsGenericParameters) continue;
                    if (Ours(m)) continue;   // Safety's redirect of the cloud files stays in charge there
                    try { h.Patch(m, prefix: new HarmonyMethod(typeof(SteamGuard), nameof(Block))); Patched++; }
                    catch (Exception e) { Plugin.L.LogWarning("steam guard: " + t.Name + "." + m.Name + ": " + e.Message); }
                }
            }
            Plugin.L.LogWarning("TEST MODE: the Steam client is never contacted (no SteamAPI.Init: no playtime, no 'in game'); " + Patched +
                                " Steamworks methods answer locally (" + stopwatch.ElapsedMilliseconds + " ms)");
        }

        static bool Ours(MethodBase m)
        {
            Patches p = Harmony.GetPatchInfo(m);
            if (p == null) return false;
            foreach (Patch x in p.Prefixes) if (x.PatchMethod.DeclaringType == typeof(Safety)) return true;
            return false;
        }

        static void Skip(Harmony h, MethodInfo m, string handler)
        {
            if (m == null) throw new Exception("steam guard: a method to skip was not found for " + handler);
            h.Patch(m, prefix: new HarmonyMethod(typeof(SteamGuard), handler));
        }

        static void Once(string what)
        {
            if (s_logged.Add(what)) Plugin.L.LogInfo("[steam] " + what);
        }

        static bool Block(MethodBase __originalMethod)
        {
            Reached++;
            Once("reached " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " - answered locally, nothing sent to Steam");
            return false;
        }

        static bool SkipStart()
        {
            Once("SteamManager.Start skipped: no SteamAPI.Init, so Steam never sees this test run");
            return false;
        }

        static bool SkipQuiet() => false;

        static bool SkipDestroy(SteamManager __instance)
        {
            if (SteamManager.m_instance == __instance) SteamManager.m_instance = null;
            return false;
        }

        static bool PlatformStart(ref IEnumerator __result)
        {
            __result = NoMods();
            return false;
        }

        static IEnumerator NoMods()
        {
            ModManager.WaitingForModsToInstall = false;
            Once("SteamPlatform.Start: no workshop mods, no Steam callbacks (test mode)");
            yield break;
        }

        static readonly MethodInfo s_achBaseStart = AccessTools.Method(typeof(AchievementManager), "Start");

        static bool AchievementsStart(AchievementManager_Steam __instance, ref IEnumerator __result)
        {
            // the base's own start (the achievements' loader), without the Steam part (its app id, its three callbacks)
            __result = s_achBaseStart != null ? (IEnumerator)s_achBaseStart.Invoke(__instance, null) : Nothing();
            return false;
        }

        static IEnumerator Nothing() { yield break; }

        static bool NoWorkshop(ref UGCQueryHandle_t __result)
        {
            __result = UGCQueryHandle_t.Invalid;
            Once("SteamPlatform.RequestTaggedMods: no workshop query (test mode)");
            return false;
        }

        static bool NoScore()
        {
            Once("SteamPlatform.SetScore: no leaderboard upload (test mode)");
            return false;
        }

        static bool TextLanguage(ref TextLanguage __result)
        {
            string lang = ManifestLanguage();
            __result = SteamPlatformLanguage.GetTextLanguage(lang);
            Once("the game's language from Steam's appmanifest (read only): " + lang);
            return false;
        }

        /// Steam's appmanifest of the game (read only): its "language", else english.
        static string ManifestLanguage()
        {
            try
            {
                var paths = new List<string>();
                string game = Path.GetDirectoryName(Path.GetDirectoryName(Application.dataPath.TrimEnd('/', '\\')) ?? "");
                if (!string.IsNullOrEmpty(game)) paths.Add(Path.Combine(Path.GetDirectoryName(game) ?? "", "appmanifest_456670.acf"));
                string home = Environment.GetEnvironmentVariable("HOME");
                if (!string.IsNullOrEmpty(home)) paths.Add(Path.Combine(home, ".local/share/Steam/steamapps/appmanifest_456670.acf"));
                foreach (string p in paths)
                {
                    if (!File.Exists(p)) continue;
                    foreach (string line in File.ReadAllLines(p))
                    {
                        string l = line.Trim();
                        if (!l.StartsWith("\"language\"", StringComparison.Ordinal)) continue;
                        string[] q = l.Split('"');
                        if (q.Length >= 4 && q[3].Length > 0) return q[3];
                    }
                }
            }
            catch (Exception) { }
            return "english";
        }

        // ---- the DLC ------------------------------------------------------------------------------------------------------
        static void LoadDlc()
        {
            if (s_dlcLoaded) return;
            s_dlcLoaded = true;
            try
            {
                if (!File.Exists(DlcFile)) return;
                foreach (string line in File.ReadAllLines(DlcFile))
                {
                    string[] kv = line.Split('=');
                    int id;
                    if (kv.Length == 2 && int.TryParse(kv[0].Trim(), out id)) s_dlc[id] = kv[1].Trim() == "1";
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("steam guard: " + DlcFile + ": " + e.Message); }
        }

        static bool DlcPrefix(PlatformAdditionalContent a_additionalContent, ref bool __result)
        {
            if (!TestMode.Enabled || a_additionalContent == null) return true;
            LoadDlc();
            bool known = s_dlc.TryGetValue(a_additionalContent.SteamDLCAppID, out __result);
            if (!known) __result = true;
            Once("DLC " + a_additionalContent.SteamDLCAppID + ": " + (__result ? "owned" : "not owned") + (known ? " (recorded in the user's own launch)" : " (no record yet: owned, as the user's game showed every add-on set)"));
            return false;
        }

        static void DlcPostfix(PlatformAdditionalContent a_additionalContent, bool __result)
        {
            if (TestMode.Enabled || a_additionalContent == null || !SteamManager.Initialised) return;
            try
            {
                LoadDlc();
                bool was;
                if (s_dlc.TryGetValue(a_additionalContent.SteamDLCAppID, out was) && was == __result) return;
                s_dlc[a_additionalContent.SteamDLCAppID] = __result;
                var lines = new List<string>();
                foreach (var kv in s_dlc) lines.Add(kv.Key + "=" + (kv.Value ? "1" : "0"));
                File.WriteAllLines(DlcFile, lines.ToArray());
            }
            catch (Exception e) { Plugin.L.LogWarning("steam guard: recording the DLC: " + e.Message); }
        }
    }
}

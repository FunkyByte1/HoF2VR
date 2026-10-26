using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Where the time of a load goes (2026-10-11 loading speed-ups): every step of chosen game coroutines (a MoveNext
    /// that left a different yield point) and calls of chosen methods, with the time since the last state change, into
    /// the log as "tl <t> +<dt> f<frame> <what>".
    ///   timeline on [boot|fight|all]   patch the sets (once) and log;  timeline off
    /// Patches are applied only on request (test sessions); the prefixes return at once while off.
    internal static class Timeline
    {
        static Harmony s_h;
        static bool s_on;
        static readonly HashSet<string> s_sets = new HashSet<string>();
        static readonly Dictionary<MethodBase, string> s_names = new Dictionary<MethodBase, string>();
        static readonly Dictionary<Type, FieldInfo> s_stateField = new Dictionary<Type, FieldInfo>();
        static readonly Dictionary<object, int> s_last = new Dictionary<object, int>();
        static float s_t0 = -1f, s_prev;
        public static int Lines;

        public static void Register(Harmony h)
        {
            s_h = h;
            // HOF2VR_TIMELINE=boot|fight|all: on from the first frame (the boot can't wait for a command)
            string env = Environment.GetEnvironmentVariable("HOF2VR_TIMELINE");
            if (!string.IsNullOrEmpty(env) && TestMode.Enabled)
            {
                if (env == "boot" || env == "all") Patch("boot");
                if (env == "fight" || env == "all") Patch("fight");
                s_on = true;
            }
            Diag.Commands["timeline"] = a =>
            {
                if (a.Length > 1 && a[1] == "off") { s_on = false; return "timeline off (" + Lines + " lines)"; }
                string set = a.Length > 2 ? a[2] : "all";
                if (set == "boot" || set == "all") Patch("boot");
                if (set == "fight" || set == "all") Patch("fight");
                s_on = true;
                s_t0 = -1f;
                s_last.Clear();
                return "timeline on (" + string.Join(",", new List<string>(s_sets).ToArray()) + ", " + s_names.Count + " patches)";
            };
        }

        static void Patch(string set)
        {
            if (!s_sets.Add(set)) return;
            if (set == "boot")
            {
                Co("Loader", "Start");
                Call("Loader", "NextSplash");
                Call("Loader", "OnLoadingTransitionComplete");
                Co("GameState_Init", "Startup");
                Co("GameState_Init", "UnloadLoaderScene");
                Co("GameState_StartScreen", "StartUp");
                Co("GameState_StartScreen", "Continue");
                Co("StartScreenController", "LoadCharacter");
                Call("StartScreenController", "Display");
                Call("StartScreenController", "OnProfileReady");
                Call("StartScreenController", "SetSequence");
                Call("StartScreenController", "DisplayMenuUI");
                Call("StartScreenController", "TransitionLogo");
                Co("GameState_Profile", "Startup");
                Call("AssetManager", "LoadModAssets");
            }
            else if (set == "fight")
            {
                Co("GameState_Level_In", "Startup");
                Co("GameState_Level_In", "LoadLevel");
                Co("GameState_Level_In", "AnimateCardsIntoWormhole");
                Co("GameState_Level_In", "TriggerWormhole");
                Co("GameState_Level_In", "OnInitLevelComplete");
                Co("GameState_Level_In", "LevelTransition");
                Co("GameState_Level_In", "LevelTransition_Player");
                Co("GameState_Level_In", "SetUpLevelEnvironmentState");
                Co("MeshWormhole", "TransitionUp");
                Co("MeshWormhole", "StartTransitionIn");
                Co("MeshWormhole", "StartTransitionOut");
                Co("MeshWormhole", "DisplayFeatureCard");
                Co("MeshWormhole", "DisplayPostWormholeMesh");
                Co("LevelPool", "CacheAndBuild");
                Co("UI3D", "ResetCameraFade");
                Call("CombatTask", "InitLevel");
                Call("GameState_Level_In", "DoInitLevelComplete");
                Call("GameState_Level_In", "CreateSpawners");
                Call("GameState_Level_In", "CompleteStartup");
                Call("CameraFade", "FadeToColor");
                Call("CameraFade", "FadeOut");
            }
        }

        static void Co(string type, string method)
        {
            try
            {
                MethodInfo m = AccessTools.Method(AccessTools.TypeByName(type), method);
                if (m == null) { Plugin.L.LogWarning("timeline: no " + type + "." + method); return; }
                MethodInfo mn = AccessTools.EnumeratorMoveNext(m);
                if (mn == null) { Plugin.L.LogWarning("timeline: " + type + "." + method + " is no coroutine"); return; }
                s_names[mn] = type + "." + method;
                s_h.Patch(mn, prefix: new HarmonyMethod(typeof(Timeline), nameof(StepPrefix)), postfix: new HarmonyMethod(typeof(Timeline), nameof(StepPostfix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("timeline: " + type + "." + method + ": " + e.Message); }
        }

        static void Call(string type, string method)
        {
            try
            {
                Type t = AccessTools.TypeByName(type);
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t))
                {
                    if (m.Name != method || m.IsAbstract || m.IsGenericMethod) continue;
                    s_names[m] = type + "." + method;
                    s_h.Patch(m, prefix: new HarmonyMethod(typeof(Timeline), nameof(CallPrefix)));
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("timeline: " + type + "." + method + ": " + e.Message); }
        }

        static int State(object it)
        {
            if (it == null) return -99;
            Type t = it.GetType();
            FieldInfo f;
            if (!s_stateField.TryGetValue(t, out f))
            {
                f = AccessTools.Field(t, "<>1__state") ?? AccessTools.Field(t, "$PC");
                s_stateField[t] = f;
            }
            return f != null ? (int)f.GetValue(it) : -98;
        }

        static void Log(string what)
        {
            float now = Time.realtimeSinceStartup;
            if (s_t0 < 0f) { s_t0 = now; s_prev = now; }
            Lines++;
            Plugin.L.LogInfo("tl " + (now - s_t0).ToString("0.000") + " +" + (now - s_prev).ToString("0.000") + " f" + Time.frameCount + " " + what);
            s_prev = now;
        }

        static void StepPrefix(object __instance, MethodBase __originalMethod, out int __state)
        {
            __state = s_on ? State(__instance) : 0;
        }

        static void StepPostfix(object __instance, MethodBase __originalMethod, int __state, bool __result)
        {
            if (!s_on) return;
            int after = State(__instance);
            int last;
            if (s_last.TryGetValue(__instance, out last) && last == after && __result) return;
            s_last[__instance] = after;
            string name;
            s_names.TryGetValue(__originalMethod, out name);
            Log(name + " " + __state + "->" + after + (__result ? "" : " END"));
            if (!__result) s_last.Remove(__instance);
        }

        static void CallPrefix(MethodBase __originalMethod, object[] __args)
        {
            if (!s_on) return;
            string name;
            s_names.TryGetValue(__originalMethod, out name);
            string args = "";
            if (__args != null && __args.Length > 0)
            {
                var parts = new List<string>();
                foreach (object o in __args) parts.Add(o == null ? "null" : o is float ? ((float)o).ToString("0.###") : o.ToString());
                args = "(" + string.Join(", ", parts.ToArray()) + ")";
            }
            Log(name + args);
        }
    }
}

namespace HoF2VR
{
    /// HOF2VR_AUTOSHOTS="tag:t1,t2,..." (test mode): `shot <tag>_<t>` at those seconds after the start (the boot's
    /// waiting space can't be captured by commands without racing the session script's polling).
    internal class AutoShots : UnityEngine.MonoBehaviour
    {
        string m_tag;
        readonly System.Collections.Generic.List<float> m_at = new System.Collections.Generic.List<float>();

        void Awake()
        {
            string env = System.Environment.GetEnvironmentVariable("HOF2VR_AUTOSHOTS");
            if (string.IsNullOrEmpty(env) || !TestMode.Enabled) { enabled = false; return; }
            int colon = env.IndexOf(':');
            m_tag = colon > 0 ? env.Substring(0, colon) : "auto";
            foreach (string p in env.Substring(colon + 1).Split(','))
            {
                float t;
                if (float.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t)) m_at.Add(t);
            }
            m_at.Sort();
        }

        void Update()
        {
            if (m_at.Count == 0) { enabled = false; return; }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < m_at[0]) return;
            string name = m_tag + "_" + m_at[0].ToString("0.#", System.Globalization.CultureInfo.InvariantCulture).Replace('.', 'p');
            m_at.RemoveAt(0);
            System.Func<string[], string> shot;
            if (Diag.Commands.TryGetValue("shot", out shot))
                Plugin.L.LogInfo("autoshot " + name + " (" + GameWatch.State + "): " + shot(new[] { "shot", name }));
        }
    }
}

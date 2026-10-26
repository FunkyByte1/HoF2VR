using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Defiant.GC;
using Defiant.Quality;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Smoother loading waits (docs/smoothness-2026-10-11.md fixes M2, M4, M5). VR only; each part can be
    /// switched off for A/B tests with `smooth <part> 0|1` (prio, cams, veil, pools, preload).
    ///
    ///  (a) Loading priority: the game loads at ThreadPriority.High all the time (Loader.cs:59), which lets async
    ///      loads take up to ~50 ms of a frame. In a loading hold (the waiting space, the boot) BelowNormal
    ///      (~4 ms a frame), everywhere else Normal (~10 ms); re-asserted every frame.
    ///  (b) While the waiting space covers the view (its camera clears the whole eye), the game's eye cameras
    ///      draw nothing: their culling mask is set to 0 in their own pre-cull (a sentinel: if the game sets
    ///      another mask meanwhile, that one is kept) and their AO / anti-aliasing / bloom are off. Restored for
    ///      6 warm-up frames after every scene load (first-use shader and texture costs paid behind the space)
    ///      and as soon as the place hold is released (the mist covers the space for another 0.25 s: the new
    ///      place renders behind it before it is revealed). Camera.enabled is never touched: the game switches
    ///      its table / combat / player cameras on and off during exactly these waits.
    ///  (c) The biggest one-frame steps: on the way back from a fight the game unloads unused assets (one
    ///      frame of 230-410 ms) and then collects garbage (MeshWormhole.StartTransitionOut). In VR the
    ///      waiting space first thickens into plain mist (a frozen frame of plain mist cannot judder), the
    ///      unload runs, the collection gets a frame of its own, then the space clears again. Object pools of
    ///      the last scene (35-105) are destroyed a few per frame instead of all in the activation frame. (Spreading
    ///      the card-template preload is available but off: it lengthened the title -> table wait by 1.3 s.)
    internal class LoadSmooth : MonoBehaviour
    {
        public static LoadSmooth I;
        /// HOF2VR_SMOOTH=0 starts with every part off (before/after measurements from the first frame).
        static readonly bool s_default = Environment.GetEnvironmentVariable("HOF2VR_SMOOTH") != "0";
        public static bool PrioOn = s_default, CamsOn = s_default, VeilOn = s_default, PoolsOn = s_default;
        /// The showcase card's animation in the tunnel is never seen in VR; skipping its wait was meant to shorten fight
        /// entries with a showcase by ~4 s. OFF: measured 2026-10-11, the extra ~4 s of those entries lie BEFORE the
        /// wormhole's StartTransitionOut (tunnel phase 6.6 s either way) and the card path was never reached. Kept for
        /// investigation (`smooth showcase 1`).
        public static bool SkipShowcaseCard = false;
        /// Off: spreading the card-template preload (4 a frame) made the title -> table wait 1.3 s longer for one
        /// 40-50 ms frame less (A/B 2026-10-11); kept for tests (`smooth preload 1 [n]`).
        public static bool PreloadOn = false;
        const ThreadPriority HoldPriority = ThreadPriority.BelowNormal, PlayPriority = ThreadPriority.Normal;
        const int WarmFrames = 6, PoolsPerFrame = 4;
        /// The game preloads each card template's instances in one frame (its step size is 0 = no yield); one per
        /// frame stretched the title -> table wait by 4 s (2026-10-11), so a few per frame.
        public static int PreloadPerFrame = 4;

        public static void Apply(Harmony h)
        {
            try
            {
                MethodInfo moveNext = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(MeshWormhole), "StartTransitionOut"));
                h.Patch(moveNext, transpiler: new HarmonyMethod(typeof(LoadSmooth), nameof(VeilTranspiler)));
            }
            catch (Exception e) { Plugin.L.LogWarning("LoadSmooth: wormhole unload patch failed: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(PoolManager), "OnSceneLoaded"), prefix: new HarmonyMethod(typeof(LoadSmooth), nameof(PoolsPrefix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("LoadSmooth: pool patch failed: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(CardManagerTemplates), "PreloadCardTemplates"), prefix: new HarmonyMethod(typeof(LoadSmooth), nameof(PreloadPrefix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("LoadSmooth: preload patch failed: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(MeshWormhole), "DisplayFeatureCard"), prefix: new HarmonyMethod(typeof(LoadSmooth), nameof(FeatureCardPrefix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("LoadSmooth: showcase patch failed: " + e.Message); }
        }

        void Awake()
        {
            I = this;
            Application.onBeforeRender += BeforeRender;
            Camera.onPreCull += PreCull;
            SceneManager.sceneLoaded += (s, m) => { m_warmUntil = Time.frameCount + WarmFrames; };
            Diag.StatusExtras["smooth"] = () => Diag.Q("prio " + Application.backgroundLoadingPriority + (m_active ? " cams off " + m_supp.Count : "") +
                                                      " veils " + Veils + " pools " + PoolsSpread + "/" + m_destroy.Count);
            Diag.Commands["smooth"] = a =>
            {
                if (a.Length > 2)
                {
                    bool on = a[2] == "1";
                    switch (a[1])
                    {
                        case "prio": PrioOn = on; break;
                        case "cams": CamsOn = on; break;
                        case "veil": VeilOn = on; break;
                        case "pools": PoolsOn = on; break;
                        case "preload": PreloadOn = on; if (on && a.Length > 3) PreloadPerFrame = int.Parse(a[3]); break;
                        case "showcase": SkipShowcaseCard = on; break;
                        case "all": PrioOn = CamsOn = VeilOn = PoolsOn = on; break;
                        default: return "unknown part " + a[1];
                    }
                }
                return "smooth prio " + PrioOn + " cams " + CamsOn + " veil " + VeilOn + " pools " + PoolsOn + " preload " + PreloadOn +
                       " | priority now " + Application.backgroundLoadingPriority + ", cameras emptied " + (m_active ? m_supp.Count.ToString() : "none") +
                       ", veils " + Veils + ", pools spread " + PoolsSpread;
            };
        }

        void OnDestroy()
        {
            Application.onBeforeRender -= BeforeRender;
            Camera.onPreCull -= PreCull;
        }

        /// A loading hold is on: the waiting space, a place change, the boot.
        static bool InHold()
        {
            if (VRFade.I != null && VRFade.I.IsHeldBy(Places.HoldOwner)) return true;
            return Limbo.On;
        }

        void LateUpdate()
        {
            if (!XRSettings.enabled) return;
            // (a)
            ThreadPriority want = !PrioOn ? ThreadPriority.High : InHold() ? HoldPriority : PlayPriority;
            if (Application.backgroundLoadingPriority != want)
            {
                ThreadPriority was = Application.backgroundLoadingPriority;
                Application.backgroundLoadingPriority = want;
                if (want != m_loggedPrio)
                {
                    m_loggedPrio = want;
                    Plugin.L.LogInfo("smooth: loading priority " + was + " -> " + want + " at " + Diag.Clock() + " (" + GameWatch.State + ")");
                }
            }
            // (c) pools of the last scene, a few per frame
            for (int i = 0; i < PoolsPerFrame && m_destroy.Count > 0; i++)
            {
                GameObject go = m_destroy.Dequeue();
                if (go != null) Destroy(go);
            }
        }

        ThreadPriority m_loggedPrio = (ThreadPriority)(-1);

        // ---- (b) the game's cameras behind the waiting space -------------------------------------------

        sealed class Supp
        {
            public Camera Cam;
            public int Mask;
            public readonly List<Behaviour> Fx = new List<Behaviour>(3);
            public readonly List<bool> FxWas = new List<bool>(3);
        }

        readonly Dictionary<int, Supp> m_supp = new Dictionary<int, Supp>();
        bool m_active;
        int m_warmUntil;
        public static int Suppressions;   // diagnostics

        /// Once per frame before any camera culls: should the game's cameras draw nothing this frame?
        void BeforeRender()
        {
            if (!XRSettings.enabled) return;
            bool want = CamsOn && Limbo.On && Limbo.I != null && Limbo.I.Cam != null && Limbo.I.Cam.enabled &&
                        (VRFade.I != null && VRFade.I.IsHeldBy(Places.HoldOwner) || Limbo.BootGrace || Limbo.TestWanted) &&
                        Time.frameCount > m_warmUntil;
            if (want == m_active) return;
            m_active = want;
            if (want)
            {
                Suppressions++;
                PerfMonitor.Event("cams-off");
            }
            else
            {
                Restore();
                PerfMonitor.Event("cams-on");
            }
        }

        static readonly Dictionary<int, bool> s_gameEye = new Dictionary<int, bool>();

        /// A camera of the game's that draws into the eyes (not ours, not a render-texture or tunnel camera).
        static bool GameEye(Camera cam)
        {
            if (cam.stereoTargetEye == StereoTargetEyeMask.None || cam.targetTexture != null || cam.orthographic) return false;
            int id = cam.GetInstanceID();
            bool v;
            if (s_gameEye.TryGetValue(id, out v)) return v;
            string n = cam.name;   // once per camera (names are allocated on every read)
            v = !n.StartsWith("HoF2VR_") && n != "WormholeCamera" && cam.GetComponent<VRUI.OverlayMarker>() == null && (Limbo.I == null || cam != Limbo.I.Cam);
            s_gameEye[id] = v;
            return v;
        }

        void PreCull(Camera cam)
        {
            if (!m_active || !GameEye(cam)) return;
            int id = cam.GetInstanceID();
            Supp s;
            if (!m_supp.TryGetValue(id, out s))
            {
                s = new Supp { Cam = cam, Mask = cam.cullingMask };
                CameraQualityManager q = cam.GetComponent<CameraQualityManager>();
                if (q != null)
                {
                    AddFx(s, q.HBAO);
                    AddFx(s, q.AA);
                    AddFx(s, q.Bloom);
                }
                m_supp[id] = s;
            }
            else if (cam.cullingMask != 0) s.Mask = cam.cullingMask;   // the game set a new mask meanwhile: that one counts
            cam.cullingMask = 0;
            for (int i = 0; i < s.Fx.Count; i++)
            {
                Behaviour b = s.Fx[i];
                if (b == null) continue;
                if (b.enabled) { s.FxWas[i] = true; b.enabled = false; }   // (re-enabled by the game meanwhile: it wants it on)
            }
        }

        static void AddFx(Supp s, Behaviour b)
        {
            if (b == null) return;
            s.Fx.Add(b);
            s.FxWas.Add(b.enabled);
        }

        void Restore()
        {
            int strip = (1 << VRUI.Layer) | (1 << Limbo.Layer);   // never the mod's own layers (VRUI strips them too)
            foreach (Supp s in m_supp.Values)
            {
                if (s.Cam == null) continue;   // destroyed with its scene
                if (s.Cam.cullingMask == 0) s.Cam.cullingMask = s.Mask & ~strip;
                for (int i = 0; i < s.Fx.Count; i++)
                    if (s.Fx[i] != null && s.FxWas[i] && !s.Fx[i].enabled) s.Fx[i].enabled = true;
            }
            m_supp.Clear();
        }

        // ---- (c) the asset unload on the way back from a fight, in plain mist --------------------------

        public static int Veils;
        static bool s_veilUntilGc;

        static IEnumerable<CodeInstruction> VeilTranspiler(IEnumerable<CodeInstruction> code)
        {
            MethodInfo unload = AccessTools.Method(typeof(Resources), "UnloadUnusedAssets");
            MethodInfo gc = AccessTools.Method(typeof(GarbageCollectionManager), "InvokeGarbageCollector");
            int n = 0;
            foreach (CodeInstruction c in code)
            {
                if (c.opcode == OpCodes.Call && c.operand as MethodInfo == unload) { c.operand = AccessTools.Method(typeof(LoadSmooth), nameof(VeiledUnload)); n++; }
                else if (c.opcode == OpCodes.Call && c.operand as MethodInfo == gc) { c.operand = AccessTools.Method(typeof(LoadSmooth), nameof(GcAfterUnload)); n++; }
                yield return c;
            }
            if (n != 2) Plugin.L.LogWarning("LoadSmooth: wormhole unload transpiler replaced " + n + " of 2 calls");
        }

        /// Replaces `yield return Resources.UnloadUnusedAssets()` in the wormhole's arrival: the same unload,
        /// with the waiting space in plain mist while the frame freezes.
        public static object VeiledUnload()
        {
            if (!VeilOn || !XRSettings.enabled || !Limbo.On || I == null) return Resources.UnloadUnusedAssets();
            return I.Veiled();
        }

        IEnumerator Veiled()
        {
            Veils++;
            Limbo.Veil = true;
            float t0 = Time.unscaledTime;
            while (Limbo.On && Limbo.Cover < 1f && Time.unscaledTime - t0 < 0.8f) yield return null;
            yield return null;   // one full frame of mist before the freeze
            PerfMonitor.Event("unload-unused");
            Plugin.L.LogInfo("smooth: unloading unused assets in the mist (veil " + (Time.unscaledTime - t0).ToString("0.00") + " s)");
            yield return Resources.UnloadUnusedAssets();
            yield return null;   // the game's garbage collection that follows gets a frame of its own
            s_veilUntilGc = true;
            StartCoroutine(VeilTimeout());
        }

        /// Replaces GarbageCollectionManager.InvokeGarbageCollector() there (the same GC.Collect).
        public static void GcAfterUnload()
        {
            PerfMonitor.Event("gc-collect");
            GarbageCollectionManager.InvokeGarbageCollector();
            if (s_veilUntilGc && I != null)
            {
                s_veilUntilGc = false;
                I.StartCoroutine(I.Unveil(2));
            }
        }

        IEnumerator Unveil(int frames)
        {
            for (int i = 0; i < frames; i++) yield return null;
            Limbo.Veil = false;
        }

        IEnumerator VeilTimeout()
        {
            float t0 = Time.unscaledTime;
            while (Limbo.Veil && Time.unscaledTime - t0 < 3f) yield return null;
            if (Limbo.Veil) { Limbo.Veil = false; Plugin.L.LogWarning("smooth: veil released by timeout"); }
        }

        // ---- (c) object pools and card templates ------------------------------------------------------

        readonly Queue<GameObject> m_destroy = new Queue<GameObject>();
        public static int PoolsSpread;

        /// PoolManager.OnSceneLoaded: the last scene's pools leave the manager at once (as before) but are switched
        /// off and destroyed a few per frame.
        static bool PoolsPrefix(Dictionary<UnityEngine.Object, Pool> ___m_poolsByPrefab)
        {
            if (!PoolsOn || !XRSettings.enabled || I == null || ___m_poolsByPrefab == null) return true;
            var dead = new List<UnityEngine.Object>();
            foreach (KeyValuePair<UnityEngine.Object, Pool> kv in ___m_poolsByPrefab)
                if (kv.Value == null || !kv.Value.KeepAliveOnSceneChange) dead.Add(kv.Key);
            foreach (UnityEngine.Object key in dead)
            {
                Pool p = ___m_poolsByPrefab[key];
                ___m_poolsByPrefab.Remove(key);
                if (p == null) continue;
                p.gameObject.SetActive(false);
                I.m_destroy.Enqueue(p.gameObject);
                PoolsSpread++;
            }
            if (dead.Count > 0) PerfMonitor.Event("pools-" + dead.Count);
            return false;
        }

        static bool s_preloadLogged;

        public static int ShowcaseSkips;

        /// MeshWormhole.DisplayFeatureCard in VR: the card goes onto the tunnel's feature prop as the game does it, but
        /// its "FeatureCardIn" animation (~4 s, plus its sound) is not played or waited for.
        static bool FeatureCardPrefix(ref IEnumerator __result, Card ___mFeatureCard, AnimatedProp ___m_featuredCardProp)
        {
            if (!SkipShowcaseCard || !XRSettings.enabled) return true;
            if (___mFeatureCard != null && ___m_featuredCardProp != null)
            {
                ___mFeatureCard.transform.SetParent(___m_featuredCardProp.transform, false);
                ___mFeatureCard.transform.localPosition = Vector3.zero;
                ___mFeatureCard.transform.localRotation = Quaternion.identity;
                ShowcaseSkips++;
                Plugin.L.LogInfo("smooth: the tunnel's showcase card animation skipped (not visible in VR)");
            }
            __result = NoWait();
            return false;
        }

        static IEnumerator NoWait() { yield break; }

        static int s_preloadGame = -1;

        /// A few card-template instances per frame (the game's own step size is a serialized field; 0 = all at once).
        static void PreloadPrefix(ref int ___m_numPreloadPerFrame)
        {
            if (s_preloadGame < 0) s_preloadGame = ___m_numPreloadPerFrame;
            if (!PreloadOn || !XRSettings.enabled) { ___m_numPreloadPerFrame = s_preloadGame; return; }
            if (!s_preloadLogged) { s_preloadLogged = true; Plugin.L.LogInfo("smooth: card templates preload " + PreloadPerFrame + " per frame (game: " + s_preloadGame + ")"); }
            ___m_numPreloadPerFrame = PreloadPerFrame;
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// Comfort fade: a black quad right in front of the eyes, drawn by the overlay camera after
    /// everything else.
    ///  * FadeSnap(action): fade out, run the action (e.g. a blink to a new spot), fade in.
    ///  * Hold(owner) / Release(owner): go black and stay black (loading, the trip from the table
    ///    into a fight) until every owner has released.
    ///  * QuickBlink(action): a much shorter blackout for the player's own snap turns (SnapTurnBlink).
    internal class VRFade : MonoBehaviour
    {
        public static VRFade I;
        public const float OutTime = 0.12f, InTime = 0.18f, HoldOutTime = 0.25f, HoldInTime = 0.35f;
        public const float QuickOutTime = 0.05f, QuickInTime = 0.09f;
        bool m_quick;   // the running fade is a quick blink

        Material m_mat;
        Transform m_quad;
        float m_alpha;
        Action m_action;
        bool m_snapping;
        float m_lastEnd = -10f;
        readonly Dictionary<string, float> m_holds = new Dictionary<string, float>();   // owner -> since

        public static bool Busy => I != null && (I.m_snapping || I.m_holds.Count > 0 || I.m_alpha > 0f);
        public static bool IsBlack => I != null && I.m_alpha >= 1f;
        public static float Alpha => I != null ? I.m_alpha : 0f;
        public static bool Holding => I != null && I.m_holds.Count > 0;
        public static int Count;

        void Awake()
        {
            I = this;
            Diag.StatusExtras["fades"] = () => Count.ToString();
            Diag.StatusExtras["fadeHold"] = () => Diag.Q(string.Join(",", new List<string>(m_holds.Keys).ToArray()));
            Diag.StatusExtras["fadeAlpha"] = () => m_alpha.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            Diag.StatusExtras["quickBlinks"] = () => QuickBlinks.ToString();
            // `fadetest hold|release`: a test-owned hold; `fadewhere`: where the quad was drawn last
            // `fadetest blinksnap`: a snap turn's quick blink, then a fade-snap while it fades in - the snap's own timing (log)
            Diag.Commands["fadetest"] = a =>
            {
                if (a.Length > 1 && a[1] == "blinksnap") { StartCoroutine(BlinkSnapTest()); return "blink, then a fade-snap (see the log)"; }
                if (a.Length > 1 && a[1] == "hold") Hold("test"); else Release("test");
                return "alpha " + m_alpha;
            };
            Diag.Commands["fadewhere"] = a => "alpha " + m_alpha + " active " + (m_quad != null && m_quad.gameObject.activeSelf) + " placedFrame " + m_placedFrame + " now " + Time.frameCount +
                                             " quad " + (m_quad != null ? Diag.V(m_quad.position) : "-") + " head " + (VRRig.I != null ? Diag.V(VRRig.I.HeadWorldPos) : "-");
        }

        int m_placedFrame = -1;

        public void FadeSnap(Action action, string why = "")
        {
            if (m_alpha >= 1f)
            {
                // already black (a hold, or the end of another fade): no extra blackout needed
                Run(action);
                return;
            }
            if (m_snapping) { m_action += action; return; }
            // never strobe: right after a fade, further cuts happen without another blackout
            if (Time.unscaledTime - m_lastEnd < 0.8f && m_holds.Count == 0)
            {
                Run(action);
                return;
            }
            m_action = action;
            m_snapping = true;
            // (its own timing: a snap turn's quick blink still fading in must not make this one a quick blink too - 0.05 / 0.09 s
            // instead of 0.12 / 0.18 s, and no anti-strobe mark at its end)
            m_quick = false;
            Count++;
            Plugin.L.LogInfo("fade-snap #" + Count + " (" + why + ") in " + GameWatch.State);
        }

        /// The player's own snap turn with SnapTurnBlink: dark for a moment, turned at black. Every snap
        /// blinks (no anti-strobe rule: each one is the player's own input).
        public void QuickBlink(Action action)
        {
            if (m_alpha >= 1f) { Run(action); return; }
            if (m_snapping) { m_action += action; return; }
            m_action = action;
            m_snapping = true;
            m_quick = true;
            Count++;
            QuickBlinks++;
        }

        public static int QuickBlinks;   // diagnostics

        public void Hold(string owner)
        {
            if (m_holds.ContainsKey(owner)) return;
            if (m_holds.Count == 0) PerfMonitor.BeginHold(owner);
            m_holds[owner] = Time.unscaledTime;
            Count++;
            Plugin.L.LogInfo("fade hold '" + owner + "' (#" + Count + ") at " + Diag.Clock() + " in " + GameWatch.State);
        }

        public void Release(string owner)
        {
            float since;
            if (!m_holds.TryGetValue(owner, out since)) return;
            m_holds.Remove(owner);
            Plugin.L.LogInfo("fade release '" + owner + "' at " + Diag.Clock() + " after " + (Time.unscaledTime - since).ToString("0.0") + " s");
            if (m_holds.Count == 0) PerfMonitor.EndHold(owner);
        }

        public bool IsHeldBy(string owner) => m_holds.ContainsKey(owner);

        static void Run(Action a)
        {
            try { a?.Invoke(); }
            catch (Exception e) { Plugin.L.LogError("fade action: " + e); }
        }

        void Create()
        {
            GameObject q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.name = "HoF2VR_Fade";
            q.layer = VRUI.Layer;
            DontDestroyOnLoad(q);
            m_mat = new Material(Shader.Find("Sprites/Default")) { renderQueue = 5000, color = new Color(0, 0, 0, 0) };
            q.GetComponent<MeshRenderer>().sharedMaterial = m_mat;
            m_quad = q.transform;
        }

        bool m_mist;        // this fade belongs to a place change with the waiting space: mist, not black
        bool m_bootChecked;

        void LateUpdate()
        {
            if (m_quad == null) Create();
            float dt = Time.unscaledDeltaTime;
            bool vr = UnityEngine.XR.XRSettings.enabled;
            bool space = vr && Limbo.I != null && Settings.WaitingSpace;
            // boot: the waiting space from the first frame (nothing has been shown yet)
            if (!m_bootChecked && vr)
            {
                m_bootChecked = true;
                if (space && GameWatch.State == "")
                {
                    m_alpha = 1f;
                    m_mist = true;
                    try { Limbo.I.StartBoot(); }
                    catch (Exception e) { Plugin.L.LogError("limbo boot: " + e); }
                    LoadProgress.Begin("boot");
                }
            }
            bool placeHold = m_holds.ContainsKey(Places.HoldOwner);
            // the game's own full-screen black overlay (loading screen) darkens the whole view with it
            float game = VRUI.I != null && vr ? VRUI.I.GameBlackout() : 0f;
            // the waiting space replaces the dark of a place change (and keeps the world hidden until
            // the mist has covered it again); it stays while the game's loading backdrop is still up
            bool limboWanted = space && (placeHold || Limbo.BootGrace || Limbo.TestWanted || (Limbo.On && game >= 0.95f));
            if (limboWanted) m_mist = true;
            if (vr)
            {
                try { LoadProgress.Track(placeHold, limboWanted); }
                catch (Exception e) { Plugin.L.LogError("progress: " + e); }
            }
            bool hold = m_holds.Count > 0 || Limbo.Busy;
            if (m_snapping || hold)
            {
                m_alpha = Mathf.Min(1f, m_alpha + dt / (hold && !m_snapping ? HoldOutTime : m_quick ? QuickOutTime : OutTime));
                if (m_alpha >= 1f && m_snapping)
                {
                    m_snapping = false;
                    Action a = m_action;
                    m_action = null;
                    Run(a);
                }
            }
            else if (m_alpha > 0f)
            {
                float inTime = Time.unscaledTime - m_lastHoldEnd < 2f ? (Limbo.SlowReveal ? 0.6f : HoldInTime) : m_quick ? QuickInTime : InTime;
                m_alpha -= dt / inTime;
                // a quick blink does not count as a fade for the anti-strobe rule
                if (m_alpha <= 0f) { m_alpha = 0f; if (!m_quick) m_lastEnd = Time.unscaledTime; m_quick = false; Limbo.SlowReveal = false; }
            }
            if (hold) m_lastHoldEnd = Time.unscaledTime;
            if (Limbo.I != null)
            {
                try { Limbo.I.Step(limboWanted, m_alpha >= 1f, dt); }
                catch (Exception e) { Plugin.L.LogError("limbo: " + e); }
            }
            // in mist the loading backdrop would show on the panel as a black rectangle: it is kept off
            // the panel until the view has cleared (its alpha still darkens the view, in mist); in black
            // it is invisible
            if (VRUI.I != null && vr) VRUI.I.SuppressBlackouts(Limbo.On || m_mist);
            float shown;
            Color col;
            if (Limbo.On) { shown = Limbo.Cover; col = Limbo.Mist; }
            else
            {
                shown = Mathf.Max(m_alpha, game);
                col = m_mist ? Limbo.Mist : Color.black;
            }
            if (!hold && !m_snapping && m_alpha <= 0f && game <= 0.001f) m_mist = false;
            m_mat.color = new Color(col.r, col.g, col.b, shown);
            m_quad.gameObject.SetActive(shown > 0.001f);
        }

        float m_lastHoldEnd = -10f;

        System.Collections.IEnumerator BlinkSnapTest()
        {
            QuickBlink(() => { });
            float t0 = Time.unscaledTime;
            while (m_alpha < 1f && Time.unscaledTime - t0 < 1f) yield return null;
            while (m_alpha >= 1f && Time.unscaledTime - t0 < 1f) yield return null;   // the blink fades in
            bool quick = m_quick;
            float end0 = m_lastEnd;
            FadeSnap(() => { }, "test: right after a quick blink");
            float ts = Time.unscaledTime;
            while (m_alpha < 1f && Time.unscaledTime - ts < 1f) yield return null;
            float tb = Time.unscaledTime;
            while (m_alpha > 0f && Time.unscaledTime - tb < 1f) yield return null;
            Plugin.L.LogInfo("fadetest blinksnap: the fade-snap's fade-in took " + (Time.unscaledTime - tb).ToString("0.000") + " s (its own " + InTime + " s, a quick blink's " + QuickInTime +
                             " s); its end " + (m_lastEnd > end0 ? "marked" : "not marked") + " for the anti-strobe rule (quick while the blink faded in: " + quick + ")");
        }

        /// Keep the quad glued in front of the head (called by the overlay camera before culling).
        public void Place(Transform head)
        {
            if (m_quad == null || !m_quad.gameObject.activeSelf) return;
            m_placedFrame = Time.frameCount;
            float s = VRRig.I != null ? VRRig.I.Scale : 1f;
            m_quad.position = head.position + head.forward * (0.08f * s);
            m_quad.rotation = head.rotation;
            m_quad.localScale = new Vector3(s, s, 1f);
        }
    }
}

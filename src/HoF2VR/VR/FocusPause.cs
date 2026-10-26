using System;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace HoF2VR
{
    /// A fight never goes on unseen: the game pauses while the headset is not showing it (the Quest's passthrough,
    /// its system menu, the headset taken off) and goes on when it shows it again. (2026-10-11 07:49: the Quest's
    /// double-tap passthrough covered a fight for 23 s; the game never knew and the hero took hits.)
    ///
    /// Signals:
    ///  1. OpenComposite's IVRSystem::ShouldApplicationPause() = the OpenXR session is not FOCUSED (WiVRn passes the
    ///     headset app's state on, as far as it does). Counted only after the session was focused once.
    ///  2. Fallback: the tracking froze. Head and both controllers (each: untracked, still, or moving in an exactly
    ///     straight line = extrapolated) for 0.5 s and 25 frames right after live tracking; a paused headset app
    ///     sends no tracking at all, while a worn headset and held controllers always move a little. Test runs
    ///     start with it off (the simulator's head is perfectly still): `focus fallback 1`.
    /// Effect: in a fight (GameState_Level_Play) the game's own pause, exactly as the pause button opens it
    /// (PauseMenuManager: its pause menu, Level_Pause, time and music stopped). When the headset shows the game again
    /// a note says so and the fight goes on after 2 s the way the menu's Resume does it - unless the player uses the
    /// controls meanwhile (then the pause menu is theirs). A pause the player opened themselves is never touched.
    /// Outside fights nothing is paused (nothing at the table runs against the clock); every hidden period is logged.
    internal class FocusPause : MonoBehaviour
    {
        public static FocusPause I;
        public static bool Hidden;
        public static string Why = "-";
        public static int HiddenCount, Pauses, Resumes, StayedPaused;   // diagnostics
        public const float ResumeAfter = 2f;

        bool m_focusSeen;
        bool m_lastNotFocused;
        float m_hiddenSince = -1f, m_visibleSince = -1f;
        string m_pendingWhy;
        bool m_wePaused;
        float m_resumeAt = -1f;
        string m_fake;               // test seam: "hidden" | "visible" | null
        public static bool FallbackOn = !TestMode.Enabled;

        void Awake()
        {
            I = this;
            Diag.StatusExtras["focus"] = () => Diag.Q((Hidden ? "hidden (" + Why + ")" : "visible") + " pauses " + Pauses + " resumes " + Resumes +
                                                     " stayed " + StayedPaused + (m_wePaused ? " ourPause" : "") + (m_resumeAt > 0f ? " resumeIn " + (m_resumeAt - Time.unscaledTime).ToString("0.0") : ""));
            // `focus [fallback 0|1 | fake hidden|visible|off | info]`
            Diag.Commands["focus"] = a =>
            {
                if (a.Length > 2 && a[1] == "fallback") { FallbackOn = a[2] == "1"; ResetFrozen(); return "fallback " + FallbackOn; }
                if (a.Length > 2 && a[1] == "fake") { m_fake = a[2] == "off" ? null : a[2]; return "fake " + (m_fake ?? "off"); }
                CVRSystem sys = VRInput.VRSystem;
                string sap = "-";
                try { if (sys != null) sap = sys.ShouldApplicationPause().ToString(); } catch (Exception e) { sap = "error " + e.Message; }
                return (Hidden ? "hidden (" + Why + ")" : "visible") + " | ShouldApplicationPause " + sap + " focusSeen " + m_focusSeen +
                       " | fallback " + FallbackOn + " frozen " + m_frozenFrames + " f / " + (m_frozenSince > 0f ? (Time.unscaledTime - m_frozenSince).ToString("0.00") : "0") + " s, live " + LiveShare().ToString("0.00") +
                       " | pauses " + Pauses + " resumes " + Resumes + " stayed " + StayedPaused + " ourPause " + m_wePaused + " state " + GameWatch.State;
            };
        }

        void Update()
        {
            if (!XRSettings.enabled) return;
            string why = null;
            bool notFocused = NotFocused();
            if (notFocused) why = "the OpenXR session is not focused";
            if (Frozen() && why == null) why = "the tracking froze";
            if (m_fake == "hidden") why = "test: fake hidden";
            else if (m_fake == "visible") why = null;
            float now = Time.unscaledTime;

            if (why != null)
            {
                m_visibleSince = -1f;
                if (!Hidden)
                {
                    if (m_hiddenSince < 0f) { m_hiddenSince = now; m_pendingWhy = why; }
                    if (now - m_hiddenSince >= 0.2f) BecomeHidden(m_pendingWhy);
                }
            }
            else
            {
                m_hiddenSince = -1f;
                if (Hidden)
                {
                    if (m_visibleSince < 0f) m_visibleSince = now;
                    if (now - m_visibleSince >= 0.3f) BecomeVisible();
                }
            }

            // a fight that starts (or goes on) while the headset shows something else waits
            if (Hidden && GameWatch.State == "GameState_Level_Play") Pause();
            // (the state switch may land a frame later: never judge our own pause in its first second)
            if (m_wePaused && GameWatch.State != "GameState_Level_Pause" && now - m_pausedAt > 1f)
            {
                // resumed by the pause menu, the fight ended, ... - not ours any more
                m_wePaused = false;
                m_resumeAt = -1f;
            }
            if (m_resumeAt > 0f)
            {
                if (AnyInput())
                {
                    // the player uses the pause menu now (A/B there resume by themselves): it is theirs
                    m_resumeAt = -1f;
                    m_wePaused = false;
                    StayedPaused++;
                    Plugin.L.LogInfo("focus: the player uses the controls - the pause menu is theirs now");
                    if (VRHelp.I != null) VRHelp.I.Hide();
                }
                else if (now >= m_resumeAt) Resume();
            }
        }

        void BecomeHidden(string why)
        {
            m_hiddenStart = Time.unscaledTime;
            Hidden = true;
            Why = why;
            HiddenCount++;
            m_resumeAt = -1f;
            Plugin.L.LogInfo("focus: the headset is not showing the game (" + why + ") at " + Diag.Clock() + " in " + GameWatch.State);
        }

        void BecomeVisible()
        {
            Plugin.L.LogInfo("focus: the headset shows the game again at " + Diag.Clock() + " after " + (Time.unscaledTime - m_hiddenStart).ToString("0.0") + " s (" + Why + ") in " + GameWatch.State);
            Hidden = false;
            Why = "-";
            ResetFrozen();
            if (m_wePaused && GameWatch.State == "GameState_Level_Pause")
            {
                m_resumeAt = Time.unscaledTime + ResumeAfter;
                if (VRHelp.I != null)
                    VRHelp.I.Show("Welcome back. The fight was paused while\nthe headset wasn't showing it.\nIt goes on in 2 seconds.\n(Use the pause menu to stay paused.)", ResumeAfter + 0.3f, down: 0.34f, tilt: 25f, keep: true);   // below the pause panel
            }
        }

        float m_hiddenStart, m_pausedAt;

        /// The pause button's own path (PauseMenuManager), so the game's pause bookkeeping stays consistent.
        void Pause()
        {
            Game g = Game.Instance;
            MenuManager mm = MenuManager.Instance;
            if (g == null || mm == null || mm.PauseMenuManager == null || !(g.ActiveGameState is GameState_Level_Play)) return;
            if (mm.PauseMenuManager.CurrentState != PauseMenuManager.StateType.Closed) return;
            try
            {
                mm.PauseMenuManager.SetState(PauseMenuManager.StateType.Pause);   // refuses while a menu is moving: next frame
                if (!(g.ActiveGameState is GameState_Level_Pause)) return;
                m_wePaused = true;
                m_pausedAt = Time.unscaledTime;
                Pauses++;
                Plugin.L.LogInfo("focus: fight paused (" + Why + ")");
            }
            catch (Exception e) { Plugin.L.LogError("focus: pause failed: " + e); }
        }

        /// As the pause menu's Resume does it.
        void Resume()
        {
            m_resumeAt = -1f;
            Game g = Game.Instance;
            MenuManager mm = MenuManager.Instance;
            if (!m_wePaused || g == null || mm == null || mm.PauseMenuManager == null || !(g.ActiveGameState is GameState_Level_Pause)) { m_wePaused = false; return; }
            m_wePaused = false;
            try
            {
                mm.PauseMenuManager.SetState(PauseMenuManager.State.StateName.None);
                Resumes++;
                Plugin.L.LogInfo("focus: fight resumed");
            }
            catch (Exception e) { Plugin.L.LogError("focus: resume failed: " + e); }
        }

        static bool AnyInput()
        {
            Hand l = VRInput.L, r = VRInput.R;
            return l.TriggerDown || r.TriggerDown || l.GripDown || r.GripDown || l.ADown || r.ADown || l.BDown || r.BDown || l.StickDown || r.StickDown || l.MenuDown || r.MenuDown ||
                   l.Stick.sqrMagnitude > 0.25f || r.Stick.sqrMagnitude > 0.25f;
        }

        // ---- signal 1: the session's focus ----------------------------------------------------------------

        bool NotFocused()
        {
            CVRSystem sys = VRInput.VRSystem;
            if (sys == null) return false;
            bool pause;
            try { pause = sys.ShouldApplicationPause(); }
            catch (Exception) { return false; }
            if (!pause && !m_focusSeen)
            {
                m_focusSeen = true;
                Plugin.L.LogInfo("focus: the OpenXR session is focused (IVRSystem::ShouldApplicationPause false) at " + Diag.Clock());
            }
            if (pause != m_lastNotFocused && m_focusSeen)
                Plugin.L.LogInfo("focus: ShouldApplicationPause " + m_lastNotFocused + " -> " + pause + " at " + Diag.Clock());
            m_lastNotFocused = pause;
            return m_focusSeen && pause;
        }

        // ---- signal 2: frozen tracking --------------------------------------------------------------------

        struct Track
        {
            public bool Has, HasD;
            public Vector3 P, D;
            public Quaternion Q;
            public Vector4 DQ;
        }

        Track m_head, m_left, m_right;
        int m_frozenFrames;
        float m_frozenSince = -1f;
        bool m_frozen;
        // live history: was each of the last 90 frames moving like real tracking?
        readonly bool[] m_live = new bool[90];
        int m_liveIdx, m_liveCount;
        float m_liveAtFreeze;

        void ResetFrozen()
        {
            m_frozenFrames = 0;
            m_frozenSince = -1f;
            m_frozen = false;
        }

        float LiveShare()
        {
            int n = Mathf.Min(m_liveCount, m_live.Length), k = 0;
            for (int i = 0; i < n; i++) if (m_live[i]) k++;
            return n > 0 ? (float)k / n : 0f;
        }

        /// Still or moving in an exactly straight line (an extrapolated pose) since the last frame.
        static bool Dead(ref Track t, Vector3 p, Quaternion q)
        {
            if (t.Has && q.x * t.Q.x + q.y * t.Q.y + q.z * t.Q.z + q.w * t.Q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            bool dead = false;
            if (t.Has)
            {
                Vector3 d = p - t.P;
                var dq = new Vector4(q.x - t.Q.x, q.y - t.Q.y, q.z - t.Q.z, q.w - t.Q.w);
                if (t.HasD)
                {
                    Vector3 dd = d - t.D;
                    Vector4 ddq = dq - t.DQ;
                    dead = Mathf.Abs(dd.x) < 5e-6f && Mathf.Abs(dd.y) < 5e-6f && Mathf.Abs(dd.z) < 5e-6f &&
                           Mathf.Abs(ddq.x) < 5e-6f && Mathf.Abs(ddq.y) < 5e-6f && Mathf.Abs(ddq.z) < 5e-6f && Mathf.Abs(ddq.w) < 5e-6f;
                }
                t.D = d;
                t.DQ = dq;
                t.HasD = true;
            }
            t.P = p;
            t.Q = q;
            t.Has = true;
            return dead;
        }

        bool Frozen()
        {
            VRInput.Update();
            bool headDead = !VRInput.HeadTracked | Dead(ref m_head, VRInput.HeadPos, VRInput.HeadRot);
            bool lDead = !VRInput.L.Tracked | Dead(ref m_left, VRInput.L.Pos, VRInput.L.Rot);
            bool rDead = !VRInput.R.Tracked | Dead(ref m_right, VRInput.R.Pos, VRInput.R.Rot);
            bool dead = headDead && lDead && rDead;
            m_live[m_liveIdx] = !headDead;
            m_liveIdx = (m_liveIdx + 1) % m_live.Length;
            m_liveCount++;
            if (!dead)
            {
                if (m_frozen) Plugin.L.LogInfo("focus: tracking moves again after " + m_frozenFrames + " frozen frames");
                ResetFrozen();
                return false;
            }
            if (m_frozenFrames == 0)
            {
                m_frozenSince = Time.unscaledTime;
                m_liveAtFreeze = LiveShare();   // real tracking just before the freeze?
            }
            m_frozenFrames++;
            if (!m_frozen && m_frozenFrames >= 25 && Time.unscaledTime - m_frozenSince >= 0.5f && m_liveAtFreeze >= 0.6f)
            {
                m_frozen = true;
                Plugin.L.LogInfo("focus: tracking frozen for " + m_frozenFrames + " frames / " + (Time.unscaledTime - m_frozenSince).ToString("0.00") + " s after live tracking (" +
                                 (m_liveAtFreeze * 100f).ToString("0") + " % live frames before)" + (FallbackOn ? "" : " - fallback off (test run), ignored"));
            }
            return m_frozen && FallbackOn;
        }
    }
}

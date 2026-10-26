using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace HoF2VR
{
    /// One motion controller: pose/velocity in tracking space plus buttons and axes.
    internal class Hand
    {
        public readonly bool IsLeft;
        public bool Tracked;
        public Vector3 Pos;
        public Quaternion Rot = Quaternion.identity;
        /// The pose read at Update (Unity's XR nodes: the PREVIOUS frame's render pose). Pos/Rot hold it until the late hook
        /// (VRInput.LateHands) replaces them with the pose of the frame being rendered; LateFrame says it did this frame.
        public Vector3 LatchedPos;
        public Quaternion LatchedRot = Quaternion.identity;
        public int LateFrame = -10;
        public Vector3 Vel, AngVel;
        public float Trigger, Grip;
        public Vector2 Stick;
        public bool TriggerBtn, GripBtn, A, B, StickClick, Menu;
        /// Finger resting on the trigger (capacitive touch; false when the runtime never reports it).
        public bool TriggerTouch;
        bool m_pTrig, m_pGrip, m_pA, m_pB, m_pStick, m_pMenu;
        // finite-difference motion (some runtimes, e.g. Monado's remote driver, report no velocity)
        Vector3 m_prevPos;
        Quaternion m_prevRot = Quaternion.identity;
        bool m_hasPrev;
        internal Vector3 FdVel, FdAngVel;

        internal void Differentiate(float dt)
        {
            if (!Tracked || dt <= 1e-4f) { m_hasPrev = false; return; }
            if (m_hasPrev)
            {
                Vector3 v = (Pos - m_prevPos) / dt;
                if (v.sqrMagnitude > 15f * 15f) v = FdVel;   // tracking jump, not a real motion
                Quaternion dq = Rot * Quaternion.Inverse(m_prevRot);
                float ang;
                Vector3 axis;
                dq.ToAngleAxis(out ang, out axis);
                if (ang > 180f) ang -= 360f;
                Vector3 w = float.IsNaN(axis.x) ? Vector3.zero : axis * (ang * Mathf.Deg2Rad / dt);
                FdVel = Vector3.Lerp(FdVel, v, 0.6f);
                FdAngVel = Vector3.Lerp(FdAngVel, w, 0.6f);
            }
            m_prevPos = Pos;
            m_prevRot = Rot;
            m_hasPrev = true;
            if (Vel.sqrMagnitude < 1e-6f) Vel = FdVel;
            if (AngVel.sqrMagnitude < 1e-6f) AngVel = FdAngVel;
        }

        public Hand(bool left) { IsLeft = left; }

        public bool TriggerDown => TriggerBtn && !m_pTrig;
        public bool TriggerUp => !TriggerBtn && m_pTrig;
        public bool GripDown => GripBtn && !m_pGrip;
        public bool GripUp => !GripBtn && m_pGrip;
        public bool ADown => A && !m_pA;
        public bool BDown => B && !m_pB;
        public bool StickDown => StickClick && !m_pStick;
        public bool MenuDown => Menu && !m_pMenu;

        internal void Latch()
        {
            m_pTrig = TriggerBtn; m_pGrip = GripBtn; m_pA = A; m_pB = B; m_pStick = StickClick; m_pMenu = Menu;
        }
    }

    /// Reads the controllers once per frame. Poses come from Unity's XR nodes (same tracking space
    /// as the HMD); buttons and axes from OpenVR's legacy controller state (OpenComposite maps the
    /// runtime's controllers onto it). Tests can override buttons/axes with the `pad` command.
    internal static class VRInput
    {
        public static readonly Hand L = new Hand(true), R = new Hand(false);
        /// Both hands, right first / left first (shared arrays: per-frame loops must not allocate).
        public static readonly Hand[] RL = { R, L }, LR = { L, R };
        public static Vector3 HeadPos;
        public static Quaternion HeadRot = Quaternion.identity;

        /// The render-time head pose (see VRRig.LatePose) replaces this frame's latched one.
        public static void OverrideHead(Vector3 pos, Quaternion rot)
        {
            HeadPos = pos;
            HeadRot = rot;
        }
        public static bool HeadTracked;
        public static int Frame = -1;

        static CVRSystem s_sys;
        static float s_nextTry;
        static readonly List<XRNodeState> s_nodes = new List<XRNodeState>();
        static readonly ulong[] s_lastButtons = new ulong[2];
        static readonly Vector2[] s_lastAxes = new Vector2[6];
        static uint s_stateSize;

        // test overrides: name -> release time (unscaled); axes held until cleared
        static readonly Dictionary<string, float> s_fakeUntil = new Dictionary<string, float>();
        static readonly Dictionary<string, float> s_fakeAxis = new Dictionary<string, float>();

        public static void Update()
        {
            if (Frame == Time.frameCount) return;
            Frame = Time.frameCount;
            L.Latch();
            R.Latch();

            s_nodes.Clear();
            InputTracking.GetNodeStates(s_nodes);
            L.Tracked = R.Tracked = HeadTracked = false;
            foreach (XRNodeState n in s_nodes)
            {
                Hand h = n.nodeType == XRNode.LeftHand ? L : n.nodeType == XRNode.RightHand ? R : null;
                if (n.nodeType == XRNode.Head)
                {
                    HeadTracked = n.tracked;
                    n.TryGetPosition(out HeadPos);
                    n.TryGetRotation(out HeadRot);
                }
                if (h == null) continue;
                h.Tracked = n.tracked;
                n.TryGetPosition(out h.Pos);
                n.TryGetRotation(out h.Rot);
                n.TryGetVelocity(out h.Vel);
                n.TryGetAngularVelocity(out h.AngVel);
            }
            float dt = Time.unscaledDeltaTime;
            L.Differentiate(dt);
            R.Differentiate(dt);
            L.LatchedPos = L.Pos; L.LatchedRot = L.Rot;
            R.LatchedPos = R.Pos; R.LatchedRot = R.Rot;
            if (!HeadTracked)
            {
                HeadPos = InputTracking.GetLocalPosition(XRNode.Head);
                HeadRot = InputTracking.GetLocalRotation(XRNode.Head);
            }
            NoteHead();

            ReadButtons(L, ETrackedControllerRole.LeftHand, 0);
            ReadButtons(R, ETrackedControllerRole.RightHand, 1);
            ApplyFakes();
        }

        public static CVRSystem VRSystem => Sys();

        // ---- where the player stands: the head's recent history (the third playtest, mark 20) -------------------------------
        // A place change recentred on the head of that moment. At a challenge's start that is the press of the Start plate, out
        // to the right: the player was put 0.24 m right of the table's middle once they stood straight again.
        const int HistN = 100;   // (15 a second: 6.7 s, more than VRRig.SettleSeconds)
        const float HistStep = 1f / 15f;
        static readonly Vector3[] s_histPos = new Vector3[HistN];
        static readonly float[] s_histYaw = new float[HistN], s_histT = new float[HistN];
        static int s_histHead, s_histCount;
        static float s_histNext;
        static readonly float[] s_sort = new float[HistN];

        static void NoteHead()
        {
            float now = Time.unscaledTime;
            if (now < s_histNext || HeadPos == Vector3.zero) return;
            s_histNext = now + HistStep;
            s_histPos[s_histHead] = HeadPos;
            s_histYaw[s_histHead] = HeadRot.eulerAngles.y;
            s_histT[s_histHead] = now;
            s_histHead = (s_histHead + 1) % HistN;
            if (s_histCount < HistN) s_histCount++;
        }

        /// Where the head mostly was over the last `seconds` (tracking space): the median of its position and of its facing
        /// (a short lean or turn - reaching for a plate - does not count). False when there are too few samples.
        public static bool StandingHead(float seconds, out Vector3 pos, out float yaw) => StandingHead(seconds, Time.unscaledTime, out pos, out yaw);

        /// The same over the `seconds` before `until` (unscaled time): where the player stood before reaching out.
        public static bool StandingHead(float seconds, float until, out Vector3 pos, out float yaw)
        {
            pos = HeadPos;
            yaw = HeadRot.eulerAngles.y;
            float since = until - seconds;
            int n = 0;
            for (int k = 0; k < s_histCount; k++) { float t = s_histT[(s_histHead - 1 - k + HistN) % HistN]; if (t >= since && t <= until) n++; }
            if (n < 8) return false;
            pos = new Vector3(Median(0, n, since, until), Median(1, n, since, until), Median(2, n, since, until));
            yaw = Mathf.Repeat(yaw + Median(3, n, since, until), 360f);
            return true;
        }

        /// The median of one coordinate of the samples from `since` to `until` (3 = the facing, as degrees from the facing now).
        static float Median(int axis, int n, float since, float until)
        {
            float now = HeadRot.eulerAngles.y;
            int m = 0;
            for (int k = 0; k < s_histCount && m < n; k++)
            {
                int i = (s_histHead - 1 - k + HistN) % HistN;
                if (s_histT[i] < since || s_histT[i] > until) continue;
                s_sort[m++] = axis < 3 ? s_histPos[i][axis] : Mathf.DeltaAngle(now, s_histYaw[i]);
            }
            System.Array.Sort(s_sort, 0, m);
            return m % 2 == 1 ? s_sort[m / 2] : 0.5f * (s_sort[m / 2 - 1] + s_sort[m / 2]);
        }

        // ---- the render-time controller pose (docs/hand-feel-design.md §3.2) --------------------------------------------
        // Unity's XR nodes give the controllers' pose of the PREVIOUS frame (as for the head, VRRig's late pose): the gloves
        // were drawn a frame (~11 ms at 90 Hz) behind the hand. In the late hook (the tracker camera's OnPreCull) the runtime
        // has the poses of the frame being rendered: IVRCompositor.GetLastPoses' render poses (OpenComposite locates every
        // device at the frame's predicted display time). They replace Pos/Rot for the drawing that follows; the next frame's
        // Update reads the same pose from Unity, so the touch logic sees what was drawn.
        static CVRCompositor s_comp;
        static float s_nextComp;
        static readonly TrackedDevicePose_t[] s_render = new TrackedDevicePose_t[16], s_gamePoses = new TrackedDevicePose_t[16];
        public static bool LateHandsOn = true;   // test toggle: `latehands 0|1`
        public static int LateHandFrames, LateRejects;
        public static Vector3 LastLateDeltaR;   // tracking space, metres: render-time minus latched (right hand), diagnostics

        static CVRCompositor Comp()
        {
            if (s_comp != null) return s_comp;
            if (Time.unscaledTime < s_nextComp || !XRSettings.enabled) return null;
            s_nextComp = Time.unscaledTime + 3f;
            try
            {
                EVRInitError err = EVRInitError.None;
                IntPtr p = OpenVRInterop.GetGenericInterface("FnTable:" + OpenVR.IVRCompositor_Version, ref err);
                if (p != IntPtr.Zero && err == EVRInitError.None) { s_comp = new CVRCompositor(p); Plugin.L.LogInfo("VRInput: got " + OpenVR.IVRCompositor_Version + " (render-time controller poses)"); }
            }
            catch (Exception e) { Plugin.L.LogWarning("VRInput: compositor: " + e.Message); }
            return s_comp;
        }

        /// Replaces both hands' Pos/Rot with the render-time poses; false when they are not available (then nothing changes).
        public static bool LateHands()
        {
            if (!LateHandsOn || !Settings.LatePose || !XRSettings.enabled || Frame != Time.frameCount) return false;
            CVRSystem sys = Sys();
            CVRCompositor comp = Comp();
            if (sys == null || comp == null) return false;
            try
            {
                if (comp.GetLastPoses(s_render, s_gamePoses) != EVRCompositorError.None) return false;
                bool any = Late(L, sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand));
                any |= Late(R, sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand));
                if (any) LateHandFrames++;
                return any;
            }
            catch (Exception e)
            {
                LateHandsOn = false;
                Plugin.L.LogWarning("VRInput: render-time controller poses switched off after an error: " + e.Message);
                return false;
            }
        }

        static bool Late(Hand h, uint idx)
        {
            if (!h.Tracked || idx >= s_render.Length) return false;
            TrackedDevicePose_t p = s_render[idx];
            if (!p.bPoseIsValid || !p.bDeviceIsConnected) return false;
            Vector3 pos;
            Quaternion rot;
            ToUnity(ref p.mDeviceToAbsoluteTracking, out pos, out rot);
            // never a jump: a pose that far from Unity's (another tracking space, a glitch) is not used
            if ((pos - h.LatchedPos).sqrMagnitude > 0.15f * 0.15f || Quaternion.Angle(rot, h.LatchedRot) > 45f) { LateRejects++; return false; }
            if (!h.IsLeft) LastLateDeltaR = pos - h.LatchedPos;
            h.Pos = pos;
            h.Rot = rot;
            h.LateFrame = Time.frameCount;
            return true;
        }

        /// OpenVR's right-handed device-to-tracking matrix -> Unity's left-handed tracking space (z flipped, as
        /// SteamVR_Utils.RigidTransform does).
        public static void ToUnity(ref HmdMatrix34_t m, out Vector3 pos, out Quaternion rot)
        {
            pos = new Vector3(m.m3, m.m7, -m.m11);
            var fwd = new Vector3(-m.m2, -m.m6, m.m10);
            var up = new Vector3(m.m1, m.m5, -m.m9);
            rot = fwd.sqrMagnitude > 1e-8f && up.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(fwd, up) : Quaternion.identity;
        }

        /// The HMD's render-time pose from the same call (index 0), for the probe.
        public static bool LateHead(out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            if (!s_render[0].bPoseIsValid) return false;
            ToUnity(ref s_render[0].mDeviceToAbsoluteTracking, out pos, out rot);
            return true;
        }

        static CVRSystem Sys()
        {
            if (s_sys != null) return s_sys;
            if (Time.unscaledTime < s_nextTry || !XRSettings.enabled) return null;
            s_nextTry = Time.unscaledTime + 3f;
            try
            {
                EVRInitError err = EVRInitError.None;
                IntPtr p = OpenVRInterop.GetGenericInterface("FnTable:" + OpenVR.IVRSystem_Version, ref err);
                if (p != IntPtr.Zero && err == EVRInitError.None)
                {
                    s_sys = new CVRSystem(p);
                    s_stateSize = (uint)Marshal.SizeOf(typeof(VRControllerState_t));
                    Plugin.L.LogInfo("VRInput: got " + OpenVR.IVRSystem_Version);
                }
                else Plugin.L.LogWarning("VRInput: IVRSystem unavailable: " + err);
            }
            catch (Exception e) { Plugin.L.LogWarning("VRInput: " + e.Message); }
            return s_sys;
        }

        static void ReadButtons(Hand h, ETrackedControllerRole role, int side)
        {
            CVRSystem sys = Sys();
            if (sys == null) { Unread(h, side); return; }
            uint idx = sys.GetTrackedDeviceIndexForControllerRole(role);
            if (idx == OpenVR.k_unTrackedDeviceIndexInvalid) { Unread(h, side); return; }
            var st = new VRControllerState_t();
            if (!sys.GetControllerState(idx, ref st, s_stateSize) || Time.unscaledTime < s_testLostUntil[side]) { Unread(h, side); return; }
            s_unreadSince[side] = -1f;
            FingerSampler.Store(h.IsLeft, ref st);   // the gloves' fingers read the same state (no second call)
            ulong b = st.ulButtonPressed;
            h.Stick = new Vector2(st.rAxis0.x, st.rAxis0.y);
            h.Trigger = st.rAxis1.x;
            h.TriggerTouch = (st.ulButtonTouched & Bit(EVRButtonId.k_EButton_SteamVR_Trigger)) != 0 || h.Trigger > 0.05f;
            h.Grip = Mathf.Max(st.rAxis2.x, (b & Bit(EVRButtonId.k_EButton_Grip)) != 0 ? 1f : 0f);
            h.TriggerBtn = h.Trigger > 0.55f || (b & Bit(EVRButtonId.k_EButton_SteamVR_Trigger)) != 0 && h.Trigger > 0.3f;
            h.GripBtn = h.Grip > 0.55f;
            h.A = (b & Bit(EVRButtonId.k_EButton_A)) != 0;
            h.B = (b & Bit(EVRButtonId.k_EButton_ApplicationMenu)) != 0;
            h.StickClick = (b & Bit(EVRButtonId.k_EButton_SteamVR_Touchpad)) != 0;
            h.Menu = (b & Bit(EVRButtonId.k_EButton_System)) != 0;

            // learn the runtime's mapping: log raw changes
            if (b != s_lastButtons[side] || Changed(ref s_lastAxes[side * 3], st.rAxis0) ||
                Changed(ref s_lastAxes[side * 3 + 1], st.rAxis1) || Changed(ref s_lastAxes[side * 3 + 2], st.rAxis2))
            {
                s_lastButtons[side] = b;
                if (Settings.LogInput)
                    Plugin.L.LogInfo(string.Format("raw {0}: buttons=0x{1:X} ax0=({2:0.00},{3:0.00}) ax1={4:0.00} ax2={5:0.00}",
                        h.IsLeft ? "L" : "R", b, st.rAxis0.x, st.rAxis0.y, st.rAxis1.x, st.rAxis2.x));
            }
        }

        /// A controller whose state can't be read (it lost power, dropped out): after LostAfter its buttons, trigger, grip and
        /// stick read as let go - before, they stayed at their last values (the hero walked on, a card stayed gripped, the
        /// trigger held). A single unreadable frame changes nothing (no false release of a held grip).
        static readonly float[] s_unreadSince = { -1f, -1f };
        static readonly float[] s_testLostUntil = { -1f, -1f };   // `pad l|r lost <ms>`: as if its state could not be read
        public static float LostAfter = 0.25f;
        public static int LostClears;

        static void Unread(Hand h, int side)
        {
            float now = Time.unscaledTime;
            if (s_unreadSince[side] < 0f) s_unreadSince[side] = now;
            if (now - s_unreadSince[side] < LostAfter) return;
            if (h.Trigger == 0f && h.Grip == 0f && h.Stick == Vector2.zero && !h.TriggerBtn && !h.GripBtn && !h.A && !h.B && !h.StickClick && !h.Menu && !h.TriggerTouch) return;
            h.Trigger = h.Grip = 0f;
            h.Stick = Vector2.zero;
            h.TriggerBtn = h.GripBtn = h.A = h.B = h.StickClick = h.Menu = h.TriggerTouch = false;
            LostClears++;
            Plugin.L.LogInfo("VRInput: the " + (h.IsLeft ? "left" : "right") + " controller's state can't be read (" + (now - s_unreadSince[side]).ToString("0.00") + " s): its buttons and stick let go");
        }

        static bool Changed(ref Vector2 last, VRControllerAxis_t a)
        {
            var v = new Vector2(a.x, a.y);
            if ((v - last).sqrMagnitude < 0.04f) return false;
            last = v;
            return true;
        }

        static ulong Bit(EVRButtonId id) => 1UL << (int)id;

        // ---- test seam: `pad <l|r> <trigger|grip|a|b|stick|menu> [ms]` and `pad <l|r> axis <x> <y>`
        public static string PadCommand(string[] a)
        {
            if (a.Length < 3) return "usage: pad l|r trigger|grip|a|b|stick|menu|ttouch [ms] | pad l|r axis x y | pad l|r lost [ms] | pad clear";
            string side = a[1].Substring(0, 1);
            if (a[2] == "lost")
            {
                float lms = a.Length > 3 ? float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture) : 1000f;
                s_testLostUntil[side == "l" ? 0 : 1] = Time.unscaledTime + lms / 1000f;
                return "the " + side + " controller unreadable for " + lms + " ms (lets go after " + (LostAfter * 1000f).ToString("0") + " ms; clears so far " + LostClears + ")";
            }
            if (a[2] == "axis")
            {
                s_fakeAxis[side + "x"] = float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture);
                s_fakeAxis[side + "y"] = float.Parse(a[4], System.Globalization.CultureInfo.InvariantCulture);
                return "axis set";
            }
            float ms = a.Length > 3 ? float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture) : 150f;
            s_fakeUntil[side + a[2]] = Time.unscaledTime + ms / 1000f;
            return "pressing " + side + " " + a[2] + " for " + ms + " ms";
        }

        public static void ClearFakes() { s_fakeUntil.Clear(); s_fakeAxis.Clear(); }

        static void ApplyFakes()
        {
            foreach (var kv in s_fakeUntil)
            {
                if (Time.unscaledTime > kv.Value) continue;
                Hand h = kv.Key[0] == 'l' ? L : R;
                switch (kv.Key.Substring(1))
                {
                    case "trigger": h.TriggerBtn = true; h.Trigger = 1; h.TriggerTouch = true; break;
                    case "ttouch": h.TriggerTouch = true; break;
                    case "grip": h.GripBtn = true; h.Grip = 1; break;
                    case "a": h.A = true; break;
                    case "b": h.B = true; break;
                    case "stick": h.StickClick = true; break;
                    case "menu": h.Menu = true; break;
                }
            }
            float v;
            if (s_fakeAxis.TryGetValue("lx", out v)) L.Stick.x = v;
            if (s_fakeAxis.TryGetValue("ly", out v)) L.Stick.y = v;
            if (s_fakeAxis.TryGetValue("rx", out v)) R.Stick.x = v;
            if (s_fakeAxis.TryGetValue("ry", out v)) R.Stick.y = v;
        }
    }
}

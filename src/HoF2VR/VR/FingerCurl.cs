using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

// The gloves' fingers follow the player's own (from the work/limbo2/hands prototype; integrated 2026-10-11 with
// Hands-fingers.patch: VRHands + VRInput hooks).
//
// What reaches the mod from a Quest 2 Touch controller (Quest runtime -> WiVRn -> Monado
// oculus/touch_controller profile -> OpenComposite legacy action set -> IVRSystem::GetControllerState):
//   index   trigger value (rAxis1.x) + capacitive trigger touch (touched bit 33)
//   middle/ring/pinky   grip value only (rAxis2.x; the Touch grip has no touch sensor)
//   thumb   capacitive touch on the stick (bit 32), A/X (bit 7), B/Y (bit 1); presses of those,
//           the left menu button (pressed bit 0) and the stick deflection
//   lost    the thumb-rest touch: OpenComposite binds no legacy action to it, so a thumb lying on
//           the thumb rest reads as "off everything" (hovering)
// The real-Quest session of 2026-10-11 morning logged both "trigger touch" and "button/stick touch"
// (logs/user-session-20261011-morning/LogOutput.log lines 89-90), so the touch bits do arrive.

namespace HoF2VR
{
    /// One controller's finger signals for one frame, raw from OpenComposite's legacy controller state.
    internal struct FingerSignals
    {
        public bool Valid;
        /// Raw analog axes. Not merged with the click bits: VRInput's Hand.Grip jumps to 1 as soon as
        /// the runtime's own squeeze threshold fires, which would snap the fingers shut mid-squeeze.
        public float Trigger, Grip;
        public Vector2 Stick;
        public ulong Pressed, Touched;
    }

    /// Collects FingerSignals once per frame per hand - from VRInput's own read when VRInput hands it
    /// over (Store), else with one GetControllerState call per hand - learns which touch kinds the
    /// runtime reports, logs the first sighting of each (so a normal play session proves what the real
    /// controllers send, nobody has to test anything), and carries the `fingers fake` test overrides.
    internal static class FingerSampler
    {
        public const ulong BitMenu = 1UL << (int)EVRButtonId.k_EButton_System;              // left Touch menu (press only)
        public const ulong BitBY = 1UL << (int)EVRButtonId.k_EButton_ApplicationMenu;        // B / Y
        public const ulong BitGrip = 1UL << (int)EVRButtonId.k_EButton_Grip;                 // grip past the runtime's threshold
        public const ulong BitAX = 1UL << (int)EVRButtonId.k_EButton_A;                      // A / X
        public const ulong BitStick = 1UL << (int)EVRButtonId.k_EButton_SteamVR_Touchpad;    // thumbstick (Index: also trackpad)
        public const ulong BitTrigger = 1UL << (int)EVRButtonId.k_EButton_SteamVR_Trigger;
        public const ulong ThumbTouch = BitBY | BitAX | BitStick;
        public const ulong ThumbPress = BitMenu | BitBY | BitAX | BitStick;

        /// The runtime has reported this kind of capacitive touch at least once this session. Until then
        /// "not touched" means nothing (a runtime without touch would show every finger lifted).
        public static bool TriggerTouchSeen, ThumbTouchSeen;

        /// "Not touched" on the trigger means the index is lifted: seen once this session, or Quest Touch controllers, whose
        /// trigger is always capacitive (playtest 4, P4-5: a restarted game never saw a trigger touch in its first 30 s - the
        /// user held the grip with the index lifted - so every grip squeeze counted as a page and the menu board refused the
        /// pointing hand until the grip was let go). `fingers assume on|off|auto` (tests: Monado's simulated controllers are
        /// 'knuckles').
        public static bool TriggerTouchKnown => TriggerTouchSeen || (s_assume < 0 ? ControllerGeometry.Type == "oculus_touch" : s_assume > 0);
        static int s_assume = -1;

        static readonly FingerSignals[] s_sig = new FingerSignals[2];
        static readonly int[] s_frame = { -1, -1 };
        static uint s_size;
        static readonly ulong[] s_everTouched = new ulong[2];
        static readonly float[] s_maxTrig = new float[2], s_maxGrip = new float[2];
        // `fingers watch`: log raw changes until this (unscaled) time
        static float s_watchUntil = -1f;
        static readonly ulong[] s_lastT = new ulong[2], s_lastP = new ulong[2];
        static readonly float[] s_lastTrig = new float[2], s_lastGrip = new float[2];
        // `fingers fake`: analog values (< 0 = live) and touch bits forced on / off
        static readonly float[] s_fakeTrig = { -1f, -1f }, s_fakeGrip = { -1f, -1f };
        static readonly ulong[] s_fakeOn = new ulong[2], s_fakeOff = new ulong[2];

        /// From VRInput.ReadButtons, which already holds this frame's state (no second native call).
        public static void Store(bool left, ref VRControllerState_t st)
        {
            int side = left ? 0 : 1;
            s_frame[side] = Time.frameCount;
            FingerSignals s;
            s.Valid = true;
            s.Trigger = st.rAxis1.x;
            s.Grip = st.rAxis2.x;
            s.Stick = new Vector2(st.rAxis0.x, st.rAxis0.y);
            s.Pressed = st.ulButtonPressed;
            s.Touched = st.ulButtonTouched;
            s_sig[side] = s;
            if (s.Trigger > s_maxTrig[side]) s_maxTrig[side] = s.Trigger;
            if (s.Grip > s_maxGrip[side]) s_maxGrip[side] = s.Grip;

            ulong fresh = s.Touched & ~s_everTouched[side];
            if (fresh != 0)
            {
                s_everTouched[side] |= fresh;
                if ((fresh & BitTrigger) != 0) TriggerTouchSeen = true;
                if ((fresh & ThumbTouch) != 0) ThumbTouchSeen = true;
                Plugin.L.LogInfo("fingers: " + (left ? "L" : "R") + " reports touch " + Names(fresh) + " (0x" + fresh.ToString("X") + ")");
            }
            if (s_watchUntil > 0f) Watch(side, ref s);
        }

        /// This frame's signals with the test fakes applied; reads the controller itself when VRInput
        /// did not store them this frame.
        public static FingerSignals Get(bool left)
        {
            int side = left ? 0 : 1;
            if (s_frame[side] != Time.frameCount) Poll(left);
            return Peek(left);
        }

        /// The last stored signals with the fakes, without reading anything (diagnostics).
        public static FingerSignals Peek(bool left)
        {
            int side = left ? 0 : 1;
            FingerSignals s = s_sig[side];
            if (s_fakeTrig[side] >= 0f) { s.Trigger = s_fakeTrig[side]; s.Valid = true; }
            if (s_fakeGrip[side] >= 0f) { s.Grip = s_fakeGrip[side]; s.Valid = true; }
            if ((s_fakeOn[side] | s_fakeOff[side]) != 0) { s.Touched = (s.Touched | s_fakeOn[side]) & ~s_fakeOff[side]; s.Valid = true; }
            return s;
        }

        static void Poll(bool left)
        {
            int side = left ? 0 : 1;
            s_frame[side] = Time.frameCount;
            s_sig[side].Valid = false;
            CVRSystem sys = VRInput.VRSystem;
            if (sys == null) return;
            uint idx = sys.GetTrackedDeviceIndexForControllerRole(left ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand);
            if (idx == OpenVR.k_unTrackedDeviceIndexInvalid) return;
            if (s_size == 0) s_size = (uint)Marshal.SizeOf(typeof(VRControllerState_t));
            var st = new VRControllerState_t();
            if (sys.GetControllerState(idx, ref st, s_size)) Store(left, ref st);
        }

        static void Watch(int side, ref FingerSignals s)
        {
            if (Time.unscaledTime > s_watchUntil) { s_watchUntil = -1f; Plugin.L.LogInfo("fingers: watch ended"); return; }
            if (s.Touched == s_lastT[side] && s.Pressed == s_lastP[side] &&
                Mathf.Abs(s.Trigger - s_lastTrig[side]) < 0.1f && Mathf.Abs(s.Grip - s_lastGrip[side]) < 0.1f) return;
            s_lastT[side] = s.Touched;
            s_lastP[side] = s.Pressed;
            s_lastTrig[side] = s.Trigger;
            s_lastGrip[side] = s.Grip;
            Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "fingers {0}: touched [{1}] pressed [{2}] trig {3:0.00} grip {4:0.00} stick ({5:0.00},{6:0.00})",
                side == 0 ? "L" : "R", Names(s.Touched), Names(s.Pressed), s.Trigger, s.Grip, s.Stick.x, s.Stick.y));
        }

        public static string Names(ulong b)
        {
            string n = "";
            if ((b & BitTrigger) != 0) n += " trigger";
            if ((b & BitStick) != 0) n += " stick";
            if ((b & BitAX) != 0) n += " A/X";
            if ((b & BitBY) != 0) n += " B/Y";
            if ((b & BitMenu) != 0) n += " menu";
            if ((b & BitGrip) != 0) n += " grip";
            ulong other = b & ~(BitTrigger | BitStick | BitAX | BitBY | BitMenu | BitGrip);
            if (other != 0) n += " other:0x" + other.ToString("X");
            return n.Length > 0 ? n.Substring(1) : "-";
        }

        public static void Assume(int v) { s_assume = v; }

        public static string DescribeSeen()
        {
            return string.Format(CultureInfo.InvariantCulture, "seen trigger={0} thumb={1} ever L[{2}] R[{3}] max trig/grip L {4:0.00}/{5:0.00} R {6:0.00}/{7:0.00}{8}",
                TriggerTouchSeen ? 1 : 0, ThumbTouchSeen ? 1 : 0, Names(s_everTouched[0]), Names(s_everTouched[1]),
                s_maxTrig[0], s_maxGrip[0], s_maxTrig[1], s_maxGrip[1], s_watchUntil > 0f ? " (watching)" : "");
        }

        public static string WatchCommand(float seconds)
        {
            s_watchUntil = Time.unscaledTime + seconds;
            s_lastT[0] = s_lastT[1] = ulong.MaxValue;   // log the current state once
            return "logging raw finger signals for " + seconds.ToString("0", CultureInfo.InvariantCulture) + " s";
        }

        /// fake l|r trig|grip <0-1> | fake l|r ttouch|stouch|atouch|btouch 0|1 | fake clear
        public static string FakeCommand(string[] a)
        {
            if (a.Length > 2 && a[2] == "clear")
            {
                for (int i = 0; i < 2; i++) { s_fakeTrig[i] = s_fakeGrip[i] = -1f; s_fakeOn[i] = s_fakeOff[i] = 0; }
                return "fakes cleared";
            }
            if (a.Length < 5) return "usage: fingers fake l|r trig|grip <0-1> | fingers fake l|r ttouch|stouch|atouch|btouch 0|1 | fingers fake clear";
            int side = a[2].StartsWith("l", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            float v = float.Parse(a[4], CultureInfo.InvariantCulture);
            ulong bit = 0;
            switch (a[3])
            {
                case "trig": s_fakeTrig[side] = Mathf.Clamp01(v); return "trigger " + v;
                case "grip": s_fakeGrip[side] = Mathf.Clamp01(v); return "grip " + v;
                case "ttouch": bit = BitTrigger; break;
                case "stouch": bit = BitStick; break;
                case "atouch": bit = BitAX; break;
                case "btouch": bit = BitBY; break;
                default: return "unknown fake '" + a[3] + "'";
            }
            if (v > 0.5f) { s_fakeOn[side] |= bit; s_fakeOff[side] &= ~bit; }
            else { s_fakeOff[side] |= bit; s_fakeOn[side] &= ~bit; }
            // a fake touch also teaches the capability (tests on runtimes that never report it)
            if (bit == BitTrigger) TriggerTouchSeen = true; else ThumbTouchSeen = true;
            return a[3] + (v > 0.5f ? " on" : " off");
        }
    }

    /// The fingers of one glove: maps FingerSignals to HandPose curls (HandGeometry's pose space:
    /// curl 0 open .. 1 round the controller handle .. 2 tight fist, Point 0..1 straightens the
    /// index, Thumb 0 lifted .. 1 on the controller's face .. 2 round the grip) and smooths them with
    /// critically damped springs. Overrides: forced test poses, the fist round a held sword/shield,
    /// fingers closed round held dice, the pointing finger near a touch target, and - only on runtimes
    /// that never report trigger touch - the old forced point of the laser hand. Each glove follows
    /// its own controller, so left-handed play needs nothing here (CombatVR mounts the weapon on the
    /// other hand; the Holding fist follows that mount). Per-frame: arithmetic only, no allocations.
    internal sealed class FingerCurl
    {
        // ---- tuning (live: `fingers tune <Field> <value>`) -------------------------------------
        public static float TriggerDead = 0.03f, TriggerFull = 0.95f;
        public static float GripDead = 0.04f, GripFull = 0.88f;
        /// Ring and pinky start (and finish) closing this much later than the middle finger.
        public static float GripStagger = 0.035f;
        /// Middle/ring/pinky with the grip released. The Touch grip cannot tell fingers resting on it from
        /// fingers lifted off it (no touch sensor), so released reads as open.
        public static float OpenCurl = 0.05f;
        /// Index resting on the trigger without pulling (trigger touch reported, value 0).
        public static float IndexRest = 0.45f;
        /// Same while the runtime never reported trigger touch (can't tell resting from lifted).
        public static float IndexRestBlind = 0.25f;
        /// Index lifted off the trigger: its curl under the point blend, and how straight it gets with
        /// an open hand (a relaxed finger) - a closed hand straightens it fully (a real point).
        public static float IndexLifted = 0.1f, LiftedPointOpen = 0.55f;
        /// Thumb off every sensor. It may be lying on the thumb rest (not reported), so only hover.
        /// (User 2026-10-11: "the thumb is too far out when touching the face buttons": all three a bit deeper.)
        public static float ThumbHover = 0.8f;
        /// Thumb touching a face button or the stick (1 = the fist pose's thumb: it stood out from the controller).
        public static float ThumbOn = 1.15f;
        /// Thumb pressing a face button or the stick: a little deeper than resting.
        public static float ThumbPressed = 1.22f;
        /// Thumb up while the fist is closed and the thumb has been off everything for ThumbsUpDelay.
        /// ThumbsUpGrip > 1 disables it.
        public static float ThumbsUp = 0.05f, ThumbsUpGrip = 0.85f, ThumbsUpDelay = 0.4f;
        /// Touch-off debounce (s): a finger sliding between sensors doesn't flash "lifted".
        public static float LiftDelay = 0.05f, ThumbLiftDelay = 0.12f;
        /// Spring rates (rad/s): analog curls (ramp lag 2/w = 44 ms, about the old 18/s filter) and
        /// touch-driven steps (point, thumb: eased, 90 % in ~160 ms).
        public static float OmegaAnalog = 45f, OmegaBinary = 24f;
        /// Fingers closed round held dice (DiceHand.HoldPoint puts them in the curled fingers).
        public static float DiceCurl = 1f, DiceIndex = 0.9f;
        public static float ReadyCurl = 0.3f;   // a ready hand over something to grab
        /// The pinch (a card held at its edge): the index half curled to meet the thumb, the others resting behind.
        public static float PinchIndex = 0.55f, PinchThumb = 0.85f, PinchRest = 0.7f;

        static readonly HandPose HoldFist = new HandPose(1.9f, 2f, 2f, 2f, 0f, 2f);

        public struct Context
        {
            public string Forced;     // `hands pose` name, "" = live input
            public bool Holding;      // CombatVR holds a sword / shield in this hand
            public bool DiceHeld;     // DiceHand.Holder is this hand
            public bool LaserHand;    // VRPointer drives the laser with this hand
            public bool WantsPoint;   // TouchInput: a touch target is near this fingertip
            public float PointWeight; // HandFeel: 0 at 12 cm in front of a target, 1 from 6 cm (the glove pre-shapes)
            public float GrabReady;   // 0..1: something to grab under the open hand (DiceHand, CardGrab): a "ready" hand
            public bool Pinch;        // a picked-up table card is held between the thumb and the index (TableCardGrab)
        }

        /// Critically damped spring, exact step for a fixed target (stable for any dt, no overshoot
        /// from rest).
        struct Spring
        {
            public float X, V;
            public void Snap(float x) { X = x; V = 0f; }
            public void Step(float target, float w, float e, float dt)
            {
                float y = X - target;
                float k = (V + w * y) * dt;
                X = target + (y + k) * e;
                V = (V - w * k) * e;
            }
        }

        public readonly bool Left;
        Spring m_index, m_middle, m_ring, m_pinky, m_point, m_thumb;
        float m_trigOff, m_thumbOff, m_thumbsUpFor;
        int m_lastFrame = -100;
        bool m_init;
        /// Diagnostics: the target and smoothed pose of the last step, and the input-only curls (0..1:
        /// 0 straight / thumb up, 1 closed round the handle / thumb on the face).
        public HandPose Target, Pose;
        public float CurlThumb, CurlIndex, CurlMiddle, CurlRing, CurlPinky;
        Context m_ctx;

        public FingerCurl(bool left) { Left = left; }

        /// Once per frame per shown hand (VRHands.Step). A second call in the same frame (dt 0, e.g.
        /// from onBeforeRender) returns the same pose.
        public HandPose Step(ref FingerSignals sig, Context ctx, float dt)
        {
            int frame = Time.frameCount;
            if (m_init && (dt <= 0f || frame == m_lastFrame)) return Pose;
            bool gap = frame - m_lastFrame > 5;   // hidden or untracked meanwhile: start at the target
            m_lastFrame = frame;
            m_ctx = ctx;
            dt = Mathf.Clamp(dt, 0f, 0.1f);
            // unreadable state: hold the last target (zeros would read as an open hand, finger lifted)
            HandPose t = sig.Valid || !m_init || ctx.Holding || !string.IsNullOrEmpty(ctx.Forced) ? Map(ref sig, ctx, dt) : Target;
            Target = t;
            if (!m_init || gap)
            {
                m_index.Snap(t.Index); m_middle.Snap(t.Middle); m_ring.Snap(t.Ring); m_pinky.Snap(t.Pinky);
                m_point.Snap(t.Point); m_thumb.Snap(t.Thumb);
                m_init = true;
            }
            else if (dt > 0f)
            {
                float wa = OmegaAnalog, wb = OmegaBinary;
                float ea = Mathf.Exp(-wa * dt), eb = Mathf.Exp(-wb * dt);
                m_index.Step(t.Index, wa, ea, dt);
                m_middle.Step(t.Middle, wa, ea, dt);
                m_ring.Step(t.Ring, wa, ea, dt);
                m_pinky.Step(t.Pinky, wa, ea, dt);
                m_point.Step(t.Point, wb, eb, dt);
                m_thumb.Step(t.Thumb, wb, eb, dt);
            }
            Pose = new HandPose(Mathf.Clamp(m_index.X, 0f, 2f), Mathf.Clamp(m_middle.X, 0f, 2f), Mathf.Clamp(m_ring.X, 0f, 2f),
                Mathf.Clamp(m_pinky.X, 0f, 2f), Mathf.Clamp01(m_point.X), Mathf.Clamp(m_thumb.X, 0f, 2f));
            return Pose;
        }

        /// Input -> target pose for this frame.
        HandPose Map(ref FingerSignals sig, Context ctx, float dt)
        {
            HandPose forced;
            if (ForcedPose(ctx.Forced, out forced)) return forced;
            if (ctx.Holding) return HoldFist;   // fist round the hilt / shield handle

            // middle, ring, pinky: the grip (analog). Digital-only grips fall back to the click bit.
            float g = sig.Grip;
            if (g < GripDead && (sig.Pressed & FingerSampler.BitGrip) != 0) g = 1f;
            float gn = Remap(g, GripDead, GripFull);
            float s = GripStagger;
            float middle = Mathf.Lerp(OpenCurl, 1f, EaseOut(gn));
            float ring = Mathf.Lerp(OpenCurl, 1f, EaseOut(Remap(g, GripDead + s, Mathf.Min(1f, GripFull + s))));
            float pinky = Mathf.Lerp(OpenCurl, 1f, EaseOut(Remap(g, GripDead + 2f * s, Mathf.Min(1f, GripFull + 2f * s))));

            // index: trigger touch + value
            float v = Remap(sig.Trigger, TriggerDead, TriggerFull);
            bool trigKnown = FingerSampler.TriggerTouchKnown;
            bool onTrig = !trigKnown || v > 0f || ((sig.Touched | sig.Pressed) & FingerSampler.BitTrigger) != 0;
            m_trigOff = onTrig ? 0f : m_trigOff + dt;
            float index, point;
            if (m_trigOff > LiftDelay)
            {
                index = IndexLifted;
                point = Mathf.Lerp(LiftedPointOpen, 1f, gn);
            }
            else
            {
                index = Mathf.Lerp(trigKnown ? IndexRest : IndexRestBlind, 1f, v);
                point = 0f;
            }

            // thumb: any touch / press / stick deflection = on the controller's face
            bool thumbKnown = FingerSampler.ThumbTouchSeen;
            bool pressed = (sig.Pressed & FingerSampler.ThumbPress) != 0;
            bool down = !thumbKnown || pressed || (sig.Touched & FingerSampler.ThumbTouch) != 0 || sig.Stick.sqrMagnitude > 0.02f;
            m_thumbOff = down ? 0f : m_thumbOff + dt;
            bool lifted = m_thumbOff > ThumbLiftDelay;
            m_thumbsUpFor = lifted && g >= ThumbsUpGrip ? m_thumbsUpFor + dt : 0f;
            float thumb = !lifted ? (pressed ? ThumbPressed : ThumbOn) : m_thumbsUpFor > ThumbsUpDelay ? ThumbsUp : ThumbHover;

            CurlIndex = (1f - point) * index;
            CurlMiddle = middle;
            CurlRing = ring;
            CurlPinky = pinky;
            CurlThumb = Mathf.Clamp01(thumb);

            if (ctx.DiceHeld)
            {
                // the dice sit in the curled fingers: closed whatever the grip says (a seam hold has none)
                middle = Mathf.Max(middle, DiceCurl);
                ring = Mathf.Max(ring, DiceCurl);
                pinky = Mathf.Max(pinky, DiceCurl);
                index = Mathf.Max(index, DiceIndex);
                point = 0f;
                thumb = Mathf.Max(thumb, 1f);
            }
            else if (ctx.Pinch)
            {
                // a card held at its edge between the thumb and the index (docs/hand-feel-design.md §3.7 "pinch")
                index = PinchIndex;
                middle = Mathf.Max(middle, PinchRest);
                ring = Mathf.Max(ring, PinchRest + 0.08f);
                pinky = Mathf.Max(pinky, PinchRest + 0.12f);
                point = 0f;
                thumb = PinchThumb;
            }
            else if (ctx.GrabReady > 0.001f && !ctx.WantsPoint)
            {
                // something to grab under the open hand (docs/hand-feel-design.md §3.7): the fingers curl a little, ready
                float w = Mathf.Clamp01(ctx.GrabReady);
                middle = Mathf.Max(middle, ReadyCurl * w);
                ring = Mathf.Max(ring, ReadyCurl * w);
                pinky = Mathf.Max(pinky, ReadyCurl * w);
                index = Mathf.Max(index, ReadyCurl * 0.8f * w);
                point *= 1f - w;
            }
            else if (ctx.WantsPoint && v < 0.3f)
            {
                // a touch target is near the fingertip: point at it, more the nearer it is (touch uses the fixed pointing
                // tip, so this is only what the player sees)
                float w = ctx.PointWeight > 0f ? Mathf.Clamp01(ctx.PointWeight) : 1f;
                point = Mathf.Lerp(point, 1f, w);
                index = Mathf.Lerp(index, 0.3f, w);
            }
            else if (ctx.LaserHand && !trigKnown)
            {
                // no trigger touch from this runtime: the laser hand points until the trigger is
                // pulled, then curls to click (the old behaviour). With touch it follows the finger.
                point = 1f - Mathf.InverseLerp(0.05f, 0.55f, sig.Trigger);
                index = Mathf.Lerp(0.45f, 1f, sig.Trigger);
                middle = Mathf.Max(middle, 0.8f);
                ring = Mathf.Max(ring, 0.8f);
                pinky = Mathf.Max(pinky, 0.8f);
            }
            return new HandPose(index, middle, ring, pinky, point, thumb);
        }

        /// The `hands pose` test poses (unchanged from VRHands.Target).
        public static bool ForcedPose(string name, out HandPose p)
        {
            switch (name)
            {
                case "open": p = new HandPose(0f, 0f, 0f, 0f, 0f, 0.35f); return true;
                case "grip": p = new HandPose(1f, 1f, 1f, 1f, 0f, 1f); return true;
                case "fist": p = new HandPose(1.9f, 2f, 2f, 2f, 0f, 2f); return true;
                case "point": p = new HandPose(0.35f, 1f, 1f, 1f, 1f, 1f); return true;
                case "thumbup": p = new HandPose(1f, 1f, 1f, 1f, 0f, 0f); return true;
            }
            p = new HandPose();
            return false;
        }

        static float Remap(float x, float lo, float hi) => hi > lo ? Mathf.Clamp01((x - lo) / (hi - lo)) : (x >= hi ? 1f : 0f);

        /// Fingers reach the handle early in the squeeze, then tighten slowly.
        static float EaseOut(float x) => 1f - (1f - x) * (1f - x);

        // ---- diagnostics: `fingers` ----------------------------------------------------------------

        public string Describe()
        {
            FingerSignals s = FingerSampler.Peek(Left);
            return string.Format(CultureInfo.InvariantCulture,
                "{0}trig {1:0.00} grip {2:0.00} stick ({3:0.0},{4:0.0}) touched [{5}] pressed [{6}] | curl th{7:0.00} i{8:0.00} m{9:0.00} r{10:0.00} p{11:0.00} | " +
                "target i{12:0.00} m{13:0.00} r{14:0.00} p{15:0.00} pt{16:0.00} th{17:0.00} | pose i{18:0.00} m{19:0.00} r{20:0.00} p{21:0.00} pt{22:0.00} th{23:0.00}{24}",
                s.Valid ? "" : "(no state) ", s.Trigger, s.Grip, s.Stick.x, s.Stick.y, FingerSampler.Names(s.Touched), FingerSampler.Names(s.Pressed),
                CurlThumb, CurlIndex, CurlMiddle, CurlRing, CurlPinky,
                Target.Index, Target.Middle, Target.Ring, Target.Pinky, Target.Point, Target.Thumb,
                Pose.Index, Pose.Middle, Pose.Ring, Pose.Pinky, Pose.Point, Pose.Thumb,
                (m_ctx.Holding ? " holding" : "") + (m_ctx.DiceHeld ? " dice" : "") + (m_ctx.LaserHand ? " laser" : "") +
                (m_ctx.WantsPoint ? " wantspoint" : "") + (string.IsNullOrEmpty(m_ctx.Forced) ? "" : " forced:" + m_ctx.Forced));
        }

        /// fingers                                   signals, curls, target and pose of both hands
        /// fingers watch [s]                         log every raw touch/press change (and 0.1 axis moves) for s s (20)
        /// fingers fake l|r trig|grip <0-1>          hold an analog value (until `fingers fake clear`)
        /// fingers fake l|r ttouch|stouch|atouch|btouch 0|1   force a touch off / on
        /// fingers fake clear
        /// fingers tune <Field> [value]              read / set a FingerCurl tuning field (e.g. IndexRest 0.5)
        public static string Command(string[] a, FingerCurl l, FingerCurl r)
        {
            if (a.Length < 2)
                return FingerSampler.DescribeSeen() + " || L " + (l != null ? l.Describe() : "-") + " || R " + (r != null ? r.Describe() : "-");
            switch (a[1])
            {
                case "assume":
                {
                    // the Quest Touch assumption (trigger touch known without having seen one): on / off / auto (by the type)
                    if (a.Length > 2) FingerSampler.Assume(a[2] == "on" ? 1 : a[2] == "off" ? 0 : -1);
                    return "trigger touch known " + FingerSampler.TriggerTouchKnown + " (seen " + FingerSampler.TriggerTouchSeen + ", controllers '" + ControllerGeometry.Type + "')";
                }
                case "watch":
                    return FingerSampler.WatchCommand(a.Length > 2 ? float.Parse(a[2], CultureInfo.InvariantCulture) : 20f);
                case "fake":
                    return FingerSampler.FakeCommand(a);
                case "tune":
                {
                    if (a.Length < 3) return "usage: fingers tune <Field> [value]";
                    FieldInfo f = typeof(FingerCurl).GetField(a[2], BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                    if (f == null || f.FieldType != typeof(float) || f.IsInitOnly) return "no float field '" + a[2] + "'";
                    if (a.Length > 3) f.SetValue(null, float.Parse(a[3], CultureInfo.InvariantCulture));
                    return f.Name + " = " + ((float)f.GetValue(null)).ToString("0.###", CultureInfo.InvariantCulture);
                }
            }
            return "usage: fingers [watch [s] | fake l|r trig|grip <0-1> | fake l|r ttouch|stouch|atouch|btouch 0|1 | fake clear | tune <Field> [value]]";
        }
    }
}

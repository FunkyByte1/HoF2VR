using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Touch the table's cards, map tiles and dice with a fingertip (interaction design §2.2, §2.3, §3.1).
    /// The fingertip (the glove's index tip; else the controller's pointer tip plus the finger) is tested
    /// against the colliders and the layer the game's own 3D UI picks (the 'Card Table 3DUI Camera' mask),
    /// and walked up to the selectable that acts, exactly like the laser (VRPointer.PickTarget: a card on
    /// a map tile means the tile; a tile the hero can't reach is "blocked").
    ///  Hover: the finger comes down onto a target from above (within 3 cm of its surface) -> the game's own
    ///         hover (UISelection.TryHover: highlight, the hero turns toward a tile), a light tick, a dot.
    ///  Press: after 80 ms of hover the finger pushes 1 cm through the surface, moving into it (not
    ///         sweeping across, not creeping) -> the game's own select (UISelection.TrySelect), a click.
    ///         Refused (blocked tile, card still moving, locked) -> a double tick and nothing happens.
    ///  One press per contact: the finger must come back 1.5 cm above the surface; 0.35 s between presses.
    /// Far targets (the map is ~1 m away) need a lean - or the laser, which keeps working.
    internal class TouchInput : MonoBehaviour
    {
        public static TouchInput I;
        // real metres and seconds (world = these x the room scale)
        public static float HoverRadius = 0.03f, HoverDepth = 0.03f, LeaveDepth = 0.04f, AboveDepth = 0.015f;
        // (2026-10-11 evening, "my finger has to go half way into the button ... It should be the tip of my finger"; numbers
        // from docs/hand-feel-design.md §3.4: no hover dwell - it let fast pokes sink ~4 cm or get lost - and the press
        // 2 mm past the face instead of 1 cm; the sideways limit is an angle (within 60 degrees of straight in), the
        // finger may have come from the front up to 1.5 s before)
        // (2026-10-12, the hand feel's Phase 1: the click depth is per family - cards and dice 2 mm, map tiles 3 mm - the
        // fingertip is HandFeel's interaction tip (smoothed and pulled toward the target's middle near it), the drawn finger
        // stops on the face, re-arm 1 cm in front)
        public static float Dwell = 0f, MinApproach = 0.05f, MaxTangential = 0.5f, MaxAngleTan = 1.73f, RearmDepth = 0.01f, NearRadius = 0.14f;
        public static float FromAboveWindow = 1.5f, Refractory = 0.35f, Hysteresis = 0.006f, FootMargin = 0.005f, StayMargin = 0.006f, Below = 0.08f;

        /// Pending: a press on a card of a row that scrolls, waiting to see whether the finger slides along the row (a swipe: no
        /// press) or lifts without sliding (the press: selected then). (The second playtest, P2-5, mark 6: the deck builder's row
        /// selected the card a finger came down on before it could slide - "you need to be able to swipe it".)
        enum State { Idle, Hover, Latched, Drag, Pending }

        struct Cand
        {
            public Collider Col;
            public UISelectable Sel;
            public bool Blocked;
            public float Depth;     // world units above (+) / through (-) the surface
            public Vector3 N;       // surface normal, toward the player
            public bool Wheel;      // a card of the spinning wheel: contact stops it
            public FeelSurface Surf;
            public PressFamily Fam;
        }

        sealed class Finger
        {
            public readonly bool Left;
            public State St;
            public Cand C;
            public bool HasC;
            public float HoverSince, ApproachT = -10f;
            public float MinDepth, MaxApproach, MaxSide;   // diagnostics of the current hover
            public UISelectable AboveSel;     // the target the finger was last seen above (>= 1.5 cm)
            public float AboveT = -10f;
            public Vector3 Tip, PrevTip;
            public bool HasPrev;
            public bool Override;
            public Vector3 OverrideTip;
            public Transform Dot;
            public bool Near, Touched;
            public int NearFrame;
            // the depth over the last moments: a press needs the finger to have come in at least MinApproach over
            // ApproachWindow (a creep or one jittery frame never presses; a poke does at once)
            public readonly MotionWindow Path = new MotionWindow();
            public readonly float[] DepthT = new float[16], DepthV = new float[16];
            public int DepthN, DepthI;
            // swipe to scroll (user 2026-10-12: rows with many cards "only scroll with the stick. I should be able to swipe
            // along them"): the lateral way of the finger over a scrollable row since the hover began
            public Vector3 DragFrom, DragAxis;
            public float DragAccum, DragStep, DragV, DragLastT;
            public UISelectable DragSel;
            public System.Action<UISelectable, float> DragScroll;
            public Finger(bool left) { Left = left; }

            public void Record(float t, float depth)
            {
                DepthT[DepthI] = t; DepthV[DepthI] = depth;
                DepthI = (DepthI + 1) % DepthT.Length;
                if (DepthN < DepthT.Length) DepthN++;
            }

            /// Inward speed over the window (m/s in world units): depth then minus depth now.
            public float InwardSpeed(float now, float depthNow, float window)
            {
                float best = float.NaN, bestT = 0f;
                for (int i = 0; i < DepthN; i++)
                {
                    float age = now - DepthT[i];
                    if (age < window * 0.5f || age > window * 1.5f) continue;
                    if (float.IsNaN(best) || Mathf.Abs(age - window) < Mathf.Abs(now - bestT - window)) { best = DepthV[i]; bestT = DepthT[i]; }
                }
                if (float.IsNaN(best))
                {
                    // a fast poke came into range only just now: the oldest sample (at least 25 ms old) tells its speed
                    for (int i = 0; i < DepthN; i++)
                    {
                        float age = now - DepthT[i];
                        if (age >= 0.025f && age <= window * 1.5f && (float.IsNaN(best) || DepthT[i] < bestT)) { best = DepthV[i]; bestT = DepthT[i]; }
                    }
                }
                return float.IsNaN(best) ? 0f : (best - depthNow) / Mathf.Max(1e-3f, now - bestT);
            }
        }

        public static float ApproachWindow = 0.1f;

        static readonly Finger s_l = new Finger(true), s_r = new Finger(false);
        static readonly Collider[] s_hits = new Collider[48];
        static float s_lastConfirm = -10f;
        public static int Hovers, Selects, Refusals;
        public static string Last = "-";
        static Material s_dotMat;

        void Awake()
        {
            I = this;
            Diag.StatusExtras["touch"] = () => Diag.Q(Describe());
            Diag.Commands["touch"] = Command;
            Diag.Commands["touchpose"] = TouchPose;
        }

        static Finger F(Hand h) => h.IsLeft ? s_l : s_r;

        /// The fingertip in the world: what the player sees (the glove's index tip), else the controller's.
        public static Vector3 TipOf(Hand h)
        {
            Finger f = F(h);
            if (f.Override) return f.OverrideTip;
            // the glove's index tip as it is when pointing (fixed in the hand: it doesn't move with the
            // finger animation), else the controller's pointer tip plus the finger
            Vector3 local;
            if (VRHands.On && VRRig.I != null && VRHands.TryGetPointingTipLocal(h.IsLeft, out local)) return VRRig.I.ToWorld(h.Pos + h.Rot * local);
            return HandWorld.Tip(h);
        }

        /// The glove should point: a touch target is within a few centimetres of the fingertip.
        public static bool WantsPoint(Hand h)
        {
            Finger f = F(h);
            return f.Near && Time.frameCount - f.NearFrame <= 2;
        }

        /// Is a hand touching something (hover or pressed) - the laser then leaves the game's hover alone.
        public static bool Touching(Hand h) => F(h).St != State.Idle;
        /// What this hand's fingertip is on now (hover, press or swipe), or null (DealerGaze glances at it).
        public static Transform Touched(Hand h)
        {
            Finger f = F(h);
            return f.St != State.Idle && f.C.Sel != null ? f.C.Sel.transform : null;
        }

        static int PickMask()
        {
            UI3D ui = UI3D.Instance;
            UICamera uc = ui != null ? ui.UICamera : null;
            return uc != null && uc.cachedCamera != null ? uc.cachedCamera.cullingMask & (int)uc.eventReceiverMask : 1 << 16;
        }

        static bool Allowed(Hand h, Finger f)
        {
            if (f.Override) return true;
            if (!Settings.TouchCards || !XRSettings.enabled || VRRig.I == null || !Places.AtCaravan || GameWatch.InCombatScene) return false;
            if (VRFade.IsBlack || !h.Tracked || h.TriggerBtn || DiceHand.Holder == h) return false;   // the trigger is the laser's
            if (TableCardGrab.Holds(h)) return false;   // a picked-up card moves with this hand: it touches nothing
            if (FigurineGrab.Holds(h)) return false;   // so does the hero's figurine (2026-10-17: carrying it over the next place, the
                                                        // fingertip selected that tile itself and the figurine went back)
            if (Minigames.DiceCatapult) return false;   // waiting dice are for the grip (DiceHand)
            MenuManager mm = MenuManager.Instance;
            return mm == null || !mm.Paused;
        }

        /// Surface frame of a collider at the fingertip: depth along the normal (toward the eye) and
        /// whether the tip is over the face.
        static bool Surface(Collider c, Vector3 tip, Vector3 eye, float margin, out float depth, out Vector3 n)
        {
            FeelSurface fs;
            return Surface(c, tip, eye, margin, out depth, out n, out fs);
        }

        static bool Surface(Collider c, Vector3 tip, Vector3 eye, float margin, out float depth, out Vector3 n, out FeelSurface fs)
        {
            depth = 0f;
            n = Vector3.up;
            fs = default(FeelSurface);
            BoxCollider b = c as BoxCollider;
            CapsuleCollider cap = b == null && PoleTops ? c as CapsuleCollider : null;
            if (cap != null && c.GetComponentInParent<ChallengeMapLocation>() == null) cap = null;
            if (b == null && cap == null)
            {
                Vector3 cp = c.ClosestPoint(tip);
                Vector3 d = tip - cp;
                float dist = d.magnitude;
                n = dist > 1e-5f ? d / dist : (eye - c.bounds.center).normalized;
                depth = dist;
                // no face to pull toward: the surface is the closest point (no assist, the stop still works)
                fs.Centre = cp;
                fs.N = n;
                fs.U = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.forward).normalized;
                fs.V = Vector3.Cross(fs.U, n);
                return true;
            }
            // a capsule (the challenge map's pins) as the box round it: from its closest point the depth never went below zero, so
            // a finger could hover a pin but never press it (P2-8)
            Transform t = c.transform;
            Vector3 ls = t.lossyScale;
            Vector3 bs = b != null ? b.size : CapsuleBox(cap);
            Vector3 size = new Vector3(Mathf.Abs(bs.x * ls.x), Mathf.Abs(bs.y * ls.y), Mathf.Abs(bs.z * ls.z));
            Vector3 center = t.TransformPoint(b != null ? b.center : cap.center);
            Vector3 ax0 = t.right, ax1 = t.up, ax2 = t.forward;
            int k = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
            Vector3 rel = tip - center;
            float lo = Mathf.Min(size.x, Mathf.Min(size.y, size.z)), hi = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            int kp;
            if (CubeFaces && lo > 1e-5f && hi < lo * 1.25f)
            {
                // a cube (a die): no thin side to press - the face the finger is beyond the most is the one it presses (the second
                // playtest, P2-3, mark 14: the first axis was taken, a side face on most dice, so only a die lying with that side up
                // could be tapped - one die of three took every tap, the others were "lost")
                float best = float.MinValue;
                for (int i = 0; i < 3; i++)
                {
                    Vector3 ax = i == 0 ? ax0 : i == 1 ? ax1 : ax2;
                    float out_ = Mathf.Abs(Vector3.Dot(rel, ax)) / (size[i] * 0.5f);
                    if (out_ > best) { best = out_; k = i; }
                }
                n = k == 0 ? ax0 : k == 1 ? ax1 : ax2;
                if (Vector3.Dot(n, rel) < 0f) n = -n;
            }
            else if (cap != null && Pole(size, ax0, ax1, ax2, out kp))
            {
                k = kp;
                // a pin standing up (the challenge map's: a capsule 2.7 cm across, 10.5 cm tall): pressed on its top, from above,
                // as the player reaches down to the map (not on its thinnest axis, a side face toward the eyes)
                n = k == 0 ? ax0 : k == 1 ? ax1 : ax2;
                if (n.y < 0f) n = -n;
            }
            else
            {
                n = k == 0 ? ax0 : k == 1 ? ax1 : ax2;
                if (Vector3.Dot(n, eye - center) < 0f) n = -n;
            }
            depth = Vector3.Dot(rel, n) - size[k] * 0.5f;
            fs.Centre = center + n * (size[k] * 0.5f);
            fs.N = n;
            int iu = k == 0 ? 1 : 0, iv = k == 2 ? 1 : 2;
            fs.U = iu == 0 ? ax0 : ax1;
            fs.V = iv == 1 ? ax1 : ax2;
            fs.Half = new Vector2(size[iu] * 0.5f, size[iv] * 0.5f);
            for (int i = 0; i < 3; i++)
            {
                if (i == k) continue;
                Vector3 ax = i == 0 ? ax0 : i == 1 ? ax1 : ax2;
                if (Mathf.Abs(Vector3.Dot(rel, ax)) > size[i] * 0.5f + margin) return false;
            }
            return true;
        }

        /// A capsule's box (local size): its height along its direction, its diameter across.
        static Vector3 CapsuleBox(CapsuleCollider cap)
        {
            float d = cap.radius * 2f, h = Mathf.Max(cap.height, d);
            return cap.direction == 0 ? new Vector3(h, d, d) : cap.direction == 1 ? new Vector3(d, h, d) : new Vector3(d, d, h);
        }

        /// (P2-8) a challenge map pin (a capsule) is pressed on its top (false = as before: never pressed; for A/B tests)
        public static bool PoleTops = true;

        /// A box much longer along one axis than across (twice its larger cross size) whose long axis stands within 30 degrees
        /// of vertical: `k` = that axis.
        static bool Pole(Vector3 size, Vector3 ax0, Vector3 ax1, Vector3 ax2, out int k)
        {
            k = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            float across = Mathf.Max(k == 0 ? 0f : size.x, Mathf.Max(k == 1 ? 0f : size.y, k == 2 ? 0f : size.z));
            if (across <= 1e-5f || size[k] < across * 2f) return false;
            Vector3 ax = k == 0 ? ax0 : k == 1 ? ax1 : ax2;
            return Mathf.Abs(ax.normalized.y) > 0.866f;
        }

        /// Inside the face (+ margin), sideways.
        static bool InFoot(FeelSurface fs, Vector3 tip, float margin)
        {
            if (fs.Half.x <= 0f || fs.Half.y <= 0f) return true;
            Vector3 rel = tip - fs.Centre;
            return Mathf.Abs(Vector3.Dot(rel, fs.U)) <= fs.Half.x + margin && Mathf.Abs(Vector3.Dot(rel, fs.V)) <= fs.Half.y + margin;
        }

        static readonly Dictionary<int, PressFamily> s_fams = new Dictionary<int, PressFamily>();

        /// Map tiles (and what lies on them), dice, the wheel's cards, other cards (docs/hand-feel-design.md §3.4 table).
        static PressFamily FamilyOf(UISelectable sel, Collider c, bool wheel)
        {
            if (wheel) return PressFamily.Wheel;
            Transform t = sel != null ? sel.transform : c.transform;
            int id = t.GetInstanceID();
            PressFamily f;
            if (s_fams.TryGetValue(id, out f)) return f;
            f = PressFamily.Card;
            for (Transform x = t; x != null; x = x.parent)
            {
                if (x.name == "MapLayout") { f = PressFamily.Tile; break; }
                if (x.name == "DiceGame") { f = PressFamily.Die; break; }
            }
            if (s_fams.Count > 512) s_fams.Clear();
            s_fams[id] = f;
            return f;
        }

        static bool Best(Finger f, Vector3 tip, out Cand best) { Cand near; bool hasNear; return Best(f, tip, out best, out near, out hasNear); }

        /// `best` = the target under the finger for hover and press; `near` = the nearest one ahead within 12 cm (the
        /// glove pre-shapes, the assist and the shadow refer to it).
        static bool Best(Finger f, Vector3 tip, out Cand best, out Cand near, out bool hasNear)
        {
            best = default(Cand);
            near = default(Cand);
            hasNear = false;
            float s = VRRig.I.Scale;
            Vector3 eye = VRRig.I.HeadWorldPos;
            int n = Physics.OverlapSphereNonAlloc(tip, NearRadius * s, s_hits, PickMask(), QueryTriggerInteraction.Collide);
            // while the wheel takes its stop, any of its cards means "stop" (the game's own stop selectable)
            UISelectable wheelStop;
            Transform wheel = TableStops.Armed(out wheelStop) == "wheel" && DeckManager.Instance != null && DeckManager.Instance.WheelSpinContainer != null
                ? DeckManager.Instance.WheelSpinContainer.transform : null;
            s_groups.Clear();
            for (int i = 0; i < n; i++)
            {
                Collider c = s_hits[i];
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                bool blocked;
                UISelectable sel;
                bool isWheel = wheel != null && c.transform.IsChildOf(wheel);
                if (isWheel) { sel = wheelStop; blocked = false; }
                else sel = VRPointer.PickTarget(c.gameObject, out blocked);
                if (sel == null || !sel.IsValid || !(sel.HasSelectAction || blocked)) continue;
                float depth;
                Vector3 nrm;
                FeelSurface fs;
                PressFamily fam = FamilyOf(sel, c, isWheel);
                if (!Surface(c, tip, eye, Mathf.Max(FootMargin * s, HandFeel.Capture(fam)) + 0.02f * s, out depth, out nrm, out fs)) continue;
                if (depth > 0.12f * s || depth < -Below * s) continue;
                fs.Family = fam;
                var cand = new Cand { Col = c, Sel = sel, Blocked = blocked, Depth = depth, N = nrm, Wheel = isWheel, Surf = fs, Fam = fam };
                if (!hasNear || depth < near.Depth) { near = cand; hasNear = true; }
                // a hovered target is kept until the finger is StayMargin further out (no flicker on an edge)
                float foot = FootMargin + (f.St != State.Idle && f.HasC && sel == f.C.Sel ? StayMargin : 0f);
                if (!InFoot(fs, tip, foot * s) || depth > HoverDepth * s * 1.5f) continue;
                // one target can have several colliders (a map tile's box and the card lying in it): its
                // surface is the highest one, i.e. the smallest depth
                Cand cur;
                if (s_groups.TryGetValue(sel, out cur) && cur.Depth <= depth) continue;
                s_groups[sel] = cand;
            }
            bool found = false;
            float bestAbs = float.MaxValue;
            foreach (Cand g in s_groups.Values)
            {
                float abs = Mathf.Abs(g.Depth);
                // keep the current target unless another is clearly nearer (tiles are 1.3 cm apart); a finger
                // pushed into it keeps it whatever is beside it (dice in the result row are 3 cm apart)
                if (f.HasC && g.Sel == f.C.Sel) abs = g.Depth <= 0f ? -1f : abs - Hysteresis * s;
                if (abs >= bestAbs) continue;
                bestAbs = abs;
                best = g;
                found = true;
            }
            // remember the target the finger is above (>= 1.5 cm): hover needs a finger that came down onto it
            if (found && best.Depth > AboveDepth * s) { f.AboveSel = best.Sel; f.AboveT = Time.unscaledTime; }
            return found;
        }

        static readonly Dictionary<UISelectable, Cand> s_groups = new Dictionary<UISelectable, Cand>();

        /// Came down onto it: seen at least 1.5 cm above this target within the last 0.6 s (a card that
        /// arrives under a resting finger, a finger rising from under the table or sliding in from the
        /// side does not hover). The spinning wheel's cards sweep into the finger, so they need no approach.
        static bool FromAbove(Finger f, Cand c) => c.Wheel || (f.AboveSel == c.Sel && Time.unscaledTime - f.AboveT < FromAboveWindow);

        void Update()
        {
            Step(VRInput.R);
            Step(VRInput.L);
        }

        static void Step(Hand h)
        {
            Finger f = F(h);
            if (!Allowed(h, f))
            {
                if (f.St != State.Idle) Leave(f, "not allowed");
                f.HasPrev = false;
                ShowDot(f, false);
                return;
            }
            float s = VRRig.I.Scale, now = Time.unscaledTime;
            Vector3 tip = f.Override ? f.OverrideTip : HandFeel.Tip(h);
            float dt = Mathf.Max(1e-3f, Time.unscaledDeltaTime);
            Vector3 vel = f.HasPrev ? (tip - f.PrevTip) / dt : Vector3.zero;
            if (!f.HasPrev) f.Path.Clear();
            f.PrevTip = tip;
            f.HasPrev = true;
            f.Tip = tip;
            f.Path.Add(now, tip);
            Cand c, nc;
            bool hasNear;
            bool has = Best(f, tip, out c, out nc, out hasNear);
            f.Near = has || hasNear;
            f.NearFrame = Time.frameCount;
            if (!f.Override)
            {
                // the target held by this finger, else the nearest ahead: the pre-shaping glove, the assist and the shadow
                if (f.St != State.Idle && has && c.Sel == f.C.Sel) HandFeel.Offer(h, c.Surf, c.Depth, c.Depth <= 0f);
                else if (hasNear) HandFeel.Offer(h, nc.Surf, nc.Depth, false);
            }

            // moving away from the surface (rising from under the table, pulling back) never starts a hover
            bool receding = has && Vector3.Dot(vel, c.N) / s > 0.05f;
            if (has) f.Record(now, c.Depth);   // also before the hover: a fast poke has its run-up on record
            // hover starts and, for a fast poke that arrives already past the click point, the press comes in the same frame
            bool canEnter = has && c.Depth < HoverDepth * s && FromAbove(f, c) && !receding;
            if (f.St == State.Drag)
            {
                // the row follows the finger until it lifts off (or goes far below the row); then it coasts on
                bool off = !has || c.Depth > LeaveDepth * s * 2f;
                if (off) { EndDrag(f, h); ShowDot(f, false); return; }
                DragTick(f, h, tip, now);
                ShowDot(f, true);
                return;
            }
            if (f.St == State.Idle) { if (canEnter) Enter(f, h, c); }
            else if (f.St == State.Hover && (!has || c.Sel != f.C.Sel || c.Depth > LeaveDepth * s))
            {
                Leave(f, !has ? "lost it" : c.Sel != f.C.Sel ? "other target" : "pulled out");
                if (canEnter) Enter(f, h, c);
            }
            switch (f.St)
            {
                case State.Hover:
                    f.C = c;
                    if (StartDrag(f, h, c, tip, now)) break;
                    float approach = Vector3.Dot(vel, -c.N) / s;
                    float tangential = (vel - c.N * Vector3.Dot(vel, c.N)).magnitude / s;
                    f.MinDepth = Mathf.Min(f.MinDepth, c.Depth / s);
                    f.MaxApproach = Mathf.Max(f.MaxApproach, approach);
                    f.MaxSide = Mathf.Max(f.MaxSide, tangential);
                    if (approach >= MinApproach) f.ApproachT = now;   // a poke that just stopped still counts
                    if (c.Wheel && c.Depth <= 0f && now - s_lastConfirm > Refractory)
                    {
                        // the spinning cards move under the finger: touching any of them stops the wheel
                        Confirm(f, h);
                        f.St = State.Latched;
                        break;
                    }
                    if (c.Depth <= 0f) Touch(f, h, c);
                    // a tile the hero can't reach is refused as a pressing finger meets it (a finger brushing across it
                    // is not a press: the same motion rules)
                    float clickAt = c.Blocked ? 0f : c.Fam.ClickAt;
                    if (now - f.HoverSince >= Dwell && c.Depth <= -clickAt * s && now - s_lastConfirm > Refractory)
                    {
                        float inward = f.InwardSpeed(now, c.Depth, ApproachWindow) / s;
                        float inW, sideW;
                        bool moved = f.Path.Speeds(now, tip, c.N, s, ApproachWindow, out inW, out sideW);
                        if (inward >= MinApproach && now - f.ApproachT < 0.15f && moved && HandFeel.DirectionOk(Mathf.Max(inward, inW), sideW))
                        {
                            if (Scrolls(c)) { f.St = State.Pending; Pendings++; PressFeel.Hover(h, tip, c.Fam); break; }
                            Confirm(f, h);
                            f.St = State.Latched;
                        }
                        else if (c.Depth < -0.03f * s)
                        {
                            // pushed through without a proper press (swept across, crept in): no press this contact
                            Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "touch: {0} through {1} without a press (approach {2:0.00} m/s, sideways {3:0.00} m/s)",
                                h.IsLeft ? "L" : "R", Name(c.Sel), approach, tangential));
                            f.St = State.Latched;
                        }
                    }
                    break;
                case State.Pending:
                    // a slide along the row first: a swipe, nothing selected; lifted off (or out of it) without sliding: the press
                    if (has && c.Sel == f.C.Sel && StartDrag(f, h, c, tip, now)) { PendingSwipes++; break; }
                    if (!has || c.Sel != f.C.Sel || c.Depth > RearmDepth * s)
                    {
                        if (now - s_lastConfirm > Refractory) { PendingSelects++; Confirm(f, h); }
                        Leave(f, "lifted: the press");
                        if (canEnter) Enter(f, h, c);
                    }
                    else
                    {
                        f.C = c;
                        if (c.Depth <= 0f) Touch(f, h, c);
                    }
                    break;
                case State.Latched:
                    if (!has || c.Sel != f.C.Sel || c.Depth > RearmDepth * s) Leave(f, "re-armed");
                    else
                    {
                        f.C = c;
                        if (c.Depth <= 0f) Touch(f, h, c);
                    }
                    break;
            }
            ShowDot(f, f.St != State.Idle);
        }

        static string Name(UISelectable sel) => sel != null ? VRUI.Path(sel.transform) : "-";

        // ---- swipe to scroll ----------------------------------------------------------------------------------------------
        public static float DragStart = 0.022f, Coast = 3.2f;   // real metres along the row before a touch becomes a swipe; m/s^2 of slowing
        public static int Drags, DragSteps, CoastSteps, Pendings, PendingSelects, PendingSwipes;
        /// (P2-3) a cube's pressed face is the one the finger is beyond (false = the old first-axis face; for A/B tests)
        public static bool CubeFaces = true;

        /// A card of a row that scrolls (its group has the mouse wheel's scroll action): pressed only when lifted without a slide.
        static bool Scrolls(Cand c) => Settings.SwipeScroll && c.Sel != null && !c.Wheel && !c.Blocked && c.Sel.ScrollAction != null;

        /// A finger over a card of a row that scrolls (its group has a scroll action, as the mouse wheel uses) that moves
        /// along the row by DragStart before it presses: no press this contact - a swipe.
        static bool StartDrag(Finger f, Hand h, Cand c, Vector3 tip, float now)
        {
            if (!Settings.SwipeScroll || c.Sel == null || c.Wheel || c.Blocked) return false;
            System.Action<UISelectable, float> scroll = c.Sel.ScrollAction;
            if (scroll == null) return false;
            float s = VRRig.I.Scale;
            Vector3 axis = c.Sel.transform.right;
            axis -= c.N * Vector3.Dot(axis, c.N);
            if (axis.sqrMagnitude < 1e-6f) return false;
            axis.Normalize();
            float along = Vector3.Dot(tip - f.DragFrom, axis) / s;
            if (Mathf.Abs(along) < DragStart) return false;
            f.St = State.Drag;
            f.DragAxis = axis;
            f.DragSel = c.Sel;
            f.DragScroll = scroll;
            f.DragAccum = 0f;
            f.DragFrom = tip;
            f.DragV = 0f;
            f.DragLastT = now;
            // one step = a card's width along the row (its collider), at least 4 cm
            float w = c.Col != null ? Mathf.Abs(Vector3.Dot(c.Col.bounds.size, new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z)))) / s : 0.06f;
            f.DragStep = Mathf.Clamp(w * 0.85f, 0.04f, 0.14f);
            Drags++;
            Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "touch: {0} swipes along {1} (a step every {2:0.0} cm): no press this contact",
                h.IsLeft ? "L" : "R", Name(c.Sel), f.DragStep * 100f));
            PressFeel.Hover(h, tip, c.Fam);
            DragTick(f, h, tip, now, along);
            return true;
        }

        static void DragTick(Finger f, Hand h, Vector3 tip, float now, float extra = 0f)
        {
            float s = VRRig.I.Scale;
            float d = Vector3.Dot(tip - f.DragFrom, f.DragAxis) / s + extra;
            f.DragFrom = tip;
            float dt = Mathf.Max(1e-3f, now - f.DragLastT);
            f.DragLastT = now;
            f.DragV = Mathf.Lerp(f.DragV, d / dt, 0.35f);
            f.DragAccum += d;
            Steps(f, h, false);
        }

        /// The row moves with the finger: a step each card's width (finger to the left = the cards to the left: the next
        /// ones come in from the right, as a phone's list).
        static void Steps(Finger f, Hand h, bool coasting)
        {
            while (Mathf.Abs(f.DragAccum) >= f.DragStep)
            {
                float dir = Mathf.Sign(f.DragAccum);
                f.DragAccum -= dir * f.DragStep;
                try { if (f.DragSel != null) f.DragScroll(f.DragSel, dir > 0f ? 1f : -1f); }
                catch (System.Exception e) { Plugin.L.LogWarning("touch: scroll: " + e.Message); }
                if (coasting) CoastSteps++; else DragSteps++;
                Haptics.Event("scroll", h.IsLeft, 0.25f, 0.004f);
            }
        }

        /// The finger lifted: the row coasts on with the swipe's speed, slowing down (momentum).
        static void EndDrag(Finger f, Hand h)
        {
            f.St = State.Idle;
            f.HasC = false;
            if (Mathf.Abs(f.DragV) > 0.25f && I != null) I.StartCoroutine(CoastOn(f, h, f.DragV));
            Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "touch: {0} swipe ended ({1:0.00} m/s)", h.IsLeft ? "L" : "R", f.DragV));
        }

        static System.Collections.IEnumerator CoastOn(Finger f, Hand h, float v)
        {
            while (Mathf.Abs(v) > 0.12f && f.St == State.Idle)
            {
                float dt = Mathf.Min(0.05f, Time.unscaledDeltaTime);
                f.DragAccum += v * dt;
                Steps(f, h, true);
                v = Mathf.MoveTowards(v, 0f, Coast * dt);
                yield return null;
            }
            f.DragAccum = 0f;
        }

        static void Enter(Finger f, Hand h, Cand c)
        {
            f.MinDepth = float.MaxValue;
            f.MaxApproach = f.MaxSide = 0f;
            f.St = State.Hover;
            f.C = c;
            f.HasC = true;
            f.HoverSince = Time.unscaledTime;
            f.DragFrom = f.Tip;
            Hovers++;
            VRPointer.Active = false;   // the laser leaves the game's hover to the finger
            f.Touched = false;
            bool hovered = !c.Blocked && UISelection.TryHover(c.Sel);
            if (hovered) PressFeel.Hover(h, f.Tip, c.Fam);
            Plugin.L.LogInfo("touch: " + (h.IsLeft ? "L" : "R") + " over " + Name(c.Sel) + (c.Blocked ? " (blocked)" : hovered ? "" : " (hover refused)"));
        }

        /// The finger is on the target's face: a contact cue once (unless the click follows within 30 ms), and the drawn
        /// finger stops on it (not on the wheel's moving cards).
        static void Touch(Finger f, Hand h, Cand c)
        {
            if (!f.Touched)
            {
                f.Touched = true;
                if (!c.Blocked && !c.Wheel) PressFeel.Contact(h, f.Tip, c.Fam);
            }
            if (!f.Override && c.Fam.Stops && !c.Wheel) HandFeel.Contact(h, c.N, -c.Depth);
        }

        static void Leave(Finger f, string why)
        {
            if (f.St == State.Hover && f.MinDepth < 0f)
                Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "touch: {0} left {1} without a press ({2}): deepest {3:0.0} cm, {4:0} ms, approach up to {5:0.00} m/s, sideways up to {6:0.00} m/s",
                    f.Left ? "L" : "R", Name(f.C.Sel), why, f.MinDepth * 100f, (Time.unscaledTime - f.HoverSince) * 1000f, f.MaxApproach, f.MaxSide));
            f.St = State.Idle;
            f.HasC = false;
        }

        /// The game's own select on what the finger pressed (no input buffer, nothing else selected).
        static void Confirm(Finger f, Hand h)
        {
            s_lastConfirm = Time.unscaledTime;
            UISelectable t = f.C.Sel;
            string why = null;
            if (f.C.Blocked && (t == null || !t.HasSelectAction || !t.IsSelectable)) why = "blocked";
            else if (t == null || !t.IsValid) why = "not valid";
            else if (!UISelection.TryHover(t)) why = "hover refused";
            else if (UISelectable.GetTargetSelectable(t.Selection.HoveredObject) != t) why = "hover went elsewhere";
            else if (!UISelection.TrySelect(t)) why = "select refused";
            if (why == null)
            {
                Selects++;
                Last = "select " + Name(t);
                PressFeel.Click(h, f.Tip, f.C.Fam ?? PressFamily.Card);
            }
            else
            {
                Refusals++;
                Last = why + " " + Name(t);
                PressFeel.Refuse(h, f.Tip);
            }
            float sc = VRRig.I != null ? VRRig.I.Scale : 1f;
            Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "touch: {0} pressed {1} -> {2} ({3}, fingertip {4:0.0} mm in)",
                h.IsLeft ? "L" : "R", Name(t), why ?? "selected", f.C.Fam != null ? f.C.Fam.Name : "-", -f.C.Depth / sc * 1000f));
        }

        static void ShowDot(Finger f, bool on)
        {
            if (f.Dot == null)
            {
                if (!on) return;
                GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                UnityEngine.Object.Destroy(dot.GetComponent<Collider>());
                dot.name = "HoF2VR_TouchDot" + (f.Left ? "L" : "R");
                dot.layer = VRUI.Layer;
                UnityEngine.Object.DontDestroyOnLoad(dot);
                if (s_dotMat == null) s_dotMat = new Material(Shader.Find("Unlit/Color")) { color = new Color(1f, 0.82f, 0.35f), renderQueue = 5120 };
                dot.GetComponent<MeshRenderer>().sharedMaterial = s_dotMat;
                f.Dot = dot.transform;
            }
            if (f.Dot.gameObject.activeSelf != on) f.Dot.gameObject.SetActive(on);
            if (!on) return;
            float s = VRRig.I != null ? VRRig.I.Scale : 1f;
            // on the surface under the finger (a contact point), shrinking as the finger presses in
            Vector3 p = f.Tip - f.C.N * f.C.Depth;
            f.Dot.position = p + f.C.N * (0.002f * s);
            f.Dot.localScale = Vector3.one * (f.St == State.Latched ? 0.012f : 0.008f) * s;
        }

        static string Describe()
        {
            return "L:" + One(s_l) + " | R:" + One(s_r) + " | hovers " + Hovers + " selects " + Selects + " refused " + Refusals + " | swipes " + Drags + " steps " + DragSteps + " coast " + CoastSteps + " | row presses held " + Pendings + " (selected on lift " + PendingSelects + ", became swipes " + PendingSwipes + ") | last " + Last;
        }

        static string One(Finger f)
        {
            if (f.St == State.Idle) return "idle";
            float s = VRRig.I != null ? VRRig.I.Scale : 1f;
            return f.St + " " + (f.C.Sel != null ? f.C.Sel.name : "-") + string.Format(CultureInfo.InvariantCulture, " d={0:0.0}cm", f.C.Depth / s * 100f);
        }

        // ---- test seam -------------------------------------------------------------------------------
        //   touch <name> [l|r] [tap|hover|swipe|slow|graze|hold]
        // drives a synthetic fingertip onto the collider whose path contains <name> (as `world click`
        // matches), through the real state machine; the outcome is logged ("touch: ... pressed ... ->")
        // and counted in the `touch` status.
        /// `touchpose <name> [cm] [l|r]`: the world point `cm` (default 6) in front of that target's face,
        /// and the xrsim move that pushes a fingertip from there 1.5 cm through the face.
        static string TouchPose(string[] a)
        {
            float cm = 6f;
            Hand h = VRInput.R;
            var key = new List<string>();
            for (int i = 1; i < a.Length; i++)
            {
                float v;
                if (a[i] == "l" || a[i] == "r") h = a[i] == "l" ? VRInput.L : VRInput.R;
                else if (float.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) cm = v;
                else key.Add(a[i]);
            }
            Collider c = Find(string.Join(" ", key.ToArray()));
            if (c == null || VRRig.I == null) return "none";
            Vector3 n, face;
            FaceOf(c, out face, out n);
            float s = VRRig.I.Scale;
            Vector3 p = face + n * (cm * 0.01f * s);
            Vector3 d = VRRig.I.FromWorldDir(-n * ((cm + 1.5f) * 0.01f * s)) / s;
            return string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####} {2:0.####} | move {3} {4:0.####} {5:0.####} {6:0.####}",
                p.x, p.y, p.z, h.IsLeft ? "l" : "r", d.x, d.y, -d.z);
        }

        static Collider Find(string name)
        {
            int mask = PickMask();
            Collider best = null;
            foreach (Collider c in FindObjectsOfType<Collider>())
            {
                if (!c.enabled || !c.gameObject.activeInHierarchy || ((1 << c.gameObject.layer) & mask) == 0) continue;
                bool blocked;
                if (VRPointer.PickTarget(c.gameObject, out blocked) == null) continue;
                string path = VRUI.Path(c.transform);
                if (path.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (best == null || path.Length < VRUI.Path(best.transform).Length) best = c;
            }
            return best;
        }

        /// The centre of a collider's face toward the player, and its normal.
        static void FaceOf(Collider c, out Vector3 face, out Vector3 n)
        {
            float s = VRRig.I.Scale;
            Vector3 center = c.bounds.center, probe = center + Vector3.up * 0.05f * s;
            float depth;
            Surface(c, probe, VRRig.I.HeadWorldPos, 0f, out depth, out n);
            face = center + n * (Vector3.Dot(probe - center, n) - depth);
        }

        static string Command(string[] a)
        {
            if (a.Length < 2) return Describe();
            string mode = "tap";
            Hand h = VRInput.R;
            var key = new List<string>();
            for (int i = 1; i < a.Length; i++)
            {
                string w = a[i].ToLowerInvariant();
                if (w == "l" || w == "r") h = w == "l" ? VRInput.L : VRInput.R;
                else if (w == "tap" || w == "hover" || w == "swipe" || w == "slow" || w == "graze" || w == "hold") mode = w;
                else key.Add(a[i]);
            }
            string name = string.Join(" ", key.ToArray());
            if (VRRig.I == null) return "no rig";
            Collider best = Find(name);
            if (best == null) return "no touchable collider matching '" + name + "'";
            I.StartCoroutine(Drive(F(h), best, mode));
            return mode + " " + VRUI.Path(best.transform);
        }

        static IEnumerator Drive(Finger f, Collider c, string mode)
        {
            float s = VRRig.I.Scale;
            Vector3 n, face;
            FaceOf(c, out face, out n);
            Vector3 side = Vector3.Cross(n, Vector3.Cross(Vector3.up, n).sqrMagnitude > 1e-4f ? Vector3.up : Vector3.forward).normalized;
            Vector3 along = Vector3.Cross(n, side).normalized;
            f.Override = true;
            int sel0 = Selects, ref0 = Refusals;
            IEnumerator Move(Vector3 from, Vector3 to, float seconds)
            {
                float t0 = Time.unscaledTime;
                while (true)
                {
                    float k = seconds <= 0f ? 1f : Mathf.Clamp01((Time.unscaledTime - t0) / seconds);
                    f.OverrideTip = Vector3.Lerp(from, to, k);
                    yield return null;
                    if (k >= 1f) yield break;
                }
            }
            IEnumerator Hold(Vector3 at, float seconds)
            {
                float t0 = Time.unscaledTime;
                f.OverrideTip = at;
                while (Time.unscaledTime - t0 < seconds) yield return null;
            }
            Vector3 P(float cm) => face + n * (cm * 0.01f * s);
            switch (mode)
            {
                case "tap":
                    yield return I.StartCoroutine(Hold(P(6f), 0.1f));
                    yield return I.StartCoroutine(Move(P(6f), P(2f), 0.12f));
                    yield return I.StartCoroutine(Hold(P(2f), 0.15f));
                    yield return I.StartCoroutine(Move(P(2f), P(-1.5f), 0.08f));
                    yield return I.StartCoroutine(Hold(P(-1.5f), 0.05f));
                    yield return I.StartCoroutine(Move(P(-1.5f), P(6f), 0.12f));
                    break;
                case "hold":
                    // into the card and kept there (a hand held into the spinning wheel's path)
                    yield return I.StartCoroutine(Hold(P(6f), 0.1f));
                    yield return I.StartCoroutine(Move(P(6f), P(-1.5f), 0.25f));
                    yield return I.StartCoroutine(Hold(P(-1.5f), 1.5f));
                    yield return I.StartCoroutine(Move(P(-1.5f), P(6f), 0.15f));
                    break;
                case "hover":
                    yield return I.StartCoroutine(Hold(P(6f), 0.1f));
                    yield return I.StartCoroutine(Move(P(6f), P(2f), 0.12f));
                    yield return I.StartCoroutine(Hold(P(2f), 0.5f));
                    yield return I.StartCoroutine(Move(P(2f), P(6f), 0.12f));
                    break;
                case "slow":
                    yield return I.StartCoroutine(Hold(P(6f), 0.1f));
                    yield return I.StartCoroutine(Move(P(6f), P(2f), 0.12f));
                    yield return I.StartCoroutine(Hold(P(2f), 0.15f));
                    yield return I.StartCoroutine(Move(P(2f), P(-1.5f), 1.75f));   // 0.02 m/s
                    yield return I.StartCoroutine(Move(P(-1.5f), P(6f), 0.12f));
                    break;
                case "swipe":
                    {
                        // in from the side, mostly sideways (a swipe, not a press on the tile beside it)
                        float half = Mathf.Max(c.bounds.extents.x, c.bounds.extents.z) + 0.04f * s;
                        Vector3 a0 = P(2f) - along * (half + 0.06f * s), a1 = P(-0.5f) - along * half, b1 = P(-0.5f) + along * half;
                        yield return I.StartCoroutine(Hold(a0, 0.1f));
                        yield return I.StartCoroutine(Move(a0, a1, 0.1f));
                        yield return I.StartCoroutine(Move(a1, b1, (2f * half / s) / 1.0f));   // 1 m/s across
                        yield return I.StartCoroutine(Move(b1, b1 + n * 0.06f * s, 0.12f));
                    }
                    break;
                case "graze":
                    // the finger is already in the card when it arrives (no approach, no motion)
                    yield return I.StartCoroutine(Hold(P(-0.5f), 0.4f));
                    yield return I.StartCoroutine(Move(P(-0.5f), P(6f), 0.3f));
                    break;
            }
            yield return I.StartCoroutine(Hold(P(8f), 0.1f));
            f.Override = false;
            string outcome = Selects > sel0 ? "selected" : Refusals > ref0 ? "refused" : "none";
            Plugin.L.LogInfo("touch: " + mode + " " + VRUI.Path(c.transform) + " -> " + outcome);
            Last = mode + " -> " + outcome;
        }
    }
}

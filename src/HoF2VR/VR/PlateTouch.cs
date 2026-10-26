using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace HoF2VR
{
    /// A flat button that travels when pressed: its transform is the pressed face (rest face + Press along T.forward, away
    /// from the player); Size is world metres.
    internal class PressPlate
    {
        public Transform T;
        public Vector2 Size;
        public bool On, Hovered, Disabled;
        public float Press, PressV;      // travel now (world) and its spring's speed
        public float Glow;               // 0..1, the nearest hand's proximity (1 - d / 6 cm)^2
        public int DriveFrame = -10;     // a finger set Press this frame
        public Vector3 RestFace => T.position - T.forward * Press;
    }

    /// The press state machine for plates (docs/hand-feel-design.md §3.4), one per board:
    ///  Idle -> Hover: the interaction fingertip within 3 cm in front of a plate, having been 1.5 cm or more in front of
    ///    THIS plate within the last 1.5 s, not moving away: the game's hover / the board's, a tick.
    ///  Contact: the fingertip reaches the face: the plate travels with it (Travel 6 mm), the drawn finger rides the face
    ///    and is held at the bottom (HandFeel.Contact); a touch pulse + tap unless the click follows within 30 ms.
    ///  Click: travel past ClickAt (3.5 mm), moving in (within 60 degrees of straight in), 0.3 s after the last press: the
    ///    board's action; refused (disabled, not now) -> two knocks, the plate stays up. Disabled plates don't travel: the
    ///    finger stops on the face and a refusal comes at contact.
    ///  Release: after a click, travel back under ReleaseAt (1.5 mm): a release tick + a higher click.
    ///  Re-arm: the fingertip 1 cm in front again. One click per contact. Plates nobody drives spring back (~40 ms).
    internal sealed class PlateTouch
    {
        /// When a finger last pressed any plate (unscaled time): a place change right after it recentres on where the player stood
        /// before reaching out (VRRig.Recenter, the third playtest, mark 20).
        public static float LastPressAt = -100f;
        public static float HoverDepth = 0.03f, AboveDepth = 0.015f, RearmDepth = 0.01f, LeaveDepth = 0.04f, MinApproach = 0.05f,
                            MaxTangential = 0.5f, MaxAngleTan = 1.73f, FromAboveWindow = 1.5f, Refractory = 0.3f, Margin = 0.004f, Below = 0.08f;
        /// A hovered plate is kept until the fingertip is this much further outside it (hysteresis: a finger resting on an
        /// edge doesn't flicker between hover and nothing).
        public static float StayMargin = 0.006f;
        enum St { Idle, Hover, Latched }

        sealed class Finger
        {
            public St S;
            public int Plate = -1, AbovePlate = -1, NearFrame = -10;
            public float HoverSince, ApproachT = -10f, AboveT = -10f;
            public bool Touched, Clicked, ReleasedFb, Refused;
            public Vector3 Prev;
            public bool HasPrev, Override;
            public Vector3 OverrideTip;
            public readonly MotionWindow Path = new MotionWindow();
        }

        readonly Finger m_l = new Finger(), m_r = new Finger();
        float m_lastPress = -10f;
        public readonly string Tag;
        public PressFamily Family;
        public int Hovers, Selects, Refusals, Releases;
        public string Last = "-";
        public float LastDepthMm, LastSpeed, LastSide;
        /// The press: null = done, else the reason it was refused.
        public Func<int, Hand, string> OnPress;
        /// Hover started on plate i (the game's hover, for example): false = refused (no tick).
        public Func<int, Hand, bool> OnHover;
        public Func<int, string> Label = i => (i + 1).ToString();

        public PlateTouch(string tag, PressFamily family) { Tag = tag; Family = family; s_all.Add(this); }

        public static int Remaps;

        /// The board's plates were numbered again (a menu's list changed under the finger): each finger keeps the plate it is on
        /// by what it is - `map` gives its new number, -1 when it is gone. (The pause menu's Settings page: Back was plate 16 of 17
        /// while General's options showed; the finger's hover moved the game's hover to Back, the options closed and Back became
        /// plate 6 of 6 - the finger was on a plate that no longer existed, and the press never came, 2026-10-18.)
        public void Remap(Func<int, int> map)
        {
            foreach (Finger f in new[] { m_l, m_r })
            {
                if (f.Plate >= 0)
                {
                    int j = map(f.Plate);
                    if (j != f.Plate) Remaps++;
                    if (j < 0) { f.S = St.Idle; f.Touched = f.Clicked = f.Refused = false; }
                    f.Plate = j;
                }
                if (f.AbovePlate >= 0) f.AbovePlate = map(f.AbovePlate);
            }
        }

        // every board's plates as last stepped (the reach probe measures them; tests only - nothing reads this in play)
        static readonly List<PlateTouch> s_all = new List<PlateTouch>();
        Func<int, PressPlate> m_lastAt;
        int m_lastCount, m_lastFrame = -100;

        /// Each plate of every board stepped within the last few frames: (board tag, label, plate).
        public static void ForEachShown(Action<string, string, PressPlate> f)
        {
            foreach (PlateTouch t in s_all)
            {
                if (t.m_lastAt == null || Time.frameCount - t.m_lastFrame > 5) continue;
                for (int i = 0; i < t.m_lastCount; i++)
                {
                    PressPlate p = t.m_lastAt(i);
                    if (p == null || p.T == null || !p.T.gameObject.activeInHierarchy) continue;
                    string label;
                    try { label = t.Label(i); } catch (Exception) { label = (i + 1).ToString(); }
                    f(t.Tag, label, p);
                }
            }
        }

        Finger F(Hand h) => h.IsLeft ? m_l : m_r;
        public bool Touching(Hand h) => F(h).S != St.Idle;
        public bool Near(Hand h) => Time.frameCount - F(h).NearFrame <= 2;
        public int HoveredPlate(Hand h) => F(h).S != St.Idle ? F(h).Plate : -1;
        /// The plate this finger pressed (clicked) and still holds down, or -1 (menu sliders are dragged while held).
        public int LatchedPlate(Hand h) => F(h).S == St.Latched && F(h).Clicked ? F(h).Plate : -1;

        public void SetOverride(Hand h, bool on, Vector3 tip) { Finger f = F(h); f.Override = on; f.OverrideTip = tip; }
        public bool Overridden(Hand h) => F(h).Override;

        /// One hand, once per frame. `at(i)` = plate i (0..count-1), `allowed` = this hand may touch now.
        public void Step(Hand h, Func<int, PressPlate> at, int count, bool allowed)
        {
            if (count > 0) { m_lastAt = at; m_lastCount = count; m_lastFrame = Time.frameCount; }
            Finger f = F(h);
            if (!allowed || count == 0)
            {
                if (f.S != St.Idle) Leave(f, at, count);
                f.HasPrev = false;
                return;
            }
            float sc = VRRig.I.Scale, now = Time.unscaledTime;
            Vector3 tip = f.Override ? f.OverrideTip : HandFeel.Tip(h);
            Vector3 vel = f.HasPrev ? (tip - f.Prev) / Mathf.Max(1e-3f, Time.unscaledDeltaTime) : Vector3.zero;
            if (!f.HasPrev) f.Path.Clear();
            f.Prev = tip;
            f.HasPrev = true;
            f.Path.Add(now, tip);
            float capture = HandFeel.Capture(Family);
            // the plate under the finger (depth > 0 = in front of its rest face) and the nearest one ahead (for the glow, the
            // pre-shaping hand and the assist)
            int best = -1, near = -1;
            float bestDepth = 0f, bestAbs = float.MaxValue, nearDepth = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                PressPlate q = at(i);
                if (q == null || !q.On || q.T == null) continue;
                Vector3 face = q.RestFace, nrm = -q.T.forward;
                float depth = Vector3.Dot(tip - face, nrm);
                Vector3 rel = tip - face;
                float u = Mathf.Abs(Vector3.Dot(rel, q.T.right)), v = Mathf.Abs(Vector3.Dot(rel, q.T.up));
                float hx = q.Size.x * 0.5f, hy = q.Size.y * 0.5f;
                if (depth < -Below * sc || depth > 0.12f * sc) continue;
                if (u <= hx + capture + 0.02f * sc && v <= hy + capture + 0.02f * sc && depth < nearDepth) { near = i; nearDepth = depth; }
                float m = (Margin + (f.S != St.Idle && i == f.Plate ? StayMargin : 0f)) * sc;
                if (u > hx + m || v > hy + m || depth > 0.06f * sc) continue;
                float abs = Mathf.Abs(depth) - (i == f.Plate ? 0.006f * sc : 0f);
                if (abs >= bestAbs) continue;
                bestAbs = abs;
                best = i;
                bestDepth = depth;
            }
            if (best >= 0 && bestDepth > AboveDepth * sc) { f.AbovePlate = best; f.AboveT = now; }
            if (near >= 0)
            {
                f.NearFrame = Time.frameCount;
                PressPlate q = at(near);
                float g = Mathf.Clamp01(1f - nearDepth / (0.06f * sc));
                q.Glow = Mathf.Max(q.Glow, g * g);
                if (!f.Override) HandFeel.Offer(h, Surface(q), f.S != St.Idle && f.Plate == near ? Mathf.Min(nearDepth, bestDepth) : nearDepth,
                                                f.S != St.Idle && f.Plate == near && bestDepth <= 0f);
            }
            Vector3 n = best >= 0 ? -at(best).T.forward : Vector3.zero;
            if (HandFeel.Trace && (best >= 0 || f.S != St.Idle))
                Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "trace {0} {1}: f{2} t={3:0.000} {4} plate {5} d={6:0.0}mm v={7:0.00}m/s in={8:0.00}",
                    Tag, h.IsLeft ? "L" : "R", Time.frameCount, now, f.S, best + 1, bestDepth / sc * 1000f, vel.magnitude / sc, best >= 0 ? Vector3.Dot(vel, -n) / sc : 0f));
            bool fromAbove = best >= 0 && f.AbovePlate == best && now - f.AboveT < FromAboveWindow;
            bool receding = best >= 0 && Vector3.Dot(vel, n) / sc > 0.05f;
            // hover starts and, for a fast poke that arrives already past the click point, the click comes in the same frame
            bool canEnter = best >= 0 && bestDepth < HoverDepth * sc && fromAbove && !receding;
            if (f.S == St.Idle) { if (canEnter) Enter(f, h, at, best, tip); }
            else if (f.S == St.Hover && (best != f.Plate || bestDepth > LeaveDepth * sc))
            {
                if (HandFeel.Trace) Plugin.L.LogInfo(Tag + ": " + (h.IsLeft ? "L" : "R") + " left plate " + (f.Plate + 1) + (best != f.Plate ? " (now " + (best + 1) + ")" : " (pulled out)"));
                Leave(f, at, count);
                if (canEnter) Enter(f, h, at, best, tip);
            }
            switch (f.S)
            {
                case St.Hover:
                    {
                        PressPlate q = at(f.Plate);
                        Ride(f, h, q, bestDepth, n, tip, sc);
                        float approach = Vector3.Dot(vel, -n) / sc;
                        float tangential = (vel - n * Vector3.Dot(vel, n)).magnitude / sc;
                        if (approach >= MinApproach) f.ApproachT = now;
                        if (q.Disabled)
                        {
                            // a disabled plate doesn't move: the finger stops on it, a refusal once per contact
                            if (bestDepth <= 0f && !f.Refused && now - m_lastPress > Refractory)
                            {
                                f.Refused = true;
                                m_lastPress = now;
                                Refusals++;
                                Last = "disabled " + Label(f.Plate);
                                PressFeel.Refuse(h, tip);
                                Plugin.L.LogInfo(Tag + ": " + (h.IsLeft ? "L" : "R") + " touched disabled plate " + (f.Plate + 1) + " '" + Label(f.Plate) + "'");
                                f.S = St.Latched;
                            }
                            break;
                        }
                        if (bestDepth <= -Family.ClickAt * sc && now - m_lastPress > Refractory)
                        {
                            // the motion over the last 100 ms: moving in (at least 5 cm/s), within 60 degrees of straight in
                            float inW, sideW;
                            bool moved = f.Path.Speeds(now, tip, n, sc, 0.1f, out inW, out sideW);
                            if (now - f.ApproachT < 0.15f && moved && inW >= MinApproach && HandFeel.DirectionOk(inW, sideW))
                            {
                                LastSide = sideW;
                                Press(f, h, q, tip, bestDepth, inW);
                                f.S = St.Latched;
                            }
                            else if (bestDepth < -0.03f * sc)
                            {
                                Plugin.L.LogInfo(Tag + ": plate " + (f.Plate + 1) + " pushed through without a press");
                                f.S = St.Latched;
                            }
                        }
                    }
                    break;
                case St.Latched:
                    {
                        if (best != f.Plate || bestDepth > RearmDepth * sc) { Leave(f, at, count); break; }
                        PressPlate q = at(f.Plate);
                        Ride(f, h, q, bestDepth, n, tip, sc);
                        if (f.Clicked && !f.ReleasedFb && -bestDepth < Family.ReleaseAt * sc)
                        {
                            f.ReleasedFb = true;
                            Releases++;
                            PressFeel.Release(h, tip);
                        }
                    }
                    break;
            }
        }

        /// The plate goes down with the finger (not a disabled one), the drawn finger rides its face and is held at the
        /// bottom; a contact cue the first time the finger meets it.
        void Ride(Finger f, Hand h, PressPlate q, float depth, Vector3 n, Vector3 tip, float sc)
        {
            float travel = q.Disabled ? 0f : Family.Travel * sc;
            q.Press = Mathf.Clamp(-depth, 0f, travel);
            q.PressV = 0f;
            q.DriveFrame = Time.frameCount;
            if (depth > 0f) return;
            if (!f.Touched && !f.Clicked)
            {
                f.Touched = true;
                if (!q.Disabled) PressFeel.Contact(h, tip, Family);
            }
            if (!f.Override && Family.Stops) HandFeel.Contact(h, n, -depth - travel);
        }

        void Enter(Finger f, Hand h, Func<int, PressPlate> at, int plate, Vector3 tip)
        {
            f.S = St.Hover;
            f.Plate = plate;
            f.HoverSince = Time.unscaledTime;
            f.Touched = f.Clicked = f.ReleasedFb = f.Refused = false;
            PressPlate q = at(plate);
            q.Hovered = true;
            Hovers++;
            bool ok = OnHover == null || OnHover(plate, h);
            if (ok) PressFeel.Hover(h, tip, Family);
            Plugin.L.LogInfo(Tag + ": " + (h.IsLeft ? "L" : "R") + " over plate " + (plate + 1) + " '" + Label(plate) + "'" + (ok ? "" : " (hover refused)"));
        }

        void Leave(Finger f, Func<int, PressPlate> at, int count)
        {
            if (f.Plate >= 0 && f.Plate < count) { PressPlate q = at(f.Plate); if (q != null) q.Hovered = false; }
            f.S = St.Idle;
            f.Plate = -1;
        }

        void Press(Finger f, Hand h, PressPlate q, Vector3 tip, float depth, float approach)
        {
            m_lastPress = Time.unscaledTime;
            f.Clicked = true;
            LastDepthMm = -depth / VRRig.I.Scale * 1000f;
            LastSpeed = approach;
            string why = OnPress != null ? OnPress(f.Plate, h) : null;
            float sc = VRRig.I.Scale;
            if (why == null)
            {
                Selects++;
                Last = "select " + Label(f.Plate);
                PressFeel.Click(h, tip, Family);
            }
            else
            {
                Refusals++;
                Last = why + " " + Label(f.Plate);
                PressFeel.Refuse(h, tip);
            }
            LastPressAt = Time.unscaledTime;
            // press telemetry (§4 "Click at the fingertip"): how deep the fingertip was when it clicked
            Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "{0}: {1} pressed plate {2} '{3}' -> {4} (fingertip {5:0.0} mm in, travel {6:0.0} mm, {7:0.00} m/s in)",
                Tag, h.IsLeft ? "L" : "R", f.Plate + 1, Label(f.Plate), why ?? "selected", -depth / sc * 1000f, q.Press / sc * 1000f, approach));
            Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "press: {0} {1} {2:0.0} mm {3:0.00} m/s", Tag, Family.Name, -depth / sc * 1000f, approach));
        }

        /// The plates no finger drives come back up (critically damped, ~40 ms), and the glow fades.
        public static void Settle(Func<int, PressPlate> at, int count, float dt)
        {
            const float w = 60f;
            float e = Mathf.Exp(-w * dt);
            for (int i = 0; i < count; i++)
            {
                PressPlate q = at(i);
                if (q == null) continue;
                if (q.DriveFrame < Time.frameCount - 1)
                {
                    float y = q.Press, k = (q.PressV + w * y) * dt;
                    q.Press = Mathf.Max(0f, (y + k) * e);
                    q.PressV = (q.PressV - w * k) * e;
                }
                q.Glow = Mathf.Max(0f, q.Glow - dt * 6f);
            }
        }

        static FeelSurface Surface(PressPlate q) => new FeelSurface
        {
            Centre = q.RestFace, N = -q.T.forward, U = q.T.right, V = q.T.up, Half = q.Size * 0.5f, Family = PressFamily.Plate,
        };

        public string Describe() => "touch L:" + m_l.S + " R:" + m_r.S + " | hovers " + Hovers + " selects " + Selects + " refused " + Refusals + " releases " + Releases + " last " + Last +
                                    string.Format(CultureInfo.InvariantCulture, " | last press {0:0.0} mm in at {1:0.00} m/s in, {2:0.00} sideways", LastDepthMm, LastSpeed, LastSide);

        /// A synthetic fingertip through the real state machine (`ptouch` and the boards' test seams): tap, hover, slow,
        /// swipe at plate n.
        public IEnumerator Drive(MonoBehaviour runner, Hand h, Func<int, PressPlate> at, int n, string mode, Action<string> done)
        {
            Finger f = F(h);
            float s = VRRig.I.Scale;
            PressPlate q = at(n);
            int sel0 = Selects, ref0 = Refusals;
            f.Override = true;
            Vector3 P(float cm, float side = 0f) => q.T.position - q.T.forward * (q.Press + cm * 0.01f * s) + q.T.right * side;
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
            IEnumerator Hold(Vector3 p, float seconds)
            {
                float t0 = Time.unscaledTime;
                f.OverrideTip = p;
                while (Time.unscaledTime - t0 < seconds) yield return null;
            }
            switch (mode)
            {
                case "hover":
                    yield return runner.StartCoroutine(Hold(P(6f), 0.1f));
                    yield return runner.StartCoroutine(Move(P(6f), P(1.5f), 0.12f));
                    yield return runner.StartCoroutine(Hold(P(1.5f), 0.5f));
                    yield return runner.StartCoroutine(Move(P(1.5f), P(6f), 0.12f));
                    break;
                case "slow":
                    yield return runner.StartCoroutine(Hold(P(6f), 0.1f));
                    yield return runner.StartCoroutine(Move(P(6f), P(2f), 0.12f));
                    yield return runner.StartCoroutine(Move(P(2f), P(-1.5f), 1.75f));
                    yield return runner.StartCoroutine(Move(P(-1.5f), P(6f), 0.12f));
                    break;
                case "swipe":
                    {
                        // in from the side, mostly sideways (a swipe, not a press on whatever lies beside the plate)
                        float half = q.Size.x * 0.5f + 0.04f * s;
                        yield return runner.StartCoroutine(Hold(P(2f, -half - 0.06f * s), 0.1f));
                        yield return runner.StartCoroutine(Move(P(2f, -half - 0.06f * s), P(-0.5f, -half), 0.1f));
                        yield return runner.StartCoroutine(Move(P(-0.5f, -half), P(-0.5f, half), 2f * half / s));
                        yield return runner.StartCoroutine(Move(P(-0.5f, half), P(6f, half), 0.12f));
                    }
                    break;
                default:   // tap
                    yield return runner.StartCoroutine(Hold(P(6f), 0.1f));
                    yield return runner.StartCoroutine(Move(P(6f), P(2f), 0.12f));
                    yield return runner.StartCoroutine(Hold(P(2f), 0.15f));
                    yield return runner.StartCoroutine(Move(P(2f), P(-1.5f), 0.08f));
                    yield return runner.StartCoroutine(Hold(P(-1.5f), 0.05f));
                    yield return runner.StartCoroutine(Move(P(-1.5f), P(6f), 0.12f));
                    break;
            }
            f.OverrideTip = P(10f);
            yield return null;
            f.Override = false;
            string outcome = Selects > sel0 ? "selected" : Refusals > ref0 ? "refused" : "none";
            Last = mode + " -> " + outcome;
            Plugin.L.LogInfo(Tag + ": ptouch " + mode + " plate " + (n + 1) + " -> " + outcome);
            done?.Invoke(outcome);
        }
    }
}

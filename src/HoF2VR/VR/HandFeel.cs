using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// What a press target is like (docs/hand-feel-design.md §3.4; real metres, x the room scale in the world).
    internal sealed class PressFamily
    {
        public readonly string Name;
        public readonly float Travel, ClickAt, ReleaseAt, Capture;
        public readonly bool Stops, Button;
        public PressFamily(string name, float travel, float clickAt, float releaseAt, float capture, bool stops, bool button)
        {
            Name = name; Travel = travel; ClickAt = clickAt; ReleaseAt = releaseAt; Capture = capture; Stops = stops; Button = button;
        }
        // travel, click at, release at, capture margin
        public static readonly PressFamily Plate = new PressFamily("plate", 0.006f, 0.0035f, 0.0015f, 0.010f, true, true);
        public static readonly PressFamily Board = new PressFamily("board", 0.006f, 0.0035f, 0.0015f, 0.008f, true, true);
        public static readonly PressFamily Card = new PressFamily("card", 0f, 0.002f, 0f, 0.008f, true, false);
        public static readonly PressFamily Tile = new PressFamily("tile", 0f, 0.003f, 0f, 0.004f, true, false);
        public static readonly PressFamily Die = new PressFamily("die", 0f, 0.002f, 0f, 0.005f, true, false);
        public static readonly PressFamily Wheel = new PressFamily("wheel", 0f, 0f, 0f, 0f, false, false);
    }

    /// A press target's face this frame (world).
    internal struct FeelSurface
    {
        public Vector3 Centre, N, U, V;   // the face's middle, its normal toward the player, its two sideways axes
        public Vector2 Half;             // half the face's size along U and V
        public PressFamily Family;
    }

    /// The tracked hand, the interaction hand and the drawn hand (docs/hand-feel-design.md §3.1, §3.3, §3.5, §3.6):
    ///  * tracked = the controller; never filtered (throws, swings and dice read it directly);
    ///  * interaction = tracked + a sideways smoothing correction (One Euro filter, only near a target) + a sideways pull
    ///    toward the middle of the target the fingertip comes onto (at most MaxPull, only in the last 6 cm); the press
    ///    logic of every touch target reads this fingertip (HandFeel.Tip);
    ///  * drawn = interaction + the stop: a finger that came onto a button or card from the front is held on its face (a
    ///    button's face moves with its travel) until the real hand is 5 cm past it, then let go; the glove is drawn there,
    ///    and a faint ghost glove shows the real hand while the two are more than 2 cm apart.
    /// Each press system offers its nearest target per hand (Offer) and reports contacts (Contact); the offsets use the last
    /// frame's best offer and decay with critically damped springs when nothing is near, so nothing ever jumps. The gloves
    /// are placed again just before rendering with this frame's offsets.
    internal class HandFeel : MonoBehaviour
    {
        public static HandFeel I;
        public static float SpringOmega = 50f, ReleaseOmega = 40f;
        public static float NearDist = 0.06f, FullDist = 0.01f, PointFar = 0.12f, PointFull = 0.06f;
        public static float GhostFrom = 0.02f, GhostFull = 0.04f, Hold = 0.05f;
        public static int Contacts, LetGos;
        public static bool Trace;
        /// The direction guard at a press (§3.4): moving faster than 5 cm/s, within 60 degrees of straight in.
        public static float MaxAngleTan = 1.73f, SlowSpeed = 0.05f;
        public static bool DirectionOk(float approach, float tangential)
        {
            float speed = Mathf.Sqrt(approach * approach + tangential * tangential);
            return speed < SlowSpeed || (approach > 0f && tangential <= MaxAngleTan * approach);
        }
        public static float MaxStop, MaxAssist, MaxFilter;

        // MaxLag: the filtered fingertip never trails the real one by more than this (the design's lag limit at speed): the
        // speed estimate is itself filtered (1 Hz), so at the start of a fast move the plain filter would trail by centimetres
        struct Params { public float Assist, MaxPull, Margin, FMin, Beta, MaxLag; public bool Smooth; }
        static readonly Params[] s_rows =
        {
            new Params { Assist = 0f, MaxPull = 0f, Margin = 0f, FMin = 4f, Beta = 120f, MaxLag = 0.0013f, Smooth = false },          // Off
            new Params { Assist = 0.35f, MaxPull = 0.006f, Margin = 0.5f, FMin = 4f, Beta = 120f, MaxLag = 0.0013f, Smooth = true },  // Subtle
            new Params { Assist = 0.6f, MaxPull = 0.012f, Margin = 1f, FMin = 2f, Beta = 80f, MaxLag = 0.002f, Smooth = true },       // Normal
            new Params { Assist = 0.85f, MaxPull = 0.02f, Margin = 1.5f, FMin = 1f, Beta = 60f, MaxLag = 0.0027f, Smooth = true },    // Strong
        };

        static Params Lerp(Params a, Params b, float t) => new Params
        {
            Assist = Mathf.Lerp(a.Assist, b.Assist, t), MaxPull = Mathf.Lerp(a.MaxPull, b.MaxPull, t), Margin = Mathf.Lerp(a.Margin, b.Margin, t),
            FMin = Mathf.Lerp(a.FMin, b.FMin, t), Beta = Mathf.Lerp(a.Beta, b.Beta, t), MaxLag = Mathf.Lerp(a.MaxLag, b.MaxLag, t), Smooth = b.Smooth,
        };

        static Params Row(float x, float[] keys)
        {
            if (x <= keys[0]) return s_rows[0];
            for (int i = 1; i < 4; i++)
                if (x <= keys[i]) return Lerp(s_rows[i - 1], s_rows[i], (x - keys[i - 1]) / (keys[i] - keys[i - 1]));
            return s_rows[3];
        }

        static readonly float[] s_assistKeys = { 0f, 0.35f, 0.6f, 0.85f }, s_smoothKeys = { 0f, 0.33f, 0.66f, 1f };

        /// The preset's numbers, with the file-only TouchAssist / Smoothing overrides.
        static Params Current()
        {
            Params p = s_rows[(int)Settings.HandPreset];
            float a = Settings.TouchAssist;
            if (a >= 0f) { Params q = Row(a, s_assistKeys); p.Assist = q.Assist; p.MaxPull = q.MaxPull; p.Margin = q.Margin; }
            float sm = Settings.Smoothing;
            if (sm >= 0f)
            {
                if (sm < 0.01f) p.Smooth = false;
                else { Params q = Row(Mathf.Max(sm, 0.33f), s_smoothKeys); p.Smooth = true; p.FMin = q.FMin; p.Beta = q.Beta; p.MaxLag = q.MaxLag; }
            }
            return p;
        }

        /// The capture margin of a family with the preset (Off: the old 4-5 mm), world units.
        public static float Capture(PressFamily f)
        {
            float sc = VRRig.I != null ? VRRig.I.Scale : 1f;
            return Mathf.Max(f.Capture * Current().Margin, 0.004f) * sc;
        }

        sealed class St
        {
            public bool Left, Valid, FInit, Frozen, LetGo;
            public Vector3 FX, FDX;                                  // One Euro state (tracking space)
            public bool HasSurf, HasOffer, NewHas, NewContact;
            public FeelSurface Surf, Offer, NewSurf;
            public int OfferFrame = -10, NewFrame = -10, ContactFrame = -10, StopFrame = -10, FinalFrame = -10;
            public float NewD, OfferD = float.MaxValue, PointNew, PointW, D = float.MaxValue;
            public Vector3 Tracked, Tip, Filter, Assist, AssistV, AssistTarget, Stop, StopV, StopN, Drawn;
            public float StopExcess, StopHold;
            public Vector3 LateD;                                    // render-time minus latched fingertip (world), this frame
            public Transform Shadow;
        }

        static readonly St s_l = new St { Left = true }, s_r = new St();
        static St S(Hand h) => h.IsLeft ? s_l : s_r;
        static float Scale => VRRig.I != null ? VRRig.I.Scale : 1f;
        static int s_earlyFrame = -1, s_finalFrame = -1;

        void Awake()
        {
            I = this;
            Application.onBeforeRender += BeforeRender;
            Diag.StatusExtras["feel"] = () => Diag.Q(Describe());
            Diag.Commands["feel"] = Command;
            Diag.Commands["lateprobe"] = LateProbe;
            Diag.Commands["latehands"] = a => { VRInput.LateHandsOn = a.Length > 1 && a[1] == "1"; return "latehands=" + VRInput.LateHandsOn; };
            Diag.StatusExtras["lateHands"] = () => Diag.Q(VRInput.LateHandFrames + " frames, " + VRInput.LateRejects + " refused, last R " +
                                                           (VRInput.LastLateDeltaR.magnitude * 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " mm");
        }

        void OnDestroy() { Application.onBeforeRender -= BeforeRender; }

        void Update()
        {
            Early();
            PressFeel.Update();
        }

        // ---- the interaction fingertip ---------------------------------------------------------------------------------
        /// Once per frame, before any touch logic (each press system calls it; the first call does the work).
        public static void Early()
        {
            int fr = Time.frameCount;
            if (s_earlyFrame == fr) return;
            s_earlyFrame = fr;
            VRInput.Update();
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 1e-3f, 0.1f);
            Params p = Current();
            EarlyHand(VRInput.L, s_l, dt, p);
            EarlyHand(VRInput.R, s_r, dt, p);
        }

        /// The fingertip the press logic uses (world): the glove's pointing tip with the smoothing and the assist.
        public static Vector3 Tip(Hand h)
        {
            Early();
            St s = S(h);
            return s.Valid ? s.Tip : TouchInput.TipOf(h);
        }

        /// The real fingertip (world), for diagnostics.
        public static Vector3 Tracked(Hand h) { Early(); return S(h).Tracked; }

        static void EarlyHand(Hand h, St s, float dt, Params p)
        {
            VRRig rig = VRRig.I;
            Vector3 local;
            if (rig == null || !XRSettings.enabled || !h.Tracked || !VRHands.TryGetPointingTipLocal(h.IsLeft, out local))
            {
                s.Valid = false;
                s.FInit = false;
                return;
            }
            float sc = rig.Scale;
            Vector3 raw = h.Pos + h.Rot * local;   // tracking space, real metres
            s.Tracked = rig.ToWorld(raw);
            s.Valid = true;
            int fr = Time.frameCount;
            // the surface the corrections refer to: the best offer of the last frame (fine for a sideways nudge)
            s.HasSurf = s.HasOffer && fr - s.OfferFrame <= 2;
            if (s.HasSurf) s.Surf = s.Offer;
            Vector3 filt = OneEuro(s, raw, dt, p.FMin, p.Beta, p.MaxLag);
            float d = s.HasSurf ? Vector3.Dot(s.Tracked - s.Surf.Centre, s.Surf.N) / sc : float.MaxValue;
            float w = s.HasSurf ? Near(d) : 0f;
            if (!s.Frozen)
            {
                // smoothing: only sideways (the axis into the face is never smoothed: presses must not come later)
                Vector3 corr = rig.ToWorldDir(filt - raw) * sc;
                s.Filter = p.Smooth && s.HasSurf ? Lat(corr, s.Surf.N) * w : Vector3.zero;
                Vector3 target = Vector3.zero;
                if (s.HasSurf && p.Assist > 0f && s.Surf.Half.x > 0f && s.Surf.Half.y > 0f)
                {
                    Vector3 rel = s.Tracked + s.Filter - s.Surf.Centre;
                    float u = Vector3.Dot(rel, s.Surf.U), v = Vector3.Dot(rel, s.Surf.V);
                    float hx = s.Surf.Half.x, hy = s.Surf.Half.y;
                    float cap = Mathf.Max(s.Surf.Family.Capture * p.Margin, 0.004f) * sc;
                    if (Mathf.Abs(u) <= hx + cap && Mathf.Abs(v) <= hy + cap)
                    {
                        // toward the sweet zone: the face shrunk by a quarter of its smaller side (at least 3 mm)
                        float inset = Mathf.Max(0.25f * Mathf.Min(hx, hy), 0.0015f * sc);
                        float sx = Mathf.Max(0f, hx - inset), sy = Mathf.Max(0f, hy - inset);
                        Vector3 pull = s.Surf.U * (Mathf.Clamp(u, -sx, sx) - u) + s.Surf.V * (Mathf.Clamp(v, -sy, sy) - v);
                        target = Vector3.ClampMagnitude(pull * (p.Assist * w), p.MaxPull * sc);
                    }
                }
                s.AssistTarget = target;
            }
            Spring(ref s.Assist, ref s.AssistV, s.AssistTarget, SpringOmega, dt);
            s.Tip = s.Tracked + s.Filter + s.Assist;
            s.D = d;
            MaxAssist = Mathf.Max(MaxAssist, s.Assist.magnitude / sc);
            MaxFilter = Mathf.Max(MaxFilter, s.Filter.magnitude / sc);
        }

        static Vector3 Lat(Vector3 v, Vector3 n) => v - n * Vector3.Dot(v, n);

        /// 1 within FullDist of the face (and inside it), 0 from NearDist on, smooth between (real metres).
        static float Near(float d)
        {
            if (d <= FullDist) return 1f;
            if (d >= NearDist) return 0f;
            float t = 1f - (d - FullDist) / (NearDist - FullDist);
            return t * t * (3f - 2f * t);
        }

        /// The glove pre-shapes: starts pointing 12 cm in front of a target, points fully from 6 cm (Alyx's hand poses
        /// blend over 10-15 cm).
        static float PointWeight(float d)
        {
            if (d <= PointFull) return 1f;
            if (d >= PointFar) return 0f;
            float t = 1f - (d - PointFull) / (PointFar - PointFull);
            return t * t * (3f - 2f * t);
        }

        static Vector3 OneEuro(St s, Vector3 x, float dt, float fmin, float beta, float maxLag)
        {
            if (!s.FInit) { s.FInit = true; s.FX = x; s.FDX = Vector3.zero; return x; }
            Vector3 dx = (x - s.FX) / dt;
            s.FDX += Alpha(1f, dt) * (dx - s.FDX);
            float fc = fmin + beta * s.FDX.magnitude;
            s.FX += Alpha(fc, dt) * (x - s.FX);
            Vector3 lag = s.FX - x;
            if (lag.magnitude > maxLag) s.FX = x + lag.normalized * maxLag;
            return s.FX;
        }

        static float Alpha(float fc, float dt) => 1f / (1f + 1f / (2f * Mathf.PI * fc * dt));

        /// Critically damped spring, exact step for a fixed target.
        static void Spring(ref Vector3 x, ref Vector3 v, Vector3 target, float w, float dt)
        {
            float e = Mathf.Exp(-w * dt);
            Vector3 y = x - target;
            Vector3 k = (v + w * y) * dt;
            x = target + (y + k) * e;
            v = (v - w * k) * e;
        }

        // ---- what the press systems tell -------------------------------------------------------------------------------
        /// A press system's nearest target for this hand (`d` = the interaction tip's distance in front of its face, world;
        /// `contact` = the finger is on it, having come from the front). The nearest offer of the frame wins.
        public static void Offer(Hand h, FeelSurface surf, float d, bool contact)
        {
            St s = S(h);
            int fr = Time.frameCount;
            float dr = d / Scale;
            if (s.NewFrame != fr) { s.NewFrame = fr; s.NewHas = false; s.PointNew = 0f; }
            s.PointNew = Mathf.Max(s.PointNew, PointWeight(dr));
            if (s.NewHas && (s.NewContact && !contact || (contact == s.NewContact && Mathf.Abs(dr) >= Mathf.Abs(s.NewD)))) return;
            s.NewHas = true;
            s.NewSurf = surf;
            s.NewD = dr;
            s.NewContact = contact;
        }

        /// The finger is on a surface it came onto from the front: the drawn fingertip is held `excess` (world) back
        /// along `n`, where excess = how far the interaction tip is past where the drawn one stays. Past Hold (5 cm, or
        /// `holdReal`) the stop lets go until the finger leaves the surface.
        public static void Contact(Hand h, Vector3 n, float excess, float holdReal = -1f)
        {
            St s = S(h);
            int fr = Time.frameCount;
            if (fr - s.ContactFrame > 3) Contacts++;
            s.ContactFrame = fr;
            if (!Settings.StopAtSurfaces || s.LetGo) return;
            float hold = (holdReal > 0f ? holdReal : Hold) * Scale;
            if (excess > hold)
            {
                s.LetGo = true;
                LetGos++;
                Plugin.L.LogInfo("feel: " + (s.Left ? "L" : "R") + " let go of the surface (real hand " + (excess / Scale * 100f).ToString("0.0", CultureInfo.InvariantCulture) + " cm past it)");
                return;
            }
            if (s.StopFrame != fr || excess > s.StopExcess) { s.StopFrame = fr; s.StopN = n; s.StopExcess = Mathf.Max(0f, excess); s.StopHold = hold; }
        }

        /// How much the glove should point (0..1) - a target within 12 cm in front of the fingertip.
        public static float PointWeight(Hand h)
        {
            St s = S(h);
            return Time.frameCount - s.FinalFrame <= 2 ? s.PointW : 0f;
        }

        /// The drawn hand minus the tracked one (world), for the gloves.
        public static Vector3 DrawnOffset(bool left)
        {
            St s = left ? s_l : s_r;
            return s.Valid && Time.frameCount - s.FinalFrame <= 2 ? s.Drawn : Vector3.zero;
        }

        /// The ghost glove's opacity (0..1 of its setting's maximum) for a hand.
        public static float GhostAlpha(bool left)
        {
            if (Settings.GhostHand == Settings.GhostMode.Off) return 0f;
            float m = DrawnOffset(left).magnitude / Scale;
            float t = Mathf.Clamp01((m - GhostFrom) / (GhostFull - GhostFrom));
            return t * (Settings.GhostHand == Settings.GhostMode.Clear ? 0.4f : 0.2f);
        }

        // ---- per frame, just before rendering ------------------------------------------------------------------------------
        void BeforeRender()
        {
            int fr = Time.frameCount;
            if (s_finalFrame == fr) return;
            s_finalFrame = fr;
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 1e-3f, 0.1f);
            try
            {
                Final(s_l, dt);
                Final(s_r, dt);
                // the gloves again, with this frame's offsets (the boards decide in LateUpdate, maybe after the gloves)
                if (VRHands.I != null) VRHands.I.UpdateHands();
            }
            catch (Exception e) { Plugin.L.LogError("feel: " + e); }
        }

        static void Final(St s, float dt)
        {
            int fr = Time.frameCount;
            if (fr - s.ContactFrame > 3) s.LetGo = false;   // the finger left the surface: the next contact stops again
            s.Frozen = fr - s.ContactFrame <= 1;            // in contact: the sideways corrections keep their value
            bool hold = s.StopFrame == fr && !s.LetGo && s.Valid;
            if (hold) { s.Stop = s.StopN * s.StopExcess; s.StopV = Vector3.zero; }
            else Spring(ref s.Stop, ref s.StopV, Vector3.zero, ReleaseOmega, dt);
            // this frame's best offer is what the next frame's corrections refer to
            bool fresh = s.NewFrame == fr;
            s.HasOffer = fresh && s.NewHas;
            if (s.HasOffer) { s.Offer = s.NewSurf; s.OfferFrame = fr; s.OfferD = s.NewD; }
            s.PointW = fresh ? s.PointNew : 0f;
            if (!s.Valid) { s.Assist = s.AssistV = s.AssistTarget = s.Filter = Vector3.zero; }
            s.Drawn = s.Valid ? s.Filter + s.Assist + s.Stop : Vector3.zero;
            s.FinalFrame = fr;
            MaxStop = Mathf.Max(MaxStop, s.Stop.magnitude / Scale);
            Shadow(s, fresh && s.NewHas);
        }

        // ---- the render-time pose (§3.2) ---------------------------------------------------------------------------------
        /// The controllers' render-time poses just replaced the latched ones (VRInput.LateHands, in the late hook): a hand
        /// held by a stop stays on its surface for the part of the newest motion that goes into it (and comes off for the part
        /// that goes out), and the fingertip's shadow follows the sideways part.
        public static void Late()
        {
            VRRig rig = VRRig.I;
            if (rig == null) return;
            LateHand(VRInput.L, s_l, rig);
            LateHand(VRInput.R, s_r, rig);
        }

        static void LateHand(Hand h, St s, VRRig rig)
        {
            int fr = Time.frameCount;
            s.LateD = Vector3.zero;
            Vector3 local;
            if (h.LateFrame != fr || !s.Valid || s.FinalFrame != fr || !VRHands.TryGetPointingTipLocal(h.IsLeft, out local)) return;
            Vector3 d = rig.ToWorld(h.Pos + h.Rot * local) - rig.ToWorld(h.LatchedPos + h.LatchedRot * local);
            s.LateD = d;
            if (s.StopFrame == fr && !s.LetGo)
            {
                float e = Mathf.Clamp(s.StopExcess - Vector3.Dot(d, s.StopN), 0f, s.StopHold);
                s.Stop = s.StopN * e;
                s.Drawn = s.Filter + s.Assist + s.Stop;
            }
            if (s.Shadow != null && s.Shadow.gameObject.activeSelf && s.HasOffer) s.Shadow.position += Lat(d, s.Offer.N);
        }

        // `lateprobe N`: N frames of the right hand after the late hook - the latched and render-time poses, where the glove was
        // drawn (minus HandFeel's offset), Unity's late head and the runtime's render head (all tracking space, metres)
        static int s_probeLeft;
        static readonly System.Text.StringBuilder s_probe = new System.Text.StringBuilder();

        public static string LateProbe(string[] a)
        {
            s_probeLeft = a.Length > 1 ? int.Parse(a[1]) : 120;
            s_probe.Length = 0;
            s_probe.Append("frame late lx ly lz rx ry rz gx gy gz ang hx hy hz ox oy oz\n");
            return "probing " + s_probeLeft + " frames";
        }

        public static void ProbeFrame(bool late)
        {
            if (s_probeLeft <= 0) return;
            VRRig rig = VRRig.I;
            Hand h = VRInput.R;
            Transform g = VRHands.GloveRoot(false);
            if (rig == null || g == null) return;
            Vector3 gl = rig.FromWorld(g.position - DrawnOffset(false));
            Vector3 oh;
            Quaternion orot;
            if (!VRInput.LateHead(out oh, out orot)) oh = new Vector3(float.NaN, float.NaN, float.NaN);
            float ang = Quaternion.Angle(rig.ToWorld(h.Rot), g.rotation);
            s_probe.AppendFormat(CultureInfo.InvariantCulture, "{0} {1} {2:0.00000} {3:0.00000} {4:0.00000} {5:0.00000} {6:0.00000} {7:0.00000} {8:0.00000} {9:0.00000} {10:0.00000} {11:0.000} {12:0.00000} {13:0.00000} {14:0.00000} {15:0.00000} {16:0.00000} {17:0.00000}\n",
                Time.frameCount, late ? 1 : 0, h.LatchedPos.x, h.LatchedPos.y, h.LatchedPos.z, h.Pos.x, h.Pos.y, h.Pos.z, gl.x, gl.y, gl.z, ang,
                VRInput.HeadPos.x, VRInput.HeadPos.y, VRInput.HeadPos.z, oh.x, oh.y, oh.z);
            if (--s_probeLeft == 0) Plugin.L.LogInfo("lateprobe:\n" + s_probe);
        }

        // ---- the fingertip's shadow on the face it comes onto (§3.4 "Near") ------------------------------------------------
        static Material s_shadowMat;
        static Texture2D s_shadowTex;

        static void Shadow(St s, bool has)
        {
            float sc = Scale;
            float d = s.OfferD;
            bool on = has && s.Valid && s.Offer.Family != PressFamily.Wheel && d < NearDist && d > -0.01f && XRSettings.enabled;
            if (!on) { if (s.Shadow != null && s.Shadow.gameObject.activeSelf) s.Shadow.gameObject.SetActive(false); return; }
            if (s.Shadow == null) s.Shadow = MakeShadow(s.Left);
            if (s.Shadow == null) return;
            if (!s.Shadow.gameObject.activeSelf) s.Shadow.gameObject.SetActive(true);
            Vector3 tip = s.Tip + s.Stop;
            Vector3 n = s.Offer.N;
            Vector3 p = tip - n * Vector3.Dot(tip - s.Offer.Centre, n) + n * (0.0006f * sc);
            float dd = Mathf.Max(0f, d);
            float r = (0.006f + 0.4f * dd) * sc;
            s.Shadow.SetPositionAndRotation(p, Quaternion.LookRotation(-n, s.Offer.V.sqrMagnitude > 0.5f ? s.Offer.V : Vector3.up));
            s.Shadow.localScale = new Vector3(2f * r, 2f * r, 1f);
            // on the game's dark buttons a shadow can't be seen: there it is a soft warm light under the fingertip
            // (MRTK's "proximity light"); on cards, tiles and dice a soft shadow
            bool light = s.Offer.Family.Button;
            float a = (light ? 0.38f : 0.5f) * (1f - dd / NearDist);
            MeshRenderer mr = s.Shadow.GetComponent<MeshRenderer>();
            mr.sharedMaterial.color = light ? new Color(1f, 0.78f, 0.42f, a) : new Color(0.04f, 0.025f, 0.015f, a);
        }

        static Transform MakeShadow(bool left)
        {
            if (s_shadowTex == null)
            {
                const int n = 64;
                s_shadowTex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "HoF2VR_FingerShadow", wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[n * n];
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = Mathf.Clamp01(1f - r);
                        a = a * a * (3f - 2f * a);
                        px[y * n + x] = new Color32(255, 255, 255, (byte)(255f * a));
                    }
                s_shadowTex.SetPixels32(px);
                s_shadowTex.Apply(false, true);
            }
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) return null;
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.name = "HoF2VR_FingerShadow" + (left ? "L" : "R");
            go.layer = VRUI.Layer;
            UnityEngine.Object.DontDestroyOnLoad(go);
            // after the plates (5101-5103), before the touch dot (5120); the overlay's depth-only glove hides it under the finger
            var m = new Material(sh) { name = "HoF2VR_FingerShadow", mainTexture = s_shadowTex, renderQueue = 5104 };
            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = m;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go.transform;
        }

        // ---- diagnostics -----------------------------------------------------------------------------------------------
        static string One(St s)
        {
            float sc = Scale;
            if (!s.Valid) return "-";
            return string.Format(CultureInfo.InvariantCulture, "d {0} filter {1:0.0} assist {2:0.0} stop {3:0.0} mm{4}{5} point {6:0.00} ghost {7:0.00}",
                s.HasOffer ? (s.OfferD * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "cm " + s.Offer.Family.Name : "-",
                s.Filter.magnitude / sc * 1000f, s.Assist.magnitude / sc * 1000f, s.Stop.magnitude / sc * 1000f,
                Time.frameCount - s.ContactFrame <= 2 ? " contact" : "", s.LetGo ? " letgo" : "", s.PointW, GhostAlpha(s.Left));
        }

        static string Describe()
        {
            Params p = Current();
            return string.Format(CultureInfo.InvariantCulture, "{0} (assist {1:0.00}, pull {2:0.0} cm, smooth {3}) stop {4} ghost {5} | L: {6} | R: {7} | contacts {8} letgos {9} max stop {10:0.0} assist {11:0.0} filter {12:0.0} mm | {13}",
                Settings.HandPreset, p.Assist, p.MaxPull * 100f, p.Smooth ? p.FMin.ToString("0.#", CultureInfo.InvariantCulture) + "Hz/" + p.Beta.ToString("0", CultureInfo.InvariantCulture) : "off",
                Settings.StopAtSurfaces ? "on" : "off", Settings.GhostHand, One(s_l), One(s_r), Contacts, LetGos, MaxStop * 1000f, MaxAssist * 1000f, MaxFilter * 1000f,
                PressFeel.Describe());
        }

        /// `feel` - the state; `feel reset` - zero the counters and maxima; `feel probe` - the tracked, interaction and drawn
        /// fingertips of both hands to the log.
        static string Command(string[] a)
        {
            if (a.Length > 1 && a[1] == "reset")
            {
                Contacts = LetGos = 0;
                MaxStop = MaxAssist = MaxFilter = 0f;
                PressFeel.Reset();
                Haptics.ResetRate();
                return "reset";
            }
            if (a.Length > 1 && a[1] == "trace") { Trace = a.Length < 3 || a[2] != "0"; return "trace " + Trace; }
            if (a.Length > 1 && a[1] == "probe")
            {
                foreach (St s in new[] { s_l, s_r })
                    Plugin.L.LogInfo("feel probe " + (s.Left ? "L" : "R") + ": tracked " + Diag.V(s.Tracked) + " interaction " + Diag.V(s.Tip) + " drawn " + Diag.V(s.Tip + s.Stop) + " | " + One(s));
                return "probed";
            }
            return Describe();
        }
    }

    /// The fingertip's recent path: how fast it moved into a face and sideways over about the last 100 ms (a press is
    /// judged by the motion that brought the finger there, not by one frame: a slanted swipe that stops in a button, a
    /// creep or one jittery frame never press).
    internal sealed class MotionWindow
    {
        readonly float[] m_t = new float[32];
        readonly Vector3[] m_p = new Vector3[32];
        int m_n, m_i;

        public void Add(float t, Vector3 p)
        {
            m_t[m_i] = t;
            m_p[m_i] = p;
            m_i = (m_i + 1) % m_t.Length;
            if (m_n < m_t.Length) m_n++;
        }

        public void Clear() { m_n = 0; }

        /// Inward (into the face with normal n) and sideways speed, real m/s, over about `window` seconds.
        public bool Speeds(float now, Vector3 pNow, Vector3 n, float sc, float window, out float inward, out float side)
        {
            inward = side = 0f;
            int best = -1;
            float bestErr = float.MaxValue;
            for (int i = 0; i < m_n; i++)
            {
                float age = now - m_t[i];
                if (age < 0.025f || age > window * 1.5f) continue;
                float err = Mathf.Abs(age - window);
                if (err < bestErr) { bestErr = err; best = i; }
            }
            if (best < 0) return false;
            float dt = Mathf.Max(1e-3f, now - m_t[best]);
            Vector3 d = pNow - m_p[best];
            float along = Vector3.Dot(d, n);
            inward = -along / sc / dt;
            side = (d - n * along).magnitude / sc / dt;
            return true;
        }
    }

    /// Feedback for every state change of a press (§3.10, §3.11): hover = a tick (+ a very soft tick sound on buttons;
    /// cards have the game's own hover sound); contact = a light touch and a tap, only if the click does not follow within
    /// 30 ms (a fast poke feels one clean click, a slow press feels the contact first); click = the click pulse + a crisp
    /// click (buttons) or a tap (cards; the game plays its own select sound); release = a short tick and the click a little
    /// higher and quieter; refusal = two knocks and a dull double knock.
    internal static class PressFeel
    {
        public static int Hovers, Touches, Clicks, Releases, Refusals;
        public static float ContactDelay = 0.03f;
        /// The hand check's vibration test: the buttons' own pulses would be felt instead of the test buzz.
        public static bool MuteHaptics;
        static void Cue(Haptics.CueKind k, bool left) { if (!MuteHaptics) Haptics.Cue(k, left); }
        static readonly float[] s_touchAt = { -1f, -1f };
        static readonly Vector3[] s_touchP = new Vector3[2];
        static readonly bool[] s_touchButton = new bool[2];

        public static void Hover(Hand h, Vector3 p, PressFamily f)
        {
            Hovers++;
            Cue(Haptics.CueKind.Tick, h.IsLeft);
            if (f.Button) HandSounds.Play(HandSounds.Kind.Tick, p);
        }

        public static void Contact(Hand h, Vector3 p, PressFamily f)
        {
            int i = h.IsLeft ? 0 : 1;
            s_touchAt[i] = Time.unscaledTime + ContactDelay;
            s_touchP[i] = p;
            s_touchButton[i] = f.Button;
        }

        public static void Click(Hand h, Vector3 p, PressFamily f)
        {
            s_touchAt[h.IsLeft ? 0 : 1] = -1f;
            Clicks++;
            Cue(Haptics.CueKind.Click, h.IsLeft);
            HandSounds.Play(f.Button ? HandSounds.Kind.Click : HandSounds.Kind.Tap, p);
        }

        public static void Release(Hand h, Vector3 p)
        {
            Releases++;
            Cue(Haptics.CueKind.Release, h.IsLeft);
            HandSounds.Play(HandSounds.Kind.Release, p);
        }

        public static void Refuse(Hand h, Vector3 p)
        {
            s_touchAt[h.IsLeft ? 0 : 1] = -1f;
            Refusals++;
            Cue(Haptics.CueKind.Refuse, h.IsLeft);
            HandSounds.Play(HandSounds.Kind.Refuse, p);
        }

        public static void Update()
        {
            float now = Time.unscaledTime;
            for (int i = 0; i < 2; i++)
            {
                if (s_touchAt[i] < 0f || now < s_touchAt[i]) continue;
                s_touchAt[i] = -1f;
                Touches++;
                Cue(Haptics.CueKind.Touch, i == 0);
                HandSounds.Play(HandSounds.Kind.Tap, s_touchP[i], s_touchButton[i] ? 0.8f : 1f);
            }
        }

        public static void Reset() { Hovers = Touches = Clicks = Releases = Refusals = 0; }

        public static string Describe() => "feedback hovers " + Hovers + " touches " + Touches + " clicks " + Clicks + " releases " + Releases + " refusals " + Refusals +
                                           " | sounds " + HandSounds.Describe();
    }
}

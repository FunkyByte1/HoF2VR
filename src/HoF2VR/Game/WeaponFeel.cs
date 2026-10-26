using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// Until You Fall's held weapon (combat Stage 2, the coordinator's B for the user, 2026-10-13).
    ///
    /// Weapon physics (`[Combat] WeaponPhysics`, on): the weapon no longer sits glued to the controller. Its pose follows
    /// the hand's pose from `lag` seconds ago (UYF's history lag: growing with the hand's speed along each weapon's curve -
    /// the light blade from 3 to 6 m/s as its square, the heavy blade and axe 0-4 m/s about linearly - then shrinking again
    /// at the weapon's catch-up rate) on stiff springs (about 70 rad/s, damped on the difference of velocities, so a steady
    /// swing adds no lag of its own). The maximum lag is UYF's per weapon (WeaponSpec.Lag, from its SpringConfig): light
    /// blade and mace 25 ms, heavy blade and axe 50 ms, daggers none, the ultra-heavy axe and hammer 150 ms in one hand
    /// (the greatsword 25 ms), all three 50 ms in two. A blocked blow knocks the weapon
    /// (UYF's SpringImpact: an impulse along the blow; the springs at half strength, back to full over 0.5 s), and so does
    /// a hit an enemy's raised shield turns aside. Every check that uses the blade - hits, crits, blocks - reads the weapon
    /// where it is drawn (BladeHits asks Pose()).
    ///
    /// The two-handed grip (UYF's TwoHandedSecondaryGrip, style "Wide"; in UYF only the ultra-heavy greatsword, axe and
    /// hammer have one - HoF2's two-handed weapons are those): the free hand's grip pressed within 0.15 m of the haft takes
    /// hold of it, anywhere along it (the artefact's aim then does not start). It never lets go by distance, only when the
    /// grip is released. While held, the weapon points along the line between the hands (its roll stays the weapon hand's;
    /// the weapon hand's grip stays where it is), the off hand's fingers close (the grip is held), and a crit's direction
    /// leeway is 40 deg (one hand: 30; WeaponTable.CritLeeway).
    ///
    /// Seams: `weaponfeel?` (lag, springs, two-handed, the visual offset now), `vrweaponphysics on|off|setting`,
    /// `twohand?`.
    [DefaultExecutionOrder(-60)]
    internal class WeaponFeel : MonoBehaviour
    {
        public static WeaponFeel I;

        /// The weapon hand is held in two hands now.
        public static bool TwoHanded;
        /// The off hand's grip press this frame went to the haft (Abilities: no artefact aim for it).
        public static bool ClaimedThisPress;
        public static int Grabs, Releases, Impacts, Snaps;
        /// The loosest the weapon hand's springs were since `weaponfeel? reset` (1 = never loosened): the knock test reads it
        /// (MR3d-f: with Lead = Hold a soldier's blow came late in the test's own waits, its read after the 0.5 s were over).
        public static float SpringsMin = 1f;
        /// A hand that moved further than this in one frame jumped (tracking lost and found, a test's placement): no hand
        /// swings that fast (0.5 m a frame is 36 m/s at 72 Hz). The weapon is put there at once instead of springing after it
        /// along the jump (MR2b: the spring's catch-up swept the blade through a thief at 38 m/s - a wrong-way crit cut).
        public const float JumpSnap = 0.5f;
        public static float LastLag, LastOffset, MaxOffset, MaxLag, MaxSpeed;

        public const float Omega = 70f, Zeta = 0.85f, GripLimit = 0.2f;
        public const float GrabRadius = 0.15f, SoftTime = 0.5f;

        sealed class Sample { public float T; public Vector3 P; public Quaternion R; }

        sealed class Follow
        {
            public bool Has;
            public Vector3 P, V;                         // tracking space: the virtual hand (the weapon's grip frame)
            public Quaternion R = Quaternion.identity;
            public Vector3 W;                            // angular velocity, rad/s (tracking)
            public Vector3 TP, TV;                       // the target (the lagged hand) and its velocity
            public Quaternion TR = Quaternion.identity;
            public Vector3 TW;
            public bool HasTarget;
            public float Lag;
            public float SoftAt = -10f;
            public readonly List<Sample> Hist = new List<Sample>();
        }

        sealed class Rest { public Vector3 Pos, LastPos; public Quaternion Rot, LastRot; public bool Driven; }

        readonly Follow[] m_f = { new Follow(), new Follow() };   // [0] right, [1] left
        readonly Dictionary<int, Rest> m_rest = new Dictionary<int, Rest>();
        bool m_offHeld;          // the off hand holds the haft
        bool m_offLeft;
        float m_haftSign = 1f;   // +1: the off hand toward the tip

        void Awake()
        {
            I = this;
            Diag.Commands["weaponfeel?"] = a =>
            {
                if (a.Length > 1 && a[1] == "reset") { MaxOffset = MaxLag = MaxSpeed = 0f; Impacts = Grabs = Releases = 0; SpringsMin = 1f; return "reset"; }
                return Describe();
            };
            Diag.Commands["twohand?"] = a => TwoHandQuery();
            // `haft? [s]`: the xrsim command that puts the free hand's grip on the haft `s` m from the weapon hand's grip (the
            // controller unrotated) - for the tests, as `guardat` for blocks
            Diag.Commands["haft?"] = a =>
            {
                float at = 0.3f;
                if (a.Length > 1) float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out at);
                bool lh = Settings.LeftHanded;
                Hand main = lh ? VRInput.L : VRInput.R;
                Vector3 g, t;
                if (!Haft(lh, out g, out t)) return "no weapon";
                Vector3 gT = main.Pos + main.Rot * g, d = (main.Rot * (t - g)).normalized;
                Vector3 ctrl = gT + d * at - ControllerGeometry.GripPos(!lh);
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                // xrsim speaks OpenXR (z toward the player): the tracking z negated (BA2's first try put the hand 1.1 m off)
                return string.Format(inv, "hand {0} {1:0.###} {2:0.###} {3:0.###} 0 0 0 | gripT {4} dirT {5}", lh ? "r" : "l", ctrl.x, ctrl.y, -ctrl.z, Diag.V(gT), Diag.V(d));
            };
        }

        static bool On => CombatSettings.WeaponPhysics;

        /// Can the current weapon be held in two hands? UYF's data (its Grabbable.canBeTwoHanded): only its three ultra-heavy
        /// weapons - the greatsword, the heavy axe, the hammer - which HoF2's two-handed swords, axes and maces play as
        /// (WeaponTable); a two-handed polearm plays as UYF's heavy longsword, one-handed there.
        public static bool TwoHander()
        {
            try
            {
                return PlayerController.Instance != null && PlayerController.Instance.WeaponHandedness == Handedness.TwoHanded && WeaponTable.Current().TwoHand;
            }
            catch (Exception) { return false; }
        }

        /// Does the follow draw this hand's weapon now (else it sits on the controller as placed)?
        public static bool Drives(bool left) => I != null && Physical.Live && I.m_f[left ? 1 : 0].Has;

        /// The weapon hand's pose as drawn (tracking space) - what BladeHits measures the blade from. False when the
        /// weapon follows the controller exactly (physics off, no two-handed hold, not in a live fight).
        public static bool Pose(bool left, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            if (I == null || !Physical.Live) return false;
            Follow f = I.m_f[left ? 1 : 0];
            if (!f.Has) return false;
            pos = f.P;
            rot = f.R;
            return true;
        }

        /// A point of a held model in its hand's frame at the model's rest pose (where CombatVR placed it), whatever the
        /// lag offset drawn now.
        public static Vector3 HandLocal(Transform model, Vector3 modelPoint)
        {
            Rest r;
            if (I != null && model != null && I.m_rest.TryGetValue(model.GetInstanceID(), out r) && r.Driven)
                return r.Pos + r.Rot * Vector3.Scale(modelPoint, model.localScale);
            return model.parent != null ? model.parent.InverseTransformPoint(model.TransformPoint(modelPoint)) : model.TransformPoint(modelPoint);
        }

        /// A knock on the weapon (UYF's SpringImpact): a blow blocked with it, or its hit turned aside by a raised shield.
        /// `dirT` along the blow (tracking space), `impulse` UYF's 5 for a block.
        public static void Impact(bool left, Vector3 dirT, float impulse)
        {
            if (I == null) return;
            Follow f = I.m_f[left ? 1 : 0];
            if (!f.Has) return;
            Impacts++;
            // a weapon of ~1.5 kg: 5 N s -> 3.3 m/s at the grip frame, and a turn about the axis square to the blade and the blow
            Vector3 d = dirT.sqrMagnitude > 1e-6f ? dirT.normalized : Vector3.down;
            f.V += d * (impulse / 1.5f);
            Vector3 blade = f.R * Vector3.forward;
            Vector3 ax = Vector3.Cross(blade, d);
            if (ax.sqrMagnitude > 1e-6f) f.W += ax.normalized * (impulse * 2.4f);
            f.SoftAt = Time.unscaledTime;
        }

        static float Soft(Follow f)
        {
            float x = (Time.unscaledTime - f.SoftAt) / SoftTime;
            return x >= 1f ? 1f : Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(x));
        }

        void LateUpdate()
        {
            ClaimedThisPress = false;
            bool live = Physical.Live && PlayerController.Instance != null && CombatVR.I != null && VRRig.I != null;
            if (!live)
            {
                Reset();
                return;
            }
            PlayerController pc = PlayerController.Instance;
            Inventory inv = pc.Inventory;
            bool leftHanded = Settings.LeftHanded;
            bool twoHander = TwoHander();
            m_offLeft = !leftHanded;
            Hand main = leftHanded ? VRInput.L : VRInput.R, off = leftHanded ? VRInput.R : VRInput.L;
            bool shieldInOff = inv != null && inv.Shield != null && inv.Shield.Model != null && inv.Shield.Model != pc.Model;
            float now = Time.unscaledTime, dt = Mathf.Clamp(Time.unscaledDeltaTime, 0.001f, 0.05f);
            WeaponSpec spec = WeaponTable.Current();

            // the two-handed grip
            if (!twoHander || shieldInOff || !off.Tracked || !main.Tracked) { if (m_offHeld) { m_offHeld = false; Releases++; } }
            else if (m_offHeld && !off.GripBtn) { m_offHeld = false; Releases++; }
            else if (!m_offHeld && off.GripDown && NearHaft(main, off, leftHanded, out m_haftSign)) { m_offHeld = true; Grabs++; ClaimedThisPress = true; }
            TwoHanded = m_offHeld;

            for (int side = 0; side < 2; side++)
            {
                bool left = side == 1;
                Follow f = m_f[side];
                Hand h = left ? VRInput.L : VRInput.R;
                bool weaponHand = HoldsWeapon(left);
                if (!weaponHand || !h.Tracked) { f.Has = false; f.HasTarget = false; f.Hist.Clear(); continue; }
                // the hand's pose, turned along the line between the hands while held in two
                Vector3 hp = h.Pos;
                Quaternion hr = h.Rot;
                if (m_offHeld && left == leftHanded) TwoHandPose(h, off, left, ref hp, ref hr);
                if (f.Hist.Count > 0 && (hp - f.Hist[f.Hist.Count - 1].P).magnitude > JumpSnap)
                {
                    f.Hist.Clear();
                    f.Has = false;
                    f.HasTarget = false;
                    f.Lag = 0f;
                    Snaps++;
                }
                f.Hist.Add(new Sample { T = now, P = hp, R = hr });
                while (f.Hist.Count > 2 && now - f.Hist[1].T > 0.4f) f.Hist.RemoveAt(0);
                if (!On)
                {
                    // physics off: the weapon follows the controller; only the two-handed turn is drawn
                    f.Has = m_offHeld && left == leftHanded;
                    f.P = hp; f.R = hr; f.V = Vector3.zero; f.W = Vector3.zero; f.HasTarget = false;
                    continue;
                }
                // the history lag (UYF's SpringConfig): grows with the hand's speed over the weapon's range along its curve, shrinks
                // again at its catch-up rate; in two hands UYF's _2H config (50 ms, nearly full from 2 m/s)
                bool two = m_offHeld;
                float maxLag = two ? spec.Lag2H : spec.Lag, from = two ? 0f : spec.LagFrom, to = two ? 4f : spec.LagTo;
                float pow = two ? spec.LagPow2H : spec.LagPow, catchup = two ? spec.Catchup2H : spec.Catchup;
                float speed = h.Vel.magnitude;
                if (speed < 1e-3f && f.Hist.Count >= 2) { Sample a = f.Hist[f.Hist.Count - 2]; speed = (hp - a.P).magnitude / Mathf.Max(1e-3f, now - a.T); }
                float x = Mathf.Clamp01((speed - from) / Mathf.Max(0.1f, to - from));
                float want = maxLag * Mathf.Pow(x, Mathf.Max(0.05f, pow));
                f.Lag = want >= f.Lag ? want : Mathf.Max(want, f.Lag - catchup * dt);
                LastLag = f.Lag;
                if (f.Lag > MaxLag) MaxLag = f.Lag;
                if (speed > MaxSpeed) MaxSpeed = speed;
                Vector3 tp;
                Quaternion tr;
                At(f, now - f.Lag, out tp, out tr);
                if (!f.HasTarget) { f.TP = tp; f.TR = tr; f.TV = Vector3.zero; f.TW = Vector3.zero; f.HasTarget = true; }
                f.TV = (tp - f.TP) / dt;
                Quaternion dq = tr * Quaternion.Inverse(f.TR);
                float dang;
                Vector3 dax;
                dq.ToAngleAxis(out dang, out dax);
                if (dang > 180f) dang -= 360f;
                f.TW = Mathf.Abs(dang) > 1e-3f && !float.IsNaN(dax.x) ? dax.normalized * (dang * Mathf.Deg2Rad / dt) : Vector3.zero;
                f.TP = tp;
                f.TR = tr;
                if (!f.Has) { f.P = tp; f.R = tr; f.V = f.TV; f.W = f.TW; f.Has = true; }
                // the springs (UYF's joint drives): critically damped on the difference of velocities, 4 steps a frame
                float soft = Soft(f);
                if (soft < SpringsMin && left == leftHanded) SpringsMin = soft;
                float w = Omega * Mathf.Sqrt(soft);
                float step = dt / 4f;
                for (int k = 0; k < 4; k++)
                {
                    Vector3 acc = w * w * (tp - f.P) + 2f * Zeta * w * (f.TV - f.V);
                    f.V += acc * step;
                    f.P += f.V * step;
                    Quaternion e = tr * Quaternion.Inverse(f.R);
                    float ang;
                    Vector3 ax;
                    e.ToAngleAxis(out ang, out ax);
                    if (ang > 180f) ang -= 360f;
                    Vector3 errV = Mathf.Abs(ang) > 1e-3f && !float.IsNaN(ax.x) ? ax.normalized * (ang * Mathf.Deg2Rad) : Vector3.zero;
                    Vector3 alpha = w * w * errV + 2f * Zeta * w * (f.TW - f.W);
                    f.W += alpha * step;
                    float wm = f.W.magnitude;
                    if (wm > 1e-4f) f.R = Quaternion.AngleAxis(wm * step * Mathf.Rad2Deg, f.W / wm) * f.R;
                }
                // UYF's grip limit: the weapon stays within 0.2 m of the (lagged) hand
                Vector3 off2 = f.P - tp;
                if (off2.magnitude > GripLimit) { f.P = tp + off2.normalized * GripLimit; f.V = f.TV; }
                if (float.IsNaN(f.P.x) || float.IsNaN(f.R.x)) { f.P = tp; f.R = tr; f.V = Vector3.zero; f.W = Vector3.zero; }
                LastOffset = (f.P - h.Pos).magnitude;
                if (LastOffset > MaxOffset) MaxOffset = LastOffset;
            }
            Draw(inv, pc);
        }

        /// The lagged pose: the hand's pose `t` (unscaled time) from the history, interpolated.
        static void At(Follow f, float t, out Vector3 p, out Quaternion r)
        {
            List<Sample> h = f.Hist;
            Sample last = h[h.Count - 1];
            if (t >= last.T || h.Count == 1) { p = last.P; r = last.R; return; }
            for (int i = h.Count - 2; i >= 0; i--)
            {
                Sample a = h[i], b = h[i + 1];
                if (t >= a.T)
                {
                    float u = b.T > a.T ? (t - a.T) / (b.T - a.T) : 1f;
                    p = Vector3.Lerp(a.P, b.P, u);
                    r = Quaternion.Slerp(a.R, b.R, u);
                    return;
                }
            }
            p = h[0].P;
            r = h[0].R;
        }

        /// Does this hand hold a weapon model now (CombatVR's held models)?
        static bool HoldsWeapon(bool left)
        {
            PlayerController pc = PlayerController.Instance;
            CombatVR vr = CombatVR.I;
            if (pc == null || vr == null || pc.Inventory == null || pc.Inventory.Weapon == null || pc.Inventory.Weapon.AllModels == null) return false;
            Transform hand = left ? vr.HandL : vr.HandR;
            foreach (Model m in pc.Inventory.Weapon.AllModels) if (m != null && m.transform.parent == hand) return true;
            return false;
        }

        /// The weapon's grip and tip in its hand's frame (rest pose): the haft's line.
        static bool Haft(bool left, out Vector3 grip, out Vector3 tip)
        {
            grip = ControllerGeometry.GripPos(left);
            tip = grip + Vector3.forward;
            PlayerController pc = PlayerController.Instance;
            CombatVR vr = CombatVR.I;
            if (pc == null || vr == null || pc.Inventory == null || pc.Inventory.Weapon == null) return false;
            Transform hand = left ? vr.HandL : vr.HandR;
            foreach (Model m in pc.Inventory.Weapon.AllModels)
            {
                if (m == null || m.transform.parent != hand) continue;
                Bounds lb = CombatVR.LocalBounds(m.transform);
                Vector3 lng, mid, thin;
                CombatVR.Axes(lb, out lng, out mid, out thin);
                float half = 0.5f * Mathf.Abs(Vector3.Dot(lb.size, lng));
                Vector3 a = HandLocal(m.transform, lb.center + lng * half), b = HandLocal(m.transform, lb.center - lng * half);
                // the tip: the end farther from the grip
                tip = (a - grip).sqrMagnitude >= (b - grip).sqrMagnitude ? a : b;
                return true;
            }
            return false;
        }

        /// Is the off hand's grip point within GrabRadius of the haft (from just behind the pommel to 75 % of the way to the
        /// tip)? sign: +1 when it is toward the tip from the weapon hand.
        static bool NearHaft(Hand main, Hand off, bool leftHanded, out float sign)
        {
            sign = 1f;
            Vector3 g, t;
            if (!Haft(leftHanded, out g, out t)) return false;
            Vector3 gT = main.Pos + main.Rot * g, tT = main.Pos + main.Rot * t;
            Vector3 o = off.Pos + off.Rot * ControllerGeometry.GripPos(!leftHanded);
            Vector3 d = tT - gT;
            float len = d.magnitude;
            if (len < 0.05f) return false;
            d /= len;
            float s = Vector3.Dot(o - gT, d);
            if (s < -0.25f || s > 0.75f * len) return false;
            float perp = (o - gT - d * s).magnitude;
            if (perp > GrabRadius) return false;
            sign = s >= 0f ? 1f : -1f;
            return true;
        }

        /// The weapon hand's pose turned so the weapon points along the line between the hands, about its grip point.
        void TwoHandPose(Hand main, Hand off, bool left, ref Vector3 pos, ref Quaternion rot)
        {
            Vector3 g, t;
            if (!Haft(left, out g, out t)) return;
            Vector3 gripT = main.Pos + main.Rot * g;
            Vector3 o = off.Pos + off.Rot * ControllerGeometry.GripPos(!left);
            Vector3 want = (o - gripT) * m_haftSign;
            if (want.magnitude < 0.05f) return;
            Vector3 now = main.Rot * (t - g);
            Quaternion turn = Quaternion.FromToRotation(now.normalized, want.normalized);
            rot = turn * main.Rot;
            pos = gripT - rot * g;
        }

        /// Draw the weapon models where the follow put them: each model's local pose under its hand = the offset of the
        /// drawn pose from the controller, applied to the rest pose CombatVR placed it at.
        void Draw(Inventory inv, PlayerController pc)
        {
            if (inv == null || inv.Weapon == null || inv.Weapon.AllModels == null) return;
            CombatVR vr = CombatVR.I;
            foreach (Model m in inv.Weapon.AllModels)
            {
                if (m == null) continue;
                Transform t = m.transform;
                bool left = t.parent == vr.HandL;
                if (!left && t.parent != vr.HandR) continue;
                int id = t.GetInstanceID();
                Rest r;
                if (!m_rest.TryGetValue(id, out r)) { r = new Rest { Pos = t.localPosition, Rot = t.localRotation }; m_rest[id] = r; }
                else if (!r.Driven || t.localPosition != r.LastPos || t.localRotation != r.LastRot) { r.Pos = t.localPosition; r.Rot = t.localRotation; }   // CombatVR placed it anew
                Follow f = m_f[left ? 1 : 0];
                Hand h = left ? VRInput.L : VRInput.R;
                if (!f.Has)
                {
                    if (r.Driven) { t.localPosition = r.Pos; t.localRotation = r.Rot; r.Driven = false; }
                    continue;
                }
                Quaternion inv0 = Quaternion.Inverse(h.Rot);
                Vector3 dp = inv0 * (f.P - h.Pos);
                Quaternion dr = inv0 * f.R;
                t.localPosition = dp + dr * r.Pos;
                t.localRotation = dr * r.Rot;
                r.LastPos = t.localPosition;
                r.LastRot = t.localRotation;
                r.Driven = true;
            }
        }

        void Reset()
        {
            TwoHanded = false;
            m_offHeld = false;
            foreach (Follow f in m_f) { f.Has = false; f.HasTarget = false; f.Hist.Clear(); f.Lag = 0f; }
            // models still driven go back to their rest pose
            try
            {
                PlayerController pc = PlayerController.Instance;
                if (pc != null && pc.Inventory != null && pc.Inventory.Weapon != null && pc.Inventory.Weapon.AllModels != null)
                    foreach (Model m in pc.Inventory.Weapon.AllModels)
                    {
                        Rest r;
                        if (m != null && m_rest.TryGetValue(m.transform.GetInstanceID(), out r) && r.Driven)
                        {
                            m.transform.localPosition = r.Pos;
                            m.transform.localRotation = r.Rot;
                            r.Driven = false;
                        }
                    }
            }
            catch (Exception) { }
            if (m_rest.Count > 50) m_rest.Clear();
        }

        /// `twohand?`: the grip's state, the leeway, and how far the drawn weapon is from the line between the hands.
        static string TwoHandQuery()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            bool lh = Settings.LeftHanded;
            Hand main = lh ? VRInput.L : VRInput.R, off = lh ? VRInput.R : VRInput.L;
            string head = "two-handed " + TwoHanded + " grabs " + Grabs + " releases " + Releases + " leeway " + WeaponTable.CritLeeway().ToString("0", inv) +
                          " two-hander " + TwoHander() + " (" + WeaponTable.Current().Name + ")";
            Vector3 g, t;
            if (I == null || !Haft(lh, out g, out t)) return head + " | no weapon";
            Vector3 gT = main.Pos + main.Rot * g, oT = off.Pos + off.Rot * ControllerGeometry.GripPos(!lh);
            Follow f = I.m_f[lh ? 1 : 0];
            Vector3 drawn = (f.Has ? f.R : main.Rot) * (t - g);
            Vector3 want = (oT - gT) * I.m_haftSign;
            return head + " | apart " + (oT - gT).magnitude.ToString("0.00", inv) + " m, the drawn weapon " + Vector3.Angle(drawn, want).ToString("0.0", inv) +
                   " deg off the line between the hands";
        }

        static string Describe()
        {
            WeaponSpec s = WeaponTable.Current();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return "weapon physics " + (CombatSettings.WeaponPhysics ? "on" : "off") + " | " + s.Name + " lag max " + (s.Lag * 1000f).ToString("0", inv) + " ms (two hands " +
                   (s.Lag2H * 1000f).ToString("0", inv) + ") | lag now " + (LastLag * 1000f).ToString("0", inv) + " ms (max " + (MaxLag * 1000f).ToString("0.0", inv) +
                   " ms at hand speeds up to " + MaxSpeed.ToString("0.0", inv) + " m/s), offset " + LastOffset.ToString("0.000", inv) + " m (max " +
                   MaxOffset.ToString("0.000", inv) + ") | two-handed " + TwoHanded + " grabs " + Grabs + " releases " + Releases + " leeway " +
                   WeaponTable.CritLeeway().ToString("0", inv) + " | impacts " + Impacts +
                   (I != null ? " springs " + Soft(I.m_f[Settings.LeftHanded ? 1 : 0]).ToString("0.00", inv) : "") + " springsMin " + SpringsMin.ToString("0.00", inv);
        }
    }
}

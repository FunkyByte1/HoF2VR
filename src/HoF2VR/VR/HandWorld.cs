using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// World-space hand data for physical interaction (dice, table gestures, touch): palm and
    /// fingertip positions, velocities and a short motion history (throws). Poses come from VRInput
    /// (tracking space) through VRRig (world). At the table the rig never moves by itself, so a world
    /// velocity is the hand's own motion times the room scale.
    internal static class HandWorld
    {
        /// The extended index finger reaches this far (m) ahead of the controller's pointer tip.
        public const float FingerReach = 0.055f;

        public struct Sample { public float T; public Vector3 Palm; public Vector3 Vel; }

        sealed class Track
        {
            public readonly Sample[] Buf = new Sample[96];
            public int Count, Head;
            public int Frame = -1;
            public bool Valid;
            public Vector3 Palm, Tip, Origin, Vel, AngVel;
            public Quaternion Rot = Quaternion.identity;
            public Vector3 Anchor;
            public float AnchorYaw, Scale;
            public int RigVersion = -1;
        }

        static readonly Track s_l = new Track(), s_r = new Track();
        static Track Of(Hand h) => h.IsLeft ? s_l : s_r;

        /// Controller-frame offset of the fingertip (pointer tip plus the extended finger).
        public static Vector3 TipLocal(bool left) =>
            ControllerGeometry.TipPos(left) + Quaternion.Euler(ControllerGeometry.TipPitchDown, 0f, 0f) * Vector3.forward * FingerReach;

        /// Controller-frame offset of the palm (the fist centre round the grip).
        public static Vector3 PalmLocal(bool left) => ControllerGeometry.GripPos(left);

        static Track Get(Hand h)
        {
            Track t = Of(h);
            if (t.Frame == Time.frameCount) return t;
            t.Frame = Time.frameCount;
            VRInput.Update();
            VRRig rig = VRRig.I;
            t.Valid = rig != null && h.Tracked && XRSettings.enabled;
            if (!t.Valid) return t;
            t.Rot = rig.ToWorld(h.Rot);   // (also brings the rig's anchor up to date)
            // a new station (place change, recenter) moves the hand in the world without any motion:
            // the history must not read that as one
            if (rig.AnchorPos != t.Anchor || rig.AnchorYaw != t.AnchorYaw || rig.Scale != t.Scale || rig.Version != t.RigVersion)
            {
                t.RigVersion = rig.Version;
                t.Anchor = rig.AnchorPos;
                t.AnchorYaw = rig.AnchorYaw;
                t.Scale = rig.Scale;
                t.Count = 0;
            }
            t.Origin = rig.ToWorld(h.Pos);
            t.Palm = rig.ToWorld(h.Pos + h.Rot * PalmLocal(h.IsLeft));
            t.Tip = rig.ToWorld(h.Pos + h.Rot * TipLocal(h.IsLeft));
            t.AngVel = rig.ToWorldDir(h.AngVel);
            // the palm's own velocity: the controller's plus the swing of the 10-13 cm lever round it
            t.Vel = rig.ToWorldDir(h.Vel) * rig.Scale + Vector3.Cross(t.AngVel, t.Palm - t.Origin);
            float now = Time.unscaledTime;
            t.Head = (t.Head + 1) % t.Buf.Length;
            t.Buf[t.Head] = new Sample { T = now, Palm = t.Palm, Vel = t.Vel };
            if (t.Count < t.Buf.Length) t.Count++;
            return t;
        }

        /// Updates both hands (VRCore calls it every frame, so the history has no gaps).
        public static void Tick()
        {
            Get(VRInput.L);
            Get(VRInput.R);
        }

        public static bool Valid(Hand h) => Get(h).Valid;
        public static Vector3 Palm(Hand h) => Get(h).Palm;
        public static Vector3 Tip(Hand h) => Get(h).Tip;
        public static Quaternion Rot(Hand h) => Get(h).Rot;
        /// Palm velocity, world units per second.
        public static Vector3 Vel(Hand h) => Get(h).Vel;
        public static Vector3 AngVel(Hand h) => Get(h).AngVel;
        /// The pointing ray (the laser's: from the pointer tip, pitched down on Touch).
        public static Ray PointRay(Hand h)
        {
            Track t = Get(h);
            Quaternion r = t.Rot * Quaternion.Euler(ControllerGeometry.TipPitchDown + VRPointer.PitchOffset, 0f, 0f);
            Vector3 o = VRRig.I != null ? VRRig.I.ToWorld(h.Pos + h.Rot * ControllerGeometry.TipPos(h.IsLeft)) : t.Tip;
            return new Ray(o, r * Vector3.forward);
        }

        /// Mean palm velocity over the last `window` seconds (at least the two newest samples), world u/s.
        public static Vector3 MeanVel(Hand h, float window)
        {
            Track t = Get(h);
            if (t.Count == 0) return Vector3.zero;
            float now = Time.unscaledTime;
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int i = 0; i < t.Count; i++)
            {
                Sample s = t.Buf[(t.Head - i + t.Buf.Length) % t.Buf.Length];
                if (n >= 2 && now - s.T > window) break;
                sum += s.Vel;
                n++;
            }
            return n > 0 ? sum / n : Vector3.zero;
        }

        /// Fastest palm speed in the last `window` seconds (world u/s) and its velocity.
        public static Vector3 PeakVel(Hand h, float window)
        {
            Track t = Get(h);
            float now = Time.unscaledTime;
            Vector3 best = Vector3.zero;
            for (int i = 0; i < t.Count; i++)
            {
                Sample s = t.Buf[(t.Head - i + t.Buf.Length) % t.Buf.Length];
                if (now - s.T > window) break;
                if (s.Vel.sqrMagnitude > best.sqrMagnitude) best = s.Vel;
            }
            return best;
        }

        /// The palm's velocity for a throw at a release (docs/hand-feel-design.md §3.8, Alyx's rule): in the last `window`
        /// seconds the fastest moment, averaged with its two neighbours. The grip opens over a few frames while the hand is
        /// already slowing, so a plain mean of the last 60 ms came out about a quarter short. The velocities are taken from
        /// the palm's own positions over two frames (the per-frame velocity is smoothed and trails a quick throw by ~10 %);
        /// the larger of that and the runtime's velocity at the peak wins.
        public static Vector3 ThrowVel(Hand h, float window = 0.1f)
        {
            Track t = Get(h);
            if (t.Count < 3) return t.Count > 0 ? t.Buf[t.Head].Vel : Vector3.zero;
            float now = Time.unscaledTime;
            int n = 0;
            for (int i = 0; i < t.Count - 2; i++)
            {
                if (i > 0 && now - t.Buf[(t.Head - i + t.Buf.Length) % t.Buf.Length].T > window) break;
                n = i + 1;
            }
            int best = -1;
            float bestSq = -1f;
            for (int i = 0; i < n; i++)
            {
                float sq = RawVel(t, i).sqrMagnitude;
                if (sq > bestSq) { bestSq = sq; best = i; }
            }
            if (best < 0) return Vector3.zero;
            Vector3 sum = Vector3.zero, smooth = Vector3.zero;
            int k = 0;
            for (int j = best - 1; j <= best + 1; j++)
            {
                if (j < 0 || j >= t.Count - 2) continue;
                sum += RawVel(t, j);
                smooth += t.Buf[(t.Head - j + t.Buf.Length) % t.Buf.Length].Vel;
                k++;
            }
            if (k == 0) return Vector3.zero;
            sum /= k;
            smooth /= k;
            return smooth.sqrMagnitude > sum.sqrMagnitude ? smooth : sum;
        }

        /// The last `n` palm speeds (real m/s, two-frame positions | the per-frame velocity) with their age in ms, for the throw log.
        public static string SpeedTrace(Hand h, int n = 10)
        {
            Track t = Get(h);
            var sb = new System.Text.StringBuilder();
            float now = Time.unscaledTime, sc = t.Scale > 0f ? t.Scale : 1f;
            for (int i = 0; i < n && i < t.Count - 2; i++)
            {
                Sample s = t.Buf[(t.Head - i + t.Buf.Length) % t.Buf.Length];
                sb.AppendFormat(CultureInfo.InvariantCulture, " -{0:0}ms {1:0.00}|{2:0.00}", (now - s.T) * 1000f, RawVel(t, i).magnitude / sc, s.Vel.magnitude / sc);
            }
            return sb.ToString();
        }

        /// The palm's velocity at ring sample i (0 = now) from its positions two frames apart.
        static Vector3 RawVel(Track t, int i)
        {
            Sample a = t.Buf[(t.Head - i + t.Buf.Length) % t.Buf.Length], b = t.Buf[(t.Head - i - 2 + 2 * t.Buf.Length) % t.Buf.Length];
            float dt = a.T - b.T;
            return dt > 1e-4f ? (a.Palm - b.Palm) / dt : Vector3.zero;
        }

        /// How far the palm moved in the last `window` seconds (world units).
        public static Vector3 Displacement(Hand h, float window)
        {
            Track t = Get(h);
            if (t.Count < 2) return Vector3.zero;
            float now = Time.unscaledTime;
            Vector3 oldest = t.Palm;
            for (int i = 1; i < t.Count; i++)
            {
                Sample s = t.Buf[(t.Head - i + t.Buf.Length) % t.Buf.Length];
                if (now - s.T > window) break;
                oldest = s.Palm;
            }
            return t.Palm - oldest;
        }

        /// Highest palm y in the last `window` seconds (slap: the hand came from above).
        public static float MaxY(Hand h, float window)
        {
            Track t = Get(h);
            float now = Time.unscaledTime, best = float.MinValue;
            for (int i = 0; i < t.Count; i++)
            {
                Sample s = t.Buf[(t.Head - i + t.Buf.Length) % t.Buf.Length];
                if (i > 0 && now - s.T > window) break;
                best = Mathf.Max(best, s.Palm.y);
            }
            return best;
        }

        /// The previous frame's palm (for plane crossings).
        public static bool PrevPalm(Hand h, out Vector3 palm)
        {
            Track t = Get(h);
            palm = t.Palm;
            if (t.Count < 2) return false;
            palm = t.Buf[(t.Head - 1 + t.Buf.Length) % t.Buf.Length].Palm;
            return true;
        }

        static Hand Side(string s) => s != null && s.StartsWith("l", StringComparison.OrdinalIgnoreCase) ? VRInput.L : VRInput.R;
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        /// Named world points tests can send a hand to (`handto r palm dice`). Other modules add theirs.
        public static readonly Dictionary<string, Func<Vector3?>> Targets = new Dictionary<string, Func<Vector3?>>(StringComparer.OrdinalIgnoreCase);

        public static void Register()
        {
            // `finger [l|r]`: where the hand is in the world (palm, fingertip), how fast it moves
            Diag.Commands["finger"] = a =>
            {
                Hand h = Side(a.Length > 1 ? a[1] : "r");
                Track t = Get(h);
                if (!t.Valid) return (h.IsLeft ? "L" : "R") + " untracked";
                float s = VRRig.I != null ? VRRig.I.Scale : 1f;
                Vector3 eye = VRRig.I != null ? VRRig.I.NeutralEye : Vector3.zero;
                return string.Format(CultureInfo.InvariantCulture, "{0}: palm {1} tip {2} vel {3} ({4:0.00} m/s) palm-eye {5:0.00} m grip={6:0.0} trig={7:0.0} ttouch={8}",
                    h.IsLeft ? "L" : "R", Diag.V(t.Palm), Diag.V(t.Tip), Diag.V(t.Vel), t.Vel.magnitude / s, Vector3.Distance(t.Palm, eye) / s, h.Grip, h.Trigger, h.TriggerTouch);
            };
            // `handto l|r palm|tip <x y z | target name>`: the xrsim relative move (OpenXR stage metres)
            // that brings the simulated controller's palm / fingertip onto that world point
            Diag.Commands["handto"] = a =>
            {
                if (a.Length < 4 || VRRig.I == null) return "usage: handto l|r palm|tip <x y z | " + string.Join("|", new List<string>(Targets.Keys).ToArray()) + ">";
                Hand h = Side(a[1]);
                if (!h.Tracked) return "hand untracked";
                bool tip = a[2].StartsWith("t", StringComparison.OrdinalIgnoreCase);
                Vector3 world;
                if (a.Length >= 6) world = new Vector3(F(a[3]), F(a[4]), F(a[5]));
                else
                {
                    Func<Vector3?> f;
                    Vector3? p = Targets.TryGetValue(a[3], out f) ? f() : null;
                    if (p == null) return "no target '" + a[3] + "' right now";
                    world = p.Value;
                }
                // the fingertip is the one touch uses (the glove's index tip when the gloves are shown)
                Vector3 nowWorld = tip ? TouchInput.TipOf(h) : Palm(h);
                Vector3 d = VRRig.I.FromWorldDir(world - nowWorld) / VRRig.I.Scale;
                // Unity tracking space = OpenXR stage with z mirrored
                return string.Format(CultureInfo.InvariantCulture, "move {0} {1:0.####} {2:0.####} {3:0.####}", h.IsLeft ? "l" : "r", d.x, d.y, -d.z);
            };
            // `aim l|r <x y z | target>`: xrsim angles (yaw pitch roll, OpenXR) that point that hand's
            // laser at the point from where the hand is now (use with xrsim `rot`)
            Diag.Commands["aim"] = a =>
            {
                if (a.Length < 3 || VRRig.I == null) return "usage: aim l|r <x y z | target>";
                Hand h = Side(a[1]);
                if (!h.Tracked) return "hand untracked";
                Vector3 world;
                if (a.Length >= 5) world = new Vector3(F(a[2]), F(a[3]), F(a[4]));
                else
                {
                    Func<Vector3?> f;
                    Vector3? p = Targets.TryGetValue(a[2], out f) ? f() : null;
                    if (p == null) return "no target '" + a[2] + "' right now";
                    world = p.Value;
                }
                Ray ray = PointRay(h);
                Vector3 d = VRRig.I.FromWorldDir(world - ray.origin).normalized;   // tracking space (Unity)
                Vector3 xr = new Vector3(d.x, d.y, -d.z);                         // OpenXR stage
                float pitch = Mathf.Asin(Mathf.Clamp(xr.y, -1f, 1f)) * Mathf.Rad2Deg + ControllerGeometry.TipPitchDown + VRPointer.PitchOffset;
                float yaw = Mathf.Atan2(-xr.x, -xr.z) * Mathf.Rad2Deg;
                return string.Format(CultureInfo.InvariantCulture, "rot {0} {1:0.##} {2:0.##} 0", h.IsLeft ? "l" : "r", yaw, pitch);
            };
            Targets["eye"] = () => VRRig.I != null ? VRRig.I.NeutralEye : (Vector3?)null;
            // `where <name>`: the top centre of the touchable collider whose path contains <name> (x y z)
            Diag.Commands["where"] = a =>
            {
                string name = string.Join(" ", a, 1, a.Length - 1);
                Collider best = null;
                foreach (Collider c in UnityEngine.Object.FindObjectsOfType<Collider>())
                {
                    if (!c.enabled || !c.gameObject.activeInHierarchy || c.gameObject.layer == 17) continue;
                    string path = VRUI.Path(c.transform);
                    if (path.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0 || c.GetComponentInParent<UISelectable>() == null) continue;
                    if (best == null || path.Length < VRUI.Path(best.transform).Length) best = c;
                }
                if (best == null) return "none";
                Bounds b = best.bounds;
                return string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####} {2:0.####} {3}", b.center.x, b.max.y, b.center.z, VRUI.Path(best.transform));
            };
        }
    }
}

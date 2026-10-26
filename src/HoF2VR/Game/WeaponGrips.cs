using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// Weapon grips (combat Deliverable 1a; docs/combat-uyf-design.md §7.6): weapons are held the way the game's hero
    /// holds them, by construction, instead of a guess from each model's bounds (which gave the mesh's middle axis no
    /// sign: the user's axe was held with its edge sideways, "backwards").
    ///
    /// What the survey found (`gripsurvey`, 2026-10-11, all 102 weapon and shield cards):
    ///  * every weapon model hangs under the hero's "weapon" node (dual wield: also "offhand_weapon") with an identity
    ///    pose, and its blade or haft runs along the node's +Z, from the hand toward the tip or head (80 of 80);
    ///  * the game's own swings lead with the node's -X (`gripcal` over 12 attacks: tip motion -x 91768 vs +x 8061,
    ///    speed-squared weighted), and the one-sided axe heads point to -X (RustedAxe, Executioner's Axe);
    ///  * the HugeHammer's material has no albedo and a magenta colour (FF00E3) in the game's own data.
    /// So in the VR hand: node +Z along the blade (the controller's pointing axis tilted up by the grip pitch), node -X
    /// toward the trigger side (where a downward chop moves the head), node +X toward the thumb; the mesh's grip (the
    /// handle under a sword's guard; the game's hand point on a haft) in the fist. Per-model exceptions: Table.
    /// Shields keep CombatVR's face/handle placement.
    ///
    /// Seams:
    ///  `gripsurvey [id part]`  every weapon and shield card: the equipment's model, its attach node and its authored
    ///                          pose under that node (the game's own hold), the mesh in that node's frame (long axis,
    ///                          head side, edge direction, grip), and its materials (shader, supported, main texture)
    ///  `heronodes`             the hero model's attach nodes (name, bone, local pose)
    ///  `gripcal [s]`, `gripcal?`  sample the hero's weapon node during the game's own attacks (press X meanwhile)
    ///  `griptest [id part]`    every weapon model in both hands: grip in the fist, striking part beyond it, edges toward
    ///                          the chop, no magenta (static and rendered); the held models against the table
    ///  `gripshow <id>|off`     a fresh model of that card in the weapon hand instead of the held one (for shots)
    internal static class WeaponGrips
    {
        public static void Register()
        {
            Diag.Commands["gripsurvey"] = a => Survey(a.Length > 1 ? a[1] : null);
            Diag.Commands["heronodes"] = a => HeroNodes();
            Diag.Commands["griptest"] = a => Test(a.Length > 1 ? a[1] : null);
            Diag.Commands["gripshow"] = a => Show(a.Length > 1 ? a[1] : "off");
            // `gripcal [seconds]`: while the hero's own attacks play (press X meanwhile), which way the weapon node's
            // tip moves in the node's frame: the side that leads the game's swings is the striking side
            Diag.Commands["gripcal"] = a =>
            {
                float s = a.Length > 1 ? float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture) : 6f;
                GripCal.Until = Time.unscaledTime + s;
                GripCal.Reset();
                return "sampling the weapon node for " + s + " s";
            };
            Diag.Commands["gripcal?"] = a => GripCal.Report();
        }

        /// The game's own swings, seen from the hero's weapon attach node (see `gripcal`).
        internal static class GripCal
        {
            public static float Until;
            static Vector3 s_prevTip, s_sum;
            static bool s_have;
            static int s_frames, s_fast;
            static float s_weight;
            static readonly float[] s_bins = new float[6];   // +x -x +y -y +z -z, speed-weighted

            public static void Reset()
            {
                s_have = false; s_sum = Vector3.zero; s_frames = s_fast = 0; s_weight = 0f;
                for (int i = 0; i < 6; i++) s_bins[i] = 0f;
            }

            public static void Tick()
            {
                if (Time.unscaledTime > Until) { s_have = false; return; }
                PlayerController pc = PlayerController.Instance;
                Transform node = pc != null && pc.Model != null && pc.Model.HasAttachNode("weapon") ? pc.Model.FindAttachNode("weapon") : null;
                if (node == null || Time.deltaTime <= 0f) return;
                Vector3 tip = node.position + node.forward * 0.8f;
                if (s_have)
                {
                    Vector3 v = (tip - s_prevTip) / Time.deltaTime;
                    s_frames++;
                    if (v.magnitude > 2f && v.magnitude < 60f)
                    {
                        Vector3 l = node.InverseTransformDirection(v);
                        l = l.normalized * v.magnitude;
                        s_sum += l;
                        s_weight += v.magnitude;
                        s_fast++;
                        // weighted by speed squared: the strike, not the slower wind-up, decides
                        float k = v.magnitude;
                        s_bins[l.x >= 0 ? 0 : 1] += Mathf.Abs(l.x) * k;
                        s_bins[l.y >= 0 ? 2 : 3] += Mathf.Abs(l.y) * k;
                        s_bins[l.z >= 0 ? 4 : 5] += Mathf.Abs(l.z) * k;
                    }
                }
                s_prevTip = tip;
                s_have = true;
            }

            public static string Report()
            {
                Vector3 m = s_weight > 0f ? s_sum / s_weight : Vector3.zero;
                return "frames " + s_frames + " fast " + s_fast + " mean tip motion (node frame) " + V3(m) +
                       " | +x " + s_bins[0].ToString("0") + " -x " + s_bins[1].ToString("0") + " +y " + s_bins[2].ToString("0") + " -y " + s_bins[3].ToString("0") +
                       " +z " + s_bins[4].ToString("0") + " -z " + s_bins[5].ToString("0");
            }
        }


        // ---------------------------------------------------------------------------------------- the hold

        /// Per-model exceptions (by model name without "(Clone)"). The pose: Slide = metres the weapon moves out of the
        /// fist along the blade; Roll = degrees about the blade; Flip = the other end forward; At = the grip's distance up the
        /// blade from the model's origin (model units), when the mesh search finds the wrong place. The test: FistR = a wider
        /// handle accepted (m); NoEdge = no edge to check. Every entry says which shot it was checked on.
        public struct Grip { public float Slide, Roll, FistR, At; public bool Flip, NoEdge; public string Why; }
        public static readonly Dictionary<string, Grip> Table = new Dictionary<string, Grip>
        {
            { "SacrificialBladesModel", new Grip { At = 0.06f, Why = "the mesh search took a notch half-way up the blade for the guard: the handle is at the end (shot G4_SacrificialBlades)" } },
            { "Player Trap Torch", new Grip { NoEdge = true, Why = "a torch, round: no edge (shot G4_BasicTorch)" } },
            { "LawAndOrderModel", new Grip { NoEdge = true, Why = "a spiked club, round: no edge (checked on shot G3_LawAndOrder)" } },
            { "JackPoisonBladesModel", new Grip { FistR = 0.07f, Why = "an ornate curved handle (shot G3_JackPoisonBlades)" } },
            { "TheWindsReturnModel", new Grip { FistR = 0.12f, Why = "Deatharangs: throwing blades held by the bar across their middle (shot G3_Deatharangs)" } },
        };

        /// The fist closes round a handle at most this thick (radius, m), unless the table accepts more.
        public const float FistRadius = 0.05f;

        /// The node frame (+Z blade, -X striking side) in the controller frame: +Z along the blade direction, +X toward
        /// the controller's top. `tilt` = the blade's pitch up from the pointing axis.
        public static Quaternion Calibration(float tilt)
        {
            Quaternion p = Quaternion.Euler(-tilt, 0f, 0f);
            return Quaternion.LookRotation(p * Vector3.forward, p * Vector3.up) * Quaternion.Inverse(Quaternion.LookRotation(Vector3.forward, Vector3.right));
        }

        public static string ModelKey(string name) => name != null && name.EndsWith("(Clone)") ? name.Substring(0, name.Length - 7) : name ?? "";

        static readonly Dictionary<string, float> s_grip = new Dictionary<string, float>();

        /// Model units from the model's origin along the node's +Z (in model space) to the middle of the grip.
        static float GripOf(Transform t, string key, Vector3 longModel)
        {
            float g;
            if (s_grip.TryGetValue(key, out g)) return g;
            g = VRHands.FindGrip(t, longModel);
            s_grip[key] = g;
            return g;
        }

        /// The pose of a held weapon model in its hand's (controller) frame: the game's hold. `lrot` = its authored
        /// rotation under the hero's node; `scale` = its world scale.
        public static void Pose(Transform t, Quaternion lrot, Vector3 scale, bool left, out Vector3 pos, out Quaternion rot)
        {
            string key = ModelKey(t.name);
            Grip g;
            Table.TryGetValue(key, out g);
            Quaternion c = Calibration(ControllerGeometry.GripPitchUp + CombatVR.SwordTilt);
            Quaternion adj = Quaternion.AngleAxis(g.Roll, Vector3.forward) * (g.Flip ? Quaternion.AngleAxis(180f, Vector3.up) : Quaternion.identity);
            rot = c * adj * lrot;
            Vector3 longModel = Quaternion.Inverse(adj * lrot) * Vector3.forward;   // the blade direction in model space
            float grip = g.At > 0f ? g.At : GripOf(t, key, longModel);
            Vector3 blade = c * Vector3.forward, top = c * Vector3.right;
            pos = ControllerGeometry.GripPos(left) - rot * Vector3.Scale(longModel * grip, scale) + blade * (CombatVR.SwordForward + g.Slide) + top * CombatVR.SwordUp;
        }

        // ---------------------------------------------------------------------------------------- broken materials

        public static int Repaired;

        /// A material the game's data left without its albedo and with a placeholder magenta colour (the HugeHammer:
        /// HugeHammer_Marmoset, FF00E3, no main texture) renders as a magenta weapon. It gets a steel colour (its normal
        /// and metal maps stay). The shared material is changed once, so the hammer looks right everywhere.
        public static int FixMaterials(Transform model)
        {
            int n = 0;
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
                foreach (Material m in r.sharedMaterials)
                    if (IsPlaceholder(m))
                    {
                        Color was = m.GetColor("_Color");
                        m.SetColor("_Color", new Color(0.52f, 0.5f, 0.47f, was.a));
                        n++;
                        Repaired++;
                        Plugin.L.LogInfo("WeaponGrips: material " + m.name + " of " + model.name + " had no albedo and colour " + ColorUtility.ToHtmlStringRGB(was) + " (magenta): steel colour now");
                    }
            return n;
        }

        public static bool IsPlaceholder(Material m)
        {
            if (m == null || !m.HasProperty("_Color")) return false;
            if (m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null) return false;
            Color c = m.GetColor("_Color");
            return c.r > 0.75f && c.b > 0.6f && c.g < 0.25f;
        }

        // ---------------------------------------------------------------------------------------- geometry

        /// A weapon's shape in its attach node's frame (the game's own hold).
        public struct Shape
        {
            public int Verts;
            public bool Readable;
            public Vector3 Long;      // principal axis, from the grip toward the head end
            public Vector3 Wide;      // second principal axis (a sword's edge-to-edge direction)
            public Vector3 Edge;      // perpendicular to Long: from the haft toward the far side of the head
            public float Asym;        // head extent toward Edge / toward -Edge (an axe ~2+, symmetric ~1)
            public float Near, Far;   // extent along Long from the node origin (negative = behind the grip)
            public float HeadEnd;     // +1 = the wide end is the far end, -1 = the near end
            public Vector3 Centre;    // haft axis point nearest the node origin
            public string Note;
        }

        /// The model's vertices in its parent node's frame, with the authored local pose applied.
        public static List<Vector3> NodeVerts(Transform model, Vector3 lpos, Quaternion lrot, Vector3 lscale, out bool readable)
        {
            var pts = new List<Vector3>();
            readable = true;
            Matrix4x4 authored = Matrix4x4.TRS(lpos, lrot, lscale);
            Matrix4x4 toModel = model.worldToLocalMatrix;
            foreach (MeshFilter mf in model.GetComponentsInChildren<MeshFilter>(true))
                Add(pts, mf.sharedMesh, authored * toModel * mf.transform.localToWorldMatrix, ref readable);
            foreach (SkinnedMeshRenderer smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                Add(pts, smr.sharedMesh, authored * toModel * smr.transform.localToWorldMatrix, ref readable);
            return pts;
        }

        static void Add(List<Vector3> pts, Mesh m, Matrix4x4 k, ref bool readable)
        {
            if (m == null) return;
            if (m.isReadable)
            {
                Vector3[] v = m.vertices;
                int step = Mathf.Max(1, v.Length / 4000);
                for (int i = 0; i < v.Length; i += step) pts.Add(k.MultiplyPoint3x4(v[i]));
                return;
            }
            readable = false;
            Bounds b = m.bounds;
            for (int i = 0; i < 8; i++)
                pts.Add(k.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
        }

        static Vector3 PowerIter(float[,] c, Vector3 seed)
        {
            Vector3 v = seed.normalized;
            for (int it = 0; it < 64; it++)
            {
                Vector3 n = new Vector3(c[0, 0] * v.x + c[0, 1] * v.y + c[0, 2] * v.z,
                                        c[1, 0] * v.x + c[1, 1] * v.y + c[1, 2] * v.z,
                                        c[2, 0] * v.x + c[2, 1] * v.y + c[2, 2] * v.z);
                if (n.sqrMagnitude < 1e-20f) break;
                v = n.normalized;
            }
            return v;
        }

        public static Shape Analyse(List<Vector3> pts)
        {
            var s = new Shape { Verts = pts.Count };
            if (pts.Count < 8) { s.Note = "no mesh"; return s; }
            Vector3 mean = Vector3.zero;
            foreach (Vector3 p in pts) mean += p;
            mean /= pts.Count;
            var c = new float[3, 3];
            foreach (Vector3 p in pts)
            {
                Vector3 d = p - mean;
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) c[i, j] += d[i] * d[j];
            }
            Vector3 l = PowerIter(c, new Vector3(0.3f, 0.9f, 0.2f));
            // deflate for the second axis
            float lam = 0f;
            Vector3 cl = new Vector3(c[0, 0] * l.x + c[0, 1] * l.y + c[0, 2] * l.z, c[1, 0] * l.x + c[1, 1] * l.y + c[1, 2] * l.z, c[2, 0] * l.x + c[2, 1] * l.y + c[2, 2] * l.z);
            lam = Vector3.Dot(cl, l);
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) c[i, j] -= lam * l[i] * l[j];
            Vector3 seed2 = Vector3.Cross(l, Mathf.Abs(l.y) < 0.9f ? Vector3.up : Vector3.right);
            Vector3 w = PowerIter(c, seed2);
            w = (w - l * Vector3.Dot(w, l)).normalized;

            // extents along the long axis from the node origin (where the game's hand closes)
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (Vector3 p in pts) { float t = Vector3.Dot(p, l); lo = Mathf.Min(lo, t); hi = Mathf.Max(hi, t); }
            float len = hi - lo;
            // haft axis: median perpendicular position of the middle 40 %
            var mid = new List<Vector3>();
            foreach (Vector3 p in pts) { float t = (Vector3.Dot(p, l) - lo) / Mathf.Max(1e-5f, len); if (t > 0.3f && t < 0.7f) mid.Add(p - l * Vector3.Dot(p, l)); }
            Vector3 axis = Vector3.zero;
            if (mid.Count > 0)
            {
                var xs = mid.Select(p => p.x).OrderBy(x => x).ToList();
                var ys = mid.Select(p => p.y).OrderBy(x => x).ToList();
                var zs = mid.Select(p => p.z).OrderBy(x => x).ToList();
                axis = new Vector3(xs[xs.Count / 2], ys[ys.Count / 2], zs[zs.Count / 2]);
            }
            // spread of each end (the last 25 %) around the haft axis: the head is the wider end
            float spreadHi = 0f, spreadLo = 0f;
            foreach (Vector3 p in pts)
            {
                float t = (Vector3.Dot(p, l) - lo) / Mathf.Max(1e-5f, len);
                Vector3 d = p - l * Vector3.Dot(p, l) - axis;
                if (t > 0.75f) spreadHi = Mathf.Max(spreadHi, d.magnitude);
                if (t < 0.25f) spreadLo = Mathf.Max(spreadLo, d.magnitude);
            }
            s.HeadEnd = spreadHi >= spreadLo ? 1f : -1f;
            // the long axis points from the grip (node origin) toward the end that reaches further
            if (Mathf.Abs(lo) > Mathf.Abs(hi)) { l = -l; float t0 = lo; lo = -hi; hi = -t0; s.HeadEnd = -s.HeadEnd; }
            s.Long = l;
            s.Wide = w;
            s.Near = lo;
            s.Far = hi;
            s.Centre = axis;
            // head region: the wide end's last 30 %; the edge = toward its farthest point from the haft axis
            bool headFar = s.HeadEnd > 0f;
            Vector3 far = Vector3.zero;
            float best = 0f;
            var head = new List<Vector3>();
            foreach (Vector3 p in pts)
            {
                float t = (Vector3.Dot(p, l) - lo) / Mathf.Max(1e-5f, hi - lo);
                if (headFar ? t < 0.7f : t > 0.3f) continue;
                Vector3 d = p - l * Vector3.Dot(p, l) - axis;
                head.Add(d);
                if (d.magnitude > best) { best = d.magnitude; far = d; }
            }
            if (best > 1e-5f)
            {
                s.Edge = far.normalized;
                float pos = 0f, neg = 0f;
                foreach (Vector3 d in head) { float t = Vector3.Dot(d, s.Edge); pos = Mathf.Max(pos, t); neg = Mathf.Max(neg, -t); }
                s.Asym = pos / Mathf.Max(0.005f, neg);
            }
            return s;
        }


        // ---------------------------------------------------------------------------------------- the test

        /// One weapon model held in one hand (the controller frame): null if it passes, else why not.
        static string Check(Transform model, Quaternion lrot, Vector3 scale, WeaponRef w, bool left, StringBuilder info)
        {
            Vector3 pos;
            Quaternion rot;
            Pose(model, lrot, scale, left, out pos, out rot);
            bool readable;
            List<Vector3> pts = NodeVerts(model, Vector3.zero, Quaternion.identity, Vector3.one, out readable);   // model space
            if (pts.Count < 8) return "no mesh";
            Quaternion c = Calibration(ControllerGeometry.GripPitchUp + CombatVR.SwordTilt);
            Vector3 fist = ControllerGeometry.GripPos(left), B = c * Vector3.forward, U = c * Vector3.right, W = Vector3.Cross(B, U);
            float ahead = 0f, behind = 0f;
            var hand = new List<Vector3>(pts.Count);
            foreach (Vector3 p in pts)
            {
                Vector3 h = pos + rot * Vector3.Scale(p, scale) - fist;
                hand.Add(h);
                float a = Vector3.Dot(h, B);
                ahead = Mathf.Max(ahead, a);
                behind = Mathf.Max(behind, -a);
            }
            // what the fist closes round: the mesh's cross-sections 2 cm below, at and 2 cm above the grip point (edges
            // of the triangles cut by those planes: a long haft has vertices only at its ends)
            var near = new List<float>();
            foreach (MeshFilter mf in model.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh m = mf.sharedMesh;
                if (m == null || !m.isReadable) continue;
                Matrix4x4 k = model.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                Vector3[] v = m.vertices;
                int[] tr = m.triangles;
                var hv = new Vector3[v.Length];
                for (int i = 0; i < v.Length; i++) hv[i] = pos + rot * Vector3.Scale(k.MultiplyPoint3x4(v[i]), scale) - fist;
                for (int i = 0; i + 2 < tr.Length; i += 3)
                    for (int ed = 0; ed < 3; ed++)
                    {
                        Vector3 p0 = hv[tr[i + ed]], p1 = hv[tr[i + (ed + 1) % 3]];
                        float a0 = Vector3.Dot(p0, B), a1 = Vector3.Dot(p1, B);
                        for (int sl = -1; sl <= 1; sl++)
                        {
                            float cut = sl * 0.02f;
                            if ((a0 - cut) * (a1 - cut) > 0f || Mathf.Abs(a1 - a0) < 1e-6f) continue;
                            Vector3 x = Vector3.Lerp(p0, p1, (cut - a0) / (a1 - a0));
                            near.Add((x - B * cut).magnitude);
                        }
                    }
            }
            near.Sort();
            float fistR = near.Count > 0 ? near[Mathf.Min(near.Count - 1, near.Count * 9 / 10)] : -1f;
            // head region: the far 30 % ahead of the fist
            float uu = 0f, ww = 0f, uw = 0f, best = 0f;
            Vector3 far = Vector3.zero, mean = Vector3.zero;
            var head = new List<Vector3>();
            foreach (Vector3 h in hand)
            {
                float a = Vector3.Dot(h, B);
                if (a < ahead * 0.7f) continue;
                Vector3 perp = h - B * a;
                head.Add(perp);
                mean += perp;
            }
            if (head.Count > 0) mean /= head.Count;
            foreach (Vector3 perp in head)
            {
                Vector3 d = perp - mean;
                float u = Vector3.Dot(d, U), v = Vector3.Dot(d, W);
                uu += u * u; ww += v * v; uw += u * v;
                if (perp.magnitude > best) { best = perp.magnitude; far = perp; }
            }
            float pos2 = 0f, neg2 = 0f;
            Vector3 e = far.sqrMagnitude > 1e-8f ? far.normalized : Vector3.zero;
            foreach (Vector3 perp in head) { float t = Vector3.Dot(perp, e); pos2 = Mathf.Max(pos2, t); neg2 = Mathf.Max(neg2, -t); }
            float asym = pos2 / Mathf.Max(0.005f, neg2);
            float widthAngle = Mathf.Abs(0.5f * Mathf.Atan2(2f * uw, uu - ww) * Mathf.Rad2Deg);   // the head's broad axis from the U line
            float edgeAngle = Vector3.Angle(e, -U);
            info.Append(left ? " L:" : " R:").Append(" fist r ").Append(fistR.ToString("0.000")).Append(" ahead ").Append(ahead.ToString("0.00")).Append(" behind ").Append(behind.ToString("0.00"))
                .Append(" width ").Append(widthAngle.ToString("0")).Append(" deg, far side ").Append(edgeAngle.ToString("0")).Append(" deg from the chop (asym ").Append(asym.ToString("0.0")).Append(')');
            if (fistR < 0f) return (left ? "L" : "R") + ": nothing in the fist";
            Grip g;
            Table.TryGetValue(ModelKey(model.name), out g);
            if (fistR > Mathf.Max(FistRadius, g.FistR)) return (left ? "L" : "R") + ": the fist is on a wide part (" + fistR.ToString("0.000") + " m)";
            if (ahead < 0.25f || ahead < 1.5f * behind) return (left ? "L" : "R") + ": the striking part is not beyond the fist (ahead " + ahead.ToString("0.00") + ", behind " + behind.ToString("0.00") + ")";
            if (g.NoEdge) return null;
            bool axe = w.Category == WeaponCategory.Axe, sword = w.Category == WeaponCategory.Sword;
            if (axe && asym >= 1.4f && edgeAngle > 30f) return (left ? "L" : "R") + ": the axe's edge is " + edgeAngle.ToString("0") + " deg off the chop";
            if ((sword || (axe && asym < 1.4f)) && widthAngle > 30f) return (left ? "L" : "R") + ": the blade's flat faces the chop (edge plane " + widthAngle.ToString("0") + " deg off)";
            return null;
        }

        /// Magenta pixels of a model rendered alone (an off-screen camera far below the world, one light).
        static int MagentaPixels(GameObject inst, out int seen)
        {
            seen = 0;
            Bounds b = new Bounds(inst.transform.position, Vector3.zero);
            bool any = false;
            foreach (Renderer r in inst.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any) return 0;
            var camGo = new GameObject("HoF2VR_GripTestCam");
            var lightGo = new GameObject("HoF2VR_GripTestLight");
            RenderTexture rt = RenderTexture.GetTemporary(128, 128, 24, RenderTextureFormat.ARGB32);
            Texture2D tex = new Texture2D(128, 128, TextureFormat.RGBA32, false);
            int n = 0;
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                float size = Mathf.Max(0.2f, b.size.magnitude);
                cam.transform.position = b.center + Vector3.up * size * 1.3f;
                cam.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = size * 3f;
                cam.fieldOfView = 50f;
                cam.targetTexture = rt;
                Light l = lightGo.AddComponent<Light>();
                l.type = LightType.Directional;
                l.intensity = 1.2f;
                lightGo.transform.rotation = Quaternion.LookRotation(new Vector3(0.3f, -1f, 0.2f));
                cam.Render();
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, 128, 128), 0, 0, false);
                tex.Apply();
                RenderTexture.active = prev;
                foreach (Color32 p in tex.GetPixels32())
                {
                    if (p.r + p.g + p.b > 30) seen++;
                    if (p.r > 70 && p.b > 70 && p.g * 2 < Mathf.Min(p.r, p.b)) n++;
                }
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(tex);
                UnityEngine.Object.Destroy(camGo);
                UnityEngine.Object.Destroy(lightGo);
            }
            return n;
        }

        public static string LastTest = "-";

        static string Test(string filter)
        {
            var sb = new StringBuilder();
            var failed = new List<string>();
            var seen = new HashSet<string>();
            int n = 0, mags = 0;
            foreach (EquipmentCard ec in AssetManager.Instance.Cards.Assets.OfType<EquipmentCard>())
            {
                WeaponRef w = ec.Equipment as WeaponRef;
                if (w == null) continue;
                string id = ec.CardData.Id;
                if (filter != null && id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Model prefab = null;
                try { Equipment eq = ec.Equipment.ResolveEquipmentPrefab(); prefab = eq != null ? eq.ModelPrefab : null; }
                catch (Exception e) { sb.Append("\n  SKIP ").Append(id).Append(" (").Append(e.Message).Append(')'); continue; }
                if (prefab == null) { sb.Append("\n  SKIP ").Append(id).Append(" (no model)"); continue; }
                string key = ModelKey(prefab.name);
                if (!seen.Add(key)) continue;
                GameObject inst = UnityEngine.Object.Instantiate(prefab.gameObject, new Vector3(0f, -2000f, 0f), Quaternion.identity);
                inst.name = prefab.name;
                try
                {
                    n++;
                    FixMaterials(inst.transform);
                    var info = new StringBuilder();
                    Transform pt = prefab.transform;
                    string r = Check(inst.transform, pt.localRotation, pt.localScale, w, false, info);
                    string l = Check(inst.transform, pt.localRotation, pt.localScale, w, true, info);
                    bool staticMag = inst.GetComponentsInChildren<Renderer>(true).SelectMany(x => x.sharedMaterials).Any(IsPlaceholder);
                    int vis;
                    int mag = MagentaPixels(inst, out vis);
                    string why = r ?? l ?? (staticMag ? "magenta material" : mag >= 20 ? mag + " magenta pixels" : vis < 40 ? "not rendered (" + vis + " px)" : null);
                    if (mag >= 20 || staticMag) mags++;
                    sb.Append("\n  ").Append(why == null ? "PASS " : "FAIL ").Append(id).Append(" (").Append(key).Append(", ").Append(w.Handedness).Append(' ').Append(w.Category).Append(")")
                      .Append(info).Append(" | rendered ").Append(vis).Append(" px, magenta ").Append(mag).Append(why != null ? " | " + why : "");
                    if (why != null) failed.Add(id + ": " + why);
                }
                catch (Exception e) { failed.Add(id + ": error " + e.Message); sb.Append("\n  FAIL ").Append(id).Append(" error ").Append(e); }
                finally { UnityEngine.Object.Destroy(inst); }
            }
            // the models held now: their pose in the hand against the table (within 3 deg)
            int held = 0, off = 0;
            if (CombatVR.I != null)
                foreach (Transform hand in new[] { CombatVR.I.HandR, CombatVR.I.HandL })
                {
                    if (hand == null) continue;
                    for (int i = 0; i < hand.childCount; i++)
                    {
                        Transform t = hand.GetChild(i);
                        Quaternion lrot;
                        bool shield;
                        if (!CombatVR.Authored(t, out lrot, out shield) || shield) continue;
                        Vector3 p;
                        Quaternion q;
                        Pose(t, lrot, t.localScale, hand == CombatVR.I.HandL, out p, out q);
                        float ang = Quaternion.Angle(q, t.localRotation);
                        held++;
                        sb.Append("\n  held ").Append(t.name).Append(hand == CombatVR.I.HandL ? " (left)" : " (right)").Append(": ").Append(ang.ToString("0.0")).Append(" deg from the table, ")
                          .Append((p - t.localPosition).magnitude.ToString("0.000")).Append(" m");
                        if (ang > 3f || (p - t.localPosition).magnitude > 0.01f) { off++; failed.Add("held " + t.name + " off the table"); }
                    }
                }
            LastTest = n + " models, " + (n - failed.Count(f => !f.StartsWith("held"))) + " pass, magenta " + mags + ", held " + held + " (" + off + " off)" +
                       (failed.Count > 0 ? " | FAILED: " + string.Join("; ", failed.ToArray()) : "");
            Plugin.L.LogInfo("grip test: " + LastTest + sb);
            return LastTest;
        }

        // `gripshow <id>|off`
        static GameObject s_show;
        static readonly List<Renderer> s_hidden = new List<Renderer>();

        static string Show(string id)
        {
            if (s_show != null) { UnityEngine.Object.Destroy(s_show); s_show = null; }
            foreach (Renderer r in s_hidden) if (r != null) r.enabled = true;
            s_hidden.Clear();
            if (id == "off") return "off";
            if (CombatVR.I == null || !CombatVR.Active) return "not in a fight";
            var ec = AssetManager.Instance.Cards.Assets.OfType<EquipmentCard>().FirstOrDefault(x => x.CardData.Id == id);
            WeaponRef w = ec != null ? ec.Equipment as WeaponRef : null;
            if (w == null) return "no weapon card " + id;
            Model prefab = ec.Equipment.ResolveEquipmentPrefab().ModelPrefab;
            if (prefab == null) return "no model";
            bool left = Settings.LeftHanded;
            Transform hand = left ? CombatVR.I.HandL : CombatVR.I.HandR;
            for (int i = 0; i < hand.childCount; i++)
                foreach (Renderer r in hand.GetChild(i).GetComponentsInChildren<Renderer>())
                    if (r.enabled) { r.enabled = false; s_hidden.Add(r); }
            s_show = UnityEngine.Object.Instantiate(prefab.gameObject, hand, false);
            s_show.name = prefab.name;
            FixMaterials(s_show.transform);
            Transform t = s_show.transform;
            t.localScale = prefab.transform.localScale;
            Vector3 pos;
            Quaternion rot;
            Pose(t, prefab.transform.localRotation, t.localScale, left, out pos, out rot);
            t.localPosition = pos;
            t.localRotation = rot;
            return "showing " + id + " (" + prefab.name + ") in the " + (left ? "left" : "right") + " hand";
        }

        // ---------------------------------------------------------------------------------------- seams

        static string F(float v) => v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        static string V3(Vector3 v) => "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")";

        public static string Materials(Transform model)
        {
            var sb = new StringBuilder();
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null) { sb.Append(" [").Append(r.name).Append(": NO MATERIAL]"); continue; }
                    Shader sh = m.shader;
                    Texture main = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                    sb.Append(" [").Append(r.name).Append(": ").Append(m.name).Append(" / ").Append(sh != null ? sh.name : "no shader")
                      .Append(sh != null && !sh.isSupported ? " UNSUPPORTED" : "").Append(" main=").Append(main != null ? main.name : "-");
                    if (m.HasProperty("_Color")) sb.Append(" col=").Append(ColorUtility.ToHtmlStringRGBA(m.GetColor("_Color")));
                    sb.Append(" kw=").Append(string.Join(",", m.shaderKeywords ?? new string[0])).Append(']');
                }
            }
            return sb.ToString();
        }

        static string Survey(string filter)
        {
            var sb = new StringBuilder();
            int n = 0;
            var byNode = new Dictionary<string, List<Shape>>();
            foreach (EquipmentCard ec in AssetManager.Instance.Cards.Assets.OfType<EquipmentCard>())
            {
                WeaponRef w = ec.Equipment as WeaponRef;
                ShieldRef sr = ec.Equipment as ShieldRef;
                if (w == null && sr == null) continue;
                string id = ec.CardData.Id;
                if (filter != null && id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    Equipment eq = ec.Equipment.ResolveEquipmentPrefab();
                    Model prefab = eq != null ? eq.ModelPrefab : null;
                    string kind = w != null ? "W " + w.Handedness + " " + w.Category : "S";
                    if (prefab == null) { sb.Append("\n").Append(kind).Append(' ').Append(id).Append(" no model"); continue; }
                    Transform t = prefab.transform;
                    bool readable;
                    List<Vector3> pts = NodeVerts(t, t.localPosition, t.localRotation, t.localScale, out readable);
                    Shape s = Analyse(pts);
                    s.Readable = readable;
                    string attach = eq.ModelAttach ?? "";
                    sb.Append("\n").Append(kind).Append(' ').Append(id).Append(" model=").Append(prefab.name).Append(" attach=").Append(attach)
                      .Append(" lpos=").Append(V3(t.localPosition)).Append(" lrot=").Append(V3(t.localEulerAngles)).Append(" lscale=").Append(V3(t.localScale))
                      .Append(" verts=").Append(s.Verts).Append(readable ? "" : " UNREADABLE")
                      .Append(" long=").Append(V3(s.Long)).Append(" near=").Append(F(s.Near)).Append(" far=").Append(F(s.Far)).Append(" headEnd=").Append(s.HeadEnd)
                      .Append(" wide=").Append(V3(s.Wide)).Append(" edge=").Append(V3(s.Edge)).Append(" asym=").Append(s.Asym.ToString("0.00"))
                      .Append(" axis=").Append(V3(s.Centre))
                      .Append(" mats:").Append(Materials(t));
                    string key = attach.Split(',')[0];
                    if (w != null)
                    {
                        List<Shape> list;
                        if (!byNode.TryGetValue(key, out list)) byNode[key] = list = new List<Shape>();
                        list.Add(s);
                    }
                    n++;
                }
                catch (Exception e) { sb.Append("\n? ").Append(id).Append(" error ").Append(e.Message); }
            }
            foreach (var kv in byNode)
            {
                Vector3 sum = Vector3.zero;
                foreach (Shape s in kv.Value) sum += s.Long;
                sb.Append("\nnode ").Append(kv.Key).Append(": ").Append(kv.Value.Count).Append(" weapons, mean long ").Append(V3(sum / kv.Value.Count));
            }
            Plugin.L.LogInfo("grip survey:" + sb);
            return n + " items (see log)";
        }

        static string HeroNodes()
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null || pc.Model == null) return "no hero";
            var sb = new StringBuilder();
            foreach (Model.AttachNode an in pc.Model.AttachNodes ?? new Model.AttachNode[0])
            {
                if (an == null) continue;
                Transform t = an.Transform;
                sb.Append("\n  ").Append(an.Name).Append(" -> ").Append(t != null ? VRUI.Path(t) : "null");
                if (t != null)
                    sb.Append(" lpos=").Append(V3(t.localPosition)).Append(" lrot=").Append(V3(t.localEulerAngles)).Append(" lossy=").Append(V3(t.lossyScale))
                      .Append(" children=").Append(t.childCount);
            }
            Plugin.L.LogInfo("hero attach nodes (" + pc.Model.name + "):" + sb);
            return (pc.Model.AttachNodes != null ? pc.Model.AttachNodes.Length : 0) + " nodes (see log)";
        }
    }
}

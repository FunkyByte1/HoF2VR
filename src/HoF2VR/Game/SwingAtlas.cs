using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// Where an enemy's blow really lands (combat Stage 1, docs/combat-progress.md 2026-10-12 night): per attacker model and
    /// attack clip, the striking part's segment at the game's hit event and its motion just before, in the attacker's own
    /// frame (its root: x right, y up from the feet, z forward), divided by the model's scale; plus how far the root itself
    /// moved from the telegraph to the hit (leaps). The table is measured in the fixtures (SwingTrace, scripts/swing_atlas.py
    /// -> SwingAtlasData.cs: decisions.md's pre-compute rule) and learned at run time for clips not in it (each strike's own
    /// blade at its impact; the first strike of an unknown clip uses UYF's direction palette). The block bar is placed
    /// where the predicted blade crosses the bar's plane, across its motion (Place).
    internal static class SwingAtlas
    {
        public sealed class Shape
        {
            public Vector3 A, B, V, Move;   // root frame / model scale: the blade's ends where it passes the guard zone, its motion (m/s), the root's move
            public float Dt;                // when that pass is, from the hit event (s; <= 0)
            public float Pass = 0.3f;       // how near the guard point the blade passed when measured (m)
            public float Reach = 0.2f;      // ... measured flat (no height): whether the blow comes to the player at all (m)
            public int N;
            public bool Seeded;
            public string Key = "";
        }

        static readonly Dictionary<string, Shape> s_table = new Dictionary<string, Shape>();
        static readonly Dictionary<string, Shape> s_byClip = new Dictionary<string, Shape>();
        public static int Learned, Predicted, Fallbacks;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static void EnsureSeed()
        {
            if (s_table.Count > 0) return;
            foreach (var e in SwingAtlasData.Entries)
            {
                var sh = new Shape { A = e.A, B = e.B, V = e.V, Move = e.Move, N = e.N, Seeded = true, Key = KeyOf(e.Model, e.Clip, e.Event), Dt = e.Dt, Pass = e.Pass, Reach = e.Reach };
                s_table[sh.Key] = sh;
                string ck = e.Clip + "@" + e.Event.ToString("0.00", Inv);
                if (!s_byClip.ContainsKey(ck)) s_byClip[ck] = sh;
            }
        }

        /// The shape of this model's blow with this clip; else the same clip's of another model (clips are shared across a
        /// rig: the thieves' and the guards' run attack); else null.
        /// Audit item 12 (docs/uyf-parity-audit.md E10): a hit event whose blade at the impact is behind its own attacker and
        /// never near the player - the templar's and the Black Knight's first hit of `Templar_attack_1` and of their counter
        /// combo (both ends at z < 0.1, 1.5-1.8 m from the guard point). The flat game lands these at a distance; they are a
        /// wind-up's hit event, not a blow at the player: no bar, no lead, no damage (Guard, ThreatWarning).
        public static bool Phantom(Shape sh) => sh != null && sh.Reach > Guard.ReachLimit && sh.A.z < 0.1f && sh.B.z < 0.1f;

        public static Shape Find(string model, string clip, float ev)
        {
            EnsureSeed();
            if (string.IsNullOrEmpty(clip)) return null;
            Shape sh;
            if (s_table.TryGetValue(KeyOf(model, clip, ev), out sh)) return sh;
            if (s_byClip.TryGetValue(clip + "@" + ev.ToString("0.00", Inv), out sh)) return sh;
            return null;
        }

        public static float ScaleOf(Controller c)
        {
            try { float s = c.Model != null ? c.Model.transform.lossyScale.y : c.transform.lossyScale.y; return s > 0.05f ? s : 1f; }
            catch (Exception) { return 1f; }
        }

        /// The blade at the coming impact in world space: the attacker's root now (its facing, which tracks the target until
        /// it commits), plus the part of the clip's root move not yet travelled since the telegraph.
        public static void World(Shape sh, Controller c, Vector3 telePos, out Vector3 a, out Vector3 b, out Vector3 v)
        {
            Transform root = c.transform;
            float s = ScaleOf(c);
            // the attacker's facing at the impact: toward the hero (every measured blow ended facing it, 0.00-0.03 deg off);
            // its facing now would sweep the bar sideways while it still turns in its wind-up
            Vector3 fw = root.forward;
            PlayerController pc = PlayerController.Instance;
            if (pc != null) { Vector3 to = pc.transform.position - root.position; to.y = 0f; if (to.sqrMagnitude > 0.04f) fw = to; }
            fw.y = 0f;
            Quaternion rot = fw.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(fw) : root.rotation;
            Vector3 pos = root.position;
            Vector3 total = rot * (sh.Move * s);
            total.y = 0f;
            float tm = total.magnitude;
            if (tm > 0.15f)
            {
                float done = Vector3.Dot(root.position - telePos, total / tm);
                pos += total * Mathf.Clamp01(1f - done / tm);
            }
            a = pos + rot * (sh.A * s);
            b = pos + rot * (sh.B * s);
            v = rot * (sh.V * s);
        }

        /// The bar's depth in front of the eyes toward the attacker (UYF's engagement space: half the distance, 0.25-0.5 m).
        public static float Depth(Vector3 head, Vector3 att)
        {
            Vector3 d = att - head;
            d.y = 0f;
            return Mathf.Clamp(d.magnitude * 0.5f, 0.25f, 0.5f);
        }

        /// The guard zone, relative to the eyes in the bar's plane: where a held guard can meet a blow (lateral, vertical).
        public const float ZoneX = 0.55f, ZoneLow = -1.0f, ZoneHigh = 0.3f;

        /// Mark 16: a sweep's bar on the side it lands (Place). Test seam `landside on|off`.
        public static bool LandSide = true;
        public const float BodyDepth = 0.15f, LandLead = 0.12f;
        public static int LandShifts;

        /// The bar from a predicted blade (world): its centre where the blade crosses the bar's plane (the end nearest the
        /// plane if it doesn't cross it), kept inside the guard zone; its axis square to the blade's motion in that plane
        /// (level for a thrust, whose motion runs into the plane). `crossing` = how far the blade's motion runs across the
        /// plane rather than into it (0..1).
        public static void Place(Vector3 head, Vector3 f, float depth, Vector3 a, Vector3 b, Vector3 v, out Vector3 center, out Vector3 axis, out float crossing, out bool clamped)
        {
            Vector3 p0 = head + f * depth;
            float da = Vector3.Dot(a - p0, f), db = Vector3.Dot(b - p0, f);
            float u = (da > 0f) == (db > 0f) ? (Mathf.Abs(da) < Mathf.Abs(db) ? 0f : 1f) : Mathf.Clamp01(da / (da - db));
            Vector3 p = Vector3.Lerp(a, b, u);
            Vector3 r = Vector3.Cross(Vector3.up, f).normalized;
            float x = Vector3.Dot(p - head, r), y = p.y - head.y;
            // Playtest 3 (2026-10-18, mark 16): "When they are swinging to hit my right side, the orange bar is on my left. And
            // vice versa." A sweep crosses the guard plane on the side it comes from and lands on the other: the soldiers' blades
            // passed the plane at +0.10 m moving left at 12-14 m/s (and toward the player at 7), and the user held the sword
            // where the blow lands, 0.9 m from the bar (the playtest's "guard missed" lines). The bar goes where the blade's
            // sideways motion carries it by the time it reaches the body (BodyDepth from the eyes; at most LandLead s): the side it
            // hits. Height and tilt are kept.
            if (LandSide)
            {
                float vz = -Vector3.Dot(v, f), vx = Vector3.Dot(v, r);
                if (vz > 0.5f && Mathf.Abs(vx) > 0.3f * v.magnitude)
                {
                    float t = Mathf.Clamp((Vector3.Dot(p - head, f) - BodyDepth) / vz, 0f, LandLead);
                    x += vx * t;
                    LandShifts++;
                }
            }
            float cx = Mathf.Clamp(x, -ZoneX, ZoneX), cy = Mathf.Clamp(y, ZoneLow, ZoneHigh);
            clamped = Mathf.Abs(cx - x) > 0.02f || Mathf.Abs(cy - y) > 0.02f;
            center = p0 + r * cx + Vector3.up * cy;
            Vector3 vp = v - f * Vector3.Dot(v, f);
            float vm = v.magnitude;
            crossing = vm > 1e-3f ? vp.magnitude / vm : 0f;
            axis = crossing >= 0.35f && vp.sqrMagnitude > 1e-4f ? Vector3.Cross(f, vp).normalized : r;
            // keep the axis pointing "rightish/up" so the bar never flips between frames
            if (Vector3.Dot(axis, r) < -0.05f || (Mathf.Abs(Vector3.Dot(axis, r)) <= 0.05f && axis.y < 0f)) axis = -axis;
        }

        // ---------------------------------------------------------------------------------------- learning at run time

        /// One strike's recent frames: its root, its striking parts (world) and the player's guard point, to measure where
        /// its blade passes.
        public sealed class Track
        {
            sealed class F { public float T; public Vector3 Root, G; public Quaternion Rot; public string[] N; public Vector3[] A, B; }
            readonly List<F> m_f = new List<F>();
            public Controller C;
            public Model M;
            public string Model = "", Clip = "";
            public float Event;
            public Vector3 TelePos;
            public Quaternion TeleRot;
            public string LastPart = "";
            public float LastPass = -1f, LastDt;

            /// The guard point: where a held guard meets a blow aimed at the eyes (0.35 m toward the attacker, 0.15 m down).
            public static Vector3 GuardPoint(Vector3 head, Vector3 f) => head + f * 0.35f - Vector3.up * 0.15f;

            static bool IsWeapon(string name) =>
                name.StartsWith("trail:") || (name.StartsWith("mesh:") && name.IndexOf("shield", StringComparison.OrdinalIgnoreCase) < 0);

            public void Tick(Vector3 head, Vector3 f)
            {
                if (C == null || M == null) return;
                float now = Time.unscaledTime;
                if (m_f.Count > 0 && m_f[m_f.Count - 1].T >= now) return;
                List<EnemyWeapon.Part> parts = EnemyWeapon.Sample(M);
                var fr = new F { T = now, Root = C.transform.position, Rot = C.transform.rotation, G = GuardPoint(head, f), N = new string[parts.Count], A = new Vector3[parts.Count], B = new Vector3[parts.Count] };
                for (int i = 0; i < parts.Count; i++) { fr.N[i] = parts[i].Kind + ":" + parts[i].Name; fr.A[i] = parts[i].WA; fr.B[i] = parts[i].WB; }
                m_f.Add(fr);
                while (m_f.Count > 2 && now - m_f[0].T > 0.5f) m_f.RemoveAt(0);
            }

            float BodyMotion(string name, float upTo)
            {
                float sum = 0f;
                F last = null;
                int lj = -1;
                foreach (F fr in m_f)
                {
                    int j = Array.IndexOf(fr.N, name);
                    if (fr.T < upTo - 0.35f || fr.T > upTo || j < 0) { last = null; continue; }
                    if (last != null)
                    {
                        Quaternion ia = Quaternion.Inverse(fr.Rot), ib = Quaternion.Inverse(last.Rot);
                        sum += (ia * (fr.B[j] - fr.Root) - ib * (last.B[lj] - last.Root)).magnitude + (ia * (fr.A[j] - fr.Root) - ib * (last.A[lj] - last.Root)).magnitude;
                    }
                    last = fr;
                    lj = j;
                }
                return sum;
            }

            /// At the impact: the striking part (of the parts that clearly swing - at least 40 % of the most motion against
            /// the body in the last 0.35 s - the one that passes closest to the guard point in the last 0.25 s) and that
            /// pass: the part's ends and the motion of its point nearest the guard point, in the root frame then, over the
            /// model's scale; the root's move since the telegraph. False without enough frames.
            public bool Measure(Vector3 head, Vector3 f, out Shape sh)
            {
                sh = null;
                Tick(head, f);
                if (m_f.Count < 3) return false;
                F im = m_f[m_f.Count - 1];
                float most = 0f;
                var motion = new float[im.N.Length];
                for (int i = 0; i < im.N.Length; i++) { motion[i] = BodyMotion(im.N[i], im.T); most = Mathf.Max(most, motion[i]); }
                if (most < 0.1f) return false;
                string bestName = null;
                F bestF = null;
                int bestJ = -1;
                float bestD = float.MaxValue, bestU = 0f;
                // weapons first (a trail or a weapon mesh): a shield, a hand or a foot only if no weapon comes within 0.6 m
                for (int pass = 0; pass < 2 && (bestF == null || bestD > 0.6f); pass++)
                {
                    if (pass == 1) { bestF = null; bestD = float.MaxValue; }
                    for (int i = 0; i < im.N.Length; i++)
                    {
                        if (motion[i] < Mathf.Max(0.1f, 0.4f * most)) continue;
                        if (pass == 0 && !IsWeapon(im.N[i])) continue;
                        foreach (F fr in m_f)
                        {
                            int j = Array.IndexOf(fr.N, im.N[i]);
                            if (j < 0 || fr.T < im.T - 0.25f) continue;
                            Vector3 c1, c2;
                            float s, u;
                            SwingTrace.SegSeg(fr.A[j], fr.B[j], fr.G, fr.G, out c1, out c2, out s, out u);
                            float d = (c1 - fr.G).magnitude;
                            if (d < bestD) { bestD = d; bestName = im.N[i]; bestF = fr; bestJ = j; bestU = s; }
                        }
                    }
                    if (pass == 0 && bestF != null && bestD <= 0.6f) break;
                    if (pass == 0 && bestF != null) { /* keep the weapon's pass in case nothing else does better */ }
                }
                if (bestF == null) return false;
                // the motion of that point over the 0.1 s up to the pass (or the frames right after it, at the start)
                Vector3 vel = Vector3.zero;
                Vector3 pNow = Vector3.Lerp(bestF.A[bestJ], bestF.B[bestJ], bestU);
                foreach (F fr in m_f)
                {
                    int j = Array.IndexOf(fr.N, bestName);
                    if (j < 0 || fr.T < bestF.T - 0.1f || bestF.T - fr.T < 1e-3f) continue;
                    vel = (pNow - Vector3.Lerp(fr.A[j], fr.B[j], bestU)) / (bestF.T - fr.T);
                    break;
                }
                if (vel == Vector3.zero)
                    foreach (F fr in m_f)
                    {
                        int j = Array.IndexOf(fr.N, bestName);
                        if (j < 0 || fr.T <= bestF.T + 1e-3f) continue;
                        vel = (Vector3.Lerp(fr.A[j], fr.B[j], bestU) - pNow) / (fr.T - bestF.T);
                        break;
                    }
                // the pass measured flat: a sweep at the hip passes far below the chest-level guard point but right in front
                // of the player; the templar's first hit stays 1.8 m away whatever the height
                Vector3 fa = bestF.A[bestJ], fb = bestF.B[bestJ], fg = bestF.G;
                fa.y = fb.y = fg.y = 0f;
                Vector3 h1, h2;
                float hs, hu;
                SwingTrace.SegSeg(fa, fb, fg, fg, out h1, out h2, out hs, out hu);
                float reach = (h1 - fg).magnitude;
                float sc = ScaleOf(C);
                Quaternion inv = Quaternion.Inverse(bestF.Rot);
                sh = new Shape
                {
                    A = inv * (bestF.A[bestJ] - bestF.Root) / sc, B = inv * (bestF.B[bestJ] - bestF.Root) / sc, V = inv * vel / sc,
                    Move = Quaternion.Inverse(TeleRot) * (bestF.Root - TelePos) / sc, N = 1, Key = KeyOf(Model, Clip, Event), Dt = bestF.T - im.T, Pass = bestD, Reach = reach
                };
                LastPart = bestName;
                LastPass = bestD;
                LastDt = sh.Dt;
                return true;
            }
        }

        public static string KeyOf(string model, string clip, float ev) => model + "|" + clip + "@" + ev.ToString("0.00", Inv);

        /// A strike landed: its measured shape refines (or creates) this model's and clip's entry. Seeded entries stay as
        /// measured in the fixtures (only learned ones move).
        public static void Learn(Track t, Vector3 head, Vector3 f)
        {
            if (t == null || string.IsNullOrEmpty(t.Clip)) return;
            Shape m;
            if (!t.Measure(head, f, out m)) return;
            EnsureSeed();
            Shape sh;
            if (s_table.TryGetValue(m.Key, out sh))
            {
                if (sh.Seeded) return;
                float k = 1f / Mathf.Min(sh.N + 1, 5);
                sh.A = Vector3.Lerp(sh.A, m.A, k); sh.B = Vector3.Lerp(sh.B, m.B, k); sh.V = Vector3.Lerp(sh.V, m.V, k); sh.Move = Vector3.Lerp(sh.Move, m.Move, k); sh.Pass = Mathf.Lerp(sh.Pass, m.Pass, k); sh.Reach = Mathf.Lerp(sh.Reach, m.Reach, k);
                sh.N++;
                return;
            }
            s_table[m.Key] = m;
            string ck = t.Clip + "@" + t.Event.ToString("0.00", Inv);
            if (!s_byClip.ContainsKey(ck)) s_byClip[ck] = m;
            Learned++;
            Plugin.L.LogInfo("SwingAtlas: learned " + m.Key + " a " + Diag.V(m.A) + " b " + Diag.V(m.B) + " v " + Diag.V(m.V) + " move " + Diag.V(m.Move));
        }

        public static string Describe()
        {
            EnsureSeed();
            return s_table.Count + " shapes (" + s_table.Values.Count(x => x.Seeded) + " measured in fixtures, " + Learned + " learned this run), predicted " + Predicted + ", fallbacks " + Fallbacks;
        }

        /// `atlas dump`: the learned entries (for SwingAtlasData.cs) -> testdata/probes/atlas_learned.tsv
        public static string Dump()
        {
            var sb = new StringBuilder("model\tclip\tevent\ta\tb\tv\tmove\tn\tdt\n");
            Func<Vector3, string> V = x => x.x.ToString("0.000", Inv) + "," + x.y.ToString("0.000", Inv) + "," + x.z.ToString("0.000", Inv);
            foreach (Shape sh in s_table.Values.Where(x => !x.Seeded))
            {
                int i = sh.Key.IndexOf('|'), j = sh.Key.LastIndexOf('@');
                sb.Append(sh.Key.Substring(0, i)).Append('\t').Append(sh.Key.Substring(i + 1, j - i - 1)).Append('\t').Append(sh.Key.Substring(j + 1)).Append('\t').Append(V(sh.A)).Append('\t').Append(V(sh.B)).Append('\t').Append(V(sh.V)).Append('\t')
                  .Append(V(sh.Move)).Append('\t').Append(sh.N).Append('\t').Append(sh.Dt.ToString("0.00", Inv)).Append('\n');
            }
            string dir = Path.Combine(TestMode.Dir, "probes");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "atlas_learned.tsv"), sb.ToString());
            return Describe() + " -> " + dir + "/atlas_learned.tsv";
        }
    }
}

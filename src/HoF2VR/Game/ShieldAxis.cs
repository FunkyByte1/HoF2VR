using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// Which way is up on a shield model (model space, in its face plane).
    ///
    /// Playtest 4 (P4-12, 2026-10-19, mark 222059_9): "My shield is still in the wrong orientation. I'm holding my hand straight out
    /// ... The shield was still up like a wall. But it was rotated like a clock about 90 degrees." The game's shield models are drawn
    /// turned in their own plane (they hang from the hero's shield bone in the game's attach frame): BasicShield's outline is
    /// mirror-symmetric about a line 57.6 deg from its +x axis, its wide flat top at 237.6 deg - but the hold took +x as up (the
    /// bounds' middle axis, and F7's "in-plane axis nearest the world's up" in the hero's showcase pose: 0.65), so the shield stood
    /// 122 deg off upright. Read from the mesh instead: the line the outline is mirror-symmetric about (heater, kite, tower and
    /// emblem shields; 15 of the game's 20 shield models have a clean one), toward the wide end. A shield without a clear mirror
    /// line (round ones) or without a wide end takes the direction the game's shields are drawn with (Convention: the wide ends of
    /// the heater-like models lie at 233-264 deg in their x-y plane; every model's face is its z).
    internal static class ShieldAxis
    {
        public const float ConventionDeg = 245f;
        const float CleanRms = 0.025f;     // the mirror line's rms error, as a share of the shield's length along it
        const float Distinct = 2f;         // ... and at least this much better than any line > 20 deg away (round shields: ~1-1.7)
        const float WideShare = 0.12f;     // the wide end: its third at least this much wider than the other end's

        public static Vector3 Convention(Vector3 face)
        {
            float a = ConventionDeg * Mathf.Deg2Rad;
            Vector3 c = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
            c -= face * Vector3.Dot(c, face);
            if (c.sqrMagnitude < 1e-6f) c = Vector3.Cross(face, Vector3.right);
            return c.normalized;
        }

        /// The model's design up (model space, unit, square to `face`), or null without readable meshes. `how` says why.
        public static Vector3? DesignUp(Transform model, Vector3 face, out string how)
        {
            how = "no mesh";
            if (model == null || face.sqrMagnitude < 1e-6f) return null;
            face.Normalize();
            List<Vector3> pts = Verts(model);
            if (pts == null) return null;
            Vector3 e1, e2;
            Basis(face, out e1, out e2);
            List<Vector2> hull = FaceHull(pts, e1, e2);
            if (hull.Count < 3) return null;
            // the hull's area centroid and its outline sampled evenly (~160 points)
            float area = 0f, cx = 0f, cy = 0f, per = 0f;
            for (int i = 0; i < hull.Count; i++)
            {
                Vector2 p = hull[i], q = hull[(i + 1) % hull.Count];
                float c = p.x * q.y - q.x * p.y;
                area += c; cx += (p.x + q.x) * c; cy += (p.y + q.y) * c;
                per += (q - p).magnitude;
            }
            if (Mathf.Abs(area) < 1e-6f) return null;
            Vector2 C = new Vector2(cx / (3f * area), cy / (3f * area));
            var samples = new List<Vector2>();
            float step = per / 160f;
            for (int i = 0; i < hull.Count; i++)
            {
                Vector2 p = hull[i], q = hull[(i + 1) % hull.Count];
                int n = Mathf.Max(1, Mathf.RoundToInt((q - p).magnitude / step));
                for (int j = 0; j < n; j++) samples.Add(Vector2.Lerp(p, q, j / (float)n));
            }
            // the mirror line through the centroid: the angle whose reflection of the outline lies best on the outline
            float best = 0f, bestE = float.MaxValue;
            var errs = new float[90];
            for (int d = 0; d < 90; d++)
            {
                errs[d] = MirrorError(samples, hull, C, d * 2f);
                if (errs[d] < bestE) { bestE = errs[d]; best = d * 2f; }
            }
            for (float d = best - 2f; d <= best + 2f; d += 0.25f)
            {
                float e = MirrorError(samples, hull, C, d);
                if (e < bestE) { bestE = e; best = d; }
            }
            float other = float.MaxValue;
            for (int d = 0; d < 90; d++)
            {
                float off = Mathf.Abs(Mathf.DeltaAngle(d * 2f * 2f, best * 2f)) * 0.5f;   // line angles are mod 180
                if (off > 20f) other = Mathf.Min(other, errs[d]);
            }
            Vector2 u = new Vector2(Mathf.Cos(best * Mathf.Deg2Rad), Mathf.Sin(best * Mathf.Deg2Rad)), w = new Vector2(-u.y, u.x);
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (Vector2 s in samples) { float a = Vector2.Dot(s - C, u); lo = Mathf.Min(lo, a); hi = Mathf.Max(hi, a); }
            float len = hi - lo, wLo0 = float.MaxValue, wLo1 = float.MinValue, wHi0 = float.MaxValue, wHi1 = float.MinValue;
            foreach (Vector2 s in samples)
            {
                float a = Vector2.Dot(s - C, u), b = Vector2.Dot(s - C, w);
                if (a < lo + len / 3f) { wLo0 = Mathf.Min(wLo0, b); wLo1 = Mathf.Max(wLo1, b); }
                if (a > hi - len / 3f) { wHi0 = Mathf.Min(wHi0, b); wHi1 = Mathf.Max(wHi1, b); }
            }
            float wLo = wLo1 - wLo0, wHi = wHi1 - wHi0;
            Vector3 axis3 = (e1 * u.x + e2 * u.y).normalized, conv = Convention(face);
            string nums = "mirror line " + best.ToString("0.0") + " deg (rms " + (bestE * 1000f).ToString("0.0") + " mm, next line " + (other * 1000f).ToString("0.0") +
                          " mm), length " + len.ToString("0.00") + " m, ends " + wLo.ToString("0.00") + " / " + wHi.ToString("0.00") + " m wide";
            if (bestE > CleanRms * len || other < Distinct * bestE)
            {
                how = "no clear mirror line (" + nums + "): the game's convention";
                return conv;
            }
            if (Mathf.Abs(wHi - wLo) >= WideShare * Mathf.Max(wHi, wLo))
            {
                how = "the mirror line toward the wide end (" + nums + ")";
                return wHi > wLo ? axis3 : -axis3;
            }
            how = "the mirror line, its end nearer the game's convention (" + nums + ")";
            return Vector3.Dot(axis3, conv) >= 0f ? axis3 : -axis3;
        }

        /// The model's vertices in model space (the readable meshes of its active children), or null with fewer than 12.
        static List<Vector3> Verts(Transform model)
        {
            var pts = new List<Vector3>();
            Matrix4x4 toModel = model.worldToLocalMatrix;
            foreach (MeshFilter mf in model.GetComponentsInChildren<MeshFilter>(false))
            {
                Mesh m = mf.sharedMesh;
                if (m == null || !m.isReadable) continue;
                Matrix4x4 k = toModel * mf.transform.localToWorldMatrix;
                foreach (Vector3 v in m.vertices) pts.Add(k.MultiplyPoint3x4(v));
            }
            return pts.Count < 12 ? null : pts;
        }

        /// The face plane's basis: e1 from the model axis least along the face.
        static void Basis(Vector3 face, out Vector3 e1, out Vector3 e2)
        {
            Vector3 seed = Mathf.Abs(face.x) < 0.9f ? Vector3.right : Vector3.up;
            e1 = (seed - face * Vector3.Dot(seed, face)).normalized;
            e2 = Vector3.Cross(face, e1);
        }

        static List<Vector2> FaceHull(List<Vector3> pts, Vector3 e1, Vector3 e2)
        {
            var flat = new List<Vector2>(pts.Count);
            foreach (Vector3 p in pts) flat.Add(new Vector2(Vector3.Dot(p, e1), Vector3.Dot(p, e2)));
            return Hull(flat);
        }

        // ---------------------------------------------------------------------------------------- the outline (the circle's coverage)

        /// The most corners an outline keeps (Guard's coverage walks them for every live blow each frame).
        public const int OutlineMax = 48;
        static readonly Dictionary<string, Vector3[]> s_outlines = new Dictionary<string, Vector3[]>();
        static readonly Dictionary<string, string> s_outlineHow = new Dictionary<string, string>();

        /// The shield's own outline (model space) for Guard's block rule - 2026-10-20, the user: "Not the middle. Any part of the
        /// shield has to cover at least 55% of the circle." The convex hull of its mesh's vertices seen square to its face, on the face
        /// plane through `at` (the bounds' centre), cut to at most OutlineMax corners: the corner whose triangle with its two
        /// neighbours is smallest goes first (Visvalingam; a round shield's 100+ hull corners lose < 1 % of the area). Computed once
        /// per model (by its name); null without readable meshes. `how` gives the corners and the area.
        public static Vector3[] Outline(Transform model, Vector3 face, Vector3 at, out string how)
        {
            how = "no mesh";
            if (model == null || face.sqrMagnitude < 1e-6f) return null;
            string key = model.name.Replace("(Clone)", "").Trim();
            Vector3[] got;
            if (s_outlines.TryGetValue(key, out got)) { how = s_outlineHow[key] + " (kept)"; return got; }
            face.Normalize();
            List<Vector3> pts = Verts(model);
            if (pts == null) return null;
            Vector3 e1, e2;
            Basis(face, out e1, out e2);
            List<Vector2> hull = FaceHull(pts, e1, e2);
            if (hull.Count < 3) return null;
            int corners = hull.Count;
            float area0 = Mathf.Abs(Area(hull));
            while (hull.Count > OutlineMax)
            {
                int bi = 0;
                float ba = float.MaxValue;
                for (int i = 0; i < hull.Count; i++)
                {
                    float a = Mathf.Abs(Cross(hull[(i + hull.Count - 1) % hull.Count], hull[i], hull[(i + 1) % hull.Count]));
                    if (a < ba) { ba = a; bi = i; }
                }
                hull.RemoveAt(bi);
            }
            float area1 = Mathf.Abs(Area(hull)), k = Vector3.Dot(at, face);
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            var o = new Vector3[hull.Count];
            for (int i = 0; i < hull.Count; i++)
            {
                o[i] = e1 * hull[i].x + e2 * hull[i].y + face * k;
                minX = Mathf.Min(minX, hull[i].x); maxX = Mathf.Max(maxX, hull[i].x); minY = Mathf.Min(minY, hull[i].y); maxY = Mathf.Max(maxY, hull[i].y);
            }
            how = "the mesh's hull in its face plane: " + pts.Count + " vertices, " + corners + " hull corners -> " + o.Length + " (area " +
                  area1.ToString("0.000") + " of " + area0.ToString("0.000") + " model units^2, " + (maxX - minX).ToString("0.00") + " x " + (maxY - minY).ToString("0.00") + " across)";
            s_outlines[key] = o;
            s_outlineHow[key] = how;
            return o;
        }

        /// A round outline (model space): `n` corners of radius `r` round `at` in the plane square to `face` (a shield without readable meshes).
        public static Vector3[] Disc(Vector3 face, Vector3 at, float r, int n)
        {
            Vector3 e1, e2;
            Basis(face.normalized, out e1, out e2);
            var o = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float a = i * 2f * Mathf.PI / n;
                o[i] = at + (e1 * Mathf.Cos(a) + e2 * Mathf.Sin(a)) * r;
            }
            return o;
        }

        static float Area(List<Vector2> poly)
        {
            float a = 0f;
            for (int i = 0; i < poly.Count; i++) { Vector2 p = poly[i], q = poly[(i + 1) % poly.Count]; a += p.x * q.y - q.x * p.y; }
            return 0.5f * a;
        }

        static float MirrorError(List<Vector2> samples, List<Vector2> hull, Vector2 c, float deg)
        {
            Vector2 u = new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad));
            float sum = 0f;
            int n = 0;
            for (int i = 0; i < samples.Count; i += 2)
            {
                Vector2 d = samples[i] - c;
                Vector2 r = c + 2f * Vector2.Dot(d, u) * u - d;
                sum += SqDistToOutline(r, hull);
                n++;
            }
            return n > 0 ? Mathf.Sqrt(sum / n) : float.MaxValue;
        }

        static float SqDistToOutline(Vector2 p, List<Vector2> hull)
        {
            float best = float.MaxValue;
            for (int i = 0; i < hull.Count; i++)
            {
                Vector2 a = hull[i], b = hull[(i + 1) % hull.Count], ab = b - a;
                float t = ab.sqrMagnitude > 1e-12f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                float d = (a + ab * t - p).sqrMagnitude;
                if (d < best) best = d;
            }
            return best;
        }

        /// The convex hull (Andrew's monotone chain), counter-clockwise.
        static List<Vector2> Hull(List<Vector2> pts)
        {
            pts.Sort((p, q) => p.x != q.x ? p.x.CompareTo(q.x) : p.y.CompareTo(q.y));
            var h = new List<Vector2>(64);
            for (int pass = 0; pass < 2; pass++)
            {
                int start = h.Count;
                for (int k = 0; k < pts.Count; k++)
                {
                    Vector2 p = pass == 0 ? pts[k] : pts[pts.Count - 1 - k];
                    while (h.Count >= start + 2 && Cross(h[h.Count - 2], h[h.Count - 1], p) <= 0f) h.RemoveAt(h.Count - 1);
                    h.Add(p);
                }
                h.RemoveAt(h.Count - 1);
            }
            return h;
        }

        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
    }
}

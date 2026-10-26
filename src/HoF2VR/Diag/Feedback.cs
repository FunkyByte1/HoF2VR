using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// Probes for the user's 2026-10-11 morning feedback (title menu, table spot, reach, text placement).
    ///   bodyhits [r m] [h m]   renderers whose triangles reach into the player's body at the current station:
    ///                          a vertical cylinder of radius r (real metres, default 0.35) from the floor to
    ///                          head height (+h above the eyes, default 0.1)
    ///   uiwhere <substr> [max] 2D widgets/panels whose path contains <substr>: their rectangle on the UI
    ///                          texture (pixels), alpha, clipping and the components that place them
    ///   camkids                children of every camera that draws into the headset (they follow the head)
    internal static class Feedback
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Register()
        {
            Diag.Commands["bodyhits"] = a =>
            {
                float r = a.Length > 1 ? float.Parse(a[1], Inv) : 0.35f;
                float h = a.Length > 2 ? float.Parse(a[2], Inv) : 0.1f;
                List<BodyHit> hits = BodyHits(r, h, true, true);
                var sb = new StringBuilder();
                foreach (BodyHit b in hits) sb.Append("\n  ").Append(b);
                Plugin.L.LogInfo("bodyhits r=" + r + " m at " + (VRRig.I != null ? Diag.V(VRRig.I.AnchorPos) : "-") + ":" + sb);
                return hits.Count + " renderers (see log)";
            };
            Diag.Commands["uiwhere"] = a =>
            {
                int max = 30;
                int last = a.Length - 1;
                if (last >= 2 && int.TryParse(a[last], out max)) last--; else max = 30;
                string key = string.Join(" ", a, 1, last);
                string s = UiWhere(key, max);
                Plugin.L.LogInfo("uiwhere '" + key + "':" + s);
                return "logged";
            };
            // `colliders <substr> [max]`: colliders (path, layer, trigger, rigidbody) and whether layer 16 / the dice layer collide
            Diag.Commands["colliders"] = a =>
            {
                int max = 20;
                int last = a.Length - 1;
                if (last >= 2 && int.TryParse(a[last], out max)) last--; else max = 20;
                string key = string.Join(" ", a, 1, last);
                var sb = new StringBuilder();
                int n = 0;
                foreach (Collider c in UnityEngine.Object.FindObjectsOfType<Collider>())
                {
                    string path = VRUI.Path(c.transform);
                    if (path.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (n++ >= max) break;
                    sb.AppendFormat("\n  {0} layer={1} trigger={2} enabled={3} rb={4} bounds {5}..{6}", path, c.gameObject.layer, c.isTrigger, c.enabled && c.gameObject.activeInHierarchy,
                        c.attachedRigidbody != null ? c.attachedRigidbody.name : "-", Diag.V(c.bounds.min), Diag.V(c.bounds.max));
                }
                sb.Append("\n  layer pairs that collide:");
                for (int i = 0; i < 32; i++)
                    for (int j = i; j < 32; j++)
                        if ((i == 16 || j == 16) && !Physics.GetIgnoreLayerCollision(i, j)) sb.AppendFormat(" {0}-{1}", i, j);
                Plugin.L.LogInfo("colliders '" + key + "':" + sb);
                return n + " colliders (see log)";
            };
            Diag.Commands["camkids"] = a =>
            {
                var sb = new StringBuilder();
                foreach (Camera c in Camera.allCameras)
                {
                    if (c.stereoTargetEye == StereoTargetEyeMask.None || c.targetTexture != null) continue;
                    sb.AppendFormat("\n  '{0}' ({1} children)", VRUI.Path(c.transform), c.transform.childCount);
                    foreach (Transform t in c.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == c.transform) continue;
                        Renderer rd = t.GetComponent<Renderer>();
                        sb.AppendFormat("\n     {0} active={1} layer={2}{3}", VRUI.Path(t), t.gameObject.activeInHierarchy, t.gameObject.layer,
                            rd != null ? " renderer " + rd.GetType().Name + (rd.enabled ? "" : "(off)") : "");
                    }
                }
                Plugin.L.LogInfo("camkids:" + sb);
                return "logged";
            };
        }

        internal sealed class BodyHit
        {
            public Renderer R;
            public string How;          // "mesh" (triangles tested) or "bounds"
            public int Tris;            // triangles inside the cylinder
            public float D;             // nearest horizontal distance to the axis (real metres)
            public float YMin, YMax;    // height range of the part inside (real metres above the floor)
            public override string ToString()
            {
                Bounds b = R.bounds;
                string mats = "";
                foreach (Material m in R.sharedMaterials) if (m != null) mats += (mats.Length > 0 ? "," : "") + m.name;
                return string.Format(Inv, "{0} d={1:0.00} m y {2:0.00}..{3:0.00} m {4}{5} static={6} | {7} | bounds {8}..{9} | mats {10}",
                    R.name, D, YMin, YMax, How, How == "mesh" ? " tris=" + Tris : "", R.isPartOfStaticBatch, VRUI.Path(R.transform), Diag.V(b.min), Diag.V(b.max), mats);
            }
        }

        static readonly List<Vector3> s_verts = new List<Vector3>();
        static readonly List<int> s_tris = new List<int>();

        /// What the player's body (a cylinder of radius r real metres around the station's neutral spot, from the
        /// floor to h above the eyes) passes through. Meshes that can be read are tested triangle by triangle;
        /// others (static batches, unreadable meshes) by their bounds.
        internal static List<BodyHit> BodyHits(float r, float h, bool all, bool fresh = false)
        {
            var res = new List<BodyHit>();
            VRRig rig = VRRig.I;
            if (rig == null) return res;
            float sc = rig.Scale;
            Vector3 axis = rig.AnchorPos;
            float rad = r * sc, y0 = axis.y + 0.02f * sc, y1 = axis.y + (rig.EyeHeight + h) * sc;
            foreach (Renderer rd in fresh ? new List<Renderer>(UnityEngine.Object.FindObjectsOfType<Renderer>()) : SceneScan.Renderers())
            {
                if (rd == null || !rd.enabled || !rd.gameObject.activeInHierarchy) continue;
                if (rd is ParticleSystemRenderer || rd is LineRenderer || rd is TrailRenderer) continue;
                if (rd.gameObject.layer == VRUI.Layer || rd.gameObject.layer == Limbo.Layer) continue;
                Transform root = rd.transform.root;
                if (root.name.StartsWith("HoF2VR")) continue;
                Bounds b = rd.bounds;
                if (b.max.y < y0 || b.min.y > y1) continue;
                if (FlatDist(b, axis) > rad) continue;
                var hit = new BodyHit { R = rd, How = "bounds", D = FlatDist(b, axis) / sc, YMin = (Mathf.Max(b.min.y, y0) - axis.y) / sc, YMax = (Mathf.Min(b.max.y, y1) - axis.y) / sc };
                Mesh mesh = null;
                MeshFilter mf = rd.GetComponent<MeshFilter>();
                if (mf != null && !rd.isPartOfStaticBatch) mesh = mf.sharedMesh;
                SkinnedMeshRenderer smr = rd as SkinnedMeshRenderer;
                if (smr != null) mesh = null;   // skinned: bounds only (characters)
                if (mesh != null && mesh.isReadable)
                {
                    hit.How = "mesh";
                    s_verts.Clear();
                    mesh.GetVertices(s_verts);
                    Matrix4x4 m = rd.transform.localToWorldMatrix;
                    for (int i = 0; i < s_verts.Count; i++) s_verts[i] = m.MultiplyPoint3x4(s_verts[i]);
                    float dmin = float.MaxValue, ylo = float.MaxValue, yhi = float.MinValue;
                    int n = 0;
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    {
                        if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                        s_tris.Clear();
                        mesh.GetTriangles(s_tris, sub);
                        for (int t = 0; t + 2 < s_tris.Count; t += 3)
                        {
                            Vector3 p0 = s_verts[s_tris[t]], p1 = s_verts[s_tris[t + 1]], p2 = s_verts[s_tris[t + 2]];
                            float tlo = Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y)), thi = Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y));
                            if (thi < y0 || tlo > y1) continue;
                            float d = TriFlatDist(p0, p1, p2, axis);
                            if (d < dmin) dmin = d;
                            if (d > rad) continue;
                            n++;
                            ylo = Mathf.Min(ylo, Mathf.Max(tlo, y0));
                            yhi = Mathf.Max(yhi, Mathf.Min(thi, y1));
                        }
                    }
                    hit.Tris = n;
                    hit.D = dmin == float.MaxValue ? -1f : dmin / sc;
                    if (n == 0) { if (!all) continue; hit.YMin = hit.YMax = 0f; hit.How = "mesh-miss"; }
                    else { hit.YMin = (ylo - axis.y) / sc; hit.YMax = (yhi - axis.y) / sc; }
                }
                else if (mesh != null) hit.How = "bounds (unreadable mesh)";
                res.Add(hit);
            }
            res.Sort((x, y) => x.D.CompareTo(y.D));
            return res;
        }

        static float FlatDist(Bounds b, Vector3 p)
        {
            float dx = Mathf.Max(0f, Mathf.Max(b.min.x - p.x, p.x - b.max.x)), dz = Mathf.Max(0f, Mathf.Max(b.min.z - p.z, p.z - b.max.z));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// Horizontal (xz) distance from a vertical axis through p to a triangle's projection.
        static float TriFlatDist(Vector3 a, Vector3 b, Vector3 c, Vector3 p)
        {
            var A = new Vector2(a.x, a.z); var B = new Vector2(b.x, b.z); var C = new Vector2(c.x, c.z); var P = new Vector2(p.x, p.z);
            float d1 = Cross(B - A, P - A), d2 = Cross(C - B, P - B), d3 = Cross(A - C, P - C);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            if (!(neg && pos)) return 0f;   // inside (or degenerate)
            return Mathf.Min(SegDist(P, A, B), Mathf.Min(SegDist(P, B, C), SegDist(P, C, A)));
        }

        static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

        static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude > 1e-12f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return (a + ab * t - p).magnitude;
        }

        /// A widget's rectangle on the UI texture (pixels, origin bottom-left) from its world corners.
        internal static bool RtRect(UIWidget w, out Rect px)
        {
            px = default(Rect);
            Camera cam = VRUI.I != null ? VRUI.I.UiCamera : null;
            if (cam == null || w == null) return false;
            Vector3[] c = w.worldCorners;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (Vector3 p in c)
            {
                Vector3 s = cam.WorldToScreenPoint(p);
                x0 = Mathf.Min(x0, s.x); y0 = Mathf.Min(y0, s.y); x1 = Mathf.Max(x1, s.x); y1 = Mathf.Max(y1, s.y);
            }
            px = Rect.MinMaxRect(x0, y0, x1, y1);
            return true;
        }

        static string UiWhere(string key, int max)
        {
            var sb = new StringBuilder();
            int n = 0;
            foreach (UIRect rect in UnityEngine.Object.FindObjectsOfType<UIRect>())
            {
                if (rect == null || !rect.isActiveAndEnabled) continue;
                string path = VRUI.Path(rect.transform);
                if (path.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (n++ >= max) break;
                sb.AppendFormat("\n  {0} [{1}] alpha={2:0.##} layer={3}", path, rect.GetType().Name, rect.finalAlpha, rect.gameObject.layer);
                UIWidget w = rect as UIWidget;
                Rect px;
                if (w != null && RtRect(w, out px))
                    sb.AppendFormat(Inv, " rt=({0:0},{1:0})-({2:0},{3:0}) {4}x{5} visible={6}", px.xMin, px.yMin, px.xMax, px.yMax, w.width, w.height, w.isVisible);
                UIPanel p = rect as UIPanel;
                if (p != null) sb.AppendFormat(" clip={0} region={1} depth={2}", p.clipping, p.finalClipRegion, p.depth);
                if (rect.isAnchored)
                    sb.AppendFormat(" anchors L={0} R={1} B={2} T={3} update={4}", T(rect.leftAnchor.target), T(rect.rightAnchor.target), T(rect.bottomAnchor.target), T(rect.topAnchor.target), rect.updateAnchors);
                for (Transform t = rect.transform; t != null; t = t.parent)
                    foreach (MonoBehaviour b in t.GetComponents<MonoBehaviour>())
                    {
                        if (b == null) continue;
                        string tn = b.GetType().Name;
                        if (tn == "UIAnchor") { UIAnchor ua = (UIAnchor)b; sb.AppendFormat("\n     UIAnchor on '{0}' side={1} cam={2} once={3} rel={4} px={5}", t.name, ua.side, ua.uiCamera ? ua.uiCamera.name : "-", ua.runOnlyOnce, ua.relativeOffset, ua.pixelOffset); }
                        else if (tn.Contains("Anchor") || tn.Contains("Attach") || tn.Contains("Follow") || tn.Contains("Stretch") || tn == "UIGrid" || tn == "UITable" || tn == "Transition")
                            sb.AppendFormat("\n     {0} on '{1}'{2}", tn, t.name, b.enabled ? "" : " (off)");
                    }
            }
            return n + " matches" + sb;
        }

        static string T(Transform t) => t == null ? "-" : t.name;
    }
}

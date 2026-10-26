using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// World-scale probes: how tall the game's characters are in world units, where their eyes are,
    /// and where the VR head currently sits, so scale decisions rest on numbers, not impressions.
    internal static class Measure
    {
        public static void Register()
        {
            Diag.Commands["bodies"] = a => { Plugin.L.LogInfo("bodies:" + Bodies()); return "logged"; };
            // `bounds <path substring> [max]`: renderer + collider bounds of matching objects
            Diag.Commands["bounds"] = a =>
            {
                int max, n = 0;
                string key = KeyAndMax(a, 20, out max);
                var sb = new StringBuilder();
                foreach (Transform t in Object.FindObjectsOfType<Transform>())
                {
                    string path = VRUI.Path(t);
                    if (path.IndexOf(key, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Renderer r = t.GetComponent<Renderer>();
                    Collider c = t.GetComponent<Collider>();
                    if (r == null && c == null) continue;
                    if (n++ >= max) break;
                    sb.AppendFormat("\n  {0} active={1}", path, t.gameObject.activeInHierarchy);
                    if (r != null) sb.AppendFormat(" | R min={0} max={1}", Diag.V(r.bounds.min), Diag.V(r.bounds.max));
                    if (c != null) sb.AppendFormat(" | C min={0} max={1}", Diag.V(c.bounds.min), Diag.V(c.bounds.max));
                }
                Plugin.L.LogInfo("bounds '" + key + "':" + sb);
                return n + " logged";
            };
            // `tf <path substring> [max]`: transforms (also without renderers) with world pose and scale
            Diag.Commands["tf"] = a =>
            {
                int max, n = 0;
                string key = KeyAndMax(a, 30, out max);
                var sb = new StringBuilder();
                foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (!t.gameObject.scene.IsValid()) continue;
                    string path = VRUI.Path(t);
                    if (path.IndexOf(key, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (n++ >= max) break;
                    sb.AppendFormat("\n  {0} active={1} pos={2} rot={3} lossy={4} local={5}", path, t.gameObject.activeInHierarchy, Diag.V(t.position),
                        Diag.V(t.eulerAngles), Diag.V(t.lossyScale), Diag.V(t.localPosition));
                }
                Plugin.L.LogInfo("tf '" + key + "':" + sb);
                return n + " logged";
            };
            // `near x y z r [h]`: renderers whose bounds reach within r (horizontally) of the vertical
            // column from y to y+h (default 2.3) - what a player standing at (x,z) on floor y touches
            Diag.Commands["near"] = a =>
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                float x = float.Parse(a[1], inv), y = float.Parse(a[2], inv), z = float.Parse(a[3], inv), r = float.Parse(a[4], inv);
                float h = a.Length > 5 ? float.Parse(a[5], inv) : 2.3f;
                var sb = new StringBuilder();
                int n = 0;
                foreach (Renderer rd in Object.FindObjectsOfType<Renderer>())
                {
                    if (!rd.enabled || !rd.gameObject.activeInHierarchy || rd is ParticleSystemRenderer) continue;
                    Bounds b = rd.bounds;
                    if (b.max.y < y + 0.02f || b.min.y > y + h) continue;
                    float dx = Mathf.Max(0f, Mathf.Max(b.min.x - x, x - b.max.x)), dz = Mathf.Max(0f, Mathf.Max(b.min.z - z, z - b.max.z));
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d > r || b.size.magnitude > 8f) continue;   // skip the room shell itself
                    if (n++ > 40) break;
                    sb.AppendFormat("\n  d={0:0.00} {1} min={2} max={3}", d, VRUI.Path(rd.transform), Diag.V(b.min), Diag.V(b.max));
                }
                Plugin.L.LogInfo("near (" + x + "," + y + "," + z + ") r " + r + ":" + sb);
                return n + " logged";
            };
            // `ground x y z`: the first surface below a point
            Diag.Commands["ground"] = a =>
            {
                var p = new Vector3(float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture));
                RaycastHit hit;
                if (!Physics.Raycast(p, Vector3.down, out hit, 50f, ~0, QueryTriggerInteraction.Ignore)) return "no ground within 50 m";
                return "ground y=" + hit.point.y.ToString("0.###") + " (" + (p.y - hit.point.y).ToString("0.###") + " below) '" + VRUI.Path(hit.collider.transform) + "' layer " + hit.collider.gameObject.layer;
            };
        }

        /// `<cmd> <key with spaces> [max]`: the key is everything up to an optional number at the end.
        static string KeyAndMax(string[] a, int def, out int max)
        {
            int last = a.Length - 1;
            max = def;
            if (last >= 2 && int.TryParse(a[last], out max)) last--;
            else max = def;
            return string.Join(" ", a, 1, last);
        }

        static float Ground(Vector3 p)
        {
            RaycastHit hit;
            if (Physics.Raycast(p + Vector3.up * 1.5f, Vector3.down, out hit, 6f, ~0, QueryTriggerInteraction.Ignore)) return hit.point.y;
            return float.NaN;
        }

        static void Body(StringBuilder sb, string label, Transform root, Animator an, Model model)
        {
            Vector3 p = root.position;
            sb.AppendFormat("\n  {0} '{1}' at {2} lossy={3} groundY={4:0.###}", label, root.name, Diag.V(p), Diag.V(root.lossyScale), Ground(p));
            int n;
            Bounds b = TableGeo.RendererBounds((model != null ? model.gameObject : root.gameObject), out n);
            if (n > 0) sb.AppendFormat(" | renderers={0} top={1:0.###} (h {2:0.###}) size={3}", n, b.max.y, b.max.y - p.y, Diag.V(b.size));
            if (an != null && an.isHuman)
            {
                Transform head = an.GetBoneTransform(HumanBodyBones.Head);
                Transform foot = an.GetBoneTransform(HumanBodyBones.LeftFoot);
                if (head != null) sb.AppendFormat(" | head bone h {0:0.###}", head.position.y - p.y);
                if (foot != null) sb.AppendFormat(" foot h {0:0.###}", foot.position.y - p.y);
                sb.AppendFormat(" humanScale={0:0.###}", an.humanScale);
            }
            if (model != null)
            {
                Transform[] eyes = model.FindMultiAttachNode("eyes");
                if (eyes != null && eyes.Length > 0)
                {
                    Vector3 m = Vector3.zero;
                    foreach (Transform e in eyes) m += e.position;
                    sb.AppendFormat(" | eyes h {0:0.###}", m.y / eyes.Length - p.y);
                }
            }
            UnityEngine.AI.NavMeshAgent agent = root.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent != null) sb.AppendFormat(" | agent h {0:0.##} r {1:0.##}", agent.height, agent.radius);
        }

        public static string Bodies()
        {
            var sb = new StringBuilder();
            foreach (Controller c in Object.FindObjectsOfType<Controller>())
            {
                if (!c.isActiveAndEnabled) continue;
                Model m = c.Model;
                Body(sb, c is PlayerController ? "HERO" : c.GetType().Name, c.transform, m != null ? m.Animator : null, m);
            }
            Dealer d = Dealer.Instance;
            if (d != null) Body(sb, "DEALER", d.transform, d.GetComponentInChildren<Animator>(), null);
            if (VRRig.I != null)
            {
                Vector3 h = VRRig.I.HeadWorldPos;
                sb.AppendFormat("\n  VR head world {0} groundY={1:0.###} (eye {2:0.###} above ground) anchor {3} tracking head y {4:0.###}",
                    Diag.V(h), Ground(h), h.y - Ground(h), Diag.V(VRRig.I.AnchorPos), VRInput.HeadPos.y);
            }
            return sb.ToString();
        }
    }
}

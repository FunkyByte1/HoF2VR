using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// Test-only navigation helpers that still go through the real input paths (laser + NGUI + InControl).
    ///   ui list [filter]          clickable 2D widgets with their panel pixel positions
    ///   ui click <name>           aim the laser at the widget whose name/path contains <name> and click
    ///   ui hover <name>           aim only
    ///   ui clickpx <x> <y>        click a panel pixel
    ///   world list [filter]       colliders the 3D UI camera can pick (cards, slots, dice...)
    ///   world click <name>        aim at a 3D collider and click
    internal static class TestSeams
    {
        public static void Register()
        {
            Diag.Commands["ui"] = Ui;
            Diag.Commands["world"] = World;
            // `herobody`: the hero model's renderers - drawn or shadows only (in VR fights the body is shadows only), layer, bounds
            Diag.Commands["herobody"] = a =>
            {
                PlayerController pc = PlayerController.Instance;
                Model model = pc != null ? pc.Model : null;
                if (model == null) return "no hero model";
                var sb = new StringBuilder();
                int drawn = 0, n = 0;
                foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                    n++;
                    bool vis = r.enabled && r.gameObject.activeInHierarchy && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                    if (vis) drawn++;
                    sb.Append("\n  ").Append(vis ? "DRAWN " : "hidden ").Append(VRUI.Path(r.transform)).Append(" layer ").Append(r.gameObject.layer)
                      .Append(" ").Append(r.shadowCastingMode).Append(" at ").Append(Diag.V(r.bounds.center));
                }
                Plugin.L.LogInfo("herobody " + model.name + ": " + drawn + " of " + n + " drawn" + sb);
                return drawn + " of " + n + " drawn (see log)";
            };
            // `labels <text substring>`: 2D labels showing that text, where they are on the panel and
            // which components up their hierarchy place them (attachments to 3D objects etc.)
            Diag.Commands["labels"] = a =>
            {
                string key = string.Join(" ", a.Skip(1).ToArray());
                var sb = new StringBuilder();
                int n = 0;
                Camera cam = VRUI.I != null ? VRUI.I.UiCamera : null;
                foreach (UILabel l in UnityEngine.Object.FindObjectsOfType<UILabel>())
                {
                    if (!l.isActiveAndEnabled || l.text == null || l.text.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    n++;
                    Vector3 sp = cam != null ? cam.WorldToScreenPoint(l.transform.position) : Vector3.zero;
                    sb.AppendFormat("\n  '{0}' at px ({1:0},{2:0}) {3}", l.text.Replace("\n", " "), sp.x, sp.y, VRUI.Path(l.transform));
                    for (Transform t = l.transform; t != null; t = t.parent)
                        foreach (MonoBehaviour b in t.GetComponents<MonoBehaviour>())
                        {
                            if (b == null) continue;
                            string tn = b.GetType().Name;
                            if (tn.Contains("Attach") || tn.Contains("Anchor") || tn.Contains("Follow") || tn.Contains("Track") || tn.Contains("World"))
                                sb.AppendFormat("\n     {0} on '{1}'", tn, t.name);
                        }
                }
                Plugin.L.LogInfo("labels '" + key + "':" + sb);
                return n + " labels (see log)";
            };
            // `uidraw [layer]`: what draws into the 2D UI texture - every live renderer on the layer (default 17, the 2D UI)
            // with its material, shader and texture, and the behaviours on the UI cameras (image effects write the texture too)
            Diag.Commands["uidraw"] = a =>
            {
                int layer = a.Length > 1 ? int.Parse(a[1]) : 17;
                var sb = new StringBuilder();
                int n = 0;
                foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.gameObject.layer != layer) continue;
                    n++;
                    Material m = r.sharedMaterial;
                    sb.AppendFormat("\n  {0} [{1}] mat={2} shader={3} tex={4} q={5}", VRUI.Path(r.transform), r.GetType().Name, m != null ? m.name : "-",
                                    m != null && m.shader != null ? m.shader.name : "-", m != null && m.mainTexture != null ? m.mainTexture.name : "-", m != null ? m.renderQueue : 0);
                }
                foreach (Camera c in Camera.allCameras)
                {
                    if (c == null || (c.cullingMask & (1 << layer)) == 0) continue;
                    sb.AppendFormat("\n  camera '{0}' depth {1} clear {2}:", c.name, c.depth, c.clearFlags);
                    foreach (Behaviour b in c.GetComponents<Behaviour>()) sb.AppendFormat(" {0}{1}", b.GetType().Name, b.enabled ? "" : "(off)");
                }
                Plugin.L.LogInfo("uidraw layer " + layer + ":" + sb);
                return n + " renderers (see log)";
            };
            Diag.Commands["widgets"] = a =>
            {
                // `widgets <min width> [path part]`: with a path part, also inactive ones (marked) - e.g. `widgets 0 Failure`
                int min = a.Length > 1 ? int.Parse(a[1]) : 1200;
                string part = a.Length > 2 ? a[2] : null;
                var sb = new StringBuilder();
                int n = 0;
                UIWidget[] all = part != null ? Resources.FindObjectsOfTypeAll<UIWidget>() : UnityEngine.Object.FindObjectsOfType<UIWidget>();
                foreach (UIWidget w in all)
                {
                    if (w == null || !w.gameObject.scene.IsValid() || w.width < min) continue;
                    bool live = w.enabled && w.gameObject.activeInHierarchy;
                    if (part == null && !live) continue;
                    string path = VRUI.Path(w.transform);
                    if (part != null && path.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    n++;
                    UISprite sp = w as UISprite;
                    sb.AppendFormat("\n  {0}{1}x{2} a={3:0.##} {4} [{5}{6}] color={7}", live ? "" : "(off) ", w.width, w.height, w.finalAlpha, path, w.GetType().Name,
                                    sp != null ? " " + sp.spriteName : "", w.color);
                }
                Plugin.L.LogInfo("widgets >= " + min + ":" + sb);
                return n + " widgets (see log)";
            };
        }

        static List<KeyValuePair<string, Vector2>> UiTargets(string filter)
        {
            var res = new List<KeyValuePair<string, Vector2>>();
            Camera cam = VRUI.I != null ? VRUI.I.UiCamera : null;
            if (cam == null) return res;
            foreach (Collider c in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                if (!c.enabled || !c.gameObject.activeInHierarchy || c.gameObject.layer != 17) continue;
                Vector3 sp = cam.WorldToScreenPoint(c.bounds.center);
                if (sp.x < 0 || sp.y < 0 || sp.x > VRUI.Width || sp.y > VRUI.Height) continue;
                string path = VRUI.Path(c.transform);
                if (filter != null && path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                res.Add(new KeyValuePair<string, Vector2>(path, new Vector2(sp.x, sp.y)));
            }
            return res;
        }

        static string Ui(string[] a)
        {
            string op = a.Length > 1 ? a[1] : "list";
            string arg = a.Length > 2 ? string.Join(" ", a.Skip(2).ToArray()) : null;
            if (op == "clickpx")
            {
                VRPointer.AimPanel(new Vector2(float.Parse(a[2]), float.Parse(a[3])), true);
                return "clicking px";
            }
            var targets = UiTargets(op == "list" ? arg : arg);
            if (op == "list")
            {
                var sb = new StringBuilder();
                foreach (var t in targets) sb.AppendFormat("\n  ({0:0},{1:0}) {2}", t.Value.x, t.Value.y, t.Key);
                Plugin.L.LogInfo("ui targets:" + sb);
                return targets.Count + " targets (see log)";
            }
            if (targets.Count == 0) return "no ui target matching '" + arg + "'";
            // prefer the shortest path (the button itself, not its children)
            var best = targets.OrderBy(t => t.Key.Length).First();
            VRPointer.AimPanel(best.Value, op == "click");
            return (op == "click" ? "clicking " : "hovering ") + best.Key + " at " + best.Value;
        }

        static string World(string[] a)
        {
            string op = a.Length > 1 ? a[1] : "list";
            string arg = a.Length > 2 ? string.Join(" ", a.Skip(2).ToArray()) : null;
            var cols = new List<Collider>();
            foreach (Collider c in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                if (!c.enabled || !c.gameObject.activeInHierarchy || c.gameObject.layer == 17) continue;
                if (c.GetComponentInParent<UISelectable>() == null) continue;
                string path = VRUI.Path(c.transform);
                if (arg != null && path.IndexOf(arg, StringComparison.OrdinalIgnoreCase) < 0) continue;
                cols.Add(c);
            }
            if (op == "list")
            {
                var sb = new StringBuilder();
                foreach (Collider c in cols)
                    sb.AppendFormat("\n  {0} layer={1} at {2}", VRUI.Path(c.transform), c.gameObject.layer, Diag.V(c.bounds.center));
                Plugin.L.LogInfo("world targets:" + sb);
                return cols.Count + " targets (see log)";
            }
            if (cols.Count == 0) return "no world target matching '" + arg + "'";
            Collider best = cols.OrderBy(c => VRUI.Path(c.transform).Length).First();
            VRPointer.AimWorld(best.bounds.center, op == "click");
            return (op == "click" ? "clicking " : "hovering ") + VRUI.Path(best.transform);
        }
    }
}

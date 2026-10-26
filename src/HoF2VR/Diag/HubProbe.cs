using System.Globalization;
using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// `hubgeo` (round 5 item 4): the side table where the challenge map and the deck are chosen - the player's spot there,
    /// the challenge map's pins (where the map lies), the furniture under them (the table's long side), the 2D panel, and the
    /// deck builder's two rows of cards. Real metres where it says so; world coordinates otherwise. -> the log.
    internal static class HubProbe
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Register()
        {
            Diag.Commands["hubgeo"] = a => Probe();
            Diag.Commands["headlook"] = HeadLook;
            Diag.Commands["invgeo"] = a => InvGeo();
            Diag.Commands["dbgeo"] = a => DbGeo();
        }

        /// `invgeo`: the open inventory's parts - its root, the slots' root, its containers - with their distance from the
        /// eyes (real m) and their parents (round 5 item 5: the inventory's cards hang 0.31-0.37 m from the eyes).
        static string InvGeo()
        {
            UIInventory inv = UIInventory.Instance;
            if (inv == null || VRRig.I == null) return "no inventory";
            Vector3 eye = VRRig.I.HeadWorldPos;
            float sc = VRRig.I.Scale;
            var sb = new StringBuilder("inventory geometry (active " + inv.isActiveAndEnabled + ", state " + (inv.ActiveState != null ? inv.ActiveState.GetType().Name : "-") + "):");
            System.Action<string, Transform> line = (n, t) =>
            {
                if (t == null) { sb.Append("\n  ").Append(n).Append(": -"); return; }
                sb.Append("\n  ").Append(n).Append(": ").Append(VRUI.Path(t)).Append(" at ").Append((Vector3.Distance(t.position, eye) / sc).ToString("0.00", Inv)).Append(" m, scale ").Append(Diag.V(t.lossyScale));
            };
            line("root", inv.transform);
            line("slots", inv.SlotsTransform);
            line("slots top", inv.SlotsPositionTop);
            line("slots centre", inv.SlotsPositionCentre);
            int i = 0;
            foreach (CardContainer c in inv.Containers)
            {
                if (c == null) continue;
                float near = float.MaxValue;
                foreach (Card card in c.Cards) if (card != null) near = Mathf.Min(near, Vector3.Distance(card.transform.position, eye) / sc);
                line("container " + (i++) + " (" + c.Cards.Count + " cards, nearest " + (near < 100f ? near.ToString("0.00", Inv) : "-") + ")", c.transform);
                // (P2-4) each card's top and bottom edge on the 2D panel (its pixels, as the slot icons and trait lists are placed)
                foreach (Card card in c.Cards)
                {
                    if (card == null || !card.gameObject.activeInHierarchy) continue;
                    Bounds cb = default(Bounds);
                    bool has = false;
                    foreach (Renderer r in card.GetComponentsInChildren<Renderer>()) { if (!r.enabled) continue; if (!has) { cb = r.bounds; has = true; } else cb.Encapsulate(r.bounds); }
                    if (!has) continue;
                    Vector2 top = PanelPx(new Vector3(cb.center.x, cb.max.y, cb.center.z), eye), bot = PanelPx(new Vector3(cb.center.x, cb.min.y, cb.center.z), eye);
                    sb.Append("\n    ").Append(card.name.Replace("(Clone)", "")).Append(" px x ").Append(top.x.ToString("0", Inv)).Append(" top ").Append(top.y.ToString("0", Inv)).Append(" bottom ").Append(bot.y.ToString("0", Inv))
                      .Append(" depth ").Append((Vector3.Dot(cb.center - eye, VRUI.I != null && VRUI.I.Panel != null ? VRUI.I.Panel.forward : Vector3.forward) / sc).ToString("0.000", Inv))
                      .Append(" size ").Append(Diag.V(cb.size / sc));
                }
            }
            if (VRUI.I != null && VRUI.I.Panel != null)
                sb.Append("\n  panel at depth ").Append((Vector3.Dot(VRUI.I.Panel.position - eye, VRUI.I.Panel.forward) / sc).ToString("0.000", Inv)).Append(" m, ").Append(VRUI.Width).Append("x").Append(VRUI.Height)
                  .Append(" px, height ").Append((VRUI.I.Panel.localScale.y / sc).ToString("0.000", Inv)).Append(" m");
            Plugin.L.LogInfo(sb.ToString());
            return "logged";
        }

        /// A world point's pixel on the 2D panel seen from `eye` (unclamped; -1e5 when the line misses it).
        static Vector2 PanelPx(Vector3 world, Vector3 eye)
        {
            if (VRUI.I == null || VRUI.I.Panel == null) return new Vector2(-1e5f, -1e5f);
            Transform pn = VRUI.I.Panel;
            var ray = new Ray(eye, world - eye);
            float d;
            if (!new Plane(pn.forward, pn.position).Raycast(ray, out d) || d <= 0f) return new Vector2(-1e5f, -1e5f);
            Vector3 l = pn.InverseTransformPoint(ray.GetPoint(d));
            return new Vector2((l.x + 0.5f) * VRUI.Width, (l.y + 0.5f) * VRUI.Height);
        }

        /// `dbgeo` (P2-6/P2-7): the challenge deck builder's parts in the station's frame (real metres: ahead, up from the
        /// floor, right; directions as (right, up, ahead)) - its root, each visible mode's containers with their first card's
        /// face, the challenge card, its other visible renderers, the boards and the map case's frame. -> the log.
        static string DbGeo()
        {
            VRRig rig = VRRig.I;
            DeckBuilder cb = DeckBuilder.Instance;
            if (rig == null || cb == null) return "no rig / deck builder";
            float sc = rig.Scale;
            Quaternion inv = Quaternion.Inverse(Quaternion.Euler(0f, rig.StationYaw, 0f));
            Vector3 floor = rig.StationFloor;
            System.Func<Vector3, string> P = w => { Vector3 d = inv * (w - floor) / sc; return "(" + d.z.ToString("0.000", Inv) + " ahead, " + d.y.ToString("0.000", Inv) + " up, " + d.x.ToString("0.000", Inv) + " right)"; };
            System.Func<Vector3, string> D = w => { Vector3 d = inv * w; return "(" + d.x.ToString("0.00", Inv) + "," + d.y.ToString("0.00", Inv) + "," + d.z.ToString("0.00", Inv) + ")"; };
            System.Func<Bounds, string> B = b =>
            {
                Vector3 a = inv * (b.min - floor) / sc, c = inv * (b.max - floor) / sc;
                return "ahead " + Mathf.Min(a.z, c.z).ToString("0.000", Inv) + ".." + Mathf.Max(a.z, c.z).ToString("0.000", Inv) + " up " + a.y.ToString("0.000", Inv) + ".." + c.y.ToString("0.000", Inv) +
                       " right " + Mathf.Min(a.x, c.x).ToString("0.000", Inv) + ".." + Mathf.Max(a.x, c.x).ToString("0.000", Inv);
            };
            Transform root = cb.transform;
            var sb = new StringBuilder("deck builder geometry (state " + GameWatch.State + ", menu " + Diag.CurrentMenu() + ", place " + Places.Current + ", active " + cb.IsActive + "):");
            sb.Append("\n  station floor ").Append(Diag.V(floor)).Append(" yaw ").Append(rig.StationYaw.ToString("0", Inv)).Append(" eye ").Append(P(rig.NeutralEye)).Append(" scale ").Append(sc.ToString("0.00", Inv));
            sb.Append("\n  root ").Append(VRUI.Path(root)).Append(" at ").Append(P(root.position)).Append(" fwd ").Append(D(root.forward)).Append(" up ").Append(D(root.up)).Append(" lossy ").Append(Diag.V(root.lossyScale))
              .Append(" local ").Append(Diag.V(root.localPosition)).Append(" ").Append(Diag.V(root.localEulerAngles));
            System.Action<string, CardContainer> box = (name, c) =>
            {
                if (c == null) { sb.Append("\n    ").Append(name).Append(": -"); return; }
                sb.Append("\n    ").Append(name).Append(": ").Append(VRUI.Path(c.transform).Replace(VRUI.Path(root), "~")).Append(" active ").Append(c.gameObject.activeInHierarchy)
                  .Append(" at ").Append(P(c.transform.position)).Append(" (root-local ").Append(Diag.V(root.InverseTransformPoint(c.transform.position))).Append(") up ").Append(D(c.transform.up)).Append(" fwd ").Append(D(c.transform.forward))
                  .Append(" cards ").Append(c.Cards.Count);
                Bounds rb = default(Bounds);
                bool has = false;
                foreach (Card card in c.Cards)
                {
                    if (card == null || !card.gameObject.activeInHierarchy) continue;
                    foreach (Renderer r in card.GetComponentsInChildren<Renderer>())
                    {
                        if (!r.enabled) continue;
                        if (!has) { rb = r.bounds; has = true; } else rb.Encapsulate(r.bounds);
                    }
                    if (card == c.Cards[0]) sb.Append(" | card 0 up ").Append(D(card.transform.up)).Append(" fwd ").Append(D(card.transform.forward)).Append(" lossy ").Append(Diag.V(card.transform.lossyScale));
                }
                if (has) sb.Append(" | row ").Append(B(rb));
            };
            foreach (DeckBuilderMode m in cb.AllModes)
            {
                if (m == null || !m.gameObject.activeInHierarchy) continue;
                sb.Append("\n  mode ").Append(m.GetType().Name).Append(m == cb.ActiveMode ? " (active)" : "").Append(" at ").Append(P(m.transform.position));
                if (m.Deck != null) { box("deck slot", m.Deck.Container); box("deck pool", m.Deck.CardPoolContainer); }
                box("player deck", m.PlayerDeckContainer);
                var f = HarmonyLib.AccessTools.Field(m.GetType(), "m_cardSetBrowser");
                CardBrowser set = f != null ? f.GetValue(m) as CardBrowser : null;
                box("card set", set != null ? set.Container : null);
            }
            box("challenge", cb.ChallengeContainer);
            int n = 0;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r.GetComponentInParent<Card>() != null) continue;
                if (n++ >= 30) break;
                sb.Append("\n  renderer ").Append(VRUI.Path(r.transform).Replace(VRUI.Path(root), "~")).Append(" layer ").Append(r.gameObject.layer).Append(" queue ").Append(r.sharedMaterial != null ? r.sharedMaterial.renderQueue : -1)
                  .Append(" ").Append(B(r.bounds));
            }
            foreach (Renderer r in SceneScan.Renderers())
            {
                if (r == null) continue;
                if (!SideTable.IsBoard(r.transform) && VRUI.Path(r.transform) != SideTable.CaseFramePath) continue;
                sb.Append("\n  room ").Append(VRUI.Path(r.transform)).Append(" enabled ").Append(r.enabled).Append(" ").Append(B(r.bounds));
            }
            Plugin.L.LogInfo(sb.ToString());
            return "logged";
        }

        /// `headlook <path part> from <x y z>` (tests): the xrsim head command (OpenXR stage metres, yaw/pitch in degrees)
        /// that puts the eyes at x y z looking at the middle of the visible renderers whose path holds that part.
        static string HeadLook(string[] a)
        {
            int f = System.Array.IndexOf(a, "from");
            if (f < 2 || a.Length < f + 4 || VRRig.I == null) return "usage: headlook <path part> from <x y z>";
            string part = string.Join(" ", a, 1, f - 1);
            float sx = float.Parse(a[f + 1], Inv), sy = float.Parse(a[f + 2], Inv), sz = float.Parse(a[f + 3], Inv);
            Bounds b = default(Bounds);
            int n = 0;
            foreach (Renderer r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (VRUI.Path(r.transform).IndexOf(part, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (n == 0) b = r.bounds; else b.Encapsulate(r.bounds);
                n++;
            }
            if (n == 0) return "none";
            Vector3 eye = VRRig.I.ToWorld(new Vector3(sx, sy, -sz));
            Vector3 d = VRRig.I.FromWorldDir(b.center - eye);
            float dx = d.x, dy = d.y, dz = -d.z;   // OpenXR: z mirrored, forward is -z
            float yaw = Mathf.Atan2(-dx, -dz) * Mathf.Rad2Deg, pitch = Mathf.Atan2(dy, Mathf.Sqrt(dx * dx + dz * dz)) * Mathf.Rad2Deg;
            return string.Format(Inv, "head {0:0.###} {1:0.###} {2:0.###} {3:0.#} {4:0.#} 0", sx, sy, sz, yaw, pitch);
        }

        static string Probe()
        {
            var sb = new StringBuilder("hub geometry (state " + GameWatch.State + ", place " + Places.Current + "):");
            VRRig rig = VRRig.I;
            float sc = rig != null ? rig.Scale : 1f;
            Vector3 floor;
            float yaw;
            if (Places.HubSpot(out floor, out yaw)) sb.Append("\n  hub spot floor ").Append(Diag.V(floor)).Append(" yaw ").Append(yaw.ToString("0", Inv));
            if (rig != null) sb.Append("\n  station ").Append(Diag.V(rig.StationFloor)).Append(" yaw ").Append(rig.StationYaw.ToString("0", Inv)).Append(" eye ").Append(Diag.V(rig.NeutralEye)).Append(" scale ").Append(sc.ToString("0.00", Inv));
            CardCamera cc = CardCamera.Instance;
            if (cc != null && cc.ChallengeMapCamera != null)
                sb.Append("\n  challenge map shot pivot ").Append(Diag.V(cc.ChallengeMapCamera.transform.position)).Append(" fwd ").Append(Diag.V(cc.ChallengeMapCamera.transform.forward));
            // the map: its pins
            Bounds pins = default(Bounds);
            int n = 0;
            foreach (ChallengeMapLocation l in Object.FindObjectsOfType<ChallengeMapLocation>())
            {
                if (!l.isActiveAndEnabled) continue;
                if (n == 0) pins = new Bounds(l.transform.position, Vector3.zero); else pins.Encapsulate(l.transform.position);
                n++;
            }
            if (n > 0)
            {
                sb.Append("\n  map pins ").Append(n).Append(": centre ").Append(Diag.V(pins.center)).Append(" size ").Append(Diag.V(pins.size));
                if (rig != null)
                {
                    Vector3 d = Quaternion.Inverse(rig.NeutralRot) * (pins.center - rig.NeutralEye) / sc;
                    sb.Append(" | from the eyes: ").Append(d.z.ToString("0.00", Inv)).Append(" ahead, ").Append((-d.y).ToString("0.00", Inv)).Append(" below, ").Append(d.x.ToString("0.00", Inv)).Append(" right (real m)");
                }
                // the furniture under the map: renderers whose bounds hold the pins' middle from below
                foreach (Renderer r in SceneScan.Renderers())
                {
                    if (r == null || !r.enabled) continue;
                    Bounds b = r.bounds;
                    if (b.size.x > 6f * sc || b.size.z > 6f * sc || b.size.x < 0.5f * sc) continue;
                    if (pins.center.x < b.min.x || pins.center.x > b.max.x || pins.center.z < b.min.z || pins.center.z > b.max.z) continue;
                    if (b.max.y > pins.center.y + 0.1f * sc || b.max.y < pins.center.y - 0.6f * sc) continue;
                    sb.Append("\n  under the map: '").Append(VRUI.Path(r.transform)).Append("' bounds ").Append(Diag.V(b.min)).Append(" - ").Append(Diag.V(b.max))
                      .Append(" (x ").Append((b.size.x / sc).ToString("0.00", Inv)).Append(" by z ").Append((b.size.z / sc).ToString("0.00", Inv)).Append(" real m, top ").Append(b.max.y.ToString("0.000", Inv)).Append(")");
                }
                // (P2-7) what stands between the player's spot and the map: the room's renderers whose box reaches into the
                // strip from the station to the pins' middle, knee to chest high (not the UI, not the map's own case)
                if (rig != null)
                {
                    Vector3 from = rig.StationFloor, to = new Vector3(pins.center.x, from.y, pins.center.z);
                    foreach (Renderer r in SceneScan.Renderers())
                    {
                        if (r == null || !r.enabled || r.transform.root.name == "UIManager" || r.transform.root.name.StartsWith("HoF2VR")) continue;
                        Bounds b = r.bounds;
                        if (b.size.x > 3f * sc || b.size.z > 3f * sc || b.max.y < from.y + 0.4f * sc || b.min.y > from.y + 1.3f * sc) continue;
                        Vector3 c = new Vector3(b.center.x, from.y, b.center.z);
                        Vector3 ab = to - from;
                        float t = Mathf.Clamp01(Vector3.Dot(c - from, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                        float side = Vector3.Distance(c, from + ab * t) - Mathf.Max(b.extents.x, b.extents.z);
                        if (side > 0.25f * sc || t <= 0f || t >= 1f) continue;
                        Vector3 dd = Quaternion.Inverse(rig.NeutralRot) * (b.center - rig.NeutralEye) / sc;
                        sb.Append("\n  between: '").Append(VRUI.Path(r.transform)).Append("' ").Append(dd.z.ToString("0.00", Inv)).Append(" ahead, top ")
                          .Append(((b.max.y - from.y) / sc).ToString("0.00", Inv)).Append(" m over the floor, size ").Append(Diag.V(b.size / sc));
                    }
                }
            }
            if (VRUI.I != null && VRUI.I.Panel != null) sb.Append("\n  2D panel at ").Append(Diag.V(VRUI.I.Panel.position)).Append(" scale ").Append(Diag.V(VRUI.I.Panel.localScale)).Append(" | map screen: ").Append(VRUI.MapPanelWhere);
            // the challenge's deck builder (its active mode) and the adventure deck builder's rows
            DeckBuilder cb = DeckBuilder.Instance;
            if (cb != null && cb.IsActive && cb.ActiveMode != null)
            {
                sb.Append("\n  challenge deck builder mode ").Append(cb.ActiveMode.GetType().Name).Append(" (").Append(cb.ActiveMode.Source != null ? cb.ActiveMode.Source.Count : -1).Append("/").Append(cb.ActiveMode.CardLimit).Append(")");
                Row(sb, "its deck row", cb.ActiveMode.PlayerDeckContainer, rig, sc);
                var f = HarmonyLib.AccessTools.Field(cb.ActiveMode.GetType(), "m_cardSetBrowser");
                CardBrowser set = f != null ? f.GetValue(cb.ActiveMode) as CardBrowser : null;
                Row(sb, "its card-set row", set != null ? set.Container : null, rig, sc);
            }
            AdventureDeckBuilder db = Object.FindObjectOfType<AdventureDeckBuilder>();
            if (db != null)
            {
                Row(sb, "deck row", db.DeckBrowser != null ? db.DeckBrowser.Container : null, rig, sc);
                Row(sb, "choice row", db.ChoiceBrowser != null ? db.ChoiceBrowser.Container : null, rig, sc);
            }
            else sb.Append("\n  no deck builder");
            Plugin.L.LogInfo(sb.ToString());
            return "logged (" + n + " pins" + (db != null ? ", deck builder" : "") + ")";
        }

        static void Row(StringBuilder sb, string name, CardContainer box, VRRig rig, float sc)
        {
            if (box == null) { sb.Append("\n  ").Append(name).Append(": -"); return; }
            Bounds b = default(Bounds);
            int n = 0;
            foreach (Card c in box.Cards)
            {
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                foreach (Renderer r in c.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled) continue;
                    if (n == 0) b = r.bounds; else b.Encapsulate(r.bounds);
                    n++;
                }
            }
            sb.Append("\n  ").Append(name).Append(": ").Append(box.Cards.Count).Append(" cards, container ").Append(VRUI.Path(box.transform));
            if (n > 0)
            {
                sb.Append(" bounds ").Append(Diag.V(b.min)).Append(" - ").Append(Diag.V(b.max));
                if (rig != null)
                {
                    Vector3 d = Quaternion.Inverse(rig.NeutralRot) * (b.center - rig.NeutralEye) / sc;
                    sb.Append(" | middle ").Append(d.z.ToString("0.00", Inv)).Append(" ahead, ").Append((-d.y).ToString("0.00", Inv)).Append(" below, ").Append(d.x.ToString("0.00", Inv)).Append(" right");
                }
            }
        }
    }
}

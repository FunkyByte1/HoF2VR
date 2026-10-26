using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// Card-table geometry helpers (where the table, the Dealer and the camera shots are).
    internal static class TableGeo
    {
        static string Grid(MapLayoutSlot s) => "(" + Mathf.RoundToInt(s.GridPosition.x) + ", " + Mathf.RoundToInt(s.GridPosition.y) + ")";

        public static void Register()
        {
            Diag.Commands["geo"] = a => { Plugin.L.LogInfo("table geometry:" + Describe()); return "logged"; };
            // `map` -> "player (x, y); movable (x, y) (x, y) ..." for tests (autoplay picks a move)
            Diag.Commands["map"] = a =>
            {
                if (Map.Instance == null) return "no map";
                MapLayoutSlot here = Map.Instance.LastSelectedSlot;
                var sb = new System.Text.StringBuilder("player " + (here != null ? Grid(here) : "-") + "; movable");
                foreach (MapLayoutSlot s in Object.FindObjectsOfType<MapLayoutSlot>())
                {
                    bool can;
                    try { can = s.CanMoveTo && s.Selectable != null && s.Selectable.isActiveAndEnabled; } catch { can = false; }
                    if (can && s != here) sb.Append(' ').Append(Grid(s));
                }
                return sb.ToString();
            };
        }

        public static Bounds RendererBounds(GameObject go, out int count)
        {
            var b = new Bounds();
            count = 0;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r is ParticleSystemRenderer) continue;
                if (count++ == 0) b = r.bounds;
                else b.Encapsulate(r.bounds);
            }
            return b;
        }

        static void Line(StringBuilder sb, string name, Transform t)
        {
            if (t == null) { sb.Append("\n  ").Append(name).Append(": -"); return; }
            sb.AppendFormat("\n  {0}: pos={1} rot={2} lossy={3} path={4}", name, Diag.V(t.position), Diag.V(t.eulerAngles),
                Diag.V(t.lossyScale), VRUI.Path(t));
        }

        static void Shot(StringBuilder sb, string name, FixedCamera c)
        {
            if (c == null) { sb.Append("\n  shot ").Append(name).Append(": -"); return; }
            sb.AppendFormat("\n  shot {0}: pivot={1} pivotRot={2} cam={3} camRot={4} dist={5} fov={6:0}", name, Diag.V(c.transform.position),
                Diag.V(c.transform.eulerAngles), Diag.V(c.Position), Diag.V(c.Rotation.eulerAngles), c.Distance.x, c.FOV);
        }

        public static string Describe()
        {
            var sb = new StringBuilder();
            CardTable table = CardTable.Instance;
            Line(sb, "CardTable", table ? table.transform : null);
            if (table != null && table.Prop != null)
            {
                int n;
                Bounds b = RendererBounds(table.Prop.gameObject, out n);
                sb.AppendFormat("\n  table prop bounds: center={0} size={1} ({2} renderers)", Diag.V(b.center), Diag.V(b.size), n);
            }
            Dealer dealer = Dealer.Instance;
            Line(sb, "Dealer", dealer ? dealer.transform : null);
            if (dealer != null)
            {
                int n;
                Bounds b = RendererBounds(dealer.gameObject, out n);
                sb.AppendFormat("\n  dealer bounds: center={0} size={1} ({2} renderers)", Diag.V(b.center), Diag.V(b.size), n);
            }
            CardCamera cc = CardCamera.Instance;
            if (cc != null)
            {
                sb.AppendFormat("\n  top shot: {0}", cc.TopCamera != null ? cc.TopCamera.ToString() : "-");
                Shot(sb, "Map", cc.MapCamera);
                Shot(sb, "ChallengeMap", cc.ChallengeMapCamera);
                Shot(sb, "Dice", cc.DiceCamera);
                Shot(sb, "Pendulum", cc.PendulumCamera);
                Shot(sb, "Wheel", cc.WheelCamera);
                Shot(sb, "DeckBuilder", cc.DeckBuilderCamera);
                Shot(sb, "Start", cc.StartCamera);
            }
            if (table != null)
            {
                Vector3 c = table.transform.position;
                foreach (Vector3 off in new[] { Vector3.zero, new Vector3(0, 0, -0.5f), new Vector3(0, 0, -1f), new Vector3(0.5f, 0, 0), new Vector3(-0.5f, 0, 0), new Vector3(0, 0, 0.5f) })
                {
                    RaycastHit hit;
                    Vector3 from = c + off + Vector3.up * 3f;
                    if (Physics.Raycast(from, Vector3.down, out hit, 10f, ~0, QueryTriggerInteraction.Ignore))
                        sb.AppendFormat("\n  down-ray at {0}: y={1:0.###} '{2}' layer={3}", Diag.V(c + off), hit.point.y, VRUI.Path(hit.collider.transform), hit.collider.gameObject.layer);
                    else sb.AppendFormat("\n  down-ray at {0}: no hit", Diag.V(c + off));
                }
            }
            int cards = 0;
            foreach (Card card in Object.FindObjectsOfType<Card>())
            {
                if (!card.gameObject.activeInHierarchy || cards++ > 12) continue;
                sb.AppendFormat("\n  card '{0}' at {1} rot={2}", card.name, Diag.V(card.transform.position), Diag.V(card.transform.eulerAngles));
            }
            Camera ui3d = VRRig.I != null ? VRRig.I.PrimaryCamera : null;
            Line(sb, "primary cam", ui3d ? ui3d.transform : null);
            if (ui3d != null) Line(sb, "primary cam parent", ui3d.transform.parent);
            return sb.ToString();
        }
    }
}

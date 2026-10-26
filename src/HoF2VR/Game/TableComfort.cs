using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The card-table spot, made comfortable (user feedback 2026-10-11):
    ///  * "I'm standing inside some sort of wooden things attached to the map table ... the spot where I'm standing
    ///    is actually good": whatever furniture passes through the player's body at the card-table spot (a
    ///    cylinder of BodyRadius real metres round the neutral spot, from the floor to above the head) is hidden
    ///    while the player stands there - whole pieces (a board with its trims), never the card table itself,
    ///    the cards, the dice or the Dealer. Measured at the user's spot: the deck-builder board beside the table
    ///    (Models/Dealer_Caravan/dealer_caravan_deckbuilder/DeckBuilder_Board (1) and its trims), 0.25 m to the
    ///    left at hip height. It is shown again at the side table, where the deck builder is used.
    ///  * "The cards on the table are a little far": besides the closer standing spot (Places.CaravanSpot,
    ///    Settings StandDistance), the dungeon map is drawn toward the player until its nearest row lies
    ///    MapRowPastEdge behind the table's near edge (never pushed away; bigger maps already reach closer).
    internal class TableComfort : MonoBehaviour
    {
        public static float BodyRadius = 0.35f, AboveHead = 0.1f;
        // (2026-10-11 evening: the middle rows out of reach - the nearest row now 0.12 m past the edge, was 0.20)
        public static float MapRowPastEdge = 0.12f, MaxMapPull = 0.30f;   // real metres
        public static bool Hide = true, Pull = true;                        // test toggles: `tablecomfort hide|pull 0|1`

        sealed class Hidden { public Renderer R; }
        readonly List<Hidden> m_hidden = new List<Hidden>();
        int m_version = -1;
        public static string HiddenNames = "";

        void Awake()
        {
            Diag.StatusExtras["tableComfort"] = () => Diag.Q("hidden " + m_hidden.Count + (HiddenNames.Length > 0 ? " (" + HiddenNames + ")" : "") + " | map pull " + (m_pull / Mathf.Max(0.01f, Settings.TableScale)).ToString("0.00") + " m");
            Diag.Commands["tablecomfort"] = a =>
            {
                if (a.Length > 2 && a[1] == "hide") { Hide = a[2] == "1"; m_version = -1; if (!Hide) Restore("test"); }
                if (a.Length > 2 && a[1] == "pull") { Pull = a[2] == "1"; m_layout = null; }
                return "hide=" + Hide + " pull=" + Pull + " hidden " + m_hidden.Count + " [" + HiddenNames + "] map pull " + m_pull.ToString("0.###") + " u";
            };
        }

        void LateUpdate()
        {
            if (!XRSettings.enabled || VRRig.I == null) return;
            bool atTable = Hide && Places.Current == Places.Place.Caravan && VRRig.I.Mode == RigMode.Station && VRRig.I.HasStation;
            if (!atTable)
            {
                if (m_hidden.Count > 0) Restore("left the card table");
                m_version = -1;
            }
            else if (VRRig.I.Version != m_version)
            {
                m_version = VRRig.I.Version;
                Restore(null);
                HideAround();
            }
            else
            {
                // the game's environment states can switch renderers back on
                foreach (Hidden h in m_hidden) if (h.R != null && h.R.enabled) h.R.enabled = false;
            }
            if (Pull) MapPull();
        }

        // ---- furniture in the player's body ------------------------------------------------------------
        void HideAround()
        {
            var pieces = new List<Transform>();
            foreach (Feedback.BodyHit hit in Feedback.BodyHits(BodyRadius, AboveHead, false))
            {
                if (Excluded(hit.R)) continue;
                Transform piece = PieceOf(hit.R.transform);
                if (!pieces.Contains(piece)) pieces.Add(piece);
            }
            var names = new StringBuilder();
            foreach (Transform piece in pieces)
            {
                foreach (Renderer r in piece.GetComponentsInChildren<Renderer>())
                {
                    if (r == null || !r.enabled || Excluded(r)) continue;
                    r.enabled = false;
                    m_hidden.Add(new Hidden { R = r });
                }
                names.Append(names.Length > 0 ? ", " : "").Append(piece.name);
            }
            HiddenNames = names.ToString();
            if (pieces.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (Hidden h in m_hidden) sb.Append("\n  ").Append(VRUI.Path(h.R.transform));
                Plugin.L.LogInfo("TableComfort: hid " + m_hidden.Count + " renderers of " + pieces.Count + " pieces in the player's body at the card table (" + HiddenNames + "):" + sb);
            }
            else Plugin.L.LogInfo("TableComfort: nothing in the player's body at the card table");
        }

        void Restore(string why)
        {
            int n = 0;
            foreach (Hidden h in m_hidden) if (h.R != null && !h.R.enabled) { h.R.enabled = true; n++; }
            if (why != null && m_hidden.Count > 0) Plugin.L.LogInfo("TableComfort: " + n + " hidden renderers shown again (" + why + ")");
            m_hidden.Clear();
            HiddenNames = "";
        }

        /// A whole piece of the room's furniture: Models/Dealer_Caravan/<group>/<piece> (a board with its trims).
        static Transform PieceOf(Transform t)
        {
            var chain = new List<Transform>();
            for (Transform x = t; x != null; x = x.parent) chain.Add(x);
            chain.Reverse();
            if (chain.Count >= 4 && chain[0].name == "Models" && chain[1].name == "Dealer_Caravan") return chain[3];
            return t;
        }

        static bool Excluded(Renderer r)
        {
            string p = VRUI.Path(r.transform);
            if (p.StartsWith("UIManager") || p.StartsWith("HoF2VR")) return true;           // cards, tiles, our own things
            if (p.Contains("dealer_caravan_cardtable")) return true;                        // the card table (top, drape)
            if (SideTable.RemoveBoards && SideTable.IsBoard(r.transform)) return true;      // the deck builder's boards: SideTable's (removed)
            if (p.Contains("Card Table and Table Objects")) return true;                    // dice, pendulum, tray
            if (p.StartsWith("Models/Dealer/") || r.GetComponentInParent<Dealer>() != null) return true;   // the Dealer
            Bounds b = r.bounds;
            float sc = VRRig.I.Scale;
            return b.size.x > 3f * sc || b.size.z > 3f * sc;                                // the room shell, floor
        }

        // ---- the dungeon map within reach -------------------------------------------------------------
        Map m_map;
        MapLayout m_layout;
        Vector3 m_pullVec;
        float m_pull;

        void MapPull()
        {
            Map map = Map.Instance;
            if (map == null) { m_map = null; return; }
            Transform mt = map.transform;
            if (map != m_map) { m_map = map; m_pullVec = Vector3.zero; m_pull = 0f; m_layout = null; }
            MapLayout layout = map.MapLayout;
            if (layout == null || layout == m_layout || layout.Slots == null || layout.Slots.Count == 0) return;
            Vector3 edge, dir;
            if (!TableEdge(out edge, out dir)) return;
            m_layout = layout;
            float nearest = float.MaxValue;
            foreach (MapLayoutSlot s in layout.Slots)
            {
                if (s == null) continue;
                Vector3 p = s.transform.position - m_pullVec;     // where it would be without the pull
                nearest = Mathf.Min(nearest, Vector3.Dot(new Vector3(p.x - edge.x, 0f, p.z - edge.z), dir));
            }
            if (nearest == float.MaxValue) return;
            float sc = Settings.TableScale;
            float pull = Mathf.Clamp(nearest - MapRowPastEdge * sc, 0f, MaxMapPull * sc);
            Vector3 vec = -dir * pull;
            mt.position += vec - m_pullVec;
            Plugin.L.LogInfo("TableComfort: map (" + layout.Slots.Count + " slots) nearest row " + (nearest / sc).ToString("0.00") + " m past the table edge -> drawn " +
                             (pull / sc).ToString("0.00") + " m toward the player" + (m_pullVec != Vector3.zero ? " (was " + (m_pull / sc).ToString("0.00") + " m)" : ""));
            m_pullVec = vec;
            m_pull = pull;
        }

        /// The card table's near edge on the line from the player's side of the room to the Dealer, and that
        /// direction (horizontal), as Places.CaravanSpot measures them.
        static bool TableEdge(out Vector3 edge, out Vector3 dir)
        {
            edge = Vector3.zero;
            dir = Vector3.forward;
            Dealer dealer = Dealer.Instance;
            CardCamera cc = CardCamera.Instance;
            GameObject tableGo = GameObject.Find("Models/Dealer_Caravan/dealer_caravan_cardtable/Table/Collider");
            Collider table = tableGo != null ? tableGo.GetComponent<Collider>() : null;
            if (dealer == null || cc == null || table == null) return false;
            Vector3 seat = cc.StartCamera != null ? cc.StartCamera.Position : (cc.MapCamera != null ? cc.MapCamera.Position : dealer.transform.position - Vector3.forward * 2.5f);
            dir = dealer.transform.position - seat;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) return false;
            dir.Normalize();
            Bounds b = table.bounds;
            var ray = new Ray(new Vector3(seat.x, b.max.y - 0.01f, seat.z), dir);
            float enter;
            edge = b.IntersectRay(ray, out enter) ? ray.GetPoint(enter) : new Vector3(seat.x, b.max.y, b.min.z);
            return true;
        }
    }
}

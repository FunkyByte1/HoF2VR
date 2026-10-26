using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The side table: the challenge map's case and the deck builder (the second playtest, 2026-10-17).
    ///  * P2-7, mark 1: "I'm too far away from the table because this thing's in the way. I need to get rid of this thing and
    ///    move up a little bit more." The two wooden boards in front of the case
    ///    (Models/Dealer_Caravan/dealer_caravan_deckbuilder/DeckBuilder_Board and DeckBuilder_Board (1)) are hidden in VR, at
    ///    the side table and at the card table beside it. The side table's spot steps up to the case (Places.HubSpot): the
    ///    eyes Settings.StandDistance behind its near edge, as at the card table.
    ///  * P2-6, marks 3-5 ("the cards just like floating randomly", "this tool UI doesn't really work"): the challenge deck
    ///    builder laid its rows on those boards, tilted 35 degrees toward the game's camera, the three decks under the rows
    ///    (they showed through them while a deck was built), and its challenge card hung at a point of the game's camera
    ///    screen (in VR: floating over the map). While the player is at the side table its rows lie flat on the case's near
    ///    half, your deck nearest, the cards to choose from beyond it; the three decks lie near while one is picked and beyond
    ///    the rows while it is built; the challenge card stands at the far left corner.
    internal class SideTable : MonoBehaviour
    {
        public static bool MoveDeck = true;   // test toggle: `sidetable deck 0|1`
        /// Real metres past the case's near edge (the player's eyes stand StandDistance before it): the cards to choose from
        /// (Build), your deck (Build), the three decks to pick from (SelectMode) and where those three wait while one is built;
        /// DeckLift over the frame's top. The challenge card stands at the far left corner (ChallengeAhead past the edge,
        /// ChallengeRight; leaning back ChallengeLean degrees).
        public static float ChoiceAhead = 0.27f, DeckRowAhead = 0.10f, SlotsAhead = 0.15f, SlotsAway = 0.60f, DeckLift = 0.006f;
        public static float ChallengeAhead = 0.66f, ChallengeRight = -0.47f, ChallengeLean = 12f, ChallengeHalf = 0.071f, Glide = 0.35f;
        /// The third playtest, mark 4 ("It would be cool if these cards were right where my left hand is, like kind of on an angle
        /// too"): while a deck is picked, the three decks hang where the player's left hand was then - DecksAhead past the case's near
        /// edge, DecksRight of the player's middle, DecksAbove the case's top (real metres; their left hand in that picture) - tilted
        /// to face the eyes the player stands with (fixed in the room, never with the head).
        public static float DecksAhead = 0.35f, DecksRight = -0.04f, DecksAbove = 0.23f;
        /// Mark 6 ("these four outlines of cards or shadows" - the user: gone entirely): with the deck builder closed, its decks and
        /// rows (their empty outlines) are put away this far below the room, out of sight and out of reach.
        public static float AwayBelow = 30f;
        static readonly AccessTools.FieldRef<CardCamera, List<ICameraProperties>> s_camStack = AccessTools.FieldRefAccess<CardCamera, List<ICameraProperties>>("m_cameraStack");
        static readonly AccessTools.FieldRef<DeckBuilder, DeckBuilderStateMachine> s_machine = AccessTools.FieldRefAccess<DeckBuilder, DeckBuilderStateMachine>("m_stateMachine");

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(ScreenToWorldAnchor), "Update"), postfix: new HarmonyMethod(typeof(SideTable), nameof(AnchorPostfix)));
        }

        /// The deck builder's challenge card (the game puts it at a point of its deck-builder camera's screen, which in VR
        /// floated over the map): with the deck builder on the case it stands at the rows' far side instead.
        static void AnchorPostfix(ScreenToWorldAnchor __instance)
        {
            if (!DeckOn) return;
            DeckBuilder cb = DeckBuilder.Instance;
            if (cb == null || cb.ChallengeContainer == null || __instance.gameObject != cb.ChallengeContainer.gameObject) return;
            __instance.transform.SetPositionAndRotation(ChallengeAt, ChallengeRot);
        }
        public static bool RemoveBoards = true;   // test toggle: `sidetable boards 0|1`
        const string BoardPath = "Models/Dealer_Caravan/dealer_caravan_deckbuilder/DeckBuilder_Board";
        /// The map case's frame: its near edge is what the player stands at.
        public const string CaseFramePath = "Models/Dealer_Caravan/dealer_caravan_map_table/MapTable_Frame";

        readonly List<Renderer> m_boards = new List<Renderer>();
        int m_scan = -1;
        static int s_hidden;

        /// One of the deck builder's two boards (or their trims).
        public static bool IsBoard(Transform t)
        {
            if (t == null || t.root.name != "Models") return false;
            return VRUI.Path(t).StartsWith(BoardPath, System.StringComparison.Ordinal);
        }

        void Awake()
        {
            Diag.StatusExtras["sideTable"] = () => Diag.Q("boards " + (RemoveBoards ? "removed (" + s_hidden + " renderers)" : "shown") + " | deck builder " + DeckLast);
            Diag.Commands["sidetable"] = a =>
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (a.Length > 2 && a[1] == "boards") { RemoveBoards = a[2] == "1"; if (!RemoveBoards) Show(); }
                if (a.Length > 2 && a[1] == "deck") MoveDeck = a[2] == "1";
                if (a.Length > 3 && a[1] == "set")
                {
                    float v = float.Parse(a[3], inv);
                    switch (a[2])
                    {
                        case "choice": ChoiceAhead = v; break;
                        case "deckrow": DeckRowAhead = v; break;
                        case "slots": SlotsAhead = v; break;
                        case "away": SlotsAway = v; break;
                        case "lift": DeckLift = v; break;
                        case "cahead": ChallengeAhead = v; break;
                        case "cright": ChallengeRight = v; break;
                        case "clean": ChallengeLean = v; break;
                        case "chalf": ChallengeHalf = v; break;
                        case "dahead": DecksAhead = v; m_resetHand = true; break;
                        case "dright": DecksRight = v; m_resetHand = true; break;
                        case "dabove": DecksAbove = v; m_resetHand = true; break;
                    }
                }
                return "boards " + (RemoveBoards ? "removed" : "shown") + ", " + s_hidden + " hidden | deck " + MoveDeck + ": choice " + ChoiceAhead + " deck row " + DeckRowAhead + " slots " + SlotsAhead + "/" + SlotsAway + " lift " + DeckLift +
                       " | challenge " + ChallengeAhead + " right " + ChallengeRight + " lean " + ChallengeLean + " | decks at the hand " + DecksAhead + " ahead " + DecksRight + " right " + DecksAbove + " above | " + DeckLast;
            };
        }

        void LateUpdate()
        {
            DeckPose();
            if (!RemoveBoards || !XRSettings.enabled) return;
            if (m_scan != SceneScan.Version)
            {
                m_scan = SceneScan.Version;
                m_boards.Clear();
                foreach (Renderer r in SceneScan.Renderers()) if (r != null && IsBoard(r.transform)) m_boards.Add(r);
                if (m_boards.Count > 0) Plugin.L.LogInfo("SideTable: the deck builder's " + m_boards.Count + " board renderers are removed");
            }
            int n = 0;
            foreach (Renderer r in m_boards)
            {
                if (r == null) continue;
                if (r.enabled) r.enabled = false;   // (the game's environment states can switch renderers back on)
                n++;
            }
            s_hidden = n;
        }

        // ---- the challenge deck builder on the case ----------------------------------------------------------------------
        // Its root stays where the game put it; two of its groups are laid flat on the case, each by one of its containers
        // (that container's face turned up, its middle put on the spot): 'Browsers' (the Build rows: the cards to choose from,
        // and your deck - moved nearer the player by itself, the companion's slot with it) and 'Modes' (the three decks to
        // pick from: near in SelectMode, beyond the rows while one is built - they lay under the rows on the game's boards).
        sealed class Posed { public Transform T; public Vector3 Pos; public Quaternion Rot; public bool Moved; }
        readonly Dictionary<Transform, Posed> m_posed = new Dictionary<Transform, Posed>();
        UIWidget m_bg;
        DeckBuilder m_dbFor;
        public static string DeckLast = "-";
        /// Where the challenge card stands while the deck builder lies on the case (world; valid while DeckOn).
        public static Vector3 ChallengeAt;
        public static Quaternion ChallengeRot = Quaternion.identity;
        public static bool DeckOn;
        public static int Lays;

        Posed Pose(Transform t)
        {
            Posed p;
            if (!m_posed.TryGetValue(t, out p)) { p = new Posed { T = t, Pos = t.localPosition, Rot = t.localRotation }; m_posed[t] = p; }
            return p;
        }

        void RestoreAll()
        {
            foreach (Posed p in m_posed.Values)
                if (p.T != null && p.Moved) { p.T.localPosition = p.Pos; p.T.localRotation = p.Rot; p.Moved = false; }
        }

        /// The group laid flat by `refT` (a container inside it that the game never moves): its face up, its middle at `target`.
        void Lay(Transform group, Transform refT, Vector3 target)
        {
            Vector3 pos;
            Quaternion rot;
            LayPose(group, refT, target, Vector3.up, out pos, out rot);
            Put(group, pos, rot);
        }

        void Put(Transform group, Vector3 pos, Quaternion rot)
        {
            if ((group.position - pos).sqrMagnitude > 1e-10f || Quaternion.Angle(group.rotation, rot) > 0.01f) group.SetPositionAndRotation(pos, rot);
            Pose(group).Moved = true;
        }

        /// Where the group goes for `refT`'s face to look along `face` (up = flat; `face` must be square to the player's sideways
        /// direction: the group turns about that axis only, so a row stays level) with its middle at `target`.
        void LayPose(Transform group, Transform refT, Vector3 target, Vector3 face, out Vector3 pos, out Quaternion rot)
        {
            Posed p = Pose(group);
            Vector3 g0 = group.parent.TransformPoint(p.Pos);
            Quaternion r0 = group.parent.rotation * p.Rot;
            // the container in the group's own frame (constant: only the group is moved), then where it was as authored
            Vector3 local = group.InverseTransformPoint(refT.position);
            Vector3 upLocal = Quaternion.Inverse(group.rotation) * refT.up;
            Vector3 ref0 = Matrix4x4.TRS(g0, r0, group.lossyScale).MultiplyPoint3x4(local);
            Quaternion q = Quaternion.FromToRotation(Vector3.up, face) * Quaternion.FromToRotation(r0 * upLocal, Vector3.up);
            pos = target - q * (ref0 - g0);
            rot = q * r0;
        }

        /// The deck builder is open: its view is in the card camera's stack (the game takes it out when it closes).
        static bool DeckUp()
        {
            CardCamera cc = CardCamera.Instance;
            if (cc == null || cc.DeckBuilderCamera == null) return false;
            try
            {
                List<ICameraProperties> st = s_camStack(cc);
                if (st != null) for (int i = 0; i < st.Count; i++) if (ReferenceEquals(st[i], cc.DeckBuilderCamera)) return true;
            }
            catch (System.Exception) { }
            return false;
        }

        float m_t = float.NaN;
        static bool m_resetHand;
        Vector3 m_handPos;
        Quaternion m_handRot;
        bool m_hasHand;

        /// A transform moved by `delta` (world) from where its (already laid) parent puts it.
        void Shift(Transform t, Vector3 delta)
        {
            Posed p = Pose(t);
            Vector3 want = t.parent.TransformPoint(p.Pos) + delta;
            if ((t.position - want).sqrMagnitude > 1e-10f) t.position = want;
            if (t.localRotation != p.Rot) t.localRotation = p.Rot;
            p.Moved = true;
        }

        void DeckPose()
        {
            DeckBuilder cb = DeckBuilder.Instance;
            if (cb != m_dbFor) { RestoreAll(); m_posed.Clear(); if (m_bg != null) m_bg.enabled = true; m_bg = null; m_dbFor = cb; m_t = float.NaN; m_hasHand = false; }
            DeckOn = false;
            VRRig rig = VRRig.I;
            bool want = cb != null && MoveDeck && RemoveBoards && XRSettings.enabled && rig != null && rig.HasStation && Places.Current == Places.Place.Hub;
            float ahead = 0f;
            Bounds frame = default(Bounds);
            Vector3 dir = Quaternion.Euler(0f, rig != null ? rig.StationYaw : 0f, 0f) * Vector3.forward;
            Transform root = cb != null ? cb.transform : null;
            Transform browsers = root != null ? root.Find("Browsers") : null, modes = root != null ? root.Find("Modes") : null;
            Transform set = browsers != null ? browsers.Find("CardSetBrowser") : null, deck = browsers != null ? browsers.Find("DeckBrowser") : null;
            Transform comp = browsers != null ? browsers.Find("CompanionContainers") : null, mid = modes != null ? modes.Find("Encounter/DeckBuilderDeck") : null;
            if (want && (browsers == null || modes == null || set == null || deck == null || mid == null)) { want = false; DeckLast = "its rows not found"; }
            if (want && !CaseEdge(rig.StationFloor, dir, out ahead, out frame)) { want = false; DeckLast = "no case found"; }
            if (!want)
            {
                if (DeckLast.StartsWith("on the case")) DeckLast = "as the game places it";
                RestoreAll();
                if (m_bg != null) { m_bg.enabled = true; m_bg = null; }
                m_t = float.NaN;
                m_hasHand = false;
                return;
            }
            float sc = rig.Scale, top = frame.max.y + DeckLift * sc;
            Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
            Vector3 edge = rig.StationFloor + dir * ahead;
            edge.y = top;
            if (!DeckUp())
            {
                // closed (the challenge map; after Back): out of sight - their empty outlines lay on the map (mark 6)
                Lay(browsers, set, edge - Vector3.up * (AwayBelow * sc));
                Lay(modes, mid, edge - Vector3.up * (AwayBelow * sc) + dir * (1f * sc));
                m_t = float.NaN;
                m_hasHand = false;
                if (!DeckLast.StartsWith("put away")) Plugin.L.LogInfo("SideTable: the deck builder is closed: its decks and rows put away under the room");
                DeckLast = "put away (closed)";
                ChallengePose(edge, dir, right, sc);   // (the challenge card still stands at its corner when the game brings it)
                DeckOn = true;
                return;
            }
            // the rows: the cards to choose from at ChoiceAhead; your deck (and the companion's slot) nearer, at DeckRowAhead
            Lay(browsers, set, edge + dir * (ChoiceAhead * sc));
            Vector3 deckNat = browsers.TransformPoint(Pose(deck).Pos);
            float deckIn = Vector3.Dot(deckNat - edge, dir);
            Vector3 delta = dir * (DeckRowAhead * sc - deckIn) + Vector3.up * (top - deckNat.y);
            Shift(deck, delta);
            if (comp != null) Shift(comp, delta);
            // the three decks: where the left hand was (mark 4), tilted to the eyes, to pick one; flat beyond the rows while one is
            // built (gliding between)
            DeckBuilderStateMachine m = s_machine(cb);
            bool building = m != null && m.ActiveState is DeckBuilderState_Build;
            if (m_resetHand) { m_hasHand = false; m_resetHand = false; }
            if (!m_hasHand)
            {
                // fixed when the deck builder opens: from the station's eyes, never the head of the moment
                Vector3 at = edge + dir * (DecksAhead * sc) + right * (DecksRight * sc) + Vector3.up * (DecksAbove * sc - DeckLift * sc);
                Vector3 f = rig.NeutralEye - at;
                f -= right * Vector3.Dot(f, right);
                if (f.sqrMagnitude < 1e-6f) f = Vector3.up;
                LayPose(modes, mid, at, f.normalized, out m_handPos, out m_handRot);
                m_hasHand = true;
            }
            Vector3 awayPos;
            Quaternion awayRot;
            LayPose(modes, mid, edge + dir * (SlotsAway * sc), Vector3.up, out awayPos, out awayRot);
            float tWant = building ? 1f : 0f;
            if (float.IsNaN(m_t) || VRFade.IsBlack) m_t = tWant;
            else m_t = Mathf.MoveTowards(m_t, tWant, Time.unscaledDeltaTime / Mathf.Max(0.05f, Glide));
            float k = Mathf.SmoothStep(0f, 1f, m_t);
            Put(modes, Vector3.Lerp(m_handPos, awayPos, k), Quaternion.Slerp(m_handRot, awayRot, k));
            ChallengePose(edge, dir, right, sc);
            if (cb.ChallengeContainer != null)
            {
                Transform chal = cb.ChallengeContainer.transform;
                Posed pc = Pose(chal);
                if ((chal.position - ChallengeAt).sqrMagnitude > 1e-10f || Quaternion.Angle(chal.rotation, ChallengeRot) > 0.01f) chal.SetPositionAndRotation(ChallengeAt, ChallengeRot);
                pc.Moved = true;
                // its dark backdrop (a 4720 x 960 texture behind the card that shades the flat screen) lay over the case like a dark
                // pane: off while the deck builder lies there
                if (m_bg == null)
                {
                    Transform bg = chal.Find("BG");
                    UIWidget w = bg != null ? bg.GetComponent<UIWidget>() : null;
                    if (w != null && w.enabled) { w.enabled = false; m_bg = w; }
                }
                else if (m_bg.enabled) m_bg.enabled = false;
            }
            if (!DeckLast.StartsWith("on the case")) { Lays++; Plugin.L.LogInfo("SideTable: the deck builder laid on the map's case (" + (building ? "building a deck" : "choosing a deck") + ")"); }
            DeckOn = true;
            DeckLast = "on the case (" + (building ? "building: the decks beyond the rows" : "the decks at the left hand") + ")";
        }

        /// The challenge card: standing at the far left corner, leaning back a little, facing the player.
        static void ChallengePose(Vector3 edge, Vector3 dir, Vector3 right, float sc)
        {
            float lean = ChallengeLean * Mathf.Deg2Rad;
            Vector3 cardTop = Vector3.up * Mathf.Cos(lean) + dir * Mathf.Sin(lean), face = -dir * Mathf.Cos(lean) + Vector3.up * Mathf.Sin(lean);
            ChallengeAt = edge + dir * (ChallengeAhead * sc) + right * (ChallengeRight * sc) + cardTop * (ChallengeHalf * sc);
            // (the card lies in its container turned half round its long axis: the container is turned so the card's face looks
            // at the player)
            ChallengeRot = Quaternion.LookRotation(cardTop, face) * Quaternion.AngleAxis(180f, Vector3.forward);
        }

        void Show()
        {
            foreach (Renderer r in m_boards) if (r != null) r.enabled = true;
            m_boards.Clear();
            m_scan = -1;
            s_hidden = 0;
        }

        static Renderer s_frame;
        static int s_frameScan = -1;

        /// The near edge of the map's case along `dir` from `floor` (world); false when the case is not found or the spot is
        /// already inside its outline.
        public static bool CaseEdge(Vector3 floor, Vector3 dir, out float ahead, out Bounds frame)
        {
            ahead = 0f;
            frame = default(Bounds);
            if (s_frameScan != SceneScan.Version || s_frame == null)
            {
                s_frameScan = SceneScan.Version;
                GameObject go = GameObject.Find(CaseFramePath);
                s_frame = go != null ? go.GetComponent<Renderer>() : null;
            }
            Renderer r = s_frame;
            if (r == null) return false;
            frame = r.bounds;
            var ray = new Ray(new Vector3(floor.x, frame.center.y, floor.z), dir);
            if (frame.Contains(ray.origin)) return false;
            float enter;
            if (!frame.IntersectRay(ray, out enter) || enter <= 0f) return false;
            ahead = enter;
            return true;
        }
    }
}

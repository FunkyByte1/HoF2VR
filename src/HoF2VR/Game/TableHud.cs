using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The player's stat cards at the Dealer's table (user 2026-10-11 evening: "I couldn't see things like my health and
    /// food in the dealer's room"; the inventory "is still bugged").
    ///  * The game shows health, food, gold and fame as a fan of cards (StatHand) plus the companion's card at the bottom
    ///    left of its screen ('3D Root/Scalar/BottomLeft', an NGUI UIAnchor). On the player's VR screen that put them
    ///    0.23 m from the eyes, 16 degrees down and 20 left: above the view while looking at the table, in the face while
    ///    looking ahead, and in front of the inventory's equipment row. With Table.StatsOnTable the anchor is put on the
    ///    table instead, to the player's left near the edge, propped up toward the eyes: a glance down-left shows them,
    ///    with the game's own art, numbers and gain/loss animations, and nothing else is covered.
    ///  * The game tints the edge of the screen red when the player starves or is poisoned (CameraVignette over the whole
    ///    view: in VR the edge of the player's vision turned red - "I really don't like that"). In VR the tint is not
    ///    drawn; the card it is about (food when starving, health when poisoned) glows red on the table instead, with
    ///    the tint's own timing.
    internal class TableHud : MonoBehaviour
    {
        public static TableHud I;
        // the cards' spot, in real metres from the player's neutral eyes (+x right, +z ahead) and the table top
        public static float Right = -0.56f, PastEdge = 0.26f, Lift = 0.06f, Tilt = 0f;
        static Transform s_anchor;
        static int s_anchorScene = -1;
        public static int Placed;
        public static string LastPose = "-";

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(CameraVignette), "SetConfig"), prefix: new HarmonyMethod(typeof(TableHud), nameof(VignettePrefix)));
            h.Patch(AccessTools.Method(typeof(CardZoom), "Zoom"), prefix: new HarmonyMethod(typeof(TableHud), nameof(ZoomPrefix)), postfix: new HarmonyMethod(typeof(TableHud), nameof(ZoomPostfix)));
            try { h.Patch(AccessTools.Method(typeof(Token), "Start"), postfix: new HarmonyMethod(typeof(TableHud), nameof(TokenStartPostfix))); }
            catch (Exception e) { Plugin.L.LogWarning("TableHud: token start patch: " + e.Message); }
            try { h.Patch(AccessTools.Method(typeof(Shard), "Init"), postfix: new HarmonyMethod(typeof(TableHud), nameof(ShardInitPostfix))); }
            catch (Exception e) { Plugin.L.LogWarning("TableHud: shard init patch: " + e.Message); }
            // the private Show(EncounterDetailType, EncounterDetailDef) that tints the wash (its struct type is private)
            foreach (System.Reflection.MethodInfo m in AccessTools.GetDeclaredMethods(typeof(EncounterDetailUI)))
                if (m.Name == "Show" && m.GetParameters().Length == 2)
                    h.Patch(m, postfix: new HarmonyMethod(typeof(TableHud), nameof(DetailShowPostfix)));
        }

        // ---- the encounter detail's themed wash (the shop's green felt) ---------------------------------------------------
        // A colour sprite and a texture tinted per encounter type, screen-space washes behind the cards in the flat game; in VR
        // they hung across the room as a flat green rectangle with the controls bar's cut-out as a black stripe through it
        // (playtester review PT_shop, 2026-10-12). Made fully transparent in VR when the game sets them; the shop's cards,
        // prices and buttons are unchanged.
        static readonly AccessTools.FieldRef<EncounterDetailUI, UISprite> s_detailSprite = AccessTools.FieldRefAccess<EncounterDetailUI, UISprite>("m_colourSprite");
        static readonly AccessTools.FieldRef<EncounterDetailUI, UITexture> s_detailTexture = AccessTools.FieldRefAccess<EncounterDetailUI, UITexture>("m_texture");
        public static int DetailWashes;

        static void DetailShowPostfix(EncounterDetailUI __instance)
        {
            if (!XRSettings.enabled) return;
            try
            {
                UISprite sp = s_detailSprite(__instance);
                UITexture tx = s_detailTexture(__instance);
                if (sp != null) { Color c = sp.color; c.a = 0f; sp.color = c; }
                if (tx != null) { Color c = tx.color; c.a = 0f; tx.color = c; }
                DetailWashes++;
                Plugin.L.LogInfo("tablehud: the encounter detail's themed wash made transparent in VR (" + DetailWashes + ")");
            }
            catch (System.Exception e) { Plugin.L.LogWarning("tablehud: detail wash: " + e.Message); }
        }

        // ---- cards shown to the player (CardZoom: earned cards, gains, equipment offers) ------------------------------------
        /// "When I earn a card, it is too close to me. I need it further back" (user 2026-10-11 evening): the game's zoom
        /// spots are 0.29-0.31 m from the eyes on the VR screen. Centre/left/right zooms go ZoomFarther times as far along
        /// the same line of sight, ZoomGrow times as big (a little smaller to the eye, still within reach of a fingertip).
        public static float ZoomFarther = 1.55f, ZoomGrow = 1.2f;
        /// (round 5, the proximity sweep: a gained card's near edge came to 0.39 m from the eyes at the 0.43 m centre) the zoom
        /// centre is at least ZoomMin from the eyes, so the whole card stays outside the keep-out rule's 0.45 m
        /// (2026-10-17, the spawn rule: nothing nearer than 0.76 m to the eyes) the zoom's centre at least ZoomMin: the zoomed card
        /// (0.12 m from its centre to its nearest corner at the zoom's size) stays outside 0.76 m
        /// (21:50, the rule revised to 0.53 m: 0.65)
        public static float ZoomMin = 0.65f;
        /// The zoom's container once the mod has placed it (TableBoards keeps the controls column clear of its cards).
        public static Transform ZoomContainer;
        static readonly System.Collections.Generic.List<Renderer> s_zoomRs = new System.Collections.Generic.List<Renderer>();

        /// The world box of the zoomed card(s) while the zoom shows one at the table; false when none.
        public static bool ZoomBounds(out Bounds b)
        {
            b = default(Bounds);
            Transform t = ZoomContainer;
            if (t == null || !t.gameObject.activeInHierarchy || !Places.AtCaravan) return false;
            bool has = false;
            s_zoomRs.Clear();
            t.GetComponentsInChildren(false, s_zoomRs);
            foreach (Renderer r in s_zoomRs)
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer || r.GetComponentInParent<ParticleSystem>() != null || r.GetComponentInParent<Card>() == null) continue;
                if (VRRig.I != null && r.bounds.size.magnitude > 0.8f * VRRig.I.Scale) continue;
                if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
            }
            return has;
        }
        public static string LastZoom = "-";

        /// The game places the zoom container only when the card is not in it yet (CardZoom.Zoom); a card zoomed again (the
        /// equip question after a choice, new actions) stays where it is - pushing it again moved it 1.55x further each time
        /// (0.43 -> 0.67 m in the round-5 sweep).
        static void ZoomPrefix(CardZoom __instance, CardZoom.ZoomData a_data, out bool __state)
        {
            __state = a_data != null && a_data.Card != null && __instance.Container != null && a_data.Card.Container != __instance.Container;
        }

        static void TokenStartPostfix(Token __instance) => TokenStarted(__instance);

        static void ZoomPostfix(CardZoom __instance, CardZoom.ZoomData a_data, bool __state)
        {
            try
            {
                if (!__state || !XRSettings.enabled || !Places.AtCaravan || VRRig.I == null || a_data == null || __instance.Container == null) return;
                if (ChoiceZoom.Holding(a_data.Card)) return;   // a card chosen from a row stays in its place (ChoiceZoom)
                CardZoom.PositionType pos = a_data.Position;
                if (pos == CardZoom.PositionType.Wheel || pos == CardZoom.PositionType.Pendulum) return;
                Transform t = __instance.Container.transform;
                ZoomContainer = t;
                Vector3 eye = VRRig.I.NeutralEye;
                Vector3 p = t.position;
                float before = Vector3.Distance(eye, p) / VRRig.I.Scale;
                float sc = VRRig.I.Scale;
                Vector3 dir = (p - eye).sqrMagnitude > 1e-8f ? (p - eye).normalized : VRRig.I.NeutralRot * Vector3.forward;
                t.position = eye + dir * Mathf.Max(Vector3.Distance(eye, p) * ZoomFarther, ZoomMin * sc);
                t.localScale = t.localScale * ZoomGrow;
                LastZoom = pos + " " + before.ToString("0.00") + " -> " + (Vector3.Distance(eye, t.position) / VRRig.I.Scale).ToString("0.00") + " m";
                Plugin.L.LogInfo("TableHud: zoomed card (" + LastZoom + ")");
            }
            catch (Exception e) { Plugin.L.LogWarning("TableHud: zoom: " + e.Message); }
        }

        void Awake()
        {
            I = this;
            Camera.onPreCull += BubblePreCull;
            Camera.onPostRender += BubblePostRender;
            Diag.StatusExtras["tableHud"] = () => Diag.Q("stats " + (Active ? "on the table" : "off") + " placed " + Placed + " " + LastPose + " glow " + s_glows + " zoom " + LastZoom + " tokens pushed " + TokensPushed + " | hidden pile " + PileLast);
            Diag.Commands["tablehud"] = a =>
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (a.Length > 4) { Right = float.Parse(a[1], inv); PastEdge = float.Parse(a[2], inv); Lift = float.Parse(a[3], inv); Tilt = float.Parse(a[4], inv); s_last = -1; }
                if (a.Length > 1 && a[1] == "glow") { Glow(a.Length > 2 && a[2] == "health" ? "Card_ST_Health" : "Card_ST_Food", 1.5f, 1f); return "glow"; }
                return "right " + Right + " past the edge " + PastEdge + " lift " + Lift + " tilt " + Tilt + " | " + LastPose;
            };
        }

        static bool Active => Settings.StatsOnTable && XRSettings.enabled && Places.Current == Places.Place.Caravan && VRRig.I != null && VRRig.I.HasStation;

        /// The stat cards' anchor ('3D Root/Scalar/BottomLeft'), found once per scene load.
        static Transform Anchor()
        {
            if (s_anchor != null) return s_anchor;
            int scene = SceneScan.Version;
            if (scene == s_anchorScene) return null;
            s_anchorScene = scene;
            GameObject go = GameObject.Find("UIManager/UI Root (3D)/3D Root/Scalar/BottomLeft");
            s_anchor = go != null ? go.transform : null;
            return s_anchor;
        }

        /// From TableView after a UIAnchor ran on the player's screen: the stat cards' anchor goes onto the table.
        public static void AfterAnchor(UIAnchor a)
        {
            if (!Active || a == null || a.transform != Anchor()) return;
            s_last = -1;
            Place();
        }

        static int s_last = -1;
        static int s_lastVersion = -1;

        void LateUpdate()
        {
            if (!Active) { if (s_wasActive) Leave(); return; }
            s_wasActive = true;
            // once per arrival (the station's version) and whenever something moved it back
            Transform t = Anchor();
            if (t == null) return;
            if (s_lastVersion != VRRig.I.Version || (t.position - s_pos).sqrMagnitude > 1e-8f) s_last = -1;
            if (s_last < 0) Place();
            else if (Time.frameCount % 15 == 0) { KeepLightsOnCards(t); KeepPile(); }   // (the game may switch the fame light on again)
            // the map can grow under the fan after it was placed (a new stage's cards): placed again when they meet
            if (Time.unscaledTime >= s_nextOverlap && s_last >= 0 && s_last != Time.frameCount)
            {
                s_nextOverlap = Time.unscaledTime + 1f;
                Bounds fan;
                if (CardBounds(t, out fan) && OverlapsMap(fan, VRRig.I.Scale)) Place();
            }
            StepGlow();
        }

        static bool s_wasActive;
        static float s_nextOverlap;
        // ---- the fan never lies on the map (T6, playtest 2026-10-17, marks 6, 7, 8, 14: the Life and Food cards over the map's
        // bottom-left corner): moved left a step at a time until its footprint on the table clears every map card and the hero
        public static float AvoidStep = 0.02f, AvoidMax = 0.40f, AvoidMargin = 0.012f;
        public static int Avoided;
        static readonly System.Collections.Generic.List<Bounds> s_mapBoxes = new System.Collections.Generic.List<Bounds>();

        static void MapBoxes()
        {
            s_mapBoxes.Clear();
            Map m = Map.Instance;
            if (m == null || m.MapLayout == null || m.MapLayout.Slots == null) return;
            foreach (MapLayoutSlot sl in m.MapLayout.Slots)
            {
                if (sl == null || sl.Cards == null) continue;
                foreach (Card c in sl.Cards)
                {
                    if (c == null || !c.isActiveAndEnabled) continue;
                    s_rs.Clear();
                    c.GetComponentsInChildren(false, s_rs);
                    // (the third playtest, mark 23: "These cards are clipping through each other" - the Life card over the bottom
                    // map card under the hero: only renderers named c_cardmodel counted, not every card template's own model)
                    foreach (Renderer r in s_rs)
                        if (r != null && r.enabled && !(r is ParticleSystemRenderer) && r.GetComponentInParent<ParticleSystem>() == null && r.bounds.size.magnitude < 0.6f * VRRig.I.Scale)
                            s_mapBoxes.Add(r.bounds);
                }
            }
            // the encounter's own card where it lies on the map (it is not in a map slot while it is played)
            Encounter enc = Encounter.Instance;
            if (enc != null && enc.Card != null && enc.Card.isActiveAndEnabled && enc.Card.transform.position.y < VRRig.I.NeutralEye.y - 0.55f * VRRig.I.Scale)
            {
                s_rs.Clear();
                enc.Card.GetComponentsInChildren(false, s_rs);
                foreach (Renderer r in s_rs)
                    if (r != null && r.enabled && !(r is ParticleSystemRenderer) && r.GetComponentInParent<ParticleSystem>() == null && r.bounds.size.magnitude < 0.6f * VRRig.I.Scale)
                        s_mapBoxes.Add(r.bounds);
            }
            // the hourglass beside the fan's spot (the fan stood against it)
            Defiant.Environment.HourglassController hg = Defiant.Environment.HourglassController.Instance;
            if (hg != null)
            {
                s_rs.Clear();
                hg.GetComponentsInChildren(false, s_rs);
                foreach (Renderer r in s_rs) if (r != null && r.enabled && !(r is ParticleSystemRenderer) && r.bounds.size.magnitude < 1f) s_mapBoxes.Add(r.bounds);
            }
            if (m.PlayerCounter != null && m.PlayerCounter.isActiveAndEnabled)
            {
                s_rs.Clear();
                m.PlayerCounter.GetComponentsInChildren(false, s_rs);
                foreach (Renderer r in s_rs) if (r != null && r.enabled && !(r is ParticleSystemRenderer)) s_mapBoxes.Add(r.bounds);
            }
        }

        static bool OverlapsMap(Bounds fan, float sc)
        {
            MapBoxes();
            float mg = AvoidMargin * sc;
            foreach (Bounds b in s_mapBoxes)
                if (fan.min.x < b.max.x + mg && fan.max.x > b.min.x - mg && fan.min.z < b.max.z + mg && fan.max.z > b.min.z - mg) return true;
            return false;
        }

        static int AvoidMap(Transform t, Vector3 right, float sc)
        {
            Bounds fan;
            if (!CardBounds(t, out fan)) return 0;
            int steps = 0, max = Mathf.RoundToInt(AvoidMax / AvoidStep);
            Vector3 step = -right * (AvoidStep * sc);
            while (steps < max && OverlapsMap(fan, sc)) { t.position += step; fan.center += step; steps++; }
            if (steps > 0) { Avoided++; Plugin.L.LogInfo("TableHud: the stat cards moved " + (steps * AvoidStep).ToString("0.00") + " m left, off the map"); }
            return steps;
        }
        static Vector3 s_pos;

        static void Leave()
        {
            s_wasActive = false;
            s_last = -1;
            RestoreLights();
            RestorePile();
            // the game's own layout again (its anchor runs on the next screen refresh)
            if (s_anchor != null && TableView.On) TableView.Refresh("stats back on the screen");
        }

        static void Place()
        {
            Transform t = Anchor();
            if (t == null) return;
            VRRig rig = VRRig.I;
            float sc = rig.Scale;
            Vector3 eye = rig.NeutralEye;
            Quaternion yaw = rig.NeutralRot;
            float top = TableTop(eye.y - 0.8f * sc);
            Vector3 fwd = yaw * Vector3.forward, right = yaw * Vector3.right;
            Vector3 p = new Vector3(eye.x, top + Lift * sc, eye.z) + fwd * ((Settings.StandDistance + PastEdge) * sc) + right * (Right * sc);
            // the cards face the eyes (the anchor's frame is a screen's: forward away from the viewer, up = up)
            Vector3 look = p - eye;
            Quaternion rot = Quaternion.LookRotation(look, Vector3.up) * Quaternion.Euler(Tilt, 0f, 0f);
            t.SetPositionAndRotation(p, rot);
            // the cards hang off the anchor (the screen's corner) toward the screen's middle: centre the fan itself on the spot
            Bounds b;
            if (CardBounds(t, out b)) t.position += p - b.center;
            int moved = AvoidMap(t, right, sc);
            if (moved > 0) p += -right * (moved * AvoidStep * sc);
            // (the fan's middle as its cards show it; with them hidden, where they last were since this arrival - the pile holding
            // them stays put - else the spot itself)
            Bounds fan;
            if (CardBounds(t, out fan)) { s_fanMid = fan.center; s_fanMidVersion = rig.Version; }
            PlacePile(s_fanMidVersion == rig.Version ? s_fanMid : p, right, top, sc);
            s_pos = t.position;
            s_last = Time.frameCount;
            s_lastVersion = rig.Version;
            Placed++;
            KeepLightsOnCards(t);
            Vector3 rel = Quaternion.Inverse(yaw) * (p - eye) / sc;
            LastPose = "at " + rel.x.ToString("0.00") + " right, " + rel.z.ToString("0.00") + " ahead, " + (-rel.y).ToString("0.00") + " below the eyes (" +
                       look.magnitude.ToString("0.00") + " u)";
            if (Placed <= 3 || Placed % 50 == 0) Plugin.L.LogInfo("TableHud: the stat cards lie on the table " + LastPose);
        }

        // ---- the stat cards' hidden pile (the seated b1 at the call to battle, 2026-10-19) ---------------------------------------
        /// The game hides the stat cards (the call to battle, the dice, the pendulum, the deck builder, the map view, a challenge's
        /// end) by dealing them into StatHand's hidden container, 'Scale/Player Decks Left/OffscreenStatHand': below the bottom left
        /// of its flat screen. In VR that spot lies on the table at the player's near left, on the map's near-left card: at every
        /// call to battle the stat cards slid from the fan toward the player and lay 0.50 m from seated eyes, 0.23 m from the body's
        /// axis, over the map. With the stats on the table the hidden pile lies flat where the fan stands (moved left off the map,
        /// the hourglass and the hero as the fan is): the cards gather there and fan out again from there, never toward the player.
        static Transform s_pile;
        static Vector3 s_pileLocal, s_pileWant, s_fanMid, s_pilePos;
        static int s_fanMidVersion = -1;
        static bool s_pileMoved;
        public static string PileLast = "-";

        static CardContainer PileContainer()
        {
            DeckManager dm = DeckManager.Instance;
            StatHand sh = dm != null ? dm.StatHand : null;
            return sh != null ? sh.HiddenContainer : null;
        }

        static readonly System.Collections.Generic.List<Card> s_pileCards = new System.Collections.Generic.List<Card>();

        /// The pile's middle (world): where the game's layout puts the stat cards in the container (CardContainer.GetCardPosition,
        /// in the container's frame - its layout's own spot is 0.2 m from where the cards lie); its layout's spot without cards.
        static Vector3 PileMiddle(CardContainer hc)
        {
            Transform h = hc.transform;
            s_pileCards.Clear();
            DeckManager dm = DeckManager.Instance;
            if (dm != null && dm.StatHand != null && dm.StatHand.Container != null && dm.StatHand.Container.Cards != null)
                foreach (Card c in dm.StatHand.Container.Cards) if (c != null) s_pileCards.Add(c);
            if (hc.Cards != null) foreach (Card c in hc.Cards) if (c != null && !s_pileCards.Contains(c)) s_pileCards.Add(c);
            if (hc.Layout != null && s_pileCards.Count > 0)
            {
                try
                {
                    Vector3 sum = Vector3.zero;
                    for (int i = 0; i < s_pileCards.Count; i++) sum += h.TransformPoint(hc.GetCardPosition(s_pileCards[i], s_pileCards, i));
                    return sum / s_pileCards.Count;
                }
                catch (Exception) { }
            }
            return hc.Layout != null ? h.TransformPoint(hc.Layout.transform.localPosition) : h.position;
        }

        /// Half the side of a square that holds a stat card lying flat, turned any way (world units): from a stat card's collider.
        static float PileHalf(float sc)
        {
            DeckManager dm = DeckManager.Instance;
            if (dm != null && dm.StatHand != null)
                foreach (CardContainer box in new[] { dm.StatHand.Container, dm.StatHand.HiddenContainer })
                    if (box != null && box.Cards != null)
                        foreach (Card c in box.Cards)
                        {
                            BoxCollider bc = c != null ? c.GetComponentInChildren<BoxCollider>(true) : null;
                            if (bc == null) continue;
                            Vector3 s = Vector3.Scale(bc.size, bc.transform.lossyScale);
                            float l = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                            if (l > 0.01f * sc && l < 0.5f * sc) return l * 0.5f;
                        }
            return 0.08f * sc;   // (a stat card at the caravan: 0.108 x 0.156 world units)
        }

        static void PlacePile(Vector3 fanMid, Vector3 right, float top, float sc)
        {
            CardContainer hc = PileContainer();
            if (hc == null) return;
            Transform h = hc.transform;
            if (h != s_pile) { RestorePile(); s_pile = h; s_pileLocal = h.localPosition; }
            else if (s_pileMoved) h.localPosition = s_pileLocal;   // (every placing starts from the game's own)
            Vector3 mid = PileMiddle(hc);
            // lying on the table top under the fan's middle, then left a step at a time until clear of the map, the hourglass, the hero
            Vector3 want = new Vector3(fanMid.x, top + 0.002f * sc, fanMid.z);
            float half = PileHalf(sc);
            var box = new Bounds(want, new Vector3(half * 2f, 0.01f * sc, half * 2f));
            int steps = 0, max = Mathf.RoundToInt(AvoidMax / AvoidStep);
            Vector3 step = -right * (AvoidStep * sc);
            DeckBoxes(h, sc);
            while (steps < max && (OverlapsMap(box, sc) || OverlapsDecks(box, sc))) { want += step; box.center += step; steps++; }
            h.position += want - mid;
            s_pileWant = want;
            s_pilePos = h.position;
            s_pileMoved = true;
            VRRig rig = VRRig.I;
            Vector3 rel = Quaternion.Inverse(rig.NeutralRot) * (want - rig.NeutralEye) / sc;
            string pose = "at " + rel.x.ToString("0.00") + " right, " + rel.z.ToString("0.00") + " ahead, " + (-rel.y).ToString("0.00") + " below the eyes, " +
                          ((want - rig.NeutralEye).magnitude / sc).ToString("0.00") + " m" + (steps > 0 ? " (" + (steps * AvoidStep).ToString("0.00") + " m left, off the map)" : "") +
                          " - the game's spot " + ((mid - rig.NeutralEye).magnitude / sc).ToString("0.00") + " m";
            if (pose != PileLast) { PileLast = pose; Plugin.L.LogInfo("TableHud: the stat cards' hidden pile lies where the fan stands, " + pose); }
        }

        /// The player's decks lying beside it on the table ('Player Decks Left': the inventory, reserve and equipment decks and their
        /// face-up cards; the pile's own container left out): the pile lies clear of them too (one lay against the reserve's face-up
        /// card in the first try).
        static readonly System.Collections.Generic.List<Bounds> s_deckBoxes = new System.Collections.Generic.List<Bounds>();
        static void DeckBoxes(Transform pile, float sc)
        {
            s_deckBoxes.Clear();
            Transform group = pile.parent;
            if (group == null) return;
            s_rs.Clear();
            group.GetComponentsInChildren(false, s_rs);
            foreach (Renderer r in s_rs)
                if (r != null && r.enabled && !(r is ParticleSystemRenderer) && !r.transform.IsChildOf(pile) && r.GetComponentInParent<ParticleSystem>() == null &&
                    r.bounds.size.magnitude < 0.6f * sc)
                    s_deckBoxes.Add(r.bounds);
        }

        static bool OverlapsDecks(Bounds b, float sc)
        {
            float mg = AvoidMargin * sc;
            foreach (Bounds d in s_deckBoxes)
                if (b.min.x < d.max.x + mg && b.max.x > d.min.x - mg && b.min.z < d.max.z + mg && b.max.z > d.min.z - mg) return true;
            return false;
        }

        /// The pile where the game put it, when the stats leave the table (or the game's container changed).
        static void RestorePile()
        {
            if (s_pile != null && s_pileMoved) s_pile.localPosition = s_pileLocal;
            s_pileMoved = false;
        }

        /// The pile's parent moved (the game re-anchored 'Player Decks Left'): put back on its spot.
        static void KeepPile()
        {
            if (!s_pileMoved || s_pile == null) return;
            CardContainer hc = PileContainer();
            if (hc == null || hc.transform != s_pile) { s_last = -1; return; }
            // (its own world position as put: the middle's layout may hold random offsets)
            Vector3 d = s_pilePos - s_pile.position;
            if (d.sqrMagnitude > 1e-6f) s_pile.position = s_pilePos;
        }

        // ---- the stat cards' own lights (T10, playtest 2026-10-17 mark 5: "the light by the hourglass is visibly misaligned") --
        /// The game lights its stat fan with spot lights under the same anchor ('StatHand/Spotlight - CompanionContainer', 4.7,
        /// every layer but 0; 'FameNotification/Light'), aimed for the fan's place on the flat screen. With the anchor on the table
        /// they shone past the cards onto the table: a bright disc 0.25 m from the hourglass, there even while the fan was away
        /// (`lights`). Kept to the cards' layer, the hourglass's top (on that layer too) still flared white. On the table they are
        /// off (the table's own lights and CardLook's card light light the fan); back on when the fan goes back to the screen.
        static readonly System.Collections.Generic.List<Light> s_lightsOff = new System.Collections.Generic.List<Light>();
        public static int LightsKept;

        static void KeepLightsOnCards(Transform anchor)
        {
            foreach (Light l in anchor.GetComponentsInChildren<Light>(true))
            {
                if (l == null || !l.enabled) continue;
                l.enabled = false;
                if (!s_lightsOff.Contains(l)) { s_lightsOff.Add(l); LightsKept++; Plugin.L.LogInfo("TableHud: the stat cards' light '" + l.name + "' is off while they lie on the table (it lit the table and the hourglass)"); }
            }
        }

        static void RestoreLights()
        {
            foreach (Light l in s_lightsOff) if (l != null) l.enabled = true;
            s_lightsOff.Clear();
        }

        static readonly System.Collections.Generic.List<Renderer> s_rs = new System.Collections.Generic.List<Renderer>();

        /// The stat and companion cards' bounds (only the card models: the fan's footprint).
        static bool CardBounds(Transform t, out Bounds b)
        {
            b = default(Bounds);
            bool has = false;
            s_rs.Clear();
            t.GetComponentsInChildren(false, s_rs);
            foreach (Renderer r in s_rs)
            {
                if (r == null || !r.enabled || r.name != "c_cardmodel") continue;
                if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
            }
            return has;
        }

        /// The card table's top (world y), from its collider; `fallback` when it is not there.
        /// (The game switches the table's collider object off during the call to battle - Level_In, a second or two after it
        /// begins - and GameObject.Find finds only active objects: the watchdog and KeepOut then had no table, so everything lying
        /// on it counted as in the air. The seated b1's near-row map cards (0.51-0.52 m from the eyes) and the hidden stat cards
        /// (0.50 m) were measured by the eye rule the first time the head looked down, 2026-10-19. The table itself stays where it
        /// is: while the collider object lives, its top as last measured.)
        public static float TableTop(float fallback)
        {
            GameObject go = GameObject.Find("Models/Dealer_Caravan/dealer_caravan_cardtable/Table/Collider");
            Collider c = go != null ? go.GetComponent<Collider>() : null;
            // (a switched-off collider's bounds are empty: not measured then)
            if (c != null && c.enabled) { s_tableCol = c; s_tableTop = c.bounds.max.y; s_tableKept = false; return s_tableTop; }
            if (s_tableCol == null) return fallback;   // (a destroyed collider - the room unloaded - is null here)
            if (!s_tableKept)
            {
                s_tableKept = true;
                TableTopKept++;
                Plugin.L.LogInfo("TableHud: the table's collider is switched off (" + GameWatch.State.Replace("GameState_", "") + "): its top kept as last measured (y " + s_tableTop.ToString("0.000") + ")");
            }
            return s_tableTop;
        }
        static Collider s_tableCol;
        static float s_tableTop = float.MinValue;
        static bool s_tableKept;
        public static int TableTopKept;

        // ---- tokens never fly through the player ---------------------------------------------------------------------------
        /// "When I earn a coin, it spawns next to me and goes through me" (user 2026-10-11 evening): the game's token flights
        /// (challenge coins, encounter/equipment tokens, the end-of-run claim) are animation clips authored around the flat
        /// camera, where the player's head is in VR. While the table camera draws, every token closer to the head than
        /// TokenBubble is shown pushed out to that distance along the line from the head (its own animation is untouched:
        /// the pose is put back after drawing), so a coin passes beside the face instead of through it.
        /// (round 5: the bubble holds a token's pivot; its model reaches ~3 cm nearer - 0.49 keeps the drawn token outside 0.45)
        /// (the pivot: the coin's model reaches up to 5 cm nearer - the end coin's edge came to 0.20 m from the eyes' vertical
        /// with the pivot held at 0.22, and to 0.44 m from the eyes with it at 0.49: now 0.23 m (05:16) and, at 0.51, 0.46 m)
        /// (2026-10-17: the spawn rule's 0.76 m plus the model's 5 cm reach; 21:50, the rule revised: 0.53 m plus the reach)
        public static float TokenBubble = 0.58f, TokenBody = 0.26f;   // Proximity's rule plus the model's reach
        public static int TokensPushed;
        static Token[] s_tokens = new Token[0];
        static Shard[] s_shards = new Shard[0];
        static readonly System.Collections.Generic.List<MonoBehaviour> s_bubbled = new System.Collections.Generic.List<MonoBehaviour>();
        /// The tokens (challenge coins, reward tokens) TableHud keeps out of the body (DealerGaze watches them move).
        public static Token[] Tokens => s_tokens;
        static float s_nextTokenScan;
        // (round 5: "When I opened the coins at the end, they were inside me" - the scan every 0.5 s missed a coin's first
        // frames: a token is now registered at its Start, before it is first drawn; the bubble is the keep-out rule's 0.45 m
        // round the eyes plus the body's cylinder, 0.30 m round the head down to the floor)
        static readonly System.Collections.Generic.List<Token> s_new = new System.Collections.Generic.List<Token>();
        public static void TokenStarted(Token t) { if (t != null) { s_new.Add(t); DealerGaze.TokenAppeared(t); } }
        static void ShardInitPostfix(Shard __instance) { if (__instance != null) s_newShards.Add(__instance); }
        static readonly System.Collections.Generic.List<Shard> s_newShards = new System.Collections.Generic.List<Shard>();
        /// The third playtest (marks 3, 13: judder at the map and the table): the whole-world searches for tokens and shards every
        /// 0.5 s cost 5-8 ms each, inside the 3D UI camera's cull - a frame missed twice a second at every table place. Now the
        /// world is searched once per scene load; tokens join at their Start and shards at their Init. `scans legacy 1` = the old way
        /// (measurements).
        public static bool LegacyScans;
        static int s_tokenScene = -1;
        static readonly System.Collections.Generic.List<Token> s_tokenList = new System.Collections.Generic.List<Token>();
        static readonly System.Collections.Generic.List<Shard> s_shardList = new System.Collections.Generic.List<Shard>();

        /// The live tokens and shards (registered, the destroyed dropped); true when the set changed.
        static bool Gather()
        {
            bool changed = false;
            if (s_tokenScene != SceneScan.Version)
            {
                s_tokenScene = SceneScan.Version;
                s_tokenList.Clear();
                s_shardList.Clear();
                // (inactive ones too: switched on again later, they get no second Start / Init)
                foreach (Token t in Resources.FindObjectsOfTypeAll<Token>()) if (t != null && t.gameObject.scene.IsValid()) s_tokenList.Add(t);
                foreach (Shard sh in Resources.FindObjectsOfTypeAll<Shard>()) if (sh != null && sh.gameObject.scene.IsValid()) s_shardList.Add(sh);
                changed = true;
            }
            foreach (Token t in s_new) if (t != null && !s_tokenList.Contains(t)) { s_tokenList.Add(t); changed = true; }
            foreach (Shard sh in s_newShards) if (sh != null && !s_shardList.Contains(sh)) { s_shardList.Add(sh); changed = true; }
            s_newShards.Clear();
            if (s_tokenList.RemoveAll(t => t == null) > 0) changed = true;
            if (s_shardList.RemoveAll(t => t == null) > 0) changed = true;
            return changed;
        }
        static readonly System.Collections.Generic.List<KeyValuePairTV> s_pushed = new System.Collections.Generic.List<KeyValuePairTV>();
        struct KeyValuePairTV { public Transform T; public Vector3 Local; }
        static int s_bubbleFrame = -1;

        static void BubblePreCull(Camera cam)
        {
            // not while the waiting space is shown: the head is in the space then, and a token the game presents in front of
            // the flat camera (an unclaimed reward) was pushed into the campfire scene in front of the player (playtester
            // review PT_space_ahead after a shop, 2026-10-12)
            if (s_bubbleFrame == Time.frameCount || !XRSettings.enabled || !Places.AtCaravan || VRRig.I == null || Limbo.On || VRFade.IsBlack) return;
            UICamera ui = UI3D.Instance != null ? UI3D.Instance.UICamera : null;
            if (ui == null || cam != ui.cachedCamera) return;
            s_bubbleFrame = Time.frameCount;
            if (LegacyScans ? Time.unscaledTime >= s_nextTokenScan || s_new.Count > 0 : Gather())
            {
                s_nextTokenScan = Time.unscaledTime + 0.5f;
                // (the challenge's shards fly like tokens but are not Tokens - the 0.76 m sweep, 2026-10-17: a Token_Shard 0.55 m
                // from the eyes on its first frame)
                if (LegacyScans) { s_tokens = FindObjectsOfType<Token>(); s_shards = FindObjectsOfType<Shard>(); }
                else { s_tokens = s_tokenList.ToArray(); s_shards = s_shardList.ToArray(); }
                s_bubbled.Clear();
                foreach (Token t in s_tokens) if (t != null) s_bubbled.Add(t);
                foreach (Shard sh in s_shards) if (sh != null) s_bubbled.Add(sh);
            }
            s_new.Clear();
            // (the spawn rule, revised 2026-10-17 21:50: a token is kept from the eyes of the moment it appeared or flew up -
            // SpawnRule - never from the head of each frame, so it never moves with the head)
            Vector3 live = VRRig.I.HeadWorldPos;
            float sc = VRRig.I.Scale, r = TokenBubble * sc, body = TokenBody * sc;
            foreach (MonoBehaviour t in s_bubbled)
            {
                if (t == null || !t.isActiveAndEnabled) continue;
                Transform tr = t.transform;
                Vector3 p = tr.position;
                if ((p - live).sqrMagnitude > (r + sc) * (r + sc)) continue;   // far off
                SpawnRule.Hold h = SpawnRule.Track(t.GetInstanceID(), p);
                if (!h.Has)
                {
                    if (!(h.Fresh || h.Fast)) continue;
                    Vector3 dl = p - live;
                    bool inBodyNow = p.y < live.y && new Vector3(dl.x, 0f, dl.z).magnitude < body;
                    if (dl.magnitude >= r && !inBodyNow) continue;
                    SpawnRule.Capture(h, live, t.name);
                }
                Vector3 head = h.Eye;
                Vector3 d = p - head;
                float m = d.magnitude;
                // the body: a cylinder round the head's vertical, from the floor up to the eyes
                Vector3 flat = new Vector3(d.x, 0f, d.z);
                bool inBody = p.y < head.y && flat.magnitude < body;
                SpawnRule.Settle(h, m >= r && !inBody);
                if (m >= r && !inBody) continue;
                s_pushed.Add(new KeyValuePairTV { T = tr, Local = tr.localPosition });
                if (inBody)
                {
                    Vector3 dir = flat.sqrMagnitude > 1e-6f ? flat.normalized : VRRig.I.NeutralRot * Vector3.forward;
                    p = new Vector3(head.x, p.y, head.z) + dir * body;
                    d = p - head;
                    m = d.magnitude;
                }
                if (m < r) p = head + (m > 1e-4f ? d / m : VRRig.I.NeutralRot * Vector3.forward) * r;
                tr.position = p;
                TokensPushed++;
            }
        }

        static void BubblePostRender(Camera cam)
        {
            if (s_pushed.Count == 0) return;
            if (cam.stereoEnabled && cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left) return;
            UICamera ui = UI3D.Instance != null ? UI3D.Instance.UICamera : null;
            if (ui == null || cam != ui.cachedCamera) return;
            foreach (var p in s_pushed) if (p.T != null) p.T.localPosition = p.Local;
            s_pushed.Clear();
        }

        // ---- the red screen edge -> a red glow on the card it is about -------------------------------------------------
        static readonly AccessTools.FieldRef<Player, CameraVignetteConfig> s_starveCfg = Field("m_starveVignetteConfig");
        static readonly AccessTools.FieldRef<Player, CameraVignetteConfig> s_poisonCfg = Field("m_poisonVignetteConfig");
        static int s_glows;

        static AccessTools.FieldRef<Player, CameraVignetteConfig> Field(string n)
        {
            try { return AccessTools.FieldRefAccess<Player, CameraVignetteConfig>(n); }
            catch (Exception e) { Plugin.L.LogWarning("TableHud: " + n + ": " + e.Message); return null; }
        }

        /// CameraVignette.SetConfig at the Dealer's table: in VR the tint over the edge of the view is not drawn; the card it
        /// is about glows red instead. (Fights keep the game's hit tint for now: the combat overhaul decides.)
        static bool VignettePrefix(CameraVignetteConfig a_config)
        {
            if (!XRSettings.enabled || a_config == null || !Places.AtCaravan) return true;   // fights: the overhaul's call
            try
            {
                Player p = Player.Instance;
                string card = null;
                if (p != null && s_starveCfg != null && s_starveCfg(p) == a_config) card = "Card_ST_Food";
                else if (p != null && s_poisonCfg != null && s_poisonCfg(p) == a_config) card = "Card_ST_Health";
                if (card != null && Places.AtCaravan) Glow(card, Mathf.Max(0.6f, a_config.Duration), Mathf.Clamp01(a_config.Intensity));
                Plugin.L.LogInfo("TableHud: the game's red screen edge (" + (card ?? "other") + ", " + a_config.Duration.ToString("0.0") + " s) " +
                                 (card != null && Places.AtCaravan ? "shown as a red glow on the card instead" : "not drawn in VR"));
            }
            catch (Exception e) { Plugin.L.LogWarning("TableHud: vignette: " + e.Message); }
            return false;
        }

        static Light s_light;
        static Transform s_glowCard;
        static float s_glowStart, s_glowDur, s_glowMax;

        static void Glow(string cardName, float duration, float intensity)
        {
            Transform t = Anchor();
            Transform card = t != null ? Find(t, cardName) : null;
            if (card == null) { Plugin.L.LogInfo("TableHud: no " + cardName + " to light up"); return; }
            if (s_light == null)
            {
                var go = new GameObject("HoF2VR_CardGlow");
                DontDestroyOnLoad(go);
                s_light = go.AddComponent<Light>();
                s_light.type = LightType.Point;
                s_light.color = new Color(1f, 0.12f, 0.06f);
                s_light.shadows = LightShadows.None;
                s_light.renderMode = LightRenderMode.ForcePixel;
            }
            s_glowCard = card;
            s_glowStart = Time.unscaledTime;
            s_glowDur = duration;
            s_glowMax = Mathf.Max(0.5f, intensity);
            s_glows++;
        }

        static Transform Find(Transform root, string name)
        {
            foreach (Transform c in root.GetComponentsInChildren<Transform>(true)) if (c.name == name) return c;
            return null;
        }

        static void StepGlow()
        {
            if (s_light == null) return;
            float age = Time.unscaledTime - s_glowStart;
            bool on = s_glowCard != null && age < s_glowDur + 0.6f;
            if (s_light.enabled != on) s_light.enabled = on;
            if (!on) return;
            float sc = VRRig.I != null ? VRRig.I.Scale : 1f;
            // rises quickly, pulses twice, fades out (the tint's own length)
            float k = Mathf.Clamp01(age / 0.15f) * (age < s_glowDur ? 0.75f + 0.25f * Mathf.Cos(age * Mathf.PI * 2f / Mathf.Max(0.3f, s_glowDur * 0.5f)) : Mathf.Clamp01(1f - (age - s_glowDur) / 0.6f));
            Transform c = s_glowCard;
            Vector3 toEye = VRRig.I != null ? (VRRig.I.NeutralEye - c.position).normalized : -c.forward;
            s_light.transform.position = c.position + toEye * (0.06f * sc);
            s_light.range = 0.22f * sc;
            s_light.intensity = 6f * s_glowMax * k;
        }
    }
}

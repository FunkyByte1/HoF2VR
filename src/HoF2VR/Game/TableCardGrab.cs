using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Hand feel Phase 2, the rest (docs/hand-feel-design.md §3.7, docs/interaction-design.md "Pick up any table card"):
    /// on the map at the Dealer's table, an open hand over a card lying in the map gets ready (a tick, the fingers curl a
    /// little); a fresh squeeze of the grip picks the card up - it stays where it was in the hand (the grab offset kept, so
    /// nothing jumps) and the glove takes a pinch; it follows the hand (also at the render-time pose); opening the hand
    /// (grip under 0.35) lets go and the card's own container slides it home (the game's RepositionCard). Purely to look at:
    /// the game is not told anything. Never while the card or the map is busy (a card in motion, a menu over the map,
    /// another state); if the game takes the card meanwhile (another container, hidden) it is simply let go.
    /// Setting [Hands] GrabThrow (with the dice and the inventory's card inspect).
    /// Feature A, the rest (2026-10-13, the user's "flip face-down cards yourself"): a face-down card the game lets the player
    /// turn over now - a chance card once the shuffle is done, a reward card dealt face-down after a token - is picked up the
    /// same way; turned over in the hand (its face toward the eyes) it is let go and the game's own choice runs on it, exactly
    /// as a touch would: the game turns it (and, in the chance game, the others) face up. Let go unturned, it goes back and
    /// nothing is chosen. Face-down cards the game keeps hidden (the dungeon map's) still show a blank face (item 7).
    internal class TableCardGrab : MonoBehaviour
    {
        public static TableCardGrab I;
        public static float PalmReach = 0.06f, TipReach = 0.03f, Open = 0.3f, ReleaseGrip = 0.35f, StillFor = 0.25f, Attach = 0.1f;
        /// how far the held card's face must turn toward the eyes to count as turned over (cos of the angle: 0.25 = 75 deg)
        public static float FlipDot = 0.25f;
        public static int Readies, Grabs, Releases, Aborts, Flips, FlipRefusals;
        static bool s_reveal;              // the held card is one the game lets the player turn over
        static Vector3 s_faceLocal;        // its face's normal in the card's own frame (away from the eyes when picked up)
        public static float HeldFaceDot = -1f;
        public static string Last = "-";
        static Hand s_holder;
        static Card s_card;
        public static Card HeldCard => s_card;
        static CardContainer s_from;
        static Vector3 s_localPos, s_homePos;
        static Quaternion s_localRot;
        static float s_since;
        static readonly bool[] s_hover = new bool[2];
        static readonly float[] s_ready = new float[2];
        static readonly Card[] s_cand = new Card[2];
        static readonly float[] s_nearAt = { -10f, -10f };
        static readonly Collider[] s_hits = new Collider[24];
        // a card counts as lying still once its position held for StillFor
        static readonly Dictionary<int, KeyValuePair<Vector3, float>> s_still = new Dictionary<int, KeyValuePair<Vector3, float>>();

        public static Hand Holder => s_holder;
        public static bool Holds(Hand h) => s_holder != null && s_holder == h;
        public static float Ready(Hand h) => s_ready[h.IsLeft ? 1 : 0];
        /// The grip of this hand belongs to a card (ready under it, or held): the game gets no page from it.
        public static bool OwnsGrip(Hand h) => Holds(h) || Time.unscaledTime - s_nearAt[h.IsLeft ? 1 : 0] < 0.3f;

        void Awake()
        {
            I = this;
            Diag.StatusExtras["tableCard"] = () => Diag.Q(Describe());
            Diag.Commands["tcard"] = Command;
            HandWorld.Targets["mapcard"] = () => { Card c = NearestMapCard(); return c != null ? (Vector3?)Top(c) : null; };
        }

        static float Scale => VRRig.I != null ? VRRig.I.Scale : 1f;

        /// Where cards may be picked up: the dungeon map at the card table, nothing over it.
        internal static bool MapIdle()
        {
            if (!XRSettings.enabled || !Settings.GrabThrow || VRRig.I == null || !Places.AtCaravan || CombatVR.Active || VRFade.IsBlack) return false;
            if (GameWatch.State != "GameState_Map" || DiceHand.Holder != null || CardGrab.Holding || Minigames.DiceCatapult) return false;
            try
            {
                MenuManager mm = MenuManager.Instance;
                if (mm != null && (mm.Paused || mm.CurrentMenu(MenuManager.StackPriority.Secondary) != null || mm.CurrentMenu(MenuManager.StackPriority.Dialogue) != null ||
                                   mm.CurrentMenu(MenuManager.StackPriority.Failure) != null)) return false;
            }
            catch (Exception) { return false; }
            return true;
        }

        static bool InMap(Card c)
        {
            for (Transform x = c.transform; x != null; x = x.parent) if (x.name == "MapLayout") return true;
            return false;
        }

        static bool UnderHero(Card c)
        {
            try
            {
                Map m = Map.Instance;
                PlayerCounter pc = m != null ? m.PlayerCounter : null;
                Collider col = c.GetComponentInChildren<Collider>();
                if (pc == null || col == null) return false;
                Bounds b = col.bounds;
                Vector3 p = pc.transform.position;
                return p.x >= b.min.x && p.x <= b.max.x && p.z >= b.min.z && p.z <= b.max.z;
            }
            catch (Exception) { return false; }
        }

        /// A map card that lies still and visible, not moved by the game.
        static bool Pickable(Card c)
        {
            if (c == null || !c.isActiveAndEnabled || c.Container == null || !InMap(c)) return false;
            MotionController mc = c.MotionController;
            if (mc != null && mc.HasTarget) return false;
            if (UnderHero(c)) return false;   // the hero figurine stands on it: it stays
            int id = c.GetInstanceID();
            Vector3 p = c.transform.position;
            KeyValuePair<Vector3, float> was;
            if (!s_still.TryGetValue(id, out was) || (was.Key - p).sqrMagnitude > 1e-6f) { if (s_still.Count > 256) s_still.Clear(); s_still[id] = new KeyValuePair<Vector3, float>(p, Time.unscaledTime); return false; }
            return Time.unscaledTime - was.Value >= StillFor;
        }

        /// Where a face-down card may be turned over by hand: at the card table, nothing paused, no other hold.
        internal static bool RevealIdle()
        {
            if (!XRSettings.enabled || !Settings.GrabThrow || VRRig.I == null || !Places.AtCaravan || CombatVR.Active || VRFade.IsBlack) return false;
            if (DiceHand.Holder != null || CardGrab.Holding || Minigames.DiceCatapult) return false;
            try
            {
                MenuManager mm = MenuManager.Instance;
                if (mm != null && mm.Paused) return false;
            }
            catch (Exception) { return false; }
            return true;
        }

        /// A face-down card the game lets the player turn over now: a chance card once the shuffle is done (its choice waits)
        /// or a reward card dealt face-down (CardSetModifierContainer) - selectable, lying still. Never a card of the dungeon map
        /// (selecting one there moves the hero).
        static bool Revealable(Card c)
        {
            if (c == null || !c.isActiveAndEnabled || !c.Flipped || c.Container == null || InMap(c)) return false;
            if (!(c is ChanceCard && ChanceCardTask.RunningTask != null) && !(c.Container is CardSetModifierContainer)) return false;
            UISelectable sel = c.Selectable;
            if (sel == null || !sel.isActiveAndEnabled || !sel.IsSelectable || !sel.HasSelectAction) return false;
            MotionController mc = c.MotionController;
            if (mc != null && mc.HasTarget) return false;
            int id = c.GetInstanceID();
            Vector3 p = c.transform.position;
            KeyValuePair<Vector3, float> was;
            if (!s_still.TryGetValue(id, out was) || (was.Key - p).sqrMagnitude > 1e-6f) { if (s_still.Count > 256) s_still.Clear(); s_still[id] = new KeyValuePair<Vector3, float>(p, Time.unscaledTime); return false; }
            return Time.unscaledTime - was.Value >= StillFor;
        }

        /// The map card under this open hand: the palm within PalmReach of its collider, or the fingertip within TipReach.
        static Card CandidateFor(Hand h) => CandidateFor(h, MapIdle(), RevealIdle());

        static Card CandidateFor(Hand h, bool mapIdle, bool revealIdle)
        {
            if (!HandWorld.Valid(h) || !mapIdle && !revealIdle) return null;
            float s = Scale;
            Vector3 palm = HandWorld.Palm(h), tip = HandFeel.Tip(h);
            int n = Physics.OverlapSphereNonAlloc(palm, (PalmReach + 0.05f) * s, s_hits, PickMask(), QueryTriggerInteraction.Collide);
            Card best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                Collider c = s_hits[i];
                if (c == null || !c.enabled) continue;
                Card card = c.GetComponentInParent<Card>();
                if (card == null || !(mapIdle && Pickable(card)) && !(revealIdle && Revealable(card))) continue;
                float dp = Vector3.Distance(c.ClosestPoint(palm), palm), dt = Vector3.Distance(c.ClosestPoint(tip), tip);
                float d = Mathf.Min(dp / (PalmReach * s), dt / (TipReach * s));   // 1 = at the reach
                if (d > 1f || d >= bestD) continue;
                bestD = d;
                best = card;
            }
            return best;
        }

        internal static int PickMask()
        {
            UI3D ui = UI3D.Instance;
            UICamera uc = ui != null ? ui.UICamera : null;
            return uc != null && uc.cachedCamera != null ? uc.cachedCamera.cullingMask & (int)uc.eventReceiverMask : 1 << 16;
        }

        void Update()
        {
            try { Tick(); }
            catch (Exception e) { Plugin.L.LogError("tablecard: " + e); if (s_holder != null) LetGo("error", true); }
        }

        void Tick()
        {
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 1e-3f, 0.1f);
            bool idle = MapIdle(), revealIdle = RevealIdle();
            if (s_holder != null)
            {
                // the game took the card (another container, hidden) or the map is busy: let go at once
                if (s_card == null || !s_card.isActiveAndEnabled || s_card.Container != s_from) { Aborts++; LetGo("the game took the card", false); }
                else if (s_reveal && (!s_card.Flipped || !s_card.Selectable.IsSelectable)) { Aborts++; LetGo("the game turned the card itself", true); }
                else if (!(s_reveal ? revealIdle : idle) || !s_holder.Tracked) { Aborts++; LetGo("the map is busy", true); }
                else if (s_holder.Grip < ReleaseGrip && Time.unscaledTime - s_since > 0.15f) LetGo("let go", true);
                else
                {
                    Follow(false);
                    if (s_reveal && FaceDot() > FlipDot) TurnedOver();
                }
                s_hover[0] = s_hover[1] = false;
                s_ready[0] = s_ready[1] = 0f;
                return;
            }
            foreach (Hand h in VRInput.RL)
            {
                int i = h.IsLeft ? 1 : 0;
                // a card under the hand (open: ready - a tick, the fingers curl a little); a fresh squeeze with the index
                // on the trigger takes it (a pointing squeeze, index lifted, stays a touch)
                Card c = (idle || revealIdle) && !h.TriggerBtn && !FigurineGrab.Near(h) ? CandidateFor(h, idle, revealIdle) : null;
                if (c != null) s_nearAt[i] = Time.unscaledTime;
                bool hover = c != null && h.Grip < Open;
                if (hover && !s_hover[i]) { Readies++; Haptics.Cue(Haptics.CueKind.Tick, h.IsLeft); }
                s_hover[i] = hover;
                s_cand[i] = c;
                s_ready[i] = Mathf.MoveTowards(s_ready[i], hover ? 1f : 0f, dt * 8f);
                bool pointing = FingerSampler.TriggerTouchSeen && !h.TriggerTouch;
                if (c != null && h.GripDown && !pointing) { Take(h, c); return; }
            }
        }

        static void Take(Hand h, Card c)
        {
            Transform g = VRHands.GloveRoot(h.IsLeft);
            Transform t = c.transform;
            s_holder = h;
            s_card = c;
            s_from = c.Container;
            s_homePos = t.position;
            s_since = Time.unscaledTime;
            // the card stays where it is in the hand (no jump): its pose relative to the glove (or the controller's palm)
            Vector3 p = g != null ? g.position : HandWorld.Palm(h);
            Quaternion r = g != null ? g.rotation : HandWorld.Rot(h);
            Quaternion inv = Quaternion.Inverse(r);
            s_localPos = inv * (t.position - p);
            s_localRot = inv * t.rotation;
            s_reveal = Revealable(c) || c.Flipped && !InMap(c) && (c is ChanceCard || c.Container is CardSetModifierContainer);
            s_faceLocal = FaceAway(c);
            HeldFaceDot = -1f;
            Grabs++;
            s_hover[0] = s_hover[1] = false;
            s_ready[0] = s_ready[1] = 0f;
            Last = (h.IsLeft ? "L" : "R") + " picked up " + c.name.Replace("(Clone)", "") + (s_reveal ? " (face down: turn it over to reveal it)" : "");
            Haptics.Cue(Haptics.CueKind.Grab, h.IsLeft);
            Plugin.L.LogInfo("tablecard: " + Last + " (from " + VRUI.Path(s_from.transform) + ")");
        }

        /// The card's face normal in its own frame: its thinnest axis (the collider's box), the side away from the eyes - a
        /// face-down card shows its back to the player when picked up.
        static Vector3 FaceAway(Card c)
        {
            Transform t = c.transform;
            Vector3 eye = VRRig.I != null ? VRRig.I.HeadWorldPos : t.position + Vector3.up;
            Vector3 world = Vector3.zero;
            BoxCollider box = c.GetComponentInChildren<BoxCollider>();
            if (box != null)
            {
                Vector3 sz = Vector3.Scale(box.size, box.transform.lossyScale);
                sz = new Vector3(Mathf.Abs(sz.x), Mathf.Abs(sz.y), Mathf.Abs(sz.z));
                Vector3 ax = sz.x <= sz.y && sz.x <= sz.z ? Vector3.right : sz.y <= sz.z ? Vector3.up : Vector3.forward;
                world = box.transform.TransformDirection(ax).normalized;
            }
            else
            {
                // no box: the card's own axis most along the line of sight (the card faces the player, back first)
                Vector3 d = (eye - t.position).normalized;
                float bx = Mathf.Abs(Vector3.Dot(t.right, d)), by = Mathf.Abs(Vector3.Dot(t.up, d)), bz = Mathf.Abs(Vector3.Dot(t.forward, d));
                world = bx >= by && bx >= bz ? t.right : by >= bz ? t.up : t.forward;
            }
            Vector3 centre = CardCentre(c);
            if (Vector3.Dot(world, eye - centre) > 0f) world = -world;
            return Quaternion.Inverse(t.rotation) * world;
        }

        static Vector3 CardCentre(Card c)
        {
            Collider col = c.GetComponentInChildren<Collider>();
            return col != null ? col.bounds.center : c.transform.position;
        }

        /// How far the held card's face looks at the eyes (1 = straight at them, -1 = its back to them).
        static float FaceDot()
        {
            if (s_card == null || VRRig.I == null) return -1f;
            Vector3 n = s_card.transform.rotation * s_faceLocal;
            Vector3 d = VRRig.I.HeadWorldPos - CardCentre(s_card);
            HeldFaceDot = d.sqrMagnitude > 1e-8f ? Vector3.Dot(n.normalized, d.normalized) : -1f;
            return HeldFaceDot;
        }

        /// Turned over in the hand: let go (back to its place) and the game's own choice on it - as a fingertip press does
        /// (TouchInput.Confirm): the game reveals it.
        static void TurnedOver()
        {
            Hand h = s_holder;
            Card c = s_card;
            float dot = HeldFaceDot;
            LetGo("turned over", true);
            string why = null;
            try
            {
                UISelectable sel = c != null ? c.Selectable : null;
                if (sel == null || !sel.IsValid) why = "not valid";
                else if (!UISelection.TryHover(sel)) why = "hover refused";
                else if (UISelectable.GetTargetSelectable(sel.Selection.HoveredObject) != sel) why = "hover went elsewhere";
                else if (!UISelection.TrySelect(sel)) why = "select refused";
            }
            catch (Exception e) { why = "error " + e.Message; }
            if (why == null) { Flips++; Last = (h != null && h.IsLeft ? "L" : "R") + " turned over " + c.name.Replace("(Clone)", "") + ": revealed"; if (h != null) PressFeel.Click(h, HandWorld.Palm(h), PressFamily.Card); }
            else { FlipRefusals++; Last = "turned over " + (c != null ? c.name.Replace("(Clone)", "") : "?") + ": " + why; if (h != null) PressFeel.Refuse(h, HandWorld.Palm(h)); }
            Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "tablecard: {0} (its face {1:0.00} toward the eyes)", Last, dot));
        }

        /// The held card at the hand: in Update, and again in the late hook with the render-time glove.
        static void Follow(bool late)
        {
            if (s_holder == null || s_card == null) return;
            Transform g = VRHands.GloveRoot(s_holder.IsLeft);
            if (late && g == null) return;
            Vector3 p = g != null ? g.position : HandWorld.Palm(s_holder);
            Quaternion r = g != null ? g.rotation : HandWorld.Rot(s_holder);
            s_card.transform.SetPositionAndRotation(p + r * s_localPos, r * s_localRot);
        }

        /// VRRig's late hook, after the gloves were placed at the render-time pose.
        public static void Late() { if (s_holder != null) Follow(true); }

        static void LetGo(string why, bool home)
        {
            Hand h = s_holder;
            Card c = s_card;
            CardContainer from = s_from;
            s_holder = null;
            s_card = null;
            s_from = null;
            s_reveal = false;
            Releases++;
            Last = why;
            if (home && c != null && from != null && c.Container == from)
            {
                try { from.RepositionCard(new CardContainer.RepositionData { Card = c, Duration = 0.35f }); }
                catch (Exception e) { Plugin.L.LogWarning("tablecard: back home failed: " + e.Message); }
            }
            if (h != null) Haptics.Cue(Haptics.CueKind.LetGo, h.IsLeft);
            Plugin.L.LogInfo("tablecard: " + why + (home ? " (back to its place)" : ""));
        }

        // ---- tests ----------------------------------------------------------------------------------------------------
        /// The map card nearest the right hand (or the eyes) that could be picked up.
        static Card NearestMapCard()
        {
            Vector3 from = HandWorld.Valid(VRInput.R) ? HandWorld.Palm(VRInput.R) : VRRig.I != null ? VRRig.I.HeadWorldPos : Vector3.zero;
            Card best = null;
            float bestD = float.MaxValue;
            foreach (Card c in UnityEngine.Object.FindObjectsOfType<Card>())
            {
                if (!Pickable(c)) continue;
                float d = (c.transform.position - from).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        /// A point 3 cm above the card's middle (where a palm over it would be).
        static Vector3 Top(Card c)
        {
            Collider col = c.GetComponentInChildren<Collider>();
            Vector3 mid = col != null ? col.bounds.center : c.transform.position;
            float top = col != null ? col.bounds.max.y : mid.y;
            return new Vector3(mid.x, top + 0.03f * Scale, mid.z);
        }

        string Describe()
        {
            string held = s_holder != null && s_card != null
                ? (s_holder.IsLeft ? "L" : "R") + " holds " + s_card.name.Replace("(Clone)", "") + string.Format(CultureInfo.InvariantCulture, " {0:0.000} m from its place", Vector3.Distance(s_card.transform.position, s_homePos) / Scale)
                : "free";
            return held + " | ready L " + s_ready[1].ToString("0.0", CultureInfo.InvariantCulture) + (s_cand[1] != null ? " " + s_cand[1].name.Replace("(Clone)", "") : "") +
                   " R " + s_ready[0].ToString("0.0", CultureInfo.InvariantCulture) + (s_cand[0] != null ? " " + s_cand[0].name.Replace("(Clone)", "") : "") +
                   " | readies " + Readies + " grabs " + Grabs + " releases " + Releases + " aborts " + Aborts + " flips " + Flips + " refused " + FlipRefusals +
                   (s_reveal && s_card != null ? " | face " + HeldFaceDot.ToString("0.00", CultureInfo.InvariantCulture) : "") + " last " + Last + (MapIdle() ? "" : " | map busy");
        }

        /// `tcard` (state), `tcard where [name]` (the held card, the nearest pickable map card or the named card: where it is,
        /// a point 3 cm above it).
        string Command(string[] a)
        {
            // `tcard flipwhere`: the face-down card nearest the right hand that may be turned over now, and a point 2 cm in front
            // of its middle toward the eyes (where an open palm reaching for it would be; the collider's point nearest the eyes
            // lies at an edge, nearer a neighbour in the chance game's row)
            if (a.Length > 1 && a[1] == "flipwhere")
            {
                if (!RevealIdle()) return "not now (reveal idle false)";
                Vector3 from = HandWorld.Valid(VRInput.R) ? HandWorld.Palm(VRInput.R) : VRRig.I.HeadWorldPos;
                Card best = null;
                float bestD = float.MaxValue;
                foreach (Card x in UnityEngine.Object.FindObjectsOfType<Card>())
                {
                    if (!Revealable(x)) continue;
                    float d = (x.transform.position - from).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = x; }
                }
                if (best == null) return "no face-down card to turn over";
                Vector3 eye = VRRig.I.HeadWorldPos, cen = CardCentre(best);
                Vector3 front = cen + (eye - cen).normalized * 0.02f * Scale;
                return string.Format(CultureInfo.InvariantCulture, "{0} at {1:0.###} {2:0.###} {3:0.###} front {4:0.###} {5:0.###} {6:0.###}", best.name.Replace("(Clone)", ""),
                    cen.x, cen.y, cen.z, front.x, front.y, front.z);
            }
            if (a.Length > 1 && a[1] == "where")
            {
                Card c = s_card ?? NearestMapCard();
                if (a.Length > 2)
                {
                    // a card by its name (wherever it is now)
                    c = null;
                    foreach (Card x in UnityEngine.Object.FindObjectsOfType<Card>())
                        if (x != null && x.name.Replace("(Clone)", "") == a[2]) { c = x; break; }
                }
                if (c == null) return "no pickable map card";
                Vector3 p = c.transform.position, t = Top(c);
                return string.Format(CultureInfo.InvariantCulture, "{0} at {1:0.###} {2:0.###} {3:0.###} top {4:0.###} {5:0.###} {6:0.###}{7}", c.name.Replace("(Clone)", ""),
                    p.x, p.y, p.z, t.x, t.y, t.z, c == s_card ? string.Format(CultureInfo.InvariantCulture, " (held, {0:0.000} m from its place)", Vector3.Distance(p, s_homePos) / Scale) : "");
            }
            return Describe();
        }
    }
}

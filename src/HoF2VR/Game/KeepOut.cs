using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Nothing at the Dealer's table comes too close (round 5 item 5; docs/table-events.md). The game presents cards for
    /// its flat camera, which in VR is the player's head: the challenge cards of the caravan's attract screen 0.11 m from
    /// the eyes, the encounter card coming back from a fight (its portal closing) 0.24 m, the monster cards before a fight
    /// 0.44 m (the round-5 sweep). While the table camera draws, every card nearer than Eyes to the head is moved back
    /// along its line of sight to Eyes and grown by the same factor - from the eyes it looks exactly as the game made it,
    /// only at a comfortable depth - and put back after drawing (the game's animation and logic are untouched; its first
    /// frame is already at the new depth). Not moved: the rows the player touches (the choice and shop rows, the inventory,
    /// the deck builder's rows: their places are made for reach), a card in a hand, a chosen card held in place (ChoiceZoom).
    /// The zoom spot keeps its own rule (TableHud.ZoomMin), the tokens theirs (TableHud's bubble). Setting [Table] KeepOut.
    internal class KeepOut : MonoBehaviour
    {
        /// (the user's spawn rule, playtest 2026-10-17: "Nothing ever spawns or quickly gets brought up within 2.5 ft of the
        /// player. Menus and stuff are the exception." 2.5 ft = 0.76 m from the eyes; was 0.47 m) A card that comes nearer is drawn
        /// back along its line of sight to Eyes, grown at most MaxGrow times (was 4: the same picture as the game's, so a card the
        /// game flies into its flat camera filled the view from 0.47 m - the battle card of marks 8 and 14). A card that would
        /// need more than MaxGrow to keep its look simply stays its own size at Eyes.
        /// (revised by the user after the second playtest, 2026-10-17 21:50: "Things can be close to my head. Things can *spawn* or
        /// *jump up* ... close to my head. Maybe 2.5 ft is too long. How about ~1.75ft?" and "The cards all seem to move based on my
        /// head movement. That is exactly what they *shouldn't* do.") 0.53 m, and only for the moment a card appears or is brought
        /// up quickly (SpawnRule): from then on it is kept from the eyes of that moment, so it stays put in the world whatever the
        /// head does; a card the head comes to (leaning in) is never moved, nor anything lying or standing on the table.
        public static float Eyes = 0.53f, MaxGrow = 1.6f, LieEase = 0.4f;
        public static int Pushed, PushedFrames;
        public static string Last = "-";
        static readonly List<Card> s_cards = new List<Card>();
        static readonly List<Card> s_new = new List<Card>();
        static readonly HashSet<CardContainer> s_touchRows = new HashSet<CardContainer>();
        static readonly List<CardMove.Saved> s_moved = new List<CardMove.Saved>();
        static float s_nextScan;
        static int s_frame = -1;
        static string s_trace = "";
        static int s_traceLeft;

        internal static bool TraceOn => s_traceLeft > 0 && s_trace.Length > 0;

        /// (kotrace) the watchdog's side: a renderer whose path holds a traced name
        internal static bool TracedPath(string path)
        {
            if (s_traceLeft <= 0 || s_trace.Length == 0) return false;
            foreach (string part in s_trace.Split('|')) if (part.Length > 0 && path.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        internal static bool Traced(Card c)
        {
            if (s_traceLeft <= 0 || s_trace.Length == 0) return false;
            foreach (string part in s_trace.Split('|')) if (part.Length > 0 && c.name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static void TraceLog(Card c, string what)
        {
            Plugin.L.LogInfo("kotrace " + Time.frameCount + " " + GameWatch.State.Replace("GameState_", "") + " " + c.name.Replace("(Clone)", "") + " [" + (c.Container != null ? c.Container.name : "-") + "] " + what);
        }

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Card), "Start"), postfix: new HarmonyMethod(typeof(KeepOut), nameof(CardStart)));
            try { h.Patch(AccessTools.Method(typeof(CardContainerLayout), "Awake"), postfix: new HarmonyMethod(typeof(KeepOut), nameof(LayoutAwake))); }
            catch (System.Exception e) { Plugin.L.LogWarning("KeepOut: layout awake patch: " + e.Message); }
            // the touch rows' browsers register when they wake (no whole-world search for them)
            try
            {
                h.Patch(AccessTools.Method(typeof(AltCardBrowser), "Awake"), postfix: new HarmonyMethod(typeof(KeepOut), nameof(BrowserAwake)));
                h.Patch(AccessTools.Method(typeof(CardBrowser), "Awake"), postfix: new HarmonyMethod(typeof(KeepOut), nameof(BrowserAwake)));
            }
            catch (Exception e) { Plugin.L.LogWarning("KeepOut: browser hooks: " + e.Message); s_browserHooksFailed = true; }
        }

        static void CardStart(Card __instance) { if (__instance != null) s_new.Add(__instance); }

        static readonly List<MonoBehaviour> s_browsers = new List<MonoBehaviour>();
        static bool s_browserHooksFailed;
        static void BrowserAwake(MonoBehaviour __instance) { if (__instance != null && !s_browsers.Contains(__instance)) s_browsers.Add(__instance); }

        void Awake()
        {
            Camera.onPreCull += PreCull;
            Camera.onPostRender += PostRender;
            // `kotrace <name part> [frames]`: each frame's decision for the cards whose name holds it -> the log (why a card is or
            // isn't kept: skipped as a touch row / held / touchable, not fresh or fast, on the table, its distances)
            Diag.Commands["kotrace"] = a =>
            {
                s_trace = a.Length > 1 ? a[1] : "";
                s_traceLeft = a.Length > 2 ? int.Parse(a[2]) : 900;
                return "tracing '" + s_trace + "' for " + s_traceLeft + " frames";
            };
            Diag.StatusExtras["keepout"] = () => Diag.Q((Settings.KeepOut ? "on" : "off") + " | " + SpawnRule.Describe() + " | cards " + s_cards.Count + ", touch rows " + s_touchRows.Count + " | pushed " + Pushed + " (" + PushedFrames + " frames), effects shrunk " + FxKept + " / not drawn " + FxHidden + ", lifted off the table " + Lifted + ", tokens stopped " + Stopped + " | card lists " + s_cardScans + " (longest " + s_cardScanMs.ToString("0.0") + " ms, " + s_cardScansInView + " in view) | " + Last);
        }

        // ---- the inventory (a touch row the player opens): its cards hung 0.34-0.42 m from the eyes (round 5 invgeo), the
        // nearest edges 0.31 m. The whole inventory (its slots, cards, the context row, their colliders and texts) is placed
        // InventoryFarther times as far from the eyes and as big - the same picture, at 0.46-0.57 m, still within reach.
        public static float InventoryFarther = 1.35f;
        Transform m_inv;
        Vector3 m_invLocal, m_invScale;
        bool m_invBase;
        public static string InvLast = "-";
        static float s_invLastK = -1f;

        void LateUpdate()
        {
            // a new scene's cards listed while the view is still dark (the search over every card in memory, out of sight;
            // otherwise at the first frame KeepOut works - as the old search did five times a second)
            if (s_cardsVersion != SceneScan.Version && VRFade.IsBlack && Settings.KeepOut && XRSettings.enabled) Cards();
            UIInventory inv = UIInventory.Instance;
            if (inv == null) { m_inv = null; return; }
            Transform t = inv.transform;
            if (t != m_inv) { m_inv = t; m_invLocal = t.localPosition; m_invScale = t.localScale; m_invBase = true; }
            if (!m_invBase || t.parent == null) return;
            bool on = Settings.KeepOut && XRSettings.enabled && Places.AtCaravan && VRRig.I != null && VRRig.I.HasStation;
            float k = on ? InventoryFarther : 1f;
            Vector3 eyeLocal = t.parent.InverseTransformPoint(VRRig.I != null ? VRRig.I.NeutralEye : t.parent.position);
            Vector3 want = on ? eyeLocal + (m_invLocal - eyeLocal) * k : m_invLocal;
            // (P2-4) the cards a little lower, clear of the slot icons and trait lists above them (InventoryLayers raises those)
            if (on && InventoryLayers.Active) want += t.parent.InverseTransformVector(Vector3.down * (InventoryLayers.CardsLower * VRRig.I.Scale));
            if ((t.localPosition - want).sqrMagnitude > 1e-10f) t.localPosition = want;
            Vector3 ws = m_invScale * k;
            if ((t.localScale - ws).sqrMagnitude > 1e-14f) t.localScale = ws;
            if (k != s_invLastK) { s_invLastK = k; InvLast = on ? "x" + k.ToString("0.00") : "as the game places it"; }   // (not a new string every frame)
        }

        static bool Wanted => Settings.KeepOut && XRSettings.enabled && Places.AtCaravan && VRRig.I != null && !Limbo.On && !VRFade.IsBlack && !CombatVR.Active;

        // The cards: every card of the loaded scenes once per scene change (SceneScan, inactive ones too) plus each card that
        // starts since (Card.Start); destroyed ones dropped every 2 s. (Until 2026-10-14 this was three whole-world searches
        // every 0.2 s - and every frame a card started, so each zoom at the table re-searched for several frames: 30-55 ms of
        // a frame and most of the mod's garbage at the table, the GC stalls' part 2.)
        static readonly SceneScan.AllOf<Card> s_allCards = new SceneScan.AllOf<Card>();
        static int s_cardsVersion = -1;
        static float s_nextPrune;
        static readonly Predicate<Card> s_gone = c => c == null;
        static int s_cardScans, s_cardScansInView;
        static float s_cardScanMs;
        static readonly Predicate<MonoBehaviour> s_goneB = b => b == null;

        static void Cards()
        {
            if (s_cardsVersion != SceneScan.Version)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                s_cardsVersion = SceneScan.Version;
                s_cards.Clear();
                s_cards.AddRange(s_allCards.Get());
                s_new.Clear();
                s_flyCache.Clear();
                float ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000f / System.Diagnostics.Stopwatch.Frequency;
                s_cardScans++;
                if (!VRFade.IsBlack) s_cardScansInView++;
                if (ms > s_cardScanMs) s_cardScanMs = ms;
            }
            if (s_new.Count > 0)
            {
                for (int i = 0; i < s_new.Count; i++) if (s_new[i] != null && !s_cards.Contains(s_new[i])) s_cards.Add(s_new[i]);
                s_new.Clear();
            }
            if (Time.unscaledTime >= s_nextPrune)
            {
                s_nextPrune = Time.unscaledTime + 2f;
                s_cards.RemoveAll(s_gone);
                s_browsers.RemoveAll(s_goneB);
            }
        }

        /// The rows the player touches (their places are made for reach) and what goes with them.
        static void Scan()
        {
            s_nextScan = Time.unscaledTime + 0.2f;
            s_touchRows.Clear();
            try
            {
                if (UI3D.Instance != null && UI3D.Instance.CardChoice != null && UI3D.Instance.CardChoice.Container != null) s_touchRows.Add(UI3D.Instance.CardChoice.Container);
                if (s_browserHooksFailed && s_browsers.Count == 0)
                {
                    foreach (AltCardBrowser b in FindObjectsOfType<AltCardBrowser>()) s_browsers.Add(b);
                    foreach (CardBrowser b in FindObjectsOfType<CardBrowser>()) s_browsers.Add(b);
                }
                for (int i = 0; i < s_browsers.Count; i++)
                {
                    MonoBehaviour mb = s_browsers[i];
                    if (mb == null) continue;
                    AltCardBrowser ab = mb as AltCardBrowser;
                    CardContainer box = ab != null ? ab.Container : (mb as CardBrowser) != null ? ((CardBrowser)mb).Container : null;
                    if (box != null) s_touchRows.Add(box);
                }
                UIInventory inv = UIInventory.Instance;
                if (inv != null) foreach (CardContainer c in inv.Containers) if (c != null) s_touchRows.Add(c);
                // the wheel's cards: any of them touched stops it (TouchInput)
                if (DeckManager.Instance != null && DeckManager.Instance.WheelSpinContainer != null) s_touchRows.Add(DeckManager.Instance.WheelSpinContainer);
            }
            catch (Exception e) { Last = "scan: " + e.Message; }
        }

        static void PreCull(Camera cam)
        {
            if (s_frame == Time.frameCount) return;
            if (!Wanted) { if (SpawnRule.Count > 0 && (VRFade.IsBlack || Limbo.On || !Places.AtCaravan)) SpawnRule.Clear(); return; }
            UICamera ui = UI3D.Instance != null ? UI3D.Instance.UICamera : null;
            if (ui == null || cam != ui.cachedCamera) return;
            s_frame = Time.frameCount;
            bool started = s_new.Count > 0;   // (a card started: its row may be new - the rows at once, as before; cheap now)
            Cards();
            if (started || Time.unscaledTime >= s_nextScan) Scan();
            SpawnRule.Prune();
            // the eyes now: only for the moment something appears or is brought up (SpawnRule); everything is then kept from
            // the eyes of that moment, so nothing follows the head
            Vector3 live = VRRig.I.HeadWorldPos;
            // (pushed 1 cm past the rule: the watchdog measures the same point)
            float sc = VRRig.I.Scale, r = (Eyes + 0.01f) * sc, bubble = Bubble * sc, near = r + Reach * sc;
            if (s_top0 == float.MinValue || Time.unscaledTime >= s_nextTop0) { s_nextTop0 = Time.unscaledTime + 2f; s_top0 = TableHud.TableTop(float.MinValue); }
            int n = 0;
            for (int i = 0; i < s_cards.Count; i++)
            {
                Card c = s_cards[i];
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                Transform t = c.transform;
                Vector3 d = t.position - live;
                float m = d.magnitude;
                bool tr = Traced(c);
                if (m >= near || m < 1e-4f) { if (tr && m < near * 2f) TraceLog(c, "far " + (m / sc).ToString("0.00")); continue; }   // far off: nothing of it comes near
                CardContainer box = c.Container;
                if (box != null && s_touchRows.Contains(box)) { if (tr) TraceLog(c, "touch row"); continue; }
                // a card the player can touch now (a shop's or a choice's row, whatever its container) keeps its place: its
                // collider is where it is drawn (the shop's row, "Equipment Deck", was drawn 7 cm away from its touch)
                if (Touchable(c)) { if (tr) TraceLog(c, "touchable"); continue; }
                if (FightCard.Holding(c)) { Nearest(c, live, s_flying); n += FxKeep(c, live, r); if (tr) TraceLog(c, "held by the player (its effects kept)"); continue; }
                if (TableCardGrab.HeldCard == c || ChoiceZoom.Holding(c)) { if (tr) TraceLog(c, "held by the player"); continue; }
                if (IsChildOfMoved(t)) { if (tr) TraceLog(c, "child of a moved thing"); continue; }
                SpawnRule.Hold h = SpawnRule.Track(c.GetInstanceID(), t.position);
                if (!h.Has)
                {
                    // not kept yet: only a card that appears now or is brought up quickly is (never one the head comes to), and
                    // never one that lies or stands on the table (the table is where it is: the map, the stat cards, the decks)
                    float dl = Nearest(c, live, s_flying);
                    // (lying on the table = low over it and not rising: a card lifted off a deck toward the player is brought up while
                    // still within the table's 12 cm - the seated end of a challenge's orbit and the combat intro's cards came to
                    // 0.37-0.44 m from the eyes that way, 2026-10-18)
                    bool onTable = s_top0 > float.MinValue && Bottom(c) < s_top0 + SpawnRule.OnTable * sc && !h.Rising;
                    // (a card whose renderers come on after it was first seen appears then)
                    bool appears = Shows(c.GetInstanceID(), dl < 1e6f);
                    if (h.Fresh || h.Fast || h.Recent || appears)
                    {
                        if (!onTable && (dl < r || InBody(c, live, sc))) SpawnRule.Capture(h, live, c.name);
                    }
                    if (tr) TraceLog(c, "not held: root " + (m / sc).ToString("0.00") + " nearest " + (dl / sc).ToString("0.00") + (h.Fresh ? " fresh" : "") + (h.Fast ? " fast" : "") +
                                        (h.Recent ? " recent" : "") + (onTable ? " ON TABLE (bottom " + ((Bottom(c) - s_top0) / sc).ToString("0.00") + ")" : "") + (h.Has ? " -> CAPTURED" : ""));
                    if (!h.Has) { n += FxKeep(c, live, r); n += Flying(live, bubble); continue; }
                }
                Vector3 eye = h.Eye;
                // (come to rest lying on the table - an orbit card landing on a seated player's near deck: eased to where the game
                // has it over LieEase seconds, then let go - the table is where it is)
                float ease = 0f;
                if (s_top0 > float.MinValue && !h.Fast && !h.Rising && Bottom(c) < s_top0 + SpawnRule.OnTable * sc)
                {
                    if (h.LieAt < 0f) h.LieAt = Time.unscaledTime;
                    ease = Mathf.Clamp01((Time.unscaledTime - h.LieAt) / LieEase);
                    if (ease >= 1f) { h.Has = false; h.LieAt = -1f; SpawnRule.Releases++; if (tr) TraceLog(c, "let go: lying on the table"); n += FxKeep(c, live, r); continue; }
                }
                else h.LieAt = -1f;
                // its own nearest point (its face and frame; not the token models flying out of it): all of it goes to Eyes
                float dn = Nearest(c, eye, s_flying);
                if (tr) TraceLog(c, "held: nearest " + (dn / sc).ToString("0.00") + " from its eyes" + (dn < r ? " -> pushed" : ""));
                bool inBody = false;
                if (dn < Through * sc)
                {
                    // flying through the eye itself (a card dealt past the flat camera: its box holds the eye) - no depth
                    // can fix that: it is not drawn for those few frames
                    CardMove.Hide(c, s_hidden);
                    HiddenFrames++;
                    continue;
                }
                if (dn < r)
                {
                    // back to Eyes (its nearest point; grown by g <= k its nearest point stays at least that far)
                    float k = r / Mathf.Max(dn, 1e-3f), g = Mathf.Min(k, MaxGrow);
                    if (ease > 0f) { k = Mathf.Lerp(k, 1f, ease); g = Mathf.Lerp(g, 1f, ease); }
                    // (a card in the air drawn back along a line that falls toward the table would sink into the map - the battle
                    // card of mark 8, 2026-10-17: drawn back and up instead, a few degrees at a time, until it clears the table top)
                    float bottom0 = Bottom(c);
                    int m0 = s_moved.Count;
                    CardMove.Push(c, eye, k, g, s_moved);
                    if (tr)
                    {
                        float after = Nearest(c, eye, s_flying);
                        string nm = "";
                        s_rends.Clear();
                        c.GetComponentsInChildren(false, s_rends);
                        foreach (Renderer rd in s_rends) if (rd != null && rd.enabled && !(rd is ParticleSystemRenderer)) nm += " " + rd.name + "@" + (Vector3.Distance(rd.bounds.ClosestPoint(eye), eye) / sc).ToString("0.00");
                        TraceLog(c, "after the push (k " + k.ToString("0.00") + ", g " + g.ToString("0.00") + "): nearest " + (after / sc).ToString("0.00") + " |" + nm + " | card " + c.GetInstanceID() + " at " + Diag.V(t.position) + " " + VRUI.Path(t));
                    }
                    if (ease <= 0f && s_top0 > float.MinValue && bottom0 > s_top0 + 0.02f * sc && Bottom(c) < s_top0 + 0.005f * sc)
                    {
                        Vector3 dir = (t.position - eye).normalized;
                        Vector3 axis = Vector3.Cross(dir, Vector3.up);
                        if (axis.sqrMagnitude > 1e-6f)
                        {
                            axis.Normalize();
                            for (float ang = 4f; ang <= 40f; ang += 4f)
                            {
                                CardMove.RestoreFrom(s_moved, m0);
                                CardMove.PushTurned(c, eye, Quaternion.AngleAxis(ang, axis), k, g, s_moved);   // (axis = dir x up: +ang turns the line upward)
                                if (Bottom(c) >= s_top0 + 0.005f * sc) { Lifted++; break; }
                            }
                        }
                    }
                    // (grown less than moved - MaxGrow - a wide card very near keeps a corner nearer than the rule; and the lift above
                    // starts again from the game's pose with k and g alone: the seated combat intro's cards, 0.27-0.29 m from the eyes,
                    // lifted clear of the table, were drawn at 0.48 m - 2026-10-18. Moved on along its line, not grown, until it is out)
                    for (int it = 0; it < 3 && g < k && ease <= 0f; it++)
                    {
                        float dn2 = Nearest(c, eye, s_flying);
                        if (dn2 >= r - 0.001f * sc || dn2 < 1e-4f) break;
                        CardMove.Push(c, eye, r / dn2, 1f, s_moved);
                    }
                    n++;
                    if (s_logged.Count < 60 && s_logged.Add(c.GetInstanceID()))
                        Plugin.L.LogInfo("keepout: " + c.name.Replace("(Clone)", "") + (box != null ? " (" + box.name + ")" : "") + " drawn back from " + (dn / sc).ToString("0.00") + " to " + Eyes.ToString("0.00") + " m (x" + g.ToString("0.0") + " size) in " + GameWatch.State.Replace("GameState_", "") + " / " + Diag.CurrentMenu());
                    if (Pushed == 0 || Time.frameCount % 90 == 0) Last = c.name.Replace("(Clone)", "") + " " + (dn / sc).ToString("0.00") + " -> " + Eyes.ToString("0.00") + " m (x" + g.ToString("0.0") + " size)" + (box != null ? " in " + box.name : "");
                }
                // the body (the end of a challenge: its discard sends the cards round the player in an orbit, at chest height
                // 0.21-0.22 m from the eyes' vertical - RC tour 13 Oct): a card in the body's cylinder - below the eyes, above
                // the table top, not standing on it - is moved out sideways to BodyKeep (moving away from the vertical only
                // takes it further from the eyes)
                if (BodyOut(c, eye, sc)) { n++; inBody = true; }
                SpawnRule.Settle(h, dn >= r && !inBody);
                Nearest(c, eye, s_flying);   // (its effects, where the card is drawn now)
                n += FxKeep(c, live, r);
                n += Flying(live, bubble);
            }
            // the deck layouts (the player's InventoryDeck / ReserveDeck card stacks, DeckLayout: card backs, no Card): candidate 8's
            // seated b1 - 0.67-0.73 m from seated eyes at the call to battle. Drawn back like a card - when one is brought up, not
            // where it lies on the table.
            // (every 0.2 s at the end of a challenge and before a fight - a reward's stack appears then: the token reward's
            // 'TokenRewardStackContainer' 0.40 m from seated eyes on its first frame - else every second)
            bool busy = GameWatch.State == "GameState_Post" || GameWatch.State == "GameState_Level_In";
            if (TableHud.LegacyScans) { if (Time.unscaledTime >= s_nextDecks) { s_nextDecks = Time.unscaledTime + (busy ? 0.2f : 1f); s_decks = FindObjectsOfType<DeckLayout>(); } }
            else if (GatherDecks()) s_decks = s_deckList.ToArray();
            foreach (DeckLayout dl in s_decks)
            {
                // (by its object, not the component's switch - the game's layouts are often switched off, as its containers are;
                // and by its cards, not its pivot: the token reward's stack hung 0.40 m from seated eyes on its first frame)
                if (dl == null || !dl.gameObject.activeInHierarchy) continue;
                Transform dt = dl.transform;
                if (TraceOn && s_trace.IndexOf("deck", StringComparison.OrdinalIgnoreCase) >= 0 && Time.frameCount % 15 == 0)
                    Plugin.L.LogInfo("kotrace " + Time.frameCount + " deck layout seen " + VRUI.Path(dt) + " pivot " + ((dt.position - live).magnitude / sc).ToString("0.00") + " nearest " + (NearestUnder(dt, live) / sc).ToString("0.00") +
                                     " box " + (dl.GetComponentInParent<CardContainer>() != null ? dl.GetComponentInParent<CardContainer>().name : "-"));
                if ((dt.position - live).magnitude >= near * 3f || IsChildOfMoved(dt)) continue;
                CardContainer dbox = dl.GetComponentInParent<CardContainer>();
                if (dbox != null && s_touchRows.Contains(dbox)) continue;
                SpawnRule.Hold h = SpawnRule.Track(dl.GetInstanceID(), dt.position);
                if (!h.Has)
                {
                    // (the token reward's stack is there, hidden, before its card back is shown 0.40 m from seated eyes: shown = appears)
                    float dl0 = NearestUnder(dt, live);
                    bool appears = Shows(dl.GetInstanceID(), dl0 < 1e6f);
                    if (!(h.Fresh || h.Fast || h.Recent || appears)) continue;
                    if (s_top0 > float.MinValue && LowestUnder(dt) < s_top0 + SpawnRule.OnTable * sc && !h.Rising) continue;
                    if (TraceOn && s_trace.IndexOf("deck", StringComparison.OrdinalIgnoreCase) >= 0)
                        Plugin.L.LogInfo("kotrace " + Time.frameCount + " deck layout " + VRUI.Path(dt) + " nearest " + (dl0 / sc).ToString("0.00") + (h.Fresh ? " fresh" : "") + (h.Fast ? " fast" : ""));
                    if (dl0 >= r) continue;
                    SpawnRule.Capture(h, live, dl.name);
                }
                float dd0 = NearestUnder(dt, h.Eye);
                SpawnRule.Settle(h, dd0 >= r);
                if (dd0 >= r || dd0 < 1e-4f) continue;
                float kk = r / dd0;
                CardMove.Push(dl, h.Eye, kk, Mathf.Min(kk, MaxGrow), s_moved);
                DecksPushed++;
                n++;
            }
            // the titles of the 3D UI's containers (a reward's 'Containers (Scaled)/CardSetModifierContainer/Title', an NGUI text the
            // game hangs 0.27 m from its flat camera): kept like the cards, from the eyes of the moment they appear
            foreach (Transform tt in Titles())
            {
                if (tt == null || !tt.gameObject.activeInHierarchy) continue;
                SpawnRule.Hold h = SpawnRule.Track(tt.GetInstanceID(), tt.position);
                if (TraceOn && s_trace.IndexOf("title", StringComparison.OrdinalIgnoreCase) >= 0)
                    Plugin.L.LogInfo("kotrace " + Time.frameCount + " title " + VRUI.Path(tt) + " texts " + (CardMove.NearestTexts(tt, live) / sc).ToString("0.00") + (h.Has ? " held" : "") + (h.Fresh ? " fresh" : "") + (h.Fast ? " fast" : ""));
                // (its texts are drawn a frame after it is switched on: the first frame they can be measured is when it appears)
                float tl = CardMove.NearestTexts(tt, live);
                bool shown = tl < 1e6f, wasShown = s_titleShown.Contains(tt.GetInstanceID());
                if (shown && !wasShown) s_titleShown.Add(tt.GetInstanceID()); else if (!shown && wasShown) s_titleShown.Remove(tt.GetInstanceID());
                if (!h.Has && (h.Fresh || h.Fast || h.Recent || (shown && !wasShown)) && tl < r) SpawnRule.Capture(h, live, tt.parent.name + "/Title");
                if (!h.Has) continue;
                float dt2 = CardMove.NearestTexts(tt, h.Eye);
                SpawnRule.Settle(h, dt2 >= r);
                if (dt2 < r && dt2 > 1e-4f) { float kt = r / dt2; CardMove.Push(tt, h.Eye, kt, Mathf.Min(kt, MaxGrow), s_moved); TitlesPushed++; n++; }
            }
            // the zoom's darkening veil (CardZoom/ZoomDarken, its own NGUI panel) hangs 0.21 m from the flat camera: back with
            // the zoom's own rule, the same picture (kept from the eyes of the moment it appeared)
            Transform dk = Darken();
            if (dk != null && dk.gameObject.activeInHierarchy)
            {
                SpawnRule.Hold h = SpawnRule.Track(dk.GetInstanceID(), dk.position);
                if (!h.Has && (h.Fresh || h.Fast) && CardMove.NearestTexts(dk, live) < r) SpawnRule.Capture(h, live, "ZoomDarken");
                if (h.Has)
                {
                    float dd = CardMove.NearestTexts(dk, h.Eye);   // an NGUI widget: drawn by its panel's draw calls
                    SpawnRule.Settle(h, dd >= r);
                    if (dd < r && dd > 1e-4f) { float kd = r / dd; CardMove.Push(dk, h.Eye, kd, kd, s_moved); n++; }
                }
            }
            if (n > 0) { Pushed += n; PushedFrames++; }
            if (s_traceLeft > 0) s_traceLeft--;
        }

        /// The token or shard models flying out of the card just measured (s_flying): one that comes near when it appears or flies
        /// up stops at the bubble round the eyes of that moment, no bigger (the challenge tokens of the caravan's attract screen
        /// came to 0.07 m).
        static int Flying(Vector3 live, float bubble)
        {
            int n = 0;
            for (int j = 0; j < s_flying.Count; j++)
            {
                Transform fly = s_flying[j];
                if (fly == null) continue;
                SpawnRule.Hold fh = SpawnRule.Track(fly.GetInstanceID(), fly.position);
                if (!fh.Has)
                {
                    if (!(fh.Fresh || fh.Fast) || NearestUnder(fly, live) >= bubble) continue;
                    SpawnRule.Capture(fh, live, fly.name);
                }
                float fn = NearestUnder(fly, fh.Eye);
                SpawnRule.Settle(fh, fn >= bubble);
                if (fn >= bubble) continue;
                Vector3 dir = (fly.position - fh.Eye).sqrMagnitude > 1e-8f ? (fly.position - fh.Eye).normalized : VRRig.I.NeutralRot * Vector3.forward;
                s_moved.Add(new CardMove.Saved { T = fly, Pos = fly.position, Scale = fly.localScale });
                fly.position += dir * (bubble - fn + 0.01f * VRRig.I.Scale);
                Stopped++;
                n++;
            }
            return n;
        }

        /// The lowest point of what a transform draws.
        static float LowestUnder(Transform root)
        {
            s_rends.Clear();
            root.GetComponentsInChildren(false, s_rends);
            float lo = float.MaxValue;
            foreach (Renderer rd in s_rends) if (rd != null && rd.enabled && !(rd is ParticleSystemRenderer)) lo = Mathf.Min(lo, rd.bounds.min.y);
            return lo;
        }

        static readonly HashSet<int> s_logged = new HashSet<int>();
        static DeckLayout[] s_decks = new DeckLayout[0];
        // (the third playtest, marks 3, 13: the whole-world search for them every second - every 0.2 s before a fight - cost a
        // missed frame each time; now once per scene load, and the new ones join at their Awake)
        static readonly System.Collections.Generic.List<DeckLayout> s_deckList = new System.Collections.Generic.List<DeckLayout>(), s_newDecks = new System.Collections.Generic.List<DeckLayout>();
        static int s_deckScene = -1;
        static void LayoutAwake(CardContainerLayout __instance) { DeckLayout d = __instance as DeckLayout; if (d != null) s_newDecks.Add(d); }
        static bool GatherDecks()
        {
            bool changed = false;
            if (s_deckScene != SceneScan.Version)
            {
                s_deckScene = SceneScan.Version;
                s_deckList.Clear();
                // (inactive ones too: a layout switched off at the scan and on again later gets no second Awake - the seated b1's
                // OffscreenStatHand cards, 0.50 m from the eyes at the call to battle, were not held then)
                foreach (DeckLayout d in Resources.FindObjectsOfTypeAll<DeckLayout>()) if (d != null && d.gameObject.scene.IsValid()) s_deckList.Add(d);
                changed = true;
            }
            foreach (DeckLayout d in s_newDecks) if (d != null && !s_deckList.Contains(d)) { s_deckList.Add(d); changed = true; }
            s_newDecks.Clear();
            if (s_deckList.RemoveAll(d => d == null) > 0) changed = true;
            return changed;
        }
        static readonly List<Transform> s_titles = new List<Transform>();
        static readonly HashSet<int> s_titleShown = new HashSet<int>();
        static readonly HashSet<int> s_shown = new HashSet<int>();

        /// Drawn now (something of it can be measured) but not last time it was looked at: it appears.
        static bool Shows(int id, bool shown)
        {
            if (shown) return s_shown.Add(id);
            s_shown.Remove(id);
            return false;
        }
        static int s_titlesScene = -1;
        public static int TitlesPushed;

        /// 'UI Root (3D)/3D Root/Scalar/Containers (Scaled)/*/Title' (found once per scene).
        static List<Transform> Titles()
        {
            if (s_titlesScene == SceneScan.Version) return s_titles;
            s_titlesScene = SceneScan.Version;
            s_titles.Clear();
            GameObject root = GameObject.Find("UIManager/UI Root (3D)/3D Root/Scalar/Containers (Scaled)");
            if (root != null)
                foreach (Transform box in root.transform)
                {
                    Transform t = box.Find("Title");
                    if (t != null) s_titles.Add(t);
                }
            return s_titles;
        }
        static float s_nextDecks;
        public static int DecksPushed;
        static float s_top0 = float.MinValue, s_nextTop0;
        public static int Lifted;

        /// The lowest point of a card's own renderers (not the token models flying out of it).
        static float Bottom(Card c)
        {
            s_rends.Clear();
            c.GetComponentsInChildren(false, s_rends);
            float lo = float.MaxValue;
            foreach (Renderer rd in s_rends)
            {
                if (rd == null || !rd.enabled || rd is ParticleSystemRenderer || FlyingRoot(rd.transform) != null || FxRoot(rd.transform, c.transform) != null) continue;
                lo = Mathf.Min(lo, rd.bounds.min.y);
            }
            return lo;
        }

        static bool Touchable(Card c)
        {
            UISelectable sel = c.Selectable;
            return sel != null && sel.isActiveAndEnabled && sel.IsSelectable;
        }
        static Transform s_darken;
        static CardZoom s_darkenOf;

        static Transform Darken()
        {
            CardZoom z = UI3D.Instance != null ? UI3D.Instance.CardZoom : null;
            if (z == null) return null;
            if (z != s_darkenOf) { s_darkenOf = z; s_darken = z.transform.Find("ZoomDarken"); }
            return s_darken;
        }

        /// (a token's pivot; its model reaches up to 5 cm nearer: 0.58 keeps the drawn token outside the 0.53 m rule)
        public static float Bubble = 0.58f, Reach = 1.0f, Through = 0.12f;
        /// The body's cylinder kept clear (Proximity's 0.22 m plus 2 cm), from 5 cm over the table top up to the eyes.
        public static float BodyKeep = 0.24f;
        public static int BodyPushed;
        static float s_top = float.MinValue, s_nextTop;

        /// In the body's cylinder round the eyes' vertical (below the eyes, above the table top, not standing on it): how far its
        /// box is from that vertical (world units), and the box.
        static bool BodyBox(Card c, Vector3 eye, float sc, out float hd, out Bounds b)
        {
            hd = float.MaxValue;
            b = default(Bounds);
            if (Time.unscaledTime >= s_nextTop) { s_nextTop = Time.unscaledTime + 2f; s_top = TableHud.TableTop(float.MinValue); }
            if (s_top == float.MinValue) return false;
            s_rends.Clear();
            c.GetComponentsInChildren(false, s_rends);
            bool has = false;
            foreach (Renderer rd in s_rends)
            {
                if (rd == null || !rd.enabled || rd is ParticleSystemRenderer) continue;
                if (!has) { b = rd.bounds; has = true; } else b.Encapsulate(rd.bounds);
            }
            if (!has) return false;
            float from = s_top + 0.05f * sc;
            if (b.min.y >= eye.y || b.max.y <= from || b.min.y < s_top + 0.03f * sc) return false;   // above the eyes, on the table
            float dx = Mathf.Max(0f, Mathf.Max(b.min.x - eye.x, eye.x - b.max.x)), dz = Mathf.Max(0f, Mathf.Max(b.min.z - eye.z, eye.z - b.max.z));
            hd = Mathf.Sqrt(dx * dx + dz * dz);
            return hd < BodyKeep * sc;
        }

        static bool InBody(Card c, Vector3 eye, float sc) { float hd; Bounds b; return BodyBox(c, eye, sc, out hd, out b); }

        static bool BodyOut(Card c, Vector3 eye, float sc)
        {
            float hd;
            Bounds b;
            if (!BodyBox(c, eye, sc, out hd, out b)) return false;
            float keep = BodyKeep * sc;
            Vector3 flat = new Vector3(b.center.x - eye.x, 0f, b.center.z - eye.z);
            Vector3 dir = flat.sqrMagnitude > 1e-6f ? flat.normalized : VRRig.I.NeutralRot * Vector3.forward;
            CardMove.Shift(c, dir * (keep - hd + 0.005f * sc), s_moved);
            BodyPushed++;
            if (s_logged.Count < 60 && s_logged.Add(c.GetInstanceID() ^ 0x5bd1e995))
                Plugin.L.LogInfo("keepout: " + c.name.Replace("(Clone)", "") + (c.Container != null ? " (" + c.Container.name + ")" : "") + " moved out of the body: " +
                                 (hd / sc).ToString("0.00") + " -> " + BodyKeep.ToString("0.00") + " m from the eyes' vertical in " + GameWatch.State.Replace("GameState_", "") + " / " + Diag.CurrentMenu());
            return true;
        }
        public static int HiddenFrames;
        static readonly List<Renderer> s_hidden = new List<Renderer>();
        public static int Stopped;
        static readonly List<Renderer> s_rends = new List<Renderer>();
        static readonly List<Transform> s_flying = new List<Transform>();

        /// The nearest point of a card's own renderers to the eye (world units); the "AnimationContainer"s of token and
        /// shard models in it are listed in `flying` instead.
        static float Nearest(Card c, Vector3 eye, List<Transform> flying)
        {
            flying.Clear();
            s_fxRoots.Clear();
            s_rends.Clear();
            c.GetComponentsInChildren(false, s_rends);
            float best = float.MaxValue;
            foreach (Renderer rd in s_rends)
            {
                if (rd == null || !rd.enabled) continue;
                Transform fly = FlyingRoot(rd.transform);
                if (fly != null) { if (!flying.Contains(fly)) flying.Add(fly); continue; }
                // (particles: their box is the system's bounds, not where the particles are - the portal's held the eyes; the
                // watchdog leaves them out too)
                if (rd is ParticleSystemRenderer) continue;
                // (an effect it carries - the combat portal's glow and wormhole, 'FXCardCombat_Portal': kept on its own, FxKeep;
                // its bounds reached 0.5 m below the table and round the eyes, so the card counted as lying on the table and was
                // never kept - the call to battle, 2026-10-18)
                Transform fx = FxRoot(rd.transform, c.transform);
                if (fx != null) { if (!s_fxRoots.Contains(fx)) s_fxRoots.Add(fx); continue; }
                best = Mathf.Min(best, Vector3.Distance(rd.bounds.ClosestPoint(eye), eye));
            }
            return best;
        }

        // ---- the effects a card carries (the call to battle's portal, 2026-10-18) -------------------------------------------
        static readonly List<Transform> s_fxRoots = new List<Transform>();
        static readonly Dictionary<int, Transform> s_fxCache = new Dictionary<int, Transform>();
        static readonly List<Renderer> s_fxRends = new List<Renderer>();
        static float s_fxPrune;
        public static int FxKept, FxHidden;

        /// The topmost transform named "FX..." between a renderer and its card (the game's effects: FXCardCombat_Portal with its
        /// FXCardEdgeGlow and wormhole_fade_in), or null; cached per transform (a name read makes a new string).
        static Transform FxRoot(Transform t, Transform card)
        {
            if (Time.unscaledTime >= s_fxPrune) { s_fxPrune = Time.unscaledTime + 10f; s_fxCache.Clear(); }
            int id = t.GetInstanceID();
            Transform root;
            if (s_fxCache.TryGetValue(id, out root) && (root == null || t.IsChildOf(root))) return root;
            root = null;
            for (Transform x = t; x != null && x != card; x = x.parent)
                if (x.name.StartsWith("FX", StringComparison.Ordinal)) root = x;
            s_fxCache[id] = root;
            return root;
        }

        /// The card's effects (found by the last Nearest) under the rule: an effect that appears or is brought up within it is
        /// kept from the eyes of that moment - shrunk toward the card's middle until its nearest point is at the rule (the
        /// game's wormhole is a tunnel aimed at its flat camera: 0.2 m from the eyes, round the head when the card was touched),
        /// or not drawn when even a fifth of it would come nearer (the card itself within the rule: the fight card in the hand).
        static int FxKeep(Card c, Vector3 live, float r)
        {
            if (s_fxRoots.Count == 0) return 0;
            int n = 0;
            Vector3 anchor = Centre(c);
            for (int i = 0; i < s_fxRoots.Count; i++)
            {
                Transform f = s_fxRoots[i];
                if (f == null) continue;
                SpawnRule.Hold h = SpawnRule.Track(f.GetInstanceID(), f.position);
                if (!h.Has)
                {
                    if (!(h.Fresh || h.Fast || h.Recent) || NearestFx(f, live) >= r) continue;
                    SpawnRule.Capture(h, live, f.name);
                }
                float d = NearestFx(f, h.Eye);
                SpawnRule.Settle(h, d >= r);
                if (d >= r) continue;
                float k = FxScale(f, anchor, h.Eye, r);
                if (k < 0.2f)
                {
                    s_fxRends.Clear();
                    f.GetComponentsInChildren(false, s_fxRends);
                    foreach (Renderer rd in s_fxRends) if (rd != null && rd.enabled && !(rd is ParticleSystemRenderer)) { rd.enabled = false; s_hidden.Add(rd); }
                    FxHidden++;
                }
                else { CardMove.ScaleAbout(f, anchor, k, s_moved, false); FxKept++; }
                n++;
                if (s_logged.Count < 60 && s_logged.Add(f.GetInstanceID()))
                    Plugin.L.LogInfo("keepout: " + c.name.Replace("(Clone)", "") + "'s effect " + f.name.Replace("(Clone)", "") + " " + (d / VRRig.I.Scale).ToString("0.00") + " m from the eyes: " +
                                     (k < 0.2f ? "not drawn" : "shrunk x" + k.ToString("0.00") + " toward the card") + " in " + GameWatch.State.Replace("GameState_", ""));
            }
            return n;
        }

        /// The middle of the card's own renderers (not its effects or flying tokens).
        static Vector3 Centre(Card c)
        {
            s_rends.Clear();
            c.GetComponentsInChildren(false, s_rends);
            bool any = false;
            Bounds b = default(Bounds);
            foreach (Renderer rd in s_rends)
            {
                if (rd == null || !rd.enabled || rd is ParticleSystemRenderer || FlyingRoot(rd.transform) != null || FxRoot(rd.transform, c.transform) != null) continue;
                if (!any) { b = rd.bounds; any = true; } else b.Encapsulate(rd.bounds);
            }
            return any ? b.center : c.transform.position;
        }

        /// The largest factor (<= 1) the effect can keep, shrunk about `anchor`, with its nearest point at least `r` from `eye`
        /// (its renderers' boxes shrunk with it); 0 when none.
        static float FxScale(Transform f, Vector3 anchor, Vector3 eye, float r)
        {
            s_fxRends.Clear();
            f.GetComponentsInChildren(false, s_fxRends);
            float lo = 0f, hi = 1f;
            if (Vector3.Distance(anchor, eye) < r) return 0f;
            for (int it = 0; it < 12; it++)
            {
                float mid = (lo + hi) * 0.5f;
                if (FxNearest(anchor, eye, mid) >= r) lo = mid; else hi = mid;
            }
            return FxNearest(anchor, eye, 1f) >= r ? 1f : lo;
        }

        /// The effect's nearest point (its meshes; not its particles' system bounds).
        static float NearestFx(Transform f, Vector3 eye)
        {
            s_fxRends.Clear();
            f.GetComponentsInChildren(false, s_fxRends);
            float best = float.MaxValue;
            foreach (Renderer rd in s_fxRends) if (rd != null && rd.enabled && !(rd is ParticleSystemRenderer)) best = Mathf.Min(best, Vector3.Distance(rd.bounds.ClosestPoint(eye), eye));
            return best;
        }

        static float FxNearest(Vector3 anchor, Vector3 eye, float k)
        {
            float best = float.MaxValue;
            foreach (Renderer rd in s_fxRends)
            {
                if (rd == null || !rd.enabled || rd is ParticleSystemRenderer) continue;
                Bounds b = rd.bounds;
                var bs = new Bounds(anchor + (b.center - anchor) * k, b.size * k);
                best = Mathf.Min(best, Vector3.Distance(bs.ClosestPoint(eye), eye));
            }
            return best;
        }

        /// (each transform's "is it an AnimationContainer" cached: Object.name makes a new string on every read, and this ran
        /// for every renderer of every card near the player every frame; the walk up stays live, so pooled models moved to
        /// another parent are seen where they are)
        static readonly Dictionary<int, bool> s_flyCache = new Dictionary<int, bool>();

        static Transform FlyingRoot(Transform t)
        {
            for (int i = 0; i < 3 && t != null; i++, t = t.parent)
            {
                int id = t.GetInstanceID();
                bool fly;
                if (!s_flyCache.TryGetValue(id, out fly))
                {
                    if (s_flyCache.Count > 50000) s_flyCache.Clear();
                    fly = t.name == "AnimationContainer";
                    s_flyCache[id] = fly;
                }
                if (fly) return t;
            }
            return null;
        }

        static float NearestUnder(Transform root, Vector3 eye)
        {
            s_rends.Clear();
            root.GetComponentsInChildren(false, s_rends);
            float best = float.MaxValue;
            foreach (Renderer rd in s_rends) if (rd != null && rd.enabled) best = Mathf.Min(best, Vector3.Distance(rd.bounds.ClosestPoint(eye), eye));
            return best;
        }

        /// A card inside a card already moved (rare) goes with it.
        static bool IsChildOfMoved(Transform t)
        {
            for (int i = 0; i < s_moved.Count; i++) if (s_moved[i].T != null && t.IsChildOf(s_moved[i].T)) return true;
            return false;
        }

        static void PostRender(Camera cam)
        {
            if (s_moved.Count == 0 && s_hidden.Count == 0) return;
            if (cam.stereoEnabled && cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left) return;
            UICamera ui = UI3D.Instance != null ? UI3D.Instance.UICamera : null;
            if (ui == null || cam != ui.cachedCamera) return;
            CardMove.Restore(s_moved);   // in reverse order (a child moved after its parent)
            foreach (Renderer h in s_hidden) if (h != null) h.enabled = true;
            s_hidden.Clear();
        }

        /// NGUI's draw calls (root "New Game Object"s: the texts of a card) belong to their panel's place.
        public static Transform Owner(Transform t)
        {
            if (t == null) return null;
            UIDrawCall dc = t.GetComponent<UIDrawCall>();
            return dc != null && dc.manager != null ? dc.manager.cachedTransform : t;
        }

        /// The watchdog's question: is this renderer part of a touch row (made for reach) - reported, not a violation.
        public static bool InTouchRow(Transform t)
        {
            t = Owner(t);
            if (t == null) return false;
            UIInventory inv = UIInventory.Instance;
            if (inv != null && t.IsChildOf(inv.transform)) return true;
            // the hero's figurine: the player's own piece, picked up and carried by hand (FigurineGrab) - on a seated table's
            // near row it stands ~0.44 m from the eyes, as a piece on a real table would (the seated tour 2026-10-14)
            if (Settings.GrabThrow)
            {
                Map m = Map.Instance;
                if (m != null && m.PlayerCounter != null && t.IsChildOf(m.PlayerCounter.transform)) return true;
            }
            Card c = t.GetComponentInParent<Card>();
            // (the wheel's cards sweep toward the eyes: measured by the rule since 2026-10-18 - WheelSeat keeps their circle out)
            DeckManager dm = DeckManager.Instance;
            if (c != null && dm != null && dm.WheelSpinContainer != null && c.Container == dm.WheelSpinContainer) return false;
            // (the fight card in the player's hand is touched; the game's portal opening on it is not - KeepOut keeps it, FxKeep)
            if (c != null && FightCard.Holding(c) && FxRoot(t, c.transform) != null) return false;
            // (a chosen card held in place and brought 4 cm forward is the player's own touch: ChoiceZoom)
            // (the fight card held out is brought up: no touch row since 2026-10-18 - the watchdog measures its face, HeldFightCardFace)
            return c != null && (Touchable(c) || ChoiceZoom.Holding(c) || c.Container != null && s_touchRows.Contains(c.Container));
        }

        /// The watchdog's question: is this renderer the held-out fight card itself (its model or texts, not the portal effect it
        /// carries)? It is measured by its face (FightCard.FaceDistance), by the spawn rule.
        public static bool HeldFightCardFace(Transform t)
        {
            t = Owner(t);
            if (t == null) return false;
            Card c = t.GetComponentInParent<Card>();
            return c != null && FightCard.Holding(c) && FxRoot(t, c.transform) == null;
        }
    }

    /// The user's spawn rule as it now stands (2026-10-17 21:50, after the second playtest; docs/decisions.md): something that
    /// appears, or is brought up quickly (a reward, a card the game shows, coins), has its place at least 0.53 m from the eyes
    /// AT THAT MOMENT, and after that it stays put in the world - nothing on the table moves with the head; leaning in close to a
    /// card is fine. (Until then KeepOut and the token bubble measured every frame from the head, so whatever was near moved,
    /// grew and jumped as the head moved: marks 9, 11, 13 and 17 of the second playtest.)
    /// Each thing that comes near gets a Hold: the first frame it is seen, or the frames it moves faster than Speed, while it is
    /// within the rule of the eyes, the eyes are taken (Capture) and kept; it is then always drawn back from those eyes, never
    /// from the head of the moment. The hold ends (Settle) once it has been outside the rule, resting, for Release seconds - so
    /// the next time it is brought up the eyes of that moment count. A thing never captured (one the head leans to, one lying on
    /// the table) is never moved. Everything is forgotten in the dark (a place change, a fade), so after it all is new again.
    internal static class SpawnRule
    {
        /// Speed: real metres a second that count as "brought up" (a card's idle bob is ~0.03); OnTable: a thing whose lowest point
        /// is within this of the table top lies or stands on the table (the stat fan is propped 6 cm up); Release: seconds.
        public static float Speed = 0.10f, OnTable = 0.12f, Release = 0.3f, RecentFast = 0.6f, RiseSpeed = 0.05f;
        public static int Captures, Releases;
        public static string LastCapture = "-";

        internal sealed class Hold
        {
            public Vector3 Last, Eye;
            public float At, Rest = -1f;
            public int Frame;
            public bool Has, Fresh, Fast, Recent, Rising;
            public float FastAt = -10f, RiseAt = -10f, LieAt = -1f;
        }

        static readonly Dictionary<int, Hold> s_holds = new Dictionary<int, Hold>();
        static readonly List<int> s_drop = new List<int>();
        static float s_nextPrune;
        public static int Count => s_holds.Count;
        public static int Held { get { int n = 0; foreach (Hold h in s_holds.Values) if (h.Has) n++; return n; } }

        /// This frame's look at a thing at `pos` (its game pose, before any draw-time move): Fresh = seen for the first time (or
        /// again after an absence), Fast = moved faster than Speed since it was last seen.
        public static Hold Track(int id, Vector3 pos)
        {
            float now = Time.unscaledTime;
            int f = Time.frameCount;
            Hold h;
            if (!s_holds.TryGetValue(id, out h)) { h = new Hold { Last = pos, At = now, Frame = f, Fresh = true }; s_holds[id] = h; return h; }
            if (h.Frame == f) return h;   // (asked twice in a frame: KeepOut and the token bubble)
            bool absent = f - h.Frame > 3;
            float dt = Mathf.Max(now - h.At, 1e-3f);
            float sc = VRRig.I != null ? VRRig.I.Scale : 1f;
            h.Fresh = absent;
            h.Fast = !absent && (pos - h.Last).magnitude / dt > Speed * sc;
            if (h.Fast) h.FastAt = now;
            // (rising: going up faster than RiseSpeed - a card lifted off a deck on the table, or one that just was)
            if (!absent && (pos.y - h.Last.y) / dt > RiseSpeed * sc) h.RiseAt = now;
            h.Rising = !absent && now - h.RiseAt < RecentFast;
            // (still settling from a fast move: a card the game eases in slows below Speed before it crosses the rule)
            h.Recent = !absent && !h.Fast && now - h.FastAt < RecentFast;
            if (absent) { h.Has = false; h.Rest = -1f; }
            h.Last = pos;
            h.At = now;
            h.Frame = f;
            return h;
        }

        public static void Capture(Hold h, Vector3 eye, string what)
        {
            h.Has = true;
            h.Eye = eye;
            h.Rest = -1f;
            Captures++;
            LastCapture = (what ?? "?").Replace("(Clone)", "") + (h.Fresh ? " appeared" : " brought up") + " at " + Time.unscaledTime.ToString("0.0");
        }

        /// After the frame's move: `outside` = no part of it within the rule of its kept eyes. Held on while it is inside or moving.
        public static void Settle(Hold h, bool outside)
        {
            if (!h.Has) return;
            if (!outside || h.Fast) { h.Rest = -1f; return; }
            if (h.Rest < 0f) h.Rest = Time.unscaledTime;
            else if (Time.unscaledTime - h.Rest >= Release) { h.Has = false; h.Rest = -1f; Releases++; }
        }

        public static void Clear() { s_holds.Clear(); }

        /// The eyes a thing is kept from (its hold's), if it is held now (the watchdog measures from them).
        public static bool TryEye(int id, out Vector3 eye)
        {
            Hold h;
            eye = Vector3.zero;
            if (!s_holds.TryGetValue(id, out h) || !h.Has || Time.frameCount - h.Frame > 3) return false;
            eye = h.Eye;
            return true;
        }

        /// Things not seen for 5 s forgotten (every 5 s).
        public static void Prune()
        {
            if (Time.unscaledTime < s_nextPrune) return;
            s_nextPrune = Time.unscaledTime + 5f;
            s_drop.Clear();
            int f = Time.frameCount;
            foreach (KeyValuePair<int, Hold> kv in s_holds) if (f - kv.Value.Frame > 300) s_drop.Add(kv.Key);
            foreach (int id in s_drop) s_holds.Remove(id);
        }

        public static string Describe() => "spawn rule " + KeepOut.Eyes.ToString("0.00") + " m: tracked " + s_holds.Count + ", held " + Held + ", captures " + Captures + ", releases " + Releases + " (" + LastCapture + ")";
    }

    /// Moving a card for one draw (KeepOut, ChoiceZoom): each card has its own NGUI panel (CardTemplate) whose texts are
    /// drawn by root "draw call" objects placed on the panel in LateUpdate - a card moved later, while the camera draws,
    /// would leave its texts behind. Its panels' draw calls are moved the same way, and everything is put back after.
    internal static class CardMove
    {
        public struct Saved { public Transform T; public Vector3 Pos, Scale; public Quaternion Rot; public bool HasRot; }
        static readonly Dictionary<UIPanel, List<UIDrawCall>> s_byPanel = new Dictionary<UIPanel, List<UIDrawCall>>();
        static readonly List<UIPanel> s_panels = new List<UIPanel>();
        static int s_frame = -1;
        static float s_nextPrune;

        static void Index()
        {
            if (s_frame == Time.frameCount) return;
            s_frame = Time.frameCount;
            if (Time.unscaledTime >= s_nextPrune) { s_nextPrune = Time.unscaledTime + 10f; s_byPanel.Clear(); }
            foreach (var l in s_byPanel.Values) l.Clear();
            BetterList<UIDrawCall> all = UIDrawCall.activeList;
            if (all == null) return;
            for (int i = 0; i < all.size; i++)
            {
                UIDrawCall dc = all.buffer[i];
                if (dc == null || dc.manager == null) continue;
                List<UIDrawCall> l;
                if (!s_byPanel.TryGetValue(dc.manager, out l)) s_byPanel[dc.manager] = l = new List<UIDrawCall>();
                l.Add(dc);
            }
        }

        /// The card's texts: the draw calls of the NGUI panels inside it.
        static void Texts(Component c, List<Transform> into)
        {
            Index();
            s_panels.Clear();
            c.GetComponentsInChildren(false, s_panels);
            foreach (UIPanel p in s_panels)
            {
                List<UIDrawCall> l;
                if (p != null && s_byPanel.TryGetValue(p, out l)) foreach (UIDrawCall dc in l) if (dc != null) into.Add(dc.cachedTransform);
            }
        }

        static readonly List<Transform> s_parts = new List<Transform>();
        static readonly List<Transform> s_texts = new List<Transform>();

        /// The nearest point of what a component's own NGUI panels draw (world units).
        public static float NearestTexts(Component c, Vector3 eye)
        {
            s_texts.Clear();
            Texts(c, s_texts);
            float best = float.MaxValue;
            foreach (Transform t in s_texts)
            {
                Renderer r = t != null ? t.GetComponent<Renderer>() : null;
                if (r != null && r.enabled) best = Mathf.Min(best, Vector3.Distance(r.bounds.ClosestPoint(eye), eye));
            }
            return best;
        }

        /// Back along the line of sight from `eye` by the factor k, grown by k (the same picture from the eye).
        public static void Push(Component c, Vector3 eye, float k, List<Saved> saved) => Push(c, eye, k, k, saved);

        /// Back along the line of sight by the factor k, grown by g (g < k: smaller to the eye than the game made it).
        public static void Push(Component c, Vector3 eye, float k, float g, List<Saved> saved)
        {
            s_parts.Clear();
            s_parts.Add(c.transform);
            Texts(c, s_parts);
            foreach (Transform t in s_parts)
            {
                saved.Add(new Saved { T = t, Pos = t.position, Scale = t.localScale });
                t.position = eye + (t.position - eye) * k;
                t.localScale = t.localScale * g;
            }
        }

        /// As Push, its line of sight turned by `turn` about the eye first (the card turned with it: the same picture, higher).
        public static void PushTurned(Component c, Vector3 eye, Quaternion turn, float k, float g, List<Saved> saved)
        {
            s_parts.Clear();
            s_parts.Add(c.transform);
            Texts(c, s_parts);
            foreach (Transform t in s_parts)
            {
                saved.Add(new Saved { T = t, Pos = t.position, Scale = t.localScale, Rot = t.rotation, HasRot = true });
                t.position = eye + turn * ((t.position - eye) * k);
                t.rotation = turn * t.rotation;
                t.localScale = t.localScale * g;
            }
        }

        /// Puts back what was saved from index `from` on (and forgets it).
        public static void RestoreFrom(List<Saved> saved, int from)
        {
            for (int i = saved.Count - 1; i >= from; i--)
            {
                Saved sv = saved[i];
                if (sv.T != null) { sv.T.position = sv.Pos; sv.T.localScale = sv.Scale; if (sv.HasRot) sv.T.rotation = sv.Rot; }
                saved.RemoveAt(i);
            }
        }

        /// Moved by an offset (no growing).
        public static void Shift(Card c, Vector3 offset, List<Saved> saved)
        {
            s_parts.Clear();
            s_parts.Add(c.transform);
            Texts(c, s_parts);
            foreach (Transform t in s_parts)
            {
                saved.Add(new Saved { T = t, Pos = t.position, Scale = t.localScale });
                t.position += offset;
            }
        }

        /// Grown by s about a point (the transforms' positions and scales; a card's texts with it).
        public static void ScaleAbout(Component c, Vector3 pivot, float s, List<Saved> saved, bool texts)
        {
            s_parts.Clear();
            s_parts.Add(c.transform);
            if (texts) Texts(c, s_parts);
            foreach (Transform t in s_parts)
            {
                saved.Add(new Saved { T = t, Pos = t.position, Scale = t.localScale });
                t.position = pivot + (t.position - pivot) * s;
                t.localScale = t.localScale * s;
            }
        }

        /// Not drawn this time: the card's renderers and its texts (put back by the caller after drawing).
        public static void Hide(Component c, List<Renderer> hidden)
        {
            s_parts.Clear();
            Texts(c, s_parts);
            foreach (Renderer r in c.GetComponentsInChildren<Renderer>(false)) if (r.enabled) { r.enabled = false; hidden.Add(r); }
            foreach (Transform t in s_parts)
            {
                Renderer r = t != null ? t.GetComponent<Renderer>() : null;
                if (r != null && r.enabled) { r.enabled = false; hidden.Add(r); }
            }
        }

        public static void Restore(List<Saved> saved)
        {
            for (int i = saved.Count - 1; i >= 0; i--)
            {
                Saved sv = saved[i];
                if (sv.T == null) continue;
                sv.T.position = sv.Pos;
                sv.T.localScale = sv.Scale;
                if (sv.HasRot) sv.T.rotation = sv.Rot;
            }
            saved.Clear();
        }
    }
}

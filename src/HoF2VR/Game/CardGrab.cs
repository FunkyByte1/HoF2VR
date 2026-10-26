using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Grab a card to look at it (hand feel Phase 2, docs/hand-feel-design.md §3.12; docs/interaction-design.md §3.5): in the
    /// inventory and the deck builder, the game offers "Inspect" (its Y button) for the card it has hovered - by touch, or the
    /// stick. A fresh grip with the hand at that card (within 8 cm of the palm or the fingertip) asks the game for it: the
    /// card flies up in front of the player, the game's own close-up. Letting go (the grip under 0.35) is the game's Back: it
    /// goes home. An open hand at such a card gets ready (the fingers curl a little, a tick). Settings.GrabThrow.
    internal class CardGrab : MonoBehaviour
    {
        public static float Reach = 0.08f, Open = 0.3f, ReleaseGrip = 0.35f;
        public static int Grabs, Releases, Readies;
        public static string Last = "-";
        static Hand s_holder;
        static float s_since;
        static readonly bool[] s_hover = new bool[2];
        static readonly float[] s_ready = new float[2];
        static readonly AccessTools.FieldRef<DeckBuilder, DeckBuilderStateMachine> s_dbMachine = AccessTools.FieldRefAccess<DeckBuilder, DeckBuilderStateMachine>("m_stateMachine");

        public static float Ready(Hand h) => s_ready[h.IsLeft ? 1 : 0];
        public static bool Holding => s_holder != null;

        void Awake()
        {
            Diag.StatusExtras["cardGrab"] = () => Diag.Q(Describe());
        }

        /// The card the game would inspect now (its hovered card, while the inventory's or the deck builder's browsing state
        /// offers Inspect), or null.
        static Card InspectableCard(out UISelectable sel)
        {
            sel = null;
            try
            {
                bool offers = false;
                UIInventory inv = UIInventory.Instance;
                if (inv != null && inv.isActiveAndEnabled && inv.ActiveState is UIInventoryState_Category) offers = true;
                DeckBuilder db = DeckBuilder.Instance;
                if (!offers && db != null && db.isActiveAndEnabled)
                {
                    DeckBuilderStateMachine m = s_dbMachine(db);
                    offers = m != null && m.ActiveState is DeckBuilderState_Build;
                }
                if (!offers) return null;
                foreach (SelectionCategory c in new[] { SelectionCategory.Inventory, SelectionCategory.Main, SelectionCategory.Menu })
                {
                    UISelection s = UISelectionManager.GetSelection(c);
                    UISelectable h = s != null ? s.HoveredObject : null;
                    if (h == null) continue;
                    Card card = h.GetComponent<Card>();
                    if (card == null) { CardContainer cc = h.GetComponent<CardContainer>(); if (cc != null && cc.Cards.Count > 0) card = cc.Cards[0]; }
                    if (card != null) { sel = h; return card; }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// The game is showing a card close up (an inventory or deck builder inspect state).
        static bool Inspecting()
        {
            try
            {
                UIInventory inv = UIInventory.Instance;
                if (inv != null && inv.isActiveAndEnabled && inv.ActiveState != null && inv.ActiveState.GetType().Name.StartsWith("UIInventoryState_Inspect", StringComparison.Ordinal)) return true;
                DeckBuilder db = DeckBuilder.Instance;
                if (db != null && db.isActiveAndEnabled)
                {
                    DeckBuilderStateMachine m = s_dbMachine(db);
                    if (m != null && m.ActiveState != null && m.ActiveState.GetType().Name.IndexOf("Inspect", StringComparison.Ordinal) >= 0) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        static bool Near(Hand h, Card card)
        {
            if (!HandWorld.Valid(h) || card == null) return false;
            float r = Reach * (VRRig.I != null ? VRRig.I.Scale : 1f);
            Vector3 c = card.transform.position;
            return Vector3.Distance(HandWorld.Palm(h), c) <= r * 1.5f || Vector3.Distance(HandFeel.Tip(h), c) <= r;
        }

        void Update()
        {
            if (!XRSettings.enabled || !Settings.GrabThrow || CombatVR.Active || VRFade.IsBlack)
            {
                if (s_holder != null) LetGo("off");
                s_hover[0] = s_hover[1] = false;
                s_ready[0] = s_ready[1] = 0f;
                return;
            }
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 1e-3f, 0.1f);
            if (s_holder != null)
            {
                // held: the game's close-up stays while the grip does; opening the hand puts the card back
                if (s_holder.Grip < ReleaseGrip && Time.unscaledTime - s_since > 0.15f) LetGo("let go");
                else if (Time.unscaledTime - s_since > 1.5f && !Inspecting()) { s_holder = null; Last = "the game closed it"; }
                return;
            }
            UISelectable sel;
            Card card = DiceHand.Holder != null ? null : InspectableCard(out sel);
            foreach (Hand h in VRInput.RL)
            {
                int i = h.IsLeft ? 1 : 0;
                bool near = card != null && Near(h, card);
                bool hover = near && h.Grip < Open;
                if (hover && !s_hover[i]) { Readies++; Haptics.Cue(Haptics.CueKind.Tick, h.IsLeft); }
                s_hover[i] = hover;
                s_ready[i] = Mathf.MoveTowards(s_ready[i], hover ? 1f : 0f, dt * 8f);
                if (!near || !h.GripDown) continue;
                card = InspectableCard(out sel);
                if (card == null || sel == null) break;
                UISelection.TryHover(sel);
                VRPad.Press("Y", 4);   // the game's Inspect (its F2 / Y on the nav bar)
                s_holder = h;
                s_since = Time.unscaledTime;
                Grabs++;
                s_ready[0] = s_ready[1] = 0f;
                Last = (h.IsLeft ? "L" : "R") + " took " + card.name.Replace("(Clone)", "");
                Haptics.Cue(Haptics.CueKind.Grab, h.IsLeft);
                Plugin.L.LogInfo("cardgrab: " + Last + " to look at it (the game's Inspect)");
                break;
            }
        }

        static void LetGo(string why)
        {
            Hand h = s_holder;
            s_holder = null;
            if (Inspecting()) VRPad.Press("B", 4);   // the game's Back: the card goes home
            Releases++;
            Last = why;
            if (h != null) Haptics.Cue(Haptics.CueKind.LetGo, h.IsLeft);
            Plugin.L.LogInfo("cardgrab: " + why + " - the card goes back");
        }

        static string Describe()
        {
            UISelectable sel;
            Card c = InspectableCard(out sel);
            return string.Format(CultureInfo.InvariantCulture, "{0} | inspectable {1} | inspecting {2} | ready {3}{4} | grabs {5} releases {6} readies {7} | last {8}",
                s_holder != null ? "held " + (s_holder.IsLeft ? "L" : "R") : "free", c != null ? c.name.Replace("(Clone)", "") : "-", Inspecting(),
                s_hover[1] ? "L" : "", s_hover[0] ? "R" : "", Grabs, Releases, Readies, Last);
        }
    }
}

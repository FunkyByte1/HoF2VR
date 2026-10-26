using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Earned gear and its question (round 5 item 6; the user 2026-10-12: "When I earned gear, I had no way to choose whether
    /// or not to equip it" / "When I gained something like a weapon, it didn't give me options to equip or not equip").
    /// Every way the game hands over a piece of equipment ends in Card.Activate -> EquipmentCard.OnActivate, which asks with
    /// the card zoom (CardZoom): "Yes" / "No" when that slot holds something, "Equip" alone when it is free (or the card is
    /// forced), "Reserve" when it can't be worn. The zoom's controls bar is touch plates (TableBoards.NavPlates). This notes
    /// each question: the way it came (who called Card.Activate: an encounter's "Activate Card" task and its card, a choice
    /// of cards, Thunaer's boon, a fame unlock, a test seam...), what the game asked, and whether the plates offered those
    /// words within 4 s. Status `equipAsk`; log lines "equip question: ...".
    internal static class EquipAsk
    {
        public static int Asked, WithPlates, Without, Direct;
        public static string Last = "-";
        static readonly Dictionary<string, string> s_paths = new Dictionary<string, string>();
        static readonly Dictionary<int, string> s_origin = new Dictionary<int, string>();
        static EquipmentCard s_card;
        static string s_path = "", s_confirm = "", s_cancel = "";
        static float s_at = -1f;
        static bool s_zoomed, s_done = true;

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Card), "Activate", new[] { typeof(Card), typeof(CardActivationConfig), typeof(ICardProcessorOwner), typeof(Action<Card>) }),
                prefix: new HarmonyMethod(typeof(EquipAsk), nameof(ActivatePrefix)));
            h.Patch(AccessTools.Method(typeof(EquipmentCard), "OnActivate"), prefix: new HarmonyMethod(typeof(EquipAsk), nameof(OnActivatePrefix)));
            h.Patch(AccessTools.Method(typeof(CardZoom), "Zoom"), prefix: new HarmonyMethod(typeof(EquipAsk), nameof(ZoomPrefix)));
            Diag.Commands["equipask"] = Command;
            Diag.StatusExtras["equipAsk"] = () => Diag.Q(Asked + " asked, " + WithPlates + " with plates, " + Without + " WITHOUT, " + Direct + " equipped at once | ways: "
                + (s_paths.Count == 0 ? "-" : string.Join("; ", Pairs())) + " | " + Last);
        }

        static string[] Pairs()
        {
            var l = new List<string>();
            foreach (var kv in s_paths) l.Add(kv.Key + " -> " + kv.Value);
            return l.ToArray();
        }

        /// Who hands the card over: the first caller outside the card system.
        static void ActivatePrefix(Card a_card, ICardProcessorOwner a_owner)
        {
            try
            {
                if (!(a_card is EquipmentCard)) return;
                string who = "?";
                var st = new StackTrace(1, false);
                for (int i = 0; i < st.FrameCount && i < 24; i++)
                {
                    MethodBase m = st.GetFrame(i).GetMethod();
                    Type t = m != null ? m.DeclaringType : null;
                    if (t == null) continue;
                    while (t.Name.StartsWith("<") && t.DeclaringType != null) t = t.DeclaringType;
                    if (t == typeof(Card) || t == typeof(EquipmentCard) || t == typeof(EquipAsk) || t.Name == "CardProcessor") continue;
                    who = Describe(t, m);
                    break;
                }
                string owner = a_owner as UnityEngine.Object != null ? ((UnityEngine.Object)a_owner).name.Replace("(Clone)", "") : a_owner != null ? a_owner.GetType().Name : "-";
                s_origin[a_card.GetInstanceID()] = who + " (owner " + owner + ")";
            }
            catch (Exception e) { Plugin.L.LogWarning("equipAsk: " + e.Message); }
        }

        static string Describe(Type t, MethodBase m)
        {
            switch (t.Name)
            {
                case "AddCardToEncounterContainerTask": return "an encounter's Activate Card task";
                case "AdventureChoice": return "a choice of cards";
                case "ThunaersBoonTask": return "Thunaer's boon";
                case "PlayerFameUnlockHandler": return "a fame unlock";
                case "Map": return "the map";
                case "CombatTask": return "a fight";
                case "TwitchGiftHandler": case "TwitchDealHandler": return "Twitch";
            }
            if (t.Namespace == "HoF2VR") return "a test seam (" + t.Name + ")";
            return t.Name + "." + m.Name;
        }

        static void OnActivatePrefix(EquipmentCard __instance)
        {
            try
            {
                Finish();
                s_card = __instance;
                s_why = Why(__instance);
                string o;
                s_path = s_origin.TryGetValue(__instance.GetInstanceID(), out o) ? o : "?";
                s_origin.Remove(__instance.GetInstanceID());
                s_confirm = s_cancel = "";
                s_zoomed = false;
                s_done = false;
                s_at = Time.unscaledTime;
                Asked++;
            }
            catch (Exception e) { Plugin.L.LogWarning("equipAsk: " + e.Message); }
        }

        static void ZoomPrefix(CardZoom.ZoomData a_data)
        {
            try
            {
                if (s_done || a_data == null || a_data.Card == null || a_data.Card != s_card) return;
                s_zoomed = true;
                s_confirm = Text(a_data.ConfirmText);
                s_cancel = a_data.CancelAction != null ? Text(a_data.CancelText) : "";
            }
            catch (Exception e) { Plugin.L.LogWarning("equipAsk: " + e.Message); }
        }

        static string Text(IStringLibraryReader r)
        {
            if (r == null) return "";
            try { return r.GetValue(UIManager.StringLibrary) ?? ""; }
            catch (Exception) { try { return r.GetValue(null) ?? ""; } catch (Exception) { return "?"; } }
        }

        /// Once a frame (Diag.Update): did the plates offer what the game asked?
        public static void Tick()
        {
            if (s_done || s_card == null) return;
            float t = Time.unscaledTime - s_at;
            if (!s_zoomed)
            {
                if (t > 1.5f) Finish();
                return;
            }
            string nav = TableBoards.NavShown ?? "";
            bool ok = nav.Length > 0 && Has(nav, s_confirm) && (s_cancel.Length == 0 || Has(nav, s_cancel));
            if (ok && t > 0.3f)
            {
                WithPlates++;
                Note(true, nav);
            }
            else if (t > 4f)
            {
                Without++;
                Note(false, nav);
            }
        }

        static bool Has(string nav, string word) => word.Length == 0 || nav.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;

        static void Note(bool ok, string nav)
        {
            s_done = true;
            string asked = s_confirm + (s_cancel.Length > 0 ? " / " + s_cancel : "");
            Last = Name(s_card) + " via " + s_path + ": the game asks '" + asked + "', plates " + (ok ? "'" + nav + "'" : "MISSING (shown: '" + nav + "')");
            s_paths[Short(s_path)] = ok ? "plates " + asked : "NO PLATES for " + asked;
            if (ok) Plugin.L.LogInfo("equip question: " + Last);
            else Plugin.L.LogWarning("equip question: " + Last);
            s_card = null;
        }

        /// The card was activated without a question (already shown in the zoom with the slot free, or reserved).
        static void Finish()
        {
            if (s_done || s_card == null) { s_done = true; return; }
            if (!s_zoomed)
            {
                Direct++;
                Last = Name(s_card) + " via " + s_path + ": no question - " + s_why + " (the game's own rule)";
                s_paths[Short(s_path)] = "no question: " + s_why;
                Plugin.L.LogInfo("equip question: " + Last);
                s_done = true;
                s_card = null;
            }
            else
            {
                Without++;
                Note(false, TableBoards.NavShown ?? "");
            }
        }

        static string s_why = "";

        /// Why the game may hand a card over without asking (EquipmentCard.OnActivate): already shown in the zoom (a
        /// purchase or a choice confirmed there) with its slot free, or it can't be worn (reserved).
        static string Why(EquipmentCard c)
        {
            try
            {
                Player p = Player.Instance;
                if (p == null) return "?";
                bool shown = UI3D.Instance != null && c.Container == UI3D.Instance.CardZoom.Container;
                if (!Inventory.CanEquip(p.InventoryData, c)) return "it can't be worn now (reserved)" + (shown ? ", already shown" : "");
                bool free = Inventory.GetEquippedCard(p.InventoryData, c.EquipmentCategory) == null;
                return (free ? "its slot (" + c.EquipmentCategory + ") was free" : "its slot (" + c.EquipmentCategory + ") was taken") + (shown ? ", already shown in the zoom" : "");
            }
            catch (Exception e) { return "? " + e.Message; }
        }

        /// `equipask occupied`: the cards of the open choice (a shop's row...) whose slot is taken - buying one of those
        /// must ask (tests).
        static string Command(string[] a)
        {
            if (a.Length > 1 && a[1] == "occupied")
            {
                CardChoice ch = UI3D.Instance != null ? UI3D.Instance.CardChoice : null;
                Player p = Player.Instance;
                if (ch == null || ch.Container == null || p == null) return "none";
                var l = new List<string>();
                foreach (Card c in ch.Container.Cards)
                {
                    var e = c as EquipmentCard;
                    if (e == null || !Inventory.CanEquip(p.InventoryData, e) || Inventory.GetEquippedCard(p.InventoryData, e.EquipmentCategory) == null) continue;
                    l.Add(Name(c));
                }
                return l.Count > 0 ? string.Join(" ", l.ToArray()) : "none";
            }
            return Asked + " asked | " + Last;
        }

        static string Short(string path)
        {
            int i = path.IndexOf(" (owner", StringComparison.Ordinal);
            string owner = i >= 0 ? path.Substring(i + 7).TrimEnd(')').Trim() : "";
            return (i >= 0 ? path.Substring(0, i) : path) + (owner.Length > 0 && owner != "-" ? " [" + owner + "]" : "");
        }

        static string Name(Card c) => c != null ? c.name.Replace("(Clone)", "") : "-";
    }
}

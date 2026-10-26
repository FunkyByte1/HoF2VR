using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Development shortcut (user 2026-10-11: "make development faster"; off for a shipping version, Dev.JumpMenu): jump
    /// straight to the table, into a fight or into an encounter instead of playing up to it.
    ///  * In VR: hold B + Y together for 1.5 s (DevChord) - a card lists the jumps: A = a fight, X = an encounter,
    ///    Y = the next fight / encounter on the list (the card shows which), B = close. At the title A continues to the
    ///    table; in a fight A wins it (back to the table).
    ///  * Test seams: `jump list [filter]`, `jump fight [name]`, `jump encounter [name]`, `jump table`, `jump win`.
    /// Encounters are the game's own: the card is made from its prefab and activated the way the map does it (the
    /// encounter runs as if met, then the map gets the focus back); nothing in the save is touched until the game's own
    /// encounter flow saves as usual.
    internal static class DevJump
    {
        public static string Last = "-";
        static List<Card> s_fights, s_others;
        static int s_fightIx, s_otherIx;
        static readonly FieldInfo s_sting = typeof(EncounterCard).GetField("m_sting", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Register()
        {
            Diag.StatusExtras["devJump"] = () => Diag.Q((s_menu ? "open" : "closed") + ", jumps " + Jumps + ", last " + Last);
            // `envstate [name|Type]`: the arena's lighting states (the game's EnvironmentStateManager, switched as its debug menu
            // does): list them, or change to one by name or by type (Day, Night, Sunset...). For pictures (2026-10-19 v3: a true
            // night arena); test mode or the dev profile only.
            Diag.Commands["envstate"] = a =>
            {
                if (!Scenarios.SeamsAllowed) return "test mode or the dev profile only";
                var m = Defiant.EnvironmentalStates.EnvironmentStateManager.Instance;
                if (m == null) return "no EnvironmentStateManager";
                var sb = new System.Text.StringBuilder();
                if (a.Length > 1)
                {
                    string w = string.Join(" ", a, 1, a.Length - 1);
                    bool named = false;
                    if (m.EnvironmentStates != null) foreach (var st in m.EnvironmentStates) if (st != null && st.Name == w) named = true;
                    if (m.EnvironmentStateScenes != null) foreach (var st in m.EnvironmentStateScenes) if (st != null && st.Name == w) named = true;
                    if (named) m.ChangeState(w);
                    else
                    {
                        try { m.ChangeState((Defiant.EnvironmentalStates.EnvironmentStateType)Enum.Parse(typeof(Defiant.EnvironmentalStates.EnvironmentStateType), w, true)); }
                        catch (Exception) { return "no state or type '" + w + "'"; }
                    }
                    sb.Append("changing to ").Append(w).Append("; ");
                }
                sb.Append("now ").Append(m.CurrentState != null ? m.CurrentState.Name + " (" + m.CurrentState.Type + ")" : "-")
                  .Append(", scene ").Append(m.CurrentStateScene != null ? m.CurrentStateScene.Name + " (" + m.CurrentStateScene.Type + ")" : "-")
                  .Append(", loading ").Append(m.IsLoadingAdditiveState).Append(" | states:");
                if (m.EnvironmentStates != null) foreach (var st in m.EnvironmentStates) if (st != null) sb.Append(' ').Append(st.Name).Append(" (").Append(st.Type).Append(')');
                sb.Append(" | scenes:");
                if (m.EnvironmentStateScenes != null) foreach (var st in m.EnvironmentStateScenes) if (st != null) sb.Append(' ').Append(st.Name).Append(" (").Append(st.Type).Append(')');
                return sb.ToString();
            };
            Diag.Commands["jump"] = a =>
            {
                string op = a.Length > 1 ? a[1] : "";
                string arg = a.Length > 2 ? string.Join(" ", a, 2, a.Length - 2) : "";
                switch (op)
                {
                    case "list": return List(arg);
                    case "fight": return Encounter(arg, true);
                    case "encounter": return Encounter(arg, false);
                    case "table": return Table();
                    case "win": return Win();
                    default: return "jump list [filter] | fight [name] | encounter [name] | table | win (" + Last + ")";
                }
            };
        }

        static int s_loadedReset = -1;

        static void Load()
        {
            // the game's reset (quit to title, the Scenarios' profile switch) loads its cards anew: the lists then too
            if (s_fights != null && s_loadedReset == ResetGuard.Resets) return;
            s_loadedReset = ResetGuard.Resets;
            s_fights = new List<Card>();
            s_others = new List<Card>();
            AssetManager am = AssetManager.Instance;
            if (am == null || am.Cards == null) return;
            foreach (Card c in am.Cards.Assets)
            {
                EncounterCard e = c as EncounterCard;
                if (e == null) continue;
                string st = s_sting != null ? Convert.ToString(s_sting.GetValue(e)) : "";
                if (st == "Combat") s_fights.Add(c); else if (st != "Boss") s_others.Add(c);
            }
            s_fights.Sort((x, y) => string.CompareOrdinal(x.name, y.name));
            s_others.Sort((x, y) => string.CompareOrdinal(x.name, y.name));
            Plugin.L.LogInfo("DevJump: " + s_fights.Count + " fights, " + s_others.Count + " other encounters");
        }

        static string List(string filter)
        {
            Load();
            var sb = new System.Text.StringBuilder("DevJump encounters (" + filter + "):\n  fights:");
            foreach (Card c in s_fights) if (filter.Length == 0 || c.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(' ').Append(c.name);
            sb.Append("\n  others:");
            foreach (Card c in s_others) if (filter.Length == 0 || c.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(' ').Append(c.name);
            Plugin.L.LogInfo(sb.ToString());
            return s_fights.Count + " fights, " + s_others.Count + " others (see log)";
        }

        public static string Selected(bool fight)
        {
            Load();
            List<Card> l = fight ? s_fights : s_others;
            if (l == null || l.Count == 0) return "-";
            int ix = fight ? s_fightIx : s_otherIx;
            return Pretty(l[((ix % l.Count) + l.Count) % l.Count].name);
        }

        public static void Next(bool fight)
        {
            if (fight) s_fightIx++; else s_otherIx++;
        }

        static string Pretty(string n) => n.StartsWith("Card_EN_") ? n.Substring(8) : n;

        /// An encounter (a fight when `fight`) by name, or the selected one of the list.
        /// The map is ready for a jump: the map's own menu in front, nothing over it. (2026-10-12, bug from the combat worker:
        /// from the first_fight snapshot the character choice was still open on the map; a jumped fight then crashed in the
        /// game's fight intro - GameState_Level_In.AnimateCards moved hero cards that were not dealt yet - and hung in
        /// Level_In behind the mist.)
        public static bool MapReady(out string why)
        {
            why = null;
            if (GameWatch.State != "GameState_Map" || Map.Instance == null) { why = "only on the dungeon map (now " + GameWatch.State + ")"; return false; }
            MenuManager mm = MenuManager.Instance;
            if (mm == null) return true;
            try
            {
                if (mm.Paused) { why = "not while the game is paused"; return false; }
                foreach (MenuManager.StackPriority p in new[] { MenuManager.StackPriority.Secondary, MenuManager.StackPriority.Dialogue, MenuManager.StackPriority.Failure })
                {
                    Menu over = mm.CurrentMenu(p);
                    if (over != null) { why = "not while '" + over.name.Replace("(Clone)", "") + "' is open: close it first"; return false; }
                }
                Menu cur = mm.CurrentMenu(MenuManager.StackPriority.Primary);
                string n = cur != null ? cur.name.Replace("(Clone)", "") : "";
                if (n != "Map") { why = "only once the map is ready (now '" + (n.Length > 0 ? n : "-") + "': finish it first)"; return false; }
            }
            catch (Exception) { }
            return true;
        }

        /// The game's fight intro (GameState_Level_In.AnimateCards) takes the hero's equipped cards and the health card out of
        /// the containers they say they are in. In a run whose cards are not dealt yet (the first fight of a new run: the
        /// first_fight snapshot) they are not in them, the intro crashes and the game hangs behind the mist: a jumped fight is
        /// refused there.
        static bool HeroCardsDealt(out string why)
        {
            why = null;
            try
            {
                Player p = Player.Instance;
                DeckManager dm = DeckManager.Instance;
                if (p == null || dm == null) return true;
                foreach (Equipment.EquipmentType t in new[] { Equipment.EquipmentType.Weapon, Equipment.EquipmentType.Shield, Equipment.EquipmentType.Armour,
                                                              Equipment.EquipmentType.Helm, Equipment.EquipmentType.Artifact })
                {
                    EquipmentCard c = Inventory.GetEquippedCard(p.InventoryData, t);
                    if (c != null && c.Container != null && !c.Container.Cards.Contains(c))
                    {
                        why = "not yet in this game: the hero's cards are not dealt (play the first encounter normally)";
                        return false;
                    }
                }
                StatHand sh = dm.StatHand;
                Card hc = sh != null ? sh.HealthCard : null;
                if (hc != null && sh.Container != null && !sh.Container.Cards.Contains(hc))
                {
                    why = "not yet in this game: the health card is not dealt (play the first encounter normally)";
                    return false;
                }
            }
            catch (Exception e) { why = "the hero's cards can't be checked (" + e.Message + ")"; return false; }
            return true;
        }

        public static string Encounter(string name, bool fight)
        {
            string why;
            if (!MapReady(out why)) { Plugin.L.LogInfo("DevJump: refused - " + why); return why; }
            if (fight && !HeroCardsDealt(out why)) { Plugin.L.LogInfo("DevJump: refused - fights " + why); return "fights " + why; }
            Load();
            List<Card> l = fight ? s_fights : s_others;
            if (l == null || l.Count == 0) return "no encounters known";
            Card prefab = null;
            if (name.Length > 0) prefab = l.FirstOrDefault(c => c.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) ?? s_others.Concat(s_fights).FirstOrDefault(c => c.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            else prefab = l[(((fight ? s_fightIx : s_otherIx) % l.Count) + l.Count) % l.Count];
            if (prefab == null) return "no encounter like '" + name + "'";
            try
            {
                Card card = Card.Create(prefab, DeckManager.Instance.OffscreenContainer, true);
                Card.Activate(card, CardManager.Instance, c => { if (Map.Instance != null) Map.Instance.Focus(); });
                Last = (fight ? "fight " : "encounter ") + prefab.name;
                Plugin.L.LogInfo("DevJump: " + Last);
                return "started " + prefab.name;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("DevJump: " + prefab.name + ": " + e);
                return "failed: " + e.Message;
            }
        }

        /// To the table: Continue at the title; win the fight in a fight (the way out of it is still the player's).
        public static string Table()
        {
            string st = GameWatch.State;
            if (st == "GameState_StartScreen")
            {
                Last = "continue";
                return Diag.I != null ? Diag.I.Run("ui click ContinueButton") : "no diag";
            }
            if (st == "GameState_Level_Play") return Win();
            return "already at the table or on the way (" + st + ")";
        }

        static string Win()
        {
            if (GameWatch.State != "GameState_Level_Play" || CombatEncounter.Instance == null) return "not in a fight";
            CombatEncounter.Instance.Complete(null);
            Last = "win";
            return "fight won";
        }

        // ---- the VR menu ----------------------------------------------------------------------------------------------
        static bool s_menu;
        static float s_noteAt, s_devAskedAt = -1f;
        static bool s_menuOpenedThisPress;
        public static int Jumps;

        /// Each frame (VRGamepad): B + Y held together for 1.5 s (DevChord) opens the card; while it is open the face buttons
        /// choose. Returns true while the menu owns the face buttons (the game must not see them).
        public static bool Step(float chordHeld, bool a, bool b, bool x, bool y, bool trigger = false)
        {
            if (!Settings.JumpMenu || !XRSettings.enabled) { s_menu = false; return false; }
            if (chordHeld > 0f)
            {
                if (!s_menu && !s_menuOpenedThisPress && chordHeld >= DevChord.JumpHold)
                {
                    s_menuOpenedThisPress = true;
                    Haptics.Pulse(true, 0.4f, 0.04f);
                    // with the Scenarios menu: the pause menu opens on its Dev tab; where there is no pause menu (the title, the
                    // waiting space) the jump card below comes instead
                    if (Scenarios.Enabled && VRSettings.RequestDev()) s_devAskedAt = Time.unscaledTime;
                    else { s_menu = true; Note(); }
                }
                if (s_menu) return true;   // the chord is still held: nothing chooses yet
            }
            else s_menuOpenedThisPress = false;
            if (s_devAskedAt > 0f && !VRSettings.DevWaiting)
            {
                if (!VRSettings.PageOpen) { s_menu = true; Note(); }
                s_devAskedAt = -1f;
            }
            if (!s_menu) return false;
            if (a || b || x || y || trigger) DevChord.Consume();
            string res = null;
            if (trigger && !GameWatch.InCombatPlay && HandCheck.I != null)
            {
                // the one-minute hand check (glove fit, fingertips, vibration test), wherever the player stands
                s_menu = false;
                if (VRHelp.I != null) VRHelp.I.Hide();
                HandCheck.I.Begin();
                return true;
            }
            if (a) res = GameWatch.State == "GameState_Map" ? Encounter("", true) : Table();
            else if (x) res = Encounter("", false);
            else if (y) { Next(GameWatch.State == "GameState_Map"); Note(); return true; }
            else if (b) res = "closed";
            if (res != null)
            {
                s_menu = false;
                Jumps++;
                if (VRHelp.I != null) VRHelp.I.Show("Dev jump: " + res, 2f);
                Haptics.Pulse(false, 0.4f, 0.05f);
                Haptics.Pulse(true, 0.4f, 0.05f);
            }
            else if (Time.unscaledTime >= s_noteAt) Note();
            return true;
        }

        static void Note()
        {
            s_noteAt = Time.unscaledTime + 1.5f;
            if (VRHelp.I == null) return;
            string st = GameWatch.State;
            string check = "\nTrigger|Check my hands (one minute)";
            string notReady;
            bool mapState = st == "GameState_Map";
            if (mapState && !MapReady(out notReady))
            {
                VRHelp.I.Show("DEV JUMP\n\nJumps " + notReady + "." + check + "\nB|Close\n\n(B + Y held half a second: mark a bug)", 2f);
                return;
            }
            string text = mapState
                ? "DEV JUMP\n\nA|Fight: " + Selected(true) + "\nX|Encounter: " + Selected(false) + "\nY|Next on the lists" + check + "\nB|Close"
                : st == "GameState_Level_Play" ? "DEV JUMP\n\nA|Win this fight\nB|Close"
                : st == "GameState_StartScreen" ? "DEV JUMP\n\nA|Continue to the table" + check + "\nB|Close"
                : "DEV JUMP\n\n(jumps only on the map, at the title or in a fight)" + check + "\nB|Close";
            VRHelp.I.Show(text, 2f);
        }
    }
}

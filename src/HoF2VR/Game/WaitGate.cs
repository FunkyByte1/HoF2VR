using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The waiting space waits for the player (user 2026-10-11 afternoon: "Once the place I'm going is loaded, but before
    /// I'm teleported there, can we make it so I have to press A first?").
    ///  * Gate: when Places would release its hold (the next place is ready) and the waiting space is up, the hold stays
    ///    and the board (WaitBoard) says the place is ready; A (right controller) or X (left) lets the player go. The
    ///    button is kept from the game until it is released, and no pad input reaches the game while the gate waits
    ///    (the player can't see that place yet). Touch is not offered: the hands are not drawn in the waiting space.
    ///  * The fight waits too ("I don't have time to read it before getting attacked"): GameState_Level_In's last step
    ///    waits for the Dialogue stack to empty before it starts the combat music, input and Level_Play (the game's own
    ///    pausing controls tutorial uses that wait); a transpiler adds one "dialogue" while the player is still in the
    ///    waiting space and for FightGrace seconds after A, so the enemies stand where they spawned until then.
    ///  * The loading spinner (MenuManager.LoadSpinner, the turning dragon in the screen's corner) never shows in VR: the
    ///    waiting space has its own progress bar, and on the panel it hung in mid-air.
    /// Settings Comfort.PressToContinue (gate + fight hold) and Comfort.WaitingBoard (the board).
    internal class WaitGate : MonoBehaviour
    {
        public static WaitGate I;
        /// The next place is ready and the gate waits for the player's A / X.
        public static bool Waiting { get; private set; }
        public static float ReadySince = -10f;
        static float s_goAt = -10f;
        static bool s_swallow;
        static bool s_testGo;
        public static int Gates, Goes, FightHolds;
        public static string Key = "";
        /// After "go" the fight waits this long (the mist clears in ~0.4 s, then a moment to look round).
        public static float FightGrace = 1.2f;
        /// A press counts only this long after the prompt appeared (a press already under way is not a "go").
        const float PressDelay = 0.3f;

        public static void Apply(Harmony h)
        {
            try
            {
                MethodInfo moveNext = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(GameState_Level_In), "LevelTransition"));
                h.Patch(moveNext, transpiler: new HarmonyMethod(typeof(WaitGate), nameof(FightHoldTranspiler)));
            }
            catch (Exception e) { Plugin.L.LogWarning("gate: fight hold patch failed (fights start as soon as they are revealed): " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(Transition), "Show"), prefix: new HarmonyMethod(typeof(WaitGate), nameof(SpinnerShowPrefix)));
                h.Patch(AccessTools.Method(typeof(Transition), "SnapShow"), prefix: new HarmonyMethod(typeof(WaitGate), nameof(SpinnerSnapPrefix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("gate: spinner patch failed: " + e.Message); }
        }

        void Awake()
        {
            I = this;
            Diag.StatusExtras["gate"] = () => Diag.Q((Waiting ? "waiting " + (Time.unscaledTime - ReadySince).ToString("0.0") + " s" : "idle") +
                                                     " gates " + Gates + " goes " + Goes + " fightHolds " + FightHolds + (s_fightHeld ? " fight-held" : "") + " key " + Key);
            // `gate` = status; `gate go` = the player's A (tests)
            Diag.Commands["gate"] = a =>
            {
                if (a.Length > 1 && a[1] == "go") { s_testGo = true; return "go requested (waiting " + Waiting + ")"; }
                // `gate auto <s>`: test runs go on by themselves this long after a place is ready (0 = off)
                if (a.Length > 2 && a[1] == "auto") s_auto = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                if (a.Length > 2 && a[1] == "grace") FightGrace = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                // (B1: the encounter card held out at the table counts as a wait for the player - scripts answer it with A or `gate go`)
                return "gate " + (Waiting ? "waiting" : FightCard.Offered ? "waiting (the encounter card at the table)" : "idle") + " setting " + Settings.PressToContinue + " gates " + Gates + " goes " + Goes +
                       " fight holds " + FightHolds + " held now " + s_fightHeld + " grace " + FightGrace + " | " + Title + " | " + Subtitle;
            };
        }

        static bool Active() => XRSettings.enabled && Settings.PressToContinue && Settings.WaitingSpace;

        // ---- the gate ------------------------------------------------------------------------------------

        /// Places.ReleaseHold: may the place hold be released now? false = the player is still in the waiting space
        /// and the gate waits for them.
        public static bool MayRelease()
        {
            if (!Active())
            {
                if (Waiting) Close("setting off");
                return true;
            }
            if (FightFlow.PreGone)
            {
                // the A for this fight was given while its arena was held: this is its reveal, no second gate; the fight's grace
                // counts from now
                FightFlow.PreGone = false;
                s_goAt = Time.unscaledTime;
                LastGoAt = s_goAt;
                s_swallow = false;
                // (item 30: the fight card touched once loaded - this is the reveal of its straight way in)
                FightFlow.DirectDone("the fight is revealed");
                return true;
            }
            if (!Waiting)
            {
                // nothing to wait for when the space was never shown (a quick hop) or is already covered for good
                if (!Limbo.On) { s_testGo = false; return true; }
                Open();
                return false;
            }
            if (s_testGo || GoPressed()) { Go(s_testGo ? "test" : "button"); return true; }
            if (s_auto > 0f && TestMode.Enabled && Time.unscaledTime - ReadySince >= s_auto) { Go("test auto"); return true; }
            return false;
        }

        static float s_auto;
        /// Test runs: `gate auto <s>` (FightCard touches the table's encounter card after as long).
        public static float AutoSeconds => s_auto;

        /// A test's `gate go` that nothing else took yet: FightCard takes it as the touch at the table.
        /// A pending test go dropped (FightCard: one from before the fight began); true if there was one.
        public static bool DropTestGo()
        {
            if (!s_testGo || Waiting) return false;
            s_testGo = false;
            return true;
        }

        public static bool TakeTestGo()
        {
            if (!s_testGo || Waiting) return false;
            s_testGo = false;
            return true;
        }

        static void Open()
        {
            Waiting = true;
            ReadySince = Time.unscaledTime;
            Key = LoadProgress.Key;
            Gates++;
            s_testGo = false;
            LoadProgress.Ready();
            Describe();
            // a soft double tick: it's ready (the board says so too)
            Haptics.Event("ready", false, 0.25f, 0.03f, 2, 0.12f);
            Haptics.Event("ready", true, 0.25f, 0.03f, 2, 0.12f);
            Plugin.L.LogInfo("gate: '" + Key + "' is ready at " + Diag.Clock() + " - waiting for A (" + Title + (Subtitle.Length > 0 ? " / " + Subtitle : "") + ")");
        }

        static bool GoPressed()
        {
            if (Time.unscaledTime - ReadySince < PressDelay) return false;
            if (s_touchGo) { s_touchGo = false; TouchGoes++; s_goLeft = s_touchLeft; return true; }
            if (VRInput.R.ADown) { s_goLeft = false; return true; }
            if (VRInput.L.ADown) { s_goLeft = true; return true; }
            return false;
        }

        static bool s_touchGo, s_touchLeft, s_goLeft;
        /// When the player last said go (A or the Continue plate) - LoadProgress times the next wait from it.
        public static float LastGoAt = -100f;
        public static int TouchGoes;
        /// The waiting space's Continue plate (GoPlates): as A, only while the gate waits (the place is ready).
        public static void TouchGo() { TouchGo(false); }
        public static void TouchGo(bool left) { if (Waiting) { s_touchGo = true; s_touchLeft = left; } }

        static void Go(string why)
        {
            Plugin.L.LogInfo("gate: go (" + why + ") after " + (Time.unscaledTime - ReadySince).ToString("0.0") + " s at " + Diag.Clock());
            Waiting = false;
            s_testGo = false;
            s_goAt = Time.unscaledTime;
            LastGoAt = s_goAt;
            s_swallow = true;
            Goes++;
            LoadProgress.ReadyDone();
            // (on the hand that said go: A right, X left, or the hand that touched Continue - M2, 2026-10-17: it was always the
            // right hand's)
            Haptics.Event("go", s_goLeft && why == "button", 0.45f, 0.04f);
        }

        /// Places starts a different place change while the gate waits (the game moved on behind the waiting space): the
        /// gate opens again once that place is ready, with its own description.
        public static void Retarget(string to)
        {
            if (Waiting) Close("the destination changed to " + to);
        }

        static void Close(string why)
        {
            if (!Waiting) return;
            Plugin.L.LogInfo("gate: closed without a go (" + why + ")");
            Waiting = false;
            s_testGo = false;
            LoadProgress.ReadyDone();
        }

        /// No pad input reaches the game while the gate waits, nor the A / X that said "go" until it is released.
        public static bool BlocksPad => Waiting || s_swallow;

        void Update()
        {
            FightFlow.Tick();
            // a held arena (FightFlow): the gate opens when its data is in, and its A starts the activation behind the mist
            if (FightFlow.Held && Active() && Limbo.On)
            {
                if (!Waiting && FightFlow.Ready && !FightFlow.PreGone) Open();
                else if (Waiting)
                {
                    // (the reason from the first look: GoPressed consumes a touch, so asking it again said "test auto" for a
                    // player's own Continue - seen in the user's session of 2026-10-17)
                    bool pressed = !s_testGo && GoPressed();
                    if (s_testGo || pressed || (s_auto > 0f && TestMode.Enabled && Time.unscaledTime - ReadySince >= s_auto))
                    {
                        Go(s_testGo ? "test" : pressed ? "button" : "test auto");
                        FightFlow.Go("the player's A");
                    }
                }
            }
            if (Waiting && (VRFade.I == null || !VRFade.I.IsHeldBy(Places.HoldOwner))) Close("the hold ended");
            if (s_swallow && Time.unscaledTime - s_goAt > 0.1f && !VRInput.R.A && !VRInput.L.A) s_swallow = false;
            if (s_swallow && Time.unscaledTime - s_goAt > 5f) s_swallow = false;
            if (Waiting && Time.frameCount % 45 == 0) Describe();   // the encounter and its enemies can still change
        }

        // ---- the fight waits in Level_In while the player is in the waiting space ---------------------------

        static bool s_fightHeld;
        static float s_heldSince = -1f;
        static bool s_deadlockWarned;

        /// Replaces `dialogueStack.Stack.Count` in GameState_Level_In.LevelTransition's last wait.
        public static int DialogueCount(List<Menu> stack)
        {
            int n = stack != null ? stack.Count : 0;
            bool hold = HoldFight();
            if (hold != s_fightHeld)
            {
                s_fightHeld = hold;
                if (hold) { FightHolds++; s_heldSince = Time.unscaledTime; Plugin.L.LogInfo("gate: the fight waits in Level_In until the player arrives (" + Diag.Clock() + ")"); }
                else Plugin.L.LogInfo("gate: the fight starts (" + Diag.Clock() + ", held " + (Time.unscaledTime - s_heldSince).ToString("0.0") + " s)");
            }
            return hold ? n + 1 : n;
        }

        static bool HoldFight()
        {
            if (!Active()) return false;
            if (Waiting) return true;
            float now = Time.unscaledTime;
            // (the waiting space up, or - item 30 - the straight way in from the fight card, in the place change's dark)
            if (VRFade.I != null && VRFade.I.IsHeldBy(Places.HoldOwner) && (Limbo.On || FightFlow.Direct))
            {
                // never a deadlock: the place hold ends at the latest 6 s after the arrival (Places' tunnel timeout) and the
                // gate then waits for A; a hold that still lasts long after that is something else - let the game go on
                if (s_fightHeld && now - s_heldSince > 45f)
                {
                    if (!s_deadlockWarned) { s_deadlockWarned = true; Plugin.L.LogWarning("gate: the place hold has lasted 45 s with the fight waiting - letting the fight start"); }
                    return false;
                }
                return true;
            }
            return now - s_goAt < FightGrace;
        }

        static IEnumerable<CodeInstruction> FightHoldTranspiler(IEnumerable<CodeInstruction> code)
        {
            MethodInfo getStack = AccessTools.PropertyGetter(typeof(MenuStack), "Stack");
            MethodInfo getCount = AccessTools.PropertyGetter(typeof(List<Menu>), "Count");
            MethodInfo ours = AccessTools.Method(typeof(WaitGate), nameof(DialogueCount));
            List<CodeInstruction> list = code.ToList();
            int n = 0;
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if (!list[i].Calls(getStack) || !list[i + 1].Calls(getCount)) continue;
                list[i + 1].opcode = OpCodes.Call;
                list[i + 1].operand = ours;
                n++;
            }
            if (n != 1) Plugin.L.LogWarning("gate: the fight hold transpiler replaced " + n + " dialogue-stack counts (expected 1)");
            else Plugin.L.LogInfo("gate: fights wait in Level_In until the player leaves the waiting space");
            return list;
        }

        // ---- the loading spinner -----------------------------------------------------------------------

        static bool IsSpinner(Transition t)
        {
            if (!XRSettings.enabled) return false;
            MenuManager mm = MenuManager.Instance;
            return mm != null && t == mm.LoadSpinner;
        }

        static bool SpinnerShowPrefix(Transition __instance, ref float __result)
        {
            if (!IsSpinner(__instance)) return true;
            __result = 0f;
            return false;
        }

        static bool SpinnerSnapPrefix(Transition __instance) => !IsSpinner(__instance);

        // ---- where the player is going (the board's text) -----------------------------------------------

        public static string Title = "", Subtitle = "", Details = "";

        /// Fills Title / Subtitle / Details for the current wait (LoadProgress.Key; WaitBoard reads them).
        public static void Describe()
        {
            string key = LoadProgress.Key;
            if (key.Length == 0) key = Key;
            string b = key.Split('+')[0];
            string sub = "", det = "";
            string title;
            bool death = false;
            switch (b)
            {
                case "boot": title = "Hand of Fate 2"; sub = "The game is starting"; break;
                case "title_table": title = "To the Dealer's table"; sub = ChallengeName(); break;
                case "fight_in": title = "Into battle"; sub = EncounterName(); det = Enemies(); break;
                case "fight_out":
                    title = "Back to the Dealer's table"; sub = VRUI.EndRunShowing() ? "" : EncounterName();
                    string dt, ds;
                    if (VRUI.EndRunShowing() && VRUI.FailureText(out dt, out ds)) { title = Clean(dt); sub = Clean(ds); det = "Back to the Dealer's table"; death = true; }
                    break;
                case "camp_in": title = "To the camp"; break;
                case "camp_out": title = "Back to the Dealer's table"; break;
                case "boss_in": title = "The final battle"; sub = EncounterName(); break;
                case "boss_out": title = "Back to the Dealer's table"; break;
                case "table_swap": title = "Changing tables"; break;
                default: title = "On the way"; break;
            }
            Title = title;
            Subtitle = sub ?? "";
            Details = det ?? "";
            IsDeath = death;
        }

        /// The board shows the game's death text (Level_Out after a death).
        public static bool IsDeath;

        public static bool IsFight => LoadProgress.Key.StartsWith("fight_in") || LoadProgress.Key.StartsWith("boss_in") || Key.StartsWith("fight_in") || Key.StartsWith("boss_in");

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "*NONE*") return "";
            // NGUI colour / style tags ([ff0000], [-], [b]) don't belong on the board
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '[')
                {
                    int e = s.IndexOf(']', i);
                    if (e > i && e - i <= 9) { i = e; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString().Trim();
        }

        static string EncounterName()
        {
            try
            {
                Encounter enc = Encounter.Instance;
                return enc != null && enc.Card != null ? Clean(Card.LocaliseCard(enc.Card, null)) : "";
            }
            catch (Exception) { return ""; }
        }

        static string ChallengeName()
        {
            try
            {
                Challenge c = Challenge.Instance;
                return c != null && c.ChallengeCard != null ? Clean(Card.LocaliseCard(c.ChallengeCard, null)) : "";
            }
            catch (Exception) { return ""; }
        }

        /// "Against: 3 Fool's Guards, Bandit Leader" from the enemy cards of the encounter.
        static string Enemies()
        {
            try
            {
                var parts = new List<string>();
                foreach (SpawnerCard c in Encounter.CombatSpawnerCards)
                {
                    if (c == null || c.Team != TeamType.Enemy) continue;
                    string name = Clean(Card.LocaliseCard(c, null));
                    if (name.Length == 0) continue;
                    int n = c is MonsterCard m ? m.Count : 1;
                    parts.Add(n > 1 ? n + " x " + name : name);
                    if (parts.Count >= 4) break;
                }
                return parts.Count > 0 ? "Against: " + string.Join(", ", parts.ToArray()) : "";
            }
            catch (Exception) { return ""; }
        }
    }
}

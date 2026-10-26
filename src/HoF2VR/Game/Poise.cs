using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// Until You Fall's enemy guard ("poise"), guard break, stagger and crit combo (docs/uyf-reference.md §3.8-§3.9; combat
    /// Stage 2 item 1, docs/combat-uyf-design.md §9.2). Our own code with UYF's numbers (work/uyf/reports/enemy_stats.tsv):
    ///  * every enemy has a guard; its size and refill since the user's rulings of 2026-10-18 (EnemyTable.GuardOf): by the
    ///    game's tier, 3 standard sword hits for a basic enemy (2 with the basic axe), 5 advanced, 7 elite, 10 a boss, full
    ///    again 10 s after it began to refill (elites 8 s, bosses 6 s, never under 4 s); the rest by the UYF enemy its family
    ///    fights like (Stage 2 item 9: a thief = UYF's Husk, a soldier = Knight...); x `[Combat] EnemyGuard`. It refills
    ///    once 5 s (the Horror 10 s) passed without a guard hit;
    ///  * our hits chip it by the weapon's guard damage (WeaponTable: sword 7) x the quality (Under 0.3, Standard 1, Big 1.2)
    ///    x the enemy's hit multiplier (Knight 0.75, Captain 0.9, others 1) x 0.25 when out of hit charges; our blocks chip
    ///    the attacker's by the weapon's guard damage x its blocked multiplier (Husk 1.5, Knight 1, Captain 1.3);
    ///  * at 0 the guard breaks: the enemy freezes 0.15 s, then staggers (the game's own stun, held) and starts nothing;
    ///    a guard-break crit opens on it (7 s); cutting along it (the way its arrow points, within CritLeeway; since 2026-10-18 a cut against the arrow is wrong; with a two-handed
    ///    weapon) is a crit: health damage, 0.2 s hit-stop, and the next crit 0.3 s later, each lasting 0.25 s less (Husk
    ///    3 -> 1.5 s, Knight 2 -> 0.5 s) up to `[Combat] CritCombo` crits; then the combo is done: the enemy is knocked back
    ///    and recovers (Husk 3 s, Knight 2.5 s, Captain 2.2 s, boss 1.8 s); a crit left to time out drops the combo (1.5 s);
    ///    then (since the audit of 2026-10-18) it fights again with its guard growing from empty at its refill rate (full in
    ///    its refill time, never under 4 s);
    ///  * with `[Combat] Damage = Crits` (default) health is lost only through crits; EveryHit keeps the earlier rule.
    internal class Poise : MonoBehaviour
    {
        public static Poise I;
        public static int PostBreakRefills;
        public static int GuardHits, BlockChips, Breaks, CritsOpened, Crits, CritsWrong, CritsTimedOut, CombosDone, CombosDropped, Waits, InterruptsOpened, Interrupts;
        public static float LastChip;
        public static string Last = "-";
        static readonly List<string> s_log = new List<string>();

        public enum Phase { Up, Broken, Recover }
        public enum Outcome { Guard, Crit, Wrong, Waiting }

        /// UYF's enemy numbers for one archetype (Stage 2 item 9: from the enemy table, EnemyTable / data/enemy_uyf.tsv).
        sealed class Kind
        {
            public string Name;
            public float Guard, Regen, Delay, HitMult, BlockMult, FirstCrit, ComboCrit, MinCrit, Finished, InterruptCrit;
            public float SuperArmor;   // UYF's Block_Guard_SuperArmor: unmodified guard damage that breaks through its block
            public float SpeedUp = 0.25f, CritDelay = 0.3f, Dropped = 1.5f;
            public string Tier = "Basic";
            public int Hits;
            public float Refill;
        }
        static readonly Dictionary<string, Kind> s_kinds = new Dictionary<string, Kind>();

        sealed class Crit
        {
            public float Opened, Duration, Angle;   // Angle: the natural cut's direction in the marker's plane (deg; 0 = to the right, -90 = down)
            public bool Big;
            public bool Interrupt;                  // an interrupt crit between the blows of a combo (UYF §3.8)
            public Defiant.Action Act;              // ... the combo it stops
            public string Result;
            public float ResultAt = -1f, WrongAt = -10f;
            public IndicatorLooks.ISlash Vis;
            public Vector3 Axis;                    // world, this frame
        }

        sealed class State
        {
            public Controller C;
            public AIController Ai;
            public Targetable T;
            public Kind K;
            public string Key = "";
            public float Max, Guard, LastGuardHit = -100f;
            public Phase Ph = Phase.Up;
            public float PhaseAt, HoldUntil, RefillStart = -1f, RefillDur = 1f;
            public bool Done;                       // the last combo was completed (else dropped)
            public int Step;                        // crits landed in this stagger
            public Crit Open;
            public float NextCritAt = -1f;
            public int LastLine = -1;
            public float ChipGlowAt = -10f, ChipFrom;   // for the bar's burn
            public float BrokeAt = -10f;
            public bool Pending;                    // the stagger's strike end and stun, done next frame
            public int StepAt;                      // the difficulty step its guard was sized for
            public float Pool = -1f;                // UYF's block super armor left in its current block (a pool [I])
            public bool WasBlocking;
            public readonly List<float> HitsAt = new List<float>();   // the player's hits on it (any kind), last 20 s
        }

        readonly Dictionary<int, State> m_states = new Dictionary<int, State>();
        readonly List<Crit> m_fading = new List<Crit>();
        readonly List<int> m_gone = new List<int>();

        void Awake() { I = this; }

        // ---- UYF's block timing (§3.10: blocks last >= 2 s, >= 2 s apart, give up after 5 s) on the game's shield bearers
        // (ActionBlock; the phalanx's turtle and the hit-count guard keep their own rules)
        static readonly Dictionary<int, float> s_blockSince = new Dictionary<int, float>(), s_blockEnded = new Dictionary<int, float>();
        public static int BlocksHeld, BlocksGivenUp, BlocksRefused, BlockBegins;

        public static void BlockPatch(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(HarmonyLib.AccessTools.Method(typeof(ActionBlock), "Begin"), prefix: new HarmonyLib.HarmonyMethod(typeof(Poise), nameof(BlockBeginPrefix)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(Poise), nameof(BlockBeginPostfix)));
                h.Patch(HarmonyLib.AccessTools.Method(typeof(ActionBlock), "CheckBlockConditions"), postfix: new HarmonyLib.HarmonyMethod(typeof(Poise), nameof(BlockCondPostfix)));
                h.Patch(HarmonyLib.AccessTools.Method(typeof(ActionBlock), "Complete"), postfix: new HarmonyLib.HarmonyMethod(typeof(Poise), nameof(BlockCompletePostfix)));
                h.Patch(HarmonyLib.AccessTools.Method(typeof(Defiant.Action), "CheckEntryConditions"), prefix: new HarmonyLib.HarmonyMethod(typeof(Poise), nameof(ForcedEntryPrefix)));
                // the hit-count guard may not raise the shield again within 2 s of it coming down (UYF: blocks >= 2 s apart): its
                // SetReady(true) waits - before, it raised it for a frame and BlockTiming lowered it again (CP2b: a 6.0 s "block")
                h.Patch(HarmonyLib.AccessTools.Method(typeof(ActionHitByCount), "SetReady"), prefix: new HarmonyLib.HarmonyMethod(typeof(Poise), nameof(SetReadyPrefix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("Poise: block timing patch: " + e.Message); }
        }

        static bool SetReadyPrefix(ActionHitByCount __instance, bool a_ready, bool a_force)
        {
            if (!a_ready || a_force || !Physical.Live || __instance == null || __instance.Actor == null) return true;
            try
            {
                Controller c = __instance.Actor.GetComponent<Controller>();
                float down;
                if (c != null && s_downSince.TryGetValue(c.GetInstanceID(), out down) && Time.time - down < 2f) { ShieldTooSoon++; return false; }
                // UYF: a shield carrier blocks when its hits in the last 3 s pass its archetype's threshold (HoF2's own count
                // of hits, without a time window, waits for that)
                if (c != null && CarriesShield(c) && !Aggressive(c)) { GuardGated++; return false; }
            }
            catch (Exception) { }
            return true;
        }

        static bool Enemy(Defiant.Action a)
        {
            PlayerController pc = PlayerController.Instance;
            return Physical.Live && a != null && a.Actor != null && (pc == null || a.Actor.gameObject != pc.gameObject);
        }

        static bool BlockBeginPrefix(ActionBlock __instance, ref bool __result)
        {
            if (!Enemy(__instance)) return true;
            float ended;
            if (s_blockEnded.TryGetValue(__instance.GetInstanceID(), out ended) && Time.time - ended < 2f) { BlocksRefused++; __result = false; return false; }
            return true;
        }

        static void BlockBeginPostfix(ActionBlock __instance, bool __result)
        {
            if (__result && Enemy(__instance)) { s_blockSince[__instance.GetInstanceID()] = Time.time; BlockBegins++; }
            // our own block: the shield is up for real. A soldier's ActionBlock can be the animation alone (m_dummyBlock: its
            // Controller.Blocking comes from the hit-count guard, ActionHitByCount's block-as-fallback): S2T5 saw the block
            // running and Blocking false
            if (__result && ReferenceEquals(__instance, s_forcing))
            {
                Controller c = __instance.Actor != null ? __instance.Actor.GetComponent<Controller>() : null;
                if (c != null) c.Blocking = true;
            }
        }

        static void BlockCondPostfix(ActionBlock __instance, ref bool __result)
        {
            if (!Enemy(__instance)) return;
            float since;
            if (!s_blockSince.TryGetValue(__instance.GetInstanceID(), out since)) return;
            float held = Time.time - since;
            if (s_forced.Contains(__instance.GetInstanceID()))
            {
                // our own block: up 2 s, then while it was struck in the last second, down at 5 s
                float hitAt;
                Controller c = __instance.Actor != null ? __instance.Actor.GetComponent<Controller>() : null;
                bool struck = c != null && s_lastHitOnBlock.TryGetValue(c.GetInstanceID(), out hitAt) && Time.time - hitAt < 1f;
                bool keep = held < 2f || (struck && held < 5f);
                if (!keep && held >= 5f) BlocksGivenUp++;
                __result = keep;
                return;
            }
            if (!__result && held < 2f) { __result = true; BlocksHeld++; }
            else if (__result && held >= 5f) { __result = false; BlocksGivenUp++; }
        }

        static void BlockCompletePostfix(ActionBlock __instance)
        {
            if (!Enemy(__instance)) return;
            s_blockSince.Remove(__instance.GetInstanceID());
            s_blockEnded[__instance.GetInstanceID()] = Time.time;
            if (s_forced.Remove(__instance.GetInstanceID()))
            {
                // our block ends: the shield down (the hit-count guard raises it again on its own when it is ready)
                Controller c = __instance.Actor != null ? __instance.Actor.GetComponent<Controller>() : null;
                if (c != null) c.Blocking = false;
            }
        }

        // ---- shields up (the coordinator, 2026-10-13: "SHIELDS MUST GO UP"): in Physical mode the hero never performs the attack
        // actions the game's shield bearers react to, so their ActionBlock never started (`blockcond`: its entry waits for the
        // hit-count guard). Here, UYF's rule from our side: a bearer raises its shield 0.5 s after its own attack ends (UYF's
        // Block_PostAttackBlockTimer), within 3.5 m of the player, at least 2 s after its last block; it holds it at least 2 s,
        // then while it was hit in the last second, and gives up at 5 s. Started through the action's own Evaluate (its
        // priority against what it is doing; only its entry condition is waved through), so the game's block plays: the
        // shield animation, Controller.Blocking, the deflects.
        static Defiant.Action s_forcing;
        static readonly HashSet<int> s_forced = new HashSet<int>();
        static readonly Dictionary<int, float> s_lastStrikeEnd = new Dictionary<int, float>(), s_lastHitOnBlock = new Dictionary<int, float>();
        static readonly Dictionary<int, ActionBlock> s_blockOf = new Dictionary<int, ActionBlock>();
        public static int ShieldsRaised, ShieldsAfterAttack, ShieldsAggression, GuardGated;

        static bool ForcedEntryPrefix(Defiant.Action __instance, ref bool __result)
        {
            if (s_forcing == null || !ReferenceEquals(__instance, s_forcing)) return true;
            __result = true;
            return false;
        }

        /// BladeHits: the player struck this enemy (its block, if raised, stays up while it is under attack).
        public static void StruckWhileBlocking(Controller c)
        {
            if (c != null) s_lastHitOnBlock[c.GetInstanceID()] = Time.time;
        }

        static readonly Dictionary<int, bool> s_shieldOf = new Dictionary<int, bool>();

        /// A shield in the enemy's model (a part named like one: EnemyWeapon's parts).
        static bool CarriesShield(Controller c)
        {
            bool v;
            int id = c.GetInstanceID();
            if (s_shieldOf.TryGetValue(id, out v)) return v;
            v = false;
            try
            {
                foreach (EnemyWeapon.Part p in EnemyWeapon.Sample(c.Model))
                    if (p.Name != null && p.Name.ToLowerInvariant().Contains("shield")) { v = true; break; }
                if (!v && c.Model != null)
                    foreach (Transform t in c.Model.GetComponentsInChildren<Transform>(true))
                        if (t.name.ToLowerInvariant().Contains("shield")) { v = true; break; }
            }
            catch (Exception) { }
            s_shieldOf[id] = v;
            return v;
        }

        void ShieldsUp()
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null || VRRig.I == null) return;
            Targetable me = pc.Targetable;
            BetterList<Targetable> all = me.EnumerateWithinRadius(12f, me.GetEnemyTeams());
            float now = Time.time;
            Vector3 head = VRRig.I.HeadWorldPos;
            for (int i = 0; i < all.size; i++)
            {
                Targetable t = all[i];
                Controller c = t != null ? t.Controller : null;
                if (c == null) continue;
                int id = c.GetInstanceID();
                ActionBlock b;
                if (!s_blockOf.TryGetValue(id, out b))
                {
                    try { Actor actor = t.GetComponent<Actor>(); b = actor != null ? actor.GetComponentInChildren<ActionBlock>(true) : null; } catch (Exception) { b = null; }
                    s_blockOf[id] = b;
                }
                if (b == null || b.IsRunning) continue;
                if (!CarriesShield(c)) continue;   // shield carriers only (an ogre has a block action too: it raised it after every attack)
                if (Guard.RunningStrikeOf(c) != null) { s_lastStrikeEnd[id] = now; continue; }
                // UYF's two reasons to block: its hits in the last 3 s over its archetype's threshold (everyone that blocks), or
                // 0.5 s after its own attack (the captains only: their Block_PostAttackBlockTimer)
                float aggr, lose;
                bool afterAttack;
                EnemyTable.BlockRules(c, out aggr, out afterAttack, out lose);
                bool hitHard = Aggressive(c);
                float end;
                bool post = afterAttack && s_lastStrikeEnd.TryGetValue(id, out end) && now - end >= 0.5f && now - end <= 1.5f;
                if (!hitHard && !post) continue;
                float ended;
                if (s_blockEnded.TryGetValue(b.GetInstanceID(), out ended) && now - ended < 2f) continue;
                if (s_downSince.TryGetValue(id, out ended) && now - ended < 2f) continue;   // the shield came down < 2 s ago
                if (c.Blocking) continue;                                                    // already up (the hit-count guard)
                if (Staggered(c) || c.IsStunned(0f) || c.Prone.HasValue) continue;
                Vector3 d = t.transform.position - head;
                d.y = 0f;
                if (!hitHard && d.magnitude > 3.5f) continue;
                s_forcing = b;
                try { b.Evaluate(); } catch (Exception e) { Plugin.L.LogWarning("Poise: shield up: " + e.Message); }
                finally { s_forcing = null; }
                s_lastStrikeEnd.Remove(id);
                if (b.IsRunning)
                {
                    s_forced.Add(b.GetInstanceID());
                    ShieldsRaised++;
                    if (hitHard) ShieldsAggression++;
                    else ShieldsAfterAttack++;
                    Note("shield up: " + WeaponGrips.ModelKey(t.name) + (hitHard ? " (hit more than " + aggr.ToString("0.##") + " times in 3 s)" : " (0.5 s after its attack)"));
                }
            }
        }

        // UYF's block timing on the shield itself (Controller.Blocking), whoever raised it - the hit-count guard (HoF2's
        // soldiers block after a few hits: S2T5 saw its blocks last 6.3 s while being struck), the soldier's ActionBlock, our
        // ShieldsUp: up at least 2 s, given up at 5 s, at least 2 s apart. Not the phalanx (its turtle keeps its own rules) and
        // never a staggered or stunned enemy (no shield then).
        static readonly Dictionary<int, float> s_upSince = new Dictionary<int, float>(), s_downSince = new Dictionary<int, float>();
        static readonly Dictionary<int, bool> s_phalanx = new Dictionary<int, bool>();
        public static int ShieldDrops, ShieldHolds, ShieldTooSoon;
        public static float LastShieldUp;

        /// Each shield's time up, as BlockTiming saw it (`shields? [reset]`).
        public static readonly List<float> ShieldSpans = new List<float>();
        static void Span(float t) { if (ShieldSpans.Count > 100) ShieldSpans.RemoveAt(0); ShieldSpans.Add(t); }

        void BlockTiming()
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null) return;
            Targetable me = pc.Targetable;
            BetterList<Targetable> all = me.EnumerateWithinRadius(25f, me.GetEnemyTeams());
            float now = Time.time;
            for (int i = 0; i < all.size; i++)
            {
                Targetable t = all[i];
                Controller c = t != null ? t.Controller : null;
                if (c == null) continue;
                int id = c.GetInstanceID();
                ActionBlock b;
                if (!s_blockOf.TryGetValue(id, out b))
                {
                    try { Actor actor = t.GetComponent<Actor>(); b = actor != null ? actor.GetComponentInChildren<ActionBlock>(true) : null; } catch (Exception) { b = null; }
                    s_blockOf[id] = b;
                }
                if (b == null) continue;
                bool phalanx;
                if (!s_phalanx.TryGetValue(id, out phalanx))
                {
                    try { Actor actor = t.GetComponent<Actor>(); phalanx = actor != null && actor.GetComponentInChildren<ActionPhalanxBlock>(true) != null; } catch (Exception) { phalanx = false; }
                    s_phalanx[id] = phalanx;
                }
                if (phalanx) continue;
                float since, down;
                if (Staggered(c) || c.IsStunned(0f) || c.Prone.HasValue)
                {
                    if (c.Blocking) c.Blocking = false;
                    if (s_upSince.Remove(id)) s_downSince[id] = now;
                    continue;
                }
                if (c.Blocking)
                {
                    if (!s_upSince.TryGetValue(id, out since))
                    {
                        if (s_downSince.TryGetValue(id, out down) && now - down < 2f) { c.Blocking = false; ShieldTooSoon++; continue; }
                        s_upSince[id] = now;
                    }
                    else if (now - since >= 5f)
                    {
                        c.Blocking = false;
                        s_upSince.Remove(id);
                        s_downSince[id] = now;
                        LastShieldUp = now - since;
                        Span(LastShieldUp);
                        ShieldDrops++;
                        Note("shield given up after " + LastShieldUp.ToString("0.0") + " s: " + WeaponGrips.ModelKey(t.name));
                    }
                }
                else if (s_upSince.TryGetValue(id, out since))
                {
                    if (now - since < 2f) { c.Blocking = true; ShieldHolds++; }
                    else
                    {
                        s_upSince.Remove(id);
                        s_downSince[id] = now;
                        LastShieldUp = now - since;
                        Span(LastShieldUp);
                    }
                }
            }
        }

        public static string Describe() =>
            "chips " + GuardHits + " blockChips " + BlockChips + " lastChip " + LastChip.ToString("0.0") + " breaks " + Breaks + " critsOpened " + CritsOpened + " critsLanded " + Crits +
            " critsWrong " + CritsWrong + " nearPlaced " + NearPlaced + " lineCrossings " + LineCrossings + " lineHits " + BladeHits.LineHits + " lineLast [" + LineLast + "] critsTimedOut " + CritsTimedOut + " combosDone " + CombosDone + " combosDropped " + CombosDropped + " waits " + Waits + " interruptsOpened " + InterruptsOpened + " interrupts " + Interrupts + " shieldsRaised " + ShieldsRaised + " shieldsAfterAttack " + ShieldsAfterAttack + " shieldsAggression " + ShieldsAggression + " guardGated " + GuardGated + " poolHits " + PoolHits + " shieldDrops " + ShieldDrops + " shieldHolds " + ShieldHolds + " shieldTooSoon " + ShieldTooSoon + " lastShieldUp " + LastShieldUp.ToString("0.0") + " blockBegins " + BlockBegins + " blocksHeld " + BlocksHeld + " blocksGivenUp " + BlocksGivenUp + " blocksRefused " + BlocksRefused + " poise " + Last;

        public static void ResetCounts()
        {
            GuardHits = BlockChips = Breaks = CritsOpened = Crits = CritsWrong = CritsTimedOut = CombosDone = CombosDropped = Waits = InterruptsOpened = Interrupts = 0;
            ShieldDrops = ShieldHolds = ShieldTooSoon = 0;
            TryNotes = 0;
            LastShieldUp = 0f;
            LastChip = 0f;
            Last = "-";
            s_log.Clear();
        }

        public static string Log() => string.Join("\n  ", s_log.ToArray());

        static void Note(string s)
        {
            Last = s;
            if (s_log.Count < 300) s_log.Add(FightClock.Now.ToString("0.00") + " " + s);
            if (s_log.Count <= 80) Plugin.L.LogInfo("Poise: " + s);
        }

        // ---------------------------------------------------------------------------------------- state

        static Kind KindOf(Targetable t, out string key)
        {
            key = WeaponGrips.ModelKey(t.name);
            string how;
            EnemyTable.Arch a = EnemyTable.Of(t.Controller, out how);
            Kind k;
            // the guard and its refill at the difficulty, by the enemy's tier (the user's rulings of 2026-10-18: EnemyTable.GuardOf)
            float regen, refill;
            int hits;
            string tier;
            float guard = EnemyTable.GuardOf(a, t.Controller, out regen, out hits, out refill, out tier);
            string ck = a.Name + "/" + tier + "@" + CombatSettings.Step;
            if (!s_kinds.TryGetValue(ck, out k))
            {
                s_kinds[ck] = k = new Kind
                {
                    Name = a.Name, Guard = guard, Regen = regen, Delay = a.Delay, HitMult = a.HitMult, BlockMult = a.BlockMult,
                    FirstCrit = a.FirstCrit, ComboCrit = a.ComboCrit, MinCrit = a.MinCrit, Finished = a.Finished, InterruptCrit = a.InterruptCrit, SuperArmor = a.SuperArmor,
                    Tier = tier, Hits = hits, Refill = refill
                };
                Plugin.L.LogInfo("Poise: guard of " + a.Name + " (" + tier + ") at " + CombatSettings.Difficulty + ": " + guard.ToString("0.0") + ", " + hits +
                                 " sword hits, full again in " + refill.ToString("0.0") + " s (" + regen.ToString("0.00") + "/s after " + a.Delay.ToString("0") + " s)");
            }
            return k;
        }

        State Get(Targetable t)
        {
            if (t == null || t.Controller == null) return null;
            int id = t.GetInstanceID();
            State s;
            if (m_states.TryGetValue(id, out s) && s.T == t)
            {
                // the difficulty or EnemyGuard changed (the settings page, `vrguard`, live): the guard's size and refill follow, the
                // guard kept in proportion (MR2a: `vrguard 1000` stayed on a soldier's existing guard for the rest of the fight)
                if (s.StepAt != CombatSettings.Step || Mathf.Abs(s.Max - s.K.Guard * CombatSettings.EnemyGuard) > 0.01f)
                {
                    string key0;
                    Kind k0 = KindOf(t, out key0);
                    float max = k0.Guard * CombatSettings.EnemyGuard, frac = s.Max > 0f ? s.Guard / s.Max : 1f;
                    s.K = k0;
                    s.Max = max;
                    s.Guard = max * frac;
                    s.StepAt = CombatSettings.Step;
                }
                return s;
            }
            string key;
            Kind k = KindOf(t, out key);
            s = new State { T = t, C = t.Controller, Ai = t.AIController, K = k, Key = key, StepAt = CombatSettings.Step };
            s.Max = s.Guard = k.Guard * CombatSettings.EnemyGuard;
            m_states[id] = s;
            return s;
        }

        /// For the enemy meter (EnemyBars): the guard's share left, the share still glowing from the last chip, the phase and
        /// how long it has lasted, and in Recover the hold's progress (0..1: the shell re-forms over it). False = no guard (not an
        /// enemy we track yet: full).
        public static bool View(Targetable t, out float guard, out float glow, out Phase ph, out float since, out float hold)
        {
            guard = 1f;
            glow = 1f;
            ph = Phase.Up;
            since = 99f;
            hold = 0f;
            State s;
            if (I == null || t == null || !I.m_states.TryGetValue(t.GetInstanceID(), out s) || s.T != t || s.Max <= 0f) return false;
            float now = FightClock.Now;
            guard = Mathf.Clamp01(s.Guard / s.Max);
            // the chipped part glows 0.25 s, then burns away over 0.3 s (UYF's meter burn)
            float k = Mathf.Clamp01((now - s.ChipGlowAt - 0.25f) / 0.3f);
            glow = Mathf.Max(guard, Mathf.Lerp(Mathf.Clamp01(s.ChipFrom / s.Max), guard, k));
            ph = s.Ph;
            since = now - s.PhaseAt;
            if (s.Ph == Phase.Recover && s.HoldUntil > s.PhaseAt) hold = Mathf.Clamp01((now - s.PhaseAt) / (s.HoldUntil - s.PhaseAt));
            return true;
        }

        /// The enemy is staggered (its guard broken, crits open or coming) or recovering: it starts nothing.
        public static bool Staggered(Controller c)
        {
            if (I == null || c == null || c.Targetable == null) return false;
            State s;
            return I.m_states.TryGetValue(c.Targetable.GetInstanceID(), out s) && s.Ph != Phase.Up;
        }

        // ---------------------------------------------------------------------------------------- chips

        void Chip(State s, float amount, string why)
        {
            if (s == null || amount <= 0f || s.Ph != Phase.Up) return;
            float now = FightClock.Now;
            if (now - s.ChipGlowAt > 0.55f) s.ChipFrom = s.Guard;   // a chip in the glow keeps the trail from the first one
            s.ChipGlowAt = now;
            s.Guard = Mathf.Max(0f, s.Guard - amount);
            s.LastGuardHit = now;
            LastChip = amount;
            Note(why + " " + s.Key + " -" + amount.ToString("0.0") + " -> " + s.Guard.ToString("0.0") + "/" + s.Max.ToString("0"));
            if (s.Guard <= 0f) Break(s);
        }

        /// A blade hit (BladeHits): a guard hit while the guard is up (it chips it), a crit when it cuts along the open crit,
        /// otherwise nothing (the wrong way, or between crits).
        public static Outcome Blade(Targetable t, int quality, bool stab, bool charged, Vector3 swingDir, out string why)
        {
            why = "";
            if (I == null) return Outcome.Guard;
            State s = I.Get(t);
            if (s == null) return Outcome.Guard;
            WeaponSpec w = WeaponTable.Current();
            if (s.Ph == Phase.Up && s.Open != null && s.Open.Interrupt && s.Open.Result == null && !stab && I.Along(s.Open, swingDir) <= WeaponTable.CritLeeway())
            {
                // an interrupt crit, cut along its line: health (a crit) and the combo stops; the guard is chipped as well
                if (VRRig.I != null) TryNote("OK", s, JudgeOpen(s, t, swingDir, VRRig.I.HeadWorldPos), WeaponTable.CritLeeway(), " (q" + quality + ")");
                float q0 = quality == 0 ? w.UnderG : quality == 1 ? 1f : w.BigG;
                I.Interrupted(s);
                I.Chip(s, w.Guard * q0 * s.K.HitMult * (charged ? 1f : WeaponTable.Uncharged), "interrupt");
                why = " INTERRUPT crit (the combo stops)";
                return Outcome.Crit;
            }
            if (s.Ph == Phase.Up)
            {
                float q = quality == 0 ? w.UnderG : quality == 1 ? 1f : w.BigG;
                float chip = w.Guard * q * s.K.HitMult * (charged ? 1f : WeaponTable.Uncharged);
                GuardHits++;
                I.Chip(s, chip, "hit");
                why = " guard -" + chip.ToString("0.0") + " (" + s.Guard.ToString("0") + "/" + s.Max.ToString("0") + ")";
                return Outcome.Guard;
            }
            if (s.Ph == Phase.Broken && s.Open != null && s.Open.Result == null)
            {
                float a = I.Along(s.Open, swingDir);
                float lee = WeaponTable.CritLeeway();
                if (!stab && a <= lee)
                {
                    if (VRRig.I != null) TryNote("OK", s, JudgeOpen(s, t, swingDir, VRRig.I.HeadWorldPos), lee, " (q" + quality + ")");
                    I.Land(s, quality);
                    why = " crit " + s.Step + "/" + CombatSettings.CritCombo + " (" + a.ToString("0") + " deg off its line)";
                    return Outcome.Crit;
                }
                CritsWrong++;
                s.Open.WrongAt = FightClock.Now;
                why = stab ? " a stab: crits need a cut" : " the wrong way: " + a.ToString("0") + " deg off its arrow (" + lee.ToString("0") + " allowed)" + (a >= 180f - lee ? " - against the arrow" : "");
                Note("wrong cut " + s.Key + why);
                return Outcome.Wrong;
            }
            Waits++;
            why = s.Ph == Phase.Broken ? " (the next crit is coming)" : " (recovering)";
            return Outcome.Waiting;
        }

        /// The player blocked this attacker's blow (Guard): UYF's "defending builds toward the guard break".
        public static void Blocked(Controller attacker)
        {
            if (I == null || attacker == null || attacker.Targetable == null) return;
            State s = I.Get(attacker.Targetable);
            if (s == null || s.Ph != Phase.Up) return;
            float chip = WeaponTable.Current().Guard * s.K.BlockMult;
            BlockChips++;
            I.Chip(s, chip, "block");
        }

        /// The player's playtest 2026-10-17: "If my weapon goes through the line, it should be considered the right distance" - a
        /// cut through a crit's drawn line counts at any depth, as the eyes see it: the blade (base to tip) projected from the
        /// eyes onto the line's plane crosses the line (its drawn length + 15 %, LineTolerance either side) somewhere between the last
        /// frame and this one, the cut runs along it (CritLeeway), and the line is at most LineReach from the eyes. The enemies
        /// whose open crit the blade went through this frame; their line's centre as the hit point.
        public const float LineReach = 2.6f;
        /// How far beside the drawn line (in its plane, m) the blade may pass and still go "through" it: the line's glow is
        /// wider than its core, and a cut seen 10 cm under a line 2 m away looks like a cut through it (PF3g). Playtest 4
        /// (P4-9, "I almost have to be too perfect"): 0.12 -> 0.2 m, near UYF's own crit target - a capsule of radius 0.25 m,
        /// 1.1 m long along the arrow (docs/uyf-reference.md §3.9) - so the line's reach is its drawn half-length x 1.15 +
        /// 0.2 m either way along it (0.50 m; the guard-break crit's 0.56), as UYF's 0.55.
        public static float LineTolerance = 0.2f;
        public static int LineCrossings;
        // the last line check's numbers (the audit, item 8: formatted only when asked - `pcombat?` - not every frame)
        static float s_llFar = -1f, s_llAlong;
        static int s_llWhy;   // 0 checked, 1 too far, 2 not along
        static bool s_llCrossed, s_llSeen;
        static Vector2 s_llA, s_llB;
        public static string LineLast => s_llFar < 0f ? "-" : "line at " + s_llFar.ToString("0.00") + " m, along " + s_llAlong.ToString("0") + " deg" +
            (s_llWhy == 1 ? " (too far)" : s_llWhy == 2 ? " (not along)" : s_llSeen ? " blade seen " + s_llA.ToString("0.00") + "-" + s_llB.ToString("0.00") + (s_llCrossed ? " CROSSED" : "") : "");

        /// misses: the enemies whose line the blade went through this frame against or across the arrow (BladeHits keeps the
        /// best of them for the swing; a cut that never follows the arrow is a wrong cut once the swing ends).
        public static void CrossedLines(Vector3 eye, Vector3 pBase, Vector3 pTip, Vector3 base_, Vector3 tip, Vector3 swingDir, List<Targetable> hits, List<Vector3> points, List<Targetable> misses)
        {
            hits.Clear();
            points.Clear();
            misses.Clear();
            if (I == null) return;
            float lee = WeaponTable.CritLeeway();
            foreach (State s in I.m_states.Values)
            {
                Crit c = s.Open;
                if (c == null || c.Result != null || c.Vis == null || s.T == null) continue;
                if (!(s.Ph == Phase.Broken || (s.Ph == Phase.Up && c.Interrupt))) continue;
                Vector3 ctr = c.Vis.Root.position, n = c.Vis.Root.forward, ax = c.Axis;
                float far = (ctr - eye).magnitude, along = I.Along(c, swingDir);
                s_llFar = far; s_llAlong = along; s_llWhy = 0; s_llSeen = false;
                if (ax.sqrMagnitude < 0.5f || far > LineReach) { s_llWhy = 1; continue; }
                Vector3 perp = Vector3.Cross(n, ax).normalized;
                float half = (c.Big ? 0.31f : 0.26f) * 1.15f;
                bool crossed = false;
                Vector2 la = Vector2.zero, lb = Vector2.zero;
                for (int i = 0; i <= 4 && !crossed; i++)
                {
                    float k = i / 4f;
                    Vector2 a, b;
                    if (!Project(eye, ctr, n, ax, perp, Vector3.Lerp(pBase, base_, k), out a) || !Project(eye, ctr, n, ax, perp, Vector3.Lerp(pTip, tip, k), out b)) continue;
                    la = a; lb = b;
                    crossed = SegmentMeetsLine(a, b, half, LineTolerance);
                }
                s_llSeen = true; s_llA = la; s_llB = lb; s_llCrossed = crossed;
                if (!crossed) continue;
                if (along > lee) { s_llWhy = 2; misses.Add(s.T); continue; }
                LineCrossings++;
                hits.Add(s.T);
                points.Add(ctr);
            }
        }

        /// A world point seen from the eyes, on the plane through ctr (normal n): its place along the line (x) and across it (y).
        static bool Project(Vector3 eye, Vector3 ctr, Vector3 n, Vector3 ax, Vector3 perp, Vector3 p, out Vector2 uv)
        {
            uv = Vector2.zero;
            Vector3 d = p - eye;
            float dn = Vector3.Dot(d, n);
            if (dn < 0.1f) return false;   // behind or at the eyes
            Vector3 q = eye + d * (Vector3.Dot(ctr - eye, n) / dn) - ctr;
            uv = new Vector2(Vector3.Dot(q, ax), Vector3.Dot(q, perp));
            return true;
        }

        /// Does the 2D segment a-b come within tol of the line from (-half, 0) to (half, 0)?
        static bool SegmentMeetsLine(Vector2 a, Vector2 b, float half, float tol)
        {
            if ((a.y <= 0f && b.y >= 0f) || (a.y >= 0f && b.y <= 0f))
            {
                float dy = b.y - a.y;
                float x = Mathf.Abs(dy) < 1e-6f ? Mathf.Clamp(0f, Mathf.Min(a.x, b.x), Mathf.Max(a.x, b.x)) : a.x + (b.x - a.x) * (-a.y / dy);
                if (Mathf.Abs(dy) < 1e-6f) return Mathf.Max(a.x, b.x) >= -half - tol && Mathf.Min(a.x, b.x) <= half + tol;
                if (Mathf.Abs(x) <= half + tol) return true;
            }
            float d = Mathf.Min(ToLine(a, half), ToLine(b, half));
            d = Mathf.Min(d, Mathf.Min(ToSeg(new Vector2(-half, 0f), a, b), ToSeg(new Vector2(half, 0f), a, b)));
            return d <= tol;
        }

        static float ToLine(Vector2 p, float half) => new Vector2(Mathf.Max(0f, Mathf.Abs(p.x) - half), p.y).magnitude;

        static float ToSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float k = ab.sqrMagnitude > 1e-10f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return (a + ab * k - p).magnitude;
        }

        /// How far (deg) a cut runs from a crit's arrow: 0 = the way the arrow points, 180 = against it (CutAngle).
        /// The user, playtest 3 (2026-10-18, mark 22): "I struck a down arrow upwards and it still gave it to me... I should have to
        /// decently follow the direction of the arrow." Until then either way along the line counted.
        float Along(Crit c, Vector3 swingDir)
        {
            float eye, share;
            return CutAngle(swingDir, c.Vis != null ? c.Vis.Root.forward : Vector3.forward, c.Axis, out eye, out share);
        }

        /// The least share of a cut that must run upright (square to the player's way to the marker) for its direction to be
        /// judged; a cut mostly toward or away from the player says nothing about the arrow (90 deg).
        public const float MinShare = 0.3f;

        /// How far (deg) a cut runs from an arrow drawn facing the eyes (view: eyes -> marker; axis: the arrow, in the drawn plane
        /// square to view): 0 = the way it points, 180 = against it, 90 when the cut runs mostly toward or away from the player.
        /// Judged in the UPRIGHT plane facing the player - the arrow's sideways and up/down parts (as drawn) on the horizontal
        /// right and the world's up, the cut without its part toward or away from the player. Playtest 4 (P4-9, "it might be a
        /// thing about me standing at a weird angle or distance"): until then the cut was judged in the drawn plane (UYF's way,
        /// docs/uyf-reference.md §3.9), which tilts with the line of sight - looking down 45 deg at a marker, a chop down and
        /// forward runs along the line of sight and had no part left in that plane (6 of the 13 wrong cuts in the user's
        /// skeleton fight were exactly that: "90 deg off its arrow"), and at steeper looks it even read as going up. With the
        /// gaze level (UYF's case) the two planes are the same. eye: the drawn plane's angle (the log); share: the cut's
        /// upright part (0..1).
        public static float CutAngle(Vector3 swingDir, Vector3 view, Vector3 axis, out float eye, out float share)
        {
            eye = 90f;
            share = 0f;
            if (swingDir.sqrMagnitude < 1e-10f || axis.sqrMagnitude < 0.5f || view.sqrMagnitude < 1e-8f) return 90f;
            Vector3 s = swingDir.normalized;
            view.Normalize();
            Vector3 pe = s - view * Vector3.Dot(s, view);
            if (pe.sqrMagnitude > MinShare * MinShare) eye = Vector3.Angle(pe, axis);
            Vector3 f = view;
            f.y = 0f;
            if (f.sqrMagnitude < 0.04f) { share = pe.magnitude; return eye; }   // looking almost straight down or up: the drawn plane
            f.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, f);            // the drawn right (horizontal)
            Vector3 upDrawn = Vector3.Cross(view, right).normalized;   // the drawn up (the world's up turned square to the gaze)
            Vector3 ax = right * Vector3.Dot(axis, right) + Vector3.up * Vector3.Dot(axis, upDrawn);
            if (ax.sqrMagnitude < 1e-6f) return eye;
            Vector3 pu = s - f * Vector3.Dot(s, f);
            share = pu.magnitude;
            if (share <= MinShare) return 90f;
            return Vector3.Angle(pu, ax);
        }

        // ---- the cut at an open crit, frame by frame (BladeHits): UYF re-judges its crit every frame the blade is in it and an
        // off frame spoils nothing (docs/uyf-reference.md §3.9); here a body contact or a pass through the line that does not
        // follow the arrow is kept by BladeHits as the swing's best try, and only a swing that never followed it is a wrong cut.

        /// One try at an open crit: the cut's angle to the arrow (upright, as judged), the drawn plane's, the upright share; the
        /// marker's distance from the eyes and how far below eye level (deg); the enemy's horizontal distance.
        public struct Try { public float Angle, Eye, Share, Far, Below, Away; public Vector3 CutT, ArrowT; }

        /// The enemy is staggered with a crit open and not cut yet (its guard broken): its crit is what a cut on it is judged by.
        /// interrupts: an open interrupt crit counts too (its body still takes guard hits; only its line is judged this way).
        /// opened: the crit's opening time (a later try is checked against the same crit).
        public static bool CritOpen(Targetable t, out float opened, bool interrupts = false)
        {
            opened = -1f;
            State s;
            if (I == null || t == null || !I.m_states.TryGetValue(t.GetInstanceID(), out s) || s.T != t) return false;
            if (s.Open == null || s.Open.Result != null || s.Open.Vis == null) return false;
            if (!(s.Ph == Phase.Broken || (interrupts && s.Ph == Phase.Up && s.Open.Interrupt))) return false;
            opened = s.Open.Opened;
            return true;
        }

        /// The try a cut (swingDir, world) makes at this enemy's open crit, seen from the eyes.
        public static Try Judge(Targetable t, Vector3 swingDir, Vector3 eye)
        {
            State s;
            if (I == null || t == null || !I.m_states.TryGetValue(t.GetInstanceID(), out s) || s.Open == null) return new Try { Angle = 90f, Eye = 90f };
            return JudgeOpen(s, t, swingDir, eye);
        }

        static Try JudgeOpen(State s, Targetable t, Vector3 swingDir, Vector3 eye)
        {
            var r = new Try { Angle = 90f, Eye = 90f };
            Crit c = s.Open;
            if (c == null) return r;
            r.Angle = CutAngle(swingDir, c.Vis != null ? c.Vis.Root.forward : Vector3.forward, c.Axis, out r.Eye, out r.Share);
            if (VRRig.I != null) { r.CutT = VRRig.I.FromWorldDir(swingDir).normalized; r.ArrowT = VRRig.I.FromWorldDir(c.Axis).normalized; }
            if (c.Vis != null)
            {
                Vector3 d = c.Vis.Root.position - eye;
                r.Far = d.magnitude;
                r.Below = r.Far > 1e-4f ? Mathf.Asin(Mathf.Clamp(-d.y / r.Far, -1f, 1f)) * Mathf.Rad2Deg : 0f;
            }
            if (t != null)
            {
                Vector3 e = t.transform.position - eye;
                e.y = 0f;
                r.Away = e.magnitude;
            }
            return r;
        }

        /// BladeHits, once a legal cut has gone through an open crit (the enemy's body or the line) without once following the
        /// arrow closely enough: a wrong cut - counted, the marker flickers, logged with the swing's best frame. False when that
        /// crit is no longer open (cut, timed out, or another one opened).
        public static bool Missed(Targetable t, float opened, Try best, int frames, string via, float speed)
        {
            State s;
            if (I == null || t == null || !I.m_states.TryGetValue(t.GetInstanceID(), out s) || s.Open == null || s.Open.Result != null ||
                Mathf.Abs(s.Open.Opened - opened) > 1e-4f) return false;
            float lee = WeaponTable.CritLeeway();
            CritsWrong++;
            s.Open.WrongAt = FightClock.Now;
            string why = best.Share <= MinShare ? "the cut ran mostly toward or away from you (its upright part " + (best.Share * 100f).ToString("0") + " %)" :
                         best.Angle >= 180f - lee ? "against the arrow" : "across the arrow";
            TryNote("MISS", s, best, lee, ", " + frames + " frame(s) through it (" + via + "), " + speed.ToString("0.0") + " m/s: " + why);
            Last = "wrong cut " + s.Key + " " + best.Angle.ToString("0") + " deg";
            return true;
        }

        public static int TryNotes;

        /// Every judged crit try in the log (not capped by Note's 80): the numbers the user's "standing at a weird angle or
        /// distance" needs.
        static void TryNote(string result, State s, Try r, float lee, string tail)
        {
            if (TryNotes++ > 400) return;
            Crit c = s.Open;
            Plugin.L.LogInfo("Poise: crit try " + result + " " + s.Key + "#" + (s.T != null ? s.T.GetInstanceID() : 0) + " " +
                (c != null ? (c.Big ? "break " : c.Interrupt ? "interrupt " : "combo ") + c.Angle.ToString("0") : "-") + ": " + r.Angle.ToString("0") +
                " deg off the arrow (" + lee.ToString("0") + " allowed; in the plane facing the eyes " + r.Eye.ToString("0") + "), upright part " +
                (r.Share * 100f).ToString("0") + " %; marker " + r.Far.ToString("0.00") + " m from the eyes, " + r.Below.ToString("0") +
                " deg below eye level; enemy " + r.Away.ToString("0.00") + " m away; the cut " + Diag.V(r.CutT) + ", the arrow " + Diag.V(r.ArrowT) + " (tracking)" + tail);
        }

        /// Guard: an enemy combo has another blow coming in about `gap` s - UYF's interrupt crit opens between the blows (UYF:
        /// 0.5 s warning, then 1.25 s; Captain 0.9): here at once, until the next blow lands (at most its
        /// InterruptCritDuration), only when the gap leaves time to react (>= 0.45 s) and the guard is up.
        /// `vrcritwindow <k>` (test mode only): interrupt crits stay open k times as long and the combo waits for them - the
        /// test harness's cut takes ~0.45 s from the crit's opening, the ogre's window is UYF's 0.5 s.
        public static float InterruptStretch = 1f;

        public static void OfferInterrupt(Controller c, Defiant.Action act, float gap)
        {
            if (I == null || c == null || c.Targetable == null || gap < 0.45f) return;
            State s = I.Get(c.Targetable);
            if (s == null || s.Ph != Phase.Up || (s.Open != null && s.Open.Result == null)) return;
            float now = FightClock.Now;
            int line;
            do line = UnityEngine.Random.Range(0, s_lines.Length); while (line == s.LastLine && s_lines.Length > 1);
            s.LastLine = line;
            float ang = s_lines[line];
            if (Settings.LeftHanded && line > 0) ang = 180f - ang;
            float dur = Mathf.Min(s.K.InterruptCrit, gap) * (TestMode.Enabled ? InterruptStretch : 1f);
            s.Open = new Crit { Opened = now, Duration = dur, Angle = ang, Interrupt = true, Act = act };
            // a stretched window (tests only) holds the combo's next blow until it closes: the blow must not land first
            if (TestMode.Enabled && InterruptStretch > 1.01f) Guard.Hold(c, dur);
            InterruptsOpened++;
            Note("interrupt crit on " + s.Key + " " + ang.ToString("0") + " deg for " + s.Open.Duration.ToString("0.00") + " s");
        }

        void Interrupted(State s)
        {
            float now = FightClock.Now;
            Crit c = s.Open;
            c.Result = "ok";
            c.ResultAt = now;
            m_fading.Add(c);
            s.Open = null;
            Interrupts++;
            Crits++;
            Note("INTERRUPTED " + s.Key + "'s combo");
            try { if (c.Act != null && c.Act.IsRunning) c.Act.Complete(); } catch (Exception) { }
            Guard.DropStrike(s.C);
            Guard.Hold(s.C, 1.0f);
        }

        /// A hit on this enemy's raised block (UYF §3.10): its unmodified guard damage `raw` comes off the block's super armor,
        /// a pool (UYF's Block_Guard_SuperArmor has Min / Max companions, the pattern of its guard and health: a depletable
        /// pool [I], inferred from those numbers): each block starts full (Husk 35, Knight 60,
        /// elite Knight 100, Captain 200, boss 100 ...); when it runs out the hit breaks through - a guard break. Else the hit
        /// is deflected (BladeHits). `left` = the pool before this hit.
        public static bool BreakThrough(Targetable t, float raw, out float left)
        {
            left = 50f;
            if (I == null) return false;
            State s = I.Get(t);
            if (s == null) return false;
            float max = SuperArmorOverride ?? s.K.SuperArmor;
            if (s.Pool < 0f || !s.WasBlocking) { s.Pool = max; s.WasBlocking = true; }   // a block whose start we did not see
            if (SuperArmorOverride.HasValue) s.Pool = Mathf.Min(s.Pool, SuperArmorOverride.Value);
            left = s.Pool;
            if (s.Ph != Phase.Up) return false;
            s.Pool -= raw;
            if (s.Pool > 0.001f) { PoolHits++; return false; }
            s.Pool = max;
            I.Chip(s, s.Guard + 1f, "break through");
            return true;
        }

        public static int PoolHits;

        /// BladeHits: the player's hit landed on this enemy (a guard hit, a crit, a deflected one, a bash): UYF's hits-taken
        /// history (blocked or not), for its aggression rule.
        public static void HitTaken(Targetable t)
        {
            if (I == null || t == null) return;
            State s = I.Get(t);
            if (s == null) return;
            float now = Time.time;
            s.HitsAt.Add(now);
            while (s.HitsAt.Count > 0 && now - s.HitsAt[0] > 20f) s.HitsAt.RemoveAt(0);
        }

        /// UYF's aggression rule: more hits than its archetype's Block_AggressionThreshold landed on it in the last 3 s
        /// (_Out_HitsTakenHistory's default period): it blocks (Knight 2.25: the third hit; Husk 4; a lesser husk never).
        public static bool Aggressive(Controller c)
        {
            if (I == null || c == null || c.Targetable == null) return false;
            State s;
            if (!I.m_states.TryGetValue(c.Targetable.GetInstanceID(), out s)) return false;
            float aggr, lose;
            bool after;
            EnemyTable.BlockRules(c, out aggr, out after, out lose);
            return HitsIn(s, 3f) > aggr;
        }

        static int HitsIn(State s, float secs)
        {
            float now = Time.time;
            int n = 0;
            for (int i = s.HitsAt.Count - 1; i >= 0 && now - s.HitsAt[i] <= secs; i--) n++;
            return n;
        }

        /// Test seam `vrsuperarmor <x>|setting`: every blocking enemy's super armor (the break-through test).
        public static float? SuperArmorOverride;

        /// The open crit's line on this enemy (world, this frame), for the test seam `bladetest <enemy> square`.
        public static bool OpenAxis(Targetable t, out Vector3 axis)
        {
            axis = Vector3.right;
            State s;
            if (I == null || t == null || !I.m_states.TryGetValue(t.GetInstanceID(), out s) || s.Open == null) return false;
            axis = s.Open.Axis;
            return axis.sqrMagnitude > 1e-6f;
        }

        /// UYF's dash into an enemy (CombatMove): its guard chipped by 10 x 0.3-1.25 by the distance dashed.
        public static void DashHit(Targetable t, float amount)
        {
            if (I == null) return;
            State s = I.Get(t);
            if (s == null || s.Ph != Phase.Up) return;
            I.Chip(s, amount, "dash");
        }

        /// The shield bash (BladeHits): chips the guard by the weapon's guard damage (x1.5 when charged).
        public static void Bash(Targetable t, bool charged)
        {
            if (I == null) return;
            State s = I.Get(t);
            if (s == null || s.Ph != Phase.Up) return;
            I.Chip(s, WeaponTable.Current().Guard * (charged ? 1.5f : 1f), "bash");
        }

        // ---------------------------------------------------------------------------------------- break, crits, recovery

        void Break(State s)
        {
            float now = FightClock.Now;
            s.Guard = 0f;
            s.Ph = Phase.Broken;
            s.PhaseAt = s.BrokeAt = now;
            s.Step = 0;
            if (s.Open != null) { if (s.Open.Result == null) { s.Open.Result = "timeout"; s.Open.ResultAt = now; } m_fading.Add(s.Open); }
            s.Open = null;
            s.NextCritAt = now + 0.15f;   // the stagger's freeze, then the guard-break crit
            Breaks++;
            Note("GUARD BREAK " + s.Key + " (" + s.K.Name + ", guard " + s.Max.ToString("0") + ")");
            // its strike (a block can break it at its own blow, inside the game's hit) ends and the stun starts next frame
            s.Pending = true;
            Guard.DropStrike(s.C);
            Guard.Hold(s.C, 60f);
            CombatFeel.HitStop(s.C, 0.15f);
            Vector3 at = Chest(s);
            CombatSounds.Layer(CombatSounds.GuardBreak, at, 1f);
            Color b0, b1;
            IndicatorLooks.BreakColours(CombatSettings.Look, out b0, out b1);
            CombatFeel.Burst(at, b0, 0.6f, 0.35f);
            CombatFeel.Burst(at, b1, 0.35f, 0.2f);
            CombatFeel.ImpactBoth(true);
            // (no shake of its own: UYF shakes only for the breaking hit, as a guard hit)
        }

        /// Hold the enemy in the game's own stun (its stagger animation) while its crits are open; let it go after.
        static void Stun(State s, bool on)
        {
            try
            {
                if (s.C == null) return;
                if (on) s.C.Stunned = new Disabled { Time = Time.time, Duration = 600f, Locked = true };
                else if (s.C.Stunned.HasValue) s.C.Stunned = new Disabled { Time = Time.time - 1f, Duration = 0f, Locked = false };
            }
            catch (Exception e) { Plugin.L.LogWarning("Poise: stun: " + e.Message); }
        }

        static readonly int[] s_lines = { -90, -135, -45, 180 };   // the natural cut on each line: a chop, both diagonals, a forehand sweep

        void OpenCrit(State s)
        {
            float now = FightClock.Now;
            bool big = s.Step == 0;
            // a different line from the last one (UYF's directional crits; the player changes the cut every time)
            int line;
            do line = UnityEngine.Random.Range(0, s_lines.Length); while (line == s.LastLine && s_lines.Length > 1);
            s.LastLine = line;
            float ang = s_lines[line];
            if (Settings.LeftHanded && line > 0) ang = 180f - ang;   // mirrored for the left hand (the chop stays)
            float dur = big ? s.K.FirstCrit : Mathf.Max(s.K.MinCrit, s.K.ComboCrit - s.K.SpeedUp * (s.Step - 1));
            s.Open = new Crit { Opened = now, Duration = dur, Angle = ang, Big = big };
            CritsOpened++;
            CombatSounds.Layer(CombatSounds.Premonition, Chest(s), big ? 0.35f : 0.22f, big ? 0.8f : 1.15f);
            Note("crit " + (big ? "(guard break) " : "") + (s.Step + 1) + "/" + CombatSettings.CritCombo + " on " + s.Key + " " + ang.ToString("0") + " deg for " + dur.ToString("0.00") + " s");
        }

        void Land(State s, int quality)
        {
            float now = FightClock.Now;
            s.Open.Result = "ok";
            s.Open.ResultAt = now;
            m_fading.Add(s.Open);
            s.Open = null;
            s.Step++;
            Crits++;
            Note("CRIT " + s.Step + "/" + CombatSettings.CritCombo + " on " + s.Key + " (q" + quality + ")");
            if (s.Step >= CombatSettings.CritCombo) Recover(s, true);
            else s.NextCritAt = now + s.K.CritDelay;
        }

        void Recover(State s, bool done)
        {
            float now = FightClock.Now;
            if (s.Open != null)
            {
                if (s.Open.Result == null) { s.Open.Result = "timeout"; s.Open.ResultAt = now; }
                m_fading.Add(s.Open);
                s.Open = null;
            }
            s.Ph = Phase.Recover;
            s.PhaseAt = now;
            s.Done = done;
            s.HoldUntil = now + (done ? s.K.Finished : s.K.Dropped);
            s.RefillStart = -1f;
            Stun(s, false);
            Guard.Hold(s.C, s.HoldUntil - now);
            if (done)
            {
                CombosDone++;
                Vector3 at = Chest(s);
                CombatSounds.Layer(CombatSounds.ComboDone, at, 0.6f);
                // UYF: the time the enemy gets for its big knockback and its recovery
                if (s.Ai != null && VRRig.I != null) EnemyPush.Start(s.Ai, s.Ai.transform.position - VRRig.I.HeadWorldPos, 1.0f, 0.35f);
                Note("combo done on " + s.Key + " (" + s.Step + " crits), it recovers " + s.K.Finished.ToString("0.0") + " s");
            }
            else
            {
                CombosDropped++;
                Note("combo dropped on " + s.Key + " after " + s.Step + " crits, it recovers " + s.K.Dropped.ToString("0.0") + " s");
            }
        }

        // ---------------------------------------------------------------------------------------- every frame

        static Vector3 Chest(State s)
        {
            try { return s.T.AimTransform.position; } catch (Exception) { return s.C != null ? s.C.transform.position + Vector3.up * 1.2f : Vector3.zero; }
        }

        void LateUpdate()
        {
            float now = FightClock.Now, dt = Time.unscaledDeltaTime;
            if (Physical.Paused)
            {
                // the audit, item 3: a pause keeps every guard, stagger and crit (FightClock stands still); only the slashes hide
                foreach (State s in m_states.Values) if (s.Open != null && s.Open.Vis != null && s.Open.Vis.Root.gameObject.activeSelf) s.Open.Vis.Root.gameObject.SetActive(false);
                return;
            }
            if (!Physical.Live)
            {
                if (m_states.Count > 0) { foreach (State s in m_states.Values) Close(s); m_states.Clear(); TryNotes = 0; }   // (the crit-try log: 400 a fight)
                foreach (Crit c in m_fading) if (c.Vis != null) c.Vis.Destroy();
                m_fading.Clear();
                return;
            }
            Vector3 head = VRRig.I.HeadWorldPos;
            try { ShieldsUp(); } catch (Exception e) { Plugin.L.LogWarning("Poise: shields: " + e.Message); }
            try { BlockTiming(); } catch (Exception e) { Plugin.L.LogWarning("Poise: block timing: " + e.Message); }
            m_gone.Clear();
            foreach (var kv in m_states)
            {
                State s = kv.Value;
                Destroyable d = s.T != null ? s.T.GetComponent<Destroyable>() : null;
                if (s.T == null || s.C == null || d == null || d.IsDead)
                {
                    if (s.Open != null) { if (s.Open.Result == null) { s.Open.Result = "timeout"; s.Open.ResultAt = now; } m_fading.Add(s.Open); s.Open = null; }
                    if (s.C != null) Guard.Hold(s.C, 0f);
                    m_gone.Add(kv.Key);
                    continue;
                }
                // a block begins: its super armor pool is full (UYF: the pool refills between blocks)
                bool bl = s.C.Blocking;
                if (bl && !s.WasBlocking) s.Pool = SuperArmorOverride ?? s.K.SuperArmor;
                s.WasBlocking = bl;
                switch (s.Ph)
                {
                    case Phase.Up:
                        if (s.Guard < s.Max && now - s.LastGuardHit >= s.K.Delay)
                            s.Guard = Mathf.Min(s.Max, s.Guard + s.K.Regen * CombatSettings.EnemyGuard * dt);
                        // an interrupt crit closes quietly when its time is up or its combo ended
                        if (s.Open != null && s.Open.Interrupt && s.Open.Result == null &&
                            (now - s.Open.Opened > s.Open.Duration || (s.Open.Act != null && !s.Open.Act.IsRunning)))
                        {
                            s.Open.Result = "timeout";
                            s.Open.ResultAt = now;
                            m_fading.Add(s.Open);
                            s.Open = null;
                        }
                        break;
                    case Phase.Broken:
                        if (s.Pending)
                        {
                            s.Pending = false;
                            // the strike it was in: over (the counter's way: the flat game's blow ends it)
                            try
                            {
                                Defiant.Action run = Guard.RunningStrikeOf(s.C);
                                if (run != null && run.IsRunning) run.Complete();
                            }
                            catch (Exception) { }
                            Stun(s, true);
                        }
                        // the game must not let it out of the stun meanwhile (another hit's reaction, its own timer)
                        if (!s.C.IsStunned(0f)) Stun(s, true);
                        Guard.Hold(s.C, 1f);
                        if (s.Open == null && now >= s.NextCritAt) OpenCrit(s);
                        if (s.Open != null && s.Open.Result == null && now - s.Open.Opened > s.Open.Duration)
                        {
                            CritsTimedOut++;
                            Note("crit timed out on " + s.Key);
                            Recover(s, false);
                        }
                        break;
                    case Phase.Recover:
                        // the audit, item 2 (the user's rule: guards take about 10 s to regenerate, never under 4): after the
                        // hold the enemy fights again at once and its guard grows from empty at its refill rate - full in its
                        // refill time (EnemyTable.GuardOf: 10 s basic and advanced, 8 elite, 6 boss, >= 4), not UYF's 1-1.25 s
                        if (now >= s.HoldUntil)
                        {
                            s.Ph = Phase.Up;
                            s.PhaseAt = now;
                            s.Guard = 0f;
                            s.RefillStart = -1f;
                            s.LastGuardHit = now - s.K.Delay;   // the refill starts now, not after the usual pause
                            PostBreakRefills++;
                            Note("guard refilling on " + s.Key + " (full in " + s.K.Refill.ToString("0.0") + " s)");
                        }
                        break;
                }
                if (s.Open != null) Draw(s.Open, s, head, now);
            }
            foreach (int id in m_gone) { Close(m_states[id]); m_states.Remove(id); }
            for (int i = m_fading.Count - 1; i >= 0; i--)
            {
                Crit c = m_fading[i];
                if (now - c.ResultAt > 0.45f || c.Vis == null) { if (c.Vis != null) c.Vis.Destroy(); m_fading.RemoveAt(i); continue; }
                c.Vis.Draw(now - c.Opened, 0f, c.Result, now - c.ResultAt, 99f, c.Big, c.Interrupt);
            }
        }

        void Close(State s)
        {
            if (s.Open != null && s.Open.Vis != null) s.Open.Vis.Destroy();
            s.Open = null;
            if (s.Ph == Phase.Broken) Stun(s, false);
        }

        public const float MinFromEyes = 0.8f;
        public static int NearPlaced;
        static Vector3 view0(Vector3 head, Vector3 chest) { Vector3 d = chest - head; return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward; }

        /// The crit on the enemy's chest, moved toward the player so no limb hides it, facing the eyes; its line turned to the
        /// cut's angle in that plane.
        void Draw(Crit c, State s, Vector3 head, float now)
        {
            if (c.Vis == null) c.Vis = IndicatorLooks.NewSlash(CombatSettings.Look);
            Vector3 chest = Chest(s);
            Vector3 to = head - chest;
            to.y = 0f;
            float dist = to.magnitude;
            Vector3 pos = chest + (dist > 1e-3f ? to / dist : Vector3.zero) * Mathf.Clamp(dist * 0.3f, 0.2f, 0.45f);
            pos.y = Mathf.Clamp(pos.y, head.y - 0.75f, head.y + 0.1f);
            // never within the spawn rule's 0.76 m of the eyes (candidate 8's boss: a big enemy close by put its slash at about
            // half a metre - a large flare on the left): 0.8 m out along the line from the eyes
            Vector3 fromEye = pos - head;
            if (fromEye.magnitude < MinFromEyes) { pos = head + (fromEye.sqrMagnitude > 1e-6f ? fromEye.normalized : view0(head, chest)) * MinFromEyes; NearPlaced++; }
            Vector3 view = (pos - head).normalized;
            Vector3 up = Vector3.up - view * Vector3.Dot(Vector3.up, view);
            if (up.sqrMagnitude < 1e-6f) return;
            up.Normalize();
            Vector3 right = Vector3.Cross(up, view);
            float a = c.Angle * Mathf.Deg2Rad;
            c.Axis = (right * Mathf.Cos(a) + up * Mathf.Sin(a)).normalized;
            if (!c.Vis.Root.gameObject.activeSelf) c.Vis.Root.gameObject.SetActive(true);
            c.Vis.Root.position = pos;
            c.Vis.Root.rotation = Quaternion.LookRotation(view, Vector3.Cross(view, c.Axis));
            float left = 1f - Mathf.Clamp01((now - c.Opened) / c.Duration);
            c.Vis.Draw(now - c.Opened, left, c.Result, c.Result != null ? now - c.ResultAt : 0f, now - c.WrongAt, c.Big, c.Interrupt);
        }

        // ---------------------------------------------------------------------------------------- test seams

        /// `poise? [enemy|nearest]`: that enemy's guard, phase, open crit (its line in tracking space, for a cut along it).
        public static string Query(string who)
        {
            if (I == null) return "no poise";
            Targetable t = Physical.FindEnemy(string.IsNullOrEmpty(who) ? "nearest" : who);
            if (t == null) return "no enemy";
            State s = I.Get(t);
            if (s == null) return "no state";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string crit = "-";
            if (s.Open != null)
            {
                Vector3 ax = VRRig.I != null ? VRRig.I.FromWorldDir(s.Open.Axis) : s.Open.Axis;
                crit = (s.Open.Big ? "break " : s.Open.Interrupt ? "interrupt " : "combo ") + s.Open.Angle.ToString("0", inv) + " left " + (s.Open.Duration - (FightClock.Now - s.Open.Opened)).ToString("0.00", inv) +
                       " axisT " + ax.x.ToString("0.000", inv) + "," + ax.y.ToString("0.000", inv) + "," + ax.z.ToString("0.000", inv) +
                       " at " + (s.Open.Vis != null ? Diag.V(s.Open.Vis.Root.position) : "-") +
                       (s.Open.Vis != null && VRRig.I != null ? " atT " + Diag.V(VRRig.I.FromWorld(s.Open.Vis.Root.position)) : "");
            }
            float aggr = 0f, lose = 0f;
            bool after = false;
            try { EnemyTable.BlockRules(s.C, out aggr, out after, out lose); } catch (Exception) { }
            return s.Key + " " + s.K.Name + " guard " + s.Guard.ToString("0.0", inv) + "/" + s.Max.ToString("0", inv) + " phase " + s.Ph + " step " + s.Step + " crit " + crit +
                   " stunned " + (s.C != null && s.C.IsStunned(0f)) + " held " + Guard.HeldFor(s.C).ToString("0.0", inv) + " hits3 " + HitsIn(s, 3f) + "/" + aggr.ToString("0.##", inv) +
                   " pool " + (s.Pool < 0f ? (SuperArmorOverride ?? s.K.SuperArmor) : s.Pool).ToString("0.0", inv) + "/" + (SuperArmorOverride ?? s.K.SuperArmor).ToString("0", inv) +
                   " blocking " + (s.C != null && s.C.Blocking) + " id #" + t.GetInstanceID() +
                   " tier " + s.K.Tier + " hitsToBreak " + s.K.Hits + " refill " + s.K.Refill.ToString("0.0", inv) + " regen " + (s.K.Regen * CombatSettings.EnemyGuard).ToString("0.00", inv) +
                   " max " + s.Max.ToString("0.00", inv);
        }

        /// `poiseint <enemy|nearest> [seconds]`: open an interrupt crit on it now, as between a combo's blows (its running
        /// strike is the combo it stops; tests).
        public static string OpenInterrupt(string who, float seconds)
        {
            if (I == null) return "no poise";
            Targetable t = Physical.FindEnemy(who);
            State s = t != null ? I.Get(t) : null;
            if (s == null) return "no enemy";
            if (s.Ph != Phase.Up) return "not up: " + s.Ph;
            if (s.Open != null) { s.Open.Result = "timeout"; s.Open.ResultAt = FightClock.Now; I.m_fading.Add(s.Open); s.Open = null; }
            OfferInterrupt(s.C, null, Mathf.Max(0.5f, seconds));
            // the seam's own window (the test's cut is slow), not tied to a strike the enemy may be in (it would close with it)
            if (s.Open != null) s.Open.Duration = Mathf.Max(0.5f, seconds);
            return Query("#" + t.GetInstanceID());
        }

        /// `blockcond <enemy>`: its block actions (ActionBlock, ActionPhalanxBlock, ActionHitByCount) and what decides them -
        /// the entry condition's and the block condition's parts (their types and whether they hold now): diagnostics for why
        /// a shield bearer does or does not block in Physical mode.
        public static string BlockConditions(string who)
        {
            Targetable t = Physical.FindEnemy(who);
            Actor actor = t != null ? t.GetComponent<Actor>() : null;
            if (actor == null) return "no enemy";
            var sb = new System.Text.StringBuilder(WeaponGrips.ModelKey(t.name) + " blocking " + (t.Controller != null && t.Controller.Blocking));
            foreach (Defiant.Action a in actor.GetComponentsInChildren<Defiant.Action>(true))
            {
                if (!(a is ActionBlock) && !(a is ActionPhalanxBlock) && !(a is ActionHitByCount)) continue;
                sb.Append(" | ").Append(a.GetType().Name).Append(a.IsRunning ? " (running)" : "").Append(" entry ").Append(Parts(a.EntryCondition));
                var dummy = HarmonyLib.AccessTools.Field(a.GetType(), "m_dummyBlock");
                if (dummy != null) sb.Append(" dummy ").Append(dummy.GetValue(a));
                var fallback = HarmonyLib.AccessTools.Field(a.GetType(), "m_blockAsFallback");
                if (fallback != null) sb.Append(" blockAsFallback ").Append(fallback.GetValue(a));
                var f = HarmonyLib.AccessTools.Field(a.GetType(), "m_blockCondition");
                if (f != null) sb.Append(" block ").Append(Parts(f.GetValue(a) as Condition));
            }
            return sb.ToString();
        }

        static string Parts(Condition c)
        {
            if (c == null) return "-";
            var sb = new System.Text.StringBuilder("[");
            foreach (Transform ch in c.transform)
            {
                Condition k = ch.GetComponent<Condition>();
                if (k == null) continue;
                bool ok = false;
                try { ok = k.Check(); } catch (Exception) { }
                sb.Append(k.GetType().Name).Append(ok ? "=1 " : "=0 ");
            }
            return sb.Append("]").ToString();
        }

        /// `poiseset <enemy|nearest> <guard>`: set an enemy's guard now (tests: break it at once with 0).
        public static string Set(string who, float guard)
        {
            if (I == null) return "no poise";
            Targetable t = Physical.FindEnemy(who);
            State s = t != null ? I.Get(t) : null;
            if (s == null) return "no enemy";
            if (guard < 0f)
            {
                // `poiseset <enemy> -1`: back to a full guard at once (shots of several looks in a row)
                if (s.Open != null) { if (s.Open.Vis != null) s.Open.Vis.Destroy(); s.Open = null; }
                if (s.Ph == Phase.Broken) Stun(s, false);
                s.Ph = Phase.Up;
                s.Guard = s.Max;
                s.PhaseAt = FightClock.Now;
                Guard.Hold(s.C, 0f);
                return Query(who);
            }
            if (s.Ph != Phase.Up) return "not up: " + s.Ph;
            if (guard <= 0f) { I.Chip(s, s.Guard + 1f, "seam"); return Query(who); }
            s.Guard = Mathf.Min(s.Max, guard);
            s.LastGuardHit = FightClock.Now;   // (env 3) a set guard holds like a hit: its refill waits the delay (fight time, as the regen check)
            return Query(who);
        }
    }
}

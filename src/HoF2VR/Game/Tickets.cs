using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Until You Fall's turn-taking tickets (docs/uyf-reference.md §3.11; combat Stage 2 item 5) on top of HoF2's own one
    /// melee attacker per target (AITarget.MeleeAttacker = UYF's Engaged ticket; its beat timer between attacks = UYF's
    /// cooldowns). What UYF adds, here:
    ///  * the ranking: the Engaged ticket goes to the fittest enemy (UYF: EngagementFitness x 1000 + (200 - distance) +
    ///    PlayerRelativeFitness) instead of a random one of the inner ring: "in position" (inner ring, within 2.8 m: UYF's
    ///    StartMeleeAttackRange) 100, "capable" 50, +10 off cooldown (not the last attacker), +20 in the player's view;
    ///    the runner-up is OnDeck (shown in `tickets?`);
    ///  * a 10 s possession limit (TicketPosessionTimer): an attacker that held the ticket that long gives it up;
    ///  * 0.25-0.5 s after gaining the ticket before its first strike (AttackCooldown_TicketGained; 0.35 s here);
    ///  * the swap grace by the number of enemies (UYF 2/3/4/5: 0.5/0.4/0.3/0.25 s), x1.2 on Normal (the user's "a little
    ///    slower than UYF"; Relaxed x1.8, Fast x1): CombatSettings.SwapGraceFor.
    /// Waiting enemies keep HoF2's own rings (UYF's 6-15 m standby distance would change every enemy type's ring data).
    internal static class Tickets
    {
        public static int Ranked, Kept, Expired, GainWaits, BlockLosses, ApproachTimeouts, Demotions;
        // UYF's approach time-out (item 8): the holder has come into reach; the stall clock (it restarts while the holder
        // closes in by Progress); the nearest it has been; its time-out
        static bool s_reached;
        static AIController s_approachOf;   // the turn the approach clock belongs to (null: the next holder starts afresh)
        static float s_approachClock, s_approachBest, s_approachLimit = 1f;
        public const float Progress = 0.2f;
        static readonly Dictionary<int, float> s_demoted = new Dictionary<int, float>();   // UYF's TicketSwapDemotionTimer: id -> until
        static float s_blockSince = -1f;
        public static string Last = "-";
        static readonly Dictionary<int, float> s_gained = new Dictionary<int, float>();
        static AIController s_holder;
        static float s_heldSince;
        public const float Possession = 10f, GainedCooldown = 0.35f, InPosition = 2.8f;

        public static void Apply(Harmony h)
        {
            try { h.Patch(AccessTools.Method(typeof(AITarget), "UpdateMeleeAttacker"), postfix: new HarmonyMethod(typeof(Tickets), nameof(PickPostfix))); }
            catch (Exception e) { Plugin.L.LogWarning("Tickets: patch failed: " + e.Message); }
            Diag.Commands["tickets?"] = a =>
            {
                if (a.Length > 2 && a[1] == "idle" && TestMode.Enabled) s_idleOff = a[2] == "off";
                // test: every turn's possession limit (s; "off" = UYF's by archetype) - short turns, where the idle bonus tells
                if (a.Length > 2 && a[1] == "possession" && TestMode.Enabled)
                    s_possession = a[2] == "off" ? -1f : float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                if (a.Length > 1 && a[1] == "reset") { Turns = Repeats = 0; s_turnEnded.Clear(); s_why.Clear(); }
                if (a.Length > 1 && a[1] == "why") return s_why.Count > 0 ? string.Join(" || ", s_why.ToArray()) : "no repeats";
                return Describe();
            };
        }

        public static string Describe()
        {
            string s = "ranked " + Ranked + " kept " + Kept + " expired " + Expired + " gainWaits " + GainWaits + " blockLosses " + BlockLosses + " approachTimeouts " + ApproachTimeouts + " demotions " + Demotions + " turns " + Turns + " repeats " + Repeats + " idleBonus " + (CombatSettings.WaitingEnemies && !s_idleOff) + " last " + Last;
            try
            {
                PlayerController pc = PlayerController.Instance;
                AITarget at = pc != null ? pc.GetComponent<Targetable>().AITarget : null;
                if (at != null)
                {
                    AIController on;
                    AIController best = Best(at, out on, true);
                    s += " | engaged " + (at.MeleeAttacker != null ? WeaponGrips.ModelKey(at.MeleeAttacker.name) + "#" + IdOf(at.MeleeAttacker) : "-") +
                         " for " + (s_holder != null && s_holder == at.MeleeAttacker ? (FightClock.Now - s_heldSince).ToString("0.0") : "0") + " s" +
                         " ondeck " + (on != null ? WeaponGrips.ModelKey(on.name) + "#" + IdOf(on) : "-") + " inner " + at.MeleeInner.size + " outer " + at.MeleeOuter.size;
                }
            }
            catch (Exception) { }
            return s;
        }

        /// The id the enemy seams take (`#<id>`): its Targetable's.
        static int IdOf(AIController ai) => ai.Targetable != null ? ai.Targetable.GetInstanceID() : ai.GetInstanceID();

        // UYF's idle bonus (its "Update Ticketing and Engagement Fitness" tree): losing the ticket starts the
        // TicketSwapInactivityBonusTimer (6 s) with the demotion timer; while neither runs the enemy's EngagementFitness is
        // +20, so that with 3 enemies the third one also gets its turn. So an enemy
        // whose turn ended less than 6 s ago ranks below the others of its fitness. With `[Combat] WaitingEnemies`.
        public const float IdleBonusAfter = 6f, IdleBonus = 20f;
        static readonly Dictionary<int, float> s_turnEnded = new Dictionary<int, float>();
        public static int Turns, Repeats;
        static bool s_idleOff;   // test seam `tickets? idle off|on`

        static void TurnEnded(AIController old)
        {
            if (old == null) return;
            if (s_turnEnded.Count > 64) s_turnEnded.Clear();
            s_turnEnded[old.GetInstanceID()] = FightClock.Now;
        }

        /// UYF: losing the ticket starts the demotion timer (its priority down for a husk 2 s, a knight 1) with the inactivity
        /// timer - also when a fitter enemy takes the turn (with the waiting enemies; as Lose).
        static void HandOver(AIController old)
        {
            TurnEnded(old);
            if (!CombatSettings.WaitingEnemies || old == null) return;
            try
            {
                if (s_demoted.Count > 64) s_demoted.Clear();
                s_demoted[old.GetInstanceID()] = FightClock.Now + EnemyTable.StandbyOf(old)[EnemyTable.SbDemotion];
                Demotions++;
            }
            catch (Exception) { }
        }

        static void NewTurn(AITarget at, AIController got)
        {
            Turns++;
            if (!Idle(got)) { Repeats++; Why(at, got); }   // its turn back less than 6 s after the last one ended
            s_holder = got;
            s_heldSince = FightClock.Now;
        }

        /// The holder's possession limit has run out (UYF's TicketPosessionTimer by archetype): its fitness is Low (10),
        /// so it hands its ticket on.
        static bool PastLimit(AIController ai)
        {
            try { return ai != null && ai == s_holder && FightClock.Now - s_heldSince >= PossessionOf(ai); }
            catch (Exception) { return false; }
        }

        static float s_possession = -1f;   // test seam `tickets? possession <s>|off`
        static float PossessionOf(AIController ai) => s_possession > 0f ? s_possession : EnemyTable.TicketOf(ai)[0];

        // `tickets? why`: the last repeats - the one that got its turn back and every other enemy's score (or why it could not)
        static readonly List<string> s_why = new List<string>();
        static void Why(AITarget at, AIController got)
        {
            try
            {
                Vector3 head = VRRig.I != null ? VRRig.I.HeadWorldPos : at.transform.position;
                Vector3 gaze = VRRig.I != null ? VRRig.I.HeadWorldRot * Vector3.forward : Vector3.forward;
                gaze.y = 0f;
                System.Text.StringBuilder b = new System.Text.StringBuilder();
                b.Append(FightClock.Now.ToString("0.0")).Append(" got #").Append(IdOf(got));
                for (int i = 0; i < at.AI.size; i++)
                {
                    AIController ai = at.AI[i];
                    if (ai == null) continue;
                    bool inner = at.MeleeInner.Contains(ai), outer = at.MeleeOuter.Contains(ai);
                    Vector3 d = ai.transform.position - head;
                    d.y = 0f;
                    float t;
                    b.Append(" | #").Append(IdOf(ai)).Append(' ').Append(d.magnitude.ToString("0.0")).Append(" m ").Append(inner ? "inner" : outer ? "outer" : "no ring");
                    if (!ai.CanAttackTarget) b.Append(" cannot");
                    if (Poise.Staggered(ai)) b.Append(" staggered");
                    if (Demoted(ai)) b.Append(" demoted");
                    b.Append(" ended ").Append(s_turnEnded.TryGetValue(ai.GetInstanceID(), out t) ? (FightClock.Now - t).ToString("0.0") + " s ago" : "never");
                    if (inner || outer) b.Append(" score ").Append((Score(at, ai, inner, head, gaze) / 1000f).ToString("0.0"));
                }
                if (s_why.Count >= 6) s_why.RemoveAt(0);
                s_why.Add(b.ToString());
            }
            catch (Exception) { }
        }

        static bool Idle(AIController ai)
        {
            float t;
            return !s_turnEnded.TryGetValue(ai.GetInstanceID(), out t) || FightClock.Now - t >= IdleBonusAfter;
        }

        static float Score(AITarget at, AIController ai, bool inner, Vector3 head, Vector3 gaze)
        {
            Vector3 d = ai.transform.position - head;
            d.y = 0f;
            float dist = d.magnitude;
            // UYF's fitness by archetype: High in position, Moderate when only capable, + AttackReadied when its own attack
            // is off cooldown (the knight 100 / 60 / +15, a spellsword 110 / 10 / +10, the base 100 / 50 / +10)
            float[] tk = EnemyTable.TicketOf(ai);
            float fit = inner && dist <= InPosition ? tk[1] : tk[2];
            if (!Guard.OnCooldown(ai)) fit += tk[3];
            if (PastLimit(ai)) fit = 10f;   // UYF's Low: giving its ticket up (the possession limit)
            if (CombatSettings.WaitingEnemies && !s_idleOff && Idle(ai)) fit += IdleBonus;
            float rel = gaze.sqrMagnitude > 1e-4f && d.sqrMagnitude > 1e-4f && Vector3.Angle(gaze, d) <= 60f ? 20f : 0f;
            float sc = fit * 1000f + (200f - dist) + rel;
            return Demoted(ai) ? sc - 1e7f : sc;   // UYF: after losing the ticket its priority is down for a while
        }

        static bool Demoted(AIController ai)
        {
            float until;
            return s_demoted.Count > 0 && CombatSettings.WaitingEnemies && s_demoted.TryGetValue(ai.GetInstanceID(), out until) && FightClock.Now < until;
        }

        /// UYF's OnDeck (Standby): the fittest enemy that can attack and is not the turn holder. The current one stays unless
        /// another beats it by a whole fitness step (not by being a little nearer: two waiters don't swap back and forth).
        public static AIController NextOf(AITarget at, AIController keep)
        {
            AIController best = null;
            float bs = float.MinValue, ks = float.MinValue;
            Vector3 head = VRRig.I != null ? VRRig.I.HeadWorldPos : at.transform.position;
            Vector3 gaze = VRRig.I != null ? VRRig.I.HeadWorldRot * Vector3.forward : Vector3.forward;
            gaze.y = 0f;
            for (int ring = 0; ring < 2; ring++)
            {
                BetterList<AIController> list = ring == 0 ? at.MeleeInner : at.MeleeOuter;
                for (int i = 0; i < list.size; i++)
                {
                    AIController ai = list[i];
                    // (the turn's holder too, between its attacks: HoF2's slot is empty then)
                    if (ai == null || ai == at.MeleeAttacker || ai == s_holder || !ai.CanAttackTarget || Poise.Staggered(ai)) continue;
                    float sc = Score(at, ai, ring == 0, head, gaze);
                    if (ai == keep) ks = sc;
                    if (sc > bs) { best = ai; bs = sc; }
                }
            }
            return keep != null && ks > float.MinValue && ks >= bs - 1000f ? keep : best;
        }

        /// The fittest enemy of the inner and outer rings that can attack (and the runner-up: OnDeck).
        static AIController Best(AITarget at, out AIController onDeck, bool includeHolder)
        {
            onDeck = null;
            AIController best = null;
            float bs = float.MinValue, os = float.MinValue;
            Vector3 head = VRRig.I != null ? VRRig.I.HeadWorldPos : at.transform.position;
            Vector3 gaze = VRRig.I != null ? VRRig.I.HeadWorldRot * Vector3.forward : Vector3.forward;
            gaze.y = 0f;
            for (int ring = 0; ring < 2; ring++)
            {
                BetterList<AIController> list = ring == 0 ? at.MeleeInner : at.MeleeOuter;
                for (int i = 0; i < list.size; i++)
                {
                    AIController ai = list[i];
                    if (ai == null || !ai.CanAttackTarget || Poise.Staggered(ai)) continue;
                    if (!includeHolder && ai == at.MeleeAttacker) continue;
                    float sc = Score(at, ai, ring == 0, head, gaze);
                    if (sc > bs) { onDeck = best; os = bs; best = ai; bs = sc; }
                    else if (sc > os) { onDeck = ai; os = sc; }
                }
            }
            return best;
        }

        /// HoF2 picked a melee attacker at random: UYF's ranking picks instead.
        static void PickPostfix(AITarget __instance)
        {
            try
            {
                if (!Physical.Live || __instance == null || __instance.MeleeAttacker == null) return;
                PlayerController pc = PlayerController.Instance;
                if (pc == null || __instance.Targetable == null || __instance.Targetable.gameObject != pc.gameObject) return;
                AIController on;
                AIController best = Best(__instance, out on, true);
                if (best != null && best != __instance.MeleeAttacker) { __instance.MeleeAttacker = best; Ranked++; }
                else Kept++;
                AIController got = __instance.MeleeAttacker;
                // a pick of the turn's holder after its own attack goes on with its turn (UYF holds the ticket across attacks:
                // no new TicketGained wait, the possession clock runs on); another enemy's pick is a new turn
                if (got != s_holder)
                {
                    s_gained[got.GetInstanceID()] = FightClock.Now;
                    if (s_holder != null)
                    {
                        if (PastLimit(s_holder)) Expired++;
                        HandOver(s_holder);
                    }
                    NewTurn(__instance, got);
                }
                Last = WeaponGrips.ModelKey(got.name) + "#" + IdOf(got) + (on != null ? " (on deck " + WeaponGrips.ModelKey(on.name) + ")" : "");
            }
            catch (Exception e) { Plugin.L.LogWarning("Tickets: pick: " + e.Message); }
        }

        /// Guard.HoldStrike: 0.35 s after gaining the ticket before the first strike.
        public static bool JustGained(AIController ai)
        {
            float at;
            if (ai == null || !s_gained.TryGetValue(ai.GetInstanceID(), out at)) return false;
            float wait = GainedCooldown;
            try { wait = EnemyTable.TicketWaitFor(ai); } catch (Exception) { }   // UYF's AttackCooldown_TicketGained by archetype and difficulty
            if (FightClock.Now - at < wait) { GainWaits++; return true; }
            return false;
        }

        static float Flat(AIController m, PlayerController pc)
        {
            Vector3 d = m.transform.position - pc.transform.position;
            d.y = 0f;
            return d.magnitude;
        }

        /// A new turn: the approach clock starts after UYF's wait before engaging (its TicketSwapGraceTimer, set from the
        /// grace by the enemy count: EnemyTable.GraceFor).
        static void StartApproach(AIController m, PlayerController pc)
        {
            s_reached = false;
            if (m == null) return;
            try
            {
                s_approachLimit = EnemyTable.StandbyOf(m)[EnemyTable.SbApproach];
                s_approachBest = Flat(m, pc);
                s_approachClock = FightClock.Now + EnemyTable.GraceFor(m, Physical.CountAlive());
            }
            catch (Exception) { s_approachLimit = 1f; s_approachClock = FightClock.Now; }
        }

        /// The turn taken from its holder (HoF2 picks the next on its next frame). With the waiting enemies on, as in UYF: the
        /// loser's priority is down for its TicketSwapDemotionTimer (a husk 2 s, a knight 1) and HoF2 counts it as the last
        /// attacker; off, as before item 8.
        static void Lose(AITarget at, AIController m, string why)
        {
            m.TriggerAttackerCleared();
            if (CombatSettings.WaitingEnemies)
            {
                at.LastMeleeAttacker = m;
                try
                {
                    if (s_demoted.Count > 64) s_demoted.Clear();
                    s_demoted[m.GetInstanceID()] = FightClock.Now + EnemyTable.StandbyOf(m)[EnemyTable.SbDemotion];
                    Demotions++;
                }
                catch (Exception) { }
            }
            at.MeleeAttacker = null;
            Last = why;
            TurnEnded(m);
            s_holder = null;
            s_approachOf = null;
            s_blockSince = -1f;
        }

        /// Every frame (Guard.LateUpdate): the waiting enemies' bands (Standby) and the ways a turn is lost - UYF's approach
        /// time-out, a block kept too long, the possession limit.
        public static void Tick()
        {
            try
            {
                if (!Physical.Live) { s_holder = null; Standby.RestoreAll(); return; }
                PlayerController pc = PlayerController.Instance;
                AITarget at = pc != null ? pc.GetComponent<Targetable>().AITarget : null;
                if (at == null) return;
                Standby.Tick(at);   // UYF's waiting enemies (item 8)
                Shoves.Tick(at);    // UYF's shoves (merge-ready #4)
                AIController m = at.MeleeAttacker;
                // the approach clock follows the turn itself: PickPostfix sets s_holder on its own, and a turn lost and handed back
                // to the same enemy is a new turn (MR3d: the clock was left from the turn before - the holder timed out on its
                // first frame, every frame: 2116 time-outs in 45 s)
                if (m != s_approachOf) { s_approachOf = m; StartApproach(m, pc); }
                // HoF2 empties the slot after each of the holder's attacks (AIManager.OnCombatantMeleeAttack) and picks again at its
                // next beat: the turn goes on (MR4b: a turn per attack - the possession limit never ran, expired 0, and an enemy
                // picked again after its own attack counted as a repeat and lost its idle bonus at every attack)
                if (m == null) return;
                if (m != s_holder)
                {
                    if (s_holder != null) HandOver(s_holder);
                    NewTurn(at, m);
                    s_blockSince = -1f;
                    return;
                }
                // UYF's MeleeApproachTimeOut (item 8, E12): the time an enemy gets to reach melee range before its Attack
                // Ticket goes to another - a husk 1 s, a knight 0.5, the Captain boss 3. UYF's enemies engage with a
                // forward dash, which that time covers; HoF2's walk in, so the clock runs while the holder is not closing in (it
                // restarts each time it comes 0.2 m nearer): a holder held up, or one the hero keeps away from, gives the turn up
                if (m != null && !s_reached && CombatSettings.WaitingEnemies)
                {
                    float d = Flat(m, pc);
                    float now = FightClock.Now;
                    if (d <= InPosition) s_reached = true;
                    else if (d < s_approachBest - Progress) { s_approachBest = d; if (now > s_approachClock) s_approachClock = now; }
                    else if (now - s_approachClock >= s_approachLimit && !m.Blocking && !Poise.Staggered(m) && !Guard.Shows(m) && Guard.RunningStrikeOf(m) == null)
                    {
                        ApproachTimeouts++;
                        Lose(at, m, "approach timed out: " + WeaponGrips.ModelKey(m.name) + " " + d.ToString("0.0") + " m away");
                        return;
                    }
                }
                // UYF's Block_LoseTicketTimer: a holder that keeps blocking gives up its turn (Knight 1 s, Captain 2 s, ...)
                if (m != null && m.Blocking)
                {
                    if (s_blockSince < 0f) s_blockSince = FightClock.Now;
                    float aggr, lose = 1f;
                    bool after;
                    try { EnemyTable.BlockRules(m, out aggr, out after, out lose); } catch (Exception) { }
                    if (FightClock.Now - s_blockSince >= lose && !Guard.Shows(m) && Guard.RunningStrikeOf(m) == null)
                    {
                        BlockLosses++;
                        Lose(at, m, "blocking cost the turn: " + WeaponGrips.ModelKey(m.name));
                        return;
                    }
                }
                else s_blockSince = -1f;
                // UYF's possession limit by archetype (a knight 8 s, a husk 7, a captain 6, spellswords and bosses 4; base 10)
                if (m == null || FightClock.Now - s_heldSince < PossessionOf(m)) return;
                // not in the middle of a strike (UYF: a locked ticket is not taken)
                if (Guard.Shows(m) || Guard.RunningStrikeOf(m) != null) return;
                Expired++;
                Lose(at, m, "expired " + WeaponGrips.ModelKey(m.name));
            }
            catch (Exception) { }
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// Every HoF2 enemy family mapped to the Until You Fall enemy it fights like (combat Stage 2 item 9; data/enemy_uyf.tsv,
    /// generated into EnemyTableData.cs by scripts/enemy_table.py): its guard, refill, hit and blocked multipliers, super
    /// armor and crit timings (Poise), and per difficulty step (CombatSettings.Difficulty, UYF's own numbers; Normal = UYF's
    /// Normal): the lead from its telegraph to its blow (Threats, Guard), its attack speed (EnemyTuning), the pause between
    /// its attacks and after it gains the ticket, the swap grace (Guard, Tickets), its guard and refill (Poise). A model-name prefix picks
    /// the family (the longest wins); a boss without its own row is UYF's Aether Horror; anything else goes by its HoF2
    /// class (Basic husk, Advanced knight, Elite captain).
    internal static class EnemyTable
    {
        public sealed class Arch
        {
            public int Index;                       // its row in EnemyTableData.Steps
            public readonly string Name;
            public readonly float Guard, Regen, Delay, HitMult, BlockMult, SuperArmor, FirstCrit, ComboCrit, MinCrit, Finished, InterruptCrit, Lead, AttackSpeed;
            public Arch(string name, float guard, float regen, float delay, float hit, float blocked, float superArmor, float firstCrit, float comboCrit, float minCrit,
                        float finished, float interrupt, float lead, float speed)
            {
                Name = name; Guard = guard; Regen = regen; Delay = delay; HitMult = hit; BlockMult = blocked; SuperArmor = superArmor;
                FirstCrit = firstCrit; ComboCrit = comboCrit; MinCrit = minCrit; Finished = finished; InterruptCrit = interrupt; Lead = lead; AttackSpeed = speed;
            }
        }

        static Dictionary<string, Arch> s_arch;
        static readonly Dictionary<string, KeyValuePair<Arch, string>> s_cache = new Dictionary<string, KeyValuePair<Arch, string>>();

        static Arch Named(string name)
        {
            if (s_arch == null)
            {
                s_arch = new Dictionary<string, Arch>();
                for (int i = 0; i < EnemyTableData.Archetypes.Length; i++)
                {
                    Arch a = EnemyTableData.Archetypes[i];
                    a.Index = i;
                    s_arch[a.Name] = a;
                }
            }
            Arch r;
            return s_arch.TryGetValue(name, out r) ? r : s_arch["husk"];
        }

        /// The archetype of an enemy model (WeaponGrips.ModelKey), and how it was found ("family X", "boss", "class Y").
        public static Arch For(string model, AIClassType cls, bool boss, out string how)
        {
            string key = (model ?? "") + "|" + cls + "|" + boss;
            KeyValuePair<Arch, string> hit;
            if (s_cache.TryGetValue(key, out hit)) { how = hit.Value; return hit.Key; }
            Arch a = null;
            how = "";
            foreach (string[] f in EnemyTableData.Families)
                if (!string.IsNullOrEmpty(model) && model.StartsWith(f[0], StringComparison.Ordinal)) { a = Named(f[1]); how = "family " + f[0]; break; }
            if (a == null && boss) { a = Named("horror"); how = "boss"; }
            if (a == null) { a = Named(cls == AIClassType.Elite ? "captain" : cls == AIClassType.Advanced ? "knight" : "husk"); how = "class " + cls; }
            s_cache[key] = new KeyValuePair<Arch, string>(a, how);
            return a;
        }

        /// The archetype of this enemy (its AI's class and boss flag for the fallback).
        public static Arch Of(Controller c, out string how)
        {
            AIClassType cls = AIClassType.Basic;
            bool boss = false;
            string model = "";
            try
            {
                AI ai = c != null ? c.GetComponent<AI>() : null;
                if (ai != null && ai.Ref != null) { cls = ai.Ref.Class; boss = ai.Ref.Boss; }
                model = WeaponGrips.ModelKey(c != null && c.Model != null ? c.Model.name : c != null ? c.name : "");
            }
            catch (Exception) { }
            return For(model, cls, boss, out how);
        }

        // ---- the guard's size and refill (the user's rulings of 2026-10-18, after playtest 3; they replace UYF's sizes) ----
        // "The easiest enemies should have guards breaking in about 2-3 hits"; "guards should take about 10 seconds to regenerate
        // on easy-normal enemies ... and never less than 4 seconds". The size comes from the game's own tier of the enemy (its AI's
        // class: Basic, Advanced, Elite; bosses): that many standard hits of the arming sword (the basic one-handed weapon; the
        // basic axe takes about a third fewer) at Normal, through the archetype's hit multiplier, so a soldier and a thief of the
        // same tier take the same number of hits. UYF's difficulty steps keep their ratios (Nightmare guards are bigger, Easy
        // refills slower); no step refills a guard faster than MinRefill. The lesser husks (gnomes, nabbers) keep UYF's 5 (a hit).

        /// The weapon guard damage the hit counts are measured with (WeaponTable.ArmingSword).
        public const float RefWeaponGuard = 7f;
        /// No guard refills from empty to full in less than this (s, any step).
        public const float MinRefill = 4f;

        /// Standard sword hits that break a guard of this tier at Normal.
        public static int HitsFor(AIClassType cls, bool boss) => boss ? 10 : cls == AIClassType.Elite ? 7 : cls == AIClassType.Advanced ? 5 : 3;
        /// Its full refill at Normal (s): basic and advanced 10, elites 8, bosses 6.
        public static float RefillFor(AIClassType cls, bool boss) => boss ? 6f : cls == AIClassType.Elite ? 8f : 10f;

        /// The game's tier of this enemy (its AI's class) and whether it is a boss.
        public static void TierOf(Controller c, out AIClassType cls, out bool boss)
        {
            cls = AIClassType.Basic;
            boss = false;
            try
            {
                AI ai = c != null ? c.GetComponent<AI>() : null;
                if (ai != null && ai.Ref != null) { cls = ai.Ref.Class; boss = ai.Ref.Boss; }
            }
            catch (Exception) { }
        }

        /// This enemy's guard at the current step, its regen (per s) and the numbers behind them.
        public static float GuardOf(Arch a, Controller c, out float regen, out int hits, out float refill, out string tier)
        {
            AIClassType cls;
            bool boss;
            TierOf(c, out cls, out boss);
            tier = boss ? "boss" : cls.ToString();
            float[] st = StepOf(a), nm = EnemyTableData.Steps[Mathf.Clamp(a.Index, 0, EnemyTableData.Steps.Length - 1)][1];
            float guard;
            if (a.Name == "lesserhusk") { hits = 1; guard = st[FGuard]; }
            else
            {
                hits = HitsFor(cls, boss);
                // just under `hits` hits (and over hits - 1), scaled as UYF scales this archetype's guard at the step
                guard = (RefWeaponGuard * a.HitMult * hits - 0.5f) * (nm[FGuard] > 0f ? st[FGuard] / nm[FGuard] : 1f);
            }
            float uyfNow = st[FGuard] / Mathf.Max(0.01f, st[FRegen]), uyfNormal = nm[FGuard] / Mathf.Max(0.01f, nm[FRegen]);
            refill = Mathf.Max(MinRefill, RefillFor(cls, boss) * (uyfNormal > 0f ? uyfNow / uyfNormal : 1f));
            regen = guard / refill;
            return guard;
        }

        // ---- UYF's difficulty, per archetype and step (EnemyTableData.Steps; CombatSettings.Difficulty) ----
        public const int FLead = 0, FSpeed = 1, FCdShort = 2, FCdMed = 3, FCdLong = 4, FTicket = 5, FGrace2 = 6, FGuard = 10, FRegen = 11, FOffscreen = 12;

        /// This archetype's numbers at the current step (Easy .. Nightmare 3).
        public static float[] StepOf(Arch a)
        {
            if (s_arch == null) Named("husk");
            float[][] rows = EnemyTableData.Steps[Mathf.Clamp(a.Index, 0, EnemyTableData.Steps.Length - 1)];
            return rows[Mathf.Clamp(CombatSettings.Step, 0, rows.Length - 1)];
        }

        static float[] StepOf(Controller c)
        {
            string how;
            return StepOf(Of(c, out how));
        }

        /// The lead every strike of this enemy gets from its telegraph to its blow (s): UYF's PremonitionDuration for its
        /// archetype at the difficulty (Normal = UYF's Normal).
        public static float LeadFor(Controller c) => StepOf(c)[FLead];

        /// Its attack speed at the difficulty (UYF's AttackSpeed: Controller.CalculateAttackSpeed x this; EnemyTuning).
        public static float SpeedFor(Controller c) => StepOf(c)[FSpeed];

        /// The least time between two strikes of this enemy (UYF's AttackCooldown_Med at the difficulty).
        public static float CooldownFor(Controller c) => StepOf(c)[FCdMed];
        /// One of its three pauses after an attack (UYF's AttackCooldown_Short / Med / Long at the difficulty): 0, 1, 2.
        public static float PauseFor(Controller c, int which) => StepOf(c)[which <= 0 ? FCdShort : which == 1 ? FCdMed : FCdLong];

        /// The wait after this enemy gains the ticket before its first strike (UYF's AttackCooldown_TicketGained).
        public static float TicketWaitFor(Controller c) => StepOf(c)[FTicket];

        /// After a blow, this enemy (a different attacker) waits this long before it may start (UYF's TicketSwapGraceTime by
        /// the number of enemies: 2, 3, 4, 5+).
        public static float GraceFor(Controller c, int enemies) => StepOf(c)[FGrace2 + Mathf.Clamp(enemies - 2, 0, 3)];

        /// The least time between two shots or throws in the fight (UYF's ranged cooldown with several enemies about,
        /// RangedAttackCooldown_MultiEnemy - only its spellswords shoot: Easy 10, Normal 3, Hard 2, Nightmares 1 s; the elite 9
        /// / 3 / 4 / 3 s), counted from the last frame any enemy's ranged strike ran (Guard.HoldStrike).
        public static float RangedCooldownFor(Controller c)
        {
            string how;
            Arch a = Of(c, out how);
            int st = Mathf.Clamp(CombatSettings.Step, 0, 5);
            return a.Name == "elitespellsword" ? new[] { 9f, 3f, 4f, 3f, 3f, 3f }[st] : new[] { 10f, 3f, 2f, 1f, 1f, 1f }[st];
        }

        /// UYF's blocking for this enemy's archetype (EnemyTableData.Block): the hits in the last 3 s it takes before it
        /// blocks, whether it also blocks 0.5 s after its own attack (the captains), how long a block may last before it costs
        /// the enemy its turn (s).
        public static void BlockRules(Controller c, out float aggression, out bool afterAttack, out float loseTicket)
        {
            string how;
            Arch a = Of(c, out how);
            float[] r = EnemyTableData.Block[Mathf.Clamp(a.Index, 0, EnemyTableData.Block.Length - 1)];
            aggression = r[0];
            afterAttack = r[1] > 0.5f;
            loseTicket = r[2];
        }

        /// UYF's waiting enemy for this enemy's archetype (EnemyTableData.Standby, item 8): on deck and standby - distance, edge,
        /// bullseye (m) -, the approach time-out (s), the priority drop after losing the ticket (s).
        public const int SbOnDeck = 0, SbStandby = 3, SbApproach = 6, SbDemotion = 7;
        public static float[] StandbyOf(Controller c)
        {
            string how;
            Arch a = Of(c, out how);
            return EnemyTableData.Standby[Mathf.Clamp(a.Index, 0, EnemyTableData.Standby.Length - 1)];
        }

        /// UYF's StrafeWaitTimer for this enemy's archetype (s).
        public static float StrafeWaitOf(Controller c)
        {
            string how;
            Arch a = Of(c, out how);
            return EnemyTableData.StrafeWait[Mathf.Clamp(a.Index, 0, EnemyTableData.StrafeWait.Length - 1)];
        }

        /// UYF's shove for this enemy's archetype (EnemyTableData.Shove): time inside (s), range (m), push (m).
        public static float[] ShoveOf(Controller c)
        {
            string how;
            Arch a = Of(c, out how);
            return EnemyTableData.Shove[Mathf.Clamp(a.Index, 0, EnemyTableData.Shove.Length - 1)];
        }

        /// UYF's ticket numbers for this enemy's archetype (EnemyTableData.Ticket): possession (s), fitness High / Moderate,
        /// the bonus when its attack is off cooldown.
        public static float[] TicketOf(Controller c)
        {
            string how;
            Arch a = Of(c, out how);
            return EnemyTableData.Ticket[Mathf.Clamp(a.Index, 0, EnemyTableData.Ticket.Length - 1)];
        }

        /// `enemytable? [enemy]`: the table, or one enemy's archetype and why.
        public static string Query(string who)
        {
            if (who == "all")
            {
                // every enemy kind in the fight, once
                PlayerController pc = PlayerController.Instance;
                if (pc == null || pc.Targetable == null) return "no fight";
                var seen = new HashSet<string>();
                var sb = new System.Text.StringBuilder();
                BetterList<Targetable> all = pc.Targetable.EnumerateWithinRadius(60f, pc.Targetable.GetEnemyTeams());
                for (int i = 0; i < all.size; i++)
                {
                    Targetable t = all[i];
                    if (t == null || t.Controller == null) continue;
                    string model = WeaponGrips.ModelKey(t.Controller.Model != null ? t.Controller.Model.name : t.name);
                    if (!seen.Add(model)) continue;
                    string how;
                    Arch a = Of(t.Controller, out how);
                    float[] st = StepOf(a);
                    float rg, rf;
                    int hn;
                    string tr;
                    float gd = GuardOf(a, t.Controller, out rg, out hn, out rf, out tr);
                    sb.Append(model).Append(": ").Append(a.Name).Append(" (").Append(how).Append(", ").Append(tr).Append(") guard ").Append(gd.ToString("0.0")).Append(" hits ").Append(hn).Append(" refill ").Append(rf.ToString("0.0")).Append(" lead ")
                      .Append(st[FLead].ToString("0.00")).Append(" speed ").Append(st[FSpeed].ToString("0.00")).Append(" | ");
                }
                return sb.Length > 0 ? sb.ToString() : "no enemies";
            }
            if (!string.IsNullOrEmpty(who))
            {
                Targetable t = Physical.FindEnemy(who);
                if (t == null || t.Controller == null) return "no enemy";
                string how;
                Arch a = Of(t.Controller, out how);
                float[] st = StepOf(a);
                float rg, rf;
                int hn;
                string tr;
                float gd = GuardOf(a, t.Controller, out rg, out hn, out rf, out tr);
                float live = 1f;
                try { live = t.Controller.CalculateAttackSpeed(); } catch (Exception) { }
                return WeaponGrips.ModelKey(t.Controller.Model != null ? t.Controller.Model.name : t.name) + ": " + a.Name + " (" + how + ") at " + CombatSettings.Difficulty +
                       ": " + tr + " guard " + gd.ToString("0.0") + " (" + hn + " sword hits) regen " + rg.ToString("0.00") + " (full in " + rf.ToString("0.0") + " s; UYF's guard " + st[FGuard] + " regen " + st[FRegen] + ") lead " + st[FLead].ToString("0.00") + " s speed " + st[FSpeed].ToString("0.00") +
                       " (live " + live.ToString("0.00") + ") cooldown " + st[FCdMed].ToString("0.00") + " pauses " + st[FCdShort].ToString("0.00") + "/" + st[FCdMed].ToString("0.00") + "/" + st[FCdLong].ToString("0.00") + " ticket " + st[FTicket].ToString("0.00") +
                       " grace " + st[FGrace2].ToString("0.00") + "-" + st[FGrace2 + 3].ToString("0.00") + " super armor " + a.SuperArmor;
            }
            return EnemyTableData.Families.Length + " families, " + EnemyTableData.Archetypes.Length + " archetypes";
        }
    }
}

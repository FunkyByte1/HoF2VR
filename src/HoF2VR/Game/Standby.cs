using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace HoF2VR
{
    /// UYF's waiting enemies (docs/uyf-parity-audit.md E13; item 8; `[Combat] WaitingEnemies`). UYF keeps the enemy next in
    /// turn (its OnDeck: Tickets.NextOf) at its GuardedRange and the others at their InWaitingRange - a husk or a knight 4 and
    /// 6 m, a spellsword 8 and 10, the Aether Horror 10 and 15 (EnemyTableData.Standby) -, each a band: it moves once past the
    /// band's edge (0.3 or 0.5 m) and stops inside its bullseye (0.15 or 0.3 m). The enemy whose turn it is comes in as before.
    /// HoF2 keeps every melee waiter between its faction's InnerRadius and OuterRadius (4-10 m), backing it off only inside
    /// TargetRadius (2 m): each enemy gets its own copy of its faction's ring data, set every frame for its role - TargetRadius
    /// the band's inner edge (it backs off inside it), InnerRadius the bullseye's inner side (backing off stops there),
    /// OuterRadius the outer edge (it comes in beyond it). The turn holder (and, until the next pick, the enemy whose turn just
    /// ended) and ranged enemies keep the game's numbers; the game's data comes back when the setting is off or the fight ends.
    internal static class Standby
    {
        static readonly AccessTools.FieldRef<AIController, AIManager.AIManagerData> s_data = AccessTools.FieldRefAccess<AIController, AIManager.AIManagerData>("m_aiData");
        static readonly AccessTools.FieldRef<AIManager.AIManagerData, float> s_target = AccessTools.FieldRefAccess<AIManager.AIManagerData, float>("m_targetRadius");
        static readonly AccessTools.FieldRef<AIManager.AIManagerData, float> s_inner = AccessTools.FieldRefAccess<AIManager.AIManagerData, float>("m_innerRadius");
        static readonly AccessTools.FieldRef<AIManager.AIManagerData, float> s_outer = AccessTools.FieldRefAccess<AIManager.AIManagerData, float>("m_outerRadius");
        static readonly AccessTools.FieldRef<AIManager.AIManagerData, float> s_ranged = AccessTools.FieldRefAccess<AIManager.AIManagerData, float>("m_rangedRadius");
        static readonly AccessTools.FieldRef<AIManager.AIManagerData, float> s_relax = AccessTools.FieldRefAccess<AIManager.AIManagerData, float>("m_relaxRadius");

        const int RoleGame = 0, RoleAttacker = 1, RoleOnDeck = 2, RoleStandby = 3;
        static readonly string[] s_roleNames = { "game", "attacker", "ondeck", "standby" };

        sealed class Own
        {
            public AIController Ai;
            public AIManager.AIManagerData Game, Mine;
            public float[] Row;          // its EnemyTableData.Standby row
            public int Role = -1;
            public float RoleSince;
            public float StrafeFrom;     // Time.time its strafe clock (UYF's StrafeWaitTimer) last started
        }

        static readonly Dictionary<int, Own> s_own = new Dictionary<int, Own>();
        static readonly List<int> s_gone = new List<int>();
        static AIController s_onDeck;
        static float s_nextRank;
        public static int Copies, Retakes, Restores, RoleChanges, OnDeckChanges;

        /// The enemy next in turn (UYF's OnDeck), or null.
        public static AIController OnDeck => s_onDeck;

        // UYF's strafe (E13, merge-ready #4): a waiting enemy (on deck or standby) steps aside after its StrafeWaitTimer (a
        // knight 3 s, a husk 4, a lesser husk 6, the boss 6: EnemyTableData.StrafeWait) - HoF2's own idle step (ActionMoveAI,
        // every 3-4 s, any way) timed by it and sideways only. The turn holder and `WaitingEnemies = false` keep HoF2's steps.
        // UYF's knight tree (sharedassets11: "Attempt Strafe" and the waiting-range branches) starts that timer at aggro and
        // again as each strafe starts - nowhere else: a move back into the band does not restart it. HoF2's SetIdle (after
        // every move, flee, step, block or taunt) set it afresh, so a waiter corrected often never strafed (test reliability
        // 2026-10-17 round 3: strafe 3/7 once the back-off worked). The clock is the enemy's own (from its first frame in
        // the fight, then from each strafe); an idle waiter steps as soon as it is due.
        static readonly AccessTools.FieldRef<ActionMoveAI, float> s_nextStep = AccessTools.FieldRefAccess<ActionMoveAI, float>("m_nextStepTime");
        static readonly AccessTools.FieldRef<ActionMoveAI, AIController> s_mover = AccessTools.FieldRefAccess<ActionMoveAI, AIController>("m_controller");
        public static int Strafes, StrafesTimed, StrafesLong;
        public static float StrafeGapMin = 99f, StrafeGapMax;
        public static string LastStrafe = "-";

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ActionMoveAI), "SetIdle"), postfix: new HarmonyMethod(typeof(Standby), nameof(IdlePostfix)));
                h.Patch(AccessTools.Method(typeof(ActionMoveAI), "CanStep"), postfix: new HarmonyMethod(typeof(Standby), nameof(CanStepPostfix)));
                h.Patch(AccessTools.Method(typeof(ActionMoveAI), "SetStep"), postfix: new HarmonyMethod(typeof(Standby), nameof(StepPostfix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("Standby: strafe patch failed: " + e.Message); }
            try { h.Patch(AccessTools.Method(typeof(ActionMoveAI), "UpdateFleeDestination"), postfix: new HarmonyMethod(typeof(Standby), nameof(FleePostfix))); }
            catch (Exception e) { Plugin.L.LogWarning("Standby: back-off patch failed: " + e.Message); }
            try { h.Patch(AccessTools.Method(typeof(ActionMoveAI), "SetMoveToIdle"), prefix: new HarmonyMethod(typeof(Standby), nameof(StopPrefix)), postfix: new HarmonyMethod(typeof(Standby), nameof(StopPostfix))); }
            catch (Exception e) { Plugin.L.LogWarning("Standby: on-deck stop patch failed: " + e.Message); }
        }

        /// An enemy waiting its turn (on deck or standby) with the waiting enemies on.
        public static bool Waiter(AIController ai)
        {
            Own o;
            return ai != null && CombatSettings.WaitingEnemies && Physical.Live && s_own.TryGetValue(ai.GetInstanceID(), out o) && (o.Role == RoleOnDeck || o.Role == RoleStandby);
        }

        static void IdlePostfix(ActionMoveAI __instance)
        {
            try
            {
                AIController ai = s_mover(__instance);
                Own o;
                if (!Waiter(ai) || !s_own.TryGetValue(ai.GetInstanceID(), out o)) return;
                s_nextStep(__instance) = Mathf.Max(Time.time, o.StrafeFrom + EnemyTable.StrafeWaitOf(ai));
                StrafesTimed++;
            }
            catch (Exception) { }
        }

        // its timed step goes sideways (ActionMoveAI.Step: Left 1, Right 2, Forward 4, Backward 8); one forced by standing too
        // near the hero (before its time) may still step back
        static void CanStepPostfix(ActionMoveAI __instance, object[] __args, ref bool __result)
        {
            if (!__result) return;
            try
            {
                int step = Convert.ToInt32(__args[0]);
                if ((step == 4 || step == 8) && Time.time >= s_nextStep(__instance) && Waiter(s_mover(__instance))) __result = false;
            }
            catch (Exception) { }
        }

        static void StepPostfix(ActionMoveAI __instance, object[] __args)
        {
            try
            {
                AIController ai = s_mover(__instance);
                Own o;
                if (!Waiter(ai) || !s_own.TryGetValue(ai.GetInstanceID(), out o)) return;
                int step = Convert.ToInt32(__args[0]);
                float gap = Time.time - o.StrafeFrom;   // since its clock started (UYF's StrafeWaitTimer)
                if (step == 1 || step == 2) o.StrafeFrom = Time.time;   // a strafe starts the clock again
                Strafes++;
                if (step == 4 || step == 8) StrafesLong++;
                if (gap >= 0f) { StrafeGapMin = Mathf.Min(StrafeGapMin, gap); StrafeGapMax = Mathf.Max(StrafeGapMax, gap); }
                LastStrafe = WeaponGrips.ModelKey(ai.name) + " " + (step == 1 ? "left" : step == 2 ? "right" : step == 4 ? "forward" : "back") + " after " + gap.ToString("0.0") + " s (its wait " + EnemyTable.StrafeWaitOf(ai).ToString("0.0") + ")";
            }
            catch (Exception) { }
        }

        // UYF's band moves its enemy back toward its centre once it is inside the band's edge (E13). HoF2's waiter backs off
        // (ActionMoveAI's flee, inside TargetRadius = the band's inner edge, until InnerRadius = the bullseye) to the point of its
        // 2 m nav grid nearest to it within TargetRadius-OuterRadius of the hero - in the on-deck band (0.6 m wide) often none:
        // it then "flees" to where it stands (test reliability 2026-10-17: the on-deck thief at 3.1-3.5 m of its 3.7-4.3 band,
        // every too-near sample in Flee). A waiter's back-off goes straight back from the hero to its band's middle (on the
        // navmesh) when that line reaches the band; else the game's pick when it found one; else as far back along the line as
        // the navmesh goes (a wall or the arena's edge behind it).
        static readonly AccessTools.FieldRef<ActionMoveAI, Vector3> s_fleeTo = AccessTools.FieldRefAccess<ActionMoveAI, Vector3>("m_fleeDestination");
        public static int BackOffs, BackOffsKept, KeptPick, KeptStay;
        public static string LastBackOff = "-", LastKept = "-";

        static void FleePostfix(ActionMoveAI __instance)
        {
            try
            {
                AIController ai = s_mover(__instance);
                Own o;
                if (!Waiter(ai) || !s_own.TryGetValue(ai.GetInstanceID(), out o) || o.Row == null) return;
                Targetable me = ai.Targetable, hero = ai.Target;
                if (me == null || hero == null) return;
                int k = o.Role == RoleOnDeck ? EnemyTable.SbOnDeck : EnemyTable.SbStandby;
                float mid = o.Row[k];
                Vector3 from = hero.transform.position, at = me.transform.position;
                Vector3 away = at - from;
                away.y = 0f;
                if (away.sqrMagnitude < 1e-4f) return;
                // the game's measure takes the hero's radius off (Targetable.Distance): the band's middle by it
                float r = hero.UseRadiusInCombat ? hero.Radius : 0f;
                Vector3 want = from + away.normalized * Mathf.Sqrt(mid * mid + r * r);
                NavMeshHit hit;
                Vector3 to;
                if (!NavMesh.Raycast(at, want, out hit, NavMesh.AllAreas)) to = want;   // the whole way is clear
                else to = hit.position;                                                  // as far as it goes
                if (NavMesh.SamplePosition(to, out hit, 0.5f, NavMesh.AllAreas)) to = hit.position;
                Vector3 d0 = at - from, d1 = to - from, game = s_fleeTo(__instance);
                d0.y = d1.y = 0f;
                float reach = Mathf.Sqrt(Mathf.Max(0f, d1.sqrMagnitude - r * r));   // the game's measure at that point
                bool gameNone = (game - at).sqrMagnitude < 0.04f;                     // its grid had no point in the band
                if (reach < mid - o.Row[k + 1] && (!gameNone || d1.magnitude < d0.magnitude + 0.25f))
                {
                    BackOffsKept++;
                    if (gameNone) KeptStay++; else KeptPick++;
                    Vector3 dg = game - from;
                    dg.y = 0f;
                    LastKept = WeaponGrips.ModelKey(ai.name) + " " + s_roleNames[o.Role] + " at " + me.Distance(hero).ToString("0.00") + " m line " + reach.ToString("0.00") +
                               (gameNone ? " stays" : " pick " + Mathf.Sqrt(Mathf.Max(0f, dg.sqrMagnitude - r * r)).ToString("0.00")) + " m";
                    return;
                }
                s_fleeTo(__instance) = to;
                BackOffs++;
                LastBackOff = WeaponGrips.ModelKey(ai.name) + " " + s_roleNames[o.Role] + " from " + me.Distance(hero).ToString("0.00") + " m to " +
                              Mathf.Sqrt(Mathf.Max(0f, d1.sqrMagnitude - r * r)).ToString("0.00") + " m";
            }
            catch (Exception) { }
        }

        // UYF's band "stops it inside the bullseye" (E13). HoF2 stops a mover through its MoveToIdle run-out (m_useTransitions:
        // the move speed eased to 0 over ~1 s of root motion), which carried a thief 0.6-1.3 m on past where ActionMoveAI
        // decided to stop (test reliability round 3: a flee stopped at 3.86 m ended at 4.6-4.7, a move in stopped at 4.0-4.3
        // ended at 2.9-3.2) - wider than the on-deck band (0.6 m), so the on-deck waiter swung between Flee and Move every 4 s
        // and never settled. The on-deck waiter stops at once (SetIdle, as an enemy without transitions does); the standby
        // band (1 m, its edge 0.5) and every other mover keep the run-out.
        static readonly AccessTools.FieldRef<ActionMoveAI, bool> s_transitions = AccessTools.FieldRefAccess<ActionMoveAI, bool>("m_useTransitions");
        public static int QuickStops;

        static void StopPrefix(ActionMoveAI __instance, ref bool __state)
        {
            __state = false;
            try
            {
                AIController ai = s_mover(__instance);
                Own o;
                if (!s_transitions(__instance) || !Waiter(ai) || !s_own.TryGetValue(ai.GetInstanceID(), out o) || o.Role != RoleOnDeck) return;
                s_transitions(__instance) = false;
                __state = true;
                QuickStops++;
            }
            catch (Exception) { }
        }

        static void StopPostfix(ActionMoveAI __instance, bool __state)
        {
            if (__state) s_transitions(__instance) = true;
        }

        public static void ResetStrafes() { Strafes = StrafesTimed = StrafesLong = 0; StrafeGapMin = 99f; StrafeGapMax = 0f; LastStrafe = "-"; }

        static Own Get(AIController ai)
        {
            AIManager.AIManagerData cur = s_data(ai);
            if (cur == null) return null;
            int id = ai.GetInstanceID();
            Own o;
            if (s_own.TryGetValue(id, out o))
            {
                if (!ReferenceEquals(cur, o.Mine)) { o.Game = cur; s_data(ai) = o.Mine; Retakes++; }   // the game set its data again
                return o;
            }
            o = new Own { Ai = ai, Game = cur, Mine = new AIManager.AIManagerData(), Row = EnemyTable.StandbyOf(ai), StrafeFrom = Time.time };
            s_own[id] = o;
            s_data(ai) = o.Mine;
            Copies++;
            return o;
        }

        static void Set(Own o, int role)
        {
            AIManager.AIManagerData g = o.Game, m = o.Mine;
            s_ranged(m) = s_ranged(g);
            s_relax(m) = s_relax(g);
            if (role == RoleOnDeck || role == RoleStandby)
            {
                int k = role == RoleOnDeck ? EnemyTable.SbOnDeck : EnemyTable.SbStandby;
                float d = o.Row[k], edge = o.Row[k + 1], bull = o.Row[k + 2];
                s_target(m) = d - edge;
                s_inner(m) = d - bull;
                s_outer(m) = d + edge;
            }
            else
            {
                s_target(m) = s_target(g);
                s_inner(m) = s_inner(g);
                s_outer(m) = s_outer(g);
            }
            if (role != o.Role) { o.Role = role; o.RoleSince = Time.unscaledTime; RoleChanges++; }
        }

        /// Every frame (Tickets.Tick): each enemy's band by its role.
        public static void Tick(AITarget at)
        {
            if (at == null || !CombatSettings.WaitingEnemies || !Physical.Live) { RestoreAll(); return; }
            float now = Time.unscaledTime;
            if (now >= s_nextRank)
            {
                s_nextRank = now + 0.2f;
                AIController next = Tickets.NextOf(at, s_onDeck);
                if (next != s_onDeck) { s_onDeck = next; OnDeckChanges++; }
                Sweep(at);
            }
            AIController holder = at.MeleeAttacker != null ? at.MeleeAttacker : at.LastMeleeAttacker;
            BetterList<AIController> all = at.AI;
            for (int i = 0; i < all.size; i++)
            {
                AIController ai = all[i];
                if (ai == null) continue;
                Own o = Get(ai);
                if (o == null) continue;
                Set(o, ai.Mode != AIMode.Melee ? RoleGame : ai == holder ? RoleAttacker : ai == s_onDeck ? RoleOnDeck : RoleStandby);
            }
        }

        static void Restore(Own o)
        {
            try { if (o.Ai != null && ReferenceEquals(s_data(o.Ai), o.Mine)) s_data(o.Ai) = o.Game; } catch (Exception) { }
            Restores++;
        }

        /// The dead forgotten; an enemy no longer after the hero gets the game's data back.
        static void Sweep(AITarget at)
        {
            s_gone.Clear();
            foreach (var kv in s_own)
            {
                Own o = kv.Value;
                if (o.Ai == null) { s_gone.Add(kv.Key); continue; }
                if (!at.AI.Contains(o.Ai)) { Restore(o); s_gone.Add(kv.Key); }
            }
            for (int i = 0; i < s_gone.Count; i++) s_own.Remove(s_gone[i]);
        }

        public static void RestoreAll()
        {
            s_onDeck = null;
            if (s_own.Count == 0) return;
            foreach (var kv in s_own) Restore(kv.Value);
            s_own.Clear();
        }

        static readonly AccessTools.FieldRef<ActionMoveAI, int> s_blocking = AccessTools.FieldRefAccess<ActionMoveAI, int>("m_isBlocking");
        static readonly AccessTools.FieldRef<ActionMoveAI, float> s_stateTime = AccessTools.FieldRefAccess<ActionMoveAI, float>("m_stateTime");

        /// For `waiting?`: what the enemy's body runs now (its lower and upper body's actions; ActionMoveAI's state and blocks).
        static string ActState(AIController ai)
        {
            try
            {
                Actor actor = ai.GetComponent<Actor>();
                if (actor == null) return "";
                Defiant.Action lo = actor.RunningActionLowerBody, up = actor.RunningActionUpperBody;
                string r = " act " + (lo != null ? lo.GetType().Name : "-") + "/" + (up != null ? up.GetType().Name : "-");
                ActionMoveAI m = lo as ActionMoveAI;
                if (m != null)
                {
                    r += " st " + m.CurrentState + " blk " + s_blocking(m) + " in " + (Time.time - s_stateTime(m)).ToString("0.0");
                    Targetable hero = ai.Target;
                    if (m.CurrentState.ToString().StartsWith("Flee") && hero != null)
                    {
                        Vector3 d = s_fleeTo(m) - hero.transform.position;
                        d.y = 0f;
                        float rr = hero.UseRadiusInCombat ? hero.Radius : 0f;
                        r += " to " + Mathf.Sqrt(Mathf.Max(0f, d.sqrMagnitude - rr * rr)).ToString("0.00");
                    }
                }
                return r;
            }
            catch (Exception) { return ""; }
        }

        /// `waiting?`: each enemy after the hero - its role, its distance (HoF2's own measure), the band it keeps, since when.
        public static string Query()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("waiting enemies ").Append(CombatSettings.WaitingEnemies ? "on" : "off").Append(" copies ").Append(Copies).Append(" retakes ").Append(Retakes)
              .Append(" restores ").Append(Restores).Append(" roleChanges ").Append(RoleChanges).Append(" onDeckChanges ").Append(OnDeckChanges)
              .Append(" approachTimeouts ").Append(Tickets.ApproachTimeouts).Append(" demotions ").Append(Tickets.Demotions)
              .Append(" strafes ").Append(Strafes).Append(" strafesTimed ").Append(StrafesTimed).Append(" strafesLong ").Append(StrafesLong)
              .Append(" strafeGapMin ").Append(StrafeGapMin.ToString("0.0")).Append(" strafeGapMax ").Append(StrafeGapMax.ToString("0.0")).Append(" lastStrafe ").Append(LastStrafe)
              .Append(" backOffs ").Append(BackOffs).Append(" backOffsKept ").Append(BackOffsKept).Append(" keptPick ").Append(KeptPick).Append(" keptStay ").Append(KeptStay)
              .Append(" lastBackOff ").Append(LastBackOff).Append(" lastKept ").Append(LastKept);
            sb.Append(" quickStops ").Append(QuickStops);
            try
            {
                PlayerController pc = PlayerController.Instance;
                Targetable me = pc != null ? pc.GetComponent<Targetable>() : null;
                float now = Time.unscaledTime;
                foreach (var kv in s_own)
                {
                    Own o = kv.Value;
                    if (o.Ai == null || o.Role < 0) continue;
                    Targetable t = o.Ai.Targetable;
                    float dist = me != null && t != null ? t.Distance(me) : -1f;
                    sb.Append(" | ").Append(WeaponGrips.ModelKey(o.Ai.name)).Append('#').Append(t != null ? t.GetInstanceID() : 0)
                      .Append(' ').Append(s_roleNames[o.Role]).Append(' ').Append(dist.ToString("0.00")).Append(" m band ")
                      .Append(s_target(o.Mine).ToString("0.00")).Append('-').Append(s_outer(o.Mine).ToString("0.00"))
                      .Append(" for ").Append((now - o.RoleSince).ToString("0.0")).Append(" s").Append(ActState(o.Ai));
                }
            }
            catch (Exception) { }
            return sb.ToString();
        }
    }
}

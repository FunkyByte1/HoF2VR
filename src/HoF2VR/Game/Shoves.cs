using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Until You Fall's shoves (docs/uyf-parity-audit.md E7, merge-ready #4): while the hero stands inside an enemy's
    /// StartMeleeAttackRange its ShoveTimer runs (restarted whenever the hero is out of it); when it runs out the enemy shoves
    /// the hero back - no damage (its enemy graphs' ShoveTimer, StartMeleeAttackRange and Knockback Shove per archetype:
    /// EnemyTableData.Shove). HoF2 has no shove move, so the enemy's next attack is the shove: no marker (UYF's shoves are
    /// untelegraphed: PremonitionScalar 0), no lead held, no damage and no guard either way, the hero pushed along the
    /// attacker's line. `[Combat] Shoves`, Physical mode; false = none.
    /// An enemy waiting its turn shoves too (UYF's HuskCombatantBehavior: "BLOCK OR ACT ACCORDING TO STATE" tries the shove
    /// sequence first - the ShoveTimer run out or ForceShoveTrigger - and only then its CheckTicketNodes, priority 1 the
    /// ticket, 2 on deck, else the waiting range): HoF2 lets only the attack slot's holder attack (AIManager.CanAttack), so a
    /// waiter that is due may attack for its shove (the slot stays the turn holder's), and its attack ends once the shove lands.
    internal static class Shoves
    {
        public static int Due, Shoved, Cancelled, Whiffed, WaiterShoves;
        static readonly HashSet<int> s_waiter = new HashSet<int>();   // shoves under way by an enemy not holding the turn
        static readonly List<AIController> s_end = new List<AIController>();   // a waiter's attack to end (the frame after its shove)
        public static string Last = "-";
        static readonly Dictionary<int, float> s_inside = new Dictionary<int, float>();    // enemy id -> inside its range since
        static readonly Dictionary<int, AIController> s_due = new Dictionary<int, AIController>();   // its next attack shoves
        static readonly Dictionary<int, float> s_shoving = new Dictionary<int, float>();   // its shove under way, since
        static readonly List<int> s_drop = new List<int>();
        static float s_next, s_timeOverride = -1f;   // test seam `shoves? time <s>|off`
        /// The fight's first seconds of play with no shove (N7): the player finds the enemies first.
        public static float Grace = 6f;
        static float s_liveSince = -100f, s_lastTick = -100f;
        static bool GraceHeld;
        // UYF's StartMeleeAttackRange is a threshold with edges (0.3-0.4 m beyond its distance): the game's own step back from
        // a hero too near (ActionMoveAI, about 1 m) does not end the time inside (MR4e: due after 14.7 s, then called off)
        const float EdgeOut = 0.4f;

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(AIManager), "CanCombatantAttack", new[] { typeof(AITarget), typeof(AIController), typeof(bool) }),
                        postfix: new HarmonyMethod(typeof(Shoves), nameof(CanAttackPostfix)));
                h.Patch(AccessTools.Method(typeof(AIManager), "IsCombatantLookingToAttack", new[] { typeof(AITarget), typeof(AIController) }),
                        postfix: new HarmonyMethod(typeof(Shoves), nameof(LookingPostfix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("Shoves: waiter patch failed: " + e.Message); }
            Diag.Commands["shoves?"] = a =>
            {
                if (a.Length > 2 && a[1] == "time" && TestMode.Enabled)
                    s_timeOverride = a[2] == "off" ? -1f : float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                if (a.Length > 1 && a[1] == "reset") { Due = Shoved = Cancelled = Whiffed = WaiterShoves = 0; Last = "-"; }
                return Describe();
            };
            Diag.Commands["vrshoves"] = a =>
            {
                if (a.Length > 1) CombatSettings.OverrideShoves(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return Describe();
            };
        }

        static string Describe()
        {
            string s = "shoves " + CombatSettings.Shoves + " due " + Due + " shoved " + Shoved + " cancelled " + Cancelled + " whiffed " + Whiffed +
                       " waiterShoves " + WaiterShoves + " waiting " + s_due.Count + " grace " + Mathf.Max(0f, Grace - (Time.unscaledTime - s_liveSince)).ToString("0.0") + " | last " + Last;
            float now = Time.unscaledTime;
            foreach (var kv in s_inside) s += " | #" + kv.Key + " inside " + (now - kv.Value).ToString("0.0") + " s";
            return s;
        }

        /// Every frame (Tickets.Tick; the timers at 10 Hz): each melee enemy after the hero - inside its range or not.
        public static void Tick(AITarget at)
        {
            if (s_end.Count > 0)
            {
                // a waiter's shove has landed: the rest of its attack (a combo's later blows) is not its to swing
                foreach (AIController e in s_end)
                {
                    try { Defiant.Action run = e != null ? Guard.RunningStrikeOf(e) : null; if (run != null && run.IsRunning) run.Complete(); }
                    catch (Exception) { }
                }
                s_end.Clear();
            }
            if (!CombatSettings.Shoves || at == null || VRRig.I == null)
            {
                if (s_inside.Count > 0 || s_due.Count > 0 || s_shoving.Count > 0) { s_inside.Clear(); s_due.Clear(); s_shoving.Clear(); s_waiter.Clear(); }
                return;
            }
            float now = Time.unscaledTime;
            // N7 (the player's courtyard fight 2026-10-17: "shoved by EmpireSoldier (waiting its turn): 4.0 m" right after
            // Continue - the soldiers stand within their 1.9 m at the start, and UYF's 3.5 s ran out before the player had found
            // them): no shove timer runs in a fight's first Grace seconds of play (after the showcase's Continue, after a pause)
            if (now - s_lastTick > 0.5f) { s_liveSince = now; s_inside.Clear(); s_due.Clear(); }
            s_lastTick = now;
            if (now - s_liveSince < Grace) { GraceHeld = true; return; }
            if (GraceHeld) { GraceHeld = false; Plugin.L.LogInfo("Shoves: the fight's first " + Grace.ToString("0") + " s are over - shove timers run"); }
            if (now < s_next) return;
            s_next = now + 0.1f;
            Vector3 head = VRRig.I.HeadWorldPos;
            s_drop.Clear();
            foreach (var kv in s_shoving) if (now - kv.Value > 4f) s_drop.Add(kv.Key);   // its blow never landed (whiffed, stopped)
            for (int i = 0; i < s_drop.Count; i++) { s_shoving.Remove(s_drop[i]); s_waiter.Remove(s_drop[i]); Whiffed++; }
            s_drop.Clear();
            foreach (var kv in s_inside) if (!Present(at, kv.Key)) s_drop.Add(kv.Key);   // dead or gone
            for (int i = 0; i < s_drop.Count; i++) { s_inside.Remove(s_drop[i]); s_due.Remove(s_drop[i]); }
            for (int i = 0; i < at.AI.size; i++)
            {
                AIController ai = at.AI[i];
                if (ai == null || ai.Mode != AIMode.Melee) continue;
                int id = ai.GetInstanceID();
                float[] sh;
                try { sh = EnemyTable.ShoveOf(ai); } catch (Exception) { continue; }
                Vector3 to = ai.transform.position - head;
                to.y = 0f;
                bool inside;
                if (!s_inside.ContainsKey(id)) inside = to.magnitude <= sh[1];
                else inside = to.magnitude <= sh[1] + EdgeOut;   // UYF's threshold edge: in at its distance, out 0.4 m beyond
                if (!inside)
                {
                    // out of its range: the timer starts afresh (UYF), a shove not begun yet is off
                    s_inside.Remove(id);
                    if (s_due.Remove(id)) Cancelled++;
                    continue;
                }
                float since;
                if (!s_inside.TryGetValue(id, out since)) { s_inside[id] = now; continue; }
                if (s_due.ContainsKey(id) || s_shoving.ContainsKey(id)) continue;
                float limit = s_timeOverride > 0f ? s_timeOverride : sh[0];
                if (now - since < limit) continue;
                s_due[id] = ai;
                Due++;
                Last = "due: " + WeaponGrips.ModelKey(ai.name) + (ReferenceEquals(at.MeleeAttacker, ai) ? "" : " (waiting its turn)") + " after " +
                       (now - since).ToString("0.0") + " s inside " + sh[1].ToString("0.0") + " m";
            }
        }

        static bool Present(AITarget at, int id)
        {
            for (int i = 0; i < at.AI.size; i++) if (at.AI[i] != null && at.AI[i].GetInstanceID() == id) return true;
            return false;
        }

        /// Guard, at a fresh telegraph: this attack is the enemy's shove (its due flag taken).
        public static bool Take(AIController ai)
        {
            if (ai == null || !CombatSettings.Shoves) return false;
            int id = ai.GetInstanceID();
            if (!s_due.Remove(id)) return false;
            s_shoving[id] = Time.unscaledTime;
            AITarget at = ai.Target != null ? ai.Target.AITarget : null;
            if (at != null && !ReferenceEquals(at.MeleeAttacker, ai)) s_waiter.Add(id); else s_waiter.Remove(id);
            return true;
        }

        /// A due enemy that does not hold the attack slot (UYF: the shove comes before the ticket).
        static bool WaiterDue(AITarget at, AIController ai)
        {
            if (s_due.Count == 0 || ai == null || at == null || !CombatSettings.Shoves || ai.Mode != AIMode.Melee) return false;
            return !ReferenceEquals(at.MeleeAttacker, ai) && s_due.ContainsKey(ai.GetInstanceID());
        }

        /// AIManager.CanCombatantAttack: a due waiter may attack (its shove) - the game's other bars kept (a repellent target,
        /// exclusive attackers).
        static void CanAttackPostfix(AITarget a_target, AIController a_ai, ref bool __result)
        {
            if (__result || !WaiterDue(a_target, a_ai)) return;
            try
            {
                if (a_target.IsTargetRepellent(0)) return;
                if (a_target.Targetable != null && a_target.Targetable.ExclusiveAiAttackers.size > 0 && !a_target.Targetable.ExclusiveAiAttackers.Contains(a_ai)) return;
            }
            catch (Exception) { return; }
            __result = true;
        }

        /// AIManager.IsCombatantLookingToAttack: a due waiter goes for the hero (its shove), as the slot's holder does.
        static void LookingPostfix(AITarget a_target, AIController a_ai, ref bool __result)
        {
            if (!__result && WaiterDue(a_target, a_ai)) __result = true;
        }

        /// ThreatWarning: this attacker's coming blow is a shove (no lead held: it swings at its own speed).
        public static bool Shoving(Controller c)
        {
            if (c == null || (s_due.Count == 0 && s_shoving.Count == 0)) return false;
            int id = c.GetInstanceID();
            return s_due.ContainsKey(id) || s_shoving.ContainsKey(id);
        }

        /// Guard.Resolve: the shove lands - no damage, the hero pushed `away` (from the attacker) by its Knockback Shove.
        public static void Landed(AIController ai, Vector3 away)
        {
            int id = ai.GetInstanceID();
            s_shoving.Remove(id);
            s_inside.Remove(id);   // its timer starts afresh
            bool waiter = s_waiter.Remove(id);
            if (waiter) { WaiterShoves++; s_end.Add(ai); }
            float push = 3f;
            try { push = EnemyTable.ShoveOf(ai)[2]; } catch (Exception) { }
            Shoved++;
            Last = "shoved by " + WeaponGrips.ModelKey(ai.name) + (waiter ? " (waiting its turn)" : "") + ": " + push.ToString("0.0") + " m";
            Plugin.L.LogInfo("Shoves: " + Last);
            CombatMove.Knockback(away, push, "shoved");
            CameraShake.Add(0.25f, 0.07f, 0.05f, "shoved");
            Haptics.Pulse(true, 0.8f, 0.12f);
            Haptics.Pulse(false, 0.8f, 0.12f);
            if (VRRig.I != null) CombatSounds.Layer(CombatSounds.Thump, VRRig.I.HeadWorldPos + Vector3.down * 0.4f - away * 0.3f, 0.8f, 0.8f);
        }
    }
}

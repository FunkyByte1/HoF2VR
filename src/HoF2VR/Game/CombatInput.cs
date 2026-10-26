using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Motion controls for first-person combat. Gestures become presses on the virtual pad, so the
    /// game's own rules (combos, cancel windows, buffering, counters) decide what happens:
    ///  * sword swing (blade-tip speed)          -> Attack (X); consecutive swings chain the combo
    ///  * shield-hand jab while an attack comes  -> Counter (Y)
    ///  * shield-hand forward thrust otherwise   -> Bash / Stun (B)
    ///  * downward stab with the sword            -> Finisher (LT) when an enemy is prone
    /// Targeting is steered to where the player looks (the game would otherwise pick any enemy in
    /// 360 degrees when the stick is idle). Enemy attack wind-ups buzz the controllers.
    internal class CombatInput : MonoBehaviour
    {
        public static CombatInput I;

        bool m_weaponSwing, m_shieldSwing;
        float m_weaponReady, m_shieldReady;
        public static int Swings, Counters, Bashes, Stabs;
        public static int GameAttacks, GameHits, CounterOk, CounterFail;
        static readonly Dictionary<int, Telegraph> s_telegraphs = new Dictionary<int, Telegraph>();

        class Telegraph { public Model Model; public bool Blockable; public float Since; }

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(TargetUtils), "FindTarget"), prefix: new HarmonyMethod(typeof(CombatInput), nameof(SteerTarget)));
            h.Patch(AccessTools.Method(typeof(ActionCounter), "BeginMelee"), postfix: new HarmonyMethod(typeof(CombatInput), nameof(OnCounterOk)));
            h.Patch(AccessTools.Method(typeof(ActionCounter), "BeginRanged"), postfix: new HarmonyMethod(typeof(CombatInput), nameof(OnCounterOk)));
            h.Patch(AccessTools.Method(typeof(ActionCounter), "BeginNone"), postfix: new HarmonyMethod(typeof(CombatInput), nameof(OnCounterFail)));
            h.Patch(AccessTools.Method(typeof(Destroyable), "ApplyDamage"), prefix: new HarmonyMethod(typeof(CombatInput), nameof(BeforeDamage)),
                postfix: new HarmonyMethod(typeof(CombatInput), nameof(OnPlayerDamaged)));
            h.Patch(AccessTools.Method(typeof(ActionMelee), "Begin"), postfix: new HarmonyMethod(typeof(CombatInput), nameof(OnAttackBegin)));
            h.Patch(AccessTools.Method(typeof(ActionMelee), "OnHit"), postfix: new HarmonyMethod(typeof(CombatInput), nameof(OnAttackHit)));
            h.Patch(AccessTools.Method(typeof(CombatUtils), "StartMeleeEffect"), postfix: new HarmonyMethod(typeof(CombatInput), nameof(TelegraphStart)));
            h.Patch(AccessTools.Method(typeof(CombatUtils), "StopMeleeEffect"), postfix: new HarmonyMethod(typeof(CombatInput), nameof(TelegraphStop)));
            h.Patch(AccessTools.Method(typeof(CombatUtils), "StartRangedEffect"), postfix: new HarmonyMethod(typeof(CombatInput), nameof(TelegraphStart)));
            h.Patch(AccessTools.Method(typeof(CombatUtils), "StopRangedEffect"), postfix: new HarmonyMethod(typeof(CombatInput), nameof(TelegraphStop)));
        }

        void Awake()
        {
            I = this;
            Diag.StatusExtras["gestures"] = () => Diag.Q("swings " + Swings + " counters " + Counters + " bashes " + Bashes + " stabs " + Stabs + " telegraphs " + s_telegraphs.Count);
            Diag.StatusExtras["outcomes"] = () => Diag.Q("attacks " + GameAttacks + " hits " + GameHits + " counterOK " + CounterOk + " counterFail " + CounterFail);
            Diag.Commands["tele?"] = a => s_telegraphs.Count + " " + (CanCounterNow() ? "COUNTER" : "no");
            Diag.StatusExtras["enemies"] = () =>
            {
                PlayerController pc = PlayerController.Instance;
                if (pc == null) return Diag.Q("-");
                Targetable me = pc.GetComponent<Targetable>();
                BetterList<Targetable> all = me.EnumerateWithinRadius(60f, me.GetEnemyTeams());
                int alive = 0;
                float hp = 0f;
                for (int i = 0; i < all.size; i++)
                {
                    Destroyable d = all[i].GetComponent<Destroyable>();
                    if (d == null || d.IsDead) continue;
                    alive++;
                    hp += d.CurrentHealth;
                }
                Destroyable mine = pc.GetComponent<Destroyable>();
                return Diag.Q(alive + " alive, hp " + hp.ToString("0") + "; player hp " + (mine ? mine.CurrentHealth.ToString("0") : "?"));
            };
        }

        static bool IsPlayer(Component c) => PlayerController.Instance != null && c != null && c.transform.IsChildOf(PlayerController.Instance.transform);
        static void OnCounterOk(Component __instance)
        {
            if (!IsPlayer(__instance)) return;
            CounterOk++;
            if (CombatVR.Active) Haptics.Pulse(!Settings.LeftHanded, 0.9f, 0.12f);   // shield hand
        }
        static void OnCounterFail(Component __instance) { if (IsPlayer(__instance)) CounterFail++; }
        static void OnAttackBegin(Component __instance) { if (IsPlayer(__instance)) GameAttacks++; }
        static void OnAttackHit(Component __instance)
        {
            if (!IsPlayer(__instance)) return;
            GameHits++;
            if (CombatVR.Active) Haptics.Pulse(Settings.LeftHanded, 0.8f, 0.07f);    // weapon hand
        }
        static void OnPlayerDamaged(Destroyable __instance, float __state)
        {
            if (!CombatVR.Active || PlayerController.Instance == null || __instance.gameObject != PlayerController.Instance.gameObject) return;
            if (__instance.CurrentHealth >= __state) return;
            // the hit shown in the world, not as a red screen edge (CombatFeel); Physical mode: the impact in both hands and
            // the attacker steps back
            Controller attacker = null;
            bool unblockable = false;
            try
            {
                Hit h = PlayerController.Instance.LastHitBy;
                attacker = h.Source != null ? h.Source.GetComponent<AIController>() : null;
                unblockable = h.HitWasBlockBreaker;
            }
            catch (Exception) { }
            if (!(CombatSettings.Physical && Guard.HitFrame == Time.frameCount)) CombatFeel.HeroHit(attacker, unblockable);
            if (CombatSettings.Physical) return;
            Haptics.Pulse(true, 0.7f, 0.15f);
            Haptics.Pulse(false, 0.7f, 0.15f);
        }
        static void BeforeDamage(Destroyable __instance, out float __state) { __state = __instance.CurrentHealth; }

        // ---- target steering
        static void SteerTarget(Targetable a_targetable, ref Vector3 a_direction, ref float a_angle)
        {
            if (!CombatVR.Active || PlayerController.Instance == null || a_targetable == null) return;
            if (a_targetable.gameObject != PlayerController.Instance.gameObject) return;
            Vector3 gaze = VRRig.I.HeadWorldRot * Vector3.forward;
            gaze.y = 0f;
            if (gaze.sqrMagnitude < 1e-4f) return;
            a_direction = gaze.normalized;
            a_angle = Mathf.Clamp(a_angle, 60f, 90f);
        }

        // ---- telegraphs (enemy attack wind-ups)
        static void TelegraphStart(Model a_model, bool a_blockable)
        {
            if (a_model == null) return;
            s_telegraphs[a_model.GetInstanceID()] = new Telegraph { Model = a_model, Blockable = a_blockable, Since = Time.time };
            if (!CombatVR.Active) return;
            Vector3 to = a_model.transform.position - VRRig.I.HeadWorldPos;
            to.y = 0f;
            Vector3 gaze = VRRig.I.HeadWorldRot * Vector3.forward;
            gaze.y = 0f;
            float angle = Vector3.Angle(gaze, to);
            if (ThreatWarning.I != null) ThreatWarning.I.OnStart(a_model, a_blockable);
            // outside the view ThreatWarning's heartbeat runs on that side for the whole wind-up (with a marker and a
            // sound); in view: one light tick, on the attacker's side (the heartbeat stays the only rhythm that means
            // "from where you can't see", docs/combat-uyf-design.md §7.5.3)
            if (angle > ThreatWarning.ViewHalf && Settings.OffViewWarning) return;
            bool fromLeft = Vector3.Cross(gaze, to).y < 0f;
            Haptics.Pulse(fromLeft, 0.5f, 0.02f);
        }

        static void TelegraphStop(Model a_model)
        {
            if (a_model != null) s_telegraphs.Remove(a_model.GetInstanceID());
            if (a_model != null && ThreatWarning.I != null) ThreatWarning.I.OnStop(a_model);
        }

        public static IEnumerable<Model> ActiveAttackers()
        {
            foreach (Telegraph t in s_telegraphs.Values) if (t.Model != null) yield return t.Model;
        }

        /// Same rule as the game's ConditionPlayerCanSuccessfullyCounter.
        public static bool CanCounterNow()
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null) return false;
            Targetable me = pc.GetComponent<Targetable>();
            BetterList<Targetable> near = me.EnumerateWithinRadius(pc.Combat.DefaultAttackRange, me.GetEnemyTeams());
            for (int i = 0; i < near.size; i++)
                if (ActionCounter.GetAction(near[i]) != null) return true;
            return pc.ProjectileOwnerToReflect != null || (pc.ProjectileToReflect != null && !pc.ProjectileToReflect.IsExpiring);
        }

        static bool AnyProneEnemyNear()
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null) return false;
            Targetable me = pc.GetComponent<Targetable>();
            BetterList<Targetable> near = me.EnumerateWithinRadius(4f, me.GetEnemyTeams());
            for (int i = 0; i < near.size; i++)
            {
                Controller c = near[i].Controller;
                if (c != null && c.Prone.HasValue) return true;
            }
            return false;
        }

        // ---- gestures
        /// Blade-tip speed: hand velocity plus the wrist's rotation sweeping the blade.
        static float TipSpeed(Hand h)
        {
            Vector3 blade = h.Rot * (Quaternion.Euler(-ControllerGeometry.GripPitchUp, 0f, 0f) * Vector3.forward) * 0.6f;
            return (h.Vel + Vector3.Cross(h.AngVel, blade)).magnitude;
        }

        void Update()
        {
            // gestures only once the fight is live (first person already starts in the enemy showcase); Physical mode
            // presses nothing (the blade and the guard act by themselves: BladeHits, Guard)
            if (!CombatVR.Active || !GameWatch.InCombatPlay || CombatSettings.Physical)
            {
                m_weaponSwing = m_shieldSwing = false;
                return;
            }
            bool lefty = Settings.LeftHanded;
            Hand weapon = lefty ? VRInput.L : VRInput.R;
            Hand shield = lefty ? VRInput.R : VRInput.L;
            float now = Time.unscaledTime;

            // Sword: one press per swing (rising edge over SwingOn, re-armed below SwingOff)
            if (weapon.Tracked)
            {
                float tip = TipSpeed(weapon);
                bool stabDown = weapon.Vel.y < -Settings.StabSpeed && (weapon.Rot * Vector3.forward).y < -0.3f;
                if (!m_weaponSwing && now >= m_weaponReady && (tip > Settings.SwingSpeed || stabDown))
                {
                    m_weaponSwing = true;
                    if (stabDown && AnyProneEnemyNear()) { VRPad.Press("LT", 4); Stabs++; }
                    else { VRPad.Press("X", 4); Swings++; }
                    Haptics.Pulse(lefty, 0.3f, 0.05f);
                }
                else if (m_weaponSwing && tip < Settings.SwingSpeed * 0.45f)
                {
                    m_weaponSwing = false;
                    m_weaponReady = now + 0.1f;
                }
            }

            // Shield hand: jab = counter when an attack can be countered, else a forward thrust = bash
            if (shield.Tracked)
            {
                float speed = shield.Vel.magnitude;
                if (!m_shieldSwing && now >= m_shieldReady && speed > Settings.ShieldSpeed)
                {
                    m_shieldSwing = true;
                    Vector3 fwd = VRInput.HeadRot * Vector3.forward;
                    fwd.y = 0f;
                    float forwardness = Vector3.Dot(shield.Vel.normalized, fwd.normalized);
                    if (CanCounterNow()) { VRPad.Press("Y", 4); Counters++; Haptics.Pulse(!lefty, 0.5f, 0.08f); }
                    else if (forwardness > 0.6f) { VRPad.Press("B", 4); Bashes++; Haptics.Pulse(!lefty, 0.4f, 0.06f); }
                    else m_shieldSwing = false;   // not a gesture we use; stay armed
                }
                else if (m_shieldSwing && speed < Settings.ShieldSpeed * 0.45f)
                {
                    m_shieldSwing = false;
                    m_shieldReady = now + 0.1f;
                }
            }
        }
    }
}

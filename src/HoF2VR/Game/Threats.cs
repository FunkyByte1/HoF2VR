using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Combat tuning for VR (user 2026-10-11 afternoon):
    ///  * "Combat is really hard right now. For testing at least, can you make enemies have 25 % health": every enemy
    ///    starts with Settings.EnemyHealth of its normal health. Destroyable.Start (once per instance, before its health
    ///    is set up) gets its MaxHealth.BaseValue scaled - enemies only (a Controller on the Enemy team, never the player,
    ///    companions or props) and never in the final battle (its stages count absolute health). Health bars read
    ///    HealthRatio, which starts at 1 as before.
    ///  * "It's impossible to tell if someone is going to attack me from behind. I need a warning": see ThreatWarning,
    ///    which also slows the wind-up of strikes from outside the view through Slow/Unslow here: the attacker's
    ///    Controller.CalculateAttackSpeed is scaled until the strike is over - the game's attack actions read it every
    ///    frame (melee, combos, ranged, assassinate, templar; dashes don't use it), the AI has no attack timeouts, and
    ///    the counter window (= the telegraph) lasts as long.
    ///  * The game's own off-screen enemy arrows (Indicator's UI elements) were projected for a flat screen; in VR they
    ///    are switched off in fights (the markers below replace them).
    ///  * The user, 2026-10-18 (after playtest 3): "make the enemies do about 1/3 as much damage as right now": every hit on
    ///    the player from an enemy-team source (blows, shots, their fire) x Settings.EnemyDamage, through the game's own
    ///    incoming-damage path (the player's Controller.ModifyDamageIn, as the game's own damage-in cards do), so armour,
    ///    blocks, cards and curses still apply on top.
    internal static class EnemyTuning
    {
        public static int Scaled, Skipped, DamageCut;
        public static float LastDamageIn = -1f, LastDamageOut = -1f;
        static readonly HashSet<int> s_damageHooked = new HashSet<int>();
        static readonly Dictionary<int, float> s_slowed = new Dictionary<int, float>();   // controller id -> factor
        static readonly int AttackSpeedHash = Animator.StringToHash("AttackSpeed");

        public static void Apply(Harmony h)
        {
            try { h.Patch(AccessTools.Method(typeof(Destroyable), "Start"), prefix: new HarmonyMethod(typeof(EnemyTuning), nameof(BeforeStart))); }
            catch (Exception e) { Plugin.L.LogWarning("EnemyTuning: health patch failed: " + e.Message); }
            try { h.Patch(AccessTools.Method(typeof(Controller), "CalculateAttackSpeed"), postfix: new HarmonyMethod(typeof(EnemyTuning), nameof(AttackSpeedPostfix))); }
            catch (Exception e) { Plugin.L.LogWarning("EnemyTuning: attack speed patch failed: " + e.Message); }
            try { h.Patch(AccessTools.Method(typeof(Indicator), "LateUpdate"), postfix: new HarmonyMethod(typeof(EnemyTuning), nameof(IndicatorPostfix))); }
            catch (Exception e) { Plugin.L.LogWarning("EnemyTuning: indicator patch failed: " + e.Message); }
            // the game's marker over an attacker's head (FXIndicator_Melee(Un)Counterable, FXIndicator_Ranged..: a billboard glow
            // above it) is hidden in VR; the attacker's own rim glow (FXRimCharacter..On, the same event) stays. The player,
            // 2026-10-17: "Get rid of the green marker on top of an attacking enemy's head. The enemy should still glow green."
            Effect.OnEffect += HideHeadMarker;
            Diag.StatusExtras["enemyTuning"] = () => Diag.Q("health x" + Settings.EnemyHealth.ToString("0.00") + " scaled " + Scaled + " skipped " + Skipped +
                                                            " damageIn x" + DamageInFactor.ToString("0.00") + " cut " + DamageCut + " last " + LastDamageIn.ToString("0.0") + "->" + LastDamageOut.ToString("0.0") +
                                                            " slowed now " + s_slowed.Count + " slowdowns " + Slowdowns + " head markers hidden " + HeadMarkersHidden +
                                                            " last " + LastHeadMarker);
        }

        public static int HeadMarkersHidden;
        public static string LastHeadMarker = "-";

        static void HideHeadMarker(Effect effect, Model model, EffectController instance)
        {
            if (!XRSettings.enabled || instance == null) return;
            try
            {
                string n = instance.name;
                if (n == null || !n.StartsWith("FXIndicator_", StringComparison.Ordinal)) return;
                // every renderer of the pooled instance (it is reused: the next telegraph's copy is hidden again here)
                foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                HeadMarkersHidden++;
                LastHeadMarker = n + " over " + (model != null ? WeaponGrips.ModelKey(model.name) : "-");
                if (HeadMarkersHidden <= 3) Plugin.L.LogInfo("EnemyTuning: the head marker " + LastHeadMarker + " hidden (the rim glow stays)");
            }
            catch (Exception e) { Plugin.L.LogWarning("EnemyTuning: head marker: " + e.Message); }
        }

        /// An enemy creature or enemy object (turret, totem) - the game's own rule for its floating health bars.
        static bool IsEnemy(Destroyable d)
        {
            if (d.GetComponent<PlayerController>() != null || d.GetComponent<Companion>() != null) return false;
            Targetable t = d.GetComponent<Targetable>();
            return t != null && t.InitialTeam == TeamType.Enemy && d.GetComponent<Controller>() != null;
        }

        static void BeforeStart(Destroyable __instance)
        {
            if (!XRSettings.enabled) return;
            HookPlayerDamage(__instance);
            float f = Settings.EnemyHealth;
            if (f >= 0.999f || __instance == null) return;
            try
            {
                if (!IsEnemy(__instance)) return;
                if (BossBattleManager.Instance != null) { Skipped++; return; }   // the final battle (stage maths on absolute health)
                float before = __instance.MaxHealth.BaseValue;
                __instance.MaxHealth.BaseValue = before * f;
                Scaled++;
                if (Scaled <= 6 || Scaled % 20 == 0)
                    Plugin.L.LogInfo("EnemyTuning: " + __instance.name + " health " + before.ToString("0") + " -> " + (before * f).ToString("0") + " (x" + f.ToString("0.00") + ", #" + Scaled + ")");
            }
            catch (Exception e) { Plugin.L.LogWarning("EnemyTuning: " + e.Message); }
        }

        /// The player's incoming damage x EnemyDamage (once per player controller: its own delegate list, as a card adds one).
        static void HookPlayerDamage(Destroyable d)
        {
            try
            {
                if (d == null) return;
                PlayerController pc = d.GetComponent<PlayerController>();
                if (pc == null) return;
                Controller c = d.GetComponent<Controller>();
                if (c == null || !s_damageHooked.Add(c.GetInstanceID())) return;
                c.ModifyDamageIn = (Controller.ModifyDamageDelegate)Delegate.Combine(c.ModifyDamageIn, new Controller.ModifyDamageDelegate(PlayerDamageIn));
                Plugin.L.LogInfo("EnemyTuning: enemy damage to the player x" + Settings.EnemyDamage.ToString("0.00") + " (" + d.name + ")");
            }
            catch (Exception e) { Plugin.L.LogWarning("EnemyTuning: damage hook: " + e.Message); }
        }

        /// Test seam `vrdamagein <x>|setting`.
        public static float? DamageInOverride;
        public static float DamageInFactor => DamageInOverride ?? Settings.EnemyDamage;

        static void PlayerDamageIn(ICombatEffectContainer a_container, Targetable a_target, ref float a_damage, Destroyable.DamageType a_damageType, bool a_blocked)
        {
            try
            {
                Guard.DamageSeen(a_container, a_damage, a_blocked);   // P4-16 diagnostics (`dmglog?`)
                float k = DamageInFactor;
                if (k >= 0.999f || a_damage <= 0f || !XRSettings.enabled) return;
                Targetable src = a_container != null ? a_container.Source : null;
                if (src == null || src.InitialTeam != TeamType.Enemy) return;
                LastDamageIn = a_damage;
                a_damage *= k;
                LastDamageOut = a_damage;
                DamageCut++;
            }
            catch (Exception) { }
        }

        // ---- the wind-up of an attacker the player can't see ----

        public static int Slowdowns;

        public static void Slow(Controller c, float factor)
        {
            if (c == null || factor >= 0.999f) return;
            s_slowed[c.GetInstanceID()] = factor;
            Slowdowns++;
            Refresh(c);
        }

        /// The slow this attacker is under (1 = none): CalculateAttackSpeed's result includes it.
        /// The animator speed this attacker's strike is held at (1 = none; GlobalSlow).
        public static float GlobalOf(Controller c)
        {
            float f;
            return c != null && s_global.TryGetValue(c.GetInstanceID(), out f) && f > 0.001f ? f : 1f;
        }

        public static float SlowOf(Controller c)
        {
            float f;
            return c != null && s_slowed.TryGetValue(c.GetInstanceID(), out f) && f > 0.001f ? f : 1f;
        }

        public static void Unslow(Controller c)
        {
            if (c == null || !s_slowed.Remove(c.GetInstanceID())) return;
            Refresh(c);   // Complete does not reset the animator's parameter
        }

        public static void UnslowAll() { s_slowed.Clear(); s_global.Clear(); }

        // ---- strikes whose animation ignores the AttackSpeed parameter (the templar multi-hit sets it, but its states
        // don't use it: the empire general's blows came after 0.48 s with the parameter at 0.40): slowed by the animator's
        // own speed instead, kept every frame while the strike lasts (left alone while a hit-stop or a pause holds it) ----
        static readonly Dictionary<int, float> s_global = new Dictionary<int, float>();
        public static int GlobalSlows;

        public static void GlobalSlow(Controller c, float factor)
        {
            if (c == null || factor >= 0.999f) return;
            s_global[c.GetInstanceID()] = factor;
            GlobalSlows++;
            if (GlobalSlows <= 30) Plugin.L.LogInfo("EnemyTuning: global slow #" + GlobalSlows + " " + c.name + " x" + factor.ToString("0.00") + " (its state ignores AttackSpeed)");
            KeepGlobalSlow(c);
        }

        public static void KeepGlobalSlow(Controller c)
        {
            float f;
            if (c == null || !s_global.TryGetValue(c.GetInstanceID(), out f)) return;
            try
            {
                AnimatorProxy a = c.Model != null ? c.Animator : null;
                if (a == null) return;
                float cur = a.GetGlobalSpeed();
                if (cur < 0.01f) return;                       // a hit-stop or the game's pause holds it
                if (Mathf.Abs(cur - f) > 0.001f) a.SetGlobalSpeed(f);
            }
            catch (Exception) { }
        }

        public static void GlobalUnslow(Controller c)
        {
            if (c == null || !s_global.Remove(c.GetInstanceID())) return;
            try
            {
                AnimatorProxy a = c.Model != null ? c.Animator : null;
                if (a == null) return;
                if (a.GetGlobalSpeed() < 0.01f) CombatFeel.RestoreSpeedAfterStop(c, 1f);   // a hit-stop gives back 1, not the slow
                else a.SetGlobalSpeed(1f);
            }
            catch (Exception) { }
        }

        /// The attacker's animation state ignores the AttackSpeed parameter: it is set (slowed) but the state plays at 1.
        public static bool IgnoresAttackSpeed(Controller c)
        {
            try
            {
                Animator anim = c != null && c.Model != null && c.Animator != null ? c.Animator.Animator : null;
                if (anim == null) return false;
                if (Mathf.Abs(anim.GetFloat(AttackSpeedHash) - 1f) <= 0.05f) return false;
                // the state blended into counts too: a blow's state that ignores the parameter plays at full speed through the
                // whole blend into it (a shadow warrior's blow landed 0.70 s after its marker, of 0.90, still in its hold)
                if (Mathf.Abs(anim.GetCurrentAnimatorStateInfo(0).speedMultiplier - 1f) < 0.01f) return true;
                return anim.IsInTransition(0) && Mathf.Abs(anim.GetNextAnimatorStateInfo(0).speedMultiplier - 1f) < 0.01f;
            }
            catch (Exception) { return false; }
        }

        /// For the lead diagnostics: this attacker's slow factor now (1 = none) and its animator's AttackSpeed.
        public static string SlowState(Controller c)
        {
            if (c == null) return "-";
            float f;
            string sp = "-";
            try { if (c.Model != null && c.Animator != null && c.Animator.Animator != null) sp = c.Animator.Animator.GetFloat(AttackSpeedHash).ToString("0.00"); } catch (Exception) { }
            return "slow " + (s_slowed.TryGetValue(c.GetInstanceID(), out f) ? f.ToString("0.00") : "none") + " attackSpeed " + sp;
        }

        static void Refresh(Controller c)
        {
            try
            {
                AnimatorProxy a = c.Model != null ? c.Animator : null;
                if (a != null) a.SetFloat(AttackSpeedHash, c.CalculateAttackSpeed());
            }
            catch (Exception) { }
        }

        static void AttackSpeedPostfix(Controller __instance, ref float __result)
        {
            float f;
            if (s_slowed.Count > 0 && s_slowed.TryGetValue(__instance.GetInstanceID(), out f)) __result *= f;
            // UYF's AttackSpeed of the enemy's archetype at the difficulty (Physical mode; the coordinator's A)
            if (Physical.On && __instance is AIController && __instance.Targetable != null && __instance.Targetable.Team == TeamType.Enemy)
            {
                try { __result *= EnemyTable.SpeedFor(__instance); } catch (Exception) { }
            }
        }

        static readonly AccessTools.FieldRef<Indicator, UIIndicator> s_uiArrow = AccessTools.FieldRefAccess<Indicator, UIIndicator>("m_uiElement");
        static readonly AccessTools.FieldRef<Indicator, UIIndicator> s_uiIcon = AccessTools.FieldRefAccess<Indicator, UIIndicator>("m_uiIconElement");

        static void IndicatorPostfix(Indicator __instance)
        {
            if (!CombatVR.Active) return;
            UIIndicator a = s_uiArrow(__instance), b = s_uiIcon(__instance);
            if (a != null && a.gameObject.activeSelf) a.gameObject.SetActive(false);
            if (b != null && b.gameObject.activeSelf) b.gameObject.SetActive(false);
        }
    }

    /// Warnings for attacks from behind (combat Deliverable 1d; docs/combat-uyf-design.md §8.5.4, §7.5; the user,
    /// 2026-10-11: "I still didn't feel well warned that I was about to be attacked from behind" ... "an obvious warning,
    /// and attacks from behind stay blockable and dodgeable"). "Off-view" = more than 55 deg from the gaze, "behind" =
    /// more than 110 deg.
    ///  * Fewer: one strike from outside the view at a time (a second off-view attacker waits until the first strike is
    ///    over), and on Relaxed and Normal an enemy behind the player only starts attacks that can be countered: its
    ///    others wait until the player faces it (CombatSettings.UnblockableFromBehind; always allowed on Fast).
    ///  * Earlier: the enemy the game picks to attack the player (AITarget.MeleeAttacker / RangedAttacker) is announced
    ///    while it is out of view (a small amber marker, one tick on that side), and may not start for
    ///    CombatSettings.OffViewAnnounce (1.0 s on Normal): AIManager.CanCombatantAttack says no until then.
    ///  * Longer: a strike that starts out of view winds up CombatSettings.OffViewBonus (0.8 s on Normal) longer: its
    ///    attack speed is scaled by natural / (natural + bonus) until the impact (EnemyTuning.Slow), where natural is the
    ///    telegraph-to-impact time learned per enemy type (0.9 s until known).
    ///  * Obvious, in three channels: (1) a marker at the edge of the view in the attacker's direction (radar: ahead =
    ///    up, behind = the bottom), 10 deg, 35 deg out, in the colour of the game's own glow (green = counter it, red =
    ///    get out of the way) and filling up until the impact; (2) a sting from the attacker (thump + rising hiss), then
    ///    a tone rising to the impact, muffled (low-pass) the further behind it is; (3) a heartbeat in the controller on
    ///    that side (both when behind), every 0.5 s speeding up to 0.2 s, used for nothing else.
    /// The game's own off-screen arrows (flat-screen UI) stay off in VR (EnemyTuning.IndicatorPostfix).
    internal class ThreatWarning : MonoBehaviour
    {
        public static ThreatWarning I;
        /// Outside the view beyond ViewHalf from the gaze: UYF's off-screen edge, 40 deg (its PremonitionUIOffsetData for the Rift
        /// and the Index; the Quest 2's view is as wide; was 55).
        public static float ViewHalf = 40f, BehindAngle = 110f;
        /// Playtest 3 (2026-10-18), marks 18 and 21: "Arrows that tell me where I'm getting shot at ... make them bigger and
        /// brighter"; "I need even more of a warning before they're going to hit me from behind": the strike's arrow 10 -> 14 deg
        /// (x BehindBoost from behind), a shot's 10 -> 18 deg, the announcement's 6 -> 9 deg, each with a soft additive halo;
        /// the off-view lead and the announcement earlier (CombatSettings.OffViewBonus / OffViewAnnounce), the tone louder.
        public static float MarkerRadius = 35f, MarkerSize = 14f, AnnounceSize = 9f, ShotMarkerSize = 18f, BehindBoost = 1.25f, HaloScale = 1.75f;
        const int MaxMarkers = 3;
        const float DefaultWindup = 0.9f;
        /// Physical mode, an enemy type not seen yet: its wind-up taken as short as the shortest measured (the thieves' 0.48 s),
        /// so its first strikes get at least the class's lead too (with 0.9 s an empire soldier's first strikes, 0.68 s by
        /// nature, came 0.90 s after the glow instead of 1.20 s: env 1, 2026-10-12). Learned from its first strike on.
        const float UnseenWindup = 0.45f;
        public static int ExactLeads, ExactMisses;
        static readonly int s_attackSpeedHash = Animator.StringToHash("AttackSpeed");

        /// The time until the attacker's blow lands at its natural speed: the next "hit" event of the clip it plays now (or the
        /// one it blends into), over the state's playback speed. False when the clip holds no hit event (the blow comes in a
        /// later state): the learned wind-up is used then.
        static bool TimeToHit(Controller c, out float seconds, out float speed)
        {
            string clip;
            float at;
            bool ok = HitClip(c, out seconds, out clip, out at, out speed);
            if (!ok) ExactMisses++;
            return ok;
        }

        /// The clip that delivers the attacker's next blow (the one it plays now, or the one it blends into, with a "hit" event
        /// still ahead), the real time until that event, and the event's time in the clip (s). False if none holds one.
        public static int PhantomStarts, LaterBlows;

        /// The attacker's next hit event is a phantom (SwingAtlas.Phantom: the templar's wind-up hits).
        static bool PhantomNext(Controller c)
        {
            try
            {
                float sec, ev;
                string clip;
                if (!HitClip(c, out sec, out clip, out ev)) return false;
                return SwingAtlas.Phantom(SwingAtlas.Find(WeaponGrips.ModelKey(c.Model != null ? c.Model.name : c.name), clip, ev));
            }
            catch (Exception) { return false; }
        }

        public static bool HitClip(Controller c, out float seconds, out string clipName, out float eventTime)
        {
            float speed;
            return HitClip(c, out seconds, out clipName, out eventTime, out speed);
        }

        /// ... and the state's natural playback speed (clip seconds per real second; the AttackSpeed parameter's own value,
        /// not a slow it may be under).
        public static bool HitClip(Controller c, out float seconds, out string clipName, out float eventTime, out float natural)
        {
            natural = 0f;
            seconds = 0f;
            clipName = null;
            eventTime = 0f;
            try
            {
                Animator anim = c != null && c.Model != null && c.Animator != null ? c.Animator.Animator : null;
                if (anim == null) return false;
                for (int pass = 0; pass < 2; pass++)
                {
                    bool next = pass == 1;
                    if (next && !anim.IsInTransition(0)) break;
                    AnimatorStateInfo st = next ? anim.GetNextAnimatorStateInfo(0) : anim.GetCurrentAnimatorStateInfo(0);
                    AnimatorClipInfo[] infos = next ? anim.GetNextAnimatorClipInfo(0) : anim.GetCurrentAnimatorClipInfo(0);
                    // the state's multiplier is the AttackSpeed parameter as the animator last applied it: right after a
                    // slowed strike of this attacker (the next blow of a templar multi-hit or a combo) it still holds that
                    // slow, and the blow's natural time came out up to 5x too long, its lead too short (0.18 s seen); the
                    // parameter itself already holds the natural speed again (Unslow): read it instead
                    float mult = st.speedMultiplier;
                    if (Mathf.Abs(mult - 1f) > 0.001f) mult = anim.GetFloat(s_attackSpeedHash);
                    float speed = st.speed * mult;
                    if (infos == null || speed <= 0.01f) continue;
                    foreach (AnimatorClipInfo ci in infos)
                    {
                        AnimationClip clip = ci.clip;
                        if (clip == null || ci.weight < 0.3f || clip.length <= 0.01f) continue;
                        float t = (st.normalizedTime - Mathf.Floor(st.normalizedTime)) * clip.length;
                        foreach (AnimationEvent ev in clip.events)
                        {
                            if (ev.stringParameter != "hit" || ev.time < t - 0.01f) continue;
                            float dt = (ev.time - t) / speed;
                            if (dt < 0.05f || dt > 3f) continue;
                            seconds = dt;
                            natural = speed;
                            clipName = clip.name;
                            eventTime = ev.time;
                            return true;
                        }
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        sealed class Warn
        {
            public Controller Ctrl;
            public AIController Ai;
            public Model Model;
            public bool Picked, Near, OffView;
            public float AnnouncedAt = -1f, HeldAt = -10f, LastInView = -10f;
            public float AskedAt = -10f;   // the last time it asked to attack from out of view (the gates)
            public bool Striking, Blockable, OffAtStart;
            public float StrikeStart, Expected = DefaultWindup, Factor = 1f;
            public bool Exact;   // the natural wind-up read from the attack's own animation
            public float Speed = -1f;   // ... and its state's natural playback speed then (audit E3: the gaps to its later blows)
            public bool Global;  // slowed by the animator's own speed (its state ignores AttackSpeed)
            public float Want;              // the lead this strike should get (its class's, + the off-view bonus)
            public float FindBy = -1f;      // no "hit" event at the telegraph: looked for each frame until then (s)
            public float HoldUntil = -1f;   // Lead = Hold: the attack waits (at HoldF) until then, then swings at its natural speed
            public float HoldF = HoldFactor;   // the hold's speed: HoldFactor, stiller for a short wind-up under a long lead (HoldSpeed)
            public float SlowCheckAt = -1f;  // Lead = Slow: the next check of the wind-up against its plan (SlowCheck; -1 none)
            public string Trace = "";       // Lead = Slow: the plan and each check (TraceOf; printed with a short lead)
            public float HoldFor;           // ... and how long that wait was planned (s)
            public string Key = "";
            public float Bearing;
            public float NextBeat, DubAt = -1f;
            public bool BeatL, BeatR;
            public AudioSource Tone;
            public AudioLowPassFilter Lp;
        }

        readonly Dictionary<int, Warn> m_warns = new Dictionary<int, Warn>();
        readonly List<int> m_drop = new List<int>();
        static readonly Dictionary<string, float> s_windup = new Dictionary<string, float>();
        Transform[] m_markers, m_fills, m_halos;
        Material m_haloMat;
        Material m_mat;
        static bool s_live;
        static GameObject s_player;

        // diagnostics and the behind test (`warn?`)
        public static int Announces, HeldAnnounce, HeldSecond, HeldUnblockable, OffViewStarts, Stings, Heartbeats, MarkersShown, Strikes, RedBehind, MaxLiveOff;
        public static float MinAnnounceLead = 99f;   // announcement -> telegraph, off-view strikes of announced attackers
        public static float MinBonusShare = 99f;     // (telegraph -> impact) - natural, over the bonus, off-view strikes
        public static int UnannouncedOff;            // off-view strikes whose attacker was never announced (not polite)
        public static int TurnedAway;                // off-view strikes whose attacker was in view up to 0.6 s before
        /// P2-10: shots in flight from out of view that got the off-view marker (distinct shots), and its frames; off-view
        /// ranged wind-ups (a shooter's telegraph begun out of view).
        public static int ShotWarns, ShotWarnFrames, OffViewRanged;
        static int s_fightLogs;                      // log lines of this fight (the announcements: 40 per fight, not per run)
        static readonly HashSet<int> s_shotsWarned = new HashSet<int>();
        static readonly List<ShieldShots.Incoming> s_incoming = new List<ShieldShots.Incoming>();
        float m_shotBeat, m_shotDub = -1f;
        bool m_shotBeatL, m_shotBeatR;
        public static string Last = "-";
        static readonly List<string> s_log = new List<string>();

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(AIManager), "CanCombatantAttack", new[] { typeof(AITarget), typeof(AIController), typeof(bool) }),
                    postfix: new HarmonyMethod(typeof(ThreatWarning), nameof(CanAttackPostfix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("ThreatWarning: attack gate patch failed: " + e.Message); }
            try { h.Patch(AccessTools.Method(typeof(Defiant.Action), "Evaluate"), prefix: new HarmonyMethod(typeof(ThreatWarning), nameof(EvaluatePrefix))); }
            catch (Exception e) { Plugin.L.LogWarning("ThreatWarning: strike gate patch failed: " + e.Message); }
        }

        void Awake()
        {
            I = this;
            SeedAtlas();
            Diag.StatusExtras["threats"] = () => Diag.Q(m_warns.Count + " tracked, announces " + Announces + ", off-view starts " + OffViewStarts + ", markers " + MarkersShown +
                                                        ", last " + Last);
            Diag.Commands["threats"] = a =>
            {
                if (a.Length > 2 && a[1] == "view") ViewHalf = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                var sb = new System.Text.StringBuilder();
                foreach (Warn w in m_warns.Values)
                    sb.Append(" | ").Append(w.Ctrl != null ? w.Ctrl.name : "?").Append(" bearing ").Append(w.Bearing.ToString("0")).Append(w.Picked ? " picked" : "")
                      .Append(w.AnnouncedAt >= 0f ? " announced " + (FightClock.Now - w.AnnouncedAt).ToString("0.0") + " s" : "").Append(w.Striking ? (w.OffAtStart ? " STRIKE off-view" : " strike") : "");
                return m_warns.Count + " tracked" + sb + " (view half " + ViewHalf + ")";
            };
            // `warn?`: the counts the behind test checks; `warn? log` also logs every off-view strike
            Diag.Commands["warn?"] = a =>
            {
                if (a.Length > 1 && a[1] == "log") Plugin.L.LogInfo("off-view strikes:\n  " + string.Join("\n  ", s_log.ToArray()));
                if (a.Length > 1 && a[1] == "reset")
                {
                    Announces = HeldAnnounce = HeldSecond = HeldUnblockable = OffViewStarts = Stings = Heartbeats = MarkersShown = Strikes = RedBehind = MaxLiveOff = UnannouncedOff = TurnedAway = 0;
                    ShotWarns = ShotWarnFrames = OffViewRanged = 0;
                    s_shotsWarned.Clear();
                    MinAnnounceLead = MinBonusShare = 99f;
                    s_log.Clear();
                    return "reset";
                }
                return "strikes " + Strikes + " laterBlows " + LaterBlows + " offview " + OffViewStarts + " announces " + Announces + " heldAnnounce " + HeldAnnounce + " heldSecond " + HeldSecond +
                       " heldUnblockable " + HeldUnblockable + " redBehind " + RedBehind + " maxLiveOff " + MaxLiveOff + " minAnnounceLead " + MinAnnounceLead.ToString("0.00") +
                       " unannounced " + UnannouncedOff + " turnedAway " + TurnedAway + " minBonusShare " + MinBonusShare.ToString("0.00") + " stings " + Stings + " heartbeats " + Heartbeats + " markers " + MarkersShown +
                       " shotWarns " + ShotWarns + " shotWarnFrames " + ShotWarnFrames + " offViewRanged " + OffViewRanged +
                       " pace " + CombatSettings.Pace + " announce " + CombatSettings.OffViewAnnounce + " bonus " + CombatSettings.OffViewBonus;
            };
        }

        // ---------------------------------------------------------------------------------------- geometry

        /// Horizontal angle of a world point from the gaze (deg, + = to the right).
        public static float BearingOf(Vector3 p)
        {
            if (VRRig.I == null) return 0f;
            Vector3 to = p - VRRig.I.HeadWorldPos;
            to.y = 0f;
            Vector3 gaze = VRRig.I.HeadWorldRot * Vector3.forward;
            gaze.y = 0f;
            if (to.sqrMagnitude < 1e-4f || gaze.sqrMagnitude < 1e-6f) return 0f;
            return Vector3.SignedAngle(gaze, to, Vector3.up);
        }

        /// The shot log's view of an attacker (Guard.Shots): how long ago it was announced (-1: not announced) and how long
        /// ago it was last in view (-1: not tracked).
        internal static void ViewOf(Controller c, out float announcedAgo, out float inViewAgo)
        {
            announcedAgo = inViewAgo = -1f;
            Warn w;
            if (I == null || c == null || !I.m_warns.TryGetValue(c.GetInstanceID(), out w)) return;
            float now = FightClock.Now;
            if (w.AnnouncedAt >= 0f) announcedAgo = now - w.AnnouncedAt;
            inViewAgo = now - w.LastInView;
        }

        Warn Get(Controller c)
        {
            Warn w;
            if (!m_warns.TryGetValue(c.GetInstanceID(), out w)) m_warns[c.GetInstanceID()] = w = new Warn { Ctrl = c, Ai = c as AIController };
            return w;
        }

        bool OtherOffViewStrikeLive(Controller c)
        {
            foreach (Warn w in m_warns.Values)
                if (w.Striking && w.OffAtStart && w.Ctrl != c) return true;
            return false;
        }

        static float Natural(string key)
        {
            float v;
            if (s_windup.TryGetValue(key, out v)) return v;
            int i = key.IndexOf('#');   // an attack of a type: the type's seed (the Phase 0 atlas)
            return i > 0 && s_windup.TryGetValue(key.Substring(0, i), out v) ? v : DefaultWindup;
        }

        /// Which of its attacks the attacker winds up (its hit's direction): one enemy type's attacks wind up differently
        /// (an empire guard 0.53-0.68 s, Phase 0), so each is learned on its own.
        static string Variant(Controller c)
        {
            try
            {
                Actor actor = c != null ? c.GetComponent<Actor>() : null;
                IActionAICombat act = actor != null ? actor.GetRunningAction<IActionAICombat>() : null;
                ICombatEffectContainer cont = act as ICombatEffectContainer;
                CombatEffectHit hit = cont != null ? Guard.FirstHit(cont.CombatEffects, 0) : null;
                string a = act != null ? act.GetType().Name : "";
                return "#" + (a.StartsWith("Action") ? a.Substring(6) : a) + (hit != null ? "/" + hit.Direction + "/" + hit.VerticalDirection : "");
            }
            catch (Exception) { return "#?"; }
        }

        static string KeyOf(Model m) => m == null ? "" : WeaponGrips.ModelKey(m.name);

        /// The telegraph-to-impact time of the strike this attacker is winding up now (its learned time if none is live).
        /// For the lead diagnostics: the attacker's live strike (natural wind-up, factor, its telegraph's age).
        public static string StrikeState(Controller c)
        {
            Warn w;
            if (I == null || c == null || !I.m_warns.TryGetValue(c.GetInstanceID(), out w)) return "no warn";
            return "striking " + w.Striking + " key " + w.Key + (w.Exact ? " exact" : "") + " natural " + Natural(w.Key).ToString("0.00") + " factor " + w.Factor.ToString("0.00") +
                   " expected " + w.Expected.ToString("0.00") + " since " + (FightClock.Now - w.StrikeStart).ToString("0.00");
        }

        /// Lead = Slow: this attacker's plan and checks (the strike now or the last one).
        public static string TraceOf(Controller c)
        {
            Warn w;
            return I != null && c != null && I.m_warns.TryGetValue(c.GetInstanceID(), out w) ? w.Trace : "";
        }

        /// The attacker's animator state now (and the one it blends into) by its clips' names, for the traces.
        static string StateName(Controller c)
        {
            try
            {
                Animator anim = c != null && c.Model != null && c.Animator != null ? c.Animator.Animator : null;
                if (anim == null) return "-";
                AnimatorClipInfo[] cur = anim.GetCurrentAnimatorClipInfo(0);
                string r = cur != null && cur.Length > 0 && cur[0].clip != null ? cur[0].clip.name : "-";
                if (anim.IsInTransition(0))
                {
                    AnimatorClipInfo[] nx = anim.GetNextAnimatorClipInfo(0);
                    r += ">" + (nx != null && nx.Length > 0 && nx[0].clip != null ? nx[0].clip.name : "-");
                }
                return r;
            }
            catch (Exception) { return "?"; }
        }

        public static float ExpectedOf(Controller c)
        {
            Warn w;
            if (I != null && c != null && I.m_warns.TryGetValue(c.GetInstanceID(), out w) && w.Striking) return w.Expected;
            return c != null ? Natural(KeyOf(c.Model)) : DefaultWindup;
        }

        /// Guard (audit E3): this attacker's strike now began out of view, so the off-view rule gave it its longer lead
        /// (OnStart's off-view branch) - its timing is that lead, not its run's rhythm.
        public static bool StartedOffView(Controller c)
        {
            Warn w;
            return I != null && c != null && Settings.OffViewWarning && I.m_warns.TryGetValue(c.GetInstanceID(), out w) && w.Striking && w.OffAtStart;
        }

        /// The natural playback speed of the attacker's strike state (clip seconds per real second): read at its telegraph,
        /// before a hold slowed it; after its strike, from the animation now. -1 if unknown.
        public static float NaturalSpeed(Controller c)
        {
            Warn w;
            if (I != null && c != null && I.m_warns.TryGetValue(c.GetInstanceID(), out w) && w.Striking) return w.Exact ? w.Speed : -1f;
            float sec, ev, sp;
            string clip;
            return HitClip(c, out sec, out clip, out ev, out sp) ? sp : -1f;
        }

        /// The "hit" events of the attacker's clip `clipName` (the state it plays now, or the one it blends into) after
        /// `afterEvent` (clip seconds), in order, into `evs`; their count.
        public static int LaterHits(Controller c, string clipName, float afterEvent, List<float> evs)
        {
            evs.Clear();
            try
            {
                Animator anim = c != null && c.Model != null && c.Animator != null ? c.Animator.Animator : null;
                if (anim == null || string.IsNullOrEmpty(clipName)) return 0;
                for (int pass = 0; pass < 2 && evs.Count == 0; pass++)
                {
                    if (pass == 1 && !anim.IsInTransition(0)) break;
                    AnimatorClipInfo[] infos = pass == 1 ? anim.GetNextAnimatorClipInfo(0) : anim.GetCurrentAnimatorClipInfo(0);
                    if (infos == null) continue;
                    foreach (AnimatorClipInfo ci in infos)
                    {
                        if (ci.clip == null || ci.clip.name != clipName) continue;
                        foreach (AnimationEvent ev in ci.clip.events)
                            if (ev.stringParameter == "hit" && ev.time > afterEvent + 0.01f) evs.Add(ev.time);
                        break;
                    }
                }
                evs.Sort();
            }
            catch (Exception) { evs.Clear(); }
            return evs.Count;
        }

        /// Seeds of the learned wind-ups: the Phase 0 strike atlas (docs/combat-probes.md, telegraph to impact, median).
        static void SeedAtlas()
        {
            if (s_windup.Count > 0) return;
            s_windup["ThievesThugModel"] = 0.48f;
            s_windup["NorseWarriorModel"] = 0.67f;
            s_windup["ShadowWarriorModel"] = 0.83f;
            s_windup["CorruptedTankModel"] = 0.82f;
            s_windup["CorruptedMinionModel"] = 0.62f;
            s_windup["EmpireGuardModel"] = 0.68f;
        }

        /// Physical mode: the lead every strike gets from its telegraph to its impact (§8.5.5; Stage 2 item 9: by the
        /// attacker's UYF archetype, EnemyTable); 0 in Classic mode (the game's own timing).
        static float LeadFor(Controller c)
        {
            if (!CombatSettings.Physical) return 0f;
            return EnemyTable.LeadFor(c);   // UYF's lead of the enemy's archetype at the difficulty (item 9, A)
        }

        // ---------------------------------------------------------------------------------------- the gates

        /// The game's own "may this AI attack its target now?" (polite attackers): not before an off-view attacker has
        /// been announced long enough, and not while another strike from outside the view is live.
        static void CanAttackPostfix(AITarget a_target, AIController a_ai, ref bool __result)
        {
            if (!__result || !s_live || I == null || a_ai == null || a_target == null) return;
            try
            {
                if (a_target.Targetable == null || a_target.Targetable.gameObject != s_player) return;
                float b = Mathf.Abs(BearingOf(a_ai.transform.position));
                if (b <= ViewHalf) return;
                if (I.HoldForAnnouncement(I.Get(a_ai))) { __result = false; return; }
                if (I.OtherOffViewStrikeLive(a_ai)) { HeldSecond++; __result = false; }
            }
            catch (Exception) { }
        }

        /// Guard's waiting shooters (UYF: they shoot whoever holds the attack slot) are let through after this class's own
        /// postfix has run: the same gates for one out of view - announced OffViewAnnounce first, one off-view strike at a time.
        public static bool HoldsOffView(AIController ai)
        {
            if (!s_live || I == null || ai == null) return false;
            try
            {
                if (Mathf.Abs(BearingOf(ai.transform.position)) <= ViewHalf) return false;
                if (I.HoldForAnnouncement(I.Get(ai))) return true;
                if (I.OtherOffViewStrikeLive(ai)) { HeldSecond++; return true; }
            }
            catch (Exception) { }
            return false;
        }

        /// An attacker out of view may not start before it has been announced for OffViewAnnounce (announced now if not).
        bool HoldForAnnouncement(Warn w)
        {
            float now = FightClock.Now;
            w.AskedAt = now;
            if (w.AnnouncedAt < 0f) Announce(w, now);
            if (now - w.AnnouncedAt >= CombatSettings.OffViewAnnounce) return false;
            HeldAnnounce++;
            w.HeldAt = now;
            return true;
        }

        static readonly Dictionary<Type, bool> s_strikeType = new Dictionary<Type, bool>();
        static readonly Dictionary<int, AIController> s_aiOf = new Dictionary<int, AIController>();

        internal static bool IsStrike(Defiant.Action a)
        {
            Type t = a.GetType();
            bool v;
            if (s_strikeType.TryGetValue(t, out v)) return v;
            v = a is IActionAICombat && !(a is ActionSelfAI) && !(a is ActionSelfInAnimateAI) && !(a is ActionSelfOrcShaman) && !(a is ActionSelfSkeletonKing) &&
                !(a is ActionHitByCount) && !(a is ActionAntiBully) && !(a is ActionDashCompanion);
            s_strikeType[t] = v;
            return v;
        }

        /// Whether the player can answer this strike with a counter (the game's green glow).
        static bool Counterable(Defiant.Action a)
        {
            if (a is ActionMeleeComboAI || a is ActionMeleeThrall) return false;
            var c = a as ICombatEffectContainer;
            if (c == null) return false;
            bool b;
            try { b = CombatUtils.IsBlockable(c); } catch (Exception) { return true; }
            if (b && (a is ActionRangedAI || a is ActionRangedGunslinger || a is ActionRangedSkeletonQueen))
            {
                PlayerController pc = PlayerController.Instance;
                b = pc != null && (pc.Combat.Reflect > 0 || pc.Combat.Defend > 0);
            }
            return b;
        }

        /// Every enemy strike action (polite or not) before it starts: one off-view strike at a time, and no strike that
        /// can't be countered from behind (Relaxed / Normal).
        static bool EvaluatePrefix(Defiant.Action __instance)
        {
            if (!s_live || __instance.IsRunning) return true;
            try
            {
                if (!IsStrike(__instance)) return true;
                Actor actor = __instance.Actor;
                if (actor == null) return true;
                int id = actor.GetInstanceID();
                AIController ai;
                if (!s_aiOf.TryGetValue(id, out ai)) s_aiOf[id] = ai = actor.GetComponent<AIController>();
                if (ai == null || ai.Target == null || ai.Target.gameObject != s_player) return true;
                float b = Mathf.Abs(BearingOf(actor.transform.position));
                if (b <= ViewHalf) return true;
                // the attacker the game picked, or any enemy within striking distance, waits for its announcement (the
                // game decides to engage before the approach, so a strike can start without asking again)
                Warn w;
                if (I.m_warns.TryGetValue(ai.GetInstanceID(), out w) && (w.Picked || w.Near) && I.HoldForAnnouncement(w)) return false;
                if (I.OtherOffViewStrikeLive(ai)) { HeldSecond++; return false; }
                if (b > BehindAngle && !CombatSettings.UnblockableFromBehind && BossBattleManager.Instance == null && !Counterable(__instance)) { HeldUnblockable++; return false; }
            }
            catch (Exception) { }
            return true;
        }

        /// The attacker's running strike is a shot or a throw (its warning goes on with the shot's flight: P2-10).
        static bool IsRangedStrike(Controller c)
        {
            try
            {
                Actor actor = c != null ? c.GetComponent<Actor>() : null;
                return actor != null && (actor.GetRunningAction<ActionRangedAI>() != null || actor.GetRunningAction<ActionRangedSkeletonQueen>() != null ||
                                         actor.GetRunningAction<ActionRangedGunslinger>() != null);
            }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------------------------------- announcement

        static bool Dead(Controller c)
        {
            Destroyable d = c != null ? c.GetComponent<Destroyable>() : null;
            return c == null || (d != null && d.IsDead);
        }

        void Announce(Warn w, float now)
        {
            w.AnnouncedAt = now;
            Announces++;
            if (++s_fightLogs <= 40)
                Plugin.L.LogInfo("ThreatWarning: announce #" + Announces + " " + w.Ctrl.name + "#" + w.Ctrl.GetInstanceID() + " bearing " + BearingOf(w.Ctrl.transform.position).ToString("0") +
                                 (w.Picked ? " picked" : "") + (w.Near ? " near" : "") + " offView " + w.OffView + " striking " + w.Striking);
            w.Bearing = BearingOf(w.Ctrl.transform.position);
            // one tick on that side (both when behind)
            if (Settings.OffViewWarning)
            {
                bool behind = Mathf.Abs(w.Bearing) > BehindAngle;
                // mark 21: a firmer tick (was 0.6 x 20 ms)
                if (behind || w.Bearing < 0f) Haptics.Pulse(true, 0.9f, 0.045f);
                if (behind || w.Bearing > 0f) Haptics.Pulse(false, 0.9f, 0.045f);
            }
            Last = "announced " + w.Ctrl.name + " at " + w.Bearing.ToString("0") + " deg";
        }

        // ---------------------------------------------------------------------------------------- strikes

        /// CombatInput's telegraph hook: an attack aimed at the player winds up (the game's glow starts).
        public void OnStart(Model model, bool blockable)
        {
            if (model == null || !CombatVR.Active) return;
            Controller ctrl = model.GetComponentInParent<Controller>();
            if (ctrl == null) return;
            Warn w = Get(ctrl);
            float now = FightClock.Now;
            if (w.Striking) End(w, false, "restarted");
            w.Model = model;
            w.Striking = true;
            w.StrikeStart = now;
            w.Blockable = blockable;
            w.Key = KeyOf(model) + Variant(ctrl);
            w.Bearing = BearingOf(model.transform.position);
            w.OffAtStart = Mathf.Abs(w.Bearing) > ViewHalf;
            Strikes++;
            float v0, exact = 0f;
            float natural = s_windup.TryGetValue(w.Key, out v0) ? v0 : CombatSettings.Physical ? UnseenWindup : Natural(w.Key);
            // Physical mode: the attack's own animation says when its blow lands (its "hit" event), so its lead is exact from
            // the first strike on - no guess, no overshoot (a shield bearer winding up too long holds the attack slot too long
            // and counter-attacks instead of raising its shield: env 1, 2026-10-12)
            float clipSpeed = -1f;
            w.Exact = CombatSettings.Physical && TimeToHit(ctrl, out exact, out clipSpeed);
            w.Speed = w.Exact ? clipSpeed : -1f;
            w.Want = LeadFor(ctrl) + (w.OffAtStart && Settings.OffViewWarning ? CombatSettings.OffViewBonus : 0f);
            w.SlowCheckAt = -1f;
            w.Trace = "";
            // no hit event yet (the attack's clip not begun at the telegraph): its lead planned on the learned wind-up, then
            // again once the event shows (MR4: a shadow warrior's blow 0.83 s of 0.90 on a learned 0.84 s)
            w.FindBy = !w.Exact && CombatSettings.Physical && CombatSettings.LeadStyle == CombatSettings.LeadRule.Hold ? now + 0.4f : -1f;
            if (w.Exact) { natural = exact; ExactLeads++; }
            if (w.Exact && PhantomNext(ctrl))
            {
                // audit item 12: this window's hit event is a wind-up's (the blade behind the attacker): it runs at its own speed,
                // no lead, no warning - the next blow's window brings those
                w.Factor = 1f;
                w.Expected = natural;
                PhantomStarts++;
                return;
            }
            if (Shoves.Shoving(ctrl))
            {
                // UYF's shove (merge-ready #4): untelegraphed, at its own speed - no lead held, no warning
                w.Factor = 1f;
                w.Expected = natural;
                return;
            }
            if (!blockable && Mathf.Abs(w.Bearing) > BehindAngle) RedBehind++;
            if (w.OffAtStart && Settings.OffViewWarning)
            {
                OffViewStarts++;
                bool ranged = IsRangedStrike(ctrl);
                if (ranged) OffViewRanged++;
                if (++s_fightLogs <= 40)
                    Plugin.L.LogInfo("ThreatWarning: off-view " + (ranged ? "ranged " : "") + "strike " + ctrl.name + " bearing " + w.Bearing.ToString("0") + (blockable ? "" : " (red)") +
                                     (w.AnnouncedAt >= 0f ? " announced " + (now - w.AnnouncedAt).ToString("0.00") + " s before" : now - w.LastInView < 0.6f ? " (turned away from it)" : " NOT announced"));
                // an attack that began while the attacker was in view and the player then turned away: no gate could
                // hold it (it gets the longer wind-up and the warnings all the same)
                if (now - w.LastInView < 0.6f) TurnedAway++;
                else if (w.AnnouncedAt >= 0f) MinAnnounceLead = Mathf.Min(MinAnnounceLead, now - w.AnnouncedAt);
                else UnannouncedOff++;
                float bonus = CombatSettings.OffViewBonus;
                if (!StartHold(w, ctrl, natural, Mathf.Max(natural, LeadFor(ctrl)) + bonus, now))
                {
                    w.Factor = Mathf.Clamp(natural / (Mathf.Max(natural, LeadFor(ctrl)) + bonus), SlowFloor, 1f);
                    EnemyTuning.Slow(ctrl, w.Factor);
                    w.Expected = natural / w.Factor;
                    if (CombatSettings.Physical) w.SlowCheckAt = now + SlowCheckEvery;
                    w.Trace = "plan off " + (w.Exact ? "exact " : "learned ") + natural.ToString("0.00") + " x" + w.Factor.ToString("0.00") + " exp " + w.Expected.ToString("0.00");
                }
                w.NextBeat = now;
                StartSound(w);
                int live = 0;
                foreach (Warn o in m_warns.Values) if (o.Striking && o.OffAtStart) live++;
                MaxLiveOff = Mathf.Max(MaxLiveOff, live);
                Last = (ctrl.name) + " at " + w.Bearing.ToString("0") + " deg, " + (blockable ? "counterable" : "not counterable") + ", natural " + natural.ToString("0.00") + " s, x" + w.Factor.ToString("0.00");
            }
            else
            {
                // Physical mode: at least the class's lead (the game's own leads are 0.48-0.83 s)
                // - but a later blow of a run whose marker already shows (audit E3: Guard shows it ahead, at its own lead) keeps
                // the run's own rhythm: no hold, no slow
                if (w.Exact && Guard.AheadShown(ctrl)) { w.Factor = 1f; w.Expected = natural; w.HoldUntil = -1f; LaterBlows++; }
                else if (!StartHold(w, ctrl, natural, Mathf.Max(natural, LeadFor(ctrl)), now))
                {
                    w.Factor = Mathf.Clamp(natural / Mathf.Max(natural, LeadFor(ctrl)), SlowFloor, 1f);
                    if (w.Factor < 0.999f) EnemyTuning.Slow(ctrl, w.Factor);
                    w.Expected = natural / w.Factor;
                    w.SlowCheckAt = now + SlowCheckEvery;
                    w.Trace = "plan " + (w.Exact ? "exact " : "learned ") + natural.ToString("0.00") + " want " + LeadFor(ctrl).ToString("0.00") + " x" + w.Factor.ToString("0.00") +
                              " exp " + w.Expected.ToString("0.00") + " " + StateName(ctrl);
                }
            }
        }

        /// UYF's lead (docs/uyf-parity-audit.md E2, item 9; its AttackTimelinePlayer shifts the sequence: the premonition shows
        /// `lead` before the impact and the animation waits, then plays at natural speed x AttackSpeed): with `[Combat] Lead =
        /// Hold` the attack runs at HoldFactor (all but still) for the time its natural wind-up is short of the lead, then at its
        /// own speed - no slow-motion swing. The total is the same lead: HoldFor x HoldFactor + (natural - HoldFor x HoldFactor).
        public const float HoldFactor = 0.1f;
        public static int Holds, HoldReleases;

        static bool StartHold(Warn w, Controller ctrl, float natural, float lead, float now)
        {
            if (!CombatSettings.Physical || CombatSettings.LeadStyle != CombatSettings.LeadRule.Hold) return false;
            float extra = lead - natural;
            if (extra <= 0.02f) { w.Factor = 1f; w.Expected = natural; w.HoldUntil = -1f; return true; }
            float f = HoldSpeed(natural, lead);
            w.HoldF = f;
            w.HoldFor = extra / (1f - f);
            w.HoldUntil = now + w.HoldFor;
            w.Factor = f;
            w.Expected = lead;
            EnemyTuning.Slow(ctrl, f);
            Holds++;
            return true;
        }

        /// The hold's speed for a wind-up of `natural` s under a lead of `lead` s. A hold at HoldFactor still moves the wind-up
        /// on (HoldFor x HoldFactor); a short wind-up under a long lead - a run's later blow (0.1 s left) whose telegraph came
        /// off view (1.7 s) - landed inside its hold, after natural / HoldFactor (MR4g: 0.95 s of 1.70; the "unreleased" holds
        /// of MR4e/MR4f). Such a hold is stiller: what it moves on stays within 80 % of the wind-up.
        static float HoldSpeed(float natural, float lead)
        {
            return Mathf.Clamp(0.8f * natural / Mathf.Max(0.01f, lead), 0.005f, HoldFactor);
        }

        public static int Replans, LateExact;

        /// Lead = Slow (the slow-motion wind-up): every SlowCheckEvery the wind-up is checked against its plan. A slow that took
        /// late or not at all - the state blended into ignored AttackSpeed until then, or the learned wind-up was long - brought
        /// a blow ahead of its lead (MR4g's leadhold, Slow: 0.87 and 0.78 s of 0.90; the 14 Oct soak's ShadowWarrior 0.78 s,
        /// slowed). By the hit event's real time left at natural speed, the rest of the wind-up is stretched over the rest of
        /// the lead (the animator's own speed when the state ignores AttackSpeed). Only ever slower: a blow behind its plan is
        /// left alone (the lead is a minimum).
        public const float SlowCheckEvery = 0.1f;

        /// Lead = Slow: the slowest a wind-up is stretched (x0.05: a blow whose hit event is 0.15 s ahead at its telegraph
        /// gets its 0.90 s, or 1.70 off view). It was x0.2: such a blow came after 0.75-0.78 s, short (test reliability
        /// 2026-10-17 round 2: leadhold's Slow phase, ShadowElder / ShadowWarrior 0.78 and 0.55 s of 0.90 - the 0.2 floor,
        /// with natural 0.15 / 0.11 s at the telegraph). Hold already goes down to x0.005 (HoldSpeed).
        public const float SlowFloor = 0.05f;
        public static int SlowChecks, SlowFixes;
        public static float SlowFixMax;   // the largest lead a fix made up (s)

        void SlowCheck(Warn w, float now)
        {
            if (w.SlowCheckAt < 0f || now < w.SlowCheckAt || w.HoldUntil >= 0f) return;
            float el = now - w.StrikeStart, rest = w.Expected - el, left;
            w.SlowCheckAt = rest > 0.15f ? now + SlowCheckEvery : -1f;
            if (rest <= 0.06f) return;
            if (!NaturalLeft(w.Ctrl, out left)) { if (w.Trace.Length < 600) w.Trace += " | " + el.ToString("0.00") + " no event " + StateName(w.Ctrl); return; }
            SlowChecks++;
            bool ignores = EnemyTuning.IgnoresAttackSpeed(w.Ctrl);
            // the speed the wind-up runs at now, against its natural speed
            float cur = w.Global ? EnemyTuning.GlobalOf(w.Ctrl) * (ignores ? 1f : EnemyTuning.SlowOf(w.Ctrl)) : ignores ? 1f : EnemyTuning.SlowOf(w.Ctrl);
            float due = left / Mathf.Max(0.05f, cur);
            if (w.Trace.Length < 600)
                w.Trace += " | " + el.ToString("0.00") + " left " + left.ToString("0.00") + " x" + cur.ToString("0.00") + (ignores ? "i" : "") + " due " + due.ToString("0.00") + "/" + rest.ToString("0.00");
            if (due >= rest - 0.04f) return;
            float f = Mathf.Clamp(left / rest, SlowFloor, 1f);
            if (f >= 0.999f) return;
            SlowFixes++;
            w.Trace += " fix x" + f.ToString("0.00");
            SlowFixMax = Mathf.Max(SlowFixMax, rest - due);
            if (SlowFixes <= 40)
                Plugin.L.LogInfo("ThreatWarning: Lead = Slow, " + w.Ctrl.name + " ahead of its lead: the blow due in " + due.ToString("0.00") + " s of " + rest.ToString("0.00") +
                                 " (" + el.ToString("0.00") + " s in, " + StrikeState(w.Ctrl) + "), now x" + f.ToString("0.00") + (ignores ? " (the animator's speed)" : ""));
            if (ignores || w.Global) { w.Global = true; EnemyTuning.GlobalSlow(w.Ctrl, ignores ? f : f / Mathf.Max(0.05f, EnemyTuning.SlowOf(w.Ctrl))); }
            else EnemyTuning.Slow(w.Ctrl, f);
        }

        /// A hold planned again from now (Lead = Hold): what is left of the strike's lead over what is left of its natural
        /// wind-up `left` - its state ignored AttackSpeed until now (the wind-up ran at full speed), or its hit event showed only
        /// after the telegraph. Held from now (begun if none was), released at once when nothing is left.
        void Replan(Warn w, float now, float left)
        {
            float rest = w.Want - (now - w.StrikeStart);
            float f = HoldSpeed(left, rest);
            float hold = (rest - left) / (1f - f);
            Replans++;
            if (hold <= 0.02f)
            {
                if (w.HoldUntil >= 0f) { w.HoldUntil = now; ReleaseHold(w, now); }
                return;
            }
            w.HoldUntil = now + hold;
            w.HoldFor = hold;
            w.Expected = (now - w.StrikeStart) + left + hold * (1f - f);
            bool fresh = w.Factor > HoldFactor + 0.001f;   // not held until now: a hold begins
            w.HoldF = f;
            if (fresh || Mathf.Abs(w.Factor - f) > 0.001f)
            {
                w.Factor = f;
                EnemyTuning.Slow(w.Ctrl, f);
                if (w.Global) EnemyTuning.GlobalSlow(w.Ctrl, f);
                if (fresh) Holds++;
            }
        }

        /// The real time to the attacker's next "hit" event at the attack's natural speed (its state's speed x the attacker's own
        /// attack speed, not a slow or a hold it may be under; the animator's global speed left out).
        static bool NaturalLeft(Controller c, out float left)
        {
            left = 0f;
            try
            {
                Animator anim = c != null && c.Model != null && c.Animator != null ? c.Animator.Animator : null;
                if (anim == null) return false;
                for (int pass = 0; pass < 2; pass++)
                {
                    bool next = pass == 1;
                    if (next && !anim.IsInTransition(0)) break;
                    AnimatorStateInfo st = next ? anim.GetNextAnimatorStateInfo(0) : anim.GetCurrentAnimatorStateInfo(0);
                    AnimatorClipInfo[] infos = next ? anim.GetNextAnimatorClipInfo(0) : anim.GetCurrentAnimatorClipInfo(0);
                    float mult = Mathf.Abs(st.speedMultiplier - 1f) > 0.001f ? c.CalculateAttackSpeed() / EnemyTuning.SlowOf(c) : 1f;
                    float speed = st.speed * mult;
                    if (infos == null || speed <= 0.01f) continue;
                    foreach (AnimatorClipInfo ci in infos)
                    {
                        AnimationClip clip = ci.clip;
                        if (clip == null || ci.weight < 0.3f || clip.length <= 0.01f) continue;
                        float t = (st.normalizedTime - Mathf.Floor(st.normalizedTime)) * clip.length;
                        foreach (AnimationEvent ev in clip.events)
                        {
                            if (ev.stringParameter != "hit" || ev.time < t - 0.01f) continue;
                            float dt = (ev.time - t) / speed;
                            if (dt < 0.02f || dt > 3f) continue;
                            left = dt;
                            return true;
                        }
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        /// Every frame: a hold whose time is up lets the attack swing at its own speed.
        void ReleaseHold(Warn w, float now)
        {
            if (w.HoldUntil < 0f || now < w.HoldUntil || w.Ctrl == null) return;
            w.HoldUntil = -1f;
            EnemyTuning.Unslow(w.Ctrl);
            if (w.Global) { EnemyTuning.GlobalUnslow(w.Ctrl); w.Global = false; }
            w.Factor = 1f;
            HoldReleases++;
        }

        static readonly AccessTools.FieldRef<ActionMeleeAI, bool> s_hasHit = AccessTools.FieldRefAccess<ActionMeleeAI, bool>("m_hasHit");

        public void OnStop(Model model)
        {
            if (model == null) return;
            foreach (Warn w in m_warns.Values)
                if (w.Model == model && w.Striking) { bool hit = ReachedHit(w); End(w, hit, hit ? "hit while held" : "stopped"); return; }
        }

        /// The strike got to its impact (its "hit" event), not cut short by a hit, a stagger or the end of the fight: only
        /// those measure the natural wind-up.
        static bool ReachedHit(Warn w)
        {
            try
            {
                Actor a = w.Ctrl != null ? w.Ctrl.GetComponent<Actor>() : null;
                ActionMeleeAI m = a != null ? a.GetRunningAction<ActionMeleeAI>() : null;
                if (m != null) return s_hasHit(m);
            }
            catch (Exception) { }
            return true;
        }

        /// Holds that ended with their strike, before their release (leadhold's accounting: holds = released + these), by why.
        public static int HoldsCut;
        static readonly Dictionary<string, int> s_cutWhy = new Dictionary<string, int>();
        public static string LastCut = "-";
        public static string CutReasons()
        {
            var parts = new List<string>();
            foreach (var kv in s_cutWhy) parts.Add(kv.Key + " " + kv.Value);
            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "-";
        }
        public static void ResetCuts() { HoldsCut = 0; s_cutWhy.Clear(); LastCut = "-"; }

        /// The strike is over (impact, or the window closed): learn its natural wind-up, stop its slow-down and sound.
        void End(Warn w, bool learn, string why = "ended")
        {
            float dur = FightClock.Now - w.StrikeStart;
            if (w.HoldUntil >= 0f)
            {
                // a hold its strike did not wait out (cut short, a new telegraph, the attacker gone): not released, ended here
                HoldsCut++;
                int n;
                s_cutWhy[why] = s_cutWhy.TryGetValue(why, out n) ? n + 1 : 1;
                LastCut = w.Key + " " + why + " " + dur.ToString("0.00") + " s after its telegraph, " + (w.HoldUntil - FightClock.Now).ToString("0.00") + " s of its hold left";
                if (s_log.Count < 200) s_log.Add("hold cut: " + LastCut);
            }
            if (learn && dur > 0.15f && dur < 8f && w.Key.Length > 0)
            {
                // the slowed part ran at Factor of the game's speed; a hold ran HoldFor at HoldFactor, the rest at full speed
                float nat = w.HoldFor > 0f ? dur - w.HoldFor * (1f - w.HoldF) : dur * w.Factor;
                float old;
                // a strike held up on the way (a hit-stop, a counter's freeze, the game's antic hold) is no measure of the
                // natural wind-up: much longer ones are not learned (a too long estimate would make the next lead short)
                if (CombatFeel.StoppedSince(w.Ctrl, w.StrikeStart - 0.05f)) { }   // frozen by a hit-stop on the way: no measure
                else if (!s_windup.TryGetValue(w.Key, out old)) s_windup[w.Key] = nat;
                // erring short is safe (a longer lead); erring long makes the next lead short: down at once, up slowly
                else if (nat < old && nat >= old * 0.6f) s_windup[w.Key] = nat;
                else if (nat >= old && nat <= old * 1.6f) s_windup[w.Key] = Mathf.Lerp(old, nat, 0.1f);
                if (w.OffAtStart && w.Factor < 0.999f)
                {
                    float bonus = CombatSettings.OffViewBonus;
                    float share = (dur - nat) / Mathf.Max(0.01f, bonus);
                    MinBonusShare = Mathf.Min(MinBonusShare, share);
                    if (s_log.Count < 200)
                        s_log.Add(w.Key + " bearing " + w.Bearing.ToString("0") + " telegraph->impact " + dur.ToString("0.00") + " s (natural " + nat.ToString("0.00") + " s, extra " +
                                  (dur - nat).ToString("0.00") + " s)" + (w.AnnouncedAt >= 0f ? " announced " + (w.StrikeStart - w.AnnouncedAt).ToString("0.00") + " s before" : " not announced"));
                }
            }
            if ((w.Factor < 0.999f || w.HoldUntil >= 0f) && w.Ctrl != null) EnemyTuning.Unslow(w.Ctrl);
            if (w.Global) { EnemyTuning.GlobalUnslow(w.Ctrl); w.Global = false; }
            w.Factor = 1f;
            w.HoldUntil = -1f;
            w.HoldFor = 0f;
            w.HoldF = HoldFactor;
            w.Striking = false;
            w.DubAt = -1f;
            StopSound(w);
        }

        // ---------------------------------------------------------------------------------------- sound

        void StartSound(Warn w)
        {
            float vol = CombatSettings.OffViewSound;
            if (vol <= 0f || w.Model == null) return;
            try
            {
                if (w.Tone == null)
                {
                    w.Tone = CombatSounds.Source("HoF2VR_WarnTone", CombatSounds.Riser, true, 30f);
                    w.Lp = w.Tone.gameObject.AddComponent<AudioLowPassFilter>();
                }
                w.Tone.transform.position = w.Model.transform.position + Vector3.up * 1.4f;
                w.Tone.PlayOneShot(CombatSounds.Sting, vol);
                w.Tone.volume = 0f;
                w.Tone.pitch = 1f;
                w.Tone.Play();
                Stings++;
            }
            catch (Exception e) { Plugin.L.LogWarning("ThreatWarning: sound: " + e.Message); }
        }

        static void StopSound(Warn w)
        {
            if (w.Tone != null) w.Tone.Stop();
        }

        // ---------------------------------------------------------------------------------------- every frame

        void LateUpdate()
        {
            PlayerController pc = PlayerController.Instance;
            s_live = CombatVR.Active && XRSettings.enabled && pc != null && GameWatch.InCombatPlay && Settings.OffViewWarning;
            s_player = pc != null ? pc.gameObject : null;
            if (!CombatVR.Active)
            {
                if (m_warns.Count > 0) { foreach (Warn w in m_warns.Values) { End(w, false, "combat over"); if (w.Tone != null) Destroy(w.Tone.gameObject); } m_warns.Clear(); }
                s_fightLogs = 0;
                s_shotsWarned.Clear();
                EnemyTuning.UnslowAll();
                s_aiOf.Clear();
                return;
            }
            float now = FightClock.Now;
            // the attack slots: an attacker picked while out of view is announced
            AIController melee = null, ranged = null;
            try
            {
                AITarget at = pc != null ? pc.GetComponent<Targetable>().AITarget : null;
                if (at != null) { melee = at.MeleeAttacker; ranged = at.RangedAttacker; }
            }
            catch (Exception) { }
            foreach (Warn w in m_warns.Values) { w.Picked = false; w.Near = false; }
            if (melee != null && !Dead(melee)) Get(melee).Picked = true;   // (no array a frame: the audit, item 8)
            if (ranged != null && !Dead(ranged)) Get(ranged).Picked = true;
            // enemies within striking distance (4 m) are tracked too: non-polite ones strike without being picked
            try
            {
                Targetable me = pc.GetComponent<Targetable>();
                BetterList<Targetable> near = me.EnumerateWithinRadius(4f, me.GetEnemyTeams());
                for (int i = 0; i < near.size; i++)
                {
                    AIController ai = near[i] != null ? near[i].GetComponent<AIController>() : null;
                    if (ai != null && !Dead(ai)) Get(ai).Near = true;
                }
            }
            catch (Exception) { }
            foreach (Warn w in m_warns.Values)
            {
                if (w.Ctrl == null) continue;
                if (!w.Striking) w.Bearing = BearingOf(w.Ctrl.transform.position);
                float ab = Mathf.Abs(w.Bearing);
                // out of view beyond 55 deg, back in view under 45 deg (no flicker at the edge)
                bool off = w.OffView ? ab > ViewHalf - 10f : ab > ViewHalf;
                if (off && !w.OffView && (w.Picked || w.Near) && !w.Striking && s_live && w.AnnouncedAt < 0f) Announce(w, now);
                if (!off) { w.AnnouncedAt = -1f; w.LastInView = now; }   // seen: the next time it is out of view it is announced again
                w.OffView = off;
            }
            m_drop.Clear();
            foreach (var kv in m_warns)
            {
                Warn w = kv.Value;
                Destroyable d = w.Ctrl != null ? w.Ctrl.GetComponent<Destroyable>() : null;
                if (w.Ctrl == null || (d != null && d.IsDead))
                {
                    End(w, false, "dead");
                    if (w.Tone != null) Destroy(w.Tone.gameObject);
                    m_drop.Add(kv.Key);
                    continue;
                }
                // no threat now - but an attacker that asks to attack from out of view (a shooter far off, neither picked nor
                // near) keeps its warning: dropped and made again each frame, it was announced again each frame (a haptic
                // tick per frame: 4019 announcements in 2 min of the lobs fixture, the user's musketeer 24 in a row on
                // 17 October) and never waited out its announcement
                if (!w.Picked && !w.Near && !w.Striking && now - w.AskedAt > 1.5f)
                {
                    if (w.Tone != null) Destroy(w.Tone.gameObject);
                    m_drop.Add(kv.Key);
                    continue;
                }
                if (!w.Striking) continue;
                if (now - w.StrikeStart > w.Expected + 4f) { End(w, false, "never resolved"); continue; }   // never resolved
                ReleaseHold(w, now);
                if (w.FindBy >= 0f)
                {
                    float left;
                    if (now > w.FindBy || w.Global) w.FindBy = -1f;
                    else if (NaturalLeft(w.Ctrl, out left)) { w.FindBy = -1f; w.Exact = true; LateExact++; Replan(w, now, left); }
                }
                // the slow-down didn't take (the state ignores AttackSpeed): by the animator's speed, kept each frame - for
                // strikes shown with a bar only (a gnome's red leap at the hero shows none: slowed this way it hung in the
                // air for 2.3 s instead of its natural 0.8 s, its natural time unknown - no "hit" event, never learned)
                if (w.HoldUntil >= 0f && !w.Global && now - w.StrikeStart > 0.05f && Guard.Shows(w.Ctrl) && EnemyTuning.IgnoresAttackSpeed(w.Ctrl))
                {
                    // a hold on a state that ignores AttackSpeed: by the animator's own speed until the release. Until now the
                    // wind-up ran at full speed, so the hold is planned again from here: what is left of the lead over what is
                    // left of the wind-up (MR4: shadow warriors' fresh blows 0.85-0.87 s after their markers, of 0.90)
                    w.Global = true;
                    float left;
                    if (NaturalLeft(w.Ctrl, out left)) Replan(w, now, left);
                    if (w.HoldUntil >= 0f) EnemyTuning.GlobalSlow(w.Ctrl, w.HoldF);
                }
                else if (w.HoldUntil < 0f && w.Factor < 0.999f && !w.Global && now - w.StrikeStart > 0.05f && Guard.Shows(w.Ctrl) && EnemyTuning.IgnoresAttackSpeed(w.Ctrl))
                {
                    // what ran at full speed until now is made up for: the rest of the natural wind-up stretched over the
                    // rest of the lead
                    float el = now - w.StrikeStart, natural = w.Factor * w.Expected;
                    float f = natural - el > 0.02f && w.Expected - el > 0.05f ? Mathf.Clamp((natural - el) / (w.Expected - el), Mathf.Min(SlowFloor, w.Factor), w.Factor) : w.Factor;
                    w.Global = true;
                    EnemyTuning.GlobalSlow(w.Ctrl, f);
                }
                SlowCheck(w, now);
                if (w.Global) EnemyTuning.KeepGlobalSlow(w.Ctrl);
                if (w.Model != null) w.Bearing = BearingOf(w.Model.transform.position);
                if (!w.OffAtStart || !Settings.OffViewWarning) continue;
                float progress = Mathf.Clamp01((now - w.StrikeStart) / Mathf.Max(0.1f, w.Expected));
                float behindness = Mathf.InverseLerp(90f, 180f, Mathf.Abs(w.Bearing));
                // sound: the tone follows the attacker, rises to the impact, muffled from behind
                if (w.Tone != null && w.Model != null)
                {
                    w.Tone.transform.position = w.Model.transform.position + Vector3.up * 1.4f;
                    w.Tone.volume = CombatSettings.OffViewSound * Mathf.Lerp(0.35f, 1f, progress * progress);   // mark 21: louder from the start
                    w.Tone.pitch = Mathf.Lerp(0.8f, 1.6f, progress);
                    // with the HRTF on, the HRTF itself makes it sound behind you: no extra muffling (HeadShadowWithHrtf), and
                    // the warning is never the voice dropped when more than 32 play
                    bool hrtf = Hrtf.On(w.Tone);
                    if (w.Lp != null) w.Lp.cutoffFrequency = hrtf && !Settings.HeadShadowWithHrtf ? 22000f : Mathf.Lerp(22000f, 6000f, behindness);
                    w.Tone.priority = hrtf ? 16 : 128;
                }
                // heartbeat: lub-dub on that side (both when behind), every 0.5 s speeding up to 0.2 s
                bool both = Mathf.Abs(w.Bearing) > BehindAngle;
                if (now >= w.NextBeat && GameWatch.InCombatPlay)
                {
                    w.BeatL = both || w.Bearing < 0f;
                    w.BeatR = both || w.Bearing > 0f;
                    if (w.BeatL) Haptics.Pulse(true, 1f, 0.03f);
                    if (w.BeatR) Haptics.Pulse(false, 1f, 0.03f);
                    w.DubAt = now + 0.11f;
                    w.NextBeat = now + Mathf.Lerp(0.5f, 0.2f, progress);
                    Heartbeats++;
                }
                if (w.DubAt > 0f && now >= w.DubAt)
                {
                    if (w.BeatL) Haptics.Pulse(true, 0.8f, 0.02f);
                    if (w.BeatR) Haptics.Pulse(false, 0.8f, 0.02f);
                    w.DubAt = -1f;
                }
            }
            foreach (int id in m_drop) m_warns.Remove(id);
            ShotBeats(now);
        }

        /// P2-10 (the user's second playtest: "I had no warning for the fire guy. He just shot fire at me, and I couldn't see
        /// him"): a shot or a throw flying at the player from out of view keeps the off-view warning after it leaves the
        /// shooter's hand - the marker at the edge of the view toward the shooter (Place) and the heartbeat on that side,
        /// faster as it comes - until it lands, is stopped or misses. The strike's own warning ended at the throw, while the
        /// bottle still had a second to fly; the shot's bar sits toward the shooter, out of view too.
        void ShotBeats(float now)
        {
            ShieldShots.IncomingShots(s_incoming);
            if (!s_live) return;
            float bestProgress = -1f, bearing = 0f;
            foreach (ShieldShots.Incoming sh in s_incoming)
            {
                float b = BearingOf(sh.From);
                if (Mathf.Abs(b) <= ViewHalf || StrikeWarned(sh.Src)) continue;
                if (sh.Progress > bestProgress) { bestProgress = sh.Progress; bearing = b; }
            }
            if (bestProgress < 0f) { m_shotDub = -1f; return; }
            if (now >= m_shotBeat && GameWatch.InCombatPlay)
            {
                bool both = Mathf.Abs(bearing) > BehindAngle;
                m_shotBeatL = both || bearing < 0f;
                m_shotBeatR = both || bearing > 0f;
                if (m_shotBeatL) Haptics.Pulse(true, 1f, 0.03f);
                if (m_shotBeatR) Haptics.Pulse(false, 1f, 0.03f);
                m_shotDub = now + 0.11f;
                m_shotBeat = now + Mathf.Lerp(0.4f, 0.15f, bestProgress);
                Heartbeats++;
            }
            if (m_shotDub > 0f && now >= m_shotDub)
            {
                if (m_shotBeatL) Haptics.Pulse(true, 0.8f, 0.02f);
                if (m_shotBeatR) Haptics.Pulse(false, 0.8f, 0.02f);
                m_shotDub = -1f;
            }
        }

        /// The shooter's own strike warning still shows (its wind-up began out of view and has not ended).
        bool StrikeWarned(Controller c)
        {
            Warn w;
            return c != null && m_warns.TryGetValue(c.GetInstanceID(), out w) && w.Striking && w.OffAtStart;
        }

        // ---------------------------------------------------------------------------------------- the marker in view

        static MaterialPropertyBlock s_mpb;
        // green and red = the game's own telegraph glows (FXIndicator_MeleeCounterable #00FE3D, ..Uncounterable #FE0303;
        // docs/combat-probes.md)
        static readonly Color Amber = new Color(1f, 0.68f, 0.12f), Green = new Color(0f, 0.996f, 0.239f), Red = new Color(0.996f, 0.012f, 0.012f);

        /// The overlay camera's pre-cull (head pose of this render): the markers in front of the head.
        public void Place(Transform head)
        {
            int shown = 0;
            if (CombatVR.Active && Settings.OffViewWarning && GameWatch.InCombatPlay)
            {
                float now = FightClock.Now;
                float sc = VRRig.I != null ? VRRig.I.Scale : 1f;
                foreach (Warn w in m_warns.Values)
                {
                    if (shown >= MaxMarkers) break;
                    if (Mathf.Abs(w.Bearing) <= ViewHalf) continue;
                    bool strike = w.Striking && w.OffAtStart;
                    bool announced = !w.Striking && w.AnnouncedAt >= 0f && (w.Picked || now - w.HeldAt < 0.5f);
                    if (!strike && !announced) continue;
                    int i = shown++;
                    Transform m = Marker(i), f = m_fills[i];
                    float phi = (90f - w.Bearing) * Mathf.Deg2Rad;   // radar: ahead = up, right = right, behind = down
                    float r = Mathf.Tan(MarkerRadius * Mathf.Deg2Rad);
                    Vector3 local = new Vector3(Mathf.Cos(phi) * r, Mathf.Sin(phi) * r, 1f);
                    Quaternion rot = head.rotation * Quaternion.Euler(0f, 0f, 90f - w.Bearing);
                    Vector3 p = head.position + head.rotation * (local * sc);
                    float size = Mathf.Tan((strike ? MarkerSize : AnnounceSize) * Mathf.Deg2Rad) * sc;
                    float progress = strike ? Mathf.Clamp01((now - w.StrikeStart) / Mathf.Max(0.1f, w.Expected)) : 0f;
                    Color c = strike ? (w.Blockable || Guard.RedBlocks ? (CombatSettings.Physical ? Premonition.Cyan : Green) : Red) : Amber;
                    float pulse = 0.75f + 0.25f * Mathf.Sin((now - (strike ? w.StrikeStart : w.AnnouncedAt)) * Mathf.Lerp(8f, 22f, progress));
                    if (strike && Mathf.Abs(w.Bearing) > BehindAngle) size *= BehindBoost;
                    m.SetPositionAndRotation(p, rot);
                    m.localScale = Vector3.one * size;
                    var mpb = s_mpb ?? (s_mpb = new MaterialPropertyBlock());
                    mpb.SetColor("_Color", new Color(c.r, c.g, c.b, strike ? 1f : 0.9f * pulse));
                    m.GetComponent<MeshRenderer>().SetPropertyBlock(mpb);
                    if (!m.gameObject.activeSelf) { m.gameObject.SetActive(true); MarkersShown++; }
                    Halo(i, p + head.rotation * Vector3.forward * (0.002f * sc), rot, size, c, (strike ? 0.5f : 0.35f) * pulse);
                    // the fill grows from the arrow's middle until the impact
                    bool fill = strike && progress > 0.02f;
                    if (fill)
                    {
                        f.SetPositionAndRotation(p + head.rotation * Vector3.forward * (-0.001f * sc), rot);
                        f.localScale = Vector3.one * size * Mathf.Lerp(0.15f, 0.82f, progress);
                        Color cf = Color.Lerp(c, Color.white, 0.35f);
                        mpb.SetColor("_Color", new Color(cf.r, cf.g, cf.b, 0.7f + 0.3f * pulse));
                        f.GetComponent<MeshRenderer>().SetPropertyBlock(mpb);
                    }
                    if (f.gameObject.activeSelf != fill) f.gameObject.SetActive(fill);
                }
                // P2-10: shots flying at the player from out of view (ShotBeats): the strike's marker goes on toward the shooter
                foreach (ShieldShots.Incoming sh in s_incoming)
                {
                    if (shown >= MaxMarkers) break;
                    float bearing = BearingOf(sh.From);
                    if (Mathf.Abs(bearing) <= ViewHalf || StrikeWarned(sh.Src)) continue;
                    int i = shown++;
                    Transform m = Marker(i), f = m_fills[i];
                    float phi = (90f - bearing) * Mathf.Deg2Rad;
                    float r = Mathf.Tan(MarkerRadius * Mathf.Deg2Rad);
                    Vector3 local = new Vector3(Mathf.Cos(phi) * r, Mathf.Sin(phi) * r, 1f);
                    Quaternion rot = head.rotation * Quaternion.Euler(0f, 0f, 90f - bearing);
                    Vector3 p = head.position + head.rotation * (local * sc);
                    float size = Mathf.Tan(ShotMarkerSize * Mathf.Deg2Rad) * sc;
                    Color c = sh.Blockable ? (CombatSettings.Physical ? Premonition.Cyan : Green) : Red;
                    float pulse = 0.75f + 0.25f * Mathf.Sin(now * Mathf.Lerp(8f, 22f, sh.Progress));
                    m.SetPositionAndRotation(p, rot);
                    m.localScale = Vector3.one * size;
                    var mpb = s_mpb ?? (s_mpb = new MaterialPropertyBlock());
                    mpb.SetColor("_Color", new Color(c.r, c.g, c.b, 1f));
                    m.GetComponent<MeshRenderer>().SetPropertyBlock(mpb);
                    if (!m.gameObject.activeSelf) { m.gameObject.SetActive(true); MarkersShown++; }
                    Halo(i, p + head.rotation * Vector3.forward * (0.002f * sc), rot, size, c, 0.6f * pulse);
                    f.SetPositionAndRotation(p + head.rotation * Vector3.forward * (-0.001f * sc), rot);
                    f.localScale = Vector3.one * size * Mathf.Lerp(0.15f, 0.82f, sh.Progress);
                    Color cf = Color.Lerp(c, Color.white, 0.35f);
                    mpb.SetColor("_Color", new Color(cf.r, cf.g, cf.b, 0.7f + 0.3f * pulse));
                    f.GetComponent<MeshRenderer>().SetPropertyBlock(mpb);
                    if (!f.gameObject.activeSelf) f.gameObject.SetActive(true);
                    ShotWarnFrames++;
                    if (s_shotsWarned.Add(sh.Id)) ShotWarns++;
                }
            }
            if (m_markers != null)
                for (int i = shown; i < m_markers.Length; i++)
                {
                    if (m_markers[i] != null && m_markers[i].gameObject.activeSelf) m_markers[i].gameObject.SetActive(false);
                    if (m_fills[i] != null && m_fills[i].gameObject.activeSelf) m_fills[i].gameObject.SetActive(false);
                    if (m_halos != null && m_halos[i] != null && m_halos[i].gameObject.activeSelf) m_halos[i].gameObject.SetActive(false);
                }
        }

        Transform Marker(int i)
        {
            if (m_markers == null) { m_markers = new Transform[MaxMarkers]; m_fills = new Transform[MaxMarkers]; m_halos = new Transform[MaxMarkers]; }
            if (m_markers[i] != null) return m_markers[i];
            if (m_mat == null) m_mat = new Material(Shader.Find("Sprites/Default")) { renderQueue = 5150 };
            m_markers[i] = MakeObj("HoF2VR_ThreatMarker" + i, Arrow(false));
            m_fills[i] = MakeObj("HoF2VR_ThreatFill" + i, Arrow(true));
            if (m_haloMat == null) m_haloMat = new Material(Premonition.Additive()) { renderQueue = 5149 };
            m_halos[i] = MakeObj("HoF2VR_ThreatHalo" + i, Arrow(true));
            m_halos[i].GetComponent<MeshRenderer>().sharedMaterial = m_haloMat;
            return m_markers[i];
        }

        public static int HaloFrames;

        /// Marks 18 / 21: a soft additive halo behind the arrow (brighter, seen at the edge of the view).
        void Halo(int i, Vector3 p, Quaternion rot, float size, Color c, float a)
        {
            if (m_halos == null || m_halos[i] == null) return;
            Transform h = m_halos[i];
            h.SetPositionAndRotation(p, rot);
            h.localScale = Vector3.one * size * HaloScale;
            var mpb = s_mpb ?? (s_mpb = new MaterialPropertyBlock());
            mpb.SetColor("_Color", new Color(c.r, c.g, c.b, Mathf.Clamp01(a)));
            h.GetComponent<MeshRenderer>().SetPropertyBlock(mpb);
            if (!h.gameObject.activeSelf) h.gameObject.SetActive(true);
            HaloFrames++;
        }

        Transform MakeObj(string name, Mesh mesh)
        {
            var go = new GameObject(name) { layer = VRUI.Layer };
            DontDestroyOnLoad(go);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = m_mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.SetActive(false);
            return go.transform;
        }

        static Mesh s_outline, s_solid;

        /// An arrow head pointing along +x (outward), about one unit across: the outline (a thick border) or the solid
        /// fill (scaled up toward the impact).
        static Mesh Arrow(bool solid)
        {
            if (solid && s_solid != null) return s_solid;
            if (!solid && s_outline != null) return s_outline;
            Vector3 tip = new Vector3(0.5f, 0f, 0f), top = new Vector3(-0.4f, 0.45f, 0f), bot = new Vector3(-0.4f, -0.45f, 0f), notch = new Vector3(-0.15f, 0f, 0f);
            Mesh m;
            if (solid)
            {
                m = new Mesh { name = "HoF2VR_ArrowFill", vertices = new[] { tip, top, notch, bot }, colors = new[] { Color.white, Color.white, Color.white, Color.white }, triangles = new[] { 0, 1, 2, 0, 2, 3 } };
            }
            else
            {
                // outer arrow minus an inner one: a border 0.12 thick
                Vector3[] o = { tip, top, notch, bot };
                Vector3 c = (tip + top + notch + bot) / 4f;
                var v = new List<Vector3>();
                foreach (Vector3 p in o) v.Add(p);
                foreach (Vector3 p in o) v.Add(c + (p - c) * 0.68f);
                var t = new List<int>();
                for (int k = 0; k < 4; k++)
                {
                    int a = k, b = (k + 1) % 4;
                    t.AddRange(new[] { a, b, 4 + b, a, 4 + b, 4 + a });
                }
                var cols = new Color[8];
                for (int k = 0; k < 8; k++) cols[k] = Color.white;
                m = new Mesh { name = "HoF2VR_ArrowOutline", vertices = v.ToArray(), colors = cols, triangles = t.ToArray() };
            }
            m.RecalculateBounds();
            if (solid) s_solid = m; else s_outline = m;
            return m;
        }
    }
}

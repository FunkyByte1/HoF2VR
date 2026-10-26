using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// `[Combat] Mode = Physical`: the lean core loop of docs/combat-uyf-design.md §8.5 (Strike: BladeHits; Guard: Guard;
    /// feedback: CombatFeel). Classic (the default) keeps the game's own moves (CombatInput's gestures press its buttons).
    /// In Physical mode:
    ///  * the triggers and gestures press nothing (VRGamepad, CombatInput); B (the game's bash) and LT (its finisher) stay as
    ///    button fallbacks, with their hero motion dropped (no lunge, no approach, no slide: the view never moves by itself);
    ///  * walking, the Dash and the warnings from behind are the same as in Classic.
    /// The fight's own clock: real time that stands still while the game is paused in a fight, so every combat timer (strikes,
    /// guards, crits, gnomes, warnings, tickets, shots) resumes where it stopped. Ticked once a frame (Guard.LateUpdate).
    internal static class FightClock
    {
        static float s_paused, s_last = -1f;
        static int s_frame = -1;
        public static float PausedTotal => s_paused;
        /// Test seam (`timescale 0 hold`): the clock also stands still while the game's time is stopped, so a test's frozen
        /// blow keeps its eta (a frozen table of shield placements outlived the strike's 8 s life in real time). Only while
        /// Time.timeScale is 0; any other `timescale` command ends it.
        public static bool Held;

        public static void Tick()
        {
            if (Time.frameCount == s_frame) return;
            s_frame = Time.frameCount;
            float t = Time.unscaledTime;
            if (s_last >= 0f && (Physical.Paused || (Held && Time.timeScale == 0f))) s_paused += t - s_last;
            s_last = t;
        }

        public static float Now { get { Tick(); return Time.unscaledTime - s_paused; } }
    }

    internal static class Physical
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static float MotionDropped;   // metres of hero motion from button fallbacks not applied
        static readonly System.Collections.Generic.HashSet<int> s_tough = new System.Collections.Generic.HashSet<int>();

        /// Physical mode and first-person combat.
        public static bool On => CombatSettings.Physical && CombatVR.Active && XRSettings.enabled;
        /// ... and the fight is live.
        public static bool Live => On && GameWatch.InCombatPlay;
        /// ... and the game is paused in it (the pause menu, the VR settings page over it): the fight's state is kept, frozen
        /// (the audit of 2026-10-18, item 3: a pause threw away every guard, freed broken enemies, dropped the coming blow).
        public static bool Paused => On && GameWatch.State == "GameState_Level_Pause";

        // ---- the push cap (§2.5.3): our hits push an enemy at most PushCap, so it stays within blade reach; a counter pushes
        // it at most 0.7 m (the riposte follows); riposte, bash and finisher hits keep the game's full push (they make space)
        public const float PushCap = 0.05f;   // UYF's weapon Pushback (DefaultWeaponStats; was 0.4 m of HoF2's own push)
        static readonly System.Collections.Generic.Dictionary<int, KeyValuePair> s_cap = new System.Collections.Generic.Dictionary<int, KeyValuePair>();
        struct KeyValuePair { public float Cap, Until; }
        sealed class Budget { public float Left, Until; }
        static readonly System.Collections.Generic.Dictionary<int, Budget> s_budget = new System.Collections.Generic.Dictionary<int, Budget>();
        static readonly AccessTools.FieldRef<ActionHit, float> s_push = AccessTools.FieldRefAccess<ActionHit, float>("m_pushDistance");
        public static int Capped;
        static int s_traced;
        public static float CappedMetres;

        /// The next hit reaction of this enemy (within 0.3 s) pushes it at most `cap` metres.
        public static void CapPush(Controller c, float cap)
        {
            if (c == null) return;
            int id = c.gameObject.GetInstanceID();
            s_cap[id] = new KeyValuePair { Cap = cap, Until = Time.unscaledTime + 0.3f };
            // the reaction's root motion (a stagger's stumble) counts against the same cap, for its first 1.5 s
            s_budget[id] = new Budget { Left = cap, Until = Time.unscaledTime + 1.5f };
        }

        /// ActionHit's root motion on an enemy we just hit or countered: at most what is left of its cap.
        static void CapDelta(ActionHit __instance, ref Vector3 a_deltaPosition)
        {
            if (s_budget.Count == 0 || !On || __instance.Actor == null) return;
            Controller ctrl = __instance.Actor.GetComponentInParent<Controller>();
            if (ctrl == null || ctrl is PlayerController) return;
            Budget b;
            int id = ctrl.gameObject.GetInstanceID();
            if (!s_budget.TryGetValue(id, out b)) return;
            if (Time.unscaledTime > b.Until) { s_budget.Remove(id); return; }
            float d = new Vector2(a_deltaPosition.x, a_deltaPosition.z).magnitude;
            if (d <= b.Left) { b.Left -= d; return; }
            float k = d > 1e-5f ? b.Left / d : 0f;
            CappedMetres += d - b.Left;
            b.Left = 0f;
            a_deltaPosition = new Vector3(a_deltaPosition.x * k, a_deltaPosition.y, a_deltaPosition.z * k);
            Capped++;
        }

        static void HitBeginPostfix(ActionHit __instance, bool __result)
        {
            if (!__result || s_cap.Count == 0 || !On || __instance.Actor == null) return;
            KeyValuePair kv;
            Controller ctrl = __instance.Actor.GetComponentInParent<Controller>() ?? __instance.Actor.GetComponentInChildren<Controller>();
            if (ctrl == null) return;
            int id = ctrl.gameObject.GetInstanceID();
            if (s_traced++ < 30) Plugin.L.LogInfo("Physical: hit reaction on " + ctrl.name + " push " + s_push(__instance).ToString("0.00") + (s_cap.ContainsKey(id) ? " (capped)" : " (no cap)"));
            if (!s_cap.TryGetValue(id, out kv)) return;
            s_cap.Remove(id);
            if (Time.unscaledTime > kv.Until) return;
            try
            {
                float p = s_push(__instance);
                if (p > kv.Cap) { CappedMetres += p - kv.Cap; s_push(__instance) = kv.Cap; Capped++; }
            }
            catch (Exception) { }
        }

        public static void Apply(Harmony h)
        {
            BladeHits.HbcPatch(h);
            BladeHits.DisablePatch(h);
            Poise.BlockPatch(h);
            Tickets.Apply(h);
            Shoves.Apply(h);
            Standby.Apply(h);
            SuperMeter.Apply(h);
            Guard.Apply(h);
            CombatFeel.Apply(h);
            KillLaunch.Apply(h);
            try
            {
                h.Patch(AccessTools.Method(typeof(ActionHit), "Begin"), postfix: new HarmonyMethod(typeof(Physical), nameof(HitBeginPostfix)));
                h.Patch(AccessTools.Method(typeof(ActionHit), "OnDeltaMove"), prefix: new HarmonyMethod(typeof(Physical), nameof(CapDelta)));
            }
            catch (Exception e) { Plugin.L.LogWarning("Physical: push cap patch: " + e.Message); }
            var drop = new HarmonyMethod(typeof(Physical), nameof(DropMove));
            var dropDelta = new HarmonyMethod(typeof(Physical), nameof(DropDelta));
            foreach (var t in new[] { typeof(ActionMelee), typeof(ActionMeleeProneFinisher), typeof(ActionMeleeFlurry) })
            {
                try
                {
                    string mv = t == typeof(ActionMelee) ? "MoveSlideToProgress" : "MoveToProgress";
                    h.Patch(AccessTools.Method(t, mv), prefix: drop);
                    h.Patch(AccessTools.Method(t, "OnDeltaMove"), prefix: dropDelta);
                }
                catch (Exception e) { Plugin.L.LogWarning("Physical: motion patch " + t.Name + ": " + e.Message); }
            }
        }

        static bool Hero(Defiant.Action a)
        {
            PlayerController pc = PlayerController.Instance;
            return pc != null && a != null && a.Actor != null && a.Actor.gameObject == pc.gameObject;
        }

        /// The button fallbacks' scripted moves (the bash's lunge, the finisher's approach, the flurry's slides): not in
        /// Physical mode.
        static bool DropMove(Defiant.Action __instance)
        {
            if (!On || !Hero(__instance)) return true;
            return false;
        }

        static void DropDelta(Defiant.Action __instance, ref Vector3 a_deltaPosition)
        {
            if (!On || !Hero(__instance)) return;
            MotionDropped += new Vector2(a_deltaPosition.x, a_deltaPosition.z).magnitude;
            a_deltaPosition = Vector3.zero;
        }

        public static void Register(GameObject root)
        {
            root.AddComponent<SwingBend>();   // the thieves' low sweep bent up to chest height (the user: "Bend them up")
            root.AddComponent<CombatFeel>();
            root.AddComponent<BladeHits>();
            root.AddComponent<Guard>();
            root.AddComponent<Poise>();
            root.AddComponent<ScreenFlash>();
            root.AddComponent<WeaponFeel>();   // UYF's held weapon: the lag and the knock (WeaponPhysics), the two-handed grip
            root.AddComponent<SeatedRules>();   // UYF's seated rules: the 5 cm duck, captains and the Aether Horror nearer
            root.AddComponent<DashMarker>();    // UYF's dash: where it lands
            root.AddComponent<WeaponTrail>();   // UYF's trail on the player's blade
            root.AddComponent<BodyLean>();      // UYF's block lean and recoil on enemies (item 18)
            // `vrcircle on|off|setting|probe`, `vrglovehud on|off|setting`, `vrweaponsound on|off|setting` (UYF's C6, I13, F5)
            Diag.Commands["vrcircle"] = a =>
            {
                if (a.Length > 1 && a[1] != "probe") CombatSettings.OverrideCircle(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return CombatMove.CircleProbe();
            };
            Diag.Commands["vrglovehud"] = a =>
            {
                if (a.Length > 1) CombatSettings.OverrideGloveHud(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return "glove hud " + CombatSettings.GloveHud;
            };
            Diag.Commands["vrweaponsound"] = a =>
            {
                if (a.Length > 1 && a[1] == "reset") WeaponTrail.RushPeak = 0f;
                else if (a.Length > 1) CombatSettings.OverrideWeaponSound(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return "weapon sound " + CombatSettings.WeaponSound + " k " + WeaponTrail.RushK.ToString("0.00", Inv) + " volume " + WeaponTrail.RushVol.ToString("0.000", Inv) +
                       " peak " + WeaponTrail.RushPeak.ToString("0.000", Inv) +
                       " pitch " + WeaponTrail.RushPitch.ToString("0.00", Inv) + " tip " + WeaponTrail.LastTipSpeed.ToString("0.0", Inv) + " m/s";
            };
            // `world? [reset]`: the blade meeting the world (item 16): impacts, scrapes, the last one
            Diag.Commands["world?"] = a =>
            {
                if (a.Length > 1 && a[1] == "reset") { WeaponTrail.WorldImpacts = 0; WeaponTrail.WorldScrapes = 0; }
                return "world impacts " + WeaponTrail.WorldImpacts + " scrapes " + WeaponTrail.WorldScrapes + " last " + WeaponTrail.LastWorld;
            };
            // `bossdeath?`: bosses killed since the boot (their flashes: `vrdiffflash`'s `last flash`)
            Diag.Commands["bossdeath?"] = a => "boss deaths " + CombatFeel.BossDeaths + " slow motions " + CombatFeel.BossSlowMos + " lowest " + CombatFeel.BossSlowLowest.ToString("0.00") + " now " + Time.timeScale.ToString("0.00") + " last flash " + ScreenFlash.Last;
            Diag.Commands["vrbossslow"] = a => { if (a.Length > 1) CombatSettings.OverrideBossSlow(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null); return "BossSlowMotion " + CombatSettings.BossSlowMotion; };
            Diag.Commands["bodylean?"] = BodyLean.Query;
            Diag.Commands["waiting?"] = a => { if (a.Length > 1 && a[1] == "reset") Standby.ResetStrafes(); return Standby.Query(); };
            Diag.Commands["vrwaiting"] = a =>
            {
                if (a.Length > 1) CombatSettings.OverrideWaiting(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return Standby.Query();
            };
            Diag.Commands["ahead?"] = a =>
            {
                if (a.Length > 1 && a[1] == "reset") Guard.ResetAhead();
                return Guard.AheadDescribe();
            };
            Diag.Commands["vrpauses"] = a =>
            {
                if (a.Length > 1 && a[1] == "reset") Guard.ResetPauses();
                else if (a.Length > 1) CombatSettings.OverrideVariedPauses(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return Guard.PausesDescribe();
            };
            Diag.Commands["vrcombogaps"] = a =>
            {
                if (a.Length > 1) CombatSettings.OverrideComboGaps(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return Guard.AheadDescribe();
            };
            Diag.Commands["vrtrail"] = a =>
            {
                if (a.Length > 1) CombatSettings.OverrideTrail(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                if (a.Length > 1 && a[1] == "reset") { WeaponTrail.Drawn = 0; WeaponTrail.PeakAlpha = 0f; }
                return "weapon trail " + CombatSettings.WeaponTrail + " drawn " + WeaponTrail.Drawn + " peak alpha " + WeaponTrail.PeakAlpha.ToString("0.00", Inv) +
                       " last tip " + WeaponTrail.LastTipSpeed.ToString("0.0", Inv) + " m/s";
            };
            // `vrlead hold|slow|setting`: [Combat] Lead live (item 9: UYF's hold, or the slowed wind-up)
            Diag.Commands["vrlead"] = a =>
            {
                // `vrlead cuts`: the holds that ended with their strike before their release, by why (leadhold's accounting)
                if (a.Length > 1 && a[1] == "cuts") return "cut " + ThreatWarning.HoldsCut + " (" + ThreatWarning.CutReasons() + ") last " + ThreatWarning.LastCut;
                if (a.Length > 1) CombatSettings.OverrideLead(a[1] == "hold" ? CombatSettings.LeadRule.Hold : a[1] == "slow" ? (CombatSettings.LeadRule?)CombatSettings.LeadRule.Slow : null);
                return "lead " + CombatSettings.LeadStyle + " (holds " + ThreatWarning.Holds + ", released " + ThreatWarning.HoldReleases + ", cut " + ThreatWarning.HoldsCut + ")" +
                       " slowChecks " + ThreatWarning.SlowChecks + " slowFixes " + ThreatWarning.SlowFixes + " slowFixMax " + ThreatWarning.SlowFixMax.ToString("0.00", Inv);
            };
            Diag.Commands["vrdiffflash"] = a =>
            {
                if (a.Length > 1) CombatSettings.OverrideDiffFlash(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return "difficulty flash " + CombatSettings.DiffFlashOn + " (flashes " + CombatSettings.DifficultyFlashes + ", last flash " + ScreenFlash.Last + ")";
            };
            Diag.Commands["vrsmartsnap"] = a =>
            {
                // `vrsmartsnap probe left|right`: the step a snap turn that way would take now (the target, or the fixed angle)
                if (a.Length > 2 && a[1] == "probe" && CombatVR.I != null)
                {
                    float st = CombatVR.I.SnapStep(a[2] == "left" ? -1 : 1);
                    return "step " + st.ToString("0", Inv) + " (fixed " + Settings.SnapAngle.ToString("0", Inv) + ", smart turns " + CombatVR.SmartSnaps + ", last " + CombatVR.LastSmartSnap + ")";
                }
                if (a.Length > 1) CombatSettings.OverrideSmartSnap(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return "smart snap " + CombatSettings.SmartSnap + " (smart turns " + CombatVR.SmartSnaps + ", last " + CombatVR.LastSmartSnap + ")";
            };
            // `vrdash uyf|short|setting`, `vrdashflick on|off|setting`: [Combat] Dash and DashFlick live (not written)
            Diag.Commands["vrdash"] = a =>
            {
                if (a.Length > 1) CombatSettings.OverrideDashKind(a[1] == "uyf" ? CombatSettings.DashRules.UYF : a[1] == "short" ? (CombatSettings.DashRules?)CombatSettings.DashRules.Short : null);
                return "dash " + CombatSettings.DashKind + " (charge back after " + CombatMove.ChargeTime.ToString("0", Inv) + " s, flick " + CombatSettings.DashFlick + ")";
            };
            Diag.Commands["vrdashflick"] = a =>
            {
                if (a.Length > 1) CombatSettings.OverrideDashFlick(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return "dash flick " + CombatSettings.DashFlick;
            };
            Diag.StatusExtras["physical"] = () => Diag.Q(CombatSettings.Mode + (Live ? " live" : "") + " | " + BladeHits.Describe() + " | " + Guard.Describe());
            // `vrcombat physical|classic|setting`: switch the combat mode live (the settings file is not written)
            Diag.Commands["vrcombat"] = a =>
            {
                if (a.Length > 1)
                {
                    if (a[1] == "physical") CombatSettings.OverrideMode(CombatSettings.CombatMode.Physical);
                    else if (a[1] == "classic") CombatSettings.OverrideMode(CombatSettings.CombatMode.Classic);
                    else CombatSettings.OverrideMode(null);
                }
                return "combat mode " + CombatSettings.Mode + " (difficulty " + CombatSettings.Difficulty + ", counter " + CombatSettings.Counter + ", lean dodge " + CombatSettings.LeanDodge + ")";
            };
            // `vrcounter off|meet|anyblock|setting`, `vrlean on|off|setting`: [Combat] Counter and LeanDodge live (not written)
            Diag.Commands["vrcounter"] = a =>
            {
                if (a.Length > 1)
                {
                    if (a[1] == "off") CombatSettings.OverrideCounter(CombatSettings.CounterRule.Off);
                    else if (a[1] == "meet") CombatSettings.OverrideCounter(CombatSettings.CounterRule.Meet);
                    else if (a[1] == "anyblock") CombatSettings.OverrideCounter(CombatSettings.CounterRule.AnyBlock);
                    else CombatSettings.OverrideCounter(null);
                }
                return "counter " + CombatSettings.Counter;
            };
            Diag.Commands["premo"] = a => Guard.Demo(a);
            // `vrcommit on|off`: UYF's committed attacker (its facing held around each blow) on or off (the before/after test)
            Diag.Commands["vrcommit"] = a =>
            {
                if (a.Length > 1) Guard.CommitsOn = a[1] != "off";
                return "enemy commits " + Guard.CommitsOn + " (holds " + Guard.FacingHolds + ", most turned back " + Guard.FacingHeldDeg.ToString("0.0", Inv) + " deg)";
            };
            Diag.Commands["vrlean"] = a =>
            {
                if (a.Length > 1) CombatSettings.OverrideLean(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return "lean dodge " + CombatSettings.LeanDodge;
            };
            // `vrthrow point|hand|setting`: [Combat] Throwing live (the settings file is not written)
            Diag.Commands["vrthrow"] = a =>
            {
                if (a.Length > 1)
                {
                    if (a[1] == "point") CombatSettings.OverrideThrowing(CombatSettings.ThrowMode.Point);
                    else if (a[1] == "hand") CombatSettings.OverrideThrowing(CombatSettings.ThrowMode.Hand);
                    else CombatSettings.OverrideThrowing(null);
                }
                return "throwing " + CombatSettings.Throwing;
            };
            Diag.Commands["vrpace"] = a =>
            {
                if (a.Length > 1)
                {
                    if (a[1] == "relaxed") CombatSettings.OverridePace(CombatSettings.PaceMode.Relaxed);
                    else if (a[1] == "normal") CombatSettings.OverridePace(CombatSettings.PaceMode.Normal);
                    else if (a[1] == "fast") CombatSettings.OverridePace(CombatSettings.PaceMode.Fast);
                    else CombatSettings.OverridePace(null);
                }
                return "pace " + CombatSettings.Pace + " (difficulty " + CombatSettings.Difficulty + "): swap grace " +
                       CombatSettings.SwapGrace.ToString("0.00") + " s, guard " + CombatSettings.GuardTolerance.ToString("0.000") + " m, dodge " + CombatSettings.DodgeDistance.ToString("0.00") + " m";
            };
            // `vrdiff easy|normal|hard|nightmare1|nightmare2|nightmare3|setting`: [Combat] Difficulty live (not written)
            Diag.Commands["vrdiff"] = a =>
            {
                if (a.Length > 1)
                {
                    CombatSettings.DifficultyLevel d;
                    bool ok = false;
                    try { d = (CombatSettings.DifficultyLevel)Enum.Parse(typeof(CombatSettings.DifficultyLevel), a[1], true); CombatSettings.OverrideDifficulty(d); ok = true; CombatSettings.DifficultyFlash(); } catch (Exception) { }
                    if (!ok) CombatSettings.OverrideDifficulty(null);
                }
                return "difficulty " + CombatSettings.Difficulty + " (step " + CombatSettings.Step + "): announce " + CombatSettings.OffViewAnnounce.ToString("0.00", Inv) + " s, bonus " +
                       CombatSettings.OffViewBonus.ToString("0.00", Inv) + " s, dash window " + CombatSettings.DashWindow.ToString("0.00", Inv) + " s, unblockable from behind " +
                       CombatSettings.UnblockableFromBehind + ", swap grace " + CombatSettings.SwapGrace.ToString("0.00", Inv) + " s";
            };
            // `leads? [reset]`: each judged blow's lead (telegraph -> impact), what its archetype wants at the difficulty, its attack speed
            Diag.Commands["leads?"] = a =>
            {
                if (a.Length > 1 && a[1] == "reset") { Guard.Leads.Clear(); return "reset"; }
                var sb = new System.Text.StringBuilder(Guard.Leads.Count + " blows:");
                foreach (Guard.LeadRow r in Guard.Leads)
                    sb.Append(' ').Append(r.Model).Append('/').Append(r.Arch).Append('@').Append(r.Step).Append(' ').Append(r.Lead.ToString("0.00", Inv)).Append('/')
                      .Append(r.Want.ToString("0.00", Inv)).Append(" x").Append(r.Speed.ToString("0.00", Inv)).Append(';');
                return sb.ToString();
            };
            // `rangedshots? [reset]`: every enemy shot or throw at the player since the reset (the archer test): at s, shooter,
            // gap since the last shot's last frame / the global cooldown due, bearing, announced s before (-: never), last in view
            // s before, "melee" when a melee strike was live
            Diag.Commands["rangedshots?"] = a =>
            {
                if (a.Length > 1 && a[1] == "reset") { Guard.Shots.Clear(); Guard.ShotsSince = Time.unscaledTime; return "reset"; }
                var sb = new System.Text.StringBuilder(Guard.Shots.Count + " shots, cooldown now " + CombatSettings.Difficulty + ":");
                foreach (Guard.ShotRow r in Guard.Shots)
                    sb.Append(' ').Append(r.At.ToString("0.0", Inv)).Append(' ').Append(r.Who).Append(" gap ").Append(r.Gap > 50f ? "first" : r.Gap.ToString("0.00", Inv)).Append('/')
                      .Append(r.Cooldown.ToString("0.0", Inv)).Append(" bearing ").Append(r.Bearing.ToString("0", Inv)).Append(" announced ")
                      .Append(r.AnnouncedAgo < 0f ? "-" : r.AnnouncedAgo.ToString("0.00", Inv)).Append(" inview ").Append(r.InViewAgo < 0f ? "-" : r.InViewAgo.ToString("0.00", Inv))
                      .Append(r.MeleeLive ? " melee" : "").Append(';');
                return sb.ToString();
            };
            // `pcombat? [reset|log]`: Physical mode's counts (strike, guard, feedback)
            Diag.Commands["pcombat?"] = a =>
            {
                if (a.Length > 1 && a[1] == "reset")
                {
                    BladeHits.ResetCounts();
                    Guard.ResetCounts();
                    Poise.ResetCounts();
                    ScreenFlash.Flashes = 0;
                    CombatFeel.Impacts = CombatFeel.Stops = CombatFeel.SlowMos = CombatFeel.Vignettes = CombatFeel.Bursts = CombatFeel.Pushes = 0;
                    CombatFeel.UnearnedSlowFrames = CombatFeel.EarnedSlowFrames = 0;
                    MotionDropped = 0f;
                    return "reset";
                }
                if (a.Length > 1 && a[1] == "log") Plugin.L.LogInfo("Physical combat log:\n  blade:\n  " + BladeHits.Log() + "\n  guard:\n  " + Guard.Log() + "\n  poise:\n  " + Poise.Log());
                return CombatSettings.Mode + " | " + BladeHits.Describe() + " | " + Guard.Describe() + " | " + Poise.Describe() + " | flashes " + ScreenFlash.Flashes + " flash " + ScreenFlash.Last + " | feel impacts " + CombatFeel.Impacts + " stops " + CombatFeel.Stops +
                       " lastStopMs " + CombatFeel.LastStopMs.ToString("0") + " slowmos " + CombatFeel.SlowMos + " earnedSlow " + CombatFeel.EarnedSlowFrames +
                       " unearnedSlow " + CombatFeel.UnearnedSlowFrames + " vignettes " + CombatFeel.Vignettes + " bursts " + CombatFeel.Bursts + " pushes " + CombatFeel.Pushes +
                       " layers " + CombatSounds.Layers + " snared " + Snared + " struggles " + Struggles + " snareFxHidden " + SnareFxHidden + " capped " + Capped + " (" + CappedMetres.ToString("0.0") + " m) motionDropped " + MotionDropped.ToString("0.00") + " timescale " + (Game.Instance != null ? Game.Instance.TimeScale.ToString("0.00") : "-");
            };
            Diag.Commands["bladetest"] = a =>
            {
                if (!TestMode.Enabled) return "test mode only";
                if (!Live) return "not a live Physical fight";
                return BladeHits.Test(a.Length > 1 ? a[1] : "nearest", a.Length > 2 ? a[2] : "slash");
            };
            Diag.Commands["strike?"] = a => Guard.Query();
            // `ringframe [d|w <v>]`: the shield's frame ring (IndicatorLooks.ShieldRing.Frame*: its diameter, its band), this run only
            Diag.Commands["ringframe"] = a =>
            {
                if (a.Length > 2)
                {
                    float v = float.Parse(a[2], Inv);
                    switch (a[1])
                    {
                        case "d": IndicatorLooks.ShieldRing.FrameD = v; break;
                        case "w": IndicatorLooks.ShieldRing.FrameW = v; break;
                        default: return "keys: d w";
                    }
                }
                Vector3 c, n;
                float r;
                bool held = Guard.Shield(out c, out n, out r);
                return "frame d " + IndicatorLooks.ShieldRing.FrameD.ToString("0.000", Inv) + " w " + IndicatorLooks.ShieldRing.FrameW.ToString("0.000", Inv) +
                       " line on shield " + Guard.LineOnShield.ToString("0.00", Inv) +
                       " | shield " + (held ? "held r " + r.ToString("0.000", Inv) + " -> frame inner " + IndicatorLooks.ShieldRing.FrameInner(r).ToString("0.000", Inv) +
                       " at " + Diag.V(c) : "not held") + " | rule: the shield's outline covers >= " + Guard.ShieldCover.ToString("0.00", Inv) +
                       " of the circle r " + Guard.CircleR.ToString("0.000", Inv);
            };
            Diag.Commands["guardwhy"] = a => Guard.WhyNow();
            // Stage 2 item 1: `poise? [enemy]`, `poiseset <enemy> <guard>` (0 = break it now), `vrdamage crits|everyhit|setting`,
            // `vrcombo N|setting`, `vrguard X|setting`, `flash <alpha> [white|yellow|red] [seconds]`
            Diag.Commands["poise?"] = a => Poise.Query(a.Length > 1 ? a[1] : "nearest");
            Diag.Commands["poiseset"] = a =>
            {
                if (!TestMode.Enabled) return "test mode only";
                if (a.Length < 3) return "usage: poiseset <enemy|nearest> <guard>";
                return Poise.Set(a[1], float.Parse(a[2], Inv));
            };
            Diag.Commands["blockcond"] = a => Poise.BlockConditions(a.Length > 1 ? a[1] : "nearest");
            // `vrcritwindow <k>|setting` (test mode): interrupt crits open k times as long, the combo waiting for them
            Diag.Commands["vrcritwindow"] = a =>
            {
                if (!TestMode.Enabled) return "test mode only";
                float k;
                if (a.Length > 1 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, Inv, out k)) Poise.InterruptStretch = Mathf.Clamp(k, 1f, 6f);
                else if (a.Length > 1) Poise.InterruptStretch = 1f;
                return "interrupt window x" + Poise.InterruptStretch.ToString("0.0", Inv);
            };
            // `aside <enemy> [m]` (test mode): every other enemy at least m (3) from that one - a test's cut or swing at it meets
            // nobody else on the way (CPZ1, env 1's main run: a follow-up cut taken by another thief)
            Diag.Commands["aside"] = a =>
            {
                if (!TestMode.Enabled) return "test mode only";
                Targetable t = FindEnemy(a.Length > 1 ? a[1] : "nearest");
                PlayerController pc = PlayerController.Instance;
                if (t == null || pc == null) return "no enemy";
                float m = 3f;
                if (a.Length > 2) float.TryParse(a[2], System.Globalization.NumberStyles.Float, Inv, out m);
                int moved = 0;
                BetterList<Targetable> all = pc.Targetable.EnumerateWithinRadius(30f, pc.Targetable.GetEnemyTeams());
                for (int i = 0; i < all.size; i++)
                {
                    Targetable o = all[i];
                    if (o == null || o == t || o.Controller == null) continue;
                    Vector3 d = o.transform.position - t.transform.position;
                    d.y = 0f;
                    if (d.magnitude >= m) continue;
                    if (d.sqrMagnitude < 1e-4f) d = Vector3.right;
                    Vector3 to = t.transform.position + d.normalized * m;
                    UnityEngine.AI.NavMeshHit hit;
                    if (UnityEngine.AI.NavMesh.SamplePosition(to, out hit, 2f, UnityEngine.AI.NavMesh.AllAreas)) to = hit.position;
                    UnityEngine.AI.NavMeshAgent ag = o.Controller.GetComponent<UnityEngine.AI.NavMeshAgent>();
                    if (ag != null && ag.enabled && ag.isOnNavMesh) ag.Warp(to); else o.Controller.transform.position = to;
                    moved++;
                }
                return "aside: " + moved + " moved " + m.ToString("0.0", Inv) + " m from " + WeaponGrips.ModelKey(t.name);
            };
            // `shields? [reset]`: each shield bearer's time with the shield up (Poise.BlockTiming: UYF's 2-5 s)
            Diag.Commands["shields?"] = a =>
            {
                if (a.Length > 1 && a[1] == "reset") { Poise.ShieldSpans.Clear(); return "reset"; }
                var sb = new System.Text.StringBuilder(Poise.ShieldSpans.Count + " shields:");
                foreach (float x in Poise.ShieldSpans) sb.Append(' ').Append(x.ToString("0.00", Inv));
                return sb.ToString();
            };
            // `vrlook <pill|arc|ghost|gilded|ring|bracket|setting>`, `vrbytype on|off|setting`: [Combat] IndicatorLook / ByType live
            Diag.Commands["vrlook"] = a =>
            {
                if (a.Length > 1)
                {
                    IndicatorLooks.LookKind k;
                    bool ok = false;
                    try { k = (IndicatorLooks.LookKind)Enum.Parse(typeof(IndicatorLooks.LookKind), a[1], true); CombatSettings.OverrideLook(k); ok = true; } catch (Exception) { }
                    if (!ok) CombatSettings.OverrideLook(null);
                }
                return "indicator look " + CombatSettings.Look + (CombatSettings.IndicatorByType ? " (by type)" : "");
            };
            Diag.Commands["vrsuperarmor"] = a =>
            {
                float v;
                if (a.Length > 1) Poise.SuperArmorOverride = float.TryParse(a[1], NumberStyles.Float, Inv, out v) ? v : (float?)null;
                return "super armor " + (Poise.SuperArmorOverride.HasValue ? Poise.SuperArmorOverride.Value.ToString("0", Inv) : "by archetype");
            };
            Diag.Commands["vrknock"] = a =>
            {
                if (a.Length > 1) CombatSettings.OverrideKnockback(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return "knockback " + CombatSettings.Knockback;
            };
            Diag.Commands["vrweaponphysics"] = a =>
            {
                if (a.Length > 1) CombatSettings.OverrideWeaponPhysics(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return "weapon physics " + CombatSettings.WeaponPhysics;
            };
            Diag.Commands["vrmagnet"] = a =>
            {
                if (a.Length > 1) CombatSettings.OverrideMagnetism(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return "magnetism " + CombatSettings.Magnetism;
            };
            Diag.Commands["vrbytype"] = a =>
            {
                if (a.Length > 1) CombatSettings.OverrideByType(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return "indicator by type " + CombatSettings.IndicatorByType;
            };
            // Stage 2 item 9: `enemytable? [enemy]` - the table's size, or that enemy's UYF archetype, guard and lead
            Diag.Commands["enemytable?"] = a => EnemyTable.Query(a.Length > 1 ? a[1] : null);
            Diag.Commands["vrhitflash"] = a =>
            {
                if (a.Length > 1) CombatSettings.OverrideHitFlash(a[1] == "on" ? true : a[1] == "off" ? (bool?)false : null);
                return "hit-taken flash " + CombatSettings.HitTakenFlash;
            };
            Diag.Commands["poiseint"] = a =>
            {
                if (!TestMode.Enabled) return "test mode only";
                return Poise.OpenInterrupt(a.Length > 1 ? a[1] : "nearest", a.Length > 2 ? float.Parse(a[2], Inv) : 1.25f);
            };
            Diag.Commands["vrdamage"] = a =>
            {
                if (a.Length > 1) CombatSettings.OverrideDamage(a[1] == "crits" ? CombatSettings.DamageRule.Crits : a[1] == "everyhit" ? (CombatSettings.DamageRule?)CombatSettings.DamageRule.EveryHit : null);
                return "damage " + CombatSettings.Damage;
            };
            Diag.Commands["vrcombo"] = a =>
            {
                if (a.Length > 1) { int n; CombatSettings.OverrideCombo(int.TryParse(a[1], out n) ? n : (int?)null); }
                return "crit combo " + CombatSettings.CritCombo;
            };
            Diag.Commands["vrguard"] = a =>
            {
                if (a.Length > 1) { float g; CombatSettings.OverrideGuard(float.TryParse(a[1], NumberStyles.Float, Inv, out g) ? g : (float?)null); }
                return "enemy guard x" + CombatSettings.EnemyGuard.ToString("0.00", Inv);
            };
            // `vrdamagein <x>|setting`: enemy damage to the player x this (EnemyTuning; the user's 1/3 of 2026-10-18)
            Diag.Commands["vrdamagein"] = a =>
            {
                if (a.Length > 1) { float g; EnemyTuning.DamageInOverride = float.TryParse(a[1], NumberStyles.Float, Inv, out g) ? Mathf.Clamp(g, 0.01f, 1f) : (float?)null; }
                return "enemy damage in x" + EnemyTuning.DamageInFactor.ToString("0.00", Inv) + " (cut " + EnemyTuning.DamageCut + ", last " + EnemyTuning.LastDamageIn.ToString("0.00", Inv) + " -> " + EnemyTuning.LastDamageOut.ToString("0.00", Inv) + ")";
            };
            Diag.Commands["flash"] = a =>
            {
                float al = a.Length > 1 ? float.Parse(a[1], Inv) : 0.39f, sec = a.Length > 3 ? float.Parse(a[3], Inv) : 0.25f;
                string c = a.Length > 2 ? a[2] : "white";
                ScreenFlash.Show(c == "yellow" ? ScreenFlash.PaleYellow : c == "red" ? ScreenFlash.Red : ScreenFlash.White, al, sec, "seam", c == "red");
                return "flash " + ScreenFlash.Last;
            };
            // `vrweapon <uyf weapon>|game`: grade swings as that UYF weapon (Stage 2 item 3); `weapon?`: the one in use
            Diag.Commands["vrweapon"] = a => WeaponTable.Override(a.Length > 1 ? a[1] : "game");
            Diag.Commands["weapon?"] = a => WeaponTable.Current().ToString();
            Diag.Commands["hbclog"] = a =>
            {
                if (a.Length > 1 && a[1] == "reset") { BladeHits.HbcLog.Clear(); BladeHits.HbcProcess = 0; return "reset"; }
                if (a.Length > 1 && (a[1] == "on" || a[1] == "off")) { BladeHits.HbcOn = a[1] == "on"; return "hbclog " + a[1]; }
                Plugin.L.LogInfo("hit-count guard log (" + BladeHits.HbcLog.Count + ", Process calls " + BladeHits.HbcProcess + "):\n  " + string.Join("\n  ", BladeHits.HbcLog.ToArray()));
                return BladeHits.HbcLog.Count + " entries, Process calls " + BladeHits.HbcProcess;
            };
            // `controlscard?`: the fight controls card's rows for the current settings (GlyphCard)
            Diag.Commands["controlscard?"] = a => GlyphCard.RowText();
            // `heroat <enemy|nearest> <deg> <dist>`: the hero (and the view, by a blink if far) to `dist` m from that enemy,
            // `deg` degrees from where it faces (0 = in front, 90 = its right, 180 = behind it), facing it - the flank test
            Diag.Commands["heroat"] = a =>
            {
                if (!TestMode.Enabled) return "test mode only";
                if (a.Length < 4) return "usage: heroat <enemy|nearest> <deg> <dist>";
                Targetable t = FindEnemy(a[1]);
                PlayerController pc = PlayerController.Instance;
                if (t == null || pc == null) return "no enemy";
                float deg = float.Parse(a[2], Inv), dist = float.Parse(a[3], Inv);
                Vector3 f = t.transform.forward;
                f.y = 0f;
                f.Normalize();
                Vector3 p = t.transform.position + Quaternion.Euler(0f, deg, 0f) * f * (dist + (t.UseRadiusInCombat ? t.Radius : 0f));
                UnityEngine.AI.NavMeshHit nh;
                if (!UnityEngine.AI.NavMesh.SamplePosition(p, out nh, 1.5f, UnityEngine.AI.NavMesh.AllAreas)) return "no ground there";
                UnityEngine.AI.NavMeshAgent ag = pc.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (ag != null && ag.enabled) ag.Warp(nh.position); else pc.transform.position = nh.position;
                Vector3 to = t.transform.position - nh.position;
                to.y = 0f;
                if (to.sqrMagnitude > 1e-4f) pc.transform.rotation = Quaternion.LookRotation(to);
                if (CombatVR.I != null) CombatVR.I.FaceTowards(t.transform.position, false);
                Vector3 from = nh.position - t.transform.position;
                from.y = 0f;
                return "hero at " + Diag.V(nh.position) + ", " + Vector3.Angle(f, from).ToString("0", Inv) + " deg off " + WeaponGrips.ModelKey(t.name) + "'s facing, " + from.magnitude.ToString("0.00", Inv) + " m";
            };
            // `knockdown <enemy|nearest>`: the game's knockdown on that enemy (Controller.Knockdown, as heavy hits do) - for
            // the finisher test
            Diag.Commands["knockdown"] = a =>
            {
                if (!TestMode.Enabled) return "test mode only";
                Targetable t = FindEnemy(a.Length > 1 ? a[1] : "nearest");
                if (t == null || t.Controller == null) return "no enemy";
                // one of the hero's own knockdown blows (an attack whose effects knock down), applied as the game does
                PlayerController pc = PlayerController.Instance;
                PlayerCombatAttack kd = null;
                foreach (PlayerCombatAttack atk in pc.GetComponentsInChildren<PlayerCombatAttack>(true))
                    if (HasKnockdown(((ICombatEffectContainer)atk).CombatEffects, 0)) { kd = atk; break; }
                if (kd != null) CombatEffect.Apply(kd, new CombatTarget(t), pc.transform);
                else t.Controller.Knockdown(null, 1f);
                return WeaponGrips.ModelKey(t.name) + " knocked down by " + (kd != null ? kd.name : "Controller.Knockdown") + ": " + t.Controller.Prone.HasValue;
            };
            // `tough on|off`: the enemies take no damage (their hits, reactions and the damage numbers still happen): tests
            // that hit a lot keep their fixture alive
            Diag.Commands["tough"] = a =>
            {
                if (!TestMode.Enabled) return "test mode only";
                bool on = a.Length < 2 || a[1] != "off";
                PlayerController pc = PlayerController.Instance;
                if (pc == null) return "no hero";
                Targetable me = pc.Targetable;
                BetterList<Targetable> all = me.EnumerateWithinRadius(80f, me.GetEnemyTeams());
                int n = 0;
                for (int i = 0; i < all.size; i++)
                {
                    Destroyable d = all[i] != null ? all[i].GetComponent<Destroyable>() : null;
                    if (d == null || d.IsDead) continue;
                    int id = d.GetInstanceID();
                    if (on && s_tough.Add(id)) d.Invulnerable++;
                    else if (!on && s_tough.Remove(id) && d.Invulnerable > 0) d.Invulnerable--;
                    n++;
                }
                return "tough " + (on ? "on" : "off") + " for " + n + " enemies";
            };
            // `face <enemy|nearest>`: turn the view to face it (a snap turn, as the player's right stick would)
            Diag.Commands["face"] = a =>
            {
                if (!TestMode.Enabled) return "test mode only";
                // `face track <enemy|holder>|off`: the view kept on it every frame (holder: the melee turn's holder, the last one
                // between turns) - an attacker's run begins in view (combogaps: every run judged had begun off view, MR4e/MR4f)
                if (a.Length > 2 && a[1] == "track") { s_track = a[2] == "off" ? null : a[2]; s_trackLast = null; return "face track " + (s_track ?? "off"); }
                Targetable t = a.Length > 1 && a[1] == "striker" ? Guard.Striker() : FindEnemy(a.Length > 1 ? a[1] : "nearest");
                if (t == null || CombatVR.I == null) return "no enemy";
                CombatVR.I.FaceTowards(t.transform.position, false);
                return "facing " + WeaponGrips.ModelKey(t.name) + " at " + Dist(t).ToString("0.00", Inv) + " m";
            };
            // `enemy? [name|nearest]`: distance (head to its body), bearing, health, armour, blocking, prone, action
            Diag.Commands["enemy?"] = a =>
            {
                Targetable t = FindEnemy(a.Length > 1 ? a[1] : "nearest");
                if (t == null) return "none";
                Controller c = t.Controller;
                Destroyable d = t.GetComponent<Destroyable>();
                Actor actor = t.GetComponent<Actor>();
                Defiant.Action run = actor != null ? actor.RunningActionLowerBody : null;
                return WeaponGrips.ModelKey(t.name) + " dist " + Dist(t).ToString("0.00", Inv) + " bearing " + ThreatWarning.BearingOf(t.transform.position).ToString("0", Inv) +
                       " hp " + (d != null ? d.CurrentHealth.ToString("0.0", Inv) : "-") + " armour " + (t.Protection != null ? t.Protection.Health.ToString(Inv) : "-") +
                       " blocking " + (c != null && c.Blocking) + " prone " + (c != null && c.Prone.HasValue && TargetUtils.IsTargetProne(t)) + " action " + (run != null ? run.GetType().Name : "-") +
                       " alive " + CountAlive() + " at " + Diag.V(t.transform.position) + " head " + Diag.V(VRRig.I.HeadWorldPos) + " id " + t.GetInstanceID() + " running " + Running(actor) + " speed " + (c != null && c.Model != null ? c.Animator.GetGlobalSpeed().ToString("0.00", Inv) : "-");
            };
            // `guardat shield|blade [dx dy dz]`: the xrsim `move` that puts the shield's centre (or the blade's middle) on the
            // next strike's marker (+ an offset in metres, world), from where the hand is now (keep its rotation)
            Diag.Commands["guardat"] = a => GuardAt(a);
            // `bladeat <enemy|nearest>`: the blade's middle, tip and direction (world) and the enemy's centre - for aiming swings
            Diag.Commands["blade?"] = a =>
            {
                Hand h = Settings.LeftHanded ? VRInput.L : VRInput.R;
                Vector3 g, tip;
                if (!BladeOf(Settings.LeftHanded, out g, out tip)) return "no blade";
                Vector3 dirT = VRRig.I.FromWorldDir(tip - g).normalized;
                return "grip " + Diag.V(g) + " tip " + Diag.V(tip) + " len " + Vector3.Distance(g, tip).ToString("0.00", Inv) + " dirT " + Diag.V(dirT);
            };
        }

        static bool HasKnockdown(CombatEffect[] fx, int depth)
        {
            if (fx == null || depth > 3) return false;
            foreach (CombatEffect e in fx)
            {
                if (e is CombatEffectKnockdown) return true;
                CombatEffectGroup g = e as CombatEffectGroup;
                if (g != null && HasKnockdown(((ICombatEffectContainer)g).CombatEffects, depth + 1)) return true;
            }
            return false;
        }

        static string Running(Actor a)
        {
            if (a == null || a.RunningActions == null) return "-";
            var sb = new System.Text.StringBuilder();
            foreach (Defiant.Action x in a.RunningActions) if (x != null) sb.Append(sb.Length > 0 ? "," : "").Append(x.GetType().Name);
            return sb.ToString();
        }

        // ---- the snare struggle (§8.5.6: "shake both controllers to struggle") ----
        // The game frees a snared hero by mashing Counter (Y) or waggling the move stick; in fights the stick walks
        // (CombatMove) and in Physical mode the triggers press nothing - so each shake of a controller is a Y press, and
        // the shield hand's trigger presses Y while snared.
        public static bool Snared;
        public static int Struggles;
        static Actor s_heroActor;
        static readonly Vector3[] s_shakeDir = new Vector3[2];

        public static void SnareTick()
        {
            Snared = false;
            if (!Live) return;
            PlayerController pc = PlayerController.Instance;
            if (pc == null) return;
            if (s_heroActor == null || s_heroActor.gameObject != pc.gameObject) s_heroActor = pc.GetComponent<Actor>();
            bool snared = s_heroActor != null && s_heroActor.GetRunningAction<ActionSnared>() != null;
            HideSnareFx(pc, snared || GnomeFight.Clinging);
            if (!snared) return;
            Snared = true;
            if (GnomeFight.Clinging) return;   // F8: a clinging gnome is broken off by the ring's five cuts, not by shaking
            for (int i = 0; i < 2; i++)
            {
                Hand h = i == 0 ? VRInput.L : VRInput.R;
                if (!h.Tracked || h.Vel.magnitude < 1.0f) continue;
                Vector3 d = h.Vel.normalized;
                if (Vector3.Dot(d, s_shakeDir[i]) < -0.3f)
                {
                    VRPad.Press("Y", 3);
                    Struggles++;
                    CombatFeel.Tick(i == 0, 5f);
                }
                s_shakeDir[i] = d;
            }
        }

        /// The game's snare effects on the hero (the treant vines: FX_treant_snare_explode, mesh particles of
        /// Tree_Skeleton_Entangle_Snare_Model) sit on his spine, so in first person they burst around the eyes: GNP's pictures
        /// had broad green bands across the view as a gnome clung and as the snare broke (HideBody leaves particles drawn:
        /// they are not his body). Hidden while he is snared or a gnome clings, and 3 s after; shown again if the effect
        /// leaves him (a pooled effect used elsewhere).
        public static int SnareFxHidden;
        static float s_snareFxUntil = -1f;
        static readonly List<ParticleSystemRenderer> s_psr = new List<ParticleSystemRenderer>();
        static readonly List<Renderer> s_snareFxOff = new List<Renderer>();

        static void HideSnareFx(PlayerController pc, bool snared)
        {
            float now = Time.unscaledTime;
            if (snared) s_snareFxUntil = now + 3f;
            try
            {
                for (int i = s_snareFxOff.Count - 1; i >= 0; i--)
                {
                    Renderer r = s_snareFxOff[i];
                    if (r != null && r.transform.IsChildOf(pc.transform)) continue;
                    if (r != null) r.enabled = true;
                    s_snareFxOff.RemoveAt(i);
                }
                if (now > s_snareFxUntil) return;
                pc.GetComponentsInChildren<ParticleSystemRenderer>(false, s_psr);
                foreach (ParticleSystemRenderer r in s_psr)
                {
                    if (r == null || !r.enabled || !IsSnareFx(r, pc.transform)) continue;
                    r.enabled = false;
                    s_snareFxOff.Add(r);
                    SnareFxHidden++;
                    if (SnareFxHidden <= 10) Plugin.L.LogInfo("snare effect hidden on the hero: " + VRUI.Path(r.transform));
                }
                s_psr.Clear();
            }
            catch (Exception) { }
        }

        static bool IsSnareFx(Renderer r, Transform root)
        {
            Material m = r.sharedMaterial;
            if (m != null && m.name.IndexOf("snare", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            for (Transform p = r.transform; p != null && p != root; p = p.parent)
                if (p.name.IndexOf("snare", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static int CountAlive()
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null) return 0;
            Targetable me = pc.Targetable;
            BetterList<Targetable> all = me.EnumerateWithinRadius(80f, me.GetEnemyTeams());
            int n = 0;
            for (int i = 0; i < all.size; i++)
            {
                Targetable t = all[i];
                Destroyable d = t != null ? t.GetComponent<Destroyable>() : null;
                if (d != null && !d.IsDead && t.Controller != null && !t.Hidden) n++;
            }
            return n;
        }

        static float Dist(Targetable t)
        {
            if (VRRig.I == null) return -1f;
            Vector3 d = t.AimTransform.position - VRRig.I.HeadWorldPos;
            d.y = 0f;
            return d.magnitude;
        }

        /// An enemy by name part, or "nearest" (to the head), alive and targetable.
        static string s_track;          // test seam `face track`
        static Targetable s_trackLast;

        /// Every frame (Guard.LateUpdate), test mode: `face track`'s view kept on its enemy.
        public static void TrackFace()
        {
            if (s_track == null || !TestMode.Enabled || CombatVR.I == null) return;
            Targetable t = null;
            try
            {
                if (s_track == "holder")
                {
                    PlayerController pc = PlayerController.Instance;
                    AITarget at = pc != null && pc.Targetable != null ? pc.Targetable.AITarget : null;
                    AIController m = at != null ? at.MeleeAttacker : null;
                    if (m != null) s_trackLast = m.Targetable;
                    t = s_trackLast;
                    Destroyable d = t != null ? t.GetComponent<Destroyable>() : null;
                    if (d != null && d.IsDead) t = s_trackLast = null;
                }
                else t = FindEnemy(s_track);
            }
            catch (Exception) { t = null; }
            if (t != null) CombatVR.I.FaceTowards(t.transform.position, false);
        }

        public static Targetable FindEnemy(string who)
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null) return null;
            Targetable me = pc.Targetable;
            if (who == "riposte")
            {
                // the countered attacker while its riposte window is open
                Targetable r = Guard.RiposteTarget;
                Destroyable rd = r != null ? r.GetComponent<Destroyable>() : null;
                return r != null && rd != null && !rd.IsDead ? r : null;
            }
            BetterList<Targetable> all = me.EnumerateWithinRadius(60f, me.GetEnemyTeams());
            Targetable best = null;
            float bd = float.MaxValue;
            for (int i = 0; i < all.size; i++)
            {
                Targetable t = all[i];
                if (t == null || t.Controller == null || t.Hidden) continue;
                Destroyable d = t.GetComponent<Destroyable>();
                if (d == null || d.IsDead) continue;
                if (who == "prone") { if (!TargetUtils.IsTargetProne(t)) continue; }   // `prone`: the nearest one lying down
                else if (who == "broken") { if (!Poise.Staggered(t.Controller)) continue; }   // `broken`: the nearest one whose guard is broken
                else if (who.StartsWith("#")) { if ("#" + t.GetInstanceID() != who) continue; }   // `#<id>`: that one (`enemy?` prints its id)
                else if (who == "blocking") { if (!t.Controller.Blocking) continue; }   // `blocking`: the nearest one with its shield up
                else if (who != "nearest" && t.name.IndexOf(who, StringComparison.OrdinalIgnoreCase) < 0) continue;
                float dd = Dist(t);
                if (dd < bd) { bd = dd; best = t; }
            }
            return best;
        }

        /// The main blade's grip and tip (world), from what the hand holds now.
        static bool BladeOf(bool left, out Vector3 grip, out Vector3 tip)
        {
            grip = tip = Vector3.zero;
            PlayerController pc = PlayerController.Instance;
            CombatVR vr = CombatVR.I;
            if (pc == null || vr == null || pc.Inventory == null || pc.Inventory.Weapon == null) return false;
            foreach (Model m in pc.Inventory.Weapon.AllModels)
            {
                if (m == null || m.transform.parent != (left ? vr.HandL : vr.HandR)) continue;
                Bounds lb = CombatVR.LocalBounds(m.transform);
                Vector3 lng, mid, thin;
                CombatVR.Axes(lb, out lng, out mid, out thin);
                float half = 0.5f * Mathf.Abs(Vector3.Dot(lb.size, lng));
                tip = m.transform.TransformPoint(lb.center + lng * half);
                grip = (left ? vr.HandL : vr.HandR).TransformPoint(ControllerGeometry.GripPos(left));
                return true;
            }
            return false;
        }

        static string GuardAt(string[] a)
        {
            if (VRRig.I == null) return "no rig";
            bool shield = a.Length < 2 || a[1] != "blade";
            Vector3 off = a.Length >= 5 ? new Vector3(float.Parse(a[2], Inv), float.Parse(a[3], Inv), float.Parse(a[4], Inv)) : Vector3.zero;
            Vector3 center, axis, toA;
            float eta;
            string colour;
            if (!Guard.NextBar(out center, out axis, out toA, out eta, out colour)) return "no strike";
            PlayerController pc = PlayerController.Instance;
            CombatVR vr = CombatVR.I;
            Vector3 now;
            bool left;
            if (shield)
            {
                if (pc.Inventory.Shield == null || pc.Inventory.Shield.Model == null) return "no shield";
                Transform t = pc.Inventory.Shield.Model.transform;
                left = t.parent == vr.HandL;
                now = t.TransformPoint(CombatVR.LocalBounds(t).center);
            }
            else
            {
                left = Settings.LeftHanded;
                Vector3 g, tip;
                if (!BladeOf(left, out g, out tip)) return "no blade";
                now = Vector3.Lerp(g, tip, 0.55f);
            }
            Vector3 d = VRRig.I.FromWorldDir(center + off - now);
            return string.Format(Inv, "move {0} {1:0.####} {2:0.####} {3:0.####} | eta {4:0.00} {5} axis {6}", left ? "l" : "r", d.x, d.y, -d.z, eta, colour, Diag.V(VRRig.I.FromWorldDir(axis)));
        }
    }
}

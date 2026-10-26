using System;
using BepInEx.Configuration;
using UnityEngine;

namespace HoF2VR
{
    /// Settings of the combat overhaul ([Combat] in BepInEx/config/flat2vr.hof2vr.cfg; documented in
    /// docs/combat-progress.md, copied into PLAY.md by the game owner). Read live, like Settings.
    internal static class CombatSettings
    {
        public enum Meter { On, Engaged, Off }
        /// The user's pick of 2026-10-18 ~22:00 (mockups captures/hud-options-2026-10-18/; "I like 2 and 4 the best, but I also really
        /// like 1 and 5. Can they both be options?"): the player's own fight info on a board standing in the arena (2) or on the
        /// body (1); the enemy's guard and health as a glowing shell on its body (4) or two rings at its feet (5).
        public enum FightHudStyle { Board, Body }
        public enum EnemyStatusStyle { Shell, Rings }
        public enum PaceMode { Relaxed, Normal, Fast }
        /// Until You Fall's difficulty ladder (the coordinator's A, 2026-10-13; replaces Pace).
        public enum DifficultyLevel { Easy, Normal, Hard, Nightmare1, Nightmare2, Nightmare3 }
        public enum GripMode { Game, Bounds }
        public enum WalkMode { Direct, Game }
        public enum DashStyle { Slide, Blink }
        /// The Dash's rules (the coordinator 2026-10-13 23:15, UYF's way by default): UYF = Until You Fall's dash; Short = the
        /// earlier 2 m dash with its safety window.
        public enum DashRules { UYF, Short }
        /// How an enemy's blow is given its lead (docs/uyf-parity-audit.md item 9): Hold = UYF's - the attack waits in its
        /// first moments while its bar fills, then swings at its natural speed; Slow = the earlier way - the whole wind-up
        /// slowed to fill the lead.
        public enum LeadRule { Hold, Slow }
        public enum CombatMode { Classic, Physical }
        public enum CounterRule { Off, Meet, AnyBlock }
        public enum ThrowMode { Point, Hand }
        public enum DamageRule { Crits, EveryHit }

        static ConfigEntry<string> s_meters;
        static ConfigEntry<FightHudStyle> s_fightHud;
        static ConfigEntry<EnemyStatusStyle> s_enemyStatus;
        static ConfigEntry<PaceMode> s_pace;
        static ConfigEntry<DifficultyLevel> s_difficulty;
        static ConfigEntry<bool> s_unblockableBehind;
        static ConfigEntry<float> s_warnSound;
        static ConfigEntry<GripMode> s_grip;
        static ConfigEntry<WalkMode> s_walk;
        static ConfigEntry<float> s_walkSpeed;
        static ConfigEntry<DashStyle> s_dash;
        static ConfigEntry<DashRules> s_dashRules;
        static ConfigEntry<LeadRule> s_lead;
        static ConfigEntry<bool> s_dashFlick, s_trail, s_smartSnap, s_diffFlash, s_circle, s_gloveHud, s_weaponSound, s_waiting, s_comboGaps, s_variedPauses, s_shoves, s_bossSlow;
        static ConfigEntry<int> s_defaultsVersion;
        static ConfigEntry<CombatMode> s_mode;
        static ConfigEntry<CounterRule> s_counter;
        static ConfigEntry<bool> s_counterTurn;
        static ConfigEntry<ThrowMode> s_throw;
        static ConfigEntry<bool> s_leanDodge, s_knockback, s_shake, s_magnet, s_weaponPhysics;
        static ConfigEntry<DamageRule> s_damage;
        static ConfigEntry<int> s_critCombo;
        static ConfigEntry<float> s_enemyGuard;
        static ConfigEntry<bool> s_flash, s_hitFlash;
        static ConfigEntry<IndicatorLooks.LookKind> s_look;
        static ConfigEntry<bool> s_byType;

        public static void Init(ConfigFile cfg)
        {
            // EnemyHealthBars was true/false until 12 October: true still means On, false Off
            s_meters = cfg.Bind("Combat", "EnemyHealthBars", "On", "Enemy health beside each enemy's head (Until You Fall style, never inside the body). On = every enemy; Engaged = only the enemy about to attack you and enemies you hit in the last 5 seconds; Off = none.");
            // (EnemyHealthStyle Pips / Bar went with the pips, 2026-10-18: "get rid of pips and use a different system")
            s_fightHud = cfg.Bind("Combat", "FightHud", FightHudStyle.Board, "Your own health, the fight timer and the special in fights. Board = a dark wood board standing in the arena a few metres to your side (placed when the fight starts, it stays put; it moves only where you cannot see it move); Body = on you: the health gems on the shield glove, the weapon hand glows when the special is ready, the fight timer is a band on the shield wrist. Either way nothing floats in front of you.");
            s_enemyStatus = cfg.Bind("Combat", "EnemyStatus", EnemyStatusStyle.Shell, "How an enemy shows its guard and health. Shell = a blue-white shell glowing round its body: it cracks and dims as the guard wears down, shatters when the guard breaks and grows back as it refills; a warm glow on the body is its health, bright to faint. Rings = two rings on the ground at its feet: the outer blue one is the guard, the inner gold one its health (deep orange when low).");
            s_pace = cfg.Bind("Combat", "Pace", PaceMode.Normal, "(Replaced by Difficulty on 2026-10-13: Relaxed became Easy, Normal Normal, Fast Hard. Read once to carry an old setting over; no effect otherwise.)");
            s_difficulty = cfg.Bind("Combat", "Difficulty", DifficultyLevel.Normal, "Physical mode, Until You Fall's difficulty ladder with its own numbers for each kind of enemy (the enemy table): Easy, Normal (UYF's Normal), Hard, Nightmare1, Nightmare2, Nightmare3. Each step shortens the warning before a blow (a knight: 1.2 / 0.9 / 0.8 / 0.7 / 0.65 / 0.6 s), speeds the attacks (a knight 0.85x ... 1.05x), shortens the pauses between attacks and between attackers, and the Nightmares give enemies bigger guards. Our own extras scale with it too: the warning for attacks from outside your view (announced 1.5 / 1.0 / 0.6 / 0.5 / 0.45 / 0.4 s ahead), the Dash's dodge window, and attacks you cannot block may come from behind only from Hard up. How precisely you must block does not change.");
            // UYF's difficulty change flash: 1.5 s in the step's colour (its DifficultySwap_* screen flashes)
            s_difficulty.SettingChanged += (o, e) => DifficultyFlash();
            // a config from before Difficulty: carry its Pace over once (Relaxed -> Easy, Fast -> Hard), then Pace back to its default
            if (s_pace.Value != PaceMode.Normal && s_difficulty.Value == DifficultyLevel.Normal)
            {
                s_difficulty.Value = s_pace.Value == PaceMode.Relaxed ? DifficultyLevel.Easy : DifficultyLevel.Hard;
                s_pace.Value = PaceMode.Normal;
            }
            s_unblockableBehind = cfg.Bind("Combat", "UnblockableFromBehind", false, "false = on Relaxed and Normal an enemy behind you only starts attacks you can counter; its other attacks wait until you face it. true = any attack may come from behind (always the case on Fast).");
            s_warnSound = cfg.Bind("Combat", "OffViewSound", 1.0f, "Loudness of the warning sound for attacks from outside your view (it comes from the attacker; muffled when it is behind you). 0 = off.");
            s_walk = cfg.Bind("Combat", "Walking", WalkMode.Direct, "How the left stick moves you in fights. Direct = at once and exactly where you push, relative to where you looked when you pushed it: no speeding up, no slowing down, no turning first, nobody shoves you; A dashes (see DashStyle). Game = the game walks your hero (with its run-up and turns) and A is its 5 m dodge roll, as before 12 October.");
            s_walkSpeed = cfg.Bind("Combat", "WalkSpeed", 1.8f, "Walking speed in fights with Walking = Direct, in metres per second (Until You Fall's: 1.8, its other choices 1.0 and 1.3; 2.2 = the earlier default). Sideways is 0.7 and backwards 0.5 of it (UYF's).");
            s_dash = cfg.Bind("Combat", "DashStyle", DashStyle.Slide, "How a dash looks (see Dash): Slide = a quick glide with the comfort tunnel; Blink = the view goes dark for a moment and you are there.");
            s_dashRules = cfg.Bind("Combat", "Dash", DashRules.UYF, "The Dash (A with the left stick's direction, stick centred = back; or a flick of the right stick, see DashFlick). UYF = Until You Fall's: forward 6 m, homing on an enemy ahead (within 3 m and 50 deg, 6 m and 35 deg, 9 m and 25 deg) and hitting it (it wears its guard - more the further you came - pushes it back, knocks the others round it away, and an attack it has only just begun falls apart); back or sideways 3 m; 3 charges, each back after 4 s. Nothing makes you safe during it but where you end up. Short = the earlier dash: 2 m any way, charges back after 2 s, and nothing reaches you from the press until 0.2 s after it.");
            s_lead = cfg.Bind("Combat", "Lead", LeadRule.Hold, "How an enemy's blow gives you time (Physical mode). Hold = as in Until You Fall: the attacker waits a moment at the start of its attack while the marker fills, then swings at its natural speed. Slow = the earlier way: the whole wind-up plays slowed to fill the time (slow-motion swings).");
            s_diffFlash = cfg.Bind("Combat", "DifficultyFlash", true, "As in Until You Fall: changing Difficulty flashes the view for 1.5 s in the step's colour (Easy green, Normal blue, Hard orange, Nightmare 1 yellow, 2 purple, 3 red). false = no flash.");
            s_trail = cfg.Bind("Combat", "WeaponTrail", true, "Physical mode, as in Until You Fall: a short trail behind your blade in fast swings (brighter the faster the tip; nothing in slow moves), in the colour of IndicatorLook. false = none.");
            s_smartSnap = cfg.Bind("Combat", "SmartSnapTurn", true, "Fights, with snap turning on: as in Until You Fall, a snap turn turns you to the nearest enemy on that side (anything from 8 to 190 deg away, up to 20 m) instead of the fixed angle; with nobody on that side, the fixed angle. false = always the fixed angle.");
            s_waiting = cfg.Bind("Combat", "WaitingEnemies", true, "Fights, as in Until You Fall: the enemy next in turn waits close (a soldier or a thug 4 m), the others further back (6 m; a spellsword-like enemy 8 and 10, a boss 10 and 15), backing off when you come nearer; an enemy whose turn has begun but which stops closing in gives the turn up (a thug after 1 s, a soldier 0.5) and waits a moment before its next. false = the game's own spacing (waiting enemies anywhere 4-10 m away).");
            s_comboGaps = cfg.Bind("Combat", "ComboGaps", true, "Fights, with Lead = Hold, as in Until You Fall: an enemy's quick run of blows (a shadow warrior's flurry, the general's double swing) keeps its own rhythm - each later blow's marker appears its full lead before that blow, while the one before is still up (several at once), and the attack does not pause before each blow. false = every blow of a run waits for its own full lead (the attack pauses between blows).");
            s_variedPauses = cfg.Bind("Combat", "VariedPauses", true, "Fights, as in Until You Fall: the pause after each of an enemy's attacks is drawn from its short, medium and long pause (a soldier 0.5 / 0.75 / 1 s on Normal; a thug's are all the same), and an attack you stop part-way (a guard break, an interrupt, a dash into it) is followed by the medium one. false = always the medium pause, from its last blow (before merge-ready #4).");
            s_bossSlow = cfg.Bind("Combat", "BossSlowMotion", true, "Fights, as in Until You Fall: a boss's death slows the world to a tenth for 1.5 s (with its flashes and rumble). false = only the game's own last-kill moment (before merge-ready #4).");
            s_shoves = cfg.Bind("Combat", "Shoves", true, "Fights, as in Until You Fall: stand inside an enemy's reach (a soldier 1.9 m, a thug 1.5) for its time (most 3.5 s, a captain 4.5, the heaviest and bosses 7) and its next attack is a shove: no marker, no damage, you are pushed back (most 3-4 m, spellcaster-like enemies 6, captains 1.5). false = no shoves (before merge-ready #4).");
            s_circle = cfg.Bind("Combat", "CircleTarget", false, "Fights: as Until You Fall's option (off there too), pushing the left stick sideways circles the enemy you face, keeping your distance from it (blended in as it comes within 4 m, fully from 2.5 m). false = the stick walks you sideways in a straight line.");
            s_gloveHud = cfg.Bind("Combat", "GloveHud", true, "Your health gems and the super meter on the back of the glove (Until You Fall's HUD switch). false = neither is shown.");
            s_weaponSound = cfg.Bind("Combat", "WeaponSound", true, "Physical mode, as in Until You Fall: your weapon makes a soft rushing sound that grows louder and brighter the faster you move it (quiet when it is still). false = only the whoosh when a swing turns legal.");
            s_dashFlick = cfg.Bind("Combat", "DashFlick", true, "Fights: flicking the right stick forward or back dashes that way, as in Until You Fall (beside A). Left and right still turn. false = only A dashes.");
            s_mode = cfg.Bind("Combat", "Mode", CombatMode.Physical, "Physical is the default since 12 October. Classic = the game's own fighting moves: a swing presses Attack, a jab with the shield hand counters, the hero's blows are animated. Physical = Until You Fall's combat: hits land where your blade lands, bigger swings hit harder, waggling does nothing; a glowing bar shows where each blow will pass: put your shield or weapon on it (it turns white) to block; an arrow means you can't block it: duck or lean the way it points.");
            // "Counter" replaces "CounterTiming" (12 October): Until You Fall has no parry, so Off is the default now
            s_counter = cfg.Bind("Combat", "Counter", CounterRule.Off, "Physical mode: Off = as in Until You Fall, a block is a block (no counter, no riposte). Meet = the earlier counter: a guard that meets the blow in its last 0.35 s counters a gold attack (the attacker staggers, your next hit within 2 s is a riposte); a guard held there early blocks. AnyBlock = every block of a gold attack counters.");
            s_leanDodge = cfg.Bind("Combat", "LeanDodge", false, "Physical mode: false = as in Until You Fall, attacks you can block must be blocked (or stepped away from), and attacks you can't block show an arrow: duck or lean the way it points before the hit. true = the earlier rule: leaning 0.2 m sideways or down before the hit dodges any attack, and attacks you can't block show a red bar.");
            s_weaponPhysics = cfg.Bind("Combat", "WeaponPhysics", true, "Physical mode, as in Until You Fall: your weapon follows your hand on stiff springs, a little behind it when you swing fast (light blade and mace up to 25 ms, heavy blade and axe 50 ms, a two-hander in one hand 150 ms, in two 50 ms), and a blocked blow knocks it for a moment. Hits, slashes and blocks use the weapon where you see it. false = the weapon sits exactly on the controller.");
            s_knockback = cfg.Bind("Combat", "Knockback", true, "Physical mode, as in Until You Fall: you are pushed back along the attacker's line - a little when you block (6 cm; a heavy blow 1 m), 1.6 m when a blow hits you (a heavy one 3.5 m, a shot 3 m). Walls, the arena's edge and bodies stop it. It moves your view. false = never (the attacker steps back instead).");
            s_shake = cfg.Bind("Combat", "CameraShake", true, "Physical mode, as in Until You Fall: the view shakes briefly - a block 0.05 s, a hit on an enemy's guard 0.25 s, a crit 0.6 s, a hit you take 0.3 s. false = never.");
            s_magnet = cfg.Bind("Combat", "Magnetism", true, "Physical mode, as in Until You Fall: a hit on the enemy you face pulls you toward it (5 m/s) until you stand your weapon's distance from it (a sword 1 m, daggers 0.55 m, a greatsword 1.2 m); not during a dash or just after it. It moves your view. false = never.");
            s_damage = cfg.Bind("Combat", "Damage", DamageRule.Crits, "Physical mode, how enemies lose health. Crits = as in Until You Fall: your hits and your blocks wear down the enemy's guard (the bar above its health); when it breaks, the enemy staggers and glowing slashes appear on it one after another - cut along each one (either way) to hurt it. EveryHit = the earlier rule: every clean hit hurts as well (guard breaks and slashes still happen).");
            s_critCombo = cfg.Bind("Combat", "CritCombo", 3, "Physical mode: how many slashes a guard break gives you, the first one included (1-8). Until You Fall starts at 1 more after the first and raises it with upgrades.");
            s_enemyGuard = cfg.Bind("Combat", "EnemyGuard", 1.0f, "Physical mode: every enemy's guard x this (1 = a basic enemy breaks in 3 sword hits or 2 axe hits, an advanced one in 5, an elite in 7, a boss in 10; 0.5 = half as many).");
            s_flash = cfg.Bind("Combat", "ScreenFlash", true, "Physical mode, as in Until You Fall: a short white flash over the view when your hit lands on an enemy's guard (a bigger hit, a brighter flash) and a pale yellow one on a slash that hurts it. false = never.");
            s_hitFlash = cfg.Bind("Combat", "HitTakenFlash", false, "Physical mode: Until You Fall's red flash over the view when a shot hits you. Off by default (a red tint at the edge of the view was not liked).");
            s_look = cfg.Bind("Combat", "IndicatorLook", IndicatorLooks.LookKind.Gilded, "How the combat indicators look (the block marker, the dodge arrow, the block's flash, the crit slashes and the guard bar above an enemy; the same place and size for all): Pill = the glowing bar filling from the middle; Arc = a crescent bowed toward where the blade comes from; Ghost = a see-through copy of the enemy's weapon where it will pass, solidifying as the blow nears; Gilded = Hand of Fate's card gold with embers; Ring = a ring closing onto the block spot; Bracket = quiet corner brackets and a thin line.");
            s_byType = cfg.Bind("Combat", "IndicatorByType", false, "true = the block markers are coloured by the blow: normal in the look's colour, heavy orange, unblockable red.");
            s_counterTurn = cfg.Bind("Combat", "CounterTurn", false, "Physical mode: true = after you counter an attacker behind you, you are turned to face it (a quick snap). false = you turn yourself (one flick of the right stick).");
            s_throw = cfg.Bind("Combat", "Throwing", ThrowMode.Point, "Physical mode, weapon abilities and artifacts (hold the grip, point, let go): Point = an arc from your hand shows where it lands, for everything; Hand = bombs and other thrown area items are thrown for real (no arc: throw it, it lands where your throw goes, nudged toward a group of enemies; a gentle release puts it back). Everything else still points.");
            // defaults that moved to UYF's (2026-10-13 night, the coordinator's rule: UYF's way by default): a config still holding
            // an old default is carried over once, as Pace was to Difficulty - EnemyHealth 0.25 -> 1.0 (a basic enemy dies in
            // one crit combo), WalkSpeed 2.2 -> 1.8 m/s (UYF's)
            // 2: the player's playtest of 2026-10-17 ("way way too much health... like 15% of what it is"): EnemyHealth 1.0 -> 0.15,
            // once (a value the player chose stays; 0.25 from before 1 goes on to 1.0 and then to 0.15, as both defaults were)
            s_defaultsVersion = cfg.Bind("Combat", "DefaultsVersion", 0, "(Set by the mod: old defaults carried over to the new ones once - 1: EnemyHealth 0.25 -> 1.0, WalkSpeed 2.2 -> 1.8; 2: EnemyHealth 1.0 -> 0.15; 3: the guards by tier and EnemyDamage 0.33, nothing to carry; 4: FightHud Board, EnemyStatus Shell.)");
            if (s_defaultsVersion.Value < 1)
            {
                ConfigEntry<float> eh;
                if (cfg.TryGetEntry("Combat", "EnemyHealth", out eh) && Mathf.Abs(eh.Value - 0.25f) < 0.001f) eh.Value = 1f;
                if (Mathf.Abs(s_walkSpeed.Value - 2.2f) < 0.001f) s_walkSpeed.Value = 1.8f;
                s_defaultsVersion.Value = 1;
            }
            if (s_defaultsVersion.Value < 2)
            {
                ConfigEntry<float> eh;
                if (cfg.TryGetEntry("Combat", "EnemyHealth", out eh) && Mathf.Abs(eh.Value - 1f) < 0.001f) eh.Value = 0.15f;
                s_defaultsVersion.Value = 2;
            }
            // 3: the user's rulings of 2026-10-18 (after playtest 3): the guards by tier (3 sword hits for a basic enemy, a 10 s refill:
            // EnemyTable.GuardOf, the same EnemyGuard 1.0) and EnemyDamage 0.33 (a new entry: it starts at its default) - nothing in
            // an older config to carry over; the version records that the config has met them
            if (s_defaultsVersion.Value < 3) s_defaultsVersion.Value = 3;
            // 4: the new fight HUD and enemy status (the user's pick of 2026-10-18 ~22:00): the board and the shell, once, whatever an
            // earlier test build left in the file (the pips' EnemyHealthStyle is no longer read)
            if (s_defaultsVersion.Value < 4)
            {
                s_fightHud.Value = FightHudStyle.Board;
                s_enemyStatus.Value = EnemyStatusStyle.Shell;
                s_defaultsVersion.Value = 4;
            }
            s_grip = cfg.Bind("Controls", "WeaponGrip", GripMode.Game, "How weapons sit in your hand: Game = as the game's hero holds each weapon (edges and heads face the way the game swings them; per-weapon table); Bounds = the older guess from each model's shape (before 12 October).");
        }

        public static Meter Meters
        {
            get
            {
                string v = (s_meters?.Value ?? "On").Trim();
                if (v.Equals("Off", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return Meter.Off;
                if (v.Equals("Engaged", StringComparison.OrdinalIgnoreCase)) return Meter.Engaged;
                return Meter.On;
            }
        }
        public static FightHudStyle FightHud => s_fightHudOverride ?? (s_fightHud?.Value ?? FightHudStyle.Board);
        public static EnemyStatusStyle EnemyStatus => s_statusOverride ?? (s_enemyStatus?.Value ?? EnemyStatusStyle.Shell);
        static FightHudStyle? s_fightHudOverride;
        static EnemyStatusStyle? s_statusOverride;
        /// Test seams `vrhud board|body|setting`, `vrstatus shell|rings|setting`.
        public static void OverrideFightHud(FightHudStyle? v) { s_fightHudOverride = v; }
        public static void OverrideEnemyStatus(EnemyStatusStyle? v) { s_statusOverride = v; }
        /// The old three-step Pace, read from Difficulty (Easy = Relaxed, Normal = Normal, Hard and up = Fast) for what still asks.
        public static PaceMode Pace => Difficulty == DifficultyLevel.Easy ? PaceMode.Relaxed : Difficulty == DifficultyLevel.Normal ? PaceMode.Normal : PaceMode.Fast;
        static PaceMode? s_paceOverride;
        public static DifficultyLevel Difficulty => s_diffOverride ?? (s_difficulty?.Value ?? DifficultyLevel.Normal);
        static DifficultyLevel? s_diffOverride;
        /// Test seam `vrdiff <step>|setting`.
        public static void OverrideDifficulty(DifficultyLevel? d) { s_diffOverride = d; }
        static readonly Color[] s_stepColours =
        {
            new Color(0.49f, 0.82f, 0.70f), new Color(0.50f, 0.70f, 0.85f), new Color(0.90f, 0.62f, 0.34f),
            new Color(0.90f, 0.86f, 0.27f), new Color(0.63f, 0.19f, 0.89f), new Color(0.82f, 0.23f, 0.23f),
        };
        public static LeadRule LeadStyle => s_leadOverride ?? (s_lead?.Value ?? LeadRule.Hold);
        static LeadRule? s_leadOverride;
        /// Test seam `vrlead hold|slow|setting`.
        public static void OverrideLead(LeadRule? r) { s_leadOverride = r; }
        public static bool DiffFlashOn => s_diffFlashOverride ?? (s_diffFlash == null || s_diffFlash.Value);
        static bool? s_diffFlashOverride;
        /// Test seam `vrdiffflash on|off|setting`.
        public static void OverrideDiffFlash(bool? b) { s_diffFlashOverride = b; }
        public static int DifficultyFlashes;
        /// UYF's DifficultySwap_* screen flash: 1.5 s in the step's colour at full strength, fading (`[Combat] DifficultyFlash`).
        public static void DifficultyFlash()
        {
            if (!DiffFlashOn) return;
            try { HoF2VR.ScreenFlash.Show(s_stepColours[Mathf.Clamp(Step, 0, 5)], 1f, 1.5f, "difficulty " + Difficulty); DifficultyFlashes++; } catch (Exception) { }
        }

        /// The step 0 (Easy) .. 5 (Nightmare 3).
        public static int Step => (int)Difficulty;
        static float ByStep(float easy, float normal, float hard, float n1, float n2, float n3)
        {
            switch (Difficulty)
            {
                case DifficultyLevel.Easy: return easy;
                case DifficultyLevel.Hard: return hard;
                case DifficultyLevel.Nightmare1: return n1;
                case DifficultyLevel.Nightmare2: return n2;
                case DifficultyLevel.Nightmare3: return n3;
                default: return normal;
            }
        }
        /// Test seam `vrpace relaxed|normal|fast|setting`: switches the pace live without writing the settings file.
        public static void OverridePace(PaceMode? p)
        {
            s_paceOverride = p;
            s_diffOverride = p == null ? (DifficultyLevel?)null : p == PaceMode.Relaxed ? DifficultyLevel.Easy : p == PaceMode.Fast ? DifficultyLevel.Hard : DifficultyLevel.Normal;
        }
        /// Attacks that can't be blocked may come from behind: the setting, or from Hard up.
        public static bool UnblockableFromBehind => (s_unblockableBehind != null && s_unblockableBehind.Value) || Difficulty >= DifficultyLevel.Hard;
        public static float OffViewSound => Mathf.Clamp01(s_warnSound?.Value ?? 1f);
        public static GripMode Grip => s_grip?.Value ?? GripMode.Game;
        public static WalkMode Walking => s_walk?.Value ?? WalkMode.Direct;
        public static float WalkSpeed => Mathf.Clamp(s_walkSpeed?.Value ?? 1.8f, 1f, 3.5f);
        public static DashStyle Dash => s_dash?.Value ?? DashStyle.Slide;
        public static DashRules DashKind => s_dashRulesOverride ?? (s_dashRules?.Value ?? DashRules.UYF);
        static DashRules? s_dashRulesOverride;
        /// Test seam `vrdash uyf|short|setting`.
        public static void OverrideDashKind(DashRules? k) { s_dashRulesOverride = k; }
        public static bool UyfDash => DashKind == DashRules.UYF;
        public static bool DashFlick => s_dashFlickOverride ?? (s_dashFlick == null || s_dashFlick.Value);
        public static bool WeaponTrail => s_trailOverride ?? (s_trail == null || s_trail.Value);
        static bool? s_trailOverride;
        /// Test seam `vrtrail on|off|setting`.
        public static void OverrideTrail(bool? b) { s_trailOverride = b; }
        public static bool WaitingEnemies => s_waitingOverride ?? (s_waiting == null || s_waiting.Value);
        static bool? s_waitingOverride;
        /// Test seam `vrwaiting on|off|setting`.
        public static void OverrideWaiting(bool? b) { s_waitingOverride = b; }
        public static bool BossSlowMotion => s_bossSlowOverride ?? (s_bossSlow == null || s_bossSlow.Value);
        static bool? s_bossSlowOverride;
        /// Test seam `vrbossslow on|off|setting`.
        public static void OverrideBossSlow(bool? b) { s_bossSlowOverride = b; }
        // test mode: off unless a test asks (`vrshoves on`) - the suites hold the hero still in front of enemies on purpose
        public static bool Shoves => s_shovesOverride ?? (!TestMode.Enabled && (s_shoves == null || s_shoves.Value));
        static bool? s_shovesOverride;
        /// Test seam `vrshoves on|off|setting`.
        public static void OverrideShoves(bool? b) { s_shovesOverride = b; }
        public static bool VariedPauses => s_variedPausesOverride ?? (s_variedPauses == null || s_variedPauses.Value);
        static bool? s_variedPausesOverride;
        /// Test seam `vrpauses on|off|setting`.
        public static void OverrideVariedPauses(bool? b) { s_variedPausesOverride = b; }
        public static bool ComboGaps => s_comboGapsOverride ?? (s_comboGaps == null || s_comboGaps.Value);
        static bool? s_comboGapsOverride;
        /// Test seam `vrcombogaps on|off|setting`.
        public static void OverrideComboGaps(bool? b) { s_comboGapsOverride = b; }
        public static bool CircleTarget => s_circleOverride ?? (s_circle != null && s_circle.Value);
        public static bool GloveHud => s_gloveHudOverride ?? (s_gloveHud == null || s_gloveHud.Value);
        public static bool WeaponSound => s_weaponSoundOverride ?? (s_weaponSound == null || s_weaponSound.Value);
        static bool? s_circleOverride, s_gloveHudOverride, s_weaponSoundOverride;
        /// Test seams `vrcircle`, `vrglovehud`, `vrweaponsound` (on|off|setting).
        public static void OverrideCircle(bool? b) { s_circleOverride = b; }
        public static void OverrideGloveHud(bool? b) { s_gloveHudOverride = b; }
        public static void OverrideWeaponSound(bool? b) { s_weaponSoundOverride = b; }
        public static bool SmartSnap => s_smartSnapOverride ?? (s_smartSnap == null || s_smartSnap.Value);
        static bool? s_smartSnapOverride;
        /// Test seam `vrsmartsnap on|off|setting`.
        public static void OverrideSmartSnap(bool? b) { s_smartSnapOverride = b; }
        static bool? s_dashFlickOverride;
        /// Test seam `vrdashflick on|off|setting`.
        public static void OverrideDashFlick(bool? b) { s_dashFlickOverride = b; }
        /// The combat mode: Physical = the lean core loop (docs/combat-uyf-design.md §8.5; the default since the 12 October
        /// evening release); Classic = the game's own moves.
        public static CombatMode Mode { get { return s_modeOverride ?? (s_mode?.Value ?? CombatMode.Physical); } }
        static CombatMode? s_modeOverride;
        /// Test seam `vrcombat physical|classic|setting`: switches live without writing the player's settings file.
        public static void OverrideMode(CombatMode? m) { s_modeOverride = m; }
        public static bool Physical => Mode == CombatMode.Physical;
        public static CounterRule Counter => s_counterOverride ?? (s_counter?.Value ?? CounterRule.Off);
        static CounterRule? s_counterOverride;
        /// Test seam `vrcounter off|meet|anyblock|setting`.
        public static void OverrideCounter(CounterRule? c) { s_counterOverride = c; }
        public static bool LeanDodge => s_leanOverride ?? (s_leanDodge != null && s_leanDodge.Value);
        static bool? s_leanOverride;
        /// Test seam `vrlean on|off|setting`.
        public static void OverrideLean(bool? b) { s_leanOverride = b; }
        public static bool Knockback => s_knockOverride ?? (s_knockback == null || s_knockback.Value);
        public static bool CameraShake => s_shake == null || s_shake.Value;
        public static bool Magnetism => s_magnetOverride ?? (s_magnet == null || s_magnet.Value);
        static bool? s_knockOverride, s_magnetOverride;
        /// Test seams `vrknock on|off|setting`, `vrmagnet on|off|setting` (the older suites let many blows through in god mode).
        public static void OverrideKnockback(bool? b) { s_knockOverride = b; }
        public static void OverrideMagnetism(bool? b) { s_magnetOverride = b; }
        public static bool WeaponPhysics => s_physicsOverride ?? (s_weaponPhysics == null || s_weaponPhysics.Value);
        static bool? s_physicsOverride;
        /// Test seam `vrweaponphysics on|off|setting`.
        public static void OverrideWeaponPhysics(bool? b) { s_physicsOverride = b; }
        public static bool CounterTurn => s_counterTurn != null && s_counterTurn.Value;
        public static DamageRule Damage => s_damageOverride ?? (s_damage?.Value ?? DamageRule.Crits);
        static DamageRule? s_damageOverride;
        /// Test seam `vrdamage crits|everyhit|setting`.
        public static void OverrideDamage(DamageRule? d) { s_damageOverride = d; }
        public static bool CritsOnly => Damage == DamageRule.Crits;
        public static int CritCombo => s_comboOverride ?? Mathf.Clamp(s_critCombo?.Value ?? 3, 1, 8);
        static int? s_comboOverride;
        /// Test seam `vrcombo N|setting`.
        public static void OverrideCombo(int? n) { s_comboOverride = n.HasValue ? Mathf.Clamp(n.Value, 1, 8) : (int?)null; }
        public static float EnemyGuard => s_guardOverride ?? Mathf.Clamp(s_enemyGuard?.Value ?? 1f, 0.1f, 5f);
        static float? s_guardOverride;
        /// Test seam `vrguard X|setting`.
        public static void OverrideGuard(float? g) { s_guardOverride = g; }
        public static bool ScreenFlash => s_flash == null || s_flash.Value;
        public static IndicatorLooks.LookKind Look => s_lookOverride ?? (s_look?.Value ?? IndicatorLooks.LookKind.Gilded);
        static IndicatorLooks.LookKind? s_lookOverride;
        /// Test seam `vrlook pill|arc|ghost|gilded|ring|bracket|setting`.
        public static void OverrideLook(IndicatorLooks.LookKind? k) { s_lookOverride = k; }
        public static bool IndicatorByType => s_byTypeOverride ?? (s_byType != null && s_byType.Value);
        static bool? s_byTypeOverride;
        /// Test seam `vrbytype on|off|setting`.
        public static void OverrideByType(bool? b) { s_byTypeOverride = b; }
        public static bool HitTakenFlash => s_hitFlashOverride ?? (s_hitFlash != null && s_hitFlash.Value);
        static bool? s_hitFlashOverride;
        /// Test seam `vrhitflash on|off|setting`.
        public static void OverrideHitFlash(bool? b) { s_hitFlashOverride = b; }
        public static ThrowMode Throwing => s_throwOverride ?? (s_throw?.Value ?? ThrowMode.Point);
        static ThrowMode? s_throwOverride;
        /// Test seam `vrthrow point|hand|setting`.
        public static void OverrideThrowing(ThrowMode? m) { s_throwOverride = m; }

        // ---- the difficulty's own extras (Physical mode; UYF's numbers per enemy are in EnemyTable) ----
        static float ByPace(float relaxed, float normal, float fast) => Pace == PaceMode.Relaxed ? relaxed : Pace == PaceMode.Fast ? fast : normal;
        /// (Before Difficulty) the lead every strike got from its telegraph to its impact, by class: kept for the `vrpace` seam.
        public static float Lead(AIClassType c, bool boss)
        {
            float b = boss ? 1.2f : c == AIClassType.Elite ? 0.9f : c == AIClassType.Advanced ? 1.1f : 1.2f;
            return b * ByPace(1.25f, 1f, 0.8f);
        }
        /// After an impact the next attack anywhere waits this long (s): the lean core's pause, kept on Easy as its gentler pace;
        /// from Normal up UYF's (none beyond each enemy's own swap grace and cooldowns; the coordinator 2026-10-13 23:15).
        public static float SwapGrace => ByStep(0.9f, 0f, 0f, 0f, 0f, 0f);
        /// The lean core's calm after you defend (no rapid follow-ups for 2.5 s after a block, counter or dodge): Easy only.
        public static bool CalmAfterDefending => Difficulty == DifficultyLevel.Easy;
        /// (Before the enemy table had UYF's per-enemy grace) UYF's template TicketSwapGraceTime by the number of enemies; the
        /// enemy's own (EnemyTable.GraceFor) is used now.
        public static float SwapGraceFor(int enemies) => enemies <= 2 ? 0.5f : enemies == 3 ? 0.4f : enemies == 4 ? 0.3f : 0.25f;
        /// How near the bar a guard must be (in the bar's plane, m) and how well a blade must line up with it (deg): Until You
        /// Fall's (0.165 m, 30 deg) at every difficulty (the coordinator: the block tolerance does not change).
        public static float GuardTolerance => 0.165f;
        public static float GuardAngle => 30f;
        /// How far the head must move sideways or down after the telegraph to slip a strike (LeanDodge, m).
        public static float DodgeDistance => ByStep(0.15f, 0.20f, 0.25f, 0.25f, 0.25f, 0.25f);
        /// The Dash's dodge window after the dash ends (s).
        public static float DashWindow => ByStep(0.3f, 0.2f, 0.1f, 0.1f, 0.1f, 0.1f);

        /// An enemy picked to attack from outside the view waits this long before it may start (§8.5.5).
        /// Playtest 3 (2026-10-18, mark 21: "even more of a warning before they're going to hit me from behind"): both earlier
        /// (Normal: the announcement 1.0 -> 1.5 s before it may start, the off-view lead +0.8 -> +1.3 s: 2.2 s for a soldier).
        public static float OffViewAnnounce => ByStep(2.0f, 1.5f, 0.9f, 0.75f, 0.65f, 0.6f);
        public static float OffViewBonus => ByStep(1.7f, 1.3f, 0.8f, 0.6f, 0.5f, 0.4f);
    }
}

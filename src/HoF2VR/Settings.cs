using System;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace HoF2VR
{
    /// User-tunable settings (BepInEx/config/flat2vr.hof2vr.cfg). Values are read live: every use reads
    /// the entry, and the file is re-read a moment after it is saved, so edits apply while playing.
    internal static class Settings
    {
        static ConfigFile s_cfg;
        /// This test run saves its settings file (HOF2VR_CFG_SAVE=1, a scratch game copy only).
        static bool s_cfgSaveRun;
        static ConfigEntry<bool> s_logInput;
        static ConfigEntry<bool> s_snapTurn, s_snapBlink;
        static ConfigEntry<float> s_snapAngle;
        static ConfigEntry<float> s_smoothTurnSpeed;
        static ConfigEntry<float> s_renderScale;
        static ConfigEntry<bool> s_leftHanded;
        static ConfigEntry<float> s_swing, s_shield, s_stab, s_vignette, s_walkVignette, s_turnVignette;
        static ConfigEntry<bool> s_seated;
        static ConfigEntry<float> s_eyeHeight, s_tableScale, s_seatedTable, s_seatedLift;
        /// Seated at the Dealer's table and the side table: Table = the table's top SeatedTableHeight above the real floor (a
        /// real table's height, within a seated reach); Lifted = Until You Fall's seated view there too (SeatedLift, as everywhere).
        public enum TableView { Table, Lifted }
        static ConfigEntry<TableView> s_seatedTableView;
        static ConfigEntry<float> s_perfLog;
        public enum AoMode { Half, Full, Off }
        static ConfigEntry<AoMode> s_vrAo;
        public enum DiceMode { Hybrid, Classic }
        static ConfigEntry<DiceMode> s_diceThrow;
        static ConfigEntry<bool> s_diceSummon, s_stopSlap, s_stopGrip, s_nudgeFlick, s_touchCards;
        static ConfigEntry<float> s_interactHaptics;
        static ConfigEntry<bool> s_waitingSpace, s_playerFightExit;
        public enum SpaceLook { Campfire, Simple }
        static ConfigEntry<SpaceLook> s_spaceLook;
        static ConfigEntry<float> s_fightExitAuto;
        static ConfigEntry<bool> s_textAboveDealer, s_choiceBoard;
        static ConfigEntry<float> s_standDistance;
        // 2026-10-11 afternoon feedback round
        static ConfigEntry<bool> s_pressToContinue, s_waitingBoard, s_flatGround;
        public enum BoardStyle { Card, Gilded, Banner }
        static ConfigEntry<BoardStyle> s_boardStyle;
        static ConfigEntry<bool> s_fastLoading, s_quietNear, s_skipIntros;
        public enum FightCard { WaitingSpace, Fight, Off }
        static ConfigEntry<FightCard> s_controlsCard;
        static ConfigEntry<float> s_enemyHealth;
        static ConfigEntry<float> s_enemyDamage;
        static ConfigEntry<bool> s_offViewWarning;   // EnemyHealthBars and the old OffViewSlowdown: CombatSettings
        static ConfigEntry<float> s_standSideways, s_choiceDistance;
        static ConfigEntry<bool> s_lasers, s_questPrompts, s_vibration;
        // 2026-10-11 evening playtest (round 4)
        static ConfigEntry<bool> s_touchToFight;
        static ConfigEntry<bool> s_gloveStats;
        static ConfigEntry<bool> s_holdArena, s_calmAfterWin, s_statsOnTable, s_controlsOnTable, s_controlButtons, s_mapFromTable, s_quickHopFade, s_bugMark, s_jumpMenu, s_scenarios;
        internal static ConfigEntry<float> s_fitPitch, s_fitUp, s_fitAlong, s_fitYaw, s_fitRoll, s_fitSide, s_playerSize;
        // hand feel, Phase 1 (docs/hand-feel-design.md §3.13)
        public enum FeelPreset { Off, Subtle, Normal, Strong }
        public enum GhostMode { Off, Faint, Clear }
        static ConfigEntry<FeelPreset> s_feelPreset;
        static ConfigEntry<GhostMode> s_ghostHand;
        static ConfigEntry<bool> s_stopAtSurfaces, s_titleBoard, s_latePose, s_menuBoard, s_grabThrow;
        static ConfigEntry<float> s_throwBoost;
        internal static ConfigEntry<bool> s_handCheckOffered;
        internal static ConfigEntry<float> s_hapticFloor;
        static ConfigEntry<float> s_hapticStrength, s_handSounds, s_touchAssist, s_smoothing;
        // 3D sound with a real HRTF (VR/Hrtf.cs, docs/hrtf-plan.md)
        public enum HrtfInterp { Nearest, Bilinear }
        public enum HrtfNorm { None, RMS }
        static ConfigEntry<HrtfNorm> s_hrtfNorm;
        static ConfigEntry<bool> s_hrtf, s_hrtfAir, s_headShadowWithHrtf;
        static ConfigEntry<HrtfInterp> s_hrtfInterp;
        static ConfigEntry<string> s_hrtfGroups, s_hrtfSofa;
        static ConfigEntry<float> s_hrtfMinBlend, s_hrtfMinDistance, s_hrtfGainDb;

        public static void Init(ConfigFile cfg)
        {
            s_cfg = cfg;
            // A test run never writes the settings file (env 1's tests share it with the player's game). BepInEx saves the
            // whole file on every new key's Bind and every set, so a value set for one run only (HOF2VR_SEATED below, `cfg`,
            // a Seated pressed in a test) was written by the next Bind: 2026-10-14 the seated tests left Seated = true in the
            // player's file, and every later standing test ran seated. The player's own game saves as always.
            // (HOF2VR_CFG_SAVE=1: a test run that saves its settings file as the player's game does - the settings carry-over
            // check, in a scratch copy of the game only; refused in the Steam install, whose file is the player's)
            bool cfgSave = TestMode.Enabled && Environment.GetEnvironmentVariable("HOF2VR_CFG_SAVE") == "1" &&
                           cfg.ConfigFilePath.Replace('\\', '/').IndexOf("/steamapps/common/", StringComparison.OrdinalIgnoreCase) < 0;
            if (TestMode.Enabled && !cfgSave) cfg.SaveOnConfigSet = false;
            s_cfgSaveRun = cfgSave;
            s_logInput =cfg.Bind("Debug", "LogInput", false, "Log raw controller button/axis changes (for troubleshooting).");
            s_snapTurn = cfg.Bind("Comfort", "SnapTurn", false, "Turn in steps with the right stick instead of smoothly.");
            s_snapAngle = cfg.Bind("Comfort", "SnapTurnAngle", 30f, "Degrees per snap turn.");
            s_snapBlink = cfg.Bind("Comfort", "SnapTurnBlink", false, "With SnapTurn on: a very short blink (the view goes dark for a moment) on each snap turn, so the turn is never seen as a jump.");
            s_smoothTurnSpeed = cfg.Bind("Comfort", "SmoothTurnSpeed", 120f, "Degrees per second for smooth turning.");
            s_renderScale = cfg.Bind("Display", "RenderScale", 1.0f, "Eye texture resolution scale (1 = runtime's recommended size).");
            s_vignette = cfg.Bind("Comfort", "Vignette", 0.8f, "Darken the edge of the view while the game moves you fast (dodges, lunges). 0 = off, 1 = strongest.");
            s_walkVignette = cfg.Bind("Comfort", "WalkVignette", 0.4f, "Darken the edge of the view while you walk with the left stick in fights. 0 = off, 1 = strongest.");
            s_turnVignette = cfg.Bind("Comfort", "TurnVignette", 0.5f, "Darken the edge of the view while you turn smoothly with the right stick. 0 = off, 1 = strongest.");
            s_leftHanded = cfg.Bind("Controls", "LeftHanded", false, "Weapon in the left hand, shield in the right.");
            s_seated = cfg.Bind("Comfort", "Seated", false, "Playing sitting down (choose Standing or Seated on the hand check's first page, or on the VR settings page's Comfort tab: it applies at once and saves itself). Seated: at the Dealer's table (and the side table) you sit at it like at a real table - its top SeatedTableHeight above your floor, below your real seated eyes; in fights and everywhere else your view is lifted by SeatedLift to about standing height. false = standing: you see everything from your real height.");
            s_eyeHeight = cfg.Bind("Comfort", "EyeHeight", 1.65f, "Eye height in metres used when the headset reports no floor (a tracking origin at eye level).");
            s_seatedTable = cfg.Bind("Comfort", "SeatedTableHeight", 0.75f, "Seated: how high above your real floor the Dealer's table top (and the map on the side table) is, in metres, for seated eyes 1.20 m above the floor - a real table is about 0.75, a little below your elbows. It follows your own seated eye height (taken when you choose Seated): as much lower or higher as your eyes are, so its top stays 0.45 m below them. Nudge it in VR: VR Settings, Comfort, Seated table - / +.");
            s_seatedLift = cfg.Bind("Comfort", "SeatedLift", 0.4572f, "Seated: in fights and away from the tables (the title, the campfire, the waiting space; at the tables too with SeatedTableView = Lifted) your view is lifted this many metres above your real seated eyes - about standing height (Until You Fall's seated boost, 18 inches). The same lift every time, whatever your head does at the moment you arrive somewhere.");
            s_seatedTableView = cfg.Bind("Comfort", "SeatedTableView", TableView.Table, "Seated, at the Dealer's table and the side table: Table = you sit at it like at a real table, its top SeatedTableHeight above your floor (within a seated arm's reach); Lifted = Until You Fall's seated view there too - lifted SeatedLift like everywhere else, so you see the table as a standing player does, but its top is then about 0.73 m below seated eyes of 1.20 m, at your knees (the near cards and the dice out of a seated reach). Switch it in VR: VR Settings, Comfort, At the table.");
            s_tableScale = cfg.Bind("Display", "TableRoomScale", 1.2f, "The Dealer's caravan is built larger than life; this many game units per real metre make it life-size (1 = as built).");
            s_swing = cfg.Bind("Controls", "SwingSpeed", 3.0f, "Blade-tip speed (m/s) a sword swing needs to count as an attack. Lower = easier.");
            s_shield = cfg.Bind("Controls", "ShieldSpeed", 1.6f, "Shield-hand speed (m/s) for counter (while an attack is coming) and shield bash (forward thrust).");
            s_stab = cfg.Bind("Controls", "StabSpeed", 1.8f, "Downward sword speed (m/s) for a finisher stab on a downed enemy.");
            s_vrAo = cfg.Bind("Display", "AmbientOcclusion", AoMode.Half, "Ambient occlusion (soft contact shadows) in VR. Half = computed at half resolution: looks the same in the headset and was the biggest single GPU saving (about a third of the GPU work), so frames stay on time. Full = the game's own full-resolution setting. Off = none (flatter look). The flat game's own setting is never changed.");
            s_perfLog = cfg.Bind("Debug", "PerfLogSeconds", 10f, "Every this many seconds in VR, log one line of performance numbers (frame times, missed frames, GPU clock) to BepInEx/LogOutput.log. 0 = off.");
            s_diceThrow = cfg.Bind("Table", "DiceThrow", DiceMode.Hybrid, "Hybrid = grab the dice with a grip (reach for them, or point at them and squeeze to pull them into your hand), shake, and throw them by letting go: your hand's speed sets the game's throw strength and its direction bends the throw toward the tray. The game still rolls them with its own random spin and reads the faces that land up. Classic = only the trigger: hold to draw the dice back, let go to throw (this also works in Hybrid mode).");
            s_diceSummon = cfg.Bind("Table", "DiceSummon", true, "With DiceThrow = Hybrid: squeezing the grip while pointing at the dice pulls them into your hand, so you don't have to lean over the table.");
            s_stopSlap = cfg.Bind("Table", "StopBySlap", true, "Slap the table (a quick downward hit onto the table top) to stop the swinging pendulum or the spinning wheel.");
            s_stopGrip = cfg.Bind("Table", "StopByGrip", true, "Squeeze either grip to stop the swinging pendulum or the spinning wheel.");
            s_nudgeFlick = cfg.Bind("Table", "NudgeByFlick", true, "When the game offers to nudge the wheel: flick a hand sideways to nudge it that way, slap the table to keep the card.");
            s_touchCards = cfg.Bind("Table", "TouchCards", true, "Touch cards, map tiles and dice with your fingertip: bring the finger onto a card from above (the game highlights it, you feel a tick), then push it in about a centimetre to pick it (a click). Far things (the map) need a lean - or the laser, which keeps working.");
            s_waitingSpace = cfg.Bind("Comfort", "WaitingSpace", true, "While the game loads (starting the game, title to table, table to a fight and back) you stand in a calm misty night clearing with a warm glow ahead instead of in black. The game's menus and messages stay readable in front of you. false = plain black, as before.");
            s_spaceLook = cfg.Bind("Comfort", "WaitingSpaceLook", SpaceLook.Campfire, "With WaitingSpace on: Campfire = the title's campfire clearing (built from the game's own art), with small fire-lights on the ground that fill up as the game loads. Simple = the plain misty clearing with a warm glow from before.");
            s_playerFightExit = cfg.Bind("Comfort", "PlayerFightExit", true, "After you win a fight, the Dealer's purple swirl (the portal that pulls you into the cards) opens in the arena at chest height, a couple of steps away: walk into it, touch it, or point at it and pull the trigger to go back to the Dealer's table (holding A for a second after 20 s, or Forfeit in the pause menu, also leaves). Nothing takes you back by itself. false = the game takes you back right after the last enemy falls, as before.");
            s_fightExitAuto = cfg.Bind("Comfort", "PlayerFightExitAutoSeconds", 0f, "With PlayerFightExit on: go back by itself after this many seconds anyway. 0 = never.");
            s_textAboveDealer = cfg.Bind("Table", "TextAboveDealer", true, "The story text of an encounter floats above the Dealer, a little to the right, so you can read it while you look at the table. false = with the rest of the game's screen in front of you, as before.");
            s_choiceBoard = cfg.Bind("Table", "ChoiceBoard", true, "An encounter's choices appear as buttons on a small board within reach in front of you: touch one with a fingertip (push it in about a centimetre) or point at it and pull the trigger. The stick and A still work. false = the choices stay with the story text.");
            s_standDistance = cfg.Bind("Table", "StandDistance", 0.18f, "How far your eyes are from the near edge of the card table, in metres (the room is scaled to life size). Smaller = closer to the cards; 0.4 is roughly where tall players stood before 11 October.");
            s_standSideways = cfg.Bind("Table", "StandSideways", 0f, "Where you stand along the card table's near edge, in metres: 0 = centred across from the Dealer; positive = to the right, negative = to the left. (Until 11 October you stood about 0.2 m to the right.) Furniture you would stand in is hidden while you stand there.");
            s_choiceDistance = cfg.Bind("Table", "ChoiceBoardDistance", 0.52f, "How far in front of your eyes an encounter's choice buttons float, in metres (0.42 until 11 October). Keep it within reach of your fingertip.");
            s_lasers = cfg.Bind("Controls", "Lasers", false, "Laser pointers from the controllers: point at a card or button and pull the trigger to pick it. false = no lasers: touch things with a fingertip, or use the stick and A.");
            s_vibration = cfg.Bind("Controls", "Vibration", true, "Controller vibration (the game's rumble, attack warnings, hits, table touches). false = none. (Each vibration request goes through the streaming; if the game ever stutters right when a controller buzzes, try false.)");
            s_questPrompts = cfg.Bind("Controls", "QuestButtonPrompts", true, "The game's button prompts show the Quest controllers' buttons (A/B/X/Y, grips, triggers, the stick click, the menu button), and in fights what each action really is in VR. false = the game's Xbox prompts.");
            s_pressToContinue = cfg.Bind("Comfort", "PressToContinue", true, "In the waiting space: once the next place is ready, it waits for you - press A (or X) to go there. A fight does not start before you are there. false = you are taken there as soon as it is ready.");
            s_waitingBoard = cfg.Bind("Comfort", "WaitingBoard", true, "In the waiting space a board shows where you are going (for a fight: the encounter, the enemies and the VR fighting controls), a progress bar, and when it is ready the prompt to go. false = only the small fire-lights on the ground.");
            s_boardStyle = cfg.Bind("Comfort", "WaitingBoardStyle", BoardStyle.Card, "How the waiting-space board looks (all in the game's own fonts and art): Card = gilded lettering in the game's gold heading frame, and before a fight the encounter's own card floats beside it; Gilded = the same without the card; Banner = the destination on the game's red map ribbon.");
            s_fastLoading = cfg.Bind("Comfort", "FastLoading", true, "Shorter waits in VR: the card deal, the portal tunnel's fades and the showcase card's flight in the tunnel run faster or are skipped - they play behind the waiting space, where you never see them (about 4 s into a fight instead of 9-14, 3 s back instead of 7). The start's logo screens and title intro are a separate development shortcut ([Dev] SkipIntros). false = the game's own timings.");
            s_quietNear = cfg.Bind("Comfort", "QuietNearGround", true, "In the waiting space the ground right around you is shaded and has no plants: when a frame comes late, the headset shows the last one again, and near detail is what visibly slides and jumps. false = the clearing as the game's title has it.");
            s_skipIntros = cfg.Bind("Dev", "SkipIntros", false, "Development shortcut (for testing): skip the logo screens at the start and speed up the title's camera move and logo fade, so the game is ready about 13 s sooner. false = the game's own start, as a player sees it.");
            s_flatGround = cfg.Bind("Comfort", "FlatGround", true, "Stand on level ground: the waiting space's ground is levelled where you stand, and at the title you stand on the flattest spot near the game's camera. false = as the game's ground is (on a slope).");
            s_controlsCard = cfg.Bind("Combat", "ControlsCard", FightCard.Off, "No longer used: the VR fighting controls are not shown as text any more (the user, 2026-10-17: \"I, and nobody else, has the patience to read that. It should just be intuitive.\"). Until then: WaitingSpace = a card beside the board before a fight; Fight = a card at the start of the first fights.");
            s_enemyHealth = cfg.Bind("Combat", "EnemyHealth", 0.15f, "Enemies start with this share of their normal health (0.15 = the default since 17 October, the player's choice: \"bring it down to like 15%\"; 1 = as in the game; 0.05-1).");
            s_enemyDamage = cfg.Bind("Combat", "EnemyDamage", 0.33f, "How hard enemies hit you: every enemy blow, shot and fire does this share of the game's damage (0.33 = the default since 18 October, the player's choice: \"about 1/3 as much damage\"; 1 = as in the game; 0.1-1). Armour, blocks, cards and curses apply on top.");
            s_offViewWarning = cfg.Bind("Combat", "OffViewWarning", true, "Attacks from outside your view: the enemy picked to attack is announced first (a small amber marker at the edge of your view, one tick in the controller on that side) and waits a moment; its attack then winds up longer (Combat.Pace), with a marker in the colour of its glow (green = counter it, red = get out of the way) that fills up until the hit, a hiss from the attacker (muffled when it is behind you; Combat.OffViewSound), and a heartbeat in the controller on that side (both when it is behind you). Only one such attack at a time. false = none of this.");
            s_statsOnTable = cfg.Bind("Table", "StatsOnTable", true, "Your health, food, gold and fame cards (and your companion's card) lie on the table to your left, propped up toward you, instead of floating at the bottom left of your view (where you could not see them while looking at the table, and where they covered the inventory). false = as in the flat game.");
            s_gloveStats = cfg.Bind("Table", "GloveStats", true, "At the Dealer's table the back of your shield glove shows your health (the gems, as in fights) and, on its cuff, your health, food and gold in numbers: turn the back of the hand toward you to read them. false = the gems only in fights.");
            s_controlsOnTable = cfg.Bind("Table", "ControlsOnTable", true, "The game's controls bar (the button prompts at the bottom of the screen) lies on the near edge of the table in two small strips, instead of a black bar floating across the room. false = as before.");
            s_holdArena = cfg.Bind("Comfort", "HoldArena", true, "Before a fight the arena loads behind the campfire but stays asleep until you press A: nothing from the fight is heard there (user 2026-10-12: enemy noises and fight music at the campfire), and the arena's start-up hitches happen behind a short mist after your A instead of while you stand at the fire. The game's music and sounds are faded out at the campfire and come back with the fight. false = the arena starts behind the campfire as before.");
            s_portalLook = cfg.Bind("Comfort", "PortalLook", PortalStyle.Orb, "The exit portal after a won fight, from the game's own effects: Orb = a glowing sphere of turning layers round a dark core; Swirl = the flat purple vortex as before; Rift = an upright tear in the air; Halo = the game's own magic shield bubble (a companion's spell) round a dark core. It lights the ground around you in its colour and hums, so you can find it when it opens behind you.");
            s_cardLook = cfg.Bind("Table", "CardLook", HoF2VR.CardLook.Look.Strong, "How bright the cards at the Dealer's table are (user 2026-10-12: \"All the cards are kind of hard to see and read. Especially darkened ones like places you already visited\"). Game = as the game draws them; Bright = all cards brighter, a visited one only a little darker than the rest; Lit = a little brighter plus a small warm light over the table; Marked = brighter, and a visited card greyed cool instead of darkened; Strong = much brighter plus the light. Compare them on captures/card-contrast-2026-10-12/index.html.");
            s_hideFaces = cfg.Bind("Table", "HideFaces", true, "A card the game means to be hidden (the shuffled \"choose a card\" game) shows its back on both sides until it is turned, so walking round it shows nothing (user 2026-10-12). false = as the game draws it.");
            s_pendulumToPlayer = cfg.Bind("Table", "PendulumToPlayer", true, "While the pendulum (the metronome mini-game) swings it comes to you: about 60 cm in front of you, a little below your eyes, and goes back to its place on the table afterwards (user 2026-10-12). false = it stays on the table.");
            s_swipeScroll = cfg.Bind("Hands", "SwipeScroll", true, "Rows with many cards (the inventory's spares, the shop, the deck builder) scroll when you drag a finger along them, like a phone's list, and coast on after a quick swipe. A drag never presses a card: once your finger has moved about 2 cm along the row, that touch is a swipe. false = only the stick scrolls.");
            s_mapAtDeckSpot = cfg.Bind("Table", "MapAtDeckSpot", true, "Choosing a challenge on the map happens where the deck is chosen: at the side table, standing in front of its long side; going on to the deck builder needs no move (user 2026-10-12: \"the choose a deck area was good for choosing a map\"). The challenge's details stand on a panel beyond the map, toward the wall. false = MapFromCardTable decides (the card table, room dimmed).");
            s_deckLabels = cfg.Bind("Table", "DeckLabels", true, "At the deck builder each row says what it is: \"YOUR DECK\" with how many cards it holds over your deck (which lies on a dark tray), the step's title over the cards to choose from (user 2026-10-12: hard to tell which row was the deck). false = as the game shows it.");
            s_dealerGaze = cfg.Bind("Table", "DealerGaze", true, "The Dealer notices you: his head follows you a little (up to 30 degrees to the side, 15 up or down) and meets your eyes when you look at him; now and then it goes to what matters on the table (a card he shows, the pendulum, what your fingertip touches, a glance at the board). false = his head moves only as his animation does.");
            s_readingLight = cfg.Bind("Table", "ReadingLight", true, "A card the game raises to you to be read (a reward, gear with its Equip / Store question, a gained card, the shop's cards, an encounter card, a choice) gets a soft light from your side that travels with it, so it is bright and readable whatever the room's light (user 2026-10-13: \"On a card close up, they are all in shadows\"). false = lit only by the room (and CardLook).");
            s_keepOut = cfg.Bind("Table", "KeepOut", true, "Nothing the game shows at the Dealer's table comes nearer than about half a metre to your eyes: cards it presents to its flat camera (the caravan's challenge cards, a card coming back from a fight, the monsters before one) are drawn further back along your line of sight and bigger by the same amount - they look the same, only at a comfortable depth. The rows you touch keep their places.");
            s_choiceStaysPut = cfg.Bind("Table", "ChoiceStaysPut", true, "Choosing one card of several: the card you pick stays where it is and comes a little toward you while the game asks to confirm (Continue / Back); the others go back a little and dim, without turning (user 2026-10-12: the picked card went behind the others). false = the game's own zoom.");
            s_touchToFight = cfg.Bind("Comfort", "TouchToFight", true, "Entering a fight (B1, the user's decision of 2026-10-10): you stay at the Dealer's table for his call to battle - the room darkens, the cards gather above the table - and then the encounter card comes to you, glowing: touch it (or press A) to be dealt in; then the campfire waiting space (its board, the controls card, Press A) and the fight. The fight's arena waits asleep until your A at the campfire, as before. false = the campfire right away when a fight begins, as before.");
            s_calmAfterWin = cfg.Bind("Comfort", "CalmAfterWin", true, "When a fight is won and the exit portal opens, the fight music stops and the Dealer's table music plays, calm, until you leave. false = the fight music plays on.");
            s_controlButtons = cfg.Bind("Table", "ControlButtons", true, "At the Dealer's table every button of the game's controls bar (Back, Continue, Equip, Yes / No, Inventory...) is a button you touch, in a column to your right, facing you (user 2026-10-12: menus without a way back, screens that only went on with A, prompts at weird angles on the table). A, B, X and Y keep working. false = the bar's two strips lying on the table edge (ControlsOnTable).");
            s_mapFromTable = cfg.Bind("Table", "MapFromCardTable", true, "Choosing a challenge on the map: you stay at the card table (no move to the side table); the map lights up and the rest of the room dims. false = taken to the side table as before.");
            s_quickHopFade = cfg.Bind("Comfort", "QuickHopFade", true, "Only entering a fight (and the start of the game) goes through the campfire waiting space with its board and \"Press (A)\". Quick hops (back to the table after a fight, title to table, after a death) are a short fade instead. false = the campfire for every change of place, as before.");
            // (2026-10-20, the user: "Try it at 1.0" - the old ReachBoost key, 1.06 since 2026-10-11, is no longer read: a cfg that
            // still holds it gets life size; this key takes its place)
            s_playerSize = cfg.Bind("Table", "PlayerSize", 1f, "At the Dealer's table: your size against life size (1 = life size: the caravan as TableRoomScale makes it, you at your own height). Above 1 you are bigger - the table sits lower and the map's far rows come nearer (user 2026-10-11: \"The table should be lower so I can reach cards in the middle of it. Or maybe I should be bigger\" - 1.06 until 2026-10-20, the old ReachBoost). 0.8 - 1.4.");
            s_fitPitch = cfg.Bind("Hands", "GloveAngle", 20f, "Quest Touch controllers: how far the gloves are tilted (degrees, + = fingers further down) from the fit the mod was first built with, so the glove's fist wraps the controller's handle as your hand does. 0 = the old fit (about 20 degrees off). Adjust in VR: pause, VR Settings, Hands, Calibrate hands (the hand check's glove page: tilt, turn, roll, up, forward, side; while it is open, both grips held + the right stick up/down tilt the gloves too, left/right move them along the handle); it saves itself.");
            s_fitUp = cfg.Bind("Hands", "GloveUp", 1.75f, "Quest Touch controllers: the gloves' shift toward the top of the controller, in cm (with GloveAngle).");
            s_fitAlong = cfg.Bind("Hands", "GloveAlong", 2.5f, "Quest Touch controllers: the gloves' shift along the handle toward the controller's front ring, in cm (with GloveAngle).");
            s_fitYaw = cfg.Bind("Hands", "GloveTurn", 0f, "Quest Touch controllers: the gloves turned left or right about the grip (degrees; both hands turn as mirror images). Set on the hand check's first page.");
            s_fitRoll = cfg.Bind("Hands", "GloveRoll", 0f, "Quest Touch controllers: the gloves rolled about the handle (degrees; mirror images). Set on the hand check's first page.");
            s_fitSide = cfg.Bind("Hands", "GloveSide", 0f, "Quest Touch controllers: the gloves' shift sideways in cm (+ = toward the other hand). Set on the hand check's first page.");
            s_bugMark = cfg.Bind("Dev", "BugMark", false, "Development shortcut (for testing): hold B and Y together for half a second (a short buzz) and let go to mark a bug: both eyes' pictures, the game's state, your position, the log's end and (with WiVRn's microphone on) 8 seconds of your voice are saved with this session's logs (BepInEx/hof2vr_sessions/<start>/marks/). Two buzzes and a note confirm it.");
            s_scenarios = cfg.Bind("Dev", "Scenarios", false, "Development shortcut (for testing): a Scenarios menu (pause menu > VR Settings > Dev, or hold B + Y for about two seconds) loads ready-made situations: fights with a chosen weapon and enemies, bosses, gaining equipment, the end-of-challenge coins, the mini-games, a shop, the deck builder, death, the exit portal. They run in a separate dev save: your own save and run are never written; Leave scenarios brings your own back at the title. Read at the start of the game.");
            s_jumpMenu = cfg.Bind("Dev", "JumpMenu", false, "Development shortcut (for testing): hold B and Y together for about two seconds: a card offers jumps - on the map A = a fight, X = an encounter, Y = the next one on the lists; at the title A = straight to the table; in a fight A = win it. B closes the card.");
            s_feelPreset = cfg.Bind("Hands", "Preset", FeelPreset.Normal, "How much the game helps your fingertip near buttons, cards and map tiles. Normal = as your finger comes in, it drifts up to about 1 cm onto the button's middle and is held steady (no shaking on small targets). Subtle = about half of that. Strong = up to 2 cm. Off = no help: things react exactly where your finger is.");
            s_stopAtSurfaces = cfg.Bind("Hands", "StopAtSurfaces", true, "Your glove stops on buttons, cards and map tiles instead of sinking into them: a button goes down with your finger and the glove rests on it. The card table's top stops your whole hand the same way. Your real hand may go further; about 5 cm past the surface (8 cm into the table) the glove lets go and follows it again. false = the glove always follows the controller exactly.");
            s_ghostHand = cfg.Bind("Hands", "GhostHand", GhostMode.Faint, "While the glove is held back by something you touch (from about 2 cm), a see-through outline shows where your real hand is. Off = never, Faint = barely there, Clear = easier to see.");
            s_hapticStrength = cfg.Bind("Hands", "HapticStrength", 1.0f, "How long the small vibrations last when you touch things (a tick when your finger is over something, a click when it presses, a softer tick when a button comes back up, two knocks when something can't be pressed). 0 = none, 1 = normal, 2 = twice as long. (The Quest controllers let a game choose only how long a buzz lasts, not how strong it is.)");
            s_handSounds = cfg.Bind("Hands", "Sounds", 0.7f, "Volume of the hand sounds: the click of a button, the tap of a card, a soft tick when your finger is over something, the knock when something can't be pressed. 0 = off, 1 = loudest.");
            s_hapticFloor = cfg.Bind("Hands", "HapticFloorMs", 3f, "The shortest vibration you can feel, in milliseconds (the hand check's vibration test sets it). Shorter vibrations are made this long, so every touch can be felt.");
            s_touchAssist = cfg.Bind("Hands", "TouchAssist", -1f, "File only: the pull of your fingertip toward a button's middle on its own. -1 = from Preset; 0 = none, 0.35 = subtle, 0.6 = normal, 0.85 = strong.");
            s_smoothing = cfg.Bind("Hands", "Smoothing", -1f, "File only: how steady your fingertip is held near buttons (it filters tiny shakes only while the hand is nearly still, so moving never lags more than about 2 mm). -1 = from Preset; 0 = none, 0.33 = subtle, 0.66 = normal, 1 = strong.");
            s_titleBoard = cfg.Bind("Hands", "TitleMenuBoard", true, "At the title, the menu's buttons (Continue, New Game, ...) stand on a small board within reach in front of you: touch one with a fingertip, as the encounter's choices. The stick and A still work. false = the menu stays far away next to the logo (stick and A only).");
            s_menuBoard = cfg.Bind("Hands", "MenuBoard", true, "The pause menu, the settings and the game's question boxes (\"Are you sure?\") come within reach: a board about half a metre in front of you, every button, switch and arrow on it a button you press with your fingertip; sliders and the scroll bar follow your finger while you hold them down. At the Dealer's table a question's answers stand on the small board of choices, like an encounter's. The stick and A still work. false = they stay on the far screen (stick and A only).");
            s_grabThrow = cfg.Bind("Hands", "GrabThrow", true, "Grabbing and throwing (hand feel, part 2): an open hand at the dice gets ready (the fingers curl a little, the dice light up, a tick); the dice stay in your hand until you really open it (not when the grip only relaxes a little); a throw takes the fastest moment of your swing, as in Half-Life: Alyx (a fast flick no longer comes out weak). In the inventory, squeeze a grip at a card you are touching to look at it closely; let go and it goes back. false = as before.");
            s_throwBoost = cfg.Bind("Hands", "ThrowBoost", 1.1f, "With GrabThrow: throws are this much stronger than your hand's speed (throws in VR come out short). 1 = exactly your hand's speed.");
            s_latePose = cfg.Bind("Hands", "LatePose", true, "Your gloves are drawn with the newest controller position, the one the headset shows the picture with (about one frame fresher, so they don't trail your hands). false = the older one, as before.");
            s_handCheckOffered = cfg.Bind("Hands", "HandCheckOffered", false, "Set by the game once the one-minute hand check (glove fit, fingertips, vibration test) was offered at the title. false = offer it again at the next start. It can always be run from the development jump menu (hold B + Y for about two seconds).");
            s_interactHaptics = cfg.Bind("Table", "InteractionHaptics", 1.0f, "Strength of the controller vibration for table interactions (grabbing, shaking and throwing the dice, stopping the pendulum or wheel). 0 = off.");
            s_hrtf = cfg.Bind("Audio", "Hrtf", false, "3D sound: sounds in the world (the Dealer's voice, enemies, hits, footsteps, the cards on the table) are heard through a real HRTF (Steam Audio's head-related filters), so they sit around your head - in front, behind, above - as in Half-Life: Alyx, instead of only left/right panning. Music, the hero's own sounds and menu clicks stay as they are. Applies at once (also on the VR settings page, Voice tab). Needs the Steam Audio files from install.sh; without them, or if anything fails, the game's own panning is used.");
            s_hrtfInterp = cfg.Bind("Audio", "HrtfInterpolation", HrtfInterp.Bilinear, "How the HRTF follows a sound's direction between its measured directions: Bilinear = smooth while you turn your head (twice the CPU of Nearest, still tiny); Nearest = the closest measured direction.");
            s_hrtfGroups = cfg.Bind("Audio", "HrtfGroups", Hrtf.DefaultGroups, "The game's sound groups (mixer groups) that go through the HRTF. Not listed: music, stings, ambience beds, camera effects, the hero's own sounds, menu selection sounds.");
            s_hrtfMinBlend = cfg.Bind("Audio", "HrtfMinBlend", 0.5f, "A sound goes through the HRTF only when it is at least this much 3D (Unity's spatial blend, 0 = flat, 1 = fully 3D).");
            s_hrtfMinDistance = cfg.Bind("Audio", "HrtfMinDistance", 0.25f, "Sounds closer to your head than this (real metres) keep the game's panning: at the head a sound has no direction.");
            s_hrtfGainDb = cfg.Bind("Audio", "HrtfGainDb", 0f, "Loudness of the HRTF sounds in dB (read at the start of the game).");
            s_hrtfNorm = cfg.Bind("Audio", "HrtfNormalization", HrtfNorm.None, "Steam Audio's default HRTF is louder in some directions than in others (measured: right-side sounds 3-4 dB quieter than left-side ones, straight ahead quieter than behind). RMS = the same loudness from every direction (Steam Audio's own option; the direction cues stay). None = the HRTF as measured. Read when the HRTF is made (the start of the game).");
            s_hrtfSofa = cfg.Bind("Audio", "HrtfSofaFile", "", "A personal HRTF as a SOFA file (full path), instead of Steam Audio's default. Empty = the default. Read at the start of the game.");
            s_hrtfAir = cfg.Bind("Audio", "HrtfAirAbsorption", false, "Far sounds lose some of their highs, as in air (Steam Audio's air absorption).");
            s_headShadowWithHrtf = cfg.Bind("Audio", "HeadShadowWithHrtf", false, "The off-view attack warning's own muffling from behind (a low-pass filter) also while the HRTF is on. false = the HRTF alone makes it sound behind you.");
            try { s_stamp = File.GetLastWriteTimeUtc(cfg.ConfigFilePath); } catch (Exception) { }
            // a seated test run (HOF2VR_SEATED=1, scripts' --seated): seated from the first frame, for this run only
            string seatedEnv = TestMode.Enabled ? Environment.GetEnvironmentVariable("HOF2VR_SEATED") : null;
            if (seatedEnv == "1" || seatedEnv == "0")
            {
                SetLive(s_seated, seatedEnv == "1");
                Plugin.L.LogInfo("settings: " + (seatedEnv == "1" ? "seated" : "standing") + " for this test run (HOF2VR_SEATED)");
            }
            // a test run with the HRTF on (or off) from the boot (HOF2VR_HRTF=1/0; env 1's candidate boots it both ways): for
            // this run only - env 1's settings file is the player's, and test runs never write it
            string hrtfEnv = TestMode.Enabled ? Environment.GetEnvironmentVariable("HOF2VR_HRTF") : null;
            if (hrtfEnv == "1" || hrtfEnv == "0")
            {
                SetLive(s_hrtf, hrtfEnv == "1");
                Plugin.L.LogInfo("settings: the HRTF " + (hrtfEnv == "1" ? "on" : "off") + " for this test run (HOF2VR_HRTF)");
            }
            if (s_cfgSaveRun) Plugin.L.LogWarning("settings: this test run saves its settings file (HOF2VR_CFG_SAVE): " + cfg.ConfigFilePath);
            // `cfg [Key [value]]`: show or set a setting for this run only (tests; the file is not written)
            Diag.Commands["cfg"] = a =>
            {
                if (a.Length < 2)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (ConfigDefinition d in cfg.Keys) sb.Append(d.Key).Append('=').Append(cfg[d].GetSerializedValue()).Append(' ');
                    return sb.ToString();
                }
                foreach (ConfigDefinition d in cfg.Keys)
                {
                    if (!d.Key.Equals(a[1], StringComparison.OrdinalIgnoreCase)) continue;
                    if (a.Length > 2)
                    {
                        bool save = cfg.SaveOnConfigSet;
                        cfg.SaveOnConfigSet = false;
                        try { cfg[d].SetSerializedValue(a[2]); }
                        finally { cfg.SaveOnConfigSet = save; }
                    }
                    return d.Key + "=" + cfg[d].GetSerializedValue() + (a.Length > 2 ? " (this run only)" : "");
                }
                return "no setting " + a[1];
            };
        }

        /// The settings file (the VR settings page writes its entries through it).
        internal static ConfigFile Cfg => s_cfg;

        /// Sets [section] key for this run only (tests: the file is not written), as `cfg Key value` does.
        public static bool SetForRun(string section, string key, string value)
        {
            if (s_cfg == null) return false;
            foreach (ConfigDefinition d in s_cfg.Keys)
            {
                if (d.Section != section || d.Key != key) continue;
                bool save = s_cfg.SaveOnConfigSet;
                s_cfg.SaveOnConfigSet = false;
                try { s_cfg[d].SetSerializedValue(value); }
                finally { s_cfg.SaveOnConfigSet = save; }
                return true;
            }
            return false;
        }

        public static bool HrtfOn => s_hrtf != null && s_hrtf.Value;
        public static HrtfInterp HrtfInterpolation => s_hrtfInterp?.Value ?? HrtfInterp.Bilinear;
        public static string HrtfGroups => s_hrtfGroups?.Value ?? Hrtf.DefaultGroups;
        public static float HrtfMinBlend => Mathf.Clamp01(s_hrtfMinBlend?.Value ?? 0.5f);
        public static float HrtfMinDistance => Mathf.Clamp(s_hrtfMinDistance?.Value ?? 0.25f, 0f, 2f);
        public static float HrtfGainDb => Mathf.Clamp(s_hrtfGainDb?.Value ?? 0f, -12f, 12f);
        public static HrtfNorm HrtfNormalization => s_hrtfNorm?.Value ?? HrtfNorm.None;
        public static string HrtfSofaFile => s_hrtfSofa?.Value ?? "";
        public static bool HrtfAirAbsorption => s_hrtfAir != null && s_hrtfAir.Value;
        public static bool HeadShadowWithHrtf => s_headShadowWithHrtf != null && s_headShadowWithHrtf.Value;

        static DateTime s_stamp;
        static float s_nextCheck;

        /// Re-read the settings file when it was saved (checked every 2 s).
        public static void Poll()
        {
            if (s_cfg == null || Time.unscaledTime < s_nextCheck) return;
            s_nextCheck = Time.unscaledTime + 2f;
            try
            {
                DateTime t = File.GetLastWriteTimeUtc(s_cfg.ConfigFilePath);
                if (t == s_stamp) return;
                s_stamp = t;
                bool save = s_cfg.SaveOnConfigSet;
                s_cfg.SaveOnConfigSet = false;   // reading must not write the file back
                try { s_cfg.Reload(); }
                finally { s_cfg.SaveOnConfigSet = save; }
                Plugin.L.LogInfo("settings re-read from " + Path.GetFileName(s_cfg.ConfigFilePath));
            }
            catch (Exception e) { Plugin.L.LogWarning("settings: " + e.Message); }
        }

        public static bool LogInput => s_logInput != null && s_logInput.Value;
        public static bool SnapTurn => s_snapTurn != null && s_snapTurn.Value;
        public static float SnapAngle => s_snapAngle?.Value ?? 30f;
        public static bool SnapTurnBlink => s_snapBlink != null && s_snapBlink.Value;
        public static float SmoothTurnSpeed => s_smoothTurnSpeed?.Value ?? 120f;
        public static float RenderScale => s_renderScale?.Value ?? 1f;
        public static bool LeftHanded => s_leftHanded != null && s_leftHanded.Value;
        public static float SwingSpeed => s_swing?.Value ?? 3.0f;
        public static float ShieldSpeed => s_shield?.Value ?? 1.6f;
        public static float StabSpeed => s_stab?.Value ?? 1.8f;
        public static float Vignette => Mathf.Clamp01(s_vignette?.Value ?? 0.8f);
        public static float WalkVignette => Mathf.Clamp01(s_walkVignette?.Value ?? 0.4f);
        public static float TurnVignette => Mathf.Clamp01(s_turnVignette?.Value ?? 0.5f);
        public static bool Seated => s_seated != null && s_seated.Value;
        public static float EyeHeight => Mathf.Clamp(s_eyeHeight?.Value ?? 1.65f, 1.0f, 2.2f);
        public const float SeatedTableMin = 0.55f, SeatedTableMax = 1.05f;   // (the page's steps: 2.5 cm, from 0.75)
        public static float SeatedTableHeight => Mathf.Clamp(s_seatedTable?.Value ?? 0.75f, SeatedTableMin, SeatedTableMax);
        public static float SeatedLift => Mathf.Clamp(s_seatedLift?.Value ?? 0.4572f, 0f, 1f);
        public static TableView SeatedTableView => s_seatedTableView?.Value ?? TableView.Table;

        /// Standing / Seated (the hand check's first page, the VR settings page): live at once; saved, except in test runs
        /// (they share the player's settings file: there it holds for the run only).
        public static void SetSeated(bool on) => SetLive(s_seated, on);
        public static void SetSeatedTableView(TableView v) => SetLive(s_seatedTableView, v);

        /// The seated table's height nudge (metres, + = the table comes up toward the eyes).
        public static void StepSeatedTable(float step) =>
            SetLive(s_seatedTable, Mathf.Round(Mathf.Clamp(SeatedTableHeight + step, SeatedTableMin, SeatedTableMax) * 1000f) / 1000f);

        static void SetLive<T>(ConfigEntry<T> e, T v)
        {
            if (e == null || s_cfg == null) return;
            bool save = s_cfg.SaveOnConfigSet;
            if (TestMode.Enabled) s_cfg.SaveOnConfigSet = false;
            try { e.Value = v; }
            finally { s_cfg.SaveOnConfigSet = save; }
        }
        /// The caravan's scale with the player's size there (Table.PlayerSize; 1 = life size).
        public static float TableScale => Mathf.Clamp((s_tableScale?.Value ?? 1.2f) * PlayerSize, 0.5f, 3f);
        /// Table.PlayerSize, or the `playersize` seam's value for this run (NaN = the cfg's; never saved).
        public static float LivePlayerSize = float.NaN;
        public static float PlayerSize => Mathf.Clamp(float.IsNaN(LivePlayerSize) ? s_playerSize?.Value ?? 1f : LivePlayerSize, 0.8f, 1.4f);
        public static AoMode VrAmbientOcclusion => s_vrAo?.Value ?? AoMode.Half;
        public static float PerfLogSeconds => Mathf.Max(0f, s_perfLog?.Value ?? 10f);
        public static DiceMode DiceThrow => s_diceThrow?.Value ?? DiceMode.Hybrid;
        public static bool DiceSummon => s_diceSummon == null || s_diceSummon.Value;
        public static bool StopBySlap => s_stopSlap == null || s_stopSlap.Value;
        public static bool StopByGrip => s_stopGrip == null || s_stopGrip.Value;
        public static bool NudgeByFlick => s_nudgeFlick == null || s_nudgeFlick.Value;
        public static bool TouchCards => s_touchCards == null || s_touchCards.Value;
        public static bool PlayerFightExit => s_playerFightExit == null || s_playerFightExit.Value;
        public static float FightExitAutoSeconds => Mathf.Max(0f, s_fightExitAuto?.Value ?? 0f);
        public static bool WaitingSpace => s_waitingSpace == null || s_waitingSpace.Value;
        public static SpaceLook WaitingSpaceLook => s_spaceLook?.Value ?? SpaceLook.Campfire;
        public static float InteractionHaptics => Mathf.Clamp(s_interactHaptics?.Value ?? 1f, 0f, 2f);
        public static bool TextAboveDealer => s_textAboveDealer == null || s_textAboveDealer.Value;
        public static bool ChoiceBoard => s_choiceBoard == null || s_choiceBoard.Value;
        public static float StandDistance => Mathf.Clamp(s_standDistance?.Value ?? 0.18f, 0.05f, 0.8f);
        public static float StandSideways => Mathf.Clamp(s_standSideways?.Value ?? 0f, -1f, 1f);
        public static float ChoiceBoardDistance => Mathf.Clamp(s_choiceDistance?.Value ?? 0.52f, 0.3f, 0.75f);
        public static bool Lasers => s_lasers != null && s_lasers.Value;
        public static bool QuestButtonPrompts => s_questPrompts == null || s_questPrompts.Value;
        public static bool Vibration => s_vibration == null || s_vibration.Value;
        public static bool PressToContinue => s_pressToContinue == null || s_pressToContinue.Value;
        public static bool WaitingBoard => s_waitingBoard == null || s_waitingBoard.Value;
        public static BoardStyle WaitingBoardStyle => s_boardStyle?.Value ?? BoardStyle.Card;
        public static bool FlatGround => s_flatGround == null || s_flatGround.Value;
        public static bool FastLoading => s_fastLoading == null || s_fastLoading.Value;
        public static bool SkipIntros => s_skipIntros != null && s_skipIntros.Value;
        public static bool QuietNearGround => s_quietNear == null || s_quietNear.Value;
        /// (always Off since the playtest of 2026-10-17: no wall of text about the controls, whatever an older settings file says)
        public static FightCard ControlsCard => FightCard.Off;
        public static float EnemyHealth => Mathf.Clamp(s_enemyHealth?.Value ?? 0.15f, 0.05f, 1f);
        public static float EnemyDamage => Mathf.Clamp(s_enemyDamage?.Value ?? 0.33f, 0.1f, 1f);
        public static bool OffViewWarning => s_offViewWarning == null || s_offViewWarning.Value;
        public static bool StatsOnTable => s_statsOnTable == null || s_statsOnTable.Value;
        public static bool GloveStats => s_gloveStats == null || s_gloveStats.Value;
        public static bool ControlsOnTable => s_controlsOnTable == null || s_controlsOnTable.Value;
        public static bool ControlButtons => s_controlButtons == null || s_controlButtons.Value;
        public static bool HoldArena => s_holdArena == null || s_holdArena.Value;
        public static bool TouchToFight => s_touchToFight == null || s_touchToFight.Value;
        public enum PortalStyle { Orb, Swirl, Rift, Halo }
        static ConfigEntry<CardLook.Look> s_cardLook;
        public static CardLook.Look CardLook => s_cardLook != null ? s_cardLook.Value : HoF2VR.CardLook.Look.Strong;
        static ConfigEntry<bool> s_hideFaces;
        public static bool HideFaces => s_hideFaces == null || s_hideFaces.Value;
        static ConfigEntry<bool> s_pendulumToPlayer;
        public static bool PendulumToPlayer => s_pendulumToPlayer == null || s_pendulumToPlayer.Value;
        static ConfigEntry<bool> s_swipeScroll;
        public static bool SwipeScroll => s_swipeScroll == null || s_swipeScroll.Value;
        static ConfigEntry<bool> s_mapAtDeckSpot;
        public static bool MapAtDeckSpot => s_mapAtDeckSpot == null || s_mapAtDeckSpot.Value;
        static ConfigEntry<bool> s_deckLabels;
        public static bool DeckLabels => s_deckLabels == null || s_deckLabels.Value;
        static ConfigEntry<bool> s_choiceStaysPut;
        public static bool ChoiceStaysPut => s_choiceStaysPut == null || s_choiceStaysPut.Value;
        static ConfigEntry<bool> s_keepOut, s_dealerGaze, s_readingLight;
        public static bool ReadingLight => s_readingLight == null || s_readingLight.Value;
        public static bool DealerGaze => s_dealerGaze == null || s_dealerGaze.Value;
        public static bool KeepOut => s_keepOut == null || s_keepOut.Value;
        static ConfigEntry<PortalStyle> s_portalLook;
        public static PortalStyle? OverridePortalLook;
        public static PortalStyle PortalLook => OverridePortalLook ?? (s_portalLook != null ? s_portalLook.Value : PortalStyle.Orb);
        public static bool CalmAfterWin => s_calmAfterWin == null || s_calmAfterWin.Value;
        public static bool MapFromCardTable => s_mapFromTable == null || s_mapFromTable.Value;
        public static bool QuickHopFade => s_quickHopFade == null || s_quickHopFade.Value;
        public static bool BugMark => s_bugMark != null && s_bugMark.Value;
        public static bool Scenarios => s_scenarios != null && s_scenarios.Value;
        public static bool JumpMenu => s_jumpMenu != null && s_jumpMenu.Value;
        public static float GloveAngle => Mathf.Clamp(float.IsNaN(LiveGloveAngle) ? s_fitPitch?.Value ?? 20f : LiveGloveAngle, -45f, 45f);
        public static float GloveUp => Mathf.Clamp(float.IsNaN(LiveGloveUp) ? s_fitUp?.Value ?? 1.75f : LiveGloveUp, -6f, 6f);
        public static float GloveAlong => Mathf.Clamp(float.IsNaN(LiveGloveAlong) ? s_fitAlong?.Value ?? 2.5f : LiveGloveAlong, -8f, 8f);
        public static float GloveTurn => Mathf.Clamp(float.IsNaN(LiveGloveTurn) ? s_fitYaw?.Value ?? 0f : LiveGloveTurn, -45f, 45f);
        public static float GloveRoll => Mathf.Clamp(float.IsNaN(LiveGloveRoll) ? s_fitRoll?.Value ?? 0f : LiveGloveRoll, -60f, 60f);
        public static float GloveSide => Mathf.Clamp(float.IsNaN(LiveGloveSide) ? s_fitSide?.Value ?? 0f : LiveGloveSide, -5f, 5f);
        public static float LiveGloveTurn = float.NaN, LiveGloveRoll = float.NaN, LiveGloveSide = float.NaN;

        /// The hand check's manual fit (item 12, 2026-10-12): one axis a step at a time, live; `SaveGloveFit` writes them all.
        public static void StepGloveFit(string axis, float step)
        {
            switch (axis)
            {
                case "tilt": LiveGloveAngle = Mathf.Clamp(GloveAngle + step, -45f, 45f); break;
                case "turn": LiveGloveTurn = Mathf.Clamp(GloveTurn + step, -45f, 45f); break;
                case "roll": LiveGloveRoll = Mathf.Clamp(GloveRoll + step, -60f, 60f); break;
                case "up": LiveGloveUp = Mathf.Clamp(GloveUp + step, -6f, 6f); break;
                case "along": LiveGloveAlong = Mathf.Clamp(GloveAlong + step, -8f, 8f); break;
                case "side": LiveGloveSide = Mathf.Clamp(GloveSide + step, -5f, 5f); break;
            }
        }

        /// The hand check's sliders (M1, 2026-10-17): one axis set to a value, live (saved with the rest by SaveGloveFit).
        public static void SetGloveFit(string axis, float v)
        {
            switch (axis)
            {
                case "tilt": LiveGloveAngle = Mathf.Clamp(v, -45f, 45f); break;
                case "turn": LiveGloveTurn = Mathf.Clamp(v, -45f, 45f); break;
                case "roll": LiveGloveRoll = Mathf.Clamp(v, -60f, 60f); break;
                case "up": LiveGloveUp = Mathf.Clamp(v, -6f, 6f); break;
                case "along": LiveGloveAlong = Mathf.Clamp(v, -8f, 8f); break;
                case "side": LiveGloveSide = Mathf.Clamp(v, -5f, 5f); break;
            }
        }

        public static float GloveFit(string axis)
        {
            switch (axis)
            {
                case "tilt": return GloveAngle;
                case "turn": return GloveTurn;
                case "roll": return GloveRoll;
                case "up": return GloveUp;
                case "along": return GloveAlong;
                default: return GloveSide;
            }
        }

        /// Back to the fit the mod ships with.
        public static void ResetGloveFit()
        {
            LiveGloveAngle = 20f; LiveGloveTurn = 0f; LiveGloveRoll = 0f; LiveGloveUp = 1.75f; LiveGloveAlong = 2.5f; LiveGloveSide = 0f;
        }

        public static string GloveFitText() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "tilt {0:0.#}\u00b0, turn {1:0.#}\u00b0, roll {2:0.#}\u00b0, up {3:0.##} cm, forward {4:0.##} cm, side {5:0.##} cm", GloveAngle, GloveTurn, GloveRoll, GloveUp, GloveAlong, GloveSide);
        public static FeelPreset HandPreset => s_feelPreset?.Value ?? FeelPreset.Normal;
        public static bool StopAtSurfaces => s_stopAtSurfaces == null || s_stopAtSurfaces.Value;
        public static bool TitleMenuBoard => s_titleBoard == null || s_titleBoard.Value;
        public static bool LatePose => s_latePose == null || s_latePose.Value;
        public static bool MenuBoard => s_menuBoard == null || s_menuBoard.Value;
        public static bool GrabThrow => s_grabThrow == null || s_grabThrow.Value;
        public static float ThrowBoost => s_throwBoost == null ? 1.1f : Mathf.Clamp(s_throwBoost.Value, 1f, 1.5f);
        public static GhostMode GhostHand => s_ghostHand?.Value ?? GhostMode.Faint;
        /// Interaction haptics length factor: [Hands] HapticStrength x the older [Table] InteractionHaptics (both 1 by default).
        public static float HapticStrength => Mathf.Clamp(float.IsNaN(LiveHapticStrength) ? s_hapticStrength?.Value ?? 1f : LiveHapticStrength, 0f, 2f) * InteractionHaptics;
        public static float HandSounds => Mathf.Clamp01(s_handSounds?.Value ?? 0.7f);
        public static float HapticFloorMs => Mathf.Clamp(float.IsNaN(LiveHapticFloor) ? s_hapticFloor?.Value ?? 3f : LiveHapticFloor, 1f, 12f);
        /// The hand check's result for this run (test runs never write the file).
        public static float LiveHapticFloor = float.NaN, LiveGloveUp = float.NaN;
        public static float TouchAssist => s_touchAssist?.Value ?? -1f;
        public static float Smoothing => s_smoothing?.Value ?? -1f;
        public static bool HandCheckOffered => s_handCheckOffered != null && s_handCheckOffered.Value;
        /// Saves a hand-check result (never from test runs: they share the player's settings file).
        public static void SaveHandCheck(float floorMs, bool offered)
        {
            if (TestMode.Enabled) return;
            if (floorMs > 0f && s_hapticFloor != null) s_hapticFloor.Value = Mathf.Min(floorMs, 10f);
            if (offered && s_handCheckOffered != null) s_handCheckOffered.Value = true;
        }

        /// The hand check's fingertip fix: the gloves move up / along the handle (cm). Saved, except in test runs (this run only).
        public static void ShiftGloveFit(float upCm, float alongCm)
        {
            float up = Mathf.Clamp(GloveUp + upCm, -6f, 6f), along = Mathf.Clamp(GloveAlong + alongCm, -8f, 8f);
            if (TestMode.Enabled || s_fitUp == null || s_fitAlong == null) { LiveGloveUp = up; LiveGloveAlong = along; return; }
            s_fitUp.Value = Mathf.Round(up * 100f) / 100f;
            s_fitAlong.Value = Mathf.Round(along * 100f) / 100f;
        }

        /// The in-VR glove adjustment (VRHands) while the grips are held; written to the file when they are let go.
        public static float LiveGloveAngle = float.NaN, LiveGloveAlong = float.NaN;

        public static void SaveGloveFit()
        {
            if (TestMode.Enabled) return;   // test runs share the player's settings file: the live values stay for this run only
            if (s_fitPitch != null && !float.IsNaN(LiveGloveAngle)) s_fitPitch.Value = Mathf.Round(LiveGloveAngle * 10f) / 10f;
            if (s_fitAlong != null && !float.IsNaN(LiveGloveAlong)) s_fitAlong.Value = Mathf.Round(LiveGloveAlong * 100f) / 100f;
            if (s_fitUp != null && !float.IsNaN(LiveGloveUp)) s_fitUp.Value = Mathf.Round(LiveGloveUp * 100f) / 100f;
            if (s_fitYaw != null && !float.IsNaN(LiveGloveTurn)) s_fitYaw.Value = Mathf.Round(LiveGloveTurn * 10f) / 10f;
            if (s_fitRoll != null && !float.IsNaN(LiveGloveRoll)) s_fitRoll.Value = Mathf.Round(LiveGloveRoll * 10f) / 10f;
            if (s_fitSide != null && !float.IsNaN(LiveGloveSide)) s_fitSide.Value = Mathf.Round(LiveGloveSide * 100f) / 100f;
            LiveGloveAngle = LiveGloveAlong = LiveGloveUp = LiveGloveTurn = LiveGloveRoll = LiveGloveSide = float.NaN;
        }

        /// The hand check's vibration length (Hands.HapticStrength, 0-2): live; saved, except in test runs.
        public static float LiveHapticStrength = float.NaN;
        public static void StepHapticStrength(float step)
        {
            float v = Mathf.Clamp(Mathf.Round(((float.IsNaN(LiveHapticStrength) ? s_hapticStrength?.Value ?? 1f : LiveHapticStrength) + step) * 4f) / 4f, 0f, 2f);
            if (TestMode.Enabled || s_hapticStrength == null) { LiveHapticStrength = v; return; }
            s_hapticStrength.Value = v;
            LiveHapticStrength = float.NaN;
        }
    }
}

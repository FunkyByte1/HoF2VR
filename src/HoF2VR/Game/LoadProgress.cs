// PROTOTYPE (work/limbo2/progress, 2026-10-11) - not in the build yet. Compile-checked against the mod's
// sources and the game's Assembly-CSharp (net35, C# 7.3 features only, no newer BCL API).
//
// The user: "Can we get a progress bar or something so you have an idea of how long things are taking?"
//
// LoadProgress is a rendering-free model of how far the current wait in the waiting space has got:
//   LoadProgress.Begin(key) / End(success) / Track(placeHold, spaceWanted)  - lifecycle
//   LoadProgress.Value (0..1, never goes back, < 0.985 until End), Phase (player-facing label), PhaseId,
//   Elapsed, Expected, Remaining (s), Known (false = no estimate: show "busy", not a fill), Visible
//
// =====================================================================================================
// 1. WHAT THE GAME DOES, AND WHERE THE TIME GOES  (game files: reference/decompiled/Assembly-CSharp)
// =====================================================================================================
// Timing sources: BepInEx lines carry no timestamps and Player.log neither. Used instead: the mod's own
// durations ("fade release 'place' after X s", "limbo: waiting space off after X s", "place: left P after
// X s", "place: waited X s in the dark for the tunnel to fade") and PerfMonitor's 10 s windows, whose
// "misses" lists give exact in-window times of long frames (/guard = a camera rendered for the first time
// = a scene was activated). Quest = the user's session 2026-10-11 07:44 (WiVRn, 90 Hz,
// work/perf/user-session-20261011-0744-*.log). Monado = test runs logs/bepinex-limbo-F*, -regress-limbo,
// -interact-limbo-final, limbo-L2.out (60 Hz).
//
// All bundles are chunked LZ4HC (checked the UnityFS block tables: scene/ui 27.9 MB -> 92 MB, start_scene,
// shared_nature 90 MB, shared_shaders 105 MB, ...), so AssetBundle.LoadFromFile - which the game calls
// SYNCHRONOUSLY (AssetBundleManager.cs:284) - only reads the block table; data streams in through
// SceneManager.LoadSceneAsync / AssetBundle.LoadAssetAsync. On the user's PC the arena load + activation
// took ~0.2-0.5 s and the table reload ~0.2 s. Almost all the wall time is the game's own SCRIPTED
// choreography (WaitForSeconds, CameraFade fades, splash timers, the title intro animation), which is
// why time-based progress with learned durations is accurate here; real counters matter for cold caches.
//
// boot (first VR frame -> title menu up). Quest 20.6 s; Monado 21.9-22.1 s (~20 runs).
//   Loader.Start (Loader.cs:57-97): 2 splash menus, each m_timerInterval = 2.0 s after its transition
//   (Update :99-109, NextSplash :111-139), then the Loading menu. After 1 s + AssetBundleManager init it
//   preloads start_scene (:75) and UI (:77) with allowSceneActivation=false, plus a SYNC LoadAsset of the
//   UI atlas (:76). Activation is allowed only when the Loading menu is in (OnLoadingTransitionComplete
//   :150-165). AssetManager.Start (AssetManager.cs:420-431) loads 30 asset libraries one after another
//   (LoadAllAssetsAsync; list :371-406) in parallel with the splashes.
//   GameState_Init.Startup (GameState_Init.cs:54-178): waits AssetManager (:65), StartScreen_Init menu,
//   StartScreenController.Preload (2 hero models, :85), unloads loader (:86), GC (:87), Display() (:88:
//   title camera on). Then sign-in, profile, settings, player, last save, mods, 0.5 s (:139), OnProfileReady
//   -> LoadCharacter (StartScreenController.cs:248-292; Player.log "[TIME]" 0.14 + 0.09 s, + 0.5 s) ->
//   SetSequence(StartScreen_Ready) (:291) -> the intro animation's "ready" event -> TransitionLogo ->
//   DisplayMenuUI sets m_menuShown (:354-357), which Places waits for.
//   Quest split (perf window offsets, +0.6..1.0 s to hold time): activation frames (/guard, 120 + 210 ms)
//   at ~8.3-8.9 s, GameState_Init ~8.8 s, Display()+GC (96 ms /gc/guard) ~12.6 s, one 27 ms frame ~13.4 s,
//   then NO long frame until the release at 20.6 s: the last ~6-7 s are mostly the title's own intro
//   (camera fly-in + logo fade) playing behind the space. Seeds: splash 6.8 | activate 2.0 (split est.,
//   sum 8.8 measured) | init 3.8 | profile 2.1 (est.) | title 5.9 (est.).
//
// title_table (Places departs at GameState_StartScreen.cs:82 -> space released). Quest: hold 1.1 s
//   ("left Title after 1.0 s", probe 0.1 s) + 1.8 s while the game's loading backdrop is still opaque =
//   2.9 s. Monado: hold 2.3-2.5 + 1.6 s. GameState_Profile.Startup (GameState_Profile.cs:25-109):
//   LoadModAssets :49, card_table LoadLevelAsync :78 (no activation) then the SAME op again with activation
//   :87-88, UnloadSceneAsync(start_scene) :99, PreloadCampfireArt / PreloadCardTemplates :105-106 ->
//   GameState_Start. Seeds: table 1.0 | settle 0.1 | reveal 1.8.
//
// fight_in (GameState_Level_In). Quest 9.4 s (no showcase intro); Monado 13.6-14.0 s (every one of them
//   had a showcase intro, Player.log "Playing intro showcase moment id ..."). Most likely the feature
//   card's "FeatureCardIn" animation in the tunnel (MeshWormhole.DisplayFeatureCard :731-750, awaited in
//   StartTransitionOut :460) adds the ~4 s, never seen in VR (a Monado run's one fight without a showcase,
//   logs/autoplay-interact2.out 03:23:40, spent 14 s in the whole Level_In state vs 20-25 s with one).
//   deal:   AnimateCardsIntoWormhole :386-418; TriggerWormhole :511-522 = WormholeDelay 1.75 + 1 s +
//           TransitionUp (MeshWormhole.cs:392-439: 0.2 s table fade + 1.5 s) ~ 4.5 s. Ends at
//           LevelManager.PreLoadLevelAsync :643 (LevelManager.cs:89-99).
//   load:   AssetBundleManager.LoadLevelAsync (AssetBundleManager.cs:529-541) -> AssetBundleLoadLevelOperation
//           (.Request = SceneManager.LoadSceneAsync, made a frame later in its Update,
//           AssetBundleLoadLevelOperation.cs:44-61, :55); activation allowed at once (:656), waited at :658.
//           Quest: hitch at ~4.9 s, arena active + new cameras at ~5.0-5.1 s.
//   build:  table UnLoad(bundle) :660 (CardTableLoader.cs:213-229), player camera / singletons / player
//           controller :693-731, CombatTask.InitLevel (CombatTask.cs:685-706) -> LevelPool.CacheAndBuild
//           (LevelPool.cs:74-93; BuildSliced :207-245 instantiates ONE object per frame), LevelTransition
//           :775-950 (incl. an additive environment scene, EnvironmentStateManager.cs:264).
//   tunnel: StartTransitionOut :951 (MeshWormhole.cs:450-508: 1 s + feature card + 1.5 s wormhole fade +
//           GC) -> ResetCameraFade(PlayerCamera, 1.5 s).
//   reveal: the PlayerCamera fade-in; camera on at :991 -> Places arrival ("waited 1.4 s") -> release.
//   Seeds: deal 4.6 | load 0.4 | build 0.3 | tunnel 2.6 | reveal 1.5   (showcase: tunnel 6.7 -> 13.8 s).
//
// fight_out (GameState_Level_Out, Places departs at DisplayPreWormholeMesh :137). Quest 7.1 s ("left
//   Fight after 3.7 s" + "waited 3.3 s"); Monado 7.1-7.3 s with the player-initiated exit (FightExit);
//   9.1-9.3 s in older runs that ended through the game's own exit (+2 s before arrival: "left Fight after
//   5.8 s"), so deaths and the game's own exit portals may take longer - learned like the rest.
//   leave:   0.5 s (:138) + TransitionUp(Out) (:139: PlayerCamera fade to colour 1.5 s + wormhole fade 1.5 s).
//   rebuild: StartTransitionIn :140; unload env-state scene :151, arena :156, bundle :166;
//            CardTableLoader.LoadRoutine :173-180 (LoadLevelAsync card_table); room setup :182-245.
//   tunnel:  StartTransitionOut(UICameraFade, unloadUnused) :255: 1.5 s fade + Resources.UnloadUnusedAssets
//            (Player.log: 227 ms) + GC.
//   reveal:  UICameraFade fade-in 1.5 s -> release.          Seeds: 3.55 | 0.2 | 1.85 | 1.5.
//
// Not measured (no logs), seeds from the code (est.):
//   camp_in   Customisation_In: same deal (:258-266) | GenericSceneLoader.Load :315 (+ activation :317) |
//             table UnLoad :325, companions :349 | StartTransitionOut(PlayerCamera) :279 | entry camera
//             (Places waits until it stops overriding the PlayerCamera).   4.6 | 0.6 | 0.6 | 2.6 | 3.0
//   camp_out  Customisation_Out: exit camera :56/:60-74, then like fight_out (:80-161).  5.5 | 0.4 | 1.85 | 1.5
//   boss_in   BossBattle_In: table fade 1 s :201-206 | PreLoadLevelAsync :209 (+ :221) | bossBattle.Init
//             (BossBattleManager.cs:386-436 incl. LevelPool.CacheAndBuild) and PlayerCamera fade :151.
//             1.1 | 1.5 | 3.0
//   boss_out  BossBattle_Out: fades :147/:152, 0.5 s :183, LoadRoutine :198, table FadeOut(1) :238.
//             1.6 | 0.5 | 1.5
//   table_swap CardTableLoader.SetScene (CardTableLoader.cs:124-194): fade 0.5 s, UnLoad, LoadRoutine,
//             0.25 s, ..., FadeOut(2) + 2 s.   0.3 | 0.5 | 2.6
//
// =====================================================================================================
// 2. THE MODEL
// =====================================================================================================
// * A wait has a key (above) and a fixed list of phases. Each phase ends at a MILESTONE seen through a
//   Harmony hook or a cheap poll (not at a guessed time); a later milestone also closes every earlier phase.
//   Each phase has a learned duration d_i (seeded from the logs, see section 1).
// * Inside the current phase: a fraction from a real source where one exists (AsyncOperation.progress of
//   the captured scene load, LevelPool's sliced-build counter, Loader's splash counter, AssetManager's 30
//   libraries, the destination CameraFade's alpha for the final fade), otherwise elapsed / d, eased: linear
//   to 80 %, then asymptotic (C1, never reaches 1). With a real source, time alone may carry a phase to 60 %.
// * Expected time left R = (1 - fraction) * d_cur + sum of the later phases (= LoadProgress.Remaining).
//   The shown value moves at v = (0.985 - shown) / max(R, 0.3 s) per second. With a correct estimate that is
//   a straight line reaching 98.5 % exactly at the end; an early milestone shrinks R (it speeds up), an
//   overrun keeps R small but positive (it slows down but never stops), a later surprise (showcase variant,
//   cold-cache load) lowers the pace - it never goes backwards, never jumps and never shows 100 % before
//   End. After End(true) it glides to 100 % in ~0.25 s while the mist covers the space. (A first version
//   mapped phases to fixed shares of the bar; a simulated 2.6 s load overrun then froze the bar at 53 % for
//   2.5 s - this pace model moves on smoothly from 52 to 98 %.)
// * Learning: on End(true) every phase whose start and end milestones were really seen updates an
//   exponential moving average (the seed counts as one sample; alpha 1/2, 1/3, 1/4, then 0.25; a sample is
//   clamped to max(4x, +3 s)); nothing is learned after an abort, a stall frame > 2 s or a total > 2.5x the
//   expectation + 5 s. Persisted in BepInEx/config/hof2vr_loadtimes.txt (key, phase, seconds, samples).
//   Every end logs one line with the measured phase times: that is also the calibration data the design
//   doc asked for ("add timestamps").
// * Cost per frame: one Step() (guarded by Time.frameCount): a few field reads through cached FieldRef
//   delegates (AccessTools_Field from Places.cs), float maths, no allocation, no reflection.
//
// =====================================================================================================
// 3. HARMONY HOOKS (all wrapped in try/catch; a hook that cannot be found is skipped with a warning)
// =====================================================================================================
//  H1 AssetBundleManager.LoadLevelAsync(string,string,bool,bool) (AssetBundleManager.cs:529)  POSTFIX
//     every scene load -> the AssetBundleLoadLevelOperation (+ mark SceneLoadStart). Covers
//     LevelManager.PreLoadLevelAsync (arena, boss), GenericSceneLoader.Load (camp), CardTableLoader.LoadRoutine
//     (table), GameState_Profile:78/:87, EnvironmentStateManager additive states, Loader :75/:77.
//  H2 AssetBundleManager.LoadAllAssetsAsync(string,Type) (:464)  POSTFIX  count AssetManager library loads.
//  H3 Loader.OnLoadingTransitionComplete(Menu,bool) (Loader.cs:150, private)  POSTFIX  boot: splashes done,
//     scenes may activate (mark LoaderDone); keeps the Loader for its private splash counters.
//  H4 StartScreenController.Display() (StartScreenController.cs:221)  POSTFIX  boot: title shown (TitleShown).
//  H5 StartScreenController.SetSequence(StartSequence) (:365)  POSTFIX  boot: title intro starts (TitleSequence).
//  H6 MeshWormhole.Setup(List<Card>,Card,WormholeDestination,WormholeStyle) (MeshWormhole.cs:334)  POSTFIX
//     a_featureCard != null && destination Combat = showcase intro (Level_In:493) -> key fight_in+showcase.
//  H7 MeshWormhole.StartTransitionIn() (:441)  PREFIX  tunnel part begins (Level_In:670, Level_Out:140,
//     Customisation_In:316, Customisation_Out:83) -> TunnelIn.
//  H8 MeshWormhole.StartTransitionOut(CameraFade,bool) (:450)  PREFIX  destination built (Level_In:951,
//     Level_Out:255, Customisation_In:279, Customisation_Out:161) -> TunnelOut; keeps a_targetSceneCameraFade
//     (its fade-out, started by ResetCameraFade :492, is the reveal: mark RevealFade + real progress).
//  H9 CardTableLoader.SetScene(CardTableSceneType,IEnumerator,Action) (CardTableLoader.cs:124)  POSTFIX
//     a real table variant swap (SceneChangeRequired) -> key table_swap for the hold that follows.
//  + GameWatch.Changed ("GameState_Init" -> InitEntered). Polled in Step: Places.Current (Arrived),
//    VRFade.IsHeldBy("place") (Released), the captured op's IsDone()/Request.progress (SceneLoaded),
//    the target fade's IsFading / private m_blendAlpha, LevelPool.Instance private m_objectPairs /
//    mSlicedIndex, AssetManager.Instance.IsLoading, Loader private m_splashIndex / m_splashSequence /
//    m_menuTimer / m_timerInterval / m_splashComplete (found once via H3 or FindObjectOfType, max 10 tries).
//  Not used on purpose: LevelManager.CurrentLevelLoadProgress (LevelManager.cs:39-49, public int 0..100) -
//  its m_load is never cleared, so it reads 100 (the previous fight) until the next PreLoadLevelAsync;
//  GameState_Level_In.s_lastLevelLoadTime (:127, set :720) is the game's own load time (:649-:719) and is
//  only logged at the end of fight_in for calibration.
//
// =====================================================================================================
// 4. INTEGRATION NOTES
// =====================================================================================================
//  Plugin.Awake (after Settings.Init, next to FightExit.Apply(H)):   LoadProgress.Init(); LoadProgress.Apply(H);
//  VRFade.LateUpdate:
//    * boot branch, right after Limbo.I.StartBoot():                 LoadProgress.Begin("boot");
//    * after placeHold and limboWanted are computed (every frame):   LoadProgress.Track(placeHold, limboWanted);
//      Track begins a keyed wait when Places' hold appears (key = KeyFor(GameWatch.State), see below; the
//      start is the hold's first frame) and ends it with End(true) when the place hold AND the space are
//      both gone (title_table: the space stays ~1.8 s after the release while the game's loading backdrop
//      is opaque - that time is part of the wait the player sees, so it is learned too).
//  Places.LateUpdate: optional, more exact - its give-up branches ("dark for N s ... showing what the game
//    shows", "not ready after N s - placing for now") should call LoadProgress.End(false): no learning from
//    timeouts. (An explicit Begin(KeyFor(...)) where it first calls VRFade.I.Hold(HoldOwner) is equivalent
//    to Track.)
//  Limbo / LimboCamp (renderer): draw while LoadProgress.Visible && (Elapsed > 0.8 s || !Active); fill =
//    Value when Known, otherwise a "busy" wave; Phase is a short player-facing label (plain names, no
//    "caravan"); Remaining rounded up, worth showing only above ~3 s. Nothing head-locked.
//  Keys (KeyFor): "" or GameState_Init -> boot; GameState_StartScreen / _Profile / _Start -> title_table;
//    GameState_Level_In -> fight_in (-> fight_in+showcase when H6 saw a feature card); GameState_Level_Out ->
//    fight_out; GameState_BossBattle_In/_Out -> boss_in/boss_out; GameState_Customisation_In/_Out ->
//    camp_in/camp_out; any other state within 3 s of a real CardTableLoader.SetScene -> table_swap;
//    otherwise no bar (quick hops, e.g. table <-> side table 0.3 s); a keyless hold that keeps the space up
//    for more than 1 s -> "other" (Known = false).
//  Diag seams: status "progress"; command `progress [info|learned|reset|begin <key>|end [fail]]`.
//
// =====================================================================================================
// 5. RISKS
// =====================================================================================================
//  * AsyncOperation.progress stops at 0.9 while allowSceneActivation is false. Only the boot really sits
//    there (Loader.cs:75-82: the scenes wait for the splashes - "loaded", not slow); Profile:78->87 and
//    Customisation_In:315->317 for a frame or two; Level_In:656 and BossBattle_In:221 allow it at once.
//    Activation itself has no progress (0.9 -> isDone in one step; the 210 ms frame at boot) -> time-based.
//  * .Request is null until AssetBundleManager.Update runs the op a frame later -> reads as 0.
//  * Boot: the wait starts at the first VR frame (VRFade's boot branch); Loader.Start and the first splash
//    are already running then, so the splash phase starts part-way (its real source reads Loader's counters).
//    The hooks are in place before Loader does any loading: Plugin.Awake runs in the chainloader
//    (Application .cctor entry point); in Player.log ~80 "Fallback handler could not load library
//    ...data-0x....so" lines (most likely the HarmonyX patch assemblies) come before "Current platform Steam"
//    (Loader.Start :63), and Loader waits >= 1 s (:70-74) before its first LoadLevelAsync; H3 fires only
//    after both splashes. Before H3 the Loader is found with FindObjectOfType (at most 10 tries, 0.5 s apart).
//  * The boot is ~20.6 s mostly of scripted waits: ~8.8 s loader (two 2 s splashes + menu transitions;
//    loads overlap), ~3.8 s GameState_Init to Display(), ~8 s profile + the title intro animation that plays
//    behind the space. The profile/title split is an estimate (no timestamps); the first End log fixes it.
//  * Showcase intros (+~4 s) are decided per fight; H6 fires in the same frame as Level_In.OnEnter, before
//    Places' hold - hooks record marks with timestamps even while idle and Begin keeps the ones < 0.25 s old.
//  * Pauses, alt-tab, the Steam overlay, a debugger: a stall > 2 s or a very long total is not learned.
//  * Places may release and take the hold again (provisional arrival after a timeout): Track ignores a new
//    hold within 0.5 s of an End.
//  * boss_in: the PlayerCamera is instantiated enabled but black (BossBattle_In:246-251), so Places may end
//    the wait early; boss and camp seeds are unmeasured.
//  * Long main-thread frames (scene activation ~0.2 s, GC, UnloadUnusedAssets 0.23 s) freeze the bar for that
//    frame; the compositor reprojects. Not fixable from here.
//  * Two findings outside this file: in VR the ~4 s showcase card animation in the tunnel and the ~6 s title
//    intro are waited out invisibly; skipping/shortening them in VR would cut real waiting time (verify).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Defiant.PostProcess;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    internal static class LoadProgress
    {
        // ---- read side ------------------------------------------------------------------------------

        public static bool Active => s_active;
        /// Draw something: a wait is running, or the bar is gliding to 100 % after End(true).
        public static bool Visible => s_active || s_finishing;
        /// False for "other" (no estimate): show a busy indicator instead of a fill.
        public static bool Known => s_baseKey != "other";
        public static string Key => s_key ?? "";
        /// 0..1, never decreases during a wait, stays below 0.985 until End.
        public static float Value { get { Step(); return s_shown; } }
        public static string Phase { get { Step(); return s_active ? s_plan[s_cur].Label : ""; } }
        public static string PhaseId { get { Step(); return s_active ? s_plan[s_cur].Id : ""; } }
        public static float Elapsed => s_active ? Time.unscaledTime - s_begin : 0f;
        public static float Expected => s_active ? Sum(s_dur) : 0f;
        /// Rough seconds left (current phase rest + later phases); 0 when idle.
        public static float Remaining { get { Step(); return s_active ? s_remaining : 0f; } }

        // ---- plans ----------------------------------------------------------------------------------

        /// Milestones. Each phase ends at one (the last at End).
        enum M { LoaderDone, InitEntered, TitleShown, TitleSequence, SceneLoadStart, SceneLoaded, TunnelIn, TunnelOut, RevealFade, Arrived, Released, End, Count }
        /// Real progress sources for a phase.
        enum Src { None, Splash, AssetLibs, SceneOp, Pool, Reveal }

        sealed class Ph
        {
            public readonly string Id, Label;
            public readonly M EndMark;
            public readonly Src Real;
            public readonly float RealShare;   // share of the phase the real source covers
            public Ph(string id, string label, M end, Src real = Src.None, float realShare = 1f)
            {
                Id = id; Label = label; EndMark = end; Real = real; RealShare = realShare;
            }
        }

        static readonly Dictionary<string, Ph[]> s_plans = new Dictionary<string, Ph[]>
        {
            { "boot", new[] {
                new Ph("splash", "Starting the game", M.LoaderDone, Src.Splash, 0.95f),
                new Ph("activate", "Opening the world", M.InitEntered),
                new Ph("init", "Setting the scene", M.TitleShown, Src.AssetLibs, 0.5f),
                new Ph("profile", "Loading your save", M.TitleSequence),
                new Ph("title", "Almost there", M.End) } },
            { "title_table", new[] {
                new Ph("table", "Going to the Dealer's table", M.Arrived, Src.SceneOp, 0.6f),
                new Ph("settle", "Arriving", M.Released),
                new Ph("reveal", "Arriving", M.End) } },
            { "fight_in", new[] {
                new Ph("deal", "The Dealer deals", M.SceneLoadStart),
                new Ph("load", "Loading the battlefield", M.SceneLoaded, Src.SceneOp),
                new Ph("build", "Gathering the enemies", M.TunnelOut, Src.Pool),
                new Ph("tunnel", "Through the portal", M.RevealFade),
                new Ph("reveal", "Arriving", M.End, Src.Reveal) } },
            { "fight_out", new[] {
                new Ph("leave", "Leaving the battlefield", M.TunnelIn),
                new Ph("rebuild", "Back to the table", M.TunnelOut, Src.SceneOp, 0.8f),
                new Ph("tunnel", "Through the portal", M.RevealFade),
                new Ph("reveal", "Arriving", M.End, Src.Reveal) } },
            { "camp_in", new[] {
                new Ph("deal", "The Dealer deals", M.SceneLoadStart),
                new Ph("load", "Loading the camp", M.SceneLoaded, Src.SceneOp),
                new Ph("build", "Setting up camp", M.TunnelOut),
                new Ph("tunnel", "Through the portal", M.RevealFade),
                new Ph("reveal", "Arriving", M.End) } },
            { "camp_out", new[] {
                new Ph("leave", "Leaving the camp", M.TunnelIn),
                new Ph("rebuild", "Back to the table", M.TunnelOut, Src.SceneOp, 0.8f),
                new Ph("tunnel", "Through the portal", M.RevealFade),
                new Ph("reveal", "Arriving", M.End, Src.Reveal) } },
            { "boss_in", new[] {
                new Ph("fade", "The final battle", M.SceneLoadStart),
                new Ph("load", "Loading the battlefield", M.SceneLoaded, Src.SceneOp),
                new Ph("build", "Gathering the enemies", M.End, Src.Pool) } },
            { "boss_out", new[] {
                new Ph("leave", "Leaving the battlefield", M.SceneLoadStart),
                new Ph("load", "Back to the table", M.SceneLoaded, Src.SceneOp),
                new Ph("reveal", "Arriving", M.End) } },
            { "table_swap", new[] {
                new Ph("fade", "Changing tables", M.SceneLoadStart),
                new Ph("load", "Changing tables", M.SceneLoaded, Src.SceneOp),
                new Ph("reveal", "Arriving", M.End) } },
            { "other", new[] { new Ph("wait", "Loading", M.End) } },
        };

        /// Seeds (s) per full key: Quest session 2026-10-11 where measured, Monado or the code otherwise
        /// (see section 1 of the header).
        static readonly Dictionary<string, float[]> s_seeds = new Dictionary<string, float[]>
        {
            { "boot", new[] { 6.8f, 2.0f, 3.8f, 2.1f, 5.9f } },
            { "title_table", new[] { 1.0f, 0.1f, 1.8f } },
            { "fight_in", new[] { 4.6f, 0.4f, 0.3f, 2.6f, 1.5f } },
            { "fight_in+showcase", new[] { 4.6f, 0.5f, 0.5f, 6.7f, 1.5f } },
            { "fight_out", new[] { 3.55f, 0.2f, 1.85f, 1.5f } },
            { "camp_in", new[] { 4.6f, 0.6f, 0.6f, 2.6f, 3.0f } },
            { "camp_out", new[] { 5.5f, 0.4f, 1.85f, 1.5f } },
            { "boss_in", new[] { 1.1f, 1.5f, 3.0f } },
            { "boss_out", new[] { 1.6f, 0.5f, 1.5f } },
            { "table_swap", new[] { 0.3f, 0.5f, 2.6f } },
            { "other", new[] { 4.0f } },
        };

        // learned durations / sample counts per full key (copies of the seeds until something is learned)
        static readonly Dictionary<string, float[]> s_learned = new Dictionary<string, float[]>();
        static readonly Dictionary<string, int[]> s_samples = new Dictionary<string, int[]>();

        // ---- state ----------------------------------------------------------------------------------

        static bool s_active, s_finishing;
        static string s_key, s_baseKey = "other";
        static Ph[] s_plan = s_plans["other"];
        static float[] s_dur = new[] { 4f };
        static float s_begin, s_shown, s_remaining, s_maxDt;
        static int s_cur, s_frame = -1;
        static readonly float[] s_markAt = NewMarks();
        static bool s_sawTransition, s_sawHold;
        static float s_holdSince = -1f, s_endedAt = -10f;

        // captured by the hooks
        static AssetBundleLoadLevelOperation s_firstOp;   // the wait's (first) scene load
        static string s_opName = "";
        static float s_opAt = -10f;
        static CameraFade s_target;                       // the destination camera's fade (StartTransitionOut)
        static float s_featureAt = -10f, s_swapAt = -10f;
        static int s_libLoads;
        static Loader s_loader;
        static float s_loaderLookAt = -10f;
        static int s_loaderLooks;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // cached field readers (FieldRef delegates; never reflection per frame)
        static readonly AccessTools_Field<Loader, int> s_splashIndex = new AccessTools_Field<Loader, int>("m_splashIndex");
        static readonly AccessTools_Field<Loader, List<SplashMenu>> s_splashSeq = new AccessTools_Field<Loader, List<SplashMenu>>("m_splashSequence");
        static readonly AccessTools_Field<Loader, float> s_menuTimer = new AccessTools_Field<Loader, float>("m_menuTimer");
        static readonly AccessTools_Field<Loader, float> s_timerInterval = new AccessTools_Field<Loader, float>("m_timerInterval");
        static readonly AccessTools_Field<Loader, bool> s_splashComplete = new AccessTools_Field<Loader, bool>("m_splashComplete");
        static readonly AccessTools_Field<LevelPool, LevelPool.ObjectPair[]> s_poolPairs = new AccessTools_Field<LevelPool, LevelPool.ObjectPair[]>("m_objectPairs");
        static readonly AccessTools_Field<LevelPool, int> s_poolIndex = new AccessTools_Field<LevelPool, int>("mSlicedIndex");
        static readonly AccessTools_Field<CameraFade, float> s_fadeAlpha = new AccessTools_Field<CameraFade, float>("m_blendAlpha");

        static float[] NewMarks()
        {
            var a = new float[(int)M.Count];
            for (int i = 0; i < a.Length; i++) a[i] = -1f;
            return a;
        }

        // ---- lifecycle --------------------------------------------------------------------------------

        /// Plugin.Awake, after Settings.Init: read the learned durations.
        public static void Init()
        {
            try { Load(); }
            catch (Exception e) { Plugin.L.LogWarning("progress: could not read " + FilePath + " - built-in durations: " + e.Message); }
        }

        /// The key for a place hold that starts now (null = a quick hop: no bar).
        public static string KeyFor(string state)
        {
            switch (state)
            {
                case "":
                case "GameState_Init": return "boot";
                case "GameState_StartScreen":
                case "GameState_Profile":
                case "GameState_Start": return "title_table";
                case "GameState_Level_In": return "fight_in";
                case "GameState_Level_Out": return "fight_out";
                case "GameState_BossBattle_In": return "boss_in";
                case "GameState_BossBattle_Out": return "boss_out";
                case "GameState_Customisation_In": return "camp_in";
                case "GameState_Customisation_Out": return "camp_out";
            }
            if (Time.unscaledTime - s_swapAt < 3f) return "table_swap";
            return null;
        }

        public static void Begin(string key) { Begin(key, Time.unscaledTime); }

        /// A wait starts (`since`: when its dark began, if that was a little earlier).
        public static void Begin(string key, float since)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (s_active)
            {
                if (BaseOf(s_key) == BaseOf(key)) return;   // the same wait (boot: VRFade and Track both begin it)
                End(false, "replaced by " + key);
            }
            Ph[] plan;
            if (!s_plans.TryGetValue(BaseOf(key), out plan)) { key = "other"; plan = s_plans[key]; }
            float now = Time.unscaledTime;
            // hooks that fired in this very frame, before the hold was taken, belong to this wait
            for (int i = 0; i < s_markAt.Length; i++) if (s_markAt[i] < now - 0.25f) s_markAt[i] = -1f;
            if (s_opAt < now - 0.25f) s_firstOp = null;
            s_target = null;
            s_key = key;
            s_baseKey = BaseOf(key);
            s_plan = plan;
            s_dur = Durations(key);
            s_begin = Mathf.Min(since, now);
            // (N5, 2026-10-17: after the waiting space's gate the wait begins at the player's A, not at the hold's start - the
            // gnome fight's "deal 11.0 s, expected 2.4" was the player's own 9.4 s at the campfire before A, then 1.9 s in the
            // dark; it was learned into the next estimates too)
            if (WaitGate.LastGoAt > s_begin && WaitGate.LastGoAt <= now) s_begin = WaitGate.LastGoAt;
            s_active = true;
            s_finishing = false;
            s_shown = 0f;
            s_cur = 0;
            s_frame = -1;
            s_maxDt = 0f;
            s_sawTransition = s_sawHold = false;
            if (s_baseKey == "boot") s_loaderLooks = 0;
            if (s_baseKey == "fight_in" && s_featureAt >= now - 1f) SetVariant("showcase");
            Plugin.L.LogInfo("progress: " + s_key + " begins (expected " + Sum(s_dur).ToString("0.0", Inv) + " s)");
        }

        public static void End(bool success) { End(success, success ? "released" : "aborted"); }

        /// The wait is over as far as loading goes (WaitGate: the place is ready, the player is asked to press A): the
        /// times are learned now (not including the player's own pause), the bar fills, and no new wait begins until
        /// the place hold is gone (ReadyDone).
        public static void Ready()
        {
            if (s_active) End(true, "ready");
            s_ready = true;
        }

        public static void ReadyDone() { s_ready = false; }

        /// The bar is full and the place waits for the player.
        public static bool IsReady => s_ready;
        static bool s_ready;

        static void End(bool success, string why)
        {
            if (!s_active) return;
            Step();
            float now = Time.unscaledTime;
            s_markAt[(int)M.End] = now;
            float total = now - s_begin, expected = Sum(s_dur);
            string phases = Summary(now);
            bool learn = success && s_baseKey != "other" && s_maxDt < 2f && total <= 2.5f * expected + 5f;
            if (learn) { LearnAll(now); Save(); }
            string extra = s_baseKey == "fight_in" ? " | game's own load time " + GameState_Level_In.s_lastLevelLoadTime.ToString("0.00", Inv) + " s (" + GameState_Level_In.s_lastLevelLoadedName + ")" : "";
            Plugin.L.LogInfo("progress: " + s_key + (success ? " done" : " aborted (" + why + ")") + " after " + total.ToString("0.0", Inv) +
                             " s (expected " + expected.ToString("0.0", Inv) + ", shown " + Mathf.RoundToInt(s_shown * 100f) + " %) | " + phases +
                             (s_opName.Length > 0 && s_firstOp != null ? " | scene " + s_opName : "") + extra + (learn ? "" : " | not learned"));
            s_active = false;
            s_finishing = success;
            s_endedAt = now;
        }

        /// The per-frame driver for VRFade.LateUpdate: begins a keyed wait when Places' hold appears and ends
        /// it when the place hold and the waiting space are both gone.
        public static void Track(bool placeHold, bool spaceWanted)
        {
            float now = Time.unscaledTime;
            if (!placeHold) { s_holdSince = -1f; s_ready = false; }
            else if (s_holdSince < 0f) s_holdSince = now;
            if (!s_active)
            {
                if (placeHold && now - s_endedAt > 0.5f && !s_ready)
                {
                    string key = KeyFor(GameWatch.State);
                    if (key != null) Begin(key, s_holdSince);
                    else if (spaceWanted && now - s_holdSince > 1f) Begin("other", s_holdSince);
                }
            }
            else if (!placeHold && !spaceWanted) End(true);
            Step();
        }

        // ---- the model --------------------------------------------------------------------------------

        static void Step()
        {
            int f = Time.frameCount;
            if (f == s_frame) return;
            s_frame = f;
            float dt = Time.unscaledDeltaTime;
            if (!s_active)
            {
                if (s_finishing)
                {
                    s_shown = Mathf.Min(1f, s_shown + 4f * dt);
                    if (s_shown >= 1f) s_finishing = false;
                }
                return;
            }
            if (dt > s_maxDt) s_maxDt = dt;
            float now = Time.unscaledTime;
            try { Poll(now); }
            catch (Exception e) { Plugin.L.LogError("progress poll: " + e); }

            // the current phase follows the last phase whose milestone was seen (skipped ones count as done)
            int n = s_plan.Length, cur = 0;
            float start = s_begin;
            for (int i = 0; i < n - 1; i++)
            {
                float t = s_markAt[(int)s_plan[i].EndMark];
                if (t < 0f) continue;
                cur = i + 1;
                if (t > start) start = t;
            }
            s_cur = cur;
            float rest = 0f;
            for (int i = cur + 1; i < n; i++) rest += s_dur[i];
            float d = s_dur[cur];
            float tf = TimeFrac(now - start, d);
            float real = -1f;
            try { real = Real(s_plan[cur].Real); }
            catch (Exception) { real = -1f; }
            float frac = real < 0f ? tf : Mathf.Max(real * s_plan[cur].RealShare, Mathf.Min(tf, 0.6f));
            frac = Mathf.Clamp(frac, 0f, 0.995f);
            // expected time left: the rest of this phase + every later phase
            s_remaining = (1f - frac) * d + rest;
            // move at the pace that reaches the cap exactly when the wait is expected to end: a straight
            // line when the estimate is right, faster when a milestone comes early, slower (never backwards,
            // never stopped) when the wait runs long
            float v = (Cap - s_shown) / Mathf.Max(s_remaining, 0.3f);
            s_shown = Mathf.Min(Cap, s_shown + v * Mathf.Min(dt, 0.25f));
        }

        /// The shown value approaches this until End (never 100 % before the wait is really over).
        const float Cap = 0.985f;

        /// Elapsed / expected: linear to 80 %, then asymptotic (C1 at the knee), never 1.
        static float TimeFrac(float t, float d)
        {
            const float K = 0.8f;
            float x = Mathf.Max(0f, t) / Mathf.Max(0.05f, d);
            if (x <= K) return x;
            return K + (1f - K) * (1f - Mathf.Exp(-(x - K) / (1f - K)));
        }

        static void Poll(float now)
        {
            // arrival: Places leaves Transition for the new place
            Places.Place p = Places.Current;
            if (p == Places.Place.Transition) s_sawTransition = true;
            else if (s_sawTransition && p != Places.Place.None) Fire(M.Arrived, now);
            // release: Places' dark hold is gone (after it was seen)
            bool held = VRFade.I != null && VRFade.I.IsHeldBy(Places.HoldOwner);
            if (held) s_sawHold = true;
            else if (s_sawHold) Fire(M.Released, now);
            // the wait's scene load has finished (activation included)
            if (s_firstOp != null && s_markAt[(int)M.SceneLoaded] < 0f && s_firstOp.IsDone()) Fire(M.SceneLoaded, now);
            // the destination camera starts fading in (after StartTransitionOut)
            if (s_target != null && s_markAt[(int)M.TunnelOut] >= 0f && s_markAt[(int)M.RevealFade] < 0f && s_target.IsFading) Fire(M.RevealFade, now);
            // a showcase intro decided after Begin
            if (s_baseKey == "fight_in" && s_key.IndexOf('+') < 0 && s_featureAt >= s_begin - 1f) SetVariant("showcase");
            // (boot: if H3 never fires, GameState_Init skips the splash phase - then neither it nor
            // "activate" is learned)
        }

        static float Real(Src s)
        {
            switch (s)
            {
                case Src.Splash: return SplashFrac();
                case Src.AssetLibs:
                    {
                        AssetManager am = AssetManager.Instance;
                        return am != null && am.IsLoading ? Mathf.Clamp01(s_libLoads / 30f) : -1f;
                    }
                case Src.SceneOp: return OpFrac(s_firstOp);
                case Src.Pool: return PoolFrac();
                case Src.Reveal:
                    return s_target != null && s_target.IsFading ? Mathf.Clamp01(1f - s_fadeAlpha.Get(s_target)) : -1f;
            }
            return -1f;
        }

        /// 0..0.9 = loading, 0.9 = loaded (waiting for / doing activation, no progress of its own), 1 = done.
        static float OpFrac(AssetBundleLoadLevelOperation op)
        {
            if (op == null) return -1f;
            AsyncOperation r = op.Request;
            if (r == null) return 0f;           // the op starts on AssetBundleManager's next Update
            if (r.isDone) return 1f;
            return Mathf.Clamp01(r.progress) * 0.95f;
        }

        /// Loader: splash i of n shown for menuTimer of timerInterval s; the Loading menu is the last part.
        static float SplashFrac()
        {
            Loader l = FindLoader();
            if (l == null) return -1f;
            if (s_splashComplete.Get(l)) return 0.9f;
            List<SplashMenu> seq = s_splashSeq.Get(l);
            int count = seq != null ? seq.Count : 2;
            float interval = Mathf.Max(0.1f, s_timerInterval.Get(l));
            float shown = Mathf.Max(0, s_splashIndex.Get(l) - 1) + Mathf.Clamp01(s_menuTimer.Get(l) / interval);
            return Mathf.Clamp01(shown / (count + 0.5f));
        }

        static Loader FindLoader()
        {
            if (s_loader != null) return s_loader;
            float now = Time.unscaledTime;
            if (s_loaderLooks >= 10 || now - s_loaderLookAt < 0.5f) return null;
            s_loaderLookAt = now;
            s_loaderLooks++;
            s_loader = UnityEngine.Object.FindObjectOfType<Loader>();
            return s_loader;
        }

        /// LevelPool.BuildSliced: one instance per frame, mSlicedIndex of m_objectPairs.Length.
        static float PoolFrac()
        {
            LevelPool pool = LevelPool.Instance;
            if (pool == null) return -1f;
            LevelPool.ObjectPair[] pairs = s_poolPairs.Get(pool);
            if (pairs == null || pairs.Length == 0) return 0.3f;   // pool made: enemy prefabs still loading
            return 0.35f + 0.5f * Mathf.Clamp01((float)s_poolIndex.Get(pool) / pairs.Length);
        }

        static void Fire(M m, float now)
        {
            if (s_markAt[(int)m] < 0f) s_markAt[(int)m] = now;
        }

        static void Mark(M m) { Fire(m, Time.unscaledTime); }

        static void SetVariant(string v)
        {
            string key = s_baseKey + "+" + v;
            if (key == s_key) return;
            s_key = key;
            s_dur = Durations(key);
            Plugin.L.LogInfo("progress: now " + s_key + " (expected " + Sum(s_dur).ToString("0.0", Inv) + " s)");
        }

        // ---- learning -----------------------------------------------------------------------------------

        static void LearnAll(float now)
        {
            int n = s_plan.Length;
            int[] cnt = Samples(s_key);
            float prev = s_begin;
            bool prevSeen = true;
            for (int i = 0; i < n; i++)
            {
                float t = i == n - 1 ? now : s_markAt[(int)s_plan[i].EndMark];
                if (t < 0f) { prevSeen = false; continue; }   // merged into a later phase: neither is learned
                if (t < prev) t = prev;
                if (prevSeen) Learn(s_dur, cnt, i, t - prev);
                prev = t;
                prevSeen = true;
            }
        }

        static void Learn(float[] dur, int[] cnt, int i, float d)
        {
            float old = dur[i];
            d = Mathf.Clamp(d, 0f, Mathf.Max(old * 4f, old + 3f));
            float a = cnt[i] < 3 ? 1f / (cnt[i] + 2f) : 0.25f;   // the seed counts as one sample
            dur[i] = Mathf.Max(0.05f, old + a * (d - old));
            cnt[i]++;
        }

        /// "deal 4.6/4.6 load 0.4/0.4 ..." = measured / expected per phase ("-" = milestone not seen).
        static string Summary(float now)
        {
            var sb = new StringBuilder();
            float prev = s_begin;
            for (int i = 0; i < s_plan.Length; i++)
            {
                float t = i == s_plan.Length - 1 ? now : s_markAt[(int)s_plan[i].EndMark];
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(s_plan[i].Id).Append(' ');
                if (t < 0f) sb.Append('-');
                else { sb.Append(Mathf.Max(0f, t - prev).ToString("0.0", Inv)); prev = Mathf.Max(prev, t); }
                sb.Append('/').Append(s_dur[i].ToString("0.0", Inv));
            }
            return sb.ToString();
        }

        static float[] Durations(string key)
        {
            float[] d;
            if (s_learned.TryGetValue(key, out d)) return d;
            float[] seed;
            if (!s_seeds.TryGetValue(key, out seed) && !s_seeds.TryGetValue(BaseOf(key), out seed)) seed = s_seeds["other"];
            d = (float[])seed.Clone();
            s_learned[key] = d;
            s_samples[key] = new int[d.Length];
            return d;
        }

        static int[] Samples(string key)
        {
            Durations(key);
            return s_samples[key];
        }

        static string BaseOf(string key)
        {
            if (key == null) return "other";
            int i = key.IndexOf('+');
            return i < 0 ? key : key.Substring(0, i);
        }

        static float Sum(float[] a)
        {
            float s = 0f;
            for (int i = 0; i < a.Length; i++) s += a[i];
            return s;
        }

        // ---- persistence: BepInEx/config/hof2vr_loadtimes.txt -----------------------------------------

        /// The player's learned times live with their settings; test runs (the simulator's pacing differs) keep theirs
        /// in the test folder.
        static string FilePath => TestMode.Enabled ? Path.Combine(TestMode.Dir, "hof2vr_loadtimes.txt") : Path.Combine(BepInEx.Paths.ConfigPath, "hof2vr_loadtimes.txt");

        static void Load()
        {
            string path = FilePath;
            if (!File.Exists(path)) return;
            int n = 0;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] f = line.Split('\t');   // key, phase, seconds, samples
                if (f.Length < 4) continue;
                Ph[] plan;
                if (!s_plans.TryGetValue(BaseOf(f[0]), out plan)) continue;
                int i = -1;
                for (int k = 0; k < plan.Length; k++) if (plan[k].Id == f[1]) i = k;
                float v;
                int c;
                if (i < 0 || !float.TryParse(f[2], NumberStyles.Float, Inv, out v) || !int.TryParse(f[3], NumberStyles.Integer, Inv, out c)) continue;
                if (v < 0.05f || v > 300f) continue;
                Durations(f[0])[i] = v;
                s_samples[f[0]][i] = Mathf.Max(0, c);
                n++;
            }
            Plugin.L.LogInfo("progress: " + n + " learned phase durations from " + path);
        }

        static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("# Hand of Fate 2 VR: learned waiting times (seconds) per transition and phase, written by the mod.\n");
                sb.Append("# key<TAB>phase<TAB>seconds<TAB>samples - delete the file to go back to the built-in values.\n");
                foreach (KeyValuePair<string, float[]> kv in s_learned)
                {
                    int[] cnt = s_samples[kv.Key];
                    bool any = false;
                    for (int i = 0; i < cnt.Length; i++) if (cnt[i] > 0) any = true;
                    if (!any) continue;
                    Ph[] plan = s_plans[BaseOf(kv.Key)];
                    for (int i = 0; i < kv.Value.Length && i < plan.Length; i++)
                        sb.Append(kv.Key).Append('\t').Append(plan[i].Id).Append('\t').Append(kv.Value[i].ToString("0.###", Inv)).Append('\t').Append(cnt[i].ToString(Inv)).Append('\n');
                }
                string path = FilePath, tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e) { Plugin.L.LogWarning("progress: could not save " + FilePath + ": " + e.Message); }
        }

        // ---- hooks --------------------------------------------------------------------------------------

        public static void Apply(Harmony h)
        {
            Patch(h, AccessTools.Method(typeof(AssetBundleManager), "LoadLevelAsync", new[] { typeof(string), typeof(string), typeof(bool), typeof(bool) }), null, nameof(OnLoadLevelAsync));
            Patch(h, AccessTools.Method(typeof(AssetBundleManager), "LoadAllAssetsAsync"), null, nameof(OnLoadAllAssets));
            Patch(h, AccessTools.Method(typeof(Loader), "OnLoadingTransitionComplete"), null, nameof(OnLoaderReady));
            Patch(h, AccessTools.Method(typeof(StartScreenController), "Display"), null, nameof(OnTitleShown));
            Patch(h, AccessTools.Method(typeof(StartScreenController), "SetSequence"), null, nameof(OnTitleSequence));
            Patch(h, AccessTools.Method(typeof(MeshWormhole), "Setup"), null, nameof(OnWormholeSetup));
            Patch(h, AccessTools.Method(typeof(MeshWormhole), "StartTransitionIn"), nameof(OnTunnelIn), null);
            Patch(h, AccessTools.Method(typeof(MeshWormhole), "StartTransitionOut"), nameof(OnTunnelOut), null);
            Patch(h, AccessTools.Method(typeof(CardTableLoader), "SetScene"), null, nameof(OnSetScene));
            GameWatch.Changed += OnState;

            Diag.StatusExtras["progress"] = () => Diag.Q(s_active
                ? s_key + " " + PhaseId + " " + Mathf.RoundToInt(Value * 100f) + "% " + Elapsed.ToString("0.0", Inv) + "/" + Expected.ToString("0.0", Inv) + " s"
                : "-");
            // `progress [info|learned|reset|begin <key>|end [fail]]`
            Diag.Commands["progress"] = a =>
            {
                string what = a.Length > 1 ? a[1] : "info";
                if (what == "learned") return Dump();
                if (what == "reset")
                {
                    s_learned.Clear();
                    s_samples.Clear();
                    try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch (Exception) { }
                    return "learned durations reset";
                }
                if (what == "begin" && a.Length > 2) { Begin(a[2]); return "begun " + s_key; }
                if (what == "end") { End(!(a.Length > 2 && a[2] == "fail")); return "ended"; }
                return s_active
                    ? s_key + " phase " + PhaseId + " (" + Phase + ") " + (Value * 100f).ToString("0", Inv) + " % elapsed " + Elapsed.ToString("0.0", Inv) +
                      " s expected " + Expected.ToString("0.0", Inv) + " s remaining " + Remaining.ToString("0.0", Inv) + " s | " + Summary(Time.unscaledTime)
                    : "idle (last " + (s_key ?? "-") + ")";
            };
        }

        static void Patch(Harmony h, System.Reflection.MethodInfo m, string prefix, string postfix)
        {
            if (m == null) { Plugin.L.LogWarning("progress: hook " + (prefix ?? postfix) + " not found - skipped"); return; }
            try
            {
                h.Patch(m, prefix: prefix != null ? new HarmonyMethod(typeof(LoadProgress), prefix) : null,
                        postfix: postfix != null ? new HarmonyMethod(typeof(LoadProgress), postfix) : null);
            }
            catch (Exception e) { Plugin.L.LogWarning("progress: could not hook " + m.DeclaringType.Name + "." + m.Name + ": " + e.Message); }
        }

        static string Dump()
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, float[]> kv in s_learned)
            {
                Ph[] plan = s_plans[BaseOf(kv.Key)];
                sb.Append(kv.Key).Append(':');
                for (int i = 0; i < kv.Value.Length && i < plan.Length; i++)
                    sb.Append(' ').Append(plan[i].Id).Append(' ').Append(kv.Value[i].ToString("0.0", Inv)).Append(" (").Append(s_samples[kv.Key][i]).Append(')');
                sb.Append("; ");
            }
            return sb.Length > 0 ? sb.ToString() : "nothing learned yet (built-in values)";
        }

        static void OnState(string state)
        {
            if (state == "GameState_Init") Mark(M.InitEntered);
        }

        // H1: every scene load the game starts through its bundle manager
        static void OnLoadLevelAsync(AssetBundleLoadOperation __result, string a_levelName)
        {
            try
            {
                var op = __result as AssetBundleLoadLevelOperation;
                if (op == null) return;
                // the wait's first scene (later ones - an environment state's additive scene - do not replace it);
                // while idle the latest one is kept for a Begin in the same frame
                if (!s_active || s_firstOp == null)
                {
                    s_firstOp = op;
                    s_opName = a_levelName ?? "";
                }
                s_opAt = Time.unscaledTime;
                Mark(M.SceneLoadStart);
            }
            catch (Exception e) { Plugin.L.LogError("progress hook: " + e); }
        }

        // H2: AssetManager's asset libraries (boot)
        static void OnLoadAllAssets() { s_libLoads++; }

        // H3: the splashes are over, the boot scenes may activate
        static void OnLoaderReady(Loader __instance, bool a_showing)
        {
            if (!a_showing) return;
            s_loader = __instance;
            Mark(M.LoaderDone);
        }

        // H4, H5: the title scene is shown (flat game); its intro animation starts
        static void OnTitleShown() { Mark(M.TitleShown); }

        static void OnTitleSequence()
        {
            if (s_markAt[(int)M.TitleShown] >= 0f) Mark(M.TitleSequence);
        }

        // H6: a showcase intro puts a feature card into the tunnel (~4 s more)
        static void OnWormholeSetup(Card a_featureCard, MeshWormhole.WormholeDestination a_destination)
        {
            // (LoadSmooth skips the showcase card's animation in VR: then the plain fight_in plan fits)
            if (a_featureCard != null && a_destination == MeshWormhole.WormholeDestination.Combat && !FastLoad.SkipsShowcase && !(LoadSmooth.SkipShowcaseCard && UnityEngine.XR.XRSettings.enabled))
                s_featureAt = Time.unscaledTime;
        }

        // H7: the tunnel part of the trip begins
        static void OnTunnelIn() { Mark(M.TunnelIn); }

        // H8: the destination is built; its camera fades in at the end of this coroutine
        static void OnTunnelOut(CameraFade a_targetSceneCameraFade)
        {
            if (s_markAt[(int)M.TunnelOut] >= 0f) return;
            s_target = a_targetSceneCameraFade;
            Mark(M.TunnelOut);
        }

        // H9: a table variant swap (only when the scene really changes)
        static void OnSetScene(CardTableLoader __instance, CardTableLoader.CardTableSceneType a_sceneType)
        {
            try { if (__instance != null && __instance.SceneChangeRequired(a_sceneType)) s_swapAt = Time.unscaledTime; }
            catch (Exception) { }
        }
    }
}

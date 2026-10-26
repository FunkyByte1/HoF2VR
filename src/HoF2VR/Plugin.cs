using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "flat2vr.hof2vr";
        public const string Name = "Hand of Fate 2 VR";
        public const string Version = "0.1.0";

        internal static ManualLogSource L;
        internal static Plugin Instance;
        internal static Harmony H;
        internal static GameObject Root;

        void Awake()
        {
            Instance = this;
            // BaseUnityPlugin's constructor can fail half-way (Unity development players refuse
            // Application.isEditor there) and leave Logger/Config unset: never depend on them.
            L = Logger ?? BepInEx.Logging.Logger.CreateLogSource(Name);
            H = new Harmony(Guid);
            // Test isolation comes first. If it cannot be guaranteed for a test run, end the
            // process at once, before the game gets a chance to load or save a profile.
            try
            {
                TestMode.Init();
                Safety.Apply(H);
                Safety.Verify();
                SteamGuard.Apply(H);
            }
            catch (Exception e)
            {
                L.LogError("SAFETY SETUP FAILED: " + e);
                if (TestMode.Enabled || Environment.GetEnvironmentVariable("HOF2VR_TEST") == "1")
                {
                    L.LogError("test run without save isolation - killing the game");
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                }
            }
            Settings.Init(Config ?? new ConfigFile(Path.Combine(Paths.ConfigPath, Guid + ".cfg"), true));
            NarrationSettings.Init(Settings.Cfg);
            try
            {
                // the Scenarios' dev profile needs the save guards in normal play too (pass-through until it is in use)
                if (Settings.Scenarios) Safety.ApplyForScenarios(H);
            }
            catch (Exception e) { L.LogError("Scenarios: save guards failed - the Scenarios menu stays off: " + e); Scenarios.Broken = true; }
            ResetGuard.Apply(H);
            RenderFixes.Apply(H);
            StereoFog.Apply(H);
            StereoDarken.Apply(H);
            GameWatch.Apply(H);
            VRPad.Apply(H);
            VRPointer.Apply(H);
            CombatInput.Apply(H);
            CombatHud.Apply(H);
            GnomeFight.Apply(H);   // F8: the player's gnome fight
            PlayerInfo.Apply(H);
            VRHelp.Apply(H);
            UiAnchors.Apply(H);
            Places.Apply(H);
            TableView.Apply(H);
            CombatVR.Apply(H);
            DiceHand.Apply(H);
            TableStops.Apply(H);
            FightExit.Apply(H);
            LoadSmooth.Apply(H);
            LoadProgress.Init();
            LoadProgress.Apply(H);
            WaitGate.Apply(H);
            FightFlow.Apply(H);
            FightCard.Apply(H);
            VRAudio.Apply(H);
            CardLook.Apply(H);
            ChoiceZoom.Apply(H);
            EquipAsk.Apply(H);
            KeepOut.Apply(H);
            DealPath.Apply(H);
            SideTable.Apply(H);
            EnemyTuning.Apply(H);
            QuestPrompts.Apply(H);
            FastLoad.Apply(H);
            TableHud.Apply(H);
            Narrator.Apply(H);
            DealerLines.Apply(H);
            Hrtf.Apply(H);

            Root = new GameObject("HoF2VR");
            UnityEngine.Object.DontDestroyOnLoad(Root);
            Root.AddComponent<Diag>();
            Root.AddComponent<PerfMonitor>();
            Root.AddComponent<RenderFixes>();
            Root.AddComponent<VrGraphics>();
            Root.AddComponent<VRRig>();
            Root.AddComponent<VRCore>();
            Root.AddComponent<VRUI>();
            Root.AddComponent<VRPad>();
            Root.AddComponent<VRPointer>();
            Root.AddComponent<VRHands>();
            Root.AddComponent<VRFade>();
            Root.AddComponent<Limbo>();
            Root.AddComponent<Places>();
            Root.AddComponent<CombatVR>();
            Root.AddComponent<CombatInput>();
            Root.AddComponent<VRHelp>();
            Root.AddComponent<Comfort>();
            Root.AddComponent<VRAudio>();
            Root.AddComponent<Hrtf>();
            Root.AddComponent<HrtfProbe>();
            Root.AddComponent<Narrator>();
            Root.AddComponent<DealerMotion>();
            Root.AddComponent<EnemyBars>();
            Root.AddComponent<TerrainInfo>();
            Root.AddComponent<CullingGuard>();
            Root.AddComponent<PoseProbe>();
            Root.AddComponent<DiceHand>();
            Root.AddComponent<DiceReroll>();
            Root.AddComponent<TableStops>();
            Root.AddComponent<HandFeel>();
            Root.AddComponent<TableTop>();
            Root.AddComponent<CardGrab>();
            Root.AddComponent<TableCardGrab>();
            Root.AddComponent<TestBoard>();
            Root.AddComponent<HandCheck>();
            Root.AddComponent<TouchInput>();
            Root.AddComponent<FightExit>();
            Root.AddComponent<TableComfort>();
            Root.AddComponent<SideTable>();
            Root.AddComponent<InventoryLayers>();
            Root.AddComponent<TableBoards>();
            Root.AddComponent<VRSettings>();
            Root.AddComponent<FocusPause>();
            Root.AddComponent<LoadSmooth>();
            Root.AddComponent<WaitGate>();
            Root.AddComponent<FightCard>();
            Root.AddComponent<GoPlates>();
            Root.AddComponent<TurnLog>();
            Root.AddComponent<WheelSeat>();
            Root.AddComponent<WaitBoard>();
            Root.AddComponent<ThreatWarning>();
            Root.AddComponent<QuestPrompts>();
            Root.AddComponent<AutoShots>();
            Root.AddComponent<LumaProbe>();
            Root.AddComponent<TableHud>();
            Root.AddComponent<MapFocus>();
            Root.AddComponent<BugMark>();
            Root.AddComponent<CardLook>();
            Root.AddComponent<Proximity>();
            Root.AddComponent<Metronome>();
            Root.AddComponent<PendulumView>();
            Root.AddComponent<DeckRows>();
            Root.AddComponent<ChoiceZoom>();
            Root.AddComponent<KeepOut>();
            Root.AddComponent<FigurineGrab>();
            Diag.Commands["pad"] = VRInput.PadCommand;
            TestSeams.Register();
            Inspect.Register();
            Minigames.Register();
            TableGeo.Register();
            Measure.Register();
            HandSize.Register();
            PlayerInfo.Register();
            HandWorld.Register();
            ReachProbe.Register();
            MinigameSeams.Register();
            Feedback.Register();
            UpdateProfiler.Register();
            CamTimes.Register();
            WeaponSeams.Register();
            CombatMod.Init(H, Root, Settings.Cfg);   // the combat overhaul (env 2, branch combat); (Config can be unset: see above)
            Timeline.Register(H);
            TitleSpot.Register();
            DevJump.Register();
            DevChord.Register();
            Scenarios.Register();
            HubProbe.Register();
            Diag.StatusExtras["haptics"] = () => Diag.Q(Haptics.Describe());
            Diag.Commands["hapticlog"] = a => { Haptics.Trace = a.Length < 2 || a[1] != "off"; return "haptic log " + (Haptics.Trace ? "on" : "off"); };
            L.LogInfo($"{Name} {Version} loaded. test mode={TestMode.Enabled} dir={TestMode.Dir}");
            if (TestMode.FlagIgnored)
                L.LogWarning("a test flag file (" + TestMode.FlagFile + ") is in the game folder, but the test harness did not start this game: normal play, flag ignored");
        }
    }
}

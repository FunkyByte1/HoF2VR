using System;
using Defiant.Cameras;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Where the player stands outside fights. Every place is ONE fixed spot on the floor: the view
    /// never moves by itself while the place lasts, whatever the game's camera does (shot changes,
    /// the orbiting title camera, fly-throughs). Changing place - title to caravan, caravan to a fight
    /// and back - happens in the dark: fade out, wait until the new place exists, move, recenter on
    /// the player, fade in. Fights are first person (CombatVR drives the rig there).
    internal class Places : MonoBehaviour
    {
        public enum Place { None, Transition, Title, Caravan, Hub, Campfire, Other, Fight }

        public static Place Current = Place.None;
        /// In the Dealer's caravan: at the card table, or at the side table (Hub: challenge map,
        /// deck builder, boss loadout), which is behind the card-table spot.
        public static bool AtCaravan => Current == Place.Caravan || Current == Place.Hub;
        public const string HoldOwner = "place";
        const float ReadyTimeout = 12f;
        /// The title waits for its menu; the first start of the game can load for a long time.
        const float TitleTimeout = 25f, LoadingTimeout = 120f;

        Place m_target = Place.None;
        float m_blackSince = -1f;
        /// The current place was taken before it was ready (timeout): take it again, in the dark,
        /// as soon as it is ready.
        bool m_provisional;

        static readonly Func<Camera> s_tableCam = () =>
        {
            UICamera ui = UI3D.Instance != null ? UI3D.Instance.UICamera : null;
            return ui != null ? ui.cachedCamera : null;
        };

        static readonly AccessTools_Field<StartScreenController, Camera> s_startCam = new AccessTools_Field<StartScreenController, Camera>("m_startScreenCamera");
        static readonly AccessTools_Field<StartScreenController, bool> s_menuShown = new AccessTools_Field<StartScreenController, bool>("m_menuShown");

        /// The title scene's own camera (the 3D UI camera also renders for a moment at boot).
        static readonly Func<Camera> s_titleCam = () =>
        {
            StartScreenController s = StartScreenController.Instance;
            return s != null ? s_startCam.Get(s) : null;
        };

        static Func<Camera> PrimaryFor(Place p)
        {
            switch (p)
            {
                case Place.Caravan:
                case Place.Hub: return s_tableCam;
                case Place.Title: return s_titleCam;
                default: return null;
            }
        }

        public static void Apply(HarmonyLib.Harmony h)
        {
            // the tunnel's last stretch hangs on the camera (= the head) for several seconds after the
            // trip; in VR the trip happens in the dark, so it is never shown
            h.Patch(HarmonyLib.AccessTools.Method(typeof(MeshWormhole), "AttachCameraElement"),
                postfix: new HarmonyLib.HarmonyMethod(typeof(Places), nameof(HideCameraTunnel)));
            // ...and its first stretch (a scrolling ring on the head while the arena is still shown, at
            // the end of a fight or a campfire visit) is where the game starts to leave: go dark there
            h.Patch(HarmonyLib.AccessTools.Method(typeof(MeshWormhole), "DisplayPreWormholeMesh"),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Places), nameof(SkipPreTunnel)));
            // the fight camera fading out (to the tunnel's colour, or to black after the final battle)
            // is the game leaving the arena too
            h.Patch(HarmonyLib.AccessTools.Method(typeof(Defiant.PostProcess.CameraFade), "FadeToColor"),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Places), nameof(OnCameraFadeOut)));
            h.Patch(HarmonyLib.AccessTools.Method(typeof(CameraAnchor), "UpdatePosition"),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Places), nameof(AnchorPrefix)),
                postfix: new HarmonyLib.HarmonyMethod(typeof(Places), nameof(AnchorPostfix)));
            GameWatch.Changed += OnStateChanged;
            try
            {
                h.Patch(HarmonyLib.AccessTools.Method(typeof(FixedCamera), "LateUpdate"),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(Places), nameof(FixedCameraPostfix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("place: FixedCamera patch failed: " + e.Message); }
            // the room's realtime reflection probe is re-rendered a few frames after an environment
            // change (EnvironmentStateReflectionProbe.UpdateProbeAsync -> RenderProbe): a place is not
            // revealed until that has happened (a frame of the room once showed through the dark there)
            h.Patch(HarmonyLib.AccessTools.Method(typeof(Defiant.EnvironmentalStates.EnvironmentStateReflectionProbe), "UpdateProbe"),
                postfix: new HarmonyLib.HarmonyMethod(typeof(Places), nameof(OnProbeUpdate)));
            Camera.onPreCull += c =>
            {
                if (!IsProbeCamera(c)) return;
                if (s_probeRendered != Time.frameCount) PerfMonitor.Event("probe");
                s_probeRendered = Time.frameCount;
                s_probePending = -1;
            };
        }

        static int s_probePending = -1, s_probeRendered = -100;
        static readonly System.Collections.Generic.Dictionary<int, bool> s_probeCam = new System.Collections.Generic.Dictionary<int, bool>();

        /// The engine's reflection probe camera (by name, read once per camera: names allocate).
        static bool IsProbeCamera(Camera c)
        {
            int id = c.GetInstanceID();
            bool v;
            if (!s_probeCam.TryGetValue(id, out v)) s_probeCam[id] = v = c.name == "Reflection Probes Camera";
            return v;
        }
        const int ProbeWaitFrames = 30, ProbeSettleFrames = 3;

        static void OnProbeUpdate() { if (s_probePending < 0) s_probePending = Time.frameCount; }

        /// A reflection-probe refresh is due (requested, not yet rendered) or has just happened.
        static bool ProbeBusy()
        {
            if (s_probePending >= 0 && Time.frameCount - s_probePending > ProbeWaitFrames) s_probePending = -1;   // never came
            return s_probePending >= 0 || Time.frameCount - s_probeRendered < ProbeSettleFrames;
        }

        // ---- leaving a place ------------------------------------------------------------------
        // The game starts leaving a place a while before its state machine says so (the fight's exit
        // tunnel starts in Level_Out while the arena is still up; the title switches the table camera
        // on a frame before it changes state). From that moment the place is over: the view goes dark
        // and stays dark until the next place is ready.
        static Place s_leaving = Place.None;
        static float s_leavingSince;
        const float LeavingTimeout = 90f;

        public static void Depart(string why)
        {
            if (!XRSettings.enabled || s_leaving != Place.None) return;
            Place from = CombatVR.Active ? Place.Fight : Current;
            if (from == Place.None || from == Place.Transition) return;
            s_leaving = from;
            s_leavingSince = Time.unscaledTime;
            Plugin.L.LogInfo("place: leaving " + from + " (" + why + ", state " + GameWatch.State + ")");
            QueueShots("leave_" + from);
        }

        static bool SkipPreTunnel(ref System.Collections.IEnumerator __result)
        {
            if (!XRSettings.enabled) return true;
            Depart("exit tunnel");
            __result = Nothing();
            return false;
        }

        static System.Collections.IEnumerator Nothing() { yield break; }

        static void OnCameraFadeOut(Defiant.PostProcess.CameraFade __instance, float a_time)
        {
            if (!XRSettings.enabled || a_time <= 0f || !GameWatch.State.EndsWith("_Out")) return;
            PlayerCamera pc = PlayerCamera.Instance;
            if (pc != null && __instance == pc.CameraFadeComponent) Depart("fight camera fades out");
        }

        static void OnStateChanged(string state) => QueueShots(state.Replace("GameState_", ""));

        // The game pins some 3D objects to a point of a camera's screen (CameraAnchor): the title logo
        // (the start menu hangs below it), and at the caravan the encounter card, event cards and the
        // targets cards fly to. At a station the view no longer follows the game's cameras, so:
        //  * title: the logo is placed in front of the player's neutral view;
        //  * caravan: the maths runs on the flat screen at the player's screen (TableView), so the
        //    cards line up with the 2D panel as on a monitor.
        static Vector3 s_anchorCamPos;
        static Quaternion s_anchorCamRot;
        static Camera s_anchorCam;

        /// __state: 0 untouched, 1 camera posed at the neutral view, 2 camera swapped for the screen
        static void AnchorPrefix(ref Camera ___m_camera, out int __state)
        {
            __state = 0;
            if (!XRSettings.enabled || ___m_camera == null || VRRig.I == null || VRRig.I.Mode != RigMode.Station || !VRRig.I.HasStation) return;
            if (___m_camera.orthographic || ___m_camera.targetTexture != null) return;
            if (AtCaravan && TableView.On)
            {
                Camera screen = TableView.ScreenCamera();
                if (screen == null) return;
                s_anchorCam = ___m_camera;
                ___m_camera = screen;
                __state = 2;
                return;
            }
            if (Current != Place.Title || ___m_camera.stereoTargetEye == StereoTargetEyeMask.None) return;
            // title: the logo (the 2D menu hangs below it) is pinned to a point of the menu panel's flat screen,
            // as on a monitor. (Posed at the neutral view but with the headset's ~100 degree field of view it
            // landed at the panel's right edge, and the menu with it.)
            Camera menu = TableView.MenuScreenCamera();
            if (menu != null)
            {
                s_anchorCam = ___m_camera;
                ___m_camera = menu;
                __state = 2;
                return;
            }
            Transform t = ___m_camera.transform;
            s_anchorCamPos = t.position;
            s_anchorCamRot = t.rotation;
            t.SetPositionAndRotation(VRRig.I.NeutralEye, VRRig.I.NeutralRot);
            __state = 1;
        }

        static void AnchorPostfix(ref Camera ___m_camera, int __state)
        {
            if (__state == 2) ___m_camera = s_anchorCam;
            else if (__state == 1 && ___m_camera != null) ___m_camera.transform.SetPositionAndRotation(s_anchorCamPos, s_anchorCamRot);
        }

        static Places I;

        void Awake()
        {
            I = this;
            s_appliedSeated = Settings.Seated;
            s_appliedTable = Settings.SeatedTableHeight;
            s_appliedView = Settings.SeatedTableView;
            Diag.StatusExtras["place"] = () => Diag.Q(Current + (m_target != Current ? " -> " + m_target : "") + (s_leaving != Place.None ? " (leaving " + s_leaving + ")" : ""));
            Diag.Commands["relshot"] = a => { s_relShot = a.Length > 1 && a[1] != "off" ? a[1] : null; return "relshot " + (s_relShot ?? "off"); };
            // `stateshot <tag>|off`: eye captures at every game state change and every departure (the
            // frame it happens and a few after), to check what a transition shows
            Diag.Commands["stateshot"] = a => { s_stateShot = a.Length > 1 && a[1] != "off" ? a[1] : null; return "stateshot " + (s_stateShot ?? "off"); };
            // `posture seated|standing|table <m>|view table|lifted`: as the hand check's / settings page's buttons (this run only
            // in tests)
            Diag.Commands["posture"] = a =>
            {
                if (a.Length > 1 && (a[1] == "seated" || a[1] == "standing")) { Settings.SetSeated(a[1] == "seated"); PostureChanged("seam"); }
                else if (a.Length > 2 && a[1] == "view" && (a[2] == "table" || a[2] == "lifted"))
                {
                    Settings.SetSeatedTableView(a[2] == "lifted" ? Settings.TableView.Lifted : Settings.TableView.Table);
                    PostureChanged("seam: view");
                }
                else if (a.Length > 2 && a[1] == "table")
                {
                    float m;
                    if (float.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out m))
                    {
                        Settings.StepSeatedTable(m - Settings.SeatedTableHeight);
                        PostureChanged("seam: table");
                    }
                }
                // eye = the neutral eye height (from the last recentre); head = where the head is now, above the place's floor
                string head = VRRig.I != null ? ((VRRig.I.HeadWorldPos.y - VRRig.I.AnchorPos.y) / VRRig.I.Scale).ToString("0.000") : "-";
                return (Settings.Seated ? "seated" : "standing") + " | " + VRRig.PostureNote + " | eye " + (VRRig.I != null ? VRRig.I.EyeHeight.ToString("0.000") : "-") + " head " + head +
                       " | table " + Settings.SeatedTableHeight.ToString("0.000") + " view " + Settings.SeatedTableView + " top " + VRRig.SeatedTableTop().ToString("0.000") +
                       " seated eyes " + (float.IsNaN(VRRig.SeatedHead) ? "-" : VRRig.SeatedHead.ToString("0.000")) + " | busy " + PostureBusy + " | changes " + PostureChanges;
            };
            // `playersize [<v>|cfg]`: the player's size at the tables for this run only (Settings.PlayerSize; never saved); at a
            // table the place is taken again in the dark at once (the reach checks at two sizes in one session)
            Diag.Commands["playersize"] = a =>
            {
                if (a.Length > 1)
                {
                    Settings.LivePlayerSize = a[1] == "cfg" ? float.NaN : float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture);
                    if (Current == Place.Caravan || Current == Place.Hub) s_redoWhy = "player size " + Settings.PlayerSize.ToString("0.00");
                }
                return "player size " + Settings.PlayerSize.ToString("0.00") + (float.IsNaN(Settings.LivePlayerSize) ? " (cfg)" : " (this run)") + ", table scale " +
                       Settings.TableScale.ToString("0.000") + ", rig " + (VRRig.I != null ? VRRig.I.Scale.ToString("0.000") : "-") + (s_redoWhy != null ? ", taking the place again" : "");
            };
            Diag.Commands["place"] = a =>
            {
                if (VRRig.I == null) return "no rig";
                return "place " + Current + " station " + Diag.V(VRRig.I.StationFloor) + " yaw " + VRRig.I.StationYaw.ToString("0") +
                       " scale " + VRRig.I.Scale.ToString("0.00") + " eye " + VRRig.I.EyeHeight.ToString("0.00");
            };
        }

        static bool TableCamOn()
        {
            Camera c = s_tableCam();
            return c != null && c.isActiveAndEnabled;
        }

        static bool PlayerCamOn()
        {
            PlayerCamera pc = PlayerCamera.Instance;
            return pc != null && pc.Camera != null && pc.Camera.isActiveAndEnabled;
        }

        /// Which place the game is in now (Transition = between places: stay dark). A place the game has
        /// started to leave is not wanted any more.
        static Place Wanted()
        {
            Place p = GameWants();
            if (s_leaving != Place.None)
            {
                if (p == s_leaving) return Place.Transition;
                if (Time.unscaledTime - s_leavingSince > LeavingTimeout)
                {
                    Plugin.L.LogWarning("place: still leaving " + s_leaving + " after " + LeavingTimeout + " s - giving up");
                    s_leaving = Place.None;
                }
            }
            return p;
        }

        static Place GameWants()
        {
            if (CombatVR.Active)
            {
                // the game has switched back to the table camera: the fight is over
                if (TableCamOn() && GameWatch.State.EndsWith("_Out")) return Place.Transition;
                return Place.Fight;
            }
            switch (GameWatch.State)
            {
                case "":
                case "GameState_Profile":       // the title scene unloads, the caravan loads
                    return Place.Transition;
                case "GameState_Init":
                case "GameState_StartScreen":
                    return Place.Title;
                case "GameState_Level_In":      // the dive into the card and the tunnel
                    // B1: the player stays at the table for the Dealer's call to battle until they touch the encounter card
                    return FightCard.AtTable ? TablePlace() : Place.Transition;
                case "GameState_BossBattle_In":
                case "GameState_Customisation_Out":
                    return Place.Transition;
                case "GameState_Level_Out":     // the tunnel back, then the caravan again
                case "GameState_BossBattle_Out":
                    return TableCamOn() && !PlayerCamOn() && Time.unscaledTime - GameWatch.StateTime > 1f ? TablePlace() : Place.Transition;
                case "GameState_Customisation_In":
                    return Place.Transition;
                case "GameState_Customisation":
                    return Place.Campfire;
                case "GameState_Level_Play":
                case "GameState_Level_Pause":
                    return Place.Transition;    // CombatVR takes over as soon as the hero exists
                default:
                    return TableCamOn() ? TablePlace() : Place.Other;
            }
        }

        /// The game's shots of the side table (challenge map, deck builder, boss loadout). The side
        /// table is ~120 degrees behind the card-table spot, so it gets a spot of its own.
        public static bool HubShot()
        {
            CardCamera cc = CardCamera.Instance;
            if (cc == null) return false;
            var top = cc.TopCamera as FixedCamera;
            return top != null && (top == cc.ChallengeMapCamera || top == cc.DeckBuilderCamera || top == cc.DeckBuilderBuildCamera || top == cc.DeckBuilderFillCamera);
        }

        static bool s_hubSide;
        static float s_sideSince = -1f;
        const float SideSettle = 0.35f;

        /// Card table or side table: follows the game's shot, once it has settled (a shot pushed for a
        /// moment must not cost two fades).
        static Place TablePlace()
        {
            // the challenge map is chosen from the card table (Table.MapFromCardTable, MapFocus)
            bool hub = HubShot() && !MapFocus.MapShotAtTable();
            if (Current != Place.Caravan && Current != Place.Hub) { s_hubSide = hub; s_sideSince = -1f; }
            else if (hub != s_hubSide)
            {
                if (s_sideSince < 0f) s_sideSince = Time.unscaledTime;
                if (Time.unscaledTime - s_sideSince >= SideSettle) { s_hubSide = hub; s_sideSince = -1f; }
            }
            else s_sideSince = -1f;
            return s_hubSide ? Place.Hub : Place.Caravan;
        }

        static bool Ready(Place p)
        {
            Camera prim = VRRig.I.PrimaryCamera;
            switch (p)
            {
                case Place.Title:
                    // the start camera flies in from its loading pose; take the spot once it has
                    // arrived and the menu is up (the logo and menu are placed from that pose)
                    StartScreenController ssc = StartScreenController.Instance;
                    Camera tc = s_titleCam();
                    return ssc != null && tc != null && tc.isActiveAndEnabled && GameWatch.State == "GameState_StartScreen" && s_menuShown.Get(ssc);
                case Place.Caravan:
                case Place.Hub:
                    // after a fight the table camera is back before the room scene is reloaded
                    return TableCamOn() && Dealer.Instance != null && CardCamera.Instance != null && RoomLoaded();
                case Place.Campfire:
                    return prim != null && PlayerCamOn() && !PlayerCameraOverridden();
                default:
                    return prim != null;
            }
        }

        const string TableColliderPath = "Models/Dealer_Caravan/dealer_caravan_cardtable/Table/Collider";

        /// The Dealer's room scene (whichever variant the game uses now) is loaded and activated.
        static bool RoomLoaded()
        {
            CardTableLoader loader = CardTableLoader.Instance;
            if (loader == null || loader.CardTableScene == null) return GameObject.Find(TableColliderPath) != null;
            string name;
            try { name = loader.CardTableScene.SceneName; }
            catch (Exception) { return GameObject.Find(TableColliderPath) != null; }
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                UnityEngine.SceneManagement.Scene s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (s.name == name) return s.isLoaded;
            }
            return false;
        }

        static readonly AccessTools_Field<PlayerCamera, ICameraProperties> s_override = new AccessTools_Field<PlayerCamera, ICameraProperties>("m_overrideCamera");

        static bool PlayerCameraOverridden()
        {
            PlayerCamera pc = PlayerCamera.Instance;
            return pc != null && s_override.Get(pc) != null;
        }

        void LateUpdate()
        {
            if (!XRSettings.enabled || VRRig.I == null || VRFade.I == null) return;
            TailTick();
            RunQueuedShots();
            KeepTunnelCameraOff();
            GateTableCamera();
            Place want = Wanted();
            // stuck in the dark for too long: show what the game shows until it moves on
            if (want == Place.Transition && m_gaveUpAt >= 0f && GameWatch.StateTime <= m_gaveUpAt) want = Place.Other;
            else if (want != Place.Transition) m_gaveUpAt = -1f;
            if (want == Place.Fight)
            {
                // CombatVR has placed the player on the hero (in the dark); show the arena once the
                // tunnel has gone (it hangs on the camera, i.e. on the head, while it fades)
                if (Current != Place.Fight) Plugin.L.LogInfo("place: " + Current + " -> Fight");
                if (Current != Place.Fight) m_arrivedAt = Time.unscaledTime;
                Current = Place.Fight;
                m_target = Place.Fight;
                m_provisional = false;
                if (s_leaving != Place.None && s_leaving != Place.Fight) s_leaving = Place.None;
                if (VRRig.PreferredPrimary == s_tableCam || VRRig.PreferredPrimary == s_titleCam) VRRig.PreferredPrimary = null;
                if (VRFade.I.IsHeldBy(HoldOwner) && TunnelBusy(Place.Fight) && Time.unscaledTime - m_arrivedAt < TunnelTimeout) return;
                ReleaseHold();
                return;
            }
            // the camera the place is about (between places: none until the next place is known)
            Func<Camera> pref = PrimaryFor(want);
            if (pref != null) VRRig.PreferredPrimary = pref;
            else if (VRRig.PreferredPrimary == s_tableCam || VRRig.PreferredPrimary == s_titleCam) VRRig.PreferredPrimary = null;

            // a place taken before it was ready is taken again (in the dark) once it is; so is the place after a posture change
            // (a posture change waiting for a place the game is leaving is moot: the next place takes the posture anyway)
            if (s_redoWhy != null && want != Current) { Plugin.L.LogInfo("place: the posture redo (" + s_redoWhy + ") goes with the move to " + want); s_redoWhy = null; }
            bool redo = (m_provisional || s_redoWhy != null) && want == Current && Current != Place.Transition && Ready(want);
            if (want == Current && Current != Place.Transition && !redo)
            {
                if (VRFade.I.IsHeldBy(HoldOwner) && (!TunnelBusy(Current) || Time.unscaledTime - m_arrivedAt >= TunnelTimeout)) ReleaseHold();
                return;
            }
            // (a redo starts once: its settle frames must not start over every frame - it never arrived before 2026-10-13)
            if (want != m_target || redo && !m_redoing)
            {
                if (want != m_target && want != Place.Transition) WaitGate.Retarget(want.ToString());
                m_target = want;
                m_readyFrame = -1;
                m_redoing = redo;
                Plugin.L.LogInfo("place: " + Current + " -> " + want + (redo ? (s_redoWhy != null ? " (again: " + s_redoWhy + ")" : " (now ready)") : "") + " (state " + GameWatch.State + ")");
            }
            // leave the current place in the dark
            VRFade.I.Hold(HoldOwner);
            if (!VRFade.IsBlack) { m_blackSince = -1f; return; }
            if (m_blackSince < 0f) m_blackSince = Time.unscaledTime;
            if (want == Place.Transition)
            {
                if (Current != Place.Transition) { Current = Place.Transition; m_transitionSince = Time.unscaledTime; }
                string st = GameWatch.State;
                float limit = st == "" || st == "GameState_Init" ? LoadingTimeout : TransitionTimeout;
                if (Time.unscaledTime - m_transitionSince > limit && Time.unscaledTime - GameWatch.StateTime > limit)
                {
                    Plugin.L.LogWarning("place: dark for " + limit + " s in " + st + " - showing what the game shows");
                    m_gaveUpAt = Time.unscaledTime;
                }
                return;
            }
            bool ready = Ready(want);
            float timeout = Timeout(want);
            float waited = Time.unscaledTime - Mathf.Max(m_blackSince, GameWatch.StateTime);
            if (!ready) m_readyFrame = -1;
            else if (m_readyFrame < 0) m_readyFrame = Time.frameCount;
            if (!ready && waited < timeout) return;
            // let the new place render a couple of frames before the player is put there (the engine
            // rebuilds render targets as a place appears)
            if (ready && Time.frameCount - m_readyFrame < SettleFrames) return;
            if (!ready) Plugin.L.LogWarning("place: " + want + " not ready after " + timeout + " s in " + GameWatch.State + " - placing for now");
            if (Arrive(want))
            {
                if (s_redoWhy != null && want == Current)
                {
                    Plugin.L.LogInfo("place: " + want + " taken again for the posture (" + s_redoWhy + ")");
                    PostureBusyUntil = Time.unscaledTime + 1.5f;   // the fade back in
                    PostureApplied?.Invoke();
                }
                s_redoWhy = null;
                m_redoing = false;
                Current = want;
                m_provisional = !ready;
                m_arrivedAt = Time.unscaledTime;
                if (s_leaving != Place.None && s_leaving != want)
                {
                    Plugin.L.LogInfo("place: left " + s_leaving + " after " + (Time.unscaledTime - s_leavingSince).ToString("0.0") + " s");
                    s_leaving = Place.None;
                }
                if (VRUI.I != null) VRUI.I.SnapPanel();
                QueueShots("arrive_" + want);
            }
        }

        float m_arrivedAt;
        bool m_probeLogged, m_redoing;
        public const string TailOwner = "fight return";
        public static float ExitTail = 0.6f;
        float m_tailUntil = -1f;
        int m_tailMapFrame = -1;

        /// The fight's return tail (ReleaseHold): black until the map's state has been in for two frames, at most ExitTail.
        void TailTick()
        {
            if (VRFade.I == null || !VRFade.I.IsHeldBy(TailOwner)) { m_tailMapFrame = -1; return; }
            bool mapIn = GameWatch.State != "GameState_Level_Out";
            if (mapIn && m_tailMapFrame < 0) m_tailMapFrame = Time.frameCount;
            if (mapIn && Time.frameCount - m_tailMapFrame >= 2 || Time.unscaledTime >= m_tailUntil)
            {
                VRFade.I.Release(TailOwner);
                Plugin.L.LogInfo("place: the fight's return tail ends (" + (mapIn ? "map in" : "time") + ")");
                m_tailMapFrame = -1;
            }
        }
        int m_readyFrame = -1;
        const int SettleFrames = 2;
        float m_transitionSince;
        float m_gaveUpAt = -1f;
        const float TunnelTimeout = 6f, TransitionTimeout = 60f;

        // The table <-> fight tunnel is shown in the dark in VR (and its camera, which the game hangs on
        // the 3D UI rig, would only re-draw the player's surroundings): it never renders.
        static bool s_tunnelCamLogged;

        static void KeepTunnelCameraOff()
        {
            MeshWormhole w = UI3D.Instance != null ? UI3D.Instance.Wormhole : null;
            Camera wc = w != null ? s_tunnelCam.Get(w) : null;
            if (wc == null || !wc.enabled) return;
            wc.enabled = false;
            if (!s_tunnelCamLogged) { s_tunnelCamLogged = true; Plugin.L.LogInfo("place: the tunnel camera stays off in VR"); }
        }

        // The game switches the table camera on a frame before it leaves the title. It would paint bare
        // sky over the title (it looks at the Dealer's room from the title spot) while the view fades
        // out, so it stays off until the view is dark.
        static bool s_tableCamGated;

        static void GateTableCamera()
        {
            Camera tc = s_tableCam();
            if (Current == Place.Title && tc != null && tc.enabled && !VRFade.IsBlack)
            {
                tc.enabled = false;
                if (!s_tableCamGated) Depart("the table camera is switched on");
                s_tableCamGated = true;
            }
            else if (s_tableCamGated && (Current != Place.Title || VRFade.IsBlack))
            {
                s_tableCamGated = false;
                if (tc != null) tc.enabled = true;
            }
        }

        /// After arriving: lift the dark once the trip's tunnel has gone.
        void ReleaseHold()
        {
            if (!VRFade.I.IsHeldBy(HoldOwner)) return;
            if (ProbeBusy())
            {
                if (!m_probeLogged) { m_probeLogged = true; Plugin.L.LogInfo("place: waiting for the room's reflection probe refresh"); }
                return;
            }
            m_probeLogged = false;
            // the waiting space waits for the player's A once the place is ready (Settings.PressToContinue)
            if (!WaitGate.MayRelease()) return;
            // back from a fight the game ends its return (GameState_Level_Out -> Map) a moment after this, with a long frame
            // (80 ms in the round-5 tour, 0.18 s after the release, at 68 % of the fade): the view stays black a little longer
            // under its own hold (TailOwner), until the map's state is in or ExitTail has passed. (Holding the place itself
            // until then held the game's return too: +1.5 s of black every time.)
            if (Current == Place.Caravan && GameWatch.State == "GameState_Level_Out")
            {
                VRFade.I.Hold(TailOwner);
                m_tailUntil = Time.unscaledTime + ExitTail;
            }
            float waited = Time.unscaledTime - m_arrivedAt;
            if (waited > 0.05f) Plugin.L.LogInfo("place: waited " + waited.ToString("0.0") + " s in the dark for the tunnel to fade");
            VRFade.I.Release(HoldOwner);
            if (s_relShot != null && Diag.I != null) StartCoroutine(ReleaseShots(s_relShot + "_" + (++s_relShotN) + "_" + Current));
        }

        // test hook `relshot <tag>|off`: eye captures right after every place is revealed
        static string s_relShot;
        static int s_relShotN;
        static string s_stateShot;
        static int s_stateShotN;

        static readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<float, string>> s_shots =
            new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<float, string>>();
        static int s_nextShotFrame;

        /// `stateshot` on: the same captures for another moment (FightCard: the touch, the way out of the table).
        public static void TestShots(string what) => QueueShots(what);

        /// Eye captures the frame it happens and 0.15 / 0.4 s later (one at a time: captures must not overlap).
        static void QueueShots(string what)
        {
            if (s_stateShot == null) return;
            Diag.CamLogFrames = Math.Max(Diag.CamLogFrames, 8);
            string name = s_stateShot + "_" + (++s_stateShotN) + "_" + what;
            float t0 = Time.unscaledTime;
            foreach (float d in new[] { 0f, 0.15f, 0.4f })
                s_shots.Add(new System.Collections.Generic.KeyValuePair<float, string>(t0 + d, name + "_" + Mathf.RoundToInt(d * 100)));
        }

        static void RunQueuedShots()
        {
            if (s_shots.Count == 0 || Diag.I == null || Time.frameCount < s_nextShotFrame || Time.unscaledTime < s_shots[0].Key) return;
            Diag.I.Run("shot " + s_shots[0].Value);
            s_shots.RemoveAt(0);
            s_nextShotFrame = Time.frameCount + 3;
        }

        System.Collections.IEnumerator ReleaseShots(string name)
        {
            float t0 = Time.unscaledTime;
            foreach (float d in new[] { 0f, 0.3f, 0.6f, 1.2f, 2.4f })
            {
                while (Time.unscaledTime - t0 < d) yield return null;
                Diag.I.Run("shot " + name + "_" + Mathf.RoundToInt(d * 10));
            }
        }

        /// The table <-> fight tunnel (MeshWormhole) is still showing, or the destination camera is
        /// still fading in from the tunnel's colour. Its last part hangs on the camera, i.e. on the
        /// player's head: keep the place dark until it is over.
        static readonly AccessTools_Field<MeshWormhole, Transform> s_tunnelAttach = new AccessTools_Field<MeshWormhole, Transform>("m_cameraAttach");
        static readonly AccessTools_Field<MeshWormhole, Camera> s_tunnelCam = new AccessTools_Field<MeshWormhole, Camera>("m_camera");

        static void HideCameraTunnel(MeshWormhole __instance)
        {
            if (!XRSettings.enabled) return;
            Transform t = s_tunnelAttach.Get(__instance);
            if (t != null) t.gameObject.SetActive(false);
        }

        static readonly AccessTools_Field<MeshWormhole, bool> s_tunnelAnimating = new AccessTools_Field<MeshWormhole, bool>("mWormholeAnimating");

        static bool TunnelBusy(Place p)
        {
            MeshWormhole w = UI3D.Instance != null ? UI3D.Instance.Wormhole : null;
            if (w != null && w.gameObject.activeInHierarchy)
            {
                // the trip runs from the departure (TransitionUp) to the end of the arrival
                // (StartTransitionOut); its camera never renders in VR, so it says nothing
                Transform attach = s_tunnelAttach.Get(w);
                if (s_tunnelAnimating.Get(w) || (attach != null && attach.gameObject.activeInHierarchy)) return true;
            }
            Defiant.PostProcess.CameraFade fade = null;
            if (p == Place.Fight) { PlayerCamera pc = PlayerCamera.Instance; fade = pc != null ? pc.CameraFadeComponent : null; }
            else if (p == Place.Caravan || p == Place.Hub) fade = UI3D.Instance != null ? UI3D.Instance.UICameraFade : null;
            return fade != null && fade.isActiveAndEnabled && fade.IsFading;
        }

        /// How long a place may take to get ready before the player is put there anyway (and moved
        /// again, in the dark, once it is ready).
        static float Timeout(Place p)
        {
            string st = GameWatch.State;
            if (st == "" || st == "GameState_Init") return LoadingTimeout;   // the game is still loading
            return p == Place.Title ? TitleTimeout : ReadyTimeout;
        }

        // ---- posture (Standing / Seated) ----------------------------------------------------------------------------------
        static string s_redoWhy;
        /// Until then a posture change is under way (its short dark): the hand check and the VR settings page stay open.
        public static float PostureBusyUntil = -1f;
        public static bool PostureBusy => s_redoWhy != null || Time.unscaledTime < PostureBusyUntil;
        /// After a posture change has placed the player again (boards placed once in front of the player move with it).
        public static event Action PostureApplied;
        public static int PostureChanges;

        /// The posture, the seated table's height or the seated view at the tables changed (Settings.Seated, SeatedTableHeight,
        /// SeatedTableView): at a place, the player is placed there again in the dark (the spot and the height follow the
        /// posture); in a fight or between places only the lift changes, at a short fade. Never moves anyone without their
        /// input: this is the player's own button.
        public static void PostureChanged(string why)
        {
            bool seated = Settings.Seated;
            float table = Settings.SeatedTableHeight;
            Settings.TableView view = Settings.SeatedTableView;
            bool turned = seated != s_appliedSeated, nudged = Mathf.Abs(table - s_appliedTable) > 1e-4f || view != s_appliedView;
            s_appliedSeated = seated;
            s_appliedTable = table;
            s_appliedView = view;
            if (turned) VRRig.PostureReset();   // the seated eyes taken again when the place is taken again (or the lift changes)
            if (!turned && (!nudged || !seated)) return;   // nothing the player would see changed
            if (!XRSettings.enabled || VRRig.I == null || VRFade.I == null) return;
            PostureChanges++;
            Plugin.L.LogInfo("place: posture " + (seated ? "seated" : "standing") + ", table " + table.ToString("0.00") + " m, view " + view + " (" + why + ") at " + Current);
            if (!turned)
            {
                // the seated table's height nudge or the view at the tables: only the lift, at a blink (the player's own press,
                // as a snap turn's blink)
                if (float.IsNaN(VRRig.I.SeatedSurface)) return;   // not at a table now: it applies at the next one
                PostureBusyUntil = Time.unscaledTime + 1f;
                VRFade.I.QuickBlink(() => { VRRig.I.Relift(why); PostureApplied?.Invoke(); });
                return;
            }
            PostureBusyUntil = Time.unscaledTime + 2.5f;
            bool place = !CombatVR.Active && (Current == Place.Title || Current == Place.Caravan || Current == Place.Hub || Current == Place.Campfire || Current == Place.Other) &&
                         GameWatch.State != "GameState_Level_In" && GameWatch.State != "GameState_BossBattle_In" && !Limbo.On;
            if (place) { s_redoWhy = why; return; }
            VRFade.I.FadeSnap(() => { VRRig.I.Relift(why); PostureBusyUntil = Time.unscaledTime + 1f; PostureApplied?.Invoke(); }, "posture: " + why);
        }

        static bool s_appliedSeated;
        static float s_appliedTable = -1f;
        static Settings.TableView s_appliedView;

        /// Seated play: the height (real metres above the room's floor) of what the player sits at - the card table's top, or
        /// the challenge map on the side table (the middle of its relief, where the pins stand). Measured once per scene.
        public static float SeatedSurface(Place p)
        {
            Dealer dealer = Dealer.Instance;
            if (dealer == null) return p == Place.Hub ? SideTableFallback : CardTableFallback;
            float floorY = dealer.transform.position.y, sc = Settings.TableScale;
            if (p == Place.Caravan)
            {
                Bounds b;
                return ReachProbe.TableBounds(out b) ? (b.max.y - floorY) / sc : CardTableFallback;
            }
            if (s_mapBoardScene != SceneScan.Version || float.IsNaN(s_mapBoardY))
            {
                s_mapBoardScene = SceneScan.Version;
                s_mapBoardY = float.NaN;
                GameObject board = GameObject.Find(MapBoardPath);
                if (board != null)
                {
                    bool any = false;
                    Bounds m = default(Bounds);
                    foreach (Renderer r in board.GetComponentsInChildren<Renderer>())
                    {
                        if (r == null || !r.enabled) continue;
                        if (!any) { m = r.bounds; any = true; } else m.Encapsulate(r.bounds);
                    }
                    if (any) s_mapBoardY = (m.min.y + m.max.y) * 0.5f;
                }
            }
            return float.IsNaN(s_mapBoardY) ? SideTableFallback : (s_mapBoardY - floorY) / sc;
        }

        const string MapBoardPath = "Models/Caravan_Map/Map_Pivot/MapBoard";
        /// (measured 2026-10-11/13: the card table's top 0.93 m, the map's relief 0.81 m above the floor at the room's scale)
        const float CardTableFallback = 0.93f, SideTableFallback = 0.81f;
        static float s_mapBoardY = float.NaN;
        static int s_mapBoardScene = -1;

        /// Put the player at the new place (the screen is black). False = cannot yet (no camera).
        static bool Arrive(Place p)
        {
            VRRig rig = VRRig.I;
            rig.Recenter(Settings.Seated && (p == Place.Caravan || p == Place.Hub) ? SeatedSurface(p) : float.NaN, true);
            switch (p)
            {
                case Place.Caravan:
                case Place.Hub:
                    Vector3 floor;
                    float yaw;
                    if (p == Place.Hub ? HubSpot(out floor, out yaw) : CaravanSpot(out floor, out yaw))
                    {
                        rig.SetStation(floor, yaw, Settings.TableScale, p == Place.Hub ? "caravan side table" : "caravan");
                        return true;
                    }
                    break;
            }
            // somewhere the game frames with its own camera (title, campfire, anything unknown): stand
            // where that camera is, on the ground below it if there is one, facing its way. The
            // place's own camera if it has one (its transform holds the game's pose outside rendering).
            Func<Camera> pf = PrimaryFor(p);
            Camera cam = pf != null ? pf() : null;
            Vector3 eye;
            Quaternion rot;
            if (cam != null && cam.isActiveAndEnabled)
            {
                eye = cam.transform.position;
                rot = cam.transform.rotation;
            }
            else
            {
                cam = rig.PrimaryCamera;
                if (cam == null) return false;
                eye = rig.GameCamPos;
                rot = rig.GameCamRot;
            }
            Vector3 ground;
            if (p == Place.Title && TitleSpot.Enabled)
            {
                // beside the hero in the clearing, facing the campfire, on levelled ground (TitleSpot)
                Vector3 tf;
                float ty;
                string why;
                if (TitleSpot.Spot(eye, out tf, out ty, out why))
                {
                    rig.SetStation(tf, ty, 1f, "Title (" + why + ")");
                    HideNearMoons(eye);
                    return true;
                }
                Plugin.L.LogWarning("place: title spot: " + why + " - the camera's spot instead");
            }
            Vector3 f = GroundBelow(eye, out ground) ? ground : rig.FloorBelowEye(eye);
            string flat = "";
            if (p == Place.Title && f == ground && Settings.FlatGround)
            {
                // the title camera stands on a steep hillside: the nearest level ground (a terrace about 1 m ahead)
                Vector2 spot;
                float h;
                string why;
                if (FlatSpot.Find(new Vector2(eye.x, eye.z), rot.eulerAngles.y, FlatSpot.RayHeight(eye.y + 1.5f, 6f, ~0), out spot, out h, out why))
                    f = new Vector3(spot.x, h, spot.y);
                flat = ", flat ground: " + why;
            }
            rig.SetStation(f, rot.eulerAngles.y, 1f, p + " (camera '" + cam.name + "' at " + Diag.V(eye) + (f == ground ? ", ground " + (eye.y - ground.y).ToString("0.00") + " m below" : ground != Vector3.zero ? ", camera ground " + (eye.y - ground.y).ToString("0.00") + " m below" : ", no ground") + flat + ")");
            if (p == Place.Title) HideNearMoons(eye);
            return true;
        }

        /// The title scene frames its flat camera with fake moons: glowing spheres placed for the camera's shots, two of them
        /// right behind and above the title camera ("Moon (3)", 5 m across, centred 4.2 m behind it: its surface reached the
        /// player's head; "Moon (4)" 7 m above). In VR the player turns round into them: a giant disc filling the view, and
        /// with small head moves the head went in and out of it - "Things would change from completely dark/invisible to
        /// bright and back constantly" (user 2026-10-11). Moons closer than NearMoon metres are switched off at the title;
        /// the distant one stays.
        public static float NearMoon = 15f;
        public static int MoonsHidden;

        static void HideNearMoons(Vector3 eye)
        {
            try
            {
                foreach (Renderer r in SceneScan.Renderers())
                {
                    if (r == null || !r.enabled || !r.name.StartsWith("Moon")) continue;
                    Transform p = r.transform.parent;
                    if (p == null || p.name != "Night") continue;
                    float d = Vector3.Distance(r.bounds.ClosestPoint(eye), eye);
                    if (d > NearMoon) continue;
                    r.enabled = false;
                    MoonsHidden++;
                    Plugin.L.LogInfo("place: title moon '" + VRUI.Path(r.transform) + "' hidden in VR (" + d.ToString("0.0") + " m from the title camera)");
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("place: moons: " + e.Message); }
        }

        static bool GroundBelow(Vector3 eye, out Vector3 ground)
        {
            ground = Vector3.zero;
            RaycastHit hit;
            if (Physics.Raycast(eye + Vector3.up * 0.05f, Vector3.down, out hit, 4f, ~0, QueryTriggerInteraction.Ignore) && eye.y - hit.point.y > 0.4f)
            {
                ground = new Vector3(eye.x, hit.point.y, eye.z);
                return true;
            }
            return false;
        }

        /// The side table (challenge map, deck builder): standing where the game frames it from, facing
        /// it, looking down at the middle of its near half at about 45 degrees.
        public static bool HubSpot(out Vector3 floor, out float yaw)
        {
            floor = Vector3.zero;
            yaw = 0f;
            Dealer dealer = Dealer.Instance;
            CardCamera cc = CardCamera.Instance;
            if (dealer == null || cc == null || cc.ChallengeMapCamera == null) return false;
            FixedCamera shot = cc.ChallengeMapCamera;
            // The shot's pivot, which the game never moves or turns. (shot.Rotation is its child camera's rotation:
            // Unity's headset tracking left a head rotation in that disabled stereo camera, so the side table spot
            // came out facing the head's way at that moment - yaw 27 / 54 instead of 270 in both real-Quest sessions
            // of 2026-10-11, and once 0.8 m outside the caravan. FixedCameraPostfix now also keeps it straight.)
            Vector3 dir = shot.transform.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) return false;
            dir.Normalize();
            yaw = Quaternion.LookRotation(dir).eulerAngles.y;
            float floorY = dealer.transform.position.y;
            Vector3 target = shot.transform.position;   // the shot's pivot: on the side table's near half
            float eyeY = floorY + VRRig.I.EyeHeight * Settings.TableScale;
            float back = Mathf.Clamp(eyeY - target.y, 0.45f, 1.2f);
            Vector3 spot = target - dir * back;
            Vector3 ideal = new Vector3(spot.x, floorY, spot.z);
            // (P2-7, the second playtest: "I'm too far away from the table ... move up a little bit more") with the deck
            // builder's boards gone (SideTable), step up to the map's case: the eyes Settings.StandDistance behind its near edge,
            // as at the card table (they stood 0.51 m back, behind the boards; the map's far pins 1.24 m ahead).
            // (the third playtest, mark 1: "Start me over a bit to the left": the room was looked for at the spot behind, before
            // the step up, and a 'Token_Shard' there put the player 0.15-0.20 m right of the map's middle. Now the step up
            // comes first, and the room is looked for where the player stands, the case before them not counting.)
            float ahead;
            Bounds frame;
            float edgeAlong = float.NaN;
            if (SideTable.RemoveBoards && SideTable.CaseEdge(ideal, dir, out ahead, out frame))
            {
                float step = ahead - Settings.StandDistance * Settings.TableScale;
                edgeAlong = Vector3.Dot(ideal + dir * ahead, dir);
                if (Mathf.Abs(step) > 0.005f)
                {
                    ideal += dir * step;
                    Plugin.L.LogInfo("place: side table spot " + (step > 0f ? "moved " : "moved back ") + (Mathf.Abs(step) / Settings.TableScale).ToString("0.00") + " m to stand " +
                                     Settings.StandDistance.ToString("0.00") + " m from the map case's edge (was " + (ahead / Settings.TableScale).ToString("0.00") + " m)");
                }
            }
            floor = Clear(ideal, dir, "side table", edgeAlong);
            Vector3 moved = floor - ideal;
            if (moved.magnitude > 0.3f * Settings.TableScale)
                Plugin.L.LogWarning("place: the side table spot had to move " + (moved.magnitude / Settings.TableScale).ToString("0.00") + " m to find room (pivot yaw " +
                                    shot.transform.eulerAngles.y.ToString("0") + ", camera yaw " + shot.Rotation.eulerAngles.y.ToString("0") + ")");
            return true;
        }

        /// FixedCamera.LateUpdate (the game resets only its child camera's position each frame): in VR the child
        /// camera's rotation is put back to the shot's own too. Those cameras never render (the game switches them
        /// off), but Unity's headset tracking turned them with the head, and the game reads them - the side table's
        /// spot (above) and the deck builder's challenge card (DeckBuilder -> ScreenToWorldAnchor).
        static void FixedCameraPostfix(FixedCamera __instance)
        {
            if (!XRSettings.enabled) return;
            Camera c = __instance.Camera;
            if (c == null || c.enabled) return;   // a camera that renders is the rig's (VRRig drives it)
            Transform t = c.transform;
            if (t.localRotation != Quaternion.identity) t.localRotation = Quaternion.identity;
        }

        /// The player needs room: the nearest point to `spot` (sideways, or further back from what
        /// they face) where no furniture between knee and head height is within reach of the body.
        /// The caravan has no colliders, so this goes by renderer bounds.
        /// `edgeAlong` (not NaN): what lies wholly at or beyond this distance along `dir` (the side table's case and what is on it)
        /// is not in the way. Small pieces (under SmallPiece real metres every way: a token shard on a shelf) never are.
        static Vector3 Clear(Vector3 spot, Vector3 dir, string what, float edgeAlong = float.NaN)
        {
            float sc = Settings.TableScale;
            float reach = 0.22f * sc, low = spot.y + 0.3f * sc, high = spot.y + (VRRig.I.EyeHeight + 0.15f) * sc;
            var obstacles = new System.Collections.Generic.List<Bounds>();
            var names = new System.Collections.Generic.List<string>();
            foreach (Renderer rd in SceneScan.Renderers())   // cached per scene load (a whole-world search costs ms)
            {
                if (rd == null || !rd.enabled || !rd.gameObject.activeInHierarchy || rd is ParticleSystemRenderer || rd is LineRenderer || rd.gameObject.layer == VRUI.Layer) continue;
                Transform root = rd.transform.root;
                if (root.name == "UIManager" || root.name.StartsWith("HoF2VR")) continue;   // cards and UI float in the air
                if (SideTable.RemoveBoards && SideTable.IsBoard(rd.transform)) continue;      // the deck builder's boards: removed (P2-7)
                Bounds b = rd.bounds;
                if (b.size.x > 3f * sc || b.size.z > 3f * sc) continue;                 // the room shell, floor, ceiling
                if (b.max.y < low || b.min.y > high) continue;
                if (Flat(b, spot) > 1.5f * sc) continue;
                if (!float.IsNaN(edgeAlong) && MinAlong(b, dir) >= edgeAlong - 0.01f * sc) continue;
                if (!float.IsNaN(edgeAlong) && b.size.x < SmallPiece * sc && b.size.y < SmallPiece * sc && b.size.z < SmallPiece * sc) continue;
                obstacles.Add(b);
                names.Add(rd.name);
            }
            Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
            Vector3 best = spot;
            float bestCost = float.MaxValue;
            string blocker = null;
            for (int bi = 0; bi <= 10; bi++)
                for (int si = -10; si <= 10; si++)
                {
                    Vector3 p = spot - dir * (bi * 0.05f * sc) + right * (si * 0.05f * sc);
                    float cost = (bi * bi + si * si * 1.5f);   // prefer stepping back over stepping aside
                    if (cost >= bestCost) continue;
                    int hit = -1;
                    for (int i = 0; i < obstacles.Count && hit < 0; i++) if (Flat(obstacles[i], p) < reach) hit = i;
                    if (hit >= 0) { if (bi == 0 && si == 0) blocker = names[hit]; continue; }
                    best = p;
                    bestCost = cost;
                }
            if (bestCost == float.MaxValue) { Plugin.L.LogWarning("place: no clear spot near the " + what + " - using the ideal one"); return spot; }
            if (blocker != null)
                Plugin.L.LogInfo("place: the " + what + " spot " + Diag.V(spot) + " touches '" + blocker + "' - moved " + Diag.V(best - spot));
            return best;
        }

        public static float SmallPiece = 0.15f;

        /// The least of a box's corners along a horizontal direction.
        static float MinAlong(Bounds b, Vector3 dir)
        {
            float m = float.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, 0f, (i & 2) == 0 ? b.min.z : b.max.z);
                m = Mathf.Min(m, Vector3.Dot(c, dir));
            }
            return m;
        }

        /// Horizontal distance from a point to a box.
        static float Flat(Bounds b, Vector3 p)
        {
            float dx = Mathf.Max(0f, Mathf.Max(b.min.x - p.x, p.x - b.max.x)), dz = Mathf.Max(0f, Mathf.Max(b.min.z - p.z, p.z - b.max.z));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// The caravan: standing at the near edge of the card table, facing the Dealer, looking down
        /// at the middle of the table's near half (where the dungeon is dealt) at about 45 degrees.
        /// The challenge map and deck builder are on the side table to the left.
        public static bool CaravanSpot(out Vector3 floor, out float yaw)
        {
            floor = Vector3.zero;
            yaw = 0f;
            Dealer dealer = Dealer.Instance;
            CardCamera cc = CardCamera.Instance;
            if (dealer == null || cc == null) return false;
            float floorY = dealer.transform.position.y;
            Vector3 seat = cc.StartCamera != null ? cc.StartCamera.Position : (cc.MapCamera != null ? cc.MapCamera.Position : dealer.transform.position - Vector3.forward * 2.5f);
            Vector3 dir = dealer.transform.position - seat;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
            dir.Normalize();
            yaw = Quaternion.LookRotation(dir).eulerAngles.y;

            GameObject tableGo = GameObject.Find("Models/Dealer_Caravan/dealer_caravan_cardtable/Table/Collider");
            Collider table = tableGo != null ? tableGo.GetComponent<Collider>() : null;
            float scale = Settings.TableScale;
            float eyeY = floorY + VRRig.I.EyeHeight * scale;
            Vector3 target;
            Vector3 edge = Vector3.zero;
            if (table != null)
            {
                Bounds b = table.bounds;
                float top = b.max.y;
                var ray = new Ray(new Vector3(seat.x, top - 0.01f, seat.z), dir);
                float enter;
                edge = b.IntersectRay(ray, out enter) ? ray.GetPoint(enter) : new Vector3(seat.x, top, b.min.z);
                target = edge + dir * 0.45f;
                target.y = top;
            }
            else
            {
                // no table found: the dungeon map shot's own focus
                target = cc.MapCamera != null ? cc.MapCamera.transform.position : seat + dir * 1.5f;
                Plugin.L.LogWarning("place: card table collider not found - using the map shot");
            }
            float back = Mathf.Max(0.3f, eyeY - target.y);   // 45 degrees down onto the target
            Vector3 spot = target - dir * back;
            if (table != null && CentredSpot(table, dealer, seat, scale, out floor, out yaw))
            {
                // user 2026-10-11 afternoon: "I want to be centered across from the dealer" (the room's free spot was
                // ~0.2 m to the right): on the line from the Dealer through the table's middle, StandDistance behind the
                // near edge, StandSideways along it. The furniture there (both deck-builder boards) is hidden while the
                // player stands there (TableComfort), so no sideways escape (Clear) any more.
                return true;
            }
            floor = Clear(new Vector3(spot.x, floorY, spot.z), dir, "card table");
            if (table != null)
            {
                // Then step up to the table: the eyes StandDistance (real metres) behind its near edge, whatever the
                // player's height. (The 45-degree rule alone put a player with 1.75 m eyes 0.40 m back and the
                // nearest map row 0.80 m ahead: "the cards are a little far". The sideways position stays where
                // the room had space - the user liked that spot; the furniture beside it is hidden (TableComfort).)
                float ahead = Vector3.Dot(new Vector3(edge.x - floor.x, 0f, edge.z - floor.z), dir);
                float step = ahead - Settings.StandDistance * scale;
                if (Mathf.Abs(step) > 0.005f)
                {
                    floor += dir * step;
                    Plugin.L.LogInfo("place: card table spot " + (step > 0f ? "moved " : "moved back ") + (Mathf.Abs(step) / scale).ToString("0.00") + " m to stand " +
                                     Settings.StandDistance.ToString("0.00") + " m from the table edge (was " + (ahead / scale).ToString("0.00") + " m)");
                }
            }
            // face the Dealer from where the player ended up
            Vector3 toDealer = dealer.transform.position - floor;
            toDealer.y = 0f;
            if (toDealer.sqrMagnitude > 0.01f) yaw = Quaternion.LookRotation(toDealer).eulerAngles.y;
            return true;
        }

        /// The card-table spot across from the Dealer: on the line from the game's own seat (its start shot) toward the
        /// Dealer (his root stands at the table's middle; the line is the table's axis through him), the eyes
        /// Settings.StandDistance behind the table's near edge, moved Settings.StandSideways along the edge (positive = to
        /// the player's right), facing the Dealer.
        static bool CentredSpot(Collider table, Dealer dealer, Vector3 seat, float scale, out Vector3 floor, out float yaw)
        {
            floor = Vector3.zero;
            yaw = 0f;
            Bounds b = table.bounds;
            Vector3 d = dealer.transform.position;
            Vector3 dir = new Vector3(d.x - seat.x, 0f, d.z - seat.z);
            if (dir.sqrMagnitude < 1e-4f) return false;
            dir.Normalize();
            // the near edge: where the line from the seat toward the Dealer enters the table
            var ray = new Ray(new Vector3(seat.x, b.max.y - 0.01f, seat.z) - dir * 2f, dir);
            float enter;
            if (!b.IntersectRay(ray, out enter)) return false;
            Vector3 edge = ray.GetPoint(enter);
            Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
            float floorY = d.y;
            Vector3 p = edge - dir * (Settings.StandDistance * scale) + right * (Settings.StandSideways * scale);
            floor = new Vector3(p.x, floorY, p.z);
            Vector3 face = d - floor;
            face.y = 0f;
            yaw = Quaternion.LookRotation(face.sqrMagnitude > 0.01f ? face : dir).eulerAngles.y;
            Plugin.L.LogInfo("place: card table spot across from the Dealer " + Diag.V(floor) + " yaw " + yaw.ToString("0") + " (edge " + Diag.V(edge) + ", " +
                             Settings.StandDistance.ToString("0.00") + " m back, " + Settings.StandSideways.ToString("0.00") + " m sideways)");
            return true;
        }
    }

    /// Small cached private-field reader.
    internal class AccessTools_Field<TObj, TField> where TObj : class
    {
        readonly HarmonyLib.AccessTools.FieldRef<TObj, TField> m_ref;
        public AccessTools_Field(string name)
        {
            try { m_ref = HarmonyLib.AccessTools.FieldRefAccess<TObj, TField>(name); }
            catch (Exception e) { Plugin.L.LogWarning("field " + typeof(TObj).Name + "." + name + ": " + e.Message); }
        }
        public TField Get(TObj o) => m_ref != null && o != null ? m_ref(o) : default(TField);
    }
}

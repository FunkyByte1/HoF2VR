using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Lives on every driven camera. Unity keeps a list of cameras with XR auto-tracking disabled
    /// and custom stereo matrices; a camera destroyed by a scene unload while still in that state
    /// is a suspect for native crashes in Camera::Render. The guard hands the camera back to Unity
    /// whenever it is disabled or destroyed; the rig re-arms it when it renders again.
    internal class CameraGuard : MonoBehaviour
    {
        public bool Armed;
        Camera m_cam;

        public void Arm(Camera cam)
        {
            m_cam = cam;
            XRDevice.DisableAutoXRCameraTracking(cam, true);
            Armed = true;
        }

        void Release()
        {
            if (!Armed || m_cam == null) return;
            Armed = false;
            try
            {
                m_cam.ResetStereoViewMatrices();
                XRDevice.DisableAutoXRCameraTracking(m_cam, false);
            }
            catch (System.Exception) { }
            if (VRRig.I != null) VRRig.I.Forget(m_cam);
        }

        void OnDisable() => Release();
        void OnDestroy() => Release();
    }

    internal enum RigMode
    {
        Station,  // a fixed spot on the floor (title, card table, campfire...): it never moves by itself
        Body,     // the floor under the hero (first-person combat); yaw turned by the player
    }

    /// Drives every stereo game camera. Unity's automatic HMD tracking is switched off per camera and
    /// every camera is placed at  anchor + yaw * (Scale * head pose relative to the neutral spot).
    /// The anchor is a point on the virtual FLOOR: the player's real floor maps onto it, so the eyes
    /// sit at the player's real height (times Scale). The anchor only changes when the player moves
    /// (walking, turning, their own attacks in combat) or during a fade between places.
    internal class VRRig : MonoBehaviour
    {
        public static VRRig I;

        public RigMode Mode = RigMode.Station;
        /// Station: floor point the neutral spot maps to, the world yaw it faces, and the world scale
        /// (game units per real metre; the card-table room is built larger than life).
        public Vector3 StationFloor;
        public float StationYaw;
        public bool HasStation;
        public float Scale = 1f;
        public Func<Vector3> BodyFloorPos;   // Body mode: the floor under the player this frame
        public float BodyYaw;                // Body mode: world yaw (turning changes this)

        // calibration (tracking space)
        Vector3 m_neutralPos;
        float m_neutralYaw;
        float m_heightOffset;
        bool m_calibrated;
        public const float StandardEyeHeight = 1.65f;

        class Driven
        {
            public Camera Cam;
            public Vector3 IntPos, SetPos;
            public Quaternion IntRot = Quaternion.identity, SetRot = Quaternion.identity;
            public bool HasSet;
            public int Frame = -1;
            // Station mode: the game's own local pose, put back after the camera has rendered
            public bool Restore;
            public Vector3 GameLocalPos;
            public Quaternion GameLocalRot;
        }

        /// The camera the current mode is about (combat: the player camera; table: the card table
        /// camera). Other stereo cameras drawing at the same time share its viewpoint.
        public static Func<Camera> PreferredPrimary;
        public static bool RestoreGamePose = true;   // test toggle: `rigrestore 0|1`

        readonly Dictionary<int, Driven> m_driven = new Dictionary<int, Driven>();
        Driven m_primary;

        /// The rig anchor this frame: the floor point under the neutral spot, and the yaw it faces.
        public Vector3 AnchorPos => m_anchorPos;
        public float AnchorYaw => m_anchorYaw;
        Vector3 m_anchorPos;
        float m_anchorYaw;
        int m_anchorFrame = -1;
        /// Neutral eye height above the floor in real metres (real height, or standard when seated).
        public float EyeHeight => m_neutralPos.y + m_heightOffset;
        public Camera PrimaryCamera => m_primary != null ? m_primary.Cam : null;
        /// The game's intended pose for the primary camera this frame.
        public Vector3 GameCamPos => m_primary != null ? m_primary.IntPos : Vector3.zero;
        public Quaternion GameCamRot => m_primary != null ? m_primary.IntRot : Quaternion.identity;

        void Awake()
        {
            I = this;
            Camera.onPreCull += PreCull;
            Camera.onPostRender += PostRender;
            Application.onBeforeRender += BeforeRender;
            Diag.Commands["recenter"] = a => { Recenter(); return "recentered"; };
            Diag.Commands["latepose"] = a => { LatePoseOn = a.Length > 1 && a[1] == "1"; return "latepose=" + LatePoseOn; };
            Diag.Commands["paralleleyes"] = a => { ParallelEyes = a.Length < 2 || a[1] != "0"; return "parallel eyes " + ParallelEyes; };
            Diag.Commands["rigrestore"] = a => { RestoreGamePose = a.Length > 1 && a[1] == "1"; return "restore=" + RestoreGamePose; };
            Diag.Commands["rig"] = a =>
            {
                if (a.Length > 1) Mode = (RigMode)Enum.Parse(typeof(RigMode), a[1], true);
                return "mode=" + Mode + " anchor=" + Diag.V(AnchorPos) + " yaw=" + AnchorYaw.ToString("0") +
                       " primary=" + (PrimaryCamera ? PrimaryCamera.name : "-") + " game=" + Diag.V(GameCamPos);
            };
            Diag.StatusExtras["rigMode"] = () => Diag.Q(Mode.ToString());
            Diag.StatusExtras["rigCam"] = () => Diag.Q(PrimaryCamera ? PrimaryCamera.name : "");
            Diag.StatusExtras["latePose"] = () => LatePoseFrames.ToString();
            Diag.StatusExtras["posture"] = () => Diag.Q((Settings.Seated ? "seated" : "standing") + " | " + PostureNote + " | lift " + m_heightOffset.ToString("0.000") +
                                                        " eye " + EyeHeight.ToString("0.000") + " | table " + Settings.SeatedTableHeight.ToString("0.00"));
        }

        void OnDestroy() { Camera.onPreCull -= PreCull; Camera.onPostRender -= PostRender; Application.onBeforeRender -= BeforeRender; }

        /// Station mode: after the last eye, give the camera back the game's own pose, so the game's
        /// camera logic (and parents it moves every frame, like NGUI's 3D UI root) never builds on
        /// our head-tracked pose.
        void PostRender(Camera cam)
        {
            Driven d;
            if (!m_driven.TryGetValue(cam.GetInstanceID(), out d) || !d.Restore) return;
            if (cam.stereoEnabled && cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left) return;
            d.Restore = false;
            cam.transform.localPosition = d.GameLocalPos;
            cam.transform.localRotation = d.GameLocalRot;
        }

        /// The head-tracked pose last given to the primary camera (its transform may already hold
        /// the game's pose again).
        public Vector3 PrimaryPos => m_primary != null ? m_primary.SetPos : Vector3.zero;
        public Quaternion PrimaryRot => m_primary != null ? m_primary.SetRot : Quaternion.identity;

        /// Forget a camera that is being disabled or destroyed (called by its CameraGuard).
        internal void Forget(Camera cam)
        {
            m_driven.Remove(cam.GetInstanceID());
            if (m_primary != null && m_primary.Cam == cam) m_primary = null;
        }

        /// The player's current spot and facing become the neutral spot. Height is never recentred:
        /// the runtime's floor is the virtual floor (seated players get a fixed lift instead).
        /// Changes whenever the tracking-to-world mapping jumps (recenter, a new station).
        public int Version;

        // ---- posture (Settings.Seated) ----------------------------------------------------------------------------------
        // Standing: no lift - the eyes at the player's real height. Seated (user 2026-10-13: "How seated-friendly is this?"):
        //  * at a table (the card table, the side table): the table's top is put where a real table's is, SeatedTableHeight
        //    above the real floor (0.75 m: about 0.45 m below seated eyes of ~1.2 m) - the player sits at it like at a real
        //    one; a standing-height lift put the table in the lap;
        //  * everywhere else (fights, the title, the campfire, the waiting space): a fixed lift of SeatedLift (UYF's seated
        //    boost 0.4572 m) to about standing height;
        //  * SeatedTableView = Lifted (2026-10-16, the seated round): UYF's lift at the tables too - its IsSeated lifts the view
        //    everywhere; there the card table's top is 0.73 m below seated eyes of 1.20 m (0.47 m above the real floor, below
        //    a seated player's thighs), so it is the setting, not the default.
        // Both are fixed offsets from the real floor: never taken from where the head happens to be when a place is
        // entered (every place change recentres, at a moment the player doesn't choose - a head bent down to press A would
        // otherwise have put the eyes too high for the whole fight).
        float m_surface = float.NaN;
        /// The seated table's top at the current place (real metres above the virtual floor), NaN away from a table.
        public float SeatedSurface => m_surface;
        public static string PostureNote = "standing";

        // The seated player's own height at the tables (2026-10-16, the seated round's item 3): SeatedTableHeight (0.75 m) puts
        // the table's top 0.45 m below seated eyes of 1.20 m; a shorter seated player (eyes 1.05 m above the floor) had it
        // 0.30 m below the eyes and the map's near row 0.39 m from them - inside the keep-out (0.45 m); a taller one (1.35 m)
        // 0.60 m below, the figurine and the near row in the lap (the seated reach probe). The table now follows the seated
        // eyes - SeatedTableHeight + (eyes - SeatedEyeRef) - so its top stays 0.45 m below them; the nudge moves it from there.
        // The eyes are taken once per posture choice: from the head at the place taken again for Seated (the player's own
        // press, sitting), or at the first seated recentre of a run - never again at later recentres (a head bent down then
        // would move the table). A head outside 0.9-1.5 m is not a seated head (the headset on the desk at the boot, a player
        // still standing): asked again at the next recentre.
        public const float SeatedEyeRef = 1.20f, SeatedTableFloor = 0.40f, SeatedTableCeiling = 1.30f;
        static float s_seatedHead = float.NaN;
        /// The seated eye height the tables are placed for (real metres above the real floor), NaN until known.
        public static float SeatedHead => s_seatedHead;
        /// A posture choice: the seated eyes are taken again at the next recentre (or lift change).
        public static void PostureReset() => s_seatedHead = float.NaN;

        static void NoteSeatedHead(float headY)
        {
            if (!Settings.Seated || !float.IsNaN(s_seatedHead) || headY < 0.9f || headY > 1.5f) return;
            s_seatedHead = headY;
            float by = headY - SeatedEyeRef;
            Plugin.L.LogInfo("rig: seated eyes " + headY.ToString("0.00") + " m above the floor" +
                             (Mathf.Abs(by) > 0.005f ? " - the tables " + Mathf.Abs(by).ToString("0.00") + " m " + (by < 0f ? "lower" : "higher") : ""));
        }

        /// The seated table's top above the real floor (real metres): SeatedTableHeight for seated eyes of SeatedEyeRef, as much
        /// lower or higher as the player's seated eyes are (SeatedTableHeight itself until they are known).
        public static float SeatedTableTop()
        {
            float by = float.IsNaN(s_seatedHead) ? 0f : s_seatedHead - SeatedEyeRef;
            return Mathf.Clamp(Settings.SeatedTableHeight + by, SeatedTableFloor, SeatedTableCeiling);
        }

        /// The lift for a neutral head at headY (real metres above the runtime's floor) at a place with this table top.
        static float LiftFor(float headY, float surface, out string note)
        {
            if (headY < 0.5f)
            {
                // a tracking origin at eye level (seated runtimes) reports ~0 m: the eyes at EyeHeight, as always
                note = "no floor: eyes at " + Settings.EyeHeight.ToString("0.00");
                return Settings.EyeHeight - headY;
            }
            if (!Settings.Seated) { note = "standing"; return 0f; }
            if (!float.IsNaN(surface) && Settings.SeatedTableView == Settings.TableView.Lifted)
            {
                // Until You Fall's seated view at the tables too (IsSeated: the view SeatedHeightBoost above the real seated
                // head, everywhere): the table as a standing player sees it - and as low below the seated eyes
                note = "seated at a table, lifted " + Settings.SeatedLift.ToString("0.00") + " m as everywhere (SeatedTableView Lifted)";
                return Settings.SeatedLift;
            }
            if (!float.IsNaN(surface))
            {
                float top = SeatedTableTop();
                note = "seated at a table: its top " + surface.ToString("0.00") + " m -> " + top.ToString("0.00") + " m above the floor" +
                       (Mathf.Abs(top - Settings.SeatedTableHeight) > 1e-4f ? " (" + Settings.SeatedTableHeight.ToString("0.00") + " for seated eyes of " + s_seatedHead.ToString("0.00") + " m)" : "");
                return surface - top;
            }
            note = "seated: lifted " + Settings.SeatedLift.ToString("0.00") + " m";
            return Settings.SeatedLift;
        }

        /// Recentre where the player is (the recentre button, a fight's start): a seated player at a table keeps the table's
        /// lift; anywhere else the surface is gone.
        public void Recenter() => Recenter(Places.AtCaravan && !CombatVR.Active ? m_surface : float.NaN);

        /// Recentre at a place: `seatedSurface` = its table's top for seated play (real metres above the virtual floor;
        /// NaN = not a table).
        public void Recenter(float seatedSurface) => Recenter(seatedSurface, false);

        /// Seconds of head history a place change recentres on (`settled`), and how far the head may be from that spot (real
        /// metres) for it still to count (further: the player walked - the head of now).
        public static float SettleSeconds = 4f, SettleReach = 0.45f, SettleAfterPress = 2.5f, SettleBeforePress = 1.0f;

        /// `settled` (a place change): within SettleAfterPress of a plate press, on where the player mostly stood and faced in the
        /// SettleSeconds before the reach to it (SettleBeforePress before the press), not the head of this instant - the third playtest, mark 20: a challenge starts on the press of the
        /// Start plate, out to the right, and the player was put 0.24 m right of the table's middle once they stood straight again.
        public void Recenter(float seatedSurface, bool settled)
        {
            Version++;
            VRInput.Update();
            m_neutralPos = VRInput.HeadPos;
            m_neutralYaw = VRInput.HeadRot.eulerAngles.y;
            string from = "";
            Vector3 sp;
            float sy;
            // (only right after a plate press - the reach out to it is the lean; anything else recentres on the head of now)
            if (settled && Time.unscaledTime - PlateTouch.LastPressAt <= SettleAfterPress &&
                VRInput.StandingHead(SettleSeconds, PlateTouch.LastPressAt - SettleBeforePress, out sp, out sy))
            {
                Vector3 d = m_neutralPos - sp;
                d.y = 0f;
                if (d.magnitude <= SettleReach)
                {
                    from = string.Format(System.Globalization.CultureInfo.InvariantCulture, " (where the head mostly was in the {0:0} s before the reach to the plate; the head now {1:0.00} m and {2:0} deg from it)",
                                         SettleSeconds, d.magnitude, Mathf.DeltaAngle(sy, m_neutralYaw));
                    m_neutralPos = new Vector3(sp.x, m_neutralPos.y, sp.z);   // (the height of now: sitting down is real)
                    m_neutralYaw = sy;
                }
                else from = " (the head now " + d.magnitude.ToString("0.00") + " m from where it mostly was: the player walked)";
            }
            m_surface = seatedSurface;
            NoteSeatedHead(m_neutralPos.y);
            m_heightOffset = LiftFor(m_neutralPos.y, m_surface, out PostureNote);
            m_calibrated = true;
            Plugin.L.LogInfo("recenter: neutral head " + Diag.V(m_neutralPos) + " yaw " + m_neutralYaw.ToString("0") +
                             " eye height " + EyeHeight.ToString("0.00") + (m_heightOffset != 0f ? " (lift " + m_heightOffset.ToString("0.00") + ")" : "") + " - " + PostureNote + from);
        }

        /// The posture or the seated table's height changed: the lift again, the neutral spot and facing kept (call it in
        /// the dark: the view moves up or down).
        public void Relift(string why)
        {
            if (!m_calibrated) return;
            float was = m_heightOffset;
            VRInput.Update();
            NoteSeatedHead(VRInput.HeadPos.y);   // (a posture chosen in a fight or between places: the head now)
            m_heightOffset = LiftFor(m_neutralPos.y, m_surface, out PostureNote);
            if (Mathf.Abs(was - m_heightOffset) < 1e-4f) return;
            Version++;
            Plugin.L.LogInfo("rig: lift " + was.ToString("0.00") + " -> " + m_heightOffset.ToString("0.00") + " m (" + why + "), eye height " + EyeHeight.ToString("0.00") + " - " + PostureNote);
        }

        /// The lift a seated or standing player gets away from the tables (the waiting space builds its own frame with it).
        public static float AwayLift(float headY)
        {
            string note;
            return LiftFor(headY, float.NaN, out note);
        }

        /// The neutral spot in tracking space (position, facing) and the height lift (seated): the
        /// waiting space (Limbo) builds its own fixed frame from it.
        public void NeutralFrame(out Vector3 pos, out float yaw, out float lift)
        {
            if (!m_calibrated) Recenter();
            pos = m_neutralPos;
            yaw = m_neutralYaw;
            lift = m_heightOffset;
        }

        /// Tracking position -> "anchor space": the neutral spot's floor point at the origin, facing
        /// +Z, real metres, y = height above the floor.
        Vector3 Local(Vector3 trackingPos)
        {
            Vector3 p = Quaternion.Euler(0, -m_neutralYaw, 0) * new Vector3(trackingPos.x - m_neutralPos.x, 0f, trackingPos.z - m_neutralPos.z);
            p.y = trackingPos.y + m_heightOffset;
            return p;
        }

        /// Head pose relative to the anchor (world units, i.e. already scaled), facing +Z.
        public void HeadRelative(out Vector3 pos, out Quaternion rot)
        {
            pos = Local(VRInput.HeadPos) * Scale;
            rot = Quaternion.Euler(0, -m_neutralYaw, 0) * VRInput.HeadRot;
        }

        /// Tracking-space pose (e.g. a controller) -> world.
        public Vector3 ToWorld(Vector3 trackingPos)
        {
            UpdateAnchor();
            return m_anchorPos + Quaternion.Euler(0, m_anchorYaw, 0) * (Local(trackingPos) * Scale);
        }

        public Quaternion ToWorld(Quaternion trackingRot)
        {
            UpdateAnchor();
            return Quaternion.Euler(0, m_anchorYaw - m_neutralYaw, 0) * trackingRot;
        }

        /// World direction a tracking-space direction maps to.
        public Vector3 ToWorldDir(Vector3 trackingDir) { UpdateAnchor(); return Quaternion.Euler(0, m_anchorYaw - m_neutralYaw, 0) * trackingDir; }

        /// Inverse of ToWorld: the tracking-space point that maps to a world point (tests place the
        /// simulated controllers with it).
        public Vector3 FromWorld(Vector3 world)
        {
            UpdateAnchor();
            Vector3 q = Quaternion.Euler(0, -m_anchorYaw, 0) * (world - m_anchorPos) / Mathf.Max(1e-4f, Scale);
            Vector3 xz = Quaternion.Euler(0, m_neutralYaw, 0) * new Vector3(q.x, 0f, q.z);
            return new Vector3(m_neutralPos.x + xz.x, q.y - m_heightOffset, m_neutralPos.z + xz.z);
        }

        /// Inverse of ToWorld(Quaternion).
        public Quaternion FromWorld(Quaternion worldRot) { UpdateAnchor(); return Quaternion.Euler(0, m_neutralYaw - m_anchorYaw, 0) * worldRot; }

        /// Tracking-space direction a world direction maps to.
        public Vector3 FromWorldDir(Vector3 worldDir) { UpdateAnchor(); return Quaternion.Euler(0, m_neutralYaw - m_anchorYaw, 0) * worldDir; }

        public Vector3 HeadWorldPos => ToWorld(VRInput.HeadPos);
        public Quaternion HeadWorldRot => ToWorld(VRInput.HeadRot);

        /// A station whose neutral eyes are exactly at `eye` (used where the game gives a camera pose
        /// but no floor): the floor point is that far below.
        public Vector3 FloorBelowEye(Vector3 eye) => eye - Vector3.up * (EyeHeight * Scale);

        /// Where the player's eyes are when they stand on the neutral spot, looking level the way the
        /// place faces (the "screen" of a station: menus and things the game pins to its camera).
        public Vector3 NeutralEye { get { UpdateAnchor(); return m_anchorPos + Vector3.up * (EyeHeight * Scale); } }
        public Quaternion NeutralRot { get { UpdateAnchor(); return Quaternion.Euler(0f, m_anchorYaw, 0f); } }

        public void SetStation(Vector3 floor, float yaw, float scale, string why)
        {
            Version++;
            Mode = RigMode.Station;
            StationFloor = floor;
            StationYaw = yaw;
            Scale = scale;
            HasStation = true;
            m_anchorFrame = -1;
            Plugin.L.LogInfo("rig: station " + why + " floor " + Diag.V(floor) + " yaw " + yaw.ToString("0") + " scale " + scale.ToString("0.00") +
                             " -> eyes at " + Diag.V(floor + Vector3.up * (EyeHeight * scale)));
        }

        /// The anchor for this frame (Body mode reads the body once per frame).
        void UpdateAnchor()
        {
            if (m_anchorFrame == Time.frameCount) return;
            m_anchorFrame = Time.frameCount;
            if (!m_calibrated) Recenter();
            if (Mode == RigMode.Body && BodyFloorPos != null)
            {
                m_anchorPos = BodyFloorPos();
                m_anchorYaw = BodyYaw;
                return;
            }
            if (!HasStation && m_primary != null && m_primary.HasSet)
            {
                // nothing has placed the player yet: eyes where the game's camera is, until a place
                // controller takes over (it never follows that camera afterwards)
                SetStation(FloorBelowEye(m_primary.IntPos), m_primary.IntRot.eulerAngles.y, 1f, "first camera '" + m_primary.Cam.name + "'");
            }
            m_anchorPos = StationFloor;
            m_anchorYaw = StationYaw;
        }

        /// The table <-> fight tunnel cameras are never driven (Places keeps the tunnel camera off in
        /// VR; driven, it re-drew the player's surroundings instead of the tunnel).
        static bool Drivable(Camera cam)
        {
            if (cam.stereoTargetEye == StereoTargetEyeMask.None || cam.targetTexture != null || cam.orthographic) return false;
            // the name test once per camera: Object.name allocates a new string on every read, and this runs for
            // every camera and eye every frame
            int id = cam.GetInstanceID();
            bool ok;
            if (!s_drivableByName.TryGetValue(id, out ok))
            {
                string n = cam.name;
                ok = !n.StartsWith("HoF2VR_") && n != "WormholeCamera";
                s_drivableByName[id] = ok;
            }
            return ok && cam.GetComponent<VRUI.OverlayMarker>() == null;
        }

        static readonly Dictionary<int, bool> s_drivableByName = new Dictionary<int, bool>();

        /// Poses for all driven cameras are set just before rendering starts (after the game's
        /// LateUpdate), so both eyes cull, light and draw with the same head pose. (Setting them in
        /// the first eye's onPreCull left that eye culled with the previous matrices: grass and
        /// lights went missing in the left eye only.)
        void BeforeRender()
        {
            if (!XRSettings.enabled) return;
            EnsureTracker();
            VRInput.Update();
            Camera pref = PreferredPrimary != null ? PreferredPrimary() : null;
            Driven first = null;
            if (pref != null) m_driven.TryGetValue(pref.GetInstanceID(), out first);
            if (first == null) first = m_primary;
            if (first != null) Drive(first);
            s_list.Clear();
            s_list.AddRange(m_driven.Values);
            foreach (Driven d in s_list) if (d != first) Drive(d);
        }

        static readonly List<Driven> s_list = new List<Driven>();

        // ---- late head pose -------------------------------------------------------------------
        // Unity hands the head pose of the frame being rendered (the one the runtime gets with the
        // submitted frame) only to cameras it tracks itself, right before they render; InputTracking
        // still reports the previous frame's pose then. Drawing with that one-frame-old pose made
        // the view lag and judder whenever the head turned. So an empty camera that Unity tracks
        // renders first each frame, and its pose re-poses every camera we drive before they draw.
        Camera m_tracker;
        int m_lateFrame = -1;
        public int LatePoseFrames;   // diagnostics: frames that got the late pose
        public static bool LatePoseOn = true;   // test toggle: `latepose 0|1`

        void EnsureTracker()
        {
            if (m_tracker != null || !XRSettings.enabled) return;
            var go = new GameObject("HoF2VR_Tracker");
            DontDestroyOnLoad(go);
            m_tracker = go.AddComponent<Camera>();
            m_tracker.cullingMask = 0;
            m_tracker.clearFlags = CameraClearFlags.Nothing;
            m_tracker.depth = -10000;            // before every game camera
            m_tracker.allowMSAA = false;
            m_tracker.allowHDR = false;
            m_tracker.useOcclusionCulling = false;
            m_tracker.stereoTargetEye = StereoTargetEyeMask.Both;
            go.AddComponent<TrackerHook>();
            Plugin.L.LogInfo("rig: late-pose tracker camera created");
        }

        internal class TrackerHook : MonoBehaviour
        {
            void OnPreCull()
            {
                if (I != null) I.LatePose(transform.localPosition, transform.localRotation);
            }
        }

        void LatePose(Vector3 headPos, Quaternion headRot)
        {
            if (!LatePoseOn || m_lateFrame == Time.frameCount) return;   // second eye
            m_lateFrame = Time.frameCount;
            VRInput.OverrideHead(headPos, headRot);
            LatePoseFrames++;
            foreach (Driven d in m_driven.Values)
                if (d.Frame == Time.frameCount) Pose(d);
            // the hands too (docs/hand-feel-design.md §3.2): the controllers' render-time poses, the gloves placed again
            try
            {
                bool late = VRInput.LateHands();
                if (late)
                {
                    HandFeel.Late();
                    if (VRHands.I != null) VRHands.I.UpdateHands();
                    TableCardGrab.Late();   // a picked-up table card stays in the glove
                    FigurineGrab.Late();    // and the hero's figurine
                }
                HandFeel.ProbeFrame(late);
            }
            catch (System.Exception e) { Plugin.L.LogError("late hands: " + e); }
        }

        /// Final camera pose from this frame's anchor and the (late) head pose. Every driven camera
        /// gets the same one (3D UI and effect cameras draw from where the player looks).
        void Pose(Driven d)
        {
            Camera cam = d.Cam;
            if (cam == null || !cam.isActiveAndEnabled) return;
            UpdateAnchor();
            Vector3 rp;
            Quaternion rr;
            HeadRelative(out rp, out rr);
            Quaternion yaw = Quaternion.Euler(0, m_anchorYaw, 0);
            Transform t = cam.transform;
            t.position = m_anchorPos + yaw * rp;
            t.rotation = yaw * rr;
            d.SetPos = t.position;
            d.SetRot = t.rotation;
            d.HasSet = true;
            SetEyes(cam, d.SetPos, d.SetRot);
        }

        void PreCull(Camera cam)
        {
            if (!XRSettings.enabled || !Drivable(cam)) return;
            Driven d;
            if (!m_driven.TryGetValue(cam.GetInstanceID(), out d))
            {
                d = new Driven { Cam = cam };
                m_driven[cam.GetInstanceID()] = d;
                Plugin.L.LogInfo("rig: driving camera '" + cam.name + "' (scene " + cam.gameObject.scene.name + ", clip " + cam.nearClipPlane + ".." + cam.farClipPlane +
                                 ", scale " + cam.transform.lossyScale.x.ToString("0.###") + ", mask 0x" + cam.cullingMask.ToString("X") + ")");
            }
            CameraGuard guard = cam.GetComponent<CameraGuard>();
            if (guard == null) guard = cam.gameObject.AddComponent<CameraGuard>();
            if (!guard.Armed) guard.Arm(cam);
            Drive(d);   // only does anything for a camera that appeared after onBeforeRender
        }

        void Drive(Driven d)
        {
            Camera cam = d.Cam;
            if (cam == null || !cam.isActiveAndEnabled || !Drivable(cam)) return;
            if (d.Frame == Time.frameCount) return;   // done this frame (or second eye)
            d.Frame = Time.frameCount;
            VRInput.Update();
            if (!m_calibrated) Recenter();

            Transform t = cam.transform;
            Camera pref = PreferredPrimary != null ? PreferredPrimary() : null;
            if (pref != null && !pref.isActiveAndEnabled) pref = null;
            if (pref != null ? cam == pref
                             : m_primary == null || !m_primary.Cam || !m_primary.Cam.isActiveAndEnabled || cam == Camera.main)
                m_primary = d;
            else if (m_primary == null || !m_primary.Cam || !m_primary.Cam.isActiveAndEnabled)
                m_primary = d;

            // What the game wants this camera to show (diagnostics, and stations that take the
            // game's framing once). In Station mode the transform holds the game's own pose (it is
            // put back after rendering); in Body mode it keeps ours between frames (the game's
            // camera-relative stick input then follows the player's view), so a changed transform
            // means the game moved it.
            bool restore = Mode == RigMode.Station && RestoreGamePose;
            if (restore || !d.HasSet || (t.position - d.SetPos).sqrMagnitude > 1e-8f || Quaternion.Angle(t.rotation, d.SetRot) > 0.01f)
            {
                d.IntPos = t.position;
                d.IntRot = t.rotation;
            }
            if (restore)
            {
                d.Restore = true;
                d.GameLocalPos = t.localPosition;
                d.GameLocalRot = t.localRotation;
            }
            else d.Restore = false;
            Pose(d);
        }

        /// Explicit per-eye view matrices in world units (eye separation times the rig Scale). Unity
        /// would otherwise derive the eye separation from the camera's lossyScale; the table camera sits under NGUI's UIRoot
        /// (scale 0.002), which squashed the stereo to nothing.
        public static void SetEyes(Camera cam, Vector3 headPos, Quaternion headRot) => SetEyes(cam, headPos, headRot, I != null ? I.Scale : 1f);

        /// Both eyes rotated exactly like the head when the runtime's eyes are parallel (see SetEyes).
        public static bool ParallelEyes = true;

        /// The same with an explicit world scale (the waiting space is always life-size).
        public static void SetEyes(Camera cam, Vector3 headPos, Quaternion headRot, float s)
        {
            Vector3 h = InputTracking.GetLocalPosition(XRNode.Head);
            Quaternion hr = InputTracking.GetLocalRotation(XRNode.Head);
            Quaternion inv = Quaternion.Inverse(hr);
            Vector3 lo = inv * (InputTracking.GetLocalPosition(XRNode.LeftEye) - h);
            Vector3 ro = inv * (InputTracking.GetLocalPosition(XRNode.RightEye) - h);
            Quaternion lr = inv * InputTracking.GetLocalRotation(XRNode.LeftEye);
            Quaternion rr = inv * InputTracking.GetLocalRotation(XRNode.RightEye);
            if ((ro - lo).sqrMagnitude < 1e-6f)
            {
                lo = new Vector3(-0.032f, 0f, 0f);
                ro = new Vector3(0.032f, 0f, 0f);
                lr = rr = Quaternion.identity;
            }
            else if (ParallelEyes && Quaternion.Angle(lr, Quaternion.identity) < 0.5f && Quaternion.Angle(rr, Quaternion.identity) < 0.5f)
            {
                // Headsets with parallel eyes (Quest 2, the simulator): both eyes get exactly the head's rotation. The
                // two eye rotations read back from the runtime differ by float noise (~1e-7); in the frames where that
                // noise was large enough Unity culled the camera a second time for the right eye, and that eye then drew
                // without any directional-light shadows - the whole night scene flashed moonlit for one frame in one
                // eye ("still some flickering when I look around", title, 2026-10-11). `paralleleyes 0|1`.
                lr = rr = Quaternion.identity;
            }
            // a larger-than-life room (Scale > 1) is seen with proportionally wider-set eyes
            lo *= s;
            ro *= s;
            cam.SetStereoViewMatrix(Camera.StereoscopicEye.Left, View(headPos + headRot * lo, headRot * lr));
            cam.SetStereoViewMatrix(Camera.StereoscopicEye.Right, View(headPos + headRot * ro, headRot * rr));
        }

        static Matrix4x4 View(Vector3 pos, Quaternion rot)
        {
            return Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(pos, rot, Vector3.one).inverse;
        }
    }
}

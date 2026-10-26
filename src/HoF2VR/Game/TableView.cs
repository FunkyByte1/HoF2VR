using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The caravan's 3D UI in VR. On the monitor the game lays its 3D UI out on the table camera's
    /// screen: zoomed and chance cards hang on the camera rig ('UI Root (3D)/3D Root'), encounter
    /// cards, event cards and the stat cards sit at screen points of a camera (CameraAnchor /
    /// UIAnchor), and some anchors are children of the camera itself. In VR the view no longer follows
    /// the game's camera, the camera's frustum is the headset's (~90 degrees), and the camera is moved
    /// to the head for rendering - so those cards ended up behind the player, at the edge of vision,
    /// or glued to the head ("cut off when I look at it").
    /// Here the caravan gets one "screen": the player's neutral eyes looking at the 2D panel, with the
    /// flat game's field of view. The camera rig is put there, screen anchors are computed on it, and
    /// the camera's children no longer follow the head. Cards then appear in front of the player where
    /// they appear on the monitor, next to the dialogue on the panel.
    internal static class TableView
    {
        public static bool On = true;   // test toggle: `tableview 0|1`
        public static bool HasShot;
        public static Vector3 ShotPos;
        public static Quaternion ShotRot = Quaternion.identity;
        public static bool Overridden;
        /// Vertical field of view of the game's table shots on a monitor (read from its shot cameras).
        public static float FlatFov = 60f;
        static bool s_fovRead;

        static readonly AccessTools.FieldRef<CardCamera, Transform> s_rig = AccessTools.FieldRefAccess<CardCamera, Transform>("m_cameraTransform");
        static Camera s_screen;
        static Vector3 s_refreshedPos;
        static Quaternion s_refreshedRot;
        static int s_detached = -1;
        public static int Refreshes;

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(CardCamera), "UpdateCameraProperties"), postfix: new HarmonyMethod(typeof(TableView), nameof(Postfix)));
            h.Patch(AccessTools.Method(typeof(UIAnchor), "Update"),
                prefix: new HarmonyMethod(typeof(TableView), nameof(UIAnchorPrefix)), postfix: new HarmonyMethod(typeof(TableView), nameof(UIAnchorPostfix)));
            Diag.Commands["tableview"] = a =>
            {
                if (a.Length > 1) On = a[1] == "1";
                return "tableview=" + On + " overridden=" + Overridden + " fov=" + FlatFov + " refreshes=" + Refreshes + " shot=" + Diag.V(ShotPos) + " " + Diag.V(ShotRot.eulerAngles);
            };
            Diag.Commands["shotfov"] = a =>
            {
                var sb = new System.Text.StringBuilder();
                foreach (FixedCamera f in Object.FindObjectsOfType<FixedCamera>())
                    if (f.Camera != null) sb.AppendFormat(" {0}={1:0.#}", f.name, AuthoredFov(f.Camera));
                return sb.ToString();
            };
            Diag.Commands["anchors"] = a => { Refresh("command"); return "refreshed " + Refreshes; };
        }

        /// In VR a stereo camera reports the headset's field of view; with stereo off for a moment it
        /// reports its own.
        static float AuthoredFov(Camera c)
        {
            StereoTargetEyeMask was = c.stereoTargetEye;
            c.stereoTargetEye = StereoTargetEyeMask.None;
            float fov = c.fieldOfView;
            c.stereoTargetEye = was;
            return fov;
        }

        static void ReadFlatFov(CardCamera cc)
        {
            if (s_fovRead) return;
            s_fovRead = true;
            // the table shots the player sees most (some shot cameras were overwritten with the
            // headset's value by Unity; the reveal/dice/wheel shots keep the authored one)
            foreach (FixedCamera f in new FixedCamera[] { cc.RevealCamera, cc.DiceCamera, cc.WheelCamera, cc.PendulumCamera })
            {
                if (f == null || f.Camera == null) continue;
                float fov = AuthoredFov(f.Camera);
                if (fov > 20f && fov < 80f) { FlatFov = fov; break; }
            }
            Plugin.L.LogInfo("TableView: the table is framed with a " + FlatFov.ToString("0.#") + " degree field of view on a monitor");
        }

        /// A camera that never renders: the flat screen at the player's screen pose (1920x1080, the
        /// flat field of view). Screen-point maths (CameraAnchor, UIAnchor) run on it at the caravan.
        public static Camera ScreenCamera()
        {
            if (!EnsureScreen()) return null;
            Vector3 eye;
            Quaternion rot;
            VRUI.I.TableScreen(out eye, out rot);
            return PoseScreen(eye, rot, FlatFov);
        }

        /// The same flat camera on the menu panel's screen (the title): the title logo is pinned to a point of
        /// its camera's screen; with the headset's wide field of view that point was near the panel's edge.
        public static Camera MenuScreenCamera()
        {
            if (!EnsureScreen()) return null;
            Vector3 eye;
            Quaternion rot;
            float fov;
            VRUI.I.MenuScreen(out eye, out rot, out fov);
            return PoseScreen(eye, rot, fov);
        }

        static bool EnsureScreen()
        {
            if (VRUI.I == null || VRUI.I.RT == null || VRRig.I == null) return false;
            if (s_screen == null)
            {
                var go = new GameObject("HoF2VR_FlatScreen");
                Object.DontDestroyOnLoad(go);
                s_screen = go.AddComponent<Camera>();
                s_screen.enabled = false;
                s_screen.stereoTargetEye = StereoTargetEyeMask.None;
                s_screen.targetTexture = VRUI.I.RT;   // pixelRect 1920x1080, aspect 16:9 (never drawn into)
                s_screen.cullingMask = 0;
                s_screen.nearClipPlane = 0.01f;
            }
            return true;
        }

        static Camera PoseScreen(Vector3 eye, Quaternion rot, float fov)
        {
            s_screen.transform.SetPositionAndRotation(eye, rot);
            s_screen.fieldOfView = fov;
            s_screen.ResetAspect();
            return s_screen;
        }

        static void Postfix(CardCamera __instance)
        {
            Transform rig = s_rig(__instance);
            if (rig == null) return;
            ShotPos = rig.position;
            ShotRot = rig.rotation;
            HasShot = true;
            Overridden = On && XRSettings.enabled && Places.AtCaravan && VRRig.I != null && VRUI.I != null && VRUI.I.Panel != null;
            if (!XRSettings.enabled) return;
            ReadFlatFov(__instance);
            DetachCameraChildren(rig);
            if (!Overridden) return;
            Vector3 pos;
            Quaternion rot;
            VRUI.I.TableScreen(out pos, out rot);
            rig.SetPositionAndRotation(pos, rot);
            // screen anchors are only computed when they are enabled: redo them when the screen moved - at most
            // every few frames while it moves (a panel turn), and once more when it has settled
            bool moved = (pos - s_refreshedPos).sqrMagnitude > 1e-6f || Quaternion.Angle(rot, s_refreshedRot) > 0.2f;
            bool still = (pos - s_lastPos).sqrMagnitude < 1e-8f && Quaternion.Angle(rot, s_lastRot) < 0.01f;
            s_lastPos = pos;
            s_lastRot = rot;
            if (moved && (still || Time.frameCount - s_refreshFrame >= RefreshEvery))
                Refresh("screen at " + Diag.V(pos) + " yaw " + rot.eulerAngles.y.ToString("0"));
        }

        static Vector3 s_lastPos;
        static Quaternion s_lastRot = Quaternion.identity;
        static int s_refreshFrame = -100;
        const int RefreshEvery = 6;
        static readonly SceneScan.AllOf<CameraAnchor> s_cameraAnchors = new SceneScan.AllOf<CameraAnchor>();
        static readonly SceneScan.AllOf<UIAnchor> s_uiAnchors = new SceneScan.AllOf<UIAnchor>();

        /// Things parented to the table camera (event-card anchors in its screen corners) would follow
        /// the head while it renders: give them to the camera's parent instead, where they stay put.
        static void DetachCameraChildren(Transform rig)
        {
            UICamera ui = UI3D.Instance != null ? UI3D.Instance.UICamera : null;
            Camera cam = ui != null ? ui.cachedCamera : null;
            if (cam == null || cam.GetInstanceID() == s_detached) return;
            s_detached = cam.GetInstanceID();
            Transform t = cam.transform;
            var kids = new List<Transform>();
            for (int i = 0; i < t.childCount; i++) kids.Add(t.GetChild(i));
            foreach (Transform k in kids) k.SetParent(t.parent, true);
            if (kids.Count > 0) Plugin.L.LogInfo("TableView: " + kids.Count + " anchors under '" + cam.name + "' no longer follow the head");
        }

        /// Re-run every screen anchor on the caravan screen.
        public static void Refresh(string why)
        {
            if (!XRSettings.enabled || !Places.AtCaravan || ScreenCamera() == null) return;
            Vector3 pos;
            Quaternion rot;
            VRUI.I.TableScreen(out pos, out rot);
            s_refreshedPos = pos;
            s_refreshedRot = rot;
            s_refreshFrame = Time.frameCount;
            int n = 0;
            // (lists cached per scene load: the whole-world searches cost milliseconds per call)
            foreach (CameraAnchor ca in s_cameraAnchors.Get())
            {
                if (ca == null || !ca.isActiveAndEnabled) continue;
                try { ca.UpdatePosition(); n++; }
                catch (System.Exception e) { Plugin.L.LogWarning("TableView: anchor " + ca.name + ": " + e.Message); }
            }
            Camera table = UI3D.Instance != null && UI3D.Instance.UICamera != null ? UI3D.Instance.UICamera.cachedCamera : null;
            foreach (UIAnchor ua in s_uiAnchors.Get())
            {
                if (ua == null || ua.uiCamera == null || ua.uiCamera != table) continue;
                bool was = ua.enabled;
                try { ua.Update(); n++; }
                catch (System.Exception e) { Plugin.L.LogWarning("TableView: UIAnchor " + ua.name + ": " + e.Message); }
                ua.enabled = was;
            }
            Refreshes++;
            if (Refreshes <= 5 || Refreshes % 50 == 0) Plugin.L.LogInfo("TableView: " + n + " screen anchors laid out on the player's screen (" + why + ")");
        }

        // UIAnchor (NGUI) with the table camera: the same maths on the flat screen at the caravan.
        static Camera s_uiAnchorCam;

        static void UIAnchorPrefix(UIAnchor __instance, out bool __state)
        {
            __state = false;
            if (!On || !XRSettings.enabled || !Places.AtCaravan || __instance.uiCamera == null || __instance.uiCamera.orthographic) return;
            if (__instance.uiCamera.stereoTargetEye == StereoTargetEyeMask.None) return;
            Camera screen = ScreenCamera();
            if (screen == null) return;
            s_uiAnchorCam = __instance.uiCamera;
            __instance.uiCamera = screen;
            __state = true;
        }

        static void UIAnchorPostfix(UIAnchor __instance, bool __state)
        {
            if (__state) __instance.uiCamera = s_uiAnchorCam;
            if (__state) TableHud.AfterAnchor(__instance);
        }
    }
}

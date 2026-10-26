using System.Text;
using UnityEngine;
using Valve.VR;

namespace HoF2VR
{
    /// Where things are on the controller, in the pose OpenVR (OpenComposite) reports — the SteamVR
    /// "raw" controller frame, converted to Unity (z flipped). Values from OpenComposite's interaction
    /// profiles (SteamVR render-model components):
    ///   Quest 2 Touch: grip (fist centre) at (+-0.007, -0.0018, -0.102), fist axis 20.6 deg above the
    ///                  frame's forward; pointer tip at (+-0.0167, -0.0252, -0.0247), aiming 37.4 deg below.
    ///   Index:         grip at (0, -0.015, -0.13), fist axis 15.4 deg up; pointer = frame forward.
    internal static class ControllerGeometry
    {
        public static string Type = "";
        /// Tests (`ctrltype`): the type kept whatever the runtime reports (the Monado test headset reports Index "knuckles").
        public static string Forced;
        static float s_nextQuery;

        /// OpenComposite's Quest 2 grip point (right hand): where the hand model was first fitted.
        public static readonly Vector3 TouchGrip = new Vector3(-0.007f, -0.0018f, -0.102f);

        /// The fitted glove's grip point on Touch (Settings Hands.GloveUp/GloveAlong; x mirrored for the left hand).
        public static Vector3 TouchFitGrip(bool left)
        {
            // (GloveSide: + = toward the other hand; the right hand's other hand is at -x)
            Vector3 g = TouchGrip + new Vector3(0.0025f * Mathf.Clamp01(Mathf.Abs(Settings.GloveAngle) / 20f) - Settings.GloveSide * 0.01f, Settings.GloveUp * 0.01f, Settings.GloveAlong * 0.01f);
            if (left) g.x = -g.x;
            return g;
        }

        public static Vector3 GripPos(bool left)
        {
            if (Type == "knuckles") return new Vector3(0f, -0.015f, -0.13f);
            return TouchFitGrip(left);
        }

        /// Degrees the fist axis (a held sword's blade) points above the frame's forward (Touch: the fitted glove's).
        public static float GripPitchUp => Type == "knuckles" ? 15.4f : 20.6f - Settings.GloveAngle;

        public static Vector3 TipPos(bool left)
        {
            if (Type == "knuckles") return Vector3.zero;
            return new Vector3(left ? 0.0167f : -0.0167f, -0.0252f, -0.0247f);
        }

        /// Degrees the pointing direction is below the frame's forward.
        public static float TipPitchDown => Type == "knuckles" ? 0f : 37.4f;

        public static void Update(CVRSystem sys)
        {
            if (sys == null || Time.unscaledTime < s_nextQuery) return;
            s_nextQuery = Time.unscaledTime + 5f;
            if (Forced != null) { Type = Forced; return; }
            uint idx = sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);
            if (idx == OpenVR.k_unTrackedDeviceIndexInvalid) idx = sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
            if (idx == OpenVR.k_unTrackedDeviceIndexInvalid) return;
            var sb = new StringBuilder(64);
            ETrackedPropertyError err = ETrackedPropertyError.TrackedProp_Success;
            sys.GetStringTrackedDeviceProperty(idx, ETrackedDeviceProperty.Prop_ControllerType_String, sb, 64, ref err);
            string t = err == ETrackedPropertyError.TrackedProp_Success ? sb.ToString() : "";
            if (t != Type)
            {
                Type = t;
                Plugin.L.LogInfo("controllers: type '" + Type + "'");
            }
        }
    }
}

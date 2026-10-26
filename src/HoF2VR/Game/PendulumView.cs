using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The pendulum as the game shows it (the third playtest, mark 24: "What's wrong with the pendulum? It's definitely not what
    /// it's supposed to be" - a thin string and a dot, its red zones in pieces). The device's case (its back plate, brass rails,
    /// the sun, the flames) is statically batched with the room: Metronome's move brought only its needle and zones in front, the
    /// case stayed on the table. Now the device stays where it is and the game's own close-up of it - its pendulum camera's view,
    /// only the device (the clip planes round it, nothing behind) - is shown on a window in front of the player, PendulumView.Ahead
    /// ahead and Down below the eyes the player stands with (fixed while it swings; never with the head). The swing, its zones and
    /// the stops (slap, grip, trigger) are the game's and the mod's as before. `pendview 0|1` (off: Metronome's move as before).
    internal class PendulumView : MonoBehaviour
    {
        public static bool On = true;
        public static float Ahead = 0.62f, Down = 0.14f, Width = 0.42f, Depth = 0.13f;
        public static string Last = "-";
        public static int Shows;
        Camera m_cam;
        RenderTexture m_rt;
        GameObject m_quad;
        Material m_mat;
        bool m_shown;
        Vector3 m_at;
        Quaternion m_rot;

        void Awake()
        {
            Diag.Commands["pendview"] = a =>
            {
                if (a.Length > 1 && (a[1] == "0" || a[1] == "1")) On = a[1] == "1";
                return "pendulum view " + (On ? "on" : "off") + " | shown " + m_shown + " | shows " + Shows + " | " + Last;
            };
        }

        static bool Wanted()
        {
            if (!On || !XRSettings.enabled || !Settings.PendulumToPlayer || PendulumTask.RunningTask == null) return false;
            if (CardTable.Instance == null || CardTable.Instance.Pendulum == null || CardCamera.Instance == null || CardCamera.Instance.PendulumCamera == null) return false;
            return Places.AtCaravan && !CombatVR.Active && VRRig.I != null && VRRig.I.HasStation && !VRFade.IsBlack;
        }

        void LateUpdate()
        {
            if (!Wanted()) { Hide(); return; }
            FixedCamera fc = CardCamera.Instance.PendulumCamera;
            Camera src = fc.Camera;
            if (src == null) { Hide(); return; }
            if (m_cam == null) Build();
            // the shot as the game frames it: its pivot's turn (the child camera's own rotation carries the headset's), the child's
            // place under it
            m_cam.CopyFrom(src);
            m_cam.stereoTargetEye = StereoTargetEyeMask.None;
            m_cam.targetTexture = m_rt;
            m_cam.enabled = false;
            m_cam.clearFlags = CameraClearFlags.SolidColor;
            m_cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            Vector3 pos = fc.transform.TransformPoint(src.transform.localPosition);
            Quaternion rot = fc.transform.rotation;
            m_cam.transform.SetPositionAndRotation(pos, rot);
            // only the device: the clip planes round its middle
            Transform dev = CardTable.Instance.Pendulum.transform;
            float d = Mathf.Max(0.05f, Vector3.Dot(dev.position - pos, rot * Vector3.forward));
            m_cam.nearClipPlane = Mathf.Max(0.01f, d - Depth);
            m_cam.farClipPlane = d + Depth;
            m_cam.aspect = (float)m_rt.width / m_rt.height;
            m_cam.Render();
            if (!m_shown)
            {
                // the window's place, fixed while it swings: from the eyes the player stands with
                VRRig rig = VRRig.I;
                float sc = rig.Scale;
                Vector3 eye = rig.NeutralEye;
                m_at = eye + rig.NeutralRot * (new Vector3(0f, -Down, Ahead) * sc);
                m_rot = Quaternion.LookRotation(m_at - eye, Vector3.up);
                float w = Width * sc;
                m_quad.transform.localScale = new Vector3(w, w * m_rt.height / m_rt.width, 1f);
                m_quad.transform.SetPositionAndRotation(m_at, m_rot);
                m_quad.SetActive(true);
                m_shown = true;
                Shows++;
                Last = string.Format(System.Globalization.CultureInfo.InvariantCulture, "the game's close-up {0:0.00} m ahead, {1:0.00} m below the eyes, {2:0.00} m wide (the camera {3:0.00} m from the device, clip {4:0.00}-{5:0.00})",
                                     Ahead, Down, Width, d, m_cam.nearClipPlane, m_cam.farClipPlane);
                Plugin.L.LogInfo("pendulum view: " + Last);
            }
        }

        void Build()
        {
            var go = new GameObject("HoF2VR_PendulumCam");
            DontDestroyOnLoad(go);
            m_cam = go.AddComponent<Camera>();
            m_cam.enabled = false;
            m_rt = new RenderTexture(768, 480, 24, RenderTextureFormat.ARGB32) { name = "HoF2VR_PendulumView", antiAliasing = 4 };
            m_quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(m_quad.GetComponent<Collider>());
            m_quad.name = "HoF2VR_PendulumWindow";
            m_quad.layer = VRUI.Layer;
            DontDestroyOnLoad(m_quad);
            // premultiplied over the room: the cleared background (alpha 0) leaves it as it is, the device shows opaque
            m_mat = new Material(Shader.Find("Unlit/Premultiplied Colored")) { mainTexture = m_rt, renderQueue = 5104 };
            var mr = m_quad.GetComponent<MeshRenderer>();
            mr.sharedMaterial = m_mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            m_quad.SetActive(false);
        }

        void Hide()
        {
            if (!m_shown) return;
            m_shown = false;
            if (m_quad != null) m_quad.SetActive(false);
        }

        /// The window is shown (Metronome then leaves the device on the table).
        public static bool Showing => On;
    }
}

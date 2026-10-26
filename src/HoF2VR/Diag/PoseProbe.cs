using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace HoF2VR
{
    /// `poselog [frames]` - per frame: head yaw as the mod latched it (VRInput, Update time), as Unity
    /// reports it just before rendering (Application.onBeforeRender), in the first camera's
    /// onPreCull, and the HMD render pose of the runtime's last WaitGetPoses. Shows which pose the
    /// eyes are drawn with versus the one the frame is submitted with.
    internal class PoseProbe : MonoBehaviour
    {
        CVRCompositor m_comp;
        readonly TrackedDevicePose_t[] m_render = new TrackedDevicePose_t[16];
        readonly TrackedDevicePose_t[] m_game = new TrackedDevicePose_t[16];
        int m_left;
        readonly StringBuilder m_sb = new StringBuilder();
        float m_beforeRenderYaw, m_preCullYaw, m_autoYaw = float.NaN;
        int m_preCullFrame = -1;
        Camera m_auto;   // a do-nothing camera Unity tracks itself, to compare against

        void Awake()
        {
            Diag.Commands["poselog"] = a =>
            {
                m_left = a.Length > 1 ? int.Parse(a[1]) : 60;
                m_sb.Length = 0;
                m_sb.Append("frame  t(ms)  latched  beforeRender  firstGameCamYaw  wgpRender  wgpGame  unityAutoCam\n");
                if (m_auto == null)
                {
                    var go = new GameObject("HoF2VR_PoseProbe");
                    DontDestroyOnLoad(go);
                    m_auto = go.AddComponent<Camera>();
                    m_auto.cullingMask = 0;
                    m_auto.clearFlags = CameraClearFlags.Nothing;
                    m_auto.depth = -100;
                    m_auto.allowMSAA = false;
                    m_auto.allowHDR = false;
                }
                m_auto.enabled = true;
                return "logging " + m_left + " frames";
            };
            Application.onBeforeRender += BeforeRender;
            Camera.onPreCull += PreCull;
        }

        void OnDestroy()
        {
            Application.onBeforeRender -= BeforeRender;
            Camera.onPreCull -= PreCull;
        }

        static float Yaw(Quaternion q) => q.eulerAngles.y;

        static float HeadYawNow()
        {
            var states = new List<XRNodeState>();
            InputTracking.GetNodeStates(states);
            foreach (XRNodeState s in states)
            {
                Quaternion r;
                if (s.nodeType == XRNode.Head && s.TryGetRotation(out r)) return Yaw(r);
            }
            return float.NaN;
        }

        void BeforeRender()
        {
            if (m_left <= 0) return;
            m_beforeRenderYaw = HeadYawNow();
        }

        void PreCull(Camera cam)
        {
            if (m_left > 0 && cam == m_auto) m_autoYaw = Yaw(cam.transform.rotation);
            if (m_left <= 0 || m_preCullFrame == Time.frameCount || cam.stereoTargetEye == StereoTargetEyeMask.None || cam == m_auto) return;
            m_preCullFrame = Time.frameCount;
            // what the first game camera actually renders with (its world yaw)
            m_preCullYaw = Yaw(cam.transform.rotation);
        }

        static float PoseYaw(TrackedDevicePose_t p)
        {
            HmdMatrix34_t m = p.mDeviceToAbsoluteTracking;
            // OpenVR: right-handed, -Z forward; yaw of the forward vector
            float fx = -m.m2, fz = -m.m10;
            float yaw = Mathf.Atan2(fx, -fz) * Mathf.Rad2Deg;
            return (yaw + 360f) % 360f;
        }

        void LateUpdate()
        {
            if (m_left <= 0) return;
            if (m_comp == null)
            {
                EVRInitError err = EVRInitError.None;
                IntPtr p = OpenVRInterop.GetGenericInterface("FnTable:" + OpenVR.IVRCompositor_Version, ref err);
                if (p != IntPtr.Zero) m_comp = new CVRCompositor(p);
            }
            // previous frame's values are complete now
            float wr = float.NaN, wg = float.NaN;
            if (m_comp != null && m_comp.GetLastPoses(m_render, m_game) == EVRCompositorError.None)
            {
                wr = PoseYaw(m_render[0]);
                wg = PoseYaw(m_game[0]);
            }
            m_sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0} {1:0.0} {2:0.00} {3:0.00} {4:0.00} {5:0.00} {6:0.00} {7:0.00}\n",
                Time.frameCount, Time.realtimeSinceStartup * 1000f, Yaw(VRInput.HeadRot), m_beforeRenderYaw, m_preCullYaw, wr, wg, m_autoYaw);
            if (--m_left == 0)
            {
                Plugin.L.LogInfo("poselog:\n" + m_sb);
                if (m_auto != null) m_auto.enabled = false;
            }
        }
    }
}

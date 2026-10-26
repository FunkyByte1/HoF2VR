using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Works around a Unity 2017.4 engine crash in multi-pass stereo (VR only).
    ///
    /// Terrains draw their patches (and grass) as per-camera "intermediate renderers", added during
    /// every culling pass - in multi-pass stereo once for each eye, into the same per-camera list.
    /// When the right eye's additions outgrow the list it is reallocated while the left eye's
    /// render-queue extraction job still reads the old block, and ExecuteRenderQueueJob dereferences
    /// freed memory. Core dumps of every crash show exactly that (list 3 = camera intermediate
    /// renderers, memory already reused for bounding boxes). It strikes in the first frames after a
    /// level with terrain loads, when a fresh camera's list is still small.
    ///
    /// Fix: the first time a stereo camera is about to cull, queue a few thousand invisible dummy
    /// meshes for that camera only (Graphics.DrawMesh with a camera, far outside any frustum). The
    /// list then grows big at a safe moment and keeps that capacity, so the eyes never reallocate it.
    internal class CullingGuard : MonoBehaviour
    {
        const int Dummies = 4096;
        static float Refresh = 10f;   // test: `cullguard refresh S`
        readonly Dictionary<int, float> m_grown = new Dictionary<int, float>();
        readonly Dictionary<int, int> m_lastFrame = new Dictionary<int, int>();
        Mesh m_mesh;
        Material m_mat;
        Matrix4x4 m_far;
        int m_count;
        /// Frame of the latest list growth (perf telemetry marks missed frames right after one).
        public static int LastGrowFrame = -100;

        /// HOF2VR_CULLFIX=0 turns the workaround off (for A/B crash tests only).
        static readonly bool s_enabled = System.Environment.GetEnvironmentVariable("HOF2VR_CULLFIX") != "0";

        void Awake()
        {
            if (!s_enabled) { Plugin.L.LogWarning("CullingGuard disabled (HOF2VR_CULLFIX=0)"); return; }
            Camera.onPreCull += PreCull;
            Diag.StatusExtras["cullguard"] = () => Diag.Q(m_count + " cameras");
            Diag.Commands["cullguard"] = a =>
            {
                if (a.Length > 2 && a[1] == "refresh") Refresh = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                return "cullguard: " + m_count + " cameras, refresh every " + Refresh + " s, last growth frame " + LastGrowFrame + " (now " + Time.frameCount + ")";
            };
        }

        void OnDestroy() { Camera.onPreCull -= PreCull; }

        void PreCull(Camera cam)
        {
            if (!XRSettings.enabled || cam.stereoTargetEye == StereoTargetEyeMask.None || cam.targetTexture != null) return;
            // once per camera, and again every few seconds in case the engine ever trims the list -
            // always before the first eye of a frame (growing it between the eyes is the crash)
            float last;
            int frame;
            int id = cam.GetInstanceID();
            bool secondEye = m_lastFrame.TryGetValue(id, out frame) && frame == Time.frameCount;
            m_lastFrame[id] = Time.frameCount;
            if (secondEye) return;
            if (m_grown.TryGetValue(id, out last) && Time.unscaledTime - last < Refresh) return;
            bool first = !m_grown.ContainsKey(id);
            m_grown[id] = Time.unscaledTime;
            if (m_mesh == null)
            {
                m_mesh = new Mesh { name = "HoF2VR_CullingGuard" };
                m_mesh.vertices = new[] { Vector3.zero, Vector3.right * 0.001f, Vector3.up * 0.001f };
                m_mesh.triangles = new[] { 0, 1, 2 };
                m_mesh.RecalculateBounds();
                m_mat = new Material(Shader.Find("Sprites/Default"));
                m_far = Matrix4x4.TRS(new Vector3(0f, -1e6f, 0f), Quaternion.identity, Vector3.one);
            }
            LastGrowFrame = Time.frameCount;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < Dummies; i++)
                Graphics.DrawMesh(m_mesh, m_far, m_mat, 0, cam, 0, null, false, false);
            if (!first) return;
            m_count++;
            Plugin.L.LogInfo("CullingGuard: pre-grew intermediate list of '" + cam.name + "' (" + Dummies + ", " +
                             sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms)");
        }
    }
}

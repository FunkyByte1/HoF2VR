using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Brightness of what each eye was shown, frame by frame (test mode): finds flicker. "There is still some flickering
    /// when I look around" (user, title, 2026-10-11 evening) - a frame whose brightness jumps against both neighbours
    /// while the head turns smoothly, or one eye differing from the other.
    ///   lumalog <tag> <frames>   for the next frames: per eye the mean luminance of the final eye image (after every
    ///                            camera, overlay included) and its brightness in 16x16 fixed view directions (tangents
    ///                            -0.85..0.85 both ways, i.e. +-40 degrees, inside both eyes' fields: each eye's own
    ///                            projection maps them, so left and right compare the same direction), plus the head's
    ///                            yaw/pitch -> <test dir>/luma/<tag>.csv (scripts/flicker.py reads it)
    ///   lumalog off              stop early
    /// A last camera (depth 1001) reads the eye texture in its post-render: halving blits down to ~1/32, one small
    /// read-back per eye (costs ~1-2 ms a frame while it runs).
    internal class LumaProbe : MonoBehaviour
    {
        const int Grid = 16;
        const float Span = 0.85f;
        static LumaProbe I;
        Camera m_cam;
        StreamWriter m_out;
        FileStream m_raw;   // `raw`: every eye's downsampled image (RGB24, rows bottom-up), w/h in the log line
        int m_left;
        string m_tag;
        Texture2D m_read;
        readonly float[] m_cells = new float[Grid * Grid];
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        void Awake()
        {
            I = this;
            Diag.Commands["lumalog"] = a =>
            {
                if (a.Length > 1 && a[1] == "off") { Stop(); return "lumalog off"; }
                if (!XRSettings.enabled) return "no XR";
                string tag = a.Length > 1 ? a[1] : "luma";
                int frames = a.Length > 2 ? int.Parse(a[2]) : 300;
                string r = Begin(tag, frames);
                if (a.Length > 3 && a[3] == "raw") m_raw = new FileStream(Path.Combine(Path.Combine(TestMode.Dir, "luma"), tag + ".raw"), FileMode.Create);
                return r;
            };
            Camera.onPostRender += OnPost;
        }

        void OnDestroy() { Camera.onPostRender -= OnPost; Stop(); }

        string Begin(string tag, int frames)
        {
            Stop();
            string dir = Path.Combine(TestMode.Dir, "luma");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, tag + ".csv");
            m_out = new StreamWriter(path, false, Encoding.ASCII);
            var h = new StringBuilder("frame,t,eye,yaw,pitch,mean");
            for (int i = 0; i < Grid * Grid; i++) h.Append(",c").Append(i);
            m_out.WriteLine(h.ToString());
            m_left = frames;
            m_tag = tag;
            if (m_cam == null)
            {
                var go = new GameObject("HoF2VR_Luma");
                DontDestroyOnLoad(go);
                m_cam = go.AddComponent<Camera>();
                m_cam.depth = 1001;
                m_cam.cullingMask = 0;
                m_cam.clearFlags = CameraClearFlags.Nothing;
                m_cam.allowMSAA = false;
                m_cam.allowHDR = false;
                m_cam.stereoTargetEye = StereoTargetEyeMask.Both;
            }
            m_cam.enabled = true;
            Plugin.L.LogInfo("lumalog " + tag + ": " + frames + " frames -> " + path);
            return "logging " + frames + " frames to " + path;
        }

        void Stop()
        {
            if (m_cam != null) m_cam.enabled = false;
            if (m_out != null)
            {
                try { m_out.Flush(); m_out.Close(); } catch (Exception) { }
                m_out = null;
                Plugin.L.LogInfo("lumalog " + m_tag + " done");
            }
            if (m_raw != null) { try { m_raw.Close(); } catch (Exception) { } m_raw = null; }
            m_left = 0;
        }

        byte[] m_rawBuf;
        bool m_rawLogged;
        // flash capture: a right-eye frame much brighter than both its left eye and its previous frame in many directions
        // -> full-size PNGs of it and of the previous right-eye frame (kept on the GPU every frame) + the camera state
        readonly float[] m_leftCells = new float[Grid * Grid], m_prevRight = new float[Grid * Grid];
        int m_leftFrame = -1, m_prevRightFrame = -1, m_flashes;
        RenderTexture m_prevRightRT;
        public static int FlashMin = 40, FlashMax = 3;

        void CaptureFlash(RenderTexture src, int n)
        {
            m_flashes++;
            string dir = Path.Combine(TestMode.Dir, "luma");
            string name = m_tag + "_flash" + m_flashes + "_f" + Time.frameCount;
            // the eye texture through the same blit as the kept previous frame (a direct read of the eye texture
            // gives its raw encoding, the blit copy the converted one: compare like with like)
            RenderTexture nowRT = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, nowRT);
            foreach (var pair in new[] { new { rt = nowRT, tag = "now" }, new { rt = m_prevRightRT, tag = "prev" } })
            {
                RenderTexture.active = pair.rt;
                var t = new Texture2D(pair.rt.width, pair.rt.height, TextureFormat.RGB24, false);
                t.ReadPixels(new Rect(0, 0, pair.rt.width, pair.rt.height), 0, 0, false);
                File.WriteAllBytes(Path.Combine(dir, name + "_" + pair.tag + ".png"), t.EncodeToPNG());
                Destroy(t);
            }
            RenderTexture.ReleaseTemporary(nowRT);
            RenderTexture.active = src;
            var sb = new StringBuilder("lumalog FLASH " + name + ": right eye brighter than its left eye and its last frame in " + n + " of " + (Grid * Grid) + " directions");
            Camera prim = VRRig.I != null ? VRRig.I.PrimaryCamera : null;
            if (prim != null)
            {
                Matrix4x4 l = prim.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse, r = prim.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse;
                sb.Append("\n  primary '").Append(prim.name).Append("' transform ").Append(Diag.V(prim.transform.position)).Append(" rot ").Append(Diag.V(prim.transform.eulerAngles))
                  .Append("\n  left eye ").Append(Diag.V(l.GetColumn(3))).Append(" fwd ").Append(Diag.V(-(Vector3)l.GetColumn(2)))
                  .Append(" right eye ").Append(Diag.V(r.GetColumn(3))).Append(" fwd ").Append(Diag.V(-(Vector3)r.GetColumn(2)))
                  .Append("\n  worldToCamera pos ").Append(Diag.V(prim.cameraToWorldMatrix.GetColumn(3)))
                  .Append(" late pose frames ").Append(VRRig.I.LatePoseFrames);
            }
            Plugin.L.LogInfo(sb.ToString());
        }

        static float Lum(Color32 c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        void LateUpdate()
        {
            if (m_out != null && m_left <= 0) Stop();
        }

        void OnPost(Camera cam)
        {
            if (m_out == null || cam != m_cam) return;
            RenderTexture src = RenderTexture.active;
            if (src == null) return;
            int eye = cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? 1 : 0;
            try
            {
                // halving blits (bilinear = a 2x2 box each) down to <= 128 px wide
                RenderTexture cur = src;
                int w = src.width, h = src.height;
                RenderTexture tmp = null;
                while (w > 128)
                {
                    w = Mathf.Max(1, w / 2);
                    h = Mathf.Max(1, h / 2);
                    RenderTexture next = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                    next.filterMode = FilterMode.Bilinear;
                    Graphics.Blit(cur, next);
                    if (tmp != null) RenderTexture.ReleaseTemporary(tmp);
                    tmp = next;
                    cur = next;
                }
                RenderTexture.active = cur;
                if (m_read == null || m_read.width != w || m_read.height != h)
                {
                    if (m_read != null) Destroy(m_read);
                    m_read = new Texture2D(w, h, TextureFormat.RGB24, false);
                }
                m_read.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                Color32[] px = m_read.GetPixels32();
                if (m_raw != null)
                {
                    if (m_rawBuf == null || m_rawBuf.Length != px.Length * 3) m_rawBuf = new byte[px.Length * 3];
                    for (int i = 0; i < px.Length; i++) { m_rawBuf[i * 3] = px[i].r; m_rawBuf[i * 3 + 1] = px[i].g; m_rawBuf[i * 3 + 2] = px[i].b; }
                    m_raw.Write(m_rawBuf, 0, m_rawBuf.Length);
                    if (!m_rawLogged) { m_rawLogged = true; Plugin.L.LogInfo("lumalog raw: " + w + "x" + h + " RGB24 per eye, left then right"); }
                }
                if (tmp != null) RenderTexture.ReleaseTemporary(tmp);
                RenderTexture.active = src;
                double sum = 0;
                for (int i = 0; i < px.Length; i++) sum += Lum(px[i]);
                // the fixed view directions, through this eye's projection (ndc.x = P00 u - P02, ndc.y = P11 v - P12 for
                // the view-space direction (u, v, -1)); each sample is the 3x3 neighbourhood there
                Matrix4x4 P = cam.stereoEnabled ? cam.GetStereoProjectionMatrix(eye == 1 ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left) : cam.projectionMatrix;
                for (int gy = 0; gy < Grid; gy++)
                    for (int gx = 0; gx < Grid; gx++)
                    {
                        float u = -Span + 2f * Span * (gx + 0.5f) / Grid, v = Span - 2f * Span * (gy + 0.5f) / Grid;   // row 0 = top
                        float nx = P.m00 * u - P.m02, ny = P.m11 * v - P.m12;
                        int cx = Mathf.Clamp(Mathf.RoundToInt((nx + 1f) * 0.5f * w), 1, w - 2), cy = Mathf.Clamp(Mathf.RoundToInt((ny + 1f) * 0.5f * h), 1, h - 2);
                        float acc = 0f;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++) acc += Lum(px[(cy + dy) * w + cx + dx]);
                        m_cells[gy * Grid + gx] = acc / 9f;
                    }
                if (eye == 0) { Array.Copy(m_cells, m_leftCells, m_cells.Length); m_leftFrame = Time.frameCount; }
                else
                {
                    if (m_leftFrame == Time.frameCount && m_prevRightFrame == Time.frameCount - 1 && m_flashes < FlashMax)
                    {
                        int n = 0;
                        for (int i = 0; i < m_cells.Length; i++)
                            if (m_cells[i] - m_leftCells[i] > 10f && m_cells[i] - m_prevRight[i] > 10f) n++;
                        if (n >= FlashMin) CaptureFlash(src, n);
                    }
                    Array.Copy(m_cells, m_prevRight, m_cells.Length);
                    m_prevRightFrame = Time.frameCount;
                    if (m_prevRightRT == null || m_prevRightRT.width != src.width || m_prevRightRT.height != src.height)
                    {
                        if (m_prevRightRT != null) m_prevRightRT.Release();
                        m_prevRightRT = new RenderTexture(src.width, src.height, 0, RenderTextureFormat.ARGB32);
                    }
                    Graphics.Blit(src, m_prevRightRT);
                    RenderTexture.active = src;
                }
                Vector3 e = VRInput.HeadRot.eulerAngles;
                var sb = new StringBuilder();
                sb.Append(Time.frameCount).Append(',').Append(Time.unscaledTime.ToString("0.000", Inv)).Append(',').Append(eye).Append(',')
                  .Append(e.y.ToString("0.00", Inv)).Append(',').Append((e.x > 180f ? 360f - e.x : -e.x).ToString("0.00", Inv)).Append(',')
                  .Append((sum / (w * h)).ToString("0.00", Inv));
                for (int i = 0; i < m_cells.Length; i++) sb.Append(',').Append(m_cells[i].ToString("0.0", Inv));
                m_out.WriteLine(sb.ToString());
                if (eye == 1 || !cam.stereoEnabled) m_left--;
            }
            catch (Exception ex)
            {
                RenderTexture.active = src;
                Plugin.L.LogWarning("lumalog: " + ex.Message);
                Stop();
            }
        }
    }
}

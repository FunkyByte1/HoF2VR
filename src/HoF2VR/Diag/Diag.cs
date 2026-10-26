using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Test/diagnostic channel. In test mode it writes <dir>/status.json once a second and runs
    /// commands written to <dir>/cmd/command.txt (one per line; the file is deleted once read).
    /// Results are appended to <dir>/cmd/results.log as "<seq> <command> => <result>".
    internal class Diag : MonoBehaviour
    {
        public static Diag I;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        float m_nextStatus;
        float m_nextPoll;
        int m_seq;
        float m_fpsAccum;
        int m_fpsFrames;
        float m_fps;
        float m_fpsT0;
        string m_lastXrSummary;
        float m_nextXr;

        public static readonly Dictionary<string, Func<string[], string>> Commands =
            new Dictionary<string, Func<string[], string>>(StringComparer.OrdinalIgnoreCase);

        void Awake()
        {
            I = this;
            Commands["state"] = a => { WriteStatus(); return "ok"; };
            Commands["quit"] = a => { Application.Quit(); return "quitting"; };
            // `timescale <v> [hold]`: hold (with 0, test runs) stops the fight's clock too (FightClock.Held)
            Commands["timescale"] = a =>
            {
                Time.timeScale = F(a[1]);
                FightClock.Held = TestMode.Enabled && Time.timeScale == 0f && a.Length > 2 && a[2] == "hold";
                return "timescale=" + Time.timeScale + (FightClock.Held ? " (the fight clock held)" : "");
            };
            Commands["cams"] = a => DescribeCameras();
            Commands["shot"] = a => { StartCoroutine(CaptureEyes(a.Length > 1 ? a[1] : "shot")); return "capturing"; };
            Commands["shot2"] = a => { StartCoroutine(Capture(a.Length > 1 ? a[1] : "shot")); return "capturing (re-render)"; };
            Camera.onPostRender += OnAnyPostRender;
            // `camlog <frames>`: every camera that renders in the next frames, in order (also armed for a
            // few frames at each state change while `stateshot` is on)
            Commands["camlog"] = a => { CamLogFrames = a.Length > 1 ? int.Parse(a[1]) : 5; return "logging cameras for " + CamLogFrames + " frames"; };
            Camera.onPreCull += cam =>
            {
                if (CamLogFrames <= 0) return;
                Plugin.L.LogInfo(string.Format(Inv, "camlog f{0} '{1}' depth {2} {3} target {4} rt {5} at {6} fade {7}{8}", Time.frameCount, cam.name, cam.depth,
                    cam.stereoEnabled ? cam.stereoActiveEye.ToString() : "mono", cam.targetTexture ? cam.targetTexture.name : "-",
                    RenderTexture.active ? RenderTexture.active.name : "-", V(cam.transform.position),
                    VRFade.I != null ? (VRFade.IsBlack ? "black" : "not black") : "-", cam.enabled ? "" : " (disabled cam)"));
            };
            Commands["xr"] = a => XrCommand(a);
            Commands["log"] = a => { Plugin.L.LogInfo("[note] " + string.Join(" ", a.Skip(1).ToArray())); return "ok"; };
            Commands["scenes"] = a => DescribeScenes();
            Commands["camstress"] = a => { StartCoroutine(CamStress(int.Parse(a[1]), a.Length > 2 && a[2] == "safe")); return "stressing"; };
            Commands["frametimes"] = a => { StartCoroutine(FrameTimes(a.Length > 1 ? int.Parse(a[1]) : 450)); return "sampling"; };
            Commands["tstress"] = a => { StartCoroutine(TerrainStress(int.Parse(a[1]), a.Length > 2 && a[2] == "split")); return "stressing"; };
            Commands["shader"] = a => { string n = string.Join(" ", a.Skip(1).ToArray()); return n + ": " + (Shader.Find(n) != null ? "found" : "missing"); };
            // `userfirst [check <unix-socket-table>]`: the test instance's watch for the user (it quits when they
            // need the machine); `check` runs it once more, optionally on a stand-in for /proc/net/unix
            Commands["userfirst"] = a => a.Length > 1 && a[1] == "check"
                ? "check: " + (TestMode.UserWantsTheMachine(a.Length > 2 ? a[2] : null) ?? "clear")
                : "last: " + TestMode.LastCheck;
            SceneManager.sceneLoaded += (s, m) => Plugin.L.LogInfo("scene loaded: " + s.name + " (" + m + ")");
        }

        static float F(string s) => float.Parse(s, Inv);

        void OnApplicationQuit() => TestMode.OnQuit();

        /// Crash probe: create cameras, disable XR auto-tracking on them, render a frame, destroy them.
        /// With "safe" the tracking is re-enabled before destruction.
        IEnumerator CamStress(int n, bool safe)
        {
            for (int i = 0; i < n; i++)
            {
                var go = new GameObject("HoF2VR_stress_" + i);
                Camera c = go.AddComponent<Camera>();
                c.depth = -50;
                c.cullingMask = 0;
                c.clearFlags = CameraClearFlags.Nothing;
                XRDevice.DisableAutoXRCameraTracking(c, true);
                yield return null;
                yield return null;
                if (safe) XRDevice.DisableAutoXRCameraTracking(c, false);
                Destroy(go);
                yield return null;
            }
            Plugin.L.LogInfo("camstress done (" + n + ", safe=" + safe + ")");
        }

        /// Frame-time histogram (ms buckets) of the next n frames -> log.
        IEnumerator FrameTimes(int n)
        {
            var buckets = new SortedDictionary<int, int>();
            float sum = 0f, max = 0f;
            for (int i = 0; i < n; i++)
            {
                yield return null;
                float ms = Time.unscaledDeltaTime * 1000f;
                sum += ms;
                if (ms > max) max = ms;
                int b = Mathf.RoundToInt(ms);
                int c;
                buckets.TryGetValue(b, out c);
                buckets[b] = c + 1;
            }
            var sb = new StringBuilder("frame times over " + n + " frames: avg " + (sum / n).ToString("0.00", Inv) + " ms, max " + max.ToString("0.0", Inv) + " ms\n");
            foreach (var kv in buckets) sb.Append("  ").Append(kv.Key).Append(" ms: ").Append(kv.Value).Append('\n');
            Plugin.L.LogInfo(sb.ToString());
        }

        /// Crash probe for the multi-pass culling race: short-lived stereo cameras that copy the main
        /// camera and render the scene for a frame or two. With "split" the left eye looks at the sky
        /// and the right eye at the ground, so the second eye's culling collects far more nodes.
        IEnumerator TerrainStress(int n, bool split)
        {
            Camera src = VRRig.I != null ? VRRig.I.PrimaryCamera : null;
            if (src == null) { Plugin.L.LogWarning("tstress: no primary camera"); yield break; }
            for (int i = 0; i < n; i++)
            {
                var go = new GameObject("HoF2VR_tstress_" + i);
                go.transform.position = src.transform.position;
                go.transform.rotation = src.transform.rotation;
                Camera c = go.AddComponent<Camera>();
                c.CopyFrom(src);
                c.depth = src.depth - 1;
                if (split)
                {
                    XRDevice.DisableAutoXRCameraTracking(c, true);
                    Vector3 p = src.transform.position;
                    Matrix4x4 sky = Matrix4x4.TRS(p, Quaternion.LookRotation(Vector3.up, Vector3.forward), Vector3.one).inverse;
                    Matrix4x4 ground = Matrix4x4.TRS(p + Vector3.up * 40f, Quaternion.LookRotation(Vector3.down, Vector3.forward), Vector3.one).inverse;
                    sky.SetRow(2, -sky.GetRow(2));
                    ground.SetRow(2, -ground.GetRow(2));
                    c.SetStereoViewMatrix(Camera.StereoscopicEye.Left, sky);
                    c.SetStereoViewMatrix(Camera.StereoscopicEye.Right, ground);
                }
                yield return null;
                yield return null;
                if (split)
                {
                    c.ResetStereoViewMatrices();
                    XRDevice.DisableAutoXRCameraTracking(c, false);
                }
                Destroy(go);
                yield return null;
                if (i % 20 == 0) Plugin.L.LogInfo("tstress " + i + "/" + n);
            }
            Plugin.L.LogInfo("tstress done (" + n + ", split=" + split + ")");
        }

        static readonly bool s_testSound = Environment.GetEnvironmentVariable("HOF2VR_SOUND") == "1";
        /// `hrtf sound 1` (Hrtf.cs): a silent test run makes sound from now on - only after the harness has checked that the
        /// game's audio stream is on a test sink nobody hears (scripts/hrtf_test.py).
        public static bool SoundUnlocked;

        public static int CamLogFrames;
        int m_camLogFrame = -1;

        static bool s_occlusionSet;

        void Update()
        {
            ResetGuard.Tick();
            EquipAsk.Tick();
            if (CamLogFrames > 0 && m_camLogFrame != Time.frameCount) { m_camLogFrame = Time.frameCount; CamLogFrames--; }
            // Test runs are silent (the user may be at the PC). The game sets AudioListener.volume
            // from its own settings, so keep overriding it; nothing is saved.
            if (TestMode.Enabled && !s_testSound && !SoundUnlocked) AudioListener.volume = 0f;
            // Test runs draw no hidden-area mask into the eye images (2026-10-13, the close-up pictures' "two big black
            // triangles"): the simulated headset's mask is made for its own field of view, not the Quest-like one xrsim sets,
            // so it cut black triangles into the visible picture. The real headset's mask lies outside its lenses.
            if (TestMode.Enabled && !s_occlusionSet && XRSettings.enabled)
            {
                s_occlusionSet = true;
                XRSettings.useOcclusionMesh = false;
                Plugin.L.LogInfo("test run: the headset's hidden-area mask is off (the simulator's does not fit its field of view)");
            }
            m_fpsFrames++;
            if (Time.unscaledTime - m_fpsT0 >= 1f)
            {
                m_fps = m_fpsFrames / (Time.unscaledTime - m_fpsT0);
                m_fpsFrames = 0;
                m_fpsT0 = Time.unscaledTime;
            }
            if (Time.unscaledTime >= m_nextXr)   // (a formatted string: not every frame)
            {
                m_nextXr = Time.unscaledTime + 1f;
                string xr = XrSummary();
                if (xr != m_lastXrSummary)
                {
                    m_lastXrSummary = xr;
                    Plugin.L.LogInfo("XR: " + xr);
                }
            }
            if (!TestMode.Enabled) return;
            TestMode.Tick();   // a test instance steps aside for the user
            if (Time.unscaledTime >= m_nextStatus)
            {
                m_nextStatus = Time.unscaledTime + 1f;
                WriteStatus();
            }
            if (Time.unscaledTime >= m_nextPoll)
            {
                m_nextPoll = Time.unscaledTime + 0.1f;
                PollCommands();
            }
        }

        static string XrSummary()
        {
            return string.Format(Inv, "enabled={0} device='{1}' present={2} model='{3}' active={4} eye={5}x{6} hz={7:0.#}",
                XRSettings.enabled, XRSettings.loadedDeviceName, XRDevice.isPresent, XRDevice.model,
                XRSettings.isDeviceActive, XRSettings.eyeTextureWidth, XRSettings.eyeTextureHeight, XRDevice.refreshRate);
        }

        void PollCommands()
        {
            string path = Path.Combine(TestMode.Cmd, "command.txt");
            if (!File.Exists(path)) return;
            string[] lines;
            try
            {
                // moved aside first: a sender that writes a new command.txt meanwhile is never deleted unread
                string reading = Path.Combine(TestMode.Cmd, "command.reading");
                if (File.Exists(reading)) File.Delete(reading);
                File.Move(path, reading);
                lines = File.ReadAllLines(reading);
                File.Delete(reading);
            }
            catch (Exception) { return; }
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                Run(line);
            }
        }

        public string Run(string line)
        {
            string[] a = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string result;
            try
            {
                Func<string[], string> fn;
                result = Commands.TryGetValue(a[0], out fn) ? fn(a) : "unknown command";
            }
            catch (Exception e) { result = "error: " + e.GetType().Name + ": " + e.Message; }
            m_seq++;
            string entry = m_seq + " " + line + " => " + result;
            Plugin.L.LogInfo("[cmd] " + entry);
            if (TestMode.Enabled)
                File.AppendAllText(Path.Combine(TestMode.Cmd, "results.log"), entry + "\n");
            return result;
        }

        string XrCommand(string[] a)
        {
            if (a.Length >= 3 && a[1] == "load") { XRSettings.LoadDeviceByName(a[2]); return "requested " + a[2]; }
            if (a.Length >= 3 && a[1] == "enable") { XRSettings.enabled = a[2] == "1"; return "enabled=" + XRSettings.enabled; }
            if (a.Length >= 3 && a[1] == "scale") { XRSettings.eyeTextureResolutionScale = F(a[2]); return "scale=" + XRSettings.eyeTextureResolutionScale; }
            // `xr occlusion on|off`: the headset's hidden-area mask (the lens's invisible corners, drawn black into the eye images)
            if (a.Length >= 3 && a[1] == "occlusion") { XRSettings.useOcclusionMesh = a[2] == "on"; return "occlusion mesh " + (XRSettings.useOcclusionMesh ? "on" : "off"); }
            return XrSummary() + " supported=[" + string.Join(",", XRSettings.supportedDevices) + "]";
        }

        public static string DescribeCameras()
        {
            var sb = new StringBuilder();
            foreach (Camera c in Camera.allCameras.OrderBy(c => c.depth))
            {
                sb.AppendFormat(Inv, "\n  '{0}' depth={1} enabled={2} ortho={3} eye={4} target={5} mask=0x{6:X} clear={7} fov={8:0.#} near={9} far={10} pos={11} rot={12} parent='{13}'",
                    c.name, c.depth, c.enabled, c.orthographic, c.stereoTargetEye, c.targetTexture ? c.targetTexture.name : "-",
                    c.cullingMask, c.clearFlags, c.fieldOfView, c.nearClipPlane, c.farClipPlane, V(c.transform.position),
                    V(c.transform.eulerAngles), c.transform.parent ? c.transform.parent.name : "-");
                sb.AppendFormat("\n      scale={4} tree={5}\n      path={0} msaa={1} hdr={2} fx=[{3}]", c.actualRenderingPath, c.allowMSAA, c.allowHDR,
                    string.Join(", ", c.GetComponents<MonoBehaviour>().Where(b => b != null)
                        .Select(b => b.GetType().Name + (b.enabled ? "" : "(off)")).ToArray()), V(c.transform.lossyScale), VRUI.Path(c.transform));
            }
            sb.AppendFormat("\n  quality: level={0} msaa={1} vsync={2} shadows={3}", QualitySettings.GetQualityLevel(),
                QualitySettings.antiAliasing, QualitySettings.vSyncCount, QualitySettings.shadows);
            Plugin.L.LogInfo("cameras:" + sb);
            return Camera.allCamerasCount + " cameras (see log)";
        }

        static string DescribeScenes()
        {
            var names = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++) names.Add(SceneManager.GetSceneAt(i).name);
            return "active=" + SceneManager.GetActiveScene().name + " loaded=[" + string.Join(",", names.ToArray()) + "]";
        }

        /// Wall-clock time of day (logs line up with the headset's and the PC's other logs).
        public static string Clock() => DateTime.Now.ToString("HH:mm:ss.fff", Inv);

        public static string V(Vector3 v) => string.Format(Inv, "({0:0.###},{1:0.###},{2:0.###})", v.x, v.y, v.z);

        void WriteStatus()
        {
            var sb = new StringBuilder();
            sb.Append("{");
            Kv(sb, "t", Time.unscaledTime.ToString("0.0", Inv));
            Kv(sb, "frame", Time.frameCount.ToString(Inv));
            Kv(sb, "fps", m_fps.ToString("0.0", Inv));
            Kv(sb, "scene", Q(SceneManager.GetActiveScene().name));
            Kv(sb, "xrEnabled", XRSettings.enabled ? "true" : "false");
            Kv(sb, "xrDevice", Q(XRSettings.loadedDeviceName));
            Kv(sb, "xrPresent", XRDevice.isPresent ? "true" : "false");
            Kv(sb, "eyeW", XRSettings.eyeTextureWidth.ToString(Inv));
            Kv(sb, "eyeH", XRSettings.eyeTextureHeight.ToString(Inv));
            Kv(sb, "head", Q(V(InputTracking.GetLocalPosition(XRNode.Head))));
            Camera main = Camera.main;
            Kv(sb, "mainCam", Q(main ? main.name : ""));
            Kv(sb, "mainCamPos", Q(main ? V(main.transform.position) : ""));
            foreach (var kv in StatusExtras)
            {
                string v;
                try { v = kv.Value(); } catch (Exception e) { v = Q("error " + e.Message); }
                Kv(sb, kv.Key, v);
            }
            sb.Length--;
            sb.Append("}\n");
            string tmp = Path.Combine(TestMode.Dir, "status.json.tmp");
            File.WriteAllText(tmp, sb.ToString());
            string dst = Path.Combine(TestMode.Dir, "status.json");
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);
        }

        /// Other modules add live values here (each returns a JSON value).
        public static readonly Dictionary<string, Func<string>> StatusExtras = new Dictionary<string, Func<string>>();

        static void Kv(StringBuilder sb, string k, string v) { sb.Append('"').Append(k).Append("\":").Append(v).Append(','); }
        public static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // ---- exact capture: copy the real eye textures after the last stereo camera has drawn
        RenderTexture[] m_eyeGrab = new RenderTexture[2];
        bool[] m_eyeGot = new bool[2];
        bool m_grabbing;
        Camera m_lastStereo;

        static Camera LastStereoCamera()
        {
            Camera best = null;
            foreach (Camera c in Camera.allCameras)
            {
                if (!c.enabled || c.targetTexture != null || c.stereoTargetEye == StereoTargetEyeMask.None) continue;
                if (best == null || c.depth >= best.depth) best = c;
            }
            return best;
        }

        Texture2D[] m_eyeRaw = new Texture2D[2];
        string m_grabInfo = "";

        /// Reads the finished eye image straight out of the eye texture (raw bytes, as the runtime gets
        /// them). A shader blit copy came out much too dark in HDR scenes (sRGB handling).
        void OnAnyPostRender(Camera cam)
        {
            if (!m_grabbing || cam != m_lastStereo) return;
            int eye = cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? 1 : 0;
            RenderTexture src = RenderTexture.active;
            if (src == null) return;
            Texture2D t = m_eyeRaw[eye];
            if (t == null || t.width != src.width || t.height != src.height)
            {
                if (t != null) Destroy(t);
                t = m_eyeRaw[eye] = new Texture2D(src.width, src.height, TextureFormat.RGB24, false);
            }
            t.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0, false);
            m_eyeGot[eye] = true;
            m_grabInfo = VRUI.OverlayMarker.LastInfo + " (grab frame " + Time.frameCount + ", fade " + (VRFade.I != null && VRFade.IsBlack ? "black" : "not black") +
                         ", read '" + src.name + "' " + src.width + "x" + src.height + " " + src.format + ")";
        }

        Camera m_grabCam;

        /// A do-nothing stereo camera that draws last (after the overlay), without MSAA or HDR, so in
        /// its OnPostRender the active target is the finished eye texture itself. (Grabbing in the
        /// overlay camera's callback read its MSAA intermediate, which came out far too dark.)
        Camera GrabCamera()
        {
            if (m_grabCam != null) return m_grabCam;
            var go = new GameObject("HoF2VR_Grab");
            DontDestroyOnLoad(go);
            m_grabCam = go.AddComponent<Camera>();
            m_grabCam.depth = 1000;
            m_grabCam.cullingMask = 0;
            m_grabCam.clearFlags = CameraClearFlags.Nothing;
            m_grabCam.allowMSAA = false;
            m_grabCam.allowHDR = false;
            m_grabCam.stereoTargetEye = StereoTargetEyeMask.Both;
            m_grabCam.enabled = false;
            return m_grabCam;
        }

        /// Both eyes into `dir`/`name`_eyes.png (bug marks).
        public IEnumerator CaptureEyesTo(string dir, string name) => CaptureEyes(name, dir);

        /// The bug mark's capture, spread out (N4, playtest 2026-10-17: every mark froze the game 0.15-0.23 s - the desktop
        /// screenshot's PNG, both eyes' full-size pixel copies, the downscale and the eyes' PNG all in one frame). Now: the eyes
        /// are read back in one frame as before (the GPU copy is the moment of the mark); each eye is copied and halved in a frame
        /// of its own; the picture is a JPEG (a fraction of PNG's encoding time) written to disk on a thread; the desktop view is
        /// read and encoded the same way two frames later. Each frame's cost is logged (`bug mark capture: ...`).
        public IEnumerator CaptureEyesQuick(string dir, string name)
        {
            CaptureUntil = Time.unscaledTime + 2.5f;
            if (!XRSettings.enabled) { yield return CaptureEyes(name, dir); yield break; }
            var sw = new System.Diagnostics.Stopwatch();
            var costs = new System.Text.StringBuilder();
            float worst = 0f;
            System.Action<string> step = what => { float ms = (float)sw.Elapsed.TotalMilliseconds; worst = Mathf.Max(worst, ms); costs.Append(what).Append(' ').Append(ms.ToString("0.0")).Append(" ms, "); };
            m_lastStereo = GrabCamera();
            m_lastStereo.enabled = true;
            m_eyeGot[0] = m_eyeGot[1] = false;
            m_grabbing = true;
            for (int i = 0; i < 10 && !(m_eyeGot[0] && m_eyeGot[1]); i++) yield return null;
            m_grabbing = false;
            Destroy(m_lastStereo.gameObject);
            m_grabCam = null;
            if (!(m_eyeGot[0] && m_eyeGot[1])) { Plugin.L.LogWarning("bug mark: eye grab failed"); yield break; }
            Directory.CreateDirectory(dir);
            int w = m_eyeRaw[0].width, h = m_eyeRaw[0].height, sw2 = w / 2, sh = h / 2;
            var px = new Color32[sw2 * 2 * sh];
            for (int eye = 0; eye < 2; eye++)
            {
                yield return null;
                sw.Reset(); sw.Start();
                Color32[] raw = m_eyeRaw[eye].GetPixels32();
                for (int y = 0; y < sh; y++)
                {
                    int src = (y * 2) * w, dst = y * sw2 * 2 + eye * sw2;
                    for (int x = 0; x < sw2; x++) px[dst + x] = raw[src + x * 2];
                }
                sw.Stop(); step("eye " + eye);
            }
            for (int e = 0; e < 2; e++) { if (m_eyeRaw[e] != null) Destroy(m_eyeRaw[e]); m_eyeRaw[e] = null; }
            yield return null;
            sw.Reset(); sw.Start();
            var tex = new Texture2D(sw2 * 2, sh, TextureFormat.RGB24, false);
            tex.SetPixels32(px);
            tex.Apply(false);
            byte[] jpg = tex.EncodeToJPG(90);
            Destroy(tex);
            WriteLater(Path.Combine(dir, name + "_eyes.jpg"), jpg);
            sw.Stop(); step("eyes jpg");
            // the desktop view (the left eye as the window shows it), a little later
            yield return null;
            yield return null;
            yield return new WaitForEndOfFrame();
            sw.Reset(); sw.Start();
            var scr = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            scr.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
            scr.Apply(false);
            byte[] mj = scr.EncodeToJPG(85);
            Destroy(scr);
            WriteLater(Path.Combine(dir, name + "_mirror.jpg"), mj);
            sw.Stop(); step("desktop jpg");
            LastQuickCost = worst;
            Plugin.L.LogInfo("bug mark capture: " + costs + "the longest frame's share " + worst.ToString("0.0") + " ms | " + w + "x" + h + " eyes -> " + name + "_eyes.jpg");
        }

        public static float LastQuickCost = -1f;

        static void WriteLater(string path, byte[] data)
        {
            var t = new System.Threading.Thread(() =>
            {
                try { File.WriteAllBytes(path, data); } catch (Exception) { }
            }) { IsBackground = true, Name = "hof2vr capture write" };
            t.Start();
        }

        /// The menu the status line shows (GameWatch's).
        public static string CurrentMenu()
        {
            Func<string> f;
            return StatusExtras.TryGetValue("menu", out f) && f != null ? f() : "-";
        }

        /// A test capture is running (its readbacks and PNG writes stall frames): measurements leave this time out.
        public static float CaptureUntil = -1f;
        public static bool Capturing => Time.unscaledTime < CaptureUntil;

        IEnumerator CaptureEyes(string name, string toDir = null)
        {
            CaptureUntil = Time.unscaledTime + 2.5f;
            if (!XRSettings.enabled)
            {
                yield return Capture(name);
                yield break;
            }
            m_lastStereo = GrabCamera();
            m_lastStereo.enabled = true;
            // the desktop mirror of the same frame: what was submitted to the headset (left eye)
            try { ScreenCapture.CaptureScreenshot(Path.Combine(toDir ?? Path.Combine(TestMode.Dir, "shots"), name + "_mirror.png")); } catch (Exception) { }
            m_eyeGot[0] = m_eyeGot[1] = false;
            m_grabbing = true;
            for (int i = 0; i < 10 && !(m_eyeGot[0] && m_eyeGot[1]); i++) yield return null;
            m_grabbing = false;
            // a fresh grab camera every time: a re-enabled one read a much darker image
            Destroy(m_lastStereo.gameObject);
            m_grabCam = null;
            if (!(m_eyeGot[0] && m_eyeGot[1]))
            {
                Plugin.L.LogWarning("eye grab failed; falling back to re-render");
                yield return Capture(name);
                yield break;
            }
            string dir = toDir ?? Path.Combine(TestMode.Dir, "shots");
            Directory.CreateDirectory(dir);
            int w = m_eyeRaw[0].width, h = m_eyeRaw[0].height;
            int sw = w / 2, sh = h / 2;  // half resolution keeps files small
            var px = new Color32[sw * 2 * sh];
            for (int eye = 0; eye < 2; eye++)
            {
                Color32[] raw = m_eyeRaw[eye].GetPixels32();
                for (int y = 0; y < sh; y++)
                    for (int x = 0; x < sw; x++)
                        px[y * sw * 2 + eye * sw + x] = raw[(y * 2) * w + x * 2];
            }
            var tex = new Texture2D(sw * 2, sh, TextureFormat.RGB24, false);
            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(dir, name + "_eyes.png"), tex.EncodeToPNG());
            Destroy(tex);
            // fresh read-back textures every time (reusing them gave a dark second capture)
            for (int e = 0; e < 2; e++) { if (m_eyeRaw[e] != null) Destroy(m_eyeRaw[e]); m_eyeRaw[e] = null; }
            Plugin.L.LogInfo("captured eyes " + name + " from '" + m_lastStereo.name + "' " + w + "x" + h + " | " + m_grabInfo);
        }

        /// Renders what each eye sees (all screen cameras in depth order, using the HMD's
        /// per-eye view/projection) side by side into <dir>/shots/<name>_sbs.png, and saves the
        /// desktop mirror as <name>_mirror.png.
        IEnumerator Capture(string name)
        {
            yield return new WaitForEndOfFrame();
            string dir = Path.Combine(TestMode.Dir, "shots");
            Directory.CreateDirectory(dir);
            try
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, name + "_mirror.png"));
                int w = 960, h = 1056;
                if (XRSettings.enabled && XRSettings.eyeTextureWidth > 0)
                {
                    float s = 1056f / XRSettings.eyeTextureHeight;
                    w = Mathf.RoundToInt(XRSettings.eyeTextureWidth * s);
                }
                var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
                var tex = new Texture2D(w * 2, h, TextureFormat.RGB24, false);
                var go = new GameObject("HoF2VR_capture");
                var cap = go.AddComponent<Camera>();
                cap.enabled = false;
                List<Camera> cams = Camera.allCameras.Where(c => c.enabled && c.targetTexture == null).OrderBy(c => c.depth).ToList();
                for (int eye = 0; eye < 2; eye++)
                {
                    RenderTexture.active = rt;
                    GL.Clear(true, true, Color.black);
                    foreach (Camera src in cams)
                    {
                        cap.CopyFrom(src);
                        cap.stereoTargetEye = StereoTargetEyeMask.None;
                        cap.targetTexture = rt;
                        if (XRSettings.enabled && src.stereoTargetEye != StereoTargetEyeMask.None)
                        {
                            var e = eye == 0 ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right;
                            cap.worldToCameraMatrix = src.GetStereoViewMatrix(e);
                            cap.projectionMatrix = src.GetStereoProjectionMatrix(e);
                        }
                        cap.Render();
                        cap.ResetWorldToCameraMatrix();
                        cap.ResetProjectionMatrix();
                    }
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, w, h), eye * w, 0);
                }
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(dir, name + "_sbs.png"), tex.EncodeToPNG());
                Destroy(tex);
                Destroy(go);
                RenderTexture.ReleaseTemporary(rt);
                Plugin.L.LogInfo("captured " + name + " (" + cams.Count + " cameras, " + w + "x" + h + " per eye)");
            }
            catch (Exception e) { Plugin.L.LogError("capture failed: " + e); }
        }
    }
}

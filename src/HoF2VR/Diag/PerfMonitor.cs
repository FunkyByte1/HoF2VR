using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Performance telemetry for VR sessions (cheap; also runs in the player's own sessions so their
    /// log says how smooth a session was).
    ///
    ///  * Frame timing from Time.unscaledDeltaTime (the frame pacing the headset sees): average,
    ///    percentiles, share of frames over 11.1 / 13 ms and "missed" frames (> 1.5 refresh periods,
    ///    i.e. shown twice), GC runs, and EVERY missed frame with its main-thread timeline.
    ///  * The main-thread timeline of each frame (ms): gap = from the previous frame's end to our first
    ///    Update (present/submit, async-load integration and scene activation, FixedUpdate: "unaccounted" in
    ///    the 2026-10-11 analysis), l = all Updates and LateUpdates (first Update -> last LateUpdate),
    ///    w = w1 + w2: w1 = last LateUpdate -> onBeforeRender (the VR runtime's frame wait), w2 = onBeforeRender
    ///    handlers -> first camera cull, r = culling and drawing of all cameras. busy = dt - w1 = the main thread's
    ///    own time in the frame (`perf trace N` shows each frame's timeline).
    ///  * A "perf stall" line for every frame with busy >= StallMs (wall clock, timeline, GC, scene events,
    ///    state, hold), and a "perf hold" summary for every loading hold (boot, place changes): its frames, the
    ///    worst ones and the frozen time - the before/after number for the loading fixes.
    ///  * GPU clock, busy % and power from amdgpu's sysfs files, sampled at 20 Hz on a background
    ///    thread (read-only), and the GPU power state (profile, performance level, clock range, power
    ///    cap), logged whenever it changes (LACT switches profiles while the game runs).
    ///
    /// A summary line is logged every PerfLogSeconds (Settings; 0 = off) in VR. Test command:
    ///   perf            summary of the window so far      perf N        exactly the next N frames
    ///   perf every S    log interval (0 = off)            perf state    GPU power state
    ///   perf trace N    the timeline of each of the next N frames, one line each
    /// Never call OpenComposite's IVRCompositor::GetFrameTimeRemaining or GetCumulativeStats here:
    /// both are stubs that abort the game. XRStats are constants under OpenComposite (ignored).
    [DefaultExecutionOrder(-32000)]
    internal class PerfMonitor : MonoBehaviour
    {
        public static PerfMonitor I;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        /// A frame whose main thread was busy this long (not counting the VR frame wait) is a stall.
        public const float StallMs = 40f;
        const int MaxListedMisses = 60;

        // ---- frame window -------------------------------------------------------------------
        class Window
        {
            public string Tag;
            public int Want;              // >0: a fixed-length measurement (perf N)
            public double T0;             // stopwatch seconds at the start
            public string Clock0;         // wall clock at the start
            public readonly List<float> Dt = new List<float>(2048);
            public readonly List<float> Main = new List<float>(2048);
            public double GapSum, LogicSum, WaitSum, RenderSum;
            public float RenderMax, BusyMax;
            public int Gc, Stalls;
            public int MissAtGc, MissAtMain;
            public readonly StringBuilder Misses = new StringBuilder();
            public int MissCount;
            public int Frame0;
        }

        Window m_periodic;
        Window m_fixed;
        readonly Stopwatch m_sw = Stopwatch.StartNew();
        // this frame's timestamps (stopwatch seconds) and the frames they belong to
        double m_frameStart, m_prevEof = -1, m_tLate = -1, m_tBR = -1, m_tCull = -1;
        int m_startFrame = -1, m_lateFrame = -1, m_brFrame = -1, m_cullFrame = -1;
        /// The last finished frame's timeline (ms); Valid = all its timestamps were seen.
        public struct Timeline
        {
            public int Frame;
            public bool Valid;
            public float Dt, Gap, Logic, W1, W2, Render;
            /// The VR frame wait (OpenComposite's WaitGetPoses -> xrWaitFrame) sits in W1, between the last
            /// LateUpdate and onBeforeRender (`perf trace`, 2026-10-11: ~13.5 of 16.7 ms in Monado); busy = the rest.
            public float Busy => Dt - W1;
        }
        public static Timeline Last;
        float m_lastMain;
        int m_lastGc;
        float m_interval;
        public static string LastLine = "";
        int m_traceLeft;
        // scene events seen since the last frame record (rare: only on loads)
        static string s_events;

        // ---- GPU sampler --------------------------------------------------------------------
        struct GpuSample { public double T; public float Mhz; public float Busy; public float Watts; }
        readonly GpuSample[] m_ring = new GpuSample[1024];
        int m_ringCount;                 // samples written so far (slot = count % length)
        readonly object m_lock = new object();
        Thread m_thread;
        bool m_samplerTried;
        volatile bool m_run;
        string m_dev, m_hw;
        volatile string m_gpuState = "";
        string m_loggedState = "";

        void Awake()
        {
            I = this;
            m_interval = Settings.PerfLogSeconds;
            m_lastGc = GC.CollectionCount(0);
            Diag.Commands["perf"] = Command;
            Diag.StatusExtras["perf"] = () => Diag.Q(LastLine);
            StartCoroutine(EndOfFrame());
            Camera.onPreCull += FirstCull;
            Application.onBeforeRender += BeforeRender;
            gameObject.AddComponent<LateMark>();
            SceneManager.sceneLoaded += (s, m) => Event("+" + s.name);
            SceneManager.sceneUnloaded += s => Event("-" + s.name);
        }

        /// Something worth seeing next to a slow frame (a scene loaded or unloaded, a GC, a game step the mod
        /// spreads out): tagged onto the frame it happens in.
        public static void Event(string what)
        {
            s_events = s_events == null ? what : s_events + " " + what;
        }

        /// The last LateUpdate of the frame.
        [DefaultExecutionOrder(32000)]
        internal class LateMark : MonoBehaviour
        {
            void LateUpdate()
            {
                if (I == null) return;
                I.m_tLate = I.m_sw.Elapsed.TotalSeconds;
                I.m_lateFrame = Time.frameCount;
            }
        }

        void BeforeRender()
        {
            if (m_brFrame == Time.frameCount) return;
            m_brFrame = Time.frameCount;
            m_tBR = m_sw.Elapsed.TotalSeconds;
        }

        void FirstCull(Camera cam)
        {
            if (m_cullFrame == Time.frameCount) return;
            m_cullFrame = Time.frameCount;
            m_tCull = m_sw.Elapsed.TotalSeconds;
        }

        void OnDestroy()
        {
            m_run = false;
            Camera.onPreCull -= FirstCull;
            Application.onBeforeRender -= BeforeRender;
        }

        void OnApplicationQuit()
        {
            m_run = false;
            if (m_hold != null) EndHold("quit");
        }

        string Command(string[] a)
        {
            if (a.Length > 2 && a[1] == "every")
            {
                m_interval = float.Parse(a[2], Inv);
                m_periodic = null;
                return "perf log every " + m_interval + " s";
            }
            if (a.Length > 1 && a[1] == "state") return "gpu: " + m_gpuState;
            if (a.Length > 2 && a[1] == "trace") { m_traceLeft = int.Parse(a[2]); return "tracing " + m_traceLeft + " frames"; }
            if (a.Length > 1)
            {
                int n = int.Parse(a[1]);
                m_fixed = NewWindow("perf " + n + (a.Length > 2 ? " " + string.Join(" ", a, 2, a.Length - 2) : ""), n);
                return "sampling " + n + " frames";
            }
            if (m_periodic == null || m_periodic.Dt.Count == 0) return "no frames yet";
            string line = Summary(m_periodic, "perf now");
            Plugin.L.LogInfo(line);
            return line;
        }

        Window NewWindow(string tag, int want)
        {
            return new Window { Tag = tag, Want = want, T0 = m_sw.Elapsed.TotalSeconds, Frame0 = Time.frameCount, Clock0 = Diag.Clock() };
        }

        static readonly WaitForEndOfFrame s_eof = new WaitForEndOfFrame();

        /// The frame's timeline, once all its cameras have drawn (the rest of the frame is the present).
        IEnumerator EndOfFrame()
        {
            while (true)
            {
                yield return s_eof;
                double eof = m_sw.Elapsed.TotalSeconds;
                int f = Time.frameCount;
                m_lastMain = (float)((eof - m_frameStart) * 1000.0);
                var t = new Timeline { Frame = f };
                t.Valid = m_prevEof > 0 && m_startFrame == f && m_lateFrame == f && m_brFrame == f && m_cullFrame == f &&
                          m_tLate >= m_frameStart && m_tBR >= m_tLate && m_tCull >= m_tBR;
                t.Dt = m_prevEof > 0 ? (float)((eof - m_prevEof) * 1000.0) : 0f;
                if (t.Valid)
                {
                    t.Gap = (float)((m_frameStart - m_prevEof) * 1000.0);
                    t.Logic = (float)((m_tLate - m_frameStart) * 1000.0);
                    t.W1 = (float)((m_tBR - m_tLate) * 1000.0);
                    t.W2 = (float)((m_tCull - m_tBR) * 1000.0);
                    t.Render = (float)((eof - m_tCull) * 1000.0);
                }
                m_prevEof = eof;
                Last = t;
                OnFrame(t);
            }
        }

        // ---- per-frame: trace, stalls, holds ----------------------------------------------------

        void OnFrame(Timeline t)
        {
            if (!XRSettings.enabled) { s_events = null; return; }
            int gc = GC.CollectionCount(0);
            bool gcRan = gc != m_frameGc;
            m_frameGc = gc;
            string ev = s_events;
            s_events = null;
            if (m_traceLeft > 0)
            {
                m_traceLeft--;
                Plugin.L.LogInfo(string.Format(Inv, "perf trace f{0} {1} dt {2:0.0} | gap {3:0.0} l {4:0.0} w1 {5:0.0} w2 {6:0.0} r {7:0.0}{8}{9}{10}",
                    t.Frame, Diag.Clock(), t.Dt, t.Gap, t.Logic, t.W1, t.W2, t.Render, t.Valid ? "" : " (incomplete)", gcRan ? " gc" : "", ev != null ? " [" + ev + "]" : ""));
            }
            // every collection with its frame's time and the heap in use after it (release prep 2026-10-13, the Boehm heap
            // settings: a bigger heap collects less often, but each collection may take longer) - one line per collection
            if (gcRan && Time.frameCount > 2)
                Plugin.L.LogInfo(string.Format(Inv, "perf gc {0} busy {1:0} ms (dt {2:0}){3} | {4} | view {5} | used {6:0} MB",
                    Diag.Clock(), t.Valid ? t.Busy : t.Dt, t.Dt, t.Valid ? "" : " (incomplete)", GameWatch.State, View(),
                    GC.GetTotalMemory(false) / 1048576.0));
            if (!t.Valid) return;
            float busy = t.Busy;
            if (m_hold != null) m_hold.Add(t, busy, gcRan, ev);
            if (busy >= StallMs && Time.frameCount > 2)
            {
                string prof = UpdateProfiler.On ? UpdateProfiler.Top(8) : "";
                Plugin.L.LogInfo(string.Format(Inv, "perf stall {0} busy {1:0} ms (dt {2:0}: gap {3:0.0} l {4:0.0} w {5:0.0}+{6:0.0} r {7:0.0}){8}{9} | {10} | view {13}{11}{12}",
                    Diag.Clock(), busy, t.Dt, t.Gap, t.Logic, t.W1, t.W2, t.Render, gcRan ? " gc" : "", ev != null ? " [" + ev + "]" : "",
                    GameWatch.State, m_hold != null ? " | hold " + m_hold.Name : "", prof.Length > 0 ? " | " + prof : "", View()));
            }
        }

        int m_frameGc;

        /// What the eyes saw during a stall (a frozen frame of black or plain mist can't judder): black, mist (the waiting
        /// space veiled), space (the waiting space), fading (the black coming or going), open.
        public static string View()
        {
            if (VRFade.IsBlack) return "black";
            if (Limbo.On) return Limbo.Veil || Limbo.ArenaVeil ? "mist" : "space";
            float a = VRFade.Alpha;
            return a > 0f ? "fading " + a.ToString("0.00", Inv) : "open";
        }

        // ---- holds: the loading waits (boot, place changes) -------------------------------------

        class Hold
        {
            public string Name, Clock0, Why;
            public double T0;
            public int Frames, Over25, Stalls;
            public float BusyMax, DtMax, StallSum, BusySum;
            public string State0, Place0;
            // the 6 worst frames
            public readonly float[] TopBusy = new float[6];
            public readonly string[] TopDesc = new string[6];

            public void Add(Timeline t, float busy, bool gc, string ev)
            {
                Frames++;
                BusySum += busy;
                if (busy > BusyMax) BusyMax = busy;
                if (t.Dt > DtMax) DtMax = t.Dt;
                if (busy >= 25f) Over25++;
                if (busy >= StallMs) { Stalls++; StallSum += busy; }
                int slot = -1;
                for (int i = 0; i < TopBusy.Length; i++) if (TopBusy[i] < busy && (slot < 0 || TopBusy[i] < TopBusy[slot])) slot = i;
                if (slot < 0) return;
                TopBusy[slot] = busy;
                TopDesc[slot] = string.Format(Inv, "{0:0}(g{1:0} l{2:0} w{3:0} r{4:0}{5}{6})", busy, t.Gap, t.Logic, t.W1, t.Render, gc ? " gc" : "", ev != null ? " " + ev : "");
            }
        }

        Hold m_hold;
        bool m_bootDone;

        /// VRFade: a hold began (the view goes dark / into the waiting space).
        public static void BeginHold(string name)
        {
            if (I == null || !XRSettings.enabled) return;
            if (I.m_hold != null) return;   // nested holds count as one
            I.m_hold = new Hold { Name = name, T0 = I.m_sw.Elapsed.TotalSeconds, Clock0 = Diag.Clock(), State0 = GameWatch.State, Place0 = Places.Current.ToString() };
        }

        /// VRFade: the last owner released the hold.
        public static void EndHold(string why)
        {
            if (I == null || I.m_hold == null) return;
            Hold h = I.m_hold;
            I.m_hold = null;
            if (h.Name == "boot") I.m_bootDone = true;
            var top = new List<KeyValuePair<float, string>>();
            for (int i = 0; i < h.TopBusy.Length; i++) if (h.TopDesc[i] != null) top.Add(new KeyValuePair<float, string>(h.TopBusy[i], h.TopDesc[i]));
            top.Sort((x, y) => y.Key.CompareTo(x.Key));
            var sb = new StringBuilder();
            foreach (var kv in top) sb.Append(sb.Length > 0 ? " " : "").Append(kv.Value);
            Plugin.L.LogInfo(string.Format(Inv,
                "perf hold '{0}' {1} -> {2} ({3} -> {4}) {5:0.0} s: {6} frames, busy avg {7:0.0} max {8:0} ms, >=25 ms {9}, stalls >= {10:0} ms {11} ({12:0} ms), dt max {13:0} | load priority {14} | worst {15}",
                h.Name, h.Clock0, Diag.Clock(), h.Place0 + " " + h.State0.Replace("GameState_", ""), Places.Current + " " + GameWatch.State.Replace("GameState_", ""), I.m_sw.Elapsed.TotalSeconds - h.T0, h.Frames, h.Frames > 0 ? h.BusySum / h.Frames : 0f, h.BusyMax,
                h.Over25, StallMs, h.Stalls, h.StallSum, h.DtMax, Application.backgroundLoadingPriority, sb));
        }

        void Update()
        {
            double now = m_sw.Elapsed.TotalSeconds;
            m_frameStart = now;
            m_startFrame = Time.frameCount;
            if (!XRSettings.enabled) return;
            if (!m_samplerTried) { m_samplerTried = true; StartSampler(); }   // VR sessions only
            // the boot is one long hold from the first VR frame until the first place is shown
            if (!m_bootDone && m_hold == null && GameWatch.State == "") BeginHold("boot");
            float dt = Time.unscaledDeltaTime * 1000f;
            int gc = GC.CollectionCount(0);
            bool gcRan = gc != m_lastGc;
            m_lastGc = gc;
            if (Time.frameCount < 30) return;

            if (m_interval > 0f)
            {
                if (m_periodic == null) m_periodic = NewWindow("perf", 0);
                Add(m_periodic, dt, gcRan, now);
                if (now - m_periodic.T0 >= m_interval)
                {
                    string line = Summary(m_periodic, "perf " + m_interval.ToString("0", Inv) + "s");
                    Plugin.L.LogInfo(line);
                    m_periodic = NewWindow("perf", 0);
                }
            }
            if (m_fixed != null)
            {
                Add(m_fixed, dt, gcRan, now);
                if (m_fixed.Dt.Count >= m_fixed.Want)
                {
                    string line = Summary(m_fixed, m_fixed.Tag);
                    Plugin.L.LogInfo(line);
                    if (TestMode.Enabled)
                        try { File.AppendAllText(Path.Combine(TestMode.Dir, "perf.log"), DateTime.Now.ToString("HH:mm:ss ", Inv) + line + "\n"); } catch (Exception) { }
                    m_fixed = null;
                }
            }
            if (m_gpuState != m_loggedState)
            {
                m_loggedState = m_gpuState;
                Plugin.L.LogInfo("perf: GPU power state " + m_loggedState);
            }
        }

        float Budget => 1000f / (XRDevice.refreshRate > 1f ? XRDevice.refreshRate : 90f);

        void Add(Window w, float dt, bool gc, double now)
        {
            Timeline t = Last;   // the previous frame's timeline (its dt is this frame's delta)
            w.Dt.Add(dt);
            w.Main.Add(m_lastMain);
            if (t.Valid)
            {
                w.GapSum += t.Gap;
                w.LogicSum += t.Logic;
                w.WaitSum += t.W1 + t.W2;
                w.RenderSum += t.Render;
                if (t.Render > w.RenderMax) w.RenderMax = t.Render;
                if (t.Busy > w.BusyMax) w.BusyMax = t.Busy;
                if (t.Busy >= StallMs) w.Stalls++;
            }
            if (gc) w.Gc++;
            if (dt > 1.5f * Budget)
            {
                w.MissCount++;
                if (gc) w.MissAtGc++;
                bool guard = Time.frameCount - CullingGuard.LastGrowFrame <= 2;
                bool cpu = t.Valid && t.Busy > Budget * 0.75f;
                if (cpu) w.MissAtMain++;
                if (w.MissCount <= MaxListedMisses)
                {
                    w.Misses.Append(w.Misses.Length > 0 ? " " : "").Append((now - w.T0).ToString("0.0", Inv)).Append("s/").Append(dt.ToString("0", Inv));
                    if (t.Valid)
                        w.Misses.Append(" (g").Append(t.Gap.ToString("0.0", Inv)).Append(" l").Append(t.Logic.ToString("0.0", Inv))
                            .Append(" w").Append((t.W1 + t.W2).ToString("0.0", Inv)).Append(" r").Append(t.Render.ToString("0.0", Inv)).Append(")");
                    w.Misses.Append(gc ? "/gc" : "").Append(guard ? "/guard" : "");
                }
            }
        }

        static float Pct(List<float> sorted, float p)
        {
            if (sorted.Count == 0) return 0f;
            int i = Mathf.Clamp(Mathf.CeilToInt(p * sorted.Count) - 1, 0, sorted.Count - 1);
            return sorted[i];
        }

        string Summary(Window w, string tag)
        {
            int n = w.Dt.Count;
            var dt = new List<float>(w.Dt);
            dt.Sort();
            float sum = 0f;
            int over11 = 0, over13 = 0;
            foreach (float v in w.Dt) { sum += v; if (v > 11.11f) over11++; if (v > 13f) over13++; }
            double t1 = m_sw.Elapsed.TotalSeconds;
            string gpu = GpuSummary(w.T0, t1);
            string line = string.Format(Inv,
                "{0} @{1} [{2}] {3} frames {4:0.0}s: avg {5:0.00} p50 {6:0.0} p95 {7:0.0} p99 {8:0.0} max {9:0.0} ms | >11.1 {10:0.0}% >13 {11:0.0}% miss {12:0.0}% ({13}) | " +
                "main gap {14:0.0} logic {15:0.0} wait {16:0.0} render {17:0.0} (max {18:0.0}) busy max {19:0} stalls {20} ms | gc {21} | {22}{23}",
                tag, w.Clock0, GameWatch.State, n, t1 - w.T0, n > 0 ? sum / n : 0f, Pct(dt, 0.5f), Pct(dt, 0.95f), Pct(dt, 0.99f), n > 0 ? dt[n - 1] : 0f,
                n > 0 ? 100f * over11 / n : 0f, n > 0 ? 100f * over13 / n : 0f, n > 0 ? 100f * w.MissCount / n : 0f, w.MissCount,
                n > 0 ? w.GapSum / n : 0, n > 0 ? w.LogicSum / n : 0, n > 0 ? w.WaitSum / n : 0, n > 0 ? w.RenderSum / n : 0, w.RenderMax, w.BusyMax, w.Stalls,
                w.Gc + (w.MissAtGc > 0 ? " (" + w.MissAtGc + " at a miss)" : ""),
                gpu, w.MissCount > 0 ? " | misses " + w.Misses + (w.MissCount > MaxListedMisses ? " ..." : "") + (w.MissAtMain > 0 ? " (" + w.MissAtMain + " cpu-heavy)" : "") : "");
            LastLine = line;
            return line;
        }

        string GpuSummary(double t0, double t1)
        {
            if (m_thread == null) return "gpu n/a";
            var clk = new List<float>();
            float busy = 0f, watts = 0f, work = 0f;
            lock (m_lock)
            {
                int count = Math.Min(m_ringCount, m_ring.Length);
                for (int k = 0; k < count; k++)
                {
                    GpuSample s = m_ring[(m_ringCount - 1 - k) % m_ring.Length];
                    if (s.T < t0) break;
                    if (s.T > t1) continue;
                    clk.Add(s.Mhz);
                    busy += s.Busy;
                    work += s.Mhz * s.Busy / 100f;
                    watts += s.Watts;
                }
            }
            if (clk.Count == 0) return "gpu no samples";
            int n = clk.Count;
            clk.Sort();
            // "work": clock x busy, a rough measure of GPU cycles spent (MHz-equivalent)
            return string.Format(Inv, "sclk med {0:0} p10 {1:0} p90 {2:0} MHz busy {3:0}% work {6:0} {4:0} W (n {5})",
                Pct(clk, 0.5f), Pct(clk, 0.1f), Pct(clk, 0.9f), busy / n, watts / n, n, work / n);
        }

        // ---- sysfs sampler (background thread) ------------------------------------------------

        void StartSampler()
        {
            if (Application.platform != RuntimePlatform.LinuxPlayer) return;
            try
            {
                foreach (string card in Directory.GetDirectories("/sys/class/drm", "card*"))
                {
                    string dev = Path.Combine(card, "device");
                    if (card.Contains("-") || !File.Exists(Path.Combine(dev, "vendor"))) continue;
                    if (File.ReadAllText(Path.Combine(dev, "vendor")).Trim() != "0x1002") continue;
                    string[] hw = Directory.Exists(Path.Combine(dev, "hwmon")) ? Directory.GetDirectories(Path.Combine(dev, "hwmon"), "hwmon*") : new string[0];
                    if (hw.Length == 0 || !File.Exists(Path.Combine(dev, "gpu_busy_percent"))) continue;
                    m_dev = dev;
                    m_hw = hw[0];
                    break;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("perf: no GPU telemetry (" + e.Message + ")"); return; }
            if (m_dev == null) { Plugin.L.LogInfo("perf: no amdgpu card found, GPU telemetry off"); return; }
            m_run = true;
            m_thread = new Thread(SamplerLoop) { IsBackground = true, Name = "HoF2VR perf sampler" };
            m_thread.Start();
            Plugin.L.LogInfo("perf: GPU telemetry from " + m_dev + " (" + m_hw + "), log every " + m_interval + " s");
        }

        readonly byte[] m_buf = new byte[8192];

        string ReadText(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1))
                {
                    int total = 0, r;
                    while (total < m_buf.Length && (r = fs.Read(m_buf, total, m_buf.Length - total)) > 0) total += r;
                    return Encoding.ASCII.GetString(m_buf, 0, total);
                }
            }
            catch (Exception) { return ""; }
        }

        long ReadLong(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1))
                    return ParseLong(fs.Read(m_buf, 0, 64));
            }
            catch (Exception) { return -1; }
        }

        long ParseLong(int total)
        {
            long v = 0;
            bool any = false;
            for (int i = 0; i < total; i++)
            {
                byte c = m_buf[i];
                if (c < (byte)'0' || c > (byte)'9') { if (any) break; continue; }
                v = v * 10 + (c - (byte)'0');
                any = true;
            }
            return any ? v : -1;
        }

        /// A sysfs value kept open and re-read from the start (a new FileStream per read allocated ~60 objects a
        /// second on the shared heap: more frequent garbage collections on the game's main thread).
        long ReadOpen(ref FileStream fs, string path)
        {
            try
            {
                if (fs == null) fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
                fs.Seek(0, SeekOrigin.Begin);
                return ParseLong(fs.Read(m_buf, 0, 64));
            }
            catch (Exception)
            {
                try { if (fs != null) fs.Dispose(); } catch (Exception) { }
                fs = null;
                return -1;
            }
        }

        void SamplerLoop()
        {
            string fClk = Path.Combine(m_hw, "freq1_input"), fBusy = Path.Combine(m_dev, "gpu_busy_percent"), fPow = Path.Combine(m_hw, "power1_average");
            double nextState = 0;
            try
            {
                FileStream clk = null, busy = null, pow = null;
                while (m_run)
                {
                    double t = m_sw.Elapsed.TotalSeconds;
                    var s = new GpuSample { T = t, Mhz = ReadOpen(ref clk, fClk) / 1e6f, Busy = ReadOpen(ref busy, fBusy), Watts = ReadOpen(ref pow, fPow) / 1e6f };
                    lock (m_lock)
                    {
                        m_ring[m_ringCount % m_ring.Length] = s;
                        m_ringCount++;
                    }
                    if (t >= nextState)
                    {
                        nextState = t + 5.0;
                        m_gpuState = ReadState();
                    }
                    Thread.Sleep(50);
                }
            }
            catch (Exception e) { m_gpuState = "sampler stopped: " + e.Message; }
        }

        string ReadState()
        {
            string profile = "?";
            foreach (string l in ReadText(Path.Combine(m_dev, "pp_power_profile_mode")).Split('\n'))
                if (l.Contains("*")) { profile = l.Split(':')[0].Trim().TrimEnd('*').Trim(); break; }
            string od = ReadText(Path.Combine(m_dev, "pp_od_clk_voltage"));
            string sclkMax = "?";
            int i = od.IndexOf("OD_SCLK", StringComparison.Ordinal);
            if (i >= 0)
            {
                string[] lines = od.Substring(i).Split('\n');
                if (lines.Length > 2) sclkMax = lines[2].Trim().Replace("1: ", "");
            }
            long cap = ReadLong(Path.Combine(m_hw, "power1_cap"));
            return "profile " + profile + ", level " + ReadText(Path.Combine(m_dev, "power_dpm_force_performance_level")).Trim() +
                   ", max sclk " + sclkMax + ", power cap " + (cap > 0 ? (cap / 1000000).ToString(Inv) + " W" : "?");
        }
    }
}

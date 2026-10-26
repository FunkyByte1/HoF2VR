using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// Test sounds for proving the HRTF without ears (docs/hrtf-plan.md §6; scripts/hrtf_test.py records them from a
    /// PipeWire null sink and scripts/hrtf_analyze.py measures them). Test runs only, except `probe`.
    ///   hrtf sweep <tag> [hrtf|pan|ab]   a 2D sync chirp, then a 250 ms pink-noise burst every 0.5 s from 2 m: azimuth 0-345
    ///                                    in 15 degree steps at elevation 0, then 0/90/180/270 at +-45; once through the
    ///                                    HRTF, once with Unity's panning (ab); every other source muted meanwhile;
    ///                                    the schedule (DSP times) -> testdata/hrtf/<tag>.json
    ///   hrtf say <tag> [n]               the same burst n times (6) through the Dealer's own voice source (wherever the
    ///                                    game and VRAudio have it, through the play hook's policy like any line of his)
    ///   hrtf enemy <tag> [n]             the same through the nearest live enemy's own sound source in a fight
    ///   hrtf probe <az> <el> <dist m> [secs] [hrtf|pan]   a looping pink noise placed once relative to the head
    /// Azimuth: degrees to the right of straight ahead; elevation: up. Distances in real metres (the world scale applied).
    internal class HrtfProbe : MonoBehaviour
    {
        public static HrtfProbe I;
        public static bool MuteOthers;
        static readonly List<AudioSource> s_muted = new List<AudioSource>();
        static readonly HashSet<int> s_mine = new HashSet<int>();
        static AudioClip s_burst, s_loop, s_chirp;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        AudioSource m_probe, m_keep;
        float m_probeEnd, m_followAz, m_followEl, m_followDist;
        bool m_follow;
        bool m_busy;

        public const float BurstSecs = 0.25f, Gap = 0.5f, Dist = 2f, ChirpSecs = 0.1f, ChirpF0 = 300f, ChirpF1 = 8000f;
        public const int Seed = 1234;

        void Awake() { I = this; }

        static readonly string[] s_mixerVols = { "MusicVolume", "VoiceVolume", "SFXVolume", "UIVolume" };
        static float[] s_mixerSaved;

        void LateUpdate()
        {
            bool mute = MuteOthers || m_busy;
            if (mute)
            {
                foreach (AudioSource s in FindObjectsOfType<AudioSource>())
                {
                    int id = s.GetInstanceID();
                    if (s_mine.Contains(id) || s == m_keep || s.mute) continue;
                    s.mute = true;
                    s_muted.Add(s);
                }
                // MuteOthers also holds the game's mixer down (its four exposed volumes), so a sound that starts during a
                // load's frame never reaches the output (the probe and the sweep play straight to the master, not through it)
                if (MuteOthers) MixerMute(true);
            }
            else if (s_muted.Count > 0)
            {
                foreach (AudioSource s in s_muted) if (s != null) s.mute = false;
                s_muted.Clear();
            }
            if (!MuteOthers && s_mixerSaved != null) MixerMute(false);
            if (m_probe != null)
            {
                if (m_probeEnd > 0f && Time.unscaledTime > m_probeEnd) StopProbe();
                else if (m_follow && m_probe.isPlaying)
                {
                    Transform ears = Ears;   // head-locked: the same direction after a load moved the player anywhere
                    float scale = VRRig.I != null ? VRRig.I.Scale : 1f;
                    if (ears != null) m_probe.transform.position = ears.position + ears.rotation * Dir(m_followAz, m_followEl) * (m_followDist * scale);
                }
            }
        }

        static void MixerMute(bool on)
        {
            try
            {
                UnityEngine.Audio.AudioMixer mx = SoundManager.Instance != null ? SoundManager.Instance.Mixer : null;
                if (mx == null) return;
                if (on)
                {
                    if (s_mixerSaved == null)
                    {
                        s_mixerSaved = new float[s_mixerVols.Length];
                        for (int i = 0; i < s_mixerVols.Length; i++) { float v; s_mixerSaved[i] = mx.GetFloat(s_mixerVols[i], out v) ? v : 0f; }
                    }
                    foreach (string n in s_mixerVols) mx.SetFloat(n, -80f);
                }
                else if (s_mixerSaved != null)
                {
                    for (int i = 0; i < s_mixerVols.Length; i++) mx.SetFloat(s_mixerVols[i], s_mixerSaved[i]);
                    s_mixerSaved = null;
                }
            }
            catch (Exception) { }
        }

        static float F(string s) => float.Parse(s, Inv);

        // ---- the load check's trace -----------------------------------------------------------------------------------

        /// `hrtf trace 1|0` (test runs): at each frame's end, the listeners that are on (ours or the game's), how far each is
        /// from the probe (near: within 1.5x the probe's distance), the listener pause and volume, the probe's own state -
        /// logged with the wall clock whenever that changes, so the recording's dropouts can be placed against it.
        public static bool Trace;
        string m_traceSig;
        Coroutine m_traceCo;

        public static string TraceCmd(string[] a)
        {
            if (!TestMode.Enabled) return "test runs only";
            if (I == null) return "no probe component";
            Trace = a.Length > 2 && a[2] == "1";
            if (Trace && I.m_traceCo == null) I.m_traceCo = I.StartCoroutine(I.TraceRoutine());
            return "trace " + (Trace ? "on" : "off");
        }

        IEnumerator TraceRoutine()
        {
            var eof = new WaitForEndOfFrame();
            m_traceSig = null;
            while (Trace)
            {
                yield return eof;
                try { TraceTick(); }
                catch (Exception e) { Plugin.L.LogWarning("hrtf trace: " + e.Message); Trace = false; }
            }
            m_traceCo = null;
        }

        void TraceTick()
        {
            var sb = new StringBuilder();
            Transform ours = VRAudio.EarsTransform;
            float unit = Mathf.Max(1e-5f, (m_followDist > 0f ? m_followDist : Dist) * (VRRig.I != null ? VRRig.I.Scale : 1f));
            int on = 0;
            foreach (AudioListener l in FindObjectsOfType<AudioListener>())
            {
                if (!l.isActiveAndEnabled) continue;
                on++;
                sb.Append('[').Append(l.transform == ours ? "ours" : VRUI.Path(l.transform));
                if (m_probe != null)
                {
                    float r = Vector3.Distance(l.transform.position, m_probe.transform.position) / unit;
                    sb.Append(r < 1.5f ? " near" : " far ~1e" + Mathf.FloorToInt(Mathf.Log10(r)).ToString(Inv));
                }
                sb.Append("] ");
            }
            sb.Append("on ").Append(on).Append(" | pause ").Append(AudioListener.pause ? "yes" : "no")
              .Append(" volume ").Append(AudioListener.volume.ToString("0.00", Inv));
            if (m_probe != null)
            {
                float fv;
                sb.Append(" | probe ").Append(m_probe.isPlaying ? "playing" : "stopped").Append(m_probe.spatialize ? " hrtf" : " pan")
                  .Append(m_probe.GetSpatializerFloat(7, out fv) ? " attached" : m_probe.spatialize ? " NOT attached" : "");
            }
            string sig = sb.ToString();
            if (sig == m_traceSig) return;
            m_traceSig = sig;
            Plugin.L.LogInfo(string.Format(Inv, "hrtf trace {0:HH:mm:ss.fff} f{1} dsp {2:0.000}: {3}", DateTime.Now, Time.frameCount, AudioSettings.dspTime, sig));
        }

        // ---- the clips ------------------------------------------------------------------------------------------------

        static int Rate => Hrtf.Rate > 0 ? Hrtf.Rate : 48000;

        /// Pink noise (Paul Kellet's filter on seeded white noise), RMS -26 dBFS, 5 ms raised-cosine ends.
        static float[] Pink(int n, int seed)
        {
            var rnd = new System.Random(seed);
            var d = new float[n];
            double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0, sum = 0;
            for (int i = 0; i < n; i++)
            {
                double w = rnd.NextDouble() * 2.0 - 1.0;
                b0 = 0.99886 * b0 + w * 0.0555179;
                b1 = 0.99332 * b1 + w * 0.0750759;
                b2 = 0.96900 * b2 + w * 0.1538520;
                b3 = 0.86650 * b3 + w * 0.3104856;
                b4 = 0.55000 * b4 + w * 0.5329522;
                b5 = -0.7616 * b5 - w * 0.0168980;
                double p = b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362;
                b6 = w * 0.115926;
                d[i] = (float)p;
                sum += p * p;
            }
            float g = (float)(0.05 / Math.Sqrt(sum / n));   // -26 dBFS RMS
            for (int i = 0; i < n; i++) d[i] *= g;
            return d;
        }

        static void Ramp(float[] d, int r)
        {
            r = Math.Min(r, d.Length / 2);
            for (int i = 0; i < r; i++)
            {
                float k = 0.5f - 0.5f * Mathf.Cos(Mathf.PI * i / r);
                d[i] *= k;
                d[d.Length - 1 - i] *= k;
            }
        }

        static AudioClip Burst()
        {
            if (s_burst != null) return s_burst;
            int rate = Rate, n = (int)(rate * BurstSecs);
            float[] d = Pink(n, Seed);
            Ramp(d, rate / 200);
            s_burst = AudioClip.Create("HoF2VR_HrtfBurst", n, 1, rate, false);
            s_burst.SetData(d, 0);
            return s_burst;
        }

        static AudioClip Loop()
        {
            if (s_loop != null) return s_loop;
            int rate = Rate, n = rate * 4, x = rate / 20;
            float[] raw = Pink(n + x, Seed + 1), d = new float[n];
            for (int i = 0; i < n; i++) d[i] = raw[i];
            for (int i = 0; i < x; i++)   // the tail cross-faded into the head: a seamless loop
            {
                float k = (float)i / x;
                d[i] = raw[i] * k + raw[n + i] * (1f - k);
            }
            s_loop = AudioClip.Create("HoF2VR_HrtfLoop", n, 1, rate, false);
            s_loop.SetData(d, 0);
            return s_loop;
        }

        static AudioClip s_tone;

        /// A seamless sine loop at rate/48 Hz (1000 Hz at 48 kHz: exactly 48 samples a period, 1 s long), amplitude 0.1: any
        /// click or dropout in the output shows as energy outside the tone (scripts/hrtf_clicks.py).
        static AudioClip Tone()
        {
            if (s_tone != null) return s_tone;
            int rate = Rate, n = rate;
            var d = new float[n];
            for (int i = 0; i < n; i++) d[i] = 0.1f * Mathf.Sin(2f * Mathf.PI * (i % 48) / 48f);
            s_tone = AudioClip.Create("HoF2VR_HrtfTone", n, 1, rate, false);
            s_tone.SetData(d, 0);
            return s_tone;
        }

        /// The sync chirp: 100 ms exponential sweep 300 Hz -> 8 kHz, amplitude 0.3, 2 ms ends.
        static AudioClip Chirp()
        {
            if (s_chirp != null) return s_chirp;
            int rate = Rate, n = (int)(rate * ChirpSecs);
            var d = new float[n];
            double k = Math.Log(ChirpF1 / ChirpF0), ph = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate, f = ChirpF0 * Math.Exp(k * t / ChirpSecs);
                ph += 2 * Math.PI * f / rate;
                d[i] = (float)(0.3 * Math.Sin(ph));
            }
            Ramp(d, rate / 500);
            s_chirp = AudioClip.Create("HoF2VR_HrtfChirp", n, 1, rate, false);
            s_chirp.SetData(d, 0);
            return s_chirp;
        }

        // ---- sources ----------------------------------------------------------------------------------------------------

        static AudioSource NewSource(string name, Transform parent, bool spatial)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            else DontDestroyOnLoad(go);
            AudioSource a = go.AddComponent<AudioSource>();
            Hrtf.Exempt(a);
            s_mine.Add(a.GetInstanceID());
            a.playOnAwake = false;
            a.priority = 0;
            a.dopplerLevel = 0f;
            a.spread = 0f;
            a.bypassReverbZones = true;
            a.spatialBlend = spatial ? 1f : 0f;
            a.rolloffMode = AudioRolloffMode.Linear;   // the same level at any test distance: within minDistance
            a.minDistance = 50f;
            a.maxDistance = 100f;
            return a;
        }

        /// The listener actually in use: VRAudio's head-following ears, or - while the mod hands the sound to a game camera's
        /// listener (the fight card's tunnel) - that one; Camera.main only if no listener is on. (C1 2026-10-14: a probe that
        /// followed Camera.main while the tunnel's listener was on fell silent for 0.6 s at the fight's load.)
        static Transform Ears
        {
            get
            {
                if (VRAudio.EarsTransform != null) return VRAudio.EarsTransform;
                foreach (AudioListener l in FindObjectsOfType<AudioListener>())
                    if (l.isActiveAndEnabled) return l.transform;
                return Camera.main != null ? Camera.main.transform : null;
            }
        }

        static Vector3 Dir(float az, float el)
        {
            float a = az * Mathf.Deg2Rad, e = el * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(a) * Mathf.Cos(e), Mathf.Sin(e), Mathf.Cos(a) * Mathf.Cos(e));
        }

        /// Where a point is from the ears: azimuth (right +), elevation, distance in real metres.
        static string Where(Vector3 p)
        {
            Transform ears = Ears;
            if (ears == null) return "\"az\":null";
            Vector3 l = ears.InverseTransformPoint(p);
            float scale = VRRig.I != null ? VRRig.I.Scale : 1f;
            float az = Mathf.Atan2(l.x, l.z) * Mathf.Rad2Deg, el = Mathf.Atan2(l.y, new Vector2(l.x, l.z).magnitude) * Mathf.Rad2Deg;
            return string.Format(Inv, "\"az\":{0:0.0},\"el\":{1:0.0},\"dist_m\":{2:0.00}", az, el, l.magnitude / scale);
        }

        static string HeadJson()
        {
            Transform ears = Ears;
            if (ears == null) return "null";
            Vector3 e = ears.rotation.eulerAngles;
            return string.Format(Inv, "{{\"pos\":[{0:0.###},{1:0.###},{2:0.###}],\"yaw\":{3:0.0},\"pitch\":{4:0.0},\"roll\":{5:0.0},\"scale\":{6:0.###}}}",
                ears.position.x, ears.position.y, ears.position.z, e.y, e.x, e.z, VRRig.I != null ? VRRig.I.Scale : 1f);
        }

        static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        static void Save(string tag, string json)
        {
            string dir = Path.Combine(TestMode.Dir, "hrtf");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, tag + ".json"), json);
            Plugin.L.LogInfo("hrtf: schedule '" + tag + "' -> " + Path.Combine(dir, tag + ".json"));
        }

        // ---- probe ------------------------------------------------------------------------------------------------------

        public static string Probe(string[] a)
        {
            if (I == null) return "no probe component";
            if (a.Length > 2 && a[2] == "stop") { I.StopProbe(); return "probe stopped"; }
            if (a.Length < 5) return "hrtf probe <az> <el> <dist m> [secs] [hrtf|pan] [tone] [follow] | hrtf probe stop";
            Transform ears = Ears;
            if (ears == null) return "no ears";
            float az = F(a[2]), el = F(a[3]), dist = F(a[4]), secs = a.Length > 5 ? F(a[5]) : 10f;
            bool hrtf = !(a.Length > 6 && a[6] == "pan"), tone = Array.IndexOf(a, "tone") > 6, follow = Array.IndexOf(a, "follow") > 6;
            if (I.m_probe == null) { I.m_probe = NewSource("HoF2VR_HrtfProbe", null, true); I.m_probe.loop = true; }
            AudioClip clip = tone ? Tone() : Loop();
            if (I.m_probe.clip != clip) { I.m_probe.Stop(); I.m_probe.clip = clip; }
            float scale = VRRig.I != null ? VRRig.I.Scale : 1f;
            I.m_probe.transform.position = ears.position + ears.rotation * Dir(az, el) * (dist * scale);
            I.m_follow = follow;
            I.m_followAz = az;
            I.m_followEl = el;
            I.m_followDist = dist;
            bool flagged = Hrtf.ForceFlag(I.m_probe, hrtf);
            if (!I.m_probe.isPlaying) I.m_probe.Play();
            I.m_probeEnd = secs > 0f ? Time.unscaledTime + secs : 0f;
            return string.Format(Inv, "probe {6} at az {0} el {1} {2} m ({3}) for {4} s{7}, {5}", az, el, dist, Diag.V(I.m_probe.transform.position), secs > 0f ? secs.ToString(Inv) : "ever",
                hrtf ? (flagged ? "HRTF" : "HRTF NOT READY: " + Hrtf.Reason) : "panning", tone ? "1 kHz tone" : "pink noise", follow ? ", head-locked" : "");
        }

        void StopProbe()
        {
            if (m_probe != null) m_probe.Stop();
            m_probeEnd = 0f;
        }

        // ---- the sweep (T3) -------------------------------------------------------------------------------------------

        public static string Sweep(string[] a)
        {
            if (!TestMode.Enabled) return "test runs only";
            if (I == null || I.m_busy) return "busy";
            string tag = a.Length > 2 ? a[2] : "sweep", mode = a.Length > 3 ? a[3] : "ab";
            bool rev = Array.IndexOf(a, "rev") > 3, noatt = Array.IndexOf(a, "noatt") > 3;
            if (mode != "pan" && !Hrtf.Ready) return "HRTF not ready: " + Hrtf.Reason;
            if (Ears == null) return "no ears";
            I.StartCoroutine(I.SweepRoutine(tag, mode, rev, noatt));
            return "sweeping '" + tag + "' (" + mode + (rev ? ", reversed order" : "") + (noatt ? ", no distance attenuation in the plugin" : "") + ")";
        }

        /// noatt (an experiment): the HRTF bursts tell the plugin to apply no distance attenuation (parameter 0 = 0), so the value
        /// Unity hands it cannot scale them.
        IEnumerator SweepRoutine(string tag, string mode, bool rev, bool noatt)
        {
            m_busy = true;
            yield return null;
            yield return null;   // the mute takes effect
            var pos = new List<Vector2>();
            for (int az = 0; az < 360; az += 15) pos.Add(new Vector2(az, 0f));
            foreach (int az in new[] { 0, 90, 180, 270 }) { pos.Add(new Vector2(az, 45f)); pos.Add(new Vector2(az, -45f)); }
            if (rev) pos.Reverse();   // the same directions the other way round (a level that follows time, not direction, shows)
            bool[] passes = mode == "ab" ? new[] { true, false } : new[] { mode != "pan" };
            var root = new GameObject("HoF2VR_HrtfSweep");
            DontDestroyOnLoad(root);
            Transform ears = Ears;
            float scale = VRRig.I != null ? VRRig.I.Scale : 1f;
            AudioClip burst = Burst(), chirp = Chirp();
            var js = new StringBuilder();
            int buf, nbuf;
            AudioSettings.GetDSPBufferSize(out buf, out nbuf);
            js.AppendFormat(Inv, "{{\"tag\":\"{0}\",\"kind\":\"sweep\",\"order\":\"" + (rev ? "reversed" : "forward") + "\",\"noatt\":" + (noatt ? "true" : "false") + ",\"mode\":\"{1}\",\"rate\":{2},\"buffer\":{3},\"burst_s\":{4},\"gap_s\":{5},\"dist_m\":{6},\"chirp\":{{\"s\":{7},\"f0\":{8},\"f1\":{9}}},\"seed\":{10},\"head\":{11},\"interp\":\"{12}\",\"passes\":[",
                Esc(tag), mode, AudioSettings.outputSampleRate, buf, BurstSecs, Gap, Dist, ChirpSecs, ChirpF0, ChirpF1, Seed, HeadJson(), Settings.HrtfInterpolation);
            // a rolling schedule: each source is made and scheduled ~0.3 s before it plays and goes after it, so at most a few
            // probe voices exist at once (all 66 at once, r1, crowded the game's 32 real voices: ~4 dB level steps in time)
            double t = AudioSettings.dspTime + 0.6;
            var times = new List<double>();
            for (int p = 0; p < passes.Length; p++)
            {
                times.Add(t);
                t += 1.0 + pos.Count * Gap + 1.5;
            }
            var live = new List<KeyValuePair<AudioSource, double>>();
            for (int p = 0; p < passes.Length; p++)
            {
                double tc = times[p];
                while (AudioSettings.dspTime < tc - 0.3) { Reap(live); yield return null; }
                AudioSource c = NewSource("chirp" + p, root.transform, false);
                c.clip = chirp;
                c.PlayScheduled(tc);
                live.Add(new KeyValuePair<AudioSource, double>(c, tc + ChirpSecs + 0.2));
                js.AppendFormat(Inv, "{0}{{\"hrtf\":{1},\"chirp_t\":{2:0.000000},\"bursts\":[", p > 0 ? "," : "", passes[p] ? "true" : "false", tc);
                for (int i = 0; i < pos.Count; i++)
                {
                    double tb = tc + 1.0 + i * Gap;
                    while (AudioSettings.dspTime < tb - 0.3) { Reap(live); yield return null; }
                    AudioSource b = NewSource("burst" + p + "_" + i, root.transform, true);
                    b.clip = burst;
                    b.transform.position = ears.position + ears.rotation * Dir(pos[i].x, pos[i].y) * (Dist * scale);
                    bool flagged = Hrtf.ForceFlag(b, passes[p]);
                    if (noatt && passes[p]) b.SetSpatializerFloat(0, 0f);
                    float v;
                    bool attached = b.GetSpatializerFloat(7, out v);
                    b.PlayScheduled(tb);
                    live.Add(new KeyValuePair<AudioSource, double>(b, tb + BurstSecs + 0.2));
                    js.AppendFormat(Inv, "{0}{{\"az\":{1:0},\"el\":{2:0},\"t\":{3:0.000000},\"spatialize\":{4},\"attached\":{5},\"where\":{{{6}}}}}",
                        i > 0 ? "," : "", pos[i].x, pos[i].y, tb, b.spatialize ? "true" : "false", attached ? "true" : "false", Where(b.transform.position));
                    if (passes[p] && !flagged) Plugin.L.LogWarning("hrtf sweep: burst " + i + " not on the HRTF");
                }
                js.Append("]}");
            }
            js.Append("]}");
            Save(tag, js.ToString());
            Plugin.L.LogInfo(string.Format(Inv, "hrtf sweep '{0}': {1} pass(es) x {2} bursts, ends at DSP {3:0.000} (now {4:0.000})", tag, passes.Length, pos.Count, t, AudioSettings.dspTime));
            while (AudioSettings.dspTime < t - 1.3) { Reap(live); yield return null; }
            Destroy(root);
            m_busy = false;
            Plugin.L.LogInfo("hrtf sweep '" + tag + "' done");
        }

        /// Sources whose sound has ended go (the sweep keeps only a few alive).
        static void Reap(List<KeyValuePair<AudioSource, double>> live)
        {
            double now = AudioSettings.dspTime;
            for (int i = live.Count - 1; i >= 0; i--)
                if (now > live[i].Value) { if (live[i].Key != null) Destroy(live[i].Key.gameObject); live.RemoveAt(i); }
        }

        // ---- the 3 dB quirk (an experiment) --------------------------------------------------------------------------

        /// The variants, played round-robin at azimuth 60 (the same HRTF): which ones come out 3 dB quiet?
        ///   new-before  a new source, spatialize set before PlayScheduled (as the sweep)
        ///   new-after   a new source, PlayScheduled first, spatialize set right after (the play hook's order)
        ///   new-late    a new source, spatialize set 30 ms after its sound started
        ///   reuse       one source flagged once and played again every round
        ///   stereo      a new source with a 2-channel clip (the same burst in both)
        ///   spread      a new source with spread 60
        ///   pan         a new source with stereoPan 0.5
        ///   play        a new source, flagged, Play() at the time (as the game's effects start)
        ///   oneshot     a new source, flagged, PlayOneShot(clip) at the time
        ///   policy-hook a new source NOT exempt (the mod's own, Mark without flagging): Play(), the play hook's policy flags it after
        ///               the voice started - today's path for a game source's first play
        ///   policy-pre  the same, the policy applied right before Play() (Hrtf.BeforePlay: the sound effects' pre-play hook)
        static readonly string[] QuirkVariants = { "new-before", "new-after", "new-late", "reuse", "stereo", "spread", "pan", "play", "oneshot", "policy-hook", "policy-pre" };
        static AudioClip s_burstStereo;

        static AudioClip BurstStereo()
        {
            if (s_burstStereo != null) return s_burstStereo;
            int rate = Rate, n = (int)(rate * BurstSecs);
            float[] m = Pink(n, Seed);
            Ramp(m, rate / 200);
            var d = new float[n * 2];
            for (int i = 0; i < n; i++) { d[2 * i] = m[i]; d[2 * i + 1] = m[i]; }
            s_burstStereo = AudioClip.Create("HoF2VR_HrtfBurstStereo", n, 2, rate, false);
            s_burstStereo.SetData(d, 0);
            return s_burstStereo;
        }

        /// A source the policy decides about (not exempt): the mod's own, marked without flagging, mixed straight to the master.
        static AudioSource PolicySource(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            AudioSource a = go.AddComponent<AudioSource>();
            s_mine.Add(a.GetInstanceID());
            a.playOnAwake = false;
            a.priority = 0;
            a.dopplerLevel = 0f;
            a.spread = 0f;
            a.spatialBlend = 1f;
            a.rolloffMode = AudioRolloffMode.Linear;
            a.minDistance = 50f;
            a.maxDistance = 100f;
            Hrtf.Mark(a, false);
            return a;
        }

        public static string Quirk(string[] a)
        {
            if (!TestMode.Enabled) return "test runs only";
            if (I == null || I.m_busy) return "busy";
            if (!Hrtf.Ready) return "HRTF not ready: " + Hrtf.Reason;
            if (Ears == null) return "no ears";
            string tag = a.Length > 2 ? a[2] : "quirk";
            int rounds = a.Length > 3 ? int.Parse(a[3], Inv) : 6;
            string[] vs = a.Length > 4 ? a[4].Split(',') : QuirkVariants;
            foreach (string v in vs) if (Array.IndexOf(QuirkVariants, v) < 0) return "no variant " + v;
            I.StartCoroutine(I.QuirkRoutine(tag, rounds, vs));
            return "quirk test '" + tag + "': " + rounds + " rounds x " + vs.Length + " variants (" + string.Join(",", vs) + ")";
        }

        IEnumerator QuirkRoutine(string tag, int rounds, string[] variants)
        {
            m_busy = true;
            yield return null;
            yield return null;
            var root = new GameObject("HoF2VR_HrtfQuirk");
            DontDestroyOnLoad(root);
            Transform ears = Ears;
            float scale = VRRig.I != null ? VRRig.I.Scale : 1f;
            Vector3 pos = ears.position + ears.rotation * Dir(60f, 0f) * (Dist * scale);
            AudioClip burst = Burst(), chirp = Chirp(), stereo = BurstStereo();
            AudioSource reuse = NewSource("reuse", root.transform, true);
            reuse.clip = burst;
            reuse.transform.position = pos;
            Hrtf.ForceFlag(reuse, true);
            AudioSource c = NewSource("chirp", root.transform, false);
            c.clip = chirp;
            double t0 = AudioSettings.dspTime + 0.6;
            c.PlayScheduled(t0);
            var js = new StringBuilder();
            int buf, nbuf;
            AudioSettings.GetDSPBufferSize(out buf, out nbuf);
            js.AppendFormat(Inv, "{{\"tag\":\"{0}\",\"kind\":\"quirk\",\"rate\":{1},\"buffer\":{2},\"burst_s\":{3},\"chirp\":{{\"s\":{4},\"f0\":{5},\"f1\":{6}}},\"chirp_t\":{7:0.000000},\"az\":60,\"bursts\":[",
                Esc(tag), AudioSettings.outputSampleRate, buf, BurstSecs, ChirpSecs, ChirpF0, ChirpF1, t0);
            var live = new List<KeyValuePair<AudioSource, double>>();
            AudioSource late = null;
            double lateAt = 0, t = t0 + 1.0;
            int k = 0;
            for (int r = 0; r < rounds; r++)
                foreach (string v in variants)
                {
                    bool call = v == "play" || v == "oneshot" || v.StartsWith("policy");
                    while (AudioSettings.dspTime < t - (call ? 0.004 : 0.3))
                    {
                        if (late != null && AudioSettings.dspTime >= lateAt) { Hrtf.ForceFlag(late, true); late = null; }
                        Reap(live);
                        yield return null;
                    }
                    if (late != null) { Hrtf.ForceFlag(late, true); late = null; }
                    AudioSource src = v == "reuse" ? reuse : v.StartsWith("policy") ? PolicySource(v + r, root.transform) : NewSource(v + r, root.transform, true);
                    if (v != "reuse")
                    {
                        src.transform.position = pos;
                        src.clip = v == "stereo" ? stereo : v == "oneshot" ? null : burst;
                        if (v == "spread") src.spread = 60f;
                        if (v == "pan") src.panStereo = 0.5f;
                    }
                    double at = t;
                    switch (v)
                    {
                        case "new-after": src.PlayScheduled(t); Hrtf.ForceFlag(src, true); break;
                        case "new-late": src.PlayScheduled(t); late = src; lateAt = t + 0.03; break;
                        case "play": Hrtf.ForceFlag(src, true); at = AudioSettings.dspTime; src.Play(); break;
                        case "oneshot": Hrtf.ForceFlag(src, true); at = AudioSettings.dspTime; src.PlayOneShot(burst, 1f); break;
                        case "policy-hook": at = AudioSettings.dspTime; src.Play(); break;
                        case "policy-pre": Hrtf.BeforePlay(src); at = AudioSettings.dspTime; src.Play(); break;
                        case "reuse": src.PlayScheduled(t); break;
                        default: Hrtf.ForceFlag(src, true); src.PlayScheduled(t); break;
                    }
                    float fv;
                    bool attached = src.GetSpatializerFloat(7, out fv);
                    js.AppendFormat(Inv, "{0}{{\"i\":{1},\"round\":{2},\"variant\":\"{3}\",\"t\":{4:0.000000},\"call\":{5},\"spatialize\":{6},\"attached\":{7}}}",
                        k > 0 ? "," : "", k, r, v, at, call ? "true" : "false", src.spatialize ? "true" : "false", attached ? "true" : "false");
                    if (v != "reuse") live.Add(new KeyValuePair<AudioSource, double>(src, t + BurstSecs + 0.3));
                    k++;
                    t += Gap;
                }
            js.Append("]}");
            Save(tag, js.ToString());
            while (AudioSettings.dspTime < t + 0.3)
            {
                if (late != null && AudioSettings.dspTime >= lateAt) { Hrtf.ForceFlag(late, true); late = null; }
                Reap(live);
                yield return null;
            }
            Destroy(root);
            m_busy = false;
            Plugin.L.LogInfo("hrtf quirk '" + tag + "' done: " + k + " bursts");
        }

        // ---- a game source of its own: the Dealer's voice, an enemy -----------------------------------------------------

        /// `hrtf where dealer|enemy`: where that source is from the ears (the test turns the head by it).
        public static string WhereCmd(string[] a)
        {
            string kind = a.Length > 2 ? a[2] : "enemy", what;
            AudioSource src = FindSource(kind == "dealer" ? "dealer" : "enemy", out what);
            if (src == null) return "none: " + what;
            return "{" + Where(src.transform.position) + ",\"source\":\"" + Esc(what) + "\",\"hrtf\":" + (Hrtf.On(src) ? "true" : "false") + "}";
        }

        /// The Dealer's own voice source, or the nearest live enemy's own 3D source (EnemyNPC first, then Vocals, any other).
        static AudioSource FindSource(string kind, out string what)
        {
            AudioSource src = null;
            what = "-";
            if (kind == "dealer")
            {
                Voice v = Voice.Instance;
                src = v != null ? v.Source : null;
                what = src != null ? VRUI.Path(src.transform) : "no Voice source";
            }
            else
            {
                Transform ears = Ears;
                float best = float.MaxValue;
                foreach (AIController ai in FindObjectsOfType<AIController>())
                {
                    Destroyable d = ai.GetComponent<Destroyable>();
                    if (d != null && d.IsDead) continue;
                    AudioSource pick = null;
                    foreach (AudioSource s in ai.GetComponentsInChildren<AudioSource>(true))
                    {
                        if (!s.isActiveAndEnabled || s.spatialBlend < 0.5f || s.outputAudioMixerGroup == null) continue;
                        string g = s.outputAudioMixerGroup.name;
                        if (pick == null || g == "EnemyNPC" || (g == "Vocals" && pick.outputAudioMixerGroup.name != "EnemyNPC")) pick = s;
                    }
                    if (pick == null) continue;
                    float dd = ears != null ? (ai.transform.position - ears.position).sqrMagnitude : 0f;
                    if (dd < best) { best = dd; src = pick; what = VRUI.Path(ai.transform) + " -> " + VRUI.Path(pick.transform); }
                }
                if (src == null) src = EnemyEffectSource(out what);
            }
            return src;
        }

        static AudioSource s_enemyProbe;

        /// The game plays enemy sounds from effect objects it spawns at the enemy (SoundEffectController), not from sources on
        /// the enemy. So: a source at the nearest live enemy's chest, with the mixer group and 3D settings of one of the game's
        /// own enemy sound sources (EnemyNPC first, then Vocals / Enemies), NOT exempt: the play hook's policy flags it as it
        /// would the game's effect.
        static AudioSource EnemyEffectSource(out string what)
        {
            what = "no live enemy";
            Transform ears = Ears;
            AIController near = null;
            float best = float.MaxValue;
            foreach (AIController ai in FindObjectsOfType<AIController>())
            {
                Destroyable d = ai.GetComponent<Destroyable>();
                if (d != null && d.IsDead) continue;
                float dd = ears != null ? (ai.transform.position - ears.position).sqrMagnitude : 0f;
                if (dd < best) { best = dd; near = ai; }
            }
            if (near == null) return null;
            AudioSource model = null;
            int rank = 9;
            foreach (AudioSource a in Resources.FindObjectsOfTypeAll<AudioSource>())
            {
                if (a == null || a.outputAudioMixerGroup == null || a.spatialBlend < 0.5f) continue;
                string g = a.outputAudioMixerGroup.name;
                int r = g == "EnemyNPC" ? 0 : g == "Vocals" ? 1 : g == "Enemies" ? 2 : 9;
                if (r < rank) { rank = r; model = a; }
            }
            if (model == null) { what = "no game sound source in group EnemyNPC / Vocals / Enemies"; return null; }
            if (s_enemyProbe == null)
            {
                var go = new GameObject("HoF2VR_HrtfEnemySound");
                s_enemyProbe = go.AddComponent<AudioSource>();
                s_enemyProbe.playOnAwake = false;
            }
            AudioSource p = s_enemyProbe;
            p.transform.SetParent(near.transform, false);
            p.transform.localPosition = Vector3.up * 1.3f;
            p.outputAudioMixerGroup = model.outputAudioMixerGroup;
            p.spatialBlend = 1f;
            p.rolloffMode = model.rolloffMode;
            p.minDistance = model.minDistance;
            p.maxDistance = model.maxDistance;
            p.spread = model.spread;
            p.dopplerLevel = 0f;
            p.priority = model.priority;
            if (!p.gameObject.activeSelf) p.gameObject.SetActive(true);
            what = VRUI.Path(near.transform) + " -> a source at its chest like '" + model.name + "' (group " + model.outputAudioMixerGroup.name + ", " +
                   model.rolloffMode + " " + model.minDistance.ToString("0.#", Inv) + ".." + model.maxDistance.ToString("0.#", Inv) + ", spread " + model.spread.ToString("0", Inv) + ")";
            return p;
        }

        public static string Measure(string[] a, string kind)
        {
            if (!TestMode.Enabled) return "test runs only";
            if (I == null || I.m_busy) return "busy";
            string tag = a.Length > 2 ? a[2] : kind;
            int n = a.Length > 3 ? int.Parse(a[3], Inv) : 6;
            string what;
            AudioSource src = FindSource(kind, out what);
            if (src == null) return "no source: " + what;
            I.StartCoroutine(I.MeasureRoutine(src, kind, tag, n, what));
            return "measuring '" + tag + "': " + what;
        }

        IEnumerator MeasureRoutine(AudioSource src, string kind, string tag, int n, string what)
        {
            m_busy = true;
            m_keep = src;
            if (src.mute) { src.mute = false; }
            yield return null;
            yield return null;
            AudioClip burst = Burst(), chirp = Chirp();
            AudioSource c = NewSource("HoF2VR_HrtfChirp", null, false);
            c.clip = chirp;
            double t0 = AudioSettings.dspTime + 0.5;
            c.PlayScheduled(t0);
            var js = new StringBuilder();
            int buf, nbuf;
            AudioSettings.GetDSPBufferSize(out buf, out nbuf);
            js.AppendFormat(Inv, "{{\"tag\":\"{0}\",\"kind\":\"{1}\",\"source\":\"{2}\",\"group\":\"{3}\",\"rate\":{4},\"buffer\":{5},\"burst_s\":{6},\"chirp\":{{\"s\":{7},\"f0\":{8},\"f1\":{9}}},\"seed\":{10},\"head\":{11},\"hrtf_setting\":{12},\"chirp_t\":{13:0.000000},\"bursts\":[",
                Esc(tag), kind, Esc(what), src.outputAudioMixerGroup != null ? Esc(src.outputAudioMixerGroup.name) : "-", AudioSettings.outputSampleRate, buf, BurstSecs,
                ChirpSecs, ChirpF0, ChirpF1, Seed, HeadJson(), Settings.HrtfOn ? "true" : "false", t0);
            double next = t0 + 1.0;
            for (int i = 0; i < n; i++)
            {
                while (AudioSettings.dspTime < next - 0.02) yield return null;
                if (src == null) break;
                double at = AudioSettings.dspTime;
                src.PlayOneShot(burst, 1f);   // through the play hook: the policy decides, as for any sound of its own
                float v;
                bool attached = src.GetSpatializerFloat(7, out v);
                js.AppendFormat(Inv, "{0}{{\"t_call\":{1:0.000000},\"spatialize\":{2},\"attached\":{3},\"on_hrtf\":{4},\"blend\":{5:0.00},\"where\":{{{6}}}}}",
                    i > 0 ? "," : "", at, src.spatialize ? "true" : "false", attached ? "true" : "false", Hrtf.On(src) ? "true" : "false", src.spatialBlend, Where(src.transform.position));
                next += 0.8;
            }
            js.Append("]}");
            Save(tag, js.ToString());
            double end = next + 0.3;
            while (AudioSettings.dspTime < end) yield return null;
            Destroy(c.gameObject);
            m_keep = null;
            m_busy = false;
            Plugin.L.LogInfo("hrtf " + kind + " '" + tag + "' done: " + what);
        }
    }
}

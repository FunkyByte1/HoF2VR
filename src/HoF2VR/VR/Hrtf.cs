using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;
using SA = HoF2VR.SteamAudio;

namespace HoF2VR
{
    /// 3D sound with a real HRTF (docs/hrtf-plan.md): Steam Audio 4.8.1 in Unity's own spatializer slot.
    ///  * The data patch (scripts/patch_vr.py) names "Steam Audio Spatializer" and preloads libaudioplugin_phonon.so;
    ///    install.sh puts the plugin and libphonon.so into <Data>/Plugins/x86_64.
    ///  * Here, once, before any sound is switched over: the Steam Audio context and the default HRTF are made over
    ///    P/Invoke and handed to the plugin (without them a spatialized source plays silence), then a self-check on a
    ///    hidden test source (the effect attaches and answers its parameters).
    ///  * Then each sound the game plays (a postfix on AudioSettings.InvokeOnAudioSourcePlay, Unity's per-play callback,
    ///    plus a pass at each scene load) gets AudioSource.spatialize when it qualifies: 3D (blend >= HrtfMinBlend), its
    ///    mixer group listed in HrtfGroups (or one of the mod's own 3D sources, Mark), not wide (spread < 120), not
    ///    ambisonic, not at the head. Everything else, and everything while [Audio] Hrtf is off, keeps Unity's panning.
    ///  * What stays: the game's mixer groups, snapshots and volume sliders (the spatializer is a per-source effect ahead
    ///    of the source's group) and Unity's roll-off curves (the plugin takes Unity's distance value). The ears are
    ///    VRAudio's head-following listener.
    ///  * Any failure (no plugin, no data patch, not stereo, wrong version, init or self-check) is logged once and nothing
    ///    is switched over: the game's own panning. Nothing is ever released or terminated (the audio thread may run on).
    internal class Hrtf : MonoBehaviour
    {
        public static Hrtf I;
        public const string PluginName = "Steam Audio Spatializer";
        // the Steam Audio spatializer's parameter indices (spatialize_effect.cpp, 4.8.1)
        const int P_ApplyDA = 0, P_ApplyAA = 1, P_Interp = 7, P_DistAttCurve = 9, P_DirectBinaural = 30;

        /// Init done and the self-check passed: sources may be switched over.
        public static bool Ready { get; private set; }
        /// Why HRTF is not working (or "ready").
        public static string Reason = "not started";
        /// Switched over right now: Ready, [Audio] Hrtf on, and not suspended (speaker mode, device change).
        public static bool Active => Ready && Settings.HrtfOn && s_suspended == null;

        static bool s_initStarted, s_initFailed, s_patched;
        static string s_suspended;            // why HRTF is paused while Ready (a speaker mode other than stereo...)
        static string s_version = "-", s_norm = "-";
        static IntPtr s_context, s_hrtf;
        static int s_rate, s_buffer, s_numBuffers;
        static double s_initMs;
        static SA.LogCallback s_logCb;        // kept here: the native side holds the pointer
        static readonly Queue<string> s_nativeLog = new Queue<string>();

        // the sources
        static readonly Dictionary<int, AudioSource> s_flagged = new Dictionary<int, AudioSource>();
        static readonly Dictionary<int, float> s_pendingParams = new Dictionary<int, float>();   // id -> give up at
        static readonly HashSet<int> s_paramsOk = new HashSet<int>();   // flagged sources whose effect took the parameters
        static readonly HashSet<int> s_own = new HashSet<int>();     // the mod's own 3D sources (Mark): any group
        static readonly HashSet<int> s_exempt = new HashSet<int>();  // test probes: the policy leaves them alone
        static readonly Dictionary<int, bool> s_groupOk = new Dictionary<int, bool>();
        static string s_groupsFrom;
        static HashSet<string> s_groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool s_lastOn;
        static Settings.HrtfInterp s_lastInterp;
        static float s_nextClean;
        public static int Plays, ProbePlays, Flags, Unflags, ParamRetries, ParamFails, HookErrors, ScenePasses;
        static string s_lastHookError;

        public const string DefaultGroups = "Dealer,Voice,Vocals,EnemyNPC,Enemies,Footsteps,Foley,Hits,ProtectionHits,Parries,Weapons,WeaponsSkills," +
                                            "Combat,SFX,Allies,AlliesFoley,AlliesVoc,Ally,Kallass,Brimstone,Props,Artifacts,CardTable,CardTableFoley," +
                                            "CardTableMagic,PlayerMeeple,Showcases,UI";

        // ---- the hook ---------------------------------------------------------------------------------------------------

        public static void Apply(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(AudioSettings), "InvokeOnAudioSourcePlay", new[] { typeof(AudioSource) });
                if (m == null) throw new MissingMethodException("AudioSettings.InvokeOnAudioSourcePlay");
                h.Patch(m, postfix: new HarmonyMethod(typeof(Hrtf), nameof(PlayPostfix)));
                s_patched = true;
            }
            catch (Exception e) { Plugin.L.LogWarning("Hrtf: the per-play hook is not patched (only scene loads switch sources over): " + e.Message); }
        }

        /// The policy for a source that is about to play (its voice does not exist yet): the quirk experiment's `policy-pre`.
        /// The mod itself never flags a source before its first play: in the experiment (2026-10-14 C1) every 3 dB-quiet play
        /// came from a source given spatialize before its voice started (6 of 36 early in a session), none flagged right after
        /// its Play (0 of 18) - which is what the play hook does.
        public static void BeforePlay(AudioSource s) { if (s != null) Consider(s); }

        /// Unity calls AudioSettings.InvokeOnAudioSourcePlay at the end of every AudioSource.Play / PlayOneShot.
        static void PlayPostfix(AudioSource source)
        {
            try
            {
                Plays++;
                Consider(source);
            }
            catch (Exception e)
            {
                HookErrors++;
                if (s_lastHookError != e.Message) { s_lastHookError = e.Message; Plugin.L.LogWarning("Hrtf: " + e); }
            }
        }

        /// One of the mod's own 3D sources (the narration, combat sounds, hand sounds): it qualifies whatever its group. It is
        /// switched over by the play hook at its plays, right after the voice starts (flagFirst: at once - only for tests).
        public static void Mark(AudioSource s, bool flagFirst = false)
        {
            if (s == null) return;
            s_own.Add(s.GetInstanceID());
            if (flagFirst && Active) Consider(s);
        }

        /// Whether this source plays through the HRTF right now.
        public static bool On(AudioSource s) => s != null && Active && s_flagged.ContainsKey(s.GetInstanceID()) && s.spatialize;

        // ---- life -------------------------------------------------------------------------------------------------------

        void Awake()
        {
            I = this;
            SceneManager.sceneLoaded += (s, m) => { if (Active) ScenePass("scene " + s.name); };
            AudioSettings.OnAudioConfigurationChanged += OnConfigChanged;
            Diag.Commands["hrtf"] = Command;
            Diag.StatusExtras["hrtf"] = () => Diag.Q(Short());
            s_lastOn = Settings.HrtfOn;
            s_lastInterp = Settings.HrtfInterpolation;
            if (Settings.HrtfOn) StartInit();
            else Reason = "off ([Audio] Hrtf = false)";
        }

        void Update()
        {
            DrainNativeLog();
            bool on = Settings.HrtfOn;
            if (on != s_lastOn)
            {
                s_lastOn = on;
                Plugin.L.LogInfo("Hrtf: [Audio] Hrtf " + (on ? "on" : "off"));
                if (on)
                {
                    if (!s_initStarted) StartInit();
                    else if (Active) ScenePass("switched on");
                }
                else UnflagAll("switched off");
            }
            if (Settings.HrtfInterpolation != s_lastInterp)
            {
                s_lastInterp = Settings.HrtfInterpolation;
                foreach (AudioSource s in s_flagged.Values) if (s != null) ApplyParams(s);
            }
            if (s_pendingParams.Count > 0) RetryParams();
            if (Time.unscaledTime >= s_nextClean)
            {
                s_nextClean = Time.unscaledTime + 5f;
                Clean();
            }
        }

        // ---- init -------------------------------------------------------------------------------------------------------

        void StartInit()
        {
            if (s_initStarted) return;
            s_initStarted = true;
            StartCoroutine(InitRoutine());
        }

        IEnumerator InitRoutine()
        {
            // the audio system answers from the first frames on; wait (at most 5 s) for a rate and a buffer
            float until = Time.realtimeSinceStartup + 5f;
            while (AudioSettings.outputSampleRate <= 0 && Time.realtimeSinceStartup < until) yield return null;
            string why = InitNative();
            if (why != null) { Fail(why); yield break; }
            // self-check: a hidden source takes the effect and answers its parameters
            var go = new GameObject("HoF2VR_HrtfCheck");
            DontDestroyOnLoad(go);
            AudioSource a = go.AddComponent<AudioSource>();
            s_exempt.Add(a.GetInstanceID());
            a.playOnAwake = false;
            a.volume = 0f;
            a.spatialBlend = 1f;
            a.clip = AudioClip.Create("HoF2VR_HrtfCheckSilence", Mathf.Max(256, s_rate / 10), 1, Mathf.Max(8000, s_rate), false);
            float v;
            bool attached = false, answers = false;
            string err = null;
            try
            {
                a.spatialize = true;
                attached = a.GetSpatializerFloat(P_Interp, out v);
                if (!attached) a.Play();
            }
            catch (Exception e) { err = e.Message; }
            for (int i = 0; i < 30 && !attached && err == null; i++)
            {
                yield return null;
                try { attached = a.GetSpatializerFloat(P_Interp, out v); } catch (Exception e) { err = e.Message; }
            }
            try
            {
                if (attached)
                    answers = a.SetSpatializerFloat(P_Interp, 1f) && a.GetSpatializerFloat(P_Interp, out v) && Mathf.Abs(v - 1f) < 0.01f &&
                              a.SetSpatializerFloat(P_Interp, 0f) && a.GetSpatializerFloat(P_Interp, out v) && Mathf.Abs(v) < 0.01f;
                a.Stop();
                a.spatialize = false;
            }
            catch (Exception e) { err = err ?? e.Message; }
            Destroy(go);
            if (err != null) { Fail("self-check: " + err); yield break; }
            if (!attached) { Fail("self-check: the spatializer did not attach to a test source (the plugin's effect is not registered - see Player.log 'Plugins:' / 'audioplugin_phonon')"); yield break; }
            if (!answers) { Fail("self-check: the spatializer attached but does not answer its parameters"); yield break; }
            Ready = true;
            Reason = "ready";
            Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "Hrtf: ready - Steam Audio {0}, '{1}', {2} Hz, DSP buffer {3} x {4}, {5}, interpolation {6}, normalization {9}, gain {10:0.#} dB, init {7:0.0} ms, self-check attached and answering; hook {8}",
                s_version, AudioSettings.GetSpatializerPluginName(), s_rate, s_buffer, s_numBuffers, AudioSettings.speakerMode, Settings.HrtfInterpolation, s_initMs,
                s_patched ? "patched" : "MISSING (scene loads only)", Settings.HrtfNormalization, Settings.HrtfGainDb));
            if (Active) ScenePass("ready");
        }

        static void Fail(string why)
        {
            s_initFailed = true;
            Ready = false;
            Reason = why;
            Plugin.L.LogWarning("Hrtf: not used - " + why + ". Sounds keep the game's own panning.");
        }

        /// Context, HRTF, plugin. Returns null when done, else why not.
        static string InitNative()
        {
            // the files first: without the plugin Unity also reports an empty spatializer name (r8: it blanks a name it
            // could not load), which would read as a missing data patch
            string dir = Path.Combine(Path.Combine(Application.dataPath, "Plugins"), "x86_64");
            foreach (string f in new[] { "libaudioplugin_phonon.so", "libphonon.so" })
                if (!File.Exists(Path.Combine(dir, f))) return "plugin missing (" + f + " not in " + dir + ")";
            string name = AudioSettings.GetSpatializerPluginName();
            if (name != PluginName)
                return "no spatializer configured (spatializer '" + name + "': the data patch is not in - install.sh without --no-hrtf - or Unity could not load the plugin - Player.log 'Preloaded')";
            if (AudioSettings.speakerMode != AudioSpeakerMode.Stereo) return "speaker mode " + AudioSettings.speakerMode + " (HRTF needs stereo)";
            s_rate = AudioSettings.outputSampleRate;
            AudioSettings.GetDSPBufferSize(out s_buffer, out s_numBuffers);
            if (s_rate <= 0 || s_buffer <= 0) return "no audio output (rate " + s_rate + ", buffer " + s_buffer + ")";
            var sw = Stopwatch.StartNew();
            try
            {
                uint major, minor, patch;
                SA.API.iplUnityGetVersion(out major, out minor, out patch);
                s_version = major + "." + minor + "." + patch;
                if (major != SA.Constants.kVersionMajor || minor != SA.Constants.kVersionMinor) return "plugin version " + s_version + " (built for 4.8.x)";
                var cs = new SA.ContextSettings
                {
                    version = SA.Constants.kVersion,
                    simdLevel = SA.SIMDLevel.AVX2,
                };
                if (TestMode.Enabled)
                {
                    s_logCb = NativeLog;   // test runs only: the callback may come from the audio thread; it only queues
                    cs.logCallback = s_logCb;
                }
                SA.Error e = SA.API.iplContextCreate(ref cs, out s_context);
                if (e != SA.Error.Success || s_context == IntPtr.Zero) return "iplContextCreate: " + e;
                var aus = new SA.AudioSettings { samplingRate = s_rate, frameSize = s_buffer };
                string sofa = Settings.HrtfSofaFile;
                var hs = new SA.HRTFSettings
                {
                    type = string.IsNullOrEmpty(sofa) ? SA.HRTFType.Default : SA.HRTFType.SOFA,
                    sofaFileName = string.IsNullOrEmpty(sofa) ? null : sofa,
                    volume = Mathf.Pow(10f, Settings.HrtfGainDb / 20f),
                    normType = Settings.HrtfNormalization == Settings.HrtfNorm.RMS ? SA.HRTFNormType.RMS : SA.HRTFNormType.None,
                };
                s_norm = hs.normType.ToString();
                e = SA.API.iplHRTFCreate(s_context, ref aus, ref hs, out s_hrtf);
                if (e != SA.Error.Success || s_hrtf == IntPtr.Zero) return "iplHRTFCreate (" + (string.IsNullOrEmpty(sofa) ? "default" : sofa) + "): " + e;
                SA.API.iplUnityInitialize(s_context);
                SA.API.iplUnitySetHRTF(s_hrtf);
            }
            catch (DllNotFoundException ex) { return "plugin not loadable: " + ex.Message; }
            catch (EntryPointNotFoundException ex) { return "plugin entry point missing: " + ex.Message; }
            catch (Exception ex) { return "init: " + ex.GetType().Name + " " + ex.Message; }
            s_initMs = sw.Elapsed.TotalMilliseconds;
            return null;
        }

        static void NativeLog(SA.LogLevel level, string message)
        {
            lock (s_nativeLog) { if (s_nativeLog.Count < 200) s_nativeLog.Enqueue(level + ": " + message); }
        }

        static void DrainNativeLog()
        {
            if (s_logCb == null) return;
            lock (s_nativeLog)
                while (s_nativeLog.Count > 0) Plugin.L.LogInfo("Hrtf: Steam Audio " + s_nativeLog.Dequeue());
        }

        /// The output changed (the game's Speaker Mode option, a device change). A speaker mode other than stereo pauses
        /// HRTF; back to stereo with the same rate and buffer resumes it. A new rate or buffer size would need a new HRTF
        /// while the audio thread runs: HRTF stays off until the game restarts (logged).
        static void OnConfigChanged(bool deviceWasChanged)
        {
            try
            {
                int rate = AudioSettings.outputSampleRate, buf, num;
                AudioSettings.GetDSPBufferSize(out buf, out num);
                AudioSpeakerMode mode = AudioSettings.speakerMode;
                Plugin.L.LogInfo("Hrtf: audio configuration changed (device changed " + deviceWasChanged + "): " + rate + " Hz, buffer " + buf + " x " + num + ", " + mode);
                if (!Ready) return;
                if (rate != s_rate || buf != s_buffer)
                {
                    Ready = false;
                    s_initFailed = true;
                    Reason = "the audio output changed to " + rate + " Hz / buffer " + buf + " (the HRTF was made for " + s_rate + " / " + s_buffer + "): off until the game restarts";
                    UnflagAll("output changed");
                    Plugin.L.LogWarning("Hrtf: " + Reason);
                    return;
                }
                if (mode != AudioSpeakerMode.Stereo)
                {
                    if (s_suspended != null) return;
                    s_suspended = "speaker mode " + mode + " (HRTF needs stereo)";
                    UnflagAll(s_suspended);
                    Plugin.L.LogWarning("Hrtf: paused - " + s_suspended);
                    return;
                }
                if (s_suspended != null)
                {
                    Plugin.L.LogInfo("Hrtf: resumed (" + mode + " again; was: " + s_suspended + ")");
                    s_suspended = null;
                    if (Active) ScenePass("resumed");
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Hrtf: configuration change: " + e.Message); }
        }

        // ---- the policy -------------------------------------------------------------------------------------------------

        static void Consider(AudioSource s)
        {
            if (s == null) return;
            int id = s.GetInstanceID();
            if (s_exempt.Contains(id)) { ProbePlays++; return; }
            if (Qualifies(s, id) == null)
            {
                if (!s_flagged.ContainsKey(id) || !s.spatialize) Flag(s, id);
                // flagged while its object was inactive, its effect came only now: the parameters (bilinear...) now
                else if (!s_paramsOk.Contains(id) && ApplyParams(s)) { s_paramsOk.Add(id); s_pendingParams.Remove(id); ParamRetries++; }
            }
            else if (s.spatialize || s_flagged.ContainsKey(id)) Unflag(s, id);
        }

        /// null when the source should play through the HRTF, else why not.
        static string Qualifies(AudioSource s, int id)
        {
            if (!Active) return Ready ? (s_suspended ?? "off") : Reason;
            if (s.spatialBlend < Settings.HrtfMinBlend) return "2D";
            if (!s_own.Contains(id))
            {
                AudioMixerGroup g = s.outputAudioMixerGroup;
                if (g == null) return "no mixer group";
                if (!GroupOk(g)) return "group";
            }
            if (s.spread >= 120f) return "wide";
            AudioClip c = s.clip;
            if (c != null && c.ambisonic) return "ambisonic";
            Transform ears = VRAudio.EarsTransform;
            if (ears != null)
            {
                float scale = VRRig.I != null ? VRRig.I.Scale : 1f;
                float min = Settings.HrtfMinDistance * scale;
                if ((s.transform.position - ears.position).sqrMagnitude < min * min) return "at the head";
            }
            return null;
        }

        static bool GroupOk(AudioMixerGroup g)
        {
            string list = Settings.HrtfGroups;
            if (!ReferenceEquals(list, s_groupsFrom))
            {
                s_groupsFrom = list;
                s_groups = new HashSet<string>(list.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                s_groupOk.Clear();
            }
            int gid = g.GetInstanceID();
            bool ok;
            if (!s_groupOk.TryGetValue(gid, out ok)) s_groupOk[gid] = ok = s_groups.Contains(g.name);
            return ok;
        }

        static void Flag(AudioSource s, int id)
        {
            if (!s.spatialize) { s.spatialize = true; Flags++; }
            s_flagged[id] = s;
            if (ApplyParams(s)) s_paramsOk.Add(id);
            else { s_paramsOk.Remove(id); s_pendingParams[id] = Time.unscaledTime + 2f; }
        }

        static void Unflag(AudioSource s, int id)
        {
            if (s != null && s.spatialize) { s.spatialize = false; Unflags++; }
            s_flagged.Remove(id);
            s_pendingParams.Remove(id);
            s_paramsOk.Remove(id);
        }

        /// The effect's parameters: Valve's defaults, plus the interpolation (bilinear: smooth while the head turns).
        static bool ApplyParams(AudioSource s)
        {
            if (!s.SetSpatializerFloat(P_Interp, Settings.HrtfInterpolation == Settings.HrtfInterp.Bilinear ? 1f : 0f)) return false;
            s.SetSpatializerFloat(P_ApplyDA, 1f);           // distance attenuation on ...
            s.SetSpatializerFloat(P_DistAttCurve, 1f);      // ... from Unity's own curve (the game's roll-off decides loudness)
            s.SetSpatializerFloat(P_DirectBinaural, 1f);
            s.SetSpatializerFloat(P_ApplyAA, Settings.HrtfAirAbsorption ? 1f : 0f);
            return true;
        }

        static void RetryParams()
        {
            List<int> done = null;
            foreach (var kv in s_pendingParams)
            {
                AudioSource s;
                bool gone = !s_flagged.TryGetValue(kv.Key, out s) || s == null;
                if (!gone && ApplyParams(s)) { ParamRetries++; s_paramsOk.Add(kv.Key); (done = done ?? new List<int>()).Add(kv.Key); }
                else if (gone || Time.unscaledTime > kv.Value)
                {
                    // (an inactive object has no effect yet: it gets the parameters at its next play, in Consider)
                    if (!gone && s.isActiveAndEnabled) { ParamFails++; Plugin.L.LogWarning("Hrtf: no spatializer parameters on '" + VRUI.Path(s.transform) + "' after 2 s (it keeps Valve's defaults)"); }
                    (done = done ?? new List<int>()).Add(kv.Key);
                }
            }
            if (done != null) foreach (int id in done) s_pendingParams.Remove(id);
        }

        /// Sources already playing (loops, ambience) go over now; the rest at their next play, through the hook - right after
        /// their voice started, never before (the 3 dB quirk: flagging before the first play can leave that play 3 dB quiet).
        static void ScenePass(string why)
        {
            ScenePasses++;
            int before = s_flagged.Count;
            foreach (AudioSource s in FindObjectsOfType<AudioSource>()) if (s.isPlaying || s.spatialize) Consider(s);
            Plugin.L.LogInfo("Hrtf: " + why + ": " + s_flagged.Count + " sources on the HRTF (" + (s_flagged.Count - before).ToString("+0;-0;0") + ")");
        }

        static void UnflagAll(string why)
        {
            int n = 0;
            foreach (var kv in s_flagged.ToArray()) { if (kv.Value != null && kv.Value.spatialize) n++; Unflag(kv.Value, kv.Key); }
            Plugin.L.LogInfo("Hrtf: " + why + ": " + n + " sources back on Unity's panning");
        }

        static void Clean()
        {
            List<int> gone = null;
            foreach (var kv in s_flagged) if (kv.Value == null) (gone = gone ?? new List<int>()).Add(kv.Key);
            if (gone != null) foreach (int id in gone) { s_flagged.Remove(id); s_pendingParams.Remove(id); s_paramsOk.Remove(id); }
            if (s_own.Count > 256 || s_exempt.Count > 256)   // ids of destroyed sources: never reused, harmless; keep the sets small
            {
                var live = new HashSet<int>(Resources.FindObjectsOfTypeAll<AudioSource>().Select(a => a.GetInstanceID()));
                s_own.IntersectWith(live);
                s_exempt.IntersectWith(live);
            }
        }

        // ---- diagnostics ------------------------------------------------------------------------------------------------

        static string Short()
        {
            if (!Ready) return "off: " + Reason;
            return (Active ? "on" : "paused: " + (s_suspended ?? "[Audio] Hrtf = false")) + " " + s_version + " " + s_rate + "/" + s_buffer + " flagged " + s_flagged.Count;
        }

        static string Status()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(Ready ? (Active ? "ON" : "PAUSED (" + (s_suspended ?? "[Audio] Hrtf = false") + ")") : "OFF (" + Reason + ")");
            sb.AppendFormat(inv, " | spatializer '{0}' | Steam Audio {1} | {2} Hz, buffer {3} x {4} | {5} | interp {6} | norm {9} | init {7:0.0} ms | hook {8}",
                AudioSettings.GetSpatializerPluginName(), s_version, AudioSettings.outputSampleRate, s_buffer, s_numBuffers, AudioSettings.speakerMode,
                Settings.HrtfInterpolation, s_initMs, s_patched ? "on" : "MISSING", s_norm);
            int live = 0, attached = 0;
            var byGroup = new SortedDictionary<string, int>();
            foreach (AudioSource s in s_flagged.Values)
            {
                if (s == null) continue;
                live++;
                float v;
                if (s.spatialize && s.GetSpatializerFloat(P_Interp, out v)) attached++;
                string g = s.outputAudioMixerGroup != null ? s.outputAudioMixerGroup.name : "-";
                int n;
                byGroup.TryGetValue(g, out n);
                byGroup[g] = n + 1;
            }
            sb.AppendFormat(inv, " | flagged {0} (live {1}, attached {2}) | plays {3} (test probes {11}) flags {4} unflags {5} param retries {6} fails {7} | hook errors {8} | scene passes {9} | own {10} | by group:",
                s_flagged.Count, live, attached, Plays, Flags, Unflags, ParamRetries, ParamFails, HookErrors, ScenePasses, s_own.Count, ProbePlays);
            foreach (var kv in byGroup) sb.Append(' ').Append(kv.Key).Append(' ').Append(kv.Value);
            return sb.ToString();
        }

        string Command(string[] a)
        {
            var inv = CultureInfo.InvariantCulture;
            string op = a.Length > 1 ? a[1].ToLowerInvariant() : "status";
            switch (op)
            {
                case "status": return Status();
                case "on":
                case "off":
                    Settings.SetForRun("Audio", "Hrtf", op == "on" ? "true" : "false");
                    return "[Audio] Hrtf " + op + " (this run only) - " + Short();
                case "interp":
                    if (a.Length > 2) Settings.SetForRun("Audio", "HrtfInterpolation", a[2].StartsWith("b", StringComparison.OrdinalIgnoreCase) ? "Bilinear" : "Nearest");
                    return "interpolation " + Settings.HrtfInterpolation;
                case "list":
                {
                    var sb = new StringBuilder();
                    foreach (var kv in s_flagged)
                    {
                        AudioSource s = kv.Value;
                        if (s == null) { sb.Append("\n  (gone) ").Append(kv.Key); continue; }
                        float v;
                        bool att = s.GetSpatializerFloat(P_Interp, out v);
                        sb.AppendFormat(inv, "\n  {0} spatialize={1} attached={2} interp={3} playing={4} blend={5:0.00} group={6} clip={7} at {8}",
                            VRUI.Path(s.transform), s.spatialize, att, att ? v.ToString("0", inv) : "-", s.isPlaying, s.spatialBlend,
                            s.outputAudioMixerGroup != null ? s.outputAudioMixerGroup.name : "-", s.clip != null ? s.clip.name : "-", Diag.V(s.transform.position));
                    }
                    Plugin.L.LogInfo("hrtf list: " + Status() + sb);
                    return s_flagged.Count + " flagged (see log)";
                }
                case "why":
                {
                    // every source in the scene and why it is or is not on the HRTF -> log
                    var sb = new StringBuilder();
                    var count = new SortedDictionary<string, int>();
                    foreach (AudioSource s in FindObjectsOfType<AudioSource>())
                    {
                        int id = s.GetInstanceID();
                        string w = s_exempt.Contains(id) ? "test probe" : Qualifies(s, id) ?? "HRTF";
                        int n;
                        count.TryGetValue(w, out n);
                        count[w] = n + 1;
                        sb.AppendFormat(inv, "\n  [{0}] {1} spatialize={2} blend={3:0.00} group={4}", w, VRUI.Path(s.transform), s.spatialize, s.spatialBlend,
                            s.outputAudioMixerGroup != null ? s.outputAudioMixerGroup.name : "-");
                    }
                    Plugin.L.LogInfo("hrtf why:" + sb);
                    return string.Join(", ", count.Select(kv => kv.Key + " " + kv.Value).ToArray());
                }
                case "sound":
                    if (!TestMode.Enabled) return "test runs only";
                    Diag.SoundUnlocked = a.Length > 2 && a[2] == "1";
                    AudioListener.volume = Diag.SoundUnlocked ? 1f : 0f;
                    return "test sound " + (Diag.SoundUnlocked ? "ON (listener volume 1)" : "off");
                case "probe":
                    return HrtfProbe.Probe(a);
                case "sweep":
                    return HrtfProbe.Sweep(a);
                case "say":
                    return HrtfProbe.Measure(a, "dealer");
                case "enemy":
                    return HrtfProbe.Measure(a, "enemy");
                case "where":
                    return HrtfProbe.WhereCmd(a);
                case "quirk":
                    return HrtfProbe.Quirk(a);
                case "trace":
                    return HrtfProbe.TraceCmd(a);
                case "mem":
                {
                    // the soak's bookkeeping: the process's memory and what the HRTF holds
                    long rss = 0;
                    try
                    {
                        foreach (string l in File.ReadAllLines("/proc/self/status"))
                            if (l.StartsWith("VmRSS:")) rss = long.Parse(l.Substring(6).Trim().Split(' ')[0], inv);
                    }
                    catch (Exception) { }
                    return string.Format(inv, "rss {0} kB | flagged {1} | own {2} | exempt {3} | pending {4} | paramsOk {5} | plays {6} | hook errors {7}",
                        rss, s_flagged.Count, s_own.Count, s_exempt.Count, s_pendingParams.Count, s_paramsOk.Count, Plays, HookErrors);
                }
                case "reset":
                {
                    // T7: what the game's Speaker Mode option does (AudioSettings.Reset), test runs only
                    if (!TestMode.Enabled) return "test runs only";
                    AudioConfiguration c = AudioSettings.GetConfiguration();
                    c.speakerMode = a.Length > 2 && a[2].StartsWith("m", StringComparison.OrdinalIgnoreCase) ? AudioSpeakerMode.Mono : AudioSpeakerMode.Stereo;
                    bool ok = AudioSettings.Reset(c);
                    return "AudioSettings.Reset(" + c.speakerMode + ") " + ok + " -> " + Short();
                }
                case "mute":
                    HrtfProbe.MuteOthers = a.Length > 2 && a[2] == "1";
                    return "other sources " + (HrtfProbe.MuteOthers ? "muted" : "back");
            }
            return "hrtf [status|on|off|interp nearest|bilinear|list|why|sound 0|1|probe <az> <el> <dist m> [secs] [hrtf|pan]|probe stop|sweep <tag> [hrtf|pan|ab]|say <tag> [n]|enemy <tag> [n]|where dealer|enemy|mute 0|1|reset mono|stereo]";
        }

        // ---- for the test probes (HrtfProbe) ------------------------------------------------------------------------

        internal static void Exempt(AudioSource s) { if (s != null) s_exempt.Add(s.GetInstanceID()); }

        /// A test probe on or off the HRTF, whatever the policy says (the probe is exempt from it).
        internal static bool ForceFlag(AudioSource s, bool on)
        {
            if (s == null) return false;
            if (!on) { if (s.spatialize) s.spatialize = false; return true; }
            if (!Ready) return false;
            s.spatialize = true;
            return ApplyParams(s);
        }

        internal static int Rate => s_rate > 0 ? s_rate : AudioSettings.outputSampleRate;
        internal static int Buffer => s_buffer;
    }
}

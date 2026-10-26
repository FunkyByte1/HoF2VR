using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// The Dealer reads the story aloud (docs/narration-design.md §4; the user's choices in docs/decisions.md "Narration").
    ///  * When an encounter page appears (EncounterPanel.UpdatePage: story pages, companion chats, intros - all tier-1 text)
    ///    the clip named by the hash of the shown text (else of its template) plays from ClipStore, at the chosen pace.
    ///  * It plays on our own AudioSource that mirrors the game's Dealer voice: the same place (VRAudio puts that at his
    ///    mouth in 3D at the table), the same 3D settings and the same mixer group "Dealer" (the voice slider, the pause
    ///    freeze). No Doppler.
    ///  * Never over the Dealer: it waits for his line (and 0.35 s after it), pauses when he starts one and resumes 0.35 s
    ///    after it (0.3 s back). His muted lines don't count. His idle remarks are held back while it reads.
    ///  * Choosing an option, the encounter's end, a new page or leaving the table cuts it (0.12 s fade).
    ///  * The same page is not read twice unless it is opened again (after a choice, or when the encounter comes back to it).
    ///  * A page without a clip stays silent; each missing hash is logged once.
    ///  * His mouth follows the reading (the game's VoiceFlap reads our source while it is audible) and the clip's gesture
    ///    track plays his own talk gestures (only while he idles, like the game's lines). The head motion: DealerMotion.
    internal class Narrator : MonoBehaviour
    {
        public static Narrator I;

        enum St { Idle, Waiting, Playing, Paused }
        St m_st = St.Idle;
        AudioSource m_src;
        ClipStore.Entry m_cur, m_fadeEntry;
        string m_hash = "", m_text = "", m_enc = "-", m_how = "";
        float m_since, m_playStart;
        // the game's Dealer line: audible now / chosen and about to start
        bool m_voiceBusy, m_voiceSoon;
        float m_voiceQuietSince = -100f;
        IVoiceClip m_ccSeen;
        float m_ccSeenAt;
        bool m_waitCounted;
        // fade-out
        float m_fadeStart = -1f, m_fadeLen = 0.12f, m_fadeVol;
        // gestures
        int m_gest;
        bool m_gestured;
        // the page memory (not read twice)
        Page m_lastPage;
        string m_lastHash = "";
        bool m_lastOpen;
        EncounterPanel m_panel;
        // tests
        public static int Pages, Plays, Misses, SameSkips, Pauses, Resumes, Stops, Ends, NotEnglish, Waited;
        public static string LastStop = "-", LastPlay = "-", LastMiss = "-";
        public static float LastStopMs = -1f, LastWaitedS, Rms, Flap;
        static readonly HashSet<string> s_missLogged = new HashSet<string>();
        static readonly List<string> s_misses = new List<string>();
        readonly float[] m_buf = new float[512];

        static VariableString s_lastText;
        static readonly AccessTools.FieldRef<EncounterPanel, UILabel> s_pageText = AccessTools.FieldRefAccess<EncounterPanel, UILabel>("m_pageText");
        static readonly AccessTools.FieldRef<VoiceFlap, AudioSource> s_flapSource = AccessTools.FieldRefAccess<VoiceFlap, AudioSource>("m_audioSource");
        static readonly AccessTools.FieldRef<VoiceFlap, Animator> s_flapAnim = AccessTools.FieldRefAccess<VoiceFlap, Animator>("m_drivenAnimator");

        // ---- hooks ------------------------------------------------------------------------------------------------------
        public static void Apply(Harmony h)
        {
            Try(h, typeof(Page), "GetNextText", null, nameof(GetNextTextPostfix));
            Try(h, typeof(EncounterPanel), "UpdatePage", null, nameof(UpdatePagePostfix));
            Try(h, typeof(EncounterPanel), "OnOptionChosen", nameof(OptionPrefix), null);
            Try(h, typeof(EncounterPanel), "DetachEncounter", nameof(DetachPrefix), null);
            Try(h, typeof(Voice), "PostTrigger", nameof(PostTriggerPrefix), null);
            Try(h, typeof(VoiceFlap), "Update", nameof(FlapPrefix), nameof(FlapPostfix));
            GameWatch.Changed += s =>
            {
                if (I != null && I.m_st != St.Idle && (GameWatch.InCombatScene || s.Contains("StartScreen") || s.Contains("Init") || s.Contains("Profile") || s.Contains("EndRun")))
                    I.Stop("left the table (" + s + ")");
            };
        }

        static void Try(Harmony h, Type t, string method, string prefix, string postfix)
        {
            try
            {
                h.Patch(AccessTools.Method(t, method),
                        prefix: prefix != null ? new HarmonyMethod(typeof(Narrator), prefix) : null,
                        postfix: postfix != null ? new HarmonyMethod(typeof(Narrator), postfix) : null);
            }
            catch (Exception e) { Plugin.L.LogWarning("Narrator: " + t.Name + "." + method + " not patched: " + e.Message); }
        }

        static void GetNextTextPostfix(VariableString __result) => s_lastText = __result;

        static void UpdatePagePostfix(EncounterPanel __instance, Page a_page)
        {
            VariableString vs = s_lastText;
            s_lastText = null;
            try { if (I != null) I.OnPage(__instance, a_page, vs); }
            catch (Exception e) { Plugin.L.LogError("narration page: " + e); }
        }

        static void OptionPrefix() { if (I != null) I.Advance("a choice"); }
        static void DetachPrefix() { if (I != null) I.Advance("the encounter ended"); }

        /// His idle remarks wait while the story is read (reading is activity).
        static bool PostTriggerPrefix(Voice.Trigger a_trigger) => !(a_trigger == Voice.Trigger.Idle && I != null && I.Busy);

        /// The lip flap reads our source while the reading is audible (the game's line otherwise).
        static void FlapPrefix(VoiceFlap __instance, out AudioSource __state)
        {
            __state = null;
            if (I == null || !I.Audible) return;
            __state = s_flapSource(__instance);
            s_flapSource(__instance) = I.m_src;
            FlapFrames++;
        }

        static void FlapPostfix(VoiceFlap __instance, AudioSource __state)
        {
            if (__state != null) s_flapSource(__instance) = __state;
            if (I != null && I.Audible)
            {
                Animator a = s_flapAnim(__instance);
                if (a != null && a.isActiveAndEnabled && a.layerCount > 1) Flap = a.GetCurrentAnimatorStateInfo(1).normalizedTime;
            }
        }

        // ---- life -------------------------------------------------------------------------------------------------------
        void Awake()
        {
            I = this;
            var go = new GameObject("HoF2VR_Narrator");
            DontDestroyOnLoad(go);
            m_src = go.AddComponent<AudioSource>();
            m_src.playOnAwake = false;
            m_src.loop = false;
            m_src.dopplerLevel = 0f;
            m_src.priority = 0;
            m_src.bypassReverbZones = true;
            m_src.spatialBlend = 1f;
            Hrtf.Mark(m_src);   // the narration comes from his mouth like his own lines (through the HRTF when it is on)
            Diag.Commands["narr"] = Command;
            Diag.StatusExtras["narr"] = () => Diag.Q(Describe());
        }

        public bool Busy => m_st != St.Idle;
        bool Audible => m_st == St.Playing && m_src != null && m_src.isPlaying;
        public bool Uses(ClipStore.Entry e) => e != null && (e == m_cur || e == m_fadeEntry);

        /// The clip driving his head now (DealerMotion), sampled at the source's own clock (the pause freezes it).
        public bool MotionNow(out ClipStore.Motion m, out float t)
        {
            m = null;
            t = 0f;
            if ((m_st != St.Playing && m_st != St.Paused) || m_cur == null || m_cur.Motion == null) return false;
            m = m_cur.Motion;
            t = m_src.time;
            return true;
        }

        void Update()
        {
            try { Tick(); }
            catch (Exception e)
            {
                Plugin.L.LogError("narration: " + e);
                Release(ref m_cur);
                m_st = St.Idle;
            }
        }

        void Tick()
        {
            ClipStore.Tick();
            DealerLines.Tick();
            WatchVoice();
            if (m_st != St.Idle && !NarrationSettings.Enabled) Stop("narration switched off");
            Fade();
            float now = Time.unscaledTime;
            switch (m_st)
            {
                case St.Waiting:
                    if (m_cur == null) { m_st = St.Idle; break; }
                    if (m_cur.Failed) { Miss(m_hash, m_text, "the clip did not load: " + m_cur.Error); Release(ref m_cur); m_st = St.Idle; break; }
                    if (now - m_since > 40f) { Plugin.L.LogInfo("narration: gave up waiting for " + m_hash + " (" + (m_voiceBusy ? "the Dealer kept talking" : "not ready") + ")"); Release(ref m_cur); m_st = St.Idle; break; }
                    if (!m_cur.Ready || m_fadeStart >= 0f || m_voiceBusy || m_voiceSoon || now - m_voiceQuietSince < 0.35f || !PanelShowing()) break;
                    Play();
                    break;
                case St.Playing:
                    if (m_voiceBusy)
                    {
                        m_src.Pause();
                        m_st = St.Paused;
                        Pauses++;
                        Plugin.L.LogInfo("narration: paused at " + m_src.time.ToString("0.00") + " s - the Dealer speaks");
                        break;
                    }
                    if (!m_src.isPlaying && now - m_playStart > 0.25f)
                    {
                        Ends++;
                        Plugin.L.LogInfo("narration: " + m_hash + " finished");
                        GestureEnd();
                        Release(ref m_cur);
                        m_st = St.Idle;
                        break;
                    }
                    Gestures();
                    Measure();
                    break;
                case St.Paused:
                    if (!m_voiceBusy && now - m_voiceQuietSince >= 0.35f)
                    {
                        m_src.time = Mathf.Max(0f, m_src.time - 0.3f);
                        m_src.UnPause();
                        m_st = St.Playing;
                        Resumes++;
                        Plugin.L.LogInfo("narration: resumed at " + m_src.time.ToString("0.00") + " s");
                    }
                    break;
            }
            if (m_st != St.Playing) Rms = 0f;
            if (Busy || DealerLines.Busy) MeasureVoice();
        }

        /// Mirrors the game's Dealer voice: its place and 3D settings (VRAudio puts it at his mouth at the table) and its
        /// mixer group.
        void LateUpdate()
        {
            if (m_src == null) return;
            Voice v = Voice.Instance;
            AudioSource g = v != null ? v.Source : null;
            if (g != null)
            {
                m_src.transform.position = g.transform.position;
                m_src.spatialBlend = g.spatialBlend;
                m_src.rolloffMode = g.rolloffMode;
                m_src.minDistance = g.minDistance;
                m_src.maxDistance = g.maxDistance;
                m_src.spread = g.spread;
                if (m_src.outputAudioMixerGroup != g.outputAudioMixerGroup) m_src.outputAudioMixerGroup = g.outputAudioMixerGroup;
            }
            m_src.dopplerLevel = 0f;
            if (m_fadeStart < 0f) m_src.volume = NarrationSettings.Volume;
        }

        // ---- the game's own line ------------------------------------------------------------------------------------------
        void WatchVoice()
        {
            Voice v = Voice.Instance;
            IVoiceClip cc = v != null ? v.CurrentClip : null;
            bool busy = false, soon = false;
            if (cc != null)
            {
                AudioSource a = null;
                try { a = cc.Audio; } catch (Exception) { }
                if (a != null && a.isPlaying) busy = !DealerLines.IsSilent(a.clip);
                else
                {
                    // chosen but not started yet (its delay, the load): a reading waits for it (up to 3 s); one that
                    // plays already only pauses once he is really heard (a muted line never pauses it)
                    if (cc != m_ccSeen) { m_ccSeen = cc; m_ccSeenAt = Time.unscaledTime; }
                    soon = Time.unscaledTime - m_ccSeenAt < 3f;
                }
            }
            else m_ccSeen = null;
            if (busy) m_voiceQuietSince = Time.unscaledTime;
            if ((busy || soon) && m_st == St.Waiting && !m_waitCounted) { m_waitCounted = true; Waited++; }
            m_voiceBusy = busy;
            m_voiceSoon = soon;
        }

        // ---- pages ------------------------------------------------------------------------------------------------------
        void OnPage(EncounterPanel panel, Page page, VariableString vs)
        {
            m_panel = panel;
            if (!NarrationSettings.Enabled) return;
            Pages++;
            LanguageManager lm = LanguageManager.Instance;
            if (lm != null && lm.CurrentLanguage != "en") { NotEnglish++; Stop("not English"); return; }
            UILabel label = s_pageText(panel);
            string shown = label != null && label.gameObject.activeSelf ? label.text : "";
            string template = null;
            Encounter enc = Encounter.Instance;
            if (vs != null && vs.HasKey && enc != null && enc.StringLibrary != null) template = enc.StringLibrary.Localise(vs.Key);
            if (NarrText.Norm(shown).Length == 0) { Stop("a page without text"); m_lastPage = null; return; }
            string hash = NarrText.Hash(shown);
            if (page != null && page == m_lastPage && hash == m_lastHash && m_lastOpen)
            {
                SameSkips++;
                Plugin.L.LogInfo("narration: the same page again (" + hash + ") - not read twice");
                return;
            }
            m_lastPage = page;
            m_lastHash = hash;
            m_lastOpen = true;
            Stop("a new page");
            string how = "text", path = ClipStore.Find(hash);
            if (path == null && !string.IsNullOrEmpty(template))
            {
                string th = NarrText.Hash(template);
                if (th != hash && (path = ClipStore.Find(th)) != null) { hash = th; how = "template"; }
            }
            if (path == null) { Miss(hash, shown, vs != null ? vs.Key : ""); return; }
            Begin(path, hash, shown, how, enc != null ? enc.Id : "-");
        }

        void Begin(string path, string hash, string text, string how, string enc)
        {
            Release(ref m_cur);
            m_cur = ClipStore.Get(path, hash);
            m_cur.Pins++;
            m_hash = hash;
            m_text = text;
            m_how = how;
            m_enc = enc;
            m_st = St.Waiting;
            m_waitCounted = false;
            m_since = Time.unscaledTime;
            Plugin.L.LogInfo("narration: page " + hash + " (" + enc + ", by its " + how + ") -> " + System.IO.Path.GetFileName(path) + " waits" +
                             (m_voiceBusy ? " for the Dealer's line" : "") + ": '" + Short(text, 70) + "'");
        }

        void Play()
        {
            m_src.clip = m_cur.Clip;
            m_src.volume = NarrationSettings.Volume;
            m_src.time = 0f;
            m_src.Play();
            m_st = St.Playing;
            m_playStart = Time.unscaledTime;
            m_gest = 0;
            m_gestured = false;
            Plays++;
            LastPlay = m_hash;
            LastWaitedS = Time.unscaledTime - m_since;
            Plugin.L.LogInfo("narration: plays " + m_hash + " (" + m_cur.Clip.length.ToString("0.0") + " s, " + NarrationSettings.Pace + ") after " + LastWaitedS.ToString("0.00") +
                             " s, at " + Diag.V(m_src.transform.position) + " blend " + m_src.spatialBlend.ToString("0.0") + " group " + (m_src.outputAudioMixerGroup != null ? m_src.outputAudioMixerGroup.name : "-"));
        }

        /// A choice or the encounter's end: stop, and the page may be read again when it comes back.
        void Advance(string why)
        {
            m_lastOpen = false;
            m_lastPage = null;
            if (m_st != St.Idle) Stop(why);
        }

        public void Stop(string why, float fade = 0.12f)
        {
            if (m_st == St.Playing || m_st == St.Paused)
            {
                Release(ref m_fadeEntry);
                if (m_st == St.Paused)
                {
                    // silent already (the Dealer speaks): no fade over him
                    m_src.Stop();
                    m_fadeStart = -1f;
                    LastStopMs = 0f;
                }
                else
                {
                    m_fadeStart = Time.unscaledTime;
                    m_fadeLen = Mathf.Max(0.01f, fade);
                    m_fadeVol = m_src.volume;
                    m_fadeEntry = m_cur;
                    m_cur = null;
                    LastStopMs = -1f;
                }
                Stops++;
                LastStop = why;
                GestureEnd();
                Plugin.L.LogInfo("narration: " + m_hash + " stopped at " + m_src.time.ToString("0.00") + " s: " + why);
            }
            else if (m_st == St.Waiting) Plugin.L.LogInfo("narration: " + m_hash + " dropped before it played: " + why);
            Release(ref m_cur);
            m_st = St.Idle;
        }

        void Fade()
        {
            if (m_fadeStart < 0f) return;
            float u = (Time.unscaledTime - m_fadeStart) / m_fadeLen;
            if (u < 1f) { m_src.volume = m_fadeVol * (1f - u); return; }
            m_src.Stop();
            m_src.volume = NarrationSettings.Volume;
            LastStopMs = (Time.unscaledTime - m_fadeStart) * 1000f;
            m_fadeStart = -1f;
            Release(ref m_fadeEntry);
        }

        static void Release(ref ClipStore.Entry e)
        {
            if (e != null) e.Pins--;
            e = null;
        }

        bool PanelShowing()
        {
            if (m_panel == null) return true;   // a test `narr say`
            UILabel l = s_pageText(m_panel);
            return l != null && l.gameObject.activeInHierarchy && !string.IsNullOrEmpty(l.text);
        }

        void Miss(string hash, string text, string key)
        {
            Misses++;
            LastMiss = hash;
            if (!s_missLogged.Add(hash)) return;
            s_misses.Add(hash + "\t" + (text ?? "").Replace("\n", "\\n"));
            Plugin.L.LogInfo("narration: no clip for " + hash + " (key " + key + ") '" + Short(text, 80) + "' - silent");
        }

        // ---- his gestures and the measures ---------------------------------------------------------------------------------
        void Gestures()
        {
            ClipStore.Motion m = m_cur != null ? m_cur.Motion : null;
            if (m == null) return;
            float t = m_src.time;
            while (m_gest < m.GestT.Length && m.GestT[m_gest] <= t)
            {
                Dealer d = Dealer.Instance;
                if (d != null && d.IsInSubTag("Idle")) { d.PlayAnimation(m.GestN[m_gest]); m_gestured = true; }
                m_gest++;
            }
        }

        void GestureEnd()
        {
            if (!m_gestured) return;
            m_gestured = false;
            Dealer d = Dealer.Instance;
            if (d != null && d.IsInSubTag("Idle")) d.PlayAnimation("Idle_Random");
        }

        void Measure()
        {
            m_src.GetOutputData(m_buf, 0);
            float s = 0f;
            for (int i = 0; i < m_buf.Length; i++) s += m_buf[i] * m_buf[i];
            Rms = Mathf.Sqrt(s / m_buf.Length);
            if (Rms > RmsMax) RmsMax = Rms;
            if (Flap > FlapMax) FlapMax = Flap;
        }
        public static float RmsMax, FlapMax, VoiceRmsMax;
        public static int FlapFrames;

        /// The game's own voice output while it plays (in silent test runs FMOD gives no output data at all: 0 here too).
        void MeasureVoice()
        {
            AudioSource g = Voice.Instance != null ? Voice.Instance.Source : null;
            if (g == null || !g.isPlaying) return;
            g.GetOutputData(m_buf, 0);
            float s = 0f;
            for (int i = 0; i < m_buf.Length; i++) s += m_buf[i] * m_buf[i];
            VoiceRmsMax = Mathf.Max(VoiceRmsMax, Mathf.Sqrt(s / m_buf.Length));
        }

        // ---- status and test seams ---------------------------------------------------------------------------------------
        static string Short(string s, int n)
        {
            s = (s ?? "").Replace("\n", " ");
            return s.Length > n ? s.Substring(0, n) + "..." : s;
        }

        string Describe()
        {
            var inv = CultureInfo.InvariantCulture;
            string where = "-";
            Transform head = VRAudio.DealerHeadBone;
            if (head != null) where = (m_src.transform.position - head.position).magnitude.ToString("0.000", inv);
            return string.Format(inv, "{0} {1} t {2:0.00}/{3:0.00} by {4} | pages {5} plays {6} misses {7} same {8} waited {9} pauses {10} resumes {11} stops {12} ends {13} | last stop '{14}' {15:0} ms | rms {16:0.000} max {17:0.000} flap {18:0.00} max {19:0.00} frames {28} voice rms max {29:0.000} | src {20} from head {21} blend {22:0.0} group {23} vol {24:0.00} | {25} | {26} | {27}",
                m_st, m_hash, m_src.time, m_src.clip != null ? m_src.clip.length : 0f, m_how, Pages, Plays, Misses, SameSkips, Waited, Pauses, Resumes, Stops, Ends,
                LastStop, LastStopMs, Rms, RmsMax, Flap, FlapMax, Diag.V(m_src.transform.position), where, m_src.spatialBlend,
                m_src.outputAudioMixerGroup != null ? m_src.outputAudioMixerGroup.name : "-", m_src.volume, NarrationSettings.Describe(), ClipStore.Describe(), DealerLines.Describe(),
                FlapFrames, VoiceRmsMax);
        }

        /// narr                      the state (also in `status`)
        /// narr set <Key> <value>    a [Narration] setting for this run only (Enabled, Pace, Volume, OriginalDealerLines, HeadMotion, ClipDir)
        /// narr test <dir>           ClipDir = dir, Enabled, Normal, Replace, HeadMotion 1 - for this run
        /// narr selftest <tsv>       the hash parity check (work/narration/hash_vectors.tsv)
        /// narr hash <text>          the clip name of a text
        /// narr say <hash>           play a clip by its hash, as if its page appeared
        /// narr stop                 stop
        /// narr repage [again]       the current page's display again (the "not twice" rule); again = left and opened again
        /// narr misses               the missed hashes so far -> log
        /// narr reset                zero the counters
        /// narr subs 0|1             test runs: the game's subtitles off/on for this run
        /// narr vo [play <name>]     the Dealer's lines (DealerLines); play one through the game's own voice
        /// narr motiontest <motion> <curves.tsv>   the head-motion curves against the reference (work/narration/motion9.py)
        string Command(string[] a)
        {
            string op = a.Length > 1 ? a[1] : "";
            switch (op)
            {
                case "set": return a.Length > 3 ? NarrationSettings.SetForRun(a[2], string.Join(" ", a, 3, a.Length - 3)) : "narr set <Key> <value>";
                case "test":
                    if (a.Length > 2) NarrationSettings.SetForRun("ClipDir", string.Join(" ", a, 2, a.Length - 2));
                    NarrationSettings.SetForRun("Enabled", "true");
                    NarrationSettings.SetForRun("Pace", "Normal");
                    NarrationSettings.SetForRun("OriginalDealerLines", "Replace");
                    NarrationSettings.SetForRun("HeadMotion", "1");
                    return NarrationSettings.Describe() + " | pace folder " + (ClipStore.PaceDir() ?? "none");
                case "selftest": return NarrText.SelfTest(a.Length > 2 ? string.Join(" ", a, 2, a.Length - 2) : "");
                case "hash": { string t = string.Join(" ", a, 2, Math.Max(0, a.Length - 2)); return NarrText.Hash(t) + " <- '" + NarrText.Norm(t) + "'"; }
                case "say":
                    {
                        if (a.Length < 3) return "narr say <hash>";
                        string p = ClipStore.Find(a[2]);
                        if (p == null) return "no clip for " + a[2] + " in " + (ClipStore.PaceDir() ?? "-");
                        Stop("narr say");
                        m_panel = null;
                        Begin(p, a[2], "(test)", "test", "-");
                        return "waiting " + a[2];
                    }
                case "stop": Stop("narr stop"); return "stopped";
                case "repage":
                    {
                        // `narr repage`: the panel shows the current page once more; `narr repage again`: it was left and
                        // opened again (a choice in between)
                        if (m_panel == null || m_lastPage == null) return "no page";
                        Page page = m_lastPage;
                        if (a.Length > 2 && a[2] == "again") Advance("narr repage again");
                        int before = SameSkips;
                        OnPage(m_panel, page, null);
                        return SameSkips > before ? "the same page: not read twice" : "read again (" + m_st + ")";
                    }
                case "misses":
                    Plugin.L.LogInfo("narration misses (" + s_misses.Count + "):\n  " + string.Join("\n  ", s_misses.ToArray()));
                    return s_misses.Count + " missed hashes (see log)";
                case "subs":
                    {
                        // test runs only: the game's subtitles on/off for this run (UIManager.ShowSubs is not saved here)
                        if (!TestMode.Enabled) return "test runs only";
                        UIManager ui = UIManager.Instance;
                        if (ui == null) return "no UIManager";
                        if (a.Length > 2) ui.ShowSubs = a[2] == "1";
                        return "subtitles " + (ui.ShowSubs ? "on" : "off");
                    }
                case "reset":
                    Pages = Plays = Misses = SameSkips = Pauses = Resumes = Stops = Ends = NotEnglish = Waited = FlapFrames = 0;
                    RmsMax = FlapMax = VoiceRmsMax = 0f;
                    DealerMotion.ResetMax();
                    return "reset";
                case "vo": return DealerLines.Command(a);
                case "motiontest": return a.Length > 3 ? DealerMotion.CurvesTest(a[2], a[3]) : "narr motiontest <file.motion> <curves.tsv>";
                default: return Describe();
            }
        }
    }
}

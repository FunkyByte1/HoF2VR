using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// The Dealer's own recorded lines while the narration is on (docs/narration-design.md §7; [Narration] OriginalDealerLines).
    /// The audio is swapped inside the game's own line, when the game loads it (a postfix on
    /// AssetBundleLoadAssetOperationFull.IsDone for a VoiceClipTriggers of the English "voice" bundle): the game still decides
    /// when he speaks, shows the same subtitle, waits the clip's length, moves his mouth (VoiceFlap reads the same source)
    /// and plays his gestures (the line's triggers, re-timed to the new clip).
    ///  * Replace: our re-voiced clip (vo/<line name>.ogg, or the transcript's hash) when there is one. While it is still
    ///    loading the load is held for at most 0.5 s (the game's coroutine just waits); after that, this time, muted.
    ///  * Mute (and Replace without a clip): a silent clip as long as the original; subtitle and gestures stay. (Replace with
    ///    no clip folder installed at all leaves his lines as they are.)
    ///  * Keep, or the narration off: the originals (put back at once).
    ///  * Only the Dealer's lines (dealer_lines.txt, 1,659 names from the VO inventory); companions and Kallas stay original.
    ///  * The loaded ScriptableObject is changed in memory only; the bundle and the save are never touched.
    ///  * Subtitles with [sX eY] timings follow the new clip's length (a prefix on Subtitle.ProcessSubtitlesPreSplit).
    internal static class DealerLines
    {
        sealed class Orig
        {
            public AudioClip Audio;
            public VoiceClipTriggers.Trigger[] Triggers;
            public string Key = "";          // what is in the asset now: "mute" / the clip's path
            public float HoldSince = -1f;
        }

        static readonly AccessTools.FieldRef<AssetBundleLoadAssetOperationFull, Type> s_type = AccessTools.FieldRefAccess<AssetBundleLoadAssetOperationFull, Type>("m_Type");
        static readonly AccessTools.FieldRef<AssetBundleLoadAssetOperationFull, AssetBundleRequest> s_req = AccessTools.FieldRefAccess<AssetBundleLoadAssetOperationFull, AssetBundleRequest>("m_Request");
        static readonly AccessTools.FieldRef<AssetBundleLoadAssetOperationFull, string> s_bundle = AccessTools.FieldRefAccess<AssetBundleLoadAssetOperationFull, string>("m_AssetBundleName");
        static readonly AccessTools.FieldRef<VoiceClip, int> s_index = AccessTools.FieldRefAccess<VoiceClip, int>("m_index");

        static HashSet<string> s_dealer;
        static readonly Dictionary<VoiceClipTriggers, Orig> s_lines = new Dictionary<VoiceClipTriggers, Orig>();
        /// our clips in the game's lines -> the original's length (subtitle timing) and the clip's entry (motion)
        static readonly Dictionary<AudioClip, float> s_ourLen = new Dictionary<AudioClip, float>();
        static readonly Dictionary<AudioClip, ClipStore.Entry> s_ourEntry = new Dictionary<AudioClip, ClipStore.Entry>();
        static readonly Dictionary<int, AudioClip> s_silent = new Dictionary<int, AudioClip>();
        static readonly HashSet<AudioClip> s_silentSet = new HashSet<AudioClip>();
        static readonly HashSet<string> s_logged = new HashSet<string>();
        public static int Swaps, Mutes, Holds, Timeouts, Restores, Others, SubtitleScales;
        public static string Last = "-";

        /// The game's voice plays one of his changed lines now.
        public static bool Busy
        {
            get
            {
                AudioSource s = Voice.Instance != null ? Voice.Instance.Source : null;
                return s != null && s.isPlaying && s.clip != null && (s_ourLen.ContainsKey(s.clip) || s_silentSet.Contains(s.clip));
            }
        }

        static bool Wanted => NarrationSettings.Enabled && NarrationSettings.OriginalDealerLines != NarrationSettings.LinesMode.Keep;

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(AssetBundleLoadAssetOperationFull), "IsDone"), postfix: new HarmonyMethod(typeof(DealerLines), nameof(IsDonePostfix)));
                h.Patch(AccessTools.Method(typeof(Subtitle), "ProcessSubtitlesPreSplit"), prefix: new HarmonyMethod(typeof(DealerLines), nameof(SubtitlePrefix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("DealerLines: not patched: " + e.Message); }
        }

        static HashSet<string> Dealer
        {
            get
            {
                if (s_dealer != null) return s_dealer;
                s_dealer = new HashSet<string>();
                try
                {
                    using (Stream s = typeof(DealerLines).Assembly.GetManifestResourceStream("HoF2VR.dealer_lines.txt"))
                    using (var r = new StreamReader(s))
                    {
                        string l;
                        while ((l = r.ReadLine()) != null)
                            if (l.Length > 0 && l[0] != '#') s_dealer.Add(l.Trim());
                    }
                }
                catch (Exception e) { Plugin.L.LogError("DealerLines: the list of his lines did not load: " + e.Message); }
                Plugin.L.LogInfo("DealerLines: " + s_dealer.Count + " lines are the Dealer's");
                return s_dealer;
            }
        }

        // ---- the swap ----------------------------------------------------------------------------------------------------
        static void IsDonePostfix(AssetBundleLoadAssetOperationFull __instance, ref bool __result)
        {
            if (!__result || (!Wanted && s_lines.Count == 0)) return;
            try
            {
                if (s_type(__instance) != typeof(VoiceClipTriggers)) return;
                AssetBundleRequest r = s_req(__instance);
                VoiceClipTriggers vct = r != null ? r.asset as VoiceClipTriggers : null;
                if (vct == null) return;
                if (!Prepare(vct, s_bundle(__instance))) __result = false;
            }
            catch (Exception e)
            {
                if (s_logged.Add("err " + e.GetType().Name)) Plugin.L.LogError("DealerLines: " + e);
            }
        }

        /// Puts the right audio into the line; false = hold the load a little longer (our clip is still loading).
        static bool Prepare(VoiceClipTriggers vct, string bundle)
        {
            Orig o;
            s_lines.TryGetValue(vct, out o);
            if (!Wanted)
            {
                if (o != null) Restore(vct, o, "narration off / Keep");
                return true;
            }
            if (bundle != "voice") return true;                    // English only (the Chinese VO bundle is voice_zh)
            if (!Dealer.Contains(vct.name))
            {
                if (s_logged.Add("other " + vct.name)) { Others++; Plugin.L.LogInfo("DealerLines: '" + vct.name + "' is not the Dealer's - stays original"); }
                return true;
            }
            if (NarrationSettings.OriginalDealerLines == NarrationSettings.LinesMode.Replace && ClipStore.PaceDir() == null)
            {
                // no voice clips installed at all: nothing to replace his lines with - they stay (only Mute silences them)
                if (o != null) Restore(vct, o, "no clip folder");
                return true;
            }
            if (o == null)
            {
                o = new Orig { Audio = vct.Audio, Triggers = vct.Triggers };
                s_lines[vct] = o;
            }
            string path = null;
            if (NarrationSettings.OriginalDealerLines == NarrationSettings.LinesMode.Replace)
                path = ClipStore.FindLine(vct.name, Transcript(vct) is string t && t.Length > 0 ? NarrText.Hash(t) : null);
            if (path != null)
            {
                if (o.Key == path) return true;
                ClipStore.Entry e = ClipStore.Get(path, "vo:" + vct.name);
                if (e.Ready)
                {
                    Swap(vct, o, e);
                    return true;
                }
                if (e.Loading)
                {
                    if (o.HoldSince < 0f) { o.HoldSince = Time.unscaledTime; Holds++; }
                    if (Time.unscaledTime - o.HoldSince < 0.5f) return false;
                    Timeouts++;
                    Plugin.L.LogInfo("DealerLines: " + vct.name + "'s clip is still loading after 0.5 s - muted this time");
                }
            }
            if (o.Key != "mute") Mute(vct, o, path != null ? "its clip did not load in time" : NarrationSettings.OriginalDealerLines == NarrationSettings.LinesMode.Mute ? "Mute" : "no re-voiced clip");
            return true;
        }

        static string Transcript(VoiceClipTriggers vct)
        {
            try
            {
                if (vct.Subtitle == null || !vct.Subtitle.HasKey || vct.StringLibrary == null) return null;
                return vct.StringLibrary.Localise(vct.Subtitle.Key, "en");
            }
            catch (Exception) { return null; }
        }

        static void Swap(VoiceClipTriggers vct, Orig o, ClipStore.Entry e)
        {
            Unpin(vct);
            float origLen = o.Audio != null ? o.Audio.length : e.Clip.length;
            vct.Audio = e.Clip;
            vct.Triggers = Retime(o.Triggers, origLen, e.Clip.length, e.Motion);
            o.Key = e.Path;
            o.HoldSince = -1f;
            e.Pins++;
            s_ourLen[e.Clip] = origLen;
            s_ourEntry[e.Clip] = e;
            Swaps++;
            Last = vct.name + " re-voiced (" + origLen.ToString("0.00") + " -> " + e.Clip.length.ToString("0.00") + " s, gestures " + (o.Triggers != null ? o.Triggers.Length : 0) + " -> " + vct.Triggers.Length +
                   (e.Motion != null && e.Motion.GestT.Length > 0 ? " from the clip's data" : " re-timed") + ")";
            Plugin.L.LogInfo("DealerLines: " + Last);
        }

        static void Mute(VoiceClipTriggers vct, Orig o, string why)
        {
            Unpin(vct);
            float len = o.Audio != null ? o.Audio.length : 1f;
            vct.Audio = Silent(len);
            vct.Triggers = o.Triggers;
            o.Key = "mute";
            o.HoldSince = -1f;
            Mutes++;
            Last = vct.name + " muted (" + why + ", " + len.ToString("0.00") + " s; subtitle and gestures stay)";
            Plugin.L.LogInfo("DealerLines: " + Last);
        }

        static void Restore(VoiceClipTriggers vct, Orig o, string why)
        {
            Unpin(vct);
            vct.Audio = o.Audio;
            vct.Triggers = o.Triggers;
            s_lines.Remove(vct);
            Restores++;
            Last = vct.name + " original again (" + why + ")";
        }

        static void Unpin(VoiceClipTriggers vct)
        {
            AudioClip c = vct.Audio;
            ClipStore.Entry e;
            if (c != null && s_ourEntry.TryGetValue(c, out e)) e.Pins--;
        }

        /// Each frame: the originals go back as soon as the narration is off (or Keep).
        public static void Tick()
        {
            if (Wanted || s_lines.Count == 0) return;
            int n = s_lines.Count;
            foreach (KeyValuePair<VoiceClipTriggers, Orig> kv in s_lines.ToList())
                if (kv.Key != null) Restore(kv.Key, kv.Value, "narration off / Keep");
            s_lines.Clear();
            Plugin.L.LogInfo("DealerLines: " + n + " of his lines are original again");
        }

        /// The gestures on the new clip: the clip's own track when it has one (the voice side re-times the original's), else
        /// the original's moved in proportion; always inside the clip (the game's trigger loop waits for each one).
        static VoiceClipTriggers.Trigger[] Retime(VoiceClipTriggers.Trigger[] orig, float origLen, float newLen, ClipStore.Motion m)
        {
            float end = Mathf.Max(0f, newLen - 0.05f);
            var list = new List<VoiceClipTriggers.Trigger>();
            if (m != null && m.GestT.Length > 0)
            {
                for (int i = 0; i < m.GestT.Length; i++) list.Add(new VoiceClipTriggers.Trigger { Time = Mathf.Min(m.GestT[i], end), Name = m.GestN[i] });
                return list.ToArray();
            }
            if (orig == null) return new VoiceClipTriggers.Trigger[0];
            float k = origLen > 0.01f ? newLen / origLen : 1f;
            foreach (VoiceClipTriggers.Trigger t in orig)
                if (t != null) list.Add(new VoiceClipTriggers.Trigger { Time = Mathf.Min(t.Time * k, end), Name = t.Name });
            return list.ToArray();
        }

        static AudioClip Silent(float len)
        {
            int ms = Mathf.Max(100, Mathf.RoundToInt(len * 1000f));
            AudioClip c;
            if (s_silent.TryGetValue(ms, out c) && c != null) return c;
            const int hz = 8000;
            int n = Mathf.Max(1, ms * hz / 1000);
            c = AudioClip.Create("hof2vr_silent_" + ms, n, 1, hz, false);
            c.SetData(new float[n], 0);
            s_silent[ms] = c;
            s_silentSet.Add(c);
            return c;
        }

        public static bool IsSilent(AudioClip c) => c != null && s_silentSet.Contains(c);

        /// [sX eY] subtitle timings stretched to our clip's length.
        static void SubtitlePrefix(float[] a_startTimes, float[] a_endTimes, AudioSource a_audioSource)
        {
            try
            {
                AudioClip c = a_audioSource != null ? a_audioSource.clip : null;
                float orig;
                if (c == null || !s_ourLen.TryGetValue(c, out orig) || orig <= 0.01f) return;
                float k = c.length / orig;
                for (int i = 0; i < a_startTimes.Length; i++) a_startTimes[i] *= k;
                for (int i = 0; i < a_endTimes.Length; i++) a_endTimes[i] *= k;
                SubtitleScales++;
            }
            catch (Exception) { }
        }

        /// A re-voiced line of his playing on the game's voice now: its motion data and clock (DealerMotion).
        public static bool MotionNow(out ClipStore.Motion m, out float t)
        {
            m = null;
            t = 0f;
            Voice v = Voice.Instance;
            AudioSource s = v != null ? v.Source : null;
            ClipStore.Entry e;
            if (s == null || !s.isPlaying || s.clip == null || !s_ourEntry.TryGetValue(s.clip, out e) || e.Motion == null) return false;
            m = e.Motion;
            t = s.time;
            return true;
        }

        public static string Describe()
        {
            AudioSource s = Voice.Instance != null ? Voice.Instance.Source : null;
            string now = s == null ? "-" : (s.isPlaying ? "playing " : "idle ") + (s.clip != null ? s.clip.name + " " + s.time.ToString("0.00") + "/" + s.clip.length.ToString("0.00") : "-");
            return "lines: swaps " + Swaps + " mutes " + Mutes + " holds " + Holds + " timeouts " + Timeouts + " restores " + Restores + " others " + Others + " subtitle scales " + SubtitleScales +
                   " in memory " + s_lines.Count + " | voice " + now + " | last " + Last;
        }

        /// narr vo                the state
        /// narr vo play <name>    the game plays one of its lines (found in the loaded VoiceClips) through its own voice
        public static string Command(string[] a)
        {
            if (a.Length > 3 && a[2] == "play") return PlayLine(a[3]);
            if (a.Length > 3 && a[2] == "info") return Info(a[3]);
            return Describe();
        }

        static string PlayLine(string name)
        {
            if (Voice.Instance == null) return "no Voice";
            string tail = "/" + name + ".asset";
            foreach (VoiceClip vc in Resources.FindObjectsOfTypeAll<VoiceClip>())
            {
                if (vc == null || !vc.gameObject.scene.IsValid()) continue;
                VoiceClip.Clip[] clips = vc.Clips;
                if (clips == null) continue;
                for (int i = 0; i < clips.Length; i++)
                {
                    if (clips[i] == null || clips[i].Data == null || clips[i].Data.IndexOf(tail, StringComparison.Ordinal) < 0) continue;
                    s_index(vc) = i + 1;      // VoiceClip.Play plays m_clips[m_index - 1]
                    Voice.Instance.Play(vc);
                    return "playing " + name + " (" + VRUI.Path(vc.transform) + " #" + i + ")";
                }
            }
            return "no loaded VoiceClip has " + name;
        }

        static string Info(string name)
        {
            foreach (KeyValuePair<VoiceClipTriggers, Orig> kv in s_lines)
                if (kv.Key != null && kv.Key.name == name)
                    return name + ": audio " + (kv.Key.Audio != null ? kv.Key.Audio.name + " " + kv.Key.Audio.length.ToString("0.00") : "-") + " (original " + (kv.Value.Audio != null ? kv.Value.Audio.length.ToString("0.00") : "-") +
                           ") subtitle " + (kv.Key.Subtitle != null ? kv.Key.Subtitle.Key : "-") + " gestures " + (kv.Key.Triggers != null ? kv.Key.Triggers.Length : 0) + "/" + (kv.Value.Triggers != null ? kv.Value.Triggers.Length : 0) +
                           " [" + string.Join(" ", (kv.Key.Triggers ?? new VoiceClipTriggers.Trigger[0]).Select(t => t.Time.ToString("0.00") + ":" + t.Name).ToArray()) + "]";
            return name + ": not changed (original)";
        }
    }
}

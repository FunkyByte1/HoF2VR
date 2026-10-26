using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HoF2VR
{
    /// The voice clips on disk and their per-clip motion data (docs/narration-design.md §4.4, §8.4).
    ///  * Layout: <ClipDir>/<Pace>/<hash>.ogg (or <hh>/<hash>.ogg, hh = the hash's first two digits; .wav also works).
    ///    The Dealer's re-voiced lines: <ClipDir>/<Pace>/vo/<line name>.ogg, or by their transcript's hash like a page.
    ///    A pace folder that does not exist falls back to Normal.
    ///  * Motion data (optional): <clip path without the extension>.motion, lines "key TAB value" with the keys of
    ///    work/narration/motion9.py to_tsv() (beats, still, phrases, gestures, env25) plus dur.
    ///  * Loading: WWW from file, kept compressed in memory (Vorbis is decoded while it plays); at most ~40 clips cached,
    ///    the least recently used one that nothing plays or holds is unloaded.
    internal static class ClipStore
    {
        public sealed class Motion
        {
            public float Dur;
            public float[] BeatT = new float[0], BeatS = new float[0];
            public float[] StillA = new float[0], StillB = new float[0];
            public float[] PhrA = new float[0], PhrB = new float[0];
            public int[] PhrSide = new int[0];
            public float[] GestT = new float[0];
            public string[] GestN = new string[0];
            public int Env25;
            public string Describe() => "dur " + Dur.ToString("0.00", CultureInfo.InvariantCulture) + " beats " + BeatT.Length + " still " + StillA.Length + " phrases " + PhrA.Length + " gestures " + GestT.Length + " env " + Env25;
        }

        public sealed class Entry
        {
            public string Path, Key;
            public AudioClip Clip;
            public Motion Motion;
            public bool Failed;
            public string Error = "";
            public float Asked, Used;
            public int Pins;                  // swapped into a game line (DealerLines) or playing: never unloaded
            internal WWW Www;
            public bool Ready => Clip != null && Clip.loadState == AudioDataLoadState.Loaded;
            public bool Loading => !Failed && !Ready;
        }

        static readonly Dictionary<string, Entry> s_byPath = new Dictionary<string, Entry>();
        static readonly List<Entry> s_loading = new List<Entry>();
        const int MaxCached = 40;
        public static int Loads, LoadFails, Unloads;
        public static float LastLoadMs;
        static readonly HashSet<string> s_warned = new HashSet<string>();

        /// The pace folder in use (the chosen pace's, or Normal's when it has none).
        public static string PaceDir()
        {
            string root = NarrationSettings.Root;
            string want = Path.Combine(root, NarrationSettings.Pace.ToString());
            if (Directory.Exists(want)) return want;
            string normal = Path.Combine(root, "Normal");
            if (Directory.Exists(normal))
            {
                if (s_warned.Add("pace " + want)) Plugin.L.LogWarning("narration: no clip folder for the pace " + NarrationSettings.Pace + " (" + want + ") - Normal's clips play");
                return normal;
            }
            if (s_warned.Add("root " + want)) Plugin.L.LogWarning("narration: no clip folder " + want + " (nor Normal) - nothing to play");
            return null;
        }

        static readonly string[] s_exts = { ".ogg", ".wav" };

        /// The clip of a text hash in the current pace's folder, or null.
        public static string Find(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return null;
            string dir = PaceDir();
            if (dir == null) return null;
            foreach (string ext in s_exts)
            {
                string p = Path.Combine(Path.Combine(dir, hash.Substring(0, 2)), hash + ext);
                if (File.Exists(p)) return p;
                p = Path.Combine(dir, hash + ext);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        /// A re-voiced line of the Dealer: by its name (vo/<name>.ogg), else by its transcript's hash.
        public static string FindLine(string name, string transcriptHash)
        {
            string dir = PaceDir();
            if (dir == null) return null;
            if (!string.IsNullOrEmpty(name))
                foreach (string ext in s_exts)
                {
                    string p = Path.Combine(Path.Combine(dir, "vo"), name + ext);
                    if (File.Exists(p)) return p;
                }
            return Find(transcriptHash);
        }

        /// The entry of a clip file; starts loading it when it is new.
        public static Entry Get(string path, string key)
        {
            Entry e;
            if (s_byPath.TryGetValue(path, out e))
            {
                e.Used = Time.unscaledTime;
                return e;
            }
            e = new Entry { Path = path, Key = key, Asked = Time.unscaledTime, Used = Time.unscaledTime };
            s_byPath[path] = e;
            try
            {
                e.Motion = ReadMotion(Path.ChangeExtension(path, ".motion"));
                e.Www = new WWW(new Uri(path).AbsoluteUri);
                s_loading.Add(e);
            }
            catch (Exception x)
            {
                e.Failed = true;
                e.Error = x.Message;
                LoadFails++;
                Plugin.L.LogWarning("narration: cannot load " + path + ": " + x.Message);
            }
            Trim();
            return e;
        }

        /// Each frame: finished downloads become clips; clips that finished loading are ready.
        public static void Tick()
        {
            for (int i = s_loading.Count - 1; i >= 0; i--)
            {
                Entry e = s_loading[i];
                try
                {
                    if (e.Clip == null)
                    {
                        if (!e.Www.isDone) continue;
                        if (!string.IsNullOrEmpty(e.Www.error)) throw new Exception(e.Www.error);
                        bool wav = e.Path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
                        e.Clip = wav ? e.Www.GetAudioClip(false, false, AudioType.WAV) : e.Www.GetAudioClipCompressed(false, AudioType.OGGVORBIS);
                        if (e.Clip == null) throw new Exception("no audio in the file");
                        e.Clip.name = "hof2vr:" + e.Key;
                    }
                    AudioDataLoadState st = e.Clip.loadState;
                    if (st == AudioDataLoadState.Loading || st == AudioDataLoadState.Unloaded) { if (st == AudioDataLoadState.Unloaded) e.Clip.LoadAudioData(); continue; }
                    if (st == AudioDataLoadState.Failed) throw new Exception("the clip failed to decode");
                    s_loading.RemoveAt(i);
                    e.Www.Dispose();
                    e.Www = null;
                    Loads++;
                    LastLoadMs = (Time.unscaledTime - e.Asked) * 1000f;
                    Plugin.L.LogInfo("narration: loaded " + e.Key + " (" + e.Clip.length.ToString("0.00") + " s, " + e.Clip.frequency + " Hz, " + LastLoadMs.ToString("0") + " ms" +
                                     (e.Motion != null ? "; motion " + e.Motion.Describe() : "; no motion data") + ")");
                }
                catch (Exception x)
                {
                    s_loading.RemoveAt(i);
                    e.Failed = true;
                    e.Error = x.Message;
                    LoadFails++;
                    if (e.Www != null) { e.Www.Dispose(); e.Www = null; }
                    Plugin.L.LogWarning("narration: " + e.Path + " did not load: " + x.Message);
                }
            }
        }

        static void Trim()
        {
            if (s_byPath.Count <= MaxCached) return;
            foreach (Entry old in s_byPath.Values.Where(x => x.Pins <= 0 && x.Www == null).OrderBy(x => x.Used).Take(s_byPath.Count - MaxCached).ToList())
            {
                if (Narrator.I != null && Narrator.I.Uses(old)) continue;
                s_byPath.Remove(old.Path);
                if (old.Clip != null) UnityEngine.Object.Destroy(old.Clip);
                Unloads++;
            }
        }

        public static string Describe() =>
            s_byPath.Count + " cached, " + s_loading.Count + " loading, loads " + Loads + " fails " + LoadFails + " unloads " + Unloads + " last " + LastLoadMs.ToString("0") + " ms";

        // ---- motion data (format v1, docs/narration-design.md §8.4) ------------------------------------------------------

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static Motion ReadMotion(string path)
        {
            if (!File.Exists(path)) return null;
            var m = new Motion();
            foreach (string raw in File.ReadAllLines(path))
            {
                int tab = raw.IndexOf('\t');
                if (tab <= 0) continue;
                string k = raw.Substring(0, tab).Trim(), v = raw.Substring(tab + 1).Trim();
                try
                {
                    switch (k)
                    {
                        case "dur": m.Dur = F(v); break;
                        case "beats": Pairs(v, ':', out m.BeatT, out m.BeatS); break;
                        case "still": Pairs(v, '-', out m.StillA, out m.StillB); break;
                        case "phrases":
                            {
                                var a = new List<float>(); var b = new List<float>(); var s = new List<int>();
                                foreach (string it in Items(v))
                                {
                                    int c = it.LastIndexOf(':'), d = it.IndexOf('-');
                                    if (c < 0 || d < 0 || d > c) continue;
                                    a.Add(F(it.Substring(0, d))); b.Add(F(it.Substring(d + 1, c - d - 1)));
                                    string side = it.Substring(c + 1).Trim();
                                    s.Add(side == "L" ? -1 : side == "R" ? 1 : 0);
                                }
                                m.PhrA = a.ToArray(); m.PhrB = b.ToArray(); m.PhrSide = s.ToArray();
                                break;
                            }
                        case "gestures":
                            {
                                var t = new List<float>(); var n = new List<string>();
                                foreach (string it in Items(v))
                                {
                                    int c = it.IndexOf(':');
                                    if (c <= 0) continue;
                                    t.Add(F(it.Substring(0, c))); n.Add(it.Substring(c + 1).Trim());
                                }
                                m.GestT = t.ToArray(); m.GestN = n.ToArray();
                                break;
                            }
                        case "env25": m.Env25 = v.Length > 0 ? Convert.FromBase64String(v).Length : 0; break;
                    }
                }
                catch (Exception x) { Plugin.L.LogWarning("narration: motion " + path + " '" + k + "': " + x.Message); }
            }
            return m;
        }

        static IEnumerable<string> Items(string v) => v.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0);
        static float F(string s) => float.Parse(s.Trim(), NumberStyles.Float, Inv);

        static void Pairs(string v, char sep, out float[] a, out float[] b)
        {
            var la = new List<float>(); var lb = new List<float>();
            foreach (string it in Items(v))
            {
                int c = it.IndexOf(sep, 1);
                if (c <= 0) continue;
                la.Add(F(it.Substring(0, c))); lb.Add(F(it.Substring(c + 1)));
            }
            a = la.ToArray(); b = lb.ToArray();
        }
    }
}

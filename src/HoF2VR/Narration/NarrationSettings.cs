using System;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace HoF2VR
{
    /// [Narration] in flat2vr.hof2vr.cfg (docs/narration-design.md §4.8, §7, §8; the user's choices in docs/decisions.md
    /// "Narration"). Read live like every other setting; the VR settings page's "Voice" tab writes them.
    internal static class NarrationSettings
    {
        public enum PaceMode { Slow, Normal, Fast }
        public enum LinesMode { Replace, Mute, Keep }

        static ConfigFile s_cfg;
        static ConfigEntry<bool> s_enabled;
        static ConfigEntry<PaceMode> s_pace;
        static ConfigEntry<float> s_volume, s_motion;
        static ConfigEntry<LinesMode> s_lines;
        static ConfigEntry<string> s_dir;

        public static void Init(ConfigFile cfg)
        {
            s_cfg = cfg;
            s_enabled = cfg.Bind("Narration", "Enabled", false, "The Dealer reads the story aloud: each encounter page's text is played from pre-made voice clips, from the Dealer at the table. Choosing an option stops the reading. false = the game as it is (text only).");
            s_pace = cfg.Bind("Narration", "Pace", PaceMode.Normal, "How fast the Dealer reads: Slow (about 136 words a minute), Normal (about 158), Fast (about 180). A change applies from the next page.");
            s_volume = cfg.Bind("Narration", "Volume", 1f, "Volume of the reading (0-1), on top of the game's voice volume.");
            s_lines = cfg.Bind("Narration", "OriginalDealerLines", LinesMode.Replace, "With Enabled on: the Dealer's own recorded remarks (they don't say the card text). Replace = he says them in the narration's voice (where a re-voiced clip exists; otherwise muted), with the same subtitles and gestures; Mute = silent (subtitles and gestures stay); Keep = the original recordings.");
            s_motion = cfg.Bind("Narration", "HeadMotion", 1f, "With Enabled on: the Dealer's head moves a little with the reading (small nods and tilts, from the clip's motion data). 0 = off, 1 = the subtle design, 1.5 = a bit more.");
            s_dir = cfg.Bind("Narration", "ClipDir", "", "Folder of the voice clips, with one subfolder per pace (Slow, Normal, Fast). Empty = the 'narration' folder next to the mod (BepInEx/plugins/HoF2VR/narration).");
        }

        public static bool Enabled => s_enabled != null && s_enabled.Value;
        public static PaceMode Pace => s_pace?.Value ?? PaceMode.Normal;
        public static float Volume => Mathf.Clamp01(s_volume?.Value ?? 1f);
        public static LinesMode OriginalDealerLines => s_lines?.Value ?? LinesMode.Replace;
        public static float HeadMotion => Mathf.Clamp(s_motion?.Value ?? 1f, 0f, 1.5f);

        /// The clip folder (ClipDir, or BepInEx/plugins/HoF2VR/narration).
        public static string Root
        {
            get
            {
                string d = s_dir != null ? (s_dir.Value ?? "").Trim() : "";
                if (d.Length > 0)
                {
                    if (d.StartsWith("~/")) d = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", d.Substring(2));
                    return d;
                }
                string here = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".";
                return Path.Combine(here, "narration");
            }
        }

        /// Sets a [Narration] key for this run only (the file is not written): test seams.
        public static string SetForRun(string key, string value)
        {
            if (s_cfg == null) return "no config";
            foreach (ConfigDefinition d in s_cfg.Keys)
            {
                if (d.Section != "Narration" || !d.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                bool save = s_cfg.SaveOnConfigSet;
                s_cfg.SaveOnConfigSet = false;
                try { s_cfg[d].SetSerializedValue(value); }
                finally { s_cfg.SaveOnConfigSet = save; }
                return d.Key + "=" + s_cfg[d].GetSerializedValue() + " (this run only)";
            }
            return "no [Narration] key " + key;
        }

        public static string Describe() =>
            "enabled " + Enabled + " pace " + Pace + " volume " + Volume.ToString("0.##") + " lines " + OriginalDealerLines + " motion " + HeadMotion.ToString("0.##") + " dir " + Root;
    }
}

using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// "Bug mark" (Dev.BugMark; a development shortcut, off for a shipping version): hold B + Y together for half a second
    /// and let go (DevChord; held 1.5 s the dev jump menu opens instead). The mod saves, into this session's folder
    /// (BepInEx/hof2vr_sessions/<start>/marks/<time>/; the launcher keeps the logs there too):
    ///   eyes_eyes.jpg (both eyes as the headset showed them; eyes_mirror.jpg the desktop view, since 2026-10-17 JPEG, cheaper), state.txt (game state, place, positions of head/hands/hero,
    ///   the settings), log_tail.txt (the log's last 400 lines), and voice.wav - 8 s from the headset's microphone
    ///   (WiVRn's PipeWire source, when WiVRn's microphone is on) so the player can say what is wrong.
    /// Two buzzes and a note confirm it; the log says where it went. `mark [note]` does the same from the test channel.
    internal class BugMark : MonoBehaviour
    {
        public static BugMark I;
        public static int Marks;
        public static string LastDir = "-";
        public static float VoiceSeconds = 8f;
        bool m_armed;

        void Awake()
        {
            I = this;
            Diag.Commands["mark"] = a => { StartCoroutine(Mark(a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : "test")); return "marking"; };
            Diag.StatusExtras["bugMark"] = () => Diag.Q(Marks + " marks, last " + LastDir);
        }

        /// B + Y held together (DevChord): a tick at 0.5 s; let go before 1.5 s = a mark (held longer, the jump menu opens
        /// instead, when it is on).
        void Update()
        {
            if (!Settings.BugMark || !XRSettings.enabled) return;
            DevChord.Tick();
            if (DevChord.ChordSince >= 0f)
            {
                if (!m_armed && DevChord.Held >= DevChord.MarkHold && !(Settings.JumpMenu && DevChord.Held >= DevChord.JumpHold))
                {
                    m_armed = true;
                    Haptics.Pulse(true, 0.35f, 0.02f);
                    Haptics.Pulse(false, 0.35f, 0.02f);
                }
                return;
            }
            if (DevChord.EndedNow && DevChord.EndedHeld >= DevChord.MarkHold && (!Settings.JumpMenu || DevChord.EndedHeld < DevChord.JumpHold))
                StartCoroutine(Mark("button"));
            m_armed = false;
        }

        /// Where marks go: the launcher's session folder, else the test folder, else BepInEx/hof2vr_marks.
        static string Root()
        {
            string s = Environment.GetEnvironmentVariable("HOF2VR_SESSION_DIR");
            if (!string.IsNullOrEmpty(s)) return Path.Combine(s, "marks");
            if (TestMode.Enabled) return Path.Combine(TestMode.Dir, "marks");
            return Path.Combine(BepInEx.Paths.BepInExRootPath, "hof2vr_marks");
        }

        IEnumerator Mark(string why)
        {
            Marks++;
            string dir = Path.Combine(Root(), DateTime.Now.ToString("HHmmss") + "_" + Marks);
            try { Directory.CreateDirectory(dir); }
            catch (Exception e) { Plugin.L.LogWarning("bug mark: " + e.Message); yield break; }
            LastDir = dir;
            Haptics.Pulse(true, 0.6f, 0.06f);
            Haptics.Pulse(false, 0.6f, 0.06f);
            // the voice memo starts at once on its own thread (PulseMic); the state is taken now, its voice line filled in
            // once the thread knows the source (the eyes are captured meanwhile)
            bool allowed = !TestMode.Enabled || why.StartsWith("voice");
            PulseMic job = allowed ? PulseMic.Record(Path.Combine(dir, "voice.wav"), VoiceSeconds) : null;
            string state = null;
            try { state = State(why, VoicePlaceholder); } catch (Exception e) { Plugin.L.LogWarning("bug mark state: " + e.Message); }
            if (Diag.I != null) yield return Diag.I.CaptureEyesQuick(dir, "eyes");
            float until = Time.unscaledTime + 3f;
            while (job != null && !job.Looked && Time.unscaledTime < until) yield return null;
            string voice = job == null ? "no voice (test run: `mark voice` records)"
                : job.Source != null ? "recording " + VoiceSeconds.ToString("0") + " s from " + job.Source
                : job.Outcome ?? "no voice (the sound server did not answer)";
            Plugin.L.LogInfo("BUG MARK #" + Marks + " (" + why + ") at " + DateTime.Now.ToString("HH:mm:ss.fff") + " -> " + dir + " | " + voice);
            if (state != null)
                try { File.WriteAllText(Path.Combine(dir, "state.txt"), state.Replace(VoicePlaceholder, voice)); } catch (Exception e) { Plugin.L.LogWarning("bug mark state: " + e.Message); }
            yield return new WaitForSecondsRealtime(0.3f);
            try { File.WriteAllText(Path.Combine(dir, "log_tail.txt"), LogTail(400)); } catch (Exception e) { Plugin.L.LogWarning("bug mark log: " + e.Message); }
            Haptics.Pulse(true, 0.6f, 0.06f);
            Haptics.Pulse(false, 0.6f, 0.06f);
            if (VRHelp.I != null)
                VRHelp.I.Show("Bug marked" + (voice.StartsWith("recording") ? " - say what's wrong (" + VoiceSeconds.ToString("0") + " s)" : ""), 3f);
            if (job != null && job.Source != null) StartCoroutine(CheckVoice(job, dir));
        }

        const string VoicePlaceholder = "\u0001voice\u0001";

        static string State(string why, string voice)
        {
            var sb = new StringBuilder();
            sb.Append("bug mark #").Append(Marks).Append(" (").Append(why).Append(") ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append('\n');
            sb.Append("game state ").Append(GameWatch.State).Append(" for ").Append((Time.unscaledTime - GameWatch.StateTime).ToString("0.0")).Append(" s; place ").Append(Places.Current).Append('\n');
            VRRig rig = VRRig.I;
            if (rig != null)
            {
                sb.Append("station ").Append(Diag.V(rig.StationFloor)).Append(" yaw ").Append(rig.StationYaw.ToString("0")).Append(" scale ").Append(rig.Scale.ToString("0.00"))
                  .Append(" eye height ").Append(rig.EyeHeight.ToString("0.00")).Append(" mode ").Append(rig.Mode).Append('\n');
                sb.Append("head ").Append(Diag.V(rig.HeadWorldPos)).Append(" rot ").Append(Diag.V(rig.HeadWorldRot.eulerAngles)).Append('\n');
                foreach (Hand h in new[] { VRInput.L, VRInput.R })
                    sb.Append(h.IsLeft ? "left" : "right").Append(" hand ").Append(h.Tracked ? Diag.V(rig.ToWorld(h.Pos)) : "not tracked")
                      .Append(" trigger ").Append(h.Trigger.ToString("0.00")).Append(" grip ").Append(h.Grip.ToString("0.00")).Append('\n');
            }
            sb.Append("first person ").Append(CombatVR.Active).Append("; voice ").Append(voice).Append('\n');
            sb.Append("menu ").Append(Diag.CurrentMenu()).Append('\n');
            sb.Append("controllers '").Append(ControllerGeometry.Type).Append("', glove angle ").Append(Settings.GloveAngle.ToString("0.0")).Append('\n');
            try
            {
                string cfg = Path.Combine(BepInEx.Paths.ConfigPath, Plugin.Guid + ".cfg");
                if (File.Exists(cfg)) sb.Append("\n---- settings (").Append(cfg).Append(")\n").Append(File.ReadAllText(cfg));
            }
            catch (Exception) { }
            return sb.ToString();
        }

        static string LogTail(int lines)
        {
            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - 200000);
                fs.Seek(start, SeekOrigin.Begin);
                var r = new StreamReader(fs);
                string[] all = r.ReadToEnd().Split('\n');
                int from = Math.Max(0, all.Length - lines);
                return string.Join("\n", all, from, all.Length - from);
            }
        }

        // ---- the voice memo: the headset's microphone (Diag/PulseMic.cs: libpulse in the game's own process) -------------------
        IEnumerator CheckVoice(PulseMic job, string dir)
        {
            float until = Time.unscaledTime + VoiceSeconds + 5f;
            while (!job.Done && Time.unscaledTime < until) yield return null;
            string o = job.Outcome ?? "still recording after " + (VoiceSeconds + 5f).ToString("0") + " s (the source sent nothing?)";
            Plugin.L.LogInfo("bug mark voice: " + o);
            try { File.WriteAllText(Path.Combine(dir, "voice.txt"), o + "\n"); } catch (Exception) { }
        }
    }
}

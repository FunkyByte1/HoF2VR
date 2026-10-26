using System.Collections.Generic;
using UnityEngine;
using Valve.VR;

namespace HoF2VR
{
    /// Controller vibration through OpenVR's legacy haptic pulse (OpenComposite maps it to OpenXR
    /// haptics). The game's rumble (camera shake, hits) arrives via VRGamepad.Vibrate.
    /// Interaction feedback (touch, dice, table gestures) uses Event(): named, scaled by the
    /// InteractionHaptics setting, queued as short patterns (double ticks) and counted for tests.
    internal static class Haptics
    {
        static float s_gameL, s_gameR;
        static float s_untilL, s_untilR, s_strengthL, s_strengthR;
        // continuous level set every frame by a holder (e.g. dice in the hand); decays if not refreshed
        static float s_contL, s_contR;
        static int s_contFrameL = -1, s_contFrameR = -1;

        struct Step { public bool Left; public float Start, End, Strength; }
        static readonly List<Step> s_queue = new List<Step>();

        // test/diagnostic record of interaction events
        public static int Count;
        public static string Last = "-";
        static readonly Dictionary<string, int> s_counts = new Dictionary<string, int>();

        /// `hapticlog on|off` (tests): every vibration request with its hand, to the log (the user's "pressing a button buzzes the
        /// wrong hand", 2026-10-17: which requests a press makes, on which hand).
        public static bool Trace;
        static void T(string what, bool left) { if (Trace) Plugin.L.LogInfo("haptics: " + what + (left ? " L" : " R") + " (frame " + Time.frameCount + ")"); }

        public static void FromGame(float left, float right)
        {
            if (Trace && (left > 0.02f || right > 0.02f) && (s_gameL <= 0.02f && s_gameR <= 0.02f)) Plugin.L.LogInfo("haptics: the game's rumble L " + left.ToString("0.00") + " R " + right.ToString("0.00"));
            s_gameL = Mathf.Clamp01(left);
            s_gameR = Mathf.Clamp01(right);
        }

        /// One-off buzz on a hand, e.g. a weapon hit.
        public static void Pulse(bool left, float strength, float seconds)
        {
            T("pulse " + seconds.ToString("0.000") + " s", left);
            float until = Time.unscaledTime + seconds;
            if (left) { s_untilL = until; s_strengthL = Mathf.Clamp01(strength); }
            else { s_untilR = until; s_strengthR = Mathf.Clamp01(strength); }
        }

        /// A named interaction event: one pulse, or `repeat` pulses `gap` seconds apart.
        public static void Event(string name, bool left, float strength, float seconds, int repeat = 1, float gap = 0.06f)
        {
            Count++;
            Last = name + (left ? " L" : " R");
            T("event " + name, left);
            int n;
            s_counts.TryGetValue(name, out n);
            s_counts[name] = n + 1;
            float k = Settings.HapticStrength;
            if (k <= 0f) return;
            float t = Time.unscaledTime;
            for (int i = 0; i < Mathf.Max(1, repeat); i++)
            {
                float s0 = t + i * (seconds + gap);
                s_queue.Add(new Step { Left = left, Start = s0, End = s0 + seconds, Strength = Mathf.Clamp01(strength * k) });
            }
        }

        // ---- the hand-feel cue vocabulary (docs/hand-feel-design.md §3.10) ------------------------------------------------
        // The path allows only durations: OpenComposite's legacy pulse is amplitude 1, the Quest 2's motor has one
        // frequency, WiVRn 26.9 cannot stop a pulse early, and a new pulse replaces the running one. So the cues differ by
        // length and rhythm, shorter ones are lengthened to the player's floor (Hands.HapticFloorMs), a weaker cue never cuts
        // a longer one that still runs, cues within 25 ms on one hand merge (the longer wins), and outside fights a hand gets
        // at most 12 calls a second.
        public enum CueKind { Tick, Touch, Click, Release, Refuse, Bump, Grab, LetGo }
        static readonly float[] s_cueMs = { 4f, 4f, 12f, 4f, 8f, 8f, 10f, 6f };
        struct CueStep { public bool Left; public float At, Ms; public string Name; public bool Exact; }
        static readonly List<CueStep> s_cues = new List<CueStep>();
        static readonly float[] s_motorUntil = { -1f, -1f };
        static readonly List<float>[] s_recent = { new List<float>(), new List<float>() };
        public static int CueCalls, CueDropped, CueMerged, CueBudget;
        public static float LastCueMs;
        public static float BudgetPerSecond = 12f, MergeWindow = 0.025f, MaxWait = 0.06f;

        /// A feel cue on one hand (`scale` lengthens or shortens it, e.g. a bump by the hit's speed).
        public static void Cue(CueKind kind, bool left, float scale = 1f)
        {
            string name = "cue " + kind.ToString().ToLowerInvariant();
            Count++;
            Last = name + (left ? " L" : " R");
            T(name, left);
            int n;
            s_counts.TryGetValue(name, out n);
            s_counts[name] = n + 1;
            float k = Settings.HapticStrength;
            if (k <= 0f) return;
            float now = Time.unscaledTime, ms = s_cueMs[(int)kind] * scale * k;
            if (kind == CueKind.Refuse)
            {
                // two knocks, ramping down (Meta's warning: two events over 150 ms)
                s_cues.Add(new CueStep { Left = left, At = now, Ms = ms, Name = name });
                s_cues.Add(new CueStep { Left = left, At = now + 0.12f, Ms = 4f * scale * k, Name = name });
                return;
            }
            s_cues.Add(new CueStep { Left = left, At = now, Ms = ms, Name = name });
        }

        /// The vibration test: exactly `ms` on one hand (no floor, no strength factor).
        public static void TestPulse(bool left, float ms)
        {
            s_cues.Add(new CueStep { Left = left, At = Time.unscaledTime, Ms = ms, Name = "test", Exact = true });
            Count++;
            Last = "test " + ms.ToString("0") + " ms" + (left ? " L" : " R");
        }

        static void DeliverCues(CVRSystem sys, float now)
        {
            for (int side = 0; side < 2; side++)
            {
                bool left = side == 0;
                // the due cues of this hand: the longest one is sent, the others merge into it
                int pick = -1;
                for (int i = 0; i < s_cues.Count; i++)
                {
                    CueStep c = s_cues[i];
                    if (c.Left != left || c.At > now) continue;
                    if (pick < 0 || c.Ms > s_cues[pick].Ms) pick = i;
                }
                if (pick < 0) continue;
                CueStep best = s_cues[pick];
                float ms = best.Exact ? best.Ms : Mathf.Max(best.Ms, Settings.HapticFloorMs);
                ms = Mathf.Clamp(ms, 0.5f, 65f);
                // a longer pulse still running (a cue or the game's rumble chunk): wait for it, or drop a stale cue
                float until = Mathf.Max(s_motorUntil[side], s_coveredUntil[side]);
                bool busy = now < until && (until - now) * 1000f > ms * 0.5f;
                if (busy && now - best.At < MaxWait) continue;
                int merged = 0;
                for (int i = s_cues.Count - 1; i >= 0; i--)
                {
                    CueStep c = s_cues[i];
                    if (c.Left != left || c.At > now + MergeWindow) continue;
                    if (c.At <= now || Mathf.Abs(c.At - best.At) <= MergeWindow) { s_cues.RemoveAt(i); merged++; }
                }
                if (merged > 1) CueMerged += merged - 1;
                if (busy) { CueDropped++; continue; }
                List<float> rec = s_recent[side];
                for (int i = rec.Count - 1; i >= 0; i--) if (now - rec[i] > 1f) rec.RemoveAt(i);
                if (!best.Exact && !CombatVR.Active && rec.Count >= BudgetPerSecond) { CueBudget++; continue; }
                if (sys == null || !Settings.Vibration) continue;
                uint idx = sys.GetTrackedDeviceIndexForControllerRole(left ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand);
                if (idx == OpenVR.k_unTrackedDeviceIndexInvalid) continue;
                rec.Add(now);
                s_motorUntil[side] = now + ms * 0.001f;
                s_coveredUntil[side] = Mathf.Max(s_coveredUntil[side], s_motorUntil[side]);
                LastCueMs = ms;
                Send(sys, idx, ms);
                CueCalls++;
            }
        }

        static void Send(CVRSystem sys, uint idx, float ms)
        {
            s_watch.Reset();
            s_watch.Start();
            sys.TriggerHapticPulse(idx, 0, (ushort)Mathf.Clamp(ms * 1000f, 300f, 65000f));
            s_watch.Stop();
            if (Trace) Plugin.L.LogInfo("haptics: sent " + ms.ToString("0.0") + " ms to device " + idx + " (" + sys.GetControllerRoleForTrackedDeviceIndex(idx) + ")");
            Calls++;
            double t = s_watch.Elapsed.TotalMilliseconds;
            if (t > MaxMs) MaxMs = t;
            if (t > 4.0)
            {
                SlowCalls++;
                SlowMs += t;
                PerfMonitor.Event("haptic-" + t.ToString("0"));
                if (SlowCalls <= 12 || SlowCalls % 50 == 0)
                    Plugin.L.LogInfo("haptics: a vibration request took " + t.ToString("0.0") + " ms (" + SlowCalls + " slow of " + Calls + ")");
            }
        }

        /// Calls in the last second on a hand (the budget's measure, for tests).
        public static int RecentCalls(bool left)
        {
            List<float> rec = s_recent[left ? 0 : 1];
            float now = Time.unscaledTime;
            int n = 0;
            foreach (float t in rec) if (now - t <= 1f) n++;
            return n;
        }

        /// Continuous vibration while something is held: call every frame (stops when not refreshed).
        public static void Continuous(bool left, float strength)
        {
            float v = Mathf.Clamp01(strength * Settings.HapticStrength);
            if (left) { s_contL = v; s_contFrameL = Time.frameCount; }
            else { s_contR = v; s_contFrameR = Time.frameCount; }
        }

        public static string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Count).Append(" last=").Append(Last).Append(" | ").Append(Timing())
              .Append(" | cues ").Append(CueCalls).Append(" sent, ").Append(CueMerged).Append(" merged, ").Append(CueDropped).Append(" dropped, ")
              .Append(CueBudget).Append(" over budget, last ").Append(LastCueMs.ToString("0.#")).Append(" ms, floor ").Append(Settings.HapticFloorMs.ToString("0.#"))
              .Append(" ms, max/s L ").Append(s_maxRate[0]).Append(" R ").Append(s_maxRate[1]).Append(" |");
            foreach (var kv in s_counts) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            return sb.ToString();
        }

        // ---- delivery -----------------------------------------------------------------------------------
        // OpenComposite turns each legacy pulse into one xrApplyHapticFeedback (amplitude 1, the pulse's 0-65 ms as
        // its duration), an IPC call that waits for the runtime's answer. WiVRn forwards it with a blocking,
        // mutex-guarded send on the socket that also carries the video, so a call can wait behind video data: the
        // user's session of 2026-10-11 14:44 had 59-87 ms frames in clusters of 2-4 right after slaps, touches,
        // counters and hits (the mod called it every frame while a buzz lasted). Now one call covers the next
        // ChunkSeconds of a buzz (its strength = how long the motor runs in that chunk), so a tick is one call and a
        // long buzz a few; every call is timed and a slow one is tagged in the frame's perf stall line ("haptic").
        public static float ChunkSeconds = 0.15f;
        static readonly int[] s_maxRate = new int[2];
        public static void ResetRate() { s_maxRate[0] = s_maxRate[1] = 0; }
        static readonly float[] s_coveredUntil = { -1f, -1f };
        public static int Calls, SlowCalls;
        public static double SlowMs, MaxMs;
        static readonly System.Diagnostics.Stopwatch s_watch = new System.Diagnostics.Stopwatch();

        /// Called once per frame by VRCore.
        public static void Update(CVRSystem sys)
        {
            float now = Time.unscaledTime;
            float ql = 0f, qr = 0f;
            for (int i = s_queue.Count - 1; i >= 0; i--)
            {
                Step s = s_queue[i];
                if (now > s.End) { s_queue.RemoveAt(i); continue; }
                if (now < s.Start) continue;
                if (s.Left) ql = Mathf.Max(ql, s.Strength); else qr = Mathf.Max(qr, s.Strength);
            }
            DeliverCues(sys, now);
            for (int side = 0; side < 2; side++) s_maxRate[side] = Mathf.Max(s_maxRate[side], RecentCalls(side == 0));
            float cl = Time.frameCount - s_contFrameL <= 1 ? s_contL : 0f;
            float cr = Time.frameCount - s_contFrameR <= 1 ? s_contR : 0f;
            if (sys == null) return;
            if (!Settings.Vibration) return;
            float l = Mathf.Max(Mathf.Max(s_gameL * GameRumbleScale, now < s_untilL ? s_strengthL : 0f), Mathf.Max(ql, cl));
            float r = Mathf.Max(Mathf.Max(s_gameR * GameRumbleScale, now < s_untilR ? s_strengthR : 0f), Mathf.Max(qr, cr));
            Buzz(sys, ETrackedControllerRole.LeftHand, 0, l, now, Remaining(true, now));
            Buzz(sys, ETrackedControllerRole.RightHand, 1, r, now, Remaining(false, now));
        }

        /// The game's own rumble (hits, camera shakes) is continuous while the game holds it; scaled down a little (it
        /// was made for a pad's big motors).
        public static float GameRumbleScale = 0.8f;

        /// How long the current buzz of a hand still lasts (s): the longest running pulse / step; the game's rumble and
        /// continuous holds count as one chunk.
        static float Remaining(bool left, float now)
        {
            float rem = 0f;
            float until = left ? s_untilL : s_untilR;
            if (now < until) rem = until - now;
            foreach (Step st in s_queue)
                if (st.Left == left && now >= st.Start && now < st.End) rem = Mathf.Max(rem, st.End - now);
            if ((left ? s_gameL : s_gameR) > 0.02f || Time.frameCount - (left ? s_contFrameL : s_contFrameR) <= 1) rem = Mathf.Max(rem, ChunkSeconds);
            return rem;
        }

        static void Buzz(CVRSystem sys, ETrackedControllerRole role, int side, float strength, float now, float remaining)
        {
            if (strength < 0.02f || remaining <= 0f) return;
            if (now < s_coveredUntil[side]) return;   // the motor is still running from the last call
            float chunk = Mathf.Min(remaining, ChunkSeconds);
            // the old per-frame pulses ran the motor strength x 4 ms every 11 ms: the same share of the chunk, in one go
            // pulses shorter than the player's floor are not felt (the old hover tick was 0.8 ms): lengthened to it
            float runMs = Mathf.Clamp(Mathf.Max(chunk * 1000f * strength * 0.36f, Settings.HapticFloorMs), 0.3f, 65f);
            uint idx = sys.GetTrackedDeviceIndexForControllerRole(role);
            if (idx == OpenVR.k_unTrackedDeviceIndexInvalid) return;
            s_coveredUntil[side] = now + Mathf.Max(chunk, 0.05f);
            s_recent[side].Add(now);
            Send(sys, idx, runMs);
        }

        public static string Timing() => Calls + " calls, " + SlowCalls + " slow (" + SlowMs.ToString("0") + " ms), max " + MaxMs.ToString("0.0") + " ms";
    }
}

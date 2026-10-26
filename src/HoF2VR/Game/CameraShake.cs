using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// Until You Fall's camera shake, our own code made to behave as UYF's does (docs/uyf-reference.md §3.12; the user,
    /// 2026-10-12: do it the way UYF does): the strongest running request moves
    /// the whole view (UYF: the camera rig's root; here the fight rig's anchor, so the eyes and the hands move together) in
    /// straight steps, each to a random point inside a sphere of Magnitude x Damping(age / Duration) around the rest
    /// position (UYF's damping curves fall linearly 1 -> 0), one step per IterationTime; with no request left it returns to
    /// rest. UYF's requests: block 0.05 s / 0.05 m / 8 ms steps; hit taken 0.3 s / 0.15 m / 8 ms; guard hit 0.25 s / 0.07 m /
    /// 25 ms. `[Combat] CameraShake = false` switches it off.
    internal static class CameraShake
    {
        sealed class Req { public float Start, Duration, Magnitude, Step; public string Why; }
        static readonly List<Req> s_req = new List<Req>();
        static Vector3 s_offset, s_from, s_target;
        static float s_t = 1f, s_len = 0.01f;
        static int s_frame = -1;
        const float ReturnTime = 0.05f;
        public static int Shakes;
        public static float MaxOffset;
        public static string Last = "-";

        public static void Add(float duration, float magnitude, float step, string why)
        {
            if (!CombatSettings.CameraShake || !CombatSettings.Physical || duration <= 0f || magnitude <= 0f) return;
            s_req.Add(new Req { Start = Time.unscaledTime, Duration = duration, Magnitude = magnitude, Step = Mathf.Max(0.004f, step), Why = why });
            Shakes++;
            Last = why + " " + duration.ToString("0.00") + " s / " + magnitude.ToString("0.000") + " m";
        }

        /// The view's offset this frame (world, m); advanced once per frame.
        public static Vector3 Offset
        {
            get
            {
                if (s_frame != Time.frameCount) { s_frame = Time.frameCount; Tick(); }
                return s_offset;
            }
        }

        static void Tick()
        {
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            for (int i = s_req.Count - 1; i >= 0; i--) if (now - s_req[i].Start > s_req[i].Duration) s_req.RemoveAt(i);
            if (!CombatSettings.CameraShake) s_req.Clear();
            if (s_t >= 1f)
            {
                s_offset = s_target;
                Req best = null;
                foreach (Req r in s_req) if (best == null || r.Magnitude > best.Magnitude) best = r;
                if (best != null)
                {
                    float damp = 1f - Mathf.Clamp01((now - best.Start) / best.Duration);
                    s_from = s_offset;
                    s_target = Random.insideUnitSphere * best.Magnitude * damp;
                    s_len = best.Step;
                    s_t = 0f;
                }
                else if (s_offset.sqrMagnitude > 1e-8f)
                {
                    s_from = s_offset;
                    s_target = Vector3.zero;
                    s_len = ReturnTime;
                    s_t = 0f;
                }
                else return;
            }
            s_t = Mathf.Min(1f, s_t + dt / s_len);
            s_offset = Vector3.Lerp(s_from, s_target, s_t);
            MaxOffset = Mathf.Max(MaxOffset, s_offset.magnitude);
        }

        public static void Reset()
        {
            s_req.Clear();
            s_offset = s_from = s_target = Vector3.zero;
            s_t = 1f;
        }

        public static string Describe() => "shakes " + Shakes + " max " + MaxOffset.ToString("0.000") + " m last " + Last;
    }
}

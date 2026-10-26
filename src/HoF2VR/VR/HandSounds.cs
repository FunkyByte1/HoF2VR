using System;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The hands' small sounds (docs/hand-feel-design.md §3.11): a soft tick when a finger is over something, a tap when
    /// it meets a card or a button, a crisp click when a button commits, the same click a little higher and quieter when
    /// it comes back up, a dull double knock when something can't be pressed, a wood knock for the table. Made once from
    /// code (filtered noise and damped sines, a fixed seed), played in 3D at the contact point from a small pool, routed
    /// through the game's sound-effects mixer group once it exists (so the game's volume applies), at most one per kind
    /// per 40 ms, +-4 % pitch. Volume: Hands.Sounds.
    internal static class HandSounds
    {
        public enum Kind { Tick, Tap, Click, Release, Refuse, Knock }
        // base level of each kind (linear, before Hands.Sounds): the click is the loudest, a hover tick barely there
        static readonly float[] s_level = { 0.10f, 0.28f, 0.55f, 0.33f, 0.42f, 0.45f };
        static AudioClip[] s_clips;
        static AudioSource[] s_pool;
        static int s_next;
        static readonly float[] s_last = new float[6];
        static AudioMixerGroup s_group;
        static float s_nextGroupTry;
        static System.Random s_rnd = new System.Random(7);
        public static int Played;
        public static string LastKind = "-", Group = "-";

        public static void Play(Kind k, Vector3 at, float gain = 1f)
        {
            float vol = Settings.HandSounds;
            if (vol <= 0f || !XRSettings.enabled) return;
            float now = Time.unscaledTime;
            if (now - s_last[(int)k] < 0.04f) return;
            s_last[(int)k] = now;
            try
            {
                Ensure();
                AudioSource a = s_pool[s_next++ % s_pool.Length];
                if (a == null) return;
                a.transform.position = at;
                a.clip = s_clips[k == Kind.Release ? (int)Kind.Click : (int)k];
                a.volume = Mathf.Clamp01(vol * s_level[(int)k] * gain);
                float pitch = 1f + ((float)s_rnd.NextDouble() * 2f - 1f) * 0.04f;
                a.pitch = k == Kind.Release ? pitch * 1.19f : pitch;   // the release: three semitones up
                if (s_group != null && a.outputAudioMixerGroup != s_group) a.outputAudioMixerGroup = s_group;
                a.Play();
                Played++;
                LastKind = k.ToString();
            }
            catch (Exception e) { Plugin.L.LogWarning("hand sounds: " + e.Message); }
        }

        static void Ensure()
        {
            if (s_clips == null) Make();
            if (s_pool == null || s_pool[0] == null)
            {
                var root = new GameObject("HoF2VR_HandSounds");
                UnityEngine.Object.DontDestroyOnLoad(root);
                s_pool = new AudioSource[6];
                for (int i = 0; i < s_pool.Length; i++)
                {
                    var go = new GameObject("src" + i);
                    go.transform.SetParent(root.transform, false);
                    AudioSource a = go.AddComponent<AudioSource>();
                    a.playOnAwake = false;
                    a.spatialBlend = 1f;
                    a.rolloffMode = AudioRolloffMode.Logarithmic;
                    a.minDistance = 0.5f;     // full level within arm's reach, then softer
                    a.maxDistance = 6f;
                    a.dopplerLevel = 0f;
                    a.spread = 0f;
                    a.priority = 64;
                    Hrtf.Mark(a);   // the clicks and taps at the hand: through the HRTF when it is on
                    s_pool[i] = a;
                }
            }
            if (s_group == null && Time.unscaledTime >= s_nextGroupTry)
            {
                s_nextGroupTry = Time.unscaledTime + 5f;
                AudioMixerGroup any = null;
                var names = new System.Text.StringBuilder();
                foreach (AudioMixerGroup g in Resources.FindObjectsOfTypeAll<AudioMixerGroup>())
                {
                    if (g == null) continue;
                    names.Append(g.name).Append(' ');
                    string n = g.name.ToLowerInvariant();
                    if (n == "sfx" || n == "ui" || n == "soundfx" || n == "sound effects") { s_group = g; break; }
                    if (any == null && (n.Contains("sfx") || n.Contains("effect"))) any = g;
                }
                if (s_group == null) s_group = any;
                if (s_group != null) { Group = s_group.name; Plugin.L.LogInfo("hand sounds: mixer group '" + s_group.name + "' (of: " + names + ")"); }
            }
        }

        // ---- the clips -------------------------------------------------------------------------------------------------
        static void Make()
        {
            int rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            var rnd = new System.Random(11);
            s_clips = new AudioClip[6];
            s_clips[(int)Kind.Tick] = Clip("tick", rate, 0.012f, (t, n) => 0.5f * Hp(n, 0) * Env(t, 0.0008f) + 0.35f * Mathf.Sin(2f * Mathf.PI * 3200f * t) * Env(t, 0.0012f), rnd);
            s_clips[(int)Kind.Tap] = Clip("tap", rate, 0.04f, (t, n) => 0.55f * Mathf.Sin(2f * Mathf.PI * 880f * t) * Env(t, 0.006f) + 0.35f * Lp(n, 1) * Env(t, 0.003f), rnd);
            s_clips[(int)Kind.Click] = Clip("click", rate, 0.035f, (t, n) =>
                0.7f * Hp(n, 2) * Env(t, 0.0012f)                                   // the 2-4 kHz snap
                + 0.6f * Mathf.Sin(2f * Mathf.PI * 600f * t) * Env(t, 0.004f)        // the 600 Hz body
                + 0.25f * Mathf.Sin(2f * Mathf.PI * 2700f * t) * Env(t, 0.002f), rnd);
            s_clips[(int)Kind.Refuse] = Clip("refuse", rate, 0.2f, (t, n) => Knock(t, 0f, 230f, 1f, n) + Knock(t, 0.09f, 200f, 0.7f, n), rnd);
            s_clips[(int)Kind.Knock] = Clip("knock", rate, 0.14f, (t, n) => Knock(t, 0f, 180f, 1f, n) + 0.4f * Mathf.Sin(2f * Mathf.PI * 410f * t) * Env(t, 0.01f), rnd);
            s_clips[(int)Kind.Release] = s_clips[(int)Kind.Click];
        }

        static float Env(float t, float tau) => t < 0f ? 0f : Mathf.Exp(-t / tau) * Mathf.Min(1f, t / 0.0004f);

        static float Knock(float t, float at, float hz, float amp, float[] n)
        {
            float u = t - at;
            if (u < 0f) return 0f;
            return amp * (0.75f * Mathf.Sin(2f * Mathf.PI * hz * u) * Env(u, 0.014f) + 0.3f * Lp(n, 1) * Env(u, 0.003f));
        }

        // filtered noise per sample: n[0] high-passed (bright), n[1] low-passed (soft), n[2] band 2-4 kHz
        static float Hp(float[] n, int i) => n[i == 0 ? 0 : 2];
        static float Lp(float[] n, int i) => n[1];

        static AudioClip Clip(string name, int rate, float seconds, Func<float, float[], float> f, System.Random rnd)
        {
            int len = Mathf.CeilToInt(seconds * rate);
            var data = new float[len];
            var n = new float[3];
            float lp = 0f, lp2 = 0f, hpPrev = 0f, hpOut = 0f, bpLow = 0f;
            float aLp = Mathf.Exp(-2f * Mathf.PI * 1500f / rate), aBp = Mathf.Exp(-2f * Mathf.PI * 4000f / rate), aHp = Mathf.Exp(-2f * Mathf.PI * 2000f / rate);
            float peak = 1e-4f;
            for (int i = 0; i < len; i++)
            {
                float w = (float)(rnd.NextDouble() * 2.0 - 1.0);
                lp = aLp * lp + (1f - aLp) * w;
                hpOut = aHp * (hpOut + w - hpPrev);
                hpPrev = w;
                bpLow = aBp * bpLow + (1f - aBp) * hpOut;
                lp2 = lp;
                n[0] = hpOut; n[1] = lp2 * 2.5f; n[2] = bpLow * 1.6f;
                float v = f(i / (float)rate, n);
                // a short fade out at the end (no click from cutting off)
                float tail = Mathf.Clamp01((len - i) / (0.003f * rate));
                data[i] = v * tail;
                peak = Mathf.Max(peak, Mathf.Abs(data[i]));
            }
            float g = 0.9f / peak;
            for (int i = 0; i < len; i++) data[i] *= g;
            AudioClip c = AudioClip.Create("HoF2VR_hand_" + name, len, 1, rate, false);
            c.SetData(data, 0);
            return c;
        }

        public static string Describe() => Played + " played, last " + LastKind + ", group " + Group + ", volume " + Settings.HandSounds.ToString("0.00");
    }
}

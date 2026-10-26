using System;
using UnityEngine;
using UnityEngine.Audio;

namespace HoF2VR
{
    /// Sounds the combat work makes itself (generated once at start-up from code; nothing from other games):
    ///  * Sting: the "behind you" warning, a low thump with a hiss rising over it (0.5 s), only for strikes from outside
    ///    the view (docs/combat-uyf-design.md §7.5.3 item 5, §8.5.4);
    ///  * Riser: a soft breathy tone (loop) that the warning raises in pitch and loudness toward the impact;
    ///  * Hum: the exit portal's drone (loop), from where the portal stands, so it can be found out of sight (§7.7).
    /// Played as fully 3D sources, routed through the game's sound-effects mixer group when it can be found (its volume
    /// slider applies).
    internal static class CombatSounds
    {
        static AudioClip s_sting, s_riser, s_hum;
        static AudioMixerGroup s_sfx;
        static bool s_looked;
        static readonly System.Random s_rng = new System.Random(7);

        public static AudioClip Sting => s_sting ?? (s_sting = MakeSting());
        public static AudioClip Riser => s_riser ?? (s_riser = MakeRiser());
        public static AudioClip Rush => s_rush ?? (s_rush = MakeRush());
        static AudioClip s_rush;

        /// 2 s of soft noise for the weapon's movement loop (WeaponTrail.Sound filters and pitches it by the blade's speed):
        /// the end crossfaded into the start, so the loop has no seam.
        static AudioClip MakeRush()
        {
            int sr = Rate, n = sr * 2, fade = sr / 5;
            var d = new float[n + fade];
            float lp = 0f;
            for (int i = 0; i < n + fade; i++) { lp += (Noise() - lp) * 0.35f; d[i] = lp; }
            var o = new float[n];
            for (int i = 0; i < n; i++)
            {
                float v = d[i];
                if (i < fade) { float w = i / (float)fade; v = d[i] * w + d[n + i] * (1f - w); }
                o[i] = Mathf.Clamp(v * 1.6f, -1f, 1f);
            }
            AudioClip c = AudioClip.Create("HoF2VR_Rush", n, 1, sr, false);
            c.SetData(o, 0);
            return c;
        }
        public static AudioClip Hum => s_hum ?? (s_hum = MakeHum());
        public static AudioClip Whoosh => s_whoosh ?? (s_whoosh = MakeWhoosh());
        public static AudioClip Chime => s_chime ?? (s_chime = MakeChime());
        static AudioClip s_whoosh, s_chime;
        static AudioSource s_oneShot;

        /// A one-shot 3D sound at a point (one shared source, moved there).
        public static void PlayAt(AudioClip clip, Vector3 at, float volume)
        {
            try
            {
                if (s_oneShot == null)
                {
                    s_oneShot = Source("HoF2VR_CombatOneShot", null, false, 15f);
                    UnityEngine.Object.DontDestroyOnLoad(s_oneShot.gameObject);
                    s_oneShot.minDistance = 0.5f;
                }
                s_oneShot.transform.position = at;
                s_oneShot.PlayOneShot(clip, volume);
            }
            catch (Exception) { }
        }

        static AudioClip MakeWhoosh()
        {
            // 0.22 s: noise through a band that sweeps up then down, a fast rise and a soft tail
            int sr = Rate, n = (int)(sr * 0.22f);
            var d = new float[n];
            float lp = 0f, hp = 0f, prev = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float a = Mathf.Lerp(0.08f, 0.45f, Mathf.Sin(t * Mathf.PI));   // band follows the swell
                lp += (Noise() - lp) * a;
                hp = 0.9f * (hp + lp - prev);
                prev = lp;
                float env = Mathf.Sin(Mathf.Clamp01(t * 1.6f) * Mathf.PI * 0.5f) * (1f - t) * 1.8f;
                d[i] = Mathf.Clamp(hp * env, -1f, 1f);
            }
            AudioClip c = AudioClip.Create("HoF2VR_Whoosh", n, 1, sr, false);
            c.SetData(d, 0);
            return c;
        }

        static AudioClip MakeChime()
        {
            // 0.5 s: a bright two-partial ping (a fifth), quick attack, ringing decay
            int sr = Rate, n = (int)(sr * 0.5f);
            var d = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float env = Mathf.Clamp01(t / 0.004f) * Mathf.Exp(-t / 0.12f);
                d[i] = (Mathf.Sin(2f * Mathf.PI * 1320f * t) * 0.5f + Mathf.Sin(2f * Mathf.PI * 1980f * t) * 0.3f + Mathf.Sin(2f * Mathf.PI * 2640f * t) * 0.12f) * env * 0.7f;
            }
            AudioClip c = AudioClip.Create("HoF2VR_Chime", n, 1, sr, false);
            c.SetData(d, 0);
            return c;
        }

        static int Rate => AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;

        /// The game's sound-effects group (a name with "sfx" or "effect"), or null.
        public static AudioMixerGroup Sfx()
        {
            if (s_looked) return s_sfx;
            s_looked = true;
            try
            {
                var names = new System.Text.StringBuilder();
                int best = 0;
                foreach (AudioMixerGroup g in Resources.FindObjectsOfTypeAll<AudioMixerGroup>())
                {
                    string n = g.name.ToLowerInvariant();
                    names.Append(' ').Append(g.name).Append(g.audioMixer != null ? "(" + g.audioMixer.name + ")" : "");
                    // the game's sound effects: "SFX" itself, else a group with sfx in its name (never the camera's or
                    // the music's groups)
                    int score = n == "sfx" ? 3 : n.Contains("sfx") && !n.Contains("camera") && !n.Contains("music") ? 2 : 0;
                    if (score > best) { best = score; s_sfx = g; }
                }
                Plugin.L.LogInfo("CombatSounds: mixer group " + (s_sfx != null ? "'" + s_sfx.name + "'" : "none (master only)") + "; groups:" + names);
            }
            catch (Exception e) { Plugin.L.LogWarning("CombatSounds: " + e.Message); }
            return s_sfx;
        }

        /// A 3D source on its own object (follow it yourself, or parent it).
        public static AudioSource Source(string name, AudioClip clip, bool loop, float maxDistance)
        {
            var go = new GameObject(name);
            var s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.loop = loop;
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.dopplerLevel = 0f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 1f;
            s.maxDistance = maxDistance;
            s.spread = 30f;
            s.outputAudioMixerGroup = Sfx();
            Hrtf.Mark(s);   // one of the mod's own 3D sounds: through the HRTF when it is on, whatever its group
            return s;
        }

        static float Noise() => (float)(s_rng.NextDouble() * 2.0 - 1.0);

        static AudioClip MakeSting()
        {
            int sr = Rate, n = (int)(sr * 0.5f);
            var d = new float[n];
            float lp = 0f, hp = 0f, prev = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                // thump: 58 Hz falling to 42 Hz, fast decay, with a soft click
                float f = Mathf.Lerp(58f, 42f, Mathf.Clamp01(t / 0.2f));
                float thump = Mathf.Sin(2f * Mathf.PI * f * t) * Mathf.Exp(-t / 0.09f) * 0.9f + (t < 0.004f ? Noise() * 0.3f * (1f - t / 0.004f) : 0f);
                // hiss: band-limited noise rising from 0.05 s, peaking near 0.42 s
                float w = Noise();
                lp += (w - lp) * 0.35f;              // low-pass
                hp = 0.92f * (hp + lp - prev);       // then high-pass: a breathy band
                prev = lp;
                float env = Mathf.Clamp01((t - 0.05f) / 0.37f);
                env = env * env * (t > 0.44f ? Mathf.Clamp01((0.5f - t) / 0.06f) : 1f);
                d[i] = Mathf.Clamp(thump + hp * 1.6f * env, -1f, 1f);
            }
            AudioClip c = AudioClip.Create("HoF2VR_Sting", n, 1, sr, false);
            c.SetData(d, 0);
            return c;
        }

        static AudioClip MakeRiser()
        {
            // 1 s loop: a breathy tone (two sines a fifth apart + filtered noise), seamless (whole cycles)
            int sr = Rate, n = sr;
            var d = new float[n];
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float tone = Mathf.Sin(2f * Mathf.PI * 220f * t) * 0.35f + Mathf.Sin(2f * Mathf.PI * 330f * t) * 0.2f + Mathf.Sin(2f * Mathf.PI * 110f * t) * 0.25f;
                lp += (Noise() - lp) * 0.12f;
                float trem = 0.85f + 0.15f * Mathf.Sin(2f * Mathf.PI * 6f * t);
                d[i] = (tone * trem + lp * 0.5f) * 0.6f;
            }
            AudioClip c = AudioClip.Create("HoF2VR_Riser", n, 1, sr, false);
            c.SetData(d, 0);
            return c;
        }

        // ---- Physical mode's layers (docs/combat-uyf-design.md §8.5.8): under the game's own hit, parry and bash sounds ----
        public static AudioClip Thump => s_thump ?? (s_thump = MakeThump());
        public static AudioClip Ring => s_ring ?? (s_ring = MakeRing());
        public static AudioClip Clank => s_clank ?? (s_clank = MakeClank());
        static AudioClip s_thump, s_ring, s_clank;
        static AudioSource[] s_pool;
        static int s_next;
        public static int Layers;   // diagnostics: layered one-shots played

        /// A one-shot at a point from a small pool of sources, so layers that start together don't cut each other off.
        public static void Layer(AudioClip clip, Vector3 at, float volume, float pitch = 1f)
        {
            try
            {
                if (s_pool == null)
                {
                    s_pool = new AudioSource[8];
                    for (int i = 0; i < s_pool.Length; i++)
                    {
                        s_pool[i] = Source("HoF2VR_CombatLayer" + i, null, false, 20f);
                        UnityEngine.Object.DontDestroyOnLoad(s_pool[i].gameObject);
                        s_pool[i].minDistance = 0.8f;
                    }
                }
                AudioSource src = s_pool[s_next];
                s_next = (s_next + 1) % s_pool.Length;
                src.transform.position = at;
                src.pitch = pitch * (1f + ((float)s_rng.NextDouble() - 0.5f) * 0.08f);
                src.PlayOneShot(clip, Mathf.Clamp01(volume));
                Layers++;
            }
            catch (Exception) { }
        }

        // ---- combat Stage 1: the premonitions' sounds (UYF's cues, our own synthesis; docs/uyf-reference.md §3.2) ----
        public static AudioClip Premonition => s_prem ?? (s_prem = MakePremonition());
        public static AudioClip LockIn => s_lock ?? (s_lock = MakeLockIn());
        public static AudioClip Clash => s_clash ?? (s_clash = MakeClash());
        public static AudioClip DodgeCue => s_dcue ?? (s_dcue = MakeDodgeCue());
        public static AudioClip DodgeReady => s_dready ?? (s_dready = MakeDodgeReady());
        public static AudioClip Dodged => s_dodged ?? (s_dodged = MakeDodged());
        static AudioClip s_prem, s_lock, s_clash, s_dcue, s_dready, s_dodged;

        static AudioClip Clip(string name, float[] d)
        {
            AudioClip c = AudioClip.Create(name, d.Length, 1, Rate, false);
            c.SetData(d, 0);
            return c;
        }

        static AudioClip MakePremonition()
        {
            // 0.35 s: a bar appears - an airy swell (band noise rising) under a soft glassy tone
            int sr = Rate, n = (int)(sr * 0.35f);
            var d = new float[n];
            float lp = 0f, hp = 0f, prev = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr, k = i / (float)n;
                lp += (Noise() - lp) * Mathf.Lerp(0.05f, 0.35f, k);
                hp = 0.92f * (hp + lp - prev);
                prev = lp;
                float env = Mathf.Sin(Mathf.Clamp01(k * 1.4f) * Mathf.PI * 0.5f) * (1f - k);
                float tone = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(880f, 1175f, k) * t) * 0.18f * Mathf.Clamp01(k * 4f) * (1f - k);
                d[i] = Mathf.Clamp(hp * env * 1.2f + tone, -1f, 1f);
            }
            return Clip("HoF2VR_Premonition", d);
        }

        static AudioClip MakeLockIn()
        {
            // 0.16 s: the guard locks in - a crisp tick and a bright rising two-note "ti-ting"
            int sr = Rate, n = (int)(sr * 0.16f);
            var d = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float click = t < 0.002f ? Noise() * (1f - t / 0.002f) * 0.6f : 0f;
                float n1 = t < 0.05f ? Mathf.Sin(2f * Mathf.PI * 1568f * t) * Mathf.Exp(-t / 0.03f) : 0f;
                float t2 = t - 0.045f;
                float n2 = t2 > 0f ? (Mathf.Sin(2f * Mathf.PI * 2349f * t2) + 0.35f * Mathf.Sin(2f * Mathf.PI * 4698f * t2)) * Mathf.Clamp01(t2 / 0.002f) * Mathf.Exp(-t2 / 0.045f) : 0f;
                d[i] = Mathf.Clamp(click + n1 * 0.45f + n2 * 0.5f, -1f, 1f);
            }
            return Clip("HoF2VR_LockIn", d);
        }

        static AudioClip MakeClash()
        {
            // 0.6 s: steel on steel - a hard transient, a bright inharmonic "shing" with a fast shimmer, a low knock
            int sr = Rate, n = (int)(sr * 0.6f);
            var d = new float[n];
            float[] fr = { 1873f, 2741f, 3517f, 4922f, 6230f };
            float[] am = { 0.32f, 0.26f, 0.2f, 0.14f, 0.08f };
            float[] dk = { 0.22f, 0.16f, 0.12f, 0.08f, 0.05f };
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float v = 0f;
                for (int k = 0; k < fr.Length; k++) v += Mathf.Sin(2f * Mathf.PI * fr[k] * t * (1f + 0.004f * Mathf.Sin(2f * Mathf.PI * 23f * t))) * am[k] * Mathf.Exp(-t / dk[k]);
                lp += (Noise() - lp) * 0.6f;
                float hit = lp * Mathf.Exp(-t / 0.004f) * 0.9f;
                float knock = Mathf.Sin(2f * Mathf.PI * 160f * t) * Mathf.Exp(-t / 0.03f) * 0.5f;
                d[i] = Mathf.Clamp((v + hit + knock) * Mathf.Clamp01(t / 0.0015f) * 0.85f, -1f, 1f);
            }
            return Clip("HoF2VR_Clash", d);
        }

        static AudioClip MakeDodgeCue()
        {
            // 0.5 s: an unblockable blow is coming - a low pulsing hum with a warning rasp
            int sr = Rate, n = (int)(sr * 0.5f);
            var d = new float[n];
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr, k = i / (float)n;
                float env = Mathf.Clamp01(t / 0.02f) * (1f - k) * (1f - k);
                float hum = Mathf.Sin(2f * Mathf.PI * 110f * t) * 0.5f + Mathf.Sin(2f * Mathf.PI * 165f * t) * 0.3f;
                float am = 0.6f + 0.4f * Mathf.Sin(2f * Mathf.PI * 14f * t);
                lp += (Noise() - lp) * 0.2f;
                d[i] = Mathf.Clamp((hum * am + lp * 0.35f) * env * 0.9f, -1f, 1f);
            }
            return Clip("HoF2VR_DodgeCue", d);
        }

        static AudioClip MakeDodgeReady()
        {
            // 0.18 s: the head is far enough along the arrow - two quick rising notes
            int sr = Rate, n = (int)(sr * 0.18f);
            var d = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float f0 = t < 0.07f ? 784f : 1175f, t0 = t < 0.07f ? t : t - 0.07f;
                d[i] = Mathf.Sin(2f * Mathf.PI * f0 * t) * Mathf.Clamp01(t0 / 0.003f) * Mathf.Exp(-t0 / 0.05f) * 0.55f;
            }
            return Clip("HoF2VR_DodgeReady", d);
        }

        static AudioClip MakeDodged()
        {
            // 0.45 s: dodged - a close whoosh past the ear and a bright airy ping
            int sr = Rate, n = (int)(sr * 0.45f);
            var d = new float[n];
            float lp = 0f, hp = 0f, prev = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr, k = i / (float)n;
                lp += (Noise() - lp) * Mathf.Lerp(0.5f, 0.08f, k);
                hp = 0.88f * (hp + lp - prev);
                prev = lp;
                float wh = hp * Mathf.Sin(Mathf.Clamp01(k * 3f) * Mathf.PI * 0.5f) * (1f - k) * 1.4f;
                float t2 = t - 0.06f;
                float ping = t2 > 0f ? (Mathf.Sin(2f * Mathf.PI * 1760f * t2) * 0.4f + Mathf.Sin(2f * Mathf.PI * 2637f * t2) * 0.2f) * Mathf.Exp(-t2 / 0.12f) : 0f;
                d[i] = Mathf.Clamp(wh + ping, -1f, 1f);
            }
            return Clip("HoF2VR_Dodged", d);
        }

        // ---- combat Stage 2: the enemy guard's sounds (UYF's cues per hit quality and for crits; our own synthesis) ----
        public static AudioClip GuardHit => s_ghit ?? (s_ghit = MakeGuardHit());
        public static AudioClip GuardBreak => s_gbreak ?? (s_gbreak = MakeGuardBreak());
        public static AudioClip Crit => s_crit ?? (s_crit = MakeCrit());
        public static AudioClip Deflect => s_deflect ?? (s_deflect = MakeDeflect());
        public static AudioClip ComboDone => s_combo ?? (s_combo = MakeComboDone());
        static AudioClip s_ghit, s_gbreak, s_crit, s_deflect, s_combo;

        static AudioClip MakeGuardHit()
        {
            // 0.22 s: a blow on a guard - a dense mid thud with a dull plated rattle (no ring: it didn't get through)
            int sr = Rate, n = (int)(sr * 0.22f);
            var d = new float[n];
            float lp = 0f, lp2 = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float thud = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(150f, 95f, Mathf.Clamp01(t / 0.08f)) * t) * Mathf.Exp(-t / 0.05f) * 0.8f;
                lp += (Noise() - lp) * 0.35f;
                lp2 += (lp - lp2) * 0.5f;
                float rattle = lp2 * Mathf.Exp(-t / 0.03f) * (0.7f + 0.3f * Mathf.Sin(2f * Mathf.PI * 41f * t)) * 0.9f;
                float plate = (Mathf.Sin(2f * Mathf.PI * 690f * t) * 0.18f + Mathf.Sin(2f * Mathf.PI * 1130f * t) * 0.1f) * Mathf.Exp(-t / 0.025f);
                d[i] = Mathf.Clamp((thud + rattle + plate) * Mathf.Clamp01(t / 0.001f), -1f, 1f);
            }
            return Clip("HoF2VR_GuardHit", d);
        }

        static AudioClip MakeGuardBreak()
        {
            // 0.9 s: the guard breaks - a hard crack, glassy shards scattering (falling bright partials) over a deep boom
            int sr = Rate, n = (int)(sr * 0.9f);
            var d = new float[n];
            float lp = 0f;
            float[] fr = { 2310f, 3170f, 4020f, 5260f, 6810f, 2790f };
            float[] at = { 0.0f, 0.03f, 0.07f, 0.12f, 0.18f, 0.25f };
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                lp += (Noise() - lp) * 0.7f;
                float crack = lp * Mathf.Exp(-t / 0.012f) * 1.1f;
                float boom = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(70f, 38f, Mathf.Clamp01(t / 0.4f)) * t) * Mathf.Exp(-t / 0.22f) * 0.75f;
                float shards = 0f;
                for (int k = 0; k < fr.Length; k++)
                {
                    float tk = t - at[k];
                    if (tk > 0f) shards += Mathf.Sin(2f * Mathf.PI * fr[k] * (1f - 0.08f * tk) * tk) * Mathf.Exp(-tk / 0.09f) * 0.16f;
                }
                d[i] = Mathf.Clamp(crack + boom + shards, -1f, 1f);
            }
            return Clip("HoF2VR_GuardBreak", d);
        }

        static AudioClip MakeCrit()
        {
            // 0.7 s: a crit lands - a fast slicing hiss into a heavy cut and a bright, sustained ring
            int sr = Rate, n = (int)(sr * 0.7f);
            var d = new float[n];
            float lp = 0f, hp = 0f, prev = 0f;
            float[] fr = { 880f, 1320f, 1980f, 2640f };
            float[] am = { 0.32f, 0.22f, 0.14f, 0.08f };
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                lp += (Noise() - lp) * 0.55f;
                hp = 0.9f * (hp + lp - prev);
                prev = lp;
                float slice = t < 0.07f ? hp * Mathf.Sin(Mathf.PI * t / 0.07f) * 0.9f : 0f;
                float t2 = t - 0.06f;
                float cut = t2 > 0f ? Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(120f, 60f, Mathf.Clamp01(t2 / 0.1f)) * t2) * Mathf.Exp(-t2 / 0.07f) * 0.8f : 0f;
                float ring = 0f;
                if (t2 > 0f) for (int k = 0; k < fr.Length; k++) ring += Mathf.Sin(2f * Mathf.PI * fr[k] * t2) * am[k] * Mathf.Exp(-t2 / (0.35f - k * 0.06f));
                d[i] = Mathf.Clamp(slice + cut + ring, -1f, 1f);
            }
            return Clip("HoF2VR_Crit", d);
        }

        static AudioClip MakeDeflect()
        {
            // 0.15 s: a cut that doesn't count (the wrong way across a crit) - a short dry tick and a muted ping
            int sr = Rate, n = (int)(sr * 0.15f);
            var d = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float tick = t < 0.003f ? Noise() * (1f - t / 0.003f) * 0.5f : 0f;
                float ping = Mathf.Sin(2f * Mathf.PI * 520f * t) * Mathf.Exp(-t / 0.035f) * 0.35f;
                d[i] = Mathf.Clamp(tick + ping, -1f, 1f);
            }
            return Clip("HoF2VR_Deflect", d);
        }

        static AudioClip MakeComboDone()
        {
            // 0.8 s: the combo is complete - a quick rising three-note chime over a soft swell
            int sr = Rate, n = (int)(sr * 0.8f);
            var d = new float[n];
            float[] notes = { 784f, 988f, 1319f };
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr, v = 0f;
                for (int k = 0; k < 3; k++)
                {
                    float tk = t - k * 0.07f;
                    if (tk > 0f) v += (Mathf.Sin(2f * Mathf.PI * notes[k] * tk) + 0.3f * Mathf.Sin(4f * Mathf.PI * notes[k] * tk)) * Mathf.Clamp01(tk / 0.004f) * Mathf.Exp(-tk / 0.25f) * 0.3f;
                }
                d[i] = Mathf.Clamp(v, -1f, 1f);
            }
            return Clip("HoF2VR_ComboDone", d);
        }

        static AudioClip MakeThump()
        {
            // 0.3 s: a deep body blow - 90 Hz falling to 48 Hz, a hard click on top, fast decay
            int sr = Rate, n = (int)(sr * 0.3f);
            var d = new float[n];
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float f = Mathf.Lerp(90f, 48f, Mathf.Clamp01(t / 0.12f));
                float body = Mathf.Sin(2f * Mathf.PI * f * t) * Mathf.Exp(-t / 0.075f);
                lp += (Noise() - lp) * 0.5f;
                float click = lp * Mathf.Exp(-t / 0.006f) * 0.8f;
                d[i] = Mathf.Clamp((body + click) * 0.95f, -1f, 1f);
            }
            AudioClip c = AudioClip.Create("HoF2VR_Thump", n, 1, sr, false);
            c.SetData(d, 0);
            return c;
        }

        static AudioClip MakeRing()
        {
            // 0.9 s: a struck-steel ring for the counter - inharmonic partials, a bright attack, a long shimmer
            int sr = Rate, n = (int)(sr * 0.9f);
            var d = new float[n];
            float[] fr = { 523f, 1187f, 1661f, 2489f, 3322f };
            float[] am = { 0.45f, 0.3f, 0.22f, 0.14f, 0.08f };
            float[] dk = { 0.38f, 0.25f, 0.18f, 0.12f, 0.08f };
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float v = 0f;
                for (int k = 0; k < fr.Length; k++) v += Mathf.Sin(2f * Mathf.PI * fr[k] * t * (1f + 0.002f * Mathf.Sin(2f * Mathf.PI * 5f * t))) * am[k] * Mathf.Exp(-t / dk[k]);
                v += (t < 0.003f ? Noise() * 0.5f * (1f - t / 0.003f) : 0f);
                d[i] = Mathf.Clamp(v * Mathf.Clamp01(t / 0.002f) * 0.8f, -1f, 1f);
            }
            AudioClip c = AudioClip.Create("HoF2VR_Ring", n, 1, sr, false);
            c.SetData(d, 0);
            return c;
        }

        static AudioClip MakeClank()
        {
            // 0.25 s: a dull clank (a blade stopped by a shield) - a mid knock with a short metallic edge
            int sr = Rate, n = (int)(sr * 0.25f);
            var d = new float[n];
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float knock = Mathf.Sin(2f * Mathf.PI * 210f * t) * Mathf.Exp(-t / 0.035f) * 0.7f;
                float metal = (Mathf.Sin(2f * Mathf.PI * 1430f * t) * 0.25f + Mathf.Sin(2f * Mathf.PI * 2210f * t) * 0.15f) * Mathf.Exp(-t / 0.05f);
                lp += (Noise() - lp) * 0.3f;
                d[i] = Mathf.Clamp(knock + metal + lp * Mathf.Exp(-t / 0.01f) * 0.5f, -1f, 1f);
            }
            AudioClip c = AudioClip.Create("HoF2VR_Clank", n, 1, sr, false);
            c.SetData(d, 0);
            return c;
        }

        static AudioClip MakeHum()
        {
            // 4 s loop: a low drone (55 / 82.5 / 110 Hz) with a slow swell and a soft airy layer; whole cycles at 0.25 Hz
            int sr = Rate, n = sr * 4;
            var d = new float[n];
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float swell = 0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * 0.25f * t);
                float drone = Mathf.Sin(2f * Mathf.PI * 55f * t) * 0.5f + Mathf.Sin(2f * Mathf.PI * 82.5f * t) * 0.25f + Mathf.Sin(2f * Mathf.PI * 110f * t + Mathf.Sin(2f * Mathf.PI * 0.5f * t)) * 0.2f;
                lp += (Noise() - lp) * 0.04f;
                d[i] = (drone * swell + lp * 0.6f) * 0.55f;
            }
            AudioClip c = AudioClip.Create("HoF2VR_PortalHum", n, 1, sr, false);
            c.SetData(d, 0);
            return c;
        }
    }
}

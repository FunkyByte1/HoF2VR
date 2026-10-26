using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Sound in VR.
    ///  * Ears: the game hears through listeners on its cameras (the card-table camera, the fight
    ///    camera's listener object, the tunnel camera), which follow the game's camera, not the
    ///    player's head. In VR one listener of our own follows the head - position and rotation - in
    ///    every place, and the game's listeners are kept switched off, so every 3D sound pans as the
    ///    player turns their head.
    ///  * The Dealer's voice: his recorded lines play on the game's Voice AudioSource (mixer group
    ///    "Dealer"), which sits at the world origin. At the card table it is moved to the Dealer's head
    ///    every frame and made fully 3D (no Doppler, logarithmic roll-off sized for the room). Its mixer
    ///    routing (voice volume slider, pause) and the lip flap that reads it are left as they are. In
    ///    fights and at the title the game's own setting applies (the game makes the voice 2D in fights).
    ///  * The rest of the flat world (2026-10-13): effects the game plays flat (2D) although they come from something in the
    ///    room - the Dealer's attract magic, the challenge tokens' whoosh and unlock, the deck builder's and the table's card
    ///    sounds, the food card - are made 3D at their object the moment they start (SoundEffectController.Play) at the
    ///    table, the side table and the title: full volume within WorldMin (the room's whole size: only their direction
    ///    changes, not the mix), no Doppler. Music, stings, the Dealer's voice and the pooled menu sounds stay as they are.
    internal class VRAudio : MonoBehaviour
    {
        public static VRAudio I;
        public static bool EarsOn = true;      // test toggle `ears 0|1`
        public static bool DealerOn = true;    // test toggle `voice 3d 0|1`
        /// The Dealer's voice: full volume up to MinDistance real metres (the player's spots at the
        /// table and the side table are ~2 m from him), softer further away, no change past MaxDistance.
        public static float MinDistance = 2.2f, MaxDistance = 25f, Spread = 40f;
        /// From the head bone to the mouth (real metres, along the Dealer's facing / up).
        public static float MouthForward = 0.09f, MouthUp = -0.03f;

        AudioListener m_ears;
        readonly List<AudioListener> m_muted = new List<AudioListener>();
        int m_lastCamCount = -1, m_scanFrame = -1000;
        float m_scanTime = -100f;
        bool m_sceneLoaded;

        // the Dealer's voice
        AudioSource m_voice;
        bool m_saved, m_placed;
        Vector3 m_savedPos;
        AudioRolloffMode m_savedRolloff;
        float m_savedMin, m_savedMax, m_savedSpread, m_savedDoppler, m_savedBlend;
        Transform m_head;
        Dealer m_headOf;
        float m_nextHeadSearch;

        /// Our head-following listener while it is in use (Hrtf's distance check and test probes), else null.
        public static Transform EarsTransform => I != null && I.m_ears != null && I.m_ears.isActiveAndEnabled ? I.m_ears.transform : null;

        /// The Dealer's head bone VRAudio found (the narration's tests measure from it), or null.
        public static Transform DealerHeadBone => I != null && I.m_head != null ? I.m_head : null;

        /// Search the head bone again (DealerMotion deoptimized his rig: the bones are new transforms).
        public void ForgetHead()
        {
            m_head = null;
            m_headOf = null;
            m_nextHeadSearch = 0f;
        }

        void Awake()
        {
            I = this;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) => m_sceneLoaded = true;
            Diag.Commands["ears"] = a =>
            {
                if (a.Length > 1) EarsOn = a[1] == "1";
                return "ears " + (EarsOn ? "on" : "off") + " " + DescribeEars();
            };
            Diag.Commands["voice"] = VoiceCommand;
            Diag.Commands["worldsound"] = a =>
            {
                if (a.Length > 1) WorldOn = a[1] == "1";
                return "world sounds " + (WorldOn ? "3D" : "as the game") + " | made 3D " + World3D + " (now " + s_made3D.Count + "), kept " + WorldKept + " | last " + WorldLast;
            };
            Diag.Commands["audio"] = AudioCommand;
            Diag.StatusExtras["ears"] = () => Diag.Q(m_ears != null && m_ears.isActiveAndEnabled ? "head, " + m_muted.Count + " game listeners off" : "game");
            Diag.StatusExtras["dealerVoice"] = () => Diag.Q(m_placed ? "3D at the Dealer" : "game");
        }

        public static void Apply(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(HarmonyLib.AccessTools.Method(typeof(SoundEffectController), "Play"),
                        prefix: new HarmonyLib.HarmonyMethod(typeof(VRAudio), nameof(SoundPlayPrefix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("VRAudio: world sounds patch failed (they stay as the game has them): " + e.Message); }
        }

        // ---- the rest of the flat world in 3D ------------------------------------------------------------------------------
        public static bool WorldOn = true;   // test toggle `worldsound 0|1`
        public static float WorldMin = 2.5f, WorldMax = 25f, WorldSpread = 60f, WorldNear = 8f;
        /// Mixer groups that stay flat: music, stings, the Dealer's own voice (placed by DealerVoice), the narration.
        static readonly HashSet<string> s_keep2D = new HashSet<string> { "CardTable", "Music", "EncounterStings", "Stings", "Dealer", "Narration" };
        struct Flat { public AudioSource S; public float Blend, Min, Max, Spread, Doppler; public AudioRolloffMode Rolloff; }
        static readonly Dictionary<int, Flat> s_made3D = new Dictionary<int, Flat>();
        public static int World3D, WorldKept;
        public static string WorldLast = "-";

        static bool WorldPlace => XRSettings.enabled && WorldOn && VRRig.I != null && !CombatVR.Active &&
                                  (Places.AtCaravan || Places.Current == Places.Place.Title);

        /// Right before a game effect starts: flat but from a thing in the room -> 3D at that thing.
        static void SoundPlayPrefix(SoundEffectController __instance)
        {
            try
            {
                AudioSource src = __instance.AudioSource;
                if (src == null || src.spatialBlend >= 0.5f || !WorldPlace) return;
                string group = src.outputAudioMixerGroup != null ? src.outputAudioMixerGroup.name : "";
                if (s_keep2D.Contains(group)) return;
                Transform t = src.transform;
                // pooled sources (menu clicks, hovers) are played from wherever the pool keeps them: not a place in the room
                if (t.root.name.StartsWith("PoolManager")) { WorldKept++; return; }
                float d = Vector3.Distance(VRRig.I.HeadWorldPos, t.position) / VRRig.I.Scale;
                if (d > WorldNear) { WorldKept++; return; }
                int id = src.GetInstanceID();
                if (!s_made3D.ContainsKey(id))
                    s_made3D[id] = new Flat { S = src, Blend = src.spatialBlend, Min = src.minDistance, Max = src.maxDistance, Spread = src.spread, Doppler = src.dopplerLevel, Rolloff = src.rolloffMode };
                float sc = VRRig.I.Scale;
                src.spatialBlend = 1f;
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                src.minDistance = WorldMin * sc;
                src.maxDistance = WorldMax * sc;
                src.spread = WorldSpread;
                src.dopplerLevel = 0f;
                World3D++;
                WorldLast = VRUI.Path(t) + " (" + group + ", " + d.ToString("0.0") + " m)";
                if (World3D <= 40 || World3D % 100 == 0) Plugin.L.LogInfo("VRAudio: '" + WorldLast + "' plays 3D at its object (was flat)");
            }
            catch (Exception e) { Plugin.L.LogWarning("VRAudio: world sound: " + e.Message); }
        }

        /// Away from the table and the title: the sources made 3D get their own settings back (some live on through fights).
        static void RestoreWorld()
        {
            if (s_made3D.Count == 0) return;
            foreach (Flat f in s_made3D.Values)
            {
                if (f.S == null) continue;
                f.S.spatialBlend = f.Blend;
                f.S.rolloffMode = f.Rolloff;
                f.S.minDistance = f.Min;
                f.S.maxDistance = f.Max;
                f.S.spread = f.Spread;
                f.S.dopplerLevel = f.Doppler;
            }
            s_made3D.Clear();
        }

        void LateUpdate()
        {
            try
            {
                Ears();
                DealerVoice();
                if (!WorldPlace) RestoreWorld();
            }
            catch (Exception e)
            {
                Plugin.L.LogError("VRAudio: " + e);
                enabled = false;
                Unmute();
                RestoreVoice();
            }
        }

        // ---- ears -----------------------------------------------------------------------------

        void Ears()
        {
            bool want = XRSettings.enabled && EarsOn && VRRig.I != null;
            if (!want)
            {
                if (m_ears != null && m_ears.enabled) { m_ears.enabled = false; Unmute(); }
                return;
            }
            if (m_ears == null)
            {
                var go = new GameObject("HoF2VR_Ears");
                DontDestroyOnLoad(go);
                m_ears = go.AddComponent<AudioListener>();
                Plugin.L.LogInfo("VRAudio: the player's ears follow the headset");
            }
            m_ears.transform.SetPositionAndRotation(VRRig.I.HeadWorldPos, VRRig.I.HeadWorldRot);
            // the game's listeners appear with cameras and scenes; it switches them on and off itself
            // (a whole-world search: on scene loads and camera changes, else every 2 s - it was every 15 frames)
            if (m_sceneLoaded || Camera.allCamerasCount != m_lastCamCount || Time.unscaledTime - m_scanTime >= 2f)
            {
                m_sceneLoaded = false;
                m_lastCamCount = Camera.allCamerasCount;
                m_scanFrame = Time.frameCount;
                m_scanTime = Time.unscaledTime;
                foreach (AudioListener l in FindObjectsOfType<AudioListener>())
                    if (l != m_ears && !m_muted.Contains(l))
                    {
                        m_muted.Add(l);
                        Plugin.L.LogInfo("VRAudio: game listener '" + VRUI.Path(l.transform) + "' (" + (l.enabled ? "on" : "off") + ") stays off; components: " +
                                         string.Join(", ", l.GetComponents<Component>().Select(c => c.GetType().Name).ToArray()));
                    }
                m_muted.RemoveAll(l => l == null);
            }
            foreach (AudioListener l in m_muted)
                if (l != null && l.enabled) l.enabled = false;
            if (!m_ears.enabled) m_ears.enabled = true;
        }

        /// Hands the game its listeners back (test toggle / VR off): the ones on active objects.
        void Unmute()
        {
            foreach (AudioListener l in m_muted)
                if (l != null && l.gameObject.activeInHierarchy)
                {
                    Camera c = l.GetComponent<Camera>();
                    if (c == null || c.enabled) l.enabled = true;
                }
            m_muted.Clear();
        }

        string DescribeEars()
        {
            var sb = new StringBuilder();
            foreach (AudioListener l in FindObjectsOfType<AudioListener>())
                sb.Append(l == m_ears ? "[ours" : "[" + VRUI.Path(l.transform)).Append(l.enabled ? " on" : " off").Append(" at ").Append(Diag.V(l.transform.position))
                  .Append(" yaw ").Append(l.transform.eulerAngles.y.ToString("0")).Append("] ");
            if (VRRig.I != null) sb.Append("head ").Append(Diag.V(VRRig.I.HeadWorldPos)).Append(" yaw ").Append(VRRig.I.HeadWorldRot.eulerAngles.y.ToString("0"));
            return sb.ToString();
        }

        // ---- the Dealer's voice -------------------------------------------------------------------

        void DealerVoice()
        {
            Voice v = Voice.Instance;
            AudioSource src = v != null ? v.Source : null;
            if (src != m_voice) { m_voice = src; m_saved = false; m_placed = false; }
            Dealer dealer = Dealer.Instance;
            bool want = XRSettings.enabled && DealerOn && src != null && dealer != null && Places.AtCaravan && !CombatVR.Active;
            Transform head = want ? DealerHead(dealer) : null;
            if (head == null) { RestoreVoice(); return; }
            if (!m_saved)
            {
                m_savedPos = src.transform.position;
                m_savedRolloff = src.rolloffMode;
                m_savedMin = src.minDistance;
                m_savedMax = src.maxDistance;
                m_savedSpread = src.spread;
                m_savedDoppler = src.dopplerLevel;
                m_savedBlend = src.spatialBlend;
                m_saved = true;
                Plugin.L.LogInfo("VRAudio: the Dealer's voice '" + VRUI.Path(src.transform) + "' (mixer group '" + (src.outputAudioMixerGroup != null ? src.outputAudioMixerGroup.name : "-") +
                                 "') was at " + Diag.V(m_savedPos) + ", blend " + m_savedBlend.ToString("0.00") + ", " + m_savedRolloff + " " + m_savedMin + ".." + m_savedMax +
                                 ", doppler " + m_savedDoppler + ", spread " + m_savedSpread + " - now 3D at his head '" + head.name + "'");
            }
            float scale = Settings.TableScale;   // the room is built larger than life
            src.transform.position = Mouth(head, dealer, scale);
            src.spatialBlend = 1f;
            src.dopplerLevel = 0f;
            src.rolloffMode = AudioRolloffMode.Logarithmic;
            src.minDistance = MinDistance * scale;
            src.maxDistance = MaxDistance * scale;
            src.spread = Spread;
            m_placed = true;
        }

        static Vector3 Mouth(Transform head, Dealer dealer, float scale)
        {
            Vector3 fwd = dealer.transform.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            return head.position + fwd * (MouthForward * scale) + Vector3.up * (MouthUp * scale);
        }

        void RestoreVoice()
        {
            if (!m_placed || m_voice == null) { m_placed = false; return; }
            m_voice.transform.position = m_savedPos;
            m_voice.rolloffMode = m_savedRolloff;
            m_voice.minDistance = m_savedMin;
            m_voice.maxDistance = m_savedMax;
            m_voice.spread = m_savedSpread;
            m_voice.dopplerLevel = m_savedDoppler;
            // spatialBlend: the game blends it itself (to 0 entering a fight, back to 1 after it)
            m_placed = false;
        }

        /// The Dealer's head bone ("Dealer Head" in the card-table scenes; any "head" bone otherwise).
        Transform DealerHead(Dealer dealer)
        {
            if (m_head != null && m_headOf == dealer) return m_head;
            if (Time.unscaledTime < m_nextHeadSearch && m_headOf == dealer) return null;
            m_nextHeadSearch = Time.unscaledTime + 2f;
            m_headOf = dealer;
            m_head = null;
            Transform best = null;
            foreach (Transform t in dealer.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                if (n == "Dealer Head") { best = t; break; }
                if (n.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("hat", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("end", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("nub", StringComparison.OrdinalIgnoreCase) < 0 && (best == null || n.Length < best.name.Length))
                    best = t;
            }
            m_head = best;
            if (best != null) Plugin.L.LogInfo("VRAudio: the Dealer's head bone is '" + VRUI.Path(best) + "' at " + Diag.V(best.position));
            else Plugin.L.LogWarning("VRAudio: no head bone under the Dealer - his voice stays as the game has it");
            return best;
        }

        // ---- test commands ------------------------------------------------------------------------

        /// voice            the Dealer voice source: settings, where it is relative to the ears
        /// voice 3d 0|1     switch the 3D placement off/on
        /// voice say        make the Dealer say an idle line (the game picks it)
        /// voice dist <min> <max> <spread>   tune (real metres, degrees)
        string VoiceCommand(string[] a)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (a.Length > 2 && a[1] == "3d") DealerOn = a[2] == "1";
            if (a.Length > 4 && a[1] == "dist") { MinDistance = float.Parse(a[2], inv); MaxDistance = float.Parse(a[3], inv); Spread = float.Parse(a[4], inv); }
            if (a.Length > 1 && a[1] == "say" && Voice.Instance != null) Voice.Instance.PostTrigger(Voice.Trigger.Idle);
            AudioSource s = Voice.Instance != null ? Voice.Instance.Source : null;
            if (s == null) return "no Voice source";
            AudioListener ears = m_ears != null && m_ears.isActiveAndEnabled ? m_ears : FindObjectsOfType<AudioListener>().FirstOrDefault(l => l.isActiveAndEnabled);
            string rel = "-";
            if (ears != null)
            {
                Vector3 p = ears.transform.InverseTransformPoint(s.transform.position);
                float az = Mathf.Atan2(p.x, p.z) * Mathf.Rad2Deg;
                rel = Diag.V(p) + " azimuth " + az.ToString("0") + " deg (" + (Mathf.Abs(az) < 15f ? "ahead" : az > 0 ? "right" : "left") + ") dist " + p.magnitude.ToString("0.00");
            }
            return string.Format(inv, "{0} at {1} playing={2} clip={3} blend={4:0.00} doppler={5} {6} {7:0.##}..{8:0.##} spread={9:0} group={10} | in ears space {11} | head bone {12} | ears {13}",
                m_placed ? "3D" : "game", Diag.V(s.transform.position), s.isPlaying, s.clip != null ? s.clip.name : "-", s.spatialBlend, s.dopplerLevel, s.rolloffMode,
                s.minDistance, s.maxDistance, s.spread, s.outputAudioMixerGroup != null ? s.outputAudioMixerGroup.name : "-", rel,
                m_head != null ? m_head.name + " " + Diag.V(m_head.position) : "-", ears == m_ears ? "ours" : ears != null ? VRUI.Path(ears.transform) : "none");
        }

        /// audio list                 every AudioListener and AudioSource -> log
        /// audio watch <seconds>      every source that plays during that time, grouped by mixer group -> log
        string AudioCommand(string[] a)
        {
            string op = a.Length > 1 ? a[1] : "list";
            if (op == "watch")
            {
                float secs = a.Length > 2 ? float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : 30f;
                StartCoroutine(Watch(secs, a.Length > 3 ? a[3] : "watch"));
                return "watching " + secs + " s";
            }
            var sb = new StringBuilder();
            foreach (AudioListener l in Resources.FindObjectsOfTypeAll<AudioListener>())
            {
                if (!l.gameObject.scene.IsValid() && l != m_ears) continue;
                sb.AppendFormat("\n  LISTENER {0} enabled={1} active={2} at {3}", l == m_ears ? "(ours)" : VRUI.Path(l.transform), l.enabled, l.gameObject.activeInHierarchy, Diag.V(l.transform.position));
            }
            int n = 0;
            foreach (AudioSource s in Resources.FindObjectsOfTypeAll<AudioSource>())
            {
                if (!s.gameObject.scene.IsValid()) continue;
                n++;
                sb.Append("\n  ").Append(Describe(s));
            }
            Plugin.L.LogInfo("audio:" + sb);
            return n + " sources (see log)";
        }

        static string Describe(AudioSource s)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0} on={1} playing={2} clip={3} blend={4:0.00} group={5} {6} {7:0.#}..{8:0.#} doppler={9:0.#} spread={10:0} loop={11} at {12}",
                VRUI.Path(s.transform), s.isActiveAndEnabled, s.isPlaying, s.clip != null ? s.clip.name : "-", s.spatialBlend,
                s.outputAudioMixerGroup != null ? s.outputAudioMixerGroup.name : "-", s.rolloffMode, s.minDistance, s.maxDistance, s.dopplerLevel, s.spread, s.loop, Diag.V(s.transform.position));
        }

        class Heard
        {
            public string Path, Clip, Group;
            public float MinBlend = 9f, MaxBlend = -1f, MinDist = 1e9f, MaxDist;
            public int Frames, Starts, LastFrame = -10, HrtfFrames;
        }

        IEnumerator Watch(float secs, string tag)
        {
            var heard = new Dictionary<string, Heard>();
            float end = Time.unscaledTime + secs;
            while (Time.unscaledTime < end)
            {
                Vector3 ear = m_ears != null && m_ears.isActiveAndEnabled ? m_ears.transform.position : (Camera.main != null ? Camera.main.transform.position : Vector3.zero);
                foreach (AudioSource s in FindObjectsOfType<AudioSource>())
                {
                    if (!s.isPlaying) continue;
                    string clip = s.clip != null ? s.clip.name : "(oneshot)";
                    string key = VRUI.Path(s.transform) + "|" + clip;
                    Heard h;
                    if (!heard.TryGetValue(key, out h))
                        heard[key] = h = new Heard { Path = VRUI.Path(s.transform), Clip = clip, Group = s.outputAudioMixerGroup != null ? s.outputAudioMixerGroup.name : "-" };
                    if (h.LastFrame < Time.frameCount - 1) h.Starts++;   // was not playing the frame before
                    h.LastFrame = Time.frameCount;
                    h.Frames++;
                    if (s.spatialize) h.HrtfFrames++;
                    h.MinBlend = Mathf.Min(h.MinBlend, s.spatialBlend);
                    h.MaxBlend = Mathf.Max(h.MaxBlend, s.spatialBlend);
                    float d = Vector3.Distance(ear, s.transform.position);
                    h.MinDist = Mathf.Min(h.MinDist, d);
                    h.MaxDist = Mathf.Max(h.MaxDist, d);
                }
                yield return null;
            }
            var sb = new StringBuilder();
            foreach (var g in heard.Values.GroupBy(h => h.Group).OrderBy(g => g.Key))
            {
                sb.Append("\n group '").Append(g.Key).Append("':");
                foreach (Heard h in g.OrderBy(h => h.MaxBlend).ThenBy(h => h.Path))
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "\n   {0}{9} blend {1:0.00}-{2:0.00} dist {3:0.0}-{4:0.0} frames {5} starts {6} clip {7} | {8}",
                        h.MaxBlend < 0.5f ? "2D" : h.MinBlend > 0.99f ? "3D" : "mix", h.MinBlend, h.MaxBlend, h.MinDist, h.MaxDist, h.Frames, h.Starts, h.Clip, h.Path,
                        h.HrtfFrames == 0 ? "" : h.HrtfFrames == h.Frames ? " HRTF" : " HRTF(part)");
            }
            Plugin.L.LogInfo("audio watch '" + tag + "' (" + secs + " s, state " + GameWatch.State + "): " + heard.Count + " sources played" + sb);
        }
    }
}

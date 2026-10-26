using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// Until You Fall's trail on the player's blade (its WeaponTrailManager; docs/uyf-parity-audit.md F15): a ribbon between the
    /// blade's base and tip over its last 0.12 s, brighter as the tip speeds from 2 to 6 m/s (nothing below 2), fading with
    /// age. Styled with `[Combat] IndicatorLook` (the coordinator, 2026-10-13 23:15): Pill cyan, Arc pale with a bright
    /// edge, Ghost white and faint, Gilded card gold with an ember edge, Ring steel, Bracket a thin line near the tip. Our
    /// own procedural mesh; `[Combat] WeaponTrail` switches it off. The game's own trails follow its attack animations,
    /// which Physical mode does not play.
    internal class WeaponTrail : MonoBehaviour
    {
        public const float Life = 0.12f, SlowTip = 2f, FastTip = 6f;
        struct Pt { public float T; public Vector3 B, Tip; public float A; }   // (a struct: no garbage per frame)
        sealed class Ribbon
        {
            public readonly List<Pt> Pts = new List<Pt>();
            public GameObject Go;
            public Mesh Mesh;
            public float LastSeen;
            public Vector3 LastTip, LastTipT;   // the tip last frame: world, and tracking space (the player's own motion)
            public bool HasLast;
        }
        readonly Dictionary<int, Ribbon> m_ribbons = new Dictionary<int, Ribbon>();
        readonly List<Vector3> m_v = new List<Vector3>();
        readonly List<Color> m_c = new List<Color>();
        readonly List<int> m_t = new List<int>();
        readonly List<int> m_gone = new List<int>();
        public static int Drawn;
        public static float PeakAlpha, LastTipSpeed;
        public static WeaponTrail Host;   // for coroutines of the combat feel (SuperMeter's activation rumble)

        void Awake() { Host = this; }

        // ---- UYF's ready super (P30): the weapon glows - a pulsing line along each blade - and a soft loop plays at it
        GameObject m_glow;
        Mesh m_glowMesh;
        AudioSource m_loop;
        public static int GlowFrames;
        public static float GlowLen;   // the glowing stretch's length (m), for `super?`
        /// The glow's half width (m: a little over half the blade's width) and how far it sits toward the eyes from the blade.
        public const float GlowHalfWidth = 0.03f, GlowLift = 0.012f;
        static readonly float[] s_glowRows = { 0f, 0.08f, 0.5f, 0.92f, 1f }, s_glowFade = { 0f, 1f, 1f, 1f, 0.3f };
        readonly Premonition.Builder m_gb = new Premonition.Builder();

        void Glow(float now)
        {
            bool on = Physical.Live && SuperMeter.Full && BladeHits.BladeCount > 0;
            if (!on)
            {
                if (m_glow != null && m_glow.activeSelf) m_glow.SetActive(false);
                if (m_loop != null && m_loop.isPlaying) m_loop.Stop();
                return;
            }
            Vector3 b, tip;
            int id;
            if (!BladeHits.BladeAt(0, out b, out tip, out id)) return;
            if (m_glow == null)
            {
                m_glow = new GameObject("HoF2VR_SuperGlow");
                m_glowMesh = new Mesh { name = "HoF2VR_SuperGlow" };
                m_glowMesh.MarkDynamic();
                m_glow.AddComponent<MeshFilter>().sharedMesh = m_glowMesh;
                var mr = m_glow.AddComponent<MeshRenderer>();
                mr.sharedMaterial = Premonition.Alpha();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                m_loop = m_glow.AddComponent<AudioSource>();
                m_loop.clip = CombatSounds.Riser;
                m_loop.loop = true;
                m_loop.spatialBlend = 1f;
                m_loop.volume = 0.12f;
                m_loop.pitch = 1.6f;
                m_loop.minDistance = 0.3f;
                m_loop.maxDistance = 4f;
                m_loop.rolloffMode = AudioRolloffMode.Linear;
            }
            if (!m_glow.activeSelf) m_glow.SetActive(true);
            if (!m_loop.isPlaying) m_loop.Play();
            // P2-9 (the user's second playtest, 2026-10-17: "Why is there a beam coming out of this sword?"): the glow is a
            // ribbon on the blade itself - its vertices at the blade's real base and tip, turned across the view at each end,
            // so both eyes see it on the blade at the blade's own depth. It was a flat line drawn in one plane at the blade's
            // middle: for a blade pointing away from the eyes it began halfway up the blade and ran past the tip, a beam
            // floating at one depth (mark 213810_15). Narrow (the blade's own width) and soft-edged: the sword glows.
            Vector3 along = tip - b;
            float len = along.magnitude;
            if (len < 0.05f || VRRig.I == null) return;
            Vector3 dir = along / len, head = VRRig.I.HeadWorldPos;
            float pulse = 0.6f + 0.4f * Mathf.Sin(now * 5f);
            Color c = CombatSettings.Look == IndicatorLooks.LookKind.Pill ? Premonition.Cyan : IndicatorLooks.GoldC;
            Color core = new Color(1f, 0.95f, 0.8f);
            // the object (it carries the hum's AudioSource) sits at the blade's middle; the vertices are relative to it
            Vector3 mid = (b + tip) * 0.5f;
            m_glow.transform.position = mid;
            m_glow.transform.rotation = Quaternion.identity;
            m_gb.Clear();
            // rows along the blade (faded in at the base, out at the tip); across: clear edge, glow, core, glow, clear edge
            float[] rows = s_glowRows, fade = s_glowFade;
            for (int i = 0; i < rows.Length; i++)
            {
                Vector3 p = b + along * rows[i];
                Vector3 view = p - head;
                float dist = view.magnitude;
                if (dist < 1e-3f) return;
                view /= dist;
                Vector3 side = Vector3.Cross(dir, view);
                if (side.sqrMagnitude < 1e-6f) side = Vector3.Cross(dir, Vector3.up);
                side.Normalize();
                p -= view * GlowLift;   // a little toward the eyes: the blade's own faces do not hide it
                p -= mid;
                float f = fade[i] * pulse;
                m_gb.V.Add(p - side * GlowHalfWidth); m_gb.C.Add(new Color(c.r, c.g, c.b, 0f));
                m_gb.V.Add(p - side * GlowHalfWidth * 0.4f); m_gb.C.Add(new Color(c.r, c.g, c.b, 0.22f * f));
                m_gb.V.Add(p); m_gb.C.Add(new Color(core.r, core.g, core.b, 0.38f * f));
                m_gb.V.Add(p + side * GlowHalfWidth * 0.4f); m_gb.C.Add(new Color(c.r, c.g, c.b, 0.22f * f));
                m_gb.V.Add(p + side * GlowHalfWidth); m_gb.C.Add(new Color(c.r, c.g, c.b, 0f));
                if (i == 0) continue;
                int r0 = (i - 1) * 5, r1 = i * 5;
                for (int k = 0; k < 4; k++)
                {
                    m_gb.T.Add(r0 + k); m_gb.T.Add(r1 + k); m_gb.T.Add(r1 + k + 1);
                    m_gb.T.Add(r0 + k); m_gb.T.Add(r1 + k + 1); m_gb.T.Add(r0 + k + 1);
                }
            }
            m_gb.To(m_glowMesh);
            GlowLen = len;
            GlowFrames++;
        }

        // ---- UYF's weapon movement loop (its VelocityPositionalSource on the sword: volume 0.2-1, pitch 0.7-1, the high-pass
        // 800 -> 400 Hz and the low-pass 800 -> 4000 Hz as the speed goes from 1 to 18 m/s over its last 18 samples)
        AudioSource m_rush;
        AudioLowPassFilter m_rushLp;
        AudioHighPassFilter m_rushHp;
        float m_rushSpeed;
        public const float RushVolume = 0.3f;
        public static float RushK, RushVol, RushPitch, RushPeak;   // RushPeak: the loudest since `vrweaponsound reset`

        void Sound(float now, float dt)
        {
            Vector3 b, tip;
            int id;
            bool on = Physical.Live && CombatSettings.WeaponSound && BladeHits.BladeCount > 0 && BladeHits.BladeAt(0, out b, out tip, out id);
            if (!on)
            {
                if (m_rush != null && m_rush.isPlaying) m_rush.Stop();
                m_rushSpeed = 0f;
                RushK = RushVol = 0f;
                return;
            }
            BladeHits.BladeAt(0, out b, out tip, out id);
            if (m_rush == null)
            {
                m_rush = CombatSounds.Source("HoF2VR_WeaponRush", CombatSounds.Rush, true, 6f);
                m_rush.minDistance = 0.5f;
                m_rushLp = m_rush.gameObject.AddComponent<AudioLowPassFilter>();
                m_rushHp = m_rush.gameObject.AddComponent<AudioHighPassFilter>();
                DontDestroyOnLoad(m_rush.gameObject);
            }
            // the tip's speed in tracking space (LastTipSpeed, this frame's), smoothed over ~18 frames
            m_rushSpeed = Mathf.Lerp(m_rushSpeed, LastTipSpeed, 1f - Mathf.Exp(-dt / 0.12f));
            float k = Mathf.InverseLerp(1f, 18f, m_rushSpeed);
            RushK = k;
            RushVol = RushVolume * Mathf.Lerp(0.2f, 1f, k);
            RushPitch = Mathf.Lerp(0.7f, 1f, k);
            m_rush.volume = RushVol;
            RushPeak = Mathf.Max(RushPeak, RushVol);
            m_rush.pitch = RushPitch;
            m_rushLp.cutoffFrequency = Mathf.Lerp(800f, 4000f, k);
            m_rushHp.cutoffFrequency = Mathf.Lerp(800f, 400f, k);
            m_rush.transform.position = (b + tip) * 0.5f;
            if (!m_rush.isPlaying) m_rush.Play();
        }

        // ---- UYF's weapon meeting the world (docs/uyf-parity-audit.md F16, item 16; its ArmamentCollisionVFX): where the
        // blade meets a wall, the floor or a prop - sparks, a clank (by the blade's speed) and a knock in the weapon hand;
        // dragged along it, a scrape (a loud, bright rush) with small sparks and ticks. UYF's blade is a physics body that
        // also stops there; ours still passes through (WeaponFeel draws it where the hand is).
        public static int WorldImpacts, WorldScrapes;
        public static string LastWorld = "-";
        static int s_solid = 0;
        static readonly bool[] s_inWorld = new bool[2];
        static readonly Vector3[] s_prevTip = new Vector3[2];
        static readonly bool[] s_hasPrev = new bool[2];
        AudioSource m_scrape;
        AudioHighPassFilter m_scrapeHp;
        float m_nextTick, m_scrapeUntil;

        /// Solid scenery: not the characters, effects, UI or the mod's limbo (ExitPortal's mask), and no collider of a moving
        /// body, an agent or a character controller (the hero, enemies, ragdolls).
        static int SolidMask()
        {
            if (s_solid != 0) return s_solid;
            int m = ~0;
            foreach (int l in new[] { 8, 13, 14, 15, 16, 17, 21, 22, 23, 26, 30, 31, Limbo.Layer }) m &= ~(1 << l);
            return s_solid = m;
        }

        static bool Solid(Collider col)
        {
            if (col == null) return false;
            Rigidbody rb = col.attachedRigidbody;
            if (rb != null && !rb.isKinematic) return false;
            return col.GetComponentInParent<UnityEngine.AI.NavMeshAgent>() == null && col.GetComponentInParent<CharacterController>() == null;
        }

        void World(float now, float dt)
        {
            bool live = Physical.Live;
            bool scraping = false;
            float scrapeSpeed = 0f;
            Vector3 scrapeAt = Vector3.zero;
            for (int i = 0; i < 2; i++)
            {
                Vector3 b, tip;
                int id;
                if (!live || i >= BladeHits.BladeCount || !BladeHits.BladeAt(i, out b, out tip, out id)) { s_inWorld[i] = false; s_hasPrev[i] = false; continue; }
                Ribbon r;
                float speed = m_ribbons.TryGetValue(id, out r) && r.HasLast ? m_tipSpeed[i] : 0f;
                RaycastHit hit;
                bool inside = Physics.Linecast(b, tip, out hit, SolidMask(), QueryTriggerInteraction.Ignore) && Solid(hit.collider);
                // a fast tip through a thin surface between two frames (not across a jump of the rig or the hand)
                if (!inside && s_hasPrev[i] && speed < 30f && (tip - s_prevTip[i]).magnitude < 0.6f)
                    inside = Physics.Linecast(s_prevTip[i], tip, out hit, SolidMask(), QueryTriggerInteraction.Ignore) && Solid(hit.collider);
                s_prevTip[i] = tip;
                s_hasPrev[i] = true;
                bool left = i == 0 ? Settings.LeftHanded : !Settings.LeftHanded;
                if (!inside) { s_inWorld[i] = false; continue; }
                if (!s_inWorld[i])
                {
                    s_inWorld[i] = true;
                    if (speed >= 1f)
                    {
                        WorldImpacts++;
                        float k = Mathf.InverseLerp(1f, 8f, speed);
                        CombatSounds.Layer(CombatSounds.Clank, hit.point, Mathf.Lerp(0.25f, 0.8f, k), 1.25f);
                        CombatFeel.Burst(hit.point, new Color(1f, 0.78f, 0.4f), Mathf.Lerp(0.06f, 0.16f, k), 0.18f);
                        Haptics.Pulse(left, Mathf.Lerp(0.3f, 0.9f, k), 0.04f);
                        LastWorld = "impact " + (hit.collider != null ? hit.collider.name : "-") + " at " + speed.ToString("0.0") + " m/s";
                    }
                }
                else if (speed >= 1.5f)
                {
                    scraping = true;
                    scrapeSpeed = Mathf.Max(scrapeSpeed, speed);
                    scrapeAt = hit.point;
                    if (now >= m_nextTick)
                    {
                        m_nextTick = now + 0.07f;
                        WorldScrapes++;
                        CombatFeel.Burst(hit.point, new Color(1f, 0.7f, 0.35f), 0.04f, 0.12f);
                        Haptics.Pulse(left, 0.25f, 0.02f);
                        LastWorld = "scrape " + (hit.collider != null ? hit.collider.name : "-") + " at " + speed.ToString("0.0") + " m/s";
                    }
                }
            }
            // the scrape's sound: the weapon's rush, high-passed and bright, by the drag's speed
            if (scraping) m_scrapeUntil = now + 0.08f;
            if (now < m_scrapeUntil)
            {
                if (m_scrape == null)
                {
                    m_scrape = CombatSounds.Source("HoF2VR_WeaponScrape", CombatSounds.Rush, true, 6f);
                    m_scrape.minDistance = 0.5f;
                    m_scrapeHp = m_scrape.gameObject.AddComponent<AudioHighPassFilter>();
                    m_scrapeHp.cutoffFrequency = 2500f;
                    DontDestroyOnLoad(m_scrape.gameObject);
                }
                if (scraping)
                {
                    m_scrape.transform.position = scrapeAt;
                    m_scrape.volume = Mathf.Lerp(0.15f, 0.55f, Mathf.InverseLerp(1.5f, 6f, scrapeSpeed));
                    m_scrape.pitch = Mathf.Lerp(1.2f, 1.7f, Mathf.InverseLerp(1.5f, 6f, scrapeSpeed));
                }
                if (!m_scrape.isPlaying) m_scrape.Play();
            }
            else if (m_scrape != null && m_scrape.isPlaying) m_scrape.Stop();
        }

        // each blade's tip speed (tracking space) this frame, for World
        readonly float[] m_tipSpeed = new float[2];

        void LateUpdate()
        {
            Glow(Time.unscaledTime);
            Sound(Time.unscaledTime, Time.unscaledDeltaTime);
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            // the tips' speeds are wanted with the trail off too (the weapon's sound, the world's touch): the ribbons are kept
            // for them, only not drawn
            bool live = Physical.Live, on = live && CombatSettings.WeaponTrail;
            if (live)
            {
                for (int i = 0; i < BladeHits.BladeCount; i++)
                {
                    Vector3 b, tip;
                    int id;
                    if (!BladeHits.BladeAt(i, out b, out tip, out id)) continue;
                    Ribbon r;
                    if (!m_ribbons.TryGetValue(id, out r)) m_ribbons[id] = r = new Ribbon();
                    // the swing's speed in tracking space: walking, a dash, a knockback or a snap turn move the rig, not the
                    // blade (MR2b: a hand placed in one frame drew a full trail; a snap turn or a dash would streak one across)
                    Vector3 tipT = VRRig.I != null ? VRRig.I.FromWorld(tip) : tip;
                    float jumpT = r.HasLast ? (tipT - r.LastTipT).magnitude : 0f, jumpW = r.HasLast ? (tip - r.LastTip).magnitude : 0f;
                    float speed = r.HasLast && dt > 1e-4f ? jumpT / dt : 0f;
                    // the rig jumped (snap turn, dash, knockback, recenter) or the hand did (tracking lost, a test's placement):
                    // the ribbon starts again rather than stretch across it
                    if (r.HasLast && (jumpW > jumpT + 0.25f || speed > 30f)) { r.Pts.Clear(); speed = 0f; }
                    r.LastTip = tip;
                    r.LastTipT = tipT;
                    r.HasLast = true;
                    r.LastSeen = now;
                    LastTipSpeed = speed;
                    if (i < 2) m_tipSpeed[i] = speed;
                    // UYF: brighter with the tip's speed from 2 to 6 m/s
                    float a = Mathf.Clamp01((speed - SlowTip) / (FastTip - SlowTip));
                    if (on) r.Pts.Add(new Pt { T = now, B = b, Tip = tip, A = a });
                }
            }
            m_gone.Clear();
            foreach (var kv in m_ribbons)
            {
                Ribbon r = kv.Value;
                while (r.Pts.Count > 0 && now - r.Pts[0].T > Life) r.Pts.RemoveAt(0);
                if (!live || now - r.LastSeen > 0.5f)
                {
                    if (r.Go != null) Destroy(r.Go);
                    m_gone.Add(kv.Key);
                    continue;
                }
                if (!on) { r.Pts.Clear(); if (r.Go != null && r.Go.activeSelf) r.Go.SetActive(false); continue; }
                Draw(r, now);
            }
            foreach (int id in m_gone) m_ribbons.Remove(id);
            World(now, dt);
        }

        void Draw(Ribbon r, float now)
        {
            float peak = 0f;
            foreach (Pt p in r.Pts) peak = Mathf.Max(peak, p.A);
            if (r.Go == null)
            {
                r.Go = new GameObject("HoF2VR_WeaponTrail");
                r.Mesh = new Mesh { name = "HoF2VR_WeaponTrail" };
                r.Mesh.MarkDynamic();
                r.Go.AddComponent<MeshFilter>().sharedMesh = r.Mesh;
                var mr = r.Go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = Premonition.Alpha();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            if (r.Pts.Count < 2 || peak <= 0.01f) { if (r.Go.activeSelf) r.Go.SetActive(false); return; }
            if (!r.Go.activeSelf) r.Go.SetActive(true);
            Color c, edge;
            float from = 0f;   // where along the blade the ribbon starts (0 = the base)
            float maxA = 0.55f;
            switch (CombatSettings.Look)
            {
                case IndicatorLooks.LookKind.Pill: c = Premonition.Cyan; edge = new Color(0.6f, 0.95f, 1f); break;
                case IndicatorLooks.LookKind.Arc: c = IndicatorLooks.Pale; edge = Color.white; maxA = 0.5f; break;
                case IndicatorLooks.LookKind.Ghost: c = Color.white; edge = Color.white; maxA = 0.28f; break;
                case IndicatorLooks.LookKind.Ring: c = IndicatorLooks.Steel; edge = Color.white; break;
                case IndicatorLooks.LookKind.Bracket: c = Color.white; edge = Color.white; from = 0.7f; maxA = 0.45f; break;
                default: c = IndicatorLooks.GoldC; edge = IndicatorLooks.Ember; maxA = 0.6f; break;   // Gilded
            }
            m_v.Clear(); m_c.Clear(); m_t.Clear();
            int n = r.Pts.Count;
            for (int i = 0; i < n; i++)
            {
                Pt p = r.Pts[i];
                float age = Mathf.Clamp01((now - p.T) / Life);
                float a = p.A * (1f - age) * maxA;
                Vector3 b = Vector3.Lerp(p.B, p.Tip, from);
                m_v.Add(b); m_c.Add(new Color(c.r, c.g, c.b, a * 0.35f));
                m_v.Add(p.Tip); m_c.Add(new Color(edge.r, edge.g, edge.b, a));
                if (i > 0)
                {
                    int k = (i - 1) * 2;
                    m_t.Add(k); m_t.Add(k + 1); m_t.Add(k + 2);
                    m_t.Add(k + 1); m_t.Add(k + 3); m_t.Add(k + 2);
                    // both faces: the ribbon is seen from either side
                    m_t.Add(k); m_t.Add(k + 2); m_t.Add(k + 1);
                    m_t.Add(k + 1); m_t.Add(k + 2); m_t.Add(k + 3);
                }
            }
            r.Go.transform.position = Vector3.zero;
            r.Go.transform.rotation = Quaternion.identity;
            r.Mesh.Clear();
            r.Mesh.SetVertices(m_v);
            r.Mesh.SetColors(m_c);
            r.Mesh.SetTriangles(m_t, 0);
            r.Mesh.RecalculateBounds();
            Drawn++;
            PeakAlpha = Mathf.Max(PeakAlpha, peak);
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// `[Combat] EnemyStatus = Rings` (the user's pick of 2026-10-18 ~22:00, mockup captures/hud-options-2026-10-18/enemy_ring.png):
    /// two rings on the ground at the enemy's feet. The outer, blue one is its guard: it empties with every hit (the chipped part
    /// glows white, then burns away) and fills again as the guard refills; when the guard breaks it is empty and flashes. The
    /// inner, gold one is its health; it turns deep orange when the health is low (under 30 %; not red: red means only "can't
    /// block", the user's ruling of playtest 3). Both arcs shrink toward the point that
    /// faces the player (the part in front of the enemy's legs, never hidden by them).
    /// The rings lie on the ground: its height is sampled round them (12 rays down, again when the enemy has moved or every
    /// 0.15 s) and they sit Lift above it, so slopes and steps keep them flat on the ground. A dark band under them and a soft
    /// additive glow keep them readable on snow, grass, cobbles and in night arenas. Sized by the enemy's body (its collider).
    internal sealed class EnemyRings
    {
        public static float Width = 0.075f, Gap = 0.035f, Lift = 0.035f, MinR = 0.5f;
        public static readonly Color Guard = new Color(0.12f, 0.45f, 1f), GuardCore = new Color(0.45f, 0.72f, 1f), Chip = new Color(1f, 1f, 1f),
                                     Gold = new Color(1f, 0.76f, 0.22f), Low = new Color(0.93f, 0.3f, 0.02f), Dark = new Color(0.015f, 0.015f, 0.025f),
                                     Track = new Color(0.16f, 0.17f, 0.2f);
        const int Seg = 64, Samples = 12;

        readonly GameObject m_go;
        readonly Mesh m_body, m_glow;
        readonly GameObject m_glowGo;
        readonly float[] m_ground = new float[Samples];
        Vector3 m_sampledAt = new Vector3(9999f, 0f, 0f);
        float m_nextSample;
        readonly List<Vector3> m_v = new List<Vector3>(), m_gv = new List<Vector3>();
        readonly List<Color> m_c = new List<Color>(), m_gc = new List<Color>();
        readonly List<int> m_t = new List<int>(), m_gt = new List<int>();
        public float ShownGuard = -1f, ShownHealth = -1f, Radius;
        public bool Flashing, LowHealth;
        public int GroundHits;
        static int s_mask = -1;
        static int Mask => s_mask >= 0 ? s_mask : (s_mask = 1 | LayerBit("Ground"));
        static int LayerBit(string n) { int l = LayerMask.NameToLayer(n); return l >= 0 ? 1 << l : 0; }

        public EnemyRings()
        {
            m_go = new GameObject("HoF2VR_EnemyRings");
            m_body = Part(m_go, Premonition.Alpha(), "HoF2VR_EnemyRings");
            m_glowGo = new GameObject("glow");
            m_glowGo.transform.SetParent(m_go.transform, false);
            m_glow = Part(m_glowGo, Premonition.Additive(), "HoF2VR_EnemyRingsGlow");
        }

        static Mesh Part(GameObject go, Material mat, string name)
        {
            var mesh = new Mesh { name = name };
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return mesh;
        }

        public bool Shown => m_go != null && m_go.activeSelf;

        public void Hide() { if (m_go != null && m_go.activeSelf) m_go.SetActive(false); }

        public void Destroy()
        {
            if (m_go != null) Object.Destroy(m_go);
            Object.Destroy(m_body);
            Object.Destroy(m_glow);
        }

        void Sample(Vector3 c, float r, float fallback)
        {
            GroundHits = 0;
            for (int i = 0; i < Samples; i++)
            {
                float a = i * Mathf.PI * 2f / Samples;
                Vector3 p = c + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * r;
                RaycastHit hit;
                if (Physics.Raycast(p + Vector3.up * 1.0f, Vector3.down, out hit, 2.2f, Mask, QueryTriggerInteraction.Ignore) && Mathf.Abs(hit.point.y - fallback) < 0.9f)
                {
                    m_ground[i] = hit.point.y;
                    GroundHits++;
                }
                else m_ground[i] = fallback;
            }
            m_sampledAt = c;
        }

        float GroundAt(float ang)
        {
            float u = Mathf.Repeat(ang / (Mathf.PI * 2f), 1f) * Samples;
            int i = Mathf.FloorToInt(u) % Samples;
            return Mathf.Lerp(m_ground[i], m_ground[(i + 1) % Samples], u - Mathf.Floor(u));
        }

        /// guard / glow: shares (glow >= guard: the part still glowing from the last hit); broken: the guard's break (empty,
        /// flashing); hasGuard false (Classic mode): the health ring alone.
        public void Draw(Vector3 feet, float bodyR, Vector3 eye, float alpha, bool hasGuard, float guard, float glow, Poise.Phase ph, float since, float health)
        {
            if (alpha <= 0.01f) { Hide(); return; }
            if (!m_go.activeSelf) m_go.SetActive(true);
            float now = Time.unscaledTime;
            float R = Mathf.Max(MinR, bodyR + 0.22f);
            Radius = R;
            float rOut = R, rIn = R - Width - Gap;
            if ((Flat(feet - m_sampledAt)).sqrMagnitude > 0.03f * 0.03f || now >= m_nextSample)
            {
                Sample(feet, R, feet.y);
                m_nextSample = now + 0.15f;
            }
            // the arcs open from the far side: their middle faces the player
            Vector3 to = Flat(eye - feet);
            float face = to.sqrMagnitude > 1e-4f ? Mathf.Atan2(to.x, to.z) : 0f;
            m_v.Clear(); m_c.Clear(); m_t.Clear(); m_gv.Clear(); m_gc.Clear(); m_gt.Clear();
            ShownGuard = hasGuard ? guard : -1f;
            ShownHealth = health;
            Flashing = hasGuard && ph == Poise.Phase.Broken;
            float pulse = 0.5f + 0.5f * Mathf.Sin(now * 15f);
            // the dark band under both rings
            Band(m_v, m_c, m_t, feet, face, rIn - Width * 0.5f - 0.025f, rOut + Width * 0.5f + 0.025f, 1f, A(Dark, 0.5f * alpha), 0f);
            if (hasGuard)
            {
                Band(m_v, m_c, m_t, feet, face, rOut - Width * 0.5f, rOut + Width * 0.5f, 1f, A(Track, 0.55f * alpha), 0.003f);
                if (Flashing)
                {
                    // broken: empty, its edges flashing white-blue
                    Color fl = A(Color.Lerp(Guard, Chip, pulse), (0.45f + 0.55f * pulse) * alpha);
                    Band(m_v, m_c, m_t, feet, face, rOut + Width * 0.5f - 0.014f, rOut + Width * 0.5f, 1f, fl, 0.006f);
                    Band(m_v, m_c, m_t, feet, face, rOut - Width * 0.5f, rOut - Width * 0.5f + 0.014f, 1f, fl, 0.006f);
                    Band(m_gv, m_gc, m_gt, feet, face, rOut - Width, rOut + Width, 1f, A(Guard, 0.35f * pulse * alpha), 0.008f);
                }
                else
                {
                    bool refill = ph == Poise.Phase.Recover;
                    if (glow > guard + 0.002f) Band(m_v, m_c, m_t, feet, face, rOut - Width * 0.5f, rOut + Width * 0.5f, glow, A(Chip, 0.95f * alpha), 0.005f);
                    if (guard > 0.002f)
                    {
                        Band(m_v, m_c, m_t, feet, face, rOut - Width * 0.5f, rOut + Width * 0.5f, guard, A(Guard, alpha), 0.006f);
                        Band(m_v, m_c, m_t, feet, face, rOut - Width * 0.14f, rOut + Width * 0.14f, guard, A(GuardCore, (refill ? 0.5f + 0.4f * pulse : 0.6f) * alpha), 0.008f);
                        Band(m_gv, m_gc, m_gt, feet, face, rOut - Width * 1.1f, rOut + Width * 1.1f, guard, A(Guard, 0.22f * alpha), 0.009f);
                    }
                }
            }
            // health: gold, deep orange when low (never red)
            LowHealth = health < 0.3f;
            Color hc = health < 0.3f ? Low : health < 0.42f ? Color.Lerp(Low, Gold, (health - 0.3f) / 0.12f) : Gold;
            Band(m_v, m_c, m_t, feet, face, rIn - Width * 0.5f, rIn + Width * 0.5f, 1f, A(Track, 0.55f * alpha), 0.003f);
            if (health > 0.002f)
            {
                Band(m_v, m_c, m_t, feet, face, rIn - Width * 0.5f, rIn + Width * 0.5f, health, A(hc, alpha), 0.006f);
                Band(m_gv, m_gc, m_gt, feet, face, rIn - Width * 1.0f, rIn + Width * 1.0f, health, A(hc, (LowHealth ? 0.18f + 0.12f * pulse : 0.2f) * alpha), 0.009f);
            }
            Put(m_body, m_v, m_c, m_t);
            Put(m_glow, m_gv, m_gc, m_gt);
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
        static Color A(Color c, float a) { c.a *= a; return c; }

        static void Put(Mesh m, List<Vector3> v, List<Color> c, List<int> t)
        {
            m.Clear();
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
        }

        /// An arc of the band between radii r0 and r1, `share` of the full circle, centred on the side facing the player, on
        /// the ground (+ Lift + up).
        void Band(List<Vector3> v, List<Color> c, List<int> t, Vector3 feet, float face, float r0, float r1, float share, Color col, float up)
        {
            share = Mathf.Clamp01(share);
            if (share <= 0.002f) return;
            int n = Mathf.Max(2, Mathf.CeilToInt(Seg * share));
            float half = Mathf.PI * share, b = v.Count;
            for (int i = 0; i <= n; i++)
            {
                float a = face - half + 2f * half * i / n;
                Vector3 d = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                float y = GroundAt(a) + Lift + up;
                Vector3 p0 = feet + d * r0, p1 = feet + d * r1;
                p0.y = p1.y = y;
                v.Add(p0); v.Add(p1);
                c.Add(col); c.Add(col);
            }
            int bi = (int)b;
            for (int i = 0; i < n; i++)
            {
                int q = bi + i * 2;
                t.Add(q); t.Add(q + 1); t.Add(q + 2);
                t.Add(q + 1); t.Add(q + 3); t.Add(q + 2);
            }
        }
    }
}

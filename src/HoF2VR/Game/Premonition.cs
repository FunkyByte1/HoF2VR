using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// The look of Until You Fall's "premonitions" in our own art (docs/uyf-reference.md §3.2; nothing of UYF's is used):
    ///  * the block bar: a glowing pill 0.6 m long across the blow, its fill growing from the middle to both ends over the
    ///    lead (UYF: fill = elapsed / duration), so the timing reads at a glance. It scales in from 25 %, turns white with
    ///    a lock-in click while the guard is valid, flashes green with a shock ring on a block and red on a hit, and dims
    ///    while the head is out of the blow's reach;
    ///  * the dodge arrow: three chevrons pointing the way to move the head (duck = down, lean = sideways); they light up
    ///    one by one over the lead (the third at the impact), march and brighten while the head is already far enough
    ///    along ("succeeding"), and streak away on a dodge.
    /// Meshes are built here (vertex colours, Unity's built-in Hidden/Internal-Colored with additive or alpha blending),
    /// rebuilt in place each frame where they animate.
    internal static class Premonition
    {
        public static readonly Color Cyan = new Color(0.05f, 0.78f, 1f), Valid = new Color(1f, 1f, 1f), Success = new Color(0.25f, 1f, 0.45f),
                                     Fail = new Color(1f, 0.16f, 0.1f), Dodge = new Color(1f, 0.42f, 0.08f), DodgeOk = new Color(1f, 0.93f, 0.55f),
                                     Gold = new Color(1f, 0.78f, 0.12f);
        /// The user's playtest 3 (2026-10-18): the bar turns green while the guard is lined up enough to block (it was white:
        /// "too subtle"); a wrong-way crit cut flickers grey, not red (red is kept for nothing: a blow that lands fades grey).
        public static readonly Color Lined = new Color(0.2f, 1f, 0.35f), Miss = new Color(0.55f, 0.6f, 0.65f), Pop = new Color(0.92f, 1f, 0.94f);

        static Material s_add, s_alpha;

        public static Material Additive()
        {
            if (s_add != null) return s_add;
            s_add = new Material(Shader.Find("Hidden/Internal-Colored")) { renderQueue = 3160 };
            s_add.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            s_add.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            s_add.SetInt("_ZWrite", 0);
            s_add.SetInt("_Cull", 0);
            s_add.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            return s_add;
        }

        static Material s_alphaOver, s_addOver;

        /// Alpha / Additive drawn over everything (ZTest Always; the shield's frame ring: a shield held in it must not hide it).
        public static Material AlphaOver() => s_alphaOver != null ? s_alphaOver : (s_alphaOver = Over(Alpha(), 3175));
        public static Material AdditiveOver() => s_addOver != null ? s_addOver : (s_addOver = Over(Additive(), 3180));

        static Material Over(Material from, int queue)
        {
            var m = new Material(from) { renderQueue = queue };
            m.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            return m;
        }

        public static Material Alpha()
        {
            if (s_alpha != null) return s_alpha;
            s_alpha = new Material(Shader.Find("Hidden/Internal-Colored")) { renderQueue = 3155 };
            s_alpha.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            s_alpha.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            s_alpha.SetInt("_ZWrite", 0);
            s_alpha.SetInt("_Cull", 0);
            s_alpha.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            return s_alpha;
        }

        /// A mesh being filled: vertices, colours, triangles (reused between frames).
        public sealed class Builder
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Color> C = new List<Color>();
            public readonly List<int> T = new List<int>();
            public void Clear() { V.Clear(); C.Clear(); T.Clear(); }
            /// A share of opacity applied to everything this builder writes next (Guard: the bar fades while the shield, not the
            /// weapon, is on its circle); 1 = as drawn. Set by the caller around a marker's Draw.
            public static float Fade = 1f;
            static readonly float[] s_four = new float[4];   // scratch (main thread only)
            static readonly Color[] s_fourC = new Color[4];

            public void To(Mesh m)
            {
                m.Clear();
                m.SetVertices(V);
                if (Fade < 0.999f) for (int i = 0; i < C.Count; i++) { Color c = C[i]; c.a *= Fade; C[i] = c; }
                m.SetColors(C);
                m.SetTriangles(T, 0);
                m.RecalculateBounds();
            }

            /// A ring between the outlines of a pill (a segment of half length `h` on x, rounded) at distances d0 and d1
            /// from its centre line, coloured c0 and c1 (d0 = 0: the solid inside).
            public void Pill(float h, float d0, Color c0, float d1, Color c1, float z = 0f, int cap = 14)
            {
                int n = (cap + 1) * 2, b = V.Count;
                for (int ring = 0; ring < 2; ring++)
                {
                    float d = ring == 0 ? d0 : d1;
                    Color c = ring == 0 ? c0 : c1;
                    for (int s = 0; s < 2; s++)
                        for (int i = 0; i <= cap; i++)
                        {
                            float a = (s == 0 ? -90f : 90f) + 180f * i / cap;
                            float cx = s == 0 ? h : -h;
                            V.Add(new Vector3(cx + d * Mathf.Cos(a * Mathf.Deg2Rad), d * Mathf.Sin(a * Mathf.Deg2Rad), z));
                            C.Add(c);
                        }
                }
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    T.Add(b + i); T.Add(b + n + i); T.Add(b + n + j);
                    T.Add(b + i); T.Add(b + n + j); T.Add(b + j);
                }
            }

            /// A lens (a slash's body) along x: half length `h`, half width `w` at the middle tapering to points, a solid core
            /// coloured `c` and a feather of `soft` fading to clear.
            public void Lens(float h, float w, Color c, float soft, float z = 0f, int n = 18)
            {
                Color clear = new Color(c.r, c.g, c.b, 0f);
                int b = V.Count;
                for (int i = 0; i <= n; i++)
                {
                    float x = -h + 2f * h * i / n, k = x / h;
                    float hw = w * Mathf.Sqrt(Mathf.Max(0f, 1f - k * k));
                    V.Add(new Vector3(x, -hw - soft, z)); C.Add(clear);
                    V.Add(new Vector3(x, -hw, z)); C.Add(c);
                    V.Add(new Vector3(x, hw, z)); C.Add(c);
                    V.Add(new Vector3(x, hw + soft, z)); C.Add(clear);
                }
                for (int i = 0; i < n; i++)
                    for (int k = 0; k < 3; k++)
                    {
                        int a0 = b + i * 4 + k, a1 = a0 + 4;
                        T.Add(a0); T.Add(a1); T.Add(a1 + 1);
                        T.Add(a0); T.Add(a1 + 1); T.Add(a0 + 1);
                    }
            }

            /// An arc of a ring (radius r, width w, soft edges) from `from` degrees over `sweep` degrees (0 = +y, clockwise
            /// as seen from the eyes' side).
            public void Arc(float r, float w, float from, float sweep, Color c, float soft, float z = 0f)
            {
                if (sweep <= 0.5f) return;
                Color clear = new Color(c.r, c.g, c.b, 0f);
                int n = Mathf.Clamp(Mathf.CeilToInt(sweep / 6f), 2, 64), b = V.Count;
                float[] rr = s_four;   // (no array a call: the fight HUD and the markers draw every frame - the HUD audit, item 2)
                rr[0] = r - w * 0.5f - soft; rr[1] = r - w * 0.5f; rr[2] = r + w * 0.5f; rr[3] = r + w * 0.5f + soft;
                for (int i = 0; i <= n; i++)
                {
                    float a = (from + sweep * i / n) * Mathf.Deg2Rad;
                    Vector2 d = new Vector2(Mathf.Sin(a), Mathf.Cos(a));
                    for (int k = 0; k < 4; k++) { V.Add(new Vector3(d.x * rr[k], d.y * rr[k], z)); C.Add(k == 0 || k == 3 ? clear : c); }
                }
                for (int i = 0; i < n; i++)
                    for (int k = 0; k < 3; k++)
                    {
                        int a0 = b + i * 4 + k, a1 = a0 + 4;
                        T.Add(a0); T.Add(a1); T.Add(a1 + 1);
                        T.Add(a0); T.Add(a1 + 1); T.Add(a0 + 1);
                    }
            }

            /// A thick stroke from a to b (width w) with soft sides: a solid core and a feather of `soft` either side.
            public void Stroke(Vector2 a, Vector2 b, float w, Color c, float soft, float z = 0f)
            {
                Vector2 d = (b - a);
                if (d.sqrMagnitude < 1e-8f) return;
                Vector2 n = new Vector2(-d.y, d.x).normalized;
                Color clear = new Color(c.r, c.g, c.b, 0f);
                float[] off = s_four;
                off[0] = -w * 0.5f - soft; off[1] = -w * 0.5f; off[2] = w * 0.5f; off[3] = w * 0.5f + soft;
                Color[] col = s_fourC;
                col[0] = clear; col[1] = c; col[2] = c; col[3] = clear;
                int bse = V.Count;
                for (int k = 0; k < 4; k++)
                {
                    V.Add(new Vector3(a.x + n.x * off[k], a.y + n.y * off[k], z)); C.Add(col[k]);
                    V.Add(new Vector3(b.x + n.x * off[k], b.y + n.y * off[k], z)); C.Add(col[k]);
                }
                for (int k = 0; k < 3; k++)
                {
                    int i0 = bse + k * 2;
                    T.Add(i0); T.Add(i0 + 1); T.Add(i0 + 3);
                    T.Add(i0); T.Add(i0 + 3); T.Add(i0 + 2);
                }
            }
        }

        static GameObject Part(Transform parent, string name, Material m, out Mesh mesh)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            mesh = new Mesh();
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        static float EaseOutBack(float x) { const float c1 = 1.70158f, c3 = c1 + 1f; x -= 1f; return 1f + c3 * x * x * x + c1 * x * x; }
        static Color A(Color c, float a) => new Color(c.r, c.g, c.b, c.a * a);
        static Color Mix(Color a, Color b, float t) => Color.Lerp(a, b, Mathf.Clamp01(t));

        // ======================================================================================== the block bar

        /// One block bar. Place it with `Root` (local x = the bar's axis, local z = toward the eyes' side it faces), then
        /// call Draw every frame.
        public sealed class Bar
        {
            public const float Length = 0.6f, Radius = 0.021f;
            public readonly Transform Root;
            readonly Mesh m_glow, m_body, m_shock;
            readonly Builder m_b = new Builder();
            public bool Dead => Root == null;

            public Bar(string name = "HoF2VR_BlockBar")
            {
                Root = new GameObject(name).transform;
                Part(Root, "glow", Additive(), out m_glow);
                Part(Root, "body", Alpha(), out m_body);
                Part(Root, "shock", Additive(), out m_shock);
            }

            public void Destroy()
            {
                if (Root != null) UnityEngine.Object.Destroy(Root.gameObject);
                UnityEngine.Object.Destroy(m_glow);
                UnityEngine.Object.Destroy(m_body);
                UnityEngine.Object.Destroy(m_shock);
            }

            /// age: seconds since it appeared; fill: 0..1 over the lead; valid: the guard is on it now; inRange: the head is in
            /// the blow's reach; result: null while live, else "ok" / "fail" with `resultAge` seconds since.
            public void Draw(float age, float fill, bool valid, bool inRange, string result, float resultAge, Color baseColour)
            {
                float h = Length * 0.5f - Radius;
                // the show, UYF's (its BlockPremonitionRendererAsset): scale from 25 % to full, linearly over 1 s; alpha up over
                // the first 0.3 s
                float show = Mathf.Clamp01(age / 1f);
                float scale = Mathf.Lerp(0.25f, 1f, show);
                float alpha = Mathf.Clamp01(age / 0.3f);
                Color fillC = valid ? Lined : baseColour, rimC = valid ? Lined : baseColour, glowC = valid ? Lined : baseColour;
                float glowA = valid ? 0.95f : 0.55f + 0.25f * fill, shockT = -1f;
                if (result == "fade")
                {
                    // the blow missed (out of its reach): the bar just fades, grey
                    float k = Mathf.Clamp01(resultAge / 0.35f);
                    fillC = rimC = glowC = Mix(baseColour, new Color(0.55f, 0.6f, 0.65f), 0.7f);
                    alpha = (1f - k) * 0.6f;
                    glowA *= 0.3f;
                    valid = false;
                }
                else if (result != null)
                {
                    bool ok = result == "ok";
                    Color rc = ok ? Success : Fail;
                    float k = Mathf.Clamp01(resultAge / 0.45f);
                    fillC = rimC = glowC = Mix(Pop, rc, Mathf.Clamp01(resultAge / 0.12f));
                    fill = 1f;
                    glowA = 1.2f * (1f - k);
                    alpha = 1f - k * k;
                    scale = ok ? 1f + 0.25f * Mathf.Sqrt(k) : 1f - 0.25f * k;
                    shockT = k;
                    valid = false;
                }
                else if (!inRange)
                {
                    // out of the blow's reach: dimmed and greyed
                    fillC = Mix(fillC, new Color(0.55f, 0.6f, 0.65f), 0.6f);
                    rimC = Mix(rimC, new Color(0.55f, 0.6f, 0.65f), 0.6f);
                    glowA *= 0.25f;
                    alpha *= 0.4f;
                }
                Root.localScale = Vector3.one * scale * (valid ? 1.06f : 1f);
                float pulse = valid ? 0.85f + 0.15f * Mathf.Sin(Time.unscaledTime * 30f) : 1f;
                // glow: a soft halo around the pill, brighter as the fill grows and while valid
                m_b.Clear();
                m_b.Pill(h, Radius, A(glowC, alpha * glowA * pulse), Radius + 0.022f, A(glowC, alpha * glowA * 0.35f * pulse), 0.002f);
                m_b.Pill(h, Radius + 0.022f, A(glowC, alpha * glowA * 0.35f * pulse), Radius + 0.075f, A(glowC, 0f), 0.002f);
                m_b.To(m_glow);
                // body: a dark translucent track, the fill growing from the middle, a crisp rim
                m_b.Clear();
                m_b.Pill(h, 0f, A(baseColour * 0.18f, alpha * 0.55f), Radius - 0.003f, A(baseColour * 0.25f, alpha * 0.6f), 0f);
                float fh = Mathf.Max(0f, h * Mathf.Clamp01(fill)), fr = (Radius - 0.006f) * Mathf.Lerp(0.55f, 1f, Mathf.Clamp01(fill * 3f));
                m_b.Pill(fh, 0f, A(Color.Lerp(fillC, Color.white, 0.35f), alpha), fr, A(fillC, alpha), -0.001f);
                m_b.Pill(h, Radius - 0.0035f, A(rimC, alpha), Radius, A(rimC, alpha * 0.9f), -0.0015f);
                m_b.Pill(h, Radius, A(rimC, alpha * 0.9f), Radius + 0.0025f, A(rimC, 0f), -0.0015f);
                m_b.To(m_body);
                // the shock ring of a result: a thin outline flying outward
                m_b.Clear();
                if (shockT >= 0f)
                {
                    float d = Radius + 0.12f * Mathf.Sqrt(shockT);
                    Color sc = A(glowC, (1f - shockT) * 0.9f);
                    m_b.Pill(h * (1f + 0.15f * shockT), d - 0.006f, A(sc, 0f), d, sc, -0.002f);
                    m_b.Pill(h * (1f + 0.15f * shockT), d, sc, d + 0.01f, A(sc, 0f), -0.002f);
                }
                m_b.To(m_shock);
            }
        }

        // ======================================================================================== the dodge arrow

        /// One dodge arrow: three chevrons pointing along the root's local +y. Place it with `Root`.
        public sealed class Arrow
        {
            public readonly Transform Root;
            readonly Mesh m_glow, m_body;
            readonly Builder m_b = new Builder(), m_g = new Builder();

            public Arrow(string name = "HoF2VR_DodgeArrow")
            {
                Root = new GameObject(name).transform;
                Part(Root, "glow", Additive(), out m_glow);
                Part(Root, "body", Alpha(), out m_body);
            }

            public void Destroy()
            {
                if (Root != null) UnityEngine.Object.Destroy(Root.gameObject);
                UnityEngine.Object.Destroy(m_glow);
                UnityEngine.Object.Destroy(m_body);
            }

            /// fill 0..1 over the lead; progress 0..1+ = how far along the arrow the head already is (1 = enough);
            /// result null / "ok" / "fail".
            public void Draw(float age, float fill, float progress, string result, float resultAge)
            {
                float show = Mathf.Clamp01(age / 0.2f);
                float alpha = show, scale = Mathf.Lerp(0.4f, 1f, EaseOutBack(show));
                bool ok = progress >= 1f && result == null;
                Color c = ok ? DodgeOk : Mix(Dodge, DodgeOk, progress * 0.5f);
                float march = 0f, spread = 1f;
                if (result != null)
                {
                    float k = Mathf.Clamp01(resultAge / (result == "ok" ? 0.6f : 0.5f));
                    c = result == "ok" ? Mix(Valid, Success, resultAge / 0.1f) : Mix(Valid, Fail, resultAge / 0.08f);
                    alpha = 1f - k;
                    fill = 1f;
                    if (result == "ok") { march = k * 0.25f; spread = 1f + k * 1.5f; } else scale *= 1f - 0.3f * k;
                }
                else if (ok) march = (Time.unscaledTime * 0.35f) % 0.05f;
                Root.localScale = Vector3.one * scale;
                m_b.Clear();
                Builder glow = m_g;
                glow.Clear();
                for (int i = 0; i < 3; i++)
                {
                    // chevron i lights up when the fill passes i / 3 (the third at the impact)
                    float lit = Mathf.Clamp01((fill * 3f - i) * 2.5f);
                    float a = alpha * Mathf.Lerp(0.28f, 1f, lit);
                    float y = (i * 0.055f) * spread + march;
                    float w = 0.075f - i * 0.006f;
                    Color ci = A(c, a);
                    m_b.Stroke(new Vector2(-w, y - w * 0.75f), new Vector2(0f, y), 0.016f, ci, 0.004f);
                    m_b.Stroke(new Vector2(w, y - w * 0.75f), new Vector2(0f, y), 0.016f, ci, 0.004f);
                    Color gi = A(c, a * (ok ? 0.75f : 0.45f));
                    glow.Stroke(new Vector2(-w, y - w * 0.75f), new Vector2(0f, y), 0.03f, gi, 0.03f, 0.002f);
                    glow.Stroke(new Vector2(w, y - w * 0.75f), new Vector2(0f, y), 0.03f, gi, 0.03f, 0.002f);
                }
                m_b.To(m_body);
                glow.To(m_glow);
            }
        }

        // ======================================================================================== the crit slash

        public static readonly Color CritGold = new Color(1f, 0.82f, 0.25f), CritCore = new Color(1f, 0.97f, 0.8f), Interrupt = new Color(1f, 0.38f, 0.12f);

        /// One crit marker (UYF's directional crit point, our art; combat Stage 2 item 1): a glowing slash across the enemy -
        /// cut along it, either way. Local x = the slash's line (the arrowhead at +x shows the natural way), local z away from
        /// the eyes (placed like the bar). A thin arc round its middle runs down with the time left; the guard-break crit has a
        /// cracked ring round it too. Success: it flares and streaks away green; timeout: grey and shrinking; a cut the wrong
        /// way flickers it red.
        public sealed class Slash
        {
            public readonly Transform Root;
            readonly Mesh m_glow, m_body;
            readonly Builder m_b = new Builder(), m_g = new Builder();

            public Slash(string name = "HoF2VR_CritSlash")
            {
                Root = new GameObject(name).transform;
                Part(Root, "glow", Additive(), out m_glow);
                Part(Root, "body", Alpha(), out m_body);
            }

            public void Destroy()
            {
                if (Root != null) UnityEngine.Object.Destroy(Root.gameObject);
                UnityEngine.Object.Destroy(m_glow);
                UnityEngine.Object.Destroy(m_body);
            }

            /// age: seconds since it opened; left: the share of its time left (1 -> 0); result null / "ok" / "timeout";
            /// wrong: seconds since a cut the wrong way (or a large number); big: the guard-break crit; interrupt: an
            /// interrupt crit (orange).
            public void Draw(float age, float left, string result, float resultAge, float wrong, bool big, bool interrupt)
            {
                float len = big ? 0.62f : 0.52f, h = len * 0.5f, w = big ? 0.034f : 0.028f;
                float show = Mathf.Clamp01(age / 0.22f);
                float scale = Mathf.Lerp(0.3f, 1f, EaseOutBack(show)), alpha = Mathf.Clamp01(age / 0.08f);
                Color main = interrupt ? Interrupt : CritGold, core = CritCore;
                float stretch = 1f, glowA = 0.75f;
                // the last third of its time: it pulses faster and faster
                float urgency = Mathf.Clamp01((0.35f - left) / 0.35f);
                float pulse = 1f - 0.25f * urgency * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * Mathf.Lerp(8f, 26f, urgency)));
                if (result == "ok")
                {
                    float k = Mathf.Clamp01(resultAge / 0.4f);
                    main = Mix(Valid, Success, resultAge / 0.12f);
                    core = Valid;
                    alpha = 1f - k * k;
                    stretch = 1f + 1.6f * Mathf.Sqrt(k);
                    scale *= 1f + 0.15f * k;
                    glowA = 1.3f * (1f - k);
                    left = 0f;
                    pulse = 1f;
                }
                else if (result != null)
                {
                    float k = Mathf.Clamp01(resultAge / 0.35f);
                    main = core = Mix(main, new Color(0.55f, 0.6f, 0.65f), 0.8f);
                    alpha = (1f - k) * 0.7f;
                    scale *= 1f - 0.35f * k;
                    glowA = 0.25f;
                    left = 0f;
                }
                else if (wrong < 0.18f)
                {
                    float k = 1f - wrong / 0.18f;
                    main = Mix(main, Miss, k);
                    core = Mix(core, Miss, k * 0.7f);
                }
                Root.localScale = new Vector3(scale * stretch, scale, scale);
                // glow: a wide soft lens and, for the guard break, a halo ring
                m_g.Clear();
                m_g.Lens(h * 1.06f, w * 2.4f, A(main, alpha * glowA * 0.5f * pulse), 0.05f, 0.002f);
                if (big && result == null) m_g.Arc(0.2f, 0.03f, 0f, 360f, A(main, alpha * 0.35f * pulse), 0.03f, 0.002f);
                m_g.To(m_glow);
                // body: the lens (a gold edge round a bright core), the arrowhead, the timer arc, the cracked ring
                m_b.Clear();
                m_b.Lens(h, w, A(main, alpha * pulse), 0.004f);
                m_b.Lens(h * 0.92f, w * 0.45f, A(core, alpha), 0.004f, -0.001f);
                float ax = h + 0.02f, aw = big ? 0.075f : 0.06f;
                m_b.Stroke(new Vector2(ax - aw, aw * 0.8f), new Vector2(ax, 0f), 0.016f, A(main, alpha * pulse), 0.004f, -0.0015f);
                m_b.Stroke(new Vector2(ax - aw, -aw * 0.8f), new Vector2(ax, 0f), 0.016f, A(main, alpha * pulse), 0.004f, -0.0015f);
                if (left > 0f) m_b.Arc(0.12f, 0.009f, 0f, 360f * Mathf.Clamp01(left), A(core, alpha * 0.85f), 0.004f, -0.002f);
                if (big && result == null)
                    for (int i = 0; i < 5; i++) m_b.Arc(0.2f, 0.012f, i * 72f + 9f, 54f, A(main, alpha * 0.9f * pulse), 0.004f, -0.002f);
                m_b.To(m_body);
            }
        }
    }
}

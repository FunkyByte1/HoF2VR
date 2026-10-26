using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// The combat indicators' looks (`[Combat] IndicatorLook`; the user, 2026-10-13: "I don't like how its just a straight blue
    /// bar. Can you show me some screenshots for different aesthetics options for the combat indicators?"). Each look draws
    /// the block marker (the same place, axis and size as the bar), the dodge arrow and the block's flash; all are our own
    /// procedural meshes (Premonition's builder and materials), the ghost look a copy of the attacker's own weapon mesh.
    ///  * Pill   - the glowing pill filling from the middle (Stage 1's baseline);
    ///  * Arc    - a crescent bowed toward where the blade comes from, filling along its curve, with speed streaks behind it;
    ///             the dodge: a curved arrow; the block: the crescent snaps and throws sparks;
    ///  * Ghost  - a translucent copy of the attacker's weapon where it will pass, solidifying as the blow nears, the block
    ///             spot marked by two brackets; the dodge: the ghost at the head with chevrons away; the block: the ghost
    ///             flares and is knocked back;
    ///  * Gilded - Hand of Fate's card gilding: a gold double line with diamond end caps and embers rising, molten gold filling;
    ///             the dodge: gold chevrons trailing embers; the block: a gold burst;
    ///  * Ring   - a ring closing onto the block spot (the timing at a glance) around a short line across the blow; the dodge:
    ///             a ring closing on the head with a tick the way to go; the block: the ring snaps outward;
    ///  * Bracket- quiet: two corner brackets at the marker's ends and a thin line, brightening as the blow nears; the dodge:
    ///             two thin chevrons; the block: the brackets clap together.
    /// `[Combat] IndicatorByType` (any look): normal blows in the look's colour, heavy ones orange, unblockable red.
    internal static class IndicatorLooks
    {
        public enum LookKind { Pill, Arc, Ghost, Gilded, Ring, Bracket }

        public interface IBlock
        {
            Transform Root { get; }
            void Draw(float age, float fill, bool valid, bool inRange, string result, float resultAge, Color baseColour);
            void Destroy();
        }

        public interface IDodge
        {
            Transform Root { get; }
            void Draw(float age, float fill, float progress, string result, float resultAge);
            void Destroy();
        }

        /// What a look may use beyond the marker's frame: where the blade comes from in the marker's plane (local y: +1 / -1),
        /// the attacker (its weapon mesh), the blade's predicted pass (world).
        public sealed class Context
        {
            public float From = 1f;
            public Controller Attacker;
            public bool HasPass;
            public Vector3 PassA, PassB;
            public static readonly Context None = new Context();
        }

        public static readonly Color Orange = new Color(1f, 0.5f, 0.08f), Red = new Color(1f, 0.12f, 0.08f), GoldC = new Color(1f, 0.78f, 0.28f),
                                     Ember = new Color(1f, 0.45f, 0.12f), Pale = new Color(1f, 0.95f, 0.82f), Steel = new Color(0.75f, 0.9f, 1f);

        /// A crit slash (Poise): cut along its line; local x = the cut's natural way (the arrowhead), local z away from the eyes.
        public interface ISlash
        {
            Transform Root { get; }
            /// left: the share of its time left; result null / "ok" / "timeout"; wrong: seconds since a wrong-way cut; big: the
            /// guard-break crit; interrupt: an interrupt crit.
            void Draw(float age, float left, string result, float resultAge, float wrong, bool big, bool interrupt);
            void Destroy();
        }

        public static ISlash NewSlash(LookKind k, string name = "HoF2VR_CritSlash")
        {
            switch (k)
            {
                case LookKind.Arc: return new ArcSlash(name);
                case LookKind.Ghost: return new GhostSlash(name);
                case LookKind.Gilded: return new GildedSlash(name);
                case LookKind.Ring: return new RingSlash(name);
                case LookKind.Bracket: return new BracketSlash(name);
                default: return new PillSlash(name);
            }
        }

        /// The guard break's burst colours for this look (Poise.Break).
        public static void BreakColours(LookKind k, out Color a, out Color b)
        {
            switch (k)
            {
                case LookKind.Gilded: a = GoldC; b = Ember; break;
                case LookKind.Ghost: a = Pale; b = Steel; break;
                case LookKind.Ring: a = Premonition.Cyan; b = Color.white; break;
                case LookKind.Bracket: a = Steel; b = Color.white; break;
                case LookKind.Arc: a = Premonition.Cyan; b = Pale; break;
                default: a = new Color(0.65f, 0.85f, 1f); b = Color.white; break;
            }
        }

        public static IBlock NewBlock(LookKind k, Context c, string name = "HoF2VR_BlockMarker")
        {
            switch (k)
            {
                case LookKind.Arc: return new ArcBlock(name, c ?? Context.None);
                case LookKind.Ghost: return new GhostBlock(name, c ?? Context.None);
                case LookKind.Gilded: return new GildedBlock(name);
                case LookKind.Ring: return new RingBlock(name);
                case LookKind.Bracket: return new BracketBlock(name);
                default: return new PillBlock(name);
            }
        }

        public static IDodge NewDodge(LookKind k, Context c, string name = "HoF2VR_DodgeMarker")
        {
            switch (k)
            {
                case LookKind.Arc: return new ArcDodge(name);
                case LookKind.Ghost: return new GhostDodge(name, c ?? Context.None);
                case LookKind.Gilded: return new GildedDodge(name);
                case LookKind.Ring: return new RingDodge(name);
                case LookKind.Bracket: return new BracketDodge(name);
                default: return new PillDodge(name);
            }
        }


        // ======================================================================================== the shield's circle

        /// The user's playtest 3 (2026-10-18), note B: with a shield the bar (for the other weapon) gets a circle at its middle -
        /// the shield held like the circle (there, facing the way it faces) blocks. The circle lies square to the line toward the
        /// attacker (its normal = local z), the shield's own size; it turns green while the shield is in place, and mostly fades
        /// while the weapon is lined up instead. A blow only the shield can stop (lobs, shots the blade can't stop) shows the
        /// circle alone. The look's own colour (Gilded: its gold), whatever the look.
        public sealed class ShieldRing
        {
            public Transform Root { get; }
            readonly Mesh m_glow, m_body;
            readonly Premonition.Builder m_b = new Premonition.Builder(), m_g = new Premonition.Builder();
            /// over: drawn over everything (the melee frame: the shield held in it must not hide it).
            public ShieldRing(string name = "HoF2VR_ShieldRing", bool over = false)
            {
                Root = new GameObject(name).transform;
                m_glow = Part(Root, "glow", over ? Premonition.AdditiveOver() : Premonition.Additive());
                m_body = Part(Root, "body", over ? Premonition.AlphaOver() : Premonition.Alpha());
            }

            // the melee frame (the user's playtest 3 note B and the v3 pictures: the circle was the shield's own size, so a shield
            // held on it hid it and its green - "green when the shield is in place"): a ring drawn over everything. P4-13 (playtest 4,
            // mark 222127_10, "about a foot in diameter"): FrameD across the band's middle, whatever the shield (it was 1.2x the
            // shield's radius: 0.96 m across for the basic shield); FrameW wide. The shield blocks when its outline covers at least
            // 55 % of the circle FrameD across (Guard.Coverage; 2026-10-20, before it the shield's centre inside its outer edge).
            public static float FrameD = 0.30f, FrameW = 0.02f;
            public static float FrameInner(float shieldR) => Mathf.Max(0.05f, FrameD * 0.5f - FrameW * 0.5f);
            public static float FrameOuter => FrameD * 0.5f + FrameW * 0.5f;
            public float ShownInner { get; private set; }

            /// The melee frame: as Draw, but `shieldR` is the held shield's radius and the ring frames it (see FrameK).
            public void Frame(float age, float fill, bool shield, bool weapon, string result, float resultAge, float shieldR, Color c)
            {
                float alpha = Mathf.Clamp01(age / 0.25f), scale = Mathf.Lerp(0.85f, 1f, EaseOutBack(Mathf.Clamp01(age / 0.3f)));
                Color main = shield ? Premonition.Lined : c;
                if (weapon && !shield) alpha *= 0.18f;
                if (result == "ok")
                {
                    float k = Mathf.Clamp01(resultAge / 0.45f);
                    main = Mix(Premonition.Pop, Premonition.Success, resultAge / 0.12f);
                    alpha = 1f - k * k;
                    scale = 1f + 0.2f * Mathf.Sqrt(k);
                }
                else if (result != null)
                {
                    float k = Mathf.Clamp01(resultAge / 0.35f);
                    main = Mix(main, Premonition.Miss, 0.8f);
                    alpha *= (1f - k) * 0.5f;
                }
                Root.localScale = Vector3.one * scale;
                bool lit = shield || result == "ok";
                // a slim ring while it waits for the shield, the full band (and its glow) once the shield is in it
                float w = lit ? FrameW : FrameW * 0.6f, mid = FrameD * 0.5f, rIn = mid - w * 0.5f, f = result != null ? 1f : Mathf.Clamp01(fill);
                ShownInner = rIn;
                m_b.Clear();
                m_g.Clear();
                // a dark backing a little wider than the band (snow, sky), the band, and outside it a thin arc that closes as
                // the blow nears (its timing); a soft glow, strong while the shield is in place
                m_b.Arc(mid, w + 0.012f, 0f, 360f, A(new Color(0.02f, 0.03f, 0.03f), alpha * 0.45f), 0.003f);
                m_b.Arc(mid, w, 0f, 360f, A(main, alpha * (lit ? 1f : 0.85f)), 0.003f);
                m_b.Arc(rIn + w + 0.016f, 0.006f, 90f, 360f * f, A(main, alpha * 0.8f), 0.002f);
                m_g.Arc(mid + 0.004f, w + 0.03f, 0f, 360f, A(main, alpha * (lit ? 0.45f : 0.15f)), 0.02f);
                m_b.To(m_body);
                m_g.To(m_glow);
            }
            public void Destroy() { if (Root != null) UnityEngine.Object.Destroy(Root.gameObject); UnityEngine.Object.Destroy(m_glow); UnityEngine.Object.Destroy(m_body); }

            public static Color ColourOf(LookKind k, Color baseColour) => k == LookKind.Gilded ? GoldC : k == LookKind.Ghost ? Pale : k == LookKind.Bracket ? Steel : baseColour;

            /// age since shown; fill 0..1 over the lead; shield: the shield is in place; weapon: the weapon is lined up instead;
            /// result null / "ok" (the shield blocked it) / "fade" (anything else); radius in metres.
            public void Draw(float age, float fill, bool shield, bool weapon, string result, float resultAge, float radius, Color c)
            {
                float alpha = Mathf.Clamp01(age / 0.25f), scale = Mathf.Lerp(0.6f, 1f, EaseOutBack(Mathf.Clamp01(age / 0.3f)));
                Color main = shield ? Premonition.Lined : c;
                if (weapon && !shield) alpha *= 0.18f;
                if (result == "ok")
                {
                    float k = Mathf.Clamp01(resultAge / 0.45f);
                    main = Mix(Premonition.Pop, Premonition.Success, resultAge / 0.12f);
                    alpha = 1f - k * k;
                    scale = 1f + 0.3f * Mathf.Sqrt(k);
                }
                else if (result != null)
                {
                    float k = Mathf.Clamp01(resultAge / 0.35f);
                    main = Mix(main, Premonition.Miss, 0.8f);
                    alpha *= (1f - k) * 0.5f;
                }
                Root.localScale = Vector3.one * scale;
                float r = Mathf.Clamp(radius, 0.12f, Guard.RingMax), f = result != null ? 1f : Mathf.Clamp01(fill);
                m_b.Clear();
                m_g.Clear();
                // a crisp rim, a fainter inner ring that closes as the blow nears (its timing), and a soft glow
                m_b.Arc(r, 0.009f, 0f, 360f, A(main, alpha), 0.003f);
                m_b.Arc(r * 0.82f, 0.005f, 90f, 360f * f, A(main, alpha * 0.75f), 0.002f, -0.001f);
                m_g.Arc(r, 0.04f, 0f, 360f, A(main, alpha * (shield || result == "ok" ? 0.55f : 0.25f)), 0.03f, 0.002f);
                m_b.To(m_body);
                m_g.To(m_glow);
            }
        }

        // ======================================================================================== the ground arrow (shots, lobs, can't-block blows)

        /// The user's playtest 3 (2026-10-18), note C and the ruling of 17:40 ("The white/red bar just looks bad... the line on the
        /// ground filling towards you"): a grey arrow on the ground from the enemy to the player that fills from the enemy toward
        /// the player and is full exactly when the attack reaches them; then it goes. RED fill = nothing you hold can stop it (get
        /// out of the way); a warm gold-white fill = your guard can (the shield's circle shows where). It follows the ground's
        /// height (a ray down every StepLen), lies Lift above it, starts a little in front of the enemy and stops short of the
        /// player's feet with its head, so it never runs under them. A dark border under a light body and an additive glow keep it
        /// readable on grass, snow, cobbles and in night arenas.
        public sealed class GroundArrow
        {
            public Transform Root { get; }
            readonly Mesh m_glow, m_body;
            readonly List<Vector3> m_v = new List<Vector3>(), m_gv = new List<Vector3>();
            readonly List<Color> m_c = new List<Color>(), m_gc = new List<Color>();
            readonly List<int> m_t = new List<int>(), m_gt = new List<int>();
            readonly List<Vector3> m_path = new List<Vector3>();
            // the coordinator's review (v1 -> v2): a clearly grey, slightly see-through empty body (the near-white read as filled on
            // snow and wet cobbles), a saturated warm gold fill
            public static readonly Color Grey = new Color(0.47f, 0.49f, 0.52f), Dark = new Color(0.02f, 0.02f, 0.03f),
                                         Red = new Color(1f, 0.1f, 0.06f), Warm = new Color(1f, 0.6f, 0.02f);
            public const float BodyAlpha = 0.62f, FullAt = 0.92f;
            public const float StepLen = 0.2f, Lift = 0.045f, Width = 0.17f, HeadLen = 0.5f, HeadHalf = 0.26f, StopShort = 0.55f, StartOut = 0.35f;
            static int s_mask = -1;
            static int Mask => s_mask >= 0 ? s_mask : (s_mask = 1 | LayerBit("Ground"));
            static int LayerBit(string n) { int l = LayerMask.NameToLayer(n); return l >= 0 ? 1 << l : 0; }

            public GroundArrow(string name = "HoF2VR_GroundArrow")
            {
                Root = new GameObject(name).transform;
                m_glow = Part(Root, "glow", Premonition.Additive());
                m_body = Part(Root, "body", Premonition.Alpha());
            }
            public void Destroy() { if (Root != null) UnityEngine.Object.Destroy(Root.gameObject); UnityEngine.Object.Destroy(m_glow); UnityEngine.Object.Destroy(m_body); }

            static float GroundY(Vector3 p, float fallback)
            {
                RaycastHit hit;
                if (Physics.Raycast(p + Vector3.up * 1.2f, Vector3.down, out hit, 3f, Mask, QueryTriggerInteraction.Ignore)) return hit.point.y;
                return fallback;
            }

            /// From `from` (the enemy's feet) to `to` (the player's feet); fill 0..1 (1 = it reaches the player now); red = can't be
            /// stopped.
            public void Draw(Vector3 from, Vector3 to, float age, float fill, bool red)
            {
                Vector3 d = to - from;
                d.y = 0f;
                float len = d.magnitude;
                // the audit, item 7: a close shooter (under ~1.4 m) still gets an arrow, shortened (its head too), down to 0.2 m
                float stop = Mathf.Min(StopShort, len * 0.3f);
                float x0 = Mathf.Min(StartOut, len * 0.12f), x1 = len - stop;
                if (x1 - x0 < 0.2f) { Root.gameObject.SetActive(false); return; }
                float headLen = Mathf.Min(HeadLen, (x1 - x0) * 0.45f), headHalf = Mathf.Max(0.1f, HeadHalf * headLen / HeadLen);
                Root.gameObject.SetActive(true);
                Root.position = Vector3.zero;
                Root.rotation = Quaternion.identity;
                Root.localScale = Vector3.one;
                d /= len;
                Vector3 side = Vector3.Cross(Vector3.up, d).normalized;
                float alpha = Mathf.Clamp01(age / 0.2f), f = Mathf.Clamp01(fill);
                Color fc = red ? Red : Warm;
                float pulse = f > 0.8f ? 0.7f + 0.3f * Mathf.Sin(Time.unscaledTime * 26f) : 1f;
                // the path on the ground
                int n = Mathf.Max(2, Mathf.CeilToInt((x1 - x0) / StepLen) + 1);
                m_path.Clear();
                float y = from.y;
                for (int i = 0; i < n; i++)
                {
                    Vector3 p = from + d * Mathf.Lerp(x0, x1, i / (float)(n - 1));
                    y = GroundY(p, Mathf.Lerp(from.y, to.y, i / (float)(n - 1)));
                    p.y = y + Lift;
                    m_path.Add(p);
                }
                float xs = x1 - headLen, xf = Mathf.Lerp(x0, x1, f);
                m_v.Clear(); m_c.Clear(); m_t.Clear(); m_gv.Clear(); m_gc.Clear(); m_gt.Clear();
                // the shaft: a dark border, the light body, the fill from the enemy's end (z-order by a few millimetres up)
                Strip(m_v, m_c, m_t, x0, xs, Width + 0.06f, A(Dark, 0.55f * alpha), 0f, x0, x1);
                Strip(m_v, m_c, m_t, x0, xs, Width, A(Grey, BodyAlpha * alpha), 0.004f, x0, x1);
                if (xf > x0 + 0.01f) Strip(m_v, m_c, m_t, x0, Mathf.Min(xf, xs), Width * 0.72f, A(fc, alpha * pulse), 0.008f, x0, x1);
                // the head: a dark outline, the light head, its fill as the front passes
                Head(m_v, m_c, m_t, xs, x1, headHalf + 0.04f, A(Dark, 0.55f * alpha), 0f, x0, x1);
                Head(m_v, m_c, m_t, xs, x1, headHalf, A(Grey, BodyAlpha * alpha), 0.004f, x0, x1);
                // the last moment (FullAt on): the whole head in the fill's colour - "it is here now"
                if (f >= FullAt) Head(m_v, m_c, m_t, xs, x1, headHalf, A(fc, alpha * pulse), 0.008f, x0, x1);
                else if (xf > xs) Head(m_v, m_c, m_t, xs, Mathf.Lerp(xs, x1, Mathf.Clamp01((xf - xs) / Mathf.Max(0.01f, x1 - xs))), headHalf * 0.72f, A(fc, alpha * pulse), 0.008f, x0, x1, x1);
                if (f >= FullAt) Head(m_gv, m_gc, m_gt, xs - 0.05f, x1 + 0.05f, headHalf * 1.6f, A(fc, 0.35f * alpha * pulse), 0.01f, x0, x1);
                // the glow over the filled part
                if (xf > x0 + 0.01f) Strip(m_gv, m_gc, m_gt, x0, Mathf.Min(xf, x1), Width * 2.4f, A(fc, 0.32f * alpha * pulse), 0.01f, x0, x1);
                Put(m_body, m_v, m_c, m_t);
                Put(m_glow, m_gv, m_gc, m_gt);
            }

            Vector3 At(float x, float x0, float x1)
            {
                float u = Mathf.Clamp01((x - x0) / Mathf.Max(0.01f, x1 - x0)) * (m_path.Count - 1);
                int i = Mathf.Min(m_path.Count - 2, Mathf.FloorToInt(u));
                return Vector3.Lerp(m_path[i], m_path[i + 1], u - i);
            }

            Vector3 SideAt(float x, float x0, float x1)
            {
                Vector3 a = At(x - 0.05f, x0, x1), b = At(x + 0.05f, x0, x1), t = b - a;
                t.y = 0f;
                return Vector3.Cross(Vector3.up, t.sqrMagnitude > 1e-6f ? t.normalized : Vector3.forward).normalized;
            }

            void Strip(List<Vector3> v, List<Color> c, List<int> t, float a, float b, float w, Color col, float up, float x0, float x1)
            {
                if (b - a < 0.005f) return;
                int k = Mathf.Max(1, Mathf.CeilToInt((b - a) / StepLen)), b0 = v.Count;
                for (int i = 0; i <= k; i++)
                {
                    float x = Mathf.Lerp(a, b, i / (float)k);
                    Vector3 p = At(x, x0, x1) + Vector3.up * up, s = SideAt(x, x0, x1) * (w * 0.5f);
                    v.Add(p - s); v.Add(p + s);
                    c.Add(col); c.Add(col);
                }
                for (int i = 0; i < k; i++)
                {
                    int q = b0 + i * 2;
                    t.Add(q); t.Add(q + 2); t.Add(q + 1);
                    t.Add(q + 1); t.Add(q + 2); t.Add(q + 3);
                }
            }

            /// A filled triangle from its base at `a` (half width `half`) to its point at `b` (or a trapezoid cut at `cut`).
            void Head(List<Vector3> v, List<Color> c, List<int> t, float a, float b, float half, Color col, float up, float x0, float x1, float tipAt = -1f)
            {
                float tip = tipAt > 0f ? tipAt : b;
                Vector3 pa = At(a, x0, x1) + Vector3.up * up, pt = At(tip, x0, x1) + Vector3.up * up, s = SideAt(a, x0, x1);
                if (tipAt > 0f && b < tip)
                {
                    // filled up to b: a trapezoid of the triangle
                    float k = Mathf.Clamp01((b - a) / Mathf.Max(0.01f, tip - a));
                    Vector3 pb = At(b, x0, x1) + Vector3.up * up;
                    int q0 = v.Count;
                    v.Add(pa - s * half); v.Add(pa + s * half); v.Add(pb + s * half * (1f - k)); v.Add(pb - s * half * (1f - k));
                    for (int i = 0; i < 4; i++) c.Add(col);
                    t.Add(q0); t.Add(q0 + 1); t.Add(q0 + 2); t.Add(q0); t.Add(q0 + 2); t.Add(q0 + 3);
                    return;
                }
                int q = v.Count;
                v.Add(pa - s * half); v.Add(pa + s * half); v.Add(pt);
                c.Add(col); c.Add(col); c.Add(col);
                t.Add(q); t.Add(q + 1); t.Add(q + 2);
            }

            static void Put(Mesh m, List<Vector3> v, List<Color> c, List<int> t)
            {
                m.Clear();
                m.SetVertices(v);
                m.SetColors(c);
                m.SetTriangles(t, 0);
                m.RecalculateBounds();
            }
        }

        // ======================================================================================== crit slashes per look

        abstract class SlashBase : ISlash
        {
            public Transform Root { get; }
            protected readonly Mesh Glow, Body;
            protected readonly Premonition.Builder B = new Premonition.Builder(), G = new Premonition.Builder();
            protected SlashBase(string name)
            {
                Root = new GameObject(name).transform;
                Glow = Part(Root, "glow", Premonition.Additive());
                Body = Part(Root, "body", Premonition.Alpha());
            }
            public void Destroy() { if (Root != null) UnityEngine.Object.Destroy(Root.gameObject); UnityEngine.Object.Destroy(Glow); UnityEngine.Object.Destroy(Body); }
            public abstract void Draw(float age, float left, string result, float resultAge, float wrong, bool big, bool interrupt);

            /// The shared envelope: show, result (green streak / grey shrink), the wrong cut's red flicker, the last third's pulse.
            protected void Env(float age, float left, string result, float resultAge, float wrong, ref Color main, out float alpha, out float scale, out float stretch,
                               out float pulse, out bool ok)
            {
                float show = Mathf.Clamp01(age / 0.22f);
                scale = Mathf.Lerp(0.3f, 1f, EaseOutBack(show));
                alpha = Mathf.Clamp01(age / 0.08f);
                stretch = 1f;
                ok = result == "ok";
                float urgency = Mathf.Clamp01((0.35f - left) / 0.35f);
                pulse = 1f - 0.25f * urgency * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * Mathf.Lerp(8f, 26f, urgency)));
                if (ok)
                {
                    float k = Mathf.Clamp01(resultAge / 0.4f);
                    main = Mix(Premonition.Valid, Premonition.Success, resultAge / 0.12f);
                    alpha = 1f - k * k;
                    stretch = 1f + 1.6f * Mathf.Sqrt(k);
                    pulse = 1f;
                }
                else if (result != null)
                {
                    float k = Mathf.Clamp01(resultAge / 0.35f);
                    main = Mix(main, new Color(0.55f, 0.6f, 0.65f), 0.8f);
                    alpha = (1f - k) * 0.7f;
                    scale *= 1f - 0.35f * k;
                }
                else if (wrong < 0.18f) main = Mix(main, Premonition.Miss, 1f - wrong / 0.18f);
            }
        }

        sealed class PillSlash : ISlash
        {
            readonly Premonition.Slash m_s;
            public PillSlash(string name) { m_s = new Premonition.Slash(name); }
            public Transform Root => m_s.Root;
            public void Draw(float age, float left, string result, float resultAge, float wrong, bool big, bool interrupt) => m_s.Draw(age, left, result, resultAge, wrong, big, interrupt);
            public void Destroy() => m_s.Destroy();
        }

        sealed class ArcSlash : SlashBase
        {
            public ArcSlash(string name) : base(name) { }
            public override void Draw(float age, float left, string result, float resultAge, float wrong, bool big, bool interrupt)
            {
                Color main = interrupt ? Premonition.Interrupt : Premonition.Cyan;
                float alpha, scale, stretch, pulse;
                bool ok;
                Env(age, left, result, resultAge, wrong, ref main, out alpha, out scale, out stretch, out pulse, out ok);
                Root.localScale = new Vector3(scale * stretch, scale, scale);
                B.Clear();
                G.Clear();
                // a curved cut: a crescent sweeping along the line, thick in its middle; the arrowhead at its end
                float h = big ? 0.32f : 0.27f, bow = 0.06f;
                int n = 20;
                Vector2 prev = Vector2.zero;
                for (int i = 0; i <= n; i++)
                {
                    float u = i / (float)n, x = Mathf.Lerp(-h, h, u), y = bow * (1f - 4f * (u - 0.5f) * (u - 0.5f));
                    Vector2 p = new Vector2(x, y);
                    if (i > 0)
                    {
                        float mid = 1f - Mathf.Abs(u - 0.5f) * 2f, lit = u <= Mathf.Max(left, 0.02f) || result != null ? 1f : 0.35f;
                        B.Stroke(prev, p, Mathf.Lerp(0.006f, big ? 0.03f : 0.024f, mid), A(main, alpha * pulse * lit), 0.004f);
                        G.Stroke(prev, p, 0.05f, A(main, alpha * 0.3f * lit), 0.03f, 0.002f);
                    }
                    prev = p;
                }
                Vector2 tip = new Vector2(h + 0.02f, 0f);
                B.Stroke(tip, tip + new Vector2(-0.06f, 0.045f), 0.014f, A(main, alpha), 0.004f, -0.001f);
                B.Stroke(tip, tip + new Vector2(-0.06f, -0.045f), 0.014f, A(main, alpha), 0.004f, -0.001f);
                if (big && result == null) G.Arc(0.19f, 0.025f, 0f, 360f, A(main, alpha * 0.35f * pulse), 0.03f, 0.002f);
                B.To(Body);
                G.To(Glow);
            }
        }

        sealed class GhostSlash : SlashBase
        {
            public GhostSlash(string name) : base(name) { }
            public override void Draw(float age, float left, string result, float resultAge, float wrong, bool big, bool interrupt)
            {
                Color main = interrupt ? Premonition.Interrupt : Pale;
                float alpha, scale, stretch, pulse;
                bool ok;
                Env(age, left, result, resultAge, wrong, ref main, out alpha, out scale, out stretch, out pulse, out ok);
                Root.localScale = new Vector3(scale * stretch, scale, scale);
                B.Clear();
                G.Clear();
                // a ghost blade drawn along the cut, sweeping a little in the cut's direction (an afterimage trail behind it)
                float h = big ? 0.3f : 0.25f, sweep = result == null ? 0.04f * Mathf.Sin(Time.unscaledTime * 3f) : 0f;
                for (int k = 3; k >= 0; k--)
                {
                    float off = -k * 0.025f + sweep, ka = k == 0 ? 1f : 0.25f / k;
                    B.Lens(h, 0.018f, A(main, alpha * 0.55f * ka * pulse), 0.006f, k * 0.0005f);
                    B.Stroke(new Vector2(-h + off, 0f), new Vector2(h + off, 0f), 0.003f, A(Color.white, alpha * 0.7f * ka), 0.002f, -0.001f);
                }
                G.Lens(h * 1.05f, 0.05f, A(main, alpha * 0.25f), 0.04f, 0.002f);
                Vector2 tip = new Vector2(h + 0.025f, 0f);
                B.Stroke(tip, tip + new Vector2(-0.05f, 0.035f), 0.008f, A(main, alpha), 0.003f, -0.001f);
                B.Stroke(tip, tip + new Vector2(-0.05f, -0.035f), 0.008f, A(main, alpha), 0.003f, -0.001f);
                if (result == null) B.Arc(0.1f, 0.006f, 0f, 360f * Mathf.Clamp01(left), A(main, alpha * 0.6f), 0.003f, -0.002f);
                B.To(Body);
                G.To(Glow);
            }
        }

        sealed class GildedSlash : SlashBase
        {
            readonly float m_seed = UnityEngine.Random.value * 10f;
            public GildedSlash(string name) : base(name) { }
            public override void Draw(float age, float left, string result, float resultAge, float wrong, bool big, bool interrupt)
            {
                Color main = interrupt ? Ember : GoldC;
                float alpha, scale, stretch, pulse;
                bool ok;
                Env(age, left, result, resultAge, wrong, ref main, out alpha, out scale, out stretch, out pulse, out ok);
                Root.localScale = new Vector3(scale * stretch, scale, scale);
                B.Clear();
                G.Clear();
                float h = big ? 0.31f : 0.26f, now = Time.unscaledTime;
                // the gilded cut: a dark lacquer lens edged in gold, a molten core, diamond ends; embers along it
                B.Lens(h, 0.03f, A(new Color(0.1f, 0.05f, 0.02f), alpha * 0.75f), 0.004f, 0.001f);
                B.Lens(h * 0.96f, 0.012f, A(Mix(main, Pale, 0.4f), alpha * pulse), 0.004f, -0.001f);
                B.Stroke(new Vector2(-h, 0.032f), new Vector2(h * 0.6f, 0.032f), 0.003f, A(main, alpha), 0.002f, -0.0015f);
                B.Stroke(new Vector2(-h * 0.6f, -0.032f), new Vector2(h, -0.032f), 0.003f, A(main, alpha), 0.002f, -0.0015f);
                Vector2 tip = new Vector2(h + 0.025f, 0f);
                B.Stroke(tip, tip + new Vector2(-0.06f, 0.045f), 0.012f, A(main, alpha), 0.003f, -0.002f);
                B.Stroke(tip, tip + new Vector2(-0.06f, -0.045f), 0.012f, A(main, alpha), 0.003f, -0.002f);
                G.Lens(h, 0.06f, A(Ember, alpha * 0.35f * pulse), 0.04f, 0.002f);
                int n = ok ? 16 : 7;
                for (int i = 0; i < n; i++)
                {
                    float ph = (now * (0.7f + 0.11f * i) + i * 0.29f + m_seed) % 1f, x = Mathf.Lerp(-h, h, (i * 0.618f + m_seed) % 1f), y = 0.03f + ph * (ok ? 0.2f : 0.09f);
                    G.Stroke(new Vector2(x, y), new Vector2(x + 0.004f, y + 0.012f), 0.007f, A(Ember, alpha * (1f - ph)), 0.006f, 0.002f);
                }
                if (big && result == null)
                    for (int i = 0; i < 8; i++) B.Arc(0.2f, 0.01f, i * 45f + 6f, 33f, A(main, alpha * 0.9f * pulse), 0.003f, -0.002f);
                if (result == null) B.Arc(0.11f, 0.007f, 0f, 360f * Mathf.Clamp01(left), A(Pale, alpha * 0.8f), 0.003f, -0.002f);
                B.To(Body);
                G.To(Glow);
            }
        }

        sealed class RingSlash : SlashBase
        {
            public RingSlash(string name) : base(name) { }
            public override void Draw(float age, float left, string result, float resultAge, float wrong, bool big, bool interrupt)
            {
                Color main = interrupt ? Premonition.Interrupt : Premonition.Cyan;
                float alpha, scale, stretch, pulse;
                bool ok;
                Env(age, left, result, resultAge, wrong, ref main, out alpha, out scale, out stretch, out pulse, out ok);
                Root.localScale = new Vector3(scale * stretch, scale, scale);
                B.Clear();
                G.Clear();
                // the line to cut through a ring that closes in over the crit's time (the time left at a glance)
                float h = big ? 0.3f : 0.25f, rr = result == null ? Mathf.Lerp(0.07f, big ? 0.3f : 0.24f, Mathf.Clamp01(left)) : 0.07f;
                B.Stroke(new Vector2(-h, 0f), new Vector2(h, 0f), big ? 0.02f : 0.014f, A(main, alpha * pulse), 0.004f);
                G.Stroke(new Vector2(-h, 0f), new Vector2(h, 0f), 0.05f, A(main, alpha * 0.3f), 0.03f, 0.002f);
                Vector2 tip = new Vector2(h + 0.02f, 0f);
                B.Stroke(tip, tip + new Vector2(-0.05f, 0.04f), 0.012f, A(main, alpha), 0.004f, -0.001f);
                B.Stroke(tip, tip + new Vector2(-0.05f, -0.04f), 0.012f, A(main, alpha), 0.004f, -0.001f);
                B.Arc(rr, big ? 0.014f : 0.01f, 0f, 360f, A(main, alpha * (ok ? 1f : 0.75f)), 0.004f, -0.0015f);
                G.Arc(rr, 0.04f, 0f, 360f, A(main, alpha * 0.25f), 0.03f, 0.002f);
                B.To(Body);
                G.To(Glow);
            }
        }

        sealed class BracketSlash : SlashBase
        {
            public BracketSlash(string name) : base(name) { }
            public override void Draw(float age, float left, string result, float resultAge, float wrong, bool big, bool interrupt)
            {
                Color main = interrupt ? Premonition.Interrupt : Steel;
                float alpha, scale, stretch, pulse;
                bool ok;
                Env(age, left, result, resultAge, wrong, ref main, out alpha, out scale, out stretch, out pulse, out ok);
                Root.localScale = new Vector3(scale * stretch, scale, scale);
                B.Clear();
                float h = big ? 0.3f : 0.25f, br = 0.04f;
                // a thin cut line with brackets at its ends and a small arrowhead; the time left as the line's lit length
                float lit = result != null ? 1f : Mathf.Clamp01(left);
                B.Stroke(new Vector2(-h, 0f), new Vector2(-h + 2f * h * lit, 0f), big ? 0.008f : 0.005f, A(main, alpha * pulse), 0.002f);
                B.Stroke(new Vector2(-h + 2f * h * lit, 0f), new Vector2(h, 0f), 0.003f, A(main, alpha * 0.35f), 0.002f);
                for (int sgn = -1; sgn <= 1; sgn += 2)
                {
                    Vector2 e = new Vector2(sgn * (h + 0.015f), 0f);
                    B.Stroke(e + new Vector2(0f, -br), e + new Vector2(0f, br), 0.006f, A(main, alpha), 0.002f, -0.001f);
                }
                Vector2 tip = new Vector2(h + 0.04f, 0f);
                B.Stroke(tip, tip + new Vector2(-0.03f, 0.022f), 0.006f, A(main, alpha), 0.002f, -0.001f);
                B.Stroke(tip, tip + new Vector2(-0.03f, -0.022f), 0.006f, A(main, alpha), 0.002f, -0.001f);
                B.To(Body);
            }
        }

        // ---------------------------------------------------------------------------------------- shared

        static Color A(Color c, float a) => new Color(c.r, c.g, c.b, c.a * a);
        static Color Mix(Color a, Color b, float t) => Color.Lerp(a, b, Mathf.Clamp01(t));
        static float EaseOutBack(float x) { const float c1 = 1.70158f, c3 = c1 + 1f; x -= 1f; return 1f + c3 * x * x * x + c1 * x * x; }

        static Mesh Part(Transform parent, string name, Material m)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var mesh = new Mesh();
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return mesh;
        }

        /// The common show / result envelope: scale and alpha over the show, and the result's colour, alpha and progress.
        static void Envelope(float age, string result, float resultAge, ref Color c, out float alpha, out float scale, out float k, out bool ok, out bool fail, out bool fade)
        {
            // UYF's show: 25 % to full, linearly over 1 s; alpha over the first 0.3 s
            float show = Mathf.Clamp01(age / 1f);
            scale = Mathf.Lerp(0.25f, 1f, show);
            alpha = Mathf.Clamp01(age / 0.3f);
            ok = result == "ok";
            fail = result == "fail";
            fade = result == "fade" || result == "timeout";
            k = 0f;
            if (ok)
            {
                // blocked: a white-hot pop that turns green and swells as it goes (the lined-up bar is green already: the
                // flash must still read as the success)
                k = Mathf.Clamp01(resultAge / 0.45f);
                c = Mix(Premonition.Pop, Premonition.Success, resultAge / 0.12f);
                alpha = 1f - k * k;
                scale = 1f + 0.3f * Mathf.Sqrt(k);
            }
            else if (fail || fade)
            {
                k = Mathf.Clamp01(resultAge / 0.35f);
                c = Mix(c, new Color(0.55f, 0.6f, 0.65f), 0.7f);
                alpha = (1f - k) * 0.6f;
            }
        }

        // ---------------------------------------------------------------------------------------- Pill (the baseline)

        sealed class PillBlock : IBlock
        {
            readonly Premonition.Bar m_b;
            public PillBlock(string name) { m_b = new Premonition.Bar(name); }
            public Transform Root => m_b.Root;
            public void Draw(float age, float fill, bool valid, bool inRange, string result, float resultAge, Color c) => m_b.Draw(age, fill, valid, inRange, result, resultAge, c);
            public void Destroy() => m_b.Destroy();
        }

        sealed class PillDodge : IDodge
        {
            readonly Premonition.Arrow m_a;
            public PillDodge(string name) { m_a = new Premonition.Arrow(name); }
            public Transform Root => m_a.Root;
            public void Draw(float age, float fill, float progress, string result, float resultAge) => m_a.Draw(age, fill, progress, result, resultAge);
            public void Destroy() => m_a.Destroy();
        }

        // ---------------------------------------------------------------------------------------- Arc

        sealed class ArcBlock : IBlock
        {
            public Transform Root { get; }
            readonly Mesh m_glow, m_body;
            readonly Premonition.Builder m_b = new Premonition.Builder(), m_g = new Premonition.Builder();
            readonly float m_from;
            public ArcBlock(string name, Context c)
            {
                Root = new GameObject(name).transform;
                m_glow = Part(Root, "glow", Premonition.Additive());
                m_body = Part(Root, "body", Premonition.Alpha());
                m_from = c.From >= 0f ? 1f : -1f;
            }
            public void Destroy() { if (Root != null) UnityEngine.Object.Destroy(Root.gameObject); UnityEngine.Object.Destroy(m_glow); UnityEngine.Object.Destroy(m_body); }

            /// A crescent through the bar's ends bowed toward the blade (local y = m_from), its sagitta s.
            void Crescent(Premonition.Builder b, float h, float s, float w0, float w1, float t0, float t1, Color c0, Color c1, float z)
            {
                // the arc of a circle through (-h, 0), (h, 0) and (0, s): centre (0, (s*s - h*h) / (2s)), radius r
                float yc = (s * s - h * h) / (2f * s), r = s - yc;
                float a0 = Mathf.Atan2(-yc, -h), a1 = Mathf.Atan2(-yc, h);   // the end angles
                // the arc's half (from the end to the middle) mirrored: sample from left end to right end
                int n = 24;
                Vector2 prev = Vector2.zero;
                for (int i = 0; i <= n; i++)
                {
                    float u = i / (float)n;
                    if (u < t0 || u > t1) continue;
                    float a = Mathf.Lerp(a0, a0 + DeltaRad(a0, a1) , u);
                    Vector2 p = new Vector2(Mathf.Cos(a) * r, yc + Mathf.Sin(a) * r);
                    p.y *= m_from;
                    if (i > 0 && u - 1f / n >= t0)
                    {
                        float mid = 1f - Mathf.Abs(u - 0.5f) * 2f;   // thick in the middle, pointed at the ends
                        b.Stroke(prev, p, Mathf.Lerp(w0, w1, mid), Color.Lerp(c0, c1, mid), 0.004f, z);
                    }
                    prev = p;
                }
            }

            static float DeltaRad(float a0, float a1)
            {
                // the short way through the bulge: from the left end over the top to the right end
                float d = a1 - a0;
                while (d > 0f) d -= 2f * Mathf.PI;
                return d;
            }

            public void Draw(float age, float fill, bool valid, bool inRange, string result, float resultAge, Color c)
            {
                Color main = valid ? Premonition.Lined : c;
                float alpha, scale, k;
                bool ok, fail, fade;
                Envelope(age, result, resultAge, ref main, out alpha, out scale, out k, out ok, out fail, out fade);
                if (!inRange && result == null) { main = Mix(main, new Color(0.55f, 0.6f, 0.65f), 0.6f); alpha *= 0.4f; }
                float h = 0.29f, s = 0.07f;
                Root.localScale = Vector3.one * scale * (ok ? 1f + 0.2f * k : 1f);
                m_b.Clear();
                m_g.Clear();
                // the track, then the fill growing from the middle out along the curve
                Crescent(m_b, h, s, 0.006f, 0.022f, 0f, 1f, A(c * 0.3f, alpha * 0.5f), A(c * 0.35f, alpha * 0.55f), 0f);
                float f = result != null ? 1f : Mathf.Clamp01(fill);
                Crescent(m_b, h, s, 0.004f, 0.016f, 0.5f - f * 0.5f, 0.5f + f * 0.5f, A(Mix(main, Color.white, 0.4f), alpha), A(main, alpha), -0.001f);
                Crescent(m_g, h, s, 0.02f, 0.06f, 0f, 1f, A(main, alpha * (valid ? 0.6f : 0.25f + 0.3f * f)), A(main, alpha * (valid ? 0.8f : 0.35f + 0.3f * f)), 0.002f);
                // speed streaks behind it, on the side the blade comes from (they rush in as the blow nears)
                if (result == null)
                    for (int i = 0; i < 3; i++)
                    {
                        float x = (i - 1) * 0.12f, y0 = (0.16f + 0.1f * (1f - f) + 0.03f * i) * m_from, y1 = y0 - 0.07f * m_from;
                        m_g.Stroke(new Vector2(x, y0), new Vector2(x, y1), 0.006f, A(main, alpha * 0.45f * f), 0.01f, 0.002f);
                    }
                // the block: sparks flung outward from the crescent
                if (ok)
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * 45f * Mathf.Deg2Rad, r0 = 0.05f + 0.25f * k, r1 = r0 + 0.06f;
                        m_g.Stroke(new Vector2(Mathf.Cos(a) * r0, Mathf.Sin(a) * r0), new Vector2(Mathf.Cos(a) * r1, Mathf.Sin(a) * r1), 0.008f, A(Premonition.Success, 1f - k), 0.008f, 0.002f);
                    }
                m_b.To(m_body);
                m_g.To(m_glow);
            }
        }

        sealed class ArcDodge : IDodge
        {
            public Transform Root { get; }
            readonly Mesh m_glow, m_body;
            readonly Premonition.Builder m_b = new Premonition.Builder(), m_g = new Premonition.Builder();
            public ArcDodge(string name)
            {
                Root = new GameObject(name).transform;
                m_glow = Part(Root, "glow", Premonition.Additive());
                m_body = Part(Root, "body", Premonition.Alpha());
            }
            public void Destroy() { if (Root != null) UnityEngine.Object.Destroy(Root.gameObject); UnityEngine.Object.Destroy(m_glow); UnityEngine.Object.Destroy(m_body); }
            public void Draw(float age, float fill, float progress, string result, float resultAge)
            {
                Color c = progress >= 1f && result == null ? Premonition.DodgeOk : Premonition.Dodge;
                float alpha, scale, k;
                bool ok, fail, fade;
                Envelope(age, result, resultAge, ref c, out alpha, out scale, out k, out ok, out fail, out fade);
                Root.localScale = Vector3.one * scale * 1.5f;
                m_b.Clear();
                m_g.Clear();
                // a curved arrow sweeping round and up the way to move (local +y): its whole path faint, lit as it fills, its
                // head always there (the way at a glance) and brighter as it fills
                int n = 16;
                float f = result != null ? 1f : Mathf.Clamp01(fill), lift = ok ? 0.15f * k : 0f;
                Vector2 prev = Swoosh(0f, lift);
                for (int i = 1; i <= n; i++)
                {
                    float u = i / (float)n;
                    Vector2 p = Swoosh(u, lift);
                    bool lit = u <= f + 1e-4f;
                    m_b.Stroke(prev, p, 0.014f, A(c, lit ? alpha : alpha * 0.25f), 0.004f);
                    if (lit) m_g.Stroke(prev, p, 0.03f, A(c, alpha * 0.4f), 0.02f, 0.002f);
                    prev = p;
                }
                Vector2 tip = Swoosh(1f, lift);
                float ha = alpha * (0.55f + 0.45f * f);
                foreach (float sx in new[] { -1f, 1f })
                {
                    Vector2 e = tip + new Vector2(0.042f * sx, -0.04f);
                    m_b.Stroke(tip, e, 0.016f, A(c, ha), 0.004f);
                    m_g.Stroke(tip, e, 0.035f, A(c, ha * 0.4f), 0.02f, 0.002f);
                }
                m_b.To(m_body);
                m_g.To(m_glow);
            }

            /// The swoosh's point at u (0..1): from low on the left, round under and up, ending headed along +y.
            static Vector2 Swoosh(float u, float lift)
            {
                float a = Mathf.Lerp(-160f, 0f, u) * Mathf.Deg2Rad;
                return new Vector2(Mathf.Cos(a) * 0.08f - 0.04f, Mathf.Sin(a) * 0.1f + 0.04f + lift);
            }
        }

        // ---------------------------------------------------------------------------------------- Ghost

        /// A copy of the attacker's weapon mesh (the first renderer the swing trace calls a weapon), its long axis found
        /// from its bounds.
        sealed class GhostMesh
        {
            public Mesh Mesh;
            public bool Owned;   // a copy of ours (destroyed with the marker)
            public Vector3 LongAxis = Vector3.forward, Center;
            public float Length = 0.8f;
            public static GhostMesh Of(Controller c)
            {
                var g = new GhostMesh { LongAxis = Vector3.right, Length = 1f };   // no weapon mesh: a plain blade quad along x
                try
                {
                    if (c != null && c.Model != null)
                        foreach (MeshFilter mf in c.Model.GetComponentsInChildren<MeshFilter>(false))
                        {
                            if (mf == null || mf.sharedMesh == null) continue;
                            string n = mf.name.ToLowerInvariant();
                            if (n.Contains("shield") || !(n.Contains("weapon") || n.Contains("sword") || n.Contains("axe") || n.Contains("dagger") || n.Contains("blade") ||
                                                           n.Contains("knife") || n.Contains("mace") || n.Contains("club") || n.Contains("spear") || n.Contains("hammer") || n.Contains("sabre") || n.Contains("saber"))) continue;
                            Mesh copy;
                            if (mf.sharedMesh.isReadable)
                            {
                                copy = UnityEngine.Object.Instantiate(mf.sharedMesh);
                                var cols = new Color[copy.vertexCount];
                                for (int i = 0; i < cols.Length; i++) cols[i] = Color.white;
                                copy.colors = cols;   // the material multiplies the vertex colour
                                g.Owned = true;
                            }
                            else copy = mf.sharedMesh;
                            g.Mesh = copy;
                            Bounds b = copy.bounds;
                            Vector3 s = Vector3.Scale(b.size, mf.transform.lossyScale);
                            g.LongAxis = s.x >= s.y && s.x >= s.z ? Vector3.right : s.y >= s.z ? Vector3.up : Vector3.forward;
                            g.Length = Mathf.Max(0.2f, Vector3.Dot(b.size, g.LongAxis));
                            g.Center = b.center;
                            break;
                        }
                }
                catch (Exception) { }
                return g;
            }
        }

        sealed class GhostBlock : IBlock
        {
            public Transform Root { get; }
            readonly Mesh m_glow, m_body;
            readonly Premonition.Builder m_b = new Premonition.Builder(), m_g = new Premonition.Builder();
            readonly Transform m_ghost;
            readonly Material m_mat;
            readonly GhostMesh m_mesh;
            readonly Context m_c;
            public GhostBlock(string name, Context c)
            {
                m_c = c;
                Root = new GameObject(name).transform;
                m_glow = Part(Root, "glow", Premonition.Additive());
                m_body = Part(Root, "body", Premonition.Alpha());
                m_mesh = GhostMesh.Of(c.Attacker);
                m_mat = new Material(Premonition.Additive()) { renderQueue = 3165 };
                var go = new GameObject("ghost");
                go.transform.SetParent(Root, false);
                go.AddComponent<MeshFilter>().sharedMesh = m_mesh.Mesh != null ? m_mesh.Mesh : CombatFeel.WhiteQuad;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = m_mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                m_ghost = go.transform;
            }
            public void Destroy()
            {
                if (Root != null) UnityEngine.Object.Destroy(Root.gameObject);
                UnityEngine.Object.Destroy(m_glow);
                UnityEngine.Object.Destroy(m_body);
                UnityEngine.Object.Destroy(m_mat);
                if (m_mesh.Mesh != null && m_mesh.Owned) UnityEngine.Object.Destroy(m_mesh.Mesh);
            }
            public void Draw(float age, float fill, bool valid, bool inRange, string result, float resultAge, Color c)
            {
                Color main = valid ? Premonition.Lined : c;
                float alpha, scale, k;
                bool ok, fail, fade;
                Envelope(age, result, resultAge, ref main, out alpha, out scale, out k, out ok, out fail, out fade);
                if (!inRange && result == null) alpha *= 0.4f;
                float f = result != null ? 1f : Mathf.Clamp01(fill);
                Root.localScale = Vector3.one;
                // the ghost: along the blade's predicted pass (else along the marker), translucent -> nearly solid as it nears
                Vector3 a, b;
                if (m_c.HasPass) { a = Root.InverseTransformPoint(m_c.PassA); b = Root.InverseTransformPoint(m_c.PassB); }
                else { a = new Vector3(-0.35f, 0.02f * m_c.From, 0.05f); b = new Vector3(0.35f, -0.02f * m_c.From, 0.05f); }
                Vector3 mid = (a + b) * 0.5f, dir = b - a;
                if (dir.sqrMagnitude < 1e-6f) dir = Vector3.right;
                float len = dir.magnitude;
                Quaternion q = Quaternion.FromToRotation(m_mesh.LongAxis, dir.normalized);
                float sc = m_mesh.Mesh != null ? len / m_mesh.Length : 1f;
                Vector3 kick = ok ? Vector3.forward * (0.25f * k) : Vector3.zero;
                m_ghost.localRotation = q;
                m_ghost.localScale = m_mesh.Mesh != null ? Vector3.one * sc : new Vector3(len, 0.03f, 1f);
                m_ghost.localPosition = mid + kick - q * (m_mesh.Center * sc);
                Color gc = ok ? Mix(Premonition.Pop, Premonition.Success, k * 2f) : fail ? Premonition.Miss : main;
                m_mat.color = A(gc, alpha * Mathf.Lerp(0.12f, 0.75f, f * f) * (ok ? 1.6f * (1f - k) : 1f));
                // the block spot: two brackets at the marker's ends (the bar's place and size)
                m_b.Clear();
                m_g.Clear();
                float h = 0.3f, br = 0.05f;
                Color bc = A(main, alpha * (0.5f + 0.5f * f));
                for (int s = -1; s <= 1; s += 2)
                {
                    Vector2 e = new Vector2(s * h, 0f);
                    m_b.Stroke(e + new Vector2(0f, -br), e + new Vector2(0f, br), 0.01f, bc, 0.003f);
                    m_b.Stroke(e + new Vector2(0f, br), e + new Vector2(-s * br * 0.8f, br), 0.01f, bc, 0.003f);
                    m_b.Stroke(e + new Vector2(0f, -br), e + new Vector2(-s * br * 0.8f, -br), 0.01f, bc, 0.003f);
                    m_g.Stroke(e + new Vector2(0f, -br), e + new Vector2(0f, br), 0.03f, A(main, alpha * 0.3f * f), 0.02f, 0.002f);
                }
                if (valid) m_b.Stroke(new Vector2(-h, 0f), new Vector2(h, 0f), 0.004f, A(Premonition.Lined, alpha * 0.8f), 0.003f);
                m_b.To(m_body);
                m_g.To(m_glow);
            }
        }

        sealed class GhostDodge : IDodge
        {
            readonly Premonition.Arrow m_arrow;
            public GhostDodge(string name, Context c) { m_arrow = new Premonition.Arrow(name); }
            public Transform Root => m_arrow.Root;
            public void Draw(float age, float fill, float progress, string result, float resultAge) => m_arrow.Draw(age, fill, progress, result, resultAge);
            public void Destroy() => m_arrow.Destroy();
        }

        // ---------------------------------------------------------------------------------------- Gilded

        sealed class GildedBlock : IBlock
        {
            public Transform Root { get; }
            readonly Mesh m_glow, m_body;
            readonly Premonition.Builder m_b = new Premonition.Builder(), m_g = new Premonition.Builder();
            readonly float m_seed = UnityEngine.Random.value * 10f;
            public GildedBlock(string name)
            {
                Root = new GameObject(name).transform;
                m_glow = Part(Root, "glow", Premonition.Additive());
                m_body = Part(Root, "body", Premonition.Alpha());
            }
            public void Destroy() { if (Root != null) UnityEngine.Object.Destroy(Root.gameObject); UnityEngine.Object.Destroy(m_glow); UnityEngine.Object.Destroy(m_body); }

            static void Diamond(Premonition.Builder b, Vector2 c, float r, Color col, float z)
            {
                b.Stroke(c + new Vector2(-r, 0f), c + new Vector2(0f, r), 0.006f, col, 0.002f, z);
                b.Stroke(c + new Vector2(0f, r), c + new Vector2(r, 0f), 0.006f, col, 0.002f, z);
                b.Stroke(c + new Vector2(r, 0f), c + new Vector2(0f, -r), 0.006f, col, 0.002f, z);
                b.Stroke(c + new Vector2(0f, -r), c + new Vector2(-r, 0f), 0.006f, col, 0.002f, z);
            }

            public void Draw(float age, float fill, bool valid, bool inRange, string result, float resultAge, Color c)
            {
                // the look's own gold; a heavy or unblockable colour (IndicatorByType) tints the gilding
                Color gold = c == Premonition.Cyan ? GoldC : Mix(GoldC, c, 0.6f);
                Color main = valid ? Premonition.Lined : gold;
                float alpha, scale, k;
                bool ok, fail, fade;
                Envelope(age, result, resultAge, ref main, out alpha, out scale, out k, out ok, out fail, out fade);
                if (ok) main = Mix(Premonition.Pop, Premonition.Success, Mathf.Clamp01(resultAge / 0.12f));
                if (!inRange && result == null) alpha *= 0.4f;
                Root.localScale = Vector3.one * scale;
                float h = 0.28f, f = result != null ? 1f : Mathf.Clamp01(fill), now = Time.unscaledTime;
                m_b.Clear();
                m_g.Clear();
                // the gilded frame: a double line, diamond end caps (the cards' corner ornaments), a dark lacquer between
                m_b.Stroke(new Vector2(-h, 0f), new Vector2(h, 0f), 0.026f, A(new Color(0.08f, 0.04f, 0.02f), alpha * 0.7f), 0.004f, 0.001f);
                m_b.Stroke(new Vector2(-h, 0.016f), new Vector2(h, 0.016f), 0.004f, A(main, alpha), 0.002f);
                m_b.Stroke(new Vector2(-h, -0.016f), new Vector2(h, -0.016f), 0.004f, A(main, alpha), 0.002f);
                Diamond(m_b, new Vector2(-h - 0.02f, 0f), 0.022f, A(main, alpha), -0.001f);
                Diamond(m_b, new Vector2(h + 0.02f, 0f), 0.022f, A(main, alpha), -0.001f);
                Diamond(m_b, Vector2.zero, 0.012f, A(main, alpha * 0.8f), -0.001f);
                // molten gold filling from the middle
                float fh = h * f;
                m_b.Stroke(new Vector2(-fh, 0f), new Vector2(fh, 0f), 0.014f, A(valid || ok ? main : Mix(main, Ember, 0.25f), alpha), 0.003f, -0.0005f);
                m_g.Stroke(new Vector2(-fh, 0f), new Vector2(fh, 0f), 0.05f, A(valid ? Premonition.Lined : ok ? Premonition.Success : Ember, alpha * (valid || ok ? 0.6f : 0.35f)), 0.03f, 0.002f);
                // embers rising off it, more as the blow nears (a burst on the block)
                int n = ok ? 18 : 8;
                for (int i = 0; i < n; i++)
                {
                    float ph = (now * (0.6f + 0.13f * i) + i * 0.37f + m_seed) % 1f;
                    float x = Mathf.Lerp(-h, h, (i * 0.618f + m_seed) % 1f);
                    float y = 0.02f + ph * (ok ? 0.25f * (0.4f + k) : 0.12f);
                    float ea = alpha * (ok ? (1f - k) : f) * (1f - ph);
                    m_g.Stroke(new Vector2(x, y), new Vector2(x + 0.004f, y + 0.012f), 0.007f, A(Ember, ea), 0.006f, 0.002f);
                }
                m_b.To(m_body);
                m_g.To(m_glow);
            }
        }

        sealed class GildedDodge : IDodge
        {
            public Transform Root { get; }
            readonly Mesh m_glow, m_body;
            readonly Premonition.Builder m_b = new Premonition.Builder(), m_g = new Premonition.Builder();
            public GildedDodge(string name)
            {
                Root = new GameObject(name).transform;
                m_glow = Part(Root, "glow", Premonition.Additive());
                m_body = Part(Root, "body", Premonition.Alpha());
            }
            public void Destroy() { if (Root != null) UnityEngine.Object.Destroy(Root.gameObject); UnityEngine.Object.Destroy(m_glow); UnityEngine.Object.Destroy(m_body); }
            public void Draw(float age, float fill, float progress, string result, float resultAge)
            {
                Color c = progress >= 1f && result == null ? Pale : Mix(Ember, GoldC, 0.5f);
                float alpha, scale, k;
                bool ok, fail, fade;
                Envelope(age, result, resultAge, ref c, out alpha, out scale, out k, out ok, out fail, out fade);
                Root.localScale = Vector3.one * scale;
                m_b.Clear();
                m_g.Clear();
                float f = result != null ? 1f : fill, now = Time.unscaledTime;
                for (int i = 0; i < 3; i++)
                {
                    float lit = Mathf.Clamp01((f * 3f - i) * 2.5f), y = i * 0.055f + (ok ? k * 0.2f : 0f), w = 0.07f - i * 0.006f;
                    Color ci = A(c, alpha * Mathf.Lerp(0.3f, 1f, lit));
                    m_b.Stroke(new Vector2(-w, y - w * 0.75f), new Vector2(0f, y), 0.012f, ci, 0.003f);
                    m_b.Stroke(new Vector2(w, y - w * 0.75f), new Vector2(0f, y), 0.012f, ci, 0.003f);
                    m_b.Stroke(new Vector2(-w * 0.8f, y - w * 0.75f - 0.012f), new Vector2(0f, y - 0.012f), 0.004f, A(GoldC, alpha * lit), 0.002f);
                    m_b.Stroke(new Vector2(w * 0.8f, y - w * 0.75f - 0.012f), new Vector2(0f, y - 0.012f), 0.004f, A(GoldC, alpha * lit), 0.002f);
                    // ember trails
                    float ph = (now * 1.3f + i * 0.31f) % 1f;
                    m_g.Stroke(new Vector2(0f, y - 0.02f - ph * 0.06f), new Vector2(0f, y - 0.03f - ph * 0.06f), 0.008f, A(Ember, alpha * lit * (1f - ph)), 0.008f, 0.002f);
                }
                m_b.To(m_body);
                m_g.To(m_glow);
            }
        }

        // ---------------------------------------------------------------------------------------- Ring

        sealed class RingBlock : IBlock
        {
            public Transform Root { get; }
            readonly Mesh m_glow, m_body;
            readonly Premonition.Builder m_b = new Premonition.Builder(), m_g = new Premonition.Builder();
            public RingBlock(string name)
            {
                Root = new GameObject(name).transform;
                m_glow = Part(Root, "glow", Premonition.Additive());
                m_body = Part(Root, "body", Premonition.Alpha());
            }
            public void Destroy() { if (Root != null) UnityEngine.Object.Destroy(Root.gameObject); UnityEngine.Object.Destroy(m_glow); UnityEngine.Object.Destroy(m_body); }
            public void Draw(float age, float fill, bool valid, bool inRange, string result, float resultAge, Color c)
            {
                Color main = valid ? Premonition.Lined : c;
                float alpha, scale, k;
                bool ok, fail, fade;
                Envelope(age, result, resultAge, ref main, out alpha, out scale, out k, out ok, out fail, out fade);
                if (!inRange && result == null) alpha *= 0.4f;
                Root.localScale = Vector3.one;
                float f = result != null ? 1f : Mathf.Clamp01(fill);
                // the closing ring: from 0.45 m down to the target ring (0.12 m) at the impact
                float target = 0.12f, rr = ok ? target + 0.25f * Mathf.Sqrt(k) : Mathf.Lerp(0.45f, target, f * f * (3f - 2f * f));
                m_b.Clear();
                m_g.Clear();
                m_b.Arc(target, 0.008f, 0f, 360f, A(main, alpha * 0.55f), 0.003f, 0.001f);
                m_b.Arc(rr, ok ? 0.006f : 0.012f, 0f, 360f, A(main, alpha * (ok ? 1f - k : 0.4f + 0.6f * f)), 0.004f);
                m_g.Arc(rr, 0.04f, 0f, 360f, A(main, alpha * (ok ? 1f - k : 0.15f + 0.35f * f)), 0.03f, 0.002f);
                // the line across the blow (the block's axis), the marker's full length
                m_b.Stroke(new Vector2(-0.3f, 0f), new Vector2(0.3f, 0f), valid ? 0.012f : 0.006f, A(main, alpha * (0.5f + 0.5f * f)), 0.003f);
                if (valid) m_g.Stroke(new Vector2(-0.3f, 0f), new Vector2(0.3f, 0f), 0.04f, A(Premonition.Lined, alpha * 0.5f), 0.03f, 0.002f);
                m_b.To(m_body);
                m_g.To(m_glow);
            }
        }

        sealed class RingDodge : IDodge
        {
            public Transform Root { get; }
            readonly Mesh m_glow, m_body;
            readonly Premonition.Builder m_b = new Premonition.Builder(), m_g = new Premonition.Builder();
            public RingDodge(string name)
            {
                Root = new GameObject(name).transform;
                m_glow = Part(Root, "glow", Premonition.Additive());
                m_body = Part(Root, "body", Premonition.Alpha());
            }
            public void Destroy() { if (Root != null) UnityEngine.Object.Destroy(Root.gameObject); UnityEngine.Object.Destroy(m_glow); UnityEngine.Object.Destroy(m_body); }
            public void Draw(float age, float fill, float progress, string result, float resultAge)
            {
                Color c = progress >= 1f && result == null ? Premonition.DodgeOk : Premonition.Dodge;
                float alpha, scale, k;
                bool ok, fail, fade;
                Envelope(age, result, resultAge, ref c, out alpha, out scale, out k, out ok, out fail, out fade);
                Root.localScale = Vector3.one;
                float f = result != null ? 1f : Mathf.Clamp01(fill), rr = Mathf.Lerp(0.3f, 0.1f, f);
                m_b.Clear();
                m_g.Clear();
                m_b.Arc(rr, 0.01f, 0f, 360f, A(c, alpha * (0.4f + 0.6f * f)), 0.004f);
                m_g.Arc(rr, 0.035f, 0f, 360f, A(c, alpha * 0.3f), 0.03f, 0.002f);
                // two chevrons inside the ring, pointing the way to go (local +y); the ring closes onto them
                float shift = ok ? 0.2f * k : 0f;
                for (int j = 0; j < 2; j++)
                {
                    float y = -0.02f + j * 0.045f + shift;
                    float ca = alpha * (j == 0 ? 0.5f + 0.5f * f : 0.8f + 0.2f * f);
                    foreach (float sx in new[] { -1f, 1f })
                    {
                        Vector2 e = new Vector2(0.05f * sx, y - 0.035f);
                        m_b.Stroke(e, new Vector2(0f, y), 0.015f, A(c, ca), 0.004f);
                        m_g.Stroke(e, new Vector2(0f, y), 0.035f, A(c, ca * 0.35f), 0.02f, 0.002f);
                    }
                }
                m_b.To(m_body);
                m_g.To(m_glow);
            }
        }

        // ---------------------------------------------------------------------------------------- Bracket

        sealed class BracketBlock : IBlock
        {
            public Transform Root { get; }
            readonly Mesh m_glow, m_body;
            readonly Premonition.Builder m_b = new Premonition.Builder(), m_g = new Premonition.Builder();
            public BracketBlock(string name)
            {
                Root = new GameObject(name).transform;
                m_glow = Part(Root, "glow", Premonition.Additive());
                m_body = Part(Root, "body", Premonition.Alpha());
            }
            public void Destroy() { if (Root != null) UnityEngine.Object.Destroy(Root.gameObject); UnityEngine.Object.Destroy(m_glow); UnityEngine.Object.Destroy(m_body); }
            public void Draw(float age, float fill, bool valid, bool inRange, string result, float resultAge, Color c)
            {
                Color main = valid ? Premonition.Lined : Mix(c, Steel, 0.35f);
                float alpha, scale, k;
                bool ok, fail, fade;
                Envelope(age, result, resultAge, ref main, out alpha, out scale, out k, out ok, out fail, out fade);
                if (!inRange && result == null) alpha *= 0.4f;
                Root.localScale = Vector3.one;
                float f = result != null ? 1f : Mathf.Clamp01(fill);
                // the brackets close in a little as the blow nears; on the block they clap together
                float h = ok ? Mathf.Lerp(0.3f, 0.04f, Mathf.Sqrt(k)) : 0.3f + 0.04f * (1f - f), br = 0.045f;
                Color bc = A(main, alpha * (0.45f + 0.55f * f));
                m_b.Clear();
                m_g.Clear();
                for (int s = -1; s <= 1; s += 2)
                {
                    Vector2 e = new Vector2(s * h, 0f);
                    m_b.Stroke(e + new Vector2(0f, -br), e + new Vector2(0f, br), 0.007f, bc, 0.002f);
                    m_b.Stroke(e + new Vector2(0f, br), e + new Vector2(-s * 0.03f, br), 0.007f, bc, 0.002f);
                    m_b.Stroke(e + new Vector2(0f, -br), e + new Vector2(-s * 0.03f, -br), 0.007f, bc, 0.002f);
                }
                // the thin line: dashed early, solid near the impact
                int dashes = 12;
                for (int i = 0; i < dashes; i++)
                {
                    float x0 = Mathf.Lerp(-h + 0.03f, h - 0.03f, i / (float)dashes), x1 = Mathf.Lerp(-h + 0.03f, h - 0.03f, (i + Mathf.Lerp(0.45f, 1f, f)) / dashes);
                    m_b.Stroke(new Vector2(x0, 0f), new Vector2(x1, 0f), valid ? 0.006f : 0.003f, A(main, alpha * (0.3f + 0.6f * f)), 0.002f);
                }
                if (valid || ok) m_g.Stroke(new Vector2(-h, 0f), new Vector2(h, 0f), 0.03f, A(main, alpha * 0.35f), 0.02f, 0.002f);
                m_b.To(m_body);
                m_g.To(m_glow);
            }
        }

        sealed class BracketDodge : IDodge
        {
            public Transform Root { get; }
            readonly Mesh m_body;
            readonly Premonition.Builder m_b = new Premonition.Builder();
            public BracketDodge(string name)
            {
                Root = new GameObject(name).transform;
                m_body = Part(Root, "body", Premonition.Alpha());
            }
            public void Destroy() { if (Root != null) UnityEngine.Object.Destroy(Root.gameObject); UnityEngine.Object.Destroy(m_body); }
            public void Draw(float age, float fill, float progress, string result, float resultAge)
            {
                Color c = progress >= 1f && result == null ? Premonition.DodgeOk : Premonition.Dodge;
                float alpha, scale, k;
                bool ok, fail, fade;
                Envelope(age, result, resultAge, ref c, out alpha, out scale, out k, out ok, out fail, out fade);
                Root.localScale = Vector3.one * scale;
                m_b.Clear();
                float f = result != null ? 1f : fill;
                for (int i = 0; i < 2; i++)
                {
                    float y = i * 0.05f + (ok ? 0.2f * k : 0f), w = 0.06f;
                    Color ci = A(c, alpha * Mathf.Lerp(0.3f, 1f, Mathf.Clamp01((f * 2f - i) * 2f)));
                    m_b.Stroke(new Vector2(-w, y - w * 0.7f), new Vector2(0f, y), 0.006f, ci, 0.002f);
                    m_b.Stroke(new Vector2(w, y - w * 0.7f), new Vector2(0f, y), 0.006f, ci, 0.002f);
                }
                m_b.To(m_body);
            }
        }
    }
}

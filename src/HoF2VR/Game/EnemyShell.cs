using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace HoF2VR
{
    /// `[Combat] EnemyStatus = Shell` (the user's pick of 2026-10-18 ~22:00, mockup captures/hud-options-2026-10-18/enemy_shell.png):
    /// the enemy's guard is a translucent blue-white shell glowing round its own body; its health is a warm glow on the body.
    ///  * The shell: a copy of each of the enemy's body meshes (its skinned meshes and the rigid armour pieces on them), pushed out
    ///    along the normals (Inner 1.6 cm, Outer 4 cm) and skinned to the enemy's own bones, drawn back faces only behind the body:
    ///    what shows is a band round the silhouette - a bright additive inner edge and a softer blue outer one (the game's bloom
    ///    makes it glow). Nothing per vertex on the CPU per frame: the copies are built once per mesh (shared by every enemy that
    ///    wears it) and the effects are blend shapes and material colours.
    ///  * Wearing down, in steps readable at 2-5 m (the coordinator's review of 2026-10-19: "full" and "worn" looked the same):
    ///    whole (80-100 % guard) bright and 4 cm thick; worn (55-80 %) 62 % bright and 2.8 cm; low (30-55 %) 48 % and 2.1 cm;
    ///    nearly gone (< 30 %) 42 % and 1.6 cm, flickering (the band shapes thin it evenly; the v3 review of 2026-10-19: at 40 /
    ///    20 % the far ones looked unguarded - "low" must never read as "broken", which is no shell at all). And cracks: three sets of
    ///    jagged lines over the body (walked along the mesh's edges, skinned like it) open at 80 %, 55 % and 30 % guard; a hit
    ///    flashes it. Refilling (the guard growing back after 5 s without a hit) the shell pulses softly and the cracks close.
    ///  * The break: a bright flash, then the shell shatters - its own triangles fly off as glowing shards (0.7 s) - and is gone
    ///    while the crits are open (the gold slashes, as before).
    ///  * Re-forming (the stagger's recovery hold): the shell grows back from the feet up over the hold (four height bands of
    ///    the copy rise out of the body in turn), brighter while it forms, to the nearly-gone step - the enemy fights again with
    ///    an empty guard that grows back from there (one hit breaks it again).
    ///  * Near the player (the v3 review of 2026-10-19: within ~1.5 m the thick bright glow hid the body, the weapon and the
    ///    wind-up): by the eyes' distance to the enemy the shell fades to a thin faint rim - the full look from NearOut (2.6 m)
    ///    out, inside NearIn (1.3 m) NearThick of the step's thickness and NearBright of its brightness, the cracks dimmer and
    ///    narrower - and the arms' part of it (upper arm, forearm, hand: an "arms" blend shape, by the vertices' arm bone weights)
    ///    sinks into the body inside ArmsOut (2.6 m), gone inside ArmsIn (1.6 m): nothing round the weapon arm's motion. The
    ///    steps keep their ratios, so they still read at 2-3 m; the break's flash and shards are not faded at any distance.
    ///  * Health: the game's own character rim (the shader's RIM_ON fresnel, the one its green and red attack glows use), warm
    ///    pale gold-white (not amber: it must never be taken for the game's red "can't block" glow), from bright (full health) to
    ///    faint (nearly dead, then it throbs). While the game shows its own glow on the enemy
    ///    (green: its blow can be blocked; red: it cannot) the game's glow wins and the warm one waits (CharacterRim.Play).
    internal sealed class EnemyShell
    {
        // ---- the look (test seam `shell tune <key> <value>`) -----------------------------------------------------------------
        public static float Inner = 0.016f, Outer = 0.040f, CrackW = 0.02f;
        public static Color OuterC = new Color(0.16f, 0.42f, 1f), InnerC = new Color(0.36f, 0.64f, 1f), CrackC = new Color(0.86f, 0.95f, 1f);
        public static float OuterA = 0.68f, InnerK = 1.05f, CrackK = 2.8f, DimTo = 0.3f;
        // (P4-11, playtest 4: "they look like ghosts ... The glow should be decently faint") the whole steady look - the blue
        // band, its bright inner edge, the cracks - at Glow of what it was; a break still flashes in full
        // (the first try, 0.45 for all of it, left the bright inner edge nearly as it was at night - the bloom: the edge's
        // additive light at GlowEdge of that, the blue band's cover and the cracks at Glow)
        public static float Glow = 0.45f, GlowEdge = 0.25f;
        public static float ForceGuard = -1f;   // test seam `shell tune force <share>`: every shell shows that guard (the perf's worst case)
        public const int Bands = 4, Levels = 3;
        public static readonly float[] CrackAt = { 0.8f, 0.55f, 0.3f };
        // the steps (whole, worn, low, nearly gone): brightness and the band shapes' weight that thins the shell evenly
        // the glow (inner additive edge, bloom) fades by step; the blue outer band keeps most of its cover (StepAlpha) so that a
        // low guard still shows as a clear thin shell on snow and sky (v3 pictures: brightness alone left 40 / 20 % invisible at 3 m)
        public static readonly float[] StepBright = { 1f, 0.6f, 0.42f, 0.36f }, StepThick = { 0.040f, 0.031f, 0.025f, 0.021f }, StepAlpha = { 1f, 0.9f, 0.82f, 0.78f };
        // bd1946c's steps (`shell tune old 1` shows that look again, for before/after pictures from one moment)
        static readonly float[] OldBright = { 1f, 0.5f, 0.32f, 0.22f }, OldThin = { 0f, 38f, 58f, 70f };
        public static bool Old;
        // near the player: the full look from NearOut out; inside NearIn the step's thickness x NearThick, brightness x NearBright
        // (the additive inner edge x NearInner more), the cracks x NearCrack bright and NearCrackW open; the arms sink in
        // between ArmsOut and ArmsIn. Flicker: the nearly-gone step's brightness wavers by +-Flicker.
        public static float NearIn = 1.3f, NearOut = 2.6f, NearThick = 0.35f, NearBright = 0.4f, NearInner = 0.6f, NearCrack = 0.35f, NearCrackW = 0.5f;
        public static float ArmsIn = 1.6f, ArmsOut = 2.6f, Flicker = 0.32f;
        public static Color RimC = new Color(1f, 0.9f, 0.7f);
        public static float RimMax = 0.08f, RimMin = 0.008f, RimPow = 2.4f, RimK = 0.3f;   // RimK: P4-11, the health glow fainter too

        public static int HullsBuilt, HullFailures, Shatters, Reforms, RimApplies, GameRimYields;
        public static float BuildMs;
        public static string LastBuild = "-";

        // ---- the copies, one per source mesh -----------------------------------------------------------------------------
        sealed class Hull
        {
            public Mesh Mesh;
            public int N;                         // the source's vertices (each layer)
            public int BandShape0 = -1, CrackShape0 = -1, ArmShape = -1;
            public Mesh Thin;                     // rigid: a thin copy (near the player and the low steps)
            public int[] Cracks = new int[Levels];
            public bool Rigid;
            public string Name;
            public int[] InnerTris;
        }
        static readonly Dictionary<int, Hull> s_hulls = new Dictionary<int, Hull>();
        static readonly Dictionary<int, string> s_failed = new Dictionary<int, string>();

        sealed class Part
        {
            public Renderer Src;
            public SkinnedMeshRenderer Smr;        // skinned: our copy (under the shells' root)
            public MeshFilter SrcFilter;           // rigid: drawn with the source's matrix
            public Hull H;
            public int Band;                       // rigid: its height band
            public bool Arm;                       // rigid: hangs from an arm bone (hidden while the arms sink in)
            public float[] W = new float[Bands + Levels + 1];
            public bool On;
        }

        readonly List<Part> m_parts = new List<Part>();
        readonly List<Renderer> m_pending = new List<Renderer>();
        readonly List<Material> m_rim = new List<Material>();
        Material m_outer, m_inner, m_crackMat;
        Model m_model;
        int m_modelId;
        bool m_collected, m_rimOn, m_rimForce = true;
        long m_rimKey = long.MinValue;
        float m_lastGuard = 1f, m_flash, m_breakFlash, m_shownGuard = 1f, m_feetY, m_tall = 1.8f;
        Poise.Phase m_lastPh = Poise.Phase.Up;
        readonly float[] m_crack = new float[Levels];
        float m_step = 0f;   // the shown step (0 whole .. 3 nearly gone), eased
        bool m_sGuarded, m_sRegen;
        Poise.Phase m_sPh;
        float m_sRefill;
        /// The state as last drawn (diagnostics: `bars?`), built only when asked (the HUD audit of 2026-10-19, item 2: it was
        /// built every frame for every enemy, hidden ones too).
        public string State => !m_sGuarded ? "noguard" : m_sPh == Poise.Phase.Broken ? (m_breakFlash > 0f ? "breaking" : "broken") :
                               m_sPh == Poise.Phase.Recover ? "reforming " + m_sRefill.ToString("0.00") : (CracksOpen > 0 ? "cracked" + CracksOpen : "whole") + " step" + Step + (m_sRegen ? " refilling" : "");
        public int Visible, CracksOpen, Step;
        public float Brightness, Near = 1f, ArmsSunk, Thick;

        static Transform s_root;
        static Transform Root
        {
            get
            {
                // a scene object of the fight (it goes with the arena)
                if (s_root == null) s_root = new GameObject("HoF2VR_Shells").transform;
                return s_root;
            }
        }

        public EnemyShell(Model model)
        {
            m_model = model;
            m_modelId = model != null ? model.GetInstanceID() : 0;
        }

        static Material Mat(string name, bool additive, CullMode cull, int queue)
        {
            var m = new Material(Shader.Find("Hidden/Internal-Colored")) { name = name, renderQueue = queue };
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            m.SetInt("_ZWrite", 0);
            m.SetInt("_Cull", (int)cull);
            m.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            return m;
        }

        /// Linear colour -> what SetColor wants (material colours are read as sRGB in a linear project).
        static Color Lin(Color c)
        {
            if (QualitySettings.activeColorSpace != ColorSpace.Linear) return c;
            return new Color(Mathf.LinearToGammaSpace(Mathf.Max(0f, c.r)), Mathf.LinearToGammaSpace(Mathf.Max(0f, c.g)), Mathf.LinearToGammaSpace(Mathf.Max(0f, c.b)), c.a);
        }

        static bool CharacterShader(Material m)
        {
            if (m == null || m.shader == null) return false;
            string n = m.shader.name;
            return n.StartsWith("HoF/Character") || n.StartsWith("HoF/Cloth");
        }

        static readonly string[] s_weaponWords = { "sword", "sabre", "saber", "blade", "axe", "mace", "dagger", "knife", "spear", "staff", "bow", "arrow", "shield", "buckler",
                                                   "gun", "musket", "pistol", "rifle", "hammer", "club", "torch", "bottle", "bomb", "molotov", "scythe", "whip", "weapon", "flail", "bolas" };

        /// The enemy's weapons and what it carries get no shell (a glowing blade would read as a block marker): by the game's
        /// weapon shaders and by name.
        public static bool Weapon(Renderer r)
        {
            string n = r.name.ToLowerInvariant();
            foreach (string w in s_weaponWords) if (n.Contains(w)) return true;
            foreach (Material m in r.sharedMaterials) if (m != null && m.shader != null && m.shader.name.StartsWith("HoF/Weapon")) return true;
            return false;
        }

        static readonly string[] s_cardWords = { "hair", "fur", "feather", "beard", "lash", "brow", "plume", "mane", "tail", "fringe", "grass", "leaf" };
        static bool Cards(Renderer r)
        {
            string n = r.name.ToLowerInvariant();
            foreach (string w in s_cardWords) if (n.Contains(w)) return true;
            var smr = r as SkinnedMeshRenderer;
            Mesh m = smr != null ? smr.sharedMesh : (r.GetComponent<MeshFilter>() != null ? r.GetComponent<MeshFilter>().sharedMesh : null);
            return m == null || m.vertexCount < 150;
        }

        static bool BodyRenderer(Renderer r)
        {
            if (r == null || r.shadowCastingMode == ShadowCastingMode.ShadowsOnly || Weapon(r)) return false;
            if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) return false;
            Material[] ms = r.sharedMaterials;
            bool any = false;
            for (int i = 0; i < ms.Length; i++)
            {
                if (!CharacterShader(ms[i])) continue;
                any = true;
                // alpha-cut cards (hair, feathers, fur): a pushed-out copy of a card is a slab, not a shell (alpha-cut cloth - the
                // thieves' ragged cloaks - is in: it is most of their outline)
                if (ms[i].shader.name.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0 && Cards(r)) return false;
            }
            return any;
        }

        // ---- per frame -----------------------------------------------------------------------------------------------------
        public struct View
        {
            public float Alpha;          // the meter's fade (0 = not shown)
            public bool Guarded;         // Physical mode: the enemy has a guard
            public float Guard;          // share left
            public Poise.Phase Ph;
            public float Hold;           // Recover: the hold's progress (0..1)
            public bool Dead;            // dying (the meter's fade)
            public float Health;         // share left
            public Vector3 Feet;
            public float Tall;
            public Vector3 Eye;
        }

        /// One hull built per call across all enemies (the first frames of a fight: a few milliseconds each).
        public static int BuildBudget;

        public void Update(View v)
        {
            m_feetY = v.Feet.y;
            m_tall = Mathf.Max(0.5f, v.Tall);
            if (!m_collected) Collect();
            while (m_pending.Count > 0 && BuildBudget > 0)
            {
                Renderer r = m_pending[0];
                m_pending.RemoveAt(0);
                AddPart(r);
            }
            float dt = Time.unscaledDeltaTime, now = Time.unscaledTime;
            float g = v.Guarded ? Mathf.Clamp01(ForceGuard >= 0f ? ForceGuard : v.Guard) : 1f;
            // a hit on the guard: a flash
            if (v.Guarded && v.Ph == Poise.Phase.Up && g < m_lastGuard - 0.004f) m_flash = 1f;
            bool regen = v.Guarded && v.Ph == Poise.Phase.Up && g > m_lastGuard + 0.0001f && g < 0.999f;
            if (v.Guarded && v.Ph == Poise.Phase.Broken && m_lastPh != Poise.Phase.Broken) Shatter(v);
            bool enteredRecover = v.Guarded && v.Ph == Poise.Phase.Recover && m_lastPh != Poise.Phase.Recover;
            bool enteredUp = v.Guarded && v.Ph == Poise.Phase.Up && m_lastPh != Poise.Phase.Up;
            if (enteredRecover) Reforms++;
            m_lastPh = v.Guarded ? v.Ph : Poise.Phase.Up;
            m_lastGuard = g;
            m_flash = Mathf.Max(0f, m_flash - dt / 0.25f);
            m_breakFlash = Mathf.Max(0f, m_breakFlash - dt / 0.12f);
            m_shownGuard = Mathf.MoveTowards(m_shownGuard, g, dt * 2.5f);
            if (g < m_shownGuard) m_shownGuard = Mathf.Max(g, m_shownGuard - dt * 1.2f);
            if (enteredUp) m_shownGuard = g;

            // what the shell shows (a corpse killed while it recovered re-forms nothing)
            bool show = v.Guarded && v.Alpha > 0.01f && (v.Ph == Poise.Phase.Up || (v.Ph == Poise.Phase.Broken && m_breakFlash > 0f) || (v.Ph == Poise.Phase.Recover && !v.Dead));
            float bright = 0f, refill = 1f;
            // the step by the guard left (the cracks' thresholds); eased so a hit steps it down in ~0.15 s. Recover re-forms the
            // nearly-gone step (3), the one Up starts from (its guard grows from empty); the easing only runs within a phase (the
            // HUD audit of 2026-10-19, item 4: m_step eased 0 -> 3 at Up's start, a whole bright shell flashing on for ~0.4 s)
            Step = !v.Guarded ? 0 : v.Ph == Poise.Phase.Recover ? 3 : v.Ph != Poise.Phase.Up ? 0 :
                   m_shownGuard >= CrackAt[0] ? 0 : m_shownGuard >= CrackAt[1] ? 1 : m_shownGuard >= CrackAt[2] ? 2 : 3;
            m_step = enteredUp || enteredRecover ? Step : Mathf.MoveTowards(m_step, Step, dt * 7f);
            int s0 = Mathf.FloorToInt(m_step), s1 = Mathf.Min(3, s0 + 1);
            float sf = m_step - s0;
            // near the player: the eyes' distance to the enemy (level) -> 0 (inside NearIn) .. 1 (from NearOut out)
            Vector3 dv = v.Feet - v.Eye;
            dv.y = 0f;
            float dist = dv.magnitude;
            float near = Old ? 1f : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(NearIn, NearOut, dist));
            float arms = Old ? 0f : 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ArmsIn, ArmsOut, dist));
            Near = near;
            ArmsSunk = arms;
            // the thickness (outer layer, m) -> the band shapes' weight (100 = deflated 4 mm into the body)
            float thick = Mathf.Lerp(StepThick[s0], StepThick[s1], sf) * Mathf.Lerp(NearThick, 1f, near);
            float thin = Old ? Mathf.Lerp(OldThin[s0], OldThin[s1], sf) : Mathf.Clamp(100f * (Outer - thick) / (Outer + 0.004f), 0f, 100f);
            Thick = Old ? Outer - thin / 100f * (Outer + 0.004f) : thick;
            float alphaK = 1f;
            if (show)
            {
                float[] sb = Old ? OldBright : StepBright;
                if (v.Ph == Poise.Phase.Up)
                {
                    bright = Mathf.Lerp(sb[s0], sb[s1], sf) * (regen ? 0.88f + 0.18f * Mathf.Sin(now * 6f) : 1f);
                    // nearly gone: a thin shell that flickers (never mistaken for the broken one, which is gone)
                    float fl = Old ? 0f : Mathf.Clamp01(m_step - 2f);
                    float wob = fl > 0f ? 1f + Flicker * fl * (2f * Mathf.PerlinNoise(now * 7.5f, (m_modelId & 1023) * 0.37f) - 1f) : 1f;
                    bright *= wob * Mathf.Lerp(NearBright, 1f, near);
                    // the band's cover: its own step share of the full look (not the glow's), faded near like the glow
                    if (!Old && bright > 0f) alphaK = Mathf.Lerp(StepAlpha[s0], StepAlpha[s1], sf) * wob * Mathf.Lerp(NearBright, 1f, near) / bright;
                }
                else if (v.Ph == Poise.Phase.Recover)
                {
                    // the HUD audit of 2026-10-19, item 4: since the post-break regrow (the audit of 2026-10-18, item 2) the guard is
                    // empty through the whole hold, so the sweep (refill = the guard) never played and Recover looked broken.
                    // Now the shell grows back from the feet up over the hold, to the nearly-gone step's look (thin, flickering,
                    // cracked toward the end) that Up begins with - its glow brighter while it forms
                    refill = Mathf.Clamp01(v.Hold);
                    float fl = Old ? 0f : refill;
                    float wob = fl > 0f ? 1f + Flicker * fl * (2f * Mathf.PerlinNoise(now * 7.5f, (m_modelId & 1023) * 0.37f) - 1f) : 1f;
                    bright = sb[3] * (1f + 0.6f * (1f - refill)) * wob * Mathf.Lerp(NearBright, 1f, near);
                    if (!Old && bright > 0f) alphaK = StepAlpha[3] * wob * Mathf.Lerp(NearBright, 1f, near) / bright;
                }
                else bright = 1.6f;   // the break's flash (not faded near: unmistakable at any distance)
                bright *= v.Alpha * (1f + 0.7f * m_flash + 1.5f * m_breakFlash);
            }
            Brightness = bright;
            // the cracks: open below their guard share, close again above it (+4 %) while it refills
            CracksOpen = 0;
            for (int L = 0; L < Levels; L++)
            {
                // (Recover: they open over the hold's second half - Up begins at an empty guard, all three open)
                float target = v.Ph == Poise.Phase.Recover && v.Guarded ? 100f * Mathf.Clamp01((v.Hold - 0.5f) * 2f) :
                               v.Ph == Poise.Phase.Up && v.Guarded && m_shownGuard < CrackAt[L] ? 100f : (m_shownGuard > CrackAt[L] + 0.04f || v.Ph != Poise.Phase.Up ? 0f : m_crack[L]);
                if (target > 0f) target *= Mathf.Lerp(NearCrackW, 1f, near);
                m_crack[L] = Mathf.MoveTowards(m_crack[L], target, dt * (target > m_crack[L] ? 700f : 160f));
                if (m_crack[L] > 50f * Mathf.Lerp(NearCrackW, 1f, near)) CracksOpen++;   // (near, the cracks open only to NearCrackW)
            }
            if (m_outer == null)
            {
                m_outer = Mat("HoF2VR_ShellOuter", false, CullMode.Front, 3050);
                m_inner = Mat("HoF2VR_ShellInner", true, CullMode.Front, 3051);
                m_crackMat = Mat("HoF2VR_ShellCrack", true, CullMode.Off, 3052);
            }
            if (show)
            {
                float gk = Mathf.Lerp(Glow, 1f, Mathf.Clamp01(m_breakFlash));
                m_outer.SetColor("_Color", Lin(new Color(OuterC.r, OuterC.g, OuterC.b, Mathf.Clamp01(OuterA * gk * Mathf.Min(1.2f, bright * alphaK)))));
                float ek = Mathf.Lerp(GlowEdge, 1f, Mathf.Clamp01(m_breakFlash));
                Color ic = InnerC * (InnerK * ek * bright * (v.Ph == Poise.Phase.Broken ? 1f : Mathf.Lerp(NearInner, 1f, near)));
                ic.a = 1f;
                m_inner.SetColor("_Color", Lin(ic));
                Color cc = CrackC * (CrackK * gk * v.Alpha * (0.85f + 0.5f * m_flash) * Mathf.Lerp(NearCrack, 1f, near));
                cc.a = 1f;
                m_crackMat.SetColor("_Color", Lin(cc));
            }
            Visible = 0;
            for (int i = m_parts.Count - 1; i >= 0; i--)
            {
                Part p = m_parts[i];
                if (p.Src == null) { if (p.Smr != null) UnityEngine.Object.Destroy(p.Smr.gameObject); m_parts.RemoveAt(i); continue; }
                bool srcOn = p.Src.enabled && p.Src.gameObject.activeInHierarchy;
                bool on = show && srcOn;
                if (p.H.Rigid)
                {
                    // a rigid piece: shown while its height band is up (the re-forming sweep)
                    if (on && v.Ph == Poise.Phase.Recover && refill * Bands < p.Band + 0.5f) on = false;
                    // near: an arm's piece goes with the arms; the thin copy for a thin shell (no blend shapes on a drawn mesh)
                    if (on && p.Arm && arms > 0.5f && v.Ph != Poise.Phase.Broken) on = false;
                    if (on && p.SrcFilter != null)
                    {
                        Matrix4x4 mx = p.Src.transform.localToWorldMatrix;
                        int layer = p.Src.gameObject.layer;
                        Mesh rm = !Old && p.H.Thin != null && v.Ph != Poise.Phase.Broken && Thick < 0.025f ? p.H.Thin : p.H.Mesh;
                        Graphics.DrawMesh(rm, mx, m_outer, layer, null, 0, null, false, false);
                        Graphics.DrawMesh(rm, mx, m_inner, layer, null, 1, null, false, false);
                        Visible++;
                    }
                    continue;
                }
                if (p.Smr.enabled != on) p.Smr.enabled = on;
                if (!on) continue;
                Visible++;
                // the bands: deflated into the body (100) until the re-forming sweep reaches them; else thinned by the step
                float wMax = 0f;
                for (int b = 0; b < Bands; b++)
                {
                    float w = v.Ph == Poise.Phase.Recover ? Mathf.Max(thin, 100f * (1f - Mathf.Clamp01(refill * Bands - b))) : thin;
                    wMax = Mathf.Max(wMax, w);
                    SetW(p, b, p.H.BandShape0 + b, w);
                }
                for (int L = 0; L < Levels; L++)
                    if (p.H.Cracks[L] > 0) SetW(p, Bands + L, p.H.CrackShape0 + L, m_crack[L]);
                // the arms sink the rest of the way in (band + arms <= 100: deeper would poke out of thin fingers); the break's
                // shards are the whole shell
                if (p.H.ArmShape >= 0) SetW(p, Bands + Levels, p.H.ArmShape, v.Ph == Poise.Phase.Broken ? 0f : arms * (100f - wMax));
            }
            m_sGuarded = v.Guarded;
            m_sPh = v.Ph;
            m_sRefill = refill;
            m_sRegen = regen;
            Rim(v);
        }

        static void SetW(Part p, int slot, int shape, float w)
        {
            if (shape < 0) return;
            if (Mathf.Abs(p.W[slot] - w) < 0.5f && !(w == 0f && p.W[slot] != 0f)) return;
            p.W[slot] = w;
            p.Smr.SetBlendShapeWeight(shape, w);
        }

        public void Hide()
        {
            foreach (Part p in m_parts) if (p.Smr != null && p.Smr.enabled) p.Smr.enabled = false;
            Visible = 0;
            ReleaseRim();
        }

        public void Destroy()
        {
            foreach (Part p in m_parts) if (p.Smr != null) UnityEngine.Object.Destroy(p.Smr.gameObject);
            m_parts.Clear();
            ReleaseRim();
            if (m_outer != null) { UnityEngine.Object.Destroy(m_outer); UnityEngine.Object.Destroy(m_inner); UnityEngine.Object.Destroy(m_crackMat); }
        }

        // ---- the enemy's renderers ---------------------------------------------------------------------------------------
        void Collect()
        {
            m_collected = true;
            if (m_model == null) return;
            foreach (Renderer r in m_model.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.shadowCastingMode == ShadowCastingMode.ShadowsOnly || (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer))) continue;
                bool character = false;
                foreach (Material sm in r.sharedMaterials) if (CharacterShader(sm)) { character = true; break; }
                if (!character) continue;
                if (BodyRenderer(r)) m_pending.Add(r);
                // the health glow: the body's own materials with the game's rim
                Material[] mats = r.materials;
                for (int i = 0; i < mats.Length; i++) if (mats[i] != null && mats[i].HasProperty("_RimStrength") && CharacterShader(mats[i])) m_rim.Add(mats[i]);
            }
            // the biggest first: the body's shell appears before its buttons
            m_pending.Sort((a, b) => VertexCount(b).CompareTo(VertexCount(a)));
        }

        static int VertexCount(Renderer r)
        {
            try
            {
                var s = r as SkinnedMeshRenderer;
                if (s != null) return s.sharedMesh != null ? s.sharedMesh.vertexCount : 0;
                MeshFilter f = r.GetComponent<MeshFilter>();
                return f != null && f.sharedMesh != null ? f.sharedMesh.vertexCount : 0;
            }
            catch (Exception) { return 0; }
        }

        void AddPart(Renderer r)
        {
            if (r == null) return;
            var smr = r as SkinnedMeshRenderer;
            Mesh src = smr != null ? smr.sharedMesh : (r.GetComponent<MeshFilter>() != null ? r.GetComponent<MeshFilter>().sharedMesh : null);
            if (src == null || src.vertexCount < 24) return;
            int key = src.GetInstanceID() * 2 + (smr != null ? 0 : 1);
            if (s_failed.ContainsKey(key)) return;
            Hull h;
            if (!s_hulls.TryGetValue(key, out h) || h.Mesh == null)
            {
                BuildBudget--;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string why;
                h = smr != null ? BuildSkinned(smr, m_feetY, m_tall, out why) : BuildRigid(r, src, out why);
                sw.Stop();
                BuildMs += (float)sw.Elapsed.TotalMilliseconds;
                if (h == null)
                {
                    s_failed[key] = why;
                    HullFailures++;
                    Plugin.L.LogInfo("EnemyShell: no shell for " + src.name + ": " + why);
                    return;
                }
                s_hulls[key] = h;
                HullsBuilt++;
                LastBuild = h.Name + " " + h.N + " verts, cracks " + h.Cracks[0] + "/" + h.Cracks[1] + "/" + h.Cracks[2] + ", " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms";
                Plugin.L.LogInfo("EnemyShell: shell of " + LastBuild);
            }
            var p = new Part { Src = r, H = h };
            if (h.Rigid)
            {
                p.SrcFilter = r.GetComponent<MeshFilter>();
                for (Transform t = r.transform.parent; t != null && m_model != null && t != m_model.transform; t = t.parent)
                    if (ArmBone(t.name)) { p.Arm = true; break; }
                float y = r.bounds.center.y;
                p.Band = Mathf.Clamp(Mathf.FloorToInt((y - m_feetY) / m_tall * Bands), 0, Bands - 1);
            }
            else
            {
                var go = new GameObject("HoF2VR_Shell_" + src.name) { layer = r.gameObject.layer };
                go.transform.SetParent(Root, false);
                SkinnedMeshRenderer s = go.AddComponent<SkinnedMeshRenderer>();
                s.sharedMesh = h.Mesh;
                s.bones = smr.bones;
                s.rootBone = smr.rootBone;
                s.quality = smr.quality;
                s.updateWhenOffscreen = false;
                Bounds lb = smr.localBounds;
                lb.Expand(0.25f);
                s.localBounds = lb;
                s.sharedMaterials = new[] { m_outer ?? (m_outer = Mat("HoF2VR_ShellOuter", false, CullMode.Front, 3050)), m_inner ?? (m_inner = Mat("HoF2VR_ShellInner", true, CullMode.Front, 3051)),
                                            m_crackMat ?? (m_crackMat = Mat("HoF2VR_ShellCrack", true, CullMode.Off, 3052)) };
                s.shadowCastingMode = ShadowCastingMode.Off;
                s.receiveShadows = false;
                s.lightProbeUsage = LightProbeUsage.Off;
                s.reflectionProbeUsage = ReflectionProbeUsage.Off;
                s.enabled = false;
                p.Smr = s;
            }
            m_parts.Add(p);
        }

        // ---- building a skinned copy ---------------------------------------------------------------------------------------
        static readonly List<Vector3> s_v = new List<Vector3>(), s_n = new List<Vector3>();

        static Vector3 SkinPoint(Matrix4x4[] skin, BoneWeight w, Vector3 p)
        {
            Vector3 r = Vector3.zero;
            if (w.weight0 > 0f) r += skin[w.boneIndex0].MultiplyPoint3x4(p) * w.weight0;
            if (w.weight1 > 0f) r += skin[w.boneIndex1].MultiplyPoint3x4(p) * w.weight1;
            if (w.weight2 > 0f) r += skin[w.boneIndex2].MultiplyPoint3x4(p) * w.weight2;
            if (w.weight3 > 0f) r += skin[w.boneIndex3].MultiplyPoint3x4(p) * w.weight3;
            float t = w.weight0 + w.weight1 + w.weight2 + w.weight3;
            return t > 1e-4f ? r / t : skin[w.boneIndex0].MultiplyPoint3x4(p);
        }

        /// An arm bone by its name (3ds Max Biped "Bip01 R UpperArm" / "Forearm" / "ForeTwist" / "Hand" / "Finger0", "RUpArmTwist";
        /// "hand_r", "LeftForeArm"...): from the upper arm down; not the clavicle or shoulder, not a weapon's handle or armour.
        public static bool ArmBone(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            if (n.Contains("handle") || n.Contains("armor") || n.Contains("armour") || n.Contains("clav") || n.Contains("shoulder") || n.Contains("cloth")) return false;
            return n.Contains("arm") || n.Contains("hand") || n.Contains("finger") || n.Contains("thumb") || n.Contains("wrist") || n.Contains("elbow") ||
                   n.Contains("foretwist") || n.Contains("fore twist");
        }

        static float ArmShare(BoneWeight w, bool[] arm)
        {
            float a = 0f, t = w.weight0 + w.weight1 + w.weight2 + w.weight3;
            if (w.weight0 > 0f && w.boneIndex0 < arm.Length && arm[w.boneIndex0]) a += w.weight0;
            if (w.weight1 > 0f && w.boneIndex1 < arm.Length && arm[w.boneIndex1]) a += w.weight1;
            if (w.weight2 > 0f && w.boneIndex2 < arm.Length && arm[w.boneIndex2]) a += w.weight2;
            if (w.weight3 > 0f && w.boneIndex3 < arm.Length && arm[w.boneIndex3]) a += w.weight3;
            return t > 1e-4f ? Mathf.Clamp01(a / t) : 0f;
        }

        static Hull BuildSkinned(SkinnedMeshRenderer src, float feetY, float tall, out string why)
        {
            why = null;
            Mesh sm = src.sharedMesh;
            if (!sm.isReadable) { why = "the mesh is not readable"; return null; }
            s_v.Clear(); s_n.Clear();
            sm.GetVertices(s_v);
            sm.GetNormals(s_n);
            int n = s_v.Count;
            if (s_n.Count != n)
            {
                Mesh tmp = UnityEngine.Object.Instantiate(sm);
                tmp.RecalculateNormals();
                s_n.Clear();
                tmp.GetNormals(s_n);
                UnityEngine.Object.Destroy(tmp);
                if (s_n.Count != n) { why = "no normals"; return null; }
            }
            BoneWeight[] bw = sm.boneWeights;
            Matrix4x4[] bp = sm.bindposes;
            Transform[] bones = src.bones;
            if (bw == null || bw.Length != n || bp == null || bp.Length == 0 || bones == null || bones.Length != bp.Length) { why = "no skin (" + (bw != null ? bw.Length : 0) + " weights, " + (bp != null ? bp.Length : 0) + " bind poses, " + (bones != null ? bones.Length : 0) + " bones)"; return null; }
            // the bones now: mesh units -> world, and the scale (metres per mesh unit)
            var skin = new Matrix4x4[bones.Length];
            var scales = new List<float>();
            for (int i = 0; i < bones.Length; i++)
            {
                skin[i] = bones[i] != null ? bones[i].localToWorldMatrix * bp[i] : Matrix4x4.identity;
                if (bones[i] != null) scales.Add(((Vector3)skin[i].GetColumn(0)).magnitude);
            }
            if (scales.Count == 0) { why = "no bones"; return null; }
            scales.Sort();
            float k = Mathf.Max(1e-5f, scales[scales.Count / 2]);
            // each vertex's height share now (the bands of the re-forming sweep)
            var band = new int[n];
            var world = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                world[i] = SkinPoint(skin, bw[i], s_v[i]);
                band[i] = Mathf.Clamp(Mathf.FloorToInt((world[i].y - feetY) / tall * Bands), 0, Bands - 1);
            }
            var tris = new List<int>();
            for (int s = 0; s < sm.subMeshCount; s++)
                if (sm.GetTopology(s) == MeshTopology.Triangles) tris.AddRange(sm.GetTriangles(s));
            if (tris.Count < 3) { why = "no triangles"; return null; }

            // the cracks (bind pose, along the mesh's edges)
            var crackV = new List<Vector3>();
            var crackN = new List<Vector3>();
            var crackOpen = new List<Vector3>();
            var crackW = new List<BoneWeight>();
            var crackLevel = new List<int>();
            var crackT = new List<int>();
            var counts = new int[Levels];
            float area = 0f;
            for (int t = 0; t + 2 < tris.Count; t += 3) area += Vector3.Cross(world[tris[t + 1]] - world[tris[t]], world[tris[t + 2]] - world[tris[t]]).magnitude * 0.5f;
            int[] per = area >= 0.9f ? new[] { 2, 3, 4 } : area >= 0.3f ? new[] { 1, 2, 2 } : area >= 0.08f ? new[] { 0, 1, 1 } : new[] { 0, 0, 0 };
            if (per[0] + per[1] + per[2] > 0)
                Cracks(sm.name, s_v, s_n, bw, world, tris, k, feetY, tall, per, counts, crackV, crackN, crackOpen, crackW, crackLevel, crackT);

            int c = crackV.Count, total = 2 * n + c;
            var v = new Vector3[total];
            var w8 = new BoneWeight[total];
            var col = new Color32[total];
            float dO = Outer / k, dI = Inner / k;
            for (int i = 0; i < n; i++)
            {
                v[i] = s_v[i] + s_n[i] * dO;
                v[n + i] = s_v[i] + s_n[i] * dI;
                w8[i] = w8[n + i] = bw[i];
            }
            for (int i = 0; i < c; i++) { v[2 * n + i] = crackV[i]; w8[2 * n + i] = crackW[i]; }
            for (int i = 0; i < total; i++) col[i] = new Color32(255, 255, 255, 255);
            var mesh = new Mesh { name = "HoF2VR_Shell_" + sm.name };
            if (total > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = v;
            mesh.colors32 = col;
            mesh.boneWeights = w8;
            mesh.bindposes = bp;
            mesh.subMeshCount = 3;
            mesh.SetTriangles(tris, 0);
            var inner = new List<int>(tris.Count);
            for (int i = 0; i < tris.Count; i++) inner.Add(tris[i] + n);
            mesh.SetTriangles(inner, 1);
            var ct = new List<int>(crackT.Count);
            for (int i = 0; i < crackT.Count; i++) ct.Add(crackT[i] + 2 * n);
            mesh.SetTriangles(ct, 2);
            // blend shapes: each band deflated into the body; each crack level opened to its width
            var h = new Hull { Mesh = mesh, N = n, Name = sm.name };
            var delta = new Vector3[total];
            h.BandShape0 = 0;
            for (int b = 0; b < Bands; b++)
            {
                Array.Clear(delta, 0, total);
                for (int i = 0; i < n; i++)
                {
                    if (band[i] != b) continue;
                    delta[i] = -s_n[i] * (dO + 0.004f / k);
                    delta[n + i] = -s_n[i] * (dI + 0.004f / k);
                }
                mesh.AddBlendShapeFrame("band" + b, 100f, delta, null, null);
            }
            h.CrackShape0 = Bands;
            for (int L = 0; L < Levels; L++)
            {
                Array.Clear(delta, 0, total);
                for (int i = 0; i < c; i++) if (crackLevel[i] == L) delta[2 * n + i] = crackOpen[i];
                mesh.AddBlendShapeFrame("crack" + L, 100f, delta, null, null);
                h.Cracks[L] = counts[L];
            }
            // the arms (near the player, 2026-10-19 v3): every layer of the arm bones' share sunk 4 mm into the body
            var arm = new bool[bones.Length];
            int arms = 0;
            for (int i = 0; i < bones.Length; i++) if (bones[i] != null && ArmBone(bones[i].name)) { arm[i] = true; arms++; }
            if (arms > 0)
            {
                Array.Clear(delta, 0, total);
                int moved = 0;
                for (int i = 0; i < n; i++)
                {
                    float a = ArmShare(bw[i], arm);
                    if (a <= 0f) continue;
                    delta[i] = -s_n[i] * ((dO + 0.004f / k) * a);
                    delta[n + i] = -s_n[i] * ((dI + 0.004f / k) * a);
                    moved++;
                }
                for (int i = 0; i < c; i++)
                {
                    float a = ArmShare(crackW[i], arm);
                    if (a > 0f) delta[2 * n + i] = -crackN[i] * (((Outer + 0.003f) / k + 0.004f / k) * a);
                }
                if (moved > 0)
                {
                    h.ArmShape = mesh.blendShapeCount;
                    mesh.AddBlendShapeFrame("arms", 100f, delta, null, null);
                }
            }
            mesh.RecalculateBounds();
            h.InnerTris = inner.ToArray();
            return h;
        }

        /// Jagged lines walked along the mesh's edges from seeds spread by area over the body's middle (25-92 % of its height),
        /// each a thin ribbon on the inner shell, widest at its start; a branch from most of them. `per` = how many per level.
        static void Cracks(string name, List<Vector3> pos, List<Vector3> nrm, BoneWeight[] bw, Vector3[] world, List<int> tris, float k, float feetY, float tall, int[] per, int[] counts,
                           List<Vector3> outV, List<Vector3> outN, List<Vector3> outOpen, List<BoneWeight> outW, List<int> outLevel, List<int> outT)
        {
            int n = pos.Count;
            // weld by position (1 mm): walks cross the UV seams
            var weld = new int[n];
            var map = new Dictionary<long, int>();
            var rep = new List<int>();
            float q = 0.001f / k;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = pos[i];
                long key = ((long)Mathf.RoundToInt(p.x / q) * 73856093L) ^ ((long)Mathf.RoundToInt(p.y / q) * 19349663L) ^ ((long)Mathf.RoundToInt(p.z / q) * 83492791L);
                int id;
                if (!map.TryGetValue(key, out id)) { id = rep.Count; map[key] = id; rep.Add(i); }
                weld[i] = id;
            }
            int m = rep.Count;
            var adj = new List<int>[m];
            for (int i = 0; i < m; i++) adj[i] = new List<int>(6);
            for (int t = 0; t + 2 < tris.Count; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = weld[tris[t + e]], b = weld[tris[t + (e + 1) % 3]];
                    if (a == b) continue;
                    if (!adj[a].Contains(b)) adj[a].Add(b);
                    if (!adj[b].Contains(a)) adj[b].Add(a);
                }
            // seeds by area over the middle of the body
            var seedTris = new List<int>();
            var cum = new List<float>();
            float sum = 0f;
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                Vector3 a = world[tris[t]], b = world[tris[t + 1]], c = world[tris[t + 2]];
                float h01 = ((a.y + b.y + c.y) / 3f - feetY) / tall;
                if (h01 < 0.25f || h01 > 0.92f) continue;
                sum += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                seedTris.Add(t);
                cum.Add(sum);
            }
            if (seedTris.Count == 0) return;
            var rng = new System.Random(name.GetHashCode());
            for (int L = 0; L < Levels; L++)
                for (int s = 0; s < per[L]; s++)
                {
                    float r = (float)rng.NextDouble() * sum;
                    int ti = cum.BinarySearch(r);
                    if (ti < 0) ti = ~ti;
                    ti = Mathf.Clamp(ti, 0, seedTris.Count - 1);
                    int seed = weld[tris[seedTris[ti]]];
                    Vector3 sn = nrm[rep[seed]];
                    Vector3 any = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f);
                    Vector3 head = Vector3.ProjectOnPlane(any, sn).normalized;
                    if (head.sqrMagnitude < 0.5f) head = Vector3.ProjectOnPlane(Vector3.up, sn).normalized;
                    float len = 0.24f + 0.14f * (float)rng.NextDouble();
                    List<int> path = Walk(seed, head, len, pos, nrm, rep, adj, k, rng);
                    if (path.Count < 3) continue;
                    Ribbon(path, 1f, 0.3f, L, pos, nrm, bw, rep, k, outV, outN, outOpen, outW, outLevel, outT);
                    counts[L]++;
                    if (path.Count >= 6 && rng.NextDouble() < 0.8)
                    {
                        int at = path.Count * (4 + rng.Next(3)) / 10;
                        Vector3 bd = pos[rep[path[Mathf.Min(at + 1, path.Count - 1)]]] - pos[rep[path[at]]];
                        Vector3 bn = nrm[rep[path[at]]];
                        Vector3 side = Vector3.Cross(bn, bd).normalized * (rng.NextDouble() < 0.5 ? 1f : -1f);
                        List<int> br = Walk(path[at], (bd.normalized + side * 1.2f).normalized, len * 0.45f, pos, nrm, rep, adj, k, rng);
                        if (br.Count >= 3) Ribbon(br, 0.65f, 0.2f, L, pos, nrm, bw, rep, k, outV, outN, outOpen, outW, outLevel, outT);
                    }
                }
        }

        static List<int> Walk(int start, Vector3 head, float len, List<Vector3> pos, List<Vector3> nrm, List<int> rep, List<int>[] adj, float k, System.Random rng)
        {
            var path = new List<int> { start };
            var seen = new HashSet<int> { start };
            int cur = start;
            float done = 0f;
            Vector3 dir = head;
            while (done < len && path.Count < 48)
            {
                int best = -1;
                float bestScore = -9f;
                Vector3 pc = pos[rep[cur]];
                foreach (int nb in adj[cur])
                {
                    if (seen.Contains(nb)) continue;
                    Vector3 d = pos[rep[nb]] - pc;
                    float mg = d.magnitude;
                    if (mg < 1e-7f) continue;
                    float score = Vector3.Dot(d / mg, dir) + ((float)rng.NextDouble() - 0.5f) * 0.5f;
                    if (score > bestScore) { bestScore = score; best = nb; }
                }
                if (best < 0 || bestScore < -0.1f) break;
                Vector3 step = pos[rep[best]] - pc;
                done += step.magnitude * k;
                // the heading wanders (+-25 deg) and follows the surface
                Vector3 nn = nrm[rep[best]];
                dir = Vector3.ProjectOnPlane((dir + step.normalized) * 0.5f, nn).normalized;
                dir = Quaternion.AngleAxis(((float)rng.NextDouble() - 0.5f) * 50f, nn) * dir;
                cur = best;
                path.Add(cur);
                seen.Add(cur);
            }
            return path;
        }

        static void Ribbon(List<int> path, float w0, float w1, int level, List<Vector3> pos, List<Vector3> nrm, BoneWeight[] bw, List<int> rep, float k,
                           List<Vector3> outV, List<Vector3> outN, List<Vector3> outOpen, List<BoneWeight> outW, List<int> outLevel, List<int> outT)
        {
            int b0 = outV.Count, cnt = path.Count;
            for (int i = 0; i < cnt; i++)
            {
                int vi = rep[path[i]];
                Vector3 p = pos[vi], nn = nrm[vi].normalized;
                Vector3 prev = pos[rep[path[Mathf.Max(0, i - 1)]]], next = pos[rep[path[Mathf.Min(cnt - 1, i + 1)]]];
                Vector3 t = next - prev;
                Vector3 side = Vector3.Cross(nn, t).normalized;
                float half = CrackW * 0.5f / k * Mathf.Lerp(w0, w1, i / (float)(cnt - 1));
                Vector3 at = p + nn * ((Outer + 0.003f) / k);
                // both edges start on the centre line (closed: nothing drawn); the level's blend shape opens them
                outV.Add(at); outN.Add(nn); outOpen.Add(side * half); outW.Add(bw[vi]); outLevel.Add(level);
                outV.Add(at); outN.Add(nn); outOpen.Add(-side * half); outW.Add(bw[vi]); outLevel.Add(level);
            }
            for (int i = 0; i + 1 < cnt; i++)
            {
                int a0 = b0 + i * 2, a1 = a0 + 1, c0 = a0 + 2, c1 = a0 + 3;
                outT.Add(a0); outT.Add(c0); outT.Add(a1);
                outT.Add(a1); outT.Add(c0); outT.Add(c1);
            }
        }

        // ---- a rigid piece (a helmet, a pauldron on a bone): two pushed-out layers, drawn with its own matrix -----------------
        static Hull BuildRigid(Renderer r, Mesh sm, out string why)
        {
            why = null;
            if (!sm.isReadable) { why = "the mesh is not readable"; return null; }
            s_v.Clear(); s_n.Clear();
            sm.GetVertices(s_v);
            sm.GetNormals(s_n);
            int n = s_v.Count;
            if (s_n.Count != n) { why = "no normals"; return null; }
            Vector3 ls = r.transform.lossyScale;
            float k = Mathf.Max(1e-5f, (Mathf.Abs(ls.x) + Mathf.Abs(ls.y) + Mathf.Abs(ls.z)) / 3f);
            var tris = new List<int>();
            for (int s = 0; s < sm.subMeshCount; s++)
                if (sm.GetTopology(s) == MeshTopology.Triangles) tris.AddRange(sm.GetTriangles(s));
            if (tris.Count < 3) { why = "no triangles"; return null; }
            var v = new Vector3[2 * n];
            var col = new Color32[2 * n];
            for (int i = 0; i < n; i++)
            {
                v[i] = s_v[i] + s_n[i] * (Outer / k);
                v[n + i] = s_v[i] + s_n[i] * (Inner / k);
                col[i] = col[n + i] = new Color32(255, 255, 255, 255);
            }
            var mesh = new Mesh { name = "HoF2VR_ShellRigid_" + sm.name };
            mesh.vertices = v;
            mesh.colors32 = col;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(tris, 0);
            var inner = new List<int>(tris.Count);
            for (int i = 0; i < tris.Count; i++) inner.Add(tris[i] + n);
            mesh.SetTriangles(inner, 1);
            mesh.RecalculateBounds();
            // the thin copy (outer 1.1 cm, inner 0.3 cm): near the player and the low steps
            var vt = new Vector3[2 * n];
            for (int i = 0; i < n; i++)
            {
                vt[i] = s_v[i] + s_n[i] * (0.011f / k);
                vt[n + i] = s_v[i] + s_n[i] * (0.003f / k);
            }
            var thin = new Mesh { name = "HoF2VR_ShellRigidThin_" + sm.name };
            thin.vertices = vt;
            thin.colors32 = col;
            thin.subMeshCount = 2;
            thin.SetTriangles(tris, 0);
            thin.SetTriangles(inner, 1);
            thin.RecalculateBounds();
            return new Hull { Mesh = mesh, Thin = thin, N = n, Rigid = true, Name = sm.name + " (rigid)", InnerTris = inner.ToArray() };
        }

        // ---- the break: the shell's own triangles fly off ------------------------------------------------------------------
        sealed class Shard { public Vector3 A, B, C, Vel, Axis; public float Spin, Born, Life; }
        static readonly List<Shard> s_shards = new List<Shard>();
        static GameObject s_shardGo;
        static Mesh s_shardMesh, s_bake;
        static readonly List<Vector3> s_sv = new List<Vector3>();
        static readonly List<Color> s_sc = new List<Color>();
        static readonly List<int> s_st = new List<int>();
        public static int ShardsLive => s_shards.Count;

        void Shatter(View v)
        {
            Shatters++;
            m_breakFlash = 1f;
            Part big = null;
            foreach (Part p in m_parts) if (!p.H.Rigid && p.Smr != null && p.Src != null && p.Src.enabled && (big == null || p.H.N > big.H.N)) big = p;
            if (big == null) return;
            try
            {
                if (s_bake == null) s_bake = new Mesh { name = "HoF2VR_ShellBake" };
                big.Smr.BakeMesh(s_bake);
                s_sv.Clear();
                s_bake.GetVertices(s_sv);
                if (s_sv.Count < 2 * big.H.N) return;
                // BakeMesh's space: the renderer's, with or without its scale (Unity versions differ): the one that lands on the body
                Transform t = big.Smr.transform;
                Matrix4x4 a = t.localToWorldMatrix, b = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                Vector3 c0 = a.MultiplyPoint3x4(s_sv[big.H.N]), c1 = b.MultiplyPoint3x4(s_sv[big.H.N]);
                Vector3 want = big.Src.bounds.center;
                Matrix4x4 mx = (c0 - want).sqrMagnitude <= (c1 - want).sqrMagnitude ? a : b;
                int[] tri = big.H.InnerTris;
                if (tri == null || tri.Length < 3) return;
                var rng = new System.Random(Time.frameCount);
                float now = Time.unscaledTime;
                Vector3 centre = v.Feet + Vector3.up * (v.Tall * 0.55f);
                int count = Mathf.Min(110, tri.Length / 3);
                for (int i = 0; i < count; i++)
                {
                    int ti = rng.Next(tri.Length / 3) * 3;
                    Vector3 pa = mx.MultiplyPoint3x4(s_sv[tri[ti]]), pb = mx.MultiplyPoint3x4(s_sv[tri[ti + 1]]), pc = mx.MultiplyPoint3x4(s_sv[tri[ti + 2]]);
                    Vector3 cen = (pa + pb + pc) / 3f;
                    // a little bigger than the mesh's triangles: shards read as glass, not dust
                    float grow = 1.6f;
                    Vector3 outw = cen - centre;
                    outw.y *= 0.4f;
                    Vector3 vel = outw.normalized * (1.0f + 1.6f * (float)rng.NextDouble()) + Vector3.up * (0.6f + 1.2f * (float)rng.NextDouble());
                    s_shards.Add(new Shard
                    {
                        A = cen + (pa - cen) * grow, B = cen + (pb - cen) * grow, C = cen + (pc - cen) * grow, Vel = vel,
                        Axis = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f).normalized,
                        Spin = 360f + 540f * (float)rng.NextDouble(), Born = now, Life = 0.55f + 0.35f * (float)rng.NextDouble()
                    });
                }
                if (s_shards.Count > 400) s_shards.RemoveRange(0, s_shards.Count - 400);
                if (s_shardGo == null)
                {
                    s_shardGo = new GameObject("HoF2VR_ShellShards") { layer = big.Src.gameObject.layer };
                    s_shardMesh = new Mesh { name = "HoF2VR_ShellShards" };
                    s_shardMesh.MarkDynamic();
                    s_shardGo.AddComponent<MeshFilter>().sharedMesh = s_shardMesh;
                    var mr = s_shardGo.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = Mat("HoF2VR_ShellShard", true, CullMode.Off, 3053);
                    mr.sharedMaterial.SetColor("_Color", Lin(new Color(2.6f, 2.9f, 3.4f, 1f)));
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("EnemyShell: shatter: " + e.Message); }
        }

        /// The shards fly and fade (once per frame, for every enemy's).
        public static void ShardsTick()
        {
            if (s_shards.Count == 0)
            {
                if (s_shardGo != null && s_shardGo.activeSelf) s_shardGo.SetActive(false);
                return;
            }
            if (s_shardGo == null) { s_shards.Clear(); return; }
            if (!s_shardGo.activeSelf) s_shardGo.SetActive(true);
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            s_sv.Clear(); s_sc.Clear(); s_st.Clear();
            for (int i = s_shards.Count - 1; i >= 0; i--)
            {
                Shard s = s_shards[i];
                float age = now - s.Born;
                if (age >= s.Life) { s_shards.RemoveAt(i); continue; }
                s.Vel += Vector3.down * (3.5f * dt);
                Vector3 mv = s.Vel * dt, cen = (s.A + s.B + s.C) / 3f + mv;
                Quaternion q = Quaternion.AngleAxis(s.Spin * dt, s.Axis);
                s.A = cen + q * (s.A + mv - cen);
                s.B = cen + q * (s.B + mv - cen);
                s.C = cen + q * (s.C + mv - cen);
                float u = age / s.Life, a = (1f - u) * (1f - u);
                Color c = new Color(0.75f + 0.25f * (1f - u), 0.88f, 1f, a);
                int b = s_sv.Count;
                s_sv.Add(s.A); s_sv.Add(s.B); s_sv.Add(s.C);
                s_sc.Add(c); s_sc.Add(c); s_sc.Add(c);
                s_st.Add(b); s_st.Add(b + 1); s_st.Add(b + 2);
            }
            s_shardMesh.Clear();
            s_shardMesh.SetVertices(s_sv);
            s_shardMesh.SetColors(s_sc);
            s_shardMesh.SetTriangles(s_st, 0);
            s_shardMesh.RecalculateBounds();
        }

        /// A fight's end: the copies go (the arena's meshes are unloaded with it; the next fight's are new objects).
        public static void ClearCache()
        {
            foreach (Hull h in s_hulls.Values) if (h.Mesh != null) UnityEngine.Object.Destroy(h.Mesh);
            s_hulls.Clear();
            s_failed.Clear();
            s_gameRim.Clear();
            ClearShards();
        }

        public static void ClearShards()
        {
            s_shards.Clear();
            if (s_shardGo != null) s_shardGo.SetActive(false);
        }

        // ---- the health glow: the game's rim, warm ---------------------------------------------------------------------------
        static readonly Dictionary<int, bool> s_gameRim = new Dictionary<int, bool>();
        static AccessTools.FieldRef<CharacterRim, bool> s_rimEnabled;
        public static int GameRimPlays;

        public static void Apply(Harmony h)
        {
            try
            {
                s_rimEnabled = AccessTools.FieldRefAccess<CharacterRim, bool>("m_enabled");
                h.Patch(AccessTools.Method(typeof(CharacterRim), "Play", new[] { typeof(Model) }), postfix: new HarmonyMethod(typeof(EnemyShell), nameof(RimPlayed)));
            }
            catch (Exception e) { Plugin.L.LogWarning("EnemyShell: CharacterRim patch failed (the health glow then never yields to the game's): " + e.Message); }
        }

        static void RimPlayed(CharacterRim __instance, Model a_model)
        {
            try
            {
                if (a_model == null) return;
                GameRimPlays++;
                s_gameRim[a_model.GetInstanceID()] = s_rimEnabled != null && s_rimEnabled(__instance);
            }
            catch (Exception) { }
        }

        public static bool GameRimOn(Model m)
        {
            bool on;
            return m != null && s_gameRim.TryGetValue(m.GetInstanceID(), out on) && on;
        }

        public static void ForgetGameRims() { s_gameRim.Clear(); }

        void Rim(View v)
        {
            if (m_rim.Count == 0) return;
            if (GameRimOn(m_model))
            {
                if (m_rimOn) GameRimYields++;
                m_rimOn = false;
                m_rimForce = true;   // the game's glow set its own tint: ours again in full afterwards
                return;
            }
            float s = v.Alpha <= 0.01f ? 0f : Mathf.Lerp(RimMin, RimMax, Mathf.Clamp01(v.Health)) * v.Alpha * RimK;
            bool pulse = v.Health < 0.25f && v.Alpha > 0.01f;
            long key = Mathf.RoundToInt(s * 2000f) * 2 + (pulse ? 1 : 0);
            if (key == m_rimKey && !m_rimForce) return;
            m_rimKey = key;
            m_rimForce = false;
            RimApplies++;
            Color tint = Lin(RimC);
            foreach (Material m in m_rim)
            {
                if (m == null) continue;
                if (s <= 0f) { m.DisableKeyword("RIM_ON"); continue; }
                m.EnableKeyword("RIM_ON");
                m.SetTexture("_RimMatcap", Texture2D.whiteTexture);
                m.SetColor("_RimMatcapTint", tint);
                m.SetFloat("_RimPower", RimPow);
                m.SetFloat("_RimStrength", s);
                m.SetFloat("_RimBaseline", 0f);
                m.SetFloat("_PulseContribution", pulse ? 1f : 0f);
            }
            m_rimOn = s > 0f;
            RimStrength = s;
        }

        void ReleaseRim()
        {
            if (!m_rimOn) return;
            m_rimOn = false;
            m_rimForce = true;
            m_rimKey = long.MinValue;
            if (GameRimOn(m_model)) return;
            foreach (Material m in m_rim) if (m != null) m.DisableKeyword("RIM_ON");
        }

        /// `shell parts <enemy>`: each of the enemy's renderers, its shader and what it got (shell, rim, nothing and why).
        public static string PartsOf(Model model)
        {
            if (model == null) return "no model";
            var sb = new System.Text.StringBuilder();
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                Material[] ms = r.sharedMaterials;
                string sh = ms.Length > 0 && ms[0] != null && ms[0].shader != null ? ms[0].shader.name : "-";
                var smr = r as SkinnedMeshRenderer;
                Mesh mesh = smr != null ? smr.sharedMesh : (r.GetComponent<MeshFilter>() != null ? r.GetComponent<MeshFilter>().sharedMesh : null);
                string got = !BodyRenderer(r) ? (Weapon(r) ? "weapon" : r.shadowCastingMode == ShadowCastingMode.ShadowsOnly ? "shadow only" : "not body (" + sh + ")") : "shell";
                string why;
                if (got == "shell" && mesh != null && s_failed.TryGetValue(mesh.GetInstanceID() * 2 + (smr != null ? 0 : 1), out why)) got = "failed: " + why;
                sb.Append(" | ").Append(r.name).Append(smr != null ? " [skinned " : " [rigid ").Append(mesh != null ? mesh.vertexCount : 0).Append("] ").Append(sh)
                  .Append(r.enabled && r.gameObject.activeInHierarchy ? "" : " (off)").Append(" -> ").Append(got);
            }
            return sb.ToString();
        }

        public bool RimShown => m_rimOn;
        public float RimStrength;
        public int RimMats => m_rim.Count;
        public int Parts => m_parts.Count;
        public int Pending => m_pending.Count;

        public static string Describe() =>
            "hulls " + HullsBuilt + " (" + s_hulls.Count + " cached, " + HullFailures + " without, " + BuildMs.ToString("0") + " ms) shatters " + Shatters + " reforms " + Reforms +
            " shards " + s_shards.Count + " rimApplies " + RimApplies + " gameRimPlays " + GameRimPlays + " yields " + GameRimYields + " last " + LastBuild;

        /// `shell tune <key> <value>`.
        public static string Tune(string key, float val)
        {
            switch (key)
            {
                case "inner": Inner = val; break;
                case "outer": Outer = val; break;
                case "outera": OuterA = val; break;
                case "innerk": InnerK = val; break;
                case "crackk": CrackK = val; break;
                case "dim": DimTo = val; break;
                case "force": ForceGuard = val; break;
                case "old": Old = val > 0.5f; break;
                case "nearin": NearIn = val; break;
                case "nearout": NearOut = val; break;
                case "nearthick": NearThick = val; break;
                case "nearbright": NearBright = val; break;
                case "nearinner": NearInner = val; break;
                case "nearcrack": NearCrack = val; break;
                case "nearcrackw": NearCrackW = val; break;
                case "armsin": ArmsIn = val; break;
                case "armsout": ArmsOut = val; break;
                case "flicker": Flicker = val; break;
                case "step1": case "step2": case "step3":
                    { int i = key[4] - '0'; if (val >= 1f) StepBright[i] = val / 100f; else StepThick[i] = val; } break;
                case "alpha1": case "alpha2": case "alpha3": StepAlpha[key[5] - '0'] = val; break;
                case "rimmax": RimMax = val; break;
                case "rimmin": RimMin = val; break;
                case "rimpow": RimPow = val; break;
                case "glow": Glow = val; break;
                case "glowedge": GlowEdge = val; break;
                case "rimk": RimK = val; break;
                default: return "keys: inner outer (rebuild: next fight) outera innerk crackk dim rimmax rimmin rimpow force (every shell shows this guard share; -1 off) " +
                                "glow (the steady look's share, P4-11) rimk (the health glow's share) old (1: bd1946c's look) nearin nearout nearthick nearbright nearinner nearcrack nearcrackw armsin armsout flicker stepN (<1: thickness m, >=1: brightness %) alphaN (the band's cover share)";
            }
            return key + " = " + val;
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// (from the work/limbo2 prototype, docs/loading-v2-design.md §1) The waiting space as a replica of the title's
    /// campfire clearing, built from the asset pack (LimboPack) with shaders that exist from the first
    /// frame of the boot (player data, Appendix B of loading-design.md):
    ///   Legacy Shaders/Diffuse      - logs, stones, stumps, rocks (lit by our own fire light + moon)
    ///   Nature/Terrain/Diffuse      - the forest floor (4 splats + control map); base map + Legacy Diffuse
    ///                                 if that shader is missing
    ///   Unlit/Premultiplied Colored - additive (vertex alpha 0): flames, glow, embers, glowing ash, the
    ///                                 progress lights (NGUI shader: tex * vertex colour, ZWrite off)
    ///   Sprites/Default             - alpha-blended foliage and the tree-wall backdrop (unlit; tinted by
    ///                                 a per-renderer _Color that fakes fire light and fog)
    /// Isolation from the game (the space must look the same at boot, at the table and after a snow arena):
    ///   * layer 7 only, its own camera (Limbo's), as today;
    ///   * LimboEnv on that camera swaps RenderSettings fog/ambient for its own render only (OnPreRender ->
    ///     OnPostRender; it has no image effects, so nothing reads them later);
    ///   * game lights lose layer 7 from their culling masks (rescanned twice a second while shown: the
    ///     arenas' environment managers instantiate new suns); our fire and moon lights see layer 7 only;
    ///   * built-in shaders only: none of the game's global _g* values or keywords (snow, wetness) reach it.
    /// Integration: Limbo.Build() calls LimboCamp.TryBuild(m_root, Layer) after creating its root and keeps
    /// its procedural parts only if this returns null; Limbo.Show(flavour) calls Place(boot); Limbo.Animate
    /// calls Tick(t); Limbo's camera gets LimboEnv. See the design doc for the exact diff.
    internal sealed class LimboCamp
    {
        public Transform Root;
        int m_layer;
        Light m_fire, m_moon;
        LimboPack m_pack;
        readonly List<Mesh> m_emisMeshes = new List<Mesh>();
        readonly List<Color32[]> m_emisBase = new List<Color32[]>();
        readonly List<Color32[]> m_emisNow = new List<Color32[]>();
        LimboProgressLights m_progress;
        float m_nextScrub;
        public LimboProgressLights Progress => m_progress;

        static Shader S(string name)
        {
            Shader s = Shader.Find(name);
            if (s == null) Plugin.L.LogWarning("limbo camp: shader '" + name + "' not found");
            return s;
        }

        /// null = no pack or something missing: the caller keeps the procedural space.
        public static LimboCamp TryBuild(Transform limboRoot, int layer)
        {
            LimboPack pack = LimboPack.TryLoad(LimboPack.DefaultPath);
            if (pack == null) return null;
            try
            {
                pack.ParseLayout();
                var c = new LimboCamp { m_pack = pack, m_layer = layer };
                c.Build(limboRoot);
                return c;
            }
            catch (System.Exception e)
            {
                Plugin.L.LogError("limbo camp: build failed - procedural waiting space: " + e);
                return null;
            }
        }

        GameObject Part(string name, Mesh mesh, Material mat, Transform parent)
        {
            var go = new GameObject(name) { layer = m_layer };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;   // ambient comes from LimboEnv's SH
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return go;
        }

        void Build(Transform limboRoot)
        {
            var root = new GameObject("HoF2VR_LimboCamp") { layer = m_layer };
            root.transform.SetParent(limboRoot, false);
            Root = root.transform;
            Shader diffuse = S("Legacy Shaders/Diffuse"), terrain = Shader.Find("Nature/Terrain/Diffuse"),
                   add = S("Unlit/Premultiplied Colored"), sprite = S("Sprites/Default");
            if (diffuse == null || add == null || sprite == null) throw new System.Exception("boot shaders missing");

            // ---- the ground: the title clearing's own terrain
            Mesh ground = m_pack.Mesh(m_pack.Floor.Mesh);
            if (ground != null)
            {
                Material gm;
                Texture2D ctrl = m_pack.Tex(m_pack.Floor.Control);
                if (terrain != null && ctrl != null && m_pack.Floor.Splat != null && m_pack.Floor.Splat.Length >= 4)
                {
                    gm = new Material(terrain);
                    gm.SetTexture("_Control", ctrl);
                    for (int i = 0; i < 4; i++)
                    {
                        gm.SetTexture("_Splat" + i, m_pack.Tex(m_pack.Floor.Splat[i]));
                        gm.SetTextureScale("_Splat" + i, m_pack.Floor.SplatTiling[i]);
                    }
                    gm.SetTexture("_MainTex", m_pack.Tex(m_pack.Floor.BaseTex));   // the shader's base map
                }
                else gm = new Material(diffuse) { mainTexture = m_pack.Tex(m_pack.Floor.BaseTex) };
                gm.color = new Color(0.85f, 0.85f, 0.85f, 1f);
                GameObject g = Part("ground", ground, gm, Root);
                if (ground.isReadable)
                {
                    try { m_groundHeights = new MeshHeights(ground, g.transform.localPosition, g.transform.localRotation, g.transform.localScale); }
                    catch (System.Exception e) { m_groundHeights = null; Plugin.L.LogWarning("limbo camp: ground heights unavailable (" + e.Message + ") - spots as built"); }
                }
            }

            // ---- props in their title poses (relative to the fire's ground)
            var mats = new Dictionary<string, Material>();
            foreach (LimboPack.Prop p in m_pack.Props)
            {
                Mesh mesh = m_pack.Mesh(p.Mesh);
                Texture2D main = m_pack.Tex(p.MainTex);
                if (mesh == null) continue;
                bool foliage = p.Kind == "foliage" || p.Kind == "backdrop" || p.Name.StartsWith("grass");
                string mk = (foliage ? "s:" : "d:") + p.MainTex;
                Material mat;
                if (!mats.TryGetValue(mk, out mat))
                {
                    mat = new Material(foliage ? sprite : diffuse) { mainTexture = main };
                    mat.mainTextureScale = p.Tiling;
                    if (foliage) mat.renderQueue = p.Kind == "backdrop" ? 2900 : 2950;
                    mats[mk] = mat;
                }
                GameObject go = Part(p.Name, mesh, mat, Root);
                go.transform.localPosition = p.Pos;
                go.transform.localRotation = p.Rot;
                go.transform.localScale = p.Scale;
                if (foliage && p.Kind != "backdrop") m_plants.Add(new KeyValuePair<GameObject, Vector3>(go, p.Pos));
                if (foliage)
                {
                    // unlit: fake the fire's light and the mist by distance (set once; the set never moves)
                    float df = new Vector2(p.Pos.x, p.Pos.z).magnitude;
                    float lit = Mathf.Clamp01(1.1f / (1f + df * df * 0.15f));
                    Color moon = new Color(0.16f, 0.19f, 0.26f), warm = new Color(0.95f, 0.55f, 0.25f);
                    Color c = Color.Lerp(moon, warm, lit) * (p.Kind == "backdrop" ? 0.55f : 1f);
                    c = Color.Lerp(c, Limbo.Mist, Mathf.Clamp01((df - 6f) / 18f));
                    c.a = 1f;
                    var mpb = new MaterialPropertyBlock();
                    mpb.SetColor("_Color", c);
                    go.GetComponent<MeshRenderer>().SetPropertyBlock(mpb);
                }
                // the glowing embers in the wood and the ash: an additive copy of the mesh with the
                // emission map, vertex colours = emission x flicker (updated in Tick)
                Texture2D emis = m_pack.Tex(p.EmisTex);
                if (emis != null && p.Emission > 0f && p.Kind != "backdrop" && mesh.isReadable)
                {
                    Mesh em = Object.Instantiate(mesh);
                    em.MarkDynamic();
                    int n = em.vertexCount;
                    var cols = new Color32[n];
                    m_emisMeshes.Add(em);
                    m_emisBase.Add(cols);
                    m_emisNow.Add(new Color32[n]);
                    var am = new Material(add) { mainTexture = emis, renderQueue = 3001 };
                    GameObject eg = Part(p.Name + "_glow", em, am, go.transform);
                    eg.transform.localScale = Vector3.one * 1.003f;   // a hair above the surface
                }
            }

            // ---- fire: lights, particles
            var fireGo = new GameObject("fire") { layer = m_layer };
            fireGo.transform.SetParent(Root, false);
            fireGo.transform.localPosition = new Vector3(0.05f, 0.25f, -0.05f);
            m_fire = fireGo.AddComponent<Light>();
            m_fire.type = LightType.Point;
            m_fire.color = m_pack.FireColour;
            m_fire.range = 7f;                 // the title's 2.5 m light only lit the logs; ours reaches the player
            m_fire.intensity = 1.6f;
            m_fire.cullingMask = 1 << m_layer;
            m_fire.renderMode = LightRenderMode.ForcePixel;
            m_fire.shadows = LightShadows.None;
            var moonGo = new GameObject("moon") { layer = m_layer };
            moonGo.transform.SetParent(Root, false);
            moonGo.transform.localRotation = Quaternion.Euler(50f, 200f, 0f);
            m_moon = moonGo.AddComponent<Light>();
            m_moon.type = LightType.Directional;
            m_moon.color = new Color(0.45f, 0.55f, 0.85f);
            m_moon.intensity = 0.3f;
            m_moon.cullingMask = 1 << m_layer;
            m_moon.shadows = LightShadows.None;
            LimboFire.Build(fireGo.transform, m_layer, m_pack, add);

            // sound: the title's own campfire loop at the fire (3D) and its forest night bed (2D, quiet).
            // At boot the game's mixer does not exist yet: plain sources; once AudioManager is up the
            // integration can route them to the game's Ambience group so the game's volume applies.
            // NOTE: the mod's AudioListener (VRAudio) stays at the head in the GAME world while the space
            // (at W0, 6 km below) is shown, so true 3D would be silent: both play 2D and the crackle is
            // panned/attenuated by hand from the fire's bearing to the limbo camera (Tick).
            m_crackle = Sound(fireGo, m_pack.Sound("snd/campfire"), 0.55f, 0f);
            m_forest = Sound(Root.gameObject, m_pack.Sound("snd/forest"), 0.22f, 0f);

            Stars(Root.parent, m_pack.Tex("vfx_glow_radial"), add);
            m_progress = new LimboProgressLights(m_layer, m_pack.Tex("vfx_glow_radial"), add, limboRoot);
            Plugin.L.LogInfo("limbo camp: built (" + m_pack.Props.Count + " props, ground " + (ground != null) + ", terrain shader " + (terrain != null) + ")");
        }

        AudioSource m_crackle, m_forest;

        /// Soft stars: the game's radial glow on quads 0.3-0.6 deg wide (6-11 px on the Quest, soft edges),
        /// static - today's hard 3-5 px quads with a fast twinkle read as pixels. One mesh, drawn first.
        void Stars(Transform limboRoot, Texture2D dot, Shader add)
        {
            if (dot == null) return;
            const int n = 160;
            var v = new Vector3[n * 4]; var uv = new Vector2[n * 4]; var c = new Color32[n * 4]; var t = new int[n * 6];
            var rnd = new System.Random(11);
            for (int i = 0; i < n; i++)
            {
                float az = (float)rnd.NextDouble() * 360f, el = Mathf.Lerp(14f, 80f, Mathf.Sqrt((float)rnd.NextDouble()));
                Vector3 dir = Quaternion.Euler(-el, az, 0f) * Vector3.forward, p = dir * 90f;
                float size = Mathf.Lerp(0.25f, 0.45f, (float)rnd.NextDouble());
                Vector3 r = Vector3.Cross(Vector3.up, dir).normalized * size, u = Vector3.Cross(dir, r).normalized * size;
                v[i * 4] = p - r - u; v[i * 4 + 1] = p - r + u; v[i * 4 + 2] = p + r + u; v[i * 4 + 3] = p + r - u;
                uv[i * 4] = new Vector2(0, 0); uv[i * 4 + 1] = new Vector2(0, 1); uv[i * 4 + 2] = new Vector2(1, 1); uv[i * 4 + 3] = new Vector2(1, 0);
                byte b = (byte)(255f * Mathf.Lerp(0.25f, 0.85f, (float)(rnd.NextDouble() * rnd.NextDouble())));
                var col = new Color32((byte)(b * 0.85f), (byte)(b * 0.9f), b, 0);
                c[i * 4] = c[i * 4 + 1] = c[i * 4 + 2] = c[i * 4 + 3] = col;
                t[i * 6] = i * 4; t[i * 6 + 1] = i * 4 + 1; t[i * 6 + 2] = i * 4 + 2; t[i * 6 + 3] = i * 4; t[i * 6 + 4] = i * 4 + 2; t[i * 6 + 5] = i * 4 + 3;
            }
            var m = new Mesh { name = "HoF2VR_LimboSoftStars", vertices = v, uv = uv, colors32 = c, triangles = t };
            m.RecalculateBounds();
            var go = Part("softstars", m, new Material(add) { mainTexture = dot, renderQueue = 2500 }, limboRoot);
            go.name = "HoF2VR_LimboSoftStars";
        }

        static AudioSource Sound(GameObject go, AudioClip clip, float volume, float spatial)
        {
            if (clip == null) return null;
            var a = go.AddComponent<AudioSource>();
            a.clip = clip;
            a.loop = true;
            a.volume = volume;
            a.spatialBlend = spatial;
            a.rolloffMode = AudioRolloffMode.Logarithmic;
            a.minDistance = 1.5f;
            a.maxDistance = 25f;
            a.dopplerLevel = 0f;
            a.playOnAwake = true;   // plays whenever the space is active (the root is switched on/off)
            return a;
        }

        /// Put the player's spot (Limbo's frame: feet at the root's origin, facing +z) at the boot spot
        /// (the title camera's: the fire 6.75 m ahead becomes the title campfire at the handover) or the
        /// near one (3.2 m from the fire). The progress lights go with the player, not the fire.
        public void Place(bool boot)
        {
            Vector4 s;
            // the start too stands a few steps from the fire, facing it: the title now puts the player beside the hero in
            // the clearing (TitleSpot), no longer up on the camera's hillside 5.7 m away
            string key = boot && !TitleSpot.Enabled ? "boot" : "near";
            if (!m_pack.Spots.TryGetValue(key, out s)) s = new Vector4(0f, 0f, -4f, 0f);
            if (Settings.FlatGround) s = LevelSpot(s, key);
            Quaternion r = Quaternion.Euler(0f, -s.w, 0f);
            Root.localRotation = r;
            Root.localPosition = -(r * new Vector3(s.x, s.y, s.z));
            m_progress.Place(Root.parent, boot ? 2.4f : 2.0f);
            try { QuietNear(s); }
            catch (System.Exception e) { Plugin.L.LogWarning("limbo camp: near ground not shaded: " + e.Message); }
        }

        // ---- the near ground (smoothness doc fix #10, OK'd by the user 2026-10-11) ---------------------------------
        // When a frame comes late the headset shows the last one again, turned with the head only: near detail is what
        // visibly slides and then jumps; far things hardly move. Plants within NearClear of the player's spot are put
        // away and the ground under the player fades into shadow (a soft dark disc draped on the ground).
        // Settings Comfort.QuietNearGround.
        public static float NearClear = 2.3f, ShadeRadius = 2.9f, ShadeAlpha = 0.6f;
        readonly List<KeyValuePair<GameObject, Vector3>> m_plants = new List<KeyValuePair<GameObject, Vector3>>();
        GameObject m_shade;
        Mesh m_shadeMesh;
        public static int PlantsHidden;

        void QuietNear(Vector4 s)
        {
            bool on = Settings.QuietNearGround;
            int hidden = 0;
            foreach (KeyValuePair<GameObject, Vector3> kv in m_plants)
            {
                if (kv.Key == null) continue;
                bool near = on && new Vector2(kv.Value.x - s.x, kv.Value.z - s.z).magnitude < NearClear;
                if (kv.Key.activeSelf == near) kv.Key.SetActive(!near);
                if (near) hidden++;
            }
            PlantsHidden = hidden;
            if (m_shade == null)
            {
                m_shadeMesh = new Mesh { name = "HoF2VR_NearShade" };
                m_shadeMesh.MarkDynamic();
                var mat = new Material(Shader.Find("Hidden/Internal-Colored")) { renderQueue = 2940, color = Color.white };
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                m_shade = Part("nearShade", m_shadeMesh, mat, Root);
            }
            m_shade.SetActive(on);
            if (!on) return;
            // rings x segments, draped on the clearing's ground (its own heights) a few cm up
            const int rings = 9, segs = 36;
            var v = new Vector3[1 + rings * segs];
            var c = new Color[v.Length];
            v[0] = new Vector3(s.x, Height(s.x, s.z, s.y) + 0.03f, s.z);
            c[0] = new Color(0f, 0f, 0f, ShadeAlpha);
            for (int i = 1; i <= rings; i++)
            {
                float f = (float)i / rings, r = ShadeRadius * f;
                float a = ShadeAlpha * Mathf.Pow(1f - f * f, 1.5f);
                for (int j = 0; j < segs; j++)
                {
                    float ang = j * Mathf.PI * 2f / segs;
                    float x = s.x + r * Mathf.Cos(ang), z = s.z + r * Mathf.Sin(ang);
                    int k = 1 + (i - 1) * segs + j;
                    v[k] = new Vector3(x, Height(x, z, s.y) + 0.03f, z);
                    c[k] = new Color(0f, 0f, 0f, a);
                }
            }
            var t = new List<int>();
            for (int j = 0; j < segs; j++) { t.Add(0); t.Add(1 + (j + 1) % segs); t.Add(1 + j); }
            for (int i = 1; i < rings; i++)
                for (int j = 0; j < segs; j++)
                {
                    int a0 = 1 + (i - 1) * segs + j, a1 = 1 + (i - 1) * segs + (j + 1) % segs;
                    int b0 = a0 + segs, b1 = a1 + segs;
                    t.Add(a0); t.Add(a1); t.Add(b1);
                    t.Add(a0); t.Add(b1); t.Add(b0);
                }
            m_shadeMesh.Clear();
            m_shadeMesh.vertices = v;
            m_shadeMesh.colors = c;
            m_shadeMesh.SetTriangles(t, 0);
            m_shadeMesh.RecalculateBounds();
            Plugin.L.LogInfo("limbo camp: near ground quiet (" + hidden + " plants within " + NearClear + " m put away, shade " + ShadeRadius + " m)");
        }

        float Height(float x, float z, float fallback)
        {
            if (m_groundHeights == null) return fallback;
            float h = m_groundHeights.At(x, z);
            return float.IsNaN(h) ? fallback : h;
        }

        MeshHeights m_groundHeights;
        readonly System.Collections.Generic.Dictionary<string, Vector4> m_levelSpots = new System.Collections.Generic.Dictionary<string, Vector4>();

        /// The nearest level ground to a spot (FlatSpot, on this clearing's own ground): the title camera's spot is on a
        /// hillside, the near spot at the edge of a step. Searched once per spot (the clearing never changes).
        Vector4 LevelSpot(Vector4 s, string name)
        {
            Vector4 done;
            if (m_levelSpots.TryGetValue(name, out done)) return done;
            Vector4 result = s;
            if (m_groundHeights != null)
            {
                // heights in the clearing's own frame (the camp root's local space: the fire's ground at the origin)
                System.Func<float, float, float> h = m_groundHeights.At;
                Vector2 best;
                float bh;
                string why;
                if (FlatSpot.Find(new Vector2(s.x, s.z), s.w, h, out best, out bh, out why)) result = new Vector4(best.x, bh, best.y, s.w);
                Plugin.L.LogInfo("limbo camp: the " + name + " spot " + why + " -> (" + result.x.ToString("0.00") + ", " + result.y.ToString("0.00") + ", " + result.z.ToString("0.00") + ")" +
                                 ", fire " + new Vector2(result.x, result.z).magnitude.ToString("0.00") + " m away");
            }
            m_levelSpots[name] = result;
            return result;
        }

        /// Each frame while shown (Limbo.LateUpdate).
        public void Tick(float t)
        {
            // the title's LightFlicker: smooth noise between min and max at 'speed'
            float k = Mathf.PerlinNoise(t * m_pack.FireSpeed * 0.35f, 0.5f);
            float inten = Mathf.Lerp(m_pack.FireMin, m_pack.FireMax, k) / m_pack.FireMax;
            m_fire.intensity = 1.6f * inten;
            if (Time.frameCount % 2 == 0)
                for (int i = 0; i < m_emisMeshes.Count; i++)
                {
                    Color32[] now = m_emisNow[i];
                    byte v = (byte)Mathf.Clamp(150f * inten + 40f * Mathf.PerlinNoise(t * 1.3f, i), 0f, 255f);
                    var c = new Color32((byte)(v), (byte)(v * 0.62f), (byte)(v * 0.3f), 0);
                    for (int j = 0; j < now.Length; j++) now[j] = c;
                    m_emisMeshes[i].colors32 = now;
                }
            m_progress.Tick(t);
            if (m_crackle != null && Limbo.I != null && Limbo.I.Cam != null)
            {
                Transform cam = Limbo.I.Cam.transform;
                Vector3 d = cam.InverseTransformPoint(m_fire.transform.position);
                float dist = d.magnitude;
                m_crackle.panStereo = Mathf.Clamp(d.x / Mathf.Max(0.5f, dist), -1f, 1f) * 0.8f;
                m_crackle.volume = 0.55f * Mathf.Clamp01(2.5f / Mathf.Max(1f, dist)) * (0.9f + 0.1f * inten);
            }
            if (Time.unscaledTime >= m_nextScrub)
            {
                m_nextScrub = Time.unscaledTime + 0.5f;
                LimboEnv.ScrubGameLights(m_layer, m_fire, m_moon);
            }
        }
    }

    /// The fire's particles, after the title's PFX_ENV_torch_fire_01_updated (start_scene: flames x3,
    /// glow, smoke, embers) - same textures, values tuned for a fire seen from 3-7 m in VR. Additive
    /// through the NGUI premultiplied shader: start colour alpha 0, fades go through the RGB.
    internal static class LimboFire
    {
        static Gradient Fade(Color c, float inT, float outT)
        {
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.black, 0f), new GradientColorKey(c, inT), new GradientColorKey(c, outT), new GradientColorKey(Color.black, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        static ParticleSystem Ps(string name, Transform parent, int layer, Material mat, ParticleSystemRenderMode mode)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = mode;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.maxParticleSize = 3f;
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;   // the camp root never moves while shown
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.playOnAwake = true;
            main.loop = true;
            main.prewarm = true;
            main.startColor = new Color(1f, 1f, 1f, 0f);
            return ps;
        }

        static void Sheet(ParticleSystem ps, int x, int y)
        {
            var ts = ps.textureSheetAnimation;
            ts.enabled = true;
            ts.numTilesX = x;
            ts.numTilesY = y;
            ts.animation = ParticleSystemAnimationType.WholeSheet;
            ts.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));
        }

        public static void Build(Transform fire, int layer, LimboPack pack, Shader add)
        {
            Material M(string tex, int q) { Texture2D t = pack.Tex(tex); return t == null ? null : new Material(add) { mainTexture = t, renderQueue = q }; }
            var warm = new Color(1f, 0.78f, 0.5f);

            Material flames = M("PFX_SEQ_fire_03", 3100);
            if (flames != null)
            {
                ParticleSystem ps = Ps("flames", fire, layer, flames, ParticleSystemRenderMode.VerticalBillboard);
                var m = ps.main;
                m.startLifetime = new ParticleSystem.MinMaxCurve(0.9f, 1.3f);
                m.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 0.25f);
                m.startSize = new ParticleSystem.MinMaxCurve(0.45f, 0.65f);
                m.startRotation = new ParticleSystem.MinMaxCurve(-0.15f, 0.15f);
                m.maxParticles = 12;
                var em = ps.emission; em.rateOverTime = 7f;
                var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Circle; sh.radius = 0.12f;
                sh.rotation = new Vector3(-90f, 0f, 0f);
                var col = ps.colorOverLifetime; col.enabled = true; col.color = Fade(warm * 1.1f, 0.1f, 0.6f);
                Sheet(ps, 8, 8);
                ps.Play();
            }
            Material windy = M("PFX_SEQ_windy_fire_2048_01", 3101);
            if (windy != null)
            {
                ParticleSystem ps = Ps("flames_big", fire, layer, windy, ParticleSystemRenderMode.VerticalBillboard);
                var m = ps.main;
                m.startLifetime = new ParticleSystem.MinMaxCurve(1.6f, 2.1f);
                m.startSpeed = 0f;
                m.startSize = new ParticleSystem.MinMaxCurve(0.8f, 1.0f);
                m.maxParticles = 3;
                var em = ps.emission; em.rateOverTime = 1.2f;
                var sh = ps.shape; sh.enabled = false;
                var col = ps.colorOverLifetime; col.enabled = true; col.color = Fade(warm * 0.8f, 0.15f, 0.7f);
                Sheet(ps, 8, 8);
                ps.transform.localPosition = new Vector3(0f, 0.2f, 0f);
                ps.Play();
            }
            Material glow = M("vfx_glow_radial", 3090);
            if (glow != null)
            {
                ParticleSystem ps = Ps("glow", fire, layer, glow, ParticleSystemRenderMode.Billboard);
                var m = ps.main;
                m.startLifetime = new ParticleSystem.MinMaxCurve(2f, 3f);
                m.startSpeed = 0.05f;
                m.startSize = new ParticleSystem.MinMaxCurve(1.6f, 2.2f);
                m.maxParticles = 5;
                var em = ps.emission; em.rateOverTime = 1.5f;
                var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 0.05f;
                var col = ps.colorOverLifetime; col.enabled = true; col.color = Fade(new Color(0.55f, 0.28f, 0.1f), 0.3f, 0.6f);
                ps.Play();
            }
            Material smoke = M("PFX_SEQ_cloud_smoke_04", 3080);
            if (smoke != null)
            {
                ParticleSystem ps = Ps("smoke", fire, layer, smoke, ParticleSystemRenderMode.VerticalBillboard);
                var m = ps.main;
                m.startLifetime = new ParticleSystem.MinMaxCurve(3f, 4f);
                m.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 0.7f);
                m.startSize = new ParticleSystem.MinMaxCurve(0.5f, 0.8f);
                m.maxParticles = 16;
                var em = ps.emission; em.rateOverTime = 3f;
                var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 6f; sh.radius = 0.15f;
                sh.rotation = new Vector3(-90f, 0f, 0f);
                var col = ps.colorOverLifetime; col.enabled = true; col.color = Fade(new Color(0.07f, 0.065f, 0.06f), 0.2f, 0.5f);
                var sz = ps.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 1.8f));
                Sheet(ps, 4, 4);
                ps.transform.localPosition = new Vector3(0f, 0.6f, 0f);
                ps.Play();
            }
            Material sparks = M("vfx_sparks_tiled_01", 3110);
            if (sparks != null)
            {
                ParticleSystem ps = Ps("embers", fire, layer, sparks, ParticleSystemRenderMode.Billboard);
                var m = ps.main;
                m.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 2.5f);
                m.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.3f);
                m.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.05f);
                m.gravityModifier = -0.03f;
                m.maxParticles = 40;
                var em = ps.emission; em.rateOverTime = 9f;
                var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 25f; sh.radius = 0.11f;
                sh.rotation = new Vector3(-90f, 0f, 0f);
                var col = ps.colorOverLifetime; col.enabled = true; col.color = Fade(new Color(1f, 0.6f, 0.25f) * 1.5f, 0.05f, 0.6f);
                var nz = ps.noise; nz.enabled = true; nz.strength = 0.35f; nz.frequency = 0.8f;
                Sheet(ps, 2, 2);
                ps.Play();
            }
        }
    }

    /// The progress indicator (docs/loading-v2-design.md §2): a shallow arc of 12 small fire-lights on the
    /// ground 2-2.4 m ahead of the player (about 35-40 deg below the horizon: in view when looking at the
    /// fire, never in the face). Lit left to right with LoadProgress.Value; the next one glows in
    /// proportion; unknown duration = a slow soft wave running along the arc. One mesh, 12 quads, vertex
    /// colours rewritten only when the shown value moves (no per-frame allocation).
    internal sealed class LimboProgressLights
    {
        const int N = 12;
        // 0.3 m above the player's floor (not 0.12): the clearing's ground rises toward the fire and buried them
        const float ArcDeg = 46f, Size = 0.07f, Lift = 0.3f;
        readonly Mesh m_mesh;
        readonly Transform m_t;
        readonly Color32[] m_cols = new Color32[N * 4];
        float m_shown = -1f;
        public float Value = -1f;          // < 0 = unknown (wave), else 0..1
        public bool Visible = true;

        public LimboProgressLights(int layer, Texture2D dot, Shader add, Transform limboRoot)
        {
            var go = new GameObject("HoF2VR_LimboProgress") { layer = layer };
            m_t = go.transform;
            m_t.SetParent(limboRoot, false);   // under the DontDestroyOnLoad root at once (the boot scene unloads)
            m_mesh = new Mesh { name = "HoF2VR_LimboProgress" };
            m_mesh.MarkDynamic();
            var v = new Vector3[N * 4];
            var uv = new Vector2[N * 4];
            var tri = new int[N * 6];
            for (int i = 0; i < N; i++)
            {
                uv[i * 4] = new Vector2(0, 0); uv[i * 4 + 1] = new Vector2(0, 1); uv[i * 4 + 2] = new Vector2(1, 1); uv[i * 4 + 3] = new Vector2(1, 0);
                tri[i * 6] = i * 4; tri[i * 6 + 1] = i * 4 + 1; tri[i * 6 + 2] = i * 4 + 2;
                tri[i * 6 + 3] = i * 4; tri[i * 6 + 4] = i * 4 + 2; tri[i * 6 + 5] = i * 4 + 3;
            }
            m_mesh.vertices = v;
            m_mesh.uv = uv;
            m_mesh.colors32 = m_cols;
            m_mesh.triangles = tri;
            go.AddComponent<MeshFilter>().sharedMesh = m_mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = new Material(add) { mainTexture = dot, renderQueue = 3120 };
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        /// Upright soft dots on an arc round the player's spot (Limbo's root frame), `dist` ahead.
        public void Place(Transform limboRoot, float dist)
        {
            m_t.SetParent(limboRoot, false);
            m_t.localPosition = Vector3.zero;
            m_t.localRotation = Quaternion.identity;
            var v = new Vector3[N * 4];
            for (int i = 0; i < N; i++)
            {
                float a = Mathf.Deg2Rad * Mathf.Lerp(-ArcDeg * 0.5f, ArcDeg * 0.5f, i / (N - 1f));
                Vector3 c = new Vector3(Mathf.Sin(a) * dist, Lift, Mathf.Cos(a) * dist);
                Vector3 right = new Vector3(Mathf.Cos(a), 0f, -Mathf.Sin(a)) * Size, up = Vector3.up * Size;
                v[i * 4] = c - right - up; v[i * 4 + 1] = c - right + up; v[i * 4 + 2] = c + right + up; v[i * 4 + 3] = c + right - up;
            }
            m_mesh.vertices = v;
            m_mesh.RecalculateBounds();
            m_shown = -2f;
        }

        public void Tick(float t)
        {
            float val = Visible ? Value : -3f;
            bool wave = Visible && Value < 0f;
            if (!wave && Mathf.Abs(val - m_shown) < 0.002f) return;
            m_shown = val;
            for (int i = 0; i < N; i++)
            {
                float k;
                if (!Visible) k = 0f;
                else if (wave) k = 0.15f + 0.5f * Mathf.Pow(Mathf.Max(0f, Mathf.Sin(t * 1.2f - i * 0.45f)), 6f);
                else k = 0.12f + 0.88f * Mathf.Clamp01(val * N - i);   // unlit ones stay as faint embers
                var c = new Color32((byte)(255f * k), (byte)(170f * k), (byte)(90f * k), 0);
                m_cols[i * 4] = m_cols[i * 4 + 1] = m_cols[i * 4 + 2] = m_cols[i * 4 + 3] = c;
            }
            m_mesh.colors32 = m_cols;
        }
    }

    /// On the waiting-space camera: its render sees our fog and ambient, not the active game scene's.
    /// RenderSettings are global (the active scene's); they are swapped only between this camera's
    /// OnPreRender and OnPostRender (per eye in multi-pass) and restored at once.
    internal sealed class LimboEnv : MonoBehaviour
    {
        public static Color FogColour = new Color(0.040f, 0.075f, 0.110f);
        public static float FogDensity = 0.045f;          // EXP2: ~10 % at 7 m, ~60 % at 21 m
        public static Color Ambient = new Color(0.05f, 0.062f, 0.095f);   // a little above the prototype's: the boot view
                                                                         // (20.9 mean) was darker than the title it hands over to (25.5)
        bool m_fog;
        FogMode m_mode;
        Color m_fogCol;
        float m_density;
        UnityEngine.Rendering.SphericalHarmonicsL2 m_probe, m_ours;
        UnityEngine.Rendering.AmbientMode m_amode;
        bool m_saved, m_oursMade;
        /// The ambient was swapped at least once since the space appeared (Limbo asks the GI to recompute the game's
        /// ambient when the space goes: see AfterShown).
        public static bool AmbientTouched;
        /// Test switch (`limboenv ambient 0|1`): swap the ambient for the space's render at all.
        public static bool SwapAmbient = true;

        void OnPreRender()
        {
            if (!m_oursMade)
            {
                m_ours = new UnityEngine.Rendering.SphericalHarmonicsL2();
                m_ours.AddAmbientLight(Ambient);
                m_oursMade = true;
            }
            m_fog = RenderSettings.fog; m_mode = RenderSettings.fogMode; m_fogCol = RenderSettings.fogColor; m_density = RenderSettings.fogDensity;
            m_amode = RenderSettings.ambientMode; m_probe = RenderSettings.ambientProbe;
            m_saved = true;
            // EXP2 is the only fog variant the built-in shaders kept (loader scene); NGUI/Sprites have no fog
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = FogColour;
            RenderSettings.fogDensity = FogDensity;
            if (SwapAmbient)
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Custom;
                RenderSettings.ambientProbe = m_ours;
                AmbientTouched = true;
            }
        }

        void OnPostRender()
        {
            if (!m_saved) return;
            m_saved = false;
            RenderSettings.fog = m_fog; RenderSettings.fogMode = m_mode; RenderSettings.fogColor = m_fogCol; RenderSettings.fogDensity = m_density;
            if (SwapAmbient) { RenderSettings.ambientMode = m_amode; RenderSettings.ambientProbe = m_probe; }
        }

        /// The space has gone (Limbo.Hide). Writing RenderSettings.ambientProbe back after each of the space's renders
        /// overrides the game's realtime-GI ambient with the copy read a moment before - while a new scene's environment
        /// (the title's night sky ambient: GameState_Init's EnvironmentStateManager.UpdateDynamicGI runs while the boot
        /// space is up) was still being computed asynchronously, that copy was the OLD scene's ambient, and the override
        /// stays until the game's next environment change. The title then had (almost) no ambient: only the fire, the
        /// character lights and a shadowed sun lit it, and the sun's shadow cascades - fitted to each eye's view - made
        /// whole areas switch between black and lit as the head turned (user 2026-10-11: "Things would change from
        /// completely dark/invisible to bright and back constantly"). So the GI recomputes the active scene's ambient
        /// from its own sky once the space is gone (what the game itself does on every environment change).
        public static void AfterShown()
        {
            if (!AmbientTouched) return;
            AmbientTouched = false;
            try
            {
                string before = Describe();
                if (RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Skybox) DynamicGI.UpdateEnvironment();
                Plugin.L.LogInfo("limbo: the game's ambient is recomputed from its sky (" + before + ")");
            }
            catch (System.Exception e) { Plugin.L.LogWarning("limbo: ambient refresh: " + e.Message); }
        }

        /// `ambient`: the game's current ambient (mode, the probe's average colour) - diagnostics.
        public static string Describe()
        {
            UnityEngine.Rendering.SphericalHarmonicsL2 p = RenderSettings.ambientProbe;
            var dc = new Color(p[0, 0], p[1, 0], p[2, 0]);
            return "ambient " + RenderSettings.ambientMode + " intensity " + RenderSettings.ambientIntensity.ToString("0.00") + " probe DC " +
                   dc.r.ToString("0.000") + "," + dc.g.ToString("0.000") + "," + dc.b.ToString("0.000") + " sky " + (RenderSettings.skybox != null ? RenderSettings.skybox.name : "-") +
                   " fog " + RenderSettings.fog + " " + RenderSettings.fogMode + " " + RenderSettings.fogDensity.ToString("0.000");
        }

        /// Game lights never light layer 7 (the game does not use it); new suns appear with environment
        /// changes, so this runs twice a second while the space is shown. Only directional lights can reach the
        /// space (it is 6 km below every game place), and the engine lists those that light a layer: no
        /// whole-world search (docs/smoothness-2026-10-11.md fix M3).
        public static void ScrubGameLights(int layer, Light a, Light b)
        {
            int bit = 1 << layer;
            foreach (Light l in Light.GetLights(LightType.Directional, layer))
                if (l != null && l != a && l != b && (l.cullingMask & bit) != 0) l.cullingMask &= ~bit;
        }
    }

    /// Ground heights of a mesh lying in the XZ plane (the clearing's terrain), by its triangles, bucketed in a 0.5 m grid
    /// (CPU only: the waiting space is placed while its objects are inactive, so no physics).
    internal sealed class MeshHeights
    {
        readonly Vector3[] m_v;
        readonly int[] m_t;
        readonly System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<int>> m_cells = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<int>>();
        const float Cell = 0.5f;

        public MeshHeights(Mesh m, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            Vector3[] v = m.vertices;
            m_v = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++) m_v[i] = pos + rot * Vector3.Scale(v[i], scale);
            m_t = m.triangles;
            for (int t = 0; t + 2 < m_t.Length; t += 3)
            {
                Vector3 a = m_v[m_t[t]], b = m_v[m_t[t + 1]], c = m_v[m_t[t + 2]];
                int x0 = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) / Cell), x1 = Mathf.FloorToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) / Cell);
                int z0 = Mathf.FloorToInt(Mathf.Min(a.z, Mathf.Min(b.z, c.z)) / Cell), z1 = Mathf.FloorToInt(Mathf.Max(a.z, Mathf.Max(b.z, c.z)) / Cell);
                if ((x1 - x0) * (z1 - z0) > 400) continue;   // a degenerate giant triangle
                for (int x = x0; x <= x1; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long k = ((long)x << 32) ^ (uint)z;
                        System.Collections.Generic.List<int> l;
                        if (!m_cells.TryGetValue(k, out l)) m_cells[k] = l = new System.Collections.Generic.List<int>();
                        l.Add(t);
                    }
            }
        }

        /// Height of the highest triangle over (x, z), NaN off the mesh.
        public float At(float x, float z)
        {
            long k = ((long)Mathf.FloorToInt(x / Cell) << 32) ^ (uint)Mathf.FloorToInt(z / Cell);
            System.Collections.Generic.List<int> l;
            if (!m_cells.TryGetValue(k, out l)) return float.NaN;
            float best = float.NaN;
            foreach (int t in l)
            {
                Vector3 a = m_v[m_t[t]], b = m_v[m_t[t + 1]], c = m_v[m_t[t + 2]];
                float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                if (Mathf.Abs(d) < 1e-9f) continue;
                float w1 = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / d;
                float w2 = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / d;
                float w3 = 1f - w1 - w2;
                if (w1 < -1e-4f || w2 < -1e-4f || w3 < -1e-4f) continue;
                float y = w1 * a.y + w2 * b.y + w3 * c.y;
                if (float.IsNaN(best) || y > best) best = y;
            }
            return best;
        }
    }
}

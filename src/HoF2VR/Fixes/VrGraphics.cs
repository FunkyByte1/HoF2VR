using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Defiant.Quality;
using HarmonyLib;
using Sapphire.PostProcess;
using UnityEngine;
using UnityEngine.XR;
using UnityStandardAssets.CinematicEffects;

namespace HoF2VR
{
    /// VR-only graphics overrides on top of the game's own quality settings. Nothing here writes the
    /// game's settings models or prefs: camera effects are adjusted after the game's
    /// CameraQualityManager.ApplyQualitySettings (postfix), engine-wide values (shadows, lights, LOD,
    /// grass) are re-asserted every frame while VR is on, so a flat session keeps the player's own
    /// settings untouched.
    ///
    /// Test command `gfx` (A/B on the real headset; "game" = the game's own value):
    ///   gfx                       current overrides and live values (also logs every effect camera)
    ///   gfx ao game|off|3|6|12|20 [half|full]      ambient occlusion samples / resolution
    ///   gfx aosrc game|gbuffer|depthnormals       AO input (GBuffer needs no extra geometry pass)
    ///   gfx smaa game|low|med|high|ultra|fxaa|off  anti-aliasing (game = TAA mapped to SMAA high)
    ///   gfx bloom game|lq|hq|off   gfx shadowdist game|M   gfx cascades game|0|2|4
    ///   gfx shadowres game|0..3    gfx shadows game|off|hard|soft    gfx lights game|N
    ///   gfx lod game|BIAS          gfx grass game|DENSITY [DIST]      gfx aniso game|N
    ///   gfx fx game|off            gfx taakw game|off                  gfx reset
    internal class VrGraphics : MonoBehaviour
    {
        public static VrGraphics I;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // camera-side overrides (null/negative = the game's)
        public static int AoSamples = -1;        // 0 = off, else 3/6/12/20
        public static int AoHalf = -1;           // 0 full, 1 half
        public static int AoSource = -1;         // AmbientOcclusion.OcclusionSource
        public static int Smaa = -1;             // 0..3 SMAA preset, 4 FXAA, 5 off
        public static int Bloom = -1;            // 0 off, 1 LQ, 2 HQ
        public static bool TaaKeywordOff;        // clear the global TAA_ON keyword (TAA never runs in VR)

        /// An engine-wide value the game may set at any time: re-asserted every frame while VR is on,
        /// remembering the game's latest own value so "game" puts it back.
        sealed class Knob
        {
            public string Name;
            public float Want = float.NaN;   // NaN = the game's value
            public Func<float> Get;
            public Action<float> Set;
            public Func<float, float, float> Combine = (game, want) => want;
            float m_game = float.NaN, m_last = float.NaN;

            public void Tick()
            {
                float cur = Get();
                if (float.IsNaN(m_last) || cur != m_last) m_game = cur;   // first look, or the game set it
                float target = float.IsNaN(Want) ? m_game : Combine(m_game, Want);
                if (cur != target) Set(target);
                m_last = float.IsNaN(Want) ? float.NaN : target;
            }

            public string Describe() => Name + "=" + (float.IsNaN(Want) ? "game" : Want.ToString("0.##", Inv)) + " (now " + Get().ToString("0.##", Inv) + ", game " + m_game.ToString("0.##", Inv) + ")";
        }

        readonly List<Knob> m_knobs = new List<Knob>();
        Knob m_shadowDist, m_cascades, m_shadowRes, m_shadows, m_lights, m_lod, m_vsync;
        public static float GrassDensity = float.NaN, GrassDistance = float.NaN;
        public static int Aniso = -1;
        public static bool FxOff;

        void Awake()
        {
            I = this;
            m_shadowDist = AddKnob("shadowdist", () => QualitySettings.shadowDistance, v => QualitySettings.shadowDistance = v, (g, w) => Mathf.Min(g, w));
            m_cascades = AddKnob("cascades", () => QualitySettings.shadowCascades, v => QualitySettings.shadowCascades = (int)v);
            m_shadowRes = AddKnob("shadowres", () => (float)QualitySettings.shadowResolution, v => QualitySettings.shadowResolution = (ShadowResolution)(int)v);
            m_shadows = AddKnob("shadows", () => (float)QualitySettings.shadows, v => QualitySettings.shadows = (ShadowQuality)(int)v);
            m_lights = AddKnob("lights", () => QualitySettings.pixelLightCount, v => QualitySettings.pixelLightCount = (int)v);
            m_lod = AddKnob("lod", () => QualitySettings.lodBias, v => QualitySettings.lodBias = v);
            m_vsync = AddKnob("vsync", () => QualitySettings.vSyncCount, v => QualitySettings.vSyncCount = (int)v);
            Diag.Commands["gfx"] = Command;
        }

        Knob AddKnob(string name, Func<float> get, Action<float> set, Func<float, float, float> combine = null)
        {
            var k = new Knob { Name = name, Get = get, Set = set };
            if (combine != null) k.Combine = combine;
            m_knobs.Add(k);
            return k;
        }

        void LateUpdate()
        {
            if (!XRSettings.enabled) return;
            foreach (Knob k in m_knobs) k.Tick();
            if (TaaKeywordOff) Shader.DisableKeyword("TAA_ON");
            if (Time.unscaledTime >= m_nextSlow)
            {
                m_nextSlow = Time.unscaledTime + 1f;
                ApplyGrass();
                if (Settings.VrAmbientOcclusion != m_aoApplied)
                {
                    if (m_aoApplied != (Settings.AoMode)(-1)) ReapplyCameras();   // the settings file changed
                    m_aoApplied = Settings.VrAmbientOcclusion;
                }
                if (FxOff && (!Effect.DisableNonEssentialEffects || EnvironmentEffectController.EnableEnvironmentEffect))
                {
                    Effect.DisableNonEssentialEffects = true;
                    EnvironmentEffectController.EnableEnvironmentEffect = false;
                }
            }
        }

        float m_nextSlow;
        Settings.AoMode m_aoApplied = (Settings.AoMode)(-1);

        // ---- terrain grass ---------------------------------------------------------------------
        readonly Dictionary<int, Vector2> m_grassOrig = new Dictionary<int, Vector2>();   // density, distance
        float m_grassAppliedD = float.NaN, m_grassAppliedR = float.NaN;

        void ApplyGrass()
        {
            bool changed = !Same(m_grassAppliedD, GrassDensity) || !Same(m_grassAppliedR, GrassDistance);
            foreach (Terrain t in Terrain.activeTerrains)
            {
                if (t == null) continue;
                int id = t.GetInstanceID();
                Vector2 orig;
                bool known = m_grassOrig.TryGetValue(id, out orig);
                if (!known) { orig = new Vector2(t.detailObjectDensity, t.detailObjectDistance); m_grassOrig[id] = orig; }
                if (known && !changed) continue;
                float d = float.IsNaN(GrassDensity) ? orig.x : orig.x * GrassDensity;
                float r = float.IsNaN(GrassDistance) ? orig.y : Mathf.Min(orig.y, GrassDistance);
                if (t.detailObjectDensity != d) t.detailObjectDensity = d;
                if (t.detailObjectDistance != r) t.detailObjectDistance = r;
            }
            m_grassAppliedD = GrassDensity;
            m_grassAppliedR = GrassDistance;
        }

        static bool Same(float a, float b) => (float.IsNaN(a) && float.IsNaN(b)) || a == b;

        // ---- camera effects (called from RenderFixes.VrQuality after the game's own apply) -----------
        static readonly Dictionary<int, int> s_aoSourceOrig = new Dictionary<int, int>();
        static readonly Dictionary<int, bool> s_aoAmbientOrig = new Dictionary<int, bool>();

        public static void ApplyCamera(CameraQualityManager q, bool allowsAA)
        {
            AmbientOcclusion ao = q.HBAO;
            if (ao != null && ao.enabled)
            {
                int id = ao.GetInstanceID();
                if (!s_aoSourceOrig.ContainsKey(id)) { s_aoSourceOrig[id] = (int)ao.settings.occlusionSource; s_aoAmbientOrig[id] = ao.settings.ambientOnly; }
                // the player's setting, unless a test override says otherwise
                Settings.AoMode mode = Settings.VrAmbientOcclusion;
                int samples = AoSamples >= 0 ? AoSamples : mode == Settings.AoMode.Off ? 0 : -1;
                int half = AoHalf >= 0 ? AoHalf : mode == Settings.AoMode.Half ? 1 : -1;
                if (samples == 0) ao.enabled = false;
                else
                {
                    if (samples > 0)
                    {
                        ao.settings.sampleCount = samples <= 3 ? AmbientOcclusion.SampleCount.Lowest : samples <= 6 ? AmbientOcclusion.SampleCount.Low :
                                                  samples <= 12 ? AmbientOcclusion.SampleCount.Medium : AmbientOcclusion.SampleCount.High;
                    }
                    if (half >= 0) ao.settings.downsampling = half == 1;
                    ao.settings.occlusionSource = (AmbientOcclusion.OcclusionSource)(AoSource >= 0 ? AoSource : s_aoSourceOrig[id]);
                    // keep the game's look: AO over the whole image, not only the ambient light
                    ao.settings.ambientOnly = AoSource >= 0 ? false : s_aoAmbientOrig[id];
                }
            }
            if (ao != null) FixDepthNormals(q.Camera ?? q.GetComponent<Camera>());
            if (q.AA != null && allowsAA && Smaa >= 0)
            {
                if (Smaa == 5) q.AA.enabled = false;
                else if (Smaa == 4) { q.AA.enabled = true; q.AA.method = 1; }
                else
                {
                    q.AA.enabled = true;
                    q.AA.method = 0;
                    var smaa = (SMAA)q.AA.current;
                    smaa.quality = SMAA.QualitySettings.presetQualitySettings[Smaa];
                    smaa.temporal.enabled = false;
                }
            }
            HoFUberPostProcessing post = q.Bloom;
            if (post != null && Bloom >= 0)
            {
                post.EnableBloom = Bloom > 0;
                post.UseLowQualityBloom = Bloom == 1;
            }
        }

        /// The AO effect asks its camera for a DepthNormals texture (an extra pass over all opaque
        /// geometry, per eye) unless it reads the G-buffer, and never takes that request back. Drop
        /// it when nothing on the camera needs it any more.
        static void FixDepthNormals(Camera cam)
        {
            if (cam == null || (cam.depthTextureMode & DepthTextureMode.DepthNormals) == 0) return;
            foreach (MonoBehaviour b in cam.GetComponents<MonoBehaviour>())
            {
                if (b == null || !b.enabled) continue;
                var ao = b as AmbientOcclusion;
                if (ao != null) { if (ao.settings.occlusionSource != AmbientOcclusion.OcclusionSource.GBuffer) return; continue; }
                string n = b.GetType().Name;
                if (n == "SSAOPro" || n == "ScreenSpaceAmbientOcclusion" || n == "EdgeDetection") return;
            }
            cam.depthTextureMode &= ~DepthTextureMode.DepthNormals;
            Plugin.L.LogInfo("VrGraphics: '" + cam.name + "' no longer renders a DepthNormals texture");
        }

        static readonly MethodInfo s_apply = AccessTools.Method(typeof(CameraQualityManager), "ApplyQualitySettings");

        /// Re-run the game's camera quality apply (and so our postfix) on every effect camera.
        static void ReapplyCameras()
        {
            foreach (CameraQualityManager q in FindObjectsOfType<CameraQualityManager>())
                try { s_apply.Invoke(q, null); } catch (Exception e) { Plugin.L.LogWarning("gfx: re-apply failed on " + q.name + ": " + e.GetBaseException().Message); }
        }

        // ---- command -------------------------------------------------------------------------------
        static float ParseOrGame(string s) => s == "game" ? float.NaN : float.Parse(s, Inv);

        string Command(string[] a)
        {
            string op = a.Length > 1 ? a[1] : "";
            string v = a.Length > 2 ? a[2] : "game";
            switch (op)
            {
                case "":
                    return Describe();
                case "reset":
                    AoSamples = AoHalf = AoSource = Smaa = Bloom = Aniso = -1;
                    TaaKeywordOff = FxOff = false;
                    GrassDensity = GrassDistance = float.NaN;
                    foreach (Knob k in m_knobs) k.Want = float.NaN;
                    ReapplyCameras();
                    ReapplyGeneral();
                    break;
                case "ao":
                    AoSamples = v == "game" ? -1 : v == "off" ? 0 : int.Parse(v);
                    AoHalf = a.Length > 3 ? (a[3] == "half" ? 1 : a[3] == "full" ? 0 : -1) : AoHalf;
                    ReapplyCameras();
                    break;
                case "aosrc":
                    AoSource = v == "game" ? -1 : v == "gbuffer" ? 2 : v == "depthnormals" ? 1 : v == "depth" ? 0 : int.Parse(v);
                    ReapplyCameras();
                    break;
                case "smaa":
                    Smaa = v == "game" ? -1 : v == "low" ? 0 : v == "med" ? 1 : v == "high" ? 2 : v == "ultra" ? 3 : v == "fxaa" ? 4 : v == "off" ? 5 : int.Parse(v);
                    ReapplyCameras();
                    break;
                case "bloom":
                    Bloom = v == "game" ? -1 : v == "off" ? 0 : v == "lq" ? 1 : v == "hq" ? 2 : int.Parse(v);
                    ReapplyCameras();
                    break;
                case "taakw":
                    TaaKeywordOff = v == "off";
                    if (!TaaKeywordOff) ReapplyCameras();
                    break;
                case "fx":
                    FxOff = v == "off";
                    if (!FxOff) ReapplyGeneral();
                    break;
                case "aniso":
                    Aniso = v == "game" ? -1 : int.Parse(v);
                    if (Aniso > 0) Texture.SetGlobalAnisotropicFilteringLimits(Aniso, Aniso);
                    else ReapplyGeneral();
                    break;
                case "grass":
                    GrassDensity = ParseOrGame(v);
                    GrassDistance = a.Length > 3 ? ParseOrGame(a[3]) : float.NaN;
                    break;
                case "shadows":
                    m_shadows.Want = v == "game" ? float.NaN : v == "off" ? 0 : v == "hard" ? 1 : v == "soft" ? 2 : float.Parse(v, Inv);
                    break;
                default:
                    Knob knob = m_knobs.Find(k => k.Name == op);
                    if (knob == null) return "unknown gfx option " + op;
                    knob.Want = ParseOrGame(v);
                    break;
            }
            string d = Describe();
            Plugin.L.LogInfo("gfx " + string.Join(" ", a, 1, a.Length - 1) + " -> " + d);
            return d;
        }

        /// The game's general settings apply (anisotropic filtering, effects) - re-run to restore them.
        static void ReapplyGeneral()
        {
            try
            {
                QualitySettingsManager m = QualitySettingsManager.Instance;
                if (m != null && m.GeneralSettings != null) m.GeneralSettings.OnApply();
            }
            catch (Exception e) { Plugin.L.LogWarning("gfx: general re-apply failed: " + e.GetBaseException().Message); }
        }

        string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendFormat(Inv, "ao={0}{1} aosrc={2} smaa={3} bloom={4} taakw={5} fx={6} aniso={7} grass={8}/{9} | ",
                AoSamples < 0 ? "game" : AoSamples == 0 ? "off" : AoSamples.ToString(), AoHalf < 0 ? "" : AoHalf == 1 ? " half" : " full",
                AoSource < 0 ? "game" : ((AmbientOcclusion.OcclusionSource)AoSource).ToString(), Smaa < 0 ? "game" : Smaa.ToString(),
                Bloom < 0 ? "game" : Bloom.ToString(), TaaKeywordOff ? "off" : "game", FxOff ? "off" : "game", Aniso < 0 ? "game" : Aniso.ToString(),
                float.IsNaN(GrassDensity) ? "game" : GrassDensity.ToString("0.##", Inv), float.IsNaN(GrassDistance) ? "game" : GrassDistance.ToString("0", Inv));
            foreach (Knob k in m_knobs) sb.Append(k.Describe()).Append(' ');
            var cams = new StringBuilder();
            foreach (CameraQualityManager q in FindObjectsOfType<CameraQualityManager>())
            {
                Camera c = q.GetComponent<Camera>();
                AmbientOcclusion ao = q.HBAO;
                cams.AppendFormat(Inv, "\n  '{0}' enabled={1} depthTex={2} path={3} | AO {4} | AA {5} | bloom {6}",
                    q.name, c != null && c.isActiveAndEnabled, c != null ? c.depthTextureMode.ToString() : "-", c != null ? c.actualRenderingPath.ToString() : "-",
                    ao == null ? "none" : (ao.enabled ? "on" : "off") + " samples=" + ao.settings.sampleCount + " half=" + ao.settings.downsampling + " src=" + ao.settings.occlusionSource +
                                          " ambientOnly=" + ao.settings.ambientOnly + " r=" + ao.settings.radius.ToString("0.##", Inv) + " i=" + ao.settings.intensity.ToString("0.##", Inv),
                    q.AA == null ? "none" : (q.AA.enabled ? "on" : "off") + " method=" + q.AA.method + (q.AA.method == 0 && q.AA.current is SMAA ? " preset=" + PresetName(((SMAA)q.AA.current).quality) : ""),
                    q.Bloom == null ? "none" : (q.Bloom.enabled ? "on" : "off") + " bloom=" + q.Bloom.EnableBloom + " lq=" + q.Bloom.UseLowQualityBloom);
            }
            Terrain[] ts = Terrain.activeTerrains;
            foreach (Terrain t in ts)
                if (t != null) cams.AppendFormat(Inv, "\n  terrain '{0}' detailDensity={1:0.##} detailDist={2:0}", t.name, t.detailObjectDensity, t.detailObjectDistance);
            Plugin.L.LogInfo("gfx state: " + sb + cams + "\n  shadows " + QualitySettings.shadows + " res " + QualitySettings.shadowResolution + " cascades " + QualitySettings.shadowCascades +
                             " dist " + QualitySettings.shadowDistance + " lights " + QualitySettings.pixelLightCount + " lod " + QualitySettings.lodBias + " TAA_ON " + Shader.IsKeywordEnabled("TAA_ON"));
            return sb.ToString();
        }

        static string PresetName(SMAA.QualitySettings q)
        {
            for (int i = 0; i < SMAA.QualitySettings.presetQualitySettings.Length; i++)
                if (SMAA.QualitySettings.presetQualitySettings[i].Equals(q)) return new[] { "low", "med", "high", "ultra" }[i];
            return "custom";
        }
    }
}

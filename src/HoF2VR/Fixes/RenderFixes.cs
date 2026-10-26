using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Defiant.Quality;
using Defiant.Quality.Models;
using HarmonyLib;
using Sapphire.PostProcess;
using UnityEngine;
using UnityEngine.XR;
using UnityStandardAssets.CinematicEffects;

namespace HoF2VR
{
    /// Rendering fixes needed as soon as Unity's VR is on:
    ///  * VR-hostile image effects are kept disabled (TAA crashes on Vulkan in stereo and smears
    ///    anyway; depth of field / motion blur / lens distortions / grain are wrong or nauseating
    ///    in a headset; SSR is screen-space and breaks between eyes).
    ///  * The game's camera scripts set Camera.fieldOfView every frame, which Unity refuses in VR
    ///    with a log warning per call. Those call sites are redirected to SetFov, which skips
    ///    stereo cameras.
    internal class RenderFixes : MonoBehaviour
    {
        static readonly HashSet<string> Hostile = new HashSet<string>
        {
            "TemporalAntiAliasing", "DepthOfField", "CameraMotionBlur", "MotionBlur", "ScreenSpaceReflection",
            "Fisheye", "Vortex", "Twirl", "TiltShift", "RadialBlurEffect", "FilmGrain", "NoiseAndGrain",
            "NoiseAndScratches", "VignetteAndChromaticAberration",
        };

        static readonly HashSet<int> s_reported = new HashSet<int>();
        float m_next;
        static Camera[] s_cams = new Camera[16];
        static readonly System.Collections.Generic.List<MonoBehaviour> s_behaviours = new System.Collections.Generic.List<MonoBehaviour>();

        public static void Apply(Harmony h)
        {
            MethodInfo setter = AccessTools.PropertySetter(typeof(Camera), "fieldOfView");
            MethodInfo replacement = AccessTools.Method(typeof(RenderFixes), nameof(SetFov));
            int patched = 0;
            foreach (string typeName in new[] { "CardCamera", "Defiant.Cameras.PlayerCamera", "TweenFOV", "GloryCamera" })
            {
                Type t = AccessTools.TypeByName(typeName);
                if (t == null) { Plugin.L.LogWarning("RenderFixes: type not found " + typeName); continue; }
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t))
                {
                    if (m.IsAbstract || m.GetMethodBody() == null) continue;
                    if (!PatchProcessor.ReadMethodBody(m).Any(p => p.Value is MethodInfo mi && mi == setter)) continue;
                    h.Patch(m, transpiler: new HarmonyMethod(typeof(RenderFixes), nameof(FovTranspiler)));
                    patched++;
                }
            }
            Plugin.L.LogInfo("RenderFixes: redirected fieldOfView in " + patched + " methods");

            // Belt and braces for the effects: the Update() scan below can miss the first frame of a
            // freshly enabled camera, and one TAA frame in stereo on Vulkan is a native crash.
            int fx = 0;
            foreach (string asmName in new[] { "Assembly-CSharp", "Assembly-CSharp-firstpass" })
            {
                Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == asmName);
                if (asm == null) continue;
                foreach (Type t in AccessTools.GetTypesFromAssembly(asm))
                {
                    if (!Hostile.Contains(t.Name) || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                    MethodInfo ori = AccessTools.DeclaredMethod(t, "OnRenderImage");
                    if (ori != null) { h.Patch(ori, prefix: new HarmonyMethod(typeof(RenderFixes), nameof(PassThrough))); fx++; }
                    foreach (string cb in new[] { "OnPreCull", "OnPreRender", "OnPostRender" })
                    {
                        MethodInfo m = AccessTools.DeclaredMethod(t, cb);
                        if (m != null) { h.Patch(m, prefix: new HarmonyMethod(typeof(RenderFixes), nameof(SkipInVr))); fx++; }
                    }
                }
            }
            Plugin.L.LogInfo("RenderFixes: guarded " + fx + " image-effect callbacks");

            // Camera-lens effects make no sense in front of eyes (chromatic fringes, dirt on the
            // "lens", aperture star glare). And TAA - the game's default AA - can't run in stereo,
            // so with it selected use the game's own SMAA instead of no anti-aliasing at all.
            h.Patch(AccessTools.Method(typeof(CameraQualityManager), "ApplyQualitySettings"),
                postfix: new HarmonyMethod(typeof(RenderFixes), nameof(VrQuality)));
            h.Patch(AccessTools.Method(typeof(SMAA), "OnRenderImage"),
                prefix: new HarmonyMethod(typeof(RenderFixes), nameof(SmaaProbe)));

            // In VR the desktop window is only a mirror. The game re-applies its saved resolution and
            // fullscreen mode at startup and from the options menu; every change rebuilds the Vulkan
            // swapchain, and rebuilding it while the headset is running deadlocked vkQueuePresentKHR.
            // The launcher starts the window at 1920x1080 windowed (matching the UI texture); keep it.
            foreach (var target in new[] { "Defiant.ResolutionManager:SetResolution", "UIGraphicsSettingsResolution:", "UIGraphicsSettingsFullScreen:" })
            {
                string[] parts = target.Split(':');
                Type t = AccessTools.TypeByName(parts[0]);
                if (t == null) { Plugin.L.LogWarning("RenderFixes: no type " + parts[0]); continue; }
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t))
                {
                    if (parts[1].Length > 0 ? m.Name != parts[1] : !CallsSetResolution(m)) continue;
                    h.Patch(m, prefix: new HarmonyMethod(typeof(RenderFixes), nameof(SkipInVrLogged)));
                    Plugin.L.LogInfo("RenderFixes: resolution changes blocked in VR: " + t.Name + "." + m.Name);
                }
            }
        }

        static bool CallsSetResolution(MethodInfo m)
        {
            if (m.GetMethodBody() == null) return false;
            try { return PatchProcessor.ReadMethodBody(m).Any(p => p.Value is MethodInfo mi && mi.DeclaringType == typeof(Screen) && mi.Name == "SetResolution"); }
            catch (Exception) { return false; }
        }

        static bool SkipInVrLogged(MethodBase __originalMethod)
        {
            if (!XRSettings.enabled) return true;
            Plugin.L.LogInfo("RenderFixes: skipped " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " (VR)");
            return false;
        }

        static bool PassThrough(MonoBehaviour __instance, object[] __args)
        {
            if (!XRSettings.enabled) return true;
            Graphics.Blit((RenderTexture)__args[0], (RenderTexture)__args[1]);
            if (__instance.enabled)
            {
                __instance.enabled = false;
                if (s_reported.Add(__instance.GetInstanceID()))
                    Plugin.L.LogInfo("RenderFixes: disabled " + __instance.GetType().Name + " on '" + __instance.name + "' (first frame)");
            }
            return false;
        }

        static bool SkipInVr() => !XRSettings.enabled;

        static readonly AccessTools.FieldRef<CameraQualityManager, bool> s_allowsAA =
            AccessTools.FieldRefAccess<CameraQualityManager, bool>("m_cameraAllowsAA");

        static void VrQuality(CameraQualityManager __instance)
        {
            if (!XRSettings.enabled) return;
            HoFUberPostProcessing post = __instance.Bloom;
            if (post != null)
            {
                post.EnableChromaticAberration = false;
                post.EnableVignetting = false;
                post.EnableLensDirt = false;
            }
            if (__instance.Glare != null) __instance.Glare.enabled = false;
            QualitySettingsManager q = QualitySettingsManager.Instance;
            if (q != null && q.CameraSettings.AntiAliasing == AntiAliasingSetting.TAA && __instance.AA != null && s_allowsAA(__instance))
            {
                __instance.AA.enabled = true;
                __instance.AA.method = 0;
                var smaa = (SMAA)__instance.AA.current;
                smaa.quality = SMAA.QualitySettings.presetQualitySettings[2];
                smaa.temporal.enabled = false;
                Plugin.L.LogInfo("RenderFixes: TAA selected -> SMAA on '" + __instance.name + "' (VR)");
            }
            VrGraphics.ApplyCamera(__instance, s_allowsAA(__instance));
        }

        static bool s_smaaLogged;
        static void SmaaProbe(Camera camera, RenderTexture source)
        {
            if (s_smaaLogged || !XRSettings.enabled) return;
            s_smaaLogged = true;
            Plugin.L.LogInfo("SMAA probe: camera '" + camera.name + "' pixel " + camera.pixelWidth + "x" + camera.pixelHeight
                + " source " + source.width + "x" + source.height + " eye " + camera.stereoActiveEye);
        }

        static IEnumerable<CodeInstruction> FovTranspiler(IEnumerable<CodeInstruction> code)
        {
            MethodInfo setter = AccessTools.PropertySetter(typeof(Camera), "fieldOfView");
            MethodInfo replacement = AccessTools.Method(typeof(RenderFixes), nameof(SetFov));
            foreach (CodeInstruction ci in code)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo mi && mi == setter)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = replacement;
                }
                yield return ci;
            }
        }

        public static void SetFov(Camera cam, float fov)
        {
            if (XRSettings.enabled && cam.stereoTargetEye != StereoTargetEyeMask.None) return;
            cam.fieldOfView = fov;
        }

        void Update()
        {
            if (!XRSettings.enabled || Time.unscaledTime < m_next) return;
            m_next = Time.unscaledTime + 0.5f;
            if (s_cams.Length < Camera.allCamerasCount) s_cams = new Camera[Camera.allCamerasCount + 8];
            int n = Camera.GetAllCameras(s_cams);   // (no new arrays every half second)
            for (int i = 0; i < n; i++)
            {
                Camera cam = s_cams[i];
                if (cam == null) continue;
                cam.GetComponents(s_behaviours);
                foreach (MonoBehaviour b in s_behaviours)
                {
                    if (b == null || !b.enabled || !Hostile.Contains(b.GetType().Name)) continue;
                    b.enabled = false;
                    if (s_reported.Add(b.GetInstanceID()))
                        Plugin.L.LogInfo("RenderFixes: disabled " + b.GetType().Name + " on '" + cam.name + "'");
                }
            }
        }
    }
}

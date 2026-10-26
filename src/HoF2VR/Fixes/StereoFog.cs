using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The game's distance/height fog (DistanceAndHeightFog, GlobalFog-style) rebuilds each pixel's
    /// world position from the depth buffer, a camera position and four frustum-corner rays. It builds
    /// those from the camera transform, fieldOfView and aspect: one symmetric frustum at the centre
    /// between the eyes. A headset eye has its own position (half the IPD to the side) and an
    /// asymmetric frustum, so every pixel's ray was off by a different amount in each eye: the fog
    /// thickness and the height-fog edge came out different between the eyes (seen at the misty title
    /// screen).
    ///
    /// In VR this prefix runs the effect with the rendering eye's own frustum (from its projection
    /// matrix) and position (from its view matrix); everything else is the game's code unchanged.
    /// `stereofog 0|1` switches it for A/B tests, `stereofog info` logs the eye matrices.
    internal static class StereoFog
    {
        public static bool Enabled = true;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static readonly AccessTools.FieldRef<DistanceAndHeightFog, Material> s_mat =
            AccessTools.FieldRefAccess<DistanceAndHeightFog, Material>("fogMaterial");

        static readonly int SunDirWS = Shader.PropertyToID("_SunDirectionWS");
        static readonly int SunColor = Shader.PropertyToID("_SunColor");
        static readonly int SunSpread = Shader.PropertyToID("_SunSpread");
        static readonly int CornersWS = Shader.PropertyToID("_FrustumCornersWS");
        static readonly int CameraWS = Shader.PropertyToID("_CameraWS");
        static readonly int HeightParams = Shader.PropertyToID("_HeightParams");
        static readonly int HeightParams1 = Shader.PropertyToID("_HeightParams1");
        static readonly int DistanceParams = Shader.PropertyToID("_DistanceParams");
        static readonly int DistanceParams1 = Shader.PropertyToID("_DistanceParams1");
        static readonly int SceneFogParams = Shader.PropertyToID("_SceneFogParams");
        static readonly int SceneFogMode = Shader.PropertyToID("_SceneFogMode");
        static readonly int FogColor = Shader.PropertyToID("_FogColor");
        static readonly int FogColor1 = Shader.PropertyToID("_FogColor1");
        static readonly int MainTex = Shader.PropertyToID("_MainTex");

        static int s_uses;
        static bool s_logged;

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(DistanceAndHeightFog), "OnRenderImage"),
                prefix: new HarmonyMethod(typeof(StereoFog), nameof(Prefix)));
            Diag.Commands["stereofog"] = a =>
            {
                if (a.Length > 1 && a[1] == "info") return Info();
                if (a.Length > 1) Enabled = a[1] != "0";
                return "stereo fog " + (Enabled ? "on" : "off (game's centre-eye maths)") + ", used " + s_uses + " times";
            };
        }

        /// The eye frustum's corner ray at view depth `far`, in world space. View space looks down -z;
        /// for an off-centre GL projection P a point at depth d on the frustum edge ndc has
        /// x = d (ndc.x + P02) / P00 and y = d (ndc.y + P12) / P11.
        static Vector3 Corner(Matrix4x4 camToWorld, Matrix4x4 p, float far, float nx, float ny)
        {
            return camToWorld.MultiplyVector(new Vector3(far * (nx + p.m02) / p.m00, far * (ny + p.m12) / p.m11, -far));
        }

        static bool Prefix(DistanceAndHeightFog __instance, RenderTexture source, RenderTexture destination)
        {
            if (!Enabled || !XRSettings.enabled) return true;
            Camera cam = __instance.GetComponent<Camera>();
            if (cam == null || !cam.stereoEnabled) return true;
            try
            {
                Render(__instance, cam, source, destination);
            }
            catch (Exception e)
            {
                Enabled = false;
                Plugin.L.LogError("StereoFog failed, back to the game's fog: " + e);
                return true;
            }
            return false;
        }

        static void Render(DistanceAndHeightFog fog, Camera cam, RenderTexture source, RenderTexture destination)
        {
            // --- the game's own checks, unchanged
            bool deferred = cam.actualRenderingPath == RenderingPath.DeferredShading;
            if (!deferred || DistanceAndHeightFog.DisableFog)
            {
                Graphics.Blit(source, destination);
                return;
            }
            bool sceneFog = fog.gameObject.activeInHierarchy && fog.enabled && fog.CheckResources() && RenderSettings.fog && deferred;
            if (!fog.CheckResources() || (!fog.heightFog && !sceneFog))
            {
                Graphics.Blit(source, destination);
                return;
            }
            Material mat = s_mat(fog);

            // --- per eye: the rendering eye's position and asymmetric frustum
            Camera.StereoscopicEye eye = cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
            Matrix4x4 camToWorld = cam.GetStereoViewMatrix(eye).inverse;
            Matrix4x4 proj = cam.GetStereoProjectionMatrix(eye);
            float far = cam.farClipPlane;
            Matrix4x4 corners = Matrix4x4.identity;
            corners.SetRow(0, Corner(camToWorld, proj, far, -1f, 1f));    // top left
            corners.SetRow(1, Corner(camToWorld, proj, far, 1f, 1f));     // top right
            corners.SetRow(2, Corner(camToWorld, proj, far, 1f, -1f));    // bottom right
            corners.SetRow(3, Corner(camToWorld, proj, far, -1f, -1f));   // bottom left
            Vector3 position = camToWorld.GetColumn(3);
            if (!s_logged)
            {
                s_logged = true;
                Plugin.L.LogInfo("StereoFog: per-eye fog on '" + cam.name + "' (" + eye + " eye at " + Diag.V(position) + ", head " + Diag.V(cam.transform.position) + ")");
            }
            s_uses++;

            // --- the rest exactly as DistanceAndHeightFog.OnRenderImage
            float num3 = position.y - fog.height;
            float num4 = position.y - fog.height1;
            float z = num3 <= 0f ? 1f : 0f;
            float z2 = num4 <= 0f ? 1f : 0f;
            if (fog.SunTransform != null && fog.sunContribution)
            {
                mat.EnableKeyword("SUN_CONTRIBUTION");
                mat.SetVector(SunDirWS, fog.SunTransform.forward);
                mat.SetColor(SunColor, fog.SunColor);
                mat.SetFloat(SunSpread, fog.SunSpread);
            }
            else
            {
                mat.DisableKeyword("SUN_CONTRIBUTION");
            }
            mat.SetMatrix(CornersWS, corners);
            mat.SetVector(CameraWS, position);
            mat.SetVector(HeightParams, new Vector4(fog.height, num3, z, fog.heightDensity * 0.5f));
            mat.SetVector(HeightParams1, new Vector4(fog.height1, num4, z2, fog.heightDensity1 * 0.5f));
            mat.SetVector(DistanceParams, new Vector4(-Mathf.Max(fog.startDistance, 0f), 0f, 0f, 0f));
            mat.SetVector(DistanceParams1, new Vector4(-Mathf.Max(fog.startDistance1, 0f), 0f, 0f, 0f));
            FogMode fogMode = RenderSettings.fogMode;
            float fogDensity = RenderSettings.fogDensity;
            float fogStart = RenderSettings.fogStartDistance;
            float fogEnd = RenderSettings.fogEndDistance;
            bool linear = fogMode == FogMode.Linear;
            float range = linear ? fogEnd - fogStart : 0f;
            float invRange = Mathf.Abs(range) > 0.0001f ? 1f / range : 0f;
            Vector4 sceneParams = default(Vector4);
            sceneParams.x = fogDensity * 1.2011224f;
            sceneParams.y = fogDensity * 1.442695f;
            sceneParams.z = linear ? -invRange : 0f;
            sceneParams.w = linear ? fogEnd * invRange : 0f;
            mat.SetVector(SceneFogParams, sceneParams);
            mat.SetVector(SceneFogMode, new Vector4((float)fogMode, 0f, 0f, 0f));
            mat.SetColor(FogColor, fog.heightFogColor);
            mat.SetColor(FogColor1, fog.heightFogColor1);
            int pass = (!sceneFog || !fog.heightFog) ? (sceneFog ? 1 : 2) : 0;
            Blit(source, destination, mat, pass);
        }

        /// DistanceAndHeightFog.CustomGraphicsBlit: a full-screen quad whose vertex z picks the
        /// frustum-corner row (0 TL, 1 TR, 2 BR, 3 BL).
        static void Blit(RenderTexture source, RenderTexture dest, Material mat, int pass)
        {
            RenderTexture.active = dest;
            mat.SetTexture(MainTex, source);
            GL.PushMatrix();
            GL.LoadOrtho();
            mat.SetPass(pass);
            GL.Begin(GL.QUADS);
            GL.MultiTexCoord2(0, 0f, 0f);
            GL.Vertex3(0f, 0f, 3f);
            GL.MultiTexCoord2(0, 1f, 0f);
            GL.Vertex3(1f, 0f, 2f);
            GL.MultiTexCoord2(0, 1f, 1f);
            GL.Vertex3(1f, 1f, 1f);
            GL.MultiTexCoord2(0, 0f, 1f);
            GL.Vertex3(0f, 1f, 0f);
            GL.End();
            GL.PopMatrix();
        }

        static string Info()
        {
            Camera cam = VRRig.I != null ? VRRig.I.PrimaryCamera : null;
            if (cam == null) return "no primary camera";
            var sb = new System.Text.StringBuilder("stereo matrices of '" + cam.name + "' fov " + cam.fieldOfView.ToString("0.##", Inv) + " aspect " +
                                                   cam.aspect.ToString("0.####", Inv) + " near " + cam.nearClipPlane + " far " + cam.farClipPlane);
            foreach (Camera.StereoscopicEye e in new[] { Camera.StereoscopicEye.Left, Camera.StereoscopicEye.Right })
            {
                Matrix4x4 p = cam.GetStereoProjectionMatrix(e);
                Matrix4x4 c2w = cam.GetStereoViewMatrix(e).inverse;
                sb.AppendFormat(Inv, "\n  {0}: P00 {1:0.#####} P02 {2:0.#####} P11 {3:0.#####} P12 {4:0.#####} | tan L {5:0.####} R {6:0.####} D {7:0.####} U {8:0.####} | eye {9}",
                    e, p.m00, p.m02, p.m11, p.m12, (1f - p.m02) / p.m00, (1f + p.m02) / p.m00, (1f - p.m12) / p.m11, (1f + p.m12) / p.m11, Diag.V(c2w.GetColumn(3)));
                // check against Unity's own per-eye unprojection: corner ray directions of this fix
                // and of the game's centre-eye frustum vs the eye's true ones (degrees)
                Transform t = cam.transform;
                float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad), tanH = tanV * cam.aspect;
                float worstMine = 0f, worstGame = 0f;
                foreach (var c in new[] { new Vector2(-1, 1), new Vector2(1, 1), new Vector2(1, -1), new Vector2(-1, -1), new Vector2(0, 0) })
                {
                    Vector4 v = p.inverse * new Vector4(c.x, c.y, 1f, 1f);   // unproject the far-plane point
                    Vector3 truth = c2w.MultiplyVector(new Vector3(v.x / v.w, v.y / v.w, v.z / v.w));
                    Vector3 mine = Corner(c2w, p, cam.farClipPlane, c.x, c.y);
                    Vector3 game = t.forward + t.right * (c.x * tanH) + t.up * (c.y * tanV);
                    worstMine = Mathf.Max(worstMine, Vector3.Angle(truth, mine));
                    worstGame = Mathf.Max(worstGame, Vector3.Angle(truth, game));
                }
                sb.AppendFormat(Inv, " | ray error vs Unity: per-eye fix {0:0.00} deg, game's centre-eye {1:0.00} deg", worstMine, worstGame);
            }
            sb.Append("\n  head " + Diag.V(cam.transform.position) + " (fog uses " + (Enabled ? "eye" : "head") + ")");
            Plugin.L.LogInfo(sb.ToString());
            return "logged";
        }
    }
}

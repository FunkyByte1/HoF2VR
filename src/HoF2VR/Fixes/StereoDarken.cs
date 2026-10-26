using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using Defiant.PostProcess;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The game's darkness effects (WorldDarken in dark levels, WorldDarkenCaravan at the table,
    /// WraithVoid around wraiths) light pools of the screen around world-space lights. Their shaders
    /// ("Hidden/World Darken", "Hidden/Wraith Void", read from the game's shared_shaders bundle)
    /// rebuild each pixel's position as
    ///     _CameraPosition + eyeDepth * _CameraToWorldMatrix(3x3) * (ndc.x * tan * aspect, ndc.y * tan, -1)
    /// i.e. one symmetric frustum at the camera transform. A headset eye sits half the IPD to the side
    /// with an asymmetric frustum, so the pools landed at different spots in the two eyes (floating
    /// at the wrong depth).
    ///
    /// Per eye we hand the shader values that make its own formula exact: the eye's position, tan =
    /// 1/P11, aspect = P11/P00, and the eye's camera-to-world matrix times a shear that adds the
    /// off-centre terms (P02/P00, P12/P11) - the shader only uses the 3x3 part, so the shear is exact.
    /// `stereodarken 0|1` switches it (A/B), `stereodarken info` checks the rays against the eye's
    /// true frustum.
    internal static class StereoDarken
    {
        public static bool Enabled = true;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly int C2W = Shader.PropertyToID("_CameraToWorldMatrix");
        static readonly int CamPos = Shader.PropertyToID("_CameraPosition");
        static readonly int TanHalf = Shader.PropertyToID("_TanHalfFOV");
        static readonly int Aspect = Shader.PropertyToID("_ViewAspect");
        static int s_uses;

        public static void Apply(Harmony h)
        {
            var globals = new HarmonyMethod(typeof(StereoDarken), nameof(SetGlobalsPrefix));
            h.Patch(AccessTools.Method(typeof(WorldDarken), "OnRenderImage"), prefix: globals);
            h.Patch(AccessTools.Method(typeof(WorldDarkenCaravan), "OnRenderImage"), prefix: globals);
            h.Patch(AccessTools.Method(typeof(WraithVoid), "OnRenderImage"), transpiler: new HarmonyMethod(typeof(StereoDarken), nameof(Transpiler)));
            Diag.Commands["stereodarken"] = a =>
            {
                if (a.Length > 1 && a[1] == "info") return Info();
                if (a.Length > 1) Enabled = a[1] != "0";
                return "stereo darken " + (Enabled ? "on" : "off (game's centre-eye maths)") + ", used " + s_uses + " times";
            };
        }

        /// The rendering eye's values for the darkness shaders. False when not rendering a stereo eye.
        static bool EyeValues(Camera cam, out Matrix4x4 c2w, out Vector3 pos, out float tan, out float aspect)
        {
            c2w = Matrix4x4.identity; pos = Vector3.zero; tan = 1f; aspect = 1f;
            if (!Enabled || !XRSettings.enabled || cam == null || !cam.stereoEnabled) return false;
            try { return Compute(cam, out c2w, out pos, out tan, out aspect); }
            catch (Exception e)
            {
                Enabled = false;   // fall back to the game's own maths for the rest of the session
                Plugin.L.LogError("StereoDarken failed, back to the game's maths: " + e);
                return false;
            }
        }

        static bool Compute(Camera cam, out Matrix4x4 c2w, out Vector3 pos, out float tan, out float aspect)
        {
            Camera.StereoscopicEye eye = cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
            Matrix4x4 eyeC2W = cam.GetStereoViewMatrix(eye).inverse;
            Matrix4x4 p = cam.GetStereoProjectionMatrix(eye);
            Matrix4x4 shear = Matrix4x4.identity;
            shear.m02 = -p.m02 / p.m00;   // x' = x - z * P02/P00 (z = -1 along the ray)
            shear.m12 = -p.m12 / p.m11;
            c2w = eyeC2W * shear;
            pos = eyeC2W.GetColumn(3);
            tan = 1f / p.m11;
            aspect = p.m11 / p.m00;
            s_uses++;
            return true;
        }

        static void SetGlobalsPrefix(MonoBehaviour __instance)
        {
            Matrix4x4 c2w; Vector3 pos; float tan, aspect;
            if (!EyeValues(__instance.GetComponent<Camera>(), out c2w, out pos, out tan, out aspect)) return;
            Shader.SetGlobalMatrix(C2W, c2w);
            Shader.SetGlobalVector(CamPos, new Vector4(pos.x, pos.y, pos.z, 1f));
            Shader.SetGlobalFloat(TanHalf, tan);
            Shader.SetGlobalFloat(Aspect, aspect);
        }

        // ---- WraithVoid sets the same values on its material inside OnRenderImage: swap the reads
        public static Matrix4x4 CameraToWorld(Camera cam)
        {
            Matrix4x4 c2w; Vector3 pos; float tan, aspect;
            return EyeValues(cam, out c2w, out pos, out tan, out aspect) ? c2w : cam.cameraToWorldMatrix;
        }

        public static Vector3 Position(Transform t)
        {
            Matrix4x4 c2w; Vector3 pos; float tan, aspect;
            return EyeValues(t.GetComponent<Camera>(), out c2w, out pos, out tan, out aspect) ? pos : t.position;
        }

        public static float FieldOfView(Camera cam)
        {
            Matrix4x4 c2w; Vector3 pos; float tan, aspect;
            return EyeValues(cam, out c2w, out pos, out tan, out aspect) ? 2f * Mathf.Atan(tan) * Mathf.Rad2Deg : cam.fieldOfView;
        }

        public static float AspectOf(Camera cam)
        {
            Matrix4x4 c2w; Vector3 pos; float tan, aspect;
            return EyeValues(cam, out c2w, out pos, out tan, out aspect) ? aspect : cam.aspect;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var map = new Dictionary<MethodInfo, MethodInfo>
            {
                { AccessTools.PropertyGetter(typeof(Camera), "cameraToWorldMatrix"), AccessTools.Method(typeof(StereoDarken), nameof(CameraToWorld)) },
                { AccessTools.PropertyGetter(typeof(Transform), "position"), AccessTools.Method(typeof(StereoDarken), nameof(Position)) },
                { AccessTools.PropertyGetter(typeof(Camera), "fieldOfView"), AccessTools.Method(typeof(StereoDarken), nameof(FieldOfView)) },
                { AccessTools.PropertyGetter(typeof(Camera), "aspect"), AccessTools.Method(typeof(StereoDarken), nameof(AspectOf)) },
            };
            int n = 0;
            foreach (CodeInstruction ci in code)
            {
                MethodInfo mi = ci.operand as MethodInfo, to;
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && mi != null && map.TryGetValue(mi, out to))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = to;
                    n++;
                }
                yield return ci;
            }
            Plugin.L.LogInfo("StereoDarken: WraithVoid.OnRenderImage per-eye reads: " + n);
        }

        /// Emulates the shaders' ray formula with this fix's values (and with the game's) for the
        /// four corners and the centre, against the eye's true frustum (inverse projection).
        static string Info()
        {
            Camera cam = VRRig.I != null ? VRRig.I.PrimaryCamera : null;
            if (cam == null) return "no primary camera";
            var sb = new System.Text.StringBuilder("stereo darken check on '" + cam.name + "':");
            foreach (Camera.StereoscopicEye e in new[] { Camera.StereoscopicEye.Left, Camera.StereoscopicEye.Right })
            {
                Matrix4x4 eyeC2W = cam.GetStereoViewMatrix(e).inverse;
                Matrix4x4 p = cam.GetStereoProjectionMatrix(e);
                Matrix4x4 shear = Matrix4x4.identity;
                shear.m02 = -p.m02 / p.m00;
                shear.m12 = -p.m12 / p.m11;
                Matrix4x4 c2w = eyeC2W * shear;
                float tan = 1f / p.m11, asp = p.m11 / p.m00;
                // the game's values: the head camera's own matrix and symmetric fov/aspect
                float gTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad), gAsp = cam.aspect;
                Matrix4x4 gC2W = VRRig.I.PrimaryRot == Quaternion.identity ? cam.cameraToWorldMatrix
                                 : Matrix4x4.TRS(VRRig.I.PrimaryPos, VRRig.I.PrimaryRot, Vector3.one) * Matrix4x4.Scale(new Vector3(1, 1, -1));
                float worstMine = 0f, worstGame = 0f;
                foreach (var c in new[] { new Vector2(-1, 1), new Vector2(1, 1), new Vector2(1, -1), new Vector2(-1, -1), new Vector2(0, 0) })
                {
                    Vector4 v = p.inverse * new Vector4(c.x, c.y, 1f, 1f);
                    Vector3 truth = eyeC2W.MultiplyVector(new Vector3(v.x / v.w, v.y / v.w, v.z / v.w));
                    Vector3 mine = c2w.MultiplyVector(new Vector3(c.x * tan * asp, c.y * tan, -1f));
                    Vector3 game = gC2W.MultiplyVector(new Vector3(c.x * gTan * gAsp, c.y * gTan, -1f));
                    worstMine = Mathf.Max(worstMine, Vector3.Angle(truth, mine));
                    worstGame = Mathf.Max(worstGame, Vector3.Angle(truth, game));
                }
                sb.AppendFormat(Inv, "\n  {0}: ray error per-eye fix {1:0.00} deg, game's centre-eye {2:0.00} deg", e, worstMine, worstGame);
            }
            Plugin.L.LogInfo(sb.ToString());
            return sb.ToString().Replace("\n", " |");
        }
    }
}

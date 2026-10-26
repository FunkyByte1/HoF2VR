using System;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Until You Fall's kill launch (docs/uyf-parity-audit.md E25, item 10): a killing blow of real force (UYF's force rating
    /// >= 2.5: our Standard and Big hits) throws the body along the swing - an impulse of 400 (Standard) or 600 (Big) on the
    /// death's ragdoll, blended up for an uppercut and down for a hammerfist (a swing within 30 deg of straight up / down:
    /// 0.5 / 0.6). HoF2 already falls into a ragdoll on death with its own hit's impulse (ActionDead: Ragdoll.SwitchToRagdoll,
    /// or the PuppetMaster's first muscle); the impulse is replaced there when the player's blade made the kill.
    internal static class KillLaunch
    {
        static Targetable s_target;
        static Vector3 s_dir;
        static int s_quality;
        static float s_at = -10f;
        public static int Launches;
        public static string Last = "-";

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(Ragdoll), "SwitchToRagdoll"), prefix: new HarmonyMethod(typeof(KillLaunch), nameof(RagdollPrefix)));
                h.Patch(AccessTools.Method(typeof(ActionDead), "SwitchToPuppetMaster"), prefix: new HarmonyMethod(typeof(KillLaunch), nameof(PuppetPrefix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("KillLaunch: patch failed: " + e.Message); }
            Diag.Commands["launch?"] = a => "kill launches " + Launches + " last " + Last;
        }

        /// BladeHits, before a blade hit is applied: if it kills, the body flies along `swingDir` (world).
        public static void Arm(Targetable t, Vector3 swingDir, int quality)
        {
            s_target = quality >= 1 ? t : null;
            s_dir = swingDir;
            s_quality = quality;
            s_at = Time.unscaledTime;
        }

        static bool Mine(Component c)
        {
            if (s_target == null || c == null || Time.unscaledTime - s_at > 1.5f) return false;
            try { return c.transform.IsChildOf(s_target.transform) || (s_target.Controller != null && s_target.Controller.Model != null && c.transform.IsChildOf(s_target.Controller.Model.transform)); }
            catch (Exception) { return false; }
        }

        static Vector3 Impulse()
        {
            Vector3 d = s_dir.sqrMagnitude > 1e-6f ? s_dir.normalized : Vector3.forward;
            float up = Vector3.Angle(d, Vector3.up);
            Vector3 flat = new Vector3(d.x, 0f, d.z);
            flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.zero;
            Vector3 dir = flat;
            if (up <= 30f) dir = Vector3.Lerp(flat, Vector3.up, 0.5f).normalized;              // an uppercut
            else if (up >= 150f) dir = Vector3.Lerp(flat, Vector3.down, 0.6f).normalized;      // a hammerfist
            if (dir.sqrMagnitude < 1e-6f) dir = d;
            return dir * (s_quality >= 2 ? 600f : 400f);
        }

        static void RagdollPrefix(Ragdoll __instance, ref Vector3 a_impulse)
        {
            if (!Physical.Live || !Mine(__instance)) return;
            a_impulse = Impulse();
            Launches++;
            Last = WeaponGrips.ModelKey(s_target.name) + " " + a_impulse.magnitude.ToString("0") + " along " + Diag.V(a_impulse.normalized) + " (ragdoll)";
            s_target = null;
        }

        static void PuppetPrefix(ActionDead __instance, ref bool a_applyImpulse, ref Vector3 a_impulse)
        {
            if (!Physical.Live || !Mine(__instance)) return;
            a_applyImpulse = true;
            a_impulse = Impulse();
            Launches++;
            Last = WeaponGrips.ModelKey(s_target.name) + " " + a_impulse.magnitude.ToString("0") + " along " + Diag.V(a_impulse.normalized) + " (puppet)";
            s_target = null;
        }
    }
}

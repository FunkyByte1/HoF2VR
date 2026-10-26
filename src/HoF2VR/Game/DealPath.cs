using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The cards' flights at the table stay clear of the player's body (the third playtest, mark 28: "There was cards coming out
    /// from that red deck to my abdomen and then back to the center"). The game flies its cards along curved paths
    /// (MotionController, FlightPath: sideways and up from the straight line, authored for its camera high over the table); at the
    /// call to battle the enemies' cards swing from the Dealer's deck out toward the near edge - into the body of a player standing
    /// there. Mid-flight a card is kept out of the body (BodyRadius real metres round the line under the eyes the player stands
    /// with, below the eyes) and EyeKeep from those eyes; the pull fades in and out over the flight's first and last fifth, so it
    /// still leaves and lands exactly where the game sends it. Fixed to the place's eyes, never the head of the moment.
    internal static class DealPath
    {
        public static bool On = true;
        public static float BodyRadius = 0.42f, EyeKeep = 0.53f, Ramp = 0.2f;
        public static int Kept;
        public static string Last = "-";

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(MotionController), "CalculatePosition"), postfix: new HarmonyMethod(typeof(DealPath), nameof(Postfix)));
            Diag.Commands["dealpath"] = a =>
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (a.Length > 1 && (a[1] == "0" || a[1] == "1")) On = a[1] == "1";
                if (a.Length > 2 && a[1] == "radius") BodyRadius = float.Parse(a[2], inv);
                return "deal paths kept out of the body " + On + " (radius " + BodyRadius + ", eyes " + EyeKeep + "): kept " + Kept + " | " + Last;
            };
        }

        static void Postfix(MotionTarget a_motionTarget, float a_t, ref Vector3 __result)
        {
            if (!On || !XRSettings.enabled) return;
            VRRig rig = VRRig.I;
            if (rig == null || !rig.HasStation || !Places.AtCaravan || CombatVR.Active) return;
            float w = Mathf.Clamp01(Mathf.Min(a_t, 1f - a_t) / Ramp);
            if (w <= 0f) return;
            float sc = rig.Scale;
            Vector3 eye = rig.NeutralEye, p = __result, q = p;
            // the body: a cylinder under the eyes
            if (q.y < eye.y + 0.1f * sc)
            {
                Vector3 d = new Vector3(q.x - eye.x, 0f, q.z - eye.z);
                float r = BodyRadius * sc;
                if (d.sqrMagnitude < r * r)
                {
                    if (d.sqrMagnitude < 1e-6f) d = Quaternion.Euler(0f, rig.StationYaw, 0f) * Vector3.forward;
                    d = d.normalized * r;
                    q = new Vector3(eye.x + d.x, q.y, eye.z + d.z);
                }
            }
            // the eyes
            Vector3 e = q - eye;
            if (e.sqrMagnitude < EyeKeep * sc * EyeKeep * sc && e.sqrMagnitude > 1e-8f) q = eye + e.normalized * (EyeKeep * sc);
            if ((q - p).sqrMagnitude < 1e-8f) return;
            __result = p + (q - p) * w;
            Kept++;
            if (Time.frameCount % 30 == 0 || Last == "-")
            {
                Transform t = a_motionTarget != null ? a_motionTarget.Transform : null;
                Last = (t != null ? t.name : "?") + " kept " + ((q - p).magnitude * w / sc).ToString("0.00") + " m out at t " + a_t.ToString("0.00") + " (" + GameWatch.State.Replace("GameState_", "") + ")";
            }
        }
    }
}

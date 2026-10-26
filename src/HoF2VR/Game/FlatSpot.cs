using System;
using UnityEngine;

namespace HoF2VR
{
    /// "Can you put me on flat ground? It is disorienting being on a hill" (user 2026-10-11, the campfire waiting space;
    /// and at the title: "somewhere that isn't quite on a hill"). The title camera stands on a 35-45 degree hillside
    /// (measured: the ground rises 1 m within 1.25 m behind the spot and drops 0.45 m just in front), the waiting space's
    /// near spot on the edge of a step; flat terraces lie about 0.5-1.25 m ahead of both.
    /// Find() looks for the nearest spot whose ground is level within PadRadius around it (heights from a delegate:
    /// raycasts at the title, the waiting space's own ground mesh there), searching ahead and to the sides of the ideal
    /// spot but never back (behind both spots the ground climbs). Settings Comfort.FlatGround.
    internal static class FlatSpot
    {
        public static float PadRadius = 0.45f;      // the player's area that must be level (m)
        public static float MaxRise = 0.05f;        // largest height difference from the centre within the pad (~6 deg at 0.45 m)
        public static float Radius = 1.7f, Step = 0.15f, Back = 0.15f, MaxAbove = 0.3f;

        /// ideal (x, z) and yaw (deg, the way the place faces); height(x, z) = ground height or NaN. Returns the spot and
        /// its ground height, or false when no level spot is near (then the caller keeps the ideal one).
        public static bool Find(Vector2 ideal, float yaw, Func<float, float, float> height, out Vector2 best, out float bestH, out string why)
        {
            best = ideal;
            bestH = height(ideal.x, ideal.y);
            float h0 = bestH;
            float r0 = Rise(ideal.x, ideal.y, bestH, height);
            if (r0 <= MaxRise) { why = "level already (" + Deg(r0) + ")"; return true; }
            float yr = yaw * Mathf.Deg2Rad;
            var fwd = new Vector2(Mathf.Sin(yr), Mathf.Cos(yr));
            var right = new Vector2(Mathf.Cos(yr), -Mathf.Sin(yr));
            float bestCost = float.MaxValue, bestRise = float.MaxValue;
            Vector2 flattest = ideal;
            float flattestH = bestH, flattestRise = r0;
            int n = Mathf.CeilToInt(Radius / Step);
            for (int j = Mathf.FloorToInt(-Back / Step); j <= n; j++)
                for (int i = -n; i <= n; i++)
                {
                    Vector2 off = right * (i * Step) + fwd * (j * Step);
                    float d = off.magnitude;
                    if (d > Radius) continue;
                    Vector2 c = ideal + off;
                    float h = height(c.x, c.y);
                    if (float.IsNaN(h)) continue;
                    // stepping down the slope onto the terrace, never up onto a rock or a stump
                    if (!float.IsNaN(h0) && h > h0 + MaxAbove) continue;
                    float rise = Rise(c.x, c.y, h, height);
                    if (rise < flattestRise) { flattestRise = rise; flattest = c; flattestH = h; }
                    if (rise > MaxRise) continue;
                    // nearest level spot; a little sideways costs more than ahead (the place's framing stays)
                    float cost = d + Mathf.Abs(i * Step) * 0.3f + rise * 4f;
                    if (cost < bestCost) { bestCost = cost; best = c; bestH = h; bestRise = rise; }
                }
            if (bestCost < float.MaxValue)
            {
                Vector2 o = best - ideal;
                why = "moved " + Vector2.Dot(o, fwd).ToString("0.00") + " m ahead, " + Vector2.Dot(o, right).ToString("0.00") + " m right, ground " +
                      (bestH - height(ideal.x, ideal.y)).ToString("+0.00;-0.00") + " m (level within " + Deg(bestRise) + "; was " + Deg(r0) + ")";
                return true;
            }
            why = "no level spot within " + Radius + " m (flattest " + Deg(flattestRise) + ", the ideal " + Deg(r0) + ")";
            best = ideal;
            return false;
        }

        static string Deg(float rise) => (Mathf.Atan2(rise, PadRadius) * Mathf.Rad2Deg).ToString("0") + " deg";

        /// Largest height difference from h0 within PadRadius (8 directions at two radii).
        static float Rise(float x, float z, float h0, Func<float, float, float> height)
        {
            if (float.IsNaN(h0)) return float.MaxValue;
            float worst = 0f;
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI * 0.25f;
                for (int ri = 1; ri <= 2; ri++)
                {
                    float r = PadRadius * ri * 0.5f;
                    float h = height(x + Mathf.Cos(a) * r, z + Mathf.Sin(a) * r);
                    if (float.IsNaN(h)) return float.MaxValue;
                    worst = Mathf.Max(worst, Mathf.Abs(h - h0));
                }
            }
            return worst;
        }

        /// Ground height under (x, z) from colliders between `top` and `top - depth` (the game's scenes).
        public static Func<float, float, float> RayHeight(float top, float depth, int mask)
        {
            return (x, z) =>
            {
                RaycastHit hit;
                if (Physics.Raycast(new Vector3(x, top, z), Vector3.down, out hit, depth, mask, QueryTriggerInteraction.Ignore)) return hit.point.y;
                return float.NaN;
            };
        }
    }
}

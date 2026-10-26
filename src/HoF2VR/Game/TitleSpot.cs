using System;
using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// Where the player stands at the title (user 2026-10-11 evening: "I spawned facing the character, which was weird. I
    /// want to face towards the fire"; "it looked like if I took one step forward I'd fall a couple feet. Can you make it so
    /// all the ground around me and even behind me is at roughly the same level?"). The title camera stands on a hillside
    /// behind the hero, who stands between it and the campfire. The spot now: in the clearing beside the hero (the hero to
    /// one side, never in front), facing the fire, on ground levelled around the player (the terrain's own surface, texture
    /// and grass, made level within Flat metres and blended back to the original by Blend metres; changed at most MaxShift
    /// up or down, so hills nearby stay hills).
    internal static class TitleSpot
    {
        public static float FireMin = 2.8f, FireMax = 4.6f, HeroMin = 1.1f, HeroMax = 2.2f, Flat = 1.6f, Blend = 1.6f, MaxShift = 0.45f;
        public static string Last = "-";
        public static bool Enabled = true;   // `titlemap off|on` (test toggle)
        static int s_flattenedScene = -1;

        public static void Register()
        {
            // `titlelight <intensity> [range] [up]`: the light over the feet, live (tuning against shots)
            Diag.Commands["titlelight"] = a =>
            {
                GameObject go = GameObject.Find("HoF2VR_TitleFeetLight");
                Light l = go != null ? go.GetComponent<Light>() : null;
                if (l == null) return "no title light";
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (a.Length > 1) l.intensity = float.Parse(a[1], inv);
                if (a.Length > 2) l.range = float.Parse(a[2], inv);
                if (a.Length > 3) go.transform.position += Vector3.up * (float.Parse(a[3], inv) - FeetLightUp);
                return "title light " + l.intensity + " range " + l.range + " at " + Diag.V(go.transform.position);
            };
            Diag.Commands["titlemap"] = a =>
            {
                if (a.Length > 1 && (a[1] == "off" || a[1] == "on")) { Enabled = a[1] == "on"; return "title spot " + (Enabled ? "beside the hero" : "the camera's"); }
                return Map();
            };
        }

        static bool Find(out Vector3 fire, out Vector3 hero)
        {
            fire = hero = Vector3.zero;
            GameObject f = GameObject.Find("SceneContents/Art/Forest/Camp_Fire");
            if (f == null) return false;
            fire = f.transform.position;
            Collider c = f.GetComponentInChildren<Collider>();
            if (c != null) fire = c.bounds.center;
            GameObject hc = GameObject.Find("SceneContents/CustomisationCharacterContainer");
            bool has = false;
            Bounds b = default(Bounds);
            if (hc != null)
                foreach (Renderer r in hc.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || !r.enabled) continue;
                    if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
                }
            hero = has ? new Vector3(b.center.x, b.min.y, b.center.z) : fire + Vector3.back * 4f;
            return true;
        }

        static float H(Terrain t, float x, float z) => t.SampleHeight(new Vector3(x, 0f, z)) + t.transform.position.y;

        /// The spot: in the clearing beside the hero, facing the fire. False = no terrain/fire (keep the old spot).
        public static bool Spot(Vector3 cameraPos, out Vector3 floor, out float yaw, out string why)
        {
            floor = Vector3.zero;
            yaw = 0f;
            why = "";
            Vector3 fire, hero;
            Terrain t = Terrain.activeTerrain;
            if (t == null || !Find(out fire, out hero)) { why = "no terrain or fire"; return false; }
            // candidates round the fire, on the camera's side; level ground, the hero beside (not in front), a good distance
            Vector3 toCam = cameraPos - fire;
            toCam.y = 0f;
            float camBearing = Mathf.Atan2(toCam.x, toCam.z) * Mathf.Rad2Deg;
            float best = float.MaxValue;
            Vector3 bestP = Vector3.zero;
            float bestRise = 0f, bestHeroAng = 0f;
            for (float bearing = -75f; bearing <= 75f; bearing += 5f)
                for (float r = FireMin; r <= FireMax; r += 0.2f)
                {
                    float a = (camBearing + bearing) * Mathf.Deg2Rad;
                    Vector3 p = fire + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * r;
                    Vector3 toHero = hero - p;
                    toHero.y = 0f;
                    float hd = toHero.magnitude;
                    if (hd < HeroMin) continue;
                    Vector3 toFire = fire - p;
                    toFire.y = 0f;
                    float heroAng = Vector3.Angle(toFire, toHero);
                    if (heroAng < 45f || heroAng > 115f) continue;   // the hero beside the player: not in front of the fire, not behind
                    float h0 = H(t, p.x, p.z);
                    float rise = 0f;
                    for (int k = 0; k < 8; k++)
                    {
                        float aa = k * Mathf.PI / 4f;
                        foreach (float rr in new[] { 0.5f, 1.0f, 1.5f })
                            rise = Mathf.Max(rise, Mathf.Abs(H(t, p.x + Mathf.Cos(aa) * rr, p.z + Mathf.Sin(aa) * rr) - h0));
                    }
                    // level ground first, then the hero at a friendly distance, then near the camera's side and ~3.6 m from the fire
                    float cost = rise * 6f + Mathf.Max(0f, hd - HeroMax) * 1.5f + Mathf.Abs(bearing) * 0.01f + Mathf.Abs(r - 3.6f) * 0.3f + Mathf.Abs(heroAng - 85f) * 0.02f;
                    if (cost < best) { best = cost; bestP = new Vector3(p.x, h0, p.z); bestRise = rise; bestHeroAng = heroAng; }
                }
            if (best == float.MaxValue) { why = "no spot beside the hero"; return false; }
            floor = bestP;
            Vector3 face = fire - bestP;
            face.y = 0f;
            yaw = Quaternion.LookRotation(face).eulerAngles.y;
            float heroDist = Vector3.Distance(new Vector3(hero.x, 0f, hero.z), new Vector3(bestP.x, 0f, bestP.z));
            why = "beside the hero (" + heroDist.ToString("0.0") + " m, " + bestHeroAng.ToString("0") + " deg from the fire), " +
                  Vector3.Distance(new Vector3(fire.x, 0f, fire.z), new Vector3(bestP.x, 0f, bestP.z)).ToString("0.0") + " m from the fire, ground within " +
                  bestRise.ToString("0.00") + " m over 1.5 m";
            if (Settings.FlatGround) why += "; " + Level(t, bestP);
            floor.y = H(t, bestP.x, bestP.z);
            why += "; " + Surroundings(floor, yaw);
            Last = why;
            return true;
        }

        // ---- M3, playtest 2026-10-17 ("light the ground they stand on a little; remove the plant on their right, or move it
        // further away"): the spot is in a spiky grass clump (SceneContents/Art/Forest/Background/Foliage/Grass/grass_Gamba_green
        // (27): its box held the player's feet, 0.9 m tall) with a 3 m bush 0.45 m away behind on the right, and the fire's light
        // ends 2 m short of the spot (the ground under the player was black). The foliage within ClearRadius of the spot is not
        // drawn at the title, and a small warm light hangs above the player's feet.
        public static float ClearRadius = 0.9f, FeetLightUp = 0.9f, FeetLightAhead = 0.35f, FeetLightRange = 3.0f, FeetLightIntensity = 1.6f;
        public static Color FeetLightColour = new Color(1f, 0.74f, 0.48f);
        public static int Cleared;
        static int s_clearedScene = -1;

        static string Surroundings(Vector3 floor, float yaw)
        {
            int scene = SceneScan.Version;
            if (s_clearedScene == scene) return "surroundings done already";
            s_clearedScene = scene;
            int n = 0;
            var names = new StringBuilder();
            GameObject art = GameObject.Find("SceneContents/Art");
            if (art != null)
                foreach (Renderer r in art.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
                    string path = VRUI.Path(r.transform);
                    if (path.IndexOf("/Foliage/", StringComparison.Ordinal) < 0) continue;
                    Bounds b = r.bounds;
                    if (b.size.magnitude > 8f) continue;
                    float dx = Mathf.Max(0f, Mathf.Max(b.min.x - floor.x, floor.x - b.max.x)), dz = Mathf.Max(0f, Mathf.Max(b.min.z - floor.z, floor.z - b.max.z));
                    if (Mathf.Sqrt(dx * dx + dz * dz) > ClearRadius || b.min.y > floor.y + 2f) continue;
                    r.enabled = false;
                    n++;
                    Cleared++;
                    if (names.Length < 300) names.Append(names.Length > 0 ? ", " : "").Append(r.name);
                }
            // the light: a soft warm pool on the ground round the feet, a little ahead (in the title scene: it goes with it)
            var go = new GameObject("HoF2VR_TitleFeetLight");
            UnityEngine.SceneManagement.Scene sc = art != null ? art.scene : UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (sc.IsValid()) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, sc);
            go.transform.position = floor + Vector3.up * FeetLightUp + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * FeetLightAhead;
            Light l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = FeetLightColour;
            l.range = FeetLightRange;
            l.intensity = FeetLightIntensity;
            l.shadows = LightShadows.None;
            l.renderMode = LightRenderMode.ForcePixel;
            string res = n + " plants within " + ClearRadius.ToString("0.0") + " m not drawn (" + names + "), a light over the feet";
            Plugin.L.LogInfo("title spot: " + res);
            return res;
        }

        /// The terrain round the spot made level (in memory only; the game's files are never touched).
        static string Level(Terrain t, Vector3 c)
        {
            int scene = SceneScan.Version;
            if (s_flattenedScene == scene) return "ground levelled already";
            TerrainData d = t.terrainData;
            if (d == null) return "no terrain data";
            int res = d.heightmapResolution;
            Vector3 size = d.size, origin = t.transform.position;
            float target = H(t, c.x, c.z);
            float reach = Flat + Blend;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((c.x - reach - origin.x) / size.x * (res - 1)), 0, res - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((c.x + reach - origin.x) / size.x * (res - 1)), 0, res - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((c.z - reach - origin.z) / size.z * (res - 1)), 0, res - 1);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((c.z + reach - origin.z) / size.z * (res - 1)), 0, res - 1);
            int w = x1 - x0 + 1, hgt = z1 - z0 + 1;
            if (w < 2 || hgt < 2) return "spot outside the terrain";
            float[,] h = d.GetHeights(x0, z0, w, hgt);
            float worst = 0f;
            int changed = 0;
            for (int j = 0; j < hgt; j++)
                for (int i = 0; i < w; i++)
                {
                    float wx = origin.x + (x0 + i) / (float)(res - 1) * size.x, wz = origin.z + (z0 + j) / (float)(res - 1) * size.z;
                    float dist = Mathf.Sqrt((wx - c.x) * (wx - c.x) + (wz - c.z) * (wz - c.z));
                    if (dist >= reach) continue;
                    float k = dist <= Flat ? 1f : Mathf.SmoothStep(1f, 0f, (dist - Flat) / Blend);
                    float cur = origin.y + h[j, i] * size.y;
                    float want = Mathf.Lerp(cur, target, k);
                    want = Mathf.Clamp(want, cur - MaxShift, cur + MaxShift);
                    worst = Mathf.Max(worst, Mathf.Abs(want - cur));
                    h[j, i] = (want - origin.y) / size.y;
                    changed++;
                }
            d.SetHeights(x0, z0, h);
            s_flattenedScene = scene;
            return "ground levelled within " + Flat + " m (" + changed + " heights, largest change " + worst.ToString("0.00") + " m)";
        }

        /// `titlemap`: terrain heights round the fire (0.5 m grid, relative to the fire's ground), the hero and the spot.
        static string Map()
        {
            Vector3 fire, hero;
            Terrain t = Terrain.activeTerrain;
            if (t == null || !Find(out fire, out hero)) return "no terrain or fire";
            float g = H(t, fire.x, fire.z);
            var sb = new StringBuilder("title terrain round the fire " + Diag.V(fire) + " (ground " + g.ToString("0.00") + "), hero " + Diag.V(hero) + ", rows +z down:\n");
            for (int j = 7; j >= -10; j--)
            {
                for (int i = -8; i <= 8; i++)
                {
                    float x = fire.x + i * 0.5f, z = fire.z + j * 0.5f;
                    bool isHero = Mathf.Abs(x - hero.x) < 0.25f && Mathf.Abs(z - hero.z) < 0.25f;
                    sb.Append(isHero ? "  H  " : (H(t, x, z) - g).ToString("+0.0;-0.0").PadLeft(5));
                }
                sb.Append('\n');
            }
            Plugin.L.LogInfo(sb.ToString());
            return "logged (" + Last + ")";
        }
    }
}

using System;
using Defiant.PostProcess;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Choosing a challenge from where the player talks to the Dealer (user 2026-10-11 evening: "When selecting from the
    /// map, can I be in the same position as I am when talking to the dealer? ... Maybe you light up the map noticeably and
    /// dim the rest of the room ... Then you don't even teleport them"). With Table.MapFromCardTable the challenge map
    /// state keeps the player at the card table (Places: no side-table spot for the map shot; the deck-builder boards
    /// stay hidden there, TableComfort), the panel with the map's text turns to the map (VRUI), and the room dims around
    /// a pool of light on the map: the game's own WorldDarkenCaravan darkness (per eye: StereoDarken) with one of its
    /// WorldDarkenLight holes over the map, plus a warm spot light above it.
    internal class MapFocus : MonoBehaviour
    {
        public static MapFocus I;
        public static float Dim = 0.3f, HoleRadius = 1.05f, FadeTime = 0.6f, SpotIntensity = 2.2f;
        static bool s_on;
        static Vector3 s_centre;
        static bool s_weEnabled;
        static float s_level = 1f;
        GameObject m_hole, m_spot;
        public static string Last = "-";

        /// The challenge map shot is on now and handled here (the player stays at the card table).
        public static bool MapShotAtTable()
        {
            // (2026-10-12 evening, replaces round 4: "the choose a deck area was good for choosing a map": with
            // Table.MapAtDeckSpot the challenge map is chosen at the side table where the deck is chosen - no move between)
            if (Settings.MapAtDeckSpot || !Settings.MapFromCardTable || !XRSettings.enabled) return false;
            CardCamera cc = CardCamera.Instance;
            return cc != null && cc.ChallengeMapCamera != null && cc.TopCamera as FixedCamera == cc.ChallengeMapCamera;
        }

        /// The panel's way while the map is up: toward the map from the eyes.
        public static bool PanelYaw(Vector3 eye, out float yaw)
        {
            yaw = 0f;
            if (!s_on) return false;
            Vector3 d = s_centre - eye;
            d.y = 0f;
            if (d.sqrMagnitude < 1e-4f) return false;
            yaw = Quaternion.LookRotation(d).eulerAngles.y;
            return true;
        }

        void Awake()
        {
            I = this;
            Diag.StatusExtras["mapFocus"] = () => Diag.Q((s_on ? "on " : "off ") + Last + " level " + s_level.ToString("0.00"));
        }

        void LateUpdate()
        {
            bool want = Places.Current == Places.Place.Caravan && MapShotAtTable() && !VRFade.IsBlack;
            if (want && !s_on) Begin();
            else if (!want && s_on) s_on = false;
            Step();
        }

        void Begin()
        {
            // the map: the middle of its unlocked locations
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (ChallengeMapLocation l in FindObjectsOfType<ChallengeMapLocation>())
            {
                if (l == null || !l.isActiveAndEnabled) continue;
                sum += l.transform.position;
                n++;
            }
            if (n == 0) { Last = "no map locations"; return; }
            s_centre = sum / n;
            s_on = true;
            float sc = VRRig.I != null ? VRRig.I.Scale : 1f;
            if (m_hole == null)
            {
                m_hole = new GameObject("HoF2VR_MapLight");
                DontDestroyOnLoad(m_hole);
                var h = m_hole.AddComponent<WorldDarkenLight>();
                h.Radius = HoleRadius * sc;
                h.Exponent = 3f;
                h.Strength = 1f;
                m_spot = new GameObject("HoF2VR_MapSpot");
                DontDestroyOnLoad(m_spot);
                Light l = m_spot.AddComponent<Light>();
                l.type = LightType.Spot;
                l.color = new Color(1f, 0.82f, 0.55f);
                l.spotAngle = 80f;
                l.shadows = LightShadows.None;
            }
            m_hole.transform.position = s_centre;
            m_hole.SetActive(true);
            Light spot = m_spot.GetComponent<Light>();
            spot.range = 2.5f * sc;
            spot.intensity = 0f;
            m_spot.transform.SetPositionAndRotation(s_centre + Vector3.up * (1.1f * sc), Quaternion.LookRotation(Vector3.down, Vector3.forward));
            m_spot.SetActive(true);
            Vector3 e = VRRig.I != null ? VRRig.I.NeutralEye : s_centre;
            Last = n + " locations, " + (Vector3.Distance(e, s_centre) / sc).ToString("0.00") + " m from the eyes";
            Plugin.L.LogInfo("MapFocus: the challenge map from the card table (" + Last + "): the room dims, the map lights up");
        }

        void Step()
        {
            WorldDarkenCaravan wd = WorldDarkenCaravan.Instance;
            float target = s_on ? Dim : 1f;
            float before = s_level;
            s_level = Mathf.MoveTowards(s_level, target, Time.unscaledDeltaTime * (1f - Dim) / FadeTime);
            if (wd != null && (s_on || s_weEnabled))
            {
                if (s_on && !wd.enabled) { wd.enabled = true; s_weEnabled = true; }
                if (s_weEnabled || before != s_level) wd.FadeIntensity = s_level;
                if (!s_on && s_level >= 1f && s_weEnabled) { wd.enabled = false; s_weEnabled = false; }
            }
            float k = (1f - s_level) / (1f - Dim);
            if (m_spot != null)
            {
                Light l = m_spot.GetComponent<Light>();
                l.intensity = SpotIntensity * Mathf.Clamp01(k);
                bool vis = s_on || s_level < 1f;
                if (m_spot.activeSelf != vis) m_spot.SetActive(vis);
                if (m_hole.activeSelf != vis) m_hole.SetActive(vis);
            }
        }
    }
}

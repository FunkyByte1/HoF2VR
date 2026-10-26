using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The challenge map's symbols at their pins (the third playtest, marks 2 and 6: "These buttons should be a lot bigger ... they
    /// need symbols. That's what it's called"). The game draws each challenge's marker - its tarot symbol, and the red banner with
    /// its name while it is chosen - on the flat screen, over where its pin shows from the game's map camera; in VR that screen is
    /// the panel beyond the map, so the symbols stood in a small row in front of the window, far from their pins. Now each marker
    /// is cropped from the UI texture onto its own plate standing over its pin on the map, MarkerMmPerPx real millimetres per UI
    /// pixel (several times its size on the panel), turned to the eyes the player stands with (fixed in the room: never with the
    /// head), and cut out of the panel.
    internal partial class TableBoards
    {
        public static bool MarkersAtPins = true;
        public static float MarkerMmPerPx = 1.25f, MarkerAbove = 0.075f;
        public static int MarkersShown;
        public static string MarkersWhere = "-";
        readonly List<Quad> m_markers = new List<Quad>();
        static readonly SceneScan.AllOf<ChallengeMapMarkerManager> s_markerManagers = new SceneScan.AllOf<ChallengeMapMarkerManager>();
        static readonly AccessTools.FieldRef<ChallengeMapMarkerManager, List<ChallengeMapMarker>> s_markerList =
            AccessTools.FieldRefAccess<ChallengeMapMarkerManager, List<ChallengeMapMarker>>("m_markers");
        static readonly AccessTools.FieldRef<ChallengeMapMarker, UISprite> s_markerIcon = AccessTools.FieldRefAccess<ChallengeMapMarker, UISprite>("m_iconSprite");
        static readonly List<UIWidget> s_markerWidgets = new List<UIWidget>();

        bool MarkersWanted => MarkersAtPins && XRSettings.enabled && VRRig.I != null && VRRig.I.HasStation && VRUI.I != null && VRUI.I.RT != null &&
                              Places.Current == Places.Place.Hub && GameWatch.State == "GameState_ChallengeMap" && !VRFade.IsBlack;

        /// Each frame (before the panel's holes are set): the markers' plates over their pins.
        void MapMarkers()
        {
            NavTabStrip();
            int n = 0;
            if (MarkersWanted)
            {
                EnsureMaterials();
                float sc = VRRig.I.Scale, perPx = MarkerMmPerPx * 0.001f * sc;
                Vector3 eye = VRRig.I.NeutralEye;
                foreach (ChallengeMapMarkerManager mm in s_markerManagers.Get())
                {
                    if (mm == null || !mm.isActiveAndEnabled) continue;
                    List<ChallengeMapMarker> list = s_markerList(mm);
                    if (list == null) continue;
                    foreach (ChallengeMapMarker m in list)
                    {
                        if (m == null || !m.isActiveAndEnabled || m.Location == null) continue;
                        UISprite icon = s_markerIcon(m);
                        Rect ir;
                        if (icon == null || !Shown(icon) || !Feedback.RtRect(icon, out ir)) continue;
                        // the icon and whatever of the marker shows with it (the banner and its name while chosen)
                        Rect r = default(Rect);
                        bool has = false;
                        s_markerWidgets.Clear();
                        m.GetComponentsInChildren(false, s_markerWidgets);
                        foreach (UIWidget w in s_markerWidgets) if (Shown(w)) GrowDrawn(ref r, ref has, w);
                        if (!has) continue;
                        r = Pad(r, 4f, 4f);
                        if (r.width < 6f || r.height < 6f || r.xMax <= 0f || r.yMax <= 0f || r.xMin >= VRUI.Width || r.yMin >= VRUI.Height) continue;
                        while (m_markers.Count <= n) m_markers.Add(new Quad());
                        Quad q = m_markers[n];
                        if (q.T == null) BuildMarker(q, "HoF2VR_MapMarker" + (n + 1));
                        q.Px = r;
                        // the icon's middle stands MarkerAbove over the pin; the plate faces the eyes, upright
                        Vector3 pin = m.Location.transform.position;
                        Vector3 at = pin + Vector3.up * (MarkerAbove * sc);
                        Vector3 look = at - eye;
                        if (look.sqrMagnitude < 1e-6f) look = Vector3.forward;
                        Quaternion rot = Quaternion.LookRotation(look, Vector3.up);
                        Vector2 off = (r.center - ir.center) * perPx;   // the plate's middle from the icon's
                        q.T.SetPositionAndRotation(at + rot * new Vector3(off.x, off.y, 0f), rot);
                        Fit(q, new Vector2(r.width * perPx, r.height * perPx), 0f);
                        m_holes.Add(r);
                        if (n == 0) MarkersWhere = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} at its pin ({1:0.000} m wide, {2:0.00} m from the eyes)",
                                                              m.Location.name, r.width * perPx / sc, Vector3.Distance(at, eye) / sc);
                        n++;
                    }
                }
            }
            for (int i = 0; i < m_markers.Count; i++) if (m_markers[i].T != null) Show(m_markers[i], i < n);
            if (n != MarkersShown && n > 0 && MarkersShown == 0) Plugin.L.LogInfo("boards: " + n + " challenge symbols over their pins (" + MarkersWhere + ")");
            MarkersShown = n;
            if (n == 0) MarkersWhere = "-";
        }

        // ---- the row of challenge tabs (the symbols in a row over the map, mark 2) -----------------------------------------
        /// The challenge map's tabs (UIChallengeNav: a symbol per challenge of the section) shown TabsGrow times bigger where they
        /// are on the panel (about their middle, never wider than the panel), a crop in front of it; the row is cut out of it.
        public static float TabsGrow = 2.6f, TabsNear = 0.7f, TabsMaxWidth = 0.9f;
        public static string TabsWhere = "-";
        // (the whole bottom group: the tabs, UIChallengeNav, and the chosen challenge's banner over them, UIChallengeNavDisplay_*
        // - "The Emperor": cropped apart, the banner's edge showed in the tabs' crop)
        const string NavTabsPath = "UIManager/UI Root (2D)/Camera/MenuManager/PrimaryMenus/ChallengeMap/Bottom";
        Transform m_navTabs;
        int m_navTabsScene = -1;
        Quad m_tabStrip;

        Quad m_bannerStrip;
        Transform m_navTabsRow, m_navBanner;

        /// The laid-out extent (shown or not) of a UI group's widgets on the UI texture: the chosen challenge's banner fades in and
        /// out, the crop keeps its size and place.
        static bool LaidOut(Transform root, out Rect r)
        {
            r = default(Rect);
            bool has = false;
            s_markerWidgets.Clear();
            root.GetComponentsInChildren(false, s_markerWidgets);
            foreach (UIWidget w in s_markerWidgets)
                if (w != null && w.enabled && w.gameObject.activeInHierarchy && w.width >= 2 && w.height >= 2 && w.GetType() != typeof(UIWidget)) GrowDrawn(ref r, ref has, w);
            if (has) r = Rect.MinMaxRect(Mathf.Max(0f, r.xMin - 2f), Mathf.Max(0f, r.yMin - 2f), Mathf.Min(VRUI.Width, r.xMax + 2f), Mathf.Min(VRUI.Height, r.yMax + 2f));
            return has && r.width > 8f && r.height > 8f;
        }

        /// The row of tabs (UIChallengeNav) and the chosen challenge's banner (UIChallengeNavDisplay_*) as two crops: brought
        /// TabsNear of the way from the eyes (seen where they were, bigger by 1/TabsNear) and grown by one factor (TabsGrow, the row
        /// never wider than TabsMaxWidth real metres), centred over the map, the banner standing on the row; both cut out of the
        /// panel.
        void NavTabStrip()
        {
            bool tabsOn = false, bannerOn = false;
            if (MarkersWanted && VRUI.I.Panel != null && VRUI.I.Panel.gameObject.activeInHierarchy)
            {
                if (m_navTabsScene != SceneScan.Version || m_navTabs == null)
                {
                    m_navTabsScene = SceneScan.Version;
                    GameObject go = GameObject.Find(NavTabsPath);
                    m_navTabs = go != null ? go.transform : null;
                    m_navTabsRow = m_navBanner = null;
                    if (m_navTabs != null)
                        foreach (Transform ch in m_navTabs)
                        {
                            if (ch.name == "UIChallengeNav") m_navTabsRow = ch;
                            else if (ch.name.StartsWith("UIChallengeNavDisplay", System.StringComparison.Ordinal) && (m_navBanner == null || ch.gameObject.activeInHierarchy)) m_navBanner = ch;
                        }
                }
                Rect tr, br = default(Rect);
                if (m_navTabsRow != null && m_navTabsRow.gameObject.activeInHierarchy && LaidOut(m_navTabsRow, out tr))
                {
                    EnsureMaterials();
                    if (m_tabStrip == null) m_tabStrip = new Quad();
                    if (m_tabStrip.T == null) BuildMarker(m_tabStrip, "HoF2VR_ChallengeTabs");
                    Transform pt = VRUI.I.Panel;
                    Vector3 ps = pt.lossyScale;
                    float sc = VRRig.I.Scale;
                    Vector3 eye = VRRig.I.NeutralEye;
                    float perPxX = ps.x / VRUI.Width * TabsNear, perPxY = ps.y / VRUI.Height * TabsNear;
                    float k = Mathf.Max(1f, Mathf.Min(TabsGrow, TabsMaxWidth * sc / Mathf.Max(1e-4f, tr.width * perPxX)));
                    // the row's middle: over the map's middle (the panel's), at its own height on the panel
                    Vector3 c = pt.TransformPoint(new Vector3(0f, tr.center.y / VRUI.Height - 0.5f, 0f));
                    c = eye + (c - eye) * TabsNear;
                    Vector2 ts = new Vector2(tr.width * perPxX * k, tr.height * perPxY * k);
                    m_tabStrip.Px = tr;
                    m_tabStrip.T.SetPositionAndRotation(c, pt.rotation);
                    Fit(m_tabStrip, ts, 0f);
                    m_holes.Add(tr);
                    tabsOn = true;
                    if (m_navBanner != null && m_navBanner.gameObject.activeInHierarchy && LaidOut(m_navBanner, out br))
                    {
                        if (m_bannerStrip == null) m_bannerStrip = new Quad();
                        if (m_bannerStrip.T == null) BuildMarker(m_bannerStrip, "HoF2VR_ChallengeBanner");
                        Vector2 bs = new Vector2(br.width * perPxX * k, br.height * perPxY * k);
                        Vector3 bc = c + pt.up * (ts.y * 0.5f + bs.y * 0.5f + 0.004f * sc);
                        m_bannerStrip.Px = br;
                        m_bannerStrip.T.SetPositionAndRotation(bc, pt.rotation);
                        Fit(m_bannerStrip, bs, 0f);
                        m_holes.Add(br);
                        bannerOn = true;
                    }
                    TabsWhere = string.Format(System.Globalization.CultureInfo.InvariantCulture, "row {0:0} px x{1:0.00}, {4:0.00} m from the eyes: {2:0.00} x {3:0.00} m ({5:0.0} times as big to the eye){6}",
                                              tr.width, k, ts.x / sc, ts.y / sc, Vector3.Distance(c, eye) / sc, k / TabsNear, bannerOn ? ", the banner " + br.width.ToString("0") + " px on it" : "");
                }
            }
            if (m_tabStrip != null) Show(m_tabStrip, tabsOn);
            if (m_bannerStrip != null) Show(m_bannerStrip, bannerOn);
            if (!tabsOn) TabsWhere = "-";
        }

        /// A marker's plate: the crop only (no backing, no frame: the symbol stands on its own over the map).
        void BuildMarker(Quad q, string name)
        {
            var go = new GameObject(name) { layer = VRUI.Layer };
            go.transform.SetParent(m_root, false);
            q.T = go.transform;
            q.M = new Mesh { name = name };
            q.M.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            q.M.triangles = new[] { 0, 1, 2, 1, 0, 3 };
            q.M.uv = new Vector2[4];
            go.AddComponent<MeshFilter>().sharedMesh = q.M;
            q.R = go.AddComponent<MeshRenderer>();
            q.R.sharedMaterial = m_crop;
            q.R.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            q.R.receiveShadows = false;
        }
    }
}

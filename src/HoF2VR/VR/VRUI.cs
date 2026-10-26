using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Shows the game's 2D NGUI screen on a panel in the world.
    /// The orthographic NGUI camera(s) render into a RenderTexture (transparent background); a quad on
    /// a private layer shows it, drawn by our own overlay camera after the game's cameras so the panel
    /// is never hidden by scenery. The panel sits in front of the rig anchor (not head-locked).
    internal class VRUI : MonoBehaviour
    {
        public static VRUI I;
        public const int Layer = 23;          // not rendered by the game's cameras (we strip it from them)
        public const int Width = 1920, Height = 1080;

        public RenderTexture RT;
        public Camera Overlay;
        public Transform Panel;
        public float Distance = 1.6f, PanelWidth = 1.9f, HeightOffset = -0.15f;
        /// The caravan's "screen": the flat game's table view (TableView.FlatFov high, 16:9) seen from the
        /// neutral eyes. The panel shows the 2D UI on it, TableDistance away (real metres) with its middle
        /// TablePitch degrees below eye level, and TableView lays the game's 3D UI (zoomed cards,
        /// encounter cards, chance cards) out on the same screen - so both line up as on the monitor.
        public float TableDistance = 0.75f, TablePitch = 9f;
        public bool Visible = true;

        readonly HashSet<int> m_redirected = new HashSet<int>();
        float m_hudYaw, m_hudPitch;
        bool m_hudInit, m_hudPaused;
        /// Combat HUD panel: width and distance (real metres), how far its middle sits below eye level,
        /// and how far (degrees) the head may turn away from it before it follows.
        public float HudWidth = 1.6f, HudDistance = 1.25f, HudDown = 0.05f, HudDeadzone = 28f;
        float m_panelYaw;
        bool m_panelYawInit;
        // the fight's description (UIShowcase) held in place in the world
        bool m_showcaseFixed;
        Vector3 m_showcasePos;
        Quaternion m_showcaseRot = Quaternion.identity;
        public static int ShowcaseFixes;
        /// The fight's description is up (GoPlates offers its touchable way on): set each frame it is placed.
        public static bool ShowcaseShowing;
        public static int ShowcaseFrame = -10;
        UIShowcase m_showcase;
        float m_nextShowcaseFind;
        static readonly HarmonyLib.AccessTools.FieldRef<UIShowcase, UIShowcasePanel> s_scLeft = HarmonyLib.AccessTools.FieldRefAccess<UIShowcase, UIShowcasePanel>("m_leftPanel");
        static readonly HarmonyLib.AccessTools.FieldRef<UIShowcase, UIShowcasePanel> s_scRight = HarmonyLib.AccessTools.FieldRefAccess<UIShowcase, UIShowcasePanel>("m_rightPanel");

        // ---- the challenge map's screen at the side table ---------------------------------------------------------------
        public static float MapPanelAbove = 0.45f, MapPanelHeight = 0.58f, MapPanelBeyond = 0.06f;
        Bounds m_pins;
        bool m_hasPins;
        float m_nextPins;
        public static string MapPanelWhere = "-";

        /// Where the screen goes while the challenge map is up at the side table: beyond the map's far edge (as the player
        /// faces it), its lower edge MapPanelAbove over the map, upright and square to the player's facing.
        bool HubMapPanel(out Vector3 pos, out Quaternion rot, out float height)
        {
            pos = Vector3.zero; rot = Quaternion.identity; height = 0f;
            if (!Settings.MapAtDeckSpot || Places.Current != Places.Place.Hub || GameWatch.State != "GameState_ChallengeMap" || VRRig.I == null) { m_hasPins = false; return false; }
            if (!m_hasPins || Time.unscaledTime >= m_nextPins)
            {
                m_nextPins = Time.unscaledTime + 2f;
                m_hasPins = false;
                foreach (ChallengeMapLocation l in FindObjectsOfType<ChallengeMapLocation>())
                {
                    if (!l.isActiveAndEnabled) continue;
                    if (!m_hasPins) { m_pins = new Bounds(l.transform.position, Vector3.zero); m_hasPins = true; }
                    else m_pins.Encapsulate(l.transform.position);
                }
            }
            if (!m_hasPins) return false;
            float sc = VRRig.I.Scale;
            Vector3 eye = VRRig.I.NeutralEye;
            Quaternion yaw = VRRig.I.NeutralRot;
            Vector3 fwd = yaw * Vector3.forward;
            // the map's far edge along the player's forward, from its corners
            float far = float.MinValue;
            Vector3 c = m_pins.center, e = m_pins.extents;
            for (int i = 0; i < 4; i++)
            {
                Vector3 corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x, 0f, (i & 2) == 0 ? -e.z : e.z);
                far = Mathf.Max(far, Vector3.Dot(corner - eye, fwd));
            }
            height = MapPanelHeight * sc;
            float mapY = m_pins.min.y;
            Vector3 mid = new Vector3(c.x, 0f, c.z);
            Vector3 along = eye + fwd * (far + MapPanelBeyond * sc);
            // centred on the map sideways, beyond its far edge
            Vector3 side = yaw * Vector3.right;
            float lateral = Vector3.Dot(mid - new Vector3(eye.x, 0f, eye.z), side);
            pos = new Vector3(along.x, mapY + MapPanelAbove * sc + height * 0.5f, along.z) + side * lateral;
            rot = yaw;
            MapPanelWhere = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00} m ahead, lower edge {1:0.00} m over the map, {2:0.00} m tall",
                (far + MapPanelBeyond * sc) / sc, MapPanelAbove, MapPanelHeight);
            return true;
        }

        /// The game's showcase (the enemy or equipment description before a fight) is on screen.
        bool ShowcaseUp()
        {
            if (GameWatch.State != "GameState_Level_In") return false;   // it shows only while the fight is introduced
            if (m_showcase == null)
            {
                if (Time.unscaledTime < m_nextShowcaseFind) return false;
                m_nextShowcaseFind = Time.unscaledTime + 1f;
                m_showcase = FindObjectOfType<UIShowcase>();
                if (m_showcase == null) return false;
            }
            if (!m_showcase.isActiveAndEnabled) return false;
            UIShowcasePanel l = s_scLeft(m_showcase), r = s_scRight(m_showcase);
            return (l != null && l.gameObject.activeInHierarchy) || (r != null && r.gameObject.activeInHierarchy);
        }

        /// At the caravan: the direction from the player's eyes to what the game's table camera is
        /// framing (where its view meets the table top). Shots of the table itself stay straight
        /// ahead; the side table (challenge map, deck builder) turns the panel that way.
        static float TableFocusYaw(Vector3 eye, float stationYaw)
        {
            // the game's intended shot (TableView puts the camera rig itself at the player's screen);
            // side-table shots get their own spot (Places.Hub) instead of a turned panel
            if (!TableView.HasShot || Places.HubShot()) return stationYaw;
            Vector3 camPos = TableView.ShotPos;
            Vector3 fwd = TableView.ShotRot * Vector3.forward;
            float tableTop = VRRig.I.AnchorPos.y + 1.0f * VRRig.I.Scale;
            Vector3 focus = camPos + fwd * 2f;
            if (fwd.y < -0.05f)
            {
                float t = (camPos.y - tableTop) / -fwd.y;
                if (t > 0f && t < 6f) focus = camPos + fwd * t;
            }
            Vector3 d = focus - eye;
            d.y = 0f;
            if (d.sqrMagnitude < 1e-4f) return stationYaw;
            float bearing = Quaternion.LookRotation(d).eulerAngles.y;
            return Mathf.Abs(Mathf.DeltaAngle(bearing, stationYaw)) > 50f ? bearing : stationYaw;
        }
        /// Which way the panel (outside fights) wants to face: the place's way. (At the card table it used to
        /// turn to whatever the game's camera framed when that was well off to the side - in the user's session
        /// 2026-10-11 it swung 89 degrees to the right during the challenge setup, taking the text, the prompts
        /// and the cards the game presents with it, away from the table. `tablefocus 1` brings it back.)
        float PanelTargetYaw()
        {
            float yaw = VRRig.I.AnchorYaw;
            float map;
            if (Places.AtCaravan && MapFocus.PanelYaw(VRRig.I.NeutralEye, out map)) return map;   // the challenge map's text above the map
            return Places.AtCaravan && s_tableFocus ? TableFocusYaw(VRRig.I.NeutralEye, yaw) : yaw;
        }

        static bool s_tableFocus;

        /// A new place (called in the dark): the panel is there at once, not turning from the old one.
        public static float TitleLift = 3f;

        public void SnapPanel()
        {
            if (VRRig.I == null) return;
            m_panelYaw = PanelTargetYaw();
            m_panelYawInit = true;
        }

        /// The caravan's "screen": the neutral eyes looking at the middle of the 2D panel (the panel
        /// shows the flat screen's 2D UI; TableView puts the game's 3D UI camera rig here so zoomed
        /// cards line up with it as on the flat screen).
        public void TableScreen(out Vector3 eye, out Quaternion rot)
        {
            eye = VRRig.I.NeutralEye;
            float yaw = m_panelYawInit ? m_panelYaw : VRRig.I.AnchorYaw;
            rot = Quaternion.Euler(0f, yaw, 0f) * Quaternion.Euler(TablePitch, 0f, 0f);
        }

        /// The menu panel's "screen" (title, campfire, other stations): the neutral eyes looking at the middle of
        /// the panel, and the panel's height as seen from there. A flat 16:9 camera with this pose and vertical
        /// field of view sees each panel pixel exactly where the panel shows it, so the title logo (which the
        /// game pins to a point of its camera's screen) lands where the flat game puts it, next to the 2D menu.
        public void MenuScreen(out Vector3 eye, out Quaternion rot, out float vfov)
        {
            float sc = VRRig.I.Scale;
            eye = VRRig.I.NeutralEye;
            float dist = Distance * sc, width = PanelWidth * sc, up = HeightOffset * sc;
            float yaw = m_panelYawInit ? m_panelYaw : VRRig.I.AnchorYaw;
            // (looking TitleLift degrees above the panel's middle: the logo, and the menu below it, sit a little
            // higher than on a monitor, so the goblin anchored to the screen's bottom stays clear of the menu)
            rot = Quaternion.Euler(0f, yaw, 0f) * Quaternion.Euler(Mathf.Atan2(-up, dist) * Mathf.Rad2Deg - TitleLift, 0f, 0f);
            float d = Mathf.Sqrt(dist * dist + up * up);
            vfov = 2f * Mathf.Atan(width * Height / Width * 0.5f / d) * Mathf.Rad2Deg;
        }

        readonly List<UIWidget> m_letterbox = new List<UIWidget>();

        /// The cinematic black bars (MenuManager/Shared/Top|Bottom/BlackBar_*) would hang in mid-air. (Found by
        /// the widget scan below, which runs at scene loads, not every 2 s.)
        void HideLetterbox()
        {
            if (m_scanAt.Count > 0 || m_blackoutScanDue) GameBlackout();
            foreach (UIWidget w in m_letterbox)
                if (w != null && w.alpha > 0f) w.alpha = 0f;
            // The death / failure screens' full-screen backdrops (MenuManager/Shared/Failure/FailureTransitions/<kind>/BG and
            // BlackBackground/Sprite): on a monitor they darken the whole screen behind "You died"; on the panel they were a
            // dimmed rectangle round the text (user 2026-10-11). The sprite component is switched off: the transitions' colour
            // tweens may still write its colour (harmless), and Transition.Activate toggles GameObjects, never this component.
            foreach (UIWidget w in m_failureBg)
                if (w != null && w.enabled) w.enabled = false;
            // the dice screen's right-hand backdrop (DiceMenu/DiceUI/Right/RollStatus: a purple patterned texture 915x1830, a dark purple strip 316x1170 and a
            // dark gradient square behind "Rolls remaining / Target / Score"): made for the screen's right third, on the panel at
            // the table it stood as a purple wall or, cut round the boards, purple strips over the table (playtest 2026-10-17,
            // marks 6, 7). Only the texts and their frame stay.
            foreach (UIWidget w in m_diceBg)
                if (w != null && w.enabled) w.enabled = false;
            // ... and the roll status itself (rolls remaining, target, score) brought in from the screen's right edge: at the
            // table that edge stood at the top right of the view, cut off (candidate 8's walk_03_dice_waiting); RollShift (UI
            // pixels) puts it lower and nearer the middle, in view while looking at the dice. Put back away from the table.
            if (m_rollStatus != null)
            {
                bool shift = Places.AtCaravan && XRSettings.enabled;
                Vector3 want = m_rollHome + (shift ? RollShift : Vector3.zero);
                if ((m_rollStatus.localPosition - want).sqrMagnitude > 0.01f) m_rollStatus.localPosition = want;
            }
            // ... and the death screens' full-screen tints that are plain meshes, not NGUI widgets (Death_Bleed/gui_death_overlay,
            // a red smoky vignette drawn black on the panel's texture): on the panel they were a dim, smoky box round the death
            // text during the quick fade after a death (the virtual playtester's PT_death; the user: "no dim box")
            foreach (Renderer r in m_failureOverlays)
                if (r != null && r.enabled) r.enabled = false;
            HideFloatingTexts(EndRunShowing());
        }

        // The fight's floating numbers (UI Root (2D)/FloatingTextManager: damage, gold) while the death text shows: the
        // killing blow's number stayed in a corner of the panel (a red "7" beside the death text, PT_death). CombatHud hides
        // each text from its own Update, but a finished one no longer updates; the whole root is scaled to zero instead.
        Transform m_floatRoot;
        int m_floatScene = -1;
        Vector3 m_floatScale = Vector3.one;
        bool m_floatHidden;

        void HideFloatingTexts(bool hide)
        {
            if (hide && m_floatScene != SceneScan.Version)
            {
                m_floatScene = SceneScan.Version;
                if (m_floatRoot == null)
                {
                    GameObject go = GameObject.Find("UIManager/UI Root (2D)/FloatingTextManager");
                    m_floatRoot = go != null ? go.transform : null;
                }
            }
            if (m_floatRoot == null) return;
            if (hide && !m_floatHidden)
            {
                m_floatScale = m_floatRoot.localScale;
                m_floatRoot.localScale = Vector3.zero;
                m_floatHidden = true;
            }
            else if (!hide && m_floatHidden)
            {
                m_floatRoot.localScale = m_floatScale;
                m_floatHidden = false;
            }
        }

        readonly List<UIWidget> m_failureBg = new List<UIWidget>();
        readonly List<UIWidget> m_diceBg = new List<UIWidget>();
        Transform m_rollStatus;
        Vector3 m_rollHome;
        public static Vector3 RollShift = new Vector3(-300f, -260f, 0f);
        public static int DiceBackdropsHidden => I != null ? I.m_diceBg.Count : 0;
        readonly List<Renderer> m_failureOverlays = new List<Renderer>();
        public static int FailureOverlaysHidden => I != null ? I.m_failureOverlays.Count : 0;

        /// MenuManager/Shared/Failure/FailureTransitions (found through the failure backdrops), or null.
        Transform FailureRoot()
        {
            foreach (UIWidget w in m_failureBg)
            {
                if (w == null || w.transform.parent == null || w.transform.parent.parent == null) continue;
                Transform ft = w.transform.parent.parent;
                if (ft.name != "FailureTransitions") ft = ft.parent;
                if (ft != null && ft.name == "FailureTransitions") return ft;
            }
            return null;
        }

        static bool IsFailureBackdrop(UIWidget w)
        {
            Transform t = w.transform, p = t.parent;
            if (p == null || p.parent == null || p.parent.name != "FailureTransitions" && (p.parent.parent == null || p.parent.parent.name != "FailureTransitions")) return false;
            return t.name == "BG" || (t.name == "Sprite" && p.name == "BlackBackground");
        }

        /// The death / end-of-run text is up (Level_Out's "You died" menu stays from the death until ~2 s into GameState_Post).
        public static bool EndRunShowing()
        {
            try
            {
                MenuManager mm = MenuManager.Instance;
                return mm != null && mm.EndRunMenuManager != null && mm.EndRunMenuManager.Current != null;
            }
            catch (System.Exception) { return false; }
        }

        /// The death / end-of-run text the game shows now: its active FailureTransitions kind's title and subtitle
        /// (the waiting-space board shows them in place of the 2D panel).
        public static bool FailureText(out string title, out string sub)
        {
            title = sub = "";
            if (I == null) return false;
            UILabel tl, sl;
            UIWidget backing;
            if (!FailureLabels(out tl, out sl, out backing)) return false;
            title = tl.text;
            sub = sl != null ? sl.text : "";
            return true;
        }

        /// The death / end-of-run text's labels as the game shows them now (the active FailureTransitions kind's Title and
        /// Subtitle; `sub` null when it has none) and the dark blotch behind them (FailureTransitions/*Backing*, or null).
        public static bool FailureLabels(out UILabel title, out UILabel sub, out UIWidget backing)
        {
            title = sub = null;
            backing = null;
            Transform ft = I != null ? I.FailureRoot() : null;
            if (ft == null || !ft.gameObject.activeInHierarchy) return false;
            foreach (Transform kind in ft)
            {
                if (!kind.gameObject.activeInHierarchy) continue;
                if (backing == null && kind.name.IndexOf("Backing", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    UIWidget b = kind.GetComponentInChildren<UIWidget>();
                    if (b != null && b.enabled && b.finalAlpha > 0.01f) backing = b;
                    continue;
                }
                if (title != null) continue;
                Transform t = kind.Find("Title"), s = kind.Find("Subtitle");
                UILabel tl = t != null ? t.GetComponent<UILabel>() : null, sl = s != null ? s.GetComponent<UILabel>() : null;
                if (tl == null || !tl.gameObject.activeInHierarchy || string.IsNullOrEmpty(tl.text)) continue;
                title = tl;
                sub = sl != null && sl.gameObject.activeInHierarchy && !string.IsNullOrEmpty(sl.text) ? sl : null;
            }
            return title != null;
        }

        /// In the waiting space the game's 2D screen is put away (it belongs to the place you are going to, and the
        /// loading spinner has no meaning there) except for the boot's splash logos and the death / end-of-run text.
        public static bool PanelInWaitingSpace()
        {
            string st = GameWatch.State;
            // the boot: the splash logos (with Comfort.FastLoading they are skipped: then only the game's loading spinner
            // would show - the board says what is happening)
            // (with the intros: only while the logos are on - the loading screen after them is just the game's spinner)
            if (st == "" || st == "GameState_Init") return !FastLoad.SkipsIntros && SplashLogosShowing();
            // the death text: on the board when there is one (no panel, no dimmed rectangle)
            return EndRunShowing() && !(Settings.WaitingBoard && WaitBoard.Showing);
        }
        public static int PanelHidden;   // diagnostics: frames the panel was put away in the waiting space

        static readonly HarmonyLib.AccessTools.FieldRef<Loader, bool> s_splashDone = HarmonyLib.AccessTools.FieldRefAccess<Loader, bool>("m_splashComplete");
        static Loader s_loader;
        static float s_nextLoaderFind;

        /// The boot's logo screens are still playing (the Loader's splash sequence). After them the Loader shows its loading
        /// menu - only the turning spinner, which has no meaning in the waiting space (the board says what is happening).
        static bool SplashLogosShowing()
        {
            if (s_loader == null && Time.unscaledTime >= s_nextLoaderFind)
            {
                s_nextLoaderFind = Time.unscaledTime + 1f;
                s_loader = UnityEngine.Object.FindObjectOfType<Loader>();
            }
            try { return s_loader != null && !s_splashDone(s_loader); }
            catch (System.Exception) { return false; }
        }
        // ---- the game's own full-screen black overlays ----------------------------------------------
        // On a monitor the loading screen's black backdrop covers the screen and fades out as a place
        // appears; on the panel it was a black rectangle floating in the room (the first map after the
        // title). In VR the whole view goes dark with it instead (VRFade follows its alpha). Only
        // overlays that were fully opaque count, so a dimmed menu backdrop never darkens the world.
        sealed class Blackout { public UIWidget W; public bool Opaque; }
        readonly List<Blackout> m_blackouts = new List<Blackout>();
        readonly HashSet<int> m_seenWidgets = new HashSet<int>();
        bool m_blackoutScanDue = true;
        // when to scan the UI widgets again: 1 s and 4 s after each scene load (anchored widgets get their
        // full-screen size only once laid out), and after a game-state change if the last scan is 20 s old.
        // (It used to scan every 10 s and on every state change: a whole-world search costs milliseconds.)
        readonly List<float> m_scanAt = new List<float>();
        float m_lastWidgetScan = -100f;
        public static int WidgetScans;

        /// A widget scan `delay` s from now - unless one is already due within a second of that (several scenes
        /// load in a row at a place change).
        void ScanLater(float delay)
        {
            float t = Time.unscaledTime + delay;
            foreach (float x in m_scanAt) if (Mathf.Abs(x - t) < 1f) return;
            m_scanAt.Add(t);
        }
        int m_blackoutFrame = -1;
        float m_blackout;
        public static string BlackoutBy = "";

        /// Alpha of the game's full-screen black 2D overlay this frame (0 when there is none).
        public float GameBlackout()
        {
            if (m_blackoutFrame == Time.frameCount) return m_blackout;
            m_blackoutFrame = Time.frameCount;
            bool due = m_blackoutScanDue;
            for (int i = m_scanAt.Count - 1; i >= 0; i--)
                if (Time.unscaledTime >= m_scanAt[i]) { due = true; m_scanAt.RemoveAt(i); }
            if (due) ScanBlackouts();
            float a = 0f;
            string by = "";
            foreach (Blackout b in m_blackouts)
            {
                UIWidget w = b.W;
                float wa = w != null && (w.enabled || m_suppressed.Contains(w)) && w.gameObject.activeInHierarchy ? w.finalAlpha : 0f;
                if (wa >= 0.95f) b.Opaque = true;
                else if (wa <= 0.01f) b.Opaque = false;
                if (b.Opaque && wa > a) { a = wa; by = Path(w.transform); }
            }
            if ((a > 0f) != (m_blackout > 0f)) Plugin.L.LogInfo("VRUI: the game's black overlay " + (a > 0f ? "'" + by + "' darkens the view" : "is gone"));
            if (a > 0f && by != BlackoutBy && BlackoutBy.Length > 0) Plugin.L.LogInfo("VRUI: black overlay now '" + by + "'");
            m_blackout = a;
            BlackoutBy = by;
            return a;
        }

        // In the waiting space (Limbo) the loading screen's black backdrop would float in the clearing as
        // a black rectangle: it is switched off on the panel while the space shows (its alpha still
        // counts above), and switched back on afterwards unless the game changed it meanwhile.
        readonly HashSet<UIWidget> m_suppressed = new HashSet<UIWidget>();
        bool m_suppressing;

        public void SuppressBlackouts(bool on)
        {
            if (on)
            {
                m_suppressing = true;
                foreach (Blackout b in m_blackouts)
                {
                    UIWidget w = b.W;
                    if (w == null || !w.enabled) continue;
                    // (also when the game switched it on again meanwhile)
                    w.enabled = false;
                    if (m_suppressed.Add(w)) Plugin.L.LogInfo("VRUI: '" + Path(w.transform) + "' kept off the panel in the waiting space");
                }
                return;
            }
            if (!m_suppressing) return;
            m_suppressing = false;
            foreach (UIWidget w in m_suppressed)
                if (w != null && !w.enabled) w.enabled = true;
            m_suppressed.Clear();
        }

        void ScanBlackouts()
        {
            m_blackoutScanDue = false;
            m_lastWidgetScan = Time.unscaledTime;
            WidgetScans++;
            m_letterbox.RemoveAll(x => x == null);
            m_failureBg.RemoveAll(x => x == null);
            m_diceBg.RemoveAll(x => x == null);
            foreach (UIWidget w in Resources.FindObjectsOfTypeAll<UIWidget>())
            {
                if (w == null || !w.gameObject.scene.IsValid()) continue;
                Transform lp = w.transform.parent;
                if (lp != null && (lp.name == "BlackBar_Top" || lp.name == "BlackBar_Bottom") && !m_letterbox.Contains(w))
                {
                    m_letterbox.Add(w);
                    Plugin.L.LogInfo("VRUI: hiding letterbox bar '" + Path(w.transform) + "'");
                }
                if (lp != null && IsFailureBackdrop(w) && !m_failureBg.Contains(w))
                {
                    m_failureBg.Add(w);
                    Plugin.L.LogInfo("VRUI: hiding the failure screen backdrop '" + Path(w.transform) + "' in VR (it dimmed a rectangle round the text)");
                }
                if (lp != null && lp.name == "RollStatus" && (w.name == "Pattern" || w.name == "DarkenedBacking" || w.name == "BlurredBacking") && lp.parent != null && lp.parent.name == "Right" &&
                    !m_diceBg.Contains(w))
                {
                    m_diceBg.Add(w);
                    if (m_rollStatus == null) { m_rollStatus = lp; m_rollHome = lp.localPosition; }
                    Plugin.L.LogInfo("VRUI: hiding the dice screen's backdrop '" + Path(w.transform) + "' in VR (a purple wall / strips at the table)");
                }
                if (w.gameObject.layer != 17) continue;
                if (w.width < 1500 || w.height < 800) continue;
                if (!m_seenWidgets.Add(w.GetInstanceID())) continue;
                Color c = w.color;
                bool dark = c.a >= 0.95f && Mathf.Max(c.r, Mathf.Max(c.g, c.b)) <= 0.12f;
                Plugin.L.LogInfo("VRUI: full-screen widget '" + Path(w.transform) + "' " + w.width + "x" + w.height + " " + w.GetType().Name + " color " + c +
                                 (dark ? " - black: the view darkens with it" : ""));
                if (dark) m_blackouts.Add(new Blackout { W = w });
            }
            m_blackouts.RemoveAll(b => b.W == null);
            m_failureOverlays.RemoveAll(x => x == null);
            Transform froot = FailureRoot();
            if (froot != null)
                foreach (Renderer r in froot.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || m_failureOverlays.Contains(r) || r.GetComponent("UIDrawCall") != null) continue;
                    m_failureOverlays.Add(r);
                    Plugin.L.LogInfo("VRUI: hiding the failure screen overlay '" + Path(r.transform) + "' in VR (a full-screen tint: a dim box round the text on the panel)");
                }
        }

        static bool s_noOverlay, s_noRedirect;
        readonly List<Camera> m_uiCams = new List<Camera>();
        float m_nextScan;
        int m_lastCamCount = -1;

        /// NGUI's own reaction to a screen resize: every anchor and stretch is laid out again (on the
        /// panel's 1920x1080 instead of the headset eye's size).
        static void Relayout(string why)
        {
            try
            {
                UIRoot.Broadcast("UpdateAnchors");
                if (UICamera.onScreenResize != null) UICamera.onScreenResize();
                Plugin.L.LogInfo("VRUI: 2D UI laid out again for the panel (" + why + ")");
            }
            catch (System.Exception e) { Plugin.L.LogWarning("VRUI: relayout failed: " + e.Message); }
        }

        void Awake()
        {
            I = this;
            Diag.Commands["rtshot"] = a => DumpRT(a.Length > 1 ? a[1] : "rt");
            Diag.Commands["uicams"] = a => DescribeUiCams();
            Diag.Commands["overlaymsaa"] = a => { if (Overlay != null) Overlay.allowMSAA = a.Length > 1 && a[1] == "1"; return "overlay allowMSAA=" + (Overlay != null && Overlay.allowMSAA); };
            Diag.Commands["tablepanel"] = a =>
            {
                if (a.Length > 2) { TableDistance = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture); TablePitch = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture); }
                return "table panel d=" + TableDistance + " pitch=" + TablePitch + " fov=" + TableView.FlatFov;
            };
            // the 2D UI cameras must render into the panel before the UI lays itself out (its corner
            // anchors use the camera's pixel size, which is the headset eye's while it is a stereo camera)
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (sc, m) =>
            {
                if (XRSettings.enabled && RT != null) ScanCameras();
                ScanLater(1f);
                ScanLater(4f);
            };
            GameWatch.Changed += st => { if (Time.unscaledTime - m_lastWidgetScan > 20f) ScanLater(0.5f); };
            Diag.StatusExtras["blackout"] = () => Diag.Q(m_blackout > 0f ? BlackoutBy + " " + m_blackout.ToString("0.00") : "");
            Diag.Commands["hud"] = a =>
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (a.Length > 4) { HudWidth = float.Parse(a[1], inv); HudDistance = float.Parse(a[2], inv); HudDown = float.Parse(a[3], inv); HudDeadzone = float.Parse(a[4], inv); }
                return "hud w=" + HudWidth + " d=" + HudDistance + " down=" + HudDown + " deadzone=" + HudDeadzone + " yaw=" + m_hudYaw.ToString("0");
            };
            Diag.Commands["relayout"] = a => { Relayout("command"); return "relayout"; };
            Diag.Commands["tablefocus"] = a => { if (a.Length > 1) s_tableFocus = a[1] == "1"; return "table panel follows the game's shots: " + s_tableFocus; };
            Diag.Commands["holes"] = a =>
            {
                var sb = new System.Text.StringBuilder();
                foreach (Rect h in m_holes) sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, " ({0:0},{1:0})-({2:0},{3:0})", h.xMin, h.yMin, h.xMax, h.yMax);
                return m_holes.Count + " holes" + sb;
            };
            Diag.Commands["panel"] = a =>
            {
                if (a.Length > 2) { Distance = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture); PanelWidth = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture); }
                return "panel d=" + Distance + " w=" + PanelWidth + " uiCams=" + m_uiCams.Count;
            };
        }

        string DumpRT(string name)
        {
            var tex = new Texture2D(Width, Height, TextureFormat.ARGB32, false);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = RT;
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            string dir = System.IO.Path.Combine(TestMode.Dir, "shots");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name + "_ui.png"), tex.EncodeToPNG());
            Destroy(tex);
            return "saved " + name + "_ui.png";
        }

        string DescribeUiCams()
        {
            var sb = new System.Text.StringBuilder();
            foreach (Camera c in Camera.allCameras)
                if (c.targetTexture == RT)
                    sb.AppendFormat("\n  '{0}' depth={1} mask=0x{2:X} clear={3} ortho={4} size={5} aspect={8:0.###} pixelRect={9} proj00={10:0.####} scene={6} path={7}", c.name, c.depth, c.cullingMask, c.clearFlags,
                        c.orthographic, c.orthographicSize, c.gameObject.scene.name, Path(c.transform), c.aspect, c.pixelRect, c.projectionMatrix.m00);
            Plugin.L.LogInfo("UI cameras:" + sb);
            return "logged";
        }

        /// Where a world ray crosses the panel, in UI texture pixels (origin bottom-left). At the card table the
        /// story text above the Dealer and the choice plates (TableBoards) are parts of the same texture shown
        /// elsewhere: they are tested first, and the panel's holes where they were don't count.
        public bool HitPanel(Ray ray, out Vector2 px, out float dist)
        {
            px = Vector2.zero;
            dist = 0f;
            if (Panel == null || !Panel.gameObject.activeInHierarchy) return false;
            if (TableBoards.I != null && TableBoards.I.Hit(ray, out px, out dist)) return true;
            var plane = new Plane(Panel.forward, Panel.position);
            if (!plane.Raycast(ray, out dist) || dist <= 0f) return false;
            Vector3 local = Panel.InverseTransformPoint(ray.GetPoint(dist));
            if (Mathf.Abs(local.x) > 0.5f || Mathf.Abs(local.y) > 0.5f) return false;
            px = new Vector2((local.x + 0.5f) * Width, (local.y + 0.5f) * Height);
            foreach (Rect h in m_holes) if (h.Contains(px)) return false;
            return true;
        }

        // ---- holes: parts of the UI texture shown elsewhere (TableBoards) are left out of the panel ----
        readonly List<Rect> m_holes = new List<Rect>();
        Mesh m_panelMesh, m_quadMesh;
        readonly List<Vector3> m_hv = new List<Vector3>();
        readonly List<Vector2> m_huv = new List<Vector2>();
        readonly List<int> m_ht = new List<int>();
        readonly List<float> m_xs = new List<float>(), m_ys = new List<float>();

        /// The panel shows the whole texture except these pixel rectangles (rebuilt only when they change).
        public void SetHoles(List<Rect> holes)
        {
            if (Panel == null) return;
            bool same = holes.Count == m_holes.Count;
            for (int i = 0; same && i < holes.Count; i++)
            {
                Rect a = holes[i], b = m_holes[i];
                same = Mathf.Abs(a.xMin - b.xMin) < 0.5f && Mathf.Abs(a.yMin - b.yMin) < 0.5f && Mathf.Abs(a.xMax - b.xMax) < 0.5f && Mathf.Abs(a.yMax - b.yMax) < 0.5f;
            }
            if (same) return;
            m_holes.Clear();
            m_holes.AddRange(holes);
            MeshFilter mf = Panel.GetComponent<MeshFilter>();
            if (m_quadMesh == null) m_quadMesh = mf.sharedMesh;
            if (m_holes.Count == 0) { mf.sharedMesh = m_quadMesh; return; }
            if (m_panelMesh == null) m_panelMesh = new Mesh { name = "HoF2VR_PanelWithHoles" };
            // a grid through every hole edge; cells inside a hole are left out
            m_xs.Clear(); m_ys.Clear();
            m_xs.Add(0f); m_xs.Add(Width); m_ys.Add(0f); m_ys.Add(Height);
            foreach (Rect h in m_holes)
            {
                m_xs.Add(Mathf.Clamp(h.xMin, 0f, Width)); m_xs.Add(Mathf.Clamp(h.xMax, 0f, Width));
                m_ys.Add(Mathf.Clamp(h.yMin, 0f, Height)); m_ys.Add(Mathf.Clamp(h.yMax, 0f, Height));
            }
            m_xs.Sort(); m_ys.Sort();
            m_hv.Clear(); m_huv.Clear(); m_ht.Clear();
            for (int i = 0; i + 1 < m_xs.Count; i++)
                for (int j = 0; j + 1 < m_ys.Count; j++)
                {
                    float x0 = m_xs[i], x1 = m_xs[i + 1], y0 = m_ys[j], y1 = m_ys[j + 1];
                    if (x1 - x0 < 0.01f || y1 - y0 < 0.01f) continue;
                    var c = new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f);
                    bool hole = false;
                    foreach (Rect h in m_holes) if (h.Contains(c)) { hole = true; break; }
                    if (hole) continue;
                    int k = m_hv.Count;
                    m_hv.Add(new Vector3(x0 / Width - 0.5f, y0 / Height - 0.5f, 0f)); m_huv.Add(new Vector2(x0 / Width, y0 / Height));
                    m_hv.Add(new Vector3(x1 / Width - 0.5f, y1 / Height - 0.5f, 0f)); m_huv.Add(new Vector2(x1 / Width, y1 / Height));
                    m_hv.Add(new Vector3(x1 / Width - 0.5f, y0 / Height - 0.5f, 0f)); m_huv.Add(new Vector2(x1 / Width, y0 / Height));
                    m_hv.Add(new Vector3(x0 / Width - 0.5f, y1 / Height - 0.5f, 0f)); m_huv.Add(new Vector2(x0 / Width, y1 / Height));
                    m_ht.Add(k); m_ht.Add(k + 1); m_ht.Add(k + 2); m_ht.Add(k + 1); m_ht.Add(k); m_ht.Add(k + 3);
                }
            m_panelMesh.Clear();
            m_panelMesh.SetVertices(m_hv);
            m_panelMesh.SetUVs(0, m_huv);
            m_panelMesh.SetTriangles(m_ht, 0);
            m_panelMesh.RecalculateBounds();
            mf.sharedMesh = m_panelMesh;
        }

        /// For 2D labels the game pins to 3D objects (shop prices, card counters, tooltips): instead of
        /// the stereo camera's flat-screen projection, the UI-texture pixel where the line from the
        /// eyes to the object crosses the panel's plane - the label then shows in front of its object.
        /// z = distance, like Camera.WorldToScreenPoint. Other cameras are passed through.
        public static Vector3 WorldToScreenForUi(Camera cam, Vector3 world)
        {
            if (!XRSettings.enabled || I == null || I.Panel == null || VRRig.I == null || cam == null ||
                cam.stereoTargetEye == StereoTargetEyeMask.None || cam.targetTexture != null)
                return cam.WorldToScreenPoint(world);
            // At the title the 2D menu hangs below the 3D logo (5 m away): seen from the live head it slid across
            // the panel whenever the head moved (turning toward it pushed it off the panel's right edge, and the
            // goblin in the corner seemed to wander over it). From the neutral eyes it keeps the flat game's
            // layout on the panel. At the table, labels on cards follow the head so they stay on their cards.
            Vector3 eye = Places.Current == Places.Place.Title ? VRRig.I.NeutralEye : VRRig.I.HeadWorldPos;
            var ray = new Ray(eye, world - eye);
            var plane = new Plane(I.Panel.forward, I.Panel.position);
            float d;
            if (!plane.Raycast(ray, out d) || d <= 0f) return new Vector3(-100000f, -100000f, -1f);
            Vector3 local = I.Panel.InverseTransformPoint(ray.GetPoint(d));
            float x = (local.x + 0.5f) * Width, y = (local.y + 0.5f) * Height;
            // Objects just past the panel's edge (cards lying low on the table) keep their label at
            // the edge, above the button bar, instead of losing it; far-off ones stay hidden.
            const float Reach = 300f, MinY = 110f, Margin = 40f;
            if (x > -Reach && x < Width + Reach && y > -Reach && y < Height + Reach)
            {
                x = Mathf.Clamp(x, Margin, Width - Margin);
                y = Mathf.Clamp(y, MinY, Height - Margin);
            }
            return new Vector3(x, y, (world - eye).magnitude);
        }

        public Vector3 PanelPixelToWorld(Vector2 px)
        {
            return Panel.TransformPoint(new Vector3(px.x / Width - 0.5f, px.y / Height - 0.5f, 0f));
        }

        /// The UI camera (rendering into RT) for screen<->pixel conversions.
        public Camera UiCamera
        {
            get
            {
                foreach (Camera c in m_uiCams) if (c != null && c.enabled) return c;
                return null;
            }
        }

        public static string Path(Transform t)
        {
            string p = t.name;
            for (Transform x = t.parent; x != null; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        void Create()
        {
            // Mipmaps + trilinear: the panel shows 1920 UI pixels in ~1300 eye pixels; without mips small
            // text shimmers as the head moves.
            RT = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
            {
                name = "HoF2VR_UI", antiAliasing = 1, useMipMap = true, autoGenerateMips = true,
                filterMode = FilterMode.Trilinear, anisoLevel = 4,
            };
            RT.Create();

            var camGo = new GameObject("HoF2VR_Overlay");
            DontDestroyOnLoad(camGo);
            Overlay = camGo.AddComponent<Camera>();
            Overlay.clearFlags = CameraClearFlags.Depth;
            Overlay.cullingMask = 1 << Layer;
            Overlay.depth = 100;
            Overlay.renderingPath = RenderingPath.Forward;
            Overlay.nearClipPlane = 0.03f;
            Overlay.farClipPlane = 200f;
            // Match the game's eye-texture setup (MSAA eye textures). A non-MSAA camera drawing into
            // the MSAA eye texture triggered a GPU page fault on Vulkan.
            Overlay.allowHDR = false;
            Overlay.allowMSAA = true;
            Overlay.stereoTargetEye = StereoTargetEyeMask.Both;
            XRDevice.DisableAutoXRCameraTracking(Overlay, true);
            camGo.AddComponent<OverlayMarker>();

            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(quad.GetComponent<Collider>());
            quad.name = "HoF2VR_Panel";
            quad.layer = Layer;
            DontDestroyOnLoad(quad);
            // drawn after the fade quad: while the world is dark (loading, the trip into a fight) the
            // game's menus and dialogs stay readable
            var mat = new Material(Shader.Find("Unlit/Premultiplied Colored")) { mainTexture = RT, renderQueue = 5100 };
            quad.GetComponent<MeshRenderer>().sharedMaterial = mat;
            Panel = quad.transform;
            Plugin.L.LogInfo("VRUI: panel created");
        }

        void LateUpdate()
        {
            if (!XRSettings.enabled) return;
            if (RT == null)
            {
                string dbg = System.Environment.GetEnvironmentVariable("HOF2VR_UI") ?? "";
                s_noOverlay = dbg.Contains("nooverlay");
                s_noRedirect = dbg.Contains("noredirect");
                Create();
                if (s_noOverlay) { Overlay.enabled = false; Panel.gameObject.SetActive(false); }
            }
            if (Time.unscaledTime >= m_nextScan || Camera.allCamerasCount != m_lastCamCount)
            {
                m_nextScan = Time.unscaledTime + 0.5f;
                m_lastCamCount = Camera.allCamerasCount;
                ScanCameras();
            }
            if (VRRig.I == null) return;
            HideLetterbox();
            float sc = VRRig.I.Scale;
            Vector3 limboOrigin = Vector3.zero;
            float limboYaw = 0f;
            bool inLimbo = Limbo.On && Limbo.TrackingFrame(out limboOrigin, out limboYaw);
            if (inLimbo)
            {
                // The waiting space: the panel stays where it appeared, in the space's own frame (the real room). The game
                // places the player at the next place behind the space (recenter, new station, first person): following
                // the rig, the panel jumped to wherever the head pointed then, and in the fight's HUD mode it followed the
                // head - the "You died" text and the loading spinner "started following my head" (user 2026-10-11).
                Quaternion fy = Quaternion.Euler(0f, limboYaw, 0f);
                Vector3 tp = limboOrigin + fy * new Vector3(0f, HeightOffset, Distance);
                Quaternion tr = fy * Quaternion.Euler(Mathf.Atan2(-HeightOffset, Distance) * Mathf.Rad2Deg, 0f, 0f);
                Panel.position = VRRig.I.ToWorld(tp);
                Panel.rotation = VRRig.I.ToWorld(tr);
                Panel.localScale = new Vector3(PanelWidth * sc, PanelWidth * sc * Height / Width, 1);
                m_hudInit = false;
                m_panelYawInit = false;
            }
            else if (CombatVR.Active && ShowcaseUp())
            {
                ShowcaseShowing = true;
                ShowcaseFrame = Time.frameCount;
                // The enemy (or equipment) description before a fight (the game's UIShowcase; user 2026-10-12: "it moves around
                // with my head. I'd rather it stay in one place so it is easier to read"): the panel stays where it appeared, in
                // the world - in front of where the player looked, at the HUD's distance and size, level - until it closes.
                if (!m_showcaseFixed)
                {
                    float y = VRRig.I.HeadWorldRot.eulerAngles.y;
                    Quaternion yw = Quaternion.Euler(0f, y, 0f);
                    m_showcasePos = VRRig.I.HeadWorldPos + yw * (new Vector3(0f, -HudDown, HudDistance) * sc);
                    m_showcaseRot = yw * Quaternion.Euler(Mathf.Atan2(HudDown, HudDistance) * Mathf.Rad2Deg, 0f, 0f);
                    m_showcaseFixed = true;
                    ShowcaseFixes++;
                    Plugin.L.LogInfo("VRUI: the fight's description stays in place " + HudDistance.ToString("0.00") + " m ahead (" + Diag.V(m_showcasePos) + ")");
                }
                Panel.position = m_showcasePos;
                Panel.rotation = m_showcaseRot;
                Panel.localScale = new Vector3(HudWidth * sc, HudWidth * sc * Height / Width, 1);
                m_hudInit = false;   // the HUD starts afresh where the player looks afterwards
                m_panelYawInit = false;
            }
            else if (CombatVR.Active)
            {
                m_showcaseFixed = false;
                // Combat HUD: the game's corner widgets (health, charges, enemy list) and its menus on a
                // level panel in front of the player. It turns with the player's stick turns and follows
                // the head only once it looks well away (HudDeadzone), so any corner can be looked at
                // (a visor glued to the head kept them at the edge of the view, cut off). While the
                // game is paused it stays put in front of where the player looked when pausing.
                Vector3 headEuler = VRRig.I.HeadWorldRot.eulerAngles;
                float rel = Mathf.DeltaAngle(VRRig.I.AnchorYaw, headEuler.y);
                bool paused = GameWatch.State == "GameState_Level_Pause" || Time.timeScale == 0f;
                if (!m_hudInit || (paused && !m_hudPaused))
                {
                    // a menu that just opened appears where the player looks
                    m_hudYaw = rel;
                    m_hudPitch = paused ? Mathf.Clamp(Mathf.DeltaAngle(0f, headEuler.x), -35f, 35f) : 0f;
                    m_hudInit = true;
                }
                if (!paused) m_hudPitch = 0f;
                m_hudPaused = paused;
                if (!paused)
                {
                    float off = Mathf.DeltaAngle(m_hudYaw, rel);
                    if (Mathf.Abs(off) > HudDeadzone)
                        m_hudYaw += (off - Mathf.Sign(off) * HudDeadzone) * (1f - Mathf.Exp(-6f * Time.unscaledDeltaTime));
                }
                Quaternion yaw = Quaternion.Euler(m_hudPitch, VRRig.I.AnchorYaw + m_hudYaw, 0f);
                Vector3 off3 = new Vector3(0f, -HudDown, HudDistance);
                Panel.position = VRRig.I.HeadWorldPos + yaw * (off3 * sc);
                Panel.rotation = yaw * Quaternion.Euler(Mathf.Atan2(HudDown, HudDistance) * Mathf.Rad2Deg, 0f, 0f);
                Panel.localScale = new Vector3(HudWidth * sc, HudWidth * sc * Height / Width, 1);
                m_panelYawInit = false;
            }
            else
            {
                m_hudInit = false;
                // Panel in front of the neutral eyes, facing them. In menus it floats further out. At the
                // caravan it is the flat game's screen (see TableDistance) and turns to whatever the game
                // is showing if that is well off to the side.
                bool table = Places.AtCaravan;
                Vector3 eye;
                float yawDeg = PanelTargetYaw();
                // in the dark (a new place) it is simply there; otherwise it turns at a readable pace
                if (!m_panelYawInit || VRFade.IsBlack) { m_panelYaw = yawDeg; m_panelYawInit = true; }
                m_panelYaw = Mathf.MoveTowardsAngle(m_panelYaw, yawDeg, 240f * Time.unscaledDeltaTime);
                Vector3 hp;
                Quaternion hr;
                float hh;
                if (table && HubMapPanel(out hp, out hr, out hh))
                {
                    // the challenge map chosen at the side table (Table.MapAtDeckSpot; user 2026-10-12 ~22:15/22:25: "put it
                    // towards the wall, about a foot and a half above the map" - "parallel with the map table"): the screen with
                    // the challenge's details stands beyond the map, upright, its lower edge MapPanelAbove over the map
                    Panel.position = hp;
                    Panel.rotation = hr;
                    Panel.localScale = new Vector3(hh * Width / Height, hh, 1);
                    InventoryScreen = false;
                }
                else if (table)
                {
                    Quaternion rot;
                    TableScreen(out eye, out rot);
                    float d = TableDistance * sc;
                    // The inventory's cards hang 0.4 m from the eyes, in front of this screen, and its 2D parts (trait lists,
                    // slot icons, badges) sit on and beside them: drawn on top of the cards but 0.35 m behind them, they
                    // fought the eyes' depth (user 2026-10-11: "Some things were on top of things but also behind or
                    // something. It was unmanageable"). While it is open the screen comes forward to just behind its
                    // nearest card (same angular size; the cards stay within reach of a fingertip).
                    // (2026-10-11 evening, "still bugged": the screen now lies IN the row of equipment cards - their median
                    // depth - so the slot icons, badges and trait lists drawn for a card sit at that card's own depth and stay
                    // on it when the head moves; the stat cards that hung in front of the row lie on the table, TableHud)
                    // (P2-4, the second playtest's mark 8: the containers' components are switched off while the inventory is open,
                    // so no card was counted and the screen stayed 0.29 m behind the cards - its slot icons and trait lists drawn over
                    // the cards but seen behind them. Now counted by their objects, and the screen 1 cm in front of the nearest card:
                    // the 2D parts are in front of the cards both as drawn and as seen)
                    float inv = InventoryDepth(eye, rot * Vector3.forward);
                    float want = inv > 0f ? Mathf.Clamp(inv - InventoryFront * sc, 0.3f * sc, d) : d;
                    m_tableDist = m_tableDist <= 0f || VRFade.IsBlack ? want : Mathf.MoveTowards(m_tableDist, want, 1.2f * sc * Time.unscaledDeltaTime);
                    d = m_tableDist;
                    InventoryScreen = inv > 0f;
                    float h = 2f * d * Mathf.Tan(TableView.FlatFov * 0.5f * Mathf.Deg2Rad);
                    Panel.position = eye + rot * new Vector3(0f, 0f, d);
                    Panel.rotation = rot;
                    Panel.localScale = new Vector3(h * Width / Height, h, 1);
                }
                else
                {
                    eye = VRRig.I.NeutralEye;
                    float dist = Distance * sc, width = PanelWidth * sc, up = HeightOffset * sc;
                    Quaternion yaw = Quaternion.Euler(0, m_panelYaw, 0);
                    Panel.position = eye + yaw * new Vector3(0, up, dist);
                    // tilted to face the eyes (it sits a little below eye level)
                    Panel.rotation = yaw * Quaternion.Euler(Mathf.Atan2(-up, dist) * Mathf.Rad2Deg, 0f, 0f);
                    Panel.localScale = new Vector3(width, width * Height / Width, 1);
                }
            }
            bool show = Visible && m_uiCams.Count > 0 && !s_noOverlay;
            if (show && inLimbo && !PanelInWaitingSpace()) { show = false; PanelHidden++; }
            if (Panel.gameObject.activeSelf != show) Panel.gameObject.SetActive(show);
        }

        float m_tableDist = -1f;
        /// The table screen is pulled forward to the open inventory's cards.
        public static bool InventoryScreen;

        static readonly List<float> s_depths = new List<float>();

        /// Real metres the table screen stands in front of the open inventory's nearest card.
        public static float InventoryFront = 0.01f;

        /// The open inventory's cards: the nearest one's depth along the screen's forward from the eyes (world units), else -1.
        static float InventoryDepth(Vector3 eye, Vector3 fwd)
        {
            try
            {
                UIInventory inv = UIInventory.Instance;
                if (inv == null || !inv.isActiveAndEnabled) return -1f;
                var st = inv.ActiveState;
                if (st == null || st is UIInventoryState_Closed) return -1f;
                s_depths.Clear();
                foreach (CardContainer c in inv.Containers)
                {
                    if (c == null || !c.gameObject.activeInHierarchy || c.Cards == null) continue;
                    foreach (Card card in c.Cards)
                    {
                        if (card == null || !card.gameObject.activeInHierarchy) continue;
                        float dep = Vector3.Dot(card.transform.position - eye, fwd);
                        if (dep > 0.05f) s_depths.Add(dep);
                    }
                }
                if (s_depths.Count == 0) return -1f;
                s_depths.Sort();
                return s_depths[0];
            }
            catch (System.Exception) { return -1f; }
        }

        static Camera[] s_scanCams = new Camera[16];

        void ScanCameras()
        {
            bool relayout = false;
            m_uiCams.Clear();
            if (s_scanCams.Length < Camera.allCamerasCount) s_scanCams = new Camera[Camera.allCamerasCount + 8];
            int count = Camera.GetAllCameras(s_scanCams);   // (no new array every half second)
            for (int ci = 0; ci < count; ci++)
            {
                Camera c = s_scanCams[ci];
                if (c == null) continue;
                if (c == Overlay) continue;
                // keep our layers (overlay, waiting space) out of every game camera
                if ((c.cullingMask & (1 << Layer)) != 0) c.cullingMask &= ~(1 << Layer);
                if ((c.cullingMask & (1 << Limbo.Layer)) != 0 && (Limbo.I == null || c != Limbo.I.Cam)) c.cullingMask &= ~(1 << Limbo.Layer);
                if (!c.orthographic || c.GetComponent("UICamera") == null || s_noRedirect) continue;
                if (c.targetTexture != null && c.targetTexture != RT) continue;   // game's own RT camera
                m_uiCams.Add(c);
                bool fresh = m_redirected.Add(c.GetInstanceID());
                if (fresh)
                    Plugin.L.LogInfo("VRUI: redirecting UI camera '" + c.name + "' (depth " + c.depth + ", mask 0x" + c.cullingMask.ToString("X") + ", was " + c.pixelWidth + "x" + c.pixelHeight +
                                     " aspect " + c.aspect.ToString("0.###") + ")");
                c.targetTexture = RT;
                c.stereoTargetEye = StereoTargetEyeMask.None;
                if (fresh || Mathf.Abs(c.aspect - (float)Width / Height) > 0.01f)
                {
                    // as a stereo camera it took the headset eye's aspect; the panel is 16:9
                    c.ResetAspect();
                    if (Mathf.Abs(c.aspect - (float)Width / Height) > 0.01f) c.aspect = (float)Width / Height;
                    relayout = true;
                }
            }
            // Clear the RT once per frame before the first UI camera draws (lowest depth clears).
            Camera first = null;
            foreach (Camera c in m_uiCams) if (first == null || c.depth < first.depth) first = c;
            foreach (Camera c in m_uiCams)
            {
                if (c == first) { c.clearFlags = CameraClearFlags.SolidColor; c.backgroundColor = new Color(0, 0, 0, 0); }
            }
            if (relayout) Relayout("new UI camera");
        }

        /// Overlay camera follows the primary game camera's VR pose (set earlier this frame).
        internal class OverlayMarker : MonoBehaviour
        {
            // diagnostics for eye captures: where the overlay (fade, panel) drew from in the last frame
            static int s_frame;
            static Vector3 s_pos, s_head;
            static Camera s_from;
            public static string LastInfo => "overlay frame " + s_frame + " from " + (s_from != null ? "'" + s_from.name + "'" : "head") + " at " + Diag.V(s_pos) + " head " + Diag.V(s_head);
            Camera m_cam;
            void Awake() { m_cam = GetComponent<Camera>(); }
            void OnPreCull()
            {
                if (VRRig.I == null) return;
                Camera p = VRRig.I.PrimaryCamera;
                // no game camera (between scenes): the rig's own head pose, so the fade and the panel
                // stay where they belong instead of freezing in front of the eyes
                transform.position = p != null ? VRRig.I.PrimaryPos : VRRig.I.HeadWorldPos;
                transform.rotation = p != null ? VRRig.I.PrimaryRot : VRRig.I.HeadWorldRot;
                s_frame = Time.frameCount;
                s_from = p;
                s_pos = transform.position;
                s_head = VRRig.I.HeadWorldPos;
                VRRig.SetEyes(m_cam, transform.position, transform.rotation);
                if (VRFade.I != null) VRFade.I.Place(transform);
                if (Comfort.I != null) Comfort.I.Place(transform);
                if (ThreatWarning.I != null) ThreatWarning.I.Place(transform);
                if (ScreenFlash.I != null) ScreenFlash.I.Place(transform);
            }
        }
    }
}

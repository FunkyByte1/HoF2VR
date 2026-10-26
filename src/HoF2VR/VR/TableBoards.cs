using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// At the card table the encounter's story text and its choices leave the flat screen (user feedback
    /// 2026-10-11: "the text and prompts appear on the right side, so I can't look at the table while I look
    /// at them"; "for the choices I'm still selecting them with the stick and A").
    ///  * Story: the encounter's paragraph (and the announcement under it) is shown above the Dealer, a little
    ///    to the right, facing the player - Settings Table.TextAboveDealer.
    ///  * Choices: every option button of the encounter is shown on its own plate on a small board in front of
    ///    the player, above the table edge, within reach: touch a plate with a fingertip (hover = the game's own
    ///    highlight + a tick, push ~1 cm in = select + a click), or point the laser at it and pull the trigger.
    ///    The stick and A keep working (the plates show the game's own buttons) - Settings Table.ChoiceBoard.
    /// Nothing in the game's UI moves: the plates and the story panel show live crops of the 2D UI texture, and
    /// the main panel leaves those rectangles out (VRUI.SetHoles), so nothing shows twice. Input goes through
    /// the game's own paths (NGUI hover under the laser; touch = TryHover + A, like a laser click).
    internal partial class TableBoards : MonoBehaviour
    {
        public static TableBoards I;

        // placement, real metres / degrees (tunable with `boards set ...`)
        // (2026-10-11 afternoon: "the text is a little too close to the dealer - a little smaller so there is a little more
        // space": 0.055 deg/px, 0.10 m above and 0.30 m right before)
        public static float StoryDegPerPx = 0.047f;    // angular size of one UI pixel on the story panel
        public static float StoryAbove = 0.15f;        // gap between the Dealer's head and the text's bottom edge
        public static float StoryRight = 0.34f;        // the text's centre this far right of the Dealer's head
        public static float StoryDepth = 0.88f;        // fraction of the Dealer's distance (in front of him)
        public static float BoardAheadOverride = -1f, BoardDown = 0.36f, BoardRight = 0.02f;   // ahead: Settings.ChoiceBoardDistance
        // (P2-7: the player now stands at the map's case, 0.27 m nearer; at 0.36 left / 0.40 ahead the column hung over the
        // map's left part - now beside it)
        // (the third playtest, mark 8: "Perhaps we could put this menu like where my left hand is so that it's a little more out
        // of the way" - the user: fixed there, not on the hand. Their left hand then: 0.23 m ahead of the eyes, 0.41 m left, 0.55 m
        // below; it was 0.28 / 0.55 / 0.30)
        public static float HubBoardAhead = 0.23f, HubBoardDown = 0.55f, HubBoardLeft = 0.41f, HubBoardScale = 0.5f;
        public static float BoardMmPerPx = 0.75f;      // plate size: millimetres per UI pixel
        public static float BoardMaxWidth = 0.46f;     // metres; longer options make the whole column smaller
        public static float PlateGap = 0.012f, PlateTravel = 0.006f;
        // touch: the shared plate press (PlateTouch, docs/hand-feel-design.md §3.4: plates travel 6 mm, click at 3.5 mm,
        // release at 1.5 mm, no hover dwell, sideways within 60 degrees of straight in, from the front within 1.5 s)
        readonly PlateTouch m_touch = new PlateTouch("boards", PressFamily.Plate);

        static readonly AccessTools.FieldRef<EncounterPanel, UILabel> s_pageText = AccessTools.FieldRefAccess<EncounterPanel, UILabel>("m_pageText");
        static readonly AccessTools.FieldRef<EncounterPanel, UILabel> s_announce = AccessTools.FieldRefAccess<EncounterPanel, UILabel>("m_announcementText");
        static readonly AccessTools.FieldRef<EncounterPanel, UITable> s_layoutTable = AccessTools.FieldRefAccess<EncounterPanel, UITable>("m_layoutTable");
        static readonly AccessTools.FieldRef<EncounterPanel, UITable> s_optionTable = AccessTools.FieldRefAccess<EncounterPanel, UITable>("m_pageOptionTable");
        static readonly AccessTools.FieldRef<EncounterPanel, Transform> s_chosen = AccessTools.FieldRefAccess<EncounterPanel, Transform>("m_selectedChoiceTransform");
        static readonly AccessTools.FieldRef<EncounterPanel, List<PageOptionButton>> s_buttons = AccessTools.FieldRefAccess<EncounterPanel, List<PageOptionButton>>("m_pageOptionButtons");
        static readonly AccessTools.FieldRef<PageOptionButton, UILabel> s_btnText = AccessTools.FieldRefAccess<PageOptionButton, UILabel>("m_text");
        static readonly AccessTools.FieldRef<PageOptionButton, UILabel> s_btnLetter = AccessTools.FieldRefAccess<PageOptionButton, UILabel>("m_letterText");

        EncounterPanel m_panel;
        float m_nextFind;

        // ---- the title menu on the board (docs/hand-feel-design.md §3.12, Phase 1b; Hands.TitleMenuBoard) -----------------
        // At the title the menu's buttons are cropped from the 2D UI onto plates within reach, like the encounter's choices,
        // and cut out of the far panel; the logo stays there. Touch = the game's hover + A, as the laser's click.
        public static float TitleAhead = 0.46f, TitleDown = 0.22f, TitleRight = 0.06f, TitleGap = 0.008f, TitleMargin = 0.005f;
        static readonly AccessTools.FieldRef<StartScreenUI, UIGrid> s_titleGrid = AccessTools.FieldRefAccess<StartScreenUI, UIGrid>("m_buttonGrid");
        StartScreenUI m_title;
        float m_nextTitleFind;
        bool m_titleMode;
        static readonly List<UISelectable> s_titleSels = new List<UISelectable>();

        static bool TitleAllowed => XRSettings.enabled && VRRig.I != null && VRUI.I != null && VRUI.I.RT != null && Settings.TitleMenuBoard &&
                                    Places.Current == Places.Place.Title && GameWatch.State == "GameState_StartScreen" && !VRFade.IsBlack &&
                                    (HandCheck.I == null || !HandCheck.I.Active);

        StartScreenUI TitleUI()
        {
            if (m_title != null) return m_title.isActiveAndEnabled ? m_title : null;
            if (Time.unscaledTime < m_nextTitleFind) return null;
            m_nextTitleFind = Time.unscaledTime + 1f;
            m_title = FindObjectOfType<StartScreenUI>();
            return m_title != null && m_title.isActiveAndEnabled ? m_title : null;
        }

        /// Nothing over the title menu (a dialogue, the pause menu): those keep the pad.
        static bool TitleOnTop()
        {
            MenuManager mm = MenuManager.Instance;
            if (mm == null) return true;
            try
            {
                if (mm.Paused) return false;
                foreach (MenuManager.StackPriority pr in s_over)
                    if (mm.CurrentMenu(pr) != null) return false;
            }
            catch (Exception) { }
            return true;
        }

        /// The title's buttons that show, top to bottom, with their crops.
        int TitlePlates(StartScreenUI ui)
        {
            UIGrid grid = s_titleGrid(ui);
            if (grid == null) return 0;
            s_titleSels.Clear();
            grid.GetComponentsInChildren(false, s_titleSels);
            int n = 0;
            var rects = new List<KeyValuePair<UISelectable, Rect>>();
            foreach (UISelectable sel in s_titleSels)
            {
                if (sel == null || !sel.isActiveAndEnabled || !sel.IsValid) continue;
                Rect r = default(Rect);
                bool has = false;
                s_widgets.Clear();
                sel.GetComponentsInChildren(false, s_widgets);
                foreach (UIWidget w in s_widgets) if (Shown(w)) Grow(ref r, ref has, w);
                if (!has) continue;
                // room for the hover decorations beside the label, and big enough to touch
                float pw = Mathf.Max(r.width + 120f, MinPlatePx.x), ph = Mathf.Max(r.height + 8f, MinPlatePx.y);
                r = Pad(new Rect(r.center.x - pw * 0.5f, r.center.y - ph * 0.5f, pw, ph), 4f, 2f);
                rects.Add(new KeyValuePair<UISelectable, Rect>(sel, r));
            }
            rects.Sort((a, b) => b.Value.center.y.CompareTo(a.Value.center.y));
            foreach (var kv in rects)
            {
                Quad q = PlateAt(m_plateCount + n);
                q.Btn = null;
                q.Nav = null;
                q.Sel = kv.Key;
                q.Px = kv.Value;
                q.Disabled = !kv.Key.IsSelectable;
                n++;
            }
            return n;
        }

        sealed class Quad : PressPlate
        {
            public Transform Back, Frame;
            public Mesh M;
            public MeshRenderer R;
            public Rect Px;            // the crop on the UI texture (pixels, origin bottom-left)
            public PageOptionButton Btn;
            public UISelectable Sel;   // a title menu button (Btn is null then)
            public NavBarButton Nav;   // a button of the game's controls bar (Table.ControlButtons)
            public UISelectable Target => Btn != null ? Btn.Selectable : Sel;
            public Rect UvPx = new Rect(-1f, -1f, 0f, 0f);
            public float FitMargin = -1f;
            public UIProgressBar Bar;   // a menu slider or scroll bar: dragged by a finger in contact (TableBoards.Menu)
            public Rect Track, Thumb;   // its track and thumb on the UI texture (pixels)
            public readonly Vector2[] Uv = new Vector2[4];
            public Color FrameColour;
            public EnumControl Enum;    // a settings row with ◁ value ▷ (P4-1: its arrows are on its plate)
        }

        Quad m_story;
        readonly List<Quad> m_plates = new List<Quad>();
        // the game's controls bar (Table.ControlsOnTable): its left and right groups as two strips lying on the table's near
        // edge; the bar itself (its black backdrop) is cut out of the main panel
        Quad m_navL, m_navR;
        public static float NavPastEdge = 0.08f, NavSide = 0.17f, NavMmPerPx = 0.52f;
        static readonly string[] s_navBars = { "UIManager/UI Root (2D)/Camera/MenuManager/Shared/Bottom/MainNavBar",
                                               "UIManager/UI Root (2D)/Camera/MenuManager/Shared/Bottom/InventoryNavBar",
                                               "UIManager/UI Root (2D)/Camera/MenuManager/Shared/AltCardBrowserNavBar" };
        readonly Transform[] m_navRoots = new Transform[3];
        int m_navScene = -1;
        int m_plateCount;
        Material m_crop, m_back;
        Transform m_root;
        readonly List<Rect> m_holes = new List<Rect>();

        void Awake()
        {
            I = this;
            Diag.StatusExtras["boards"] = () => Diag.Q(Describe());
            Diag.StatusExtras["deathText"] = () => Diag.Q(DeathText);
            Diag.Commands["boards"] = Command;
            Diag.Commands["ptouch"] = PTouch;
            Diag.Commands["markers"] = a =>
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (a.Length > 1 && (a[1] == "0" || a[1] == "1")) MarkersAtPins = a[1] == "1";
                if (a.Length > 2 && a[1] == "mm") MarkerMmPerPx = float.Parse(a[2], inv);
                if (a.Length > 2 && a[1] == "above") MarkerAbove = float.Parse(a[2], inv);
                if (a.Length > 2 && a[1] == "tabs") TabsGrow = float.Parse(a[2], inv);
                if (a.Length > 2 && a[1] == "near") TabsNear = float.Parse(a[2], inv);
                if (a.Length > 2 && a[1] == "maxw") TabsMaxWidth = float.Parse(a[2], inv);
                return "markers at the pins " + MarkersAtPins + ", " + MarkerMmPerPx + " mm/px, " + MarkerAbove + " m above: " + MarkersShown + " shown | " + MarkersWhere + " | tabs " + TabsWhere;
            };
            Diag.Commands["platepose"] = PlatePose;
            Diag.Commands["navgeo"] = a => NavGeo();
            Diag.Commands["navcards"] = a => NavCards();
            Diag.Commands["navobstacle"] = a =>
            {
                if (a.Length > 1 && a[1] == "clear") { s_testObstacles.Clear(); return "test obstacles cleared"; }
                if (a.Length > 6 && a[1] == "add")
                {
                    var inv = CultureInfo.InvariantCulture;
                    s_testObstacles.Add(new Vector4(float.Parse(a[2], inv), float.Parse(a[3], inv), float.Parse(a[4], inv), float.Parse(a[5], inv)));
                    s_testObstacleH = float.Parse(a[6], inv);
                    return "test obstacle added (" + s_testObstacles.Count + "); column: " + NavClear;
                }
                return s_testObstacles.Count + " test obstacles; column: " + NavClear;
            };
            m_touch.OnHover = HoverPlate;
            m_touch.OnPress = PressPlateAt;
            m_touch.Label = i => i < m_plates.Count ? Label(m_plates[i]) : "-";
            HandWorld.Targets["plate1"] = () => PlateFace(0, 0f);
            HandWorld.Targets["plate2"] = () => PlateFace(1, 0f);
        }

        static bool Allowed => XRSettings.enabled && VRRig.I != null && VRUI.I != null && VRUI.I.RT != null && Places.Current == Places.Place.Caravan && !CombatVR.Active;

        // ---- finding the encounter panel's pieces ------------------------------------------------------
        static readonly SceneScan.AllOf<EncounterPanel> s_panels = new SceneScan.AllOf<EncounterPanel>();

        /// The encounter is the menu in front: nothing of a higher priority (a dialogue, the inventory, the pause
        /// menu, a failure screen) covers it - those use the screen's middle, and a crop would cut them apart.
        static readonly MenuManager.StackPriority[] s_over = { MenuManager.StackPriority.Secondary, MenuManager.StackPriority.Dialogue, MenuManager.StackPriority.Failure };

        static bool OnTop(EncounterPanel p)
        {
            MenuManager mm = MenuManager.Instance;
            if (mm == null) return true;
            try
            {
                if (mm.Paused) return false;
                foreach (MenuManager.StackPriority pr in s_over)
                    if (mm.CurrentMenu(pr) != null) return false;
                Menu cur = mm.CurrentMenu(MenuManager.StackPriority.Primary);
                return cur == null || p.transform.IsChildOf(cur.transform);
            }
            catch (Exception) { return true; }
        }

        /// The game's encounter panel when it is showing (found once per scene load, inactive ones included).
        EncounterPanel Panel()
        {
            if (m_panel != null && m_panel.isActiveAndEnabled) return m_panel;
            if (m_panel != null) return null;
            if (Time.unscaledTime < m_nextFind) return null;
            m_nextFind = Time.unscaledTime + 5f;
            foreach (EncounterPanel p in s_panels.Get()) if (p != null) { m_panel = p; break; }
            return m_panel != null && m_panel.isActiveAndEnabled ? m_panel : null;
        }

        static readonly List<UIWidget> s_widgets = new List<UIWidget>();

        static bool Shown(UIWidget w)
        {
            if (w == null || !w.enabled || !w.gameObject.activeInHierarchy || w.finalAlpha < 0.02f || w.width < 2 || w.height < 2) return false;
            if (w.GetType() == typeof(UIWidget)) return false;   // a plain container/anchor point draws nothing
            UILabel l = w as UILabel;
            return l == null || !string.IsNullOrEmpty(l.text);
        }

        static void Grow(ref Rect r, ref bool has, UIWidget w)
        {
            Rect px;
            if (!Feedback.RtRect(w, out px)) return;
            if (!has) { r = px; has = true; }
            else r = Rect.MinMaxRect(Mathf.Min(r.xMin, px.xMin), Mathf.Min(r.yMin, px.yMin), Mathf.Max(r.xMax, px.xMax), Mathf.Max(r.yMax, px.yMax));
        }

        /// As Grow, but a label counts only as wide as its printed text (its widget can be wider: the third playtest, mark 5 - a
        /// controls button's label box reached over the next button's glyph, and that plate was cut short to the left, through its
        /// "X").
        static void GrowDrawn(ref Rect r, ref bool has, UIWidget w)
        {
            UILabel l = w as UILabel;
            Rect px;
            if (l == null || l.width <= 0 || !Feedback.RtRect(w, out px)) { Grow(ref r, ref has, w); return; }
            float f;
            try { f = Mathf.Clamp01(l.printedSize.x / l.width); } catch (Exception) { f = 1f; }
            if (f > 0.98f) { Grow(ref r, ref has, w); return; }
            NGUIText.Alignment al = l.alignment;
            if (al == NGUIText.Alignment.Automatic)
            {
                UIWidget.Pivot pv = l.pivot;
                al = pv == UIWidget.Pivot.Left || pv == UIWidget.Pivot.TopLeft || pv == UIWidget.Pivot.BottomLeft ? NGUIText.Alignment.Left :
                     pv == UIWidget.Pivot.Right || pv == UIWidget.Pivot.TopRight || pv == UIWidget.Pivot.BottomRight ? NGUIText.Alignment.Right : NGUIText.Alignment.Center;
            }
            float wpx = Mathf.Min(px.width, px.width * f + 6f);   // (the glyphs' outline and shadow reach past the printed size)
            if (al == NGUIText.Alignment.Left || al == NGUIText.Alignment.Justified) px.xMax = px.xMin + wpx;
            else if (al == NGUIText.Alignment.Right) px.xMin = px.xMax - wpx;
            else { float c = px.center.x; px.xMin = c - wpx * 0.5f; px.xMax = c + wpx * 0.5f; }
            if (!has) { r = px; has = true; }
            else r = Rect.MinMaxRect(Mathf.Min(r.xMin, px.xMin), Mathf.Min(r.yMin, px.yMin), Mathf.Max(r.xMax, px.xMax), Mathf.Max(r.yMax, px.yMax));
        }

        static Rect Pad(Rect r, float x, float y)
        {
            r = Rect.MinMaxRect(r.xMin - x, r.yMin - y, r.xMax + x, r.yMax + y);
            return Rect.MinMaxRect(Mathf.Max(0f, r.xMin), Mathf.Max(0f, r.yMin), Mathf.Min(VRUI.Width, r.xMax), Mathf.Min(VRUI.Height, r.yMax));
        }

        /// The story text: every visible widget of the panel's layout outside the option table.
        bool StoryRect(EncounterPanel p, out Rect r)
        {
            r = default(Rect);
            bool has = false;
            UITable layout = s_layoutTable(p), options = s_optionTable(p);
            Transform root = layout != null ? layout.transform : p.transform;
            s_widgets.Clear();
            root.GetComponentsInChildren(false, s_widgets);
            foreach (UIWidget w in s_widgets)
            {
                if (!Shown(w)) continue;
                if (options != null && w.transform.IsChildOf(options.transform)) continue;
                Grow(ref r, ref has, w);
            }
            // the option chosen last stays above the text as its header ("1) Accept.")
            Transform chosen = s_chosen(p);
            if (has && chosen != null && !chosen.IsChildOf(root))
            {
                s_widgets.Clear();
                chosen.GetComponentsInChildren(false, s_widgets);
                foreach (UIWidget w in s_widgets) if (Shown(w)) Grow(ref r, ref has, w);
            }
            if (has) r = Pad(r, 18f, 12f);
            return has && r.width > 8f && r.height > 8f;
        }

        public static float ArrowLeftPx = 96f, ArrowRightPx = 150f;
        static readonly AccessTools.FieldRef<PageOptionButton, UISprite> s_leftArrow = AccessTools.FieldRefAccess<PageOptionButton, UISprite>("m_leftArrow");
        static readonly AccessTools.FieldRef<PageOptionButton, UISprite> s_rightArrow = AccessTools.FieldRefAccess<PageOptionButton, UISprite>("m_rightArrow");

        /// One option button: its letter, text and background, and room for the hover arrows (which appear beside it). The arrows
        /// themselves never count (the third playtest, mark 12, a shop: "Depending on which of these I'm hovering at, it changes the
        /// width of these" - a hovered option's arrows reached past the room kept for them and widened the whole column).
        static bool OptionRect(PageOptionButton b, out Rect r)
        {
            r = default(Rect);
            bool has = false;
            s_widgets.Clear();
            b.GetComponentsInChildren(false, s_widgets);
            UISprite la = s_leftArrow(b), ra = s_rightArrow(b);
            foreach (UIWidget w in s_widgets)
            {
                if (!Shown(w)) continue;
                if ((la != null && (w == la || w.transform.IsChildOf(la.transform))) || (ra != null && (w == ra || w.transform.IsChildOf(ra.transform)))) continue;
                GrowDrawn(ref r, ref has, w);
            }
            if (!has) return false;
            // room for the arrows the game shows beside a hovered option (left ~90 px, right ~110 px)
            Rect t;
            UILabel letter = s_btnLetter(b), text = s_btnText(b);
            float left = r.xMin, right = r.xMax;
            // (how far the arrows reach past the letter and the words: measured whenever they show, never less than ArrowLeftPx /
            // ArrowRightPx - mark 12's hovered option showed its right arrow cut off at a steady width of +112 px)
            Rect ar;
            if (la != null && Shown(la) && letter != null && Shown(letter) && Feedback.RtRect(la, out ar) && Feedback.RtRect(letter, out t)) ArrowLeftPx = Mathf.Max(ArrowLeftPx, t.xMin - ar.xMin + 4f);
            if (ra != null && Shown(ra) && text != null && Shown(text) && Feedback.RtRect(ra, out ar) && Feedback.RtRect(text, out t)) ArrowRightPx = Mathf.Max(ArrowRightPx, ar.xMax - t.xMax + 4f);
            if (letter != null && Shown(letter) && Feedback.RtRect(letter, out t)) left = Mathf.Min(left, t.xMin - ArrowLeftPx);
            if (text != null && Shown(text) && Feedback.RtRect(text, out t)) right = Mathf.Max(right, t.xMax + ArrowRightPx);
            if (letter == null && text == null) { left -= 20f; right += 20f; }
            r = Rect.MinMaxRect(left, r.yMin, right, r.yMax);
            // big enough to touch (a lone "continue" arrow is 13 px wide)
            float pw = Mathf.Max(r.width, MinPlatePx.x), ph = Mathf.Max(r.height, MinPlatePx.y);
            r = new Rect(r.center.x - pw * 0.5f, r.center.y - ph * 0.5f, pw, ph);
            r = Pad(r, 6f, 6f);
            return r.width > 8f && r.height > 8f;
        }

        public static Vector2 MinPlatePx = new Vector2(220f, 44f);
        public static int ArrowsSkipped;
        static int s_arrowsNow;   // this frame's (the option the column's Continue stands for)
        public static bool ArrowLeftToColumn => s_arrowsNow > 0;

        /// An option that is only the game's "continue" arrow: no letter and no words shown.
        static bool ArrowOnly(PageOptionButton b)
        {
            UILabel letter = s_btnLetter(b), text = s_btnText(b);
            bool hasLetter = letter != null && Shown(letter) && !string.IsNullOrEmpty(letter.text);
            bool hasText = text != null && Shown(text) && !string.IsNullOrEmpty(text.text);
            return !hasLetter && !hasText;
        }

        /// The controls column shows a button that presses A now (its own Continue / Select).
        bool NavOffersA()
        {
            if (!Settings.ControlButtons || !NavPlace) return false;
            if (m_navScene != SceneScan.Version) return NavHasA;   // (the roots are found again in NavPlatesInner this frame)
            foreach (Transform root in m_navRoots)
            {
                if (root == null || !root.gameObject.activeInHierarchy) continue;
                s_navBtns.Clear();
                root.GetComponentsInChildren(false, s_navBtns);
                foreach (NavBarButton nb in s_navBtns) if (nb != null && nb.isActiveAndEnabled && nb.Action != null && PadButton(nb) == "A") return true;
            }
            return false;
        }

        // ---- per frame ---------------------------------------------------------------------------------
        // what each plate showed last frame (its button / control / bar button): the touch follows a plate that is numbered again
        readonly List<object> m_prevIds = new List<object>();
        static object IdOf(Quad q) => q.Btn != null ? (object)q.Btn : q.Nav != null ? (object)q.Nav : q.Sel;

        void LateUpdate()
        {
            m_holes.Clear();
            bool story = false;
            m_prevIds.Clear();
            for (int i = 0; i < m_plateCount; i++) m_prevIds.Add(IdOf(m_plates[i]));
            m_plateCount = 0;
            for (int i = 0; i < m_plates.Count; i++) m_plates[i].Enum = null;   // (set again by MenuPlates for this frame's rows)
            s_arrowsNow = 0;
            EncounterPanel p = Allowed && (Settings.TextAboveDealer || Settings.ChoiceBoard) ? Panel() : null;
            if (p != null && !OnTop(p)) p = null;   // a dialogue, the inventory or the pause menu over it: as before
            Rect sr = default(Rect);
            if (p != null)
            {
                if (Settings.TextAboveDealer && StoryRect(p, out sr)) story = true;
                if (Settings.ChoiceBoard)
                {
                    List<PageOptionButton> list = s_buttons(p);
                    if (list != null)
                        foreach (PageOptionButton b in list)
                        {
                            Rect r;
                            if (b == null || !b.isActiveAndEnabled || !OptionRect(b, out r)) continue;
                            // a lone "next page" arrow (no letter, no words) while the controls column has its Continue (A): no
                            // plate - it showed as an empty black box with a small arrow (playtest 2026-10-17, marks 2, 4, 5)
                            // (also while the column has no Continue yet - the first seconds of a story page, candidate 8's
                            // walk_02_story_*: the game's own Continue comes to the column a moment later)
                            if (ArrowOnly(b) && Settings.ControlButtons && NavPlace) { ArrowsSkipped++; s_arrowsNow++; continue; }
                            Quad q = PlateAt(m_plateCount++);
                            q.Btn = b;
                            q.Nav = null;
                            q.Px = r;
                            q.Disabled = b.State == PageOptionButton.StateType.Disabled;
                        }
                }
            }
            // the death / end-of-run text back at the card table (the game keeps it up ~2 s into GameState_Post): above the
            // Dealer like an encounter's story, cut out of the big screen together with the dark blotch behind it (it was
            // drawn over his face: the virtual playtester's PT_after_death)
            bool endRun = false;
            Rect endBack = default(Rect);
            UIWidget endBackW = null;
            if (!story && Allowed && Settings.TextAboveDealer && VRUI.EndRunShowing() && EndRunRect(out sr, out endBack, out endBackW)) { story = true; endRun = true; }
            EndRunBacking(endBackW, endRun);
            m_titleMode = false;
            if (p == null && m_plateCount == 0 && TitleAllowed && TitleOnTop())
            {
                StartScreenUI ui = TitleUI();
                if (ui != null) { m_plateCount += TitlePlates(ui); m_titleMode = m_plateCount > 0; }
            }
            // menus by touch: a dialogue's buttons at the table on this board; the pause / settings menus and dialogues elsewhere
            // on the near menu board, their controls as plates in place (TableBoards.Menu.cs)
            if (p == null && m_plateCount == 0 && !m_titleMode) m_plateCount += MenuPlates();
            else { m_menuMode = m_dialogueMode = false; MenuKind = ""; }
            // a screen that only goes on with a button (the attract screen's Start, the end of a challenge...): its buttons
            if (p == null && m_plateCount == 0 && !m_titleMode && !m_menuMode) m_plateCount += ScreenPlates();
            if (m_plateCount > 1 && !m_menuMode)
            {
                // one column of equal plates: common left and right edges (the game's list is left-aligned)
                float l = float.MaxValue, r = float.MinValue;
                for (int i = 0; i < m_plateCount; i++) { l = Mathf.Min(l, m_plates[i].Px.xMin); r = Mathf.Max(r, m_plates[i].Px.xMax); }
                for (int i = 0; i < m_plateCount; i++) { Rect q = m_plates[i].Px; m_plates[i].Px = Rect.MinMaxRect(l, q.yMin, r, q.yMax); }
            }
            // (a lone "next page" arrow left to the column's Continue still counts as the encounter's option: the Scenarios'
            // @confirm waits for one, then presses A - candidate 8's tour: the fight scenarios stopped at the map without it)
            OptionPlates = s_arrowsNow;
            for (int i = 0; i < m_plateCount; i++) if (m_plates[i].Btn != null) OptionPlates++;
            // the game's controls bar as buttons (Table.ControlButtons): after the board's plates, in their own column
            m_navStart = m_plateCount;
            if (!m_menuMode && !m_titleMode) m_plateCount += NavPlates();
            NoTouchCheck();
            if (story || m_plateCount > 0) EnsureVisuals();
            if (m_story != null)
            {
                UpdateDealerAnchor(story);
                if (story) { m_story.Px = sr; m_holes.Add(sr); if (endRun && endBack.width > 0f) m_holes.Add(endBack); PlaceStory(); }
                Show(m_story, story);
            }
            CountEndRun(endRun && m_story != null && m_story.On);
            for (int i = 0; i < m_plates.Count; i++)
            {
                bool on = i < m_plateCount;
                if (on) m_holes.Add(m_plates[i].Px);
                else { m_plates[i].Btn = null; m_plates[i].Sel = null; m_plates[i].Nav = null; m_plates[i].Hovered = false; }
                Show(m_plates[i], on);
            }
            PlateTouch.Settle(PlateOf, m_plateCount, Mathf.Clamp(Time.unscaledDeltaTime, 1e-3f, 0.1f));
            ShowMenuBoard(m_menuMode);
            if (m_menuMode) PlaceMenu();
            else if (m_navStart > 0) PlaceBoard();
            if (m_plateCount > m_navStart) PlaceNavPlates();
            NavStrips();
            MapMarkers();
            if (VRUI.I != null) VRUI.I.SetHoles(m_holes);
            // (a plate numbered again: the fingers on it keep it)
            bool renumbered = m_prevIds.Count != m_plateCount;
            for (int i = 0; i < m_prevIds.Count && !renumbered; i++) if (!ReferenceEquals(m_prevIds[i], IdOf(m_plates[i]))) renumbered = true;
            if (renumbered && m_prevIds.Count > 0)
                m_touch.Remap(i =>
                {
                    if (i >= m_prevIds.Count || m_prevIds[i] == null) return -1;
                    for (int j = 0; j < m_plateCount; j++) if (ReferenceEquals(IdOf(m_plates[j]), m_prevIds[i])) return j;
                    return -1;
                });
            StepTouch(VRInput.R);
            StepTouch(VRInput.L);
            if (m_menuMode) { DragBars(VRInput.R); DragBars(VRInput.L); }
            UpdatePlateLook();
        }

        Quad PlateAt(int i)
        {
            while (m_plates.Count <= i) m_plates.Add(new Quad());
            return m_plates[i];
        }

        void EnsureMaterials()
        {
            if (m_root == null)
            {
                var go = new GameObject("HoF2VR_TableBoards");
                DontDestroyOnLoad(go);
                m_root = go.transform;
            }
            if (m_crop == null)
            {
                Material panel = VRUI.I.Panel.GetComponent<MeshRenderer>().sharedMaterial;
                // after the main panel (5100): the plates sit in front of it
                m_crop = new Material(panel) { name = "HoF2VR_Crop", renderQueue = 5103 };
                m_back = new Material(Shader.Find("Sprites/Default")) { name = "HoF2VR_Backing", renderQueue = 5102 };
            }
        }

        void EnsureVisuals()
        {
            EnsureMaterials();
            if (m_story == null) m_story = Make("HoF2VR_Story", 0.62f, false);
            for (int i = 0; i < m_plateCount; i++) if (m_plates[i].T == null) Build(m_plates[i], "HoF2VR_Plate" + (i + 1), 0.82f, true);
        }

        Quad Make(string name, float backAlpha, bool frame)
        {
            var q = new Quad();
            Build(q, name, backAlpha, frame);
            return q;
        }

        void Build(Quad q, string name, float backAlpha, bool frame)
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
            // a dark backing (the text reads against the room or the table) and, for plates, a gold frame
            q.Back = Slab(name + "_Back", q.T, new Color(0.02f, 0.015f, 0.01f, backAlpha), 0f);
            if (frame) q.Frame = Slab(name + "_Frame", q.T, new Color(0.62f, 0.48f, 0.22f, 0.9f), 0.06f);
        }

        /// A rounded-off flat rectangle (backing) or a ring (frame, `ring` = border width relative to the
        /// height) in vertex colours, drawn just before the crops.
        Transform Slab(string name, Transform parent, Color c, float ring)
        {
            var go = new GameObject(name) { layer = VRUI.Layer };
            go.transform.SetParent(parent, false);
            var m = new Mesh { name = name };
            Color lin = new Color(Mathf.GammaToLinearSpace(c.r), Mathf.GammaToLinearSpace(c.g), Mathf.GammaToLinearSpace(c.b), c.a);
            if (ring <= 0f)
            {
                m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
                m.triangles = new[] { 0, 1, 2, 1, 0, 3 };
                m.colors = new[] { lin, lin, lin, lin };
            }
            else
            {
                // outer and inner rectangles (the inner one is set per frame in SetFrame)
                m.vertices = new Vector3[8];
                m.colors = new[] { lin, lin, lin, lin, lin, lin, lin, lin };
                m.triangles = new[] { 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7 };
            }
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = m_back;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        static void Show(Quad q, bool on)
        {
            if (q == null || q.T == null) { if (q != null) q.On = false; return; }
            if (q.T.gameObject.activeSelf != on) q.T.gameObject.SetActive(on);
            q.On = on;
        }

        /// UVs of the crop; the quad is scaled to `size` (world) with a margin for the backing.
        static void Fit(Quad q, Vector2 size, float margin)
        {
            Rect r = q.Px;
            if (r != q.UvPx)
            {
                q.UvPx = r;
                float u0 = r.xMin / VRUI.Width, u1 = r.xMax / VRUI.Width, v0 = r.yMin / VRUI.Height, v1 = r.yMax / VRUI.Height;
                q.Uv[0] = new Vector2(u0, v0); q.Uv[1] = new Vector2(u1, v1); q.Uv[2] = new Vector2(u1, v0); q.Uv[3] = new Vector2(u0, v1);
                q.M.uv = q.Uv;
            }
            if (size == q.Size && margin == q.FitMargin) return;
            q.FitMargin = margin;
            q.Size = size;
            q.T.localScale = new Vector3(size.x, size.y, 1f);
            // backing and frame are children of the scaled quad: their local size is relative to it
            if (q.Back != null)
            {
                q.Back.localScale = new Vector3(1f + 2f * margin / size.x, 1f + 2f * margin / size.y, 1f);
                q.Back.localPosition = new Vector3(0f, 0f, 0.0015f / Mathf.Max(1e-4f, q.T.lossyScale.z));
            }
            if (q.Frame != null) SetFrame(q, margin);
        }

        static void SetFrame(Quad q, float margin)
        {
            Mesh m = q.Frame.GetComponent<MeshFilter>().sharedMesh;
            float ox = 0.5f + margin / q.Size.x, oy = 0.5f + margin / q.Size.y;
            float bw = Mathf.Max(0.003f * (VRRig.I != null ? VRRig.I.Scale : 1f), margin * 0.35f);
            float ix = ox - bw / q.Size.x, iy = oy - bw / q.Size.y;
            m.vertices = new[]
            {
                new Vector3(-ox, -oy, 0f), new Vector3(ox, -oy, 0f), new Vector3(ox, oy, 0f), new Vector3(-ox, oy, 0f),
                new Vector3(-ix, -iy, 0f), new Vector3(ix, -iy, 0f), new Vector3(ix, iy, 0f), new Vector3(-ix, iy, 0f),
            };
            m.RecalculateBounds();
            q.Frame.localScale = Vector3.one;
            q.Frame.localPosition = new Vector3(0f, 0f, -0.0005f / Mathf.Max(1e-4f, q.T.lossyScale.z));
        }

        // ---- the death text at the table ------------------------------------------------------------------
        /// Where the death / end-of-run text is drawn at the card table now: "dealer" (above him), "panel" (the big screen,
        /// in front of him), "" (not showing there). The test seam's status `deathText`; a summary line in the log when it goes.
        public static string DeathText = "";
        int m_endDealer, m_endPanel;
        float m_endGap = float.NaN;

        void CountEndRun(bool aboveDealer)
        {
            bool atTable = XRSettings.enabled && Places.Current == Places.Place.Caravan && !CombatVR.Active && VRUI.EndRunShowing() && !VRFade.IsBlack &&
                           VRUI.I != null && VRUI.I.Panel != null && VRUI.I.Panel.gameObject.activeSelf;
            if (atTable)
            {
                if (aboveDealer) { m_endDealer++; m_endGap = StoryGapDeg(); DeathText = "dealer"; }
                else { m_endPanel++; DeathText = "panel"; }
                return;
            }
            DeathText = "";
            if (m_endDealer + m_endPanel == 0) return;
            Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "boards: the death text at the table: {0} frames above the Dealer (its bottom edge {1:0.0} deg above his head, from the {3}), {2} frames on the big screen",
                                           m_endDealer, m_endGap, m_endPanel, HeadFrom));
            m_endDealer = m_endPanel = 0;
            m_endGap = float.NaN;
        }

        /// How far the story quad's bottom edge is above the top of the Dealer's head, seen from the eyes (degrees; negative =
        /// the text covers his head).
        float StoryGapDeg()
        {
            if (m_story == null || m_story.T == null || VRRig.I == null) return float.NaN;
            Vector3 eye = VRRig.I.NeutralEye;
            Vector3 bottom = m_story.T.position - m_story.T.up * (m_story.Size.y * 0.5f);
            Vector3 headTop = LiveDealerHead() + Vector3.up * (0.16f * VRRig.I.Scale);   // where he is now, not the anchor
            float a = Mathf.Atan2(bottom.y - eye.y, Horizontal(bottom - eye)) * Mathf.Rad2Deg;
            float b = Mathf.Atan2(headTop.y - eye.y, Horizontal(headTop - eye)) * Mathf.Rad2Deg;
            return a - b;
        }

        static float Horizontal(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        UIWidget m_endBacking;

        /// While the death text stands above the Dealer it has no dark backing (the user: "without a dim box"): the game's
        /// blotch behind it and the story panel's own backing are off; both come back when it goes.
        void EndRunBacking(UIWidget w, bool on)
        {
            if (on && w != null && w.enabled) { w.enabled = false; m_endBacking = w; }
            if (!on && m_endBacking != null) { m_endBacking.enabled = true; m_endBacking = null; }
            if (m_story != null && m_story.Back != null && m_story.Back.gameObject.activeSelf == on) m_story.Back.gameObject.SetActive(!on);
        }

        /// The death / end-of-run text (title and subtitle as printed) on the UI texture, and the dark blotch behind it.
        static bool EndRunRect(out Rect r, out Rect backing, out UIWidget back)
        {
            r = backing = default(Rect);
            UILabel title, sub;
            if (!VRUI.FailureLabels(out title, out sub, out back)) return false;
            Rect t;
            bool has = false;
            if (TextRect(title, out t)) { r = t; has = true; }
            if (sub != null && TextRect(sub, out t))
            {
                r = has ? Rect.MinMaxRect(Mathf.Min(r.xMin, t.xMin), Mathf.Min(r.yMin, t.yMin), Mathf.Max(r.xMax, t.xMax), Mathf.Max(r.yMax, t.yMax)) : t;
                has = true;
            }
            if (!has) return false;
            r = Pad(r, 28f, 18f);
            if (back != null && Shown(back)) Feedback.RtRect(back, out backing);
            return r.width > 8f && r.height > 8f;
        }

        /// A label's printed text on the UI texture (pixels). NGUI lays the text block out from the widget's pivot
        /// (UILabel.ApplyOffset: its top at (1 - pivot.y) x the printed height) and aligns the lines in the widget's width.
        static bool TextRect(UILabel l, out Rect px)
        {
            px = default(Rect);
            Camera cam = VRUI.I != null ? VRUI.I.UiCamera : null;
            if (cam == null || !Shown(l)) return false;
            Vector2 ps = l.printedSize, po = l.pivotOffset;
            if (ps.x < 1f || ps.y < 1f) return false;
            float w = l.width, a = po.x;
            switch (l.alignment)
            {
                case NGUIText.Alignment.Left: a = 0f; break;
                case NGUIText.Alignment.Center: a = 0.5f; break;
                case NGUIText.Alignment.Right: a = 1f; break;
            }
            float x0 = -po.x * w + (w - ps.x) * a, y0 = -po.y * ps.y;
            Transform tr = l.cachedTransform;
            float sx0 = float.MaxValue, sy0 = float.MaxValue, sx1 = float.MinValue, sy1 = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                var local = new Vector3(i == 1 || i == 2 ? x0 + ps.x : x0, i >= 2 ? y0 + ps.y : y0, 0f);
                Vector3 sp = cam.WorldToScreenPoint(tr.TransformPoint(local));
                sx0 = Mathf.Min(sx0, sp.x); sy0 = Mathf.Min(sy0, sp.y); sx1 = Mathf.Max(sx1, sp.x); sy1 = Mathf.Max(sy1, sp.y);
            }
            px = Rect.MinMaxRect(sx0, sy0, sx1, sy1);
            return true;
        }

        // ---- placement ---------------------------------------------------------------------------------
        Vector3 m_dealerHead;
        int m_headVersion = -1;
        Transform m_headT;
        int m_headTVersion = -1;
        float m_nextHeadFind;
        bool m_storyWasOn, m_headFollow;
        public static string HeadFrom = "-";

        /// The Dealer's head bone now: the active "Dealer Head" under the Dealer (the idle model's; GameObject.Find by path
        /// missed it when another of his models was the active one), else above his root.
        Vector3 LiveDealerHead()
        {
            Dealer d = Dealer.Instance;
            if (m_headTVersion != VRRig.I.Version || ((m_headT == null || !m_headT.gameObject.activeInHierarchy) && Time.unscaledTime >= m_nextHeadFind))
            {
                m_headTVersion = VRRig.I.Version;
                m_nextHeadFind = Time.unscaledTime + 1f;
                m_headT = null;
                if (d != null)
                    foreach (Transform t in d.GetComponentsInChildren<Transform>(false))
                        if (t.name == "Dealer Head") { m_headT = t; break; }
                if (m_headT == null)
                {
                    GameObject go = GameObject.Find("Models/Dealer/Dealer_idle_01.mecanim/Dealer Head");
                    m_headT = go != null ? go.transform : null;
                }
            }
            if (m_headT != null && m_headT.gameObject.activeInHierarchy) { HeadFrom = "head bone"; return m_headT.position; }
            if (d != null) { HeadFrom = "his root"; return d.transform.position + Vector3.up * 1.75f; }
            HeadFrom = "fallback";
            return VRRig.I.NeutralEye + VRRig.I.NeutralRot * Vector3.forward * 2f;
        }

        /// The story text's anchor: the Dealer's head, taken when a text appears (and at each arrival), then held still so the
        /// text doesn't bob with his idle animation; if he moves more than 20 cm away from it, it follows him at a calm pace
        /// (after a death he was elsewhere when the place changed: the text stood beside his face).
        void UpdateDealerAnchor(bool storyNow)
        {
            if (!storyNow) { m_storyWasOn = false; return; }
            Vector3 live = LiveDealerHead();
            float sc = VRRig.I.Scale;
            if (!m_storyWasOn || m_headVersion != VRRig.I.Version)
            {
                m_dealerHead = live;
                m_headVersion = VRRig.I.Version;
                m_headFollow = false;
            }
            else
            {
                float d = (live - m_dealerHead).magnitude;
                if (d > 0.2f * sc) m_headFollow = true;
                if (m_headFollow)
                {
                    m_dealerHead = Vector3.MoveTowards(m_dealerHead, live, Mathf.Max(0.6f * sc, 3f * d) * Time.unscaledDeltaTime);
                    if ((live - m_dealerHead).magnitude < 0.01f * sc) m_headFollow = false;
                }
            }
            m_storyWasOn = true;
        }

        Vector3 DealerHead() => m_dealerHead;

        void PlaceStory()
        {
            float sc = VRRig.I.Scale;
            Vector3 eye = VRRig.I.NeutralEye;
            Quaternion yaw = VRRig.I.NeutralRot;
            Vector3 head = DealerHead();
            Vector3 right = yaw * Vector3.right;
            // from the eyes toward a point above and right of the Dealer's head, at StoryDepth of his distance
            Vector3 toHead = head - eye;
            float dist = toHead.magnitude * StoryDepth;
            float perPx = dist * Mathf.Tan(StoryDegPerPx * Mathf.Deg2Rad);
            Vector2 size = new Vector2(m_story.Px.width * perPx, m_story.Px.height * perPx);
            Vector3 target = head + Vector3.up * ((0.16f + StoryAbove) * sc) + right * (StoryRight * sc);
            Vector3 dir = (target - eye).normalized;
            Vector3 centre = eye + dir * dist;
            Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);
            // the bottom edge sits at the target height: lift the centre by half the height
            centre += rot * Vector3.up * (size.y * 0.5f);
            m_story.T.SetPositionAndRotation(centre, rot);
            Fit(m_story, size, 0.025f * sc);
        }

        Vector3 m_boardCentre;
        Quaternion m_boardRot;

        void PlaceBoard()
        {
            float sc = VRRig.I.Scale;
            Vector3 eye = VRRig.I.NeutralEye;
            Quaternion yaw = VRRig.I.NeutralRot;
            float side = Settings.LeftHanded ? -BoardRight : BoardRight;
            // the distance is a setting (user 2026-10-11: "a little further from me"; 0.42 m before)
            float ahead = BoardAheadOverride > 0f ? BoardAheadOverride : Settings.ChoiceBoardDistance;
            float down = BoardDown;
            // the title: a little closer and higher (the menu is short), toward the far menu on the right of the fire
            if (m_titleMode) { ahead = TitleAhead; down = TitleDown; side = Settings.LeftHanded ? -TitleRight : TitleRight; }
            // the challenge map at the side table (round 5 item 4): its sections go to the left, smaller, so the map ahead
            // and its screen beyond it (toward the wall) stay in view; the controls column stays on the right
            bool hubMap = !m_titleMode && !m_menuMode && ScreenMenu == "ChallengeMap" && Places.Current == Places.Place.Hub;
            if (hubMap) { ahead = HubBoardAhead; down = HubBoardDown; side = Settings.LeftHanded ? HubBoardLeft : -HubBoardLeft; }
            Vector3 centre = eye + yaw * (new Vector3(side, -down, ahead) * sc);
            Quaternion rot = Quaternion.LookRotation(centre - eye, yaw * Vector3.up);
            m_boardCentre = centre;
            m_boardRot = rot;
            float perPx = BoardMmPerPx * 0.001f * sc * (hubMap ? HubBoardScale : 1f), gap = (m_titleMode ? TitleGap : PlateGap) * sc, margin = (m_titleMode ? TitleMargin : 0.008f) * sc;
            // long options: the whole column shrinks to stay within BoardMaxWidth (within reach of both hands)
            float widest = 0f;
            for (int i = 0; i < m_navStart; i++) widest = Mathf.Max(widest, m_plates[i].Px.width);
            if (widest * perPx > BoardMaxWidth * sc) perPx = BoardMaxWidth * sc / widest;
            float total = 0f, maxW = 0f;
            for (int i = 0; i < m_navStart; i++)
            {
                total += m_plates[i].Px.height * perPx + (i > 0 ? gap + 2f * margin : 0f);
                maxW = Mathf.Max(maxW, m_plates[i].Px.width * perPx);
            }
            float y = total * 0.5f;
            for (int i = 0; i < m_navStart; i++)
            {
                Quad q = m_plates[i];
                Vector2 size = new Vector2(q.Px.width * perPx, q.Px.height * perPx);
                float x = -maxW * 0.5f + size.x * 0.5f;   // left-aligned, like the game's list
                Vector3 local = new Vector3(x, y - size.y * 0.5f, q.Press);
                q.T.SetPositionAndRotation(centre + rot * local, rot);
                Fit(q, size, margin);
                y -= size.y + gap + 2f * margin;
            }
        }

        static bool NavAllowed => Settings.ControlsOnTable && !Settings.ControlButtons && NavPlace;
        static bool NavPlace => XRSettings.enabled && VRRig.I != null && VRRig.I.HasStation && VRUI.I != null && VRUI.I.RT != null &&
                                Places.AtCaravan && !CombatVR.Active && !VRFade.IsBlack;

        // ---- the controls bar as buttons (Table.ControlButtons; user 2026-10-12 evening: "Several menus didn't have ways to back
        // out of them", "there was no option to continue except pressing A", "The button prompts were in weird places at weird
        // angles and collided with the table"): every live button of the game's controls bars (the main one, the inventory's,
        // the card browser's) is a plate - its own glyph and words cropped from the UI - in one column to the player's right,
        // upright and facing the eyes, within reach. A touch is the button's own action (as a click on it in the flat game).
        public static float NavAhead = 0.44f, NavRight = 0.34f, NavDown = 0.28f, NavPlateMmPerPx = 0.62f, NavGap = 0.010f, NavAboveTable = 0.12f;
        int m_navStart;
        public static int NavPresses;
        public static string NavShown = "";
        static readonly List<NavBarButton> s_navBtns = new List<NavBarButton>();
        static readonly List<KeyValuePair<NavBarButton, Rect>> s_navRects = new List<KeyValuePair<NavBarButton, Rect>>();

        public static bool NavHasA;
        /// The encounter's options on the choice board now (the Scenarios' @confirm).
        public static int OptionPlates;

        /// Selects the encounter's option i as a touch does (the game's hover + A).
        public static bool SelectOption(int i)
        {
            if (I == null || i < 0 || i >= I.m_plates.Count) return false;
            Quad q = I.m_plates[i];
            if (q.Btn == null || !q.Btn.isActiveAndEnabled) return false;
            UISelectable sel = q.Target;
            if (sel == null || !UISelection.TryHover(sel)) return false;
            VRPad.Press("A", 4);
            return true;
        }

        // the column's buttons this frame and when NavShown was last written (it is rebuilt only when they change: building it
        // every frame was garbage for the table's collections - 2026-10-13)
        static readonly List<NavBarButton> s_navNow = new List<NavBarButton>(), s_navWas = new List<NavBarButton>();
        static readonly List<string> s_navWasText = new List<string>();

        static void NavShownUpdate()
        {
            // the same buttons with the same label strings (a label's text is read, not copied: no garbage)
            bool same = s_navNow.Count == s_navWas.Count;
            for (int i = 0; same && i < s_navNow.Count; i++) same = s_navNow[i] == s_navWas[i] && ReferenceEquals(NavLabelText(s_navNow[i]), s_navWasText[i]);
            if (same) return;
            s_navWas.Clear();
            s_navWas.AddRange(s_navNow);
            s_navWasText.Clear();
            for (int i = 0; i < s_navNow.Count; i++) s_navWasText.Add(NavLabelText(s_navNow[i]));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s_navNow.Count; i++) sb.Append(i > 0 ? ", " : "").Append(NavText(s_navNow[i]));
            NavShown = sb.ToString();
        }

        int NavPlates()
        {
            s_navNow.Clear();
            NavHasA = false;
            int count = NavPlatesInner();
            NavShownUpdate();
            return count;
        }

        int NavPlatesInner()
        {
            if (!Settings.ControlButtons || !NavPlace) return 0;
            if (m_navScene != SceneScan.Version)
            {
                m_navScene = SceneScan.Version;
                for (int i = 0; i < s_navBars.Length; i++) { GameObject go = GameObject.Find(s_navBars[i]); m_navRoots[i] = go != null ? go.transform : null; }
            }
            s_navRects.Clear();
            Rect all = default(Rect);
            bool hasAll = false;
            foreach (Transform root in m_navRoots)
            {
                if (root == null || !root.gameObject.activeInHierarchy) continue;
                s_navBtns.Clear();
                root.GetComponentsInChildren(false, s_navBtns);
                foreach (NavBarButton b in s_navBtns)
                {
                    if (b == null || !b.isActiveAndEnabled || b.Action == null) continue;
                    Rect r = default(Rect);
                    bool has = false;
                    s_widgets.Clear();
                    b.GetComponentsInChildren(false, s_widgets);
                    foreach (UIWidget w in s_widgets) if (Shown(w)) GrowDrawn(ref r, ref has, w);
                    if (!has || r.width < 4f) continue;
                    s_navRects.Add(new KeyValuePair<NavBarButton, Rect>(b, r));
                }
                // the whole bar (its black backdrop) leaves the far screen
                s_widgets.Clear();
                root.GetComponentsInChildren(false, s_widgets);
                foreach (UIWidget w in s_widgets) if (Shown(w)) Grow(ref all, ref hasAll, w);
            }
            if (hasAll) m_holes.Add(Pad(all, 4f, 4f));
            // the bar's own backdrop under the buttons (Shared/Bottom's other widgets: the dark strip the user saw as "an
            // empty bar at the bottom that was colliding with stuff") leaves the far screen too
            BottomBackdrop();
            if (s_navRects.Count == 0) return 0;
            // the bar's order: left to right
            s_navRects.Sort((x, y) => x.Value.center.x.CompareTo(y.Value.center.x));
            int n = 0;
            foreach (var kv in s_navRects)
            {
                Quad q = PlateAt(m_plateCount + n);
                q.Btn = null;
                q.Nav = kv.Key;
                q.Sel = kv.Key.GetComponent<UISelectable>();
                Rect r = kv.Value;
                // its own glyph and words only, tall enough to touch. The 6 px margin stops short of the neighbouring
                // buttons on the same bar (playtest 2026-10-17, mark 1: the right edge of "Inventory" and "Camp" showed
                // the left arc of the next button's glyph, 4-5 px away in the game's bar)
                float ph = Mathf.Max(r.height + 10f, 44f);
                float x0 = r.xMin - 6f, x1 = r.xMax + 6f;
                foreach (var o in s_navRects)
                {
                    if (o.Key == kv.Key) continue;
                    Rect b = o.Value;
                    if (b.yMax < r.center.y - ph * 0.5f || b.yMin > r.center.y + ph * 0.5f) continue;   // another bar (row)
                    // (overlapping rects too: never past the neighbour's left edge, but at least half of its own)
                    if (b.center.x > r.center.x) x1 = Mathf.Min(x1, Mathf.Max(b.xMin - 1f, r.center.x));
                    else x0 = Mathf.Max(x0, Mathf.Min(b.xMax + 1f, r.center.x));
                }
                // never into its own glyph or words (mark 5)
                x0 = Mathf.Min(x0, r.xMin - 2f);
                x1 = Mathf.Max(x1, r.xMax + 2f);
                q.Px = new Rect(x0, r.center.y - ph * 0.5f, x1 - x0, ph);
                q.Disabled = false;
                s_navNow.Add(kv.Key);
                if (PadButton(kv.Key) == "A") NavHasA = true;
                n++;
            }
            return n;
        }

        static readonly AccessTools.FieldRef<NavBarButton, InputButton> s_navInput = AccessTools.FieldRefAccess<NavBarButton, InputButton>("m_inputButton");
        static readonly AccessTools.FieldRef<NavBarButton, InputButton> s_navAlt = AccessTools.FieldRefAccess<NavBarButton, InputButton>("m_alternateInputButton");

        /// The virtual pad button (VRPad.Press) behind a controls-bar button, or null.
        static string PadButton(NavBarButton b)
        {
            InputButton ib = s_navAlt(b) != InputButton.Count ? s_navAlt(b) : s_navInput(b);
            switch (ib)
            {
                case InputButton.Select: return "A";
                case InputButton.Cancel: return "B";
                case InputButton.F1: return "BACK";
                case InputButton.F2: return "Y";
                case InputButton.F3: return "X";
                case InputButton.Next: return "RB";
                case InputButton.Prev: return "LB";
                case InputButton.Pause: return "START";
                case InputButton.DPadUp: return "UP";
                case InputButton.DPadDown: return "DOWN";
                case InputButton.DPadLeft: return "LEFT";
                case InputButton.DPadRight: return "RIGHT";
                default: return null;
            }
        }

        Transform m_bottom;
        int m_bottomScene = -1;
        static readonly HashSet<string> s_bottomLogged = new HashSet<string>();

        void BottomBackdrop()
        {
            if (m_bottomScene != SceneScan.Version)
            {
                m_bottomScene = SceneScan.Version;
                GameObject go = GameObject.Find("UIManager/UI Root (2D)/Camera/MenuManager/Shared/Bottom");
                m_bottom = go != null ? go.transform : null;
            }
            if (m_bottom == null || !m_bottom.gameObject.activeInHierarchy) return;
            s_widgets.Clear();
            m_bottom.GetComponentsInChildren(false, s_widgets);
            foreach (UIWidget w in s_widgets)
            {
                if (!Shown(w) || w is UILabel) continue;   // words stay (a hint or a title would still be read there)
                if (w.GetComponentInParent<NavBar>() != null) continue;
                Rect r = default(Rect);
                bool has = false;
                Grow(ref r, ref has, w);
                if (!has || r.height > VRUI.Height * 0.25f) continue;   // only strips
                m_holes.Add(Pad(r, 2f, 2f));
                if (s_bottomLogged.Add(w.name)) Plugin.L.LogInfo("boards: the controls bar's backdrop '" + VRUI.Path(w.transform) + "' leaves the far screen (" + R(r) + ")");
            }
        }

        static string NavLabelText(NavBarButton b)
        {
            UILabel l = b != null ? b.GetComponentInChildren<UILabel>() : null;
            return l != null ? l.text : null;
        }

        static string NavText(NavBarButton b)
        {
            UILabel l = b != null ? b.GetComponentInChildren<UILabel>() : null;
            return l != null && !string.IsNullOrEmpty(l.text) ? l.text : b != null ? b.name : "-";
        }

        /// The column: to the player's right (left for the left-handed), a little below the choice board, facing the eyes.
        void PlaceNavPlates()
        {
            VRRig rig = VRRig.I;
            float sc = rig.Scale;
            Vector3 eye = rig.NeutralEye;
            Quaternion yaw = rig.NeutralRot;
            float side = Settings.LeftHanded ? -NavRight : NavRight;
            float perPx = NavPlateMmPerPx * 0.001f * sc, gap = NavGap * sc, margin = 0.006f * sc;
            float total = 0f;
            for (int i = m_navStart; i < m_plateCount; i++) total += m_plates[i].Px.height * perPx + (i > m_navStart ? gap + 2f * margin : 0f);
            // (P2-4) with the open inventory's cards lowered, the column goes down with them (it stood below their row's right
            // end) - but never nearer the table top than NavAboveTable (seated, the table is close): what it can't go down, it
            // goes out to the side instead, past the row's end
            float down = NavDown;
            if (InventoryLayers.Active)
            {
                float want = NavDown + InventoryLayers.CardsLower;
                float lowest = eye.y - (want * sc + total * 0.5f), floorY = TableTopY(eye.y - 0.8f * sc) + NavAboveTable * sc;
                float short_ = lowest < floorY ? (floorY - lowest) / sc : 0f;
                down = want - Mathf.Min(short_, InventoryLayers.CardsLower);
                side += (Settings.LeftHanded ? -1f : 1f) * Mathf.Min(short_, InventoryLayers.CardsLower) * 1.5f;
            }
            float maxW = 0f;
            for (int i = m_navStart; i < m_plateCount; i++) maxW = Mathf.Max(maxW, m_plates[i].Px.width * perPx);
            // (the third playtest, mark 27: "the cards and buttons are clipping through each other" - a card the game shows on
            // the right, zoomed, stood where the column is: the column steps down, then out to the side, until the card is clear
            // of it as the eyes see them)
            // (P4-6, playtest 4 mark 221208_1: the encounter's own card - the game's EncounterContainer, here at 0.5 m - stood
            // where the column was, its Inventory / Continue over the card's corner: every card the encounter shows counts too,
            // each on its own)
            s_navObstacles.Clear();
            Bounds zb;
            if (TableHud.ZoomBounds(out zb)) s_navObstacles.Add(zb);
            EncounterCards(s_navObstacles, sc);
            // (tests: `navobstacle add <right> <down> <ahead> <w> <h>`: a card-sized box where the eyes see the column - the user's
            // encounter card of P4-6 - eye-relative, real metres)
            foreach (Vector4 t in s_testObstacles)
            {
                Vector3 c = eye + yaw * (new Vector3(t.x, -t.y, t.z) * sc);
                Vector3 ex = yaw * new Vector3(t.w * 0.5f * sc, 0f, 0f), ez = yaw * new Vector3(0f, 0f, 0.002f * sc);
                s_navObstacles.Add(new Bounds(c, 2f * new Vector3(Mathf.Abs(ex.x) + Mathf.Abs(ez.x), s_testObstacleH * 0.5f * sc, Mathf.Abs(ex.z) + Mathf.Abs(ez.z))));
            }
            if (s_navObstacles.Count > 0)
            {
                float floorY = TableTopY(eye.y - 0.8f * sc) + NavAboveTable * sc;
                float baseDown = down, baseSide = side;
                for (int k = 0; k <= 14; k++)
                {
                    down = baseDown + Mathf.Min(k, 6) * 0.03f;
                    side = baseSide + (Settings.LeftHanded ? -1f : 1f) * Mathf.Max(0, k - 6) * 0.03f;
                    if (eye.y - (down * sc + total * 0.5f) < floorY) down = (eye.y - floorY) / sc - total * 0.5f / sc;
                    bool meets = false;
                    foreach (Bounds ob in s_navObstacles) if (ColumnMeets(eye, yaw, side, down, maxW, total, margin, ob, sc)) { meets = true; break; }
                    if (!meets) break;
                }
                if (Mathf.Abs(down - baseDown) > 1e-3f || Mathf.Abs(side - baseSide) > 1e-3f) NavClear = string.Format(System.Globalization.CultureInfo.InvariantCulture, "clear of the shown cards: {0:0.00} m lower, {1:0.00} m out", down - baseDown, Mathf.Abs(side - baseSide));
                else NavClear = "-";
            }
            else NavClear = "-";
            Vector3 centre = eye + yaw * (new Vector3(side, -down, NavAhead) * sc);
            Quaternion rot = Quaternion.LookRotation(centre - eye, yaw * Vector3.up);
            float y = total * 0.5f;
            for (int i = m_navStart; i < m_plateCount; i++)
            {
                Quad q = m_plates[i];
                Vector2 size = new Vector2(q.Px.width * perPx, q.Px.height * perPx);
                // left-aligned (the list's own look)
                float x = -maxW * 0.5f + size.x * 0.5f;
                q.T.SetPositionAndRotation(centre + rot * new Vector3(x, y - size.y * 0.5f, q.Press), rot);
                Fit(q, size, margin);
                y -= size.y + gap + 2f * margin;
            }
            m_navCentre = centre;
            m_navRot = rot;
            m_navSide = side; m_navDown = down; m_navW = maxW; m_navH = total; m_navMargin = margin;
        }

        float m_navSide, m_navDown, m_navW, m_navH, m_navMargin;

        /// `navcards`: every card in the scene whose box meets the controls column as the eyes see it (path, box centre and size,
        /// distance from the eyes) - which card a column covers (P4-6).
        string NavCards()
        {
            if (VRRig.I == null || m_plateCount <= m_navStart) return "no column";
            VRRig rig = VRRig.I;
            float sc = rig.Scale;
            var sb = new System.Text.StringBuilder();
            int n = 0;
            var rs = new List<Renderer>();
            foreach (Card c in FindObjectsOfType<Card>())
            {
                if (c == null || !c.isActiveAndEnabled) continue;
                rs.Clear();
                c.GetComponentsInChildren(false, rs);
                Bounds b = default(Bounds);
                bool has = false;
                foreach (Renderer r in rs)
                {
                    if (r == null || !r.enabled || r is ParticleSystemRenderer || r.GetComponentInParent<ParticleSystem>() != null) continue;
                    if (r.bounds.size.magnitude > 0.8f * sc) continue;
                    if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
                }
                if (!has || !ColumnMeets(rig.NeutralEye, rig.NeutralRot, m_navSide, m_navDown, m_navW, m_navH, m_navMargin, b, sc)) continue;
                n++;
                sb.AppendFormat(CultureInfo.InvariantCulture, "\n  {0} centre {1} size {2} {3:0.00} m from the eyes", VRUI.Path(c.transform), Diag.V(b.center), Diag.V(b.size), Vector3.Distance(b.center, rig.NeutralEye) / sc);
            }
            Plugin.L.LogInfo("navcards: " + n + " cards meet the column" + sb);
            return n + " cards meet the column (see log)" + (n > 0 ? ": " + sb.ToString().Replace("\n  ", " | ").Substring(0, Math.Min(400, sb.Length)) : "");
        }

        public static string NavClear = "-";
        static readonly List<Bounds> s_navObstacles = new List<Bounds>();
        static readonly List<Vector4> s_testObstacles = new List<Vector4>();
        static float s_testObstacleH = 0.24f;
        static readonly List<Renderer> s_encRs = new List<Renderer>();
        static readonly List<Card> s_encCards = new List<Card>();

        /// Each card the encounter shows at the table (the game's encounter and enemy rows: DeckManager.EncounterContainer /
        /// EnemyContainer), one box each.
        static void EncounterCards(List<Bounds> into, float sc)
        {
            DeckManager dm = DeckManager.Instance;
            if (dm == null || !Places.AtCaravan) return;
            RowCards(dm.EncounterContainer, into, sc);
            RowCards(dm.EnemyContainer, into, sc);
        }

        static void RowCards(CardContainer row, List<Bounds> into, float sc)
        {
            {
                if (row == null || !row.gameObject.activeInHierarchy) return;
                s_encCards.Clear();
                row.GetComponentsInChildren(false, s_encCards);
                foreach (Card c in s_encCards)
                {
                    if (c == null || !c.isActiveAndEnabled) continue;
                    s_encRs.Clear();
                    c.GetComponentsInChildren(false, s_encRs);
                    bool has = false;
                    Bounds b = default(Bounds);
                    foreach (Renderer r in s_encRs)
                    {
                        if (r == null || !r.enabled || r is ParticleSystemRenderer || r.GetComponentInParent<ParticleSystem>() != null) continue;
                        if (r.bounds.size.magnitude > 0.8f * sc) continue;
                        if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
                    }
                    if (has) into.Add(b);
                }
            }
        }

        /// Does the column (its middle `side` right, `down` below and NavAhead ahead of the eyes, `w` x `h` world) meet the box
        /// `zb` as the eyes see them (the box's corners carried along their sight lines onto the column's plane)?
        static bool ColumnMeets(Vector3 eye, Quaternion yaw, float side, float down, float w, float h, float margin, Bounds zb, float sc)
        {
            Vector3 c = eye + yaw * (new Vector3(side, -down, NavAhead) * sc);
            Quaternion rot = Quaternion.LookRotation(c - eye, yaw * Vector3.up);
            Vector3 n = rot * Vector3.forward;
            float d = Vector3.Dot(c - eye, n);
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = new Vector3((i & 1) == 0 ? zb.min.x : zb.max.x, (i & 2) == 0 ? zb.min.y : zb.max.y, (i & 4) == 0 ? zb.min.z : zb.max.z);
                float along = Vector3.Dot(p - eye, n);
                if (along <= 1e-3f) return false;   // behind the eyes' plane: not in the way
                Vector3 q = Quaternion.Inverse(rot) * (eye + (p - eye) * (d / along) - c);
                x0 = Mathf.Min(x0, q.x); x1 = Mathf.Max(x1, q.x); y0 = Mathf.Min(y0, q.y); y1 = Mathf.Max(y1, q.y);
            }
            float hw = w * 0.5f + margin + 0.01f * sc, hh = h * 0.5f + margin + 0.01f * sc;
            return x1 > -hw && x0 < hw && y1 > -hh && y0 < hh;
        }

        Vector3 m_navCentre;
        Quaternion m_navRot = Quaternion.identity;

        /// `navgeo`: where the controls column is (the playtester's prompt check, user 2026-10-12: "The button prompts were in
        /// weird places at weird angles and collided with the table"): its lowest edge above the table top, its angle to the
        /// eyes, whether it overlaps the choice board, real metres.
        string NavGeo()
        {
            if (m_plateCount <= m_navStart || VRRig.I == null) return "no controls shown";
            float sc = VRRig.I.Scale;
            Vector3 eye = VRRig.I.HeadWorldPos;
            float low = float.MaxValue, maxAng = 0f;
            Rect col = new Rect(0, 0, 0, 0);
            bool first = true;
            for (int i = m_navStart; i < m_plateCount; i++)
            {
                Quad q = m_plates[i];
                if (q.T == null) continue;
                Vector3 c = q.T.position;
                float half = q.T.lossyScale.y * 0.5f;
                low = Mathf.Min(low, c.y - Mathf.Abs(half));
                maxAng = Mathf.Max(maxAng, Vector3.Angle(-q.T.forward, eye - c));
                Vector2 v = ViewXY(eye, c);
                if (first) { col = new Rect(v, Vector2.zero); first = false; } else col = Rect.MinMaxRect(Mathf.Min(col.xMin, v.x), Mathf.Min(col.yMin, v.y), Mathf.Max(col.xMax, v.x), Mathf.Max(col.yMax, v.y));
            }
            bool overlap = false;
            for (int i = 0; i < m_navStart; i++)
            {
                Quad q = m_plates[i];
                if (q.T == null) continue;
                Vector2 v = ViewXY(eye, q.T.position);
                if (v.x > col.xMin - 3f && v.x < col.xMax + 3f && v.y > col.yMin - 3f && v.y < col.yMax + 3f) overlap = true;
            }
            float top = TableTopY(eye.y - 0.8f * sc);
            return string.Format(CultureInfo.InvariantCulture, "controls {0}: lowest edge {1:0.00} m above the table top, facing the eyes within {2:0} deg, overlaps the choice board {3}",
                m_plateCount - m_navStart, (low - top) / sc, maxAng, overlap ? "YES" : "no");
        }

        /// Degrees right / up of the straight-ahead view (the neutral yaw) - for overlap tests between boards.
        static Vector2 ViewXY(Vector3 eye, Vector3 p)
        {
            Vector3 d = Quaternion.Inverse(VRRig.I.NeutralRot) * (p - eye);
            return new Vector2(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg);
        }

        /// The controls bar: its two button groups as strips on the table edge, the whole bar cut out of the panel.
        void NavStrips()
        {
            Rect l = default(Rect), r = default(Rect), all = default(Rect);
            bool hasL = false, hasR = false, hasAll = false;
            if (NavAllowed)
            {
                if (m_navScene != SceneScan.Version)
                {
                    m_navScene = SceneScan.Version;
                    for (int i = 0; i < s_navBars.Length; i++) { GameObject go = GameObject.Find(s_navBars[i]); m_navRoots[i] = go != null ? go.transform : null; }
                }
                foreach (Transform root in m_navRoots)
                {
                    if (root == null || !root.gameObject.activeInHierarchy) continue;
                    s_widgets.Clear();
                    root.GetComponentsInChildren(false, s_widgets);
                    foreach (UIWidget w in s_widgets)
                    {
                        if (!Shown(w)) continue;
                        Grow(ref all, ref hasAll, w);
                        string side = Side(w.transform, root);
                        if (side == "L") Grow(ref l, ref hasL, w);
                        else if (side == "R") Grow(ref r, ref hasR, w);
                    }
                }
            }
            if (hasAll) m_holes.Add(Pad(all, 4f, 4f));
            if ((hasL || hasR) && m_navL == null)
            {
                EnsureMaterials();
                m_navL = Make("HoF2VR_ControlsL", 0.78f, false);
                m_navR = Make("HoF2VR_ControlsR", 0.78f, false);
            }
            if (m_navL == null) return;
            PlaceNav(m_navL, hasL, Pad(l, 10f, 6f), -1f);
            PlaceNav(m_navR, hasR, Pad(r, 10f, 6f), 1f);
        }

        /// Which group of the bar a widget belongs to: under a "BottomLeft" or "BottomRight" container.
        static string Side(Transform t, Transform root)
        {
            for (Transform p = t; p != null && p != root; p = p.parent)
            {
                if (p.name == "BottomLeft") return "L";
                if (p.name == "BottomRight") return "R";
            }
            return "";
        }

        void PlaceNav(Quad q, bool on, Rect px, float sideSign)
        {
            Show(q, on);
            if (!on) return;
            q.Px = px;
            VRRig rig = VRRig.I;
            float sc = rig.Scale;
            Vector3 eye = rig.NeutralEye;
            Quaternion yaw = rig.NeutralRot;
            float top = TableTopY(eye.y - 0.8f * sc);
            Vector2 size = new Vector2(px.width, px.height) * (NavMmPerPx * 0.001f * sc);
            // the strips sit beside each other, their inner ends NavSide from the middle, lying on the table edge
            float x = sideSign * (NavSide * sc + size.x * 0.5f);
            Vector3 c = new Vector3(eye.x, top + 0.004f * sc, eye.z) + yaw * new Vector3(x, 0f, (Settings.StandDistance + NavPastEdge) * sc);
            // facing the eyes like the choice plates, its top edge away from the player: it leans back over the table
            Quaternion rot = Quaternion.LookRotation(c - eye, yaw * Vector3.forward);
            q.T.SetPositionAndRotation(c + (rot * Vector3.up) * (size.y * 0.5f + 0.004f * sc), rot);
            Fit(q, size, 0.006f * sc);
        }

        static float s_topY = float.NaN;
        static int s_topVersion = -1;

        static float TableTopY(float fallback)
        {
            if (VRRig.I != null && s_topVersion == VRRig.I.Version && !float.IsNaN(s_topY)) return s_topY;
            s_topVersion = VRRig.I != null ? VRRig.I.Version : -1;
            GameObject go = GameObject.Find("Models/Dealer_Caravan/dealer_caravan_cardtable/Table/Collider");
            Collider c = go != null ? go.GetComponent<Collider>() : null;
            s_topY = c != null ? c.bounds.max.y : fallback;
            return s_topY;
        }

        void UpdatePlateLook()
        {
            UISelectable hovered = null;
            foreach (Quad q in m_plates)
            {
                UISelectable tgt = q.Target;
                if (!q.On || q.Frame == null || (tgt == null && q.Nav == null)) continue;
                if (q.Nav == null && hovered == null && tgt.Selection != null) hovered = tgt.Selection.HoveredObject;
                bool hot = q.Hovered || (q.Nav == null && hovered == tgt);
                Renderer fr = q.Frame.GetComponent<Renderer>();
                Mesh m = q.Frame.GetComponent<MeshFilter>().sharedMesh;
                // the frame warms up as a fingertip comes near (docs/hand-feel-design.md §3.4 "Near"), gold when hovered
                Color idle = new Color(0.62f, 0.48f, 0.22f, 0.85f), hotC = new Color(1f, 0.82f, 0.4f, 1f);
                Color c = hot ? hotC : Color.Lerp(idle, hotC, Mathf.Round(q.Glow * 0.7f * 20f) / 20f);
                if (q.Disabled) c = new Color(0.3f, 0.28f, 0.25f, 0.6f);
                if (c == q.FrameColour) continue;
                q.FrameColour = c;
                Color lin = new Color(Mathf.GammaToLinearSpace(c.r), Mathf.GammaToLinearSpace(c.g), Mathf.GammaToLinearSpace(c.b), c.a);
                m.colors = new[] { lin, lin, lin, lin, lin, lin, lin, lin };
                if (fr != null && !fr.enabled) fr.enabled = true;
            }
        }

        // ---- the laser ---------------------------------------------------------------------------------
        /// Where a world ray crosses the story panel or a plate, as a UI-texture pixel (the nearest one).
        public bool Hit(Ray ray, out Vector2 px, out float dist)
        {
            px = Vector2.zero;
            dist = float.MaxValue;
            bool any = false;
            if (m_story != null && m_story.On) any |= HitQuad(m_story, ray, ref px, ref dist);
            for (int i = 0; i < m_plateCount && i < m_plates.Count; i++) if (m_plates[i].On) any |= HitQuad(m_plates[i], ray, ref px, ref dist);
            return any;
        }

        /// The world point where a UI-texture pixel is shown when it lies in the story or on a plate (the laser test
        /// seams aim there instead of at the main panel's hole).
        public bool WorldForPixel(Vector2 px, out Vector3 world)
        {
            world = Vector3.zero;
            if (m_story != null && m_story.On && Map(m_story, px, out world)) return true;
            for (int i = 0; i < m_plateCount && i < m_plates.Count; i++) if (m_plates[i].On && Map(m_plates[i], px, out world)) return true;
            return false;
        }

        static bool Map(Quad q, Vector2 px, out Vector3 world)
        {
            world = Vector3.zero;
            if (!q.Px.Contains(px) || q.T == null) return false;
            world = q.T.TransformPoint(new Vector3((px.x - q.Px.xMin) / q.Px.width - 0.5f, (px.y - q.Px.yMin) / q.Px.height - 0.5f, 0f));
            return true;
        }

        static bool HitQuad(Quad q, Ray ray, ref Vector2 px, ref float best)
        {
            var plane = new Plane(q.T.forward, q.T.position);
            float d;
            if (!plane.Raycast(ray, out d) || d <= 0f || d >= best) return false;
            Vector3 local = q.T.InverseTransformPoint(ray.GetPoint(d));
            if (Mathf.Abs(local.x) > 0.5f || Mathf.Abs(local.y) > 0.5f) return false;
            px = new Vector2(q.Px.xMin + (local.x + 0.5f) * q.Px.width, q.Px.yMin + (local.y + 0.5f) * q.Px.height);
            best = d;
            return true;
        }

        // ---- touch -------------------------------------------------------------------------------------
        PressPlate PlateOf(int i) => i < m_plates.Count ? m_plates[i] : null;

        /// A fingertip is on a plate (the laser leaves the game's hover alone meanwhile).
        public static bool Touching(Hand h) => I != null && I.m_touch.Touching(h);

        /// A plate is near this fingertip (the pre-shaping now comes from HandFeel.PointWeight).
        public static bool WantsPoint(Hand h) => I != null && I.m_touch.Near(h);

        void StepTouch(Hand h)
        {
            // a pointing hand (grip squeezed, index lifted) touches as well as an open one; a fist that pages doesn't
            bool ok = m_plateCount > 0 && (m_titleMode ? Settings.TitleMenuBoard : m_menuMode || m_dialogueMode ? Settings.MenuBoard : Settings.ChoiceBoard) &&
                      (m_touch.Overridden(h) || (h.Tracked && !h.TriggerBtn && (!h.GripBtn || VRPad.PointingGrip(h)) && !VRFade.IsBlack));
            m_touch.Step(h, PlateOf, m_plateCount, ok);
        }

        bool HoverPlate(int i, Hand h)
        {
            VRPointer.Active = false;   // the finger owns the game's hover now
            Quad q = m_plates[i];
            if (q.Nav != null) return true;   // a controls-bar button: no hover in the game, the plate lights up
            UISelectable sel = q.Target;
            return sel != null && UISelection.TryHover(sel);
        }

        /// The laser's own click: hover it, then A (the game's select on what is hovered).
        string PressPlateAt(int i, Hand h)
        {
            Quad q = m_plates[i];
            if (q.Nav != null)
            {
                // the controls bar's button: its own action, as a click on it in the flat game
                if (!q.Nav.isActiveAndEnabled || q.Nav.Action == null) return "gone";
                NavPresses++;
                // the button's own pad button, as if the player pressed it on the controller: the game's input decides (an
                // A acts on what is hovered, as the flat game's A does); a button without a pad button: its click action
                string pad = PadButton(q.Nav);
                Plugin.L.LogInfo("boards: controls button '" + NavText(q.Nav) + "' pressed (" + (pad ?? "its click") + ")");
                if (pad != null) VRPad.Press(pad, 4);
                else q.Nav.OnSelected(null);
                return null;
            }
            if (q.Bar != null) { DragBar(q, h); return null; }   // a slider / scroll bar: the value under the finger (no A)
            if (q.Enum != null && m_menuMode) return PressEnum(q, h);
            UISelectable sel = q.Target;
            if (sel == null || !sel.IsValid) return "not valid";
            if (q.Disabled || !sel.IsSelectable) return "disabled";
            if (!UISelection.TryHover(sel))
            {
                // a nav-bar button or a control in another selection group: its own select
                if (m_menuMode || m_dialogueMode) return UISelection.TrySelect(sel) ? null : "hover refused";
                return "hover refused";
            }
            VRPad.Press("A", 4);
            return null;
        }

        static string Label(Quad q)
        {
            if (q.Nav != null) return "nav:" + NavText(q.Nav);
            if (q.Btn == null && q.Sel != null)
            {
                UILabel tl = q.Sel.GetComponentInChildren<UILabel>();
                return tl != null && !string.IsNullOrEmpty(tl.text) ? tl.text : q.Sel.name;
            }
            UILabel t = q.Btn != null ? s_btnText(q.Btn) : null;
            return t != null && !string.IsNullOrEmpty(t.text) ? t.text : q.Btn != null ? q.Btn.name : "-";
        }

        // ---- diagnostics and test seams ------------------------------------------------------------------
        string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(m_story != null && m_story.On ? "story " + R(m_story.Px) : "no story");
            if (m_titleMode) sb.Append(" | title menu");
            if (m_menuMode || m_dialogueMode) sb.Append(" | ").Append(m_menuMode ? "menu board " : "table dialogue ").Append(MenuKind).Append(m_menuMode ? " " + R(m_menuPx) + " (" + MenuLayout + "; pages " + MenuPages + ", content changes " + MenuRelayouts + ", shrinks " + MenuShrinks + ", row presses " + EnumPresses + ")" : "");
            if (ScreenMenu.Length > 0) sb.Append(" | screen ").Append(ScreenMenu).Append(NavTabsLeftOut > 0 ? " (challenge tabs left to the map's pins: " + NavTabsLeftOut + ")" : "");
            if (NoTouchMenu.Length > 0) sb.Append(" | NO TOUCH WAY ON: ").Append(NoTouchMenu);
            sb.Append(" | plates ").Append(m_plateCount);
            for (int i = 0; i < m_plateCount; i++)
                sb.Append(" [").Append(i + 1).Append(' ').Append(Label(m_plates[i])).Append(m_plates[i].Bar != null ? string.Format(CultureInfo.InvariantCulture, " ={0:0.00}", m_plates[i].Bar.value) : "")
                  .Append(m_plates[i].Hovered ? " *" : "").Append(']');
            sb.Append(" | ").Append(m_touch.Describe());
            return sb.ToString();
        }

        static string R(Rect r) => string.Format(CultureInfo.InvariantCulture, "({0:0},{1:0})-({2:0},{3:0})", r.xMin, r.yMin, r.xMax, r.yMax);

        string Command(string[] a)
        {
            var inv = CultureInfo.InvariantCulture;
            // `boards aim|click <n>`: the laser (the real VRPointer path, from the controller) at plate n's middle
            if (a.Length > 2 && (a[1] == "aim" || a[1] == "click"))
            {
                int n = int.Parse(a[2]) - 1;
                Vector3? p = PlateFace(n, 0f);
                if (p == null) return "no plate " + (n + 1);
                VRPointer.AimWorld(p.Value, a[1] == "click");
                return (a[1] == "click" ? "clicking" : "aiming at") + " plate " + (n + 1) + " '" + Label(m_plates[n]) + "'";
            }
            if (a.Length > 2 && (a[1] == "bar" || a[1] == "val") || a.Length > 1 && a[1] == "dialogue") return MenuCommand(a);
            if (a.Length > 2 && a[1] == "set")
            {
                for (int i = 2; i + 1 < a.Length; i += 2)
                {
                    float v = float.Parse(a[i + 1], inv);
                    switch (a[i])
                    {
                        case "deg": StoryDegPerPx = v; break;
                        case "above": StoryAbove = v; break;
                        case "right": StoryRight = v; break;
                        case "depth": StoryDepth = v; break;
                        case "ahead": BoardAheadOverride = v; break;
                        case "down": BoardDown = v; break;
                        case "bright": BoardRight = v; break;
                        case "mm": BoardMmPerPx = v; break;
                    }
                }
            }
            string s = Describe();
            if (m_story != null && m_story.On && VRRig.I != null)
            {
                Vector3 e = VRRig.I.NeutralEye, c = m_story.T.position;
                Vector3 d = Quaternion.Inverse(VRRig.I.NeutralRot) * (c - e);
                s += string.Format(inv, " | story centre {0} = {1:0.00} m, {2:0} deg right, {3:0} deg up, {4:0.00}x{5:0.00} m", Diag.V(c), (c - e).magnitude / VRRig.I.Scale,
                    Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, m_story.Size.x / VRRig.I.Scale, m_story.Size.y / VRRig.I.Scale);
            }
            if (m_plateCount > 0 && VRRig.I != null)
            {
                Vector3 e = VRRig.I.NeutralEye;
                Vector3 d = Quaternion.Inverse(VRRig.I.NeutralRot) * (m_boardCentre - e);
                s += string.Format(inv, " | board centre {0}: {1:0.00} ahead {2:0.00} below {3:0.00} right (m)", Diag.V(m_boardCentre), d.z / VRRig.I.Scale, -d.y / VRRig.I.Scale, d.x / VRRig.I.Scale);
                if (m_plateCount > m_navStart)
                {
                    Vector3 dn = Quaternion.Inverse(VRRig.I.NeutralRot) * (m_navCentre - VRRig.I.NeutralEye);
                    s += string.Format(inv, " | column {0:0.00} ahead {1:0.00} below {2:0.00} right, {3}", dn.z / VRRig.I.Scale, -dn.y / VRRig.I.Scale, dn.x / VRRig.I.Scale, NavClear);
                }
                for (int i = 0; i < m_plateCount; i++)
                    s += string.Format(inv, " | plate {0} px {1} at {2} {3:0.000}x{4:0.000} m", i + 1, R(m_plates[i].Px), Diag.V(m_plates[i].T.position), m_plates[i].Size.x / VRRig.I.Scale, m_plates[i].Size.y / VRRig.I.Scale);
            }
            return s;
        }

        /// The world point `cm` in front of plate n's face (n from 0).
        Vector3? PlateFace(int n, float cm)
        {
            if (n < 0 || n >= m_plateCount || !m_plates[n].On || VRRig.I == null) return null;
            Quad q = m_plates[n];
            return q.T.position - q.T.forward * (q.Press + cm * 0.01f * VRRig.I.Scale);
        }

        /// `platepose <n> [cm] [l|r]`: the point `cm` (default 6) in front of plate n's face and the xrsim move
        /// that pushes a fingertip from there 1.5 cm through it.
        string PlatePose(string[] a)
        {
            int n = a.Length > 1 ? int.Parse(a[1]) - 1 : 0;
            float cm = a.Length > 2 ? float.Parse(a[2], CultureInfo.InvariantCulture) : 6f;
            Hand h = a.Length > 3 && a[3] == "l" ? VRInput.L : VRInput.R;
            Vector3? p = PlateFace(n, cm);
            if (p == null) return "none";
            Quad q = m_plates[n];
            float s = VRRig.I.Scale;
            // `platepose <n> <cm> r <fx>`: fx (-0.5 .. 0.5) of the plate's width to the right of its middle (P4-1's ◁ ▷ rows)
            if (a.Length > 4) p = p.Value + q.T.right * (float.Parse(a[4], CultureInfo.InvariantCulture) * q.Size.x);
            Vector3 d = VRRig.I.FromWorldDir(q.T.forward * ((cm + 1.5f) * 0.01f * s)) / s;
            return string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####} {2:0.####} | move {3} {4:0.####} {5:0.####} {6:0.####}",
                p.Value.x, p.Value.y, p.Value.z, h.IsLeft ? "l" : "r", d.x, d.y, -d.z);
        }

        /// `ptouch <n> [tap|hover|swipe|slow] [l|r]`: a synthetic fingertip through the real state machine.
        /// `ptouch <label> ...` (e.g. nav:Continue): the plate with that label if one is shown now - found in the same frame as
        /// the touch starts, so a test never touches whatever took that plate's number since it read the boards (on the
        /// dungeon map the plates are the next places to go: a tap there walks the hero on). `ptouch opt`: the first plate
        /// that is not a controls button (a page's first option), likewise.
        string PTouch(string[] a)
        {
            if (a.Length < 2) return Describe();
            int n;
            if (!int.TryParse(a[1], out n))
            {
                n = -1;
                for (int i = 0; i < m_plateCount; i++)
                {
                    string l = Label(m_plates[i]);
                    if (a[1] == "opt" ? !l.StartsWith("nav:", StringComparison.Ordinal) : l == a[1]) { n = i + 1; break; }
                }
                if (n < 0) return "no plate '" + a[1] + "' (" + m_plateCount + " shown)";
            }
            n -= 1;
            string mode = a.Length > 2 ? a[2] : "tap";
            Hand h = a.Length > 3 && a[3] == "l" ? VRInput.L : VRInput.R;
            if (n < 0 || n >= m_plateCount) return "no plate " + (n + 1) + " (" + m_plateCount + " shown)";
            StartCoroutine(m_touch.Drive(this, h, PlateOf, n, mode, null));
            return mode + " plate " + (n + 1) + " '" + Label(m_plates[n]) + "'";
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Menus by touch (docs/hand-feel-design.md §3.12 Phase 1b, docs/interaction-design.md §3.6), with the same press as
    /// everything else (PlateTouch):
    ///  * the pause menu and its settings (anywhere, fights included: the game is paused), the menus over the title (its
    ///    settings) and the game's question boxes (UIDialogue: "Are you sure?", the wheel nudge) away from the card table come
    ///    onto a MENU BOARD within reach: a framed crop of the menu (0.5 m ahead, a little below the eyes, where the player
    ///    looked when it opened; the far screen leaves it out), every control on it a plate in place, raised 6 mm, pressed
    ///    like a button (the game's hover + A; a control of another selection group: its own select);
    ///  * sliders and the scroll bar: pressed, then dragged - the value follows the finger along the bar while it holds the
    ///    plate down (a tick every tenth);
    ///  * at the card table a question's answers stand on the choice board like an encounter's options (the question stays on
    ///    the far screen).
    /// The stick and A keep working; lasers stay off. Settings: [Hands] MenuBoard.
    internal partial class TableBoards
    {
        public static float MenuAhead = 0.50f, MenuDown = 0.14f, MenuMmPerPx = 0.34f, MenuRaise = 0.006f, MenuBackAlpha = 0.74f;
        bool m_menuMode, m_dialogueMode, m_menuPlaced;
        public static string MenuKind = "";
        Quad m_menuBoard;
        Material m_boardCrop, m_boardBack;
        Rect m_menuPx;
        // (a board that jumped under the finger, 2026-10-18: the pause menu's Settings page opens with the hovered tab's options
        // beside the tabs - 17 plates - and they close when the hover moves on - 6; the board re-centred on what was left, so the
        // Back plate a finger was coming to moved away. Now each page of a menu keeps the pixel at its board's middle - the anchor -
        // where it was, so what stays never moves; and the board keeps the size it had (it grows when more shows, never shrinks
        // while the page is up). A new page - its first control another - is laid out afresh.)
        Vector2 m_menuAnchor;
        Rect m_menuUnion;
        float m_menuShrinkSince = -1f;
        public static float MenuShrinkAfter = 1.0f;
        public static int MenuShrinks;
        UISelectable m_menuKey;
        int m_menuLastCount = -1;
        Rect m_menuLast;
        public static int MenuRelayouts, MenuPages;
        public static string MenuLayout = "-";
        Vector3 m_menuEye;
        Quaternion m_menuYaw = Quaternion.identity;
        static readonly List<UISelectable> s_menuSels = new List<UISelectable>();
        static readonly List<KeyValuePair<UISelectable, Rect>> s_menuRects = new List<KeyValuePair<UISelectable, Rect>>();
        public static int MenuPresses, BarDrags, DialogueFarHidden;
        static readonly Rect FullScreen = new Rect(0f, 0f, VRUI.Width, VRUI.Height);
        // the VR settings page's way in (VRSettings): a button under the pause menu's list, on this board
        public static bool PauseEntryOn;
        public static Vector3 PauseEntryCentre;
        public static Quaternion PauseEntryRot = Quaternion.identity;
        public static Vector2 PauseEntrySize;
        bool m_entry;
        Rect m_entryPx;
        static readonly HarmonyLib.AccessTools.FieldRef<PauseMenu, UISelectable> s_pmQuit = HarmonyLib.AccessTools.FieldRefAccess<PauseMenu, UISelectable>("m_quitSelectable");
        static readonly HarmonyLib.AccessTools.FieldRef<PauseMenu, UISelectable> s_pmCredits = HarmonyLib.AccessTools.FieldRefAccess<PauseMenu, UISelectable>("m_creditsSelectable");
        static readonly HarmonyLib.AccessTools.FieldRef<PauseMenu, UIGrid> s_pmList = HarmonyLib.AccessTools.FieldRefAccess<PauseMenu, UIGrid>("m_defaultSettingsGrid");

        static bool MenuAllowed => XRSettings.enabled && VRRig.I != null && VRUI.I != null && VRUI.I.RT != null && Settings.MenuBoard && !VRFade.IsBlack &&
                                   !Limbo.On;

        /// The game's menu in front that a board takes over, or null: a question box over everything; while the game is paused
        /// the pause menu; at the title a menu over the start screen (its settings, saves, mods).
        static Transform FrontMenu(out string kind)
        {
            kind = "";
            MenuManager mm = MenuManager.Instance;
            if (mm == null) return null;
            try
            {
                Menu d = mm.CurrentMenu(MenuManager.StackPriority.Dialogue);
                if (d != null && d.isActiveAndEnabled) { kind = "dialogue"; return Top(d.transform, mm.transform); }
                if (mm.CurrentMenu(MenuManager.StackPriority.Failure) != null) return null;
                Menu s = mm.CurrentMenu(MenuManager.StackPriority.Secondary);
                if (s != null && s.isActiveAndEnabled && (mm.Paused || Places.Current == Places.Place.Title)) { kind = mm.Paused ? "pause" : "title menu"; return Top(s.transform, mm.transform); }
            }
            catch (Exception) { }
            return null;
        }

        /// The menu's whole screen: the pause menu's Menu sits on Pause/Left/PauseMenu while its settings live under Pause/Right,
        /// so the top container under the menu stacks (MenuManager/SecondaryMenus/Pause) is taken.
        internal static bool PauseMenuOpen()
        {
            try
            {
                Menu s = MenuManager.Instance != null ? MenuManager.Instance.CurrentMenu(MenuManager.StackPriority.Secondary) : null;
                return s != null && s.isActiveAndEnabled && s.GetComponentInChildren<PauseMenu>(true) != null;
            }
            catch (Exception) { return false; }
        }

        static Transform Top(Transform t, Transform manager)
        {
            Transform top = t;
            for (Transform x = t; x != null && x.parent != null; x = x.parent)
            {
                if (x.parent == manager) break;
                if (x.parent.name.EndsWith("Menus", StringComparison.Ordinal) || x.parent.name.EndsWith("Dialogues", StringComparison.Ordinal) ||
                    x.parent.name == "DialogueRoot") { top = x; break; }
            }
            if (top != s_lastTop) { s_lastTop = top; if (top != null) Plugin.L.LogInfo("boards: the menu in front is '" + VRUI.Path(top) + "' (its Menu on '" + VRUI.Path(t) + "')"); }
            return top;
        }
        static Transform s_lastTop;

        /// This frame's plates for the menu in front (0 = none); sets the mode.
        int MenuPlates()
        {
            m_menuMode = m_dialogueMode = false;
            MenuKind = "";
            string kind = "";
            Transform root = MenuAllowed ? FrontMenu(out kind) : null;
            // the hand check open (no board while it is: its own board is in front): over the pause menu - opened from the VR
            // settings page's Hands tab (P4-3) - the menu stays out of the far screen and the board keeps its place for the page's
            // return; over anything else no board, as before
            if (root != null && HandCheck.I != null && HandCheck.I.Active)
            {
                if (kind == "pause") { m_holes.Add(FullScreen); MenuKind = "hand check"; return 0; }
                root = null;
            }
            if (root == null) { m_menuPlaced = false; return 0; }
            m_entry = false;
            // the VR settings page stands in for the pause menu: no board and no plates, but the menu stays out of the far screen
            if (VRSettings.PageOpen && kind == "pause")
            {
                m_holes.Add(FullScreen);
                MenuKind = "vr settings";
                return 0;
            }
            // a question at the card table goes on the choice board, unless it belongs to the pause menu (which is on the
            // menu board then); a question box counts as paused for the game, so the pause menu itself is what is checked
            bool tableDialogue = kind == "dialogue" && Places.Current == Places.Place.Caravan && !CombatVR.Active && !PauseMenuOpen();
            // the controls: every live selectable under the menu, with the rect of what it shows (clipped by its scroll panel)
            s_menuSels.Clear();
            root.GetComponentsInChildren(false, s_menuSels);
            s_menuRects.Clear();
            foreach (UISelectable sel in s_menuSels)
            {
                if (sel == null || !sel.isActiveAndEnabled || sel.IsFallthrough) continue;
                // (P4-1) a settings row's arrows (◁ value ▷) are part of its row: the row's plate takes them (PressEnum)
                if (ArrowOfRow(sel)) continue;
                Rect r;
                if (!SelectableRect(sel, out r)) continue;
                s_menuRects.Add(new KeyValuePair<UISelectable, Rect>(sel, r));
            }
            if (!tableDialogue) SettingsColumn();
            // a control that contains another one (a setting's row round its arrows) gives way to it
            for (int i = s_menuRects.Count - 1; i >= 0; i--)
            {
                Rect a = s_menuRects[i].Value;
                for (int j = 0; j < s_menuRects.Count; j++)
                {
                    if (j == i) continue;
                    Rect b = s_menuRects[j].Value;
                    if (b.width * b.height < a.width * a.height && a.Contains(b.center) && b.xMin >= a.xMin - 2f && b.xMax <= a.xMax + 2f) { s_menuRects.RemoveAt(i); break; }
                }
            }
            if (s_menuRects.Count == 0) return 0;
            // top to bottom, then left to right (stable numbers for the test seams)
            s_menuRects.Sort((x, y) => Mathf.Abs(x.Value.center.y - y.Value.center.y) > 8f ? y.Value.center.y.CompareTo(x.Value.center.y) : x.Value.center.x.CompareTo(y.Value.center.x));
            int n = 0;
            foreach (var kv in s_menuRects)
            {
                Quad q = PlateAt(m_plateCount + n);
                q.Btn = null;
                q.Nav = null;
                q.Sel = kv.Key;
                q.Px = tableDialogue ? Pad(Grow(kv.Value, MinPlatePx), 4f, 2f) : kv.Value;
                q.Disabled = !kv.Key.IsSelectable;
                q.Enum = kv.Key.GetComponent<EnumControl>();
                BarOf(q, kv.Key);
                n++;
            }
            MenuKind = kind;
            if (tableDialogue) { m_dialogueMode = true; return n; }
            // the board: the menu's own area (its visible parts, without full-screen backdrops), left out of the far screen
            Rect area;
            if (!MenuArea(root, out area)) return 0;
            // the VR settings button under the pause menu's own list (its main page only), on the board
            if (kind == "pause" && VRSettings.Wanted && PauseEntryRect(root, n, out m_entryPx))
            {
                m_entry = true;
                Rect e = Pad(m_entryPx, 12f, 12f);
                area = Rect.MinMaxRect(Mathf.Min(area.xMin, e.xMin), Mathf.Min(area.yMin, e.yMin), Mathf.Max(area.xMax, e.xMax), Mathf.Max(area.yMax, e.yMax));
            }
            m_menuPx = area;
            m_menuMode = true;
            m_holes.Add(area);
            // (P4-2, playtest 4: Exit's "Are you sure you want to quit?" stood in front of a big black slab, larger than the pause
            // board and off to its side - the game darkens the whole screen behind a question (and the pause menu under it
            // showed dimmed through that): on the far screen 1.6 m away. A question's board stands alone: the far screen is left
            // out entirely while it is up.)
            // (and the pause menu's: when the board shrank back after Credits, the right half's dark panel showed there too)
            if (kind == "dialogue" || kind == "pause") { m_holes.Add(FullScreen); DialogueFarHidden++; }
            return n;
        }

        // ---- screens that only go on with a button (user 2026-10-12 evening: "Sometimes there was no option to continue
        // except pressing A"): their own buttons on the choice board, as plates (the game's hover + A, as a touch does)
        /// The game's primary screens whose buttons come onto the choice board (the dungeon map, the inventory, an encounter
        /// and the card browsers have their own touch: cards, tiles, options, the controls column).
        public static readonly HashSet<string> ScreenMenus = new HashSet<string> { "Attract", "ChallengeMap", "ScoreDisplay", "TokenUnlock", "ChallengeComplete", "ChallengeFailed", "Results", "EndRun" };
        public static string ScreenMenu = "", NoTouchMenu = "";
        /// the challenge tabs left off the plates on this frame (P2-8)
        public static int NavTabsLeftOut;
        static readonly HashSet<string> s_noTouchLogged = new HashSet<string>();

        int ScreenPlates()
        {
            ScreenMenu = "";
            MenuManager mm = MenuManager.Instance;
            if (mm == null || !XRSettings.enabled || !Settings.ChoiceBoard || !Places.AtCaravan || VRFade.IsBlack || Limbo.On) return 0;
            Menu cur;
            try { cur = mm.CurrentMenu(MenuManager.StackPriority.Primary); } catch (Exception) { return 0; }
            if (cur == null || !cur.isActiveAndEnabled) return 0;
            string name = cur.name.Replace("(Clone)", "");
            if (!ScreenMenus.Contains(name)) return 0;
            s_menuSels.Clear();
            cur.GetComponentsInChildren(false, s_menuSels);
            int n = 0, tabs = 0;
            foreach (UISelectable sel in s_menuSels)
            {
                if (sel == null || !sel.isActiveAndEnabled || sel.IsFallthrough || !sel.IsValid) continue;
                if (sel.GetComponent<NavBarButton>() != null) continue;
                // (P2-8, the second playtest's mark 2: "half of the map selectors on them") the challenge map's own challenge
                // tabs - small icons in rows, so a plate cut out the halves of two rows. Each tab is a pin on the map in front of
                // the player, touched there; only the map's section buttons (Campaign, Adventure, Endless...) come on plates.
                // (the third playtest, mark 7: "Something's wrong with this top bar here" - the Campfire section's own tab, a
                // UICustomisationNavTab, came on a plate: the column's common edges made it a wide dark box with its small red banner
                // in a corner. Every nav tab - challenges' and the campfire's - stays with the tabs' row, TabsGrow times its size)
                if (sel.GetComponent<UINavTab>() != null) { tabs++; continue; }
                Rect r;
                if (!SelectableRect(sel, out r)) continue;
                if (n >= 6) break;
                Quad q = PlateAt(m_plateCount + n);
                q.Btn = null;
                q.Nav = null;
                q.Sel = sel;
                q.Px = Pad(Grow(r, MinPlatePx), 4f, 2f);
                q.Disabled = !sel.IsSelectable;
                n++;
            }
            NavTabsLeftOut = tabs;
            if (n > 0) ScreenMenu = name;
            return n;
        }

        /// The playtester's "no way on by touch" check: the primary screen at the table (no menu over it) whose buttons
        /// have no plate at all (logged once per screen, so new screens show up).
        void NoTouchCheck()
        {
            NoTouchMenu = "";
            MenuManager mm = MenuManager.Instance;
            // (B1: at the table before a fight the encounter card held out to the player is the way on - FightCard)
            // (an encounter whose only button is the lone arrow left to the column's Continue is touchable: N10)
            if (mm == null || m_plateCount > 0 || ArrowLeftToColumn || !XRSettings.enabled || !Places.AtCaravan || VRFade.IsBlack || Limbo.On || CombatVR.Active || FightCard.AtTable) return;
            Menu cur;
            try
            {
                foreach (MenuManager.StackPriority pr in s_over) if (mm.CurrentMenu(pr) != null) return;
                cur = mm.CurrentMenu(MenuManager.StackPriority.Primary);
            }
            catch (Exception) { return; }
            if (cur == null || !cur.isActiveAndEnabled) return;
            string name = cur.name.Replace("(Clone)", "");
            s_menuSels.Clear();
            cur.GetComponentsInChildren(false, s_menuSels);
            int buttons = 0;
            foreach (UISelectable sel in s_menuSels)
            {
                if (sel == null || !sel.isActiveAndEnabled || sel.IsFallthrough || !sel.IsValid) continue;
                Rect r;
                if (SelectableRect(sel, out r)) buttons++;
            }
            if (buttons == 0) return;
            NoTouchMenu = name + " (" + buttons + " buttons)";
            if (s_noTouchLogged.Add(name)) Plugin.L.LogWarning("boards: the screen '" + name + "' has " + buttons + " buttons and no touch plates (only the controller's buttons work there)");
        }

        /// Where the VR settings button goes on the pause menu's main page: one step below its last button (Exit), as wide as
        /// the list; false on its sub-pages (the settings) or when the list is not found.
        bool PauseEntryRect(Transform root, int n, out Rect r)
        {
            r = default(Rect);
            PauseMenu pm = root.GetComponentInChildren<PauseMenu>();
            if (pm == null) return false;
            UIGrid list = s_pmList(pm);
            if (list == null || !list.gameObject.activeInHierarchy) return false;
            UISelectable quit = s_pmQuit(pm), credits = s_pmCredits(pm);
            Quad qq = null, qc = null;
            float l = float.MaxValue, rr = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Quad q = m_plates[m_plateCount + i];
                if (q.Sel == null || !q.Sel.transform.IsChildOf(list.transform)) continue;
                if (q.Sel == quit) qq = q;
                if (q.Sel == credits) qc = q;
                l = Mathf.Min(l, q.Px.xMin);
                rr = Mathf.Max(rr, q.Px.xMax);
            }
            if (qq == null || rr <= l) return false;
            float pitch = qc != null ? qc.Px.center.y - qq.Px.center.y : 0f;
            if (pitch < qq.Px.height * 1.1f) pitch = qq.Px.height * 1.3f;
            float h = Mathf.Max(qq.Px.height, 40f);
            r = new Rect(l, qq.Px.center.y - pitch - h * 0.5f, rr - l, h);
            return r.yMin > 4f;
        }

        /// The pause menu board's frame (the eyes and the heading when it opened), for the VR settings page.
        public static bool MenuFrame(out Vector3 eye, out Quaternion yaw)
        {
            eye = I != null ? I.m_menuEye : Vector3.zero;
            yaw = I != null ? I.m_menuYaw : Quaternion.identity;
            return I != null && I.m_menuPlaced;
        }

        static Rect Grow(Rect r, Vector2 min)
        {
            float w = Mathf.Max(r.width + 40f, min.x), h = Mathf.Max(r.height + 8f, min.y);
            return new Rect(r.center.x - w * 0.5f, r.center.y - h * 0.5f, w, h);
        }

        /// What a control shows on the UI texture (its visible widgets), clipped by the scroll panel it sits in; false when it
        /// shows nothing or is mostly scrolled out of view.
        static bool SelectableRect(UISelectable sel, out Rect r)
        {
            r = default(Rect);
            bool has = false;
            s_widgets.Clear();
            sel.GetComponentsInChildren(false, s_widgets);
            // (P4-1) the game's hover glow (HoveredSprite, HighlightSprite) doesn't size a plate: a hovered button's plate grew
            // over its neighbour's and shrank again as the hover moved on
            foreach (UIWidget w in s_widgets) if (Shown(w) && !HoverArt(w)) Grow(ref r, ref has, w);
            UIWidget own = sel.GetComponent<UIWidget>();
            if (!has && own != null && own.enabled && own.gameObject.activeInHierarchy && own.width > 8 && own.height > 8) { Feedback.RtRect(own, out r); has = true; }
            if (!has) return false;
            float full = r.width * r.height;
            // full-screen catchers are not buttons
            if (r.width > VRUI.Width * 0.9f && r.height > VRUI.Height * 0.9f) return false;
            UIPanel panel = NGUITools.FindInParents<UIPanel>(sel.gameObject);
            Rect clip;
            if (panel != null && panel.hasClipping && ClipRect(panel, out clip))
            {
                r = Rect.MinMaxRect(Mathf.Max(r.xMin, clip.xMin), Mathf.Max(r.yMin, clip.yMin), Mathf.Min(r.xMax, clip.xMax), Mathf.Min(r.yMax, clip.yMax));
                if (r.width <= 0f || r.height <= 0f || r.width * r.height < 0.6f * full) return false;
            }
            r = Pad(r, 4f, 2f);
            return r.width >= 12f && r.height >= 12f;
        }

        // ---- P4-1 (playtest 4, "the menu is all screwed up in the settings screen"): the Settings page's rows ----------
        // Each row of the game's settings list (a PauseMenuControl: its name on the left, a divider, its value on the right) is
        // 900 px wide on the UI texture, the name right-aligned in a wide field. Their plates were the union of what each
        // showed: a checkbox row from the name field's far left edge (over the hovered tab's plate) to its box, a button row to
        // its button, a row with arrows only two tiny arrow plates out at its right end (its own plate gave way to them) - every
        // frame a different width, straddling the menu's teal/black seam. Now every row is one plate of the same width: from
        // the furthest-left name as written (its glyphs) to the value field's right end, arrows inside; a row with ◁ ▷ changes
        // its value by where the fingertip presses (the value's left half: back, elsewhere: on).
        public static int EnumPresses, ColumnRows;

        static bool ArrowOfRow(UISelectable sel)
        {
            EnumControl en = sel.GetComponentInParent<EnumControl>();
            return en != null && en.Selectable != sel && en.isActiveAndEnabled && en.Selectable != null && en.Selectable.isActiveAndEnabled;
        }

        /// The settings rows (PauseMenuControls that are not sliders) on one common column.
        static void SettingsColumn()
        {
            float l = float.MaxValue, r = float.MinValue;
            int rows = 0;
            for (int i = 0; i < s_menuRects.Count; i++)
            {
                PauseMenuControl c = s_menuRects[i].Key.GetComponent<PauseMenuControl>();
                if (c == null || c is SliderControl) continue;
                Rect own;
                UIWidget w = c.GetComponent<UIWidget>();
                if (w == null || !Feedback.RtRect(w, out own)) continue;
                float start = NameStart(c, own);
                l = Mathf.Min(l, start - 16f);
                r = Mathf.Max(r, own.xMax - 12f);
                rows++;
            }
            ColumnRows = rows;
            if (rows < 2 || r <= l) return;
            for (int i = 0; i < s_menuRects.Count; i++)
            {
                PauseMenuControl c = s_menuRects[i].Key.GetComponent<PauseMenuControl>();
                if (c == null || c is SliderControl) continue;
                Rect old = s_menuRects[i].Value, own;
                UIWidget w = c.GetComponent<UIWidget>();
                if (w == null || !Feedback.RtRect(w, out own)) continue;
                // the row's own height (inside the scroll panel's clip, as its plate was)
                float y0 = Mathf.Max(own.yMin + 2f, old.yMin), y1 = Mathf.Min(own.yMax - 2f, old.yMax);
                if (y1 - y0 < 12f) continue;
                s_menuRects[i] = new KeyValuePair<UISelectable, Rect>(s_menuRects[i].Key, Rect.MinMaxRect(l, y0, r, y1));
            }
        }

        /// Where a row's name starts as written (its right-aligned label's glyphs), else its row's left edge.
        static float NameStart(PauseMenuControl c, Rect own)
        {
            float best = float.MaxValue;
            foreach (UILabel lab in c.GetComponentsInChildren<UILabel>(false))
            {
                if (lab == null || !lab.enabled || string.IsNullOrEmpty(lab.text) || lab.finalAlpha < 0.02f) continue;
                Rect d;
                if (!Drawn(lab, out d)) continue;
                if (d.center.x > own.center.x) continue;   // the name's side only (the value is on the right)
                best = Mathf.Min(best, d.xMin);
            }
            return best < float.MaxValue ? best : own.xMin;
        }

        /// A label's glyphs on the UI texture (its geometry, widget space), else its widget's rect.
        static bool Drawn(UILabel lab, out Rect r)
        {
            r = default(Rect);
            Camera cam = VRUI.I != null ? VRUI.I.UiCamera : null;
            if (cam == null) return false;
            List<Vector3> v = lab.geometry != null ? lab.geometry.verts : null;
            if (v == null || v.Count == 0) return Feedback.RtRect(lab, out r);
            Transform t = lab.cachedTransform;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < v.Count; i++)
            {
                Vector3 sp = cam.WorldToScreenPoint(t.TransformPoint(v[i]));
                x0 = Mathf.Min(x0, sp.x); y0 = Mathf.Min(y0, sp.y); x1 = Mathf.Max(x1, sp.x); y1 = Mathf.Max(y1, sp.y);
            }
            r = Rect.MinMaxRect(x0, y0, x1, y1);
            return r.width > 0f;
        }

        /// A row with ◁ value ▷ pressed: back when the fingertip is over the value's left half, on elsewhere (the game's own
        /// arrows: EnumControl.PrevValue / NextValue, refused at an end as its arrow is then).
        string PressEnum(Quad q, Hand h)
        {
            EnumControl en = q.Enum;
            if (en == null || VRRig.I == null) return "gone";
            UISelection.TryHover(q.Sel);   // the game's highlight on the row, as a hover would
            float perPx = MenuMmPerPx * 0.001f * VRRig.I.Scale;
            Vector3 local = Quaternion.Inverse(m_boardRot) * (HandFeel.Tip(h) - m_boardCentre);
            float px = m_menuAnchor.x + local.x / perPx;
            Rect field;
            bool back = en.FieldLabel != null && Feedback.RtRect(en.FieldLabel, out field) && px < field.center.x && px > field.xMin - 40f;
            UISelectable left = null, right = null;
            int k = 0;
            foreach (UISelectable s in en.Selectables) { if (k == 1) left = s; else if (k == 2) right = s; k++; }
            UISelectable arrow = back ? left : right;
            if (arrow != null && !arrow.enabled) return "disabled";
            if (back) en.PrevValue(); else en.NextValue();
            EnumPresses++;
            Plugin.L.LogInfo("boards: the row '" + Label(q) + "' " + (back ? "back" : "on") + " -> '" + (en.FieldLabel != null ? en.FieldLabel.text : "?") + "'");
            return null;
        }

        static bool HoverArt(UIWidget w) => w is UISprite && (w.name.IndexOf("Hover", StringComparison.Ordinal) >= 0 || w.name.IndexOf("Highlight", StringComparison.Ordinal) >= 0);

        static bool ClipRect(UIPanel p, out Rect r)
        {
            r = default(Rect);
            Camera cam = VRUI.I != null ? VRUI.I.UiCamera : null;
            if (cam == null) return false;
            Vector3[] c = p.worldCorners;
            if (c == null || c.Length < 4) return false;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (Vector3 w in c)
            {
                Vector3 s = cam.WorldToScreenPoint(w);
                x0 = Mathf.Min(x0, s.x); y0 = Mathf.Min(y0, s.y); x1 = Mathf.Max(x1, s.x); y1 = Mathf.Max(y1, s.y);
            }
            r = Rect.MinMaxRect(x0, y0, x1, y1);
            return true;
        }

        /// The menu's own area on the UI texture: every visible widget under it except full-screen backdrops.
        static bool MenuArea(Transform root, out Rect r)
        {
            r = default(Rect);
            bool has = false;
            s_widgets.Clear();
            root.GetComponentsInChildren(false, s_widgets);
            foreach (UIWidget w in s_widgets)
            {
                if (!Shown(w)) continue;
                Rect px;
                if (!Feedback.RtRect(w, out px)) continue;
                if (px.width > VRUI.Width * 0.85f && px.height > VRUI.Height * 0.85f) continue;   // a backdrop
                UIPanel panel = NGUITools.FindInParents<UIPanel>(w.gameObject);
                Rect clip;
                if (panel != null && panel.hasClipping && ClipRect(panel, out clip))
                {
                    px = Rect.MinMaxRect(Mathf.Max(px.xMin, clip.xMin), Mathf.Max(px.yMin, clip.yMin), Mathf.Min(px.xMax, clip.xMax), Mathf.Min(px.yMax, clip.yMax));
                    if (px.width <= 0f || px.height <= 0f) continue;
                }
                if (!has) { r = px; has = true; }
                else r = Rect.MinMaxRect(Mathf.Min(r.xMin, px.xMin), Mathf.Min(r.yMin, px.yMin), Mathf.Max(r.xMax, px.xMax), Mathf.Max(r.yMax, px.yMax));
            }
            if (!has) return false;
            r = Pad(r, 12f, 12f);
            return r.width > 40f && r.height > 30f;
        }

        // ---- sliders and the scroll bar -------------------------------------------------------------------------------
        static void BarOf(Quad q, UISelectable sel)
        {
            q.Bar = null;
            UIProgressBar bar = sel.GetComponentInChildren<UIProgressBar>();
            if (bar == null) bar = sel.GetComponentInParent<UIScrollBar>();
            if (bar == null || !bar.isActiveAndEnabled) return;
            UIWidget bg = bar.backgroundWidget != null ? bar.backgroundWidget : bar.foregroundWidget;
            Rect track;
            if (bg == null || !Feedback.RtRect(bg, out track) || track.width < 4f || track.height < 4f) return;
            q.Bar = bar;
            q.Track = track;
            Rect thumb = default(Rect);
            UIScrollBar sb = bar as UIScrollBar;
            if (sb != null && bar.foregroundWidget != null) Feedback.RtRect(bar.foregroundWidget, out thumb);
            q.Thumb = thumb;
            // the bar itself is the plate (a scroll bar's selectable is only its thumb; a slider's is its whole row, label
            // included: touching the label must not set the value)
            Rect bar2 = track;
            if (bar.thumb != null)
            {
                Rect th;
                UIWidget tw = bar.thumb.GetComponent<UIWidget>();
                if (tw != null && Feedback.RtRect(tw, out th)) bar2 = Rect.MinMaxRect(Mathf.Min(bar2.xMin, th.xMin), Mathf.Min(bar2.yMin, th.yMin), Mathf.Max(bar2.xMax, th.xMax), Mathf.Max(bar2.yMax, th.yMax));
            }
            q.Px = Pad(bar2, sb != null ? 10f : 14f, sb != null ? 4f : 10f);
        }

        /// While a finger holds a slider or scroll bar plate down, the value follows it along the bar.
        void DragBars(Hand h)
        {
            int i = m_touch.LatchedPlate(h);
            if (i < 0 || i >= m_plateCount) return;
            Quad q = m_plates[i];
            if (q.Bar != null) DragBar(q, h);
        }

        void DragBar(Quad q, Hand h)
        {
            if (q.Bar == null || VRRig.I == null) return;
            float sc = VRRig.I.Scale, perPx = MenuMmPerPx * 0.001f * sc;
            Vector3 local = Quaternion.Inverse(m_boardRot) * (HandFeel.Tip(h) - m_boardCentre);
            float px = m_menuAnchor.x + local.x / perPx, py = m_menuAnchor.y + local.y / perPx;
            Rect t = q.Track;
            float pos;
            switch (q.Bar.fillDirection)
            {
                case UIProgressBar.FillDirection.RightToLeft: pos = (t.xMax - px) / t.width; break;
                case UIProgressBar.FillDirection.BottomToTop: pos = (py - t.yMin) / t.height; break;
                case UIProgressBar.FillDirection.TopToBottom: pos = (t.yMax - py) / t.height; break;
                default: pos = (px - t.xMin) / t.width; break;
            }
            UIScrollBar sb = q.Bar as UIScrollBar;
            if (sb != null)
            {
                // a scroll bar's value is where its thumb is: the thumb's middle under the finger
                float b = Mathf.Clamp(sb.barSize, 0f, 0.95f);
                pos = (pos - b * 0.5f) / Mathf.Max(0.05f, 1f - b);
            }
            pos = Mathf.Clamp01(pos);
            float old = q.Bar.value;
            if (Mathf.Abs(old - pos) < 0.002f) return;
            q.Bar.value = pos;   // the game's own path: the bar's onChange (a setting's value, the scroll view's position)
            BarDrags++;
            if (Mathf.FloorToInt(old * 10f + 0.001f) != Mathf.FloorToInt(pos * 10f + 0.001f)) Haptics.Cue(Haptics.CueKind.Tick, h.IsLeft);
        }

        /// A settings control's value as text (PauseMenuControl<T>.CurrentValue), "" for anything else.
        static string ControlValue(UISelectable sel)
        {
            if (sel == null) return "";
            foreach (MonoBehaviour mb in sel.GetComponents<MonoBehaviour>())
            {
                if (mb == null || !(mb is PauseMenuControl)) continue;
                // (SliderControl overrides CurrentValue with a setter only: the getter is on a base type)
                for (Type t = mb.GetType(); t != null; t = t.BaseType)
                {
                    var pi = t.GetProperty("CurrentValue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly);
                    if (pi == null || pi.GetGetMethod() == null) continue;
                    try { return Convert.ToString(pi.GetValue(mb, null), CultureInfo.InvariantCulture); }
                    catch (Exception) { return ""; }
                }
                return "";
            }
            return "";
        }

        static readonly HarmonyLib.AccessTools.FieldRef<PauseMenu, Dialogue> s_forfeitError = HarmonyLib.AccessTools.FieldRefAccess<PauseMenu, Dialogue>("m_forfeitErrorDialoguePrefab");

        /// Test seam `boards dialogue`: one of the game's own question boxes (the pause menu's "finish the encounter first"
        /// notice, through the game's Dialogue.Show) wherever the player is, e.g. at the card table without pausing.
        static string ShowTestDialogue()
        {
            PauseMenu pm = null;
            foreach (PauseMenu x in Resources.FindObjectsOfTypeAll<PauseMenu>()) if (x != null && x.gameObject.scene.IsValid()) { pm = x; break; }
            Dialogue prefab = pm != null ? s_forfeitError(pm) : null;
            if (prefab == null) return "no dialogue prefab";
            Dialogue d = Dialogue.Show(prefab);
            return d != null ? "shown '" + d.name + "'" : "not shown";
        }

        /// Test seams: `boards bar <n> [value]` reads (or sets, through the bar's own value) plate n's slider / scroll bar;
        /// `boards val <n>` a settings control's value.
        string MenuCommand(string[] a)
        {
            if (a[1] == "dialogue") return ShowTestDialogue();
            int n = int.Parse(a[2]) - 1;
            if (n < 0 || n >= m_plateCount) return "no plate " + (n + 1);
            Quad q = m_plates[n];
            if (a[1] == "val") return "plate " + (n + 1) + " '" + Label(q) + "' = " + ControlValue(q.Sel);
            if (q.Bar == null) return "plate " + (n + 1) + " is not a bar";
            if (a.Length > 3) q.Bar.value = float.Parse(a[3], CultureInfo.InvariantCulture);
            return string.Format(CultureInfo.InvariantCulture, "plate {0} bar {1:0.000} ({2}) value {3}", n + 1, q.Bar.value, q.Bar.fillDirection, ControlValue(q.Sel));
        }

        // ---- the board -------------------------------------------------------------------------------------------------
        void ShowMenuBoard(bool on)
        {
            if (!on)
            {
                if (!VRSettings.PageOpen) m_menuPlaced = false;   // back from the VR settings page: the board where it was
                PauseEntryOn = false;
                if (m_menuBoard != null) Show(m_menuBoard, false);
                return;
            }
            if (m_menuBoard == null)
            {
                EnsureMaterials();
                if (m_boardCrop == null)
                {
                    // the board is drawn before its plates (5102/5103) and after the far screen (5100)
                    m_boardCrop = new Material(m_crop) { name = "HoF2VR_MenuCrop", renderQueue = 5101 };
                    m_boardBack = new Material(m_back) { name = "HoF2VR_MenuBacking", renderQueue = 5100 };
                }
                // (P4-1) its backing lets the room show through dimly where the menu draws nothing (the settings list's right
                // half), as the flat game shows its world behind that list - it was a flat black slab
                m_menuBoard = Make("HoF2VR_MenuBoard", MenuBackAlpha, true);
                m_menuBoard.R.sharedMaterial = m_boardCrop;
                if (m_menuBoard.Back != null) m_menuBoard.Back.GetComponent<MeshRenderer>().sharedMaterial = m_boardBack;
            }
            Show(m_menuBoard, true);
        }

        /// The board where the player looked when the menu opened (it then stays put), its plates in place on it, raised.
        void PlaceMenu()
        {
            VRRig rig = VRRig.I;
            float sc = rig.Scale;
            bool fresh = !m_menuPlaced;
            if (!m_menuPlaced)
            {
                m_menuPlaced = true;
                m_menuEye = rig.HeadWorldPos;
                m_menuYaw = Quaternion.Euler(0f, rig.HeadWorldRot.eulerAngles.y, 0f);
                Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "boards: the menu board for the {0} ({1} plates, {2:0}x{3:0} px)", MenuKind, m_plateCount, m_menuPx.width, m_menuPx.height));
            }
            float perPx = MenuMmPerPx * 0.001f * sc;
            Vector3 centre = m_menuEye + m_menuYaw * (new Vector3(0f, -MenuDown, MenuAhead) * sc);
            Quaternion rot = Quaternion.LookRotation(centre - m_menuEye, Vector3.up);
            m_boardCentre = centre;
            m_boardRot = rot;
            Rect c = m_menuPx;
            // the page: laid out afresh when it is new (the board's first time, or its first control another), else kept in place
            UISelectable key = m_plateCount > 0 ? m_plates[0].Sel : null;
            if (fresh || key != m_menuKey)
            {
                m_menuKey = key;
                m_menuAnchor = c.center;
                m_menuUnion = c;
                m_menuShrinkSince = -1f;
                MenuPages++;
                MenuLayout = MenuKind + " page laid out: " + m_plateCount + " plates, " + c.width.ToString("0", CultureInfo.InvariantCulture) + "x" + c.height.ToString("0", CultureInfo.InvariantCulture) + " px";
                Plugin.L.LogInfo("boards: the menu board's page laid out (" + MenuKind + ", " + m_plateCount + " plates, " + R(c) + ", first control '" + (key != null ? key.name : "-") + "')");
            }
            else if (m_plateCount != m_menuLastCount || Mathf.Abs(c.width - m_menuLast.width) > 2f || Mathf.Abs(c.height - m_menuLast.height) > 2f)
            {
                m_menuShrinkSince = -1f;
                Rect u = Rect.MinMaxRect(Mathf.Min(m_menuUnion.xMin, c.xMin), Mathf.Min(m_menuUnion.yMin, c.yMin), Mathf.Max(m_menuUnion.xMax, c.xMax), Mathf.Max(m_menuUnion.yMax, c.yMax));
                MenuRelayouts++;
                MenuLayout = MenuKind + " content changed: " + m_menuLastCount + " -> " + m_plateCount + " plates, kept in place" + (u != m_menuUnion ? " (the board grew)" : "");
                Plugin.L.LogInfo("boards: the menu board's content changed (" + MenuKind + ", " + m_menuLastCount + " -> " + m_plateCount + " plates, " + R(m_menuLast) + " -> " + R(c) +
                                 "): the plates kept in place" + (u != m_menuUnion ? ", the board grew to " + R(u) : ", the board as it was"));
                m_menuUnion = u;
            }
            m_menuLastCount = m_plateCount;
            m_menuLast = c;
            // (P4-1: hovering Credits shows the credits beside the pause list - the board grew to 1832 px - and when the hover
            // moved on the board kept that size, its right half an empty black slab) the board goes back to what shows after a
            // moment; the anchor stays, so nothing on it moves
            bool smaller = c.xMin > m_menuUnion.xMin + 8f || c.xMax < m_menuUnion.xMax - 8f || c.yMin > m_menuUnion.yMin + 8f || c.yMax < m_menuUnion.yMax - 8f;
            if (!smaller) m_menuShrinkSince = -1f;
            else if (m_menuShrinkSince < 0f) m_menuShrinkSince = Time.unscaledTime;
            else if (Time.unscaledTime - m_menuShrinkSince > MenuShrinkAfter)
            {
                MenuShrinks++;
                Plugin.L.LogInfo("boards: the menu board shrank back to what shows (" + MenuKind + ", " + R(m_menuUnion) + " -> " + R(c) + ")");
                m_menuUnion = c;
                m_menuShrinkSince = -1f;
            }
            Rect board = m_menuUnion;
            m_menuBoard.Px = board;
            m_menuBoard.T.SetPositionAndRotation(centre + rot * new Vector3((board.center.x - m_menuAnchor.x) * perPx, (board.center.y - m_menuAnchor.y) * perPx, 0f), rot);
            Fit(m_menuBoard, new Vector2(board.width * perPx, board.height * perPx), 0.012f * sc);
            float raise = MenuRaise * sc;
            for (int i = 0; i < m_plateCount; i++)
            {
                Quad q = m_plates[i];
                var size = new Vector2(q.Px.width * perPx, q.Px.height * perPx);
                var local = new Vector3((q.Px.center.x - m_menuAnchor.x) * perPx, (q.Px.center.y - m_menuAnchor.y) * perPx, -raise + q.Press);
                q.T.SetPositionAndRotation(centre + rot * local, rot);
                Fit(q, size, 0.003f * sc);
            }
            PauseEntryOn = m_entry;
            if (m_entry)
            {
                Rect e = m_entryPx;
                PauseEntryCentre = centre + rot * new Vector3((e.center.x - m_menuAnchor.x) * perPx, (e.center.y - m_menuAnchor.y) * perPx, 0f);
                PauseEntryRot = rot;
                PauseEntrySize = new Vector2(e.width * perPx, e.height * perPx);
            }
        }
    }
}

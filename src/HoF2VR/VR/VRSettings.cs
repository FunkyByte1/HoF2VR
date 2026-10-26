using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The VR settings page (PLAY.md "VR settings"): the mod's settings the player compares most, in the pause menu. On the
    /// pause menu's touch board a "VR Settings" button stands under the game's own list (TableBoards.PauseEntry*); it opens a
    /// page in the same place: five tabs (Fights, Hands, Turning, Comfort, Voice), each row one setting with its choices as buttons
    /// (the one in effect lit) or - / + for a number. Every button is pressed with the fingertip through the shared plate
    /// press (PlateTouch: travel, click, release, assist, stop, ghost), like every other button.
    /// A change applies at once (every setting is read live) and is saved to the settings file right away - except in test
    /// runs, which share the player's settings file: there it holds for the run only. Back (or B) returns to the pause menu;
    /// closing the pause menu (the menu button) closes the page. While the page is open the game gets no stick or face
    /// buttons. Follows [Hands] MenuBoard (no board, no page).
    internal class VRSettings : MonoBehaviour
    {
        public static VRSettings I;
        public static float Ahead = 0.50f, Down = 0.19f, Raise = 0.006f, Mpp = 0.00042f;
        public static float PageW = 0.50f, PlateH = 0.034f, RowPitch = 0.044f, LabelW = 0.16f, Gap = 0.008f, Margin = 0.022f;
        const int MaxRows = 9;   // the page is as tall as its tab needs, at least 6 rows (Fights has 9: the headset switches, 2026-10-14)
        public static int Opens, Changes, EntryShows;
        public static string Last = "-";

        /// The pause menu board offers the page (the board is on and the player is in VR).
        public static bool Wanted => XRSettings.enabled && Settings.MenuBoard;
        public static bool PageOpen => I != null && I.m_mode == Mode.Page;
        /// The page has the controllers' buttons (and B after it closed the page, until it is let go).
        public static bool OwnsPad => I != null && (I.m_mode == Mode.Page || (I.m_swallow && (DevChord.B || VRInput.R.B || VRInput.R.A)));

        enum Mode { None, Entry, Page }
        enum Kind { Entry, Back, Tab, Choice, Minus, Plus, Run, DevCat, DevPick, DevRepeat, DevLeave }

        sealed class Row
        {
            public string Name, Section, Key;
            public string[] Values, Names;      // a choice: the values in the file and what the buttons say; null = a number
            public float Min, Max, Step;
            public Func<float, string> Show;
            public Func<string> Now;             // the value in effect when it isn't simply the file's (test overrides)
            public Action After;                 // after a change
            public string ActText;               // an action row: its one button (Do runs Act, Can says whether it may)
            public Action Act;
            public Func<bool> Can;
            public ConfigEntryBase Entry;
            public UILabel Name3D, Value3D;
            public TouchSlider Slider;          // a number's slider between its - and + (M1, 2026-10-17)
        }

        sealed class Plate : PressPlate
        {
            public Transform Back, Frame;
            public Mesh BackMesh, FrameMesh;
            public UILabel Label;
            public Kind K;
            public int Row = -1, Opt = -1;
            public bool Selected;
            public string Key = "";
            public Vector2 Local, Real;          // centre on the page and size (real metres)
            public Color FrameShown = Color.clear, BackShown = Color.clear, LabelShown = Color.clear;
        }

        // (the fight's looks on their own tab since 2026-10-14: the Fights tab got the headset switches for merge-ready #2's
        // new defaults - the coordinator: every new default's earlier answer switchable in the headset, not only in the file)
        // (an Enemies tab since 2026-10-16: the headset switches for merge-ready #4's new UYF behaviours - the Fights tab is full)
        static readonly string[] s_tabsBase = { "Fights", "Looks", "Enemies", "Hands", "Turning", "Comfort", "Voice" };
        static readonly string[] s_tabsDev = { "Fights", "Looks", "Enemies", "Hands", "Turning", "Comfort", "Voice", "Dev" };
        /// The page's tabs: "Dev" (the Scenarios menu, Diag/Scenarios.cs) only with [Dev] Scenarios on.
        static string[] s_tabs => Scenarios.Enabled ? s_tabsDev : s_tabsBase;
        bool DevTab => m_tab < s_tabs.Length && s_tabs[m_tab] == "Dev";
        string m_devCat = "Fights";
        bool m_wantDev;
        float m_wantDevUntil;
        Scenarios.Entry[] m_devEntries = new Scenarios.Entry[64];
        Row[][] m_rows;
        Mode m_mode = Mode.None;
        int m_tab;
        bool m_swallow, m_wantOpen, m_wantClose, m_relayout, m_wantCheck, m_underCheck;
        public static int CheckOpens, CheckReturns;
        string m_closeWhy = "";
        Transform m_root, m_bg;
        UILabel m_title, m_hint;
        UISprite m_fill, m_border, m_divider;
        readonly List<Plate> m_plates = new List<Plate>();
        readonly List<UILabel> m_rowLabels = new List<UILabel>();
        int m_count;
        Material m_backMat, m_frameMat;
        PlateTouch m_touch;
        Vector3 m_centre;
        Quaternion m_rot = Quaternion.identity;
        float m_nextRefresh, m_entryGrace;

        void Awake()
        {
            I = this;
            Define();
            m_touch = new PlateTouch("vrsettings", PressFamily.Board);
            m_touch.OnHover = (i, h) => true;
            m_touch.OnPress = PressAt;
            m_touch.Label = i => i < m_count ? m_plates[i].Key : "-";
            Diag.Commands["vrset"] = Command;
            // `vrslider <row 1-9> <value> [depth cm]`: the world point on that number row's slider for the value (tests), its row
            Diag.Commands["vrslider"] = a =>
            {
                int ri = a.Length > 1 ? int.Parse(a[1]) - 1 : 0;
                if (ri < 0 || ri >= MaxRows || m_sliders[ri] == null || m_mode != Mode.Page) return "no slider";
                float v = float.Parse(a[2], CultureInfo.InvariantCulture), d = a.Length > 3 ? float.Parse(a[3], CultureInfo.InvariantCulture) / 100f : 0.008f;
                Vector3 w = m_sliders[ri].PointFor(v, d);
                return string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####} {2:0.####} | {3} {4:0.###} drags {5} moves {6} writes {7} queues {8}", w.x, w.y, w.z,
                                     m_sliders[ri].Name, m_sliders[ri].Value, TouchSlider.Drags, TouchSlider.Moves, SliderWrites, m_sliders[ri].Queues);
            };
            Diag.StatusExtras["vrsettings"] = () => Diag.Q(Describe());
            // `sliderqueues old|new`: the number rows' sliders as before P4-4 (one queue for track, fill and knob) or now
            Diag.Commands["sliderqueues"] = a =>
            {
                bool on = !(a.Length > 1 && a[1] == "old");
                var sb = new System.Text.StringBuilder(on ? "layered:" : "old (one queue):");
                foreach (TouchSlider sl in m_sliders) if (sl != null) { sl.UseLayers(on); sb.Append(' ').Append(sl.Name).Append(' ').Append(sl.Queues); }
                return sb.ToString();
            };
        }

        // ---- the settings on the page -----------------------------------------------------------------------------------
        static string Num(float v, string fmt, string unit, string zero) =>
            zero != null && v <= 0.0001f ? zero : v.ToString(fmt, CultureInfo.InvariantCulture) + unit;

        static Row Choice(string name, string section, string key, string[] values, string[] names = null, Func<string> now = null, Action after = null) =>
            new Row { Name = name, Section = section, Key = key, Values = values, Names = names ?? values, Now = now, After = after };

        static Row Number(string name, string section, string key, float min, float max, float step, Func<float, string> show, Action after = null) =>
            new Row { Name = name, Section = section, Key = key, Min = min, Max = max, Step = step, Show = show, After = after };

        static Row ActionRow(string name, string text, Action act, Func<bool> can) => new Row { Name = name, ActText = text, Act = act, Can = can };

        void Define()
        {
            m_rows = new[]
            {
                new[]
                {
                    Choice("Combat", "Combat", "Mode", new[] { "Physical", "Classic" }, null, () => CombatSettings.Mode.ToString(), () => CombatSettings.OverrideMode(null)),
                    Choice("Difficulty", "Combat", "Difficulty", new[] { "Easy", "Normal", "Hard", "Nightmare1", "Nightmare2", "Nightmare3" },
                           new[] { "Easy", "Normal", "Hard", "NM 1", "NM 2", "NM 3" }, () => CombatSettings.Difficulty.ToString(), () => CombatSettings.OverrideDifficulty(null)),
                    // merge-ready #2's defaults with their earlier answers (UYF's way by default; each switchable in the headset):
                    // enemy toughness (1.0, was 0.25: next fight), the blow's lead (Hold, was Slow: the next attack), the dash
                    // (UYF's 6 m, was Short's 2 m) and its right-stick flick (at once)
                    Number("Enemy toughness", "Combat", "EnemyHealth", 0.05f, 1f, 0.05f, v => Num(v * 100f, "0", " %", null)),
                    Choice("Lead", "Combat", "Lead", new[] { "Hold", "Slow" }),
                    Choice("Dash", "Combat", "Dash", new[] { "UYF", "Short" }),
                    Choice("Dash flick", "Combat", "DashFlick", new[] { "false", "true" }, new[] { "Off", "On" }),
                    Number("Walk speed", "Combat", "WalkSpeed", 1f, 3.4f, 0.2f, v => Num(v, "0.0", " m/s", null)),
                    // the combat markers' look (IndicatorLooks; the user flips between them mid-fight: new markers take it at once)
                    Choice("Marker look", "Combat", "IndicatorLook", new[] { "Pill", "Arc", "Ghost", "Gilded", "Ring", "Bracket" }, null,
                           () => CombatSettings.Look.ToString(), () => CombatSettings.OverrideLook(null)),
                    Choice("Marker colour", "Combat", "IndicatorByType", new[] { "false", "true" }, new[] { "One colour", "By blow" },
                           () => CombatSettings.IndicatorByType ? "true" : "false", () => CombatSettings.OverrideByType(null)),
                },
                new[]
                {
                    // the user's pick of 2026-10-18 ~22:00 (both as options): your info on a board in the arena or on your body; the
                    // enemy's guard and health as a shell on its body or two rings at its feet (both switch at once, mid-fight too)
                    Choice("Your info", "Combat", "FightHud", new[] { "Board", "Body" }, new[] { "Board", "On you" }, () => CombatSettings.FightHud.ToString(), () => CombatSettings.OverrideFightHud(null)),
                    Choice("Enemy guard", "Combat", "EnemyStatus", new[] { "Shell", "Rings" }, null, () => CombatSettings.EnemyStatus.ToString(), () => CombatSettings.OverrideEnemyStatus(null)),
                    Choice("Health shown", "Combat", "EnemyHealthBars", new[] { "On", "Engaged", "Off" }, null, () => CombatSettings.Meters.ToString()),
                    Choice("Dash look", "Combat", "DashStyle", new[] { "Slide", "Blink" }),
                    Choice("Difficulty flash", "Combat", "DifficultyFlash", new[] { "false", "true" }, new[] { "Off", "On" }),
                },
                new[]
                {
                    // merge-ready #4's UYF behaviours, each with its off switch (2026-10-16; read live: the next attack, the
                    // next pause, the next boss's death)
                    Choice("Shoves", "Combat", "Shoves", new[] { "false", "true" }, new[] { "Off", "On" }),
                    Choice("Varied pauses", "Combat", "VariedPauses", new[] { "false", "true" }, new[] { "Off", "On" }),
                    Choice("Boss slow motion", "Combat", "BossSlowMotion", new[] { "false", "true" }, new[] { "Off", "On" }),
                },
                new[]
                {
                    Choice("Touch help", "Hands", "Preset", new[] { "Off", "Subtle", "Normal", "Strong" }),
                    Choice("Ghost hand", "Hands", "GhostHand", new[] { "Off", "Faint", "Clear" }),
                    Number("Hand sounds", "Hands", "Sounds", 0f, 1f, 0.1f, v => Num(v, "0.0", "", "Off")),
                    Number("Touch buzz", "Hands", "HapticStrength", 0f, 2f, 0.25f, v => Num(v, "0.##", "x", "Off")),
                    // (playtest 4, P4-3, the user: "Is there no option here to change the orientations of my hands?" - the glove fit
                    // was only on the hand check, which only the jump card opened; with Scenarios on, B + Y opens the Dev tab
                    // instead, so at the map there was no way to it) the hand check's fit page, over the pause menu
                    ActionRow("Glove fit", "Calibrate hands", () => m_wantCheck = true, CheckAllowed),
                },
                new[]
                {
                    Choice("Turning", "Comfort", "SnapTurn", new[] { "false", "true" }, new[] { "Smooth", "Snap" }),
                    Number("Snap angle", "Comfort", "SnapTurnAngle", 15f, 90f, 15f, v => Num(v, "0", " deg", null)),
                    Choice("Snap blink", "Comfort", "SnapTurnBlink", new[] { "false", "true" }, new[] { "Off", "On" }),
                    Number("Turn speed", "Comfort", "SmoothTurnSpeed", 60f, 240f, 30f, v => Num(v, "0", " deg/s", null)),
                },
                new[]
                {
                    // Standing / Seated (2026-10-13; VRRig's posture): applies at once, in a short dark; the seated table's height
                    Choice("Posture", "Comfort", "Seated", new[] { "false", "true" }, new[] { "Standing", "Seated" }, null, () => Places.PostureChanged("VR settings")),
                    Number("Seated table", "Comfort", "SeatedTableHeight", Settings.SeatedTableMin, Settings.SeatedTableMax, 0.025f, v => Num(v * 100f, "0.#", " cm", null),
                           () => Places.PostureChanged("VR settings: the seated table's height")),
                    // seated at the tables: a real table's height (the default) or Until You Fall's lifted view there too (2026-10-16)
                    Choice("At the table", "Comfort", "SeatedTableView", new[] { "Table", "Lifted" }, new[] { "Real table", "Lifted" }, null,
                           () => Places.PostureChanged("VR settings: the seated view at the tables")),
                    Number("Turn vignette", "Comfort", "TurnVignette", 0f, 1f, 0.1f, v => Num(v, "0.0", "", "Off")),
                    Number("Walk vignette", "Comfort", "WalkVignette", 0f, 1f, 0.1f, v => Num(v, "0.0", "", "Off")),
                    Number("Dash vignette", "Comfort", "Vignette", 0f, 1f, 0.1f, v => Num(v, "0.0", "", "Off")),
                    Choice("Waiting board", "Comfort", "WaitingBoardStyle", new[] { "Card", "Gilded", "Banner" }),
                    Choice("Exit portal", "Comfort", "PortalLook", new[] { "Orb", "Swirl", "Rift", "Halo" }),
                    Choice("Cards", "Table", "CardLook", new[] { "Game", "Bright", "Lit", "Marked", "Strong" }),
                },
                new[]
                {
                    // the narration (Narration/Narrator.cs; docs/narration-design.md)
                    Choice("Narration", "Narration", "Enabled", new[] { "false", "true" }, new[] { "Off", "On" }),
                    Choice("Pace", "Narration", "Pace", new[] { "Slow", "Normal", "Fast" }),
                    Number("Volume", "Narration", "Volume", 0f, 1f, 0.1f, v => Num(v * 100f, "0", " %", "Off")),
                    Choice("His own lines", "Narration", "OriginalDealerLines", new[] { "Replace", "Mute", "Keep" }, new[] { "Re-voice", "Mute", "Keep" }),
                    Number("Head motion", "Narration", "HeadMotion", 0f, 1.5f, 0.25f, v => Num(v, "0.##", "x", "Off")),
                    // 3D sound through a real HRTF (VR/Hrtf.cs; docs/hrtf-plan.md): A/B it by touch while he talks
                    Choice("3D sound (HRTF)", "Audio", "Hrtf", new[] { "false", "true" }, new[] { "Off", "On" }),
                },
            };
        }

        static ConfigEntryBase EntryOf(Row r)
        {
            if (r.Entry != null) return r.Entry;
            ConfigFile cfg = Settings.Cfg;
            if (cfg == null) return null;
            foreach (ConfigDefinition d in cfg.Keys)
                if (d.Section == r.Section && d.Key == r.Key) { r.Entry = cfg[d]; break; }
            return r.Entry;
        }

        static string Current(Row r)
        {
            if (r.Now != null) return r.Now();
            ConfigEntryBase e = EntryOf(r);
            return e != null ? e.GetSerializedValue() : "";
        }

        static float CurrentNumber(Row r)
        {
            float v;
            return float.TryParse(Current(r), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : r.Min;
        }

        // ---- the number rows' sliders (M1) ----------------------------------------------------------------------------
        readonly TouchSlider[] m_sliders = new TouchSlider[MaxRows];
        public static int SliderWrites;

        TouchSlider SliderFor(int r)
        {
            if (r < 0 || r >= MaxRows || m_root == null) return null;
            if (m_sliders[r] == null) m_sliders[r] = new TouchSlider(m_root, "row" + (r + 1), VRUI.Layer, m_backMat);
            return m_sliders[r];
        }

        /// A slider's value, live (not saved while the finger drags: one write when it lets go).
        void WriteLive(Row r, float v)
        {
            ConfigFile cfg = Settings.Cfg;
            if (cfg == null) return;
            bool save = cfg.SaveOnConfigSet;
            cfg.SaveOnConfigSet = false;
            try { Write(r, v.ToString("0.###", CultureInfo.InvariantCulture)); }
            finally { cfg.SaveOnConfigSet = save; }
            SliderWrites++;
            if (r.Value3D != null) r.Value3D.text = r.Show(v);
        }

        void SaveAfterDrag(Row r)
        {
            ConfigFile cfg = Settings.Cfg;
            if (cfg == null || TestMode.Enabled) return;   // (test runs never write the player's file)
            if (cfg.SaveOnConfigSet) cfg.Save();
            Plugin.L.LogInfo("vr settings: [" + r.Section + "] " + r.Key + " set by its slider (saved)");
        }

        /// Sets a setting: live at once (every setting is read where it is used); saved to the file, except in test runs.
        static bool Write(Row r, string value)
        {
            ConfigEntryBase e = EntryOf(r);
            ConfigFile cfg = Settings.Cfg;
            if (e == null || cfg == null) return false;
            bool save = cfg.SaveOnConfigSet;
            if (TestMode.Enabled) cfg.SaveOnConfigSet = false;
            try { e.SetSerializedValue(value); }
            finally { cfg.SaveOnConfigSet = save; }
            r.After?.Invoke();
            Changes++;
            Last = r.Key + "=" + e.GetSerializedValue();
            Plugin.L.LogInfo("vr settings: [" + r.Section + "] " + Last + (TestMode.Enabled ? " (test run: for this run only)" : " (saved)"));
            return true;
        }

        // ---- per frame --------------------------------------------------------------------------------------------------
        void LateUpdate()
        {
            try { Tick(); }
            catch (Exception e) { Plugin.L.LogError("vr settings: " + e); Switch(Mode.None, "error"); }
        }

        static bool DialogueOnTop()
        {
            try
            {
                MenuManager mm = MenuManager.Instance;
                Menu d = mm != null ? mm.CurrentMenu(MenuManager.StackPriority.Dialogue) : null;
                return d != null && d.isActiveAndEnabled;
            }
            catch (Exception) { return false; }
        }

        void Tick()
        {
            if (m_swallow && !VRInput.R.B && !VRInput.R.A) m_swallow = false;
            // (the player's own Standing / Seated darkens the view for a moment: the page stays)
            bool pause = Wanted && VRRig.I != null && VRUI.I != null && (!VRFade.IsBlack || Places.PostureBusy) && !Limbo.On && TableBoards.PauseMenuOpen() && !DialogueOnTop() &&
                         MenuManager.Instance != null && MenuManager.Instance.Paused;
            // (back from the page the board comes back a frame later: the button stays meanwhile)
            bool entry = TableBoards.PauseEntryOn || (m_mode == Mode.Entry && Time.unscaledTime < m_entryGrace);
            Mode want = m_mode == Mode.Page ? (pause ? Mode.Page : Mode.None) : pause && entry ? Mode.Entry : Mode.None;
            if (m_mode == Mode.Page && want == Mode.None) m_closeWhy = "the pause menu closed";
            DevChord.Tick();
            bool check = HandCheck.I != null && HandCheck.I.Active;
            if (m_mode == Mode.Page && pause && DevChord.BDown && !check) { want = Mode.Entry; m_swallow = true; m_closeWhy = "B"; }
            if (m_wantDev && want == Mode.Entry) { want = Mode.Page; m_wantDev = false; m_closeWhy = "B + Y"; }
            if (m_wantDev && Time.unscaledTime > m_wantDevUntil) m_wantDev = false;
            if (want != m_mode) Switch(want, m_closeWhy);
            m_closeWhy = "";
            // the hand check opened from the Hands tab (P4-3): the page waits hidden behind it (the pause menu's board too:
            // TableBoards) and comes back where it was when the check closes (its Back, Next ... Close, or a place change)
            if (m_mode == Mode.Page && check) { HideForCheck(); return; }
            if (m_underCheck)
            {
                m_underCheck = false;
                if (m_mode != Mode.None && m_root != null)
                {
                    CheckReturns++;
                    m_root.gameObject.SetActive(true);
                    m_relayout = true;
                    Plugin.L.LogInfo("vr settings: back from the hand check (tab " + s_tabs[m_tab] + "; glove fit " + Settings.GloveFitText() + ")");
                }
            }
            if (m_mode == Mode.None) return;
            if (!Ensure()) return;
            if (m_relayout) { m_relayout = false; Layout(); }
            Place();
            if (Time.unscaledTime >= m_nextRefresh) Refresh();
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 1e-3f, 0.1f);
            PlateTouch.Settle(At, m_count, dt);
            float sc = VRRig.I.Scale;
            for (int i = 0; i < m_count; i++)
            {
                Plate p = m_plates[i];
                p.T.localPosition = new Vector3(p.Local.x, p.Local.y, -Raise + p.Press / sc);
            }
            // the sliders first: a fingertip dragging one presses no plate meanwhile
            bool dragL = false, dragR = false;
            foreach (TouchSlider sl in m_sliders)
                if (sl != null && sl.Tick()) { if (sl.DraggedBy(true)) dragL = true; else dragR = true; }
            foreach (Hand h in VRInput.RL)
            {
                bool ok = XRSettings.enabled && (m_touch.Overridden(h) || (h.Tracked && !h.TriggerBtn && (!h.GripBtn || VRPad.PointingGrip(h)) && !VRFade.IsBlack));
                if (h.IsLeft ? dragL : dragR) ok = false;
                m_touch.Step(h, At, m_count, ok);
            }
            Look();
            // what the presses asked for (after the press loop: the plates change)
            if (m_wantOpen) { m_wantOpen = false; Switch(Mode.Page, "opened"); }
            else if (m_wantClose) { m_wantClose = false; Switch(Mode.Entry, "Back"); }
            else if (m_wantCheck) { m_wantCheck = false; OpenHandCheck(); }
        }

        /// The hand check may open over the page: outside fights (it closes itself in one) and not while it is already open.
        static bool CheckAllowed() => HandCheck.I != null && !HandCheck.I.Active && !CombatVR.Active && !GameWatch.InCombatPlay;

        /// "Calibrate hands": the hand check's first page (the glove fit: tilt, turn, roll, up, forward, side, Reset, the fingertip
        /// guide; both grips + the right stick too), in front of the player; its Back saves the fit and brings this page back.
        void OpenHandCheck()
        {
            if (m_mode != Mode.Page || !CheckAllowed()) return;
            CheckOpens++;
            Plugin.L.LogInfo("vr settings: Calibrate hands - the hand check's glove fit opens over the pause menu (fit now " + Settings.GloveFitText() + ")");
            HandCheck.I.Begin();
            if (HandCheck.I.Active) HideForCheck();
        }

        void HideForCheck()
        {
            if (m_underCheck) return;
            m_underCheck = true;
            foreach (Hand h in VRInput.RL) m_touch.Step(h, At, 0, false);   // the fingers let go of the page's plates
            if (m_root != null) m_root.gameObject.SetActive(false);
        }

        PressPlate At(int i) => i < m_count ? m_plates[i] : null;

        void Switch(Mode to, string why)
        {
            // the fingers let go of the old plates first (their numbers change)
            foreach (Hand h in VRInput.RL) m_touch.Step(h, At, 0, false);
            Mode from = m_mode;
            m_mode = to;
            if (to == Mode.Page && from != Mode.Page)
            {
                Opens++;
                m_tab = Mathf.Clamp(m_tab, 0, s_tabs.Length - 1);
                Plugin.L.LogInfo("vr settings: page open (tab " + s_tabs[m_tab] + ")" + (TestMode.Enabled ? " - test run: changes hold for this run only" : ""));
            }
            else if (from == Mode.Page) Plugin.L.LogInfo("vr settings: page closed (" + why + ")");
            if (to == Mode.Entry && from == Mode.None) EntryShows++;
            if (to == Mode.Entry && from == Mode.Page) m_entryGrace = Time.unscaledTime + 0.3f;
            if (m_root == null) return;
            m_root.gameObject.SetActive(to != Mode.None && !m_underCheck);
            if (to != Mode.None) { Layout(); Place(); Refresh(); }
        }

        // ---- building -------------------------------------------------------------------------------------------------
        bool Ensure()
        {
            if (m_root != null) return true;
            if (!GameText.Ready || !GameText.AtlasReady || VRUI.I == null) return false;
            var go = new GameObject("HoF2VR_VRSettings") { layer = VRUI.Layer };
            DontDestroyOnLoad(go);
            m_root = go.transform;
            GameText.Panel(go, 5108);   // the buttons' labels, after their backings and frames
            Shader sh = Shader.Find("Sprites/Default");
            m_backMat = new Material(sh) { name = "HoF2VR_SettingsPlateBack", renderQueue = 5106 };
            m_frameMat = new Material(sh) { name = "HoF2VR_SettingsPlateFrame", renderQueue = 5107 };
            // the page: the game's dark card with its gold rim, a gilded title and its double divider, the rows' names
            var bg = new GameObject("page") { layer = VRUI.Layer };
            bg.transform.SetParent(m_root, false);
            bg.transform.localPosition = new Vector3(0f, 0f, 0.002f);
            bg.transform.localScale = Vector3.one * Mpp;
            m_bg = bg.transform;
            GameText.Panel(bg, 5101);
            UIAtlas at = GameText.Atlas;
            m_fill = GameText.Sprite(m_bg, "fill", at, "gui_black", new Color(0.035f, 0.028f, 0.02f, 0.93f), -1, 200, 200, UIBasicSprite.Type.Simple);
            m_fill.transform.localPosition = new Vector3(0f, 0f, 5f);
            m_border = GameText.Sprite(m_bg, "border", at, "gui_border", new Color(0.93f, 0.76f, 0.47f, 0.97f), 0, 200, 200);
            m_border.transform.localPosition = new Vector3(0f, 0f, 4f);
            m_divider = GameText.Sprite(m_bg, "divider", at, "gui_divider_double", new Color(0.86f, 0.67f, 0.36f, 1f), 2, 300, 19);
            m_title = GameText.Label(m_bg, "title", GameText.Stylised, 42, Color.white, 4, UIWidget.Pivot.Left, NGUIText.Alignment.Left);
            GameText.Gild(m_title, 1.2f);
            m_title.text = "VR Settings";
            m_hint = GameText.Label(m_bg, "hint", GameText.Normal, 21, new Color(0.86f, 0.8f, 0.68f, 0.95f), 4);
            for (int i = 0; i < MaxRows; i++)
            {
                UILabel l = GameText.Label(m_bg, "row" + (i + 1), GameText.Normal, 27, new Color(1f, 0.85f, 0.55f, 1f), 4, UIWidget.Pivot.Left, NGUIText.Alignment.Left);
                m_rowLabels.Add(l);
            }
            for (int i = 0; i < MaxRows; i++)
                m_rowLabels.Add(GameText.Label(m_bg, "value" + (i + 1), GameText.Normal, 28, GameText.Parchment, 4));
            go.SetActive(m_mode != Mode.None);
            if (m_mode != Mode.None) { Layout(); Place(); Refresh(); }
            return true;
        }

        Plate PlateAt(int i)
        {
            while (m_plates.Count <= i)
            {
                var p = new Plate();
                var go = new GameObject("plate" + (m_plates.Count + 1)) { layer = VRUI.Layer };
                go.transform.SetParent(m_root, false);
                p.T = go.transform;
                p.BackMesh = QuadMesh();
                p.Back = MeshObject("back", p.T, p.BackMesh, m_backMat);
                p.FrameMesh = new Mesh { name = "HoF2VR_SettingsFrame" };
                p.Frame = MeshObject("frame", p.T, p.FrameMesh, m_frameMat);
                var lh = new GameObject("label") { layer = VRUI.Layer };
                lh.transform.SetParent(p.T, false);
                lh.transform.localPosition = new Vector3(0f, 0f, -0.0008f);
                lh.transform.localScale = Vector3.one * Mpp;
                p.Label = GameText.Label(lh.transform, "text", GameText.Normal, 26, GameText.Parchment, 10);
                m_plates.Add(p);
            }
            return m_plates[i];
        }

        static Mesh QuadMesh()
        {
            var m = new Mesh { name = "HoF2VR_SettingsQuad" };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            m.triangles = new[] { 0, 1, 2, 1, 0, 3 };
            m.colors = new[] { Color.black, Color.black, Color.black, Color.black };
            return m;
        }

        static Transform MeshObject(string name, Transform parent, Mesh mesh, Material mat)
        {
            var go = new GameObject(name) { layer = VRUI.Layer };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        static void SetFrame(Plate p, float w, float h, float bw)
        {
            float ox = w * 0.5f + bw, oy = h * 0.5f + bw, ix = w * 0.5f, iy = h * 0.5f;
            p.FrameMesh.vertices = new[]
            {
                new Vector3(-ox, -oy, -0.0004f), new Vector3(ox, -oy, -0.0004f), new Vector3(ox, oy, -0.0004f), new Vector3(-ox, oy, -0.0004f),
                new Vector3(-ix, -iy, -0.0004f), new Vector3(ix, -iy, -0.0004f), new Vector3(ix, iy, -0.0004f), new Vector3(-ix, iy, -0.0004f),
            };
            if (p.FrameMesh.triangles.Length == 0)
            {
                p.FrameMesh.triangles = new[] { 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7 };
                p.FrameShown = Color.clear;
            }
            p.FrameMesh.RecalculateBounds();
        }

        /// One plate: what it is, what it says, where it sits on the page and its size (real metres).
        Plate Put(int i, Kind k, string text, string key, float x, float y, float w, float h, int row = -1, int opt = -1)
        {
            Plate p = PlateAt(i);
            if (p.Key != key || !p.On)
            {
                // a new button here: no travel, glow or hover carried over from the one before
                p.Hovered = p.Selected = false;
                p.Press = p.PressV = p.Glow = 0f;
            }
            p.K = k;
            p.Key = key;
            p.Row = row;
            p.Opt = opt;
            p.Local = new Vector2(x, y);
            p.Real = new Vector2(w, h);
            p.On = true;
            p.Disabled = false;
            p.T.gameObject.SetActive(true);
            p.T.localRotation = Quaternion.identity;
            p.Back.localScale = new Vector3(w, h, 1f);
            SetFrame(p, w, h, 0.0028f);
            p.Label.text = text;
            p.Label.fontSize = k == Kind.Minus || k == Kind.Plus ? 34 : 26;
            // the label fits the plate: shrunk if it is too long (or the plate low)
            Vector2 sz = p.Label.printedSize;
            float fit = 1f;
            if (sz.x > 1f) fit = Mathf.Min(fit, (w - 0.008f) / (sz.x * Mpp));
            if (sz.y > 1f) fit = Mathf.Min(fit, h * 0.78f / (sz.y * Mpp));
            p.Label.transform.parent.localScale = Vector3.one * Mpp * Mathf.Clamp(fit, 0.4f, 1f);
            p.FrameShown = p.BackShown = p.LabelShown = Color.clear;
            return p;
        }

        float PageH => DevTab ? m_devH : 0.014f + 0.046f + 0.012f + PlateH + 0.016f + RowsOnTab * RowPitch + 0.026f + 0.010f;
        int RowsOnTab => m_rows != null && m_tab >= 0 && m_tab < m_rows.Length ? Mathf.Clamp(m_rows[m_tab].Length, 6, MaxRows) : 6;
        float PageWNow => DevTab ? DevPageW : PageW;
        public static float DevPageW = 0.62f, DevDown = 0.12f, DevPitch = 0.042f;
        const int DevCols = 6;
        float m_devH = 0.45f;

        /// The plates of the mode (and the page's tab).
        void Layout()
        {
            foreach (TouchSlider sl in m_sliders) if (sl != null) sl.SetActive(false);   // (the number rows show theirs again)
            if (m_root == null) return;
            int n = 0;
            for (int i = 0; i < m_rowLabels.Count; i++) m_rowLabels[i].gameObject.SetActive(false);
            if (m_mode == Mode.Entry)
            {
                m_bg.gameObject.SetActive(false);
                float sc = Mathf.Max(0.01f, VRRig.I != null ? VRRig.I.Scale : 1f);
                Vector2 s = TableBoards.PauseEntrySize / sc;
                Put(n++, Kind.Entry, "VR Settings", "VR Settings", 0f, 0f, Mathf.Max(0.08f, s.x), Mathf.Max(0.02f, s.y));
            }
            else if (m_mode == Mode.Page)
            {
                m_bg.gameObject.SetActive(true);
                if (DevTab) m_devH = DevHeight();
                float W = PageWNow, H = PageH, top = H * 0.5f, left = -W * 0.5f + Margin;
                m_fill.width = m_border.width = Mathf.RoundToInt(W / Mpp);
                m_fill.height = m_border.height = Mathf.RoundToInt(H / Mpp);
                float titleY = top - 0.014f - 0.021f;
                m_title.transform.localPosition = new Vector3(left / Mpp, titleY / Mpp, 0f);
                m_divider.width = Mathf.RoundToInt((W - 2f * Margin - 0.13f) / Mpp);
                m_divider.transform.localPosition = new Vector3((left + (W - 2f * Margin - 0.13f) * 0.5f) / Mpp, (top - 0.014f - 0.046f + 0.004f) / Mpp, 0f);
                // Back, at the right of the title
                Put(n++, Kind.Back, "Back", "Back", W * 0.5f - Margin - 0.05f, titleY, 0.10f, PlateH);
                // the tabs
                float tabY = top - 0.014f - 0.046f - 0.012f - PlateH * 0.5f;
                float tabW = (W - 2f * Margin - (s_tabs.Length - 1) * Gap) / s_tabs.Length;
                for (int t = 0; t < s_tabs.Length; t++)
                    Put(n++, Kind.Tab, s_tabs[t], s_tabs[t], left + tabW * 0.5f + t * (tabW + Gap), tabY, tabW, PlateH, -1, t);
                if (DevTab)
                {
                    n = LayoutDev(n, W, H, left, tabY);
                    m_count = n;
                    for (int i = n; i < m_plates.Count; i++) { m_plates[i].On = false; m_plates[i].Hovered = false; m_plates[i].T.gameObject.SetActive(false); }
                    m_nextRefresh = 0f;
                    return;
                }
                // the rows: the name on the left, the choices (or - value +) on the right
                Row[] rows = m_rows[m_tab];
                float x0 = left + LabelW, area = W * 0.5f - Margin - x0;
                for (int r = 0; r < rows.Length && r < MaxRows; r++)
                {
                    Row row = rows[r];
                    float y = tabY - PlateH * 0.5f - 0.016f - RowPitch * 0.5f - r * RowPitch;
                    UILabel name = m_rowLabels[r];
                    name.gameObject.SetActive(true);
                    name.text = row.Name;
                    name.transform.localPosition = new Vector3(left / Mpp, y / Mpp, 0f);
                    row.Name3D = name;
                    row.Value3D = null;
                    if (row.Act != null)
                    {
                        float w = Mathf.Min(0.20f, area);
                        Put(n++, Kind.Run, row.ActText, row.Name + "/" + row.ActText, x0 + w * 0.5f, y, w, PlateH, r);
                    }
                    else if (row.Values != null)
                    {
                        int c = row.Values.Length;
                        float w = Mathf.Min(0.105f, (area - (c - 1) * Gap) / c);
                        for (int o = 0; o < c; o++)
                            Put(n++, Kind.Choice, row.Names[o], row.Name + "/" + row.Names[o], x0 + w * 0.5f + o * (w + Gap), y, w, PlateH, r, o);
                    }
                    else
                    {
                        // - , a slider with the value over it, + (M1, the user 2026-10-17: "it's impossible to do this without
                        // sliders"; - and + stay for fine steps)
                        const float bw = 0.05f;
                        float vw = Mathf.Max(0.11f, area - 2f * bw - 2f * Gap);
                        Put(n++, Kind.Minus, "-", row.Name + "/-", x0 + bw * 0.5f, y, bw, PlateH, r);
                        float sx = x0 + bw + Gap + vw * 0.5f;
                        UILabel v = m_rowLabels[MaxRows + r];
                        v.gameObject.SetActive(true);
                        v.fontSize = 22;
                        v.transform.localPosition = new Vector3(sx / Mpp, (y + 0.0095f) / Mpp, 0f);
                        row.Value3D = v;
                        TouchSlider sl = SliderFor(r);
                        if (sl != null)
                        {
                            sl.Min = row.Min; sl.Max = row.Max; sl.Step = row.Step; sl.Name = row.Name;
                            sl.Length = vw - 0.016f; sl.Height = 0.020f;
                            Row target = row;
                            sl.Changed = val => WriteLive(target, val);
                            sl.Released = () => SaveAfterDrag(target);
                            sl.SetActive(true);
                            sl.Layout(new Vector3(sx, y - 0.0065f, -Raise), 1f);
                            sl.Set(CurrentNumber(row));
                            row.Slider = sl;
                        }
                        Put(n++, Kind.Plus, "+", row.Name + "/+", x0 + bw + Gap + vw + Gap + bw * 0.5f, y, bw, PlateH, r);
                    }
                }
                m_hint.text = TestMode.Enabled ? "Test run: changes hold for this run only (the settings file is not written)."
                                               : "Changes apply at once and are saved.   B or Back: the pause menu.";
                m_hint.transform.localPosition = new Vector3(0f, (-H * 0.5f + 0.010f + 0.013f) / Mpp, 0f);
            }
            m_count = n;
            for (int i = n; i < m_plates.Count; i++) { m_plates[i].On = false; m_plates[i].Hovered = false; m_plates[i].T.gameObject.SetActive(false); }
            m_nextRefresh = 0f;
        }

        // ---- the Dev tab: the Scenarios menu (Diag/Scenarios.cs) --------------------------------------------------------
        static string[] DevCats => Scenarios.Tabs.Where(t => t != "Weapon").ToArray();

        int DevRows(int count) => (count + DevCols - 1) / DevCols;

        float DevHeight()
        {
            float h = 0.014f + 0.046f + 0.012f + PlateH + 0.012f + PlateH + 0.012f;
            if (m_devCat == "Fights") h += 0.020f + DevRows(Scenarios.InTab("Weapon").Count()) * DevPitch + 0.006f + 0.020f;
            h += DevRows(Scenarios.InTab(m_devCat).Count()) * DevPitch;
            return h + 0.010f + PlateH + 0.034f + 0.010f;
        }

        /// The categories, then the scenarios of the chosen one as buttons (the Fights: the weapons first, the one in use lit),
        /// then Repeat and Leave; the hint line says what the runner is doing.
        int LayoutDev(int n, float W, float H, float left, float tabY)
        {
            string[] cats = DevCats;
            if (Array.IndexOf(cats, m_devCat) < 0 && cats.Length > 0) m_devCat = cats[0];
            float y = tabY - PlateH - 0.012f;
            float cw = (W - 2f * Margin - (cats.Length - 1) * Gap) / Math.Max(1, cats.Length);
            for (int c = 0; c < cats.Length; c++)
                Put(n++, Kind.DevCat, cats[c], "Dev/" + cats[c], left + cw * 0.5f + c * (cw + Gap), y, cw, PlateH, -1, c);
            y -= PlateH * 0.5f + 0.012f;
            float pw = (W - 2f * Margin - (DevCols - 1) * Gap) / DevCols;
            int labels = 0;
            Action<string, float> head = (text, yy) =>
            {
                UILabel l = m_rowLabels[labels++];
                l.gameObject.SetActive(true);
                l.text = text;
                l.transform.localPosition = new Vector3(left / Mpp, yy / Mpp, 0f);
            };
            Func<IEnumerable<Scenarios.Entry>, int> grid = list =>
            {
                int i = 0;
                foreach (Scenarios.Entry e in list)
                {
                    int r = i / DevCols, c = i % DevCols;
                    float yy = y - DevPitch * 0.5f - r * DevPitch;
                    if (n < m_devEntries.Length) m_devEntries[n] = e;
                    Put(n++, Kind.DevPick, e.Label, e.Tab + "/" + e.Label, left + pw * 0.5f + c * (pw + Gap), yy, pw, PlateH, -1, e.Index);
                    i++;
                }
                return DevRows(i);
            };
            if (m_devCat == "Fights")
            {
                head("Weapon", y - 0.010f);
                y -= 0.020f;
                y -= grid(Scenarios.InTab("Weapon")) * DevPitch + 0.006f;
                head("Then the enemies", y - 0.010f);
                y -= 0.020f;
            }
            y -= grid(Scenarios.InTab(m_devCat)) * DevPitch;
            // Repeat (the last scenario) and Leave (back to your own save, at the title)
            float by = y - 0.010f - PlateH * 0.5f;
            string last = Scenarios.Last != null ? Scenarios.Last.Label : "-";
            Put(n++, Kind.DevRepeat, "Repeat: " + last, "Dev/Repeat", left + 0.15f, by, 0.30f, PlateH);
            Put(n++, Kind.DevLeave, "Leave scenarios", "Dev/Leave", W * 0.5f - Margin - 0.085f, by, 0.17f, PlateH);
            m_hint.transform.localPosition = new Vector3(0f, (-H * 0.5f + 0.010f + 0.013f) / Mpp, 0f);
            m_hint.text = DevHint();
            return n;
        }

        static string DevHint()
        {
            string where = DevProfile.Active ? "In the dev save: your own save is not touched." : "Your own run: a scenario first switches to the dev save (yours stays as it is).";
            return Scenarios.Busy ? Scenarios.Status : where;
        }

        /// The B + Y long hold (DevChord) with [Dev] Scenarios: the pause menu opens with this page on the Dev tab.
        public static bool RequestDev()
        {
            if (I == null || !Scenarios.Enabled || !Wanted) return false;
            // no pause menu at the title or in the waiting space: the jump card at once
            string st = GameWatch.State;
            if (st == "GameState_StartScreen" || st == "" || st == "GameState_Init" || Limbo.On || WaitGate.BlocksPad || VRFade.IsBlack) return false;
            int t = Array.IndexOf(s_tabs, "Dev");
            if (t < 0) return false;
            I.m_tab = t;
            I.m_wantDev = true;
            I.m_wantDevUntil = Time.unscaledTime + 2.5f;
            if (I.m_mode == Mode.Page) { I.m_relayout = true; I.m_wantDev = false; return true; }
            if (MenuManager.Instance == null || !MenuManager.Instance.Paused) VRPad.Press("START", 6);
            return true;
        }

        public static bool DevWaiting => I != null && I.m_wantDev && Time.unscaledTime < I.m_wantDevUntil;

        /// The page stands where the pause menu's board stood (where the player looked when the menu opened), a little lower;
        /// the button way in sits on that board.
        void Place()
        {
            VRRig rig = VRRig.I;
            if (rig == null || m_root == null) return;
            float sc = rig.Scale;
            if (m_mode == Mode.Entry)
            {
                m_centre = TableBoards.PauseEntryCentre;
                m_rot = TableBoards.PauseEntryRot;
            }
            else if (m_mode == Mode.Page)
            {
                Vector3 eye;
                Quaternion yaw;
                if (!TableBoards.MenuFrame(out eye, out yaw)) { eye = rig.HeadWorldPos; yaw = Quaternion.Euler(0f, rig.HeadWorldRot.eulerAngles.y, 0f); }
                m_centre = eye + yaw * (new Vector3(0f, -(DevTab ? DevDown : Down), Ahead) * sc);
                m_rot = Quaternion.LookRotation(m_centre - eye, Vector3.up);
            }
            m_root.SetPositionAndRotation(m_centre, m_rot);
            m_root.localScale = Vector3.one * sc;
            for (int i = 0; i < m_count; i++) m_plates[i].Size = m_plates[i].Real * sc;
        }

        /// What is in effect now: the lit choices, the numbers, the ends of the ranges.
        void Refresh()
        {
            m_nextRefresh = Time.unscaledTime + 0.2f;
            if (m_mode != Mode.Page) return;
            if (DevTab)
            {
                for (int i = 0; i < m_count; i++)
                {
                    Plate p = m_plates[i];
                    switch (p.K)
                    {
                        case Kind.Tab: p.Selected = p.Opt == m_tab; break;
                        case Kind.DevCat: p.Selected = p.Key == "Dev/" + m_devCat; break;
                        case Kind.DevPick: p.Selected = Scenarios.Loadout != null && p.Opt == Scenarios.Loadout.Index || Scenarios.Running != null && p.Opt == Scenarios.Running.Index; break;
                        case Kind.DevRepeat: p.Disabled = Scenarios.Last == null || Scenarios.Busy; break;
                        case Kind.DevLeave: p.Disabled = DevProfile.Mode == DevProfile.M.Real; break;
                        default: p.Selected = false; break;
                    }
                }
                m_hint.text = DevHint();
                return;
            }
            Row[] rows = m_rows[m_tab];
            for (int i = 0; i < m_count; i++)
            {
                Plate p = m_plates[i];
                switch (p.K)
                {
                    case Kind.Tab: p.Selected = p.Opt == m_tab; break;
                    case Kind.Choice: p.Selected = string.Equals(Current(rows[p.Row]), rows[p.Row].Values[p.Opt], StringComparison.OrdinalIgnoreCase); break;
                    case Kind.Minus: p.Disabled = CurrentNumber(rows[p.Row]) <= rows[p.Row].Min + 1e-4f; break;
                    case Kind.Plus: p.Disabled = CurrentNumber(rows[p.Row]) >= rows[p.Row].Max - 1e-4f; break;
                    case Kind.Run: p.Selected = false; p.Disabled = rows[p.Row].Can != null && !rows[p.Row].Can(); break;
                    default: p.Selected = false; break;
                }
            }
            foreach (Row r in rows)
            {
                if (r.Value3D != null) r.Value3D.text = r.Show(CurrentNumber(r));
                if (r.Slider != null && !r.Slider.Dragging) r.Slider.Set(CurrentNumber(r));
            }
        }

        static readonly Color s_backIdle = new Color(0.035f, 0.028f, 0.02f, 0.92f), s_backHover = new Color(0.09f, 0.07f, 0.045f, 0.94f),
                              s_backLit = new Color(0.36f, 0.24f, 0.08f, 0.96f), s_frameIdle = new Color(0.62f, 0.48f, 0.22f, 0.85f),
                              s_frameHot = new Color(1f, 0.82f, 0.4f, 1f), s_frameOff = new Color(0.3f, 0.28f, 0.25f, 0.6f);

        void Look()
        {
            for (int i = 0; i < m_count; i++)
            {
                Plate p = m_plates[i];
                Color f = p.Disabled ? s_frameOff : p.Hovered || p.Selected ? s_frameHot : Color.Lerp(s_frameIdle, s_frameHot, Mathf.Round(p.Glow * 0.7f * 20f) / 20f);
                Color b = p.Selected ? s_backLit : p.Hovered ? s_backHover : s_backIdle;
                Color l = p.Disabled ? new Color(0.6f, 0.58f, 0.55f, 1f) : p.Selected ? GameText.GoldTop : GameText.Parchment;
                if (f != p.FrameShown)
                {
                    p.FrameShown = f;
                    Color lin = f.linear;
                    lin.a = f.a;
                    p.FrameMesh.colors = new[] { lin, lin, lin, lin, lin, lin, lin, lin };
                }
                if (b != p.BackShown)
                {
                    p.BackShown = b;
                    Color lin = b.linear;
                    lin.a = b.a;
                    p.BackMesh.colors = new[] { lin, lin, lin, lin };
                }
                if (l != p.LabelShown) { p.LabelShown = l; p.Label.color = l; }
            }
        }

        // ---- pressing -------------------------------------------------------------------------------------------------
        string PressAt(int i, Hand h)
        {
            if (i < 0 || i >= m_count) return "gone";
            Plate p = m_plates[i];
            if (p.Disabled) return "disabled";
            Do(p);
            return null;
        }

        void Do(Plate p)
        {
            switch (p.K)
            {
                case Kind.Entry: m_wantOpen = true; break;
                case Kind.Back: m_wantClose = true; break;
                case Kind.Tab:
                    if (p.Opt != m_tab) { m_tab = p.Opt; m_relayout = true; }
                    break;
                case Kind.Choice:
                    {
                        Row r = m_rows[m_tab][p.Row];
                        Write(r, r.Values[p.Opt]);
                        break;
                    }
                case Kind.Run:
                    {
                        Row r = m_rows[m_tab][p.Row];
                        if (r.Act != null && (r.Can == null || r.Can())) r.Act();
                        break;
                    }
                case Kind.DevCat:
                    m_devCat = p.Key.Substring(4);
                    m_relayout = true;
                    break;
                case Kind.DevPick:
                    {
                        Scenarios.Entry e = p.Opt >= 0 && p.Opt < Scenarios.Catalog.Count ? Scenarios.Catalog[p.Opt] : null;
                        string r = Scenarios.Pick(e);
                        Plugin.L.LogInfo("vr settings: Dev " + (e != null ? e.Tab + "/" + e.Label : "-") + ": " + r);
                        if (e != null && e.Tab == "Weapon") break;
                        m_relayout = true;
                        break;
                    }
                case Kind.DevRepeat:
                    if (Scenarios.Last != null) Scenarios.Pick(Scenarios.Last);
                    break;
                case Kind.DevLeave:
                    Scenarios.Leave();
                    break;
                case Kind.Minus:
                case Kind.Plus:
                    {
                        Row r = m_rows[m_tab][p.Row];
                        float v = CurrentNumber(r) + (p.K == Kind.Plus ? r.Step : -r.Step);
                        v = Mathf.Clamp(Mathf.Round(v / r.Step) * r.Step, r.Min, r.Max);
                        Write(r, v.ToString("0.###", CultureInfo.InvariantCulture));
                        break;
                    }
            }
            m_nextRefresh = 0f;
        }

        // ---- tests ----------------------------------------------------------------------------------------------------
        /// A button by its key ("Back", "Fights", "Combat/Classic", "Walk speed/+"; '_' for a space), exact first, else a part.
        int Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return -1;
            key = key.Replace('_', ' ');
            for (int i = 0; i < m_count; i++) if (string.Equals(m_plates[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
            for (int i = 0; i < m_count; i++) if (m_plates[i].Key.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return i;
            return -1;
        }

        string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(m_mode == Mode.Page ? "page " + s_tabs[m_tab] : m_mode == Mode.Entry ? "entry" : "closed");
            sb.Append(" | opens ").Append(Opens).Append(" changes ").Append(Changes).Append(" last ").Append(Last);
            if (m_mode == Mode.Page && DevTab) sb.Append(" | dev ").Append(m_devCat);
            else if (m_mode == Mode.Page)
            {
                sb.Append(" |");
                foreach (Row r in m_rows[m_tab]) sb.Append(' ').Append(r.Name).Append('=').Append(r.Act != null ? "[" + r.ActText + "]" : r.Values != null ? Current(r) : r.Show(CurrentNumber(r))).Append(';');
            }
            if (m_mode != Mode.None)
            {
                sb.Append(" | plates");
                for (int i = 0; i < m_count; i++) sb.Append(" [").Append(i + 1).Append(' ').Append(m_plates[i].Key).Append(m_plates[i].Selected ? "*" : "").Append(m_plates[i].Disabled ? "-" : "").Append(']');
            }
            sb.Append(" | ").Append(m_touch.Describe());
            sb.Append(" | hand check ").Append(m_underCheck ? "over the page" : "-").Append(" opens ").Append(CheckOpens).Append(" returns ").Append(CheckReturns);
            return sb.ToString();
        }

        /// `vrset` (state), `vrset open [tab]`, `vrset close`, `vrset tab <name>`, `vrset geo <key>`, `vrset pose <key> [cm] [l|r]`
        /// (the point cm in front of a button and the xrsim move through it, as `platepose`), `vrset tap|hover <key> [l|r]` (a
        /// synthetic fingertip through the real press), `vrset size` (sizes and distances for the checks).
        string Command(string[] a)
        {
            string op = a.Length > 1 ? a[1] : "";
            Hand hand = Array.IndexOf(a, "l") > 1 ? VRInput.L : VRInput.R;
            switch (op)
            {
                case "open":
                    if (a.Length > 2) { int t = Array.FindIndex(s_tabs, x => x.Equals(a[2], StringComparison.OrdinalIgnoreCase)); if (t >= 0) m_tab = t; }
                    if (m_mode == Mode.None) return "no pause menu board";
                    m_wantOpen = true;
                    return "opening";
                case "close": if (m_mode == Mode.Page) m_wantClose = true; return "closing";
                case "tab":
                    {
                        int t = a.Length > 2 ? Array.FindIndex(s_tabs, x => x.Equals(a[2], StringComparison.OrdinalIgnoreCase)) : -1;
                        if (t < 0) return "no tab";
                        m_tab = t;
                        m_relayout = true;
                        return "tab " + s_tabs[t];
                    }
                case "geo":
                case "pose":
                case "tap":
                case "hover":
                    {
                        int i = Find(a.Length > 2 ? a[2] : "");
                        if (i < 0) return "no button " + (a.Length > 2 ? a[2] : "");
                        Plate p = m_plates[i];
                        VRRig r = VRRig.I;
                        float s = r.Scale;
                        if (op == "geo")
                        {
                            Vector3 c = p.RestFace, nn = r.FromWorldDir(-p.T.forward), u = r.FromWorldDir(p.T.right);
                            Vector3 eye = r.HeadWorldPos, rel = Quaternion.Inverse(Quaternion.Euler(0f, r.HeadWorldRot.eulerAngles.y, 0f)) * (c - eye) / s;
                            return string.Format(CultureInfo.InvariantCulture, "button {0} '{1}' centre {2:0.####} {3:0.####} {4:0.####} normal {5:0.###} {6:0.###} {7:0.###} right {8:0.###} {9:0.###} {10:0.###} size {11:0.####} {12:0.####} | {13:0.###} ahead {14:0.###} below {15:0.###} right of the eyes",
                                i + 1, p.Key, c.x, c.y, c.z, nn.x, nn.y, -nn.z, u.x, u.y, -u.z, p.Size.x / s, p.Size.y / s, rel.z, -rel.y, rel.x);
                        }
                        if (op == "pose")
                        {
                            float cm = 6f;
                            if (a.Length > 3) float.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out cm);
                            Vector3 face = p.RestFace, pt = face - p.T.forward * (cm * 0.01f * s);
                            Vector3 d = r.FromWorldDir(p.T.forward * ((cm + 1.5f) * 0.01f * s)) / s;
                            return string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####} {2:0.####} | move {3} {4:0.####} {5:0.####} {6:0.####}",
                                pt.x, pt.y, pt.z, hand.IsLeft ? "l" : "r", d.x, d.y, -d.z);
                        }
                        StartCoroutine(m_touch.Drive(this, hand, At, i, op, null));
                        return op + " " + p.Key;
                    }
                case "size":
                    {
                        if (m_mode == Mode.None) return "closed";
                        Plate p = m_plates[0];
                        return string.Format(CultureInfo.InvariantCulture, "{0}: first button {1:0.000} x {2:0.000} m; page {3:0.000} x {4:0.000} m, {5:0.00} m ahead; text {6:0.0} mm per line (rows), {7:0.0} mm (buttons)",
                            m_mode, p.Real.x, p.Real.y, PageW, PageH, Ahead, 27 * Mpp * 1000f * 1.2f, 26 * Mpp * 1000f * 1.2f);
                    }
                default: return Describe();
            }
        }
    }
}

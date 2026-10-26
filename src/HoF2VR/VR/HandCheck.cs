using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The one-minute hand check (docs/hand-feel-design.md §3.2, §3.10, §5 question 3): offered once at the title after
    /// the hand-feel update (Hands.HandCheckOffered), skippable at every step, again any time from the VR settings page (Hands,
    /// "Calibrate hands", over the pause menu: P4-3), the development jump menu (the trigger) or the `handcheck start` seam. Four short pages on a board of buttons within reach (TouchBoard,
    /// the same buttons and feel as everywhere):
    ///  1. Your gloves (2026-10-12: the user needed the other axes; the automatic fingertip fix was "still off"): the fit in
    ///     all six ways - tilt, turn, roll, up, forward, side - a step per press, live, with the gap between the gloves'
    ///     fingertips shown as a guide while the real fingertips touch (no automatic fix: it only moved two axes and its
    ///     steps chased the way the hands were held - NOTES.md round 5, item 12). Reset; saved when the page is left.
    ///  2. Vibrations: buzzes of 2, 3, 4, 6, 8 and 12 ms, one after the other, in both hands: the first one felt becomes
    ///     Hands.HapticFloorMs (shorter cues are lengthened to it); and the touch vibrations' length (Weaker / Stronger,
    ///     Hands.HapticStrength) with a test buzz. The board's own clicks are silent here.
    ///  3. What you will feel: a button to press (tick, click, release) and a locked one (two knocks).
    /// Every page has Back.
    /// Nothing is written from test runs (they share the player's settings file).
    internal class HandCheck : MonoBehaviour
    {
        public static HandCheck I;
        enum Page { Off, Offer, Fit, Buzz, Feel, Done }
        Page m_page = Page.Off;
        TouchBoard m_board;
        int m_version = -1;
        float m_titleSince = -1f, m_pageSince, m_buzzAt = -1f, m_closeAt = -1f;
        static readonly float[] s_ladder = { 2f, 3f, 4f, 6f, 8f, 12f };
        int m_rung;
        float m_floor = -1f;
        // fingertips
        float m_gap = -1f, m_steadySince = -1f, m_lastGap = -1f, m_nextText;
        Vector3 m_fix;
        bool m_fixed;
        public static int Offers, Runs, Completed;
        static bool s_autoInTest;   // `handcheck auto`: let the title offer run in a test run (nothing is saved there)
        public static string Last = "-";

        void Awake()
        {
            I = this;
            Diag.Commands["handcheck"] = Command;
            Diag.StatusExtras["handcheck"] = () => Diag.Q(Describe());
            // a posture change places the player again (in the dark): the board comes along, in front of the new eyes
            Places.PostureApplied += () =>
            {
                if (m_page == Page.Off || m_board == null || VRRig.I == null) return;
                m_board.PlaceInFront();
                m_version = VRRig.I.Version;
            };
        }

        public bool Active => m_page != Page.Off;

        /// From the dev jump menu or the seam: run the check now, where the player stands.
        public void Begin()
        {
            Runs++;
            Show(Page.Fit);
        }

        void LateUpdate()
        {
            try { Tick(); }
            catch (Exception e) { Plugin.L.LogError("handcheck: " + e); Close("error"); }
        }

        void Tick()
        {
            bool vr = XRSettings.enabled && VRRig.I != null;
            // the offer: once, at the title, a few seconds after it is shown (never in test runs: `handcheck offer` there)
            bool title = vr && Places.Current == Places.Place.Title && !VRFade.IsBlack && GameWatch.State == "GameState_StartScreen";
            m_titleSince = title ? (m_titleSince < 0f ? Time.unscaledTime : m_titleSince) : -1f;
            if (m_page == Page.Off && title && (!Settings.HandCheckOffered || s_autoInTest) && (!TestMode.Enabled || s_autoInTest) && Offers == 0 &&
                Time.unscaledTime - m_titleSince > 2.5f && GameText.Ready && GameText.AtlasReady)
                Offer();
            if (m_page == Page.Off) return;
            if (Places.PostureBusy && vr) m_version = VRRig.I.Version;   // the player's own Standing / Seated: the check stays
            if (!vr || VRFade.IsBlack && !Places.PostureBusy || VRRig.I.Version != m_version || CombatVR.Active) { Close("the place changed"); return; }
            if (m_page == Page.Offer && !title) { Close("left the title"); return; }
            m_board.Tick();
            switch (m_page)
            {
                case Page.Fit: Fingertips(); break;
                case Page.Buzz:
                    if (m_buzzAt > 0f && Time.unscaledTime >= m_buzzAt)
                    {
                        m_buzzAt = -1f;
                        Haptics.TestPulse(false, s_ladder[m_rung]);
                        Haptics.TestPulse(true, s_ladder[m_rung]);
                        Plugin.L.LogInfo("handcheck: test buzz " + s_ladder[m_rung] + " ms");
                    }
                    break;
                case Page.Done:
                    if (m_closeAt > 0f && Time.unscaledTime >= m_closeAt) Close("done");
                    break;
            }
        }

        void Offer()
        {
            Offers++;
            Show(Page.Offer);
            // offered once: the player may simply go on with the game; the jump menu runs it later
            Settings.SaveHandCheck(0f, true);
            Plugin.L.LogInfo("handcheck: offered at the title" + (TestMode.Enabled ? " (test run: not saved)" : " (saved: offered)"));
        }

        void Show(Page p)
        {
            if (m_board == null)
            {
                m_board = new TouchBoard("handcheck");
                m_board.Pressed = Pressed;
            }
            bool first = m_page == Page.Off;
            m_page = p;
            m_pageSince = Time.unscaledTime;
            PressFeel.MuteHaptics = p == Page.Buzz;
            string text;
            string[] buttons;
            switch (p)
            {
                case Page.Offer:
                    text = "YOUR HANDS\n\nNew: buttons now go down under your finger and click, your fingertip is held steady near them, and your glove rests on what it touches.\n\nA one-minute check of your gloves and the vibrations?";
                    buttons = new[] { "Start", "Not now" };
                    break;
                case Page.Fit:
                    m_gap = m_lastGap = m_steadySince = -1f;
                    text = FitText();
                    buttons = FitButtons;
                    break;
                case Page.Buzz:
                    m_rung = 0;
                    m_buzzAt = Time.unscaledTime + 1.2f;
                    text = BuzzText();
                    buttons = new[] { "Felt it", "Nothing", "Again", "Weaker", "Stronger", "Test buzz", "Back", "Next" };
                    break;
                case Page.Feel:
                    text = "3 OF 3: WHAT YOU WILL FEEL\n\nTouch the buttons. Over one: a light tick. Pushed in: a click. Coming back up: a softer tick. The grey one is locked: two knocks.";
                    buttons = new[] { "Press me", "Locked", "Done", "Back" };
                    break;
                default:
                    text = "SAVED\n\n" + (m_floor > 0f ? "The shortest buzz you feel: " + m_floor.ToString("0", CultureInfo.InvariantCulture) + " ms. Shorter ones are made this long.\n\n" : "") +
                           "Run this check again any time: pause, VR Settings, Hands, Calibrate hands.";
                    buttons = new[] { "Close", "Back" };
                    m_closeAt = Time.unscaledTime + 10f;
                    break;
            }
            // the fit page: one row per axis, a slider between its - and + (M1, 2026-10-17: "it's impossible to do this without
            // sliders"), then Back / Reset / Next and the posture; the others a row or two
            if (m_board != null)
            {
                bool fit = p == Page.Fit;
                m_board.Columns = fit ? 2 : p == Page.Buzz ? 3 : 0;
                m_board.Rows = fit ? FitRows : null;
                m_board.Width = fit ? 0.085f : p == Page.Buzz ? 0.11f : 0f;
                m_board.Height = fit ? 0.036f : 0f;
                m_board.DownHere = fit ? 0.12f : float.NaN;
                if (fit) EnsureSliders();
                m_board.Sliders = fit ? m_sliders : null;
                if (m_sliders != null) foreach (TouchSlider sl in m_sliders) if (sl != null) { sl.SetActive(fit); if (fit) sl.Set(Settings.GloveFit(sl.Name)); }
            }
            // the board is placed once, where the player looks, and stays there from page to page
            if (!m_board.Show(first, text, buttons)) { m_page = Page.Off; return; }
            if (p == Page.Feel) m_board.SetDisabled(1, true);
            if (p == Page.Fit) ShowPosture();
            if (first) m_version = VRRig.I.Version;
            Last = p.ToString();
            Plugin.L.LogInfo("handcheck: page " + p);
        }

        void Pressed(int i, Hand h)
        {
            switch (m_page)
            {
                case Page.Offer:
                    if (i == 0) { Runs++; Show(Page.Fit); } else Close("not now");
                    break;
                case Page.Fit:
                    if (i < 12)
                    {
                        // tilt, turn, roll (1 degree), up, forward, side (0.25 cm): live
                        string[] axes = { "tilt", "turn", "roll", "up", "along", "side" };
                        float[] steps = { 1f, 1f, 1f, 0.25f, 0.25f, 0.25f };
                        int a = i / 2;
                        Settings.StepGloveFit(axes[a], (i % 2 == 0 ? -1f : 1f) * steps[a]);
                        FitChanges++;
                        if (m_sliders != null && m_sliders[a] != null) m_sliders[a].Set(Settings.GloveFit(axes[a]));
                        m_board.SetText(FitText());
                    }
                    else if (i == 12) { SaveFit(); Close("back"); }
                    else if (i == 13) { Settings.ResetGloveFit(); FitChanges++; SyncSliders(); m_board.SetText(FitText()); }
                    else if (i == PostureStanding || i == PostureSeated) SetPosture(i == PostureSeated);
                    else { SaveFit(); Show(Page.Buzz); }
                    break;
                case Page.Buzz:
                    if (i == 0) Felt(s_ladder[m_rung]);
                    else if (i == 1)
                    {
                        if (m_rung + 1 < s_ladder.Length) { m_rung++; m_buzzAt = Time.unscaledTime + 0.5f; m_board.SetText(BuzzText()); }
                        else Felt(s_ladder[s_ladder.Length - 1]);
                    }
                    else if (i == 2) m_buzzAt = Time.unscaledTime + 0.4f;
                    else if (i == 3 || i == 4)
                    {
                        // the touch vibrations' length (the user: "a button to decrease vibration intensity if I increased it too much")
                        Settings.StepHapticStrength(i == 3 ? -0.25f : 0.25f);
                        StrengthChanges++;
                        TestStrength(h);
                        m_board.SetText(BuzzText());
                    }
                    else if (i == 5) TestStrength(h);
                    else if (i == 6) Show(Page.Fit);
                    else Show(Page.Feel);
                    break;
                case Page.Feel:
                    if (i == 2) Show(Page.Done);
                    else if (i == 3) Show(Page.Buzz);
                    break;
                case Page.Done:
                    if (i == 1) Show(Page.Feel); else Close("closed");
                    break;
            }
        }

        void Felt(float ms)
        {
            m_floor = ms;
            Settings.SaveHandCheck(ms, true);
            Settings.LiveHapticFloor = ms;
            Plugin.L.LogInfo("handcheck: shortest buzz felt " + ms + " ms" + (TestMode.Enabled ? " (test run: this run only)" : " (saved)"));
            Show(Page.Feel);
        }

        string BuzzText() => "2 OF 3: VIBRATIONS\n\nHold both controllers loosely. Each buzz is a little longer than the one before. Did you feel this one?\n\nBuzz " +
                             (m_rung + 1) + " of " + s_ladder.Length + " (" + s_ladder[m_rung].ToString("0", CultureInfo.InvariantCulture) + " ms)\n\n" +
                             "Touch vibrations: " + (Settings.HapticStrength <= 0.001f ? "off" : Settings.HapticStrength.ToString("0.##", CultureInfo.InvariantCulture) + "x") +
                             " (Weaker / Stronger, then a test buzz)";

        /// A touch click at the current strength, in the hand that pressed (M2, 2026-10-17: it buzzed both hands, so a press
        /// with the right hand was felt in the left too; now each hand can be tried on its own).
        void TestStrength(Hand h)
        {
            Haptics.Event("click", h.IsLeft, 0.5f, 0.012f);
            Plugin.L.LogInfo("handcheck: touch vibration test on the " + (h.IsLeft ? "left" : "right") + " hand");
        }

        static readonly string[] FitButtons = { "Tilt -", "Tilt +", "Turn -", "Turn +", "Roll -", "Roll +", "Up -", "Up +", "Forward -", "Forward +", "Side -", "Side +", "Back", "Reset", "Next",
                                                "Standing", "Seated" };
        static readonly int[] FitRows = { 2, 2, 2, 2, 2, 2, 3, 2 };
        static readonly string[] s_axes = { "tilt", "turn", "roll", "up", "along", "side" };
        static readonly float[] s_slMin = { -45f, -45f, -60f, -6f, -8f, -5f }, s_slMax = { 45f, 45f, 60f, 6f, 8f, 5f }, s_slStep = { 0.5f, 0.5f, 0.5f, 0.1f, 0.1f, 0.1f };
        TouchSlider[] m_sliders;
        public static int SliderChanges;

        void EnsureSliders()
        {
            if (m_sliders != null || m_board == null) return;
            var made = new TouchSlider[s_axes.Length];
            for (int a = 0; a < s_axes.Length; a++)
            {
                TouchSlider sl = m_board.NewSlider(s_axes[a]);
                if (sl == null) return;
                sl.Min = s_slMin[a]; sl.Max = s_slMax[a]; sl.Step = s_slStep[a];
                string axis = s_axes[a];
                sl.Changed = v =>
                {
                    Settings.SetGloveFit(axis, v);
                    FitChanges++;
                    SliderChanges++;
                    m_board.SetText(FitText());
                };
                made[a] = sl;
            }
            m_sliders = made;
        }

        void SyncSliders()
        {
            if (m_sliders == null) return;
            for (int a = 0; a < m_sliders.Length; a++) if (m_sliders[a] != null) m_sliders[a].Set(Settings.GloveFit(s_axes[a]));
        }
        const int PostureStanding = 15, PostureSeated = 16;
        public static int FitChanges, StrengthChanges, PostureChoices;

        /// Standing / Seated (user 2026-10-13: "Should we do something to make it more seated friendly?"): at once (the player
        /// is placed again in a short dark) and saved (not from test runs).
        void SetPosture(bool seated)
        {
            PostureChoices++;
            if (seated != Settings.Seated)
            {
                Settings.SetSeated(seated);
                Plugin.L.LogInfo("handcheck: " + (seated ? "seated" : "standing") + (TestMode.Enabled ? " (test run: this run only)" : " (saved)"));
                Places.PostureChanged("hand check: " + (seated ? "Seated" : "Standing"));
            }
            ShowPosture();
            m_board.SetText(FitText());
        }

        void ShowPosture()
        {
            m_board.SetSelected(PostureStanding, !Settings.Seated);
            m_board.SetSelected(PostureSeated, Settings.Seated);
        }

        void SaveFit()
        {
            Settings.SaveGloveFit();
            Plugin.L.LogInfo("handcheck: glove fit " + Settings.GloveFitText() + (TestMode.Enabled ? " (test run: this run only)" : " (saved)"));
        }

        string FitText()
        {
            string tips;
            if (m_gap < 0f) tips = "Touch your two index fingertips together (fingers off the triggers) to see how far apart the gloves' fingertips are.";
            else if (m_gap < 0.008f) tips = "The gloves' fingertips touch too (" + (m_gap * 100f).ToString("0.0", CultureInfo.InvariantCulture) + " cm apart).";
            else tips = "The gloves' fingertips are " + (m_gap * 100f).ToString("0.0", CultureInfo.InvariantCulture) + " cm apart while yours touch.";
            return "1 OF 3: YOUR GLOVES\n\nHold the controllers the way you always do and look at your hands. Move the gloves until they sit like your hands - each button moves them a little, at once.\n\n" +
                   tips + "\n\nNow: " + Settings.GloveFitText() + "\n\n" +
                   (Settings.Seated && Settings.SeatedTableView == Settings.TableView.Lifted ? "You play SEATED: you see from about standing height everywhere, the Dealer's table too."
                    : Settings.Seated ? "You play SEATED: you sit at the Dealer's table like at a real one; in fights you see from standing height."
                                    : "You play STANDING. Playing in a chair? Sit down, then touch Seated.");
        }

        /// Both pointing fingertips close together and steady for a second: their gap (real metres) and the shift that
        /// would bring the gloves' tips onto their middle (controller frame, the right hand's; the left is its mirror).
        void Fingertips()
        {
            Hand l = VRInput.L, r = VRInput.R;
            Vector3 ll, lr;
            if (!l.Tracked || !r.Tracked || !VRHands.TryGetPointingTipLocal(true, out ll) || !VRHands.TryGetPointingTipLocal(false, out lr)) return;
            Vector3 tl = l.Pos + l.Rot * ll, tr = r.Pos + r.Rot * lr;   // tracking space, real metres
            float gap = Vector3.Distance(tl, tr);
            float now = Time.unscaledTime;
            if (gap > 0.07f) { m_steadySince = -1f; m_lastGap = -1f; return; }
            if (m_lastGap < 0f || Mathf.Abs(gap - m_lastGap) > 0.004f) { m_steadySince = now; m_lastGap = gap; return; }
            if (now - m_steadySince < 1.0f) return;
            bool changed = m_gap < 0f || Mathf.Abs(gap - m_gap) > 0.002f;
            m_gap = gap;
            // where the gap lies in each controller's frame (the right hand's; the left mirrored): logged, a guide only
            Vector3 mid = (tl + tr) * 0.5f;
            Vector3 er = Quaternion.Inverse(r.Rot) * (mid - tr), el = Quaternion.Inverse(l.Rot) * (mid - tl);
            el.x = -el.x;
            m_fix = (er + el) * 0.5f;
            if (changed || now >= m_nextText)
            {
                m_nextText = now + 1f;
                m_board.SetText(FitText());
                if (changed) Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "handcheck: fingertips {0:0.0} cm apart (per hand: side {1:0.0}, up {2:0.0}, along {3:0.0} cm) at {4}",
                    gap * 100f, m_fix.x * 100f, m_fix.y * 100f, m_fix.z * 100f, Settings.GloveFitText()));
            }
        }

        void Close(string why)
        {
            // (the fit is "saved when the page is left" - also when the check closes on its own: a recentre, a place change; only
            // Back / Next saved it before, and the live changes were lost at the next start)
            if (m_page == Page.Fit) SaveFit();
            if (m_board != null) m_board.Hide();
            if (m_page == Page.Done || m_page == Page.Feel) Completed++;
            if (m_page != Page.Off) Plugin.L.LogInfo("handcheck: closed (" + why + ")");
            m_page = Page.Off;
            m_buzzAt = m_closeAt = -1f;
            PressFeel.MuteHaptics = false;
            Last = "closed: " + why;
        }

        string Describe() => m_page + " | offered " + Settings.HandCheckOffered + " (this run " + Offers + ") runs " + Runs + " completed " + Completed +
                             " | posture " + (Settings.Seated ? "seated" : "standing") + " choices " + PostureChoices +
                             " | gap " + (m_gap < 0f ? "-" : (m_gap * 100f).ToString("0.0", CultureInfo.InvariantCulture) + " cm") +
                             " | floor " + Settings.HapticFloorMs.ToString("0.#", CultureInfo.InvariantCulture) + " ms | strength " +
                             Settings.HapticStrength.ToString("0.##", CultureInfo.InvariantCulture) + " | fit " + Settings.GloveFitText() + " | last " + Last +
                             (m_board != null ? " | " + m_board.Describe() : "");

        /// `handcheck start|offer|close|state`, `handcheck tap <i>` (a synthetic fingertip on button i), `handcheck pose <i> [cm]`.
        string Command(string[] a)
        {
            string op = a.Length > 1 ? a[1] : "state";
            int ix = a.Length > 2 ? Math.Max(0, int.Parse(a[2]) - 1) : 0;
            switch (op)
            {
                case "start": Begin(); return "page " + m_page;
                case "offer": Offer(); return "page " + m_page;
                case "auto": s_autoInTest = true; Offers = 0; return "the title offer may run in this test run";
                case "close": Close("seam"); return "closed";
                case "tap":
                    if (m_board == null || !m_board.Visible || ix >= m_board.Count) return "no button " + (ix + 1);
                    StartCoroutine(m_board.Drive(this, VRInput.R, ix, "tap"));
                    return "tap button " + (ix + 1);
                case "slider":
                    {
                        // `handcheck slider <axis 1-6> <value> [depth cm]`: the world point on that slider for the value (tests)
                        if (m_sliders == null || ix >= m_sliders.Length || a.Length < 4) return "no slider";
                        float v = float.Parse(a[3], CultureInfo.InvariantCulture), d = a.Length > 4 ? float.Parse(a[4], CultureInfo.InvariantCulture) / 100f : 0.008f;
                        Vector3 w = m_sliders[ix].PointFor(v, d);
                        return string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####} {2:0.####} | {3} {4:0.##} drags {5} moves {6} changes {7}", w.x, w.y, w.z,
                                             m_sliders[ix].Name, m_sliders[ix].Value, TouchSlider.Drags, TouchSlider.Moves, SliderChanges);
                    }
                case "pose":
                    {
                        float cm = 6f;
                        if (a.Length > 3) float.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out cm);
                        return m_board != null ? m_board.Pose(ix, cm, VRInput.R) : "none";
                    }
                default: return Describe();
            }
        }
    }
}

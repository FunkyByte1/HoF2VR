using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Stop the pendulum or the wheel by hand (interaction design §3.3), from anywhere at the table:
    ///  * slap the table: a hand comes down onto the table top fast (palm crossing the top from at
    ///    least 8 cm above within 0.3 s, at 1.2 m/s or more, over the table)
    ///  * or squeeze either grip.
    /// Both end in the game's own selection path: UISelection.TrySelect on the mini-game's default
    /// selectable (the one the game hovers and locks while it accepts input), whose action is the
    /// game's PendulumTask.EvaluatePendulum / WheelSpinTask.OnWheelSelect. The trigger (laser at
    /// nothing = A) keeps working.
    internal class TableStops : MonoBehaviour
    {
        public static float SlapSpeed = 1.2f;      // m/s downward
        public static float SlapFrom = 0.08f;      // m above the plane within SlapWindow
        public static float SlapWindow = 0.3f;     // s
        public static float PalmDepth = 0.03f;     // the palm's surface is this far below the grip centre
        public static float Debounce = 1f;

        static FieldInfo s_spinState;
        static float s_lastSlap = -10f;
        public static float LastSlapTime => s_lastSlap;
        public static int Stops, Refused, Slaps;
        public static string Last = "-";
        static bool s_hintPendulum, s_hintWheel, s_stopEarly;

        void Awake()
        {
            s_spinState = AccessTools.Field(typeof(WheelOfFortune), "m_spinState");
            Diag.StatusExtras["pendulum"] = () => Diag.Q(PendulumStatus());
            Diag.StatusExtras["wheel"] = () => Diag.Q(WheelStatus());
            Diag.StatusExtras["stops"] = () => Diag.Q(Stops + " refused " + Refused + " slaps " + Slaps + " nudges " + Nudges + " last " + Last);
            Diag.StatusExtras["nudge"] = () =>
            {
                UIDialogue ui = NudgeDialogue();
                if (ui == null) return Diag.Q("-");
                var sb = new System.Text.StringBuilder("open:");
                for (int i = 0; i < ui.Buttons.Count; i++) sb.Append(' ').Append(i).Append("='").Append(Label(ui.Buttons[i]).Replace("\n", " ")).Append('\'');
                return Diag.Q(sb.ToString());
            };
            Diag.Commands["stop"] = a =>
            {
                // `stop early`: stop in the frame the next pendulum swing starts (inside the game's own
                // 0.2 s guard) -> must be ignored by the game, the pendulum keeps swinging
                if (a.Length > 1 && a[1] == "early") { s_stopEarly = true; return "will stop in the frame the next swing starts"; }
                // `stop [l|r] [grip|slap]`: run the stop exactly as the gesture would (test seam)
                Hand h = a.Length > 1 && a[1].StartsWith("l") ? VRInput.L : VRInput.R;
                UISelectable sel;
                string what = Armed(out sel);
                if (what == null) return "nothing to stop (" + PendulumStatus() + " / " + WheelStatus() + ")";
                return Stop(what, sel, h, a.Length > 2 ? a[2] + " [seam]" : "seam") ? "stopped " + what : "refused";
            };
        }

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(PendulumTask), "SwingPendulum"), postfix: new HarmonyMethod(typeof(TableStops), nameof(SwingPostfix)));
        }

        /// Test seam `stop early`: a stop in the very frame the swing starts, inside the game's own
        /// 0.2 s input guard (PendulumTask.EvaluatePendulum) - the game must ignore it.
        static void SwingPostfix()
        {
            if (!s_stopEarly) return;
            s_stopEarly = false;
            UISelectable sel;
            string what = Armed(out sel);
            if (what != null) Stop(what, sel, VRInput.R, "grip [seam, swing start]");
        }

        static WheelOfFortune Wheel()
        {
            DeckManager dm = DeckManager.Instance;
            return dm != null && dm.WheelSpinContainer != null ? dm.WheelSpinContainer.GetComponent<WheelOfFortune>() : null;
        }

        static string SpinState(WheelOfFortune w)
        {
            try { return w != null && s_spinState != null ? s_spinState.GetValue(w).ToString() : "-"; }
            catch (Exception) { return "?"; }
        }

        static string PendulumStatus()
        {
            PendulumTask t = PendulumTask.RunningTask;
            Pendulum p = CardTable.Instance != null ? CardTable.Instance.Pendulum : null;
            if (t == null || p == null) return "-";
            return (p.IsSwinging ? "swinging" : "stopped") + " result=" + t.Result;
        }

        static string WheelStatus()
        {
            WheelSpinTask t = WheelSpinTask.RunningTask;
            if (t == null) return "-";
            Card c = t.Result;
            return SpinState(Wheel()) + " result=" + (c != null ? c.name.Replace("(Clone)", "") : "-");
        }

        /// What a stop gesture would stop right now (null = nothing): only while the game itself takes
        /// the stop input - its default selectable has the stop action and is the locked hover.
        public static string Armed(out UISelectable sel)
        {
            sel = null;
            try
            {
                Pendulum p = CardTable.Instance != null ? CardTable.Instance.Pendulum : null;
                if (PendulumTask.RunningTask != null && p != null && p.IsSwinging && p.SelectableGroup != null)
                {
                    UISelectable d = p.SelectableGroup.DefaultSelectable;
                    if (d != null && d.HasSelectAction && d.Selection != null && d.Selection.LockedObject == d) { sel = d; return "pendulum"; }
                }
                WheelOfFortune w = Wheel();
                if (WheelSpinTask.RunningTask != null && w != null && SpinState(w) == "Looping" && w.SelectableGroup != null)
                {
                    UISelectable d = w.SelectableGroup.DefaultSelectable;
                    if (d != null && d.HasSelectAction && d.Selection != null && d.Selection.LockedObject == d) { sel = d; return "wheel"; }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// The grips stop the mini-game instead of paging while one is running.
        public static bool OwnsGrips => PendulumTask.RunningTask != null || WheelSpinTask.RunningTask != null;

        // ---- wheel nudge ---------------------------------------------------------------------------
        // After the wheel stops with a nudge left (a card effect), the game asks in a dialogue: nudge
        // left, keep, nudge right. A quick sideways flick of either hand picks the nudge that way (the
        // button whose label says left / right), a slap keeps the card. Laser, stick and A still work.
        public static float FlickSpeed = 0.8f, FlickDistance = 0.10f, FlickWindow = 0.25f;
        static float s_nudgeSince = -1f, s_lastFlick = -10f;
        public static int Nudges;

        static UIDialogue NudgeDialogue()
        {
            if (WheelSpinTask.RunningTask == null || MenuManager.Instance == null) return null;
            MenuStack stack = MenuManager.Instance.GetStack(MenuManager.StackPriority.Dialogue);
            Dialogue d = stack != null ? stack.CurrentMenu() as Dialogue : null;
            UIDialogue ui = d != null ? d.GetComponentInChildren<UIDialogue>() : null;
            return ui != null && ui.Buttons.Count >= 3 ? ui : null;
        }

        static string Label(UIDialogueButton b)
        {
            UILabel l = b != null ? b.GetComponentInChildren<UILabel>() : null;
            return l != null && l.text != null ? l.text : "";
        }

        /// Which button a flick that way means: the one labelled left / right, else the game's order
        /// (option 0 = nudge left, 2 = nudge right, as WheelSpinTask.PromptNudge names them).
        static int NudgeIndex(UIDialogue ui, bool left)
        {
            for (int i = 0; i < ui.Buttons.Count; i++)
                if (Label(ui.Buttons[i]).IndexOf(left ? "left" : "right", StringComparison.OrdinalIgnoreCase) >= 0) return i;
            return left ? 0 : 2;
        }

        static bool PickNudge(UIDialogue ui, int index, Hand h, string how)
        {
            UISelectable sel = ui.Buttons[index].Selectable;
            bool ok = false;
            try { ok = sel != null && UISelection.TryHover(sel) && UISelection.TrySelect(sel); }
            catch (Exception e) { Plugin.L.LogWarning("nudge: " + e.Message); }
            Plugin.L.LogInfo("nudge: option " + index + " '" + Label(ui.Buttons[index]).Replace("\n", " ") + "' by " + how + " (" + (h.IsLeft ? "L" : "R") + ") -> " + (ok ? "selected" : "refused"));
            if (ok) { Nudges++; Last = "nudge " + index + " by " + how; Haptics.Cue(Haptics.CueKind.Click, h.IsLeft); }
            else Haptics.Cue(Haptics.CueKind.Refuse, h.IsLeft);
            return ok;
        }

        /// The flick or slap on the nudge dialogue; true when it took this frame's hand input.
        static bool Nudge()
        {
            UIDialogue ui = NudgeDialogue();
            if (ui == null) { s_nudgeSince = -1f; return false; }
            if (s_nudgeSince < 0f) s_nudgeSince = Time.unscaledTime;
            if (Time.unscaledTime - s_nudgeSince < 0.3f || VRRig.I == null) return true;
            float s = VRRig.I.Scale;
            Vector3 right = VRRig.I.NeutralRot * Vector3.right;
            foreach (Hand h in VRInput.RL)
            {
                if (!HandWorld.Valid(h)) continue;
                if (Settings.StopBySlap && Slapped(h)) { PickNudge(ui, 1, h, "slap"); return true; }
                Vector3 v = HandWorld.Vel(h) / s;
                float lat = Vector3.Dot(v, right), d = Vector3.Dot(HandWorld.Displacement(h, FlickWindow), right) / s;
                if (Mathf.Abs(lat) < FlickSpeed || Mathf.Abs(d) < FlickDistance || Mathf.Sign(lat) != Mathf.Sign(d) ||
                    Mathf.Abs(v.y) > Mathf.Abs(lat) || Time.unscaledTime - s_lastFlick < 0.8f) continue;
                s_lastFlick = Time.unscaledTime;
                PickNudge(ui, NudgeIndex(ui, lat < 0f), h, lat < 0f ? "flick left" : "flick right");
                return true;
            }
            return true;
        }

        void Update()
        {
            if (!XRSettings.enabled || !Places.AtCaravan) return;
            if (Settings.NudgeByFlick && Nudge()) return;
            UISelectable sel;
            string what = Armed(out sel);
            if (what != null) Hint(what);

            foreach (Hand h in VRInput.RL)
            {
                if (Settings.StopByGrip && what != null && h.GripDown) { Stop(what, sel, h, "grip"); return; }
                // without lasers the trigger has no pointer to confirm with: it stops the pendulum / wheel itself
                if (!Settings.Lasers && what != null && h.TriggerDown) { Stop(what, sel, h, "trigger"); return; }
                if (Settings.StopBySlap && Slapped(h))
                {
                    Slaps++;
                    if (what != null) { Stop(what, sel, h, "slap"); return; }
                    Plugin.L.LogInfo("stop: slap by " + (h.IsLeft ? "L" : "R") + " (nothing to stop)");
                }
            }
        }

        /// The stop hint's card: HintLeft left of straight ahead, HintDown below the eyes (real metres), beside what it is about.
        public static float HintLeft = 0.42f, HintDown = 0.30f;

        static void Hint(string what)
        {
            if (VRHelp.I == null) return;
            if (what == "pendulum" && !s_hintPendulum)
            {
                s_hintPendulum = true;
                // (to the left of the pendulum, which comes in front of the player: mark 24 had this card over its zones)
                VRHelp.I.Show("PENDULUM\n\nSlap the table or squeeze a grip to stop it.\n(The trigger works too.)", 6f, HintDown, 30f, false, -HintLeft);
            }
            else if (what == "wheel" && !s_hintWheel)
            {
                s_hintWheel = true;
                VRHelp.I.Show("WHEEL\n\nSlap the table or squeeze a grip to stop it.\n(The trigger works too.)", 6f, HintDown, 30f, false, -HintLeft);
            }
        }

        /// A slap onto the table top this frame.
        static bool Slapped(Hand h)
        {
            if (!HandWorld.Valid(h) || VRRig.I == null) return false;
            Bounds b;
            if (!ReachProbe.TableBounds(out b)) return false;
            float s = VRRig.I.Scale;
            float plane = b.max.y + PalmDepth * s;
            Vector3 p = HandWorld.Palm(h), prev;
            if (!HandWorld.PrevPalm(h, out prev) || !(prev.y > plane && p.y <= plane)) return false;
            float m = 0.05f * s;
            if (p.x < b.min.x - m || p.x > b.max.x + m || p.z < b.min.z - m || p.z > b.max.z + m) return false;
            float dt = Mathf.Max(1e-3f, Time.unscaledDeltaTime);
            float down = Mathf.Max((prev.y - p.y) / dt, -HandWorld.Vel(h).y) / s;
            float from = (HandWorld.MaxY(h, SlapWindow) - plane) / s;
            bool ok = down >= SlapSpeed && down < 15f && from >= SlapFrom && Time.unscaledTime - s_lastSlap > Debounce;
            if (ok || down >= SlapSpeed * 0.5f) Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "stop: {0} hand crossed the table top at {1:0.00} m/s from {2:0.00} m above -> {3}",
                h.IsLeft ? "L" : "R", down, from, ok ? "slap" : "no slap"));
            if (ok)
            {
                s_lastSlap = Time.unscaledTime;
                // the glove's own landing on the table top (TableTop) may have knocked a frame earlier: no second knock
                // the slap is felt and heard (docs/hand-feel-design.md §3.10 `bump`, §3.11 knock): longer and louder the
                // harder the hit
                if (Time.unscaledTime - TableTop.LastKnockTime > 0.15f)
                {
                    Haptics.Cue(Haptics.CueKind.Bump, h.IsLeft, Mathf.Clamp(down / 1.2f, 1f, 3.5f));
                    HandSounds.Play(HandSounds.Kind.Knock, new Vector3(p.x, plane, p.z), Mathf.Clamp((down - 0.6f) / 2.5f, 0.35f, 1f));
                }
            }
            return ok;
        }

        static bool Stop(string what, UISelectable sel, Hand h, string how)
        {
            bool selected = false;
            try { selected = UISelection.TrySelect(sel); }
            catch (Exception e) { Plugin.L.LogWarning("stop: " + e.Message); }
            UISelectable again;
            bool stopped = selected && Armed(out again) != what;
            if (stopped)
            {
                Stops++;
                Last = what + " by " + how + " (" + (h.IsLeft ? "L" : "R") + ")";
                Haptics.Event("stop " + what, h.IsLeft, 1f, 0.07f);
                if (VRHelp.I != null) VRHelp.I.Hide();
            }
            else
            {
                Refused++;
                Haptics.Cue(Haptics.CueKind.Refuse, h.IsLeft);
                HandSounds.Play(HandSounds.Kind.Refuse, HandWorld.Palm(h));
            }
            Plugin.L.LogInfo("stop: " + what + " by " + how + " (" + (h.IsLeft ? "L" : "R") + ") -> " + (stopped ? "stopped" : selected ? "too early (the game ignored it)" : "refused by the game"));
            return stopped;
        }
    }
}

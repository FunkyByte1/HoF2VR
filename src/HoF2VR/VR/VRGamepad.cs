using System.Collections.Generic;
using HarmonyLib;
using InControl;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// A virtual Xbox One pad registered with InControl. The game reads it exactly like a real pad
    /// (glyphs, buffering, priorities, rebinding all keep working). VRPad fills in its state.
    internal class VRGamepad : InputDevice
    {
        public Vector2 LS, RS;
        public float LT, RT;
        public bool A, B, X, Y, LB, RB, Start, Back, DUp, DDown, DLeft, DRight;

        public VRGamepad() : base("Xbox One Controller")
        {
            Meta = "HoF2VR virtual pad";
            SortOrder = -100;
            DeviceClass = InputDeviceClass.Controller;
            DeviceStyle = InputDeviceStyle.XboxOne;
            AddControl(InputControlType.LeftStickLeft, "Left Stick Left", 0.2f, 0.9f);
            AddControl(InputControlType.LeftStickRight, "Left Stick Right", 0.2f, 0.9f);
            AddControl(InputControlType.LeftStickUp, "Left Stick Up", 0.2f, 0.9f);
            AddControl(InputControlType.LeftStickDown, "Left Stick Down", 0.2f, 0.9f);
            AddControl(InputControlType.RightStickLeft, "Right Stick Left", 0.2f, 0.9f);
            AddControl(InputControlType.RightStickRight, "Right Stick Right", 0.2f, 0.9f);
            AddControl(InputControlType.RightStickUp, "Right Stick Up", 0.2f, 0.9f);
            AddControl(InputControlType.RightStickDown, "Right Stick Down", 0.2f, 0.9f);
            AddControl(InputControlType.LeftTrigger, "Left Trigger", 0.2f, 0.9f);
            AddControl(InputControlType.RightTrigger, "Right Trigger", 0.2f, 0.9f);
            AddControl(InputControlType.DPadUp, "DPad Up", 0.2f, 0.9f);
            AddControl(InputControlType.DPadDown, "DPad Down", 0.2f, 0.9f);
            AddControl(InputControlType.DPadLeft, "DPad Left", 0.2f, 0.9f);
            AddControl(InputControlType.DPadRight, "DPad Right", 0.2f, 0.9f);
            AddControl(InputControlType.Action1, "A");
            AddControl(InputControlType.Action2, "B");
            AddControl(InputControlType.Action3, "X");
            AddControl(InputControlType.Action4, "Y");
            AddControl(InputControlType.LeftBumper, "Left Bumper");
            AddControl(InputControlType.RightBumper, "Right Bumper");
            AddControl(InputControlType.View, "View");
            AddControl(InputControlType.Menu, "Menu");
            // deliberately no stick buttons: R3 opens the bug reporter, L3+R3 the debug GUI
        }

        /// InControl started counting its update ticks again (the game's reset: "Quit to title", the Scenarios' profile switch -
        /// Fixes/ResetGuard): this pad's controls still hold the old ticks and would throw "Cannot be updated with an earlier
        /// tick" every frame (6,000 times in a test). VRPad replaces the pad outside InControl's update.
        public bool Stale;
        ulong m_lastTick;

        public override void Update(ulong tick, float dt)
        {
            if (tick < m_lastTick || Stale) { Stale = true; return; }
            m_lastTick = tick;
            float invert = InControl.InputManager.InvertYAxis ? -1f : 1f;
            Stick(InputControlType.LeftStickLeft, InputControlType.LeftStickRight, InputControlType.LeftStickUp, InputControlType.LeftStickDown, LS, invert, tick, dt);
            Stick(InputControlType.RightStickLeft, InputControlType.RightStickRight, InputControlType.RightStickUp, InputControlType.RightStickDown, RS, invert, tick, dt);
            UpdateWithValue(InputControlType.LeftTrigger, LT, tick, dt);
            UpdateWithValue(InputControlType.RightTrigger, RT, tick, dt);
            UpdateWithState(InputControlType.DPadUp, DUp, tick, dt);
            UpdateWithState(InputControlType.DPadDown, DDown, tick, dt);
            UpdateWithState(InputControlType.DPadLeft, DLeft, tick, dt);
            UpdateWithState(InputControlType.DPadRight, DRight, tick, dt);
            UpdateWithState(InputControlType.Action1, A, tick, dt);
            UpdateWithState(InputControlType.Action2, B, tick, dt);
            UpdateWithState(InputControlType.Action3, X, tick, dt);
            UpdateWithState(InputControlType.Action4, Y, tick, dt);
            UpdateWithState(InputControlType.LeftBumper, LB, tick, dt);
            UpdateWithState(InputControlType.RightBumper, RB, tick, dt);
            UpdateWithState(InputControlType.View, Back, tick, dt);
            UpdateWithState(InputControlType.Menu, Start, tick, dt);
        }

        void Stick(InputControlType l, InputControlType r, InputControlType u, InputControlType d, Vector2 v, float invert, ulong tick, float dt)
        {
            UpdateWithValue(l, Mathf.Max(0f, -v.x), tick, dt);
            UpdateWithValue(r, Mathf.Max(0f, v.x), tick, dt);
            UpdateWithValue(u, Mathf.Max(0f, v.y * invert), tick, dt);
            UpdateWithValue(d, Mathf.Max(0f, -v.y * invert), tick, dt);
        }

        public override void Vibrate(float leftMotor, float rightMotor) => Haptics.FromGame(leftMotor, rightMotor);
    }

    /// Maps the motion controllers onto the virtual pad, depending on what the game is doing.
    internal class VRPad : MonoBehaviour
    {
        public static VRGamepad Dev;
        public static VRPad I;

        /// Synthetic presses (gestures, tests): button name -> frames remaining.
        static readonly Dictionary<string, int> s_pulses = new Dictionary<string, int>();
        static readonly List<string> s_keys = new List<string>();

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Defiant.InputManager), "InitDefaultBindings"),
                postfix: new HarmonyMethod(typeof(VRPad), nameof(PinDevice)));
            h.Patch(AccessTools.Method(typeof(Defiant.InputManager), "OnInputBy"),
                prefix: new HarmonyMethod(typeof(VRPad), nameof(OnInputByPrefix)));
        }

        void Awake()
        {
            I = this;
            Diag.StatusExtras["grips"] = () => Diag.Q("pointing " + PointingGrips + " page " + PageGrips + " lifted " + LiftedToPoint + " | now L " + (m_gripPage[0] ? "page" : VRInput.L.GripBtn ? "point" : "-") +
                                                   " R " + (m_gripPage[1] ? "page" : VRInput.R.GripBtn ? "point" : "-") + " | trigger touch known " + FingerSampler.TriggerTouchKnown);
            Diag.Commands["press"] = a =>
            {
                Press(a[1], a.Length > 2 ? int.Parse(a[2]) : 4);
                return "pulse " + a[1];
            };
        }

        /// Hold a pad button for a few frames (long enough for the game's rising-edge detection).
        public static void Press(string button, int frames = 4) => s_pulses[button.ToUpperInvariant()] = frames;

        static void PinDevice(Defiant.InputManager __instance)
        {
            if (Dev == null || !(XRSettings.enabled || TestMode.Enabled)) return;
            if (__instance.CombatActionSet != null) __instance.CombatActionSet.Device = Dev;
            if (__instance.UIActionSet != null) __instance.UIActionSet.Device = Dev;
            Plugin.L.LogInfo("VRPad: action sets pinned to the virtual pad");
        }

        static bool OnInputByPrefix(InputDeviceClass a_inputDeviceClass)
        {
            // In VR the game stays in Controller mode: its A/B/X/Y prompts match the Touch buttons, combat
            // keeps pad behaviour (run, aim), and the laser works through hover + A. A twitch of the
            // desktop mouse would otherwise flip everything to keyboard/mouse mode.
            if (!XRSettings.enabled) return true;
            return a_inputDeviceClass != InputDeviceClass.Mouse && a_inputDeviceClass != InputDeviceClass.Keyboard;
        }

        void Update()
        {
            // flat test runs get the virtual pad too, so scripted presses work there
            if (!XRSettings.enabled && !TestMode.Enabled) return;
            if (Dev != null && Dev.Stale)
            {
                try { if (Dev.IsAttached) InControl.InputManager.DetachDevice(Dev); } catch (System.Exception e) { Plugin.L.LogWarning("VRPad: detach: " + e.Message); }
                Dev = null;
                Plugin.L.LogInfo("VRPad: InControl restarted (a game reset) - a fresh virtual pad");
            }
            if (Dev == null) Dev = new VRGamepad();
            if (!Dev.IsAttached && InControl.InputManager.IsSetup)
            {
                InControl.InputManager.AttachDevice(Dev);
                if (Defiant.InputManager.Instance != null) PinDevice(Defiant.InputManager.Instance);
                Plugin.L.LogInfo("VRPad: virtual pad attached");
            }
            if (XRSettings.enabled) KeepOthersPassive();
            Map();
        }

        // The freezes (user 2026-10-11 "super laggy"; ~15 a minute of >= 40 ms in their session log, in bursts): every few
        // seconds another controller-class device (a joystick the system lists, with a twitching axis) became InControl's
        // active device for a moment and the virtual pad took over again. Each change made the game's ControllerAtlasManager
        // set the button-glyph atlas again, which re-fills every NGUI widget (UIAtlas.MarkAsChanged, 60-90 ms per change;
        // measured in the simulator with the script profiler: InControlManager.Update 170-210 ms every 4.9 s, twice). In VR
        // the game reads only the virtual pad (its action sets are pinned to it), so every other device is made Passive:
        // InControl never makes a passive device the active one.
        readonly HashSet<InputDevice> m_passive = new HashSet<InputDevice>();
        public static int MadePassive;
        int m_passiveCount = -1;

        void KeepOthersPassive()
        {
            System.Collections.ObjectModel.ReadOnlyCollection<InputDevice> devs = InControl.InputManager.Devices;
            if (devs == null || devs.Count == m_passiveCount && Time.frameCount % 90 != 0) return;
            m_passiveCount = devs.Count;
            foreach (InputDevice d in devs)
            {
                if (d == null || d == Dev || d.Passive) continue;
                d.Passive = true;
                MadePassive++;
                if (m_passive.Add(d))
                    Plugin.L.LogInfo("VRPad: '" + d.Name + "' (" + d.DeviceClass + ", " + d.DeviceStyle + (string.IsNullOrEmpty(d.Meta) ? "" : ", " + d.Meta) +
                                     ") is passive in VR: it can't take over from the virtual pad");
            }
        }

        float m_stickHeldSince = -1f;
        bool m_stickLong;

        // A grip squeeze is a page (LB / RB) only when it is a fist: the index finger rests on the trigger as the grip
        // closes. Squeezed with the index lifted it is a pointing hand (user 2026-10-11: "I want to make a pointing hand
        // to touch the button" - the grip used to page and the plates refused a hand with the grip held). Decided when
        // the grip closes and kept until it opens, so resting the finger back on the trigger later never pages. Runtimes
        // that never report trigger touch page as before.
        readonly bool[] m_gripPage = new bool[2];
        public static int PointingGrips, PageGrips;   // diagnostics

        void UpdateGripPages(Hand l, Hand r)
        {
            GripPage(l, 0);
            GripPage(r, 1);
        }

        // (P4-5) a grip closed as a fist (the index on the trigger) that then lifts the index to point: after LiftToPoint s with
        // the index off the trigger it is a pointing hand (the page it made is done; its bumper lets go) - a finger coming back
        // onto the trigger never turns it into a page again
        public static float LiftToPoint = 0.3f;
        readonly float[] m_liftSince = { -1f, -1f };
        public static int LiftedToPoint;

        void GripPage(Hand h, int i)
        {
            if (!h.GripBtn) { m_gripPage[i] = false; m_liftSince[i] = -1f; return; }
            if (h.GripDown)
            {
                bool pointing = FingerSampler.TriggerTouchKnown && !h.TriggerTouch;
                m_gripPage[i] = !pointing;
                m_liftSince[i] = -1f;
                if (pointing) PointingGrips++; else PageGrips++;
                return;
            }
            if (!m_gripPage[i] || !FingerSampler.TriggerTouchKnown) return;
            if (h.TriggerTouch) { m_liftSince[i] = -1f; return; }
            if (m_liftSince[i] < 0f) m_liftSince[i] = Time.unscaledTime;
            else if (Time.unscaledTime - m_liftSince[i] >= LiftToPoint)
            {
                m_gripPage[i] = false;
                LiftedToPoint++;
                Plugin.L.LogInfo("VRPad: " + (h.IsLeft ? "L" : "R") + " grip held, the index lifted: a pointing hand now (no page)");
            }
        }

        /// The hand is squeezing its grip as a pointing hand (index lifted): a touch, never a page.
        public static bool PointingGrip(Hand h) => I != null && h.GripBtn && !I.m_gripPage[h.IsLeft ? 0 : 1];

        /// Left stick click: short press = F1 (inventory) in menus. A hold (0.8 s) does nothing here any more: it paused (a
        /// fallback for runtimes without the Touch menu button), and in fights it opened the pause while the player held the
        /// stick to aim a bomb (playtest 3, mark 11: "I couldn't aim it because that opened up"). The left controller's menu
        /// button pauses (WiVRn has it). Returns true while the short-press pulse is due.
        bool LeftStickClick(Hand l)
        {
            // both sticks in together open WiVRn's own window on the headset: never the inventory
            if (VRInput.R.StickClick) { m_stickLong = true; }
            if (l.StickDown) { m_stickHeldSince = Time.unscaledTime; m_stickLong = VRInput.R.StickClick; }
            if (l.StickClick && !m_stickLong && m_stickHeldSince >= 0f && Time.unscaledTime - m_stickHeldSince > 0.8f) m_stickLong = true;   // a hold: no F1
            bool shortRelease = !l.StickClick && m_stickHeldSince >= 0f && !m_stickLong;
            if (!l.StickClick) m_stickHeldSince = -1f;
            return shortRelease;
        }

        void Map()
        {
            Hand l = VRInput.L, r = VRInput.R;
            bool stickShort = LeftStickClick(l);
            VRGamepad d = Dev;
            d.LS = d.RS = Vector2.zero;
            d.LT = d.RT = 0;
            d.A = d.B = d.X = d.Y = d.LB = d.RB = d.Start = d.Back = d.DUp = d.DDown = d.DLeft = d.DRight = false;
            UpdateGripPages(l, r);

            // B (right) + Y (left) together are the dev gestures (DevChord): the game sees B and Y only through the chord filter
            DevChord.Tick();
            bool rB = DevChord.B, lY = DevChord.Y, rA = r.A && !DevChord.SwallowA, lX = l.A && !DevChord.SwallowX;
            if (DevJump.Step(DevChord.Held, r.ADown, r.BDown, l.ADown, l.BDown, r.TriggerDown || l.TriggerDown))
            {
                // the dev jump card owns the face buttons while it is open
                VRPointer.SuppressConfirm = true;
            }
            else if (WaitGate.BlocksPad)
            {
                // the player is still in the waiting space (or holds the A that said "go"): the place they are going to
                // gets nothing from the controllers yet (test pulses below still go through)
                VRPointer.SuppressConfirm = true;
            }
            else if (VRSettings.OwnsPad)
            {
                // the VR settings page is open over the pause menu: the game gets nothing but the menu button (which closes the
                // pause menu, and the page with it); B is the page's Back
                VRPointer.SuppressConfirm = true;
                d.Start = l.Menu;
            }
            else if (GameWatch.InCombatPlay)
            {
                VRPointer.SuppressConfirm = false;
                // Combat: movement only from the stick; turning stays in the VR rig (right stick).
                // Buttons are the fallback for gestures (CombatInput adds swing/shield/dodge pulses).
                // walking and the Dash (A): CombatMove (the mod walks the hero; Combat.Walking = Game gives the stick and the
                // dodge to the game as before)
                CombatMove.Pad(d, l, r);
                d.B = rB;             // Stun / shield bash
                // triggers and grips follow the hands (left-handed: weapon in the left hand)
                Hand weapon = Settings.LeftHanded ? l : r, shield = Settings.LeftHanded ? r : l;
                // Physical combat mode: the blade and the guard act by themselves; the triggers press nothing
                d.X = weapon.TriggerBtn && !CombatSettings.Physical;   // Attack
                d.Y = shield.TriggerBtn && (!CombatSettings.Physical || Physical.Snared);   // Counter (Physical: only to struggle free)
                // the abilities: Classic presses the game's buttons; Physical holds, points and lets go itself (Abilities)
                d.RT = weapon.GripBtn && !CombatSettings.Physical ? 1f : 0f;   // Weapon ability
                d.RB = shield.GripBtn && !CombatSettings.Physical;     // Artifact
                d.LB = lX;            // Companion (X on the left Touch)
                d.LT = lY ? 1f : 0f;  // Prone finisher (Y on the left Touch)
                d.Start = l.Menu;
            }
            else
            {
                // Menus and the table: sticks navigate, A/B select/cancel, X/Y the nav-bar F3/F2,
                // grips page (Prev/Next), left stick click = F1 (inventory). Triggers drive the laser.
                d.LS = l.Stick;
                d.RS = r.Stick;
                // (a pinch at the hero's figurine is the trigger and A: A / X of a hand there pick it up, never select - FigurineGrab)
                bool keepA = FigurineGrab.SwallowFace(r), keepX = FigurineGrab.SwallowFace(l);
                d.A = rA && !keepA;
                d.B = rB;
                d.X = lX && !keepX;
                d.Y = lY;
                // grips page (LB/RB) - except while they grab the dice or stop the pendulum / wheel, and a pointing hand
                // (grip squeezed, index off the trigger) never pages: that's how a fingertip touches the choice buttons
                bool gripsOwned = Minigames.DiceCatapult || DiceHand.Holder != null || TableStops.OwnsGrips;
                d.LB = l.GripBtn && !gripsOwned && !TableCardGrab.OwnsGrip(l) && m_gripPage[0];
                d.RB = r.GripBtn && !gripsOwned && !TableCardGrab.OwnsGrip(r) && m_gripPage[1];
                if (stickShort) Press("BACK", 4);
                VRPointer.SuppressConfirm = Minigames.MapDice(d, l, r);
                d.Start = l.Menu;
                if (rB || l.A || lY || l.Stick.sqrMagnitude > 0.25f) VRPointer.Active = false;
            }

            s_keys.Clear();
            s_keys.AddRange(s_pulses.Keys);
            foreach (string k in s_keys)
            {
                int n = s_pulses[k];
                if (n <= 0) { s_pulses.Remove(k); continue; }
                s_pulses[k] = n - 1;
                // a pulse is "down" for its first half and "up" for the rest, so repeats re-trigger
                if (n <= 1) continue;
                switch (k)
                {
                    case "A": d.A = true; break;
                    case "B": d.B = true; break;
                    case "X": d.X = true; break;
                    case "Y": d.Y = true; break;
                    case "LB": d.LB = true; break;
                    case "RB": d.RB = true; break;
                    case "LT": d.LT = 1f; break;
                    case "RT": d.RT = 1f; break;
                    case "START": d.Start = true; break;
                    case "BACK": d.Back = true; break;
                    case "UP": d.DUp = true; break;
                    case "DOWN": d.DDown = true; break;
                    case "LEFT": d.DLeft = true; break;
                    case "RIGHT": d.DRight = true; break;
                }
            }
        }
    }
}

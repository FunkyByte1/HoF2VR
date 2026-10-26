using System;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// A touchable way on at the two stops that still went on only with A (the coverage audit's H17; the user 2026-10-12
    /// 21:50: "Sometimes there was no option to continue except pressing A"): the campfire's "Press (A)" - a Continue plate
    /// within reach once the place is ready (the rule of 2026-10-11: going on only once the destination is loaded) - and
    /// the enemies' description at a fight's start (the game's showcase) - a Continue plate under it. A stays everywhere.
    internal class GoPlates : MonoBehaviour
    {
        public static GoPlates I;
        TouchBoard m_gate, m_show;
        bool m_gateOn, m_showOn;
        public static int GateShows, ShowShows, ShowPresses;
        public static float GateDown = 0.30f, ShowDown = 0.34f;

        void Awake()
        {
            I = this;
            Diag.StatusExtras["goPlates"] = () => Diag.Q("gate " + (m_gateOn ? "shown" : "-") + " (" + GateShows + ", touched " + WaitGate.TouchGoes + ") | showcase " +
                                                          (m_showOn ? "shown" : "-") + " (" + ShowShows + ", touched " + ShowPresses + ")");
            // `goplate gate|show pose [cm]`: the plate's poke pose (as `handcheck pose`)
            Diag.Commands["goplate"] = a =>
            {
                TouchBoard b = a.Length > 1 && a[1] == "show" ? m_show : m_gate;
                float cm = 6f;
                if (a.Length > 3) float.TryParse(a[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cm);
                return b != null && b.Visible ? b.Pose(0, cm, VRInput.R) : "not shown";
            };
        }

        void LateUpdate()
        {
            try { Tick(); }
            catch (Exception e) { Plugin.L.LogError("go plates: " + e); m_gateOn = m_showOn = false; }
        }

        void Tick()
        {
            bool vr = XRSettings.enabled && VRRig.I != null;
            // the campfire: the gate waits for the player (the next place is ready)
            bool gate = vr && WaitGate.Waiting && Limbo.On && !Limbo.ArenaVeil && Limbo.Cover < 0.5f;
            if (gate != m_gateOn)
            {
                m_gateOn = gate;
                if (gate)
                {
                    if (m_gate == null) { m_gate = new TouchBoard("gate") { InSpace = true, LabelScale = 1.5f }; m_gate.Pressed = (i, h) => WaitGate.TouchGo(h.IsLeft); }
                    float d = TouchBoard.Down;
                    TouchBoard.Down = GateDown;
                    bool ok = m_gate.Show(true, "", "Continue");
                    TouchBoard.Down = d;
                    if (ok) { GateShows++; Plugin.L.LogInfo("go plates: Continue within reach at the campfire (the place is ready)"); }
                    else m_gateOn = false;
                }
                else if (m_gate != null) m_gate.Hide();
            }
            if (m_gateOn) m_gate.Tick();
            // the enemies' description before a fight
            bool show = vr && CombatVR.Active && VRUI.ShowcaseShowing && Time.frameCount - VRUI.ShowcaseFrame <= 2 && !VRFade.IsBlack;
            if (!show && Time.frameCount - VRUI.ShowcaseFrame > 2) VRUI.ShowcaseShowing = false;
            if (show != m_showOn)
            {
                m_showOn = show;
                if (show)
                {
                    if (m_show == null)
                    {
                        m_show = new TouchBoard("showcase") { LabelScale = 1.5f };
                        m_show.Pressed = (i, h) =>
                        {
                            ShowPresses++;
                            VRPad.Press("A", 6);
                            Plugin.L.LogInfo("go plates: the enemies' description closed by touch (as A)");
                        };
                    }
                    float d = TouchBoard.Down;
                    TouchBoard.Down = ShowDown;
                    bool ok = m_show.Show(true, "", "Continue");
                    TouchBoard.Down = d;
                    if (ok) { ShowShows++; Plugin.L.LogInfo("go plates: Continue within reach under the enemies' description"); }
                    else m_showOn = false;
                }
                else if (m_show != null) m_show.Hide();
            }
            if (m_showOn) m_show.Tick();
        }
    }
}

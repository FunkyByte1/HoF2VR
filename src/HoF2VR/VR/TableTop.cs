using System.Globalization;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The card table's top stops the drawn hand (docs/hand-feel-design.md §3.3), as the buttons stop the finger: a glove that
    /// comes down onto the table rests on it instead of sinking in. The glove's lowest point (its posed mesh at the controller)
    /// is held on the table top under it (the table's own collider: its real shape and edges); the real hand may go 8 cm
    /// further (real desks are often lower), then the glove lets go and follows it until the hand is above the table again.
    /// Only a hand that came down from above is stopped: one that is under the table's edge, or rises from below, is not.
    /// Coming down at a walking pace or faster gives a knock and a bump that grow with the speed (a slap that stops the
    /// pendulum or the wheel keeps its own). The stop itself is HandFeel's (the late pose, the ghost glove and the spring
    /// back all come with it).
    internal class TableTop : MonoBehaviour
    {
        public static float HoldReal = 0.08f, KnockFrom = 0.25f;
        public static int Contacts, Knocks;
        public static float LastKnockTime = -10f;
        public static string Last = "-";

        sealed class St
        {
            public bool Armed, InContact, HasLast, Over;
            public float LastY, Top, Low;
        }

        readonly St m_l = new St(), m_r = new St();
        Collider m_table;
        int m_tableVersion = -1;
        float m_nextFind;

        void Awake()
        {
            Diag.StatusExtras["tableTop"] = () => Diag.Q(Describe());
        }

        Collider Table()
        {
            if (m_table != null && m_tableVersion == SceneScan.Version) return m_table;
            if (m_tableVersion == SceneScan.Version && Time.unscaledTime < m_nextFind) return null;
            m_tableVersion = SceneScan.Version;
            m_nextFind = Time.unscaledTime + 2f;
            GameObject go = GameObject.Find("Models/Dealer_Caravan/dealer_caravan_cardtable/Table/Collider");
            m_table = go != null ? go.GetComponent<Collider>() : null;
            return m_table;
        }

        void LateUpdate()
        {
            bool on = XRSettings.enabled && Settings.StopAtSurfaces && VRRig.I != null && Places.Current == Places.Place.Caravan && !CombatVR.Active &&
                      !VRFade.IsBlack;
            Collider c = on ? Table() : null;
            Step(VRInput.L, m_l, c);
            Step(VRInput.R, m_r, c);
        }

        static void Reset(St s)
        {
            s.Armed = s.InContact = s.HasLast = s.Over = false;
        }

        void Step(Hand h, St s, Collider table)
        {
            Vector3 low;
            if (table == null || !h.Tracked || !VRHands.LowestPoint(h.IsLeft, h.Pos, h.Rot, out low)) { Reset(s); return; }
            float sc = VRRig.I.Scale;
            float dt = Mathf.Max(1e-3f, Time.unscaledDeltaTime);
            float down = s.HasLast ? (s.LastY - low.y) / dt / sc : 0f;   // the lowest point's downward speed, real m/s
            s.LastY = low.y;
            s.HasLast = true;
            s.Low = low.y;
            RaycastHit hit;
            s.Over = table.Raycast(new Ray(new Vector3(low.x, low.y + 0.4f * sc, low.z), Vector3.down), out hit, 0.9f * sc) && hit.normal.y > 0.7f;
            if (!s.Over) { s.Armed = false; s.InContact = false; return; }
            s.Top = hit.point.y;
            float e = hit.point.y - low.y;   // > 0: the glove would be inside the table
            if (e <= 0f) { s.Armed = true; s.InContact = false; return; }
            if (!s.Armed) return;   // under the edge, or rising from below: not stopped
            if (!s.InContact)
            {
                s.InContact = true;
                Contacts++;
                Feedback(h, new Vector3(low.x, hit.point.y, low.z), down);
            }
            HandFeel.Contact(h, Vector3.up, e, HoldReal);
        }

        static void Feedback(Hand h, Vector3 at, float down)
        {
            Last = string.Format(CultureInfo.InvariantCulture, "{0} at {1:0.00} m/s", h.IsLeft ? "L" : "R", down);
            // a slap that stops the pendulum or the wheel gives its own (bigger) knock
            if (down < KnockFrom || Time.unscaledTime - TableStops.LastSlapTime < 0.4f) return;
            Knocks++;
            LastKnockTime = Time.unscaledTime;
            Haptics.Cue(Haptics.CueKind.Bump, h.IsLeft, Mathf.Clamp(down / 1.2f, 0.5f, 3f));
            HandSounds.Play(HandSounds.Kind.Knock, at, Mathf.Clamp((down - 0.15f) / 2.5f, 0.12f, 0.8f));
        }

        string Describe()
        {
            VRRig rig = VRRig.I;
            return Side("L", m_l, rig, false) + " | " + Side("R", m_r, rig, true) + " | contacts " + Contacts + " knocks " + Knocks + " last " + Last;
        }

        /// Heights in tracking space (real metres): the table top under the glove, the tracked glove's lowest point and the
        /// drawn one (with HandFeel's offset).
        static string Side(string name, St s, VRRig rig, bool right)
        {
            if (rig == null || !s.HasLast) return name + " -";
            float drawn = s.Low + HandFeel.DrawnOffset(!right).y;
            return string.Format(CultureInfo.InvariantCulture, "{0} {1}{2} top {3:0.0000} low {4:0.0000} drawn {5:0.0000}", name,
                                 s.InContact ? "contact" : s.Armed ? "armed" : "-", s.Over ? "" : " (off the table)",
                                 s.Over ? rig.FromWorld(new Vector3(0f, s.Top, 0f)).y : float.NaN, rig.FromWorld(new Vector3(0f, s.Low, 0f)).y,
                                 rig.FromWorld(new Vector3(0f, drawn, 0f)).y);
        }
    }
}

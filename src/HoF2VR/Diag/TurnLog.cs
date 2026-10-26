using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace HoF2VR
{
    /// The per-frame turn log for the turning test (scripts/turn_test.py; the coverage audit's C4: snap angle, smooth speed,
    /// the snap blink, the turn and walk vignettes): `turnlog on|off|dump` -> testdata/probes/turnlog.tsv with, per frame,
    /// the time, the right and left sticks' x/y, the view's yaw in the fight (CombatVR), the comfort tunnel's strength and
    /// cause, the fade's alpha and the quick-blink count.
    internal class TurnLog : MonoBehaviour
    {
        static readonly List<string> s_rows = new List<string>();
        static bool s_on;

        void Awake()
        {
            Diag.Commands["turnlog"] = a =>
            {
                string op = a.Length > 1 ? a[1] : "";
                if (op == "on") { s_rows.Clear(); s_on = true; return "turn log on"; }
                if (op == "off") { s_on = false; return "turn log off (" + s_rows.Count + " rows)"; }
                if (op == "dump")
                {
                    string dir = System.IO.Path.Combine(TestMode.Dir ?? ".", "probes");
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "turnlog.tsv"),
                        "t\trx\try\tlx\tly\tyaw\tvignette\tcause\tfade\tblinks\tsnaps\n" + string.Join("\n", s_rows.ToArray()) + "\n");
                    return s_rows.Count + " rows -> probes/turnlog.tsv";
                }
                return "turn log " + (s_on ? "on" : "off") + ", " + s_rows.Count + " rows";
            };
        }

        void LateUpdate()
        {
            if (!s_on || s_rows.Count > 20000) return;
            var inv = CultureInfo.InvariantCulture;
            Vector2 r = VRInput.R.Stick, l = VRInput.L.Stick;
            float yaw = CombatVR.I != null && CombatVR.Active ? CombatVR.I.Yaw : float.NaN;
            float v = Comfort.I != null ? Comfort.I.Strength : 0f;
            string c = Comfort.I != null && Comfort.I.Cause.Length > 0 ? Comfort.I.Cause : "-";
            s_rows.Add(string.Format(inv, "{0:0.0000}\t{1:0.000}\t{2:0.000}\t{3:0.000}\t{4:0.000}\t{5:0.000}\t{6:0.000}\t{7}\t{8:0.000}\t{9}\t{10}",
                Time.unscaledTime, r.x, r.y, l.x, l.y, yaw, v, c, VRFade.Alpha, VRFade.QuickBlinks, CombatVR.SnapTurns));
        }
    }
}

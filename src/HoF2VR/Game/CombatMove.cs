using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace HoF2VR
{
    /// Walking and the Dash in fights (combat Phase 1 part A; docs/combat-uyf-design.md §7.1.3, §8.5.3; the user,
    /// 2026-10-11: "My character's movement has turn around time, acceleration, etc. All of those don't feel good in VR",
    /// and "A solid dash/dodge mechanic is a MUST-have").
    ///
    /// Walking (CombatSettings.WalkMode = Direct): the game no longer gets the left stick in fights, so its ActionMove (root
    /// motion, 0.5 / 1.5 s ramps, a 6-sample stick buffer, turning before moving) never runs. The mod moves the hero itself:
    ///  * the stick's velocity at once: radial deadzone 0.15, linear above it, x WalkSpeed (2.2 m/s), forward 1.0,
    ///    sideways 0.8, backward 0.65; only a 40 ms low-pass against jitter; letting go stops on the same frame;
    ///  * the direction of the head's yaw when the stick left the centre, kept while it is held (glancing round does not
    ///    bend the path), turning with the right-stick turns, sampled again after a snap turn or a return to the centre;
    ///  * along the navmesh (NavMeshAgent.Move): walls and edges stop it, nothing pushes; the hero's agent has no obstacle
    ///    avoidance in fights, so other bodies never shove it; the view follows the hero 1:1 (CombatVR);
    ///  * only while the hero is free (idle); the game's own actions (attacks and their lunges, hits, dodges) own it
    ///    otherwise. The hero turns to the gaze while it is free, so an attack without a target goes where you look.
    ///
    /// The Dash (A): the game's own dodge still runs (its duck window, the enemies' reaction to a dodge), but its 5 m roll
    /// becomes the Dash:
    ///  * the stick's direction relative to the head (as in walking; centred = straight back), 2.0 m in 0.15 s at a
    ///    constant speed, starting and stopping at once, along the navmesh;
    ///  * forward (within 45 deg of the gaze) at an enemy within 6 m and 35 deg of it: it ends at blade range, 0.9 m from
    ///    the enemy's body, taking up to 0.25 s;
    ///  * nothing an enemy does reaches the player from the press until 0.2 s after the dash ends (Controller.OnIgnore on
    ///    the hero; traps still do);
    ///  * 3 charges, each back 2 s after use; with none left a refuse tick and no dash;
    ///  * CombatSettings.DashStyle: Slide (the comfort vignette at full strength during it) or Blink (a short dark blink,
    ///    the move happening while it is dark);
    ///  * a whoosh and a double tick; a chime when something really was dodged.
    ///
    /// `[Combat] Dash = UYF` (the default since 2026-10-13 23:15; the above is `Dash = Short`): Until You Fall's dash
    /// (docs/uyf-parity-audit.md P20-P25, I10; its PlayerDash): forward 6 m in 0.187 s (32 m/s), homing on an enemy within
    /// 50 deg and 3 m, 35 deg and 6 m or 25 deg and 9 m and ending at it; back or sideways 3 m; 3 charges, each back 4 s after
    /// use, 0.2 s at least between two dashes; no safety window (a dash avoids a blow only by leaving its reach). A dash that
    /// reaches its enemy hits it: its guard worn by 10 x 0.3-1.25 (by the distance dashed, 1-6 m), pushed 3 m back, the others
    /// within 2 m of it knocked out to 2 m, and an attack it began less than 0.25 s ago falls apart (it fumbles for 1.25 s).
    /// Started by A or by a flick of the right stick forward / back (`[Combat] DashFlick`), without the game's dodge (its
    /// duck window would make the hero safe). A ring on the floor shows where it lands; three pips beside the super meter
    /// on the glove show the charges.
    internal class CombatMove : MonoBehaviour
    {
        public static CombatMove I;
        public const float Deadzone = 0.15f, Filter = 0.04f;
        public const float DashDistance = 2.0f, DashTime = 0.15f;
        /// UYF's dash: forward and back distances, speed (6 m in 0.187 s), the homing arcs, the least time between dashes.
        public const float UyfForward = 6f, UyfBack = 3f, UyfSpeed = 6f / 0.187f, UyfGap = 0.2f, UyfEndGap = 0.5f;
        /// UYF's landing (its DashTargeting / PlayerDash): the dash ends 1 m before whatever stands in its way
        /// (`_minDistanceToObstacle`); a dash with less than 0.5 m of room fails - no move, no charge (`_minDashDistance`);
        /// enemies within 0.3 m of its line (`_sphereCastRadius`) are nudged 1 m aside at 30 m/s (PushBackDashNudge).
        public const float UyfObstacleGap = 1f, UyfMinDash = 0.5f, UyfPathRadius = 0.3f, UyfNudge = 1f, UyfNudgeRate = 30f;
        static readonly float[] s_arcDist = { 3f, 6f, 9f }, s_arcDeg = { 50f, 35f, 25f };
        /// Each charge comes back this long after use: UYF 4 s, the short dash 2 s.
        public static float ChargeTime => CombatSettings.UyfDash ? 4f : 2f;
        public static int Impacts, Fumbles, Shockwaves, Flicks;
        public static string LastImpact = "-";
        /// The dodge window after the dash ends: 0.3 / 0.2 / 0.1 s on Relaxed / Normal / Fast (§8.5.5).
        public static float DashWindow => CombatSettings.DashWindow;
        const int Charges = 3;

        // walking state
        Vector2 m_stick;          // filtered stick (zero inside the deadzone)
        bool m_walking;
        float m_relYaw;           // the walk direction's yaw relative to the body yaw (CombatVR.Yaw)
        int m_snaps;
        Vector3 m_lastPos;
        public static Vector3 Velocity;      // what the mod asked for this frame (world, m/s)
        public static float Speed;           // the hero's real horizontal speed this frame
        public static int Frames, Steps;
        // the walk's status for the seams, kept as values and made a string only when asked (it was built every frame: most
        // of CombatMove.Update's 54 KB/s of garbage in MR2d's `profile alloc`)
        static string s_lastAct = "-";
        static bool s_lastFree = true;
        static float s_lastV, s_lastYaw;
        public static string Last => "action " + s_lastAct + (s_lastFree ? "" : " (busy)") + " v " + s_lastV.ToString("0.00") + " yaw " + s_lastYaw.ToString("0");
        NavMeshAgent m_agent;
        ObstacleAvoidanceType m_avoid;
        bool m_avoidSet;

        static bool s_logOn;
        static readonly List<string> s_log = new List<string>();

        // dash state
        static readonly float[] s_ready = new float[Charges];
        static bool s_aWas;
        static Vector3 s_dashDir;
        static float s_dashDist, s_dashTime, s_dashStart = -10f, s_dashMoved, s_dashEnd = -10f, s_pressAt = -10f;
        static bool s_dashBlink, s_dashLive, s_own;
        public static int OwnDashes;
        static Vector3 s_dashFrom;
        static string s_homed = "";
        static Targetable s_homedT;
        // the direction the stick pushed while a press waited for the game's dodge: the game begins its dodge only when A is
        // let go (a held A is its run) - Pad lets it go 0.12 s of game time after the press, 0.2-0.3 s of real time in a
        // counter's slow motion - and a stick already back in the centre by then sent the dash straight back (env 2,
        // 12 October: the dash toward the countered soldier went 2 m away from it)
        static Vector3 s_heldDir;
        static bool s_heldValid;
        static string s_dirWhy = "";   // for `dash?`: where the dash's way came from
        static float s_gazeYaw;
        static float s_homedRad;
        static ActionDodge s_dodge;
        public static int Dashes, Refused, Dodged, Homed, Failed, Nudges, Blocked, Shortened;
        static string s_plan = "";      // for `dash?`: how far the way was free when the dash began
        static bool s_capped;           // the way ends before the homed enemy: the dash stops there
        static float s_dashStartU;      // the dash's own clock (UYF's dash runs on unscaled time, as `_isUnscaledDeltaTime`)
        public static string LastDash = "-";
        static Controller.IgnoreDelegate s_ignore;
        static PlayerController s_ignoreOn;

        static AccessTools.FieldRef<ActionDodge, Vector3> s_dir, s_turnDir;
        static AccessTools.FieldRef<ActionDodge, float> s_dist;

        public static void Apply(Harmony h)
        {
            try
            {
                s_dir = AccessTools.FieldRefAccess<ActionDodge, Vector3>("m_direction");
                s_turnDir = AccessTools.FieldRefAccess<ActionDodge, Vector3>("m_turnDirection");
                s_dist = AccessTools.FieldRefAccess<ActionDodge, float>("m_distanceToTravel");
                h.Patch(AccessTools.Method(typeof(ActionDodge), "Begin"), postfix: new HarmonyMethod(typeof(CombatMove), nameof(DodgeBegin)));
                h.Patch(AccessTools.Method(typeof(ActionDodge), "MoveToProgress"), prefix: new HarmonyMethod(typeof(CombatMove), nameof(DodgeMove)));
                h.Patch(AccessTools.Method(typeof(ActionDodge), "Complete"), postfix: new HarmonyMethod(typeof(CombatMove), nameof(DodgeComplete)));
                // the roll's animation moves the hero too (root motion): not in a dash
                h.Patch(AccessTools.Method(typeof(ActionDodge), "OnDeltaMove"), prefix: new HarmonyMethod(typeof(CombatMove), nameof(DodgeRootMotion)));
            }
            catch (Exception e) { Plugin.L.LogWarning("CombatMove: dodge patches failed: " + e.Message); }
        }

        void Awake()
        {
            I = this;
            Diag.StatusExtras["loco"] = () => Diag.Q(CombatSettings.Walking + " stick " + m_stick.x.ToString("0.00") + "," + m_stick.y.ToString("0.00") +
                " v " + Velocity.magnitude.ToString("0.00") + " speed " + Speed.ToString("0.00") + " walking " + m_walking + " | " + Last);
            Diag.Commands["dash?"] = a => a.Length > 1 && a[1] == "near" ? Nearest() : a.Length > 2 && a[1] == "window" && TestMode.Enabled ? OpenWindow(a[2]) :
                                          a.Length > 1 && a[1] == "free" ? FreeAt(a.Length > 2 ? a[2] : "180") :
                                          a.Length > 1 && a[1] == "predict" ? PredictAt(a.Length > 2 && a[2].StartsWith("f")) :
                                          a.Length > 2 && a[1] == "edge" && TestMode.Enabled ? ToEdge(a[2], a.Length > 3 ? a[3] : "180") : "dashes " + Dashes + " (own " + OwnDashes + ", joined " + Joined + ") refused " + Refused + " dodged " + Dodged + " homed " + Homed + " charges " + ChargesLeft() + "/" + Charges +
                                          " style " + CombatSettings.Dash + " kind " + CombatSettings.DashKind + " flicks " + Flicks + " impacts " + Impacts + " fumbles " + Fumbles +
                                          " shockwaves " + Shockwaves + " failed " + Failed + " shortened " + Shortened + " blocked " + Blocked + " nudges " + Nudges +
                                          " clingRefused " + ClingRefused + " busyRefused " + BusyRefused + " (" + LastBusy + ") | last " + LastDash + " | impact " + LastImpact +
                                          " | push " + (s_impactPush != null ? s_impactPush.Moved.ToString("0.00") + " m of " + s_impactPush.Dist.ToString("0.0") : "-");
            // `movelog on|off|dump`: per frame: time, raw and filtered stick, the asked velocity, the hero's position and action
            Diag.Commands["movelog"] = a =>
            {
                string op = a.Length > 1 ? a[1] : "";
                if (op == "on") { s_log.Clear(); s_logOn = true; return "move log on"; }
                if (op == "off") { s_logOn = false; return "move log off (" + s_log.Count + " rows)"; }
                if (op == "dump")
                {
                    string dir = System.IO.Path.Combine(TestMode.Dir, "probes");
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "movelog.tsv"), "t\traw_x\traw_y\tstick_x\tstick_y\tvx\tvz\tx\ty\tz\taction\tdash\n" + string.Join("\n", s_log.ToArray()) + "\n");
                    return s_log.Count + " rows -> probes/movelog.tsv";
                }
                return "move log " + (s_logOn ? "on" : "off") + ", " + s_log.Count + " rows";
            };
            Diag.Commands["walk?"] = a => "mode " + CombatSettings.Walking + " speed " + CombatSettings.WalkSpeed + " stick " + m_stick + " v " + Velocity.magnitude.ToString("0.00") +
                                          " hero speed " + Speed.ToString("0.00") + " | " + Last;
        }

        /// Test seam `dash? window <s>`: the dodge window open for that long without a dash (does the window keep enemy
        /// strikes off the player?).
        static string OpenWindow(string secs)
        {
            float t = float.Parse(secs, System.Globalization.CultureInfo.InvariantCulture);
            s_pressAt = Time.time;
            s_dashEnd = Time.time + Mathf.Max(0f, t - DashWindow);
            return "dodge window open for " + t + " s (dodged so far " + Dodged + ")";
        }

        /// Test seam `dash? free [deg]`: the way from the hero `deg` from the gaze (180 = straight back): how far the floor goes
        /// (up to 12 m) and where a UYF dash that way (3 m, 6 m ahead) would land.
        static string FreeAt(string deg)
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null || VRRig.I == null) return "no hero";
            float d = float.Parse(deg, System.Globalization.CultureInfo.InvariantCulture);
            Vector3 dir = Quaternion.Euler(0f, VRRig.I.HeadWorldRot.eulerAngles.y + d, 0f) * Vector3.forward;
            bool edge;
            float free = FreeWay(pc.transform.position, dir, 12f, out edge);
            float want = Mathf.Abs(Mathf.DeltaAngle(0f, d)) <= 45f ? UyfForward : UyfBack;
            bool edge2;
            float gap = ObstacleGap(), free2 = FreeWay(pc.transform.position, dir, want + gap, out edge2);
            float land = edge2 ? Mathf.Max(0f, Mathf.Min(want, free2 - gap)) : want;
            return "free " + (edge ? free.ToString("0.00") : "12+") + " m at " + d.ToString("0") + " deg from the gaze; a dash of " + want.ToString("0") +
                   " m lands " + land.ToString("0.00") + " m out" + (land < UyfMinDash ? " (fails: under " + UyfMinDash.ToString("0.0") + " m)" : "") +
                   "; gap " + gap.ToString("0.00") + " m | " + FloorLast;
        }

        /// Test seam `dash? predict f|b`: where the flick's landing ring would stand (CombatMove.Predict), from the hero and
        /// from the gaze - with N2's turn along the edge in it.
        static string PredictAt(bool forward)
        {
            PlayerController pc = PlayerController.Instance;
            Vector3 at;
            if (pc == null || VRRig.I == null) return "no hero";
            if (!Predict(forward, out at)) return "no ring (that dash would fail)";
            Vector3 d = at - pc.transform.position;
            d.y = 0f;
            float deg = d.sqrMagnitude > 1e-4f ? Mathf.DeltaAngle(VRRig.I.HeadWorldRot.eulerAngles.y, Quaternion.LookRotation(d).eulerAngles.y) : 0f;
            return "ring " + d.magnitude.ToString("0.00") + " m out at " + deg.ToString("0") + " deg from the gaze";
        }

        /// Test seam `dash? edge <m> [deg]`: the hero moved along `deg` from the gaze (180 = back) - toward the floor's end or
        /// away from it - until it ends `m` metres away that way (a dash's run-in to a wall or the arena's end).
        static string ToEdge(string metres, string deg)
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null || VRRig.I == null) return "no hero";
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            float m = float.Parse(metres, inv), d = float.Parse(deg, inv);
            Vector3 dir = Quaternion.Euler(0f, VRRig.I.HeadWorldRot.eulerAngles.y + d, 0f) * Vector3.forward;
            bool edge;
            float free = FreeWay(pc.transform.position, dir, 30f, out edge);
            if (!edge) return "no edge within 30 m at " + d.ToString("0") + " deg";
            Vector3 to = pc.transform.position + dir * (free - m);   // toward the edge, or away from it when it is nearer
            NavMeshHit hit;
            if (!NavMesh.SamplePosition(to, out hit, 1f, NavMesh.AllAreas)) return "no floor there";
            NavMeshAgent ag = pc.GetComponent<NavMeshAgent>();
            if (ag != null && ag.enabled) ag.Warp(hit.position); else pc.transform.position = hit.position;
            float now = FreeWay(hit.position, dir, 30f, out edge);
            return "hero moved " + (free - m).ToString("0.00") + " m: the edge now " + now.ToString("0.00") + " m away at " + d.ToString("0") + " deg";
        }

        /// The nearest live enemy: its bearing from the gaze (deg, + = right), distance from the hero and body radius.
        static string Nearest()
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null || VRRig.I == null) return "no hero";
            Targetable me = pc.GetComponent<Targetable>();
            BetterList<Targetable> all = me.EnumerateWithinRadius(30f, me.GetEnemyTeams());
            Targetable best = null;
            float bd = float.MaxValue;
            for (int i = 0; i < all.size; i++)
            {
                Targetable t = all[i];
                Destroyable d = t != null ? t.GetComponent<Destroyable>() : null;
                if (t == null || (d != null && d.IsDead) || t.GetComponent<AIController>() == null) continue;
                Vector3 to = t.transform.position - pc.transform.position;
                to.y = 0f;
                if (to.magnitude < bd) { bd = to.magnitude; best = t; }
            }
            if (best == null) return "none";
            return "nearest " + WeaponGrips.ModelKey(best.name) + " bearing " + ThreatWarning.BearingOf(best.transform.position).ToString("0") + " dist " + bd.ToString("0.00") +
                   " radius " + BodyRadius(best).ToString("0.00");
        }

        static Vector3 s_walkedInGameAction;

        /// CombatVR: how far the player's own walking moved the hero during a game action since the last call.
        public static Vector3 ConsumeWalked()
        {
            Vector3 w = s_walkedInGameAction;
            s_walkedInGameAction = Vector3.zero;
            return w;
        }

        public static int ChargesLeft()
        {
            int n = 0;
            foreach (float t in s_ready) if (Time.time >= t) n++;
            return n;
        }

        static bool InFight => CombatVR.Active && GameWatch.InCombatPlay && PlayerController.Instance != null && VRRig.I != null && CombatVR.I != null;

        /// VRGamepad's combat mapping: what the game gets from the left stick and A this frame.
        public static void Pad(VRGamepad d, Hand l, Hand r)
        {
            if (CombatSettings.Walking == CombatSettings.WalkMode.Game || !InFight)
            {
                // as before 12 October: the game walks the hero; A = its dodge in the stick's direction (centred: back)
                d.LS = l.Stick;
                d.A = r.A;
                if (r.A && l.Stick.sqrMagnitude < 0.04f) d.LS = new Vector2(0f, -1f);
                return;
            }
            d.LS = Vector2.zero;   // the mod walks the hero (CombatMove.Update)
            // a won fight's exit waits for A held a second (FightExit): the press is not a dash then (MR2b: every leave began
            // with a 3 m dash back); the right stick's flick still dashes
            bool down = r.A && !s_aWas && !FightExit.Pending;
            s_aWas = r.A;
            // UYF's control: a flick of the right stick forward or back dashes that way (left and right still turn)
            int flick = 0;
            if (CombatSettings.DashFlick)
            {
                Vector2 rs = VRInput.R.Stick;
                if (Mathf.Abs(rs.y) < 0.3f) s_flickArmed = true;
                else if (s_flickArmed && Mathf.Abs(rs.y) >= 0.7f && Mathf.Abs(rs.y) > 1.5f * Mathf.Abs(rs.x)) { flick = rs.y > 0f ? 1 : -1; s_flickArmed = false; Flicks++; }
            }
            if ((down || flick != 0) && GnomeFight.Clinging)
            {
                // F8: a clinging gnome holds the hero: no dash (the ring breaks it)
                down = false;
                flick = 0;
                ClingRefused++;
                if (ClingRefused <= 20) Plugin.L.LogInfo("dash: refused - a gnome clings (the ring frees you)");
                Haptics.Cue(Haptics.CueKind.Refuse, false);
            }
            if (down || flick != 0)
            {
                bool tooSoon = CombatSettings.UyfDash && Time.time - s_dashStart < UyfGap;
                if (ChargesLeft() > 0 && !tooSoon)
                {
                    s_pressAt = Time.time;
                    s_pressPending = true;
                    s_heldValid = false;
                    s_flick = flick;
                }
                else if (!tooSoon)
                {
                    Refused++;
                    Haptics.Cue(Haptics.CueKind.Refuse, false);   // hand feel's "not now": two knocks, ramping down
                }
            }
            if (CombatSettings.UyfDash) return;   // UYF's dash runs on its own (Update): the game's dodge would make the hero safe
            // until the dash begins: the stick's direction, kept (the dash takes it if the stick is back in the centre by then)
            if (s_pressPending && I != null && l.Stick.magnitude >= Deadzone)
            {
                s_heldDir = I.StickDirection(l.Stick);
                s_heldValid = true;
            }
            // the game's dodge gets A (and a stick pointing the dash's way) for a few frames after a press with a charge
            if (s_pressPending && Time.time - s_pressAt < 0.12f)
            {
                d.A = true;
                d.LS = new Vector2(0f, 1f);
            }
        }

        static bool s_pressPending;
        static bool s_flickArmed = true;
        static int s_flick;           // the press was a right-stick flick: +1 forward, -1 back (from the gaze), 0 = A

        // ---------------------------------------------------------------------------------------- walking

        void Update()
        {
            if (!InFight || CombatSettings.Walking != CombatSettings.WalkMode.Direct)
            {
                m_stick = Vector2.zero;
                m_walking = false;
                Velocity = Vector3.zero;
                RestoreAvoidance();
                if (InFight && s_logOn && s_log.Count < 4000)
                {
                    // Walking = Game: the log still shows what the game's walk does
                    PlayerController hp = PlayerController.Instance;
                    Vector3 p0 = hp.transform.position;
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    s_log.Add(string.Format(ci, "{0:0.0000}\t{1:0.000}\t{2:0.000}\t0\t0\t0\t0\t{3:0.0000}\t{4:0.0000}\t{5:0.0000}\t{6}\t0", Time.unscaledTime, VRInput.L.Stick.x,
                        VRInput.L.Stick.y, p0.x, p0.y, p0.z, HeroAction(hp) is string ha && ha.Length > 0 ? ha : "-"));
                }
                return;
            }
            PlayerController pc = PlayerController.Instance;
            if (m_agent == null || m_agent.gameObject != pc.gameObject) { RestoreAvoidance(); m_agent = pc.GetComponent<NavMeshAgent>(); }
            if (m_agent != null && !m_avoidSet)
            {
                m_avoid = m_agent.obstacleAvoidanceType;
                m_agent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;   // nothing shoves the hero
                m_avoidSet = true;
            }
            EnsureIgnore(pc);
            float dt = Time.unscaledDeltaTime;
            if (Time.timeScale <= 0f || dt <= 0f) return;   // paused: nothing moves
            Vector2 raw = VRInput.L.Stick;
            float mag = raw.magnitude;
            float headYaw = VRRig.I.HeadWorldRot.eulerAngles.y;
            float bodyYaw = CombatVR.I.Yaw;
            if (mag < Deadzone)
            {
                m_stick = Vector2.zero;   // let go: stop on this frame
                m_walking = false;
            }
            else
            {
                Vector2 target = raw / mag * Mathf.Clamp01((mag - Deadzone) / (1f - Deadzone));
                if (!m_walking || CombatVR.SnapTurns != m_snaps)
                {
                    // the direction of the head's yaw when the stick leaves the centre (and after a snap turn)
                    m_relYaw = Mathf.DeltaAngle(bodyYaw, headYaw);
                    m_snaps = CombatVR.SnapTurns;
                    if (!m_walking) m_stick = target;   // start at once
                }
                m_walking = true;
                m_stick = Vector2.Lerp(m_stick, target, 1f - Mathf.Exp(-dt / Filter));
            }
            string busy = HeroAction(pc);
            // UYF's dash: at once, on its own (not through the game's dodge), unless the hero is down, stunned or snared
            if (CombatSettings.UyfDash && s_pressPending && !s_dashLive && s_dashStart < s_pressAt &&
                (busy == "" || busy == "ActionHit" || busy == "ActionIdle" || busy == "ActionMove"))
                StartDash(pc, null);
            // A pressed with a charge, but the game's dodge did not start (it does not during a hit reaction): the dash runs
            // on its own (the same move and dodge window), unless the hero is down, stunned or snared
            if (s_pressPending && !s_dashLive && Time.time - s_pressAt > (busy == "ActionHit" ? 0.1f : 0.25f) && s_dashStart < s_pressAt &&
                (busy == "" || busy == "ActionHit" || busy == "ActionIdle" || busy == "ActionMove"))
            {
                OwnDashes++;
                Plugin.L.LogInfo("dash: the game's dodge did not start (" + (busy.Length > 0 ? busy : "-") + "): own dash");
                StartDash(pc, null);
            }
            if (s_pressPending && Time.time - s_pressAt > 0.4f)
            {
                // the hero was down: no dash, no charge used (logged: F8's gnome test pressed A five times with no dash and
                // no word why)
                s_pressPending = false;
                BusyRefused++;
                LastBusy = busy.Length > 0 ? busy : "-";
                if (BusyRefused <= 20) Plugin.L.LogInfo("dash: A pressed, no dash - the hero busy (" + LastBusy + ")");
            }
            if (s_dashLive && s_own && !s_dashBlink && m_agent != null && m_agent.isOnNavMesh) Step(pc, m_agent, busy);
            Vector3 pos = pc.transform.position;
            Vector3 moved = pos - m_lastPos;
            moved.y = 0f;
            Speed = dt > 0f && Frames > 0 ? moved.magnitude / dt : 0f;
            Velocity = Vector3.zero;
            string act = HeroAction(pc);
            // free: idle, or a hit reaction (the body is not seen; the player keeps control, the game's push is dropped)
            bool free = act == "" || act == "ActionIdle" || act == "ActionMove" || act == "ActionHit";
            if (m_walking && free && !s_dashLive && !GnomeFight.Clinging && m_agent != null && m_agent.enabled && m_agent.isOnNavMesh)
            {
                float speed = CombatSettings.WalkSpeed;
                Vector2 s = m_stick;
                Vector3 local = new Vector3(s.x * 0.7f, 0f, s.y * (s.y >= 0f ? 1f : 0.5f)) * speed;   // UYF: sideways x0.7, back x0.5
                Velocity = Quaternion.Euler(0f, bodyYaw + m_relYaw, 0f) * local;
                // UYF's AutoCircleTarget (`[Combat] CircleTarget`, off as in UYF): the sideways part circles the faced enemy
                if (CombatSettings.CircleTarget && Mathf.Abs(s.x) > 0.02f)
                    Velocity = CircleVelocity(pc.transform.position, Velocity, Quaternion.Euler(0f, bodyYaw + m_relYaw, 0f) * new Vector3(s.x * 0.7f * speed, 0f, 0f),
                                              s.x > 0f ? 1f : -1f, Faced(pc, headYaw, 4f, 35f), dt);
                Vector3 before = pc.transform.position;
                m_agent.Move(Velocity * dt);
                // during a hit reaction the view does not follow the hero (CombatVR: the game's doing); the player's own
                // steps it does follow, through this
                if (act == "ActionHit") s_walkedInGameAction += pc.transform.position - before;
                Steps++;
            }
            if (free)
            {
                // the hero faces the gaze while it is free: its attacks without a target go where the player looks
                Quaternion want = Quaternion.Euler(0f, headYaw, 0f);
                pc.transform.rotation = Quaternion.RotateTowards(pc.transform.rotation, want, 540f * dt);
            }
            Push(pc, dt);
            Pull(pc, dt);
            m_lastPos = pc.transform.position;
            Frames++;
            if (s_logOn && s_log.Count < 4000)
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                Vector3 p = pc.transform.position;
                s_log.Add(string.Format(inv, "{0:0.0000}\t{1:0.000}\t{2:0.000}\t{3:0.000}\t{4:0.000}\t{5:0.000}\t{6:0.000}\t{7:0.0000}\t{8:0.0000}\t{9:0.0000}\t{10}\t{11}",
                    Time.unscaledTime, raw.x, raw.y, m_stick.x, m_stick.y, Velocity.x, Velocity.z, p.x, p.y, p.z, act.Length > 0 ? act : "-", s_dashLive ? 1 : 0));
            }
            s_lastAct = act.Length > 0 ? act : "-";
            s_lastFree = free;
            s_lastV = Velocity.magnitude;
            s_lastYaw = bodyYaw + m_relYaw;
        }

        public static int Circles;
        public static string LastCircle = "-";

        /// UYF's AutoCircleTarget (docs/uyf-parity-audit.md C6; its WorldMover, off by default): round the enemy `t` (faced
        /// within 35 deg and 4 m), the walk's sideways part `side` (world; `sign` +1 = the stick to the right) becomes a move
        /// along the circle at the same speed (the distance kept), blended in from 4 m (none) to 2.5 m (all). `v` = the whole
        /// walk; returns it with that part swapped. (Unity: AngleAxis(+a, up) turns +z toward +x - the tangent Cross(up, r).)
        static Vector3 CircleVelocity(Vector3 pos, Vector3 v, Vector3 side, float sign, Targetable t, float dt)
        {
            try
            {
                if (t == null || dt <= 0f) { LastCircle = "no enemy faced within 4 m"; return v; }
                Vector3 r = pos - t.transform.position;
                r.y = 0f;
                float d = r.magnitude;
                float k = Mathf.InverseLerp(4f, 2.5f, d);
                if (d < 0.3f || k <= 0f) { LastCircle = WeaponGrips.ModelKey(t.name) + " " + d.ToString("0.00") + " m: beyond the blend"; return v; }
                // the arc's way: the tangent that points along the straight side step
                Vector3 tangent = Vector3.Cross(Vector3.up, r / d);
                float ang = side.magnitude * dt / d;   // radians this frame
                if (Vector3.Dot(tangent, side) < 0f) ang = -ang;
                Vector3 next = Quaternion.AngleAxis(ang * Mathf.Rad2Deg, Vector3.up) * r;
                Vector3 arc = (next - r) / dt;
                Circles++;
                LastCircle = WeaponGrips.ModelKey(t.name) + " " + d.ToString("0.00") + " m, blend " + k.ToString("0.00") + (sign > 0f ? ", right" : ", left");
                return v - side + Vector3.Lerp(side, arc, k);
            }
            catch (Exception) { return v; }
        }

        /// The nearest enemy within `dist` m and `deg` of the gaze's yaw.
        static Targetable Faced(PlayerController pc, float headYaw, float dist, float deg)
        {
            Targetable me = pc.GetComponent<Targetable>();
            if (me == null) return null;
            Vector3 gaze = Quaternion.Euler(0f, headYaw, 0f) * Vector3.forward;
            BetterList<Targetable> near = me.EnumerateWithinRadius(dist, me.GetEnemyTeams());
            Targetable best = null;
            float bd = float.MaxValue;
            for (int i = 0; i < near.size; i++)
            {
                Targetable t = near[i];
                Destroyable dd = t != null ? t.GetComponent<Destroyable>() : null;
                if (t == null || (dd != null && dd.IsDead) || t.Controller == null) continue;
                Vector3 to = t.transform.position - pc.transform.position;
                to.y = 0f;
                if (to.sqrMagnitude < 1e-4f || Vector3.Angle(gaze, to) > deg) continue;
                if (to.magnitude < bd) { bd = to.magnitude; best = t; }
            }
            return best;
        }

        /// `vrcircle probe`: one second of a full right push from here, worked out without moving (the enemy held where it
        /// is): the distance to the faced enemy after it, round the circle and in a straight side step, and the angle walked
        /// round it.
        public static string CircleProbe()
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null || VRRig.I == null) return "no hero";
            float headYaw = VRRig.I.HeadWorldRot.eulerAngles.y, speed = CombatSettings.WalkSpeed, dt = 1f / 72f;
            Targetable t = Faced(pc, headYaw, 4f, 35f);
            if (t == null) return "circle " + CombatSettings.CircleTarget + " | no enemy faced within 4 m and 35 deg";
            Vector3 side = Quaternion.Euler(0f, headYaw, 0f) * new Vector3(0.7f * speed, 0f, 0f);
            Vector3 c = t.transform.position, p = pc.transform.position, q = p;
            c.y = p.y = q.y = 0f;
            int n0 = Circles;
            for (int i = 0; i < 72; i++)
            {
                Vector3 v = CombatSettings.CircleTarget ? CircleVelocity(new Vector3(p.x, t.transform.position.y, p.z), side, side, 1f, t, dt) : side;
                v.y = 0f;
                p += v * dt;
                q += side * dt;
            }
            Circles = n0;
            Vector3 r0 = pc.transform.position - t.transform.position;
            r0.y = 0f;
            return "circle " + CombatSettings.CircleTarget + " | " + WeaponGrips.ModelKey(t.name) + " at " + r0.magnitude.ToString("0.00") + " m; after 1 s: round it " +
                   (p - c).magnitude.ToString("0.00") + " m (" + Vector3.Angle(r0, p - c).ToString("0") + " deg round), straight " + (q - c).magnitude.ToString("0.00") + " m | " + LastCircle;
        }

        static Type s_haType;
        static string s_haName = "";

        /// The hero's running lower-body action's type name ("" for none; the name kept per type: Type.Name makes a new
        /// string on every call, and this runs several times a frame).
        static string HeroAction(PlayerController pc)
        {
            Actor actor = pc.GetComponent<Actor>();
            Defiant.Action a = actor != null ? actor.RunningActionLowerBody : null;
            if (a == null) return "";
            Type t = a.GetType();
            if (t != s_haType) { s_haType = t; s_haName = t.Name; }
            return s_haName;
        }

        void RestoreAvoidance()
        {
            if (m_avoidSet && m_agent != null) m_agent.obstacleAvoidanceType = m_avoid;
            m_avoidSet = false;
            if (s_ignoreOn != null && !CombatVR.Active)
            {
                try { s_ignoreOn.OnIgnore = (Controller.IgnoreDelegate)Delegate.Remove(s_ignoreOn.OnIgnore, s_ignore); } catch (Exception) { }
                s_ignoreOn = null;
            }
        }

        // ---------------------------------------------------------------------------------------- the dash

        /// The world direction of a stick push: the walking direction while walking, else relative to the gaze.
        Vector3 StickDirection(Vector2 raw)
        {
            float yaw = m_walking ? CombatVR.I.Yaw + m_relYaw : VRRig.I.HeadWorldRot.eulerAngles.y;
            return Quaternion.Euler(0f, yaw, 0f) * new Vector3(raw.x, 0f, raw.y).normalized;
        }

        /// The world direction a dash takes now: the walking direction while the stick is held, the stick's push since the
        /// press if it was let go before the dash began, else straight back from the gaze.
        Vector3 DashDirection(out bool forward)
        {
            float headYaw = VRRig.I.HeadWorldRot.eulerAngles.y;
            Vector2 raw = VRInput.L.Stick;
            Vector3 dir;
            if (s_flick != 0)
            {
                dir = Quaternion.Euler(0f, headYaw, 0f) * (s_flick > 0 ? Vector3.forward : Vector3.back);
                s_dirWhy = s_flick > 0 ? "flick forward" : "flick back";
                s_flick = 0;
                s_gazeYaw = headYaw;
                s_heldValid = false;
                forward = s_dirWhy == "flick forward";
                return dir;
            }
            if (raw.magnitude >= Deadzone) dir = StickDirection(raw);
            else if (s_heldValid) dir = s_heldDir;   // let go before the game's dodge began: the way it pushed after the press
            else dir = Quaternion.Euler(0f, headYaw, 0f) * Vector3.back;
            s_dirWhy = raw.magnitude >= Deadzone ? "stick" : s_heldValid ? "kept" : "back";
            s_gazeYaw = headYaw;
            s_heldValid = false;
            Vector3 gaze = Quaternion.Euler(0f, headYaw, 0f) * Vector3.forward;
            forward = Vector3.Angle(dir, gaze) <= 45f;
            return dir;
        }

        static void DodgeBegin(ActionDodge __instance, bool __result)
        {
            if (!__result || I == null || CombatSettings.Walking != CombatSettings.WalkMode.Direct || !InFight) return;
            PlayerController pc = PlayerController.Instance;
            if (__instance.Actor == null || __instance.Actor.gameObject != pc.gameObject) return;
            if (s_own && (s_dashLive || Time.time - s_dashStart < 0.4f))
            {
                // the dash for this press already runs on its own: the game's dodge (its duck window, the enemies' reaction)
                // joins it without a second charge or a second move
                s_dodge = __instance;
                s_dir(__instance) = s_dashDir;
                s_turnDir(__instance) = s_dashDir;
                s_dist(__instance) = 0f;
                Joined++;
                return;
            }
            StartDash(pc, __instance);
        }

        public static int Joined;

        /// A dash: through the game's dodge (`dodge`), or on its own when the game can't start one (a hit reaction).
        static void StartDash(PlayerController pc, ActionDodge dodge)
        {
            s_pressPending = false;
            try
            {
                // use a charge
                int best = 0;
                for (int i = 1; i < Charges; i++) if (s_ready[i] < s_ready[best]) best = i;
                float was = s_ready[best];
                s_ready[best] = Mathf.Max(Time.time, s_ready[best]) + ChargeTime;
                bool forward;
                Vector3 dir = I.DashDirection(out forward);
                float dist = DashDistance, time = DashTime;
                s_homed = "";
                s_homedT = null;
                s_uyf = CombatSettings.UyfDash;
                s_homeGap = s_uyf ? UyfEndGap : 0.9f;
                s_homeCap = s_uyf ? 9.5f / UyfSpeed : 0.25f;
                if (s_uyf)
                {
                    // UYF: 6 m forward (homing on an enemy in its arcs, ending at it), else 3 m, at 32 m/s
                    dist = forward ? UyfForward : UyfBack;
                    time = dist / UyfSpeed;
                    if (forward)
                    {
                        Targetable bestT = HomeUyf(pc, dir);
                        if (bestT != null)
                        {
                            Vector3 to = bestT.transform.position - pc.transform.position;
                            to.y = 0f;
                            float rad = BodyRadius(bestT);
                            float travel = Mathf.Max(0f, to.magnitude - rad - s_homeGap);
                            dir = to.normalized;
                            dist = travel;
                            time = Mathf.Max(0.04f, travel / UyfSpeed);
                            s_homed = WeaponGrips.ModelKey(bestT.name) + " " + to.magnitude.ToString("0.0") + " m away";
                            s_homedT = bestT;
                            s_homedRad = rad;
                            Homed++;
                        }
                    }
                    forward = false;   // (the short dash's homing below is not UYF's)
                    // UYF's landing: 1 m before whatever stands in the way (the floor's end, a wall, a prop); with less than
                    // 0.5 m of room and no enemy to reach, the dash fails: no move, no charge, a dull knock (env 1's fights:
                    // a back dash planned 3 m pressed into the arena's end and covered 0.4-1.3 m, its ring beyond the end)
                    float gap = ObstacleGap();
                    bool edge;
                    s_uyfWant = dist;
                    float free = FreeWay(pc.transform.position, dir, dist + gap, out edge);
                    s_capped = false;
                    s_plan = edge ? "the floor ends " + free.ToString("0.00") + " m out" : "free";
                    if (edge && free - gap < dist - 0.005f)
                    {
                        Shortened++;
                        s_plan += ": " + dist.ToString("0.00") + " -> " + Mathf.Max(0f, free - gap).ToString("0.00") + " m";
                        dist = Mathf.Max(0f, free - gap);
                        s_capped = s_homedT != null;
                        time = Mathf.Max(s_homedT != null ? 0.04f : 0f, dist / UyfSpeed);
                    }
                    // N2 (the player's fights 2026-10-17: "the floor ends 0.00-0.96 m out" again and again, nearly all back
                    // dashes pressed at the arena's end - the navmesh stops there, the ground looks open): with no room the
                    // way it was pressed, the dash turns along the edge to the nearest way with room (30-90 deg either side),
                    // rather than doing nothing
                    if (dist < UyfMinDash && s_homedT == null)
                    {
                        string how;
                        if (EdgeTurn(pc.transform.position, ref dir, ref dist, s_uyfWant, gap, out how))
                        {
                            EdgeTurns++;
                            s_plan += "; " + how;
                            time = Mathf.Max(0.04f, dist / UyfSpeed);
                        }
                    }
                    if (dist < UyfMinDash && s_homedT == null)
                    {
                        s_ready[best] = was;
                        Failed++;
                        LastDash = "failed: " + s_plan + " (under " + UyfMinDash.ToString("0.0") + " m of room), charges left " + ChargesLeft() + ", way " + s_dirWhy + " " +
                                   Mathf.DeltaAngle(s_gazeYaw, Quaternion.LookRotation(dir).eulerAngles.y).ToString("0") + " deg from the gaze";
                        Plugin.L.LogInfo("dash: " + LastDash);
                        Haptics.Cue(Haptics.CueKind.Refuse, false);
                        CameraShake.Add(0.1f, 0.1f, 0.1f, "dash failed");
                        CombatSounds.Layer(CombatSounds.Thump, VRRig.I.HeadWorldPos + dir * 0.4f, 0.35f, 0.6f);
                        return;
                    }
                    NudgeAside(pc, dir, dist, s_homedT);
                }
                else s_plan = "";
                if (forward)
                {
                    // homing: an enemy within 6 m and 35 deg of the dash: end at blade range, up to 0.25 s
                    Targetable me = pc.GetComponent<Targetable>();
                    BetterList<Targetable> near = me.EnumerateWithinRadius(6.5f, me.GetEnemyTeams());
                    Targetable bestT = null;
                    float bestScore = float.MaxValue, bestRad = 0.4f;
                    for (int i = 0; i < near.size; i++)
                    {
                        Targetable t = near[i];
                        Destroyable d = t != null ? t.GetComponent<Destroyable>() : null;
                        if (t == null || (d != null && d.IsDead)) continue;
                        Vector3 to = t.transform.position - pc.transform.position;
                        to.y = 0f;
                        float ang = Vector3.Angle(dir, to);
                        if (to.magnitude > 6f || ang > 35f) continue;
                        float score = to.magnitude + ang * 0.05f;
                        if (score < bestScore) { bestScore = score; bestT = t; bestRad = BodyRadius(t); }
                    }
                    if (bestT != null)
                    {
                        Vector3 to = bestT.transform.position - pc.transform.position;
                        to.y = 0f;
                        float travel = Mathf.Max(0f, to.magnitude - bestRad - s_homeGap);
                        dir = to.normalized;
                        dist = travel;
                        time = Mathf.Clamp(travel / (DashDistance / DashTime), 0.06f, 0.25f);
                        s_homed = WeaponGrips.ModelKey(bestT.name) + " " + to.magnitude.ToString("0.0") + " m away";
                        s_homedT = bestT;
                        s_homedRad = bestRad;
                        Homed++;
                    }
                }
                if (dodge != null)
                {
                    s_dir(dodge) = dir;
                    s_turnDir(dodge) = dir;
                    s_dist(dodge) = dist;
                }
                s_dashDir = dir;
                s_dashDist = dist;
                s_dashTime = time;
                s_dashStart = Time.time;
                s_dashStartU = s_uyf ? Time.unscaledTime : Time.time;
                s_dashMoved = 0f;
                s_dashFrom = pc.transform.position;
                s_dashEnd = Time.time + time;
                s_dashLive = true;
                s_dodge = dodge;
                s_own = dodge == null;
                s_dashBlink = CombatSettings.Dash == CombatSettings.DashStyle.Blink;
                Dashes++;
                GnomeFight.Dashed();   // F8: a gnome in the air and still away misses
                DashMarker.Show(pc.transform.position + dir * dist, time + 0.4f);
                // feedback: a whoosh and a double tick
                Haptics.Pulse(true, 1f, 0.015f);
                Haptics.Pulse(false, 1f, 0.015f);
                I.Invoke(nameof(SecondTick), 0.07f);
                CombatSounds.PlayAt(CombatSounds.Whoosh, VRRig.I.HeadWorldPos + dir * 0.3f, 0.6f);
                if (s_dashBlink)
                {
                    // Blink: the whole move while the view is dark for a moment
                    NavMeshAgent agent = pc.GetComponent<NavMeshAgent>();
                    if (VRFade.I != null && agent != null)
                        VRFade.I.QuickBlink(() =>
                        {
                            if (agent != null && agent.isOnNavMesh) agent.Move(s_dashDir * s_dashDist);
                            s_dashMoved = s_dashDist;
                            if (PlayerController.Instance != null) Finish(PlayerController.Instance);
                        });
                }
                else Comfort.DashUntil = Time.unscaledTime + time + 0.05f;
                Comfort.DashPeak = 0f;
            }
            catch (Exception e) { Plugin.L.LogWarning("CombatMove: dash: " + e.Message); }
        }

        void SecondTick()
        {
            Haptics.Pulse(true, 1f, 0.012f);
            Haptics.Pulse(false, 1f, 0.012f);
        }

        /// Half the width of an enemy's body: its collider, else its agent.
        static float BodyRadius(Targetable t)
        {
            try
            {
                Collider c = t.Shape != null ? t.Shape.GetComponent<Collider>() : null;
                if (c is CapsuleCollider) return ((CapsuleCollider)c).radius * Mathf.Max(c.transform.lossyScale.x, c.transform.lossyScale.z);
                if (c != null) return Mathf.Max(c.bounds.extents.x, c.bounds.extents.z);
                NavMeshAgent a = t.GetComponent<NavMeshAgent>();
                if (a != null) return a.radius;
            }
            catch (Exception) { }
            return 0.4f;
        }

        /// The dodge's motion each frame: the Dash at a constant speed instead of the roll's curve.
        static bool DodgeMove(ActionDodge __instance)
        {
            if (__instance != s_dodge) return true;
            if (!s_dashLive || s_dashBlink || s_own) return false;   // an own dash moves in Update
            PlayerController pc = PlayerController.Instance;
            NavMeshAgent agent = pc != null ? pc.GetComponent<NavMeshAgent>() : null;
            if (agent == null || !agent.isOnNavMesh) return false;
            Step(pc, agent, null);
            return false;
        }

        /// One frame of a dash: a constant speed along its direction; a homing dash re-aims at its enemy every frame (it
        /// moves) and stops at blade range, at the latest after 0.25 s.
        static void Step(PlayerController pc, NavMeshAgent agent, string busy)
        {
            Vector3 before = pc.transform.position;
            // UYF's dash keeps its pace in slow motion (unscaled time); the short dash runs with the game's dodge (game time)
            float now = s_uyf ? Time.unscaledTime : Time.time, dt = s_uyf ? Time.unscaledDeltaTime : Time.deltaTime;
            if (s_homedT != null)
            {
                float speed = s_dashDist / Mathf.Max(0.05f, s_dashTime);
                if (s_uyf) speed = UyfSpeed;
                Vector3 to = s_homedT.transform.position - pc.transform.position;
                to.y = 0f;
                float left = to.magnitude - s_homedRad - s_homeGap;
                if (s_capped) left = Mathf.Min(left, s_dashDist - s_dashMoved);   // the way ends before the enemy
                float step = Mathf.Min(Mathf.Max(0f, left), speed * dt);
                if (step > 0f && to.sqrMagnitude > 1e-4f) agent.Move(to.normalized * step);
                s_dashMoved += step;
                if (left - step <= 0.01f || now - s_dashStartU >= s_homeCap) Finish(pc);
            }
            else
            {
                float k = s_dashTime > 0f ? Mathf.Clamp01((now - s_dashStartU) / s_dashTime) : 1f;
                float step = k * s_dashDist - s_dashMoved;
                if (step > 0f) agent.Move(s_dashDir * step);
                s_dashMoved = k * s_dashDist;
                // UYF: a move that is stopped dead ends the dash where it is (DASH_MOVE_BLOCKED_THRESHOLD)
                Vector3 went = pc.transform.position - before;
                went.y = 0f;
                if (s_uyf && step > 0.002f && went.magnitude < 0.0001f) { Blocked++; s_plan += (s_plan.Length > 0 ? ", " : "") + "blocked"; Finish(pc); }
                else if (k >= 1f) Finish(pc);
            }
            if (busy == "ActionHit") s_walkedInGameAction += pc.transform.position - before;
        }

        static void Finish(PlayerController pc)
        {
            if (!s_dashLive) return;
            s_dashLive = false;
            s_dashEnd = Time.time;
            Vector3 moved = pc.transform.position - s_dashFrom;
            moved.y = 0f;
            string gap = "";
            if (s_homedT != null)
            {
                Vector3 to = s_homedT.transform.position - pc.transform.position;
                to.y = 0f;
                gap = ", ended " + (to.magnitude - s_homedRad).ToString("0.00") + " m from its body";
            }
            LastDash = (s_dashBlink ? "blink " : "slide ") + moved.magnitude.ToString("0.00") + " m of " + s_dashDist.ToString("0.00") + " in " + ((s_uyf ? Time.unscaledTime : Time.time) - s_dashStartU).ToString("0.000") +
                       " s" + (s_own ? " (own)" : "") + (s_plan.Length > 0 ? ", " + s_plan : "") + (s_homed.Length > 0 ? ", homed on " + s_homed + gap : "") + (s_dashBlink ? "" : ", tunnel " + Comfort.DashPeak.ToString("0.00")) +
                       ", charges left " + ChargesLeft() +
                       (s_dashDir.sqrMagnitude > 1e-6f ? ", way " + s_dirWhy + " " + Mathf.DeltaAngle(s_gazeYaw, Quaternion.LookRotation(s_dashDir).eulerAngles.y).ToString("0") + " deg from the gaze" : "");
            Plugin.L.LogInfo("dash: " + LastDash);
            if (s_uyf && s_homedT != null)
            {
                Vector3 to = s_homedT.transform.position - pc.transform.position;
                to.y = 0f;
                if (to.magnitude - s_homedRad <= s_homeGap + 0.2f) Impact(pc, s_homedT, moved.magnitude, to.sqrMagnitude > 1e-4f ? to.normalized : s_dashDir);
            }
        }

        static bool s_uyf;
        static float s_homeGap = 0.9f, s_homeCap = 0.25f;
        static EnemyPush.Push s_impactPush;   // the last impact's push (dash?: how far it really went)

        /// Where a flick of the right stick (forward or back from the gaze) would land now: on the enemy it would home on, else
        /// 6 m ahead or 3 m back, stopped by the navmesh's edge (DashMarker's preview).
        public static bool Predict(bool forward, out Vector3 at)
        {
            at = Vector3.zero;
            PlayerController pc = PlayerController.Instance;
            if (pc == null || VRRig.I == null) return false;
            Vector3 from = pc.transform.position;
            Vector3 dir = Quaternion.Euler(0f, VRRig.I.HeadWorldRot.eulerAngles.y, 0f) * (forward ? Vector3.forward : Vector3.back);
            float dist = forward ? UyfForward : UyfBack;
            if (forward)
            {
                Targetable t = HomeUyf(pc, dir);
                if (t != null)
                {
                    Vector3 to = t.transform.position - from;
                    to.y = 0f;
                    dir = to.normalized;
                    dist = Mathf.Max(0f, to.magnitude - BodyRadius(t) - UyfEndGap);
                }
            }
            bool edge;
            float gap = ObstacleGap(), free = FreeWay(from, dir, dist + gap, out edge);
            float want = dist;
            if (edge) dist = Mathf.Max(0f, Mathf.Min(dist, free - gap));
            bool homing = forward && HomeUyf(pc, dir) != null;
            string how;
            if (dist < UyfMinDash && !homing) EdgeTurn(from, ref dir, ref dist, want, gap, out how);   // N2: the ring shows the turn too
            if (dist < UyfMinDash && !homing) return false;   // that dash would fail: no ring
            at = from + dir * dist;
            return true;
        }

        /// How far the floor goes from `from` along `dir` (up to `look` m): to the navmesh's edge - a wall, a prop, the arena's
        /// end (HoF2's counterpart of UYF's obstacle sphere cast). `edge` false: nothing within `look`, or no floor under the
        /// hero to tell from (then the dash keeps its length, as before).
        static float FreeWay(Vector3 from, Vector3 dir, float look, out bool edge)
        {
            edge = false;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) return look;
            dir.Normalize();
            // N2 (the player's fights 2026-10-17: "the floor ends 0.00 m out" again and again): the floor is the hero's own
            // navmesh (its agent type and areas) from the agent's own place. Measured: both were already right (agent type 0,
            // the hero on its mesh) - the mesh really ends there; the fix is EdgeTurn. The old query stays for the log.
            NavMeshQueryFilter f = HeroFilter();
            NavMeshHit hit;
            // the hero's place on its navmesh: the agent's own (nextPosition) when it has one - its transform can stand off
            // the mesh (root motion, a push), and the nearest mesh point to it then lies on the mesh's edge, where every way
            // outward "ends" at 0.00 m
            Vector3 origin = from;
            string onMesh = "no agent";
            try
            {
                NavMeshAgent ag = PlayerController.Instance != null ? PlayerController.Instance.GetComponent<NavMeshAgent>() : null;
                if (ag != null && ag.enabled && ag.isOnNavMesh && (ag.nextPosition - from).sqrMagnitude < 4f) { origin = ag.nextPosition; onMesh = "agent on its mesh"; }
                else if (ag != null) onMesh = "agent " + (ag.enabled ? "off its mesh" : "disabled");
            }
            catch (Exception) { }
            if (!NavMesh.SamplePosition(origin, out hit, 1f, f)) { FloorLast = "no floor of the hero's within 1 m (" + onMesh + ")"; return look; }
            Vector3 start = hit.position;
            Vector3 offT = start - from;
            offT.y = 0f;
            FloorOff = offT.magnitude;
            float own = look;
            bool ownEdge = NavMesh.Raycast(start, start + dir * look, out hit, f);
            if (ownEdge) { Vector3 d = hit.position - start; d.y = 0f; own = d.magnitude; }
            // the old query, for the log (where the two disagree, the old one was the bug)
            float old = look;
            NavMeshHit h2;
            bool oldEdge = false;
            if (NavMesh.SamplePosition(from, out h2, 1f, NavMesh.AllAreas))
            {
                Vector3 s0 = h2.position;
                oldEdge = NavMesh.Raycast(s0, s0 + dir * look, out h2, NavMesh.AllAreas);
                if (oldEdge) { Vector3 d = h2.position - s0; d.y = 0f; old = d.magnitude; }
            }
            FloorLast = onMesh + ", the hero " + FloorOff.ToString("0.00") + " m from its mesh point; hero's mesh (agent type " + f.agentTypeID + ") " + (ownEdge ? own.ToString("0.00") : look.ToString("0") + "+") + " m, the default query " +
                        (oldEdge ? old.ToString("0.00") : look.ToString("0") + "+") + " m";
            if (ownEdge != oldEdge || Mathf.Abs(own - old) > 0.05f)
            {
                FloorDisagree++;
                if (FloorDisagree <= 20) Plugin.L.LogInfo("CombatMove: the floor check: " + FloorLast + " (" + look.ToString("0.0") + " m looked)");
            }
            edge = ownEdge;
            return own;
        }

        public static int FloorDisagree, EdgeTurns, ClingRefused, BusyRefused;
        public static string LastBusy = "-";
        static float s_uyfWant;

        /// N2 (the player's fights 2026-10-17: "the floor ends 0.00-0.96 m out" again and again, nearly all back dashes pressed
        /// at the arena's end - its navmesh stops there though the ground looks open): with under UyfMinDash of room the way it
        /// was pressed, the dash turns along the edge - the first of 30 / 60 / 90 deg either side with min(want, 1.5) m of
        /// room, else the roomiest over UyfMinDash. The dash and its landing ring both use it. False = no way with room.
        static bool EdgeTurn(Vector3 from, ref Vector3 dir, ref float dist, float want, float gap, out string how)
        {
            how = "";
            float bestRoom = 0f, bestTurn = 0f;
            Vector3 bestDir = dir;
            foreach (float turn in s_edgeTurns)
            {
                Vector3 d2 = Quaternion.Euler(0f, turn, 0f) * dir;
                bool e2;
                float f2 = FreeWay(from, d2, want + gap, out e2);
                float room = Mathf.Min(want, e2 ? Mathf.Max(0f, f2 - gap) : want);
                if (room >= Mathf.Min(want, 1.5f)) { bestRoom = room; bestDir = d2; bestTurn = turn; break; }
                if (room > bestRoom) { bestRoom = room; bestDir = d2; bestTurn = turn; }
            }
            if (bestRoom < UyfMinDash) return false;
            dir = bestDir;
            dist = bestRoom;
            how = "turned " + bestTurn.ToString("0") + " deg along the edge (" + bestRoom.ToString("0.00") + " m of room)";
            return true;
        }
        static readonly float[] s_edgeTurns = { 30f, -30f, 60f, -60f, 90f, -90f };
        public static float FloorOff;
        public static string FloorLast = "-";

        static NavMeshQueryFilter HeroFilter()
        {
            NavMeshQueryFilter f = new NavMeshQueryFilter { areaMask = NavMesh.AllAreas, agentTypeID = 0 };
            try
            {
                NavMeshAgent a = PlayerController.Instance != null ? PlayerController.Instance.GetComponent<NavMeshAgent>() : null;
                if (a != null) { f.agentTypeID = a.agentTypeID; f.areaMask = a.areaMask; }
            }
            catch (Exception) { }
            return f;
        }

        /// UYF's 1 m from an obstacle, counted from the navmesh's edge (which lies the hero's radius off the obstacle).
        static float ObstacleGap()
        {
            NavMeshAgent a = PlayerController.Instance != null ? PlayerController.Instance.GetComponent<NavMeshAgent>() : null;
            float r = a != null ? Mathf.Clamp(a.radius, 0f, 0.9f) : 0.3f;
            return UyfObstacleGap - r;
        }

        /// UYF's MoveEnemies: the enemies in the dash's way - their bodies within 0.3 m of its line, the one it homes on aside -
        /// are nudged 1 m out of it, sideways, at 30 m/s (PushBackDashNudge), so the dash passes them.
        static void NudgeAside(PlayerController pc, Vector3 dir, float dist, Targetable target)
        {
            try
            {
                dir.y = 0f;
                if (dir.sqrMagnitude < 1e-6f || dist <= 0f) return;
                dir.Normalize();
                Targetable me = pc.GetComponent<Targetable>();
                BetterList<Targetable> near = me.EnumerateWithinRadius(dist + 2f, me.GetEnemyTeams());
                Vector3 from = pc.transform.position;
                for (int i = 0; i < near.size; i++)
                {
                    Targetable o = near[i];
                    if (o == null || o == target || !(o.Controller is AIController)) continue;
                    Destroyable d = o.GetComponent<Destroyable>();
                    if (d != null && d.IsDead) continue;
                    Vector3 rel = o.transform.position - from;
                    rel.y = 0f;
                    float along = Vector3.Dot(rel, dir), rad = BodyRadius(o);
                    if (along < -rad || along > dist + rad + UyfPathRadius) continue;
                    Vector3 side = rel - dir * along;
                    if (side.magnitude >= UyfPathRadius + rad) continue;
                    if (side.sqrMagnitude < 1e-4f) side = Vector3.Cross(Vector3.up, dir);   // right on the line: to the dash's right
                    EnemyPush.Start((AIController)o.Controller, side.normalized, UyfNudge, UyfNudge / UyfNudgeRate);
                    Nudges++;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("CombatMove: dash nudge: " + e.Message); }
        }

        /// UYF's homing: the best enemy in the forward dash's arcs (50 deg within 3 m, 35 deg within 6 m, 25 deg within 9 m).
        static Targetable HomeUyf(PlayerController pc, Vector3 dir)
        {
            Targetable me = pc.GetComponent<Targetable>();
            BetterList<Targetable> near = me.EnumerateWithinRadius(9.5f, me.GetEnemyTeams());
            Targetable best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < near.size; i++)
            {
                Targetable t = near[i];
                Destroyable d = t != null ? t.GetComponent<Destroyable>() : null;
                if (t == null || (d != null && d.IsDead) || t.Controller == null) continue;
                Vector3 to = t.transform.position - pc.transform.position;
                to.y = 0f;
                float dist = to.magnitude, ang = Vector3.Angle(dir, to);
                bool inArc = false;
                for (int k = 0; k < s_arcDist.Length; k++) if (dist <= s_arcDist[k] && ang <= s_arcDeg[k]) { inArc = true; break; }
                if (!inArc) continue;
                float score = dist + ang * 0.05f;
                if (score < bestScore) { bestScore = score; best = t; }
            }
            return best;
        }

        /// UYF's dash reaches its enemy: its guard worn (10 x 0.3-1.25 by the distance dashed, 1-6 m), it is pushed 3 m back
        /// and the others within 2 m of it are knocked out to 2 m; an attack it began less than 0.25 s ago falls apart (it
        /// fumbles for 1.25 s: UYF 1-1.5 s).
        static void Impact(PlayerController pc, Targetable t, float moved, Vector3 dir)
        {
            try
            {
                Impacts++;
                float k = Mathf.Lerp(0.3f, 1.25f, Mathf.InverseLerp(1f, 6f, moved));
                Poise.DashHit(t, 10f * k);
                AIController ai = t.Controller as AIController;
                string fumble = "";
                float age = Guard.StrikeAge(t.Controller);
                if (age >= 0f && age <= 0.25f)
                {
                    Defiant.Action run = Guard.RunningStrikeOf(t.Controller);
                    try { if (run != null && run.IsRunning) run.Complete(); } catch (Exception) { }
                    Guard.DropStrike(t.Controller);
                    Guard.Hold(t.Controller, 1.25f);
                    Fumbles++;
                    fumble = ", its attack (" + age.ToString("0.00") + " s in) fumbled";
                }
                if (ai != null) s_impactPush = EnemyPush.Start(ai, dir, 3f, 0.35f);
                // the shockwave: the others within 2 m of it, out to 2 m
                Targetable me = pc.GetComponent<Targetable>();
                BetterList<Targetable> near = me.EnumerateWithinRadius(8f, me.GetEnemyTeams());
                int waves = 0;
                for (int i = 0; i < near.size; i++)
                {
                    Targetable o = near[i];
                    if (o == null || o == t || !(o.Controller is AIController)) continue;
                    Vector3 away = o.transform.position - t.transform.position;
                    away.y = 0f;
                    if (away.magnitude >= 2f || away.sqrMagnitude < 1e-4f) continue;
                    EnemyPush.Start((AIController)o.Controller, away, 2f - away.magnitude, 0.3f);
                    waves++;
                }
                Shockwaves += waves;
                Vector3 at = t.AimTransform != null ? t.AimTransform.position : t.transform.position + Vector3.up * 1.2f;
                CombatSounds.Layer(CombatSounds.Thump, at, 0.9f);
                CameraShake.Add(0.25f, 0.07f, 0.02f, "dash impact");
                CombatFeel.Impact(false, true);
                CombatFeel.Impact(true, true);
                LastImpact = WeaponGrips.ModelKey(t.name) + " after " + moved.ToString("0.0") + " m: guard -" + (10f * k).ToString("0.0") + ", pushed 3 m, " + waves + " knocked away" + fumble;
                Plugin.L.LogInfo("dash impact: " + LastImpact);
            }
            catch (Exception e) { Plugin.L.LogWarning("CombatMove: dash impact: " + e.Message); }
        }

        static void DodgeComplete(ActionDodge __instance)
        {
            if (__instance == s_dodge && s_dashLive && PlayerController.Instance != null) Finish(PlayerController.Instance);
            if (__instance == s_dodge) s_dodge = null;
        }

        /// The roll animation's own displacement is dropped while the dodge is a dash (the rotation stays).
        static void DodgeRootMotion(ActionDodge __instance, ref Vector3 a_deltaPosition)
        {
            if (__instance != null && __instance == s_dodge) a_deltaPosition = Vector3.zero;
        }

        /// The dash is over (and its window): the game's roll animation would hold the hero another ~0.7 s; it ends
        /// here, so the player walks or dashes again at once.
        void LateUpdate()
        {
            if (s_dodge == null || s_dashLive || Time.time < s_dashEnd + DashWindow) return;
            ActionDodge d = s_dodge;
            s_dodge = null;
            try { if (d.IsRunning) { d.Complete(); EarlyEnds++; } } catch (Exception e) { Plugin.L.LogWarning("CombatMove: ending the roll: " + e.Message); }
        }

        public static int EarlyEnds;

        // ---------------------------------------------------------------------------------------- UYF's knockback

        static float s_kbLeft;
        static Vector3 s_kbDir;
        // Until You Fall's magnetism, behaving as UYF's does: a hit on the enemy you face
        // pulls you toward it at 5 m/s, until you stand the weapon's MagnetismDistance from its body (sword 1.0 m, daggers
        // 0.55 m, greatsword 1.2 m); not while dashing nor 0.25 s after a dash; a new hit sets a new pull
        static float s_mgLeft;
        static Vector3 s_mgDir;
        public static int Magnets, MagnetStops;
        public static float MagnetLast;
        public const float MagnetRate = 5f, MagnetAfterDash = 0.25f;
        public static int Knockbacks, KnockStops;
        public static float KnockedTotal, KnockedLast;
        public static string KnockWhy = "-";

        /// Until You Fall's knockback, behaving as UYF's does (docs/uyf-reference.md §3.6; the user, 2026-10-12: do it
        /// the way UYF does): the player is moved `dist` m along `dir` (horizontal; UYF: the
        /// engagement space's forward, from the attacker to the player); a new push keeps the larger of what is left and its
        /// own; the speed falls from 10 m/s to 0.5 m/s as the rest shrinks from 1 m to 0.1 m. Here the hero is moved along
        /// the navmesh (walls and edges stop it) and the view goes along; it stops short of an enemy's body. `[Combat]
        /// Knockback = false` switches it off.
        public static void Knockback(Vector3 dir, float dist, string why)
        {
            if (!CombatSettings.Knockback || !CombatSettings.Physical || dist <= 0f) return;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) return;
            s_kbDir = dir.normalized;
            s_kbLeft = Mathf.Max(s_kbLeft, dist);
            Knockbacks++;
            KnockedLast = 0f;
            KnockWhy = why + " " + dist.ToString("0.00") + " m";
        }

        /// Moves the hero by `delta` along the floor: through its NavMeshAgent, or - while a game action (the hero's own hit
        /// reaction) has switched the agent off - along the navmesh by hand: its edge stops it, the floor's height is kept.
        void MoveOnNavmesh(PlayerController pc, Vector3 delta)
        {
            if (m_agent != null && m_agent.enabled && m_agent.isOnNavMesh) { m_agent.Move(delta); return; }
            Vector3 from = pc.transform.position, to = from + delta;
            NavMeshHit hit;
            if (NavMesh.Raycast(from, to, out hit, NavMesh.AllAreas)) to = hit.position;
            if (NavMesh.SamplePosition(to, out hit, 0.6f, NavMesh.AllAreas)) to = hit.position;
            else return;
            pc.transform.position = to;
            OffAgentMoves++;
        }

        public static int OffAgentMoves;

        void Push(PlayerController pc, float dt)
        {
            if (s_kbLeft <= 0f) return;
            if (m_agent == null || s_dashLive) { s_kbLeft = 0f; return; }
            float rate = Mathf.Lerp(0.5f, 10f, Mathf.InverseLerp(0.1f, 1f, s_kbLeft));
            float step = Mathf.Min(s_kbLeft, rate * dt);
            Vector3 before = pc.transform.position;
            if (EnemyAhead(pc, before + s_kbDir * (step + 0.05f))) { s_kbLeft = 0f; KnockStops++; return; }
            MoveOnNavmesh(pc, s_kbDir * step);
            Vector3 moved = pc.transform.position - before;
            moved.y = 0f;
            if (!CombatVR.HeroMovesView) s_walkedInGameAction += moved;   // during a game action the view goes along through this
            KnockedTotal += moved.magnitude;
            KnockedLast += moved.magnitude;
            s_kbLeft -= step;
            if (moved.magnitude < step * 0.3f) { s_kbLeft = 0f; KnockStops++; }   // a wall or the arena's edge
        }

        /// UYF's magnetism (see s_mgLeft): the player struck `t` (BladeHits) with a weapon whose magnetism distance is `standoff`.
        public static string MagnetWhy = "-";

        public static void Magnetize(Targetable t, float standoff)
        {
            if (!CombatSettings.Magnetism || !CombatSettings.Physical || t == null || VRRig.I == null) { MagnetWhy = "off"; return; }
            if (s_dashLive || Time.time < s_dashEnd + MagnetAfterDash) { MagnetWhy = "dash"; return; }
            Vector3 head = VRRig.I.HeadWorldPos;
            Vector3 to = t.transform.position - head;
            to.y = 0f;
            Vector3 gaze = VRRig.I.HeadWorldRot * Vector3.forward;
            gaze.y = 0f;
            // the enemy you face (UYF: the current target)
            float off = to.sqrMagnitude > 1e-4f && gaze.sqrMagnitude > 1e-4f ? Vector3.Angle(gaze, to) : 180f;
            if (off > 35f) { MagnetWhy = "not faced (" + off.ToString("0") + " deg)"; return; }
            float amount = to.magnitude - BodyRadius(t) - standoff;
            MagnetWhy = "dist " + (to.magnitude - BodyRadius(t)).ToString("0.00") + " standoff " + standoff.ToString("0.00");
            if (amount <= 0.02f) return;
            s_mgDir = to.normalized;
            s_mgLeft = amount;   // UYF: to the weapon's distance, no cap (was 1.5 m)
            Magnets++;
            MagnetLast = 0f;
        }

        /// The magnetism's step this frame (5 m/s; walls, edges and bodies stop it).
        void Pull(PlayerController pc, float dt)
        {
            if (s_mgLeft <= 0f) return;
            if (!CombatSettings.Magnetism || m_agent == null || s_dashLive) { s_mgLeft = 0f; return; }
            float step = Mathf.Min(s_mgLeft, MagnetRate * dt);
            Vector3 before = pc.transform.position;
            if (EnemyAhead(pc, before + s_mgDir * (step + 0.05f))) { s_mgLeft = 0f; MagnetStops++; return; }
            MoveOnNavmesh(pc, s_mgDir * step);
            Vector3 moved = pc.transform.position - before;
            moved.y = 0f;
            if (!CombatVR.HeroMovesView) s_walkedInGameAction += moved;
            MagnetLast += moved.magnitude;
            s_mgLeft -= step;
            if (moved.magnitude < step * 0.3f) { s_mgLeft = 0f; MagnetStops++; }
        }

        /// An enemy's body would be within 0.35 m of the hero at `p`.
        static bool EnemyAhead(PlayerController pc, Vector3 p)
        {
            try
            {
                Targetable me = pc.Targetable;
                BetterList<Targetable> all = me.EnumerateWithinRadius(3f, me.GetEnemyTeams());
                for (int i = 0; i < all.size; i++)
                {
                    Targetable t = all[i];
                    if (t == null || t.Controller == null) continue;
                    Vector3 d = t.transform.position - p;
                    d.y = 0f;
                    if (d.magnitude < BodyRadius(t) + 0.35f) return true;
                }
            }
            catch (System.Exception) { }
            return false;
        }

        /// The Dash's dodge window is open now (from the press until DashWindow after the dash).
        public static bool Dodging => !CombatSettings.UyfDash && (s_dashLive || Time.time <= s_dashEnd + DashWindow) && Time.time >= s_pressAt - 0.05f;

        // ---------------------------------------------------------------------------------------- the dodge window

        static void EnsureIgnore(PlayerController pc)
        {
            if (s_ignoreOn == pc) return;
            if (s_ignore == null) s_ignore = IgnoreWhileDashing;
            try
            {
                pc.OnIgnore = (Controller.IgnoreDelegate)Delegate.Combine(pc.OnIgnore, s_ignore);
                s_ignoreOn = pc;
            }
            catch (Exception e) { Plugin.L.LogWarning("CombatMove: dodge window: " + e.Message); }
        }

        /// From the press until 0.2 s after the dash: what an enemy does misses the player (traps still hit).
        static bool IgnoreWhileDashing(ICombatEffectContainer a_container, Targetable a_target)
        {
            if (CombatSettings.UyfDash) return false;   // UYF's dash keeps nothing off the player
            if (!s_dashLive && Time.time > s_dashEnd + DashWindow) return false;
            if (Time.time < s_pressAt - 0.05f) return false;
            try
            {
                PlayerController pc = PlayerController.Instance;
                if (pc == null || a_target == null || a_target.gameObject != pc.gameObject || a_container == null) return false;
                Targetable src = a_container.Source;
                if (src == null || src.gameObject == pc.gameObject || src.GetComponent<AIController>() == null) return false;
                if (!pc.GetComponent<Targetable>().IsEnemy(src)) return false;
                Dodged++;
                if (Dodged <= 30) Plugin.L.LogInfo("dash: dodged " + WeaponGrips.ModelKey(src.name) + " (" + a_container.GetType().Name + ")");
                CombatSounds.PlayAt(CombatSounds.Chime, VRRig.I != null ? VRRig.I.HeadWorldPos + Vector3.up * 0.2f : pc.transform.position, 0.5f);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}

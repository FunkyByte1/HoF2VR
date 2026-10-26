using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Physical mode's Guard: Until You Fall's premonitions, block and dodge (docs/combat-uyf-design.md §9.1, Stage 1 of
    /// "UYF wholesale"; docs/uyf-reference.md §3.2-§3.4):
    ///  * every enemy strike in view gets a premonition in front of the face: a block bar where the attacker's blade will
    ///    pass (SwingAtlas), across its motion, filling over the lead and turning white (lock-in sound) while the guard is
    ///    valid; an unblockable strike gets a dodge arrow instead (Premonition.cs, our own art and sounds);
    ///  * judged once, at the impact (one prefix on CombatEffect.Apply covers every enemy melee): UYF's block test (0.165 m
    ///    in the bar's plane, 0.4 m horizontally, +-30 deg; a shield whose disc covers the bar) -> the game's own block, no
    ///    health lost, UYF's 6 cm knockback (1 m for a heavy blow) and 0.05 s shake; the arrow -> the head moved far
    ///    enough along it since the telegraph (12.5 cm, a duck 8 cm, bosses 30 cm); else out of reach, or hit;
    ///  * the earlier rules are settings: `Counter = Meet | AnyBlock` (a guard that met the blow counters, then the
    ///    riposte), `LeanDodge = true` (leaning 0.2 m dodges any blow; red bars instead of arrows). A strike from outside
    ///    the view is guarded by holding the shield or weapon out toward it;
    ///  * pace: every strike gets at least its class's lead from the telegraph to the impact (ThreatWarning slows the
    ///    wind-up), one live strike at a time for every AI, and SwapGrace before a different attacker may start.
    internal class Guard : MonoBehaviour
    {
        public static Guard I;
        public const float MeetWindow = 0.35f, MeetDistance = 0.12f, BarLength = 0.6f;
        public static int RedBlocked;
        public static int Strikes, Blocks, Counters, Dodges, OutOfRange, HitsTaken, Unguarded, Held, Swaps, MaxLive, Markers, LockIns, Turned;
        public static string Last = "-";
        public static int HitFrame = -1;
        public static int Ended;   // countered strikes ended by the counter
        public static float MinLead = 99f;   // telegraph -> impact of in-view strikes (s, real time)
        public static int LeadShort;         // ... shorter than their class's lead (- 1 frame)   // the frame a melee strike was resolved as a hit (its feedback already ran)
        static readonly List<string> s_log = new List<string>();

        public enum Look { Gold, White, Red }

        sealed class Strike
        {
            public Controller Ctrl;
            public AIController Ai;
            public Targetable T;
            public Model Model;
            public bool Blockable;
            public Look Colour;
            public float Start, Expected;
            public Vector3 HeadT0;
            public float Alpha;            // UYF's attack angle: 0 = from above, +-90 = the sides, 180 = from below
            public bool Valid, WasValid, OffView;
            public string Result;
            public float ResultAt = -1f;
            public Color ResultColour;
            public IndicatorLooks.IBlock Vis;  // the block marker (UYF's premonition, our art; `[Combat] IndicatorLook`)
            public IndicatorLooks.IDodge Arr;  // the dodge arrow (unblockable strikes)
            public bool Heavy;                 // a heavy blow (IndicatorByType: orange)
            public float ShownAt = -1f;
            public bool Seen;              // a marker was drawn for it
            public bool Thrust;            // (unused since Stage 1: the swing atlas knows where each blow goes)
            public string Clip = "";       // the clip that delivers the blow (its "hit" event), or "~" + its action
            public float Event;            // that hit event's time in the clip (s)
            public SwingAtlas.Shape Shape; // where its blade lands (null: UYF's direction palette)
            public SwingAtlas.Track Track; // its blade's recent frames: its shape is learned at the impact
            public Vector3 TelePos;        // the attacker's root at the telegraph
            public float Crossing = 1f;    // how much the blade's motion runs across the bar's plane (0 = a thrust)
            public bool Clamped;           // the bar was pulled into the guard zone (the blade passes outside it)
            public bool InRange = true;    // the head is inside the blow's reach (else the bar dims)
            public bool Threat = true;     // P4-15: the blow can still reach the player (else no bar, no circle)
            public float NoThreatSince = -1f;
            public bool GreenSeen, Judging;   // P4-16: the circle was green (the shield on it) on the frame before its impact
            public float Cover = -1f, FaceDeg = -1f, CoverDepth = -1f;   // 2026-10-20: the shield's last judged coverage of the circle, facing, depth
            public bool Dodge;             // unblockable: UYF's dodge arrow instead of a bar
            public float DodgeX, DodgeY;   // the arrow's direction in the strike's plane (x right, y up; unit)
            public Vector3 DodgeDirT;      // that direction in tracking space, fixed at the telegraph (the room's frame)
            public bool DodgeDirFixed;
            public float DodgeNeed;        // metres the head must move along it by the impact (UYF: 0.125, duck 0.08, boss 0.3)
            public float Progress;         // how far along the head is now (1 = enough)
            public bool WasSucceeding;
            public float Committed;        // how far the attacker was slid in during its wind-up (bring the swing to the bar)
            public bool Holding;           // the attacker committed: its facing held around the blow (UYF's stopped turning)
            public Quaternion HoldRot;
            public bool FollowUp;          // a later blow of a combo that brings no telegraph of its own (opened at the blow before)
            public bool Shove;             // merge-ready #4 (UYF's E7): the enemy's shove - no marker, no damage, the hero pushed back
            public bool Phantom;           // audit item 12: a wind-up's hit event (SwingAtlas.Phantom): no bar, no lead, no damage
            public bool Guess;             // no strike action yet and no atlas shape: its bar would be a guess - none drawn (G redbar)
            public Defiant.Action Act;     // the attacker's strike action (a follow-up ends with it)
            public string Key = "";        // the attacker's model and action, for the learned gap between a combo's blows
            public int Id = ++s_serial;    // for the swing trace
            public bool Ahead;             // audit E3: a later blow's marker shown ahead of it (drawn, not judged yet)
            public bool FromAhead;         // ... now the live strike (its marker up since it was shown ahead)
            public bool Began;             // ... its marker has appeared (the dodge is measured from the head's place then)
            public int Blow = -1;          // which blow of its action (the action's hit index), when known
            public bool OffHeld;           // ... its telegraph came out of view: ThreatWarning's off-view lead (not its run's rhythm)
            public bool ValidShield;       // playtest 3 note B: the guard on it now is the shield (on its circle), not the weapon
            public bool BlockedByShield;   // ... the shield blocked it (the circle flashes, the bar fades)
            public IndicatorLooks.ShieldRing Ring;   // ... the shield's circle at the bar's middle (a shield held)
            public float RingRadius = 0.2f;
            public IndicatorLooks.GroundArrow GroundMark;   // a blow VR can't block either (LeanDodge): the red ground arrow
        }
        static int s_serial;

        readonly Dictionary<int, Strike> m_strikes = new Dictionary<int, Strike>();
        readonly List<Strike> m_fading = new List<Strike>();   // replaced strikes still showing their result
        // audit E3: per attacker, the later blows of its run whose markers show ahead of them (in order)
        readonly Dictionary<int, List<Strike>> m_ahead = new Dictionary<int, List<Strike>>();
        static readonly List<float> s_evs = new List<float>();
        public static int AheadMade, AheadAdopted, AheadDropped, AheadJudged, AheadShort, AheadOverlap, AheadOffHeld;
        public static float AheadMinLead = 99f, AheadGapMin = 99f, AheadGapMax;
        public static int FollowUps, FollowUpsJudged, AimHeld, DodgeShort, Phantoms, Guesses;
        float m_lastImpact = -10f;
        int m_lastImpactBy;
        float m_engagedUntil = -10f;
        float m_riposteUntil = -10f;
        Targetable m_riposteOn;
        IActionParry m_riposteAct;

        public static void Apply(Harmony h)
        {
            try
            {
                var apply = AccessTools.Method(typeof(CombatEffect), "Apply", new[] { typeof(ICombatEffectContainer), typeof(CombatTarget), typeof(Transform) });
                h.Patch(apply, prefix: new HarmonyMethod(typeof(Guard), nameof(ApplyPrefix)), finalizer: new HarmonyMethod(typeof(Guard), nameof(ApplyFinalizer)));
            }
            catch (Exception e) { Plugin.L.LogWarning("Guard: strike patch failed: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(CombatUtils), "StartMeleeEffect"), postfix: new HarmonyMethod(typeof(Guard), nameof(TelegraphStart)));
                h.Patch(AccessTools.Method(typeof(CombatUtils), "StopMeleeEffect"), postfix: new HarmonyMethod(typeof(Guard), nameof(TelegraphStop)));
            }
            catch (Exception e) { Plugin.L.LogWarning("Guard: telegraph patch failed: " + e.Message); }
            // every blow event of the multi-blow strikes (combat Phase 2: the later blows' bars)
            foreach (Type t in new[] { typeof(ActionMeleeComboAI), typeof(ActionTemplarMeleeAI) })
            {
                try { h.Patch(AccessTools.Method(t, "OnHit"), prefix: new HarmonyMethod(typeof(Guard), nameof(BlowPrefix)), postfix: new HarmonyMethod(typeof(Guard), nameof(BlowPostfix))); }
                catch (Exception e) { Plugin.L.LogWarning("Guard: combo patch " + t.Name + ": " + e.Message); }
            }
            try { h.Patch(AccessTools.Method(typeof(Defiant.Action), "Evaluate"), prefix: new HarmonyMethod(typeof(Guard), nameof(EvaluatePrefix)), postfix: new HarmonyMethod(typeof(Guard), nameof(EvaluatePostfix))); }
            catch (Exception e) { Plugin.L.LogWarning("Guard: pace patch failed: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(AIManager), "CanCombatantAttack", new[] { typeof(AITarget), typeof(AIController), typeof(bool) }),
                    postfix: new HarmonyMethod(typeof(Guard), nameof(CanAttackPostfix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("Guard: slot patch failed: " + e.Message); }
            try { h.Patch(AccessTools.Method(typeof(AITarget), "UpdateRangedAttacker"), prefix: new HarmonyMethod(typeof(Guard), nameof(PickShooterPrefix))); }
            catch (Exception e) { Plugin.L.LogWarning("Guard: ranged beat patch failed: " + e.Message); }
            try { h.Patch(AccessTools.Method(typeof(Destroyable), "ApplyDamage"), prefix: new HarmonyMethod(typeof(Guard), nameof(HeroDamaged))); }
            catch (Exception e) { Plugin.L.LogWarning("Guard: hero damage hook failed: " + e.Message); }
        }

        void Awake()
        {
            I = this;
            // `vrthreat on|off`: P4-15's rule (no bar or circle for a blow that can't reach the player), this run only
            Diag.Commands["vrthreat"] = a =>
            {
                if (a.Length > 1) ThreatRule = a[1] != "off";
                return "threat rule " + (ThreatRule ? "on" : "off") + ", hides " + ThreatHides + " hiddenThenHit " + HiddenThenHit;
            };
            // `shieldcover? [need <v>] [clear]`: the rule (2026-10-20: the shield covers >= 55 % of the circle), the held shield's
            // outline, each live blow's coverage now (exact, and a 200 x 200 grid's estimate as a check), the last blows' records
            Diag.Commands["shieldcover?"] = a => CoverQuery(a);
            Diag.Commands["shieldcover"] = a => CoverQuery(a);
            // `coverat <share> | off <m> [axis|right|up|left|down|-axis] [noplane]`: the xrsim move of the shield hand that makes the
            // held shield cover `share` of the live blow's circle (moved along that direction in the circle's plane; the hand's turn
            // kept), or puts its centre `m` off the circle's centre that way - with the coverage it will have there (tests)
            Diag.Commands["coverat"] = a => CoverAt(a);
            // `dmglog? [clear]`: the damage that reached the player (P4-16: what landed although the circle was green)
            Diag.Commands["dmglog?"] = a =>
            {
                if (a.Length > 1 && a[1] == "clear") { s_dmg.Clear(); DamageEvents = DamageAfterBlock = DamageUnjudged = ToHealth = ToHealthAfterBlock = GreenBlocked = GreenNotBlocked = BlockedNotGreen = 0; ToHealthAfterBlockSum = 0f; }
                return "damage " + DamageEvents + " afterBlock " + DamageAfterBlock + " unjudged " + DamageUnjudged + " toHealth " + ToHealth + " toHealthAfterBlock " + ToHealthAfterBlock +
                       " (" + ToHealthAfterBlockSum.ToString("0.00") + ") greenBlocked " + GreenBlocked + " greenNotBlocked " + GreenNotBlocked + " blockedNotGreen " + BlockedNotGreen + " | " + string.Join(" | ", s_dmg.ToArray());
            };
        }

        // ---- P4-16 diagnostics: every damage the game deals the player (EnemyTuning's damage-in hook), with the guard's verdict
        public static int GreenBlocked, GreenNotBlocked, BlockedNotGreen;
        public static int ToHealth, ToHealthAfterBlock;
        public static float ToHealthAfterBlockSum;

        /// Destroyable.ApplyDamage on the hero: what really reaches its health (after the game's blocked multiplier and armour).
        static void HeroDamaged(Destroyable __instance, Destroyable.Damage a_damage)
        {
            try
            {
                if (!Physical.Live || __instance == null) return;
                PlayerController pc = PlayerController.Instance;
                if (pc == null || __instance.gameObject != pc.gameObject || a_damage.Amount <= 0f) return;
                ToHealth++;
                bool afterBlock = s_blockFrame == Time.frameCount;
                if (afterBlock) { ToHealthAfterBlock++; ToHealthAfterBlockSum += a_damage.Amount; }
                string line = FightClock.Now.ToString("0.00") + " health -" + a_damage.Amount.ToString("0.00") + (afterBlock ? " AFTER THE GUARD'S BLOCK" : "");
                if (s_dmg.Count >= 12) s_dmg.RemoveAt(0);
                s_dmg.Add(line);
                if (ToHealth <= 60) Plugin.L.LogInfo("Guard: the hero's health " + line);
            }
            catch (Exception) { }
        }

        public static int DamageEvents, DamageAfterBlock, DamageUnjudged;
        static int s_blockFrame = -1;
        static readonly List<string> s_dmg = new List<string>();

        public static void DamageSeen(ICombatEffectContainer c, float damage, bool blocked)
        {
            try
            {
                if (!Physical.Live) return;
                DamageEvents++;
                bool afterBlock = s_blockFrame == Time.frameCount, judgedHit = HitFrame == Time.frameCount;
                if (afterBlock) DamageAfterBlock++;
                else if (!judgedHit) DamageUnjudged++;
                string line = FightClock.Now.ToString("0.00") + " " + (c != null && c.Source != null ? WeaponGrips.ModelKey(c.Source.name) : "-") + " " +
                              (c != null ? c.GetType().Name + "/" + c.Category : "-") + " " + damage.ToString("0.0") + (blocked ? " blocked" : "") +
                              (afterBlock ? " AFTER THE GUARD'S BLOCK" : judgedHit ? " (the guard's hit)" : " (not judged by the guard)");
                if (s_dmg.Count >= 12) s_dmg.RemoveAt(0);
                s_dmg.Add(line);
                if (DamageEvents <= 60) Plugin.L.LogInfo("Guard: damage to the player " + line);
            }
            catch (Exception) { }
        }

        public static string Describe() =>
            "strikes " + Strikes + " blocks " + Blocks + " redBlocked " + RedBlocked + " counters " + Counters + " dodges " + Dodges + " dodgeBypassed " + DodgeBypassed + " cooldowns " + Cooldowns +
            " rangedHolds " + RangedHolds + " beatsToMelee " + BeatsToMelee + " waitingShooters " + WaitingShooters + " outOfRange " + OutOfRange + " hitsTaken " + HitsTaken +
            " unguarded " + Unguarded + " held " + Held + " swaps " + Swaps + " maxLive " + MaxLive + " markers " + Markers + " rings " + Rings + " threatHides " + ThreatHides + " hiddenThenHit " + HiddenThenHit + " greenBlocked " + GreenBlocked + " greenNotBlocked " + GreenNotBlocked + " blockedNotGreen " + BlockedNotGreen + " shieldFace " + CombatVR.ShieldFaceElevation.ToString("0") + " shieldClamps " + CombatVR.ShieldClamps + " lockIns " + LockIns + " turned " + Turned +
            " commits " + Commits + " commitMost " + CommitMost.ToString("0.00") + " facingHolds " + FacingHolds + " facingHeldDeg " + FacingHeldDeg.ToString("0.0") + " arrows " + Arrows + " dodgeReadies " + DodgeReadies + " dodgeShort " + DodgeShort + " knockbacks " + CombatMove.Knockbacks + " knocked " + CombatMove.KnockedLast.ToString("0.000") + " " + CameraShake.Describe() +
            " atlasPredicted " + SwingAtlas.Predicted + " atlasFallbacks " + SwingAtlas.Fallbacks + " atlasLearned " + SwingAtlas.Learned +
            " minLead " + MinLead.ToString("0.00") + " leadShort " + LeadShort + " exactLeads " + ThreatWarning.ExactLeads + " exactMisses " + ThreatWarning.ExactMisses + " globalSlows " + EnemyTuning.GlobalSlows + " replans " + ThreatWarning.Replans + " lateExact " + ThreatWarning.LateExact + " ended " + Ended + " sideMisses " + SideMisses + " followUps " + FollowUps + " followUpsJudged " + FollowUpsJudged + " combosGoOn " + CombosGoOn + " aimHeld " + AimHeld + " phantoms " + Phantoms + " guesses " + Guesses +
            " aheadMade " + AheadMade + " aheadAdopted " + AheadAdopted + " aheadDropped " + AheadDropped + " aheadJudged " + AheadJudged + " aheadShort " + AheadShort +
            " aheadMinLead " + AheadMinLead.ToString("0.00") + " aheadOverlap " + AheadOverlap + " last " + Last;

        public static void ResetCounts()
        {
            Strikes = Blocks = Counters = Dodges = OutOfRange = HitsTaken = Unguarded = Held = Swaps = MaxLive = Markers = LockIns = Turned = LeadShort = FollowUps = FollowUpsJudged = AimHeld = 0;
            Arrows = DodgeReadies = DodgeShort = SideMisses = 0;
            DodgeBypassed = Cooldowns = RangedHolds = BeatsToMelee = 0;
            CombatMove.Knockbacks = 0;
            CameraShake.Shakes = 0;
            CameraShake.MaxOffset = 0f;
            MinLead = 99f;
            ResetAhead();
            s_log.Clear();
            Last = "-";
        }

        public static string Log() => string.Join("\n  ", s_log.ToArray());

        static int s_longSlow;

        static void Note(string s)
        {
            Last = s;
            if (s_log.Count < 300) s_log.Add(FightClock.Now.ToString("0.00") + " " + s);
            if (s_log.Count <= 60) Plugin.L.LogInfo("Guard: " + s);
        }

        // ---------------------------------------------------------------------------------------- the guard (from BladeHits)
        sealed class Pose
        {
            public bool Has, Shield;
            public Vector3 Center, Normal, Grip, Tip;   // world
            public float Disc;
            public Vector3[] Outline;                   // a shield's outline (model space, ShieldAxis.Outline; BladeHits' array)
            public Matrix4x4 ToWorld = Matrix4x4.identity;   // ... and its model -> world map this frame
            public readonly List<Seg> Hist = new List<Seg>();   // tracking: shield centre, or blade base and tip
            public int Frame;
        }
        struct Seg { public float T; public Vector3 A, B; }
        static readonly Pose[] s_pose = { new Pose(), new Pose(), new Pose() };   // 0 = left weapon, 1 = right weapon, 2 = shield

        /// BladeHits' per-frame geometry of what each hand holds (world; tracking samples for the "meet" rule).
        public static void Feed(bool left, bool shield, Vector3 center, Vector3 normal, float disc, Vector3 grip, Vector3 tip, Vector3 aT, Vector3 bT,
                                Vector3[] outline, Matrix4x4 toWorld)
        {
            Pose p = s_pose[shield ? 2 : left ? 0 : 1];
            p.Has = true;
            p.Shield = shield;
            p.Center = center;
            p.Normal = normal;
            p.Disc = disc;
            p.Outline = outline;
            p.ToWorld = toWorld;
            p.Grip = grip;
            p.Tip = tip;
            p.Frame = Time.frameCount;
            float now = FightClock.Now;
            p.Hist.Add(new Seg { T = now, A = aT, B = bT });
            while (p.Hist.Count > 2 && now - p.Hist[0].T > MeetWindow + 0.15f) p.Hist.RemoveAt(0);
        }

        static bool Fresh(Pose p) => p.Has && Time.frameCount - p.Frame <= 2;

        /// The held shield this frame (world): its centre, face normal and radius (ShieldShots: the shield against shots).
        public static bool Shield(out Vector3 center, out Vector3 normal, out float radius)
        {
            Pose p = s_pose[2];
            center = p.Center;
            normal = p.Normal;
            radius = p.Disc;
            return Fresh(p);
        }

        /// The guard pose (shield or weapon) that is on the bar now, or null; `met` = it moved >= MeetDistance into place in
        /// the last MeetWindow (the optional counter). UYF's AttackBlocker.CheckBlock (docs/uyf-reference.md §3.3): for each
        /// held weapon and each of the bar's three points P (its middle and both ends), C = the blade's point nearest P; the
        /// weapon blocks if C is within GuardTolerance (UYF 0.165 m) of P in the bar's plane and 0.4 m of it horizontally, and
        /// the blade, seen in the bar's plane, lies within GuardAngle (UYF 30 deg) of the bar's axis either way; the best
        /// score wins (UYF: InverseLerp(180, 0, angle) x (1 - dXZ^2 / 0.4^2) x (1 - dXY^2 / tol^2)). A shield (HoF2 has them,
        /// UYF doesn't) blocks when its own outline, seen along the blow (projected onto the circle's plane), covers at least
        /// ShieldCover of the drawn circle (2026-10-20, the user: "Not the middle. Any part of the shield has to cover at least 55%
        /// of the circle"), its centre within 0.4 m of the plane, its face toward the attacker (within ShieldFacingDeg). The live
        /// preview (the circle's green) and the impact run this same test.
        Pose Judge(Strike s, Vector3 head, out bool met)
        {
            met = false;
            s.Cover = s.FaceDeg = s.CoverDepth = -1f;
            Vector3 att = AttPos(s);
            Vector3 f = att - head;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-4f) return null;
            f.Normalize();
            float tol = CombatSettings.GuardTolerance, ang = CombatSettings.GuardAngle;
            const float maxXZ = 0.4f;
            Pose best = null;
            float bestT = 0.5f, bestScore = 0f;
            if (s.OffView)
            {
                // held out toward it (Arkham's arm out): >= 0.2 m from the head, within 60 deg of its bearing; > 150 deg behind:
                // any guard held out more than 60 deg from the gaze
                float bearing = Mathf.Abs(ThreatWarning.BearingOf(att));
                Vector3 gaze = VRRig.I.HeadWorldRot * Vector3.forward;
                gaze.y = 0f;
                for (int i = 0; i < 3; i++)
                {
                    Pose p = s_pose[i];
                    if (!Fresh(p)) continue;
                    Vector3 pt = p.Shield ? p.Center : (p.Grip + p.Tip) * 0.5f;
                    Vector3 o = pt - head;
                    o.y = 0f;
                    if (o.magnitude < 0.2f) continue;
                    bool ok = Vector3.Angle(o, f) <= 60f || (bearing > 150f && Vector3.Angle(o, gaze) > 60f);
                    if (ok) { best = p; break; }
                }
            }
            else
            {
                Vector3 center, axis;
                Bar(s, head, f, out center, out axis);
                for (int i = 0; i < 3; i++)
                {
                    Pose p = s_pose[i];
                    if (!Fresh(p)) continue;
                    if (p.Shield)
                    {
                        // the shield on the circle at the bar's middle (playtest 3 note B) blocks when its own outline, seen along the
                        // blow (the circle faces f), covers at least ShieldCover of the drawn circle (2026-10-20: "Not the middle. Any
                        // part of the shield has to cover at least 55% of the circle"; the centre-inside rule of P4-13 before), its
                        // centre within maxXZ of the plane, its face toward the attacker within ShieldFacingDeg
                        Vector3 d = p.Center - center;
                        float depth = Mathf.Abs(Vector3.Dot(d, f));
                        Vector3 toA = att - p.Center;
                        float cover = Coverage(p, center, f, Vector3.zero);
                        s.Cover = cover;
                        s.FaceDeg = Vector3.Angle(p.Normal, toA);
                        s.CoverDepth = depth;
                        if (cover >= ShieldCover && depth <= maxXZ && Vector3.Dot(p.Normal, toA.normalized) > Mathf.Cos(ShieldFacingDeg * Mathf.Deg2Rad))
                        {
                            float score = cover * (1f - depth * depth / (maxXZ * maxXZ));
                            if (score > bestScore) { bestScore = score; best = p; }
                        }
                        continue;
                    }
                    Vector3 dir = p.Tip - p.Grip;
                    if (dir.sqrMagnitude < 1e-6f) continue;
                    Vector3 dirP = dir - f * Vector3.Dot(dir, f);
                    if (dirP.sqrMagnitude < 1e-6f) continue;
                    float a = Vector3.Angle(dirP, axis);
                    a = Mathf.Min(a, 180f - a);
                    if (a > ang) continue;
                    for (int k = -1; k <= 1; k++)
                    {
                        Vector3 bp = center + axis * (k * BarLength * 0.5f);
                        float t;
                        Vector3 c = Closest(p.Grip, p.Tip, bp, out t);
                        Vector3 d = c - bp;
                        float dXY = (d - f * Vector3.Dot(d, f)).magnitude;
                        Vector3 h = d;
                        h.y = 0f;
                        float dXZ = h.magnitude;
                        if (dXY > tol || dXZ > maxXZ) continue;
                        float score = Mathf.InverseLerp(180f, 0f, a) * (1f - dXZ * dXZ / (maxXZ * maxXZ)) * (1f - dXY * dXY / (tol * tol));
                        if (score > bestScore) { bestScore = score; best = p; bestT = t; }
                    }
                }
            }
            if (best != null) met = Met(best, bestT);
            return best;
        }

        /// Why no held guard counted at this impact: per fresh pose its numbers against the bar (diagnostics).
        string WhyNot(Strike s, Vector3 head)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Vector3 f;
            if (!Toward(s, head, out f)) return "no direction";
            Vector3 center, axis, att = AttPos(s);
            Bar(s, head, f, out center, out axis);
            var sb = new System.Text.StringBuilder("bar " + Diag.V(center - head) + " axis " + Diag.V(axis));
            for (int i = 0; i < 3; i++)
            {
                Pose p = s_pose[i];
                if (!p.Has) continue;
                sb.Append(i == 2 ? " | shield" : i == 0 ? " | left blade" : " | right blade").Append(Fresh(p) ? "" : " (stale " + (Time.frameCount - p.Frame) + " frames)");
                if (p.Shield)
                {
                    Vector3 d = p.Center - center;
                    sb.Append(" cover ").Append(Coverage(p, center, f, Vector3.zero).ToString("0.00", inv)).Append(" (need ").Append(ShieldCover.ToString("0.00", inv)).Append(")")
                      .Append(" centre off ").Append((d - f * Vector3.Dot(d, f)).magnitude.ToString("0.00", inv)).Append(" depth ").Append(Mathf.Abs(Vector3.Dot(d, f)).ToString("0.00", inv))
                      .Append(" face ").Append(Vector3.Angle(p.Normal, att - p.Center).ToString("0", inv)).Append(" deg")
                      .Append(" at ").Append(Diag.V(p.Center - head));
                }
                else
                {
                    float t;
                    Vector3 c = Closest(p.Grip, p.Tip, center, out t), d = c - center, dir = p.Tip - p.Grip, dirP = dir - f * Vector3.Dot(dir, f);
                    float a = dirP.sqrMagnitude > 1e-6f ? Vector3.Angle(dirP, axis) : 90f;
                    sb.Append(" in-plane ").Append((d - f * Vector3.Dot(d, f)).magnitude.ToString("0.00", inv)).Append(" angle ").Append(Mathf.Min(a, 180f - a).ToString("0", inv));
                }
            }
            return sb.ToString();
        }

        static Vector3 AttPos(Strike s) => s.T != null ? s.T.AimTransform.position : s.Ctrl.transform.position;

        /// How squarely a shield must face the attacker to block on the circle (deg; it was 72 before the circle).
        public const float ShieldFacingDeg = 65f;
        /// The share of the drawn circle the shield's outline must cover to block (and the circle to turn green) - 2026-10-20, the
        /// user: "Not the middle. Any part of the shield has to cover at least 55% of the circle." (`shieldcover need <v>`: this run)
        public static float ShieldCover = 0.55f;
        /// The circle the shield must cover: the one drawn, 0.30 m across (P4-13, playtest 4: "about a foot in diameter"; the
        /// band's middle). Before 2026-10-20 the shield's centre had to be inside its outer edge (0.165 m); before P4-13 the
        /// shield's own radius up to 0.26 m round a frame 1.2x the shield.
        public static float CircleR => IndicatorLooks.ShieldRing.FrameD * 0.5f;
        /// (kept for the old callers: the circle's radius; RingMax: the shot circle's largest)
        public static float RingRadiusOf(float disc) => CircleR;
        public const float RingMax = 0.26f;

        static readonly Vector2[] s_poly = new Vector2[64];

        /// The share of the drawn circle (radius CircleR round `center`, facing `f`) covered by the held shield's outline seen along
        /// `f` (each corner projected onto the circle's plane), the shield moved by `shift` (world; tests only, else zero). Exact:
        /// the area of a convex polygon and a disc in common is the sum over its edges of the circle's part of the triangle (the
        /// circle's centre, the edge) - a triangle where the edge is inside, a sector where it is outside (the edge cut where it
        /// crosses the circle). A shield seen edge-on projects to a line and covers nothing. No allocations.
        static float Coverage(Pose p, Vector3 center, Vector3 f, Vector3 shift)
        {
            Vector3[] ol = p.Outline;
            if (ol == null || ol.Length < 3) return 0f;
            float R = CircleR;
            if (R < 1e-4f) return 0f;
            Vector3 r = Vector3.Cross(Vector3.up, f);
            if (r.sqrMagnitude < 1e-8f) r = Vector3.Cross(Vector3.forward, f);
            r.Normalize();
            Vector3 u = Vector3.Cross(f, r).normalized;
            int n = Mathf.Min(ol.Length, s_poly.Length);
            Vector3 c0 = center - shift;
            float inv = 1f / R;
            for (int i = 0; i < n; i++)
            {
                Vector3 w = p.ToWorld.MultiplyPoint3x4(ol[i]) - c0;
                s_poly[i] = new Vector2(Vector3.Dot(w, r) * inv, Vector3.Dot(w, u) * inv);
            }
            float a = 0f;
            for (int i = 0; i < n; i++) a += EdgeArea(s_poly[i], s_poly[(i + 1) % n]);
            return Mathf.Clamp01(Mathf.Abs(a) / Mathf.PI);
        }

        /// The signed area the unit circle (round the origin) and the triangle (origin, A, B) have in common.
        static float EdgeArea(Vector2 A, Vector2 B)
        {
            Vector2 d = B - A;
            float aa = d.sqrMagnitude;
            if (aa < 1e-12f) return 0f;
            float b = Vector2.Dot(A, d), c = A.sqrMagnitude - 1f, disc = b * b - aa * c;
            if (disc <= 0f) return Sector(A, B);   // the edge's line misses the circle
            float sq = Mathf.Sqrt(disc);
            float t0 = Mathf.Clamp01((-b - sq) / aa), t1 = Mathf.Clamp01((-b + sq) / aa);
            Vector2 P = A + d * t0, Q = A + d * t1;
            float area = 0f;
            if (t0 > 0f) area += Sector(A, P);
            if (t1 > t0) area += 0.5f * (P.x * Q.y - P.y * Q.x);
            if (t1 < 1f) area += Sector(Q, B);
            return area;
        }

        static float Sector(Vector2 P, Vector2 Q) => 0.5f * Mathf.Atan2(P.x * Q.y - P.y * Q.x, Vector2.Dot(P, Q));

        /// The same share estimated by points (a k x k grid over the disc, each point in the projected polygon): `shieldcover?`'s check.
        static float CoverageGrid(Pose p, Vector3 center, Vector3 f, int k)
        {
            Coverage(p, center, f, Vector3.zero);   // fills s_poly
            int n = Mathf.Min(p.Outline != null ? p.Outline.Length : 0, s_poly.Length);
            if (n < 3) return 0f;
            float sgn = 0f;
            for (int i = 0; i < n; i++) { Vector2 A = s_poly[i], B = s_poly[(i + 1) % n]; sgn += A.x * B.y - A.y * B.x; }
            if (Mathf.Abs(sgn) < 1e-9f) return 0f;
            int inside = 0, all = 0;
            for (int ix = 0; ix < k; ix++)
                for (int iy = 0; iy < k; iy++)
                {
                    Vector2 q = new Vector2(-1f + (ix + 0.5f) * 2f / k, -1f + (iy + 0.5f) * 2f / k);
                    if (q.sqrMagnitude > 1f) continue;
                    all++;
                    bool inPoly = true;
                    for (int i = 0; i < n && inPoly; i++)
                    {
                        Vector2 A = s_poly[i], B = s_poly[(i + 1) % n];
                        if (((B.x - A.x) * (q.y - A.y) - (B.y - A.y) * (q.x - A.x)) * sgn < 0f) inPoly = false;
                    }
                    if (inPoly) inside++;
                }
            return all > 0 ? inside / (float)all : 0f;
        }
        /// P4-13: "When my shield is in the circle, the line (for the weapon blocking) should be turned super faint" (was 0.3).
        public static float LineOnShield = 0.08f;

        /// The guard's point moved >= MeetDistance within the last MeetWindow (tracking space: walking and turning don't count).
        static bool Met(Pose p, float t)
        {
            if (p.Hist.Count < 2) return false;
            float now = FightClock.Now;
            Seg cur = p.Hist[p.Hist.Count - 1];
            Vector3 c = p.Shield ? cur.A : Vector3.Lerp(cur.A, cur.B, t);
            float most = 0f;
            for (int i = p.Hist.Count - 2; i >= 0; i--)
            {
                if (now - p.Hist[i].T > MeetWindow) break;
                Seg h = p.Hist[i];
                Vector3 o = p.Shield ? h.A : Vector3.Lerp(h.A, h.B, t);
                most = Mathf.Max(most, (c - o).magnitude);
            }
            return most >= MeetDistance;
        }

        static Vector3 Closest(Vector3 a, Vector3 b, Vector3 p, out float t)
        {
            Vector3 ab = b - a;
            float l = ab.sqrMagnitude;
            t = l > 1e-6f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / l) : 0f;
            return a + ab * t;
        }

        /// The bar: where the attacker's blade will cross the bar's plane at the impact (SwingAtlas: the blow measured per
        /// attacker and clip, placed on the attacker's root now), across its motion, kept inside the guard zone. The plane
        /// is UYF's engagement space: horizontal toward the attacker, half way to it but 0.25-0.5 m from the eyes. A blow not
        /// in the atlas yet uses UYF's palette on its ellipse (x = 0.25 sin(alpha) toward the side the blade
        /// comes from, y = -0.08 + 0.13 cos(alpha) from the eyes; the bar across alpha).
        void Bar(Strike s, Vector3 head, Vector3 f, out Vector3 center, out Vector3 axis)
        {
            Vector3 att = AttPos(s);
            float depth = SwingAtlas.Depth(head, att);
            // a blow whose blade never reached the guard zone when measured (the templar's first hit lands at 1.8 m with
            // nothing of it near the player) gets UYF's palette in front instead of a bar at the edge of the zone
            if (s.Shape != null && s.Ctrl != null && s.Shape.Reach <= ReachLimit)
            {
                Vector3 a, b, v;
                SwingAtlas.World(s.Shape, s.Ctrl, s.TelePos, out a, out b, out v);
                SwingAtlas.Place(head, f, depth, a, b, v, out center, out axis, out s.Crossing, out s.Clamped);
                SideOfHit(s, head, f, ref center);
                return;
            }
            Vector3 r = Vector3.Cross(Vector3.up, f).normalized;
            float al = s.Alpha * Mathf.Deg2Rad;
            center = head + f * depth + r * (0.25f * Mathf.Sin(al)) + Vector3.up * (-0.08f + 0.13f * Mathf.Cos(al));
            axis = (r * Mathf.Cos(al) + Vector3.up * Mathf.Sin(al)).normalized;
            s.Crossing = 1f;
            s.Clamped = false;
        }

        /// Mark 16 (playtest 3): a blow known only by its hit direction ("~ActionMeleeAI/Left/Normal": the soldiers', thieves',
        /// goblins'... - no clip of its own in the atlas, so its shape is learned or borrowed from another rig) lands on that side
        /// of the player - the soldiers' Left blades passed the bar's plane moving left at 10-13 m/s, their Right ones moving
        /// right. Its bar is kept on that side, at least SideMin from the middle (SwingAtlas.Place puts a sweep's bar where it
        /// lands; this is for the ones whose borrowed shape still says otherwise). The palette (AlphaOf) agrees: Left = -90.
        public const float SideMin = 0.2f;
        public static int SideFixes;

        static void SideOfHit(Strike s, Vector3 head, Vector3 f, ref Vector3 center)
        {
            if (!SwingAtlas.LandSide || string.IsNullOrEmpty(s.Clip) || s.Clip[0] != '~') return;
            int side = s.Clip.IndexOf("/Left/", StringComparison.Ordinal) >= 0 ? -1 : s.Clip.IndexOf("/Right/", StringComparison.Ordinal) >= 0 ? 1 : 0;
            if (side == 0) return;
            Vector3 r = Vector3.Cross(Vector3.up, f).normalized;
            float x = Vector3.Dot(center - head, r);
            if (x * side >= SideMin) return;
            float want = side * Mathf.Min(SwingAtlas.ZoneX, Mathf.Max(SideMin, Mathf.Abs(x)));
            center += r * (want - x);
            SideFixes++;
        }

        /// The bar as placed before Stage 1 (for the trace's before/after numbers only): the Phase 0 "thrust" table (a level
        /// bar at chin height for thieves, guards and Norse; the corrupted tank's from the right), else the hit direction's
        /// palette on the old ellipse.
        static void LegacyBar(Strike s, Vector3 head, Vector3 f, out Vector3 center, out Vector3 axis)
        {
            Vector3 att = AttPos(s);
            Vector3 flat = att - head;
            flat.y = 0f;
            float dist = flat.magnitude;
            Vector3 r = Vector3.Cross(Vector3.up, f).normalized;
            string m = WeaponGrips.ModelKey(s.Model != null ? s.Model.name : "");
            bool thrust = m == "ThievesThugModel" || m == "EmpireGuardModel" || m == "NorseWarriorModel" || m == "NorseBerserkerModel";
            float alpha = m == "CorruptedTankModel" ? 90f : s.Alpha;
            float a = alpha * Mathf.Deg2Rad, side = 0.25f * Mathf.Sin(a), abs = Mathf.Abs(alpha);
            float y = abs <= 90f ? Mathf.Lerp(0.05f, -0.10f, abs / 90f) : Mathf.Lerp(-0.10f, -0.21f, (abs - 90f) / 90f);
            if (thrust) { side = 0f; y = -ThrustDrop; a = 0f; }
            center = head + f * Mathf.Clamp(dist * 0.5f, 0.25f, 0.5f) + r * side + Vector3.up * y;
            axis = (r * Mathf.Cos(a) + Vector3.up * Mathf.Sin(a)).normalized;
        }

        /// UYF's attack angle for a blow not in the swing atlas yet, from the strike's own hit direction (0 = from above,
        /// +-90 = from the player's right / left, 180 = from below).
        static float AlphaOf(Controller c, Model model, out bool thrust)
        {
            thrust = false;
            try
            {
                Actor actor = c.GetComponent<Actor>();
                IActionAICombat act = actor != null ? actor.GetRunningAction<IActionAICombat>() : null;
                ICombatEffectContainer cont = act as ICombatEffectContainer;
                CombatEffectHit hit = cont != null ? FirstHit(cont.CombatEffects, 0) : null;
                if (hit != null)
                {
                    if (hit.VerticalDirection == HitDirectionVertical.Upward) return 180f;
                    if (hit.Direction == HitDirection.Left) return -90f;
                    if (hit.Direction == HitDirection.Right) return 90f;
                    if (hit.VerticalDirection == HitDirectionVertical.Gut) return 120f;
                }
            }
            catch (Exception) { }
            return 0f;
        }

        /// (kept for the tests' level-bar check; Stage 1 places thrusts from the atlas)
        public const float ThrustDrop = 0.15f;

        public static CombatEffectHit FirstHit(CombatEffect[] fx, int depth)
        {
            if (fx == null || depth > 3) return null;
            foreach (CombatEffect e in fx)
            {
                CombatEffectHit h = e as CombatEffectHit;
                if (h != null) return h;
                CombatEffectGroup g = e as CombatEffectGroup;
                if (g != null)
                {
                    h = FirstHit(((ICombatEffectContainer)g).CombatEffects, depth + 1);
                    if (h != null) return h;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------- strikes

        static void TelegraphStart(Model a_model, bool a_blockable)
        {
            if (I == null || a_model == null || !Physical.Live) return;
            try { I.Open(a_model, a_blockable); } catch (Exception e) { Plugin.L.LogWarning("Guard: telegraph: " + e.Message); }
        }

        static void TelegraphStop(Model a_model)
        {
            if (I == null || a_model == null) return;
            foreach (Strike s in I.m_strikes.Values)
                if (s.Model == a_model && s.Result == null) { s.Result = "window closed"; s.ResultAt = FightClock.Now; s.ResultColour = Color.clear; }
        }

        void Open(Model model, bool blockable)
        {
            Controller ctrl = model.GetComponentInParent<Controller>();
            AIController ai = ctrl as AIController;
            if (ai == null) return;
            PlayerController pc = PlayerController.Instance;
            if (pc == null || ai.Target == null || ai.Target.gameObject != pc.gameObject) return;
            if (TelegraphAhead(ctrl, blockable)) return;   // audit E3: this blow's marker already shows
            Strike s;
            if (m_strikes.TryGetValue(ctrl.GetInstanceID(), out s)) Retire(s);
            ClearAhead(ctrl.GetInstanceID());
            s = new Strike { Ctrl = ctrl, Ai = ai, T = ctrl.Targetable, Model = model, Blockable = blockable, Start = FightClock.Now, HeadT0 = VRInput.HeadPos };
            bool counterable = blockable && ActionCounter.GetAction(s.T) != null;
            s.Colour = !blockable ? Look.Red : counterable && CombatSettings.Counter != CombatSettings.CounterRule.Off ? Look.Gold : Look.White;
            s.Alpha = AlphaOf(ctrl, model, out s.Thrust);
            s.Act = RunningStrike(ctrl);
            s.Key = WeaponGrips.ModelKey(model.name) + "/" + (s.Act != null ? s.Act.GetType().Name : "?");
            s.Expected = ThreatWarning.ExpectedOf(ctrl);
            s.OffView = Mathf.Abs(ThreatWarning.BearingOf(ctrl.transform.position)) > ThreatWarning.ViewHalf;
            s.Shove = Shoves.Take(ai);
            Prepare(s);
            // the shadow warriors' red pre-glow (the game's uncounterable warning before ActionTemplarMeleeAI begins): no
            // strike action yet, no atlas shape - a bar there was a guess at head height (alpha 0) and the real telegraph
            // came ~1 s later with its own bar elsewhere; the chains' redbar FAILs (p2c r1, gn2r r1) had the shield on the
            // guessed bar when the real blow landed. No bar until the action's own telegraph; judged as usual meanwhile
            s.Guess = s.Act == null && s.Shape == null && !s.Phantom;
            if (s.Guess) Guesses++;
            m_strikes[ctrl.GetInstanceID()] = s;
            Strikes++;
            int live = 0;
            foreach (Strike o in m_strikes.Values) if (o.Result == null) live++;
            MaxLive = Mathf.Max(MaxLive, live);
            Note("telegraph " + s.Key + " " + s.Colour + (s.OffView ? " off-view" : "") + (s.Phantom ? " PHANTOM (no bar)" : "") + (s.Guess ? " no action yet (no bar)" : "") + " clip " + s.Clip + (s.Shape != null ? " atlas" : " alpha " + s.Alpha.ToString("0")) +
                 (s.Dodge ? " dodge " + DodgeName(s) : "") + " lead " + s.Expected.ToString("0.00") + " s");
            if (SwingTrace.On) SwingTrace.Begin(s.Id, ctrl, model, s.Act != null ? s.Act.GetType().Name : "?", DirOf(s.Act as ICombatEffectContainer), s.Colour.ToString(), s.Clip, s.Event, false);
            s.Blow = s.Act != null ? BlowIndex(s.Act) : -1;
            if (!s.Phantom && !s.Shove && !s.Guess) MakeAhead(s);
        }

        /// What a strike needs from its attacker's animation: the clip that delivers the blow, where that blow lands (the
        /// swing atlas), a track of its blade to learn it at the impact, and for an unblockable blow UYF's dodge arrow.
        void Prepare(Strike s)
        {
            try { ICombatEffectContainer cont = s.Act as ICombatEffectContainer; s.Heavy = cont != null && (cont.Category & CombatCategory.HeavyMelee) != 0; } catch (Exception) { }
            float ev;
            s.Clip = ClipKey(s.Ctrl, s.Act, out ev);
            s.Event = ev;
            string model = WeaponGrips.ModelKey(s.Model != null ? s.Model.name : s.Ctrl.name);
            s.Shape = SwingAtlas.Find(model, s.Clip, ev);
            if (s.Shape != null) SwingAtlas.Predicted++; else SwingAtlas.Fallbacks++;
            s.Phantom = SwingAtlas.Phantom(s.Shape);
            s.TelePos = s.Ctrl.transform.position;
            s.Track = new SwingAtlas.Track { C = s.Ctrl, M = s.Model, Model = model, Clip = s.Clip, Event = ev, TelePos = s.TelePos, TeleRot = s.Ctrl.transform.rotation };
            ChooseDodge(s);
        }

        /// The swing atlas's key for the blow this attacker winds up: the clip that delivers it and its "hit" event's time;
        /// when no clip holds a hit event (the shadow warriors' plain attack), "~" + the strike action and its hit direction.
        public static string ClipKey(Controller c, Defiant.Action act, out float ev)
        {
            float sec;
            string clip;
            if (ThreatWarning.HitClip(c, out sec, out clip, out ev)) return clip;
            ev = 0f;
            return "~" + (act != null ? act.GetType().Name : "?") + "/" + DirOf(act as ICombatEffectContainer);
        }

        /// UYF's dodge (docs/uyf-reference.md §3.4) for an unblockable blow: the arrow's direction in the strike's plane and
        /// how far the head must move along it, from the head's place at the telegraph. A sideways sweep at head or shoulder
        /// height: duck (UYF's default, 180 deg; 8 cm, seated 5 cm). Anything else (chops, thrusts, low sweeps): lean away from where the
        /// blade crosses (12.5 cm). Bosses: 30 cm. `[Combat] LeanDodge = true` brings back the earlier rule instead (any 0.2 m
        /// sideways or down, any blow; a red bar).
        /// The player's playtest 2026-10-17: "Just take out dodge arrows entirely. I don't think we can get them to feel good
        /// right now" (you can't dodge up, nor toward the enemy). No arrow is shown any more: a blow the game can't block (its
        /// red glow) gets a bar as every other blow (RedBlocks), blocked with the shield or the weapon on it, or stepped or
        /// dashed out of its reach (the bar dims). The arrow's code below stays for a later return.
        public static bool DodgeArrows = false;
        /// Red (unblockable) blows are blocked on their bar like the others (Physical, not LeanDodge): the guard on the bar
        /// forces the game's block (Controller.ForceBlock - CombatEffect.Apply honours it for any blow without IgnoreBlock); no
        /// counter (the game's own rule: it can't be countered); no lean dodge.
        public static bool RedBlocks => CombatSettings.Physical && !CombatSettings.LeanDodge;

        void ChooseDodge(Strike s)
        {
            s.Dodge = DodgeArrows && !s.Blockable && !CombatSettings.LeanDodge;
            if (!s.Dodge) return;
            bool boss = false;
            try { AI a = s.Ai != null ? s.Ai.GetComponent<AI>() : null; boss = a != null && a.Ref != null && a.Ref.Boss; } catch (Exception) { }
            Vector3 head = VRRig.I.HeadWorldPos, f;
            bool duck = true;
            float x = 0f;
            if (s.Shape != null && Toward(s, head, out f))
            {
                Vector3 c, axis;
                Bar(s, head, f, out c, out axis);
                Vector3 r = Vector3.Cross(Vector3.up, f).normalized;
                x = Vector3.Dot(c - head, r);
                float y = c.y - head.y;
                bool sweep = s.Crossing >= 0.35f && Mathf.Abs(axis.y) > 0.7f;   // the bar stands upright: the blade sweeps sideways
                duck = sweep && y > -0.45f;
            }
            // the duck: UYF's DodgeDown distance, 8 cm standing, 5 cm seated (SeatedRules; boss strikes keep 30 cm)
            if (duck) { s.DodgeX = 0f; s.DodgeY = -1f; s.DodgeNeed = boss ? 0.3f : SeatedRules.Duck; }
            else { s.DodgeX = x > 0.04f ? -1f : 1f; s.DodgeY = 0f; s.DodgeNeed = boss ? 0.3f : 0.125f; }
            // the arrow's direction fixed now, in tracking space (UYF: from the head's place at the premonition, along the
            // arrow as shown). Taken again from the attacker's place at the impact, it flipped when the attacker moved round
            // the player meanwhile (the shadow warriors step aside: BC1 "head -0.187 m along" after a lean the right way)
            Vector3 fa;
            if (Toward(s, head, out fa))
            {
                Vector3 rr = Vector3.Cross(Vector3.up, fa).normalized;
                s.DodgeDirT = VRRig.I.FromWorldDir((rr * s.DodgeX + Vector3.up * s.DodgeY).normalized).normalized;
                s.DodgeDirFixed = true;
            }
        }

        /// The arrow's direction in the world now: the one fixed at the telegraph (turned with the rig), else from the
        /// attacker's direction f.
        Vector3 DodgeDirWorld(Strike s, Vector3 f)
        {
            if (s.DodgeDirFixed) return VRRig.I.ToWorldDir(s.DodgeDirT).normalized;
            Vector3 r = Vector3.Cross(Vector3.up, f).normalized;
            return (r * s.DodgeX + Vector3.up * s.DodgeY).normalized;
        }

        static string DodgeName(Strike s) => (s.DodgeY < -0.5f ? "duck" : s.DodgeX < 0f ? "lean-left" : "lean-right") + " " + s.DodgeNeed.ToString("0.000") + " m";

        /// How far along the arrow the head is now (1 = far enough): its move since the telegraph in tracking space (walking and
        /// turning don't count), turned into the world by the rig's yaw, onto the arrow (UYF's CheckDodge).
        float DodgeProgress(Strike s, Vector3 f)
        {
            if (!s.Dodge || s.DodgeNeed <= 0f) return 0f;
            if (s.DodgeDirFixed) return Vector3.Dot(VRInput.HeadPos - s.HeadT0, s.DodgeDirT) / s.DodgeNeed;
            Vector3 r = Vector3.Cross(Vector3.up, f).normalized;
            Vector3 d = r * s.DodgeX + Vector3.up * s.DodgeY;
            Vector3 delta = VRRig.I.ToWorldDir(VRInput.HeadPos - s.HeadT0);
            return Vector3.Dot(delta, d) / s.DodgeNeed;
        }

        /// The strike's hit direction (its first CombatEffectHit), for the trace.
        static string DirOf(ICombatEffectContainer cont)
        {
            try
            {
                CombatEffectHit hit = cont != null ? FirstHit(cont.CombatEffects, 0) : null;
                return hit != null ? hit.Direction + "/" + hit.VerticalDirection : "-";
            }
            catch (Exception) { return "?"; }
        }

        /// A strike replaced by a newer one of the same attacker: a finished one keeps showing its result; a waiting follow-up
        /// just goes.
        void Retire(Strike s)
        {
            if (s.Result != null && s.Result != "window closed" && (s.Vis != null || s.Arr != null) && FightClock.Now - s.ResultAt < ShowResult) m_fading.Add(s);
            else Close(s);
        }

        static void HideMarks(Strike s)
        {
            if (s.Vis != null && s.Vis.Root.gameObject.activeSelf) s.Vis.Root.gameObject.SetActive(false);
            if (s.Arr != null && s.Arr.Root.gameObject.activeSelf) s.Arr.Root.gameObject.SetActive(false);
            if (s.Ring != null && s.Ring.Root.gameObject.activeSelf) s.Ring.Root.gameObject.SetActive(false);
            if (s.GroundMark != null && s.GroundMark.Root.gameObject.activeSelf) s.GroundMark.Root.gameObject.SetActive(false);
        }

        void Close(Strike s)
        {
            if (s.Vis != null) { s.Vis.Destroy(); s.Vis = null; }
            if (s.Arr != null) { s.Arr.Destroy(); s.Arr = null; }
            if (s.Ring != null) { s.Ring.Destroy(); s.Ring = null; }
            if (s.GroundMark != null) { s.GroundMark.Destroy(); s.GroundMark = null; }
        }

        // ---------------------------------------------------------------------------------------- the impact

        sealed class Block { public Strike S; public Pose P; public ICombatEffectContainer C; }

        static bool ApplyPrefix(ICombatEffectContainer a_container, CombatTarget a_target, Transform a_origin, out Block __state)
        {
            __state = null;
            if (I == null || !Physical.Live || a_container == null) return true;
            try
            {
                PlayerController pc = PlayerController.Instance;
                if (pc == null || a_target.Targetable == null || a_target.Targetable.gameObject != pc.gameObject) return true;
                Targetable src = a_container.Source;
                AIController ai = src != null ? src.GetComponent<AIController>() : null;
                if (ai == null) return true;
                if ((a_container.Category & (CombatCategory.LightMelee | CombatCategory.HeavyMelee)) == 0 || a_origin != src.transform)
                {
                    // melee passes the attacker's own transform; projectiles and areas their own - not judged here. Noted when
                    // the attacker's live strike asked for a dodge (the shadow warrior's duck judged a hit, CPZ6: is its blow a
                    // projectile or an area?)
                    Strike ls;
                    if (I.m_strikes.TryGetValue(ai.GetInstanceID(), out ls) && ls.Result == null)
                    {
                        // a telegraphed blow (a bar or the dodge arrow shown) that lands as the attacker's own Ability container:
                        // the corrupted juggernaut's slam (ActionSelfAI) and the ogre's combo blows (BC1, BC2: "delivered as
                        // Ability from Ogre(Clone)") - judged like a blow: the marker's promise (and an ogre's combo goes on
                        // past a dodged or blocked first blow)
                        if ((a_container.Category & CombatCategory.Ability) != 0 && a_origin == src.transform && !CombatMove.Dodging)
                            return I.Resolve(pc, ai, src, a_container, out __state);
                        if (!ls.Dodge) return true;
                        DodgeBypassed++;
                        Note("dodge strike of " + WeaponGrips.ModelKey(ai.name) + " delivered as " + a_container.Category + " from " +
                             (a_origin != null ? a_origin.name : "-") + " (" + a_container.GetType().Name + "): not judged by the guard");
                    }
                    return true;
                }
                if (CombatMove.Dodging) return true;          // the Dash's window: the game's Ignore skips it (with its chime)
                return I.Resolve(pc, ai, src, a_container, out __state);
            }
            catch (Exception e) { Plugin.L.LogWarning("Guard: resolve: " + e); return true; }
        }

        static Exception ApplyFinalizer(Exception __exception, Block __state)
        {
            if (__state != null)
            {
                PlayerController pc = PlayerController.Instance;
                if (pc != null && pc.ForceBlock > 0) pc.ForceBlock--;
                try { I.Blocked(__state); } catch (Exception e) { Plugin.L.LogWarning("Guard: block: " + e.Message); }
            }
            return __exception;
        }

        bool Resolve(PlayerController pc, AIController ai, Targetable src, ICombatEffectContainer container, out Block block)
        {
            block = null;
            Strike s;
            Defiant.Action act0 = RunningStrike(ai);
            if (m_strikes.TryGetValue(ai.GetInstanceID(), out s) && s.Result == null && s.Phantom)
            {
                // audit item 12: the blade is behind the attacker at this hit event (the flat game lands it at 1-1.8 m): no
                // blow at the player - no damage, no feedback; the attack's next blow brings its own telegraph and lead
                Finish(s, "PHANTOM", Color.clear);
                Phantoms++;
                Note("phantom hit " + WeaponGrips.ModelKey(ai.name) + " " + s.Clip + "@" + s.Event.ToString("0.00") + ": its blade behind it - not a blow at the player");
                return false;
            }
            if (s != null && s.Result == null && s.Shove)
            {
                // UYF's shove (E7): no damage, no guard, blocked or not - the hero pushed back along the attacker's line
                Finish(s, "SHOVED", Color.clear);
                Vector3 to;
                Vector3 away = Toward(s, VRRig.I.HeadWorldPos, out to) ? -to : ai.transform.forward;
                Note("shove " + WeaponGrips.ModelKey(ai.name));
                Shoves.Landed(ai, away);
                m_lastImpact = FightClock.Now;
                m_lastImpactBy = ai.GetInstanceID();
                s_lastBlowOf[ai.GetInstanceID()] = m_lastImpact;
                DrawPause(ai, false);
                return false;
            }
            if (!m_strikes.TryGetValue(ai.GetInstanceID(), out s) || s.Result != null)
            {
                // no telegraph seen and no follow-up bar: judged all the same, toward the attacker, without a marker
                s = new Strike { Ctrl = ai, Ai = ai, T = src, Model = ai.Model, Blockable = true, Start = FightClock.Now - 1f, HeadT0 = VRInput.HeadPos, Expected = 0f };
                s.OffView = Mathf.Abs(ThreatWarning.BearingOf(ai.transform.position)) > ThreatWarning.ViewHalf;
                Unguarded++;
                Note("unguarded " + WeaponGrips.ModelKey(ai.name) + " " + (act0 != null ? act0.GetType().Name : "-"));
            }
            else if (s.FollowUp) FollowUpsJudged++;
            if (s.FromAhead && s.Result == null && !s.OffView)
            {
                // audit E3: a blow shown ahead - its marker's lead (from when it appeared to the impact)
                float lead = FightClock.Now - s.Start, want = 0f;
                try { want = EnemyTable.LeadFor(ai); } catch (Exception) { }
                AheadJudged++;
                AheadMinLead = Mathf.Min(AheadMinLead, lead);
                float prev;   // the time since the blow before (its run's own gap, not a fresh lead)
                // a blow whose telegraph came out of view waits the off-view lead by design (ThreatWarning): that wait is not
                // its run's rhythm, even when the attacker is in view again by its impact
                bool hasPrev = s_lastBlowOf.TryGetValue(ai.GetInstanceID(), out prev);
                if (s.OffHeld) AheadOffHeld++;
                else if (hasPrev && FightClock.Now - prev < 3f)
                {
                    AheadGapMin = Mathf.Min(AheadGapMin, FightClock.Now - prev);
                    AheadGapMax = Mathf.Max(AheadGapMax, FightClock.Now - prev);
                }
                if (AheadJudged <= 60)
                    Plugin.L.LogInfo("Guard: ahead judged " + s.Key + " blow " + s.Blow + " lead " + lead.ToString("0.00") + " gap " + (hasPrev ? (FightClock.Now - prev).ToString("0.00") : "-") +
                                     (s.OffHeld ? " offHeld" : "") + " | " + ThreatWarning.StrikeState(ai) + " | " + EnemyTuning.SlowState(ai) + " | hit-stopped " + CombatFeel.StoppedSince(ai, s.Start));
                if (lead < want - 0.1f) { AheadShort++; Note("AHEAD SHORT " + WeaponGrips.ModelKey(ai.name) + " blow " + s.Blow + " " + lead.ToString("0.00") + " s (wants " + want.ToString("0.00") + ")"); }
            }
            string key = WeaponGrips.ModelKey(ai.name) + (s.FollowUp ? " (follow-up)" : "");
            Vector3 head = VRRig.I.HeadWorldPos;
            // P4-16: what the circle showed on the frame before this impact (the live preview's last verdict), against the outcome
            s.GreenSeen = s.Result == null && s.Valid && s.ValidShield && !s.OffView && s.Threat;
            s.Judging = true;
            s.Cover = s.FaceDeg = s.CoverDepth = -1f;   // the record's coverage is the impact's own Judge (below), else none
            bool red = !CombatUtils.IsBlockable(container);
            bool blockable = !red || RedBlocks;
            if (s.Expected > 0f && !s.OffView && !s.FollowUp)
            {
                float lead = FightClock.Now - s.Start;
                MinLead = Mathf.Min(MinLead, lead);
                float want = 0f;
                try { want = EnemyTable.LeadFor(ai); } catch (Exception) { }
                if (lead < want - 0.034f)
                {
                    LeadShort++;
                    Note("SHORT LEAD " + WeaponGrips.ModelKey(ai.name) + " " + lead.ToString("0.00") + " s (wants " + want.ToString("0.00") + "): " + ThreatWarning.StrikeState(ai) + " | " +
                         EnemyTuning.SlowState(ai) + " | hit-stopped " + CombatFeel.StoppedSince(ai, s.Start) + " | strike " + s.Key);
                    if (LeadShort <= 100) Plugin.L.LogInfo("Guard: SHORT LEAD " + WeaponGrips.ModelKey(ai.name) + " " + lead.ToString("0.00") + " s, its plan: " + ThreatWarning.TraceOf(ai));
                }
                else if (lead > want + 0.3f && CombatSettings.LeadStyle == CombatSettings.LeadRule.Slow && ++s_longSlow <= 40)
                    Plugin.L.LogInfo("Guard: long Slow lead " + WeaponGrips.ModelKey(ai.name) + " " + lead.ToString("0.00") + " s (wants " + want.ToString("0.00") + "), its plan: " + ThreatWarning.TraceOf(ai));
                key += " after " + lead.ToString("0.00") + " s";
                try
                {
                    string how;
                    EnemyTable.Arch arch = EnemyTable.Of(ai, out how);
                    if (Leads.Count > 200) Leads.RemoveAt(0);
                    Leads.Add(new LeadRow { Model = WeaponGrips.ModelKey(ai.name), Arch = arch.Name, Lead = lead, Want = want, Speed = ai.CalculateAttackSpeed(), Step = CombatSettings.Difficulty });
                }
                catch (Exception) { }
            }
            m_lastImpact = FightClock.Now;
            m_lastImpactBy = ai.GetInstanceID();
            s_lastBlowOf[ai.GetInstanceID()] = m_lastImpact;
            DrawPause(ai, false);
            Vector3 fa;
            if (!Toward(s, head, out fa)) fa = ai.transform.forward;
            // where the blade passed is this blow's: learn it for this attacker, clip and hit event (the atlas's run-time half)
            if (s.Track != null && s.Shape == null) { try { SwingAtlas.Learn(s.Track, head, fa); } catch (Exception e) { Plugin.L.LogWarning("Guard: learn: " + e.Message); } }
            // 1. the guard (blockable strikes only)
            if (blockable)
            {
                bool met;
                Pose p = Judge(s, head, out met);
                if (p != null)
                {
                    IActionParry act = ActionCounter.GetAction(src);
                    bool counter = !red && act != null && CombatSettings.Counter != CombatSettings.CounterRule.Off && (met || CombatSettings.Counter == CombatSettings.CounterRule.AnyBlock);
                    if (counter)
                    {
                        s.BlockedByShield = p.Shield;
                        Finish(s, "COUNTERED", new Color(1f, 0.8f, 0.2f));
                        Counters++;
                        Note("counter " + key + (p.Shield ? " (shield)" : " (weapon)") + (s.OffView ? " off-view" : ""));
                        Counter(pc, src, act, p);
                        Engaged();
                        return false;
                    }
                    s.BlockedByShield = p.Shield;
                    Finish(s, "BLOCKED", new Color(0.3f, 1f, 0.4f));
                    Blocks++;
                    s_blockFrame = Time.frameCount;
                    SuperMeter.Add(1f, "block");
                    bool heavy = (container.Category & CombatCategory.HeavyMelee) != 0;
                    Note("block " + key + (p.Shield ? " (shield)" : " (weapon)") + (heavy ? " heavy" : "") + (red ? " red" : "") + (met ? " met" : "") +
                         (s.OffView ? " off-view" : "") + (p.Shield ? " at " + Diag.V(p.Center - head) : ""));
                    if (red) RedBlocked++;
                    pc.ForceBlock++;
                    block = new Block { S = s, P = p, C = container };
                    // UYF's block: the player is pushed back along the attacker's line (0.06 m; a heavy blow 1 m) and the view
                    // shakes 0.05 s by 0.05 m - each its own setting
                    CombatMove.Knockback(-fa, heavy ? 1f : 0.06f, "blocked");
                    CameraShake.Add(0.05f, 0.05f, 0.008f, "blocked");
                    // UYF's SpringImpact: a blow blocked with the weapon knocks it along the blow (impulse 5, springs x0.5 for 0.5 s)
                    if (!p.Shield && VRRig.I != null) WeaponFeel.Impact(ReferenceEquals(p, s_pose[0]), VRRig.I.FromWorldDir(-fa), 5f);
                    // Stage 2: UYF's "defending builds toward the guard break" - the block chips the attacker's guard
                    Poise.Blocked(ai);
                    Engaged();
                    return true;
                }
            }
            // 2. out of the way.
            //  UYF: an unblockable blow is dodged by moving the head along its arrow (from where it was at the telegraph) by the
            //  arrow's distance; the Dash's window covers every blow (the prefix let it through already).
            //  LeanDodge (the earlier rule, a setting): the head moved >= DodgeDistance sideways or down, for any blow.
            if (s.Dodge)
            {
                float prog = DodgeProgress(s, fa);
                if (prog >= 1f)
                {
                    Finish(s, "DODGED", new Color(0.7f, 0.9f, 1f));
                    Dodges++;
                    SuperMeter.Add(1f, "dodge");
                    Note("dodged " + key + " (" + DodgeName(s) + ", head " + (prog * s.DodgeNeed).ToString("0.000") + " m along)");
                    CombatSounds.Layer(CombatSounds.Dodged, head + fa * 0.3f, 0.7f);
                    Avoided(ai, head);
                    return false;
                }
                DodgeShort++;
                Note("dodge short " + key + " (" + DodgeName(s) + ", head " + (prog * s.DodgeNeed).ToString("0.000") + " m along)");
            }
            else if (CombatSettings.LeanDodge || !blockable)
            {
                // (the earlier rule only: with RedBlocks a red blow is blockable, and leaning does nothing)
                Vector3 d = VRInput.HeadPos - s.HeadT0;
                Vector3 line = VRRig.I.FromWorldDir(head - ai.transform.position);
                line.y = 0f;
                Vector3 perp = Vector3.Cross(Vector3.up, line).normalized;
                float lateral = Mathf.Abs(Vector3.Dot(d, perp)), down = -d.y;
                float dd = CombatSettings.DodgeDistance;
                if (perp.sqrMagnitude > 0.5f && (lateral >= dd || down >= dd))
                {
                    Finish(s, "DODGED", new Color(0.7f, 0.7f, 0.7f));
                    Dodges++;
                    Note("dodged " + key + " (head " + (lateral >= dd ? "side " + lateral.ToString("0.00") : "down " + down.ToString("0.00")) + " m)");
                    Avoided(ai, head);
                    return false;
                }
            }
            string sector;
            if (!InSector(s, head, container, out sector))
            {
                Finish(s, "OUT OF REACH", new Color(0.7f, 0.7f, 0.7f));
                OutOfRange++;
                if (s_lastSide) SideMisses++;
                Note("out of reach " + key + " (" + sector + ")");
                Avoided(ai, head);
                return false;
            }
            // 3. hit: the game applies it; the hit-taken feedback runs on the damage
            if (blockable && !s.OffView) Note("guard missed " + key + ": " + WhyNot(s, head));
            Finish(s, "HIT", new Color(1f, 0.15f, 0.1f));
            HitsTaken++;
            SuperMeter.Add(5f, "hit taken");
            HitFrame = Time.frameCount;
            CombatFeel.HeroHit(ai, red);   // in the world and the hands (also with god mode, where no damage arrives)
            Note("hit by " + key + (red ? " (red)" : "") + (s.OffView ? " off-view" : ""));
            AIManager.Instance?.DelayNextAttack(pc.Targetable, CombatSettings.SwapGrace);
            return true;
        }

        // ---------------------------------------------------------------------------------------- combos: the later blows

        /// (for Poise: a guard broken during the attacker's own strike ends that strike)
        public static Defiant.Action RunningStrikeOf(Controller c) => RunningStrike(c);

        // ---- the fight's start (candidate 8's picture walk: a thief / a soldier right on top of the player, attacking at once) ----
        /// At the first frame of play: every enemy within StartClear of the hero is pushed out to StartRoom, and no enemy
        /// starts a blow for StartHold seconds - the spawn rule (nothing within 0.76 m) and a fair moment to look round.
        public const float StartClear = 1.8f, StartRoom = 2.5f, StartHold = 1.5f;
        public static int StartPushes, StartHolds;
        static float s_lastLive = -100f;

        static void FightStart()
        {
            if (!Physical.Live) return;
            float now = FightClock.Now;
            bool fresh = now - s_lastLive > 0.5f;
            s_lastLive = now;
            if (!fresh) return;
            try
            {
                PlayerController pc = PlayerController.Instance;
                if (pc == null) return;
                Targetable me = pc.Targetable;
                BetterList<Targetable> all = me.EnumerateWithinRadius(40f, me.GetEnemyTeams());
                int held = 0, pushed = 0;
                for (int i = 0; i < all.size; i++)
                {
                    Targetable t = all[i];
                    AIController ai = t != null ? t.Controller as AIController : null;
                    if (ai == null) continue;
                    Hold(ai, StartHold);
                    held++;
                    Vector3 away = ai.transform.position - pc.transform.position;
                    away.y = 0f;
                    float d = away.magnitude;
                    if (d < StartClear)
                    {
                        if (d < 1e-3f) away = -pc.transform.forward; else away /= d;
                        EnemyPush.Start(ai, away, StartRoom - d, 0.35f);
                        pushed++;
                    }
                }
                StartHolds += held;
                StartPushes += pushed;
                Plugin.L.LogInfo("Guard: the fight's start - " + held + " enemies hold their blows " + StartHold.ToString("0.0") + " s, " + pushed +
                                 " pushed out of " + StartClear.ToString("0.0") + " m to " + StartRoom.ToString("0.0") + " m");
            }
            catch (Exception e) { Plugin.L.LogWarning("Guard: fight start: " + e.Message); }
        }

        /// Poise: this enemy starts no strike and no evade hop for exactly `seconds` from now (0 = let it go again).
        public static void Hold(Controller c, float seconds)
        {
            if (c == null) return;
            int id = c.GetInstanceID();
            if (seconds <= 0f) { s_stunned.Remove(id); return; }
            s_stunned[id] = FightClock.Now + seconds;
        }

        /// How much longer this enemy is held (s; 0 = free).
        public static float HeldFor(Controller c)
        {
            float until;
            return c != null && s_stunned.TryGetValue(c.GetInstanceID(), out until) ? Mathf.Max(0f, until - FightClock.Now) : 0f;
        }

        /// Poise: this enemy's live strike is over (a guard break): its bar goes quietly.
        public static void DropStrike(Controller c)
        {
            Strike s;
            if (I == null || c == null || !I.m_strikes.TryGetValue(c.GetInstanceID(), out s) || s.Result != null) return;
            s.Result = "window closed";
            s.ResultAt = FightClock.Now;
            s.ResultColour = Color.clear;
            // UYF's "Stop Attacks": an attack stopped part-way is followed by the medium pause, from now
            if (CombatSettings.VariedPauses && c is AIController)
            {
                s_lastBlowOf[c.GetInstanceID()] = FightClock.Now;
                DrawPause((AIController)c, true);
            }
        }

        /// The attacker's running strike action, or null.
        static Defiant.Action RunningStrike(Controller c)
        {
            try
            {
                Actor actor = c != null ? c.GetComponent<Actor>() : null;
                return actor != null ? actor.GetRunningAction<IActionAICombat>() as Defiant.Action : null;
            }
            catch (Exception) { return null; }
        }

        sealed class ComboShape { public System.Reflection.FieldInfo Index, Blows, StopBool, DontStop, HasHit; }
        static readonly Dictionary<Type, ComboShape> s_combo = new Dictionary<Type, ComboShape>();

        /// The multi-blow strike actions whose blows can come without a telegraph of their own: each blow is one "hit" event
        /// (OnHit), `m_hitIndex` counts it there: the combo AIs (`ActionMeleeComboAI`: ogres, two bosses - an offline scan of
        /// the ai_* bundles) and the templar's multi-hit. The interactive combo telegraphs every blow and stops at its first hit.
        static ComboShape ShapeOf(Defiant.Action act)
        {
            Type t = act.GetType();
            ComboShape cs;
            if (s_combo.TryGetValue(t, out cs)) return cs;
            cs = null;
            try
            {
                string blows = t == typeof(ActionMeleeComboAI) ? "m_attacks" : t == typeof(ActionTemplarMeleeAI) ? "m_effects" : null;
                if (blows != null)
                {
                    cs = new ComboShape { Index = AccessTools.Field(t, "m_hitIndex"), Blows = AccessTools.Field(t, blows), HasHit = AccessTools.Field(t, "m_hasHit") };
                    if (t == typeof(ActionMeleeComboAI)) { cs.StopBool = AccessTools.Field(t, "m_stopComboBool"); cs.DontStop = AccessTools.Field(t, "m_dontStopOnHit"); }
                    if (cs.Index == null || cs.Blows == null) cs = null;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Guard: combo shape of " + t.Name + ": " + e.Message); cs = null; }
            s_combo[t] = cs;
            return cs;
        }

        static int BlowIndex(Defiant.Action act)
        {
            ComboShape cs = ShapeOf(act);
            try { return cs != null ? (int)cs.Index.GetValue(act) : -1; } catch (Exception) { return -1; }
        }

        /// Before a blow event: which blow it is.
        static void BlowPrefix(Defiant.Action __instance, out int __state) { __state = BlowIndex(__instance); }

        /// After a blow event (landed, blocked, dodged or out of the game's own reach): the next blow's bar at once, if one comes.
        static void BlowPostfix(Defiant.Action __instance, int __state)
        {
            if (I == null || __state < 0 || !Physical.Live) return;
            try { I.AfterBlow(__instance, __state); } catch (Exception e) { Plugin.L.LogWarning("Guard: after a blow: " + e.Message); }
        }

        static readonly Dictionary<string, float> s_gap = new Dictionary<string, float>();
        /// Combo blows that didn't land after which the game's combo was let go on (AfterBlow).
        public static int CombosGoOn;
        readonly Dictionary<int, float> m_blowAt = new Dictionary<int, float>();

        /// The time between two blows of one combo, learned per attacker model and action (s; 0.55 until seen).
        static float Gap(string key)
        {
            float g;
            return s_gap.TryGetValue(key, out g) ? g : 0.55f;
        }

        void AfterBlow(Defiant.Action act, int before)
        {
            AIController ai = act.Actor != null ? act.Actor.GetComponent<AIController>() : null;
            PlayerController pc = PlayerController.Instance;
            if (ai == null || pc == null || ai.Target == null || ai.Target.gameObject != pc.gameObject) return;
            Model model = ai.Model;
            string key = WeaponGrips.ModelKey(model != null ? model.name : ai.name) + "/" + act.GetType().Name;
            float now = FightClock.Now, last, old;
            int aid = act.GetInstanceID();
            // learn the gap between blows of one run of the action
            if (before > 0 && m_blowAt.TryGetValue(aid, out last) && now - last < 3f) s_gap[key] = s_gap.TryGetValue(key, out old) ? Mathf.Lerp(old, now - last, 0.5f) : now - last;
            m_blowAt[aid] = now;
            ComboShape cs = ShapeOf(act);
            if (cs == null || !act.IsRunning) return;
            Array blows = cs.Blows.GetValue(act) as Array;
            int n = blows != null ? blows.Length : 0;
            if (before + 1 >= n) return;   // that was the last blow
            // a combo that stops at its first landed blow: the game set its stop flag in OnHit whenever the player stood in its
            // reach (ActionMeleeComboAI.CanHit) - before the blow was judged. UYF's combos go on past a blow that doesn't land:
            // blocked, dodged (the head along the arrow, or the Dash's window) or out of the sector's reach -> the game's stop
            // undone, so the next blow comes (its bar, and the interrupt crit between them). A blow that lands still stops it.
            string stopBool = cs.StopBool != null ? (string)cs.StopBool.GetValue(act) : null;
            if (cs.StopBool != null && cs.HasHit != null && (bool)cs.HasHit.GetValue(act) && !string.IsNullOrEmpty(stopBool) && !(bool)cs.DontStop.GetValue(act))
            {
                Strike judged = null;
                bool missed = CombatMove.Dodging ||
                              (m_strikes.TryGetValue(ai.GetInstanceID(), out judged) && (judged.Result == "BLOCKED" || judged.Result == "DODGED" || judged.Result == "OUT OF REACH" || judged.Result == "PHANTOM") &&
                               now - judged.ResultAt < 0.5f);
                if (!missed) return;
                try
                {
                    cs.HasHit.SetValue(act, false);
                    if (ai.Animator != null) ai.Animator.SetBool(stopBool, false);
                    CombosGoOn++;
                    Note("combo goes on: " + key + " blow " + (before + 1) + " " + (judged == null ? "dashed" : judged.Result.ToLowerInvariant()));
                }
                catch (Exception e) { Plugin.L.LogWarning("Guard: combo goes on: " + e.Message); return; }
            }
            ICombatEffectContainer next = null;
            ActionMeleeComboAI.AttackContainer ac = blows.GetValue(before + 1) as ActionMeleeComboAI.AttackContainer;
            if (ac != null) next = ac.Effects;
            NextBlow(ai, ai.Targetable, act, next, key, before + 1);
        }

        /// The next blow's bar: white or red by its own effects (gold if the action can be countered), filling over the learned
        /// gap; a fresh telegraph replaces it, the combo's end removes it.
        void NextBlow(AIController ai, Targetable src, Defiant.Action act, ICombatEffectContainer next, string key, int blow)
        {
            float now = FightClock.Now;
            // audit E3: its marker already shows (ahead of it, at its own lead): that marker becomes the live strike
            List<Strike> al;
            if (m_ahead.TryGetValue(ai.GetInstanceID(), out al))
            {
                while (al.Count > 0 && (al[0].Act != act || al[0].Blow < blow)) { Close(al[0]); al.RemoveAt(0); AheadDropped++; }
                if (al.Count > 0 && al[0].Blow == blow)
                {
                    Strike a = al[0];
                    al.RemoveAt(0);
                    Adopt(a);
                    FollowUps++;
                    float left = a.Start + a.Expected - now;
                    Note("follow-up " + key + " blow " + blow + " (shown ahead " + Mathf.Max(0f, now - a.Start).ToString("0.00") + " s) in " + left.ToString("0.00") + " s");
                    Poise.OfferInterrupt(ai, act, left);
                    if (SwingTrace.On) SwingTrace.Begin(a.Id, ai, a.Model, act.GetType().Name, DirOf(next ?? (act as ICombatEffectContainer)), a.Colour.ToString(), a.Clip, a.Event, true);
                    return;
                }
            }
            Model model = ai.Model;
            var f = new Strike { Ctrl = ai, Ai = ai, T = src, Model = model, Start = now, HeadT0 = VRInput.HeadPos, FollowUp = true, Act = act, Key = key };
            ICombatEffectContainer own = act as ICombatEffectContainer;   // the templar applies itself, blow by blow
            f.Blockable = next != null ? CombatUtils.IsBlockable(next) : own == null || CombatUtils.IsBlockable(own);
            IActionParry parry = act as IActionParry;
            f.Colour = !f.Blockable ? Look.Red : parry != null && parry.CanCounter ? Look.Gold : Look.White;
            f.Alpha = AlphaOf(ai, model, out f.Thrust);
            f.Expected = Gap(key);
            f.OffView = Mathf.Abs(ThreatWarning.BearingOf(ai.transform.position)) > ThreatWarning.ViewHalf;
            if (f.Colour == Look.Gold && CombatSettings.Counter == CombatSettings.CounterRule.Off) f.Colour = Look.White;
            Prepare(f);
            Strike old;
            int id = ai.GetInstanceID();
            if (m_strikes.TryGetValue(id, out old)) Retire(old);
            m_strikes[id] = f;
            FollowUps++;
            Note("follow-up " + key + " " + f.Colour + (f.Thrust ? " thrust" : " alpha " + f.Alpha.ToString("0")) + " in ~" + f.Expected.ToString("0.00") + " s");
            // Stage 2: UYF's interrupt crit between the blows (cut along it: the combo stops)
            Poise.OfferInterrupt(ai, act, f.Expected);
            if (SwingTrace.On) SwingTrace.Begin(f.Id, ai, model, act.GetType().Name, DirOf(next ?? own), f.Colour.ToString(), f.Clip, f.Event, true);
            f.Blow = blow;
            ClearAhead(id);
            if (!f.Phantom) MakeAhead(f);
        }

        // ---------------------------------------------------------------------------------------- audit E3: blows shown ahead

        /// UYF's combo density (docs/uyf-parity-audit.md E3; uyf-reference.md 3.1-3.2): the later blows of a run keep their
        /// authored gaps (0.17-0.8 s), each with its own premonition shown its lead before its impact - so it overlaps the blow
        /// before (UYF instances up to 8). With Lead = Hold and `[Combat] ComboGaps`: when a blow of a multi-hit whose clip
        /// holds its later blows' hit events (the templar multi-hit: the shadow warriors' flurry, the general's double swing)
        /// goes live, the later blows' strikes are made at once - each at the time its hit event comes at the clip's natural
        /// speed after this blow's impact, its marker shown from `lead` before that - and drawn, not judged; each becomes the
        /// live strike after the blow before (NextBlow) or at its own telegraph (Open), its impact read again from the
        /// animation then. Threats doesn't hold such a blow (AheadShown): the run swings in its own rhythm. The templar's
        /// wind-up hits (phantoms) get none, and none come from them (the real blow after one still waits for its lead).
        /// Combos whose blows are separate states (the ogres) keep their follow-up bars.
        static bool AheadOn => CombatSettings.ComboGaps && CombatSettings.Physical && CombatSettings.LeadStyle == CombatSettings.LeadRule.Hold;

        void MakeAhead(Strike live)
        {
            if (!AheadOn || live.Act == null || live.Ai == null || live.Ctrl == null || live.Blow < 0 || string.IsNullOrEmpty(live.Clip) || live.Clip[0] == '~' || live.Expected <= 0f) return;
            ComboShape cs = ShapeOf(live.Act);
            if (cs == null) return;
            Array blows = null;
            try { blows = cs.Blows.GetValue(live.Act) as Array; } catch (Exception) { }
            int n = blows != null ? blows.Length : 0;
            if (live.Blow + 1 >= n) return;
            float speed = ThreatWarning.NaturalSpeed(live.Ctrl);
            if (speed <= 0.01f || ThreatWarning.LaterHits(live.Ctrl, live.Clip, live.Event, s_evs) == 0) return;
            float now = FightClock.Now, impact = live.Start + live.Expected, lead = 0.9f;
            // a follow-up (no telegraph, no hold): its own impact read from the animation too, not the learned gap
            float sec0, ev0, sp0;
            string clip0;
            if (live.FollowUp && ThreatWarning.HitClip(live.Ctrl, out sec0, out clip0, out ev0, out sp0) && clip0 == live.Clip && Mathf.Abs(ev0 - live.Event) < 0.02f)
            {
                impact = now + sec0;
                live.Expected = Mathf.Max(0.05f, impact - live.Start);
            }
            try { lead = EnemyTable.LeadFor(live.Ai); } catch (Exception) { }
            string model = WeaponGrips.ModelKey(live.Model != null ? live.Model.name : live.Ai.name);
            IActionParry parry = live.Act as IActionParry;
            ICombatEffectContainer own = live.Act as ICombatEffectContainer;
            List<Strike> l = null;
            for (int i = 0; i < s_evs.Count && live.Blow + 1 + i < n; i++)
            {
                float ev = s_evs[i], at = impact + (ev - live.Event) / speed;
                SwingAtlas.Shape shape = SwingAtlas.Find(model, live.Clip, ev);
                if (SwingAtlas.Phantom(shape)) continue;
                var a = new Strike { Ctrl = live.Ctrl, Ai = live.Ai, T = live.T, Model = live.Model, Act = live.Act, Key = live.Key, FollowUp = true, Ahead = true, Blow = live.Blow + 1 + i,
                                     Clip = live.Clip, Event = ev, Shape = shape, Alpha = live.Alpha, Thrust = live.Thrust, Heavy = live.Heavy, HeadT0 = VRInput.HeadPos,
                                     TelePos = live.Ctrl.transform.position, OffView = live.OffView };
                ICombatEffectContainer next = null;
                try { ActionMeleeComboAI.AttackContainer ac = blows.GetValue(a.Blow) as ActionMeleeComboAI.AttackContainer; if (ac != null) next = ac.Effects; } catch (Exception) { }
                a.Blockable = next != null ? CombatUtils.IsBlockable(next) : own == null || CombatUtils.IsBlockable(own);
                a.Colour = !a.Blockable ? Look.Red : parry != null && parry.CanCounter && CombatSettings.Counter != CombatSettings.CounterRule.Off ? Look.Gold : Look.White;
                a.Start = Mathf.Max(now, at - lead);
                a.Expected = at - a.Start;
                ChooseDodge(a);
                if (l == null) l = new List<Strike>();
                l.Add(a);
                AheadMade++;
            }
            if (l == null) return;
            m_ahead[live.Ctrl.GetInstanceID()] = l;
            Note("ahead " + live.Key + " blow " + live.Blow + ": " + l.Count + " later blow(s), the next " + (l[0].Start + l[0].Expected - impact).ToString("0.00") + " s after this one, its marker in " + (l[0].Start - now).ToString("0.00") + " s");
        }

        void ClearAhead(int id)
        {
            List<Strike> l;
            if (!m_ahead.TryGetValue(id, out l) || l.Count == 0) return;
            foreach (Strike a in l) Close(a);
            AheadDropped += l.Count;
            l.Clear();
        }

        /// A blow shown ahead becomes the live strike: its impact read again from the animation (the hold, hit-stops and the
        /// game's own pace are in it); a marker already up keeps its start (its fill goes on), one not up yet starts at the lead.
        void Adopt(Strike a)
        {
            float now = FightClock.Now;
            int id = a.Ctrl.GetInstanceID();
            Strike old;
            if (m_strikes.TryGetValue(id, out old) && old != a) Retire(old);
            a.Ahead = false;
            a.FromAhead = true;
            a.OffView = Mathf.Abs(ThreatWarning.BearingOf(a.Ctrl.transform.position)) > ThreatWarning.ViewHalf;   // as a fresh follow-up's
            if (a.Began)
            {
                // UYF samples where the head is after the blow before (its SampleDodgePosition tracks): a dodge of this blow is
                // a move from here, not the one that dodged the blow before
                a.HeadT0 = VRInput.HeadPos;
                a.DodgeDirFixed = false;
                if (a.Dodge) ChooseDodge(a);
            }
            a.TelePos = a.Ctrl.transform.position;   // the atlas measured each blow's root motion from its own telegraph: about now
            float sec, ev;
            string clip;
            if (ThreatWarning.HitClip(a.Ctrl, out sec, out clip, out ev) && clip == a.Clip && Mathf.Abs(ev - a.Event) < 0.02f) Retime(a, now + sec);
            if (a.Shape == null) a.Track = new SwingAtlas.Track { C = a.Ctrl, M = a.Model, Model = WeaponGrips.ModelKey(a.Model != null ? a.Model.name : a.Ctrl.name), Clip = a.Clip, Event = a.Event, TelePos = a.TelePos, TeleRot = a.Ctrl.transform.rotation };
            m_strikes[id] = a;
            AheadAdopted++;
        }

        void Retime(Strike a, float impact)
        {
            float now = FightClock.Now;
            if (!a.Began)
            {
                float lead = 0.9f;
                try { lead = EnemyTable.LeadFor(a.Ai); } catch (Exception) { }
                a.Start = Mathf.Max(now, impact - lead);
            }
            a.Expected = Mathf.Max(0.05f, impact - a.Start);
        }

        /// The game's telegraph for a blow whose marker is already up (adopted after the blow before) or shown ahead: that
        /// marker stays - no fresh strike, no fresh lead. Its colour and blockability are the telegraph's.
        bool TelegraphAhead(Controller ctrl, bool blockable)
        {
            if (!AheadOn) return false;
            int id = ctrl.GetInstanceID();
            Defiant.Action act = RunningStrike(ctrl);
            if (act == null) return false;
            int blow = BlowIndex(act);   // the game's hit index: the blow this telegraph is for
            Strike s;
            bool live = m_strikes.TryGetValue(id, out s) && s.Result == null;
            if (live && s.FromAhead && s.Act == act && s.Blow == blow) { Retell(s, blockable); return true; }
            List<Strike> l;
            if (live || !m_ahead.TryGetValue(id, out l) || l.Count == 0 || l[0].Act != act || l[0].Blow != blow) return false;
            Strike a = l[0];
            l.RemoveAt(0);
            Adopt(a);
            Retell(a, blockable);
            return true;
        }

        void Retell(Strike s, bool blockable)
        {
            float now = FightClock.Now;
            bool counterable = blockable && ActionCounter.GetAction(s.T) != null;
            Look was = s.Colour;
            bool dodge = s.Dodge;
            s.Blockable = blockable;
            s.Colour = !blockable ? Look.Red : counterable && CombatSettings.Counter != CombatSettings.CounterRule.Off ? Look.Gold : Look.White;
            if (s.Colour != was || !blockable != dodge)
            {
                ChooseDodge(s);
                if (s.Dodge != dodge) { Close(s); s.Began = false; }   // a bar becomes an arrow (or back): drawn afresh
            }
            // where the attacker is now, as a fresh telegraph has it (Draw refreshes the flag only while the marker is up; until
            // then it kept the view from when the blow was made ahead); and whether ThreatWarning held the blow for being out
            // of view (its lead below is then the off-view lead)
            s.OffView = Mathf.Abs(ThreatWarning.BearingOf(s.Ctrl.transform.position)) > ThreatWarning.ViewHalf;
            s.OffHeld = ThreatWarning.StartedOffView(s.Ctrl);
            Retime(s, now + ThreatWarning.ExpectedOf(s.Ctrl));
            Strikes++;
            Note("telegraph " + s.Key + " blow " + s.Blow + " " + s.Colour + (s.OffView ? " off-view" : "") + (s.OffHeld ? " held" : "") + ": its marker shown ahead (up " + Mathf.Max(0f, now - s.Start).ToString("0.00") + " s), the blow in " + (s.Start + s.Expected - now).ToString("0.00") + " s");
        }

        /// Threats: this telegraph's blow is shown ahead - no hold for it (it keeps its run's rhythm).
        public static bool AheadShown(Controller c)
        {
            if (I == null || c == null || !AheadOn) return false;
            int id = c.GetInstanceID();
            Defiant.Action act = RunningStrike(c);
            if (act == null) return false;
            int blow = BlowIndex(act);
            Strike s;
            if (I.m_strikes.TryGetValue(id, out s) && s.Result == null && s.FromAhead && s.Act == act && s.Blow == blow) return true;
            List<Strike> l;
            return I.m_ahead.TryGetValue(id, out l) && l.Count > 0 && l[0].Act == act && l[0].Blow == blow;
        }

        /// Every frame: the blows shown ahead (dropped quietly once their run ends); the most markers of one attacker up at once.
        void DrawAhead(Vector3 head, float now)
        {
            foreach (var kv in m_ahead)
            {
                List<Strike> l = kv.Value;
                int up = 0;
                for (int i = l.Count - 1; i >= 0; i--)
                {
                    Strike a = l[i];
                    if (a.Ctrl == null || a.Model == null || a.Act == null || !a.Act.IsRunning || now > a.Start + a.Expected + 1f) { Close(a); l.RemoveAt(i); AheadDropped++; continue; }
                    Draw(a, head, now);
                    if (now >= a.Start && !a.OffView) up++;
                }
                if (up == 0) continue;
                Strike s;
                if (m_strikes.TryGetValue(kv.Key, out s) && s.Result == null && now >= s.Start && !s.OffView) up++;
                if (up > AheadOverlap) AheadOverlap = up;
            }
        }

        public static void ResetAhead()
        {
            AheadMade = AheadAdopted = AheadDropped = AheadJudged = AheadShort = AheadOverlap = AheadOffHeld = 0;
            AheadMinLead = AheadGapMin = 99f;
            AheadGapMax = 0f;
            ThreatWarning.LaterBlows = 0;
        }

        public static string AheadDescribe()
        {
            int pending = 0;
            if (I != null) foreach (var kv in I.m_ahead) pending += kv.Value.Count;
            return "ahead " + (AheadOn ? "on" : "off") + " made " + AheadMade + " adopted " + AheadAdopted + " dropped " + AheadDropped + " judged " + AheadJudged + " short " + AheadShort +
                   " minLead " + AheadMinLead.ToString("0.00") + " gapMin " + AheadGapMin.ToString("0.00") + " gapMax " + AheadGapMax.ToString("0.00") + " overlap " + AheadOverlap +
                   " pending " + pending + " laterBlows " + ThreatWarning.LaterBlows + " offHeld " + AheadOffHeld;
        }

        static readonly AccessTools.FieldRef<ActionMeleeAI, float> s_maxSep = AccessTools.FieldRefAccess<ActionMeleeAI, float>("m_maximumSeparation");

        static float Reach(ICombatEffectContainer c, AIController ai)
        {
            float r = 2.5f;
            try
            {
                ActionMeleeAI m = c as ActionMeleeAI;
                if (m != null) { float v = s_maxSep(m); if (v > 0f) r = v; }
                r *= ai.AIScale;
            }
            catch (Exception) { }
            return r;
        }

        // ---- 2026-10-20: the shield's record per blow (the coverage at the impact, the facing, green, blocked)
        public static int ShieldBlowsSeen, CoverShort, CoverBlocked, FaceShort;
        static readonly List<string> s_shieldBlows = new List<string>();

        /// One line per judged blow while a shield is held: the coverage of the circle at the impact (the impact's own Judge),
        /// the shield's angle to the attacker, the circle's green on the frame before, the outcome. Logged (the first 400 of a
        /// session) and kept (the last 16, `shieldcover?`).
        static void ShieldRecord(Strike s, string result, bool byShield)
        {
            try
            {
                if (!Fresh(s_pose[2])) return;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                ShieldBlowsSeen++;
                bool judged = s.Cover >= 0f;
                if (judged && s.Cover < ShieldCover) CoverShort++;
                if (judged && s.Cover >= ShieldCover && s.FaceDeg > ShieldFacingDeg) FaceShort++;
                if (byShield) CoverBlocked++;
                string line = FightClock.Now.ToString("0.00", inv) + " " + (s.Ctrl != null ? WeaponGrips.ModelKey(s.Ctrl.name) : "-") + " #" + s.Id + " " + result +
                              ": cover " + (judged ? s.Cover.ToString("0.00", inv) : "-") + " (need " + ShieldCover.ToString("0.00", inv) + ") face " +
                              (judged ? s.FaceDeg.ToString("0", inv) : "-") + " deg (max " + ShieldFacingDeg.ToString("0", inv) + ") depth " + (judged ? s.CoverDepth.ToString("0.00", inv) : "-") +
                              " green " + (s.GreenSeen ? "yes" : "no") + " blocked " + (byShield ? "yes (shield)" : result == "BLOCKED" || result == "COUNTERED" ? "yes (weapon)" : "no") +
                              (s.OffView ? " off-view" : !judged ? " (not judged on the circle)" : "");
                if (s_shieldBlows.Count >= 16) s_shieldBlows.RemoveAt(0);
                s_shieldBlows.Add(line);
                if (ShieldBlowsSeen <= 400) Plugin.L.LogInfo("Guard: shield blow " + line);
            }
            catch (Exception) { }
        }

        void Finish(Strike s, string result, Color c)
        {
            if (s.Judging)
            {
                s.Judging = false;
                bool byShield = (result == "BLOCKED" || result == "COUNTERED") && s.BlockedByShield;
                ShieldRecord(s, result, byShield);
                if (s.GreenSeen && !byShield)
                {
                    GreenNotBlocked++;
                    Note("GREEN BUT " + result + " " + (s.Ctrl != null ? WeaponGrips.ModelKey(s.Ctrl.name) : "-") + ": " + WhyNot(s, VRRig.I.HeadWorldPos));
                }
                else if (!s.GreenSeen && byShield) BlockedNotGreen++;
                else if (s.GreenSeen) GreenBlocked++;
                // P4-15's safety: a blow whose bar and circle were taken away as out of reach must not land
                if (!s.Threat && result == "HIT")
                {
                    HiddenThenHit++;
                    Note("HIDDEN BUT HIT " + (s.Ctrl != null ? WeaponGrips.ModelKey(s.Ctrl.name) : "-") + " (taken away as out of its reach)");
                }
            }
            s.Result = result;
            s.ResultAt = FightClock.Now;
            s.ResultColour = c;
            if (SwingTrace.On && s.Ctrl != null)
            {
                Vector3 head = VRRig.I.HeadWorldPos, center, axis, f;
                if (Toward(s, head, out f))
                {
                    Vector3 c0, a0;
                    Bar(s, head, f, out center, out axis);
                    LegacyBar(s, head, f, out c0, out a0);
                    SwingTrace.Impact(s.Id, result, s.OffView, head, f, center, axis, c0, a0, s.Track);
                }
            }
        }

        /// Horizontal unit vector from the eyes toward the attacker's aim point.
        static bool Toward(Strike s, Vector3 head, out Vector3 f)
        {
            Vector3 att = s.T != null ? s.T.AimTransform.position : s.Ctrl.transform.position;
            f = att - head;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-4f) return false;
            f.Normalize();
            return true;
        }

        void Avoided(AIController ai, Vector3 head)
        {
            try
            {
                PlayerController pc = PlayerController.Instance;
                if (ai.PoliteCombat) AIManager.Instance.TargetEvaded(pc.Targetable, ai);
                AIManager.Instance.DelayNextAttack(pc.Targetable, CombatSettings.SwapGrace);
            }
            catch (Exception) { }
            CombatSounds.Layer(CombatSounds.Chime, head + Vector3.up * 0.2f, 0.45f);
            CombatFeel.Tick(true, 6f);
            CombatFeel.Tick(false, 6f);
            Engaged();
        }

        /// "Defence counts as engagement": no rapid follow-ups for 2.5 s after a block, counter or dodge (§3.1).
        /// After the player defends, no rapid follow-ups for 2.5 s: the lean core's calm, on Easy only (from Normal up UYF's
        /// pacing: none).
        void Engaged() { if (CombatSettings.CalmAfterDefending) m_engagedUntil = FightClock.Now + 2.5f; }

        /// How long ago this enemy's live strike was telegraphed (s; -1 = none): UYF's dash fumbles an attack in its first
        /// 0.1-0.25 s.
        public static float StrikeAge(Controller c)
        {
            Strike s;
            if (I == null || c == null || !I.m_strikes.TryGetValue(c.GetInstanceID(), out s) || s.Result != null) return -1f;
            return FightClock.Now - s.Start;
        }

        void Blocked(Block b)
        {
            PlayerController pc = PlayerController.Instance;
            Strike s = b.S;
            bool left = b.P == s_pose[0] || (b.P.Shield && !Settings.LeftHanded);
            try
            {
                // the game's parry sound and sparks on the guarding shield or weapon (as ActionParry / ActionCounter do)
                Equipment eq = b.P.Shield ? (Equipment)pc.Inventory.Shield : pc.Inventory.Weapon;
                CombatSourceInfo info = b.C != null ? b.C.CombatSourceInfo : null;
                string suffix = info != null ? info.ParryEffectSuffix : null;
                if (eq != null) eq.EffectSet.Trigger("parry" + (suffix ?? ""), eq.Model);
            }
            catch (Exception) { }
            try { if (s.Ctrl != null && s.Ctrl.EffectSet != null) s.Ctrl.EffectSet.Trigger("parried", s.Ctrl.Model); } catch (Exception) { }
            Vector3 at = ClashPoint(b);
            CombatSounds.Layer(CombatSounds.Clank, at, 0.9f, 0.9f);
            CombatSounds.Layer(CombatSounds.Clash, at, 0.75f);
            CombatFeel.Burst(at, new Color(1f, 0.85f, 0.5f), 0.22f, 0.2f);
            CombatFeel.Burst(at, new Color(0.45f, 1f, 0.6f), 0.32f, 0.3f);
            CombatFeel.Impact(left);
            EnemyPush.Start(s.Ai, s.Ctrl.transform.position - VRRig.I.HeadWorldPos, 0.3f, 0.2f);
            BodyLean.Recoil(s.Ctrl);   // UYF's blocked attacker recoils (item 18)
            AIManager.Instance?.DelayNextAttack(pc.Targetable, CombatSettings.SwapGrace);
        }

        /// Where the block meets the blow: the guard's point nearest the bar's middle.
        Vector3 ClashPoint(Block b)
        {
            if (b.P.Shield) return b.P.Center;
            try
            {
                Vector3 head = VRRig.I.HeadWorldPos, f, c, ax;
                if (Toward(b.S, head, out f)) { Bar(b.S, head, f, out c, out ax); float t; return Closest(b.P.Grip, b.P.Tip, c, out t); }
            }
            catch (Exception) { }
            return (b.P.Grip + b.P.Tip) * 0.5f;
        }

        /// The game's own counter outcome (ActionCounter.BeginMelee + OnMeleeHit), minus the hero's animation, snap and turn.
        void Counter(PlayerController pc, Targetable attacker, IActionParry act, Pose p)
        {
            PlayerCombat combat = pc.Combat;
            Targetable hero = pc.Targetable;
            Vector3 dir = attacker.AimTransform.position - hero.AimTransform.position;
            dir.y = 0f;
            dir.Normalize();
            float ang = MathUtils.QuickAngle(pc.transform.forward, dir) * (Vector3.Dot(pc.transform.right, dir) < 0f ? -1f : 1f);
            PlayerCombatCounter c = act.IsPushBack ? combat.CounterPushedBack : combat.GetNextCounter(ang, act.CanBePaused);
            if (c == null) return;
            c.Use();
            act.Counter(c.PauseTarget);   // the attacker freezes (its own 0.1 s ramp to a stop, if the counter pauses)
            if (!c.PauseTarget) CombatFeel.HitStop(attacker.Controller, 0.15f);   // else a 150 ms hit-stop
            AIManager.Instance.OnTargetableCounter(hero, 1f, act.IsPushBack);
            ICombatEffectContainer cont = act.Container;
            try
            {
                CombatSourceInfo info = cont != null ? cont.CombatSourceInfo : null;
                Equipment eq = c.CombatSource == CombatSource.Secondary ? (Equipment)pc.Inventory.Shield : pc.Inventory.Weapon;
                if (p.Shield && pc.Inventory.Shield != null) eq = pc.Inventory.Shield;
                if (info != null && !string.IsNullOrEmpty(info.ParryEffectSuffix) && eq != null) eq.EffectSet.Trigger("parry" + info.ParryEffectSuffix, eq.Model);
            }
            catch (Exception) { }
            Vector3 at = p.Shield ? p.Center : (p.Grip + p.Tip) * 0.5f;
            bool left = p == s_pose[0] || (p.Shield && !Settings.LeftHanded);
            CombatSounds.Layer(CombatSounds.Clank, at, 1f);
            CombatSounds.Layer(CombatSounds.Ring, at, 0.9f);
            CombatFeel.Burst(attacker.AimTransform.position, new Color(1f, 0.8f, 0.2f), 0.5f, 0.35f);
            CombatFeel.CounterCue(left);
            m_counterJobs.Add(new CounterJob { At = FightClock.Now + 0.15f, C = c, Attacker = attacker, Act = act, Container = cont });
            if (attacker.Controller != null) s_stunned[attacker.Controller.GetInstanceID()] = FightClock.Now + StunAfterCounter;
        }

        /// A countered attacker is out of the fight for a moment (Arkham's counter): no new strike, no evade hop, for this
        /// long - the riposte has its moment.
        public const float StunAfterCounter = 1.2f;
        static readonly Dictionary<int, float> s_stunned = new Dictionary<int, float>();

        sealed class CounterJob { public float At; public PlayerCombatCounter C; public Targetable Attacker; public IActionParry Act; public ICombatEffectContainer Container; }
        readonly List<CounterJob> m_counterJobs = new List<CounterJob>();

        /// 150 ms after the counter (the attacker frozen meanwhile): its counter effects, the slow motion, the riposte window.
        void RunCounterJobs()
        {
            float now = FightClock.Now;
            for (int i = m_counterJobs.Count - 1; i >= 0; i--)
            {
                CounterJob j = m_counterJobs[i];
                if (now < j.At) continue;
                m_counterJobs.RemoveAt(i);
                PlayerController pc = PlayerController.Instance;
                if (pc == null || j.Attacker == null) continue;
                try
                {
                    Physical.CapPush(j.Attacker.Controller, 0.7f);
                    CombatEffect.Apply(j.C, new CombatTarget(j.Attacker), pc.transform);
                    // the countered strike is over (in the flat game the hero's counter blow ends it): without this a
                    // strike the counter didn't pause plays on, its recovery hop carrying the attacker out of reach
                    var running = j.Act as Defiant.Action;
                    if (running != null && running.IsRunning) { running.Complete(); Ended++; }
                    pc.Countered(j.Container, pc.Targetable);
                    // the counter's own slow motion (game data); a counter without one gets a short one of ours
                    TimeDilation td = j.C.GetComponent<TimeDilation>();
                    if (td != null) { CombatFeel.Earned("counter", 2f); td.Process(); }
                    else CombatFeel.SlowMo(0.45f, 0.35f, "counter");
                    if (j.C.RiposteAttack != null)
                    {
                        pc.Riposte = true;
                        pc.RiposteTarget = j.Attacker;
                        pc.RiposteAttack = j.C.RiposteAttack;
                        m_riposteOn = j.Attacker;
                        m_riposteAct = j.Act;
                        m_riposteUntil = now + 2f;
                        if (j.Attacker.Controller != null) CombatUtils.StartRiposteIndicatorEffect(j.Attacker.Controller.EffectSet, j.Attacker.Controller.Model);
                    }
                    if (CombatSettings.CounterTurn && Mathf.Abs(ThreatWarning.BearingOf(j.Attacker.transform.position)) > ThreatWarning.BehindAngle && CombatVR.I != null)
                    {
                        if (CombatVR.I.FaceTowards(j.Attacker.transform.position)) Turned++;
                    }
                }
                catch (Exception e) { Plugin.L.LogWarning("Guard: counter outcome: " + e.Message); }
            }
            // the riposte window closes after 2 s (or after the riposte)
            if (m_riposteOn != null && now > m_riposteUntil) EndRiposte();
        }

        /// The countered attacker while its riposte window is open (for `enemy? riposte`, `face riposte`).
        public static Targetable RiposteTarget => I != null ? I.m_riposteOn : null;

        public static void RiposteUsed(Targetable t)
        {
            if (I != null && I.m_riposteOn == t) I.EndRiposte();
        }

        void EndRiposte()
        {
            PlayerController pc = PlayerController.Instance;
            try
            {
                if (pc != null && pc.RiposteTarget == m_riposteOn) { pc.Riposte = false; pc.RiposteTarget = null; pc.RiposteAttack = null; }
                if (m_riposteOn != null && m_riposteOn.Controller != null) CombatUtils.StopRiposteIndicatorEffect(m_riposteOn.Controller.EffectSet, m_riposteOn.Controller.Model);
                if (m_riposteAct != null && ((Defiant.Action)m_riposteAct).IsRunning) m_riposteAct.CounterCancel();
            }
            catch (Exception) { }
            m_riposteOn = null;
            m_riposteAct = null;
        }

        // ---------------------------------------------------------------------------------------- pace (§8.5.5)

        /// One live strike for every AI, and SwapGrace before a different attacker: a strike action of another enemy waits.
        public bool HoldStrike(AIController ai, bool ranged = false)
        {
            float now = FightClock.Now;
            int id = ai.GetInstanceID();
            if (Abilities.HoldsStrikes) { AimHeld++; return true; }   // aiming an ability (§8.5.9): no new strike, for up to 2 s
            if (ranged)
            {
                // UYF's way (2026-10-13; in p2final's crowded fight the archers never got a shot through our holds): a shot or a
                // throw waits only for the global ranged cooldown at the difficulty (EnemyTable.RangedCooldownFor, counted from
                // the last frame any enemy's ranged strike ran) - not for the one-live-strike rule, the ticket, the swap grace or
                // a melee cooldown. The game's own rules (its attack slot, its per-shooter cooldown) and ThreatWarning's gates
                // (an off-view shooter announced first, one off-view strike at a time, the longer wind-up) still apply.
                if (now - s_lastRangedAny < RangedCooldown(ai)) { RangedHolds++; return true; }
                return false;
            }
            if (Tickets.JustGained(ai)) return true;                  // Stage 2 item 5: UYF's AttackCooldown_TicketGained
            foreach (var kv in m_strikes)
                if (kv.Key != id && kv.Value.Result == null && now - kv.Value.Start < 6f) return true;
            // UYF's difficulty (EnemyTable, per archetype and step): the swap grace before a different attacker, and the pause
            // between two strikes of the same enemy (AttackCooldown_Med)
            if (m_lastImpactBy != id && now - m_lastImpact < EnemyTable.GraceFor(ai, Physical.CountAlive())) { Swaps++; return true; }
            float last;
            if (s_lastBlowOf.TryGetValue(id, out last) && now - last < PauseOf(ai)) { Cooldowns++; return true; }
            return false;
        }

        /// The time of each enemy's last blow (its impact), for its own cooldown.
        static readonly Dictionary<int, float> s_lastBlowOf = new Dictionary<int, float>();

        /// UYF's pause after each attack (audit E14; `[Combat] VariedPauses`): its behaviour tree's "Complete Attack
        /// Sequence" starts the attack cooldown with one of three timers picked at random - AttackCooldown_Short, _Med or
        /// _Long (a BranchRandomNode) - or with _Med when the attack was stopped ("Bypass Attack Cooldown": the trigger set
        /// when Stop Attacks is called). Drawn at each blow (the last blow of a run is the one that counts) and kept per
        /// enemy; VariedPauses off: Med, as before merge-ready #4.
        static readonly Dictionary<int, float> s_pauseOf = new Dictionary<int, float>();
        public static int PauseShort, PauseMed, PauseLong, PauseStopped;
        public static float PauseMin = 99f, PauseMax;

        static void DrawPause(AIController ai, bool stopped)
        {
            try
            {
                if (!CombatSettings.VariedPauses) { s_pauseOf.Remove(ai.GetInstanceID()); return; }
                int which = stopped ? 1 : UnityEngine.Random.Range(0, 3);
                float p = EnemyTable.PauseFor(ai, which);
                if (s_pauseOf.Count > 64) s_pauseOf.Clear();
                s_pauseOf[ai.GetInstanceID()] = p;
                if (stopped) PauseStopped++;
                else if (which == 0) PauseShort++;
                else if (which == 1) PauseMed++;
                else PauseLong++;
                PauseMin = Mathf.Min(PauseMin, p);
                PauseMax = Mathf.Max(PauseMax, p);
            }
            catch (Exception) { }
        }

        /// This enemy's pause after its last attack: the one drawn for it (VariedPauses), else Med.
        static float PauseOf(AIController ai)
        {
            float p;
            if (CombatSettings.VariedPauses && ai != null && s_pauseOf.TryGetValue(ai.GetInstanceID(), out p)) return p;
            return EnemyTable.CooldownFor(ai);
        }

        public static void ResetPauses() { PauseShort = PauseMed = PauseLong = PauseStopped = 0; PauseMin = 99f; PauseMax = 0f; }

        /// `vrpauses`: the setting, the draws (short / med / long, med after a stopped attack), the smallest and largest pause
        /// drawn, the enemies held by their pause.
        public static string PausesDescribe() => "varied " + CombatSettings.VariedPauses + " short " + PauseShort + " med " + PauseMed + " long " + PauseLong +
                                                  " stopped " + PauseStopped + " min " + (PauseMin < 98f ? PauseMin.ToString("0.00") : "-") + " max " + PauseMax.ToString("0.00") +
                                                  " held " + Cooldowns;

        /// Its own attack is still on cooldown (UYF's AttackCooldown_Med at the difficulty, from its last blow): Tickets.
        public static bool OnCooldown(AIController ai)
        {
            float last;
            return ai != null && s_lastBlowOf.TryGetValue(ai.GetInstanceID(), out last) && FightClock.Now - last < PauseOf(ai);
        }
        public static int Cooldowns, DodgeBypassed;

        /// Each judged blow's lead and attack speed (the difficulty's test: `leads? [reset]`).
        public sealed class LeadRow { public string Model, Arch; public float Lead, Want, Speed; public CombatSettings.DifficultyLevel Step; }
        public static readonly List<LeadRow> Leads = new List<LeadRow>();

        static readonly Dictionary<int, AIController> s_aiOf = new Dictionary<int, AIController>();

        static bool EvaluatePrefix(Defiant.Action __instance)
        {
            if (I == null || __instance.IsRunning || !Physical.Live) return true;
            try
            {
                bool strike = ThreatWarning.IsStrike(__instance);
                if (!strike && !(__instance is IActionEvade)) return true;
                Actor actor = __instance.Actor;
                if (actor == null) return true;
                int aid = actor.GetInstanceID();
                AIController ai;
                if (!s_aiOf.TryGetValue(aid, out ai)) s_aiOf[aid] = ai = actor.GetComponent<AIController>();
                float until;
                if (ai != null && s_stunned.Count > 0 && s_stunned.TryGetValue(ai.GetInstanceID(), out until))
                {
                    if (FightClock.Now < until) { Held++; return false; }
                    s_stunned.Remove(ai.GetInstanceID());
                }
                if (!strike) return true;
                PlayerController pc = PlayerController.Instance;
                if (ai == null || pc == null || ai.Target == null || ai.Target.gameObject != pc.gameObject) return true;
                if (I.HoldStrike(ai, IsRanged(__instance))) { Held++; return false; }
            }
            catch (Exception) { }
            return true;
        }

        /// A ranged strike running: the global ranged cooldown counts from its last frame (UYF: after the attack). Its first
        /// frame logs the shot (`rangedshots?`).
        static void EvaluatePostfix(Defiant.Action __instance)
        {
            try
            {
                if (I == null || !Physical.Live || !__instance.IsRunning || !IsRanged(__instance)) return;
                Actor actor = __instance.Actor;
                AIController ai;
                if (actor == null) return;
                if (!s_aiOf.TryGetValue(actor.GetInstanceID(), out ai)) s_aiOf[actor.GetInstanceID()] = ai = actor.GetComponent<AIController>();
                // shots at the player only (the holds are for those too: a companion's shots, or an enemy's at a companion, set
                // no cooldown)
                PlayerController pc = PlayerController.Instance;
                if (ai == null || pc == null || ai.Target == null || ai.Target.gameObject != pc.gameObject) return;
                float now = FightClock.Now, lr;
                int id = ai.GetInstanceID();
                if (!s_lastRangedOf.TryGetValue(id, out lr) || now - lr > OwnShotGap) I.LogShot(ai, now);
                s_lastRangedOf[id] = now;
                s_lastRangedAny = now;
                s_lastShooter = ai;
            }
            catch (Exception) { }
        }

        /// EnemyTable.RangedCooldownFor, kept per enemy at the difficulty (asked every frame an AI thinks).
        static float RangedCooldown(AIController ai)
        {
            if (s_rangedCdStep != CombatSettings.Step) { s_rangedCdOf.Clear(); s_rangedCdStep = CombatSettings.Step; }
            float cd;
            int id = ai.GetInstanceID();
            if (!s_rangedCdOf.TryGetValue(id, out cd)) s_rangedCdOf[id] = cd = EnemyTable.RangedCooldownFor(ai);
            return cd;
        }

        static readonly Dictionary<int, float> s_rangedCdOf = new Dictionary<int, float>();
        static int s_rangedCdStep = -1;

        /// A shooter's ranged strike counts as still running while its frames come at most this far apart (an AI's actions
        /// are evaluated every frame it thinks).
        const float OwnShotGap = 0.3f;
        static readonly Dictionary<int, float> s_lastRangedOf = new Dictionary<int, float>();
        static float s_lastRangedAny = -100f;   // the last frame any enemy's ranged strike ran (UYF's global ranged cooldown)
        static AIController s_lastShooter;      // whose shot that was (the cooldown is its archetype's, at the difficulty now)
        static readonly Dictionary<Type, bool> s_rangedType = new Dictionary<Type, bool>();
        public static int RangedHolds, BeatsToMelee;

        /// Each enemy shot or throw at the player (the archer test: `rangedshots? [reset]`): when it started, by whom, the
        /// time since the last frame of the shot before (the global cooldown's gap) and the cooldown due, where the shooter
        /// stood (bearing from the gaze), how long before it had been announced and last been in view (ThreatWarning), and
        /// whether a melee strike was live at the time (what the one-live-strike rule used to hold it for).
        public sealed class ShotRow { public float At, Gap, Cooldown, Bearing, AnnouncedAgo, InViewAgo; public string Who; public bool MeleeLive; }
        public static readonly List<ShotRow> Shots = new List<ShotRow>();
        public static float ShotsSince;

        void LogShot(AIController ai, float now)
        {
            if (Shots.Count >= 200) return;
            float ann, seen;
            ThreatWarning.ViewOf(ai, out ann, out seen);
            bool melee = false;
            foreach (var kv in m_strikes)
                if (kv.Key != ai.GetInstanceID() && kv.Value.Result == null && now - kv.Value.Start < 6f) { melee = true; break; }
            Shots.Add(new ShotRow
            {
                At = now - ShotsSince, Gap = now - s_lastRangedAny, Cooldown = RangedCooldown(ai), Bearing = ThreatWarning.BearingOf(ai.transform.position),
                AnnouncedAgo = ann, InViewAgo = seen, Who = WeaponGrips.ModelKey(ai.name) + "#" + ai.GetInstanceID(), MeleeLive = melee
            });
        }

        /// The game's ranged beat (AITarget.UpdateRangedAttacker) while the global ranged cooldown runs: no shooter is picked,
        /// so the beat goes to a melee attacker (the game's own fallback when no shooter is free). A shooter picked now would
        /// keep the attack slot - and every melee attack with it - until the cooldown is over (10 s on Easy).
        static bool PickShooterPrefix(AITarget __instance)
        {
            if (I == null || !Physical.Live) return true;
            try
            {
                PlayerController pc = PlayerController.Instance;
                if (pc == null || __instance.Targetable == null || __instance.Targetable.gameObject != pc.gameObject) return true;
                // (ReferenceEquals: the cooldown also runs on after its shooter has died)
                if (!ReferenceEquals(s_lastShooter, null) && FightClock.Now - s_lastRangedAny < RangedCooldown(s_lastShooter)) { BeatsToMelee++; return false; }
            }
            catch (Exception) { }
            return true;
        }

        /// A shot or a throw (the game's ranged attack actions, or any strike whose container is Ranged).
        static bool IsRanged(Defiant.Action a)
        {
            Type t = a.GetType();
            bool v;
            if (s_rangedType.TryGetValue(t, out v)) return v;
            v = a is ActionRangedAI || a is ActionRangedGunslinger || a is ActionRangedSkeletonQueen || t.Name.IndexOf("Throw", StringComparison.Ordinal) >= 0 ||
                t.Name.IndexOf("Ranged", StringComparison.Ordinal) >= 0;
            if (!v)
            {
                var c = a as ICombatEffectContainer;
                try { v = c != null && (c.Category & CombatCategory.Ranged) != 0; } catch (Exception) { }
            }
            s_rangedType[t] = v;
            return v;
        }

        static void CanAttackPostfix(AITarget a_target, AIController a_ai, bool a_ignoreActiveProjectiles, ref bool __result)
        {
            if (I == null || a_ai == null || !Physical.Live) return;
            try
            {
                PlayerController pc = PlayerController.Instance;
                if (pc == null || a_target == null || a_target.Targetable == null || a_target.Targetable.gameObject != pc.gameObject) return;
                bool ranged = a_ai.Mode == AIMode.Ranged;
                if (!__result)
                {
                    // UYF's waiting enemies shoot while another one fights the player in melee - only the global ranged cooldown
                    // holds them (ref §3.11). HoF2 lets a shooter shoot only from the target's one attack slot, never while a melee
                    // attacker holds it (AIManager.CanAttack): in a mixed fight a shot came about once in 150 s (MRd).
                    if (!ranged || !WaitingShooterMay(a_target, a_ai, a_ignoreActiveProjectiles)) return;
                    // ThreatWarning's own gate ran before (it saw "no"): an off-view shooter is announced first here (MRm: three
                    // shots from 62-109 deg came unannounced)
                    if (ThreatWarning.HoldsOffView(a_ai)) return;
                    __result = true;
                    WaitingShooters++;
                }
                // a shooter asked again in the middle of its own shot (the cooldown its shot keeps fresh must not end it)
                float lr;
                if (ranged && s_lastRangedOf.TryGetValue(a_ai.GetInstanceID(), out lr) && FightClock.Now - lr < OwnShotGap) return;
                if (I.HoldStrike(a_ai, ranged)) __result = false;
            }
            catch (Exception) { }
        }

        /// A shooter asked while it holds no attack slot (or a melee attacker holds the player's): the game's other rules
        /// still hold - a repellent target, exclusive attackers, a projectile of its side still in flight - and the global
        /// ranged cooldown (HoldStrike, after this) decides.
        public static int WaitingShooters;
        static bool WaitingShooterMay(AITarget at, AIController ai, bool ignoreProjectiles)
        {
            if (at.IsTargetRepellent(0)) return false;
            if (at.Targetable.ExclusiveAiAttackers.size > 0 && !at.Targetable.ExclusiveAiAttackers.Contains(ai)) return false;
            if (ai.IsStunned(0f) || Poise.Staggered(ai) || !ai.CanAttackTarget) return false;
            if (!ReferenceEquals(at.RangedAttacker, null) && !ReferenceEquals(at.RangedAttacker, ai) && RunningStrike(at.RangedAttacker) != null) return false;
            if (!ignoreProjectiles)
            {
                Targetable me = ai.GetComponent<Targetable>();
                List<CombatProxyProjectile> ps = CombatManager.Instance != null ? CombatManager.Instance.Projectiles : null;
                if (ps != null && me != null)
                    for (int i = 0; i < ps.Count; i++)
                    {
                        CombatProxyProjectile p = ps[i];
                        if (p != null && !p.IsExpiring && !p.IsReflected && p.BlocksAI && p.Effect != null && me.IsAlly(p.Effect.Source)) return false;
                    }
            }
            return FightClock.Now - s_lastRangedAny >= RangedCooldown(ai);
        }

        // ---------------------------------------------------------------------------------------- every frame

        static Material s_baseMat;
        static readonly Color Gold = new Color(1f, 0.78f, 0.12f), White = new Color(0.95f, 0.95f, 0.95f), Red = new Color(0.996f, 0.05f, 0.05f);
        readonly List<int> m_drop = new List<int>();
        float m_nextClick;

        void LateUpdate()
        {
            FightClock.Tick();
            if (Physical.Paused)
            {
                // the audit, item 3: paused, every live strike is kept (FightClock stands still) and its marks hidden
                foreach (Strike s in m_strikes.Values) HideMarks(s);
                foreach (Strike s in m_fading) HideMarks(s);
                foreach (var kv in m_ahead) foreach (Strike a in kv.Value) HideMarks(a);
                SuperMeter.HandGlowTick();
                GnomeFight.Tick();   // (it hides its own marks while paused)
                return;
            }
            RunCounterJobs();
            Tickets.Tick();
            GnomeFight.Tick();   // F8
            SuperMeter.HandGlowTick();   // playtest 3 note D
            FightStart();
            Physical.TrackFace();   // test seam `face track`
            if (!Physical.Live)
            {
                if (m_strikes.Count > 0) { foreach (Strike s in m_strikes.Values) Close(s); m_strikes.Clear(); }
                if (m_fading.Count > 0) { foreach (Strike s in m_fading) Close(s); m_fading.Clear(); }
                if (m_ahead.Count > 0) { foreach (var kv in m_ahead) foreach (Strike a in kv.Value) Close(a); m_ahead.Clear(); }
                return;
            }
            if (FightClock.Now < m_engagedUntil && AIManager.Instance != null) AIManager.Instance.RapidAttacks = false;
            Vector3 head = VRRig.I.HeadWorldPos;
            float now = FightClock.Now;
            m_drop.Clear();
            foreach (var kv in m_strikes)
            {
                Strike s = kv.Value;
                bool dead = s.Ctrl == null || s.Model == null;
                if (dead || (s.Result != null && now - s.ResultAt > ShowResult) || now - s.Start > 8f) { Close(s); m_drop.Add(kv.Key); continue; }
                // a follow-up whose combo ended (or never brought the blow): gone quietly
                if (s.FollowUp && s.Result == null && (s.Act == null || !s.Act.IsRunning || now - s.Start > s.Expected + 1.5f))
                {
                    s.Result = "window closed";
                    s.ResultAt = now;
                    s.ResultColour = Color.clear;
                }
                HoldFacing(s, now);
                Draw(s, head, now);
            }
            foreach (int id in m_drop) m_strikes.Remove(id);
            if (m_ahead.Count > 0) DrawAhead(head, now);
            for (int i = m_fading.Count - 1; i >= 0; i--)
            {
                Strike s = m_fading[i];
                if (s.Ctrl == null || s.Model == null || now - s.ResultAt > ShowResult) { Close(s); m_fading.RemoveAt(i); continue; }
                Draw(s, head, now);
            }
        }

        /// UYF's committed attacker (docs/uyf-parity-audit.md E5; UYF's timelines stop the attacker's turning 0.13-0.63 s around
        /// each strike: StopAutoRotationPlayable): from CommitBefore before the blow's expected impact until CommitAfter after
        /// the blow was judged, the attacker's facing is held where it was. The game's own turning toward the hero (which made
        /// the bar slide until the impact - the stage1 `offset` flake) stops, so the blade passes where the bar showed and a
        /// late step aside leaves the blow's reach (its sector, judged at the impact in the attacker's frame).
        public const float CommitBefore = 0.3f, CommitAfter = 0.1f;
        public static bool CommitsOn = true;   // test seam `vrcommit on|off`
        public static int FacingHolds;
        public static float FacingHeldDeg;

        static void HoldFacing(Strike s, float now)
        {
            if (!CommitsOn || s.Ctrl == null || s.Expected <= 0f) { s.Holding = false; return; }
            bool inWindow = s.Result == null ? now >= s.Start + s.Expected - CommitBefore : now - s.ResultAt <= CommitAfter;
            if (!inWindow) { s.Holding = false; return; }
            Transform tr = s.Ctrl.transform;
            if (!s.Holding)
            {
                if (s.Result != null) return;   // judged before the window opened: nothing to hold
                s.Holding = true;
                s.HoldRot = tr.rotation;
                FacingHolds++;
                return;
            }
            float drift = Quaternion.Angle(tr.rotation, s.HoldRot);
            if (drift > 0.05f)
            {
                FacingHeldDeg = Mathf.Max(FacingHeldDeg, drift);
                tr.rotation = s.HoldRot;
            }
        }

        /// What a look needs beyond the marker's frame: the side the blade comes from (local y), the attacker, its blade's
        /// predicted pass (the swing atlas).
        IndicatorLooks.Context LookContext(Strike s, Vector3 head, Vector3 f, Vector3 center, Vector3 axis)
        {
            var c = new IndicatorLooks.Context { Attacker = s.Ctrl };
            try
            {
                Vector3 view = (center - head).normalized;
                Vector3 up = Vector3.Cross(view, axis).normalized;
                if (s.Shape != null && s.Ctrl != null)
                {
                    Vector3 a, b, v;
                    SwingAtlas.World(s.Shape, s.Ctrl, s.TelePos, out a, out b, out v);
                    c.From = Vector3.Dot(v, up) > 0f ? -1f : 1f;
                    c.HasPass = true;
                    c.PassA = a;
                    c.PassB = b;
                }
            }
            catch (Exception) { }
            return c;
        }

        /// How long a judged strike keeps showing its result (s).
        public const float ShowResult = 0.6f;

        void Draw(Strike s, Vector3 head, float now)
        {
            if (s.Phantom || s.Shove || s.Guess) return;   // audit item 12: no bar, no arrow, no result flash (a shove: UYF's are untelegraphed; a guess: no action yet)
            if (s.Ahead || s.FromAhead)
            {
                // audit E3: a blow shown ahead appears its lead before its impact; the dodge is measured from the head's place then
                if (now < s.Start) return;
                if (!s.Began) { s.Began = true; s.HeadT0 = VRInput.HeadPos; s.DodgeDirFixed = false; ChooseDodge(s); }
            }
            Vector3 f;
            if (!Toward(s, head, out f)) return;
            Vector3 att = AttPos(s);
            s.OffView = Mathf.Abs(ThreatWarning.BearingOf(att)) > ThreatWarning.ViewHalf;
            // the blade's track (four new arrays a frame) only where it is used: to learn a blow the atlas lacks, or for the trace
            if (s.Track != null && s.Result == null && (s.Shape == null || SwingTrace.On)) s.Track.Tick(head, f);
            if (s.Result == null && s.Shape != null && !s.OffView && !s.Ahead) Commit(s, head, f, now);
            Vector3 center, axis;
            Bar(s, head, f, out center, out axis);
            if (SwingTrace.On) { Vector3 c0, a0; LegacyBar(s, head, f, out c0, out a0); SwingTrace.Tick(s.Id, head, f, center, axis, c0, a0); }
            bool met;
            if (s.Result == null)
            {
                // the live preview: UYF's lock-in (a valid guard now) or the dodge's "succeeding"; in or out of the blow's reach
                s.InRange = InReach(s, head);
                UpdateThreat(s, head, now);
                if (s.Dodge)
                {
                    s.Progress = DodgeProgress(s, f);
                    bool ok = s.Progress >= 1f;
                    if (ok && !s.WasSucceeding)
                    {
                        DodgeReadies++;
                        CombatSounds.Layer(CombatSounds.DodgeReady, head + f * 0.4f, 0.35f);
                        CombatFeel.Tick(s.DodgeX < 0f || s.DodgeY < -0.5f, 3f);
                    }
                    s.WasSucceeding = ok;
                }
                else
                {
                    Pose vp = s.Blockable || RedBlocks ? Judge(s, head, out met) : null;
                    s.Valid = vp != null;
                    s.ValidShield = vp != null && vp.Shield;
                    if (s.Valid && !s.WasValid)
                    {
                        LockIns++;
                        if (now >= m_nextClick) { m_nextClick = now + 0.15f; CombatSounds.Layer(CombatSounds.LockIn, center, 0.5f); }
                    }
                    s.WasValid = s.Valid;
                }
            }
            // off-view strikes: ThreatWarning's edge marker shows them; the bar appears once the attacker is in view
            bool hide = s.OffView || s.Result == "window closed" || ShieldShots.DemoShown;   // (the arrow pictures' still demo)
            if (s.Dodge) { DrawArrow(s, head, f, now, hide); return; }
            // the audit, item 5: with LeanDodge a blow the game can't block is one VR can't block either - the red ground arrow,
            // no bar, no circle (s.Dodge is never set since the dodge arrows went)
            if (!s.Blockable && !RedBlocks)
            {
                if (s.Vis != null && s.Vis.Root.gameObject.activeSelf) s.Vis.Root.gameObject.SetActive(false);
                if (s.Ring != null && s.Ring.Root.gameObject.activeSelf) s.Ring.Root.gameObject.SetActive(false);
                DrawArrow(s, head, f, now, hide);
                return;
            }
            // P4-15 (all fights): a blow that can no longer reach the player shows no bar and no circle - the block icon - nor
            // its result's flash (a can't-block blow's red ground arrow above is left as it was)
            if (hide || !s.Threat) { if (s.Vis != null) s.Vis.Root.gameObject.SetActive(false); if (s.Ring != null) s.Ring.Root.gameObject.SetActive(false); return; }
            if (s.Vis == null)
            {
                s.Vis = IndicatorLooks.NewBlock(CombatSettings.Look, LookContext(s, head, f, center, axis));
                s.ShownAt = now;
                Markers++;
                s.Seen = true;
                CombatSounds.Layer(CombatSounds.Premonition, center, 0.22f);
            }
            s.Vis.Root.gameObject.SetActive(true);
            s.Vis.Root.position = center;
            // the bar's long axis is local x (= its axis, made square to the view), its face toward the eyes
            Vector3 view = (center - head).normalized;
            Vector3 upInPlane = Vector3.Cross(view, axis).normalized;
            if (upInPlane.sqrMagnitude > 1e-6f) s.Vis.Root.rotation = Quaternion.LookRotation(view, upInPlane);
            float fill = s.Expected > 0.05f ? Mathf.Clamp01((now - s.Start) / s.Expected) : 1f;
            // playtest 3 note C: red means "can't be blocked" and nothing else - a blow the game calls unblockable that VR lets you
            // block (RedBlocks) gets the normal bar; a blow that lands fades grey (it flashed red)
            Color baseC = s.Colour == Look.Gold ? Premonition.Gold : s.Colour == Look.Red && !RedBlocks ? Premonition.Fail : Premonition.Cyan;
            if (CombatSettings.IndicatorByType && s.Colour != Look.Gold) baseC = !s.Blockable && !RedBlocks ? IndicatorLooks.Red : s.Heavy ? IndicatorLooks.Orange : Premonition.Cyan;
            string res = s.Result == null ? null : s.Result == "HIT" || s.Result == "OUT OF REACH" ? "fade" : s.Result == "BLOCKED" && s.BlockedByShield ? "fade" : "ok";
            // note B: the shield on the circle - the bar fades (the circle turns green); blocked by the shield, the circle flashes
            bool shieldNow = s.Result == null ? s.Valid && s.ValidShield : s.BlockedByShield;
            Premonition.Builder.Fade = shieldNow ? LineOnShield : 1f;
            try { s.Vis.Draw(now - s.ShownAt, fill, s.Valid && !s.ValidShield, s.InRange, res, s.Result == null ? 0f : now - s.ResultAt, baseC); }
            finally { Premonition.Builder.Fade = 1f; }
            DrawRing(s, head, center, now, fill, baseC);
        }

        /// Note B: the shield's circle at the bar's middle while a shield is held (a ring framing the shield, facing the attacker).
        void DrawRing(Strike s, Vector3 head, Vector3 center, float now, float fill, Color baseC)
        {
            Vector3 sc, sn;
            float sr;
            bool held = Shield(out sc, out sn, out sr);
            if (!held && s.Ring == null) return;
            if (!held && s.Result == null) { s.Ring.Root.gameObject.SetActive(false); return; }
            if (s.Ring == null) { s.Ring = new IndicatorLooks.ShieldRing("HoF2VR_ShieldRing", true); Rings++; }
            s.Ring.Root.gameObject.SetActive(true);
            s.Ring.Root.position = center;
            Vector3 f;
            if (!Toward(s, head, out f)) f = (center - head).normalized;
            s.Ring.Root.rotation = Quaternion.LookRotation(f, Vector3.up);
            string res = s.Result == null ? null : s.Result == "BLOCKED" && s.BlockedByShield || s.Result == "COUNTERED" && s.BlockedByShield ? "ok" : "fade";
            // the ring drawn over the held shield, 0.30 m across; green when Judge's rule holds (the shield's outline covers
            // ShieldCover of it)
            if (held) s.RingRadius = sr;
            s.Ring.Frame(now - s.ShownAt, fill, s.Valid && s.ValidShield, s.Valid && !s.ValidShield, res, s.Result == null ? 0f : now - s.ResultAt, s.RingRadius,
                         IndicatorLooks.ShieldRing.ColourOf(CombatSettings.Look, baseC));
        }

        public static int Rings;

        public const float MaxCommit = 0.8f, CommitShort = 0.3f, CommitNear = 0.9f;
        /// A blow whose blade came no nearer than this to the guard point, measured flat, does not come to the player.
        public const float ReachLimit = 0.6f;
        public static int Commits;
        public static float CommitMost;

        /// Bring the swing to the bar: a blow whose blade (the atlas's prediction from the attacker's root now) would stay
        /// more than CommitShort beyond the bar's plane - a flat-game animation that lands its hit short of where a player
        /// can guard - has its attacker slide in along its line during the wind-up, spread over the time left, until the
        /// blade reaches 0.2 m of the plane: at most MaxCommit in all, never so far that its root would end nearer than
        /// CommitNear to the eyes, along the navmesh.
        void Commit(Strike s, Vector3 head, Vector3 f, float now)
        {
            if (s.Ctrl == null || s.Committed >= MaxCommit) return;
            // a blow whose blade never came near the guard zone even at its usual distance (the templar's first hit) is not a
            // distance problem: sliding its attacker in would not bring the blade
            if (s.Shape.Reach > ReachLimit) return;
            Vector3 a, b, v;
            SwingAtlas.World(s.Shape, s.Ctrl, s.TelePos, out a, out b, out v);
            Vector3 att = AttPos(s);
            Vector3 p0 = head + f * SwingAtlas.Depth(head, att);
            float near = Mathf.Min(Vector3.Dot(a - p0, f), Vector3.Dot(b - p0, f));
            if (near <= CommitShort) return;
            // the root's distance at the impact (with the clip's own move still to come)
            Vector3 root = s.Ctrl.transform.position, rest = Vector3.zero;
            float s0 = SwingAtlas.ScaleOf(s.Ctrl);
            Vector3 total = s.Ctrl.transform.rotation * (s.Shape.Move * s0);
            total.y = 0f;
            if (total.magnitude > 0.15f) rest = total * Mathf.Clamp01(1f - Vector3.Dot(root - s.TelePos, total.normalized) / total.magnitude);
            Vector3 atImpact = root + rest - head;
            atImpact.y = 0f;
            // a seated player lets UYF's captains and its Aether Horror come nearer (their movement capsule shrinks: SeatedRules)
            float want = Mathf.Min(near - 0.2f, Mathf.Min(MaxCommit - s.Committed, atImpact.magnitude - (CommitNear - SeatedRules.Shrink(s.Ctrl))));
            if (want <= 0.005f) return;
            float left = Mathf.Max(0.12f, s.Expected - (now - s.Start));
            float step = Mathf.Min(want, want / left * Time.unscaledDeltaTime);
            UnityEngine.AI.NavMeshAgent ag = s.Ctrl.GetComponent<UnityEngine.AI.NavMeshAgent>();
            Vector3 before = s.Ctrl.transform.position;
            if (ag != null && ag.enabled && ag.isOnNavMesh) ag.Move(-f * step);
            else s.Ctrl.transform.position += -f * step;
            float moved = (s.Ctrl.transform.position - before).magnitude;
            if (s.Committed <= 0f && moved > 0f) Commits++;
            s.Committed += moved;
            CommitMost = Mathf.Max(CommitMost, s.Committed);
            if (moved < step * 0.3f) s.Committed = MaxCommit;   // blocked: stop trying
        }

        /// The dodge arrow: in front of the face toward the attacker, a little toward the way to move, pointing that way.
        void DrawArrow(Strike s, Vector3 head, Vector3 f, float now, bool hide)
        {
            // playtest 3 (note C, the ruling of 17:40): a blow nothing can block shows no line in front of the face - the red ground
            // arrow from the attacker to the player, full at the impact, gone with it (the lean dodge itself is judged as before)
            PlayerController pc = PlayerController.Instance;
            if (hide || s.Result != null || pc == null || s.Ctrl == null || ShieldShots.DemoShown) { if (s.GroundMark != null) s.GroundMark.Root.gameObject.SetActive(false); return; }
            if (s.GroundMark == null)
            {
                s.GroundMark = new IndicatorLooks.GroundArrow("HoF2VR_StrikeGroundArrow");
                s.ShownAt = now;
                Markers++;
                Arrows++;
                s.Seen = true;
                CombatSounds.Layer(CombatSounds.DodgeCue, head + f * 0.5f, 0.45f);
            }
            s.GroundMark.Draw(s.Ctrl.transform.position, pc.transform.position, now - s.ShownAt, s.Expected > 0.05f ? Mathf.Clamp01((now - s.Start) / s.Expected) : 1f, true);
            if (GroundOnly) return;
            if (hide) { if (s.Arr != null) s.Arr.Root.gameObject.SetActive(false); return; }
            if (s.Arr == null)
            {
                s.Arr = IndicatorLooks.NewDodge(CombatSettings.Look, new IndicatorLooks.Context { Attacker = s.Ctrl });
                s.ShownAt = now;
                Markers++;
                Arrows++;
                s.Seen = true;
                CombatSounds.Layer(CombatSounds.DodgeCue, head + f * 0.5f, 0.45f);
            }
            s.Arr.Root.gameObject.SetActive(true);
            Vector3 d = DodgeDirWorld(s, f);
            float depth = SwingAtlas.Depth(head, AttPos(s)) * 0.85f;
            Vector3 at = head + f * depth + d * 0.07f - Vector3.up * (s.DodgeY < -0.5f ? 0.02f : 0.06f);
            s.Arr.Root.position = at;
            Vector3 view = (at - head).normalized;
            Vector3 up = d - view * Vector3.Dot(d, view);
            if (up.sqrMagnitude > 1e-6f) s.Arr.Root.rotation = Quaternion.LookRotation(view, up.normalized);
            // UYF's arrow is drawn over 1.1x the lead
            float fill = s.Expected > 0.05f ? Mathf.Clamp01((now - s.Start) / (1.1f * s.Expected)) : 1f;
            string res = s.Result == null ? null : s.Result == "DODGED" || s.Result == "OUT OF REACH" ? "ok" : "fail";
            s.Arr.Draw(now - s.ShownAt, fill, Mathf.Max(0f, s.Progress), res, s.Result == null ? 0f : now - s.ResultAt);
        }

        /// The ground arrow replaces UYF's dodge arrow in front of the face (false: both, the earlier look).
        public static bool GroundOnly = true;

        /// The head is inside the blow's reach now (Resolve's own reach test, without its 0.15 m grace).
        bool InReach(Strike s, Vector3 head)
        {
            string why;
            return InSector(s, head, s.Act as ICombatEffectContainer, out why);
        }

        /// P4-15 (playtest 4, mark 222216_12: the circle stayed right by the player after a dodge, the attacker far away): "When I am
        /// no longer being threatened by an attack, I shouldn't see the block icon. If the attack is still threatening me but in a
        /// meaningfully different direction, then the block icon should move accordingly." A blow threatens while the player's head
        /// is within its reach at the impact: Resolve's sector test (InSector), with the ground the attacker can still cover before
        /// its blow counted in - the slide-in it may still get (Commit, in view) and the rest of its clip's own move. Its arc only
        /// counts once its facing is held (CommitBefore before the impact): until then it still turns toward the player. Hidden after
        /// NoThreatHold s out of reach, shown again at once. The bar and the circle sit toward the attacker every frame (Bar), so a
        /// blow coming from a new direction moves them.
        public const float NoThreatHold = 0.12f;
        public static int ThreatHides, HiddenThenHit;
        public static bool ThreatRule = true;   // test seam `vrthreat on|off`

        void UpdateThreat(Strike s, Vector3 head, float now)
        {
            bool reach = true;
            if (ThreatRule && !s.Dodge && s.Ctrl != null)
            {
                // a margin on top (a missed marker costs more than one shown too long): 0.2 m, 0.6 m for a blow the atlas
                // doesn't know (its approach unknown)
                float slack = s.Shape == null ? 0.6f : 0.2f;
                if (s.Shape != null)
                {
                    if (CommitsOn && !s.OffView && s.Shape.Reach <= ReachLimit) slack += Mathf.Max(0f, MaxCommit - s.Committed);
                    float s0 = SwingAtlas.ScaleOf(s.Ctrl);
                    Vector3 total = s.Ctrl.transform.rotation * (s.Shape.Move * s0);
                    total.y = 0f;
                    if (total.magnitude > 0.15f) slack += total.magnitude * Mathf.Clamp01(1f - Vector3.Dot(s.Ctrl.transform.position - s.TelePos, total.normalized) / total.magnitude);
                }
                string why;
                reach = InSector(s, head, s.Act as ICombatEffectContainer, out why, slack, !s.Holding);
            }
            if (reach) { s.NoThreatSince = -1f; s.Threat = true; return; }
            if (s.NoThreatSince < 0f) s.NoThreatSince = now;
            if (s.Threat && now - s.NoThreatSince >= NoThreatHold)
            {
                s.Threat = false;
                ThreatHides++;
                Note("no threat " + WeaponGrips.ModelKey(s.Ctrl.name) + ": out of its reach - no bar, no circle");
            }
        }

        public static int SideMisses;
        static bool s_lastSide;

        /// UYF's damage sector (docs/uyf-reference.md §3.5; Stage 2 item 4): a blow only happens to the player whose head is
        /// inside an arc in front of the attacker at the impact - by default UYF's common +-45 deg, as far as the game's own
        /// reach (+0.15 m); from the swing atlas, centred on where the blade passes and wide enough for its sweep (its own
        /// angular span + 20 deg, at least 45 deg either side), as far as the blade reaches + 0.5 m when that is further. So
        /// stepping back, or out to the side of a committed swing, avoids it.
        bool InSector(Strike s, Vector3 head, ICombatEffectContainer cont, out string why, float slack = 0f, bool anyArc = false)
        {
            why = "";
            try
            {
                AIController ai = s.Ai;
                Targetable src = s.T;
                if (ai == null || src == null || s.Ctrl == null) return true;
                float reach = cont != null ? Reach(cont, ai) : 2.5f * ai.AIScale;
                Vector3 flat = head - src.AimTransform.position;
                flat.y = 0f;
                float dist = flat.magnitude - (src.UseRadiusInCombat ? src.Radius : 0f), far = reach + 0.15f;
                float centre = 0f, half = 45f;
                if (s.Shape != null)
                {
                    float k = SwingAtlas.ScaleOf(s.Ctrl);
                    Vector3 a = s.Shape.A * k, b = s.Shape.B * k;
                    float ta = Mathf.Atan2(a.x, a.z) * Mathf.Rad2Deg, tb = Mathf.Atan2(b.x, b.z) * Mathf.Rad2Deg;
                    float span = Mathf.Abs(Mathf.DeltaAngle(ta, tb));
                    centre = ta + Mathf.DeltaAngle(ta, tb) * 0.5f;
                    half = Mathf.Max(45f, span * 0.5f + 20f);
                    float bladeFar = Mathf.Max(new Vector2(a.x, a.z).magnitude, new Vector2(b.x, b.z).magnitude);
                    far = Mathf.Max(far, bladeFar + 0.5f);
                }
                Vector3 root = s.Ctrl.transform.position, fwd = s.Ctrl.transform.forward;
                Vector3 to = head - root;
                to.y = 0f;
                fwd.y = 0f;
                float ang = to.sqrMagnitude > 1e-4f && fwd.sqrMagnitude > 1e-4f ? Mathf.DeltaAngle(centre, Vector3.SignedAngle(fwd, to, Vector3.up)) : 0f;
                bool inArc = anyArc || Mathf.Abs(ang) <= half, inDist = dist - slack <= far;
                s_lastSide = inDist && !inArc;
                why = dist.ToString("0.00") + " m of " + far.ToString("0.00") + ", " + ang.ToString("0") + " deg of +-" + half.ToString("0");
                return inArc && inDist;
            }
            catch (Exception) { return true; }
        }

        public static int Arrows, DodgeReadies;

        static Transform Quad(Transform parent, Material m, Vector3 scale) => CombatFeel.Quad(parent, m, scale);

        // ---------------------------------------------------------------------------------------- test seams

        /// `strike?`: the live strikes: attacker, colour, ETA, valid guard, the marker's centre and axis (world) and the
        /// xrsim lines that put a hand's grip there.
        /// `guardwhy`: the live strike's bar against every held guard now (WhyNot's numbers, before the impact).
        public static string WhyNow()
        {
            if (I == null || VRRig.I == null) return "no guard";
            foreach (Strike s in I.m_strikes.Values)
                if (s.Result == null && !s.Guess) return WeaponGrips.ModelKey(s.Ctrl.name) + " " + I.WhyNot(s, VRRig.I.HeadWorldPos);
            return "none";
        }

        /// The live blow the tests and pictures use: the first in view, unjudged, with a bar.
        Strike LiveInView()
        {
            foreach (Strike s in m_strikes.Values)
                if (s.Result == null && !s.Guess && !s.OffView && !s.Dodge && s.Ctrl != null) return s;
            return null;
        }

        static string CoverQuery(string[] a)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 1; i < a.Length; i++)
            {
                if (a[i] == "clear") { s_shieldBlows.Clear(); ShieldBlowsSeen = CoverShort = CoverBlocked = FaceShort = 0; }
                else if (a[i] == "need" && i + 1 < a.Length) { ShieldCover = float.Parse(a[i + 1], inv); i++; }
            }
            var sb = new System.Text.StringBuilder();
            sb.Append("rule cover >= ").Append(ShieldCover.ToString("0.00", inv)).Append(" of the circle r ").Append(CircleR.ToString("0.000", inv))
              .Append(" m, face <= ").Append(ShieldFacingDeg.ToString("0", inv)).Append(" deg | outline ").Append(BladeHits.LastOutline);
            Pose p = s_pose[2];
            sb.Append(" | shield ").Append(Fresh(p) ? "held, " + (p.Outline != null ? p.Outline.Length : 0) + " corners" : "not held");
            if (I != null && VRRig.I != null && Fresh(p))
            {
                Vector3 head = VRRig.I.HeadWorldPos;
                foreach (Strike s in I.m_strikes.Values)
                {
                    if (s.Result != null || s.Guess) continue;
                    Vector3 f;
                    if (!Toward(s, head, out f)) continue;
                    Vector3 center, axis;
                    I.Bar(s, head, f, out center, out axis);
                    float exact = Coverage(p, center, f, Vector3.zero), grid = CoverageGrid(p, center, f, 200);
                    Vector3 d = p.Center - center;
                    sb.Append(" | ").Append(WeaponGrips.ModelKey(s.Ctrl.name)).Append(s.OffView ? " offview" : "").Append(" cover ").Append(exact.ToString("0.000", inv))
                      .Append(" grid ").Append(grid.ToString("0.000", inv)).Append(" centreOff ").Append((d - f * Vector3.Dot(d, f)).magnitude.ToString("0.000", inv))
                      .Append(" depth ").Append(Mathf.Abs(Vector3.Dot(d, f)).ToString("0.000", inv))
                      .Append(" face ").Append(Vector3.Angle(p.Normal, AttPos(s) - p.Center).ToString("0.0", inv)).Append(" green ").Append(s.Valid && s.ValidShield)
                      .Append(" nf ").Append(Vector3.SignedAngle(f, new Vector3(p.Normal.x, 0f, p.Normal.z), Vector3.up).ToString("0.0", inv));
                }
            }
            sb.Append(" | blows ").Append(ShieldBlowsSeen).Append(" short ").Append(CoverShort).Append(" faceShort ").Append(FaceShort).Append(" byShield ").Append(CoverBlocked)
              .Append(" greenBlocked ").Append(GreenBlocked).Append(" greenNotBlocked ").Append(GreenNotBlocked).Append(" blockedNotGreen ").Append(BlockedNotGreen);
            foreach (string l in s_shieldBlows) sb.Append(" || ").Append(l);
            return sb.ToString();
        }

        /// `coverat`: where the shield hand must move (xrsim's `move l|r dx dy dz`, tracking space) for the held shield to cover a
        /// given share of the live blow's circle, or to put its centre a given distance off the circle's centre - along a
        /// direction in the circle's plane, the shield's centre brought into the plane first, the hand's turn kept. The coverage
        /// along such a line falls as the shield leaves the circle (one convex shape sliding off another): found by halving.
        static string CoverAt(string[] a)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (I == null || VRRig.I == null) return "no guard";
            Pose p = s_pose[2];
            if (!Fresh(p) || p.Outline == null) return "no shield";
            Strike s = I.LiveInView();
            if (s == null) return "no strike";
            if (a.Length < 2) return "coverat <share> | off <m> [axis|-axis|right|left|up|down] [noplane]";
            bool off = a[1] == "off";
            float want = float.Parse(off ? (a.Length > 2 ? a[2] : "0") : a[1], inv);
            string dirName = "axis";
            bool plane = true;
            for (int i = off ? 3 : 2; i < a.Length; i++) { if (a[i] == "noplane") plane = false; else dirName = a[i]; }
            Vector3 head = VRRig.I.HeadWorldPos, f;
            if (!Toward(s, head, out f)) return "no direction";
            Vector3 center, axis;
            I.Bar(s, head, f, out center, out axis);
            Vector3 r = Vector3.Cross(Vector3.up, f).normalized, u = Vector3.Cross(f, r).normalized;
            Vector3 ax = (axis - f * Vector3.Dot(axis, f)).normalized;
            Vector3 dir = dirName == "right" ? r : dirName == "left" ? -r : dirName == "up" ? u : dirName == "down" ? -u : dirName == "-axis" ? -ax : ax;
            // the shield's centre onto the circle's centre (in the plane; and into the plane unless noplane), then along dir
            Vector3 d = p.Center - center;
            Vector3 toCentre = -(plane ? d : d - f * Vector3.Dot(d, f));
            float t;
            float c0 = Coverage(p, center, f, toCentre);
            if (off) t = want;
            else if (want >= c0) t = 0f;
            else
            {
                // (a share of 0: just clear of the circle, then 3 cm more)
                float w = want <= 0f ? 1e-4f : want, lo = 0f, hi = 1.5f;
                for (int k = 0; k < 40; k++)
                {
                    float mid = 0.5f * (lo + hi);
                    if (Coverage(p, center, f, toCentre + dir * mid) >= w) lo = mid; else hi = mid;
                }
                t = 0.5f * (lo + hi) + (want <= 0f ? 0.03f : 0f);
            }
            Vector3 move = toCentre + dir * t;
            float got = Coverage(p, center, f, move);
            bool left = BladeHitsShieldLeft();
            Vector3 dT = VRRig.I.FromWorldDir(move);
            return string.Format(inv, "move {0} {1:0.####} {2:0.####} {3:0.####} | cover {4:0.000} t {5:0.000} centreOff {6:0.000} atCentre {7:0.000} face {8:0.0} eta {9:0.00} {10}",
                                 left ? "l" : "r", dT.x, dT.y, -dT.z, got, t, Mathf.Abs(t), c0, Vector3.Angle(p.Normal, AttPos(s) - p.Center),
                                 s.Expected - (FightClock.Now - s.Start), WeaponGrips.ModelKey(s.Ctrl.name));
        }

        static bool BladeHitsShieldLeft()
        {
            PlayerController pc = PlayerController.Instance;
            CombatVR vr = CombatVR.I;
            if (pc == null || vr == null || pc.Inventory == null || pc.Inventory.Shield == null || pc.Inventory.Shield.Model == null) return !Settings.LeftHanded;
            return pc.Inventory.Shield.Model.transform.parent == vr.HandL;
        }

        public static string Query()
        {
            if (I == null) return "no guard";
            var sb = new System.Text.StringBuilder();
            Vector3 head = VRRig.I != null ? VRRig.I.HeadWorldPos : Vector3.zero;
            foreach (Strike s in I.m_strikes.Values)
            {
                if (s.Result != null || s.Guess) continue;   // a guess shows no bar
                Vector3 att = s.T != null ? s.T.AimTransform.position : s.Ctrl.transform.position;
                Vector3 f = att - head;
                f.y = 0f;
                f.Normalize();
                Vector3 c, a;
                I.Bar(s, head, f, out c, out a);
                float eta = s.Expected - (FightClock.Now - s.Start);
                sb.Append(WeaponGrips.ModelKey(s.Ctrl.name)).Append(' ').Append(s.Colour).Append(s.OffView ? " offview" : "").Append(" eta ").Append(eta.ToString("0.00"))
                  .Append(" valid ").Append(s.Valid).Append(" bar ").Append(Diag.V(c)).Append(" axis ").Append(Diag.V(a)).Append(" atlas ").Append(s.Shape != null)
                  .Append(" inrange ").Append(s.InRange).Append(" onShield ").Append(s.ValidShield).Append(" ring ").Append(s.Ring != null && s.Ring.Root.gameObject.activeSelf)
                  .Append(" shieldR ").Append(s.RingRadius.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(" frameIn ").Append(s.Ring != null ? s.Ring.ShownInner.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "-")
                  .Append(" r ").Append(CircleR.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(" need ").Append(ShieldCover.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(" cover ").Append(s.Cover.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(" face ").Append(s.FaceDeg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(" depth ").Append(s.CoverDepth.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(" barShown ").Append(s.Vis != null && s.Vis.Root.gameObject.activeSelf).Append(" threat ").Append(s.Threat).Append(" tid #").Append(s.T != null ? s.T.GetInstanceID() : 0).Append(" ground ").Append(s.GroundMark != null && s.GroundMark.Root.gameObject.activeSelf);
                if (s.Dodge)
                {
                    Vector3 dw = I.DodgeDirWorld(s, f);
                    Vector3 dt = VRRig.I.FromWorldDir(dw);
                    sb.Append(" dodge ").Append(DodgeName(s)).Append(" progress ").Append(s.Progress.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(" dirT ").Append(dt.x.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(dt.y.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(',').Append(dt.z.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                }
                sb.Append(" | ");
            }
            return sb.Length > 0 ? sb.ToString() : "none";
        }

        // `premo bar <fill> <live|valid|ok|fail|fade|out> [age] [angle] [x y]` / `premo arrow <fill> <progress> <live|ok|fail> [down|left|right]` /
        // `premo off`: a still premonition 0.45 m in front of the eyes (x right, y up from the eyes) for the captures
        static IndicatorLooks.IBlock s_demoBar;
        static IndicatorLooks.IDodge s_demoArrow;
        static IndicatorLooks.ISlash s_demoSlash;

        public static string Demo(string[] a)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (s_demoBar != null) { s_demoBar.Destroy(); s_demoBar = null; }
            if (s_demoArrow != null) { s_demoArrow.Destroy(); s_demoArrow = null; }
            if (s_demoSlash != null) { s_demoSlash.Destroy(); s_demoSlash = null; }
            if (a.Length < 2 || a[1] == "off" || VRRig.I == null) return "premo off";
            Vector3 head = VRRig.I.HeadWorldPos;
            Vector3 fw = VRRig.I.HeadWorldRot * Vector3.forward;
            fw.y = 0f;
            fw.Normalize();
            Vector3 r = Vector3.Cross(Vector3.up, fw).normalized;
            float fill = a.Length > 2 ? float.Parse(a[2], inv) : 0.5f;
            if (a[1] == "bar")
            {
                string st = a.Length > 3 ? a[3] : "live";
                float age = a.Length > 4 ? float.Parse(a[4], inv) : 1f, ang = a.Length > 5 ? float.Parse(a[5], inv) : 90f;
                float x = a.Length > 7 ? float.Parse(a[6], inv) : 0f, y = a.Length > 7 ? float.Parse(a[7], inv) : -0.12f;
                // the look in use (`vrlook`); the ghost's weapon is the nearest enemy's
                Targetable near = Physical.FindEnemy("nearest");
                s_demoBar = IndicatorLooks.NewBlock(CombatSettings.Look, new IndicatorLooks.Context { Attacker = near != null ? near.Controller : null, From = 1f }, "HoF2VR_DemoBar");
                Vector3 c = head + fw * 0.45f + r * x + Vector3.up * y;
                Vector3 axis = (r * Mathf.Cos(ang * Mathf.Deg2Rad) + Vector3.up * Mathf.Sin(ang * Mathf.Deg2Rad)).normalized;
                Vector3 view = (c - head).normalized;
                s_demoBar.Root.position = c;
                s_demoBar.Root.rotation = Quaternion.LookRotation(view, Vector3.Cross(view, axis).normalized);
                string res = st == "ok" || st == "fail" || st == "fade" ? st : null;
                Color dc = a.Length > 8 && a[8] == "heavy" && CombatSettings.IndicatorByType ? IndicatorLooks.Orange : Premonition.Cyan;
                s_demoBar.Draw(res != null ? 1f : age, fill, st == "valid", st != "out", res, res != null ? age : 0f, dc);
                return "premo bar fill " + fill + " " + st + " at " + Diag.V(c);
            }
            // `premo crit <left> <break|combo|interrupt> <live|ok|timeout> [result age] [angle]`: a still crit slash 0.6 m ahead
            if (a[1] == "crit")
            {
                float left = a.Length > 2 ? float.Parse(a[2], inv) : 0.6f;
                string kind = a.Length > 3 ? a[3] : "combo", st = a.Length > 4 ? a[4] : "live";
                float rage = a.Length > 5 ? float.Parse(a[5], inv) : 0.08f, ang = a.Length > 6 ? float.Parse(a[6], inv) : -135f;
                s_demoSlash = IndicatorLooks.NewSlash(CombatSettings.Look, "HoF2VR_DemoSlash");
                Vector3 c = head + fw * 0.6f - Vector3.up * 0.1f;
                Vector3 view = (c - head).normalized;
                Vector3 up = Vector3.up - view * Vector3.Dot(Vector3.up, view);
                up.Normalize();
                Vector3 right = Vector3.Cross(up, view);
                Vector3 axis = (right * Mathf.Cos(ang * Mathf.Deg2Rad) + up * Mathf.Sin(ang * Mathf.Deg2Rad)).normalized;
                s_demoSlash.Root.position = c;
                s_demoSlash.Root.rotation = Quaternion.LookRotation(view, Vector3.Cross(view, axis));
                string res = st == "ok" || st == "timeout" ? st : null;
                s_demoSlash.Draw(1f, left, res, res != null ? rage : 0f, 99f, kind == "break", kind == "interrupt");
                return "premo crit " + kind + " " + st + " left " + left;
            }
            if (a[1] == "arrow")
            {
                float prog = a.Length > 3 ? float.Parse(a[3], inv) : 0f;
                string st = a.Length > 4 ? a[4] : "live", way = a.Length > 5 ? a[5] : "down";
                float age = a.Length > 6 ? float.Parse(a[6], inv) : 0.15f;
                Vector3 d = way == "left" ? -r : way == "right" ? r : Vector3.down;
                s_demoArrow = IndicatorLooks.NewDodge(CombatSettings.Look, IndicatorLooks.Context.None, "HoF2VR_DemoArrow");
                Vector3 at = head + fw * 0.42f + d * 0.07f - Vector3.up * (way == "down" ? 0.02f : 0.06f);
                Vector3 view = (at - head).normalized;
                Vector3 up = d - view * Vector3.Dot(d, view);
                s_demoArrow.Root.position = at;
                s_demoArrow.Root.rotation = Quaternion.LookRotation(view, up.normalized);
                string res = st == "ok" || st == "fail" ? st : null;
                s_demoArrow.Draw(1f, fill, prog, res, res != null ? age : 0f);
                return "premo arrow " + way + " fill " + fill + " progress " + prog + " " + st;
            }
            return "premo bar|arrow|off";
        }

        /// This attacker's strike is shown to the player now (a bar: aimed at the player, not yet over).
        public static bool Shows(Controller c)
        {
            Strike s;
            return I != null && c != null && I.m_strikes.TryGetValue(c.GetInstanceID(), out s) && s.Result == null;
        }

        /// The attacker of the live strike (for `face striker`).
        public static Targetable Striker()
        {
            if (I == null) return null;
            foreach (Strike s in I.m_strikes.Values) if (s.Result == null && s.T != null) return s.T;
            return null;
        }

        /// The next strike's marker centre and the direction to its attacker (world), for `guardat`.
        public static bool NextBar(out Vector3 center, out Vector3 axis, out Vector3 toAttacker, out float eta, out string colour)
        {
            center = axis = toAttacker = Vector3.zero;
            eta = 0f;
            colour = "";
            if (I == null || VRRig.I == null) return false;
            Vector3 head = VRRig.I.HeadWorldPos;
            foreach (Strike s in I.m_strikes.Values)
            {
                if (s.Result != null || s.Guess) continue;   // a guess shows no bar
                Vector3 att = s.T != null ? s.T.AimTransform.position : s.Ctrl.transform.position;
                Vector3 f = att - head;
                f.y = 0f;
                f.Normalize();
                I.Bar(s, head, f, out center, out axis);
                toAttacker = f;
                eta = s.Expected - (FightClock.Now - s.Start);
                colour = s.Colour.ToString();
                return true;
            }
            return false;
        }
    }
}

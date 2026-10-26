using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Physical mode's Strike (docs/combat-uyf-design.md §8.5.7, §2.1): real swings; hits land where the blade lands.
    ///  * The held models (CombatVR) are the blades: each one's striking part (sword: from 0.18 m past the fist to the
    ///    tip; axe and mace heads: the last 35 %; polearms: the last 25 %) is swept every frame against the enemies'
    ///    bodies (their TargetableShape colliders), in sub-steps, so a fast swing never passes through.
    ///  * A swing is legal when the striking part's mean velocity over the last Envelope reaches MinSwing x 0.85 (UYF's
    ///    rule on the heavier weapons; reversals cancel, so waggling never gets there), or when a short chop is fast (1.6 x
    ///    over 80 ms) and goes one way (at least 0.25 m, and 60 % of its path over the envelope); there is no upper limit:
    ///    a fast swing is never rejected. A thrust along the blade (>= 1.8 m/s over 80 ms) is a legal stab. Speeds come
    ///    from the real hands only (tracking space): walking and turning add nothing.
    ///  * Quality by how far the blade travelled since the swing turned legal, or by its speed at the contact: Under (x0.3,
    ///    a swing that only just got there), Standard (x1), Big (x1.2: the blade travelled on, or it moved 2.2 x MinSwing).
    ///    One hit per swing per enemy, a re-hit cooldown, and 3 invisible hit charges (+1 every 0.25 s after 0.4 s
    ///    without hits; with none left a hit does x0.25). A slow touch does nothing but a light tick.
    ///  * The hit goes through the game's own path without the hero's attack action (no lunge, no animation):
    ///    GetNextAttack (its combo tiers), Use, Hit, CombatEffect.Apply, the weapon's hit effect at the blade.
    ///  * The situational answers: a downed enemy takes the finisher (any legal hit); armour takes quick hits as flurry
    ///    hits (the game breaks armour only with those); a hit from more than 100 deg off a blocking enemy's facing gets
    ///    past its block (flank); the shield driven face-first into an enemy at >= 1.6 m/s is the game's bash (a stun that
    ///    breaks guards); after a counter the next hit on that enemy is the riposte.
    internal class BladeHits : MonoBehaviour
    {
        public static BladeHits I;
        public static int Swings, Hits, Under, Standard, Big, Illegal, Uncharged, Blocked, Whiffed, Flurries, Bashes, Finishers, Ripostes, Flanks, Stabs, LastKills;
        public static string Last = "-";
        public static string LastOutline = "-";   // the held shield's outline: where it came from (`shieldcover?`)
        public static float LastDamage;
        static readonly List<string> s_log = new List<string>();

        enum Part { Blade, Head, Point }
        sealed class Held
        {
            public int Id;
            public Transform T;
            public bool Left, Shield;
            public Part Kind;
            public Vector3 LGrip, LBase, LTip, LCenter, LNormal;   // hand-local
            public Vector3 MCenter, MFront;                         // shields: model-local (the hand-local pair follows the drawn shield each frame)
            public Vector3[] MOutline;                              // ... its outline (model-local, ShieldAxis.Outline): Guard's coverage of the circle
            public Matrix4x4 M2H = Matrix4x4.identity;              // ... model -> hand-local (Track's; Frame takes the drawn one)
            public float Radius, Disc;
            public Vector3 Base, Tip, PBase, PTip, Center, Normal, PCenter;   // world, this frame and the last
            public bool HasPrev;
            public Vector3 LastHand, LastMidT;
            public readonly List<Sample> Hist = new List<Sample>();
            public bool Legal, Stab, WasLegal;
            public float LegalSince, Travel, Speed;
            public float StabEnergy;       // UYF's stab energy: +cos x 20/s while the tip moves along the blade (cos >= 0.7), else -3/s
            public readonly HashSet<int> Swung = new HashSet<int>();
            public readonly HashSet<int> Inside = new HashSet<int>(), InsideNow = new HashSet<int>();
            public readonly Dictionary<int, float> EnteredAt = new Dictionary<int, float>();
            public float NextBash, NextGeo;
            public readonly Dictionary<int, CritMiss> Misses = new Dictionary<int, CritMiss>();   // this swing's tries at open crits that missed the arrow
        }
        /// A legal cut's frames through an open crit (the body or the line) that did not follow its arrow: kept for the swing,
        /// a wrong cut only if the swing ends without one that did (Settle).
        sealed class CritMiss
        {
            public Targetable T;
            public float Opened, SwingAt, Last, Speed;
            public Poise.Try Best;
            public Vector3 Point;
            public int Frames;
            public bool Body, Line;
        }
        struct Sample { public float T; public Vector3 Mid, Tip, Center; public Vector3 Axis; }

        readonly Dictionary<int, Held> m_held = new Dictionary<int, Held>();
        struct StabMark { public float At; public Vector3 Dir; }
        readonly Dictionary<int, StabMark> m_stabbed = new Dictionary<int, StabMark>();
        public static int PullOuts;
        readonly Dictionary<int, float> m_nextValid = new Dictionary<int, float>();
        static readonly Collider[] s_cols = new Collider[16];
        static int s_mask;
        float m_charges = 3f, m_lastHitAt = -10f, m_nextIllegalTick, m_lastComboHit = -10f;
        int m_flurryIndex;
        Targetable m_flurryTarget;
        float m_flurryAt = -10f;
        static PlayerController s_scaledOn;
        static ICombatEffectContainer s_scaleFor;
        static float s_scale = 1f;
        static readonly AccessTools.FieldRef<Controller, Hit> s_lastHit = AccessTools.FieldRefAccess<Controller, Hit>("m_lastHit");

        void Awake()
        {
            I = this;
            s_mask = Targetable.TeamMaskToLayerMask(TeamType.Enemy);
        }

        public static string Describe() =>
            "swings " + Swings + " hits " + Hits + " (under " + Under + " standard " + Standard + " big " + Big + ") illegal " + Illegal + " uncharged " + Uncharged +
            " blocked " + Blocked + " whiffed " + Whiffed + " flurry " + Flurries + " bash " + Bashes + " finisher " + Finishers + " riposte " + Ripostes +
            " flank " + Flanks + " stab " + Stabs + " pullOuts " + PullOuts + " critTries " + CritTries + " critTriesLanded " + CritTriesLanded + " drawnJumps " + DrawnJumps + " crits " + Crits + " guardHits " + GuardHits + " healthHits " + HealthHits + " wrongWay " + WrongWay + " deflects " + Deflects + " breakThroughs " + BreakThroughs + " kicks " + Kicks + " magnets " + CombatMove.Magnets + " magnetLast " + CombatMove.MagnetLast.ToString("0.00") +
            " magnetStops " + CombatMove.MagnetStops + " magnetWhy (" + CombatMove.MagnetWhy + ") offAgent " + CombatMove.OffAgentMoves + " rangedHits " + CombatFeel.RangedHits + " stabsLegal " + StabsLegal + " charges " + (I != null ? I.m_charges.ToString("0.0") : "-") + " lastKills " + LastKills + " last " + Last;

        public static void ResetCounts()
        {
            Swings = Hits = Under = Standard = Big = Illegal = Uncharged = Blocked = Whiffed = Flurries = Bashes = Finishers = Ripostes = Flanks = Stabs = LastKills = StabsLegal = Crits = GuardHits = HealthHits = WrongWay = Deflects = BreakThroughs = Kicks = 0;
            s_log.Clear();
            Last = "-";
        }

        public static string Log() => string.Join("\n  ", s_log.ToArray());

        static void Note(string s)
        {
            Last = s;
            if (s_log.Count < 300) s_log.Add(Time.unscaledTime.ToString("0.00") + " " + s);
        }

        // ---------------------------------------------------------------------------------------- the held geometry

        /// The weapon's class (Stage 2 item 3: UYF's own numbers per weapon, WeaponTable): swing threshold, envelope, the
        /// travel for Standard / Big, the re-hit cooldown.
        static void Rules(out float minSwing, out float env, out float std, out float big, out float cooldown)
        {
            WeaponSpec w = WeaponTable.Current();
            minSwing = w.MinSwing;
            env = w.Env;
            std = w.StdSwing;
            big = w.BigSwing;
            cooldown = w.ReHit;
        }

        Held Track(Transform t, bool shield)
        {
            CombatVR vr = CombatVR.I;
            if (t == null || vr == null) return null;
            bool left;
            if (t.parent == vr.HandL) left = true;
            else if (t.parent == vr.HandR) left = false;
            else return null;   // not in a hand (yet)
            int id = t.GetInstanceID();
            Held h;
            if (!m_held.TryGetValue(id, out h) || h.T != t)
            {
                h = new Held { Id = id, T = t, Shield = shield };
                m_held[id] = h;
            }
            // the model's shape relative to the hand: measured once a second (it only changes when the grip is re-placed); not
            // while a deflect kicks it
            if (h.Left == left && (Time.unscaledTime < h.NextGeo || Kicking(t))) return h;
            h.NextGeo = Time.unscaledTime + 1f;
            h.Left = left;
            Transform hand = t.parent;
            Bounds lb = CombatVR.LocalBounds(t);
            Vector3 lng, mid, thin;
            CombatVR.Axes(lb, out lng, out mid, out thin);
            if (shield)
            {
                // the front as CombatVR draws it (candidate 8: the mesh guess's -z was the back once F7 took the front from the
                // game's pose - the judge saw the shield facing the player and no block counted, stage1 block 0/9)
                Vector3 front;
                if (CombatVR.ShieldFront(t, out front))
                {
                    if (Vector3.Dot(front, thin) < 0f && s_frontNoted.Add(id))
                        Plugin.L.LogInfo("BladeHits: the shield's judged face is its drawn front " + Diag.V(front) + " (the mesh guess " + Diag.V(thin) +
                                         " was its back: drawn and judged disagreed); its face in the world now " + Diag.V(t.TransformDirection(front).normalized));
                    thin = front;
                }
                h.MCenter = lb.center;
                h.MFront = thin;
                if (h.MOutline == null)
                {
                    // 2026-10-20 (the user: "Any part of the shield has to cover at least 55% of the circle"): the shield's own
                    // outline, once per model; a model without a readable mesh gets a disc of its bounds' radius
                    string how;
                    h.MOutline = ShieldAxis.Outline(t, thin, lb.center, out how);
                    if (h.MOutline == null)
                    {
                        float r0 = 0.5f * Mathf.Max(Vector3.Scale(lb.size, lng).magnitude, Vector3.Scale(lb.size, mid).magnitude);
                        h.MOutline = ShieldAxis.Disc(thin, lb.center, r0, 32);
                        how = "no readable mesh (" + how + "): a disc of the bounds' radius " + r0.ToString("0.000") + " model units";
                    }
                    LastOutline = t.name + ": " + how;
                    Plugin.L.LogInfo("BladeHits: the shield's outline for the circle's coverage - " + LastOutline);
                }
                h.M2H = hand.worldToLocalMatrix * t.localToWorldMatrix;
                h.LCenter = hand.InverseTransformPoint(t.TransformPoint(lb.center));
                h.LNormal = hand.InverseTransformDirection(t.TransformDirection(thin)).normalized;
                Vector3 s = t.lossyScale;
                float a = Vector3.Scale(Vector3.Scale(lb.size, lng), s).magnitude, b = Vector3.Scale(Vector3.Scale(lb.size, mid), s).magnitude;
                h.Disc = 0.5f * Mathf.Max(a, b);
                return h;
            }
            float half = 0.5f * Mathf.Abs(Vector3.Dot(lb.size, lng));
            Vector3 tipModel = lb.center + lng * half;
            h.LGrip = ControllerGeometry.GripPos(left);
            h.LTip = WeaponFeel.HandLocal(t, tipModel);   // the rest pose CombatVR placed (WeaponFeel draws a lag offset on top)
            float len = Vector3.Distance(h.LGrip, h.LTip);
            WeaponCategory cat = WeaponCategory.Sword;
            try { Weapon w = PlayerController.Instance.Inventory.Weapon; if (w != null && w.Ref != null) cat = w.Ref.Category; } catch (Exception) { }
            float baseAt;
            if (cat == WeaponCategory.Axe || cat == WeaponCategory.Mace) { h.Kind = Part.Head; baseAt = 0.65f; h.Radius = 0.10f; }
            else if (cat == WeaponCategory.Polearm) { h.Kind = Part.Point; baseAt = 0.75f; h.Radius = 0.06f; }
            else { h.Kind = Part.Blade; baseAt = len > 0.01f ? Mathf.Clamp01(Mathf.Min(0.18f, len * 0.5f) / len) : 0.3f; h.Radius = 0.05f; }
            h.LBase = Vector3.Lerp(h.LGrip, h.LTip, baseAt);
            return h;
        }

        /// Hand-local -> tracking space (the real controller) and world.
        static Vector3 TrackingOf(Hand hand, Vector3 local) => hand.Pos + hand.Rot * local;

        // ---------------------------------------------------------------------------------------- every frame

        readonly List<int> m_seen = new List<int>();

        void LateUpdate()
        {
            if (!Physical.Live) { if (m_held.Count > 0) m_held.Clear(); return; }
            PlayerController pc = PlayerController.Instance;
            Inventory inv = pc != null ? pc.Inventory : null;
            if (inv == null || VRRig.I == null) return;
            EnsureScale(pc);
            float now = Time.unscaledTime;
            // hit charges come back: +1 every 0.25 s once 0.4 s passed without a hit
            if (now - m_lastHitAt > 0.4f) m_charges = Mathf.Min(3f, m_charges + Time.unscaledDeltaTime / 0.25f);
            // the combo chain resets after 1.5 s without a hit (the game's own flurry idle time)
            if (m_lastComboHit > 0f && now - m_lastComboHit > 1.5f)
            {
                m_lastComboHit = -10f;
                try { pc.Combat.ResetAttack(true, true); } catch (Exception) { }
            }
            m_seen.Clear();
            m_bladeList.Clear();
            try
            {
                if (inv.Weapon != null && inv.Weapon.AllModels != null)
                    foreach (Model m in inv.Weapon.AllModels)
                        if (m != null) Frame(Track(m.transform, false), pc, now);
                if (inv.Shield != null && inv.Shield.Model != null && inv.Shield.Model != pc.Model)
                    Frame(Track(inv.Shield.Model.transform, true), pc, now);
            }
            catch (Exception e) { Plugin.L.LogWarning("BladeHits: " + e); }
            RunKicks();
        }

        /// The player's blades this frame (world; shields left out): for WeaponTrail.
        public static int BladeCount => I != null ? I.m_bladeList.Count : 0;
        public static bool BladeAt(int i, out Vector3 baseW, out Vector3 tipW, out int id)
        {
            baseW = tipW = Vector3.zero;
            id = 0;
            if (I == null || i < 0 || i >= I.m_bladeList.Count) return false;
            Held h = I.m_bladeList[i];
            if (h == null || h.T == null || !h.HasPrev) return false;
            baseW = h.Base;
            tipW = h.Tip;
            id = h.Id;
            return true;
        }
        readonly List<Held> m_bladeList = new List<Held>();

        void Frame(Held h, PlayerController pc, float now)
        {
            if (h == null) return;
            if (!h.Shield && !m_bladeList.Contains(h)) m_bladeList.Add(h);
            Hand hand = h.Left ? VRInput.L : VRInput.R;
            if (!hand.Tracked) { h.HasPrev = false; return; }
            // a tracking jump (> 0.5 m between two frames: no hand moves that fast) is no swing: start over (WeaponFeel puts the
            // weapon there at once on the same jump)
            if (h.HasPrev && (hand.Pos - h.LastHand).sqrMagnitude > WeaponFeel.JumpSnap * WeaponFeel.JumpSnap) { h.Hist.Clear(); h.Legal = false; h.HasPrev = false; }
            h.LastHand = hand.Pos;
            m_seen.Add(h.Id);
            VRRig rig = VRRig.I;
            if (h.Shield)
            {
                // playtest 3 (mark 29): CombatVR turns the shield about the fist each frame (never a tray) - judge it where it is drawn
                Matrix4x4 m2h = h.M2H;
                if (h.T != null && h.T.parent != null && h.MFront.sqrMagnitude > 0.5f)
                {
                    h.LCenter = h.T.parent.InverseTransformPoint(h.T.TransformPoint(h.MCenter));
                    h.LNormal = h.T.parent.InverseTransformDirection(h.T.TransformDirection(h.MFront)).normalized;
                    m2h = h.T.parent.worldToLocalMatrix * h.T.localToWorldMatrix;
                }
                Vector3 cT = TrackingOf(hand, h.LCenter);
                h.PCenter = h.Center;
                h.Center = rig.ToWorld(cT);
                h.Normal = rig.ToWorld(hand.Rot) * h.LNormal;
                Push(h, now, cT, cT, cT, hand.Rot * h.LNormal);
                // the outline goes where the centre goes (model -> hand-local -> the hand's tracking pose -> world): Guard's coverage
                Vector3 o = rig.ToWorld(TrackingOf(hand, m2h.MultiplyPoint3x4(Vector3.zero)));
                Matrix4x4 m2w = Matrix4x4.identity;
                m2w.SetColumn(0, rig.ToWorld(TrackingOf(hand, m2h.MultiplyPoint3x4(Vector3.right))) - o);
                m2w.SetColumn(1, rig.ToWorld(TrackingOf(hand, m2h.MultiplyPoint3x4(Vector3.up))) - o);
                m2w.SetColumn(2, rig.ToWorld(TrackingOf(hand, m2h.MultiplyPoint3x4(Vector3.forward))) - o);
                m2w.SetColumn(3, new Vector4(o.x, o.y, o.z, 1f));
                Guard.Feed(h.Left, true, h.Center, h.Normal, h.Disc, Vector3.zero, Vector3.zero, cT, cT, h.MOutline, m2w);
                if (h.HasPrev) Bash(h, pc, now);
                h.HasPrev = true;
                return;
            }
            // the weapon where it is drawn: WeaponFeel's follow (UYF's lag, a block's knock, the two-handed turn), else the controller
            Vector3 hp = hand.Pos;
            Quaternion hr = hand.Rot;
            Vector3 fp;
            Quaternion fr;
            if (WeaponFeel.Pose(h.Left, out fp, out fr)) { hp = fp; hr = fr; }
            Vector3 bT = hp + hr * h.LBase, tT = hp + hr * h.LTip, gT = hp + hr * h.LGrip;
            // the DRAWN blade's jump too (WeaponFeel's pose can be a frame behind the controller: a jump of the controller reset
            // the swing above, and the drawn blade then made the same jump a frame later - a fake fast swing through whatever lay
            // between, which landed and spoilt crits in the playtest-4 checks' scripted cuts; a real tracking glitch does it too)
            Vector3 midNow = (bT + tT) * 0.5f;
            if (h.HasPrev && (midNow - h.LastMidT).magnitude > Mathf.Max(WeaponFeel.JumpSnap, 25f * Time.unscaledDeltaTime)) { h.Hist.Clear(); h.Legal = false; h.HasPrev = false; DrawnJumps++; }
            h.LastMidT = midNow;
            h.PBase = h.Base;
            h.PTip = h.Tip;
            h.Base = rig.ToWorld(bT);
            h.Tip = rig.ToWorld(tT);
            Vector3 midT = (bT + tT) * 0.5f;
            Push(h, now, midT, tT, gT, (tT - gT).normalized);
            Guard.Feed(h.Left, false, Vector3.zero, Vector3.zero, 0f, rig.ToWorld(gT), h.Tip, bT, tT, null, Matrix4x4.identity);
            Grade(h, now);
            if (h.HasPrev) Sweep(h, pc, now);
            h.HasPrev = true;
            h.WasLegal = h.Legal;
        }

        static void Push(Held h, float now, Vector3 mid, Vector3 tip, Vector3 center, Vector3 axis)
        {
            h.Hist.Add(new Sample { T = now, Mid = mid, Tip = tip, Center = center, Axis = axis });
            int drop = 0;
            while (drop < h.Hist.Count - 2 && now - h.Hist[drop + 1].T > 0.45f) drop++;
            if (drop > 0) h.Hist.RemoveRange(0, drop);
        }

        /// The sample at least `ago` seconds old (the newest such), or the oldest one.
        static Sample Ago(Held h, float now, float ago)
        {
            Sample best = h.Hist[0];
            for (int i = h.Hist.Count - 1; i >= 0; i--)
                if (now - h.Hist[i].T >= ago) { best = h.Hist[i]; break; }
            return best;
        }

        /// Where the blade is going (world, unit): the striking part's motion over the last 0.1 s (UYF's HitDirectionEnvelope;
        /// tracking space, so walking and turning don't count), turned into the world.
        static Vector3 SwingDir(Held h, float now)
        {
            if (h.Hist.Count < 2 || VRRig.I == null) return h.Tip - h.PTip;
            Sample cur = h.Hist[h.Hist.Count - 1], old = Ago(h, now, 0.1f);
            Vector3 d = (h.Stab ? cur.Tip - old.Tip : cur.Mid - old.Mid);
            if (d.sqrMagnitude < 1e-8f) return h.Tip - h.PTip;
            return VRRig.I.ToWorldDir(d).normalized;
        }

        /// UYF's WeaponHitTracker (docs/uyf-reference.md §3.7) with the held weapon's own numbers (WeaponTable):
        ///  * a legal swing: the striking part's mean velocity over the weapon's envelope >= its MinLegalSwingSpeed (reversals
        ///    cancel, so a waggle never gets there); kept from Phase 1B: a short fast one-way chop (1.6 x over 80 ms, >= 0.25 m,
        ///    >= 60 % of its path) is legal too, so a fast swing is never rejected;
        ///  * a legal stab: UYF's stab energy (+cos x 20/s while the tip moves along the blade within cos >= 0.7, else -3/s;
        ///    clamped 0..1) at >= 0.5 and the tip's mean speed over the stab envelope >= MinLegalStabSpeed;
        ///  * quality (Contact): the travel since it turned legal against the weapon's Standard / Big distances.
        void Grade(Held h, float now)
        {
            WeaponSpec w = WeaponTable.Current();
            float minSwing = w.MinSwing, env = w.Env;
            Sample cur = h.Hist[h.Hist.Count - 1];
            Sample old = Ago(h, now, env);
            float span = now - old.T;
            float net = (cur.Mid - old.Mid).magnitude;
            float v = span >= env * 0.6f ? net / span : 0f;
            // the path over the same window: a waggle travels far but gets nowhere (reversals cancel)
            float path = 0f;
            for (int i = h.Hist.Count - 1; i > 0 && h.Hist[i].T > old.T; i--) path += (h.Hist[i].Mid - h.Hist[i - 1].Mid).magnitude;
            // the speed now (the last 80 ms): a short fast chop is never rejected, as long as it goes one way
            Sample o8 = Ago(h, now, 0.08f);
            float s8 = now - o8.T;
            float v8 = s8 >= 0.03f ? (cur.Mid - o8.Mid).magnitude / s8 : 0f;
            bool chop = v8 >= minSwing * 1.6f && net >= 0.25f && net >= 0.6f * path;
            // UYF's stab energy from the tip's motion this frame
            if (h.Hist.Count >= 2)
            {
                Sample prev = h.Hist[h.Hist.Count - 2];
                Vector3 d = cur.Tip - prev.Tip;
                float sd = cur.T - prev.T, m = d.magnitude;
                if (sd > 1e-4f)
                {
                    float c = m / sd >= 0.5f ? Vector3.Dot(d / m, cur.Axis) : 0f;
                    h.StabEnergy = c >= 0.7f ? Mathf.Min(1f, h.StabEnergy + c * 20f * sd) : Mathf.Max(0f, h.StabEnergy - 3f * sd);
                }
            }
            Sample os = Ago(h, now, w.StabEnv);
            float ss = now - os.T;
            float vs = ss >= w.StabEnv * 0.6f ? Vector3.Dot(cur.Tip - os.Tip, cur.Axis) / ss : 0f;
            bool stab = h.StabEnergy >= 0.5f && vs >= w.MinStab;
            // the speed that grades a hit that met the body before its swing turned legal: the fastest of the windows (40 ms
            // = the blade's speed right now)
            Sample o4 = Ago(h, now, 0.04f);
            float s4 = now - o4.T;
            float v4 = s4 >= 0.02f ? (cur.Mid - o4.Mid).magnitude / s4 : 0f;
            h.Speed = Mathf.Max(v, Mathf.Max(v8, v4));
            if (!h.Legal)
            {
                if (v >= minSwing || chop || stab)
                {
                    h.Legal = true;
                    h.Stab = v < minSwing && !chop;
                    h.LegalSince = now;
                    h.Travel = 0f;
                    h.Swung.Clear();
                    Swings++;
                    if (h.Stab) StabsLegal++;
                    CombatFeel.Tick(h.Left, 4f);
                    // a whoosh by speed (a swing that turned legal)
                    CombatSounds.Layer(CombatSounds.Whoosh, h.Tip, Mathf.Clamp01(0.25f + (h.Speed - minSwing) * 0.06f) * 0.6f, Mathf.Clamp(0.9f + h.Speed * 0.03f, 0.9f, 1.25f));
                }
            }
            else
            {
                if (h.Hist.Count >= 2) h.Travel += ((h.Stab ? cur.Tip : cur.Mid) - (h.Stab ? h.Hist[h.Hist.Count - 2].Tip : h.Hist[h.Hist.Count - 2].Mid)).magnitude;
                bool still = h.Stab ? stab || h.StabEnergy >= 0.5f && vs >= w.MinStab * 0.5f : v >= minSwing * 0.5f || v8 >= minSwing;
                if (!still) h.Legal = false;
            }
        }

        public static int StabsLegal;

        /// UYF's quality of a legal swing or stab from its travel since it turned legal: 0 Under, 1 Standard, 2 Big. A blade that
        /// met the body before its swing turned legal (an enemy so close the swing starts inside it) is graded by its speed
        /// instead (Big >= 2.2 x the legal speed, Standard >= 1.2 x).
        static int Quality(Held h, WeaponSpec w, bool metEarly)
        {
            if (h.Stab) return h.Travel >= w.BigStab ? 2 : h.Travel >= w.StdStab ? 1 : 0;
            if (h.Travel >= w.BigSwing) return 2;
            if (h.Travel >= w.StdSwing) return 1;
            if (metEarly) return h.Speed >= w.MinSwing * 2.2f ? 2 : h.Speed >= w.MinSwing * 1.2f ? 1 : 0;
            return 0;
        }

        void Sweep(Held h, PlayerController pc, float now)
        {
            Settle(h, now);
            h.InsideNow.Clear();
            float dt = Mathf.Max((h.Tip - h.PTip).magnitude, (h.Base - h.PBase).magnitude);
            int n = Mathf.Clamp(Mathf.CeilToInt(dt / 0.05f), 1, 8);
            Targetable me = pc.Targetable;
            for (int i = 1; i <= n; i++)
            {
                float k = i / (float)n;
                Vector3 a = Vector3.Lerp(h.PBase, h.Base, k), b = Vector3.Lerp(h.PTip, h.Tip, k);
                int c = Physics.OverlapCapsuleNonAlloc(a, b, h.Radius, s_cols, s_mask, QueryTriggerInteraction.Collide);
                for (int j = 0; j < c; j++)
                {
                    Collider col = s_cols[j];
                    Targetable t = TargetUtils.GetTargetable(col) ?? col.GetComponentInParent<Targetable>();
                    // (playtest 4: a gnome standing on the ground is untargetable in HoF2 but is hit here - GnomeFight.Hittable)
                    if (t == null || t == me || !me.CanAttack(t) || (t.IgnoreTargeting && !GnomeFight.Hittable(t)) || !me.IsEnemy(t)) continue;
                    int id = t.GetInstanceID();
                    if (!h.InsideNow.Add(id)) continue;
                    Vector3 mid = (a + b) * 0.5f;
                    Vector3 p;
                    try { p = col.ClosestPoint(mid); } catch (Exception) { p = col.bounds.ClosestPoint(mid); }
                    Contact(h, pc, t, p, now);
                }
                // playtest 4: a standing gnome's whole body (its own capsule covers only its lower 0.62 m)
                List<Targetable> gnomes = GnomeFight.Ground;
                for (int j = 0; j < gnomes.Count; j++)
                {
                    Targetable t = gnomes[j];
                    if (t == null || h.InsideNow.Contains(t.GetInstanceID())) continue;
                    Vector3 gp;
                    if (!GnomeFight.BladeMeets(t, a, b, h.Radius, out gp)) continue;
                    h.InsideNow.Add(t.GetInstanceID());
                    Contact(h, pc, t, gp, now);
                }
            }
            // F8: a leaping gnome (untargetable: the overlap above skips it) and the clinging gnome's ring
            // (playtest 4: a thrust at the clinging gnome counts at its ring too)
            if (h.Legal && (!h.Stab || GnomeFight.Clinging)) GnomeFight.Blade(h.PBase, h.PTip, h.Base, h.Tip, SwingDir(h, now));
            // a cut through a crit's line counts at any depth (Poise.CrossedLines): the enemies the blade did not meet this frame
            if (h.Legal && !h.Stab && VRRig.I != null)
            {
                Vector3 sd = SwingDir(h, now), eye = VRRig.I.HeadWorldPos;
                Poise.CrossedLines(eye, h.PBase, h.PTip, h.Base, h.Tip, sd, s_lineHits, s_linePts, s_lineMisses);
                for (int i = 0; i < s_lineHits.Count; i++)
                {
                    Targetable t = s_lineHits[i];
                    int id = t.GetInstanceID();
                    if (h.InsideNow.Contains(id) || h.Inside.Contains(id) || h.Swung.Contains(id)) continue;
                    float next;
                    if (m_nextValid.TryGetValue(id, out next) && now < next) continue;
                    LineHits++;
                    Note("through the line: " + WeaponGrips.ModelKey(t.name) + " (the blade did not reach the body)");
                    // graded by its speed (as a blade that met the body early): the travel since the swing turned legal is
                    // near 0 where it crosses a line ahead of the body (PF3h: "CRIT under ... travel 0.00 through the line")
                    Land(h, pc, t, s_linePts[i], now, true, " through the line");
                }
                // through the line against or across the arrow: kept as the swing's try (Settle)
                for (int i = 0; i < s_lineMisses.Count; i++)
                {
                    Targetable t = s_lineMisses[i];
                    float opened;
                    if (t == null || h.Swung.Contains(t.GetInstanceID()) || !Poise.CritOpen(t, out opened, true)) continue;
                    Miss(h, t, opened, Poise.Judge(t, sd, eye), h.Tip, now, false);
                }
            }
            h.Inside.Clear();
            foreach (int id in h.InsideNow) h.Inside.Add(id);
        }
        static readonly List<Targetable> s_lineHits = new List<Targetable>(), s_lineMisses = new List<Targetable>();
        static readonly List<Vector3> s_linePts = new List<Vector3>();
        public static int LineHits, CritTries, CritTriesLanded, DrawnJumps;

        /// A frame of a legal cut through an open crit that did not follow its arrow (Contact: the body; Sweep: the line):
        /// the swing's best such frame is kept; nothing is used up and the crit stays open (UYF re-judges its crit every frame
        /// the blade is in it, docs/uyf-reference.md §3.9).
        void Miss(Held h, Targetable t, float opened, Poise.Try ct, Vector3 point, float now, bool body)
        {
            int id = t.GetInstanceID();
            CritMiss m;
            if (!h.Misses.TryGetValue(id, out m) || m.SwingAt != h.LegalSince || Mathf.Abs(m.Opened - opened) > 1e-4f)
            {
                h.Misses[id] = m = new CritMiss { T = t, Opened = opened, SwingAt = h.LegalSince, Best = ct, Point = point };
                CritTries++;
            }
            m.Frames++;
            m.Last = now;
            m.Speed = Mathf.Max(m.Speed, h.Speed);
            if (body) m.Body = true; else m.Line = true;
            if (ct.Angle < m.Best.Angle) { m.Best = ct; m.Point = point; }
        }

        static readonly List<int> s_settled = new List<int>();

        /// The swing's tries at open crits, once it is over (no longer legal, a new swing, or 0.35 s without another try): one
        /// that landed after all is dropped; one that never followed the arrow is the wrong cut (Poise.Missed: counted, the
        /// marker flickers, the log line with the best frame's numbers), with the old feedback (the deflect sound, a tick).
        void Settle(Held h, float now)
        {
            if (h.Misses.Count == 0) return;
            s_settled.Clear();
            foreach (var kv in h.Misses)
            {
                CritMiss m = kv.Value;
                bool sameSwing = h.LegalSince == m.SwingAt;
                if (sameSwing && h.Swung.Contains(kv.Key)) { s_settled.Add(kv.Key); CritTriesLanded++; continue; }   // it landed after all
                if (sameSwing && h.Legal && now - m.Last <= 0.35f) continue;
                s_settled.Add(kv.Key);
                if (m.T == null) continue;
                if (Poise.Missed(m.T, m.Opened, m.Best, m.Frames, m.Body && m.Line ? "the body and the line" : m.Body ? "the body" : "the line", m.Speed))
                {
                    WrongWay++;
                    Note("wrong way " + WeaponGrips.ModelKey(m.T.name) + ": the cut's best frame " + m.Best.Angle.ToString("0") + " deg off its arrow (" + m.Frames + " frames)");
                    CombatSounds.Layer(CombatSounds.Deflect, m.Point, 0.7f);
                    CombatFeel.Tick(h.Left, 12f);
                }
            }
            for (int i = 0; i < s_settled.Count; i++) h.Misses.Remove(s_settled[i]);
        }

        void Contact(Held h, PlayerController pc, Targetable t, Vector3 point, float now)
        {
            int id = t.GetInstanceID();
            bool wasInside = h.Inside.Contains(id);
            if (!wasInside) h.EnteredAt[id] = now;
            if (!h.Legal)
            {
                // too slow: nothing but a light tick (resting the blade on an enemy does nothing)
                if (!wasInside)
                {
                    Illegal++;
                    if (now >= m_nextIllegalTick) { m_nextIllegalTick = now + 0.25f; CombatFeel.Tick(h.Left, 3f); }
                }
                return;
            }
            // a blade that rested inside a body and then started a swing: only an entry counts - unless it entered at most
            // 0.2 s before the swing turned legal (a fast swing whose blade met the body early is never rejected)
            float entered;
            WeaponSpec w = WeaponTable.Current();
            bool known = h.EnteredAt.TryGetValue(id, out entered);
            // (or the swing is clearly fast: 1.6 x MinSwing - an enemy standing so close that the blade was in it already)
            if (wasInside && !h.WasLegal && (!known || h.LegalSince - entered > 0.2f) && h.Speed < w.MinSwing * 1.6f) return;
            if (h.Swung.Contains(id)) return;
            float next;
            if (m_nextValid.TryGetValue(id, out next) && now < next) return;
            // UYF's stab pull-out (docs/uyf-parity-audit.md P11): within 0.5 s of a stab hit on this enemy, a swing whose blade
            // comes back out along the stab is the pull-out, not a hit
            StabMark sm;
            if (!h.Stab && m_stabbed.TryGetValue(id, out sm) && now - sm.At < 0.5f && Vector3.Dot(SwingDir(h, now), sm.Dir) < -0.5f) { PullOuts++; return; }
            // playtest 4 (P4-9): a staggered enemy with its crit open - a body contact that does not follow the arrow is no wrong cut
            // any more (13 of 13 wrong cuts in the user's skeleton fight were body contacts, judged on their first frame and the
            // swing then spent): it is kept as the swing's try, and a later frame of the same swing (the body or the line) that
            // follows the arrow still counts - UYF re-judges its crit every frame the blade is in it (docs/uyf-reference.md §3.9)
            float opened;
            if (!h.Stab && VRRig.I != null && Poise.CritOpen(t, out opened))
            {
                Poise.Try ct = Poise.Judge(t, SwingDir(h, now), VRRig.I.HeadWorldPos);
                if (ct.Angle > WeaponTable.CritLeeway()) { Miss(h, t, opened, ct, point, now, true); return; }
            }
            // quality: UYF's travel since the swing turned legal; a blade that met the body before then by its speed
            Land(h, pc, t, point, now, known && entered <= h.LegalSince + 0.02f, "");
        }

        /// A hit that counts (a body contact, or a cut through a crit's line): quality, the hit charge, the strike.
        void Land(Held h, PlayerController pc, Targetable t, Vector3 point, float now, bool metEarly, string how)
        {
            int id = t.GetInstanceID();
            WeaponSpec w = WeaponTable.Current();
            h.Swung.Add(id);
            if (t.IgnoreTargeting) GnomeFight.CountGroundHit();
            int q = Quality(h, w, metEarly);
            bool armoured = t.Protection != null && t.Protection.Health > 0 && t.Controller != null && t.Controller.IgnoreFlurry == 0;
            m_nextValid[id] = now + (armoured ? 0.2f : w.ReHit);
            float scale = q == 0 ? 0.3f : q == 1 ? 1f : 1.2f;
            bool charged = armoured || m_charges >= 1f;
            if (!armoured)
            {
                if (charged) m_charges -= 1f;
                else { scale *= 0.25f; Uncharged++; }
            }
            m_lastHitAt = now;
            if (h.Stab) Stabs++;
            Vector3 swingDir = SwingDir(h, now);
            if (h.Stab) m_stabbed[id] = new StabMark { At = now, Dir = swingDir };
            s_why = " v " + h.Speed.ToString("0.0") + " travel " + h.Travel.ToString("0.00") + (h.Stab ? " stab" : "") + (metEarly ? " early" : "") + how + " " + w.Name;
            Strike(pc, t, q, scale, point, swingDir, h.Left, armoured, false, h.Stab, charged);
            s_why = "";
        }
        static string s_why = "";
        static readonly HashSet<int> s_frontNoted = new HashSet<int>();

        static readonly System.Reflection.FieldInfo s_hbcHits = AccessTools.Field(typeof(ActionHitByCount), "m_currentHits"), s_hbcNext = AccessTools.Field(typeof(ActionHitByCount), "m_nextNumHits"),
            s_hbcReady = AccessTools.Field(typeof(ActionHitByCount), "m_ready"), s_hbcBecame = AccessTools.Field(typeof(ActionHitByCount), "m_becameAttacker"),
            s_hbcAllowed = AccessTools.Field(typeof(ActionHitByCount), "m_nextBlockAllowed");

        // ---- the hit-count guard's own decisions (diagnostics: `hbclog`) ----
        public static readonly List<string> HbcLog = new List<string>();
        public static int HbcProcess;
        public static bool HbcOn;   // `hbclog on`: log the hit-count guard's state changes (off by default: no per-frame work)
        public static void HbcPatch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ActionHitByCount), "SetReady"), postfix: new HarmonyMethod(typeof(BladeHits), nameof(SetReadyPost)));
                h.Patch(AccessTools.Method(typeof(ActionHitByCount), "TryToBecomeAttacker"), postfix: new HarmonyMethod(typeof(BladeHits), nameof(TryAttackPost)));
                h.Patch(AccessTools.Method(typeof(ActionHitByCount), "Process"), postfix: new HarmonyMethod(typeof(BladeHits), nameof(HbcProcessPost)));
            }
            catch (Exception e) { Plugin.L.LogWarning("BladeHits: hbc patch: " + e.Message); }
        }
        static void HbcProcessPost() { if (HbcOn) HbcProcess++; }
        static readonly Dictionary<int, string> s_hbcLast = new Dictionary<int, string>();
        static void SetReadyPost(ActionHitByCount __instance, bool a_ready)
        {
            if (!HbcOn || HbcLog.Count >= 400 || !Physical.Live) return;
            Controller c = __instance.Actor != null ? __instance.Actor.GetComponent<Controller>() : null;
            string st = "ready " + s_hbcReady.GetValue(__instance) + " became " + s_hbcBecame.GetValue(__instance) + " hits " + ((float)s_hbcHits.GetValue(__instance)).ToString("0.0") +
                        "/" + s_hbcNext.GetValue(__instance) + " blocking " + (c != null && c.Blocking);
            int id = __instance.GetInstanceID();
            string last;
            if (s_hbcLast.TryGetValue(id, out last) && last == st) return;   // only changes
            s_hbcLast[id] = st;
            HbcLog.Add(Time.unscaledTime.ToString("0.00") + " " + WeaponGrips.ModelKey(__instance.Actor != null ? __instance.Actor.name : "?") + "#" + id + " " + st);
        }
        static void TryAttackPost(ActionHitByCount __instance, bool __result)
        {
            if (HbcOn && HbcLog.Count < 300 && Physical.Live)
            {
                AIController ai = __instance.Actor != null ? __instance.Actor.GetComponent<AIController>() : null;
                AITarget at = ai != null && ai.Target != null ? ai.Target.GetComponent<AITarget>() : null;
                HbcLog.Add(Time.unscaledTime.ToString("0.00") + " TryToBecomeAttacker " + WeaponGrips.ModelKey(__instance.Actor != null ? __instance.Actor.name : "?") + " = " + __result +
                           " slot " + (at != null && at.MeleeAttacker != null ? WeaponGrips.ModelKey(at.MeleeAttacker.name) + (at.MeleeAttacker == ai ? "(self)" : "") : "none"));
            }
        }

        /// Diagnostics for enemies with the game's hit-count guard (shield bearers raise their shields after a few hits unless
        /// they hold the attack slot): its count, whether it holds the melee slot, is mid-strike, or slowed by the lead.
        static string GuardDiag(Targetable t, bool wasBlocking)
        {
            try
            {
                Actor actor = t.GetComponent<Actor>();
                ActionHitByCount hbc = actor != null ? actor.GetComponentInChildren<ActionHitByCount>(true) : null;
                if (hbc == null) return "";
                AIController ai = t.AIController;
                AITarget at = PlayerController.Instance != null ? PlayerController.Instance.GetComponent<AITarget>() : null;
                IActionAICombat strike = actor.GetRunningAction<IActionAICombat>();
                return " | guard hits " + ((float)s_hbcHits.GetValue(hbc)).ToString("0.0") + "/" + s_hbcNext.GetValue(hbc) + " ready " + s_hbcReady.GetValue(hbc) +
                       " became " + s_hbcBecame.GetValue(hbc) + " blockIn " + Mathf.Max(0f, (float)s_hbcAllowed.GetValue(hbc) - Time.time).ToString("0.0") +
                       " blocking " + wasBlocking + " slot " + (at != null && at.MeleeAttacker == ai) + " striking " + (strike != null ? strike.GetType().Name : "-") +
                       " stunned " + (ai != null && ai.IsStunned(0f)) + " " + EnemyTuning.SlowState(ai);
            }
            catch (Exception e) { return " | guard ?" + e.Message; }
        }

        // ---------------------------------------------------------------------------------------- the shield bash

        void Bash(Held h, PlayerController pc, float now)
        {
            if (now < h.NextBash || h.Hist.Count < 2) return;
            Sample cur = h.Hist[h.Hist.Count - 1], old = Ago(h, now, 0.06f);
            float span = now - old.T;
            if (span < 0.02f) return;
            Vector3 vT = (cur.Center - old.Center) / span;
            float sp = vT.magnitude;
            if (sp < 1.6f || Vector3.Dot(vT, cur.Axis) < 0.7f * sp) return;   // face-first, >= 1.6 m/s
            int c = Physics.OverlapSphereNonAlloc(h.Center + h.Normal * 0.05f, Mathf.Max(0.12f, h.Disc * 0.8f), s_cols, s_mask, QueryTriggerInteraction.Collide);
            Targetable me = pc.Targetable, best = null;
            float bd = float.MaxValue;
            for (int j = 0; j < c; j++)
            {
                Targetable t = TargetUtils.GetTargetable(s_cols[j]) ?? s_cols[j].GetComponentInParent<Targetable>();
                if (t == null || t == me || !me.CanAttack(t) || t.IgnoreTargeting || !me.IsEnemy(t)) continue;
                float d = (t.transform.position - h.Center).sqrMagnitude;
                if (d < bd) { bd = d; best = t; }
            }
            if (best == null) return;
            h.NextBash = now + 0.6f;
            bool armoured = best.Protection != null && best.Protection.Health > 0 && best.Controller != null && best.Controller.IgnoreFlurry == 0;
            m_lastHitAt = now;
            Strike(pc, best, 1, 1f, h.Center, vT, h.Left, armoured, true);
        }

        // ---------------------------------------------------------------------------------------- the game's hit

        static void EnsureScale(PlayerController pc)
        {
            if (s_scaledOn == pc) return;
            try
            {
                pc.ModifyDamageOut = (Controller.ModifyDamageDelegate)Delegate.Combine(pc.ModifyDamageOut, (Controller.ModifyDamageDelegate)ScaleOut);
                s_scaledOn = pc;
            }
            catch (Exception e) { Plugin.L.LogWarning("BladeHits: damage scale: " + e.Message); }
        }

        static void ScaleOut(ICombatEffectContainer a_container, Targetable a_target, ref float a_damage, Destroyable.DamageType a_type, bool a_blocked)
        {
            if (s_scaleFor != null && a_container == s_scaleFor) { a_damage *= s_scale; LastDamage = a_damage; }
        }

        public static bool AnyOtherEnemyAlive(PlayerController pc, Targetable except)
        {
            Targetable me = pc.Targetable;
            BetterList<Targetable> all = me.EnumerateWithinRadius(80f, me.GetEnemyTeams());
            for (int i = 0; i < all.size; i++)
            {
                Targetable t = all[i];
                if (t == null || t == except || t.Controller == null) continue;
                Destroyable d = t.GetComponent<Destroyable>();
                if (d != null && !d.IsDead && !t.Hidden) return true;
            }
            return false;
        }

        /// One accepted hit (or bash) on `t` through the game's own attack path. Stage 2 (UYF's guard, Poise): a blade hit is a
        /// guard hit while the enemy's guard is up (Damage = Crits: only the game's hit reaction - the flinch, its hit counters,
        /// armour's flurry count - and no health lost; the guard chipped), a crit when it cuts along the open crit of a
        /// staggered enemy (the game's whole attack: health, without its knockdown or stun), else nothing (the wrong way,
        /// between crits, while it recovers). Bash, riposte and finisher keep their own rules; a bash chips the guard too.
        void Strike(PlayerController pc, Targetable t, int quality, float scale, Vector3 point, Vector3 swingDir, bool left, bool armoured, bool bash, bool stab = false, bool charged = true)
        {
            Controller tc = t.Controller;
            if (tc == null || !CombatEffect.CanProcess) return;
            PlayerCombat combat = pc.Combat;
            Destroyable td = t.GetComponent<Destroyable>();
            string key = WeaponGrips.ModelKey(t.name);
            // a downed enemy: the finisher (any legal hit; LT stays the button fallback)
            if (TargetUtils.IsTargetProne(t) && !bash) { Finisher(pc, t, left); return; }
            // an evading enemy: the blade whiffs (the game's rule)
            Actor actor = t.GetComponent<Actor>();
            IActionEvade ev = actor != null ? actor.GetRunningAction<IActionEvade>() : null;
            if (ev != null && !ev.Vulnerable)
            {
                ev.ConsumeEvade();
                try { ActionMelee.OnPlayerMissed?.Invoke(pc); } catch (Exception) { }
                Whiffed++;
                Note("whiff " + key);
                CombatSounds.Layer(CombatSounds.Whoosh, point, 0.4f, 1.3f);
                return;
            }
            Poise.HitTaken(t);   // UYF's hits-taken history (blocked or not): its aggression rule raises a shield on it
            bool riposteNow = !bash && pc.Riposte && pc.RiposteTarget == t && pc.RiposteAttack != null && pc.WeaponHandedness != Handedness.TwoHanded;
            // Stage 2 item 2 (UYF §3.10): an enemy in its block state deflects the blade from the front - no chip, no damage -
            // unless the hit's unmodified guard damage passes its super armor (Husk 35, Knight 60, Captain 200, boss 100):
            // then it breaks through (a guard break). From its side or back the block doesn't cover it (the flank rule).
            if (!bash && !riposteNow && tc.Blocking && !Flank(pc, t))
            {
                Poise.StruckWhileBlocking(tc);
                WeaponSpec ws = WeaponTable.Current();
                float raw = ws.Guard * (quality == 0 ? ws.UnderG : quality == 1 ? 1f : ws.BigG);
                float armor;
                if (Poise.BreakThrough(t, raw, out armor))
                {
                    BreakThroughs++;
                    Note("BREAK THROUGH " + key + "'s block (guard damage " + raw.ToString("0.0") + " >= its super armor left " + armor.ToString("0.0") + ")" + s_why);
                    CombatSounds.Layer(CombatSounds.GuardBreak, point, 0.8f);
                    CombatFeel.Impact(left, true);
                    return;
                }
                Deflect(pc, t, point, swingDir, left, raw, armor);
                return;
            }
            // Stage 2: the enemy's guard decides what a blade hit is
            Poise.Outcome po = Poise.Outcome.Guard;
            string pwhy = "";
            if (!bash && !riposteNow) po = Poise.Blade(t, quality, stab, charged, swingDir, out pwhy);
            if (po == Poise.Outcome.Wrong || po == Poise.Outcome.Waiting)
            {
                if (po == Poise.Outcome.Wrong) WrongWay++;
                Note((po == Poise.Outcome.Wrong ? "wrong way " : "no hit ") + key + pwhy + s_why);
                CombatSounds.Layer(CombatSounds.Deflect, point, po == Poise.Outcome.Wrong ? 0.7f : 0.35f);
                CombatFeel.Tick(left, po == Poise.Outcome.Wrong ? 12f : 5f);
                return;
            }
            bool crit = po == Poise.Outcome.Crit;
            bool healthless = CombatSettings.CritsOnly && !crit && !riposteNow;   // a guard hit or a bash: no health lost
            WeaponSpec wspec = WeaponTable.Current();
            PlayerCombatAttack atk;
            bool riposte = false, flurry = false;
            bool chargedBash = false;
            if (armoured && !crit)
            {
                // armour: every hit is a flurry hit (the game's flurry chain picks its armour breaker itself)
                float now = Time.unscaledTime;
                if (m_flurryTarget != t || now - m_flurryAt > 1.5f) { m_flurryIndex = 0; combat.Flurry.SetUseInAttack(); }
                m_flurryTarget = t;
                m_flurryAt = now;
                atk = combat.Flurry.GetAttack(tc, ++m_flurryIndex, pc.transform);
                flurry = true;
            }
            else if (bash)
            {
                try
                {
                    TieredCombatEffects tb = pc.Inventory.Armour != null ? pc.Inventory.Armour.TieredBashEffects : null;
                    chargedBash = tb != null && tb.Determinant >= pc.CalculateBashMaxDeterminant(tb.MaxDeterminant);
                }
                catch (Exception) { }
                atk = chargedBash ? combat.ChargedStunAttack : combat.StunAttack;
                Poise.Bash(t, chargedBash);
            }
            else if (riposteNow)
            {
                atk = pc.RiposteAttack;
                riposte = true;
                pc.Riposte = false;
            }
            else atk = combat.GetNextAttack();
            if (atk == null) return;
            bool wasBlocking = tc.Blocking;
            // a blocking enemy hit from its side or back: its block doesn't cover that (the game's block has no direction)
            bool flank = false;
            if (!bash && tc.Blocking && Flank(pc, t)) { flank = true; tc.Blocking = false; Flanks++; }
            if (!riposte && !bash) Physical.CapPush(tc, Physical.PushCap);
            Hit before = s_lastHit(pc);
            s_lastHit(pc) = default(Hit);
            bool blocked = false, killed = false;
            float healthBefore = td != null ? td.CurrentHealth : 0f;
            LastDamage = 0f;
            try
            {
                atk.Use();
                try { if (tc.Animator != null && tc.Animator.Animator != null) tc.Animator.Animator.SetBool("LastHitWasFinisher", false); } catch (Exception) { }
                if (!bash && !riposte) KillLaunch.Arm(t, swingDir, quality);   // UYF: a killing blow throws the body along the swing
                atk.Hit(t, true);
                s_scaleFor = atk;
                if (healthless && !bash)
                {
                    // a guard hit: the game's whole attack with its damage scaled to 0 and no knockdown or stun - the flinch, the
                    // hit-count guard (it counts only hits whose container is the hero's own attack: through a proxy the shield
                    // bearers stopped raising their shields), armour's flurry count
                    s_scale = 0f;
                    s_noDisable = true;
                    try { CombatEffect.Apply(atk, new CombatTarget(t), pc.transform); }
                    finally { s_noDisable = false; }
                }
                else
                {
                    s_scale = healthless ? 0f : crit ? (quality == 0 ? wspec.UnderH : 1f) : scale;
                    s_noDisable = crit;   // a crit's attack: no knockdown and no stun (the stagger holds it)
                    try { CombatEffect.Apply(atk, new CombatTarget(t), pc.transform); }
                    finally { s_noDisable = false; }
                    if (bash)
                    {
                        try
                        {
                            TieredCombatEffects tb = pc.Inventory.Armour != null ? pc.Inventory.Armour.TieredBashEffects : null;
                            if (tb != null && tb.CurrentTierIndex >= 0)
                            {
                                int ix = pc.CalculateBashTier(tb.CurrentTierIndex);
                                CombatEffect[] extra = tb.Tiers[ix].TargetCombatEffects;
                                if (extra != null) CombatEffect.Apply(new CombatEffectContainerProxy(atk, extra), new CombatTarget(t), pc.transform);
                            }
                        }
                        catch (Exception) { }
                        combat.OnBash(atk, t, tc.Blocking, chargedBash);
                    }
                }
                Hit got = s_lastHit(pc);
                blocked = got.Target == t && got.Blocked;
                if (got.Target == null) s_lastHit(pc) = before;
                if (riposte)
                {
                    CombatUtils.PlayRiposteHitEffect(tc.EffectSet, tc.Model);
                    combat.OnRiposteUsed(atk, t);
                    Guard.RiposteUsed(t);
                }
                // the weapon's own hit effect (sparks, blood, the game's hit sound) - the weapon is in the player's hand
                try
                {
                    if (bash)
                    {
                        Shield sh = pc.Inventory.Shield;
                        if (sh != null && !string.IsNullOrEmpty(atk.EffectHitEvent)) sh.EffectSet.Trigger(atk.EffectHitEvent, sh.Model);
                    }
                    else
                    {
                        Weapon w = pc.Inventory.Weapon;
                        List<Model> ms = w.AllModels;
                        Model wm = ms.Count >= 2 && left != Settings.LeftHanded ? ms[1] : ms.Count >= 1 ? ms[0] : w.Model;
                        if (!string.IsNullOrEmpty(atk.EffectHitEvent)) w.EffectSet.Trigger(atk.EffectHitEvent, wm);
                    }
                }
                catch (Exception) { }
                killed = td != null && td.IsDead;
            }
            catch (Exception e) { Plugin.L.LogWarning("BladeHits: hit " + key + ": " + e.Message); }
            finally
            {
                s_scaleFor = null;
                s_scale = 1f;
                s_noDisable = false;
                if (flank && tc != null) tc.Blocking = true;
            }
            float lost = td != null ? Mathf.Max(0f, healthBefore - td.CurrentHealth) : 0f;
            Hits++;
            m_lastComboHit = Time.unscaledTime;
            if (bash) Bashes++;
            else if (flurry) Flurries++;
            else if (riposte) Ripostes++;
            if (crit) Crits++;
            else if (!bash && !riposte) GuardHits++;
            if (lost > 0.01f) HealthHits++;
            // Stage 2 item 8: UYF's super charge (+2 a hit that takes health, +0.5 a guard hit)
            if (lost > 0.01f) SuperMeter.Add(2f, "health hit");
            else if (!blocked && !bash) SuperMeter.Add(0.5f, "guard hit");
            if (quality == 0) Under++; else if (quality == 1) Standard++; else Big++;
            string what = bash ? "bash" : flurry ? "flurry" : riposte ? "riposte" : (crit ? "CRIT " : "") + (quality == 0 ? "under" : quality == 1 ? "standard" : "big");
            Note(what + " " + key + (blocked ? " BLOCKED" : "") + (flank ? " flank" : "") + " x" + scale.ToString("0.00") + " dmg " + LastDamage.ToString("0.0") + " hp -" + lost.ToString("0.0") +
                 (killed ? " KILL" : "") + s_why + pwhy + GuardDiag(t, wasBlocking));
            // feedback (§8.5.8; Stage 2: UYF's per-quality flashes and shakes, docs/uyf-reference.md §3.12)
            if (blocked)
            {
                Blocked++;
                CombatSounds.Layer(CombatSounds.Clank, point, 0.8f);
                CombatFeel.Impact(left);
                return;
            }
            if (crit)
            {
                // a crit: 0.2 s hit-stop (Under none), a pale yellow flash, the crit shake, the crit's ring
                if (!killed && quality > 0) CombatFeel.HitStop(tc, 0.2f);
                ScreenFlash.Show(ScreenFlash.PaleYellow, quality == 0 ? 0.14f : 0.47f, quality == 0 ? 0.25f : 0.5f, "crit");
                CameraShake.Add(quality == 0 ? 0.25f : 0.6f, 0.1f, 0.025f, "crit");
                CombatFeel.Impact(left, true);
                // a crit sound per quality (UYF's F4: under / standard / big each its own): ours pitched and weighted by it
                CombatSounds.Layer(CombatSounds.Crit, point, quality == 0 ? 0.7f : quality == 1 ? 0.9f : 1f, quality == 0 ? 1.12f : quality == 1 ? 1f : 0.88f);
                if (quality > 0) CombatSounds.Layer(CombatSounds.Thump, point, quality == 2 ? 0.9f : 0.75f);
                CombatFeel.Burst(point, new Color(1f, 0.92f, 0.55f), 0.45f, 0.3f);
            }
            else if (bash || riposte || !CombatSettings.CritsOnly)
            {
                // the earlier feedback: a hit-stop by quality, a thump under big hits
                float stop = bash ? 0.1f : riposte ? 0.15f : quality == 2 ? 0.1f : quality == 1 ? 0.05f : 0f;
                if (!killed) CombatFeel.HitStop(tc, stop);
                CombatFeel.Impact(left, quality == 2 || riposte);
                if (quality == 2 || riposte || bash) CombatSounds.Layer(CombatSounds.Thump, point, quality == 2 ? 0.9f : 0.7f);
            }
            else
            {
                // a guard hit (UYF): no hit-stop; a white flash (Standard 0.27, Big 0.39, Under none) and the guard-hit shake
                CombatFeel.Impact(left, quality == 2);
                CombatSounds.Layer(CombatSounds.GuardHit, point, quality == 0 ? 0.45f : quality == 1 ? 0.75f : 0.95f, quality == 2 ? 0.9f : 1f);
            }
            if (!crit && !bash && !riposte)
            {
                if (quality > 0) ScreenFlash.Show(ScreenFlash.White, quality == 1 ? 0.27f : 0.39f, 0.25f, "guard hit");
                CameraShake.Add(quality == 0 ? 0.1f : 0.25f, quality == 0 ? 0.1f : 0.07f, 0.025f, "guard hit");
            }
            if (bash) EnemyPush.Start(tc as AIController, t.transform.position - pc.transform.position, 0.5f, 0.25f);
            // Stage 2 item 6: UYF's magnetism - a hit on the enemy you face pulls you to the weapon's distance from it
            else if (!killed) CombatMove.Magnetize(t, wspec.Magnet);
        }

        public static int Crits, GuardHits, HealthHits, WrongWay, Deflects, BreakThroughs;

        /// The hero hits `t` from more than 100 deg off its facing: its block (no direction in HoF2) doesn't cover that.
        static bool Flank(PlayerController pc, Targetable t)
        {
            Vector3 from = pc.transform.position - t.transform.position;
            from.y = 0f;
            Vector3 fwd = t.transform.forward;
            fwd.y = 0f;
            return from.sqrMagnitude > 1e-4f && Vector3.Angle(fwd, from) > 100f;
        }

        /// A blocked hit (UYF's deflect): the game's own block reaction on the enemy (the attack's hit effect only: the game
        /// marks it blocked), a clank, the deflect haptic (UYF: "my hit was blocked", 0.2 s full), the blade kicked back.
        void Deflect(PlayerController pc, Targetable t, Vector3 point, Vector3 swingDir, bool left, float raw, float armor)
        {
            Deflects++;
            Blocked++;
            Hits++;
            m_lastComboHit = Time.unscaledTime;
            try
            {
                // the game's own attack, its damage scaled to 0 (the game marks it blocked: the block reaction, the hit-count
                // guard's count)
                PlayerCombatAttack atk = pc.Combat.GetNextAttack();
                if (atk != null)
                {
                    s_scaleFor = atk;
                    s_scale = 0f;
                    s_noDisable = true;
                    CombatEffect.Apply(atk, new CombatTarget(t), pc.transform);
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("BladeHits: deflect: " + e.Message); }
            finally { s_scaleFor = null; s_scale = 1f; s_noDisable = false; }
            Note("DEFLECTED by " + WeaponGrips.ModelKey(t.name) + "'s block (guard damage " + raw.ToString("0.0") + " < its super armor left " + armor.ToString("0.0") + ")" + s_why +
                 GuardDiag(t, true));
            CombatSounds.Layer(CombatSounds.Clank, point, 0.9f);
            CombatSounds.Layer(CombatSounds.Deflect, point, 0.6f, 0.8f);
            CombatFeel.Impact(left);
            CombatFeel.Burst(point, new Color(1f, 0.85f, 0.55f), 0.18f, 0.15f);
            BodyLean.Struck(t, point);   // UYF's block leans toward the struck point (item 18)
            KickBlade(left, -swingDir);
        }

        // ---- the blade kicked back by a deflect: the held model turns up to 12 deg about the grip, out and back in 0.16 s
        sealed class Kick { public Transform T; public Quaternion Base; public Vector3 Axis; public float Start; }
        readonly List<Kick> m_kicks = new List<Kick>();

        void KickBlade(bool left, Vector3 away)
        {
            // WeaponPhysics: the weapon's follow takes UYF's knock (an impulse back along the swing, its springs loosened 0.5 s)
            if (WeaponFeel.Drives(left) && VRRig.I != null)
            {
                WeaponFeel.Impact(left, VRRig.I.FromWorldDir(away), 5f);
                Kicks++;
                return;
            }
            try
            {
                PlayerController pc = PlayerController.Instance;
                CombatVR vr = CombatVR.I;
                if (pc == null || vr == null || pc.Inventory.Weapon == null) return;
                Transform hand = left ? vr.HandL : vr.HandR;
                foreach (Model m in pc.Inventory.Weapon.AllModels)
                {
                    if (m == null || m.transform.parent != hand) continue;
                    Transform t = m.transform;
                    foreach (Kick k in m_kicks) if (k.T == t) return;   // one kick at a time
                    // turn the blade back along the way it came: about the axis square to the blade and the motion
                    Vector3 blade = t.TransformDirection(Vector3.forward);
                    Vector3 axisW = Vector3.Cross(blade, away);
                    if (axisW.sqrMagnitude < 1e-6f) axisW = t.TransformDirection(Vector3.right);
                    Vector3 axisL = t.parent.InverseTransformDirection(axisW.normalized);
                    m_kicks.Add(new Kick { T = t, Base = t.localRotation, Axis = axisL, Start = Time.unscaledTime });
                    Kicks++;
                    return;
                }
            }
            catch (Exception) { }
        }

        public static int Kicks;
        bool Kicking(Transform t)
        {
            for (int i = 0; i < m_kicks.Count; i++) if (m_kicks[i].T == t) return true;
            return false;
        }

        void RunKicks()
        {
            float now = Time.unscaledTime;
            for (int i = m_kicks.Count - 1; i >= 0; i--)
            {
                Kick k = m_kicks[i];
                if (k.T == null) { m_kicks.RemoveAt(i); continue; }
                float x = (now - k.Start) / 0.16f;
                if (x >= 1f) { k.T.localRotation = k.Base; m_kicks.RemoveAt(i); continue; }
                k.T.localRotation = Quaternion.AngleAxis(12f * Mathf.Sin(Mathf.PI * x) * (1f - 0.3f * x), k.Axis) * k.Base;
            }
        }

        // a crit's attack: the game's knockdown and stun effects are skipped (Poise's stagger holds the enemy)
        static bool s_noDisable;
        static bool SkipDisable() => !s_noDisable;
        public static void DisablePatch(Harmony h)
        {
            try
            {
                var skip = new HarmonyMethod(typeof(BladeHits), nameof(SkipDisable));
                h.Patch(AccessTools.Method(typeof(CombatEffectKnockdown), "Apply", new[] { typeof(CombatEffect.ApplyContext) }), prefix: skip);
                h.Patch(AccessTools.Method(typeof(CombatEffectStun), "Apply", new[] { typeof(CombatEffect.ApplyContext) }), prefix: skip);
            }
            catch (Exception e) { Plugin.L.LogWarning("BladeHits: crit knockdown patch: " + e.Message); }
        }

        /// The last kill of a fight (CombatFeel's death hook, every way of killing): a final thump, both hands, 0.3x for 0.5 s.
        public static void LastKill(Vector3 point)
        {
            LastKills++;
            CombatSounds.Layer(CombatSounds.Thump, point, 1f, 0.8f);
            CombatFeel.ImpactBoth();
            CombatFeel.SlowMo(0.3f, 0.5f, "last kill");
        }

        void Finisher(PlayerController pc, Targetable t, bool left)
        {
            Controller tc = t.Controller;
            AIController ai = t.AIController;
            if (tc == null || ai == null || !tc.Prone.HasValue) return;
            try
            {
                PlayerCombatProne prone = pc.Combat.Prone;
                prone.Use();
                Disabled d = tc.Prone.Value;
                tc.Prone = new Disabled { Time = d.Time, Duration = d.Duration, Locked = true };
                FinisherAttack fa = prone.GetFinisherAttack(pc.Targetable, t, tc.FinisherData);
                if (fa.attack == null) return;
                tc.FinisherHideRenderersTime = fa.targetHideRenderersTime;
                Animator an = tc.Animator != null ? tc.Animator.Animator : null;
                if (an != null)
                {
                    an.SetBool("LastHitWasFinisher", true);
                    an.SetFloat("FinisherDirection", (float)fa.direction);
                    an.SetFloat("FinisherVariant", fa.variant);
                }
                CombatEffect.Apply(fa.attack, new CombatTarget(t), pc.transform);
                fa.attack.Hit(t);
                ai.Prone = null;
                Weapon w = pc.Inventory.Weapon;
                if (w != null && !string.IsNullOrEmpty(fa.attack.EffectHitEvent)) w.EffectSet.Trigger(fa.attack.EffectHitEvent, w.AllModels.Count > 0 ? w.AllModels[0] : w.Model);
                TimeDilation td = fa.attack.GetComponent<TimeDilation>();
                if (td != null) { CombatFeel.Earned("finisher", 2f); td.Process(); }
                Finishers++;
                Hits++;
                m_lastComboHit = Time.unscaledTime;
                Note("finisher " + WeaponGrips.ModelKey(t.name));
                CombatFeel.ImpactBoth(true);
                CombatSounds.Layer(CombatSounds.Thump, t.transform.position + Vector3.up, 1f, 0.9f);
            }
            catch (Exception e) { Plugin.L.LogWarning("BladeHits: finisher: " + e.Message); }
        }

        // ---------------------------------------------------------------------------------------- test seam

        /// `bladetest <enemy|nearest> slash|big|under|stab|slow|bash`: one synthetic hit of that kind on that enemy through the
        /// same path as a real swing (the scripted xrsim swings test the real path; this one checks the outcomes).
        public static string Test(string who, string kind)
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null || I == null) return "no hero";
            Targetable t = Physical.FindEnemy(who);
            if (t == null) return "no enemy '" + who + "'";
            bool left = Settings.LeftHanded;
            Vector3 p = t.AimTransform.position;
            bool armoured = t.Protection != null && t.Protection.Health > 0 && t.Controller != null && t.Controller.IgnoreFlurry == 0;
            int before = Hits;
            switch (kind)
            {
                case "slow": Illegal++; return "slow: no hit (illegal " + Illegal + ")";
                case "under": I.Strike(pc, t, 0, 0.3f, p, Vector3.right, left, armoured, false); break;
                case "big": I.Strike(pc, t, 2, 1.2f, p, Vector3.right, left, armoured, false); break;
                // a big cut square to its open crit's line, in the plane facing the player (the wrong-way checks: a real square
                // cut needs a start beside the body that a scripted hand can't reach without sweeping through it)
                case "square":
                {
                    Vector3 ax, dir = Vector3.right;
                    if (Poise.OpenAxis(t, out ax) && VRRig.I != null)
                    {
                        Vector3 view = p - VRRig.I.HeadWorldPos;
                        Vector3 c = Vector3.Cross(ax, view);
                        if (c.sqrMagnitude > 1e-6f) dir = c.normalized;
                    }
                    I.Strike(pc, t, 2, 1.2f, p, dir, left, armoured, false);
                    break;
                }
                // a big cut along its open crit's line (else across, as `big`): health through crits at EnemyHealth 1.0
                case "along":
                {
                    Vector3 ax, dir = Vector3.right;
                    if (Poise.OpenAxis(t, out ax)) dir = ax.normalized;
                    I.Strike(pc, t, 2, 1.2f, p, dir, left, armoured, false);
                    break;
                }
                // playtest 3 (mark 22): a big cut against its open crit's arrow (the line's other way): wrong since 2026-10-18
                case "against":
                {
                    Vector3 ax, dir = Vector3.left;
                    if (Poise.OpenAxis(t, out ax)) dir = -ax.normalized;
                    I.Strike(pc, t, 2, 1.2f, p, dir, left, armoured, false);
                    break;
                }
                case "bash": I.Strike(pc, t, 1, 1f, p, Vector3.forward, !left, armoured, true); break;
                case "finisher": I.Finisher(pc, t, left); break;   // a downed enemy (Controller.Prone set), whatever its animation
                default: I.Strike(pc, t, 1, 1f, p, Vector3.right, left, armoured, false); break;
            }
            return (Hits > before ? "hit: " : "no hit: ") + Last;
        }
    }
}

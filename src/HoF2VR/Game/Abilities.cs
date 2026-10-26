using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace HoF2VR
{
    /// Physical mode's abilities (docs/combat-uyf-design.md §8.5.9; decisions.md "Area/targeted abilities"): ONE scheme for
    /// every weapon ability and artefact - hold the grip of that hand, point, let go.
    ///  * Hold: the weapon hand's grip = the weapon ability, the shield hand's grip = the artefact. If the game lets it be
    ///    used now (Ability.CanUse: cooldown, charges, the weapon's hit counter, cost; not disabled by a curse) a glowing charge
    ///    sits in the hand; if not, the hand's "not now" (two knocks).
    ///  * Point: a short arc from the hand, like a teleport pointer, ends in a marker on the ground:
    ///    - areas (bombs, volleys...): a ring of the ability's radius; it snaps to the best group of enemies within 2 m of the
    ///      arc's end (an assist you can see);
    ///    - single targets (thrown weapons) and melee abilities: a ring under the enemy nearest the arc's end (within 2 m, or
    ///      15 deg of the pointing); no enemy, no use;
    ///    - directions (aimed shots): a straight ray from the hand;
    ///    - ranged abilities: the enemy nearest the pointing ray (the flat game's stick rule), else straight ahead;
    ///    - self abilities: a ring at your feet (no aim needed).
    ///  * Let go: the ability is used at the marker in the flat action's order (Ability.Begin, CombatEffect.Apply, Hit,
    ///    Complete), so charges, cooldowns, curses and cards behave as in the game. Thrown things leave the hand: the game's
    ///    own lob or projectile starts at the hand and flies its own path to the marker. The trigger cancels; letting go
    ///    without a valid marker uses nothing.
    ///  * No slow motion while aiming (the flat game freezes time while its circle is steered): instead no new enemy strike
    ///    starts while you aim, for up to 2 s.
    ///  * [Combat] Throwing = Hand: thrown area abilities (bombs) are thrown for real - no arc; where the throw would land, a
    ///    light assist toward a group, and the game's lob from the hand (a gentle release puts it back).
    ///  Classic mode keeps the game's own scheme (the grips press the ability buttons; VRGamepad).
    ///
    /// Since the player's playtest of 2026-10-17 ("Let's bind it to clicking in the stick, not gripping the grips"; holding
    /// the grip, which everyone does to hold the weapon, started "hold weapon" every time and drew a line out of the hand)
    /// the grips start nothing. A TAP of a stick click uses it at once: the weapon hand's stick = the weapon ability, the
    /// shield hand's stick = the artefact. A tap = pressed and let go within TapMax, the other stick not pressed meanwhile;
    /// (playtest 3, 2026-10-18: a hold past TapMax aims instead - the ring where the hand points, used when the stick is let
    /// go; the left stick's 0.8 s pause is gone (the left controller's menu button pauses) and the right stick's recenter hold
    /// works outside fights only; both sticks: WiVRn's window, the aim cancelled). The
    /// target, with no aiming line: the enemy (or group, for areas) nearest where you look within the ability's reach, else
    /// where the hand points (the arc's landing, as before); self abilities at your feet. The used spot flashes a ring. The
    /// pointer and its line are kept only for the test seams. Throwing = Hand has no effect now (nothing is held).
    internal class Abilities : MonoBehaviour
    {
        public static Abilities I;
        public enum Kind { Area, Single, SingleProjectile, Direction, Ranged, Melee, Self }
        public static int Holds, Refused, Used, Cancelled, Invalid, Thrown, HandStarts, Assisted, PutBack;
        public static string Last = "-";
        static readonly List<string> s_log = new List<string>();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// Arc of the pointer: launch speed (m/s) and gravity; a landing further than the ability's range is pulled back.
        public const float ArcSpeed = 9f, ArcGravity = 9.81f;
        /// How long aiming holds back new enemy strikes (s).
        public const float HoldBack = 2f;
        /// A grip let go sooner than this after it was squeezed is a squeeze, not a use: nothing happens (an accidental
        /// squeeze while swinging never fires an ability).
        public const float MinHold = 0.2f;
        /// A stick click let go within this is a tap (the use); longer is the stick's hold (pause, recenter).
        public const float TapMax = 0.45f;
        public static int Taps, TapsSpoiled, StickHolds;

        sealed class Aim
        {
            public bool WeaponSide;            // the weapon ability (weapon hand) or the artefact (shield hand)
            public bool On, Throw;             // Throw = this hold is a real throw (Throwing = Hand, an area ability)
            public Ability Ab;
            public Kind K;
            public float Range, Radius, Angle, Since;
            public bool Valid;
            public float ClickAt = -1f;        // the stick click went down (a tap in progress), -1 = none
            public bool ClickSpoiled;          // ... held too long or the other stick went in: not a tap
            public bool ByStick;               // playtest 3 (mark 11): this aim is a stick hold - the ring where the hand points, used on letting go
            public Vector3 Origin, Dir, Raw, Point, ReleaseAt;
            public Targetable Target;
            public int Covered;
            public readonly List<Vector3> Path = new List<Vector3>();
            // forced aim (test seams)
            public bool Forced;
            public Vector3 ForcedPoint;
            public Targetable ForcedTarget;
            // visuals
            public GameObject Root;
            public LineRenderer Line;
            public Transform Ring, Orb;
            public Material RingMat, OrbMat, LineMat;
        }

        readonly Aim[] m_aims = { new Aim { WeaponSide = true }, new Aim { WeaponSide = false } };
        static Aim s_use;   // the use in progress (CombatProxy.Engage moves thrown proxies to its hand)

        /// Aiming holds back new enemy strikes (Guard.HoldStrike), for at most HoldBack seconds per hold.
        public static bool HoldsStrikes
        {
            get
            {
                if (I == null) return false;
                float now = Time.unscaledTime;
                foreach (Aim a in I.m_aims) if (a.On && now - a.Since < HoldBack) return true;
                return false;
            }
        }

        public static bool Aiming => I != null && (I.m_aims[0].On || I.m_aims[1].On);

        public static void Apply(Harmony h)
        {
            try { h.Patch(AccessTools.Method(typeof(CombatProxy), "Engage"), prefix: new HarmonyMethod(typeof(Abilities), nameof(EngagePrefix))); }
            catch (Exception e) { Plugin.L.LogWarning("Abilities: proxy patch failed: " + e.Message); }
        }

        void Awake() { I = this; }

        static void Note(string s)
        {
            Last = s;
            if (s_log.Count < 200) s_log.Add(Time.unscaledTime.ToString("0.00", Inv) + " " + s);
            if (s_log.Count <= 80) Plugin.L.LogInfo("Abilities: " + s);
        }

        public static string Describe() =>
            "taps " + Taps + " tapsSpoiled " + TapsSpoiled + " stickHolds " + StickHolds + " hudIcon " + SuperMeter.HudIconState + " shows " + SuperMeter.HudIconShows + " holds " + Holds + " refused " + Refused + " used " + Used + " cancelled " + Cancelled + " invalid " + Invalid + " thrown " + Thrown + " putBack " + PutBack +
            " handStarts " + HandStarts + " assisted " + Assisted + " last " + Last;

        public static void ResetCounts()
        {
            Holds = Refused = Used = Cancelled = Invalid = Thrown = HandStarts = Assisted = PutBack = Taps = TapsSpoiled = StickHolds = 0;
            s_log.Clear();
            Last = "-";
        }

        // ---------------------------------------------------------------------------------------- the ability of a hand

        static Hand HandOf(bool weaponSide) => weaponSide != Settings.LeftHanded ? VRInput.R : VRInput.L;

        static Ability AbilityOf(PlayerController pc, bool weaponSide)
        {
            if (pc == null || pc.Inventory == null) return null;
            if (weaponSide) return pc.Inventory.Weapon != null && pc.DisableWeaponAbility == 0 ? pc.Inventory.Weapon.Ability : null;
            return pc.Inventory.Artifact != null && pc.DisableArtifactAbility == 0 ? pc.Inventory.Artifact.Ability : null;
        }

        static Kind KindOf(Ability ab, out float range, out float radius, out float angle)
        {
            range = 8f;
            radius = 0f;
            angle = 25f;
            AbilityThrown t = ab as AbilityThrown;
            if (t != null)
            {
                range = t.Range;
                radius = t.Radius;
                switch (t.Mode)
                {
                    case AbilityThrown.TargetMode.Single: return Kind.Single;
                    case AbilityThrown.TargetMode.SingleProjectile: return Kind.SingleProjectile;
                    case AbilityThrown.TargetMode.Direction: return Kind.Direction;
                    default: return Kind.Area;
                }
            }
            AbilityRanged r = ab as AbilityRanged;
            if (r != null) { range = r.Range; angle = Mathf.Min(r.Angle, 25f); return Kind.Ranged; }
            AbilityMelee m = ab as AbilityMelee;
            if (m != null)
            {
                // the flat hero lunges at its target first: reach it as the homing dash does (at most 6 m)
                PlayerCombatAttack atk = m.Attack;
                range = Mathf.Clamp(atk != null ? atk.Range + 1f : 4f, 3f, 6f);
                angle = 25f;
                return Kind.Melee;
            }
            return Kind.Self;
        }

        /// Why the ability can't be used now (null = it can).
        static string WhyNot(PlayerController pc, bool weaponSide, Ability ab)
        {
            if (pc == null) return "no hero";
            if (ab == null)
            {
                bool has = weaponSide ? pc.Inventory.Weapon != null && pc.Inventory.Weapon.Ability != null : pc.Inventory.Artifact != null && pc.Inventory.Artifact.Ability != null;
                return has ? "disabled" : weaponSide ? "the weapon has no ability" : "no artefact";
            }
            if (ab.IsCoolingDown) return "cooling down";
            if (!ab.AreChargesRemaining) return "no charges left";
            if (!ab.CanAfford) return "can't afford it";
            if (!ab.IsComboValid)
                return ab.AttackHitsRequired > 0 && Physical.Live ? "not charged yet (the super meter " + SuperMeter.Charge.ToString("0") + " of 100)"
                     : "not charged yet (hits " + ab.CurrentAttackHitCounter + " of " + ab.NextAttackHitCounterTarget + ")";
            try { if (pc.Targetable.Destroyable != null && pc.Targetable.Destroyable.IsDead) return "dead"; } catch (Exception) { }
            return null;
        }

        public static string Name(Ability ab)
        {
            if (ab == null) return "-";
            try { EquipmentCard c = ab.Card; if (c != null && c.CardData != null) return c.CardData.Id; } catch (Exception) { }
            return WeaponGrips.ModelKey(ab.name);
        }

        // ---------------------------------------------------------------------------------------- every frame

        void LateUpdate()
        {
            PlayerController pc = PlayerController.Instance;
            bool live = Physical.Live && pc != null && VRRig.I != null;
            for (int i = 0; i < 2; i++)
            {
                Aim a = m_aims[i];
                if (!live) { if (a.On) End(a); Hide(a); a.ClickAt = -1f; continue; }
                Hand h = HandOf(a.WeaponSide), other = HandOf(!a.WeaponSide);
                // the stick click's tap (never the grip: holding the weapon must never start an ability)
                float now = Time.unscaledTime;
                if (h.StickDown) { a.ClickAt = now; a.ClickSpoiled = other.StickClick; }
                if (a.ClickAt >= 0f && h.StickClick && other.StickClick) a.ClickSpoiled = true;   // both sticks: WiVRn's own window
                // playtest 3 (mark 11, the user's answer): held past TapMax the stick aims - the ring where the hand points (the
                // pointer's landing, as the grip's old hold), used when the stick is let go; a quick tap keeps the auto-target
                if (a.ClickAt >= 0f && h.StickClick && !a.ClickSpoiled && !a.On && now - a.ClickAt > TapMax)
                {
                    a.ClickAt = -1f;
                    Begin(a, pc, "stick hold");
                    if (a.On) { a.ByStick = true; a.Throw = false; StickHolds++; }
                }
                if (a.ClickAt >= 0f && !h.StickClick)
                {
                    bool tap = !a.ClickSpoiled && now - a.ClickAt <= TapMax;
                    a.ClickAt = -1f;
                    if (!tap) TapsSpoiled++;
                    else if (!a.On) Tap(a, pc);
                }
                if (!a.On) { Hide(a); continue; }
                if (a.ByStick && other.StickClick)
                {
                    Cancelled++;
                    Note("stick aim cancelled: both sticks pressed (WiVRn's window)");
                    End(a);
                    continue;
                }
                if (h.TriggerDown)
                {
                    Cancelled++;
                    Note("cancelled with the trigger: " + Name(a.Ab));
                    PressFeel.Refuse(h, HandWorld.Palm(h));
                    End(a);
                    continue;
                }
                Track(a, pc);
                Draw(a);
                if (a.ByStick ? !h.StickClick : !h.GripBtn) Release(a, pc);
            }
            // the button's glyph beside the game's weapon HUD while the weapon ability is ready (every frame, not only while
            // the glove's back is drawn: it stayed on after a use)
            SuperMeter.HudIcon(live && Ready(true));
            Flash();
        }

        /// A stick tap: the ability used at once at what you look at (else where the hand points); nothing held, no line.
        void Tap(Aim a, PlayerController pc)
        {
            Taps++;
            Begin(a, pc, "tap");
            if (!a.On) return;
            a.Throw = false;
            Track(a, pc);
            ViewAim(a, pc);
            Hand h = HandOf(a.WeaponSide);
            a.ReleaseAt = HandWorld.Valid(h) ? HandWorld.Palm(h) : a.Origin;
            if (!a.Valid)
            {
                Invalid++;
                Note("tap " + (a.WeaponSide ? "weapon" : "artefact") + " " + Name(a.Ab) + " " + a.K + ": no enemy in reach where you look or point - nothing used");
                PressFeel.Refuse(h, a.ReleaseAt);
                End(a);
                return;
            }
            Use(a, pc);
            End(a);
        }

        /// The tap's target: the enemy nearest the view's direction (within the ability's reach and 60 deg of the view; areas:
        /// the best group round it) - kept over the hand's pointing when found.
        void ViewAim(Aim a, PlayerController pc)
        {
            if (a.K == Kind.Self || a.K == Kind.Direction || VRRig.I == null) return;
            Vector3 hero = pc.transform.position, look = Flat(VRRig.I.HeadWorldRot * Vector3.forward);
            if (look == Vector3.zero) return;
            Targetable best = null;
            float bestA = 60f;
            foreach (Targetable t in Enemies(pc, hero, a.Range))
            {
                Vector3 o = Flat(t.transform.position - hero);
                float ang = o == Vector3.zero ? 0f : Vector3.Angle(o, look);
                if (ang < bestA) { bestA = ang; best = t; }
            }
            if (best == null) return;
            if (a.K == Kind.Area)
            {
                Vector3 p = best.transform.position;
                if (!OnGround(pc, ref p)) return;
                Vector3 g = AreaAssist(pc, p, a.Radius, out a.Covered);
                if (g != p) OnGround(pc, ref g);
                a.Point = g;
                a.Dir = Flat(g - hero);
                a.Valid = true;
                return;
            }
            if ((a.K == Kind.Single || a.K == Kind.SingleProjectile) && !pc.Targetable.CanAttack(best)) return;
            a.Target = best;
            a.Point = best.transform.position;
            a.Dir = Flat(a.Point - hero);
            a.Valid = true;
        }

        /// Whether that side's ability could be used now (the glove's button icon).
        public static bool Ready(bool weaponSide)
        {
            if (!Physical.Live) return false;
            PlayerController pc = PlayerController.Instance;
            if (pc == null) return false;
            // (no reason string a frame: the audit, item 8 - the same tests as WhyNot)
            try
            {
                Ability ab = AbilityOf(pc, weaponSide);
                if (ab == null || ab.IsCoolingDown || !ab.AreChargesRemaining || !ab.CanAfford || !ab.IsComboValid) return false;
                return pc.Targetable.Destroyable == null || !pc.Targetable.Destroyable.IsDead;
            }
            catch (Exception) { return false; }
        }

        void Begin(Aim a, PlayerController pc, string how = "hold")
        {
            Ability ab = AbilityOf(pc, a.WeaponSide);
            Hand h = HandOf(a.WeaponSide);
            string why = WhyNot(pc, a.WeaponSide, ab);
            if (why != null)
            {
                Refused++;
                Note(how + " " + (a.WeaponSide ? "weapon" : "artefact") + " refused: " + why + " (" + Name(ab) + ")");
                PressFeel.Refuse(h, HandWorld.Palm(h));
                return;
            }
            a.On = true;
            a.Ab = ab;
            a.K = KindOf(ab, out a.Range, out a.Radius, out a.Angle);
            a.Since = Time.unscaledTime;
            a.Throw = a.K == Kind.Area && CombatSettings.Throwing == CombatSettings.ThrowMode.Hand;
            a.Valid = false;
            a.Target = null;
            Holds++;
            Haptics.Cue(Haptics.CueKind.Grab, h.IsLeft);
            CombatSounds.Layer(CombatSounds.Chime, HandWorld.Palm(h), 0.22f, 1.25f);
            Note(how + " " + (a.WeaponSide ? "weapon" : "artefact") + " " + Name(ab) + " " + a.K + (a.Throw ? " (throw by hand)" : "") + " range " + a.Range.ToString("0.0", Inv) +
                 (a.Radius > 0f ? " radius " + a.Radius.ToString("0.0", Inv) : ""));
        }

        void End(Aim a)
        {
            a.On = false;
            a.ByStick = false;
            a.Forced = false;
            a.Ab = null;
            a.Target = null;
            Hide(a);
        }

        // ---------------------------------------------------------------------------------------- pointing

        static int s_groundMask = -1;
        /// Default, Ground and Obstacle (what the game's lobs land on), worked out on first use (not in a constructor).
        static int GroundMask => s_groundMask >= 0 ? s_groundMask : (s_groundMask = (1 << 0) | LayerBit("Ground") | LayerBit("Obstacle"));
        static int LayerBit(string n) { int l = LayerMask.NameToLayer(n); return l >= 0 ? 1 << l : 0; }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.zero; }

        /// Where the pointer's arc from `o` along `d` meets the ground (world), with the path for drawing. False = no ground.
        static bool ArcLanding(Vector3 o, Vector3 d, List<Vector3> path, out Vector3 hitP)
        {
            path.Clear();
            Vector3 p = o, v = d.normalized * ArcSpeed;
            path.Add(p);
            const float dt = 0.03f;
            for (int i = 0; i < 100; i++)
            {
                Vector3 n = p + v * dt + 0.5f * Vector3.down * ArcGravity * dt * dt;
                v += Vector3.down * ArcGravity * dt;
                RaycastHit hit;
                Vector3 seg = n - p;
                if (Physics.Raycast(p, seg.normalized, out hit, seg.magnitude, GroundMask, QueryTriggerInteraction.Ignore))
                {
                    hitP = hit.point;
                    path.Add(hitP);
                    return true;
                }
                p = n;
                path.Add(p);
                if (p.y < o.y - 30f) break;
            }
            hitP = p;
            return false;
        }

        /// The ground under (or near) p, on the hero's NavMesh. False = no walkable ground within 2 m.
        static bool OnGround(PlayerController pc, ref Vector3 p)
        {
            RaycastHit hit;
            if (Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out hit, 6f, GroundMask, QueryTriggerInteraction.Ignore)) p = hit.point;
            int mask = NavMesh.AllAreas;
            try { NavMeshAgent ag = pc.GetComponent<NavMeshAgent>(); if (ag != null) mask = ag.areaMask; } catch (Exception) { }
            NavMeshHit nh;
            if (!NavMesh.SamplePosition(p, out nh, 2f, mask)) return false;
            p = nh.position;
            return true;
        }

        static readonly List<Targetable> s_enemies = new List<Targetable>();

        static List<Targetable> Enemies(PlayerController pc, Vector3 around, float radius)
        {
            s_enemies.Clear();
            Targetable me = pc.Targetable;
            BetterList<Targetable> all = me.EnumerateWithinRadius(80f, me.GetEnemyTeams());
            for (int i = 0; i < all.size; i++)
            {
                Targetable t = all[i];
                if (t == null || t.IgnoreTargeting) continue;
                Destroyable d = t.GetComponent<Destroyable>();
                if (d == null || d.IsDead) continue;
                Vector3 f = t.transform.position - around;
                f.y = 0f;
                if (f.magnitude - (t.UseRadiusInCombat ? t.Radius : 0f) <= radius) s_enemies.Add(t);
            }
            return s_enemies;
        }

        static int Count(PlayerController pc, Vector3 c, float radius)
        {
            int n = 0;
            foreach (Targetable t in Enemies(pc, c, radius)) if (t != null) n++;
            return n;
        }

        static readonly List<Vector3> s_pts = new List<Vector3>();

        /// The area's assist: the best group of enemies within 2 m of the raw point (enemies and midpoints of pairs); the raw
        /// point when none is better.
        static Vector3 AreaAssist(PlayerController pc, Vector3 raw, float radius, out int covered)
        {
            float r = Mathf.Max(radius, 0.75f);
            covered = Count(pc, raw, r);
            List<Vector3> pts = s_pts;
            pts.Clear();
            foreach (Targetable t in Enemies(pc, raw, 2f + r)) pts.Add(t.transform.position);
            int n0 = pts.Count;
            for (int i = 0; i < n0; i++)
                for (int j = i + 1; j < n0; j++)
                    if ((pts[i] - pts[j]).magnitude <= 2f * r) pts.Add((pts[i] + pts[j]) * 0.5f);
            Vector3 best = raw;
            float bestD = 0f;
            foreach (Vector3 c in pts)
            {
                Vector3 off = c - raw;
                off.y = 0f;
                float d = off.magnitude;
                if (d > 2f) continue;
                int n = Count(pc, c, r);
                if (n > covered || (n == covered && n > 0 && best != raw && d < bestD)) { covered = n; best = c; bestD = d; }
            }
            return best;
        }

        /// The enemy for a single-target ability: the one nearest the arc's end (within 2 m), else the one nearest the
        /// pointing direction within 15 deg (and the range).
        static Targetable SingleTarget(PlayerController pc, Vector3 end, Vector3 dir, float range, bool projectile, bool byDirection = true)
        {
            Targetable t = null;
            try { t = TargetUtils.GetClosestAliveTarget(end, 2f, TeamType.Enemy, projectile); } catch (Exception) { }
            if (t != null && (t.IgnoreTargeting || !pc.Targetable.CanAttack(t))) t = null;
            if (t == null && byDirection && dir != Vector3.zero)
                try { t = TargetUtils.FindTarget(pc.Targetable, dir, range, 15f, false); } catch (Exception) { }
            return t;
        }

        void Track(Aim a, PlayerController pc)
        {
            Hand h = HandOf(a.WeaponSide);
            Ray ray = HandWorld.PointRay(h);
            a.Origin = ray.origin;
            a.Dir = Flat(ray.direction);
            if (a.Dir == Vector3.zero) a.Dir = Flat(VRRig.I.HeadWorldRot * Vector3.forward);
            Vector3 hero = pc.transform.position;
            if (a.Forced)
            {
                // a test aim: pointed straight at the forced point
                Vector3 fp = a.ForcedTarget != null ? a.ForcedTarget.transform.position : a.ForcedPoint;
                Vector3 fd = Flat(fp - hero);
                if (fd != Vector3.zero) a.Dir = fd;
            }
            a.Path.Clear();
            a.Valid = false;
            a.Target = null;
            a.Covered = 0;
            if (a.Throw && !a.Forced) return;   // a real throw: no pointer (the throw is the aim)
            switch (a.K)
            {
                case Kind.Self:
                    a.Point = hero;
                    a.Valid = true;
                    return;
                case Kind.Direction:
                    a.Point = hero + a.Dir * a.Range;
                    a.Raw = a.Point;
                    a.Valid = true;
                    return;
                case Kind.Ranged:
                case Kind.Melee:
                    if (a.Forced) a.Target = a.ForcedTarget;
                    else try { a.Target = TargetUtils.FindTarget(pc.Targetable, a.Dir, a.Range, a.Angle, false); } catch (Exception) { a.Target = null; }
                    a.Point = a.Target != null ? a.Target.transform.position : hero + a.Dir * Mathf.Min(a.Range, 8f);
                    // a ranged ability with no enemy fires straight ahead (the flat game does); a melee one needs its enemy
                    a.Valid = a.Target != null || a.K == Kind.Ranged;
                    return;
            }
            // the arc (areas and single targets)
            Vector3 end;
            if (a.Forced)
            {
                end = a.ForcedTarget != null ? a.ForcedTarget.transform.position : a.ForcedPoint;
                a.Path.Add(a.Origin);
                a.Path.Add(end);
            }
            else if (!ArcLanding(a.Origin, ray.direction, a.Path, out end))
            {
                a.Raw = end;
                return;
            }
            a.Raw = end;
            // not further than the ability reaches
            Vector3 off = end - hero;
            off.y = 0f;
            if (off.magnitude > a.Range) end = hero + off.normalized * a.Range + Vector3.up * (end.y - hero.y);
            if (a.K == Kind.Area)
            {
                if (!OnGround(pc, ref end)) return;
                Vector3 p = AreaAssist(pc, end, a.Radius, out a.Covered);
                if (p != end) OnGround(pc, ref p);
                a.Point = p;
                a.Valid = true;
                return;
            }
            // single targets
            a.Target = a.Forced && a.ForcedTarget != null ? a.ForcedTarget : SingleTarget(pc, end, a.Dir, a.Range, a.K == Kind.SingleProjectile, !a.Forced);
            a.Point = a.Target != null ? a.Target.transform.position : end;
            a.Valid = a.Target != null;
        }

        // ---------------------------------------------------------------------------------------- letting go

        void Release(Aim a, PlayerController pc)
        {
            Hand h = HandOf(a.WeaponSide);
            a.ReleaseAt = HandWorld.Valid(h) ? HandWorld.Palm(h) : a.Origin;
            if (!a.Forced && !a.ByStick && Time.unscaledTime - a.Since < MinHold)
            {
                PutBack++;
                Note("let go after " + (Time.unscaledTime - a.Since).ToString("0.00", Inv) + " s: a squeeze, nothing used");
                End(a);
                return;
            }
            if (a.Throw && !a.Forced)
            {
                if (!ThrowAim(a, pc, h)) { End(a); return; }
            }
            if (!a.Valid)
            {
                Invalid++;
                Note("let go without a valid marker (" + a.K + "): nothing used");
                PressFeel.Refuse(h, a.ReleaseAt);
                End(a);
                return;
            }
            Use(a, pc);
            End(a);
        }

        /// A real throw (Throwing = Hand): the landing from the palm's throw velocity, a light assist toward a group, then
        /// the game's lob from the hand. A gentle release puts it back.
        bool ThrowAim(Aim a, PlayerController pc, Hand h)
        {
            Vector3 v = HandWorld.ThrowVel(h);
            float sp = v.magnitude;
            if (sp < 1.5f)
            {
                PutBack++;
                Note("put back (release " + sp.ToString("0.0", Inv) + " m/s): nothing used");
                Haptics.Cue(Haptics.CueKind.LetGo, h.IsLeft);
                return false;
            }
            // the boost (HoF2's 20 m needs more than a comfortable VR throw): x1 below 2 m/s, easing up to x1.5 at 6 m/s
            v *= Mathf.Lerp(1f, 1.5f, Mathf.InverseLerp(2f, 6f, sp));
            Vector3 p = a.ReleaseAt, land = p;
            bool hit = false;
            const float dt = 0.02f;
            for (int i = 0; i < 200 && !hit; i++)
            {
                Vector3 n = p + v * dt + 0.5f * Physics.gravity * dt * dt;
                v += Physics.gravity * dt;
                RaycastHit rh;
                Vector3 seg = n - p;
                if (seg.sqrMagnitude > 1e-8f && Physics.Raycast(p, seg.normalized, out rh, seg.magnitude, GroundMask, QueryTriggerInteraction.Ignore)) { land = rh.point; hit = true; }
                p = n;
            }
            if (!hit) land = p;
            Vector3 hero = pc.transform.position;
            Vector3 off = land - hero;
            off.y = 0f;
            float dist = off.magnitude;
            if (dist > a.Range) { land = hero + off.normalized * a.Range; dist = a.Range; }
            a.Raw = land;
            a.Valid = false;
            if (!OnGround(pc, ref land)) { Note("the throw lands off the ground"); return true; }
            // the light assist: a group within 12 deg of the throw's direction and +-25 % of its distance, if it catches more
            int at = Count(pc, land, Mathf.Max(a.Radius, 0.75f));
            Vector3 best = land;
            int bestN = at;
            Vector3 yaw = Flat(off);
            foreach (Targetable t in new List<Targetable>(Enemies(pc, hero, a.Range + 2f)))
            {
                Vector3 c = t.transform.position, o = c - hero;
                o.y = 0f;
                if (yaw == Vector3.zero || Vector3.Angle(o, yaw) > 12f || Mathf.Abs(o.magnitude - dist) > 0.25f * Mathf.Max(dist, 2f)) continue;
                int n = Count(pc, c, Mathf.Max(a.Radius, 0.75f));
                if (n > bestN) { bestN = n; best = c; }
            }
            if (best != land) { Assisted++; OnGround(pc, ref best); }
            a.Point = best;
            a.Covered = bestN;
            a.Valid = true;
            Thrown++;
            Note("thrown at " + sp.ToString("0.0", Inv) + " m/s: lands " + dist.ToString("0.0", Inv) + " m away" + (best != land ? ", assisted to a group of " + bestN : ""));
            return true;
        }

        /// The flat action's order (ActionAbility.Begin/OnHit/Complete; ActionMelee in ability mode for melee abilities).
        void Use(Aim a, PlayerController pc)
        {
            Ability ab = a.Ab;
            Hand h = HandOf(a.WeaponSide);
            string why = WhyNot(pc, a.WeaponSide, ab);
            if (why != null) { Refused++; Note("let go but " + why); PressFeel.Refuse(h, a.ReleaseAt); return; }
            Targetable hero = pc.Targetable, target = a.Target, passed = null;
            CombatTarget ct;
            switch (a.K)
            {
                case Kind.Area:
                case Kind.Direction:
                    ct = new CombatTarget((Targetable)null, a.Point);
                    break;
                case Kind.Single:
                case Kind.SingleProjectile:
                {
                    Vector3 p = target.transform.position;
                    if (a.K == Kind.SingleProjectile) p.y = hero.AimTransform.position.y;
                    ct = new CombatTarget(target, p);
                    break;
                }
                case Kind.Ranged:
                    passed = target;
                    ct = target != null ? new CombatTarget(target) : new CombatTarget(hero.AimTransform.position + a.Dir * a.Range);
                    break;
                case Kind.Melee:
                    passed = target;
                    ct = new CombatTarget(target);
                    break;
                default:
                    ct = new CombatTarget(hero);
                    break;
            }
            int q0 = SafeQuantity(ab);
            // the hero faces where the ability goes while it is applied (proxies that take the hero's facing); the hero
            // faces the gaze again next frame (CombatMove)
            Transform ht = pc.transform;
            Quaternion keep = ht.rotation;
            Vector3 face = Flat(a.Point - ht.position);
            if (a.K == Kind.Direction) face = a.Dir;
            s_use = a;
            try
            {
                if (face != Vector3.zero) ht.rotation = Quaternion.LookRotation(face);
                ab.Begin(passed);
                CombatEffect.Apply(ab, ct, ht);
                ab.Hit(passed);
                ab.Complete(passed);
            }
            catch (Exception e) { Plugin.L.LogWarning("Abilities: use " + Name(ab) + ": " + e); }
            finally
            {
                s_use = null;
                ht.rotation = keep;
            }
            Used++;
            int q1 = SafeQuantity(ab);
            Note("used " + (a.WeaponSide ? "weapon" : "artefact") + " " + Name(ab) + " " + a.K + (a.Throw ? " (thrown)" : "") + " at " + Diag.V(a.Point) +
                 (target != null ? " on " + WeaponGrips.ModelKey(target.name) : "") + (a.K == Kind.Area ? " covering " + a.Covered : "") +
                 (q0 >= 0 ? " charges " + q0 + " -> " + q1 : "") + " timescale " + (Game.Instance != null ? Game.Instance.TimeScale.ToString("0.00", Inv) : "-"));
            Haptics.Cue(Haptics.CueKind.LetGo, h.IsLeft);
            CombatSounds.Layer(CombatSounds.Whoosh, a.ReleaseAt, 0.7f, 1.1f);
            if (a.K != Kind.Self && a.K != Kind.Direction) m_flash.Add(new FlashRing { At = a.Point, R = a.K == Kind.Area ? Mathf.Max(a.Radius, 0.75f) : 0.6f, T0 = Time.unscaledTime });
        }

        static int SafeQuantity(Ability ab)
        {
            try
            {
                EquipmentCard c = ab.Card;
                return c != null && c.EquipmentCategory == Equipment.EquipmentType.Artifact ? ab.CurrentArtifactQuantity : -1;
            }
            catch (Exception) { return -1; }
        }

        /// Thrown proxies of a VR use leave the hand: a lob or projectile the game spawned at the hero (its hand node) starts
        /// at the palm instead; a lob then solves its own arc to the marker, a projectile is aimed from there.
        static void EngagePrefix(CombatProxy __instance, CombatEffectProxy a_effect, CombatTarget a_target, Transform a_origin)
        {
            CombatSeams.ProxyEngaged(__instance, a_effect, a_target, a_origin);
            ShieldShots.Engaged(__instance, a_effect, a_target);
            Aim a = s_use;
            if (a == null || __instance == null) return;
            try
            {
                bool lob = __instance is CombatProxyLob, prj = __instance is CombatProxyProjectile;
                if (!lob && !prj) return;
                PlayerController pc = PlayerController.Instance;
                if (pc == null) return;
                Vector3 off = __instance.transform.position - pc.transform.position;
                off.y = 0f;
                if (off.magnitude > 2.5f) return;   // spawned at the target (an area placed there): leave it
                Vector3 hand = a.ReleaseAt;
                __instance.transform.position = hand;
                if (prj)
                {
                    Vector3 aim = a_target.Targetable != null ? a_target.Targetable.AimTransform.position : a_target.Position;
                    Vector3 d = aim - hand;
                    if (a.K == Kind.Direction) d = a.Dir;
                    if (d.sqrMagnitude > 1e-4f) __instance.transform.rotation = Quaternion.LookRotation(d);
                }
                HandStarts++;
            }
            catch (Exception e) { Plugin.L.LogWarning("Abilities: hand start: " + e.Message); }
        }

        // ---------------------------------------------------------------------------------------- drawing

        static Material s_base;
        static Texture2D s_ringTex, s_dotTex;
        static readonly Color Gold = new Color(1f, 0.78f, 0.12f), Grey = new Color(0.62f, 0.62f, 0.62f);

        static Texture2D RingTex()
        {
            if (s_ringTex != null) return s_ringTex;
            const int n = 128;
            s_ringTex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "HoF2VR_AbilityRing" };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy);
                    float ring = Mathf.Clamp01(1f - Mathf.Abs(r - 0.9f) / 0.07f);
                    float fill = r < 0.9f ? 0.18f * r : 0f;
                    px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Max(ring, fill));
                }
            s_ringTex.SetPixels(px);
            s_ringTex.Apply();
            return s_ringTex;
        }

        static Texture2D DotTex()
        {
            if (s_dotTex != null) return s_dotTex;
            const int n = 64;
            s_dotTex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "HoF2VR_AbilityDot" };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - r);
                    px[y * n + x] = new Color(1f, 1f, 1f, a * a * (3f - 2f * a));
                }
            s_dotTex.SetPixels(px);
            s_dotTex.Apply();
            return s_dotTex;
        }

        static Material Mat(Texture2D tex, int queue)
        {
            if (s_base == null) s_base = new Material(Shader.Find("Sprites/Default"));
            return new Material(s_base) { mainTexture = tex, renderQueue = queue };
        }

        void Make(Aim a)
        {
            if (a.Root != null) return;
            a.Root = new GameObject("HoF2VR_AbilityAim_" + (a.WeaponSide ? "weapon" : "artefact"));
            DontDestroyOnLoad(a.Root);
            a.LineMat = Mat(Texture2D.whiteTexture, 3155);
            a.Line = a.Root.AddComponent<LineRenderer>();
            a.Line.sharedMaterial = a.LineMat;
            a.Line.useWorldSpace = true;
            a.Line.widthMultiplier = 0.014f;
            a.Line.numCapVertices = 2;
            a.Line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            a.Line.receiveShadows = false;
            a.RingMat = Mat(RingTex(), 3152);
            a.Ring = CombatFeel.Quad(a.Root.transform, a.RingMat, Vector3.one, "ring");
            a.OrbMat = Mat(DotTex(), 3156);
            a.Orb = CombatFeel.Quad(a.Root.transform, a.OrbMat, Vector3.one * 0.09f, "charge");
        }

        static void Hide(Aim a)
        {
            if (a.Root != null && a.Root.activeSelf) a.Root.SetActive(false);
        }

        void Draw(Aim a)
        {
            Make(a);
            if (!a.Root.activeSelf) a.Root.SetActive(true);
            float now = Time.unscaledTime;
            Hand h = HandOf(a.WeaponSide);
            Vector3 head = VRRig.I.HeadWorldPos;
            Color c = a.Valid || a.Throw || a.K == Kind.Self ? Gold : Grey;
            // the charge in the hand: a pulsing glow just above the fist, facing the eyes
            Vector3 palm = HandWorld.Valid(h) ? HandWorld.Palm(h) : a.Origin;
            Vector3 orb = palm + Vector3.up * 0.06f;
            a.Orb.position = orb;
            a.Orb.rotation = Quaternion.LookRotation(orb - head);
            float pulse = 0.85f + 0.15f * Mathf.Sin((now - a.Since) * 9f);
            a.Orb.localScale = Vector3.one * 0.09f * pulse;
            a.OrbMat.color = new Color(Gold.r, Gold.g, Gold.b, 0.9f);
            // the pointer
            // a stick hold shows the ring where it lands, no arc out of the hand (rays stay: aimed shots have no ring)
            bool line = !a.Throw && a.K != Kind.Self && (!a.ByStick || a.K == Kind.Direction || a.K == Kind.Ranged || a.K == Kind.Melee);
            a.Line.enabled = line;
            if (line)
            {
                if (a.K == Kind.Direction || a.K == Kind.Ranged || a.K == Kind.Melee)
                {
                    // a ray (thicker than the arc: nothing on the ground marks it); a shot's to 15 m (it flies on)
                    a.Line.widthMultiplier = 0.022f;
                    Vector3 to = a.K == Kind.Direction ? a.Origin + a.Dir * Mathf.Min(a.Range, 15f)
                        : a.Target != null ? a.Target.AimTransform.position : a.Origin + a.Dir * Mathf.Min(a.Range, 8f);
                    a.Line.positionCount = 2;
                    a.Line.SetPosition(0, a.Origin);
                    a.Line.SetPosition(1, to);
                }
                else
                {
                    a.Line.widthMultiplier = 0.014f;
                    int n = Mathf.Min(a.Path.Count, 64);
                    a.Line.positionCount = n;
                    for (int i = 0; i < n; i++) a.Line.SetPosition(i, a.Path[i]);
                }
                Color lc = c;
                lc.a = 0.75f;
                Color end = lc;
                end.a = 0.35f;
                a.Line.startColor = lc;
                a.Line.endColor = end;
            }
            // the marker on the ground
            bool ring = !a.Throw && a.K != Kind.Direction && (a.K != Kind.Ranged || a.Target != null) && (a.K != Kind.Melee || a.Target != null);
            a.Ring.gameObject.SetActive(ring);
            if (ring)
            {
                float r = a.K == Kind.Area ? Mathf.Max(a.Radius, 0.75f) : a.K == Kind.Self ? 1.0f : 0.6f;
                Vector3 at = a.Valid ? a.Point : a.Raw;
                if (!a.Valid && a.K != Kind.Area) r = 0.25f;
                a.Ring.position = at + Vector3.up * 0.04f;
                a.Ring.rotation = Quaternion.Euler(90f, 0f, 0f);
                a.Ring.localScale = Vector3.one * (2f * r);
                Color rc = c;
                rc.a = a.Valid ? (a.Covered > 0 || a.Target != null ? 1f : 0.75f) : 0.5f;
                a.RingMat.color = rc;
            }
        }

        sealed class FlashRing { public Vector3 At; public float R, T0; public Transform T; public Material M; }
        readonly List<FlashRing> m_flash = new List<FlashRing>();

        /// After a use: the marker flares and fades where the ability lands (0.6 s).
        void Flash()
        {
            float now = Time.unscaledTime;
            for (int i = m_flash.Count - 1; i >= 0; i--)
            {
                FlashRing f = m_flash[i];
                float k = (now - f.T0) / 0.6f;
                if (k >= 1f || !Physical.Live)
                {
                    if (f.T != null) Destroy(f.T.parent != null ? f.T.parent.gameObject : f.T.gameObject);
                    if (f.M != null) Destroy(f.M);
                    m_flash.RemoveAt(i);
                    continue;
                }
                if (f.T == null)
                {
                    f.M = Mat(RingTex(), 3152);
                    var go = new GameObject("HoF2VR_AbilityFlash");
                    f.T = CombatFeel.Quad(go.transform, f.M, Vector3.one, "ring");
                }
                f.T.position = f.At + Vector3.up * 0.05f;
                f.T.rotation = Quaternion.Euler(90f, 0f, 0f);
                f.T.localScale = Vector3.one * (2f * f.R * (1f + 0.35f * k));
                f.M.color = new Color(1f, 0.85f, 0.35f, 1f - k);
            }
        }

        // ---------------------------------------------------------------------------------------- test seams

        public static void Register()
        {
            // `ability?`: both hands' abilities (kind, usable or why not, charges) and the aim in progress
            Diag.Commands["ability?"] = a => Query();
            // `abilitytest weapon|artifact <enemy|nearest|none|DX DZ>`: hold, aim at that enemy (or the ground DX m right, DZ
            // m ahead of the head; `none` = a point 6 m ahead with no enemy near), let go - the real pipeline, without the hand
            Diag.Commands["abilitytest"] = a => I != null ? I.Test(a) : "no abilities";
            // `abilityaim weapon|artifact <enemy|nearest|X Y Z>`: the xrsim `rot` that makes the pointer's arc land there
            Diag.Commands["abilityaim"] = a => AimCommand(a);
            // `abilitycharge [n]`: both abilities usable now (cooldown over, the weapon's hit counter and the super meter full, n
            // artefact charges)
            Diag.Commands["abilitycharge"] = a =>
            {
                if (!TestMode.Enabled) return "test mode only";
                PlayerController pc = PlayerController.Instance;
                if (pc == null) return "no hero";
                int n = a.Length > 1 ? int.Parse(a[1], Inv) : 3;
                SuperMeter.Charge = SuperMeter.Max;   // Stage 2 item 8: in Physical mode the super meter is the weapon's hit counter
                var sb = new StringBuilder();
                foreach (bool w in new[] { true, false })
                {
                    Ability ab = AbilityOf(pc, w);
                    if (ab == null) { sb.Append(w ? "weapon: none; " : "artefact: none"); continue; }
                    ab.Reset();
                    try { AccessTools.Field(typeof(Ability), "m_currAttackHitCounter").SetValue(ab, Mathf.Max(ab.CurrentAttackHitCounter, ab.AttackHitsRequired + 1)); } catch (Exception) { }
                    if (!w && SafeQuantity(ab) >= 0 && ab.CurrentArtifactQuantity < n) ab.CurrentArtifactQuantity = n;
                    sb.Append(w ? "weapon " : "artefact ").Append(Name(ab)).Append(" canUse ").Append(ab.CanUse).Append(w ? "; " : "");
                }
                return sb.ToString();
            };
            Diag.Commands["abilitylog"] = a =>
            {
                if (a.Length > 1 && a[1] == "reset") { ResetCounts(); return "reset"; }
                Plugin.L.LogInfo("Abilities log:\n  " + string.Join("\n  ", s_log.ToArray()));
                return Describe();
            };
        }

        static string Query()
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null || I == null) return "no hero";
            var sb = new StringBuilder();
            foreach (Aim a in I.m_aims)
            {
                Ability ab = AbilityOf(pc, a.WeaponSide);
                float r, rad, ang;
                string kind = ab != null ? KindOf(ab, out r, out rad, out ang) + " range " + r.ToString("0.0", Inv) + " radius " + rad.ToString("0.0", Inv) : "-";
                string why = WhyNot(pc, a.WeaponSide, ab);
                sb.Append(a.WeaponSide ? "weapon " : "artefact ").Append(Name(ab)).Append(' ').Append(ab != null ? ab.GetType().Name : "-").Append(' ').Append(kind)
                  .Append(" usable ").Append(why == null ? "yes" : "no (" + why + ")").Append(" charges ").Append(ab != null ? SafeQuantity(ab).ToString(Inv) : "-");
                if (a.On)
                    sb.Append(" AIMING valid ").Append(a.Valid).Append(" point ").Append(Diag.V(a.Point)).Append(" raw ").Append(Diag.V(a.Raw))
                      .Append(" target ").Append(a.Target != null ? WeaponGrips.ModelKey(a.Target.name) : "-").Append(" covered ").Append(a.Covered)
                      .Append(" path ").Append(a.Path.Count).Append(a.Throw ? " throw" : "");
                sb.Append(" | ");
            }
            sb.Append(Describe());
            return sb.ToString();
        }

        string Test(string[] a)
        {
            if (!TestMode.Enabled) return "test mode only";
            if (!Physical.Live) return "not a live Physical fight";
            if (a.Length < 3) return "usage: abilitytest weapon|artifact <enemy|nearest|none|DX DZ>";
            PlayerController pc = PlayerController.Instance;
            Aim aim = m_aims[a[1].StartsWith("w") ? 0 : 1];
            if (aim.On) End(aim);
            Begin(aim, pc);
            if (!aim.On) return "refused: " + Last;
            aim.Forced = true;
            aim.ForcedTarget = null;
            Vector3 head = VRRig.I.HeadWorldPos, fwd = Flat(VRRig.I.HeadWorldRot * Vector3.forward), right = Vector3.Cross(Vector3.up, fwd);
            if (a.Length >= 4) aim.ForcedPoint = pc.transform.position + right * float.Parse(a[2], Inv) + fwd * float.Parse(a[3], Inv);
            else if (a[2] == "none") aim.ForcedPoint = FreeSpot(pc, head, fwd);
            else
            {
                aim.ForcedTarget = Physical.FindEnemy(a[2]);
                if (aim.ForcedTarget == null) { End(aim); return "no enemy " + a[2]; }
                aim.ForcedPoint = aim.ForcedTarget.transform.position;
            }
            Track(aim, pc);
            string marker = aim.K + " valid " + aim.Valid + " point " + Diag.V(aim.Point) + " target " + (aim.Target != null ? WeaponGrips.ModelKey(aim.Target.name) : "-") + " covered " + aim.Covered;
            Hand h = HandOf(aim.WeaponSide);
            aim.ReleaseAt = HandWorld.Valid(h) ? HandWorld.Palm(h) : head + fwd * 0.4f;
            int used0 = Used;
            if (!aim.Valid) { Invalid++; Note("let go without a valid marker (" + aim.K + "): nothing used"); End(aim); return marker + " -> nothing used"; }
            Use(aim, pc);
            End(aim);
            return marker + " -> " + (Used > used0 ? "used: " : "not used: ") + Last;
        }

        /// A point on the ground about 6 m ahead with no enemy within 3 m (for `abilitytest ... none`).
        static Vector3 FreeSpot(PlayerController pc, Vector3 head, Vector3 fwd)
        {
            Vector3 hero = pc.transform.position;
            for (int k = 0; k < 12; k++)
            {
                Vector3 d = Quaternion.Euler(0f, k * 30f, 0f) * fwd;
                Vector3 p = hero + d * 6f;
                if (OnGround(pc, ref p) && Count(pc, p, 3f) == 0) return p;
            }
            return hero + fwd * 6f;
        }

        static string AimCommand(string[] a)
        {
            if (a.Length < 3 || VRRig.I == null) return "usage: abilityaim weapon|artifact <enemy|nearest|X Y Z>";
            bool weapon = a[1].StartsWith("w");
            Hand h = HandOf(weapon);
            if (!h.Tracked) return "hand untracked";
            Vector3 world;
            if (a.Length >= 5) world = new Vector3(float.Parse(a[2], Inv), float.Parse(a[3], Inv), float.Parse(a[4], Inv));
            else
            {
                Targetable t = Physical.FindEnemy(a[2]);
                if (t == null) return "no enemy " + a[2];
                world = t.transform.position;
            }
            Ray ray = HandWorld.PointRay(h);
            Vector3 o = ray.origin, d = world - o;
            float x = new Vector2(d.x, d.z).magnitude, y = d.y, v = ArcSpeed, g = ArcGravity;
            // the lower of the two launch angles that land the arc at (x, y)
            float disc = v * v * v * v - g * (g * x * x + 2f * y * v * v);
            float ang = disc >= 0f ? Mathf.Atan((v * v - Mathf.Sqrt(disc)) / (g * x)) : Mathf.PI / 4f;
            Vector3 flat = Flat(d);
            Vector3 dirW = flat * Mathf.Cos(ang) + Vector3.up * Mathf.Sin(ang);
            Vector3 dt = VRRig.I.FromWorldDir(dirW).normalized;                 // tracking space (Unity)
            Vector3 xr = new Vector3(dt.x, dt.y, -dt.z);                         // OpenXR stage
            float pitch = Mathf.Asin(Mathf.Clamp(xr.y, -1f, 1f)) * Mathf.Rad2Deg + ControllerGeometry.TipPitchDown + VRPointer.PitchOffset;
            float yaw = Mathf.Atan2(-xr.x, -xr.z) * Mathf.Rad2Deg;
            return string.Format(Inv, "rot {0} {1:0.##} {2:0.##} 0", h.IsLeft ? "l" : "r", yaw, pitch);
        }

        /// One line per ability for the survey (Diag `abilitysurvey`): kind, targeting, range, use, and its effect tree.
        public static string Describe(Ability ab)
        {
            var sb = new StringBuilder();
            float r, rad, ang;
            Kind k = KindOf(ab, out r, out rad, out ang);
            sb.Append(ab.GetType().Name).Append(' ').Append(k).Append(" range ").Append(r.ToString("0.#", Inv));
            if (rad > 0f) sb.Append(" radius ").Append(rad.ToString("0.#", Inv));
            AbilityThrown t = ab as AbilityThrown;
            if (t != null) sb.Append(" first ").Append(t.InitialDistance.ToString("0.#", Inv)).Append(t.ThrowHelper != null ? " helper " + t.ThrowHelper.GetType().Name : "");
            AbilityRanged rg = ab as AbilityRanged;
            if (rg != null) sb.Append(" angle ").Append(rg.Angle.ToString("0", Inv));
            sb.Append(" use ").Append(ab.UseCondition).Append(" cat ").Append(ab.Category).Append(" blockable ").Append(ab.IsBlockable);
            try { sb.Append(" qty ").Append(ab.StartingArtifactQuantity); } catch (Exception) { }
            try { sb.Append(" anim ").Append(ab.Animation); } catch (Exception) { }
            sb.Append(" fx {");
            try { Tree(sb, ab.CombatEffects, 0); } catch (Exception e) { sb.Append("? ").Append(e.Message); }
            sb.Append('}');
            return sb.ToString();
        }

        static void Tree(StringBuilder sb, CombatEffect[] fx, int depth)
        {
            if (fx == null || depth > 4) return;
            for (int i = 0; i < fx.Length; i++)
            {
                CombatEffect e = fx[i];
                if (e == null) continue;
                if (i > 0) sb.Append(", ");
                string n = e.GetType().Name;
                sb.Append(n.StartsWith("CombatEffect") ? n.Substring(12) : n);
                CombatEffectProxy p = e as CombatEffectProxy;
                if (p != null)
                {
                    CombatProxy pp = null;
                    try { pp = p.ProxyPrefab; } catch (Exception) { }
                    sb.Append('[').Append(pp != null ? pp.GetType().Name.Replace("CombatProxy", "") + ":" + pp.name : "?").Append("]{");
                    try { Tree(sb, ((ICombatEffectContainer)p).CombatEffects, depth + 1); } catch (Exception) { }
                    sb.Append('}');
                    continue;
                }
                CombatEffectGroup g = e as CombatEffectGroup;
                if (g != null)
                {
                    sb.Append('{');
                    try { Tree(sb, ((ICombatEffectContainer)g).CombatEffects, depth + 1); } catch (Exception) { }
                    sb.Append('}');
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Physical mode: the shield against shots (docs/combat-uyf-design.md §8.7 Phase 2, §2.3.3) - arrows, thrown knives,
    /// bolas, bottles and bombs - in the guard bar's language:
    ///  * every enemy shot flying at the player gets a bar where it will cross, 0.4 m in front of the face toward it, across
    ///    its path: white = the shield stops it, red = it can't be stopped (get out of its way). It fills up until the shot
    ///    arrives and brightens (a click) while the shield is on it; the result flashes (green stopped, red hit, grey missed);
    ///  * projectiles keep the game's own gate (CombatProxyProjectile.TestDefend: only shots the game lets a hero's shield
    ///    defend, `CanReflect`): the shield on the shot's line and facing it -> the game's Defended (its cards and curses),
    ///    its parry sound and sparks on the shield, the impact in the shield hand;
    ///  * lobs (bottles, bombs: no defend in the game) come at the chest (P2-11: the game lobs them down at the feet, where no
    ///    raised shield meets them and the bar, kept at chest height, was not on their path). One that meets the shield is
    ///    knocked away: it flies off KnockDistance along the shield's face (back at the thrower when it stands that way) and
    ///    bursts there - within SafeRadius of the player it touches nothing (it fizzles out if it hits the scenery there), so its
    ///    fire (the molotov's: 1.9 m around) never reaches the player; the game's warning circle at the feet goes. One that
    ///    is not blocked drops onto the game's own spot (its circle) when it gets there: the player's feet if they stayed,
    ///    empty ground if they dashed away. Without a shield a lob's bar is red: get out of its way. The user, 2026-10-17: "If
    ///    you can block it, it should get knocked away far enough not to set you ablaze." (It glanced off at 0.45 of its speed
    ///    before and burst a step away: still on fire.)
    ///  * (audit item 11, UYF's E26: "cut it or block it with the weapon") the blade stops a shot too: held across its line or
    ///    swung through it in the last frames (within BladeSlack of the shot's last stretch) - the same shots the shield may
    ///    stop (the game's CanReflect), without the game's shield outcome; lobs stay the shield's.
    internal class ShieldShots : MonoBehaviour
    {
        public static ShieldShots I;
        public static int Seen, Defended, Deflected, HitsTaken, Missed, Markers, LockIns, Unblockable, BladeStops;
        /// P2-11: lobs aimed at the chest, knocked away (and how far from the player they burst: the nearest), fizzled, and the
        /// enemies' fire areas (bursts) seen - with the nearest one's distance from the player when it began, and its radius.
        public static int ChestAims, Drops, Knocked, KnockBursts, Fizzles, Areas, AreaHits, KnockedAreaHits, CirclesCleared;
        public static float LastFireGap = -1f;   // the last enemy fire's (not a warning circle's) distance from the player (m)
        static readonly HashSet<int> s_knockAreas = new HashSet<int>();   // the fires of knocked lobs
        public static float KnockBurstMin = 99f, AreaMinGap = 99f, AreaRadiusMax;
        /// Where a knocked lob lands (m from the player) and how far around the player it touches nothing.
        public const float KnockDistance = 7f, SafeRadius = 4.5f;
        /// The arc of a lob at the chest and of a knocked lob (deg above the flat).
        public const float ThrowAngle = 35f, KnockAngle = 35f;
        public static string Last = "-";
        static readonly List<string> s_log = new List<string>();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        /// How far the shield may be from the shot's line (beyond its own radius, m), and how squarely it must face it.
        public const float Slack = 0.10f, Facing = 0.2f;
        /// How near the blade (its edge line) must pass the shot's line to stop it (m): the blade's half width and the shot's.
        public const float BladeSlack = 0.09f;

        sealed class Shot
        {
            public CombatProxy P;
            public Controller Src;                 // the shooter (a wind-up has no proxy yet)
            public bool WindUp;                    // from the ranged telegraph until the shot leaves
            public float Cancel = -1f;             // a wind-up whose shot never left: gone at this time
            public bool Lob, Blockable, Deflected, Valid, WasValid;
            public bool Aimed, Dropped;            // P2-11: a lob thrown at the chest; ... dropped onto the game's own spot
            public Vector3 Ground, From0;          // ... the game's target on the ground (its warning circle) and where it left
            public float Start, Eta = -1f;
            public string Who = "";
            public string Result;
            public float ResultAt;
            public Color ResultColour;
            public Vector3 Cross, Toward;          // where it will cross the guard plane; from the head toward it (flat)
            public Transform Bar, Fill;
            public Material BarMat, FillMat;
            // playtest 3 (2026-10-18), notes B and C: what can stop it - the blade gets the bar, the shield the circle; neither:
            // the ground arrow during its wind-up, nothing once it flies
            public bool BladeOK, ShieldOK, ValidBlade, ValidShield, ByShield;
            public IndicatorLooks.ShieldRing Ring;
            public IndicatorLooks.GroundArrow GroundMark;
            public float RingRadius = 0.2f, RingAt, GroundAt;
            public bool GameBlockable = true;      // the game's blockable flag at the wind-up (its ranged parry window)
        }

        readonly List<Shot> m_shots = new List<Shot>();
        static readonly AccessTools.FieldRef<CombatProxyLob, Vector3> s_lobVel = AccessTools.FieldRefAccess<CombatProxyLob, Vector3>("m_velocity");
        static readonly AccessTools.FieldRef<CombatProxyProjectile, float> s_prjSpeed = AccessTools.FieldRefAccess<CombatProxyProjectile, float>("m_projectileSpeed");
        static readonly AccessTools.FieldRef<CombatProxyLob, bool> s_lobExpiring = AccessTools.FieldRefAccess<CombatProxyLob, bool>("m_isExpiring");

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(CombatProxyProjectile), "TestDefend"), prefix: new HarmonyMethod(typeof(ShieldShots), nameof(TestDefendPrefix)));
                h.Patch(AccessTools.Method(typeof(CombatProxy), "ApplyEffects"), prefix: new HarmonyMethod(typeof(ShieldShots), nameof(ApplyPrefix)));
                h.Patch(AccessTools.Method(typeof(CombatProxyLob), "Update"), prefix: new HarmonyMethod(typeof(ShieldShots), nameof(LobPrefix)));
                h.Patch(AccessTools.Method(typeof(CombatProxyLob), "OnEngage"), postfix: new HarmonyMethod(typeof(ShieldShots), nameof(LobEngagePostfix)));
                h.Patch(AccessTools.Method(typeof(CombatUtils), "StartRangedEffect"), postfix: new HarmonyMethod(typeof(ShieldShots), nameof(RangedStart)));
                h.Patch(AccessTools.Method(typeof(CombatUtils), "StopRangedEffect"), postfix: new HarmonyMethod(typeof(ShieldShots), nameof(RangedStop)));
            }
            catch (Exception e) { Plugin.L.LogWarning("ShieldShots: patch failed: " + e.Message); }
        }

        void Awake() { I = this; }

        static void Note(string s)
        {
            Last = s;
            if (s_log.Count < 200) s_log.Add(FightClock.Now.ToString("0.00", Inv) + " " + s);
            if (s_log.Count <= 60) Plugin.L.LogInfo("ShieldShots: " + s);
        }

        public static string Describe() =>
            "seen " + Seen + " defended " + Defended + " deflected " + Deflected + " hitsTaken " + HitsTaken + " missed " + Missed + " unblockable " + Unblockable +
            " markers " + Markers + " lockIns " + LockIns + " stopFeedback " + StopFeedback + " bladeStops " + BladeStops +
            " chestAims " + ChestAims + " drops " + Drops + " knocked " + Knocked + " circlesCleared " + CirclesCleared + " knockBursts " + KnockBursts + " knockBurstMin " + KnockBurstMin.ToString("0.00", Inv) + " fizzles " + Fizzles +
            " areas " + Areas + " areaHits " + AreaHits + " knockedAreaHits " + KnockedAreaHits + " lastFireGap " + LastFireGap.ToString("0.00", Inv) + " areaMinGap " + AreaMinGap.ToString("0.00", Inv) + " areaRadiusMax " + AreaRadiusMax.ToString("0.00", Inv) + " grounds " + Grounds + " rings " + Rings + " lastGround [" + LastGround + "] last " + Last;

        public static void ResetCounts()
        {
            Seen = Defended = Deflected = HitsTaken = Missed = Markers = LockIns = Unblockable = BladeStops = 0;
            ChestAims = Drops = Knocked = KnockBursts = Fizzles = Areas = AreaHits = KnockedAreaHits = CirclesCleared = 0;
            LastFireGap = -1f;
            s_knockAreas.Clear();
            KnockBurstMin = AreaMinGap = 99f;
            AreaRadiusMax = 0f;
            s_log.Clear();
            Last = "-";
        }

        public static string Log() => string.Join("\n  ", s_log.ToArray());

        // ---------------------------------------------------------------------------------------- which shots

        /// From the Engage prefix (Abilities): an enemy's projectile or lob, aimed at the player.
        public static void Engaged(CombatProxy p, CombatEffectProxy effect, CombatTarget target)
        {
            if (I == null || p == null || !Physical.Live) return;
            if (p is CombatProxyArea) { AreaEngaged((CombatProxyArea)p, effect, target); return; }
            bool lob = p is CombatProxyLob;
            if (lob) s_knocks.Remove(p.GetInstanceID());   // a pooled lob thrown again
            CombatProxyProjectile prj = p as CombatProxyProjectile;
            if (!lob && prj == null) return;
            try
            {
                PlayerController pc = PlayerController.Instance;
                Targetable src = effect != null ? effect.Source : null;
                if (pc == null || src == null || src.gameObject == pc.gameObject) return;
                if ((src.Team & pc.Targetable.GetEnemyTeams()) == 0) return;   // the player's own or an ally's
                if (target.Targetable != null && target.Targetable.gameObject != pc.gameObject) return;
                // the shooter's wind-up bar carries on as this shot's bar
                Controller sc = src.GetComponent<Controller>();
                Shot s = null;
                foreach (Shot w in I.m_shots) if (w.WindUp && w.Src == sc && w.P == null) { s = w; break; }
                if (s != null)
                {
                    float wind = FightClock.Now - s.Start;
                    string k = s.Who;
                    float old;
                    s_wind[k] = s_wind.TryGetValue(k, out old) ? Mathf.Lerp(old, wind, 0.5f) : wind;
                }
                else { s = new Shot { Start = FightClock.Now, Who = WeaponGrips.ModelKey(src.name) }; I.m_shots.Add(s); }
                s.P = p;
                s.Lob = lob;
                s.WindUp = false;
                // a lob only the shield stops (P2-11: no shield, a red bar - get out of its way)
                s.Blockable = lob ? pc.Inventory.Shield != null : prj.CanReflect;
                if (lob) s_lobbers.Add(s.Who);
                else if (prj != null) { if (prj.CanReflect) s_noReflect.Remove(s.Who); else s_noReflect.Add(s.Who); }   // the audit, item 4
                Seen++;
                if (!s.Blockable) Unblockable++;
                Note("shot " + p.GetType().Name.Replace("CombatProxy", "") + " " + WeaponGrips.ModelKey(p.name) + " from " + s.Who + (s.Blockable ? "" : " (can't be blocked)"));
            }
            catch (Exception e) { Plugin.L.LogWarning("ShieldShots: engaged: " + e.Message); }
        }

        static readonly Dictionary<string, float> s_wind = new Dictionary<string, float>();   // telegraph -> shot, per shooter
        // shooters seen throwing lobs; seeded with the ones every log shows lobbing (playtest 3 note C: their first wind-up is
        // a lob's - the circle, or the ground arrow without a shield - not a bar for the blade)
        static readonly HashSet<string> s_lobbers = new HashSet<string> { "ThievesMolotovThrowerModel", "ThievesMolotovThrower", "VillagerMolotovModel", "VillagerMolotov" };
        /// The audit, item 4: the bomb and bottle throwers by family (their first wind-up is a lob's: the shield only), and the
        /// shooters whose last shot the game could not reflect (their wind-ups red too).
        static readonly string[] s_lobFamilies = { "GoblinLobber", "ThievesMolotov", "VillagerMolotov" };
        static readonly HashSet<string> s_noReflect = new HashSet<string>();

        static bool IsLobber(string who)
        {
            if (s_lobbers.Contains(who)) return true;
            foreach (string f in s_lobFamilies) if (who.StartsWith(f, StringComparison.Ordinal)) return true;
            return false;
        }

        /// The ranged telegraph (the shooter draws or winds up at the player): its bar appears now, on the line to the chest.
        static void RangedStart(EffectSet a_effectSet, Model a_model, bool a_blockable)
        {
            if (I == null || a_model == null || !Physical.Live) return;
            try
            {
                AIController ai = a_model.GetComponentInParent<AIController>();
                PlayerController pc = PlayerController.Instance;
                if (ai == null || pc == null || ai.Target == null || ai.Target.gameObject != pc.gameObject) return;
                // only shooters (bows, guns, bottles, bombs): the ranged telegraph also marks leaps (the gnomes' and goblins'
                // snare), self spells and charges, which no shield stops
                Actor actor = ai.GetComponent<Actor>();
                if (actor == null || (actor.GetRunningAction<ActionRangedAI>() == null && actor.GetRunningAction<ActionRangedSkeletonQueen>() == null)) return;
                foreach (Shot w in I.m_shots) if (w.WindUp && w.Src == ai) return;
                string who = WeaponGrips.ModelKey(a_model.name);
                // a thrower seen lobbing before: only the shield stops its throws (the blade stops shots, not lobs)
                bool blockable = IsLobber(who) ? pc.Inventory.Shield != null : a_blockable && !s_noReflect.Contains(who) && (pc.Inventory.Shield != null || BladeHits.BladeCount > 0);
                var s = new Shot { Src = ai, WindUp = true, Start = FightClock.Now, Who = who, Blockable = blockable, GameBlockable = a_blockable };
                I.m_shots.Add(s);
                Note("wind-up " + s.Who);
            }
            catch (Exception) { }
        }

        static void RangedStop(EffectSet a_effectSet, Model a_model, bool a_blockable)
        {
            if (I == null || a_model == null) return;
            // the shot leaves in the same frame (OnHit applies, then closes the window); one that never left goes quietly
            foreach (Shot w in I.m_shots)
                if (w.WindUp && w.P == null && w.Src != null && w.Src.Model == a_model) w.Cancel = FightClock.Now + 0.25f;
        }

        Shot Find(CombatProxy p)
        {
            for (int i = 0; i < m_shots.Count; i++) if (m_shots[i].P == p) return m_shots[i];
            return null;
        }

        // ---------------------------------------------------------------------------------------- the shield on a shot's line

        /// The shield is on the line `from` -> `to` (the shot's last stretch) and faces it.
        static bool ShieldOnLine(Vector3 from, Vector3 to, out Vector3 at)
        {
            at = Vector3.zero;
            Vector3 c, n;
            float r;
            if (!Guard.Shield(out c, out n, out r)) return false;
            Vector3 d = to - from;
            float l = d.sqrMagnitude;
            if (l < 1e-6f) return false;
            float t = Mathf.Clamp01(Vector3.Dot(c - from, d) / l);
            at = from + d * t;
            if ((c - at).magnitude > r + Slack) return false;
            return Vector3.Dot(n, -d.normalized) > Facing;
        }

        // the player's blades over the last frames (a swing through the line between two frames still stops the shot)
        const int BladeFrames = 8;
        static readonly Vector3[] s_bladeB = new Vector3[2 * BladeFrames], s_bladeT = new Vector3[2 * BladeFrames];
        static readonly float[] s_bladeAt = new float[2 * BladeFrames];
        static int s_bladeHead;

        static void RecordBlades(float now)
        {
            s_bladeHead = (s_bladeHead + 1) % BladeFrames;
            for (int i = 0; i < 2; i++)
            {
                Vector3 b, t;
                int id;
                int k = i * BladeFrames + s_bladeHead;
                if (BladeHits.BladeAt(i, out b, out t, out id)) { s_bladeB[k] = b; s_bladeT[k] = t; s_bladeAt[k] = now; }
                else s_bladeAt[k] = -10f;
            }
        }

        /// A blade (now or in the last 0.15 s, and the sweep between two frames) on the line `from` -> `to`.
        static bool BladeOnLine(Vector3 from, Vector3 to, out Vector3 at)
        {
            at = Vector3.zero;
            float now = FightClock.Now, best = BladeSlack;
            bool found = false;
            for (int i = 0; i < 2; i++)
            {
                Vector3 b, t;
                int id;
                if (BladeHits.BladeAt(i, out b, out t, out id) && Near(b, t, from, to, ref best, ref at)) found = true;
                for (int j = 0; j < BladeFrames; j++)
                {
                    int k = i * BladeFrames + j;
                    if (now - s_bladeAt[k] > 0.15f) continue;
                    if (Near(s_bladeB[k], s_bladeT[k], from, to, ref best, ref at)) found = true;
                    // the sweep to the next frame's blade: its middle line
                    int k2 = i * BladeFrames + (j + 1) % BladeFrames;
                    if (now - s_bladeAt[k2] > 0.15f || s_bladeAt[k2] <= s_bladeAt[k]) continue;
                    if (Near((s_bladeB[k] + s_bladeB[k2]) * 0.5f, (s_bladeT[k] + s_bladeT[k2]) * 0.5f, from, to, ref best, ref at)) found = true;
                }
            }
            return found;
        }

        /// The segments a-b and c-d come within `best` of each other: `at` = the point on c-d.
        static bool Near(Vector3 a, Vector3 b, Vector3 c, Vector3 d, ref float best, ref Vector3 at)
        {
            Vector3 p, q;
            ClosestSegments(a, b, c, d, out p, out q);
            float dist = (p - q).magnitude;
            if (dist > best) return false;
            best = dist;
            at = q;
            return true;
        }

        static void ClosestSegments(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float s, t;
            if (a <= 1e-8f && e <= 1e-8f) { c1 = p1; c2 = p2; return; }
            if (a <= 1e-8f) { s = 0f; t = Mathf.Clamp01(f / e); }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 1e-8f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else
                {
                    float b = Vector3.Dot(d1, d2), denom = a * e - b * b;
                    s = denom > 1e-8f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
                }
            }
            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }

        /// CombatProxyProjectile.TestDefend (the shot reached the player's body): the shield on its line defends it, through
        /// the game's own defend outcome (ActionCounter.OnDefend without the animation); else a blade on its line stops it.
        static bool TestDefendPrefix(CombatProxyProjectile __instance, Targetable a_target, ref bool __result)
        {
            if (I == null || !Physical.Live || __instance == null) return true;
            try
            {
                PlayerController pc = PlayerController.Instance;
                if (pc == null || a_target == null || a_target.gameObject != pc.gameObject) return true;
                Shot s = I.Find(__instance);
                if (!__instance.CanReflect) return true;   // the game's gate: a hit
                // this frame's step reaches the body (the shield, in front of it, lies on that step or the ones before)
                float step = Mathf.Max(1f, s_prjSpeed(__instance)) * Time.deltaTime;
                Vector3 to = __instance.transform.position + __instance.transform.forward * (step + 0.2f);
                Vector3 from = __instance.transform.position - __instance.transform.forward * 2.5f;
                Vector3 at;
                if (pc.Inventory.Shield == null || !ShieldOnLine(from, to, out at))
                {
                    // UYF: a weapon block or a cut stops it (no shield outcome of the game's: the shot just ends on the blade)
                    if (!BladeOnLine(from, to, out at)) return true;
                    BladeStops++;
                    if (s != null) s.ByShield = false;
                    Note("stopped " + (s != null ? s.Who : "?") + "'s shot with the blade");
                    Stopped(s, at, Settings.LeftHanded);
                    __result = true;
                    return false;
                }
                pc.Defended(__instance.Effect, pc.Targetable);
                try
                {
                    CombatSourceInfo info = __instance.Effect != null ? __instance.Effect.CombatSourceInfo : null;
                    Shield sh = pc.Inventory.Shield;
                    sh.EffectSet.Trigger("parry" + (info != null && !string.IsNullOrEmpty(info.ParryEffectSuffix) ? info.ParryEffectSuffix : ""), sh.Model);
                }
                catch (Exception) { }
                Defended++;
                if (s != null) s.ByShield = true;
                Note("defended " + (s != null ? s.Who : "?") + " with the shield" + " (Combat.Defend " + pc.Combat.Defend + ")");
                Stopped(s, at, !Settings.LeftHanded);
                __result = true;
                return false;
            }
            catch (Exception e) { Plugin.L.LogWarning("ShieldShots: defend: " + e.Message); return true; }
        }

        /// A tracked shot's effects reach the player: a hit (for the marker's flash and the counts).
        static void ApplyPrefix(CombatProxy __instance, CombatTarget a_target)
        {
            if (I == null || !Physical.Live || __instance == null) return;
            try
            {
                PlayerController pc = PlayerController.Instance;
                if (pc == null || a_target.Targetable == null || a_target.Targetable.gameObject != pc.gameObject) return;
                if (__instance is CombatProxyArea)
                {
                    // an enemy's fire or blast reaches the player (P2-11's check: none from a knocked lob)
                    Targetable src = __instance.Effect != null ? __instance.Effect.Source : null;
                    if (src != null && src.gameObject != pc.gameObject && (src.Team & pc.Targetable.GetEnemyTeams()) != 0)
                    {
                        AreaHits++;
                        bool knocked = s_knockAreas.Contains(__instance.GetInstanceID());
                        if (knocked) KnockedAreaHits++;
                        Note("area " + WeaponGrips.ModelKey(__instance.name) + " of " + WeaponGrips.ModelKey(src.name) + " reached the player" + (knocked ? " - A KNOCKED LOB'S" : ""));
                    }
                    return;
                }
                if (I.m_shots.Count == 0) return;
                Shot s = I.Find(__instance);
                if (s == null || s.Result != null) return;
                s.Result = "HIT";
                s.ResultAt = FightClock.Now;
                s.ResultColour = Premonition.Miss;   // playtest 3 note C: a hit fades grey (red only means "can't block")
                HitsTaken++;
                Note("hit by " + s.Who + "'s shot" + (s.Blockable ? "" : " (can't be blocked)"));
            }
            catch (Exception) { }
        }

        /// CombatProxyLob.OnEngage (the game solved the arc to the hero's feet): an enemy's lob at the player is aimed at the
        /// chest instead, where the bar shows it and a raised shield meets it (P2-11). Unblocked it meets the body as before.
        static void LobEngagePostfix(CombatProxyLob __instance)
        {
            if (I == null || !Physical.Live || __instance == null || VRRig.I == null) return;
            try
            {
                Shot s = I.Find(__instance);
                if (s == null || !s.Lob) return;
                Vector3 chest = VRRig.I.HeadWorldPos + Vector3.down * 0.35f, v;
                if (!Ballistic(__instance.transform.position, chest, ThrowAngle, out v)) return;
                CombatTarget t = __instance.Target;
                s.Ground = t.Targetable != null ? t.Targetable.transform.position : t.Position;
                s.From0 = __instance.transform.position;
                s.Aimed = true;
                s_lobVel(__instance) = v;
                __instance.transform.rotation = Quaternion.LookRotation(v);
                ChestAims++;
            }
            catch (Exception e) { Plugin.L.LogWarning("ShieldShots: lob aim: " + e.Message); }
        }

        /// The launch velocity from `from` that passes through `to` at `angle` deg above the flat (false if there is none).
        static bool Ballistic(Vector3 from, Vector3 to, float angle, out Vector3 v)
        {
            v = Vector3.zero;
            Vector3 d = to - from, flat = new Vector3(d.x, 0f, d.z);
            float x = flat.magnitude, g = Physics.gravity.magnitude;
            if (x < 0.3f) return false;
            float th = angle * Mathf.Deg2Rad, k = x * Mathf.Tan(th) - d.y;
            if (k < 0.05f) { th = Mathf.Min(75f, Mathf.Atan2(d.y, x) * Mathf.Rad2Deg + 20f) * Mathf.Deg2Rad; k = x * Mathf.Tan(th) - d.y; }
            if (k < 0.05f) return false;
            float speed = Mathf.Sqrt(0.5f * g * x * x / k) / Mathf.Cos(th);
            if (float.IsNaN(speed) || speed > 40f) return false;
            v = (flat / x * Mathf.Cos(th) + Vector3.up * Mathf.Sin(th)) * speed;
            return true;
        }

        /// A lob knocked off the shield: its flight inside SafeRadius (the mod's own steps: through nothing alive, fizzling on
        /// the scenery), then the game's.
        sealed class Knock { public CombatProxyLob Lob; public Vector3 Hero; public float At; public bool Handed, Fizzled; public string Who; }
        static readonly Dictionary<int, Knock> s_knocks = new Dictionary<int, Knock>();
        static int s_scenery;
        static readonly List<int> s_gone = new List<int>();

        /// The ground, the walls and props (not characters, effects or triggers).
        static int SceneryMask()
        {
            if (s_scenery != 0) return s_scenery;
            int m = 0;
            foreach (string n in new[] { "Default", "Ground", "Obstacle" }) { int l = LayerMask.NameToLayer(n); if (l >= 0) m |= 1 << l; }
            return s_scenery = m != 0 ? m : 1;
        }

        static Vector3 HeroFeet()
        {
            PlayerController pc = PlayerController.Instance;
            return pc != null ? pc.transform.position : VRRig.I != null ? VRRig.I.HeadWorldPos : Vector3.zero;
        }

        /// CombatProxyLob.Update: a lob whose next step meets the shield is knocked away; a knocked one inside SafeRadius moves
        /// here (false: the game's Update skipped).
        static bool LobPrefix(CombatProxyLob __instance)
        {
            if (I == null || __instance == null) return true;
            try
            {
                Knock k;
                if (s_knocks.TryGetValue(__instance.GetInstanceID(), out k)) return KnockedStep(__instance, k);
                if (!Physical.Live) return true;
                Shot s = I.Find(__instance);
                if (s == null || s.Deflected || s.Result != null) return true;
                Vector3 v = s_lobVel(__instance) + Physics.gravity * Time.deltaTime;
                Vector3 p = __instance.transform.position, n2 = p + v * Time.deltaTime;
                Vector3 at;
                // its last stretch, a little longer than this frame's step (a fast lob may cross the disc between frames)
                if (!ShieldOnLine(p - v.normalized * 0.15f, n2 + v.normalized * 0.05f, out at))
                {
                    // thrown at the chest and not blocked: at the game's spot (flat) it drops onto it, steeply
                    if (s.Aimed && !s.Dropped)
                    {
                        Vector3 way = s.Ground - s.From0, gone = n2 - s.From0;
                        way.y = 0f;
                        gone.y = 0f;
                        if (way.sqrMagnitude > 0.01f && Vector3.Dot(gone, way.normalized) >= way.magnitude)
                        {
                            s.Dropped = true;
                            Drops++;
                            s_lobVel(__instance) = (s.Ground + Vector3.up * 0.02f - p).normalized * 15f - Physics.gravity * Time.deltaTime;
                        }
                    }
                    return true;
                }
                Vector3 c, n;
                float r;
                Guard.Shield(out c, out n, out r);
                Vector3 hero = HeroFeet();
                // away: along the shield's face (flat), back at the thrower when it stands within 60 deg of that
                Vector3 away = new Vector3(n.x, 0f, n.z);
                if (away.sqrMagnitude < 0.04f) away = new Vector3(-v.x, 0f, -v.z);
                away = away.sqrMagnitude > 1e-6f ? away.normalized : Vector3.forward;
                if (s.Src != null)
                {
                    Vector3 ts = s.Src.transform.position - hero;
                    ts.y = 0f;
                    if (ts.magnitude > 2f && Vector3.Angle(away, ts) < 60f) away = ts.normalized;
                }
                Vector3 land = hero + away * KnockDistance;
                RaycastHit gh;
                land.y = Physics.Raycast(land + Vector3.up * 4f, Vector3.down, out gh, 12f, SceneryMask(), QueryTriggerInteraction.Ignore) ? gh.point.y : hero.y;
                Vector3 start = at + n * 0.15f, nv;
                if (!Ballistic(start, land, KnockAngle, out nv)) nv = (away * Mathf.Cos(KnockAngle * Mathf.Deg2Rad) + Vector3.up * Mathf.Sin(KnockAngle * Mathf.Deg2Rad)) * 9f;
                s_lobVel(__instance) = nv;
                __instance.transform.position = start;
                __instance.transform.rotation = Quaternion.LookRotation(nv);
                s_knocks[__instance.GetInstanceID()] = new Knock { Lob = __instance, Hero = hero, At = FightClock.Now, Who = s.Who };
                s.Deflected = true;
                s.ByShield = true;
                Deflected++;
                Knocked++;
                ClearCircle(__instance, s);
                Note("knocked " + s.Who + "'s lob off the shield: it lands " + KnockDistance.ToString("0.0", Inv) + " m away " + (s.Src != null && Vector3.Angle(away, s.Src.transform.position - hero) < 61f ? "(back at the thrower)" : "(along the shield)"));
                Stopped(s, at, !Settings.LeftHanded);
                return false;
            }
            catch (Exception e) { Plugin.L.LogWarning("ShieldShots: lob: " + e.Message); return true; }
        }

        /// The game's warning circle for a knocked lob (its AreaIndicator at the lob's spot, by the same thrower) goes: the bottle
        /// lands elsewhere now.
        static void ClearCircle(CombatProxyLob lob, Shot s)
        {
            try
            {
                Targetable src = lob.Effect != null ? lob.Effect.Source : null;
                var all = CombatProxyArea.AllAreas;
                for (int i = all.Count - 1; i >= 0; i--)
                {
                    CombatProxyArea a = all[i];
                    if (a == null || a.name.IndexOf("Indicator", StringComparison.Ordinal) < 0) continue;
                    if (a.Effect == null || a.Effect.Source != src) continue;
                    Vector3 d = a.transform.position - (s.Aimed ? s.Ground : HeroFeet());
                    d.y = 0f;
                    if (d.magnitude > 1.5f) continue;
                    a.Disengage();
                    CirclesCleared++;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("ShieldShots: circle: " + e.Message); }
        }

        static bool KnockedStep(CombatProxyLob lob, Knock k)
        {
            if (k.Handed || s_lobExpiring(lob)) return true;
            if (k.Fizzled) return false;
            Vector3 p = lob.transform.position, hero = HeroFeet();
            Vector3 flat = p - hero;
            flat.y = 0f;
            Vector3 flat0 = p - k.Hero;
            flat0.y = 0f;
            // out of reach of the player (where they stand now and where they stood): the game flies it on and it bursts there
            if ((flat.magnitude >= SafeRadius && flat0.magnitude >= SafeRadius) || FightClock.Now - k.At > 4f) { k.Handed = true; return true; }
            float dt = Time.deltaTime;
            Vector3 v = s_lobVel(lob) + Physics.gravity * dt;
            s_lobVel(lob) = v;
            Vector3 step = v * dt;
            float len = step.magnitude;
            if (len < 1e-5f) return false;
            RaycastHit hit;
            if (Physics.Raycast(p, step / len, out hit, len, SceneryMask(), QueryTriggerInteraction.Ignore))
            {
                // the scenery right by the player: no fire there - it goes out (the game's own miss)
                k.Fizzled = true;
                Fizzles++;
                Note("a knocked lob of " + k.Who + " fizzled on " + (hit.collider != null ? hit.collider.name : "-") + " " + flat.magnitude.ToString("0.0", Inv) + " m from the player");
                CombatFeel.Burst(hit.point, new Color(0.6f, 0.6f, 0.6f), 0.25f, 0.3f);
                CombatSounds.Layer(CombatSounds.Clank, hit.point, 0.4f, 0.7f);
                lob.transform.position = hit.point;
                try { lob.Disengage(); } catch (Exception e) { Plugin.L.LogWarning("ShieldShots: fizzle: " + e.Message); }
                s_knocks.Remove(lob.GetInstanceID());
                return false;
            }
            lob.transform.position = p + step;
            lob.transform.rotation = Quaternion.LookRotation(step);
            return false;
        }

        /// An enemy's area effect begins (a bottle's fire, a bomb's blast; P2-11's numbers): how far from the player, and how
        /// big. A knocked lob's burst counts too.
        static void AreaEngaged(CombatProxyArea a, CombatEffectProxy effect, CombatTarget target)
        {
            try
            {
                s_knockAreas.Remove(a.GetInstanceID());   // a pooled area engaged again
                PlayerController pc = PlayerController.Instance;
                Targetable src = effect != null ? effect.Source : null;
                if (pc == null || src == null || src.gameObject == pc.gameObject || (src.Team & pc.Targetable.GetEnemyTeams()) == 0) return;
                Vector3 at = target.Targetable != null ? target.Targetable.transform.position : target.Position;
                float radius = 0f;
                SphereCollider sc = a.GetComponent<Collider>() as SphereCollider;
                if (sc != null) radius = sc.radius * Mathf.Max(a.transform.lossyScale.x, a.transform.lossyScale.z);
                Vector3 d = at - HeroFeet();
                d.y = 0f;
                float gap = d.magnitude;
                Areas++;
                AreaRadiusMax = Mathf.Max(AreaRadiusMax, radius);
                AreaMinGap = Mathf.Min(AreaMinGap, gap);
                bool knocked = false;
                foreach (Knock k in s_knocks.Values)
                    if (k.Handed && k.Lob != null && (k.Lob.transform.position - at).magnitude < 2.5f) { knocked = true; break; }
                if (a.name.IndexOf("Indicator", StringComparison.Ordinal) < 0) LastFireGap = gap;
                if (knocked)
                {
                    s_knockAreas.Add(a.GetInstanceID());
                    KnockBursts++;
                    KnockBurstMin = Mathf.Min(KnockBurstMin, gap);
                }
                Note("area " + WeaponGrips.ModelKey(a.name) + " of " + WeaponGrips.ModelKey(src.name) + " " + gap.ToString("0.0", Inv) + " m from the player, radius " +
                     radius.ToString("0.0", Inv) + (knocked ? " (a knocked lob's)" : ""));
            }
            catch (Exception e) { Plugin.L.LogWarning("ShieldShots: area: " + e.Message); }
        }

        /// `left`: the hand that stopped it (the shield's, or the weapon's for a blade).
        static void Stopped(Shot s, Vector3 at, bool left)
        {
            CombatSounds.Layer(CombatSounds.Clank, at, 0.9f, 1.05f);
            CombatFeel.Burst(at, new Color(1f, 0.85f, 0.5f), 0.2f, 0.2f);
            CombatFeel.Impact(left);
            // UYF's stopped shot (docs/uyf-parity-audit.md F8, P28): a shake 0.2 s / 0.12, a white flash 0.2 for 0.15 s, a push
            // 0.06 m back along the shot, super +1
            try
            {
                CameraShake.Add(0.2f, 0.12f, 0.02f, "shot stopped");
                if (CombatSettings.ScreenFlash) ScreenFlash.Show(Color.white, 0.2f, 0.15f, "shot stopped");
                Vector3 back = s != null && s.Src != null && VRRig.I != null ? VRRig.I.HeadWorldPos - s.Src.transform.position : Vector3.zero;
                back.y = 0f;
                if (back.sqrMagnitude > 1e-4f) CombatMove.Knockback(back.normalized, 0.06f, "shot stopped");
                SuperMeter.Add(1f, "shot stopped");
                StopFeedback++;
            }
            catch (Exception e) { Plugin.L.LogWarning("ShieldShots: stop feedback: " + e.Message); }
            if (s != null && s.Result == null)
            {
                s.Result = s.Lob ? "DEFLECTED" : "BLOCKED";
                s.ResultAt = FightClock.Now;
                s.ResultColour = new Color(0.3f, 1f, 0.4f);
            }
        }

        // ---------------------------------------------------------------------------------------- P2-10: shots from out of view

        /// An enemy shot flying at the player now (thrown, not a wind-up): where it comes from (its shooter, else the shot
        /// itself), how far along its warning it is (0..1), whether the shield stops it, and the shot's id.
        public struct Incoming { public Vector3 From; public float Progress; public bool Blockable; public int Id; public Controller Src; }

        public static void IncomingShots(List<Incoming> into)
        {
            into.Clear();
            if (I == null) return;
            float now = FightClock.Now;
            foreach (Shot s in I.m_shots)
            {
                if (s.WindUp || s.P == null || s.Result != null || s.Eta < 0f) continue;
                float el = now - s.Start;
                into.Add(new Incoming
                {
                    From = s.Src != null ? s.Src.transform.position : s.P.transform.position,
                    Progress = Mathf.Clamp01(el / Mathf.Max(0.05f, el + s.Eta)),
                    Blockable = s.Blockable,
                    Id = s.P.GetInstanceID(),
                    Src = s.Src
                });
            }
        }

        // ---------------------------------------------------------------------------------------- every frame: the bars

        public static int StopFeedback;
        static Material s_base;
        static readonly Color White = new Color(0.95f, 0.95f, 0.95f), Red = new Color(0.996f, 0.05f, 0.05f);
        float m_nextClick;

        void LateUpdate()
        {
            float now = FightClock.Now;
            if (Physical.Paused)
            {
                // the audit, item 3: paused, the shots are kept (FightClock stands still), their marks hidden
                foreach (Shot s in m_shots)
                {
                    if (s.GroundMark != null && s.GroundMark.Root.gameObject.activeSelf) s.GroundMark.Root.gameObject.SetActive(false);
                    if (s.Ring != null && s.Ring.Root.gameObject.activeSelf) s.Ring.Root.gameObject.SetActive(false);
                }
                return;
            }
            bool live = Physical.Live && VRRig.I != null && PlayerController.Instance != null;
            if (live) RecordBlades(now);
            if (s_knocks.Count > 0)
            {
                s_gone.Clear();
                foreach (var kv in s_knocks) if (kv.Value.Lob == null || now - kv.Value.At > 10f) s_gone.Add(kv.Key);
                foreach (int id in s_gone) s_knocks.Remove(id);
            }
            for (int i = m_shots.Count - 1; i >= 0; i--)
            {
                Shot s = m_shots[i];
                if (s.WindUp && s.P == null)
                {
                    if (!live || s.Src == null || (s.Cancel > 0f && now > s.Cancel) || now - s.Start > 6f) { Close(s); m_shots.RemoveAt(i); continue; }
                    PredictWindUp(s, now);
                    Draw(s, now);
                    continue;
                }
                bool gone = s.P == null || !s.P.gameObject.activeInHierarchy || !s.P.enabled;
                if (!live || (s.Result != null && now - s.ResultAt > 0.4f) || now - s.Start > 8f || (gone && s.Result == null && now - s.Start > 0.1f))
                {
                    if (live && s.Result == null && s.Eta >= 0f && !s.Deflected) Missed++;
                    Close(s);
                    m_shots.RemoveAt(i);
                    continue;
                }
                if (!gone && s.Result == null) Predict(s);
                Draw(s, now);
            }
        }

        /// A wind-up: the shot will come along the line from the shooter to the chest; it arrives after the shooter's learned
        /// wind-up (0.8 s until seen) and the flight.
        void PredictWindUp(Shot s, float now)
        {
            Vector3 head = VRRig.I.HeadWorldPos;
            Targetable t = s.Src.Targetable;
            Vector3 from = t != null ? t.AimTransform.position : s.Src.transform.position;
            Vector3 toward = from - head;
            toward.y = 0f;
            float dist = toward.magnitude;
            if (dist < 0.5f) { s.Eta = -1f; return; }
            toward /= dist;
            float wind, old;
            wind = s_wind.TryGetValue(s.Who, out old) ? old : 0.8f;
            s.Eta = Mathf.Max(0.05f, wind - (now - s.Start)) + dist / 30f;
            s.Toward = toward;
            Vector3 chest = head + Vector3.down * 0.3f;
            Vector3 dir = (chest - from).normalized;
            // the line's point 0.4 m from the head toward the shooter (flat), at the line's height there
            float along = (dist - 0.4f) / Mathf.Max(0.05f, new Vector2(dir.x, dir.z).magnitude);
            Vector3 c = from + dir * along;
            s.Cross = head + toward * 0.4f + Vector3.up * Mathf.Clamp(c.y - head.y, -0.6f, 0.15f);
            Vector3 at;
            Who(s, null);
            s.ValidShield = s.ShieldOK && ShieldOnLine(c - dir * 1.0f, c + dir * 0.6f, out at);
            s.ValidBlade = s.BladeOK && !s.ValidShield && BladeOnLine(c - dir * 1.0f, c + dir * 0.6f, out at);
            s.Valid = s.ValidShield || s.ValidBlade;
        }

        /// Notes B / C: which guard can stop this shot (a wind-up: by what the shooter threw before).
        static void Who(Shot s, CombatProxyProjectile prj)
        {
            PlayerController pc = PlayerController.Instance;
            bool shield = pc != null && pc.Inventory.Shield != null, blade = BladeHits.BladeCount > 0;
            if (s.Lob || (s.WindUp && s.P == null && IsLobber(s.Who))) { s.ShieldOK = shield; s.BladeOK = false; }
            else if (prj != null) { s.ShieldOK = shield && prj.CanReflect; s.BladeOK = blade && prj.CanReflect; }
            else
            {
                // a wind-up: the game's own word (its ranged parry window's blockable) and what this shooter's last shot was
                bool may = s.GameBlockable && !s_noReflect.Contains(s.Who);
                s.ShieldOK = shield && may;
                s.BladeOK = blade && may;
            }
            s.Blockable = s.ShieldOK || s.BladeOK;
        }

        /// Where and when the shot crosses the guard plane in front of the face (its flight extrapolated; gravity for lobs).
        void Predict(Shot s)
        {
            Vector3 head = VRRig.I.HeadWorldPos;
            Vector3 p = s.P.transform.position, v;
            if (s.Lob) v = s_lobVel((CombatProxyLob)s.P);
            else v = s.P.transform.forward * Mathf.Max(1f, s_prjSpeed((CombatProxyProjectile)s.P));
            Vector3 toward = p - head;
            toward.y = 0f;
            float dist = toward.magnitude;
            Vector3 vf = v;
            vf.y = 0f;
            s.Eta = -1f;
            if (dist < 0.3f || vf.sqrMagnitude < 0.01f) return;
            toward /= dist;
            float closing = -Vector3.Dot(vf, toward);
            if (closing <= 0.2f) return;
            float t = Mathf.Max(0f, (dist - 0.4f) / closing);
            if (t > 3f) return;
            Vector3 c = p + v * t + (s.Lob ? 0.5f * Physics.gravity * t * t : Vector3.zero);
            // does it come at the body at all (within 0.8 m of the head's line, and between the knees and above the head)?
            Vector3 side = c - head;
            Vector3 lateral = side - toward * Vector3.Dot(side, toward);
            lateral.y = 0f;
            if (lateral.magnitude > 0.8f || side.y < -1.3f || side.y > 0.5f) return;
            s.Eta = t;
            s.Toward = toward;
            s.Cross = head + toward * 0.4f + lateral + Vector3.up * Mathf.Clamp(side.y, -0.6f, 0.15f);
            Vector3 at;
            Who(s, s.Lob ? null : s.P as CombatProxyProjectile);
            s.ValidShield = s.ShieldOK && ShieldOnLine(c + v.normalized * -1.0f, c + v.normalized * 0.6f, out at);
            s.ValidBlade = s.BladeOK && !s.ValidShield && BladeOnLine(c + v.normalized * -1.0f, c + v.normalized * 0.6f, out at);
            s.Valid = s.ValidShield || s.ValidBlade;
        }

        public static int Grounds, Rings;
        static string s_lgWho;
        static float s_lgFill;
        static bool s_lgGold;
        /// The last ground arrow drawn (formatted on query: the audit, item 8).
        public static string LastGround => s_lgWho == null ? "-" : s_lgWho + (s_lgGold ? " gold" : " red") + " fill " + s_lgFill.ToString("0.00", Inv);
        /// The shot's circle: the point to hold the shield on (its radius, m).
        public const float ShotRing = 0.14f;

        /// Playtest 3: the user's ruling of 17:40 ("The white/red bar just looks bad... the line on the ground filling towards
        /// you"): every shot and lob shows the ground arrow from its shooter to the player, filling until it reaches them (the
        /// wind-up and the flight), then gone. Red = nothing held can stop it; gold-white = the guard can, and the shield's circle
        /// shows where it crosses at chest height (the shield or the blade there stops it - the game's gate, as before). No bar.
        void Draw(Shot s, float now)
        {
            PlayerController pcg = PlayerController.Instance;
            bool coming = s.Result == null && s.Eta >= 0f && pcg != null && s_demoArrow == null;   // (a still demo arrow hides the live ones)
            Vector3 from = s.Src != null ? s.Src.transform.position : s.P != null ? s.P.transform.position : Vector3.zero;
            float el = now - s.Start, fill = coming ? Mathf.Clamp01(el / Mathf.Max(0.05f, el + s.Eta)) : 1f;
            if (s.Bar != null && s.Bar.gameObject.activeSelf) s.Bar.gameObject.SetActive(false);
            if (coming && (s.Src != null || s.P != null))
            {
                if (s.GroundMark == null) { s.GroundMark = new IndicatorLooks.GroundArrow(); Grounds++; s.GroundAt = now; }
                s.GroundMark.Draw(from, pcg.transform.position, now - s.GroundAt, fill, !s.Blockable);
                s_lgWho = s.Who; s_lgGold = s.Blockable; s_lgFill = fill;
            }
            else if (s.GroundMark != null && s.GroundMark.Root.gameObject.activeSelf) s.GroundMark.Root.gameObject.SetActive(false);
            if (s.Result == null)
            {
                if (s.Valid && !s.WasValid)
                {
                    LockIns++;
                    if (now >= m_nextClick) { m_nextClick = now + 0.15f; CombatSounds.Layer(CombatSounds.Chime, s.Cross, 0.18f, 1.6f); }
                }
                s.WasValid = s.Valid;
            }
            // the circle: the guard can stop it (the shield, or the blade on the circle) - where it crosses, facing the shooter
            bool ring = s.Blockable && (coming || s.Result != null) && s_demoArrow == null;
            if (ring)
            {
                s.RingRadius = ShotRing;   // where it crosses, not the shield's size (0.26 m at 0.4 m filled the view's lower half)
                if (s.Ring == null) { s.Ring = new IndicatorLooks.ShieldRing("HoF2VR_ShotRing"); Rings++; s.RingAt = now; }
                s.Ring.Root.gameObject.SetActive(true);
                s.Ring.Root.position = s.Cross;
                Vector3 head = VRRig.I.HeadWorldPos;
                s.Ring.Root.rotation = Quaternion.LookRotation(s.Toward.sqrMagnitude > 1e-4f ? s.Toward : (s.Cross - head).normalized, Vector3.up);
                // stopped by the result itself (the audit, item 1: a hit's grey has g 0.6 and played the green pop)
                bool stopped = s.Result == "BLOCKED" || s.Result == "DEFLECTED";
                string res = s.Result == null ? null : stopped ? "ok" : "fade";
                s.Ring.Draw(now - s.RingAt, fill, s.Valid, false, res, s.Result == null ? 0f : now - s.ResultAt, s.RingRadius,
                            IndicatorLooks.ShieldRing.ColourOf(CombatSettings.Look, Premonition.Cyan));
            }
            else if (s.Ring != null && s.Ring.Root.gameObject.activeSelf) s.Ring.Root.gameObject.SetActive(false);
        }

        // `groundarrow <fill> red|gold|off [enemy]`: a still ground arrow from that enemy (default the nearest) to the player at
        // that fill, with the circle at chest height for gold (the pictures of the ruling of 17:40)
        static IndicatorLooks.GroundArrow s_demoArrow;
        static IndicatorLooks.ShieldRing s_demoRing;
        public static bool DemoShown => s_demoArrow != null;

        public static string DemoArrow(string[] a)
        {
            if (s_demoArrow != null) { s_demoArrow.Destroy(); s_demoArrow = null; }
            if (s_demoRing != null) { s_demoRing.Destroy(); s_demoRing = null; }
            if (a.Length < 3 || a[2] == "off" || VRRig.I == null || PlayerController.Instance == null) return "groundarrow off";
            Targetable t = Physical.FindEnemy(a.Length > 3 ? a[3] : "nearest");
            if (t == null) return "no enemy";
            float fill = float.Parse(a[1], Inv);
            bool red = a[2] == "red";
            Vector3 from = t.transform.position, to = PlayerController.Instance.transform.position;
            s_demoArrow = new IndicatorLooks.GroundArrow("HoF2VR_DemoGroundArrow");
            s_demoArrow.Draw(from, to, 1f, fill, red);
            if (!red)
            {
                Vector3 head = VRRig.I.HeadWorldPos, toward = from - head;
                toward.y = 0f;
                toward.Normalize();
                Vector3 c = head + toward * 0.4f + Vector3.down * 0.3f;
                s_demoRing = new IndicatorLooks.ShieldRing("HoF2VR_DemoShotRing");
                s_demoRing.Root.position = c;
                s_demoRing.Root.rotation = Quaternion.LookRotation(toward, Vector3.up);
                s_demoRing.Draw(1f, fill, false, false, null, 0f, ShotRing,
                                IndicatorLooks.ShieldRing.ColourOf(CombatSettings.Look, Premonition.Cyan));
            }
            return "groundarrow " + (red ? "red" : "gold") + " fill " + fill + " from " + WeaponGrips.ModelKey(t.name);
        }

        void Make(Shot s)
        {
            if (s_base == null) s_base = new Material(Shader.Find("Sprites/Default")) { renderQueue = 3150 };
            var root = new GameObject("HoF2VR_ShotMarker").transform;
            s.Bar = root;
            s.BarMat = new Material(s_base);
            s.FillMat = new Material(s_base);
            CombatFeel.Quad(root, s.BarMat, new Vector3(Guard.BarLength + 0.03f, 0.06f, 1f));
            s.Fill = CombatFeel.Quad(root, s.FillMat, new Vector3(Guard.BarLength, 0.035f, 1f));
            CombatFeel.Quad(root, s.FillMat, new Vector3(0.012f, 0.1f, 1f)).localPosition = new Vector3(-Guard.BarLength * 0.5f, 0f, -0.001f);
            CombatFeel.Quad(root, s.FillMat, new Vector3(0.012f, 0.1f, 1f)).localPosition = new Vector3(Guard.BarLength * 0.5f, 0f, -0.001f);
            Markers++;
        }

        static void Close(Shot s)
        {
            if (s.Ring != null) { s.Ring.Destroy(); s.Ring = null; }
            if (s.GroundMark != null) { s.GroundMark.Destroy(); s.GroundMark = null; }
            if (s.Bar != null) Destroy(s.Bar.gameObject);
            if (s.BarMat != null) Destroy(s.BarMat);
            if (s.FillMat != null) Destroy(s.FillMat);
            s.Bar = null;
        }

        // ---------------------------------------------------------------------------------------- test seams

        public static void Register()
        {
            // `shots? [reset|log]`: the shield against shots - counts, and the live shots (who, ETA, blockable, shield on it)
            Diag.Commands["shots?"] = a =>
            {
                if (a.Length > 1 && a[1] == "reset") { ResetCounts(); return "reset"; }
                if (a.Length > 1 && a[1] == "log") Plugin.L.LogInfo("ShieldShots log:\n  " + Log());
                var sb = new System.Text.StringBuilder(Describe());
                if (I != null)
                    foreach (Shot s in I.m_shots)
                        sb.Append(" | ").Append(s.Who).Append(s.Lob ? " lob" : " prj").Append(" eta ").Append(s.Eta.ToString("0.00", Inv)).Append(" blockable ").Append(s.Blockable)
                          .Append(" valid ").Append(s.Valid).Append(" cross ").Append(Diag.V(s.Cross)).Append(s.Result != null ? " " + s.Result : "")
                          .Append(" bladeOK ").Append(s.BladeOK).Append(" shieldOK ").Append(s.ShieldOK).Append(" onShield ").Append(s.ValidShield).Append(" onBlade ").Append(s.ValidBlade)
                          .Append(" bar ").Append(s.Bar != null && s.Bar.gameObject.activeSelf).Append(" ring ").Append(s.Ring != null && s.Ring.Root.gameObject.activeSelf)
                          .Append(" ground ").Append(s.GroundMark != null && s.GroundMark.Root.gameObject.activeSelf);
                PlayerController pc = PlayerController.Instance;
                if (pc != null) sb.Append(" | shield ").Append(pc.Inventory.Shield != null ? WeaponGrips.ModelKey(pc.Inventory.Shield.name) : "none").Append(" defend ").Append(pc.Combat.Defend);
                return sb.ToString();
            };
            // `shotguard`: the xrsim `move` that puts the shield's centre where the next shot will cross (like `guardat`)
            Diag.Commands["shotguard"] = a =>
            {
                if (I == null || VRRig.I == null) return "no shots";
                Vector3 c, n;
                float r;
                if (!Guard.Shield(out c, out n, out r)) return "no shield";
                foreach (Shot s in I.m_shots)
                {
                    if (s.Result != null || s.Eta < 0f) continue;
                    Vector3 d = VRRig.I.FromWorldDir(s.Cross - c);
                    bool left = !Settings.LeftHanded;
                    return string.Format(Inv, "move {0} {1:0.####} {2:0.####} {3:0.####} | eta {4:0.00}", left ? "l" : "r", d.x, d.y, -d.z, s.Eta);
                }
                return "no shot";
            };
            // `shotblade`: the xrsim move that puts the weapon hand's blade (its middle) on the next shot's crossing point
            Diag.Commands["shotblade"] = a =>
            {
                if (I == null || VRRig.I == null) return "no shots";
                Vector3 b, t;
                int id;
                if (!BladeHits.BladeAt(0, out b, out t, out id)) return "no blade";
                Vector3 mid = (b + t) * 0.5f;
                foreach (Shot s in I.m_shots)
                {
                    if (s.Result != null || s.Eta < 0f) continue;
                    Vector3 d = VRRig.I.FromWorldDir(s.Cross - mid);
                    return string.Format(Inv, "move {0} {1:0.####} {2:0.####} {3:0.####} | eta {4:0.00}", Settings.LeftHanded ? "l" : "r", d.x, d.y, -d.z, s.Eta);
                }
                return "no shot";
            };
            Diag.Commands["groundarrow"] = a => DemoArrow(a);
            Diag.Commands["shotat"] = a =>
            {
                if (I == null) return "no shots";
                foreach (Shot s in I.m_shots)
                    if (s.Result == null && s.Eta >= 0f) return "cross " + Diag.V(s.Cross) + " toward " + Diag.V(s.Toward) + " eta " + s.Eta.ToString("0.00", Inv) + " blockable " + s.Blockable;
                return "none";
            };
        }
    }
}

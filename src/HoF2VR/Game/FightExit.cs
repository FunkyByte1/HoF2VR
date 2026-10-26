using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Leaving a fight is the player's own action (user decision; docs/loading-design.md §4.4): when the
    /// last enemy falls, the game's exit (CombatTask.ExitCombat -> GameState_Level_Out) is held back and
    /// the game's own purple swirl (the wormhole the Dealer pulls the player into; ExitPortal) opens at
    /// chest height about 2.3 m away on walkable ground, in sight. Walking into it, touching it, or
    /// pointing at it and pulling the trigger lets the game leave as usual (into the waiting space). The state stays Level_Play meanwhile: walking, the HUD and the pause menu keep working.
    /// Guards: the hero takes no damage while the card waits; Forfeit in the pause menu leaves through
    /// the card instead of failing the run; holding A for a second leaves too (after a hint at 20 s);
    /// anything unexpected (the game ends the fight another way, the state changes, an error) drops the
    /// hold and the game's own flow carries on. Trap/escape levels (the game's own exit portal) and the
    /// final boss (its own exit code) are left alone. Setting Comfort.PlayerFightExit.
    internal class FightExit : MonoBehaviour
    {
        public static FightExit I;
        static CombatTask s_task;
        static CombatEncounter s_enc;
        static bool s_bypass;
        static float s_since;
        static int s_resets;       // ResetGuard.Resets when the exit was held
        public static bool Pending => s_task != null;
        public static int Deferred, Released, Dropped;   // diagnostics
        public static string Last = "-";
        const float SpawnDelay = 1.5f, HintAfter = 20f, HoldA = 1.0f;
        ExitPortal m_portal;
        Transform m_card;          // the portal's root (kept under the old name for the seams)
        float m_spawnedAt = -1f, m_aSince = -1f, m_nextHint, m_headZ = -1f;
        Vector3 m_base;            // the visual ground under the portal

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(CombatTask), "ExitCombat"), prefix: new HarmonyMethod(typeof(FightExit), nameof(DeferExit)));
            h.Patch(AccessTools.Method(typeof(Game), "Forfeit"), prefix: new HarmonyMethod(typeof(FightExit), nameof(OnForfeit)));
            h.Patch(AccessTools.Method(typeof(Destroyable), "ApplyDamage"), prefix: new HarmonyMethod(typeof(FightExit), nameof(BlockHeroDamage)));
            // loot after a win as in the flat game (user decision): there the fight ends at the last kill, so
            // what the last enemy drops is never picked up
            h.Patch(AccessTools.Method(typeof(Loot), "OnTriggerEnter"), prefix: new HarmonyMethod(typeof(FightExit), nameof(NoLootAfterWin)));
        }

        static bool NoLootAfterWin() => !Pending;

        void Awake()
        {
            I = this;
            Diag.StatusExtras["fightExit"] = () => Diag.Q(Pending ? "pending " + (Time.unscaledTime - s_since).ToString("0.0") + " s" + (m_card != null ? " card " + Diag.V(m_base) + " hero dist " + HeroDist().ToString("0.00") : "") : "-") ;
            Diag.StatusExtras["fightExitCounts"] = () => Diag.Q(Deferred + "/" + Released + "/" + Dropped + " last " + Last);
            // `exitcard [touch|walk|info]`: the pending exit (tests); touch = as if the card were touched,
            // walk = the hero stands on the card (moves the hero's transform: test runs only)
            Diag.Commands["exitcard"] = a =>
            {
                string what = a.Length > 1 ? a[1] : "info";
                if (what == "probe") return Probe(a.Length > 2 ? int.Parse(a[2]) : 12);
                if (what == "last") return Spawns + " portals; last: " + LastSpawn;
                if (!Pending) return "no exit pending (" + Last + ")";
                if (what == "touch") { Release("touch (seam)"); return "released"; }
                // `exitcard signout` (test runs): the game's sign-out (its Quit to title, the Scenarios' reload) while the exit waits -
                // the reset that ended playtest 4's fight with "release failed"
                if (what == "signout")
                {
                    if (!TestMode.Enabled) return "test runs only";
                    Defiant.Platform.IPlatform p = Defiant.Platform.PlatformLocator.GetCurrentPlatform();
                    if (p == null) return "no platform";
                    Time.timeScale = 1f;
                    p.SignOut();
                    return "signed out with the exit pending";
                }
                // `exitcard look Orb|Swirl|Rift`: the open portal built again at the same spot in that look (the options'
                // captures); `exitcard light on|off`: its light (frame-time checks); `exitcard face`: the head's bearing to it
                if (what == "look" && a.Length > 2 && m_portal != null)
                {
                    Settings.PortalStyle look;
                    try { look = (Settings.PortalStyle)Enum.Parse(typeof(Settings.PortalStyle), a[2], true); }
                    catch (Exception) { return "no look " + a[2]; }
                    Settings.OverridePortalLook = look;
                    Vector3 centre = m_portal.Root.position, ground = m_portal.Ground;
                    Vector3 head = VRRig.I != null ? VRRig.I.HeadWorldPos : centre;
                    m_portal.Destroy();
                    m_portal = ExitPortal.Build(centre, ground, head);
                    m_card = m_portal.Root;
                    return "portal rebuilt as " + look;
                }
                if (what == "light" && a.Length > 2 && m_portal != null) { m_portal.LightOn(a[2] == "on"); return "portal light " + a[2]; }
                // `exitcard near <m>`: the hero stands that far from the portal, facing it (test runs: captures from close by)
                if (what == "near" && a.Length > 2 && TestMode.Enabled && m_portal != null && PlayerController.Instance != null)
                {
                    float dist = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                    Transform hero = PlayerController.Instance.transform;
                    Vector3 d = m_base - hero.position;
                    d.y = 0f;
                    if (d.sqrMagnitude < 1e-4f) return "on it";
                    Vector3 to = m_base - d.normalized * dist;
                    to.y = hero.position.y;
                    hero.position = to;
                    return "hero " + dist.ToString("0.0") + " m from the portal";
                }
                if (what == "face" && m_portal != null && VRRig.I != null)
                {
                    Vector3 d = m_portal.Root.position - VRRig.I.HeadWorldPos;
                    float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, head = VRRig.I.HeadWorldRot.eulerAngles.y;
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "turn {0:0} deg, {1:0.0} m, pitch {2:0} deg",
                        Mathf.DeltaAngle(head, yaw), new Vector2(d.x, d.z).magnitude / Mathf.Max(0.01f, VRRig.I.Scale), -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg);
                }
                if (what == "forfeit") { Game.Instance.Forfeit(); return "forfeit called (" + Last + ")"; }
                if (what == "walk" && TestMode.Enabled && m_card != null && PlayerController.Instance != null)
                {
                    PlayerController.Instance.transform.position = m_base + Vector3.up * 0.05f;
                    return "hero moved onto the card";
                }
                string bearing = "-";
                if (m_card != null && PlayerController.Instance != null && CombatVR.I != null)
                {
                    Vector3 d = m_base - PlayerController.Instance.transform.position;
                    bearing = Mathf.DeltaAngle(CombatVR.I.Yaw, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg).ToString("0");
                }
                return "pending " + (Time.unscaledTime - s_since).ToString("0.0") + " s card " + (m_card != null ? Diag.V(m_base) : "-") + " hero dist " + HeroDist().ToString("0.00") + " bearing " + bearing;
            };
            HandWorld.Targets["exitcard"] = () => m_card != null ? m_card.position : (Vector3?)null;
        }

        /// Test seam `exitcard probe N` (any time in a fight): the portal placement from N stand points - the hero's
        /// own spot with the head turned round, and walkable points 3-7 m around - without building anything.
        /// Logs each and returns a summary: how many found ground in sight, distances, chest height, the lowest
        /// ground clearance of the disc + vortex volume (negative = in the ground).
        static string Probe(int n)
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null || VRRig.I == null) return "no hero";
            Vector3 hero0 = pc.transform.position;
            float eye = VRRig.I.EyeHeight;
            int inSight = 0, fallback = 0, made = 0;
            float minClear = float.MaxValue, minD = float.MaxValue, maxD = 0f, minChest = float.MaxValue, maxChest = 0f;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
            {
                Vector3 hero = hero0;
                float yaw = i * 360f / Mathf.Max(1, n);
                if (i % 2 == 1)
                {
                    // a walkable point a few metres away (another part of the arena)
                    Vector3 p = hero0 + Quaternion.Euler(0f, yaw * 1.7f, 0f) * Vector3.forward * (3f + (i % 5));
                    NavMeshHit nh;
                    if (!NavMesh.SamplePosition(p, out nh, 1.5f, NavMesh.AllAreas)) continue;
                    hero = nh.position;
                }
                Vector3 head = hero + Vector3.up * eye;
                Vector3 centre, ground;
                string how;
                bool ok = ExitPortal.FindSpot(hero, head, yaw, eye, out centre, out ground, out how);
                made++;
                if (!ok) { fallback++; sb.Append(" [" + i + " " + how + "]"); continue; }
                Vector3 away = centre - head; away.y = 0f;
                Quaternion face = Quaternion.LookRotation(away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward);
                float clear = ExitPortal.Clearance(centre, face);
                Vector3 flat = ground - hero; flat.y = 0f;
                float chest = centre.y - hero.y;
                if (how.Contains(", in sight")) inSight++;   // (the "how" says "navmesh, N deg from the view, ..., in sight")
                minClear = Mathf.Min(minClear, clear); minD = Mathf.Min(minD, flat.magnitude); maxD = Mathf.Max(maxD, flat.magnitude);
                minChest = Mathf.Min(minChest, chest); maxChest = Mathf.Max(maxChest, chest);
                Plugin.L.LogInfo("exit probe " + i + ": hero " + Diag.V(hero) + " yaw " + yaw.ToString("0") + " -> " + Diag.V(centre) + " (" + how + "), chest " + chest.ToString("0.00") +
                                 " m, clearance " + clear.ToString("0.00") + " m");
            }
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            string res = "probe " + scene + ": " + made + " stand points, in sight " + inSight + ", fallback " + fallback +
                         (made > fallback ? ", dist " + minD.ToString("0.0") + "-" + maxD.ToString("0.0") + " m (" + Settings.PortalLook + ", radius " + ExitPortal.RadiusOf(Settings.PortalLook).ToString("0.0") +
                                            " m: the near edge " + (minD - ExitPortal.RadiusOf(Settings.PortalLook)).ToString("0.0") + " m or more), centre " + minChest.ToString("0.00") + "-" + maxChest.ToString("0.00") +
                                            " m, lowest clearance " + minClear.ToString("0.00") + " m" : "") + sb;
            Plugin.L.LogInfo("exit " + res);
            return res;
        }

        // ---- the hold ------------------------------------------------------------------------------

        static bool DeferExit(CombatTask __instance, CombatEncounter a_combatEncounter)
        {
            if (s_bypass) return true;
            try
            {
                if (s_task != null)
                {
                    // the game ends the fight another way (a scripted fail, a death): its flow wins
                    Drop("the game called ExitCombat again");
                    return true;
                }
                string why = Ineligible(__instance, a_combatEncounter);
                if (why != null)
                {
                    if (XRSettings.enabled && Settings.PlayerFightExit) Plugin.L.LogInfo("fight exit: the game's own exit (" + why + ")");
                    return true;
                }
                s_task = __instance;
                s_enc = a_combatEncounter;
                s_since = Time.unscaledTime;
                s_resets = ResetGuard.Resets;
                Deferred++;
                Last = "deferred";
                if (I != null) I.Begin();
                FightFlow.CalmMusic();
                Plugin.L.LogInfo("fight exit: won - the exit waits for the player (card in " + SpawnDelay + " s)");
                return false;
            }
            catch (Exception e)
            {
                Plugin.L.LogError("fight exit: " + e);
                s_task = null;
                s_enc = null;
                return true;
            }
        }

        /// Why the game's own exit is used (null = the player leaves through the card).
        static string Ineligible(CombatTask task, CombatEncounter enc)
        {
            if (!XRSettings.enabled || !Settings.PlayerFightExit) return "off";
            if (GameWatch.State != "GameState_Level_Play") return "state " + GameWatch.State;
            if (enc == null || enc.EncounterCompleteContext == null || !enc.EncounterCompleteContext.Success) return "not a win";
            foreach (CombatObjective o in task.Objectives)
                if (o is CombatObjectiveExitTrigger) return "the level has its own exit";
            if (PlayerController.Instance == null || Player.Instance == null || Player.Instance.Health.Get() <= 0f) return "no hero";
            if (EnemyAlive()) return "enemies still alive";
            if (!CombatVR.Active) return "not in first person";
            return null;
        }

        static bool EnemyAlive()
        {
            CombatManager cm = CombatManager.Instance;
            if (cm == null || cm.Targets == null) return false;
            foreach (Targetable t in cm.Targets)
                if (t != null && t.Team == TeamType.Enemy && t.Destroyable != null && !t.Destroyable.IsDead && !t.Hidden && t.gameObject.activeInHierarchy) return true;
            return false;
        }

        /// Leave now: the game's own exit, with the glory camera skipped (third-person slow motion after
        /// the player chose to leave would feel wrong; its camera part is ignored in VR anyway).
        static void Release(string how)
        {
            if (s_task == null) { if (I != null) I.End(false); return; }
            string gone = ExitGone();
            if (gone != null) { Drop(gone + " (wanted: " + how + ")"); return; }
            CombatTask task = s_task;
            CombatEncounter enc = s_enc;
            s_task = null;
            s_enc = null;
            if (I != null) I.End(true);
            Released++;
            Last = how;
            Plugin.L.LogInfo("fight exit: leaving by " + how + " after " + (Time.unscaledTime - s_since).ToString("0.0") + " s");
            try
            {
                if (enc != null && enc.EncounterCompleteContext != null) enc.EncounterCompleteContext.FinalObject = null;
                s_bypass = true;
                AccessTools.Method(typeof(CombatTask), "ExitCombat").Invoke(task, new object[] { enc });
            }
            catch (Exception e) { Plugin.L.LogError("fight exit: release failed: " + e); }
            finally { s_bypass = false; }
        }

        /// Why the game's own exit can no longer run (null = it can). Playtest 4 (2026-10-19 22:18, "fight exit: release failed",
        /// a NullReferenceException in CombatTask.ExitCombat): with the portal waiting, a Dev fight picked from the pause menu
        /// reset the game (ResetGuard: Game.ClearScene destroys the fight, the hero, the table); the next frame found no hero and
        /// released through the game's exit, which reached for the destroyed EquipmentStats / CardTableManager. Then there is
        /// nothing left to exit: the hold is dropped and the game's reset carries on.
        static string ExitGone()
        {
            if (ResetGuard.Resets != s_resets) return "the game reset under the held exit";
            if (Game.Instance == null) return "no Game";
            if (EquipmentStats.Instance == null) return "no EquipmentStats (the fight's scene is gone)";
            if (CardTableManager.Instance == null) return "no CardTableManager (the table is gone)";
            if (s_enc == null) return "no encounter";
            return null;
        }

        /// Give up the hold without leaving (the game has moved on by itself).
        static void Drop(string why)
        {
            if (s_task == null) return;
            s_task = null;
            s_enc = null;
            Dropped++;
            Last = "dropped: " + why;
            if (I != null) I.End(false);
            Plugin.L.LogWarning("fight exit: hold dropped (" + why + ")");
        }

        static bool OnForfeit()
        {
            if (!Pending) return true;
            Release("forfeit (pause menu)");
            return false;
        }

        static bool BlockHeroDamage(Destroyable __instance)
        {
            if (!Pending) return true;
            PlayerController pc = PlayerController.Instance;
            return pc == null || __instance == null || __instance.gameObject != pc.gameObject;
        }

        // ---- the card ------------------------------------------------------------------------------

        void Begin()
        {
            m_spawnedAt = -1f;
            m_aSince = -1f;
            m_nextHint = Time.unscaledTime + HintAfter;
        }

        void End(bool used)
        {
            if (m_portal != null)
            {
                m_portal.Destroy();
                m_portal = null;
            }
            m_card = null;
            if (used)
            {
                Haptics.Event("exit", false, 0.6f, 0.05f);
                Haptics.Event("exit", true, 0.6f, 0.05f);
            }
            if (VRHelp.I != null && m_hinted) VRHelp.I.Hide();
            m_hinted = false;
        }

        bool m_hinted;

        float HeroDist()
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null || m_card == null) return -1f;
            Vector3 d = pc.transform.position - m_base;
            d.y = 0f;
            return d.magnitude;
        }

        void Update()
        {
            if (!Pending) return;
            try { Tick(); }
            catch (Exception e)
            {
                Plugin.L.LogError("fight exit: " + e);
                Release("error fallback");
            }
        }

        void Tick()
        {
            string st = GameWatch.State;
            if (st != "GameState_Level_Play" && st != "GameState_Level_Pause") { Drop("state " + st); return; }
            string gone = ExitGone();
            if (gone != null) { Drop(gone); return; }
            PlayerController pc = PlayerController.Instance;
            if (pc == null || !CombatVR.Active) { Release("fallback (" + (pc == null ? "no hero" : "first person off") + ")"); return; }
            float waited = Time.unscaledTime - s_since;
            if (m_card == null)
            {
                TrackMotion(pc.transform.position);
                if (waited >= SpawnDelay) Spawn(pc);
                return;
            }
            m_portal.Animate();
            if (st == "GameState_Level_Pause") return;
            // 1. walk into it (feet on its ground point, or the head through its face)
            if (VRRig.I != null && Mathf.Abs(pc.transform.position.y - m_base.y) < 1.5f && m_portal.WalkedIn(pc.transform.position, VRRig.I.HeadWorldPos, ref m_headZ)) { Release("walking into the portal"); return; }
            // 2. touch it, 3. point at it and pull the trigger
            foreach (Hand h in VRInput.LR)
            {
                if (!h.Tracked || !HandWorld.Valid(h)) continue;
                if (m_portal.Contains(TouchInput.TipOf(h)) || m_portal.Contains(HandWorld.Tip(h)) || m_portal.Contains(HandWorld.Palm(h))) { Release("touching the portal"); return; }
                if (h.TriggerDown && Settings.Lasers)   // pointing is the laser's way (Controls.Lasers, off by default since 2026-10-11)
                {
                    string miss;
                    if (m_portal.PointedAt(HandWorld.PointRay(h), out miss)) { Release("pointing at the portal"); return; }
                    Plugin.L.LogInfo("fight exit: trigger (" + (h.IsLeft ? "left" : "right") + ") not at the portal: " + miss);
                }
            }
            // the fallback: holding A (no text: the user, 2026-10-11, "remove the portal text. Its obvious what the portal is";
            // the controls card before the fight mentions the portal and the A hold)
            if (waited >= HintAfter && VRInput.R.A)
            {
                if (m_aSince < 0f) m_aSince = Time.unscaledTime;
                if (Time.unscaledTime - m_aSince >= HoldA) { Release("holding A"); return; }
            }
            else m_aSince = -1f;
            float auto = Settings.FightExitAutoSeconds;
            if (auto > 0f && waited >= auto) Release("auto return after " + auto + " s");
        }



        /// The portal: chest height, ~2.3 m away on walkable ground, in sight, never on the player or out
        /// of the arena (ExitPortal.FindSpot).
        void Spawn(PlayerController pc)
        {
            Vector3 hero = pc.transform.position;
            Vector3 head = VRRig.I != null ? VRRig.I.HeadWorldPos : hero + Vector3.up * 1.6f;
            float yaw = VRRig.I != null ? VRRig.I.HeadWorldRot.eulerAngles.y : (CombatVR.I != null ? CombatVR.I.Yaw : pc.transform.eulerAngles.y);
            float eye = VRRig.I != null ? VRRig.I.EyeHeight : 1.6f;
            Vector3 centre, ground;
            string how;
            ExitPortal.FindSpot(hero, head, yaw, eye, m_motion, out centre, out ground, out how);
            m_portal = ExitPortal.Build(centre, ground, head);
            m_card = m_portal.Root;
            m_base = ground;
            m_headZ = -1f;
            m_spawnedAt = Time.unscaledTime;
            Vector3 d = ground - hero;
            float bearing = Mathf.DeltaAngle(yaw, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
            Plugin.L.LogInfo("fight exit: portal at " + Diag.V(centre) + " (" + how + "), " + HeroDist().ToString("0.0") + " m from the hero, " + bearing.ToString("0") + " deg from the view, centre " +
                             (centre.y - ground.y).ToString("0.00") + " m above the ground, chest " + (centre.y - hero.y).ToString("0.00") + " m above the feet, lowest edge clears the ground by " +
                             ExitPortal.Clearance(centre, m_card.rotation).ToString("0.00") + " m, in sight " + ExitPortal.InSight(head, centre));
            LastSpawn = "portal " + HeroDist().ToString("0.0") + " m away, " + bearing.ToString("0") + " deg from the view, player moving " + new Vector3(m_motion.x, 0f, m_motion.z).magnitude.ToString("0.0") +
                        " m/s, " + (m_motion.sqrMagnitude > 0.09f ? Vector3.Angle(new Vector3(d.x, 0f, d.z), new Vector3(m_motion.x, 0f, m_motion.z)).ToString("0") + " deg off the walking direction" : "standing") +
                        " | " + how;
            Spawns++;
            // where it opened relative to the player's view (coordinator 2026-10-12: how often does it land behind?)
            float ab = Mathf.Abs(bearing);
            string where = ab <= 50f ? "in view" : ab <= 110f ? "to the side" : "behind";
            if (ab <= 50f) InView++; else if (ab <= 110f) ToSide++; else Behind++;
            Plugin.L.LogInfo("fight exit: the portal opened " + where + " (" + bearing.ToString("0") + " deg; so far in view " + InView + ", to the side " + ToSide + ", behind " + Behind + ")");
        }
        public static int InView, ToSide, Behind;

        // the player's walking velocity over the last ~0.3 s (the walking-path rule of the portal's spot)
        Vector3 m_motion, m_lastPos;
        float m_lastT = -1f;
        public static int Spawns;
        public static string LastSpawn = "-";

        void TrackMotion(Vector3 p)
        {
            float t = Time.unscaledTime;
            if (m_lastT > 0f && t - m_lastT > 1e-3f && t - m_lastT < 0.5f)
            {
                Vector3 v = (p - m_lastPos) / (t - m_lastT);
                m_motion = Vector3.Lerp(m_motion, v, 1f - Mathf.Exp(-(t - m_lastT) / 0.15f));
            }
            else m_motion = Vector3.zero;   // a new fight (or a long frame): start over
            m_lastPos = p;
            m_lastT = t;
        }
    }
}

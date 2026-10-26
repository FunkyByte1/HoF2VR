using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// The enemies' guard and health in fights (VR), `[Combat] EnemyStatus` (the user's pick of 2026-10-18 ~22:00, mockups
    /// captures/hud-options-2026-10-18/; "Pips go: I didn't know about enemies having guards... get rid of pips and use a
    /// different system"): Shell = a glowing shell on the enemy's own body (EnemyShell), Rings = two rings at its feet
    /// (EnemyRings). The pips and the slim bar beside the head (Until You Fall's meter, combat Deliverable 1c) are gone.
    ///  * Which enemies (CombatSettings.Meters, `[Combat] EnemyHealthBars`): On = every enemy; Engaged = the enemy holding the
    ///    attack slot and enemies hurt in the last 5 s; Off = none. From the moment an enemy is up (not while it rises out of
    ///    the ground) until 1.5 s after its death (fading), within 30 m.
    ///  * The guard (Physical mode) comes from Poise.View: its share left, the share still glowing from the last chip, the
    ///    phase (Up, Broken: crits open, Recover: knocked back, then refilling). Classic mode has no guard: health alone.
    ///  * Playtest 3 mark 10 (in both styles): an enemy no weapon can kill (the first challenge's tutorial skeletons, 300000
    ///    health) shows one bomb mark beside its head instead (placed as the old meter was: the right side of the head from the
    ///    player's view, never inside any body), and no shell or rings.
    /// The game's own floating bars are projected for a flat screen and stay hidden in VR (CombatHud).
    internal class EnemyBars : MonoBehaviour
    {
        sealed class Meter
        {
            public Targetable T;
            public Destroyable D;
            public Controller C;
            public GameObject Go;        // the bomb mark
            public Mesh Mesh;
            public Transform HeadBone;
            public bool HeadChecked;
            public readonly List<Renderer> Body = new List<Renderer>();
            public float NextBodyScan;
            public Vector3 Pos, Vel;
            public bool Placed;
            public bool BombOnly;        // playtest 3, mark 10: no weapon can kill it (the tutorial's skeletons, 300000 health): one bomb mark
            public float LastHealth = -1f, LastHurt = -100f;
            public float Born, DeadSince = -1f, Alpha;
            public long Built = long.MinValue;
            public Bounds Live;          // the live body bounds this frame
            public bool HasLive;
            public float Side;           // the push applied this frame (diagnostics)
            public float Gap = 99f;      // smallest gap to any enemy's live bounds this frame (diagnostics)
            public float Height;         // above the feet (diagnostics)
            public Vector3 HeadPt;       // the head point this frame
            public float ReachR, ReachL, Top;
            public Collider Body0;       // the enemy's own collider (TargetableShape)
            public EnemyShell Shell;
            public EnemyRings Rings;
            public float Guard = 1f, Glow = 1f, Health = 1f, Hold;
            public Poise.Phase Ph;
            public bool Guarded;
            public bool HadGuard;        // Poise has had a state for it (its guard was read at least once)
        }

        public static float BombW = 0.062f, BombH = 0.062f;
        public static float Zoom = 1f;   // test seam `enemybars zoom k` (shots only)
        readonly Dictionary<int, Meter> m_meters = new Dictionary<int, Meter>();
        readonly List<int> m_gone = new List<int>();
        Material m_mat;
        float m_nextScan;
        CombatSettings.EnemyStatusStyle m_style;
        static readonly List<Vector3> s_v = new List<Vector3>();
        static readonly List<Color> s_c = new List<Color>();
        static readonly List<int> s_t = new List<int>();
        static readonly List<Renderer> s_rend = new List<Renderer>();

        // one clean hit of the player's weapon (health), learned from the player's hits on enemies (the bomb-only rule)
        static readonly List<float> s_hits = new List<float>();
        public static int HitsSeen, StyleSwitches;

        public static void Apply(Harmony h)
        {
            EnemyShell.Apply(h);
            try
            {
                h.Patch(AccessTools.Method(typeof(Destroyable), "ApplyDamage"), prefix: new HarmonyMethod(typeof(EnemyBars), nameof(BeforeDamage)),
                    postfix: new HarmonyMethod(typeof(EnemyBars), nameof(AfterDamage)));
            }
            catch (Exception e) { Plugin.L.LogWarning("EnemyBars: damage patch failed: " + e.Message); }
        }

        static void BeforeDamage(Destroyable __instance, out float __state) { __state = __instance != null ? __instance.CurrentHealth : 0f; }

        static void AfterDamage(Destroyable __instance, Destroyable.Damage a_damage, float __state)
        {
            try
            {
                if (__instance == null || !CombatVR.Active) return;
                float lost = __state - __instance.CurrentHealth;
                if (lost <= 0.01f || __instance.GetComponent<PlayerController>() != null) return;
                PlayerController pc = PlayerController.Instance;
                Targetable src = a_damage.Container != null ? a_damage.Container.Source : null;
                if (pc == null || src == null || src.gameObject != pc.gameObject) return;
                if (__instance.CurrentHealth <= 0f && lost < __state - 0.01f) return;   // a killing blow says little
                s_hits.Add(lost);
                if (s_hits.Count > 12) s_hits.RemoveAt(0);
                HitsSeen++;
            }
            catch (Exception) { }
        }

        static float HitDamage()
        {
            if (s_hits.Count >= 3)
            {
                var c = new List<float>(s_hits);
                c.Sort();
                return Mathf.Max(1f, c[c.Count / 2]);
            }
            PlayerController pc = PlayerController.Instance;
            float d = 0f;
            try { if (pc != null) d = pc.CalculateAttackDamage(); } catch (Exception) { }
            return Mathf.Max(1f, d);
        }

        void Awake()
        {
            Diag.Commands["enemybars"] = a =>
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                // `enemybars zoom <k>`: the bomb mark k times its size (close-up shots in the simulator's small eye images)
                if (a.Length > 2 && a[1] == "zoom") { Zoom = Mathf.Clamp(float.Parse(a[2], inv), 0.5f, 8f); return "zoom " + Zoom; }
                return "meters " + m_meters.Count + " mode " + CombatSettings.Meters + " style " + CombatSettings.EnemyStatus + " hit " + HitDamage().ToString("0.0") + " (" + HitsSeen + " seen)";
            };
            // `bars?`: every enemy: its style's state (shell: whole / cracked N / breaking / broken / reforming k / refilling,
            // rings: the shares shown), guard, phase and health; a bomb mark: where it is, its height and its gap to any body
            Diag.Commands["bars?"] = a =>
            {
                var sb = new StringBuilder();
                int shown = 0, inside = 0, shells = 0, rings = 0, bombs = 0;
                float minGap = 99f;
                foreach (Meter m in m_meters.Values)
                {
                    if (m.Alpha <= 0.01f) continue;
                    shown++;
                    sb.Append(" | ").Append(m.T != null ? m.T.name : "?").Append(" #").Append(m.T != null ? m.T.GetInstanceID() : 0);
                    if (m.BombOnly)
                    {
                        bombs++;
                        if (m.Gap <= 0f) inside++;
                        minGap = Mathf.Min(minGap, m.Gap);
                        sb.Append(" bombOnly at ").Append(Diag.V(m.Pos)).Append(" h ").Append(m.Height.ToString("0.00")).Append(" gap ").Append(m.Gap.ToString("0.00"));
                        continue;
                    }
                    sb.Append(" guard ").Append(m.Guarded ? m.Guard.ToString("0.00") : "-").Append(" glow ").Append(m.Glow.ToString("0.00")).Append(" phase ").Append(m.Guarded ? m.Ph.ToString() : "-")
                      .Append(" health ").Append(m.Health.ToString("0.00"));
                    if (m.Shell != null)
                    {
                        shells++;
                        sb.Append(" shell ").Append(m.Shell.State).Append(" parts ").Append(m.Shell.Visible).Append("/").Append(m.Shell.Parts).Append(m.Shell.Pending > 0 ? " pending " + m.Shell.Pending : "")
                          .Append(" bright ").Append(m.Shell.Brightness.ToString("0.00")).Append(" near ").Append(m.Shell.Near.ToString("0.00"))
                          .Append(" arms ").Append(m.Shell.ArmsSunk.ToString("0.00")).Append(" thick ").Append((m.Shell.Thick * 100f).ToString("0.0")).Append("cm rim ").Append(m.Shell.RimShown ? "on " + m.Shell.RimStrength.ToString("0.000") : EnemyShell.GameRimOn(m.C != null ? m.C.Model : null) ? "game" : "off").Append(" (").Append(m.Shell.RimMats).Append(" mats)");
                    }
                    if (m.Rings != null && m.Rings.Shown)
                    {
                        rings++;
                        sb.Append(" rings r ").Append(m.Rings.Radius.ToString("0.00")).Append(" guard ").Append(m.Rings.ShownGuard.ToString("0.00")).Append(" health ").Append(m.Rings.ShownHealth.ToString("0.00"))
                          .Append(m.Rings.Flashing ? " flashing" : "").Append(m.Rings.LowHealth ? " low" : "").Append(" ground ").Append(m.Rings.GroundHits).Append("/12");
                    }
                }
                return shown + " shown (" + shells + " shells, " + rings + " rings, " + bombs + " bomb marks), inside " + inside + ", min gap " + (bombs > 0 ? minGap.ToString("0.00") : "-") +
                       " m, mode " + CombatSettings.Meters + " style " + CombatSettings.EnemyStatus + sb;
            };
            // `enemyhp <enemy|nearest> <share>`: that enemy's health set to a share of its most (tests and pictures)
            Diag.Commands["enemyhp"] = a =>
            {
                if (!Scenarios.SeamsAllowed) return "test mode or the dev profile only";
                Targetable t = Physical.FindEnemy(a.Length > 1 ? a[1] : "nearest");
                Destroyable d = t != null ? t.GetComponent<Destroyable>() : null;
                if (d == null || a.Length < 3) return "no enemy";
                // `enemyhp <enemy> kill`: the game's own kill (its death, fall and fade: the shell / rings during the fade)
                if (a[2] == "kill")
                {
                    PlayerController pc = PlayerController.Instance;
                    d.ForceKill(pc != null && pc.Inventory != null ? pc.Inventory.Weapon : null);
                    return t.name + " #" + t.GetInstanceID() + " killed (dead " + d.IsDead + ")";
                }
                float k = Mathf.Clamp(float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture), 0.01f, 1f);
                d.CurrentHealth = Mathf.Max(1f, d.MaxHealth.Value * k);
                return t.name + " #" + t.GetInstanceID() + " health " + d.CurrentHealth.ToString("0.0") + "/" + d.MaxHealth.Value.ToString("0");
            };
            Diag.Commands["shell"] = a =>
            {
                if (a.Length > 3 && a[1] == "tune") return EnemyShell.Tune(a[2], float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture));
                if (a.Length > 1 && a[1] == "parts")
                {
                    Targetable t = Physical.FindEnemy(a.Length > 2 ? a[2] : "nearest");
                    string r = t != null ? t.name + EnemyShell.PartsOf(t.Controller != null ? t.Controller.Model : null) : "no enemy";
                    Plugin.L.LogInfo("EnemyShell: parts of " + r);
                    return r;
                }
                return EnemyShell.Describe();
            };
        }

        Material Mat()
        {
            if (m_mat == null) m_mat = new Material(Shader.Find("Sprites/Default")) { renderQueue = 3100 };
            return m_mat;
        }

        Meter Make(Targetable t, Destroyable d)
        {
            float hit = HitDamage();
            float max = Mathf.Max(1f, d.MaxHealth.Value);
            int hits = Mathf.Clamp(Mathf.CeilToInt(max / hit - 0.05f), 1, 100000);
            var m = new Meter { T = t, D = d, C = t.Controller, Born = Time.unscaledTime };
            try { m.Body0 = t.Shape != null ? t.Shape.GetComponent<Collider>() : null; } catch (Exception) { }
            // the first challenge's tutorial skeletons (SkeletonNEW_Tutorial: 300000 health, 45000 at x0.15) die only to the
            // tutorial's holy bomb. Anything a weapon would need more than BombOnlyHits hits for shows one bomb mark instead.
            // (by health only: the weapon-ability tutorial's skeletons share the name and die to a weapon)
            m.BombOnly = hits >= BombOnlyHits;
            if (m.BombOnly)
            {
                BombOnlyMeters++;
                var go = new GameObject("HoF2VR_BombMark") { layer = 0 };
                var mesh = new Mesh { name = "HoF2VR_BombMark" };
                mesh.MarkDynamic();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = Mat();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                go.SetActive(false);
                m.Go = go;
                m.Mesh = mesh;
            }
            return m;
        }

        void LateUpdate()
        {
            CombatSettings.Meter mode = CombatSettings.Meters;
            EnemyShell.BuildBudget = 1;
            if (!CombatVR.Active || PlayerController.Instance == null || mode == CombatSettings.Meter.Off || VRRig.I == null)
            {
                if (m_meters.Count > 0) Clear();
                if (!CombatVR.Active && m_wasActive) { EnemyShell.ClearCache(); m_wasActive = false; }
                EnemyShell.ShardsTick();
                return;
            }
            m_wasActive = true;
            CombatSettings.EnemyStatusStyle style = CombatSettings.EnemyStatus;
            if (style != m_style)
            {
                // the style switched (the settings page, mid-fight): the other style's pieces go at once
                StyleSwitches++;
                foreach (Meter m in m_meters.Values)
                {
                    if (style == CombatSettings.EnemyStatusStyle.Shell && m.Rings != null) { m.Rings.Destroy(); m.Rings = null; }
                    if (style == CombatSettings.EnemyStatusStyle.Rings && m.Shell != null) { m.Shell.Destroy(); m.Shell = null; }
                }
                m_style = style;
            }
            float now = Time.unscaledTime;
            if (now >= m_nextScan)
            {
                m_nextScan = now + 0.25f;
                Targetable me = PlayerController.Instance.GetComponent<Targetable>();
                BetterList<Targetable> all = me.EnumerateWithinRadius(40f, me.GetEnemyTeams());
                for (int i = 0; i < all.size; i++)
                {
                    Targetable t = all[i];
                    if (t == null || m_meters.ContainsKey(t.GetInstanceID())) continue;
                    Destroyable d = t.GetComponent<Destroyable>();
                    if (d == null || d.IsDead) continue;
                    m_meters[t.GetInstanceID()] = Make(t, d);
                }
            }
            AIController attacker = null, ranged = null;
            try
            {
                AITarget at = PlayerController.Instance.GetComponent<Targetable>().AITarget;
                if (at != null) { attacker = at.MeleeAttacker; ranged = at.RangedAttacker; }
            }
            catch (Exception) { }

            Vector3 eye = VRRig.I.HeadWorldPos;
            bool physical = CombatSettings.Physical;
            // live bodies first: every bomb mark is tested against every enemy's body
            foreach (Meter m in m_meters.Values) LiveBody(m, now);
            m_gone.Clear();
            foreach (var kv in m_meters)
            {
                Meter m = kv.Value;
                if (m.T == null || m.D == null) { m_gone.Add(kv.Key); continue; }
                float hp = Mathf.Max(0f, m.D.CurrentHealth);
                if (m.LastHealth < 0f) m.LastHealth = hp;
                if (hp < m.LastHealth - 0.01f) m.LastHurt = now;
                m.LastHealth = hp;
                bool dead = m.D.IsDead || hp <= 0f;
                if (dead && m.DeadSince < 0f) m.DeadSince = now;
                if (m.DeadSince >= 0f && now - m.DeadSince > 1.5f) { m_gone.Add(kv.Key); continue; }
                m.Health = Mathf.Clamp01(hp / Mathf.Max(1f, m.D.MaxHealth.Value));

                bool want = !m.T.Hidden && m.HasLive && now - m.Born > 0.4f && Up(m);
                if (want && mode == CombatSettings.Meter.Engaged)
                {
                    AIController ai = m.C as AIController;
                    want = (ai != null && (ai == attacker || ai == ranged)) || now - m.LastHurt < 5f || m.DeadSince >= 0f;
                }
                if (want && (m.T.transform.position - eye).sqrMagnitude > 30f * 30f) want = false;
                float fadeTo = want ? (m.DeadSince >= 0f ? 1f - (now - m.DeadSince) / 1.5f : 1f) : 0f;
                m.Alpha = Mathf.MoveTowards(m.Alpha, fadeTo, Time.unscaledDeltaTime / 0.3f);
                if (m.DeadSince >= 0f) m.Alpha = Mathf.Min(m.Alpha, fadeTo);

                // the guard (Physical mode). A dead enemy keeps the guard it last showed (the audit of 2026-10-19, item 1): Poise
                // drops a dead enemy's state on its next frame, and View's defaults (a full guard, Up) grew the corpse's shell back
                // whole (Rings: a full blue ring) during its death fade - killed while broken, it now shows no shell as it falls.
                // Any enemy whose state went after it had one (Level_Out clears them) likewise keeps its last guard, not a full one.
                m.Guarded = physical;
                if (!physical) { m.Guard = 1f; m.Glow = 1f; m.Ph = Poise.Phase.Up; m.Hold = 0f; }
                else if (m.DeadSince < 0f)
                {
                    float vg, vgl, since, vh;
                    Poise.Phase vph;
                    if (Poise.View(m.T, out vg, out vgl, out vph, out since, out vh)) { m.HadGuard = true; m.Guard = vg; m.Glow = vgl; m.Ph = vph; m.Hold = vh; }
                    else if (!m.HadGuard) { m.Guard = 1f; m.Glow = 1f; m.Ph = Poise.Phase.Up; m.Hold = 0f; }   // not tracked yet: full
                }
                float g = m.Guard, gl = m.Glow;
                Poise.Phase ph = m.Ph;

                if (m.BombOnly) { BombTick(m, eye); continue; }
                Vector3 feet = m.T.transform.position;
                float tall = m.HasLive ? Mathf.Max(0.6f, m.Live.max.y - feet.y) : 1.8f;
                if (style == CombatSettings.EnemyStatusStyle.Shell)
                {
                    // (made while still hidden too: its copies are built during the fight's opening, one a frame)
                    if (m.Shell == null) m.Shell = new EnemyShell(m.C != null ? m.C.Model : null);
                    m.Shell.Update(new EnemyShell.View { Alpha = m.Alpha, Guarded = physical, Guard = g, Ph = ph, Hold = m.Hold, Dead = m.DeadSince >= 0f, Health = m.Health, Feet = feet, Tall = tall, Eye = eye });
                }
                else
                {
                    if (m.Rings == null)
                    {
                        if (m.Alpha <= 0.01f) continue;
                        m.Rings = new EnemyRings();
                    }
                    m.Rings.Draw(feet, BodyRadius(m), eye, m.Alpha, physical, g, gl, ph, 0f, m.Health);
                }
            }
            foreach (int id in m_gone)
            {
                Meter m = m_meters[id];
                Drop(m);
                m_meters.Remove(id);
            }
            EnemyShell.ShardsTick();
        }

        bool m_wasActive;

        static void Drop(Meter m)
        {
            if (m.Go != null) Destroy(m.Go);
            if (m.Mesh != null) Destroy(m.Mesh);
            if (m.Shell != null) m.Shell.Destroy();
            if (m.Rings != null) m.Rings.Destroy();
        }

        /// The body's radius on the ground: its collider's (TargetableShape), else its live bounds' half width.
        static float BodyRadius(Meter m)
        {
            try
            {
                var cc = m.Body0 as CapsuleCollider;
                if (cc != null && cc.enabled) return Mathf.Clamp(cc.radius * Mathf.Max(Mathf.Abs(cc.transform.lossyScale.x), Mathf.Abs(cc.transform.lossyScale.z)), 0.2f, 2f);
                if (m.Body0 != null && m.Body0.enabled) { Vector3 e = m.Body0.bounds.extents; return Mathf.Clamp(Mathf.Max(e.x, e.z), 0.2f, 2f); }
            }
            catch (Exception) { }
            return m.HasLive ? Mathf.Clamp(Mathf.Max(m.Live.extents.x, m.Live.extents.z) * 0.7f, 0.25f, 2f) : 0.35f;
        }

        void BombTick(Meter m, Vector3 eye)
        {
            Vector3 target;
            float height;
            bool ok = Anchor(m, eye, out target, out height);
            if (!ok) m.Alpha = 0f;
            if (m.Alpha <= 0.01f)
            {
                if (m.Go.activeSelf) m.Go.SetActive(false);
                m.Placed = false;
                return;
            }
            if (!m.Placed) { m.Pos = target; m.Vel = Vector3.zero; m.Placed = true; }
            else m.Pos = Vector3.SmoothDamp(m.Pos, target, ref m.Vel, 0.15f, Mathf.Infinity, Time.unscaledDeltaTime);
            m.Height = height;
            Place(m, eye);
            long bk = -2L - Mathf.Clamp(Mathf.RoundToInt(m.Alpha * 20f), 0, 31);
            if (bk != m.Built)
            {
                m.Built = bk;
                s_v.Clear(); s_c.Clear(); s_t.Clear();
                BombMark(m.Alpha);
                m.Mesh.Clear();
                m.Mesh.SetVertices(s_v);
                m.Mesh.SetColors(s_c);
                m.Mesh.SetTriangles(s_t, 0);
                m.Mesh.RecalculateBounds();
            }
            if (!m.Go.activeSelf) m.Go.SetActive(true);
        }

        /// The enemy is up: its live body stands at least 0.5 m tall above its feet (not rising out of the ground).
        static bool Up(Meter m) => m.Live.max.y - m.T.transform.position.y > 0.5f;

        void LiveBody(Meter m, float now)
        {
            m.HasLive = false;
            if (m.T == null) return;
            if (now >= m.NextBodyScan)
            {
                m.NextBodyScan = now + 1f;
                m.Body.Clear();
                Model model = m.C != null ? m.C.Model : null;
                if (model != null)
                {
                    model.GetComponentsInChildren(s_rend);   // (into a kept list: no array a second per enemy)
                    foreach (Renderer r in s_rend)
                        if (r is SkinnedMeshRenderer) m.Body.Add(r);
                    s_rend.Clear();
                }
                if (!m.HeadChecked && model != null)
                {
                    m.HeadChecked = true;
                    Animator an = model.GetComponentInChildren<Animator>();
                    try { if (an != null && an.isHuman) m.HeadBone = an.GetBoneTransform(HumanBodyBones.Head); } catch (Exception) { }
                }
            }
            foreach (Renderer r in m.Body)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!m.HasLive) { m.Live = r.bounds; m.HasLive = true; }
                else m.Live.Encapsulate(r.bounds);
            }
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        /// Where the meter goes this frame: the right side of the head (from the player's view) at neck height. `at` is the
        /// meter's inner end; Place may move it to the left side or above the head when another body is there.
        static bool Anchor(Meter m, Vector3 eye, out Vector3 at, out float height)
        {
            at = Vector3.zero;
            height = 0f;
            if (!m.HasLive) return false;
            Vector3 feet = m.T.transform.position;
            float tall = m.Live.max.y - feet.y;
            Vector3 headPt = m.HeadBone != null ? m.HeadBone.position : new Vector3(m.Live.center.x, m.Live.max.y - 0.12f * tall, m.Live.center.z);
            m.HeadPt = headPt;
            float y = tall > 2.6f ? feet.y + Mathf.Clamp(tall * 0.62f, 0.9f, 2.3f) : headPt.y - 0.1f;
            y = Mathf.Clamp(y, feet.y + 0.9f, feet.y + 2.3f);
            height = y - feet.y;
            // the player's right of the enemy, from the camera (stays there when the enemy turns)
            Vector3 view = Flat(headPt - eye);
            if (view.sqrMagnitude < 1e-4f) return false;
            Vector3 right = Vector3.Cross(Vector3.up, view.normalized);
            // the live body's reach to each side of the head's vertical line
            Bounds b = m.Live;
            float reachR = 0f, reachL = 0f;
            for (int i = 0; i < 4; i++)
            {
                Vector3 c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, 0f, (i & 2) == 0 ? b.min.z : b.max.z);
                float d = Vector3.Dot(c - Flat(headPt), right);
                reachR = Mathf.Max(reachR, d);
                reachL = Mathf.Max(reachL, -d);
            }
            m.ReachR = Mathf.Clamp(reachR, 0.1f, 1.5f);
            m.ReachL = Mathf.Clamp(reachL, 0.1f, 1.5f);
            m.Top = m.Live.max.y;
            at = new Vector3(headPt.x, y, headPt.z) + right * (m.ReachR + 0.12f);
            return true;
        }

        /// Size and facing, then a spot clear of every body: the right side (pushed outward up to 0.4 m), else the left
        /// side, else just above the head; the clearest of them if none is clear.
        void Place(Meter m, Vector3 eye)
        {
            Transform t = m.Go.transform;
            float dist = Vector3.Distance(eye, m.Pos);
            float scale = Mathf.Max(1f, dist / 4f) * Zoom;
            Vector3 toward = m.Pos - eye;
            Quaternion face = Quaternion.LookRotation(toward.sqrMagnitude > 1e-6f ? toward : Vector3.forward, Vector3.up);
            Vector3 right = face * Vector3.right, up = face * Vector3.up;
            float w, h;
            w = BombW;
            h = BombH;
            w *= scale;
            h *= scale;
            float bottom = -BombH * 0.5f * scale;
            Vector3 bestP = m.Pos;
            float bestGap = float.MinValue;
            m.Side = 0f;
            Vector3 flatRight = Flat(right).normalized;
            for (int cand = 0; cand < 3 && bestGap <= 0.02f; cand++)
                for (int step = 0; step <= (cand == 2 ? 2 : 8); step++)
                {
                    Vector3 p;
                    if (cand == 0) p = m.Pos + right * (0.05f * step);
                    else if (cand == 1) p = m.Pos - flatRight * (m.ReachR + m.ReachL + 0.24f + w + 0.05f * step);   // mirrored: ends 0.12 m left of the body
                    else p = new Vector3(m.HeadPt.x, m.Top + 0.12f + 0.08f * step, m.HeadPt.z) - right * (w * 0.5f);   // above the head
                    float gap = GapToBodies(p, right, up, w, bottom, h);
                    if (gap > bestGap) { bestGap = gap; bestP = p; m.Side = cand * 10 + step; }
                    if (gap > 0.02f) break;
                }
            m.Gap = bestGap;
            t.SetPositionAndRotation(bestP, face);
            t.localScale = Vector3.one * scale;
        }

        /// Smallest distance from the meter's quad (inner end at p, extending along `right`) to any live enemy body
        /// (negative = a corner is inside one). A body is its collider (TargetableShape) and a 0.15 m ball round its head;
        /// without a collider, its live bounds shrunk to 70 % across.
        float GapToBodies(Vector3 p, Vector3 right, Vector3 up, float w, float bottom, float h)
        {
            float best = 99f;
            for (int i = 0; i < 6; i++)
            {
                Vector3 c = p + right * (w * (i % 3) * 0.5f) + up * (bottom + (i < 3 ? 0f : h));
                foreach (Meter o in m_meters.Values)
                {
                    if (!o.HasLive || o.DeadSince >= 0f) continue;
                    float d = (c - o.HeadPt).magnitude - 0.15f;
                    Collider col = o.Body0;
                    if (col != null && col.enabled)
                    {
                        Vector3 cp;
                        try { cp = col.ClosestPoint(c); } catch (Exception) { cp = col.bounds.ClosestPoint(c); }
                        float dc = (c - cp).magnitude;
                        d = Mathf.Min(d, dc < 1e-4f ? -0.05f : dc);
                    }
                    else
                    {
                        Bounds b = o.Live;
                        b.extents = new Vector3(b.extents.x * 0.7f, b.extents.y, b.extents.z * 0.7f);
                        float db = Mathf.Sqrt(b.SqrDistance(c));
                        d = Mathf.Min(d, db <= 0f ? -0.05f : db);
                    }
                    best = Mathf.Min(best, d);
                }
            }
            return best;
        }

        public const int BombOnlyHits = 120;
        public static int BombOnlyMeters;

        /// Mark 10: a bomb (a dark round body, a fuse, a spark) for an enemy only a bomb kills.
        static void BombMark(float a)
        {
            float r = 0.028f, cx = 0.028f, cy = 0f;
            Disc(cx, cy, r + 0.003f, Col(new Color(1f, 0.85f, 0.4f), 0.8f * a), 0.0005f);
            Disc(cx, cy, r, Col(new Color(0.08f, 0.07f, 0.09f), a), 0f);
            Disc(cx - r * 0.35f, cy + r * 0.35f, r * 0.25f, Col(new Color(0.55f, 0.55f, 0.6f), a), -0.0005f);
            Rect(cx + r * 0.45f, cy + r * 0.7f, 0.006f, 0.014f, -0.0005f, Col(new Color(0.75f, 0.6f, 0.35f), a));
            Diamond(cx + r * 0.45f + 0.003f, cy + r * 0.7f + 0.019f, 0.008f, Col(new Color(1f, 0.8f, 0.25f), a), -0.001f);
        }

        static void Disc(float x, float y, float r, Color c, float z = 0f, int n = 18)
        {
            int b = s_v.Count;
            s_v.Add(new Vector3(x, y, z));
            s_c.Add(c);
            for (int i = 0; i <= n; i++) { float t = i * Mathf.PI * 2f / n; s_v.Add(new Vector3(x + Mathf.Cos(t) * r, y + Mathf.Sin(t) * r, z)); s_c.Add(c); }
            for (int i = 0; i < n; i++) { s_t.Add(b); s_t.Add(b + 2 + i); s_t.Add(b + 1 + i); }
        }

        static Color Col(Color c, float a) { c.a *= a; return c; }

        static void Diamond(float x, float y, float r, Color c, float z = 0f)
        {
            int b = s_v.Count;
            s_v.Add(new Vector3(x, y + r, z)); s_v.Add(new Vector3(x + r, y, z)); s_v.Add(new Vector3(x, y - r, z)); s_v.Add(new Vector3(x - r, y, z));
            for (int i = 0; i < 4; i++) s_c.Add(c);
            s_t.Add(b); s_t.Add(b + 1); s_t.Add(b + 2); s_t.Add(b); s_t.Add(b + 2); s_t.Add(b + 3);
        }

        static void Rect(float x, float y, float w, float h, float z, Color c)
        {
            int b = s_v.Count;
            s_v.Add(new Vector3(x, y, z)); s_v.Add(new Vector3(x, y + h, z)); s_v.Add(new Vector3(x + w, y + h, z)); s_v.Add(new Vector3(x + w, y, z));
            for (int i = 0; i < 4; i++) s_c.Add(c);
            s_t.Add(b); s_t.Add(b + 1); s_t.Add(b + 2); s_t.Add(b); s_t.Add(b + 2); s_t.Add(b + 3);
        }

        void Clear()
        {
            foreach (Meter m in m_meters.Values) Drop(m);
            m_meters.Clear();
        }
    }
}

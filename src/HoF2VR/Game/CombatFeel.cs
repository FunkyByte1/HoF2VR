using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Feedback that makes hits flashy, all in the world or in the hands (docs/combat-uyf-design.md §8.5.8; the user: no
    /// vision tints, nothing shakes the view):
    ///  * haptics: the hand-feel vocabulary's durations (the Quest path sends every pulse at full strength, so cues differ
    ///    by length and rhythm): `impact` = 3 x 60 ms back to back; a Big hit adds an echo; a counter is an impact and a
    ///    second one 80 ms later;
    ///  * hit-stop: only the struck enemy's animator freezes, 50-250 ms by hit type (never the view, never the world);
    ///  * slow motion only when earned: a counter's and a finisher's own TimeDilation (the game's data), and the last kill
    ///    of a fight (ours: 0.3x for 0.5 s); the game's HitPause setting switches it off;
    ///  * the game's red screen edge when the hero is hit (CameraVignette) is never drawn in a fight: a spark burst in
    ///    front of the player toward the attacker, the impact in both hands and the attacker stepping back replace it.
    internal class CombatFeel : MonoBehaviour
    {
        public static CombatFeel I;

        // diagnostics (`pcombat?`)
        public static int Impacts, Stops, SlowMos, Vignettes, Bursts, Pushes;
        public static float LastStopMs;
        public static string LastSlow = "-";
        public static int UnearnedSlowFrames, EarnedSlowFrames;
        static float s_earnedUntil = -10f;
        static string s_earnedWhy = "";

        public static void Apply(Harmony h)
        {
            try { h.Patch(AccessTools.Method(typeof(CameraVignette), "SetConfig"), prefix: new HarmonyMethod(typeof(CombatFeel), nameof(VignettePrefix))); }
            catch (Exception e) { Plugin.L.LogWarning("CombatFeel: vignette patch failed: " + e.Message); }
            // Stage 2 item 6: a shot that hits the player (UYF's ranged hit: knockback 3 m, the hit shake, the optional red flash)
            try { h.Patch(AccessTools.Method(typeof(Destroyable), "ApplyDamage"), postfix: new HarmonyMethod(typeof(CombatFeel), nameof(HeroDamagePostfix))); }
            catch (Exception e) { Plugin.L.LogWarning("CombatFeel: hero damage patch failed: " + e.Message); }
            // the last kill of a fight, whatever killed it (a blade, a finisher, a bash, an ability, a burn)
            try { h.Patch(AccessTools.Method(typeof(Destroyable), "Die"), postfix: new HarmonyMethod(typeof(CombatFeel), nameof(DiePostfix))); }
            catch (Exception e) { Plugin.L.LogWarning("CombatFeel: death patch failed: " + e.Message); }
        }

        void Awake() { I = this; }

        /// The hero took a ranged enemy hit (a shot or a thrown item: CombatCategory.Ranged, plain damage - not a burn's or a
        /// bleed's ticks): UYF's ranged hit.
        static void HeroDamagePostfix(Destroyable __instance, Destroyable.Damage a_damage)
        {
            try
            {
                if (!Physical.Live || __instance == null) return;
                PlayerController pc = PlayerController.Instance;
                if (pc == null || __instance.gameObject != pc.gameObject) return;
                ICombatEffectContainer c = a_damage.Container;
                if (c == null || (c.Category & CombatCategory.Ranged) == 0 || a_damage.DamageType != Destroyable.DamageType.Default) return;
                Targetable src = c.Source;
                if (src == null || src.gameObject == pc.gameObject || !pc.Targetable.IsEnemy(src)) return;
                if (Time.unscaledTime - s_lastRanged < 0.3f) return;   // one shot, one reaction (a spread of pellets)
                s_lastRanged = Time.unscaledTime;
                RangedHit(src.transform.position);
            }
            catch (Exception) { }
        }
        static float s_lastRanged = -10f;

        /// Destroyable.Die: an enemy of the player died with no other enemy left -> the last kill's beat (BladeHits.LastKill).
        static void DiePostfix(Destroyable __instance)
        {
            if (!Physical.Live || __instance == null) return;
            try
            {
                PlayerController pc = PlayerController.Instance;
                Targetable t = __instance.GetComponent<Targetable>();
                if (pc == null || t == null || t.gameObject == pc.gameObject || (t.Team & pc.Targetable.GetEnemyTeams()) == 0) return;
                SuperMeter.Add(7.5f, "kill");   // Stage 2 item 8
                if (IsBoss(t)) BossDeath(t);
                if (!BladeHits.AnyOtherEnemyAlive(pc, t)) BladeHits.LastKill(t.transform.position + Vector3.up);
            }
            catch (Exception e) { Plugin.L.LogWarning("CombatFeel: death: " + e.Message); }
        }

        // ---------------------------------------------------------------------------------------- a boss's death

        public static int BossDeaths;

        /// N3 (the player's gnome fight 2026-10-17: "boss death: Gnome", the slow motion for one plain gnome): a swarm whose
        /// members share one boss bar (AIRef.Boss with CombineHealthBar: the gnomes) is one boss - only its last member's
        /// death is the boss's.
        static bool IsBoss(Targetable t)
        {
            try
            {
                AI a = t.Controller != null ? t.Controller.GetComponent<AI>() : null;
                if (a == null || a.Ref == null || !a.Ref.Boss) return false;
                if (!a.Ref.CombineHealthBar) return true;
                PlayerController pc = PlayerController.Instance;
                if (pc == null) return true;
                BetterList<Targetable> all = pc.Targetable.EnumerateWithinRadius(200f, pc.Targetable.GetEnemyTeams());
                int left = 0;
                for (int i = 0; i < all.size; i++)
                {
                    Targetable o = all[i];
                    if (o == null || o == t || o.Controller == null) continue;
                    AI oa = o.Controller.GetComponent<AI>();
                    Destroyable d = o.GetComponent<Destroyable>();
                    if (oa != null && oa.Ref == a.Ref && d != null && !d.IsDead) left++;
                }
                if (left > 0) { SwarmDeaths++; Plugin.L.LogInfo("boss death: not yet - " + WeaponGrips.ModelKey(t.name) + " is one of a swarm with one boss bar, " + left + " left"); }
                return left == 0;
            }
            catch (Exception) { return false; }
        }

        public static int SwarmDeaths;

        /// UYF's boss death (docs/uyf-parity-audit.md E28, item 17; its BossDeath_EarlyBurst_ScreenFlash and
        /// BossDeath_ScreenFlash): a pale cyan burst at full strength for 0.7 s, then a pale rose flash at full strength for
        /// 1 s, and a long rumble in both hands. (Its scripted slow motion at a boss's death is left to HoF2's own last-kill
        /// beat - since merge-ready #4 UYF's 0.1x for 1.5 s as well, `[Combat] BossSlowMotion`; its phase changes and ground
        /// premonitions are not ours - HoF2's bosses have their own.)
        static void BossDeath(Targetable t)
        {
            BossDeaths++;
            ScreenFlash.Show(new Color(0.665f, 1f, 0.98f), 1f, 0.7f, "boss death (burst)");
            s_lateFlashAt = Time.unscaledTime + 0.45f;
            for (int i = 0; i < 8; i++)
            {
                float strength = 1f - i / 8f;
                s_rumble.Add(new Rumble { At = Time.unscaledTime + i * 0.17f, Strength = Mathf.Lerp(0.3f, 1f, strength) });
            }
            // UYF's scripted slow motion at a boss's death (its TimeScaleNode: 0.1x for 1.5 s, ramped over 0.1 s; merge-ready #4)
            if (CombatSettings.BossSlowMotion) { s_slowFrom = Time.unscaledTime; BossSlowMos++; }
            Plugin.L.LogInfo("boss death: " + WeaponGrips.ModelKey(t.name));
        }

        public const float BossSlowScale = 0.1f, BossSlowLength = 1.5f, BossSlowRamp = 0.1f;
        public static int BossSlowMos;
        public static float BossSlowLowest = 1f;
        static float s_slowFrom = -1f;
        /// The boss death's slow motion is running (GnomeFight leaves the time scale to it then).
        public static bool BossSlowing => s_slowFrom >= 0f;

        /// The boss death's slow motion, each frame: the world at min(the game's own time scale, ours) - HoF2's own beats and
        /// its pause menu keep theirs - and the game's own scale back at the end.
        static void TickBossSlow()
        {
            if (s_slowFrom < 0f) return;
            try
            {
                Game g = Game.Instance;
                bool paused = MenuManager.Instance != null && MenuManager.Instance.Paused;
                float el = Time.unscaledTime - s_slowFrom, game = g != null ? g.TimeScale : 1f;
                if (el >= BossSlowLength || g == null || !Physical.On)
                {
                    s_slowFrom = -1f;
                    if (!paused) Time.timeScale = game;
                    return;
                }
                if (paused) return;
                float ramp = Mathf.Min(Mathf.Clamp01(el / BossSlowRamp), Mathf.Clamp01((BossSlowLength - el) / BossSlowRamp));
                float ours = Mathf.Lerp(1f, BossSlowScale, ramp);
                Time.timeScale = Mathf.Min(game, ours);
                BossSlowLowest = Mathf.Min(BossSlowLowest, Time.timeScale);
            }
            catch (Exception) { s_slowFrom = -1f; }
        }

        struct Rumble { public float At, Strength; }
        static readonly List<Rumble> s_rumble = new List<Rumble>();
        static float s_lateFlashAt = -1f;

        /// Every frame (LateUpdate): the boss death's second flash and its rumble.
        static void TickBossDeath()
        {
            float now = Time.unscaledTime;
            if (s_lateFlashAt > 0f && now >= s_lateFlashAt)
            {
                s_lateFlashAt = -1f;
                ScreenFlash.Show(new Color(1f, 0.709f, 0.684f), 1f, 1f, "boss death");
            }
            for (int i = s_rumble.Count - 1; i >= 0; i--)
            {
                if (now < s_rumble[i].At) continue;
                Haptics.Pulse(true, s_rumble[i].Strength, 0.16f);
                Haptics.Pulse(false, s_rumble[i].Strength, 0.16f);
                s_rumble.RemoveAt(i);
            }
        }

        // ---------------------------------------------------------------------------------------- haptics
        struct Buzz { public bool Left; public float At, Ms; }
        static readonly List<Buzz> s_buzz = new List<Buzz>();

        static void Queue(bool left, float delay, float ms) => s_buzz.Add(new Buzz { Left = left, At = Time.unscaledTime + delay, Ms = ms });

        /// `impact` on one hand: 3 x 60 ms back to back (UYF's 1.0 x 0.2 s, as durations); `echo` adds a fourth after a gap.
        public static void Impact(bool left, bool echo = false)
        {
            Impacts++;
            for (int i = 0; i < 3; i++) Queue(left, i * 0.06f, 60f);
            if (echo) Queue(left, 0.26f, 45f);
        }

        public static void ImpactBoth(bool echo = false) { Impact(true, echo); Impact(false, echo); }

        /// The counter: an impact, then a second one 80 ms after it.
        public static void CounterCue(bool left)
        {
            Impact(left);
            for (int i = 0; i < 2; i++) Queue(left, 0.26f + i * 0.06f, 60f);
        }

        /// A short tick (a swing turning legal, a slow touch on a body).
        public static void Tick(bool left, float ms = 5f) => Queue(left, 0f, ms);

        static void DeliverBuzz()
        {
            float now = Time.unscaledTime;
            for (int i = s_buzz.Count - 1; i >= 0; i--)
            {
                Buzz b = s_buzz[i];
                if (b.At > now) continue;
                s_buzz.RemoveAt(i);
                if (now - b.At > 0.2f) continue;   // stale (a hitch): drop it rather than buzz late
                Haptics.Cue(Haptics.CueKind.Bump, b.Left, b.Ms / 8f);
            }
        }

        // ---------------------------------------------------------------------------------------- hit-stop
        sealed class Stop { public AnimatorProxy A; public float Until, Speed; }

        /// A strike's slow-down ended during this enemy's hit-stop: the stop's release gives back `speed`, not the slow.
        public static void RestoreSpeedAfterStop(Controller c, float speed)
        {
            Stop st;
            if (c != null && s_stops.TryGetValue(c.GetInstanceID(), out st)) st.Speed = speed;
        }
        static readonly Dictionary<int, Stop> s_stops = new Dictionary<int, Stop>();
        static readonly Dictionary<int, float> s_lastStop = new Dictionary<int, float>();

        /// This enemy was hit-stopped since `t` (FightClock time, as the strikes' starts it is compared with: the HUD audit of
        /// 2026-10-19, item 5 - real-time stamps against fight times answered true for any stop in the P seconds ever paused
        /// before a strike): a strike of it then took longer than its natural wind-up.
        public static bool StoppedSince(Controller c, float t)
        {
            float at;
            return c != null && s_lastStop.TryGetValue(c.GetInstanceID(), out at) && at >= t;
        }

        /// Freezes only this enemy's animator for `seconds` (real time), then gives it back its speed.
        public static void HitStop(Controller c, float seconds)
        {
            if (c == null || seconds <= 0f || c.Model == null) return;
            try
            {
                AnimatorProxy a = c.Animator;
                if (a == null) return;
                int id = c.GetInstanceID();
                Stop st;
                if (!s_stops.TryGetValue(id, out st))
                {
                    float sp = a.GetGlobalSpeed();
                    if (sp < 0.01f) return;   // already frozen (a counter's pause): leave it to the game
                    s_stops[id] = st = new Stop { A = a, Speed = sp };
                }
                st.Until = Mathf.Max(st.Until, Time.unscaledTime + seconds);
                s_lastStop[id] = FightClock.Now;
                a.SetGlobalSpeed(0f);
                Stops++;
                LastStopMs = seconds * 1000f;
            }
            catch (Exception e) { Plugin.L.LogWarning("CombatFeel: hit-stop: " + e.Message); }
        }

        static readonly List<int> s_done = new List<int>();
        static void ReleaseStops()
        {
            if (s_stops.Count == 0) return;
            float now = Time.unscaledTime;
            s_done.Clear();
            foreach (var kv in s_stops)
            {
                if (now < kv.Value.Until) continue;
                s_done.Add(kv.Key);
                try
                {
                    AnimatorProxy a = kv.Value.A;
                    // somebody else changed it meanwhile (the action ended and reset it): theirs wins
                    if (a != null && a.GetGlobalSpeed() < 0.01f) a.SetGlobalSpeed(kv.Value.Speed);
                }
                catch (Exception) { }
            }
            foreach (int id in s_done) s_stops.Remove(id);
        }

        // ---------------------------------------------------------------------------------------- slow motion
        /// Slow motion that was earned (a counter, a finisher, the last kill): what the time-scale audit accepts.
        public static void Earned(string why, float seconds)
        {
            s_earnedUntil = Mathf.Max(s_earnedUntil, Time.unscaledTime + seconds);
            s_earnedWhy = why;
        }

        static float s_slowUntil = -1f;
        static bool s_slowing;

        /// Ours, for the last kill of a fight: `scale` for `seconds` of real time (the game's HitPause setting = off).
        public static void SlowMo(float scale, float seconds, string why)
        {
            Earned(why, seconds + 0.3f);
            SlowMos++;
            LastSlow = why;
            try
            {
                bool hitPause = Defiant.Settings.SettingsManager.Instance == null || Defiant.Settings.SettingsManager.Instance.GeneralSettings.HitPause;
                if (!hitPause || Game.Instance == null) return;
                Game.Instance.TimeScale = scale;
                s_slowUntil = Time.unscaledTime + seconds;
                s_slowing = true;
            }
            catch (Exception e) { Plugin.L.LogWarning("CombatFeel: slow motion: " + e.Message); }
        }

        void AuditTime()
        {
            if (s_slowing && Time.unscaledTime >= s_slowUntil)
            {
                s_slowing = false;
                try { if (Game.Instance != null && Game.Instance.TimeScale < 0.999f) Game.Instance.TimeScale = 1f; } catch (Exception) { }
            }
            if (!CombatSettings.Physical || !GameWatch.InCombatPlay || Game.Instance == null) return;
            float ts;
            try { ts = Game.Instance.TimeScale; } catch (Exception) { return; }
            if (ts >= 0.999f || ts <= 0.001f) return;   // 0 = paused (menus), not slow motion
            if (Time.unscaledTime <= s_earnedUntil) EarnedSlowFrames++;
            else
            {
                UnearnedSlowFrames++;
                if (UnearnedSlowFrames <= 5) Plugin.L.LogInfo("CombatFeel: slow motion (x" + ts.ToString("0.00") + ") not earned by a counter, finisher or last kill (last earned: " + s_earnedWhy + ")");
            }
        }

        // ---------------------------------------------------------------------------------------- the hero is hit
        /// The game's red screen edge (CameraVignette) is not drawn in fights; the hit is shown in the world instead.
        static bool VignettePrefix(CameraVignetteConfig a_config)
        {
            if (!XRSettings.enabled || a_config == null) return true;
            string st = GameWatch.State;
            if (!(GameWatch.InCombatPlay || st.StartsWith("GameState_Level") || st.StartsWith("GameState_BossBattle"))) return true;
            Vignettes++;
            return false;
        }

        /// The hero took damage in a fight (both modes): a burst of sparks between the player and the attacker, in the
        /// world; in Physical mode also the impact in both hands and the attacker stepping back 1 m (it replaces the
        /// player's own knock-back, which comfort forbids).
        public static void HeroHit(Controller attacker, bool unblockable)
        {
            if (VRRig.I == null) return;
            Vector3 head = VRRig.I.HeadWorldPos;
            Vector3 to = attacker != null ? attacker.transform.position + Vector3.up * 1.2f - head : VRRig.I.HeadWorldRot * Vector3.forward;
            if (to.sqrMagnitude < 1e-4f) to = VRRig.I.HeadWorldRot * Vector3.forward;
            Vector3 p = head + to.normalized * Mathf.Min(0.45f, to.magnitude * 0.5f) + Vector3.down * 0.25f;
            Burst(p, new Color(1f, 0.55f, 0.2f), 0.32f);
            Bursts++;
            if (!CombatSettings.Physical) return;
            ImpactBoth();
            AIController ai = attacker as AIController;
            if (CombatSettings.Knockback && ai != null)
            {
                // Until You Fall (Stage 2 item 6): the player is knocked back along the attacker's line - 1.6 m, a heavy blow
                // 3.5 m - and the view shakes 0.3 s by 0.15 m
                Vector3 away = head - ai.transform.position;
                CombatMove.Knockback(away, unblockable ? 3.5f : 1.6f, unblockable ? "heavy hit" : "hit");
            }
            else if (ai != null) EnemyPush.Start(ai, ai.transform.position - head, unblockable ? 1.5f : 1.0f, 0.4f);   // the comfort stand-in
            CameraShake.Add(0.3f, 0.15f, 0.008f, "hit taken");
        }

        /// A shot or an area hit the player (UYF: a ranged hit: 3 m knockback from where it came, the hit shake, the red flash
        /// if `[Combat] HitTakenFlash`).
        public static void RangedHit(Vector3 from)
        {
            if (!CombatSettings.Physical || VRRig.I == null) return;
            Vector3 head = VRRig.I.HeadWorldPos;
            RangedHits++;
            ImpactBoth();
            if (CombatSettings.Knockback) CombatMove.Knockback(head - from, 3f, "ranged hit");
            CameraShake.Add(0.3f, 0.15f, 0.008f, "ranged hit");
            ScreenFlash.Show(ScreenFlash.Red, 0.15f, 0.3f, "ranged hit", true);
        }

        public static int RangedHits;

        // ---------------------------------------------------------------------------------------- shapes
        static Mesh s_quad;

        /// A unit quad with white vertex colours: Sprites/Default multiplies the material colour by the vertex colour, and
        /// Unity's primitive quad has none (it drew black).
        public static Mesh WhiteQuad
        {
            get
            {
                if (s_quad != null) return s_quad;
                s_quad = new Mesh { name = "HoF2VR_WhiteQuad" };
                s_quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
                s_quad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
                s_quad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
                s_quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
                s_quad.RecalculateBounds();
                return s_quad;
            }
        }

        /// A quad child (no collider, no shadows) with the shared material.
        public static Transform Quad(Transform parent, Material m, Vector3 scale, string name = "part")
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = WhiteQuad;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        // ---------------------------------------------------------------------------------------- bursts (world)
        sealed class Spark { public Transform T; public float Start, Life, Size; public Material M; public Color C; }
        static readonly List<Spark> s_sparks = new List<Spark>();
        static Material s_mat;

        /// A short star of bright streaks at a point, facing the eyes (a world object: it never covers the view).
        public static void Burst(Vector3 at, Color c, float size, float life = 0.28f)
        {
            try
            {
                if (s_mat == null)
                {
                    Shader sh = Shader.Find("Sprites/Default");
                    s_mat = new Material(sh) { renderQueue = 3100 };
                }
                var go = new GameObject("HoF2VR_Burst");
                var t = go.transform;
                t.position = at;
                var mat = new Material(s_mat) { color = c };
                for (int i = 0; i < 6; i++)
                {
                    Transform q = Quad(t, mat, new Vector3(i % 2 == 0 ? 1f : 0.6f, 0.06f, 1f), "streak");
                    q.localRotation = Quaternion.Euler(0f, 0f, i * 30f + UnityEngine.Random.Range(-8f, 8f));
                }
                s_sparks.Add(new Spark { T = t, Start = Time.unscaledTime, Life = life, Size = size, M = mat, C = c });
            }
            catch (Exception e) { Plugin.L.LogWarning("CombatFeel: burst: " + e.Message); }
        }

        static void AnimateBursts()
        {
            if (s_sparks.Count == 0) return;
            float now = Time.unscaledTime;
            Vector3 eye = VRRig.I != null ? VRRig.I.HeadWorldPos : Vector3.zero;
            for (int i = s_sparks.Count - 1; i >= 0; i--)
            {
                Spark s = s_sparks[i];
                float k = (now - s.Start) / s.Life;
                if (k >= 1f || s.T == null)
                {
                    if (s.T != null) UnityEngine.Object.Destroy(s.T.gameObject);
                    if (s.M != null) UnityEngine.Object.Destroy(s.M);
                    s_sparks.RemoveAt(i);
                    continue;
                }
                s.T.rotation = Quaternion.LookRotation(s.T.position - eye);
                float sc = s.Size * (0.35f + 0.65f * Mathf.Sqrt(k));
                s.T.localScale = new Vector3(sc, sc, sc);
                Color c = s.C;
                c.a = 1f - k * k;
                s.M.color = c;
            }
        }

        void LateUpdate()
        {
            DeliverBuzz();
            ReleaseStops();
            AuditTime();
            AnimateBursts();
            EnemyPush.Tick();
            Physical.SnareTick();
            TickBossDeath();
            TickBossSlow();
        }
    }

    /// Enemies move instead of the player (comfort): the attacker recoils 0.3 m from a block and steps back 1 m after
    /// hitting the player (docs/combat-uyf-design.md §2.5.3), along the NavMesh (NavMeshAgent.Move, the game's own call
    /// for advances and knock-backs, so walls and edges stop it), easing out over the time given.
    internal static class EnemyPush
    {
        /// One push; Moved = how far it really went (walls and the enemy's own running stop it).
        internal sealed class Push { public AIController Ai; public Vector3 Dir; public float Dist, Done, Start, Time, Moved; }
        static readonly List<Push> s_pushes = new List<Push>();
        public static int Count;
        public static float Metres;

        public static Push Start(AIController ai, Vector3 away, float metres, float seconds)
        {
            if (ai == null || metres <= 0f) return null;
            away.y = 0f;
            if (away.sqrMagnitude < 1e-4f) return null;
            try
            {
                Destroyable d = ai.GetComponent<Destroyable>();
                if (d != null && d.IsDead) return null;
                if (ai.Prone.HasValue) return null;
            }
            catch (Exception) { return null; }
            s_pushes.RemoveAll(p => p.Ai == ai);
            var push = new Push { Ai = ai, Dir = away.normalized, Dist = metres, Start = Time.unscaledTime, Time = Mathf.Max(0.05f, seconds) };
            s_pushes.Add(push);
            Count++;
            CombatFeel.Pushes++;
            return push;
        }

        public static void Tick()
        {
            if (s_pushes.Count == 0) return;
            float now = Time.unscaledTime;
            for (int i = s_pushes.Count - 1; i >= 0; i--)
            {
                Push p = s_pushes[i];
                float k = Mathf.Clamp01((now - p.Start) / p.Time);
                float want = p.Dist * (1f - (1f - k) * (1f - k));   // ease out
                float step = want - p.Done;
                bool end = k >= 1f;
                try
                {
                    if (p.Ai == null) { s_pushes.RemoveAt(i); continue; }
                    UnityEngine.AI.NavMeshAgent ag = p.Ai.GetComponent<UnityEngine.AI.NavMeshAgent>();
                    if (ag != null && ag.enabled && ag.isOnNavMesh && step > 0f)
                    {
                        Vector3 before = p.Ai.transform.position;
                        ag.Move(p.Dir * step);
                        float moved = Vector3.Distance(before, p.Ai.transform.position);
                        Metres += moved;
                        p.Moved += moved;
                        if (moved < step * 0.2f) end = true;   // a wall
                    }
                    p.Done = want;
                }
                catch (Exception) { end = true; }
                if (end) s_pushes.RemoveAt(i);
            }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Defiant;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The dice by hand (interaction design §3.2, "hybrid" throw):
    ///  * grab: in the game's Catapault state (dice gathered and waiting), a fresh grip with the palm at
    ///    the dice - or, with DiceSummon, while pointing at them - takes them; they fly into the hand
    ///  * hold / shake: the game's gather container follows the palm; hand speed drives the game's own
    ///    tumble (its "throw strength"), shake reversals rattle and tick
    ///  * throw: letting go reads the hand's speed -> the game's throw strength (slow = the game's own
    ///    no-force roll) and calls the game's DiceRoller.SetReady(), exactly like its pad/mouse throw.
    ///    When the game launches each die (Die.Roll), its direction is bent toward the hand's (within a
    ///    cone round the game's own aim at the tray) and the hand's spin is added to the game's random
    ///    torque. Force, randomness, physics and the reading of the faces stay the game's.
    /// The trigger draw (Minigames.MapDice) stays as the fallback.
    internal class DiceHand : MonoBehaviour
    {
        public static DiceHand I;

        static readonly AccessTools.FieldRef<DiceRoller, DiceRoller.State> s_state = AccessTools.FieldRefAccess<DiceRoller, DiceRoller.State>("m_state");
        static readonly AccessTools.FieldRef<DiceRoller, Transform> s_gather = AccessTools.FieldRefAccess<DiceRoller, Transform>("m_gatherContainer");
        static readonly AccessTools.FieldRef<DiceRoller, DiceRoller.GatherArea> s_area = AccessTools.FieldRefAccess<DiceRoller, DiceRoller.GatherArea>("m_handGatherArea");
        static readonly AccessTools.FieldRef<DiceRoller, float> s_strength = AccessTools.FieldRefAccess<DiceRoller, float>("m_throwStrength");
        static readonly AccessTools.FieldRef<DiceRoller, float> s_magnitude = AccessTools.FieldRefAccess<DiceRoller, float>("m_throwMagnitude");
        static readonly AccessTools.FieldRef<DiceRoller, float> s_dragRadius = AccessTools.FieldRefAccess<DiceRoller, float>("m_dragRadius");
        static readonly AccessTools.FieldRef<DiceRoller, UISelectable> s_idle = AccessTools.FieldRefAccess<DiceRoller, UISelectable>("m_idleSelectable");
        static readonly AccessTools.FieldRef<DiceRoller, Vector3Range> s_torque = AccessTools.FieldRefAccess<DiceRoller, Vector3Range>("m_torqueRange");
        static readonly AccessTools.FieldRef<DiceRoller, Transform> s_dragCentre = AccessTools.FieldRefAccess<DiceRoller, Transform>("m_dragCentrePoint");
        static readonly AccessTools.FieldRef<DiceRoller, Transform> s_target = AccessTools.FieldRefAccess<DiceRoller, Transform>("m_targetContainer");

        // tuning (real metres / seconds; world distances are these times the room scale)
        public static float GrabExtra = 0.10f;       // beyond the dice cluster's radius that still grabs
        public static float SummonAngle = 25f;       // pointing this close to the dice summons them
        public static float DropSpeed = 0.4f;        // slower release = a drop: the game's no-force roll
        public static float FullSpeed = 2.5f;        // this fast or faster = the strongest throw
        public static float HandInfluence = 0.6f;    // how far the launch turns from the game's aim to the hand's
        public static float ConeDeg = 35f;           // never more than this from the game's aim at the tray
        public static float MaxUp = 0.25f;           // launch direction y at most (a lob, not a throw at the ceiling)
        public static float SpinShare = 0.03f;       // hand spin (rad/s) x this x the game's mean torque
        public static float HoldMinStrength = 0.55f; // held dice stay bunched in the hand
        // hand feel Phase 2 (docs/hand-feel-design.md §3.7-3.8, Settings.GrabThrow)
        public static float GrabOpen = 0.3f;         // an open hand (grip below this) at the dice is "ready"
        public static float ReleaseGrip = 0.35f;     // held dice drop only below this grip (they are taken at 0.55)
        static readonly bool[] s_hover = new bool[2];
        static readonly float[] s_ready = new float[2];
        static Transform s_glow;
        static Material s_glowMat;
        public static int Hovers;
        public static string LastEstimate = "-";

        /// 0..1: an open hand is at the waiting dice (the glove gets ready: FingerCurl).
        public static float Ready(Hand h) => s_ready[h.IsLeft ? 1 : 0];

        public static Hand Holder;
        static bool s_synthetic, s_atGameSpot;
        static DiceRoller s_roller;
        static float s_grabT, s_slide = 0.12f;
        static Vector3 s_grabFrom;
        static float s_untrackedSince = -1f;
        static Vector3 s_lastDir;
        static float s_lastRattle;

        sealed class Pending
        {
            public DiceRoller Roller;
            public Vector3 Vel, AngVel;
            public float Strength, T;
            public bool Left;
            public int Dice, Done;
        }
        static Pending s_pending;
        static DiceRoller s_landRoller;
        static bool s_landLeft;
        static float s_landUntil, s_lastLand;
        static readonly List<Die> s_thrown = new List<Die>();
        static float s_lastLaunch = -10f;
        static bool s_lastWasVr;

        // counters and results (status, tests)
        public static int Grabs, Summons, Throws, Drops, Cancels, VrRolls, ClassicRolls;
        public static string LastThrow = "-", LastResult = "-";
        static readonly int[] s_faces = new int[7];
        static readonly int[] s_facesVr = new int[7];
        static float s_missLog;

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(DiceRoller), "ProcessCatapault"), postfix: new HarmonyMethod(typeof(DiceHand), nameof(CatapaultPostfix)));
            h.Patch(AccessTools.Method(typeof(Die), nameof(Die.Roll)), prefix: new HarmonyMethod(typeof(DiceHand), nameof(RollPrefix)));
            h.Patch(AccessTools.Method(typeof(Die), "OnCollisionEnter"), postfix: new HarmonyMethod(typeof(DiceHand), nameof(CollisionPostfix)));
            h.Patch(AccessTools.Method(typeof(DiceRoller), "Evaluate"), postfix: new HarmonyMethod(typeof(DiceHand), nameof(EvaluatePostfix)));
        }

        void Awake()
        {
            I = this;
            Diag.Commands["dice"] = Command;
        }

        public static string StateOf(DiceRoller r) => r != null ? s_state(r).ToString() : "-";

        static DiceRoller Active()
        {
            DiceRollTask task = DiceRollTask.RunningTask;
            return task != null ? task.Roller : null;
        }

        /// The roller when its dice wait in the Catapault state with the game's own input check passing
        /// (DiceRoller.ProcessCatapault: its group selected and the idle selectable hovered).
        public static DiceRoller Waiting()
        {
            DiceRoller r = Active();
            if (r == null || s_state(r) != DiceRoller.State.Catapault) return null;
            return IdleValid(r) ? r : null;
        }

        /// The waiting dice's middle (the game's gather container), while they wait for the throw.
        public static bool WaitingDice(out Vector3 at, out float radius)
        {
            at = Vector3.zero; radius = 0f;
            DiceRoller r = Waiting();
            if (r == null || s_gather(r) == null) return false;
            at = s_gather(r).position;
            radius = ClusterRadius(r);
            return true;
        }

        static bool IdleValid(DiceRoller r)
        {
            try
            {
                UISelection sel = UISelectionManager.GetSelection(r.SelectableGroup.SelectionCategory);
                return sel != null && sel.IsValid && sel.SelectedGroup == r.SelectableGroup && sel.HoveredObject == s_idle(r);
            }
            catch (Exception) { return false; }
        }

        static float Scale => VRRig.I != null ? VRRig.I.Scale : 1f;

        static float ClusterRadius(DiceRoller r)
        {
            DiceRoller.GatherArea area = s_area(r);
            if (area == null || area.Bounds == null) return 0.05f * Scale;
            Vector3 ls = area.Bounds.transform.lossyScale;
            return area.Bounds.radius * Mathf.Max(ls.x, Mathf.Max(ls.y, ls.z));
        }

        /// Where the held dice sit: in the curled fingers, a little to the palm side of the fist.
        static Vector3 HoldPoint(Hand h)
        {
            Vector3 palm = HandWorld.Palm(h);
            Quaternion rot = HandWorld.Rot(h);
            float s = Scale;
            Vector3 palmSide = rot * new Vector3(h.IsLeft ? 1f : -1f, 0f, 0f);
            return palm + palmSide * (0.025f * s) + Vector3.down * (0.015f * s);
        }

        void Update()
        {
            if (!XRSettings.enabled) return;
            DiceRoller waiting = Waiting();
            if (Holder != null)
            {
                Hold(waiting);
                return;
            }
            if (waiting == null || Settings.DiceThrow != Settings.DiceMode.Hybrid || Minigames.Drawn) { NotReady(); return; }
            HoverDice(waiting);
            foreach (Hand h in VRInput.RL)
            {
                if (!h.GripDown || !HandWorld.Valid(h)) continue;
                Vector3 dice = s_gather(waiting).position;
                float dist = Vector3.Distance(HandWorld.Palm(h), dice);
                bool near = dist <= ClusterRadius(waiting) + GrabExtra * Scale;
                Ray ray = HandWorld.PointRay(h);
                float ang = Vector3.Angle(ray.direction, dice - ray.origin);
                bool summon = !near && Settings.DiceSummon && ang <= SummonAngle;
                if (near || summon)
                {
                    Grab(waiting, h, summon, false);
                    return;
                }
                Haptics.Event("dice miss", h.IsLeft, 0.15f, 0.015f);
                if (Time.unscaledTime - s_missLog > 2f)
                {
                    s_missLog = Time.unscaledTime;
                    Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "dice: {0} grip not at the dice ({1:0.00} m away, pointing {2:0} deg off)",
                        h.IsLeft ? "L" : "R", dist / Scale, ang));
                }
            }
        }

        /// An open hand at the waiting dice: a "ready" hand (the fingers curl a little), the dice light up, a tick.
        static void HoverDice(DiceRoller r)
        {
            bool any = false;
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 1e-3f, 0.1f);
            Vector3 dice = s_gather(r).position;
            float reach = ClusterRadius(r) + GrabExtra * Scale;
            foreach (Hand h in VRInput.RL)
            {
                int i = h.IsLeft ? 1 : 0;
                bool hover = Settings.GrabThrow && HandWorld.Valid(h) && h.Grip < GrabOpen && Vector3.Distance(HandWorld.Palm(h), dice) <= reach;
                if (hover && !s_hover[i])
                {
                    Hovers++;
                    Haptics.Cue(Haptics.CueKind.Tick, h.IsLeft);
                    Plugin.L.LogInfo("dice: " + (h.IsLeft ? "L" : "R") + " open hand at the dice: ready");
                }
                s_hover[i] = hover;
                s_ready[i] = Mathf.MoveTowards(s_ready[i], hover ? 1f : 0f, dt * 8f);
                any |= hover;
            }
            Glow(dice, ClusterRadius(r), any, dt);
        }

        static void NotReady()
        {
            s_hover[0] = s_hover[1] = false;
            s_ready[0] = s_ready[1] = 0f;
            if (s_glow != null && s_glow.gameObject.activeSelf) s_glow.gameObject.SetActive(false);
        }

        /// A soft warm light round the dice while a hand is ready to take them (drawn over the scene, like the fingertip's).
        static void Glow(Vector3 at, float radius, bool on, float dt)
        {
            if (s_glow == null)
            {
                if (!on) return;
                Shader sh = Shader.Find("Sprites/Default");
                if (sh == null) return;
                const int n = 64;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "HoF2VR_DiceGlow", wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[n * n];
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                        float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                        px[y * n + x] = new Color32(255, 255, 255, (byte)(255f * a * a * (3f - 2f * a)));
                    }
                tex.SetPixels32(px);
                tex.Apply(false, true);
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                UnityEngine.Object.Destroy(go.GetComponent<Collider>());
                go.name = "HoF2VR_DiceGlow";
                go.layer = VRUI.Layer;
                UnityEngine.Object.DontDestroyOnLoad(go);
                s_glowMat = new Material(sh) { name = "HoF2VR_DiceGlow", mainTexture = tex, renderQueue = 5104 };
                MeshRenderer mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterial = s_glowMat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                s_glow = go.transform;
                s_glowMat.color = new Color(1f, 0.8f, 0.45f, 0f);
            }
            float a0 = s_glowMat.color.a, a1 = Mathf.MoveTowards(a0, on ? 0.24f : 0f, dt * 2f);
            if (a1 <= 0.001f) { if (s_glow.gameObject.activeSelf) s_glow.gameObject.SetActive(false); s_glowMat.color = new Color(1f, 0.8f, 0.45f, 0f); return; }
            if (!s_glow.gameObject.activeSelf) s_glow.gameObject.SetActive(true);
            s_glowMat.color = new Color(1f, 0.8f, 0.45f, a1);
            Vector3 eye = VRRig.I != null ? VRRig.I.HeadWorldPos : at + Vector3.up;
            s_glow.SetPositionAndRotation(at, Quaternion.LookRotation(at - eye, Vector3.up));
            float d = Mathf.Clamp(3f * radius, 0.08f * Scale, 0.22f * Scale);   // a little wider than the dice cluster
            s_glow.localScale = new Vector3(d, d, 1f);
        }

        static void Grab(DiceRoller r, Hand h, bool summon, bool synthetic)
        {
            NotReady();
            Holder = h;
            s_synthetic = synthetic;
            s_roller = r;
            s_grabT = Time.unscaledTime;
            s_grabFrom = s_gather(r).position;
            float d = Vector3.Distance(s_grabFrom, HoldPoint(h)) / Scale;
            s_slide = summon ? Mathf.Clamp(d / 2.5f, 0.15f, 0.4f) : 0.1f;
            s_untrackedSince = -1f;
            s_lastDir = Vector3.zero;
            Grabs++;
            if (summon) Summons++;
            Haptics.Event("dice grab", h.IsLeft, 0.3f, 0.025f);
            try { if (r.ActiveDice.Count > 0) r.ActiveDice[0].TriggerEffect("pickup"); } catch (Exception) { }
            Minigames.HideDiceHint();
            Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "dice: grabbed by {0} ({1}, {2:0.00} m from the hand){3}", h.IsLeft ? "L" : "R",
                summon ? "summoned" : "reached", d, synthetic ? " [seam]" : ""));
        }

        void Hold(DiceRoller waiting)
        {
            Hand h = Holder;
            if (waiting == null || waiting != s_roller)
            {
                // the game took the dice back (a dialogue, a card effect, the stick/A throw)
                Plugin.L.LogInfo("dice: hold ended by the game (state " + StateOf(s_roller) + ")");
                Holder = null;
                Cancels++;
                return;
            }
            if (!h.Tracked && !s_synthetic)
            {
                if (s_untrackedSince < 0f) s_untrackedSince = Time.unscaledTime;
                else if (Time.unscaledTime - s_untrackedSince > 0.5f) Release(Vector3.zero, Vector3.zero, "tracking lost");
                return;
            }
            s_untrackedSince = -1f;
            // GrabThrow: the dice drop only when the hand really opens (grip under 0.35: a relaxing hand at 0.54 kept dropping
            // them), and the throw is the swing's fastest moment (Alyx's peak window, x ThrowBoost) instead of the last 60 ms'
            // mean, which came out about a quarter short
            bool open = Settings.GrabThrow ? h.Grip < ReleaseGrip : !h.GripBtn;
            if (!s_synthetic && open)
            {
                Vector3 mean = HandWorld.MeanVel(h, 0.06f);
                Vector3 throwV = mean;
                if (Settings.GrabThrow)
                {
                    throwV = HandWorld.ThrowVel(h) * Settings.ThrowBoost;
                    LastEstimate = string.Format(CultureInfo.InvariantCulture, "peak {0:0.00} m/s (x{1:0.00}) vs mean {2:0.00}", throwV.magnitude / Scale, Settings.ThrowBoost, mean.magnitude / Scale);
                    Plugin.L.LogInfo("dice: throw estimate " + LastEstimate + " | speeds" + HandWorld.SpeedTrace(h, 12));
                }
                Release(throwV, HandWorld.AngVel(h), "let go");
                return;
            }
            // shake: a continuous buzz with the speed, a rattle and a tick at each reversal
            Vector3 v = HandWorld.Vel(h) / Scale;
            float speed = v.magnitude;
            Haptics.Continuous(h.IsLeft, 0.05f + 0.3f * Mathf.Clamp01(speed / FullSpeed));
            if (speed > 0.3f)
            {
                if (s_lastDir != Vector3.zero && Vector3.Angle(v, s_lastDir) > 100f && Time.unscaledTime - s_lastRattle > 0.09f)
                {
                    s_lastRattle = Time.unscaledTime;
                    Haptics.Event("dice rattle", h.IsLeft, 0.4f, 0.015f);
                    try
                    {
                        List<Die> dice = s_roller.ActiveDice;
                        if (dice.Count > 0) dice[UnityEngine.Random.Range(0, dice.Count)].TriggerEffect("diceCollision");
                    }
                    catch (Exception) { }
                }
                s_lastDir = v;
            }
        }

        /// After the game's own catapult step: the dice follow the hand.
        static void CatapaultPostfix(DiceRoller __instance)
        {
            if (Holder == null || __instance != s_roller || s_atGameSpot) return;
            Transform g = s_gather(__instance);
            if (g == null) return;
            Vector3 target = HoldPoint(Holder);
            float t = Mathf.Clamp01((Time.unscaledTime - s_grabT) / s_slide);
            g.position = t < 1f ? Vector3.Lerp(s_grabFrom, target, t * t * (3f - 2f * t)) : target;
            // the game's tumble and bunching follow its throw strength, which it derives from the pull
            // magnitude next frame: drive that with the hand's speed
            float speed = HandWorld.Vel(Holder).magnitude / Scale;
            float k = Mathf.Max(HoldMinStrength, Mathf.Clamp01(speed / FullSpeed));
            s_magnitude(__instance) = k * s_dragRadius(__instance);
        }

        /// Letting go: hand speed -> the game's throw strength, then the game's own SetReady().
        static void Release(Vector3 vel, Vector3 angVel, string why)
        {
            Hand h = Holder;
            DiceRoller r = s_roller;
            Holder = null;
            s_synthetic = false;
            s_atGameSpot = false;
            if (r == null || s_state(r) != DiceRoller.State.Catapault || !IdleValid(r))
            {
                Cancels++;
                Plugin.L.LogInfo("dice: let go while the game is not waiting for a throw - left to the game");
                return;
            }
            float speed = vel.magnitude / Scale;
            float strength = speed < DropSpeed ? 0f : Mathf.Clamp((speed - DropSpeed) / (FullSpeed - DropSpeed), 0.05f, 1f);
            s_strength(r) = strength;   // StaggerRollDice: 0 = the no-force roll at its own target
            s_magnitude(r) = 0f;
            int dice = 0;
            foreach (Die d in r.ActiveDice) if (d != null && d.Use) dice++;
            s_pending = new Pending { Roller = r, Vel = vel, AngVel = angVel, Strength = strength, T = Time.unscaledTime, Left = h.IsLeft, Dice = dice };
            s_landRoller = r;
            s_landLeft = h.IsLeft;
            s_landUntil = Time.unscaledTime + 4f;
            if (strength > 0f) Throws++; else Drops++;
            Vector3 local = VRRig.I != null ? Quaternion.Inverse(VRRig.I.NeutralRot) * vel / Scale : vel;
            LastThrow = string.Format(CultureInfo.InvariantCulture, "{0} {1:0.00} m/s -> strength {2:0.00} ({3})", h.IsLeft ? "L" : "R", speed, strength, strength > 0f ? "throw" : "drop");
            Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "dice: {0} - {1}: {2:0.00} m/s (right {3:0.00}, up {4:0.00}, ahead {5:0.00}) -> {6}",
                why, h.IsLeft ? "L" : "R", speed, local.x, local.y, local.z, strength > 0f ? "throw strength " + strength.ToString("0.00", CultureInfo.InvariantCulture) : "drop -> the game's no-force roll"));
            Haptics.Event("dice release", h.IsLeft, 0.5f, 0.04f);
            r.SetReady();
        }

        /// The game's trigger / stick throw: landing ticks on that hand too.
        public static void NoteClassicThrow(bool left)
        {
            s_landRoller = Active();
            s_landLeft = left;
            s_landUntil = Time.unscaledTime + 5f;
            s_pending = null;
        }

        /// Each die as the game launches it: bend toward the hand's throw, add its spin.
        static void RollPrefix(Die __instance, ref Vector3 a_direction, ref float a_force, ref Vector3 a_torque)
        {
            if (Time.unscaledTime - s_lastLaunch > 1f) s_thrown.Clear();
            s_lastLaunch = Time.unscaledTime;
            s_thrown.Add(__instance);
            Pending p = s_pending;
            s_lastWasVr = p != null;
            if (p == null) return;
            if (__instance.Roller != p.Roller || Time.unscaledTime - p.T > 20f) { s_pending = null; s_lastWasVr = false; return; }
            Vector3 gameDir = a_direction;
            float m = gameDir.magnitude;
            if (m > 1e-4f && p.Strength > 0f && p.Vel.sqrMagnitude > 1e-6f)
            {
                // the cone's axis: the game's catapult aim (from its catapult spot at its target), not
                // the game's aim from where the hand let go - released past the target, that one would
                // point back at the player
                Vector3 n = gameDir / m;
                Transform from = s_dragCentre(p.Roller), to = s_target(p.Roller);
                if (from != null && to != null && (to.position - from.position).sqrMagnitude > 1e-6f) n = (to.position - from.position).normalized;
                Vector3 bent = Vector3.Slerp(n, p.Vel.normalized, HandInfluence);
                bent = Vector3.RotateTowards(n, bent, ConeDeg * Mathf.Deg2Rad, 0f);
                if (bent.y > MaxUp)
                {
                    Vector3 flat = new Vector3(bent.x, 0f, bent.z);
                    flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : new Vector3(n.x, 0f, n.z).normalized;
                    bent = flat * Mathf.Sqrt(1f - MaxUp * MaxUp) + Vector3.up * MaxUp;
                }
                a_direction = bent.normalized * m;
            }
            Vector3Range tr = s_torque(p.Roller);
            float meanTorque = tr != null ? (tr.Min.magnitude + tr.Max.magnitude) * 0.5f : 0f;
            Vector3 spin = p.AngVel * (SpinShare * meanTorque);
            a_torque += spin;
            if (p.Done == 0)
                Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "dice: launch from {0}: game {1} -> {2} (hand {3}), force {4:0.###}, torque {5} (+hand spin {6})",
                    Diag.V(__instance.transform.position), Diag.V(gameDir), Diag.V(a_direction), p.Strength > 0f ? Diag.V(p.Vel.normalized) : "drop",
                    a_force, Diag.V(a_torque - spin), Diag.V(spin)));
            p.Done++;
            if (p.Done >= Mathf.Max(1, p.Dice)) s_pending = null;
        }

        static void CollisionPostfix(Die __instance, Collision a_collision)
        {
            float now = Time.unscaledTime;
            if (now > s_landUntil || __instance.Roller != s_landRoller || now - s_lastLand < 0.04f || a_collision == null) return;
            s_lastLand = now;
            float v = a_collision.relativeVelocity.magnitude / Scale;
            Haptics.Event("dice land", s_landLeft, Mathf.Clamp(v / 3f, 0.1f, 0.35f), 0.015f);
        }

        static readonly AccessTools.FieldRef<DiceGame, Collider[]> s_sideCols = AccessTools.FieldRefAccess<DiceGame, Collider[]>("m_sideColliders");
        public static int InTray, OutOfTray;

        /// Where thrown dice may come to rest: the card table's top or the dice tray's floor (the game
        /// throws them forward out of the tray onto the table; only the walls on the player's side are on).
        static bool OnTable(Vector3 p)
        {
            Bounds table;
            if (ReachProbe.TableBounds(out table) && p.x >= table.min.x && p.x <= table.max.x && p.z >= table.min.z && p.z <= table.max.z &&
                Mathf.Abs(p.y - table.max.y) < 0.06f) return true;
            GameObject bottom = GameObject.Find("Models/Card Table and Table Objects/DiceGame/Colliders/Bottom");
            Collider c = bottom != null ? bottom.GetComponent<Collider>() : null;
            if (c == null) return false;
            Bounds b = c.bounds;
            return p.x >= b.min.x && p.x <= b.max.x && p.z >= b.min.z && p.z <= b.max.z && p.y > b.min.y && p.y < b.max.y + 0.06f;
        }

        static void EvaluatePostfix(DiceRoller __instance)
        {
            if (s_thrown.Count == 0) return;
            var faces = new List<string>();
            int inTray = 0;
            var where = new System.Text.StringBuilder();
            foreach (Die d in s_thrown)
            {
                if (d == null) continue;
                int v = d.Value;
                if (v >= 1 && v <= 6) { s_faces[v]++; if (s_lastWasVr) s_facesVr[v]++; }
                faces.Add(v.ToString());
                Vector3 p = d.transform.position;
                if (OnTable(p)) inTray++;
                where.Append(' ').Append(Diag.V(p));
            }
            InTray += inTray;
            OutOfTray += s_thrown.Count - inTray;
            Plugin.L.LogInfo("dice: came to rest at" + where + (inTray < s_thrown.Count ? " - " + (s_thrown.Count - inTray) + " NOT on the table" : ""));
            if (s_lastWasVr) VrRolls++; else ClassicRolls++;
            LastResult = string.Join(",", faces.ToArray()) + (s_lastWasVr ? " (hand)" : " (classic)") + " on table " + inTray + "/" + s_thrown.Count;
            Plugin.L.LogInfo("dice: landed " + LastResult + " in " + (Time.unscaledTime - s_lastLaunch).ToString("0.0", CultureInfo.InvariantCulture) + " s");
            s_thrown.Clear();
        }

        public static string Status()
        {
            DiceRoller r = Active();
            string st = r == null ? "-" : StateOf(r);
            if (Holder != null) st += " held=" + (Holder.IsLeft ? "L" : "R");
            if (Minigames.Drawn) st += " drawn";
            if (r != null && s_state(r) == DiceRoller.State.Marking)
            {
                int marked = 0;
                foreach (Die d in r.ActiveDice) if (d != null && d.Marked) marked++;
                st += " marked=" + marked;
            }
            return st + " | ready " + (s_hover[1] ? "L" : "") + (s_hover[0] ? "R" : "") + " hovers " + Hovers + " | grabs " + Grabs + " (summoned " + Summons + ") throws " + Throws + " drops " + Drops + " cancels " + Cancels +
                   " | estimate " + LastEstimate +
                   " | last " + LastThrow + " | landed " + LastResult + " | rolls hand " + VrRolls + " classic " + ClassicRolls;
        }

        static string Histogram(int[] f)
        {
            int n = 0;
            for (int i = 1; i <= 6; i++) n += f[i];
            var sb = new System.Text.StringBuilder();
            for (int i = 1; i <= 6; i++) sb.AppendFormat(CultureInfo.InvariantCulture, "{0}:{1} ", i, f[i]);
            return sb.Append("(n=").Append(n).Append(')').ToString();
        }

        // ---- test seam ---------------------------------------------------------------------------
        //   dice                       status
        //   dice grab [l|r]            take the waiting dice into that hand (as if summoned)
        //   dice throw vx vy vz [l|r] [game]  throw them with that hand velocity (m/s; right, up, ahead);
        //                              `game`: from where the game gathered them, not from the hand
        //   dice hist [reset]          faces landed so far (all / hand throws)
        static Hand Side(string[] a, int i) => a.Length > i && a[i].StartsWith("l", StringComparison.OrdinalIgnoreCase) ? VRInput.L : VRInput.R;
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        static string Command(string[] a)
        {
            string op = a.Length > 1 ? a[1].ToLowerInvariant() : "";
            if (op == "hist")
            {
                if (a.Length > 2 && a[2] == "reset") { Array.Clear(s_faces, 0, 7); Array.Clear(s_facesVr, 0, 7); VrRolls = ClassicRolls = InTray = OutOfTray = 0; }
                return "all " + Histogram(s_faces) + " | hand " + Histogram(s_facesVr) + " | on table " + InTray + " off " + OutOfTray;
            }
            if (op == "grab" || op == "throw")
            {
                if (VRRig.I == null) return "no rig";
                DiceRoller r = Waiting();
                Hand h = Side(a, op == "throw" ? 5 : 2);
                if (op == "throw" && a.Length > 5 && a[5] == "game") h = VRInput.R;
                if (Holder == null)
                {
                    if (r == null) return "the dice are not waiting for a throw (" + Status() + ")";
                    Grab(r, h, true, true);
                    // `... game`: leave the dice where the game gathered them (tests the throw alone)
                    s_atGameSpot = a[a.Length - 1] == "game";
                }
                if (op == "grab") return "holding (" + Status() + ")";
                if (a.Length < 5) return "usage: dice throw vx vy vz [l|r]";
                Vector3 local = new Vector3(F(a[2]), F(a[3]), F(a[4]));
                Vector3 world = VRRig.I.NeutralRot * local * Scale;
                I.StartCoroutine(ThrowLater(world));
                return "throwing " + Diag.V(local) + " m/s";
            }
            if (op == "release")
            {
                if (Holder == null) return "not holding";
                Release(Vector3.zero, Vector3.zero, "seam release");
                return "released";
            }
            return Status();
        }

        static IEnumerator ThrowLater(Vector3 worldVel)
        {
            yield return new WaitForSecondsRealtime(0.5f);
            if (Holder == null) yield break;
            Release(worldVel, Vector3.zero, "seam throw");
        }
    }
}

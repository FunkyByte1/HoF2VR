using System.Collections.Generic;
using Defiant.Cameras;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace HoF2VR
{
    /// First-person combat. While combat is live the rig stands on the hero's floor spot: the player's
    /// real floor is the arena floor (so they see it from their real height), yaw turned with the
    /// right stick, real head motion on top. The view only moves with the hero while the PLAYER
    /// moves it (walking, dodging, their own attacks and counters); when the game moves the hero by
    /// itself (hit reactions, knock-backs, knock-downs, stuns, spawning) the view stays put, and a
    /// gap left behind is closed while the player next moves, or by a blink once the hero is free.
    /// The hero's body renders shadows-only; the real sword and shield models move to the VR hands
    /// (hits are animation-driven, so this is visual, and trails/hit effects follow the real swing).
    internal class CombatVR : MonoBehaviour
    {
        public static CombatVR I;
        public static bool Active;

        PlayerController m_pc;
        float m_yaw;
        Vector3 m_body;          // the view's floor point (smoothed)
        Vector3 m_follow;        // where the view's floor point wants to be
        Vector3 m_lastHero;
        bool m_snapped;
        string m_action = "", m_lastAction = "";
        bool m_playerDriven;
        float m_freeSince;       // when the hero last became free (non-player action ended)
        float m_actionStart;
        Vector3 m_actionFrom;
        public static int Decoupled, Blinks;   // diagnostics: frames the view ignored hero motion; catch-up blinks
        public Transform HandR, HandL;
        float m_nextScan;

        struct Saved { public ShadowCastingMode Mode; }
        readonly Dictionary<Renderer, ShadowCastingMode> m_hidden = new Dictionary<Renderer, ShadowCastingMode>();

        class Held
        {
            public Transform T, Parent;
            public Vector3 LocalPos, LocalScale;
            public Quaternion LocalRot;
            public bool Right, Shield;
            public Vector3 Long, Thin, Mid;      // model-local axes from the mesh bounds
            public Vector3 Center;               // model-local bounds center (unscaled)
            public float Length;
            public float Grip;                   // model-local distance from the origin up Long to the grip (VRHands.FindGrip)
            public Vector3 Up;                   // shields: model-local axis that points up when held
            public bool HasHandle;               // shields: the handle was found in the mesh
            public Vector3 Handle;               // shields: model-local middle of the handle (VRHands.FindShieldGrip)
            public Quaternion BaseRot;           // the hold Place set (hand-local), before ShieldUpright's turn
            public Vector3 BasePos;
            public bool HasBase;
            public Vector3 OldUp, MeshUp;        // shields: the up the hold used before P4-12 (the bounds / game-pose guess), the mesh's
            public int FitVersion = -1;          // shields: HandGeometry.Version when Place last ran (the glove fit it followed)
        }

        /// P4-12: the last held shield's up and how it was found (diagnostics); `shieldup old|mesh` (tests): the old guess back for
        /// a before/after look.
        public static string LastShieldUp = "-";
        public static bool OldShieldUp;

        /// Model-local bounds of all meshes under t (in t's local space, unscaled).
        internal static Bounds LocalBounds(Transform t)
        {
            var b = new Bounds();
            bool any = false;
            foreach (MeshFilter mf in t.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Bounds mb = mf.sharedMesh.bounds;
                Matrix4x4 m = t.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 p = m.MultiplyPoint3x4(c);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            }
            foreach (SkinnedMeshRenderer smr in t.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Bounds wb = smr.bounds;
                Vector3 p0 = t.InverseTransformPoint(wb.min), p1 = t.InverseTransformPoint(wb.max);
                if (!any) { b = new Bounds(p0, Vector3.zero); any = true; }
                b.Encapsulate(p0);
                b.Encapsulate(p1);
            }
            return b;
        }

        internal static void Axes(Bounds b, out Vector3 longest, out Vector3 mid, out Vector3 thinnest)
        {
            Vector3 s = b.size;
            var axes = new[] { new KeyValuePair<float, Vector3>(s.x, Vector3.right), new KeyValuePair<float, Vector3>(s.y, Vector3.up), new KeyValuePair<float, Vector3>(s.z, Vector3.forward) };
            System.Array.Sort(axes, (p, q) => q.Key.CompareTo(p.Key));
            longest = axes[0].Value;
            mid = axes[1].Value;
            thinnest = axes[2].Value;
            // point "longest" from the grip (model origin) toward the far end of the mesh
            if (Vector3.Dot(b.center, longest) < 0f) longest = -longest;
            // shields: the body (and its boss) sits in front of the strap/attach point at the origin,
            // so the front face points toward the bounds center
            if (Vector3.Dot(b.center, thinnest) < 0f) thinnest = -thinnest;
        }
        readonly Dictionary<int, Held> m_held = new Dictionary<int, Held>();

        // Grip tuning in controller space (Unity: +Z = pointing direction, +Y = top of the controller).
        // The blade leaves the fist tilted up from the pointing axis; the shield is held center-grip,
        // its face along the pointing axis. `grip sword|shield <tilt> <forward m> <up m>` tunes live.
        // extra tuning on top of the controller geometry (see ControllerGeometry)
        public static float SwordTilt = 0f, SwordForward = 0.0f, SwordUp = 0.0f;
        public static float ShieldTilt = 0f, ShieldForward = 0.07f, ShieldUp = 0.0f, ShieldHandleForward = 0f;
        public static int ShieldFromPose;

        public float Yaw => m_yaw;
        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        // ---- no push-backs -------------------------------------------------------------------
        // The game knocks the hero back when it is hit (ActionHit: root motion plus a scripted push)
        // and lets disabled states (stunned, snared, knocked down...) drift it with their animation.
        // In first person that moved the player without their input (or opened a gap between the view
        // and the hero, closed later by a blink), so in VR the hero stays where it is: the reaction
        // animation and turn still play, only the displacement is dropped.
        public static bool NoPushBack = true;   // test toggle: `push 0|1` (1 = the game's push-backs)
        public static float PushDropped;        // diagnostics: metres of push-back not applied

        public static void Apply(HarmonyLib.Harmony h)
        {
            var noDelta = new HarmonyLib.HarmonyMethod(typeof(CombatVR), nameof(DropDelta));
            h.Patch(HarmonyLib.AccessTools.Method(typeof(ActionHit), "OnDeltaMove"), prefix: noDelta);
            h.Patch(HarmonyLib.AccessTools.Method(typeof(ActionDisabled), "OnDeltaMove"), prefix: noDelta);
            h.Patch(HarmonyLib.AccessTools.Method(typeof(ActionHit), "MoveToProgress"), prefix: new HarmonyLib.HarmonyMethod(typeof(CombatVR), nameof(SkipPush)));
            Diag.Commands["push"] = a => { if (a.Length > 1) NoPushBack = a[1] == "0"; return "push-backs " + (NoPushBack ? "off" : "on") + ", dropped " + PushDropped.ToString("0.00") + " m"; };
            AssassinRoom.Apply(h);   // P4-14: the assassins land further away
        }

        static bool IsHero(Defiant.Action a)
        {
            if (!NoPushBack || !Active || !XRSettings.enabled || a == null || a.Actor == null) return false;
            PlayerController pc = PlayerController.Instance;
            return pc != null && a.Actor.gameObject == pc.gameObject;
        }

        static void DropDelta(Defiant.Action __instance, ref Vector3 a_deltaPosition)
        {
            if (!IsHero(__instance)) return;
            PushDropped += new Vector2(a_deltaPosition.x, a_deltaPosition.z).magnitude;
            a_deltaPosition = Vector3.zero;
        }

        static bool SkipPush(ActionHit __instance) => !IsHero(__instance);

        void Awake()
        {
            I = this;
            HandR = new GameObject("HoF2VR_HandR").transform;
            HandL = new GameObject("HoF2VR_HandL").transform;
            DontDestroyOnLoad(HandR.gameObject);
            DontDestroyOnLoad(HandL.gameObject);
            Diag.StatusExtras["combat"] = () => Diag.Q(Active ? "fp yaw " + m_yaw.ToString("0") + " view " + Diag.V(m_body) + " action " + m_action +
                (m_playerDriven ? "" : " (view held)") + " gap " + (m_pc != null ? Vector3.Distance(Flat(m_pc.transform.position), Flat(m_body)).ToString("0.00") : "-") +
                " decoupled " + Decoupled + " blinks " + Blinks + " pushDropped " + PushDropped.ToString("0.0") + " held " + m_held.Count + " hidden " + m_hidden.Count : "off");
            Diag.Commands["blink"] = a =>
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (a.Length > 2) { BlinkGap = float.Parse(a[1], inv); BlinkAfter = float.Parse(a[2], inv); }
                return "blink gap " + BlinkGap + " m after " + BlinkAfter + " s";
            };
            Diag.Commands["grip"] = a =>
            {
                var v = new float[3];
                for (int i = 0; i < 3; i++) v[i] = float.Parse(a[2 + i], System.Globalization.CultureInfo.InvariantCulture);
                if (a[1] == "sword") { SwordTilt = v[0]; SwordForward = v[1]; SwordUp = v[2]; }
                else { ShieldTilt = v[0]; ShieldForward = v[1]; ShieldUp = v[2]; }
                foreach (var h in m_held.Values) Place(h);
                return "ok";
            };
            // P4-12 seams: `shieldpose?` (the held shield's clock turn from upright, ShieldPose); `shieldout l|r` (the xrsim rot that
            // holds that hand straight out); `shieldup old|mesh` (the old up guess back, for a before/after look; this run only);
            // `ctrltype touch|knuckles|auto` (the controller geometry as if Quest Touch were connected: TipPitchDown, the glove fit)
            Diag.Commands["shieldpose?"] = a => ShieldPose();
            // `glovefit [cfg | default | tilt|turn|roll|up|along|side <v> ...]`: the glove fit for this run only (live values, never
            // saved; cfg = the player's saved fit again)
            Diag.Commands["glovefit"] = a =>
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                for (int i = 1; i < a.Length; i++)
                {
                    if (a[i] == "cfg") Settings.LiveGloveAngle = Settings.LiveGloveTurn = Settings.LiveGloveRoll = Settings.LiveGloveUp = Settings.LiveGloveAlong = Settings.LiveGloveSide = float.NaN;
                    else if (a[i] == "default") Settings.ResetGloveFit();
                    else if (i + 1 < a.Length) { Settings.SetGloveFit(a[i], float.Parse(a[i + 1], inv)); i++; }
                }
                return "glove " + Settings.GloveFitText();
            };
            Diag.Commands["shieldout"] = a => ShieldOut(a.Length < 2 || a[1] != "r", a.Length > 2 && a[2] == "zero");
            Diag.Commands["shieldup"] = a =>
            {
                if (a.Length > 1) OldShieldUp = a[1] == "old";
                foreach (var h in m_held.Values) if (h.Shield) { h.Up = OldShieldUp ? h.OldUp : h.MeshUp; Place(h); }
                return "shield up " + (OldShieldUp ? "old (the bounds / game-pose guess)" : "from the mesh");
            };
            Diag.Commands["ctrltype"] = a =>
            {
                if (a.Length > 1) ControllerGeometry.Forced = a[1] == "auto" ? null : a[1] == "touch" ? "oculus_touch" : a[1];
                if (ControllerGeometry.Forced != null) ControllerGeometry.Type = ControllerGeometry.Forced;
                foreach (var h in m_held.Values) Place(h);
                return "controllers '" + ControllerGeometry.Type + "'" + (ControllerGeometry.Forced != null ? " (forced)" : "") + ", glove " + Settings.GloveFitText();
            };
        }

        static bool WantActive()
        {
            // first person from the moment the hero stands in the arena (the enemy showcase before the
            // fight included: following its camera cuts meant a string of fades), through the pause menu
            string st = GameWatch.State;
            if (!XRSettings.enabled || !(GameWatch.InCombatPlay || st == "GameState_Level_Pause" || st == "GameState_Level_In" ||
                                         st == "GameState_Level_Out" || st == "GameState_BossBattle_In" || st == "GameState_BossBattle_Out")) return false;
            PlayerController pc = PlayerController.Instance;
            PlayerCamera cam = PlayerCamera.Instance;
            return pc != null && cam != null && cam.Camera != null && cam.Camera.enabled;
        }

        void LateUpdate()
        {
            bool want = WantActive();
            if (want && !Active) Enter();
            else if (!want && Active) Exit();
            CombatHud.OutOfActionTick(Active);
            if (!Active) return;
            if (m_pc == null) { Exit(); return; }

            Turn();
            FollowHero();
            VRRig.I.Mode = RigMode.Body;
            VRRig.I.Scale = 1f;
            VRRig.I.BodyYaw = m_yaw;
            VRRig.PreferredPrimary = s_playerCam;

            HandsUpdate();
            if (Time.unscaledTime >= m_nextScan)
            {
                m_nextScan = Time.unscaledTime + 0.5f;
                HideBody();
                HoldEquipment();
            }
            ShieldUpright();
            PlayerCamera pcam = PlayerCamera.Instance;
            if (pcam != null && pcam.Listener != null) pcam.Listener.transform.position = VRRig.I.HeadWorldPos;
        }

        // Hero actions that move the hero because the player asked for it (stick, attack, dodge,
        // counter, finisher, ability, block). Anything else that moves the hero is the game's doing.
        static readonly string[] s_playerActions =
        {
            "ActionMove", "ActionIdle", "ActionDodge", "ActionMelee", "ActionMeleeFlurry", "ActionCounter",
            "ActionMeleeProneFinisher", "ActionMeleeKallasFinisher", "ActionAbility", "ActionAbilityFail", "ActionBlock", "ActionEvade",
            "ActionCompanionPlayerInteraction",
        };

        /// A gap the game left (knock-back) that the player has not closed with their own moves this long
        /// after the hero is free again is closed with a blink.
        public static float BlinkGap = 1.0f, BlinkAfter = 1.5f;
        const float NearClip = 0.05f;

        static bool IsPlayerAction(string type)
        {
            if (type.Length == 0) return true;
            foreach (string a in s_playerActions) if (type == a) return true;
            return false;
        }

        string LowerBodyAction()
        {
            Actor actor = m_pc.GetComponent<Actor>();
            Defiant.Action a = actor != null ? actor.RunningActionLowerBody : null;
            return a != null ? a.GetType().Name : "";
        }

        void FollowHero()
        {
            Vector3 hero = m_pc.transform.position;
            if (!m_snapped)
            {
                m_follow = m_body = m_lastHero = hero;
                m_snapped = true;
            }
            Vector3 delta = hero - m_lastHero;
            m_lastHero = hero;

            m_action = LowerBodyAction();
            bool playerDriven = IsPlayerAction(m_action);
            Vector3 walked = CombatMove.ConsumeWalked();   // the player's own steps during a game action (a hit reaction)
            if (m_action != m_lastAction)
            {
                // motion audit: how far each hero action moved the hero, and whether the view went along
                if (m_lastAction.Length > 0)
                {
                    Vector3 moved = hero - m_actionFrom;
                    moved.y = 0f;
                    if (moved.magnitude > 0.05f)
                        Plugin.L.LogInfo("hero action " + m_lastAction + " moved the hero " + moved.magnitude.ToString("0.00") + " m in " +
                                         (Time.unscaledTime - m_actionStart).ToString("0.00") + " s (" + (IsPlayerAction(m_lastAction) ? "player" : "GAME - view stayed") + ")");
                }
                m_lastAction = m_action;
                m_actionStart = Time.unscaledTime;
                m_actionFrom = hero;
                if (playerDriven && !m_playerDriven) m_freeSince = Time.unscaledTime;
            }
            m_playerDriven = playerDriven;

            float jump = delta.magnitude;
            if (jump > 2.5f)
            {
                // the game put the hero somewhere else (teleport effect, spawn, scripted move); a hero
                // dropped far below the arena (the finisher "disappear" trick) is ignored until it is back
                if (hero.y < m_follow.y - 10f) return;
                Blink(hero, "hero teleported " + jump.ToString("0.0") + " m");
            }
            else if (playerDriven)
            {
                // the player's own motion moves the view 1:1; a gap left by a knock-back closes at half
                // the player's speed on top, so it is never felt as motion of its own
                m_follow += delta;
                Vector3 gap = hero - m_follow;
                float step = delta.magnitude * 0.5f;
                if (gap.sqrMagnitude > 1e-6f && step > 0f) m_follow += Vector3.ClampMagnitude(gap, step);
                // vertical: stay on the hero's ground (slopes, steps)
                m_follow.y = Mathf.MoveTowards(m_follow.y, hero.y, Mathf.Max(Mathf.Abs(delta.y), delta.magnitude * 0.5f));
                Vector3 g = hero - m_follow;
                g.y = 0f;
                // still far from the hero a moment after a knock-back ended and the player is not moving:
                // blink back onto it rather than leave them fighting from the wrong spot
                if (g.magnitude > BlinkGap && Time.unscaledTime - m_freeSince > BlinkAfter && jump < 0.002f) Blink(hero, "back on the hero (" + g.magnitude.ToString("0.0") + " m)");
            }
            else
            {
                Decoupled++;
                m_follow += walked;   // the view follows the player's own walking, even while the game plays a hit on the hero
            }

            if (!VRFade.Busy || VRFade.IsBlack)
                m_body = Vector3.Lerp(m_body, m_follow, 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime));
        }

        void Blink(Vector3 to, string why)
        {
            if (VRFade.Busy && !VRFade.IsBlack) return;
            Blinks++;
            VRFade.I.FadeSnap(() => { m_follow = m_body = to; }, why);
        }

        public Vector3 ViewFloor => m_body;
        /// The rig's anchor in fights: the view's floor point, plus UYF's camera shake (it moves the eyes and hands together).
        Vector3 FloorPos() => m_body + CameraShake.Offset;

        /// The view follows the hero's own motion now (its action is the player's: idle, walking, the dash...); during a game
        /// action only the steps reported through CombatMove.ConsumeWalked move it.
        public static bool HeroMovesView
        {
            get
            {
                if (I == null || I.m_pc == null) return true;
                try { return IsPlayerAction(I.LowerBodyAction()); } catch (System.Exception) { return true; }
            }
        }

        static readonly System.Func<Camera> s_playerCam = () => PlayerCamera.Instance != null ? PlayerCamera.Instance.Camera : null;

        void Enter()
        {
            m_pc = PlayerController.Instance;
            Active = true;
            m_snapped = false;
            m_lastAction = m_action = "";
            m_yaw = m_pc.transform.eulerAngles.y;
            m_body = m_follow = m_lastHero = m_pc.transform.position;
            VRRig.I.BodyFloorPos = FloorPos;
            VRRig.I.Scale = 1f;
            VRRig.I.Mode = RigMode.Body;
            // the shield and sword come close to the eyes in first person: the flat game's 0.15 m near
            // plane cut them (and enemy blades) off
            Camera pcam = PlayerCamera.Instance != null ? PlayerCamera.Instance.Camera : null;
            if (pcam != null && pcam.nearClipPlane > NearClip)
            {
                Plugin.L.LogInfo("CombatVR: near clip plane " + pcam.nearClipPlane + " -> " + NearClip + " m");
                pcam.nearClipPlane = NearClip;
            }
            // the trip here happened in the dark (Places); face the arena the way the hero does
            if (VRFade.IsBlack) VRRig.I.Recenter();
            else VRFade.I.FadeSnap(() => { VRRig.I.Recenter(); }, "combat start");
            Plugin.L.LogInfo("CombatVR: first person on (hero at " + Diag.V(m_body) + ", eye height " + VRRig.I.EyeHeight.ToString("0.00") + " m real)");
        }

        void Exit()
        {
            Active = false;
            foreach (var kv in m_hidden) if (kv.Key != null) kv.Key.shadowCastingMode = kv.Value;
            m_hidden.Clear();
            foreach (Held h in m_held.Values)
            {
                if (h.T == null) continue;
                if (h.Parent == null)
                {
                    // its owner (the hero's model) is already gone and would have taken it along: without this a pooled
                    // weapon stayed in the hand into the next fight (weapon smoke test 2026-10-11: after a weapon change
                    // the old hammer still hung in the right hand next to the new daggers)
                    UnityEngine.Object.Destroy(h.T.gameObject);
                    Orphans++;
                    continue;
                }
                h.T.SetParent(h.Parent, false);
                h.T.localPosition = h.LocalPos;
                h.T.localRotation = h.LocalRot;
                h.T.localScale = h.LocalScale;
            }
            m_held.Clear();
            if (VRRig.I != null)
            {
                // stay exactly where the view is until the next place takes over (in the dark)
                VRRig.I.SetStation(m_body, m_yaw, 1f, "end of fight");
                VRRig.I.BodyFloorPos = null;
            }
            if (VRRig.PreferredPrimary == s_playerCam) VRRig.PreferredPrimary = null;
            m_pc = null;
            Comfort.TurnRate = 0f;
            Plugin.L.LogInfo("CombatVR: first person off" + (Orphans > 0 ? " (" + Orphans + " held models without owner removed so far)" : ""));
        }

        float m_snapCooldown;
        public static int Orphans;   // held models whose owner was gone at the end of a fight (destroyed)

        public static int SnapTurns;   // diagnostics
        public static int SmartSnaps;
        public static string LastSmartSnap = "-";

        /// UYF's smart snap turn (its WorldMover, docs/uyf-parity-audit.md C1; design §7.1.3): in a fight, a snap toward the
        /// nearest enemy on that side - within 182 deg of the gaze that way and 20 m, turning at least 8 and at most 190 deg -
        /// else the fixed step. +1 = right.
        public float SnapStep(int side)
        {
            float step = side * Settings.SnapAngle;
            if (!CombatSettings.SmartSnap || !Physical.Live || VRRig.I == null) return step;
            try
            {
                PlayerController pc = PlayerController.Instance;
                Targetable me = pc != null ? pc.Targetable : null;
                if (me == null) return step;
                Vector3 head = VRRig.I.HeadWorldPos;
                float gaze = VRRig.I.HeadWorldRot.eulerAngles.y;
                BetterList<Targetable> all = me.EnumerateWithinRadius(20f, me.GetEnemyTeams());
                float best = float.MaxValue, turn = 0f;
                string who = "";
                for (int i = 0; i < all.size; i++)
                {
                    Targetable t = all[i];
                    Destroyable d = t != null ? t.GetComponent<Destroyable>() : null;
                    if (t == null || (d != null && d.IsDead) || t.Controller == null || t.Hidden) continue;
                    Vector3 to = t.transform.position - head;
                    to.y = 0f;
                    if (to.magnitude > 20f || to.sqrMagnitude < 1e-4f) continue;
                    float rel = Mathf.DeltaAngle(gaze, Quaternion.LookRotation(to).eulerAngles.y);   // + = right
                    float onSide = side * rel;
                    if (onSide < 0f) onSide += 360f;   // (a target just past straight behind on the other side)
                    if (onSide < 8f || onSide > 182f) continue;   // UYF: at least 8 deg, within its 182 deg arc that way
                    // the nearest by angle: the next enemy that way
                    if (onSide < best) { best = onSide; turn = onSide; who = WeaponGrips.ModelKey(t.name); }
                }
                if (turn <= 0f) return step;
                SmartSnaps++;
                LastSmartSnap = who + " " + turn.ToString("0") + " deg " + (side > 0 ? "right" : "left");
                return side * Mathf.Min(turn, 190f);
            }
            catch (System.Exception) { return step; }
        }

        void Turn()
        {
            float x = VRInput.R.Stick.x;
            Comfort.TurnRate = 0f;
            if (Settings.SnapTurn)
            {
                m_snapCooldown -= Time.unscaledDeltaTime;
                if (Mathf.Abs(x) > 0.7f && m_snapCooldown <= 0f)
                {
                    float step = SnapStep(x > 0f ? 1 : -1);
                    SnapTurns++;
                    // SnapTurnBlink: the turn happens while the view is dark for a moment
                    if (Settings.SnapTurnBlink && VRFade.I != null) VRFade.I.QuickBlink(() => { m_yaw = Mathf.Repeat(m_yaw + step, 360f); });
                    else m_yaw += step;
                    m_snapCooldown = 0.3f;
                }
                if (Mathf.Abs(x) < 0.3f) m_snapCooldown = 0f;
            }
            else if (Mathf.Abs(x) > 0.15f)
            {
                float s = (Mathf.Abs(x) - 0.15f) / 0.85f * Mathf.Sign(x);
                float rate = s * Settings.SmoothTurnSpeed;
                m_yaw += rate * Time.unscaledDeltaTime;
                Comfort.TurnRate = rate;   // the turn vignette follows it
            }
            m_yaw = Mathf.Repeat(m_yaw, 360f);
        }

        /// Turns the view to face a world point, the way a snap turn does (Physical mode's optional counter-turn).
        public bool FaceTowards(Vector3 p, bool blink = true)
        {
            if (!Active) return false;
            float step = ThreatWarning.BearingOf(p);
            if (Mathf.Abs(step) < 5f) return false;
            if (blink && Settings.SnapTurnBlink && VRFade.I != null) VRFade.I.QuickBlink(() => { m_yaw = Mathf.Repeat(m_yaw + step, 360f); });
            else m_yaw = Mathf.Repeat(m_yaw + step, 360f);
            SnapTurns++;
            return true;
        }

        void HandsUpdate()
        {
            if (VRInput.R.Tracked)
            {
                HandR.position = VRRig.I.ToWorld(VRInput.R.Pos);
                HandR.rotation = VRRig.I.ToWorld(VRInput.R.Rot);
            }
            if (VRInput.L.Tracked)
            {
                HandL.position = VRRig.I.ToWorld(VRInput.L.Pos);
                HandL.rotation = VRRig.I.ToWorld(VRInput.L.Rot);
            }
        }

        bool IsHeldRenderer(Renderer r)
        {
            foreach (Held h in m_held.Values)
                if (h.T != null && r.transform.IsChildOf(h.T)) return true;
            return false;
        }

        void HideBody()
        {
            Model model = m_pc.Model;
            if (model == null) return;
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (IsHeldRenderer(r) || m_hidden.ContainsKey(r)) continue;
                m_hidden[r] = r.shadowCastingMode;
                r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            }
        }

        void HoldEquipment()
        {
            Inventory inv = m_pc.Inventory;
            if (inv == null) return;
            bool leftHanded = Settings.LeftHanded;
            if (inv.Weapon != null && inv.Weapon.AllModels != null)
            {
                int i = 0;
                foreach (Model m in inv.Weapon.AllModels)
                {
                    if (m == null) continue;
                    // dual wield: second weapon goes to the other hand
                    bool right = (i++ == 0) != leftHanded;
                    Hold(m.transform, right, false);
                }
            }
            // the shield slot decides, not the model's name ("BastionOfPurificationModel" is a shield)
            if (inv.Shield != null && inv.Shield.Model != null && inv.Shield.Model != m_pc.Model)
                Hold(inv.Shield.Model.transform, leftHanded, true);
        }

        void Hold(Transform t, bool right, bool shield)
        {
            int id = t.GetInstanceID();
            Held h;
            if (m_held.TryGetValue(id, out h) && h.T == t && t.parent == (h.Right ? HandR : HandL)) return;
            if (h == null)
            {
                h = new Held { T = t, Parent = t.parent, LocalPos = t.localPosition, LocalRot = t.localRotation, LocalScale = t.localScale, Right = right };
                h.Shield = shield;
                Bounds lb = LocalBounds(t);
                Axes(lb, out h.Long, out h.Mid, out h.Thin);
                h.Center = lb.center;
                h.Length = Vector3.Scale(lb.size, t.lossyScale).magnitude;
                // swords and daggers have their origin at the pommel end: put the grip in the fist
                if (!h.Shield) h.Grip = VRHands.FindGrip(t, h.Long);
                else
                {
                    // a long shield (kite, tower) is held upright by its long axis, its wide end up (the
                    // side the vertices crowd towards); a round one keeps its middle axis up. The fist
                    // closes round its handle.
                    float aspect = Vector3.Scale(lb.size, h.Long).magnitude / Mathf.Max(1e-4f, Vector3.Scale(lb.size, h.Mid).magnitude);
                    float upDot = Vector3.Dot(VRHands.MeshCentroid(t) - lb.center, h.Long);
                    h.Up = aspect > 1.25f ? (upDot >= 0f ? h.Long : -h.Long) : h.Mid;
                    // F7 (the player's playtest 2026-10-17, mark 18: the shield sat turned in the hand, its front toward the
                    // player): the game's own hold decides, read now while the model still sits on the hero's arm in the
                    // game's pose - its front is the side facing away from the hero's body, its up the in-plane axis nearest
                    // the world's up (the mesh guesses above picked the bounds' sides and the middle axis of a near-round
                    // shield). Falls back to the guesses when the model is already off the hero.
                    if (m_pc != null && t.IsChildOf(m_pc.transform))
                    {
                        Vector3 wc = t.TransformPoint(lb.center), chest = m_pc.transform.position + Vector3.up * 1.2f;
                        Vector3 outW = wc - chest;
                        outW.y = 0f;
                        Vector3 thinW = t.TransformDirection(h.Thin);
                        bool flip = outW.sqrMagnitude > 1e-4f && Vector3.Dot(thinW, outW) < 0f;
                        if (flip) h.Thin = -h.Thin;
                        Vector3 best = h.Up;
                        float bestD = -2f;
                        foreach (Vector3 c in new[] { h.Long, -h.Long, h.Mid, -h.Mid })
                        {
                            float d = Vector3.Dot(t.TransformDirection(c).normalized, Vector3.up);
                            if (d > bestD) { bestD = d; best = c; }
                        }
                        Plugin.L.LogInfo("CombatVR: shield from the game's pose: front " + Diag.V(h.Thin) + (flip ? " (flipped)" : "") + " (" +
                                         Vector3.Dot(t.TransformDirection(h.Thin), outW.normalized).ToString("0.00") + " away from the body), up " + Diag.V(best) +
                                         " (" + bestD.ToString("0.00") + " to the world's up; the mesh guess was " + Diag.V(h.Up) + ")");
                        h.Up = best;
                        ShieldFromPose++;
                    }
                    // P4-12 (playtest 4, mark 222059_9: "rotated like a clock about 90 degrees"): the models are drawn turned in their
                    // own plane - the up axis from the mesh's own mirror line (ShieldAxis), the guesses above only without a mesh
                    h.OldUp = h.Up;
                    string upHow;
                    Vector3? designUp = ShieldAxis.DesignUp(t, h.Thin, out upHow);
                    if (designUp.HasValue) h.Up = designUp.Value;
                    h.MeshUp = h.Up;
                    LastShieldUp = t.name + ": up " + Diag.V(h.Up) + " - " + upHow;
                    Plugin.L.LogInfo("CombatVR: shield up from the mesh: " + LastShieldUp + " (the old guess " + Diag.V(h.OldUp) + ", " +
                                     Vector3.Angle(h.OldUp, h.Up).ToString("0") + " deg from it)");
                    if (OldShieldUp) h.Up = h.OldUp;
                    Vector3? handle = VRHands.FindShieldGrip(t, h.Thin);
                    h.HasHandle = handle.HasValue;
                    if (handle.HasValue) h.Handle = handle.Value;
                    Plugin.L.LogInfo("CombatVR: shield up axis " + Diag.V(h.Up) + " (length/width " + aspect.ToString("0.00") + ", wide end " + upDot.ToString("0.000") + ")" +
                                     (h.HasHandle ? ", handle at " + Diag.V(h.Handle) : ", no handle found"));
                }
                Plugin.L.LogInfo("CombatVR: " + t.name + " local bounds center=" + Diag.V(lb.center) + " size=" + Diag.V(lb.size) +
                                 " long=" + Diag.V(h.Long) + " thin=" + Diag.V(h.Thin) + " world length=" + h.Length.ToString("0.00"));
                m_held[id] = h;
                // a held model must render normally even if HideBody saw it first
                foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true))
                {
                    ShadowCastingMode mode;
                    if (m_hidden.TryGetValue(r, out mode)) { r.shadowCastingMode = mode; m_hidden.Remove(r); }
                }
                Plugin.L.LogInfo("CombatVR: holding " + t.name + " in the " + (right ? "right" : "left") + " hand");
                WeaponGrips.FixMaterials(t);   // the game's own magenta placeholder materials (HugeHammer)
            }
            Vector3 lossy = t.lossyScale;
            t.SetParent(right ? HandR : HandL, false);
            t.localScale = lossy;   // hands are unscaled; keep the model's world size
            // N8 (the player's playtest 2026-10-17, mark 10: the Duelling Daggers "look like spears"): the game's models are
            // drawn for its big third-person hero (the arming sword 1.14 m, the daggers' mesh 0.77 m at world scale 1.0 - the
            // model's own size, not our scale or grip). In VR at the player's own size a few are scaled to their kind's size.
            float vs = shield ? 1f : VrScale(t.name);
            if (vs != 1f)
            {
                t.localScale = lossy * vs;
                if (!m_scaledNoted.Contains(id)) { m_scaledNoted.Add(id); Plugin.L.LogInfo("CombatVR: " + t.name + " held at x" + vs.ToString("0.00") + " of the game's size (" + (h.Length * vs).ToString("0.00") + " m)"); }
            }
            Place(h);
        }

        readonly HashSet<int> m_scaledNoted = new HashSet<int>();

        /// The VR size of a held weapon against the game's (N8): the Duelling Daggers' 0.77 m mesh x0.6 = 0.46 m, a long
        /// dagger's length (UYF's daggers: a short blade, magnetism 0.55 m).
        public static float VrScale(string name)
        {
            if (string.IsNullOrEmpty(name)) return 1f;
            if (name.StartsWith("DuellingDaggers", System.StringComparison.Ordinal)) return 0.6f;
            return 1f;
        }

        /// A held shield's front (model-local): the side facing away from the player, as drawn (F7: from the game's pose).
        /// Guard's block judge must use the same side - a player blocks where they see the shield. False = not a held shield.
        public static bool ShieldFront(Transform t, out Vector3 front)
        {
            front = Vector3.zero;
            Held h;
            if (I == null || t == null || !I.m_held.TryGetValue(t.GetInstanceID(), out h) || h.T != t || !h.Shield) return false;
            front = h.Thin;
            return true;
        }

        /// For the grip test: the authored local rotation (under the hero's node) of a model held now.
        public static bool Authored(Transform t, out Quaternion localRot, out bool shield)
        {
            localRot = Quaternion.identity;
            shield = false;
            Held h;
            if (I == null || t == null || !I.m_held.TryGetValue(t.GetInstanceID(), out h) || h.T != t) return false;
            localRot = h.LocalRot;
            shield = h.Shield;
            return true;
        }

        void Place(Held h)
        {
            if (h.T == null) return;
            Quaternion rot;
            Vector3 pos;
            Vector3 s = h.T.localScale;
            Vector3 fist = ControllerGeometry.GripPos(!h.Right);
            if (h.Shield)
            {
                // center grip: the face (thin axis) points where the knuckles point (the pointer
                // direction), the shield's up (its mesh's, ShieldAxis) along the controller's up; centre just past the fist.
                // P4-12: turned with the player's glove fit (the hand that holds it), as the glove is drawn
                Quaternion fit = ShieldFit(!h.Right);
                h.FitVersion = HandGeometry.Version;
                Vector3 face = fit * (Quaternion.Euler(ControllerGeometry.TipPitchDown - ShieldTilt, 0f, 0f) * Vector3.forward);
                Vector3 up = fit * (Quaternion.Euler(ControllerGeometry.TipPitchDown - ShieldTilt, 0f, 0f) * Vector3.up);
                rot = Quaternion.LookRotation(face, up) * Quaternion.Inverse(Quaternion.LookRotation(h.Thin, h.Up != Vector3.zero ? h.Up : h.Mid));
                // the fist round the handle; without one, the shield's middle just past the fist
                if (h.HasHandle) pos = fist + face * ShieldHandleForward + up * ShieldUp - rot * Vector3.Scale(h.Handle, s);
                else pos = fist + face * ShieldForward + up * ShieldUp - rot * Vector3.Scale(h.Center, s);
            }
            else if (CombatSettings.Grip == CombatSettings.GripMode.Game)
            {
                // the game's own hold (WeaponGrips): the edge or head toward the trigger side, as the game swings it
                WeaponGrips.Pose(h.T, h.LocalRot, s, !h.Right, out pos, out rot);
            }
            else
            {
                // the grip in the fist (the model origin is at the pommel end), the blade (long axis)
                // along the fist's axis
                Vector3 blade = Quaternion.Euler(-(ControllerGeometry.GripPitchUp + SwordTilt), 0f, 0f) * Vector3.forward;
                Vector3 up = Quaternion.Euler(-(ControllerGeometry.GripPitchUp + SwordTilt), 0f, 0f) * Vector3.up;
                rot = Quaternion.LookRotation(blade, up) * Quaternion.Inverse(Quaternion.LookRotation(h.Long, h.Thin));
                float grip = h.Grip * Vector3.Scale(h.Long, s).magnitude;   // model units -> hand units
                pos = fist + blade * (SwordForward - grip) + up * SwordUp;
            }
            h.T.localRotation = rot;
            h.T.localPosition = pos;
            h.BaseRot = rot;
            h.BasePos = pos;
            h.HasBase = true;
        }

        /// The glove fit's turn of the hand against the fit the mod ships with (Touch: [Hands] GloveAngle 20, turn 0, roll 0 - the
        /// fit the shield's hold was made for), in the controller's frame, about the fist; the left hand gets the mirror image, as
        /// its glove does (VRHands.AdaptToController, HandGeometry). P4-12: the user's fit (GloveAngle 25, GloveTurn -9.5, GloveRoll
        /// -23) turned their glove 23 deg about the handle while the shield stayed with the controller. Other controllers: none
        /// (the glove fit is Touch's).
        public const float DefaultGloveAngle = 20f;
        public static Quaternion ShieldFit(bool left)
        {
            if (ControllerGeometry.Type == "knuckles") return Quaternion.identity;
            Quaternion q = Quaternion.Euler(Settings.GloveAngle, Settings.GloveTurn, Settings.GloveRoll) * Quaternion.Inverse(Quaternion.Euler(DefaultGloveAngle, 0f, 0f));
            return left ? new Quaternion(q.x, -q.y, -q.z, q.w) : q;
        }

        /// P4-12's check (`shieldpose?`): the held shield as the player sees it - how far its up is turned from the world's up within
        /// its own face (a clock's turn; 0 = upright, + = clockwise as seen from behind it), the face's elevation and bearing, where the
        /// up came from.
        public static string ShieldPose()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (I == null) return "no CombatVR";
            foreach (Held h in I.m_held.Values)
            {
                if (!h.Shield || h.T == null) continue;
                // the clock of the shield's design up (its mesh's), whichever up the hold used
                Vector3 face = h.T.TransformDirection(h.Thin).normalized, up = h.T.TransformDirection(h.MeshUp).normalized;
                Vector3 wu = Vector3.up - face * Vector3.Dot(Vector3.up, face);
                float clock = wu.sqrMagnitude > 1e-4f ? Vector3.SignedAngle(wu.normalized, up, -face) : 0f;
                float el = Mathf.Asin(Mathf.Clamp(face.y, -1f, 1f)) * Mathf.Rad2Deg;
                Vector3 hf = VRRig.I != null ? VRRig.I.HeadWorldRot * Vector3.forward : Vector3.forward;
                float bearing = Vector3.SignedAngle(new Vector3(hf.x, 0f, hf.z), new Vector3(face.x, 0f, face.z), Vector3.up);
                Quaternion fit = ShieldFit(!h.Right);
                return string.Format(inv, "shield {0} hand {1} clock {2:0.0} faceEl {3:0.0} faceVsHead {4:0.0} up {5} face {6} meshUp {7} oldUp {8} source {9} fit {10:0.0} ctrl {11} glove {12} | {13}",
                    h.T.name, h.Right ? "r" : "l", clock, el, bearing, Diag.V(up), Diag.V(face), Diag.V(h.MeshUp), Diag.V(h.OldUp), OldShieldUp ? "old" : "mesh",
                    Quaternion.Angle(Quaternion.identity, fit), ControllerGeometry.Type, Settings.GloveFitText(), LastShieldUp);
            }
            return "no shield held";
        }

        /// `shieldout l|r [zero]`: the xrsim command that holds the shield hand straight out - the fitted hand's pointer level toward
        /// the head's facing, its thumb side up (the hold's own face/up directions, ShieldFit included). `zero`, sent while xrsim
        /// holds that controller at `rot 0 0 0`: the runtime's own turn of the controller frame against xrsim's pose (the test
        /// headset's Index frame sits 15.4 deg pitched), taken out of later answers.
        static readonly Quaternion[] s_rawOffset = { Quaternion.identity, Quaternion.identity };
        public static string ShieldOut(bool left, bool zero = false)
        {
            Hand hd = left ? VRInput.L : VRInput.R;
            if (zero) { s_rawOffset[left ? 0 : 1] = hd.Rot; return "zero " + Diag.V(hd.Rot.eulerAngles); }
            Vector3 fwd = VRInput.HeadRot * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            Quaternion fit = ShieldFit(left), tip = Quaternion.Euler(ControllerGeometry.TipPitchDown - ShieldTilt, 0f, 0f);
            Vector3 pc = fit * (tip * Vector3.forward), tc = fit * (tip * Vector3.up);
            Quaternion r = Quaternion.LookRotation(fwd.normalized, Vector3.up) * Quaternion.Inverse(Quaternion.LookRotation(pc, tc));
            r = r * Quaternion.Inverse(s_rawOffset[left ? 0 : 1]);
            Vector3 e = new Quaternion(-r.x, -r.y, r.z, r.w).eulerAngles;   // Unity tracking -> OpenXR stage (z flipped)
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "rot {0} {1:0.##} {2:0.##} {3:0.##}", left ? "l" : "r", e.y, e.x, e.z);
        }

        /// Playtest 3 (2026-10-18, mark 29: "The shield's in the completely wrong orientation", held flat like a tray, its handle
        /// side up, the hand low and forward): the face follows the fist's pointing, so a wrist pointing down laid the shield
        /// flat. Each frame (after the hands moved) a held shield's face is kept between ShieldMaxDown below level and ShieldMaxUp
        /// above it - the rest of the hold as before, turned about the fist - so it is never a tray or a lid, whatever the
        /// wrist does, with any shield, every fight.
        public static float ShieldMaxDown = 25f, ShieldMaxUp = 55f;
        public static int ShieldClamps;
        public static float ShieldFaceElevation;   // the held shield's face above level (deg; after the clamp)

        void ShieldUpright()
        {
            foreach (Held h in m_held.Values)
            {
                if (!h.Shield || !h.HasBase || h.T == null) continue;
                Transform hand = h.Right ? HandR : HandL;
                if (hand == null || h.T.parent != hand) continue;
                if (h.FitVersion != HandGeometry.Version) Place(h);   // the glove fit changed (P4-12: the shield follows it)
                Quaternion baseW = hand.rotation * h.BaseRot;
                Vector3 face = (baseW * h.Thin).normalized;
                float el = Mathf.Asin(Mathf.Clamp(face.y, -1f, 1f)) * Mathf.Rad2Deg;
                float want = Mathf.Clamp(el, -ShieldMaxDown, ShieldMaxUp);
                if (Mathf.Abs(want - el) < 0.01f)
                {
                    h.T.localRotation = h.BaseRot;
                    h.T.localPosition = h.BasePos;
                    ShieldFaceElevation = el;
                    continue;
                }
                Vector3 flat = new Vector3(face.x, 0f, face.z);
                if (flat.sqrMagnitude < 1e-4f) { flat = hand.forward; flat.y = 0f; }
                if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
                flat.Normalize();
                Vector3 desired = flat * Mathf.Cos(want * Mathf.Deg2Rad) + Vector3.up * Mathf.Sin(want * Mathf.Deg2Rad);
                Quaternion fix = Quaternion.FromToRotation(face, desired);
                Vector3 fist = hand.TransformPoint(ControllerGeometry.GripPos(!h.Right));
                Vector3 pw = hand.TransformPoint(h.BasePos);
                h.T.rotation = fix * baseW;
                h.T.position = fist + fix * (pw - fist);
                ShieldClamps++;
                ShieldFaceElevation = want;
            }
        }
    }
}

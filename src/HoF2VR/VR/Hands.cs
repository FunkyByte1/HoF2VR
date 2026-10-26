using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using Valve.VR;

namespace HoF2VR
{
    /// Gloved hands at the controllers, everywhere (title, card table, fights). Each hand is one
    /// procedural mesh (HandGeometry) drawn by the game's own camera on a layer that camera renders,
    /// so the scene lights it and hides it behind things. The fingers follow the player's own
    /// (FingerCurl): middle/ring/pinky the analog grip, the index the trigger and its capacitive touch
    /// (lifted off it = pointing), the thumb the stick and face-button touches (off them = hovering).
    /// In fights a hand holding the sword or shield closes into a fist round it,
    /// and the hand mesh hangs under CombatVR's own hand transform, so it moves exactly with the
    /// weapon. The back of the non-weapon glove shows the hero's health as a row of glowing gems.
    internal class VRHands : MonoBehaviour
    {
        public static VRHands I;
        public static bool On = true;

        // Glove colours (sRGB): leather, cuff, trim (rolled edge and wrist strap), cuff lining.
        // (tuned in the game: the player's spots are dimly lit, darker leather read as black shapes)
        public static Color Leather = new Color(0.48f, 0.33f, 0.21f);
        public static Color CuffLeather = new Color(0.34f, 0.22f, 0.14f);
        public static Color Trim = new Color(0.62f, 0.48f, 0.30f);
        public static Color Lining = new Color(0.2f, 0.135f, 0.09f);

        /// Shader preference: the first one this build can find is used (logged). "Standard" is not
        /// in Hand of Fate 2's build (its ScriptMapper entry is null) but would be preferred if a
        /// future build or asset bundle provided it.
        static readonly string[] s_shaders = { "Standard", "Legacy Shaders/Diffuse", "Legacy Shaders/VertexLit", "Unlit/Color", "Sprites/Default", "Hidden/Internal-Colored" };
        // Layers never used for the hands (Hand of Fate 2's TagManager): UI, GUI, 3DUI, 2DUI,
        // 3DUIGame, Paperdoll, Flare (= VRUI.Layer, our overlay), 3DUIDealer, OffscreenParticles,
        // 3DUILightCull, 3DUITransparency, and Water / DynamicBoneCollider.
        const int ExcludedLayers = (1 << 4) | (1 << 5) | (1 << 14) | (1 << 16) | (1 << 17) | (1 << 21) | (1 << 22) | (1 << 23) |
                                   (1 << 26) | (1 << 28) | (1 << 29) | (1 << 30) | (1 << 31);
        static readonly int[] s_preferredLayers = { 0, 13, 8, 11, 1 };   // Default, Player, Actor, Neutral, TransparentFX
        // The card-table camera ('Card Table 3DUI Camera', mask 3DUI|3DUIDealer|3DUITransparency) draws the
        // whole caravan room on its 3D UI layer: the hands go there when no world layer is drawn.
        static readonly int[] s_tableLayers = { 16 };

        sealed class Side
        {
            public readonly bool Left;
            public HandGeometry Geo;
            public Transform T;
            public Mesh Mesh;
            public MeshRenderer R, DepthR, GhostR;
            public Mesh GhostMesh;
            public Material GhostMat;
            public float GhostShown;
            public HandPose Pose, Built;
            public bool HasPose, HasBuilt, Holding, Pointing, Shown;
            public int GeoVersion = -1;
            public Vector3 TipLocal;
            public int TipVersion = -1;
            public Transform HeldModel;   // first held model under the combat mount (layer source)
            public int HeldLayer = -1;
            public float NextHeldScan;
            public readonly FingerCurl Fingers;
            public Side(bool left) { Left = left; Fingers = new FingerCurl(left); }
        }

        readonly Side m_l = new Side(true), m_r = new Side(false);
        Material m_mat;
        Texture2D m_tex;
        string m_shaderName = "";
        bool m_failed;

        // forced states for tests and screenshots (`hands pose ...`, `hands layer ...`)
        static string s_force = "";
        static int s_forceLayer = -1;

        // layer from the primary camera's culling mask (re-picked when camera or mask change)
        Camera m_lastCam;
        int m_lastMask = -1, m_camLayer;

        // VRPointer's private state (which hand drives the laser, and whether it does right now)
        Func<bool> m_pointerDriving;
        AccessTools.FieldRef<Hand> m_pointerHand;
        Hand m_lastTriggerHand;
        int m_frame = -1;   // last frame the poses were stepped

        void Awake()
        {
            I = this;
            try
            {
                MethodInfo getter = AccessTools.PropertyGetter(typeof(VRPointer), "Driving");
                if (getter != null) m_pointerDriving = AccessTools.MethodDelegate<Func<bool>>(getter);
                FieldInfo hand = AccessTools.Field(typeof(VRPointer), "s_hand");
                if (hand != null) m_pointerHand = AccessTools.StaticFieldRefAccess<Hand>(hand);
            }
            catch (Exception e) { Plugin.L.LogWarning("VRHands: pointer state not readable (" + e.Message + ") - using the last trigger hand"); }
            Diag.Commands["hands"] = Command;
            Diag.Commands["fingers"] = a => FingerCurl.Command(a, m_l.Fingers, m_r.Fingers);
            Diag.Commands["handicons"] = a => HandIcons.Describe();
            Diag.StatusExtras["hands"] = () => Diag.Q(Describe());
            Health.Awake();
        }

        void OnDestroy() { if (I == this) I = null; }

        /// World position of a hand's index fingertip while that hand is shown (for VRPointer, if the
        /// laser should leave the finger rather than the controller's aim point).
        /// The pointing fingertip in the controller frame (see HandGeometry.PointingTip), cached per geometry.
        public static bool TryGetPointingTipLocal(bool left, out Vector3 local)
        {
            local = Vector3.zero;
            Side s = I == null ? null : left ? I.m_l : I.m_r;
            if (s == null || s.Geo == null) return false;
            if (s.TipVersion != HandGeometry.Version) { s.TipLocal = s.Geo.PointingTip(); s.TipVersion = HandGeometry.Version; }
            local = s.TipLocal;
            return true;
        }

        /// The glove's live mesh (the waiting space draws copies of the hands with it).
        public static Mesh GloveMesh(bool left)
        {
            Side s = I == null ? null : left ? I.m_l : I.m_r;
            return s != null && s.Shown ? s.Mesh : null;
        }

        /// The glove's root transform while it is shown (null otherwise).
        public static Transform GloveRoot(bool left)
        {
            Side s = I == null ? null : left ? I.m_l : I.m_r;
            return s != null && s.Shown ? s.T : null;
        }

        /// The glove's lowest point (world) at the controller's pose `pos`/`rot` (tracking space) with its current finger
        /// pose: what touches a table top first (every 3rd vertex of the posed mesh; the mesh is in the glove's own frame,
        /// scaled with it). Only for a glove at the controller (not one held under a weapon in a fight).
        public static bool LowestPoint(bool left, Vector3 pos, Quaternion rot, out Vector3 low)
        {
            low = Vector3.zero;
            Side s = I == null ? null : left ? I.m_l : I.m_r;
            VRRig rig = VRRig.I;
            if (s == null || rig == null || !s.Shown || !s.HasBuilt || s.Geo == null || s.Geo.Verts == null || CombatVR.Active) return false;
            Vector3[] v = s.Geo.Verts;
            Quaternion r = rig.ToWorld(rot);
            Vector3 up = Quaternion.Inverse(r) * Vector3.up;   // world up in the glove's frame
            float best = float.MaxValue;
            int bi = -1;
            for (int i = 0; i < v.Length; i += 3)
            {
                float y = v[i].x * up.x + v[i].y * up.y + v[i].z * up.z;
                if (y < best) { best = y; bi = i; }
            }
            if (bi < 0) return false;
            low = rig.ToWorld(pos) + r * (v[bi] * rig.Scale);
            return true;
        }

        /// The gloves' materials (leather + the ambient floor pass).
        public static Material[] GloveMaterials()
        {
            if (I == null || I.m_mat == null) return null;
            return I.m_floorMat != null && Floor > 0f ? new[] { I.m_mat, I.m_floorMat } : new[] { I.m_mat };
        }

        public static bool TryGetIndexTip(bool left, out Vector3 world)
        {
            world = Vector3.zero;
            Side s = I == null ? null : left ? I.m_l : I.m_r;
            if (s == null || !s.Shown || s.T == null) return false;
            world = s.T.TransformPoint(s.Geo.IndexTip);
            return true;
        }

        /// For CombatVR: how far up `longAxis` (model space, unit, from the hilt end to the tip) the
        /// middle of the model's grip is, so the fist can close round it instead of the pommel.
        /// Model-space units; 0 when the meshes are unreadable or the shape does not tell.
        public static float FindGrip(Transform model, Vector3 longAxis)
        {
            if (model == null) return 0f;
            var pts = new List<Vector3>();
            var tris = new List<int>();
            Matrix4x4 toModel = model.worldToLocalMatrix;
            foreach (MeshFilter mf in model.GetComponentsInChildren<MeshFilter>(false))   // what is shown
            {
                Mesh m = mf.sharedMesh;
                if (m == null || !m.isReadable) continue;
                Matrix4x4 k = toModel * mf.transform.localToWorldMatrix;
                int baseIdx = pts.Count;
                foreach (Vector3 v in m.vertices) pts.Add(k.MultiplyPoint3x4(v));
                foreach (int i in m.triangles) tris.Add(baseIdx + i);
            }
            float g = HandGeometry.GripAlong(pts, tris, longAxis);
            if (float.IsNaN(g)) return 0f;
            Plugin.L.LogInfo("VRHands: grip of " + model.name + " is " + g.ToString("0.000") + " up its long axis (" + pts.Count + " vertices)");
            return g;
        }

        /// Average of the shown mesh vertices in model space (where a shape's mass is).
        public static Vector3 MeshCentroid(Transform model)
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            Matrix4x4 toModel = model.worldToLocalMatrix;
            foreach (MeshFilter mf in model.GetComponentsInChildren<MeshFilter>(false))
            {
                Mesh m = mf.sharedMesh;
                if (m == null || !m.isReadable) continue;
                Matrix4x4 k = toModel * mf.transform.localToWorldMatrix;
                foreach (Vector3 v in m.vertices) { sum += k.MultiplyPoint3x4(v); n++; }
            }
            return n > 0 ? sum / n : Vector3.zero;
        }

        /// For CombatVR: the middle of a shield's handle in model space - the rearmost part (opposite
        /// `face`, model space, unit) in the middle third of the shield. Null when the meshes are
        /// unreadable or the shape does not tell.
        public static Vector3? FindShieldGrip(Transform model, Vector3 face)
        {
            if (model == null) return null;
            var pts = new List<Vector3>();
            Matrix4x4 toModel = model.worldToLocalMatrix;
            foreach (MeshFilter mf in model.GetComponentsInChildren<MeshFilter>(false))
            {
                Mesh m = mf.sharedMesh;
                if (m == null || !m.isReadable) continue;
                Matrix4x4 k = toModel * mf.transform.localToWorldMatrix;
                foreach (Vector3 v in m.vertices) pts.Add(k.MultiplyPoint3x4(v));
            }
            if (pts.Count < 50) return null;
            face = face.normalized;
            var b = new Bounds(pts[0], Vector3.zero);
            foreach (Vector3 p in pts) b.Encapsulate(p);
            Vector3 c = b.center;
            float across = 0f;
            foreach (Vector3 p in pts) { Vector3 d = p - c; d -= face * Vector3.Dot(d, face); across = Mathf.Max(across, d.magnitude); }
            float reach = across * 0.33f;
            var depth = new List<float>();
            foreach (Vector3 p in pts) { Vector3 d = p - c; d -= face * Vector3.Dot(d, face); if (d.magnitude < reach) depth.Add(Vector3.Dot(p, face)); }
            if (depth.Count < 20) return null;
            depth.Sort();
            float cut = depth[Mathf.Max(0, depth.Count * 6 / 100)];
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (Vector3 p in pts)
            {
                Vector3 d = p - c;
                d -= face * Vector3.Dot(d, face);
                if (d.magnitude < reach && Vector3.Dot(p, face) <= cut) { sum += p; n++; }
            }
            if (n == 0) return null;
            // the rearmost points are the back of the handle: its axis is a little further in
            Vector3 grip = sum / n + face * 0.012f;
            Plugin.L.LogInfo("VRHands: handle of " + model.name + " at " + Diag.V(grip) + " (model space; " + n + " of " + pts.Count + " vertices, shield " +
                             (2f * across).ToString("0.00") + " across, depth " + (depth[depth.Count - 1] - depth[0]).ToString("0.00") + ")");
            return grip;
        }

        void LateUpdate() { UpdateHands(); }

        /// Poses and places both hands for this frame. Called from LateUpdate; safe to call again
        /// later in the frame (e.g. from Application.onBeforeRender with a fresher controller pose).
        public void UpdateHands()
        {
            if (m_failed) return;
            try { Tick(); }
            catch (Exception e)
            {
                m_failed = true;
                Plugin.L.LogError("VRHands disabled after an error: " + e);
                Show(m_l, false);
                Show(m_r, false);
            }
        }

        void Tick()
        {
            VRRig rig = VRRig.I;
            if (!On || !XRSettings.enabled || rig == null)
            {
                Show(m_l, false);
                Show(m_r, false);
                Health.Hide();
                return;
            }
            if (m_mat == null) CreateMaterial();
            if (m_mat == null) return;
            UpgradeShader();
            AdaptToController();
            VRInput.Update();
            // a second call in the same frame (e.g. from onBeforeRender) only re-places the hands
            bool step = m_frame != Time.frameCount;
            m_frame = Time.frameCount;
            if (step)
            {
                if (VRInput.L.TriggerDown) m_lastTriggerHand = VRInput.L;
                else if (VRInput.R.TriggerDown) m_lastTriggerHand = VRInput.R;
                UpdateCameraLayer(rig.PrimaryCamera);
            }
            float dt = step ? Mathf.Min(Time.unscaledDeltaTime, 0.1f) : 0f;
            Step(m_l, VRInput.L, rig, dt);
            Step(m_r, VRInput.R, rig, dt);
            Side hs = Settings.LeftHanded ? m_r : m_l;
            Health.Update(hs.Shown ? hs.T : null, hs.Geo, hs.R != null ? hs.R.gameObject.layer : 0, rig, dt);
            // P4-10: what each stick click does, on its glove (the special, the artefact)
            HandIcons.Tick(m_l.Shown ? m_l.T : null, m_l.Geo, true, m_l.R != null ? m_l.R.gameObject.layer : 0, rig);
            HandIcons.Tick(m_r.Shown ? m_r.T : null, m_r.Geo, false, m_r.R != null ? m_r.R.gameObject.layer : 0, rig);
            if (step) { AdjustFit(dt); UpdateFloor(); }
        }

        // ---- the glove fit, adjusted in VR (docs/hand-feel-design.md §3.2, the quick version) ----------------------------
        // Both grips held + the right stick: up/down tilts the gloves (Hands.GloveAngle), left/right moves them along the
        // handle (Hands.GloveAlong); a note shows the values; letting go of the grips saves them. Only while the hand check is
        // open (its page is where the gloves' fit belongs), and only once both grips have been held FitDwell first: at the
        // table the grips page and the right stick moves through menus - two fists and a nudge of the stick re-tilted the
        // gloves there and saved it (the bug hunt of 2026-10-18, finding 10).
        bool m_fitting;
        float m_fitNote, m_bothSince = -1f;
        public static int FitSaves;
        public static float FitDwell = 0.6f;

        void AdjustFit(float dt)
        {
            bool both = VRInput.L.Tracked && VRInput.R.Tracked && VRInput.L.GripBtn && VRInput.R.GripBtn && !CombatVR.Active && DiceHand.Holder == null &&
                        HandCheck.I != null && HandCheck.I.Active;
            Vector2 st = VRInput.R.Stick;
            bool push = Mathf.Abs(st.y) > 0.5f || Mathf.Abs(st.x) > 0.5f;
            // (the dwell: both grips held with the stick at rest first - grips squeezed while the stick is already pushed don't count)
            if (!both) m_bothSince = -1f;
            else if (m_bothSince < 0f && !push) m_bothSince = Time.unscaledTime;
            bool dwelt = m_bothSince >= 0f && Time.unscaledTime - m_bothSince >= FitDwell;
            if (both && push && (dwelt || m_fitting))
            {
                if (!m_fitting) { m_fitting = true; Settings.LiveGloveAngle = Settings.GloveAngle; Settings.LiveGloveAlong = Settings.GloveAlong; }
                if (Mathf.Abs(st.y) > 0.5f) Settings.LiveGloveAngle = Mathf.Clamp(Settings.LiveGloveAngle - Mathf.Sign(st.y) * 12f * dt, -45f, 45f);
                if (Mathf.Abs(st.x) > 0.5f) Settings.LiveGloveAlong = Mathf.Clamp(Settings.LiveGloveAlong + Mathf.Sign(st.x) * 1.5f * dt, -8f, 8f);
                if (Time.unscaledTime >= m_fitNote && VRHelp.I != null)
                {
                    m_fitNote = Time.unscaledTime + 0.2f;
                    // (placed once when it appears, then only its text: it no longer follows the head)
                    VRHelp.I.ShowInPlace("Glove angle " + Settings.GloveAngle.ToString("0") + "\u00b0, along the handle " + Settings.GloveAlong.ToString("0.0") +
                                    " cm\nStick up/down tilts, left/right moves. Let go of the grips to keep it.", 1.5f);
                }
            }
            else if (m_fitting && !both)
            {
                m_fitting = false;
                Plugin.L.LogInfo("VRHands: glove fit adjusted in VR: angle " + Settings.GloveAngle.ToString("0.0") + " deg, along " + Settings.GloveAlong.ToString("0.00") + " cm (saved)");
                Settings.SaveGloveFit();
                FitSaves++;
                if (VRHelp.I != null) VRHelp.I.Show("Glove fit saved: " + Settings.GloveAngle.ToString("0") + "\u00b0, " + Settings.GloveAlong.ToString("0.0") + " cm", 2f);
            }
        }

        // ---- per hand ---------------------------------------------------------------------------

        void Step(Side s, Hand h, VRRig rig, float dt)
        {
            if (!h.Tracked) { Show(s, false); return; }
            if (s.T == null) Create(s);

            // where: in fights under CombatVR's hand (the held weapon's parent), else at the controller
            Transform mount = CombatVR.Active && CombatVR.I != null ? (s.Left ? CombatVR.I.HandL : CombatVR.I.HandR) : null;
            if (mount != null)
            {
                if (s.T.parent != mount) s.T.SetParent(mount, false);
                s.T.localPosition = Vector3.zero;
                s.T.localRotation = Quaternion.identity;
                // the weapon hand rides its weapon's grip (UYF: the hand is on the weapon): WeaponFeel's drawn pose - the lag, a
                // block's knock, the two-handed turn - as an offset from the controller (the mount)
                Vector3 wp;
                Quaternion wr;
                if (WeaponFeel.Pose(s.Left, out wp, out wr))
                {
                    Quaternion inv = Quaternion.Inverse(h.Rot);
                    s.T.localPosition = inv * (wp - h.Pos);
                    s.T.localRotation = inv * wr;
                }
            }
            else
            {
                if (s.T.parent != transform) s.T.SetParent(transform, false);
                // the drawn hand: the controller + HandFeel's offset (smoothing, the pull toward a button, the stop on a
                // surface); a ghost glove shows the real hand while the two are apart
                s.T.position = rig.ToWorld(h.Pos) + HandFeel.DrawnOffset(s.Left);
                s.T.rotation = rig.ToWorld(h.Rot);
            }
            float sc = rig.Scale;   // life size in the larger-than-life caravan too
            s.T.localScale = new Vector3(sc, sc, sc);

            s.Holding = mount != null && FindHeld(s, mount);
            // the free hand on a two-hander's haft: a fist round it (WeaponFeel's two-handed grip)
            if (WeaponFeel.TwoHanded && s.Left != Settings.LeftHanded) s.Holding = true;
            s.Pointing = !s.Holding && PointerHand() == h;

            // the fingers: the player's own (FingerCurl), smoothed; fist / dice / touch-point overrides
            FingerCurl.Context ctx;
            ctx.Forced = s_force;
            ctx.Holding = s.Holding;
            ctx.DiceHeld = DiceHand.Holder == h;
            ctx.LaserHand = s.Pointing;
            ctx.PointWeight = HandFeel.PointWeight(h);
            ctx.WantsPoint = ctx.PointWeight > 0.001f;
            ctx.GrabReady = Mathf.Max(Mathf.Max(DiceHand.Ready(h), CardGrab.Ready(h)), TableCardGrab.Ready(h));
            ctx.Pinch = TableCardGrab.Holds(h);
            FingerSignals sig = FingerSampler.Get(h.IsLeft);
            s.Pose = s.Fingers.Step(ref sig, ctx, dt);
            s.HasPose = true;
            if (!s.HasBuilt || s.GeoVersion != HandGeometry.Version || HandPose.MaxDelta(s.Pose, s.Built) > 0.004f)
            {
                s.Geo.Pose(s.Pose);
                s.Mesh.vertices = s.Geo.Verts;
                s.Mesh.normals = s.Geo.Normals;
                s.Mesh.bounds = s.Geo.Bounds;
                s.Built = s.Pose;
                s.HasBuilt = true;
                s.GeoVersion = HandGeometry.Version;
            }

            int layer = s_forceLayer >= 0 ? s_forceLayer : (s.Holding && s.HeldLayer >= 0 ? s.HeldLayer : m_camLayer);
            if (s.T.gameObject.layer != layer) s.T.gameObject.layer = layer;
            Show(s, true);
            Ghost(s, h, rig, mount == null ? HandFeel.GhostAlpha(s.Left) : 0f);
        }

        // ---- the ghost glove (docs/hand-feel-design.md §3.3) ----------------------------------------------------------------
        // The same glove at the REAL controller pose, unlit warm white, see-through, on the overlay (drawn over the button or
        // card the real hand went into), while the drawn glove is held back more than 2 cm (HandFeel.GhostAlpha).
        static Shader s_ghostShader;
        public static int GhostFrames;

        void Ghost(Side s, Hand h, VRRig rig, float alpha)
        {
            if (alpha <= 0.002f)
            {
                if (s.GhostR != null && s.GhostR.enabled) s.GhostR.enabled = false;
                s.GhostShown = 0f;
                return;
            }
            if (s.GhostR == null)
            {
                if (s_ghostShader == null) s_ghostShader = Shader.Find("Hidden/Internal-Colored");
                if (s_ghostShader == null) return;
                var go = new GameObject(s.T.name + "_Ghost") { layer = VRUI.Layer };
                go.transform.SetParent(transform, false);
                s.GhostMesh = new Mesh { name = go.name };
                s.GhostMesh.MarkDynamic();
                s.GhostMesh.vertices = s.Geo.Verts;
                s.GhostMesh.normals = s.Geo.Normals;
                var white = new Color32[s.Geo.Verts.Length];
                for (int i = 0; i < white.Length; i++) white[i] = new Color32(255, 255, 255, 255);
                s.GhostMesh.colors32 = white;
                s.GhostMesh.triangles = s.Geo.Tris;
                go.AddComponent<MeshFilter>().sharedMesh = s.GhostMesh;
                s.GhostMat = new Material(s_ghostShader) { name = "HoF2VR_GhostGlove", renderQueue = 5110 };
                s.GhostMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                s.GhostMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                s.GhostMat.SetInt("_ZWrite", 0);
                s.GhostMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
                s.GhostMat.SetInt("_Cull", (int)CullMode.Back);
                s.GhostR = go.AddComponent<MeshRenderer>();
                s.GhostR.sharedMaterial = s.GhostMat;
                s.GhostR.shadowCastingMode = ShadowCastingMode.Off;
                s.GhostR.receiveShadows = false;
                s.GhostR.lightProbeUsage = LightProbeUsage.Off;
                s.GhostR.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
            Transform g = s.GhostR.transform;
            g.position = rig.ToWorld(h.Pos);
            g.rotation = rig.ToWorld(h.Rot);
            g.localScale = s.T.localScale;
            s.GhostMesh.vertices = s.Geo.Verts;
            s.GhostMesh.bounds = s.Geo.Bounds;
            s.GhostMat.color = new Color(1f, 0.93f, 0.8f, alpha);
            if (!s.GhostR.enabled) s.GhostR.enabled = true;
            s.GhostShown = alpha;
            GhostFrames++;
        }

        Hand PointerHand()
        {
            bool driving;
            Hand hand;
            if (m_pointerDriving != null && m_pointerHand != null)
            {
                driving = m_pointerDriving();
                hand = m_pointerHand();
            }
            else
            {
                hand = m_lastTriggerHand ?? VRInput.R;
                driving = VRPointer.Active && hand.Tracked && !GameWatch.InCombatPlay;
            }
            return driving ? hand : null;
        }

        /// Is something (the weapon or shield CombatVR moved there) held under the mount? Also keeps
        /// the held model's layer for the hand, checked twice a second.
        static bool FindHeld(Side s, Transform mount)
        {
            Transform first = null;
            for (int i = 0; i < mount.childCount; i++)
            {
                Transform c = mount.GetChild(i);
                if (c == s.T || !c.gameObject.activeSelf) continue;
                first = c;
                break;
            }
            if (first == null) { s.HeldModel = null; s.HeldLayer = -1; return false; }
            if (first != s.HeldModel || Time.unscaledTime >= s.NextHeldScan)
            {
                s.HeldModel = first;
                s.NextHeldScan = Time.unscaledTime + 0.5f;
                Renderer r = first.GetComponentInChildren<MeshRenderer>();
                if (r == null) r = first.GetComponentInChildren<SkinnedMeshRenderer>();
                int l = r != null ? r.gameObject.layer : first.gameObject.layer;
                s.HeldLayer = ((ExcludedLayers >> l) & 1) != 0 ? -1 : l;
            }
            return true;
        }

        void UpdateCameraLayer(Camera cam)
        {
            int mask = cam != null ? cam.cullingMask : 0;
            if (cam == m_lastCam && mask == m_lastMask) return;
            m_lastCam = cam;
            m_lastMask = mask;
            int pick = -1;
            foreach (int l in s_preferredLayers)
                if (((mask >> l) & 1) != 0) { pick = l; break; }
            foreach (int l in s_tableLayers)
                if (pick < 0 && ((mask >> l) & 1) != 0) pick = l;
            for (int l = 0; pick < 0 && l < 32; l++)
                if (((mask >> l) & 1) != 0 && ((ExcludedLayers >> l) & 1) == 0) pick = l;
            if (pick < 0) pick = 0;
            if (pick != m_camLayer || cam != null)
                Plugin.L.LogInfo("VRHands: layer " + pick + " (" + LayerMask.LayerToName(pick) + ") for camera '" + (cam != null ? cam.name : "-") +
                                 "' mask 0x" + mask.ToString("X"));
            m_camLayer = pick;
        }

        void Show(Side s, bool on)
        {
            if (s.R == null) { s.Shown = false; return; }
            if (s.R.enabled != on) s.R.enabled = on;
            if (s.DepthR != null && s.DepthR.enabled != (on && DepthPass)) s.DepthR.enabled = on && DepthPass;
            if (!on && s.GhostR != null && s.GhostR.enabled) s.GhostR.enabled = false;
            s.Shown = on;
        }

        /// The gloves hide the overlay's panel and plates where they are in front of them (`hands depth 0|1`).
        public static bool DepthPass = true;
        static Material s_depthMat;

        /// Hidden/Internal-Colored with its colour blended away (Zero, One): writes depth, leaves the image.
        static Material DepthMat()
        {
            if (s_depthMat != null) return s_depthMat;
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) return null;
            s_depthMat = new Material(sh) { name = "HoF2VR_GloveDepth", renderQueue = 4999 };
            s_depthMat.SetInt("_SrcBlend", (int)BlendMode.Zero);
            s_depthMat.SetInt("_DstBlend", (int)BlendMode.One);
            s_depthMat.SetInt("_ZWrite", 1);
            s_depthMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            s_depthMat.SetInt("_Cull", (int)CullMode.Back);
            return s_depthMat;
        }

        void Create(Side s)
        {
            s.Geo = new HandGeometry(s.Left);
            var go = new GameObject(s.Left ? "HoF2VR_GloveL" : "HoF2VR_GloveR");
            // under the mod's root object (DontDestroyOnLoad): survives scene loads, no extra DDOL
            go.transform.SetParent(transform, false);
            s.Mesh = new Mesh { name = go.name };
            s.Mesh.MarkDynamic();
            s.Geo.Pose(new HandPose(0.1f, 0.1f, 0.1f, 0.1f, 0f, 1f));
            s.Mesh.vertices = s.Geo.Verts;
            s.Mesh.normals = s.Geo.Normals;
            s.Mesh.uv = s.Geo.Uv;
            s.Mesh.colors32 = RegionColours(s.Geo);
            s.Mesh.triangles = s.Geo.Tris;
            s.Mesh.bounds = s.Geo.Bounds;
            go.AddComponent<MeshFilter>().sharedMesh = s.Mesh;
            s.R = go.AddComponent<MeshRenderer>();
            s.R.sharedMaterial = m_mat;
            if (Floor > 0f) ApplyFloor();
            s.R.shadowCastingMode = ShadowCastingMode.Off;   // never a hand shadow over the face
            s.R.receiveShadows = true;
            s.R.lightProbeUsage = LightProbeUsage.BlendProbes;
            s.R.reflectionProbeUsage = m_shaderName == "Standard" ? ReflectionProbeUsage.BlendProbes : ReflectionProbeUsage.Off;
            s.R.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            s.R.allowOcclusionWhenDynamic = false;
            s.R.enabled = false;
            s.T = go.transform;
            s.HasBuilt = false;
            // the same mesh, depth only, on the overlay layer: the overlay camera draws the panel and the choice plates
            // after the scene with a fresh depth buffer, so a plate used to cover a finger that was in front of it (the
            // fingertip seemed to sink in before it touched; docs/hand-feel-design.md §2.3)
            if (DepthMat() != null)
            {
                var dgo = new GameObject(go.name + "_Depth") { layer = VRUI.Layer };
                dgo.transform.SetParent(go.transform, false);
                dgo.AddComponent<MeshFilter>().sharedMesh = s.Mesh;
                s.DepthR = dgo.AddComponent<MeshRenderer>();
                s.DepthR.sharedMaterial = s_depthMat;
                s.DepthR.shadowCastingMode = ShadowCastingMode.Off;
                s.DepthR.receiveShadows = false;
                s.DepthR.lightProbeUsage = LightProbeUsage.Off;
                s.DepthR.reflectionProbeUsage = ReflectionProbeUsage.Off;
                s.DepthR.enabled = false;
            }
            Plugin.L.LogInfo("VRHands: " + go.name + " built (" + s.Geo.Verts.Length + " vertices, " + s.Geo.Tris.Length / 3 + " triangles)");
        }

        void CreateMaterial()
        {
            Shader sh = null;
            foreach (string n in s_shaders)
            {
                Shader f = Shader.Find(n);
                if (f != null && f.isSupported) { sh = f; m_shaderName = n; break; }
            }
            if (sh == null) { m_failed = true; Plugin.L.LogError("VRHands: no usable shader - hands disabled"); return; }
            m_tex = new Texture2D(4, 1, TextureFormat.RGBA32, false) { name = "HoF2VR_GloveColours", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            ApplyColours();
            m_mat = new Material(sh) { name = "HoF2VR_Glove", mainTexture = m_tex, color = Color.white };
            if (m_shaderName == "Unlit/Color" || m_shaderName == "Hidden/Internal-Colored") m_mat.color = Leather;
            if (m_mat.HasProperty("_Glossiness")) m_mat.SetFloat("_Glossiness", 0.28f);   // Standard: worn leather
            if (m_mat.HasProperty("_Metallic")) m_mat.SetFloat("_Metallic", 0f);
            Plugin.L.LogInfo("VRHands: glove shader '" + m_shaderName + "'");
        }

        void ApplyColours()
        {
            if (m_tex == null) return;
            m_tex.SetPixels(new[] { Leather, CuffLeather, Trim, Lining });
            m_tex.Apply(false, false);
            foreach (Side s in new[] { m_l, m_r }) if (s.Mesh != null) s.Mesh.colors32 = RegionColours(s.Geo);
        }

        // ---- ambient floor ----------------------------------------------------------------------
        // The game lights its world with deferred lights the gloves' forward shader barely sees: in a
        // dark scene they were black shapes. A second, additive pass of the same mesh adds a little of
        // the glove's own colour (linear brightness Floor), so the leather always reads; the scene's
        // lights still add on top.
        public static float Floor = 0.05f;
        /// At the title (a dark night scene lit by a distant fire) the gloves were near-black shapes: "Some hands would be
        /// nice" (user 2026-10-11 evening). There the floor is higher.
        public static float TitleFloor = 0.16f;
        float m_floorNow = -1f;

        void UpdateFloor()
        {
            float want = Places.Current == Places.Place.Title ? TitleFloor : Floor;
            if (Mathf.Approximately(want, m_floorNow) || m_floorMat == null) return;
            m_floorNow = want;
            float k = QualitySettings.activeColorSpace == ColorSpace.Linear ? Mathf.LinearToGammaSpace(want) : want;
            m_floorMat.color = new Color(k, k, k, 1f);
        }

        // "Standard" is not in the build's own resources, but the game's asset bundles bring it (the
        // card-table room and characters use it). Once it is loaded the gloves switch to it: it draws
        // into the deferred G-buffer, so they are lit by every light of the scene like the room is.
        float m_nextShaderTry;
        static bool s_shaderForced;


        /// The gloves are calibrated on Touch controllers (Quest). Another controller type (Index, as the
        /// Monado test headset reports) gets the hand moved onto its own grip (ControllerGeometry).
        // the fit the gloves were last adapted for (compared as numbers: a key string built here three times a frame was
        // ~150 KB/s of garbage - the table's GC stalls, 2026-10-13)
        string m_adaptedType;
        float m_aAngle = float.NaN, m_aUp, m_aAlong, m_aTurn, m_aRoll, m_aSide;

        void AdaptToController()
        {
            string t = ControllerGeometry.Type;
            float angle = Settings.GloveAngle, up = Settings.GloveUp, along = Settings.GloveAlong, turn = Settings.GloveTurn, roll = Settings.GloveRoll, side = Settings.GloveSide;
            if (ReferenceEquals(t, m_adaptedType) || t == m_adaptedType)
                if (angle == m_aAngle && up == m_aUp && along == m_aAlong && turn == m_aTurn && roll == m_aRoll && side == m_aSide) return;
            m_adaptedType = t;
            m_aAngle = angle; m_aUp = up; m_aAlong = along; m_aTurn = turn; m_aRoll = roll; m_aSide = side;
            if (t == "knuckles")
            {
                HandGeometry.AdaptFrom = ControllerGeometry.TouchGrip;   // Touch grip (right hand)
                HandGeometry.AdaptTo = ControllerGeometry.GripPos(false);
                HandGeometry.AdaptRot = Quaternion.Euler(20.6f - ControllerGeometry.GripPitchUp, 0f, 0f);
            }
            else
            {
                // Quest Touch (2026-10-11 evening: "My hand's angle didn't match the controller's"): the glove was fitted
                // round the controller frame's 20.6-degree grip axis, but the Quest 2's real handle runs along the frame's
                // forward (WiVRn's own controller model placed in OpenComposite's frame; docs/hand-feel-design.md §2.4):
                // the glove turns GloveAngle about the grip point (fingers down) and shifts with it. The sword's grip
                // follows (ControllerGeometry.GripPos / GripPitchUp), and the touch point is the glove's own fingertip.
                HandGeometry.AdaptFrom = ControllerGeometry.TouchGrip;
                HandGeometry.AdaptTo = ControllerGeometry.TouchFitGrip(false);
                // (2026-10-12: "an option to rotate hands along the other axes, which I need": turn and roll too, about the grip;
                // right-hand frame - the left hand is its mirror image, so both turn symmetrically)
                HandGeometry.AdaptRot = Quaternion.Euler(Settings.GloveAngle, Settings.GloveTurn, Settings.GloveRoll);
            }
            HandGeometry.Version++;
            Plugin.L.LogInfo("VRHands: controllers '" + t + "' - " + (t == "knuckles" ? "hand moved from the Touch grip onto this controller's grip"
                : "Touch fit: " + Settings.GloveFitText()));
        }

        void UpgradeShader()
        {
            if (s_shaderForced || m_shaderName == "Standard" || Time.unscaledTime < m_nextShaderTry) return;
            m_nextShaderTry = Time.unscaledTime + 2f;
            // Shader.Find does not see shaders that came with asset bundles: look among the loaded ones
            Shader sh = Shader.Find("Standard");
            if (sh == null)
                foreach (Shader x in Resources.FindObjectsOfTypeAll<Shader>())
                    if (x != null && x.name == "Standard") { sh = x; break; }
            if (sh == null || !sh.isSupported) return;
            m_mat.shader = sh;
            m_shaderName = "Standard";
            m_mat.color = Color.white;
            if (m_mat.HasProperty("_Glossiness")) m_mat.SetFloat("_Glossiness", 0.28f);   // worn leather
            if (m_mat.HasProperty("_Metallic")) m_mat.SetFloat("_Metallic", 0f);
            foreach (Side sd in new[] { m_l, m_r }) if (sd.R != null) sd.R.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            Plugin.L.LogInfo("VRHands: glove shader 'Standard' (loaded by the game)");
        }
        Material m_floorMat;

        static Color32[] RegionColours(HandGeometry g)
        {
            bool lin = QualitySettings.activeColorSpace == ColorSpace.Linear;
            var c = new Color32[g.Uv.Length];
            for (int i = 0; i < c.Length; i++)
            {
                float u = g.Uv[i].x;
                Color col = u < 0.25f ? Leather : u < 0.5f ? CuffLeather : u < 0.75f ? Trim : Lining;
                if (lin) col = col.linear;
                c[i] = col;
            }
            return c;
        }

        void ApplyFloor()
        {
            if (m_floorMat == null && Floor > 0f)
            {
                Shader sh = Shader.Find("Hidden/Internal-Colored");
                if (sh == null) { Plugin.L.LogWarning("VRHands: no Internal-Colored shader - no ambient floor"); Floor = 0f; return; }
                m_floorMat = new Material(sh) { name = "HoF2VR_GloveFloor", renderQueue = 2001 };
                m_floorMat.SetInt("_SrcBlend", (int)BlendMode.One);
                m_floorMat.SetInt("_DstBlend", (int)BlendMode.One);
                m_floorMat.SetInt("_ZWrite", 0);
                m_floorMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
                m_floorMat.SetInt("_Cull", (int)CullMode.Back);
                m_floorMat.SetFloat("_ZBias", -1f);
            }
            if (m_floorMat != null)
            {
                // material colours are taken as sRGB: hand over the gamma value of the linear factor
                float k = QualitySettings.activeColorSpace == ColorSpace.Linear ? Mathf.LinearToGammaSpace(Floor) : Floor;
                m_floorMat.color = new Color(k, k, k, 1f);
            }
            foreach (Side s in new[] { m_l, m_r })
            {
                if (s.R == null) continue;
                s.R.sharedMaterials = Floor > 0f && m_floorMat != null ? new[] { m_mat, m_floorMat } : new[] { m_mat };
            }
        }

        string DescribeLights()
        {
            var sb = new System.Text.StringBuilder();
            Vector3 head = VRRig.I != null ? VRRig.I.HeadWorldPos : Vector3.zero;
            int n = 0;
            foreach (Light l in FindObjectsOfType<Light>())
            {
                if (!l.isActiveAndEnabled) continue;
                n++;
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "\n  {0} {1} int={2:0.##} range={3:0.#} col={4} mask=0x{5:X} mode={6} shadows={7} dist={8:0.0} | {9}",
                    l.type, l.bakingOutput.lightmapBakeType, l.intensity, l.range, l.color, l.cullingMask, l.renderMode, l.shadows,
                    l.type == LightType.Directional ? 0f : Vector3.Distance(head, l.transform.position), VRUI.Path(l.transform));
            }
            Plugin.L.LogInfo("lights (" + n + "):" + sb);
            return n + " lights (see log)";
        }

        static string DescribeShaders(string filter)
        {
            var seen = new Dictionary<string, int>();
            var sample = new Dictionary<string, string>();
            foreach (Renderer r in FindObjectsOfType<Renderer>())
            {
                if (!r.enabled) continue;
                string path = VRUI.Path(r.transform);
                if (filter.Length > 0 && path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    string k = m.shader.name + " (layer " + r.gameObject.layer + ", queue " + m.renderQueue + ")";
                    int c;
                    seen.TryGetValue(k, out c);
                    seen[k] = c + 1;
                    if (!sample.ContainsKey(k)) sample[k] = path;
                }
            }
            var sb = new System.Text.StringBuilder();
            foreach (var kv in seen) sb.Append("\n  ").Append(kv.Value).Append("x ").Append(kv.Key).Append(" e.g. ").Append(sample[kv.Key]);
            Plugin.L.LogInfo("shaders '" + filter + "':" + sb);
            return seen.Count + " shaders (see log)";
        }

        // ---- test command -----------------------------------------------------------------------

        string Describe()
        {
            return string.Format("on={0} shader='{1}' camLayer={2} L[{3}] R[{4}] touch={5}{6} health={7}", On, m_shaderName, m_camLayer,
                DescribeSide(m_l), DescribeSide(m_r), FingerSampler.TriggerTouchSeen ? "trigger " : "", FingerSampler.ThumbTouchSeen ? "face" : "", Health.Describe());
        }

        static string DescribeSide(Side s)
        {
            if (s.T == null) return "-";
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0} layer={1} scale={2:0.00} {3}{4}curl i{5:0.00} m{6:0.00} r{7:0.00} p{8:0.00} point{9:0.00} thumb{10:0.00} pos={11}",
                s.Shown ? "shown" : "hidden", s.T.gameObject.layer, s.T.lossyScale.x, s.Holding ? "holding " : "", s.Pointing ? "pointing " : "",
                s.Pose.Index, s.Pose.Middle, s.Pose.Ring, s.Pose.Pinky, s.Pose.Point, s.Pose.Thumb, Diag.V(s.T.position));
        }

        static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

        /// hands                                  state of both hands
        /// hands on|off                           show / hide
        /// hands pose open|grip|fist|point|thumbup|auto   force a pose (screenshots)
        /// hands layer <n>|auto                   force the render layer
        /// hands shader <name>                    switch shader (e.g. "Legacy Shaders/VertexLit")
        /// hands color r g b                      glove leather colour (sRGB 0-1)
        /// hands nudge x y z [pitch yaw roll]     placement tuning, cm / degrees in controller space
        /// hands health <0-1>|hit|off             health gems: force a value (shown anywhere), flash, back to live
        string Command(string[] a)
        {
            if (a.Length < 2) return Describe();
            switch (a[1])
            {
                case "depth": DepthPass = a.Length < 3 || a[2] != "0"; return "depth pass " + DepthPass;
                case "on": On = true; return "on";
                case "off": On = false; return "off";
                case "pose":
                    s_force = a.Length > 2 && a[2] != "auto" ? a[2] : "";
                    return "pose " + (s_force.Length > 0 ? s_force : "auto (input)");
                case "layer":
                    s_forceLayer = a.Length > 2 && a[2] != "auto" ? int.Parse(a[2]) : -1;
                    return "layer " + (s_forceLayer >= 0 ? s_forceLayer.ToString() : "auto");
                case "shader":
                {
                    string n = string.Join(" ", a, 2, a.Length - 2);
                    Shader sh = Shader.Find(n);
                    if (sh == null || m_mat == null) return n + ": not found";
                    m_mat.shader = sh;
                    m_shaderName = n;
                    s_shaderForced = true;
                    m_mat.color = n == "Unlit/Color" || n == "Hidden/Internal-Colored" ? Leather : Color.white;
                    return "shader " + n + (sh.isSupported ? "" : " (not supported!)");
                }
                case "color":
                    Leather = new Color(F(a[2]), F(a[3]), F(a[4]));
                    ApplyColours();
                    if (m_mat != null && (m_shaderName == "Unlit/Color" || m_shaderName == "Hidden/Internal-Colored")) m_mat.color = Leather;
                    return "leather " + Leather;
                case "nudge":
                    HandGeometry.NudgePos = new Vector3(F(a[2]), F(a[3]), F(a[4])) * 0.01f;
                    HandGeometry.NudgeEuler = a.Length > 7 ? new Vector3(F(a[5]), F(a[6]), F(a[7])) : Vector3.zero;
                    HandGeometry.Version++;
                    return "nudge " + Diag.V(HandGeometry.NudgePos * 100f) + " cm, " + Diag.V(HandGeometry.NudgeEuler) + " deg";
                case "health":
                    return Health.Command(a);
                case "back":
                    // `hands back [badge|stats|lift <cm>] [ang <deg>] [font <n>] [badgesize <cm>]`: the items on the back of the wrist, this
                    // run only
                    for (int i = 2; i + 1 < a.Length; i += 2)
                    {
                        if (a[i] == "badge") HandGeometry.BadgeY = F(a[i + 1]);
                        else if (a[i] == "stats") HandGeometry.StatsY = F(a[i + 1]);
                        else if (a[i] == "lift") HandGeometry.BackLift = F(a[i + 1]);
                        else if (a[i] == "ang") HandGeometry.BadgeAng = F(a[i + 1]);
                        else if (a[i] == "font") Health.StatsFont = Mathf.Clamp(Mathf.RoundToInt(F(a[i + 1])), 8, 80);
                        else if (a[i] == "badgesize") HandIcons.CuffBadgeSize = F(a[i + 1]) * 0.01f;
                    }
                    HandGeometry.Version++;
                    return Health.StatsDescribe() + " | artefact badge " + (HandIcons.CuffBadgeSize * 100f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " cm";
                case "floor":
                    Floor = Mathf.Max(0f, F(a[2]));
                    ApplyFloor();
                    return "ambient floor " + Floor;
                case "lights":
                    return DescribeLights();
                case "probe":
                {
                    // where the right hand's index points vs the laser, in the controller frame and in the world
                    Side sd = m_r;
                    if (sd.T == null || sd.Geo == null) return "no right hand";
                    Vector3 wrist = sd.Geo.WristInController, tip = sd.Geo.IndexTip, knuckle = sd.Geo.IndexKnuckle;
                    Vector3 fingerDir = (tip - knuckle).normalized;
                    Vector3 laserDir = Quaternion.Euler(ControllerGeometry.TipPitchDown, 0f, 0f) * Vector3.forward;
                    Vector3 worldFinger = sd.T.TransformDirection(fingerDir), worldLaser = sd.T.TransformDirection(laserDir);
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "controller frame: wrist {0} knuckle {1} tip {2} finger dir {3} laser dir {4} angle {5:0.0} deg | world finger {6} laser {7} | hand rot {8} input rot {9}",
                        Diag.V(wrist * 100f), Diag.V(knuckle * 100f), Diag.V(tip * 100f), Diag.V(fingerDir), Diag.V(laserDir), Vector3.Angle(fingerDir, laserDir),
                        Diag.V(worldFinger), Diag.V(worldLaser), Diag.V(sd.T.rotation.eulerAngles), Diag.V(VRInput.R.Rot.eulerAngles));
                }
                case "shaders":
                    return DescribeShaders(a.Length > 2 ? a[2] : "");
                case "shaderfrom":
                {
                    string f = string.Join(" ", a, 2, a.Length - 2);
                    foreach (Renderer r in FindObjectsOfType<Renderer>())
                    {
                        if (!r.enabled || r.sharedMaterial == null || r.sharedMaterial.shader == null) continue;
                        if (VRUI.Path(r.transform).IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        Material src = r.sharedMaterial;
                        m_mat.shader = src.shader;
                        m_shaderName = src.shader.name;
                        s_shaderForced = true;
                        m_mat.mainTexture = m_tex;
                        if (m_mat.HasProperty("_Color")) m_mat.color = Color.white;
                        var props = new System.Text.StringBuilder();
                        foreach (string pn in new[] { "_MainTex", "_Color", "_BumpMap", "_MetallicGlossMap", "_SpecGlossMap", "_Glossiness", "_Metallic", "_EmissionColor", "_EmissionMap", "_OcclusionMap", "_DetailAlbedoMap", "_SpecColor" })
                            if (m_mat.HasProperty(pn)) props.Append(pn).Append(' ');
                        Plugin.L.LogInfo("VRHands: glove shader from '" + VRUI.Path(r.transform) + "': '" + src.shader.name + "' queue " + src.renderQueue + " keywords [" +
                                         string.Join(" ", src.shaderKeywords) + "] props " + props);
                        return "shader '" + src.shader.name + "' from " + r.name + " (props: " + props + ")";
                    }
                    return "no renderer matching '" + f + "'";
                }
            }
            return "usage: hands [on|off|pose <open|grip|fist|point|thumbup|auto>|layer <n|auto>|shader <name>|color r g b|nudge x y z [p y r]|health <0-1|hit|off>]";
        }

        // ---- health gems on the back of the non-weapon glove ----------------------------------------

        /// Half-Life: Alyx style health display: a row of gems set into the back of the glove on the
        /// shield hand, only in fights. Each gem is 1/8 of the hero's health; lit gems are warm white at
        /// full health and shift through amber to red as it drops, the gem at the edge glows partly,
        /// all of them flash when the hero is hit and beat slowly below a quarter. They stay dim unless
        /// the back of the hand is turned towards the eyes. Unlit (Sprites/Default, HDR tint so the
        /// game's bloom makes them glow), drawn by the same camera and layer as the glove.
        static class Health
        {
            static GameObject s_go;
            static Mesh s_mesh;
            static MeshRenderer s_r;
            static Material s_mat;
            static Color32[] s_colors;
            static HandGeometry s_geo;          // whose plate the mesh currently shows
            static int s_geoVersion = -1;
            static PlayerController s_pc;
            static Destroyable s_dest;
            static float s_shown = -1f;         // health ratio shown (smoothed)
            static float s_last = -1f;          // last live ratio (hit detection)
            static float s_flash, s_look;
            static float s_force = -1f;         // `hands health <0-1>`
            static bool s_visible, s_failed, s_linear;
            static int s_colorProp;
            /// Brightness of the lit gems, linear (above 1 the game's bloom makes them glow); dimmed to
            /// Dim of it while the back of the hand is turned away.
            public static float Intensity = 1.8f, Dim = 0.3f;

            public static void Awake() { s_colorProp = Shader.PropertyToID("_Color"); }

            public static string Describe()
            {
                return s_visible ? (s_shown.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + (s_force >= 0f ? " (forced)" : "")) : "hidden";
            }

            public static string Command(string[] a)
            {
                if (a.Length < 3) return "health " + Describe();
                if (a[2] == "off") { s_force = -1f; return "health: live"; }
                if (a[2] == "hit") { s_flash = 1f; return "health: flash"; }
                s_force = Mathf.Clamp01(F(a[2]));
                return "health forced to " + s_force;
            }

            public static void Hide()
            {
                if (s_r != null && s_r.enabled) s_r.enabled = false;
                s_visible = false;
                SuperMeter.Hide();
                if (s_statRoot != null && s_statRoot.gameObject.activeSelf) s_statRoot.gameObject.SetActive(false);
            }

            // ---- at the Dealer's table: health from the player's stats, and the numbers on the cuff --------------------------
            static float TableRatio()
            {
                if (!Settings.GloveStats || !Places.AtCaravan || CombatVR.Active || VRRig.I == null || !VRRig.I.HasStation) return -1f;
                try
                {
                    Player p = Player.Instance;
                    if (p == null || p.Health == null || p.MaxHealth == null) return -1f;
                    float max = p.MaxHealth.Get();
                    return max > 0f ? Mathf.Clamp01(p.Health.Get() / max) : -1f;
                }
                catch (Exception) { return -1f; }
            }

            static Transform s_statRoot;
            static UILabel s_stat;
            static int s_hp = int.MinValue, s_hpMax, s_food, s_gold;
            public static string StatsShown = "-";
            /// The stats are drawn only within this distance of the eyes (real metres) with the cuff's back toward them.
            public static float StatsNear = 0.55f;

            /// Health, food and gold on the cuff's back, along the forearm (read with the back of the hand turned toward you).
            static void Stats(Transform hand, HandGeometry geo)
            {
                bool on = Settings.GloveStats && Places.AtCaravan && !CombatVR.Active && s_force < 0f && GameText.Ready;
                Player p = on ? Player.Instance : null;
                if (p == null || p.Health == null || p.MaxHealth == null || p.Food == null || p.Gold == null)
                {
                    if (s_statRoot != null && s_statRoot.gameObject.activeSelf) s_statRoot.gameObject.SetActive(false);
                    return;
                }
                if (s_statRoot == null)
                {
                    var go = new GameObject("HoF2VR_GloveStats") { layer = VRUI.Layer };
                    GameText.Panel(go, 3150);
                    s_statRoot = go.transform;
                    var holder = new GameObject("scale") { layer = VRUI.Layer };
                    holder.transform.SetParent(s_statRoot, false);
                    holder.transform.localScale = Vector3.one * 0.00034f;
                    s_stat = GameText.Label(holder.transform, "text", GameText.Normal, 26, GameText.Parchment, 2);
                    s_stat.spacingY = 2;
                }
                if (s_statRoot.parent != hand) s_statRoot.SetParent(hand, false);
                // shown only while it can be read: the cuff's back turned toward the eyes and near them (candidate 8's tour:
                // at rest the numbers were 0.64 deg tall and turned 104 deg away - a smudge, failing mod_text_*)
                VRRig rig = VRRig.I;
                Vector3 at = hand.TransformPoint(geo.StatsBack), nn = hand.TransformDirection(geo.StatsNormal);
                Vector3 toEye = rig != null ? rig.HeadWorldPos - at : Vector3.zero;
                float sc = rig != null ? rig.Scale : 1f;
                bool readable = toEye.sqrMagnitude > 1e-8f && Vector3.Dot(nn.normalized, toEye.normalized) > 0.55f && toEye.magnitude < StatsNear * sc;
                if (s_statRoot.gameObject.activeSelf != readable) s_statRoot.gameObject.SetActive(readable);
                if (!readable) return;
                int hp = Mathf.RoundToInt(p.Health.Get()), hpMax = Mathf.RoundToInt(p.MaxHealth.Get()), food = Mathf.RoundToInt(p.Food.Get()), gold = Mathf.RoundToInt(p.Gold.Get());
                if (hp != s_hp || hpMax != s_hpMax || food != s_food || gold != s_gold)
                {
                    s_hp = hp; s_hpMax = hpMax; s_food = food; s_gold = gold;
                    StatsShown = "Health " + hp + " / " + hpMax + "\nFood " + food + "   Gold " + gold;
                    s_stat.text = StatsShown;
                }
                if (s_stat.fontSize != StatsFont) s_stat.fontSize = StatsFont;
                // lying on the back of the wrist, the text running toward the fingers, read from outside (NGUI draws toward -z)
                Vector3 n = geo.StatsNormal, x = geo.StatsDistal;
                x = (x - n * Vector3.Dot(x, n)).normalized;
                Vector3 z = -n, y = Vector3.Cross(z, x);
                s_statRoot.localPosition = geo.StatsBack;
                s_statRoot.localRotation = Quaternion.LookRotation(z, y);
            }

            /// The numbers' font size (NGUI units of 0.34 mm on the glove; `hands back font <n>`).
            public static int StatsFont = 22;

            /// `hands back`: where the numbers and the badge lie, and the numbers' printed size on the glove (cm, life size).
            public static string StatsDescribe()
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string size = "-";
                if (s_stat != null)
                {
                    Vector2 ps = s_stat.printedSize;
                    size = (ps.x * 0.034f).ToString("0.0", inv) + " x " + (ps.y * 0.034f).ToString("0.0", inv) + " cm '" + s_stat.text.Replace("\n", " | ") + "'" +
                           (s_statRoot != null && s_statRoot.gameObject.activeInHierarchy ? " shown" : " hidden");
                }
                return string.Format(inv, "badge at {0:0.00} cm {7:0} deg, numbers at {1:0.00} cm (from the wrist joint, + toward the fingers), lift {2:0.00} cm, font {3} | numbers {4} | the cuff from {5:0.00} to -{6:0.00} cm",
                    HandGeometry.BadgeY, HandGeometry.StatsY, HandGeometry.BackLift, StatsFont, size, 1.3f, HandGeometry.BackEndCm, HandGeometry.BadgeAng);
            }

            static HandGeometry s_rowFor;
            static Vector3 s_row;

            /// The gems' row: the longest extent of their vertices (hand-local).
            static Vector3 RowOf(Vector3[] v)
            {
                if (v == null || v.Length < 2) return Vector3.zero;
                Vector3 a = v[0], best = Vector3.zero;
                float most = 0f;
                for (int i = 1; i < v.Length; i++) { Vector3 d = v[i] - a; if (d.sqrMagnitude > most) { most = d.sqrMagnitude; best = d; } }
                Vector3 b = a + best;
                for (int i = 0; i < v.Length; i++) { Vector3 d = v[i] - b; if (d.sqrMagnitude > most) { most = d.sqrMagnitude; best = -d; } }
                return best;
            }

            static float LiveRatio()
            {
                PlayerController pc = PlayerController.Instance;
                if (pc == null) { s_pc = null; s_dest = null; return -1f; }
                if (pc != s_pc) { s_pc = pc; s_dest = pc.GetComponent<Destroyable>(); }
                return s_dest != null ? Mathf.Clamp01(s_dest.HealthRatio) : -1f;
            }

            public static void Update(Transform hand, HandGeometry geo, int layer, VRRig rig, float dt)
            {
                // [Combat] FightHud = Body: the fight timer as a band round this wrist (its own visibility rules)
                FightHud.Wrist(hand, geo, layer, rig);
                // (T7, playtest 2026-10-17: "should I see my health and the rest at the side table?" - at the Dealer's table too)
                float live = CombatVR.Active ? LiveRatio() : TableRatio();
                float ratio = s_force >= 0f ? s_force : live;
                // (`[Combat] GloveHud` off: UYF's HUD switch - the gems and the super meter beside them hidden)
                if (hand == null || geo == null || ratio < 0f || !CombatSettings.GloveHud) { Hide(); s_last = -1f; s_shown = -1f; return; }
                Stats(hand, geo);
                if (s_go == null && (s_failed || !Create())) return;
                if (s_geo != geo || s_geoVersion != HandGeometry.Version)
                {
                    s_geo = geo;
                    s_geoVersion = HandGeometry.Version;
                    s_mesh.vertices = geo.HealthVerts;
                    s_mesh.bounds = geo.Bounds;
                }
                if (s_go.transform.parent != hand) s_go.transform.SetParent(hand, false);
                if (s_go.layer != layer) s_go.layer = layer;

                // a hit: health went down since last frame
                if (live >= 0f && s_last >= 0f && live < s_last - 0.002f) s_flash = 1f;
                s_last = live;
                if (s_shown < 0f) s_shown = ratio;
                s_shown = Mathf.MoveTowards(s_shown, ratio, dt * 0.8f);   // gems go out over a moment
                s_flash = Mathf.Max(0f, s_flash - dt * 2.5f);

                // bright while the back of the hand faces the eyes, dim otherwise
                Vector3 c = hand.TransformPoint(geo.HealthCenter);
                Vector3 n = hand.TransformDirection(geo.HealthNormal);
                Vector3 toEye = rig.HeadWorldPos - c;
                float facing = toEye.sqrMagnitude > 1e-8f ? Vector3.Dot(n.normalized, toEye.normalized) : 0f;
                s_look = Mathf.MoveTowards(s_look, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 0.7f, facing)), dt * 5f);

                Paint(geo);
                // combat Stage 2 item 8: UYF's super meter beside the gems
                if (s_rowFor != geo) { s_rowFor = geo; s_row = RowOf(geo.HealthVerts); }
                if (CombatVR.Active || s_force >= 0f) SuperMeter.Draw(hand, geo.HealthCenter, geo.HealthNormal, s_row, layer, s_look);
                else SuperMeter.Hide();
                float beat = s_shown < 0.25f ? 0.8f + 0.2f * Mathf.Sin(Time.unscaledTime * 7.5f) : 1f;
                float k = Mathf.Lerp(Dim, 1f, s_look) * beat * (1f + 1.2f * s_flash) * Intensity;
                // material colours are taken as sRGB and linearised in a linear-space project: hand
                // over the gamma value of the linear multiplier we want
                if (s_linear) k = Mathf.LinearToGammaSpace(k);
                s_mat.SetColor(s_colorProp, new Color(k, k, k, 1f));
                if (!s_r.enabled) s_r.enabled = true;
                s_visible = true;
            }

            /// Gem colours for the shown health: lit, partly lit (the edge gem) or dark.
            static void Paint(HandGeometry geo)
            {
                int pips = HandGeometry.HealthPips;
                float h = s_shown;
                Color hue = h > 0.6f ? Color.Lerp(new Color(1f, 0.62f, 0.25f), new Color(1f, 0.93f, 0.8f), (h - 0.6f) / 0.4f)
                          : h > 0.3f ? Color.Lerp(new Color(1f, 0.25f, 0.08f), new Color(1f, 0.62f, 0.25f), (h - 0.3f) / 0.3f)
                          : new Color(1f, 0.1f, 0.06f);
                hue = Color.Lerp(hue, new Color(1f, 0.2f, 0.15f), s_flash * 0.7f);
                for (int i = 0; i < geo.HealthPip.Length; i++)
                {
                    int pip = geo.HealthPip[i];
                    int role = geo.HealthRole[i];
                    Color col;
                    if (role == 0) col = new Color(0.05f, 0.04f, 0.035f, 0.92f);           // inlay band
                    else if (role == 1) col = new Color(0.12f, 0.09f, 0.06f, 1f);          // gem bezel
                    else
                    {
                        float fill = Mathf.Clamp01(h * pips - pip);                    // 1 lit, 0 out
                        Color dark = new Color(0.16f, 0.05f, 0.03f, 1f);               // a dead ember
                        Color lit = role == 3 ? hue : hue * 0.55f;                     // centre / facet rim
                        col = Color.Lerp(dark, lit, fill);
                        col.a = 1f;
                    }
                    // vertex colours reach the shader as they are: linearise the sRGB design colours
                    s_colors[i] = s_linear ? new Color(Mathf.GammaToLinearSpace(col.r), Mathf.GammaToLinearSpace(col.g), Mathf.GammaToLinearSpace(col.b), col.a) : col;
                }
                s_mesh.colors32 = s_colors;
            }

            static bool Create()
            {
                Shader sh = Shader.Find("Sprites/Default");
                if (sh == null) { s_failed = true; Plugin.L.LogWarning("VRHands: Sprites/Default missing - no health gems"); return false; }
                s_linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
                s_go = new GameObject("HoF2VR_HealthGems");
                if (I != null) s_go.transform.SetParent(I.transform, false);
                s_mesh = new Mesh { name = "HoF2VR_HealthGems" };
                s_mesh.MarkDynamic();
                HandGeometry g = new HandGeometry(false);   // topology only; vertices come per hand
                s_mesh.vertices = g.HealthVerts;
                s_colors = new Color32[g.HealthVerts.Length];
                s_mesh.colors32 = s_colors;
                s_mesh.triangles = g.HealthTris;
                s_mesh.bounds = g.Bounds;
                s_go.AddComponent<MeshFilter>().sharedMesh = s_mesh;
                s_r = s_go.AddComponent<MeshRenderer>();
                s_mat = new Material(sh) { name = "HoF2VR_HealthGems" };
                s_r.sharedMaterial = s_mat;
                s_r.shadowCastingMode = ShadowCastingMode.Off;
                s_r.receiveShadows = false;
                s_r.lightProbeUsage = LightProbeUsage.Off;
                s_r.reflectionProbeUsage = ReflectionProbeUsage.Off;
                s_r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                s_r.allowOcclusionWhenDynamic = false;
                s_r.enabled = false;
                s_geo = null;
                return true;
            }
        }
    }

    /// Finger and thumb state of one hand. Curl: 0 relaxed and open, 1 wrapped round the controller
    /// handle, 2 a tight fist (sword hilt). Point: 0..1 straightens the index. Thumb: 0 lifted,
    /// 1 resting on the controller's face, 2 wrapped round the grip.
    internal struct HandPose
    {
        public float Index, Middle, Ring, Pinky, Point, Thumb;

        public HandPose(float index, float middle, float ring, float pinky, float point, float thumb)
        {
            Index = index; Middle = middle; Ring = ring; Pinky = pinky; Point = point; Thumb = thumb;
        }

        public static float MaxDelta(HandPose a, HandPose b)
        {
            float d = Math.Abs(a.Index - b.Index);
            d = Math.Max(d, Math.Abs(a.Middle - b.Middle));
            d = Math.Max(d, Math.Abs(a.Ring - b.Ring));
            d = Math.Max(d, Math.Abs(a.Pinky - b.Pinky));
            d = Math.Max(d, Math.Abs(a.Point - b.Point));
            return Math.Max(d, Math.Abs(a.Thumb - b.Thumb));
        }
    }

    /// One gloved hand as a mesh in the controller's own frame (Unity axes, metres, life size; the
    /// left hand is the mirror image), built from rigid parts - palm, cuff with a rolled edge and a
    /// wrist strap, three segments per finger and thumb - whose vertices are only rotated and moved
    /// when the pose changes. Pure math: no engine calls, so the offline harness runs it as well.
    ///
    /// The hand is modelled in a hand frame (centimetres): origin at the wrist joint, +X radial (thumb
    /// side), +Y distal (towards the fingertips), +Z dorsal (back of the hand), right hand. Its place
    /// on the controller was calibrated against SteamVR's Touch reference poses: wrist 16.5 cm behind
    /// the controller origin, back of the hand facing the controller's +X (outwards), the handle
    /// (OpenXR grip axis) running diagonally across the palm. Finger wrap angles were fitted offline so
    /// the fingers close round a 2 cm-radius handle (curl 1) and a 1.6 cm sword hilt (curl 2), both
    /// on the grip axis that CombatVR.Place puts the sword on.
    internal sealed class HandGeometry
    {
        const float CM = 0.01f;
        const int FingerAround = 12, PalmAround = 20, CuffAround = 24;
        public const float UGlove = 0.125f, UCuff = 0.375f, UTrim = 0.625f, UInner = 0.875f;   // 4x1 colour texture
        public const int HealthPips = 8;

        /// Placement tuning on top of the calibrated fit (`hands nudge`), controller space; the left
        /// hand gets the mirror image. Bump Version after changing.
        public static Vector3 NudgePos, NudgeEuler;
        public static int Version;
        /// Controllers other than Touch (Index "knuckles", e.g. the Monado test headset): the hand is
        /// calibrated on Touch; it is moved so its fist sits on the other controller's grip the same
        /// way (grip point AdaptFrom -> AdaptTo, metres, right hand; fist axis turned by AdaptRot).
        /// Set by VRHands; bump Version after changing.
        public static Quaternion AdaptRot = Quaternion.identity;
        public static Vector3 AdaptFrom, AdaptTo;

        public readonly bool Left;
        public readonly Vector3[] Verts, Normals;
        public readonly Vector2[] Uv;
        public readonly int[] Tris;
        public Bounds Bounds;
        // health plate (static on the back of the hand): vertices, triangles, gem index and role per
        // vertex (0 inlay band, 1 bezel, 2 facet rim, 3 gem centre), and its centre and normal
        public Vector3[] HealthVerts;
        public readonly int[] HealthTris, HealthPip, HealthRole;
        public Vector3 HealthCenter, HealthNormal;
        /// The back of the wrist (the cuff's back, BackLift cm above it, at BadgeY), its outward normal and the way to the fingers
        /// (controller frame): the shield glove's artefact badge (HandIcons).
        public Vector3 CuffBack, CuffBackNormal, CuffDistal;
        /// The same at StatsY: where the glove's stat numbers lie at the table (Hands.Health).
        public Vector3 StatsBack, StatsNormal, StatsDistal;
        /// Where on the glove's back (cm from the wrist joint along the hand, + toward the fingers; BackSurface) the badge's and
        /// the numbers' middles lie, the badge's turn round the wrist (deg from its back, + toward the thumb) and how far above
        /// the glove both lie (`hands back badge|stats|lift <cm> | ang <deg>`; bump Version after changing). Settled from the
        /// pictures of 2026-10-20 (captures/hands-2026-10-20/after_tune_*): the numbers at 0.9 ran into the gems, at 0.4 they end
        /// just short of them - 0.3; the badge stays on the back of the wrist (the back of the hand above the cuff is the super
        /// meter's: a badge at 2.4-2.5 cm lay under it), the Body style's bands leave a gap under it (FightHud.BandG).
        public static float BadgeY = -0.15f, BadgeAng = 0f, StatsY = 0.3f, BackLift = 0.35f;
        /// Tip of the index finger in the controller frame (current pose), e.g. for a laser origin.
        public Vector3 IndexTip;
        public Vector3 IndexKnuckle, WristInController;   // diagnostics (`hands probe`)

        // ---- shape (right hand, hand frame, cm) ----------------------------------------------------

        internal struct Finger
        {
            public Vector3 Mcp;
            public float L1, L2, L3;            // proximal, middle, distal bone (the tip cap adds to L3)
            public Vector2 R0, R1, R2, R3;      // (width, thickness) radius at MCP, PIP, DIP, tip
            public Finger(Vector3 mcp, float l1, float l2, float l3, Vector2 r0, Vector2 r1, Vector2 r2, Vector2 r3)
            {
                Mcp = mcp; L1 = l1; L2 = l2; L3 = l3; R0 = r0; R1 = r1; R2 = r2; R3 = r3;
            }
        }

        internal static readonly Finger[] Fingers =
        {
            new Finger(new Vector3(2.85f, 8.9f, -0.2f), 4.35f, 2.65f, 1.55f, new Vector2(1.00f, 0.92f), new Vector2(0.93f, 0.85f), new Vector2(0.86f, 0.77f), new Vector2(0.80f, 0.70f)),
            new Finger(new Vector3(0.45f, 8.85f, 0.15f), 4.4f, 3.0f, 1.75f, new Vector2(1.04f, 0.95f), new Vector2(0.96f, 0.87f), new Vector2(0.88f, 0.78f), new Vector2(0.82f, 0.72f)),
            new Finger(new Vector3(-1.75f, 8.1f, 0.15f), 4.15f, 2.8f, 1.6f, new Vector2(0.98f, 0.90f), new Vector2(0.91f, 0.83f), new Vector2(0.84f, 0.75f), new Vector2(0.78f, 0.69f)),
            new Finger(new Vector3(-3.9f, 7.3f, -0.15f), 3.2f, 1.95f, 1.3f, new Vector2(0.88f, 0.82f), new Vector2(0.81f, 0.75f), new Vector2(0.75f, 0.68f), new Vector2(0.70f, 0.63f)),
        };
        const float TipStretch = 1.15f;

        /// Finger joint angles in degrees: flexion at MCP, PIP, DIP (towards the palm) and spread at
        /// the MCP (positive = towards the thumb).
        internal struct Ang
        {
            public float Mcp, Pip, Dip, Spread;
            public Ang(float mcp, float pip, float dip, float spread) { Mcp = mcp; Pip = pip; Dip = dip; Spread = spread; }
            public static Ang Lerp(Ang a, Ang b, float t)
            {
                return new Ang(a.Mcp + (b.Mcp - a.Mcp) * t, a.Pip + (b.Pip - a.Pip) * t, a.Dip + (b.Dip - a.Dip) * t, a.Spread + (b.Spread - a.Spread) * t);
            }
        }

        internal static readonly Ang[] Open = { new Ang(10f, 14f, 8f, 3f), new Ang(10f, 16f, 9f, -3f), new Ang(11f, 18f, 10f, -9f), new Ang(12f, 20f, 11f, -16f) };
        internal static readonly Ang[] Wrap = { new Ang(52.3f, 44f, 43.8f, -0.5f), new Ang(66.3f, 45.8f, 46.8f, 0.8f), new Ang(73.8f, 40.5f, 42.3f, -9.8f), new Ang(81.8f, 21.8f, 24.3f, -16f) };
        internal static readonly Ang[] Fist = { new Ang(56.8f, 46.8f, 46.3f, -0.8f), new Ang(71f, 47.3f, 50.5f, 1.3f), new Ang(78.5f, 41f, 46.5f, -6.5f), new Ang(87.3f, 21.3f, 23.5f, -15.3f) };
        internal static Ang PointAng = new Ang(0.8f, 4f, 3f, 2.5f);   // index along the laser (0.1 deg)

        // thumb: carpometacarpal joint, bone lengths, radii (CMC, MCP, IP, tip); pose = bone directions
        internal static readonly Vector3 ThumbCmc = new Vector3(2.05f, 2.25f, -2.3f);
        internal static readonly float[] ThumbLen = { 4.0f, 3.2f, 2.0f };
        internal static readonly float[] ThumbRad = { 1.5f, 1.12f, 1.02f, 0.88f };
        internal static readonly Vector3[] ThumbLift = { new Vector3(0.81f, 0.54f, -0.24f), new Vector3(0.73f, 0.61f, -0.30f), new Vector3(0.92f, 0.26f, -0.30f) };
        internal static readonly Vector3[] ThumbRest = { new Vector3(0.56f, 0.79f, -0.23f), new Vector3(0.39f, 0.85f, -0.35f), new Vector3(0.37f, 0.82f, -0.44f) };
        internal static readonly Vector3[] ThumbWrap = { new Vector3(0.27f, 0.73f, -0.63f), new Vector3(-0.54f, 0.79f, -0.29f), new Vector3(-0.97f, -0.03f, 0.23f) };

        // where the hand sits on the right controller: wrist joint (cm) and hand axes, controller frame
        internal static readonly Vector3 WristPos = new Vector3(3.4f, 3.6f, -16.5f);
        internal static readonly Vector3 DistalAxis = new Vector3(0.06f, -0.644f, 0.763f);
        internal static readonly Vector3 DorsalAxis = new Vector3(0.992f, -0.042f, -0.115f);
        // the controller's grip (OpenXR grip pose = fist centre, handle axis) - ControllerGeometry
        internal static readonly Vector3 GripPos = new Vector3(-0.7f, -0.18f, -10.2f);
        internal static Vector3 GripAxis => new Vector3(0f, Mathf.Sin(20.6f * Mathf.Deg2Rad), Mathf.Cos(20.6f * Mathf.Deg2Rad));

        // ---- runtime state ------------------------------------------------------------------------

        sealed class Part
        {
            public int Start;
            public Vector3[] P, N;
        }

        readonly Part m_palm, m_cuff, m_strap;
        readonly Part[] m_knuckle = new Part[4];
        readonly Part[,] m_fing = new Part[4, 3];
        readonly Part[] m_thumb = new Part[3];
        Quaternion m_q;   // hand frame -> controller frame
        Vector3 m_t;      // (metres)
        int m_staticVersion = -1;
        Vector3[] m_healthLocal;

        public HandGeometry(bool left)
        {
            Left = left;
            var b = new MeshBuild();
            m_palm = b.Loft(PalmRings(), PalmPoles(true), PalmPoles(false), null, UGlove, 3);
            float[] cuffU;
            // the lining ends in a cap just inside the rim (playtest 2026-10-17, marks 3, 7, 14: through the open cuff the
            // table showed where the arm should be; the palm's back faces are culled): the cuff looks closed, in the lining's colour
            m_cuff = b.Loft(CuffRings(out cuffU), null, CuffCap(), cuffU, 0f, 2);
            m_strap = b.Loft(StrapRings(), null, null, null, UTrim, 1);
            for (int f = 0; f < 4; f++)
            {
                Finger s = Fingers[f];
                m_knuckle[f] = b.Ellipsoid(new Vector3(s.R0.x * 0.82f, 0.8f, s.R0.y * 0.72f), FingerAround, 6, UGlove);
            }
            for (int f = 0; f < 4; f++)
            {
                Finger s = Fingers[f];
                m_fing[f, 0] = b.Capsule(s.L1, s.R0, s.R1, 2, 2, 1f, FingerAround, UGlove);
                m_fing[f, 1] = b.Capsule(s.L2, s.R1, s.R2, 2, 2, 1f, FingerAround, UGlove);
                m_fing[f, 2] = b.Capsule(s.L3, s.R2, s.R3, 2, 3, TipStretch, FingerAround, UGlove);
            }
            for (int k = 0; k < 3; k++)
            {
                Vector2 r0 = new Vector2(ThumbRad[k], ThumbRad[k]), r1 = new Vector2(ThumbRad[k + 1], ThumbRad[k + 1]);
                m_thumb[k] = b.Capsule(ThumbLen[k], r0, r1, 2, k == 2 ? 3 : 2, k == 2 ? TipStretch : 1f, FingerAround, UGlove);
            }
            Verts = new Vector3[b.P.Count];
            Normals = new Vector3[b.P.Count];
            Uv = b.Uv.ToArray();
            Tris = b.T.ToArray();
            if (left)
                for (int i = 0; i < Tris.Length; i += 3) { int t = Tris[i + 1]; Tris[i + 1] = Tris[i + 2]; Tris[i + 2] = t; }
            Bounds = new Bounds(new Vector3(0f, 0f, -0.1f), new Vector3(0.36f, 0.36f, 0.4f));

            List<int> ht, hp, hr;
            m_healthLocal = HealthPlate(out ht, out hp, out hr);
            HealthVerts = new Vector3[m_healthLocal.Length];
            HealthTris = ht.ToArray();
            if (left)
                for (int i = 0; i < HealthTris.Length; i += 3) { int t = HealthTris[i + 1]; HealthTris[i + 1] = HealthTris[i + 2]; HealthTris[i + 2] = t; }
            HealthPip = hp.ToArray();
            HealthRole = hr.ToArray();
            Pose(new HandPose(0f, 0f, 0f, 0f, 0f, 1f));
        }

        /// Recomputes Verts/Normals for a pose.
        /// The index fingertip in the controller frame with the finger pointing (whatever the current
        /// animation): touch uses it, so the touch point never moves when the finger curls or extends.
        public Vector3 PointingTip()
        {
            if (m_staticVersion != Version) Place();
            var q = new Quaternion[3];
            var j = new Vector3[4];
            FingerChain(0, FingerAngles(0, 0.3f, 1f), q, j);
            return ToController(j[3] + q[2] * new Vector3(0f, Fingers[0].R3.y * TipStretch, 0f));
        }

        public void Pose(HandPose p)
        {
            if (m_staticVersion != Version) Place();
            PoseFinger(0, p.Index, p.Point);
            PoseFinger(1, p.Middle, 0f);
            PoseFinger(2, p.Ring, 0f);
            PoseFinger(3, p.Pinky, 0f);
            PoseThumb(p.Thumb);
        }

        /// Hand frame -> controller frame, plus everything that only depends on it.
        void Place()
        {
            m_staticVersion = Version;
            Vector3 y = DistalAxis.normalized;
            Vector3 z = (DorsalAxis - y * Vector3.Dot(DorsalAxis, y)).normalized;
            Quaternion q = FromBasis(Vector3.Cross(y, z), y, z);
            Vector3 t = WristPos * CM;
            // nudge: turn about the grip point, then shift
            Quaternion qn = Axis(Vector3.up, NudgeEuler.y) * Axis(Vector3.right, NudgeEuler.x) * Axis(Vector3.forward, NudgeEuler.z);
            Vector3 g = GripPos * CM;
            m_q = qn * q;
            m_t = g + qn * (t - g) + NudgePos;
            m_q = AdaptRot * m_q;
            m_t = AdaptTo + AdaptRot * (m_t - AdaptFrom);
            Write(m_palm, Quaternion.identity, Vector3.zero);
            Write(m_cuff, Quaternion.identity, Vector3.zero);
            Write(m_strap, Quaternion.identity, Vector3.zero);
            for (int f = 0; f < 4; f++) Write(m_knuckle[f], Quaternion.identity, KnuckleCentre(f));
            for (int i = 0; i < m_healthLocal.Length; i++) HealthVerts[i] = ToController(m_healthLocal[i]);
            HealthCenter = ToController(m_healthCenterLocal);
            CuffBack = BackPoint(BadgeY, BadgeAng, BackLift, out CuffBackNormal, out CuffDistal);
            StatsBack = BackPoint(StatsY, 0f, BackLift, out StatsNormal, out StatsDistal);
            Vector3 n = m_q * m_healthNormalLocal;
            if (Left) n.x = -n.x;
            HealthNormal = n;
        }

        Vector3 ToControllerDir(Vector3 d)
        {
            Vector3 v = m_q * d;
            if (Left) v.x = -v.x;
            return v;
        }

        Vector3 ToController(Vector3 handCm)
        {
            Vector3 v = m_q * (handCm * CM) + m_t;
            if (Left) v.x = -v.x;
            return v;
        }

        /// Part vertices (part frame, cm) -> controller frame (m), mirrored for the left hand.
        void Write(Part p, Quaternion q, Vector3 tCm)
        {
            Quaternion r = m_q * q;
            Vector3 t = m_q * (tCm * CM) + m_t;
            Vector3[] P = p.P, N = p.N;
            int o = p.Start;
            for (int i = 0; i < P.Length; i++)
            {
                Vector3 v = r * (P[i] * CM) + t;
                Vector3 n = r * N[i];
                if (Left) { v.x = -v.x; n.x = -n.x; }
                Verts[o + i] = v;
                Normals[o + i] = n;
            }
        }

        internal static Ang FingerAngles(int f, float curl, float point)
        {
            curl = Mathf.Clamp(curl, 0f, 2f);
            Ang a = curl <= 1f ? Ang.Lerp(Open[f], Wrap[f], curl) : Ang.Lerp(Wrap[f], Fist[f], curl - 1f);
            if (f == 0 && point > 0f) a = Ang.Lerp(a, PointAng, Mathf.Clamp01(point));
            return a;
        }

        /// Joint frames of a finger (hand frame, cm): rotation and start of each of the three segments,
        /// and the tip of the distal bone.
        internal static void FingerChain(int f, Ang a, Quaternion[] q, Vector3[] j)
        {
            Finger s = Fingers[f];
            Quaternion b = Axis(Vector3.back, a.Spread);          // about -Z: positive spreads radially
            q[0] = b * Axis(Vector3.left, a.Mcp);                  // about -X: flexion towards the palm
            q[1] = q[0] * Axis(Vector3.left, a.Pip);
            q[2] = q[1] * Axis(Vector3.left, a.Dip);
            j[0] = s.Mcp;
            j[1] = j[0] + q[0] * new Vector3(0f, s.L1, 0f);
            j[2] = j[1] + q[1] * new Vector3(0f, s.L2, 0f);
            j[3] = j[2] + q[2] * new Vector3(0f, s.L3, 0f);
        }

        readonly Quaternion[] m_cq = new Quaternion[3];
        readonly Vector3[] m_cj = new Vector3[4];

        void PoseFinger(int f, float curl, float point)
        {
            FingerChain(f, FingerAngles(f, curl, point), m_cq, m_cj);
            for (int k = 0; k < 3; k++) Write(m_fing[f, k], m_cq[k], m_cj[k]);
            if (f == 0)
            {
                IndexTip = ToController(m_cj[3] + m_cq[2] * new Vector3(0f, Fingers[0].R3.y * TipStretch, 0f));
                IndexKnuckle = ToController(m_cj[0]);
                WristInController = ToController(Vector3.zero);
            }
        }

        internal static void ThumbDirs(float t, Vector3[] d)
        {
            t = Mathf.Clamp(t, 0f, 2f);
            for (int k = 0; k < 3; k++)
                d[k] = t <= 1f ? Vector3.Lerp(ThumbLift[k], ThumbRest[k], t).normalized : Vector3.Lerp(ThumbRest[k], ThumbWrap[k], t - 1f).normalized;
        }

        readonly Vector3[] m_td = new Vector3[3];

        void PoseThumb(float t)
        {
            ThumbDirs(t, m_td);
            Vector3 j = ThumbCmc;
            for (int k = 0; k < 3; k++)
            {
                Write(m_thumb[k], FrameY(m_td[k], Vector3.forward), j);
                j += m_td[k] * ThumbLen[k];
            }
        }

        // ---- palm ----------------------------------------------------------------------------------

        // (PalmCapProx: the palm's rounded end inside the cuff, never seen - 0.2 since the short cuff of 2026-10-20, so it stops
        // short of the cuff's closed end; it was 0.8)
        const float PalmY0 = -1.0f, PalmE = 0.6f, PalmCapDist = 0.85f, PalmCapProx = 0.2f;

        /// Distal end of the palm at x: the knuckle (MCP) line, extrapolated past index and pinky.
        static float PalmEndY(float x)
        {
            Finger[] f = Fingers;
            if (x <= f[2].Mcp.x) return Mathf.LerpUnclamped(f[3].Mcp.y, f[2].Mcp.y, (x - f[3].Mcp.x) / (f[2].Mcp.x - f[3].Mcp.x));
            if (x <= f[1].Mcp.x) return Mathf.Lerp(f[2].Mcp.y, f[1].Mcp.y, (x - f[2].Mcp.x) / (f[1].Mcp.x - f[2].Mcp.x));
            return Mathf.Lerp(f[1].Mcp.y, f[0].Mcp.y, Mathf.Clamp01((x - f[1].Mcp.x) / (f[0].Mcp.x - f[1].Mcp.x)));
        }

        /// Palm cross-section at v (0 wrist end .. 1 knuckles): half width a, centre cx, half thickness
        /// b, centre cz.
        static void PalmSection(float v, out float a, out float cx, out float b, out float cz)
        {
            a = 2.95f + 1.4f * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(v / 0.8f));
            cx = Mathf.Lerp(0.15f, -0.45f, v);
            // thinner towards the knuckles, so the finger bases (MCP caps) stand out as knuckles
            b = Mathf.Lerp(1.8f, 1.02f, Mathf.SmoothStep(0f, 1f, v));
            cz = Mathf.Lerp(-0.3f, -0.12f, v);
        }

        static float Spow(float c, float e) => c < 0f ? -Mathf.Pow(-c, e) : Mathf.Pow(c, e);

        static Vector3 PalmPoint(float v, float ang, float capPhi, bool distal)
        {
            float a, cx, b, cz;
            PalmSection(v, out a, out cx, out b, out cz);
            float shrink = 1f - 0.16f * (1f - Mathf.Cos(capPhi));
            float x = cx + a * shrink * Spow(Mathf.Cos(ang), PalmE);
            float z = cz + b * Mathf.Cos(capPhi) * Spow(Mathf.Sin(ang), PalmE);
            float y = distal ? PalmEndY(x) + PalmCapDist * Mathf.Sin(capPhi) : PalmY0 + v * (PalmEndY(x) - PalmY0) - (capPhi > 0f ? PalmCapProx * Mathf.Sin(capPhi) : 0f);
            return new Vector3(x, y, z);
        }

        static Vector3[][] PalmRings()
        {
            var rings = new List<Vector3[]>();
            rings.Add(Ring(PalmAround, ang => PalmPoint(0f, ang, 50f * Mathf.Deg2Rad, false)));
            for (int k = 0; k <= 6; k++)
            {
                float v = k / 6f;
                rings.Add(Ring(PalmAround, ang => PalmPoint(v, ang, 0f, false)));
            }
            rings.Add(Ring(PalmAround, ang => PalmPoint(1f, ang, 35f * Mathf.Deg2Rad, true)));
            rings.Add(Ring(PalmAround, ang => PalmPoint(1f, ang, 65f * Mathf.Deg2Rad, true)));
            return rings.ToArray();
        }

        static Vector3? PalmPoles(bool start)
        {
            float a, cx, b, cz;
            PalmSection(start ? 0f : 1f, out a, out cx, out b, out cz);
            return start ? new Vector3(cx, PalmY0 - PalmCapProx, cz) : new Vector3(cx, PalmEndY(cx) + PalmCapDist, cz);
        }

        /// Height of the back of the hand above (x, y) - the dorsal palm surface - and its normal.
        static Vector3 PalmDorsal(float x, float y, out Vector3 normal)
        {
            Vector3 p = DorsalPoint(x, y);
            Vector3 px = DorsalPoint(x + 0.1f, y) - DorsalPoint(x - 0.1f, y);
            Vector3 py = DorsalPoint(x, y + 0.1f) - DorsalPoint(x, y - 0.1f);
            normal = Vector3.Cross(px, py).normalized;
            if (normal.z < 0f) normal = -normal;
            return p;
        }

        static Vector3 DorsalPoint(float x, float y)
        {
            float v = Mathf.Clamp01((y - PalmY0) / (PalmEndY(x) - PalmY0));
            float a, cx, b, cz;
            PalmSection(v, out a, out cx, out b, out cz);
            float c = Mathf.Clamp((x - cx) / a, -1f, 1f);
            float cosU = Mathf.Pow(Mathf.Abs(c), 1f / PalmE);
            float sinU = Mathf.Sqrt(Mathf.Max(0f, 1f - cosU * cosU));
            return new Vector3(x, y, cz + b * Mathf.Pow(sinU, PalmE));
        }

        /// The head of a finger's metacarpal: a bump on the back of the hand just behind the knuckle
        /// joint, so a fist shows its four knuckles.
        static Vector3 KnuckleCentre(int f) => Fingers[f].Mcp + new Vector3(0f, -0.2f, 0.42f);

        // ---- cuff and strap ------------------------------------------------------------------------
        // (2026-10-20, the user: "the hands in game are too big ... Is there a way to make them the same size of hands in HL:A?";
        // docs/hand-size-hla.md: the hand itself is HL:A's size, but the cuff ran 5.1 cm up the forearm and flared to 8.1 x 5.9 cm,
        // a gauntlet - HL:A's hand ends 1.2 cm behind the wrist. Now the cuff ends CuffBehind behind the wrist joint (its rolled
        // edge CuffRim more: 1.86 cm), straight, 6.9 x 4.84 cm - just round the palm's base; HL:A's wrist is 7.4 x 4.9 cm. The
        // hand, its fit on the controller and the fingertips are unchanged.)

        static readonly Vector3 CuffStart = new Vector3(0.15f, 1.3f, -0.3f);
        static readonly Vector3 CuffDir = new Vector3(0f, -1f, 0.1f);
        const float CuffBehind = 1.6f, CuffRim = 0.26f, CuffLining = 0.3f;
        static readonly Vector2 CuffR = new Vector2(3.45f, 2.42f);
        /// The cuff's length along its axis (cm): from CuffStart on the back of the hand to CuffBehind behind the wrist joint.
        static readonly float CuffLen = (CuffStart.y + CuffBehind) / Mathf.Abs(CuffDir.normalized.y);
        /// How far the glove reaches behind the wrist joint (cm), its rolled edge included (`handsize`).
        public static readonly float BackEndCm = CuffBehind + CuffRim * Mathf.Abs(CuffDir.normalized.y);
        /// The cuff's radius round its back, where the artefact's badge lies (cm, the badge's lift included): FightHud's gap.
        public static float CuffBackRadius => CuffR.x + 0.22f;
        // the wrist strap: cm from the cuff's hand end, and how far it stands out
        const float StrapFrom = 0.75f, StrapTo = 1.65f, StrapGrow = 0.08f;

        static void CuffFrame(out Vector3 dir, out Vector3 ex, out Vector3 ez)
        {
            dir = CuffDir.normalized;
            ez = (Vector3.forward - dir * Vector3.Dot(Vector3.forward, dir)).normalized;
            ex = Vector3.Cross(dir, ez).normalized;
            if (ex.x < 0f) ex = -ex;
        }

        /// A point on the cuff - t from its wrist end (0) toward the elbow (1), `ang` round it (radians; pi/2 = its back, as
        /// CuffBack), `grow` cm above its surface - in the controller frame, as now fitted (FightHud's wrist timer).
        public Vector3 CuffPoint(float t, float ang, float grow)
        {
            Vector3 dir, ex, ez;
            CuffFrame(out dir, out ex, out ez);
            Vector2 r = CuffRadius(t) + new Vector2(grow, grow);
            Vector3 c = CuffStart + dir * (CuffLen * t);
            return ToController(c + ex * (r.x * Mathf.Cos(ang)) + ez * (r.y * Mathf.Sin(ang)));
        }

        static Vector2 CuffRadius(float t) => CuffR;   // straight: no flare (2026-10-20)

        static Vector3[] CuffRing(float t, float grow, float axial)
        {
            Vector3 dir, ex, ez;
            CuffFrame(out dir, out ex, out ez);
            Vector2 r = CuffRadius(t) + new Vector2(grow, grow);
            Vector3 c = CuffStart + dir * (CuffLen * t + axial);
            return Ring(CuffAround, ang => c + ex * (r.x * Mathf.Cos(ang)) + ez * (r.y * Mathf.Sin(ang)));
        }

        static Vector3[][] CuffRings(out float[] u)
        {
            var rings = new List<Vector3[]>();
            var us = new List<float>();
            foreach (float t in new[] { 0f, 0.2f, 0.45f, 0.7f, 0.9f, 1f }) { rings.Add(CuffRing(t, 0f, 0f)); us.Add(UCuff); }
            foreach (float deg in new[] { 40f, 90f, 140f })
            {
                float al = deg * Mathf.Deg2Rad;
                rings.Add(CuffRing(1f, -CuffRim + CuffRim * Mathf.Cos(al), CuffRim * Mathf.Sin(al)));
                us.Add(UTrim);
            }
            // (the lining only a little way in, then the cap: a shallow dark end, not a deep cup - 2026-10-17; CuffLining cm deep,
            // the palm's hidden end stops short of it - PalmCapProx)
            foreach (float t in new[] { 1f, 1f - CuffLining / CuffLen }) { rings.Add(CuffRing(t, -2f * CuffRim, 0f)); us.Add(UInner); }
            u = us.ToArray();
            return rings.ToArray();
        }

        /// The middle of the lining's last ring, a little toward the opening (the cap faces out of the cuff).
        static Vector3 CuffCap()
        {
            Vector3 dir, ex, ez;
            CuffFrame(out dir, out ex, out ez);
            return CuffStart + dir * (CuffLen - CuffLining + 0.1f);
        }

        static Vector3[][] StrapRings()
        {
            float a = StrapFrom / CuffLen, b = (StrapFrom + 0.1f) / CuffLen, c = (StrapTo - 0.1f) / CuffLen, d = StrapTo / CuffLen;
            return new[] { CuffRing(a, 0f, 0f), CuffRing(b, StrapGrow, 0f), CuffRing(c, StrapGrow, 0f), CuffRing(d, 0f, 0f) };
        }

        /// A point on the glove's back (hand frame, cm), y cm along the hand from the wrist joint (+ toward the fingers): on the
        /// cuff up to its hand end (CuffStart.y), `ang` degrees round it from its back (+ toward the thumb side); on the middle of
        /// the back of the hand beyond it; with the surface's outward normal and the way to the fingers along it.
        static Vector3 BackSurface(float y, float ang, out Vector3 normal, out Vector3 distal)
        {
            if (y <= CuffStart.y)
            {
                Vector3 dir, ex, ez;
                CuffFrame(out dir, out ex, out ez);
                float th = (90f - ang) * Mathf.Deg2Rad, c = Mathf.Cos(th), sn = Mathf.Sin(th);
                normal = (ex * (c / CuffR.x) + ez * (sn / CuffR.y)).normalized;
                distal = -dir;
                return CuffStart + dir * ((CuffStart.y - y) / Mathf.Abs(dir.y)) + ex * (CuffR.x * c) + ez * (CuffR.y * sn);
            }
            Vector3 p = PalmDorsal(CuffStart.x, y, out normal), n2;
            Vector3 along = PalmDorsal(CuffStart.x, y + 0.1f, out n2) - PalmDorsal(CuffStart.x, y - 0.1f, out n2);
            distal = (along - normal * Vector3.Dot(along, normal)).normalized;
            return p;
        }

        /// The same in the controller frame, `lift` cm above the surface (the left hand mirrored).
        Vector3 BackPoint(float y, float ang, float lift, out Vector3 normal, out Vector3 distal)
        {
            Vector3 n, d;
            Vector3 p = BackSurface(y, ang, out n, out d);
            normal = ToControllerDir(n).normalized;
            distal = ToControllerDir(d).normalized;
            return ToController(p + n * lift);
        }

        // ---- health plate --------------------------------------------------------------------------

        Vector3 m_healthCenterLocal, m_healthNormalLocal;

        /// An inlay band across the back of the hand with HealthPips diamond gems along a gentle arc.
        Vector3[] HealthPlate(out List<int> tris, out List<int> pip, out List<int> role)
        {
            var p = new List<Vector3>();
            tris = new List<int>();
            pip = new List<int>();
            role = new List<int>();
            const float x0 = -2.95f, x1 = 2.35f, yMid = 4.4f;
            Func<float, float> arcY = x => yMid + 0.05f * (x + 0.3f) * (x + 0.3f);
            Vector3 n;
            m_healthCenterLocal = PalmDorsal(-0.3f, yMid, out n) + n * 0.2f;
            m_healthNormalLocal = n;

            // band: a strip along the arc, slightly above the leather
            const int seg = 16;
            for (int i = 0; i <= seg; i++)
            {
                float x = Mathf.Lerp(x0 - 0.55f, x1 + 0.55f, i / (float)seg);
                Vector3 pos = PalmDorsal(x, arcY(x), out n);
                Vector3 along = (PalmDorsal(x + 0.05f, arcY(x + 0.05f), out n) - PalmDorsal(x - 0.05f, arcY(x - 0.05f), out n)).normalized;
                PalmDorsal(x, arcY(x), out n);
                Vector3 across = Vector3.Cross(n, along).normalized;
                p.Add(pos + n * 0.12f + across * 0.62f); pip.Add(-1); role.Add(0);
                p.Add(pos + n * 0.12f - across * 0.62f); pip.Add(-1); role.Add(0);
                if (i > 0)
                {
                    int b = p.Count - 4;
                    AddTri(tris, p, b, b + 2, b + 1, n);
                    AddTri(tris, p, b + 1, b + 2, b + 3, n);
                }
            }
            // gems: bezel diamond (dark) round a faceted diamond (rim + raised centre)
            for (int g = 0; g < HealthPips; g++)
            {
                float x = Mathf.Lerp(x0, x1, g / (float)(HealthPips - 1));
                Vector3 c = PalmDorsal(x, arcY(x), out n);
                Vector3 along = (PalmDorsal(x + 0.05f, arcY(x + 0.05f), out n) - PalmDorsal(x - 0.05f, arcY(x - 0.05f), out n)).normalized;
                PalmDorsal(x, arcY(x), out n);
                Vector3 across = Vector3.Cross(n, along).normalized;
                Vector3 baseP = c + n * 0.2f;
                float w = 0.27f, h = 0.47f;
                int start = p.Count;
                Vector3[] dirs = { along * w, across * h, -along * w, -across * h };
                for (int k = 0; k < 4; k++) { p.Add(baseP + dirs[k] * 1.4f - n * 0.03f); pip.Add(g); role.Add(1); }   // bezel
                for (int k = 0; k < 4; k++) { p.Add(baseP + dirs[k]); pip.Add(g); role.Add(2); }                     // facet rim
                p.Add(baseP + n * 0.12f); pip.Add(g); role.Add(3);                                                    // centre
                for (int k = 0; k < 4; k++)
                {
                    int k1 = (k + 1) % 4;
                    AddTri(tris, p, start + k, start + k1, start + 4 + k, n);
                    AddTri(tris, p, start + 4 + k, start + k1, start + 4 + k1, n);
                    AddTri(tris, p, start + 4 + k, start + 4 + k1, start + 8, n);
                }
            }
            return p.ToArray();
        }

        static void AddTri(List<int> tris, List<Vector3> p, int a, int b, int c, Vector3 outward)
        {
            Vector3 fn = Vector3.Cross(p[b] - p[a], p[c] - p[a]);
            if (Vector3.Dot(fn, outward) < 0f) { int t = b; b = c; c = t; }
            tris.Add(a); tris.Add(b); tris.Add(c);
        }

        // ---- mesh assembly (once per hand) ----------------------------------------------------------

        static Vector3[] Ring(int n, Func<float, Vector3> f)
        {
            var r = new Vector3[n];
            for (int j = 0; j < n; j++) r[j] = f(2f * Mathf.PI * j / n);
            return r;
        }

        sealed class MeshBuild
        {
            public readonly List<Vector3> P = new List<Vector3>();
            public readonly List<Vector2> Uv = new List<Vector2>();
            public readonly List<int> T = new List<int>();

            /// A tube through the rings (all the same count and winding), optionally closed by a pole
            /// at either end. Normals come from the surface grid and point outwards (judged on the
            /// reference ring `refRing`, then kept consistent along the tube).
            public Part Loft(Vector3[][] rings, Vector3? startPole, Vector3? endPole, float[] ringU, float u, int refRing)
            {
                int R = rings.Length, n = rings[0].Length;
                var part = new Part { Start = P.Count };
                var pos = new List<Vector3>();
                var nrm = new List<Vector3>();
                var uv = new List<Vector2>();
                int ps = -1, pe = -1;
                if (startPole.HasValue) { ps = pos.Count; pos.Add(startPole.Value); nrm.Add(Vector3.zero); uv.Add(new Vector2(ringU != null ? ringU[0] : u, 0.5f)); }
                int ringStart = pos.Count;
                for (int i = 0; i < R; i++)
                    for (int j = 0; j < n; j++) { pos.Add(rings[i][j]); nrm.Add(Vector3.zero); uv.Add(new Vector2(ringU != null ? ringU[i] : u, 0.5f)); }
                if (endPole.HasValue) { pe = pos.Count; pos.Add(endPole.Value); nrm.Add(Vector3.zero); uv.Add(new Vector2(ringU != null ? ringU[R - 1] : u, 0.5f)); }

                // grid normals by central differences
                for (int i = 0; i < R; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        Vector3 du = rings[i][(j + 1) % n] - rings[i][(j + n - 1) % n];
                        Vector3 prev = i > 0 ? rings[i - 1][j] : (startPole.HasValue ? startPole.Value : rings[i][j]);
                        Vector3 next = i < R - 1 ? rings[i + 1][j] : (endPole.HasValue ? endPole.Value : rings[i][j]);
                        nrm[ringStart + i * n + j] = Vector3.Cross(next - prev, du).normalized;
                    }
                }
                // orient: outwards on the reference ring
                Vector3 centre = Vector3.zero;
                for (int j = 0; j < n; j++) centre += rings[refRing][j];
                centre /= n;
                float vote = 0f;
                for (int j = 0; j < n; j++) vote += Vector3.Dot(nrm[ringStart + refRing * n + j], rings[refRing][j] - centre);
                if (vote < 0f) for (int i = 0; i < nrm.Count; i++) nrm[i] = -nrm[i];
                if (ps >= 0) nrm[ps] = (startPole.Value - RingCentre(rings[0])).normalized;
                if (pe >= 0) nrm[pe] = (endPole.Value - RingCentre(rings[R - 1])).normalized;

                int baseIdx = P.Count;
                for (int i = 0; i < R - 1; i++)
                    for (int j = 0; j < n; j++)
                    {
                        int a = ringStart + i * n + j, b = ringStart + i * n + (j + 1) % n;
                        int c = ringStart + (i + 1) * n + (j + 1) % n, d = ringStart + (i + 1) * n + j;
                        Tri(pos, nrm, baseIdx, a, b, c);
                        Tri(pos, nrm, baseIdx, a, c, d);
                    }
                if (ps >= 0)
                    for (int j = 0; j < n; j++) Tri(pos, nrm, baseIdx, ps, ringStart + j, ringStart + (j + 1) % n);
                if (pe >= 0)
                    for (int j = 0; j < n; j++) Tri(pos, nrm, baseIdx, pe, ringStart + (R - 1) * n + (j + 1) % n, ringStart + (R - 1) * n + j);

                P.AddRange(pos);
                Uv.AddRange(uv);
                part.P = pos.ToArray();
                part.N = nrm.ToArray();
                return part;
            }

            static Vector3 RingCentre(Vector3[] r)
            {
                Vector3 c = Vector3.zero;
                foreach (Vector3 v in r) c += v;
                return c / r.Length;
            }

            /// Front faces wind so that their normal agrees with the vertex normals.
            void Tri(List<Vector3> pos, List<Vector3> nrm, int baseIdx, int a, int b, int c)
            {
                Vector3 fn = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
                if (Vector3.Dot(fn, nrm[a] + nrm[b] + nrm[c]) < 0f) { int t = b; b = c; c = t; }
                T.Add(baseIdx + a);
                T.Add(baseIdx + b);
                T.Add(baseIdx + c);
            }

            /// A bone: tapered elliptic tube from y=0 to y=len (width along X, thickness along Z) with
            /// half-ellipsoid ends (X: width, Y and Z: thickness), so neighbouring bones that bend
            /// about X share the joint's surface. `stretch` lengthens the far cap (fingertips).
            public Part Capsule(float len, Vector2 r0, Vector2 r1, int capA, int capB, float stretch, int around, float u)
            {
                var rings = new List<Vector3[]>();
                for (int k = capA; k >= 1; k--)
                {
                    float phi = 0.5f * Mathf.PI * k / (capA + 1);
                    rings.Add(Ellipse(around, r0 * Mathf.Cos(phi), -r0.y * Mathf.Sin(phi)));
                }
                rings.Add(Ellipse(around, r0, 0f));
                rings.Add(Ellipse(around, r1, len));
                for (int k = 1; k <= capB; k++)
                {
                    float phi = 0.5f * Mathf.PI * k / (capB + 1);
                    rings.Add(Ellipse(around, r1 * Mathf.Cos(phi), len + r1.y * stretch * Mathf.Sin(phi)));
                }
                return Loft(rings.ToArray(), new Vector3(0f, -r0.y, 0f), new Vector3(0f, len + r1.y * stretch, 0f), null, u, capA);
            }

            /// A closed ellipsoid round the origin (radii along X, Y, Z), poles on Y.
            public Part Ellipsoid(Vector3 r, int around, int rings, float u)
            {
                var list = new List<Vector3[]>();
                for (int k = 1; k < rings; k++)
                {
                    float phi = Mathf.PI * k / rings - 0.5f * Mathf.PI;
                    list.Add(Ring(around, ang => new Vector3(r.x * Mathf.Cos(phi) * Mathf.Cos(ang), r.y * Mathf.Sin(phi), r.z * Mathf.Cos(phi) * Mathf.Sin(ang))));
                }
                return Loft(list.ToArray(), new Vector3(0f, -r.y, 0f), new Vector3(0f, r.y, 0f), null, u, list.Count / 2);
            }

            static Vector3[] Ellipse(int n, Vector2 r, float y)
            {
                return Ring(n, ang => new Vector3(r.x * Mathf.Cos(ang), y, r.y * Mathf.Sin(ang)));
            }
        }

        // ---- where a weapon is held ---------------------------------------------------------------

        /// Distance along `axis` (unit, pointing from the hilt end towards the tip) from the model
        /// origin to the middle of a weapon's grip, from its mesh in model space (triangles optional:
        /// they fill in long faces). Sword-like weapons: the guard is the widest slice within 8-40% of
        /// the length; the grip runs down from where the guard has narrowed to where a pommel wider
        /// than the grip ends. The game's swords and daggers have their origin at the pommel end (the
        /// Rusted Sword's grip middle is 0.18 m up the blade); its axes, maces and hammers already
        /// have it up the haft where a hand goes (returns 0 = keep); a guardless haft that starts at
        /// the origin is held 10 cm up. NaN for too few points.
        internal static Action<float, float, float> GripTrace;   // offline harness: slice, width, wall

        internal static float GripAlong(IList<Vector3> pts, IList<int> tris, Vector3 axis)
        {
            if (pts == null || pts.Count < 8) return float.NaN;
            axis = axis.normalized;
            Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 w = Vector3.Cross(axis, u);
            float t0 = float.MaxValue, t1 = float.MinValue;
            for (int i = 0; i < pts.Count; i++)
            {
                float t = Vector3.Dot(pts[i], axis);
                if (t < t0) t0 = t;
                if (t > t1) t1 = t;
            }
            float len = t1 - t0;
            if (len < 0.15f) return float.NaN;
            const int Bins = 64;
            float binLen = len / Bins;
            var lo = new Vector2[Bins];
            var hi = new Vector2[Bins];
            var width = new float[Bins];   // < 0: nothing in this slice
            for (int b = 0; b < Bins; b++) width[b] = -1f;
            Action<Vector3> add = p =>
            {
                int b = Mathf.Clamp((int)((Vector3.Dot(p, axis) - t0) / binLen), 0, Bins - 1);
                var q = new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, w));
                if (width[b] < 0f) { lo[b] = hi[b] = q; }
                else { lo[b] = Vector2.Min(lo[b], q); hi[b] = Vector2.Max(hi[b], q); }
                width[b] = Mathf.Max(hi[b].x - lo[b].x, hi[b].y - lo[b].y);
            };
            for (int i = 0; i < pts.Count; i++) add(pts[i]);
            if (tris != null)
                for (int i = 0; i + 2 < tris.Count; i += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        Vector3 a = pts[tris[i + e]], c = pts[tris[i + (e + 1) % 3]];
                        int steps = Mathf.Min(64, (int)(Mathf.Abs(Vector3.Dot(c - a, axis)) / (0.5f * binLen)));
                        for (int k = 1; k < steps; k++) add(Vector3.Lerp(a, c, k / (float)steps));
                    }

            // The model reaches well below its origin: the origin is already up a haft, where the
            // game's hand goes (axes, maces, hammers) - keep it.
            if (t0 < -0.05f) return 0f;
            // Walk up from the hilt end. A slice at least 1.6x wider than the narrowest one since the
            // last such "wall" is a wall: after a short run it was a pommel (start over), after a run
            // at least a hand long it is the guard - the fist goes right under it (index finger at
            // the guard), as a sword is held.
            int half = Bins / 2, runStart = -1;
            float runMin = float.MaxValue;
            for (int b = 0; b <= half; b++)
            {
                float wb = width[b];
                if (wb < 0f) continue;
                if (GripTrace != null) GripTrace(t0 + (b + 0.5f) * binLen, wb, 1.6f * runMin);
                if (runStart >= 0 && wb >= 1.6f * runMin)
                {
                    if ((b - runStart) * binLen >= 0.08f)
                        return Mathf.Max(t0 + b * binLen - 0.06f, t0 + runStart * binLen + 0.04f);
                    runStart = -1;
                    runMin = float.MaxValue;
                    continue;
                }
                if (runStart < 0) runStart = b;
                runMin = Mathf.Min(runMin, wb);
            }
            return t0 + 0.10f;   // no guard: a haft starting at the origin, held 10 cm up
        }

        // ---- managed rotation helpers (Unity's own are engine calls) --------------------------------

        internal static Quaternion Axis(Vector3 axis, float deg)
        {
            float h = 0.5f * deg * Mathf.Deg2Rad, s = Mathf.Sin(h);
            axis = axis.normalized;
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, Mathf.Cos(h));
        }

        /// Rotation whose local X, Y, Z axes are the given orthonormal vectors.
        internal static Quaternion FromBasis(Vector3 x, Vector3 y, Vector3 z)
        {
            float m00 = x.x, m10 = x.y, m20 = x.z, m01 = y.x, m11 = y.y, m21 = y.z, m02 = z.x, m12 = z.y, m22 = z.z;
            float tr = m00 + m11 + m22;
            if (tr > 0f)
            {
                float s = Mathf.Sqrt(tr + 1f) * 2f;
                return new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            if (m00 > m11 && m00 > m22)
            {
                float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
                return new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            if (m11 > m22)
            {
                float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
                return new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            float s2 = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
            return new Quaternion((m02 + m20) / s2, (m12 + m21) / s2, 0.25f * s2, (m10 - m01) / s2);
        }

        /// A rotation taking +Y to `y` (roll from `hint`).
        internal static Quaternion FrameY(Vector3 y, Vector3 hint)
        {
            y = y.normalized;
            Vector3 x = Vector3.Cross(y, hint);
            if (x.sqrMagnitude < 1e-6f) x = Vector3.Cross(y, Vector3.right);
            x = x.normalized;
            return FromBasis(x, y, Vector3.Cross(x, y));
        }
    }
}

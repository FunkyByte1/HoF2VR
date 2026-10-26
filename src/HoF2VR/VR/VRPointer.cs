using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Laser pointer for menus and the card table. It drives the game's own NGUI picking:
    ///  * UICamera.Raycast: per UI camera, ScreenToViewportPoint/ScreenPointToRay are redirected — the
    ///    2D UI camera gets the pixel where the laser crosses the UI panel, 3D UI cameras get the
    ///    controller's world ray. NGUI's depth order then gives "panel first, table second".
    ///  * UICamera.ProcessMouse: cursor-visible check and mouse position come from the laser (hover).
    ///  * The game stays in Controller mode (A/B/X/Y glyphs match Touch); the trigger forces the hover
    ///    onto the laser's target and presses A (Select).
    internal class VRPointer : MonoBehaviour
    {
        public static VRPointer I;
        /// The laser currently drives NGUI hover (trigger pulled; cleared by stick/button navigation).
        public static bool Active = true;
        public static float PitchOffset = 0f;
        /// A mini-game owns the triggers right now (e.g. drawing the dice back).
        public static bool SuppressConfirm;

        static Hand s_hand = VRInput.R;
        static Ray s_ray;
        static bool s_onPanel, s_tracked;
        static Vector2 s_px;
        static float s_panelDist;
        static bool s_down, s_held, s_up;
        static int s_frame = -1;

        LineRenderer m_line;
        Transform m_dot;

        // test seam: aim at a panel pixel or a world point for a few frames and click
        static Vector2 s_ovPx;
        static Vector3 s_ovWorld;
        static bool s_ovIsWorld, s_ovClick;
        static int s_ovFrames;

        public static void AimPanel(Vector2 px, bool click) { s_ovPx = px; s_ovIsWorld = false; s_ovClick = click; s_ovFrames = 24; Active = true; }
        public static void AimWorld(Vector3 p, bool click) { s_ovWorld = p; s_ovIsWorld = true; s_ovClick = click; s_ovFrames = 24; Active = true; }

        public static void Apply(Harmony h)
        {
            MethodInfo raycast = AccessTools.Method(typeof(UICamera), "Raycast", new[] { typeof(Vector3) });
            h.Patch(raycast, transpiler: new HarmonyMethod(typeof(VRPointer), nameof(RaycastTranspiler)));
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(UICamera)))
            {
                if (m.GetMethodBody() == null || m == raycast) continue;
                bool uses = false;
                foreach (var ins in PatchProcessor.ReadMethodBody(m))
                {
                    var mi = ins.Value as MethodInfo;
                    if (mi != null && (mi.DeclaringType == typeof(Input) && (mi.Name == "get_mousePosition" || mi.Name.StartsWith("GetMouseButton")) ||
                                       mi.DeclaringType == typeof(Cursor) && mi.Name == "get_visible")) { uses = true; break; }
                }
                if (uses) h.Patch(m, transpiler: new HarmonyMethod(typeof(VRPointer), nameof(MouseTranspiler)));
            }
        }

        static IEnumerable<CodeInstruction> RaycastTranspiler(IEnumerable<CodeInstruction> code)
        {
            MethodInfo toViewport = AccessTools.Method(typeof(Camera), "ScreenToViewportPoint", new[] { typeof(Vector3) });
            MethodInfo toRay = AccessTools.Method(typeof(Camera), "ScreenPointToRay", new[] { typeof(Vector3) });
            foreach (CodeInstruction ci in code)
            {
                if (ci.operand is MethodInfo mi && (mi == toViewport || mi == toRay))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(VRPointer), mi == toViewport ? nameof(ViewportFor) : nameof(RayFor));
                }
                yield return ci;
            }
        }

        static IEnumerable<CodeInstruction> MouseTranspiler(IEnumerable<CodeInstruction> code)
        {
            foreach (CodeInstruction ci in code)
            {
                if (ci.operand is MethodInfo mi)
                {
                    if (mi.DeclaringType == typeof(Input))
                    {
                        if (mi.Name == "get_mousePosition") ci.operand = AccessTools.Method(typeof(VRPointer), nameof(MousePosition));
                    }
                    else if (mi.DeclaringType == typeof(Cursor) && mi.Name == "get_visible")
                        ci.operand = AccessTools.Method(typeof(VRPointer), nameof(CursorVisible));
                }
                yield return ci;
            }
        }

        /// The laser drives NGUI's hover/clicks (and is drawn). Off unless Settings Controls.Lasers (user 2026-10-11: "I don't
        /// like the lasers coming from the controllers. Can we have touch (or the A button where necessary) as the only
        /// form of selecting things?").
        static bool Driving => (Settings.Lasers || s_ovFrames > 0) && Active && s_tracked && XRSettings.enabled && !GameWatch.InCombatPlay;   // (test seams aim it anyway)

        static bool IsUiCamera(Camera cam) => VRUI.I != null && cam.targetTexture != null && cam.targetTexture == VRUI.I.RT;

        public static Vector3 ViewportFor(Camera cam, Vector3 inPos)
        {
            if (!Driving) return cam.ScreenToViewportPoint(inPos);
            if (IsUiCamera(cam)) return s_onPanel ? new Vector3(s_px.x / VRUI.Width, s_px.y / VRUI.Height, 0f) : new Vector3(-1f, -1f, 0f);
            return new Vector3(0.5f, 0.5f, 0f);
        }

        public static Ray RayFor(Camera cam, Vector3 inPos)
        {
            if (!Driving) return cam.ScreenPointToRay(inPos);
            if (IsUiCamera(cam)) return cam.ScreenPointToRay(new Vector3(s_px.x, s_px.y, 0f));
            return s_ray;
        }

        public static Vector3 MousePosition()
        {
            if (!Driving) return Input.mousePosition;
            if (s_onPanel) return new Vector3(s_px.x, s_px.y, 0f);
            Camera c = VRRig.I != null ? VRRig.I.PrimaryCamera : null;
            return c != null ? c.WorldToScreenPoint(s_ray.GetPoint(2f)) : Input.mousePosition;
        }

        public static bool CursorVisible() => Driving || Cursor.visible;
        /// The selectable the laser means: the hit object's own UISelectable, or - when that one has no
        /// action of its own (a card lying on a map tile, a label on a button) - the nearest ancestor
        /// that is selectable and has one (the tile, the button).
        internal static UISelectable PickTarget(GameObject hit) { bool blocked; return PickTarget(hit, out blocked); }

        /// `blocked`: the laser is on something whose action is currently unavailable (a map tile the
        /// hero can't reach) - the trigger should do nothing then, as a mouse click would.
        internal static UISelectable PickTarget(GameObject hit, out bool blocked)
        {
            blocked = false;
            if (hit == null) return null;
            UISelectable first = null;
            for (Transform t = hit.transform; t != null; t = t.parent)
            {
                UISelectable sel = UISelectable.GetTargetSelectable(t.GetComponent<UISelectable>());
                if (sel == null || !sel.IsValid) continue;
                if (first == null) first = sel;
                if (sel.HasSelectAction)
                {
                    if (sel.IsSelectable) return sel;
                    blocked = true;
                }
            }
            return first;
        }

        static UISelectable s_hoverTarget;

        /// The game's selection follows the laser, like mouse hover in the flat game.
        static void HoverFollow()
        {
            UISelectable target = PickTarget(UICamera.RayHitObject);
            if (target == s_hoverTarget) return;
            s_hoverTarget = target;
            // a light tick when the laser comes onto something the game lets it hover
            if (target != null && !SuppressConfirm && UISelection.TryHover(target)) Haptics.Event("hover", s_hand.IsLeft, 0.12f, 0.012f);
        }

        /// Trigger = "select what the laser points at": force the game's hover onto that selectable
        /// (a screen may have auto-hovered its default button meanwhile), then press A on the pad.
        static void Click()
        {
            if (SuppressConfirm) return;
            GameObject hit = UICamera.RayHitObject;
            bool blocked;
            UISelectable target = PickTarget(hit, out blocked);
            if (blocked && (target == null || !target.HasSelectAction || !target.IsSelectable))
            {
                Plugin.L.LogInfo("pointer: " + (hit ? hit.name : "-") + " is not selectable right now");
                Haptics.Event("refused", s_hand.IsLeft, 0.25f, 0.02f, 2, 0.06f);
                return;
            }
            if (target == null)
            {
                // Pointing at nothing: the trigger confirms whatever the game has selected (stops the
                // pendulum / wheel, throws the dice, continues dialogue) - same as pressing A.
                VRPad.Press("A", 4);
                Haptics.Event("confirm", s_hand.IsLeft, 0.3f, 0.02f);
                Plugin.L.LogInfo("pointer: confirm (no target under the laser: " + (hit ? hit.name : "-") + ")");
                return;
            }
            bool hovered = UISelection.TryHover(target);
            VRPad.Press("A", 4);
            Haptics.Event("select", s_hand.IsLeft, 0.5f, 0.03f);
            Plugin.L.LogInfo("pointer: select " + VRUI.Path(target.transform) + (hovered ? "" : " (hover refused)"));
        }

        void Awake()
        {
            I = this;
            Diag.Commands["pointer"] = a =>
            {
                if (a.Length > 1 && a[1] == "pitch") PitchOffset = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                if (a.Length > 1 && a[1] == "on") Active = true;
                if (a.Length > 1 && a[1] == "off") Active = false;
                return string.Format("active={0} tracked={1} onPanel={2} px={3} hand={4} hit={5}", Active, s_tracked, s_onPanel, s_px,
                    s_hand.IsLeft ? "L" : "R", UICamera.RayHitObject ? VRUI.Path(UICamera.RayHitObject.transform) : "-");
            };
            Diag.StatusExtras["pointer"] = () => Diag.Q(Driving ? (s_onPanel ? "panel " + s_px : "world") +
                " hit=" + (UICamera.RayHitObject ? UICamera.RayHitObject.name : "-") : "off");
        }

        void Update()
        {
            if (!XRSettings.enabled || VRRig.I == null || VRRig.I.PrimaryCamera == null) { s_tracked = false; Show(false); return; }
            if (s_frame == Time.frameCount) return;
            s_frame = Time.frameCount;
            VRInput.Update();
            // (a trigger at the hero's figurine picks it up: not the laser's - FigurineGrab)
            bool lTrig = VRInput.L.TriggerDown && !FigurineGrab.Claims(VRInput.L), rTrig = VRInput.R.TriggerDown && !FigurineGrab.Claims(VRInput.R);
            if (lTrig) s_hand = VRInput.L;
            else if (rTrig) s_hand = VRInput.R;
            if (lTrig || rTrig) Active = true;

            Hand h = s_hand;
            s_tracked = h.Tracked;
            s_down = h.TriggerDown && !FigurineGrab.Claims(h);
            s_held = h.TriggerBtn;
            s_up = h.TriggerUp;
            // the laser leaves the controller's pointer tip, aimed the way the hand points
            Vector3 origin = VRRig.I.ToWorld(h.Pos + h.Rot * ControllerGeometry.TipPos(h.IsLeft));
            Quaternion rot = VRRig.I.ToWorld(h.Rot) * Quaternion.Euler(ControllerGeometry.TipPitchDown + PitchOffset, 0f, 0f);
            s_ray = new Ray(origin, rot * Vector3.forward);
            if (s_ovFrames > 0)
            {
                s_ovFrames--;
                Vector3 target = s_ovWorld;
                if (!s_ovIsWorld && (TableBoards.I == null || !TableBoards.I.WorldForPixel(s_ovPx, out target))) target = VRUI.I.PanelPixelToWorld(s_ovPx);
                s_ray = new Ray(origin, (target - origin).normalized);
                s_tracked = true;
                // hover for a while, then press for 3 frames and release (NGUI raycasts every 20 ms)
                s_held = s_ovClick && s_ovFrames <= 12 && s_ovFrames > 8;
                s_down = s_ovClick && s_ovFrames == 12;
                s_up = s_ovClick && s_ovFrames == 8;
            }
            s_onPanel = VRUI.I != null && VRUI.I.HitPanel(s_ray, out s_px, out s_panelDist);
            Show(Driving);
            if (Driving)
            {
                Draw();
                HoverFollow();
                if (s_down) Click();
            }
            else s_hoverTarget = null;
        }

        void Show(bool on)
        {
            if (m_line == null)
            {
                if (!on) return;
                Create();
            }
            if (m_line.gameObject.activeSelf != on) m_line.gameObject.SetActive(on);
            if (m_dot.gameObject.activeSelf != on) m_dot.gameObject.SetActive(on);
        }

        void Create()
        {
            var go = new GameObject("HoF2VR_Laser") { layer = VRUI.Layer };
            DontDestroyOnLoad(go);
            m_line = go.AddComponent<LineRenderer>();
            m_line.useWorldSpace = true;
            m_line.positionCount = 2;
            m_line.widthMultiplier = 0.004f;
            m_line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_line.receiveShadows = false;
            var mat = new Material(Shader.Find("Sprites/Default")) { renderQueue = 5110 };
            m_line.sharedMaterial = mat;
            m_line.startColor = new Color(1f, 0.85f, 0.45f, 0.9f);
            m_line.endColor = new Color(1f, 0.85f, 0.45f, 0.15f);

            GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(dot.GetComponent<Collider>());
            dot.name = "HoF2VR_LaserDot";
            dot.layer = VRUI.Layer;
            DontDestroyOnLoad(dot);
            dot.GetComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = new Color(1f, 0.9f, 0.5f), renderQueue = 5120 };
            m_dot = dot.transform;
        }

        void Draw()
        {
            float dist = 3f;
            GameObject hit = UICamera.RayHitObject;
            bool hit2D = hit != null && s_onPanel && hit.layer == 17;
            if (hit2D) dist = s_panelDist;
            else if (hit != null && UICamera.lastHit.collider != null) dist = Vector3.Distance(s_ray.origin, UICamera.lastWorldPosition);
            else if (s_onPanel) dist = s_panelDist;
            dist = Mathf.Clamp(dist, 0.05f, 20f * (VRRig.I != null ? VRRig.I.Scale : 1f));
            Vector3 end = s_ray.GetPoint(dist);
            m_line.SetPosition(0, s_ray.origin);
            m_line.SetPosition(1, end);
            m_dot.position = end;
            float s = VRRig.I != null ? VRRig.I.Scale : 1f;
            m_line.widthMultiplier = 0.004f * s;
            m_dot.localScale = Vector3.one * (Mathf.Lerp(0.008f, 0.03f, dist / (5f * s)) * s);
        }
    }
}

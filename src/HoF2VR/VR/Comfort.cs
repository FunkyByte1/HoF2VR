using UnityEngine;

namespace HoF2VR
{
    /// Comfort vignette: a dark tunnel round the edge of the view while the view moves without the
    /// player's own body moving. Three causes, each with its own strength setting; the strongest wins:
    ///  * Vignette: the game moves the view fast (dodges, attack lunges);
    ///  * WalkVignette: the player walks with the left stick;
    ///  * TurnVignette: the player turns smoothly with the right stick.
    /// The strength sets how far the dark edge closes in (0: none, 1: a narrow tunnel). It is a ring
    /// mesh drawn by the overlay camera far in front of the head, so both eyes see the same tunnel
    /// (a ring a few centimetres from the eyes looked different to each eye).
    internal class Comfort : MonoBehaviour
    {
        public static Comfort I;
        Transform m_ring;
        Mesh m_mesh;
        Material m_mat;
        Vector3[] m_verts;
        float m_strength, m_builtFor = -1f;
        Vector3 m_lastBody;
        bool m_hasLast;
        string m_cause = "";
        int m_placedFrame = -1;

        /// The tunnel's strength now and why (the turn log reads them).
        public float Strength => m_strength;
        public string Cause => m_cause;
        /// Smooth-turn rate this frame (degrees per second, set by CombatVR; 0 when not turning).
        public static float TurnRate;
        /// The Dash (CombatMove, DashStyle Slide): the tunnel at Vignette strength at once, until this time (unscaled).
        public static float DashUntil;
        public static float DashPeak;   // diagnostics: the strongest tunnel during the last dash
        public static float Peak;   // diagnostics: strongest vignette since the last status read
        public static string PeakCause = "";
        /// Test override (`vignette <0-1>|off`): show this strength regardless of motion.
        static float s_force = -1f;

        const int Segments = 48;
        const float Distance = 5f;          // world units in front of the eyes (far: no disparity)
        const float Feather = 14f;          // degrees from clear to dark
        const float OpenAngle = 52f, ClosedAngle = 16f;   // clear half-angle at strength 0+ / 1 (the eye sees ~45-50 deg)
        const float SkirtAngle = 84f;       // the dark part reaches beyond the headset's field of view

        void Awake()
        {
            I = this;
            Diag.StatusExtras["vignette"] = () =>
            {
                string v = Diag.Q(m_strength.ToString("0.00") + " " + m_cause + " peak " + Peak.ToString("0.00") + " " + PeakCause);
                Peak = 0f;
                PeakCause = "";
                return v;
            };
            Diag.Commands["vignette"] = a =>
            {
                if (a.Length > 1) s_force = a[1] == "off" ? -1f : Mathf.Clamp01(float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture));
                string r = "-";
                if (m_ring != null)
                {
                    MeshRenderer mr = m_ring.GetComponent<MeshRenderer>();
                    Camera ov = VRUI.I != null ? VRUI.I.Overlay : null;
                    r = "active=" + m_ring.gameObject.activeInHierarchy + " layer=" + m_ring.gameObject.layer + " at " + Diag.V(m_ring.position) +
                        " overlay " + (ov != null ? Diag.V(ov.transform.position) + " mask 0x" + ov.cullingMask.ToString("X") : "-") +
                        " visible=" + (mr != null && mr.isVisible) + " bounds " + (mr != null ? Diag.V(mr.bounds.center) + " " + Diag.V(mr.bounds.size) : "-") +
                        " alpha " + m_mat.color.a.ToString("0.00") + " verts " + m_mesh.vertexCount + " placed f" + m_placedFrame + " now f" + Time.frameCount;
                }
                return "vignette " + m_strength.ToString("0.00") + " (" + m_cause + ")" + (s_force >= 0f ? " forced " + s_force : "") +
                       " clear half-angle " + ClearAngle(m_strength).ToString("0") + " deg | " + r;
            };
        }

        /// Half-angle of the clear middle of the view for a strength.
        static float ClearAngle(float k) => Mathf.Lerp(OpenAngle, ClosedAngle, Mathf.Clamp01(k));

        void Create()
        {
            // three rings (clear edge, dark edge, far skirt) of Segments vertices each
            m_verts = new Vector3[Segments * 3];
            var colors = new Color32[Segments * 3];
            var tris = new int[Segments * 2 * 6];
            int t = 0;
            for (int i = 0; i < Segments; i++)
            {
                colors[i] = new Color32(0, 0, 0, 0);
                colors[Segments + i] = new Color32(0, 0, 0, 255);
                colors[2 * Segments + i] = new Color32(0, 0, 0, 255);
                int j = (i + 1) % Segments;
                for (int ring = 0; ring < 2; ring++)
                {
                    int a = ring * Segments + i, b = ring * Segments + j, c = (ring + 1) * Segments + i, d = (ring + 1) * Segments + j;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
            }
            m_mesh = new Mesh { name = "HoF2VR_Vignette" };
            m_mesh.MarkDynamic();
            Shape(0f);
            m_mesh.vertices = m_verts;
            m_mesh.colors32 = colors;
            m_mesh.triangles = tris;
            m_mesh.bounds = new Bounds(new Vector3(0f, 0f, Distance), new Vector3(200f, 200f, 1f));
            var go = new GameObject("HoF2VR_Vignette") { layer = VRUI.Layer };
            DontDestroyOnLoad(go);
            go.AddComponent<MeshFilter>().sharedMesh = m_mesh;
            var r = go.AddComponent<MeshRenderer>();
            // vertex colours carry the shape; the material colour's alpha fades the whole tunnel in.
            // (Sprites/Default drew this custom mesh almost transparent: its per-sprite renderer colour
            // is not set for a plain MeshRenderer.)
            m_mat = new Material(Shader.Find("Hidden/Internal-Colored")) { name = "HoF2VR_Vignette", renderQueue = 4900, color = new Color(1, 1, 1, 0) };
            m_mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m_mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m_mat.SetInt("_ZWrite", 0);
            m_mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            m_mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            r.sharedMaterial = m_mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            m_ring = go.transform;
            go.SetActive(false);
        }

        static readonly float[] s_angles = new float[3];

        /// Ring vertices for a strength (head space: +Z ahead).
        void Shape(float k)
        {
            float clear = ClearAngle(k);
            float[] angles = s_angles;   // (reused: this runs every frame the tunnel moves)
            angles[0] = clear;
            angles[1] = Mathf.Min(clear + Feather, SkirtAngle - 1f);
            angles[2] = SkirtAngle;
            for (int ring = 0; ring < 3; ring++)
            {
                float r = Distance * Mathf.Tan(angles[ring] * Mathf.Deg2Rad);
                for (int i = 0; i < Segments; i++)
                {
                    float phi = i * Mathf.PI * 2f / Segments;
                    m_verts[ring * Segments + i] = new Vector3(Mathf.Cos(phi) * r, Mathf.Sin(phi) * r, Distance);
                }
            }
        }

        void LateUpdate()
        {
            if (m_ring == null) Create();
            float fast = 0f, walk = 0f, turn = 0f;
            if (CombatVR.Active && PlayerController.Instance != null)
            {
                // what the player's view does, not the hero (the view ignores moves the game makes)
                Vector3 body = CombatVR.I != null ? CombatVR.I.ViewFloor : PlayerController.Instance.transform.position;
                float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-3f);
                if (m_hasLast)
                {
                    Vector3 v = body - m_lastBody;
                    v.y = 0f;
                    float speed = v.magnitude / dt;
                    // walking/running is ~2-5 m/s in this game; dodges and lunges go well beyond
                    fast = Mathf.Clamp01((speed - 5.5f) / 4f) * Settings.Vignette;
                    // walking with the stick: the view glides while the body stands still
                    if (VRInput.L.Stick.sqrMagnitude > 0.04f && speed > 0.25f)
                        walk = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 1.5f, speed)) * Settings.WalkVignette;
                }
                m_lastBody = body;
                m_hasLast = true;
                if (Mathf.Abs(TurnRate) > 1f)
                    turn = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Abs(TurnRate) / 90f)) * Settings.TurnVignette;
            }
            else m_hasLast = false;
            float target = Mathf.Max(fast, Mathf.Max(walk, turn));
            string cause = target <= 0f ? "" : target == fast ? "fast" : target == walk ? "walk" : "turn";
            if (Time.unscaledTime < DashUntil && Settings.Vignette > 0f)
            {
                target = Mathf.Max(target, Settings.Vignette);
                m_strength = Mathf.Max(m_strength, Settings.Vignette);   // at once: the dash is only 0.15 s
                cause = "dash";
                DashPeak = Mathf.Max(DashPeak, m_strength);
            }
            if (s_force >= 0f) { target = s_force; cause = "forced"; }
            if (cause.Length > 0) m_cause = cause;
            // closes in quickly, opens a little slower
            m_strength = Mathf.MoveTowards(m_strength, target, Time.unscaledDeltaTime * (target > m_strength ? 6f : 2.5f));
            if (m_strength <= 0f) m_cause = "";
            if (m_strength > Peak) { Peak = m_strength; PeakCause = m_cause; }
            bool on = m_strength > 0.005f;
            if (on && Mathf.Abs(m_strength - m_builtFor) > 0.003f)
            {
                Shape(m_strength);
                m_mesh.vertices = m_verts;
                m_builtFor = m_strength;
            }
            // a very weak tunnel fades in rather than appearing at full size
            m_mat.color = new Color(1, 1, 1, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(m_strength / 0.15f)));
            if (m_ring.gameObject.activeSelf != on) m_ring.gameObject.SetActive(on);
        }

        /// Called by the overlay camera before culling: centre the tunnel on the eyes.
        public void Place(Transform head)
        {
            if (m_ring == null || !m_ring.gameObject.activeSelf) return;
            m_placedFrame = Time.frameCount;
            m_ring.position = head.position;
            m_ring.rotation = head.rotation;
            m_ring.localScale = Vector3.one;
        }
    }
}

using UnityEngine;

namespace HoF2VR
{
    /// Until You Fall's screen flash (docs/uyf-reference.md §3.12, ScreenFlashController; combat Stage 2): a colour over the
    /// whole view that fades out. UYF's table (colour, alpha, duration): guard hit Standard white 0.27 / 0.25 s, Big white 0.39
    /// / 0.25 s, Under none; crit Standard or Big pale yellow (1, 1, 0.61) 0.47 / 0.5 s, crit Under 0.14 / 0.25 s; a blocked
    /// shot white 0.2 / 0.15 s; a ranged hit taken red 0.15 / 0.3 s.
    /// `[Combat] ScreenFlash` (on) switches the white and yellow ones, `[Combat] HitTakenFlash` (off: the user disliked a red
    /// tint at the edge of the view on 2026-10-11) the red one. Drawn by the overlay camera after the world (a quad just in
    /// front of the eyes, like the comfort fade), under the comfort vignette and the fade.
    internal class ScreenFlash : MonoBehaviour
    {
        public static ScreenFlash I;
        public static int Flashes;
        public static float LastAlpha;
        public static string Last = "-";
        public static readonly Color White = Color.white, PaleYellow = new Color(1f, 1f, 0.61f), Red = new Color(1f, 0.1f, 0.08f);

        Material m_mat;
        Transform m_quad;
        Color m_col;
        float m_alpha, m_start = -10f, m_dur = 0.25f;

        void Awake() { I = this; }

        /// A flash of `colour` at `alpha`, fading out over `seconds`. `red` = a hit taken (its own setting).
        public static void Show(Color colour, float alpha, float seconds, string why, bool red = false)
        {
            if (I == null || alpha <= 0f || seconds <= 0f || !CombatSettings.Physical) return;
            if (red ? !CombatSettings.HitTakenFlash : !CombatSettings.ScreenFlash) return;
            // a weaker flash does not cut a stronger one short
            if (Time.unscaledTime - I.m_start < I.m_dur && I.Current() > alpha) return;
            I.m_col = colour;
            I.m_alpha = alpha;
            I.m_start = Time.unscaledTime;
            I.m_dur = seconds;
            Flashes++;
            LastAlpha = alpha;
            Last = why + " " + alpha.ToString("0.00") + "/" + seconds.ToString("0.00") + " s";
        }

        float Current()
        {
            float k = Mathf.Clamp01((Time.unscaledTime - m_start) / m_dur);
            return m_alpha * (1f - k) * (1f - k);
        }

        void Create()
        {
            var go = new GameObject("HoF2VR_ScreenFlash") { layer = VRUI.Layer };
            DontDestroyOnLoad(go);
            go.AddComponent<MeshFilter>().sharedMesh = CombatFeel.WhiteQuad;
            var r = go.AddComponent<MeshRenderer>();
            m_mat = new Material(Shader.Find("Hidden/Internal-Colored")) { name = "HoF2VR_ScreenFlash", renderQueue = 4800, color = new Color(1, 1, 1, 0) };
            m_mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m_mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m_mat.SetInt("_ZWrite", 0);
            m_mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            m_mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            r.sharedMaterial = m_mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            m_quad = go.transform;
            go.SetActive(false);
        }

        void LateUpdate()
        {
            if (m_quad == null) Create();
            float a = Time.unscaledTime - m_start < m_dur ? Current() : 0f;
            bool on = a > 0.003f && UnityEngine.XR.XRSettings.enabled;
            if (on) m_mat.color = new Color(m_col.r, m_col.g, m_col.b, a);
            if (m_quad.gameObject.activeSelf != on) m_quad.gameObject.SetActive(on);
        }

        /// Called by the overlay camera before culling: just in front of the eyes, wide enough for both of them.
        public void Place(Transform head)
        {
            if (m_quad == null || !m_quad.gameObject.activeSelf) return;
            float s = VRRig.I != null ? VRRig.I.Scale : 1f;
            m_quad.position = head.position + head.forward * (0.1f * s);
            m_quad.rotation = head.rotation;
            m_quad.localScale = new Vector3(2f * s, 2f * s, 1f);
        }
    }
}

using System;
using UnityEngine;
using UnityEngine.AI;

namespace HoF2VR
{
    /// Until You Fall's dash landing marker (its PlayerDash `_dashTargeting`, on by default; docs/uyf-parity-audit.md P24):
    /// a ring on the floor where the dash lands - while the right stick leans forward or back short of a flick (where a
    /// flick would take you: on the enemy it would home on, else 6 m ahead or 3 m back, stopped by walls and the arena's
    /// edge), and during a dash until a moment after it. Our own procedural ring (Premonition's strokes), in the indicator
    /// gold; UYF dash only.
    internal class DashMarker : MonoBehaviour
    {
        static GameObject s_go;
        static Mesh s_mesh;
        static readonly Premonition.Builder s_b = new Premonition.Builder();
        static Vector3 s_at;
        static float s_until = -1f;
        static bool s_preview;
        public static int Shown, Previews;
        const float Radius = 0.35f;

        /// The ring at a dash's end, for `seconds`.
        public static void Show(Vector3 at, float seconds)
        {
            if (!CombatSettings.UyfDash) return;
            s_at = Floor(at);
            s_until = Time.unscaledTime + seconds;
            s_preview = false;
            Shown++;
        }

        static Vector3 Floor(Vector3 p)
        {
            NavMeshHit hit;
            if (NavMesh.SamplePosition(p, out hit, 2f, NavMesh.AllAreas)) return hit.position;
            return p;
        }

        void LateUpdate()
        {
            float now = Time.unscaledTime;
            bool show = now < s_until;
            if (!show && CombatSettings.UyfDash && CombatSettings.DashFlick && Physical.Live && VRRig.I != null)
            {
                // the preview: the right stick leaning forward or back, not yet a flick (left / right turn)
                Vector2 rs = VRInput.R.Stick;
                if (Mathf.Abs(rs.y) >= 0.3f && Mathf.Abs(rs.y) < 0.7f && Mathf.Abs(rs.y) > 1.5f * Mathf.Abs(rs.x) && CombatMove.ChargesLeft() > 0)
                {
                    Vector3 p;
                    if (CombatMove.Predict(rs.y > 0f, out p))
                    {
                        if (!s_preview) Previews++;
                        s_at = p;
                        s_preview = true;
                        show = true;
                    }
                }
                else s_preview = false;
            }
            if (!show || !Physical.Live) { if (s_go != null && s_go.activeSelf) s_go.SetActive(false); return; }
            if (s_go == null) Build();
            if (!s_go.activeSelf) s_go.SetActive(true);
            s_go.transform.position = s_at + Vector3.up * 0.03f;
            s_go.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            float a = s_preview ? 0.55f : Mathf.Clamp01((s_until - now) / 0.3f);
            float pulse = 0.85f + 0.15f * Mathf.Sin(now * 9f);
            s_go.transform.localScale = Vector3.one * (s_preview ? pulse : 1f);
            Draw(a);
        }

        static void Build()
        {
            s_go = new GameObject("HoF2VR_DashMarker");
            DontDestroyOnLoad(s_go);
            s_mesh = new Mesh { name = "HoF2VR_DashMarker" };
            s_mesh.MarkDynamic();
            s_go.AddComponent<MeshFilter>().sharedMesh = s_mesh;
            var r = s_go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Premonition.Alpha();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        static float s_drawnA = -1f;

        static void Draw(float a)
        {
            if (Mathf.Abs(a - s_drawnA) < 0.02f) return;
            s_drawnA = a;
            Color c = new Color(1f, 0.82f, 0.35f, 0.9f * a), glow = new Color(1f, 0.7f, 0.2f, 0.3f * a);
            s_b.Clear();
            const int n = 32;
            for (int i = 0; i < n; i++)
            {
                float a0 = i * Mathf.PI * 2f / n, a1 = (i + 1) * Mathf.PI * 2f / n;
                Vector2 p0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * Radius, p1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * Radius;
                s_b.Stroke(p0, p1, 0.03f, glow, 0.02f, 0.001f);
                s_b.Stroke(p0, p1, 0.012f, c, 0.004f, 0f);
            }
            s_b.To(s_mesh);
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The pendulum mini-game ("the metronome"; user 2026-10-12 evening: "can we have the metronome move in front of me so it
    /// is easier to see?"): while the game's PendulumTask runs, the device (CardTable.Pendulum, ~20 cm, 1.4 m away on the
    /// table) and the cards dealt onto it (DeckManager.PendulumContainer) glide to a spot in front of the player - 0.62 m
    /// ahead, a little below the eyes, turned so the side the game shows faces them - and glide back when it ends. Nothing
    /// about the swing changes (its logic is local to the device); the stops (slap, grip, trigger) work as before.
    /// Setting [Table] PendulumToPlayer. Status `metronome`.
    internal class Metronome : MonoBehaviour
    {
        public static float Ahead = 0.62f, Down = 0.16f, GlideSeconds = 0.5f;
        Transform m_dev, m_cont;
        Vector3 m_devPos0, m_contPos0;
        Quaternion m_devRot0, m_contRot0;
        bool m_moved, m_contMoved;
        float m_blend;
        Vector3 m_tPos, m_midLocal;
        Quaternion m_tRot = Quaternion.identity;
        public static int Moves;
        public static string Last = "-";

        // ---- drawn bigger while in front (round 5: in front of the player it was a 10 cm needle over 2 cm zone marks - the
        // game's close-up fills the screen): while the cameras draw, the device and its cards are grown Zoom times about the
        // device's middle, and put back after the frame is drawn - its swing, its zones and the stop are untouched
        public static float Zoom = 2.2f;
        readonly List<CardMove.Saved> m_zoomed = new List<CardMove.Saved>();
        int m_zoomFrame = -1;
        Vector3 m_mid;

        void PreCull(Camera cam)
        {
            if (!m_moved || m_dev == null || m_zoomFrame == Time.frameCount || Zoom <= 1f) return;
            m_zoomFrame = Time.frameCount;
            float k = Mathf.SmoothStep(0f, 1f, m_blend);
            float s = Mathf.Lerp(1f, Zoom, k);
            if (s <= 1.001f) return;
            Vector3 mid = m_dev.TransformPoint(m_midLocal);
            CardMove.ScaleAbout(m_dev, mid, s, m_zoomed, false);
            if (m_cont != null && m_contMoved)
            {
                // the container's cards with their texts (each card's own NGUI panel), then the container itself
                foreach (Card c in m_cont.GetComponentsInChildren<Card>(false)) CardMove.ScaleAbout(c, mid, s, m_zoomed, true);
                CardMove.ScaleAbout(m_cont, mid, s, m_zoomed, false);
            }
            if (!m_restoring) StartCoroutine(RestoreAfterFrame());
        }

        bool m_restoring;

        System.Collections.IEnumerator RestoreAfterFrame()
        {
            m_restoring = true;
            yield return new WaitForEndOfFrame();
            m_restoring = false;
            // reverse order: the container before the cards inside it
            for (int i = m_zoomed.Count - 1; i >= 0; i--)
            {
                CardMove.Saved sv = m_zoomed[i];
                if (sv.T == null) continue;
                sv.T.position = sv.Pos;
                sv.T.localScale = sv.Scale;
            }
            m_zoomed.Clear();
        }

        void Awake()
        {
            Camera.onPreCull += PreCull;
            Diag.Commands["pendgeo"] = a => PendGeo();
            Diag.StatusExtras["metronome"] = () => Diag.Q((m_moved ? "in front (" + m_blend.ToString("0.00") + ")" : "on the table") + " | moves " + Moves + " | " + Last);
        }

        /// `pendgeo`: the pendulum's parts (renderers under its transform, the pendulum container's cards) -> the log.
        static string PendGeo()
        {
            Pendulum p = CardTable.Instance != null ? CardTable.Instance.Pendulum : null;
            if (p == null || VRRig.I == null) return "no pendulum";
            Vector3 eye = VRRig.I.HeadWorldPos;
            float sc = VRRig.I.Scale;
            var sb = new System.Text.StringBuilder("pendulum parts (" + VRUI.Path(p.transform) + "):");
            int n = 0;
            foreach (Renderer r in p.GetComponentsInChildren<Renderer>(true))
            {
                if (n++ > 30) break;
                sb.Append("\n  ").Append(VRUI.Path(r.transform).Replace(VRUI.Path(p.transform), "~")).Append(r.enabled && r.gameObject.activeInHierarchy ? "" : " (off)")
                  .Append(" size ").Append(Diag.V(r.bounds.size)).Append(" at ").Append((Vector3.Distance(r.bounds.center, eye) / sc).ToString("0.00")).Append(" m")
                  .Append(r.isPartOfStaticBatch ? " (static batch: drawn where it was batched)" : "");
            }
            Transform cont = DeckManager.Instance != null && DeckManager.Instance.PendulumContainer != null ? DeckManager.Instance.PendulumContainer.transform : null;
            if (cont != null) sb.Append("\n  container ").Append(VRUI.Path(cont)).Append(" cards ").Append(cont.childCount).Append(" at ").Append((Vector3.Distance(cont.position, eye) / sc).ToString("0.00")).Append(" m");
            Plugin.L.LogInfo(sb.ToString());
            return "logged " + n + " renderers";
        }

        static bool Wanted()
        {
            if (!XRSettings.enabled || !Settings.PendulumToPlayer || PendulumTask.RunningTask == null) return false;
            // (the third playtest, mark 24: moved, the device came without its case - statically batched with the room, it stays
            // drawn on the table - so only a needle and zone marks floated in front. The game's own close-up on a window instead:
            // PendulumView; this move only with `pendview 0`)
            if (PendulumView.Showing) return false;
            if (CardTable.Instance == null || CardTable.Instance.Pendulum == null) return false;
            return Places.AtCaravan && !CombatVR.Active && VRRig.I != null && VRRig.I.HasStation;
        }

        void LateUpdate()
        {
            bool want = Wanted();
            if (want && !m_moved) Begin();
            if (!m_moved) return;
            if (m_dev == null) { m_moved = false; return; }
            m_blend = Mathf.MoveTowards(m_blend, want ? 1f : 0f, Time.unscaledDeltaTime / GlideSeconds);
            float k = Mathf.SmoothStep(0f, 1f, m_blend);
            // the device's way from its place on the table to the spot in front, as one rigid move
            Vector3 pos = Vector3.Lerp(m_devPos0, m_tPos, k);
            Quaternion rot = Quaternion.Slerp(m_devRot0, m_tRot, k);
            Quaternion d = rot * Quaternion.Inverse(m_devRot0);
            m_dev.SetPositionAndRotation(pos, rot);
            if (m_cont != null && m_contMoved) m_cont.SetPositionAndRotation(pos + d * (m_contPos0 - m_devPos0), d * m_contRot0);
            if (!want && m_blend <= 0f)
            {
                m_dev.SetPositionAndRotation(m_devPos0, m_devRot0);
                if (m_cont != null && m_contMoved) m_cont.SetPositionAndRotation(m_contPos0, m_contRot0);
                m_moved = false;
                Last = "back on the table";
            }
        }

        void Begin()
        {
            Pendulum p = CardTable.Instance.Pendulum;
            m_dev = p.transform;
            m_devPos0 = m_dev.position;
            m_devRot0 = m_dev.rotation;
            m_cont = DeckManager.Instance != null && DeckManager.Instance.PendulumContainer != null ? DeckManager.Instance.PendulumContainer.transform : null;
            m_contMoved = m_cont != null && !m_cont.IsChildOf(m_dev);
            if (m_contMoved) { m_contPos0 = m_cont.position; m_contRot0 = m_cont.rotation; }
            VRRig rig = VRRig.I;
            float sc = rig.Scale;
            Vector3 eye = rig.NeutralEye;
            Quaternion yaw = rig.NeutralRot;
            // the player is to see it the way the game's own pendulum shot shows it (the device lies on the table, seen
            // from above: turned only about the vertical it would be edge-on): the camera's pose relative to the device is
            // carried over to the eyes, at Ahead's distance
            FixedCamera cam = CardCamera.Instance != null ? CardCamera.Instance.PendulumCamera : null;
            Transform ct = cam != null && cam.Camera != null ? cam.Camera.transform : cam != null ? cam.transform : null;
            Vector3 target = eye + yaw * (new Vector3(0f, -Down, Ahead) * sc);
            Quaternion look = Quaternion.LookRotation(target - eye, Vector3.up);
            // the device's visual middle (its renderers), in its own frame: that point goes to the spot in front
            Bounds vb = default(Bounds);
            bool hasB = false;
            foreach (Renderer r in m_dev.GetComponentsInChildren<Renderer>())
            {
                // (not its beam's glow: an effect 0.8 m wide round the needle's tip)
                if (r == null || !r.enabled || r is ParticleSystemRenderer || r.GetComponentInParent<ParticleSystem>() != null || r.name.IndexOf("glow", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!hasB) { vb = r.bounds; hasB = true; } else vb.Encapsulate(r.bounds);
            }
            Vector3 midLocal = hasB ? Quaternion.Inverse(m_devRot0) * (vb.center - m_devPos0) : Vector3.zero;
            m_midLocal = m_dev.InverseTransformPoint(hasB ? vb.center : m_devPos0);
            if (ct != null)
            {
                Quaternion camLocalRot = Quaternion.Inverse(m_devRot0) * ct.rotation;
                // the camera's own look at it (as rolls and pitches go), facing the player: seen as the game's close-up shows it
                // (round 5: placing the pivot where the camera's position put it left the device 0.42 m below the eyes, out of
                // view - the camera looks at its upper part, not at its pivot)
                m_tRot = look * Quaternion.Inverse(camLocalRot);
                m_tPos = target - m_tRot * midLocal;
            }
            else
            {
                m_tRot = Quaternion.LookRotation(eye - target, Vector3.up) * Quaternion.Euler(90f, 0f, 0f);
                m_tPos = target - m_tRot * midLocal;
            }
            m_blend = 0f;
            m_moved = true;
            Moves++;
            Vector3 rel = Quaternion.Inverse(yaw) * (target - eye) / sc;
            Last = "moved in front: its middle " + rel.z.ToString("0.00") + " m ahead, " + (-rel.y).ToString("0.00") + " m below the eyes, pivot " + Diag.V(m_tPos) + " (" + (Vector3.Distance(m_devPos0, eye) / sc).ToString("0.00") + " m -> " + Ahead.ToString("0.00") + " m), cards " + (m_contMoved ? "moved with it" : "on the device");
            Plugin.L.LogInfo("metronome: " + Last);
        }
    }
}

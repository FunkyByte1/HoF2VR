using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The wheel mini-game kept from the eyes (2026-10-14 seated; 2026-10-18 standing too). The game's wheel is a carousel of
    /// cards turning about a vertical axis (WheelOfFortune: only the container's local rotation), its middle at a fixed height
    /// over the table - about 0.33 m below a standing player's eyes, level with seated eyes 0.33 m ahead - and its ring as wide
    /// as that, so its near cards swept past the face and through the body (0.12 m in the seated sweep; standing 0.23 m).
    ///  * Seated (2026-10-14): while the wheel's task runs, its container glides (0.4 s) down and out to SeatedDown below and
    ///    SeatedAhead ahead of the neutral eyes.
    ///  * The spawn rule (the bug hunt of 2026-10-18, finding 5: the cards came "quickly" within 0.53 m): seated and standing,
    ///    the carousel is lowered toward the table, moved out (horizontally, away from the eyes) and, only where that is not
    ///    enough, made smaller about its axis, until the ring its cards sweep keeps MinEyes from the neutral eyes (and from the
    ///    eyes of the moment it began) and Body from the body's vertical, stays OverTable above the table top and DealerGap from
    ///    every bone of the Dealer (his skeleton as it is posed: his arms lie on the table's far side). The least change wins:
    ///    the largest size, then the least move out, then the least move down.
    ///    The ring is one card's box (the union of every card seen on it) in the ring's own frame, turned with the carousel to
    ///    where it passes nearest the eyes and measured there as the watchdog does. It is known before the first card is dealt
    ///    (DefaultMin/Max, widened by every card seen this session: the game's ring is always the same), so the carousel is in its
    ///    place before the deal (the game waits 0.5 s before it) and no card ever lands near the eyes; cards that settle on the
    ///    ring (still in the container's own frame for a check) then widen the box if they reach further. The change only grows
    ///    while the task runs. Position and size glide back when the task ends.
    /// Status `wheelSeat`.
    internal class WheelSeat : MonoBehaviour
    {
        public static float SeatedAhead = 0.50f, SeatedDown = 0.20f, GlideSeconds = 0.4f;
        /// The rule's 0.53 m and a few millimetres (real metres, measured as the watchdog does); the move is at most MaxOut
        /// out and MaxDown down, the carousel at least MinScale of its size; DealerGap from his bones (a bone is the middle of a
        /// limb: an arm's sleeve reaches ~6 cm from it).
        public static float MinEyes = 0.535f, MaxOut = 0.6f, MaxDown = 0.5f, MinScale = 0.5f, DealerGap = 0.12f, Body = 0.24f, OverTable = 0.03f;
        Transform m_cont;
        Vector3 m_pos0, m_base, m_target, m_eyeAtStart, m_scale0;
        bool m_moved;
        float m_blend, m_nextCheck, m_out, m_down, m_size = 1f;
        int m_searchedFor = -1;
        public static int Moves;
        public static float Closest = -1f;   // the closest the ring's cards come to the eyes, as placed (real metres)
        public static string Last = "-", Probe = "-";
        static readonly Vector3[] s_eyes = new Vector3[2];

        void Awake()
        {
            Diag.StatusExtras["wheelSeat"] = () => Diag.Q((m_moved ? "moved (" + m_blend.ToString("0.00") + ")" : "in its place") + " | moves " + Moves +
                                                          " | out " + m_out.ToString("0.00") + " m, down " + m_down.ToString("0.00") + " m, size " + m_size.ToString("0.00") + ", closest " + Closest.ToString("0.00") + " m | " + Last + " | " + Probe);
        }

        static bool Wanted()
        {
            if (!XRSettings.enabled || WheelSpinTask.RunningTask == null) return false;
            DeckManager dm = DeckManager.Instance;
            if (dm == null || dm.WheelSpinContainer == null) return false;
            return Places.Current == Places.Place.Caravan && !CombatVR.Active && VRRig.I != null && VRRig.I.HasStation;
        }

        static bool SeatedHere => Settings.Seated && VRRig.I != null && !float.IsNaN(VRRig.I.SeatedSurface);

        void LateUpdate()
        {
            bool want = Wanted();
            if (want && !m_moved) Begin();
            if (!m_moved) return;
            if (m_cont == null) { m_moved = false; return; }
            if (want && Time.unscaledTime >= m_nextCheck) { m_nextCheck = Time.unscaledTime + 0.25f; Keep(); }
            m_blend = Mathf.MoveTowards(m_blend, want ? 1f : 0f, Time.unscaledDeltaTime / GlideSeconds);
            float k = Mathf.SmoothStep(0f, 1f, m_blend);
            m_cont.position = Vector3.Lerp(m_pos0, m_target, k);
            m_cont.localScale = m_scale0 * Mathf.Lerp(1f, m_size, k);
            if (!want && m_blend <= 0f)
            {
                m_cont.position = m_pos0;
                m_cont.localScale = m_scale0;
                m_moved = false;
                Last = "back in its place";
            }
        }

        void Begin()
        {
            m_cont = DeckManager.Instance.WheelSpinContainer.transform;
            m_pos0 = m_cont.position;
            m_scale0 = m_cont.localScale;
            VRRig rig = VRRig.I;
            float sc = rig.Scale;
            Vector3 eye = rig.NeutralEye;
            m_eyeAtStart = rig.HeadWorldPos;
            bool seated = SeatedHere;
            m_base = seated ? eye + rig.NeutralRot * (new Vector3(0f, -SeatedDown, SeatedAhead) * sc) : m_pos0;
            m_target = m_base;
            m_out = m_down = 0f;
            m_size = 1f;
            m_searchedFor = -1;
            m_blend = 0f;
            m_moved = true;
            m_nextCheck = Time.unscaledTime + 0.25f;
            s_lastLocal.Clear();
            Moves++;
            Vector3 was = Quaternion.Inverse(rig.NeutralRot) * (m_pos0 - eye) / sc;
            Last = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}: its middle {1:0.00} m ahead / {2:0.00} m below the eyes", seated ? "seated" : "standing", was.z, -was.y) +
                   (seated ? string.Format(System.Globalization.CultureInfo.InvariantCulture, ", to {0:0.00} / {1:0.00}", SeatedAhead, SeatedDown) : "");
            Plugin.L.LogInfo("wheel: " + Last);
            CollectBones();
            // (the ring's shape is known before its first card: placed now, while the game waits to deal)
            Keep();
        }

        // ---- the ring: one card's box in the ring's own frame (the card turned to angle 0), the container's own size --------
        /// The ring's card box measured on 2026-10-19 (the caravan's wheel: the cards lie flat in the ring's plane, x across,
        /// z out from the axis; world units at the container's own size; 1.26 world units a real metre there).
        public static Vector3 DefaultMin = new Vector3(-0.065f, -0.005f, 0.11f), DefaultMax = new Vector3(0.065f, 0.005f, 0.32f);
        static Vector3 s_min = DefaultMin, s_max = DefaultMax;
        static bool s_measured;
        static int s_shape;   // grows whenever the box does (a new search)
        static readonly Dictionary<int, Vector3> s_lastLocal = new Dictionary<int, Vector3>();
        static Vector3 s_axis = Vector3.up;
        static Quaternion s_frame = Quaternion.identity;
        static float s_nowNearest;
        static int s_ringCards;
        /// (real metres) a card still on the move in the container's frame (flying onto it) doesn't count; nor one further than
        /// RingTolerance from the ring's own radius (the box's middle)
        public static float Settle = 0.01f, RingTolerance = 0.06f;

        void Frame()
        {
            // (the carousel turns about the container's own up - its parent may lean: angles are taken about that axis)
            s_axis = m_cont.up;
            Vector3 fw = Vector3.ProjectOnPlane(m_cont.parent != null ? m_cont.parent.forward : Vector3.forward, s_axis);
            if (fw.sqrMagnitude < 1e-6f) fw = Vector3.ProjectOnPlane(Vector3.right, s_axis);
            s_frame = Quaternion.LookRotation(fw.normalized, s_axis);
        }

        /// The settled ring cards widen the box (DefaultMin/Max, or the shape seen so far this session).
        void CollectRing(float sc)
        {
            s_nowNearest = float.MaxValue;
            s_ringCards = 0;
            Vector3 c0 = m_cont.position;
            Quaternion inv = Quaternion.Inverse(s_frame);
            float cur = Mathf.Max(1e-4f, m_cont.localScale.x / Mathf.Max(1e-4f, m_scale0.x));
            float ringR = 0.5f * (s_min.z + s_max.z);
            Vector3[] c = new Vector3[8];
            foreach (Renderer rd in m_cont.GetComponentsInChildren<Renderer>())
            {
                if (rd == null || !rd.enabled || rd is ParticleSystemRenderer || rd.GetComponentInParent<Card>() == null) continue;
                // (what the watchdog measures now, for the log)
                s_nowNearest = Mathf.Min(s_nowNearest, Vector3.Distance(rd.bounds.ClosestPoint(VRRig.I.NeutralEye), VRRig.I.NeutralEye) / sc);
                Vector3 local = m_cont.InverseTransformPoint(rd.bounds.center);
                int id = rd.GetInstanceID();
                Vector3 was;
                bool settled = s_lastLocal.TryGetValue(id, out was) && Vector3.Scale(local - was, m_cont.lossyScale).magnitude < Settle * sc;
                s_lastLocal[id] = local;
                if (!settled) continue;
                Vector3 rel = inv * ((rd.bounds.center - c0) / cur);
                if (Mathf.Abs(new Vector2(rel.x, rel.z).magnitude - ringR) > RingTolerance * sc) continue;
                Quaternion back = Quaternion.AngleAxis(-Mathf.Atan2(rel.x, rel.z) * Mathf.Rad2Deg, Vector3.up);
                Corners(rd, c);
                for (int k = 0; k < 8; k++)
                {
                    Vector3 p = back * (inv * ((c[k] - c0) / cur));
                    s_measured = true;
                    if (p.x < s_min.x - 1e-3f || p.y < s_min.y - 1e-3f || p.z < s_min.z - 1e-3f || p.x > s_max.x + 1e-3f || p.y > s_max.y + 1e-3f || p.z > s_max.z + 1e-3f)
                    { s_min = Vector3.Min(s_min, p); s_max = Vector3.Max(s_max, p); s_shape++; }
                }
                s_ringCards++;
            }
        }

        /// The ring's box at every turn of the carousel (Steps angles), at size 1, as offsets from its middle (world).
        const int Steps = 36;
        static readonly Vector3[,] s_rot = new Vector3[Steps, 8];

        static void Turns()
        {
            for (int a = 0; a < Steps; a++)
            {
                Quaternion q = s_frame * Quaternion.AngleAxis(a * 360f / Steps, Vector3.up);
                for (int k = 0; k < 8; k++)
                    s_rot[a, k] = q * new Vector3((k & 1) != 0 ? s_max.x : s_min.x, (k & 2) != 0 ? s_max.y : s_min.y, (k & 4) != 0 ? s_max.z : s_min.z);
            }
        }

        /// The closest the ring's cards come to the eyes as it turns, with its middle at `centre` and at `size` (world units),
        /// measured as the watchdog does - each card's world box at every turn (a card turned aslant has a wider box);
        /// `body`: the closest they come to the eyes' vertical below the eyes (the watchdog's body cylinder); `low` / `high`:
        /// their lowest / highest point.
        static float Nearest(Vector3 centre, float size, out float body, out float low, out float high)
        {
            float best = float.MaxValue;
            body = float.MaxValue;
            low = float.MaxValue;
            high = float.MinValue;
            for (int a = 0; a < Steps; a++)
            {
                Vector3 mn = centre + s_rot[a, 0] * size, mx = mn;
                for (int k = 1; k < 8; k++) { Vector3 c = centre + s_rot[a, k] * size; mn = Vector3.Min(mn, c); mx = Vector3.Max(mx, c); }
                low = Mathf.Min(low, mn.y);
                high = Mathf.Max(high, mx.y);
                foreach (Vector3 eye in s_eyes)
                {
                    float dx = Mathf.Max(0f, Mathf.Max(mn.x - eye.x, eye.x - mx.x)), dz = Mathf.Max(0f, Mathf.Max(mn.z - eye.z, eye.z - mx.z));
                    float dy = Mathf.Max(0f, Mathf.Max(mn.y - eye.y, eye.y - mx.y));
                    best = Mathf.Min(best, Mathf.Sqrt(dx * dx + dy * dy + dz * dz));
                    if (mn.y < eye.y) body = Mathf.Min(body, Mathf.Sqrt(dx * dx + dz * dz));
                }
            }
            return best;
        }

        /// How far the ring reaches out from its axis (world units, size 1): the box's farthest corner.
        static float Reach()
        {
            float r = 0f;
            for (int k = 0; k < 8; k++) r = Mathf.Max(r, new Vector2((k & 1) != 0 ? s_max.x : s_min.x, (k & 4) != 0 ? s_max.z : s_min.z).magnitude);
            return r;
        }

        // ---- what the ring must miss: the Dealer's skeleton (his arms lie on the table's far side) and the hero's figurine ---
        static readonly List<Transform> s_bones = new List<Transform>();
        static readonly List<Vector3> s_obst = new List<Vector3>();
        static readonly List<float> s_obstGap = new List<float>();
        static readonly List<string> s_obstName = new List<string>();
        /// (real metres) the figurine's box corners keep FigurineGap from the ring (it stands 0.10 m tall on the map's near row)
        public static float FigurineGap = 0.03f;

        static void CollectBones()
        {
            s_bones.Clear();
            Dealer dl = Dealer.Instance;
            if (dl == null) return;
            var seen = new HashSet<Transform>();
            foreach (SkinnedMeshRenderer smr in dl.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr == null || !smr.enabled) continue;
                foreach (Transform b in smr.bones) if (b != null && seen.Add(b)) s_bones.Add(b);
            }
        }

        static void Obstacles(float sc)
        {
            s_obst.Clear(); s_obstGap.Clear(); s_obstName.Clear();
            foreach (Transform b in s_bones) if (b != null) { s_obst.Add(b.position); s_obstGap.Add(DealerGap * sc); s_obstName.Add("the Dealer's " + b.name); }
            Transform fig = FigurineGrab.Figurine();
            if (fig == null) return;
            bool any = false;
            Bounds fb = default(Bounds);
            foreach (Renderer r in fig.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
                if (!any) { fb = r.bounds; any = true; } else fb.Encapsulate(r.bounds);
            }
            if (!any) return;
            for (int k = 0; k < 8; k++)
            {
                s_obst.Add(fb.center + Vector3.Scale(fb.extents, new Vector3((k & 1) != 0 ? 1f : -1f, (k & 2) != 0 ? 1f : -1f, (k & 4) != 0 ? 1f : -1f)));
                s_obstGap.Add(FigurineGap * sc);
                s_obstName.Add("the figurine");
            }
        }

        /// The least room between the ring's swept disc (its axis through `centre`, out to `reach`, `thick` either side of its
        /// plane) and what it must miss, less each one's gap (world units; below 0 = too close).
        static float Clearance(Vector3 centre, float reach, float thick, out int which)
        {
            float best = float.MaxValue;
            which = -1;
            for (int i = 0; i < s_obst.Count; i++)
            {
                Vector3 p = s_obst[i] - centre;
                float off = Mathf.Max(0f, Mathf.Abs(Vector3.Dot(p, s_axis)) - thick);
                float rad = Mathf.Max(0f, Vector3.ProjectOnPlane(p, s_axis).magnitude - reach);
                float room = Mathf.Sqrt(rad * rad + off * off) - s_obstGap[i];
                if (room < best) { best = room; which = i; }
            }
            return best;
        }

        /// Places the carousel: lower, further out and, where that is not enough, smaller, until its ring keeps the rule.
        void Keep()
        {
            VRRig rig = VRRig.I;
            if (rig == null || m_cont == null) return;
            float sc = Mathf.Max(0.01f, rig.Scale);
            Frame();
            CollectRing(sc);
            // (a search runs once per shape of the ring: it costs a few ms)
            if (s_shape == m_searchedFor) return;
            m_searchedFor = s_shape;
            Turns();
            s_eyes[0] = rig.NeutralEye;
            s_eyes[1] = m_eyeAtStart;
            Obstacles(sc);
            Vector3 eye = rig.NeutralEye, dir = m_base - eye;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) dir = rig.NeutralRot * Vector3.forward;
            dir.Normalize();
            float reach = Reach(), thick = Mathf.Max(Mathf.Abs(s_min.y), Mathf.Abs(s_max.y)), baseAhead = Vector3.Dot(m_base - eye, dir);
            float top = TableHud.TableTop(float.MinValue);
            float bestOut = -1f, bestDown = 0f, bestSize = 1f, near = 0f, nearBody = 0f, room = 0f;
            int which = -1;
            // (the least change first: the full size, then the least move out, then the least move down; never back - the change
            // only grows while the wheel runs)
            for (float size = m_size; size >= MinScale - 1e-4f && bestOut < 0f; size -= 0.05f)
                for (float k = m_out; k <= MaxOut + 1e-4f && bestOut < 0f; k += 0.02f)
                    for (float d = m_down; d <= MaxDown + 1e-4f; d += 0.025f)
                    {
                        Vector3 centre = m_base + dir * (k * sc) - Vector3.up * (d * sc);
                        float body, low, high;
                        float n = Nearest(centre, size, out body, out low, out high) / sc;
                        if (top > float.MinValue && low < top + OverTable * sc) break;   // (lower still is into the table)
                        if (n < MinEyes || body / sc < Body) continue;
                        int w;
                        float r = Clearance(centre, reach * size, thick * size, out w) / sc;
                        if (r < 0f) continue;
                        bestOut = k; bestDown = d; bestSize = size; near = n; nearBody = body / sc; room = r; which = w;
                        break;
                    }
            Probe = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "ring {0} cards{1}, box x {2:0.000}..{3:0.000} y {4:0.000}..{5:0.000} r {6:0.000}..{7:0.000}, reach {8:0.00} m, middle {9:0.00} m ahead, table {10:0.00} m below the eyes, axis tilt {11:0} deg, nearest now {12:0.00} m, misses {13}",
                s_ringCards, s_measured ? "" : " (the default box)", s_min.x, s_max.x, s_min.y, s_max.y, s_min.z, s_max.z, reach / sc, baseAhead / sc,
                top > float.MinValue ? (eye.y - top) / sc : -1f, Vector3.Angle(s_axis, Vector3.up), s_nowNearest < float.MaxValue ? s_nowNearest : -1f, s_obst.Count);
            if (bestOut < 0f)
            {
                // (no way within the limits: left as it is)
                float b0, l0, h0;
                Closest = Nearest(Target(dir, sc), m_size, out b0, out l0, out h0) / sc;
                Plugin.L.LogWarning("wheel: the ring can't keep " + MinEyes.ToString("0.000") + " m within the limits (" + Probe + "); its nearest " + Closest.ToString("0.00") + " m");
                return;
            }
            Closest = near;
            Probe += string.Format(System.Globalization.CultureInfo.InvariantCulture, ", body {0:0.00} m, {1}", nearBody,
                which >= 0 ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00} m beyond the gap from {1}", room, s_obstName[which]) : "nothing to miss");
            if (bestOut <= m_out + 1e-4f && bestDown <= m_down + 1e-4f && bestSize >= m_size - 1e-4f) return;
            m_out = bestOut;
            m_down = bestDown;
            m_size = bestSize;
            m_target = Target(dir, sc);
            Plugin.L.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture, "wheel: {0}; moved {1:0.00} m further out and {2:0.00} m down at {3:0.00} of its size: its ring {4:0.000} m from the eyes at the nearest ({5})",
                Last, m_out, m_down, m_size, near, Probe));
        }

        Vector3 Target(Vector3 dir, float sc) => m_base + dir * (m_out * sc) - Vector3.up * (m_down * sc);

        /// The 8 corners of the renderer's mesh box in the world (its world box's corners when it has no mesh).
        static void Corners(Renderer rd, Vector3[] into)
        {
            MeshFilter mf = rd.GetComponent<MeshFilter>();
            Mesh m = mf != null ? mf.sharedMesh : null;
            Bounds lb = m != null ? m.bounds : default(Bounds);
            Transform t = rd.transform;
            for (int k = 0; k < 8; k++)
            {
                Vector3 sgn = new Vector3((k & 1) != 0 ? 1f : -1f, (k & 2) != 0 ? 1f : -1f, (k & 4) != 0 ? 1f : -1f);
                into[k] = m != null ? t.TransformPoint(lb.center + Vector3.Scale(lb.extents, sgn)) : rd.bounds.center + Vector3.Scale(rd.bounds.extents, sgn);
            }
        }
    }
}

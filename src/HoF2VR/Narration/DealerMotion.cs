using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// The Dealer's head moves a little with the reading (docs/narration-design.md §8; [Narration] HeadMotion; the user:
    /// "nothing crazy, just making their head move a little along with the speech").
    ///  * The clip's motion data (ClipStore.Motion, format v1 of work/narration/motion9.py): a small nod on each stressed
    ///    syllable (0.8-2.6 degrees down), a slight tilt per phrase (1.6 degrees, with a 0.7 degree turn), stillness in pauses;
    ///    each channel through a critically damped spring (pitch 14, roll 5, yaw 4 rad/s) - motion9.curves() exactly.
    ///  * Sampled at the playing clip's own time: the narration's source, or the game's voice when it plays one of his
    ///    re-voiced lines. The pause menu freezes it with the sound.
    ///  * Added on top of the animated pose in LateUpdate (his idle and gestures still show): 40 % on the neck, 60 % on the
    ///    head (all on the head without a neck bone); 40 % of it while he is not idling (dealing). At most 4 / 3 / 2 degrees.
    ///  * His rig: the bones are written only when they really move his mesh. An optimized rig (the head bone only an
    ///    exposed copy) is opened once with AnimatorUtility.DeoptimizeTransformHierarchy as soon as he is there with the
    ///    narration and HeadMotion on; if that is not possible his own gestures (from the clip's data, Narrator) are the
    ///    only motion.
    internal class DealerMotion : MonoBehaviour
    {
        public static DealerMotion I;
        public static bool AutoDeopt = true;
        const float LimP = 4f, LimR = 3f, LimY = 2f;

        Dealer m_of;
        Animator m_anim;
        Transform m_head, m_neck;
        bool m_movable, m_triedDeopt;
        float m_p, m_pv, m_r, m_rv, m_y, m_yv, m_busy = 1f;
        Quaternion m_headBase, m_headWritten, m_neckBase, m_neckWritten;
        bool m_wrote;
        int m_bakeTest;
        public static string Rig = "not looked at", Src = "-", BakeResult = "-";
        public static float Pitch, Roll, Yaw, MaxPitch, MaxRoll, MaxYaw;
        public static int Frames;

        void Awake()
        {
            I = this;
            Diag.Commands["dealer"] = Command;
            Diag.StatusExtras["dealerMotion"] = () => Diag.Q(Describe());
            Diag.StatusExtras["dealerGaze"] = () => Diag.Q(DealerGaze.Describe());
        }

        public static void ResetMax() { MaxPitch = MaxRoll = MaxYaw = 0f; Frames = 0; }

        void LateUpdate()
        {
            try { Step(); }
            catch (Exception e)
            {
                Plugin.L.LogError("DealerMotion: " + e);
                enabled = false;
            }
        }

        void Step()
        {
            ClipStore.Motion m = null;
            float t = 0f;
            if (Narrator.I != null && Narrator.I.MotionNow(out m, out t)) Src = "narration";
            else if (DealerLines.MotionNow(out m, out t)) Src = "his line";
            else Src = "-";
            float gain = NarrationSettings.Enabled ? NarrationSettings.HeadMotion : 0f;
            Dealer d = Dealer.Instance;
            bool gaze = DealerGaze.Wanted || Mathf.Abs(DealerGaze.Yaw) + Mathf.Abs(DealerGaze.Pitch) > 0.05f;
            // with the head motion on, his rig is opened as soon as he is there (the table's arrival is behind a fade),
            // not in the middle of the first reading
            if (d != null && (gain > 0f || gaze) && !GameWatch.InCombatScene && (d != m_of || m_anim == null || (!m_movable && !m_triedDeopt))) Bones(d, true);
            bool rest = Mathf.Abs(m_p) + Mathf.Abs(m_r) + Mathf.Abs(m_y) + Mathf.Abs(m_pv) + Mathf.Abs(m_rv) + Mathf.Abs(m_yv) < 1e-3f;
            if (d == null || (rest && !gaze && (m == null || gain <= 0f)) && m_bakeTest == 0)
            {
                Unwrite();
                if (rest) m_p = m_r = m_y = m_pv = m_rv = m_yv = 0f;
                Pitch = Roll = Yaw = 0f;
                return;
            }
            if (!Bones(d, gain > 0f && m != null || gaze)) return;
            // his head as animated this frame (last frame's offset off), for the gaze to measure from
            Rebase();
            float tp = 0f, tr = 0f, ty = 0f;
            if (m != null && gain > 0f) Targets(m, t, out tp, out tr, out ty);
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f);
            bool idle = d.IsInSubTag("Idle");
            m_busy = Mathf.MoveTowards(m_busy, idle ? 1f : 0.4f, dt / 0.3f);
            float g = gain * m_busy;
            // the springs, in steps of at most 1/120 s (motion9.curves() steps at the frame rate)
            int n = Mathf.Max(1, Mathf.CeilToInt(dt * 120f));
            float h = dt / n;
            for (int i = 0; i < n; i++)
            {
                Spring(ref m_p, ref m_pv, tp * g, 14f, h);
                Spring(ref m_r, ref m_rv, tr * g, 5f, h);
                Spring(ref m_y, ref m_yv, ty * g, 4f, h);
            }
            Pitch = Mathf.Clamp(m_p, -LimP, LimP);
            Roll = Mathf.Clamp(m_r, -LimR, LimR);
            Yaw = Mathf.Clamp(m_y, -LimY, LimY);
            if (m != null) Frames++;
            MaxPitch = Mathf.Max(MaxPitch, Mathf.Abs(Pitch));
            MaxRoll = Mathf.Max(MaxRoll, Mathf.Abs(Roll));
            MaxYaw = Mathf.Max(MaxYaw, Mathf.Abs(Yaw));
            if (m_bakeTest > 0) BakeTest();
            float gy = 0f, gp = 0f;
            DealerGaze.Tick(d, m_head, dt, m_busy, Src != "-", out gy, out gp);
            if (!m_movable) return;
            Write(d, Pitch + gp, Roll, Yaw + gy);
        }

        /// Last frame's offset off (when his animation did not write the bone itself), and the animated pose noted.
        void Rebase()
        {
            if (m_head == null) return;
            if (m_wrote)
            {
                if (Quaternion.Angle(m_head.localRotation, m_headWritten) < 0.01f) m_head.localRotation = m_headBase;
                if (m_neck != null && Quaternion.Angle(m_neck.localRotation, m_neckWritten) < 0.01f) m_neck.localRotation = m_neckBase;
                m_wrote = false;
            }
        }

        static void Spring(ref float p, ref float v, float target, float w0, float dt)
        {
            float acc = w0 * w0 * (target - p) - 2f * w0 * v;   // semi-implicit Euler, critically damped (motion9.curves)
            v += acc * dt;
            p += v * dt;
        }

        // ---- the targets (motion9.curves without the springs) -------------------------------------------------------------
        static float Nod(float u)
        {
            if (u >= -0.10f && u < 0f) return -0.5f * (1f - Mathf.Cos(Mathf.PI * (u + 0.10f) / 0.10f));
            if (u >= 0f && u <= 0.32f) return -0.5f * (1f + Mathf.Cos(Mathf.PI * u / 0.32f));
            return 0f;
        }

        static float Ease(float u)
        {
            u = Mathf.Clamp01(u);
            return u * u * (3f - 2f * u);
        }

        public static void Targets(ClipStore.Motion m, float t, out float pitch, out float roll, out float yaw)
        {
            pitch = roll = yaw = 0f;
            for (int i = 0; i < m.BeatT.Length; i++)
            {
                float u = t - m.BeatT[i];
                if (u < -0.11f || u > 0.33f) continue;
                pitch -= (0.8f + 1.8f * m.BeatS[i]) * Nod(u);
            }
            for (int k = 0; k < m.PhrA.Length; k++)
            {
                float w = Ease((t - m.PhrA[k]) / 0.45f) * (1f - Ease((t - m.PhrB[k]) / 0.5f));
                if (w <= 0f) continue;
                int side = m.PhrSide[k];
                roll += side * 1.6f * w;
                yaw += (side != 0 ? -side : (k % 2 == 1 ? 1 : -1)) * 1.2f * 0.6f * w;
            }
            for (int i = 0; i < m.StillA.Length; i++)
            {
                float w = Ease((t - m.StillA[i]) / 0.15f) * (1f - Ease((t - m.StillB[i]) / 0.15f));
                if (w <= 0f) continue;
                pitch *= 1f - w;
                roll *= 1f - 0.6f * w;
                yaw *= 1f - 0.6f * w;
            }
        }

        // ---- his rig ------------------------------------------------------------------------------------------------------
        /// His animator, head and neck; opens an optimized rig once (when the motion is wanted). False = nothing to move.
        bool Bones(Dealer d, bool want)
        {
            Animator anim = d.Animator;
            if (anim == null) return false;
            if (anim != m_anim || m_of != d || m_head == null)
            {
                m_wrote = false;
                m_of = d;
                m_anim = anim;
                m_triedDeopt = false;
                FindBones();
                m_movable = anim.hasTransformHierarchy && m_head != null;
                Rig = Describe(anim);
                Plugin.L.LogInfo("DealerMotion: his rig - " + Rig);
            }
            if (!m_movable && want && AutoDeopt && !GameWatch.InCombatScene && !m_triedDeopt && !m_anim.hasTransformHierarchy && (NarrationSettings.HeadMotion > 0f || Settings.DealerGaze))
            {
                m_triedDeopt = true;
                Deopt(Settings.DealerGaze ? "his gaze follows the player" : "the narration's head motion is on");
            }
            return m_head != null;
        }

        void FindBones()
        {
            m_head = m_neck = null;
            if (m_anim == null) return;
            Transform best = null;
            foreach (Transform t in m_anim.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                if (n == "Dealer Head") best = t;
                else if (best == null && n.EndsWith("Head", StringComparison.OrdinalIgnoreCase)) best = t;
            }
            m_head = best;
            if (m_head != null)
                for (Transform p = m_head.parent; p != null && p != m_anim.transform; p = p.parent)
                    if (p.name.IndexOf("Neck", StringComparison.OrdinalIgnoreCase) >= 0) { m_neck = p; break; }
        }

        string Deopt(string why)
        {
            if (m_anim == null) return "no animator";
            if (m_anim.hasTransformHierarchy) return "already open";
            int before = m_anim.GetComponentsInChildren<Transform>(true).Length;
            Transform oldHead = m_head;
            try { AnimatorUtility.DeoptimizeTransformHierarchy(m_anim.gameObject); }
            catch (Exception e)
            {
                Plugin.L.LogWarning("DealerMotion: DeoptimizeTransformHierarchy failed (" + e.Message + ") - his own gestures only");
                return "failed: " + e.Message;
            }
            m_wrote = false;
            FindBones();
            int after = m_anim.GetComponentsInChildren<Transform>(true).Length;
            m_movable = m_anim.hasTransformHierarchy && m_head != null;
            if (VRAudio.I != null) VRAudio.I.ForgetHead();
            Rig = Describe(m_anim);
            string r = "opened his rig (" + why + "): transforms " + before + " -> " + after + ", the old head bone " + (oldHead == null ? "is gone" : "stays (" + VRUI.Path(oldHead) + ")") +
                       ", head now " + (m_head != null ? VRUI.Path(m_head) : "none") + ", neck " + (m_neck != null ? m_neck.name : "none") + ", movable " + m_movable;
            Plugin.L.LogInfo("DealerMotion: " + r);
            return r;
        }

        void Write(Dealer d, float pitch, float roll, float yaw)
        {
            // the animator writes his bones each frame; if it did not (a bone without curves) last frame's offset comes off first
            if (m_wrote)
            {
                if (Quaternion.Angle(m_head.localRotation, m_headWritten) < 0.01f) m_head.localRotation = m_headBase;
                if (m_neck != null && Quaternion.Angle(m_neck.localRotation, m_neckWritten) < 0.01f) m_neck.localRotation = m_neckBase;
            }
            m_headBase = m_head.localRotation;
            if (m_neck != null) m_neckBase = m_neck.localRotation;
            Transform root = d.transform;
            Vector3 fwd = root.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            if (m_neck != null)
            {
                m_neck.rotation = Offset(pitch, roll, yaw, 0.4f, right, fwd) * m_neck.rotation;
                m_head.rotation = Offset(pitch, roll, yaw, 0.6f, right, fwd) * m_head.rotation;
                m_neckWritten = m_neck.localRotation;
            }
            else m_head.rotation = Offset(pitch, roll, yaw, 1f, right, fwd) * m_head.rotation;
            m_headWritten = m_head.localRotation;
            m_wrote = true;
        }

        /// + pitch = his head nods down (about his right), + roll = toward his right shoulder side's tilt as motion9's side
        /// (-1 = to his left), + yaw = toward his right.
        static Quaternion Offset(float pitch, float roll, float yaw, float f, Vector3 right, Vector3 fwd) =>
            Quaternion.AngleAxis(pitch * f, right) * Quaternion.AngleAxis(yaw * f, Vector3.up) * Quaternion.AngleAxis(-roll * f, fwd);

        void Unwrite()
        {
            if (!m_wrote || m_head == null) { m_wrote = false; return; }
            if (Quaternion.Angle(m_head.localRotation, m_headWritten) < 0.01f) m_head.localRotation = m_headBase;
            if (m_neck != null && Quaternion.Angle(m_neck.localRotation, m_neckWritten) < 0.01f) m_neck.localRotation = m_neckBase;
            m_wrote = false;
        }

        /// Does writing his head bone move his mesh? Bakes his skinned meshes with and without a 10 degree nod.
        void BakeTest()
        {
            m_bakeTest = 0;
            if (m_head == null || m_of == null) { BakeResult = "no head"; return; }
            var sb = new StringBuilder();
            float best = 0f;
            Vector3 right = Vector3.Cross(Vector3.up, m_of.transform.forward);
            foreach (SkinnedMeshRenderer smr in m_of.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (smr == null || !smr.enabled || smr.sharedMesh == null) continue;
                var a = new Mesh();
                var b = new Mesh();
                Quaternion keep = m_head.rotation;
                smr.BakeMesh(a);
                m_head.rotation = Quaternion.AngleAxis(10f, right) * keep;
                smr.BakeMesh(b);
                m_head.rotation = keep;
                Vector3[] va = a.vertices, vb = b.vertices;
                float mx = 0f;
                for (int i = 0; i < va.Length && i < vb.Length; i++) mx = Mathf.Max(mx, (va[i] - vb[i]).magnitude);
                mx *= smr.transform.lossyScale.x;
                best = Mathf.Max(best, mx);
                sb.Append(smr.name).Append(' ').Append(va.Length).Append(" verts moved ").Append((mx * 100f).ToString("0.0", CultureInfo.InvariantCulture)).Append(" cm; ");
                Destroy(a);
                Destroy(b);
            }
            BakeResult = (best > 0.005f ? "MOVES" : "LOCKED") + " (a 10 deg nod of '" + m_head.name + "': " + sb + ")";
            Plugin.L.LogInfo("DealerMotion: bake test " + BakeResult);
        }

        static string Describe(Animator a)
        {
            if (a == null) return "no animator";
            var sb = new StringBuilder();
            sb.Append(VRUI.Path(a.transform)).Append(" transformHierarchy ").Append(a.hasTransformHierarchy).Append(" human ").Append(a.isHuman)
              .Append(" optimizable ").Append(a.isOptimizable).Append(" avatar ").Append(a.avatar != null ? a.avatar.name : "-").Append(" layers ").Append(a.layerCount)
              .Append(" params ").Append(a.parameterCount).Append(" children ").Append(a.transform.childCount);
            return sb.ToString();
        }

        string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture, "src {0} pitch {1:0.00} roll {2:0.00} yaw {3:0.00} max {4:0.00}/{5:0.00}/{6:0.00} frames {7} busy {8:0.00} movable {9} head {10} neck {11} | {12} | bake {13}",
                Src, Pitch, Roll, Yaw, MaxPitch, MaxRoll, MaxYaw, Frames, m_busy, m_movable, m_head != null ? m_head.name : "-", m_neck != null ? m_neck.name : "-", Rig, BakeResult);
        }

        /// dealer rig        his animator and bones -> log
        /// dealer rig test   does writing the head bone move his mesh (bake with and without a nod)
        /// dealer deopt      open an optimized rig now
        /// dealer motion     the state
        string Command(string[] a)
        {
            string op = a.Length > 1 ? a[1] : "motion";
            Dealer d = Dealer.Instance;
            if (op == "rig")
            {
                if (d == null) return "no Dealer";
                Bones(d, false);
                if (a.Length > 2 && a[2] == "test") { m_bakeTest = 1; return "bake test on the next frame"; }
                Plugin.L.LogInfo("DealerMotion: rig report\n" + RigReport(d));
                return Rig + " | head " + (m_head != null ? VRUI.Path(m_head) : "-") + " neck " + (m_neck != null ? VRUI.Path(m_neck) : "-") + " (see log)";
            }
            if (op == "deopt")
            {
                if (d == null) return "no Dealer";
                Bones(d, false);
                return Deopt("test");
            }
            return Describe();
        }

        string RigReport(Dealer d)
        {
            var sb = new StringBuilder();
            foreach (Animator an in d.GetComponentsInChildren<Animator>(true))
            {
                sb.Append("  animator ").Append(Describe(an)).Append(an == m_anim ? " (in use)" : "").Append('\n');
                sb.Append("    layers: ");
                for (int i = 0; i < an.layerCount; i++) sb.Append(an.GetLayerName(i)).Append(' ');
                sb.Append("\n    children: ");
                foreach (Transform c in an.transform) sb.Append(c.name).Append(c.childCount > 0 ? "(" + c.childCount + ")" : "").Append(", ");
                sb.Append('\n');
            }
            foreach (SkinnedMeshRenderer smr in d.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Transform[] bones = smr.bones;
                int alive = bones != null ? bones.Count(b => b != null) : 0;
                string headish = bones != null ? string.Join(",", bones.Where(b => b != null && (b.name.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0 || b.name.IndexOf("Neck", StringComparison.OrdinalIgnoreCase) >= 0)).Select(b => b.name).ToArray()) : "";
                sb.Append("  skinned ").Append(VRUI.Path(smr.transform)).Append(" on ").Append(smr.enabled && smr.gameObject.activeInHierarchy).Append(" bones ").Append(bones != null ? bones.Length : -1)
                  .Append(" (").Append(alive).Append(" real) root ").Append(smr.rootBone != null ? smr.rootBone.name : "-").Append(" head/neck bones [").Append(headish).Append("]\n");
            }
            // the game hangs props on his attach nodes (Model.FindAttachNode: Prop1 for the deck, the hourglass...)
            try
            {
                Model model = d.Model;
                if (model != null && model.AttachNodes != null)
                    foreach (Model.AttachNode n in model.AttachNodes)
                        sb.Append("  attach node ").Append(n.Name).Append(" -> ").Append(n.Transform != null ? VRUI.Path(n.Transform) : "MISSING").Append('\n');
            }
            catch (Exception e) { sb.Append("  attach nodes: ").Append(e.Message).Append('\n'); }
            sb.Append("  head ").Append(m_head != null ? VRUI.Path(m_head) : "-").Append(" neck ").Append(m_neck != null ? VRUI.Path(m_neck) : "-");
            return sb.ToString();
        }

        /// The C# curves against motion9.curves() (60 Hz, gain 1): `narr motiontest <file.motion> <curves.tsv>`.
        public static string CurvesTest(string motionPath, string refPath)
        {
            ClipStore.Motion m = ClipStore.ReadMotion(motionPath);
            if (m == null) return "no motion file " + motionPath;
            if (!File.Exists(refPath)) return "no reference " + refPath;
            var inv = CultureInfo.InvariantCulture;
            var rows = File.ReadAllLines(refPath).Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('\t')).ToList();
            float p = 0, pv = 0, r = 0, rv = 0, y = 0, yv = 0, worst = 0f;
            const float dt = 1f / 60f;
            int n = 0, at = -1;
            foreach (string[] c in rows)
            {
                float t = float.Parse(c[0], inv);
                float tp, tr, ty;
                Targets(m, t, out tp, out tr, out ty);
                Spring(ref p, ref pv, tp, 14f, dt);
                Spring(ref r, ref rv, tr, 5f, dt);
                Spring(ref y, ref yv, ty, 4f, dt);
                float e = Mathf.Max(Mathf.Abs(p - float.Parse(c[1], inv)), Mathf.Max(Mathf.Abs(r - float.Parse(c[2], inv)), Mathf.Abs(y - float.Parse(c[3], inv))));
                if (e > worst) { worst = e; at = n; }
                n++;
            }
            return "motion curves: " + n + " frames, largest difference " + worst.ToString("0.0000", inv) + " deg (frame " + at + ") - " + (worst <= 0.05f ? "PASS" : "FAIL") + " (limit 0.05)";
        }
    }
}

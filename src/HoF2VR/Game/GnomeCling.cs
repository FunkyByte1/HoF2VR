using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Playtest 4 (P4-7, 2026-10-19): "I can't see a gnome when it's on me"; "I have no clue how to hit them when they are on
    /// me". A clinging gnome hangs on the hero's back in HoF2 (ActionSnareGoblin attaches it to the hero model's snare node) -
    /// behind the eyes in first person - and F8 hid it, so the player saw only a gold ring in the air. Now the gnome is shown
    /// in front of the player, clinging at chest height (its hold on the hero's back turned round to the front: the same pose,
    /// facing the player, ClingAhead m out and ClingBelow m under the eyes - past the spawn rule's 0.53 m), and the ring is
    /// drawn round its body: hitting the gnome is hitting the ring. Five legal cuts or thrusts through it knock it off (the
    /// user's design, decisions.md "Gnomes", 2026-10-17). Nothing of it is drawn within NearEyes of the eyes.
    internal static partial class GnomeFight
    {
        // where its body's middle is shown: MidAhead ahead of the eyes (flat), MidBelow under them (~1.0 m from the eyes, at the
        // chest; 0.85 let a pose that reaches out come to 0.37 m of the eyes with an arm). The game's mover node is its root bone, and where the middle sits from it changes with the pose (measured
        // 0.25-0.4 m further, up to 0.37 m aside): learnt while it clings (s_midOff, slowly - the cling's wiggle stays).
        public const float MidAhead = 0.92f, MidBelow = 0.45f, NearEyes = 0.45f;
        static readonly Vector3 MidOffGuess = new Vector3(0f, 0.15f, 0.3f);
        static Vector3 s_midOff = MidOffGuess, s_lastSpot;   // the middle from the root (view frame: right, up, ahead); the root's last spot
        public static bool ShowClinger = true;   // `vrgnomes cling front|hidden`
        /// The turn (deg) from its hold on the hero's back to the front: 180 = what faced the hero's forward faces the player.
        /// `vrgnomes clingturn <deg>` (test mode) to try others.
        public static float ClingTurn = 180f;
        public static int Posed, PosedMover, PosedRoot;
        public static string LastPose = "-";

        static readonly AccessTools.FieldRef<ActionSnareGoblin, string> s_moverNode = AccessTools.FieldRefAccess<ActionSnareGoblin, string>("m_moverAttachNode");
        static readonly AccessTools.FieldRef<ActionSnareGoblin, Controller> s_targetCtl = AccessTools.FieldRefAccess<ActionSnareGoblin, Controller>("m_targetController");
        static readonly AccessTools.FieldRef<ActionSnareGoblin, int> s_snareIndex = AccessTools.FieldRefAccess<ActionSnareGoblin, int>("m_snareIndex");
        static readonly AccessTools.FieldRef<ActionSnareGoblin, string[]> s_targetNodes = AccessTools.FieldRefAccess<ActionSnareGoblin, string[]>("m_targetAttachNodes");

        // where the clinging gnome is shown: the flat way ahead of the player chosen when it clung (again when they turn away)
        static Vector3 s_clingFwd = Vector3.forward;
        // pushed further out while the body's nearest point is under BodyFromEyes from the eyes (its shape on the hero's back
        // differs per pose; measured on the drawn body, a frame late), back to 0 for the next cling
        static float s_clingExtra;
        public const float BodyFromEyes = 0.6f, MaxExtra = 0.6f;
        static readonly Dictionary<int, PoseKeep> s_keep = new Dictionary<int, PoseKeep>();

        sealed class PoseKeep { public Transform Root; public Vector3 LocalPos; public Quaternion LocalRot; }

        static void ApplyCling(Harmony h)
        {
            try { h.Patch(AccessTools.Method(typeof(ActionSnareGoblin), "UpdateAttach"), postfix: new HarmonyMethod(typeof(GnomeFight), nameof(UpdateAttachPostfix))); }
            catch (Exception e) { Plugin.L.LogWarning("GnomeFight: cling patch failed: " + e.Message); }
        }

        /// The map from the hero's body to the front: the hero's hold point (pivot) goes to the spot ahead of the eyes, turned so
        /// that what faced the hero's forward faces the player.
        static bool ClingMap(Controller hero, Transform holdNode, out Vector3 pivot, out Vector3 spot, out Quaternion turn)
        {
            pivot = spot = Vector3.zero;
            turn = Quaternion.identity;
            if (VRRig.I == null || hero == null) return false;
            Vector3 head = VRRig.I.HeadWorldPos;
            pivot = holdNode != null && holdNode != hero.transform ? holdNode.position : hero.transform.position + Vector3.up * 1.25f;
            Vector3 hf = hero.transform.forward;
            hf.y = 0f;
            if (hf.sqrMagnitude < 1e-4f) hf = Vector3.forward;
            float heroYaw = Mathf.Atan2(hf.x, hf.z) * Mathf.Rad2Deg, viewYaw = Mathf.Atan2(s_clingFwd.x, s_clingFwd.z) * Mathf.Rad2Deg;
            turn = Quaternion.Euler(0f, viewYaw + ClingTurn - heroYaw, 0f);
            Vector3 right = Vector3.Cross(Vector3.up, s_clingFwd);
            spot = new Vector3(head.x, head.y - MidBelow, head.z) + s_clingFwd * (MidAhead + s_clingExtra) - (right * s_midOff.x + Vector3.up * s_midOff.y + s_clingFwd * s_midOff.z);
            s_lastSpot = spot;
            return true;
        }

        static Transform HoldNode(ActionSnareGoblin a, Controller hero)
        {
            try
            {
                string[] tn = s_targetNodes(a);
                int i = s_snareIndex(a);
                if (hero == null || hero.Model == null || tn == null || tn.Length == 0) return null;
                return hero.Model.FindAttachNode(tn[Mathf.Clamp(i, 0, tn.Length - 1)]);
            }
            catch (Exception) { return null; }
        }

        /// The game has just put the gnome's mover node on the hero's hold node (PlayerController's late update): turn it round
        /// to the front.
        static void UpdateAttachPostfix(ActionSnareGoblin __instance)
        {
            if (!ShowClinger || !Active || __instance == null) return;
            try
            {
                G g;
                if (!s_live.TryGetValue(__instance.GetInstanceID(), out g) || g.Result != null || g.Ai == null || g.Ai.Model == null) return;
                Controller hero = s_targetCtl(__instance);
                Transform mover = g.Ai.Model.FindAttachNode(s_moverNode(__instance));
                Vector3 pivot, spot;
                Quaternion turn;
                if (mover == null || !ClingMap(hero, HoldNode(__instance, hero), out pivot, out spot, out turn)) return;
                mover.SetPositionAndRotation(spot + turn * (mover.position - pivot), turn * mover.rotation);
                g.Posed = true;
                g.PosedFrames++;
                PosedMover++;
                // measured here, right after the placing (in Tick the bones may still hold the animation's pose on the ground:
                // the game's late update and ours run in either order)
                if (VRRig.I != null) { Learn(g, VRRig.I.HeadWorldPos); HideNear(g, VRRig.I.HeadWorldPos, BodyOf(g.Ai)); }
            }
            catch (Exception) { }
        }

        /// Every frame while it clings (Tick): a gnome without a mover node (the game moves its whole body to the hero) is moved
        /// to the front by its model's root; then what is near the eyes is hidden, the rest shown.
        static void PoseClinger(G g, Vector3 head)
        {
            if (!ShowClinger || g.Ai == null || g.A == null) { Hide(g, true); return; }
            string mn = "";
            try { mn = s_moverNode(g.A); } catch (Exception) { }
            if (string.IsNullOrEmpty(mn) && g.Ai.Model != null)
            {
                Transform root = g.Ai.Model.transform;
                int id = g.Ai.GetInstanceID();
                PoseKeep k;
                if (!s_keep.TryGetValue(id, out k) || k.Root != root) s_keep[id] = k = new PoseKeep { Root = root, LocalPos = root.localPosition, LocalRot = root.localRotation };
                Controller hero = null;
                try { hero = s_targetCtl(g.A); } catch (Exception) { }
                Vector3 pivot, spot;
                Quaternion turn;
                if (root.parent != null && ClingMap(hero, HoldNode(g.A, hero), out pivot, out spot, out turn))
                {
                    Vector3 p0 = root.parent.TransformPoint(k.LocalPos);
                    Quaternion r0 = root.parent.rotation * k.LocalRot;
                    root.SetPositionAndRotation(spot + turn * (p0 - pivot), turn * r0);
                    g.Posed = true;
                    PosedRoot++;
                    g.PosedFrames++;
                    Learn(g, head);
                }
                HideNear(g, head, BodyOf(g.Ai));
            }
        }
        static float s_bodyNear = -1f;
        static Vector3 s_clingMid;
        static int s_clingMidFrame = -10;

        /// Right after it is placed: where its body's middle is from its root (learnt slowly: the cling's wiggle stays), and
        /// its nearest point from the eyes at least BodyFromEyes (the spawn rule: 0.53 m) - pushed further out if not. From its
        /// 4th placed frame (its bones: a skinned body's bounds are the last drawn frame's, frozen while looked away from - a
        /// loop on them ran the gnome 8 m off).
        static void Learn(G g, Vector3 head)
        {
            Vector3 bm;
            float d;
            if (g.PosedFrames <= 3 || !Skeleton(g, head, out bm, out d)) return;
            Vector3 m = bm - s_lastSpot, right = Vector3.Cross(Vector3.up, s_clingFwd);
            Vector3 want = new Vector3(Mathf.Clamp(Vector3.Dot(m, right), -0.5f, 0.5f), Mathf.Clamp(m.y, -0.3f, 0.6f), Mathf.Clamp(Vector3.Dot(m, s_clingFwd), -0.3f, 0.7f));
            s_midOff = Vector3.Lerp(s_midOff, want, 0.08f);
            if (d < BodyFromEyes) s_clingExtra = Mathf.Min(MaxExtra, s_clingExtra + (BodyFromEyes - d) * 0.5f);
            else if (d > BodyFromEyes + 0.1f) s_clingExtra = Mathf.Max(0f, s_clingExtra - (d - BodyFromEyes - 0.1f) * 0.2f);
            s_bodyNear = d;
            s_clingMid = bm;
            s_clingMidFrame = Time.frameCount;
        }

        /// Its largest skinned renderer (GnomeFight.Center's cache, refreshed once a second).
        static Renderer BodyOf(Controller c)
        {
            if (c == null) return null;
            Center(c);
            Renderer r;
            return s_body.TryGetValue(c.GetInstanceID(), out r) ? r : null;
        }

        /// The gnome drawn, but no part of it within NearEyes of the eyes (its spear or a limb as it shakes); the body itself is
        /// kept out by the push (PoseClinger), never hidden.
        static void HideNear(G g, Vector3 head, Renderer body)
        {
            if (g.Ai == null) return;
            try
            {
                g.Ai.GetComponentsInChildren<Renderer>(true, s_rtmp);
                for (int i = 0; i < s_rtmp.Count; i++)
                {
                    Renderer r = s_rtmp[i];
                    if (r == null || r == body) continue;
                    bool near = Mathf.Sqrt(r.bounds.SqrDistance(head)) < NearEyes;
                    bool ours = g.HiddenR.Contains(r);
                    if (near && r.enabled) { r.enabled = false; if (!ours) g.HiddenR.Add(r); }
                    else if (!near && ours && !r.enabled) { r.enabled = true; g.HiddenR.Remove(r); }
                }
                s_rtmp.Clear();
                g.Hidden = g.HiddenR.Count > 0;
            }
            catch (Exception) { }
        }

        /// Its body back where the game keeps it (the cling is over).
        static void EndPose(G g)
        {
            if (g == null || g.Ai == null) return;
            int id = g.Ai.GetInstanceID();
            PoseKeep k;
            if (s_keep.TryGetValue(id, out k))
            {
                try { if (k.Root != null) { k.Root.localPosition = k.LocalPos; k.Root.localRotation = k.LocalRot; } } catch (Exception) { }
                s_keep.Remove(id);
            }
            g.Posed = false;
            g.PosedFrames = 0;
        }

        /// The body's middle (the mean of its bones), for the ring.
        static bool BodyMiddle(G g, out Vector3 mid)
        {
            float near;
            mid = Vector3.zero;
            if (g != null && g == s_clinger && g.Posed && Time.frameCount - s_clingMidFrame <= 2) { mid = s_clingMid; return true; }   // as placed
            return g != null && Skeleton(g, VRRig.I != null ? VRRig.I.HeadWorldPos : Vector3.zero, out mid, out near);
        }

        // the bones that move with its mover node (the skinned body's bone list also holds ones that stay with the actor on the
        // ground - their mean pulled the "middle" 1.7 m down and the learning flew the gnome up and away)
        static readonly Dictionary<int, Transform[]> s_poseBones = new Dictionary<int, Transform[]>();

        static Transform[] PoseBones(G g)
        {
            if (g == null || g.Ai == null) return null;
            int id = g.Ai.GetInstanceID();
            Transform[] got;
            if (s_poseBones.TryGetValue(id, out got) && got != null && got.Length > 0 && got[0] != null) return got;
            SkinnedMeshRenderer sk = BodyOf(g.Ai) as SkinnedMeshRenderer;
            Transform[] all = sk != null ? sk.bones : null;
            if (all == null) return null;
            Transform mover = null;
            try { mover = g.A != null && g.Ai.Model != null ? g.Ai.Model.FindAttachNode(s_moverNode(g.A)) : null; } catch (Exception) { }
            var keep = new List<Transform>();
            foreach (Transform b in all) if (b != null && (mover == null || mover == g.Ai.Model.transform || b.IsChildOf(mover))) keep.Add(b);
            got = keep.Count > 0 ? keep.ToArray() : all;
            s_poseBones[id] = got;
            if (s_poseBones.Count > 32) s_poseBones.Clear();
            return got;
        }

        /// From its skinned body's bones that move with it (they follow the pose at once; the renderer's bounds are a drawn
        /// frame's): their mean, and the nearest one's distance from the eyes less 0.1 m of flesh.
        static bool Skeleton(G g, Vector3 head, out Vector3 mid, out float near)
        {
            mid = Vector3.zero;
            near = 99f;
            Transform[] bones = PoseBones(g);
            Renderer r = g != null ? BodyOf(g.Ai) : null;
            if (bones == null || bones.Length == 0) { if (r == null) return false; mid = r.bounds.center; near = Mathf.Sqrt(r.bounds.SqrDistance(head)); return true; }
            int n = 0;
            for (int i = 0; i < bones.Length; i++)
            {
                Transform b = bones[i];
                if (b == null) continue;
                Vector3 p = b.position;
                mid += p;
                n++;
                near = Mathf.Min(near, (p - head).magnitude);
            }
            if (n == 0) return false;
            mid /= n;
            near = Mathf.Max(0f, near - 0.1f);
            return true;
        }

        static G s_clinger;

        /// For `gnomes?`: the clinging gnome's display (its body's middle in tracking space, its distance from the eyes, how
        /// many of its renderers are hidden near the eyes) and the counts.
        public static string ClingQuery()
        {
            string mid = "-";
            Vector3 m;
            if (s_clinger != null && VRRig.I != null && BodyMiddle(s_clinger, out m))
                mid = Diag.V(VRRig.I.FromWorld(m)) + " eyes " + (m - VRRig.I.HeadWorldPos).magnitude.ToString("0.00") + " hiddenNear " + s_clinger.HiddenR.Count + " posedNow " + s_clinger.Posed +
                      " nearest " + s_bodyNear.ToString("0.00") + " pushed " + s_clingExtra.ToString("0.00") + " midOff " + Diag.V(s_midOff) +
                      " bones " + (PoseBones(s_clinger) != null ? PoseBones(s_clinger).Length : 0);
            return "cling " + (ShowClinger ? "front" : "hidden") + " posed " + Posed + " mover " + PosedMover + " root " + PosedRoot + " midT " + mid + " lastPose " + LastPose.Replace(" | ", " ");
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// UYF's block lean and recoil (docs/uyf-parity-audit.md E20, E23; item 18), drawn on HoF2's enemies, which have neither:
    ///  * an enemy whose raised block is hit leans its upper body toward the struck point (UYF blends its block pose by
    ///    BlockX / BlockY): up to 10 deg sideways and 6 deg back for a high hit;
    ///  * an attacker whose blow the player blocked recoils: its upper body thrown back 12 deg (UYF plays a recoil clip; the
    ///    0.3 m push stays).
    /// Both in 0.08 s and back over 0.25 s, as an extra turn of the upper spine after the animator (LateUpdate): the game's
    /// own animation and AI are untouched. `bodylean? [test <enemy>]`.
    internal class BodyLean : MonoBehaviour
    {
        public static BodyLean I;
        public const float In = 0.08f, Out = 0.25f, RecoilDeg = 12f, LeanDeg = 10f, LeanBackDeg = 6f;
        sealed class Lean { public Controller C; public Transform Bone; public float Roll, Pitch, Start; public bool Set; public Quaternion BaseLocal, SetLocal; }
        readonly List<Lean> m_leans = new List<Lean>();
        readonly Dictionary<int, Transform> m_spine = new Dictionary<int, Transform>();
        public static int Leans, Recoils, NoBone;
        public static float LastAngle;
        public static string Last = "-";

        void Awake() { I = this; }

        /// The upper spine: the deepest "spine" / "chest" bone (not a twist bone) - the one the shoulders hang from.
        Transform Spine(Controller c)
        {
            int id = c.GetInstanceID();
            Transform t;
            if (m_spine.TryGetValue(id, out t)) return t;
            t = null;
            // the neck's parent first (a Biped's top spine bone: the clavicles hang from the neck), else the deepest spine,
            // else the deepest chest bone - never a cloth, cape or twist bone (MR3d: an empire soldier's
            // "Bone_Cloth_Front_Chest02" was taken - its tabard flapped instead of its body)
            Transform neck = null, spine = null, chest = null;
            int bestSpine = -1, bestChest = -1;
            if (c.Model != null)
                foreach (Transform b in c.Model.GetComponentsInChildren<Transform>(true))
                {
                    string n = b.name.ToLowerInvariant();
                    if (n.Contains("cloth") || n.Contains("cape") || n.Contains("skirt") || n.Contains("twist")) continue;
                    if (n.Contains("neck"))
                    {
                        string pn = b.parent != null ? b.parent.name.ToLowerInvariant() : "";
                        if (neck == null || ((pn.Contains("spine") || pn.Contains("chest")) && !neck.parent.name.ToLowerInvariant().Contains("spine"))) neck = b;
                        continue;
                    }
                    bool isSpine = n.Contains("spine");
                    if (!isSpine && !n.Contains("chest")) continue;
                    int depth = 0;
                    for (Transform p = b.parent; p != null; p = p.parent) depth++;
                    if (isSpine && depth > bestSpine) { bestSpine = depth; spine = b; }
                    if (!isSpine && depth > bestChest) { bestChest = depth; chest = b; }
                }
            t = neck != null && neck.parent != null ? neck.parent : spine != null ? spine : chest;
            m_spine[id] = t;
            return t;
        }

        void Add(Controller c, float roll, float pitch, string what)
        {
            if (c == null) return;
            Transform bone = Spine(c);
            if (bone == null) { NoBone++; Last = what + ": no spine bone on " + WeaponGrips.ModelKey(c.name); return; }
            for (int i = m_leans.Count - 1; i >= 0; i--)
            {
                Lean o = m_leans[i];
                if (o.C != c) continue;
                if (o.Set && o.Bone != null && Quaternion.Angle(o.Bone.localRotation, o.SetLocal) < 0.01f) o.Bone.localRotation = o.BaseLocal;   // its turn off an unwritten bone
                m_leans.RemoveAt(i);
            }
            m_leans.Add(new Lean { C = c, Bone = bone, Roll = roll, Pitch = pitch, Start = Time.unscaledTime });
            Last = what + " " + WeaponGrips.ModelKey(c.name) + " (" + bone.name + ") roll " + roll.ToString("0.0") + " pitch " + pitch.ToString("0.0");
        }

        /// BladeHits.Deflect: the blocking enemy struck at `point` (world).
        public static void Struck(Targetable t, Vector3 point)
        {
            if (I == null || t == null || t.Controller == null) return;
            try
            {
                Transform root = t.Controller.transform;
                Transform bone = I.Spine(t.Controller);
                Vector3 local = root.InverseTransformPoint(point) - (bone != null ? root.InverseTransformPoint(bone.position) : Vector3.up * 1.2f);
                // toward the struck side: the top of the body to the hit's side (a roll about its forward), back for a high hit
                float roll = -Mathf.Clamp(local.x / 0.4f, -1f, 1f) * LeanDeg;
                float pitch = -Mathf.Clamp01(local.y / 0.4f) * LeanBackDeg;
                I.Add(t.Controller, roll, pitch, "lean");
                Leans++;
            }
            catch (Exception e) { Plugin.L.LogWarning("BodyLean: struck: " + e.Message); }
        }

        /// Guard.Blocked: the attacker whose blow was blocked recoils.
        public static void Recoil(Controller c)
        {
            if (I == null || c == null) return;
            I.Add(c, 0f, -RecoilDeg, "recoil");
            Recoils++;
        }

        void LateUpdate()
        {
            float now = Time.unscaledTime;
            for (int i = m_leans.Count - 1; i >= 0; i--)
            {
                Lean l = m_leans[i];
                float age = now - l.Start;
                // the turn goes on the pose the animator wrote this frame; a bone it did not write (culled, or no curve for it)
                // still holds last frame's turned pose - the turn is put on that frame's own pose, not stacked
                bool unwritten = l.Bone != null && l.Set && Quaternion.Angle(l.Bone.localRotation, l.SetLocal) < 0.01f;
                if (l.C == null || l.Bone == null || age > In + Out)
                {
                    if (unwritten) l.Bone.localRotation = l.BaseLocal;
                    m_leans.RemoveAt(i);
                    continue;
                }
                Quaternion baseLocal = unwritten ? l.BaseLocal : l.Bone.localRotation;
                float k = age < In ? Mathf.SmoothStep(0f, 1f, age / In) : 1f - Mathf.SmoothStep(0f, 1f, (age - In) / Out);
                Transform root = l.C.transform;
                // (Unity: AngleAxis(+a, right) turns up toward forward; AngleAxis(+a, forward) turns the top toward -x)
                Quaternion turn = Quaternion.AngleAxis(l.Pitch * k, root.right) * Quaternion.AngleAxis(l.Roll * k, root.forward);
                Quaternion parent = l.Bone.parent != null ? l.Bone.parent.rotation : Quaternion.identity;
                l.Bone.localRotation = Quaternion.Inverse(parent) * (turn * (parent * baseLocal));
                l.BaseLocal = baseLocal;
                l.SetLocal = l.Bone.localRotation;
                l.Set = true;
                LastAngle = Mathf.Max(Mathf.Abs(l.Pitch), Mathf.Abs(l.Roll)) * k;
            }
        }

        public static string Query(string[] a)
        {
            if (a.Length > 2 && a[1] == "test" && I != null)
            {
                Targetable t = Physical.FindEnemy(a[2]);
                if (t == null || t.Controller == null) return "no enemy '" + a[2] + "'";
                Recoil(t.Controller);
            }
            return "leans " + Leans + " recoils " + Recoils + " no bone " + NoBone + " live " + (I != null ? I.m_leans.Count : 0) + " angle now " + LastAngle.ToString("0.0") + " | " + Last;
        }
    }
}

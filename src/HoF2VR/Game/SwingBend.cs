using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// The thieves' low sweep bent up to chest height (the user, 2026-10-13 07:15, on "keep the thieves' low bar, or bend
    /// their swings up to chest height?": "Bend them up").
    /// The thieves' run attack (`m_rig_e_gen_attack_run_attack`: thug, poisoner, assassin) sweeps the blade across at hip
    /// height - 0.93-0.97 m above the thief's feet, about 0.8 m below a standing player's eyes (combat Stage 1's
    /// measurement). The swing itself is lifted, not just the bar: while that clip plays, the weapon arm turns up about the
    /// shoulder - the axis square to the upper arm and the vertical - by up to Angle, easing in over the wind-up, full from
    /// FullFrom s before the clip's hit event to FullTo s after it, easing out by the clip's end. The hand, the blade and the
    /// game's weapon trail follow the arm, so the blade passes at chest height; the bar keeps following the real path (the
    /// swing atlas was measured with the bend on). Applied before every other LateUpdate (the swing trace, the guard's live
    /// test and the atlas's run-time learning see the bent arm). Physical mode only; the game's hit itself (an animation
    /// event at the attacker's reach) is unchanged, so a player who does not block is still hit.
    /// Seams: `swingbend on|off|<deg>` (the angle, live), `swingbend?` (bent attackers, the arm bones found).
    [DefaultExecutionOrder(-800)]
    internal class SwingBend : MonoBehaviour
    {
        public static readonly string[] Clips = { "m_rig_e_gen_attack_run_attack" };
        public static float Angle = 25f, FullFrom = 0.35f, FullTo = 0.1f, EaseIn = 0.35f, EaseOut = 0.3f;
        public static bool Enabled = true;
        public static int Frames, Attackers, Missing;
        public static float LastWeight;
        public static string Bones = "-";

        sealed class Rig { public Transform Upper, Fore, Hand; public bool Done; public float Found; }
        readonly Dictionary<int, Rig> m_rigs = new Dictionary<int, Rig>();
        readonly HashSet<int> m_seen = new HashSet<int>();

        void Awake()
        {
            Diag.Commands["swingbend"] = a =>
            {
                if (a.Length > 1)
                {
                    float deg;
                    if (a[1] == "on") Enabled = true;
                    else if (a[1] == "off") Enabled = false;
                    else if (float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out deg)) Angle = deg;
                }
                return "swing bend " + (Enabled ? "on" : "off") + " " + Angle.ToString("0") + " deg";
            };
            Diag.Commands["swingbend?"] = a =>
            {
                if (a.Length > 1)
                {
                    // `swingbend? <enemy>`: find that enemy's weapon arm now (whatever it plays)
                    Targetable t = Physical.FindEnemy(a[1]);
                    if (t == null || t.Controller == null || t.Controller.Model == null) return "no enemy";
                    Find(t.Controller);
                }
                return "swing bend " + (Enabled ? "on" : "off") + " " + Angle.ToString("0") + " deg, frames " + Frames + ", attackers " + Attackers + ", missing " + Missing +
                       ", last weight " + LastWeight.ToString("0.00") + " | bones " + Bones;
            };
        }

        /// The bend's weight for this attacker now: 0 unless it plays a bent clip; eased in over the wind-up up to FullFrom s
        /// before the hit event, full until FullTo s after it, eased out over EaseOut.
        static readonly List<AnimatorClipInfo> s_infos = new List<AnimatorClipInfo>();
        static readonly Dictionary<int, float> s_clipEv = new Dictionary<int, float>();

        /// A clip's "hit" event (s; half its length without one), -2 when it is not one of Clips - read once per clip: the
        /// clip's name and its events array are new garbage on every read (with the clip infos' array, most of SwingBend's
        /// 48 KB/s in MR2d's `profile alloc`).
        static float ClipEvent(AnimationClip clip)
        {
            int id = clip.GetInstanceID();
            float ev;
            if (s_clipEv.TryGetValue(id, out ev)) return ev;
            ev = -2f;
            if (Array.IndexOf(Clips, clip.name) >= 0)
            {
                ev = -1f;
                foreach (AnimationEvent e in clip.events) if (e.stringParameter == "hit") { ev = e.time; break; }
                if (ev < 0f) ev = clip.length * 0.5f;
            }
            s_clipEv[id] = ev;
            return ev;
        }

        static float Weight(Animator anim)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool next = pass == 1;
                if (next && !anim.IsInTransition(0)) break;
                AnimatorStateInfo st = next ? anim.GetNextAnimatorStateInfo(0) : anim.GetCurrentAnimatorStateInfo(0);
                s_infos.Clear();
                if (next) anim.GetNextAnimatorClipInfo(0, s_infos); else anim.GetCurrentAnimatorClipInfo(0, s_infos);
                for (int k = 0; k < s_infos.Count; k++)
                {
                    AnimatorClipInfo ci = s_infos[k];
                    AnimationClip clip = ci.clip;
                    if (clip == null || ci.weight < 0.05f || clip.length <= 0.01f) continue;
                    float ev = ClipEvent(clip);
                    if (ev < -1.5f) continue;
                    float t = (st.normalizedTime - Mathf.Floor(st.normalizedTime)) * clip.length;
                    if (st.normalizedTime >= 1f && !st.loop) t = clip.length;
                    float w;
                    if (t < ev - FullFrom) w = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ev - FullFrom - EaseIn, ev - FullFrom, t));
                    else if (t <= ev + FullTo) w = 1f;
                    else w = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ev + FullTo, ev + FullTo + EaseOut, t));
                    return w * Mathf.Clamp01(ci.weight * (next ? 1f : 1f));
                }
            }
            return 0f;
        }

        static bool HandName(string n)
        {
            n = n.ToLowerInvariant();
            return n.Contains("hand") && !n.Contains("finger") && !n.Contains("thumb") && !n.Contains("handle") && !n.Contains("ik") && !n.Contains("target") &&
                   !n.Contains("cards");
        }

        /// A right-side bone by its name ("Bip01 R Hand", "hand_r", "RightHand", "r_hand").
        static bool RightSide(string n)
        {
            n = " " + n.ToLowerInvariant().Replace('_', ' ').Replace('.', ' ') + " ";
            return n.Contains(" r ") || n.Contains("right");
        }

        static float SegDist(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector3.Distance(p, a + ab * t);
        }

        /// The weapon arm of this attacker: the hand bone holding its weapon (the striking part EnemyWeapon finds; a skinned
        /// sword's transform is its root bone, not a child of the hand, so the hand is the bone named like one nearest the
        /// blade's line), then the forearm and the upper arm above it (by name where the rig names them, else the hand's
        /// parent and grandparent).
        static Rig Find(Controller c)
        {
            var r = new Rig { Done = true };
            try
            {
                // each weapon (meshes only: a trail follows one of them) with the hand bone nearest its line; the run attack
                // swings the right hand's sabre (Stage 1's trace: "Thief_Sabre_Sword_Long"; the left holds the short one or
                // a second sabre, "... (1)"), so a right hand wins, then the first found
                EnemyWeapon.Part wp = null;
                float best = 0.3f;
                bool bestRight = false;
                Transform[] bones = c.Model.GetComponentsInChildren<Transform>(true);
                foreach (EnemyWeapon.Part p in EnemyWeapon.Sample(c.Model))
                {
                    if (p.T == null || p.Kind != "mesh") continue;
                    if (p.Name != null && p.Name.ToLowerInvariant().Contains("shield")) continue;
                    Transform hand = null;
                    float near = 0.3f;
                    foreach (Transform t in bones)
                    {
                        if (t.parent == null || !HandName(t.name)) continue;
                        float d = SegDist(t.position, p.WA, p.WB);
                        if (d < near) { near = d; hand = t; }
                    }
                    if (hand == null) continue;
                    bool right = RightSide(hand.name);
                    if (r.Hand == null || (right && !bestRight)) { r.Hand = hand; best = near; bestRight = right; wp = p; }
                }
                if (r.Hand != null)
                {
                    Transform t = r.Hand.parent;
                    for (int i = 0; i < 4 && t != null && t != c.Model.transform; i++, t = t.parent)
                    {
                        string n = t.name.ToLowerInvariant();
                        if (r.Fore == null && (n.Contains("fore") || n.Contains("lowerarm") || n.Contains("lower_arm") || n.Contains("elbow"))) { r.Fore = t; continue; }
                        if (n.Contains("upperarm") || n.Contains("upper_arm") || n.Contains("uparm") || (n.Contains("arm") && !n.Contains("fore") && !n.Contains("lower") && !n.Contains("twist")))
                        { r.Upper = t; break; }
                    }
                    if (r.Fore == null) r.Fore = r.Hand.parent;
                    if (r.Upper == null && r.Fore != null) r.Upper = r.Fore.parent;
                }
                var chain = new System.Text.StringBuilder();
                for (Transform t = r.Hand; t != null && t != c.Model.transform && chain.Length < 200; t = t.parent) chain.Append(t.name).Append('<');
                Bones = WeaponGrips.ModelKey(c.Model.name) + ": weapon " + (wp != null ? wp.Kind + ":" + wp.Name + " on " + wp.T.name : "-") + " hand " + (r.Hand != null ? r.Hand.name + " (" + best.ToString("0.00") + " m)" : "-") +
                        " fore " + (r.Fore != null ? r.Fore.name : "-") + " upper " + (r.Upper != null ? r.Upper.name : "-") + " | " + chain;
            }
            catch (Exception e) { Bones = "error " + e.Message; }
            return r;
        }

        readonly Dictionary<int, bool> m_thief = new Dictionary<int, bool>();

        void LateUpdate()
        {
            if (!Enabled || !Physical.On || PlayerController.Instance == null) return;
            Targetable me = PlayerController.Instance.Targetable;
            if (me == null) return;
            BetterList<Targetable> all = me.EnumerateWithinRadius(25f, me.GetEnemyTeams());
            for (int i = 0; i < all.size; i++)
            {
                Targetable t = all[i];
                Controller c = t != null ? t.Controller : null;
                if (c == null || c.Model == null || c.Animator == null || c.Animator.Animator == null) continue;
                // a thief? (once per enemy: Object.name makes a new string on every read)
                bool thief;
                int cid = c.GetInstanceID();
                if (!m_thief.TryGetValue(cid, out thief))
                {
                    thief = t.name.StartsWith("Thieves", StringComparison.Ordinal) || c.Model.name.StartsWith("Thieves", StringComparison.Ordinal);
                    m_thief[cid] = thief;
                }
                if (!thief) continue;
                float w;
                try { w = Weight(c.Animator.Animator); } catch (Exception) { continue; }
                if (w <= 0.001f) continue;
                int id = c.GetInstanceID();
                Rig r;
                if (!m_rigs.TryGetValue(id, out r) || (r.Hand == null && Time.unscaledTime - r.Found > 1f)) { r = Find(c); r.Found = Time.unscaledTime; m_rigs[id] = r; }
                if (r.Upper == null || r.Hand == null) { Missing++; continue; }
                // up about the shoulder: the axis square to the upper arm's line (shoulder -> hand) and the vertical
                Vector3 arm = r.Hand.position - r.Upper.position;
                Vector3 axis = Vector3.Cross(arm, Vector3.up);
                if (axis.sqrMagnitude < 1e-6f) continue;
                r.Upper.rotation = Quaternion.AngleAxis(Angle * w, axis.normalized) * r.Upper.rotation;   // (Unity: about Cross(arm, up) by + turns the arm up)
                Frames++;
                LastWeight = w;
                if (m_seen.Add(id)) Attackers++;
            }
        }
    }
}

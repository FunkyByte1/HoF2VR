using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// `handsize` (2026-10-20, the user: "the hands in game are too big ... the same size of hands in HL:A"): how big the drawn
    /// glove is to the player's eyes and next to the world. Everything in real centimetres as the player perceives it: a
    /// world length divided by k, the world units per real metre the eyes are set apart by (the primary camera's two view
    /// matrices against the runtime's own eye separation). The glove: its hand length (wrist joint -> straight middle
    /// fingertip, 18.83 cm in its own frame) and its whole length (the cuff's back end -> the fingertip) times its lossy
    /// scale. The world: the table's cards (the map's slots), the hero figurine, the table top, the Dealer's and the hero's own
    /// hand (wrist -> middle fingertip joint, the humanoid bones; the glove's is 16.25 cm), the held weapon and shield.
    internal static class HandSize
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        // the glove's own frame (cm): wrist joint -> middle MCP 8.85, bones 4.4 / 3.0 / 1.75 + the tip cap 0.83
        const float GloveHand = 18.83f, GloveToDip = 16.25f;

        public static void Register()
        {
            Diag.Commands["handsize"] = a =>
            {
                string s;
                try { s = Describe(); }
                catch (System.Exception e) { s = "handsize failed: " + e.Message; }
                Plugin.L.LogInfo("handsize: " + s);
                return s;
            };
        }

        static string Cm(float m) => (m * 100f).ToString("0.0", Inv);

        static string Describe()
        {
            var sb = new StringBuilder();
            VRRig rig = VRRig.I;
            float scale = rig != null ? rig.Scale : 1f;
            // the eyes: world separation from the primary camera's stereo view matrices vs the runtime's separation
            float ipd = (InputTracking.GetLocalPosition(XRNode.RightEye) - InputTracking.GetLocalPosition(XRNode.LeftEye)).magnitude;
            Camera cam = rig != null ? rig.PrimaryCamera : null;
            float sepWorld = -1f;
            if (cam != null && cam.stereoEnabled)
            {
                Vector3 l = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse.MultiplyPoint(Vector3.zero);
                Vector3 r = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse.MultiplyPoint(Vector3.zero);
                sepWorld = (r - l).magnitude;
            }
            float k = sepWorld > 1e-5f && ipd > 1e-4f ? sepWorld / ipd : scale;
            sb.AppendFormat(Inv, "place {0} fight {1} | rig scale {2:0.000} | eyes: runtime {3} cm apart, {4} world u on '{5}' -> k {6:0.000} u per real m ({7})",
                Places.Current, CombatVR.Active, scale, Cm(ipd), sepWorld > 0f ? (sepWorld * 100f).ToString("0.00", Inv) + " cu" : "-", cam != null ? cam.name : "-", k,
                sepWorld > 0f ? "measured" : "the rig scale");
            // the gloves
            foreach (bool left in new[] { true, false })
            {
                Transform g = VRHands.GloveRoot(left);
                if (g == null) { sb.Append(left ? " | glove L hidden" : " | glove R hidden"); continue; }
                float ls = g.lossyScale.x;
                float back = HandGeometry.BackEndCm;   // the cuff's back end behind the wrist joint (cm)
                sb.AppendFormat(Inv, " | glove {0}: lossy {1:0.000}, to the eyes x{2:0.000}: hand {3} cm, whole {4} cm (the cuff {5} cm behind the wrist)",
                    left ? "L" : "R", ls, ls / k, (GloveHand * ls / k).ToString("0.0", Inv), ((GloveHand + back) * ls / k).ToString("0.0", Inv), (back * ls / k).ToString("0.0", Inv));
            }
            // the table
            Map map = Map.Instance;
            if (!CombatVR.Active && map != null && map.MapLayout != null)
            {
                float w = 0f, d = 0f;
                int n = 0;
                foreach (MapLayoutSlot slot in map.MapLayout.Slots)
                {
                    if (slot == null || !slot.gameObject.activeInHierarchy) continue;
                    Collider c = slot.GetComponent<Collider>();
                    if (c == null) continue;
                    Vector3 sz = c.bounds.size;
                    w += Mathf.Min(sz.x, sz.z); d += Mathf.Max(sz.x, sz.z); n++;
                }
                if (n > 0) sb.AppendFormat(Inv, " | map cards (slot boxes, {0}): {1} x {2} cm", n, Cm(w / n / k), Cm(d / n / k));
                CardRenderers(sb, map, k);
                Transform fig = FigurineGrab.Figurine();
                if (fig != null)
                {
                    int fn;
                    Bounds fb = TableGeo.RendererBounds(fig.gameObject, out fn);
                    if (fn > 0) sb.AppendFormat(Inv, " | figurine {0} cm tall, {1} x {2} cm", Cm(fb.size.y / k), Cm(fb.size.x / k), Cm(fb.size.z / k));
                }
            }
            CardTable table = CardTable.Instance;
            if (!CombatVR.Active && table != null && table.Prop != null)
            {
                int tn;
                Bounds tb = TableGeo.RendererBounds(table.Prop.gameObject, out tn);
                if (tn > 0) sb.AppendFormat(Inv, " | table prop {0} x {1} cm, top at {2} cm", Cm(tb.size.x / k), Cm(tb.size.z / k), Cm(tb.max.y / k));
            }
            Dealer dealer = Dealer.Instance;
            if (!CombatVR.Active && dealer != null) HandOf(sb, "Dealer", dealer.GetComponentInChildren<Animator>(), k);
            PlayerController pc = PlayerController.Instance;
            if (pc != null && pc.Model != null) HandOf(sb, "hero", pc.Model.Animator, k);
            // the held weapon and shield (their meshes' own size, not the turned world box)
            if (CombatVR.Active && CombatVR.I != null)
                foreach (Transform mount in new[] { CombatVR.I.HandR, CombatVR.I.HandL })
                {
                    if (mount == null) continue;
                    foreach (MeshFilter mf in mount.GetComponentsInChildren<MeshFilter>())
                    {
                        if (mf == null || mf.sharedMesh == null || mf.name.StartsWith("HoF2VR")) continue;
                        Renderer r = mf.GetComponent<Renderer>();
                        if (r == null || !r.enabled) continue;
                        Vector3 sz = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
                        sb.AppendFormat(Inv, " | held {0} '{1}': {2} x {3} x {4} cm", mount == CombatVR.I.HandR ? "R" : "L", mf.name, Cm(Mathf.Abs(sz.x) / k), Cm(Mathf.Abs(sz.y) / k), Cm(Mathf.Abs(sz.z) / k));
                    }
                }
            return sb.ToString();
        }

        /// The cards lying on the map (the slots' visible card meshes): the most common flat size.
        static void CardRenderers(StringBuilder sb, Map map, float k)
        {
            int n = 0;
            float a = 0f, b = 0f;
            foreach (MapLayoutSlot slot in map.MapLayout.Slots)
            {
                if (slot == null || !slot.gameObject.activeInHierarchy) continue;
                foreach (MeshFilter mf in slot.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf == null || mf.sharedMesh == null) continue;
                    Renderer r = mf.GetComponent<Renderer>();
                    if (r == null || !r.enabled) continue;
                    Vector3 sz = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
                    sz = new Vector3(Mathf.Abs(sz.x), Mathf.Abs(sz.y), Mathf.Abs(sz.z));
                    // a card: two long sides and a thin one
                    float lo = Mathf.Min(sz.x, Mathf.Min(sz.y, sz.z)), hi = Mathf.Max(sz.x, Mathf.Max(sz.y, sz.z)), mid = sz.x + sz.y + sz.z - lo - hi;
                    if (lo > mid * 0.2f || hi < 0.01f) continue;
                    if (n++ == 0) sb.AppendFormat(" | card mesh '{0}'", mf.name);
                    a += mid; b += hi;
                    break;
                }
            }
            if (n > 0) sb.AppendFormat(Inv, " ({0} cards): {1} x {2} cm", n, Cm(a / n / k), Cm(b / n / k));
        }

        /// A humanoid's hand: wrist -> middle finger's distal joint, and the knuckles' spread (index -> little proximal joints).
        static void HandOf(StringBuilder sb, string who, Animator an, float k)
        {
            if (an == null) { sb.Append(" | ").Append(who).Append(": no animator"); return; }
            if (!an.isHuman) { sb.Append(" | ").Append(who).Append(": not humanoid (").Append(an.name).Append(')'); return; }
            foreach (bool left in new[] { true, false })
            {
                Transform wrist = an.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                Transform p = an.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
                Transform i = an.GetBoneTransform(left ? HumanBodyBones.LeftMiddleIntermediate : HumanBodyBones.RightMiddleIntermediate);
                Transform d = an.GetBoneTransform(left ? HumanBodyBones.LeftMiddleDistal : HumanBodyBones.RightMiddleDistal);
                Transform ix = an.GetBoneTransform(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
                Transform lt = an.GetBoneTransform(left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);
                if (wrist == null || p == null) { sb.AppendFormat(" | {0} {1}: no hand bones", who, left ? "L" : "R"); continue; }
                float toDip = (p.position - wrist.position).magnitude + (i != null ? (i.position - p.position).magnitude : 0f) + (i != null && d != null ? (d.position - i.position).magnitude : 0f);
                sb.AppendFormat(Inv, " | {0} {1} hand: wrist -> middle {2} {3} cm (the glove {4}), knuckles {5} cm apart, lossy {6:0.000}",
                    who, left ? "L" : "R", d != null ? "DIP" : i != null ? "PIP" : "MCP", Cm(toDip / k), GloveToDip.ToString("0.0", Inv),
                    ix != null && lt != null ? Cm((ix.position - lt.position).magnitude / k) : "-", wrist.lossyScale.x);
            }
        }
    }
}

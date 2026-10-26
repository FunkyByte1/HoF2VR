using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Choosing one card of several (the game's CardChoice: DisplayCardsTask "choose one of these", ...; user 2026-10-12:
    /// "selecting a card makes it move backwards and tilt at an angle to basically ask you to confirm it. But this caused the
    /// card to be behind or collide with other cards. How about we move all the other cards back (without tilting them)
    /// and dim them?"). The game zooms the clicked card (CardZoom, with Continue / Back); in VR its zoom spot is pushed
    /// further away (TableHud.ZoomFarther) - behind the row it came from - and turned to the flat camera. From a choice row:
    ///  * the card stays where it was in the row and comes 4 cm toward the player, its own angle kept;
    ///  * the row's other cards go 7 cm back (straight away from the eyes, no turning) and are dimmed;
    ///  * Continue / Back work as before (the controls column, A / B); Back deals it back into the row as the game does.
    /// Setting [Table] ChoiceStaysPut.
    internal class ChoiceZoom : MonoBehaviour
    {
        public static ChoiceZoom I;
        public static float Toward = 0.04f, Back = 0.07f, Dim = 0.45f, Glide = 0.2f;
        static Card s_card;
        static CardContainer s_row;
        static Vector3 s_pos, s_eye;
        static Quaternion s_rot;
        static float s_since = -1f;
        public static int Zooms;
        public static string Last = "-";
        static readonly List<CardMove.Saved> s_moved = new List<CardMove.Saved>();
        static int s_frame = -1;

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(CardZoom), "Zoom"), prefix: new HarmonyMethod(typeof(ChoiceZoom), nameof(ZoomPrefix)));
        }

        void Awake()
        {
            I = this;
            Camera.onPreCull += PreCull;
            Camera.onPostRender += PostRender;
            Diag.StatusExtras["choicezoom"] = () => Diag.Q((s_card != null ? "holding " + s_card.name + " in place" : "idle") + " | zooms " + Zooms + " | " + Last);
        }

        static bool Wanted => XRSettings.enabled && Settings.ChoiceStaysPut && Places.AtCaravan && VRRig.I != null;

        /// The zoom asked for a card that lies in the choice row: it is held in place from here on.
        static void ZoomPrefix(CardZoom.ZoomData a_data)
        {
            try
            {
                if (!Wanted || a_data == null || a_data.Card == null) return;
                CardChoice ch = UI3D.Instance != null ? UI3D.Instance.CardChoice : null;
                if (ch == null || a_data.Card.Container != ch.Container) return;
                s_card = a_data.Card;
                s_row = ch.Container;
                s_pos = s_card.transform.position;
                s_rot = s_card.transform.rotation;
                // (the eyes of the moment it was chosen: toward them and away from them, never from the head of each frame - the
                // spawn rule revised 2026-10-17 21:50, "nothing on the table moves with the player's head")
                s_eye = VRRig.I.HeadWorldPos;
                s_since = Time.unscaledTime;
                Zooms++;
                Last = s_card.name.Replace("(Clone)", "") + " chosen: kept in place, the others back and dimmed";
                Plugin.L.LogInfo("ChoiceZoom: " + Last);
            }
            catch (Exception e) { Plugin.L.LogWarning("ChoiceZoom: " + e.Message); }
        }

        /// TableHud's zoom push leaves a choice's card alone.
        public static bool Holding(Card c) => s_card != null && c == s_card;

        void LateUpdate()
        {
            if (s_card == null) return;
            CardZoom z = UI3D.Instance != null ? UI3D.Instance.CardZoom : null;
            bool zoomed = z != null && z.Container != null && s_card != null && s_card.Container == z.Container;
            if (!zoomed && Time.unscaledTime - s_since > 0.5f || !Wanted)
            {
                // confirmed (gone) or Back (dealt back into the row): the game has it again
                Plugin.L.LogInfo("ChoiceZoom: released (" + (s_card != null && s_card.Container == s_row ? "back in the row" : "taken") + ")");
                s_card = null;
                s_row = null;
                CardLook.DimmedRow = null;
                Veils(false);
                return;
            }
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Time.unscaledTime - s_since) / Glide));
            Vector3 toward = (s_eye - s_pos).normalized * (Toward * VRRig.I.Scale);
            s_card.transform.SetPositionAndRotation(s_pos + toward * k, s_rot);
            CardLook.DimmedRow = s_row;
            CardLook.DimFactor = Dim;
            CardLook.DimExcept = s_card;
            // (the face of such a card is mostly NGUI - parchment, art, words - which the card mesh's colour doesn't reach:
            // a dark veil just in front of each other card's face, a child of the card, so it goes back with it)
            Veils(true);
        }

        // ---- the dimming veils ---------------------------------------------------------------------------------------------
        static readonly Dictionary<Card, GameObject> s_veils = new Dictionary<Card, GameObject>();
        static Material s_veilMat;
        public static float VeilAlpha = 0.55f;

        static void Veils(bool on)
        {
            if (!on || s_row == null)
            {
                foreach (var kv in s_veils) if (kv.Value != null) kv.Value.SetActive(false);
                return;
            }
            foreach (Card c in s_row.Cards)
            {
                if (c == null) continue;
                GameObject v;
                if (!s_veils.TryGetValue(c, out v) || v == null) { v = MakeVeil(c); s_veils[c] = v; }
                if (v != null && v.activeSelf != (c != s_card)) v.SetActive(c != s_card);
            }
        }

        /// A dark quad on the card's face side (the side toward the eyes), as big as its face, 2 mm in front of it.
        static GameObject MakeVeil(Card c)
        {
            try
            {
                MeshRenderer face = c.CardTemplate != null ? CardLook.s_mesh(c.CardTemplate) : null;
                MeshFilter mf = face != null ? face.GetComponent<MeshFilter>() : null;
                if (mf == null || mf.sharedMesh == null || VRRig.I == null) return null;
                Transform ct = c.transform;
                // the face mesh's box in the card's own frame
                Bounds mb = mf.sharedMesh.bounds, lb = default(Bounds);
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 local = ct.InverseTransformPoint(face.transform.TransformPoint(corner));
                    if (i == 0) lb = new Bounds(local, Vector3.zero); else lb.Encapsulate(local);
                }
                // its thin axis is the face's normal; the side toward the eyes is the face
                Vector3 size = lb.size;
                int thin = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
                Vector3 n = thin == 0 ? Vector3.right : thin == 1 ? Vector3.up : Vector3.forward;
                Vector3 toEye = ct.InverseTransformDirection(VRRig.I.HeadWorldPos - ct.position);
                if (Vector3.Dot(toEye, n) < 0f) n = -n;
                Vector3 a = thin == 0 ? Vector3.up : Vector3.right, b = thin == 2 ? Vector3.up : Vector3.forward;
                float off = lb.extents[thin] + 0.002f / Mathf.Max(1e-4f, ct.lossyScale.x);
                Vector3 o = lb.center + n * off;
                Vector3 ha = a * lb.extents[thin == 0 ? 1 : 0], hb = b * lb.extents[thin == 2 ? 1 : 2];
                var m = new Mesh { name = "HoF2VR_ChoiceVeil" };
                m.vertices = new[] { o - ha - hb, o + ha - hb, o + ha + hb, o - ha + hb };
                m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                Color k = new Color(0f, 0f, 0f, VeilAlpha);
                m.colors = new[] { k, k, k, k };
                m.RecalculateBounds();
                if (s_veilMat == null) s_veilMat = new Material(Shader.Find("Sprites/Default")) { name = "HoF2VR_ChoiceVeil", renderQueue = 4600 };
                var go = new GameObject("HoF2VR_ChoiceVeil") { layer = face.gameObject.layer };
                go.transform.SetParent(ct, false);
                go.AddComponent<MeshFilter>().sharedMesh = m;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = s_veilMat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                return go;
            }
            catch (Exception e) { Plugin.L.LogWarning("ChoiceZoom: veil: " + e.Message); return null; }
        }

        /// The row's other cards, straight back from the eyes while drawn (their own places are kept).
        static void PreCull(Camera cam)
        {
            if (s_card == null || s_row == null || s_frame == Time.frameCount) return;
            UICamera ui = UI3D.Instance != null ? UI3D.Instance.UICamera : null;
            if (ui == null || cam != ui.cachedCamera) return;
            s_frame = Time.frameCount;
            Vector3 eye = s_eye;
            float d = Back * VRRig.I.Scale * Mathf.Clamp01((Time.unscaledTime - s_since) / Glide);
            foreach (Card c in s_row.Cards)
            {
                if (c == null || c == s_card) continue;
                // straight back from the eyes, its texts with it (CardMove)
                CardMove.Shift(c, (c.transform.position - eye).normalized * d, s_moved);
            }
        }

        static void PostRender(Camera cam)
        {
            if (s_moved.Count == 0) return;
            if (cam.stereoEnabled && cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left) return;
            UICamera ui = UI3D.Instance != null ? UI3D.Instance.UICamera : null;
            if (ui == null || cam != ui.cachedCamera) return;
            CardMove.Restore(s_moved);
        }
    }
}

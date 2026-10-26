using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Card readability (user 2026-10-12 evening: "All the cards are kind of hard to see and read. Especially darkened ones
    /// like places you already visited on the board. Can we brighten them all and add more contrast to them?"). Options as a
    /// setting ([Table] CardLook), compared on captures/card-contrast-2026-10-12/index.html:
    ///  * Game   - as the game draws them (a visited card's material is the game's darken copy: _Color 174/255 = 0.68);
    ///  * Bright - every card face x1.25, a visited one x0.92 (still a little darker than the rest);
    ///  * Lit    - x1.15 / x0.9 and a small warm light over the table (no shadows): the game's own shading gets more light;
    ///  * Marked - x1.2, and a visited card is not darkened but greyed cool (a state, not a shadow);
    ///  * Strong - x1.45 / x1.0 and the light.
    /// How: each card face (CardTemplate's mesh) gets a MaterialPropertyBlock _Color from the material the game gave it (the
    /// normal or the darkened one), so the game's materials and its own darkening stay untouched; at the table only.
    internal class CardLook : MonoBehaviour
    {
        public static CardLook I;
        public enum Look { Game, Bright, Lit, Marked, Strong }

        public static readonly AccessTools.FieldRef<CardTemplate, MeshRenderer> s_mesh = AccessTools.FieldRefAccess<CardTemplate, MeshRenderer>("m_cardMesh");
        readonly HashSet<MeshRenderer> m_cards = new HashSet<MeshRenderer>();
        readonly List<MeshRenderer> m_drop = new List<MeshRenderer>();
        MaterialPropertyBlock m_mpb;
        float m_nextScan, m_nextApply;
        Look m_applied = (Look)(-1);
        Light m_light;
        public static int Faces, Visited;

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.PropertySetter(typeof(CardTemplate), "CardMaterial"), postfix: new HarmonyMethod(typeof(CardLook), nameof(MaterialSet)));
            }
            catch (Exception e) { Plugin.L.LogWarning("CardLook: patch failed: " + e.Message); }
        }

        void Awake()
        {
            I = this;
            m_mpb = new MaterialPropertyBlock();
            // `readlight [intensity] [range x] [up m]`: the reading light's numbers, live (tuning against shots)
            Diag.Commands["readlight"] = a =>
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (a.Length > 1) ReadIntensity = float.Parse(a[1], inv);
                if (a.Length > 2) ReadRange = float.Parse(a[2], inv);
                if (a.Length > 3) ReadUp = float.Parse(a[3], inv);
                Material m = s_firstRaised != null ? s_firstRaised.sharedMaterial : null;
                return string.Format(inv, "reading light intensity {0:0.##}, reach x{1:0.##}, {2:0.00} m over the eyes; {3} raised (first: {4}, shader {5})", ReadIntensity, ReadRange, ReadUp, Raised,
                    s_firstRaised != null ? VRUI.Path(s_firstRaised.transform) : "-", m != null ? m.shader.name : "-");
            };
            Diag.StatusExtras["cardlook"] = () => Diag.Q(Settings.CardLook + " | faces " + Faces + " visited " + Visited + " | hidden faces now " + BlankedNow + " (ever " + Blanked + ") | light " + (m_light != null && m_light.enabled ? m_light.intensity.ToString("0.00") : "off") +
                " | reading light " + (m_read != null && m_read.enabled ? m_read.intensity.ToString("0.00") + " on " + Raised + " raised, cone " + m_read.spotAngle.ToString("0") + " deg, reach " + m_read.range.ToString("0.00") : "off") +
                " | zoom veil " + (m_veilOff ? "off (" + VeilOffFrames + " frames)" : "game's"));
        }

        /// A card got its material from the game (normal or darkened): it is one of ours to look after.
        static void MaterialSet(CardTemplate __instance)
        {
            if (I == null) return;
            MeshRenderer r = s_mesh(__instance);
            if (r != null) { I.m_cards.Add(r); I.m_nextApply = 0f; }
        }

        static bool Active => XRSettings.enabled && Places.AtCaravan && !CombatVR.Active;

        void LateUpdate()
        {
            Look want = Active ? Settings.CardLook : Look.Game;
            if (Time.unscaledTime >= m_nextScan)
            {
                // cards made before this ran (or whose material was never set through the property) are found by a scan
                m_nextScan = Time.unscaledTime + 6f;
                if (Active)
                    foreach (CardTemplate t in FindObjectsOfType<CardTemplate>())
                    {
                        MeshRenderer r = s_mesh(t);
                        if (r != null) m_cards.Add(r);
                    }
            }
            // the chance game's hidden cards change every frame while it runs (user 2026-10-12: "I can cheat by looking at the
            // other sides of the cards"): their faces are blanked at once
            bool chance = ChanceCardTask.RunningTask != null || m_blanked.Count > 0 || DimmedRow != null || TableCardGrab.HeldCard != null;
            // the rows this is about now are known at once (a card made for them may not be registered yet: the scan is every 6 s)
            if (Active && DimmedRow != null) RegisterRow(DimmedRow);
            Card held = TableCardGrab.HeldCard;
            if (Active && held != null && held.CardTemplate != null) { MeshRenderer hr = s_mesh(held.CardTemplate); if (hr != null) m_cards.Add(hr); }
            if (Active && ChanceCardTask.RunningTask != null && UI3D.Instance != null && UI3D.Instance.CardChoice != null) RegisterRow(UI3D.Instance.CardChoice.Container);
            if (want != m_applied || Time.unscaledTime >= m_nextApply || chance)
            {
                // every frame while a look is on (the game fades cards through their colour's alpha; a stale block would
                // hold an old alpha), every half second otherwise
                m_nextApply = Time.unscaledTime + (want == Look.Game ? 0.5f : 0f);
                ApplyAll(want);
                m_applied = want;
            }
            PlaceLight(want);
            PlaceReadingLight();
            Veil();
        }

        // The zoom's darkening veil (CardZoom/ZoomDarken, a black sprite at 90 %): on a monitor it dims the screen behind a card
        // shown close up; in VR it hung as a dark sheet half a metre in front of the eyes over the table and the room (2026-10-13,
        // with the user's "they are all in shadows"). Off at the table in VR while the reading light is on - the card stands
        // out by its depth and its light; its sprite is switched off (the game's tweens only write its alpha) and back on after.
        readonly List<UIWidget> m_veil = new List<UIWidget>();
        CardZoom m_veilOf;
        bool m_veilOff;
        public static int VeilOffFrames;

        void Veil()
        {
            CardZoom z = UI3D.Instance != null ? UI3D.Instance.CardZoom : null;
            if (z != m_veilOf)
            {
                RestoreVeil();
                m_veilOf = z;
                m_veil.Clear();
                Transform d = z != null ? z.transform.Find("ZoomDarken") : null;
                if (d != null) d.GetComponentsInChildren(true, m_veil);
            }
            if (Settings.ReadingLight && Active)
            {
                bool any = false;
                foreach (UIWidget w in m_veil)
                    if (w != null && w.enabled) { w.enabled = false; m_veilOff = true; }
                    else if (w != null && w.gameObject.activeInHierarchy) any = true;
                if (any) VeilOffFrames++;
            }
            else if (m_veilOff) RestoreVeil();
        }

        void RestoreVeil()
        {
            foreach (UIWidget w in m_veil) if (w != null && !w.enabled) w.enabled = true;
            m_veilOff = false;
        }

        // ---- the reading light (the user, 2026-10-13, with Strong: "On a card close up, they are all in shadows or
        // something. Please fix.") ------------------------------------------------------------------------------------
        /// A card raised to the player to be read - within RaiseReach of the eyes and RaiseAbove over the table top: the
        /// zoom (a reward, gear with its question, a gained card), the shop's and a choice's cards, an encounter card, the
        /// card in a hand - gets a soft key light from the player's side that travels with it: a spot light just over the
        /// eyes aimed at the raised cards, on the cards' layer only, no shadows, its cone and reach just covering them (the
        /// board on the table stays as CardLook makes it). It fades in and out in a quarter second. [Table] ReadingLight.
        Light m_read;
        float m_readA;
        Vector3 m_readAim;
        public static int Raised;
        public static float RaiseReach = 0.85f, RaiseAbove = 0.12f, ReadIntensity = 1.1f, ReadUp = 0.10f, ReadRange = 2.5f;
        readonly List<Bounds> m_raised = new List<Bounds>();
        static MeshRenderer s_firstRaised;
        float m_nextTop, m_top = float.MinValue;

        void PlaceReadingLight()
        {
            bool on = Settings.ReadingLight && Active && VRRig.I != null && VRRig.I.HasStation && !VRFade.IsBlack && !Limbo.On;
            m_raised.Clear();
            Vector3 eye = Vector3.zero;
            float sc = 1f;
            int layer = -1;
            if (on)
            {
                sc = VRRig.I.Scale;
                eye = VRRig.I.HeadWorldPos;
                // (the table's top from its collider, looked up every 2 s: a find by path every frame is not free)
                if (Time.unscaledTime >= m_nextTop) { m_nextTop = Time.unscaledTime + 2f; m_top = TableHud.TableTop(float.MinValue); }
                float top = m_top;
                foreach (MeshRenderer r in m_cards)
                {
                    if (r == null || !r.enabled || !r.isVisible) continue;
                    Bounds b = r.bounds;
                    if (Vector3.Distance(b.center, eye) > RaiseReach * sc) continue;
                    if (top > float.MinValue && b.min.y < top + RaiseAbove * sc) continue;   // lying on the table (the board, the hand)
                    m_raised.Add(b);
                    if (layer < 0) { layer = r.gameObject.layer; s_firstRaised = r; }
                }
            }
            Raised = m_raised.Count;
            m_readA = Mathf.MoveTowards(m_readA, m_raised.Count > 0 ? 1f : 0f, Time.unscaledDeltaTime * 4f);
            if (m_readA <= 0f) { if (m_read != null) m_read.enabled = false; return; }
            if (m_read == null)
            {
                var go = new GameObject("HoF2VR_ReadingLight");
                DontDestroyOnLoad(go);
                m_read = go.AddComponent<Light>();
                m_read.type = LightType.Spot;
                m_read.shadows = LightShadows.None;
                m_read.renderMode = LightRenderMode.ForcePixel;
                m_read.color = new Color(1f, 0.97f, 0.93f);
            }
            if (m_raised.Count > 0)
            {
                // aim at the middle of what is raised; the cone covers all of it (+6 deg), the reach the farthest card + 25 cm
                Bounds all = m_raised[0];
                for (int i = 1; i < m_raised.Count; i++) all.Encapsulate(m_raised[i]);
                Vector3 from = eye + Vector3.up * ReadUp * sc;
                m_readAim = all.center;
                Vector3 dir = (all.center - from).normalized;
                float half = 0f, far = 0f;
                foreach (Bounds b in m_raised)
                {
                    Vector3 ext = b.extents;
                    for (int k = 0; k < 8; k++)
                    {
                        Vector3 corner = b.center + new Vector3((k & 1) == 0 ? -ext.x : ext.x, (k & 2) == 0 ? -ext.y : ext.y, (k & 4) == 0 ? -ext.z : ext.z);
                        half = Mathf.Max(half, Vector3.Angle(dir, corner - from));
                        far = Mathf.Max(far, Vector3.Distance(from, corner));
                    }
                }
                m_read.transform.SetPositionAndRotation(from, Quaternion.LookRotation(dir));
                m_read.spotAngle = Mathf.Clamp(half * 2f + 12f, 20f, 120f);
                // (a spot light fades with distance over its whole range: the reach is 2.5 times the farthest card, so the cards
                // get most of it; the cone keeps it off the rest)
                m_read.range = Mathf.Max(far * ReadRange, 1.0f * sc);
                m_read.cullingMask = layer >= 0 ? (1 << layer) : ~0;
            }
            m_read.intensity = ReadIntensity * m_readA;
            m_read.enabled = true;
        }

        // ---- hidden cards stay hidden (the chance game's face-down cards) ------------------------------------------------
        readonly Dictionary<MeshRenderer, Card> m_cardOf = new Dictionary<MeshRenderer, Card>();
        readonly Dictionary<MeshRenderer, List<Renderer>> m_blanked = new Dictionary<MeshRenderer, List<Renderer>>();
        readonly List<MeshRenderer> m_unblank = new List<MeshRenderer>();
        public static int Blanked, BlankedNow;
        /// ChoiceZoom: the other cards of a choice row while one is held for its confirmation are dimmed.
        public static CardContainer DimmedRow;
        public static Card DimExcept;
        public static float DimFactor = 0.45f;

        void RegisterRow(CardContainer row)
        {
            if (row == null) return;
            foreach (Card c in row.Cards)
            {
                if (c == null || c.CardTemplate == null) continue;
                MeshRenderer r = s_mesh(c.CardTemplate);
                if (r != null) m_cards.Add(r);
            }
        }

        readonly Dictionary<UIPanel, float> m_panelAlpha = new Dictionary<UIPanel, float>();
        static readonly List<UIPanel> s_cardPanels = new List<UIPanel>();

        void HidePanels(Card card)
        {
            s_cardPanels.Clear();
            card.GetComponentsInChildren(true, s_cardPanels);
            foreach (UIPanel p in s_cardPanels)
            {
                if (p == null) continue;
                if (!m_panelAlpha.ContainsKey(p)) m_panelAlpha[p] = p.alpha;
                if (p.alpha != 0f) p.alpha = 0f;
            }
        }

        void ShowPanels(Card card)
        {
            s_cardPanels.Clear();
            card.GetComponentsInChildren(true, s_cardPanels);
            foreach (UIPanel p in s_cardPanels)
            {
                float a;
                if (p == null || !m_panelAlpha.TryGetValue(p, out a)) continue;
                p.alpha = a;
                m_panelAlpha.Remove(p);
            }
        }

        /// A card the game means to be hidden: one of the chance game's cards while it is turned face down.
        static bool MeantHidden(Card c)
        {
            if (c == null || !c.Flipped) return false;
            // a face-down card picked up from the map (feature A: turning it over in the hand shows no more than the game
            // does - a blank face)
            if (TableCardGrab.HeldCard == c) return true;
            if (ChanceCardTask.RunningTask == null) return false;
            CardChoice ch = UI3D.Instance != null ? UI3D.Instance.CardChoice : null;
            return ch != null && c.Container == ch.Container;
        }

        Card CardOf(MeshRenderer r)
        {
            Card c;
            if (!m_cardOf.TryGetValue(r, out c) || c == null) { c = r.GetComponentInParent<Card>(); m_cardOf[r] = c; }
            return c;
        }

        void ApplyAll(Look look)
        {
            int faces = 0, visited = 0, blanked = 0;
            m_drop.Clear();
            foreach (MeshRenderer r in m_cards)
            {
                if (r == null) { m_drop.Add(r); continue; }
                if (look != Look.Game && !r.isVisible && !m_blanked.ContainsKey(r)) continue;   // off screen: next time
                Material m = r.sharedMaterial;
                if (m == null || !m.HasProperty("_Color")) continue;
                Card card = CardOf(r);
                bool hide = Settings.HideFaces && MeantHidden(card);
                bool dimmed = DimmedRow != null && card != null && card != DimExcept && card.Container == DimmedRow;
                Color game = m.GetColor("_Color");
                bool dark = game.r < 0.9f && game.g < 0.9f && game.b < 0.9f;
                if (look == Look.Game && !hide && !dimmed && !m_blanked.ContainsKey(r))
                {
                    r.SetPropertyBlock(null);
                    continue;
                }
                Color c = look == Look.Game ? game : Target(look, game, dark);
                if (dimmed) { float a = c.a; c *= DimFactor; c.a = a; }
                r.GetPropertyBlock(m_mpb);
                m_mpb.SetColor("_Color", c);
                if (hide)
                {
                    // the face shows the card's back too, and whatever is printed on the face (title, art, numbers) is off
                    Texture back = m.HasProperty("_BackTex") ? m.GetTexture("_BackTex") : null;
                    if (back != null) m_mpb.SetTexture("_MainTex", back);
                    if (!m_blanked.ContainsKey(r))
                    {
                        var off = new List<Renderer>();
                        if (card != null)
                            foreach (Renderer x in card.GetComponentsInChildren<Renderer>(false))
                                if (x != r && x.enabled && !(x is ParticleSystemRenderer)) { x.enabled = false; off.Add(x); }
                        m_blanked[r] = off;
                        Blanked++;
                    }
                    // the face's art and words are NGUI widgets drawn by the card's own panel (separate draw call objects):
                    // the panel is see-through while the card is hidden (round 5: from behind the faces still read
                    // "Huge Success", "Failure"...); kept at 0 every frame (the game fades cards through it)
                    if (card != null) HidePanels(card);
                    blanked++;
                }
                else if (m_blanked.ContainsKey(r))
                {
                    // revealed: the face and its print come back
                    m_mpb.Clear();
                    if (look != Look.Game) m_mpb.SetColor("_Color", c);
                    foreach (Renderer x in m_blanked[r]) if (x != null) x.enabled = true;
                    if (card != null) ShowPanels(card);
                    m_unblank.Add(r);
                }
                r.SetPropertyBlock(m_mpb);
                faces++;
                if (dark) visited++;
            }
            foreach (MeshRenderer r in m_unblank) m_blanked.Remove(r);
            m_unblank.Clear();
            foreach (MeshRenderer r in m_drop) { m_cards.Remove(r); m_cardOf.Remove(r); m_blanked.Remove(r); }
            Faces = faces;
            Visited = visited;
            BlankedNow = blanked;
        }

        static Color Target(Look look, Color game, bool dark)
        {
            float a = game.a;
            Color c;
            switch (look)
            {
                case Look.Bright: c = dark ? Grey(0.92f) : Grey(1.25f); break;
                case Look.Lit: c = dark ? Grey(0.90f) : Grey(1.15f); break;
                case Look.Marked: c = dark ? new Color(0.80f, 0.86f, 1.0f) : Grey(1.2f); break;
                case Look.Strong: c = dark ? Grey(1.0f) : Grey(1.45f); break;
                default: c = game; break;
            }
            c.a = a;
            return c;
        }

        static Color Grey(float v) => new Color(v, v, v, 1f);

        /// The small warm light over the table (Lit, Strong): above the map's middle, on the cards' layer only, no shadows.
        void PlaceLight(Look look)
        {
            bool on = (look == Look.Lit || look == Look.Strong) && Active && VRRig.I != null && VRRig.I.HasStation && !VRFade.IsBlack;
            if (!on) { if (m_light != null) m_light.enabled = false; return; }
            if (m_light == null)
            {
                var go = new GameObject("HoF2VR_CardLight");
                DontDestroyOnLoad(go);
                m_light = go.AddComponent<Light>();
                m_light.type = LightType.Point;
                m_light.shadows = LightShadows.None;
                m_light.renderMode = LightRenderMode.ForcePixel;
                m_light.color = new Color(1f, 0.92f, 0.78f);
            }
            float sc = VRRig.I.Scale;
            Vector3 eye = VRRig.I.NeutralEye;
            Quaternion yaw = VRRig.I.NeutralRot;
            m_light.transform.position = eye + yaw * (new Vector3(0f, -0.22f, 0.50f) * sc);
            m_light.range = 1.4f * sc;
            m_light.intensity = look == Look.Strong ? 0.9f : 0.65f;
            int layer = -1;
            foreach (MeshRenderer r in m_cards) if (r != null) { layer = r.gameObject.layer; break; }
            m_light.cullingMask = layer >= 0 ? (1 << layer) : ~0;
            m_light.enabled = true;
        }
    }
}

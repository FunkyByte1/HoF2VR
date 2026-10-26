using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The deck builder's two rows told apart (user 2026-10-12 ~22:00: "At the deck building table, it was hard to tell which
    /// row was my deck and which row was other cards"). The flat game says it on its 2D screen (the mode's title and how many
    /// are chosen); in VR that screen hangs far off, so each row gets its own label in the game's font and frame, right
    /// above it - "YOUR DECK" with how many cards it holds (and how many of this step are chosen), and the step's own title
    /// over the cards to choose from - and the deck row lies on a dark tray with a gold rim. Setting [Table] DeckLabels.
    internal class DeckRows : MonoBehaviour
    {
        public static float LabelAbove = 0.035f, TrayMargin = 0.025f, LabelMpp = 0.00030f, LabelRefDist = 0.5f, LabelGap = 0.02f;
        static readonly AccessTools.FieldRef<AdventureDeckBuilder, AdventureDeckBuilderMode> s_mode = AccessTools.FieldRefAccess<AdventureDeckBuilder, AdventureDeckBuilderMode>("m_currentMode");
        static readonly AccessTools.FieldRef<UIAdventureDeckBuilderStatus, UILabel> s_title = AccessTools.FieldRefAccess<UIAdventureDeckBuilderStatus, UILabel>("m_titleLabel");
        static readonly AccessTools.FieldRef<UIAdventureDeckBuilderStatusBasic, UILabel> s_counter = AccessTools.FieldRefAccess<UIAdventureDeckBuilderStatusBasic, UILabel>("m_counter");
        UIAdventureDeckBuilderStatus m_status;
        float m_nextStatus;
        AdventureDeckBuilder m_db;
        float m_nextFind;
        Transform m_root;
        TextCard m_deck, m_choice;
        Transform m_tray;
        Mesh m_trayMesh, m_rimMesh;
        public static string Last = "-";
        public static int Shows;

        void Awake() => Diag.StatusExtras["deckrows"] = () => Diag.Q(Last);

        void LateUpdate()
        {
            bool want = XRSettings.enabled && Settings.DeckLabels && Places.Current == Places.Place.Hub && !VRFade.IsBlack && VRRig.I != null;
            CardContainer deckBox = null, choiceBox = null;
            string step = "", counter = "";
            if (want) Rows(out deckBox, out choiceBox, out step, out counter);
            Bounds deck, choice;
            bool hasDeck = RowBounds(deckBox, out deck);
            bool hasChoice = RowBounds(choiceBox, out choice);
            if (!hasDeck && !hasChoice) { if (m_root != null && m_root.gameObject.activeSelf) m_root.gameObject.SetActive(false); Last = "off"; return; }
            if (!Ensure()) return;
            if (!m_root.gameObject.activeSelf) { m_root.gameObject.SetActive(true); Shows++; }
            // (the resting eyes, not the head: the labels' size, angle and side were worked out from the head each frame, so they
            // grew, turned and slid as the head moved - the second playtest's deck builder, marks 3-5)
            Vector3 eye = VRRig.I.NeutralEye;
            float sc = VRRig.I.Scale;
            int deckN = CountShown(deckBox);
            step = GameText.TitleCase(step);
            string chosen = counter.Length > 0 ? "\n" + counter + " chosen" : "";
            // each label on the side of its row away from the other row (not over the other row's cards): the nearer row's
            // toward the player, the farther row's beyond it
            Vector3 away = Vector3.zero;
            if (hasDeck && hasChoice)
            {
                // along the player's facing (the rows are side by side across it): straight toward or away from the player
                Vector3 fwd = VRRig.I.NeutralRot * Vector3.forward;
                Vector3 d = choice.center - deck.center;
                float along = Vector3.Dot(d, fwd);
                if (Mathf.Abs(along) > 1e-4f) away = fwd * Mathf.Sign(along);
            }
            // (nearer along the floor: the board is tilted, the far row stands higher and can be nearer in a straight line)
            Vector3 hd = deck.center - eye, hc = choice.center - eye;
            hd.y = 0f; hc.y = 0f;
            bool deckNear = hd.sqrMagnitude <= hc.sqrMagnitude;
            Vector3 deckAway = deckNear ? -away : away;
            string deckText = "YOUR DECK\n" + deckN + (deckN == 1 ? " card" : " cards") + chosen, choiceText = (step.Length > 0 ? step : "TO CHOOSE FROM") + "\n" + CountShown(choiceBox) + " to choose from";
            if (SideTable.DeckOn)
            {
                // the deck builder lies on the map's case (SideTable): each label lies on the case at its row's left end
                Vector3 fwd = VRRig.I.NeutralRot * Vector3.forward, right = VRRig.I.NeutralRot * Vector3.right;
                fwd.y = 0f; right.y = 0f;
                PlaceFlat(m_deck, hasDeck, deck, eye, sc, deckText, right.normalized, fwd.normalized, TrayMargin + 0.007f);
                PlaceFlat(m_choice, hasChoice, choice, eye, sc, choiceText, right.normalized, fwd.normalized, 0f);
            }
            else
            {
                Place(m_deck, hasDeck, deck, eye, sc, deckText, deckAway);
                Place(m_choice, hasChoice, choice, eye, sc, choiceText, -deckAway);
            }
            Tray(hasDeck, deck, eye, sc, deckBox);
            Last = "deck " + deckN + (hasDeck ? " at " + Diag.V(deck.center) : " -") + " | choice " + (hasChoice ? Diag.V(choice.center) : "-") + " | step '" + step + "' " + counter + " | " + m_from;
        }

        string m_from = "-";
        static readonly SceneScan.AllOf<UIAdventureDeckBuilderStatus> s_statuses = new SceneScan.AllOf<UIAdventureDeckBuilderStatus>();
        static readonly SceneScan.AllOf<AdventureDeckBuilder> s_dbs = new SceneScan.AllOf<AdventureDeckBuilder>();
        DeckBuilder m_cb;
        static readonly System.Collections.Generic.Dictionary<System.Type, System.Reflection.FieldInfo> s_setField = new System.Collections.Generic.Dictionary<System.Type, System.Reflection.FieldInfo>();

        /// The two rows and the step's words, as the flat game's status panel says them (its title, "count/limit"): the
        /// challenge's deck builder (DeckBuilder: its active mode's deck browser and card-set browser), else the adventure
        /// deck builder (AdventureDeckBuilder: its deck and choice browsers).
        void Rows(out CardContainer deckBox, out CardContainer choiceBox, out string step, out string counter)
        {
            deckBox = choiceBox = null;
            step = counter = "";
            if (Time.unscaledTime >= m_nextFind)
            {
                m_nextFind = Time.unscaledTime + 1f;
                m_cb = DeckBuilder.Instance;
                if (m_db == null) foreach (AdventureDeckBuilder d in s_dbs.Get()) if (d != null && d.isActiveAndEnabled) { m_db = d; break; }
            }
            try
            {
                DeckBuilderMode mode = m_cb != null && m_cb.IsActive ? m_cb.ActiveMode : null;
                if (mode != null)
                {
                    m_from = "challenge deck builder: " + mode.GetType().Name;
                    deckBox = mode.PlayerDeckContainer;
                    System.Reflection.FieldInfo f;
                    if (!s_setField.TryGetValue(mode.GetType(), out f)) s_setField[mode.GetType()] = f = AccessTools.Field(mode.GetType(), "m_cardSetBrowser");
                    CardBrowser set = f != null ? f.GetValue(mode) as CardBrowser : null;
                    choiceBox = set != null ? set.Container : null;
                    step = SafeText(mode.Title);
                    if (mode.CardLimit > 0 && mode.Source != null) counter = mode.Source.Count + "/" + mode.CardLimit;
                    return;
                }
            }
            catch (Exception e) { m_from = "challenge deck builder: " + e.Message; }
            if (m_db == null || !m_db.isActiveAndEnabled || !m_db.IsActive) { m_from = "-"; return; }
            m_from = "adventure deck builder";
            deckBox = m_db.DeckBrowser != null ? m_db.DeckBrowser.Container : null;
            choiceBox = m_db.ChoiceBrowser != null ? m_db.ChoiceBrowser.Container : null;
            AdventureDeckBuilderMode am = s_mode(m_db);
            // the flat game's own status panel says the step and its counter (UIAdventureDeckBuilderStatus): the same words here
            // (the third playtest, marks 3 and 13: with no status panel up - the challenge map - this searched the whole world every
            // frame, 5.5 ms of the map's 6.6 ms "logic"; now the scene's panels are listed once per scene load)
            if (m_status == null || !m_status.isActiveAndEnabled)
            {
                m_status = null;
                foreach (UIAdventureDeckBuilderStatus st in s_statuses.Get()) if (st != null && st.isActiveAndEnabled) { m_status = st; break; }
                // (one made since the scene loaded: listed again, at most every 2 s and only while that deck builder is up)
                if (m_status == null && Time.unscaledTime >= m_nextStatus) { m_nextStatus = Time.unscaledTime + 2f; s_statuses.Invalidate(); }
            }
            if (m_status != null)
            {
                UILabel t = s_title(m_status);
                step = t != null && !string.IsNullOrEmpty(t.text) ? t.text : "";
                var basic = m_status as UIAdventureDeckBuilderStatusBasic;
                UILabel c = basic != null ? s_counter(basic) : null;
                counter = c != null && !string.IsNullOrEmpty(c.text) ? c.text : "";
            }
            if (step.Length == 0 && am != null && am.Title != null) step = SafeText(am.Title);
            if (counter.Length == 0 && am != null && am.TotalRequired > 0)
                counter = (am.ChosenCards.Count + (am.CurrentChoice != null ? am.CurrentChoice.ChosenCards.Count : 0)) + "/" + am.TotalRequired;
        }

        static int LayerOf(CardContainer box)
        {
            if (box == null) return -1;
            foreach (Card card in box.Cards)
            {
                MeshRenderer face = card != null && card.CardTemplate != null ? CardLook.s_mesh(card.CardTemplate) : null;
                if (face != null) return face.gameObject.layer;
            }
            return -1;
        }

        /// A row's plane from its first card's face mesh: its thinnest axis (toward the eyes' side) and its long axis as up.
        static bool CardPlane(CardContainer box, Vector3 eye, out Vector3 normal, out Vector3 up) { Vector3 p; float h; return CardPlane(box, eye, out normal, out up, out p, out h); }

        static bool CardPlane(CardContainer box, Vector3 eye, out Vector3 normal, out Vector3 up, out Vector3 point, out float half)
        {
            normal = up = point = Vector3.zero;
            half = 0f;
            if (box == null) return false;
            foreach (Card card in box.Cards)
            {
                MeshRenderer face = card != null && card.CardTemplate != null ? CardLook.s_mesh(card.CardTemplate) : null;
                MeshFilter mf = face != null ? face.GetComponent<MeshFilter>() : null;
                if (mf == null || mf.sharedMesh == null) continue;
                Vector3 sz = Vector3.Scale(mf.sharedMesh.bounds.size, face.transform.lossyScale);
                Vector3[] axes = { face.transform.right, face.transform.up, face.transform.forward };
                float[] len = { Mathf.Abs(sz.x), Mathf.Abs(sz.y), Mathf.Abs(sz.z) };
                int thin = len[0] <= len[1] && len[0] <= len[2] ? 0 : len[1] <= len[2] ? 1 : 2;
                int lng = thin == 0 ? (len[1] >= len[2] ? 1 : 2) : thin == 1 ? (len[0] >= len[2] ? 0 : 2) : (len[0] >= len[1] ? 0 : 1);
                normal = axes[thin].normalized;
                if (Vector3.Dot(normal, eye - face.transform.position) < 0f) normal = -normal;
                up = axes[lng].normalized;
                if (Vector3.Dot(up, Vector3.up) < 0f && Mathf.Abs(Vector3.Dot(up, Vector3.up)) > 0.1f) up = -up;
                point = face.bounds.center;
                half = len[thin] * 0.5f;
                return true;
            }
            return false;
        }

        static string SafeText(IStringLibraryReader r)
        {
            try { return r.GetValue(UIManager.StringLibrary) ?? ""; } catch (Exception) { return ""; }
        }

        static int CountShown(CardContainer box)
        {
            if (box == null) return 0;
            int n = 0;
            foreach (Card c in box.Cards) if (c != null && c.gameObject.activeInHierarchy) n++;
            return n;
        }

        static bool RowBounds(CardContainer box, out Bounds b)
        {
            b = default(Bounds);
            if (box == null || !box.gameObject.activeInHierarchy) return false;
            bool has = false;
            foreach (Card c in box.Cards)
            {
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                foreach (Renderer r in c.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled || !r.isVisible) continue;
                    if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
                }
            }
            return has;
        }

        bool Ensure()
        {
            if (m_root != null) return true;
            if (!GameText.Ready || !GameText.AtlasReady || VRUI.I == null) return false;
            var go = new GameObject("HoF2VR_DeckRows");
            DontDestroyOnLoad(go);
            m_root = go.transform;
            m_deck = new TextCard(m_root, "deck", VRUI.Layer, 5104, LabelMpp, 30, 40);
            m_choice = new TextCard(m_root, "choice", VRUI.Layer, 5104, LabelMpp, 30, 40);
            // the deck's tray: a dark leather backing and a gold rim (drawn after the room, before the cards' own UI)
            // (drawn with the room by its camera - the cards come after, by the 3D UI's own camera - pulled a little toward the
            // eyes in depth so it wins against the board it lies on)
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            var tray = new GameObject("tray") { layer = 0 };
            tray.transform.SetParent(m_root, false);
            m_trayMesh = new Mesh { name = "HoF2VR_DeckTray" };
            tray.AddComponent<MeshFilter>().sharedMesh = m_trayMesh;
            var tr = tray.AddComponent<MeshRenderer>();
            tr.sharedMaterial = Mat(sh, "HoF2VR_DeckTray", 1990);
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var rim = new GameObject("rim") { layer = 0 };
            rim.transform.SetParent(tray.transform, false);
            m_rimMesh = new Mesh { name = "HoF2VR_DeckRim" };
            rim.AddComponent<MeshFilter>().sharedMesh = m_rimMesh;
            var rr = rim.AddComponent<MeshRenderer>();
            rr.sharedMaterial = Mat(sh, "HoF2VR_DeckRim", 1991);
            rr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_tray = tray.transform;
            return true;
        }

        static Material Mat(Shader sh, string name, int queue)
        {
            var m = new Material(sh) { name = name, renderQueue = queue };
            m.SetInt("_SrcBlend", 5); m.SetInt("_DstBlend", 10); m.SetInt("_ZWrite", 0); m.SetInt("_Cull", 0);
            // (before the cards, without writing depth: the cards and the hands are drawn over it; a depth bias put it over
            // the card itself)
            m.SetFloat("_ZBias", 0f);
            m.color = Color.white;
            return m;
        }

        /// A label beside a row (on the side `away`; above it when there is no other row), facing the eyes, its letters
        /// bigger with the distance (about 2 cm capitals at a metre).
        static void Place(TextCard card, bool on, Bounds row, Vector3 eye, float sc, string text, Vector3 away)
        {
            card.Root.gameObject.SetActive(on);
            if (!on) return;
            card.Set(text);
            float dist = Vector3.Distance(eye, row.center) / sc;
            float mpp = LabelMpp * Mathf.Max(1f, dist / LabelRefDist);
            float h = card.HeightPx * mpp * sc;
            Vector3 p;
            if (away.sqrMagnitude > 1e-6f)
            {
                Vector3 a = away.normalized;
                float ext = Mathf.Abs(row.extents.x * a.x) + Mathf.Abs(row.extents.z * a.z);
                p = row.center + a * (ext + LabelGap * sc + h * 0.4f);
                p.y = row.max.y + h * 0.5f;
            }
            else p = new Vector3(row.center.x, row.max.y + (LabelAbove * sc) + h * 0.5f, row.center.z);
            Vector3 look = p - eye;
            card.Root.SetPositionAndRotation(p, Quaternion.LookRotation(look.sqrMagnitude > 1e-6f ? look : Vector3.forward, Vector3.up));
            card.Root.localScale = Vector3.one * mpp * sc;
        }

        /// (P2-6) A label lying flat on the case at the left end of its row (past `margin`: the deck's tray and rim), its text's
        /// top away from the player - beside the cards, never standing in front of them (the second playtest's mark 5: "YOUR DECK"
        /// stood up over the deck's cards).
        static void PlaceFlat(TextCard card, bool on, Bounds row, Vector3 eye, float sc, string text, Vector3 right, Vector3 fwd, float margin)
        {
            card.Root.gameObject.SetActive(on);
            if (!on) return;
            card.Set(text);
            float dist = Vector3.Distance(eye, row.center) / sc;
            float mpp = LabelMpp * Mathf.Max(1f, dist / LabelRefDist);
            float w = card.WidthPx * mpp * sc;
            float ext = Mathf.Abs(row.extents.x * right.x) + Mathf.Abs(row.extents.z * right.z);
            Vector3 p = row.center - right * (ext + (margin + LabelGap) * sc + w * 0.5f);
            p.y = row.min.y + 0.002f * sc;
            card.Root.SetPositionAndRotation(p, Quaternion.LookRotation(Vector3.down, fwd));
            card.Root.localScale = Vector3.one * mpp * sc;
        }

        /// The tray: a mat under the deck row - in the cards' own plane (from a card's face), 4 mm under them, a margin wider
        /// (round 5: a quad facing the eyes "behind" the row sank into the tilted board and never showed).
        void Tray(bool on, Bounds row, Vector3 eye, float sc, CardContainer box)
        {
            m_tray.gameObject.SetActive(on);
            if (!on) return;
            // drawn by the camera that draws the cards (their layer), before them: its depth holds only the hands and the
            // cards, so the mat shows over the board it lies on (on the room's camera the board hid it) and under the cards
            int layer = LayerOf(box);
            if (layer >= 0 && m_tray.gameObject.layer != layer)
            {
                m_tray.gameObject.layer = layer;
                foreach (Transform ch in m_tray) ch.gameObject.layer = layer;
            }
            Vector3 c = row.center;
            Vector3 n, u, fp;
            float half;
            if (!CardPlane(box, eye, out n, out u, out fp, out half)) { n = (eye - c).normalized; u = Vector3.up; fp = c; half = 0f; }
            Quaternion rot = Quaternion.LookRotation(-n, u);   // the mat's back to the board, its face to the eyes' side
            // the row's extent in the mat's plane: the box's corners projected on its two axes
            Vector3 ax = rot * Vector3.right, ay = rot * Vector3.up;
            float hw = 0f, hh = 0f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = Vector3.Scale(row.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                hw = Mathf.Max(hw, Mathf.Abs(Vector3.Dot(corner, ax)));
                hh = Mathf.Max(hh, Mathf.Abs(Vector3.Dot(corner, ay)));
            }
            hw += TrayMargin * sc;
            hh += TrayMargin * sc;
            // on the cards' back plane (the board they lie on): the row's middle brought into the face plane, then half a
            // card's thickness and half a millimetre under it; the depth bias wins against the board
            Vector3 onPlane = c - n * Vector3.Dot(c - fp, n);
            m_tray.SetPositionAndRotation(onPlane - n * (half + 0.001f * sc), rot);
            Color back = new Color(0.32f, 0.05f, 0.04f, 0.9f), rim = new Color(0.95f, 0.72f, 0.33f, 1f);   // (as given: .linear made the mat near-black on the dark board)
            back.a = 0.9f;
            rim.a = 1f;
            m_trayMesh.vertices = new[] { new Vector3(-hw, -hh, 0f), new Vector3(hw, hh, 0f), new Vector3(hw, -hh, 0f), new Vector3(-hw, hh, 0f) };
            m_trayMesh.triangles = new[] { 0, 1, 2, 1, 0, 3 };
            m_trayMesh.colors = new[] { back, back, back, back };
            m_trayMesh.RecalculateBounds();
            float b = 0.007f * sc, ox = hw + b, oy = hh + b;
            m_rimMesh.vertices = new[]
            {
                new Vector3(-ox, -oy, -0.0005f), new Vector3(ox, -oy, -0.0005f), new Vector3(ox, oy, -0.0005f), new Vector3(-ox, oy, -0.0005f),
                new Vector3(-hw, -hh, -0.0005f), new Vector3(hw, -hh, -0.0005f), new Vector3(hw, hh, -0.0005f), new Vector3(-hw, hh, -0.0005f),
            };
            if (m_rimMesh.triangles.Length == 0) m_rimMesh.triangles = new[] { 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7 };
            m_rimMesh.colors = new[] { rim, rim, rim, rim, rim, rim, rim, rim };
            m_rimMesh.RecalculateBounds();
        }
    }
}

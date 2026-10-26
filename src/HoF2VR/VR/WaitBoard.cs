using System.Collections.Generic;
using InControl;
using UnityEngine;

namespace HoF2VR
{
    /// The board in the waiting space (user 2026-10-11 afternoon: "it's kind of hard to know where I am going"; "the
    /// loading bar ... can you move it somewhere else ... make it stand out a little more? I barely noticed it"; "Once
    /// the place I'm going is loaded, but before I'm teleported there, can we make it so I have to press A first?").
    /// Restyled the same evening (user: the first board "looked generic, flat, and boring. I want it to fit the games'
    /// aesthetics more"): everything is the game's own - its title and text fonts (through the language proxies), its
    /// gold heading frame, red map ribbon, dividers, smoke, glow and the button glyph its prompts use - drawn as NGUI
    /// widgets standing in the space like the game's cards.
    ///  * Main board, straight ahead at eye level (the fire stays visible below it): where the player is going in gilded
    ///    lettering (title), the encounter or challenge (subtitle), the enemies of a fight, an ember-lit progress stroke
    ///    with the phase under it, and once the place is ready (WaitGate) "Press (A) to go" with a pulsing glow.
    ///  * Before a fight, a card to the left: the VR fighting controls (Settings Combat.ControlsCard = WaitingSpace), and
    ///    (style Card) the encounter's own card, captured at the table, floating to the right.
    /// Part of the waiting space's own world (layer 7, its camera, life size, a frame fixed when the space appears).
    /// Nothing is written before the game has loaded its fonts (the first seconds of the boot: the stroke alone).
    /// Settings Comfort.WaitingBoard, Comfort.WaitingBoardStyle.
    internal class WaitBoard : MonoBehaviour
    {
        public static WaitBoard I;
        // placement in the space (metres; the player's spot at the origin, facing +z)
        // 2.4 m away (smoothness doc fix #10, OK'd by the user: nearer things judder more when a frame comes late)
        public static float Ahead = 2.4f, Lift = 0.08f, DeathLift = -0.70f;
        // (2026-10-12: -40 -> -44 deg: with the two lines about warnings and the portal the card reached the board's left edge,
        // playtester review PT_space_ahead)
        public static float CardYaw = -44f, CardAhead = 2.2f, CardDrop = 0.08f;
        public static float ShotRight = 0.92f, ShotYaw = 20f, ShotHeight = 0.70f, TextLeft = 0.14f;
        public static float Px = 0.0023f;        // metres per UI pixel on the board
        public static float CardPx = 0.0021f;    // ... on the controls card
        public static int Shows;
        public static Settings.BoardStyle? StyleOverride;     // `board style <name>`
        static Settings.BoardStyle Style => StyleOverride ?? Settings.WaitingBoardStyle;

        Transform m_root, m_board, m_shot;
        TextCard m_controls;
        GlyphCard m_glyphs;
        float m_glyphTry;
        // back panel (glows and smoke: animated), main panel (lettering and frames: static), front panel (the stroke)
        UISprite m_smoke, m_glow, m_promptGlow;
        UISprite m_frame, m_banner, m_divider, m_track, m_glyph, m_disc, m_promptBack;
        UILabel m_title, m_titleShadow, m_sub, m_det, m_phase, m_pre, m_post, m_letter;
        UISprite m_fill, m_ember, m_spark;
        Material m_shotMat;
        bool m_texts, m_dirty = true;
        int m_built = -1;
        float m_bar, m_barW = 640f, m_barY, m_promptY;
        Settings.BoardStyle m_laidStyle;
        bool m_laidShot, m_deathLaid, m_panelLaid;
        string m_controlsText = "";
        static bool s_broken;

        void Awake()
        {
            I = this;
            Diag.StatusExtras["board"] = () => Diag.Q(m_root != null && m_root.gameObject.activeInHierarchy
                ? (m_texts ? m_title.text + " | " + m_sub.text : "(no fonts yet)") + " | bar " + m_bar.ToString("0.00") + (WaitGate.Waiting ? " | READY" : "") +
                  " | style " + Style + (ShotShown ? " +card" : "") + (m_controls != null && m_controls.Root.gameObject.activeSelf ? " | controls card" : "")
                : "off");
            Diag.Commands["board"] = a =>
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (a.Length > 2 && a[1] == "set")
                {
                    for (int i = 2; i + 1 < a.Length; i += 2)
                    {
                        float v = float.Parse(a[i + 1], inv);
                        switch (a[i])
                        {
                            case "ahead": Ahead = v; break;
                            case "lift": Lift = v; break;
                            case "cardyaw": CardYaw = v; break;
                            case "cardahead": CardAhead = v; break;
                            case "carddrop": CardDrop = v; break;
                            case "shotright": ShotRight = v; break;
                            case "shotyaw": ShotYaw = v; break;
                            case "shotheight": ShotHeight = v; break;
                            case "textleft": TextLeft = v; break;
                            case "px": Px = v; if (m_board != null) m_board.localScale = Vector3.one * Px; break;
                        }
                    }
                    m_built = -1;
                }
                else if (a.Length > 2 && a[1] == "style")
                {
                    if (a[2] == "default") StyleOverride = null;
                    else StyleOverride = (Settings.BoardStyle)System.Enum.Parse(typeof(Settings.BoardStyle), a[2], true);
                    m_built = -1;
                    m_dirty = true;
                }
                else if (a.Length > 1 && a[1] == "shot")
                {
                    string why;
                    bool ok = CardShot.Capture(out why);
                    m_built = -1;
                    return "card shot " + (ok ? "taken" : "failed: " + why);
                }
                return "board ahead " + Ahead + " lift " + Lift + " style " + Style + " card " + CardShot.Status + " | " +
                       Diag.Q(m_title != null ? m_title.text : "-") + " fonts " + (GameText.Ready ? "normal->" + GameText.Resolve(GameText.Normal) + " stylised->" + GameText.Resolve(GameText.Stylised) : "not loaded");
            };
        }

        // ---- building --------------------------------------------------------------------------------

        static UIAtlas s_atlas;

        /// Our own sprite data over the game's UI atlas (same material, same texture): its red map ribbon, the timer bar
        /// and the brush stroke get slice borders, so they stretch to any text without distorting their ends.
        static UIAtlas BoardAtlas()
        {
            if (s_atlas != null) return s_atlas;
            UIAtlas src = GameText.Atlas;
            if (src == null) return null;
            var go = new GameObject("HoF2VR_BoardAtlas");
            DontDestroyOnLoad(go);
            UIAtlas a = go.AddComponent<UIAtlas>();
            a.spriteMaterial = src.spriteMaterial;
            var list = new List<UISpriteData>();
            Copy(list, src, "gui_map_banner", "banner", 104, 0, 104, 0);
            Copy(list, src, "gui_combat_timer_empty", "track", 40, 0, 40, 0);
            Copy(list, src, "gui_dlc_cost_long", "stroke", 38, 0, 41, 0);
            a.spriteList = list;
            a.pixelSize = 1.35f;
            a.MarkSpriteListAsChanged();
            s_atlas = a;
            return a;
        }

        static void Copy(List<UISpriteData> list, UIAtlas src, string from, string to, int l, int b, int r, int t)
        {
            UISpriteData sd = src.GetSprite(from);
            if (sd == null) return;
            var c = new UISpriteData();
            c.CopyFrom(sd);
            c.name = to;
            c.SetBorder(l, b, r, t);
            list.Add(c);
        }

        static GameObject PanelObject(string name, Transform parent, int queue)
        {
            var go = new GameObject(name) { layer = parent.gameObject.layer };
            go.transform.SetParent(parent, false);
            GameText.Panel(go, queue);
            return go;
        }

        void Build(Transform limboRoot)
        {
            var root = new GameObject("HoF2VR_WaitBoard") { layer = Limbo.Layer };
            m_root = root.transform;
            m_root.SetParent(limboRoot, false);
            var b = new GameObject("board") { layer = Limbo.Layer };
            m_board = b.transform;
            m_board.SetParent(m_root, false);
            m_board.localScale = Vector3.one * Px;
            UIAtlas at = GameText.Atlas, ba = BoardAtlas();
            Color gold = new Color(0.86f, 0.67f, 0.36f, 1f);
            // z: + is away from the player (UI pixels; 8 px = 1 cm)
            Transform back = PanelObject("back", m_board, 3130).transform;
            m_smoke = GameText.Sprite(back, "smoke", at, "gui_dlc_text_backing", new Color(0f, 0f, 0f, 0.62f), 0, 400, 300, UIBasicSprite.Type.Simple);
            m_smoke.transform.localPosition = new Vector3(0f, 0f, 40f);
            m_glow = GameText.Sprite(back, "glow", at, "gui_glow_white", new Color(1f, 0.55f, 0.18f, 0.25f), 1, 400, 200, UIBasicSprite.Type.Simple);
            m_promptGlow = GameText.Sprite(back, "promptGlow", at, "gui_glow_white", new Color(1f, 0.6f, 0.2f, 0.3f), 2, 400, 110, UIBasicSprite.Type.Simple);

            Transform main = PanelObject("main", m_board, 3140).transform;
            m_frame = GameText.Sprite(main, "frame", at, "gui_heading", gold, 1, 520, 140);
            m_banner = GameText.Sprite(main, "banner", ba, "banner", Color.white, 1, 600, 139);
            m_title = GameText.Label(main, "title", GameText.Stylised, 64, Color.white, 5);
            GameText.Gild(m_title, 1.6f);
            m_titleShadow = GameText.Shadow(m_title, new Vector2(4f, -5f), 6f, 0.5f);
            m_sub = GameText.Label(main, "subtitle", GameText.Stylised, 40, GameText.Parchment, 5);
            m_sub.effectStyle = UILabel.Effect.Shadow;
            m_sub.effectColor = new Color(0f, 0f, 0f, 0.7f);
            m_sub.effectDistance = new Vector2(2f, 2f);
            m_divider = GameText.Sprite(main, "divider", at, "gui_divider_full", gold, 3, 454, 19, UIBasicSprite.Type.Simple);
            m_det = GameText.Label(main, "details", GameText.Normal, 32, new Color(0.90f, 0.84f, 0.72f, 1f), 5);
            m_track = GameText.Sprite(main, "track", ba, "stroke", new Color(0.30f, 0.22f, 0.14f, 0.9f), 3, 680, 34);
            m_phase = GameText.Label(main, "phase", GameText.Normal, 28, new Color(0.80f, 0.75f, 0.66f, 1f), 5);
            m_promptBack = GameText.Sprite(main, "promptBack", at, "gui_prompt_backing", new Color(0f, 0f, 0f, 0.55f), 3, 400, 76);
            m_pre = GameText.Label(main, "press", GameText.Normal, 38, GameText.MenuWhite, 6);
            m_post = GameText.Label(main, "action", GameText.Normal, 38, GameText.MenuWhite, 6);
            m_pre.effectStyle = m_post.effectStyle = UILabel.Effect.Shadow;
            m_pre.effectColor = m_post.effectColor = new Color(0f, 0f, 0f, 0.8f);
            m_pre.effectDistance = m_post.effectDistance = new Vector2(2f, 2f);
            m_disc = GameText.Sprite(main, "disc", at, "gui_button_circle", new Color(0.16f, 0.16f, 0.18f, 1f), 6, 52, 52, UIBasicSprite.Type.Simple);
            m_letter = GameText.Label(main, "letter", GameText.Normal, 30, Color.white, 7);
            m_letter.text = "A";
            m_glyph = GameText.Sprite(main, "glyph", at, "gui_button_circle", Color.white, 8, 58, 58, UIBasicSprite.Type.Simple);

            Transform front = PanelObject("front", m_board, 3170).transform;
            m_fill = GameText.Sprite(front, "fill", ba, "stroke", new Color(1f, 0.80f, 0.42f, 1f), 1, 100, 26);
            m_fill.pivot = UIWidget.Pivot.Left;
            m_ember = GameText.Sprite(front, "ember", at, "gui_glow_white", new Color(1f, 0.55f, 0.15f, 0.6f), 2, 150, 70, UIBasicSprite.Type.Simple);
            m_spark = GameText.Sprite(front, "spark", at, "gui_glow_white", new Color(1f, 0.9f, 0.65f, 0.7f), 3, 40, 22, UIBasicSprite.Type.Simple);

            m_controls = new TextCard(m_root, "controls", Limbo.Layer, 3140, CardPx);
            m_controls.Root.gameObject.SetActive(false);
            // the same controls with the Quest buttons drawn (the text card stays as the fallback until the glyphs exist)
            m_glyphs = new GlyphCard(m_root, "controls glyphs", Limbo.Layer, 3140, CardPx);
            m_glyphs.Root.gameObject.SetActive(false);

            // the encounter's card (style Card): a quad with the snapshot (CardShot)
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.name = "encounterCard";
            q.layer = Limbo.Layer;
            m_shot = q.transform;
            m_shot.SetParent(m_root, false);
            m_shotMat = new Material(Shader.Find("Sprites/Default")) { renderQueue = 3125 };
            var mr = q.GetComponent<MeshRenderer>();
            mr.sharedMaterial = m_shotMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            q.SetActive(false);
            m_root.gameObject.SetActive(false);
        }

        // ---- per frame -----------------------------------------------------------------------------------

        void LateUpdate()
        {
            CardShot.Watch();
            bool want = Limbo.On && Settings.WaitingBoard && Limbo.Root != null && !s_broken;
            if (!want)
            {
                if (m_root != null && m_root.gameObject.activeSelf) m_root.gameObject.SetActive(false);
                return;
            }
            if (m_root == null)
            {
                // the game's fonts and art come with its UI bundle: nothing before (the first seconds of the boot)
                if (!GameText.Ready || !GameText.AtlasReady) return;
                try { Build(Limbo.Root); }
                catch (System.Exception e) { Plugin.L.LogError("board: could not build: " + e); s_broken = true; return; }
                m_texts = true;
                Plugin.L.LogInfo("board: built in the game's style (title font " + GameText.Resolve(GameText.Stylised) + ", text font " + GameText.Resolve(GameText.Normal) + ")");
            }
            if (!m_root.gameObject.activeSelf)
            {
                m_root.gameObject.SetActive(true);
                Shows++;
                m_built = -1;
                WaitGate.Describe();
            }
            if (m_built != Limbo.Shown) { Arrange(); m_built = Limbo.Shown; }
            if (Time.frameCount % 30 == 0) WaitGate.Describe();   // the encounter, its enemies, the end of a death screen
            try { Fill(); }
            catch (System.Exception e) { Plugin.L.LogError("board: " + e); s_broken = true; m_root.gameObject.SetActive(false); }
        }

        bool ShotShown => m_shot != null && m_shot.gameObject.activeSelf;

        /// The board is up (with the game's fonts) in the waiting space.
        public static bool Showing => I != null && I.m_root != null && I.m_root.gameObject.activeInHierarchy;

        /// Where the boards stand: in the space's frame, at the player's eye height (fixed while the space is up).
        void Arrange()
        {
            float eye = Limbo.EyeHeight;
            Vector3 eyeP = new Vector3(0f, eye, 0f);
            bool shot = Style == Settings.BoardStyle.Card && WaitGate.IsFight && CardShot.Ready;
            // the game's death / end-of-run text stands at eye height in the space: the board goes below it
            m_deathLaid = WaitGate.IsDeath;
            // the boot with its logo screens ([Dev] SkipIntros off): the game's 2D screen shows them at eye height in the
            // space - the board goes below them
            m_panelLaid = VRUI.PanelInWaitingSpace();
            Vector3 c = new Vector3(shot ? -TextLeft : 0f, eye + (m_panelLaid ? DeathLift : Lift), Ahead);
            m_board.localPosition = c;
            m_board.localRotation = Quaternion.LookRotation(new Vector3(c.x, 0f, c.z), Vector3.up) * Quaternion.Euler(Mathf.Atan2(eye - c.y, Ahead) * Mathf.Rad2Deg * 0f, 0f, 0f);
            Quaternion y = Quaternion.Euler(0f, CardYaw, 0f);
            Vector3 cc = y * new Vector3(0f, 0f, CardAhead) + new Vector3(0f, eye - CardDrop, 0f);
            m_controls.Root.localPosition = cc;
            m_controls.Root.localRotation = Quaternion.LookRotation(cc - eyeP, Vector3.up);
            m_glyphs.Root.localPosition = cc;
            m_glyphs.Root.localRotation = m_controls.Root.localRotation;
            if (shot)
            {
                Vector3 sp = new Vector3(ShotRight, eye + 0.02f, Ahead - 0.08f);
                m_shot.localPosition = sp;
                m_shot.localRotation = Quaternion.LookRotation(new Vector3(sp.x, 0f, sp.z), Vector3.up);
                m_shot.localScale = new Vector3(ShotHeight * CardShot.Aspect, ShotHeight, 1f);
                m_shotMat.mainTexture = CardShot.Texture;
            }
            m_shot.gameObject.SetActive(shot);
            m_laidShot = shot;
            m_dirty = true;
        }

        void Fill()
        {
            string title = WaitGate.Title ?? "", sub = WaitGate.Subtitle ?? "", det = WaitGate.Details ?? "";
            bool ready = WaitGate.Waiting;
            if (m_title.text != title) { m_title.text = title; m_titleShadow.text = title; m_dirty = true; }
            if (m_sub.text != sub) { m_sub.text = sub; m_dirty = true; }
            if (m_det.text != det) { m_det.text = det; m_dirty = true; }
            if (m_laidStyle != Style) m_dirty = true;
            bool shotWanted = Style == Settings.BoardStyle.Card && WaitGate.IsFight && CardShot.Ready;
            if (shotWanted != m_laidShot || VRUI.PanelInWaitingSpace() != m_panelLaid) { m_built = -1; return; }
            if (WaitGate.IsDeath != m_deathLaid) { m_deathLaid = WaitGate.IsDeath; m_dirty = true; }
            // the bar: the model's value; full once ready; a soft wave when the wait is unknown
            float v;
            bool wave = false;
            if (ready || LoadProgress.IsReady) v = 1f;
            else if (LoadProgress.Visible && LoadProgress.Known) v = Mathf.Clamp01(LoadProgress.Value);
            else { v = 0f; wave = true; }
            m_bar = Mathf.MoveTowards(m_bar, v, Time.unscaledDeltaTime * (v < m_bar ? 4f : 1.5f));
            if (v < 0.01f && m_bar > 0.5f && !ready) m_bar = v;   // a new wait
            float now = Time.unscaledTime;
            string phase = ready ? "" : LoadProgress.Active ? LoadProgress.Phase : wave ? "Loading" : "";
            if (m_phase.text != phase) m_phase.text = phase;
            string action = WaitGate.IsFight ? "to fight" : WaitGate.Key == "boot" ? "to begin" : "to go";
            if (m_post.text != action) { m_post.text = action; m_dirty = true; }
            if (m_pre.text != "Press") { m_pre.text = "Press"; m_dirty = true; }
            // the controls before a fight
            bool card = WaitGate.IsFight && Settings.ControlsCard == Settings.FightCard.WaitingSpace;
            if (card && !m_glyphs.Built && Time.unscaledTime >= m_glyphTry) { m_glyphTry = Time.unscaledTime + 1f; m_glyphs.Build(); }
            bool glyphCard = card && m_glyphs.Built;
            if (card && !glyphCard) { string ct = VRHelp.CombatText; if (ct != m_controlsText) { m_controlsText = ct; m_controls.Set(m_controlsText); } }
            if (m_controls.Root.gameObject.activeSelf != (card && !glyphCard)) m_controls.Root.gameObject.SetActive(card && !glyphCard);
            if (m_glyphs.Root.gameObject.activeSelf != glyphCard) m_glyphs.Root.gameObject.SetActive(glyphCard);
            if (m_dirty) Layout();

            // the prompt
            bool prompt = ready;
            SetActive(m_pre, prompt); SetActive(m_post, prompt); SetActive(m_promptBack, prompt); SetActive(m_promptGlow, prompt);
            bool glyph = prompt && UpdateGlyph();
            SetActive(m_glyph, glyph); SetActive(m_disc, prompt && !glyph); SetActive(m_letter, prompt && !glyph);
            float pulse = 0.5f + 0.5f * Mathf.Sin((now - WaitGate.ReadySince) * 4.2f);
            if (prompt) m_promptGlow.alpha = 0.16f + 0.30f * pulse;
            // firelight on the heading: a slow uneven flicker
            float flick = Mathf.PerlinNoise(now * 2.3f, 0.37f);
            m_glow.alpha = 0.16f + 0.14f * flick;

            // the stroke (left-aligned) and its ember
            bool show = wave || m_bar > 0.004f;
            float w = m_barW;
            float x0 = -w * 0.5f;
            float fw, fx;
            if (wave)
            {
                fw = w * 0.22f;
                fx = Mathf.Lerp(x0, -x0 - fw, 0.5f + 0.5f * Mathf.Sin(now * 1.3f));
            }
            else
            {
                fw = Mathf.Max(84f, w * m_bar);
                fx = x0;
            }
            SetActive(m_fill, show); SetActive(m_ember, show); SetActive(m_spark, show && !wave && m_bar < 0.999f);
            if (show)
            {
                SetPos(m_fill, fx, m_barY, -2f);
                int iw = Mathf.RoundToInt(fw);
                if (m_fill.width != iw) m_fill.width = iw;
                float ex = fx + fw - 12f;
                SetPos(m_ember, wave ? fx + fw * 0.5f : ex, m_barY, -3f);
                m_ember.alpha = 0.45f + 0.25f * Mathf.PerlinNoise(now * 5.1f, 0.81f);
                SetPos(m_spark, ex + 4f, m_barY, -4f);
            }
        }

        static void SetActive(UIWidget w, bool on)
        {
            if (w.gameObject.activeSelf != on) w.gameObject.SetActive(on);
        }

        static void SetPos(UIWidget w, float x, float y, float z)
        {
            Vector3 p = new Vector3(Mathf.Round(x), Mathf.Round(y), z);
            if (w.transform.localPosition != p) w.transform.localPosition = p;
        }

        /// The game's own glyph for A (the Quest one when Controls.QuestButtonPrompts is on); a dark disc with an A before
        /// the game's prompts exist (boot).
        bool UpdateGlyph()
        {
            UIAtlas a;
            string s;
            if (!GameText.PromptGlyph(InputControlType.Action1, out a, out s)) return false;
            if (m_glyph.atlas != a) m_glyph.atlas = a;
            if (m_glyph.spriteName != s) m_glyph.spriteName = s;
            return true;
        }

        /// Stacks the board's parts from the top (NGUI has measured the texts when they were set), centred on the
        /// board's origin.
        void Layout()
        {
            Settings.BoardStyle st = Style;
            m_laidStyle = st;
            bool banner = st == Settings.BoardStyle.Banner;
            Vector2 ts = GameText.Size(m_title), ss = GameText.Size(m_sub), ds = GameText.Size(m_det);
            float headH = banner ? 139f : Mathf.Max(ts.y + 66f, 134f);
            float headW = banner ? Mathf.Max(ts.x + 320f, 560f) : Mathf.Max(ts.x + 210f, 500f);
            float subH = ss.y > 0f ? ss.y + 4f : 0f;
            float detH = ds.y > 0f ? ds.y + 12f : 0f;
            const float divH = 30f, barH = 34f, phaseH = 32f, promptH = 84f;
            float total = headH + 8f + subH + divH + detH + 10f + barH + 8f + phaseH + promptH;
            float y = total * 0.5f;
            float headY = y - headH * 0.5f;
            SetPos(m_title, 0f, headY + (banner ? 6f : 3f), 0f);
            SetPos(m_titleShadow, 4f, headY + (banner ? 6f : 3f) - 5f, 6f);
            m_frame.gameObject.SetActive(!banner);
            m_banner.gameObject.SetActive(banner);
            if (banner)
            {
                m_banner.width = Mathf.RoundToInt(headW);
                m_banner.height = 139;
                SetPos(m_banner, 0f, headY, 4f);
                // light lettering on the red ribbon, as on the game's map
                m_title.applyGradient = true;
                m_title.gradientTop = new Color(1f, 0.97f, 0.88f, 1f);
                m_title.gradientBottom = new Color(0.93f, 0.80f, 0.58f, 1f);
                m_title.effectColor = new Color(0.20f, 0.02f, 0.02f, 0.9f);
            }
            else
            {
                m_frame.width = Mathf.RoundToInt(headW);
                m_frame.height = Mathf.RoundToInt(headH);
                SetPos(m_frame, 0f, headY, 4f);
                GameText.Gild(m_title, 1.6f);
            }
            if (m_deathLaid)
            {
                // the game's death title is blood red
                m_title.gradientTop = new Color(0.92f, 0.16f, 0.10f, 1f);
                m_title.gradientBottom = new Color(0.50f, 0.03f, 0.02f, 1f);
                m_title.effectColor = new Color(0.06f, 0.0f, 0.0f, 0.95f);
            }
            m_glow.width = Mathf.RoundToInt(headW + 200f);
            m_glow.height = Mathf.RoundToInt(headH + 150f);
            SetPos(m_glow, 0f, headY, 20f);
            float w = headW;
            y -= headH + 8f;
            if (subH > 0f) { SetPos(m_sub, 0f, y - ss.y * 0.5f, 0f); y -= subH; w = Mathf.Max(w, ss.x); }
            m_divider.width = Mathf.RoundToInt(Mathf.Clamp(Mathf.Max(ts.x, ss.x) * 0.9f, 380f, 620f));
            SetPos(m_divider, 0f, y - 13f, 2f);
            y -= divH;
            m_det.gameObject.SetActive(detH > 0f);
            if (detH > 0f) { SetPos(m_det, 0f, y - ds.y * 0.5f - 2f, 0f); y -= detH; w = Mathf.Max(w, ds.x); }
            y -= 10f;
            m_barW = 640f;
            m_barY = y - barH * 0.5f;
            m_track.width = Mathf.RoundToInt(m_barW + 40f);
            m_track.height = 34;
            SetPos(m_track, 0f, m_barY, 2f);
            y -= barH + 8f;
            SetPos(m_phase, 0f, y - phaseH * 0.5f, 0f);
            y -= phaseH;
            m_promptY = y - promptH * 0.5f;
            // "Press (A) to fight": the words around the game's glyph
            Vector2 pre = GameText.Size(m_pre), post = GameText.Size(m_post);
            const float glyphW = 58f, gap = 14f;
            float pw = pre.x + gap + glyphW + gap + post.x;
            float px = -pw * 0.5f;
            SetPos(m_pre, px + pre.x * 0.5f, m_promptY, 0f);
            SetPos(m_glyph, px + pre.x + gap + glyphW * 0.5f, m_promptY, -1f);
            SetPos(m_disc, px + pre.x + gap + glyphW * 0.5f, m_promptY, -1f);
            SetPos(m_letter, px + pre.x + gap + glyphW * 0.5f, m_promptY + 1f, -2f);
            SetPos(m_post, px + pre.x + gap + glyphW + gap + post.x * 0.5f, m_promptY, 0f);
            m_promptBack.width = Mathf.RoundToInt(pw + 170f);
            m_promptBack.height = 76;
            SetPos(m_promptBack, 0f, m_promptY, 3f);
            m_promptGlow.width = Mathf.RoundToInt(pw + 260f);
            m_promptGlow.height = 120;
            SetPos(m_promptGlow, 0f, m_promptY, 10f);
            w = Mathf.Max(w, m_barW + 40f);
            // a soft oval of shadow behind the lettering (no rectangle)
            m_smoke.width = Mathf.RoundToInt(w * 1.55f + 200f);
            m_smoke.height = Mathf.RoundToInt(total * 1.45f + 120f);
            m_smoke.gameObject.SetActive(true);
            m_dirty = false;
        }
    }

    /// The encounter's own card for the waiting space (docs/loading-design.md §3.4): captured once when a fight begins
    /// (GameState_Level_In), while it still lies revealed at the table - an orthographic camera just in front of its face,
    /// clipped tight around it (only the card's layer, so the table and the other cards stay out), into a texture that
    /// lasts for the fight.
    internal static class CardShot
    {
        static RenderTexture s_rt;
        static int s_for;
        static string s_state = "";
        public static string Status = "none";
        public static float Aspect = 0.66f;
        public static bool Ready => s_rt != null && s_for != 0 && s_ready;
        static bool s_ready;
        public static Texture Texture => s_rt;

        /// Captures at the start of each fight (first frame of Level_In); forgets the card at the next table.
        public static void Watch()
        {
            string st = GameWatch.State;
            if (st == s_state) return;
            s_state = st;
            if (st == "GameState_Level_In" || st == "GameState_BossBattle_In")
            {
                string why;
                if (Capture(out why)) Plugin.L.LogInfo("card shot: " + Status);
                else { s_ready = false; Status = "failed: " + why; Plugin.L.LogInfo("card shot: none (" + why + ")"); }
            }
            else if (st == "GameState_Map" || st == "GameState_ChallengeMap" || st == "GameState_Encounter")
                s_ready = false;
        }

        public static bool Capture(out string why)
        {
            why = "";
            Encounter e = Encounter.Instance;
            Card c = e != null ? e.Card : null;
            if (c == null) { why = "no encounter card"; return false; }
            CardTemplate tpl = c.CardTemplate;
            BoxCollider bc = tpl != null ? tpl.GetComponent<Collider>() as BoxCollider : null;
            if (bc == null) { why = "no card template"; return false; }
            Transform t = bc.transform;
            Vector3 ls = t.lossyScale;
            Vector3 sz = new Vector3(Mathf.Abs(bc.size.x * ls.x), Mathf.Abs(bc.size.y * ls.y), Mathf.Abs(bc.size.z * ls.z));
            int thin = 0, tall = 0;
            for (int i = 1; i < 3; i++)
            {
                if (sz[i] < sz[thin]) thin = i;
                if (sz[i] > sz[tall]) tall = i;
            }
            if (thin == tall) { why = "flat collider"; return false; }
            int wide = 3 - thin - tall;
            Vector3[] ax = { t.right, t.up, t.forward };
            Vector3 n = ax[thin], up = ax[tall];
            Vector3 centre = t.TransformPoint(bc.center);
            Vector3 head = VRRig.I != null ? VRRig.I.HeadWorldPos : (Camera.main != null ? Camera.main.transform.position : centre + Vector3.up);
            if (Vector3.Dot(n, head - centre) < 0f) n = -n;
            // the card's top: away from the player when it lies on the table, up when it stands
            Vector3 away = centre - head;
            away.y = 0f;
            if (Vector3.Dot(up, away.normalized + Vector3.up) < 0f) up = -up;
            float wM = sz[wide], hM = sz[tall];
            Aspect = wM / hM;
            int rh = 768, rw = Mathf.Clamp(Mathf.RoundToInt(rh * Aspect), 128, 1024);
            if (s_rt == null || s_rt.width != rw || s_rt.height != rh)
            {
                if (s_rt != null) { s_rt.Release(); Object.Destroy(s_rt); }
                s_rt = new RenderTexture(rw, rh, 24, RenderTextureFormat.ARGB32) { name = "HoF2VR_EncounterCard", antiAliasing = 4 };
                s_rt.Create();
            }
            var go = new GameObject("HoF2VR_CardShotCam");
            try
            {
                Camera cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                cam.orthographic = true;
                cam.orthographicSize = hM * 0.5f * 1.01f;
                cam.aspect = (float)rw / rh;
                float back = 0.05f;
                cam.transform.position = centre + n * back;
                cam.transform.rotation = Quaternion.LookRotation(-n, up);
                cam.nearClipPlane = Mathf.Max(0.001f, back - sz[thin] * 0.5f - 0.012f);
                cam.farClipPlane = back + sz[thin] * 0.5f + 0.012f;
                cam.cullingMask = 1 << tpl.gameObject.layer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.allowHDR = false;
                cam.targetTexture = s_rt;
                cam.Render();
                cam.targetTexture = null;
            }
            finally { Object.Destroy(go); }
            s_for = c.GetInstanceID();
            s_ready = true;
            Status = "'" + c.name + "' " + rw + "x" + rh + " (" + wM.ToString("0.000") + " x " + hM.ToString("0.000") + " m, layer " + tpl.gameObject.layer + ")";
            return true;
        }
    }
}

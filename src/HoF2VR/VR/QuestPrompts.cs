using System;
using System.Collections.Generic;
using HarmonyLib;
using InControl;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Button prompts that match the Quest controllers (user 2026-10-11 afternoon: "Since some of the buttons on VR
    /// controllers are different than the ones on a standard controller, can you change the button prompts to match?").
    /// Every controller glyph in the game is an NGUI sprite from one proxy atlas that ControllerAtlasManager points at the
    /// pad family's atlas, one sprite per InControl control (UIManager's controller bindings). In VR the proxy points at
    /// one of two copies of the Xbox One atlas instead, with the controls' sprites repainted as the mod maps them
    /// (VRPad.Map):
    ///   menus / the table: A B X Y = the Quest buttons, LB / RB = the left / right grip (page), LT / RT = the triggers,
    ///                      View = the left stick click (inventory), Menu = the left controller's menu button;
    ///   fights:            X (attack) = the weapon hand's trigger, Y (counter) = the shield hand's trigger,
    ///                      LB (companion) = X, LT (finisher) = Y, RB (artifact) = the shield hand's grip,
    ///                      RT (weapon ability) = the weapon hand's grip, A / B as they are.
    /// The art is drawn once on the CPU (dark Quest-style buttons with light letters, grip / trigger tabs, a stick ring);
    /// the sticks keep the Xbox glyphs. Settings Controls.QuestButtonPrompts (false = the Xbox glyphs).
    internal class QuestPrompts : MonoBehaviour
    {
        public static QuestPrompts I;
        static UIAtlas s_menu, s_xbox;
        // one atlas; its texture shows the menu or the fight glyphs: the repainted rectangles are copied on the GPU from
        // one of two prepared textures (a different atlas would re-fill every NGUI widget: 60-90 ms)
        static Texture2D s_live, s_texMenu, s_texFight;
        static readonly List<RectInt> s_rects = new List<RectInt>();
        static bool s_liveIsFight;
        static bool s_failed;
        static float s_nextTry;
        public static int Applies, Painted;
        public static string Status = "not built";

        static readonly AccessTools.FieldRef<ControllerAtlasManager, UIAtlas> s_proxyRef = AccessTools.FieldRefAccess<ControllerAtlasManager, UIAtlas>("m_proxyAtlas");
        static readonly AccessTools.FieldRef<ControllerAtlasManager, List<ControllerAtlasManager.AtlasMapping>> s_mapRef =
            AccessTools.FieldRefAccess<ControllerAtlasManager, List<ControllerAtlasManager.AtlasMapping>>("m_atlasMappings");
        static readonly AccessTools.FieldRef<UIManager, UIInputBinding> s_bindRef = AccessTools.FieldRefAccess<UIManager, UIInputBinding>("m_controllerBindings");

        public static void Apply(Harmony h)
        {
            try { h.Patch(AccessTools.Method(typeof(ControllerAtlasManager), "SetAtlas"), prefix: new HarmonyMethod(typeof(QuestPrompts), nameof(SetAtlasPrefix))); }
            catch (Exception e) { Plugin.L.LogWarning("QuestPrompts: patch failed: " + e.Message); }
        }

        void Awake()
        {
            I = this;
            Diag.StatusExtras["prompts"] = () => Diag.Q(Status + " applies " + Applies);
            Diag.Commands["prompts"] = a =>
            {
                if (a.Length > 1 && a[1] == "rebuild") { Destroy(); s_failed = false; s_nextTry = 0f; }
                if (a.Length > 1 && a[1] == "dump") return Dump();
                return Status + " | applies " + Applies + " | proxy -> " + ProxyTarget();
            };
        }

        /// The atlas the game's button prompts draw from (points at the Quest glyphs while they are on).
        internal static UIAtlas ProxyAtlas(ControllerAtlasManager m) => m != null ? s_proxyRef(m) : null;

        static UIAtlas s_plain;

        /// An atlas that always shows the plain Quest buttons (A, B, X, Y, grips LG/RG, triggers LT/RT, the left stick
        /// click), whatever the game's prompts show at the moment: for cards that explain the controls. Null until built.
        internal static UIAtlas PlainAtlas()
        {
            if (s_plain != null) return s_plain;
            if (s_xbox == null || s_texMenu == null) return null;
            s_plain = MakeAtlas("HoF2VR_QuestGlyphs", s_xbox, s_texMenu);
            return s_plain;
        }

        /// The game's sprite name for a pad control's glyph.
        internal static string SpriteName(UIManager ui, InputControlType c)
        {
            UIInputBinding bind = ui != null ? s_bindRef(ui) : null;
            if (bind == null || bind.Bindings == null) return null;
            foreach (UIInputBinding.Binding b in bind.Bindings)
                if (b != null && b.InputControlType == c && b.Data != null && !string.IsNullOrEmpty(b.Data.SpriteName)) return b.Data.SpriteName;
            return null;
        }

        static string ProxyTarget()
        {
            ControllerAtlasManager m = ControllerAtlasManager.Instance;
            UIAtlas p = m != null ? s_proxyRef(m) : null;
            return p != null && p.replacement != null ? p.replacement.name : "-";
        }

        static bool Wanted => XRSettings.enabled && Settings.QuestButtonPrompts;
        static bool FightContext => GameWatch.State == "GameState_Level_Play" || GameWatch.State == "GameState_Level_Pause";

        /// The game is about to point the proxy at a pad family's atlas: ours instead, and nothing at all when ours is set
        /// already (every replacement re-fills all NGUI widgets: 60-90 ms).
        static bool SetAtlasPrefix(ControllerAtlasManager __instance, ref bool __result)
        {
            if (!Wanted || s_menu == null) return true;
            UIAtlas proxy = s_proxyRef(__instance);
            if (proxy == null) return true;
            if (proxy.replacement != s_menu) { proxy.replacement = s_menu; Applies++; }
            __result = true;
            return false;
        }

        void Update()
        {
            if (!XRSettings.enabled) return;
            ControllerAtlasManager mgr = ControllerAtlasManager.Instance;
            if (mgr == null) return;
            UIAtlas proxy = s_proxyRef(mgr);
            if (proxy == null) return;
            if (Settings.QuestButtonPrompts && s_menu == null && !s_failed && Time.unscaledTime >= s_nextTry)
            {
                s_nextTry = Time.unscaledTime + 2f;
                TryBuild(mgr);
            }
            if (Time.frameCount % 10 != 0) return;
            UIAtlas cur = proxy.replacement;
            if (Wanted && s_menu != null)
            {
                if (cur != s_menu) { proxy.replacement = s_menu; Applies++; }
                bool fight = FightContext;
                if (fight != s_liveIsFight) ShowVariant(fight);
            }
            else if (cur != null && cur == s_menu)
            {
                // the setting was switched off: the game's own choice again
                proxy.replacement = s_xbox;
                try { mgr.InputManagerOnOnActiveDeviceChanged(InputManager.ActiveDevice); } catch (Exception) { }
            }
        }

        public static int Switches;

        /// The live texture shows the fight's or the menus' glyphs.
        static void ShowVariant(bool fight)
        {
            s_liveIsFight = fight;
            Texture2D src = fight ? s_texFight : s_texMenu;
            if (src == null || s_live == null) return;
            Switches++;
            if ((SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.Basic) != 0)
            {
                foreach (RectInt r in s_rects)
                    Graphics.CopyTexture(src, 0, 0, r.x, r.y, r.width, r.height, s_live, 0, 0, r.x, r.y);
            }
            else
            {
                s_live.SetPixels32(src.GetPixels32());
                s_live.Apply(false, false);
            }
        }

        static void Destroy()
        {
            ControllerAtlasManager mgr = ControllerAtlasManager.Instance;
            UIAtlas proxy = mgr != null ? s_proxyRef(mgr) : null;
            if (proxy != null && proxy.replacement == s_menu) proxy.replacement = s_xbox;
            if (s_menu != null) UnityEngine.Object.Destroy(s_menu.gameObject);
            s_menu = null;
            s_rects.Clear();
        }

        // ---- building the two atlases ---------------------------------------------------------------------

        enum Shape { Disc, Tab, Ring, Menu }

        struct Glyph
        {
            public Shape Shape;
            public string Text;
            public Glyph(Shape s, string t) { Shape = s; Text = t; }
        }

        static Dictionary<InputControlType, Glyph> Map(bool fight)
        {
            bool lefty = Settings.LeftHanded;
            string w = lefty ? "L" : "R", sh = lefty ? "R" : "L";
            var m = new Dictionary<InputControlType, Glyph>
            {
                { InputControlType.Action1, new Glyph(Shape.Disc, "A") },
                { InputControlType.Action2, new Glyph(Shape.Disc, "B") },
                { InputControlType.Menu, new Glyph(Shape.Menu, "") },
                { InputControlType.Start, new Glyph(Shape.Menu, "") },
                { InputControlType.View, new Glyph(Shape.Ring, "L") },
                { InputControlType.Back, new Glyph(Shape.Ring, "L") },
                // the stick clicks themselves (the abilities in Physical fights since 2026-10-17; HudIcons draws them)
                { InputControlType.LeftStickButton, new Glyph(Shape.Ring, "L") },
                { InputControlType.RightStickButton, new Glyph(Shape.Ring, "R") },
            };
            if (!fight)
            {
                m[InputControlType.Action3] = new Glyph(Shape.Disc, "X");
                m[InputControlType.Action4] = new Glyph(Shape.Disc, "Y");
                m[InputControlType.LeftBumper] = new Glyph(Shape.Tab, "LG");
                m[InputControlType.RightBumper] = new Glyph(Shape.Tab, "RG");
                m[InputControlType.LeftTrigger] = new Glyph(Shape.Tab, "LT");
                m[InputControlType.RightTrigger] = new Glyph(Shape.Tab, "RT");
            }
            else
            {
                m[InputControlType.Action3] = new Glyph(Shape.Tab, w + "T");      // attack: the weapon hand's trigger
                m[InputControlType.Action4] = new Glyph(Shape.Tab, sh + "T");     // counter: the shield hand's trigger
                m[InputControlType.LeftBumper] = new Glyph(Shape.Disc, "X");      // companion
                m[InputControlType.LeftTrigger] = new Glyph(Shape.Disc, "Y");     // prone finisher
                // the abilities: Physical (the player's playtest 2026-10-17) on the stick clicks - the shield hand's for the artefact,
                // the weapon hand's for the weapon ability; Classic presses the game's buttons with the grips (as painted
                // when the atlas is built)
                bool phys = CombatSettings.Physical;
                m[InputControlType.RightBumper] = phys ? new Glyph(Shape.Ring, sh) : new Glyph(Shape.Tab, sh + "G"); // artifact
                m[InputControlType.RightTrigger] = phys ? new Glyph(Shape.Ring, w) : new Glyph(Shape.Tab, w + "G"); // weapon ability
            }
            return m;
        }

        static void TryBuild(ControllerAtlasManager mgr)
        {
            try
            {
                UIManager ui = UIManager.Instance;
                if (ui == null) return;
                UIInputBinding bind = s_bindRef(ui);
                List<ControllerAtlasManager.AtlasMapping> maps = s_mapRef(mgr);
                if (bind == null || bind.Bindings == null || maps == null) return;
                UIAtlas xbox = null;
                foreach (ControllerAtlasManager.AtlasMapping am in maps)
                    if (am != null && am.atlas != null && am.ids != null && Array.IndexOf(am.ids, InputDeviceStyle.XboxOne) >= 0) { xbox = am.atlas; break; }
                if (xbox == null) { Status = "no Xbox One atlas"; s_failed = true; return; }
                Texture tex = xbox.texture;
                if (tex == null || xbox.spriteMaterial == null) return;
                s_xbox = xbox;
                var names = new Dictionary<InputControlType, string>();
                foreach (UIInputBinding.Binding b in bind.Bindings)
                    if (b != null && b.Data != null && !string.IsNullOrEmpty(b.Data.SpriteName) && !names.ContainsKey(b.InputControlType)) names[b.InputControlType] = b.Data.SpriteName;
                Color32[] src = ReadTexture(tex);
                bool pma = xbox.premultipliedAlpha;
                // the letters in the game's own text font (its language proxy; coordinator 2026-10-11: every text the mod
                // draws uses the game's fonts)
                if (!GameText.Ready) { Status = "waiting for the game's fonts"; return; }
                var glyphs = new GlyphFont(GameText.Normal);
                s_rects.Clear();
                s_texMenu = Paint("HoF2VR_QuestPrompts_Menu", xbox, tex, src, names, Map(false), glyphs, pma, true);
                s_texFight = Paint("HoF2VR_QuestPrompts_Fight", xbox, tex, src, names, Map(true), glyphs, pma, false);
                s_live = Paint("HoF2VR_QuestPrompts_Live", xbox, tex, src, names, Map(false), glyphs, pma, false);
                s_liveIsFight = false;
                s_menu = MakeAtlas("HoF2VR_QuestPrompts", xbox, s_live);
                Status = "built from '" + xbox.name + "' " + tex.width + "x" + tex.height + (pma ? " premultiplied" : "") + ", " + Painted + " sprites painted, letters in " + glyphs.Name;
                Plugin.L.LogInfo("QuestPrompts: " + Status);
            }
            catch (Exception e)
            {
                s_failed = true;
                Status = "failed: " + e.Message;
                Plugin.L.LogError("QuestPrompts: could not build the Quest prompts (the Xbox ones stay): " + e);
            }
        }

        /// The atlas texture's pixels (a GPU copy: game textures are not CPU-readable).
        static Color32[] ReadTexture(Texture tex)
        {
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var t2 = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, false);
            t2.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0, false);
            t2.Apply(false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            Color32[] px = t2.GetPixels32();
            UnityEngine.Object.Destroy(t2);
            return px;
        }

        /// A copy of the Xbox atlas texture with the mapped controls' sprites repainted (`rects`: remember the rectangles).
        static Texture2D Paint(string name, UIAtlas xbox, Texture tex, Color32[] src, Dictionary<InputControlType, string> names,
                               Dictionary<InputControlType, Glyph> map, GlyphFont font, bool pma, bool rects)
        {
            int w = tex.width, h = tex.height;
            var px = (Color32[])src.Clone();
            var done = new HashSet<string>();
            foreach (KeyValuePair<InputControlType, Glyph> kv in map)
            {
                string sprite;
                if (!names.TryGetValue(kv.Key, out sprite) || !done.Add(sprite)) continue;
                UISpriteData sd = xbox.GetSprite(sprite);
                if (sd == null || sd.width < 4 || sd.height < 4) continue;
                Paint(px, w, h, sd, kv.Value, font, pma);
                if (rects) { s_rects.Add(new RectInt(sd.x, h - sd.y - sd.height, sd.width, sd.height)); Painted++; }
            }
            var t2 = new Texture2D(w, h, TextureFormat.RGBA32, false, false) { name = name, filterMode = tex.filterMode, wrapMode = TextureWrapMode.Clamp };
            t2.SetPixels32(px);
            t2.Apply(false, false);
            return t2;
        }

        static UIAtlas MakeAtlas(string name, UIAtlas xbox, Texture2D t2)
        {
            var go = new GameObject(name);
            DontDestroyOnLoad(go);
            UIAtlas a = go.AddComponent<UIAtlas>();
            a.spriteMaterial = new Material(xbox.spriteMaterial) { name = name, mainTexture = t2 };
            var list = new List<UISpriteData>();
            foreach (UISpriteData sd in xbox.spriteList)
            {
                var c = new UISpriteData();
                c.CopyFrom(sd);
                list.Add(c);
            }
            a.spriteList = list;
            a.pixelSize = xbox.pixelSize;
            a.MarkSpriteListAsChanged();
            return a;
        }

        // ---- the art (CPU, straight or premultiplied alpha like the atlas) ----

        static readonly Color Body = new Color(0.13f, 0.13f, 0.15f, 1f), Rim = new Color(0.62f, 0.62f, 0.66f, 1f), Ink = new Color(0.97f, 0.97f, 0.97f, 1f);

        static void Paint(Color32[] px, int w, int h, UISpriteData sd, Glyph g, GlyphFont font, bool pma)
        {
            int x0 = sd.x, y0 = sd.y, sw = sd.width, sh = sd.height;
            // clear the sprite's rect (NGUI: y from the top; texture rows from the bottom)
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    int i = Index(w, h, x0 + x, y0 + y);
                    if (i >= 0) px[i] = new Color32(0, 0, 0, 0);
                }
            float cx = sw * 0.5f, cy = sh * 0.5f, r = Mathf.Min(sw, sh) * 0.5f - 1f;
            float rimW = Mathf.Max(1.2f, r * 0.09f);
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float d;   // signed distance to the shape's outline (px, negative inside)
                    switch (g.Shape)
                    {
                        case Shape.Tab:
                        {
                            float hw = Mathf.Min(sw * 0.5f - 1f, r * 1.35f), hh = Mathf.Min(sh * 0.5f - 1f, r * 0.82f), cr = hh * 0.55f;
                            float qx = Mathf.Abs(fx - cx) - (hw - cr), qy = Mathf.Abs(fy - cy) - (hh - cr);
                            d = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - cr;
                            break;
                        }
                        default:
                            d = new Vector2(fx - cx, fy - cy).magnitude - r;
                            break;
                    }
                    float cover = Mathf.Clamp01(0.5f - d);
                    if (cover <= 0f) continue;
                    // a light rim, a dark body (the stick ring: a wider rim)
                    float rimT = g.Shape == Shape.Ring ? rimW * 2.4f : rimW;
                    float inRim = Mathf.Clamp01(d + rimT + 0.5f);
                    Color c = Color.Lerp(Body, Rim, inRim);
                    Blend(px, w, h, x0 + x, y0 + y, c, cover, pma);
                }
            if (g.Shape == Shape.Menu)
            {
                // three bars
                float bw = r * 1.0f, bh = Mathf.Max(1.5f, r * 0.16f);
                for (int k = -1; k <= 1; k++)
                    FillRect(px, w, h, x0, y0, cx - bw * 0.5f, cy + k * r * 0.42f - bh * 0.5f, bw, bh, Ink, pma);
            }
            if (!string.IsNullOrEmpty(g.Text))
            {
                float boxW = g.Shape == Shape.Tab ? Mathf.Min(sw - 2f, r * 2.2f) : r * 1.15f;
                float boxH = g.Shape == Shape.Tab ? r * 0.95f : r * 1.1f;
                if (g.Shape == Shape.Ring) boxH = r * 0.9f;
                font.Draw(px, w, h, x0, y0, cx, cy, boxW, boxH, g.Text, Ink, pma);
            }
        }

        static int Index(int w, int h, int x, int yTop)
        {
            if (x < 0 || x >= w || yTop < 0 || yTop >= h) return -1;
            return (h - 1 - yTop) * w + x;
        }

        internal static void Blend(Color32[] px, int w, int h, int x, int yTop, Color c, float a, bool pma)
        {
            int i = Index(w, h, x, yTop);
            if (i < 0 || a <= 0f) return;
            Color32 d = px[i];
            float da = d.a / 255f;
            float oa = a + da * (1f - a);
            if (oa <= 1e-4f) { px[i] = new Color32(0, 0, 0, 0); return; }
            float r, g, b;
            if (pma)
            {
                // stored rgb is already x alpha
                r = c.r * a + d.r / 255f * (1f - a);
                g = c.g * a + d.g / 255f * (1f - a);
                b = c.b * a + d.b / 255f * (1f - a);
            }
            else
            {
                r = (c.r * a + d.r / 255f * da * (1f - a)) / oa;
                g = (c.g * a + d.g / 255f * da * (1f - a)) / oa;
                b = (c.b * a + d.b / 255f * da * (1f - a)) / oa;
            }
            px[i] = new Color32((byte)Mathf.Clamp(r * 255f + 0.5f, 0f, 255f), (byte)Mathf.Clamp(g * 255f + 0.5f, 0f, 255f),
                                (byte)Mathf.Clamp(b * 255f + 0.5f, 0f, 255f), (byte)Mathf.Clamp(oa * 255f + 0.5f, 0f, 255f));
        }

        static void FillRect(Color32[] px, int w, int h, int x0, int y0, float rx, float ry, float rw, float rh, Color c, bool pma)
        {
            for (int y = Mathf.FloorToInt(ry); y < Mathf.CeilToInt(ry + rh); y++)
                for (int x = Mathf.FloorToInt(rx); x < Mathf.CeilToInt(rx + rw); x++)
                {
                    float cov = Mathf.Clamp01(Mathf.Min(x + 1f, rx + rw) - Mathf.Max(x, rx)) * Mathf.Clamp01(Mathf.Min(y + 1f, ry + rh) - Mathf.Max(y, ry));
                    Blend(px, w, h, x0 + x, y0 + y, c, cov, pma);
                }
        }

        /// Letters from one of the game's NGUI bitmap fonts (its glyph coverage read back once from the font texture),
        /// scaled into a box.
        sealed class GlyphFont
        {
            readonly BMFont m_bm;
            readonly Color32[] m_px;
            readonly int m_w, m_h;
            readonly Rect m_uv;
            readonly float m_sx, m_sy;
            public readonly string Name;

            public GlyphFont(UIFont f)
            {
                UIFont r = f;
                for (int i = 0; i < 6 && r.replacement != null; i++) r = r.replacement;
                Name = r.name;
                m_bm = r.bmFont;
                Texture t = r.texture;
                m_w = t.width;
                m_h = t.height;
                m_px = ReadTexture(t);
                m_uv = r.uvRect;
                m_sx = m_uv.width / Mathf.Max(1, r.texWidth);
                m_sy = m_uv.height / Mathf.Max(1, r.texHeight);
            }

            float Coverage(float u, float v, int channel)
            {
                float fx = u * m_w - 0.5f, fy = v * m_h - 0.5f;
                int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
                float tx = fx - x0, ty = fy - y0;
                return Mathf.Lerp(Mathf.Lerp(A(x0, y0, channel), A(x0 + 1, y0, channel), tx), Mathf.Lerp(A(x0, y0 + 1, channel), A(x0 + 1, y0 + 1, channel), tx), ty);
            }

            float A(int x, int y, int channel)
            {
                if (x < 0 || y < 0 || x >= m_w || y >= m_h) return 0f;
                Color32 c = m_px[y * m_w + x];
                switch (channel)
                {
                    case 1: return c.b / 255f;
                    case 2: return c.g / 255f;
                    case 4: return c.r / 255f;
                    default: return c.a / 255f;
                }
            }

            /// Draws `text` centred at (cx, cy) of the sprite (px, y from the top) inside a box (bw x bh).
            public void Draw(Color32[] px, int w, int h, int x0, int y0, float cx, float cy, float bw, float bh, string text, Color ink, bool pma)
            {
                var gl = new BMGlyph[text.Length];
                var pens = new float[text.Length];
                float pen = 0f, left = float.MaxValue, right = float.MinValue, top = float.MinValue, bottom = float.MaxValue;
                for (int i = 0; i < text.Length; i++)
                {
                    BMGlyph g = m_bm.GetGlyph(text[i]);
                    gl[i] = g;
                    pens[i] = pen;
                    if (g == null) continue;
                    // NGUI's glyph box (font px, y up from the line's top): x pen+offsetX .. +width, y -offsetY-height .. -offsetY
                    left = Mathf.Min(left, pen + g.offsetX);
                    right = Mathf.Max(right, pen + g.offsetX + g.width);
                    top = Mathf.Max(top, -g.offsetY);
                    bottom = Mathf.Min(bottom, -g.offsetY - g.height);
                    pen += g.advance;
                }
                if (right <= left || top <= bottom) return;
                float s = Mathf.Min(bw / (right - left), bh / (top - bottom));
                // font space -> sprite px (y down)
                float ox = cx - (left + right) * 0.5f * s, oy = cy + (top + bottom) * 0.5f * s;
                for (int i = 0; i < text.Length; i++)
                {
                    BMGlyph g = gl[i];
                    if (g == null || g.width <= 0f || g.height <= 0f) continue;
                    float gx0 = ox + (pens[i] + g.offsetX) * s, gx1 = gx0 + g.width * s;
                    float gyTop = oy - (-g.offsetY) * s, gyBot = gyTop + g.height * s;
                    for (int y = Mathf.FloorToInt(gyTop); y < Mathf.CeilToInt(gyBot); y++)
                        for (int x = Mathf.FloorToInt(gx0); x < Mathf.CeilToInt(gx1); x++)
                        {
                            // 2 x 2 supersampling
                            float cov = 0f;
                            for (int sy = 0; sy < 2; sy++)
                                for (int sx = 0; sx < 2; sx++)
                                {
                                    float px0 = x + 0.25f + 0.5f * sx, py0 = y + 0.25f + 0.5f * sy;
                                    float u = (px0 - gx0) / (gx1 - gx0), v = (py0 - gyTop) / (gyBot - gyTop);
                                    if (u < 0f || u > 1f || v < 0f || v > 1f) continue;
                                    float tx = g.x + u * g.width, ty = g.y + v * g.height;   // glyph texels, y from the top
                                    cov += Coverage(m_uv.xMin + m_sx * tx, m_uv.yMax - m_sy * ty, g.channel);
                                }
                            cov *= 0.25f;
                            if (cov > 0.003f) Blend(px, w, h, x0 + x, y0 + y, ink, cov, pma);
                        }
                }
            }
        }

        static string Dump()
        {
            var sb = new System.Text.StringBuilder();
            UIManager ui = UIManager.Instance;
            UIInputBinding bind = ui != null ? s_bindRef(ui) : null;
            if (bind != null && bind.Bindings != null)
                foreach (UIInputBinding.Binding b in bind.Bindings)
                {
                    if (b == null || b.Data == null) continue;
                    UISpriteData sd = s_xbox != null ? s_xbox.GetSprite(b.Data.SpriteName) : null;
                    sb.Append("\n  ").Append(b.InputControlType).Append(" -> '").Append(b.Data.SpriteName).Append("'")
                      .Append(sd != null ? " " + sd.width + "x" + sd.height + " at " + sd.x + "," + sd.y : "");
                }
            Plugin.L.LogInfo("QuestPrompts bindings:" + sb);
            return "logged";
        }
    }
}

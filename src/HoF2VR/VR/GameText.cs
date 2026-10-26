using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// The game's own fonts and UI art for everything the mod writes (user 2026-10-11 about the waiting-space text: "generic,
    /// flat, and boring ... I want it to fit the games' aesthetics"; coordinator: "Every piece of text the mod draws itself
    /// must use these game fonts, not a Unity default font").
    /// The fonts are NGUI bitmap fonts behind the game's language proxies (LanguageManager points each proxy at the current
    /// language's font, so translations keep working). The slots, as the game itself uses them (runtime survey 2026-10-11):
    ///   Normal   (FontNormalProxy -> Lapture-Semibold): the Dealer's encounter text, menu entries' subtitles, button prompts
    ///   Stylised (FontStylisedProxy -> Behrensschrift): titles and names ("Paused", the death screen's title, challenge names)
    ///   CardDecal (FontCardDecalProxy -> BehrensschriftCardDecal): everything printed on cards
    /// They come with the shared_ui bundle; until the game has loaded it, Ready is false and the mod writes nothing.
    /// Text is drawn as NGUI widgets in the world (a UIPanel on the layer of the camera that should draw it), like the
    /// game's own cards.
    internal static class GameText
    {
        public static UIFont Normal, Stylised, StylisedItalic, CardDecal;
        public static UIAtlas Atlas;          // the game's main UI atlas (frames, banners, dividers, glows)
        static float s_next;
        public static int Lookups;

        // the game's own text colours (menus, subtitles, the Dealer's text)
        public static readonly Color MenuWhite = new Color(0.988f, 1f, 0.882f, 1f);
        public static readonly Color Parchment = new Color(1f, 0.973f, 0.902f, 1f);
        public static readonly Color DealerGold = new Color(0.718f, 0.612f, 0.435f, 1f);
        // gilded lettering: bright gold at the top of a line, deep gold at the bottom; a dark brown rim
        public static readonly Color GoldTop = new Color(1f, 0.93f, 0.66f, 1f);
        public static readonly Color GoldBottom = new Color(0.86f, 0.56f, 0.20f, 1f);
        public static readonly Color Rim = new Color(0.10f, 0.055f, 0.02f, 0.9f);

        public static bool Ready { get { Find(); return Usable(Normal) && Usable(Stylised); } }
        public static bool AtlasReady { get { Find(); return Atlas != null && Atlas.texture != null; } }

        static bool Usable(UIFont f) => f != null && f.bmFont != null && f.bmFont.isValid && f.texture != null;

        public static void Find()
        {
            if (Usable(Normal) && Usable(Stylised) && Usable(CardDecal) && Atlas != null) return;
            if (Time.unscaledTime < s_next) return;
            s_next = Time.unscaledTime + 1f;
            Lookups++;
            foreach (UIFont f in Resources.FindObjectsOfTypeAll<UIFont>())
            {
                if (f == null) continue;
                switch (f.name)
                {
                    case "FontNormalProxy": Normal = f; break;
                    case "FontStylisedProxy": Stylised = f; break;
                    case "FontStylisedItalicProxy": StylisedItalic = f; break;
                    case "FontCardDecalProxy": CardDecal = f; break;
                }
            }
            if (Atlas == null)
                foreach (UIAtlas a in Resources.FindObjectsOfTypeAll<UIAtlas>())
                    if (a != null && a.name == "UIAtlas" && a.texture != null) { Atlas = a; break; }
            if (Usable(Normal) && Usable(Stylised))
                Plugin.L.LogInfo("GameText: the game's fonts: normal -> " + Resolve(Normal) + ", stylised -> " + Resolve(Stylised) + ", card -> " + Resolve(CardDecal) +
                                 ", atlas " + (Atlas != null ? Atlas.name : "-") + " (lookup " + Lookups + ")");
        }

        /// The font a proxy points at now (for the log).
        public static string Resolve(UIFont f)
        {
            if (f == null) return "-";
            UIFont r = f;
            for (int i = 0; i < 6 && r.replacement != null; i++) r = r.replacement;
            return r.name;
        }

        /// The panel that draws a group of widgets: on `go`'s layer, in the world at go's transform (1 unit = 1 pixel of the
        /// game's UI; scale the transform to metres), drawn from render queue `queue` on.
        public static UIPanel Panel(GameObject go, int queue)
        {
            UIPanel p = go.AddComponent<UIPanel>();
            p.renderQueue = UIPanel.RenderQueue.StartAt;
            p.startingRenderQueue = queue;
            return p;
        }

        public static UILabel Label(Transform parent, string name, UIFont font, int size, Color color, int depth,
                                    UIWidget.Pivot pivot = UIWidget.Pivot.Center, NGUIText.Alignment align = NGUIText.Alignment.Center)
        {
            var go = new GameObject(name) { layer = parent.gameObject.layer };
            go.transform.SetParent(parent, false);
            UILabel l = go.AddComponent<UILabel>();
            l.bitmapFont = font;
            l.fontSize = size;
            l.overflowMethod = UILabel.Overflow.ResizeFreely;
            l.alignment = align;
            l.pivot = pivot;
            l.color = color;
            l.depth = depth;
            l.supportEncoding = false;
            return l;
        }

        /// Gilded lettering: a bright-to-deep gold gradient with a thin dark rim.
        public static void Gild(UILabel l, float rim = 1.5f)
        {
            l.color = Color.white;
            l.applyGradient = true;
            l.gradientTop = GoldTop;
            l.gradientBottom = GoldBottom;
            l.effectStyle = UILabel.Effect.Outline8;
            l.effectColor = Rim;
            l.effectDistance = new Vector2(rim, rim);
        }

        /// A soft drop shadow: a dark copy of a label, offset down-right and set a little behind it (depth in stereo).
        public static UILabel Shadow(UILabel of, Vector2 offset, float behind, float alpha)
        {
            UILabel s = Label(of.transform.parent, of.name + "_shadow", of.bitmapFont, of.fontSize, new Color(0f, 0f, 0f, alpha), of.depth - 1, of.pivot, of.alignment);
            s.effectStyle = UILabel.Effect.Outline8;
            s.effectColor = new Color(0f, 0f, 0f, alpha * 0.5f);
            s.effectDistance = new Vector2(2.5f, 2.5f);
            s.spacingX = of.spacingX;
            s.transform.localPosition = of.transform.localPosition + new Vector3(offset.x, offset.y, behind);
            return s;
        }

        public static UISprite Sprite(Transform parent, string name, UIAtlas atlas, string sprite, Color color, int depth, int w, int h,
                                      UIBasicSprite.Type type = UIBasicSprite.Type.Sliced)
        {
            var go = new GameObject(name) { layer = parent.gameObject.layer };
            go.transform.SetParent(parent, false);
            UISprite s = go.AddComponent<UISprite>();
            s.atlas = atlas;
            s.spriteName = sprite;
            s.type = type;
            s.color = color;
            s.depth = depth;
            s.width = w;
            s.height = h;
            return s;
        }

        public static bool HasSprite(UIAtlas atlas, string sprite) => atlas != null && atlas.GetSprite(sprite) != null;

        /// The game's button glyph for a pad control (the atlas the game's own prompts use: the Quest glyphs when
        /// Controls.QuestButtonPrompts is on).
        public static bool PromptGlyph(InControl.InputControlType control, out UIAtlas atlas, out string sprite)
        {
            atlas = null;
            sprite = null;
            try
            {
                ControllerAtlasManager m = ControllerAtlasManager.Instance;
                UIManager ui = UIManager.Instance;
                if (m == null || ui == null) return false;
                atlas = QuestPrompts.ProxyAtlas(m);
                sprite = QuestPrompts.SpriteName(ui, control);
                return atlas != null && sprite != null && atlas.GetSprite(sprite) != null;
            }
            catch (System.Exception) { return false; }
        }

        /// Width of a label's text in its pixels (NGUI lays out at once when the text is set).
        public static Vector2 Size(UILabel l) => string.IsNullOrEmpty(l.text) ? Vector2.zero : new Vector2(l.width, l.height);

        /// Lines of the form "input|action" in two aligned columns (the controls cards): left and right column texts.
        public static void Columns(string text, out string head, out string left, out string right, out bool hasColumns)
        {
            var l = new System.Text.StringBuilder();
            var r = new System.Text.StringBuilder();
            head = null;
            hasColumns = false;
            string[] lines = text.Split('\n');
            int start = 0;
            // a first line in capitals followed by an empty line is the card's title
            if (lines.Length > 2 && lines[0].Length > 0 && lines[0].IndexOf('|') < 0 && lines[0] == lines[0].ToUpperInvariant() && lines[1].Trim().Length == 0)
            {
                head = lines[0];
                start = 2;
            }
            for (int i = start; i < lines.Length; i++)
            {
                string line = lines[i];
                int bar = line.IndexOf('|');
                if (bar >= 0) hasColumns = true;
                l.Append(bar < 0 ? line : line.Substring(0, bar)).Append('\n');
                r.Append(bar < 0 ? "" : line.Substring(bar + 1)).Append('\n');
            }
            left = l.ToString().TrimEnd('\n');
            right = r.ToString().TrimEnd('\n');
        }

        /// "FIGHTING IN VR" -> "Fighting in VR" (the game's titles are in mixed case; Behrensschrift capitals are ornate).
        public static string TitleCase(string s)
        {
            if (string.IsNullOrEmpty(s) || s != s.ToUpperInvariant()) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            bool start = true;
            string[] words = s.ToLowerInvariant().Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                if (i > 0) sb.Append(' ');
                if (w.Length == 0) continue;
                if (w == "vr") { sb.Append("VR"); continue; }
                bool small = i > 0 && (w == "in" || w == "of" || w == "the" || w == "a" || w == "to" || w == "and");
                sb.Append(start || !small ? char.ToUpperInvariant(w[0]) + w.Substring(1) : w);
                start = false;
            }
            return sb.ToString();
        }
    }
}

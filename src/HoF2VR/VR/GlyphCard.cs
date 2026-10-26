using System.Collections.Generic;
using InControl;
using UnityEngine;

namespace HoF2VR
{
    /// The fighting controls before a fight, with the controllers' real buttons drawn the way the game draws its prompts
    /// (user 2026-10-11 evening: "can you put the actual button symbols like the game uses rather than just text? That
    /// whole text block can look much better"). The game's card style (dark fill, gold rim, gilded title, double divider)
    /// with one row per control: the Quest button glyphs (QuestPrompts' painted atlas, plain variant) or, for the motions,
    /// the motion in gold, then what it does in parchment.
    internal sealed class GlyphCard
    {
        public readonly Transform Root;
        public float WidthPx, HeightPx;
        readonly float m_mpp;
        readonly UISprite m_back, m_fill, m_divider;
        readonly UILabel m_title;
        readonly List<Component> m_rows = new List<Component>();
        bool m_built;
        int m_key = -1;
        /// Built for the current settings (the combat mode, walking and handedness pick the rows; a change rebuilds it).
        public bool Built => m_built && m_key == Key();

        // (the dash's rules and its flick, and the difficulty, are on the card too: a change rebuilds it, also while it is shown)
        static int Key() => (Settings.LeftHanded ? 1 : 0) + (int)CombatSettings.Walking * 2 + (int)CombatSettings.Mode * 4 +
                            (CombatSettings.UyfDash ? 8 : 0) + (CombatSettings.DashFlick ? 16 : 0) + (int)CombatSettings.Difficulty * 32;

        struct Row
        {
            public InputControlType[] Glyphs;   // empty: a motion (Motion text instead)
            public string Motion, Action;
            public Row(string motion, string action) { Glyphs = new InputControlType[0]; Motion = motion; Action = action; }
            public Row(string action, params InputControlType[] g) { Glyphs = g; Motion = null; Action = action; }
        }

        public GlyphCard(Transform parent, string name, int layer, int queue, float metresPerPx)
        {
            m_mpp = metresPerPx;
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * metresPerPx;
            Root = go.transform;
            GameText.Panel(go, queue);
            UIAtlas at = GameText.Atlas;
            m_fill = GameText.Sprite(Root, "fill", at, "gui_black", new Color(0.035f, 0.028f, 0.02f, 0.9f), -1, 200, 200, UIBasicSprite.Type.Simple);
            m_fill.transform.localPosition = new Vector3(0f, 0f, 5f);
            m_back = GameText.Sprite(Root, "back", at, "gui_border", new Color(0.93f, 0.76f, 0.47f, 0.97f), 0, 200, 200);
            m_back.transform.localPosition = new Vector3(0f, 0f, 4f);
            m_divider = GameText.Sprite(Root, "divider", at, "gui_divider_double", new Color(0.86f, 0.67f, 0.36f, 1f), 2, 300, 19);
            m_title = GameText.Label(Root, "title", GameText.Stylised, 44, Color.white, 4);
            GameText.Gild(m_title, 1.2f);
        }

        static Row[] Rows()
        {
            bool lefty = Settings.LeftHanded;
            InputControlType wT = lefty ? InputControlType.LeftTrigger : InputControlType.RightTrigger, sT = lefty ? InputControlType.RightTrigger : InputControlType.LeftTrigger;
            InputControlType wG = lefty ? InputControlType.LeftBumper : InputControlType.RightBumper, sG = lefty ? InputControlType.RightBumper : InputControlType.LeftBumper;
            // A: the Dash with the mod's walking (both modes) - its words follow [Combat] Dash (UYF's: no claim of safety, Short's:
            // its moment in which nothing hits you; VRHelp.DashAction); the game's own roll with Walking = Game
            bool direct = CombatSettings.Walking == CombatSettings.WalkMode.Direct;
            Row a = direct
                ? new Row(CombatSettings.UyfDash ? "Dash toward the left stick - forward onto an enemy, hitting it" : "Dash toward the left stick - nothing hits you",
                          InputControlType.Action1)
                : new Row("Dodge roll - the left stick sets the direction", InputControlType.Action1);
            // the right stick's flick dashes too ([Combat] DashFlick)
            bool flick = direct && CombatSettings.DashFlick;
            Row f = new Row(VRHelp.FlickMotion, VRHelp.FlickAction);
            Row[] rows;
            if (CombatSettings.Physical)
                // Physical combat mode: the rules card (docs/combat-uyf-design.md §8.5.1) and the abilities (§8.5.9)
                rows = new[]
                {
                    new Row("Swing for real", "Hits and blocks wear down its guard (white bar)"),
                    new Row("Guard broken: a glowing slash on it", CombatSettings.CritsOnly ? "Cut along it (either way): only that hurts" : "Cut along it (either way): a crit"),
                    new Row("Glowing bar: where the blow will pass", "Shield or sword on it (it turns white): block"),
                    new Row("Arrow: you can't block it", "Duck or lean the way the arrow points"),
                    CombatSettings.Counter != CombatSettings.CounterRule.Off ? new Row("Gold bar: meet the blow", "Counter, then hit it: the riposte")
                        : new Row("Step back out of reach", "The bar dims: the blow can't reach you"),
                    new Row("Shield into an enemy", "Bash. Downed: finish it. Armour: quick hits"),
                    a,
                    f,
                    new Row("Hold, point, let go: ability / artifact", wG, sG),
                    new Row("Companion / finisher / bash", InputControlType.Action3, InputControlType.Action4, InputControlType.Action2),
                    new Row("Pause (hold)", InputControlType.Back),
                    new Row("Left stick / right stick", "Move / turn"),
                    new Row("Heartbeat on one side, edge marker, hiss", "Attack from behind: guard toward it, or dash"),
                    new Row("After a win: the purple portal", "Walk into it or touch it (or hold A)"),
                    new Row("Two-hander: free hand's grip on the haft", "Two hands: steadier, slashes easier to hit"),
                    new Row("Difficulty " + DifficultyName() + " (pause, VR Settings)", "Easy to Nightmare 3: less or more warning, faster foes"),
                };
            else rows = new[]
            {
                new Row("Swing the sword", "Attack - keep swinging for combos"),
                new Row("Jab the shield as an enemy flashes", "Counter"),
                new Row("Thrust the shield forward", "Bash - breaks guards and armour"),
                new Row("Stab down at a fallen enemy", "Finisher"),
                a,
                f,
                new Row("Attack / counter / bash", wT, sT, InputControlType.Action2),
                new Row("Weapon ability / artifact", wG, sG),
                new Row("Companion / finisher", InputControlType.Action3, InputControlType.Action4),
                new Row("Pause (hold)", InputControlType.Back),
                new Row("Left stick / right stick", "Move / turn"),
                new Row("Heartbeat on one side, edge marker, hiss", "Attack from behind: turn, counter or dodge"),
                new Row("After a win: the purple portal", "Walk into it or touch it (or hold A)"),
            };
            return flick ? rows : System.Array.FindAll(rows, r => r.Motion != VRHelp.FlickMotion);
        }

        /// The rows as text ("motion or buttons | action"), for the `controlscard?` seam.
        static string DifficultyName()
        {
            switch (CombatSettings.Difficulty)
            {
                case CombatSettings.DifficultyLevel.Nightmare1: return "Nightmare 1";
                case CombatSettings.DifficultyLevel.Nightmare2: return "Nightmare 2";
                case CombatSettings.DifficultyLevel.Nightmare3: return "Nightmare 3";
                default: return CombatSettings.Difficulty.ToString();
            }
        }

        public static string RowText()
        {
            var sb = new System.Text.StringBuilder();
            foreach (Row r in Rows())
            {
                if (sb.Length > 0) sb.Append(" || ");
                if (r.Glyphs.Length == 0) sb.Append(r.Motion);
                else for (int i = 0; i < r.Glyphs.Length; i++) sb.Append(i > 0 ? "/" : "").Append(r.Glyphs[i]);
                sb.Append(" | ").Append(r.Action);
            }
            return sb.ToString();
        }

        /// Builds the rows (once the game's prompts and fonts exist). False = not yet possible.
        public bool Build()
        {
            UIAtlas glyphs = QuestPrompts.PlainAtlas();
            UIManager ui = UIManager.Instance;
            if (glyphs == null || ui == null || !GameText.Ready) return false;
            foreach (Component c in m_rows) if (c != null) Object.Destroy(c.gameObject);
            m_rows.Clear();
            m_title.text = "Fighting in VR";
            const float pad = 34f, rowH = 50f, glyphPx = 42f, gap = 30f, titleGap = 12f;
            Row[] rows = Rows();
            // the left column: glyphs ("/" between them) or the motion; its width is the widest entry
            var lefts = new List<List<Component>>();
            var widths = new List<float>();
            float leftW = 0f;
            foreach (Row r in rows)
            {
                var parts = new List<Component>();
                float rw = 0f;
                if (r.Glyphs.Length == 0)
                {
                    UILabel m = GameText.Label(Root, "motion", GameText.Normal, 26, new Color(1f, 0.85f, 0.55f, 1f), 4, UIWidget.Pivot.Left, NGUIText.Alignment.Left);
                    m.text = r.Motion;
                    parts.Add(m);
                    rw = m.width;
                }
                else
                    for (int i = 0; i < r.Glyphs.Length; i++)
                    {
                        if (i > 0)
                        {
                            UILabel sl = GameText.Label(Root, "slash", GameText.Normal, 26, GameText.Parchment, 4, UIWidget.Pivot.Left, NGUIText.Alignment.Left);
                            sl.text = "/";
                            parts.Add(sl);
                            rw += sl.width + 8f;
                        }
                        string sn = QuestPrompts.SpriteName(ui, r.Glyphs[i]);
                        UISprite g = GameText.Sprite(Root, "glyph", glyphs, sn ?? "", Color.white, 4, (int)glyphPx, (int)glyphPx, UIBasicSprite.Type.Simple);
                        g.pivot = UIWidget.Pivot.Left;
                        if (sn != null) { UISpriteData sd = glyphs.GetSprite(sn); if (sd != null && sd.height > 0) g.width = Mathf.RoundToInt(glyphPx * sd.width / (float)sd.height); }
                        parts.Add(g);
                        rw += g.width + 8f;
                    }
                lefts.Add(parts);
                widths.Add(rw);
                leftW = Mathf.Max(leftW, rw);
            }
            float rightW = 0f;
            var rights = new List<UILabel>();
            foreach (Row r in rows)
            {
                UILabel a = GameText.Label(Root, "action", GameText.Normal, 26, GameText.Parchment, 4, UIWidget.Pivot.Left, NGUIText.Alignment.Left);
                a.text = r.Action;
                rights.Add(a);
                rightW = Mathf.Max(rightW, a.width);
                m_rows.Add(a);
            }
            float titleW = m_title.width, th = m_title.height;
            float w = Mathf.Max(leftW + gap + rightW, titleW + 160f) + 2f * pad;
            float h = pad + th + titleGap + 19f + titleGap + rows.Length * rowH + pad * 0.6f;
            float y = h * 0.5f - pad, x0 = -w * 0.5f + pad;
            m_title.transform.localPosition = new Vector3(0f, y - th * 0.5f, 0f);
            y -= th + titleGap;
            m_divider.width = Mathf.RoundToInt(Mathf.Min(w - 2f * pad, Mathf.Max(titleW + 160f, 320f)));
            m_divider.transform.localPosition = new Vector3(0f, y - 9.5f, 1f);
            y -= 19f + titleGap;
            for (int i = 0; i < rows.Length; i++)
            {
                float cy = y - rowH * 0.5f, x = x0;
                foreach (Component c in lefts[i])
                {
                    c.transform.localPosition = new Vector3(Mathf.Round(x), Mathf.Round(cy), 0f);
                    UIWidget wd = c as UIWidget;
                    x += (wd != null ? wd.width : 0f) + 8f;
                    m_rows.Add(c);
                }
                rights[i].transform.localPosition = new Vector3(Mathf.Round(x0 + leftW + gap), Mathf.Round(cy), 0f);
                y -= rowH;
            }
            m_back.width = Mathf.RoundToInt(w + 12f);
            m_back.height = Mathf.RoundToInt(h + 12f);
            m_fill.width = Mathf.RoundToInt(w - 4f);
            m_fill.height = Mathf.RoundToInt(h - 4f);
            WidthPx = w;
            HeightPx = h;
            m_built = true;
            m_key = Key();
            return true;
        }

        public float Width => WidthPx * m_mpp;
        public float Height => HeightPx * m_mpp;
    }
}

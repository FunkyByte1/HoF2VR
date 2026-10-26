using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// A card of text in the game's own style: a dark panel with a gold rim (the game's gui_border), a gilded title in the
    /// game's title font with its double divider under it, and the body in the game's text font - lines of the form
    /// "input|action" in two aligned columns (input in gold, action in parchment), other lines across the card.
    /// Used by the waiting-space board (the fighting controls) and VRHelp (notes in front of the player).
    internal sealed class TextCard
    {
        public readonly Transform Root;
        readonly UISprite m_back, m_fill, m_divider;
        readonly UILabel m_title, m_left, m_right, m_measure;
        public float WidthPx, HeightPx;
        public readonly float MetresPerPx;

        public float Width => WidthPx * MetresPerPx;
        public float Height => HeightPx * MetresPerPx;

        public TextCard(Transform parent, string name, int layer, int queue, float metresPerPx, int bodySize = 26, int titleSize = 44)
        {
            MetresPerPx = metresPerPx;
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * metresPerPx;
            Root = go.transform;
            GameText.Panel(go, queue);
            UIAtlas at = GameText.Atlas;
            // a dark fill (the frame's own middle is see-through) under the game's gold-rimmed border
            m_fill = GameText.Sprite(Root, "fill", at, "gui_black", new Color(0.035f, 0.028f, 0.02f, 0.9f), -1, 200, 200, UIBasicSprite.Type.Simple);
            m_fill.transform.localPosition = new Vector3(0f, 0f, 5f);
            m_back = GameText.Sprite(Root, "back", at, "gui_border", new Color(0.93f, 0.76f, 0.47f, 0.97f), 0, 200, 200);
            m_back.transform.localPosition = new Vector3(0f, 0f, 4f);
            m_divider = GameText.Sprite(Root, "divider", at, "gui_divider_double", new Color(0.86f, 0.67f, 0.36f, 1f), 2, 300, 19);
            m_title = GameText.Label(Root, "title", GameText.Stylised, titleSize, Color.white, 4);
            GameText.Gild(m_title, 1.2f);
            m_left = GameText.Label(Root, "left", GameText.Normal, bodySize, new Color(1f, 0.85f, 0.55f, 1f), 4, UIWidget.Pivot.TopLeft, NGUIText.Alignment.Left);
            m_right = GameText.Label(Root, "right", GameText.Normal, bodySize, GameText.Parchment, 4, UIWidget.Pivot.TopLeft, NGUIText.Alignment.Left);
            m_measure = GameText.Label(Root, "measure", GameText.Normal, bodySize, new Color(0f, 0f, 0f, 0f), 1, UIWidget.Pivot.TopLeft, NGUIText.Alignment.Left);
        }

        /// The body colour of lines without columns (a plain note: parchment instead of the inputs' gold).
        public void Set(string text)
        {
            string head, left, right;
            bool cols;
            GameText.Columns(text, out head, out left, out right, out cols);
            m_title.text = head != null ? GameText.TitleCase(head) : "";
            m_left.color = cols ? new Color(1f, 0.85f, 0.55f, 1f) : GameText.Parchment;
            m_left.overflowWidth = 0;
            m_left.text = left;
            m_right.text = cols ? right : "";
            var sb = new StringBuilder();
            foreach (string line in text.Split('\n'))
            {
                int bar = line.IndexOf('|');
                if (bar >= 0) sb.Append(line.Substring(0, bar)).Append('\n');
            }
            m_measure.text = sb.Length > 0 ? sb.ToString().TrimEnd('\n') : "";
            Layout();
        }

        void Layout()
        {
            const float pad = 34f, gap = 34f, titleGap = 12f;
            float colW = m_measure.text.Length > 0 ? m_measure.width : 0f;
            float rightW = m_right.text.Length > 0 ? m_right.width : 0f;
            float colsW = rightW > 0f ? colW + gap + rightW : 0f;
            float titleW = m_title.text.Length > 0 ? m_title.width : 0f;
            float bodyW = Mathf.Max(colsW, Mathf.Max(titleW, 420f));
            // lines across the card (notes) wrap at the columns' width
            m_left.overflowWidth = Mathf.RoundToInt(Mathf.Max(bodyW, 420f));
            m_left.ProcessText();
            float leftW = m_left.width;
            float w = Mathf.Max(bodyW, leftW) + 2f * pad;
            bool hasTitle = titleW > 0f;
            float th = hasTitle ? m_title.height : 0f;
            float bh = Mathf.Max(m_left.height, rightW > 0f ? m_right.height : 0f);
            float h = pad + (hasTitle ? th + titleGap + 19f + titleGap : 0f) + bh + pad * 0.8f;
            float top = h * 0.5f, x0 = -w * 0.5f + pad;
            float y = top - pad;
            m_title.gameObject.SetActive(hasTitle);
            m_divider.gameObject.SetActive(hasTitle);
            if (hasTitle)
            {
                m_title.transform.localPosition = new Vector3(0f, y - th * 0.5f, 0f);
                y -= th + titleGap;
                m_divider.width = Mathf.RoundToInt(Mathf.Min(w - 2f * pad, Mathf.Max(titleW + 160f, 320f)));
                m_divider.transform.localPosition = new Vector3(0f, y - 9.5f, 1f);
                y -= 19f + titleGap;
            }
            m_left.transform.localPosition = new Vector3(x0, y, 0f);
            m_measure.transform.localPosition = new Vector3(x0, y, 0f);
            m_right.transform.localPosition = new Vector3(x0 + colW + gap, y, 0f);
            m_right.gameObject.SetActive(rightW > 0f);
            m_back.width = Mathf.RoundToInt(w + 12f);
            m_back.height = Mathf.RoundToInt(h + 12f);
            m_fill.width = Mathf.RoundToInt(w - 4f);
            m_fill.height = Mathf.RoundToInt(h - 4f);
            WidthPx = w;
            HeightPx = h;
        }
    }
}

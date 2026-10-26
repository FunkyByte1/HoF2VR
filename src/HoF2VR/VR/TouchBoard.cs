using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// A board of the mod's own buttons within arm's reach (the hand check; the `tboard` test board): a text card in the
    /// game's fonts and frame (TextCard) above a row of plates - a dark backing, a gold frame that warms up as a fingertip
    /// comes near, and a label in the game's font. The plates are pressed through the shared plate press (PlateTouch,
    /// family Board), so they have the same travel, click, release, assist, stop and ghost as the encounter's choices.
    internal sealed class TouchBoard
    {
        public static float Ahead = 0.42f, Down = 0.26f, PlateW = 0.12f, PlateH = 0.05f, Gap = 0.018f, TextMpp = 0.00042f;
        /// Buttons per row (0 = one row); rows go downward. Plate width for this board (0 = PlateW).
        public int Columns;
        /// Buttons in each row, top down (null = Columns for every row): e.g. { 4, 4, 4, 3, 2 }.
        public int[] Rows;
        /// Pressable in the waiting space too (the place hold keeps the view "black" underneath the campfire space).
        public bool InSpace;
        /// The plates' label size (1 = the hand check's; a lone big button reads better larger: GoPlates' Continue 1.5).
        public float LabelScale = 1f;
        public float Width;
        /// Rows with a slider (M1, 2026-10-17): row r < Sliders.Length holds its two plates (- and +) at the ends of the row and
        /// Sliders[r] between them (null: a plain row). The plates' height for this board (0 = PlateH), how far below the eyes it
        /// stands (NaN = Down).
        public TouchSlider[] Sliders;
        public float SliderLength = 0.20f, Height, DownHere = float.NaN;
        public readonly string Name;
        public readonly PlateTouch Touch;
        public Action<int, Hand> Pressed;
        public bool Visible => m_root != null && m_root.gameObject.activeSelf;
        public int Count => m_count;

        sealed class Plate : PressPlate
        {
            public Transform Back, Frame;
            public Mesh FrameMesh, BackMesh;
            public UILabel Label;
            public Color Shown = Color.clear, BackShown = Color.clear;
            public float RowY;   // its row's height on the board (the grid)
            public bool Selected;   // a choice in effect (lit gold, as on the VR settings page)
        }

        Transform m_root, m_textRoot;
        TextCard m_text;
        readonly List<Plate> m_plates = new List<Plate>();
        int m_count;
        Vector3 m_centre;
        Quaternion m_rot = Quaternion.identity;
        Material m_backMat, m_frameMat;
        float m_textHeight;

        public TouchBoard(string name)
        {
            Name = name;
            Touch = new PlateTouch(name, PressFamily.Board);
            Touch.OnHover = (i, h) => true;
            Touch.OnPress = (i, h) =>
            {
                if (i >= m_count || m_plates[i].Disabled) return "disabled";
                Pressed?.Invoke(i, h);
                return null;
            };
            Touch.Label = i => i < m_plates.Count && m_plates[i].Label != null ? m_plates[i].Label.text : (i + 1).ToString();
        }

        PressPlate At(int i) => i < m_count ? m_plates[i] : null;

        bool Ensure()
        {
            if (m_root != null) return true;
            if (!GameText.Ready || !GameText.AtlasReady || VRUI.I == null) return false;
            var go = new GameObject("HoF2VR_Board_" + Name) { layer = VRUI.Layer };
            UnityEngine.Object.DontDestroyOnLoad(go);
            m_root = go.transform;
            GameText.Panel(go, 5106);   // the labels, after the backings and frames
            var tr = new GameObject("text") { layer = VRUI.Layer };
            tr.transform.SetParent(m_root, false);
            m_textRoot = tr.transform;
            m_text = new TextCard(m_textRoot, "card", VRUI.Layer, 5100, TextMpp, 26, 40);
            Shader sh = Shader.Find("Sprites/Default");
            m_backMat = new Material(sh) { name = "HoF2VR_BoardBack", renderQueue = 5101 };
            m_frameMat = new Material(sh) { name = "HoF2VR_BoardFrame", renderQueue = 5102 };
            go.SetActive(false);
            return true;
        }

        /// Show the board in front of the player (where they look, levelled), with this text and these buttons.
        public bool Show(string text, params string[] buttons) => Show(true, text, buttons);

        /// `place` = put it in front of the player now (else it stays where it is).
        public bool Show(bool place, string text, params string[] buttons)
        {
            if (!Ensure()) return false;
            m_root.gameObject.SetActive(true);
            SetText(text);
            SetButtons(buttons);
            if (place) PlaceInFront(); else Layout();
            return true;
        }

        public void Hide()
        {
            if (m_root != null) m_root.gameObject.SetActive(false);
            for (int i = 0; i < m_plates.Count; i++) m_plates[i].On = false;
        }

        public void SetText(string text)
        {
            if (m_text == null) return;
            m_text.Set(text);
            m_textHeight = m_text.Height;
            if (m_textRoot != null) m_textRoot.gameObject.SetActive(!string.IsNullOrEmpty(text));   // a lone button needs no card
            Layout();
        }

        public void SetButtons(params string[] labels)
        {
            m_count = labels.Length;
            for (int i = 0; i < m_count; i++)
            {
                Plate p = PlateAt(i);
                p.Label.text = labels[i];
                p.Label.transform.parent.localScale = Vector3.one * (0.00042f * LabelScale);
                p.On = true;
                p.Disabled = false;
                p.Selected = false;
                p.T.gameObject.SetActive(true);
            }
            for (int i = m_count; i < m_plates.Count; i++) { m_plates[i].On = false; m_plates[i].T.gameObject.SetActive(false); }
            Layout();
        }

        /// A slider on this board (its row set by the order in Sliders).
        public TouchSlider NewSlider(string name)
        {
            if (!Ensure()) return null;
            return new TouchSlider(m_root, name, VRUI.Layer, m_backMat);
        }

                public void SetDisabled(int i, bool off) { if (i < m_count) m_plates[i].Disabled = off; }
        public void SetSelected(int i, bool on) { if (i < m_count) m_plates[i].Selected = on; }

        Plate PlateAt(int i)
        {
            while (m_plates.Count <= i)
            {
                var p = new Plate();
                var go = new GameObject("plate" + (m_plates.Count + 1)) { layer = VRUI.Layer };
                go.transform.SetParent(m_root, false);
                p.T = go.transform;
                p.Back = Quad("back", p.T, m_backMat, BackIdle);
                p.BackMesh = p.Back.GetComponent<MeshFilter>().sharedMesh;
                p.BackShown = BackIdle;
                var fgo = new GameObject("frame") { layer = VRUI.Layer };
                fgo.transform.SetParent(p.T, false);
                p.FrameMesh = new Mesh { name = "HoF2VR_BoardFrame" };
                fgo.AddComponent<MeshFilter>().sharedMesh = p.FrameMesh;
                var fr = fgo.AddComponent<MeshRenderer>();
                fr.sharedMaterial = m_frameMat;
                p.Frame = fgo.transform;
                var lh = new GameObject("label") { layer = VRUI.Layer };
                lh.transform.SetParent(p.T, false);
                lh.transform.localPosition = new Vector3(0f, 0f, -0.0008f);
                lh.transform.localScale = Vector3.one * 0.00042f;
                p.Label = GameText.Label(lh.transform, "text", GameText.Normal, 30, GameText.Parchment, 10);
                m_plates.Add(p);
            }
            return m_plates[i];
        }

        static Transform Quad(string name, Transform parent, Material m, Color c)
        {
            var go = new GameObject(name) { layer = VRUI.Layer };
            go.transform.SetParent(parent, false);
            var mesh = new Mesh { name = "HoF2VR_BoardQuad" };
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            mesh.triangles = new[] { 0, 1, 2, 1, 0, 3 };
            Color lin = c.linear;
            lin.a = c.a;
            mesh.colors = new[] { lin, lin, lin, lin };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        /// The board's middle at a world point, turned this way (+z away from the viewer).
        public void PlaceAt(Vector3 centre, Quaternion rot)
        {
            m_centre = centre;
            m_rot = rot;
            Layout();
        }

        /// Where the board stands: ahead of the eyes at arm's length, a little below, facing them (yaw only).
        public void PlaceInFront()
        {
            if (VRRig.I == null) return;
            float sc = VRRig.I.Scale;
            Quaternion yaw = Quaternion.Euler(0f, VRRig.I.HeadWorldRot.eulerAngles.y, 0f);
            Vector3 eye = VRRig.I.HeadWorldPos;
            m_centre = eye + yaw * (new Vector3(0f, -(float.IsNaN(DownHere) ? Down : DownHere), Ahead) * sc);
            m_rot = Quaternion.LookRotation(m_centre - eye, yaw * Vector3.up);
            Layout();
        }

        void Layout()
        {
            if (m_root == null || VRRig.I == null) return;
            float sc = VRRig.I.Scale;
            m_root.SetPositionAndRotation(m_centre, m_rot);
            m_root.localScale = Vector3.one;
            float w = (Width > 0f ? Width : PlateW) * sc, h = (Height > 0f ? Height : PlateH) * sc, gap = Gap * sc;
            int cols = Columns > 0 ? Columns : Math.Max(1, m_count);
            for (int i = 0; i < m_count; i++)
            {
                Plate p = m_plates[i];
                p.Size = new Vector2(w, h);
                int row, col, inRow;
                Cell(i, cols, out row, out col, out inRow);
                float total = inRow * w + Math.Max(0, inRow - 1) * gap;
                float x = -total * 0.5f + w * 0.5f + col * (w + gap);
                TouchSlider sl = Sliders != null && row < Sliders.Length ? Sliders[row] : null;
                if (sl != null) x = (col == 0 ? -1f : 1f) * (SliderLength * sc * 0.5f + gap + w * 0.5f);   // - and + at the ends
                p.RowY = -row * (h + gap);
                if (sl != null && col == 0) { sl.Length = SliderLength; sl.Height = h / sc; sl.Layout(new Vector3(0f, p.RowY, 0f), sc); }
                p.T.localPosition = new Vector3(x, p.RowY, p.Press);
                p.T.localRotation = Quaternion.identity;
                p.Back.localScale = new Vector3(w, h, 1f);
                SetFrame(p, w, h, 0.003f * sc);
            }
            // the text card above the plates, leaning back a little toward the eyes
            if (m_textRoot != null)
            {
                m_textRoot.localPosition = new Vector3(0f, h * 0.5f + 0.02f * sc + m_textHeight * sc * 0.5f, 0.01f * sc);
                m_textRoot.localRotation = Quaternion.Euler(-12f, 0f, 0f);
                m_textRoot.localScale = Vector3.one * sc;
            }
        }

        /// Row, column and the row's length of button i (Rows when given, else Columns per row).
        void Cell(int i, int cols, out int row, out int col, out int inRow)
        {
            if (Rows != null && Rows.Length > 0)
            {
                int start = 0;
                for (int r = 0; r < Rows.Length; r++)
                {
                    int n = Math.Max(1, Rows[r]);
                    if (i < start + n || r == Rows.Length - 1)
                    {
                        row = r;
                        col = i - start;
                        inRow = r == Rows.Length - 1 ? m_count - start : Math.Min(n, m_count - start);
                        return;
                    }
                    start += n;
                }
            }
            row = i / cols;
            col = i % cols;
            inRow = Math.Min(cols, m_count - row * cols);
        }

        static readonly Color BackIdle = new Color(0.035f, 0.028f, 0.02f, 0.92f), BackLit = new Color(0.36f, 0.24f, 0.08f, 0.96f);

        static void SetFrame(Plate p, float w, float h, float bw)
        {
            float ox = w * 0.5f + bw, oy = h * 0.5f + bw, ix = w * 0.5f, iy = h * 0.5f;
            p.FrameMesh.vertices = new[]
            {
                new Vector3(-ox, -oy, -0.0004f), new Vector3(ox, -oy, -0.0004f), new Vector3(ox, oy, -0.0004f), new Vector3(-ox, oy, -0.0004f),
                new Vector3(-ix, -iy, -0.0004f), new Vector3(ix, -iy, -0.0004f), new Vector3(ix, iy, -0.0004f), new Vector3(-ix, iy, -0.0004f),
            };
            if (p.FrameMesh.triangles.Length == 0)
            {
                p.FrameMesh.triangles = new[] { 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7 };
                p.Shown = Color.clear;
            }
            p.FrameMesh.RecalculateBounds();
        }

        /// Once per frame (the owner's LateUpdate): the plates spring back, move with their travel, glow; both hands press.
        public void Tick()
        {
            if (!Visible || VRRig.I == null) return;
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 1e-3f, 0.1f);
            PlateTouch.Settle(At, m_count, dt);
            for (int i = 0; i < m_count; i++) m_plates[i].T.localPosition = new Vector3(m_plates[i].T.localPosition.x, m_plates[i].RowY, m_plates[i].Press);
            // the sliders first: a fingertip dragging one presses no plate meanwhile
            bool dragL = false, dragR = false;
            if (Sliders != null)
                foreach (TouchSlider sl in Sliders)
                    if (sl != null && sl.Tick()) { if (sl.DraggedBy(true)) dragL = true; else dragR = true; }
            foreach (Hand h in VRInput.RL)
            {
                bool ok = XRSettings.enabled && (Touch.Overridden(h) || (h.Tracked && !h.TriggerBtn && (!h.GripBtn || VRPad.PointingGrip(h)) &&
                                                                         (!VRFade.IsBlack || InSpace && Limbo.On && Limbo.Cover < 0.5f)));
                if (h.IsLeft ? dragL : dragR) ok = false;
                Touch.Step(h, At, m_count, ok);
            }
            for (int i = 0; i < m_count; i++)
            {
                Plate p = m_plates[i];
                Color idle = new Color(0.62f, 0.48f, 0.22f, 0.85f), hot = new Color(1f, 0.82f, 0.4f, 1f);
                Color c = p.Disabled ? new Color(0.3f, 0.28f, 0.25f, 0.6f) : p.Hovered || p.Selected ? hot : Color.Lerp(idle, hot, Mathf.Round(p.Glow * 0.7f * 20f) / 20f);
                Color b = p.Selected ? BackLit : BackIdle;
                if (b != p.BackShown && p.BackMesh != null)
                {
                    p.BackShown = b;
                    Color bl = b.linear;
                    bl.a = b.a;
                    p.BackMesh.colors = new[] { bl, bl, bl, bl };
                    p.Shown = Color.clear;   // the label's colour too
                }
                if (c == p.Shown) continue;
                p.Shown = c;
                Color lin = c.linear;
                lin.a = c.a;
                p.FrameMesh.colors = new[] { lin, lin, lin, lin, lin, lin, lin, lin };
                p.Label.color = p.Disabled ? new Color(0.6f, 0.58f, 0.55f, 1f) : p.Selected ? GameText.GoldTop : GameText.Parchment;
            }
        }

        /// The point `cm` in front of plate i's face and the xrsim move that pushes a fingertip from there 1.5 cm through it.
        public string Pose(int i, float cm, Hand h)
        {
            if (i < 0 || i >= m_count || VRRig.I == null) return "none";
            Plate p = m_plates[i];
            float s = VRRig.I.Scale;
            Vector3 face = p.RestFace;
            Vector3 pt = face - p.T.forward * (cm * 0.01f * s);
            Vector3 d = VRRig.I.FromWorldDir(p.T.forward * ((cm + 1.5f) * 0.01f * s)) / s;
            return string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####} {2:0.####} | move {3} {4:0.####} {5:0.####} {6:0.####}",
                pt.x, pt.y, pt.z, h.IsLeft ? "l" : "r", d.x, d.y, -d.z);
        }

        /// Plate i's face centre (world, for `handto`), its normal toward the player and right axis (xrsim's frame, for
        /// `move`), its size (real metres) and the room scale, for tests.
        public string Geometry(int i)
        {
            if (i < 0 || i >= m_count || VRRig.I == null) return "none";
            Plate p = m_plates[i];
            VRRig r = VRRig.I;
            Vector3 c = p.RestFace, n = r.FromWorldDir(-p.T.forward), u = r.FromWorldDir(p.T.right);
            return string.Format(CultureInfo.InvariantCulture, "centre {0:0.####} {1:0.####} {2:0.####} normal {3:0.####} {4:0.####} {5:0.####} right {6:0.####} {7:0.####} {8:0.####} size {9:0.####} {10:0.####} scale {11:0.####}",
                c.x, c.y, c.z, n.x, n.y, -n.z, u.x, u.y, -u.z, p.Size.x / r.Scale, p.Size.y / r.Scale, r.Scale);
        }

        public System.Collections.IEnumerator Drive(MonoBehaviour runner, Hand h, int n, string mode) => Touch.Drive(runner, h, At, n, mode, null);

        public string Describe() => (Visible ? m_count + " buttons" : "hidden") + " | " + Touch.Describe() + (Visible ? " | travel " + Press() : "");

        string Press()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < m_count; i++) sb.Append(i > 0 ? "," : "").Append((m_plates[i].Press / Mathf.Max(1e-4f, VRRig.I != null ? VRRig.I.Scale : 1f) * 1000f).ToString("0.0", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    /// The test board (`tboard ...`, test seams only): a few plain buttons in front of the player, to poke again and again.
    ///   tboard show [n]                  n buttons (default 3) where the player looks
    ///   tboard hide
    ///   tboard pose <i> [cm] [l|r]       the point cm in front of button i and the xrsim move through it (as platepose)
    ///   tboard geo <i>                   button i's face in tracking space (centre, normal, right axis, size)
    ///   tboard tap|hover|slow|swipe <i> [l|r]   a synthetic fingertip through the real state machine
    internal class TestBoard : MonoBehaviour
    {
        public static TestBoard I;
        TouchBoard m_board;
        public static int Presses;
        public static string LastPress = "-";

        void Awake()
        {
            I = this;
            Diag.Commands["tboard"] = Command;
            Diag.StatusExtras["tboard"] = () => Diag.Q(m_board != null ? m_board.Describe() + " | pressed " + Presses + " last " + LastPress : "none");
        }

        void LateUpdate() { if (m_board != null) m_board.Tick(); }

        string Command(string[] a)
        {
            string op = a.Length > 1 ? a[1] : "";
            if (m_board == null)
            {
                m_board = new TouchBoard("tboard");
                m_board.Pressed = (i, h) => { Presses++; LastPress = (i + 1) + (h.IsLeft ? " L" : " R"); };
            }
            int ix = a.Length > 2 ? Math.Max(0, int.Parse(a[2]) - 1) : 0;
            Hand hand = Array.IndexOf(a, "l") > 1 ? VRInput.L : VRInput.R;
            switch (op)
            {
                case "show":
                    {
                        int n = a.Length > 2 ? Mathf.Clamp(int.Parse(a[2]), 1, 4) : 3;
                        var labels = new string[n];
                        for (int i = 0; i < n; i++) labels[i] = "Button " + (i + 1);
                        return m_board.Show("TEST BOARD\n\nPress the buttons with a fingertip.", labels) ? "shown " + n : "not ready (fonts)";
                    }
                case "hide": m_board.Hide(); return "hidden";
                case "pose":
                    {
                        float cm = 6f;
                        if (a.Length > 3) float.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out cm);
                        return m_board.Pose(ix, cm, hand);
                    }
                case "geo": return m_board.Geometry(ix);
                case "tap": case "hover": case "slow": case "swipe":
                    if (ix >= m_board.Count) return "no button " + (ix + 1);
                    StartCoroutine(m_board.Drive(this, hand, ix, op));
                    return op + " button " + (ix + 1);
                default: return m_board.Describe();
            }
        }
    }
}

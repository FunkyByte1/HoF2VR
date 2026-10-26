using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// A world-space copy of one of the game's own flat fight-HUD widget trees (P4-17, playtest 4: the Board's look "really bad
    /// ... Can we just use assets already in the game?"): the health dial (PlayerHealthUI), the weapon's dial (PlayerWeaponUI),
    /// the artefact's (PlayerArtifactUI), the fight timer (UICombatTimer). Made once from the original - its NGUI widgets only
    /// (sprites, textures, labels with the game's atlases and fonts; every script, collider, panel and anchor dropped) - then each
    /// frame every copy takes its original's look: shown or not, colour, sprite, fill, text, picture, size and place. The original
    /// keeps running as in the flat game (FightHud scales it to nothing in VR), so the copy shows exactly what the flat HUD would.
    internal sealed class HudMirror
    {
        public readonly Transform Root;
        public readonly MonoBehaviour Source;
        readonly List<Transform> m_srcNodes = new List<Transform>(), m_dstNodes = new List<Transform>();
        readonly List<UIWidget> m_src = new List<UIWidget>(), m_dst = new List<UIWidget>();
        readonly HashSet<UIWidget> m_kept = new HashSet<UIWidget>();
        public static int Built, Failed;
        static Transform s_holding;

        HudMirror(MonoBehaviour source, Transform root)
        {
            Source = source;
            Root = root;
        }

        /// The copy of `source`'s tree under `parent` (a world panel's transform), or null.
        public static HudMirror Of(MonoBehaviour source, Transform parent)
        {
            if (source == null || parent == null) return null;
            try
            {
                // made under an inactive holder: none of the copy's scripts wakes (the weapon dial's OnEnable would take the
                // game's PlayerWeaponUI.Instance), then stripped to its widgets and moved under the board
                if (s_holding == null)
                {
                    var h = new GameObject("HoF2VR_HudMirrorHolding");
                    h.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(h);
                    s_holding = h.transform;
                }
                GameObject copy = UnityEngine.Object.Instantiate(source.gameObject, s_holding, false);
                copy.name = "HoF2VR_Mirror_" + source.GetType().Name;
                Strip(copy);
                var m = new HudMirror(source, copy.transform);
                Pair(source.transform, copy.transform, m);
                int layer = parent.gameObject.layer;
                foreach (Transform t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
                // the copy's own panels draw right after the board's (their clipping kept)
                UIPanel host = parent.GetComponentInParent<UIPanel>();
                int q = host != null ? host.startingRenderQueue + 12 : 3180, k = 0;
                foreach (UIPanel p in copy.GetComponentsInChildren<UIPanel>(true))
                {
                    p.renderQueue = UIPanel.RenderQueue.StartAt;
                    p.startingRenderQueue = q + 4 * k++;
                }
                copy.transform.SetParent(parent, false);
                copy.transform.localPosition = Vector3.zero;
                copy.transform.localRotation = Quaternion.identity;
                copy.transform.localScale = Vector3.one;
                Built++;
                Plugin.L.LogInfo("HudMirror: the game's " + source.GetType().Name + " copied onto the board (" + m.m_dst.Count + " widgets)");
                return m;
            }
            catch (Exception e)
            {
                Failed++;
                Plugin.L.LogWarning("HudMirror: " + source.GetType().Name + " could not be copied: " + e.Message);
                return null;
            }
        }

        /// Every component but the transforms and the widgets goes (scripts first: some require a widget).
        static void Strip(GameObject go)
        {
            for (int pass = 0; pass < 3; pass++)
                foreach (Component c in go.GetComponentsInChildren<Component>(true))
                {
                    // (nested panels stay: the dials' fills are clipped by them - the heart filling like a liquid)
                    if (c == null || c is Transform || c is UIWidget || c is UIPanel) continue;
                    try { UnityEngine.Object.DestroyImmediate(c); } catch (Exception) { }
                }
            foreach (UIRect w in go.GetComponentsInChildren<UIRect>(true))
            {
                w.leftAnchor.target = w.rightAnchor.target = w.topAnchor.target = w.bottomAnchor.target = null;
                w.updateAnchors = UIRect.AnchorUpdate.OnStart;
            }
        }

        /// The two trees side by side (same structure: the copy was made from the original).
        static void Pair(Transform s, Transform d, HudMirror m)
        {
            m.m_srcNodes.Add(s);
            m.m_dstNodes.Add(d);
            UIWidget ws = s.GetComponent<UIWidget>(), wd = d.GetComponent<UIWidget>();
            if (ws != null && wd != null) { m.m_src.Add(ws); m.m_dst.Add(wd); }
            int n = Mathf.Min(s.childCount, d.childCount);
            for (int i = 0; i < n; i++) Pair(s.GetChild(i), d.GetChild(i), m);
        }

        public bool Alive => Source != null && Root != null;

        /// The copy takes the original's look (the root's place and size are the board's).
        public void Sync()
        {
            for (int i = 1; i < m_srcNodes.Count; i++)
            {
                Transform s = m_srcNodes[i], d = m_dstNodes[i];
                if (s == null || d == null) continue;
                bool on = s.gameObject.activeSelf;
                if (d.gameObject.activeSelf != on) d.gameObject.SetActive(on);
                if (!on) continue;
                if (d.localPosition != s.localPosition) d.localPosition = s.localPosition;
                if (d.localRotation != s.localRotation) d.localRotation = s.localRotation;
                // (a part FightHud hides by scaling it to nothing - the poison ring under the health dial - at its own size)
                Vector3 sc;
                if (!FightHud.OriginalScale(s, out sc)) sc = s.localScale;
                if (d.localScale != sc) d.localScale = sc;
            }
            for (int i = 0; i < m_src.Count; i++)
            {
                UIWidget s = m_src[i], d = m_dst[i];
                if (s == null || d == null || m_kept.Contains(d)) continue;
                if (d.enabled != s.enabled) d.enabled = s.enabled;
                if (!s.enabled) continue;
                if (d.color != s.color) d.color = s.color;
                if (d.width != s.width) d.width = s.width;
                if (d.height != s.height) d.height = s.height;
                UISprite ss = s as UISprite;
                if (ss != null)
                {
                    var sd = (UISprite)d;
                    if (sd.atlas != ss.atlas) sd.atlas = ss.atlas;
                    if (sd.spriteName != ss.spriteName) sd.spriteName = ss.spriteName;
                    if (sd.fillAmount != ss.fillAmount) sd.fillAmount = ss.fillAmount;
                    continue;
                }
                UITexture ts = s as UITexture;
                if (ts != null)
                {
                    var td = (UITexture)d;
                    if (td.mainTexture != ts.mainTexture) td.mainTexture = ts.mainTexture;
                    if (td.uvRect != ts.uvRect) td.uvRect = ts.uvRect;
                    if (td.fillAmount != ts.fillAmount) td.fillAmount = ts.fillAmount;
                    continue;
                }
                UILabel ls = s as UILabel;
                if (ls != null)
                {
                    var ld = (UILabel)d;
                    if (ld.text != ls.text) ld.text = ls.text;
                }
            }
        }

        /// One named widget's bounds in the copy root's space (the dial's frame: its size does not pulse), else all of them.
        public Bounds LocalBounds(string name)
        {
            Matrix4x4 toRoot = Root.worldToLocalMatrix;
            foreach (UIWidget w in m_dst)
            {
                if (w == null || w.name != name) continue;
                var b = new Bounds();
                bool has = false;
                foreach (Vector3 c in w.worldCorners)
                {
                    Vector3 p = toRoot.MultiplyPoint3x4(c);
                    if (!has) { b = new Bounds(p, Vector3.zero); has = true; } else b.Encapsulate(p);
                }
                if (has && b.size.y > 1f) return b;
            }
            return LocalBounds();
        }

        /// One copied widget's bounds in the copy root's space (shown or not), or an empty box.
        public Bounds BoundsOf(UIWidget copy)
        {
            var b = new Bounds();
            if (copy == null || Root == null) return b;
            Matrix4x4 toRoot = Root.worldToLocalMatrix;
            bool has = false;
            foreach (Vector3 c in copy.worldCorners)
            {
                Vector3 p = toRoot.MultiplyPoint3x4(c);
                if (!has) { b = new Bounds(p, Vector3.zero); has = true; } else b.Encapsulate(p);
            }
            return b;
        }

        /// The widgets' bounds in the copy root's space (its own units), for the layout.
        public Bounds LocalBounds()
        {
            var b = new Bounds();
            bool has = false;
            Matrix4x4 toRoot = Root.worldToLocalMatrix;
            foreach (UIWidget w in m_dst)
            {
                if (w == null || !w.enabled || !w.gameObject.activeInHierarchy || w.GetType() == typeof(UIWidget)) continue;
                foreach (Vector3 c in w.worldCorners)
                {
                    Vector3 p = toRoot.MultiplyPoint3x4(c);
                    if (!has) { b = new Bounds(p, Vector3.zero); has = true; } else b.Encapsulate(p);
                }
            }
            return b;
        }

        public void Destroy()
        {
            if (Root != null) UnityEngine.Object.Destroy(Root.gameObject);
        }

        public int Widgets => m_dst.Count;

        /// The copy of one of the original's widgets (to show something else in it), or null.
        public UIWidget CopyOf(UIWidget original)
        {
            if (original == null) return null;
            int i = m_src.IndexOf(original);
            return i >= 0 ? m_dst[i] : null;
        }

        /// The copy's widgets of this name: no longer follow the original (the board shows something else in them, or nothing).
        public int Keep(string name, bool hide)
        {
            int n = 0;
            foreach (UIWidget w in m_dst)
                if (w != null && w.name == name) { m_kept.Add(w); if (hide) w.enabled = false; n++; }
            return n;
        }

        /// The copy's pictures (UITextures: the weapon's, the artefact's) no longer follow the original's colour.
        public void KeepTextures()
        {
            foreach (UIWidget w in m_dst) if (w is UITexture) m_kept.Add(w);
        }

        /// The kept pictures in this colour (their texture still follows the original's).
        public void TintTextures(Color c)
        {
            for (int i = 0; i < m_dst.Count; i++)
            {
                var d = m_dst[i] as UITexture;
                if (d == null || !m_kept.Contains(d)) continue;
                var s = m_src[i] as UITexture;
                if (s != null && d.mainTexture != s.mainTexture) d.mainTexture = s.mainTexture;
                if (d.color != c) d.color = c;
                if (s != null && d.enabled != s.enabled) d.enabled = s.enabled;
            }
        }

        /// The copy of the original's widget at this path under its root (e.g. "Counter/Current"), or null.
        public T Find<T>(string name) where T : UIWidget
        {
            for (int i = 0; i < m_dst.Count; i++) if (m_dst[i] is T && m_dst[i].name == name) return (T)m_dst[i];
            return null;
        }

        /// The widgets of the copy (diagnostics: names, types, sprites).
        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < m_dst.Count; i++)
            {
                UIWidget w = m_dst[i];
                if (w == null) continue;
                sb.Append(w.name).Append('[').Append(w.GetType().Name);
                UISprite sp = w as UISprite;
                if (sp != null) sb.Append(' ').Append(sp.spriteName).Append(' ').Append(sp.fillAmount.ToString("0.00"));
                UILabel lb = w as UILabel;
                if (lb != null) sb.Append(" '").Append(lb.text).Append('\'');
                UITexture tx = w as UITexture;
                if (tx != null) sb.Append(' ').Append(tx.mainTexture != null ? tx.mainTexture.name : "-");
                sb.Append(' ').Append(w.width).Append('x').Append(w.height).Append(w.enabled && w.gameObject.activeInHierarchy ? "" : " off").Append("] ");
            }
            return sb.ToString();
        }
    }
}

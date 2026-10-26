using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The open inventory at the table, its layers apart (the second playtest, P2-4, mark 8: "The tabs are behind the cards,
    /// but the dropdown menu in the tabs are in front of the cards. If you put the cards a bit lower and the tabs/dropdown a
    /// bit higher, I don't think they would collide. Still, you need to make sure the right thing is behind the right thing").
    /// The slot icons ("tabs", 2D: PrimaryMenus/Inventory/SlotIcons) stood just above the cards, and a hovered card's trait list
    /// (the "dropdown", 2D, pinned to the card) hung from their height down over the upper half of that card and the next one.
    ///  * The cards go CardsLower further down (KeepOut places the inventory), the icons TabsUp pixels up, and a trait list
    ///    hangs from just under the icons (its backdrop's top ListGap pixels below them): a list of up to three lines fits
    ///    between the icons and the cards.
    ///  * The table screen with all the 2D parts stands just in front of the nearest card (VRUI.InventoryFront), so whatever
    ///    still overlaps (a long list) is in front of the cards both as drawn and as seen.
    internal class InventoryLayers : MonoBehaviour
    {
        public static bool On = true;                       // test toggle: `sset InventoryLayers On false`
        public static float CardsLower = 0.085f, TabsUp = 200f, ListGap = 4f;   // real metres; UI pixels (of 1080)
        public static string Last = "-";
        /// The layers are apart now (the inventory open at the table).
        public static bool Active;
        const string IconsPath = "UIManager/UI Root (2D)/Camera/MenuManager/PrimaryMenus/Inventory/SlotIcons";
        const string SharedPath = "UIManager/UI Root (2D)/Camera/MenuManager/Shared";

        sealed class Moved { public Transform T; public Vector3 Base, Set; public float Up; }
        readonly List<Moved> m_moved = new List<Moved>();
        Transform m_icons;
        Camera m_ui;
        int m_scan = -1;
        readonly List<HoverCardTraitSummary> m_lists = new List<HoverCardTraitSummary>();

        void Awake() => Diag.StatusExtras["invLayers"] = () => Diag.Q(Last);

        static bool Open()
        {
            UIInventory inv = UIInventory.Instance;
            if (inv == null || !inv.isActiveAndEnabled) return false;
            object st = inv.ActiveState;
            return st != null && !(st is UIInventoryState_Closed);
        }

        void LateUpdate()
        {
            bool want = On && XRSettings.enabled && Settings.KeepOut && Places.AtCaravan && VRRig.I != null && VRRig.I.HasStation && Open();
            Active = want;
            if (!want)
            {
                if (m_moved.Count > 0) { Restore(); Last = "off"; }
                return;
            }
            if (m_scan != SceneScan.Version || m_icons == null || m_ui == null)
            {
                m_scan = SceneScan.Version;
                Restore();
                GameObject ig = GameObject.Find(IconsPath), cg = GameObject.Find("UIManager/UI Root (2D)/Camera"), sg = GameObject.Find(SharedPath);
                m_icons = ig != null ? ig.transform : null;
                m_ui = cg != null ? cg.GetComponent<Camera>() : null;
                m_lists.Clear();
                if (sg != null) sg.GetComponentsInChildren(true, m_lists);
                if (m_icons == null || m_ui == null) { Last = "no slot icons / UI camera"; return; }
            }
            // a UI pixel in the 2D root's world units; the cards' drop in UI pixels (the table screen stands at the cards)
            float worldPerPx = m_ui.orthographicSize * 2f / VRUI.Height;
            float dropPx = VRUI.I != null && VRUI.I.Panel != null && VRUI.InventoryScreen
                ? CardsLower * VRRig.I.Scale / Mathf.Max(1e-4f, VRUI.I.Panel.localScale.y) * VRUI.Height : 0f;
            float listPx = 0f;
            Lift(m_icons, TabsUp * worldPerPx);
            // the slot icons' lower edge (2D world): the trait lists hang from just under it
            float iconsBottom = float.NaN;
            foreach (Transform icon in m_icons)
            {
                Transform sp = icon.Find("Sprite");
                UIWidget w = sp != null && sp.gameObject.activeInHierarchy ? sp.GetComponent<UIWidget>() : null;
                if (w == null) continue;
                float b = w.worldCorners[0].y;
                if (float.IsNaN(iconsBottom) || b < iconsBottom) iconsBottom = b;
            }
            int lists = 0;
            foreach (HoverCardTraitSummary s in m_lists)
            {
                if (s == null || !s.gameObject.activeInHierarchy) continue;
                Transform pivot = s.transform.Find("Pivot");
                if (pivot == null) continue;
                // the list itself (its icons and lines; not the stat badge under the card), its backdrop's top ListGap under the icons
                Transform icons = pivot.Find("Icons"), text = pivot.Find("Text");
                Transform bgT = icons != null ? icons.Find("BG") : null;
                UIWidget bg = bgT != null ? bgT.GetComponent<UIWidget>() : null;
                if (icons == null || bg == null || float.IsNaN(iconsBottom)) continue;
                float lift = LiftOf(icons) + (iconsBottom - ListGap * worldPerPx) - bg.worldCorners[1].y;
                Lift(icons, lift);
                if (text != null) Lift(text, lift);
                lists++;
                listPx = lift / worldPerPx;
            }
            Last = "cards " + (CardsLower * 100f).ToString("0.#") + " cm lower (" + dropPx.ToString("0") + " px), icons " + TabsUp.ToString("0") + " px up, " + lists + " trait lists hung under them" +
                   (lists > 0 ? " (" + listPx.ToString("0") + " px up)" : "");
        }

        /// A transform held `up` world units above where the game keeps it: a place the game gives it (a tween) since the last
        /// frame becomes the new base, so the game's own moves still play, lifted.
        void Lift(Transform t, float up)
        {
            Moved m = null;
            foreach (Moved x in m_moved) if (x.T == t) { m = x; break; }
            if (m == null) { m = new Moved { T = t, Base = t.localPosition }; m.Set = m.Base; m_moved.Add(m); }
            else if (t.localPosition != m.Set) m.Base = t.localPosition;
            m.Up = up;
            m.Set = m.Base + t.parent.InverseTransformVector(Vector3.up * up);
            t.localPosition = m.Set;
        }

        float LiftOf(Transform t)
        {
            foreach (Moved x in m_moved) if (x.T == t) return x.Up;
            return 0f;
        }

        void Restore()
        {
            foreach (Moved m in m_moved) if (m.T != null) m.T.localPosition = m.Base;
            m_moved.Clear();
        }
    }
}

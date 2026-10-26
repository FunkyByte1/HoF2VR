using System.Collections.Generic;
using UnityEngine;

namespace HoF2VR
{
    /// Hands in the waiting space, and the weapon before a fight (user 2026-10-11 evening: "Can I have hands in the
    /// campfire world? When I'm entering combat, can I have my weapon?"). The space is its own little world (layer 7, its
    /// own camera, posed in its own frame), so the game's gloves are not in it: two copies share the gloves' live meshes
    /// (the fingers move as usual) and material, posed from the controllers in the space's frame. On the way into a fight,
    /// once the arena behind the space has given the hero's weapon and shield to the hands (CombatVR), still copies of
    /// them (meshes and materials only, no game scripts) are held in the same way.
    internal static class LimboHands
    {
        static Transform s_root, s_l, s_r;
        static MeshRenderer s_lr, s_rr;
        static int s_heldKeyL = -1, s_heldKeyR = -1;
        static Transform s_heldL, s_heldR;
        public static int WeaponsShown;
        public static string Last = "-";

        /// Called by Limbo before its camera culls (the late head pose is in VRInput). frame: the space's tracking origin,
        /// yaw and lift; w0: the space's root.
        public static void Pose(Vector3 w0, Vector3 origin, float yaw, float lift, bool on)
        {
            if (!on || VRHands.I == null) { Hide(); return; }
            if (s_root == null) Build();
            if (s_root == null) return;
            Quaternion inv = Quaternion.Euler(0f, -yaw, 0f);
            PoseHand(s_l, s_lr, VRInput.L, true, w0, origin, inv, lift);
            PoseHand(s_r, s_rr, VRInput.R, false, w0, origin, inv, lift);
            Weapons();
        }

        static void PoseHand(Transform t, MeshRenderer r, Hand h, bool left, Vector3 w0, Vector3 origin, Quaternion inv, float lift)
        {
            Mesh m = VRHands.GloveMesh(left);
            bool show = h.Tracked && m != null;
            if (r.enabled != show) r.enabled = show;
            if (!show) return;
            MeshFilter mf = t.GetComponent<MeshFilter>();
            if (mf.sharedMesh != m) mf.sharedMesh = m;
            Material[] mats = VRHands.GloveMaterials();
            if (mats != null && r.sharedMaterials.Length != mats.Length) r.sharedMaterials = mats;
            Vector3 d = inv * new Vector3(h.Pos.x - origin.x, 0f, h.Pos.z - origin.z);
            d.y = h.Pos.y + lift;
            t.SetPositionAndRotation(w0 + d, inv * h.Rot);
        }

        static void Build()
        {
            if (VRHands.GloveMaterials() == null) return;
            var go = new GameObject("HoF2VR_LimboHands");
            Object.DontDestroyOnLoad(go);
            s_root = go.transform;
            s_l = Make("HoF2VR_LimboGloveL", out s_lr);
            s_r = Make("HoF2VR_LimboGloveR", out s_rr);
        }

        static Transform Make(string name, out MeshRenderer r)
        {
            var go = new GameObject(name) { layer = Limbo.Layer };
            go.transform.SetParent(s_root, false);
            go.AddComponent<MeshFilter>();
            r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = VRHands.GloveMaterials();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.enabled = false;
            return go.transform;
        }

        static void Hide()
        {
            if (s_lr != null && s_lr.enabled) s_lr.enabled = false;
            if (s_rr != null && s_rr.enabled) s_rr.enabled = false;
            Drop(ref s_heldL, ref s_heldKeyL);
            Drop(ref s_heldR, ref s_heldKeyR);
        }

        static void Drop(ref Transform held, ref int key)
        {
            if (held != null) Object.Destroy(held.gameObject);
            held = null;
            key = -1;
        }

        /// The hero's weapon and shield (CombatVR's hands) copied into the space's hands, once they are there. Since round 5
        /// the arena (and with it the hero) stays asleep until the player's A (FightFlow): before that the hands hold the
        /// copies kept from the last fight, when the equipment is the same (its card ids).
        static void Weapons()
        {
            CombatVR c = CombatVR.I;
            bool fight = CombatVR.Active && c != null && GameWatch.State == "GameState_Level_In";
            bool before = !fight && FightFlow.Held && GameWatch.State == "GameState_Level_In";
            if (before)
            {
                string key = EquipKey();
                bool same = key.Length > 0 && key == s_tplKey && Valid(s_tplL) | Valid(s_tplR);
                if (same && s_heldKeyL != -2 && s_heldKeyR != -2)
                {
                    Drop(ref s_heldL, ref s_heldKeyL);
                    Drop(ref s_heldR, ref s_heldKeyR);
                    if (Valid(s_tplL)) { s_heldL = Clone(s_tplL, s_l); s_heldKeyL = -2; }
                    if (Valid(s_tplR)) { s_heldR = Clone(s_tplR, s_r); s_heldKeyR = -2; }
                    WeaponsShown++;
                    Last = "kept from the last fight (" + key + ")";
                    Plugin.L.LogInfo("limbo: the weapon and shield of the last fight in the waiting space's hands (the arena still asleep): " + key);
                }
                return;
            }
            if (s_heldKeyL == -2) Drop(ref s_heldL, ref s_heldKeyL);
            if (s_heldKeyR == -2) Drop(ref s_heldR, ref s_heldKeyR);
            Sync(fight ? c.HandL : null, s_l, ref s_heldL, ref s_heldKeyL);
            Sync(fight ? c.HandR : null, s_r, ref s_heldR, ref s_heldKeyR);
        }

        /// Each frame (FightFlow): in a fight, still copies of what the hands hold are kept for the next fight's campfire
        /// (once per set of equipment; the waiting space itself is veiled while the arena wakes up, so this can't wait for it).
        public static void KeepTick()
        {
            CombatVR c = CombatVR.I;
            if (!CombatVR.Active || c == null || Time.frameCount % 20 != 0) return;
            string st = GameWatch.State;
            if (st != "GameState_Level_In" && st != "GameState_Level_Play") return;
            string key = EquipKey();
            if (key.Length == 0 || key == s_tplKey) return;
            Transform ml = Model(c.HandL), mr = Model(c.HandR);
            if (ml == null && mr == null) return;
            if (s_tplRoot == null)
            {
                var go = new GameObject("HoF2VR_LimboKept");
                Object.DontDestroyOnLoad(go);
                go.SetActive(false);
                s_tplRoot = go.transform;
            }
            if (s_tplL != null) Object.Destroy(s_tplL.gameObject);
            if (s_tplR != null) Object.Destroy(s_tplR.gameObject);
            s_tplL = ml != null ? Copy(ml, c.HandL, s_tplRoot) : null;
            s_tplR = mr != null ? Copy(mr, c.HandR, s_tplRoot) : null;
            s_tplKey = key;
            Plugin.L.LogInfo("limbo: kept copies of " + (ml != null ? ml.name : "-") + " / " + (mr != null ? mr.name : "-") + " for the next fight's campfire (" + key + ")");
        }

        static Transform Model(Transform mount)
        {
            if (mount == null) return null;
            for (int i = 0; i < mount.childCount; i++)
            {
                Transform ch = mount.GetChild(i);
                if (ch.gameObject.activeSelf && !ch.name.StartsWith("HoF2VR_")) return ch;
            }
            return null;
        }

        // ---- the copies kept for the next fight's campfire ------------------------------------------------------------
        static Transform s_tplRoot, s_tplL, s_tplR;
        static string s_tplKey = "";

        static string EquipKey()
        {
            try
            {
                Player p = Player.Instance;
                if (p == null) return "";
                EquipmentCard w = Inventory.GetEquippedCard(p.InventoryData, Equipment.EquipmentType.Weapon);
                EquipmentCard sh = Inventory.GetEquippedCard(p.InventoryData, Equipment.EquipmentType.Shield);
                return (w != null ? w.CardData.Id : "-") + "+" + (sh != null ? sh.CardData.Id : "-");
            }
            catch (System.Exception) { return ""; }
        }

        static bool Valid(Transform t)
        {
            if (t == null) return false;
            foreach (MeshFilter mf in t.GetComponentsInChildren<MeshFilter>(true)) if (mf.sharedMesh == null) return false;
            return t.childCount > 0;
        }

        static Transform Clone(Transform src, Transform parent)
        {
            Transform t = Object.Instantiate(src.gameObject, parent, false).transform;
            t.name = src.name;
            return t;
        }

        static void Sync(Transform mount, Transform glove, ref Transform held, ref int key)
        {
            Transform model = null;
            if (mount != null)
                for (int i = 0; i < mount.childCount; i++)
                {
                    Transform ch = mount.GetChild(i);
                    if (!ch.gameObject.activeSelf || ch.name.StartsWith("HoF2VR_")) continue;
                    model = ch;
                    break;
                }
            int k = model != null ? model.GetInstanceID() : -1;
            if (k == key) return;
            Drop(ref held, ref key);
            if (model == null) return;
            held = Copy(model, mount, glove);
            key = k;
            WeaponsShown++;
            Last = model.name + (mount.name.EndsWith("L") ? " (left)" : " (right)");
            Plugin.L.LogInfo("limbo: " + model.name + " in the waiting space's " + (glove == s_l ? "left" : "right") + " hand");
        }

        static readonly List<MeshFilter> s_mfs = new List<MeshFilter>();

        /// A still copy of the model's visible meshes, under the space's glove with the model's pose relative to its mount.
        static Transform Copy(Transform model, Transform mount, Transform glove)
        {
            var root = new GameObject("HoF2VR_LimboHeld_" + model.name) { layer = Limbo.Layer };
            root.transform.SetParent(glove, false);
            Matrix4x4 toMount = mount.worldToLocalMatrix;
            s_mfs.Clear();
            model.GetComponentsInChildren(false, s_mfs);
            foreach (MeshFilter mf in s_mfs)
            {
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled || mf.sharedMesh == null) continue;
                var go = new GameObject(mf.name) { layer = Limbo.Layer };
                go.transform.SetParent(root.transform, false);
                Matrix4x4 m = toMount * mf.transform.localToWorldMatrix;
                go.transform.localPosition = m.GetColumn(3);
                go.transform.localRotation = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
                go.transform.localScale = new Vector3(m.GetColumn(0).magnitude, m.GetColumn(1).magnitude, m.GetColumn(2).magnitude);
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = mr.sharedMaterials;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            foreach (SkinnedMeshRenderer sr in model.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (sr == null || !sr.enabled || sr.sharedMesh == null) continue;
                var baked = new Mesh();
                sr.BakeMesh(baked);
                var go = new GameObject(sr.name) { layer = Limbo.Layer };
                go.transform.SetParent(root.transform, false);
                Matrix4x4 m = toMount * Matrix4x4.TRS(sr.transform.position, sr.transform.rotation, Vector3.one);
                go.transform.localPosition = m.GetColumn(3);
                go.transform.localRotation = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
                go.AddComponent<MeshFilter>().sharedMesh = baked;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = sr.sharedMaterials;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            return root.transform;
        }
    }
}

using System;
using System.Globalization;
using UnityEngine;

namespace HoF2VR
{
    /// P4-10 (playtest 4, the user: "it was just weird that I didn't have any prompt to press a button to use it. I would like
    /// to know what specials I have equipped. Some symbol on my hands would be nice."): in a Physical fight each glove shows
    /// what its stick click does, with the game's own HUD pictures (the flat HUD's weapon and artefact dials:
    /// Weapon/Artifact.HudTexturePrefab):
    ///  * the badge: on the back of the weapon glove the weapon's picture (its special = the weapon ability), on the back of the
    ///    shield glove's wrist (its cuff) the artefact's - flat on the glove, lit while the back of the hand faces the eyes (as the health
    ///    gems), dim otherwise, a thin gold ring round it; greyed while it can't be used (charging, cooling down, no charges);
    ///  * the prompt: while it is ready, the stick-click glyph (the game's controller glyph, as on the Board and the game's own
    ///    HUD) beside the hand, turned to the eyes, and the badge lit gold - so the weapon hand's glow says which button.
    /// Only with an ability on that side; nothing outside fights or in Classic mode (the game's own HUD there).
    internal static class HandIcons
    {
        public static float BadgeSize = 0.040f, BadgeLift = 0.0018f, GlyphSize = 0.030f, GlyphOut = 0.075f, CuffLift = 0.0022f;
        /// The artefact's badge on the back of the shield glove's wrist (2026-10-20: the cuff shortened to HL:A's, 3.2 cm long
        /// on its back - the 4 cm badge stood over its end).
        public static float CuffBadgeSize = 0.028f;
        /// The artefact's badge is on the shield glove's wrist now (FightHud's Body bands leave a gap under it).
        public static bool CuffBadgeShown => s_art.Shown;
        public static int Frames, Builds;
        public static string State = "-";

        sealed class Side
        {
            public bool Weapon;
            public GameObject Badge, Prompt;
            public UITexture Pic;
            public UISprite Ring, Back;
            public Material GlyphMat;
            public bool HasGlyph;
            public Texture Tex;
            public string TexName = "-";
            public bool Shown, Ready, PromptShown;
            public float Look;
            public Vector3 At;
        }

        static readonly Side s_weapon = new Side { Weapon = true }, s_art = new Side { Weapon = false };
        const float Px = 0.0005f;   // metres per NGUI unit

        /// Each frame from Hands (every side, fights or not).
        public static void Tick(Transform hand, HandGeometry geo, bool left, int layer, VRRig rig)
        {
            bool weaponSide = left == Settings.LeftHanded;
            Side s = weaponSide ? s_weapon : s_art;
            try { Update(s, hand, geo, left, layer, rig); }
            catch (Exception e) { State = "error " + e.Message; Hide(s); }
        }

        static void Hide(Side s)
        {
            if (s.Badge != null && s.Badge.activeSelf) s.Badge.SetActive(false);
            if (s.Prompt != null && s.Prompt.activeSelf) s.Prompt.SetActive(false);
            s.Shown = s.PromptShown = false;
        }

        static void Update(Side s, Transform hand, HandGeometry geo, bool left, int layer, VRRig rig)
        {
            Texture tex = null;
            UITexture from = null;
            bool want = hand != null && geo != null && rig != null && CombatVR.Active && CombatSettings.Physical && CombatSettings.GloveHud;
            if (want)
            {
                from = HudPicture(s.Weapon);
                tex = from != null ? from.mainTexture : null;
                want = tex != null;
            }
            if (!want) { Hide(s); return; }
            if (s.Badge == null) Build(s, layer);
            if (s.Badge.layer != layer) SetLayer(s, layer);
            if (s.Tex != tex)
            {
                s.Tex = tex;
                s.TexName = tex.name;
                s.Pic.mainTexture = tex;
                s.Pic.uvRect = from.uvRect;
                // the picture's own shape inside the badge's square
                float aspect = from.height > 0 ? from.width / (float)from.height : 1f;
                int size = 52;
                s.Pic.width = aspect >= 1f ? size : Mathf.RoundToInt(size * aspect);
                s.Pic.height = aspect >= 1f ? Mathf.RoundToInt(size / aspect) : size;
                Builds++;
                Plugin.L.LogInfo("HandIcons: the " + (s.Weapon ? "weapon glove shows the weapon's" : "shield glove shows the artefact's") + " HUD picture '" + tex.name + "'");
            }
            // the badge, flat on the glove: the weapon on the back of the hand, the artefact on the back of the wrist (the cuff)
            Vector3 c = s.Weapon ? geo.HealthCenter : geo.CuffBack, n = (s.Weapon ? geo.HealthNormal : geo.CuffBackNormal).normalized;
            if (s.Badge.transform.parent != hand) s.Badge.transform.SetParent(hand, false);
            Vector3 up = s.Weapon ? Vector3.ProjectOnPlane(geo.CuffDistal.sqrMagnitude > 1e-6f ? -geo.CuffDistal : Vector3.up, n) : Vector3.ProjectOnPlane(-geo.CuffDistal, n);
            if (up.sqrMagnitude < 1e-8f) up = Vector3.ProjectOnPlane(Vector3.up, n);
            s.Badge.transform.localPosition = c + n * (s.Weapon ? BadgeLift : CuffLift);
            s.Badge.transform.localRotation = Quaternion.LookRotation(-n, up.normalized);
            s.Badge.transform.localScale = Vector3.one * ((s.Weapon ? BadgeSize : CuffBadgeSize) / 64f);
            Vector3 wc = hand.TransformPoint(c), wn = hand.TransformDirection(n).normalized, toEye = rig.HeadWorldPos - wc;
            float facing = toEye.sqrMagnitude > 1e-8f ? Vector3.Dot(wn, toEye.normalized) : 0f;
            s.Look = Mathf.MoveTowards(s.Look, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.2f, 0.65f, facing)), Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f) * 5f);
            bool ready = Abilities.Ready(s.Weapon);
            float pulse = ready ? 0.8f + 0.2f * Mathf.Sin(Time.unscaledTime * 5f) : 1f;
            float a = Mathf.Lerp(0.35f, 1f, s.Look);
            s.Pic.color = ready ? new Color(1f, 0.93f, 0.7f, a) : new Color(0.62f, 0.6f, 0.57f, a * 0.85f);
            s.Ring.color = ready ? new Color(1f, 0.82f, 0.4f, a * pulse) : new Color(0.55f, 0.45f, 0.28f, a * 0.8f);
            s.Back.color = new Color(0.04f, 0.03f, 0.02f, 0.82f * a);
            if (!s.Badge.activeSelf) s.Badge.SetActive(true);
            s.Shown = true;
            s.Ready = ready;
            s.At = wc;
            // the prompt beside the hand, facing the eyes, while ready
            bool prompt = ready && s.HasGlyph;
            if (prompt)
            {
                Vector3 grip = ControllerGeometry.GripPos(left);
                Vector3 eye = rig.HeadWorldPos, wg = hand.TransformPoint(grip);
                // out to the hand's outer side as the eyes see it, a little up
                Vector3 right = Vector3.Cross(Vector3.up, wg - eye).normalized;
                Vector3 p = wg + (left ? -right : right) * (GlyphOut * rig.Scale) + Vector3.up * (0.03f * rig.Scale);
                s.Prompt.transform.position = p;
                if ((p - eye).sqrMagnitude > 1e-6f) s.Prompt.transform.rotation = Quaternion.LookRotation(p - eye, Vector3.up);
                s.Prompt.transform.localScale = Vector3.one * (GlyphSize * rig.Scale);
                s.GlyphMat.color = new Color(1f, 0.95f, 0.8f, 0.95f * pulse);
                if (!s.Prompt.activeSelf) s.Prompt.SetActive(true);
            }
            else if (s.Prompt != null && s.Prompt.activeSelf) s.Prompt.SetActive(false);
            s.PromptShown = prompt;
            Frames++;
        }

        /// The flat HUD's picture of the weapon (only when it has an ability: the special) or of the artefact (looked up again only
        /// when the equipped card changes).
        static object s_wKey, s_aKey;
        static UITexture s_wPic, s_aPic;

        static UITexture HudPicture(bool weapon)
        {
            Player p = Player.Instance;
            PlayerController pc = PlayerController.Instance;
            if (p == null || pc == null || p.InventoryData == null || pc.Inventory == null) return null;
            if (weapon)
            {
                if (pc.Inventory.Weapon == null || pc.Inventory.Weapon.Ability == null || p.InventoryData.Weapon == null || p.InventoryData.Weapon.Prefab == null) return null;
                object key = p.InventoryData.Weapon.Card;
                if (!ReferenceEquals(key, s_wKey) || s_wPic == null)
                {
                    s_wKey = key;
                    Weapon w = p.InventoryData.Weapon.Load();
                    s_wPic = w != null ? w.HudTexturePrefab : null;
                }
                return s_wPic;
            }
            if (pc.Inventory.Artifact == null || pc.Inventory.Artifact.Ability == null || p.InventoryData.Artifact == null || p.InventoryData.Artifact.Prefab == null) return null;
            object akey = p.InventoryData.Artifact.Card;
            if (!ReferenceEquals(akey, s_aKey) || s_aPic == null)
            {
                s_aKey = akey;
                Artifact ar = p.InventoryData.Artifact.Load();
                s_aPic = ar != null ? ar.HudTexturePrefab : null;
            }
            return s_aPic;
        }

        static void Build(Side s, int layer)
        {
            s.Badge = new GameObject(s.Weapon ? "HoF2VR_WeaponBadge" : "HoF2VR_ArtefactBadge") { layer = layer };
            UnityEngine.Object.DontDestroyOnLoad(s.Badge);
            GameText.Panel(s.Badge, 3190);
            UIAtlas at = GameText.Atlas;
            // a dark disc, the picture, a thin gold ring (the game's own frame sprites where it has them)
            s.Back = GameText.Sprite(s.Badge.transform, "back", at, "gui_black", new Color(0.04f, 0.03f, 0.02f, 0.82f), 1, 64, 64, UIBasicSprite.Type.Simple);
            var pg = new GameObject("picture") { layer = layer };
            pg.transform.SetParent(s.Badge.transform, false);
            s.Pic = pg.AddComponent<UITexture>();
            s.Pic.depth = 3;
            s.Pic.width = s.Pic.height = 52;
            s.Ring = GameText.Sprite(s.Badge.transform, "ring", at, "gui_border", new Color(0.86f, 0.67f, 0.36f, 1f), 5, 66, 66);
            // the prompt: the game's stick-click glyph on a quad drawn over everything (the held weapon and the shield hid it)
            s.Prompt = new GameObject(s.Weapon ? "HoF2VR_WeaponPrompt" : "HoF2VR_ArtefactPrompt") { layer = layer };
            UnityEngine.Object.DontDestroyOnLoad(s.Prompt);
            try
            {
                UIAtlas atlas = QuestPrompts.PlainAtlas();
                bool right = s.Weapon ? !Settings.LeftHanded : Settings.LeftHanded;
                string sn = QuestPrompts.SpriteName(UIManager.Instance, right ? InControl.InputControlType.RightStickButton : InControl.InputControlType.LeftStickButton);
                UISpriteData sd = atlas != null && !string.IsNullOrEmpty(sn) ? atlas.GetSprite(sn) : null;
                Texture tex = atlas != null ? atlas.texture : null;
                Shader sh = Shader.Find("UI/Default");
                if (sd != null && tex != null && sh != null)
                {
                    float tw = tex.width, th = tex.height;
                    float u0 = sd.x / tw, u1 = (sd.x + sd.width) / tw, v1 = 1f - sd.y / th, v0 = 1f - (sd.y + sd.height) / th;
                    var mesh = new Mesh { name = "HoF2VR_PromptQuad" };
                    float hx = 0.5f, hy = 0.5f * sd.height / Mathf.Max(1f, sd.width);
                    mesh.vertices = new[] { new Vector3(-hx, -hy, 0f), new Vector3(hx, hy, 0f), new Vector3(hx, -hy, 0f), new Vector3(-hx, hy, 0f) };
                    mesh.uv = new[] { new Vector2(u0, v0), new Vector2(u1, v1), new Vector2(u1, v0), new Vector2(u0, v1) };
                    mesh.triangles = new[] { 0, 1, 2, 1, 0, 3 };
                    mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
                    s.GlyphMat = new Material(sh) { name = "HoF2VR_PromptGlyph", mainTexture = tex, renderQueue = 3195 };
                    s.GlyphMat.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
                    s.Prompt.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = s.Prompt.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = s.GlyphMat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    s.HasGlyph = true;
                    State = "glyph " + sn;
                }
                else State = "no glyph (" + (sn ?? "-") + ")";
            }
            catch (Exception e) { State = "glyph error " + e.Message; }
            s.Badge.SetActive(false);
            s.Prompt.SetActive(false);
        }

        static void SetLayer(Side s, int layer)
        {
            foreach (Transform t in s.Badge.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            foreach (Transform t in s.Prompt.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }

        static string One(string name, Side s) => name + " " + (s.Shown ? "shown '" + s.TexName + "' look " + s.Look.ToString("0.00", CultureInfo.InvariantCulture) + (s.Ready ? " READY" : "") +
                                                                 (s.PromptShown ? " prompt" : "") + " at " + Diag.V(s.At) : "hidden");

        /// `handicons`: both badges (what they show, how lit, ready, the prompt) and the tuning.
        public static string Describe() => One("weapon", s_weapon) + " | " + One("artefact", s_art) + " | frames " + Frames + " builds " + Builds + " | " + State;
    }
}

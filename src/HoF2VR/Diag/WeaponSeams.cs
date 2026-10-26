using System;
using System.Linq;
using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// Test seams for the weapon smoke test (coordinator 2026-10-11: "I've only tested the sword and shield so far"):
    ///  `weapons`        every weapon and shield card: id, handedness, category, defence (to the log)
    ///  `equip <Id>`     on the dungeon map: the game's own PlayerEquipment.SwapEquip with a fresh card of that id (the old
    ///                   one goes to the reserve); the next fight uses it. Test profile only.
    ///  `held`           what CombatVR holds in which hand now, and the game's equipment (to the log)
    internal static class WeaponSeams
    {
        public static void Register()
        {
            Diag.Commands["weapons"] = a =>
            {
                var sb = new StringBuilder();
                int n = 0;
                try
                {
                    foreach (EquipmentCard ec in AssetManager.Instance.Cards.Assets.OfType<EquipmentCard>())
                    {
                        WeaponRef w = ec.Equipment as WeaponRef;
                        ShieldRef s = ec.Equipment as ShieldRef;
                        if (w != null) { sb.Append("\n  W ").Append(ec.CardData.Id).Append(' ').Append(w.Handedness).Append(' ').Append(w.Category).Append(" '").Append(Card.LocaliseCard(ec, null)).Append("'"); n++; }
                        else if (s != null) { sb.Append("\n  S ").Append(ec.CardData.Id).Append(" def ").Append(s.DefenceValue).Append(" '").Append(Card.LocaliseCard(ec, null)).Append("'"); n++; }
                    }
                }
                catch (Exception e) { return "error: " + e.Message; }
                Plugin.L.LogInfo("weapons and shields:" + sb);
                return n + " cards (see log)";
            };
            // `uiassets [labels <path part>]`: the game's NGUI fonts and atlases (for drawing in the game's own style)
            Diag.Commands["uiassets"] = a =>
            {
                var sb = new StringBuilder();
                if (a.Length > 2 && a[1] == "labels")
                {
                    foreach (UILabel l in Resources.FindObjectsOfTypeAll<UILabel>())
                    {
                        if (l == null || !l.gameObject.scene.IsValid()) continue;
                        string path = VRUI.Path(l.transform);
                        if (path.IndexOf(a[2], StringComparison.OrdinalIgnoreCase) < 0) continue;
                        sb.Append("\n  ").Append(path).Append(" font=").Append(l.bitmapFont != null ? l.bitmapFont.name : "-").Append(" ttf=").Append(l.trueTypeFont != null ? l.trueTypeFont.name : "-")
                          .Append(" size=").Append(l.fontSize).Append(" color=").Append(l.color).Append(" effect=").Append(l.effectStyle).Append(" ").Append(l.effectColor)
                          .Append(" gradient=").Append(l.applyGradient).Append(" ").Append(l.gradientTop).Append("/").Append(l.gradientBottom).Append(" text='").Append((l.text ?? "").Replace("\n", " ").Substring(0, Math.Min(40, (l.text ?? "").Length))).Append("'");
                    }
                    Plugin.L.LogInfo("labels:" + sb);
                    return "logged";
                }
                foreach (UIFont f in Resources.FindObjectsOfTypeAll<UIFont>())
                    sb.Append("\n  font ").Append(f.name).Append(" dynamic=").Append(f.isDynamic).Append(" size=").Append(f.defaultSize).Append(" atlas=").Append(f.atlas != null ? f.atlas.name : "-")
                      .Append(" tex=").Append(f.texture != null ? f.texture.name + " " + f.texture.width + "x" + f.texture.height : "-")
                      .Append(" -> ").Append(f.replacement != null ? f.replacement.name : "-")
                      .Append(" mat=").Append(f.material != null ? f.material.name + " (" + (f.material.shader != null ? f.material.shader.name : "-") + ")" : "-");
                foreach (UIAtlas at in Resources.FindObjectsOfTypeAll<UIAtlas>())
                    sb.Append("\n  atlas ").Append(at.name).Append(" sprites=").Append(at.spriteList != null ? at.spriteList.Count : 0).Append(" tex=").Append(at.texture != null ? at.texture.name + " " + at.texture.width + "x" + at.texture.height : "-");
                Plugin.L.LogInfo("ui assets:" + sb);
                return "logged";
            };
            // `atlassprites <atlas name part> [name filter]`: sprite names and sizes of an atlas
            Diag.Commands["atlassprites"] = a =>
            {
                var sb = new StringBuilder();
                foreach (UIAtlas at in Resources.FindObjectsOfTypeAll<UIAtlas>())
                {
                    if (a.Length < 2 || at.name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) < 0 || at.spriteList == null) continue;
                    sb.Append("\n atlas ").Append(at.name);
                    foreach (UISpriteData sd in at.spriteList)
                        if (a.Length < 3 || sd.name.IndexOf(a[2], StringComparison.OrdinalIgnoreCase) >= 0)
                            sb.Append("\n  ").Append(sd.name).Append(' ').Append(sd.width).Append('x').Append(sd.height).Append(sd.hasBorder ? " border " + sd.borderLeft + "," + sd.borderBottom + "," + sd.borderRight + "," + sd.borderTop : "");
                }
                Plugin.L.LogInfo("atlas sprites:" + sb);
                return "logged";
            };
            // `atlasdump <atlas> <sprite> [sprite ...]`: crops of atlas sprites to testdata/shots/sprite_<name>.png (style work)
            Diag.Commands["atlasdump"] = a =>
            {
                if (a.Length < 3) return "usage: atlasdump <atlas> <sprite...>";
                UIAtlas at = Resources.FindObjectsOfTypeAll<UIAtlas>().FirstOrDefault(x => x.name == a[1]);
                if (at == null || at.texture == null) return "no atlas " + a[1];
                Texture tex = at.texture;
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(tex, rt);
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                int n = 0;
                string dir = System.IO.Path.Combine(TestMode.Dir, "shots");
                for (int i = 2; i < a.Length; i++)
                {
                    UISpriteData sd = at.GetSprite(a[i]);
                    if (sd == null) continue;
                    var t2 = new Texture2D(sd.width, sd.height, TextureFormat.RGBA32, false);
                    t2.ReadPixels(new Rect(sd.x, tex.height - sd.y - sd.height, sd.width, sd.height), 0, 0, false);
                    t2.Apply();
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "sprite_" + a[i] + ".png"), t2.EncodeToPNG());
                    UnityEngine.Object.Destroy(t2);
                    n++;
                }
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                return n + " sprites saved";
            };
            // `held [name part]`: renderers of the held equipment (name contains "Model(Clone)" or the part): material, shader,
            // whether the shader runs here (a magenta weapon = an unsupported shader)
            Diag.Commands["held"] = a =>
            {
                string part = a.Length > 1 ? a[1] : "Model(Clone)";
                var sb = new StringBuilder();
                int n = 0;
                foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (r == null || !r.enabled) continue;
                    string path = VRUI.Path(r.transform);
                    if (path.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    n++;
                    sb.Append("\n  ").Append(path).Append(" layer ").Append(r.gameObject.layer);
                    foreach (Material m in r.sharedMaterials)
                    {
                        if (m == null) { sb.Append(" | (no material)"); continue; }
                        Shader sh = m.shader;
                        sb.Append(" | ").Append(m.name).Append(" -> ").Append(sh != null ? sh.name : "-").Append(sh != null && !sh.isSupported ? " (NOT SUPPORTED)" : "");
                    }
                }
                try
                {
                    PlayerController pc = PlayerController.Instance;
                    Inventory inv = pc != null ? pc.Inventory : null;
                    if (inv != null && inv.Weapon != null && inv.Weapon.AllModels != null)
                    {
                        sb.Append("\n  game weapon models:");
                        foreach (Model m in inv.Weapon.AllModels) sb.Append(' ').Append(m != null ? m.name + "#" + m.GetInstanceID() : "null");
                    }
                    if (inv != null && inv.Shield != null) sb.Append("\n  game shield model: ").Append(inv.Shield.Model != null ? inv.Shield.Model.name : "null");
                }
                catch (Exception e) { sb.Append("\n  (inventory: " + e.Message + ")"); }
                Plugin.L.LogInfo("held renderers '" + part + "':" + sb);
                return n + " renderers (see log)";
            };
            Diag.Commands["equip"] = a =>
            {
                if (!Scenarios.SeamsAllowed) return "test mode or the dev profile only";
                if (a.Length < 2) return "usage: equip <Id>";
                if (GameWatch.State != "GameState_Map") return "only on the dungeon map (now " + GameWatch.State + ")";
                try
                {
                    var prefab = CardManager.Instance.GetCardById(a[1]) as EquipmentCard;
                    if (prefab == null) return "no equipment card " + a[1];
                    var card = (EquipmentCard)Card.Create(prefab, DeckManager.Instance.OffscreenContainer, true);
                    card.BuildCardTemplate();
                    PlayerEquipment.SwapEquip(card, DeckManager.Instance.InventoryContainer, (c, cc) => Plugin.L.LogInfo("equip seam: equipped " + c.name));
                    return "equipping " + card.name;
                }
                catch (Exception e) { return "error: " + e.GetBaseException().Message; }
            };
        }
    }
}

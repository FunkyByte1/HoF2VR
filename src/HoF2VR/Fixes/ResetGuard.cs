using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HoF2VR
{
    /// The game's "Quit to title" (pause menu) signs out, and the sign-out handler calls Game.ClearScene: it destroys EVERY
    /// GameObject - the mod's own, kept across scenes with DontDestroyOnLoad (the VR rig, hands, boards, the waiting space,
    /// the menus), included - then the GameResetHelper boots the game again from its Loader scene. The mod's objects (root
    /// objects named "HoF2VR...") are kept now; everything else goes exactly as the game wants. The Scenarios' profile
    /// switch (DevProfile) uses the same path.
    ///
    /// The reset also unloads the game's asset bundles, so whatever the mod's objects borrowed from them is gone: the
    /// game's fonts and UI atlas (every board's text and frames), its shaders (the gloves' Standard, the button glyphs'
    /// NGUI shader). Before the reset the mod notes what each of its labels, sprites, atlases and materials used; once the
    /// game has loaded its own again (the title), they get the new ones (`Rebind`).
    internal static class ResetGuard
    {
        public static int Resets, Rebinds;
        public static float LastAt = -100f;
        public static string LastRebind = "-";

        sealed class Notes
        {
            public readonly List<KeyValuePair<UILabel, int>> Labels = new List<KeyValuePair<UILabel, int>>();
            public readonly List<UISprite> Sprites = new List<UISprite>();
            public readonly List<UIAtlas> Atlases = new List<UIAtlas>();
            public readonly List<KeyValuePair<Material, string>> Materials = new List<KeyValuePair<Material, string>>();
        }
        static Notes s_pending;

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Game), "ClearScene"), prefix: new HarmonyMethod(typeof(ResetGuard), nameof(ClearScenePrefix)));
            Diag.StatusExtras["reset"] = () => Diag.Q("resets " + Resets + ", rebinds " + Rebinds + (s_pending != null ? " (pending)" : "") + ", last " + LastRebind);
        }

        static bool ClearScenePrefix()
        {
            Resets++;
            LastAt = Time.unscaledTime;
            var roots = new List<GameObject>();
            int kept = 0, gone = 0;
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(typeof(GameObject));
            for (int i = all.Length - 1; i >= 0; i--)
            {
                var go = all[i] as GameObject;
                if (go == null) continue;
                Transform root = go.transform.root;
                if (root != null && root.name.StartsWith("HoF2VR", StringComparison.Ordinal))
                {
                    kept++;
                    if (root == go.transform) roots.Add(go);
                    continue;
                }
                UnityEngine.Object.Destroy(go);
                gone++;
            }
            // inactive roots too (a closed page): FindObjectsOfType sees only active objects
            try
            {
                foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                    if (go != null && go.transform.parent == null && !go.activeSelf && go.scene.IsValid() && go.name.StartsWith("HoF2VR", StringComparison.Ordinal) && !roots.Contains(go))
                        roots.Add(go);
            }
            catch (Exception e) { Plugin.L.LogWarning("ResetGuard: inactive roots: " + e.Message); }
            try { s_pending = Note(roots); }
            catch (Exception e) { Plugin.L.LogWarning("ResetGuard: notes: " + e.Message); }
            Plugin.L.LogInfo("ResetGuard: the game resets (sign-out / quit to title): " + gone + " objects go, the mod's " + kept + " stay (" +
                             (s_pending != null ? s_pending.Labels.Count + " labels, " + s_pending.Sprites.Count + " sprites, " + s_pending.Atlases.Count + " atlases, " +
                              s_pending.Materials.Count + " materials to rebind" : "nothing noted") + ")");
            SceneManager.LoadScene("empty");
            var helper = new GameObject("GameResetHelper");
            GameResetHelper grh = helper.AddComponent<GameResetHelper>();
            UnityEngine.Object.DontDestroyOnLoad(helper);
            grh.Reset();
            return false;
        }

        /// What the mod's objects use of the game's fonts, UI atlas and shaders (before they go).
        static Notes Note(List<GameObject> roots)
        {
            var n = new Notes();
            UIFont[] fonts = { GameText.Normal, GameText.Stylised, GameText.StylisedItalic, GameText.CardDecal };
            UIAtlas gameAtlas = GameText.Atlas;
            Material gameAtlasMat = gameAtlas != null ? gameAtlas.spriteMaterial : null;
            var seen = new HashSet<Material>();
            foreach (GameObject r in roots)
            {
                foreach (UILabel l in r.GetComponentsInChildren<UILabel>(true))
                {
                    int k = Array.IndexOf(fonts, l.bitmapFont);
                    if (k >= 0 && l.bitmapFont != null) n.Labels.Add(new KeyValuePair<UILabel, int>(l, k));
                }
                foreach (UISprite s in r.GetComponentsInChildren<UISprite>(true))
                    if (gameAtlas != null && s.atlas == gameAtlas) n.Sprites.Add(s);
                foreach (UIAtlas a in r.GetComponentsInChildren<UIAtlas>(true))
                {
                    if (a.spriteMaterial == null) continue;
                    if (gameAtlasMat != null && a.spriteMaterial == gameAtlasMat) n.Atlases.Add(a);
                    else if (seen.Add(a.spriteMaterial) && a.spriteMaterial.shader != null) n.Materials.Add(new KeyValuePair<Material, string>(a.spriteMaterial, a.spriteMaterial.shader.name));
                }
                foreach (Renderer rr in r.GetComponentsInChildren<Renderer>(true))
                    foreach (Material m in rr.sharedMaterials)
                        if (m != null && m.shader != null && seen.Add(m)) n.Materials.Add(new KeyValuePair<Material, string>(m, m.shader.name));
            }
            return n;
        }

        /// Each frame (Diag): once the game's fonts and atlas are back, the noted things get them.
        public static void Tick()
        {
            if (s_pending == null || Time.unscaledTime - LastAt < 1f) return;
            if (!GameText.Ready || !GameText.AtlasReady) return;
            Notes n = s_pending;
            s_pending = null;
            int labels = 0, sprites = 0, atlases = 0, mats = 0, missing = 0;
            UIFont[] fonts = { GameText.Normal, GameText.Stylised, GameText.StylisedItalic, GameText.CardDecal };
            try
            {
                foreach (var kv in n.Labels)
                {
                    if (kv.Key == null || fonts[kv.Value] == null) continue;
                    kv.Key.bitmapFont = fonts[kv.Value];
                    kv.Key.MarkAsChanged();
                    labels++;
                }
                foreach (UISprite s in n.Sprites)
                {
                    if (s == null) continue;
                    s.atlas = GameText.Atlas;
                    s.MarkAsChanged();
                    sprites++;
                }
                foreach (UIAtlas a in n.Atlases)
                {
                    if (a == null) continue;
                    a.spriteMaterial = GameText.Atlas.spriteMaterial;
                    a.MarkAsChanged();
                    atlases++;
                }
                Dictionary<string, Shader> byName = null;
                Shader stopgap = Shader.Find("Sprites/Default");
                var unresolved = new List<KeyValuePair<Material, string>>();
                foreach (var kv in n.Materials)
                {
                    Material m = kv.Key;
                    if (m == null) continue;
                    if (m.shader != null && m.shader.name == kv.Value) continue;
                    Shader sh = Shader.Find(kv.Value);
                    if (sh == null)
                    {
                        if (byName == null)
                        {
                            byName = new Dictionary<string, Shader>();
                            foreach (Shader x in Resources.FindObjectsOfTypeAll<Shader>()) if (x != null && !byName.ContainsKey(x.name)) byName[x.name] = x;
                        }
                        byName.TryGetValue(kv.Value, out sh);
                    }
                    if (sh != null) { m.shader = sh; mats++; }
                    else
                    {
                        // not loaded yet (Standard comes with the room's bundles): a plain stand-in meanwhile, never magenta
                        missing++;
                        if (m.shader == null && stopgap != null) m.shader = stopgap;
                        unresolved.Add(kv);
                    }
                }
                n.Materials.Clear();
                n.Materials.AddRange(unresolved);
            }
            catch (Exception e) { Plugin.L.LogWarning("ResetGuard: rebind: " + e); }
            Rebinds++;
            LastRebind = labels + " labels, " + sprites + " sprites, " + atlases + " atlases, " + mats + " shaders" + (missing > 0 ? ", " + missing + " shaders not found yet" : "");
            Plugin.L.LogInfo("ResetGuard: the game's fonts, atlas and shaders are back: " + LastRebind);
            if (missing > 0)
            {
                // the rest when the game has loaded them (Standard comes with the room's bundles)
                var again = new Notes();
                again.Materials.AddRange(n.Materials);
                s_pending = again;
                LastAt = Time.unscaledTime + 4f;
            }
        }
    }
}

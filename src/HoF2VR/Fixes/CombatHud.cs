using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// In first-person combat the 2D widgets that the game projects from 3D positions onto the flat
    /// screen (floating enemy health bars, button prompts, off-screen arrows, damage numbers) would
    /// land in the wrong places on the VR panel. They are scaled to zero while CombatVR is active, and while the death /
    /// end-of-run text shows after a fight (the killing blow's damage number stayed in a corner of the panel: a red "7"
    /// beside the death text, the virtual playtester's PT_death).
    internal static class CombatHud
    {
        static readonly Dictionary<int, Vector3> s_scale = new Dictionary<int, Vector3>();

        public static void Apply(Harmony h)
        {
            Patch(h, "FloatingHealthBar", "LateUpdate");
            Patch(h, "InputHintsUI", "Update");
            Patch(h, "Indicator", "LateUpdate");
            Patch(h, "FloatingText", "Update");
        }

        static void Patch(Harmony h, string type, string method)
        {
            var m = AccessTools.Method(AccessTools.TypeByName(type), method);
            if (m == null) { Plugin.L.LogWarning("CombatHud: missing " + type + "." + method); return; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(CombatHud), nameof(Hide)));
        }

        // ---- the companion's card in the flat HUD's corner ----------------------------------------------------------------
        // At a fight's start the game deals the companion cards into DeckManager's PlayerOutOfActionContainer, drawn in the flat
        // screen's bottom-left corner ('UI Root (3D)/3D Root/Scalar/BottomLeft/PlayerOutOfActionContainer'). In VR that root
        // hangs on the head: a small card low in the view in every fight, moving with the head (the player's playtest
        // 2026-10-17, marks 10, 11, 15-21). It is scaled to zero while the fight's view is up, and back after.
        static Transform s_ooa;
        static Vector3 s_ooaScale;
        static bool s_ooaHidden;
        public static int OutOfActionHides;

        public static void OutOfActionTick(bool fight)
        {
            try
            {
                if (fight && s_ooa == null)
                {
                    DeckManager dm = DeckManager.Instance;
                    CardContainer c = dm != null ? dm.PlayerOutOfActionContainer : null;
                    if (c != null) s_ooa = c.transform;
                }
                if (s_ooa == null) { s_ooaHidden = false; return; }
                if (fight)
                {
                    if (!s_ooaHidden) { s_ooaScale = s_ooa.localScale; s_ooaHidden = true; OutOfActionHides++; Plugin.L.LogInfo("CombatHud: the companion card corner (PlayerOutOfActionContainer) hidden in the fight"); }
                    if (s_ooa.localScale != Vector3.zero) s_ooa.localScale = Vector3.zero;
                }
                else if (s_ooaHidden)
                {
                    s_ooa.localScale = s_ooaScale;
                    s_ooaHidden = false;
                }
            }
            catch (System.Exception) { s_ooa = null; s_ooaHidden = false; }
        }

        static void Hide(MonoBehaviour __instance)
        {
            Transform t = __instance.transform;
            int id = t.GetInstanceID();
            if (CombatVR.Active || (UnityEngine.XR.XRSettings.enabled && VRUI.EndRunShowing()))
            {
                if (!s_scale.ContainsKey(id)) s_scale[id] = t.localScale;
                if (t.localScale != Vector3.zero) t.localScale = Vector3.zero;
            }
            else
            {
                Vector3 s;
                if (s_scale.TryGetValue(id, out s))
                {
                    t.localScale = s;
                    s_scale.Remove(id);
                }
            }
        }
    }
}

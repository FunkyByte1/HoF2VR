using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// 2D labels that the game pins onto 3D objects (shop prices, card counters, inventory slot
    /// icons, deck-builder tooltips) project the object through the 3D camera onto the flat screen.
    /// In VR the 2D UI is a floating panel, so those calls are redirected to VRUI.WorldToScreenForUi,
    /// which puts the label where the line from the eyes to the object crosses the panel.
    internal static class UiAnchors
    {
        static readonly string[] Types =
        {
            "UIAttachment", "UICardAttachment", "UIShopValue", "WorldToScreenAnchor", "UIInventorySlotIcon",
            "UIAdventureDeckBuilderTooltipSet", "DeckBuilderTooltipSet", "ScreenToWorldAnchor",
        };

        static readonly MethodInfo s_w2s = AccessTools.Method(typeof(Camera), "WorldToScreenPoint", new[] { typeof(Vector3) });

        public static void Apply(Harmony h)
        {
            int n = 0;
            foreach (string name in Types)
            {
                Type t = AccessTools.TypeByName(name);
                if (t == null) { Plugin.L.LogWarning("UiAnchors: no type " + name); continue; }
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t))
                {
                    if (m.IsAbstract || m.GetMethodBody() == null) continue;
                    if (!PatchProcessor.ReadMethodBody(m).Any(p => p.Value is MethodInfo mi && mi == s_w2s)) continue;
                    h.Patch(m, transpiler: new HarmonyMethod(typeof(UiAnchors), nameof(Transpiler)));
                    n++;
                }
            }
            Plugin.L.LogInfo("UiAnchors: " + n + " methods project onto the VR panel");
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            MethodInfo repl = AccessTools.Method(typeof(VRUI), nameof(VRUI.WorldToScreenForUi));
            foreach (CodeInstruction ci in code)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo mi && mi == s_w2s)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = repl;
                }
                yield return ci;
            }
        }
    }
}

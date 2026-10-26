using System;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Tracks the game's state machine (GameState.Enter is public and non-virtual: one patch point).
    internal static class GameWatch
    {
        public static string State = "";
        public static float StateTime;
        public static event Action<string> Changed;

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GameState), "Enter"), postfix: new HarmonyMethod(typeof(GameWatch), nameof(OnEnter)));
            Diag.StatusExtras["state"] = () => Diag.Q(State);
            Diag.StatusExtras["menu"] = () =>
            {
                MenuManager mm = MenuManager.Instance;
                if (mm == null) return Diag.Q("-");
                string m = "";
                foreach (MenuManager.StackPriority p in new[] { MenuManager.StackPriority.Primary, MenuManager.StackPriority.Dialogue })
                {
                    try
                    {
                        Menu cur = mm.CurrentMenu(p);
                        if (cur != null) m += (m.Length > 0 ? " / " : "") + cur.name.Replace("(Clone)", "");
                    }
                    catch (Exception) { }
                }
                return Diag.Q(m.Length > 0 ? m : "-");
            };
            Diag.Commands["state?"] = a => State + " for " + (Time.unscaledTime - StateTime).ToString("0.0") + " s";
        }

        static void OnEnter(GameState __instance)
        {
            State = __instance.GetType().Name;
            StateTime = Time.unscaledTime;
            Plugin.L.LogInfo("game state -> " + State);
            try { Changed?.Invoke(State); }
            catch (Exception e) { Plugin.L.LogError("state handler: " + e); }
        }

        /// Live combat (also used for boss fights): combat buttons are only read in this state.
        public static bool InCombatPlay => State == "GameState_Level_Play";
        /// Any phase where the combat scene / PlayerCamera exists or is being set up / torn down.
        public static bool InCombatScene => State.StartsWith("GameState_Level_") || State.StartsWith("GameState_BossBattle");
    }
}

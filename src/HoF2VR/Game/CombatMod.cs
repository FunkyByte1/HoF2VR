using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// The combat overhaul's entry point (environment 2, branch `combat`; docs/combat-progress.md). One call from
    /// Plugin keeps the combat work out of the shared start-up code: its settings, patches, components and seams.
    internal static class CombatMod
    {
        public static void Init(Harmony h, GameObject root, ConfigFile cfg)
        {
            CombatSettings.Init(cfg);
            WeaponGrips.Register();
            EnemyBars.Apply(h);
            ThreatWarning.Apply(h);
            CombatProbes.Apply(h);
            CombatMove.Apply(h);
            CombatProbes.Register();
            SwingTrace.Register();
            CombatSeams.Apply(h);
            CombatSeams.Register();
            Physical.Apply(h);
            Physical.Register(root);
            Abilities.Apply(h);
            Abilities.Register();
            root.AddComponent<Abilities>();
            ShieldShots.Apply(h);
            ShieldShots.Register();
            root.AddComponent<ShieldShots>();
            root.AddComponent<CombatTick>();
            root.AddComponent<CombatMove>();
            FightHud.Apply(h);
            root.AddComponent<FightHud>();
        }
    }

    /// Per-frame hooks of the combat work that need no component of their own (probes).
    internal class CombatTick : MonoBehaviour
    {
        void LateUpdate()
        {
            WeaponGrips.GripCal.Tick();
            CombatProbes.Tick();
        }
    }
}

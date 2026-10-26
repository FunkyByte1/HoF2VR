using System;
using UnityEngine;

namespace HoF2VR
{
    /// Until You Fall's per-weapon numbers (docs/uyf-reference.md §3.7, the raw table in work/uyf/reports/weapon_stats.tsv;
    /// combat Stage 2 item 3): every HoF2 weapon is mapped to the UYF weapon it plays like, by its handedness and category
    /// (the game's own WeaponRef). Our numbers come from UYF's data, nothing else of UYF's is used.
    ///  * one-handed: sword = arming sword, axe = curved axe, mace = mace, polearm = arming sword;
    ///  * dual wield = daggers;
    ///  * two-handed: axe = heavy axe, mace = heavy hammer, sword = giant greatsword, polearm (staves, spears) = heavy
    ///    longsword.
    /// What they set: the legal swing (mean speed over the envelope), the legal stab, the travel since a swing turned legal
    /// for Standard and Big, the re-hit cooldown per enemy, the guard damage per quality (Stage 2 item 1), the magnetism
    /// distance (item 6). Hit charges are UYF's for every weapon: 3, +1 every 0.25 s once 0.4 s passed without a hit, an
    /// uncharged hit x0.25.
    internal sealed class WeaponSpec
    {
        public string Name;
        public float Health, Guard;                 // UYF's health damage (for the record: health comes from the game) and guard damage
        public float UnderG = 0.3f, BigG = 1.2f;   // guard damage x quality (Standard 1)
        public float UnderH = 0.3f;                // health damage x Under (Standard and Big 1)
        public float MinSwing, Env;                // a legal swing: mean speed over the envelope (m/s, s)
        public float MinStab = 2f, StabEnv;        // a legal stab
        public float StdSwing, BigSwing;           // travel since the swing turned legal (m)
        public float StdStab, BigStab;
        public float ReHit;                        // per enemy (s)
        public float Magnet;                       // magnetism distance (m)
        // UYF's held-weapon feel (SpringConfig, WeaponFeel): the history lag at full hand speed (s), the hand speed it grows
        // over (m/s) and its curve (x^LagPow), how fast it shrinks again (s per s); held in two hands (UYF's _2H config)
        public float Lag, LagFrom, LagTo = 4f, LagPow = 1f, Catchup = 0.3f;
        public bool TwoHand;                       // UYF lets it be held in two hands (its TwoHandedSecondaryGrip)
        public float Lag2H = 0.05f, LagPow2H = 0.2f, Catchup2H = 0.15f;

        public override string ToString()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return Name + ": swing " + MinSwing.ToString("0.0", inv) + " m/s over " + Env.ToString("0.000", inv) + " s, Standard " + StdSwing.ToString("0.000", inv) +
                   " / Big " + BigSwing.ToString("0.00", inv) + " m; stab " + MinStab.ToString("0.0", inv) + " m/s over " + StabEnv.ToString("0.00", inv) + " s, " +
                   StdStab.ToString("0.00", inv) + " / " + BigStab.ToString("0.00", inv) + " m; re-hit " + ReHit.ToString("0.0", inv) + " s; guard " + Guard.ToString("0", inv) +
                   " (x" + UnderG.ToString("0.0", inv) + " / 1 / x" + BigG.ToString("0.0", inv) + "); magnet " + Magnet.ToString("0.00", inv) + " m; lag " +
                   (Lag * 1000f).ToString("0", inv) + " ms" + (TwoHand ? " (two hands " + (Lag2H * 1000f).ToString("0", inv) + ")" : "");
        }
    }

    internal static class WeaponTable
    {
        // UYF's weapons (weapon_stats.tsv), the ones HoF2's weapons map to
        public static readonly WeaponSpec ArmingSword = new WeaponSpec { Name = "arming sword", Health = 6, Guard = 7, MinSwing = 4f, Env = 0.175f, StabEnv = 0.05f, StdSwing = 0.05f, BigSwing = 0.2f, StdStab = 0.02f, BigStab = 0.45f, ReHit = 0.3f, Magnet = 1.0f, Lag = 0.025f, LagFrom = 3f, LagTo = 6f, LagPow = 2f, Catchup = 0.4f };
        public static readonly WeaponSpec CurvedAxe = new WeaponSpec { Name = "curved axe", Health = 7, Guard = 6, MinSwing = 4f, Env = 0.15f, StabEnv = 0.05f, StdSwing = 0.2f, BigSwing = 0.55f, StdStab = 0.25f, BigStab = 0.6f, ReHit = 0.3f, Magnet = 0.55f, Lag = 0.05f, LagTo = 4f, LagPow = 1f, Catchup = 0.3f };
        public static readonly WeaponSpec Mace = new WeaponSpec { Name = "mace", Health = 5, Guard = 10, BigG = 1.4f, MinSwing = 4f, Env = 0.175f, StabEnv = 0.05f, StdSwing = 0.05f, BigSwing = 0.2f, StdStab = 0.25f, BigStab = 0.6f, ReHit = 0.3f, Magnet = 0.65f, Lag = 0.025f, LagTo = 6f, LagPow = 1.5f, Catchup = 0.15f };
        public static readonly WeaponSpec Daggers = new WeaponSpec { Name = "daggers", Health = 5, Guard = 5, MinSwing = 4.5f, Env = 0.1f, StabEnv = 0.05f, StdSwing = 0.075f, BigSwing = 0.35f, StdStab = 0.1f, BigStab = 0.45f, ReHit = 0.2f, Magnet = 0.55f, Lag = 0f };
        public static readonly WeaponSpec HeavyAxe = new WeaponSpec { Name = "heavy axe", Health = 7, Guard = 11, MinSwing = 3f, Env = 0.175f, StabEnv = 0.1f, StdSwing = 0.15f, BigSwing = 0.2f, StdStab = 0.05f, BigStab = 0.2f, ReHit = 0.3f, Magnet = 1.0f, Lag = 0.15f, LagTo = 4f, LagPow = 0.6f, Catchup = 0.3f, TwoHand = true };
        public static readonly WeaponSpec HeavyHammer = new WeaponSpec { Name = "heavy hammer", Health = 7, Guard = 16, MinSwing = 3f, Env = 0.175f, StabEnv = 0.1f, StdSwing = 0.15f, BigSwing = 0.2f, StdStab = 0.05f, BigStab = 0.2f, ReHit = 0.3f, Magnet = 1.0f, Lag = 0.15f, LagTo = 4f, LagPow = 0.6f, Catchup = 0.3f, TwoHand = true };
        public static readonly WeaponSpec Greatsword = new WeaponSpec { Name = "giant greatsword", Health = 8, Guard = 12, UnderG = 0.1f, UnderH = 0.1f, MinSwing = 3f, Env = 0.175f, StabEnv = 0.1f, StdSwing = 0.15f, BigSwing = 0.2f, StdStab = 0.05f, BigStab = 0.2f, ReHit = 0.3f, Magnet = 1.2f, Lag = 0.025f, LagTo = 4f, LagPow = 0.2f, Catchup = 0.15f, TwoHand = true };
        public static readonly WeaponSpec HeavyLongsword = new WeaponSpec { Name = "heavy longsword", Health = 7, Guard = 8, MinSwing = 4f, Env = 0.175f, StabEnv = 0.05f, StdSwing = 0.05f, BigSwing = 0.2f, StdStab = 0.02f, BigStab = 0.45f, ReHit = 0.3f, Magnet = 1.2f, Lag = 0.05f, LagTo = 4f, LagPow = 1f, Catchup = 0.3f };

        public const int Charges = 3;
        public const float ChargeDelay = 0.4f, ChargeEvery = 0.25f, Uncharged = 0.25f;

        static WeaponSpec s_override;
        /// Test seam `vrweapon <name>|game`: grade swings as that UYF weapon whatever is held.
        public static string Override(string name)
        {
            s_override = null;
            if (string.IsNullOrEmpty(name) || name == "game") return "weapon: " + Current();
            foreach (WeaponSpec w in All)
                if (w.Name.Replace(" ", "").StartsWith(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) { s_override = w; break; }
            return s_override != null ? "weapon override: " + s_override : "no UYF weapon '" + name + "'";
        }

        public static readonly WeaponSpec[] All = { ArmingSword, CurvedAxe, Mace, Daggers, HeavyAxe, HeavyHammer, Greatsword, HeavyLongsword };

        static PlayerController s_pc;
        static object s_weapon;
        static WeaponSpec s_cached;

        /// The UYF weapon the hero's weapon plays like now.
        public static WeaponSpec Current()
        {
            if (s_override != null) return s_override;
            PlayerController pc = PlayerController.Instance;
            object w = null;
            try { w = pc != null && pc.Inventory != null ? pc.Inventory.Weapon : null; } catch (Exception) { }
            if (s_cached != null && pc == s_pc && ReferenceEquals(w, s_weapon)) return s_cached;
            s_pc = pc;
            s_weapon = w;
            s_cached = Of(pc);
            return s_cached;
        }

        static WeaponSpec Of(PlayerController pc)
        {
            Handedness hd = Handedness.OneHanded;
            WeaponCategory cat = WeaponCategory.Sword;
            try
            {
                hd = pc.WeaponHandedness;
                Weapon w = pc.Inventory.Weapon;
                if (w != null && w.Ref != null) cat = w.Ref.Category;
            }
            catch (Exception) { }
            if (hd == Handedness.DualWielding) return Daggers;
            if (hd == Handedness.TwoHanded)
            {
                if (cat == WeaponCategory.Axe) return HeavyAxe;
                if (cat == WeaponCategory.Mace) return HeavyHammer;
                if (cat == WeaponCategory.Polearm) return HeavyLongsword;
                return Greatsword;
            }
            if (cat == WeaponCategory.Axe) return CurvedAxe;
            if (cat == WeaponCategory.Mace) return Mace;
            return ArmingSword;
        }

        /// UYF's crit direction leeway: 30 deg, 40 deg while the weapon is held in two hands (WeaponFeel's two-handed grip).
        /// How far a cut may run from a crit's line, either way along it (deg). UYF's 30 (40 two-handed) missed most of the
        /// player's real cuts (2026-10-17: 33, 34, 42, 43, 47 deg off "30 allowed"; "fight mechanics barely if ever work"): 50 / 60.
        /// The lines are 45 deg apart, so only a cut across the line (towards its perpendicular) is the wrong way.
        public static float CritLeeway() => WeaponFeel.TwoHanded ? 60f : 50f;
    }
}

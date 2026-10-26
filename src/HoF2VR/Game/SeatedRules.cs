using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.AI;

namespace HoF2VR
{
    /// Until You Fall's rules for a seated player, on the combat side (`[Comfort] Seated`; the view lift is VRRig's, env 1).
    /// In UYF, seating the player does exactly two things per enemy when the
    /// player's posture changes, and nothing else in its combat reads the posture:
    ///  * the duck: its "Standard DodgeDown Distance" becomes DodgeDown Distance Seated 0.05 m instead of Standing 0.08 m
    ///    (boss strikes keep their literal 0.3 m);
    ///  * its movement capsule takes CollisionRadius Seated instead of Standing: the captains 1.1 -> 1.0 m, the Aether Horror
    ///    1.6 -> 1.45 m, every other archetype the same - so those few come a little closer to a seated player.
    /// Ours, through the enemy table's archetypes (EnemyTableData.Radius): the duck (Guard.ChooseDodge), and the shrink on what
    /// keeps an enemy's body off the player - its NavMeshAgent radius (HoF2's movement body: how close avoidance lets it stand)
    /// x seated / standing, and the slide-in's floor (Guard.Commit: its root never nearer than CommitNear to the eyes) lowered
    /// by standing - seated. Restored when the player stands again.
    internal class SeatedRules : MonoBehaviour
    {
        public const float DuckStanding = 0.08f, DuckSeated = 0.05f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// The player plays seated (the setting).
        public static bool Seated => Settings.Seated;

        /// UYF's duck for a non-boss strike (m).
        public static float Duck => Seated ? DuckSeated : DuckStanding;

        /// UYF's movement radius of this enemy's archetype, standing and seated (m).
        public static void Radii(Controller c, out float standing, out float seated, out string arch)
        {
            standing = seated = 1f;
            arch = "?";
            if (c == null) return;
            string how;
            EnemyTable.Arch a = EnemyTable.Of(c, out how);
            arch = a.Name;
            float[] r = EnemyTableData.Radius[Mathf.Clamp(a.Index, 0, EnemyTableData.Radius.Length - 1)];
            standing = r[0];
            seated = r[1];
        }

        /// How much nearer this enemy may come now than to a standing player (m; 0 standing).
        public static float Shrink(Controller c)
        {
            if (!Seated || c == null) return 0f;
            float st, se;
            string arch;
            Radii(c, out st, out se, out arch);
            return Mathf.Max(0f, st - se);
        }

        /// Its movement body now against a standing player's (1 standing).
        public static float Scale(Controller c)
        {
            if (!Seated || c == null) return 1f;
            float st, se;
            string arch;
            Radii(c, out st, out se, out arch);
            return st > 0.01f ? se / st : 1f;
        }

        // ---- the NavMeshAgent radius: each enemy's own (first seen), x Scale while seated ----
        static readonly Dictionary<int, KeyValuePair<NavMeshAgent, float>> s_orig = new Dictionary<int, KeyValuePair<NavMeshAgent, float>>();
        public static int Shrunk, Restored;
        float m_next;

        void Awake()
        {
            // `seatedrules?`: the posture, the duck, and every enemy's archetype, movement radius and agent radius now / its own
            Diag.Commands["seatedrules?"] = a => Describe();
            // `combatseated on|off` (test mode): [Comfort] Seated in memory (the settings file is not written), then a recenter
            // so the view lift follows - the same as the player switching it
            Diag.Commands["combatseated"] = a =>
            {
                if (!TestMode.Enabled) return "test mode only";
                if (a.Length > 1 && (a[1] == "on" || a[1] == "off")) SetSeated(a[1] == "on");
                return "seated " + Settings.Seated + " eye height " + (VRRig.I != null ? VRRig.I.EyeHeight.ToString("0.00", Inv) : "-") +
                       " (setting " + Settings.EyeHeight.ToString("0.00", Inv) + ") | " + Describe();
            };
        }

        static void SetSeated(bool on)
        {
            ConfigFile cfg = Settings.Cfg;
            ConfigEntry<bool> e;
            if (cfg == null || !cfg.TryGetEntry("Comfort", "Seated", out e)) return;
            bool save = cfg.SaveOnConfigSet;
            try
            {
                cfg.SaveOnConfigSet = false;
                e.Value = on;
            }
            finally { cfg.SaveOnConfigSet = save; }
            if (VRRig.I != null) VRRig.I.Recenter();
            Apply();
        }

        void Update()
        {
            if (Time.unscaledTime < m_next) return;
            m_next = Time.unscaledTime + 0.5f;
            Apply();
        }

        static void Apply()
        {
            try
            {
                PlayerController pc = PlayerController.Instance;
                if (pc != null && pc.Targetable != null)
                {
                    BetterList<Targetable> all = pc.Targetable.EnumerateWithinRadius(80f, pc.Targetable.GetEnemyTeams());
                    for (int i = 0; i < all.size; i++)
                    {
                        Targetable t = all[i];
                        if (t == null || t.Controller == null) continue;
                        NavMeshAgent ag = t.GetComponent<NavMeshAgent>();
                        if (ag == null) continue;
                        int id = ag.GetInstanceID();
                        KeyValuePair<NavMeshAgent, float> o;
                        if (!s_orig.TryGetValue(id, out o))
                        {
                            o = new KeyValuePair<NavMeshAgent, float>(ag, ag.radius);
                            s_orig[id] = o;
                        }
                        float want = o.Value * Scale(t.Controller);
                        if (Mathf.Abs(ag.radius - want) > 1e-4f)
                        {
                            if (want < o.Value - 1e-4f) Shrunk++;
                            else Restored++;
                            ag.radius = want;
                        }
                    }
                }
                // forget agents that are gone (a fight's end, a new fight)
                List<int> gone = null;
                foreach (KeyValuePair<int, KeyValuePair<NavMeshAgent, float>> kv in s_orig)
                    if (kv.Value.Key == null) (gone ?? (gone = new List<int>())).Add(kv.Key);
                if (gone != null) foreach (int id in gone) s_orig.Remove(id);
            }
            catch (Exception e) { Plugin.L.LogWarning("SeatedRules: " + e.Message); }
        }

        static string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("seated ").Append(Seated).Append(" duck ").Append(Duck.ToString("0.000", Inv)).Append(" m commitNear ")
              .Append(Guard.CommitNear.ToString("0.00", Inv)).Append(" shrunk ").Append(Shrunk).Append(" restored ").Append(Restored);
            try
            {
                PlayerController pc = PlayerController.Instance;
                if (pc == null || pc.Targetable == null) return sb.Append(" | no fight").ToString();
                BetterList<Targetable> all = pc.Targetable.EnumerateWithinRadius(80f, pc.Targetable.GetEnemyTeams());
                for (int i = 0; i < all.size; i++)
                {
                    Targetable t = all[i];
                    if (t == null || t.Controller == null) continue;
                    float st, se;
                    string arch;
                    Radii(t.Controller, out st, out se, out arch);
                    NavMeshAgent ag = t.GetComponent<NavMeshAgent>();
                    float orig = ag != null ? ag.radius : 0f;
                    KeyValuePair<NavMeshAgent, float> o;
                    if (ag != null && s_orig.TryGetValue(ag.GetInstanceID(), out o)) orig = o.Value;
                    sb.Append(" | ").Append(WeaponGrips.ModelKey(t.Controller.Model != null ? t.Controller.Model.name : t.name)).Append(" #").Append(t.GetInstanceID())
                      .Append(' ').Append(arch).Append(" uyf ").Append(st.ToString("0.00", Inv)).Append("->").Append(se.ToString("0.00", Inv))
                      .Append(" agent ").Append(ag != null ? ag.radius.ToString("0.000", Inv) : "-").Append(" own ").Append(orig.ToString("0.000", Inv))
                      .Append(" near ").Append((Guard.CommitNear - Shrink(t.Controller)).ToString("0.00", Inv));
                }
            }
            catch (Exception e) { sb.Append(" | ").Append(e.Message); }
            return sb.ToString();
        }
    }
}

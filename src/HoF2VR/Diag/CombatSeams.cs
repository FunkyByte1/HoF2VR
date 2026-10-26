using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Test seams of the combat overhaul (docs/combat-uyf-design.md Appendix B; docs/combat-progress.md). Test mode only
    /// where they change the game.
    ///  * Fixtures: `monsters [filter]` lists every monster card with its AI (log); `fightwith <filter>[*n][,...] | off`
    ///    makes the next fight's enemies exactly these monster cards (CombatTask.PrepareCombat: the drawn monster cards
    ///    are discarded, the asked ones dealt into the enemy row instead). With DevJump (`jump fight <name>`) any enemy
    ///    type can be fought from a map save (scripts/combat_fixture.py --jump ... --with ...).
    internal static class CombatSeams
    {
        static string s_with;           // the pending override ("" = none)
        static bool s_untimed;          // the fixture's fight: a survival timer is stretched to 15 minutes
        static readonly AccessTools.FieldRef<CombatTask, CombatEncounter.Data> s_data = AccessTools.FieldRefAccess<CombatTask, CombatEncounter.Data>("m_combatData");
        public static string LastWith = "-";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(CombatTask), "PrepareCombat"), prefix: new HarmonyMethod(typeof(CombatSeams), nameof(PreparePrefix)),
                        postfix: new HarmonyMethod(typeof(CombatSeams), nameof(PreparePostfix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("CombatSeams: fixture patch failed: " + e.Message); }
        }

        public static void Register()
        {
            Diag.Commands["monsters"] = a =>
            {
                string f = a.Length > 1 ? a[1] : "";
                bool actions = a.Length > 2 && a[2] == "actions";   // `monsters <filter> actions`: also each AI's actions (loads its prefab)
                var sb = new StringBuilder();
                int n = 0;
                foreach (MonsterCard m in Monsters())
                {
                    string ai = "-";
                    AIRef r = null;
                    try { r = m.PrimaryAIRef; ai = r != null ? r.name : "-"; } catch (Exception) { }
                    if (f.Length > 0 && m.name.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 && ai.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    sb.Append("\n  ").Append(m.name).Append(" -> ").Append(ai);
                    if (actions && r != null)
                    {
                        try
                        {
                            AI p = r.ResolveAIPrefab();
                            if (p != null)
                                sb.Append(" [").Append(string.Join(",", p.GetComponentsInChildren<Defiant.Action>(true).Select(x => x.GetType().Name.Replace("Action", "")).Distinct().ToArray())).Append("]");
                        }
                        catch (Exception e) { sb.Append(" [? ").Append(e.Message).Append("]"); }
                    }
                    n++;
                }
                Plugin.L.LogInfo("monster cards (" + f + "): " + n + sb);
                return n + " monster cards (see log)";
            };
            // `abilitysurvey [filter]`: every weapon and artefact card's ability (its kind, targeting, range, radius, use) and its
            // effect tree (proxies by kind: lob, projectile, area...) -> log (combat Phase 2, the abilities)
            Diag.Commands["abilitysurvey"] = a => AbilitySurvey(a.Length > 1 ? a[1] : null);
            // `proxylog on|off|dump`: every combat proxy engaged (thrown items, arrows, areas): kind, source, reflectable
            Diag.Commands["proxylog"] = a =>
            {
                if (a.Length > 1 && a[1] == "on") { s_proxyLog = true; s_proxies.Clear(); return "proxylog on"; }
                if (a.Length > 1 && a[1] == "off") { s_proxyLog = false; return "proxylog off (" + s_proxies.Count + ")"; }
                Plugin.L.LogInfo("proxies (" + s_proxies.Count + "):\n  " + string.Join("\n  ", s_proxies.ToArray()));
                return s_proxies.Count + " proxies (see log)";
            };
            // `fight?`: the fight's timer and objectives
            Diag.Commands["fight?"] = a =>
            {
                CombatEncounter ce = CombatEncounter.Instance;
                if (ce == null) return "no fight";
                var sb = new StringBuilder("duration " + ce.Duration.ToString("0", Inv) + " s, alive " + Physical.CountAlive() + ", objectives:");
                try
                {
                    var data = AccessTools.Field(typeof(CombatEncounter), "m_data").GetValue(ce) as CombatEncounter.Data;
                    if (data != null) { sb.Append(' ').Append(data.ObjectiveRequirement); foreach (CombatObjective o in data.Objectives) sb.Append(' ').Append(o != null ? o.GetType().Name : "null"); }
                }
                catch (Exception e) { sb.Append(" (" + e.Message + ")"); }
                return sb.ToString();
            };
            // `bosses [filter]`: the encounter cards DevJump leaves out (its lists hold "Combat" stings only): bosses and the
            // other special fights, with their sting -> log; `jumpboss <name>` starts one from the map, the way DevJump does
            Diag.Commands["bosses"] = a =>
            {
                string f = a.Length > 1 ? a[1] : "";
                var sb = new StringBuilder();
                int n = 0;
                foreach (EncounterCard e in AssetManager.Instance.Cards.Assets.OfType<EncounterCard>())
                {
                    string st = s_sting != null ? Convert.ToString(s_sting.GetValue(e)) : "";
                    if (st == "Combat" || (f.Length == 0 && st != "Boss")) continue;
                    if (f.Length > 0 && e.name.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 && st.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    sb.Append("\n  ").Append(e.name).Append(" [").Append(st).Append(']');
                    n++;
                }
                Plugin.L.LogInfo("boss and special encounters (" + f + "): " + n + sb);
                return n + " encounters (see log)";
            };
            Diag.Commands["jumpboss"] = a =>
            {
                if (!Scenarios.SeamsAllowed) return "test mode or the dev profile only";
                if (a.Length < 2) return "usage: jumpboss <encounter name>";
                string why;
                if (!DevJump.MapReady(out why)) return why;
                EncounterCard prefab = AssetManager.Instance.Cards.Assets.OfType<EncounterCard>().FirstOrDefault(e => e.name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) >= 0);
                if (prefab == null) return "no encounter like '" + a[1] + "'";
                try
                {
                    Card card = Card.Create(prefab, DeckManager.Instance.OffscreenContainer, true);
                    Card.Activate(card, CardManager.Instance, c => { if (Map.Instance != null) Map.Instance.Focus(); });
                    Plugin.L.LogInfo("CombatSeams: jumpboss " + prefab.name);
                    return "started " + prefab.name;
                }
                catch (Exception e) { return "failed: " + e.Message; }
            };
            // `exits?`: the level's exit triggers (escape and trap levels: "reach the exit"), on or off, where, how far;
            // `exitwalk [dist]`: the hero `dist` m (default 2) before the nearest enabled one, facing it - then the stick walks
            // it in (does the mod's own walking set the trigger off?)
            Diag.Commands["exits?"] = a => Exits(false, 0f);
            Diag.Commands["exitwalk"] = a => !Scenarios.SeamsAllowed ? "test mode or the dev profile only" : Exits(true, a.Length > 1 ? float.Parse(a[1], Inv) : 2f);
            Diag.Commands["fightwith"] = a =>
            {
                if (!Scenarios.SeamsAllowed) return "test mode or the dev profile only";
                if (a.Length < 2) return "fightwith <card filter>[*n][,<filter>[*n]...] | off   (pending: " + (s_with ?? "-") + ", last " + LastWith + ")";
                if (a[1] == "off") { s_with = null; return "fightwith off"; }
                string spec = string.Join(" ", a, 1, a.Length - 1);
                var picked = Resolve(spec, out string why);
                if (picked == null) return why;
                s_with = spec;
                return "next fight: " + string.Join(", ", picked.Select(p => p.Key.name + "*" + p.Value).ToArray());
            };
        }

        static readonly System.Reflection.FieldInfo s_sting = typeof(EncounterCard).GetField("m_sting", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        static bool s_proxyLog;
        static readonly List<string> s_proxies = new List<string>();

        /// CombatProxy.Engage prefix (the `proxylog` seam).
        public static void ProxyEngaged(CombatProxy p, CombatEffectProxy a_effect, CombatTarget a_target, Transform a_origin)
        {
            if (!s_proxyLog || s_proxies.Count >= 300 || p == null) return;
            try
            {
                Targetable src = a_effect != null ? a_effect.Source : null;
                CombatProxyProjectile prj = p as CombatProxyProjectile;
                s_proxies.Add(Time.unscaledTime.ToString("0.00", Inv) + " " + p.GetType().Name + " " + WeaponGrips.ModelKey(p.name) + " from " + (src != null ? WeaponGrips.ModelKey(src.name) : "-") +
                              " at " + Diag.V(p.transform.position) + " -> " + (a_target.Targetable != null ? WeaponGrips.ModelKey(a_target.Targetable.name) : Diag.V(a_target.Position)) +
                              (prj != null ? " reflectable " + prj.CanReflect : "") + " container " + (a_effect != null && a_effect.Container != null ? a_effect.Container.GetType().Name : "-"));
            }
            catch (Exception) { }
        }

        static string Exits(bool place, float dist)
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null) return "no hero";
            ExitTrigger[] all = UnityEngine.Object.FindObjectsOfType<ExitTrigger>();
            var sb = new StringBuilder(all.Length + " exit triggers");
            ExitTrigger best = null;
            Bounds bestB = default(Bounds);
            float bestD = float.MaxValue;
            foreach (ExitTrigger x in all)
            {
                bool on = false;
                Bounds b = new Bounds(x.transform.position, Vector3.zero);
                foreach (Collider c in x.GetComponents<Collider>())
                {
                    if (c == null || !c.enabled) continue;
                    if (on) b.Encapsulate(c.bounds); else b = c.bounds;
                    on = true;
                }
                float d = Vector3.Distance(pc.transform.position, b.center);
                sb.Append(" | ").Append(x.name).Append(on && x.isActiveAndEnabled ? " on" : " off").Append(" at ").Append(Diag.V(b.center)).Append(" size ")
                  .Append(Diag.V(b.size)).Append(' ').Append(d.ToString("0.0", Inv)).Append(" m");
                if (on && x.isActiveAndEnabled && d < bestD) { bestD = d; best = x; bestB = b; }
            }
            if (!place) return sb.ToString();
            if (best == null) return "no enabled exit trigger | " + sb;
            // from the hero's side: the point `dist` m before the trigger's box, on the ground, facing the box's centre
            Vector3 dir = bestB.center - pc.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
            dir.Normalize();
            float tx = Mathf.Abs(dir.x) > 1e-3f ? bestB.extents.x / Mathf.Abs(dir.x) : float.MaxValue;
            float tz = Mathf.Abs(dir.z) > 1e-3f ? bestB.extents.z / Mathf.Abs(dir.z) : float.MaxValue;
            Vector3 p = bestB.center - dir * (Mathf.Min(tx, tz) + dist);
            UnityEngine.AI.NavMeshHit nh;
            if (!UnityEngine.AI.NavMesh.SamplePosition(p, out nh, 2f, UnityEngine.AI.NavMesh.AllAreas)) return "no ground before " + best.name + " | " + sb;
            UnityEngine.AI.NavMeshAgent ag = pc.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (ag != null && ag.enabled) ag.Warp(nh.position); else pc.transform.position = nh.position;
            Vector3 to = bestB.center - nh.position;
            to.y = 0f;
            if (to.sqrMagnitude > 1e-4f) pc.transform.rotation = Quaternion.LookRotation(to);
            if (CombatVR.I != null) CombatVR.I.FaceTowards(bestB.center, false);
            return "hero at " + Diag.V(nh.position) + ", " + to.magnitude.ToString("0.00", Inv) + " m from the centre of " + best.name + " (box " + Diag.V(bestB.size) + ")";
        }

        static string AbilitySurvey(string filter)
        {
            var sb = new StringBuilder();
            int n = 0, with = 0;
            foreach (EquipmentCard ec in AssetManager.Instance.Cards.Assets.OfType<EquipmentCard>())
            {
                bool art = ec.Equipment is ArtifactRef;
                if (!art && !(ec.Equipment is WeaponRef)) continue;
                string id = ec.CardData.Id;
                if (filter != null && id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                n++;
                try
                {
                    Equipment eq = ec.Equipment.ResolveEquipmentPrefab();
                    Ability ab = eq != null ? eq.GetComponent<Ability>() : null;
                    if (ab == null) continue;
                    with++;
                    sb.Append("\n").Append(art ? "A " : "W ").Append(id).Append(' ').Append(Abilities.Describe(ab));
                }
                catch (Exception e) { sb.Append("\n? ").Append(id).Append(' ').Append(e.Message); }
            }
            Plugin.L.LogInfo("ability survey (" + filter + "):" + sb);
            return with + " abilities on " + n + " weapon and artefact cards (see log)";
        }

        static IEnumerable<MonsterCard> Monsters()
        {
            AssetManager am = AssetManager.Instance;
            if (am == null || am.Cards == null) yield break;
            foreach (Card c in am.Cards.Assets)
            {
                MonsterCard m = c as MonsterCard;
                if (m != null) yield return m;
            }
        }

        /// "Thrall*2,GoblinTrapper" -> monster card prefabs and counts (each filter matches a card or its AI's name; the
        /// shortest matching card name wins).
        static List<KeyValuePair<MonsterCard, int>> Resolve(string spec, out string why)
        {
            why = null;
            var list = new List<KeyValuePair<MonsterCard, int>>();
            List<MonsterCard> all = Monsters().ToList();
            foreach (string part in spec.Split(','))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                int n = 1;
                int star = p.LastIndexOf('*');
                if (star > 0) { int.TryParse(p.Substring(star + 1), NumberStyles.Integer, Inv, out n); p = p.Substring(0, star); }
                MonsterCard best = null;
                foreach (MonsterCard m in all)
                {
                    string ai = "";
                    try { AIRef r = m.PrimaryAIRef; ai = r != null ? r.name : ""; } catch (Exception) { }
                    bool hit = m.name.Equals(p, StringComparison.OrdinalIgnoreCase) || m.name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0 ||
                               ai.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (hit && (best == null || m.name.Length < best.name.Length)) best = m;
                }
                if (best == null) { why = "no monster card like '" + p + "' (see `monsters`)"; return null; }
                list.Add(new KeyValuePair<MonsterCard, int>(best, Mathf.Clamp(n, 1, 6)));
            }
            if (list.Count == 0) { why = "nothing asked"; return null; }
            return list;
        }

        /// A fixture's fight that is a timed survival (the ambushes: won after 30 s) lasts 15 minutes instead, so tests can
        /// fight it out.
        static void PreparePostfix(CombatTask __instance)
        {
            if (!s_untimed) return;
            s_untimed = false;
            try
            {
                CombatEncounter.Data d = s_data(__instance);
                if (d != null && d.Duration > 0f) { Plugin.L.LogInfo("fightwith: the fight's timer " + d.Duration.ToString("0") + " s -> 900 s"); d.Duration = 900f; }
            }
            catch (Exception e) { Plugin.L.LogWarning("fightwith: timer: " + e.Message); }
        }

        static void PreparePrefix()
        {
            if (string.IsNullOrEmpty(s_with) || !Scenarios.SeamsAllowed) return;
            string spec = s_with;
            s_with = null;
            try
            {
                var picked = Resolve(spec, out string why);
                if (picked == null) { Plugin.L.LogWarning("fightwith: " + why); return; }
                DeckManager dm = DeckManager.Instance;
                CardContainer row = dm.EnemyContainer;
                int dropped = 0;
                for (int i = row.Cards.Count - 1; i >= 0; i--)
                    if (row.Cards[i] is MonsterCard) { Card.Discard(row.Cards[i]); dropped++; }
                // no companion in a fixture's fight (it would fight the enemies the test is about)
                int friends = 0;
                foreach (Card c in dm.FriendlyContainer.Cards) if (c is SpawnerCard) friends++;
                if (friends > 0) dm.ReturnSpawnerCards(dm.FriendlyContainer);
                int made = 0;
                foreach (var kv in picked)
                    for (int i = 0; i < kv.Value; i++) { Card.Create(kv.Key, row, true); made++; }
                LastWith = spec;
                s_untimed = true;
                Plugin.L.LogInfo("fightwith: the fight's " + dropped + " drawn monster cards replaced by " + made + ": " +
                                 string.Join(", ", picked.Select(p => p.Key.name + "*" + p.Value).ToArray()) + (friends > 0 ? "; the companion stays out" : ""));
            }
            catch (Exception e) { Plugin.L.LogWarning("fightwith: " + e); }
        }
    }
}

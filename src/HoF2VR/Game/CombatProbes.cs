using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Combat Phase 0 probes (docs/combat-uyf-design.md §8.7; results in docs/combat-probes.md). Test seams, no gameplay
    /// change:
    ///  `strikelog on|off|clear|dump`  every enemy strike aimed at the player, from the game's telegraph (the glow) to its
    ///                   end: enemy, attack action, telegraph-to-end time (the impact for melee), reach at both ends, the
    ///                   attacker's angle to the player at the end, whether a counter would have worked (ActionCounter
    ///                   .GetAction during the telegraph), the swing's side from the weapon trail, the player's damage.
    ///                   `dump` writes testdata/probes/strikes.tsv (all) and atlas.tsv (per enemy and attack).
    ///  `fxlog on|off|dump`  every effect event the game fires during the fight (EffectSet.OnTriggered) and what each
    ///                   effect instance contains (Effect.OnEffect: sounds, particle / light / material colours): the
    ///                   telegraph glows' colours and the hit, parry and swing sounds. -> testdata/probes/effects.txt
    ///  `enemysurvey [tag]`  the enemies of this fight: type, health, rig, what they can do (melee, ranged, combos,
    ///                   shields, armour, snares, behind-landing) -> testdata/probes/enemies.tsv (appended)
    internal static class CombatProbes
    {
        sealed class Strike
        {
            public Controller Ctrl;
            public Targetable T;
            public Model Model;
            public string Enemy = "", Action = "", Fight = "";
            public bool Blockable, Ranged;
            public float T0, T1 = -1f, Damage, DamageAt = -1f;
            public int Frames, CounterFrames;
            public float D0, D1, Angle1, Speed = 1f;
            public Vector3 Sweep;
            public float SweepW;
            public int TrailFrames;
            public readonly Dictionary<int, Vector3> Tips = new Dictionary<int, Vector3>();
        }

        static bool s_on, s_fx;
        static readonly List<Strike> s_live = new List<Strike>();
        static readonly List<Strike> s_done = new List<Strike>();
        static readonly Dictionary<string, int> s_events = new Dictionary<string, int>();
        static readonly Dictionary<string, string> s_effects = new Dictionary<string, string>();
        static string Dir => Path.Combine(TestMode.Dir, "probes");

        public static void Apply(Harmony h)
        {
            try
            {
                var start = new HarmonyMethod(typeof(CombatProbes), nameof(StartMelee));
                var startR = new HarmonyMethod(typeof(CombatProbes), nameof(StartRanged));
                var stop = new HarmonyMethod(typeof(CombatProbes), nameof(Stop));
                h.Patch(AccessTools.Method(typeof(CombatUtils), "StartMeleeEffect"), postfix: start);
                h.Patch(AccessTools.Method(typeof(CombatUtils), "StopMeleeEffect"), postfix: stop);
                h.Patch(AccessTools.Method(typeof(CombatUtils), "StartRangedEffect"), postfix: startR);
                h.Patch(AccessTools.Method(typeof(CombatUtils), "StopRangedEffect"), postfix: stop);
                h.Patch(AccessTools.Method(typeof(Destroyable), "ApplyDamage"), prefix: new HarmonyMethod(typeof(CombatProbes), nameof(BeforeDamage)),
                    postfix: new HarmonyMethod(typeof(CombatProbes), nameof(AfterDamage)));
            }
            catch (Exception e) { Plugin.L.LogWarning("CombatProbes: " + e.Message); }
        }

        public static void Register()
        {
            Diag.Commands["strikelog"] = a =>
            {
                string op = a.Length > 1 ? a[1] : "";
                if (op == "on") { s_on = true; return "strike log on (" + s_done.Count + " so far)"; }
                if (op == "off") { s_on = false; return "strike log off"; }
                if (op == "clear") { s_done.Clear(); s_live.Clear(); return "cleared"; }
                if (op == "dump") return Dump();
                if (op == "fight" && a.Length > 2) { s_fight = a[2]; return "fight " + s_fight; }
                return "strikes " + s_done.Count + " done, " + s_live.Count + " live, logging " + s_on;
            };
            Diag.Commands["fxlog"] = a =>
            {
                string op = a.Length > 1 ? a[1] : "";
                if (op == "on") { if (!s_fx) { EffectSet.OnTriggered += OnTriggered; Effect.OnEffect += OnEffect; } s_fx = true; return "fx log on"; }
                if (op == "off") { if (s_fx) { EffectSet.OnTriggered -= OnTriggered; Effect.OnEffect -= OnEffect; } s_fx = false; return "fx log off"; }
                if (op == "dump") return DumpFx();
                return s_events.Count + " event kinds, " + s_effects.Count + " effects, logging " + s_fx;
            };
            Diag.Commands["enemysurvey"] = a => Survey(a.Length > 1 ? a[1] : "");
        }

        static string s_fight = "-";
        static string FightName() => s_fight;

        // ---------------------------------------------------------------------------------------- strikes

        static void StartMelee(Model a_model, bool a_blockable) => Start(a_model, a_blockable, false);
        static void StartRanged(Model a_model, bool a_blockable) => Start(a_model, a_blockable, true);

        static void Start(Model model, bool blockable, bool ranged)
        {
            if (!s_on || model == null || PlayerController.Instance == null) return;
            try
            {
                Controller c = model.GetComponentInParent<Controller>();
                if (c == null) return;
                foreach (Strike old in s_live) if (old.Ctrl == c) { End(old); s_live.Remove(old); break; }
                Targetable t = c.GetComponent<Targetable>();
                Actor actor = c.GetComponent<Actor>();
                var act = actor != null ? actor.GetRunningAction<IActionAICombat>() : null;
                var s = new Strike
                {
                    Ctrl = c, T = t, Model = model, Enemy = WeaponGrips.ModelKey(model.name), Action = act != null ? act.GetType().Name : "?", Blockable = blockable,
                    Ranged = ranged, T0 = Time.time, Fight = FightName()
                };
                s.D0 = Dist(t);
                try { s.Speed = c.CalculateAttackSpeed(); } catch (Exception) { }
                s_live.Add(s);
            }
            catch (Exception e) { Plugin.L.LogWarning("strikelog: " + e.Message); }
        }

        static void Stop(Model a_model)
        {
            if (a_model == null) return;
            for (int i = 0; i < s_live.Count; i++)
                if (s_live[i].Model == a_model) { End(s_live[i]); s_live.RemoveAt(i); return; }
        }

        static float Dist(Targetable t)
        {
            PlayerController pc = PlayerController.Instance;
            if (t == null || pc == null) return -1f;
            Vector3 d = t.AimTransform.position - pc.GetComponent<Targetable>().AimTransform.position;
            d.y = 0f;
            return d.magnitude;
        }

        static void End(Strike s)
        {
            s.T1 = Time.time;
            s.D1 = Dist(s.T);
            PlayerController pc = PlayerController.Instance;
            if (s.Ctrl != null && pc != null)
            {
                Vector3 to = pc.transform.position - s.Ctrl.transform.position;
                to.y = 0f;
                Vector3 f = s.Ctrl.transform.forward;
                f.y = 0f;
                s.Angle1 = to.sqrMagnitude > 1e-4f ? Vector3.Angle(f, to) : 0f;
            }
            s_done.Add(s);
            if (s_done.Count <= 400)
                Plugin.L.LogInfo("strike " + s.Enemy + " " + s.Action + (s.Ranged ? " ranged" : "") + " " + (s.Blockable ? "counterable-glow" : "red-glow") + " " +
                                 (s.T1 - s.T0).ToString("0.00") + " s, reach " + s.D0.ToString("0.0") + "->" + s.D1.ToString("0.0") + " m, angle " + s.Angle1.ToString("0") +
                                 ", counter " + s.CounterFrames + "/" + s.Frames + ", swing " + Side(s) + (s.DamageAt >= 0f ? ", hit the player " + s.Damage.ToString("0") : ""));
        }

        /// The weapon trail's sweep in the attacker's frame.
        static string Side(Strike s)
        {
            if (s.SweepW <= 0f || s.TrailFrames < 2) return "no trail";
            Vector3 m = s.Sweep / s.SweepW;
            float ax = Mathf.Abs(m.x), ay = Mathf.Abs(m.y), az = Mathf.Abs(m.z);
            string dir = ax >= ay && ax >= az ? (m.x < 0f ? "right-to-left" : "left-to-right") : ay >= az ? (m.y < 0f ? "downward" : "upward") : (m.z > 0f ? "thrust" : "pull");
            return dir + " (" + m.x.ToString("0.00") + "," + m.y.ToString("0.00") + "," + m.z.ToString("0.00") + ")";
        }

        /// Each frame (CombatTick): the live strikes' counter window and weapon trails.
        public static void Tick()
        {
            if (!s_on || s_live.Count == 0) return;
            for (int i = s_live.Count - 1; i >= 0; i--)
            {
                Strike s = s_live[i];
                if (s.Ctrl == null || Time.time - s.T0 > 10f) { s_live.RemoveAt(i); continue; }
                s.Frames++;
                try { if (s.T != null && ActionCounter.GetAction(s.T) != null) s.CounterFrames++; } catch (Exception) { }
                if (s.Model == null) continue;
                Transform root = s.Ctrl.transform;
                foreach (TrailWorldProxy p in s.Model.GetComponentsInChildren<TrailWorldProxy>())
                {
                    if (p == null || !p.IsActive) continue;
                    Vector3 tip = p.transform.TransformPoint(p.pointB);
                    int id = p.GetInstanceID();
                    Vector3 prev;
                    if (s.Tips.TryGetValue(id, out prev))
                    {
                        Vector3 v = tip - prev;
                        Vector3 l = root.InverseTransformDirection(v);
                        s.Sweep += l;
                        s.SweepW += v.magnitude;
                        s.TrailFrames++;
                    }
                    s.Tips[id] = tip;
                }
            }
        }

        static void BeforeDamage(Destroyable __instance, out float __state) { __state = __instance != null ? __instance.CurrentHealth : 0f; }

        static void AfterDamage(Destroyable __instance, Destroyable.Damage a_damage, float __state)
        {
            if (!s_on || __instance == null || PlayerController.Instance == null || __instance.gameObject != PlayerController.Instance.gameObject) return;
            try
            {
                Targetable src = a_damage.Container != null ? a_damage.Container.Source : null;
                if (src == null) return;
                foreach (Strike s in s_live)
                    if (s.T == src && s.DamageAt < 0f) { s.DamageAt = Time.time; s.Damage = Mathf.Max(a_damage.Amount, __state - __instance.CurrentHealth); }
            }
            catch (Exception) { }
        }

        static string Dump()
        {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder("fight\tenemy\taction\tranged\tglow\tseconds\treach0\treach1\tangle\tcounter_frac\tswing\tdamage\tattack_speed\n");
            foreach (Strike s in s_done)
                sb.Append(s.Fight).Append('\t').Append(s.Enemy).Append('\t').Append(s.Action).Append('\t').Append(s.Ranged).Append('\t').Append(s.Blockable ? "counterable" : "red").Append('\t')
                  .Append((s.T1 - s.T0).ToString("0.000")).Append('\t').Append(s.D0.ToString("0.00")).Append('\t').Append(s.D1.ToString("0.00")).Append('\t').Append(s.Angle1.ToString("0"))
                  .Append('\t').Append(s.Frames > 0 ? (s.CounterFrames / (float)s.Frames).ToString("0.00") : "-").Append('\t').Append(Side(s)).Append('\t')
                  .Append(s.DamageAt >= 0f ? s.Damage.ToString("0") : "-").Append('\t').Append(s.Speed.ToString("0.00")).Append('\n');
            File.WriteAllText(Path.Combine(Dir, "strikes.tsv"), sb.ToString());
            // the atlas: per enemy and attack
            var atlas = new StringBuilder("enemy\taction\tglow\tn\tseconds_median\tseconds_min\tseconds_max\treach_end_median\tcounterable_share\tswing_mostly\thit_share\n");
            foreach (var g in s_done.GroupBy(s => s.Enemy + "\t" + s.Action + "\t" + (s.Blockable ? "counterable" : "red")).OrderBy(g => g.Key))
            {
                var secs = g.Select(s => s.T1 - s.T0).OrderBy(x => x).ToList();
                var reach = g.Select(s => s.D1).OrderBy(x => x).ToList();
                int counter = g.Count(s => s.CounterFrames > 0);
                string swing = g.Select(s => Side(s).Split(' ')[0]).GroupBy(x => x).OrderByDescending(x => x.Count()).First().Key;
                atlas.Append(g.Key).Append('\t').Append(g.Count()).Append('\t').Append(secs[secs.Count / 2].ToString("0.00")).Append('\t').Append(secs[0].ToString("0.00")).Append('\t')
                     .Append(secs[secs.Count - 1].ToString("0.00")).Append('\t').Append(reach[reach.Count / 2].ToString("0.0")).Append('\t').Append((counter / (float)g.Count()).ToString("0.00"))
                     .Append('\t').Append(swing).Append('\t').Append((g.Count(s => s.DamageAt >= 0f) / (float)g.Count()).ToString("0.00")).Append('\n');
            }
            File.WriteAllText(Path.Combine(Dir, "atlas.tsv"), atlas.ToString());
            return s_done.Count + " strikes -> " + Dir + "/strikes.tsv, atlas.tsv";
        }

        // ---------------------------------------------------------------------------------------- effects

        static void OnTriggered(EffectSet set, string ev, Model model, bool found)
        {
            if (!found) return;
            string key = ev + " | " + (set != null ? WeaponGrips.ModelKey(set.name) : "-") + " | " + (model != null ? WeaponGrips.ModelKey(model.name) : "-");
            int n;
            s_events.TryGetValue(key, out n);
            s_events[key] = n + 1;
        }

        static void OnEffect(Effect effect, Model model, EffectController instance)
        {
            if (effect == null || instance == null) return;
            string key = effect.StartEvent + " | " + WeaponGrips.ModelKey(instance.name);
            if (s_effects.ContainsKey(key) || s_effects.Count > 600) return;
            try { s_effects[key] = Describe(instance.gameObject); }
            catch (Exception e) { s_effects[key] = "error " + e.Message; }
        }

        static string C(Color c) => ColorUtility.ToHtmlStringRGBA(c) + (c.maxColorComponent > 1f ? " (HDR x" + c.maxColorComponent.ToString("0.0") + ")" : "");

        /// What an effect instance holds: sounds, lights, particle and material colours.
        static string Describe(GameObject go)
        {
            var sb = new StringBuilder();
            foreach (AudioSource a in go.GetComponentsInChildren<AudioSource>(true))
                sb.Append(" sound=").Append(a.clip != null ? a.clip.name : "-").Append(a.outputAudioMixerGroup != null ? "@" + a.outputAudioMixerGroup.name : "");
            foreach (Light l in go.GetComponentsInChildren<Light>(true)) sb.Append(" light=").Append(C(l.color)).Append(" i").Append(l.intensity.ToString("0.0"));
            foreach (ParticleSystem p in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                var mc = p.main.startColor;
                Color c = mc.mode == ParticleSystemGradientMode.Color ? mc.color : mc.mode == ParticleSystemGradientMode.TwoColors ? mc.colorMax :
                          mc.gradient != null ? mc.gradient.Evaluate(0.5f) : Color.white;
                sb.Append(" particles(").Append(p.name).Append(")=").Append(C(c));
            }
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                Material m = r.sharedMaterial;
                if (m == null) continue;
                sb.Append(" mat(").Append(r.name).Append(")=").Append(m.shader != null ? m.shader.name : "-");
                foreach (string prop in new[] { "_Color", "_TintColor", "_RimColor", "_EmissionColor", "_OutlineColor" })
                    if (m.HasProperty(prop)) sb.Append(' ').Append(prop).Append('=').Append(C(m.GetColor(prop)));
            }
            foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb is EffectController) continue;
                sb.Append(" [").Append(mb.GetType().Name);
                // the colours it carries (a rim light's, an indicator's)
                foreach (var f in mb.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    try
                    {
                        if (f.FieldType == typeof(Color)) sb.Append(' ').Append(f.Name).Append('=').Append(C((Color)f.GetValue(mb)));
                        else if (f.FieldType == typeof(Gradient) && f.GetValue(mb) != null) sb.Append(' ').Append(f.Name).Append("=gradient(").Append(C(((Gradient)f.GetValue(mb)).Evaluate(0.5f))).Append(')');
                    }
                    catch (Exception) { }
                }
                sb.Append(']');
            }
            return sb.ToString();
        }

        static string DumpFx()
        {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder("# effect events (event | effect set | model) count\n");
            foreach (var kv in s_events.OrderBy(k => k.Key)) sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            sb.Append("\n# effect instances (start event | controller): contents\n");
            foreach (var kv in s_effects.OrderBy(k => k.Key)) sb.Append(kv.Key).Append(":\t").Append(kv.Value).Append('\n');
            File.WriteAllText(Path.Combine(Dir, "effects.txt"), sb.ToString());
            return s_events.Count + " event kinds, " + s_effects.Count + " effect instances -> " + Dir + "/effects.txt";
        }

        // ---------------------------------------------------------------------------------------- enemies

        static string Survey(string tag)
        {
            PlayerController pc = PlayerController.Instance;
            if (pc == null) return "no hero";
            Targetable me = pc.GetComponent<Targetable>();
            BetterList<Targetable> all = me.EnumerateWithinRadius(80f, me.GetEnemyTeams());
            var sb = new StringBuilder();
            var kinds = new HashSet<string>();
            for (int i = 0; i < all.size; i++)
            {
                Targetable t = all[i];
                Controller c = t != null ? t.Controller : null;
                if (c == null || c.Model == null) continue;
                Actor actor = c.GetComponent<Actor>();
                var acts = new List<string>();
                if (actor != null && actor.AllActions != null)
                    foreach (Defiant.Action a in actor.AllActions)
                        if (a != null && (a is IActionAICombat || a is ActionBlock || a is ActionPhalanxBlock || a is ActionEvade || a is ActionHitByCount || a is ActionSnareGoblin || a.GetType().Name.Contains("Snare")) && !acts.Contains(a.GetType().Name)) acts.Add(a.GetType().Name);
                acts.Sort(string.CompareOrdinal);
                var flags = new List<string>();
                string all2 = string.Join(",", acts.ToArray());
                if (acts.Any(n => n.Contains("Ranged") || n.Contains("Gunslinger"))) flags.Add("ranged");
                if (acts.Contains("ActionBlock") || acts.Contains("ActionPhalanxBlock")) flags.Add("shield");
                if (acts.Contains("ActionHitByCount")) flags.Add("hit-count-guard");
                if (t.Protection != null && t.Protection.Health > 0) flags.Add("armour");
                if (acts.Any(n => n.Contains("Snare"))) flags.Add("snare");
                if (acts.Contains("ActionAssassinateAI") || acts.Contains("ActionMeleeThrall")) flags.Add("behind-landing");
                if (acts.Any(n => n.Contains("Combo"))) flags.Add("combo");
                if (acts.Contains("ActionEvade")) flags.Add("evades");
                Animator an = c.Model.GetComponentInChildren<Animator>();
                Destroyable d = t.GetComponent<Destroyable>();
                AIController ai = c as AIController;
                string key = WeaponGrips.ModelKey(c.Model.name);
                if (!kinds.Add(key + all2)) continue;
                sb.Append(tag.Length > 0 ? tag : FightName()).Append('\t').Append(key).Append('\t').Append(d != null ? d.MaxHealth.Value.ToString("0") : "-").Append('\t')
                  .Append(an != null && an.isHuman ? "humanoid" : "generic").Append('\t').Append(ai != null && ai.PoliteCombat ? "polite" : "not-polite").Append('\t')
                  .Append(string.Join(",", flags.ToArray())).Append('\t').Append(all2).Append('\n');
            }
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "enemies.tsv"), sb.ToString());
            Plugin.L.LogInfo("enemy survey:\n" + sb);
            return kinds.Count + " enemy kinds (see log)";
        }
    }
}

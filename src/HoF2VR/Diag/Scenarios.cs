using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// The Scenarios menu (Dev.Scenarios; user 2026-10-12 ~22:10: "Make a dev menu I can go into to load a bunch of
    /// specific scenarios"; docs/scenarios.md). Ready-made situations - fights with a chosen weapon and enemies, bosses,
    /// gaining equipment, the end-of-challenge coins, the mini-games, a shop, the deck builder, death, the exit portal -
    /// played in the dev profile (Safety/DevProfile.cs), never in the player's own save or run.
    ///
    /// The catalog is one text file, `scenarios.txt` (shipped in the DLL; a copy next to the DLL overrides it), one line per
    /// scenario: `Tab | Label | steps`. Steps are the mod's test-seam commands (the same ones the test harness uses), ';'
    /// between them, plus a few of the runner's own (`@map`, `@hub`, `@fresh`, `@wait`, `@until`, `@loadout`). The tab
    /// "Weapon" holds the loadouts: picking one remembers it, and the fights run it at their `@loadout`.
    /// The menu is the VR settings page's "Dev" tab (VRSettings); the playtester runs the same catalog (`scen run`).
    internal static class Scenarios
    {
        public sealed class Entry
        {
            public string Tab, Label;
            public string[] Steps;
            public int Index;
        }

        public static readonly List<Entry> Catalog = new List<Entry>();
        public static readonly List<string> Tabs = new List<string>();
        public static string CatalogFrom = "-";
        public static bool Broken;
        public static Entry Loadout, Last, Running;
        /// `scen here`: the steps run in the profile in use (test runs: the playtester takes its steps from the catalog);
        /// @map / @hub then only wait for the map, nothing is reloaded.
        static bool s_here;
        public static string Status = "idle", LastResult = "-";
        public static int Runs, Reloads;
        static Coroutine s_co;

        /// The menu can be offered (the setting, VR or a test run, the guards in place).
        /// (the guards are installed in Awake only - test runs, or [Dev] Scenarios on at the start: the setting turned on later
        /// leaves the menu off until the next start, as its text says; before, the menu came without the save redirect and the
        /// profile hook - a scenario then signed the player out of their own run and reloaded their own profile)
        public static bool Enabled => Settings.Scenarios && !Broken && Safety.Guarded;
        /// Seams that change the run (equip, fights, death...) are allowed: test runs and the dev profile only.
        public static bool SeamsAllowed => TestMode.Enabled || DevProfile.Active;
        public static bool Busy => Running != null;
        static readonly FieldInfo s_seqPos = AccessTools.Field(typeof(StartScreenController), "m_sequencePosition");

        public static void Register()
        {
            Load();
            Diag.Commands["scen"] = Command;
            Diag.Commands["gain"] = Gain;
            Diag.Commands["stat"] = StatSeam;
            Diag.Commands["cards"] = Cards;
            // `title`: the game's quit to title in the dev save (the dev run stays; Continue goes back to it)
            Diag.Commands["title"] = a =>
            {
                if (!DevProfile.Active) return "the dev profile only (your own: the pause menu's Quit to title)";
                SignOut();
                return "to the title (dev save)";
            };
            Diag.Commands["wincoins"] = a =>
            {
                if (!SeamsAllowed) return "test mode or the dev profile only";
                Challenge c = Challenge.Instance;
                if (c == null || c.Dungeon == null) return "no challenge";
                c.SetStatus(Challenge.StatusType.SuccessToken);
                c.Dungeon.EndDungeon();
                return "challenge won with its token (the end of the challenge follows)";
            };
            Diag.StatusExtras["scenarios"] = () => Diag.Q(Describe());
            // `fxfind <name part> [max]`: loaded objects (scene and assets) whose name has the part: particle systems, meshes
            // and their shaders -> log (looking for the game's own effects, e.g. a portal look)
            Diag.Commands["fxfind"] = a =>
            {
                if (a.Length < 2) return "usage: fxfind <name part> [max]";
                int max = a.Length > 2 ? int.Parse(a[2]) : 60, n = 0;
                var sb = new System.Text.StringBuilder();
                foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go == null || go.name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (go.transform.parent != null && go.transform.parent.name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) >= 0) continue;   // roots of matches only
                    var ps = go.GetComponentsInChildren<ParticleSystem>(true);
                    var rs = go.GetComponentsInChildren<Renderer>(true);
                    sb.Append("\n  ").Append(go.name).Append(go.scene.IsValid() ? " [scene " + go.scene.name + "]" : " [asset]").Append(" particles ").Append(ps.Length).Append(" renderers ").Append(rs.Length);
                    foreach (Renderer r in rs.Take(4))
                        if (r.sharedMaterial != null) sb.Append(" | ").Append(r.GetType().Name).Append(' ').Append(r.sharedMaterial.name).Append(" (").Append(r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "-").Append(')');
                    if (++n >= max) break;
                }
                Plugin.L.LogInfo("fxfind '" + a[1] + "': " + n + sb);
                return n + " objects (see log)";
            };
        }

        public static string Describe() =>
            (Enabled ? "on" : Settings.Scenarios ? "broken" : "off") + " | " + (DevProfile.Mode == DevProfile.M.Real && Status.StartsWith("leaving") ? "idle (left: your own save)" : Status) + " | loadout " + (Loadout != null ? Loadout.Label : "-") +
            " | last " + (Last != null ? Last.Label : "-") + " (" + LastResult + ") | runs " + Runs + " reloads " + Reloads + " | dev " + DevProfile.Describe() +
            " | catalog " + Catalog.Count + " from " + CatalogFrom;

        // ---- the catalog ----------------------------------------------------------------------------------------------
        static void Load()
        {
            Catalog.Clear();
            Tabs.Clear();
            string text = null;
            try
            {
                string over = Path.Combine(Path.GetDirectoryName(typeof(Scenarios).Assembly.Location) ?? ".", "scenarios.txt");
                if (File.Exists(over)) { text = File.ReadAllText(over); CatalogFrom = over; }
            }
            catch (Exception) { }
            if (text == null)
            {
                using (Stream s = typeof(Scenarios).Assembly.GetManifestResourceStream("HoF2VR.scenarios.txt"))
                    if (s != null) using (var r = new StreamReader(s)) text = r.ReadToEnd();
                CatalogFrom = "the mod";
            }
            if (text == null) { CatalogFrom = "missing"; return; }
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] f = line.Split('|');
                if (f.Length < 3) continue;
                var e = new Entry
                {
                    Tab = f[0].Trim(),
                    Label = f[1].Trim(),
                    Steps = string.Join("|", f, 2, f.Length - 2).Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(),
                    Index = Catalog.Count,
                };
                Catalog.Add(e);
                if (!Tabs.Contains(e.Tab)) Tabs.Add(e.Tab);
            }
            Loadout = Catalog.FirstOrDefault(x => x.Tab == "Weapon");
        }

        public static IEnumerable<Entry> InTab(string tab) => Catalog.Where(e => e.Tab == tab);

        public static Entry Find(string what)
        {
            int n;
            if (int.TryParse(what, out n)) return n >= 0 && n < Catalog.Count ? Catalog[n] : null;
            string w = what.Replace('_', ' ');
            return Catalog.FirstOrDefault(e => string.Equals(e.Label, w, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Tab + "/" + e.Label, w, StringComparison.OrdinalIgnoreCase))
                ?? Catalog.FirstOrDefault(e => (e.Tab + "/" + e.Label).IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ---- running --------------------------------------------------------------------------------------------------
        /// A press on the menu: a loadout is remembered, anything else runs (the menu closes, the pause menu too).
        public static string Pick(Entry e, bool here = false)
        {
            if (e == null) return "no such scenario";
            s_here = here;
            if (!Enabled && !(here && TestMode.Enabled)) return "the Scenarios menu is off";
            if (e.Tab == "Weapon") { Loadout = e; Plugin.L.LogInfo("Scenarios: weapon " + e.Label); return "weapon " + e.Label; }
            if (Busy) return "busy with " + Running.Label;
            Last = e;
            s_co = Diag.I.StartCoroutine(Run(e));
            return "running " + e.Tab + "/" + e.Label;
        }

        public static string Leave()
        {
            if (Busy) Stop("left");
            if (DevProfile.Mode == DevProfile.M.Real) return "not in the dev profile";
            DevProfile.Mode = DevProfile.M.ToReal;
            DebugMenu_Cheats.ChallengesUnlocked = false;
            Status = "leaving: back to your own save at the title";
            SignOut();
            return "leaving scenarios";
        }

        static void Stop(string why)
        {
            if (s_co != null && Diag.I != null) Diag.I.StopCoroutine(s_co);
            s_co = null;
            LastResult = why;
            Running = null;
        }

        static IEnumerator Run(Entry e)
        {
            Running = e;
            Runs++;
            Status = "running " + e.Label;
            Plugin.L.LogInfo("Scenarios: run '" + e.Tab + "/" + e.Label + "': " + string.Join("; ", e.Steps));
            ClosePause();
            var steps = new List<string>();
            foreach (string s in e.Steps)
            {
                if (s == "@loadout") { if (Loadout != null) steps.AddRange(Loadout.Steps); }
                else steps.Add(s);
            }
            if (!s_here && !steps.Any(s => s.StartsWith("@map") || s.StartsWith("@hub") || s.StartsWith("@fresh") || s == "@here") && DevProfile.Mode != DevProfile.M.Dev)
                steps.Insert(0, "@map");
            if (s_here) for (int i = 0; i < steps.Count; i++) if (steps[i] == "@map") steps[i] = "@mapready"; else if (steps[i] == "@hub") steps[i] = "@until ChallengeMap 30";
            string result = "done";
            foreach (string step in steps)
            {
                Status = e.Label + ": " + step;
                string fail = null;
                IEnumerator inner = Step(step, r => fail = r);
                while (inner.MoveNext()) yield return inner.Current;
                if (fail != null) { result = "stopped at '" + step + "': " + fail; break; }
            }
            LastResult = result;
            Status = "idle (" + e.Label + ": " + result + ")";
            Plugin.L.LogInfo("Scenarios: '" + e.Label + "' " + result);
            if (VRHelp.I != null && result != "done") VRHelp.I.Show("Scenario '" + e.Label + "'\n" + result, 4f);
            Running = null;
            s_co = null;
        }

        static IEnumerator Step(string step, Action<string> fail)
        {
            string[] a = step.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            switch (a[0])
            {
                case "@map":
                    {
                        // the dungeon map of the dev run, ready for a jump; a fresh run from the base when it isn't
                        if (DevProfile.Active && MapReadySoon()) break;
                        IEnumerator r = Reload("mid", fail);
                        while (r.MoveNext()) yield return r.Current;
                        break;
                    }
                case "@hub":
                    {
                        if (DevProfile.Active && GameWatch.State == "GameState_ChallengeMap") break;
                        IEnumerator r = Reload("hub", fail);
                        while (r.MoveNext()) yield return r.Current;
                        break;
                    }
                case "@fresh":
                    {
                        IEnumerator r = Reload(a.Length > 1 ? a[1] : "mid", fail);
                        while (r.MoveNext()) yield return r.Current;
                        break;
                    }
                case "@here":
                    break;
                case "@wait":
                    yield return new WaitForSecondsRealtime(a.Length > 1 ? F(a[1]) : 1f);
                    break;
                case "@until":
                    {
                        float t0 = Time.unscaledTime, max = a.Length > 2 ? F(a[2]) : 60f;
                        while (GameWatch.State.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            if (Time.unscaledTime - t0 > max) { fail("no " + a[1] + " after " + max + " s (now " + GameWatch.State + ")"); yield break; }
                            yield return null;
                        }
                        break;
                    }
                case "@confirm":
                    {
                        // the encounter's card waits for its "Continue" (A): pressed for the player, so a fight scenario goes
                        // straight to the campfire
                        float t0 = Time.unscaledTime;
                        while (TableBoards.OptionPlates < 1 || GameWatch.State != "GameState_Map")
                        {
                            if (Time.unscaledTime - t0 > 25f) { fail("no option on the encounter's card (now " + GameWatch.State + ")"); yield break; }
                            yield return new WaitForSecondsRealtime(0.2f);
                        }
                        // the encounter's story pages (each ends with its "continue" option) until the fight begins
                        while (GameWatch.State == "GameState_Map")
                        {
                            if (Time.unscaledTime - t0 > 40f) { fail("the encounter did not lead into its fight (now " + GameWatch.State + ")"); yield break; }
                            yield return new WaitForSecondsRealtime(1.2f);
                            if (GameWatch.State != "GameState_Map") break;
                            if (TableBoards.OptionPlates >= 1 && !TableBoards.SelectOption(0)) VRPad.Press("A", 6);
                        }
                        break;
                    }
                case "@mapready":
                    {
                        float t0 = Time.unscaledTime;
                        string why;
                        while (!DevJump.MapReady(out why))
                        {
                            if (Time.unscaledTime - t0 > 60f) { fail(why); yield break; }
                            yield return new WaitForSecondsRealtime(0.25f);
                        }
                        break;
                    }
                default:
                    {
                        string r = Diag.I.Run(step);
                        if (r.StartsWith("error", StringComparison.Ordinal) || r.StartsWith("unknown", StringComparison.Ordinal) || r.StartsWith("no ", StringComparison.Ordinal) ||
                            r.IndexOf("only", StringComparison.Ordinal) >= 0 && r.IndexOf("map", StringComparison.Ordinal) >= 0 || r.StartsWith("test mode", StringComparison.Ordinal) ||
                            r.StartsWith("failed", StringComparison.Ordinal) || r.StartsWith("busy", StringComparison.Ordinal))
                            fail(r);
                        else yield return null;
                        break;
                    }
            }
        }

        static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

        static bool MapReadySoon()
        {
            string why;
            return GameWatch.State == "GameState_Map" && DevJump.MapReady(out why);
        }

        /// The dev profile, fresh from a base: sign out (the game's quit to title), the profile load switches to the dev save,
        /// then Continue at the title and wait for the dungeon map (mid) or the challenge map (hub).
        static IEnumerator Reload(string baseName, Action<string> fail)
        {
            Reloads++;
            DevProfile.NextBase = baseName;
            DevProfile.Mode = DevProfile.M.ToDev;
            Status = "loading the dev save (" + baseName + ")";
            if (VRHelp.I != null) VRHelp.I.Show("Scenarios: loading the dev save\n(your own save is not touched)", 3f);
            SignOut();
            float t0 = Time.unscaledTime;
            // the reset boots the game again: the Loader, Init (the profile load switches), the title
            while (DevProfile.Mode != DevProfile.M.Dev || GameWatch.State != "GameState_StartScreen")
            {
                if (Time.unscaledTime - t0 > 120f) { fail("no title after the reset (now " + GameWatch.State + ", " + DevProfile.Mode + ")"); yield break; }
                yield return null;
            }
            DebugMenu_Cheats.ChallengesUnlocked = true;
            // Continue (as the title's button; a slot with mods asks first - its Play is X)
            yield return new WaitForSecondsRealtime(3f);
            // the title's own Continue without its mods question (the dev save's slot uses none that matter): StartGame once the
            // start screen is in its Ready sequence
            bool started = false;
            for (int i = 0; i < 80 && GameWatch.State == "GameState_StartScreen"; i++)
            {
                StartScreenController ssc = StartScreenController.Instance;
                object pos = ssc != null ? s_seqPos.GetValue(ssc) : null;
                if (!started && pos != null && pos.ToString() == "StartScreen_Ready")
                {
                    started = true;
                    try { ssc.StartGame(); Plugin.L.LogInfo("Scenarios: Continue at the title (dev save)"); }
                    catch (Exception ex) { Plugin.L.LogWarning("Scenarios: Continue: " + ex.GetBaseException().Message); }
                }
                if (ModsQuestionOpen()) VRPad.Press("X", 6);
                yield return new WaitForSecondsRealtime(0.5f);
            }
            string want = baseName == "hub" ? "GameState_ChallengeMap" : "GameState_Map";
            float nextStart = 0f;
            while (GameWatch.State != want)
            {
                if (Time.unscaledTime - t0 > 240f) { fail("no " + want + " (now " + GameWatch.State + ")"); yield break; }
                if (ModsQuestionOpen()) VRPad.Press("X", 6);
                // the caravan's attract screen ("Start") before the challenge map: its Start, as the player's A
                if (GameWatch.State == "GameState_Attract" && Time.unscaledTime >= nextStart) { nextStart = Time.unscaledTime + 3f; VRPad.Press("A", 6); }
                yield return new WaitForSecondsRealtime(0.5f);
            }
            if (baseName != "hub")
            {
                string why;
                while (!DevJump.MapReady(out why))
                {
                    if (Time.unscaledTime - t0 > 240f) { fail("the map never got ready: " + why); yield break; }
                    yield return new WaitForSecondsRealtime(0.5f);
                }
            }
            UnlockEquipment();
            Plugin.L.LogInfo("Scenarios: dev save '" + baseName + "' ready in " + (Time.unscaledTime - t0).ToString("0.0") + " s");
        }

        static bool ModsQuestionOpen()
        {
            try
            {
                foreach (Menu m in UnityEngine.Object.FindObjectsOfType<Menu>())
                    if (m != null && m.isActiveAndEnabled && m.name.IndexOf("Modding", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch (Exception) { }
            return false;
        }

        /// Every equipment card in the dev player's card set (the game's own debug cheat), so any weapon is at hand.
        static void UnlockEquipment()
        {
            try
            {
                MethodInfo m = AccessTools.Method(typeof(DebugMenu_Cheats), "UnlockAllEquipmentCards");
                if (m != null && Player.Instance != null) m.Invoke(null, null);
            }
            catch (Exception e) { Plugin.L.LogWarning("Scenarios: unlock equipment: " + e.GetBaseException().Message); }
        }

        /// The game's own "Quit to title": the platform signs out, the game resets (the mod stays: ResetGuard).
        static void SignOut()
        {
            Time.timeScale = 1f;
            Defiant.Platform.IPlatform p = Defiant.Platform.PlatformLocator.GetCurrentPlatform();
            if (p != null) p.SignOut();
            else Plugin.L.LogWarning("Scenarios: no platform to sign out");
        }

        static void ClosePause()
        {
            try
            {
                MenuManager mm = MenuManager.Instance;
                if (mm != null && mm.Paused) VRPad.Press("START", 4);
            }
            catch (Exception) { }
        }

        // ---- seams ----------------------------------------------------------------------------------------------------
        /// `scen` (state) | `scen list` | `scen run <label|tab/label|index>` | `scen here <label>` (test runs: in the profile in
        /// use) | `scen weapon <label>` | `scen repeat` | `scen leave` | `scen reload [mid|hub]`
        static string Command(string[] a)
        {
            string op = a.Length > 1 ? a[1] : "";
            string arg = a.Length > 2 ? string.Join(" ", a, 2, a.Length - 2) : "";
            switch (op)
            {
                case "list":
                    {
                        var sb = new System.Text.StringBuilder("scenario catalog (" + CatalogFrom + "):");
                        foreach (Entry e in Catalog) sb.Append("\n  ").Append(e.Index).Append(' ').Append(e.Tab).Append('/').Append(e.Label).Append(": ").Append(string.Join("; ", e.Steps));
                        Plugin.L.LogInfo(sb.ToString());
                        return Catalog.Count + " scenarios in " + Tabs.Count + " tabs (see log)";
                    }
                case "run": return Pick(Find(arg));
                case "here": return TestMode.Enabled ? Pick(Find(arg), true) : "test runs only";
                case "weapon": { Entry e = Find(arg); return e != null && e.Tab == "Weapon" ? Pick(e) : "no weapon " + arg; }
                case "repeat": return Last != null ? Pick(Last) : "nothing yet";
                case "leave": return Leave();
                case "stop": Stop("stopped"); return "stopped";
                case "reload":
                    {
                        if (Busy) return "busy";
                        var e = new Entry { Tab = "-", Label = "reload", Steps = new[] { "@fresh " + (arg.Length > 0 ? arg : "mid") } };
                        s_co = Diag.I.StartCoroutine(Run(e));
                        return "reloading";
                    }
                default: return Describe();
            }
        }

        /// `gain <card id>`: the card comes to the player as the game deals a reward (Card.Activate: equipment asks whether to
        /// equip it, the way an encounter's reward does).
        static string Gain(string[] a)
        {
            if (!SeamsAllowed) return "test mode or the dev profile only";
            if (a.Length < 2) return "usage: gain <card id>";
            if (GameWatch.State != "GameState_Map") return "only on the dungeon map (now " + GameWatch.State + ")";
            try
            {
                Card prefab = CardManager.Instance.GetCardById(a[1]);
                if (prefab == null) return "no card " + a[1];
                Card card = Card.Create(prefab, DeckManager.Instance.OffscreenContainer, true);
                Card.Activate(card, CardManager.Instance, c => { if (Map.Instance != null) Map.Instance.Focus(); });
                return "gaining " + card.name;
            }
            catch (Exception e) { return "error: " + e.GetBaseException().Message; }
        }

        /// `stat <health|maxhealth|gold|food|fame> <change>`: the player's stat changes the way an encounter's reward changes
        /// it (the stat's own events: the cards' animations).
        static string StatSeam(string[] a)
        {
            if (!SeamsAllowed) return "test mode or the dev profile only";
            if (a.Length < 3) return "usage: stat health|maxhealth|gold|food|fame <change>";
            Player p = Player.Instance;
            if (p == null) return "no player";
            float d = F(a[2]);
            switch (a[1].ToLowerInvariant())
            {
                case "health": p.Health.Set(p.Health.Get() + d, null, null); break;
                case "maxhealth": p.MaxHealth.Set(p.MaxHealth.Get() + Mathf.RoundToInt(d), null, null); break;
                case "gold": p.Gold.Set(p.Gold.Get() + Mathf.RoundToInt(d), null, null); break;
                case "food": p.Food.Set(p.Food.Get() + Mathf.RoundToInt(d), null, null); break;
                case "fame": p.Fame.Set(p.Fame.Get() + Mathf.RoundToInt(d), null, null); break;
                default: return "no stat " + a[1];
            }
            return a[1] + " " + (d >= 0 ? "+" : "") + a[2];
        }

        /// `cards <filter> [type]`: card ids, types and titles to the log (for the catalog).
        static string Cards(string[] a)
        {
            string f = a.Length > 1 ? a[1] : "";
            string t = a.Length > 2 ? a[2] : "";
            var sb = new System.Text.StringBuilder();
            int n = 0;
            AssetManager am = AssetManager.Instance;
            if (am == null || am.Cards == null) return "no cards yet";
            foreach (Card c in am.Cards.Assets)
            {
                if (c == null) continue;
                string id = c.CardData != null ? c.CardData.Id : c.name;
                string type = c.GetType().Name;
                if (f.Length > 0 && f != "*" && id.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 && c.name.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (t.Length > 0 && type.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string extra = "";
                var eq = c as EquipmentCard;
                if (eq != null) extra = " " + eq.EquipmentCategory + (eq.Equipment is WeaponRef w ? " " + w.Handedness : "");
                sb.Append("\n  ").Append(id).Append(" [").Append(type).Append(extra).Append("] ").Append(c.name);
                n++;
            }
            Plugin.L.LogInfo("cards (" + f + " " + t + "): " + n + sb);
            return n + " cards (see log)";
        }
    }
}

using System;
using System.Collections.Generic;
using NodeCanvas.Framework;
using UnityEngine;

namespace HoF2VR
{
    /// Test seam: run the game's own mini-game tasks (pendulum, wheel, dice) on the card table without
    /// an encounter that contains them. The task objects are the game's (PendulumTask, WheelSpinTask,
    /// DiceRollTask), executed the way an encounter graph executes them, with simple data:
    ///   mg pendulum [Easy|Medium|Hard|VeryHard]   zones: huge failure / success / huge success
    ///   mg wheel [Easy|Medium|Hard] [nudge]       six chance cards (`nudge`: plus one nudge)
    ///   mg chance [Easy|Medium|Hard]              four chance cards: shuffle, pick one
///   mg dice [target] [bonus dice] [bonus rerolls]
    ///   mg                                        what is running
    /// Run them from the dungeon map (GameState_Map): they need the challenge's dungeon settings.
    internal static class MinigameSeams
    {
        static ActionTask s_task;
        static string s_status = "-";
        static int s_runs;

        public static void Register()
        {
            Diag.Commands["mg"] = Command;
            Diag.StatusExtras["mg"] = () => Diag.Q(s_status);
            Diag.StatusExtras["chance"] = () =>
            {
                ChanceCardTask t = ChanceCardTask.RunningTask;
                return Diag.Q(t == null ? "-" : "running result=" + (t.Result != null ? t.Result.name.Replace("(Clone)", "") : "-"));
            };
        }

        static string Command(string[] a)
        {
            if (a.Length < 2) return s_status;
            if (a[1].ToLowerInvariant() == "nudge")
            {
                // `mg nudge`: one nudge for the wheel that is running now (given before the task, it was gone by the stop)
                WheelOfFortune w = DeckManager.Instance != null && DeckManager.Instance.WheelSpinContainer != null ? DeckManager.Instance.WheelSpinContainer.GetComponent<WheelOfFortune>() : null;
                if (w == null) return "no wheel";
                w.NudgesLeft += 1;
                return "nudges left " + w.NudgesLeft + (WheelSpinTask.RunningTask != null ? " (wheel running)" : " (no wheel task)");
            }
            if (s_task != null && s_task.isRunning) return "busy: " + s_status;
            if (Challenge.Instance == null || Challenge.Instance.Dungeon == null) return "no dungeon (run it from the map)";
            try
            {
                switch (a[1].ToLowerInvariant())
                {
                    case "pendulum": return Run(Pendulum(a.Length > 2 ? a[2] : "Medium"), "pendulum");
                    case "wheel":
                        // `mg wheel [difficulty] [nudge]`: give the wheel one nudge (as AddWheelNudgeTask does)
                        if (a[a.Length - 1] == "nudge" && DeckManager.Instance != null)
                        {
                            WheelOfFortune w = DeckManager.Instance.WheelSpinContainer.GetComponent<WheelOfFortune>();
                            if (w != null) w.NudgesLeft += 1;
                        }
                        return Run(Wheel(a.Length > 2 && a[2] != "nudge" ? a[2] : "Easy"), "wheel");
                    case "chance": return Run(Chance(a.Length > 2 ? a[2] : "Easy"), "chance");
                    case "dice":
                        return Run(Dice(a.Length > 2 ? int.Parse(a[2]) : 0, a.Length > 3 ? int.Parse(a[3]) : 0, a.Length > 4 ? int.Parse(a[4]) : 0), "dice");
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogError("mg " + a[1] + ": " + e);
                return "failed: " + e.Message;
            }
            return "usage: mg pendulum|wheel|dice ...";
        }

        static string Run(ActionTask task, string what)
        {
            Component agent = Dealer.Instance != null ? (Component)Dealer.Instance : CardTable.Instance;
            int run = ++s_runs;
            s_status = what + " " + run + " running";
            s_task = task;
            Plugin.L.LogInfo("mg: starting the game's " + task.GetType().Name + " (" + what + " " + run + ")");
            task.ExecuteAction(Agent.Make(agent), null, null, ok =>
            {
                s_status = what + " " + run + " done " + (ok ? "ok" : "failed");
                // In an encounter the flow goes on and the map takes the selection back (Map.Focus); a
                // task run on its own leaves the game's main selection blocked: hand it back the same way
                // (Map.FocusComplete, without Focus's save).
                try
                {
                    Map map = Map.Instance;
                    if (map != null && map.SelectableGroup != null)
                    {
                        UISelection.UnlockGroup(map.SelectableGroup);
                        UISelection.TrySetGroup(map.SelectableGroup);
                        UISelection.LockGroup(map.SelectableGroup);
                    }
                }
                catch (Exception e) { Plugin.L.LogWarning("mg: map selection: " + e.Message); }
                Plugin.L.LogInfo("mg: " + s_status);
            });
            return "started " + what + " " + run;
        }

        static PendulumZoneGroup.ZoneData Zone(Pendulum.ArcSize size, ChanceType type, float offset = 0f) =>
            new PendulumZoneGroup.ZoneData { ArcSize = size, ChanceType = type, Offset = offset };

        static ActionTask Pendulum(string difficulty)
        {
            var group = new PendulumZoneGroup.Data();
            // (the background is failure; zones are success, huge success or huge failure)
            group.Zones.Add(Zone(global::Pendulum.ArcSize._8, ChanceType.HugeFailure, 6f));
            group.Zones.Add(Zone(global::Pendulum.ArcSize._8, ChanceType.Success, 6f));
            group.Zones.Add(Zone(global::Pendulum.ArcSize._4, ChanceType.HugeSuccess));
            group.Zones.Add(Zone(global::Pendulum.ArcSize._8, ChanceType.Success));
            group.Zones.Add(Zone(global::Pendulum.ArcSize._8, ChanceType.HugeFailure, 6f));
            var data = new Pendulum.Data();
            data.ZoneGroupData.Add(group);
            data.Difficulty = new BBParameter<Pendulum.PendulumDifficulty>((Pendulum.PendulumDifficulty)Enum.Parse(typeof(Pendulum.PendulumDifficulty), difficulty, true));
            var task = new PendulumTask();
            task.m_pendulumData = data;
            task.m_chanceVar = new BBParameter<ChanceType>(ChanceType.Failure);
            return task;
        }

        static ActionTask Wheel(string difficulty)
        {
            var data = new WheelOfFortune.WheelData { Segments = new List<WheelOfFortune.SegmentData>() };
            foreach (ChanceType ct in new[] { ChanceType.Success, ChanceType.Failure, ChanceType.HugeSuccess, ChanceType.Failure, ChanceType.Success, ChanceType.HugeFailure })
            {
                var seg = new WheelOfFortune.SegmentData { Id = ct.ToString() };
                seg.ChanceTypeDescriptors.Add(new ChanceCardTask.ChanceTypeDescriptor { ChanceType = ct, Count = new BBParameter<int>(1) });
                data.Segments.Add(seg);
            }
            var task = new WheelSpinTask();
            task.m_wheelData = data;
            task.m_difficulty = (WheelDifficulty)Enum.Parse(typeof(WheelDifficulty), difficulty, true);
            task.m_shuffleType = new BBParameter<WheelSpinTask.SHUFFLE_TYPE>(WheelSpinTask.SHUFFLE_TYPE.RoundRobin);
            task.m_resultSegmentIdVar = new BBParameter<string>("");
            task.m_invertVoice = new BBParameter<bool>(false);
            task.m_suppressVoice = new BBParameter<bool>(false);
            return task;
        }

        static ActionTask Chance(string difficulty)
        {
            var task = new ChanceCardTask();
            foreach (ChanceType ct in new[] { ChanceType.HugeSuccess, ChanceType.Success, ChanceType.Failure, ChanceType.HugeFailure })
                task.m_chanceTypeDescriptors.Add(new ChanceCardTask.ChanceTypeDescriptor { ChanceType = ct, Count = new BBParameter<int>(1) });
            task.m_difficulty = (ChanceDifficulty)Enum.Parse(typeof(ChanceDifficulty), difficulty, true);
            task.m_chanceVar = new BBParameter<ChanceType>(ChanceType.Failure);
            return task;
        }

        static ActionTask Dice(int target, int bonusDice, int bonusRerolls)
        {
            var task = new DiceRollTask();
            task.m_target = new BBParameter<int>(target);
            task.m_bonusDice = new BBParameter<int>(bonusDice);
            task.m_bonusRerolls = new BBParameter<int>(bonusRerolls);
            task.m_result = new BBParameter<int>(0);
            task.m_commonSide = new BBParameter<int>(0);
            return task;
        }
    }
}

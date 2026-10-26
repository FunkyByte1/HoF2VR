using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.XR;

namespace HoF2VR
{
    /// P4-14 (playtest 4, 2026-10-19, mark 222145_11: the assassin reappeared at arm's length): "make them teleport at least 2 more
    /// feet away". The game's assassin (ActionAssassinateAI) vanishes, then reappears 1 m past its target, turned to it, and strikes
    /// (TriggerNextAttack: position += toward * (distance + 1)). In VR it lands Extra further out along that line, measured from
    /// the player's eyes (the hero's spot can sit nearer the landing than the eyes do), on the navmesh as far as the ground goes.
    internal static class AssassinRoom
    {
        public static float Extra = 0.7f;           // m beyond the game's landing (2 ft = 0.61 m)
        public static int Landings, Moved, Blocked;
        public static string Last = "-";

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ActionAssassinateAI), "TriggerNextAttack"),
                    prefix: new HarmonyMethod(typeof(AssassinRoom), nameof(Before)), postfix: new HarmonyMethod(typeof(AssassinRoom), nameof(After)));
            }
            catch (Exception e) { Plugin.L.LogWarning("AssassinRoom: patch failed: " + e.Message); }
            // `assassin? [extra <m>]`: the landings so far (and the extra distance, this run only)
            Diag.Commands["assassin?"] = a =>
            {
                if (a.Length > 2 && a[1] == "extra") Extra = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                return "extra " + Extra.ToString("0.00") + " landings " + Landings + " moved " + Moved + " blocked " + Blocked + " last " + Last;
            };
        }

        static void Before(ActionAssassinateAI __instance, out Vector3 __state)
        {
            __state = __instance != null && __instance.ActorTransform != null ? __instance.ActorTransform.position : Vector3.zero;
        }

        static void After(ActionAssassinateAI __instance, Vector3 __state)
        {
            try
            {
                if (__instance == null || !XRSettings.enabled || !CombatVR.Active || VRRig.I == null) return;
                Transform t = __instance.ActorTransform;
                Targetable target = __instance.Target;
                PlayerController pc = PlayerController.Instance;
                if (t == null || target == null || pc == null || target.gameObject != pc.gameObject) return;
                Vector3 jump = t.position - __state;
                jump.y = 0f;
                if (jump.magnitude < 0.9f) return;   // no teleport: the run ended (Complete)
                Landings++;
                Vector3 hero = pc.transform.position, head = VRRig.I.HeadWorldPos, p = t.position;
                Vector3 u = p - hero;
                u.y = 0f;
                if (u.sqrMagnitude < 1e-4f) return;
                float dGame = u.magnitude;
                u /= dGame;
                Vector3 fromHead = p - head;
                fromHead.y = 0f;
                float dHead = fromHead.magnitude;
                // at least Extra further than the game's landing, as the eyes see it
                float e = Mathf.Clamp(Mathf.Max(Extra, dGame + Extra - dHead), 0f, 1.6f);
                if (Extra <= 0f || e <= 0.01f) { Last = "game's landing " + dHead.ToString("0.00") + " m from the eyes (extra off)"; return; }
                Vector3 want = p + u * e;
                // along the ground from the game's landing (the first test: a landing sampled up to 1 m away and a ray blocked at
                // once pulled one 0.96 m TOWARD the player): only a spot further from the eyes than the game's counts
                NavMeshHit hit;
                Vector3 from = p;
                if (NavMesh.SamplePosition(p, out hit, 0.3f, NavMesh.AllAreas)) from = hit.position;
                Vector3 to = want;
                bool blocked = false;
                if (NavMesh.Raycast(from, want, out hit, NavMesh.AllAreas)) { to = hit.position; blocked = true; }
                else if (NavMesh.SamplePosition(want, out hit, 0.3f, NavMesh.AllAreas)) to = hit.position;
                to.y = Mathf.Abs(to.y - p.y) < 1.5f ? to.y : p.y;
                Vector3 moved = to - p;
                moved.y = 0f;
                Vector3 toHead = to - head;
                toHead.y = 0f;
                if (moved.magnitude < 0.05f || toHead.magnitude < dHead + 0.05f)
                {
                    Blocked++;
                    Last = "no room past " + dHead.ToString("0.00") + " m from the eyes (the ground ends): the game's landing kept";
                    if (Landings <= 40) Plugin.L.LogInfo("AssassinRoom: " + Last);
                    return;
                }
                t.position = to;
                Vector3 look = hero - to;
                look.y = 0f;
                if (look.sqrMagnitude > 1e-4f) t.rotation = Quaternion.LookRotation(look);
                Moved++;
                if (blocked) Blocked++;
                Vector3 nowHead = to - head;
                nowHead.y = 0f;
                Last = "landed " + nowHead.magnitude.ToString("0.00") + " m from the eyes (the game's " + dHead.ToString("0.00") + " m; " + dGame.ToString("0.00") + " m from the hero), moved " +
                       moved.magnitude.ToString("0.00") + " m" + (blocked ? " (the ground ends there)" : "");
                if (Landings <= 40) Plugin.L.LogInfo("AssassinRoom: " + WeaponGrips.ModelKey(t.name) + " " + Last);
            }
            catch (Exception e) { Plugin.L.LogWarning("AssassinRoom: " + e.Message); }
        }
    }
}

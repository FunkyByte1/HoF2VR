using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// Combat player diagnostics + test helpers.
    internal static class PlayerInfo
    {
        public static bool God;

        public static void Apply(HarmonyLib.Harmony h)
        {
            if (!TestMode.Enabled && !Settings.Scenarios) return;   // test runs and the Scenarios menu only: never patched for normal play
            h.Patch(HarmonyLib.AccessTools.Method(typeof(Destroyable), "ApplyDamage"),
                prefix: new HarmonyLib.HarmonyMethod(typeof(PlayerInfo), nameof(BlockPlayerDamage)));
        }

        static bool BlockPlayerDamage(Destroyable __instance)
        {
            if (!God) return true;
            PlayerController pc = PlayerController.Instance;
            return pc == null || __instance.gameObject != pc.gameObject;
        }

        public static void Register()
        {
            Diag.Commands["player"] = a => { Plugin.L.LogInfo("player:" + Describe()); return "logged"; };
            // `killall`: the game's own debug cheat "Kill Enemies" (test runs: ends a fight on demand)
            Diag.Commands["killall"] = a =>
            {
                if (!Scenarios.SeamsAllowed) return "test mode or the dev profile only";
                var m = HarmonyLib.AccessTools.Method(typeof(DebugMenu_Cheats), "KillEnemies");
                if (m == null) return "no KillEnemies";
                m.Invoke(null, null);
                return "killed enemies (state " + GameWatch.State + ")";
            };
            // `die`: the hero is left with 1 health and no god mode, so the next enemy hit kills him (the game's own
            // death path; test runs: the death flow)
            Diag.Commands["die"] = a =>
            {
                if (!Scenarios.SeamsAllowed) return "test mode or the dev profile only";
                PlayerController pc = PlayerController.Instance;
                Destroyable d = pc != null ? pc.GetComponent<Destroyable>() : null;
                if (d == null) return "no hero";
                God = false;
                DebugMenu_Cheats.GodMode = false;
                d.DebugMenuInvulnerable = 0;
                d.CurrentHealth = 1f;
                return "the hero has 1 health left (state " + GameWatch.State + ")";
            };
            Diag.Commands["god"] = a =>
            {
                if (!Scenarios.SeamsAllowed) return "test mode or the dev profile only";
                God = a.Length < 2 || a[1] != "off";
                DebugMenu_Cheats.GodMode = God;
                return "god=" + God;
            };
        }

        /// Test mode: keep the god flag applied to whatever player controller exists.
        public static void Update()
        {
            if (!God || !Scenarios.SeamsAllowed) return;
            PlayerController pc = PlayerController.Instance;
            if (pc == null) return;
            Destroyable d = pc.GetComponent<Destroyable>();
            if (d != null && d.DebugMenuInvulnerable == 0) d.DebugMenuInvulnerable = 1;
        }

        public static string Describe()
        {
            var sb = new StringBuilder();
            PlayerController pc = PlayerController.Instance;
            if (pc == null) return " no PlayerController";
            sb.AppendFormat("\n  controller {0} rot={1}", Diag.V(pc.transform.position), Diag.V(pc.transform.eulerAngles));
            Model model = pc.Model;
            if (model != null)
            {
                sb.AppendFormat("\n  model {0} path={1}", Diag.V(model.transform.position), VRUI.Path(model.transform));
                Animator an = model.Animator;
                if (an != null)
                {
                    sb.AppendFormat("\n  animator human={0}", an.isHuman);
                    if (an.isHuman)
                    {
                        Transform head = an.GetBoneTransform(HumanBodyBones.Head);
                        Transform le = an.GetBoneTransform(HumanBodyBones.LeftEye);
                        sb.AppendFormat(" head={0} at {1} leftEye={2}", head ? VRUI.Path(head) : "-", head ? Diag.V(head.position) : "-", le ? le.name : "-");
                        foreach (var b in new[] { HumanBodyBones.RightHand, HumanBodyBones.LeftHand, HumanBodyBones.Neck, HumanBodyBones.Chest })
                        {
                            Transform t = an.GetBoneTransform(b);
                            sb.AppendFormat("\n    {0}: {1}", b, t ? VRUI.Path(t) + " at " + Diag.V(t.position) : "-");
                        }
                    }
                }
                if (model.AttachNodes != null)
                    foreach (var n in model.AttachNodes)
                        sb.AppendFormat("\n  attach '{0}' -> {1}", n.Name, n.Transform ? n.Transform.name + " at " + Diag.V(n.Transform.position) : "-");
                if (model.MultiAttachNodes != null)
                    foreach (var n in model.MultiAttachNodes)
                        sb.AppendFormat("\n  multi '{0}' -> {1} transforms", n.Name, n.Transforms != null ? n.Transforms.Length : 0);
                Transform[] eyes = model.FindMultiAttachNode("eyes");
                if (eyes != null && eyes.Length > 0)
                {
                    Vector3 m = Vector3.zero;
                    foreach (Transform e in eyes) m += e.position;
                    m /= eyes.Length;
                    sb.AppendFormat("\n  eyes midpoint {0} (height above root {1:0.###}) first={2}", Diag.V(m), m.y - pc.transform.position.y, VRUI.Path(eyes[0]));
                }
                PlayerSkin skin = model.GetComponentInChildren<PlayerSkin>();
                if (skin != null)
                {
                    foreach (var g in new[] { new { n = "body", s = skin.BodySkinGroup }, new { n = "head", s = skin.HeadSkinGroup }, new { n = "helm", s = skin.HelmSkinGroup }, new { n = "hood", s = skin.HoodSkinGroup } })
                        sb.AppendFormat("\n  skin {0}: parent={1} renderers={2}", g.n, g.s != null && g.s.Parent ? g.s.Parent.name : "-", g.s != null && g.s.Renderers != null ? g.s.Renderers.Length : 0);
                }
                int rc = 0;
                foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
                    if (rc++ < 40) sb.AppendFormat("\n  renderer {0} [{1}] enabled={2}", VRUI.Path(r.transform).Replace(VRUI.Path(model.transform), "~"), r.GetType().Name, r.enabled);
            }
            Inventory inv = pc.Inventory;
            if (inv != null)
            {
                Equip(sb, "weapon", inv.Weapon);
                Equip(sb, "shield", inv.Shield);
                Equip(sb, "helm", inv.Helm);
                Equip(sb, "armour", inv.Armour);
                Equip(sb, "artifact", inv.Artifact);
            }
            return sb.ToString();
        }

        static void Equip(StringBuilder sb, string what, Equipment e)
        {
            if (e == null) { sb.AppendFormat("\n  {0}: none", what); return; }
            sb.AppendFormat("\n  {0}: {1}", what, e.name);
            if (e.AllModels != null)
                foreach (Model m in e.AllModels)
                    if (m != null) sb.AppendFormat("\n    model {0} parent={1} at {2}", m.name, m.transform.parent ? VRUI.Path(m.transform.parent) : "-", Diag.V(m.transform.position));
        }
    }
}

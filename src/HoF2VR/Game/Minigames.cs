using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Card-table mini-games with motion controllers.
    ///  Dice: grab them with a grip, shake and throw (DiceHand), or - the fallback - hold a trigger to
    ///  draw them back (the catapult tension builds like pulling the stick) and release to throw.
    ///  The pendulum and the wheel stop with a slap on the table or a grip (TableStops), or with the
    ///  trigger (VRPointer's "confirm when pointing at nothing"); chance cards are picked with the laser.
    internal static class Minigames
    {
        static readonly AccessTools.FieldRef<DiceRoller, DiceRoller.State> s_diceState =
            AccessTools.FieldRefAccess<DiceRoller, DiceRoller.State>("m_state");

        static bool s_drawn;
        static bool s_hintShown;
        static bool s_hintUp;

        /// A trigger is drawing the dice back (the fallback throw).
        public static bool Drawn => s_drawn;

        public static void Register()
        {
            Diag.StatusExtras["dice"] = () => Diag.Q(DiceHand.Status());
        }

        public static bool DiceCatapult
        {
            get
            {
                DiceRollTask task = DiceRollTask.RunningTask;
                if (task == null || task.Roller == null) return false;
                return s_diceState(task.Roller) == DiceRoller.State.Catapault;
            }
        }

        public static void HideDiceHint()
        {
            if (s_hintUp && VRHelp.I != null) VRHelp.I.Hide();
            s_hintUp = false;
            DiceTip.Hide();
        }

        /// Called from VRPad.Map in menu/table mode. Returns true if the dice took over the triggers.
        public static bool MapDice(VRGamepad d, Hand l, Hand r)
        {
            if (!DiceCatapult)
            {
                s_drawn = false;
                DiceTip.Hide();
                return false;
            }
            // (candidate 8, the coordinator 2026-10-17: not a text panel in front of the Dealer - a short hint at the dice, only if
            // they wait untaken for a few seconds)
            DiceTip.Tick();
            if (DiceHand.Holder != null)
            {
                s_drawn = false;
                return true;   // in the hand: the grip release throws, the triggers do nothing
            }
            if (l.TriggerBtn || r.TriggerBtn)
            {
                d.LS = new Vector2(0f, -1f);
                if (!s_drawn) s_drawnLeft = l.TriggerBtn && !r.TriggerBtn;
                s_drawn = true;
            }
            else if (s_drawn)
            {
                s_drawn = false;
                VRPad.Press("A", 4);
                Haptics.Pulse(s_drawnLeft, 0.6f, 0.1f);
                DiceHand.NoteClassicThrow(s_drawnLeft);
                HideDiceHint();
            }
            return true;
        }

        static bool s_drawnLeft;
    }

    /// The dice's hint: two words over the waiting dice, facing the eyes, once they have waited HintAfter seconds untaken;
    /// gone when a hand takes them or they are thrown. (Was a text card with four lines in front of the Dealer.)
    internal static class DiceTip
    {
        public static float HintAfter = 4f, Up = 0.09f;
        public static int Shows;
        static float s_since = -1f;
        static Transform s_root;
        static UILabel s_label;

        public static void Tick()
        {
            Vector3 at = Vector3.zero;
            float radius = 0f;
            bool waiting = DiceHand.Holder == null && DiceHand.WaitingDice(out at, out radius) && !VRFade.IsBlack && VRRig.I != null;
            if (!waiting) { s_since = -1f; Hide(); return; }
            float now = Time.unscaledTime;
            if (s_since < 0f) s_since = now;
            if (now - s_since < HintAfter) return;
            if (s_root == null)
            {
                if (!GameText.Ready) return;
                var go = new GameObject("HoF2VR_DiceTip") { layer = VRUI.Layer };
                UnityEngine.Object.DontDestroyOnLoad(go);
                GameText.Panel(go, 3160);
                var holder = new GameObject("scale") { layer = VRUI.Layer };
                holder.transform.SetParent(go.transform, false);
                holder.transform.localScale = Vector3.one * 0.00045f;
                s_label = GameText.Label(holder.transform, "text", GameText.Normal, 30, GameText.Parchment, 2);
                s_root = go.transform;
            }
            s_label.text = Settings.DiceThrow == Settings.DiceMode.Hybrid ? "Grab them, throw" : "Trigger: throw";
            float sc = VRRig.I.Scale;
            Vector3 p = at + Vector3.up * (radius + Up * sc);
            // (turned to the eyes once, when it appears: it stays put while the head moves - the spawn rule, 2026-10-17 21:50)
            if (!s_root.gameObject.activeSelf) s_hintRot = Quaternion.LookRotation(p - VRRig.I.HeadWorldPos, Vector3.up);
            s_root.SetPositionAndRotation(p, s_hintRot);
            s_root.localScale = Vector3.one * sc;
            if (!s_root.gameObject.activeSelf) { s_root.gameObject.SetActive(true); Shows++; Plugin.L.LogInfo("dice: the hint over the dice (untaken for " + HintAfter + " s)"); }
        }

        static Quaternion s_hintRot = Quaternion.identity;
        public static void Hide() { if (s_root != null && s_root.gameObject.activeSelf) s_root.gameObject.SetActive(false); }
        public static bool Shown => s_root != null && s_root.gameObject.activeSelf;
    }
}

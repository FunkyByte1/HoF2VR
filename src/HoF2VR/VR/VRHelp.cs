using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// VR controls card. Replaces the game's Xbox-controller tutorial (UIDynamicControllerDialogue)
    /// at the start of the first fights, and can be shown any time with the `help` command.
    /// Text lines of the form "input|action" are laid out in two aligned columns.
    internal class VRHelp : MonoBehaviour
    {
        public static VRHelp I;
        Transform m_card;
        TextCard m_text;
        float m_hideAt;
        bool m_keep;
        int m_fightsShown;
        float m_down = 0.12f;
        float m_tilt = 10f;

        /// The VR fighting controls ("input|action" lines). Shown on the waiting-space board before fights
        /// (Settings Combat.ControlsCard = WaitingSpace, the default since 2026-10-11) or on this card at fight start.
        internal static string CombatText
        {
            get
            {
                bool lefty = Settings.LeftHanded;
                string w = lefty ? "left" : "right", sh = lefty ? "right" : "left";
                if (CombatSettings.Physical)
                    // Physical combat mode: the rules fit on one card (docs/combat-uyf-design.md §8.5.1)
                    return "FIGHTING IN VR\n" +
                           "\n" +
                           "Swing for real|Hits and blocks wear down its guard (the white bar)\n" +
                           "Guard broken: a glowing slash on it|" + (CombatSettings.CritsOnly ? "Cut along it (either way): only that hurts\n" : "Cut along it (either way): a crit\n") +
                           "Glowing bar: where the blow will pass|Shield or sword on it (it turns white): block\n" +
                           "Arrow: you can't block it|Duck or lean the way the arrow points\n" +
                           (CombatSettings.Counter != CombatSettings.CounterRule.Off ? "Gold bar: meet the blow|Counter, then hit it: the riposte\n"
                               : "Step back out of reach|The bar dims: the blow can't reach you\n") +
                           DashLines() +
                           "Shield into an enemy|Bash. Downed: finish it. Armour: quick hits\n" +
                           "Left stick / right stick|Move / turn\n" +
                           Cap(w) + " grip / " + sh + " grip|Hold, point, let go: weapon ability / artifact\n" +
                           "X / Y / B|Companion / finisher / bash\n" +
                           "Hold the left stick click|Pause\n" +
                           "\n" +
                           "Attack from behind: a heartbeat on that side, a marker at the edge of your view, a hiss.\n" +
                           "After a win: walk into the purple portal or touch it (or hold A).";
                return "FIGHTING IN VR\n" +
                       "\n" +
                       "Swing the sword (" + w + " hand)|Attack - keep swinging for combos\n" +
                       "Jab the shield as an enemy flashes|Counter\n" +
                       "Thrust the shield forward|Bash - breaks guards and armour\n" +
                       "Stab down at a fallen enemy|Finisher\n" +
                       "\n" +
                       (CombatSettings.Walking == CombatSettings.WalkMode.Direct ? DashLines() : "A|Dodge roll - the left stick sets the direction\n") +
                       "Left stick / right stick|Move / turn\n" +
                       Cap(w) + " grip / " + sh + " grip|Weapon ability / artifact\n" +
                       "X / Y|Companion / finisher\n" +
                       Cap(w) + " trigger / " + sh + " trigger / B|Attack / counter / bash\n" +
                       "Hold the left stick click|Pause\n" +
                       "\n" +
                       "Attack from behind: a heartbeat on that side, a marker at the edge of your view, a hiss.\n" +
                       "After a win: walk into the purple portal or touch it (or hold A).";
            }
        }

        static string Cap(string s) => char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// The Dash on the controls cards follows [Combat] Dash (the coordinator 2026-10-14): Until You Fall's dash (the default)
        /// is a move, not a shield - only where it ends protects you, so no line may claim safety; Short keeps its moment in
        /// which nothing hits you. GlyphCard uses the same words.
        internal static string DashAction => CombatSettings.UyfDash ? "Dash - forward onto an enemy, hitting it" : "Dash - nothing hits you mid-dash";
        internal const string FlickMotion = "Right stick flicked forward / back", FlickAction = "Dash that way";

        /// "input|action" lines: A + the left stick, and the right stick's flick when [Combat] DashFlick is on.
        static string DashLines() => "A + left stick|" + DashAction + "\n" + (CombatSettings.DashFlick ? FlickMotion + "|" + FlickAction + "\n" : "");

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.PropertyGetter(typeof(UIDynamicControllerDialogue), "ShouldShow"),
                postfix: new HarmonyMethod(typeof(VRHelp), nameof(NoPadTutorial)));
        }

        static void NoPadTutorial(ref bool __result)
        {
            if (!XRSettings.enabled || !__result) return;
            __result = false;
            if (I != null) I.m_wantFightCard = true;
        }

        bool m_wantFightCard;

        void Awake()
        {
            I = this;
            Diag.Commands["help"] = a => { Show(CombatText, 20f); return "showing help"; };
        }

        /// The card: the game's fonts and frame (TextCard) on the overlay layer, 1 UI pixel = 0.7 mm at 0.75 m.
        void Create()
        {
            var root = new GameObject("HoF2VR_Help") { layer = VRUI.Layer };
            DontDestroyOnLoad(root);
            m_card = root.transform;
            m_text = new TextCard(m_card, "card", VRUI.Layer, 3200, 0.0007f);
            root.SetActive(false);
        }

        /// Shows a text card in front of the player. `down` is how far below eye height it sits
        /// (metres, at 0.75 m distance), `tilt` how much it leans back to face the eyes.
        /// keep = stays up while the player swings (the controls card goes once they start fighting).
        /// Text in the game's own fonts: nothing before the game has loaded them (the first seconds of the boot).
        public void Show(string text, float seconds, float down = 0.12f, float tilt = 10f, bool keep = false, float right = 0f)
        {
            m_right = right;
            if (!GameText.Ready || !GameText.AtlasReady)
            {
                Plugin.L.LogInfo("help: not shown, the game's fonts are not loaded yet: " + text.Split('\n')[0]);
                return;
            }
            m_keep = keep;
            if (m_card == null) Create();
            m_card.gameObject.SetActive(true);
            m_text.Set(text);
            m_hideAt = Time.unscaledTime + seconds;
            m_down = down;
            m_tilt = tilt;
            Place();
        }

        /// The card already up: its text and time only, where it is (a note that changes while it shows must not follow the
        /// head); not up: Show.
        public void ShowInPlace(string text, float seconds)
        {
            if (m_card == null || !m_card.gameObject.activeSelf) { Show(text, seconds); return; }
            m_text.Set(text);
            m_hideAt = Time.unscaledTime + seconds;
        }

        public void Hide()
        {
            if (m_card != null) m_card.gameObject.SetActive(false);
        }

        float m_right;

        void Place()
        {
            if (VRRig.I == null || m_card == null) return;
            Quaternion yaw = Quaternion.Euler(0f, VRRig.I.HeadWorldRot.eulerAngles.y, 0f);
            m_card.position = VRRig.I.HeadWorldPos + yaw * (new Vector3(m_right, -m_down, 0.75f) * VRRig.I.Scale);
            m_card.rotation = yaw * Quaternion.Euler(m_tilt, 0f, 0f);
        }

        void LateUpdate()
        {
            // first fight of every game session (the game's own "tutorial seen" flag lives in the
            // shared PlayerPrefs, so it cannot tell whether VR controls were ever explained)
            bool fight = CombatVR.Active && GameWatch.InCombatPlay;
            // the controls are on the waiting space's board before the fight now (user 2026-10-11: "the text explaining
            // combat appears when combat starts, so I don't have time to read it before getting attacked"); the card at
            // fight start only with Settings ControlsCard = Fight
            if (Settings.ControlsCard != Settings.FightCard.Fight) { m_wantFightCard = false; fight = false; }
            if (fight && m_fightsShown == 0) m_wantFightCard = true;
            if (m_wantFightCard && fight && m_fightsShown < 3)
            {
                m_wantFightCard = false;
                m_fightsShown++;
                Show(CombatText, 15f);
            }
            if (m_card == null || !m_card.gameObject.activeSelf) return;
            // hide on time, on leaving VR, or once the player starts fighting
            bool fighting = !m_keep && (CombatInput.Swings > 0 || BladeHits.Swings > 0) && Time.unscaledTime > m_hideAt - 12f && CombatVR.Active;
            if (!XRSettings.enabled || Time.unscaledTime > m_hideAt || fighting)
                m_card.gameObject.SetActive(false);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The player's own fight info, `[Combat] FightHud` (the user's pick of 2026-10-18 ~22:00, mockups
    /// captures/hud-options-2026-10-18/option2_board.png and option1_body.png: "Can they both be options?"). Either way the
    /// flat fight HUD that floated in front of the eyes on the head-following panel (the health radial "52/100", the fight
    /// timer bar, the special counter, the companions' bars, the boss bar, the combo counter) is gone in VR: its widgets are
    /// scaled to nothing while a fight runs (Harmony postfixes on their Updates, as CombatHud does for the floating bars).
    ///  * Board (default): a dark wood board on two posts standing in the arena, 2.4-3.8 m from the player and to the side
    ///    (the shield hand's side first), away from every enemy's bearing (at least 45 deg off where they stand), clear of
    ///    walls, on the ground. It shows the health (a bar and "52/100"), the fight timer when the fight has one (its title,
    ///    a bar and "0:18"), the special (the super meter; READY and the stick to click), the artefact, a combo goal, the
    ///    companions. 1.6 m wide, the numbers ~15 cm tall and the bars 8 cm thick, to be read at 2.4-3.8 m in a Quest 2. It is placed when the fight starts (it rises out of the ground there, far beyond the spawn rule's
    ///    0.53 m) and stays put in the world; it never follows the head. When the player ends up far from it (6.5 m) or has
    ///    not looked its way for 6 s, it moves - only while neither the old nor the new place is in view (or the view is
    ///    dark). It turns see-through (the wood gone, the face faint) while an enemy stands behind it or the player is
    ///    within 1.3 m of it.
    ///  * Body: the health gems on the shield glove and the weapon hand's glow when the special is ready (as before), and the
    ///    fight timer as a slim band round the shield hand's wrist that empties (green, amber in its last 5 s); a combo goal
    ///    (CombatObjectiveCombo: its enemies can't die until it is met) as a gold band beside it that fills toward the goal, with
    ///    "Combo 3 / 8" just above it, facing the eyes, while the wrist is looked at.
    internal class FightHud : MonoBehaviour
    {
        public static FightHud I;

        // =================================================================================== the flat HUD's widgets, hidden
        static readonly Dictionary<int, Vector3> s_scale = new Dictionary<int, Vector3>();
        static readonly List<string> s_hiddenList = new List<string>();
        public static int FlatHides;
        static UICombatTimer s_timer;
        static PlayerHealthUI s_healthUi;
        static AccessTools.FieldRef<UICombatTimer, float> s_tStart, s_tDur;
        static AccessTools.FieldRef<UICombatTimer, UILabel> s_tLabel;
        static AccessTools.FieldRef<UICombatTimer, UISprite> s_tGlass;
        static AccessTools.FieldRef<PlayerHealthUI, UISprite> s_heart;
        static AccessTools.FieldRef<CombatHUD, HealthBar[]> s_friendly;
        static AccessTools.FieldRef<HealthBar, Destroyable> s_barDest;
        static AccessTools.FieldRef<Ability, float> s_nextAvail;

        public static void Apply(Harmony h)
        {
            foreach (string t in new[] { "PlayerRadialStatUI", "PlayerPoisonUI", "HealthBar", "EnemyHealthBar", "BossHealthBar", "UICombatTimer", "CombatHUDCombo", "CombatHUDBanner" })
            {
                var m = AccessTools.Method(AccessTools.TypeByName(t), "Update");
                if (m == null) { Plugin.L.LogWarning("FightHud: missing " + t + ".Update"); continue; }
                h.Patch(m, postfix: new HarmonyMethod(typeof(FightHud), nameof(FlatHide)));
            }
            try
            {
                s_tStart = AccessTools.FieldRefAccess<UICombatTimer, float>("m_startTime");
                s_tDur = AccessTools.FieldRefAccess<UICombatTimer, float>("m_duration");
                s_tLabel = AccessTools.FieldRefAccess<UICombatTimer, UILabel>("m_label");
                s_tGlass = AccessTools.FieldRefAccess<UICombatTimer, UISprite>("m_hourGlass");
                s_heart = AccessTools.FieldRefAccess<PlayerHealthUI, UISprite>("m_heartFill");
                s_friendly = AccessTools.FieldRefAccess<CombatHUD, HealthBar[]>("m_friendlyHealthBars");
                s_barDest = AccessTools.FieldRefAccess<HealthBar, Destroyable>("m_destroyable");
                s_nextAvail = AccessTools.FieldRefAccess<Ability, float>("m_nextAvailableTime");
            }
            catch (Exception e) { Plugin.L.LogWarning("FightHud: field access: " + e.Message); }
        }

        static void FlatHide(MonoBehaviour __instance)
        {
            try
            {
                if (__instance is UICombatTimer) s_timer = (UICombatTimer)__instance;
                else if (__instance is PlayerHealthUI) s_healthUi = (PlayerHealthUI)__instance;
                Transform t = __instance.transform;
                int id = t.GetInstanceID();
                if (XRSettings.enabled && CombatVR.Active)
                {
                    if (!s_scale.ContainsKey(id))
                    {
                        s_scale[id] = t.localScale;
                        FlatHides++;
                        if (s_hiddenList.Count < 40) s_hiddenList.Add(__instance.GetType().Name);
                        Plugin.L.LogInfo("FightHud: the flat fight HUD's " + __instance.GetType().Name + " hidden in VR ('" + VRUI.Path(t) + "')");
                    }
                    if (t.localScale != Vector3.zero) t.localScale = Vector3.zero;
                }
                else
                {
                    Vector3 s;
                    if (s_scale.TryGetValue(id, out s)) { t.localScale = s; s_scale.Remove(id); }
                }
            }
            catch (Exception) { }
        }

        /// The flat HUD's widgets hidden right now (scaled to nothing).
        public static int FlatHiddenNow => s_scale.Count;

        /// A flat HUD part's own size while FightHud hides it (HudMirror shows the copy at it).
        public static bool OriginalScale(Transform t, out Vector3 scale) => s_scale.TryGetValue(t.GetInstanceID(), out scale);

        // =================================================================================== what the HUD shows
        public struct Info
        {
            public bool Ok;
            public float Hp, HpMax;
            public bool Timer;
            public float Left, Duration;
            public string TimerText;
            public bool Special, SpecialReady, Meter;
            public float SpecialShare;
            public int Hits, HitsNeeded;
            public bool Artefact, ArtReady, ArtCooling;
            public int ArtLeft;
            public float ArtCool;
            public int Combo, ComboNeeded;
        }

        public static int ComboSeam, ComboSeamCount = -1;
        static Info s_live;
        static bool s_hasLive;

        public static Info Read()
        {
            // Physical mode, paused (the pause menu, the VR settings page) or after the fight (Level_Out): the last live readout
            // (the HUD audit of 2026-10-19, item 6: the special row turned into the game's own hit counter "2/8" and its own
            // READY while the super meter's rules were off, and back to "73%" on resume)
            if (Physical.On && !Physical.Live && s_hasLive) return s_live;
            var i = new Info();
            PlayerController pc = PlayerController.Instance;
            if (pc == null) return i;
            try
            {
                Destroyable d = pc.GetComponent<Destroyable>();
                if (d == null) return i;
                i.Ok = true;
                i.Hp = Mathf.Max(0f, d.CurrentHealth);
                i.HpMax = Mathf.Max(1f, d.MaxHealth.Value);
                if (s_timer != null && s_tStart != null && s_tDur != null)
                {
                    float st = s_tStart(s_timer), du = s_tDur(s_timer);
                    if (st > 0f && du > 0f)
                    {
                        i.Timer = true;
                        i.Duration = du;
                        i.Left = Mathf.Clamp(st + du - Time.time, 0f, du);
                        UILabel l = s_tLabel != null ? s_tLabel(s_timer) : null;
                        i.TimerText = l != null ? l.text : "";
                    }
                }
                Ability wa = pc.Inventory != null && pc.Inventory.Weapon != null ? pc.Inventory.Weapon.Ability : null;
                if (wa != null)
                {
                    i.Special = true;
                    i.SpecialReady = Abilities.Ready(true) || (!Physical.On && wa.IsComboValid && wa.CanAfford && wa.AreChargesRemaining && !wa.IsCoolingDown);
                    if (Physical.On && wa.AttackHitsRequired > 0) { i.Meter = true; i.SpecialShare = Mathf.Clamp01(SuperMeter.Charge / SuperMeter.Max); }
                    else
                    {
                        i.HitsNeeded = Mathf.Max(0, wa.AttackHitsRequired);
                        i.Hits = Mathf.Clamp(wa.CurrentAttackHitCounter - (wa.NextAttackHitCounterTarget - wa.AttackHitsRequired), 0, Mathf.Max(1, i.HitsNeeded));
                        i.SpecialShare = i.HitsNeeded > 0 ? i.Hits / (float)i.HitsNeeded : (i.SpecialReady ? 1f : 0f);
                    }
                }
                Ability aa = pc.Inventory != null && pc.Inventory.Artifact != null ? pc.Inventory.Artifact.Ability : null;
                if (aa != null)
                {
                    i.Artefact = true;
                    i.ArtReady = Abilities.Ready(false) || (!Physical.On && aa.CanAfford && aa.AreChargesRemaining && !aa.IsCoolingDown);
                    i.ArtCooling = aa.IsCoolingDown;
                    i.ArtCool = aa.IsCoolingDown && s_nextAvail != null ? Mathf.Max(0f, s_nextAvail(aa) - Time.time) : 0f;
                    try { i.ArtLeft = aa.CurrentArtifactQuantity; } catch (Exception) { i.ArtLeft = -1; }
                }
                CombatEncounter ce = CombatEncounter.Instance;
                List<CombatObjective> objs = ce != null ? ce.Objectives : null;
                // (a loop, not LINQ: Read runs every frame - the HUD audit of 2026-10-19, item 2)
                for (int k = 0; objs != null && k < objs.Count; k++)
                {
                    var co = objs[k] as CombatObjectiveCombo;
                    if (co == null || co.RequiredCombo <= 0) continue;
                    i.ComboNeeded = co.RequiredCombo;
                    i.Combo = pc.Combat != null ? pc.Combat.ComboCounter : 0;
                    break;
                }
                // `fighthud combo <N> [count]` (tests and pictures): a combo goal of N shown as if the fight had one
                if (i.ComboNeeded <= 0 && ComboSeam > 0) { i.ComboNeeded = ComboSeam; i.Combo = ComboSeamCount >= 0 ? ComboSeamCount : pc.Combat != null ? pc.Combat.ComboCounter : 0; }
            }
            catch (Exception) { }
            if (Physical.Live && i.Ok) { s_live = i; s_hasLive = true; }
            return i;
        }

        /// The companions fighting beside the player (the flat HUD's friendly bars): their health shares and portraits.
        static readonly List<Destroyable> s_friends = new List<Destroyable>();
        static List<Destroyable> Friends()
        {
            s_friends.Clear();
            try
            {
                CombatHUD hud = CombatHUD.Instance;
                HealthBar[] bars = hud != null && s_friendly != null ? s_friendly(hud) : null;
                if (bars == null) return s_friends;
                foreach (HealthBar b in bars)
                {
                    Destroyable d = b != null && s_barDest != null ? s_barDest(b) : null;
                    if (d != null && s_friends.Count < 3) s_friends.Add(d);
                }
            }
            catch (Exception) { }
            return s_friends;
        }

        // =================================================================================== the component
        void Awake()
        {
            I = this;
            Diag.Commands["fighthud?"] = a => Describe();
            // `fighthud place`: place the board again now (tests); `fighthud board x z yaw`: put it there (world, tests)
            Diag.Commands["fighthud"] = a =>
            {
                if (a.Length > 1 && a[1] == "place") { if (m_board != null) { m_board.Placed = false; } return "board will be placed again"; }
                if (a.Length > 1 && a[1] == "sprites") return Sprites(a.Length > 2 ? a[2] : "");
                if (a.Length > 1 && a[1] == "mirror") return MirrorSeam(a.Length > 2 ? a[2] : "");
                if (a.Length > 2 && a[1] == "art")
                {
                    Board.GameArt = a[2] != "old";
                    if (m_board != null) { m_board.Destroy(); m_board = null; }
                    return "the board's look: " + (Board.GameArt ? "the game's art" : "the old drawn one") + " (a new board)";
                }
                // `fighthud timer <seconds> [text...]`: give this fight the game's timer (tests and pictures; no objective comes with it)
                if (a.Length > 2 && a[1] == "timer")
                {
                    if (!Scenarios.SeamsAllowed) return "test mode or the dev profile only";
                    if (s_timer == null)
                        foreach (UICombatTimer t in Resources.FindObjectsOfTypeAll<UICombatTimer>())
                            if (t != null && t.gameObject.scene.IsValid()) { s_timer = t; break; }
                    if (s_timer == null || s_tStart == null) return "no combat timer";
                    float secs = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                    s_tStart(s_timer) = secs > 0f ? Time.time : 0f;
                    s_tDur(s_timer) = secs;
                    UILabel l = s_tLabel != null ? s_tLabel(s_timer) : null;
                    if (l != null) l.text = a.Length > 3 ? string.Join(" ", a, 3, a.Length - 3) : "";
                    return "timer " + secs + " s '" + (l != null ? l.text : "-") + "'";
                }
                // `fighthud combo <N> [count]`: a combo goal of N on the board / the wrist (no objective comes with it; 0 = off);
                // count: the combo shown (else the hero's own)
                if (a.Length > 2 && a[1] == "combo")
                {
                    if (!Scenarios.SeamsAllowed) return "test mode or the dev profile only";
                    ComboSeam = Mathf.Max(0, int.Parse(a[2]));
                    ComboSeamCount = a.Length > 3 ? int.Parse(a[3]) : -1;
                    return "combo goal " + ComboSeam + (ComboSeamCount >= 0 ? " shown at " + ComboSeamCount : "");
                }
                return Describe();
            };
            Diag.Commands["vrhud"] = a =>
            {
                if (a.Length < 2) return "hud " + CombatSettings.FightHud;
                if (a[1] == "setting") CombatSettings.OverrideFightHud(null);
                else CombatSettings.OverrideFightHud(a[1].StartsWith("bo", StringComparison.OrdinalIgnoreCase) && a[1].Length > 2 && a[1][2] == 'd' ? CombatSettings.FightHudStyle.Body : CombatSettings.FightHudStyle.Board);
                return "hud " + CombatSettings.FightHud;
            };
            Diag.Commands["vrstatus"] = a =>
            {
                if (a.Length < 2) return "status " + CombatSettings.EnemyStatus;
                if (a[1] == "setting") CombatSettings.OverrideEnemyStatus(null);
                else CombatSettings.OverrideEnemyStatus(a[1].StartsWith("r", StringComparison.OrdinalIgnoreCase) ? CombatSettings.EnemyStatusStyle.Rings : CombatSettings.EnemyStatusStyle.Shell);
                return "status " + CombatSettings.EnemyStatus;
            };
            Diag.StatusExtras["fighthud"] = () => Diag.Q(Short());
        }

        static string Sprites(string filter)
        {
            UIAtlas at = GameText.Atlas;
            if (at == null) return "no atlas";
            var sb = new StringBuilder();
            foreach (UISpriteData d in at.spriteList)
                if (string.IsNullOrEmpty(filter) || d.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(d.name).Append(' ');
            Plugin.L.LogInfo("FightHud: atlas sprites (" + filter + "): " + sb);
            return sb.Length > 900 ? sb.ToString(0, 900) + "..." : sb.ToString();
        }

        // `fighthud mirror [off]` (P4-17, tests): copies of the game's flat HUD dials (HudMirror) on a test panel 1.2 m in front
        // of the eyes, each listed in the log (its widgets: sprites, labels, pictures)
        static GameObject s_testPanel;
        static readonly List<HudMirror> s_testMirrors = new List<HudMirror>();

        static string MirrorSeam(string op)
        {
            foreach (HudMirror m in s_testMirrors) m.Destroy();
            s_testMirrors.Clear();
            if (s_testPanel != null) { UnityEngine.Object.Destroy(s_testPanel); s_testPanel = null; }
            if (op == "off") return "test mirrors off";
            VRRig rig = VRRig.I;
            if (rig == null) return "no rig";
            s_testPanel = new GameObject("HoF2VR_MirrorTest") { layer = 0 };
            GameText.Panel(s_testPanel, 3170);
            Vector3 eye = rig.HeadWorldPos;
            Quaternion yaw = Quaternion.Euler(0f, rig.HeadWorldRot.eulerAngles.y, 0f);
            s_testPanel.transform.SetPositionAndRotation(eye + yaw * new Vector3(0f, -0.1f, 1.2f) * rig.Scale, yaw);
            s_testPanel.transform.localScale = Vector3.one * 0.0012f * rig.Scale;
            var sources = new List<MonoBehaviour>();
            foreach (Type t in new[] { typeof(PlayerHealthUI), typeof(PlayerWeaponUI), typeof(PlayerArtifactUI), typeof(PlayerBashUI), typeof(UICombatTimer) })
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(t))
                {
                    var mb = o as MonoBehaviour;
                    if (mb != null && mb.gameObject.scene.IsValid() && mb.gameObject.activeInHierarchy) { sources.Add(mb); break; }
                }
            float x = -600f;
            var sb = new StringBuilder();
            foreach (MonoBehaviour src in sources)
            {
                HudMirror m = HudMirror.Of(src, s_testPanel.transform);
                if (m == null) continue;
                m.Sync();
                m.Root.localPosition = new Vector3(x, 0f, 0f);
                x += 300f;
                s_testMirrors.Add(m);
                Bounds b = m.LocalBounds();
                Plugin.L.LogInfo("FightHud mirror " + src.GetType().Name + " ('" + VRUI.Path(src.transform) + "', bounds " + Diag.V(b.center) + " size " + Diag.V(b.size) + "): " + m.Describe());
                sb.Append(src.GetType().Name).Append(' ').Append(m.Widgets).Append(" widgets; ");
            }
            return sources.Count + " sources: " + sb;
        }

        /// The test mirrors follow their originals (from LateUpdate).
        static void SyncTestMirrors()
        {
            for (int i = 0; i < s_testMirrors.Count; i++) if (s_testMirrors[i].Alive) s_testMirrors[i].Sync();
        }

        Board m_board;
        bool m_wasFight;
        public static int BoardsPlaced, BoardMoves, MovesInView, Rises, StyleChanges;
        public static string LastMove = "-";
        CombatSettings.FightHudStyle m_style;

        void LateUpdate()
        {
            if (s_testMirrors.Count > 0) SyncTestMirrors();
            bool fight = XRSettings.enabled && CombatVR.Active && VRRig.I != null && PlayerController.Instance != null;
            if (!fight)
            {
                if (m_board != null) { m_board.Destroy(); m_board = null; }
                m_wasFight = false;
                s_hasLive = false;
                ComboSeam = 0;
                ComboSeamCount = -1;
                HideWristIfIdle();
                return;
            }
            CombatSettings.FightHudStyle style = CombatSettings.FightHud;
            if (m_wasFight && style != m_style) StyleChanges++;
            m_style = style;
            m_wasFight = true;
            HideWristIfIdle();
            if (style != CombatSettings.FightHudStyle.Board)
            {
                if (m_board != null) { m_board.Destroy(); m_board = null; }
                return;
            }
            // the board: once the fight is under way (the hero has control)
            if (m_board == null)
            {
                if (GameWatch.State != "GameState_Level_Play" && GameWatch.State != "GameState_Level_Pause") return;
                if (!GameText.Ready || !GameText.AtlasReady) return;
                m_board = new Board();
            }
            m_board.Tick(Read());
        }

        // =================================================================================== the board
        sealed class Board
        {
            public const float W = 1.60f, PostW = 0.09f, Depth = 0.04f, Px = 0.001f;
            public static float Near = 2.4f, Far = 3.8f, Ideal = 3.0f, MoveFar = 6.5f, AwayFor = 6f, GhostNear = 1.3f, ViewHalf = 58f;
            public bool Placed;
            public Vector3 Pos;        // the face's centre
            public Quaternion Rot;     // +z: from the player toward the board (the face looks back along -z)
            public float H = 0.62f, PlacedAt, AwaySince = -1f, RetryAt, Alpha, RiseT = 1f, Ground;
            public bool Ghost;
            public string Why = "-";
            public float GazeAngle, Dist, EnemyAngle;
            readonly GameObject m_root;
            readonly Transform m_wood, m_postL, m_postR, m_face, m_front, m_bars;
            readonly Mesh m_barMesh;
            readonly Premonition.Builder m_b = new Premonition.Builder();
            readonly UIPanel m_backPanel, m_frontPanel;
            readonly UISprite m_frame, m_divider, m_heart, m_glass, m_glyph, m_glyphA;
            readonly UITexture m_planks;
            readonly UILabel m_title, m_hp, m_hpMax, m_time, m_special, m_art, m_combo;
            readonly List<UILabel> m_friendNames = new List<UILabel>();
            readonly Material m_woodMat;
            int m_built = int.MinValue;
            // ---- P4-17 (playtest 4, the user: "The graphics just looked really bad ... Can we just use assets already in the game?"):
            // the board's face from the game's own art - the flat HUD's dials copied whole (HudMirror: the heart dial, the weapon's
            // dial with its picture, the artefact's), the game's bar sprites (its companions' bar frame and fill), its dialogue box's
            // dark parchment as the face, its gold border and fonts - instead of the drawn wood grain, pill bars, star and heart.
            // `fighthud art old|game` (the old look for before / after pictures; a new board is made).
            public static bool GameArt = true;
            readonly bool m_gameArt;
            HudMirror m_mHealth, m_mWeapon, m_mArt;
            float m_nextMirror;
            UIAtlas m_barAtlas;
            readonly List<UISprite> m_barSprites = new List<UISprite>();
            int m_barsUsed;
            public static float DialH = 200f, ArtDialH = 170f;
            static Texture2D s_grain;
            Vector3 m_lastPos;
            bool m_lastInView, m_hasLast, m_readyShown;
            int m_fails;

            public Board()
            {
                m_root = new GameObject("HoF2VR_FightBoard") { layer = 0 };
                m_gameArt = GameArt;
                m_woodMat = m_gameArt ? DarkMaterial() : WoodMaterial();
                m_wood = Box("wood", m_root.transform, m_woodMat);
                m_postL = Box("postL", m_root.transform, m_woodMat);
                m_postR = Box("postR", m_root.transform, m_woodMat);
                var face = new GameObject("face") { layer = 0 };
                face.transform.SetParent(m_root.transform, false);
                face.transform.localScale = Vector3.one * Px;
                m_face = face.transform;
                m_backPanel = GameText.Panel(face, 3140);
                UIAtlas at = GameText.Atlas;
                Color gold = new Color(0.86f, 0.67f, 0.36f, 1f);
                // the face: the wood's grain, unlit (a warm dark wood in any arena's light, as the game's cards), under the gold frame
                var pg = new GameObject("planks") { layer = 0 };
                pg.transform.SetParent(m_face, false);
                pg.transform.localPosition = new Vector3(0f, 0f, 1f);
                m_planks = pg.AddComponent<UITexture>();
                Texture parchment = m_gameArt ? DialogueParchment() : null;
                m_planks.mainTexture = parchment != null ? parchment : m_gameArt && GameText.Atlas != null ? null : (Texture)Grain();
                m_planks.color = parchment != null ? s_parchmentTint : new Color(0.62f, 0.5f, 0.42f, 1f);
                // (no parchment found: the game's own dark card, as the VR settings page's)
                if (m_gameArt && parchment == null && GameText.Atlas != null)
                    GameText.Sprite(m_face, "card", GameText.Atlas, "gui_black", new Color(0.05f, 0.04f, 0.03f, 0.96f), -3, 1596, 600, UIBasicSprite.Type.Simple).transform.localPosition = new Vector3(0f, 0f, 1f);
                m_planks.depth = -2;
                m_planks.width = 1600;
                m_planks.height = 600;
                m_frame = GameText.Sprite(m_face, "frame", at, "gui_border", new Color(1f, 0.83f, 0.52f, 1f), 0, 1580, 600);
                m_divider = GameText.Sprite(m_face, "divider", at, "gui_divider_full", gold, 2, 1000, 26, UIBasicSprite.Type.Simple);
                m_title = GameText.Label(m_face, "title", GameText.Stylised, 104, Color.white, 4);
                GameText.Gild(m_title, 1.4f);
                var front = new GameObject("front") { layer = 0 };
                front.transform.SetParent(m_face, false);
                front.transform.localPosition = new Vector3(0f, 0f, -3f);
                m_front = front.transform;
                m_frontPanel = GameText.Panel(front, 3170);
                // readable from 2.4-3.8 m in a Quest 2 (the coordinator's review of 2026-10-19: the first board's 128 px numbers
                // were too small): the numbers ~15 cm tall (2.2 deg at 3.8 m), the bars 8 cm thick
                m_hp = Value(m_front, "hp", 220);
                m_hpMax = Value(m_front, "hpMax", 104);
                m_hpMax.color = new Color(0.9f, 0.82f, 0.68f, 1f);
                m_time = Value(m_front, "time", 220);
                m_special = Value(m_front, "special", 150);
                m_art = Value(m_front, "artefact", 92);
                m_combo = Value(m_front, "combo", 96);
                // the game's own HUD icons where it has them: its heart and its hourglass (game art: the heart dial instead)
                m_heart = m_gameArt ? null : Icon(m_front, "heart", s_healthUi != null && s_heart != null ? s_heart(s_healthUi) : null, 150);
                m_glass = Icon(m_front, "glass", s_timer != null && s_tGlass != null ? s_tGlass(s_timer) : null, 138);
                m_glyph = Glyph(m_front, "glyph", !Settings.LeftHanded);
                m_glyphA = Glyph(m_front, "glyphA", Settings.LeftHanded);
                var bars = new GameObject("bars") { layer = 0 };
                bars.transform.SetParent(m_face, false);
                bars.transform.localPosition = new Vector3(0f, 0f, -1.5f);
                m_bars = bars.transform;
                m_barMesh = new Mesh { name = "HoF2VR_FightBoardBars" };
                m_barMesh.MarkDynamic();
                bars.AddComponent<MeshFilter>().sharedMesh = m_barMesh;
                var mr = bars.AddComponent<MeshRenderer>();
                mr.sharedMaterial = Premonition.Alpha();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                m_root.SetActive(false);
            }

            static UILabel Value(Transform p, string name, int size)
            {
                UILabel l = GameText.Label(p, name, GameText.Normal, size, GameText.Parchment, 6, UIWidget.Pivot.Left, NGUIText.Alignment.Left);
                l.effectStyle = UILabel.Effect.Outline8;
                l.effectColor = new Color(0.06f, 0.035f, 0.015f, 0.9f);
                l.effectDistance = new Vector2(2f, 2f);
                return l;
            }

            static UISprite Icon(Transform p, string name, UISprite from, int size)
            {
                if (from == null || from.atlas == null || string.IsNullOrEmpty(from.spriteName)) return null;
                UISprite s = GameText.Sprite(p, name, from.atlas, from.spriteName, Color.white, 5, size, size, UIBasicSprite.Type.Simple);
                s.keepAspectRatio = UIWidget.AspectRatioSource.BasedOnHeight;
                return s;
            }

            static UISprite Glyph(Transform p, string name, bool right)
            {
                try
                {
                    UIAtlas atlas = QuestPrompts.PlainAtlas();
                    string sn = QuestPrompts.SpriteName(UIManager.Instance, right ? InControl.InputControlType.RightStickButton : InControl.InputControlType.LeftStickButton);
                    if (atlas == null || string.IsNullOrEmpty(sn) || atlas.GetSprite(sn) == null) return null;
                    UISprite s = GameText.Sprite(p, name, atlas, sn, Color.white, 7, 124, 124, UIBasicSprite.Type.Simple);
                    s.gameObject.SetActive(false);
                    return s;
                }
                catch (Exception) { return null; }
            }

            // ---- P4-17: the game's art
            static Texture s_parchment;
            static Color s_parchmentTint = Color.white;
            static bool s_parchmentLooked;
            static readonly AccessTools.FieldRef<PauseMenu, Dialogue> s_quitPrefab = AccessTools.FieldRefAccess<PauseMenu, Dialogue>("m_quitDialoguePrefab");

            /// The game's question box's dark parchment (its pause menu's quit question: DialogueBacking_Texture), or null.
            static Texture DialogueParchment()
            {
                if (s_parchmentLooked) return s_parchment;
                s_parchmentLooked = true;
                try
                {
                    // (the question's box is a prefab of its own, UIDialogue_*: its backing texture, loaded with the pause menu)
                    foreach (UITexture t in Resources.FindObjectsOfTypeAll<UITexture>())
                        if (t != null && t.mainTexture != null && t.name == "DialogueBacking_Texture")
                        {
                            // (a pale texture the game tints: its box's own colour, opaque)
                            s_parchment = t.mainTexture;
                            s_parchmentTint = new Color(t.color.r, t.color.g, t.color.b, 1f);
                            if (s_parchmentTint.grayscale > 0.4f) s_parchmentTint = new Color(0.17f, 0.155f, 0.13f, 1f);   // (the box as it shows: dark)
                            break;
                        }
                }
                catch (Exception) { }
                Plugin.L.LogInfo("FightHud: the board's face " + (s_parchment != null ? "is the game's question box parchment '" + s_parchment.name + "' (tint " + s_parchmentTint + ")" : "keeps the drawn grain (no parchment found)"));
                return s_parchment;
            }

            static Material DarkMaterial()
            {
                Shader sh = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
                var m = new Material(sh) { name = "HoF2VR_FightBoardDark", color = new Color(0.07f, 0.055f, 0.045f, 1f) };
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.08f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                return m;
            }

            /// The flat HUD's dials copied onto the board (once their originals exist), their own small numbers and blurred
            /// screen backings left out (the board's big numbers say it).
            void EnsureMirrors()
            {
                if (!m_gameArt || Time.unscaledTime < m_nextMirror) return;
                m_nextMirror = Time.unscaledTime + 1f;
                if (m_mHealth == null && s_healthUi != null)
                {
                    m_mHealth = HudMirror.Of(s_healthUi, m_front);
                    if (m_mHealth != null)
                    {
                        foreach (string n in new[] { "CurrentHealth", "MaxHealth", "Line", "BlurredBacking (1)", "BlurredBacking", "Sprite_PoisonBar", "Sprite_PoisonBarFill_Glow",
                                                     "Label_PoisonStacks", "Sprite_PoisonBarFill", "Sprite_PoisonBarFill_Chunk" }) m_mHealth.Keep(n, true);
                        UISprite bar = m_mHealth.Find<UISprite>("Sprite_PoisonBar");
                        if (bar != null) m_barAtlas = bar.atlas;
                    }
                }
                if (m_mWeapon == null && PlayerWeaponUI.Instance != null)
                {
                    m_mWeapon = HudMirror.Of(PlayerWeaponUI.Instance, m_front);
                    if (m_mWeapon != null)
                    {
                        foreach (string n in new[] { "CurrentCombo", "TargetCombo", "Line", "BlurredBacking", "Sprite_Line", "HoF2VR_AbilityButton" }) m_mWeapon.Keep(n, true);
                        // Physical: its ring is the super meter, and the dial and the weapon light up when the special is ready (the
                        // game dims them by its own hit counter, which Physical does not use)
                        if (Physical.On) { m_mWeapon.Keep("Foreground", false); m_mWeapon.Keep("Overlay", false); m_mWeapon.KeepTextures(); }
                    }
                }
                if (m_mArt == null)
                {
                    PlayerArtifactUI au = UnityEngine.Object.FindObjectOfType<PlayerArtifactUI>();
                    if (au != null)
                    {
                        m_mArt = HudMirror.Of(au, m_front);
                        if (m_mArt != null) foreach (string n in new[] { "BlurredBacking", "Sprite_Line" }) m_mArt.Keep(n, true);
                    }
                }
                if (m_mHealth != null || m_mWeapon != null || m_mArt != null) m_built = int.MinValue;   // lay out again with them
            }

            // Candidate 11's pictures: the artefact dial's "∞" (the game's PlayerArtifactUI.m_infiniteSprite, left of the dial) still stood
            // across the board's left frame after the dial moved 45 px right. The dial now moves right until its frame and the ∞ start
            // at InnerLeft (the heart's and the weapon's dials' left edge); the room is kept whether the ∞ shows or not (a steady row).
            public static float InnerLeft = -770f;
            public static string ArtFit = "-";

            /// The artefact copy moved right until its frame ("Overlay") and its ∞ lie right of InnerLeft; returns its right edge.
            static float KeepInside(HudMirror m)
            {
                float k = m.Root.localScale.x;
                Bounds frame = m.LocalBounds("Overlay");
                float minX = frame.min.x, maxX = frame.max.x, infL = float.NaN;
                try
                {
                    UISprite inf = Traverse.Create(m.Source).Field("m_infiniteSprite").GetValue<UISprite>();
                    Bounds b = m.BoundsOf(m.CopyOf(inf));
                    if (b.size.x > 0.5f) { infL = b.min.x; minX = Mathf.Min(minX, b.min.x); maxX = Mathf.Max(maxX, b.max.x); }
                }
                catch (Exception) { }
                Vector3 p = m.Root.localPosition;
                float left = p.x + minX * k, shift = Mathf.Max(0f, InnerLeft - left);
                p.x += shift;
                m.Root.localPosition = p;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                ArtFit = "art dial left " + (left + shift).ToString("0", inv) + " (was " + left.ToString("0", inv) + ", ∞ " +
                         (float.IsNaN(infL) ? "-" : (p.x - shift + infL * k).ToString("0", inv)) + ") right " + (p.x + maxX * k).ToString("0", inv) + " shift " + shift.ToString("0", inv);
                return p.x + maxX * k;
            }

            /// A copy, its art's middle at (x, y) on the face and its art `h` tall.
            static void PlaceDial(HudMirror m, float x, float y, float h, bool on)
            {
                if (m == null) return;
                if (m.Root.gameObject.activeSelf != on) m.Root.gameObject.SetActive(on);
                if (!on) return;
                m.Root.localScale = Vector3.one;
                Bounds b = m.LocalBounds("Overlay");   // (the dial's frame: its charged pulses grow and shrink)
                float k = b.size.y > 1f ? h / b.size.y : 1f;
                m.Root.localScale = new Vector3(k, k, 1f);
                m.Root.localPosition = new Vector3(x - b.center.x * k, y - b.center.y * k, -1f);
            }

            /// A bar from the game's own bar sprites (its companions' health bar: the frame and its fill), or false.
            bool GameBar(float x0, float x1, float y, float h, float share, Color c, float a)
            {
                if (m_barAtlas == null) return false;
                while (m_barSprites.Count < m_barsUsed + 2)
                {
                    bool fill = m_barSprites.Count % 2 == 1;
                    UISprite sp = GameText.Sprite(m_front, fill ? "barFill" : "barFrame", m_barAtlas, fill ? "gui_combat_companion_health_mask" : "gui_combat_companion_health_outline",
                                                  Color.white, fill ? 3 : 4, 100, 20, fill ? UIBasicSprite.Type.Filled : UIBasicSprite.Type.Sliced);
                    if (fill) { sp.fillDirection = UIBasicSprite.FillDirection.Horizontal; sp.invert = false; }
                    m_barSprites.Add(sp);
                }
                UISprite frame = m_barSprites[m_barsUsed], f = m_barSprites[m_barsUsed + 1];
                m_barsUsed += 2;
                int w = Mathf.RoundToInt(x1 - x0), hh = Mathf.RoundToInt(h);
                SizeBar(frame, w, hh, (x0 + x1) * 0.5f, y);
                SizeBar(f, w, hh, (x0 + x1) * 0.5f, y);
                frame.color = new Color(1f, 0.92f, 0.8f, 0.95f * a);
                f.color = new Color(c.r, c.g, c.b, a);
                float sh = Mathf.Clamp01(share);
                if (Mathf.Abs(f.fillAmount - sh) > 0.001f) f.fillAmount = sh;
                return true;
            }

            static void SizeBar(UISprite sp, int w, int h, float x, float y)
            {
                if (!sp.gameObject.activeSelf) sp.gameObject.SetActive(true);
                if (sp.width != w) sp.width = w;
                if (sp.height != h) sp.height = h;
                Vector3 p = new Vector3(x, y, 0f);
                if (sp.transform.localPosition != p) sp.transform.localPosition = p;
            }

            void EndBars()
            {
                for (int i = m_barsUsed; i < m_barSprites.Count; i++) if (m_barSprites[i].gameObject.activeSelf) m_barSprites[i].gameObject.SetActive(false);
                m_barsUsed = 0;
            }

            // ---- the wood: a box with a dark grain, lit by the arena
            static Texture2D Grain()
            {
                if (s_grain == null)
                {
                    const int w = 128, h = 256;
                    s_grain = new Texture2D(w, h, TextureFormat.RGB24, true) { name = "HoF2VR_Grain", wrapMode = TextureWrapMode.Repeat };
                    var px = new Color[w * h];
                    var rng = new System.Random(7);
                    float[] wob = new float[h];
                    for (int y = 0; y < h; y++) wob[y] = Mathf.Sin(y * 0.05f) * 3f + Mathf.Sin(y * 0.17f + 1f) * 1.2f;
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            float gx = x + wob[y];
                            float g = 0.5f + 0.5f * Mathf.Sin(gx * 0.42f + Mathf.Sin(gx * 0.07f) * 2f);
                            float n = (float)rng.NextDouble() * 0.06f;
                            float k = 0.78f + 0.22f * g * g + n;
                            px[y * w + x] = new Color(0.46f * k, 0.28f * k, 0.14f * k);
                        }
                    s_grain.SetPixels(px);
                    s_grain.Apply(true);
                }
                return s_grain;
            }

            static Material WoodMaterial()
            {
                Shader sh = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
                var m = new Material(sh) { name = "HoF2VR_FightBoardWood" };
                m.mainTexture = Grain();
                m.color = Color.white;
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.12f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                return m;
            }

            static Mesh s_box;
            static Transform Box(string name, Transform parent, Material mat)
            {
                if (s_box == null)
                {
                    GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    s_box = tmp.GetComponent<MeshFilter>().sharedMesh;
                    UnityEngine.Object.DestroyImmediate(tmp);
                }
                var go = new GameObject(name) { layer = 0 };
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = s_box;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
                return go.transform;
            }

            public void Destroy()
            {
                if (m_root != null) UnityEngine.Object.Destroy(m_root);
                UnityEngine.Object.Destroy(m_barMesh);
                UnityEngine.Object.Destroy(m_woodMat);
            }

            // ---- per frame
            public void Tick(Info info)
            {
                VRRig rig = VRRig.I;
                Vector3 eye = rig.HeadWorldPos;
                Quaternion head = rig.HeadWorldRot;
                float now = Time.unscaledTime;
                EnsureMirrors();
                Layout(info);
                if (!Placed)
                {
                    string why;
                    if (now < RetryAt) return;
                    // a cramped arena: after a few tries the rules relax (nearer, enemies closer, no room check)
                    if (!Choose(eye, head, true, out why, m_fails >= 4)) { Why = why; RetryAt = now + 0.5f; m_fails++; return; }
                    m_fails = 0;
                    RetryAt = 0f;
                    Why = why;
                    m_hasLast = false;
                    Placed = true;
                    PlacedAt = now;
                    RiseT = 0f;
                    Rises++;
                    BoardsPlaced++;
                    Plugin.L.LogInfo("FightHud: the board stands at " + Diag.V(Pos) + ", " + Vector3.Distance(Flat(eye), Flat(Pos)).ToString("0.0") + " m away (" + Why + ")");
                }
                Dist = Vector3.Distance(Flat(eye), Flat(Pos));
                GazeAngle = Vector3.Angle(head * Vector3.forward, Pos - eye);
                bool inView = InView(eye, head, Pos);
                // the check behind the rule: a board that changed place while it was in view (must stay 0)
                if (m_hasLast && (Pos - m_lastPos).sqrMagnitude > 1e-6f && (m_lastInView || inView) && !VRFade.IsBlack) { MovesInView++; Plugin.L.LogWarning("FightHud: the board moved in view"); }
                m_lastPos = Pos;
                m_lastInView = inView;
                m_hasLast = true;
                // moving it: far away, or not looked at for a while - only unseen
                if (Dist > MoveFar || !inView)
                {
                    if (AwaySince < 0f) AwaySince = now;
                }
                else AwaySince = -1f;
                bool due = Dist > MoveFar || (AwaySince >= 0f && now - AwaySince > AwayFor && Dist > Near + 1.2f) || (AwaySince >= 0f && now - AwaySince > AwayFor * 2f);
                if (due && now >= RetryAt && (!inView || VRFade.IsBlack))
                {
                    RetryAt = now + 0.5f;
                    Vector3 oldPos = Pos;
                    Quaternion oldRot = Rot;
                    float oldGround = Ground;
                    string why;
                    if (Choose(eye, head, false, out why))
                    {
                        bool seen = !VRFade.IsBlack && (InView(eye, head, oldPos) || InView(eye, head, Pos));
                        if (seen) { Pos = oldPos; Rot = oldRot; Ground = oldGround; }
                        else
                        {
                            BoardMoves++;
                            LastMove = (Dist > MoveFar ? "far " : "away ") + Dist.ToString("0.0") + " m -> " + Diag.V(Pos) + " (" + why + ")";
                            Plugin.L.LogInfo("FightHud: the board moved where it can't be seen moving: " + LastMove);
                            AwaySince = -1f;
                        }
                    }
                }
                // see-through while an enemy stands behind it or the player is right at it
                Ghost = Dist < GhostNear || EnemyBehind(eye);
                RiseT = Mathf.Min(1f, RiseT + Time.unscaledDeltaTime / 0.45f);
                float fade = Ghost ? 0.28f : 1f;
                Alpha = Mathf.MoveTowards(Alpha, fade, Time.unscaledDeltaTime / 0.25f);
                Put();
                Draw(info);
            }

            static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

            /// In view: within the headset's view (about 58 deg off the gaze to the board's centre, plus its own half size).
            public bool InView(Vector3 eye, Quaternion head, Vector3 at)
            {
                Vector3 d = at - eye;
                float dist = Mathf.Max(0.3f, d.magnitude);
                float half = Mathf.Atan2(W * 0.5f, dist) * Mathf.Rad2Deg;
                return Vector3.Angle(head * Vector3.forward, d) < ViewHalf + half;
            }

            bool EnemyBehind(Vector3 eye)
            {
                try
                {
                    Targetable me = PlayerController.Instance.GetComponent<Targetable>();
                    BetterList<Targetable> all = me.EnumerateWithinRadius(30f, me.GetEnemyTeams());
                    Vector3 n = Rot * Vector3.forward;
                    var plane = new Plane(n, Pos);
                    for (int i = 0; i < all.size; i++)
                    {
                        Targetable t = all[i];
                        if (t == null) continue;
                        Destroyable d = t.GetComponent<Destroyable>();
                        if (d == null || d.IsDead) continue;
                        Vector3 chest = t.transform.position + Vector3.up * 1.1f;
                        if ((chest - eye).sqrMagnitude < (Pos - eye).sqrMagnitude) continue;
                        Ray r = new Ray(eye, (chest - eye).normalized);
                        float e;
                        if (!plane.Raycast(r, out e)) continue;
                        Vector3 hit = r.GetPoint(e), local = Quaternion.Inverse(Rot) * (hit - Pos);
                        // the board's rectangle grown by the enemy's half width (0.35 m)
                        if (Mathf.Abs(local.x) < W * 0.5f + 0.35f && Mathf.Abs(local.y) < H * 0.5f + 0.6f) return true;
                    }
                }
                catch (Exception) { }
                return false;
            }

            static int s_solid = -1;
            static int Solid
            {
                get
                {
                    if (s_solid != -1) return s_solid;
                    int m = ~0;
                    foreach (int l in new[] { 8, 13, 14, 15, 16, 17, 21, 22, 23, 26, 30, 31, Limbo.Layer }) m &= ~(1 << l);
                    return s_solid = m;
                }
            }
            static int s_ground = -1;
            static int GroundMask => s_ground >= 0 ? s_ground : (s_ground = 1 | (LayerMask.NameToLayer("Ground") >= 0 ? 1 << LayerMask.NameToLayer("Ground") : 0));

            /// The best free spot round the player: to the side (the shield hand's first), 2.4-3.8 m away, at least 45 deg
            /// off every enemy's bearing and 1.6 m from any enemy, clear of walls (nothing between the eyes and the board, nothing
            /// in its volume), on ground no more than 0.7 m above or below the player's. A move (initial false) also needs the
            /// new spot out of view.
            bool Choose(Vector3 eye, Quaternion head, bool initial, out string why, bool relaxed = false)
            {
                why = "no free spot";
                Vector3 feet = VRRig.I.FloorBelowEye(eye);
                var foes = new List<Vector3>();
                try
                {
                    Targetable me = PlayerController.Instance.GetComponent<Targetable>();
                    BetterList<Targetable> all = me.EnumerateWithinRadius(30f, me.GetEnemyTeams());
                    for (int i = 0; i < all.size; i++)
                    {
                        Targetable t = all[i];
                        Destroyable d = t != null ? t.GetComponent<Destroyable>() : null;
                        if (d != null && !d.IsDead) foes.Add(t.transform.position);
                    }
                }
                catch (Exception) { }
                Vector3 fwd = Flat(head * Vector3.forward);
                if (initial && foes.Count > 0)
                {
                    Vector3 c = Vector3.zero;
                    foreach (Vector3 f in foes) c += f;
                    c /= foes.Count;
                    Vector3 tc = Flat(c - eye);
                    if (tc.sqrMagnitude > 0.25f) fwd = tc;
                }
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                fwd.Normalize();
                float side = Settings.LeftHanded ? 1f : -1f;   // the shield hand's side
                float bestScore = float.MinValue;
                Vector3 bestP = Vector3.zero;
                Quaternion bestR = Quaternion.identity;
                float bestG = 0f, bestA = 0f;
                float[] angles = { 60f, 75f, 45f, 90f, 105f, 120f, 35f, 140f };
                float[] dists = relaxed ? new[] { 2.2f, 2.6f, Ideal, 1.9f } : new[] { Ideal, 2.6f, 3.4f, Near, Far };
                foreach (float a0 in angles)
                    for (int s = 0; s < 2; s++)
                        foreach (float d in dists)
                        {
                            float a = a0 * (s == 0 ? side : -side);
                            Vector3 dir = Quaternion.Euler(0f, a, 0f) * fwd;
                            Vector3 p = Flat(eye) + dir * d;
                            // the ground there
                            RaycastHit hit;
                            float gy;
                            if (Physics.Raycast(new Vector3(p.x, eye.y + 0.6f, p.z), Vector3.down, out hit, 4f, GroundMask, QueryTriggerInteraction.Ignore)) gy = hit.point.y;
                            else continue;
                            if (Mathf.Abs(gy - feet.y) > 0.7f) continue;
                            float centreY = Mathf.Max(gy + H * 0.5f + 0.45f, eye.y - 0.28f);
                            if (centreY - H * 0.5f - gy > 1.3f) continue;   // on stilts: a ledge or a drop below it
                            Vector3 c = new Vector3(p.x, centreY, p.z);
                            Vector3 look = Flat(c - eye).normalized;
                            Quaternion rot = Quaternion.LookRotation(look, Vector3.up) * Quaternion.Euler(8f, 0f, 0f);
                            // nothing between the eyes and the board, nothing in its volume
                            if (Physics.Linecast(eye, c - look * 0.1f, Solid, QueryTriggerInteraction.Ignore)) continue;
                            if (!relaxed && Physics.CheckBox(c, new Vector3(W * 0.5f + 0.05f, H * 0.5f + 0.05f, 0.08f), rot, Solid, QueryTriggerInteraction.Ignore)) continue;
                            // away from the enemies
                            float minA = 180f, minD = 99f;
                            foreach (Vector3 f in foes)
                            {
                                Vector3 tf = Flat(f - eye);
                                if (tf.sqrMagnitude > 1e-4f) minA = Mathf.Min(minA, Vector3.Angle(tf, dir));
                                minD = Mathf.Min(minD, Vector3.Distance(Flat(f), p));
                            }
                            if (minD < (relaxed ? 0.9f : 1.6f)) continue;
                            if (!initial && InView(eye, head, c)) continue;
                            float score = Mathf.Min(minA, 90f) * 0.6f - Mathf.Abs(Mathf.Abs(a) - 65f) * 0.35f - Mathf.Abs(d - Ideal) * 6f + (s == 0 ? 6f : 0f) - Mathf.Abs(gy - feet.y) * 25f;
                            if (minA < 45f) score -= 200f;
                            if (score > bestScore) { bestScore = score; bestP = c; bestR = rot; bestG = gy; bestA = a; EnemyAngle = minA; }
                        }
                if (bestScore == float.MinValue) return false;
                Pos = bestP;
                Rot = bestR;
                Ground = bestG;
                why = (bestA * side > 0f ? "shield side " : "weapon side ") + Mathf.Abs(bestA).ToString("0") + " deg, nearest enemy " + EnemyAngle.ToString("0") + " deg off" + (bestScore < -100f ? " (crowded)" : "") + (relaxed ? " (relaxed)" : "");
                return true;
            }

            void Put()
            {
                if (!m_root.activeSelf) m_root.SetActive(true);
                float rise = 1f - Mathf.Pow(1f - RiseT, 3f);
                float drop = (1f - rise) * (Pos.y - Ground + H * 0.5f + 0.05f);
                Vector3 c = Pos + Vector3.down * drop;
                Transform t = m_root.transform;
                t.SetPositionAndRotation(c, Rot);
                bool solid = !Ghost;
                m_wood.gameObject.SetActive(solid);
                m_postL.gameObject.SetActive(solid);
                m_postR.gameObject.SetActive(solid);
                m_wood.localPosition = new Vector3(0f, 0f, Depth * 0.5f + 0.004f);
                m_wood.localScale = new Vector3(W, H, Depth);
                // the posts down to the ground (in the board's tilted frame: straight enough at 8 deg)
                float postLen = Mathf.Max(0.1f, c.y - Ground + 0.05f);
                Post(m_postL, -1f, postLen);
                Post(m_postR, 1f, postLen);
                m_face.localPosition = Vector3.zero;
                if (m_backPanel != null) m_backPanel.alpha = Alpha;
                if (m_frontPanel != null) m_frontPanel.alpha = Alpha;
            }

            static void Post(Transform p, float side, float postLen)
            {
                p.localPosition = new Vector3(side * (W * 0.5f - 0.13f), -postLen * 0.5f + 0.02f, Depth + 0.03f);
                p.localScale = new Vector3(PostW, postLen, PostW);
            }

            // ---- the face (UI pixels, 1 mm each; y up from the centre)
            int m_rows;
            float m_hpY, m_timeY = float.NaN, m_specialY = float.NaN, m_artY = float.NaN, m_comboY = float.NaN, m_friendY = float.NaN, m_titleH;
            const float RowH = 236f, BarX0 = -560f, BarX1 = 70f, ValX = 120f, IconX = -680f, LineH = 136f, FriendH = 112f, IconK = 1.6f;

            void Layout(Info info)
            {
                int friends = Friends().Count;
                int key = (info.Timer ? 1 : 0) | (info.Special ? 2 : 0) | (info.Artefact ? 4 : 0) | (info.ComboNeeded > 0 ? 8 : 0) | (friends << 4);
                if (key == m_built) return;
                m_built = key;
                bool title = info.Timer && !string.IsNullOrEmpty(info.TimerText);
                m_titleH = title ? 160f : 0f;
                float h = 50f + m_titleH + RowH * (1 + (info.Timer ? 1 : 0) + (info.Special ? 1 : 0)) + (info.Artefact ? (m_gameArt && m_mArt != null ? RowH * 0.86f : LineH) : 0f) +
                          (info.ComboNeeded > 0 ? LineH : 0f) + friends * FriendH + 30f;
                H = h * Px;
                float y = h * 0.5f - 30f;
                m_frame.width = 1580;
                m_frame.height = Mathf.RoundToInt(h - 16f);
                m_planks.width = 1596;
                m_planks.height = Mathf.RoundToInt(h - 4f);
                m_title.gameObject.SetActive(title);
                m_divider.gameObject.SetActive(title);
                if (title)
                {
                    m_title.transform.localPosition = new Vector3(0f, y - 62f, 0f);
                    m_divider.transform.localPosition = new Vector3(0f, y - 134f, 0f);
                    y -= m_titleH;
                }
                m_hpY = y - RowH * 0.5f; y -= RowH;
                m_timeY = float.NaN; m_specialY = float.NaN; m_artY = float.NaN; m_comboY = float.NaN; m_friendY = float.NaN;
                if (info.Timer) { m_timeY = y - RowH * 0.5f; y -= RowH; }
                if (info.Special) { m_specialY = y - RowH * 0.5f; y -= RowH; }
                if (info.ComboNeeded > 0) { m_comboY = y - LineH * 0.5f; y -= LineH; }
                float artH = m_gameArt && m_mArt != null ? RowH * 0.86f : LineH;
                if (info.Artefact) { m_artY = y - artH * 0.5f; y -= artH; }
                if (friends > 0) { m_friendY = y - FriendH * 0.5f; }
                m_hp.transform.localPosition = new Vector3(ValX, m_hpY + 6f, 0f);
                if (m_heart != null) m_heart.transform.localPosition = new Vector3(IconX, m_hpY, 0f);
                PlaceDial(m_mHealth, IconX, m_hpY, DialH, true);
                PlaceDial(m_mWeapon, IconX, m_specialY, DialH, info.Special);
                PlaceDial(m_mArt, IconX + 45f, m_artY, ArtDialH, info.Artefact);
                float artRight = info.Artefact && m_mArt != null ? KeepInside(m_mArt) : IconX;
                m_time.gameObject.SetActive(info.Timer);
                if (m_glass != null) { m_glass.gameObject.SetActive(info.Timer); m_glass.transform.localPosition = new Vector3(IconX, m_timeY, 0f); }
                if (info.Timer) m_time.transform.localPosition = new Vector3(ValX, m_timeY + 6f, 0f);
                m_special.gameObject.SetActive(info.Special);
                if (info.Special) m_special.transform.localPosition = new Vector3(ValX, m_specialY + 4f, 0f);
                m_art.gameObject.SetActive(info.Artefact);
                if (info.Artefact) m_art.transform.localPosition = new Vector3(m_gameArt && m_mArt != null ? Mathf.Max(BarX0 + 50f, artRight + 24f) : IconX - 30f, m_artY, 0f);
                m_combo.gameObject.SetActive(info.ComboNeeded > 0);
                if (info.ComboNeeded > 0) m_combo.transform.localPosition = new Vector3(IconX - 30f, m_comboY, 0f);
            }

            // what the labels show now: each label's text is built only when its number changes (the HUD audit of 2026-10-19,
            // item 2: ten strings and a StringBuilder a frame in every fight); `Rows` (fighthud?) is built only when asked
            Info m_info;
            int m_hpK = int.MinValue, m_hpMaxK = int.MinValue, m_secsK = int.MinValue, m_specialK = int.MinValue, m_comboK = int.MinValue, m_artK = int.MinValue;
            readonly Destroyable[] m_friendD = new Destroyable[3];
            readonly float[] m_friendShare = new float[3];
            int m_friendN;

            /// The rows as shown (diagnostics: `fighthud?`).
            public string Rows
            {
                get
                {
                    var sb = new StringBuilder();
                    sb.Append("health ").Append(m_hp.text).Append(m_hpMax.text);
                    if (m_info.Timer) sb.Append(" | timer ").Append(m_time.text).Append(" '").Append(m_info.TimerText).Append('\'');
                    if (m_info.Special) sb.Append(" | special ").Append(m_special.text);
                    if (m_info.ComboNeeded > 0) sb.Append(" | ").Append(m_combo.text);
                    if (m_info.Artefact) sb.Append(" | ").Append(m_art.text);
                    for (int i = 0; i < m_friendN && i < m_friendNames.Count; i++)
                        sb.Append(" | ").Append(m_friendNames[i].text).Append(' ').Append(Mathf.RoundToInt(m_friendShare[i] * 100f)).Append('%');
                    return sb.ToString();
                }
            }

            void Draw(Info info)
            {
                m_info = info;
                float a = Alpha;
                // the title: the fight's own timer text ("Goblins escape")
                if (m_title.gameObject.activeSelf && m_title.text != info.TimerText) m_title.text = info.TimerText;
                // health
                int hp = Mathf.CeilToInt(info.Hp - 0.001f), hpMax = Mathf.RoundToInt(info.HpMax);
                if (hp != m_hpK) { m_hpK = hp; m_hp.text = hp.ToString(); }
                if (hpMax != m_hpMaxK) { m_hpMaxK = hpMax; m_hpMax.text = "/" + hpMax; }
                m_hpMax.transform.localPosition = new Vector3(ValX + m_hp.width + 8f, m_hpY - 22f, 0f);
                float share = info.HpMax > 0f ? Mathf.Clamp01(info.Hp / info.HpMax) : 0f;
                m_b.Clear();
                if (m_mHealth != null && m_mHealth.Alive) m_mHealth.Sync();
                if (m_mWeapon != null && m_mWeapon.Alive) m_mWeapon.Sync();
                if (m_mArt != null && m_mArt.Alive) m_mArt.Sync();
                Bar(m_hpY, share, new Color(0.86f, 0.13f, 0.08f), a);
                if (m_heart == null && m_mHealth == null) Heart(IconX, m_hpY, a);
                // the timer
                if (info.Timer)
                {
                    int secs = Mathf.CeilToInt(info.Left - 0.001f);
                    if (secs != m_secsK) { m_secsK = secs; m_time.text = (secs / 60) + ":" + (secs % 60).ToString("00"); }
                    bool late = info.Left <= 5.5f;
                    float pulse = late ? 0.65f + 0.35f * Mathf.Sin(Time.unscaledTime * 9f) : 1f;
                    m_time.color = late ? new Color(1f, 0.62f, 0.22f, 1f) : GameText.Parchment;
                    Bar(m_timeY, info.Duration > 0f ? info.Left / info.Duration : 0f, late ? new Color(1f, 0.55f, 0.1f) : new Color(0.33f, 0.82f, 0.3f), a * pulse);
                    if (m_glass == null) Glass(IconX, m_timeY, a);
                }
                // the special: the super meter (or the weapon's hit counter in Classic)
                if (info.Special)
                {
                    float pulse = info.SpecialReady ? 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * 5f) : 1f;
                    Bar(m_specialY, info.SpecialShare, info.SpecialReady ? new Color(1f, 0.86f, 0.42f) : new Color(0.95f, 0.66f, 0.18f), a * pulse);
                    if (m_mWeapon == null) Star(IconX, m_specialY, info.SpecialReady ? 1f : 0.7f, a);
                    else if (Physical.On)
                    {
                        // the weapon dial's own ring shows the super meter (Physical: it stands in for the game's hit counter)
                        UISprite ring = m_mWeapon.Find<UISprite>("Foreground");
                        if (ring != null)
                        {
                            if (!ring.enabled) ring.enabled = true;
                            if (Mathf.Abs(ring.fillAmount - info.SpecialShare) > 0.002f) ring.fillAmount = info.SpecialShare;
                        }
                        Color lit = info.SpecialReady ? new Color(1f, 1f, 1f, 1f) : new Color(0.62f, 0.6f, 0.58f, 1f);
                        UISprite ov = m_mWeapon.Find<UISprite>("Overlay");
                        if (ov != null && ov.color != lit) ov.color = lit;
                        m_mWeapon.TintTextures(lit);
                    }
                    int pct = Mathf.FloorToInt(info.SpecialShare * 100f);
                    int sk = info.SpecialReady ? -1 : info.Meter ? pct : 1000000 + info.Hits * 1000 + info.HitsNeeded;
                    if (sk != m_specialK)
                    {
                        m_specialK = sk;
                        m_special.text = info.SpecialReady ? "READY" : info.Meter ? pct + "%" : info.Hits + "/" + info.HitsNeeded;
                    }
                    if (info.SpecialReady != m_readyShown)
                    {
                        m_readyShown = info.SpecialReady;
                        if (info.SpecialReady) GameText.Gild(m_special, 1.6f);
                        else
                        {
                            m_special.applyGradient = false;
                            m_special.color = GameText.Parchment;
                            m_special.effectStyle = UILabel.Effect.Outline8;
                            m_special.effectColor = new Color(0.06f, 0.035f, 0.015f, 0.9f);
                        }
                    }
                    if (m_glyph != null)
                    {
                        m_glyph.gameObject.SetActive(info.SpecialReady);
                        m_glyph.transform.localPosition = new Vector3(Mathf.Min(ValX + m_special.width + 76f, 700f), m_specialY, 0f);
                    }
                }
                else if (m_glyph != null) m_glyph.gameObject.SetActive(false);
                if (info.ComboNeeded > 0)
                {
                    int ck = info.Combo * 100000 + info.ComboNeeded;
                    if (ck != m_comboK) { m_comboK = ck; m_combo.text = "Combo  " + info.Combo + " / " + info.ComboNeeded; }
                }
                if (info.Artefact)
                {
                    int st = info.ArtReady ? 0 : info.ArtCooling ? 1 : info.ArtLeft == 0 ? 2 : 3;
                    int ak = st + 4 * (st == 1 ? Mathf.CeilToInt(info.ArtCool) : 0) + 4096 * (info.ArtLeft + 1);
                    if (ak != m_artK)
                    {
                        m_artK = ak;
                        m_art.text = "Artefact  " + (info.ArtReady ? "ready" : info.ArtCooling ? "back in " + Mathf.CeilToInt(info.ArtCool) + " s" : info.ArtLeft == 0 ? "used up" : "not ready") +
                                     (info.ArtLeft > 0 ? "   x" + info.ArtLeft : "");
                    }
                    m_art.color = info.ArtReady ? new Color(1f, 0.9f, 0.6f, 1f) : new Color(0.75f, 0.7f, 0.62f, 1f);
                    if (m_glyphA != null)
                    {
                        m_glyphA.gameObject.SetActive(info.ArtReady);
                        m_glyphA.transform.localPosition = new Vector3((m_gameArt && m_mArt != null ? BarX0 + 50f : IconX - 30f) + m_art.width + 70f, m_artY, 0f);
                        m_glyphA.width = m_glyphA.height = 100;
                    }
                }
                else if (m_glyphA != null) m_glyphA.gameObject.SetActive(false);
                // the companions: a short bar each (a name looked up only when the slot's companion changes)
                List<Destroyable> fr = Friends();
                m_friendN = fr.Count;
                for (int i = 0; i < fr.Count; i++)
                {
                    float y = m_friendY - i * FriendH;
                    Destroyable d = fr[i];
                    float fs = d != null && d.MaxHealth != null ? Mathf.Clamp01(d.CurrentHealth / Mathf.Max(1f, d.MaxHealth.Value)) : 0f;
                    while (m_friendNames.Count <= i) m_friendNames.Add(Value(m_front, "friend" + m_friendNames.Count, 80));
                    UILabel l = m_friendNames[i];
                    if (i < m_friendD.Length && (m_friendD[i] != d || string.IsNullOrEmpty(l.text))) { m_friendD[i] = d; l.text = Companion(d); }
                    if (i < m_friendShare.Length) m_friendShare[i] = fs;
                    l.transform.localPosition = new Vector3(IconX - 30f, y, 0f);
                    if (!l.gameObject.activeSelf) l.gameObject.SetActive(true);
                    SmallBar(y, fs, new Color(0.45f, 0.75f, 0.95f), a);
                }
                for (int i = fr.Count; i < m_friendNames.Count; i++) if (m_friendNames[i].gameObject.activeSelf) m_friendNames[i].gameObject.SetActive(false);
                EndBars();
                m_b.To(m_barMesh);
            }

            static string Companion(Destroyable d)
            {
                try
                {
                    AI ai = d != null ? d.GetComponent<AI>() : null;
                    if (ai != null && ai.Ref != null && ai.Ref.ShowcaseTitle != null)
                    {
                        string t = ai.Ref.ShowcaseTitle.GetValue(ai.Ref.StringLibrary);
                        if (!string.IsNullOrEmpty(t)) return t;
                    }
                    if (d != null) return WeaponGrips.ModelKey(d.name);
                }
                catch (Exception) { }
                return "Companion";
            }

            void Bar(float y, float share, Color c, float a)
            {
                if (m_gameArt && GameBar(BarX0, BarX1, y, 46f, share, c, a)) return;
                float h = 40f;
                int bt = m_b.V.Count;
                m_b.Pill((BarX1 - BarX0) * 0.5f, 0f, Col(new Color(0.03f, 0.02f, 0.012f), 0.92f * a), h + 5f, Col(new Color(0.03f, 0.02f, 0.012f), 0.92f * a), 0f, 10);
                Shift(BarX0 + (BarX1 - BarX0) * 0.5f, y, bt);
                share = Mathf.Clamp01(share);
                if (share > 0.004f)
                {
                    float len = (BarX1 - BarX0) * share;
                    int b0 = m_b.V.Count;
                    m_b.Pill(Mathf.Max(0f, len * 0.5f - h), 0f, Col(c, a), h, Col(c * 0.8f, a), -0.5f, 10);
                    Shift(BarX0 + Mathf.Max(h, len * 0.5f), y, b0);
                    int b1 = m_b.V.Count;
                    m_b.Pill(Mathf.Max(0f, len * 0.5f - h), 0f, Col(Color.Lerp(c, Color.white, 0.45f), 0.55f * a), h * 0.35f, Col(Color.Lerp(c, Color.white, 0.45f), 0f), -0.8f, 10);
                    Shift(BarX0 + Mathf.Max(h, len * 0.5f), y + h * 0.35f, b1);
                }
            }

            void SmallBar(float y, float share, Color c, float a)
            {
                float x0 = -160f, x1 = 700f, h = 22f;
                if (m_gameArt && GameBar(x0, x1, y, 28f, share, c, a)) return;
                int b0 = m_b.V.Count;
                m_b.Pill((x1 - x0) * 0.5f, 0f, Col(new Color(0.03f, 0.02f, 0.012f), 0.92f * a), h + 4f, Col(new Color(0.03f, 0.02f, 0.012f), 0.92f * a), 0f, 8);
                Shift(x0 + (x1 - x0) * 0.5f, y, b0);
                if (share > 0.004f)
                {
                    float len = (x1 - x0) * share;
                    int b1 = m_b.V.Count;
                    m_b.Pill(Mathf.Max(0f, len * 0.5f - h), 0f, Col(c, a), h, Col(c * 0.8f, a), -0.5f, 8);
                    Shift(x0 + Mathf.Max(h, len * 0.5f), y, b1);
                }
            }

            /// Moves the builder's vertices from index `from` on by (x, y).
            void Shift(float x, float y, int from)
            {
                for (int i = Mathf.Max(0, from); i < m_b.V.Count; i++) m_b.V[i] = m_b.V[i] + new Vector3(x, y, 0f);
            }

            static Color Col(Color c, float a) { c.a = a; return c; }

            void Star(float x, float y, float k, float a)
            {
                Color c = Col(new Color(1f, 0.82f, 0.38f), a * k);
                for (int i = 0; i < 4; i++)
                {
                    float ang = i * Mathf.PI * 0.25f;
                    Vector2 d = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * (i % 2 == 0 ? 42f : 29f) * IconK;
                    m_b.Stroke(new Vector2(x, y) - d, new Vector2(x, y) + d, (i % 2 == 0 ? 12f : 9f) * IconK, c, 2f, -1f);
                }
            }

            void Heart(float x, float y, float a)
            {
                Color c = Col(new Color(0.88f, 0.12f, 0.1f), a);
                m_b.Stroke(new Vector2(x - 26f, y + 10f), new Vector2(x, y - 26f), 22f, c, 2f, -1f);
                m_b.Stroke(new Vector2(x + 26f, y + 10f), new Vector2(x, y - 26f), 22f, c, 2f, -1f);
                m_b.Stroke(new Vector2(x - 16f, y + 16f), new Vector2(x - 15f, y + 16f), 24f, c, 2f, -1f);
                m_b.Stroke(new Vector2(x + 15f, y + 16f), new Vector2(x + 16f, y + 16f), 24f, c, 2f, -1f);
            }

            void Glass(float x, float y, float a)
            {
                Color c = Col(new Color(0.95f, 0.85f, 0.6f), a);
                m_b.Stroke(new Vector2(x - 20f, y + 28f), new Vector2(x + 20f, y + 28f), 6f, c, 1.5f, -1f);
                m_b.Stroke(new Vector2(x - 20f, y - 28f), new Vector2(x + 20f, y - 28f), 6f, c, 1.5f, -1f);
                m_b.Stroke(new Vector2(x - 16f, y + 24f), new Vector2(x + 16f, y - 24f), 5f, c, 1.5f, -1f);
                m_b.Stroke(new Vector2(x + 16f, y + 24f), new Vector2(x - 16f, y - 24f), 5f, c, 1.5f, -1f);
            }
        }

        // =================================================================================== Body: the wrist timer (and the combo goal)
        static GameObject s_wrist;
        static Mesh s_wristMesh, s_wristGlowMesh;
        static readonly Premonition.Builder s_wb = new Premonition.Builder(), s_wg = new Premonition.Builder();
        static int s_wristFrame = -1;
        public static int WristFrames;
        public static float WristFill = -1f;
        // (t along the cuff, 0 its hand end .. 1 its open end: HandGeometry.CuffPoint. The cuff was cut to HL:A's 2026-10-20 - 2.9 cm
        // long, it was 6.4: the timer band 0.64-1.69 cm from its hand end, over the wrist strap, the combo band 1.95-2.54 cm)
        public static float BandT0 = 0.22f, BandT1 = 0.58f, BandGrow = 0.22f, BandFrom = -15f, BandTo = 195f;

        /// Called by the hands (Hands.Health.Update) every frame with the shield hand.
        public static void Wrist(Transform hand, HandGeometry geo, int layer, VRRig rig)
        {
            if (hand == null || geo == null || rig == null || !CombatVR.Active || CombatSettings.FightHud != CombatSettings.FightHudStyle.Body) return;
            Info info = Read();
            if (!info.Ok || (!info.Timer && info.ComboNeeded <= 0)) return;
            if (s_wrist == null)
            {
                s_wrist = new GameObject("HoF2VR_WristTimer");
                s_wristMesh = new Mesh { name = "HoF2VR_WristTimer" };
                s_wristMesh.MarkDynamic();
                s_wrist.AddComponent<MeshFilter>().sharedMesh = s_wristMesh;
                var r = s_wrist.AddComponent<MeshRenderer>();
                r.sharedMaterial = Premonition.Alpha();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                var g = new GameObject("glow");
                g.transform.SetParent(s_wrist.transform, false);
                s_wristGlowMesh = new Mesh { name = "HoF2VR_WristTimerGlow" };
                s_wristGlowMesh.MarkDynamic();
                g.AddComponent<MeshFilter>().sharedMesh = s_wristGlowMesh;
                var gr = g.AddComponent<MeshRenderer>();
                gr.sharedMaterial = Premonition.Additive();
                gr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                gr.receiveShadows = false;
            }
            if (s_wrist.transform.parent != hand) s_wrist.transform.SetParent(hand, false);
            if (s_wrist.layer != layer) { s_wrist.layer = layer; s_wrist.transform.GetChild(0).gameObject.layer = layer; }
            if (!s_wrist.activeSelf) s_wrist.SetActive(true);
            s_wristFrame = Time.frameCount;
            WristFrames++;
            s_wb.Clear(); s_wg.Clear();
            float t0 = BandT0, t1 = BandT1;
            // the pictures of 2026-10-20 (captures/hands-2026-10-20/after_tune_badge_*): on the short cuff the artefact's badge
            // (2.8 cm, the whole cuff's length) hid the middle of both bands; the back of the hand above the cuff is the super
            // meter's and the gems'. The bands now leave a gap under the badge - it sits in them like a clasp; the fill passing
            // the gap shows again beyond it
            s_gapOn = HandIcons.CuffBadgeShown;
            if (s_gapOn)
            {
                float mid = 90f - HandGeometry.BadgeAng, half = HandIcons.CuffBadgeSize * 50f / HandGeometry.CuffBackRadius * Mathf.Rad2Deg + BadgeGapPad;
                s_gap0 = mid - half; s_gap1 = mid + half;
            }
            if (info.Timer)
            {
                float fill = info.Duration > 0f ? Mathf.Clamp01(info.Left / info.Duration) : 0f;
                WristFill = fill;
                bool late = info.Left <= 5.5f;
                float pulse = late ? 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 9f) : 1f;
                Color fc = late ? new Color(1f, 0.55f, 0.1f, pulse) : new Color(0.35f, 0.88f, 0.32f, 1f);
                // the dark track round the wrist's back half, then the fill from one end
                BandG(s_wb, geo, BandFrom, BandTo, t0 - 0.02f, t1 + 0.02f, BandGrow - 0.02f, new Color(0.05f, 0.035f, 0.02f, 0.9f));
                if (fill > 0.003f)
                {
                    float to = Mathf.Lerp(BandFrom, BandTo, fill);
                    BandG(s_wb, geo, BandFrom, to, t0, t1, BandGrow, fc);
                    BandG(s_wg, geo, BandFrom, to, t0 + 0.02f, t1 - 0.02f, BandGrow + 0.01f, new Color(fc.r, fc.g, fc.b, 0.35f * pulse));
                }
                // the combo band (if any) beyond it, toward the elbow
                t0 = t1 + ComboGap;
                t1 = t0 + ComboW;
            }
            else WristFill = -1f;
            // the HUD audit of 2026-10-19, item 3: a combo objective ("Reach a combo of N": its enemies can't die until it is met)
            // showed its goal and count only on the board. On you: a gold band round the wrist that fills toward the goal, and
            // "Combo 3 / 8" above it, facing the eyes while the wrist is looked at
            ComboShown = -1;
            ComboRead = false;
            if (info.ComboNeeded > 0)
            {
                ComboShown = info.Combo;
                float share = Mathf.Clamp01(info.Combo / (float)info.ComboNeeded);
                Color gc = new Color(1f, 0.78f, 0.25f, 1f);
                BandG(s_wb, geo, BandFrom, BandTo, t0 - 0.02f, t1 + 0.02f, BandGrow - 0.02f, new Color(0.05f, 0.035f, 0.02f, 0.9f));
                if (share > 0.003f)
                {
                    float to = Mathf.Lerp(BandFrom, BandTo, share);
                    BandG(s_wb, geo, BandFrom, to, t0, t1, BandGrow, gc);
                    BandG(s_wg, geo, BandFrom, to, t0 + 0.02f, t1 - 0.02f, BandGrow + 0.01f, new Color(gc.r, gc.g, gc.b, 0.3f));
                }
                ComboLabel(hand, geo, layer, rig, info.Combo, info.ComboNeeded, (t0 + t1) * 0.5f);
            }
            s_wb.To(s_wristMesh);
            s_wg.To(s_wristGlowMesh);
        }

        public static float ComboGap = 0.09f, ComboW = 0.2f, ComboNear = 0.65f, ComboLook = 40f, ComboUp = 0.05f, ComboOut = 0.03f;
        /// The combo count on the wrist's band this frame (-1: none) and whether its label is up (`fighthud?`'s wrist part).
        public static int ComboShown = -1;
        public static bool ComboRead;
        public static string ComboText = "-";
        static Transform s_comboRoot;
        static UILabel s_comboLabel;
        static int s_comboFrame = -1, s_comboKey = int.MinValue;

        /// "Combo 3 / 8" just above the gold band, turned to face the eyes, while the player looks at the wrist (within
        /// ComboLook deg of the gaze and ComboNear m of the eyes). (v4's pictures: lying flat on the cuff's back, as the table's
        /// stats do, it was edge-on or behind the shield in every guard pose a fight leaves time for.)
        static void ComboLabel(Transform hand, HandGeometry geo, int layer, VRRig rig, int combo, int needed, float tMid)
        {
            if (!GameText.Ready) return;
            if (s_comboRoot == null)
            {
                var go = new GameObject("HoF2VR_WristCombo") { layer = layer };
                GameText.Panel(go, 3150);
                s_comboRoot = go.transform;
                var holder = new GameObject("scale") { layer = layer };
                holder.transform.SetParent(s_comboRoot, false);
                holder.transform.localScale = Vector3.one * 0.00034f;
                s_comboLabel = GameText.Label(holder.transform, "text", GameText.Normal, 84, Color.white, 2);   // capitals ~17 mm: ~1.8 deg at 0.55 m, as the board's numbers
                GameText.Gild(s_comboLabel, 1.8f);
            }
            if (s_comboRoot.parent != hand) s_comboRoot.SetParent(hand, false);
            if (s_comboRoot.gameObject.layer != layer)
            {
                s_comboRoot.gameObject.layer = layer;
                s_comboRoot.GetChild(0).gameObject.layer = layer;
                s_comboLabel.gameObject.layer = layer;
            }
            Vector3 eye = rig.HeadWorldPos, up = rig.HeadWorldRot * Vector3.up;
            Vector3 band = hand.TransformPoint(geo.CuffPoint(tMid, Mathf.PI * 0.5f, BandGrow));
            Vector3 toEye = eye - band;
            float dist = toEye.magnitude;
            bool looked = dist > 0.05f && dist < ComboNear * rig.Scale && Vector3.Angle(rig.HeadWorldRot * Vector3.forward, -toEye) < ComboLook;
            if (s_comboRoot.gameObject.activeSelf != looked) s_comboRoot.gameObject.SetActive(looked);
            if (!looked) return;
            s_comboFrame = Time.frameCount;
            ComboRead = true;
            int key = combo * 100000 + needed;
            if (key != s_comboKey) { s_comboKey = key; ComboText = "Combo " + combo + " / " + needed; s_comboLabel.text = ComboText; }
            // above the band in the view and a little toward the eyes (clear of the cuff), facing them (NGUI draws toward -z)
            Vector3 at = band + (up - toEye * (Vector3.Dot(up, toEye) / (dist * dist))).normalized * ComboUp * rig.Scale + toEye / dist * ComboOut * rig.Scale;
            s_comboRoot.SetPositionAndRotation(at, Quaternion.LookRotation(at - eye, up));
        }

        /// The gap under the artefact's badge (deg round the cuff, CuffPoint's angles: 90 = the back) while it is shown, and its
        /// margin each side (deg).
        public static float BadgeGapPad = 4f;
        static bool s_gapOn;
        static float s_gap0, s_gap1;

        /// Band, less the badge's gap (two pieces, or one, or none).
        static void BandG(Premonition.Builder b, HandGeometry geo, float fromDeg, float toDeg, float t0, float t1, float grow, Color c)
        {
            if (!s_gapOn || toDeg <= s_gap0 || fromDeg >= s_gap1) { Band(b, geo, fromDeg, toDeg, t0, t1, grow, c); return; }
            if (s_gap0 - fromDeg > 0.5f) Band(b, geo, fromDeg, s_gap0, t0, t1, grow, c);
            if (toDeg - s_gap1 > 0.5f) Band(b, geo, s_gap1, toDeg, t0, t1, grow, c);
        }

        static void Band(Premonition.Builder b, HandGeometry geo, float fromDeg, float toDeg, float t0, float t1, float grow, Color c)
        {
            int n = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(toDeg - fromDeg) / 8f));
            int b0 = b.V.Count;
            for (int i = 0; i <= n; i++)
            {
                float ang = Mathf.Lerp(fromDeg, toDeg, i / (float)n) * Mathf.Deg2Rad;
                b.V.Add(geo.CuffPoint(t0, ang, grow)); b.C.Add(c);
                b.V.Add(geo.CuffPoint(t1, ang, grow)); b.C.Add(c);
            }
            for (int i = 0; i < n; i++)
            {
                int q = b0 + i * 2;
                b.T.Add(q); b.T.Add(q + 1); b.T.Add(q + 2);
                b.T.Add(q + 1); b.T.Add(q + 3); b.T.Add(q + 2);
            }
        }

        static void HideWristIfIdle()
        {
            if (s_wrist != null && s_wrist.activeSelf && s_wristFrame < Time.frameCount - 1) { s_wrist.SetActive(false); WristFill = -1f; ComboShown = -1; ComboRead = false; }
            if (s_comboRoot != null && s_comboRoot.gameObject.activeSelf && s_comboFrame < Time.frameCount - 1) s_comboRoot.gameObject.SetActive(false);
        }

        // =================================================================================== diagnostics
        string Short() => CombatSettings.FightHud + (m_board != null ? (m_board.Placed ? " board " + m_board.Dist.ToString("0.0") + " m" + (m_board.Ghost ? " ghost" : "") : " board unplaced") : "") +
                          " flat hidden " + FlatHiddenNow + (s_wrist != null && s_wrist.activeSelf ? " wrist " + WristFill.ToString("0.00") : "");

        string Describe()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("style ").Append(CombatSettings.FightHud).Append(" fight ").Append(CombatVR.Active).Append(" flatHidden ").Append(FlatHiddenNow).Append(" flatHides ").Append(FlatHides)
              .Append(" (").Append(string.Join(",", s_hiddenList.Distinct().ToArray())).Append(")");
            if (m_board != null)
            {
                Board b = m_board;
                Vector3 eye = VRRig.I != null ? VRRig.I.HeadWorldPos : Vector3.zero;
                Vector3 fp = b.Pos, facing = b.Rot * Vector3.forward;
                sb.Append(" | board placed ").Append(b.Placed).Append(" at ").Append(fp.x.ToString("0.000", inv)).Append(',').Append(fp.y.ToString("0.000", inv)).Append(',').Append(fp.z.ToString("0.000", inv))
                  .Append(" dist ").Append(b.Dist.ToString("0.00", inv)).Append(" eyeDist ").Append(Vector3.Distance(eye, fp).ToString("0.00", inv)).Append(" gaze ").Append(b.GazeAngle.ToString("0", inv))
                  .Append(" enemyAngle ").Append(b.EnemyAngle.ToString("0", inv)).Append(" faceDot ").Append(Vector3.Dot(facing, (fp - eye).normalized).ToString("0.00", inv))
                  .Append(" ghost ").Append(b.Ghost).Append(" alpha ").Append(b.Alpha.ToString("0.00", inv)).Append(" h ").Append(b.H.ToString("0.00", inv)).Append(" ground ").Append(b.Ground.ToString("0.00", inv))
                  .Append(" why ").Append(b.Why).Append(" | rows ").Append(b.Rows).Append(" | ").Append(Board.ArtFit);
            }
            sb.Append(" | placed ").Append(BoardsPlaced).Append(" moves ").Append(BoardMoves).Append(" movesInView ").Append(MovesInView).Append(" last ").Append(LastMove)
              .Append(" | wrist ").Append(s_wrist != null && s_wrist.activeSelf ? "on " + WristFill.ToString("0.00", inv) : "off").Append(" frames ").Append(WristFrames)
              .Append(" combo ").Append(s_wrist != null && s_wrist.activeSelf && ComboShown >= 0 ? ComboShown + " label " + (ComboRead ? "'" + ComboText + "'" : "off") : "-");
            return sb.ToString();
        }
    }
}

using System;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Until You Fall's super meter (docs/uyf-reference.md §3.15; UYF's DefaultWeapon_SuperChargeStats; combat Stage 2 item
    /// 8): 0-100, +1 per block or dodge (a blocked shot too), +2 per hit that takes health, +0.5 per guard hit, +7.5 per kill,
    /// +5 when you are hit. In Physical mode it replaces the hit counters of HoF2's weapon abilities (the user: yes): a weapon
    /// ability that waits for N hits (Ability.AttackHitsRequired) is ready when the meter is full, and using it empties the
    /// meter. Artefacts keep their own charges and cooldowns. It lasts through the run (HoF2 has no rooms to reset it in).
    /// Shown on the back of the shield glove beside the health gems: a slim bar filling gold, glowing when full.
    internal static class SuperMeter
    {
        public const float Max = 100f;
        public static float Charge;
        public static int Fills, Spends;
        public static string Last = "-";

        public static bool Full => Charge >= Max - 0.001f;

        public static void Add(float amount, string why)
        {
            if (!CombatSettings.Physical || amount <= 0f) return;
            bool was = Full;
            Charge = Mathf.Min(Max, Charge + amount);
            Last = why + " +" + amount.ToString("0.0") + " -> " + Charge.ToString("0.0");
            if (Full && !was)
            {
                Fills++;
                // ready: a bright two-note chime and both hands
                if (VRRig.I != null) CombatSounds.Layer(CombatSounds.ComboDone, VRRig.I.HeadWorldPos + Vector3.down * 0.4f, 0.55f, 1.25f);
                CombatFeel.ImpactBoth();
            }
        }

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.PropertyGetter(typeof(Ability), "IsComboValid"), postfix: new HarmonyMethod(typeof(SuperMeter), nameof(ComboValidPostfix)));
                h.Patch(AccessTools.Method(typeof(Ability), "Use", new[] { typeof(Targetable) }), postfix: new HarmonyMethod(typeof(SuperMeter), nameof(UsePostfix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("SuperMeter: patch failed: " + e.Message); }
            Diag.Commands["super?"] = a =>
            {
                if (a.Length > 2 && a[1] == "set" && TestMode.Enabled) Charge = Mathf.Clamp(float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture), 0f, Max);
                return "super " + Charge.ToString("0.0") + "/" + Max.ToString("0") + (Full ? " FULL" : "") + " fills " + Fills + " spends " + Spends + " activations " + Activations +
                       " glowFrames " + WeaponTrail.GlowFrames + " glowLen " + WeaponTrail.GlowLen.ToString("0.00") + " handGlow " + HandGlowOn + " handGlowFrames " + HandGlowFrames +
                       (s_glow != null && s_glow.activeInHierarchy ? " handGlowAt " + Diag.V(s_glow.transform.position) : "") + " last " + Last +
                       " | on the glove " + (s_go != null && s_go.activeInHierarchy ? Diag.V(s_go.transform.position) + " " + (s_go.transform.parent != null ? s_go.transform.parent.name : "-") : "hidden");
            };
        }

        // ---- on the glove: a slim bar beside the health gems (Hands.Health calls Draw with their place)
        static GameObject s_go;
        static Mesh s_mesh;
        static readonly Premonition.Builder s_b = new Premonition.Builder();
        static int s_built = int.MinValue;
        public static float BarLength = 0.07f, BarWidth = 0.006f, Side = 0.017f;

        // ---- playtest 3 note D (2026-10-18): the weapon hand glows while the super (the weapon ability) is ready - its stick
        // click fires it - until it is used: a soft gold halo round the fist, turned to the eyes, pulsing (a world object on the hand)
        static GameObject s_glow;
        static Mesh s_glowMesh;
        static readonly Premonition.Builder s_gb = new Premonition.Builder();
        public static int HandGlowFrames;
        public static bool HandGlowOn;
        public static float HandGlowRadius = 0.085f;

        public static void HandGlowTick()
        {
            bool on = false;
            Transform hand = null;
            try
            {
                CombatVR vr = CombatVR.I;
                hand = vr != null ? (Settings.LeftHanded ? vr.HandL : vr.HandR) : null;
                on = hand != null && VRRig.I != null && CombatSettings.Physical && Full && Abilities.Ready(true);
            }
            catch (Exception) { on = false; }
            HandGlowOn = on;
            if (!on) { if (s_glow != null && s_glow.activeSelf) s_glow.SetActive(false); return; }
            if (s_glow == null)
            {
                s_glow = new GameObject("HoF2VR_SuperHandGlow");
                s_glowMesh = new Mesh { name = "HoF2VR_SuperHandGlow" };
                s_glowMesh.MarkDynamic();
                s_glow.AddComponent<MeshFilter>().sharedMesh = s_glowMesh;
                var r = s_glow.AddComponent<MeshRenderer>();
                r.sharedMaterial = Premonition.Additive();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            if (s_glow.transform.parent != hand) s_glow.transform.SetParent(hand, false);
            if (s_glow.layer != hand.gameObject.layer) s_glow.layer = hand.gameObject.layer;
            if (!s_glow.activeSelf) s_glow.SetActive(true);
            s_glow.transform.localPosition = ControllerGeometry.GripPos(Settings.LeftHanded);
            Vector3 eye = VRRig.I.HeadWorldPos, at = s_glow.transform.position;
            if ((at - eye).sqrMagnitude > 1e-4f) s_glow.transform.rotation = Quaternion.LookRotation(at - eye, Vector3.up);
            float t = Time.unscaledTime, pulse = 0.7f + 0.3f * Mathf.Sin(t * 5f), r0 = HandGlowRadius;
            Color gold = new Color(1f, 0.78f, 0.3f);
            s_gb.Clear();
            s_gb.Pill(0f, 0f, new Color(gold.r, gold.g, gold.b, 0.32f * pulse), r0, new Color(gold.r, gold.g, gold.b, 0f), 0f, 24);
            s_gb.Arc(r0 * (0.75f + 0.1f * pulse), 0.006f, 0f, 360f, new Color(1f, 0.9f, 0.55f, 0.55f * pulse), 0.006f, -0.001f);
            // three sparks circling the fist
            for (int i = 0; i < 3; i++)
            {
                float a = t * 2.2f + i * 2.094f;
                Vector2 p = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r0 * 0.9f;
                s_gb.Stroke(p, p + new Vector2(-Mathf.Sin(a), Mathf.Cos(a)) * 0.012f, 0.006f, new Color(1f, 0.85f, 0.45f, 0.8f), 0.004f, -0.002f);
            }
            s_gb.To(s_glowMesh);
            HandGlowFrames++;
        }

        public static void Hide()
        {
            if (s_go != null && s_go.activeSelf) s_go.SetActive(false);
        }

        /// center / normal / row: the health gems' middle, the glove's back normal and the gems' row direction (hand-local);
        /// look 0..1: how much the back of the hand faces the eyes (dimmer when it doesn't).
        public static void Draw(Transform hand, Vector3 center, Vector3 normal, Vector3 row, int layer, float look)
        {
            if (!CombatSettings.Physical || hand == null || row.sqrMagnitude < 1e-8f) { Hide(); return; }
            if (s_go == null)
            {
                s_go = new GameObject("HoF2VR_SuperMeter");
                s_mesh = new Mesh { name = "HoF2VR_SuperMeter" };
                s_mesh.MarkDynamic();
                s_go.AddComponent<MeshFilter>().sharedMesh = s_mesh;
                var r = s_go.AddComponent<MeshRenderer>();
                r.sharedMaterial = Premonition.Alpha();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            if (s_go.transform.parent != hand) s_go.transform.SetParent(hand, false);
            if (s_go.layer != layer) s_go.layer = layer;
            if (!s_go.activeSelf) s_go.SetActive(true);
            Vector3 n = normal.normalized, x = row.normalized, y = Vector3.Cross(n, x).normalized;
            s_go.transform.localPosition = center + y * Side + n * 0.002f;
            s_go.transform.localRotation = Quaternion.LookRotation(-n, y);   // local x along the row, facing out of the glove
            float k = Mathf.Clamp01(Charge / Max);
            bool full = Full;
            float pulse = full ? 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * 6f) : 1f;
            float a = Mathf.Lerp(0.35f, 1f, look);
            int charges = CombatSettings.UyfDash ? CombatMove.ChargesLeft() : -1;
            // the button icons (the player's playtest 2026-10-17: "put a small icon there of the button you need to hit to
            // activate it"): the weapon ability's stick at the bar's right end, the artefact's at its left, only while ready
            bool wReady = Abilities.Ready(true), aReady = Abilities.Ready(false);
            // (a number, not a string: no garbage every frame - main's GC part 2)
            int key = Mathf.RoundToInt(k * 60f) | (full ? (Mathf.RoundToInt(pulse * 10f) + 1) << 7 : 0) | (Mathf.RoundToInt(a * 10f) << 12) | ((charges + 1) << 17) |
                      (wReady ? 1 << 20 : 0) | (aReady ? 1 << 21 : 0) | (wReady || aReady ? (Mathf.RoundToInt((0.8f + 0.2f * Mathf.Sin(Time.unscaledTime * 5f)) * 10f) << 22) : 0);
            if (key == s_built) return;
            s_built = key;
            float h = BarLength * 0.5f;
            Color track = new Color(0.05f, 0.04f, 0.02f, 0.8f * a), fill = full ? new Color(1f, 0.88f, 0.45f, a * pulse) : new Color(0.95f, 0.66f, 0.18f, a);
            s_b.Clear();
            s_b.Stroke(new Vector2(-h - 0.002f, 0f), new Vector2(h + 0.002f, 0f), BarWidth + 0.003f, track, 0.001f, 0.0005f);
            if (k > 0.001f) s_b.Stroke(new Vector2(-h, 0f), new Vector2(-h + 2f * h * k, 0f), BarWidth, fill, 0.0015f, -0.0005f);
            if (full) s_b.Stroke(new Vector2(-h, 0f), new Vector2(h, 0f), BarWidth * 2.2f, new Color(1f, 0.9f, 0.5f, 0.35f * a * pulse), 0.004f, 0.001f);
            // UYF's dash charges: three pips under the bar, white ready, grey spent
            for (int i = 0; i < 3 && charges >= 0; i++)
            {
                float px = -h + 0.006f + i * 0.012f, py = -BarWidth - 0.006f;
                Color pc = i < charges ? new Color(0.95f, 0.95f, 0.95f, a) : new Color(0.35f, 0.35f, 0.35f, 0.7f * a);
                s_b.Stroke(new Vector2(px - 0.0025f, py), new Vector2(px + 0.0025f, py), 0.005f, pc, 0.001f, -0.0005f);
            }
            if (wReady) StickIcon(h + 0.011f, 0f, !Settings.LeftHanded, a);
            if (aReady) StickIcon(-h - 0.011f, 0f, Settings.LeftHanded, a);
            s_b.To(s_mesh);
        }

        public static int IconFrames, HudIconShows;
        public static string HudIconState = "-";
        static UISprite s_hudIcon;

        /// The same button's glyph beside the game's own weapon HUD (PlayerWeaponUI: the ability's charge, "3/3" with the
        /// weapon), while the weapon ability is ready: the weapon hand's stick click (QuestPrompts' ring R / L).
        public static void HudIcon(bool ready)
        {
            try
            {
                PlayerWeaponUI ui = PlayerWeaponUI.Instance;
                if (ui == null) { HudIconState = "no weapon HUD"; return; }
                if (s_hudIcon == null)
                {
                    UIAtlas atlas = QuestPrompts.PlainAtlas();
                    string sn = QuestPrompts.SpriteName(UIManager.Instance, Settings.LeftHanded ? InControl.InputControlType.LeftStickButton : InControl.InputControlType.RightStickButton);
                    if (atlas == null || string.IsNullOrEmpty(sn) || atlas.GetSprite(sn) == null) { HudIconState = "no glyph (" + (sn ?? "-") + ")"; return; }
                    s_hudIcon = GameText.Sprite(ui.transform, "HoF2VR_AbilityButton", atlas, sn, Color.white, 60, 64, 64, UIBasicSprite.Type.Simple);
                    s_hudIcon.transform.localPosition = new Vector3(78f, -6f, 0f);
                    HudIconState = "made (" + sn + ")";
                    Plugin.L.LogInfo("SuperMeter: the weapon ability's button glyph beside the game's weapon HUD: " + sn);
                }
                bool on = ready && CombatSettings.Physical;
                if (s_hudIcon.gameObject.activeSelf != on) { s_hudIcon.gameObject.SetActive(on); if (on) HudIconShows++; }
            }
            catch (Exception e) { HudIconState = "error " + e.Message; s_hudIcon = null; }
        }

        /// A stick seen from above, pressed in (a ring, its cap, a small arrow down onto it) and the letter of its hand.
        static void StickIcon(float x, float y, bool right, float a)
        {
            IconFrames++;
            float pulse = 0.8f + 0.2f * Mathf.Sin(Time.unscaledTime * 5f);
            Color c = new Color(1f, 0.92f, 0.62f, a * pulse), dim = new Color(0.08f, 0.06f, 0.03f, 0.75f * a);
            const float R = 0.0042f;
            const int N = 12;
            // a dark disc behind it (read on any glove colour)
            s_b.Stroke(new Vector2(x - R * 0.9f, y), new Vector2(x + R * 0.9f, y), R * 2.6f, dim, 0.0008f, 0.0004f);
            for (int i = 0; i < N; i++)
            {
                float a0 = i * Mathf.PI * 2f / N, a1 = (i + 1) * Mathf.PI * 2f / N;
                s_b.Stroke(new Vector2(x + Mathf.Cos(a0) * R, y + Mathf.Sin(a0) * R), new Vector2(x + Mathf.Cos(a1) * R, y + Mathf.Sin(a1) * R), 0.0009f, c, 0.0004f, -0.0006f);
            }
            s_b.Stroke(new Vector2(x - 0.0011f, y), new Vector2(x + 0.0011f, y), 0.0024f, c, 0.0005f, -0.0006f);   // the cap
            // the press: a small chevron above the ring
            float ty = y + R + 0.0028f;
            s_b.Stroke(new Vector2(x - 0.0022f, ty + 0.0016f), new Vector2(x, ty), 0.0008f, c, 0.0003f, -0.0006f);
            s_b.Stroke(new Vector2(x + 0.0022f, ty + 0.0016f), new Vector2(x, ty), 0.0008f, c, 0.0003f, -0.0006f);
            // the hand's letter under it: L or R (4 mm)
            float lx = x - 0.0013f, by = y - R - 0.0062f, tyL = by + 0.004f;
            s_b.Stroke(new Vector2(lx, by), new Vector2(lx, tyL), 0.0007f, c, 0.0003f, -0.0006f);
            if (!right) { s_b.Stroke(new Vector2(lx, by), new Vector2(lx + 0.0024f, by), 0.0007f, c, 0.0003f, -0.0006f); return; }
            float mid = by + 0.0021f;
            s_b.Stroke(new Vector2(lx, tyL), new Vector2(lx + 0.0018f, tyL), 0.0007f, c, 0.0003f, -0.0006f);
            s_b.Stroke(new Vector2(lx + 0.0018f, tyL), new Vector2(lx + 0.0025f, tyL - 0.0009f), 0.0007f, c, 0.0003f, -0.0006f);
            s_b.Stroke(new Vector2(lx + 0.0025f, tyL - 0.0009f), new Vector2(lx + 0.0018f, mid), 0.0007f, c, 0.0003f, -0.0006f);
            s_b.Stroke(new Vector2(lx + 0.0018f, mid), new Vector2(lx, mid), 0.0007f, c, 0.0003f, -0.0006f);
            s_b.Stroke(new Vector2(lx + 0.0010f, mid), new Vector2(lx + 0.0026f, by), 0.0007f, c, 0.0003f, -0.0006f);
        }

        static void ComboValidPostfix(Ability __instance, ref bool __result)
        {
            if (!Physical.Live || __instance == null || __instance.AttackHitsRequired <= 0) return;
            __result = Full;
        }

        static void UsePostfix(Ability __instance)
        {
            if (!Physical.Live || __instance == null || __instance.AttackHitsRequired <= 0) return;
            Charge = 0f;
            Spends++;
            Last = "spent on " + __instance.name;
            // UYF's activation (docs/uyf-parity-audit.md P30, F2): a white flash 0.2 for 0.2 s, a shake 0.1 s / 0.1, a 0.6 s
            // jagged haptic on the weapon hand
            try
            {
                if (CombatSettings.ScreenFlash) ScreenFlash.Show(Color.white, 0.2f, 0.2f, "super");
                CameraShake.Add(0.1f, 0.1f, 0.02f, "super");
                if (s_jagged == null) s_jagged = JaggedHaptic();
                Activations++;
                if (WeaponTrail.Host != null) WeaponTrail.Host.StartCoroutine(s_jagged.Clone().Run());
            }
            catch (Exception e) { Plugin.L.LogWarning("SuperMeter: activation: " + e.Message); }
        }

        public static int Activations;
        static JaggedPattern s_jagged;

        /// The super's 0.6 s jagged rumble: twelve 50 ms pulses, strong and weak in turn, on the weapon hand.
        sealed class JaggedPattern
        {
            public JaggedPattern Clone() => new JaggedPattern();
            public System.Collections.IEnumerator Run()
            {
                bool left = Settings.LeftHanded;
                for (int i = 0; i < 12; i++)
                {
                    Haptics.Pulse(left, i % 2 == 0 ? 1f : 0.45f, 0.045f);
                    yield return new WaitForSecondsRealtime(0.05f);
                }
            }
        }
        static JaggedPattern JaggedHaptic() => new JaggedPattern();
    }
}

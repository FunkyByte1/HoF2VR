using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The way into and out of a fight (user, 2026-10-12 evening: "While I was at the campfire, I heard enemy noises and
    /// fight music before I had pressed A to leave the campfire"; "After combat ends and the portal appears, can the combat
    /// music stop playing and we play some other music?"; and a "blip" at the campfire: the user's log has a 95-125 ms stall
    /// in every fight entry while the arena activated behind the campfire).
    ///  * Arena hold (Comfort.HoldArena): GameState_Level_In loads the arena with activation allowed at once; in VR, while the
    ///    waiting space shows, the activation waits (the scene loads to 90 % - disk only, no stalls - and stays inactive: no
    ///    enemies, no sounds). The gate's "Press (A)" comes when the data is in; A covers the space in plain mist, then the
    ///    arena activates and Level_In goes on behind the mist (its stalls hidden: a frozen frame of plain mist can't judder),
    ///    and the place hold reveals the fight as before.
    ///  * At the campfire before a fight the game's music and effects are faded out (the table's music and sounds would
    ///    otherwise play on there; the camp's own fire and night sounds bypass the game's mixer); they come back at the reveal.
    ///  * After a win, when the exit portal is about to open: the fight music stops (the game's own post-combat step) and the
    ///    music that played at the Dealer's table before the fight comes back, calm, until the player leaves.
    internal static class FightFlow
    {
        // ---- the arena hold ------------------------------------------------------------------------------------------------
        static AssetBundleLoadLevelOperation s_held;
        static bool s_bypass, s_activating;
        static float s_heldAt = -1f;
        public static int Holds, Activations;
        public static string Last = "-";

        public static bool Wanted => XRSettings.enabled && Settings.HoldArena && Settings.PressToContinue && Settings.WaitingSpace;
        /// An arena is loaded and waits for the player's A.
        public static bool Held => s_held != null;
        /// Its data is in: the gate may say "Press (A)".
        public static bool Ready => s_held != null && (s_held.Request == null ? false : s_held.Request.progress >= 0.89f) && !SlowedNow;
        /// How far the held arena's data is in, 0..1 (1 = Ready); 0 while nothing is held or its request has not begun.
        public static float LoadFraction => s_held == null || s_held.Request == null ? 0f :
            Mathf.Min(Mathf.Clamp01(s_held.Request.progress / 0.89f), SlowedNow ? 0.95f * (Time.unscaledTime - s_heldAt) / TestSlowLoad : 1f);
        /// A was pressed for a held arena: the next place release is that fight's reveal (no second gate).
        public static bool PreGone;
        /// The hold patch is in (false: the arena is never held, the fight card can't show the load).
        public static bool HoldPatched;
        /// Item 30 (the user, 2026-10-18 ~22:30: once the fight card says ready, touching it "skip[s] the waiting space and
        /// go[es] straight into the fight": "Yes"): the card was touched (or A) once its arena had loaded - the arena activates
        /// in the place change's own dark (no campfire), the fight waits in Level_In until the reveal (WaitGate.HoldFight), and
        /// the waiting space is not shown for this hold (Limbo.SpaceForThisHold). Until the reveal (WaitGate.MayRelease).
        public static bool Direct { get; private set; }
        static float s_directAt = -1f;
        public static int Directs;
        /// Test runs: the held arena counts as loaded only this long after its hold began (`fightcard slowload <s>`; 0 = off) -
        /// the simulator loads within the card's glide, so its "touched before ready" path needs this.
        public static float TestSlowLoad;
        static bool SlowedNow => TestSlowLoad > 0f && TestMode.Enabled && s_held != null && Time.unscaledTime - s_heldAt < TestSlowLoad;

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.PropertySetter(typeof(AssetBundleLoadLevelOperation), "AllowSceneActivation"),
                    prefix: new HarmonyMethod(typeof(FightFlow), nameof(AllowActivationPrefix)));
                HoldPatched = true;
            }
            catch (Exception e) { Plugin.L.LogWarning("FightFlow: arena hold patch failed (the arena activates behind the campfire as before): " + e.Message); }
            Diag.StatusExtras["fightflow"] = () => Diag.Q(Describe());
            // `music list [part]`: the game's music sets (loaded) -> log; `music play <name part>`: replace the music with it
            Diag.Commands["music"] = a =>
            {
                string op = a.Length > 1 ? a[1] : "";
                MusicManager mm = MusicManager.Instance;
                if (op == "list")
                {
                    var sb = new System.Text.StringBuilder();
                    int n = 0;
                    foreach (SoundSet ss in Resources.FindObjectsOfTypeAll<SoundSet>())
                    {
                        if (ss == null || (a.Length > 2 && ss.name.IndexOf(a[2], StringComparison.OrdinalIgnoreCase) < 0)) continue;
                        if (ss.name.IndexOf("MUSIC", StringComparison.OrdinalIgnoreCase) < 0 && a.Length < 3) continue;
                        sb.Append("\n  ").Append(ss.name);
                        n++;
                    }
                    Plugin.L.LogInfo("music sets: " + n + sb);
                    return n + " (see log); now " + (mm != null && mm.CurrentMusic != null ? mm.CurrentMusic.name : "-");
                }
                if (op == "play" && a.Length > 2 && mm != null)
                {
                    foreach (SoundSet ss in Resources.FindObjectsOfTypeAll<SoundSet>())
                        if (ss != null && ss.name.IndexOf(a[2], StringComparison.OrdinalIgnoreCase) >= 0) { mm.Replace(ss); return "playing " + ss.name; }
                    return "no music " + a[2];
                }
                return "now " + (mm != null && mm.CurrentMusic != null ? mm.CurrentMusic.name : "-") + " | " + MusicNote;
            };
        }

        public static string Describe() =>
            (Direct ? "straight in " + (Time.unscaledTime - s_directAt).ToString("0.0") + " s | " : "") +
            (Held ? "arena held " + (Time.unscaledTime - s_heldAt).ToString("0.0") + " s, progress " + (s_held.Request != null ? s_held.Request.progress.ToString("0.00") : "-") : "no arena held") +
            " | holds " + Holds + " activations " + Activations + " | campfire frames " + CampfireFrames + ", stalls >= 40 ms " + CampfireStalls + " (worst " + CampfireWorst.ToString("0") + " ms)" +
            " | ducked " + s_ducked + " | music " + MusicNote + " | showcase fixes " + VRUI.ShowcaseFixes +
            (VRUI.I != null && VRUI.I.Panel != null ? " panel " + Diag.V(VRUI.I.Panel.position) : "") + " | last " + Last;

        static bool AllowActivationPrefix(AssetBundleLoadLevelOperation __instance, bool value)
        {
            if (!value || s_bypass) return true;
            try
            {
                // (at the campfire, or still at the table: B1, the encounter card to touch - FightCard)
                if (!Wanted || GameWatch.State != "GameState_Level_In" || !(Limbo.On || FightCard.HoldsArena)) return true;
                if (s_held != null && s_held != __instance) return true;   // only one
                s_held = __instance;
                s_heldAt = Time.unscaledTime;
                PreGone = false;
                Holds++;
                Last = "held";
                Plugin.L.LogInfo("fight flow: the arena waits for the player's A (it loads, but stays inactive behind the campfire) at " + Diag.Clock());
                return false;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("fight flow: hold: " + e.Message);
                return true;
            }
        }

        /// The player's A at the gate for a held arena: the space goes to plain mist, then the arena activates.
        public static void Go(string why)
        {
            if (s_held == null || s_activating) return;
            PreGone = true;
            Limbo.ArenaVeil = true;
            s_activating = true;
            if (Diag.I != null) Diag.I.StartCoroutine(ActivateUnderVeil(why));
            else Activate(why);
        }

        /// Item 30: the fight card touched once its arena had loaded - straight into the fight. The arena activates once the
        /// place change's dark is full (its frozen frames then show plain dark), with no waiting space in between; false when
        /// nothing is held (the caller takes the waiting space as before).
        public static bool GoDirect(string why)
        {
            if (s_held == null || s_activating) return false;
            Direct = true;
            s_directAt = Time.unscaledTime;
            Directs++;
            PreGone = true;
            s_activating = true;
            Plugin.L.LogInfo("fight flow: straight into the fight (" + why + "): no waiting space; the arena activates in the dark (" + Diag.Clock() + ")");
            if (Diag.I != null) Diag.I.StartCoroutine(ActivateInDark(why));
            else Activate(why);
            return true;
        }

        static IEnumerator ActivateInDark(string why)
        {
            float t0 = Time.unscaledTime;
            // (Places takes its hold the frame the card leaves the table; the fade reaches full dark in VRFade.HoldOutTime)
            while (!(VRFade.IsBlack && VRFade.I.IsHeldBy(Places.HoldOwner)) && Time.unscaledTime - t0 < 2f) yield return null;
            yield return null;   // one whole frame of dark before the activation's frozen frames
            Activate(why + ", " + (Time.unscaledTime - t0).ToString("0.00") + " s after the touch's flare");
        }

        /// The fight is revealed (WaitGate.MayRelease), or the way in ended otherwise.
        public static void DirectDone(string why)
        {
            if (!Direct) return;
            Direct = false;
            Plugin.L.LogInfo("fight flow: straight in - " + why + " (" + (Time.unscaledTime - s_directAt).ToString("0.0") + " s after the touch's flare)");
        }

        static IEnumerator ActivateUnderVeil(string why)
        {
            float t0 = Time.unscaledTime;
            while (Limbo.On && Limbo.Cover < 1f && Time.unscaledTime - t0 < 1.0f) yield return null;
            yield return null;   // one whole frame of mist before the activation's frozen frames
            Activate(why);
        }

        static void Activate(string why)
        {
            AssetBundleLoadLevelOperation op = s_held;
            s_held = null;
            s_activating = false;
            if (op == null) return;
            PerfMonitor.Event("arena-activate");
            s_bypass = true;
            try { op.AllowSceneActivation = true; }
            catch (Exception e) { Plugin.L.LogWarning("fight flow: activate: " + e.Message); }
            finally { s_bypass = false; }
            Activations++;
            Last = "activated (" + why + ")";
            Plugin.L.LogInfo("fight flow: the arena activates now, " + (Direct ? "in the dark" : "behind the mist") + " (" + why + ", held " + (Time.unscaledTime - s_heldAt).ToString("0.0") + " s) at " + Diag.Clock());
        }

        /// Never hang: a held arena that nobody releases (the gate off, the space gone, the state changed) activates by itself.
        static string s_lastState = "";
        /// Frames of 40 ms or more while the campfire is in view before a fight (the user's "blip"; the target is none).
        public static int CampfireStalls, CampfireFrames;
        public static float CampfireWorst;

        public static void Tick()
        {
            if (Limbo.On && !Limbo.ArenaVeil && Limbo.Cover < 0.5f && (GameWatch.State == "GameState_Level_In" || GameWatch.State == "GameState_BossBattle_In"))
            {
                CampfireFrames++;
                float ms = Time.unscaledDeltaTime * 1000f;
                // (a test's eye capture stalls frames by itself: not counted)
                if (CampfireFrames > 3 && ms >= 40f && !Diag.Capturing)
                {
                    CampfireStalls++;
                    CampfireWorst = Mathf.Max(CampfireWorst, ms);
                    Plugin.L.LogInfo("fight flow: a " + ms.ToString("0") + " ms frame while the campfire was in view");
                }
            }
            string st = GameWatch.State;
            // the music at the dungeon map (calm; the encounter before a fight plays its tension music) is the one to come
            // back after a win
            if (st == "GameState_Map" && Time.frameCount % 30 == 0 && Diag.CurrentMenu() == "Map") RememberTableMusic();
            s_lastState = st;
            // (never left on: a way in that ended without its reveal - the state went on, or a minute passed)
            if (Direct && (Time.unscaledTime - s_directAt > 60f || st != "GameState_Level_In" && st != "GameState_Level_Play" && (VRFade.I == null || !VRFade.I.IsHeldBy(Places.HoldOwner))))
                DirectDone("ended without its reveal (" + st + ")");
            if (s_held != null && !s_activating)
            {
                bool lost = !(Limbo.On || FightCard.HoldsArena) || GameWatch.State != "GameState_Level_In" || !Settings.PressToContinue;
                if (lost) Go("the waiting space or the gate went away");
                else if (Time.unscaledTime - s_heldAt > 600f) Go("held for 10 minutes");
            }
            DuckTick();
            LimboHands.KeepTick();
            ExitPortal.PrepareTick();
        }

        // ---- the campfire's quiet ------------------------------------------------------------------------------------------
        static bool s_ducked;
        static float s_musicDb, s_sfxDb;
        static float s_duckLevel = 1f;
        const float DuckSeconds = 1.2f;

        /// Before a fight while the waiting space shows: the game's music and effects fade out; back at the reveal.
        static void DuckTick()
        {
            bool want = Wanted && Limbo.On && (GameWatch.State == "GameState_Level_In" || GameWatch.State == "GameState_BossBattle_In");
            AudioMixer mx = SoundManager.Instance != null ? SoundManager.Instance.Mixer : null;
            if (mx == null) { s_ducked = false; s_duckLevel = 1f; return; }
            if (want && !s_ducked)
            {
                s_ducked = true;
                s_duckLevel = 1f;
                mx.GetFloat("MusicVolume", out s_musicDb);
                mx.GetFloat("SFXVolume", out s_sfxDb);
                Plugin.L.LogInfo("fight flow: the game's music and effects fade out at the campfire (music " + s_musicDb.ToString("0") + " dB, effects " + s_sfxDb.ToString("0") + " dB)");
            }
            if (!s_ducked) return;
            float target = want ? 0f : 1f;
            s_duckLevel = Mathf.MoveTowards(s_duckLevel, target, Time.unscaledDeltaTime / DuckSeconds);
            // decibels from a linear gain (0 -> -80 dB)
            float g = Mathf.Max(1e-4f, s_duckLevel);
            float off = 20f * Mathf.Log10(g);
            mx.SetFloat("MusicVolume", Mathf.Max(-80f, s_musicDb + off));
            mx.SetFloat("SFXVolume", Mathf.Max(-80f, s_sfxDb + off));
            if (!want && s_duckLevel >= 1f)
            {
                mx.SetFloat("MusicVolume", s_musicDb);
                mx.SetFloat("SFXVolume", s_sfxDb);
                s_ducked = false;
                Plugin.L.LogInfo("fight flow: the game's music and effects are back");
            }
        }

        // ---- calm music after the win ---------------------------------------------------------------------------------------
        static SoundSet s_tableMusic;
        static string MusicNote = "-";

        /// The fight begins (Level_In's first frame): the music playing at the table is remembered.
        public static void RememberTableMusic()
        {
            try
            {
                MusicManager mm = MusicManager.Instance;
                SoundSet cur = mm != null ? mm.CurrentMusic : null;
                if (cur != null) { s_tableMusic = cur; MusicNote = "table music '" + cur.name + "' kept"; }
            }
            catch (Exception) { }
        }

        /// The fight is won and the exit portal is about to open: the fight's music ends (the game's own post-combat step,
        /// which also stops its escalation), then the table's music comes back, calm, until the player leaves.
        public static void CalmMusic()
        {
            if (!XRSettings.enabled || !Settings.CalmAfterWin) return;
            try
            {
                EncounterMusicManager em = EncounterMusicManager.Instance;
                if (em != null) em.PostCombat(true);
                MusicManager mm = MusicManager.Instance;
                if (mm != null && s_tableMusic != null)
                {
                    mm.Replace(s_tableMusic);
                    MusicNote = "after the win: '" + s_tableMusic.name + "' (the table's)";
                }
                else MusicNote = "after the win: the game's post-combat music (no table music known)";
                Plugin.L.LogInfo("fight flow: " + MusicNote);
            }
            catch (Exception e) { Plugin.L.LogWarning("fight flow: calm music: " + e.Message); }
        }
    }
}

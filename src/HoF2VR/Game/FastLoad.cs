using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Shorter waits in VR (user 2026-10-11: "These two seem like they should be next up, since they will make every
    /// future testing session faster"): only what the waiting space hides anyway. Settings Comfort.FastLoading.
    /// Measured with `timeline` (Diag/Timeline.cs) on the boot of 2026-10-11 18:05 (21.6 s):
    ///  * Splash logos (Loader): the scenes are loaded long before they may activate - activation waits for the splash
    ///    sequence (two logos x (fade + 2 s) + the loading menu: 8.3 s). Skipped: the loading menu is pushed at once
    ///    and Loader.Start's first wait (half the 2 s splash interval) is cut to 0.05 s.
    ///  * The title's camera move ("StartScreen_Ready" until its "ready" event: 3.0 s) runs at TitleSpeed x, and the logo
    ///    fade-in that follows (3.6 s until the menu, which the boot hold waits for) at LogoScale of its times.
    /// Measured the same way on a fight entry with an enemy showcase (13.7 s):
    ///  * The deal (Level_In's cards flying into the portal, 4.5 s) and the portal tunnel happen while the waiting space
    ///    is up: the deal's timings (TransitionInTimes) run at DealScale, the tunnel's fades (MeshWormhole's 1.5 s exit
    ///    fade speed: the portal's fade-in, its fade-out and the arena camera's fade-in) take TunnelFade seconds.
    ///  * The showcase: StartTransitionOut waited 1 s and then 4.0 s for the feature card's flight in the tunnel
    ///    (MeshWormhole.DisplayFeatureCard) - the card is put where that flight ends instead; the 1 s wait for the
    ///    tunnel's cards (every fight) is skipped too.
    internal static class FastLoad
    {
        public static float TitleSpeed = 6f, LogoScale = 0.15f, DealScale = 0.25f, TunnelFade = 0.4f;
        public static int SplashesSkipped, TitleSpeedUps, LogoFades, DealSpeedUps, ShowcaseSkips, TunnelCardSkips;
        /// LoadProgress picks the plain fight_in plan when the showcase card's wait is skipped.
        public static bool SkipsShowcase => Active;
        static bool s_logged;

        static bool Active => Settings.FastLoading && (XRSettings.enabled || VRWanted);
        /// The start's logo screens and title intro (what a player would see on the boot panel and behind the space):
        /// a development shortcut only (user 2026-10-11: "I don't want to have them disabled for a shipping version.
        /// But it would make testing faster") - [Dev] SkipIntros, or HOF2VR_SKIPINTROS=1 in a test run (run.sh).
        /// (A test run's HOF2VR_SKIPINTROS=0/1 wins over the settings file, which test runs share with the player.)
        public static bool SkipsIntros => (XRSettings.enabled || VRWanted) && (s_env == "1" || (s_env != "0" && Settings.SkipIntros));
        static readonly string s_env = TestMode.Enabled ? Environment.GetEnvironmentVariable("HOF2VR_SKIPINTROS") : null;
        /// XR may come up a moment after the first scene: the launcher's VR switch counts too.
        static bool VRWanted => !string.IsNullOrEmpty(XRSettings.loadedDeviceName) && XRSettings.loadedDeviceName != "None";

        static readonly FieldInfo s_logoFade = AccessTools.Field(typeof(StartScreenController), "m_logoFadeTime");
        static readonly FieldInfo s_2Fade = AccessTools.Field(typeof(StartScreenController), "m_2FadeTime");
        static readonly AccessTools.FieldRef<StartScreenController, AnimatedProp> s_sequence = AccessTools.FieldRefAccess<StartScreenController, AnimatedProp>("m_sequence");
        static object s_saveLogo, s_save2;
        static Animator s_fast;

        public static void Apply(Harmony h)
        {
            Try(h, typeof(Loader), "NextSplash", nameof(NextSplashPrefix), null);
            Try(h, typeof(Loader), "Start", nameof(LoaderStartPrefix), null);
            Try(h, typeof(StartScreenController), "SetSequence", null, nameof(SetSequencePostfix));
            Try(h, typeof(StartScreenController), "OnAnimationEvent", nameof(AnimationEventPrefix), null);
            Try(h, typeof(StartScreenController), "TransitionLogo", nameof(TransitionLogoPrefix), nameof(TransitionLogoPostfix));
            Try(h, typeof(MenuStack.Transition), "Update", nameof(MenuTimerPrefix), null);
            Try(h, typeof(GameState_Level_In), "AnimateCardsIntoWormhole", nameof(DealPrefix), null);
            Try(h, typeof(MeshWormhole), "TransitionUp", nameof(WormholeFadePrefix), null);
            Try(h, typeof(MeshWormhole), "StartTransitionOut", nameof(TunnelOutPrefix), null);
            Diag.StatusExtras["fastLoad"] = () => Diag.Q((Settings.FastLoading ? "on" : "off") + " intros " + (SkipsIntros ? "skipped" : "played") + " splashes skipped " + SplashesSkipped + " title x" + TitleSpeedUps + " title outs " + TitleOuts + " logo " + LogoFades +
                                                          " deals " + DealSpeedUps + " showcase cards " + ShowcaseSkips + " tunnel cards " + TunnelCardSkips);
        }

        static void Try(Harmony h, Type t, string method, string prefix, string postfix)
        {
            try
            {
                h.Patch(AccessTools.Method(t, method),
                        prefix: prefix != null ? new HarmonyMethod(typeof(FastLoad), prefix) : null,
                        postfix: postfix != null ? new HarmonyMethod(typeof(FastLoad), postfix) : null);
            }
            catch (Exception e) { Plugin.L.LogWarning("FastLoad: " + t.Name + "." + method + " not patched: " + e.Message); }
        }

        // ---- the splash logos --------------------------------------------------------------------------

        static void LoaderStartPrefix(ref float ___m_timerInterval)
        {
            if (!SkipsIntros) { Plugin.L.LogInfo("FastLoad: the start at the game's pace (VR " + XRSettings.enabled + " '" + XRSettings.loadedDeviceName + "', [Dev] SkipIntros " + Settings.SkipIntros + ")"); return; }
            ___m_timerInterval = 0.1f;
        }

        static void NextSplashPrefix(ref int ___m_splashIndex, List<SplashMenu> ___m_splashSequence)
        {
            if (!SkipsIntros || ___m_splashSequence == null || ___m_splashIndex >= ___m_splashSequence.Count) return;
            SplashesSkipped += ___m_splashSequence.Count - ___m_splashIndex;
            ___m_splashIndex = ___m_splashSequence.Count;
            if (!s_logged) { s_logged = true; Plugin.L.LogInfo("FastLoad: the splash logos are skipped (behind the waiting space): the game's scenes activate as soon as they are loaded"); }
        }

        /// The boot's menu changes (the start screen's init menu: 2.0 s) count at MenuSpeed x while the boot's waiting space
        /// is up: the game waits for a menu stack's timer, not for what its tweens show.
        public static float MenuSpeed = 10f;
        public static int MenuSpeedFrames;

        static void MenuTimerPrefix(ref float a_delta)
        {
            if (s_titleOut)
            {
                // leaving the title: the out menu's 2.0 s timer, behind the black
                if (GameWatch.State != "GameState_StartScreen") s_titleOut = false;
                else if (VRFade.IsBlack) { a_delta *= MenuSpeed; MenuSpeedFrames++; return; }
            }
            if (!SkipsIntros || !Limbo.On) return;
            string st = GameWatch.State;
            if (st != "" && st != "GameState_Init") return;
            a_delta *= MenuSpeed;
            MenuSpeedFrames++;
        }

        // ---- the title's intro ----------------------------------------------------------------------------

        static void SetSequencePostfix(StartScreenController __instance, StartScreenController.StartSequence a_sequence)
        {
            Restore();
            if (TitleOut(__instance, a_sequence)) return;
            if (!SkipsIntros || !Limbo.On) return;
            if (a_sequence != StartScreenController.StartSequence.StartScreen_Ready && a_sequence != StartScreenController.StartSequence.StartScreen_FoolVignette) return;
            AnimatedProp p = s_sequence(__instance);
            Animator an = p != null ? p.Animator : null;
            if (an == null) return;
            an.speed = TitleSpeed;
            s_fast = an;
            TitleSpeedUps++;
            Plugin.L.LogInfo("FastLoad: the title's " + a_sequence + " at " + TitleSpeed + "x (behind the waiting space)");
        }

        static void AnimationEventPrefix(AnimationEvent a_event)
        {
            if (a_event != null && (a_event.stringParameter == "ready" || a_event.stringParameter == "done")) Restore();
        }

        // ---- leaving the title ----------------------------------------------------------------------------
        /// Continue / New Game / a loaded save at the title: on a monitor the title's out animation (1.1 s) and the out menu's
        /// stack timer (2.0 s) play before the game moves on; in VR that was ~3 s of nothing happening after the press. Now the
        /// view fades out at once (the place hold, as for every trip) and, once it is black, both run fast behind it.
        public static float TitleOutSpeed = 6f;
        public static int TitleOuts;
        public static float TitleOutAt = -1f;
        static bool s_titleOut;

        static bool TitleOut(StartScreenController c, StartScreenController.StartSequence s)
        {
            if (!Active || !XRSettings.enabled || VRFade.I == null) return false;
            if (s != StartScreenController.StartSequence.StartScreen_Ready_Out && s != StartScreenController.StartSequence.StartScreen_LoadGame_Out &&
                s != StartScreenController.StartSequence.StartScreen_NewGame_Out && s != StartScreenController.StartSequence.StartScreen_FoolVignette_Out) return false;
            s_titleOut = true;
            TitleOuts++;
            TitleOutAt = Time.unscaledTime;
            Places.Depart("leaving the title: " + s);
            AnimatedProp p = s_sequence(c);
            Animator an = p != null ? p.Animator : null;
            VRFade.I.StartCoroutine(FastWhenBlack(an));
            Plugin.L.LogInfo("FastLoad: leaving the title (" + s + "): the view fades now; behind the black the out animation at " + TitleOutSpeed +
                             "x and the out menu's timer at " + MenuSpeed + "x");
            return true;
        }

        static System.Collections.IEnumerator FastWhenBlack(Animator an)
        {
            float t0 = Time.unscaledTime;
            while (!VRFade.IsBlack && Time.unscaledTime - t0 < 1.5f) yield return null;
            if (an == null || !s_titleOut || !VRFade.IsBlack) yield break;
            an.speed = TitleOutSpeed;
            s_fast = an;
        }

        static void Restore()
        {
            if (s_fast != null) { s_fast.speed = 1f; s_fast = null; }
        }

        static void TransitionLogoPrefix(StartScreenController __instance, bool a_show)
        {
            s_saveLogo = s_save2 = null;
            if (!a_show || !SkipsIntros || !Limbo.On || s_logoFade == null || s_2Fade == null) return;
            s_saveLogo = s_logoFade.GetValue(__instance);
            s_save2 = s_2Fade.GetValue(__instance);
            s_logoFade.SetValue(__instance, Scaled(s_logoFade.GetValue(__instance), LogoScale));
            s_2Fade.SetValue(__instance, Scaled(s_2Fade.GetValue(__instance), LogoScale));
            LogoFades++;
        }

        static void TransitionLogoPostfix(StartScreenController __instance)
        {
            if (s_saveLogo == null) return;
            s_logoFade.SetValue(__instance, s_saveLogo);
            s_2Fade.SetValue(__instance, s_save2);
            s_saveLogo = s_save2 = null;
        }

        // ---- fight entries and exits ----------------------------------------------------------------------

        static GameState_Level_In.TransitionInTimes? s_gameTimes;
        static float s_gameExitFade = -1f;

        /// Level_In's deal: every timing at DealScale in VR (the waiting space is up from the start of Level_In).
        static void DealPrefix(ref GameState_Level_In.TransitionInTimes ___m_transitionTimes)
        {
            if (!s_gameTimes.HasValue) s_gameTimes = ___m_transitionTimes;
            GameState_Level_In.TransitionInTimes t = s_gameTimes.Value;
            // (B1: the player watches this deal at the table - the game's own pace)
            if (Active && !FightCard.WillStay())
            {
                float k = DealScale;
                t.EncounterFlightTime *= k; t.EncounterPauseTime *= k; t.LeftTime *= k; t.RightTime *= k;
                t.FeatureTime *= k; t.CardSplitTime *= k; t.WormholeDelay *= k;
                DealSpeedUps++;
            }
            ___m_transitionTimes = t;
        }

        static void WormholeFadePrefix(ref float ___m_wormholeExitFadeSpeed)
        {
            if (s_gameExitFade < 0f) s_gameExitFade = ___m_wormholeExitFadeSpeed;
            ___m_wormholeExitFadeSpeed = Active ? Mathf.Min(s_gameExitFade, TunnelFade) : s_gameExitFade;
        }

        /// The tunnel's end: no wait for its cards' exit and the showcase card's flight (the card goes where the flight
        /// would have put it: onto the tunnel's feature prop).
        static void TunnelOutPrefix(ref List<Card> ___mCardList, ref Card ___mFeatureCard, AnimatedProp ___m_featuredCardProp, ref float ___m_wormholeExitFadeSpeed)
        {
            WormholeFadePrefix(ref ___m_wormholeExitFadeSpeed);
            if (!Active) return;
            if (___mFeatureCard != null)
            {
                if (___m_featuredCardProp != null)
                {
                    ___mFeatureCard.transform.SetParent(___m_featuredCardProp.transform, false);
                    ___mFeatureCard.transform.localPosition = Vector3.zero;
                    ___mFeatureCard.transform.localRotation = Quaternion.identity;
                }
                ___mFeatureCard = null;
                ShowcaseSkips++;
                Plugin.L.LogInfo("FastLoad: the showcase card's flight in the tunnel skipped (behind the waiting space)");
            }
            if (___mCardList != null && ___mCardList.Count > 0)
            {
                ___mCardList = new List<Card>();   // a new list: the game's own stays as it is
                TunnelCardSkips++;
            }
        }

        /// A copy of a FadeTimes struct (boxed) with every time scaled.
        static object Scaled(object box, float k)
        {
            foreach (FieldInfo f in box.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (f.FieldType == typeof(float)) f.SetValue(box, (float)f.GetValue(box) * k);
            return box;
        }
    }
}

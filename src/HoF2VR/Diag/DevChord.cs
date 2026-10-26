using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The dev gestures' button chord: **B (right controller) + Y (left controller) held together**.
    /// (User 2026-10-12 evening: "Pressing both thumbsticks opens the WiVRn menu, so I couldn't really test that." WiVRn's
    /// client toggles its window on both stick clicks - client/scenes/stream.cpp plots_toggle_1/2 - and while the window is
    /// up it reads the sticks, A/B and the grips; the Quest keeps only the Oculus button. With the window hidden nothing
    /// there reads B or Y, so B + Y is free on the headset's side.)
    ///  * held 0.5 s: a short tick on both hands; let go before 1.5 s = a bug mark (Dev.BugMark);
    ///  * held 1.5 s: the jump menu (Dev.JumpMenu).
    /// The game must not see the chord either (B = back in menus, Y = the nav bar's F2; in fights the bash and the prone
    /// finisher). So while either dev setting is on, a B or Y press waits up to `Window` for its partner: alone, it reaches
    /// the game that much later (a quick tap is replayed for a few frames); together, neither reaches the game until both
    /// are up. Buttons the jump card used (`Consume`) are kept from the game until they are let go.
    internal static class DevChord
    {
        public const float Window = 0.1f, MarkHold = 0.5f, JumpHold = 1.5f;

        /// B and Y as the game (and the mod's own B readers) should see them this frame, and their rising edges.
        public static bool B, Y, BDown, YDown;
        /// A and X (right A, left X) are only held back after the jump card used them.
        public static bool SwallowA, SwallowX;
        /// Both held together since (unscaled time); -1 = no chord.
        public static float ChordSince = -1f;
        /// The chord that ended this frame and how long it was held (for the bug mark).
        public static bool EndedNow;
        public static float EndedHeld;
        public static int Chords, Delayed, Replayed;   // diagnostics (status "devChord")

        sealed class Btn { public float DownAt = -1f; public bool Pass, Swallow; public int Replay; }
        static readonly Btn s_b = new Btn(), s_y = new Btn();
        static int s_frame = -1;
        static bool s_prevB, s_prevY;

        public static bool Enabled => XRSettings.enabled && (Settings.BugMark || Settings.JumpMenu);
        public static float Held => ChordSince < 0f ? 0f : Time.unscaledTime - ChordSince;

        public static void Register()
        {
            Diag.StatusExtras["devChord"] = () => Diag.Q((Enabled ? "on" : "off") + ", chords " + Chords + ", delayed " + Delayed + ", replayed " + Replayed +
                                                          (ChordSince >= 0f ? ", held " + Held.ToString("0.00") + " s" : ""));
        }

        /// Runs once per frame, whoever asks first (VRPad, BugMark, the VR settings page).
        public static void Tick()
        {
            if (s_frame == Time.frameCount) return;
            s_frame = Time.frameCount;
            bool rawB = VRInput.R.B, rawY = VRInput.L.B;
            float now = Time.unscaledTime;
            EndedNow = false;
            if (!VRInput.R.A) SwallowA = false;
            if (!VRInput.L.A) SwallowX = false;
            if (!Enabled)
            {
                B = rawB; Y = rawY;
                ChordSince = -1f;
                Reset(s_b); Reset(s_y);
            }
            else if (ChordSince >= 0f)
            {
                // a chord: nothing reaches the game until both are up
                B = Y = false;
                if (!rawB && !rawY)
                {
                    EndedNow = true;
                    EndedHeld = now - ChordSince;
                    ChordSince = -1f;
                    Reset(s_b); Reset(s_y);
                }
            }
            else
            {
                Edge(s_b, rawB);
                Edge(s_y, rawY);
                if (rawB && rawY && !s_b.Pass && !s_y.Pass && !s_b.Swallow && !s_y.Swallow)
                {
                    ChordSince = Mathf.Max(s_b.DownAt, s_y.DownAt);
                    Chords++;
                    B = Y = false;
                }
                else
                {
                    B = Out(s_b, rawB, now);
                    Y = Out(s_y, rawY, now);
                }
            }
            BDown = B && !s_prevB;
            YDown = Y && !s_prevY;
            s_prevB = B;
            s_prevY = Y;
        }

        /// The jump card used the face buttons held now: keep them from the game until they are let go.
        public static void Consume()
        {
            if (VRInput.R.B) { s_b.Swallow = true; s_b.Replay = 0; }
            if (VRInput.L.B) { s_y.Swallow = true; s_y.Replay = 0; }
            if (VRInput.R.A) SwallowA = true;
            if (VRInput.L.A) SwallowX = true;
        }

        static void Reset(Btn b) { b.DownAt = -1f; b.Pass = false; b.Swallow = false; b.Replay = 0; }

        static void Edge(Btn b, bool raw)
        {
            if (raw && b.DownAt < 0f) { b.DownAt = Time.unscaledTime; b.Pass = false; }
            else if (!raw && b.DownAt >= 0f)
            {
                // let go before the window ran out: replay the tap so the game still sees it
                if (!b.Pass && !b.Swallow) { b.Replay = 4; Replayed++; }
                b.DownAt = -1f;
                b.Pass = false;
                b.Swallow = false;
            }
        }

        static bool Out(Btn b, bool raw, float now)
        {
            if (raw)
            {
                if (b.Swallow) return false;
                if (!b.Pass && now - b.DownAt >= Window) { b.Pass = true; Delayed++; }
                return b.Pass;
            }
            if (b.Replay > 0) { b.Replay--; return b.Replay > 0; }
            return false;
        }
    }
}

using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The Dealer notices you (the user 2026-10-12 ~22:40, "I'd like to try it"; docs/decisions.md "Interaction"): his head
    /// follows the player, subtly, and meets their eyes when they look at him; now and then it goes to what matters on the
    /// table. `[Table] DealerGaze` (on).
    ///  * Where he looks (one target at a time, in this order): the player while he speaks (the narration or one of his
    ///    lines); a card the game shows (the zoom) for 1.5 s after it arrives; the pendulum or the wheel for its first 2 s;
    ///    the dice when their game starts (2 s) and while they roll (he follows them); the hero's figurine while it moves
    ///    (carried by the player or walking to a place); a reward as it appears (a reward card dealt, a token or coin)
    ///    for 1.5 s; what the player's fingertip is on, for 0.8 s after it starts touching something; otherwise the
    ///    player, with a 1.2 s glance at the board every 8-12 s (the user, 2026-10-12 22:45: "the card or dice in play, the
    ///    pendulum/wheel, a reward appearing, the figurine's move, a glance at what the player hovers").
    ///  * How: an offset on top of his animated head (his idle and gestures still show; DealerMotion writes it, 60 % on the
    ///    head and 40 % on the neck, with the narration's small nods), at most 30 degrees sideways and 15 up or down from
    ///    where the animation points his face - a target beyond that is followed to the edge and no further. Reaction 0.15 s
    ///    late, then a critically damped spring (3.5 rad/s: about half a second to settle); half as much while he deals.
    ///  * Off at the title, in fights and while the view is dark.
    internal static class DealerGaze
    {
        public static float MaxYaw = 30f, MaxPitch = 15f, Rate = 3.5f, Delay = 0.15f;
        public static float Yaw, Pitch;               // the offset now (degrees; + yaw = toward his right, + pitch = down)
        public static string Target = "-";
        public static int Glances, Follows;
        static float s_yv, s_pv;
        static Vector3 s_faceLocal;
        static Transform s_faceOf;
        static float s_tYaw, s_tPitch;
        static readonly float[] s_histT = new float[64];
        static readonly float[] s_histY = new float[64], s_histP = new float[64];
        static int s_hist;
        static float s_nextGlance = -1f, s_glanceUntil = -1f, s_eventUntil = -1f;
        static Vector3 s_eventAt;
        static string s_event = "";
        static Card s_lastZoom;
        static Transform s_lastTouch;
        static bool s_lastPend, s_lastWheel, s_lastDice;
        static Vector3 s_figWas;
        static bool s_figKnown;
        static int s_rewardCount = -1;
        static Token s_newToken;
        static readonly System.Collections.Generic.Dictionary<int, Vector3> s_tokenAt = new System.Collections.Generic.Dictionary<int, Vector3>();
        static float s_tokenSampleAt;
        /// A token (a challenge coin, an encounter's reward token) has just appeared (TableHud's Token.Start hook).
        public static void TokenAppeared(Token t) { if (t != null) s_newToken = t; }

        public static bool Wanted => Settings.DealerGaze && XRSettings.enabled && Places.AtCaravan && !CombatVR.Active && VRRig.I != null && !VRFade.IsBlack && !Limbo.On;

        public static string Describe() => string.Format(CultureInfo.InvariantCulture, "{0} | yaw {1:0.0} pitch {2:0.0} (target {3:0.0} / {4:0.0}) | glances {5} follows {6}",
            Wanted ? "on: " + Target : "off", Yaw, Pitch, s_tYaw, s_tPitch, Glances, Follows);

        /// Each frame from DealerMotion, after his animation (the head bone as animated): the offsets to add (0 when off:
        /// they spring back).
        public static void Tick(Dealer d, Transform head, float dt, float busy, bool speaking, out float yaw, out float pitch)
        {
            float wantY = 0f, wantP = 0f;
            if (Wanted && head != null && d != null)
            {
                Vector3 at = Choose(speaking);
                Face(head, d);
                Vector3 face = head.rotation * s_faceLocal;
                Vector3 to = at - head.position;
                // sideways: the angle in the floor's plane; up/down: the difference of the two elevations (+ = he nods down)
                Vector3 fFlat = new Vector3(face.x, 0f, face.z), tFlat = new Vector3(to.x, 0f, to.z);
                if (fFlat.sqrMagnitude > 1e-6f && tFlat.sqrMagnitude > 1e-6f)
                {
                    wantY = Vector3.SignedAngle(fFlat, tFlat, Vector3.up);
                    float eF = Mathf.Atan2(face.y, fFlat.magnitude) * Mathf.Rad2Deg, eT = Mathf.Atan2(to.y, tFlat.magnitude) * Mathf.Rad2Deg;
                    wantP = eF - eT;
                }
                // (the head is as his animation left it this frame: DealerMotion takes its own offset off first) - what is
                // added is the rest, within the cone
                wantY = Mathf.Clamp(wantY, -MaxYaw, MaxYaw);
                wantP = Mathf.Clamp(wantP, -MaxPitch, MaxPitch);
                wantY *= Mathf.Lerp(0.5f, 1f, busy);
                wantP *= Mathf.Lerp(0.5f, 1f, busy);
            }
            else Target = "-";
            // reaction delay: follow what was wanted Delay seconds ago
            float now = Time.unscaledTime;
            s_hist = (s_hist + 1) % s_histT.Length;
            s_histT[s_hist] = now; s_histY[s_hist] = wantY; s_histP[s_hist] = wantP;
            float ty = wantY, tp = wantP;
            for (int i = 0; i < s_histT.Length; i++)
            {
                int k = (s_hist - i + s_histT.Length) % s_histT.Length;
                if (now - s_histT[k] >= Delay) { ty = s_histY[k]; tp = s_histP[k]; break; }
            }
            s_tYaw = ty; s_tPitch = tp;
            int n = Mathf.Max(1, Mathf.CeilToInt(dt * 120f));
            float h = dt / n;
            for (int i = 0; i < n; i++)
            {
                Spring(ref Yaw, ref s_yv, ty, Rate, h);
                Spring(ref Pitch, ref s_pv, tp, Rate, h);
            }
            yaw = Yaw;
            pitch = Pitch;
        }

        static void Spring(ref float p, ref float v, float target, float w0, float dt)
        {
            float acc = w0 * w0 * (target - p) - 2f * w0 * v;
            v += acc * dt;
            p += v * dt;
        }

        /// The head bone's axis that points out of his face: the one closest to his body's forward (his idle faces the table).
        static void Face(Transform head, Dealer d)
        {
            if (s_faceOf == head) return;
            s_faceOf = head;
            Vector3 fwd = d.transform.forward;
            Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward, Vector3.left, Vector3.down, Vector3.back };
            float best = -2f;
            foreach (Vector3 a in axes)
            {
                float dot = Vector3.Dot(head.rotation * a, fwd);
                if (dot > best) { best = dot; s_faceLocal = a; }
            }
            Plugin.L.LogInfo("DealerGaze: his face is the head bone's " + s_faceLocal + " (" + best.ToString("0.00", CultureInfo.InvariantCulture) + " along his body's forward)");
        }

        /// What he looks at now (world point).
        static Vector3 Choose(bool speaking)
        {
            float now = Time.unscaledTime;
            Vector3 eyes = VRRig.I.HeadWorldPos;
            // new things on the table start a short look
            try
            {
                CardZoom z = UI3D.Instance != null ? UI3D.Instance.CardZoom : null;
                Card zc = z != null && z.Container != null && z.Container.Cards.Count > 0 ? z.Container.Cards[0] : null;
                if (zc != null && zc != s_lastZoom) Event("the card shown", zc.transform.position, 1.5f);
                s_lastZoom = zc;
                bool pend = PendulumTask.RunningTask != null && CardTable.Instance != null && CardTable.Instance.Pendulum != null;
                if (pend && !s_lastPend) Event("the pendulum", CardTable.Instance.Pendulum.transform.position, 2f);
                s_lastPend = pend;
                DeckManager dm = DeckManager.Instance;
                bool wheel = WheelSpinTask.RunningTask != null && dm != null && dm.WheelSpinContainer != null;
                if (wheel && !s_lastWheel) Event("the wheel", dm.WheelSpinContainer.transform.position, 2f);
                s_lastWheel = wheel;
                // the dice: a look when their game starts, then he follows them while they roll
                DiceRoller roller = ReachProbe.Roller();
                bool dice = DiceRollTask.RunningTask != null && roller != null;
                if (dice)
                {
                    Vector3 dp;
                    bool rolling = DiceAt(roller, out dp);
                    if (!s_lastDice) Event("the dice", dp, 2f);
                    else if (rolling) Event("the dice rolling", dp, 0.6f);
                }
                s_lastDice = dice;
                // the hero's figurine moving (carried by the player, or walking to the next place)
                Map map = Map.Instance;
                Transform fig = map != null && map.PlayerCounter != null ? map.PlayerCounter.transform : null;
                if (fig != null && fig.gameObject.activeInHierarchy)
                {
                    Vector3 fp = fig.position;
                    // moving faster than 0.1 m/s (carried, or walking to a place), by the clock (test runs draw fewer frames)
                    float fstep = 0.1f * VRRig.I.Scale * Mathf.Max(Time.unscaledDeltaTime, 1e-3f);
                    if (s_figKnown && (fp - s_figWas).sqrMagnitude > fstep * fstep) Event("the hero's figurine", fp, 1.0f);
                    s_figWas = fp;
                    s_figKnown = true;
                }
                else s_figKnown = false;
                // a reward appearing: a reward card dealt, or a token / coin
                int rewards = dm != null && dm.CardSetModifierContainer != null ? dm.CardSetModifierContainer.Cards.Count : 0;
                if (s_rewardCount >= 0 && rewards > s_rewardCount)
                {
                    Card rc = dm.CardSetModifierContainer.Cards[rewards - 1];
                    if (rc != null) Event("a reward", rc.transform.position, 1.5f);
                }
                s_rewardCount = rewards;
                if (s_newToken != null)
                {
                    if (s_newToken.isActiveAndEnabled) Event("a reward", s_newToken.transform.position, 1.5f);
                    s_newToken = null;
                }
                // a token rising or flying (the challenge's coin at its end, a reward token): the tokens exist from the run's
                // start, so it is their movement that shows a reward appearing
                Token[] toks = TableHud.Tokens;
                // moved more than 4 cm in half a second (a coin floating up, a token flying; a hovering token bobs a cm or
                // two): compared with where it was at the last half-second sample
                float far = 0.04f * VRRig.I.Scale;
                bool sample = now >= s_tokenSampleAt;
                if (sample) s_tokenSampleAt = now + 0.5f;
                for (int i = 0; i < toks.Length && i < 16; i++)
                {
                    Token tk = toks[i];
                    if (tk == null || !tk.isActiveAndEnabled) continue;
                    int id = tk.GetInstanceID();
                    Vector3 tp = tk.transform.position, was;
                    bool known = s_tokenAt.TryGetValue(id, out was);
                    if (known && (tp - was).sqrMagnitude > far * far) Event("a reward", tp, 1.0f);
                    if (sample || !known) s_tokenAt[id] = tp;
                }
                Transform touch = TouchInput.Touched(VRInput.R) ?? TouchInput.Touched(VRInput.L);
                if (touch != null && touch != s_lastTouch) Event("what the player touches", touch.position, 0.8f);
                s_lastTouch = touch;
            }
            catch (Exception) { }
            if (speaking) { Target = "the player (he speaks)"; return eyes; }
            if (now < s_eventUntil) { Target = s_event; return s_eventAt; }
            if (s_nextGlance < 0f) s_nextGlance = now + UnityEngine.Random.Range(8f, 12f);
            if (now >= s_nextGlance)
            {
                s_nextGlance = now + UnityEngine.Random.Range(8f, 12f);
                s_glanceUntil = now + 1.2f;
                Glances++;
            }
            if (now < s_glanceUntil)
            {
                // the board: the middle of the table, between him and the player (on its top: a seated player's eyes are
                // nearer to it than a standing one's)
                Target = "a glance at the board";
                Vector3 table = Vector3.Lerp(eyes, VRAudio.DealerHeadBone != null ? VRAudio.DealerHeadBone.position : eyes, 0.5f);
                Bounds tb;
                table.y = ReachProbe.TableBounds(out tb) ? tb.max.y : eyes.y - 0.75f * VRRig.I.Scale;
                return table;
            }
            Target = "the player";
            return eyes;
        }

        /// The dice's middle (the active ones), and whether any of them is still rolling.
        static bool DiceAt(DiceRoller r, out Vector3 at)
        {
            at = Vector3.zero;
            int n = 0;
            bool rolling = false;
            foreach (Die d in r.ActiveDice)
            {
                if (d == null || !d.gameObject.activeInHierarchy) continue;
                at += d.transform.position;
                n++;
                Rigidbody rb = d.GetComponent<Rigidbody>();
                if (rb != null && !rb.isKinematic && rb.velocity.sqrMagnitude > 0.09f) rolling = true;
            }
            if (n > 0) at /= n;
            else
            {
                Vector3? g = HandWorld.Targets.ContainsKey("dice") ? HandWorld.Targets["dice"]() : null;
                at = g ?? VRRig.I.HeadWorldPos;
            }
            return rolling;
        }

        static void Event(string what, Vector3 at, float seconds)
        {
            s_event = what;
            s_eventAt = at;
            s_eventUntil = Time.unscaledTime + seconds;
            Follows++;
        }
    }
}

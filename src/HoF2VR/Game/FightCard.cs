using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// B1: into a fight by one touch on the glowing encounter card at the table (the user's decision of 2026-10-10:
    /// "Entering a fight: one touch on the glowing encounter card at the table"; docs/loading-design.md §4.3.1).
    /// Settings Comfort.TouchToFight (on).
    ///  1. GameState_Level_In begins at the card table: the player stays there (Places keeps the table's place) for the
    ///     Dealer's call to battle - his CombatIntro, the room darkening, the encounter card glowing and rising, the other
    ///     cards gathering above the table - at the game's own pace (FastLoad leaves this deal alone).
    ///  2. The game wants the arena activated (Level_In's LoadLevel): FightFlow holds it, as at the campfire - the arena's
    ///     data loads, nothing of it wakes (no enemies, no fight noises). The table's fade to the tunnel's colour and the
    ///     move of the audio listener into the tunnel are skipped while the player is at the table.
    ///  3. The encounter card comes to the player, glowing, within reach (eye-relative: standing or seated) and waits: a
    ///     fingertip on it (or A / X, the fallback as everywhere) - a click, a flare.
    ///  4. Then the way of 2026-10-11: the mist, the campfire waiting space (its board, the controls card, Press A), and
    ///     only then the arena wakes behind the mist (FightFlow.Go) - round 5's fixes kept: no fight noises before A, the
    ///     activation's stall veiled.
    ///  5. Item 30 (the user, 2026-10-18 ~22:30): touched (or A) once the fight has loaded - its full "ready" glow - the card
    ///     goes straight into the fight: the usual dark fade, the arena activates in the dark (FightFlow.GoDirect), no waiting
    ///     space. Touched before that: the waiting space as in 4, until the fight has loaded; then its gate.
    /// Test runs: `gate go` counts as the touch, `gate auto <s>` touches it after s seconds; `fightcard pose|go|state`;
    /// `fightcard slowload <s>`: the fight counts as loaded only s seconds after its arena hold began (the "before ready" path).
    internal class FightCard : MonoBehaviour
    {
        public static FightCard I;
        public enum St { Off, Dealing, Offered, Touched, Leaving }
        static St s_st = St.Off;
        public static St State => s_st;
        /// The player stays at the Dealer's table (Places keeps the card table in Level_In).
        public static bool AtTable => s_st == St.Dealing || s_st == St.Offered || s_st == St.Touched;
        /// The arena waits (FightFlow's hold): at the table and on the way into the waiting space.
        public static bool HoldsArena => s_st != St.Off;
        public static bool Offered => s_st == St.Offered;
        public static int Starts, Offers, Touches, Buttons, Autos, Lost;
        public static string Last = "-";
        /// Where the card waits for the touch, from the player's neutral eyes (real metres; eye-relative, so a seated
        /// player gets it at the same reach): ahead, below.
        /// (the bug hunt of 2026-10-18, finding 4: the card is brought up, so the spawn rule holds for it - its face stops at least
        /// MinEyes from the eyes, real metres: 0.42 / 0.24 put its face 0.48 m from them; 0.48 / 0.24 = 0.537 m, still within a
        /// fingertip's easy reach standing and seated - the seated reach from the shoulder 0.56 m, OK <= 0.62)
        public static float OfferAhead = 0.48f, OfferDown = 0.24f, GlideSeconds = 0.8f, FlareSeconds = 0.35f, DealTimeout = 9f, MinEyes = 0.535f;
        /// The held-out card's width (real metres): the game shrinks the cards it gathers above the table (about 6 cm wide
        /// there); held out to be read and touched it is a hand-sized card.
        public static float OfferWidth = 0.16f;

        /// The third playtest, mark 14 (the user: "it would be really cool if this was actually the loading screen and there was
        /// like a loading bar, and this didn't quite glow as much until..."; their answer: the reveal becomes the loading screen,
        /// with a loading bar; it glows less until the fight has loaded): a slim bar under the held-out card fills with the
        /// arena's load (FightFlow), and the card's light stays low until it is in; then the full glow (and a soft tick) says
        /// "ready". A touch is taken at any time, as before.
        public static float DimGlow = 0.35f, BarGap = 0.014f, BarHeight = 0.009f, GrowFrom = 0.35f;
        static float s_bar = -1f, s_readyAt = -1f;
        static FightCardBar s_barObj;

        static Card s_card;
        static float s_since, s_offeredAt, s_touchedAt;
        static Vector3 s_fromPos;
        static Quaternion s_fromRot;
        static Vector3 s_toPos, s_scale0, s_scale1;
        static Quaternion s_toRot;
        static bool s_scaled;
        static bool s_approached;   // a fingertip was in front of the card's face (a touch comes from the front)
        static bool s_direct;       // item 30: touched once loaded - straight into the fight
        public static int Directs;
        static float s_hoverTick;
        static MeshWormhole s_listenerOf;
        static Light s_glow;

        public static bool Wanted => XRSettings.enabled && Settings.TouchToFight && FightFlow.Wanted;

        /// This fight is entered from the table by the touch (FastLoad's deal prefix asks before the state change is seen).
        public static bool WillStay()
        {
            if (!Wanted || CombatVR.Active || Limbo.On) return false;
            if (Places.Current != Places.Place.Caravan) return false;
            try { if (Game.IsDevRun) return false; } catch (Exception) { }
            return true;
        }

        /// The card the player holds out for touching now (KeepOut and the watchdog leave it where it is).
        public static bool Holding(Card c) => c != null && c == s_card && (s_st == St.Offered || s_st == St.Touched);

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(UI3D), "FadeCamera"), prefix: new HarmonyMethod(typeof(FightCard), nameof(FadeCameraPrefix)));
                h.Patch(AccessTools.Method(typeof(MeshWormhole), "ToggleAudioListener"), prefix: new HarmonyMethod(typeof(FightCard), nameof(ListenerPrefix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("fight card: patches failed (B1 off): " + e.Message); s_broken = true; }
            GameWatch.Changed += OnState;
        }

        static bool s_broken;

        void Awake()
        {
            I = this;
            Diag.StatusExtras["fightCard"] = () => Diag.Q(Describe());
            Diag.Commands["fightcard"] = Command;
        }

        static string Describe()
        {
            string where = "-";
            if (s_card != null && VRRig.I != null && (s_st == St.Offered || s_st == St.Touched))
            {
                Vector3 rel = Quaternion.Inverse(VRRig.I.NeutralRot) * (s_card.transform.position - VRRig.I.NeutralEye) / VRRig.I.Scale;
                where = string.Format(System.Globalization.CultureInfo.InvariantCulture, "'{0}' {1:0.00} m from the eyes (ahead {2:0.00}, below {3:0.00}, right {4:0.00})",
                    s_card.name, rel.magnitude, rel.z, -rel.y, rel.x);
            }
            return (s_st == St.Off ? "off" : s_st.ToString().ToLowerInvariant() + " " + (Time.unscaledTime - s_since).ToString("0.0") + " s") + " | " + where +
                   " | starts " + Starts + " offers " + Offers + " touches " + Touches + " buttons " + Buttons + " autos " + Autos + " lost " + Lost + " direct " + Directs +
                   " | setting " + Settings.TouchToFight + " | bar " + (s_bar < 0f ? "-" : s_bar.ToString("0.00") + (s_readyAt >= 0f ? " loaded" : " loading")) + " | last " + Last;
        }

        // ---- the start ----------------------------------------------------------------------------------------------------
        static void OnState(string st)
        {
            if (st != "GameState_Level_In")
            {
                if (s_st != St.Off) End("the state went on to " + st);
                return;
            }
            if (s_broken || !WillStay()) return;
            Starts++;
            Switch(St.Dealing, "Level_In at the table");
            s_card = null;
            s_direct = false;
            Plugin.L.LogInfo("fight card: the player stays at the table for the Dealer's call to battle (" + Diag.Clock() + ")");
            // (a test's `gate go` left from before the fight - nothing waited for it - is not this card's touch: the seated
            // tour's card went at once on a go sent at the title, 2026-10-14)
            if (WaitGate.DropTestGo()) Plugin.L.LogInfo("fight card: a test go from before the fight dropped");
        }

        static void Switch(St to, string why)
        {
            s_st = to;
            s_since = Time.unscaledTime;
            Last = to + " (" + why + ")";
        }

        static void End(string why)
        {
            if (s_st == St.Off) return;
            if (s_st == St.Dealing || s_st == St.Offered || s_st == St.Touched) GiveListener();
            Plugin.L.LogInfo("fight card: done (" + why + ", " + s_st + ")");
            Switch(St.Off, why);
            RestoreScale();
            s_card = null;
            if (s_glow != null) s_glow.enabled = false;
            HideBar();
        }

        // ---- the game's own steps while the player stays ------------------------------------------------------------------
        /// The table's camera would fade to the tunnel's colour (MeshWormhole.TransitionUp): not while the player is at it.
        static bool FadeCameraPrefix(Defiant.PostProcess.CameraFade a_cameraFade, ref IEnumerator __result)
        {
            if (!AtTable || UI3D.Instance == null || a_cameraFade != UI3D.Instance.UICameraFade) return true;
            __result = Nothing();
            Plugin.L.LogInfo("fight card: the table's fade to the tunnel skipped (the player is at the table)");
            return false;
        }

        static IEnumerator Nothing() { yield break; }

        /// The listener would move into the tunnel: the table keeps it while the player is there; the game's own move is
        /// made when the player goes (GiveListener).
        static bool ListenerPrefix(MeshWormhole __instance, bool a_enable)
        {
            if (s_giving || !AtTable || !a_enable) return true;
            s_listenerOf = __instance;
            Plugin.L.LogInfo("fight card: the audio listener stays at the table");
            return false;
        }

        static void GiveListener()
        {
            MeshWormhole w = s_listenerOf;
            s_listenerOf = null;
            if (w == null || UI3D.Instance == null || UI3D.Instance.UICameraFade == null) return;
            s_giving = true;
            try { w.ToggleAudioListener(true, UI3D.Instance.UICameraFade); Plugin.L.LogInfo("fight card: the audio listener goes into the tunnel, as the game does"); }
            catch (Exception e) { Plugin.L.LogWarning("fight card: listener: " + e.Message); }
            finally { s_giving = false; }
        }

        static bool s_giving;

        // ---- per frame ----------------------------------------------------------------------------------------------------
        void LateUpdate()
        {
            if (s_st == St.Off) return;
            try { Tick(); }
            catch (Exception e) { Plugin.L.LogError("fight card: " + e); Lost++; End("error"); }
        }

        static void Tick()
        {
            float now = Time.unscaledTime;
            if (GameWatch.State != "GameState_Level_In") { End("left Level_In"); return; }
            if (VRRig.I == null) { End("no rig"); return; }
            switch (s_st)
            {
                case St.Dealing:
                    if (Places.Current != Places.Place.Caravan) { Lost++; End("the player is not at the table"); return; }
                    // the deal is over once the game asks for the arena (FightFlow holds it): the card comes to the player
                    if (FightFlow.Held || now - s_since > DealTimeout) Offer(FightFlow.Held ? "the deal is done" : "the deal took long");
                    break;
                case St.Offered:
                    if (Places.Current != Places.Place.Caravan) { Lost++; End("the player is not at the table"); return; }
                    Pose(now);
                    string how = Touch();
                    if (how != null) { Go(how); break; }
                    if ((VRInput.R.ADown || VRInput.L.ADown) && now - s_offeredAt > 0.3f) { Buttons++; Go("A"); break; }
                    if (WaitGate.TakeTestGo()) { Go("test"); break; }
                    if (TestMode.Enabled && WaitGate.AutoSeconds > 0f && now - s_offeredAt >= WaitGate.AutoSeconds) { Autos++; Go("test auto"); break; }
                    break;
                case St.Touched:
                    Pose(now);
                    Flare(now);
                    if (now - s_touchedAt >= FlareSeconds)
                    {
                        // the game's own listener move, then away from the table: Places goes dark, the waiting space follows -
                        // or (item 30, the fight loaded at the touch) the fight itself, from the dark
                        GiveListener();
                        if (s_direct && !FightFlow.GoDirect("the fight card, touched once loaded"))
                        {
                            s_direct = false;
                            Plugin.L.LogInfo("fight card: no arena held any more - into the waiting space after all");
                        }
                        if (s_direct) Directs++;
                        Switch(St.Leaving, s_direct ? "straight into the fight" : "to the waiting space");
                        Places.TestShots(s_direct ? "card_leave_direct" : "card_leave_space");
                        if (s_glow != null) s_glow.enabled = false;
                        HideBar();
                    }
                    break;
                case St.Leaving:
                    if (Limbo.On) End("the waiting space is up");
                    // (straight in: done once the view is dark - as the waiting space's way ends once the space is up, in the dark)
                    else if (s_direct && (VRFade.IsBlack || !FightFlow.Direct)) End("straight into the fight, in the dark");
                    else if (now - s_since > 6f) { Lost++; End(s_direct ? "no dark after 6 s" : "no waiting space after 6 s"); }
                    break;
            }
        }

        // ---- the card ---------------------------------------------------------------------------------------------------
        static Card FindCard()
        {
            Encounter e = Encounter.Instance;
            Card c = e != null ? e.Card : null;
            if (c != null && c.gameObject.activeInHierarchy) return c;
            DeckManager dm = DeckManager.Instance;
            if (dm != null && dm.CombatIntroContainer != null)
                foreach (Card k in dm.CombatIntroContainer.Cards)
                    if (k is EncounterCard && k.gameObject.activeInHierarchy) return k;
            return null;
        }

        /// The card's face: centre, normal toward the eyes, up, and half sizes (world).
        static bool Face(Card c, out Vector3 centre, out Vector3 n, out Vector3 up, out Vector3 right, out float hw, out float hh)
        {
            centre = n = up = right = Vector3.zero;
            hw = hh = 0f;
            CardTemplate tpl = c.CardTemplate;
            BoxCollider bc = tpl != null ? tpl.GetComponent<Collider>() as BoxCollider : null;
            if (bc == null) return false;
            Transform t = bc.transform;
            Vector3 ls = t.lossyScale;
            Vector3 sz = new Vector3(Mathf.Abs(bc.size.x * ls.x), Mathf.Abs(bc.size.y * ls.y), Mathf.Abs(bc.size.z * ls.z));
            int thin = 0, tall = 0;
            for (int i = 1; i < 3; i++)
            {
                if (sz[i] < sz[thin]) thin = i;
                if (sz[i] > sz[tall]) tall = i;
            }
            if (thin == tall) return false;
            int wide = 3 - thin - tall;
            Vector3[] ax = { t.right, t.up, t.forward };
            centre = t.TransformPoint(bc.center);
            n = ax[thin];
            up = ax[tall];
            Vector3 eye = VRRig.I.NeutralEye;
            if (Vector3.Dot(n, eye - centre) < 0f) n = -n;
            if (Vector3.Dot(up, Vector3.up) < 0f) up = -up;
            right = Vector3.Cross(up, n).normalized;
            hw = sz[wide] * 0.5f;
            hh = sz[tall] * 0.5f;
            return true;
        }

        static void Offer(string why)
        {
            s_card = FindCard();
            Vector3 c, n, up, right;
            float hw, hh;
            if (s_card == null || !Face(s_card, out c, out n, out up, out right, out hw, out hh))
            {
                // no card to hold out: the old way (the waiting space at once)
                Lost++;
                Plugin.L.LogWarning("fight card: no encounter card to offer (" + why + ") - straight to the waiting space");
                GiveListener();
                Switch(St.Leaving, "no card");
                return;
            }
            Offers++;
            VRRig rig = VRRig.I;
            float sc = rig.Scale;
            Vector3 eye = rig.NeutralEye;
            Quaternion yaw = rig.NeutralRot;
            s_toPos = eye + yaw * (new Vector3(0f, -OfferDown, OfferAhead) * sc);
            // (from the eyes of this moment too: a head leaning in toward the table when the card comes gets it no nearer than
            // MinEyes - pushed out along the line from those eyes)
            Vector3 live = rig.HeadWorldPos, fromLive = s_toPos - live;
            if (fromLive.magnitude < MinEyes * sc && fromLive.magnitude > 1e-4f)
            {
                Plugin.L.LogInfo("fight card: the head leans in (" + (fromLive.magnitude / sc).ToString("0.00") + " m from the card's place) - held out " + MinEyes.ToString("0.00") + " m from the eyes");
                s_toPos = live + fromLive.normalized * (MinEyes * sc);
            }
            // the card's face turned to the eyes, upright as seen from them; the card's own rotation carried over by the
            // rotation that takes its face frame onto the wanted one
            Vector3 toEye = (eye - s_toPos).normalized;
            Quaternion want = Quaternion.LookRotation(toEye, Vector3.up);
            Quaternion have = Quaternion.LookRotation(n, up);
            Transform t = s_card.transform;
            s_fromPos = t.position;
            s_fromRot = t.rotation;
            // the card root's offset from its face centre goes with it
            Vector3 rootFromCentre = t.position - c;
            Quaternion delta = want * Quaternion.Inverse(have);
            s_toRot = delta * t.rotation;
            // a hand-sized card: grown about its face's centre during the glide (its scale goes back when B1 ends)
            float k = Mathf.Clamp(OfferWidth * sc / Mathf.Max(1e-4f, 2f * hw), 1f, 6f);
            s_scale0 = t.localScale;
            s_scale1 = s_scale0 * k;
            s_scaled = true;
            s_toPos = s_toPos + delta * rootFromCentre * k;
            s_offeredAt = Time.unscaledTime;
            s_approached = false;
            float into = Time.unscaledTime - s_since;
            Switch(St.Offered, why);
            Haptics.Event("ready", false, 0.25f, 0.03f, 2, 0.12f);
            Haptics.Event("ready", true, 0.25f, 0.03f, 2, 0.12f);
            Plugin.L.LogInfo("fight card: '" + s_card.name + "' comes to the player (" + why + ", " + into.ToString("0.0") + " s into Level_In; " +
                             (2f * hw / sc).ToString("0.000") + " m wide there, x" + k.ToString("0.00") + ") - touch it or press A");
        }

        static void Pose(float now)
        {
            if (s_card == null) return;
            float k = Mathf.Clamp01((now - s_offeredAt) / GlideSeconds);
            k = k * k * (3f - 2f * k);
            Transform t = s_card.transform;
            // a slow breath once it is there (2 mm, 0.25 Hz)
            float bob = s_st == St.Offered && k >= 1f ? Mathf.Sin((now - s_offeredAt - GlideSeconds) * Mathf.PI * 0.5f) * 0.002f * VRRig.I.Scale : 0f;
            t.SetPositionAndRotation(Vector3.Lerp(s_fromPos, s_toPos, k) + Vector3.up * bob, Quaternion.Slerp(s_fromRot, s_toRot, k));
            // (the third playtest, mark 25: "these cards are clipping together" - grown from the glide's start, the card swelled
            // through the cards gathered round it; it grows only once it is out of their ring, in the glide's last two thirds)
            if (s_scaled) t.localScale = Vector3.Lerp(s_scale0, s_scale1, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(GrowFrom, 1f, k)));
            Glow(now, k);
        }

        /// A soft light on the card while it waits (the cards' layer only, no shadows), breathing; a flare at the touch.
        static void Glow(float now, float arrived)
        {
            if (s_card == null) return;
            if (s_glow == null)
            {
                var go = new GameObject("HoF2VR_FightCardGlow");
                DontDestroyOnLoad(go);
                s_glow = go.AddComponent<Light>();
                s_glow.type = LightType.Point;
                s_glow.shadows = LightShadows.None;
                s_glow.renderMode = LightRenderMode.ForcePixel;
                s_glow.color = new Color(1f, 0.78f, 0.5f);
            }
            Vector3 c, n, up, right;
            float hw, hh;
            if (!Face(s_card, out c, out n, out up, out right, out hw, out hh)) return;
            float sc = VRRig.I.Scale;
            s_glow.transform.position = c + n * (0.16f * sc);
            s_glow.range = 0.5f * sc;
            s_glow.cullingMask = 1 << s_card.gameObject.layer;
            float breath = 0.85f + 0.15f * Mathf.Sin((now - s_offeredAt) * Mathf.PI * 0.8f);
            // the load: dim until the arena is in, then the full glow (a short swell as it turns ready); "ready" = the arena held
            // and loaded (Loaded: a touch now goes straight into the fight - item 30)
            bool ready = Loaded;
            float want = ready ? 1f : FightFlow.LoadFraction;
            if (s_bar < 0f) s_bar = 0f;
            s_bar = Mathf.MoveTowards(s_bar, want, Time.unscaledDeltaTime * (ready ? 2.5f : 1.2f));
            if (ready && s_readyAt < 0f && s_st == St.Offered)
            {
                s_readyAt = now;
                Haptics.Event("tick", false, 0.25f, 0.015f); Haptics.Event("tick", true, 0.25f, 0.015f);
                Plugin.L.LogInfo("fight card: the fight has loaded (" + (now - s_offeredAt).ToString("0.0") + " s after the card came): full glow");
            }
            float lit = ready ? 1f : DimGlow;
            float swell = s_readyAt >= 0f ? Mathf.Max(0f, 1f - (now - s_readyAt) / 0.6f) * 0.8f : 0f;
            s_glow.intensity = Mathf.Lerp(0.3f, (1.1f * breath + swell) * lit + s_hoverBoost, arrived);
            s_glow.enabled = true;
            if (s_barObj == null) s_barObj = new FightCardBar();
            s_barObj.Place(c - up * (hh + BarGap * sc) + n * (0.002f * sc), right, up, n, 2f * hw * 0.92f, BarHeight * sc, s_bar, arrived, ready ? Mathf.Clamp01((now - s_readyAt) / 0.4f) : 0f,
                           s_card.gameObject.layer);
        }

        /// The fight has loaded (the card's full glow): its arena is held and its data in. (Without the hold patch nothing is ever
        /// held: full glow, as before, and the waiting space after the touch.)
        public static bool Loaded => FightFlow.Ready || !FightFlow.HoldPatched;

        static void HideBar()
        {
            if (s_barObj != null) s_barObj.Hide();
            s_bar = -1f;
            s_readyAt = -1f;
        }

        static float s_hoverBoost;

        /// The card's own size back (the game flies it into the tunnel and back to the table later).
        static void RestoreScale()
        {
            if (!s_scaled) return;
            s_scaled = false;
            if (s_card != null) s_card.transform.localScale = s_scale0;
        }

        static void Flare(float now)
        {
            if (s_glow == null) return;
            float k = Mathf.Clamp01((now - s_touchedAt) / FlareSeconds);
            s_glow.intensity = Mathf.Lerp(3.2f, 1.2f, k);
        }

        /// A fingertip on the card's face (from the front, within its outline + 1 cm, up to 3 cm into it): which hand.
        static string Touch()
        {
            Vector3 c, n, up, right;
            float hw, hh;
            if (s_card == null || !Face(s_card, out c, out n, out up, out right, out hw, out hh)) return null;
            float sc = VRRig.I.Scale, margin = 0.01f * sc;
            float near = 1f;
            foreach (Hand h in VRInput.RL)
            {
                if (!HandWorld.Valid(h)) continue;
                // the fingertip every touch uses (the glove's index tip when the gloves are shown)
                Vector3 d = TouchInput.TipOf(h) - c;
                float dn = Vector3.Dot(d, n), dw = Mathf.Abs(Vector3.Dot(d, right)), dh = Mathf.Abs(Vector3.Dot(d, up));
                bool inside = dw < hw + margin && dh < hh + margin;
                if (inside && dn > 0.012f * sc && dn < 0.15f * sc) s_approached = true;
                if (inside) near = Mathf.Min(near, Mathf.Max(0f, dn / sc));
                if (s_approached && inside && dn <= 0.004f * sc && dn > -0.03f * sc)
                {
                    Touches++;
                    Haptics.Event("click", h.IsLeft, 0.6f, 0.02f);
                    return h.IsLeft ? "left fingertip" : "right fingertip";
                }
            }
            // a hand coming close: the glow warms and one soft tick
            s_hoverBoost = near < 0.05f ? 0.6f * (1f - near / 0.05f) : 0f;
            if (near < 0.03f && Time.unscaledTime > s_hoverTick) { s_hoverTick = Time.unscaledTime + 1.5f; Haptics.Event("tick", false, 0.2f, 0.01f); Haptics.Event("tick", true, 0.2f, 0.01f); }
            return null;
        }

        static void Go(string how)
        {
            s_touchedAt = Time.unscaledTime;
            // item 30: the fight loaded (its full glow) - straight in; still loading - the waiting space until it has
            s_direct = FightFlow.Ready;
            Switch(St.Touched, how);
            Places.TestShots("card_touch");
            try { s_card.TriggerEffect("wormhole_arrive"); } catch (Exception) { }
            Plugin.L.LogInfo("fight card: touched (" + how + ") " + (Time.unscaledTime - s_offeredAt).ToString("0.0") + " s after it came - " +
                             (s_direct ? "straight into the fight (it has loaded)" : "into the waiting space (the fight is still loading: " + Mathf.RoundToInt(FightFlow.LoadFraction * 100f) + " %)"));
        }

        /// The held-out card's face (its outline: the flat card as drawn, not its axis-aligned box - a card tilted to the eyes has
        /// a box that reaches ~9 cm nearer than any of it) from `eye`, world units, and its nearest point; MaxValue when no card
        /// is held out. The proximity watchdog measures the card by it (finding 4: it was a touch row, never counted).
        public static float FaceDistance(Vector3 eye, out Vector3 nearest)
        {
            nearest = eye;
            Vector3 c, n, up, right;
            float hw, hh;
            if (s_card == null || VRRig.I == null || !(s_st == St.Offered || s_st == St.Touched) || !Face(s_card, out c, out n, out up, out right, out hw, out hh)) return float.MaxValue;
            Vector3 d = eye - c;
            nearest = c + right * Mathf.Clamp(Vector3.Dot(d, right), -hw, hw) + up * Mathf.Clamp(Vector3.Dot(d, up), -hh, hh);
            return Vector3.Distance(nearest, eye);
        }

        /// The card's position as the player's (test) hand would aim at it: the point `cm` in front of its face and the move
        /// that pushes the fingertip 1.5 cm through it (as TouchBoard.Pose).
        static string PoseFor(float cm, Hand h)
        {
            Vector3 c, n, up, right;
            float hw, hh;
            if (s_card == null || VRRig.I == null || !Face(s_card, out c, out n, out up, out right, out hw, out hh)) return "none";
            float s = VRRig.I.Scale;
            Vector3 pt = c + n * (cm * 0.01f * s);
            Vector3 d = VRRig.I.FromWorldDir(-n * ((cm + 1.5f) * 0.01f * s)) / s;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.####} {1:0.####} {2:0.####} | move {3} {4:0.####} {5:0.####} {6:0.####}",
                pt.x, pt.y, pt.z, h.IsLeft ? "l" : "r", d.x, d.y, -d.z);
        }

        /// `fightcard [state]`, `fightcard pose [cm] [l|r]`, `fightcard go`.
        static string Command(string[] a)
        {
            string op = a.Length > 1 ? a[1] : "state";
            switch (op)
            {
                case "pose":
                    {
                        float cm = 6f;
                        if (a.Length > 2) float.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cm);
                        return s_st == St.Offered ? PoseFor(cm, a.Length > 3 && a[3] == "l" ? VRInput.L : VRInput.R) : "not offered (" + s_st + ")";
                    }
                case "go":
                    if (s_st != St.Offered) return "not offered (" + s_st + ")";
                    Go("seam");
                    return "go";
                case "slowload":
                    if (a.Length > 2) float.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out FightFlow.TestSlowLoad);
                    return "slowload " + FightFlow.TestSlowLoad.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s" + (TestMode.Enabled ? "" : " (test runs only)");
                default: return Describe();
            }
        }
    }

    /// The fight card's loading bar (FightCard, mark 14): a dark slot and a warm fill, one mesh of two quads under the held-out
    /// card, drawn after the cards (no depth write); built once, its corners rewritten each frame it shows.
    internal sealed class FightCardBar
    {
        readonly GameObject m_go;
        readonly Mesh m_mesh;
        readonly Vector3[] m_v = new Vector3[8];
        readonly Color32[] m_c = new Color32[8];

        public FightCardBar()
        {
            m_go = new GameObject("HoF2VR_FightCardBar");
            UnityEngine.Object.DontDestroyOnLoad(m_go);
            m_mesh = new Mesh { name = "HoF2VR_FightCardBar" };
            m_mesh.MarkDynamic();
            m_mesh.vertices = m_v;
            m_mesh.colors32 = m_c;
            m_mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            m_go.AddComponent<MeshFilter>().sharedMesh = m_mesh;
            var r = m_go.AddComponent<MeshRenderer>();
            r.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { renderQueue = 3150 };
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            m_go.SetActive(false);
        }

        /// The bar's middle at `c` on the plane of `right`/`up` (facing `n`), `w` x `h` world, filled to `v` (0..1); `alpha` fades
        /// it in with the card's glide; `ready` (0..1) warms the fill to the card's full glow.
        public void Place(Vector3 c, Vector3 right, Vector3 up, Vector3 n, float w, float h, float v, float alpha, float ready, int layer)
        {
            if (!m_go.activeSelf) m_go.SetActive(true);
            if (m_go.layer != layer) m_go.layer = layer;   // (the card's: the table's cameras draw that layer)
            Vector3 x = right * (w * 0.5f), y = up * (h * 0.5f);
            m_v[0] = c - x - y; m_v[1] = c - x + y; m_v[2] = c + x + y; m_v[3] = c + x - y;
            float fw = Mathf.Clamp01(v) * (w - h * 0.36f);
            Vector3 f0 = c - x + right * (h * 0.18f) - y + up * (h * 0.18f), f1 = f0 + up * (h * 0.64f);
            Vector3 lift = n * (0.0004f * w);
            m_v[4] = f0 + lift; m_v[5] = f1 + lift; m_v[6] = f1 + right * fw + lift; m_v[7] = f0 + right * fw + lift;
            byte a = (byte)(Mathf.Clamp01(alpha) * 255f);
            var back = new Color32(14, 10, 8, (byte)(a * 0.8f));
            var fill = Color32.Lerp(new Color32(200, 140, 70, a), new Color32(255, 226, 160, a), Mathf.Clamp01(ready));
            for (int i = 0; i < 4; i++) { m_c[i] = back; m_c[i + 4] = fill; }
            m_mesh.vertices = m_v;
            m_mesh.colors32 = m_c;
            m_mesh.RecalculateBounds();
        }

        public void Hide() { if (m_go != null && m_go.activeSelf) m_go.SetActive(false); }
    }
}

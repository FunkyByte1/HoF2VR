using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// F8: the gnome fight as the player designed it (their playtest of 2026-10-17, decisions.md "Gnomes"). HoF2's gnomes
    /// leap at the hero with ActionSnareGoblin (the "ranged" red telegraph, then the leap, then on its hit they attach to
    /// the hero and snare him - the cling); between leaps they are untargetable (ActionProneGnome makes a knocked-down gnome
    /// targetable). In Physical mode:
    ///  1. One leaps at a time, never two within OneASecond: a gnome's ActionSnareGoblin.Begin is refused while another
    ///     gnome's is in its wind-up or in the air or on the hero, or sooner than 1 s after the last one began or a cling ended.
    ///  2. It glows (the game's own red rim, the head marker hidden - F6), and the direction to strike it shows: a slash
    ///     line on it (the crit slash's look). A second gnome winding up meanwhile, or one leaping within FollowOn of the
    ///     last, gets the line perpendicular to the other's (90 deg).
    ///  3. While one is in the air and within SlowReach of the eyes, the world slows (SlowScale). A legal cut along its line
    ///     that meets it (the blade within its body's radius) knocks it aside (a sideways cut), down or back (a chop or a
    ///     thrust): its leap misses, it is pushed KnockMetres and knocked down (then targetable, as the game's own counter).
    ///     A cut the wrong way only flickers the line red.
    ///  4. A dash started while it winds up, or while it is in the air and still DodgeGap away, makes its leap miss.
    ///  5. A gnome that lands on the hero clings: the hero can't walk or dash, and a ring pops up in front of the eyes (1 m
    ///     out); five legal cuts through it break the cling - the gnome is knocked off. No other gnome leaps meanwhile.
    internal static partial class GnomeFight
    {
        public static bool On = true;   // `vrgnomes on|off`
        public static bool HoldLeaps;   // `vrgnomes hold on|off` (test mode only)
        public const float OneASecond = 1.0f, FollowOn = 2.5f, SlowReach = 2.6f, SlowScale = 0.35f, KnockMetres = 2.5f, DodgeGap = 0.6f, BodyRadius = 0.45f;
        public const int RingHits = 5;
        public static int Begun, Gated, Knocks, Wrong, Dodged, Clings, RingHitsDone, Freed, SlowFrames, ClingEnds;
        public static string Last = "-";
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        static readonly AccessTools.FieldRef<ActionSnareGoblin, bool> s_leaped = AccessTools.FieldRefAccess<ActionSnareGoblin, bool>("m_hasLeaped");
        static readonly AccessTools.FieldRef<ActionSnareGoblin, bool> s_hit = AccessTools.FieldRefAccess<ActionSnareGoblin, bool>("m_hasHit");
        static readonly AccessTools.FieldRef<ActionSnareGoblin, bool> s_attached = AccessTools.FieldRefAccess<ActionSnareGoblin, bool>("m_attached");
        static readonly AccessTools.FieldRef<ActionSnareGoblin, float?> s_hitTime = AccessTools.FieldRefAccess<ActionSnareGoblin, float?>("m_hitTime");
        static readonly AccessTools.FieldRef<ActionSnareGoblin, Targetable> s_target = AccessTools.FieldRefAccess<ActionSnareGoblin, Targetable>("m_target");
        static readonly System.Reflection.MethodInfo s_clear = AccessTools.Method(typeof(ActionSnareGoblin), "ClearTarget");

        static readonly int[] Lines = { -90, 180, -135, -45 };   // a chop, a sweep, both diagonals (Poise's lines)

        sealed class G
        {
            public ActionSnareGoblin A;
            public AIController Ai;
            public float Begun, ResultAt = -1f, WrongAt = -10f;
            public int Angle;
            public string Result;
            public IndicatorLooks.ISlash Vis;
            public Vector3 Axis, Normal;
            public bool Hidden, Posed;
            public int PosedFrames;
            public readonly List<Renderer> HiddenR = new List<Renderer>();
        }
        static readonly Dictionary<int, G> s_live = new Dictionary<int, G>();
        static readonly List<int> s_drop = new List<int>();
        static float s_lastBegin = -10f;
        static bool s_slowing;
        static int s_lastAngle = -999;

        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ActionSnareGoblin), "Begin"), prefix: new HarmonyMethod(typeof(GnomeFight), nameof(BeginPrefix)),
                        postfix: new HarmonyMethod(typeof(GnomeFight), nameof(BeginPostfix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("GnomeFight: patch failed: " + e.Message); }
            ApplyPosts(h);
            ApplyCling(h);
            Diag.Commands["gnomes?"] = a =>
            {
                if (a.Length > 1 && a[1] == "reset") { Begun = Gated = Knocks = Wrong = Dodged = Clings = RingHitsDone = Freed = SlowFrames = 0; Last = "-"; }
                return Describe();
            };
            // `gnomes? face`: the view turned to the nearest gnome winding up or in the air (gnomes are untargetable: `face`
            // can't find them) - for pictures and tests
            Diag.Commands["gnomeface"] = a =>
            {
                if (!TestMode.Enabled) return "test mode only";
                G best = null;
                foreach (G g in s_live.Values) if (g.Result == null && g.Ai != null && !Landed(g) && (best == null || HeadDist(g.Ai) < HeadDist(best.Ai))) best = g;
                if (best == null || CombatVR.I == null) return "no gnome winding up or in the air";
                CombatVR.I.FaceTowards(best.Ai.transform.position, false);
                return "facing gnome " + best.Ai.GetInstanceID() + " " + Phase(best) + " at " + HeadDist(best.Ai).ToString("0.0") + " m";
            };
            // `eyenear [r]`: what is drawn within r m (0.6) of the eyes - for pictures (GNP: a broad glowing band across the
            // view while a gnome clung)
            Diag.Commands["eyenear"] = a =>
            {
                if (VRRig.I == null) return "no rig";
                float r = a.Length > 1 ? float.Parse(a[1], Inv) : 0.6f;
                Vector3 head = VRRig.I.HeadWorldPos;
                // drawn ones only: the hero's own body is shadows-only (CombatVR.HideBody); nearest first
                var hits = new List<KeyValuePair<float, Renderer>>();
                foreach (Renderer rd in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (!rd.enabled || !rd.gameObject.activeInHierarchy || rd.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) continue;
                    Bounds b = rd.bounds;
                    if (b.size.magnitude > 20f) continue;
                    float d = Mathf.Sqrt(b.SqrDistance(head));
                    if (d <= r) hits.Add(new KeyValuePair<float, Renderer>(d, rd));
                }
                hits.Sort((x, y) => x.Key.CompareTo(y.Key));
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < hits.Count && i < 30; i++)
                {
                    Renderer rd = hits[i].Value;
                    sb.Append(" | ").Append(hits[i].Key.ToString("0.00", Inv)).Append(' ').Append(VRUI.Path(rd.transform)).Append(rd is ParticleSystemRenderer ? " (particles)" : "")
                      .Append(" [").Append(rd.sharedMaterial != null ? rd.sharedMaterial.name : "-").Append("] size ").Append(rd.bounds.size.magnitude.ToString("0.0", Inv));
                }
                return hits.Count + " drawn within " + r.ToString("0.00", Inv) + " m of the eyes" + sb;
            };
            Diag.Commands["vrgnomes"] = a =>
            {
                if (a.Length > 2 && a[1] == "posts") PostsOn = a[2] != "off";
                else if (a.Length > 2 && a[1] == "cling") ShowClinger = a[2] != "hidden";
                else if (a.Length > 2 && a[1] == "clingturn" && TestMode.Enabled) ClingTurn = float.Parse(a[2], Inv);
                // `vrgnomes hold on|off` (test mode): no gnome leaps meanwhile - the waiting formation alone, for checks and pictures
                else if (a.Length > 2 && a[1] == "hold" && TestMode.Enabled) HoldLeaps = a[2] == "on";
                else if (a.Length > 1) On = a[1] != "off";
                return "gnome fight " + (On ? "on" : "off") + ", posts " + (PostsOn ? "on" : "off") + ", the clinging one " + (ShowClinger ? "in front" : "hidden");
            };
        }

        public static string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("gnomes ").Append(On ? "on" : "off").Append(" begun ").Append(Begun).Append(" gated ").Append(Gated).Append(" knocks ").Append(Knocks)
              .Append(" wrong ").Append(Wrong).Append(" dodged ").Append(Dodged).Append(" clings ").Append(Clings).Append(" ringHits ").Append(RingHitsDone)
              .Append(" freed ").Append(Freed).Append(" clingEnds ").Append(ClingEnds).Append(" slowFrames ").Append(SlowFrames).Append(" clinging ").Append(Clinging).Append(" ring ").Append(s_ringCount)
              .Append(Clinging && VRRig.I != null ? " ringT " + V(VRRig.I.FromWorld(s_ringAt)) : "")
              .Append(" timescale ").Append(Time.timeScale.ToString("0.00")).Append(' ').Append(ClingQuery()).Append(" | live");
            foreach (G g in s_live.Values)
            {
                sb.Append(' ').Append(g.Ai != null ? g.Ai.GetInstanceID().ToString() : "-").Append(':').Append(g.Angle).Append(Phase(g))
                  .Append('@').Append(g.Ai != null ? HeadDist(g.Ai).ToString("0.0") : "-");
                // for a scripted cut (tests): its body's middle and its line in tracking space
                if (g.Ai != null && VRRig.I != null && g.Result == null)
                {
                    Vector3 c = VRRig.I.FromWorld(Center(g.Ai)), ax = VRRig.I.FromWorldDir(g.Axis);
                    sb.Append("[posT ").Append(c.x.ToString("0.000", Inv)).Append(',').Append(c.y.ToString("0.000", Inv)).Append(',').Append(c.z.ToString("0.000", Inv))
                      .Append(" axisT ").Append(ax.x.ToString("0.000", Inv)).Append(',').Append(ax.y.ToString("0.000", Inv)).Append(',').Append(ax.z.ToString("0.000", Inv)).Append(']');
                }
            }
            sb.Append(" | ").Append(LastBlade).Append(" | last ").Append(Last);
            return sb.ToString();
        }

        static bool Active => On && Physical.Live;
        static string V(Vector3 v) => v.x.ToString("0.000", Inv) + "," + v.y.ToString("0.000", Inv) + "," + v.z.ToString("0.000", Inv);

        static void Note(string s)
        {
            Last = s;
            Plugin.L.LogInfo("GnomeFight: " + s);
        }

        // ---------------------------------------------------------------------------------------- 1. one leap at a time

        static bool BeginPrefix(ActionSnareGoblin __instance, ref bool __result)
        {
            if (!Active || __instance == null) return true;
            try
            {
                AIController ai = __instance.GetComponentInParent<AIController>();
                if (ai == null || ai.Target == null || PlayerController.Instance == null || ai.Target.gameObject != PlayerController.Instance.gameObject) return true;
                float now = FightClock.Now;
                // another gnome still winding up or in the air (G1: allowing one to wind up while another flew put two in the
                // air at once), or the last began under a second ago. And none while one clings, nor in the second after a
                // cling ends (GN6: leaps went on during a cling - two clung at once, and a cut at a leaping one went to the
                // ring: while one clings the blade only counts at the ring, so a line drawn then could never be cut)
                int open = 0;
                bool clings = Clinging;
                foreach (G g in s_live.Values)
                {
                    if (g.Result == null && !Landed(g)) open++;
                    if (g.Result == null && OnHero(g)) clings = true;
                }
                if (open > 0 || clings || now - s_lastBegin < OneASecond || now < s_clingGateUntil || (HoldLeaps && TestMode.Enabled))
                {
                    Gated++;
                    __result = false;
                    return false;
                }
                // playtest 4: with the posts all round the hero, a leap from out of sight waits while another gnome stands in view
                if (OutOfSight(ai))
                {
                    GazeGated++;
                    __result = false;
                    return false;
                }
            }
            catch (Exception) { }
            return true;
        }

        static void BeginPostfix(ActionSnareGoblin __instance, bool __result)
        {
            if (!Active || !__result || __instance == null) return;
            try
            {
                AIController ai = __instance.GetComponentInParent<AIController>();
                if (ai == null) return;
                float now = FightClock.Now;
                float sinceLast = now - s_lastBegin;
                s_lastBegin = now;
                Begun++;
                // 2. the line to strike it: a second gnome winding up meanwhile gets the perpendicular one
                // (only a gnome winding up or in the air: one that landed or clings is not "another winding up"). With one
                // leap at a time that never overlaps, so "the second one" is also the one right after: a leap begun within
                // FollowOn of the last gets the line perpendicular to the last one's (the two never look alike)
                int other = -999;
                foreach (G o in s_live.Values) if (o.Result == null && o.A != null && o.A != __instance && !Landed(o)) other = o.Angle;
                if (other == -999 && sinceLast < FollowOn && s_lastAngle != -999) other = s_lastAngle;
                int ang;
                if (other != -999) ang = Perpendicular(other);
                else { do ang = Lines[UnityEngine.Random.Range(0, Lines.Length)]; while (ang == s_lastAngle && Lines.Length > 1); }
                s_lastAngle = ang;
                var g = new G { A = __instance, Ai = ai, Begun = now, Angle = ang };
                s_live[__instance.GetInstanceID()] = g;
                Note("leap begun by " + WeaponGrips.ModelKey(ai.name) + " " + ai.GetInstanceID() + ", line " + ang + " deg" + (other != -999 ? " (perpendicular to " + other + ", " + sinceLast.ToString("0.0") + " s after the last)" : "") +
                     " at " + HeadDist(ai).ToString("0.0") + " m");
            }
            catch (Exception e) { Plugin.L.LogWarning("GnomeFight: begin: " + e.Message); }
        }

        static int Perpendicular(int a) => a == -90 ? 180 : a == 180 ? -90 : a == -135 ? -45 : -135;

        static bool Landed(G g) => g.A == null || s_hit(g.A) || s_attached(g.A) || !g.A.IsRunning;

        /// On the hero: its leap's hit landed on him (the game's SnareHit, m_hitTime) or it has attached (the cling proper).
        /// GN7: between the hit and the attach (the SnareHit animation) the gnome counted neither as open nor as clinging,
        /// so the next leaps began meanwhile and two or three clung at once.
        static bool OnHero(G g) => g.A != null && g.A.IsRunning && (s_attached(g.A) || s_hitTime(g.A).HasValue);

        static string Phase(G g)
        {
            if (g.Result != null) return "(" + g.Result + ")";
            if (g.A == null || !g.A.IsRunning) return "(done)";
            if (OnHero(g)) return "(clinging)";
            if (s_hit(g.A)) return "(landed)";
            return s_leaped(g.A) ? "(air)" : "(wind-up)";
        }

        static float HeadDist(Controller c)
        {
            if (c == null || VRRig.I == null) return 99f;
            Vector3 d = c.transform.position - VRRig.I.HeadWorldPos;
            d.y = 0f;
            return d.magnitude;
        }

        // ---------------------------------------------------------------------------------------- every frame (Guard.LateUpdate)

        public static bool Clinging;
        static float s_clingSince = -1f, s_clingGateUntil = -1f;
        public const float MaxCling = 12f;
        static bool s_locked;

        static void LockSnare(bool on)
        {
            try
            {
                PlayerController pc = PlayerController.Instance;
                if (pc == null) return;
                Disabled? d = pc.Snared;
                if (on)
                {
                    if (!d.HasValue || !d.Value.Locked) pc.Snared = new Disabled { Time = Time.time, Duration = 0.5f, Locked = true };
                    s_locked = true;
                }
                else if (s_locked)
                {
                    s_locked = false;
                    if (d.HasValue && d.Value.Locked) pc.Snared = new Disabled { Time = Time.time, Duration = 0f, Locked = false };
                    if (Clinging) Note("the cling's safety: released after " + MaxCling.ToString("0") + " s");
                }
            }
            catch (Exception) { }
        }

        public static void Tick()
        {
            if (Physical.Paused)
            {
                // the audit, item 3: paused, the gnomes, the cling and the ring are kept (FightClock stands still), drawn again on
                // the way back
                foreach (G g in s_live.Values) if (g.Vis != null && g.Vis.Root.gameObject.activeSelf) g.Vis.Root.gameObject.SetActive(false);
                HideRing();
                return;
            }
            if (!Active)
            {
                if (s_live.Count > 0) { foreach (G g in s_live.Values) { Hide(g, false); EndPose(g); if (g.Vis != null) g.Vis.Destroy(); } s_live.Clear(); }
                Clinging = false;
                HideRing();
                EndSlow();
                ClearPosts();
                return;
            }
            float now = FightClock.Now;
            try { PostsTick(now); FacePosts(); } catch (Exception e) { Plugin.L.LogWarning("GnomeFight: posts: " + e.Message); }
            Vector3 head = VRRig.I != null ? VRRig.I.HeadWorldPos : Vector3.zero;
            // the way a gnome that clings now is shown: ahead of the player as they look now (kept while it clings)
            if (!Clinging && VRRig.I != null)
            {
                Vector3 f = VRRig.I.HeadWorldRot * Vector3.forward;
                f.y = 0f;
                if (f.sqrMagnitude > 1e-4f) s_clingFwd = f.normalized;
                s_clingExtra = 0f;
                s_midOff = MidOffGuess;
            }
            bool slow = false, cling = false;
            G clinger = null;
            s_drop.Clear();
            foreach (var kv in s_live)
            {
                G g = kv.Value;
                bool running = g.A != null && g.A.IsRunning && g.Ai != null;
                bool clings = g.Result == null && running && OnHero(g) && s_target(g.A) != null;
                if (clings) { cling = true; clinger = g; PoseClinger(g, head); }
                else { if (g.Posed || s_keep.ContainsKey(g.Ai != null ? g.Ai.GetInstanceID() : 0)) EndPose(g); Hide(g, false); }
                if (g.Result == null && running && s_leaped(g.A) && !s_hit(g.A) && HeadDist(g.Ai) <= SlowReach) slow = true;
                bool showLine = g.Result == null && running && !s_hit(g.A) && !s_attached(g.A);
                if (showLine) Draw(g, head, now);
                else if (g.Vis != null)
                {
                    if (g.Result != null && now - g.ResultAt < 0.45f) g.Vis.Draw(now - g.Begun, 0f, g.Result == "knocked" ? "ok" : "timeout", now - g.ResultAt, 99f, false, false);
                    else { g.Vis.Destroy(); g.Vis = null; }
                }
                if (!running && (g.Vis == null)) { Hide(g, false); s_drop.Add(kv.Key); }
            }
            foreach (int id in s_drop) s_live.Remove(id);
            Slow(slow);
            if (cling && !Clinging) { Clings++; s_clingSince = now; s_ringCount = 0; Note("a gnome clings: the ring is up (" + RingHits + " cuts)"); }
            if (!cling && Clinging) { ClingEnds++; s_clingGateUntil = now + OneASecond; Note("the cling ended by itself after " + (now - s_clingSince).ToString("0.0") + " s (" + s_ringCount + " ring cuts)"); }
            // the player's design: stuck until the ring breaks - the hero's snare locked while a gnome clings (the game's own
            // snare runs out by itself; with the hero invulnerable it lasts 0 s), released by Free; MaxCling as a safety
            if (cling) LockSnare(now - s_clingSince < MaxCling);
            else if (s_locked) LockSnare(false);
            // the safety's fallback: the game's cling has always ended with the snare (G5: at 12.0 s each time); should one
            // not, it lets go 3 s later (no more leaps come while one clings)
            if (cling && Clinging && now - s_clingSince > MaxCling + 3f)
            {
                foreach (G g in s_live.Values)
                    if (g.Result == null && OnHero(g)) { Miss(g); EndPose(g); try { g.A.Complete(); } catch (Exception) { } g.Result = "let go"; g.ResultAt = now; }
                Note("the cling's fallback: the gnome let go " + (now - s_clingSince).ToString("0.0") + " s in");
            }
            Clinging = cling;
            s_clinger = clinger;
            if (Clinging) Ring(head, now, clinger); else HideRing();
            if (clinger == null) s_posedNoted = null;
            if (clinger != null && clinger.PosedFrames >= 15 && !ReferenceEquals(s_posedNoted, clinger.A))
            {
                s_posedNoted = clinger.A;
                Posed++;
                Vector3 mid;
                LastPose = BodyMiddle(clinger, out mid) ? "body middle " + (mid - head).magnitude.ToString("0.00") + " m from the eyes, " + (head.y - mid.y).ToString("0.00") + " m below them" : "-";
                Note("the clinging gnome is shown in front (" + LastPose + "), the ring round it");
            }
        }

        /// A clinging gnome sits on the hero, inside the spawn rule's 0.76 m: its model is hidden while it clings (the ring
        /// shows where to strike); it shows again when it is knocked off or lets go.
        static void Hide(G g, bool hide)
        {
            if (g.Ai == null || (!hide && !g.Hidden)) return;
            try
            {
                if (hide)
                {
                    // every frame while it clings: also what turns on later - its weapon shown again by the game's
                    // show_attachments (inactive, so not found, when the hiding began) and its effects
                    if (!g.Hidden) g.HiddenR.Clear();
                    g.Ai.GetComponentsInChildren<Renderer>(true, s_rtmp);
                    foreach (Renderer r in s_rtmp) if (r != null && r.enabled) { r.enabled = false; g.HiddenR.Add(r); }
                    s_rtmp.Clear();
                }
                else { foreach (Renderer r in g.HiddenR) if (r != null) r.enabled = true; g.HiddenR.Clear(); }
            }
            catch (Exception) { }
            g.Hidden = hide;
        }

        static readonly List<Renderer> s_rtmp = new List<Renderer>();

        /// The line on the gnome, as Poise draws a crit: its middle moved toward the eyes, facing them.
        static void Draw(G g, Vector3 head, float now)
        {
            if (g.Vis == null) g.Vis = IndicatorLooks.NewSlash(CombatSettings.Look, "HoF2VR_GnomeSlash");
            Vector3 c = Center(g.Ai);
            Vector3 to = head - c;
            to.y = 0f;
            float dist = to.magnitude;
            Vector3 pos = c + (dist > 1e-3f ? to / dist : Vector3.zero) * Mathf.Min(0.25f, dist * 0.3f);
            // readable: a gnome is small - the line at least 0.9 m below the eyes at most (the picture walk: at a gnome's chest
            // near the player it was below the view), and never within the spawn rule's 0.76 m of the eyes (0.8 m)
            pos.y = Mathf.Clamp(pos.y + 0.25f, head.y - 0.9f, head.y);
            Vector3 fromEye = pos - head;
            if (fromEye.magnitude < 0.8f) pos = head + (fromEye.sqrMagnitude > 1e-6f ? fromEye.normalized : Vector3.forward) * 0.8f;
            Vector3 view = (pos - head).normalized;
            Vector3 up = Vector3.up - view * Vector3.Dot(Vector3.up, view);
            if (up.sqrMagnitude < 1e-6f) return;
            up.Normalize();
            Vector3 right = Vector3.Cross(up, view);
            // the audit, item 6: mirrored for a left-hander as Poise's crit lines (all but the chop), now that the cut's direction
            // counts (g.Angle stays the right-handed line: Perpendicular and the log work on that set)
            float a = (Settings.LeftHanded && g.Angle != -90 ? 180f - g.Angle : g.Angle) * Mathf.Deg2Rad;
            g.Axis = (right * Mathf.Cos(a) + up * Mathf.Sin(a)).normalized;
            g.Normal = view;
            if (!g.Vis.Root.gameObject.activeSelf) g.Vis.Root.gameObject.SetActive(true);
            g.Vis.Root.position = pos;
            g.Vis.Root.rotation = Quaternion.LookRotation(view, Vector3.Cross(view, g.Axis));
            g.Vis.Draw(now - g.Begun, 1f, null, 0f, now - g.WrongAt, false, false);
        }

        // its largest skinned renderer, looked up once a second per gnome (the audit, item 8: GetComponentsInChildren every frame)
        static readonly Dictionary<int, Renderer> s_body = new Dictionary<int, Renderer>();
        static readonly Dictionary<int, float> s_bodyAt = new Dictionary<int, float>();

        static Vector3 Center(Controller c)
        {
            if (c == null) return Vector3.zero;
            int id = c.GetInstanceID();
            Renderer best;
            float at;
            float now = Time.unscaledTime;
            if (!s_body.TryGetValue(id, out best) || best == null || !s_bodyAt.TryGetValue(id, out at) || now - at > 1f)
            {
                best = null;
                try { foreach (Renderer r in c.GetComponentsInChildren<Renderer>()) if (r is SkinnedMeshRenderer && (best == null || r.bounds.size.sqrMagnitude > best.bounds.size.sqrMagnitude)) best = r; }
                catch (Exception) { }
                s_body[id] = best;
                s_bodyAt[id] = now;
                if (s_body.Count > 64) { s_body.Clear(); s_bodyAt.Clear(); }
            }
            return best != null ? best.bounds.center : c.transform.position + Vector3.up * 0.5f;
        }

        // ---------------------------------------------------------------------------------------- 3. slow motion

        static void Slow(bool want)
        {
            try
            {
                Game g = Game.Instance;
                bool paused = MenuManager.Instance != null && MenuManager.Instance.Paused;
                float game = g != null ? g.TimeScale : 1f;
                if (want && !paused && !CombatFeel.BossSlowing)
                {
                    Time.timeScale = Mathf.Min(game, Mathf.MoveTowards(s_slowing ? Time.timeScale : 1f, SlowScale, Time.unscaledDeltaTime * 6f));
                    if (!s_slowing) Note("slow motion: a gnome in the air within " + SlowReach.ToString("0.0") + " m");
                    s_slowing = true;
                    SlowFrames++;
                }
                else if (s_slowing) EndSlow();
            }
            catch (Exception) { s_slowing = false; }
        }

        static void EndSlow()
        {
            if (!s_slowing) return;
            s_slowing = false;
            try
            {
                Game g = Game.Instance;
                bool paused = MenuManager.Instance != null && MenuManager.Instance.Paused;
                if (!paused && !CombatFeel.BossSlowing) Time.timeScale = g != null ? g.TimeScale : 1f;
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------------------------------- 3. the cut, 5. the ring

        /// BladeHits, each frame of a legal swing (not a stab): the blade's segment last frame and now, its swing direction.
        public static void Blade(Vector3 pBase, Vector3 pTip, Vector3 base_, Vector3 tip, Vector3 swingDir)
        {
            if (!Active || (s_live.Count == 0 && !Clinging)) return;
            float now = FightClock.Now;
            if (Clinging) { RingCut(pBase, pTip, base_, tip, now); return; }
            foreach (G g in s_live.Values)
            {
                if (g.Result != null || g.A == null || !g.A.IsRunning || !s_leaped(g.A) || s_hit(g.A) || g.Vis == null) continue;
                Vector3 c = Center(g.Ai);
                float d = Mathf.Min(SegDist(c, base_, tip), SegDist(c, Vector3.Lerp(pBase, base_, 0.5f), Vector3.Lerp(pTip, tip, 0.5f)));
                if (d < s_bladeMin || now - s_bladeAt > 0.5f) { s_bladeMin = d; s_bladeAt = now; LastBlade = "blade " + d.ToString("0.00") + " m from a leaping gnome's middle (" + BodyRadius.ToString("0.00") + " counts)"; }
                if (d > BodyRadius) continue;
                // along its arrow, as a crit (the way it points, CritLeeway: the user's ruling on the crit arrows, 2026-10-18)
                // judged upright as Poise's crits (playtest 4, P4-9: the drawn plane tilts with a look down at a low gnome, and a
                // chop at it read as "88 deg off")
                float eyeA, share;
                float along = Poise.CutAngle(swingDir, g.Normal, g.Axis, out eyeA, out share);
                if (along > WeaponTable.CritLeeway())
                {
                    if (now - g.WrongAt > 0.3f) { Wrong++; Note("wrong cut on a leaping gnome: " + along.ToString("0") + " deg off its arrow"); }
                    g.WrongAt = now;
                    continue;
                }
                Knock(g, swingDir, along);
            }
        }

        public static string LastBlade = "-";
        static float s_bladeMin = 99f, s_bladeAt = -10f;

        static float SegDist(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float k = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return (a + ab * k - p).magnitude;
        }

        /// A correct hit: the leap misses, the gnome is pushed the way of the cut (a chop or a thrust: away from the player)
        /// and knocked down.
        static void Knock(G g, Vector3 swingDir, float along)
        {
            Vector3 flat = swingDir;
            flat.y = 0f;
            Vector3 away = g.Ai.transform.position - (VRRig.I != null ? VRRig.I.HeadWorldPos : g.Ai.transform.position);
            away.y = 0f;
            bool chop = flat.magnitude < 0.5f * swingDir.magnitude;
            Vector3 dir = (chop || flat.sqrMagnitude < 1e-4f ? away : flat).normalized;
            if (dir.sqrMagnitude < 0.5f) dir = g.Ai.transform.forward * -1f;
            Miss(g);
            try { EnemyPush.Start(g.Ai, dir, KnockMetres, 0.2f); } catch (Exception) { }
            try { g.Ai.Knockdown(null, 1f); } catch (Exception) { }
            g.Result = "knocked";
            g.ResultAt = FightClock.Now;
            Knocks++;
            CombatFeel.ImpactBoth();
            Note("knocked " + (chop ? "down / back" : "aside") + ": a cut " + along.ToString("0") + " deg off its line");
        }

        /// The leap misses (the game's own miss: no target at its hit).
        static void Miss(G g)
        {
            try { s_clear.Invoke(g.A, null); } catch (Exception) { }
        }

        /// 4. CombatMove: a dash has started. A gnome winding up, or in the air and still DodgeGap away, misses.
        public static void Dashed()
        {
            if (!Active) return;
            foreach (G g in s_live.Values)
            {
                if (g.Result != null || g.A == null || !g.A.IsRunning || s_hit(g.A)) continue;
                PlayerController pc = PlayerController.Instance;
                if (pc == null) continue;
                Vector3 d = g.Ai.transform.position - pc.transform.position;
                d.y = 0f;
                // in the air: still DodgeGap away. Winding up (glowing, its line shown): always well before it lands - it
                // leaps at the spot the hero left (GN7: most leaps began 0.4-1.4 m from the hero, so in the air they were
                // rarely still 0.6 m away)
                bool air = s_leaped(g.A);
                if (air && d.magnitude < DodgeGap) continue;
                Miss(g);
                g.Result = "dodged";
                g.ResultAt = FightClock.Now;
                Dodged++;
                Note("a dash before it landed (" + (air ? "in the air" : "winding up") + "): the leap misses (" + d.magnitude.ToString("0.0") + " m away)");
            }
        }

        // the ring: 1 m in front of the eyes, a little low, facing them; placed when it pops up and again when the player
        // turns more than 60 deg from it
        static GameObject s_ring;
        static Mesh s_ringMesh;
        static readonly Premonition.Builder s_rb = new Premonition.Builder();
        static Vector3 s_ringAt, s_ringN;
        static int s_ringCount, s_ringBuilt = -1;
        static float s_ringHitAt = -10f;
        public const float RingDistance = 1.0f, RingRadius = 0.26f;

        static object s_posedNoted;
        static bool s_ringOnBody;

        static void Ring(Vector3 head, float now, G clinger)
        {
            Vector3 fwd = VRRig.I != null ? VRRig.I.HeadWorldRot * Vector3.forward : Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            bool placed = false;
            if (s_ringBuilt < 0 || now - s_clingSince < 0.05f || Vector3.Angle(s_clingFwd, fwd) > 60f)
            {
                // the gnome and its ring come round in front again (playtest 4: the gnome is shown there, PoseClinger)
                s_clingFwd = fwd;
                s_ringAt = head + fwd * RingDistance + Vector3.down * 0.25f;
                placed = true;
            }
            // round the shown gnome's body (its middle, smoothed: it shakes as it clings)
            Vector3 mid;
            if (ShowClinger && clinger != null && clinger.PosedFrames > 3 && BodyMiddle(clinger, out mid) && (mid - head).magnitude > 0.4f)
                s_ringAt = placed || s_ringBuilt < 0 || !s_ringOnBody ? mid : Vector3.Lerp(s_ringAt, mid, 0.3f);
            s_ringOnBody = ShowClinger && clinger != null && clinger.PosedFrames > 3;
            s_ringN = (s_ringAt - head).normalized;
            if (s_ring == null)
            {
                s_ring = new GameObject("HoF2VR_GnomeRing");
                UnityEngine.Object.DontDestroyOnLoad(s_ring);
                s_ringMesh = new Mesh { name = "HoF2VR_GnomeRing" };
                s_ringMesh.MarkDynamic();
                s_ring.AddComponent<MeshFilter>().sharedMesh = s_ringMesh;
                var r = s_ring.AddComponent<MeshRenderer>();
                r.sharedMaterial = Premonition.Alpha();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            if (!s_ring.activeSelf) s_ring.SetActive(true);
            s_ring.transform.position = s_ringAt;
            s_ring.transform.rotation = Quaternion.LookRotation(s_ringN, Vector3.up);
            float flash = Mathf.Clamp01(1f - (now - s_ringHitAt) / 0.25f);
            int key = s_ringCount * 10 + Mathf.RoundToInt(flash * 9f);
            if (key == s_ringBuilt) return;
            s_ringBuilt = key;
            s_rb.Clear();
            const int N = 40;
            for (int i = 0; i < N; i++)
            {
                float a0 = i * Mathf.PI * 2f / N, a1 = (i + 1) * Mathf.PI * 2f / N;
                // five arcs: a lit one for each cut still needed, a dark one for each done
                int seg = i * RingHits / N;
                bool done = seg < s_ringCount;
                Color c = done ? new Color(0.3f, 0.26f, 0.15f, 0.6f) : Color.Lerp(new Color(1f, 0.8f, 0.3f, 0.95f), Color.white, flash);
                if (i % (N / RingHits) == N / RingHits - 1) continue;   // gaps between the five arcs
                s_rb.Stroke(new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * RingRadius, new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * RingRadius, 0.022f, c, 0.008f);
            }
            s_rb.To(s_ringMesh);
        }

        static void HideRing()
        {
            if (s_ring != null && s_ring.activeSelf) s_ring.SetActive(false);
            s_ringBuilt = -1;
        }

        /// A legal cut whose blade passes through the ring's disc (between last frame and this one): one of the five.
        static void RingCut(Vector3 pBase, Vector3 pTip, Vector3 base_, Vector3 tip, float now)
        {
            if (now - s_ringHitAt < 0.25f) return;   // one count per cut
            bool through = false;
            for (int i = 0; i <= 4 && !through; i++)
            {
                float k = i / 4f;
                Vector3 a = Vector3.Lerp(pBase, base_, k), b = Vector3.Lerp(pTip, tip, k);
                float da = Vector3.Dot(a - s_ringAt, s_ringN), db = Vector3.Dot(b - s_ringAt, s_ringN);
                if ((da > 0f) == (db > 0f)) continue;
                Vector3 p = a + (b - a) * (da / (da - db));
                through = (p - s_ringAt).magnitude <= RingRadius * 1.15f;
            }
            if (!through) return;
            s_ringHitAt = now;
            s_ringCount++;
            RingHitsDone++;
            CombatFeel.Tick(false, 6f);
            CombatFeel.Tick(true, 4f);
            CombatSounds.Layer(CombatSounds.Thump, s_ringAt, 0.7f, 1.1f + 0.08f * s_ringCount);
            Note("the ring: " + s_ringCount + " of " + RingHits);
            if (s_ringCount >= RingHits) Free();
        }

        /// The ring broken: every clinging gnome is knocked off - its leap ended, the hero's snare too.
        static void Free()
        {
            PlayerController pc = PlayerController.Instance;
            int n = 0;
            foreach (G g in s_live.Values)
            {
                if (g.Result != null || !OnHero(g)) continue;
                Vector3 away = g.Ai.transform.position - (pc != null ? pc.transform.position : g.Ai.transform.position);
                away.y = 0f;
                if (away.sqrMagnitude < 1e-4f) away = pc != null ? pc.transform.forward : Vector3.forward;
                Miss(g);
                EndPose(g);
                try { g.A.Complete(); } catch (Exception) { }
                try { EnemyPush.Start(g.Ai, away.normalized, KnockMetres, 0.2f); } catch (Exception) { }
                try { g.Ai.Knockdown(null, 1f); } catch (Exception) { }
                g.Result = "knocked";
                g.ResultAt = FightClock.Now;
                n++;
            }
            try
            {
                Actor hero = pc != null ? pc.GetComponent<Actor>() : null;
                ActionSnared sn = hero != null ? hero.GetRunningAction<ActionSnared>() : null;
                if (sn != null && sn.IsRunning) sn.Complete();
            }
            catch (Exception) { }
            Freed++;
            LockSnare(false);
            Clinging = false;
            s_clingGateUntil = FightClock.Now + OneASecond;
            HideRing();
            CombatFeel.ImpactBoth();
            Note("the ring broke: " + n + " gnome(s) knocked off");
        }
    }
}

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace HoF2VR
{
    /// Playtest 4 (P4-7/P4-8, 2026-10-19; decisions.md "Gnomes"): the gnomes between their leaps.
    ///  * Posts: "I don't like how the gnomes are just standing there in a clump doing nothing ... can they at least look ready
    ///    to fight? Maybe they spread out around me (not in a perfectly even circle surrounding me, but something that doesn't
    ///    have them all directly in front of me)." HoF2's gnome swarm runs at the hero and stands about a metre off him (its AI
    ///    is not "polite": ActionMoveAI walks it in whenever it is more than its radius + 1 m away), and with one leap at a time
    ///    (the gate) the rest stood in a clump in front. Each waiting gnome now has a post round the hero - the posts spread all
    ///    the way round, each one's angle and distance jittered (PostMin-PostMax m), re-dealt every few seconds with a small turn
    ///    (they circle) - and walks to it (ActionMoveAI's own move to a Destination); at its post it stays (the game's idle: it
    ///    turns to face the hero, its steps and taunts), instead of walking in. Leaps start from the posts.
    ///  * A leap only from where the player can see it while some gnome stands in view (GazeGate): with posts all round, a
    ///    leap from behind would come unseen.
    ///  * Ground gnomes can be hit: "I feel like I couldn't hit them when they weren't on me." HoF2 keeps a gnome untargetable
    ///    (Targetable.IgnoreTargeting) except while it lies knocked down (ActionProneGnome) - the flat game's player never hits
    ///    a standing one - and BladeHits skipped untargetable enemies. A standing gnome (not in its leap) is now hit like any
    ///    enemy (Hittable).
    internal static partial class GnomeFight
    {
        public const float PostMin = 2.0f, PostMax = 2.9f, PostSlack = 0.7f, PostArrive = 0.4f, PostDealEvery = 7f, GazeGate = 110f;
        public static bool PostsOn = true;   // `vrgnomes posts on|off`
        public static int PostDeals, PostMoves, PostArrivals, GazeGated, GroundHits;

        sealed class Post
        {
            public AIController Ai;
            public Transform Mark;
            public float Angle, Radius;     // round the hero (world yaw, deg), metres
            public bool Live;               // waiting now (its post in use)
            public bool Dealt;              // it has a post (kept while it leaps: it comes back to it)
            public float Since;
            public bool Turning;            // turning to face the hero at its post (FacePosts)
        }
        static readonly Dictionary<int, Post> s_posts = new Dictionary<int, Post>();
        static readonly List<Post> s_waiters = new List<Post>();
        static float s_nextDeal, s_nextPostTick, s_dealBase;
        static int s_dealCount = -1;

        static readonly AccessTools.FieldRef<ActionMoveAI, AIController> s_mover = AccessTools.FieldRefAccess<ActionMoveAI, AIController>("m_controller");
        static readonly System.Reflection.MethodInfo s_setMoveToIdle = AccessTools.Method(typeof(ActionMoveAI), "SetMoveToIdle");

        static void ApplyPosts(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ActionMoveAI), "ConditionalSetMove"), prefix: new HarmonyMethod(typeof(GnomeFight), nameof(SetMovePrefix)));
                h.Patch(AccessTools.Method(typeof(ActionMoveAI), "ConditionalSetIdle"), prefix: new HarmonyMethod(typeof(GnomeFight), nameof(SetIdlePrefix)));
            }
            catch (Exception e) { Plugin.L.LogWarning("GnomeFight: posts patch failed: " + e.Message); }
            // `gnomefacts [#id|nearest]`: a gnome's AI, hit volumes and leap data (what the posts, the hits and the cling rest on)
            Diag.Commands["gnomefacts"] = a => Facts(a.Length > 1 ? a[1] : "nearest");
            // `gnomeposts?`: each gnome's post (its angle off the gaze, distance), where it is, what it runs
            Diag.Commands["gnomeposts?"] = a => PostsQuery();
        }

        static readonly AccessTools.FieldRef<ActionSnareGoblin, bool> s_gnomeSnare = AccessTools.FieldRefAccess<ActionSnareGoblin, bool>("m_gnomeSnare");

        /// A gnome: its leap is ActionSnareGoblin in the gnomes' kind (m_gnomeSnare; HoF2's goblins may carry the action too).
        static bool IsGnome(Controller c, out ActionSnareGoblin snare)
        {
            snare = null;
            if (c == null) return false;
            int id = c.GetInstanceID();
            if (!s_snareOf.TryGetValue(id, out snare) || (snare == null && Time.unscaledTime - s_snareAt[id] > 2f))
            {
                snare = c.GetComponentInChildren<ActionSnareGoblin>(true);
                bool gnome = false;
                try { gnome = snare != null && (s_gnomeSnare(snare) || c.name.IndexOf("Gnome", StringComparison.OrdinalIgnoreCase) >= 0); } catch (Exception) { }
                if (!gnome) snare = null;
                s_snareOf[id] = snare;
                s_snareAt[id] = Time.unscaledTime;
                if (s_snareOf.Count > 128) { s_snareOf.Clear(); s_snareAt.Clear(); }
            }
            return snare != null;
        }
        static readonly Dictionary<int, ActionSnareGoblin> s_snareOf = new Dictionary<int, ActionSnareGoblin>();
        static readonly Dictionary<int, float> s_snareAt = new Dictionary<int, float>();

        /// BladeHits: an untargetable enemy that may still be hit - a gnome standing on the ground (not in its leap: that one is
        /// GnomeFight's own cut, and the clinging one is the ring's).
        public static bool Hittable(Targetable t)
        {
            if (!Active || t == null) return false;
            ActionSnareGoblin snare;
            if (!IsGnome(t.Controller, out snare)) return false;
            if (snare.IsRunning) return false;
            Destroyable d = t.GetComponent<Destroyable>();
            return d != null && !d.IsDead;
        }

        public static void CountGroundHit() { GroundHits++; }

        /// The gnomes' hit volume (gnomefacts, 2026-10-19): one capsule 0.62 m tall (radius 0.3) from the feet, while the body
        /// stands 0.93 m - its head and shoulders, what a player looking down at a gnome swings at, were outside it. The blade
        /// also meets a standing gnome within GroundRadius of its body's axis, from GroundLow to GroundHigh above its feet.
        public const float GroundLow = 0.1f, GroundHigh = 0.9f, GroundRadius = 0.32f;
        static readonly List<Targetable> s_ground = new List<Targetable>();
        public static List<Targetable> Ground => s_ground;

        /// Does the blade's segment a-b (radius r) meet this standing gnome's body? point: where (on the blade, nearest its axis).
        public static bool BladeMeets(Targetable t, Vector3 a, Vector3 b, float r, out Vector3 point)
        {
            point = Vector3.zero;
            if (t == null) return false;
            Vector3 f = t.transform.position;
            Vector3 lo = f + Vector3.up * GroundLow, hi = f + Vector3.up * GroundHigh;
            float s, u;
            float d = SegSeg(a, b, lo, hi, out s, out u);
            if (d > GroundRadius + r) return false;
            point = Vector3.Lerp(a, b, s);
            return true;
        }

        /// The least distance between segments p1-q1 and p2-q2 (s, u: where on each, 0..1).
        static float SegSeg(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out float s, out float u)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            if (a <= 1e-8f && e <= 1e-8f) { s = u = 0f; return r.magnitude; }
            if (a <= 1e-8f) { s = 0f; u = Mathf.Clamp01(f / e); }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 1e-8f) { u = 0f; s = Mathf.Clamp01(-c / a); }
                else
                {
                    float bb = Vector3.Dot(d1, d2), den = a * e - bb * bb;
                    s = den > 1e-8f ? Mathf.Clamp01((bb * f - c * e) / den) : 0f;
                    u = (bb * s + f) / e;
                    if (u < 0f) { u = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (u > 1f) { u = 1f; s = Mathf.Clamp01((bb - c) / a); }
                }
            }
            return ((p1 + d1 * s) - (p2 + d2 * u)).magnitude;
        }

        // ---------------------------------------------------------------------------------------- the posts

        /// A gnome waiting its turn: after the hero, alive, not leaping or clinging, not lying down or staggered.
        static bool Waiting(AIController ai, Targetable hero)
        {
            if (ai == null || hero == null || ai.Target != hero) return false;
            ActionSnareGoblin snare;
            if (!IsGnome(ai, out snare) || snare.IsRunning) return false;
            Destroyable d = ai.GetComponent<Destroyable>();
            if (d == null || d.IsDead) return false;
            if (ai.Prone.HasValue || Poise.Staggered(ai) || ai.IsStunned(0f)) return false;
            return true;
        }

        static void PostsTick(float now)
        {
            if (Time.unscaledTime < s_nextPostTick) return;
            s_nextPostTick = Time.unscaledTime + 0.2f;
            PlayerController pc = PlayerController.Instance;
            Targetable hero = pc != null ? pc.Targetable : null;
            if (hero == null) { ClearPosts(); s_ground.Clear(); return; }
            s_waiters.Clear();
            s_ground.Clear();
            s_seen.Clear();
            // the gnomes near the hero (they are not the AI manager's combatants: their AI is not "polite", so the hero's
            // AITarget does not list them; a gnome in its leap is Hidden and may be left out - it is busy then anyway)
            BetterList<Targetable> near = hero.EnumerateWithinRadius(40f, hero.GetEnemyTeams());
            for (int i = 0; i < near.size; i++)
            {
                Targetable nt = near[i];
                AIController ai = nt != null ? nt.AIController : null;
                ActionSnareGoblin snare;
                if (ai == null || !IsGnome(ai, out snare)) continue;
                s_seen.Add(ai.GetInstanceID());
                if (ai.Targetable != null && Hittable(ai.Targetable)) s_ground.Add(ai.Targetable);
                Post p;
                int id = ai.GetInstanceID();
                if (!s_posts.TryGetValue(id, out p))
                {
                    var go = new GameObject("HoF2VR_GnomePost");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    s_posts[id] = p = new Post { Ai = ai, Mark = go.transform, Since = now };
                }
                bool wait = PostsOn && Waiting(ai, hero);
                if (!wait)
                {
                    // not waiting (leaping, clinging, down, staggered): its walk to the post stops
                    if (p.Live && ai.Destination == p.Mark) ai.Destination = null;
                    p.Live = false;
                    continue;
                }
                if (!p.Live) { p.Live = true; p.Since = now; }
                s_waiters.Add(p);
            }
            // the gone (dead, left the fight): their walks end, their marks go; one not seen this time (a leap: Hidden) waits
            s_gone.Clear();
            foreach (var kv in s_posts)
            {
                Post p = kv.Value;
                Destroyable dd = p.Ai != null ? p.Ai.GetComponent<Destroyable>() : null;
                if (p.Ai == null || dd == null || dd.IsDead) s_gone.Add(kv.Key);
                else if (!s_seen.Contains(kv.Key)) { if (p.Live && p.Ai.Destination == p.Mark) p.Ai.Destination = null; p.Live = false; }
            }
            foreach (int id in s_gone) DropPost(id);
            // a full deal every PostDealEvery s (a small turn: they circle); between deals a gnome without a post (new, or
            // back from a fall) gets the widest gap's middle - the rest keep theirs (re-dealing on every leap and return kept
            // the whole swarm walking)
            if (now >= s_nextDeal || s_dealCount < 0) Deal(hero, now);
            else foreach (Post p in s_waiters) if (!p.Dealt) Gap(p, hero);
            // the marks follow the hero (he walks and dashes)
            foreach (Post p in s_waiters) Place(p, hero);
        }
        static readonly List<int> s_gone = new List<int>();
        static readonly HashSet<int> s_seen = new HashSet<int>();

        // Candidate 11's chain (p4e3 posts: 2 of 6 gnomes settled at their posts faced 101 / 136 deg away from the player): the
        // game's idle does not always turn a gnome to the hero (its AI has no target: it is not one of the AI manager's
        // combatants). "Can they at least look ready to fight?": a gnome standing at its post turns to face the hero, FaceRate
        // deg/s (the game's turn speed is ~360), when it is more than FaceFrom deg off. Walking, it faces its way (the agent's).
        public static float FaceRate = 300f, FaceFrom = 25f;
        public static int FaceTurns;

        /// Every frame: the waiting gnomes standing at their posts turn to face the hero.
        static void FacePosts()
        {
            if (!PostsOn || s_waiters.Count == 0) return;
            PlayerController pc = PlayerController.Instance;
            Transform hero = pc != null && pc.Targetable != null ? pc.Targetable.transform : null;
            if (hero == null) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            foreach (Post p in s_waiters)
            {
                AIController ai = p.Ai;
                if (ai == null || !p.Live || p.Mark == null || ai.Destination != null) continue;
                if (FlatDist(ai.transform.position, p.Mark.position) > PostSlack) continue;
                Vector3 to = hero.position - ai.transform.position;
                to.y = 0f;
                if (to.sqrMagnitude < 0.01f) continue;
                Vector3 fwd = ai.transform.forward;
                fwd.y = 0f;
                float off = Vector3.Angle(fwd, to);
                if (off <= FaceFrom * (p.Turning ? 0.2f : 1f)) { p.Turning = false; continue; }
                if (!p.Turning) { p.Turning = true; FaceTurns++; }
                Quaternion want = Quaternion.LookRotation(to.normalized, Vector3.up);
                Quaternion flat = Quaternion.LookRotation(fwd.sqrMagnitude > 1e-4f ? fwd.normalized : to.normalized, Vector3.up);
                ai.transform.rotation = Quaternion.RotateTowards(flat, want, FaceRate * dt);
            }
        }

        /// The posts dealt: as many angles as waiting gnomes, all the way round (even steps, each jittered by up to a third of a
        /// step, the whole set turned by a random 15-35 deg from the last deal), each at its own distance; the gnomes matched to
        /// them in their order round the hero (the turn that moves them least), so nobody crosses through the middle.
        static void Deal(Targetable hero, float now)
        {
            int n = s_waiters.Count;
            s_dealCount = n;
            s_nextDeal = now + PostDealEvery * UnityEngine.Random.Range(0.8f, 1.2f);
            if (n == 0) return;
            PostDeals++;
            s_dealBase += UnityEngine.Random.Range(15f, 35f) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
            float step = 360f / n;
            var angles = new float[n];
            for (int i = 0; i < n; i++) angles[i] = Norm(s_dealBase + i * step + UnityEngine.Random.Range(-step, step) / 3f);
            Array.Sort(angles);
            // the gnomes by where they stand now (their angle round the hero)
            Vector3 hp = hero.transform.position;
            s_waiters.Sort((x, y) => AngleOf(x.Ai.transform.position, hp).CompareTo(AngleOf(y.Ai.transform.position, hp)));
            int best = 0;
            float bestCost = float.MaxValue;
            for (int r = 0; r < n; r++)
            {
                float cost = 0f;
                for (int i = 0; i < n; i++) cost += Mathf.Abs(Mathf.DeltaAngle(AngleOf(s_waiters[i].Ai.transform.position, hp), angles[(i + r) % n]));
                if (cost < bestCost) { bestCost = cost; best = r; }
            }
            for (int i = 0; i < n; i++)
            {
                Post p = s_waiters[i];
                p.Angle = angles[(i + best) % n];
                p.Radius = UnityEngine.Random.Range(PostMin, PostMax);
                p.Dealt = true;
            }
        }

        /// One more gnome between deals: the middle of the widest gap between the posts in use (jittered), its own distance.
        static void Gap(Post p, Targetable hero)
        {
            s_angles.Clear();
            foreach (Post o in s_posts.Values) if (o != p && o.Dealt && o.Live) s_angles.Add(o.Angle);
            float a;
            if (s_angles.Count == 0) a = AngleOf(p.Ai.transform.position, hero.transform.position);
            else
            {
                s_angles.Sort();
                float bestGap = -1f;
                a = 0f;
                for (int i = 0; i < s_angles.Count; i++)
                {
                    float from = s_angles[i], to = i + 1 < s_angles.Count ? s_angles[i + 1] : s_angles[0] + 360f;
                    if (to - from > bestGap) { bestGap = to - from; a = from + (to - from) * 0.5f; }
                }
                a += UnityEngine.Random.Range(-bestGap, bestGap) / 6f;
            }
            p.Angle = Norm(a);
            p.Radius = UnityEngine.Random.Range(PostMin, PostMax);
            p.Dealt = true;
        }
        static readonly List<float> s_angles = new List<float>();

        static float Norm(float a) { a %= 360f; return a < 0f ? a + 360f : a; }
        static float AngleOf(Vector3 p, Vector3 c) => Norm(Mathf.Atan2(p.x - c.x, p.z - c.z) * Mathf.Rad2Deg);

        /// Its mark on the navmesh at its angle and distance from the hero (pulled in along the line if the floor ends). Candidate
        /// 11's chain (p4e3 posts, the hero near the arena's edge): two posts behind him pulled in by the edge to 1.2-1.3 m, the
        /// gnomes waiting there all but on him. A post with under PostNear m of floor goes round the hero (PostTurnStep deg
        /// steps either way, up to 6) to the first angle with room at least PostSpacing from the other posts (env 3's hands turn
        /// 5: without the spacing two posts turned from behind met at 93 / 94 deg - a clump); else the first with room; else
        /// the one with the most (a corner).
        public const float PostNear = 1.6f, PostTurnStep = 25f, PostSpacing = 30f;
        public static int PostTurns;
        static void Place(Post p, Targetable hero)
        {
            Vector3 c = hero.transform.position;
            NavMeshHit hit;
            if (NavMesh.SamplePosition(c, out hit, 1f, NavMesh.AllAreas)) c = hit.position;
            float room;
            Vector3 best = Spot(c, p.Angle, p.Radius, out room);
            if (room < PostNear)
            {
                float bestA = p.Angle, bestRoom = room, roomA = float.NaN;
                Vector3 roomSpot = best;
                for (int k = 1; k <= 6; k++)
                    for (int s = -1; s <= 1; s += 2)
                    {
                        float a = Norm(p.Angle + s * k * PostTurnStep), r;
                        Vector3 w = Spot(c, a, p.Radius, out r);
                        if (r >= PostNear)
                        {
                            if (Spaced(p, a)) { p.Angle = a; PostTurns++; p.Mark.position = w; return; }
                            if (float.IsNaN(roomA)) { roomA = a; roomSpot = w; }
                        }
                        else if (r > bestRoom + 0.05f) { best = w; bestRoom = r; bestA = a; }
                    }
                if (!float.IsNaN(roomA)) { bestA = roomA; best = roomSpot; }
                if (bestA != p.Angle) { p.Angle = bestA; PostTurns++; }
            }
            p.Mark.position = best;
        }

        /// No other post in use within PostSpacing deg of this angle.
        static bool Spaced(Post p, float a)
        {
            foreach (Post o in s_posts.Values)
                if (o != p && o.Dealt && o.Live && Mathf.Abs(Mathf.DeltaAngle(o.Angle, a)) < PostSpacing) return false;
            return true;
        }

        /// The mark at angle (deg) and radius from c (on the navmesh); room: how far from c the floor goes that way, up to radius.
        static Vector3 Spot(Vector3 c, float angle, float radius, out float room)
        {
            float a = angle * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            Vector3 want = c + dir * radius;
            room = radius;
            NavMeshHit hit;
            // the floor ends before it: short of the edge (not under 1.2 m: then the gnome's move goes as near as it can)
            if (NavMesh.Raycast(c, want, out hit, NavMesh.AllAreas))
            {
                room = FlatDist(hit.position, c);
                if (room > 1.5f) want = hit.position - dir * 0.3f;
            }
            if (NavMesh.SamplePosition(want, out hit, 1f, NavMesh.AllAreas)) want = hit.position;
            room = Mathf.Min(room, FlatDist(want, c));
            return want;
        }

        static void DropPost(int id)
        {
            Post p;
            if (!s_posts.TryGetValue(id, out p)) return;
            try { if (p.Ai != null && p.Ai.Destination == p.Mark) p.Ai.Destination = null; } catch (Exception) { }
            if (p.Mark != null) UnityEngine.Object.Destroy(p.Mark.gameObject);
            s_posts.Remove(id);
        }

        static void ClearPosts()
        {
            if (s_posts.Count == 0) return;
            s_tmpIds.Clear();
            s_tmpIds.AddRange(s_posts.Keys);
            foreach (int id in s_tmpIds) DropPost(id);
            s_dealCount = -1;
        }
        static readonly List<int> s_tmpIds = new List<int>();

        static float FlatDist(Vector3 a, Vector3 b) { a.y = b.y = 0f; return (a - b).magnitude; }

        /// ActionMoveAI, idle: a waiting gnome at its post stays there (the game's idle then faces the hero, steps, taunts) instead
        /// of walking in to the hero; away from it, it walks to its mark (the game's own move to a Destination).
        static bool SetMovePrefix(ActionMoveAI __instance, ref bool __result)
        {
            try
            {
                AIController ai = s_mover(__instance);
                Post p;
                if (ai == null || !s_posts.TryGetValue(ai.GetInstanceID(), out p) || !p.Live || p.Mark == null || !Active) return true;
                if (ai.Destination != null && ai.Destination != p.Mark) return true;   // someone else's errand
                if (ai.Destination == null && FlatDist(ai.transform.position, p.Mark.position) <= PostSlack) { __result = false; return false; }
                if (ai.Destination == null) PostMoves++;
                ai.Destination = p.Mark;   // the original then starts the move
            }
            catch (Exception) { }
            return true;
        }

        /// ActionMoveAI, moving: a gnome on its way to its post stops there.
        static bool SetIdlePrefix(ActionMoveAI __instance, ref bool __result)
        {
            try
            {
                AIController ai = s_mover(__instance);
                Post p;
                if (ai == null || !s_posts.TryGetValue(ai.GetInstanceID(), out p) || p.Mark == null || ai.Destination != p.Mark) return true;
                if (p.Live && FlatDist(ai.transform.position, p.Mark.position) > PostArrive) return true;   // on its way (Destination set: the original says go on)
                ai.Destination = null;
                PostArrivals++;
                s_setMoveToIdle.Invoke(__instance, null);
                __result = true;
                return false;
            }
            catch (Exception) { return true; }
        }

        /// BeginPrefix: a gnome out of the player's sight (more than GazeGate deg off the gaze) does not leap while another one
        /// waits in sight - leaps come from where the player looks.
        static bool OutOfSight(AIController ai)
        {
            if (VRRig.I == null || ai == null) return false;
            Vector3 head = VRRig.I.HeadWorldPos, fwd = VRRig.I.HeadWorldRot * Vector3.forward;
            if (OffGaze(ai.transform.position, head, fwd) <= GazeGate) return false;
            foreach (Post p in s_posts.Values)
                if (p.Live && p.Ai != null && p.Ai != ai && OffGaze(p.Ai.transform.position, head, fwd) <= GazeGate * 0.8f) return true;
            return false;
        }

        static float OffGaze(Vector3 p, Vector3 head, Vector3 fwd)
        {
            Vector3 d = p - head;
            d.y = 0f;
            fwd.y = 0f;
            return d.sqrMagnitude < 1e-4f || fwd.sqrMagnitude < 1e-4f ? 0f : Vector3.Angle(d, fwd);
        }

        static string PostsQuery()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("posts ").Append(PostsOn ? "on" : "off").Append(" deals ").Append(PostDeals).Append(" moves ").Append(PostMoves).Append(" arrivals ").Append(PostArrivals)
              .Append(" gazeGated ").Append(GazeGated).Append(" groundHits ").Append(GroundHits).Append(" faceTurns ").Append(FaceTurns).Append(" postTurns ").Append(PostTurns);
            if (VRRig.I == null) return sb.ToString();
            Vector3 head = VRRig.I.HeadWorldPos, fwd = VRRig.I.HeadWorldRot * Vector3.forward;
            PlayerController pc = PlayerController.Instance;
            Vector3 hp = pc != null ? pc.transform.position : head;
            foreach (var kv in s_posts)
            {
                Post p = kv.Value;
                if (p.Ai == null) continue;
                Vector3 at = p.Ai.transform.position;
                Vector3 g = fwd; g.y = 0f;
                float gaze = g.sqrMagnitude > 1e-4f ? Norm(Mathf.Atan2(g.x, g.z) * Mathf.Rad2Deg) : 0f;
                Actor actor = p.Ai.GetComponent<Actor>();
                Defiant.Action lo = actor != null ? actor.RunningActionLowerBody : null;
                ActionMoveAI m = lo as ActionMoveAI;
                sb.Append(" | ").Append(kv.Key).Append(p.Live ? " waiting" : " busy").Append(" post ").Append(Mathf.DeltaAngle(gaze, p.Angle).ToString("0")).Append(" deg ")
                  .Append(p.Radius.ToString("0.0")).Append(" m; at ").Append(Mathf.DeltaAngle(gaze, AngleOf(at, hp)).ToString("0")).Append(" deg ")
                  .Append(FlatDist(at, hp).ToString("0.0")).Append(" m, ").Append(FlatDist(at, p.Mark.position).ToString("0.0")).Append(" m off its post")
                  .Append(p.Ai.Destination == p.Mark ? " going" : "").Append(" faces ").Append(OffGaze(hp, at, p.Ai.transform.forward).ToString("0")).Append(" deg off the hero")
                  .Append(" act ").Append(lo != null ? lo.GetType().Name : "-").Append(m != null ? "/" + m.CurrentState : "")
                  .Append(" mark ").Append(FlatDist(p.Mark.position, hp).ToString("0.0")).Append(" m");
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------------------- facts

        static string Facts(string who)
        {
            Targetable t = Physical.FindEnemy(who);
            if (t == null)
            {
                // gnomes are untargetable and may be Hidden: look through the hero's AI list
                PlayerController pc = PlayerController.Instance;
                AITarget at = pc != null && pc.Targetable != null ? pc.Targetable.AITarget : null;
                if (at != null) for (int i = 0; i < at.AI.size && t == null; i++) { ActionSnareGoblin sn; if (at.AI[i] != null && IsGnome(at.AI[i], out sn)) t = at.AI[i].Targetable; }
            }
            if (t == null || t.Controller == null) return "no enemy";
            var sb = new System.Text.StringBuilder();
            Controller c = t.Controller;
            AIController ai = t.AIController;
            sb.Append(WeaponGrips.ModelKey(t.name)).Append(" #").Append(t.GetInstanceID()).Append(" ignoreTargeting ").Append(t.IgnoreTargeting).Append(" hidden ").Append(t.Hidden)
              .Append(" radius ").Append(t.Radius.ToString("0.00")).Append(" ignoreHits ").Append(c.IgnoreHits);
            if (ai != null)
            {
                sb.Append(" mode ").Append(ai.Mode).Append(" polite ").Append(ai.PoliteCombat).Append(" dest ").Append(ai.Destination != null ? ai.Destination.name : "-");
                try
                {
                    AIManager.AIManagerData d = ai.AIData;
                    if (d != null) sb.Append(" aiData ").Append(d.TargetRadius.ToString("0.0")).Append('/').Append(d.InnerRadius.ToString("0.0")).Append('/').Append(d.OuterRadius.ToString("0.0")).Append('/').Append(d.RangedRadius.ToString("0.0"));
                }
                catch (Exception) { }
            }
            Actor actor = t.GetComponent<Actor>();
            if (actor != null && actor.RunningActions != null) { sb.Append(" running"); foreach (Defiant.Action x in actor.RunningActions) if (x != null) sb.Append(' ').Append(x.GetType().Name); }
            // its actions (every Defiant.Action under it)
            sb.Append(" | actions");
            foreach (Defiant.Action x in c.GetComponentsInChildren<Defiant.Action>(true)) sb.Append(' ').Append(x.GetType().Name);
            ActionMoveAI mv = c.GetComponentInChildren<ActionMoveAI>(true);
            if (mv != null)
            {
                sb.Append(" | move");
                foreach (string f in new[] { "m_canStep", "m_canTaunt", "m_canRelax", "m_isCoward", "m_useTransitions", "m_faceDestinations", "m_approachWhenAttacker", "m_moveToDestinationSpeed", "m_moveInSpeed", "m_approachSpeed" })
                {
                    try { sb.Append(' ').Append(f.Substring(2)).Append(' ').Append(Traverse.Create(mv).Field(f).GetValue()); } catch (Exception) { }
                }
            }
            ActionSnareGoblin sg = c.GetComponentInChildren<ActionSnareGoblin>(true);
            if (sg != null)
            {
                sb.Append(" | snare");
                foreach (string f in new[] { "m_moverAttachNode", "m_snareLimit", "m_gnomeSnare", "m_idealDistance", "m_maximumMoveScale", "m_maxAngle", "m_targetSnareOffset" })
                {
                    try { sb.Append(' ').Append(f.Substring(2)).Append(' ').Append(Traverse.Create(sg).Field(f).GetValue()); } catch (Exception) { }
                }
                try { string[] tn = Traverse.Create(sg).Field("m_targetAttachNodes").GetValue<string[]>(); sb.Append(" targetNodes ").Append(tn != null ? string.Join(",", tn) : "-"); } catch (Exception) { }
                try
                {
                    string mn = Traverse.Create(sg).Field("m_moverAttachNode").GetValue<string>();
                    Transform mt = c.Model != null ? c.Model.FindAttachNode(mn) : null;
                    sb.Append(" moverNode ").Append(mt != null ? VRUI.Path(mt) : "-").Append(" model ").Append(c.Model != null ? VRUI.Path(c.Model.transform) : "-");
                    Model hm = PlayerController.Instance != null ? PlayerController.Instance.Model : null;
                    string[] tn = Traverse.Create(sg).Field("m_targetAttachNodes").GetValue<string[]>();
                    if (hm != null && tn != null && tn.Length > 0 && VRRig.I != null)
                    {
                        Transform hn = hm.FindAttachNode(tn[0]);
                        sb.Append(" heroNode0 ").Append(hn != null ? VRUI.Path(hn) : "-").Append(" at ").Append(hn != null ? Diag.V(hn.position - VRRig.I.HeadWorldPos) : "-").Append(" from the eyes");
                    }
                }
                catch (Exception e) { sb.Append(" (" + e.Message + ")"); }
            }
            // the hit volumes the blade meets (colliders on the enemy layers) and the body's size
            sb.Append(" | colliders");
            int mask = Targetable.TeamMaskToLayerMask(TeamType.Enemy);
            foreach (Collider col in c.GetComponentsInChildren<Collider>(true))
            {
                bool onMask = ((1 << col.gameObject.layer) & mask) != 0;
                sb.Append(' ').Append(col.GetType().Name.Replace("Collider", "")).Append(onMask ? "*" : "").Append(col.enabled && col.gameObject.activeInHierarchy ? "" : "(off)").Append(col.isTrigger ? "(trig)" : "")
                  .Append(" size ").Append(Diag.V(col.bounds.size)).Append(" bottom ").Append((col.bounds.min.y - t.transform.position.y).ToString("0.00"));
            }
            Renderer best = null;
            foreach (Renderer r in c.GetComponentsInChildren<Renderer>()) if (r is SkinnedMeshRenderer && (best == null || r.bounds.size.sqrMagnitude > best.bounds.size.sqrMagnitude)) best = r;
            if (best != null) sb.Append(" | body ").Append(Diag.V(best.bounds.size)).Append(" centre ").Append((best.bounds.center.y - t.transform.position.y).ToString("0.00")).Append(" up");
            if (c.Animator != null && c.Animator.Animator != null)
            {
                sb.Append(" | anim");
                foreach (AnimatorControllerParameter ap in c.Animator.Animator.parameters) sb.Append(' ').Append(ap.name);
            }
            return sb.ToString();
        }
    }
}

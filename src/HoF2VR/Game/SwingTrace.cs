using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// An enemy's striking parts, found once per model: the game's weapon-trail lines (TrailWorldProxy pointA -> pointB: the
    /// game's own idea of the blade), weapon meshes in its hierarchy (their long axis), else its hands (forearm -> hand,
    /// stretched 0.12 m). Where an enemy's blow really goes: the swing atlas (SwingAtlas), the trace (SwingTrace), the
    /// block's clash point.
    internal static class EnemyWeapon
    {
        public sealed class Part
        {
            public Transform T;
            public Vector3 A, B;       // local to T: one end, the other end (trails: base, tip)
            public string Kind, Name;
            public Vector3 WA, WB;     // world, at the last Sample
        }

        sealed class Entry { public List<Part> Fixed = new List<Part>(); public float TrailScan = -10f; public List<Part> Trails = new List<Part>(); }
        static readonly Dictionary<int, Entry> s_cache = new Dictionary<int, Entry>();
        static readonly List<Part> s_tmp = new List<Part>();

        static readonly string[] WeaponWords = { "weapon", "sword", "axe", "dagger", "knife", "mace", "club", "hammer", "spear", "blade", "staff", "scythe",
            "cleaver", "sickle", "flail", "maul", "halberd", "glaive", "lance", "pike", "polearm", "sabre", "saber", "scimitar", "machete", "shiv", "cutlass",
            "rapier", "whip", "hook", "trident", "claw", "sceptre", "scepter", "wand", "pitchfork", "shovel", "cudgel", "bat_", "knuckle" };

        static bool WeaponName(string n)
        {
            n = n.ToLowerInvariant();
            // shields count: some blows are shield bashes (the templar's first); the motion rule picks what really swings
            if (n.Contains("shield")) return true;
            if (n.Contains("sheath") || n.Contains("scabbard") || n.Contains("holster")) return false;
            foreach (string w in WeaponWords) if (n.Contains(w)) return true;
            return false;
        }

        static Entry Get(Model m)
        {
            Entry e;
            int id = m.GetInstanceID();
            if (s_cache.TryGetValue(id, out e)) return e;
            if (s_cache.Count > 200) s_cache.Clear();
            e = new Entry();
            try
            {
                foreach (Renderer r in m.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                    // the renderer or one of its three nearest ancestors carries a weapon's name
                    bool weapon = false;
                    Transform t = r.transform;
                    for (int k = 0; k < 4 && t != null && t != m.transform; k++, t = t.parent) if (WeaponName(t.name)) { weapon = true; break; }
                    if (!weapon) continue;
                    Bounds b;
                    Transform space = r.transform;
                    var mf = r.GetComponent<MeshFilter>();
                    var sk = r as SkinnedMeshRenderer;
                    if (mf != null && mf.sharedMesh != null) b = mf.sharedMesh.bounds;
                    else if (sk != null && sk.sharedMesh != null) { b = sk.localBounds; if (sk.rootBone != null) space = sk.rootBone; }
                    else continue;
                    Vector3 ext = b.extents;
                    Vector3 axis = ext.x >= ext.y && ext.x >= ext.z ? Vector3.right * ext.x : ext.y >= ext.z ? Vector3.up * ext.y : Vector3.forward * ext.z;
                    Vector3 worldLong = space.TransformVector(axis);
                    if (worldLong.magnitude < 0.12f) continue;   // a buckle, not a weapon
                    e.Fixed.Add(new Part { T = space, A = b.center - axis, B = b.center + axis, Kind = "mesh", Name = r.name });
                }
                if (e.Fixed.Count == 0)
                {
                    // no weapon: the hands (claws, fists, gnomes' bites)
                    foreach (Transform t in m.GetComponentsInChildren<Transform>(true))
                    {
                        string n = t.name.ToLowerInvariant();
                        if (!(n.Contains("hand") || n.Contains("wrist") || n.Contains("palm")) || n.Contains("handle") || n.Contains("finger") || n.Contains("thumb") || t.parent == null) continue;
                        if (n.Contains("hand") && (n.Contains("ik") || n.Contains("target") || n.Contains("cards"))) continue;
                        Transform fore = t.parent;
                        Vector3 d = t.position - fore.position;
                        if (d.magnitude < 0.05f || d.magnitude > 0.8f) continue;
                        Vector3 tipW = t.position + d.normalized * 0.12f;
                        e.Fixed.Add(new Part { T = t, A = t.InverseTransformPoint(fore.position), B = t.InverseTransformPoint(tipW), Kind = "hand", Name = t.name });
                        if (e.Fixed.Count >= 4) break;
                    }
                }
                // the feet too: some blows are kicks (the motion and closest-pass rules pick what really strikes)
                int feet = 0;
                foreach (Transform t in m.GetComponentsInChildren<Transform>(true))
                {
                    string n = t.name.ToLowerInvariant();
                    if (!n.Contains("foot") || n.Contains("step") || n.Contains("ik") || n.Contains("target") || t.parent == null) continue;
                    Vector3 d = t.position - t.parent.position;
                    if (d.magnitude < 0.05f || d.magnitude > 0.9f) continue;
                    Vector3 toe = t.position + d.normalized * 0.1f;
                    e.Fixed.Add(new Part { T = t, A = t.InverseTransformPoint(t.parent.position), B = t.InverseTransformPoint(toe), Kind = "foot", Name = t.name });
                    if (++feet >= 2) break;
                }
            }
            catch (Exception ex) { Plugin.L.LogWarning("EnemyWeapon: " + ex.Message); }
            s_cache[id] = e;
            return e;
        }

        /// The model's striking parts with their world segments this frame (trails looked up again every 0.25 s: the game
        /// spawns them for a swing and lets them go after it).
        public static List<Part> Sample(Model m)
        {
            s_tmp.Clear();
            if (m == null) return s_tmp;
            Entry e = Get(m);
            float now = Time.unscaledTime;
            if (now - e.TrailScan > 0.25f)
            {
                e.TrailScan = now;
                e.Trails.Clear();
                try
                {
                    foreach (TrailWorldProxy p in m.GetComponentsInChildren<TrailWorldProxy>(false))
                        if (p != null && (p.pointB - p.pointA).sqrMagnitude > 1e-4f)
                            e.Trails.Add(new Part { T = p.transform, A = p.pointA, B = p.pointB, Kind = "trail", Name = p.name });
                }
                catch (Exception) { }
            }
            foreach (Part p in e.Trails) if (p.T != null) s_tmp.Add(p);
            foreach (Part p in e.Fixed) if (p.T != null) s_tmp.Add(p);
            foreach (Part p in s_tmp) { p.WA = p.T.TransformPoint(p.A); p.WB = p.T.TransformPoint(p.B); }
            return s_tmp;
        }

        /// A one-line description of what was found for this model (the `weaponparts` seam).
        public static string Describe(Model m)
        {
            if (m == null) return "no model";
            var sb = new StringBuilder(WeaponGrips.ModelKey(m.name) + ":");
            foreach (Part p in Sample(m))
                sb.Append(' ').Append(p.Kind).Append('(').Append(p.Name).Append(' ').Append((p.WB - p.WA).magnitude.ToString("0.00", CultureInfo.InvariantCulture)).Append(" m)");
            return sb.ToString();
        }
    }

    /// Combat Stage 1 probe (`swingtrace on|off|clear|dump [name]`, `swingmatch?`): for every strike at the player, from its
    /// telegraph until 0.25 s after its impact, where the enemy's striking part really is (EnemyWeapon) and where the bar is
    /// - the bar as placed now (SwingAtlas) and as placed before Stage 1 (Guard.LegacyBar), on the same blows. The match: in
    /// the blade's last 0.25 s up to its impact (and 0.05 s after), its nearest approach to the bar in the bar's plane
    /// (within 0.4 m of it in depth), and there the bar's axis against the square to the blade's motion. The striking part,
    /// its pass by the guard zone and the root's move in the attacker's own frame come from the strike's SwingAtlas.Track
    /// (the same measure the run-time learning uses) -> testdata/probes/<name>.tsv for scripts/swing_atlas.py.
    internal static class SwingTrace
    {
        public static bool On;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        sealed class Frame
        {
            public float T;
            public Vector3 Head, F, BarC, BarAxis, BarC0, BarAxis0, Root, Hero;
            public Quaternion RootRot;
            public Vector3[] A, B;
            public string[] Parts;
        }

        sealed class Trace
        {
            public int Id;
            public Controller C;
            public Model M;
            public string Enemy = "", Action = "", Dir = "", ClipHit = "", Colour = "", Result = "";
            public float T0, Impact = -1f, ClipEvent;
            public bool FollowUp, OffView, Done;
            public readonly List<Frame> Frames = new List<Frame>();
            public SwingAtlas.Shape Shape;   // the Track's measure at the impact (root frame)
            public string Part = "", Bones = "";
            public float PassD = -1f;
            public string Row;
            public Match New, Old;
        }

        public struct Match { public float InPlane, Depth, AngleOff, At; public bool Crossing, Ok; public Vector3 BladeS, VelS; }   // BladeS / VelS: the blade where it passed the bar and its motion, in the player's frame (x right, y up, z toward the attacker)

        static readonly Dictionary<int, Trace> s_live = new Dictionary<int, Trace>();
        static readonly List<Trace> s_done = new List<Trace>();

        public static void Register()
        {
            Diag.Commands["swingtrace"] = a =>
            {
                string op = a.Length > 1 ? a[1] : "";
                if (op == "on") { On = true; return "swing trace on (" + s_done.Count + " so far)"; }
                if (op == "off") { On = false; return "swing trace off"; }
                if (op == "clear") { s_done.Clear(); s_live.Clear(); return "cleared"; }
                if (op == "dump") return Dump(a.Length > 2 ? a[2] : "swings");
                return Summary(a.Length > 1 ? a[1] : null);
            };
            Diag.Commands["swingmatch?"] = a => Summary(a.Length > 1 ? a[1] : null);
            // `landside on|off`: mark 16's rule (a sweep's bar on the side it lands, SwingAtlas.Place)
            Diag.Commands["landside"] = a => { if (a.Length > 1) SwingAtlas.LandSide = a[1] != "off"; return "land side " + SwingAtlas.LandSide + " shifts " + SwingAtlas.LandShifts; };
            Diag.Commands["atlas"] = a => a.Length > 1 && a[1] == "dump" ? SwingAtlas.Dump() : SwingAtlas.Describe();
            Diag.Commands["weaponparts"] = a =>
            {
                var sb = new StringBuilder();
                PlayerController pc = PlayerController.Instance;
                if (pc == null) return "no hero";
                Targetable me = pc.GetComponent<Targetable>();
                BetterList<Targetable> all = me.EnumerateWithinRadius(60f, me.GetEnemyTeams());
                var seen = new HashSet<string>();
                for (int i = 0; i < all.size; i++)
                {
                    Controller c = all[i] != null ? all[i].Controller : null;
                    if (c == null || c.Model == null || !seen.Add(WeaponGrips.ModelKey(c.Model.name))) continue;
                    sb.Append("\n  ").Append(EnemyWeapon.Describe(c.Model));
                }
                Plugin.L.LogInfo("weapon parts:" + sb);
                return seen.Count + " enemy kinds (see log)";
            };
        }

        /// A strike opened (its telegraph, or a combo's next blow).
        public static void Begin(int id, Controller c, Model m, string action, string dir, string colour, string clip, float ev, bool followUp)
        {
            if (!On || c == null || m == null) return;
            var t = new Trace { Id = id, C = c, M = m, Enemy = WeaponGrips.ModelKey(m.name), Action = action, Dir = dir, Colour = colour, FollowUp = followUp, T0 = Time.unscaledTime,
                                ClipHit = clip ?? "", ClipEvent = ev };
            s_live[id] = t;
            if (s_live.Count > 30) { int oldest = s_live.Keys.Min(); s_live.Remove(oldest); }
        }

        /// Every frame of a live (or just judged) strike: the bar now and the bar as before Stage 1 (world centre and axis).
        public static void Tick(int id, Vector3 head, Vector3 f, Vector3 barC, Vector3 barAxis, Vector3 barC0, Vector3 barAxis0)
        {
            Trace t;
            if (!On || !s_live.TryGetValue(id, out t) || t.Done) return;
            if (t.C == null || t.M == null) { s_live.Remove(id); return; }
            float now = Time.unscaledTime;
            if (t.Frames.Count > 0 && t.Frames[t.Frames.Count - 1].T >= now) return;   // once per frame
            Record(t, head, f, barC, barAxis, barC0, barAxis0, now);
            if (t.Impact >= 0f && now - t.Impact > 0.25f) Finish(t);
            else if (now - t.T0 > 6f) s_live.Remove(id);
        }

        static void Record(Trace t, Vector3 head, Vector3 f, Vector3 barC, Vector3 barAxis, Vector3 barC0, Vector3 barAxis0, float now)
        {
            List<EnemyWeapon.Part> parts = EnemyWeapon.Sample(t.M);
            var fr = new Frame
            {
                T = now, Head = head, F = f, BarC = barC, BarAxis = barAxis, BarC0 = barC0, BarAxis0 = barAxis0, Root = t.C.transform.position,
                RootRot = t.C.transform.rotation, A = new Vector3[parts.Count], B = new Vector3[parts.Count], Parts = new string[parts.Count]
            };
            PlayerController pc = PlayerController.Instance;
            fr.Hero = pc != null ? pc.transform.position : head;
            for (int i = 0; i < parts.Count; i++) { fr.A[i] = parts[i].WA; fr.B[i] = parts[i].WB; fr.Parts[i] = parts[i].Kind + ":" + parts[i].Name; }
            t.Frames.Add(fr);
            if (t.Frames.Count > 600) t.Frames.RemoveAt(0);
        }

        /// The strike's impact (Guard's judgement at CombatEffect.Apply): the pose now is the pose of the blow.
        public static void Impact(int id, string result, bool offView, Vector3 head, Vector3 f, Vector3 barC, Vector3 barAxis, Vector3 barC0, Vector3 barAxis0,
                                  SwingAtlas.Track track)
        {
            Trace t;
            if (!On || !s_live.TryGetValue(id, out t) || t.Impact >= 0f) return;
            t.Impact = Time.unscaledTime;
            t.Result = result;
            t.OffView = offView;
            if (t.C == null || t.M == null) return;
            if (t.Frames.Count > 0 && t.Frames[t.Frames.Count - 1].T >= t.Impact) t.Frames.RemoveAt(t.Frames.Count - 1);
            Record(t, head, f, barC, barAxis, barC0, barAxis0, t.Impact);
            if (track != null)
            {
                SwingAtlas.Shape sh;
                if (track.Measure(head, f, out sh)) { t.Shape = sh; t.Part = track.LastPart; t.PassD = track.LastPass; }
            }
            // what of the attacker's body is nearest the guard point at the impact (a kick, a shield, a shoulder...)
            try
            {
                Vector3 g = SwingAtlas.Track.GuardPoint(head, f);
                var near = new List<KeyValuePair<float, string>>();
                foreach (Transform b in t.M.GetComponentsInChildren<Transform>())
                    near.Add(new KeyValuePair<float, string>((b.position - g).magnitude, b.name));
                near.Sort((x, y) => x.Key.CompareTo(y.Key));
                var sb = new StringBuilder();
                for (int i = 0; i < Math.Min(4, near.Count); i++) sb.Append(i > 0 ? "," : "").Append(near[i].Value).Append('=').Append(near[i].Key.ToString("0.00", Inv));
                t.Bones = sb.ToString();
            }
            catch (Exception) { }
        }

        /// Into the strike frame of `fr`: x right, y up from the eyes, z toward the attacker.
        static Vector3 S(Frame fr, Vector3 w)
        {
            Vector3 r = Vector3.Cross(Vector3.up, fr.F).normalized;
            Vector3 d = w - fr.Head;
            return new Vector3(Vector3.Dot(d, r), d.y, Vector3.Dot(d, fr.F));
        }

        /// Closest points between segments p1-q1 and p2-q2.
        public static void SegSeg(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2, out float s, out float u)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            if (a <= 1e-8f && e <= 1e-8f) { s = u = 0f; c1 = p1; c2 = p2; return; }
            if (a <= 1e-8f) { s = 0f; u = Mathf.Clamp01(f / e); }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 1e-8f) { u = 0f; s = Mathf.Clamp01(-c / a); }
                else
                {
                    float b = Vector3.Dot(d1, d2), den = a * e - b * b;
                    s = den != 0f ? Mathf.Clamp01((b * f - c * e) / den) : 0f;
                    u = (b * s + f) / e;
                    if (u < 0f) { u = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (u > 1f) { u = 1f; s = Mathf.Clamp01((b - c) / a); }
                }
            }
            c1 = p1 + d1 * s;
            c2 = p2 + d2 * u;
        }

        /// The bar against the blade over the blade's last 0.25 s up to the impact (and 0.05 s after): the frame where the
        /// blade comes nearest the bar in the bar's plane (within 0.4 m in depth if any frame is), and there the bar's axis
        /// against the square to the blade's motion (UYF: the bar lies across the blade's path).
        static Match Score(Trace t, string part, bool legacy)
        {
            var m = new Match { InPlane = 9f, Depth = 9f, AngleOff = 90f };
            int bestK = -1;
            Vector3 bestQ = Vector3.zero;
            float bestS = 0f;
            for (int k = 0; k < t.Frames.Count; k++)
            {
                Frame fr = t.Frames[k];
                if (fr.T < t.Impact - 0.25f || fr.T > t.Impact + 0.05f) continue;
                int j = Array.IndexOf(fr.Parts, part);
                if (j < 0) continue;
                Vector3 c = legacy ? fr.BarC0 : fr.BarC, ax = legacy ? fr.BarAxis0 : fr.BarAxis;
                Vector3 b0 = c - ax * (Guard.BarLength * 0.5f), b1 = c + ax * (Guard.BarLength * 0.5f), q, qb;
                float s, u;
                SegSeg(fr.A[j], fr.B[j], b0, b1, out q, out qb, out s, out u);
                Vector3 d = q - qb;
                float depth = Mathf.Abs(Vector3.Dot(d, fr.F)), ip = (d - fr.F * Vector3.Dot(d, fr.F)).magnitude;
                bool better = bestK < 0 || (depth <= 0.4f && (m.Depth > 0.4f || ip < m.InPlane)) || (depth > 0.4f && m.Depth > 0.4f && ip + depth < m.InPlane + m.Depth);
                if (better) { bestK = k; m.InPlane = ip; m.Depth = depth; bestQ = q; bestS = s; }
            }
            if (bestK < 0) return m;
            Frame bf = t.Frames[bestK];
            m.At = bf.T - t.Impact;
            // the blade's motion there: from the frame ~0.06 s before (or after, at the window's start)
            Vector3 vel = Vector3.zero;
            int bj = Array.IndexOf(bf.Parts, part);
            for (int k = bestK - 1; k >= 0; k--)
            {
                Frame fr = t.Frames[k];
                int j = Array.IndexOf(fr.Parts, part);
                if (j < 0) continue;
                if (bf.T - fr.T >= 0.05f || k == 0) { vel = (Vector3.Lerp(bf.A[bj], bf.B[bj], bestS) - Vector3.Lerp(fr.A[j], fr.B[j], bestS)) / Mathf.Max(1e-3f, bf.T - fr.T); break; }
            }
            Vector3 axis = legacy ? bf.BarAxis0 : bf.BarAxis;
            Vector3 vp = vel - bf.F * Vector3.Dot(vel, bf.F);
            m.Crossing = vel.magnitude > 0.3f && vp.magnitude >= 0.35f * vel.magnitude;
            float ang = vp.sqrMagnitude > 1e-6f ? Vector3.Angle(vp, axis) : 90f;
            ang = Mathf.Min(ang, 180f - ang);
            m.AngleOff = 90f - ang;
            m.Ok = m.InPlane <= 0.15f && m.Depth <= 0.4f && (!m.Crossing || m.AngleOff <= 25f);
            m.BladeS = S(bf, bestQ);
            Vector3 rr = Vector3.Cross(Vector3.up, bf.F).normalized;
            m.VelS = new Vector3(Vector3.Dot(vel, rr), vel.y, Vector3.Dot(vel, bf.F));
            return m;
        }

        static void Finish(Trace t)
        {
            t.Done = true;
            s_live.Remove(t.Id);
            if (string.IsNullOrEmpty(t.Part))
            {
                // no measure from the track: the part that moved most in the last frames
                Frame im = t.Frames.LastOrDefault(fr => fr.T <= t.Impact + 1e-4f);
                if (im == null || im.Parts.Length == 0) { s_done.Add(t); return; }
                t.Part = im.Parts[0];
            }
            t.New = Score(t, t.Part, false);
            t.Old = Score(t, t.Part, true);
            Frame f0 = t.Frames.LastOrDefault(fr => fr.T <= t.Impact + 1e-4f) ?? t.Frames[t.Frames.Count - 1];
            Func<float, string> F2 = x => x.ToString("0.00", Inv);
            Func<Vector3, string> V = x => F2(x.x) + "," + F2(x.y) + "," + F2(x.z);
            Func<Match, string> M = x => F2(x.InPlane) + "\t" + F2(x.Depth) + "\t" + (x.Crossing ? F2(x.AngleOff) : "thrust") + "\t" + F2(x.At) + "\t" + (x.Ok ? "1" : "0");
            SwingAtlas.Shape sh = t.Shape ?? new SwingAtlas.Shape();
            Vector3 toHero = f0.Hero - f0.Root;
            toHero.y = 0f;
            var row = new StringBuilder();
            row.Append(t.Enemy).Append('\t').Append(t.Action).Append('\t').Append(t.Dir).Append('\t').Append(t.ClipHit).Append('\t').Append(F2(t.ClipEvent)).Append('\t')
               .Append(t.Colour).Append(t.FollowUp ? "+follow" : "").Append(t.OffView ? "+offview" : "").Append('\t').Append(t.Result).Append('\t').Append(t.Part).Append('\t')
               .Append(F2(t.Impact - t.T0)).Append('\t').Append(M(t.New)).Append('\t').Append(M(t.Old)).Append('\t')
               .Append(F2(t.PassD)).Append('\t').Append(F2(sh.Dt)).Append('\t').Append(V(S(f0, f0.BarC))).Append('\t').Append(V(S(f0, f0.BarC0))).Append('\t')
               .Append(V(sh.A)).Append('\t').Append(V(sh.B)).Append('\t').Append(V(sh.V)).Append('\t').Append(V(sh.Move)).Append('\t')
               .Append(F2(toHero.magnitude)).Append('\t').Append(F2(f0.Head.y - f0.Hero.y)).Append('\t').Append(F2(SwingAtlas.ScaleOf(t.C))).Append('\t').Append(t.Shape != null ? "1" : "0").Append('\t').Append(t.Bones).Append('\t').Append(V(t.New.BladeS)).Append('\t').Append(V(t.New.VelS));
            t.Row = row.ToString();
            s_done.Add(t);
            if (s_done.Count > 3000) s_done.RemoveAt(0);
            if (s_done.Count <= 400)
                Plugin.L.LogInfo("swing " + t.Enemy + " " + t.Action + " clip " + t.ClipHit + "@" + F2(t.ClipEvent) + " " + t.Colour + " " + t.Result + " part " + t.Part +
                                 " | now: in-plane " + F2(t.New.InPlane) + " depth " + F2(t.New.Depth) + " angle-off " + (t.New.Crossing ? F2(t.New.AngleOff) : "thrust") + " at " + F2(t.New.At) +
                                 (t.New.Ok ? " MATCH" : " miss") + " | before: in-plane " + F2(t.Old.InPlane) + " depth " + F2(t.Old.Depth) + " angle-off " +
                                 (t.Old.Crossing ? F2(t.Old.AngleOff) : "thrust") + (t.Old.Ok ? " MATCH" : " miss") + " | pass " + F2(t.PassD) + " at " + F2(sh.Dt) + " s | nearest " + t.Bones);
        }

        public const string Header = "enemy\taction\tdir\tclip\tclip_event\tcolour\tresult\tpart\tlead\t" +
                                     "in_plane\tdepth\tangle_off\tat\tmatch\told_in_plane\told_depth\told_angle_off\told_at\told_match\t" +
                                     "pass\tpass_dt\tbar_c\told_bar_c\troot_a\troot_b\troot_vel\troot_move\thero_dist\teye\tscale\tatlas\tnear_bones\tblade_at\tblade_vel";

        /// Match numbers over the finished strikes, now and before Stage 1: the share whose bar was within 0.15 m of the
        /// blade in the bar's plane (and 0.4 m in depth) and, for blows across the plane, square to the motion within 25 deg.
        public static string Summary(string filter = null)
        {
            var rows = s_done.Where(t => t.Row != null && (filter == null || t.Enemy.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (rows.Count == 0) return "swings 0 (live " + s_live.Count + ", tracing " + On + ")";
            Func<Func<Trace, Match>, string> Line = g =>
            {
                int ok = rows.Count(t => g(t).Ok), pos = rows.Count(t => g(t).InPlane <= 0.15f && g(t).Depth <= 0.4f);
                var cross = rows.Where(t => g(t).Crossing).ToList();
                var ip = rows.Select(t => g(t).InPlane).OrderBy(x => x).ToList();
                var ao = cross.Select(t => g(t).AngleOff).OrderBy(x => x).ToList();
                return "match " + ok + "/" + rows.Count + " (" + (100f * ok / rows.Count).ToString("0") + " %) position " + pos + " in-plane median " + ip[ip.Count / 2].ToString("0.00", Inv) +
                       " p90 " + ip[Math.Min(ip.Count - 1, (int)(ip.Count * 0.9f))].ToString("0.00", Inv) + " angle " + cross.Count(t => g(t).AngleOff <= 25f) + "/" + cross.Count +
                       (ao.Count > 0 ? " median " + ao[ao.Count / 2].ToString("0", Inv) : "");
            };
            var sb = new StringBuilder("swings " + rows.Count + " | now: " + Line(t => t.New) + " | before: " + Line(t => t.Old) + " | atlas " + rows.Count(t => t.Shape != null));
            foreach (var g in rows.GroupBy(t => t.Enemy).OrderBy(g => g.Key))
                sb.Append(" | ").Append(g.Key).Append(' ').Append(g.Count(t => t.New.Ok)).Append('/').Append(g.Count()).Append(" (before ").Append(g.Count(t => t.Old.Ok)).Append(')');
            return sb.ToString();
        }

        static string Dump(string name)
        {
            string dir = Path.Combine(TestMode.Dir, "probes");
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder(Header + "\n");
            foreach (Trace t in s_done) if (t.Row != null) sb.Append(t.Row).Append('\n');
            string path = Path.Combine(dir, name + ".tsv");
            File.WriteAllText(path, sb.ToString());
            return s_done.Count(t => t.Row != null) + " swings -> " + path + " | " + Summary();
        }
    }
}

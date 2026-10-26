using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The proximity watchdog (dev and test runs; docs/table-events.md). User 2026-10-12 evening: "When I opened the coins at
    /// the end, they were inside me", "Some things still spawned too close to me", a "gained life" card that appeared close
    /// and jumped a moment later; "Can you try to check every kind of thing that can happen at the dealer table to make sure
    /// none of it has anything too close to the player?"
    ///
    /// The keep-out rule (real metres; the room's scale applied): nothing the player didn't bring there themselves may be
    ///  * within KeepEyes (0.45 m) of the eyes, or
    ///  * inside the body: a vertical cylinder of radius KeepBody (0.30 m) round the head, from the floor up to the eyes.
    /// Each frame at the Dealer's table every visible small renderer (cards, coins, tokens, dice, props; not the room, the
    /// table or the Dealer, not the mod's own gloves and boards, not what a hand holds) is measured. The first frame an object
    /// is seen is noted separately (it must already be at its final place: the user's "it jumped"); a violation is logged
    /// once per object with the game's state and the eye/body distances, and a both-eye shot is taken (at most every 8 s).
    /// Seams: `prox` (summary), `prox list` (to the log), `prox reset`, `prox on|off`. Status `proximity`.
    internal class Proximity : MonoBehaviour
    {
        public static Proximity I;
        /// (the body: 0.22 m round the eyes' vertical - the torso's front is a little ahead of the eyes, the table's edge 0.18 m;
        /// 0.30 m flagged the map's nearest row lying on the table)
        /// (2026-10-17, the user's spawn rule: "Nothing ever spawns or quickly gets brought up within 2.5 ft of the player. Menus
        /// and stuff are the exception": KeepEyes 0.45 -> 0.76 m. What lies or stands on the table - the map's near row, the dice,
        /// the figurine, the stat cards - keeps KeepOnTable from the resting eyes: the table is where it is)
        /// (21:50 the same day, the rule revised by the user: 0.53 m, and only for the moment something appears or is brought up -
        /// "Things can be close to my head", "Nothing on the table moves with the player's head". So a violation is a thing within
        /// the rule on the first frame it is seen, or one that itself moves toward the eyes faster than SpawnRule.Speed while within
        /// it; the head coming to a thing that stays put - leaning in - is not)
        public static float KeepEyes = 0.53f, KeepBody = 0.22f, KeepOnTable = 0.45f;
        /// What KeepOut draws back to exactly the rule (0.53 m) measured 0.5299: a few millimetres are the measurement's, not a
        /// violation (b1 2026-10-18: the battle's encounter card "0.53 m from the eyes" counted)
        public static float Tolerance = 0.005f;
        public static bool On = true;

        sealed class Seen
        {
            public string Name, FirstEvent, WorstEvent;
            public float FirstEye, FirstBody, WorstEye = float.MaxValue, WorstBody = float.MaxValue;
            public bool FirstBad, Reported, Touch;
            public int Frames;
            public Vector3 LastPos;
            public float LastAt;
        }

        readonly Dictionary<int, Seen> m_seen = new Dictionary<int, Seen>();
        readonly List<Renderer> m_cands = new List<Renderer>();
        float m_nextRefresh, m_nextShot;
        public static int Watching, Violations, FirstFrameViolations, Objects;
        public static string Worst = "-";
        static float s_worstEye = float.MaxValue;

        static bool Wanted => On && XRSettings.enabled && (TestMode.Enabled || DevProfile.Active || Settings.BugMark) && Places.AtCaravan && !CombatVR.Active &&
                              !VRFade.IsBlack && !Limbo.On && VRRig.I != null && VRRig.I.HasStation;

        void Awake()
        {
            I = this;
            Camera.onPreRender += Measure;
            Diag.Commands["prox"] = Command;
            Diag.Commands["lights"] = Lights;
            Diag.StatusExtras["proximity"] = () => Diag.Q(Summary());
        }

        /// `lights [metres]`: every live light within that distance of the eyes (default 3 m) - where it is, where it points, its
        /// cone and what it lights (the playtest's misaligned disc by the hourglass, 2026-10-17); the hourglass's place.
        static string Lights(string[] a)
        {
            if (VRRig.I == null) return "no rig";
            float sc = VRRig.I.Scale, within = (a.Length > 1 ? float.Parse(a[1], CultureInfo.InvariantCulture) : 3f) * sc;
            Vector3 eye = VRRig.I.NeutralEye;
            var sb = new StringBuilder();
            Defiant.Environment.HourglassController hg = Defiant.Environment.HourglassController.Instance;
            if (hg != null) sb.Append("hourglass ").Append(Diag.V(hg.transform.position)).Append(" (").Append((Vector3.Distance(hg.transform.position, eye) / sc).ToString("0.00", CultureInfo.InvariantCulture)).Append(" m) | ");
            int n = 0;
            foreach (Light l in FindObjectsOfType<Light>())
            {
                if (l == null || !l.isActiveAndEnabled) continue;
                Vector3 p = l.transform.position;
                if (l.type != LightType.Directional && Vector3.Distance(p, eye) > within) continue;
                n++;
                sb.Append('[').Append(Path(l.transform)).Append(' ').Append(l.type).Append(" at ").Append(Diag.V(p)).Append(" fwd ").Append(Diag.V(l.transform.forward))
                  .Append(" range ").Append((l.range / sc).ToString("0.00", CultureInfo.InvariantCulture)).Append(" angle ").Append(l.spotAngle.ToString("0", CultureInfo.InvariantCulture))
                  .Append(" int ").Append(l.intensity.ToString("0.00", CultureInfo.InvariantCulture)).Append(" col ").Append(ColorUtility.ToHtmlStringRGB(l.color))
                  .Append(" mask ").Append(l.cullingMask.ToString("X")).Append(' ').Append(l.renderMode).Append(" cookie ").Append(l.cookie != null ? l.cookie.name : "-");
                if (l.type == LightType.Spot)
                {
                    // where its axis meets the table top
                    float top = TableHud.TableTop(float.MinValue);
                    Vector3 f = l.transform.forward;
                    if (top > float.MinValue && f.y < -1e-3f) { Vector3 hit = p + f * ((top - p.y) / f.y); sb.Append(" hits the table at ").Append(Diag.V(hit)); if (hg != null) sb.Append(" (").Append((Vector3.Distance(hit, hg.transform.position) / sc).ToString("0.00", CultureInfo.InvariantCulture)).Append(" m from the hourglass)"); }
                }
                sb.Append("] ");
            }
            Plugin.L.LogInfo("lights: " + sb);
            return n + " lights | " + sb;
        }

        static string Summary() => (On ? "on" : "off") + " | watching " + Watching + " | objects seen " + Objects + " | violations " + Violations +
                                   " (first frame " + FirstFrameViolations + ") | closest " + Worst;

        int m_frame = -1;
        float m_nextTop, m_top = float.MinValue;

        /// Measured as drawn: while the table camera renders, after the mod's own moves (KeepOut, the token bubble, the
        /// zoom) - what the eyes see.
        void Measure(Camera cam)
        {
            if (m_frame == Time.frameCount || !Wanted) return;
            UICamera ui = UI3D.Instance != null ? UI3D.Instance.UICamera : null;
            if (ui == null || cam != ui.cachedCamera) return;
            m_frame = Time.frameCount;
            Check();
        }

        void Check()
        {
            float sc = Mathf.Max(0.01f, VRRig.I.Scale);
            if (Time.unscaledTime >= m_nextRefresh) Refresh(sc);
            Vector3 eye = VRRig.I.HeadWorldPos, neutral = VRRig.I.NeutralEye;
            float floor = VRRig.I.StationFloor.y;
            if (s_stillOp != 0) Still(sc);
            // the body is above the table top: what lies on the table can't be in it (a head leaning over the table is above
            // the cards there; the map's nearest row lies 0.21 m from the eyes' vertical, the table's edge is at 0.18 m)
            if (Time.unscaledTime >= m_nextTop) { m_nextTop = Time.unscaledTime + 2f; m_top = TableHud.TableTop(float.MinValue); }
            float bodyFrom = m_top > float.MinValue ? Mathf.Max(floor, m_top + 0.05f * sc) : floor;
            string ev = null;
            for (int i = 0; i < m_cands.Count; i++)
            {
                Renderer r = m_cands[i];
                if (r == null || !r.enabled || !r.isVisible || !r.gameObject.activeInHierarchy) continue;
                if (Held(r.transform)) continue;
                Bounds b = r.bounds;
                bool standsOnTable = m_top > float.MinValue && b.min.y < m_top + 0.12f * sc;
                // what lies or stands on the table is measured from where the player's eyes rest (the neutral eyes), not from a
                // head leaning over it: leaning down toward the dice is the player's own move (2026-10-14, the seated sweep's
                // lean: a die's glow 0.45 m from a seated head leaning 15 cm over the table, 0.49 m from the resting eyes)
                Vector3 from = standsOnTable ? neutral : eye;
                float eyeD = Vector3.Distance(b.ClosestPoint(from), from) / sc;
                float keep = (standsOnTable ? KeepOnTable : KeepEyes) - Tolerance;
                float bodyD = float.MaxValue;
                // the fight card held out to be touched is brought up (finding 4 of 2026-10-18): by the eye rule from the eyes now,
                // measured by its face - its box, tilted to the eyes, reaches nearer than any of the card
                bool fightCard = eyeD < KeepEyes + 0.15f && KeepOut.HeldFightCardFace(r.transform);
                if (fightCard)
                {
                    Vector3 np;
                    eyeD = FightCard.FaceDistance(eye, out np) / sc;
                    keep = KeepEyes - Tolerance;
                    Vector2 hd = new Vector2(np.x - eye.x, np.z - eye.z);
                    bodyD = np.y < eye.y && np.y > bodyFrom ? hd.magnitude / sc : float.MaxValue;
                    standsOnTable = false;
                }
                // (what stands on the table - its lowest point within 12 cm of the table top, e.g. the hero's figurine with the
                // sword and shield it holds a few cm up - is not in the body either: leaning over the map the head is above it,
                // the torso at the table's edge; 2026-10-13 sweeps: the figurine 0.14-0.16 m from the eyes' vertical, 0.59-0.89 m
                // below them, counted - its sword and shield part by part)
                if (!fightCard && b.min.y < eye.y && b.max.y > bodyFrom && !standsOnTable)
                {
                    float dx = Mathf.Max(0f, Mathf.Max(b.min.x - eye.x, eye.x - b.max.x)), dz = Mathf.Max(0f, Mathf.Max(b.min.z - eye.z, eye.z - b.max.z));
                    bodyD = Mathf.Sqrt(dx * dx + dz * dz) / sc;
                }
                bool nearNow = eyeD < keep || bodyD < KeepBody;
                if (nearNow && !fightCard)
                {
                    // a thing KeepOut holds is kept from the eyes of the moment it came (SpawnRule): measured from those, by the eye
                    // rule - the head leaning toward it since is the player's own move. (Also when it is low over the table: the
                    // seated b1's combat-intro cards, drawn back to 0.53 m from the head, were measured from the resting eyes by the
                    // table's rule - 0.37-0.40 m - 2026-10-18)
                    Card hc = r.GetComponentInParent<Card>();
                    Vector3 he;
                    if (hc != null && SpawnRule.TryEye(hc.GetInstanceID(), out he))
                    {
                        eyeD = Vector3.Distance(b.ClosestPoint(he), he) / sc;
                        { keep = KeepEyes - Tolerance; nearNow = eyeD < keep || bodyD < KeepBody; }
                    }
                }
                if (KeepOut.TraceOn)
                {
                    // (kotrace: what the watchdog measures for a traced card's renderers, to set beside KeepOut's own trace)
                    Card tc = r.GetComponentInParent<Card>();
                    Vector3 te;
                    if (KeepOut.TracedPath(VRUI.Path(r.transform)))
                        Plugin.L.LogInfo("kotrace " + Time.frameCount + " watchdog path " + VRUI.Path(r.transform) + " card " + (tc != null ? tc.name + " " + tc.GetInstanceID() : "none") + " eyeD " + eyeD.ToString("0.00") +
                                         " standsOnTable " + standsOnTable + " box " + Diag.V(b.center));
                    if (tc != null && KeepOut.Traced(tc))
                        Plugin.L.LogInfo("kotrace " + Time.frameCount + " watchdog " + tc.name.Replace("(Clone)", "") + "/" + r.name + " " + r.GetType().Name + " eyeD " + eyeD.ToString("0.00") + " from " +
                                         (SpawnRule.TryEye(tc.GetInstanceID(), out te) ? "its hold (" + (Vector3.Distance(te, eye) / sc).ToString("0.000") + " m from the live eye)" : standsOnTable ? "the resting eyes" : "the live eye") +
                                         " box " + Diag.V(b.center) + " size " + Diag.V(b.size) + " card at " + Diag.V(tc.transform.position) + " cam " + Camera.current?.name);
                }
                int id = r.GetInstanceID();
                Seen s;
                bool first = !m_seen.TryGetValue(id, out s);
                if (first)
                {
                    if (ev == null) ev = Event();
                    Transform own = KeepOut.Owner(r.transform);
                    s = new Seen { Name = (own != r.transform ? "texts of " : "") + Path(own) + Mat(r), FirstEye = eyeD, FirstBody = bodyD, FirstEvent = ev, Touch = KeepOut.InTouchRow(r.transform) };
                    if (s.Touch) s.Name = "(touch row) " + s.Name;
                    s.FirstBad = !s.Touch && (eyeD < keep || bodyD < KeepBody);
                    m_seen[id] = s;
                    Objects++;
                    if (s.FirstBad) FirstFrameViolations++;
                }
                s.Frames++;
                // brought toward the eyes by its own motion (not the head coming to it)
                bool approach = false;
                if (!first)
                {
                    Vector3 dc = b.center - s.LastPos;
                    float dt = Mathf.Max(Time.unscaledTime - s.LastAt, 1e-3f);
                    approach = dc.magnitude / dt > SpawnRule.Speed * sc && Vector3.Dot(dc, from - b.center) > 0f;
                }
                s.LastPos = b.center;
                s.LastAt = Time.unscaledTime;
                // a row the player touches is placed for reach on purpose: its distance is listed, not counted (asked again
                // when it comes close: a card can become touchable after it was first seen - the companion during the wheel)
                bool near = eyeD < keep || bodyD < KeepBody;
                if (near && !s.Touch && KeepOut.InTouchRow(r.transform)) { s.Touch = true; s.Name = "(touch row) " + s.Name; }
                bool bad = !s.Touch && near && (first || approach);
                if (eyeD < s.WorstEye || bodyD < s.WorstBody)
                {
                    if (ev == null) ev = Event();
                    s.WorstEye = Mathf.Min(s.WorstEye, eyeD);
                    s.WorstBody = Mathf.Min(s.WorstBody, bodyD);
                    s.WorstEvent = ev;
                }
                if (eyeD < s_worstEye)
                {
                    s_worstEye = eyeD;
                    Worst = s.Name + " " + eyeD.ToString("0.00", CultureInfo.InvariantCulture) + " m from the eyes (" + (ev ?? Event()) + ")";
                }
                if (bad && !s.Reported)
                {
                    s.Reported = true;
                    Violations++;
                    Plugin.L.LogWarning("PROXIMITY: '" + s.Name + "' " + eyeD.ToString("0.00", CultureInfo.InvariantCulture) + " m from the eyes, " +
                                        (bodyD < 100f ? bodyD.ToString("0.00", CultureInfo.InvariantCulture) : "-") + " m from the body axis" +
                                        (first ? " ON ITS FIRST FRAME" : " (first seen at " + s.FirstEye.ToString("0.00", CultureInfo.InvariantCulture) + " m)") + " | " + (ev ?? Event()));
                    // the both-eye shot only in test runs: in the player's own session (the bug mark's dev settings) a capture
                    // stalls the frame - the warning line is kept there
                    if (TestMode.Enabled && Time.unscaledTime >= m_nextShot && Diag.I != null)
                    {
                        m_nextShot = Time.unscaledTime + 8f;
                        Diag.I.Run("shot PROX_" + Violations + "_" + Safe(r.name));
                    }
                }
            }
        }

        static string Safe(string n)
        {
            var sb = new StringBuilder();
            foreach (char c in n) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.Length > 24 ? sb.ToString(0, 24) : sb.ToString();
        }

        /// The small renderers that may come close: not the room, the table, the Dealer; not the mod's own.
        void Refresh(float sc)
        {
            // (twice a second in tests; every 2 s in the player's own sessions - BugMark's: the whole-world search is the
            // watchdog's cost there)
            m_nextRefresh = Time.unscaledTime + (TestMode.Enabled || DevProfile.Active ? 0.5f : 2f);
            m_cands.Clear();
            // only what the eyes' cameras draw (the game's 2D screens are drawn by their own cameras into the panel)
            int eyeMask = 0;
            foreach (Camera c in Camera.allCameras) if (c != null && c.enabled && c.stereoTargetEye != StereoTargetEyeMask.None && c.targetTexture == null) eyeMask |= c.cullingMask;
            if (eyeMask == 0) eyeMask = ~0;
            foreach (Renderer r in FindObjectsOfType<Renderer>())
            {
                if (r == null || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                if ((eyeMask & (1 << r.gameObject.layer)) == 0) continue;
                // a card's texts (its NGUI panel's draw calls) go where the card goes: the card's own mesh is measured (the
                // draw call's box holds every widget of the panel, also ones off the card's face)
                UIDrawCall dc = r.GetComponent<UIDrawCall>();
                if (dc != null && dc.manager != null && dc.manager.GetComponentInParent<Card>() != null) continue;
                if (r.gameObject.layer == VRUI.Layer) continue;
                Vector3 size = r.bounds.size;
                if (size.x > 1.2f * sc || size.y > 1.2f * sc || size.z > 1.2f * sc) continue;   // the room, the tables, the Dealer
                if (NameSkip(r, r.transform.root)) continue;   // the mod's own, the Dealer's
                m_cands.Add(r);
            }
            Watching = m_cands.Count;
        }

        /// The names checked once per renderer (and again if its root changes): Object.name makes a new string on every read,
        /// and reading two per renderer of the whole world every refresh was most of the watchdog's garbage (2026-10-14).
        static readonly Dictionary<int, KeyValuePair<int, bool>> s_nameSkip = new Dictionary<int, KeyValuePair<int, bool>>();

        static bool NameSkip(Renderer r, Transform root)
        {
            int id = r.GetInstanceID(), rid = root.GetInstanceID();
            KeyValuePair<int, bool> v;
            if (s_nameSkip.TryGetValue(id, out v) && v.Key == rid) return v.Value;
            if (s_nameSkip.Count > 50000) s_nameSkip.Clear();
            bool skip = root.name.StartsWith("HoF2VR", StringComparison.Ordinal) || r.name.IndexOf("Dealer", StringComparison.OrdinalIgnoreCase) >= 0;
            s_nameSkip[id] = new KeyValuePair<int, bool>(rid, skip);
            return skip;
        }

        /// What a hand holds is the player's own doing (the dice, a map card picked up, a card to inspect).
        static bool Held(Transform t)
        {
            try
            {
                if (DiceHand.Holder != null && t.GetComponentInParent<Die>() != null) return true;
                if (TableCardGrab.Holder != null)
                {
                    Card c = t.GetComponentInParent<Card>();
                    if (c != null && TableCardGrab.HeldCard == c) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        static string Event()
        {
            string sc = Scenarios.Running != null ? "scenario " + Scenarios.Running.Label + ", " : "";
            return sc + GameWatch.State.Replace("GameState_", "") + " / " + Diag.CurrentMenu();
        }

        /// The material too (objects the game leaves as "New Game Object" are told apart by it).
        static string Mat(Renderer r)
        {
            Material m = r.sharedMaterial;
            return m != null ? " [" + m.name.Replace(" (Instance)", "") + "]" : "";
        }

        static string Path(Transform t)
        {
            string p = t.name;
            Transform x = t.parent;
            for (int i = 0; i < 3 && x != null; i++, x = x.parent) p = x.name + "/" + p;
            return p;
        }

        // ---- `prox still mark|check`: nothing on the table moves with the head (the second playtest, P2-1) ----------------------
        // Marks where every card, token, stack, die and the figurine is drawn (as drawn: after KeepOut's moves), then - after the
        // head has moved - how far each has moved since, as drawn, and how far the mod's own draw-time move of it changed (drawn
        // minus the game's pose of the same frame: the game's idle motion cancels out). Nothing may move by the mod's moves.
        static int s_stillOp;
        static readonly Dictionary<int, Vector3> s_still = new Dictionary<int, Vector3>();
        static readonly Dictionary<int, string> s_stillNames = new Dictionary<int, string>();
        public static string StillResult = "-";

        static bool TableThing(Renderer r)
        {
            Transform t = r.transform;
            if (t.GetComponentInParent<Card>() != null || t.GetComponentInParent<Token>() != null || t.GetComponentInParent<Shard>() != null ||
                t.GetComponentInParent<DeckLayout>() != null || t.GetComponentInParent<Die>() != null) return true;
            Map m = Map.Instance;
            return m != null && m.PlayerCounter != null && t.IsChildOf(m.PlayerCounter.transform);
        }

        // where each was before this frame's draw-time moves (LateUpdate, the game's pose): drawn minus this is the mod's own move,
        // which must not change with the head; the game's own motion (a card's idle hover) cancels out of it
        static readonly Dictionary<int, Vector3> s_gamePos = new Dictionary<int, Vector3>();
        static readonly Dictionary<int, Vector3> s_stillOff = new Dictionary<int, Vector3>();
        static int s_gameFrame = -1;

        void LateUpdate()
        {
            if (s_stillOp == 0) return;
            s_gamePos.Clear();
            s_gameFrame = Time.frameCount;
            foreach (Renderer r in m_cands)
                if (r != null && r.enabled && r.gameObject.activeInHierarchy && TableThing(r)) s_gamePos[r.GetInstanceID()] = r.bounds.center;
        }

        void Still(float sc)
        {
            if (s_gameFrame != Time.frameCount) return;   // (the game's pose of this same frame first)
            int op = s_stillOp;
            s_stillOp = 0;
            var inv = CultureInfo.InvariantCulture;
            if (op == 1)
            {
                s_still.Clear();
                s_stillOff.Clear();
                s_stillNames.Clear();
                foreach (Renderer r in m_cands)
                {
                    Vector3 g;
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || Held(r.transform) || !s_gamePos.TryGetValue(r.GetInstanceID(), out g)) continue;
                    s_still[r.GetInstanceID()] = r.bounds.center;
                    s_stillOff[r.GetInstanceID()] = r.bounds.center - g;
                    s_stillNames[r.GetInstanceID()] = Path(r.transform);
                }
                StillResult = "marked " + s_still.Count + " at " + Diag.V(VRRig.I.HeadWorldPos);
            }
            else
            {
                int n = 0, moved = 0, modMoved = 0;
                var movers = new List<KeyValuePair<float, string>>();
                float max = 0f, modMax = 0f;
                foreach (Renderer r in m_cands)
                {
                    Vector3 was, g;
                    int id = r != null ? r.GetInstanceID() : 0;
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || !s_still.TryGetValue(id, out was) || !s_gamePos.TryGetValue(id, out g)) continue;
                    n++;
                    float d = Vector3.Distance(was, r.bounds.center) / sc;
                    float dm = Vector3.Distance(s_stillOff[id], r.bounds.center - g) / sc;
                    if (d > max) max = d;
                    if (dm > modMax) modMax = dm;
                    if (dm > 0.001f) modMoved++;
                    if (d > 0.002f) { moved++; movers.Add(new KeyValuePair<float, string>(d, s_stillNames[id] + (dm > 0.001f ? " (the mod's move " + (dm * 1000f).ToString("0", inv) + " mm)" : " (the game's own)"))); }
                }
                movers.Sort((x, y) => y.Key.CompareTo(x.Key));
                var sb = new StringBuilder();
                for (int i = 0; i < movers.Count && i < 6; i++) sb.Append(" | ").Append(movers[i].Value).Append(' ').Append((movers[i].Key * 1000f).ToString("0", inv)).Append(" mm");
                StillResult = "compared " + n + " of " + s_still.Count + ", moved over 2 mm " + moved + ", most " + (max * 1000f).ToString("0.0", inv) + " mm; by the mod's moves " +
                              modMoved + ", most " + (modMax * 1000f).ToString("0.0", inv) + " mm, head at " + Diag.V(VRRig.I.HeadWorldPos) + sb;
                Plugin.L.LogInfo("proximity: still " + StillResult);
            }
        }

        string Command(string[] a)
        {
            string op = a.Length > 1 ? a[1] : "";
            switch (op)
            {
                case "still":
                    if (a.Length > 2 && a[2] == "mark") { s_stillOp = 1; StillResult = "marking"; return "marking"; }
                    if (a.Length > 2 && a[2] == "check") { s_stillOp = 2; StillResult = "checking"; return "checking"; }
                    return StillResult;
                case "on": On = true; return "on";
                case "off": On = false; return "off";
                case "reset":
                    m_seen.Clear();
                    Objects = Violations = FirstFrameViolations = 0;
                    s_worstEye = float.MaxValue;
                    Worst = "-";
                    return "reset";
                case "list":
                    {
                        var sb = new StringBuilder("proximity: " + Summary());
                        var list = new List<Seen>(m_seen.Values);
                        list.Sort((x, y) => x.WorstEye.CompareTo(y.WorstEye));
                        for (int i = 0; i < list.Count && i < 40; i++)
                        {
                            Seen s = list[i];
                            sb.Append("\n  ").Append(s.FirstBad ? "FIRST " : s.Reported ? "BAD   " : "ok    ").Append(s.Name)
                              .Append(" first ").Append(s.FirstEye.ToString("0.00", CultureInfo.InvariantCulture)).Append(" m (").Append(s.FirstEvent).Append(")")
                              .Append(" closest ").Append(s.WorstEye.ToString("0.00", CultureInfo.InvariantCulture)).Append(" m eyes, ")
                              .Append(s.WorstBody < 100f ? s.WorstBody.ToString("0.00", CultureInfo.InvariantCulture) : "-").Append(" m body (").Append(s.WorstEvent).Append(")");
                        }
                        Plugin.L.LogInfo(sb.ToString());
                        return list.Count + " objects (see log)";
                    }
                default: return Summary();
            }
        }
    }
}

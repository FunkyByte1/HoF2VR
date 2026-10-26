using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Defiant;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// Phase-0 probe of the interaction design: where every interactable family is, measured from
    /// the player's neutral eye in real metres (OK < 0.65 m: arm's length, LEAN < 0.9 m, FAR beyond),
    /// plus the 3D UI camera's picking setup and the dice roller's internals.
    ///   reach [tag]     -> log + testdata/reach/<tag or state>.txt
    internal static class ReachProbe
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static readonly AccessTools.FieldRef<DiceRoller, Transform> s_gather = AccessTools.FieldRefAccess<DiceRoller, Transform>("m_gatherContainer");
        static readonly AccessTools.FieldRef<DiceRoller, Transform> s_target = AccessTools.FieldRefAccess<DiceRoller, Transform>("m_targetContainer");
        static readonly AccessTools.FieldRef<DiceRoller, Transform> s_noForce = AccessTools.FieldRefAccess<DiceRoller, Transform>("m_noForceThrowTarget");
        static readonly AccessTools.FieldRef<DiceRoller, Transform> s_dragCentre = AccessTools.FieldRefAccess<DiceRoller, Transform>("m_dragCentrePoint");
        static readonly AccessTools.FieldRef<DiceRoller, Transform> s_rowContainer = AccessTools.FieldRefAccess<DiceRoller, Transform>("m_container");
        static readonly AccessTools.FieldRef<DiceRoller, DiceRoller.GatherArea> s_area = AccessTools.FieldRefAccess<DiceRoller, DiceRoller.GatherArea>("m_handGatherArea");
        static readonly AccessTools.FieldRef<DiceRoller, float> s_dragRadius = AccessTools.FieldRefAccess<DiceRoller, float>("m_dragRadius");
        static readonly AccessTools.FieldRef<DiceRoller, float> s_minF = AccessTools.FieldRefAccess<DiceRoller, float>("m_minCatapaultForce");
        static readonly AccessTools.FieldRef<DiceRoller, float> s_maxF = AccessTools.FieldRefAccess<DiceRoller, float>("m_maxCatapaultForce");
        static readonly AccessTools.FieldRef<DiceRoller, float> s_noForceStrength = AccessTools.FieldRefAccess<DiceRoller, float>("m_noForceThrowStrength");
        static readonly AccessTools.FieldRef<DiceRoller, float> s_looseMod = AccessTools.FieldRefAccess<DiceRoller, float>("m_looseModifier");
        static readonly AccessTools.FieldRef<DiceRoller, FloatRange> s_forceRange = AccessTools.FieldRefAccess<DiceRoller, FloatRange>("m_forceRange");
        static readonly AccessTools.FieldRef<DiceRoller, Vector3Range> s_torqueRange = AccessTools.FieldRefAccess<DiceRoller, Vector3Range>("m_torqueRange");
        static readonly AccessTools.FieldRef<DiceGame, Collider[]> s_sideCols = AccessTools.FieldRefAccess<DiceGame, Collider[]>("m_sideColliders");

        public static void Register()
        {
            Diag.Commands["reach"] = a =>
            {
                string tag = a.Length > 1 ? a[1] : GameWatch.State.Replace("GameState_", "");
                string text;
                try { text = Describe(); }
                catch (Exception e) { text = "reach failed: " + e; }
                Plugin.L.LogInfo("reach " + tag + ":" + text);
                try
                {
                    string dir = Path.Combine(TestMode.Dir ?? ".", "reach");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, tag + ".txt"), "reach " + tag + " (state " + GameWatch.State + ", place " + Places.Current + ")" + text + "\n");
                }
                catch (Exception e) { Plugin.L.LogWarning("reach: " + e.Message); }
                return "logged " + tag;
            };
            // `seatreach tip l|r` / `seatreach X Y Z`: the seated reach of the fingertip now (where a test's poke ends) or of a world
            // point, from the neutral eye in the player's frame (SeatedRate) - also in standing runs (what a seated player would need)
            Diag.Commands["seatreach"] = a =>
            {
                VRRig rig = VRRig.I;
                if (rig == null) return "no rig";
                Vector3 p;
                if (a.Length > 2 && a[1] == "tip")
                {
                    Hand h = a[2] == "l" ? VRInput.L : VRInput.R;
                    if (!HandWorld.Valid(h)) return "no hand";
                    // the glove's fingertip, what touches (HandWorld.Tip is the controller's pointer tip, ~0.14 m further
                    // ahead: the seated tour's first run rated its pokes that much too far)
                    p = TouchInput.TipOf(h);
                }
                else if (a.Length > 3)
                {
                    p = new Vector3(float.Parse(a[1], Inv), float.Parse(a[2], Inv), float.Parse(a[3], Inv));
                }
                else return "usage: seatreach tip l|r | seatreach X Y Z";
                Vector3 rel = Quaternion.Inverse(rig.NeutralRot) * (p - rig.NeutralEye) / rig.Scale;
                float sd;
                string r = SeatedRate(rel, out sd);
                return string.Format(Inv, "{0} shoulder {1:0.00} ahead {2:0.00} below {3:0.00} right {4:0.00} eye {5:0.00} ({6})", r, sd, rel.z, -rel.y, rel.x, rel.magnitude, VRRig.PostureNote);
            };
            // `fonts`: the mod's own texts shown now (labels under HoF2VR_ objects): every one in the game's fonts (the user
            // 2026-10-11: "generic, flat, and boring" text is not OK - the coverage audit's L8), and how big and how turned they
            // are from the eyes (the line's angular height; the angle between the text's face and the line of sight)
            Diag.Commands["fonts"] = a =>
            {
                VRRig rig = VRRig.I;
                Vector3 eye = rig != null ? rig.HeadWorldPos : Vector3.zero;
                float sc = rig != null ? rig.Scale : 1f;
                int n = 0, game = 0, inView = 0;
                var other = new StringBuilder();
                var byFont = new Dictionary<string, int>();
                float smallest = 999f, oblique = 0f;
                string smallestName = "-", obliqueName = "-";
                foreach (UILabel l in UnityEngine.Object.FindObjectsOfType<UILabel>())
                {
                    if (l == null || !l.isActiveAndEnabled || string.IsNullOrEmpty(l.text) || l.alpha < 0.05f) continue;
                    if (!l.transform.root.name.StartsWith("HoF2VR")) continue;
                    n++;
                    string fn = l.bitmapFont != null ? l.bitmapFont.name : l.trueTypeFont != null ? l.trueTypeFont.name + " (TTF)" : "none";
                    bool ours = l.bitmapFont != null && (l.bitmapFont == GameText.Normal || l.bitmapFont == GameText.Stylised || l.bitmapFont == GameText.StylisedItalic || l.bitmapFont == GameText.CardDecal);
                    if (ours) game++; else if (other.Length < 300) other.Append(VRUI.Path(l.transform)).Append(" (").Append(fn).Append("); ");
                    int c;
                    byFont[fn] = byFont.TryGetValue(fn, out c) ? c + 1 : 1;
                    if (rig == null || l.gameObject.layer != VRUI.Layer) continue;
                    Vector3 p = l.transform.position;
                    float d = Vector3.Distance(p, eye) / sc;
                    if (d > 3f || d < 0.05f) continue;
                    inView++;
                    float h = l.fontSize * Mathf.Abs(l.transform.lossyScale.y) / sc;   // one line, real metres
                    float deg = Mathf.Atan2(h, d) * Mathf.Rad2Deg;
                    if (deg < smallest) { smallest = deg; smallestName = VRUI.Path(l.transform) + " '" + (l.text.Length > 20 ? l.text.Substring(0, 20) : l.text).Replace("\n", " ") + "'"; }
                    float turn = Vector3.Angle(-l.transform.forward, eye - p);
                    if (turn > oblique) { oblique = turn; obliqueName = VRUI.Path(l.transform); }
                }
                var fonts = new StringBuilder();
                foreach (var kv in byFont) fonts.Append(kv.Key).Append(' ').Append(kv.Value).Append(", ");
                return string.Format(Inv, "{0} mod labels, {1} in the game's fonts ({2}) | in view {3}: the smallest line {4:0.00} deg ({5}), the most turned {6:0} deg ({7}){8}",
                    n, game, fonts.ToString().TrimEnd(',', ' '), inView, smallest == 999f ? 0f : smallest, smallestName, oblique, obliqueName,
                    other.Length > 0 ? " | NOT the game's: " + other : "");
            };
            // `selection`: the game's selection state per category (group, hover, locks) - why a hover is refused
            Diag.Commands["selection"] = a =>
            {
                var sb = new StringBuilder();
                UISelection[] all = UISelectionManager.Selections;
                if (all == null) return "no selection manager";
                foreach (UISelection sel in all)
                {
                    if (sel == null) continue;
                    sb.AppendFormat("{0}: valid={1} group={2} hover={3} lockedGroup={4} lockedObj={5} blocked={6} | ", sel.Category, sel.IsValid,
                        sel.SelectedGroup ? sel.SelectedGroup.name : "-", sel.HoveredObject ? sel.HoveredObject.name : "-",
                        sel.LockedGroup ? sel.LockedGroup.name : "-", sel.LockedObject ? sel.LockedObject.name : "-", sel.IsBlocked);
                }
                return sb.ToString();
            };
            HandWorld.Targets["dice"] = () => { Transform t = DiceT(s_gather); return t != null ? t.position : (Vector3?)null; };
            HandWorld.Targets["tray"] = () => { Transform t = DiceT(s_target); return t != null ? t.position : (Vector3?)null; };
            HandWorld.Targets["pendulum"] = () => CardTable.Instance != null && CardTable.Instance.Pendulum != null ? CardTable.Instance.Pendulum.transform.position : (Vector3?)null;
            HandWorld.Targets["wheel"] = () => DeckManager.Instance != null && DeckManager.Instance.WheelSpinContainer != null ? DeckManager.Instance.WheelSpinContainer.transform.position : (Vector3?)null;
            // a point on the table top straight ahead of the player, 0.25 m past the near edge
            HandWorld.Targets["table"] = () =>
            {
                Bounds b;
                if (!TableBounds(out b) || VRRig.I == null) return null;
                Vector3 eye = VRRig.I.NeutralEye;
                Vector3 fwd = VRRig.I.NeutralRot * Vector3.forward;
                var ray = new Ray(new Vector3(eye.x, b.max.y - 0.01f, eye.z), fwd);
                float enter;
                if (!b.IntersectRay(ray, out enter)) enter = 0f;
                Vector3 p = ray.GetPoint(enter + 0.25f * VRRig.I.Scale);
                p.y = b.max.y;
                return p;
            };
        }

        static Transform DiceT(AccessTools.FieldRef<DiceRoller, Transform> f)
        {
            DiceRoller r = Roller();
            return r != null ? f(r) : null;
        }

        public static DiceRoller Roller() => CardTable.Instance != null && CardTable.Instance.DiceGame != null ? CardTable.Instance.DiceGame.DiceRoller : null;

        static Collider s_table;

        /// The card table's top (the caravan has few colliders; this one is the table's own box).
        public static bool TableBounds(out Bounds b)
        {
            b = new Bounds();
            if (s_table == null)
            {
                GameObject go = GameObject.Find("Models/Dealer_Caravan/dealer_caravan_cardtable/Table/Collider");
                s_table = go != null ? go.GetComponent<Collider>() : null;
            }
            if (s_table == null) return false;
            b = s_table.bounds;
            return true;
        }

        static string Rate(float m) => m < 0.65f ? "OK" : m < 0.9f ? "LEAN" : "FAR";

        // Seated reach (2026-10-13; docs/reach-seated-2026-10-14.txt). A seated player can't step: from the shoulder nearer the
        // point - 0.20 m below the eyes, 0.05 m behind them, 0.18 m to the side - the fingertip reaches 0.62 m with the arm
        // comfortably out (OK); leaning forward from the hips (the shoulders ~0.20 m forward, 0.05 m down) the same arm
        // reaches further (LEAN); beyond that FAR. A point more than 0.50 m below the eyes, ahead of them within 0.6 m and in
        // front of the body (0.3 m to either side) is in the lap (LAP): seated thighs and knees are there.
        public static float SeatedArm = 0.62f, ShoulderDown = 0.20f, ShoulderBack = 0.05f, ShoulderSide = 0.18f, LeanAhead = 0.20f, LeanDown = 0.05f, LapBelow = 0.50f;
        public static int SeatedOk, SeatedLean, SeatedFar, SeatedLap;

        /// rel: from the neutral eye in the player's frame, real metres (x right, y up, z ahead).
        public static string SeatedRate(Vector3 rel, out float shoulderDist)
        {
            Vector3 sh = new Vector3(rel.x >= 0f ? ShoulderSide : -ShoulderSide, -ShoulderDown, -ShoulderBack);
            shoulderDist = (rel - sh).magnitude;
            float lean = (rel - (sh + new Vector3(0f, -LeanDown, LeanAhead))).magnitude;
            string r = shoulderDist <= SeatedArm ? "OK" : lean <= SeatedArm ? "LEAN" : "FAR";
            if (rel.y < -LapBelow && rel.z > 0f && rel.z < 0.6f && Mathf.Abs(rel.x) < 0.3f) r += "+LAP";
            return r;
        }

        /// One line: world position, distance from the neutral eye in real metres (ahead / below / right). `target` = a thing
        /// the player touches now (counted in the seated summary).
        static void Line(StringBuilder sb, string label, Vector3 p, string extra = null, Transform t = null, bool target = false)
        {
            VRRig rig = VRRig.I;
            float s = rig != null ? rig.Scale : 1f;
            Vector3 eye = rig != null ? rig.NeutralEye : Vector3.zero;
            Quaternion inv = rig != null ? Quaternion.Inverse(rig.NeutralRot) : Quaternion.identity;
            Vector3 rel = inv * (p - eye) / s;
            float d = rel.magnitude;
            sb.AppendFormat(Inv, "\n  {0,-34} {1} d={2:0.00} m (ahead {3:0.00}, below {4:0.00}, right {5:0.00}) {6}", label, Diag.V(p), d, rel.z, -rel.y, rel.x, Rate(d));
            if (Settings.Seated)
            {
                float sd;
                string r = SeatedRate(rel, out sd);
                sb.AppendFormat(Inv, " | seated {0} (shoulder {1:0.00} m)", r, sd);
                if (target)
                {
                    if (r.StartsWith("OK")) SeatedOk++; else if (r.StartsWith("LEAN")) SeatedLean++; else { SeatedFar++; FarList.Append(label.Trim()).Append("; "); }
                    if (r.EndsWith("LAP")) { SeatedLap++; FarList.Append("(lap) ").Append(label.Trim()).Append("; "); }
                    sb.Append(" [touch]");
                }
            }
            if (extra != null) sb.Append(" ").Append(extra);
            if (t != null) sb.Append(" | ").Append(VRUI.Path(t));
        }

        static readonly StringBuilder FarList = new StringBuilder();

        static void Family(StringBuilder sb, string title, System.Action body)
        {
            sb.Append("\n").Append(title).Append(':');
            try { body(); }
            catch (Exception e) { sb.Append(" (error: ").Append(e.GetType().Name).Append(' ').Append(e.Message).Append(')'); }
        }

        static void Cards(StringBuilder sb, string label, CardContainer c, int max = 12, bool touch = false)
        {
            if (c == null) { sb.Append("\n  ").Append(label).Append(": -"); return; }
            Line(sb, label + " container", c.transform.position, "cards=" + c.Cards.Count + " parent=" + (c.transform.parent ? VRUI.Path(c.transform.parent) : "-"), c.transform);
            int n = 0;
            foreach (Card card in c.Cards)
            {
                if (card == null || n++ >= max) continue;
                bool sel = card.Selectable != null && card.Selectable.IsSelectable;
                // a card the player can pick now (selectable, shown) is a touch target; touch = every shown card of a touch row
                bool shown = card.gameObject.activeInHierarchy;
                Line(sb, "  card " + card.name, card.transform.position, card.Selectable != null ? "sel=" + sel : null, null, shown && (sel || touch));
            }
        }

        public static string Describe()
        {
            var sb = new StringBuilder();
            VRRig rig = VRRig.I;
            if (rig == null) return " no rig";
            sb.AppendFormat(Inv, "\nplayer: place {0} station floor {1} yaw {2:0} scale {3:0.00} eye height {4:0.00} m -> neutral eye {5}",
                Places.Current, Diag.V(rig.StationFloor), rig.StationYaw, rig.Scale, rig.EyeHeight, Diag.V(rig.NeutralEye));
            sb.Append("\n(distances from the neutral eye in real metres; OK < 0.65 arm's length, LEAN < 0.9, FAR beyond)");
            SeatedOk = SeatedLean = SeatedFar = SeatedLap = 0;
            FarList.Length = 0;
            if (Settings.Seated)
                sb.AppendFormat(Inv, "\n(seated: {0}; reach from the nearer shoulder {1:0.00} m below / {2:0.00} m behind / {3:0.00} m beside the eyes: OK <= {4:0.00} m, LEAN = the same leaning {5:0.00} m forward, LAP = more than {6:0.00} m below the eyes over the thighs)",
                    VRRig.PostureNote, ShoulderDown, ShoulderBack, ShoulderSide, SeatedArm, LeanAhead, LapBelow);

            Family(sb, "hands", () =>
            {
                foreach (Hand h in new[] { VRInput.L, VRInput.R })
                    if (HandWorld.Valid(h)) { Line(sb, (h.IsLeft ? "L" : "R") + " palm", HandWorld.Palm(h)); Line(sb, (h.IsLeft ? "L" : "R") + " fingertip", HandWorld.Tip(h)); }
            });

            Family(sb, "table", () =>
            {
                Bounds b;
                if (!TableBounds(out b)) { sb.Append(" collider not found"); return; }
                sb.AppendFormat(Inv, "\n  table collider min {0} max {1} (top y {2:0.###})", Diag.V(b.min), Diag.V(b.max), b.max.y);
                Vector3 eye = rig.NeutralEye;
                Vector3 fwd = rig.NeutralRot * Vector3.forward;
                var ray = new Ray(new Vector3(eye.x, b.max.y - 0.01f, eye.z), fwd);
                float enter;
                if (b.IntersectRay(ray, out enter))
                {
                    Vector3 edge = ray.GetPoint(enter);
                    edge.y = b.max.y;
                    Line(sb, "near edge (straight ahead)", edge, string.Format(Inv, "horizontal {0:0.00} m, top {1:0.00} m above the floor", enter / rig.Scale, (b.max.y - rig.StationFloor.y) / rig.Scale));
                }
                Vector3? tp = HandWorld.Targets["table"]();
                if (tp != null) Line(sb, "table point 0.25 m past the edge", tp.Value);
            });

            Family(sb, "3D UI camera (picking)", () =>
            {
                UI3D ui = UI3D.Instance;
                UICamera uc = ui != null ? ui.UICamera : null;
                if (uc == null) { sb.Append(" -"); return; }
                sb.AppendFormat(Inv, "\n  '{0}' eventType={1} eventReceiverMask=0x{2:X} cullingMask=0x{3:X} pick mask=0x{4:X} eventsGoToColliders={5}",
                    VRUI.Path(uc.transform), uc.eventType, (int)uc.eventReceiverMask, uc.cachedCamera.cullingMask, uc.cachedCamera.cullingMask & (int)uc.eventReceiverMask, uc.eventsGoToColliders);
            });

            Family(sb, "map", () =>
            {
                Map map = Map.Instance;
                if (map == null || map.MapLayout == null) { sb.Append(" -"); return; }
                if (map.PlayerCounter != null) Line(sb, "hero figurine", map.PlayerCounter.transform.position, null, map.PlayerCounter.transform, GameWatch.State == "GameState_Map");
                float best = float.MaxValue, worst = 0f;
                int n = 0;
                foreach (MapLayoutSlot slot in map.MapLayout.Slots)
                {
                    if (slot == null || !slot.gameObject.activeInHierarchy) continue;
                    Collider c = slot.GetComponent<Collider>();
                    Vector3 p = c != null ? c.bounds.center : slot.transform.position;
                    float d = Vector3.Distance(p, rig.NeutralEye) / rig.Scale;
                    best = Mathf.Min(best, d);
                    worst = Mathf.Max(worst, d);
                    if (n++ < 40)
                        Line(sb, "slot (" + Mathf.RoundToInt(slot.GridPosition.x) + ", " + Mathf.RoundToInt(slot.GridPosition.y) + ")", p,
                            (slot.CanMoveTo ? "movable" : "") + (c != null ? " box " + Diag.V(c.bounds.size) : ""), null, slot.CanMoveTo && GameWatch.State == "GameState_Map");
                }
                sb.AppendFormat(Inv, "\n  {0} slots, nearest {1:0.00} m, farthest {2:0.00} m", n, best, worst);
            });

            Family(sb, "dice", () =>
            {
                DiceRoller r = Roller();
                if (r == null) { sb.Append(" -"); return; }
                Transform g = s_gather(r);
                DiceRoller.GatherArea area = s_area(r);
                if (g != null) Line(sb, "gather container (dice in hand)", g.position, "lossy " + Diag.V(g.lossyScale), g);
                if (area != null && area.Bounds != null)
                {
                    SphereCollider sc = area.Bounds;
                    float rad = sc.radius * Mathf.Max(sc.transform.lossyScale.x, Mathf.Max(sc.transform.lossyScale.y, sc.transform.lossyScale.z));
                    Line(sb, "gather area sphere", sc.transform.TransformPoint(sc.center), string.Format(Inv, "radius {0:0.###} u = {1:0.###} m, positions {2}", rad, rad / rig.Scale, area.Max), sc.transform);
                }
                Transform t;
                if ((t = s_dragCentre(r)) != null) Line(sb, "drag centre", t.position, string.Format(Inv, "drag radius {0:0.###}", s_dragRadius(r)), t);
                if ((t = s_target(r)) != null) Line(sb, "throw target", t.position, null, t);
                if ((t = s_noForce(r)) != null) Line(sb, "no-force throw target", t.position, null, t);
                if ((t = s_rowContainer(r)) != null) Line(sb, "result row", t.position, null, t);
                FloatRange fr = s_forceRange(r);
                Vector3Range tr = s_torqueRange(r);
                sb.AppendFormat(Inv, "\n  forces: catapult {0:0.###}..{1:0.###}, no-force {2:0.###}, random +{3} ({4}), torque {5} ({6}), loose {7:0.###}, state {8}",
                    s_minF(r), s_maxF(r), s_noForceStrength(r), fr != null ? Diag.V(new Vector3(fr.Min, fr.Max, 0)) : "-", fr != null ? fr.Mode.ToString() : "-",
                    tr != null ? Diag.V(tr.Min) + ".." + Diag.V(tr.Max) : "-", tr != null ? tr.Mode.ToString() : "-", s_looseMod(r), DiceHand.StateOf(r));
                int n = 0;
                foreach (Die d in r.ActiveDice)
                {
                    if (d == null) continue;
                    Rigidbody rb = d.GetComponent<Rigidbody>();
                    Collider c = d.GetComponent<Collider>();
                    Line(sb, "die " + n++, d.transform.position, string.Format(Inv, "use={0} value={1} size {2} mass {3:0.###}", d.Use, d.Value,
                        c != null ? Diag.V(c.bounds.size) : "-", rb != null ? rb.mass : 0f), null, d.gameObject.activeInHierarchy);
                }
                Collider[] sides = CardTable.Instance.DiceGame != null ? s_sideCols(CardTable.Instance.DiceGame) : null;
                if (sides != null)
                {
                    var tray = new Bounds();
                    int k = 0;
                    foreach (Collider c in sides)
                        if (c != null) { if (k++ == 0) tray = c.bounds; else tray.Encapsulate(c.bounds); }
                    if (k > 0) sb.AppendFormat(Inv, "\n  tray walls ({0}, enabled={1}): min {2} max {3}", k, sides[0] != null && sides[0].enabled, Diag.V(tray.min), Diag.V(tray.max));
                }
            });

            Family(sb, "pendulum", () =>
            {
                Pendulum p = CardTable.Instance != null ? CardTable.Instance.Pendulum : null;
                if (p == null) { sb.Append(" -"); return; }
                Line(sb, "pendulum", p.transform.position, "swinging=" + p.IsSwinging + " active=" + p.gameObject.activeInHierarchy, p.transform, p.IsSwinging);
                int nr = 0;
                foreach (Renderer rd in p.GetComponentsInChildren<Renderer>())
                {
                    if (!rd.enabled || rd is ParticleSystemRenderer || nr++ > 0) continue;
                    int n;
                    Bounds b = TableGeo.RendererBounds(p.gameObject, out n);
                    sb.AppendFormat(Inv, "\n  pendulum renderers: {0}, bounds min {1} max {2}", n, Diag.V(b.min), Diag.V(b.max));
                }
            });

            Family(sb, "wheel", () =>
            {
                DeckManager dm = DeckManager.Instance;
                if (dm == null || dm.WheelSpinContainer == null) { sb.Append(" -"); return; }
                Cards(sb, "wheel", dm.WheelSpinContainer, 16, true);
            });

            Family(sb, "presented cards", () =>
            {
                UI3D ui = UI3D.Instance;
                if (ui == null) { sb.Append(" -"); return; }
                if (ui.CardChoice != null) Cards(sb, "CardChoice", ui.CardChoice.Container);
                if (ui.CardZoom != null) Cards(sb, "CardZoom", ui.CardZoom.Container);
                if (ui.TokenFocusPivot != null) Line(sb, "token focus pivot", ui.TokenFocusPivot.position, null, ui.TokenFocusPivot);
                DeckManager dm = DeckManager.Instance;
                if (dm != null)
                {
                    if (dm.CardSetModifierContainer != null) Cards(sb, "reward cards", dm.CardSetModifierContainer);
                    Cards(sb, "encounter", dm.EncounterContainer, 3);
                    Cards(sb, "display", dm.DisplayContainer, 6);
                    if (dm.StatHand != null) Line(sb, "player stat hand", dm.StatHand.transform.position, null, dm.StatHand.transform);
                }
            });

            Family(sb, "side table", () =>
            {
                ChallengeMap cm = ChallengeMap.Instance;
                if (cm == null || cm.Locations == null) { sb.Append(" -"); return; }
                int n = 0;
                foreach (ChallengeMapLocation loc in cm.Locations.AllLocations)
                    if (loc != null && n++ < 30) Line(sb, "pin " + loc.name, loc.transform.position, loc.gameObject.activeInHierarchy ? null : "(hidden)", null,
                                                      loc.gameObject.activeInHierarchy && Places.Current == Places.Place.Hub && GameWatch.State == "GameState_ChallengeMap");
            });

            Family(sb, "touch plates (every board shown now)", () =>
            {
                int n = 0;
                PlateTouch.ForEachShown((tag, label, p) => { n++; Line(sb, tag + " [" + label + "]", p.RestFace, null, null, true); });
                if (n == 0) sb.Append(" none shown");
            });

            Family(sb, "tokens (coins)", () =>
            {
                int n = 0;
                foreach (Token t in UnityEngine.Object.FindObjectsOfType<Token>())
                {
                    if (t == null || !t.isActiveAndEnabled) continue;
                    Renderer rd = t.GetComponentInChildren<Renderer>();
                    if (rd == null || !rd.enabled || !rd.gameObject.activeInHierarchy) continue;
                    // a token is touched only at a challenge's end (its coins to open); elsewhere it just stands on the table
                    Line(sb, "token " + t.name, rd.bounds.center, null, t.transform, GameWatch.State.IndexOf("Token", StringComparison.Ordinal) >= 0);
                    n++;
                }
                if (n == 0) sb.Append(" none shown");
            });

            Family(sb, "deck builder", () =>
            {
                AdventureDeckBuilder db = UnityEngine.Object.FindObjectOfType<AdventureDeckBuilder>();
                DeckBuilder cb = DeckBuilder.Instance;
                bool any = false;
                if (db != null)
                {
                    if (db.DeckBrowser != null) { Cards(sb, "deck row", db.DeckBrowser.Container, 8, db.isActiveAndEnabled); any = true; }
                    if (db.ChoiceBrowser != null) { Cards(sb, "choice row", db.ChoiceBrowser.Container, 8, db.isActiveAndEnabled); any = true; }
                }
                if (cb != null && cb.IsActive && cb.ActiveMode != null && cb.ActiveMode.PlayerDeckContainer != null) { Cards(sb, "challenge deck row", cb.ActiveMode.PlayerDeckContainer, 8, true); any = true; }
                if (!any) sb.Append(" -");
            });

            Family(sb, "2D panel", () =>
            {
                if (VRUI.I == null) { sb.Append(" -"); return; }
                Line(sb, "panel centre", VRUI.I.PanelPixelToWorld(new Vector2(VRUI.Width / 2f, VRUI.Height / 2f)));
                Line(sb, "panel nav bar (960,85)", VRUI.I.PanelPixelToWorld(new Vector2(960f, 85f)));
            });
            if (Settings.Seated) sb.AppendFormat(Inv, "\nseated summary (touch targets now): OK {0}, LEAN {1}, FAR {2}, in the lap {3}{4}", SeatedOk, SeatedLean, SeatedFar, SeatedLap,
                                                 FarList.Length > 0 ? " | " + FarList.ToString(0, Math.Min(FarList.Length, 400)) : "");
            return sb.ToString();
        }
    }
}

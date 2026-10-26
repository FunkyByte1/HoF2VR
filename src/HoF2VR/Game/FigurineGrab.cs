using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// More hands-on table play (feature A; the user 2026-10-12 ~22:40, "I like it": "pick up the hero figurine and put it
    /// on the next spot of the map"). On the dungeon map, an open hand over the hero's figurine (the game's PlayerCounter)
    /// gets ready (a tick); a squeeze picks it up - it stays where it was in the hand and follows it. Carried over a place
    /// the hero can go to, that place lights up as when a fingertip is on it; let go there and the game moves the hero
    /// there (exactly as touching the tile: its hover and select, so its rules - one step, food - all apply; the figurine
    /// is put back first so the game walks it there itself). Let go anywhere else and it glides back to where it stood.
    /// Never while the map is busy. Setting [Hands] GrabThrow (with the cards and the dice).
    /// (the second playtest, P2-2: picked up whenever the table is calm - TableCalm - not only on the map's idle moments; set down
    /// on a place it moves the hero only when the map takes moves. And it always stands on the table: KeepOnTable.)
    /// (playtest 2026-10-17: the user pinches it - on a Touch controller that is the trigger and A together - and A selected
    /// the next thing. Now a squeeze, a pinch or the trigger alone at the figurine picks it up, it is held while the grip or the
    /// trigger is, and A / X of a hand at the figurine or holding it never reach the game, nor its trigger the laser. While a
    /// hand is near enough to take it, the figurine has a gold outline.)
    internal class FigurineGrab : MonoBehaviour
    {
        public static FigurineGrab I;
        public static float PalmReach = 0.07f, TipReach = 0.04f, Open = 0.3f, ReleaseGrip = 0.35f, Back = 0.3f;
        public static int Readies, Grabs, Moves, Returns;
        public static string Last = "-";
        static Hand s_holder;
        static Transform s_fig;
        static Vector3 s_homePos, s_localPos;
        static Quaternion s_homeRot, s_localRot;
        static float s_since;
        static UISelectable s_over;
        static readonly bool[] s_hover = new bool[2];
        // gliding back after a release elsewhere
        static Transform s_back;
        static Vector3 s_backFrom;
        static Quaternion s_backFromRot;
        static float s_backT = -1f;
        static readonly RaycastHit[] s_hits = new RaycastHit[16];

        public static bool Holding => s_holder != null;
        static readonly bool[] s_near = new bool[2];
        /// (the second playtest, P2-2: the hand at it a moment ago still counts - a pinch or a squeeze curls the glove's index, and its
        /// tip had left the figurine by the frame the trigger clicked, so it was "near" for the outline but never for the pick-up)
        public static float NearMemory = 0.5f, NearKeep = 0.15f;
        static readonly float[] s_nearAt = { -9f, -9f };
        /// This hand is at the figurine (TableCardGrab leaves the cards round it alone then).
        public static bool Near(Hand h) => s_holder != null || s_near[h.IsLeft ? 1 : 0];
        public static bool Holds(Hand h) => s_holder != null && s_holder == h;
        /// This hand is at the figurine or holds it: its trigger is the figurine's (not the laser's).
        public static bool Claims(Hand h) => Settings.GrabThrow && (Holds(h) || s_near[h.IsLeft ? 1 : 0]);
        static readonly bool[] s_swallow = new bool[2];
        public static int Swallowed;

        /// VRGamepad: the hand's lower face button (A right, X left) pressed while the hand is at the figurine or holds it does
        /// not reach the game - until it is let go, wherever the hand is by then.
        public static bool SwallowFace(Hand h)
        {
            int i = h.IsLeft ? 1 : 0;
            if (!h.A) { s_swallow[i] = false; return false; }
            if (h.ADown && Claims(h) && !s_swallow[i]) { s_swallow[i] = true; Swallowed++; Plugin.L.LogInfo("figurine: " + (h.IsLeft ? "X" : "A") + " at the figurine kept from the game"); }
            return s_swallow[i];
        }

        void Awake()
        {
            I = this;
            Diag.StatusExtras["figurine"] = () => Diag.Q(Describe());
            Diag.Commands["fig"] = Command;
            HandWorld.Targets["figurine"] = () => { Transform f = Figurine(); return f != null ? (Vector3?)(Top(f) + Vector3.up * 0.04f * Scale) : null; };
        }

        static float Scale => VRRig.I != null ? VRRig.I.Scale : 1f;

        internal static Transform Figurine()
        {
            try { Map m = Map.Instance; return m != null && m.PlayerCounter != null && m.PlayerCounter.isActiveAndEnabled ? m.PlayerCounter.transform : null; }
            catch (Exception) { return null; }
        }

        static Bounds Box(Transform f)
        {
            Bounds b = new Bounds(f.position, Vector3.zero);
            bool has = false;
            foreach (Renderer r in f.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
                if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
            }
            return b;
        }

        static Vector3 Top(Transform f) { Bounds b = Box(f); return new Vector3(b.center.x, b.max.y, b.center.z); }

        void Update()
        {
            try { Tick(); }
            catch (Exception e) { Plugin.L.LogError("figurine: " + e); if (s_holder != null) LetGo("error"); }
        }

        /// Where the figurine may be picked up and carried (the second playtest, P2-2: only the dungeon map's idle moments allowed it -
        /// the challenge's opening encounter showed the hero on the table and it "can't be picked up at all"): the card table in view,
        /// nothing paused, no dice, no card in a hand, the hero not walking. Setting it down on a place moves the hero only when the map
        /// takes moves (TableCardGrab.MapIdle); anywhere else it glides back.
        internal static bool TableCalm(Transform fig)
        {
            if (!XRSettings.enabled || !Settings.GrabThrow || VRRig.I == null || !Places.AtCaravan || CombatVR.Active || VRFade.IsBlack || Limbo.On) return false;
            if (DiceHand.Holder != null || CardGrab.Holding || Minigames.DiceCatapult || TableCardGrab.Holder != null) return false;
            try
            {
                MenuManager mm = MenuManager.Instance;
                if (mm != null && mm.Paused) return false;
                if (fig != null && s_moving != null)
                {
                    PlayerCounter pc = fig.GetComponent<PlayerCounter>();
                    if (pc != null && s_moving(pc).HasValue) return false;   // the game walks it
                }
            }
            catch (Exception) { return false; }
            return true;
        }

        static readonly HarmonyLib.AccessTools.FieldRef<PlayerCounter, float?> s_moving = MovingField();
        static HarmonyLib.AccessTools.FieldRef<PlayerCounter, float?> MovingField()
        {
            try { return HarmonyLib.AccessTools.FieldRefAccess<PlayerCounter, float?>("m_moveTime"); }
            catch (Exception) { return null; }
        }

        void Tick()
        {
            if (s_backT >= 0f) Glide();
            Transform fig = Figurine();
            bool calm = TableCalm(fig);
            if (s_holder != null)
            {
                if (fig != s_fig || !calm || !s_holder.Tracked) { LetGo("the table is busy"); return; }
                // held while the grip or the trigger is (a pinch is the trigger and A)
                if (s_holder.Grip < ReleaseGrip && !s_holder.TriggerBtn && Time.unscaledTime - s_since > 0.15f) { LetGo("let go"); return; }
                Follow(false);
                Over();
                return;
            }
            if (fig == null || !calm || s_backT >= 0f) { s_hover[0] = s_hover[1] = false; s_near[0] = s_near[1] = false; Outline.Show(null); return; }
            KeepOnTable(fig);
            Bounds box = Box(fig);
            float now = Time.unscaledTime;
            foreach (Hand h in VRInput.RL)
            {
                int i = h.IsLeft ? 1 : 0;
                bool near = false;
                if (HandWorld.Valid(h))
                {
                    float s = Scale;
                    Vector3 palm = HandWorld.Palm(h), tip = HandFeel.Tip(h);
                    float dPalm = Vector3.Distance(box.ClosestPoint(palm), palm);
                    bool touching = dPalm < PalmReach * s || Vector3.Distance(box.ClosestPoint(tip), tip) < TipReach * s;
                    if (touching) s_nearAt[i] = now;
                    near = touching || now - s_nearAt[i] < NearMemory && dPalm < NearKeep * s;
                }
                s_near[i] = near;
                bool hover = near && h.Grip < Open && !h.TriggerBtn;
                if (hover && !s_hover[i]) { Readies++; Haptics.Cue(Haptics.CueKind.Tick, h.IsLeft); }
                s_hover[i] = hover;
                // a squeeze (not a pointing hand's: grip with the index lifted is a fingertip touch), a pinch or the trigger alone
                bool pointing = FingerSampler.TriggerTouchSeen && !h.TriggerTouch;
                if (near && ((h.GripDown && !pointing) || h.TriggerDown)) { Take(h, fig); Outline.Show(null); return; }
            }
            Outline.Show(s_near[0] || s_near[1] ? fig : null);
        }

        // ---- the figurine stands on the table (the second playtest, P2-2, mark 12: "Why is this guy floating in the middle of
        // nowhere?" - at the challenge's opening it waited a few inches past the table's edge, at table height) ---------------------
        public static float EdgeMargin = 0.05f;
        public static int Kept;
        static Collider s_table;
        static int s_tableScene = -1;
        static float s_nextKeep;
        static readonly RaycastHit[] s_down = new RaycastHit[8];

        static Collider TableTop()
        {
            if (s_table != null) return s_table;
            if (s_tableScene == SceneScan.Version) return null;
            s_tableScene = SceneScan.Version;
            GameObject go = GameObject.Find("Models/Dealer_Caravan/dealer_caravan_cardtable/Table/Collider");
            s_table = go != null ? go.GetComponent<Collider>() : null;
            return s_table;
        }

        /// Is there table top under this point (straight down)?
        static bool Over(Collider table, Vector3 p, float sc)
        {
            RaycastHit hit;
            return table.Raycast(new Ray(p + Vector3.up * (0.25f * sc), Vector3.down), out hit, 0.5f * sc);
        }

        /// A figurine resting at table height with no table under its base is moved in toward the table's middle until its whole base
        /// (and EdgeMargin more) is over the table. Its own place, kept (the game's next move starts from there).
        static void KeepOnTable(Transform fig)
        {
            if (Time.unscaledTime < s_nextKeep) return;
            s_nextKeep = Time.unscaledTime + 0.25f;
            Collider table = TableTop();
            if (table == null) return;
            float sc = Scale;
            Bounds b = Box(fig);
            float top = table.bounds.max.y;
            if (Mathf.Abs(b.min.y - top) > 0.06f * sc) return;   // not standing at the table's height (carried, lifted, elsewhere)
            Vector3 c = new Vector3(b.center.x, b.min.y, b.center.z);
            Vector3 mid = table.bounds.center;
            Vector3 dir = new Vector3(mid.x - c.x, 0f, mid.z - c.z);
            if (dir.sqrMagnitude < 1e-6f) return;
            dir.Normalize();
            float r = Mathf.Max(b.extents.x, b.extents.z);
            if (Over(table, c - dir * r, sc) && Over(table, c, sc)) return;   // its outer edge is on the table
            for (float step = 0.01f; step <= 0.8f; step += 0.01f)
            {
                Vector3 p = c + dir * (step * sc);
                if (!Over(table, p - dir * (r + EdgeMargin * sc), sc)) continue;
                fig.position += dir * (step * sc);
                Kept++;
                Last = "kept on the table: moved " + step.ToString("0.00") + " m in from past the edge";
                Plugin.L.LogInfo("figurine: " + Last + " (" + GameWatch.State.Replace("GameState_", "") + ")");
                return;
            }
        }

        static void Take(Hand h, Transform fig)
        {
            Transform g = VRHands.GloveRoot(h.IsLeft);
            s_holder = h;
            s_fig = fig;
            s_homePos = fig.position;
            s_homeRot = fig.rotation;
            s_since = Time.unscaledTime;
            Vector3 p = g != null ? g.position : HandWorld.Palm(h);
            Quaternion r = g != null ? g.rotation : HandWorld.Rot(h);
            Quaternion inv = Quaternion.Inverse(r);
            s_localPos = inv * (fig.position - p);
            s_localRot = inv * fig.rotation;
            Grabs++;
            s_hover[0] = s_hover[1] = false;
            Last = (h.IsLeft ? "L" : "R") + " picked up the hero";
            Haptics.Cue(Haptics.CueKind.Grab, h.IsLeft);
            Plugin.L.LogInfo("figurine: " + Last);
        }

        static void Follow(bool late)
        {
            if (s_holder == null || s_fig == null) return;
            Transform g = VRHands.GloveRoot(s_holder.IsLeft);
            if (late && g == null) return;
            Vector3 p = g != null ? g.position : HandWorld.Palm(s_holder);
            Quaternion r = g != null ? g.rotation : HandWorld.Rot(s_holder);
            s_fig.SetPositionAndRotation(p + r * s_localPos, r * s_localRot);
        }

        /// VRRig's late hook (after the gloves are placed at the render-time pose) and after the game's own figurine update.
        public static void Late() { if (s_holder != null) Follow(true); }

        void LateUpdate() { if (s_holder != null) Follow(false); }

        /// The place under the carried figurine: lit as a fingertip on it lights it, when the hero can go there.
        static void Over()
        {
            UISelectable sel = TakesMoves() ? PlaceUnder(s_fig) : null;
            if (sel == s_over) return;
            s_over = sel;
            if (sel != null)
            {
                try { UISelection.TryHover(sel); } catch (Exception) { }
                Haptics.Cue(Haptics.CueKind.Touch, s_holder.IsLeft, 0.6f);
            }
        }

        /// The map takes the hero's moves: the dungeon map's idle moments (TableCardGrab.MapIdle), and the same at a challenge's
        /// opening (GameState_ChallengeSetup: its first encounters are played on the map there; the third playtest, mark 9: "When I pick
        /// up the guy and move him on to the next spot, he just moves back to the first spot" - every drop there went back, as only
        /// GameState_Map counted).
        internal static bool TakesMoves()
        {
            if (TableCardGrab.MapIdle()) return true;
            if (GameWatch.State != "GameState_ChallengeSetup" || !XRSettings.enabled || !Settings.GrabThrow || VRRig.I == null || !Places.AtCaravan ||
                CombatVR.Active || VRFade.IsBlack || DiceHand.Holder != null || CardGrab.Holding || Minigames.DiceCatapult) return false;
            try
            {
                MenuManager mm = MenuManager.Instance;
                if (mm == null || mm.Paused || mm.CurrentMenu(MenuManager.StackPriority.Secondary) != null || mm.CurrentMenu(MenuManager.StackPriority.Dialogue) != null ||
                    mm.CurrentMenu(MenuManager.StackPriority.Failure) != null) return false;
            }
            catch (Exception) { return false; }
            return true;
        }

        /// A drop this close beside a place (real metres, from the figurine's base to the place's outline) counts as on it.
        public static float NearDrop = 0.05f;
        static readonly Collider[] s_near3 = new Collider[24];

        /// Straight down from the figurine's base: the first map tile the hero could be sent to (not where it stands); none there,
        /// the nearest such tile whose outline is within NearDrop of the base (a drop on the tile's edge or just beside it).
        static UISelectable PlaceUnder(Transform fig)
        {
            Bounds b = Box(fig);
            Vector3 from = new Vector3(b.center.x, b.max.y, b.center.z);
            int mask = TableCardGrab.PickMask();
            int n = Physics.RaycastNonAlloc(from, Vector3.down, s_hits, 1.5f * Scale, mask, QueryTriggerInteraction.Collide);
            UISelectable best = null;
            float bestD = float.MaxValue;
            bool home = false;
            for (int i = 0; i < n; i++)
            {
                UISelectable sel = Goable(s_hits[i].collider);
                if (sel != null && s_hits[i].distance < bestD) { bestD = s_hits[i].distance; best = sel; }
                if (sel == null && OnHome(s_hits[i].collider)) home = true;
            }
            if (best != null || home) return best;   // (set down on its own place: it stays)
            // beside a tile: the tiles' colliders round the base (as tall as the figurine reaches down to the map)
            Vector3 foot = new Vector3(b.center.x, b.min.y, b.center.z);
            float reach = NearDrop * Scale + Mathf.Max(b.extents.x, b.extents.z);
            int m = Physics.OverlapCapsuleNonAlloc(foot + Vector3.up * (0.05f * Scale), foot - Vector3.up * (0.6f * Scale), reach, s_near3, mask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < m; i++)
            {
                UISelectable sel = Goable(s_near3[i]);
                if (sel == null) continue;
                Bounds cb = s_near3[i].bounds;
                float dx = Mathf.Max(0f, Mathf.Max(cb.min.x - foot.x, foot.x - cb.max.x)), dz = Mathf.Max(0f, Mathf.Max(cb.min.z - foot.z, foot.z - cb.max.z));
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < bestD) { bestD = d; best = sel; }
            }
            return best;
        }

        /// This collider is the place the hero stands on.
        static bool OnHome(Collider c)
        {
            if (c == null || !c.enabled) return false;
            Map m = Map.Instance;
            MapLayoutSlot cur = m != null && m.PlayerCounter != null ? m.PlayerCounter.CurrentSlot : null;
            return cur != null && c.GetComponentInParent<MapLayoutSlot>() == cur;
        }

        /// The map tile of this collider when the hero could be sent there (not where it stands), else null.
        static UISelectable Goable(Collider c)
        {
            if (c == null || !c.enabled) return null;
            bool blocked;
            UISelectable sel = VRPointer.PickTarget(c.gameObject, out blocked);
            if (sel == null || blocked || !sel.IsValid || !sel.HasSelectAction) return null;
            MapLayoutSlot slot = sel.GetComponent<MapLayoutSlot>();
            if (slot == null) return null;
            Map m = Map.Instance;
            if (m != null && m.PlayerCounter != null && slot == m.PlayerCounter.CurrentSlot) return null;
            return sel;
        }

        static void LetGo(string why)
        {
            Hand h = s_holder;
            Transform fig = s_fig;
            UISelectable to = s_holder != null && why == "let go" && TakesMoves() ? PlaceUnder(fig) : null;
            s_holder = null;
            s_fig = null;
            s_over = null;
            if (fig == null) return;
            if (to != null)
            {
                // put back where it stood: the game walks the hero to the new place itself, as after a tap on the tile
                fig.SetPositionAndRotation(s_homePos, s_homeRot);
                bool ok = false;
                try { ok = UISelection.TryHover(to); } catch (Exception) { }
                if (ok) VRPad.Press("A", 4);
                Moves++;
                Last = "set down on " + VRUI.Path(to.transform).Replace("UIManager/UI Root (3D)/", "") + (ok ? ": the hero goes there" : ": refused");
                if (h != null) Haptics.Cue(ok ? Haptics.CueKind.LetGo : Haptics.CueKind.Refuse, h.IsLeft);
            }
            else
            {
                s_back = fig;
                s_backFrom = fig.position;
                s_backFromRot = fig.rotation;
                s_backT = 0f;
                Returns++;
                Last = why + ": back to where it stood";
                if (h != null) Haptics.Cue(Haptics.CueKind.LetGo, h.IsLeft);
            }
            Plugin.L.LogInfo("figurine: " + Last);
        }

        static void Glide()
        {
            if (s_back == null) { s_backT = -1f; return; }
            s_backT += Time.unscaledDeltaTime / Back;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(s_backT));
            s_back.SetPositionAndRotation(Vector3.Lerp(s_backFrom, s_homePos, k), Quaternion.Slerp(s_backFromRot, s_homeRot, k));
            if (s_backT >= 1f) { s_backT = -1f; s_back = null; }
        }

        /// `fig where`: the figurine's top and the nearest place the hero can go to (its top), world coordinates (tests).
        static string Command(string[] a)
        {
            Transform f = Figurine();
            if (f == null) return "no figurine";
            Vector3 t = Top(f);
            Map m = Map.Instance;
            MapLayoutSlot cur = m != null && m.PlayerCounter != null ? m.PlayerCounter.CurrentSlot : null;
            MapLayoutSlot best = null;
            float bestD = float.MaxValue;
            foreach (MapLayoutSlot sl in UnityEngine.Object.FindObjectsOfType<MapLayoutSlot>())
            {
                if (sl == null || sl == cur || sl.Selectable == null) continue;
                bool blocked;
                UISelectable sel = VRPointer.PickTarget(sl.Selectable.gameObject, out blocked);
                if (sel == null || blocked || !sel.HasSelectAction) continue;
                float d = (sl.transform.position - f.position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = sl; }
            }
            string to = "-";
            if (best != null)
            {
                Collider c = best.GetComponentInChildren<Collider>();
                Vector3 p = c != null ? new Vector3(c.bounds.center.x, c.bounds.max.y, c.bounds.center.z) : best.transform.position;
                to = string.Format(CultureInfo.InvariantCulture, "{0} top {1:0.###} {2:0.###} {3:0.###}", best.name, p.x, p.y, p.z);
            }
            return string.Format(CultureInfo.InvariantCulture, "hero top {0:0.###} {1:0.###} {2:0.###} | to {3}", t.x, t.y, t.z, to);
        }

        string Describe()
        {
            string held = s_holder != null ? (s_holder.IsLeft ? "L" : "R") + " holds the hero" + string.Format(CultureInfo.InvariantCulture, " {0:0.00} m from its place", s_fig != null ? Vector3.Distance(s_fig.position, s_homePos) / Scale : 0f) +
                                             (s_over != null ? ", over " + s_over.name : "") : "free";
            return held + " | readies " + Readies + " grabs " + Grabs + " moves " + Moves + " returns " + Returns + " | near L " + s_near[1] + " R " + s_near[0] +
                   " | outline " + (Outline.Shown ? "on" : "off") + " | face buttons kept " + Swallowed + " | kept on the table " + Kept + " | " + Last;
        }
    }

    /// The figurine's outline while a hand can take it (T5, playtest 2026-10-17): an inverted hull - each of its meshes again,
    /// a little bigger about its own middle, drawn from the inside only in a flat gold, so a rim shows round the figure.
    internal static class Outline
    {
        public static float Grow = 1.10f;
        public static Color Gold = new Color(1f, 0.95f, 0.72f, 1f);   // (a pale warm white: the figure itself is gold)
        static Transform s_for;
        static readonly System.Collections.Generic.List<GameObject> s_hulls = new System.Collections.Generic.List<GameObject>();
        static Material s_mat;
        public static bool Shown;

        public static void Show(Transform fig)
        {
            bool on = fig != null;
            if (on && fig != s_for) Build(fig);
            if (Shown == on && (on || s_hulls.Count == 0)) return;
            Shown = on;
            for (int i = s_hulls.Count - 1; i >= 0; i--)
            {
                if (s_hulls[i] == null) { s_hulls.RemoveAt(i); continue; }
                if (s_hulls[i].activeSelf != on) s_hulls[i].SetActive(on);
            }
        }

        static void Build(Transform fig)
        {
            foreach (GameObject g in s_hulls) if (g != null) UnityEngine.Object.Destroy(g);
            s_hulls.Clear();
            s_for = fig;
            Shown = false;
            if (s_mat == null)
            {
                Shader sh = Shader.Find("Hidden/Internal-Colored");
                if (sh == null) { Plugin.L.LogWarning("figurine: no shader for the outline"); return; }
                s_mat = new Material(sh) { name = "HoF2VR_FigurineOutline", color = Gold };
                s_mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Front);
                s_mat.SetInt("_ZWrite", 1);
            }
            foreach (MeshFilter mf in fig.GetComponentsInChildren<MeshFilter>(false))
            {
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (mf.sharedMesh == null || mr == null || !mr.enabled) continue;
                var go = new GameObject("HoF2VR_Outline") { layer = mf.gameObject.layer };
                go.transform.SetParent(mf.transform, false);
                Vector3 c = mf.sharedMesh.bounds.center;
                go.transform.localPosition = c - c * Grow;
                go.transform.localScale = Vector3.one * Grow;
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = s_mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                go.SetActive(false);
                s_hulls.Add(go);
            }
            foreach (SkinnedMeshRenderer sr in fig.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (sr.sharedMesh == null || !sr.enabled) continue;
                var mesh = new Mesh { name = "HoF2VR_OutlineBaked" };
                sr.BakeMesh(mesh);
                var go = new GameObject("HoF2VR_Outline") { layer = sr.gameObject.layer };
                go.transform.SetParent(sr.transform, false);
                Vector3 c = mesh.bounds.center;
                go.transform.localPosition = c - c * Grow;
                go.transform.localScale = Vector3.one * Grow;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = s_mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                go.SetActive(false);
                s_hulls.Add(go);
            }
            Plugin.L.LogInfo("figurine: outline of " + s_hulls.Count + " meshes");
        }
    }
}

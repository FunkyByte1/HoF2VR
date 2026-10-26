using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Dice re-rolls in VR (T8, playtest 2026-10-17: "the dice spread out far enough to tap the ones to re-roll, then a re-roll
    /// button, then grab and throw them"). When the game asks which dice to roll again (DiceRoller state Marking):
    ///  * the dice that came to rest closer than Spread to each other are laid out in a grid of that pitch round where they
    ///    lie (each keeps its face up and its rotation: the game reads the same values), so a fingertip taps one at a time;
    ///  * a marked die (a tap toggles it, the game's own mark) gets a bright red-gold rim, so the choice is plain from above;
    ///  * a "Re-roll" plate stands just beyond the dice once one is marked: the game's own Re-roll (its controls bar F3, as X);
    ///  * then the marked dice wait in the hand as before: grab and throw them (DiceHand).
    internal class DiceReroll : MonoBehaviour
    {
        public static DiceReroll I;
        public static float Spread = 0.085f, Beyond = 0.10f, PlateUp = 0.06f;
        public static int Spreads, Rerolls, MarkShows;
        public static string Last = "-";
        static readonly AccessTools.FieldRef<DiceRoller, DiceRoller.State> s_state = AccessTools.FieldRefAccess<DiceRoller, DiceRoller.State>("m_state");
        DiceRoller m_spreadFor;
        TouchBoard m_plate;
        readonly Dictionary<Die, List<GameObject>> m_rims = new Dictionary<Die, List<GameObject>>();
        Material m_rimMat;

        void Awake()
        {
            I = this;
            Diag.StatusExtras["reroll"] = () => Diag.Q("spreads " + Spreads + " rerolls " + Rerolls + " marks shown " + MarkShows + " plate " + (m_plate != null && m_plate.Visible ? "shown" : "-") + " | " + Last);
            Diag.Commands["reroll"] = a =>
            {
                if (a.Length > 1 && a[1] == "press" && m_plate != null && m_plate.Visible) { StartCoroutine(m_plate.Drive(this, VRInput.R, 0, "tap")); return "tapping Re-roll"; }
                if (a.Length > 1 && a[1] == "where" && m_plate != null && m_plate.Visible) return m_plate.Pose(0, 6f, VRInput.R);
                // `reroll dice`: each rolled die's top (the middle of its upper face, world) and whether it is marked (tests: real taps)
                if (a.Length > 1 && a[1] == "dice")
                {
                    DiceRoller r = Active();
                    if (r == null) return "no dice";
                    var sb = new System.Text.StringBuilder();
                    foreach (Die d in r.ActiveDice)
                    {
                        if (d == null) continue;
                        Bounds b = new Bounds(d.transform.position, Vector3.zero);
                        foreach (Collider c in d.GetComponentsInChildren<Collider>()) if (c.enabled && !c.isTrigger) b.Encapsulate(c.bounds);
                        sb.Append(d.name).Append(' ').Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.####} {1:0.####} {2:0.####}", b.center.x, b.max.y, b.center.z))
                          .Append(d.Marked ? " marked" : " -").Append(" | ");
                    }
                    return sb.ToString();
                }
                return Last;
            };
        }

        static DiceRoller Active()
        {
            DiceRollTask task = DiceRollTask.RunningTask;
            return task != null ? task.Roller : null;
        }

        void LateUpdate()
        {
            try { Tick(); }
            catch (Exception e) { Plugin.L.LogWarning("dice reroll: " + e.Message); }
        }

        void Tick()
        {
            DiceRoller r = XRSettings.enabled && Places.AtCaravan && VRRig.I != null ? Active() : null;
            bool marking = r != null && s_state(r) == DiceRoller.State.Marking && !VRFade.IsBlack;
            if (!marking)
            {
                m_spreadFor = null;
                if (m_plate != null && m_plate.Visible) m_plate.Hide();
                HideRims();
                return;
            }
            if (m_spreadFor != r) { m_spreadFor = r; SpreadOut(r); }
            int marked = 0;
            Vector3 sum = Vector3.zero, nearest = Vector3.zero;
            Vector3 eye = VRRig.I.NeutralEye;
            float farD = -1f;
            foreach (Die d in r.ActiveDice)
            {
                if (d == null) continue;
                sum += d.transform.position;
                bool on = d.Marked;
                Rim(d, on);
                if (on) marked++;
                float dd = Vector3.Distance(d.transform.position, eye);
                if (farD < 0f || dd < farD) { farD = dd; nearest = d.transform.position; }
            }
            // the plate: once one die is marked, just beyond the dice (away from the player), upright, facing the eyes
            if (marked > 0)
            {
                if (m_plate == null) { m_plate = new TouchBoard("reroll") { LabelScale = 1.6f, Width = 0.15f }; m_plate.Pressed = (i, h) => Press(h); }
                if (!m_plate.Visible)
                {
                    float sc = VRRig.I.Scale;
                    Vector3 centre = sum / Mathf.Max(1, r.ActiveDice.Count);
                    Vector3 away = centre - eye;
                    away.y = 0f;
                    away = away.sqrMagnitude > 1e-6f ? away.normalized : VRRig.I.NeutralRot * Vector3.forward;
                    // on the player's side of the dice (beyond them lies the map: the first try stood over its cards)
                    float near = Vector3.Dot(centre - nearest, away);
                    Vector3 at = centre - away * (Mathf.Max(0f, near) + Beyond * sc) + Vector3.up * (PlateUp * sc);
                    if (m_plate.Show(false, "", "Re-roll")) { m_plate.PlaceAt(at, Quaternion.LookRotation(at - eye, Vector3.up)); Last = "Re-roll shown " + Vector3.Distance(at, eye).ToString("0.00") + " u from the eyes"; }
                }
                m_plate.Tick();
            }
            else if (m_plate != null && m_plate.Visible) m_plate.Hide();
        }

        void Press(Hand h)
        {
            Rerolls++;
            Last = (h.IsLeft ? "L" : "R") + " pressed Re-roll";
            Plugin.L.LogInfo("dice: " + Last + " (the game's Re-roll, as X)");
            VRPad.Press("X", 4);
            if (m_plate != null) m_plate.Hide();
        }

        /// The dice lying closer than Spread to a neighbour: a grid of that pitch (rows across the player's view) round
        /// their middle, at their own heights and rotations.
        void SpreadOut(DiceRoller r)
        {
            List<Die> dice = new List<Die>();
            foreach (Die d in r.ActiveDice) if (d != null) dice.Add(d);
            if (dice.Count < 2) return;
            float sc = VRRig.I.Scale, pitch = Spread * sc, closest = float.MaxValue;
            for (int i = 0; i < dice.Count; i++)
                for (int j = i + 1; j < dice.Count; j++)
                {
                    Vector3 a = dice[i].transform.position, b = dice[j].transform.position;
                    a.y = b.y = 0f;
                    closest = Mathf.Min(closest, Vector3.Distance(a, b));
                }
            if (closest >= pitch * 0.95f) { Last = "the dice lie apart already (" + (closest / sc).ToString("0.000") + " m)"; return; }
            Vector3 c = Vector3.zero;
            foreach (Die d in dice) c += d.transform.position;
            c /= dice.Count;
            Quaternion yaw = VRRig.I.NeutralRot;
            Vector3 right = yaw * Vector3.right, fwd = yaw * Vector3.forward;
            int cols = Mathf.Min(dice.Count, 4), rows = (dice.Count + cols - 1) / cols;
            // the dice in the order they lie from left to right, row by row from the near side
            dice.Sort((x, y) => Vector3.Dot(x.transform.position - c, fwd).CompareTo(Vector3.Dot(y.transform.position - c, fwd)));
            for (int row = 0; row < rows; row++)
            {
                int from = row * cols, n = Mathf.Min(cols, dice.Count - from);
                List<Die> line = dice.GetRange(from, n);
                line.Sort((x, y) => Vector3.Dot(x.transform.position - c, right).CompareTo(Vector3.Dot(y.transform.position - c, right)));
                for (int k = 0; k < n; k++)
                {
                    Die d = line[k];
                    Vector3 p = c + right * ((k - (n - 1) * 0.5f) * pitch) + fwd * ((row - (rows - 1) * 0.5f) * pitch);
                    p.y = d.transform.position.y;
                    Rigidbody rb = d.GetComponent<Rigidbody>();
                    if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.position = p; }
                    d.transform.position = p;
                }
            }
            Spreads++;
            Last = dice.Count + " dice laid out " + Spread.ToString("0.000") + " m apart (closest was " + (closest / sc).ToString("0.000") + " m)";
            Plugin.L.LogInfo("dice: " + Last + " for the re-roll choice");
        }

        void Rim(Die d, bool on)
        {
            List<GameObject> hulls;
            if (!m_rims.TryGetValue(d, out hulls))
            {
                if (!on) return;
                hulls = Build(d);
                m_rims[d] = hulls;
            }
            bool was = hulls.Count > 0 && hulls[0] != null && hulls[0].activeSelf;
            foreach (GameObject g in hulls) if (g != null && g.activeSelf != on) g.SetActive(on);
            if (on && !was) MarkShows++;
        }

        List<GameObject> Build(Die d)
        {
            var list = new List<GameObject>();
            if (m_rimMat == null)
            {
                Shader sh = Shader.Find("Hidden/Internal-Colored");
                if (sh == null) return list;
                m_rimMat = new Material(sh) { name = "HoF2VR_DieMark", color = new Color(1f, 0.42f, 0.16f, 1f) };
                m_rimMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Front);
                m_rimMat.SetInt("_ZWrite", 1);
            }
            MeshFilter body = null;
            foreach (MeshFilter mf in d.GetComponentsInChildren<MeshFilter>(false))
            {
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (mf.sharedMesh == null || mr == null || !mr.enabled) continue;
                // the die's own model (D6Model), not the game's mark or hover sprites beside it; else the largest
                bool model = mf.name.IndexOf("Model", StringComparison.Ordinal) >= 0, bodyModel = body != null && body.name.IndexOf("Model", StringComparison.Ordinal) >= 0;
                if (body == null || (model && !bodyModel) || (model == bodyModel && mr.bounds.size.sqrMagnitude > body.GetComponent<MeshRenderer>().bounds.size.sqrMagnitude)) body = mf;
            }
            foreach (MeshFilter mf in body != null ? new[] { body } : new MeshFilter[0])
            {
                var go = new GameObject("HoF2VR_DieMark") { layer = mf.gameObject.layer };
                go.transform.SetParent(mf.transform, false);
                Vector3 c = mf.sharedMesh.bounds.center;
                const float grow = 1.16f;
                go.transform.localPosition = c - c * grow;
                go.transform.localScale = Vector3.one * grow;
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = m_rimMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                go.SetActive(false);   // (Rim shows it: counted then)
                list.Add(go);
                break;   // the die's body only
            }
            return list;
        }

        void HideRims()
        {
            List<Die> gone = null;
            foreach (var kv in m_rims)
            {
                if (kv.Key == null) { (gone ?? (gone = new List<Die>())).Add(kv.Key); continue; }
                foreach (GameObject g in kv.Value) if (g != null && g.activeSelf) g.SetActive(false);
            }
            if (gone != null) foreach (Die d in gone) m_rims.Remove(d);
        }
    }
}

using System;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// A slider on a board of the mod's own (M1, playtest 2026-10-17: "it's impossible to do this without sliders", of the
    /// controller settings): a track with a gold fill and a knob, lying in the board's plane between the row's - and + plates
    /// (kept for fine steps). Put a fingertip on the track and drag along it: the value follows the finger at once, in the
    /// setting's own steps, with a light tick each step; lift the finger off the board to let go. Both hands, the same
    /// fingertip as every press (TouchInput.TipOf), never while the trigger is held or the grip squeezed (unless pointing).
    /// The parent's frame is the board's: x along the row, y up, the viewer on the -z side (pressing in = +z).
    internal sealed class TouchSlider
    {
        public float Min, Max, Step = 0f, Value;
        public Action<float> Changed;
        public Action Released;
        /// Real metres along the board (the board's units are world units: multiplied by the rig's scale at Layout).
        public float Length = 0.18f, Height = 0.030f;
        public static float TouchFront = 0.012f, TouchBehind = 0.035f, KeepFront = 0.045f, KeepBehind = 0.07f, EndSlack = 0.015f;
        public static int Drags, Moves;
        public string Name = "";
        public bool Dragging => m_hand >= 0;
        public bool DraggedBy(bool left) => m_hand == (left ? 0 : 1);

        Transform m_root, m_track, m_fill, m_knob;
        Mesh m_trackMesh, m_fillMesh, m_knobMesh;
        int m_hand = -1;
        float m_sc = 1f, m_lastShown = float.NaN;

        public TouchSlider(Transform parent, string name, int layer, Material mat)
        {
            Name = name;
            var go = new GameObject("slider " + name) { layer = layer };
            go.transform.SetParent(parent, false);
            m_root = go.transform;
            // (P4-4, playtest 4: the gold fill came and went with the head's angle - track, fill and knob shared one material, so
            // the three transparent quads were drawn in the order of their centres' distance from the eye: where the fill's
            // centre was the farther, the track was drawn over it. Now each layer has its own queue: track, fill, knob.)
            Material[] mats = Layers(mat);
            m_track = Quad("track", m_root, mats[0], new Color(0.05f, 0.04f, 0.03f, 0.95f), out m_trackMesh, layer);
            m_fill = Quad("fill", m_root, mats[1], new Color(0.55f, 0.40f, 0.16f, 0.95f), out m_fillMesh, layer);
            m_knob = Quad("knob", m_root, mats[2], new Color(1f, 0.82f, 0.42f, 1f), out m_knobMesh, layer);
        }

        static readonly System.Collections.Generic.Dictionary<Material, Material[]> s_layers = new System.Collections.Generic.Dictionary<Material, Material[]>();

        /// The board's material and two copies drawn right after it (the fill over the track, the knob over the fill).
        static Material[] Layers(Material m)
        {
            Material[] l;
            if (m == null) return new Material[3];
            if (s_layers.TryGetValue(m, out l) && l[1] != null && l[2] != null) return l;
            l = new[] { m, new Material(m) { name = m.name + "_SliderFill", renderQueue = m.renderQueue + 1 },
                           new Material(m) { name = m.name + "_SliderKnob", renderQueue = m.renderQueue + 2 } };
            s_layers[m] = l;
            return l;
        }

        /// Test seam (`sliderqueues old|new`): the fill and the knob drawn with the track's own material again (the look before
        /// P4-4: their order then follows the eyes' distance), or with their own queues.
        public void UseLayers(bool on)
        {
            if (m_track == null) return;
            Material[] l = Layers(m_track.GetComponent<MeshRenderer>().sharedMaterial);   // (the track keeps the board's own material)
            m_fill.GetComponent<MeshRenderer>().sharedMaterial = on ? l[1] : l[0];
            m_knob.GetComponent<MeshRenderer>().sharedMaterial = on ? l[2] : l[0];
        }

        /// The three layers' render queues (tests).
        public string Queues => (m_track != null ? m_track.GetComponent<MeshRenderer>().sharedMaterial.renderQueue : 0) + "/" +
                                (m_fill != null ? m_fill.GetComponent<MeshRenderer>().sharedMaterial.renderQueue : 0) + "/" +
                                (m_knob != null ? m_knob.GetComponent<MeshRenderer>().sharedMaterial.renderQueue : 0);

        public void SetActive(bool on) { if (m_root != null && m_root.gameObject.activeSelf != on) m_root.gameObject.SetActive(on); if (!on) m_hand = -1; }

        static Transform Quad(string name, Transform parent, Material m, Color c, out Mesh mesh, int layer)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            mesh = new Mesh { name = "HoF2VR_Slider_" + name };
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            mesh.triangles = new[] { 0, 1, 2, 1, 0, 3 };
            Color lin = c.linear;
            lin.a = c.a;
            mesh.colors = new[] { lin, lin, lin, lin };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        /// Its centre in the parent's frame (world units) and the rig's scale.
        public void Layout(Vector3 local, float sc)
        {
            m_sc = sc;
            m_root.localPosition = local;
            m_root.localRotation = Quaternion.identity;
            m_root.localScale = Vector3.one;
            float L = Length * sc, h = Height * sc;
            m_track.localScale = new Vector3(L, h * 0.32f, 1f);
            m_track.localPosition = new Vector3(0f, 0f, -0.0004f * sc);
            Show();
        }

        float T => Max > Min ? Mathf.Clamp01((Value - Min) / (Max - Min)) : 0f;

        void Show()
        {
            float L = Length * m_sc, h = Height * m_sc, t = T;
            m_fill.localScale = new Vector3(Mathf.Max(L * t, 1e-5f), h * 0.32f, 1f);
            m_fill.localPosition = new Vector3(-L * 0.5f + L * t * 0.5f, 0f, -0.0008f * m_sc);
            m_knob.localScale = new Vector3(h * 0.55f, h * 0.95f, 1f);
            m_knob.localPosition = new Vector3(-L * 0.5f + L * t, 0f, -0.0012f * m_sc);
            m_lastShown = Value;
        }

        /// The value set from elsewhere (the - / + plates, a reset).
        public void Set(float v) { Value = Mathf.Clamp(v, Min, Max); if (v != m_lastShown) Show(); }

        /// Once a frame: both fingertips. True while one drags it.
        public bool Tick()
        {
            if (m_root == null || !m_root.gameObject.activeInHierarchy || VRRig.I == null || !XRSettings.enabled) { m_hand = -1; return false; }
            float L = Length * m_sc, h = Height * m_sc;
            for (int side = 0; side < 2; side++)
            {
                Hand hand = side == 0 ? VRInput.L : VRInput.R;
                bool ok = hand.Tracked && !hand.TriggerBtn && (!hand.GripBtn || VRPad.PointingGrip(hand)) && !VRFade.IsBlack;
                if (m_hand >= 0 && m_hand != side) continue;
                if (!ok) { if (m_hand == side) Release(hand); continue; }
                Vector3 local = m_root.InverseTransformPoint(TouchInput.TipOf(hand));
                if (m_hand < 0)
                {
                    bool on = Mathf.Abs(local.x) <= L * 0.5f + EndSlack * m_sc && Mathf.Abs(local.y) <= h * 0.5f + 0.008f * m_sc &&
                              local.z >= -TouchFront * m_sc && local.z <= TouchBehind * m_sc;
                    if (!on) continue;
                    m_hand = side;
                    Drags++;
                    Haptics.Cue(Haptics.CueKind.Touch, hand.IsLeft);
                    HandSounds.Play(HandSounds.Kind.Tap, TouchInput.TipOf(hand), 0.7f);
                }
                bool keep = Mathf.Abs(local.y) <= h + 0.02f * m_sc && local.z >= -KeepFront * m_sc && local.z <= KeepBehind * m_sc &&
                            Mathf.Abs(local.x) <= L * 0.5f + 0.08f * m_sc;
                if (!keep) { Release(hand); continue; }
                float t = Mathf.Clamp01((local.x + L * 0.5f) / Mathf.Max(L, 1e-5f));
                float v = Min + t * (Max - Min);
                if (Step > 0f) v = Mathf.Round(v / Step) * Step;
                v = Mathf.Clamp(v, Min, Max);
                if (Mathf.Abs(v - Value) > 1e-5f)
                {
                    Value = v;
                    Moves++;
                    Show();
                    Haptics.Cue(Haptics.CueKind.Tick, hand.IsLeft);
                    if (Changed != null) Changed(v);
                }
            }
            return m_hand >= 0;
        }

        void Release(Hand h)
        {
            m_hand = -1;
            Haptics.Cue(Haptics.CueKind.Release, h.IsLeft);
            if (Released != null) Released();
        }

        /// Test seam: the world point on the track for value v (a fingertip there drags it to v).
        public Vector3 PointFor(float v, float depth)
        {
            float L = Length * m_sc;
            float t = Max > Min ? Mathf.Clamp01((v - Min) / (Max - Min)) : 0f;
            return m_root.TransformPoint(new Vector3(-L * 0.5f + L * t, 0f, depth * m_sc));
        }
    }
}

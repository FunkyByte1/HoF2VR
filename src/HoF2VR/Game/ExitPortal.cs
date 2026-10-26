using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace HoF2VR
{
    /// (from the work/limbo2 prototype, docs/loading-v2-design.md §3) The fight's exit as the game's own purple
    /// swirl - the wormhole the Dealer pulls the player into (MeshWormhole, UI scene, at y -1000) - standing
    /// upright at chest height in the arena, facing the player. Replaces FightExit's card (Build/Animate/
    /// Inside/PointedAt); the hold, guards and release of FightExit stay as they are.
    ///
    /// Where the look comes from: the UI scene is loaded from the boot to the end, fights included, so the
    /// tunnel's renderers exist in every fight (UI3D.Instance.Wormhole). Their meshes (Cylinder001: the
    /// tube, r 9.13, 6.1 long, vertex colours) and materials (HoF/Wormhole/Additive + the 'Wormhole'
    /// caustic texture and 'wormhole_gradient_waves'; tint = the current style, DefaultDark: inner cone
    /// deep blue, outer amber/grey, plus the tunnel's red/blue/cyan light colours = the purple swirl) are
    /// shared, never modified: the portal draws them through its own material copies, a few tens of cm
    /// deep, looking down the tube's axis - a vortex receding into the dark. Nothing in it moves toward
    /// the player and nothing is head-locked; the swirl only turns about its own axis (comfort rule: no
    /// streaming past the face). If the wormhole can't be found the portal falls back to procedural rings.
    internal sealed class ExitPortal
    {
        public const float Radius = 0.5f;      // a 1 m disc: easy to see, walk into, touch or point at (Swirl, Rift, Halo)
        /// The Orb (the user, 2026-10-13: "Orb is great. Make it bigger."): 2 m across, its bottom just above the ground -
        /// a doorway to walk into. Its touch and walk-in zone, light, ground glow, hum and the free space it needs grow
        /// with it; it is placed further away by the extra radius, so its near edge is no closer than the 1 m one's.
        public static float OrbRadius = 1.0f, OrbLift = 0.05f;
        public static float RadiusOf(Settings.PortalStyle look) => look == Settings.PortalStyle.Orb ? OrbRadius : Radius;
        /// This portal's radius (RadiusOf its look) and its size against the 1 m look.
        public float R = Radius;
        float K => R / Radius;
        public const float Depth = 0.9f;       // how far the vortex goes back behind its face
        public Transform Root;
        public Vector3 Ground;                 // the visual ground under it (walk-into test)
        readonly List<Material> m_tube = new List<Material>();
        readonly List<Transform> m_spin = new List<Transform>();
        readonly List<Color> m_tint = new List<Color>();
        Material m_throat, m_rim;
        Light m_light;
        float m_born;
        /// outer (Wormhole caustics), middle (gradient waves), inner cone (caustics): violet, magenta, blue
        public static readonly Color[] PortalTint = { new Color(0.56f, 0.27f, 0.93f), new Color(0.93f, 0.35f, 0.99f), new Color(0.31f, 0.37f, 1.18f) };
        public static bool UseGameStyle;   // true = the tunnel's current colours instead
        static readonly Color[] LightCols = { new Color(0.29f, 0.42f, 0.90f), new Color(0.81f, 0.18f, 0.18f), new Color(0.48f, 0.67f, 0.76f) };

        static readonly AccessTools.FieldRef<MeshWormhole, MeshRenderer> s_inner = Field("m_innerConeMesh");
        static readonly AccessTools.FieldRef<MeshWormhole, MeshRenderer> s_outer1 = Field("m_outerMesh1");
        static readonly AccessTools.FieldRef<MeshWormhole, MeshRenderer> s_outer2 = Field("m_outerMesh2");

        static AccessTools.FieldRef<MeshWormhole, MeshRenderer> Field(string n)
        {
            try { return AccessTools.FieldRefAccess<MeshWormhole, MeshRenderer>(n); }
            catch (Exception e) { Plugin.L.LogWarning("exit portal: field " + n + ": " + e.Message); return null; }
        }

        // ---------------------------------------------------------------------------------------- placement

        /// Where the portal goes (combat Deliverable 1b; docs/combat-uyf-design.md §7.7; the user, 2026-10-11: "my
        /// character's forwards momentum almost brought me into it immediately. Have the portal spawn somewhere further
        /// away from me. It doesn't necessarily have to be in the line of sight either. Somewhere that looks nice."):
        ///  * 4-7 m from the player (5 m preferred), on walkable ground of the arena (the navmesh) reachable in a straight
        ///    line, at chest height, never in anything solid;
        ///  * out of the walking path: while the player moves (> 0.3 m/s), never within 40 deg of the motion or within
        ///    1.5 m of where the next 2.5 s of it lead;
        ///  * scenic: open sky above, a deep view behind it (6 m without a solid), 1.2 m from walls and edges, level ground
        ///    (< 12 deg), 1 m from bodies and props; being in view is only a small bonus.
        /// Small arenas: the distance relaxes to 3 m, then 2 m; the walking-path rule stays. Last resort: 2 m ahead.
        public static bool FindSpot(Vector3 hero, Vector3 head, float headYaw, float eyeHeight, out Vector3 centre, out Vector3 ground, out string how) =>
            FindSpot(hero, head, headYaw, eyeHeight, Vector3.zero, out centre, out ground, out how);

        public static bool FindSpot(Vector3 hero, Vector3 head, float headYaw, float eyeHeight, Vector3 motion, out Vector3 centre, out Vector3 ground, out string how)
        {
            float chest = Mathf.Clamp(eyeHeight - 0.42f, 1.05f, 1.45f);   // ~1.3 m for 1.7 m eyes
            Settings.PortalStyle look = Settings.PortalLook;
            float r = RadiusOf(look), grow = r - Radius;   // the bigger Orb: every distance + its extra radius (the near edge stays)
            bool orb = look == Settings.PortalStyle.Orb;
            int solid = SolidMask();
            Vector3 flatMotion = new Vector3(motion.x, 0f, motion.z);
            bool moving = flatMotion.magnitude > 0.3f;
            Vector3 pathEnd = hero + flatMotion * 2.5f;
            float best = float.MaxValue;
            centre = ground = Vector3.zero;
            how = null;
            int rNav = 0, rDist = 0, rLevel = 0, rLine = 0, rBox = 0, rPath = 0, tried = 0;
            float[][] rings = { new[] { 5f, 4.5f, 5.5f, 4f, 6f, 7f }, new[] { 3.5f, 3f }, new[] { 2.5f, 2f } };
            if (grow > 0f) foreach (float[] rr in rings) for (int j = 0; j < rr.Length; j++) rr[j] += grow;
            for (int ring = 0; ring < rings.Length && how == null; ring++)
                for (int k = 0; k < 24; k++)
                    foreach (float d in rings[ring])
                    {
                        float bearing = k * 15f;
                        Vector3 dir = Quaternion.Euler(0f, headYaw + bearing, 0f) * Vector3.forward;
                        Vector3 p = hero + dir * d;
                        tried++;
                        NavMeshHit nh, block;
                        if (!NavMesh.SamplePosition(p, out nh, 0.6f, NavMesh.AllAreas)) { rNav++; continue; }
                        Vector3 g = nh.position;
                        Vector3 flat = g - hero; flat.y = 0f;
                        float dist = flat.magnitude;
                        float lo = (ring == 0 ? 4f : ring == 1 ? 3f : 1.8f) + grow, hi = (ring == 0 ? 7.2f : 4f) + grow;
                        if (dist < lo || dist > hi) { rDist++; continue; }
                        if (Mathf.Abs(g.y - hero.y) > 0.6f) { rLevel++; continue; }                               // same level as the player
                        if (NavMesh.Raycast(hero, g, out block, NavMesh.AllAreas)) { rLine++; continue; }       // walkable straight line
                        if (moving)
                        {
                            if (Vector3.Angle(flat, flatMotion) < 40f) { rPath++; continue; }
                            if (DistToSegment(Flat(g), Flat(hero), Flat(pathEnd)) < 1.5f + grow) { rPath++; continue; }
                        }
                        g.y = VisualGround(g, solid);
                        Vector3 c = orb ? new Vector3(g.x, g.y + r + OrbLift, g.z) : new Vector3(g.x, Mathf.Max(hero.y + chest, g.y + Radius + 0.25f), g.z);
                        Quaternion face = Quaternion.LookRotation(new Vector3(c.x - head.x, 0f, c.z - head.z).normalized);
                        if (!Clear(c, face, solid, look)) { rBox++; continue; }
                        string why;
                        float cost = Mathf.Abs(dist - 5f) * 0.5f + ring * 3f + Scenery(c, g, face, solid, out why);
                        float off = Mathf.Abs(Mathf.DeltaAngle(0f, bearing));
                        // in view is preferred (coordinator 2026-10-12: "keep preferring a spot in view"; it was a small bonus)
                        if (off <= 50f) cost -= 1.5f;
                        else if (off <= 80f) cost -= 0.6f;
                        if (cost >= best) continue;
                        best = cost;
                        centre = c;
                        ground = g;
                        how = "navmesh, " + Mathf.DeltaAngle(0f, bearing).ToString("0") + " deg from the view, " + dist.ToString("0.0") + " m, score " + cost.ToString("0.00") +
                              (why.Length > 0 ? " (" + why.TrimEnd(',', ' ') + ")" : "") + (moving ? ", player moving " + flatMotion.magnitude.ToString("0.0") + " m/s" : "") +
                              (InSight(head, c) ? ", in sight" : ", out of sight");
                    }
            if (how != null)
            {
                how += " | " + tried + " tried; rejected: navmesh " + rNav + ", distance " + rDist + ", level " + rLevel + ", no straight line " + rLine + ", volume " + rBox + ", walking path " + rPath;
                return true;
            }
            // last resort (no navmesh near the player): 2 m ahead of the head at chest height; the
            // portal still works by touching
            Vector3 f = Quaternion.Euler(0f, headYaw, 0f) * Vector3.forward;
            ground = hero + f * (2f + grow);
            centre = ground + Vector3.up * (orb ? r + OrbLift : chest);
            how = "no navmesh: 2 m ahead";
            return false;
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector3.Distance(p, a + ab * t);
        }

        /// The scenic cost of a spot (lower is better) and what it lost points for.
        static float Scenery(Vector3 c, Vector3 g, Quaternion face, int solid, out string why)
        {
            float cost = 0f;
            why = "";
            RaycastHit hit;
            if (Physics.Raycast(c, Vector3.up, out hit, 6f, solid, QueryTriggerInteraction.Ignore) && Solid(hit.collider)) { cost += 1.5f; why += "roof, "; }
            Vector3 away = face * Vector3.forward;
            float depth = 6f;
            foreach (RaycastHit h in Physics.RaycastAll(c, away, 6f, solid, QueryTriggerInteraction.Ignore))
                if (Solid(h.collider)) depth = Mathf.Min(depth, h.distance);
            if (depth < 6f) { cost += (6f - depth) / 6f * 1.5f; why += "backdrop " + depth.ToString("0.0") + " m, "; }
            NavMeshHit edge;
            if (NavMesh.FindClosestEdge(g, out edge, NavMesh.AllAreas) && edge.distance < 1.2f) { cost += (1.2f - edge.distance) * 1.5f; why += "edge " + edge.distance.ToString("0.0") + " m, "; }
            if (Physics.Raycast(g + Vector3.up * 0.5f, Vector3.down, out hit, 1.5f, solid, QueryTriggerInteraction.Ignore) && Vector3.Angle(hit.normal, Vector3.up) > 12f)
            { cost += 1f; why += "slope " + Vector3.Angle(hit.normal, Vector3.up).ToString("0") + " deg, "; }
            int near = 0;
            foreach (Collider col in Physics.OverlapSphere(g + Vector3.up * 0.8f, 1f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (col == null) continue;
                bool body = col.GetComponentInParent<NavMeshAgent>() != null || col.GetComponentInParent<CharacterController>() != null;
                bool prop = !body && Solid(col) && col.bounds.size.y < 3f && col.bounds.max.y > g.y + 0.2f;
                if (body || prop) near++;
            }
            if (near > 0) { cost += 0.8f + 0.2f * near; why += near + " bodies/props near, "; }
            return cost;
        }

        /// Everything the portal must not overlap or be hidden by: world geometry, not actors (layer 8),
        /// the player (13), triggers, UI layers or the mod's own layers.
        static int SolidMask()
        {
            int m = ~0;
            foreach (int l in new[] { 8, 13, 14, 15, 16, 17, 21, 22, 23, 26, 30, 31, Limbo.Layer }) m &= ~(1 << l);
            return m;
        }

        static float VisualGround(Vector3 navPoint, int solid)
        {
            RaycastHit hit;
            if (Physics.Raycast(navPoint + Vector3.up * 1.2f, Vector3.down, out hit, 2.4f, solid, QueryTriggerInteraction.Ignore)
                && Mathf.Abs(hit.point.y - navPoint.y) < 0.8f)
                return hit.point.y;
            return navPoint.y;
        }

        /// Verification: the gap between the bottom of the disc + vortex volume and the highest solid ground under
        /// its footprint (9 downward rays). Negative = in the ground.
        public static float Clearance(Vector3 c, Quaternion face)
        {
            int solid = SolidMask();
            Settings.PortalStyle look = Settings.PortalLook;
            float r = RadiusOf(look);
            bool orb = look == Settings.PortalStyle.Orb;
            float top = float.MinValue;
            for (int i = -1; i <= 1; i++)
                for (int k = 0; k <= 2; k++)
                {
                    // the Orb: its round footprint (the sphere's bottom is lowest at its middle; 0.7 r out it is 0.29 r higher)
                    Vector3 p = orb ? c + face * new Vector3(i * 0.7f * r, 0f, (k - 1) * 0.7f * r) : c + face * new Vector3(i * Radius, 0f, k * Depth * 0.5f);
                    foreach (RaycastHit hit in Physics.RaycastAll(p + Vector3.up * 0.5f, Vector3.down, 6f, solid, QueryTriggerInteraction.Ignore))
                    {
                        if (!Solid(hit.collider)) continue;
                        float y = hit.point.y + (orb && (i != 0 || k != 1) ? -(r - Mathf.Sqrt(r * r - 0.49f * r * r * (Mathf.Abs(i) + Mathf.Abs(k - 1) > 1 ? 2f : 1f))) : 0f);
                        if (y > top) top = y;
                    }
                }
            return top == float.MinValue ? 99f : (c.y - r) - top;
        }

        /// World geometry that a portal must not overlap or be hidden by: not a fighter (alive or dead: they have a
        /// NavMeshAgent or CharacterController), not a loose physics object (a dropped weapon, loot, a ragdoll part) -
        /// those come and go. Destructible props (barrels, crates) do count.
        static bool Solid(Collider col)
        {
            if (col == null) return false;
            Rigidbody rb = col.attachedRigidbody;
            if (rb != null && !rb.isKinematic) return false;
            return col.GetComponentInParent<NavMeshAgent>() == null && col.GetComponentInParent<CharacterController>() == null;
        }

        /// The first solid collider in the portal's volume (null = clear).
        static Collider VolumeBlocker(Vector3 c, Quaternion face, int solid) => VolumeBlocker(c, face, solid, Settings.PortalLook);

        static Collider VolumeBlocker(Vector3 c, Quaternion face, int solid, Settings.PortalStyle look)
        {
            if (look == Settings.PortalStyle.Orb)
            {
                // the Orb stands just above the ground: its sphere with 5 cm to spare, lifted 12 cm (the ground under it,
                // up to ~10 deg of slope, is not "in" it)
                foreach (Collider h in Physics.OverlapSphere(c + Vector3.up * 0.12f, OrbRadius + 0.05f, solid, QueryTriggerInteraction.Ignore))
                    if (Solid(h)) return h;
                return null;
            }
            Vector3 half = new Vector3(Radius + 0.15f, Radius + 0.15f, Depth * 0.5f + 0.1f);
            foreach (Collider h in Physics.OverlapBox(c + face * Vector3.forward * (Depth * 0.5f), half, face, solid, QueryTriggerInteraction.Ignore))
                if (Solid(h)) return h;
            return null;
        }

        static string Blocker(Vector3 c, Quaternion face, int solid)
        {
            Collider h = VolumeBlocker(c, face, solid);
            return h != null ? VRUI.Path(h.transform) + " layer " + h.gameObject.layer + " " + LayerMask.LayerToName(h.gameObject.layer) : "?";
        }

        public static bool InSight(Vector3 head, Vector3 c) => SightBlocker(head, c, SolidMask()) == null;

        /// The first solid collider between the eyes and the portal (null = in sight).
        static Collider SightBlocker(Vector3 head, Vector3 c, int solid)
        {
            Vector3 d = c - head;
            foreach (RaycastHit h in Physics.RaycastAll(head, d.normalized, d.magnitude, solid, QueryTriggerInteraction.Ignore))
                if (Solid(h.collider)) return h.collider;
            return null;
        }

        /// The disc plus the vortex behind it, with 15 cm to spare, touches nothing solid.
        static bool Clear(Vector3 c, Quaternion face, int solid, Settings.PortalStyle look) => VolumeBlocker(c, face, solid, look) == null;

        // ---------------------------------------------------------------------------------------- the look

        public static ExitPortal Build(Vector3 centre, Vector3 ground, Vector3 head)
        {
            var p = new ExitPortal { Ground = ground, m_born = Time.unscaledTime };
            var root = new GameObject("HoF2VR_ExitPortal") { layer = 0 };   // layer 0: the PlayerCamera draws it, with occlusion
            p.Root = root.transform;
            Vector3 away = centre - head; away.y = 0f;
            p.Root.SetPositionAndRotation(centre, Quaternion.LookRotation(away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward));
            bool game = false;
            p.Look = Settings.PortalLook;
            p.R = RadiusOf(p.Look);
            try
            {
                game = p.Look == Settings.PortalStyle.Orb ? p.Orb() : p.Look == Settings.PortalStyle.Rift ? p.Rift()
                     : p.Look == Settings.PortalStyle.Halo ? p.Halo() || p.Orb() : p.FromWormhole();
            }
            catch (Exception e) { Plugin.L.LogWarning("exit portal: " + p.Look + " failed (" + e.Message + ") - procedural swirl"); }
            if (!game) p.Procedural();
            p.Extras();
            Plugin.L.LogInfo("exit portal: built at " + Diag.V(centre) + " (" + (game ? p.Look.ToString() : "procedural") + ")");
            return p;
        }

        /// Copies of the tunnel's three tube layers, scaled into a 1 m wide, 0.9 m deep vortex.
        bool FromWormhole()
        {
            MeshWormhole w = UI3D.Instance != null ? UI3D.Instance.Wormhole : null;
            if (w == null || s_inner == null) return false;
            var layers = new[] { s_outer2 != null ? s_outer2(w) : null, s_outer1 != null ? s_outer1(w) : null, s_inner(w) };
            float[] radius = { 1.0f, 0.92f, 0.78f };          // outer layers wider, inner cone narrower (as in the tunnel)
            float[] depth = { 1.0f, 0.85f, 0.7f };
            float[] spin = { 9f, -14f, 20f };                 // deg/s about the axis (the tunnel: +-5; livelier here)
            int made = 0;
            for (int i = 0; i < layers.Length; i++)
            {
                MeshRenderer r = layers[i];
                MeshFilter mf = r != null ? r.GetComponent<MeshFilter>() : null;
                if (mf == null || mf.sharedMesh == null || r.sharedMaterial == null) continue;
                Mesh mesh = mf.sharedMesh;
                Bounds b = mesh.bounds;                        // Cylinder001: x/y +-9.13, z 0..6.11
                float rr = Mathf.Max(b.extents.x, b.extents.y);
                var go = new GameObject("swirl" + i) { layer = 0 };
                go.transform.SetParent(Root, false);
                go.transform.localScale = new Vector3(Radius * radius[i] / rr, Radius * radius[i] / rr, Depth * depth[i] / Mathf.Max(0.01f, b.size.z));
                go.transform.localPosition = new Vector3(0f, 0f, -b.min.z * go.transform.localScale.z);   // its open end on the portal's face
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                var mat = new Material(r.sharedMaterial) { renderQueue = 3010 + i };   // a copy: the game's tunnel is untouched
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                m_tube.Add(mat);
                // the portal's own purple tints (the tunnel's current style, DefaultDark, is a dim blue and
                // amber that only works full-screen; previews/portal_defaultdark.png vs portal_purple.png).
                // Gamma-space values above 1 are HDR: the game's bloom makes the swirl glow.
                m_tint.Add(UseGameStyle && mat.HasProperty("_TintColor") ? mat.GetColor("_TintColor") : PortalTint[i]);
                m_spin.Add(go.transform);
                m_spinRate.Add(spin[i]);
                made++;
            }
            return made > 0;
        }

        readonly List<float> m_spinRate = new List<float>();
        readonly List<Vector3> m_spinAxis = new List<Vector3>();
        public Settings.PortalStyle Look = Settings.PortalStyle.Swirl;

        /// The game's wormhole materials (its tube layers: caustics, gradient waves), the three of them.
        static Material[] WormholeMaterials()
        {
            MeshWormhole w = UI3D.Instance != null ? UI3D.Instance.Wormhole : null;
            if (w == null || s_inner == null) return null;
            var layers = new[] { s_outer2 != null ? s_outer2(w) : null, s_outer1 != null ? s_outer1(w) : null, s_inner(w) };
            var mats = new Material[3];
            int n = 0;
            for (int i = 0; i < 3; i++) if (layers[i] != null && layers[i].sharedMaterial != null) { mats[i] = layers[i].sharedMaterial; n++; }
            return n > 0 ? mats : null;
        }

        /// Option "Orb" (the user, 2026-10-12: "is there a way to make it more portal/spherical-like?"): the same wormhole
        /// materials on nested spheres turning about different axes - a spherical vortex - round a dark core.
        bool Orb()
        {
            Material[] src = WormholeMaterials();
            if (src == null) return false;
            float[] radius = { 0.50f * K, 0.42f * K, 0.34f * K };
            float[] spin = { 16f, -24f, 34f };
            Vector3[] axis = { new Vector3(0.2f, 1f, 0.1f).normalized, new Vector3(1f, 0.3f, -0.2f).normalized, new Vector3(-0.3f, 0.2f, 1f).normalized };
            Mesh sphere = Sphere(1f, 28, 18);
            int made = 0;
            for (int i = 0; i < 3; i++)
            {
                if (src[i] == null) continue;
                var go = new GameObject("orb" + i) { layer = 0 };
                go.transform.SetParent(Root, false);
                go.transform.localScale = Vector3.one * radius[i];
                go.AddComponent<MeshFilter>().sharedMesh = sphere;
                var mr = go.AddComponent<MeshRenderer>();
                var mat = new Material(src[i]) { renderQueue = 3010 + i };
                mat.mainTextureScale = new Vector2(2f, 1f);
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                m_tube.Add(mat);
                m_tint.Add(PortalTint[i] * 1.1f);
                m_spin.Add(go.transform);
                m_spinRate.Add(spin[i]);
                m_spinAxis.Add(axis[i]);
                made++;
            }
            // the dark core: it reads as a hole in the world, not a ball
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            var core = new Material(sh) { renderQueue = 3000 };
            core.SetInt("_SrcBlend", 5); core.SetInt("_DstBlend", 10); core.SetInt("_ZWrite", 0); core.SetInt("_Cull", 2);
            core.color = new Color(0.03f, 0f, 0.07f, 0.85f);
            var cg = new GameObject("core") { layer = 0 };
            cg.transform.SetParent(Root, false);
            cg.transform.localScale = Vector3.one * 0.24f * K;
            cg.AddComponent<MeshFilter>().sharedMesh = sphere;
            cg.AddComponent<MeshRenderer>().sharedMaterial = core;
            m_core = core;
            return made > 0;
        }

        /// Option "Rift": an upright tear in the air - the wormhole's layers squeezed into a tall ellipse (a doorway shape)
        /// with a dark centre.
        bool Rift()
        {
            if (!FromWormhole()) return false;
            foreach (Transform t in m_spin) t.localScale = new Vector3(t.localScale.x * 0.62f, t.localScale.y * 1.55f, t.localScale.z);
            return true;
        }

        Material m_core;

        /// Option "Halo" (coordinator 2026-10-12: one look from a different game asset - "glowing orbs"): the game's own
        /// magic shield bubble (PFX_malaclypse_shield in its player_effects bundle: a glowing halo and an inverted glow,
        /// looping particles), twice its size, round the Orb's dark core. The prefab comes from the bundle the game has
        /// loaded; if it hasn't, the bundle is opened, the prefab copied and the bundle closed again at once (its objects
        /// kept), so the game can load it as usual. Falls back to the Orb.
        public const string HaloPrefab = "PFX_malaclypse_shield", HaloBundle = "player_effects";
        public static float HaloScale = 0.9f;   // (x2 made a faint bubble 2 m wide round the 0.3 m core)
        static GameObject s_halo;
        public static string HaloFrom = "-";

        bool Halo()
        {
            GameObject prefab = HaloSource();
            if (prefab == null) return false;
            var go = UnityEngine.Object.Instantiate(prefab, Root, false);
            go.name = "halo";
            go.SetActive(true);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one * HaloScale;
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 0;
            foreach (ParticleSystem ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                // the one-shot opening burst ("shield_start") is left as it is; the halo and glow loop
                if (main.loop) main.prewarm = true;
                ps.Clear(true);
                ps.Play(true);
            }
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true)) { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false; }
            m_haloGo = go;
            // the Orb's dark core: the light reads as round a hole, not a lamp
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            var core = new Material(sh) { renderQueue = 2999 };
            core.SetInt("_SrcBlend", 5); core.SetInt("_DstBlend", 10); core.SetInt("_ZWrite", 0); core.SetInt("_Cull", 2);
            core.color = new Color(0.03f, 0f, 0.07f, 0.85f);
            var cg = new GameObject("core") { layer = 0 };
            cg.transform.SetParent(Root, false);
            cg.transform.localScale = Vector3.one * 0.3f;
            cg.AddComponent<MeshFilter>().sharedMesh = Sphere(1f, 28, 18);
            cg.AddComponent<MeshRenderer>().sharedMaterial = core;
            m_core = core;
            return true;
        }

        GameObject m_haloGo;

        static GameObject HaloSource() => s_halo;

        /// The halo's prefab, found while the view is still dark or misted at the fight's start (once the hero and with him
        /// the player's effect set exist): it is not an asset of its own in the game's bundle - player_effects holds one
        /// loadable prefab, PlayerEffectSet, which references it - so it is looked for among the loaded objects (a search of
        /// some milliseconds: never while the player sees the fight).
        public static void PrepareTick()
        {
            string st = GameWatch.State;
            if (st == "GameState_Level_In" && s_lastState != st) s_fightNo++;
            s_lastState = st;
            // (test runs look for it too: the options page builds every look at the same spot)
            if (Settings.PortalLook != Settings.PortalStyle.Halo && !TestMode.Enabled || s_halo != null || s_haloFight == s_fightNo) return;
            if (!CombatVR.Active || !(VRFade.IsBlack || Limbo.On)) return;
            s_haloFight = s_fightNo;
            float t0 = Time.realtimeSinceStartup;
            try
            {
                foreach (GameObject g in Resources.FindObjectsOfTypeAll<GameObject>())
                    if (g != null && g.name == HaloPrefab && g.transform.parent == null && !g.scene.IsValid()) { s_halo = g; break; }
            }
            catch (Exception e) { HaloFrom = "error: " + e.Message; }
            HaloFrom = s_halo != null ? "found among the loaded objects (the player's effect set)" : "not loaded";
            Plugin.L.LogInfo("exit portal: halo prefab " + HaloFrom + " (" + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("0.0") + " ms, in the dark)");
        }

        static int s_haloFight = -1, s_fightNo;
        static string s_lastState = "";

        static Mesh Sphere(float r, int seg, int rings)
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var c = new List<Color>(); var t = new List<int>();
            for (int y = 0; y <= rings; y++)
            {
                float lat = Mathf.PI * y / rings;
                for (int x = 0; x <= seg; x++)
                {
                    float lon = 2f * Mathf.PI * x / seg;
                    v.Add(new Vector3(Mathf.Sin(lat) * Mathf.Cos(lon), Mathf.Cos(lat), Mathf.Sin(lat) * Mathf.Sin(lon)) * r);
                    uv.Add(new Vector2((float)x / seg, (float)y / rings));
                    // brighter toward the rim of what the eye sees is done by the additive layers overlapping; vertex colour full
                    c.Add(Color.white);
                }
            }
            for (int y = 0; y < rings; y++)
                for (int x = 0; x < seg; x++)
                {
                    int a = y * (seg + 1) + x, b = a + seg + 1;
                    t.Add(a); t.Add(b); t.Add(a + 1);
                    t.Add(a + 1); t.Add(b); t.Add(b + 1);
                }
            var m = new Mesh { name = "HoF2VR_PortalSphere" };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetColors(c); m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        /// No wormhole (should not happen in a fight): violet rings turning in opposite directions.
        void Procedural()
        {
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            for (int i = 0; i < 3; i++)
            {
                var mat = new Material(sh) { renderQueue = 3010 + i };
                mat.SetInt("_SrcBlend", 1); mat.SetInt("_DstBlend", 1); mat.SetInt("_ZWrite", 0); mat.SetInt("_Cull", 0);
                var go = new GameObject("ring" + i) { layer = 0 };
                go.transform.SetParent(Root, false);
                go.transform.localPosition = new Vector3(0f, 0f, 0.15f + i * 0.25f);
                go.AddComponent<MeshFilter>().sharedMesh = Ring(Radius * (1f - i * 0.25f), 0.12f, i);
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                m_tube.Add(mat);
                m_tint.Add(new Color(0.55f, 0.25f, 0.95f) * (1f - i * 0.2f));
                m_spin.Add(go.transform);
                m_spinRate.Add(i % 2 == 0 ? 25f : -35f);
            }
        }

        static Mesh Ring(float r, float w, int seed)
        {
            const int seg = 48;
            var v = new Vector3[seg * 2]; var c = new Color[seg * 2]; var t = new int[seg * 6];
            for (int s = 0; s < seg; s++)
            {
                float a = s * Mathf.PI * 2f / seg, k = 0.5f + 0.5f * Mathf.Sin(a * 3f + seed);
                v[s * 2] = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f);
                v[s * 2 + 1] = new Vector3(Mathf.Cos(a) * (r - w), Mathf.Sin(a) * (r - w), 0f);
                c[s * 2] = new Color(k, k * 0.6f, 1f, 1f); c[s * 2 + 1] = Color.black;
                int n = (s + 1) % seg;
                t[s * 6] = s * 2; t[s * 6 + 1] = n * 2; t[s * 6 + 2] = s * 2 + 1;
                t[s * 6 + 3] = n * 2; t[s * 6 + 4] = n * 2 + 1; t[s * 6 + 5] = s * 2 + 1;
            }
            var m = new Mesh { vertices = v, colors = c, triangles = t };
            m.RecalculateBounds();
            return m;
        }

        /// A dark throat at the back (the portal reads as a hole, not a decal), an additive rim, a light.
        void Extras()
        {
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            m_throat = new Material(sh) { renderQueue = 3000 };
            m_throat.SetInt("_SrcBlend", 5); m_throat.SetInt("_DstBlend", 10); m_throat.SetInt("_ZWrite", 0); m_throat.SetInt("_Cull", 0);
            var th = new GameObject("throat") { layer = 0 };
            th.transform.SetParent(Root, false);
            th.transform.localPosition = new Vector3(0f, 0f, Depth * 0.8f);
            th.AddComponent<MeshFilter>().sharedMesh = Disc(Radius * 0.95f, new Color(0.02f, 0.0f, 0.05f, 0.92f), new Color(0.05f, 0.0f, 0.1f, 0f));
            th.AddComponent<MeshRenderer>().sharedMaterial = m_throat;
            m_rim = new Material(sh) { renderQueue = 3020 };
            m_rim.SetInt("_SrcBlend", 1); m_rim.SetInt("_DstBlend", 1); m_rim.SetInt("_ZWrite", 0); m_rim.SetInt("_Cull", 0);
            var rim = new GameObject("rim") { layer = 0 };
            rim.transform.SetParent(Root, false);
            rim.AddComponent<MeshFilter>().sharedMesh = Ring(Radius * 1.06f, 0.09f, 7);
            rim.AddComponent<MeshRenderer>().sharedMaterial = m_rim;
            if (Round) { rim.SetActive(false); th.SetActive(false); }   // the orb and the halo have their own core
            var lg = new GameObject("light") { layer = 0 };
            lg.transform.SetParent(Root, false);
            lg.transform.localPosition = new Vector3(0f, 0f, -0.25f * K);
            m_light = lg.AddComponent<Light>();
            m_light.type = LightType.Point;
            // a radiant light (the user, 2026-10-12: "make it a radiant light source so you have an idea where it is even when
            // it spawns behind you"): it reaches the player's ground at the portal's usual 4-7 m; one light, no shadows
            m_light.range = LightRange * K;
            m_light.intensity = 0f;
            m_light.shadows = LightShadows.None;
            m_light.renderMode = LightRenderMode.ForcePixel;
            m_light.cullingMask = ~((1 << Limbo.Layer) | (1 << 16) | (1 << 26) | (1 << 30));
            // out of sight it announces itself (§7.7): a purple glow on the ground under it, and its hum from where it is
            m_glow = new Material(sh) { renderQueue = 3005 };
            m_glow.SetInt("_SrcBlend", 1); m_glow.SetInt("_DstBlend", 1); m_glow.SetInt("_ZWrite", 0); m_glow.SetInt("_Cull", 0);
            var gl = new GameObject("groundglow") { layer = 0 };
            gl.transform.SetParent(Root, false);
            gl.transform.position = Ground + Vector3.up * 0.04f;
            gl.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            gl.AddComponent<MeshFilter>().sharedMesh = Disc(1.1f * K, new Color(0.55f, 0.22f, 0.95f, 1f), new Color(0f, 0f, 0f, 0f));
            gl.AddComponent<MeshRenderer>().sharedMaterial = m_glow;
            try
            {
                m_hum = CombatSounds.Source("HoF2VR_PortalHum", CombatSounds.Hum, true, 28f * K);
                m_hum.transform.SetParent(Root, false);
                m_hum.volume = 0f;
                m_hum.Play();
            }
            catch (Exception e) { Plugin.L.LogWarning("exit portal: hum: " + e.Message); }
        }

        Material m_glow;
        AudioSource m_hum;
        public static float HumVolume = 0.9f, LightRange = 14f, LightIntensity = 3f;

        static Mesh Disc(float r, Color mid, Color edge)
        {
            const int seg = 40;
            var v = new Vector3[seg + 1]; var c = new Color[seg + 1]; var t = new int[seg * 3];
            v[0] = Vector3.zero; c[0] = mid;
            for (int s = 0; s < seg; s++)
            {
                float a = s * Mathf.PI * 2f / seg;
                v[s + 1] = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f); c[s + 1] = edge;
                t[s * 3] = 0; t[s * 3 + 1] = 1 + (s + 1) % seg; t[s * 3 + 2] = 1 + s;
            }
            var m = new Mesh { vertices = v, colors = c, triangles = t };
            m.RecalculateBounds();
            return m;
        }

        /// Each frame: fade in over 0.6 s, the swirl turns and its texture flows inward (the tunnel's
        /// UVScroller speed), the rim and light breathe and shift between the tunnel's light colours.
        public void Animate()
        {
            float age = Time.unscaledTime - m_born;
            float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(age / 0.6f));
            float breathe = 0.85f + 0.15f * Mathf.Sin(age * Mathf.PI * 2f / 3.5f);
            float dt = Time.unscaledDeltaTime;
            for (int i = 0; i < m_tube.Count; i++)
            {
                if (i < m_spinAxis.Count) m_spin[i].Rotate(m_spinAxis[i], m_spinRate[i] * dt, Space.Self);
                else m_spin[i].Rotate(0f, 0f, m_spinRate[i] * dt, Space.Self);
                Material m = m_tube[i];
                if (m.HasProperty("_MainTex")) m.mainTextureOffset += new Vector2(0.01f, 0.05f) * dt;
                if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", m_tint[i] * (a * breathe));
                else m.color = m_tint[i] * (a * breathe);
            }
            float k = Mathf.PingPong(age / 4f, LightCols.Length - 1);
            int i0 = Mathf.FloorToInt(k);
            Color lc = Color.Lerp(LightCols[i0], LightCols[Mathf.Min(i0 + 1, LightCols.Length - 1)], k - i0);
            m_rim.color = Color.Lerp(lc, new Color(0.6f, 0.3f, 1f), 0.5f) * (0.5f * a * breathe);
            m_throat.color = new Color(1f, 1f, 1f, a);
            m_light.color = Color.Lerp(lc, new Color(0.62f, 0.3f, 1f), 0.55f);
            m_light.intensity = LightIntensity * a * breathe;
            m_light.enabled = !m_lightOff;
            if (m_core != null) m_core.color = new Color(0.03f, 0f, 0.07f, 0.85f * a);
            if (m_glow != null) m_glow.color = new Color(1f, 1f, 1f, 1f) * (0.55f * a * breathe);
            // the hum swells once as it opens (to full by 1.2 s), then settles
            if (m_hum != null) m_hum.volume = HumVolume * (age < 1.2f ? age / 1.2f : Mathf.Lerp(1f, 0.6f, Mathf.Clamp01((age - 1.2f) / 2.5f)));
        }

        bool m_lightOff;
        public void LightOn(bool on) { m_lightOff = !on; if (m_light != null) m_light.enabled = on; }

        public void Destroy()
        {
            if (Root != null) UnityEngine.Object.Destroy(Root.gameObject);
            foreach (Material m in m_tube) UnityEngine.Object.Destroy(m);
            if (m_glow != null) UnityEngine.Object.Destroy(m_glow);
            Root = null;
        }

        // ---------------------------------------------------------------------------------------- the ways in

        /// A point inside the portal's slab (a little thicker than the face, so a quick poke counts).
        /// The round looks (Orb, Halo): touched or walked into anywhere within the sphere.
        bool Round => Look == Settings.PortalStyle.Orb || Look == Settings.PortalStyle.Halo;

        public bool Contains(Vector3 world)
        {
            Vector3 l = Root.InverseTransformPoint(world);
            if (Round) return l.magnitude < R + 0.05f;
            return new Vector2(l.x, l.y).magnitude < R + 0.05f && l.z > -0.12f && l.z < 0.25f;
        }

        /// Walking in: the hero's feet within 0.55 m of the portal's ground point, or the head passing
        /// through the face (front to back, inside the disc).
        public bool WalkedIn(Vector3 heroFeet, Vector3 head, ref float lastHeadZ)
        {
            Vector3 d = heroFeet - Ground; d.y = 0f;
            if (d.magnitude < 0.55f * K) return true;
            Vector3 l = Root.InverseTransformPoint(head);
            if (Round && l.magnitude < R) return true;
            bool crossed = lastHeadZ < 0f && l.z >= 0f && new Vector2(l.x, l.y).magnitude < R + 0.1f;
            lastHeadZ = l.z;
            return crossed;
        }

        /// Pointing with the laser: the ray meets the face within the disc (+25 cm), or points within 10 deg
        /// of the centre.
        public bool PointedAt(Ray r, out string miss)
        {
            var plane = new Plane(-Root.forward, Root.position);
            float dist;
            bool on = plane.Raycast(r, out dist) && dist > 0f && dist < 40f;
            Vector3 l = on ? Root.InverseTransformPoint(r.GetPoint(dist)) : Vector3.zero;
            float ang = Vector3.Angle(r.direction, Root.position - r.origin);
            bool hit = on && new Vector2(l.x, l.y).magnitude < R + 0.25f;
            miss = ang.ToString("0.0") + " deg off the centre" + (on ? ", face local " + Diag.V(l) : ", misses the face");
            return hit || ang < 10f;
        }
    }
}

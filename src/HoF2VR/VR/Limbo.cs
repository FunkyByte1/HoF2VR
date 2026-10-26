using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// The waiting space (docs/loading-design.md §3.1): a calm, misty night clearing with a warm glow
    /// ahead. The player stands in it while the game loads (every place change, and the boot) instead
    /// of in black. It lives in a world of its own far below every game place (W0), on its own layer
    /// (7, unused by the game) and is drawn by its own camera (depth 50: after every game camera,
    /// before the overlay that draws the 2D panel, the laser and the fade). Only unlit vertex-colour
    /// meshes, so no game light, fog, skybox or post effect touches it. Nothing near the player moves:
    /// only distant embers rise from the glow and a few stars twinkle. The view inside follows nothing
    /// but the player's own head, in a tracking frame taken when the space appears and kept until it
    /// is gone, so a recenter or a new place being set up behind it never moves it.
    ///
    /// VRFade runs it: once a place hold has kept the view dark for a moment, the space appears out of
    /// the mist; when the hold is released, the mist thickens again, the space goes, and the new place
    /// is revealed from the mist (fades go to the mist colour, not black). Setting Comfort.WaitingSpace
    /// (false = the old black).
    internal class Limbo : MonoBehaviour
    {
        public static Limbo I;
        public const int Layer = 7;
        /// The title's (and the UI scene's) fog colour: the space blends into the title night.
        public static readonly Color Mist = new Color(0.040f, 0.075f, 0.110f, 1f);
        static readonly Vector3 W0 = new Vector3(0f, -6000f, 0f);
        /// Full dark for this long before the space appears (quick hops - table to side table - stay plain).
        const float ShowDelay = 0.3f;
        public enum Flavour { Plain, Boot }

        Camera m_cam;
        Transform m_root, m_glowRoot;
        /// v2 (docs/loading-v2-design.md §1): the title's campfire clearing from the asset pack; null = the
        /// procedural space below (no pack, or it failed to build).
        LimboCamp m_camp;
        LimboEnv m_env;
        bool m_campOn;   // this showing uses the campfire clearing (setting Comfort.WaitingSpaceLook)
        Material m_glowMat, m_coreMat, m_sparkMat;
        Mesh m_embers, m_stars;
        bool m_on;
        float m_cover = 1f;       // the mist over the space (1 = hidden)
        float m_darkSince = -1f;
        float m_shownAt;
        Flavour m_flavour;
        // the player's frame in the space (tracking space): fixed while it is shown
        Vector3 m_origin;
        float m_yaw, m_lift;
        bool m_frameOk;
        float m_bootGraceUntil = -1f;
        bool m_testOn;
        string m_shotTag;
        int m_shotN;
        int m_shotStep;
        static readonly float[] ShotTimes = { 0.7f, 4f };

        public static bool On => I != null && I.m_on;
        /// The space is up or being covered: the world behind it stays hidden (VRFade keeps its hold).
        public static bool Busy => I != null && I.m_on;
        public static float Cover => I != null ? I.m_cover : 1f;
        /// The last space shown was the boot one: the title is revealed more slowly (mist clearing).
        public static bool SlowReveal;
        /// Boot: the space is wanted from the first frame, before Places has asked for its hold.
        public static bool BootGrace => I != null && Time.unscaledTime < I.m_bootGraceUntil;
        public static bool TestWanted => I != null && I.m_testOn;
        /// Plain mist over the space while it stays up (LoadSmooth, around a known long freeze).
        public static bool Veil;
        /// The player pressed A for a held arena (FightFlow): plain mist until the place hold ends (the arena activates behind it).
        public static bool ArenaVeil;
        public static int Shown;   // diagnostics

        void Awake()
        {
            I = this;
            Diag.StatusExtras["limbo"] = () => Diag.Q(m_on ? m_flavour + " cover " + m_cover.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                                                            " eye " + (m_origin.y + m_lift).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "off");
            Diag.StatusExtras["limboShown"] = () => Shown.ToString();
            // `limbo show|hide|info`: the waiting space on demand (a test-owned dark hold);
            // `limboshot <tag>|off`: eye captures 0.7 s and 4 s after the space appears
            Diag.Commands["limbo"] = a =>
            {
                string what = a.Length > 1 ? a[1] : "info";
                if (what == "show") { m_testOn = true; if (VRFade.I != null) VRFade.I.Hold("limbotest"); }
                else if (what == "hide") { m_testOn = false; if (VRFade.I != null) VRFade.I.Release("limbotest"); }
                return "limbo " + (m_on ? "on " + m_flavour + " cover " + m_cover.ToString("0.00") : "off") + " setting " + Settings.WaitingSpace + " " + Settings.WaitingSpaceLook +
                       " camp " + (m_camp != null ? (m_campOn ? "shown" : "built") : "none") +
                       " progress " + LoadProgress.Key + " " + LoadProgress.Value.ToString("0.00") + " " + LoadProgress.Phase +
                       " cam " + (m_cam != null ? Diag.V(m_cam.transform.position) + " enabled " + m_cam.enabled : "-") + " shown " + Shown;
            };
            Diag.Commands["limboshot"] = a => { m_shotTag = a.Length > 1 && a[1] != "off" ? a[1] : null; return "limboshot " + (m_shotTag ?? "off"); };
            // `ambient [refresh]`: the game's current ambient; refresh = recompute it from the sky (DynamicGI.UpdateEnvironment)
            Diag.Commands["ambient"] = a =>
            {
                if (a.Length > 1 && a[1] == "refresh") DynamicGI.UpdateEnvironment();
                if (a.Length > 2 && a[1] == "swap") LimboEnv.SwapAmbient = a[2] == "1";
                return LimboEnv.Describe() + " | swap " + LimboEnv.SwapAmbient;
            };
        }

        // ---- run by VRFade -------------------------------------------------------------------------

        /// Boot: the space from the very first frame (nothing has been seen yet: no fade in).
        public void StartBoot()
        {
            m_bootGraceUntil = Time.unscaledTime + 3f;
            Show(Flavour.Boot);
            m_cover = 0f;
        }

        /// Comfort.QuickHopFade (the user's answer, 2026-10-11 evening): the campfire space with its board and "Press (A)"
        /// only for the start of the game and on the way into a fight; every other change of place (back to the table after
        /// a fight, title to table, after a death...) is a short fade through the mist.
        public static bool SpaceForThisHold()
        {
            // item 30: the fight card touched once its fight had loaded - straight in, no campfire stop
            if (FightFlow.Direct) return false;
            if (!Settings.QuickHopFade) return true;
            string st = GameWatch.State;
            return st == "" || st == "GameState_Init" || st == "GameState_Level_In" || st == "GameState_BossBattle_In";
        }

        /// Each frame. wanted = a place hold wants the space; dark = the view is fully dark now.
        public void Step(bool wanted, bool dark, float dt)
        {
            if (!m_on)
            {
                if (wanted && dark && SpaceForThisHold())
                {
                    if (m_darkSince < 0f) m_darkSince = Time.unscaledTime;
                    if (Time.unscaledTime - m_darkSince >= ShowDelay)
                    {
                        string st = GameWatch.State;
                        Show(st == "" || st == "GameState_Init" ? Flavour.Boot : Flavour.Plain);
                    }
                }
                else m_darkSince = -1f;
                return;
            }
            // Veil: plain mist over the space for a moment (a known long freeze is coming: a frozen frame of plain
            // mist cannot judder); the space stays and clears again afterwards
            if (wanted && !Veil && !ArenaVeil) m_cover = Mathf.Max(0f, m_cover - dt / (m_flavour == Flavour.Boot ? 0.6f : VRFade.HoldInTime));
            else
            {
                m_cover = Mathf.Min(1f, m_cover + dt / (m_flavour == Flavour.Boot && !Veil && !ArenaVeil ? 0.4f : VRFade.HoldOutTime));
                if (m_cover >= 1f && !wanted) Hide();
            }
            if (m_on && m_shotTag != null && Diag.I != null && m_shotStep < ShotTimes.Length && Time.unscaledTime - m_shownAt > ShotTimes[m_shotStep])
            {
                Diag.I.Run("shot " + m_shotTag + "_" + m_shotN + "_" + m_flavour + "_" + GameWatch.State.Replace("GameState_", "") + "_" + Mathf.RoundToInt(ShotTimes[m_shotStep] * 10));
                m_shotStep++;
            }
        }

        static bool s_broken;   // building failed once: plain dark for the rest of the session

        int m_handsFrame = -1;
        /// The gloves (and before a fight the weapon) in the space (LimboHands).
        public static bool ShowHands = true;

        /// The space faces the way the head looks when it appears (`limbo face 0|1`).
        public static bool FaceTheFire = true;

        void Show(Flavour f)
        {
            if (s_broken) return;
            if (m_root == null)
            {
                try { Build(); }
                catch (System.Exception e)
                {
                    s_broken = true;
                    Plugin.L.LogError("limbo: could not build the waiting space - plain dark instead: " + e);
                    return;
                }
            }
            m_flavour = f;
            SlowReveal = f == Flavour.Boot;
            CaptureFrame();
            // the campfire clearing, or the simple misty clearing (the setting; both are built)
            m_campOn = m_camp != null && Settings.WaitingSpaceLook == Settings.SpaceLook.Campfire;
            foreach (Transform c in m_root)
                c.gameObject.SetActive(c.name.StartsWith("HoF2VR_Limbo") == m_campOn);   // camp, soft stars, progress vs the simple parts
            if (m_env != null) m_env.enabled = m_campOn;
            if (m_campOn) m_camp.Place(f == Flavour.Boot);   // boot: the title spot (the fire = the title campfire)
            else PlaceGlow(f == Flavour.Boot ? new Vector3(0.19f, 0f, 6.74f) : new Vector3(0f, 0f, 4f));
            m_root.gameObject.SetActive(true);
            m_cam.enabled = true;
            m_on = true;
            m_cover = 1f;
            m_shownAt = Time.unscaledTime;
            m_shotStep = 0;
            m_shotN++;
            Shown++;
            m_darkSince = -1f;
            Plugin.L.LogInfo("limbo: waiting space on (" + f + ", state " + GameWatch.State + ")");
        }

        void Hide()
        {
            LimboHands.Pose(W0, m_origin, m_yaw, m_lift, false);
            Veil = false;
            ArenaVeil = false;
            m_on = false;
            m_cover = 1f;
            if (m_cam != null) m_cam.enabled = false;
            if (m_root != null) m_root.gameObject.SetActive(false);
            m_bootGraceUntil = -1f;
            Plugin.L.LogInfo("limbo: waiting space off after " + (Time.unscaledTime - m_shownAt).ToString("0.0") + " s (state " + GameWatch.State + ")");
            LimboEnv.AfterShown();
        }

        /// The player's frame in the space: the rig's neutral spot and facing (the way the place
        /// and its 2D panel face), taken once.
        void CaptureFrame()
        {
            Vector3 np;
            float ny, lift;
            VRRig.I.NeutralFrame(out np, out ny, out lift);
            // the space is away from the tables: a seated player is lifted to about standing height here, as in fights
            // (VRRig.AwayLift; standing: no lift), not the seated table's lift of the place they came from
            lift = VRRig.AwayLift(np.y);
            Vector3 head = VRInput.HeadPos;
            if (np == Vector3.zero && head != Vector3.zero)
            {
                np = head;
                ny = VRInput.HeadRot.eulerAngles.y;
                lift = VRRig.AwayLift(head.y);
            }
            else if (head != Vector3.zero && FaceTheFire)
            {
                // "I should always face the campfire when I spawn in the loading world. Once I was facing directly opposite
                // to it" (user 2026-10-11 evening): the space is laid out from where the head is and looks as it appears
                // (the fire and the board ahead of it), not from the place's last recentre
                np = new Vector3(head.x, np.y, head.z);
                ny = VRInput.HeadRot.eulerAngles.y;
            }
            m_origin = np;
            m_yaw = ny;
            m_lift = lift;
            m_frameOk = head != Vector3.zero;
        }

        /// Pose of the space's camera from the head (called before it culls; the late head pose of
        /// this frame is in VRInput by then).
        void PoseCamera()
        {
            if (!m_on || VRRig.I == null) return;
            if (!m_frameOk && VRInput.HeadPos != Vector3.zero) CaptureFrame();
            Vector3 head = VRInput.HeadPos;
            Quaternion inv = Quaternion.Euler(0f, -m_yaw, 0f);
            Vector3 d = inv * new Vector3(head.x - m_origin.x, 0f, head.z - m_origin.z);
            d.y = head.y + m_lift;
            Vector3 pos = W0 + d;
            Quaternion rot = inv * VRInput.HeadRot;
            m_cam.transform.SetPositionAndRotation(pos, rot);
            VRRig.SetEyes(m_cam, pos, rot, 1f);
            if (Time.frameCount != m_handsFrame)
            {
                m_handsFrame = Time.frameCount;
                try { LimboHands.Pose(W0, m_origin, m_yaw, m_lift, m_on && ShowHands); }
                catch (System.Exception e) { ShowHands = false; Plugin.L.LogWarning("limbo: hands off after an error: " + e.Message); }
            }
        }

        internal class Eye : MonoBehaviour
        {
            void OnPreCull() { if (I != null) I.PoseCamera(); }
        }

        void LateUpdate()
        {
            if (!m_on || m_root == null) return;
            Animate(Time.unscaledTime);
        }

        // ---- building ------------------------------------------------------------------------------

        /// Vertex colours reach the screen as they are; the game renders in linear colour space, so
        /// colours meant as sRGB (like the clear colour, which Unity converts itself) are converted.
        static Color Lin(Color c) => QualitySettings.activeColorSpace == ColorSpace.Linear ? new Color(Mathf.GammaToLinearSpace(c.r), Mathf.GammaToLinearSpace(c.g), Mathf.GammaToLinearSpace(c.b), c.a) : c;

        static Material Mat(int src, int dst, bool zwrite, int queue)
        {
            var m = new Material(Shader.Find("Hidden/Internal-Colored")) { renderQueue = queue, color = Color.white };
            m.SetInt("_SrcBlend", src);
            m.SetInt("_DstBlend", dst);
            m.SetInt("_ZWrite", zwrite ? 1 : 0);
            m.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            m.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            return m;
        }

        GameObject Part(string name, Mesh mesh, Material mat, Transform parent)
        {
            var go = new GameObject(name) { layer = Layer };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return go;
        }

        /// Rings around the centre in the XZ plane (or XY when vertical), one colour per ring.
        static Mesh Disc(string name, float[] radii, Color[] cols, int seg, bool vertical)
        {
            var v = new List<Vector3>();
            var c = new List<Color>();
            var t = new List<int>();
            v.Add(Vector3.zero);
            c.Add(cols[0]);
            for (int r = 1; r < radii.Length; r++)
                for (int s = 0; s < seg; s++)
                {
                    float a = s * Mathf.PI * 2f / seg;
                    float x = Mathf.Cos(a) * radii[r], y = Mathf.Sin(a) * radii[r];
                    v.Add(vertical ? new Vector3(x, y, 0f) : new Vector3(x, 0f, y));
                    c.Add(cols[r]);
                }
            for (int s = 0; s < seg; s++) { t.Add(0); t.Add(1 + (s + 1) % seg); t.Add(1 + s); }
            for (int r = 1; r < radii.Length - 1; r++)
            {
                int a0 = 1 + (r - 1) * seg, b0 = 1 + r * seg;
                for (int s = 0; s < seg; s++)
                {
                    int s1 = (s + 1) % seg;
                    t.Add(a0 + s); t.Add(a0 + s1); t.Add(b0 + s1);
                    t.Add(a0 + s); t.Add(b0 + s1); t.Add(b0 + s);
                }
            }
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        /// A soft round glow (additive): bright middle, nothing at the rim.
        static Mesh Glow(string name, float radius, Color centre, bool vertical, float sharp)
        {
            int n = 9;
            var radii = new float[n];
            var cols = new Color[n];
            for (int i = 0; i < n; i++)
            {
                float f = (float)i / (n - 1);
                radii[i] = radius * f;
                float k = i == n - 1 ? 0f : Mathf.Exp(-sharp * f * f);
                cols[i] = Lin(new Color(centre.r * k, centre.g * k, centre.b * k, 1f));
            }
            return Disc(name, radii, cols, 40, vertical);
        }

        void Build()
        {
            var rootGo = new GameObject("HoF2VR_Limbo") { layer = Layer };
            DontDestroyOnLoad(rootGo);
            m_root = rootGo.transform;
            m_root.position = W0;
            m_camp = LimboCamp.TryBuild(m_root, Layer);

            Material ground = Mat(1, 0, true, 2000);
            Material blend = Mat(5, 10, false, 3000);
            m_glowMat = Mat(1, 1, false, 3010);
            m_coreMat = Mat(1, 1, false, 3011);
            m_sparkMat = Mat(1, 1, false, 3020);
            Material stars = Mat(1, 1, false, 2500);

            // ground: warm dark earth under the feet, fading into the mist (fake fog, no horizon line)
            var stone = new Color(0.16f, 0.12f, 0.09f, 1f);
            float[] gr = { 0f, 1f, 2f, 3.5f, 5f, 7f, 9.5f, 12.5f, 16f, 20f, 25f, 30f, 40f };
            var gc = new Color[gr.Length];
            for (int i = 0; i < gr.Length; i++)
            {
                float k = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(2f, 30f, gr[i]));
                gc[i] = Lin(Color.Lerp(stone, Mist, k));
                gc[i].a = 1f;
            }
            Part("ground", Disc("HoF2VR_LimboGround", gr, gc, 72, false), ground, m_root);

            // horizon band: a faint wall of mist all round (depth without objects)
            {
                int seg = 72;
                float r = 35f;
                var v = new List<Vector3>();
                var c = new List<Color>();
                var t = new List<int>();
                float[] hy = { 0f, 2.5f, 8f };
                float[] ha = { 0.5f, 0.3f, 0f };
                var band = new Color(Mist.r * 1.4f, Mist.g * 1.4f, Mist.b * 1.5f, 1f);
                for (int row = 0; row < hy.Length; row++)
                    for (int s = 0; s < seg; s++)
                    {
                        float a = s * Mathf.PI * 2f / seg;
                        v.Add(new Vector3(Mathf.Cos(a) * r, hy[row], Mathf.Sin(a) * r));
                        c.Add(Lin(new Color(band.r, band.g, band.b, ha[row])));
                    }
                for (int row = 0; row < hy.Length - 1; row++)
                    for (int s = 0; s < seg; s++)
                    {
                        int s1 = (s + 1) % seg, a0 = row * seg, b0 = (row + 1) * seg;
                        t.Add(a0 + s); t.Add(b0 + s); t.Add(a0 + s1);
                        t.Add(a0 + s1); t.Add(b0 + s); t.Add(b0 + s1);
                    }
                var m = new Mesh { name = "HoF2VR_LimboBand" };
                m.SetVertices(v);
                m.SetColors(c);
                m.SetTriangles(t, 0);
                m.RecalculateBounds();
                Part("band", m, blend, m_root);
            }

            // the glow (a fire's light seen through mist): on the ground, a tall soft halo, a small core
            var gRoot = new GameObject("glow") { layer = Layer };
            gRoot.transform.SetParent(m_root, false);
            m_glowRoot = gRoot.transform;
            var warm = new Color(1.0f, 0.55f, 0.2f, 1f);
            Part("groundglow", Glow("HoF2VR_LimboGroundGlow", 3.2f, warm * 0.40f, false, 3.5f), m_glowMat, m_glowRoot).transform.localPosition = new Vector3(0f, 0.01f, 0f);
            Part("halo", Glow("HoF2VR_LimboHalo", 1.9f, warm * 0.40f, true, 3.0f), m_glowMat, m_glowRoot).transform.localPosition = new Vector3(0f, 0.75f, 0f);
            Part("core", Glow("HoF2VR_LimboCore", 0.45f, new Color(1.0f, 0.72f, 0.38f) * 0.7f, true, 3.0f), m_coreMat, m_glowRoot).transform.localPosition = new Vector3(0f, 0.28f, 0f);

            // embers rising from the glow (rebuilt each frame; far from the eyes)
            m_embers = new Mesh { name = "HoF2VR_LimboEmbers" };
            m_embers.MarkDynamic();
            var ev = new Vector3[EmberCount * 4];
            var ec = new Color[EmberCount * 4];
            var et = new int[EmberCount * 6];
            for (int i = 0; i < EmberCount; i++)
            {
                et[i * 6] = i * 4; et[i * 6 + 1] = i * 4 + 1; et[i * 6 + 2] = i * 4 + 2;
                et[i * 6 + 3] = i * 4; et[i * 6 + 4] = i * 4 + 2; et[i * 6 + 5] = i * 4 + 3;
            }
            m_embers.vertices = ev;
            m_embers.colors = ec;
            m_embers.triangles = et;
            m_embers.bounds = new Bounds(Vector3.zero, Vector3.one * 50f);
            Part("embers", m_embers, m_sparkMat, m_glowRoot);
            var rnd = new System.Random(7);
            for (int i = 0; i < EmberCount; i++) m_ember[i] = NewEmber(rnd, -(float)rnd.NextDouble() * EmberLife);
            m_rnd = rnd;

            // stars on a far dome above 12 degrees
            {
                var v = new List<Vector3>();
                var c = new List<Color>();
                var t = new List<int>();
                for (int i = 0; i < StarCount; i++)
                {
                    float az = (float)rnd.NextDouble() * 360f;
                    float el = Mathf.Lerp(12f, 80f, Mathf.Sqrt((float)rnd.NextDouble()));
                    Vector3 dir = Quaternion.Euler(-el, az, 0f) * Vector3.forward;
                    Vector3 p = dir * 90f;
                    float size = Mathf.Lerp(0.22f, 0.42f, (float)rnd.NextDouble());
                    Vector3 right = Vector3.Cross(Vector3.up, dir).normalized * size, up = Vector3.Cross(dir, right).normalized * size;
                    float b = Mathf.Lerp(0.15f, 0.6f, (float)rnd.NextDouble() * (float)rnd.NextDouble());
                    m_starBase[i] = b;
                    m_starPhase[i] = (float)rnd.NextDouble() * 10f;
                    m_starRate[i] = i % 3 == 0 ? 2f * Mathf.PI / Mathf.Lerp(3f, 8f, (float)rnd.NextDouble()) : 0f;
                    int b0 = v.Count;
                    v.Add(p - right - up); v.Add(p - right + up); v.Add(p + right + up); v.Add(p + right - up);
                    var col = Lin(new Color(b * 0.85f, b * 0.9f, b, 1f));
                    c.Add(col); c.Add(col); c.Add(col); c.Add(col);
                    t.Add(b0); t.Add(b0 + 1); t.Add(b0 + 2); t.Add(b0); t.Add(b0 + 2); t.Add(b0 + 3);
                }
                m_stars = new Mesh { name = "HoF2VR_LimboStars" };
                m_stars.MarkDynamic();
                m_stars.SetVertices(v);
                m_stars.SetColors(c);
                m_stars.SetTriangles(t, 0);
                m_stars.RecalculateBounds();
                m_starCols = c.ToArray();
                Part("stars", m_stars, stars, m_root);
            }

            // the camera
            var camGo = new GameObject("HoF2VR_LimboCamera");
            DontDestroyOnLoad(camGo);
            m_cam = camGo.AddComponent<Camera>();
            m_cam.clearFlags = CameraClearFlags.SolidColor;
            m_cam.backgroundColor = Mist;
            m_cam.cullingMask = 1 << Layer;
            m_cam.depth = 50;
            m_cam.nearClipPlane = 0.05f;
            m_cam.farClipPlane = 300f;
            m_cam.renderingPath = RenderingPath.Forward;
            m_cam.allowHDR = false;
            m_cam.allowMSAA = true;   // like the overlay: the eye textures are MSAA
            m_cam.useOcclusionCulling = false;
            m_cam.stereoTargetEye = StereoTargetEyeMask.Both;
            XRDevice.DisableAutoXRCameraTracking(m_cam, true);
            camGo.AddComponent<Eye>();
            if (m_camp != null)
            {
                // its own fog and ambient for its render only (on while the campfire clearing is shown)
                m_env = camGo.AddComponent<LimboEnv>();
                m_env.enabled = false;
            }
            m_cam.enabled = false;
            m_root.gameObject.SetActive(false);
            Plugin.L.LogInfo("limbo: waiting space built at " + Diag.V(W0) + " (layer " + Layer + ")");
        }

        public Camera Cam => m_cam;

        /// The player's frame in the space, in tracking space (the real room): where the head was and which way it faced
        /// when the space appeared. Things that belong to the space but are drawn in the game world (the 2D panel) stay put
        /// in it with VRRig.ToWorld, whatever the rig does behind the space.
        public static bool TrackingFrame(out Vector3 origin, out float yaw)
        {
            origin = Vector3.zero;
            yaw = 0f;
            if (I == null || !I.m_on || !I.m_frameOk) return false;
            origin = I.m_origin;
            yaw = I.m_yaw;
            return true;
        }

        /// The space's frame (its root at W0 = the player's spot, facing +z) as seen by its camera: world point of a
        /// spot given in the player's frame (x right, y up from the feet, z ahead).
        public static Vector3 SpacePoint(Vector3 local) => W0 + local;
        public static Transform Root => I != null ? I.m_root : null;
        /// The player's eye height in the space (tracking height + seated lift), for things placed at eye level.
        public static float EyeHeight => I != null ? I.m_origin.y + I.m_lift : 1.65f;

        void PlaceGlow(Vector3 local)
        {
            m_glowRoot.localPosition = local;
            Vector3 toPlayer = -local;
            toPlayer.y = 0f;
            m_glowRoot.localRotation = toPlayer.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(-toPlayer.normalized) : Quaternion.identity;
        }

        // ---- motion: breathing glow, embers, twinkling stars ----------------------------------------

        const int EmberCount = 24, StarCount = 300;
        const float EmberLife = 4f;
        struct Ember { public Vector3 P, V; public float Born, Size; }
        readonly Ember[] m_ember = new Ember[EmberCount];
        System.Random m_rnd;
        readonly float[] m_starBase = new float[StarCount], m_starPhase = new float[StarCount], m_starRate = new float[StarCount];
        Color[] m_starCols;
        readonly Vector3[] m_ev = new Vector3[EmberCount * 4];
        readonly Color[] m_ec = new Color[EmberCount * 4];

        Ember NewEmber(System.Random r, float born)
        {
            float a = (float)r.NextDouble() * Mathf.PI * 2f, d = (float)r.NextDouble() * 0.3f;
            return new Ember
            {
                P = new Vector3(Mathf.Cos(a) * d, 0.15f + (float)r.NextDouble() * 0.2f, Mathf.Sin(a) * d),
                V = new Vector3(((float)r.NextDouble() - 0.5f) * 0.1f, 0.2f + (float)r.NextDouble() * 0.1f, ((float)r.NextDouble() - 0.5f) * 0.1f),
                Born = born,
                Size = 0.015f + (float)r.NextDouble() * 0.015f,
            };
        }

        float m_t0 = -1f;

        void Animate(float now)
        {
            if (m_t0 < 0f) m_t0 = now;
            float t = now - m_t0;
            if (m_campOn)
            {
                // the progress lights: LoadProgress (work/limbo2/progress) - a fill when the wait is known,
                // a slow wave when it is not
                // (not for quick hops: only once a wait has lasted 0.8 s, or while it finishes)
                // (the board shows the progress since 2026-10-11 afternoon; the fire-lights only without it)
                m_camp.Progress.Visible = !Settings.WaitingBoard && LoadProgress.Visible && (LoadProgress.Elapsed > 0.8f || !LoadProgress.Active);
                m_camp.Progress.Value = LoadProgress.Known ? LoadProgress.Value : -1f;
                m_camp.Tick(t);
                return;
            }
            float breathe = 1f + 0.1f * Mathf.Sin(t * Mathf.PI * 2f / 4f);
            m_glowMat.color = new Color(breathe, breathe, breathe, 1f);
            float flicker = 1f + 0.06f * Mathf.Sin(t * 7.3f) + 0.04f * Mathf.Sin(t * 11.9f + 1f);
            m_coreMat.color = new Color(flicker * breathe, flicker * breathe, flicker * breathe, 1f);

            // embers live in the glow's local frame (it faces the player): x right, y up, z away
            var warm = new Color(1f, 0.5f, 0.15f, 1f);
            for (int i = 0; i < EmberCount; i++)
            {
                float age = t - m_ember[i].Born;
                if (age > EmberLife) { m_ember[i] = NewEmber(m_rnd, t); age = 0f; }
                float life = Mathf.Max(0f, age);
                Vector3 p = m_ember[i].P + m_ember[i].V * life + new Vector3(Mathf.Sin(life * 1.7f + i) * 0.04f, 0f, 0f);
                float k = Mathf.Clamp01(life / 0.4f) * Mathf.Clamp01((EmberLife - life) / 1.6f);
                float s = m_ember[i].Size;
                m_ev[i * 4] = p + new Vector3(-s, -s, 0f);
                m_ev[i * 4 + 1] = p + new Vector3(-s, s, 0f);
                m_ev[i * 4 + 2] = p + new Vector3(s, s, 0f);
                m_ev[i * 4 + 3] = p + new Vector3(s, -s, 0f);
                var col = Lin(new Color(warm.r * k, warm.g * k, warm.b * k, 1f));
                m_ec[i * 4] = m_ec[i * 4 + 1] = m_ec[i * 4 + 2] = m_ec[i * 4 + 3] = col;
            }
            m_embers.vertices = m_ev;
            m_embers.colors = m_ec;

            for (int i = 0; i < StarCount; i++)
            {
                if (m_starRate[i] == 0f) continue;
                float b = m_starBase[i] * (0.65f + 0.35f * Mathf.Sin(t * m_starRate[i] + m_starPhase[i]));
                var col = Lin(new Color(b * 0.85f, b * 0.9f, b, 1f));
                m_starCols[i * 4] = m_starCols[i * 4 + 1] = m_starCols[i * 4 + 2] = m_starCols[i * 4 + 3] = col;
            }
            m_stars.colors = m_starCols;
        }
    }
}

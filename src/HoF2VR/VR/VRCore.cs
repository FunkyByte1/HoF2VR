using UnityEngine;
using UnityEngine.XR;

namespace HoF2VR
{
    /// Per-frame VR housekeeping that is not tied to a camera: input polling and status reporting.
    internal class VRCore : MonoBehaviour
    {
        public static float ScaleOverride = -1f;

        void Awake()
        {
            Diag.Commands["scale"] = a =>
            {
                ScaleOverride = a.Length > 1 ? float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture) : -1f;
                return "render scale override " + ScaleOverride;
            };
            Diag.StatusExtras["handL"] = () => Diag.Q(VRInput.L.Tracked ? Diag.V(VRInput.L.Pos) : "untracked");
            Diag.StatusExtras["handR"] = () => Diag.Q(VRInput.R.Tracked ? Diag.V(VRInput.R.Pos) : "untracked");
            Diag.StatusExtras["buttons"] = () => Diag.Q(Buttons(VRInput.L) + " | " + Buttons(VRInput.R));
        }

        static string Buttons(Hand h)
        {
            return string.Format("trig={0:0.0} grip={1:0.0} stick=({2:0.0},{3:0.0}){4}{5}{6}{7}", h.Trigger, h.Grip, h.Stick.x, h.Stick.y,
                h.A ? " A" : "", h.B ? " B" : "", h.StickClick ? " SC" : "", h.Menu ? " M" : "");
        }

        float m_rcSince = -1f;

        /// Hold the right stick click for 1 s: recenter the view (the game never sees that button). Not in a Physical fight
        /// (playtest 3, 2026-10-18): there a held stick aims the weapon ability (right-handed) or the artefact (left-handed).
        void RecenterGesture()
        {
            Hand r = VRInput.R;
            if (r.StickDown) m_rcSince = Time.unscaledTime;
            if (!r.StickClick || Physical.Live) { m_rcSince = -1f; return; }
            if (VRInput.L.StickClick) { m_rcSince = -1f; return; }   // both sticks: WiVRn's own window on the headset
            if (m_rcSince >= 0f && Time.unscaledTime - m_rcSince > 1f)
            {
                m_rcSince = -1f;
                if (VRFade.I != null) VRFade.I.FadeSnap(() => VRRig.I.Recenter(), "recenter button");
                Haptics.Pulse(false, 0.5f, 0.1f);
            }
        }

        float m_appliedScale = -1f;

        /// Settings.RenderScale -> eye texture size (re-applied when the config file changes).
        void ApplyRenderScale()
        {
            float want = Mathf.Clamp(ScaleOverride > 0f ? ScaleOverride : Settings.RenderScale, 0.5f, 2f);
            if (Mathf.Approximately(want, m_appliedScale)) return;
            m_appliedScale = want;
            XRSettings.eyeTextureResolutionScale = want;
            Plugin.L.LogInfo("render scale " + want.ToString("0.00") + " -> eye " + XRSettings.eyeTextureWidth + "x" + XRSettings.eyeTextureHeight);
        }

        void Update()
        {
            Settings.Poll();
            if (!XRSettings.enabled) return;
            ApplyRenderScale();
            VRInput.Update();
            HandWorld.Tick();
            Haptics.Update(VRInput.VRSystem);
            ControllerGeometry.Update(VRInput.VRSystem);
            RecenterGesture();
            PlayerInfo.Update();
        }
    }
}

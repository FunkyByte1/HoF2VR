using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// `camtime N [ms]`: for the next N frames, each camera's cull + draw time (onPreCull -> onPostRender, main thread), and a
    /// line for every frame whose cameras took at least ms (default 6) together, naming them; then a summary per camera
    /// (playtest 3: a render spike every 0.5 s at the table and the map). Diagnosis only: the hooks stay idle when off.
    internal static class CamTimes
    {
        static int s_left, s_frame = -1, s_frames;
        static float s_min = 6f;
        static bool s_hooked;
        static readonly Dictionary<Camera, long> s_start = new Dictionary<Camera, long>();
        static readonly List<KeyValuePair<string, double>> s_now = new List<KeyValuePair<string, double>>();
        static readonly Dictionary<string, double> s_sum = new Dictionary<string, double>();
        static readonly Dictionary<string, int> s_count = new Dictionary<string, int>();
        static readonly Dictionary<string, double> s_max = new Dictionary<string, double>();
        static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        public static void Register()
        {
            Diag.Commands["scans"] = a =>
            {
                if (a.Length > 2 && a[1] == "legacy") TableHud.LegacyScans = a[2] == "1";
                return "scans: " + (TableHud.LegacyScans ? "legacy (whole-world searches every 0.2-1 s)" : "registered (a search per scene load)");
            };
            Diag.Commands["camtime"] = a =>
            {
                int n = a.Length > 1 ? int.Parse(a[1], CultureInfo.InvariantCulture) : 120;
                if (a.Length > 2) s_min = float.Parse(a[2], CultureInfo.InvariantCulture);
                if (!s_hooked)
                {
                    s_hooked = true;
                    Camera.onPreCull += Pre;
                    Camera.onPostRender += Post;
                }
                s_sum.Clear(); s_count.Clear(); s_max.Clear(); s_now.Clear();
                s_frames = 0;
                s_frame = Time.frameCount;
                s_left = n;
                return "timing the cameras for " + n + " frames (frames of >= " + s_min.ToString("0.0", CultureInfo.InvariantCulture) + " ms logged)";
            };
        }

        static void Pre(Camera c)
        {
            if (s_left <= 0 || c == null) return;
            Roll();
            s_start[c] = Stopwatch.GetTimestamp();
        }

        static void Post(Camera c)
        {
            if (s_left <= 0 || c == null) return;
            long t0;
            if (!s_start.TryGetValue(c, out t0) || t0 == 0) return;
            s_start[c] = 0;
            double ms = (Stopwatch.GetTimestamp() - t0) * TickMs;
            string name = c.name + (c.targetTexture != null ? "->" + c.targetTexture.name : "") + (c.stereoEnabled ? "[" + c.stereoActiveEye + "]" : "");
            s_now.Add(new KeyValuePair<string, double>(name, ms));
            double v;
            s_sum[name] = (s_sum.TryGetValue(name, out v) ? v : 0) + ms;
            int k;
            s_count[name] = (s_count.TryGetValue(name, out k) ? k : 0) + 1;
            s_max[name] = s_max.TryGetValue(name, out v) ? System.Math.Max(v, ms) : ms;
        }

        /// A new frame: the last one's line, and the summary when done.
        static void Roll()
        {
            if (Time.frameCount == s_frame) return;
            double tot = 0;
            foreach (var kv in s_now) tot += kv.Value;
            if (tot >= s_min)
            {
                var sb = new StringBuilder();
                sb.AppendFormat(CultureInfo.InvariantCulture, "camtime f{0} {1}: {2:0.0} ms in {3} renders:", s_frame, Diag.Clock(), tot, s_now.Count);
                foreach (var kv in s_now) sb.AppendFormat(CultureInfo.InvariantCulture, " {0} {1:0.0}", kv.Key, kv.Value);
                Plugin.L.LogInfo(sb.ToString());
            }
            s_now.Clear();
            s_frame = Time.frameCount;
            s_frames++;
            if (--s_left > 0) return;
            var r = new StringBuilder();
            r.AppendFormat(CultureInfo.InvariantCulture, "camtime summary over {0} frames ({1}):", s_frames, GameWatch.State);
            foreach (var kv in s_sum)
                r.AppendFormat(CultureInfo.InvariantCulture, "\n  {0}: {1} renders, avg {2:0.00} ms, max {3:0.0} ms, {4:0.00} ms/frame", kv.Key, s_count[kv.Key], kv.Value / s_count[kv.Key], s_max[kv.Key], kv.Value / s_frames);
            Plugin.L.LogInfo(r.ToString());
        }
    }
}

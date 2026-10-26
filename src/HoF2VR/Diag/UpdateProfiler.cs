using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace HoF2VR
{
    /// `profile on [coroutines] | off | top`: a script profiler for the main-thread freezes (user 2026-10-11: "super laggy";
    /// the session log had ~15 freezes of >= 40 ms a minute, all in the script phase Update..LateUpdate). Times every
    /// Update / LateUpdate / FixedUpdate of the game's and the mod's MonoBehaviours (and, with `coroutines`, every
    /// coroutine step: the compiler's IEnumerator.MoveNext) through Harmony, per frame; PerfMonitor appends the top
    /// entries to each "perf stall" line. Test and diagnosis only (patching takes seconds; ~1000 patched methods).
    internal static class UpdateProfiler
    {
        public static bool On;
        static Harmony s_h;
        static readonly Dictionary<MethodBase, int> s_ids = new Dictionary<MethodBase, int>();
        static readonly List<string> s_names = new List<string>();
        static long[] s_ticks = new long[8192];
        static int[] s_calls = new int[8192];
        static readonly List<int> s_touched = new List<int>();
        static int s_frame = -1;
        public static int Patched;
        static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        public static void Register()
        {
            Diag.Commands["profile"] = a =>
            {
                string what = a.Length > 1 ? a[1] : "info";
                if (what == "on") return Enable(a.Length > 2 && a[2] == "coroutines");
                if (what == "off") { On = false; return "profile off (patches stay, idle)"; }
                if (what == "top") return Top(10);
                // `profile alloc [coroutines]`: from now on also the managed heap's growth per method (statistical: the heap
                // takes new blocks while some method runs); `profile allocs [n]`: the window's report -> log
                if (what == "alloc")
                {
                    string r = On ? "profile already on" : Enable(a.Length > 2 && a[2] == "coroutines");
                    StartAllocWindow();
                    return r + " | alloc window started";
                }
                if (what == "allocs") return AllocReport(a.Length > 2 ? int.Parse(a[2]) : 25);
                // `profile window`: from now on each method's time is summed too; `profile times [n]`: the window's top n by
                // their average per frame -> log (playtest 3, the map's 6.6 ms "logic")
                if (what == "window")
                {
                    string r = On ? "profile on" : Enable(false);
                    Array.Clear(s_win, 0, s_win.Length); Array.Clear(s_winCalls, 0, s_winCalls.Length);
                    s_winFrame0 = Time.frameCount; s_winT0 = Time.unscaledTime; s_winOn = true;
                    return r + " | time window started";
                }
                if (what == "times") return TimesReport(a.Length > 2 ? int.Parse(a[2]) : 20);
                // `profile add Type.Method [Type.Method ...]`: time these too (any assembly, any visibility)
                if (what == "add") return Add(a.Skip(2).ToArray());
                return "profile " + (On ? "on" : "off") + ", " + Patched + " methods patched";
            };
        }

        static string Enable(bool coroutines)
        {
            if (s_h == null) s_h = new Harmony("flat2vr.hof2vr.profiler");
            var pre = new HarmonyMethod(typeof(UpdateProfiler), nameof(Pre));
            var post = new HarmonyMethod(typeof(UpdateProfiler), nameof(Post));
            var sw = Stopwatch.StartNew();
            int added = 0, failed = 0;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = asm.GetName().Name;
                if (n != "Assembly-CSharp" && n != "Assembly-CSharp-firstpass" && n != "HoF2VR") continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (Type t in types)
                {
                    if (t == null || t.IsGenericTypeDefinition || t.ContainsGenericParameters) continue;
                    if (t == typeof(UpdateProfiler) || t == typeof(PerfMonitor)) continue;
                    var methods = new List<MethodInfo>();
                    if (typeof(MonoBehaviour).IsAssignableFrom(t))
                        foreach (string mn in new[] { "Update", "LateUpdate", "FixedUpdate" })
                        {
                            MethodInfo m = t.GetMethod(mn, F, null, Type.EmptyTypes, null);
                            if (m != null) methods.Add(m);
                        }
                    if (coroutines && t.Name.StartsWith("<") && typeof(IEnumerator).IsAssignableFrom(t))
                    {
                        MethodInfo m = t.GetMethod("MoveNext", F, null, Type.EmptyTypes, null);
                        if (m != null) methods.Add(m);
                    }
                    foreach (MethodInfo m in methods)
                    {
                        if (m.IsAbstract || s_ids.ContainsKey(m) || s_names.Count >= s_ticks.Length) continue;
                        try
                        {
                            if (m.GetMethodBody() == null) continue;
                            string name = (t.DeclaringType != null ? t.DeclaringType.Name + "/" : "") + t.Name + "." + m.Name;
                            if (n == "HoF2VR") s_modIds.Add(s_names.Count);
                            s_ids[m] = s_names.Count;
                            s_names.Add(name);
                            s_h.Patch(m, pre, post);
                            added++;
                        }
                        catch (Exception) { failed++; }
                    }
                }
            }
            Patched += added;
            On = true;
            string r = "profile on: " + added + " methods patched (" + failed + " failed) in " + sw.ElapsedMilliseconds + " ms, total " + Patched;
            Plugin.L.LogInfo(r);
            return r;
        }

        static string Add(string[] specs)
        {
            if (s_h == null) s_h = new Harmony("flat2vr.hof2vr.profiler");
            var pre = new HarmonyMethod(typeof(UpdateProfiler), nameof(Pre));
            var post = new HarmonyMethod(typeof(UpdateProfiler), nameof(Post));
            var sb = new StringBuilder();
            foreach (string spec in specs)
            {
                int dot = spec.LastIndexOf('.');
                if (dot <= 0) { sb.Append(" ?" + spec); continue; }
                string tn = spec.Substring(0, dot), mn = spec.Substring(dot + 1);
                int n = 0;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); } catch (Exception) { continue; }
                    foreach (Type t in types)
                    {
                        if (t == null || (t.Name != tn && t.FullName != tn)) continue;
                        foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (m.Name != mn || m.IsAbstract || m.ContainsGenericParameters || s_ids.ContainsKey(m)) continue;
                            try
                            {
                                if (m.GetMethodBody() == null) continue;
                                s_ids[m] = s_names.Count;
                                s_names.Add(t.Name + "." + m.Name + (m.GetParameters().Length > 0 ? "(" + m.GetParameters().Length + ")" : ""));
                                s_h.Patch(m, pre, post);
                                n++;
                                Patched++;
                            }
                            catch (Exception e) { sb.Append(" !" + spec + ":" + e.Message); }
                        }
                    }
                }
                sb.Append(" " + spec + "=" + n);
            }
            On = true;
            return "added" + sb;
        }

        public struct St { public long T, M; }

        static void Pre(out St __state)
        {
            __state = default(St);
            if (!On) return;
            __state.T = Stopwatch.GetTimestamp();
            __state.M = s_alloc ? GC.GetTotalMemory(false) : -1L;   // (a call begun before the window opened is not counted)
        }

        static void Post(MethodBase __originalMethod, St __state)
        {
            if (__state.T == 0L) return;
            long dt = Stopwatch.GetTimestamp() - __state.T;
            long dm = s_alloc && __state.M >= 0L ? GC.GetTotalMemory(false) - __state.M : 0L;
            int id;
            if (!s_ids.TryGetValue(__originalMethod, out id)) return;
            int f = Time.frameCount;
            if (f != s_frame)
            {
                foreach (int i in s_touched) { s_ticks[i] = 0; s_calls[i] = 0; }
                s_touched.Clear();
                s_frame = f;
                if (s_alloc) SampleFrame();
            }
            if (s_calls[id] == 0) s_touched.Add(id);
            s_ticks[id] += dt;
            s_calls[id]++;
            if (dm > 0) s_bytes[id] += dm;
            if (s_winOn) { s_win[id] += dt; s_winCalls[id]++; }
        }

        static bool s_winOn;
        static readonly long[] s_win = new long[8192];
        static readonly int[] s_winCalls = new int[8192];
        static int s_winFrame0;
        static float s_winT0;

        static string TimesReport(int n)
        {
            if (!s_winOn) return "no time window (profile window)";
            int frames = Mathf.Max(1, Time.frameCount - s_winFrame0);
            double sum = 0, mod = 0;
            for (int i = 0; i < s_names.Count; i++) { sum += s_win[i] * TickMs; if (IsMod(i)) mod += s_win[i] * TickMs; }
            var sb = new StringBuilder();
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "profile times: {0} frames {1:0.0} s, state {2}: Updates {3:0.00} ms/frame, of them the mod's {4:0.00}",
                frames, Time.unscaledTime - s_winT0, GameWatch.State, sum / frames, mod / frames);
            foreach (int i in Enumerable.Range(0, s_names.Count).Where(i => s_win[i] > 0).OrderByDescending(i => s_win[i]).Take(n))
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "\n  {0,7:0.000} ms/frame {1,6:0.0} calls/frame {2}{3}", s_win[i] * TickMs / frames, s_winCalls[i] / (double)frames, IsMod(i) ? "[mod] " : "", s_names[i]);
            Plugin.L.LogInfo(sb.ToString());
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00} ms/frame in Updates (the mod's {1:0.00}) over {2} frames - see log", sum / frames, mod / frames, frames);
        }

        // ---- the allocation window (2026-10-13, the table's GC stalls part 2) ----------------------------------------
        static bool s_alloc;
        static readonly long[] s_bytes = new long[8192];
        static long s_lastMem, s_grown;
        static float s_allocStart;
        static int s_allocGc, s_allocFrames;

        static void StartAllocWindow()
        {
            Array.Clear(s_bytes, 0, s_bytes.Length);
            s_lastMem = GC.GetTotalMemory(false);
            s_grown = 0;
            s_allocStart = Time.unscaledTime;
            s_allocGc = GC.CollectionCount(0);
            s_allocFrames = 0;
            s_alloc = true;
        }

        /// Once a frame: the heap's growth since the last frame (a collection's drop is not counted).
        static void SampleFrame()
        {
            long m = GC.GetTotalMemory(false);
            if (m > s_lastMem) s_grown += m - s_lastMem;
            s_lastMem = m;
            s_allocFrames++;
        }

        static string AllocReport(int n)
        {
            if (!s_alloc) return "no alloc window (profile alloc)";
            float secs = Mathf.Max(0.01f, Time.unscaledTime - s_allocStart);
            long attributed = 0, mod = 0;
            for (int i = 0; i < s_names.Count; i++)
            {
                attributed += s_bytes[i];
                if (IsMod(i)) mod += s_bytes[i];
            }
            var sb = new StringBuilder();
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                "alloc window {0:0.0} s, {1} frames, {2} collections, state {3}: the heap grew {4:0.0} MB ({5:0} KB/s, {6:0.0} KB/frame); in Update/LateUpdate methods {7:0.0} MB, of them the mod's {8:0.0} MB; elsewhere (rendering, coroutines, physics) {9:0.0} MB",
                secs, s_allocFrames, GC.CollectionCount(0) - s_allocGc, GameWatch.State, s_grown / 1048576.0, s_grown / 1024.0 / secs, s_allocFrames > 0 ? s_grown / 1024.0 / s_allocFrames : 0,
                attributed / 1048576.0, mod / 1048576.0, Math.Max(0, s_grown - attributed) / 1048576.0);
            foreach (int i in Enumerable.Range(0, s_names.Count).Where(i => s_bytes[i] > 0).OrderByDescending(i => s_bytes[i]).Take(n))
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "\n  {0,8:0.0} KB/s {1}{2}", s_bytes[i] / 1024.0 / secs, IsMod(i) ? "[mod] " : "", s_names[i]);
            Plugin.L.LogInfo(sb.ToString());
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0} KB/s over {1:0} s (the mod's Updates {2:0} KB/s) - see log", s_grown / 1024.0 / secs, secs, mod / 1024.0 / secs);
        }

        static readonly HashSet<int> s_modIds = new HashSet<int>();
        static bool IsMod(int i) => s_modIds.Contains(i);

        /// The current frame's biggest entries ("Type.Update 12.3 ms x4 | ...") and their sum.
        public static string Top(int n)
        {
            if (!On || s_frame != Time.frameCount || s_touched.Count == 0) return "";
            double sum = 0;
            foreach (int i in s_touched) sum += s_ticks[i] * TickMs;
            var sb = new StringBuilder();
            sb.AppendFormat("scripts {0:0.0} ms in {1}:", sum, s_touched.Count);
            foreach (int i in s_touched.OrderByDescending(x => s_ticks[x]).Take(n))
            {
                double ms = s_ticks[i] * TickMs;
                if (ms < 0.5) break;
                sb.AppendFormat(" {0} {1:0.0}{2}", s_names[i], ms, s_calls[i] > 1 ? "x" + s_calls[i] : "");
            }
            return sb.ToString();
        }
    }
}

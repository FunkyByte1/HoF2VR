using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace HoF2VR
{
    /// The bug mark's voice memo, recorded inside the game's own process through libpulse (the PulseAudio client library
    /// every Steam runtime ships; PipeWire's pulse server answers it).
    /// Why not `pactl` / `parecord` (until 2026-10-17): Steam starts the game inside its Linux runtime container
    /// (SteamLinuxRuntime_soldier/_v2-entry-point -> scout-on-soldier-entry-point-v2, steamapps/../logs/gameprocess_log.txt).
    /// The container's /usr has no pactl or parecord; the host's (/run/host/usr/bin) cannot run there (its libraries are
    /// not in the container). So every mark of the user's 2026-10-17 session said "no headset microphone" while
    /// wivrn.source existed. libpulse-simple.so.0 / libpulse.so.0 are in the scout and soldier runtimes and on the host,
    /// and the container forwards the pulse socket (PULSE_SERVER), so the same calls work in and out of it.
    /// Everything runs on a background thread (no process is forked from the game: a fork of a 4 GB process alone took
    /// tens of milliseconds of the frame).
    internal sealed class PulseMic
    {
        const string Lib = "libpulse.so.0", Simple = "libpulse-simple.so.0";
        const int SampleS16LE = 3, StreamRecord = 2, Rate = 48000;

        [StructLayout(LayoutKind.Sequential)]
        struct SampleSpec { public int Format; public uint Rate; public byte Channels; }

        [DllImport(Simple)] static extern IntPtr pa_simple_new(string server, string name, int dir, string dev, string streamName, ref SampleSpec ss, IntPtr map, IntPtr attr, out int error);
        [DllImport(Simple)] static extern int pa_simple_read(IntPtr s, byte[] data, UIntPtr bytes, out int error);
        [DllImport(Simple)] static extern void pa_simple_free(IntPtr s);
        [DllImport(Lib)] static extern IntPtr pa_strerror(int error);
        [DllImport(Lib)] static extern IntPtr pa_mainloop_new();
        [DllImport(Lib)] static extern void pa_mainloop_free(IntPtr m);
        [DllImport(Lib)] static extern IntPtr pa_mainloop_get_api(IntPtr m);
        [DllImport(Lib)] static extern int pa_mainloop_iterate(IntPtr m, int block, IntPtr retval);
        [DllImport(Lib)] static extern IntPtr pa_context_new(IntPtr api, string name);
        [DllImport(Lib)] static extern int pa_context_connect(IntPtr c, string server, int flags, IntPtr api);
        [DllImport(Lib)] static extern int pa_context_get_state(IntPtr c);
        [DllImport(Lib)] static extern void pa_context_disconnect(IntPtr c);
        [DllImport(Lib)] static extern void pa_context_unref(IntPtr c);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SourceInfoCb(IntPtr c, IntPtr info, int eol, IntPtr userdata);
        [DllImport(Lib)] static extern IntPtr pa_context_get_source_info_list(IntPtr c, SourceInfoCb cb, IntPtr userdata);
        [DllImport(Lib)] static extern int pa_operation_get_state(IntPtr o);
        [DllImport(Lib)] static extern void pa_operation_unref(IntPtr o);

        // what the main thread reads (set once each by the recording thread)
        public volatile string Source;     // the headset's source, once found
        public volatile string Outcome;    // the end: "8.0 s recorded ..." or why not
        public volatile bool Looked;       // the sources were listed (Source stays null: none of WiVRn's)
        public bool Done => Outcome != null;
        /// A test run's override: record from this source (e.g. a stand-in PipeWire source) instead of WiVRn's.
        public static string ForceSource;

        public static PulseMic Record(string file, float seconds)
        {
            var job = new PulseMic();
            var t = new Thread(() => job.Run(file, seconds)) { IsBackground = true, Name = "hof2vr voice memo" };
            t.Start();
            return job;
        }

        void Run(string file, float seconds)
        {
            try
            {
                string src = ForceSource;
                if (string.IsNullOrEmpty(src)) src = FindHeadsetSource();
                Looked = true;
                if (src == null) { Outcome = "no headset microphone (no WiVRn source: is WiVRn's microphone on?)"; return; }
                Source = src;
                Outcome = Capture(src, file, seconds);
            }
            catch (DllNotFoundException e) { Looked = true; Outcome = "no voice (no libpulse here: " + e.Message + ")"; }
            catch (EntryPointNotFoundException e) { Looked = true; Outcome = "no voice (libpulse too old: " + e.Message + ")"; }
            catch (Exception e) { Looked = true; Outcome = "no voice (" + e.GetType().Name + ": " + e.Message + ")"; }
        }

        /// The first source whose name has "wivrn" in it (WiVRn's microphone: "wivrn.source" with its PipeWire backend,
        /// "WiVRn-mic" with its pulse one), not a monitor.
        static string FindHeadsetSource()
        {
            string found = null;
            IntPtr ml = pa_mainloop_new();
            if (ml == IntPtr.Zero) throw new Exception("pa_mainloop_new failed");
            IntPtr ctx = IntPtr.Zero;
            try
            {
                ctx = pa_context_new(pa_mainloop_get_api(ml), "Hand of Fate 2 VR");
                if (ctx == IntPtr.Zero || pa_context_connect(ctx, null, 0, IntPtr.Zero) < 0) throw new Exception("cannot reach the sound server");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                int st;
                while ((st = pa_context_get_state(ctx)) != 4)   // PA_CONTEXT_READY
                {
                    if (st == 5 || st == 6 || watch.ElapsedMilliseconds > 3000) throw new Exception("the sound server did not answer (state " + st + ")");
                    if (pa_mainloop_iterate(ml, 0, IntPtr.Zero) == 0) Thread.Sleep(5);
                }
                bool end = false;
                SourceInfoCb cb = (c, info, eol, ud) =>
                {
                    if (eol != 0 || info == IntPtr.Zero) { end = true; return; }
                    string name = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(info));   // pa_source_info.name (the first field)
                    if (found == null && name != null && name.IndexOf("wivrn", StringComparison.OrdinalIgnoreCase) >= 0 && !name.EndsWith(".monitor", StringComparison.Ordinal))
                        found = name;
                };
                IntPtr op = pa_context_get_source_info_list(ctx, cb, IntPtr.Zero);
                if (op == IntPtr.Zero) throw new Exception("the source list was refused");
                while (!end && pa_operation_get_state(op) == 0 && watch.ElapsedMilliseconds < 4000)
                    if (pa_mainloop_iterate(ml, 0, IntPtr.Zero) == 0) Thread.Sleep(5);
                pa_operation_unref(op);
                GC.KeepAlive(cb);
            }
            finally
            {
                if (ctx != IntPtr.Zero) { pa_context_disconnect(ctx); pa_context_unref(ctx); }
                pa_mainloop_free(ml);
            }
            return found;
        }

        /// `seconds` of 48 kHz mono 16-bit from the source into a WAV file.
        static string Capture(string src, string file, float seconds)
        {
            var ss = new SampleSpec { Format = SampleS16LE, Rate = Rate, Channels = 1 };
            int err;
            IntPtr s = pa_simple_new(null, "Hand of Fate 2 VR", StreamRecord, src, "bug mark voice memo", ref ss, IntPtr.Zero, IntPtr.Zero, out err);
            if (s == IntPtr.Zero) return "no voice (" + src + ": " + Err(err) + ")";
            int want = (int)(seconds * Rate) * 2, got = 0;
            byte[] pcm = new byte[want];
            byte[] chunk = new byte[Rate / 10 * 2];   // 0.1 s
            try
            {
                while (got < want)
                {
                    int n = Math.Min(chunk.Length, want - got);
                    if (pa_simple_read(s, chunk, (UIntPtr)n, out err) < 0) break;
                    Buffer.BlockCopy(chunk, 0, pcm, got, n);
                    got += n;
                }
            }
            finally { pa_simple_free(s); }
            using (var f = new BinaryWriter(File.Create(file)))
            {
                f.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); f.Write(36 + got);
                f.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                f.Write(16); f.Write((short)1); f.Write((short)1); f.Write(Rate); f.Write(Rate * 2); f.Write((short)2); f.Write((short)16);
                f.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); f.Write(got);
                f.Write(pcm, 0, got);
            }
            return (got / (float)(Rate * 2)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s recorded from " + src + " -> " + file +
                   (got < want ? " (the source stopped: " + Err(err) + ")" : "");
        }

        static string Err(int e)
        {
            try { IntPtr p = pa_strerror(e); return p != IntPtr.Zero ? Marshal.PtrToStringAnsi(p) : "error " + e; }
            catch (Exception) { return "error " + e; }
        }
    }
}

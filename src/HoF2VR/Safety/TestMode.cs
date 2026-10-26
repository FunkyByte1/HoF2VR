using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace HoF2VR
{
    /// Test mode keeps automated test runs away from the player's real save and Steam account.
    /// On ONLY when the test harness launched the game (HOF2VR_TEST=1 in the environment, set by
    /// scripts/run.sh). A leftover <game>/hof2vr_testmode.txt never turns a Steam launch into a test run
    /// (it is ignored and logged; the launcher removes it). Saves then live in <dir>/remote and <dir>/local,
    /// dir = HOF2VR_TEST_DIR (or the flag file's first line, or <game>/hof2vr_test).
    ///
    /// The user always wins: a test instance steps aside for the player. It quits by itself when
    ///  - the Steam launcher / the app-list play wrapper asks for it (<game>/hof2vr_stop_tests, newer than
    ///    this process),
    ///  - the user is in VR: the coordinator's vr-session-watch says so - its marker process `vr-session` runs
    ///    (only for the user's own VR apps on WiVRn; test sessions get `vr-session-test`), or its status file
    ///    ($XDG_RUNTIME_DIR/vr-session/status.json) has "user": true (older versions: "wivrn" among the sources),
    ///  - another VR app is connected to WiVRn's compositor (unless this instance itself runs on WiVRn).
    /// While it runs it keeps <game>/hof2vr_test_running fresh (a heartbeat the launcher waits on).
    internal static class TestMode
    {
        public static bool Enabled;
        public static string Dir;
        public static string GameDir;
        /// A flag file was found, but the harness did not launch the game: normal play.
        public static bool FlagIgnored;
        public const string FlagFile = "hof2vr_testmode.txt", StopFile = "hof2vr_stop_tests", RunningFile = "hof2vr_test_running";

        static DateTime s_startUtc;
        static volatile string s_quitWhy;
        static bool s_quitting;
        static Thread s_watch;
        static bool s_onWivrn;

        public static void Init()
        {
            GameDir = Path.GetDirectoryName(Application.dataPath);
            s_startUtc = DateTime.UtcNow;
            string flag = Path.Combine(GameDir, FlagFile);
            string flagDir = null;
            bool flagExists = File.Exists(flag);
            if (flagExists)
            {
                try { flagDir = File.ReadAllText(flag).Trim(); } catch (Exception) { }
            }
            Enabled = Environment.GetEnvironmentVariable("HOF2VR_TEST") == "1";
            FlagIgnored = flagExists && !Enabled;
            Dir = Environment.GetEnvironmentVariable("HOF2VR_TEST_DIR");
            if (string.IsNullOrEmpty(Dir)) Dir = flagDir;
            if (string.IsNullOrEmpty(Dir)) Dir = Path.Combine(GameDir, "hof2vr_test");
            // HOF2VR_SELFTEST_TESTMODE=<file>: write the decision and end the process at once (before the game
            // loads anything) - lets the harness check the decision for a launch WITHOUT HOF2VR_TEST safely
            string self = Environment.GetEnvironmentVariable("HOF2VR_SELFTEST_TESTMODE");
            if (!string.IsNullOrEmpty(self))
            {
                try { File.WriteAllText(self, "testmode=" + Enabled + " flagfile=" + flagExists + " flagIgnored=" + FlagIgnored + " dir=" + Dir + "\n"); }
                catch (Exception) { }
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
            if (!Enabled) return;
            Directory.CreateDirectory(Remote);
            Directory.CreateDirectory(Local);
            Directory.CreateDirectory(Cmd);
            string rt = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON") ?? "";
            s_onWivrn = rt.IndexOf("wivrn", StringComparison.OrdinalIgnoreCase) >= 0;
            Beat();
            s_watch = new Thread(WatchLoop) { IsBackground = true, Name = "HoF2VR user-first watch" };
            s_watch.Start();
        }

        public static string Remote => Path.Combine(Dir, "remote");
        public static string Local => Path.Combine(Dir, "local");
        public static string Cmd => Path.Combine(Dir, "cmd");

        /// Last result of the background check (diagnostics, `userfirst`).
        public static string LastCheck = "-";

        static void Beat()
        {
            try { File.WriteAllText(Path.Combine(GameDir, RunningFile), System.Diagnostics.Process.GetCurrentProcess().Id + " " + Dir + "\n"); }
            catch (Exception) { }
        }

        static void WatchLoop()
        {
            while (s_quitWhy == null)
            {
                try
                {
                    string why = UserWantsTheMachine(null);
                    LastCheck = why ?? "clear (" + DateTime.Now.ToString("HH:mm:ss") + ")";
                    if (why != null) { s_quitWhy = why; break; }
                    Beat();
                }
                catch (Exception e) { LastCheck = "check failed: " + e.Message; }
                Thread.Sleep(1000);
            }
        }

        /// Why the user needs the machine now (null = they don't). unixFile: a stand-in for /proc/net/unix (tests).
        public static string UserWantsTheMachine(string unixFile)
        {
            string stop = Path.Combine(GameDir, StopFile);
            if (File.Exists(stop) && File.GetLastWriteTimeUtc(stop) > s_startUtc) return "the player's launch asked for the game (" + StopFile + ")";
            if (VrSessionUser()) return "vr-session-watch: the user is in VR";
            // (every other second; the marker process `vr-session` is reflected in the status file's "user" flag -
            // scanning /proc for it allocated ~500 strings a pass: more GC pauses in test runs)
            if ((!s_onWivrn && (++s_procScan % 2) == 0) || unixFile != null)
            {
                int n = WivrnClients(unixFile ?? "/proc/net/unix");
                if (n > 0) return n + " VR app(s) connected to WiVRn";
            }
            return null;
        }

        static int s_procScan;

        /// The coordinator's vr-session-watch status: {"active": bool, "sources": ["wivrn"|"monado"|...], "user": bool,
        /// "test": bool, ...}. HOF2VR_VRSESSION_STATUS points to a stand-in file (tests; the real one belongs to that
        /// service).
        static bool VrSessionUser()
        {
            string path = Environment.GetEnvironmentVariable("HOF2VR_VRSESSION_STATUS");
            if (string.IsNullOrEmpty(path))
            {
                string rt = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
                if (string.IsNullOrEmpty(rt)) return false;
                path = Path.Combine(Path.Combine(rt, "vr-session"), "status.json");
            }
            if (!File.Exists(path)) return false;
            string j = File.ReadAllText(path).Replace(" ", "");
            if (j.IndexOf("\"user\":", StringComparison.Ordinal) >= 0) return j.IndexOf("\"user\":true", StringComparison.Ordinal) >= 0;
            if (s_onWivrn) return false;   // older status files: "wivrn" may be this very instance
            int i = j.IndexOf("\"sources\"", StringComparison.Ordinal);
            if (i < 0) return false;
            int a = j.IndexOf('[', i), b = a >= 0 ? j.IndexOf(']', a) : -1;
            return a >= 0 && b > a && j.Substring(a, b - a).IndexOf("\"wivrn\"", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool ProcessRunning(string comm)
        {
            foreach (string d in Directory.GetDirectories("/proc"))
            {
                string name = Path.GetFileName(d);
                if (name.Length == 0 || name[0] < '0' || name[0] > '9') continue;
                try
                {
                    if (File.ReadAllText(Path.Combine(d, "comm")).Trim() == comm) return true;
                }
                catch (Exception) { }   // gone meanwhile
            }
            return false;
        }

        /// Connected sockets on WiVRn's compositor socket (/proc/net/unix: "Num RefCount Protocol Flags Type St
        /// Inode Path"; St 03 = connected; the listening socket is St 01).
        static int WivrnClients(string file)
        {
            int n = 0;
            using (var r = new StreamReader(file))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (!line.EndsWith("/wivrn/comp_ipc", StringComparison.Ordinal)) continue;
                    string[] f = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length >= 8 && f[5] == "03") n++;
                }
            }
            return n;
        }

        /// Main thread, every frame (Diag.Update): quit when the user needs the machine.
        public static void Tick()
        {
            if (!Enabled || s_quitting || s_quitWhy == null) return;
            s_quitting = true;
            Plugin.L.LogWarning("USER FIRST: " + s_quitWhy + " - this test instance quits now");
            try { File.Delete(Path.Combine(GameDir, RunningFile)); } catch (Exception) { }
            // never hang around: if the normal quit stalls, end the process
            new Thread(() => { Thread.Sleep(6000); System.Diagnostics.Process.GetCurrentProcess().Kill(); }) { IsBackground = true }.Start();
            Application.Quit();
        }

        /// The heartbeat goes when the process ends normally.
        public static void OnQuit()
        {
            if (!Enabled) return;
            try { File.Delete(Path.Combine(GameDir, RunningFile)); } catch (Exception) { }
        }
    }
}

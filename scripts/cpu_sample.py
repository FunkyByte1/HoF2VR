#!/usr/bin/env python3
"""cpu_sample.py [seconds] [state-substring] - per-thread CPU of the game, as ms per frame.
Waits (up to 10 min) until the game state contains the substring (default: any)."""
import json, os, subprocess, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)
H = hof2env.H
secs = float(sys.argv[1]) if len(sys.argv) > 1 else 5
want = sys.argv[2] if len(sys.argv) > 2 else ""
def status():
    try: return json.load(open(H + "/testdata/status.json"))
    except Exception: return {}
t0 = time.time()
while want and want not in status().get("state", "") and time.time() - t0 < 600:
    time.sleep(1)
pid = str(hof2env.game_pids()[0])   # this environment's test instance
def snap():
    d = {}
    for t in os.listdir("/proc/%s/task" % pid):
        try:
            st = open("/proc/%s/task/%s/stat" % (pid, t)).read()
            name = st[st.index("(") + 1:st.rindex(")")]
            f = st[st.rindex(")") + 2:].split()
            d[t] = (name, int(f[11]) + int(f[12]))
        except Exception:
            pass
    return d
s0 = status(); a = snap(); t = time.time()
time.sleep(secs)
s1 = status(); b = snap(); dt = time.time() - t
frames = s1.get("frame", 0) - s0.get("frame", 0)
fps = frames / dt
hz = os.sysconf("SC_CLK_TCK")
print("state %s  fps %.1f  frames %d" % (s1.get("state"), fps, frames))
rows = []
for k, (n, v) in b.items():
    if k in a:
        cpu = (v - a[k][1]) / hz / dt * 100
        if cpu > 1:
            rows.append((cpu, k, n))
tot = 0
for cpu, k, n in sorted(rows, reverse=True)[:8]:
    print("  %-18s %-8s %6.1f%%  %6.2f ms/frame%s" % (n, k, cpu, cpu / 100 * 1000 / max(fps, 1), "  <- main" if k == pid else ""))
try:
    print("  GPU busy %s%%" % open("/sys/class/drm/card1/device/gpu_busy_percent").read().strip())
except Exception:
    pass

#!/usr/bin/env python3
"""Load of the running test environments (docs/two-environments.md), sampled over SECONDS (default 30):
the whole machine's CPU, each environment's game / Monado / xrsim (CPU, GPU time from the DRM fdinfo, VRAM), the
GPU's busy % / VRAM / power, and each game's frame rate (from its status.json). Read-only.

  envload.py [SECONDS] [LABEL]      one line per process and a summary; also appended to logs/envload.log
Run it through `testenv.sh quiet` so heavy background jobs pause meanwhile; outside the Bash sandbox.
"""
import glob
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hof2env  # noqa: E402

HZ = os.sysconf("SC_CLK_TCK")


def read(path, default=""):
    try:
        with open(path) as f:
            return f.read()
    except OSError:
        return default


def card():
    for dev in sorted(glob.glob("/sys/class/drm/card*/device")):
        if read(os.path.join(dev, "vendor")).strip() == "0x1002":
            return dev
    return None


def cpu_total():
    v = list(map(int, read("/proc/stat").splitlines()[0].split()[1:]))
    return sum(v[:8]), v[3] + v[4]


def ticks(pid):
    s = read("/proc/%d/stat" % pid)
    if not s:
        return None
    f = s[s.rindex(")") + 2:].split()
    return int(f[11]) + int(f[12])


def drm(pid):
    """GPU time (ns, gfx + compute) and VRAM (KiB) of a process's DRM clients, from /proc/PID/fdinfo."""
    seen, gpu_ns, vram = set(), 0, 0
    for fi in glob.glob("/proc/%d/fdinfo/*" % pid):
        txt = read(fi)
        if "drm-client-id" not in txt:
            continue
        kv = dict(line.split(":", 1) for line in txt.splitlines() if ":" in line)
        cid = kv.get("drm-client-id", "").strip()
        if cid in seen:
            continue
        seen.add(cid)
        for k in ("drm-engine-gfx", "drm-engine-compute"):
            if k in kv:
                gpu_ns += int(kv[k].split()[0])
        if "drm-memory-vram" in kv:
            vram += int(kv["drm-memory-vram"].split()[0])
    return gpu_ns, vram


def environ(pid):
    raw = read("/proc/%d/environ" % pid)
    return dict(kv.split("=", 1) for kv in raw.split("\0") if "=" in kv)


def procs():
    """(pid, label) of every environment's game, Monado and xrsim daemon."""
    out = []
    for d in os.listdir("/proc"):
        if not d.isdigit():
            continue
        pid = int(d)
        comm = read("/proc/%d/comm" % pid).strip()
        if comm == "Hand of Fate 2.":
            env = environ(pid)
            out.append((pid, "game env %s%s" % (env.get("HOF2VR_ENV", "1"), "" if env.get("HOF2VR_TEST") == "1" else " (USER)")))
        elif comm == "monado-service":
            out.append((pid, "monado env %s" % environ(pid).get("HOF2VR_ENV", "1")))
        elif comm.startswith("python") and "xrsim.py" in read("/proc/%d/cmdline" % pid) and "--daemon" in read("/proc/%d/cmdline" % pid):
            out.append((pid, "xrsim env %s" % environ(pid).get("HOF2VR_ENV", "1")))
    return sorted(out, key=lambda x: x[1])


def frame(pid):
    st = os.path.join(environ(pid).get("HOF2VR_TEST_DIR", ""), "status.json")
    try:
        with open(st) as f:
            return json.load(f).get("frame")
    except (OSError, ValueError):
        return None


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 30.0
    label = sys.argv[2] if len(sys.argv) > 2 else ""
    dev = card()
    hw = glob.glob(os.path.join(dev, "hwmon", "hwmon*"))[0] if dev else None
    ps = procs()
    c0 = cpu_total()
    t0 = {p: ticks(p) for p, _ in ps}
    g0 = {p: drm(p) for p, _ in ps}
    f0 = {p: frame(p) for p, lab in ps if lab.startswith("game")}
    w0 = time.time()
    busy, power = [], []
    while time.time() - w0 < secs:
        if dev:
            try:
                busy.append(int(read(os.path.join(dev, "gpu_busy_percent"), "0")))
                power.append(int(read(os.path.join(hw, "power1_average"), "0")) / 1e6)
            except ValueError:
                pass
        time.sleep(0.1)
    dt = time.time() - w0
    c1 = cpu_total()
    tot = c1[0] - c0[0]
    machine = 100.0 * (tot - (c1[1] - c0[1])) / max(tot, 1)
    ncpu = os.cpu_count()
    lines = []
    tracked = 0.0
    for p, lab in ps:
        t1, (gns1, vram1) = ticks(p), drm(p)
        if t1 is None or t0[p] is None:
            continue
        cpu = 100.0 * (t1 - t0[p]) / HZ / dt          # % of one core
        tracked += cpu
        gpu = 100.0 * (gns1 - g0[p][0]) / (dt * 1e9)   # % of the GPU's time (gfx + compute)
        fps = ""
        if lab.startswith("game") and f0.get(p) is not None:
            f1 = frame(p)
            if f1 is not None:
                fps = "  %.1f fps" % ((f1 - f0[p]) / dt)
        lines.append("  %-16s pid %-8d CPU %5.1f%% of a core  GPU %4.1f%%  VRAM %5.0f MiB%s" % (lab, p, cpu, gpu, vram1 / 1024, fps))
    vram_used = int(read(os.path.join(dev, "mem_info_vram_used"), "0")) / 2**20 if dev else 0
    vram_total = int(read(os.path.join(dev, "mem_info_vram_total"), "0")) / 2**20 if dev else 0
    mem = dict((l.split(":")[0], int(l.split()[1])) for l in read("/proc/meminfo").splitlines() if l.split()[1:2])
    summary = ("%s %s %.0fs: machine CPU %.1f%% of %d threads (test processes %.1f%% of one core = %.1f threads); "
               "GPU busy avg %.0f%% max %d%%, power avg %.0f W; VRAM %.0f/%.0f MiB; RAM available %.1f GiB"
               % (time.strftime("%H:%M:%S"), label, dt, machine, ncpu, tracked, tracked / 100, sum(busy) / max(len(busy), 1),
                  max(busy or [0]), sum(power) / max(len(power), 1), vram_used, vram_total, mem.get("MemAvailable", 0) / 2**20))
    print(summary)
    print("\n".join(lines))
    with open(os.path.join(hof2env.H, "logs", "envload.log"), "a") as f:
        f.write(summary + "\n" + "\n".join(lines) + "\n")


if __name__ == "__main__":
    main()

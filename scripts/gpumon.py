#!/usr/bin/env python3
"""GPU telemetry from sysfs (read-only; the AMD card is found by vendor 0x1002).

  gpumon.py state                 power profile, performance level, clock range, power cap, live clock
  gpumon.py sample SECONDS [HZ]   sample core clock / busy % / power at HZ (default 20) and print stats

Nothing is written anywhere; LACT owns these settings.
"""
import glob
import os
import statistics
import sys
import time


def card():
    for dev in sorted(glob.glob("/sys/class/drm/card*/device")):
        try:
            if open(os.path.join(dev, "vendor")).read().strip() == "0x1002":
                return dev
        except OSError:
            pass
    raise SystemExit("no AMD card found")


def read(path, default=""):
    try:
        with open(path) as f:
            return f.read()
    except OSError:
        return default


def hwmon(dev):
    h = glob.glob(os.path.join(dev, "hwmon", "hwmon*"))
    return h[0] if h else None


def state():
    dev = card()
    hw = hwmon(dev)
    prof = [l for l in read(os.path.join(dev, "pp_power_profile_mode")).splitlines() if "*" in l]
    od = read(os.path.join(dev, "pp_od_clk_voltage")).split("OD_MCLK")[0].replace("\n", " ").strip()
    sclk = [l.strip() for l in read(os.path.join(dev, "pp_dpm_sclk")).splitlines()]
    out = {
        "card": dev,
        "profile": prof[0].split(":")[0].strip() if prof else "?",
        "perf_level": read(os.path.join(dev, "power_dpm_force_performance_level")).strip(),
        "od": od,
        "dpm_sclk": " | ".join(sclk),
        "power_cap_W": int(read(os.path.join(hw, "power1_cap"), "0")) / 1e6 if hw else None,
        "sclk_MHz": int(read(os.path.join(hw, "freq1_input"), "0")) / 1e6 if hw else None,
        "busy": read(os.path.join(dev, "gpu_busy_percent")).strip(),
    }
    return out


def sample(seconds, hz=20.0):
    dev = card()
    hw = hwmon(dev)
    fclk = os.path.join(hw, "freq1_input")
    fbusy = os.path.join(dev, "gpu_busy_percent")
    fpow = os.path.join(hw, "power1_average")
    clk, busy, pw = [], [], []
    t_end = time.time() + seconds
    period = 1.0 / hz
    nxt = time.time()
    while time.time() < t_end:
        try:
            clk.append(int(read(fclk, "0")) / 1e6)
            busy.append(int(read(fbusy, "0")))
            pw.append(int(read(fpow, "0")) / 1e6)
        except ValueError:
            pass
        nxt += period
        time.sleep(max(0.0, nxt - time.time()))
    if not clk:
        return "no samples"
    q = statistics.quantiles(clk, n=10) if len(clk) >= 10 else [min(clk)] * 9
    bands = [(0, 1000), (1000, 1600), (1600, 2000), (2000, 2300), (2300, 9999)]
    hist = " ".join("%d-%d:%d%%" % (a, b if b < 9999 else 0, round(100 * sum(1 for c in clk if a <= c < b) / len(clk))) for a, b in bands)
    return ("sclk med %.0f p10 %.0f p90 %.0f MHz [%s] | busy avg %.0f%% (100%%: %d%% of samples) | power avg %.0f W | n=%d"
            % (statistics.median(clk), q[0], q[-1], hist.replace("-0:", "+:"), statistics.mean(busy),
               round(100 * sum(1 for b in busy if b >= 99) / len(busy)), statistics.mean(pw), len(clk)))


if __name__ == "__main__":
    a = sys.argv[1:]
    if not a or a[0] == "state":
        for k, v in state().items():
            print("%-12s %s" % (k, v))
    elif a[0] == "sample":
        print(sample(float(a[1]) if len(a) > 1 else 5.0, float(a[2]) if len(a) > 2 else 20.0))
    else:
        raise SystemExit(__doc__)

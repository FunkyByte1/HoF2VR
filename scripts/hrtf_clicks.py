#!/usr/bin/env python3
"""Clicks and dropouts in a tone-probe recording (the scene-load check; scripts/hrtf_test.py stage loadcheck).

  hrtf_clicks.py <recording.wav> <meta.json> [--out REPORT.json]

The recording holds only the mod's head-locked 1 kHz probe (`hrtf probe 60 0 2 0 hrtf|pan tone follow`; the game's own
sounds muted at their sources and at the mixer), so each ear is a steady sine. Every 2 ms window (two periods at 48 kHz)
the sine is fitted by least squares:
  dropout  the tone below half its median amplitude (-6 dB)
  glitch   what is left after the fit above -40 dB of the tone (a click, a discontinuity, a sound that leaked)
Windows are joined into events (gaps under 10 ms). meta.json (hrtf_test.py) gives the wall-clock time the recording
started and the phases and loads (the mod's `perf hold` windows), so every event is placed: in a load (+-1 s) or not.
Needs numpy (system python3 -I).
"""
import json
import math
import sys

import numpy as np

sys.path.insert(0, __file__.rsplit("/", 1)[0])
import hrtf_analyze as A  # noqa: E402


def analyse(x, rate, f=1000.0, win=96):
    n = (len(x) // win) * win
    x = x[:n].reshape(-1, win)
    k = np.arange(win)
    s, c = np.sin(2 * math.pi * f * k / rate), np.cos(2 * math.pi * f * k / rate)
    a = (x * s).sum(axis=1) * 2.0 / win
    b = (x * c).sum(axis=1) * 2.0 / win
    fit = a[:, None] * s[None, :] + b[:, None] * c[None, :]
    res = np.sqrt(((x - fit) ** 2).mean(axis=1))
    amp = np.sqrt(a * a + b * b)
    return amp, res


def events(flags, t, gap_win):
    out, i = [], 0
    idx = np.nonzero(flags)[0]
    while i < len(idx):
        j = i
        while j + 1 < len(idx) and idx[j + 1] - idx[j] <= gap_win:
            j += 1
        out.append((idx[i], idx[j]))
        i = j + 1
    return out


def main():
    a = sys.argv[1:]
    if len(a) < 2:
        raise SystemExit(__doc__)
    rate, x = A.read_wav(a[0])
    meta = json.load(open(a[1]))
    win = 96 * rate // 48000
    t_win = win / rate
    rep = {"wav": a[0], "seconds": len(x) / rate, "ears": {}}
    t0 = meta.get("rec_wall0", 0.0)
    loads = meta.get("loads", [])        # [name, wall_start, wall_end]
    phases = meta.get("phases", [])      # [name, wall_t]

    def where(wall):
        inside = [l[0] for l in loads if l[1] - 1.0 <= wall <= l[2] + 1.0]
        ph = [p[0] for p in phases if p[1] <= wall]
        return (inside[0] if inside else ""), (ph[-1] if ph else "")

    all_ev = []
    for ch, name in ((0, "L"), (1, "R")):
        amp, res = analyse(x[:, ch], rate, 1000.0 * rate / 48000.0, win)
        on = amp > 0.05 * (np.max(amp) if len(amp) else 1)
        if not on.any():
            rep["ears"][name] = {"tone": False}
            continue
        first, last = np.nonzero(on)[0][0] + int(0.5 / t_win), np.nonzero(on)[0][-1] - int(0.5 / t_win)
        med = float(np.median(amp[first:last]))
        drop = np.zeros_like(on)
        glit = np.zeros_like(on)
        drop[first:last] = amp[first:last] < 0.5 * med
        glit[first:last] = res[first:last] > 0.01 * med / math.sqrt(2)
        evs = []
        for kind, fl in (("dropout", drop), ("glitch", glit)):
            for s, e in events(fl, None, int(0.010 / t_win)):
                ts = s * t_win
                wall = t0 + ts
                load, ph = where(wall)
                worst = float(np.max(res[s:e + 1])) if kind == "glitch" else float(np.min(amp[s:e + 1]))
                evs.append({"ear": name, "kind": kind, "t": round(ts, 3), "ms": round((e - s + 1) * t_win * 1000, 1), "load": load, "phase": ph,
                            "level_db": round(20 * math.log10(max(worst, 1e-12) / (med / math.sqrt(2) if kind == "glitch" else med)), 1)})
        rep["ears"][name] = {"tone": True, "tone_dbfs": round(20 * math.log10(med / math.sqrt(2)), 1), "seconds_checked": round((last - first) * t_win, 1),
                             "dropouts": sum(1 for e in evs if e["kind"] == "dropout"), "glitches": sum(1 for e in evs if e["kind"] == "glitch")}
        all_ev += evs
    all_ev.sort(key=lambda e: e["t"])
    rep["events"] = all_ev
    rep["in_loads"] = sum(1 for e in all_ev if e["load"])
    for l in loads:
        rep.setdefault("per_load", []).append({"load": l[0], "seconds": round(l[2] - l[1], 1),
                                               "events": sum(1 for e in all_ev if e["load"] == l[0])})
    print("%s: %.0f s, tone %s; %d events (%d dropouts, %d glitches), %d of them in a load (+-1 s)" % (
        a[0].rsplit("/", 1)[-1], rep["seconds"], " / ".join("%s %s dBFS" % (k, v.get("tone_dbfs")) for k, v in rep["ears"].items()),
        len(all_ev), sum(1 for e in all_ev if e["kind"] == "dropout"), sum(1 for e in all_ev if e["kind"] == "glitch"), rep["in_loads"]))
    for e in all_ev[:40]:
        print("  %8.3f s  %-7s %-7s %6.1f ms  %+6.1f dB  %s  %s" % (e["t"], e["ear"], e["kind"], e["ms"], e["level_db"], e["load"] or "-", e["phase"]))
    for p in rep.get("per_load", []):
        print("  load %-40s %5.1f s: %d events" % (p["load"][:40], p["seconds"], p["events"]))
    if "--out" in a:
        json.dump(rep, open(a[a.index("--out") + 1], "w"), indent=1)


if __name__ == "__main__":
    main()

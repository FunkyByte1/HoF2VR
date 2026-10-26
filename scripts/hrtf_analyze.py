#!/usr/bin/env python3
"""Measure an HRTF test recording without ears (docs/hrtf-plan.md §6).

  hrtf_analyze.py <recording.wav> <schedule.json> [--out REPORT.json] [--table]

The schedule is what the mod wrote (VR/HrtfProbe.cs: `hrtf sweep`, `hrtf say`, `hrtf enemy`): a 2D sync chirp and pink-noise
bursts (fixed seed) at known DSP times. The recording is the game's output from the test null sink (scripts/hrtf_sink.sh,
pw-record). Everything is aligned on the chirp; then per burst:
  rms_l / rms_r      level of each ear, dBFS
  ild                broadband level difference R - L, dB (+ = right louder)
  ild_lo / ild_hi    the same in 0.3-1.5 kHz and 4-12 kHz
  itd_ms             interaural time difference from the 0.2-1.5 kHz cross-correlation within +-1 ms (+ = right ear first)
  tilt               spectral tilt, 4-12 kHz vs 0.3-1.5 kHz energy of both ears together, dB (front > back for a real HRTF)
  onset_ms           where the burst starts against the schedule (sweep: PlayScheduled time; say/enemy: the PlayOneShot call)
  clip / dropout     samples at full scale; runs of exact zeros >= 2 ms inside the burst
Sweep checks (the plan's pass rules): HRTF pass - every burst audible, ITD sign = side for |az| >= 30, |ITD| 0.45-0.85 ms at
+-90 and < 0.1 ms at 0 / 180, high-band ILD >= 8 dB at +-90, mirror symmetry of the ILD <= 2 dB, front/back tilt >= 2 dB
(30/150, 60/120 both sides), no clipping or dropouts, onset HRTF - pan <= 3 ms; panning control - |ITD| <= 1 sample, front/back
tilt difference <= 0.5 dB. say/enemy: per-file medians and the ITD sign against the source's side.
Exit 0 when every check passes, 1 if one fails. Needs numpy (system python3 -I).
"""
import json
import math
import struct
import sys

import numpy as np


# ------------------------------------------------------------------------------------------------ reading
def read_wav(path):
    """(rate, float64 array [n, ch]) from a RIFF/WAVE file: PCM 16/24/32 or float 32, plain or extensible."""
    d = open(path, "rb").read()
    if d[:4] != b"RIFF" or d[8:12] != b"WAVE":
        raise SystemExit("%s: not a WAVE file" % path)
    p, fmt, data = 12, None, None
    while p + 8 <= len(d):
        cid, size = d[p:p + 4], struct.unpack_from("<I", d, p + 4)[0]
        body = d[p + 8:p + 8 + size]
        if cid == b"fmt ":
            fmt = body
        elif cid == b"data":
            data = body if size != 0xFFFFFFFF and size > 0 else d[p + 8:]
            if size == 0:   # a header not finalised (recorder killed): take the rest of the file
                data = d[p + 8:]
        p += 8 + size + (size & 1)
        if size == 0 or size == 0xFFFFFFFF:
            break
    if fmt is None or data is None:
        raise SystemExit("%s: no fmt/data chunk" % path)
    tag, ch, rate = struct.unpack_from("<HHI", fmt, 0)
    bits = struct.unpack_from("<H", fmt, 14)[0]
    if tag == 0xFFFE and len(fmt) >= 26:
        tag = struct.unpack_from("<H", fmt, 24)[0]
    n = len(data) // (ch * bits // 8)
    data = data[:n * ch * bits // 8]
    if tag == 3 and bits == 32:
        x = np.frombuffer(data, "<f4").astype(np.float64)
    elif tag == 1 and bits == 32:
        x = np.frombuffer(data, "<i4").astype(np.float64) / 2.0 ** 31
    elif tag == 1 and bits == 16:
        x = np.frombuffer(data, "<i2").astype(np.float64) / 2.0 ** 15
    elif tag == 1 and bits == 24:
        b = np.frombuffer(data, np.uint8).reshape(-1, 3).astype(np.int32)
        v = b[:, 0] | (b[:, 1] << 8) | (b[:, 2] << 16)
        v = np.where(v >= 1 << 23, v - (1 << 24), v)
        x = v.astype(np.float64) / 2.0 ** 23
    else:
        raise SystemExit("%s: unsupported format tag %d, %d bits" % (path, tag, bits))
    return rate, x.reshape(-1, ch)


# ------------------------------------------------------------------------------------------------ signals
def chirp(rate, secs, f0, f1):
    """The mod's sync chirp, sample for sample (HrtfProbe.Chirp)."""
    n = int(rate * secs)
    k = math.log(f1 / f0)
    t = np.arange(n) / rate
    f = f0 * np.exp(k * t / secs)
    ph = np.cumsum(2 * math.pi * f / rate)
    d = 0.3 * np.sin(ph)
    r = min(rate // 500, n // 2)
    w = 0.5 - 0.5 * np.cos(math.pi * np.arange(r) / r)
    d[:r] *= w
    d[n - r:] *= w[::-1]
    return d


def xcorr_find(sig, tpl, lo, hi):
    """Index in sig[lo:hi] where tpl matches best (normalised cross-correlation)."""
    lo, hi = max(0, lo), min(len(sig), hi)
    seg = sig[lo:hi]
    if len(seg) < len(tpl):
        return None, 0.0
    nfft = 1 << int(math.ceil(math.log2(len(seg) + len(tpl))))
    c = np.fft.irfft(np.fft.rfft(seg, nfft) * np.conj(np.fft.rfft(tpl, nfft)), nfft)[:len(seg) - len(tpl) + 1]
    te = float(np.sum(tpl ** 2))
    win = np.convolve(seg ** 2, np.ones(len(tpl)), "valid")
    ok = win > 1e-6 * te          # (silence: the FFT's round-off over nothing is no match)
    nc = np.zeros_like(c)
    nc[ok] = c[ok] / np.sqrt(win[ok] * te)
    i = int(np.argmax(nc))
    return lo + i, float(nc[i])


def band(x, rate, lo, hi):
    X = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1.0 / rate)
    X[(f < lo) | (f > hi)] = 0
    return np.fft.irfft(X, len(x))


def band_energy(x, rate, lo, hi):
    w = np.hanning(len(x))
    X = np.fft.rfft(x * w)
    f = np.fft.rfftfreq(len(x), 1.0 / rate)
    return float(np.sum(np.abs(X[(f >= lo) & (f <= hi)]) ** 2)) + 1e-30


THIRD_OCT = [500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000]


def bands_db(x, rate):
    """Third-octave band levels (dB) of one channel, 500 Hz - 16 kHz."""
    w = np.hanning(len(x))
    P = np.abs(np.fft.rfft(x * w)) ** 2
    f = np.fft.rfftfreq(len(x), 1.0 / rate)
    k = 2 ** (1 / 6.0)
    return [db(float(np.sum(P[(f >= c / k) & (f < c * k)])) + 1e-30) for c in THIRD_OCT]


def shape_diff(a, b):
    """RMS (dB) of the band-level difference a - b once the overall level difference is taken out."""
    d = np.array(a) - np.array(b)
    return float(np.sqrt(np.mean((d - d.mean()) ** 2)))


def db(v):
    return 10 * math.log10(max(v, 1e-30))


def rms(x):
    return math.sqrt(float(np.mean(x ** 2)) + 1e-30)


def itd(l, r, rate, maxlag_s=0.001):
    """Interaural time difference in ms, + = the right ear first (from the 0.2-1.5 kHz band)."""
    lb, rb = band(l, rate, 200, 1500), band(r, rate, 200, 1500)
    k = int(round(maxlag_s * rate))
    n = len(lb)
    nfft = 1 << int(math.ceil(math.log2(2 * n)))
    c = np.fft.irfft(np.fft.rfft(lb, nfft) * np.conj(np.fft.rfft(rb, nfft)), nfft)
    lags = np.concatenate([c[-k:], c[:k + 1]])   # lag -k..k: c[m] = sum l[t+m] r[t]
    i = int(np.argmax(lags))
    frac = 0.0
    if 0 < i < len(lags) - 1:
        a, b, cc = lags[i - 1], lags[i], lags[i + 1]
        den = a - 2 * b + cc
        if den != 0:
            frac = 0.5 * (a - cc) / den
    m = (i - k) + frac          # l lags r by m samples: m > 0 = the right ear first
    return 1000.0 * m / rate


def onset(env, thresh):
    idx = np.nonzero(env > thresh)[0]
    return int(idx[0]) if len(idx) else None


def burst_metrics(x, rate, start, burst_s, search_before, search_after):
    """Metrics of the burst expected at sample `start` (the onset searched in [start - before, start + after])."""
    a = max(0, start - int(search_before * rate))
    b = min(len(x), start + int((search_after + burst_s + 0.05) * rate))
    seg = x[a:b]
    if len(seg) < int(burst_s * rate):
        return None
    mono = np.max(np.abs(seg), axis=1)
    k = max(1, int(0.001 * rate))
    env = np.convolve(mono, np.ones(k) / k, "same")
    peak = float(np.max(env))
    on = onset(env, 0.1 * peak) if peak > 0 else None
    if on is None:
        return {"audible": False, "rms_max_db": -300.0}
    s0 = a + on
    m0, m1 = s0 + int(0.010 * rate), s0 + int((burst_s - 0.010) * rate)
    l, r = x[m0:m1, 0], x[m0:m1, 1]
    rl, rr = rms(l), rms(r)
    el_lo, el_hi = band_energy(l, rate, 300, 1500), band_energy(l, rate, 4000, 12000)
    er_lo, er_hi = band_energy(r, rate, 300, 1500), band_energy(r, rate, 4000, 12000)
    whole = x[s0:s0 + int(burst_s * rate)]
    louder = 0 if rl >= rr else 1
    inner = x[s0 + int(0.005 * rate):s0 + int((burst_s - 0.005) * rate)]

    def zero_run(v):
        z = (v == 0.0).astype(np.int8)
        if not z.any():
            return 0
        dz = np.diff(np.concatenate([[0], z, [0]]))
        st, en = np.nonzero(dz == 1)[0], np.nonzero(dz == -1)[0]
        return int(np.max(en - st))

    floor = x[max(0, start - int(0.22 * rate)):max(0, start - int(0.03 * rate))]   # the quiet gap before it
    return {
        "audible": True,
        "onset_ms": 1000.0 * (s0 - start) / rate,
        "rms_l": db(rl ** 2), "rms_r": db(rr ** 2), "rms_max_db": db(max(rl, rr) ** 2),
        "floor_db": db(rms(floor) ** 2) if len(floor) else -300.0,
        "ild": db(rr ** 2) - db(rl ** 2),
        "ild_lo": db(er_lo) - db(el_lo), "ild_hi": db(er_hi) - db(el_hi),
        "itd_ms": itd(l, r, rate),
        "tilt": db(el_hi + er_hi) - db(el_lo + er_lo),
        "energy_db": db(rl ** 2 + rr ** 2),   # both ears together (the loudness comparison, T8)
        "bands_l": bands_db(l, rate), "bands_r": bands_db(r, rate),
        "itd_valid": abs(db(rr ** 2) - db(rl ** 2)) < 40.0,   # a silent ear (hard panning) has no time difference
        "tilt_l": db(el_hi) - db(el_lo), "tilt_r": db(er_hi) - db(er_lo),
        "clip": int(np.sum(np.abs(whole) >= 0.999)),
        "zero_run_ms_louder": 1000.0 * zero_run(inner[:, louder]) / rate,
        "zero_run_ms_both": 1000.0 * max(zero_run(inner[:, 0]), zero_run(inner[:, 1])) / rate,
    }


# ------------------------------------------------------------------------------------------------ analysis
def find_chirp(mono, rate, sch, lo=0, hi=None, first=False):
    """The chirp's position; first=True: the EARLIEST strong match (a sweep has one chirp per pass, all alike)."""
    c = sch["chirp"]
    tpl = chirp(rate, c["s"], c["f0"], c["f1"])
    hi = hi if hi is not None else len(mono)
    pos, score = xcorr_find(mono, tpl, lo, hi)
    if first and pos is not None and score >= 0.5:
        step = int(2 * rate)
        for a in range(max(0, lo), pos, step):   # the first window holding a match nearly as good
            p2, s2 = xcorr_find(mono, tpl, a, min(pos, a + step + len(tpl)))
            if p2 is not None and s2 >= 0.9 * score:
                return p2, s2
    return pos, score


def norm_az(a):
    a = a % 360.0
    return a


def side(az):
    a = norm_az(az)
    if 30 <= a <= 150:
        return 1
    if 210 <= a <= 330:
        return -1
    return 0


def analyze_sweep(rate, x, sch, rep, checks):
    mono = x.mean(axis=1)
    passes = []
    first = None
    for pi, p in enumerate(sch["passes"]):
        if first is None:
            pos, score = find_chirp(mono, rate, sch, first=True)
            first = (pos, p["chirp_t"])
        else:
            pred = first[0] + int(round((p["chirp_t"] - first[1]) * rate))
            pos, score = find_chirp(mono, rate, sch, pred - int(0.2 * rate), pred + int(0.3 * rate))
        if pos is None or score < 0.5:
            checks.append(("chirp found (pass %d)" % pi, False, "score %.2f" % score))
            continue
        rows = []
        for b in p["bursts"]:
            start = pos + int(round((b["t"] - p["chirp_t"]) * rate))
            m = burst_metrics(x, rate, start, sch["burst_s"], 0.02, 0.05)
            if m is None:
                continue
            m.update({"az": b["az"], "el": b["el"], "attached": b.get("attached"), "spatialize": b.get("spatialize")})
            rows.append(m)
        passes.append({"hrtf": p["hrtf"], "chirp_score": score, "rows": rows})
    rep["passes"] = passes

    def get(rows, az, el=0):
        for r in rows:
            if int(r["az"]) % 360 == az % 360 and int(r["el"]) == el and r.get("audible"):
                return r
        return None

    onsets = {}
    for p in passes:
        rows = p["rows"]
        name = "HRTF" if p["hrtf"] else "pan"
        aud = [r for r in rows if r.get("audible") and r["rms_max_db"] > -80 and r["rms_max_db"] > r["floor_db"] + 30]
        checks.append(("%s: every burst audible (louder ear > -80 dBFS and 30 dB over the floor)" % name, len(aud) == len(rows) == 32,
                       "%d/%d (expected 32)" % (len(aud), len(rows))))
        checks.append(("%s: no clipping" % name, all(r.get("clip", 0) == 0 for r in rows), "max %d samples" % max([r.get("clip", 0) for r in rows] or [0])))
        key = "zero_run_ms_both" if p["hrtf"] else "zero_run_ms_louder"
        worst = max([r.get(key, 0) for r in rows] or [0])
        checks.append(("%s: no dropouts (zero runs >= 2 ms inside a burst%s)" % (name, "" if p["hrtf"] else ", louder ear"), worst < 2.0, "longest %.2f ms" % worst))
        onsets[name] = float(np.mean([r["onset_ms"] for r in aud])) if aud else None
        flat = [r for r in rows if int(r["el"]) == 0 and r.get("audible")]
        if p["hrtf"]:
            checks.append(("HRTF: every burst source had the spatializer attached", all(r.get("attached") for r in rows), ""))
            lat = [r for r in flat if side(r["az"])]
            bad = [r["az"] for r in lat if (1 if r["itd_ms"] > 0 else -1) != side(r["az"])]
            checks.append(("HRTF: ITD sign = side for |az| >= 30", len(lat) == 18 and not bad, "wrong at %s" % bad if bad else "all %d of 18" % len(lat)))
            for az in (90, 270):
                r = get(rows, az)
                v = abs(r["itd_ms"]) if r else None
                checks.append(("HRTF: |ITD| at %d within 0.45-0.85 ms" % az, v is not None and 0.45 <= v <= 0.85, "%.3f ms" % v if v is not None else "missing"))
            for az in (0, 180):
                r = get(rows, az)
                v = abs(r["itd_ms"]) if r else None
                checks.append(("HRTF: |ITD| at %d under 0.1 ms" % az, v is not None and v < 0.1, "%.3f ms" % v if v is not None else "missing"))
            r90, r270 = get(rows, 90), get(rows, 270)
            checks.append(("HRTF: 4-12 kHz ILD >= 8 dB at +-90", bool(r90 and r270 and r90["ild_hi"] >= 8 and r270["ild_hi"] <= -8),
                           "%+.1f / %+.1f dB" % (r90["ild_hi"], r270["ild_hi"]) if r90 and r270 else "missing"))
            sym = []
            for az in range(15, 180, 15):
                a, b = get(rows, az), get(rows, 360 - az)
                if a and b:
                    sym.append((az, abs(a["ild"] + b["ild"])))
            worst = max(sym, key=lambda t: t[1]) if sym else (None, 99)
            checks.append(("HRTF: mirror symmetry |ILD(az) + ILD(-az)| <= 2 dB", worst[1] <= 2.0, "worst %.2f dB at %s" % (worst[1], worst[0])))
            fb = []
            for f, b in ((30, 150), (330, 210), (60, 120), (300, 240)):
                a, c = get(rows, f), get(rows, b)
                if a and c:
                    fb.append(("%d/%d" % (f, b), a["tilt"] - c["tilt"]))
            rep["front_back_tilt_hrtf"] = fb
            log_info(rep, "HRTF: front/back tilt (4-12 vs 0.3-1.5 kHz; the plan's >= 2 dB estimate): " + ", ".join("%s %+.1f dB" % t for t in fb))
            sd = front_back_shape(rows, get)
            rep["front_back_shape_hrtf"] = sd
            checks.append(("HRTF: front and back differ in spectral shape (third-octave bands, both ears, RMS >= 2 dB; panning ~0)",
                           bool(sd) and all(v >= 2.0 for _, v in sd), ", ".join("%s %.1f dB" % t for t in sd)))
        else:
            v = [r for r in rows if r.get("audible") and r.get("itd_valid")]
            maxitd = max([abs(r["itd_ms"]) for r in v] or [99])
            checks.append(("pan: |ITD| <= 1 sample everywhere", maxitd <= 1000.0 / rate + 1e-9,
                           "max %.3f ms over %d bursts (1 sample = %.3f ms; %d with one ear silent left out)" % (maxitd, len(v), 1000.0 / rate, len(rows) - len(v))))
            fb = []
            for f, b in ((30, 150), (330, 210), (60, 120), (300, 240)):
                a, c = get(rows, f), get(rows, b)
                if a and c:
                    fb.append(("%d/%d" % (f, b), a["tilt"] - c["tilt"]))
            checks.append(("pan: front/back tilt difference <= 0.5 dB", bool(fb) and all(abs(v) <= 0.5 for _, v in fb), ", ".join("%s %+.2f dB" % t for t in fb)))
            rep["front_back_tilt_pan"] = fb
            sd = front_back_shape(rows, get)
            rep["front_back_shape_pan"] = sd
            checks.append(("pan: front and back have the same spectral shape (RMS <= 0.5 dB)", bool(sd) and all(v <= 0.5 for _, v in sd),
                           ", ".join("%s %.2f dB" % t for t in sd)))
    if onsets.get("HRTF") is not None and onsets.get("pan") is not None:
        d = onsets["HRTF"] - onsets["pan"]
        # re-based on Steam Audio's default HRTF (2026-10-13 r1): its filters start ~3.5 ms late (the head's own delay is in
        # the filter); an extra DSP block would be >= 10.7 ms (512 at 48 kHz). So: < 5 ms = no added buffering.
        checks.append(("onset HRTF - pan < 5 ms (no added buffering; one DSP block = 10.7 ms)", d < 5.0,
                       "%+.2f ms (HRTF %.2f, pan %.2f vs the schedule)" % (d, onsets["HRTF"], onsets["pan"])))
    if len(passes) == 2:
        h, p = get(passes[0]["rows"], 0), get(passes[1]["rows"], 0)
        if h and p:
            # T8: the frontal burst, both ears' energy together, HRTF vs panning; and the mean over every direction
            rep["loudness_front_db"] = h["energy_db"] - p["energy_db"]
            both = [(a["energy_db"], b["energy_db"]) for a in passes[0]["rows"] for b in passes[1]["rows"]
                    if a.get("audible") and b.get("audible") and a["az"] == b["az"] and a["el"] == b["el"]]
            rep["loudness_mean_db"] = float(np.mean([a - b for a, b in both])) if both else None
            # T8, re-based: against panning the HRTF goes from -2.4 dB (straight ahead -1.7) to +1.5 dB (r3), so the gain is set
            # on the mean over all 32 directions (HrtfGainDb = -mean if it is more than 1 dB off); the frontal value is reported
            checks.append(("T8 loudness, HRTF - pan over all 32 directions within 1 dB (else set HrtfGainDb)",
                           rep["loudness_mean_db"] is not None and abs(rep["loudness_mean_db"]) <= 1.0,
                           "mean %+.2f dB (L+R energy); straight ahead %+.2f dB (HRTF L/R %.1f/%.1f, pan %.1f/%.1f dBFS)" % (
                               rep["loudness_mean_db"] or 0, rep["loudness_front_db"], h["rms_l"], h["rms_r"], p["rms_l"], p["rms_r"])))


def log_info(rep, msg):
    rep.setdefault("info", []).append(msg)
    print("info  " + msg)


def front_back_shape(rows, get):
    """For 30/150, 330/210, 60/120, 300/240: the spectral shape difference, the mean of both ears."""
    out = []
    for f, b in ((30, 150), (330, 210), (60, 120), (300, 240)):
        a, c = get(rows, f), get(rows, b)
        if a and c:
            out.append(("%d/%d" % (f, b), (shape_diff(a["bands_l"], c["bands_l"]) + shape_diff(a["bands_r"], c["bands_r"])) / 2))
    return out


def analyze_measure(rate, x, sch, rep, checks):
    mono = x.mean(axis=1)
    pos, score = find_chirp(mono, rate, sch, first=True)
    if pos is None or score < 0.5:
        checks.append(("chirp found", False, "score %.2f" % score))
        return
    rows = []
    for b in sch["bursts"]:
        start = pos + int(round((b["t_call"] - sch["chirp_t"]) * rate))
        m = burst_metrics(x, rate, start, sch["burst_s"], 0.01, 0.15)
        if m is None:
            continue
        m.update({k: b.get(k) for k in ("spatialize", "attached", "on_hrtf", "blend")})
        m.update(b.get("where", {}))
        rows.append(m)
    rep["rows"] = rows
    aud = [r for r in rows if r.get("audible") and r["rms_max_db"] > -80]
    checks.append(("every burst audible", len(aud) == len(rows) == len(sch["bursts"]), "%d/%d" % (len(aud), len(sch["bursts"]))))
    if not aud:
        return
    med = lambda k: float(np.median([r[k] for r in aud]))
    rep["median"] = {k: med(k) for k in ("itd_ms", "ild", "ild_lo", "ild_hi", "tilt", "rms_max_db", "onset_ms")}
    rep["bands_l"] = [float(v) for v in np.median([r["bands_l"] for r in aud], axis=0)]
    rep["bands_r"] = [float(v) for v in np.median([r["bands_r"] for r in aud], axis=0)]
    rep["az"] = float(np.median([r["az"] for r in aud if r.get("az") is not None])) if any(r.get("az") is not None for r in aud) else None
    rep["on_hrtf"] = all(r.get("on_hrtf") for r in aud)
    sd = side(rep["az"]) if rep["az"] is not None else 0
    if sd:
        ok = (1 if rep["median"]["itd_ms"] > 0 else -1) == sd
        checks.append(("ITD sign = the source's side (az %.0f)" % rep["az"], ok, "%+.3f ms" % rep["median"]["itd_ms"]))


def main():
    a = sys.argv[1:]
    if len(a) < 2:
        raise SystemExit(__doc__)
    wav, js = a[0], a[1]
    out = a[a.index("--out") + 1] if "--out" in a else None
    rate, x = read_wav(wav)
    if x.shape[1] < 2:
        raise SystemExit("%s: mono recording" % wav)
    x = x[:, :2]
    sch = json.load(open(js))
    if sch.get("rate") and int(sch["rate"]) != rate:
        print("note: the game mixes at %s Hz, the recording is %d Hz (PipeWire resampled; times are in seconds)" % (sch["rate"], rate))
    rep = {"wav": wav, "schedule": js, "tag": sch.get("tag"), "kind": sch.get("kind"), "rate": rate, "seconds": len(x) / rate}
    checks = []
    if sch.get("kind") == "sweep":
        analyze_sweep(rate, x, sch, rep, checks)
    else:
        analyze_measure(rate, x, sch, rep, checks)
    rep["checks"] = [{"check": c, "pass": bool(p), "value": v} for c, p, v in checks]
    if "--table" in a:
        for p in rep.get("passes", []):
            print("%s pass (chirp score %.2f):" % ("HRTF" if p["hrtf"] else "pan", p["chirp_score"]))
            print("   az  el   L dBFS  R dBFS   ILD  ILDlo  ILDhi   ITD ms   tilt  onset ms")
            for r in p["rows"]:
                if not r.get("audible"):
                    print("  %4d %3d  SILENT" % (r["az"], r["el"]))
                    continue
                print("  %4d %3d  %6.1f  %6.1f  %+5.1f  %+5.1f  %+5.1f  %+7.3f  %+5.1f  %+7.2f" % (
                    r["az"], r["el"], r["rms_l"], r["rms_r"], r["ild"], r["ild_lo"], r["ild_hi"], r["itd_ms"], r["tilt"], r["onset_ms"]))
        for r in rep.get("rows", []):
            if r.get("audible"):
                print("  az %6.1f el %5.1f dist %4.2f  L %6.1f R %6.1f  ILD %+5.1f hi %+5.1f  ITD %+7.3f ms  tilt %+5.1f  onset %+6.1f ms  hrtf %s" % (
                    r.get("az") or 0, r.get("el") or 0, r.get("dist_m") or 0, r["rms_l"], r["rms_r"], r["ild"], r["ild_hi"], r["itd_ms"], r["tilt"], r["onset_ms"], r.get("on_hrtf")))
    bad = 0
    for c in rep["checks"]:
        print("%s  %s  %s" % ("PASS" if c["pass"] else "FAIL", c["check"], c["value"]))
        bad += not c["pass"]
    if out:
        json.dump(rep, open(out, "w"), indent=1)
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()

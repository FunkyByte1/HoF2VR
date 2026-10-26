#!/usr/bin/env python3
"""The HRTF proof without ears (docs/hrtf-plan.md §6 as built): one short test game in this environment, its sound on a
temporary PipeWire null sink nobody hears, recorded and measured (scripts/hrtf_analyze.py).

  hrtf_test.py [--stages boot,sweep,dealer] [--snapshot magician] [--to table|combat] [--perf-windows 10] [--tag T]

Stages:
  boot     T2: `hrtf status` is ON with Steam Audio 4.8.1, stereo, the rate and buffer; Player.log preloaded both libraries,
           no "Plugins: Couldn't open", no "failed to initialize audio spatializer"; the mod's log says ready
  sweep    T3: `hrtf sweep <tag> ab` (32 bursts through the HRTF, then the same with Unity's panning) -> analysed
  sweepx   (experiment) the first sweep with the plugin's distance attenuation off for the HRTF bursts
  fight    from the table into a live fight (combat_fixture.py --god), for enemy after table stages
  sweep2   two more HRTF-only sweeps, the second in reverse order: each direction's level and ITD must repeat
  dealer   T5: the same burst through the Dealer's own voice source at the table, head yaw 0 / +90 / -90 / 180 (xrsim),
           HRTF on and off; the ITD must follow the turn and 180 must sound duller than 0
  enemy    a live fight (--to combat, combat_fixture) frozen (timescale 0): the burst through the nearest enemy's own source,
           the head turned to face it and then away (it is behind), HRTF on and off
  lazy     boot with [Audio] Hrtf = false (set it in env 3's settings file first): OFF, then `hrtf on` initialises it
  reset    T7: `hrtf reset mono` (AudioSettings.Reset, as the game's Speaker Mode option) pauses the HRTF, stereo resumes it
           (run enemy2 after it: the same enemy measurement once more, after the reset)
  watch    T2: `audio watch 25` and `hrtf status` by group while autoplay plays; exceptions in the log
  vrset    the VR settings page (pause menu, Voice tab): the "3D sound (HRTF)" row On/Off by touch (playtest.py)
  perf     with the quiet flag held: autoplay, [Audio] Hrtf switched on/off every --perf-window-s (40) s, --perf-windows (8), the
           `perf 10s` lines and per-thread CPU (the FMOD mixer) compared on vs off
  cpu      CPU per thread id with HRTF on / off (4 x 20 s) while autoplay plays (FMOD's mixer thread has no name)
  quirk    the 3 dB quirk experiment (`hrtf quirk`): which variants come out ~3 dB quiet, by round
  loadcheck  (first stage, starts the game itself) HRTF off at boot (env 3's settings file): a head-locked 1 kHz tone recorded
           from early in the boot through table -> fight -> table; then HRTF on and the same round trip (hrtf_clicks.py)
  soak     (first stage, starts the game itself; HRTF on at boot) the boot recorded with the tone, then e.g. `soak,quirk,soak`:
           the quirk test while the session is fresh, then --soak-min (30) minutes of autoplay with game sound through the HRTF:
           alive, exceptions, memory (`hrtf mem` every 30 s), perf lines by state
  combo    (first stage, starts the game itself; HRTF on at boot) the boot recorded, quirk, a round trip on and one off
           (both recorded), then the soak - the coordinator's three checks in one game turn
  rton, rtoff  a recorded table -> fight -> table round trip (autoplay into a fight, leave_fight.py back) with HRTF on / off,
           the tone probe following the listener in use; after both: the loads compared (hrtf_clicks.py)
  drill    T2 fallback: the two .so files taken out of the game copy, a boot: `hrtf status` must say "plugin missing" and
           sounds play (panning); then install.sh puts them back
Safety: the game starts SILENT (test runs zero the listener volume) with PULSE_SINK=hof2vr_null; only once pactl shows its
stream on hof2vr_null does `hrtf sound 1` turn the sound up. The recorder (pw-record) takes only hof2vr_null (no fallback),
checked before each recording. At the end: sound off, game stopped, sink unloaded, default sink and WirePlumber's state
file checked. The user always wins: status.json "user" true (or missing/stale) -> everything stops.
Outputs: testdata/hrtf/<tag>.wav|.json|.report.json, logs/hrtf_test-<tag>.log (this script's output).
"""
import glob
import json
import math
import os
import re
import signal
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402
import session  # noqa: E402

H = hof2env.H
OUT = os.path.join(H, "testdata", "hrtf")
SINK = "hof2vr_null"
VRS = os.path.join(hof2env.REAL_RT, "vr-session")
WP_STATE = os.path.expanduser("~/.local/state/wireplumber/stream-properties")
RESULTS = []


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


def result(name, ok, detail=""):
    RESULTS.append((name, bool(ok), detail))
    log("%s  %s  %s" % ("PASS" if ok else "FAIL", name, detail))


class UserFirst(Exception):
    pass


def user_in_vr():
    try:
        st = json.load(open(os.path.join(VRS, "status.json")))
    except Exception:
        return "no readable status.json"
    if time.time() - float(st.get("updated") or 0) > 90:
        return "status.json is stale"
    if st.get("user") is not False:
        return "the user is in VR"
    return None


def reserved():
    """Another environment's slot reservation (coordinator 2026-10-14 ~00:25; scripts/reserve.sh, docs/two-environments.md
    "Reservations"): while its /run/user/1000/vr-session/reserved-envN is fresh (< 120 s) this environment starts no game.
    run.sh waits for it too; this script also stops at its next step if one appears while it runs."""
    for f in glob.glob(os.path.join(VRS, "reserved-env*")):
        if f.endswith("reserved-env%d" % hof2env.ENV):
            continue
        try:
            age = time.time() - os.stat(f).st_mtime
        except OSError:
            continue
        if age < 120:
            return "%s holds the game slot (%s %.0f s old)" % (os.path.basename(f).replace("reserved-", ""), os.path.basename(f), age)
    return None


def guard():
    why = user_in_vr() or reserved()
    if why:
        raise UserFirst(why)


def send(c, t=15):
    guard()
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def sink(*a):
    return subprocess.run([H + "/scripts/hrtf_sink.sh"] + list(a), capture_output=True, text=True)


def sleep(s):
    end = time.time() + s
    while time.time() < end:
        guard()
        time.sleep(min(1.0, max(0.0, end - time.time())))


def bep_log():
    try:
        return open(hof2env.GLOG, errors="replace").read()
    except OSError:
        return ""


def wait_log(pattern, timeout, after=0):
    end = time.time() + timeout
    while time.time() < end:
        guard()
        m = re.search(pattern, bep_log()[after:])
        if m:
            return m
        time.sleep(0.5)
    return None


# ------------------------------------------------------------------------------------------------ the recorder
class Rec:
    """pw-record from hof2vr_null only (stream.capture.sink, no fallback); checked to be linked to its monitor."""

    def __init__(self, tag):
        guard()
        self.path = os.path.join(OUT, tag + ".wav")
        self.p = subprocess.Popen(
            ["pw-record", "--target", SINK, "-P",
             "{ stream.capture.sink = true node.dont-fallback = true node.dont-reconnect = true node.name = hof2vr_rec "
             "state.restore-props = false state.restore-target = false }",
             "--rate", "48000", "--channels", "2", "--format", "s32", self.path],
            stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        time.sleep(0.8)
        mon = subprocess.run(["pactl", "list", "short", "sources"], capture_output=True, text=True).stdout
        idx = next((l.split()[0] for l in mon.splitlines() if l.split()[1:2] == [SINK + ".monitor"]), None)
        so = subprocess.run(["pactl", "list", "source-outputs"], capture_output=True, text=True).stdout
        src = None
        for block in so.split("Source Output #")[1:]:
            if 'node.name = "hof2vr_rec"' in block:
                m = re.search(r"\n\tSource: (\d+)", block)
                src = m.group(1) if m else None
        if idx is None or src != idx or self.p.poll() is not None:
            self.p.kill()
            self.p.wait()
            try:
                os.remove(self.path)
            except OSError:
                pass
            raise SystemExit("recorder not on %s.monitor (monitor %s, recorder's source %s) - stopped it" % (SINK, idx, src))

    def stop(self):
        if self.p.poll() is None:
            self.p.send_signal(signal.SIGINT)
            try:
                self.p.wait(5)
            except subprocess.TimeoutExpired:
                self.p.kill()
                self.p.wait()
        return self.path


def analyze(tag, show_table=False):
    wav, js = os.path.join(OUT, tag + ".wav"), os.path.join(OUT, tag + ".json")
    rep = os.path.join(OUT, tag + ".report.json")
    args = ["python3", "-I", H + "/scripts/hrtf_analyze.py", wav, js, "--out", rep] + (["--table"] if show_table else [])
    r = subprocess.run(args, capture_output=True, text=True)
    print(r.stdout + r.stderr, flush=True)
    try:
        return json.load(open(rep))
    except Exception:
        return None


def record(tag, command, done_pattern, timeout, table=False):
    """Record while the mod plays a test schedule; wait for its 'done' line; analyse."""
    start = len(bep_log())
    rec = Rec(tag)
    try:
        r = send(command, 20)
        log(command, "->", r)
        if not r.startswith(("sweeping", "measuring")):
            raise SystemExit("the mod refused: " + r)
        m = wait_log(done_pattern, timeout, start)
        time.sleep(0.4)
    finally:
        rec.stop()
    if not m:
        raise SystemExit("no '%s' in the log within %d s" % (done_pattern, timeout))
    return analyze(tag, table)


# ------------------------------------------------------------------------------------------------ stages
def stage_boot():
    st = send("hrtf status")
    log("hrtf status:", st)
    ok = st.startswith("ON") and "Steam Audio 4.8.1" in st and "| Stereo |" in st
    result("boot: hrtf status ON, Steam Audio 4.8.1, stereo", ok, st[:200])
    m = re.search(r"\| (\d+) Hz, buffer (\d+) x (\d+) \|", st)
    result("boot: output rate and DSP buffer known", bool(m), m.group(0) if m else "-")
    pl = open(os.path.join(H, "logs", "player-latest.log"), errors="replace").read()
    pre = [x for x in ("libaudioplugin_phonon.so", "libphonon.so") if "Preloaded '%s'" % x in pl]
    result("boot: Player.log preloaded both Steam Audio libraries", len(pre) == 2, ", ".join(pre))
    bad = [l for l in pl.splitlines() if "Couldn't open" in l or "failed to initialize audio spatializer" in l.lower()]
    result("boot: no 'Plugins: Couldn't open', no spatializer failure", not bad, bad[0][:160] if bad else "none")
    m = re.search(r"Hrtf: ready - .*", bep_log())
    result("boot: the mod's init and self-check passed", bool(m), m.group(0)[:220] if m else "no 'Hrtf: ready' line")


def plays():
    """The play hook's count of test-probe starts (every probe of a sweep starts with PlayScheduled)."""
    m = re.search(r"\(test probes (\d+)\)", send("hrtf status"))
    return int(m.group(1)) if m else None


def stage_sweep(tag):
    p0 = plays()
    rep = record(tag, "hrtf sweep %s ab" % tag, r"hrtf sweep '%s' done" % re.escape(tag), 90, table=True)
    p1 = plays()
    # plan §10 question 2: 2 chirps + 64 bursts all start with PlayScheduled
    if p0 is not None and p1 is not None:
        result("sweep: Unity's per-play callback fires for PlayScheduled (the hook saw the 66 probe starts)", p1 - p0 == 66, "%d probe starts seen" % (p1 - p0))
    if rep is None:
        result("sweep: analysed", False, "no report")
        return
    for c in rep["checks"]:
        result("sweep: " + c["check"], c["pass"], c["value"])


def stage_sweep2(tag):
    """Two more HRTF-only sweeps, the second in reverse order: a level that belongs to a direction (the HRTF) is the same in
    both; one that drifts with time is not."""
    reps = {}
    for name, extra in (("fwd", ""), ("rev", " rev")):
        t = "%s_%s" % (tag, name)
        reps[name] = record(t, "hrtf sweep %s hrtf%s" % (t, extra), r"hrtf sweep '%s' done" % re.escape(t), 60)
    if not all(reps.values()):
        result("sweep2: both sweeps analysed", False, "")
        return
    a = {(r["az"], r["el"]): r for r in reps["fwd"]["passes"][0]["rows"] if r.get("audible")}
    b = {(r["az"], r["el"]): r for r in reps["rev"]["passes"][0]["rows"] if r.get("audible")}
    d = [(k, a[k]["energy_db"] - b[k]["energy_db"], a[k]["itd_ms"] - b[k]["itd_ms"]) for k in sorted(a) if k in b]
    worst = max(d, key=lambda t: abs(t[1])) if d else (None, 99, 99)
    result("sweep2: each direction's level is the same in forward and reverse order (within 1 dB): it belongs to the direction",
           len(d) == 32 and abs(worst[1]) <= 1.0, "%d directions, worst %+.2f dB at %s" % (len(d), worst[1], worst[0]))
    wi = max(d, key=lambda t: abs(t[2])) if d else (None, 99, 99)
    result("sweep2: each direction's ITD is the same in both orders (within 0.05 ms)", len(d) == 32 and abs(wi[2]) <= 0.05,
           "worst %+.3f ms at %s" % (wi[2], wi[0]))
    log("level by direction (L+R energy, dB), forward / reversed: " + ", ".join("%d/%d %.1f/%.1f" % (k[0], k[1], a[k]["energy_db"], b[k]["energy_db"]) for k in sorted(a) if k in b))


def head_yaw(yaw):
    sim("head 0 1.6 0 %g 0 0" % yaw)
    time.sleep(0.6)


def where(kind):
    r = send("hrtf where " + kind)
    try:
        return json.loads(r)
    except ValueError:
        return {"error": r}


def stage_dealer(tag):
    v = send("voice")
    log("voice:", v[:220])
    rows = {}
    for yaw in (0, 90, -90, 180):
        head_yaw(yaw)
        w = where("dealer")
        for mode in ("on", "off"):
            send("hrtf " + mode)
            time.sleep(0.5)
            t = "%s_y%d_%s" % (tag, yaw, mode)
            rep = record(t, "hrtf say %s 4" % t, r"hrtf dealer '%s' done" % re.escape(t), 30)
            rows[(yaw, mode)] = (rep, w)
    send("hrtf on")
    head_yaw(0)
    summarize_pairs("dealer", rows, [0, 90, -90, 180])


def stage_enemy(tag):
    send("timescale 0")
    head_yaw(0)
    time.sleep(0.5)
    rows = {}
    try:
        w = where("enemy")
        log("nearest enemy:", w)
        if "az" not in w:
            result("enemy: a live enemy with its own 3D sound source", False, str(w))
            return
        # xrsim: + yaw turns the head left; the enemy is at az (+ = right). Two steps converge on it.
        yaw0 = 0.0
        for _ in range(3):
            yaw0 -= float(w["az"])
            head_yaw(yaw0)
            w = where("enemy")
            if "az" not in w or abs(float(w["az"])) < 5:
                break
        for name, yaw in (("front", yaw0), ("behind", yaw0 + 180.0)):
            head_yaw(yaw)
            w2 = where("enemy")
            log("enemy %s: head yaw %.0f -> %s" % (name, yaw, w2))
            for mode in ("on", "off"):
                send("hrtf " + mode)
                time.sleep(0.5)
                t = "%s_%s_%s" % (tag, name, mode)
                rep = record(t, "hrtf enemy %s 4" % t, r"hrtf enemy '%s' done" % re.escape(t), 30)
                rows[(name, mode)] = (rep, w2)
    finally:
        send("hrtf on")
        send("timescale 1")
        head_yaw(0)
    summarize_pairs("enemy", rows, ["front", "behind"])


def med(rep, k):
    return rep["median"][k] if rep and "median" in rep else None


def shape(a, b):
    """Front/back spectral shape difference (dB RMS over third-octave bands, mean of both ears; level taken out)."""
    import hrtf_analyze as A
    if not a or not b or "bands_l" not in a or "bands_l" not in b:
        return None
    return (A.shape_diff(a["bands_l"], b["bands_l"]) + A.shape_diff(a["bands_r"], b["bands_r"])) / 2


def summarize_pairs(kind, rows, keys):
    log("%s summary (median per recording):" % kind)
    print("   where      mode   az     ITD ms   ILD dB  ILDhi dB  tilt dB  level dBFS  on HRTF", flush=True)
    for k in keys:
        for mode in ("on", "off"):
            rep, w = rows.get((k, mode), (None, {}))
            if not rep or "median" not in rep:
                print("   %-9s  %-4s  (no data)" % (k, mode), flush=True)
                continue
            m = rep["median"]
            print("   %-9s  %-4s  %5.0f  %+7.3f  %+6.1f  %+7.1f  %+6.1f  %8.1f   %s" % (
                k, mode, rep.get("az") or 0, m["itd_ms"], m["ild"], m["ild_hi"], m["tilt"], m["rms_max_db"], rep.get("on_hrtf")), flush=True)
            for c in rep.get("checks", []):
                result("%s %s %s: %s" % (kind, k, mode, c["check"]), c["pass"], c["value"])
    # the on/off switch reached the source; HRTF vs panning, front vs back
    for k in keys:
        on, off = rows.get((k, "on"), (None, None))[0], rows.get((k, "off"), (None, None))[0]
        if on and off and "median" in on and "median" in off:
            result("%s %s: HRTF on reached the game's source, off left it on panning" % (kind, k), bool(on.get("on_hrtf")) and not off.get("on_hrtf"),
                   "on_hrtf %s / %s" % (on.get("on_hrtf"), off.get("on_hrtf")))
    if kind == "dealer":
        on = {k: rows.get((k, "on"), (None, None))[0] for k in keys}
        off = {k: rows.get((k, "off"), (None, None))[0] for k in keys}
        if all(med(on[k], "itd_ms") is not None for k in (90, -90)):
            # head turned left (+90): he is on the right -> the right ear first (+ITD)
            result("dealer: HRTF ITD follows the head turn (yaw +90 -> right ear first, -90 -> left)",
                   med(on[90], "itd_ms") > 0.2 and med(on[-90], "itd_ms") < -0.2, "%+.3f / %+.3f ms" % (med(on[90], "itd_ms"), med(on[-90], "itd_ms")))
        if med(on[0], "tilt") is not None and med(on[180], "tilt") is not None and med(off[0], "tilt") is not None:
            log("dealer: front - back tilt (4-12 vs 0.3-1.5 kHz): HRTF %+.1f dB, panning %+.2f dB" % (
                med(on[0], "tilt") - med(on[180], "tilt"), med(off[0], "tilt") - med(off[180], "tilt")))
        sh, sp = shape(on[0], on[180]), shape(off[0], off[180])
        if sh is not None and sp is not None:
            result("dealer: him in front and behind sound different with HRTF (spectral shape >= 2 dB), the same with panning (<= 0.5 dB)",
                   sh >= 2.0 and sp <= 0.5, "HRTF %.1f dB, panning %.2f dB" % (sh, sp))
    if kind == "enemy":
        f_on, b_on = rows.get(("front", "on"), (None, None))[0], rows.get(("behind", "on"), (None, None))[0]
        f_off, b_off = rows.get(("front", "off"), (None, None))[0], rows.get(("behind", "off"), (None, None))[0]
        if None not in (med(f_on, "tilt"), med(b_on, "tilt"), med(f_off, "tilt"), med(b_off, "tilt")):
            log("enemy: front - behind tilt (4-12 vs 0.3-1.5 kHz): HRTF %+.1f dB, panning %+.2f dB" % (
                med(f_on, "tilt") - med(b_on, "tilt"), med(f_off, "tilt") - med(b_off, "tilt")))
        sh, sp = shape(f_on, b_on), shape(f_off, b_off)
        if sh is not None and sp is not None:
            result("enemy: in front and behind sound different with HRTF (spectral shape >= 2 dB), the same with panning (<= 0.5 dB)",
                   sh >= 2.0 and sp <= 0.5, "HRTF %.1f dB, panning %.2f dB" % (sh, sp))


def stage_lazy():
    """[Audio] Hrtf off at boot (env 3's settings file): nothing initialised; switched on in the game, the init runs then
    (the user's A/B on the VR settings page with the default off)."""
    s0 = send("hrtf status")
    result("lazy: off at boot - nothing initialised, the game's panning", s0.startswith("OFF") and "Hrtf = false" in s0, s0[:120])
    start = len(bep_log())
    log("hrtf on:", send("hrtf on"))
    m = wait_log(r"Hrtf: ready - .*?init ([\d.]+) ms.*", 10, start)
    s1 = send("hrtf status")
    result("lazy: switched on in the game, it initialises and is ON", bool(m) and s1.startswith("ON"),
           "init %s ms; %s" % (m.group(1) if m else "-", s1[:100]))
    # (BepInEx writes its log in chunks: anchor on the switch-on line itself, not on a file offset taken before it)
    time.sleep(3)
    txt = bep_log()
    i = txt.rfind("Hrtf: [Audio] Hrtf on")
    after = txt[i:] if i >= 0 else ""
    st = re.findall(r"perf stall [^|]*", after[:20000])
    result("lazy: no stall when it is switched on (perf stall lines after the switch-on line)", i >= 0 and not st, ", ".join(st)[:300] or "none")


def stage_reset():
    """T7: what the game's Speaker Mode option does (AudioSettings.Reset): Mono pauses the HRTF with the reason, Stereo
    resumes it; the game lives on (a later stage measures that sound still goes through the HRTF)."""
    s0 = send("hrtf status")
    log("reset mono:", send("hrtf reset mono", 20))
    sleep(2)
    s1 = send("hrtf status")
    log("reset stereo:", send("hrtf reset stereo", 20))
    sleep(2)
    s2 = send("hrtf status")
    result("reset: speaker mode Mono pauses the HRTF with its reason, every source back on panning",
           s1.startswith("PAUSED") and "Mono" in s1 and re.search(r"flagged 0 ", s1) is not None, s1[:160])
    result("reset: Stereo again resumes it", s2.startswith("ON"), s2[:160])
    result("reset: the game answers after both resets", send("state?").startswith("GameState"), send("state?")[:60])


def autoplay(minutes):
    return subprocess.Popen(["python3", H + "/scripts/autoplay.py", str(minutes)], stdout=open(os.path.join(H, "logs", "hrtf_autoplay.out"), "w"),
                            stderr=subprocess.STDOUT)


def stage_watch():
    ap = autoplay(2)
    try:
        sleep(30)
        log("audio watch:", send("audio watch 25 hrtf"))
        sleep(28)
        st = send("hrtf status")
        log("hrtf status:", st)
        m = re.search(r"flagged (\d+) \(live (\d+), attached (\d+)\).*hook errors (\d+)", st)
        # (a flagged source on an inactive effect object has no effect attached until it plays again: attached <= live)
        ok = bool(m) and int(m.group(4)) == 0 and int(m.group(1)) > 0
        result("watch: sources on the HRTF while autoplay plays, 0 hook errors", ok, st[st.find("flagged"):][:400])
        w = re.findall(r"audio watch 'hrtf'.*?(?=\n\[|\Z)", bep_log(), re.S)
        if w:
            groups = re.findall(r"group '([^']+)':", w[-1])
            hrtf_groups = sorted(set(g for g, body in re.findall(r"group '([^']+)':(.*?)(?= group '|\Z)", w[-1], re.S) if " HRTF" in body))
            log("audio watch groups:", ", ".join(groups))
            result("watch: groups heard on the HRTF", bool(hrtf_groups), ", ".join(hrtf_groups))
    finally:
        ap.terminate()
        subprocess.run([H + "/scripts/stopauto.sh"], capture_output=True)


def thread_cpu(pid):
    d = {}
    for t in glob.glob("/proc/%d/task/*" % pid):
        try:
            st = open(t + "/stat").read()
            name = st[st.index("(") + 1:st.rindex(")")]
            f = st[st.rindex(")") + 2:].split()
            d[t.rsplit("/", 1)[1]] = (name, int(f[11]) + int(f[12]))
        except Exception:
            pass
    return d


def stage_perf(windows, win_s):
    pid = hof2env.game_pids()[0]
    hz = os.sysconf("SC_CLK_TCK")
    stop = threading.Event()

    def quiet():
        while not stop.is_set():
            try:
                with open(os.path.join(VRS, "quiet"), "a"):
                    os.utime(os.path.join(VRS, "quiet"), None)
            except OSError:
                pass
            stop.wait(30)

    qt = threading.Thread(target=quiet, daemon=True)
    qt.start()
    ap = autoplay(int(windows * win_s / 60.0) + 2)
    spans, cpu = [], {"on": {}, "off": {}}
    try:
        sleep(20)
        for i in range(windows):
            mode = "on" if i % 2 == 0 else "off"
            send("hrtf " + mode)
            t0 = time.time()
            a = thread_cpu(pid)
            sleep(win_s)
            b = thread_cpu(pid)
            t1 = time.time()
            spans.append((mode, t0, t1))
            for tid, (name, ticks) in b.items():
                if tid in a:
                    c = cpu[mode].setdefault(name, [0.0, 0.0])
                    c[0] += (ticks - a[tid][1]) * 1000.0 / hz
                    c[1] += t1 - t0
            log("perf window %d: HRTF %s" % (i, mode))
    finally:
        send("hrtf on")
        stop.set()
        ap.terminate()
        subprocess.run([H + "/scripts/stopauto.sh"], capture_output=True)
        try:
            os.remove(os.path.join(VRS, "quiet"))
        except OSError:
            pass
    # the perf 10s lines: '@HH:MM:SS.mmm' is the start of a 10 s window (PerfMonitor's Clock0)
    lines = re.findall(r"perf 10s @(\d\d):(\d\d):(\d\d)\.\d+ \[([^\]]*)\] (\d+) frames [\d.]+s: avg ([\d.]+) p50 ([\d.]+) p95 ([\d.]+) p99 ([\d.]+) max ([\d.]+) ms \| .*?miss ([\d.]+)%", bep_log())
    day = time.strftime("%Y-%m-%d")
    by = {"on": [], "off": []}
    for hh, mm, ss, state, frames, avg, p50, p95, p99, mx, miss in lines:
        begin = time.mktime(time.strptime("%s %s:%s:%s" % (day, hh, mm, ss), "%Y-%m-%d %H:%M:%S"))
        for mode, t0, t1 in spans:
            if t0 + 1.0 <= begin and begin + 10.5 <= t1:
                by[mode].append(dict(state=state, avg=float(avg), p95=float(p95), p99=float(p99), max=float(mx), miss=float(miss)))
    import statistics as S
    log("perf 10s lines inside the windows: %d with HRTF on, %d off" % (len(by["on"]), len(by["off"])))
    # like with like: autoplay wanders (map, level load, fight), so compare each game state on its own
    states = sorted(set(r["state"] for m in by for r in by[m]))
    compared = []
    for st in states:
        on = [r for r in by["on"] if r["state"] == st]
        off = [r for r in by["off"] if r["state"] == st]
        if len(on) >= 2 and len(off) >= 2:
            d_avg = S.median(r["avg"] for r in on) - S.median(r["avg"] for r in off)
            d_p99 = S.median(r["p99"] for r in on) - S.median(r["p99"] for r in off)
            compared.append((st, d_avg, d_p99, len(on), len(off)))
            log("  %s: HRTF on - off: avg %+.2f ms, p99 %+.2f ms (%d / %d lines)" % (st.replace("GameState_", ""), d_avg, d_p99, len(on), len(off)))
    if compared:
        result("perf: no frame-time regression with HRTF on, state by state (median avg within 0.5 ms, p99 within 2 ms)",
               all(d <= 0.5 and p <= 2.0 for _, d, p, _, _ in compared),
               "; ".join("%s avg %+.2f p99 %+.2f (%d/%d)" % (st.replace("GameState_", ""), d, p, a, b) for st, d, p, a, b in compared))
    summary = {}
    for mode in ("on", "off"):
        if by[mode]:
            summary[mode] = {k: S.median([r[k] for r in by[mode]]) for k in ("avg", "p95", "p99", "max", "miss")}
            log("  HRTF %-3s median of %d lines: avg %.2f ms, p95 %.2f, p99 %.2f, max %.1f, miss %.1f %%; states %s" % (
                mode, len(by[mode]), summary[mode]["avg"], summary[mode]["p95"], summary[mode]["p99"], summary[mode]["max"], summary[mode]["miss"],
                ", ".join(sorted(set(r["state"].replace("GameState_", "") for r in by[mode])))))
    if "on" in summary and "off" in summary:
        log("all states together (mixes states, for the record): avg %+.2f ms, p95 %+.2f, p99 %+.2f, miss %+.1f pts" % (
            summary["on"]["avg"] - summary["off"]["avg"], summary["on"]["p95"] - summary["off"]["p95"],
            summary["on"]["p99"] - summary["off"]["p99"], summary["on"]["miss"] - summary["off"]["miss"]))
    names = sorted(set(cpu["on"]) | set(cpu["off"]), key=lambda n: -(cpu["on"].get(n, [0, 1])[0] / max(cpu["on"].get(n, [0, 1])[1], 1e-9)))
    log("per-thread CPU, ms per second (HRTF on / off):")
    for n in names[:14]:
        on = cpu["on"].get(n, [0, 1])
        off = cpu["off"].get(n, [0, 1])
        print("   %-16s %7.1f / %7.1f" % (n, on[0] / max(on[1], 1e-9), off[0] / max(off[1], 1e-9)), flush=True)
    fm = [n for n in names if "fmod" in n.lower() or "mixer" in n.lower()]
    for n in fm:
        on, off = cpu["on"].get(n, [0, 1]), cpu["off"].get(n, [0, 1])
        log("FMOD thread '%s': %.1f ms/s with HRTF, %.1f without" % (n, on[0] / max(on[1], 1e-9), off[0] / max(off[1], 1e-9)))


def stage_cpu(windows=4, win_s=20.0):
    """FMOD's mixer thread is unnamed (it shows as the process name), so: CPU per thread id with HRTF on and off while
    autoplay plays; the threads whose CPU follows the switch are the audio threads."""
    pid = hof2env.game_pids()[0]
    hz = os.sysconf("SC_CLK_TCK")
    ap = autoplay(int(windows * win_s / 60.0) + 2)
    per = {"on": {}, "off": {}}
    try:
        sleep(10)
        for i in range(windows):
            mode = "on" if i % 2 == 0 else "off"
            send("hrtf " + mode)
            sleep(1.0)
            a, t0 = thread_cpu(pid), time.time()
            sleep(win_s)
            b, t1 = thread_cpu(pid), time.time()
            for tid, (name, ticks) in b.items():
                if tid in a:
                    c = per[mode].setdefault(tid, [name, 0.0, 0.0])
                    c[1] += (ticks - a[tid][1]) * 1000.0 / hz
                    c[2] += t1 - t0
    finally:
        send("hrtf on")
        ap.terminate()
        subprocess.run([H + "/scripts/stopauto.sh"], capture_output=True)
    rate = lambda m, tid: per[m][tid][1] / max(per[m][tid][2], 1e-9) if tid in per[m] else 0.0
    tids = sorted(set(per["on"]) | set(per["off"]), key=lambda t: -(rate("on", t) + rate("off", t)))
    log("CPU per thread id, ms per second, HRTF on / off (top 12):")
    for t in tids[:12]:
        nm = (per["on"].get(t) or per["off"].get(t))[0]
        print("   %-8s %-16s %7.1f / %7.1f   %+6.1f" % (t, nm, rate("on", t), rate("off", t), rate("on", t) - rate("off", t)), flush=True)
    tot_on = sum(rate("on", t) for t in tids)
    tot_off = sum(rate("off", t) for t in tids)
    log("whole game: %.0f / %.0f ms per second (%+.1f)" % (tot_on, tot_off, tot_on - tot_off))


# ------------------------------------------------------------------------------------------------ the load check, the soak
PERF_HOLD = re.compile(r"perf hold '([^']*)' (\d\d:\d\d:\d\d\.\d+) -> (\d\d:\d\d:\d\d\.\d+) \(([^)]*)\) ([\d.]+) s")


def wall(hms):
    """HH:MM:SS.mmm today -> unix time."""
    h, m, rest = hms.split(":")
    base = time.mktime(time.strptime(time.strftime("%Y-%m-%d") + " 00:00:00", "%Y-%m-%d %H:%M:%S"))
    return base + int(h) * 3600 + int(m) * 60 + float(rest)


def loads_between(t_a, t_b):
    out = []
    for m in PERF_HOLD.finditer(bep_log()):
        a, b = wall(m.group(2)), wall(m.group(3))
        if b >= t_a and a <= t_b:
            out.append(["%s (%s)" % (m.group(1), m.group(4)), a, b])
    return out


def early_sound(probe_mode, tag, timeout=4000):
    """While session.start boots the game in another thread: as soon as the game's stream is on the test sink and the mod
    answers, mute the game, start the head-locked tone probe and the recorder, and turn the sound up (the boot's later
    loads are recorded). Returns the recorder and its wall-clock start."""
    t_end = time.time() + timeout
    pid = None
    while time.time() < t_end and not pid:
        guard()
        p = hof2env.game_pids()
        pid = p[0] if p else None
        time.sleep(0.5)
    if not pid:
        raise SystemExit("no game started")
    on = ""
    for _ in range(120):
        on = sink("game", str(pid)).stdout.strip()
        if on:
            break
        time.sleep(0.5)
    result("safety: the game's sound stream is on the test sink (boot)", on == SINK, "pid %d -> %s" % (pid, on or "no stream"))
    if on != SINK:
        subprocess.run([H + "/scripts/stop.sh"], capture_output=True)
        raise SystemExit("the game's stream is not on %s - stopped it" % SINK)
    for _ in range(240):
        try:
            if cmd.send("state?", 2):
                break
        except SystemExit:
            time.sleep(0.5)
    log("mute:", send("hrtf mute 1"))
    pr = ""
    for _ in range(120):   # the ears (VRAudio's listener or the game's camera) exist a moment after the mod answers
        pr = send("hrtf probe 60 0 2 0 %s tone follow" % probe_mode)
        if pr.startswith("probe"):
            break
        time.sleep(0.5)
    log("probe:", pr)
    rec = Rec(tag)
    t0 = time.time()
    log("sound:", send("hrtf sound 1"))
    return rec, t0


def round_trip(phases):
    """Map -> a fight (combat_fixture --fixture thieves --god: the dev jump) -> the map (leave_fight.py)."""
    st = send("state?")
    if "GameState_Map" not in st:   # (combat_fixture would reboot the game to reach a map)
        log("round trip: not on the map (%s) - skipped" % st[:40])
        return False
    phases.append(["to the fight", time.time()])
    r = subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--fixture", "thieves", "--god"], capture_output=True, text=True)
    log("fixture:", (r.stdout.strip().splitlines() or ["-"])[-1])
    phases.append(["fight", time.time()])
    sleep(6)
    phases.append(["back to the map", time.time()])
    r2 = subprocess.run(["python3", H + "/scripts/leave_fight.py", "240"], capture_output=True, text=True)
    log("leave:", (r2.stdout.strip().splitlines() or ["-"])[-1])
    sleep(4)
    phases.append(["map again", time.time()])
    return r.returncode == 0 and r2.returncode == 0


def round_trip_auto(phases):
    """The table -> a fight the natural way (combat_fixture --god: autoplay until a fight loads, the gate, the showcase) ->
    back to the same map (leave_fight.py)."""
    phases.append(["to the fight", time.time()])
    r = subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--god", "--timeout", "300"], capture_output=True, text=True)
    log("fixture:", (r.stdout.strip().splitlines() or ["-"])[-1])
    phases.append(["fight", time.time()])
    sleep(6)
    phases.append(["back to the map", time.time()])
    r2 = subprocess.run(["python3", H + "/scripts/leave_fight.py", "240"], capture_output=True, text=True)
    log("leave:", (r2.stdout.strip().splitlines() or ["-"])[-1])
    sleep(4)
    phases.append(["map again", time.time()])
    return r.returncode == 0 and r2.returncode == 0


RT = {}
PERF = {}


def perf_lines_between(t_a, t_b):
    """perf 10s lines whose 10 s window lies within [t_a, t_b], by game state: [(avg, p95, p99, miss)]."""
    by = {}
    for m in re.finditer(r"perf 10s @(\d\d:\d\d:\d\d\.\d+) \[([^\]]*)\] \d+ frames [\d.]+s: avg ([\d.]+) p50 [\d.]+ p95 ([\d.]+) p99 ([\d.]+) max [\d.]+ ms \| .*?miss ([\d.]+)%", bep_log()):
        b = wall(m.group(1))
        if t_a <= b and b + 10.5 <= t_b:
            by.setdefault(m.group(2).replace("GameState_", ""), []).append((float(m.group(3)), float(m.group(4)), float(m.group(5)), float(m.group(6))))
    return by


def stage_rt(mode, tag):
    """One recorded round trip with HRTF on or off: the head-locked tone probe (it follows the listener in use), the
    game's own sounds muted at their sources and at the mixer."""
    send("hrtf " + mode)
    if mode == "on":
        for _ in range(40):
            if send("hrtf status").startswith("ON"):
                break
            time.sleep(0.5)
    log("mute:", send("hrtf mute 1"))
    log("probe:", send("hrtf probe 60 0 2 0 %s tone follow" % ("hrtf" if mode == "on" else "pan")))
    log("trace:", send("hrtf trace 1"))
    sleep(1)
    rec = Rec("%s_rt%s" % (tag, mode))
    t0 = time.time()
    ph = [["map (HRTF %s)" % mode, t0]]
    ok = round_trip_auto(ph)
    t1 = time.time()
    RT[mode] = (ok, finish_rec(rec, t0, ph, "%s_rt%s" % (tag, mode)))
    PERF[mode] = perf_lines_between(t0, t1)
    send("hrtf trace 0")
    send("hrtf probe stop")
    send("hrtf mute 0")
    # the listeners around each dropout or glitch (hrtf trace: logged at the frame's end whenever they change)
    tr = [(wall(m.group(1)), m.group(0)) for m in re.finditer(r"hrtf trace (\d\d:\d\d:\d\d\.\d+) .*", bep_log())]
    tr = [x for x in tr if t0 - 1 <= x[0] <= t1 + 1]
    log("trace: %d changes in the round trip" % len(tr))
    shown = set()
    for e in (RT[mode][1] or {}).get("events", []):
        we = t0 + e["t"]
        near = [x for x in tr if we - 2.0 <= x[0] <= we + 2.0]
        for w, line in near:
            if line not in shown:
                shown.add(line)
                log("  %+7.3f s from the %s at %.3f s: %s" % (w - we, e["kind"], e["t"], line[:300]))
    if "on" in PERF and "off" in PERF:
        import statistics as S
        out = []
        for st in sorted(set(PERF["on"]) & set(PERF["off"])):
            a, b = PERF["on"][st], PERF["off"][st]
            if len(a) >= 2 and len(b) >= 2:
                out.append((st, S.median(x[0] for x in a) - S.median(x[0] for x in b), S.median(x[2] for x in a) - S.median(x[2] for x in b), len(a), len(b),
                            S.median(x[0] for x in a), S.median(x[0] for x in b)))
        for o in out:
            log("perf %s: HRTF on %.2f / off %.2f ms avg (%+.2f), p99 %+.2f ms (%d / %d lines)" % (o[0], o[5], o[6], o[1], o[2], o[3], o[4]))
        if out:
            result("perf: the same map and fights, HRTF on vs off, state by state: avg within 0.5 ms, p99 within 2 ms",
                   all(d <= 0.5 and p <= 2.0 for _, d, p, _, _, _, _ in out), "; ".join("%s %+.2f/%+.2f" % (o[0], o[1], o[2]) for o in out))
    if "on" in RT and "off" in RT:
        on, off = RT["on"][1], RT["off"][1]
        result("loadcheck: both round trips reached the fight and came back", RT["on"][0] and RT["off"][0], "on %s, off %s" % (RT["on"][0], RT["off"][0]))
        if on and off:
            def cnt(r, inload):
                return sum(1 for e in r["events"] if (bool(e["load"]) == inload))
            result("loadcheck: no clicks or dropouts at the loads with HRTF on beyond HRTF off on the same path",
                   cnt(on, True) <= cnt(off, True), "in loads (+-1 s): on %d, off %d; elsewhere: on %d, off %d; on: %s; off: %s" % (
                       cnt(on, True), cnt(off, True), cnt(on, False), cnt(off, False),
                       "; ".join("%s %.1f s %.0f ms %s" % (e["kind"], e["t"], e["ms"], e["load"] or e["phase"]) for e in on["events"][:6]) or "none",
                       "; ".join("%s %.1f s %.0f ms %s" % (e["kind"], e["t"], e["ms"], e["load"] or e["phase"]) for e in off["events"][:6]) or "none"))


def finish_rec(rec, t0, phases, tag):
    rec.stop()
    t1 = time.time()
    meta = {"rec_wall0": t0, "rec_wall1": t1, "phases": phases, "loads": loads_between(t0, t1)}
    mp = os.path.join(OUT, tag + ".meta.json")
    json.dump(meta, open(mp, "w"), indent=1)
    r = subprocess.run(["python3", "-I", H + "/scripts/hrtf_clicks.py", rec.path, mp, "--out", os.path.join(OUT, tag + ".clicks.json")],
                       capture_output=True, text=True)
    print(r.stdout + r.stderr, flush=True)
    try:
        return json.load(open(os.path.join(OUT, tag + ".clicks.json")))
    except Exception:
        return None


def flow_loadcheck(tag, snapshot):
    """The scene-load crackle check: HRTF off at boot (env 3's settings file), recorded from early in the boot through a
    table -> fight -> table round trip; then HRTF on and the same round trip recorded again."""
    th_err = []

    def boot():
        try:
            session.start(to="table", snapshot=snapshot)
        except BaseException as e:   # (SystemExit from a wait)
            th_err.append(repr(e))
    th = threading.Thread(target=boot, daemon=True)
    th.start()
    rec, t0 = early_sound("pan", tag + "_off")
    phases = [["boot", t0]]
    th.join()
    if th_err:
        rec.stop()
        raise SystemExit("boot: " + th_err[0])
    phases.append(["map", time.time()])
    ok1 = round_trip(phases)
    off = finish_rec(rec, t0, phases, tag + "_off")
    log("hrtf on:", send("hrtf on"))
    for _ in range(40):
        if send("hrtf status").startswith("ON"):
            break
        time.sleep(0.5)
    log("probe:", send("hrtf probe 60 0 2 0 hrtf tone follow"))
    sleep(1)
    rec = Rec(tag + "_on")
    t0 = time.time()
    phases = [["map (HRTF on)", t0]]
    ok2 = round_trip(phases)
    on = finish_rec(rec, t0, phases, tag + "_on")
    send("hrtf probe stop")
    send("hrtf mute 0")
    result("loadcheck: both round trips reached the fight and came back", ok1 and ok2, "off %s, on %s" % (ok1, ok2))
    if off and on:
        def cnt(r, inload):
            return sum(1 for e in r["events"] if (bool(e["load"]) == inload))
        result("loadcheck: no clicks or dropouts at the loads with HRTF on beyond HRTF off on the same path",
               cnt(on, True) <= cnt(off, True),
               "in loads (+-1 s): on %d, off %d (the off run includes the boot); elsewhere: on %d, off %d; on: %s" % (
                   cnt(on, True), cnt(off, True), cnt(on, False), cnt(off, False),
                   "; ".join("%s %.1f s %s" % (e["kind"], e["t"], e["load"] or e["phase"]) for e in on["events"][:6]) or "none"))


def flow_combo(tag, snapshot, soak_min):
    """All three of the coordinator's 2026-10-14 checks in one game turn: the boot with HRTF on recorded (tone probe); the
    quirk experiment while the session is fresh; a table -> fight -> table round trip with HRTF on, then the same with it
    off (the baseline), both recorded; then the soak with HRTF on."""
    th_err = []

    def boot():
        try:
            session.start(to="table", snapshot=snapshot)
        except BaseException as e:
            th_err.append(repr(e))
    th = threading.Thread(target=boot, daemon=True)
    th.start()
    rec, t0 = early_sound("hrtf", tag + "_bootOn")
    th.join()
    b = finish_rec(rec, t0, [["boot (HRTF on)", t0], ["map", time.time()]], tag + "_bootOn")
    if th_err:
        raise SystemExit("boot: " + th_err[0])
    if b:
        result("boot with HRTF on, recorded: no clicks or dropouts in its loads", b["in_loads"] == 0,
               "%d events in loads, %d elsewhere: %s" % (b["in_loads"], len(b["events"]) - b["in_loads"],
                                                         "; ".join("%s %.1f s %s" % (e["kind"], e["t"], e["load"] or e["phase"]) for e in b["events"][:6]) or "none"))
    send("hrtf probe stop")
    sleep(1)
    stage_quirk(tag + "_quirk")
    res = {}
    for mode in ("on", "off"):
        send("hrtf " + mode)
        if mode == "on":
            for _ in range(40):
                if send("hrtf status").startswith("ON"):
                    break
                time.sleep(0.5)
        log("probe:", send("hrtf probe 60 0 2 0 %s tone follow" % ("hrtf" if mode == "on" else "pan")))
        sleep(1)
        rec = Rec("%s_rt%s" % (tag, mode))
        t0 = time.time()
        ph = [["map (HRTF %s)" % mode, t0]]
        ok = round_trip(ph)
        res[mode] = (ok, finish_rec(rec, t0, ph, "%s_rt%s" % (tag, mode)))
    send("hrtf probe stop")
    send("hrtf on")
    send("hrtf mute 0")
    on, off = res["on"][1], res["off"][1]
    result("loadcheck: both round trips reached the fight and came back", res["on"][0] and res["off"][0], "on %s, off %s" % (res["on"][0], res["off"][0]))
    if on and off:
        def cnt(r, inload):
            return sum(1 for e in r["events"] if (bool(e["load"]) == inload))
        result("loadcheck: no clicks or dropouts at the loads with HRTF on beyond HRTF off on the same path",
               cnt(on, True) <= cnt(off, True), "in loads (+-1 s): on %d, off %d; elsewhere: on %d, off %d; on: %s; off: %s" % (
                   cnt(on, True), cnt(off, True), cnt(on, False), cnt(off, False),
                   "; ".join("%s %.1f s %s" % (e["kind"], e["t"], e["load"] or e["phase"]) for e in on["events"][:5]) or "none",
                   "; ".join("%s %.1f s %s" % (e["kind"], e["t"], e["load"] or e["phase"]) for e in off["events"][:5]) or "none"))
    soak_monitor(soak_min, tag)


def soak_monitor(minutes, tag):
    pid = hof2env.game_pids()[0]
    stop = threading.Event()

    def quiet():
        while not stop.is_set():
            try:
                with open(os.path.join(VRS, "quiet"), "a"):
                    os.utime(os.path.join(VRS, "quiet"), None)
            except OSError:
                pass
            stop.wait(30)
    threading.Thread(target=quiet, daemon=True).start()
    start_log = len(bep_log())
    ap = autoplay(minutes + 1)
    samples = []
    t_end = time.time() + minutes * 60
    alive = True
    try:
        while time.time() < t_end:
            sleep(30)
            if not os.path.exists("/proc/%d" % pid):
                alive = False
                log("SOAK: the game is gone")
                break
            m = send("hrtf mem", 10)
            st = send("state?", 6)[:40]
            mm = re.search(r"rss (\d+) kB \| flagged (\d+) \| own (\d+) \| exempt (\d+) \| pending (\d+) \| paramsOk (\d+) \| plays (\d+) \| hook errors (\d+)", m)
            if mm:
                samples.append([time.time()] + [int(v) for v in mm.groups()] + [st])
                log("soak: %s | %s" % (st, m))
            if ap.poll() is not None:
                log("autoplay ended (%s) - restarting it" % ap.returncode)
                ap = autoplay(max(1, int((t_end - time.time()) / 60) + 1))
    finally:
        stop.set()
        ap.terminate()
        subprocess.run([H + "/scripts/stopauto.sh"], capture_output=True)
        try:
            os.remove(os.path.join(VRS, "quiet"))
        except OSError:
            pass
    txt = bep_log()[start_log:]
    ex = len(re.findall(r"\bException\b", txt))
    pl = open(os.path.join(H, "logs", "player-latest.log"), errors="replace").read()
    crash = [l for l in pl.splitlines() if "Receiving unhandled" in l or "Crash!!!" in l or "SIGSEGV" in l]
    result("soak: the game ran %d minutes with HRTF on, no native crash" % minutes, alive and not crash, crash[0][:120] if crash else "alive")
    result("soak: 0 exceptions in the mod's log", ex == 0, "%d" % ex)
    if len(samples) >= 4:
        rss = [x[1] for x in samples]
        fl = [x[2] for x in samples]
        n = len(rss)
        q = max(1, n // 4)
        first, last = sorted(rss[:q])[q // 2], sorted(rss[-q:])[q // 2]
        log("soak RSS (MB): first quarter median %.0f, last quarter median %.0f, min %.0f, max %.0f; flagged min %d max %d last %d; hook errors %d" % (
            first / 1024, last / 1024, min(rss) / 1024, max(rss) / 1024, min(fl), max(fl), fl[-1], samples[-1][8]))
        result("soak: what the HRTF holds stays bounded (flagged sources, own, exempt) and 0 hook errors",
               max(fl) < 400 and samples[-1][4] < 400 and samples[-1][8] == 0, "flagged max %d, exempt %d, hook errors %d" % (max(fl), samples[-1][4], samples[-1][8]))
        json.dump(samples, open(os.path.join(OUT, tag + "_soak.json"), "w"))
    # the perf lines of the soak by state (compare with an HRTF-off run's)
    lines = re.findall(r"perf 10s @\S+ \[([^\]]*)\] (\d+) frames [\d.]+s: avg ([\d.]+) p50 [\d.]+ p95 ([\d.]+) p99 ([\d.]+) max [\d.]+ ms \| .*?miss ([\d.]+)%", txt)
    import statistics as S
    by = {}
    for st, fr, avg, p95, p99, miss in lines:
        by.setdefault(st.replace("GameState_", ""), []).append((float(avg), float(p95), float(p99), float(miss)))
    for st, v in sorted(by.items()):
        log("soak perf %-12s n=%3d  avg %.2f  p95 %.2f  p99 %.2f  miss %.1f%%" % (st, len(v), S.median(x[0] for x in v), S.median(x[1] for x in v),
                                                                             S.median(x[2] for x in v), S.median(x[3] for x in v)))
    json.dump(by, open(os.path.join(OUT, tag + "_soak_perf.json"), "w"))


def stage_quirk(tag, rounds=6, variants=""):
    """The 3 dB quirk experiment: `hrtf quirk` (VR/HrtfProbe.cs) plays the variants round-robin at azimuth 60; which bursts
    come out ~3 dB below the rest, by variant and by round."""
    import numpy as np
    import hrtf_analyze as A
    start = len(bep_log())
    rec = Rec(tag)
    try:
        r = send(("hrtf quirk %s %d %s" % (tag, rounds, variants)).strip(), 20)
        log("quirk:", r)
        m = wait_log(r"hrtf quirk '%s' done" % re.escape(tag), 300, start)
        time.sleep(0.4)
    finally:
        rec.stop()
    sch = json.load(open(os.path.join(OUT, tag + ".json")))
    rate, x = A.read_wav(rec.path)
    x = x[:, :2]
    pos, score = A.find_chirp(x.mean(axis=1), rate, sch, first=True)
    rows = []
    for b in sch["bursts"]:
        st = pos + int(round((b["t"] - sch["chirp_t"]) * rate))
        a0 = max(0, st - int(0.01 * rate))
        seg = x[a0:st + int(0.40 * rate)]
        env = np.convolve(np.max(np.abs(seg), axis=1), np.ones(48) / 48, "same")
        on = np.nonzero(env > 0.1 * env.max())[0]
        if not len(on):
            rows.append(dict(b, db=None))
            continue
        o = a0 + on[0]
        w = x[o + int(0.06 * rate):o + int(0.24 * rate)]
        rows.append(dict(b, db=10 * math.log10(float((w ** 2).mean() * 2) + 1e-30), onset_ms=1000.0 * (o - st) / rate))
    vals = sorted(r["db"] for r in rows if r["db"] is not None)
    ref = vals[int(0.9 * (len(vals) - 1))] if vals else 0
    log("quirk: chirp score %.2f; reference (90th percentile) %.2f dB" % (score, ref))
    by = {}
    for r in rows:
        d = (r["db"] - ref) if r["db"] is not None else None
        r["delta"] = d
        by.setdefault(r["variant"], []).append(r)
    for v, rr in by.items():
        q = [r for r in rr if r["delta"] is not None and -3.6 <= r["delta"] <= -2.4]
        log("quirk %-10s quiet %d/%d  deltas by round: %s" % (v, len(q), len(rr), " ".join("%+.1f" % r["delta"] if r["delta"] is not None else "-" for r in rr)))
    json.dump(rows, open(os.path.join(OUT, tag + ".quirk.json"), "w"), indent=1)
    return by


def stage_drill():
    d = os.path.join(hof2env.G, "Hand of Fate 2_Data", "Plugins", "x86_64")
    for f in ("libaudioplugin_phonon.so", "libphonon.so"):
        p = os.path.join(d, f)
        if os.path.exists(p):
            os.remove(p)
    session.start(to="title", install=False)
    st = send("hrtf status")
    log("hrtf status without the plugin files:", st)
    result("drill: plugin files missing -> off with the reason 'plugin missing'", "plugin missing" in st, st[:200])
    n = bep_log().count("Hrtf: not used")
    result("drill: the reason logged once (no log spam)", n == 1, "%d lines" % n)


# ------------------------------------------------------------------------------------------------ main
def main():
    a = sys.argv[1:]
    opt = {"--stages": "boot,sweep,dealer", "--snapshot": "magician", "--to": "table", "--perf-windows": "8", "--perf-window-s": "40", "--soak-min": "30", "--quirk-rounds": "6", "--quirk-variants": "", "--tag": "t" + time.strftime("%H%M")}
    i = 0
    while i < len(a):
        if a[i] in opt and i + 1 < len(a):
            opt[a[i]] = a[i + 1]
            i += 2
        else:
            raise SystemExit(__doc__)
    stages = opt["--stages"].split(",")
    tag = opt["--tag"]
    why = user_in_vr() or reserved()
    if why:
        log("NOT STARTING: %s" % why)
        sys.exit(4)
    os.makedirs(OUT, exist_ok=True)
    wp0 = open(WP_STATE).read() if os.path.exists(WP_STATE) else None
    if "drill" in stages:
        try:
            stage_drill()
        finally:
            subprocess.run([H + "/scripts/stop.sh"], capture_output=True)
            subprocess.run([H + "/scripts/install.sh"], capture_output=True)
        stages = [s for s in stages if s != "drill"]
        if not stages:
            return finish(wp0)
    r = sink("up")
    log(r.stdout.strip() or r.stderr.strip())
    if r.returncode != 0:
        sys.exit(1)
    unlocked = False
    if stages[0] in ("loadcheck", "soak", "combo"):
        # flows that record from early in the boot: they start the game themselves
        os.environ["PULSE_SINK"] = SINK
        try:
            if stages[0] == "loadcheck":
                unlocked = True
                flow_loadcheck(tag + "_load", opt["--snapshot"])
            elif stages[0] == "combo":
                unlocked = True
                flow_combo(tag, opt["--snapshot"], int(opt["--soak-min"]))
            else:
                th_err = []

                def boot():
                    try:
                        session.start(to="table", snapshot=opt["--snapshot"])
                    except BaseException as e:
                        th_err.append(repr(e))
                th = threading.Thread(target=boot, daemon=True)
                th.start()
                rec, t0 = early_sound("hrtf", tag + "_bootOn")
                unlocked = True
                th.join()
                ph = [["boot (HRTF on)", t0]]
                b = finish_rec(rec, t0, ph + [["map", time.time()]], tag + "_bootOn")
                if th_err:
                    raise SystemExit("boot: " + th_err[0])
                if b:
                    result("soak boot: the boot with HRTF on, recorded: clicks or dropouts in its loads", b["in_loads"] == 0,
                           "%d events in loads, %d elsewhere: %s" % (b["in_loads"], len(b["events"]) - b["in_loads"],
                                                                     "; ".join("%s %.1f s %s" % (e["kind"], e["t"], e["load"] or e["phase"]) for e in b["events"][:6]) or "none"))
                send("hrtf probe stop")
                send("hrtf mute 0")
                for st in stages[1:]:
                    if st == "quirk":
                        stage_quirk(tag + "_quirk")
                    elif st.startswith("soak"):
                        soak_monitor(int(opt["--soak-min"]), tag)
            ex = len(re.findall(r"\bException\b", bep_log()))
            result("no exceptions in the mod's log", ex == 0, "%d" % ex)
        except UserFirst as e:
            log("STOPPING EVERYTHING: %s" % e)
        finally:
            if unlocked:
                try:
                    cmd.send("hrtf sound 0", 5)
                except SystemExit:
                    pass
            subprocess.run([H + "/scripts/stop.sh"], capture_output=True)
            r = sink("down")
            log(r.stdout.strip(), r.stderr.strip())
            result("cleanup: the test sink is gone, the default sink as before", r.returncode == 0, r.stdout.strip()[-120:])
            finish(wp0)
        return
    try:
        os.environ["PULSE_SINK"] = SINK
        snap = opt["--snapshot"]
        if opt["--to"] == "combat":
            session.start(to="table", snapshot=snap)
            guard()
            subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--god"], check=True)
        else:
            session.start(to=opt["--to"], snapshot=snap)
        guard()
        pid = hof2env.game_pids()[0]
        on = sink("game", str(pid)).stdout.strip()
        result("safety: the game's sound stream is on the test sink", on == SINK, "pid %d -> %s" % (pid, on or "no stream"))
        if on != SINK:
            raise SystemExit("the game's stream is not on %s - not turning the sound up" % SINK)
        log("sound:", send("hrtf sound 1"))
        unlocked = True
        for s in stages:
            log("---- stage", s)
            if s == "boot":
                stage_boot()
            elif s == "sweep":
                stage_sweep(tag + "_sweep")
            elif s == "sweepx":
                # the experiment: the first sweep of the session with the plugin's distance attenuation off for the HRTF bursts
                p0 = plays()
                rep = record(tag + "_sweepx", "hrtf sweep %s_sweepx ab noatt" % tag, r"hrtf sweep '%s_sweepx' done" % re.escape(tag), 90, table=True)
                lv = sorted((r["az"], r["el"], round(r["energy_db"], 1)) for r in (rep or {}).get("passes", [{}])[0].get("rows", []) if r.get("audible"))
                log("sweepx HRTF levels (az, el, L+R dB): %s" % lv)
            elif s == "fight":
                guard()
                r = subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--god"], capture_output=True, text=True)
                print(r.stdout[-800:], flush=True)
                result("fight: a live fight reached (combat_fixture --god)", r.returncode == 0, r.stdout.strip().splitlines()[-1][:120] if r.stdout.strip() else "-")
            elif s == "sweep2":
                stage_sweep2(tag + "_sweep2")
            elif s == "dealer":
                stage_dealer(tag + "_dealer")
            elif s in ("enemy", "enemy2"):
                stage_enemy(tag + "_" + s)
            elif s == "reset":
                stage_reset()
            elif s == "lazy":
                stage_lazy()
            elif s == "cpu":
                stage_cpu()
            elif s == "quirk":
                stage_quirk(tag + "_quirk", int(opt["--quirk-rounds"]), opt["--quirk-variants"])
            elif s in ("rton", "rtoff"):
                stage_rt(s[2:], tag)
            elif s == "watch":
                stage_watch()
            elif s == "vrset":
                # the VR settings page's 3D sound row by touch (playtest.py's vrsettings stage, which also checks it)
                r = subprocess.run(["python3", H + "/scripts/playtest.py", "--skip-start", "--only", "vrsettings"], capture_output=True, text=True)
                print(r.stdout[-3000:], flush=True)
                m = re.search(r"(PASS|FAIL)\S*\s+vrsettings_hrtf_row\s*(.*)", r.stdout)
                result("vrset: the 3D sound row on the VR settings page switches HRTF by touch", bool(m) and m.group(1) == "PASS", m.group(2)[:200] if m else "no result line")
            elif s == "perf":
                stage_perf(int(opt["--perf-windows"]), float(opt["--perf-window-s"]))
        ex = len(re.findall(r"\bException\b", bep_log()))
        result("no exceptions in the mod's log", ex == 0, "%d" % ex)
    except UserFirst as e:
        log("STOPPING EVERYTHING: %s" % e)
    finally:
        if unlocked:
            try:
                cmd.send("hrtf sound 0", 5)
            except SystemExit:
                pass
        subprocess.run([H + "/scripts/stop.sh"], capture_output=True)
        r = sink("down")
        log(r.stdout.strip(), r.stderr.strip())
        result("cleanup: the test sink is gone, the default sink as before", r.returncode == 0, r.stdout.strip()[-120:])
        finish(wp0)


def finish(wp0):
    if wp0 is not None:
        time.sleep(2)
        wp1 = open(WP_STATE).read()
        new = sorted(set(wp1.splitlines()) - set(wp0.splitlines()))
        result("cleanup: nothing new in WirePlumber's stream state", not new, "; ".join(new)[:300])
    bad = [r for r in RESULTS if not r[1]]
    log("RESULT: %d checks, %d failed" % (len(RESULTS), len(bad)))
    for n, ok, d in bad:
        log("  FAILED: %s  %s" % (n, d))
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()

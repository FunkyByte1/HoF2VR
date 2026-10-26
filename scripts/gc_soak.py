#!/usr/bin/env python3
"""The Boehm heap settings, a long soak (release prep 2026-10-13, item 4; gc_probe.py's 166 s were too short to decide).

  gc_soak.py --minutes 32 [--label NAME] [--boot [--no-install]]

Run it twice, once as usual and once with the collector's settings in the environment of the launch (--boot passes this
script's environment on to session.py / run.sh; --no-install keeps the DLL installed for the first run):
  python3 scripts/gc_soak.py --minutes 32 --boot --label default
  GC_INITIAL_HEAP_SIZE=1073741824 GC_FREE_SPACE_DIVISOR=1 python3 scripts/gc_soak.py --minutes 32 --boot --label heap1g
The same cycle every time, in the dev save (a fresh dev run first, so both runs start from the same save): the table's
everyday events at the dungeon map (gains and losses, the zoom), a look round the map, a fight (the dev jump, the
campfire, 40 s of the gesture bot in god mode, the last kill, out through the portal), and every third cycle a fresh dev
run (title -> table: the big load). Touches /run/user/1000/vr-session/quiet every 60 s (timing measurements).
Measured from the mod's log since the start: every collection (`perf gc`: its frame's time = the pause, the heap in use),
every frame >= 40 ms (`perf stall`) by place and view, the loading holds (`perf hold`: load times), the 10 s windows'
p99, the exceptions (the mod's log and the game's player log); the game's RSS every 10 s. Results: logs/gc_soak.log.
Stops (exit 3) when the user starts VR (status.json "user") or the game dies.
"""
import json
import os
import re
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H
LOG = H + "/logs/gc_soak.log"
VRS = "/run/user/1000/vr-session"
EVENTS = ["Gain_life", "Gain_gold", "Lose_gold", "Gain_food", "Lose_food", "Max_health_up"]
FIGHTS = ["EL_Ambush_Goblins", "EL_Ambush_Thral"]
REST = "hand r 0.2 1.0 -0.3 0 -60 0; hand l -0.2 1.0 -0.3 0 -60 0"
STOP = threading.Event()
RSS = []   # (t, MB)


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def send(c, t=10):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def st():
    for _ in range(5):
        try:
            return cmd.status()
        except Exception:
            time.sleep(0.2)
    return {}


def state():
    return send("state?", 5)


def user_in_vr():
    try:
        with open(VRS + "/status.json") as f:
            d = json.load(f)
        return bool(d.get("user")) or time.time() - float(d.get("updated", 0)) > 90
    except Exception:
        return True


def background():
    """The quiet flag every 60 s, the game's RSS every 10 s."""
    last_quiet = 0
    while not STOP.is_set():
        now = time.time()
        if now - last_quiet >= 60:
            try:
                with open(VRS + "/quiet", "a"):
                    os.utime(VRS + "/quiet")
            except OSError:
                pass
            last_quiet = now
        for p in hof2env.game_pids():
            try:
                with open("/proc/%d/status" % p) as f:
                    m = re.search(r"VmRSS:\s+(\d+) kB", f.read())
                if m:
                    RSS.append((now, int(m.group(1)) / 1024.0))
            except OSError:
                pass
            break
        STOP.wait(10)


def check_alive():
    if user_in_vr():
        log("the user is in VR (or status.json is stale): stopping the test game")
        subprocess.run([H + "/scripts/stop.sh"], capture_output=True)
        STOP.set()
        sys.exit(3)
    if not hof2env.game_alive():
        log("the game is not running")
        STOP.set()
        sys.exit(3)


def jump_ready():
    return send("jump encounter zz_ready_probe").startswith("no encounter like")


def touch_on():
    """Whatever the table waits for, answered by touch as a player would - an equip question No, a page's first option, its
    Continue; an encounter's dice thrown. Never a button press on the dungeon map (A there walks the hero on)."""
    s0 = st()
    b = s0.get("boards", "").split(" | touch")[0]
    menu = s0.get("menu")
    opts = [i for i, t in re.findall(r"\[(\d+) ([^\]]*)\]", b) if not t.startswith("nav:")]
    # (every touch by its label, found in the game's own frame: a numbered one landed on the map's Inventory plate once the
    # page had closed under it, 06:45)
    if "Dice" in (menu or ""):
        send("dice throw 0 0.5 2.0")
    elif menu == "Inventory":
        send("ptouch nav:Back")
    elif menu == "Choice" and not opts and "nav:Select" in b:
        send("ptouch nav:Select")   # one card of several (a fight's reward): the game's hovered card, as A would
    elif menu == "Choice" and not any(k in b for k in ("nav:Continue", "nav:Back", "nav:No", "nav:Select")):
        send("touch CardChoiceContainer/Card_ r tap")   # a choice with nothing chosen yet: a card touched
    elif menu not in ("Map", None, ""):
        if "nav:No" in b:
            send("ptouch nav:No")
        elif opts:
            send("ptouch opt")
        elif "nav:Continue" in b:
            send("ptouch nav:Continue")
        elif "nav:OK" in b:
            send("ptouch nav:OK")


def settle(timeout=60):
    """Back to a dungeon map that takes jumps (a fight's result page, an equip question, an encounter's dice answered)."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        s = state()
        if "GameState_Map" in s and jump_ready():
            return True
        if "Level_Play" in s:
            send("killall")
            time.sleep(3)
            continue
        if "Level_In" in s:
            send("gate go")
            send("press A 6")   # the campfire's A (the arena waits for it)
            time.sleep(2)
            continue
        if "GameState_Map" in s:
            touch_on()
        time.sleep(2.5)
    return False


def look_round(secs):
    """A player looking over the map and round the table (smooth head turns)."""
    t_end = time.time() + secs
    poses = [(0, -25, 1200), (-35, -15, 1500), (30, -10, 1800), (0, -40, 1200), (0, -5, 1000)]
    i = 0
    yaw0, pitch0 = 0, -10
    while time.time() < t_end:
        y, p, ms = poses[i % len(poses)]
        sim("turn %d %d %d %d %d" % (yaw0, y, pitch0, p, ms))
        yaw0, pitch0 = y, p
        i += 1
        time.sleep(ms / 1000.0 + 2.0)


def table_events():
    for e in EVENTS:
        r = send("scen here " + e)
        if r.startswith("busy"):
            send("scen stop")
            settle(30)
            r = send("scen here " + e)
        time.sleep(9)
        if not settle(40):
            log("   (%s: the map did not come back: %s)" % (e, state()))


def fight(name):
    sim(REST + "; head 0 1.75 0 0 0 0")
    send("gate auto 1.5")
    t0 = time.time()
    started = False
    while time.time() - t0 < 60:
        r = send("jump fight " + name)
        if r.startswith("started"):
            started = True
            break
        settle(10)
    if not started:
        log("   fight %s: the jump was refused (%s)" % (name, state()))
        return False
    for _ in range(40):
        time.sleep(2)
        s = state()
        if "Level_Play" in s:
            break
        if "Level_In" in s:
            send("gate go")
            send("press A 6")   # the campfire's A (the arena waits for it), then the enemies' description
            continue
        touch_on()   # the encounter's page at the table
    if "Level_Play" not in state():
        log("   fight %s: never in the fight (%s)" % (name, state()))
        return False
    send("god on")
    subprocess.run(["python3", H + "/scripts/fight.py", "40"], capture_output=True, text=True, timeout=120)
    send("killall")
    for _ in range(15):
        time.sleep(1)
        if "card (" in st().get("fightExit", ""):
            break
    if "card (" in st().get("fightExit", ""):
        send("exitcard touch")
    else:
        send("jump win")
    t1 = time.time()
    while time.time() - t1 < 90 and "GameState_Map" not in state():
        time.sleep(1)
    sim(REST + "; head 0 1.75 0 0 0 0")
    return settle(60)


def fresh_run():
    sim(REST + "; head 0 1.75 0 0 0 0")
    time.sleep(0.5)
    r = send("scen run Fresh_dev_run")
    t0 = time.time()
    while time.time() - t0 < 240:
        time.sleep(3)
        s = state()
        if "GameState_Map" in s and (send("scen", 5).split(" | ") + ["", ""])[1].startswith("idle") and jump_ready():
            return True
    log("   fresh dev run: not back on the map (%s; %s)" % (state(), r))
    return False


def parse(lines):
    def num(rx, l, d=0.0):
        m = re.search(rx, l)
        return float(m.group(1)) if m else d
    gcs = [l.split("] ", 1)[-1] for l in lines if "] perf gc " in l]
    stalls = [l.split("] ", 1)[-1] for l in lines if "] perf stall " in l]
    holds = [l.split("] ", 1)[-1] for l in lines if "] perf hold " in l]
    wins = [l.split("] ", 1)[-1] for l in lines if "] perf 10s " in l]
    out = {}
    gc_busy = [num(r"busy (\d+)", l) for l in gcs]
    out["collections"] = len(gcs)
    out["gc_pause_max"] = max(gc_busy) if gc_busy else 0
    out["gc_pause_median"] = sorted(gc_busy)[len(gc_busy) // 2] if gc_busy else 0
    out["gc_ge40"] = sum(1 for b in gc_busy if b >= 40)
    out["gc_worst"] = sorted(gcs, key=lambda l: -num(r"busy (\d+)", l))[:3]
    used = [num(r"used (\d+) MB", l) for l in gcs]
    out["used_mb"] = (min(used), max(used)) if used else (0, 0)

    def place(l):
        m = re.search(r"\| (GameState_\w+|\S*) \|", l)
        s = (m.group(1) if m else "?").replace("GameState_", "") or "-"
        v = re.search(r"view (\w+)", l)
        return "%s/%s" % (s, v.group(1) if v else "?")
    by = {}
    for l in stalls:
        k = place(l)
        e = by.setdefault(k, [0, 0, 0])
        e[0] += 1
        e[1] += 1 if re.search(r"\) gc", l) else 0
        e[2] = max(e[2], num(r"busy (\d+)", l))
    out["stalls"] = len(stalls)
    out["stalls_gc"] = sum(v[1] for v in by.values())
    out["stalls_by_place"] = by
    out["stalls_open"] = sum(v[0] for k, v in by.items() if k.endswith("/open"))
    out["stalls_open_gc"] = sum(v[1] for k, v in by.items() if k.endswith("/open"))
    hk = {}
    for l in holds:
        m = re.search(r"perf hold '(\w+)' .*?\((.*?)\) ([\d.]+) s", l)
        if not m:
            continue
        k = "%s %s" % (m.group(1), re.sub(r"\s+", " ", m.group(2)))
        hk.setdefault(k, []).append(float(m.group(3)))
    out["holds"] = hk
    p99 = {}
    for l in wins:
        m = re.search(r"\[(GameState_\w+|[^\]]*)\].*?p99 ([\d.]+)", l)
        if m:
            p99.setdefault(m.group(1).replace("GameState_", ""), []).append(float(m.group(2)))
    out["p99"] = {k: (sorted(v)[len(v) // 2], max(v), len(v)) for k, v in p99.items()}
    out["gc_windows"] = sum(int(num(r"\| gc (\d+)", l)) for l in wins)
    return out


def main():
    a = sys.argv[1:]
    minutes = float(a[a.index("--minutes") + 1]) if "--minutes" in a else 32
    label = a[a.index("--label") + 1] if "--label" in a else ("heap " + os.environ["GC_INITIAL_HEAP_SIZE"] if "GC_INITIAL_HEAP_SIZE" in os.environ else "default")
    open(LOG, "a").close()
    gcenv = " ".join("%s=%s" % (k, os.environ[k]) for k in ("GC_INITIAL_HEAP_SIZE", "GC_FREE_SPACE_DIVISOR", "GC_MAXIMUM_HEAP_SIZE") if k in os.environ)
    log("==== gc soak '%s' (%s), %.0f min" % (label, gcenv or "no GC settings", minutes))
    if user_in_vr():
        log("the user is in VR (or status.json is missing/stale): not starting")
        sys.exit(3)
    if "--boot" in a:
        r = subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"] +
                           (["--no-install"] if "--no-install" in a else []), capture_output=True, text=True, timeout=7200)
        if r.returncode != 0:
            log("session start failed: %s" % (r.stdout + r.stderr).strip().splitlines()[-3:])
            sys.exit(2)
    pids = hof2env.game_pids()
    if not pids:
        log("no test game of this environment is running")
        sys.exit(2)
    seen = " ".join(kv.decode() for kv in (hof2env._environ(pids[0]) or []) if kv.startswith((b"GC_INITIAL_HEAP_SIZE=", b"GC_FREE_SPACE_DIVISOR=")))
    log("game pid %d, its GC settings: %s" % (pids[0], seen or "none"))
    threading.Thread(target=background, daemon=True).start()
    send("cfg Scenarios true")
    send("gate auto 1.5")
    sim(REST + "; head 0 1.75 0 0 0 0")
    plogs = sorted((f for f in os.listdir(H + "/logs") if f.startswith("player-") and f.endswith(".log")),
                   key=lambda f: os.path.getmtime(H + "/logs/" + f))
    plog = H + "/logs/" + plogs[-1] if plogs else None
    with open(hof2env.GLOG, errors="replace") as f:
        n0 = sum(1 for _ in f)
    p0 = 0
    if plog:
        with open(plog, errors="replace") as f:
            p0 = sum(1 for _ in f)
    t0 = time.time()
    # the same save for both runs: a fresh dev run (the dev save's mid-run base) first
    if not fresh_run():
        log("no fresh dev run: the soak goes on in the test save")
    cyc = 0
    counts = {"events": 0, "fights": 0, "fights_ok": 0, "reloads": 1}
    while time.time() - t0 < minutes * 60:
        cyc += 1
        check_alive()
        table_events()
        counts["events"] += len(EVENTS)
        check_alive()
        look_round(20)
        check_alive()
        counts["fights"] += 1
        if fight(FIGHTS[cyc % len(FIGHTS)]):
            counts["fights_ok"] += 1
        check_alive()
        if cyc % 3 == 0 and time.time() - t0 < minutes * 60 - 60:
            fresh_run()
            counts["reloads"] += 1
        log("cycle %d done at %.1f min (%s)" % (cyc, (time.time() - t0) / 60, state()))
    STOP.set()
    dur = (time.time() - t0) / 60
    time.sleep(1)
    with open(hof2env.GLOG, errors="replace") as f:
        lines = f.read().splitlines()[n0:]
    pexc = 0
    if plog:
        with open(plog, errors="replace") as f:
            pexc = sum(1 for l in f.read().splitlines()[p0:] if "Exception:" in l)
    exc = sum(1 for l in lines if "Exception" in l)
    errs = sum(1 for l in lines if l.startswith("[Error"))
    o = parse(lines)
    rss = [m for _, m in RSS]
    log("RESULT '%s' %.1f min, %d cycles (%d table events, %d/%d fights, %d loads of the dev run)" % (
        label, dur, cyc, counts["events"], counts["fights_ok"], counts["fights"], counts["reloads"]))
    log("  collections %d (10 s windows: %d); pause (the frame's busy time) median %.0f ms, longest %.0f ms, >= 40 ms %d; heap in use after one %.0f-%.0f MB" % (
        o["collections"], o["gc_windows"], o["gc_pause_median"], o["gc_pause_max"], o["gc_ge40"], o["used_mb"][0], o["used_mb"][1]))
    for l in o["gc_worst"]:
        log("    " + l[:170])
    log("  frames >= 40 ms: %d (%d with a collection); in plain view %d (%d with a collection)" % (
        o["stalls"], o["stalls_gc"], o["stalls_open"], o["stalls_open_gc"]))
    for k, v in sorted(o["stalls_by_place"].items(), key=lambda kv: -kv[1][0]):
        log("    %-28s %3d (gc %d), worst %.0f ms" % (k, v[0], v[1], v[2]))
    log("  RSS %.0f MB at the start, max %.0f, at the end %.0f (%d samples)" % (rss[0] if rss else 0, max(rss) if rss else 0, rss[-1] if rss else 0, len(rss)))
    for k, v in sorted(o["holds"].items()):
        log("  load %-48s n %2d  mean %.1f s  max %.1f s" % (k[:48], len(v), sum(v) / len(v), max(v)))
    for k, v in sorted(o["p99"].items()):
        log("  p99 %-20s median %.1f ms, max %.1f ms (%d windows)" % (k, v[0], v[1], v[2]))
    log("  exceptions: %d lines in the mod's log, %d in the game's player log; %d error lines" % (exc, pexc, errs))
    with open(H + "/logs/gc_soak-%s.json" % re.sub(r"\W+", "_", label), "w") as f:
        json.dump({"label": label, "env": gcenv, "minutes": dur, "cycles": cyc, "counts": counts, "parse": {k: v for k, v in o.items() if k != "gc_worst"},
                   "rss": RSS, "exceptions": exc, "player_exceptions": pexc, "errors": errs}, f)


if __name__ == "__main__":
    main()

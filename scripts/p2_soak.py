#!/usr/bin/env python3
"""A long Physical-mode stability soak (combat QA, docs/combat-progress.md): autoplay across many fights while a monitor
watches for exceptions, stuck states, overlong fights, the one-live-strike rule and frame stalls.

  p2_soak.py <minutes> [--boot] [--snapshot magician_mid] [--fight-limit 240] [--tag SOAK]

--boot: session.py start --snapshot ... --to table first. Physical mode (`vrcombat physical`), god mode (autoplay sets it).
Every 5 s: the game state, `pcombat?` (maxLive, unguarded, minLead, leadShort), `fight?` (alive); a fight lasting longer
than --fight-limit seconds is ended with `killall` and logged (autoplay's bot can't walk to ranged enemies). At the end:
the mod's perf lines (avg / p99 / stalls per 10 s window) and the exception lines of the game's and the mod's logs.
Results in logs/p2_soak.log; exit 1 if the game died, an exception from the mod appeared, or a state stuck for > 5 min.
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H
LOG = H + "/logs/p2_soak.log"
args = sys.argv[1:]


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def send(c, t=6):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def num(s, key):
    m = re.search(key + r" (-?[\d.]+)", s)
    return float(m.group(1)) if m else None


def main():
    minutes = float(args[0]) if args else 30
    limit = float(args[args.index("--fight-limit") + 1]) if "--fight-limit" in args else 240
    snap = args[args.index("--snapshot") + 1] if "--snapshot" in args else "magician_mid"
    if "--boot" in args:
        r = subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", snap, "--to", "table"], capture_output=True, text=True)
        log("boot: " + ((r.stdout or "").strip().splitlines() or ["?"])[-1])
    log_start = os.path.getsize(hof2env.GLOG) if os.path.exists(hof2env.GLOG) else 0
    send("vrcombat physical")
    send("pcombat? reset")
    auto = subprocess.Popen(["python3", H + "/scripts/autoplay.py", str(int(minutes) + 1)], stdout=open(H + "/logs/p2_soak-autoplay.out", "w"),
                            stderr=subprocess.STDOUT)
    t0 = time.time()
    fights = 0
    killed = 0
    fight_since = None
    state_since = time.time()
    last_state = None
    max_live = 0
    worst_stuck = 0.0
    died = False
    try:
        while time.time() - t0 < minutes * 60:
            if not hof2env.game_alive():
                log("GAME NOT RUNNING")
                died = True
                break
            if '"user": true' in open("/run/user/1000/vr-session/status.json").read():
                log("the user is in VR: stopping")
                break
            st = send("state?", 4)
            s = st.split(" for ")[0] if st else ""
            if s != last_state:
                worst_stuck = max(worst_stuck, time.time() - state_since)
                log("state %s -> %s" % (last_state, s))
                last_state, state_since = s, time.time()
            if "Level_Play" in s:
                if fight_since is None:
                    fight_since = time.time()
                    fights += 1
                    log("fight %d: %s" % (fights, send("fight?")[:120]))
                p = send("pcombat?")
                ml = num(p, "maxLive")
                if ml is not None and ml > max_live:
                    max_live = int(ml)
                    log("maxLive now %d | %s" % (max_live, p[p.find("strikes"):][:200]))
                if time.time() - fight_since > limit:
                    alive = send("fight?")
                    log("fight %d longer than %d s (%s): killall" % (fights, limit, alive[:100]))
                    send("killall")
                    killed += 1
                    fight_since = time.time() + 1e9   # once per fight
            else:
                fight_since = None
            time.sleep(5)
    finally:
        auto.terminate()
        try:
            auto.wait(15)
        except Exception:  # noqa: BLE001
            auto.kill()
    worst_stuck = max(worst_stuck, time.time() - state_since)
    p = send("pcombat?")
    log("pcombat: " + p[p.find("strikes"):][:400])
    # the logs since the start: exceptions, perf windows
    text = ""
    try:
        with open(hof2env.GLOG, errors="replace") as f:
            f.seek(log_start)
            text = f.read()
    except OSError:
        pass
    exc = [l for l in text.splitlines() if ("Exception" in l or "[Error" in l) and "HoF2VR" in l or "Hand of Fate 2 VR] " in l and "Exception" in l]
    perf = re.findall(r"perf 10s @[\d:.]+ \[([^\]]+)\] \d+ frames [\d.]+s: avg ([\d.]+) p50 [\d.]+ p95 ([\d.]+) p99 ([\d.]+) max ([\d.]+) ms.*?stalls (\d+)", text)
    fightperf = [x for x in perf if "Level_Play" in x[0]]
    if fightperf:
        p99 = sorted(float(x[3]) for x in fightperf)
        mx = max(float(x[4]) for x in fightperf)
        stalls = sum(int(x[5]) for x in fightperf)
        log("fight frames: %d windows, avg %.2f ms, p99 median %.1f / worst %.1f ms, max %.1f ms, stalls %d" % (
            len(fightperf), sum(float(x[1]) for x in fightperf) / len(fightperf), p99[len(p99) // 2], p99[-1], mx, stalls))
    log("exceptions from the mod: %d%s" % (len(exc), (" e.g. " + exc[0][:200]) if exc else ""))
    log("soak: %.0f min, fights %d (ended by killall %d), maxLive %d, longest state %.0f s, game alive %s" % (
        (time.time() - t0) / 60, fights, killed, max_live, worst_stuck, not died))
    return 1 if died or exc or worst_stuck > 300 else 0


if __name__ == "__main__":
    sys.exit(main())

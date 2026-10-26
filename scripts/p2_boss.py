#!/usr/bin/env python3
"""Boss and special fights in Physical mode (combat QA, docs/combat-progress.md): start the encounter from a map save
(the `jumpboss` seam through combat_fixture.py --jump boss:NAME), then for --seconds: the guard held in front, a swing
every few seconds at the nearest enemy (god mode, enemies keep their health), the bars and judgements logged; then the
fight ended with `killall` and the way out (the portal's A hold) checked.
  p2_boss.py NAME [NAME ...] [--seconds 90] [--tag BOSS] [--exit]
--exit (escape and trap levels): after the observation the hero is put 2 m before the nearest enabled exit trigger
(`exitwalk`), facing it, and the stick walks it in: the level must end (the mod's own walking sets the trigger off).
Per encounter: did the fight start, strikes / judged / unguarded / maxLive / shortest lead, the enemy kinds and their
running actions, exceptions from the mod, did the fight end and the game leave it. Results in logs/p2_boss.log.
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
LOG = H + "/logs/p2_boss.log"
args = sys.argv[1:]
SECS = float(args[args.index("--seconds") + 1]) if "--seconds" in args else 90
EXIT = "--exit" in args
names = [a for i, a in enumerate(args) if not a.startswith("--") and (i == 0 or args[i - 1] not in ("--seconds", "--tag"))]
results = []


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def send(c, t=8):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def user_in_vr():
    try:
        return '"user": true' in open("/run/user/1000/vr-session/status.json").read()
    except OSError:
        return False


def one(name):
    start = os.path.getsize(hof2env.GLOG) if os.path.exists(hof2env.GLOG) else 0
    # NAME = an encounter (its boss fight through `jumpboss`) or fixture:NAME (a boss AI in an ordinary arena)
    how = ["--fixture", name[8:]] if name.startswith("fixture:") else ["--jump", "boss:" + name]
    r = subprocess.run(["python3", H + "/scripts/combat_fixture.py"] + how + ["--boot", "--god", "--physical", "--equip", "BasicShield"],
                       capture_output=True, text=True)
    last = ((r.stdout or "").strip().splitlines() or ["?"])[-1]
    if "Level_Play" not in send("state?"):
        log("%s: no fight (%s | %s)" % (name, last, send("state?")))
        return False
    send("god on")
    send("pcombat? reset")
    send("strikelog on")
    sim("head 0 1.7 0 0 0 0; hand r 0.25 1.35 -0.35 0 5 0; hand l -0.25 1.2 -0.3 0 0 0")
    t0 = time.time()
    while time.time() - t0 < SECS:
        if user_in_vr():
            return False
        send("face nearest")
        sim("swing r left 90")
        time.sleep(3)
    p = send("pcombat?")
    send("enemysurvey")
    send("pcombat? log")
    fight = send("fight?")
    walked = None
    if EXIT:
        # walk into the exit trigger with the stick (the mod's own walking moves the hero)
        log("%s: %s" % (name, send("exits?")[:300]))
        w = send("exitwalk 2")
        log("%s: %s" % (name, w[:200]))
        if w.startswith("hero at"):
            sim("stick l 0 1 0")
            t2 = time.time()
            walked = False
            while time.time() - t2 < 8:
                if "Level_Play" not in send("state?", 4):
                    walked = True
                    break
                time.sleep(0.4)
            sim("stick l 0 0 0")
            log("%s: walked into the exit trigger: the level ended %s after %.1f s (%s | %s | %s)" % (
                name, walked, time.time() - t2, send("state?")[:40], send("fight?")[:80], send("exits?")[:160]))
    # end it, and the way out
    left = bool(walked)
    if not walked:
        send("killall")
        time.sleep(3)
        sim("btn r a 2500")
    t1 = time.time()
    while not left and time.time() - t1 < 60:
        s = send("state?", 4)
        if "Level_Play" not in s:
            left = True
            break
        sim("btn r a 2500")
        time.sleep(3)
    text = ""
    try:
        with open(hof2env.GLOG, errors="replace") as f:
            f.seek(start)
            text = f.read()
    except OSError:
        pass
    # the game's log goes with the next boot: keep this fight's part
    try:
        with open(H + "/logs/boss_%s.log" % re.sub(r"[^A-Za-z0-9_]+", "_", name), "w") as o:
            o.write(text)
    except OSError:
        pass
    exc = [l for l in text.splitlines() if "Exception" in l and ("HoF2VR" in l or "Hand of Fate 2 VR]" in l)]
    kinds = re.findall(r"\n-\t(\S+)\t\d+\t\S+\t\S+\t[^\t]*\t([^\n]*)", text)
    g = {k: v for k, v in re.findall(r"(strikes|blocks|counters|dodges|hitsTaken|unguarded|maxLive|minLead|leadShort|followUps|exactLeads|globalSlows) (-?[\d.]+)", p)}
    ok = left and not exc and g.get("maxLive", "0") in ("0", "1") and walked is not False
    log("%s: %s | %s | kinds %s | left the fight %s (%s) | exceptions %d%s" % (
        name, fight[:80], " ".join("%s %s" % kv for kv in g.items()), "; ".join("%s: %s" % k for k in kinds)[:300], left, send("state?")[:40],
        len(exc), (" e.g. " + exc[0][:160]) if exc else ""))
    results.append((name, ok))
    return True


def main():
    for n in names:
        if user_in_vr():
            log("the user is in VR: stopping")
            break
        one(n)
    log("bosses: %s" % ", ".join("%s %s" % (n, "OK" if ok else "CHECK") for n, ok in results))
    return 0 if results and all(ok for _, ok in results) else 1


if __name__ == "__main__":
    sys.exit(main())

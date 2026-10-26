#!/usr/bin/env python3
"""Waiting space (Limbo) check: boot, title -> table, table -> fight, fight -> table, with eye captures.

  limbo_test.py [--snapshot magician] [--no-install] [--tag L] [--off] [--no-fight] [--noshots] [--cmd "<mod command>" ...]
                [--autoplay MIN [--exit walk,touch,point,holdA,forfeit] [--probe]]
  --probe: at every fight, the exit portal's placement probe (`exitcard probe 16`) before the exit check

Eye captures land in testdata/shots/: <tag>boot_*, <tag>title, the waiting space itself
(<tag>_<n>_<flavour>_<state>_7 / _40 via `limboshot`), place arrivals (`stateshot`) and the reveals
(`relshot`). --off runs the same with Comfort.WaitingSpace=false (this run only) for the A/B of the hold
durations. Ends with the fade-hold / limbo lines of the mod log.
"""
import math
import os
import re
import signal
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import session  # noqa: E402

import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)

H = hof2env.H
GLOG = hof2env.GLOG


def send(c, t=6):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR %s" % e


def state():
    r = send("state?", 3)
    return "" if r.startswith("ERR") else r.split(" for ")[0]


def wait_for(substrs, timeout, poll=1.0):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        s = state()
        if s != last:
            print("  state:", s or "(none)", flush=True)
            last = s
        if any(x in s for x in substrs):
            return s
        time.sleep(poll)
    return None


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], stdout=subprocess.DEVNULL)


def exit_test(method, tag, n):
    """After the last kill: the exit card must appear; leave by `method`."""
    t0 = time.time()
    info = ""
    while time.time() - t0 < 10:
        info = send("exitcard info", 4)
        if "card (" in info:
            break
        time.sleep(0.5)
    print("  exit %d (%s): %s | state %s" % (n, method, info, state()), flush=True)
    if "card (" not in info:
        return
    time.sleep(1.0)
    if "--noshots" not in sys.argv:
        print("   shot:", send("shot %scard_%d" % (tag, n)), flush=True)
    if "--probe" in sys.argv:
        # look at the portal (head yaw = -bearing in xrsim's convention, 11 deg down to chest height)
        m = re.search(r"bearing (-?\d+)", info)
        if m:
            sim("head 0 1.6 0 %d -11 0" % -int(m.group(1)))
            time.sleep(1.0)
            print("   look shot:", send("shot %slook_%d" % (tag, n)), flush=True)
            time.sleep(1.5)
            sim("head 0 1.6 0 0 0 0")
    time.sleep(1.5)
    if "Level_Play" not in state():
        print("   left before acting:", state())
        return
    t1 = time.time()
    if method == "walk":
        for i in range(12):
            info = send("exitcard info", 4)
            m = re.search(r"bearing (-?\d+)", info)
            if not m or "pending" not in info:
                break
            b = math.radians(float(m.group(1)))
            sim("stick l %.2f %.2f 700" % (math.sin(b), math.cos(b)))
            time.sleep(0.8)
            if "Level_Play" not in state():
                break
    elif method == "touch":
        mv = send("handto r tip exitcard", 4)
        print("   ", mv)
        if mv.startswith("move"):
            sim(mv + " 400; wait 200")
            time.sleep(0.6)
            mv = send("handto r tip exitcard", 4)   # a second, finer step (the first was from far)
            print("   ", mv)
            if mv.startswith("move") and "pending" in send("exitcard info", 4):
                sim(mv + " 200; wait 200")
        time.sleep(1.0)
    elif method == "point":
        # the left hand (its trigger is the counter: no lunge); the `aim` seam's pitch is only good to
        # ~20 deg on the simulated Index controllers, so it is raised step by step (each miss is logged)
        sim("hand l -0.2 1.2 -0.3 0 0 0; wait 200")
        for extra in (0, 8, 16, 24, 32):
            r = send("aim l exitcard", 4)
            print("   ", r, "+%d" % extra)
            if not r.startswith("rot"):
                break
            p = r.split()
            sim("rot l %s %.2f 0; wait 300; btn l trigger 200" % (p[2], float(p[3]) + extra))
            time.sleep(1.2)
            if "Level_Play" not in state() or "pending" not in send("exitcard info", 4):
                break
    elif method == "holdA":
        print("   A before 20 s:", sim("btn r a 1500") or "", send("exitcard info", 4))
        while time.time() - t0 < 23:
            time.sleep(0.5)
        sim("btn r a 1500")
        time.sleep(1.0)
    elif method == "forfeit":
        print("   ", send("exitcard forfeit", 4))
    for i in range(20):
        if "Level_Play" not in state():
            break
        time.sleep(0.5)
    print("   after %s: %.1f s -> state %s | %s | counts %s" % (method, time.time() - t1, state(), send("exitcard info", 4), cmd.status().get("fightExitCounts")), flush=True)
    sim("hand r 0.25 1.3 -0.35 0 0 0; hand l -0.25 1.25 -0.35 0 0 0; rot l 0 0 0; rot r 0 0 0")


def main():
    a = sys.argv[1:]
    snap = a[a.index("--snapshot") + 1] if "--snapshot" in a else "magician"
    tag = a[a.index("--tag") + 1] if "--tag" in a else "L"
    off = "--off" in a
    subprocess.run([H + "/scripts/stop.sh"], stdout=subprocess.DEVNULL)
    session.restore_snapshot(snap)
    if "--no-install" not in a:
        subprocess.run([H + "/scripts/install.sh"], stdout=subprocess.DEVNULL, check=True)
    if not hof2env.monado_up():
        subprocess.run([H + "/scripts/monado.sh", "start"], check=True)
    hof2env.kill_own_zenity()
    subprocess.run([H + "/scripts/run.sh", "monado", "gl"], check=True)
    # (timed from the launch: run.sh may wait minutes for the game slot first - then the 200 s below were gone before the
    # game even started, and the boot's gate was never passed)
    t0 = time.time()
    # boot: the channel answers once the mod is up; capture the boot space a few times
    first = None
    shots = [2.0, 8.0, 15.0]
    while time.time() - t0 < 200:
        s = send("state?", 2)
        if not s.startswith("ERR"):
            if first is None:
                first = time.time()
                print("mod up after %.1f s: %s | %s" % (first - t0, s, send("limbo info")), flush=True)
                # the waiting space waits for A once the next place is ready (Comfort.PressToContinue, since 2026-10-11):
                # a test goes on by itself 1.5 s after, as session.py's pass_gates (this test predated the gate)
                print(" ", send("gate auto 1.5"))
                if off:
                    print(" ", send("cfg WaitingSpace false"))
                if "--exit" in a:
                    print(" ", send("cfg PlayerFightExit true"))
                if "--noshots" not in a:
                    send("relshot %sR" % tag)
                for i, x in enumerate(a):
                    if x == "--cmd":
                        print("  cmd:", a[i + 1], "=>", send(a[i + 1]))
            st = s.split(" for ")[0]
            if "StartScreen" in st:
                break
            if shots and "--noshots" not in a and time.time() - first >= shots[0]:
                print("  boot shot at %.1f s: %s | %s" % (time.time() - first, send("shot %sboot_%d" % (tag, int(shots[0]))), send("limbo info")), flush=True)
                shots.pop(0)
        time.sleep(0.5)
    print("title state after %.1f s" % (time.time() - t0))
    # the title is revealed once its menu is up (Places); the handover takes ~1 s
    wait_for(["StartScreen"], 60)
    deadline = time.time() + 60
    while time.time() < deadline and not send("place").startswith("place Title"):
        time.sleep(0.5)
    print("title place taken after %.1f s" % (time.time() - t0))
    time.sleep(4)
    print("title:", send("shot %stitle" % tag) if "--noshots" not in a else "-", send("place"), send("limbo info"))
    if "--noshots" not in a:
        send("limboshot %s" % tag)
        send("stateshot %sS" % tag)
    time.sleep(2)
    # title -> table (as session.py)
    subprocess.run(["python3", H + "/scripts/xrsim.py", "hand r 0.2 1.0 -0.3 0 -60 0; hand l -0.2 1.0 -0.3 0 -60 0"])
    for attempt in range(6):
        r = send("ui click ContinueButton")
        print(" ", r)
        time.sleep(4)
        if "ModdingDialog" in send("ui list ModdingDialog") or not r.startswith("clicking"):
            if send("ui list ModdingDialog").split()[0] != "0":
                print("  mods screen -> Play (X)")
                send("press X 6")
        s = wait_for(["Profile", "ChallengeMap", "Map", "Attract", "Intro"], 15)
        if s:
            break
    s = wait_for(["ChallengeMap", "GameState_Map", "Attract", "Intro", "ChallengeSetup", "Level_"], 240)
    print("table reached:", s)
    time.sleep(6)
    send("stateshot off")
    print("table:", send("shot %stable" % tag) if "--noshots" not in a else "-", send("place"), send("limbo info"))
    if "--no-fight" in a:
        return
    if "--autoplay" in a:
        # fights the way the regression reaches them; each fight is ended with `killall` after a while
        minutes = float(a[a.index("--autoplay") + 1])
        send("stateshot off")
        ap = subprocess.Popen(["python3", H + "/scripts/autoplay.py", str(minutes)], stdout=open(H + "/logs/limbo-autoplay.out", "w"), stderr=subprocess.STDOUT)
        last, since, killed = None, time.time(), False
        methods = (a[a.index("--exit") + 1] if "--exit" in a else "walk").split(",")
        n_exit = 0
        while ap.poll() is None:
            try:
                st = cmd.status()
            except Exception:
                st = {}
            s = st.get("state", "")
            if s != last:
                print("  %s state %s | place %s | limbo %s | hold %s" % (time.strftime("%H:%M:%S"), s, st.get("place"), st.get("limbo"), st.get("fadeHold")), flush=True)
                last, since, killed = s, time.time(), False
            if "Level_Play" in s and not killed and time.time() - since > 15:
                killed = True
                if "--probe" in a:
                    print("  probe:", send("exitcard probe 16", 20), flush=True)
                print("  killall:", send("killall", 8), flush=True)
                if "--exit" in a:
                    ap.send_signal(signal.SIGSTOP)   # autoplay's swings and turns would move the hands
                    try:
                        exit_test(methods[n_exit % len(methods)], tag, n_exit)
                    finally:
                        ap.send_signal(signal.SIGCONT)
                    n_exit += 1
            time.sleep(1)
        return
    # table -> fight
    send("stateshot %sF" % tag)
    try:
        session.to_combat(400)
    except SystemExit as e:
        print("to_combat:", e)
    s = wait_for(["Level_Play"], 60)
    print("fight:", s, send("place"), send("limbo info"))
    send("stateshot off")
    time.sleep(3)
    send("shot %sfight" % tag)
    send("god on")
    time.sleep(2)
    # fight -> table
    send("stateshot %sX" % tag)
    for i in range(6):
        print("killall:", send("killall"))
        if wait_for(["Level_Out"], 20):
            break
        # (since the exit orb, a won fight waits for the player: holding A 1.5 s leaves, as in the headset)
        sim("btn r a 1500")
        if wait_for(["Level_Out"], 10):
            break
    s = wait_for(["GameState_Map", "GameState_Post", "Reward", "Attract", "Encounter"], 90)
    print("back at the table:", s)
    time.sleep(8)
    send("stateshot off")
    print("table again:", send("shot %stable2" % tag), send("place"), send("limbo info"))


if __name__ == "__main__":
    try:
        main()
    finally:
        tg = sys.argv[sys.argv.index("--tag") + 1] if "--tag" in sys.argv else "L"
        subprocess.run(["cp", GLOG, H + "/logs/bepinex-limbo-%s.log" % tg])
        try:
            with open(GLOG, errors="replace") as f:
                for line in f:
                    if re.search(r"fade hold|fade release|limbo:|place: |kept off the panel|black overlay|Exception|rig: station", line):
                        print("LOG", line.rstrip()[:220])
        except FileNotFoundError:
            pass

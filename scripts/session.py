#!/usr/bin/env python3
"""Reproducible Hand of Fate 2 VR test session (test mode: isolated saves).

  session.py start [--runtime monado|wivrn] [--api gl|vulkan] [--to title|table] [--no-install] [--snapshot NAME]
                   [--shot NAME]   (NAME: an eye capture at the title, testdata/shots/NAME_eyes.png)
  session.py stop
  session.py wait <StateSubstring> [timeout_s]

start: stops the game, builds nothing (run scripts/build.sh first), installs, makes sure the
runtime is up, launches, waits for the title screen; with --to table it clicks Continue (through
the laser test seam) and waits for the card table (ChallengeMap or Map state).
"""
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402

import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)

H = hof2env.H


def sh(*a, **kw):
    return subprocess.run(list(a), **kw)


def state():
    try:
        return cmd.send("state?", 3).split(" for ")[0]
    except SystemExit:
        return ""


def wait_state(substrs, timeout):
    if isinstance(substrs, str):
        substrs = [substrs]
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        s = state()
        if s != last:
            print("  state:", s or "(no answer)", flush=True)
            last = s
        if any(x in s for x in substrs):
            return s
        time.sleep(1.5)
    raise SystemExit("timed out waiting for %s (last %r)" % (substrs, last))


def pass_gates(timeout=30):
    """The waiting space waits for A once the next place is ready (Comfort.PressToContinue, 2026-10-11): test runs go
    on by themselves 1.5 s after a place is ready (`gate auto`); waits until the boot's gate has been passed."""
    try:
        cmd.send("gate auto 1.5", 5)
    except SystemExit:
        return
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            g = cmd.send("gate", 3)
        except SystemExit:
            g = ""
        if g.startswith("gate idle") and " goes 0 " not in g and "|" in g:
            return
        time.sleep(0.5)


def restore_snapshot(name):
    src = os.path.join(H, "testdata/snapshots", name + ".profile")
    for d in ("remote", "local"):
        os.makedirs(os.path.join(H, "testdata", d), exist_ok=True)
        subprocess.run(["cp", src, os.path.join(H, "testdata", d, "player_data.profile")], check=True)
    print("restored snapshot", name)


def to_combat(timeout=300):
    """From the table: confirm pages/dialogs with A until live combat."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        s = state()
        if "Level_Play" in s:
            # dismiss tutorial dialogs that pause combat
            time.sleep(1.5)
            cmd.send("press A 6")
            time.sleep(1.5)
            print("combat:", state())
            return
        if "Gender" in cmd.send("world list Gender"):
            pass
        n = cmd.send("world list Card_CC_Gender_F").split()[0]
        if n != "0":
            cmd.send("world click Card_CC_Gender_F")
            time.sleep(3)
            continue
        cmd.send("press A 6")
        time.sleep(3)
    raise SystemExit("never reached combat (state %s)" % state())


def start(runtime="monado", api="gl", to="title", install=True, snapshot=None, gdb=False, shot=None):
    sh(H + "/scripts/stop.sh", stdout=subprocess.DEVNULL)
    if snapshot:
        restore_snapshot(snapshot)
    if install:
        sh(H + "/scripts/install.sh", stdout=subprocess.DEVNULL, check=True)
    if runtime == "monado" and not hof2env.monado_up():
        sh(H + "/scripts/monado.sh", "start", check=True)
    if runtime == "monado":
        # standing unless a seated test sets it after the start (xrsim `seat`); HOF2VR_SEATED=1 makes the game seated
        sh("python3", H + "/scripts/xrsim.py", "seat %s" % ("0.55" if os.environ.get("HOF2VR_SEATED") == "1" else "0"), stdout=subprocess.DEVNULL)
    hof2env.kill_own_zenity()
    env = dict(os.environ)
    if gdb:
        env["GDB"] = "1"
    subprocess.run([H + "/scripts/run.sh", runtime, api], check=True, env=env)
    time.sleep(10)
    wait_state("StartScreen", 180)
    pass_gates()
    time.sleep(4)
    if shot:
        print("  title shot:", cmd.send("shot " + shot, 15), flush=True)
    if to in ("table", "combat"):
        # point the real (simulated) hand away so it cannot hover anything after the seam ends
        if runtime == "monado":
            sh("python3", H + "/scripts/xrsim.py", "hand r 0.2 1.0 -0.3 0 -60 0; hand l -0.2 1.0 -0.3 0 -60 0")
        for attempt in range(6):
            r = cmd.send("ui click ContinueButton")
            print(" ", r)
            time.sleep(4)
            # A slot with workshop info shows the Mods screen first; its Play is X.
            if "ModdingDialog" in cmd.send("ui list ModdingDialog") or not r.startswith("clicking"):
                if cmd.send("ui list ModdingDialog").split()[0] != "0":
                    print("  mods screen -> Play (X)")
                    cmd.send("press X 6")
            try:
                s = wait_state(["Profile", "ChallengeMap", "Map", "Attract", "Intro"], 15)
                break
            except SystemExit:
                print("  not loading yet; retrying")
        s = wait_state(["ChallengeMap", "GameState_Map", "Attract", "Intro", "ChallengeSetup", "Level_"], 240)
        pass_gates()   # the title -> table gate as well
        print("table reached:", s)
    if to == "combat":
        to_combat()


def main():
    a = sys.argv[1:]
    if not a:
        raise SystemExit(__doc__)
    if a[0] == "stop":
        sh(H + "/scripts/stop.sh")
    elif a[0] == "wait":
        print(wait_state(a[1], float(a[2]) if len(a) > 2 else 120))
    elif a[0] == "start":
        opts = {"runtime": "monado", "api": "gl", "to": "title", "install": True, "snapshot": None, "shot": None}
        i = 1
        while i < len(a):
            if a[i] == "--no-install":
                opts["install"] = False
            else:
                opts[a[i][2:]] = a[i + 1]
                i += 1
            i += 1
        start(**opts)
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main()

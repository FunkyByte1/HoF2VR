#!/usr/bin/env python3
"""Combat Stage 1: where do enemy blows really go, and where is our bar? (docs/combat-progress.md, 2026-10-12 night)

  swing_probe.py GROUP [GROUP ...] [--seconds 70] [--tag S1] [--boot] [--guard] [--monsters]

For each GROUP (a fixture name of combat_fixture.py, or a fightwith spec "AIRef*n,AIRef*n"): a fight in Physical mode
(god mode, enemies kept alive with `tough on`), the swing trace on (`swingtrace`, Game/SwingTrace.cs); the player stands
still at 1.7 m with the hands down at the sides and turns to face each striker (the right stick's smart snap), so every
blow is in view and lands. Then `swingtrace dump <tag>_<group>` -> testdata/probes/<tag>_<group>.tsv and the match line
in logs/swing_probe.log. --guard holds the shield in front instead (blocks; the blows are the same up to the impact).
--monsters first logs every monster card's AI and actions (`monsters REF actions`).
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
LOG = H + "/logs/swing_probe.log"
args = sys.argv[1:]
SECS = float(args[args.index("--seconds") + 1]) if "--seconds" in args else 70
TAG = args[args.index("--tag") + 1] if "--tag" in args else "S1"
groups = [a for i, a in enumerate(args) if not a.startswith("--") and (i == 0 or args[i - 1] not in ("--seconds", "--tag"))]
FIXTURE_NAMES = None


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
        return True


def fixture_names():
    src = open(H + "/scripts/combat_fixture.py").read()
    return set(re.findall(r'^\s+"(\w+)": \("EL_', src, re.M))


def one(group, boot):
    name = re.sub(r"[^A-Za-z0-9]+", "_", group)[:40]
    how = ["--fixture", group] if group in FIXTURE_NAMES else ["--jump", "EL_Ambush_Thral", "--with", group]
    r = subprocess.run(["python3", H + "/scripts/combat_fixture.py"] + how + (["--boot"] if boot else []) + ["--god", "--physical"],
                       capture_output=True, text=True)
    last = ((r.stdout or "").strip().splitlines() or ["?"])[-1]
    if "Level_Play" not in send("state?"):
        log("%s: no fight (%s | %s)" % (group, last, send("state?")))
        return False
    send("god on")
    send("tough on")
    send("pcombat? reset")
    send("swingtrace clear")
    send("swingtrace on")
    log("%s: %s" % (group, send("weaponparts")))
    if "--guard" in args:
        sim("head 0 1.7 0 0 0 0; hand r 0.25 1.35 -0.35 0 5 0; hand l -0.25 1.2 -0.3 0 0 0")
    else:
        sim("head 0 1.7 0 0 0 0; hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0")
    t0 = time.time()
    last_face = 0
    while time.time() - t0 < SECS:
        if user_in_vr():
            log("the user is in VR: stopping")
            return False
        s = send("strike?", 4)
        if s and s != "none" and "offview" in s.split(" | ")[0] and time.time() - last_face > 0.6:
            send("face striker")
            last_face = time.time()
        elif (not s or s == "none") and time.time() - last_face > 2.5:
            send("face nearest")
            last_face = time.time()
        time.sleep(0.15)
    d = send("swingtrace dump %s_%s" % (TAG, name), 15)
    log("%s: %s" % (group, d))
    log("%s: pcombat %s" % (group, send("pcombat?")[:300]))
    send("swingtrace off")
    send("tough off")
    send("killall")
    time.sleep(3)
    left = False
    t1 = time.time()
    while time.time() - t1 < 60:
        if "Level_Play" not in send("state?", 4):
            left = True
            break
        sim("btn r a 2500")
        time.sleep(3)
    log("%s: left the fight %s (%s)" % (group, left, send("state?")[:60]))
    log("%s: map %s" % (group, "ready" if settle() else "NOT ready (" + str(menu()) + ")"))
    return True


def menu():
    try:
        return cmd.status().get("menu") or ""
    except Exception:
        return ""


def settle(timeout=120):
    """Back on the map: press A through the encounter card, dialogue and loot that follow a fight, until the primary menu
    is the map again (DevJump refuses to jump before)."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        m = menu()
        st = send("state?", 4)
        if "GameState_Map" in st and m in ("Map", ""):
            time.sleep(1.0)
            if menu() in ("Map", ""):
                return True
        if "Level_Play" in st:
            # the encounter went on into another fight: end it and walk out again
            send("god on")
            send("killall")
            time.sleep(3)
            sim("btn r a 2500")
            time.sleep(3)
            continue
        send("press A 6")
        time.sleep(2.0)
    return False


def main():
    global FIXTURE_NAMES
    if os.path.exists(H + "/logs/skip_probe"):
        log("logs/skip_probe exists: not probing")
        return 0
    FIXTURE_NAMES = fixture_names()
    boot = "--boot" in args
    if "--monsters" in args:
        if boot or "GameState_Map" not in send("state?"):
            subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"], capture_output=True, text=True)
            boot = False
        log("monsters: %s" % send("monsters REF actions", 120))
    for i, g in enumerate(groups):
        if user_in_vr():
            log("the user is in VR: stopping")
            break
        if not boot and "GameState_Map" not in send("state?"):
            # not on the map: leave whatever fight this is first (a reboot would hand the one-game slot to the other env)
            subprocess.run(["python3", H + "/scripts/leave_fight.py", "300"], capture_output=True, text=True)
        ok = one(g, boot or "GameState_Map" not in send("state?"))
        if not ok and not user_in_vr():
            one(g, True)
        boot = False
    return 0


if __name__ == "__main__":
    sys.exit(main())

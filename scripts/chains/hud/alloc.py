#!/usr/bin/env python3
"""env 3 (2026-10-19, the HUD audit's item 2): the fight HUD's managed allocations per method in a live fight (the big
fixture, god, hands down): Board + Shell, then Rings, UpdateProfiler's `profile alloc` window (statistical heap growth per
Update / LateUpdate) for 25 s each; the report's rows for the HUD's types. Run from the chain (lib.sh's cwd)."""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.join(os.getcwd(), "scripts"))
import cmd  # noqa: E402
import hof2env  # noqa: E402

TYPES = ("FightHud", "EnemyBars", "EnemyShell", "EnemyRings", "Hands", "Premonition", "IndicatorLooks", "Guard", "Poise", "SuperMeter", "Threats")


def send(c, t=8):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def sim(c):
    subprocess.run(["python3", "scripts/xrsim.py", c], capture_output=True, text=True)


def window(tag, secs=25):
    log0 = os.path.getsize(hof2env.GLOG) if os.path.exists(hof2env.GLOG) else 0
    print(tag, send("profile alloc", 60), flush=True)
    t0 = time.time()
    while time.time() - t0 < secs:
        send("face nearest")
        sim("head 0 1.7 0 0 0 0; hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0")
        time.sleep(1.0)
        sim("head 0 1.7 0 60 -5 0")   # a look toward the board's side and back
        time.sleep(1.0)
    print(tag, send("profile allocs 60", 20)[:120], flush=True)
    for _ in range(40):
        time.sleep(0.5)
        with open(hof2env.GLOG, errors="replace") as f:
            f.seek(log0)
            text = f.read()
        ms = list(re.finditer(r"alloc window [\d.]+ s", text))   # the report (not the "alloc window started" echo)
        if ms:
            i = ms[-1].start()
            rep = text[i:i + 12000]
            head = rep.splitlines()[0][:220]
            print(tag, head, flush=True)
            for line in rep.splitlines()[1:]:
                m = re.match(r"^\s+([\d.]+) KB/s \[(\w+)\] (\S+)", line)
                if m and m.group(3).split(".")[0] in TYPES:
                    print(tag, line.strip()[:160], flush=True)
            break
    send("profile off", 30)


r = subprocess.run(["python3", "scripts/combat_fixture.py", "--fixture", "big", "--god", "--physical", "--equip", "BasicSword_noToken,BasicShield"], capture_output=True, text=True)
print((r.stdout or "").strip().splitlines()[-1:], flush=True)
send("vrknock off")
send("vrhud board")
send("vrstatus shell")
time.sleep(10)
window("board+shell")
send("vrstatus rings")
time.sleep(2)
window("board+rings")
send("vrstatus setting")
send("vrhud setting")
print("alloc done", flush=True)

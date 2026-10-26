#!/usr/bin/env python3
"""Pause-on-focus-loss checks (VR/FocusPause.cs) in the simulator. Run OUTSIDE the Bash sandbox.

  focus_test.py [--snapshot magician] [--no-start] [tests...]
Tests (default all, in this order), each in a live fight (GameState_Level_Play):
  focus     a second OpenXR app (hello_xr) takes Monado's primary slot (monado-ctl -p): the game's session
            loses focus -> IVRSystem::ShouldApplicationPause -> the fight pauses; the game gets the slot back ->
            note, resumed 2 s later
  frozen    `focus fallback 1` + xrsim `noise on` (live tracking): no pause; `noise off` (tracking freezes exactly)
            -> paused; `noise on` -> resumed
  takeover  hidden (seam) -> paused; visible -> the stick moves within the 2 s -> stays paused; B resumes
  manual    the player's own pause (START) is never resumed by the mod
Results: PASS/FAIL lines; logs/focus_test.log. Shots testdata/shots/FP_*.
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import session  # noqa: E402

import hof2env  # noqa: E402  (this worktree's test environment, docs/two-environments.md)

H = hof2env.H
LOG = H + "/logs/focus_test.log"
RESULTS = []


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def check(name, ok, detail=""):
    RESULTS.append((name, ok))
    log(("PASS " if ok else "FAIL ") + name + (" - " + detail if detail else ""))


def send(c, t=6):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR %s" % e


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], stdout=subprocess.DEVNULL)


def state():
    return cmd.status().get("state", "")


def focus():
    return cmd.status().get("focus", "")


def wait(pred, timeout, step=0.2):
    t0 = time.time()
    while time.time() - t0 < timeout:
        try:
            if pred():
                return time.time() - t0
        except Exception:
            pass
        time.sleep(step)
    return None


def clients():
    out = subprocess.run(["monado-ctl"], capture_output=True, text=True, env=hof2env.xr_env()).stdout
    res = []
    for line in out.splitlines():
        m = re.search(r"id: (\d+)\s+act: (\d)\s+disp: (\d)\s+foc: (\d).*pid: +(\d+)\s+(.*)$", line)
        if m:
            res.append(dict(id=int(m.group(1)), act=int(m.group(2)), disp=int(m.group(3)), foc=int(m.group(4)), pid=int(m.group(5)), name=m.group(6).strip()))
    return res


def game_client():
    pid = hof2env.game_pids()   # this environment's test instance
    for c in clients():
        if pid and c["pid"] == int(pid[0]):
            return c
    return None


AP = None


def ensure_fight():
    """Autoplay drives the game into a fight and is then frozen (SIGSTOP) so it cannot interfere."""
    global AP
    import signal
    if "Level_Pause" in state():
        send("press B 6")
        time.sleep(2)
    if "Level_Play" in state():
        return True
    if AP is None or AP.poll() is not None:
        AP = subprocess.Popen(["python3", H + "/scripts/autoplay.py", "30"], stdout=open(H + "/logs/focus-autoplay.out", "a"), stderr=subprocess.STDOUT)
    else:
        AP.send_signal(signal.SIGCONT)
    ok = wait(lambda: "Level_Play" in state(), 420, 0.5) is not None
    AP.send_signal(signal.SIGSTOP)
    time.sleep(0.5)
    sim("stick l 0 0; stick r 0 0; hold r trigger 0; hold l trigger 0; hold r grip 0; hold l grip 0; "
        "hand r 0.25 1.3 -0.35 0 0 0; hand l -0.25 1.25 -0.35 0 0 0")
    if ok:
        send("god on")
        time.sleep(2)
        ok = "Level_Play" in state()
    log("  fight: %s (%s)" % (ok, state()))
    return ok


def test_focus():
    log("focus: hello_xr takes the primary slot")
    if not ensure_fight():
        return check("focus: in a fight", False, state())
    time.sleep(3)
    g = game_client()
    log("  game client %s" % g)
    env = hof2env.xr_env()   # this environment's Monado
    # (hello_xr quits on "any key": keep its stdin open)
    hx = subprocess.Popen(["hello_xr", "-g", "Vulkan2"], env=env, stdin=subprocess.PIPE, stdout=open(H + "/logs/hello_xr.log", "w"), stderr=subprocess.STDOUT)
    try:
        other = None
        for _ in range(40):
            time.sleep(0.25)
            other = next((c for c in clients() if c["pid"] == hx.pid and c["act"] == 1), None)
            if other:
                break
        log("  hello_xr client %s; all %s" % (other, clients()))
        if not other:
            return check("focus: hello_xr session", False)
        t0 = time.time()
        subprocess.run(["monado-ctl", "-p", str(other["id"])], capture_output=True, env=hof2env.xr_env())
        dt = wait(lambda: "Level_Pause" in state() and focus().startswith("hidden"), 10)
        g2 = game_client()
        check("focus: lost focus -> paused", dt is not None, "%.1f s, focus %s, state %s, game client %s" % (dt or -1, focus(), state(), g2))
        log("  " + send("focus"))
        time.sleep(2)
        check("focus: stays paused while hidden", "Level_Pause" in state(), state())
        subprocess.run(["monado-ctl", "-p", str(g["id"])], capture_output=True, env=hof2env.xr_env())
        dv = wait(lambda: focus().startswith("visible"), 10)
        log("  visible again after %s s; %s" % (dv, focus()))
        time.sleep(0.6)
        send("shot FP_focus_note")
        time.sleep(0.6)
        check("focus: still paused 1.2 s after the view returns", "Level_Pause" in state(), state())
        dr = wait(lambda: "Level_Play" in state(), 6)
        check("focus: resumed after ~2 s", dr is not None, "%.1f s after the note shot; %s" % ((dr or -1) + 0.6, focus()))
        log("  game client now %s" % game_client())
    finally:
        hx.terminate()
        try:
            hx.wait(5)
        except Exception:
            hx.kill()


def test_frozen():
    log("frozen: tracking freezes (fallback)")
    if not ensure_fight():
        return check("frozen: in a fight", False, state())
    log("  " + send("focus fallback 1"))
    sim("noise on")
    time.sleep(8)
    check("frozen: live tracking -> no pause", "Level_Play" in state() and focus().startswith("visible"), "%s | %s" % (state(), send("focus")))
    sim("noise off")
    dt = wait(lambda: "Level_Pause" in state(), 5)
    check("frozen: frozen tracking -> paused", dt is not None, "%.2f s after the freeze; %s" % (dt or -1, send("focus")))
    time.sleep(2)
    sim("noise on")
    dr = wait(lambda: "Level_Play" in state(), 6)
    check("frozen: tracking again -> resumed", dr is not None, "%.1f s; %s" % (dr or -1, focus()))
    sim("noise off")
    log("  " + send("focus fallback 0"))


def test_takeover():
    log("takeover: the player uses the controls while the note is up")
    if not ensure_fight():
        return check("takeover: in a fight", False, state())
    send("focus fake hidden")
    dt = wait(lambda: "Level_Pause" in state(), 5)
    check("takeover: paused", dt is not None, state())
    time.sleep(1)
    send("focus fake off")
    wait(lambda: focus().startswith("visible"), 5)
    time.sleep(0.2)
    send("shot FP_takeover_note")
    time.sleep(0.3)
    sim("stick l 0 1 400")
    time.sleep(3)
    check("takeover: stays paused (the menu is theirs)", "Level_Pause" in state() and "stayed 1" in focus(), "%s | %s" % (state(), focus()))
    send("press B 6")
    dr = wait(lambda: "Level_Play" in state(), 6)
    check("takeover: the menu's B resumes", dr is not None, state())


def test_manual():
    log("manual: the player's own pause is never resumed by the mod")
    if not ensure_fight():
        return check("manual: in a fight", False, state())
    time.sleep(1)
    send("press START 6")
    wait(lambda: "Level_Pause" in state(), 5)
    send("focus fake hidden")
    time.sleep(1.5)
    send("focus fake off")
    time.sleep(4)
    check("manual: still paused", "Level_Pause" in state() and "ourPause" not in focus(), "%s | %s" % (state(), focus()))
    send("press B 6")
    dr = wait(lambda: "Level_Play" in state(), 6)
    check("manual: B resumes", dr is not None, state())


def main():
    a = sys.argv[1:]
    snap = a[a.index("--snapshot") + 1] if "--snapshot" in a else "magician"
    tests = [x for x in a if x in ("focus", "frozen", "takeover", "manual")] or ["focus", "frozen", "takeover", "manual"]
    if "--no-start" not in a:
        session.start(snapshot=snap, to="table")
    log("in the game: %s | %s" % (state(), send("focus")))
    send("god on")
    try:
        for t in tests:
            globals()["test_" + t]()
            send("god on")
    finally:
        if AP is not None and AP.poll() is None:
            import signal
            AP.send_signal(signal.SIGCONT)
            AP.terminate()
    n = sum(1 for _, ok in RESULTS if ok)
    log("RESULT %d/%d PASS" % (n, len(RESULTS)))


if __name__ == "__main__":
    main()

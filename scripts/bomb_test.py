#!/usr/bin/env python3
"""P2-10 / P2-11 (the user's second playtest of 2026-10-17): bombs and shots from out of view, in the `lobs` fixture (two
molotov throwers; god on).

  bomb_test.py [--tag T] [--no-fixture] [--stay] [--parts knock,drop,dodge,offview] [--pics]

  knock    the shield on a lob's bar (shotguard): it is knocked away (knocked +1), its fire bursts at least SafeRadius
           (4.5 m) from the player and never reaches them (knockedAreaHits +0)
  drop     hands down, no block: the lob drops onto the game's own spot - the player's feet - and its fire reaches them
           (drops +1, the fire within 1 m, areaHits +1): the game's own outcome
  dodge    hands down, a dash sideways while the lob flies: it drops on the spot the player left (the fire >= 2.5 m away), no
           fire reaches the player
  offview  the throwers to the side (75 deg from the gaze; from behind, beyond 110 deg, the game's ranged strikes are not
           counterable and wait - EvaluatePrefix's HeldUnblockable): their throw is warned off view (a ranged strike's
           marker), and the flying bottle too, after it leaves the hand (warn? shotWarns +1); announcements stay one per
           attacker (no announcement every frame)
--pics: both eyes at the bar, the knock, the off-view marker in the flight (captures/fixes-p2-combat/).
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
args = sys.argv[1:]
TAG = args[args.index("--tag") + 1] if "--tag" in args else "BT"
PICS = "--pics" in args
OUT = []
HEAD = "head 0 1.65 0 0 0 0"
GUARD = "hand r 0.25 1.2 -0.35 0 5 0; hand l -0.3 1.15 -0.35 0 0 0"
# both arms hanging (the controllers pointing down). Candidate 11: with the left hand held level here the shield still met the
# lob's line - every lob knocked off it, "no drop in 4 lobs" (since P4-12 the shield is held turned upright in its own plane,
# which moves its middle against the fist; candidate 10's same pose dropped them)
DOWN = "hand r 0.35 0.85 0.05 0 -80 0; hand l -0.45 0.8 0.2 0 -80 0"


def log(m):
    print(time.strftime("%H:%M:%S") + " " + m, flush=True)


def result(name, ok, detail):
    OUT.append(ok)
    log("%s %s: %s" % ("PASS" if ok else "FAIL", name, detail))


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


def nums(s):
    d = {}
    for k, v in re.findall(r"([A-Za-z]+) (-?[\d.]+)", s):
        if k not in d:
            try:
                d[k] = float(v)
            except ValueError:
                pass
    return d


def shots():
    return nums(send("shots?"))


def pic(name):
    if not PICS:
        return
    env = dict(os.environ, PICS_OUT=os.environ.get("PICS_OUT") or H + "/captures/fixes-p2-combat")
    r = subprocess.run(["python3", H + "/scripts/chains/pf/pics.py", "shot", name], capture_output=True, text=True, env=env)
    log("  pic " + ((r.stdout or "").strip().splitlines() or ["?"])[-1])


SNAPS = []


def snap(name):
    """A fast capture (the game takes it next frame; converted to captures/ at the end): for moments a fraction of a second apart."""
    if PICS:
        send("shot " + name, 4)
        SNAPS.append((name, time.time()))


def save_snaps():
    from PIL import Image
    out = os.environ.get("PICS_OUT") or H + "/captures/fixes-p2-combat"
    os.makedirs(out, exist_ok=True)
    for name, at in SNAPS:
        src = H + "/testdata/shots/" + name + "_eyes.png"
        for _ in range(40):
            if os.path.exists(src) and os.path.getmtime(src) >= at - 1 and os.path.getsize(src) > 0:
                break
            time.sleep(0.25)
        try:
            time.sleep(0.3)
            Image.open(src).convert("RGB").save(out + "/" + name + ".jpg", quality=88)
            log("  snap saved " + out + "/" + name + ".jpg")
        except Exception as ex:
            log("  snap %s: %r" % (name, ex))
    del SNAPS[:]


def next_lob(min_eta=0.25, timeout=60):
    """Waits for a thrown lob flying at the player: (eta, blockable) or None."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        if user_in_vr():
            return None
        a = send("shotat", 4)
        m = re.search(r"eta ([\d.]+) blockable (\w+)", a)
        if m and float(m.group(1)) >= min_eta and "lob" in send("shots?", 4).split(" | ", 1)[-1]:
            return float(m.group(1)), m.group(2) == "True"
        time.sleep(0.04)
    return None


def part_knock():
    sim(HEAD + "; " + GUARD)
    for attempt in range(4):
        s0 = shots()
        lob = next_lob()
        if lob is None:
            return result("knock", False, "no lob in 60 s")
        g = send("shotguard")
        mm = re.match(r"(move [lr] \S+ \S+ \S+)", g)
        if mm:
            sim(mm.group(1) + " 40")
        if attempt == 0:
            # the bar with the bottle on its way, the bottle at the shield, knocked away, its fire far off
            t_eta = time.time() + lob[0]
            snap(TAG + "_knock_1_bar")
            for k, dt in ((2, -0.12), (3, 0.3), (4, 1.4)):
                time.sleep(max(0.0, t_eta + dt - time.time()))
                snap(TAG + "_knock_%d" % k)
        t0 = time.time()
        # the shield stays on every bar meanwhile (the other thrower's bottle must not be the fire that reaches the player)
        while time.time() - t0 < 4.0:
            g = send("shotguard", 4)
            mm = re.match(r"(move [lr] \S+ \S+ \S+)", g)
            if mm:
                sim(mm.group(1) + " 30")
            time.sleep(0.1)
        s1 = shots()
        k = s1.get("knocked", 0) - s0.get("knocked", 0)
        t1 = time.time()
        while k >= 1 and time.time() - t1 < 6 and s1.get("knockBursts", 0) + s1.get("fizzles", 0) <= s0.get("knockBursts", 0) + s0.get("fizzles", 0):
            time.sleep(0.2)   # the knocked bottle's flight (about a second; 7 m away)
            s1 = shots()
        if k < 1:
            log("  knock: attempt %d - knocked +0 (eta %.2f) | %s" % (attempt + 1, lob[0], send("shots?")[:200]))
            sim(HEAD + "; " + GUARD)
            continue
        bursts = s1.get("knockBursts", 0) - s0.get("knockBursts", 0)
        fizz = s1.get("fizzles", 0) - s0.get("fizzles", 0)
        kah = s1.get("knockedAreaHits", 0) - s0.get("knockedAreaHits", 0)
        ok = (bursts + fizz) >= 1 and s1.get("knockBurstMin", 0) >= 4.5 and kah == 0
        sim(HEAD + "; " + GUARD)
        return result("knock", ok, "the shield on the bar: knocked +%d, its burst %.1f m from the player (nearest knocked burst so far), fizzled +%d, "
                      "a knocked lob's fire on the player +%d, warning circles cleared +%d" % (
                          k, s1.get("knockBurstMin", 0), fizz, kah, s1.get("circlesCleared", 0) - s0.get("circlesCleared", 0)))
    result("knock", False, "no knock in 4 lobs")


def part_drop():
    sim(HEAD + "; " + DOWN)
    for attempt in range(4):
        s0 = shots()
        lob = next_lob()
        if lob is None:
            return result("drop", False, "no lob in 60 s")
        time.sleep(lob[0] + 1.2)
        s1 = shots()
        d = s1.get("drops", 0) - s0.get("drops", 0)
        if d < 1:
            log("  drop: attempt %d - drops +0 | %s" % (attempt + 1, send("shots?")[:200]))
            continue
        gap = s1.get("lastFireGap", -1)
        hits = s1.get("areaHits", 0) - s0.get("areaHits", 0)
        return result("drop", 0 <= gap <= 1.0 and hits >= 1, "no block: drops +%d, the fire %.2f m from the player, its fire on the player +%d" % (d, gap, hits))
    result("drop", False, "no drop in 4 lobs")


def room(deg):
    m = re.search(r"free ([\d.]+)(\+?) m", send("dash? free %d" % deg, 4))
    return 99.0 if m and m.group(2) else (float(m.group(1)) if m else 0.0)


def part_dodge():
    """A dash sideways while the bottle flies: to the side with the most floor (PF1P: the arena's edge cut the dash to
    1.85 m, inside the fire). Counted only when this bottle is the only one that dropped meanwhile (the other thrower's
    bottle lands on the player's new spot)."""
    send("vrdash uyf")
    for attempt in range(5):
        sim(HEAD + "; " + DOWN)
        time.sleep(1.0)
        # the side first (p2c3: the two floor queries made after the throw took longer than its 0.63 s flight - the fire
        # landed before the dash began)
        side = 1 if room(90) >= room(-90) else -1
        s0 = shots()
        lob = next_lob(0.55)
        if lob is None:
            return result("dodge", False, "no lob in 60 s")
        sim("stick l %d 0 0; btn r a 120" % side)
        time.sleep(0.25)
        sim("stick l 0 0 0")
        time.sleep(lob[0] + 1.0)
        s1 = shots()
        d = s1.get("drops", 0) - s0.get("drops", 0)
        dash = send("dash?")
        slide = re.search(r"slide ([\d.]+) m", dash.split("| last")[-1])
        slid = float(slide.group(1)) if slide else 0.0
        gap = s1.get("lastFireGap", -1)
        hits = s1.get("areaHits", 0) - s0.get("areaHits", 0)
        if d != 1 or slid < 2.6:
            log("  dodge: attempt %d set aside - drops +%d (one wanted), the dash slid %.2f m (2.6 wanted), the fire %.2f m away, on the player +%d" % (
                attempt + 1, d, slid, gap, hits))
            time.sleep(1.0)
            continue
        return result("dodge", gap >= 2.5 and hits == 0, "a dash of %.2f m while it flew: drops +%d, the fire %.2f m from the player, fire on the player +%d | %s" % (
            slid, d, gap, hits, dash.split("| last")[-1][:120]))
    result("dodge", False, "no clean dodge in 5 lobs")


def part_offview():
    # the throwers to the side: the gaze turned so the nearest one stands 75 deg to a side, and kept there (p2c3: the
    # nearest thrower switched between the two as they moved, no fixed yaw fit and the fallback left them in view -
    # off-view wind-ups +0 in 61 s). The bearing goes as b0 - k * yaw (k = +-1, measured here)
    send("warn? reset")
    sim(HEAD + "; " + DOWN)
    time.sleep(0.3)

    def bearing():
        mb = re.search(r"bearing (-?[\d.]+)", send("enemy? nearest", 4))
        return float(mb.group(1)) if mb else None
    b0 = bearing() or 0.0
    sim("head 0 1.65 0 20 0 0")
    time.sleep(0.4)
    b1 = bearing()
    k = -1.0 if (b1 is not None and b1 - b0 > 5.0) else 1.0
    tgt = 75.0 if b0 >= 0 else -75.0
    yaw = (b0 - tgt) / k
    sim("head 0 1.65 0 %.1f 0 0" % yaw)
    time.sleep(0.4)
    log("  offview: yaw sign %+.0f, head yaw %.0f: the nearest thrower at %s deg (wanted %.0f)" % (k, yaw, bearing(), tgt))
    w0 = nums(send("warn?"))
    t0 = time.time()
    took = False
    last_fix = time.time()
    while time.time() - t0 < 60:
        if user_in_vr():
            return
        if time.time() - last_fix > 1.0:
            last_fix = time.time()
            bn = bearing()
            if bn is not None and abs(abs(bn) - 75.0) > 15.0:
                yaw += (bn - tgt) / k
        sim("head 0 1.65 0 %.1f 0 0" % yaw)
        w = nums(send("warn?", 4))
        if w.get("shotWarnFrames", 0) > w0.get("shotWarnFrames", 0) and not took:
            pic(TAG + "_offview_bottle_in_flight")
            took = True
        if w.get("shotWarns", 0) > w0.get("shotWarns", 0) and w.get("offViewRanged", 0) > w0.get("offViewRanged", 0) and (took or not PICS):
            break
        time.sleep(0.1)
    w1 = nums(send("warn?"))
    el = time.time() - t0
    ann = w1.get("announces", 0) - w0.get("announces", 0)
    ok = w1.get("offViewRanged", 0) > w0.get("offViewRanged", 0) and w1.get("shotWarns", 0) > w0.get("shotWarns", 0) and ann <= 2 + el / 2.0
    result("offview", ok, "throwers to the side (%.0f s): off-view ranged wind-ups +%d, bottles in flight warned +%d (%d marker frames), heartbeats +%d, "
           "announcements +%d, unannounced +%d, min announce lead %.2f s" % (
               el, w1.get("offViewRanged", 0) - w0.get("offViewRanged", 0), w1.get("shotWarns", 0) - w0.get("shotWarns", 0),
               w1.get("shotWarnFrames", 0) - w0.get("shotWarnFrames", 0), w1.get("heartbeats", 0) - w0.get("heartbeats", 0),
               ann, w1.get("unannounced", 0) - w0.get("unannounced", 0), w1.get("minAnnounceLead", 99)))
    sim(HEAD)


PARTS = {"knock": part_knock, "drop": part_drop, "dodge": part_dodge, "offview": part_offview}


def main():
    parts = args[args.index("--parts") + 1].split(",") if "--parts" in args else ["knock", "drop", "dodge", "offview"]
    if "--no-fixture" not in args:
        boot = ["--boot"] if "GameState_Map" not in send("state?") else []
        r = subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--fixture", "lobs"] + boot +
                           ["--god", "--physical", "--equip", "BasicSword_noToken,BasicShield"], capture_output=True, text=True)
        log("fixture lobs: %s" % (((r.stdout or "").strip().splitlines() or ["?"])[-1]))
    if "Level_Play" not in send("state?"):
        result("fixture", False, "no fight: " + send("state?"))
        return 1
    send("god on")
    send("vrcombat physical")
    send("shots? reset")
    log("DLL %s" % send("state?")[:60])
    for p in parts:
        if user_in_vr():
            log("the user is in VR: stopping")
            break
        try:
            PARTS[p]()
        except Exception as ex:
            result(p, False, "error " + repr(ex))
    send("shots? log")
    sim(HEAD)
    save_snaps()
    if "--stay" not in args:
        subprocess.run(["python3", "-c", "import sys; sys.path.insert(0, %r); import indicator_shots; indicator_shots.leave()" % (H + "/scripts")])
    log("%s: %d/%d PASS" % (TAG, sum(OUT), len(OUT)))
    return 0 if OUT and all(OUT) else 1


if __name__ == "__main__":
    sys.exit(main())

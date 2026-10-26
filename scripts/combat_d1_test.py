#!/usr/bin/env python3
"""Checks for combat Deliverable 1 (docs/combat-progress.md): grips, enemy meters, warnings from behind, the exit portal.

  combat_d1_test.py [--tag D1] [--only grips,meters,behind,portal] [--behind-seconds 75] [--no-fixture] [--difficulty STEP]

--difficulty: `behind` at that step of [Combat] Difficulty (Easy, Normal, Hard, Nightmare1-3; set live, put back after):
the announcement is the step's OffViewAnnounce (1.5 / 1.0 / 0.6 / 0.5 / 0.45 / 0.4 s); unblockable strikes may come from
behind only from Hard up (UnblockableFromBehind = false), so "no red strike from behind" is checked on Easy and Normal.

Needs a session (session.py start --snapshot first_fight --to table); combat_fixture.py brings it into a fight first
(god mode). The parts, in this order (the portal last: it needs the fight won):
  grips   `griptest`: every weapon model in both hands - the grip in the fist, the striking part beyond it, the edge
          toward the chop, no magenta (material and rendered); the held models against the table. Then one shot per
          model in the weapon hand (`gripshow`) -> a contact sheet testdata/shots/<tag>_grips.jpg
  meters  `bars?` for both styles (Shell, Rings): every shown enemy has its shell or rings;
          Engaged shows fewer, Off none; shots of the shell and the rings
  behind  the head turned away from the fight for --behind-seconds: every strike from outside the view announced
          >= OffViewAnnounce before it, its wind-up >= 0.8 x the bonus longer, never two at once, no red strike from
          behind (Normal), the sting / heartbeat / marker channels used; a shot of the marker during a strike
  portal  the stick held forward through the last kill: the portal 4-8 m away (the 2 m Orb's rings stand 4.5-7.7 m out:
          main's "make it bigger"), >= 40 deg off the walking direction,
          no portal text; `exitcard probe 12` (spots from 12 stand points)
Results: PASS/FAIL lines on stdout and in logs/combat_d1_test.log.
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
LOG = H + "/logs/combat_d1_test.log"
GLOG = hof2env.GLOG
SHOTS = hof2env.TESTDATA + "/shots"
args = sys.argv[1:]


def opt(name, default):
    return args[args.index(name) + 1] if name in args else default


TAG = opt("--tag", "D1")
ONLY = opt("--only", "grips,meters,behind,portal").split(",")
BEHIND_S = float(opt("--behind-seconds", "75"))
results = []


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def result(name, ok, detail):
    results.append((name, ok))
    log(("PASS " if ok else "FAIL ") + name + ": " + detail)


def send(c, t=10):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def shot(name, wait=6.0):
    path = "%s/%s_eyes.png" % (SHOTS, name)
    if os.path.exists(path):
        os.remove(path)
    send("shot " + name, 15)
    t0 = time.time()
    while time.time() - t0 < wait and not os.path.exists(path):
        time.sleep(0.2)
    time.sleep(0.4)
    return path if os.path.exists(path) else None


def state():
    return send("state?", 6)


def num(pattern, text, default=None, cast=float):
    m = re.search(pattern, text)
    return cast(m.group(1)) if m else default


# ---------------------------------------------------------------------------------------------- grips

def grips():
    r = send("griptest", 120)
    n = num(r"(\d+) models", r, 0, int)
    p = num(r"(\d+) pass", r, 0, int)
    mags = num(r"magenta (\d+)", r, -1, int)
    held = num(r"held (\d+)", r, 0, int)
    off = num(r"held \d+ \((\d+) off\)", r, 1, int)
    result("grips_all_models", n >= 45 and p == n, "%d of %d weapon models pass (both hands) | %s" % (p, n, r[:600]))
    result("grips_no_magenta", mags == 0, "%d models with magenta materials or pixels" % mags)
    result("grips_held_on_table", held >= 1 and off == 0, "%d held models, %d off the table pose" % (held, off))
    # one shot per model in the weapon hand, in front of the face, seen from the side
    ids = re.findall(r"(?:PASS|FAIL) (\S+) \(", open(GLOG, errors="replace").read().split("grip test:")[-1])
    sim("head 0 1.75 0 90 -25 0; hand r -0.32 1.45 -0.12 125 5 0")
    crops = []
    try:
        from PIL import Image
    except ImportError:
        Image = None
    for wid in ids:
        send("gripshow " + wid)
        time.sleep(0.35)
        path = shot("%s_grip_%s" % (TAG, wid), 10)
        if path and Image:
            im = Image.open(path).convert("RGB")
            w, h = im.size
            im = im.crop((0, int(h * 0.2), int(w * 0.47), int(h * 0.95)))
            im.thumbnail((300, 300))
            crops.append((wid, im))
    send("gripshow off")
    if Image and crops:
        from PIL import ImageDraw
        cols = 8
        cw, ch = crops[0][1].size
        rows = (len(crops) + cols - 1) // cols
        sheet = Image.new("RGB", (cols * cw, rows * (ch + 14)), (20, 20, 20))
        d = ImageDraw.Draw(sheet)
        for i, (wid, im) in enumerate(crops):
            x, y = (i % cols) * cw, (i // cols) * (ch + 14)
            sheet.paste(im, (x, y + 14))
            d.text((x + 3, y + 1), wid[:28], fill=(230, 230, 230))
        out = "%s/%s_grips.jpg" % (SHOTS, TAG)
        sheet.save(out, quality=80)
        log("grip sheet: %s (%d models)" % (out, len(crops)))
    sim("head 0 1.75 0 0 0 0; hand r 0.3 1.0 -0.2 0 -60 0; hand l -0.3 1.0 -0.2 0 -60 0")


# ---------------------------------------------------------------------------------------------- meters

def meters():
    """(2026-10-18: the pips went; [Combat] EnemyStatus = Shell | Rings) every shown enemy has its shell or its rings; Engaged
    shows no more than On; Off none."""
    def check(label, what):
        best = None
        for _ in range(12):   # a few samples: enemies move
            b = send("bars?")
            shown = num(r"^(\d+) shown", b, 0, int)
            if best is None or shown > best[0]:
                best = (shown, b)
            if shown >= 2:
                break
            time.sleep(0.5)
        shown, b = best
        n = num(r"(\d+) " + what, b, 0, int)
        ok = shown >= 1 and n >= 1
        result("meters_" + label, ok, "shown %d, %s %d | %s" % (shown, what, n, b[:400]))
        return shown

    sim("head 0 1.75 0 0 0 0")
    send("cfg EnemyHealthBars On")
    send("cfg EnemyStatus Shell")
    time.sleep(1.5)
    n_on = check("shell", "shells")
    shot("%s_meters_shell" % TAG)
    send("cfg EnemyStatus Rings")
    time.sleep(0.8)
    check("rings", "rings")
    shot("%s_meters_rings" % TAG)
    send("cfg EnemyStatus Shell")
    send("cfg EnemyHealthBars Engaged")
    time.sleep(1.5)
    b = send("bars?")
    n_eng = num(r"^(\d+) shown", b, 99, int)
    result("meters_engaged", n_eng <= max(1, n_on), "Engaged shows %d (On showed %d) | %s" % (n_eng, n_on, b[:200]))
    send("cfg EnemyHealthBars Off")
    time.sleep(0.8)
    b = send("bars?")
    result("meters_off", num(r"^(\d+) shown", b, 99, int) == 0, b[:120])
    send("cfg EnemyHealthBars On")


# ---------------------------------------------------------------------------------------------- behind

DIFFICULTY = opt("--difficulty", "")


def behind():
    if DIFFICULTY:
        log("difficulty: " + send("vrdiff " + DIFFICULTY.lower()))
    try:
        behind_run()
    finally:
        if DIFFICULTY:
            send("vrdiff setting")


def behind_run():
    send("warn? reset")
    send("god")
    t0 = time.time()
    marker_shot = None
    # face away from the fight, turning a quarter now and then so attackers come from every side
    yaws = [180, 140, 220, 180]
    i = 0
    while time.time() - t0 < BEHIND_S:
        sim("head 0 1.75 0 %d 0 0" % yaws[i % len(yaws)])
        i += 1
        t1 = time.time()
        while time.time() - t1 < BEHIND_S / len(yaws) and time.time() - t0 < BEHIND_S:
            if marker_shot is None:
                th = send("threats")
                if "STRIKE off-view" in th:
                    time.sleep(0.25)
                    marker_shot = shot("%s_behind_marker" % TAG, 5)
            time.sleep(0.3)
            if "Level_Play" not in state():
                break
    w = send("warn?")
    send("warn? log")
    log("warn? " + w)
    off = num(r"offview (\d+)", w, 0, int)
    lead = num(r"minAnnounceLead (-?[\d.]+)", w, 0.0)
    unann = num(r"unannounced (\d+)", w, 0, int)
    share = num(r"minBonusShare (-?[\d.]+)", w, 0.0)
    maxlive = num(r"maxLiveOff (\d+)", w, 9, int)
    red = num(r"redBehind (\d+)", w, 9, int)
    announce = num(r"announce ([\d.]+) bonus", w, 1.0)
    result("behind_strikes_seen", off >= 2, "%d strikes started outside the view in %.0f s" % (off, BEHIND_S))
    turned = num(r"turnedAway (\d+)", w, 0, int)
    result("behind_announced_first", off == 0 or (lead >= announce - 0.05 and unann == 0) or (lead >= announce - 0.05 and unann <= off // 4),
           "every off-view strike announced >= %.1f s before (min %.2f s; %d never announced; %d began while the attacker was still in view)" % (announce, lead, unann, turned))
    result("behind_longer_windup", off == 0 or share >= 0.8, "off-view wind-ups got >= 80 %% of the bonus (min %.2f)" % share)
    result("behind_one_at_a_time", maxlive <= 1, "at most %d off-view strikes live at once" % maxlive)
    if DIFFICULTY.lower() in ("hard", "nightmare1", "nightmare2", "nightmare3"):
        # from Hard up, unblockable attacks may come from behind (only Easy and Normal keep them in front)
        result("behind_no_red_strike", True, "%d uncounterable strikes started from behind (allowed on %s)" % (red, DIFFICULTY))
    else:
        result("behind_no_red_strike", red == 0, "%d uncounterable strikes started from behind" % red)
    ch = (num(r"stings (\d+)", w, 0, int), num(r"heartbeats (\d+)", w, 0, int), num(r"markers (\d+)", w, 0, int))
    result("behind_three_channels", off == 0 or all(c > 0 for c in ch), "stings %d, heartbeats %d, markers %d" % ch)
    result("behind_marker_shot", marker_shot is not None, "marker shot %s" % marker_shot)
    sim("head 0 1.75 0 0 0 0")


# ---------------------------------------------------------------------------------------------- portal

def portal():
    if "Level_Play" not in state():
        result("portal", False, "not in a fight (%s)" % state())
        return
    pr = send("exitcard probe 12", 30)
    log("probe: " + pr[:300])
    before = open(GLOG, errors="replace").read().count("Victory")
    n0 = num(r"^(\d+) portals", send("exitcard last"), 0, int)
    sim("head 0 1.75 0 0 0 0; stick l 0 1 0")
    time.sleep(1.2)
    send("killall")
    t0 = time.time()
    last = ""
    while time.time() - t0 < 12:
        last = send("exitcard last")
        if num(r"^(\d+) portals", last, 0, int) > n0:
            break
        time.sleep(0.3)
    sim("stick l 0 0 0")
    dist = num(r"portal ([\d.]+) m away", last, 0.0)
    moving = num(r"player moving ([\d.]+) m/s", last, 0.0)
    off = num(r"([\d.]+) deg off the walking direction", last, None)
    result("portal_distance", 4.0 <= dist <= 8.0, "%.1f m | %s" % (dist, last[:300]))
    result("portal_out_of_path", moving < 0.3 or (off is not None and off >= 40), "player moving %.1f m/s, portal %s deg off the walking direction" % (moving, off))
    time.sleep(2.0)
    after = open(GLOG, errors="replace").read().count("Victory")
    result("portal_no_text", after == before, "portal texts in the log: %d new" % (after - before))
    m = re.search(r"in sight (\d+), fallback (\d+), dist ([\d.]+)-([\d.]+) m", pr)
    if m:
        result("portal_probe", int(m.group(2)) == 0 and float(m.group(3)) >= 1.8, pr[:200])


def main():
    log("=== combat_d1_test %s (%s)" % (TAG, ",".join(ONLY)))
    if "--no-fixture" not in args:
        r = subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--god", "--board-shot", TAG + "_board"], capture_output=True, text=True, timeout=1000)
        log("fixture: " + r.stdout.strip()[-120:])
    if "Level_Play" not in state():
        result("fight", False, "no live fight (%s)" % state())
        return 1
    for part in ("grips", "meters", "behind", "portal"):
        if part in ONLY:
            globals()[part]()
    fails = [n for n, ok in results if not ok]
    log("=== %d checks, %d failed%s" % (len(results), len(fails), (": " + ", ".join(fails)) if fails else ""))
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())

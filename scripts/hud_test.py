#!/usr/bin/env python3
"""The fight HUD and the enemies' guard/health (env 3, 2026-10-18; the user's pick of ~22:00: [Combat] FightHud = Board | Body,
[Combat] EnemyStatus = Shell | Rings; docs/decisions.md "The fight HUD and the enemies' guard/health").

  hud_test.py [--tag T] [--fixture NAME|--no-fixture] [--stay] [--parts a,b,...] [--equip CARDS]

Parts (in a live fight; combat_fixture.py's fixture, default thieves):
  flat     the flat fight HUD that floated on the head-following panel is hidden in VR: its health radial (PlayerHealthUI),
           the weapon's counter (PlayerWeaponUI) and the rest are scaled to nothing (fighthud? flatHidden)
  board    Board: placed 2.3-3.9 m away, beyond 0.53 m of the eyes, facing the player, at least 45 deg off every enemy's
           bearing (or "crowded"); then the head turned all round and moved: the board does not move (nothing follows the
           head); then the head turned away for 14 s: the board moves once, while out of view (movesInView 0), to a spot that
           follows the same rules
  body     Body: no board; with a fight timer (the `fighthud timer` seam: the fixtures have none) the band on the shield wrist
           shows and empties; a combo goal (`fighthud combo 6 2`): the gold band, "Combo 2 / 6" while the wrist is looked at;
           the timer row on the board in Board style
  shell    Shell: one enemy's guard set (poiseset) full / 70 % / 45 % / 20 %: the shell whole, then 1, 2, 3 crack sets and
           steps 1-3 (dimmer and thinner); 0: it
           shatters (shatters +1, shards flying) and is gone while broken; it re-forms (state "reforming") and is whole again;
           its health (enemyhp) 100 % / 15 %: the warm rim brighter / fainter
  rings    Rings: the outer ring's share follows the guard (70 %, 30 %), empty and flashing when broken; the inner ring's the
           health, "low" (deep orange) under 30 %; the ground found under them (12 rays)
  switch   the VR settings page (Looks tab) in the fight: Your info Board / On you and Enemy guard Shell / Rings switch the
           cfg and the fight at once
  dead     (kills two enemies: run it last) an enemy killed while its guard is broken: during its death fade the shell stays
           gone and the rings' guard arc empty (the HUD audit of 2026-10-19, item 1)
Results: PASS/FAIL lines on stdout; exit 1 on any FAIL.
"""
import math
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
TAG = args[args.index("--tag") + 1] if "--tag" in args else "HUD"
OUT = []
HEAD = "head 0 1.7 0 0 0 0"
HANDS = "hand r 0.25 1.1 -0.3 0 -40 0; hand l -0.25 1.1 -0.3 0 -40 0"
# the shell and rings parts: the blade down at the side - held forward it chipped the set guard of an enemy that walked into
# it (guards break in 3 sword blows since p3combat: the hud regression's rings_guard 0.7 -> 0.2, shell_reform never full)
HANDS_DOWN = "hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0"


def log(m):
    print(time.strftime("%H:%M:%S") + " " + m, flush=True)


def result(name, ok, detail):
    OUT.append(ok)
    log("%s %s: %s" % ("PASS" if ok else "FAIL", name, detail))
    return ok


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


def num(s, key, default=None):
    m = re.search(re.escape(key) + r" (-?[\d.]+)", s)
    return float(m.group(1)) if m else default


def board():
    """fighthud? -> a dict of the board's numbers (empty: no board)."""
    s = send("fighthud?")
    d = {"raw": s}
    m = re.search(r"board placed (\w+) at (-?[\d.]+),(-?[\d.]+),(-?[\d.]+)", s)
    if m:
        d["placed"] = m.group(1) == "True"
        d["pos"] = (float(m.group(2)), float(m.group(3)), float(m.group(4)))
    for k in ("dist", "eyeDist", "gaze", "enemyAngle", "faceDot", "alpha", "moves", "movesInView", "placed", "flatHidden"):
        v = num(s, " " + k)
        if v is not None and k != "placed":
            d[k] = v
    d["ghost"] = " ghost True" in s
    m = re.search(r" why (.*?) \| rows", s)
    d["why"] = m.group(1) if m else ""
    m = re.search(r"\| rows (.*?) \| placed", s)
    d["rows"] = m.group(1) if m else ""
    m = re.search(r"wrist (on ([\d.]+)|off)", s)
    d["wrist"] = float(m.group(2)) if m and m.group(2) else None
    return d


def dpos(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def poise(who="nearest"):
    s = send("poise? " + who)
    d = {"raw": s}
    m = re.search(r"guard ([\d.]+)/([\d.]+) phase (\w+)", s)
    if m:
        d["guard"], d["max"], d["phase"] = float(m.group(1)), float(m.group(2)), m.group(3)
    m = re.search(r"id #(-?\d+)", s)
    if m:
        d["id"] = "#" + m.group(1)
    return d


def entry(who):
    """bars?'s entry for the enemy `who` ("#id")."""
    s = send("bars?")
    for part in s.split(" | ")[1:]:
        if who in part:
            return part
    return ""


def recovered(who, timeout=18.0, seen=None):
    t0 = time.time()
    while time.time() - t0 < timeout:
        p = poise(who)
        if seen is not None:
            seen.append(entry(who))
        if p.get("phase") == "Up" and p.get("guard", 0) >= p.get("max", 1) - 0.6:
            return True
        time.sleep(0.3)
    return False


def nearest_up():
    p = poise()
    who = p.get("id", "nearest")
    if p.get("phase") != "Up":
        recovered(who, 10)
    return who, poise(who)


# ---------------------------------------------------------------------------------------------------------------- parts

def part_flat():
    b = board()
    s = b["raw"]
    names = re.search(r"flatHides \d+ \(([^)]*)\)", s)
    hid = names.group(1).split(",") if names else []
    ok = b.get("flatHidden", 0) >= 2 and "PlayerHealthUI" in hid
    return result("flat", ok, "flat HUD widgets hidden now %s: %s" % (b.get("flatHidden"), ",".join(hid)))


def part_board():
    send("vrhud board")
    sim(HEAD + "; " + HANDS)
    send("fighthud place")
    time.sleep(1.4)
    b = board()
    if "pos" not in b or not b.get("placed"):
        return result("board", False, "no board: " + b["raw"][:300])
    rows = []
    ok = 2.3 <= b.get("dist", 0) <= 3.9 and b.get("eyeDist", 0) >= 0.53 and b.get("faceDot", 0) >= 0.85
    ok_enemy = b.get("enemyAngle", 0) >= 45 or "crowded" in b["why"]
    rows.append("placed %.2f m away (eyes %.2f), facing %.2f, nearest enemy %.0f deg off, %s; rows: %s" % (
        b.get("dist", -1), b.get("eyeDist", -1), b.get("faceDot", -1), b.get("enemyAngle", -1), b["why"], b["rows"]))
    result("board_place", ok and ok_enemy, rows[-1])
    # nothing follows the head: turn and move the head all round; the board stays where it is
    p0, m0 = b["pos"], b.get("moves", 0)
    worst = 0.0
    for yaw in (0, 40, 80, 120, 160, 200, 240, 280, 320, 0):
        for dx, dz in ((0, 0), (0.3, 0), (-0.3, 0.2)):
            sim("head %.2f 1.7 %.2f %d 0 0" % (dx, dz, yaw))
            time.sleep(0.12)
            bb = board()
            if "pos" in bb:
                worst = max(worst, dpos(bb["pos"], p0))
    b1 = board()
    still = worst < 0.002 or b1.get("moves", 0) > m0
    result("board_still", still and b1.get("movesInView", 0) == 0,
           "head turned 360 deg and moved 0.3 m: the board moved %.4f m at most (moves %s -> %s, in view %s)" % (worst, m0, b1.get("moves"), b1.get("movesInView")))
    # facing away for long: it moves once, unseen
    away, gz = 0, -1.0
    for yaw in range(0, 360, 30):
        sim("head 0 1.7 0 %d 0 0" % yaw)
        time.sleep(0.15)
        g = board().get("gaze", -1)
        if g > gz:
            gz, away = g, yaw
    sim("head 0 1.7 0 %d 0 0" % away)
    m1 = board().get("moves", 0)
    t0 = time.time()
    while time.time() - t0 < 16 and board().get("moves", 0) == m1 and not user_in_vr():
        time.sleep(0.5)
    b2 = board()
    moved = b2.get("moves", 0) > m1
    # look for it again
    best, gmin = 0, 999.0
    for yaw in range(0, 360, 20):
        sim("head 0 1.7 0 %d 0 0" % yaw)
        time.sleep(0.12)
        g = board().get("gaze", 999)
        if g < gmin:
            gmin, best = g, yaw
    sim("head 0 1.7 0 %d 0 0" % best)
    time.sleep(0.3)
    b3 = board()
    rules = 2.3 <= b3.get("dist", 0) <= 3.9 and (b3.get("enemyAngle", 0) >= 45 or "crowded" in b3["why"])
    result("board_moves_unseen", moved and b3.get("movesInView", 1) == 0 and rules,
           "facing away (yaw %d, %.0f deg off) for %.0f s: moves %s -> %s, in view %s; found again at yaw %d: %.2f m, enemy %.0f deg off (%s) | last %s" % (
               away, gz, time.time() - t0, m1, b2.get("moves"), b3.get("movesInView"), best, b3.get("dist", -1), b3.get("enemyAngle", -1), b3["why"],
               re.search(r" last (.*?) \| wrist", b3["raw"]).group(1) if " last " in b3["raw"] else "-"))
    sim(HEAD)


def part_body():
    send("vrhud body")
    sim(HEAD + "; " + HANDS)
    t = send("fighthud timer 40 Hold out")
    time.sleep(1.5)
    b = board()
    f1 = b.get("wrist")
    time.sleep(3.0)
    f2 = board().get("wrist")
    ok = "pos" not in b and f1 is not None and f2 is not None and f2 < f1 and 0.8 <= f1 <= 1.0
    result("body_wrist", ok, "%s | no board: %s | wrist band fill %s then %s" % (t, "pos" not in b, f1, f2))
    # the HUD audit of 2026-10-19, item 3: a combo goal on you - the gold band on the wrist, and "Combo 2 / 6" over it while the
    # wrist is looked at (the `fighthud combo` seam: the fixtures' fights have no combo objective)
    send("fighthud combo 6 2")
    time.sleep(0.6)
    s1 = send("fighthud?")
    sim("head 0 1.7 0 0 -45 0; hand l -0.05 1.25 -0.32 90 0 -90; hand r 0.3 0.95 0.0 0 -70 0")
    time.sleep(0.8)
    s2 = send("fighthud?")
    send("fighthud combo 0")
    sim(HEAD + "; " + HANDS)
    c1 = re.search(r" combo (\S+ label \S+( \S+ \S+ \S+)?)", s1)
    c2 = re.search(r" combo (\S+ label \S+( \S+ \S+ \S+)?)", s2)
    result("body_combo", " combo 2 label" in s1 and "label 'Combo 2 / 6'" in s2,
           "hands in front: %s | looking at the wrist: %s" % (c1.group(1) if c1 else s1[-60:], c2.group(1) if c2 else s2[-60:]))
    send("vrhud board")
    time.sleep(1.4)
    rows = board()["rows"]
    result("board_timer_row", "timer 0:" in rows and "Hold out" in rows, "board rows: " + rows)
    send("fighthud timer 0")


# the shell's re-form wait: both hands behind the body (hudv4: with the hands down at the sides the regrown guard was chipped
# again - 18.9 -> 9.5 -> broken - within the 40 s wait)
AWAY = "hand r 0.45 0.95 0.3 0 -80 0; hand l -0.45 1.0 0.25 0 0 0"


def part_shell():
    send("vrstatus shell")
    sim(HEAD + "; " + AWAY)
    who, p = nearest_up()
    if "max" not in p:
        return result("shell", False, "no enemy guard: " + p["raw"][:200])
    mx = p["max"]
    send("face " + who)
    send("enemyhp %s 1" % who)
    send("poiseset %s -1" % who)
    time.sleep(1.5)
    states = []
    e = entry(who)
    states.append(("full", e))
    # the warm rim, or the game's own attack glow it waits for (an enemy close and attacking: hudv3b)
    ok_whole = " shell whole step0" in e and re.search(r"parts [1-9]", e) is not None and (" rim on" in e or " rim game" in e)
    # the steps (2026-10-19): each crack level is also a brightness/thickness step
    want = {0.7: "cracked1 step1", 0.45: "cracked2 step2", 0.2: "cracked3 step3"}
    ok_cracks = True
    for share, st in want.items():
        send("poiseset %s %.3f" % (who, mx * share))
        time.sleep(0.5)
        e = entry(who)
        states.append((share, e))
        ok_cracks &= (" shell " + st) in e
    sh0 = num(send("shell"), "shatters", 0)
    send("poiseset %s 0" % who)
    time.sleep(0.05)
    e_break = entry(who)
    d = send("shell")
    time.sleep(0.4)
    e_broken = entry(who)
    ok_break = num(d, "shatters", 0) > sh0 and (num(d, "shards", 0) or 0) > 0 and (" shell broken" in e_broken or " shell breaking" in e_broken)
    seen = []
    # the break, the stagger, then the guard regrows over ~10 s - the hero kept 4.5 m off it meanwhile (hudv4, hudv4b: its blows
    # blocked by the shield, held behind the body too, chipped the regrown guard 10.5 a block: "Guard: block ThievesThug
    # (shield)", "Poise: block ThievesThug -10.5 -> 4.4/21", and broke it again before it was whole)
    back = False
    last = 0.0
    t0 = time.time()
    while time.time() - t0 < 40:
        if time.time() - last > 2.0:
            send("heroat %s 0 4.5" % who)
            last = time.time()
        pp = poise(who)
        seen.append(entry(who))
        if pp.get("phase") == "Up" and pp.get("guard", 0) >= pp.get("max", 1) - 0.6:
            back = True
            break
        time.sleep(0.3)
    reform = any(" shell reforming" in x for x in seen)
    # the HUD audit of 2026-10-19, item 4: the re-forming sweep follows the hold (it stood at 0.00: the guard is empty through
    # Recover), and Up begins at the nearly-gone step's thickness (it flashed a whole 4 cm shell for ~0.4 s first)
    sweep = [float(m.group(1)) for m in (re.search(r" shell reforming ([\d.]+)", x) for x in seen) if m]
    # the first Up entry after the first re-forming (hudv4: a second break near the wait's end left none after the last one)
    first_rf = next((i for i, x in enumerate(seen) if " shell reforming" in x), -1)
    end_rf = next((i for i in range(first_rf, len(seen)) if " shell reforming" not in seen[i]), len(seen)) if first_rf >= 0 else len(seen)
    first_up = next((x for x in seen[end_rf:] if re.search(r" shell (whole|cracked)", x)), "")
    up_thick = num(first_up, "thick")
    time.sleep(0.5)
    e_back = entry(who)
    result("shell_whole", ok_whole, "full guard: " + states[0][1][:220])
    result("shell_cracks", ok_cracks, " | ".join("%s: %s" % (s, re.search(r"shell \S+", e).group(0) if re.search(r"shell \S+", e) else e[:80]) for s, e in states[1:]))
    result("shell_break", ok_break, "break: %s | shards %s | broken: %s" % (re.search(r"shell \S+", e_break).group(0) if "shell" in e_break else e_break[:80], num(d, "shards"), e_broken[:160]))
    result("shell_reform", back and reform and " shell whole" in e_back, "re-formed: %s, reforming seen %s, then: %s" % (back, reform, e_back[:160]))
    result("shell_recover_look", bool(sweep) and max(sweep) >= 0.5 and up_thick is not None and up_thick <= 2.6,
           "the sweep over the hold reached %s (wants >= 0.5); the first Up entry after it: %s (thick %s cm, wants <= 2.6)" % (
               max(sweep) if sweep else None, re.search(r"shell \S+ \S+", first_up).group(0) if re.search(r"shell \S+ \S+", first_up) else first_up[:60], up_thick))
    # health: the warm rim
    def rim(share):
        send("enemyhp %s %s" % (who, share))
        t0 = time.time()
        while time.time() - t0 < 6:
            time.sleep(0.4)
            r = num(entry(who), "rim on")
            if r is not None:
                return r
        return None
    r1 = rim(1)
    r2 = rim(0.15)
    result("shell_health", r1 is not None and r2 is not None and r2 < r1 * 0.5,
           "warm rim strength at full health %s, at 15 %% %s (the game's own glows yield: %s)" % (r1, r2, re.search(r"gameRimPlays \d+ yields \d+", send("shell")).group(0)))
    send("enemyhp %s 1" % who)


def part_rings():
    send("vrstatus rings")
    sim(HEAD + "; " + HANDS_DOWN)
    who, p = nearest_up()
    if "max" not in p:
        return result("rings", False, "no enemy guard: " + p["raw"][:200])
    mx = p["max"]
    send("enemyhp %s 1" % who)
    send("poiseset %s -1" % who)
    time.sleep(1.0)
    e0 = entry(who)
    send("poiseset %s %.3f" % (who, mx * 0.7))
    time.sleep(0.6)
    e1 = entry(who)
    # 30 %: a blow of its own into the held shield (a block chips the attacker) breaks so low a guard - then the ring rightly
    # shows the break (hud regression, twice); try again once it is up
    for _ in range(3):
        send("poiseset %s %.3f" % (who, mx * 0.3))
        time.sleep(0.6)
        e2 = entry(who)
        if " phase Up" in e2:
            break
        log("  rings: the guard broke at 30 %% (%s) - again" % e2[:120])
        recovered(who, 30)
    send("enemyhp %s 0.2" % who)
    send("poiseset %s 0" % who)
    time.sleep(0.4)
    e3 = entry(who)
    g = lambda e: num(e, "rings r") is not None and num(e.split("rings r")[1], "guard")
    gs = [g(e0), g(e1), g(e2)]
    ok = all(x is not None for x in gs) and abs(gs[0] - 1) < 0.05 and abs(gs[1] - 0.7) < 0.08 and abs(gs[2] - 0.3) < 0.08
    result("rings_guard", ok, "outer ring %s at full / 70 %% / 30 %%: %s | %s" % (gs, e1[:200], e2[:200]))
    ground = num(e0.split("rings r")[1], "ground") if "rings r" in e0 else None
    result("rings_ground", ground is not None and ground >= 8, "ground found under the rings: %s of 12 rays (r %s)" % (ground, num(e0, "rings r")))
    result("rings_broken_low", " flashing" in e3 and " low" in e3, "broken + 20 %% health: %s" % e3[:220])
    recovered(who, 30)
    send("enemyhp %s 1" % who)
    send("vrstatus shell")


def part_switch():
    import playtest as pt
    import switches_test as sw
    pt.LOG = H + "/logs/hud_test_switch.log"
    send("vrhud setting")
    send("vrstatus setting")
    if not sw.open_page("Looks"):
        return result("switch_page", False, "the VR settings page did not open: " + pt.vrset_now()[:80])
    time.sleep(0.5)
    send("shot %s_settings" % TAG, 20)
    res = []
    for key, ck, want, live in (("Your_info/On_you", "FightHud", "Body", lambda: "style Body" in send("fighthud?")),
                                ("Your_info/Board", "FightHud", "Board", lambda: "style Board" in send("fighthud?")),
                                ("Enemy_guard/Rings", "EnemyStatus", "Rings", lambda: "style Rings" in send("bars?")),
                                ("Enemy_guard/Shell", "EnemyStatus", "Shell", lambda: "style Shell" in send("bars?"))):
        ok, msg = sw.poke_row(key, ck, want)
        lv = live()
        res.append((ok and lv, "%s (live %s)" % (msg, lv)))
    sw.close_page()
    for (ok, msg), name in zip(res, ("switch_body", "switch_board", "switch_rings", "switch_shell")):
        result(name, ok, msg)


def part_dead():
    """The HUD audit of 2026-10-19, item 1: a dying enemy's guard does not grow back during its 1.5 s death fade. Per style:
    an enemy's guard broken (poiseset 0), then killed while broken (enemyhp kill: the game's own kill); bars? every 0.1 s
    for 1.4 s: the shell never whole / cracked / reforming, the rings' guard arc empty."""
    for style in ("shell", "rings"):
        send("vrstatus " + style)
        sim(HEAD + "; " + HANDS_DOWN)
        who, p = nearest_up()
        if "max" not in p:
            result("dead_" + style, False, "no enemy guard: " + p["raw"][:200])
            continue
        send("face " + who)
        send("poiseset %s 0" % who)
        time.sleep(0.5)
        e0 = entry(who)
        k = send("enemyhp %s kill" % who)
        seen = []
        t0 = time.time()
        while time.time() - t0 < 1.4:
            e = entry(who)
            if e:
                seen.append(e)
            time.sleep(0.1)
        if style == "shell":
            bad = [x for x in seen if re.search(r" shell (whole|cracked|reforming)", x)]
        else:
            bad = [x for x in seen if "rings r" in x and (num(x.split("rings r")[1], "guard") or 0) > 0.02]
        ok = "killed" in k and len(seen) >= 3 and not bad
        result("dead_" + style, ok, "%s | before the kill: %s | %d entries in the fade, %d with a guard back: %s" % (
            k[:60], (re.search(r"(shell|rings r) .{0,60}", e0).group(0) if re.search(r"(shell|rings r) .{0,60}", e0) else e0[:80]),
            len(seen), len(bad), (bad[0][:200] if bad else (seen[-1][:200] if seen else "-"))))
    send("vrstatus shell")


PARTS = {"flat": part_flat, "board": part_board, "body": part_body, "shell": part_shell, "rings": part_rings, "switch": part_switch, "dead": part_dead}


def main():
    parts = args[args.index("--parts") + 1].split(",") if "--parts" in args else ["flat", "board", "body", "shell", "rings", "switch"]
    fixture = args[args.index("--fixture") + 1] if "--fixture" in args else "thieves"
    equip = args[args.index("--equip") + 1] if "--equip" in args else "BasicSword_noToken,BasicShield"
    if "--no-fixture" not in args and "Level_Play" not in send("state?"):
        boot = ["--boot"] if "GameState_Map" not in send("state?") else []
        r = subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--fixture", fixture] + boot + ["--god", "--physical", "--equip", equip], capture_output=True, text=True)
        log("fixture %s: %s" % (fixture, ((r.stdout or "").strip().splitlines() or ["?"])[-1]))
    if "Level_Play" not in send("state?"):
        result("fixture", False, "no fight: " + send("state?"))
        return 1
    send("god on")
    log("DLL %s | %s" % (send("state?")[:60], send("fighthud?")[:120]))
    for p in parts:
        if user_in_vr():
            log("the user is in VR: stopping")
            break
        try:
            PARTS[p]()
        except Exception as ex:
            result(p, False, "error " + repr(ex))
    send("vrhud setting")
    send("vrstatus setting")
    sim(HEAD)
    if "--stay" not in args:
        subprocess.run(["python3", "-c", "import sys; sys.path.insert(0, %r); import indicator_shots; indicator_shots.leave()" % (H + "/scripts")])
    log("%s: %d/%d PASS" % (TAG, sum(OUT), len(OUT)))
    return 0 if OUT and all(OUT) else 1


if __name__ == "__main__":
    sys.exit(main())

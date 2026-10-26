#!/usr/bin/env python3
"""Both-eye pictures of the new fight HUD and enemy status for the user (env 3, 2026-10-18) -> the main worktree's
HandOfFate2/captures/hud-built-2026-10-18/ (or $HUD_PICS), in a live fight of this environment.

  hud_pics.py PREFIX [--jump FIGHT --with SPEC] [--parts board,body,shell,rings]

  board   the board standing at the side (a wide view with the enemies, then looking at it), with a fight timer
          (the `fighthud timer` seam: these fights have none of their own)
  body    Body: no board; the shield hand turned like a watch: the wrist timer and the health gems; the weapon hand's glow
  shell   Shell on one enemy from 2.6 m: full guard, worn down (45 %: cracked), the break (shattering), nearly dead (10 %
          health, the guard back)
  rings   the same four with Rings
  near    (v3, 2026-10-19) the shell before / after the near fade, one frozen moment each at 1, 2 and 4 m (before =
          bd1946c's look, `shell tune old 1`); at 1 m frozen during a blow when one comes (its wind-up)
  force   every shell forced to full / 67 / 40 / 20 %, one frozen moment: after, and before (bd1946c's look)
  kill    (v4) an enemy killed while broken, during its death fade (Shell, then Rings: kills two enemies)
  recover (v4) the shell during the recovery hold after a break (half way, nearly done)
  bodycombo (v4) On you with a combo goal (`fighthud combo 8 3`): the wrist's gold band and "Combo 3 / 8"
--env STATE (e.g. Night): the arena's lighting state switched first (`envstate`, as the game's debug menu does)
`sheet` (hud_pics.py sheet): the labelled contact sheet of everything in the folder (sheet.jpg).
"""
import json
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H
OUT = os.environ.get("HUD_PICS", "/home/bara/Projects/flat2vr/HandOfFate2/captures/hud-built-2026-10-18")
SHOTS = H + "/testdata/shots"
HANDS = "hand r 0.3 0.95 -0.15 0 -60 0; hand l -0.3 0.95 -0.15 0 -60 0"
LABELS = OUT + "/labels.json"


def log(m):
    print(time.strftime("%H:%M:%S") + " " + m, flush=True)


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


def label(name, title, text):
    os.makedirs(OUT, exist_ok=True)
    d = {}
    if os.path.exists(LABELS):
        d = json.load(open(LABELS))
    d[name] = [title, text]
    json.dump(d, open(LABELS, "w"), indent=1)


def shot(name, title, text):
    if user_in_vr():
        return None
    os.makedirs(OUT, exist_ok=True)
    src = SHOTS + "/HP_" + name + "_eyes.png"
    try:
        os.remove(src)
    except OSError:
        pass
    send("shot HP_" + name, 20)
    t0 = time.time()
    while time.time() - t0 < 6 and not (os.path.exists(src) and os.path.getsize(src) > 0):
        time.sleep(0.05)
    time.sleep(0.3)
    if not os.path.exists(src):
        log("NO SHOT " + name)
        return None
    from PIL import Image
    Image.open(src).convert("RGB").save(OUT + "/" + name + ".jpg", quality=90)
    label(name, title, text)
    log("saved " + name)
    return OUT + "/" + name + ".jpg"


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


def recovered(who, timeout=18.0):
    t0 = time.time()
    while time.time() - t0 < timeout:
        p = poise(who)
        if p.get("phase") == "Up" and p.get("guard", 0) >= p.get("max", 1) - 0.6:
            return True
        time.sleep(0.3)
    return False


def gaze():
    m = re.search(r" gaze (\d+)", send("fighthud?"))
    return int(m.group(1)) if m else 999


def find_board():
    best, gmin = 0, 999
    for yaw in range(0, 360, 15):
        sim("head 0 1.7 0 %d -6 0" % yaw)
        time.sleep(0.12)
        g = gaze()
        if g < gmin:
            gmin, best = g, yaw
    return best, gmin


def enemy(which="nearest"):
    p = poise(which)
    who = p.get("id", "nearest")
    if p.get("phase") not in (None, "Up"):
        recovered(who, 12)
    return who, poise(who)


YAW = [0.0, 1.0]   # the head's yaw now, the sign that turns the view toward a positive bearing


def bearing(who):
    m = re.search(r" bearing (-?\d+)", send("enemy? " + who))
    return float(m.group(1)) if m else None


def aim(who, off=0.0, pitch=-6):
    """Turns the head until `who` is `off` deg off the gaze (the bearing's sign learnt on the way)."""
    for _ in range(5):
        b = bearing(who)
        if b is None or abs(b - off) < 8:
            return b
        step = (b - off) * YAW[1]
        YAW[0] += step
        sim("head 0 1.7 0 %.0f %d 0; %s" % (YAW[0], pitch, HANDS))
        time.sleep(0.35)
        b2 = bearing(who)
        if b2 is not None and abs(b2 - off) > abs(b - off):
            YAW[1] = -YAW[1]
    return bearing(who)


def stand(who, dist=2.6):
    send("heroat %s 0 %.2f" % (who, dist))
    send("face " + who)
    YAW[0] = 0.0
    sim("head 0 1.7 0 0 -6 0; " + HANDS)
    time.sleep(0.6)
    aim(who)


def part_board(px, place):
    send("vrhud board")
    send("fighthud timer 75 %s" % ("Hold out" if "night" in place else "Goblins escape"))
    who, p = enemy()
    stand(who, 3.0)
    send("face track off")
    send("fighthud place")
    time.sleep(1.5)
    yaw, g = find_board()
    # wide: the board at the side, the nearest enemy toward the middle of the view
    YAW[0] = yaw
    sim("head 0 1.7 0 %d -6 0; %s" % (yaw, HANDS))
    time.sleep(0.4)
    b = bearing(who)
    if b is not None:
        aim(who, b * 0.45 if abs(b) < 80 else (35 if b > 0 else -35))
    time.sleep(0.4)
    send("timescale 0")
    shot(px + "_board_wide", "Board (%s)" % place, "Placed when the fight began, a few metres to the side; it stays put. Enemies ahead, nothing in the way.")
    send("timescale 1")
    sim("head 0 1.7 0 %d -6 0; %s" % (yaw, HANDS))
    YAW[0] = yaw
    time.sleep(0.6)
    send("timescale 0")
    shot(px + "_board_look", "Board, looked at (%s)" % place, "Health 52/100 style, the fight's timer, the special (READY + the stick to click when full).")
    send("timescale 1")
    log("board: " + send("fighthud?")[:300])


def part_body(px, place):
    send("vrhud body")
    send("super? set 100")
    who, p = enemy()
    stand(who, 3.2)
    # the shield hand turned like a watch, the weapon hand raised beside it (its glow: the special ready)
    for i, pose in enumerate(("hand l -0.1 1.35 -0.34 -60 20 -90", "hand l -0.22 1.2 -0.3 -70 30 -90")):
        sim("head 0 1.7 0 %.0f %d 0; %s; hand r 0.22 1.25 -0.4 0 10 0" % (YAW[0], -30 if i == 0 else -45, pose))
        time.sleep(0.8)
        send("timescale 0")
        shot(px + "_body_wrist%d" % i, "Body (%s)" % place, "Nothing in the air: the timer is the green band on the shield wrist (it empties), the health gems on the glove, the weapon hand glows: special ready.")
        send("timescale 1")
    send("super? set 0")
    send("vrhud board")


def states(px, place, style):
    send("vrstatus " + style.lower())
    who, p = enemy()
    if "max" not in p:
        log("no guard: " + p["raw"][:100])
        return
    mx = p["max"]
    send("enemyhp %s 1" % who)
    send("poiseset %s -1" % who)
    stand(who, 2.6)
    time.sleep(0.8)
    what = {"Shell": ("the blue-white shell is whole: bright and thick; the pale gold glow on the body: full health", "the guard is low (40 %): the shell is a step dimmer and thinner each time, and cracked",
                      "the guard breaks: the shell shatters (shards), now cut the gold slash", "the guard is back, the pale gold glow nearly gone: almost dead"),
            "Rings": ("outer blue ring = guard (full), inner gold ring = health (full)", "the blue ring has emptied with the hits (40 %); the health ring is still full",
                      "the guard is broken: the blue ring is empty and flashes; cut the gold slash", "health low: the inner ring is deep orange (red only means can't block)")}[style]
    send("timescale 0")   # first frozen, then the hero placed 2.6 m from the enemy (it walks in meanwhile)
    send("heroat %s 0 2.6" % who)
    time.sleep(0.3)
    aim(who)
    shot("%s_%s_1full" % (px, style.lower()), "%s - full guard (%s)" % (style, place), what[0])
    send("timescale 1")
    send("poiseset %s %.3f" % (who, mx * 0.4))
    time.sleep(1.0)
    send("timescale 0")   # first frozen, then the hero placed 2.6 m from the enemy (it walks in meanwhile)
    send("heroat %s 0 2.6" % who)
    time.sleep(0.3)
    aim(who)
    shot("%s_%s_2worn" % (px, style.lower()), "%s - worn down (%s)" % (style, place), what[1])
    send("timescale 1")
    aim(who)
    send("poiseset %s 0" % who)
    time.sleep(0.09)
    send("timescale 0")
    shot("%s_%s_3broken" % (px, style.lower()), "%s - guard broken (%s)" % (style, place), what[2])
    send("timescale 1")
    recovered(who, 18)
    send("enemyhp %s 0.1" % who)
    if style == "Rings":
        send("poiseset %s %.3f" % (who, mx * 0.6))
    stand(who, 2.6)
    time.sleep(1.0)
    send("timescale 0")   # first frozen, then the hero placed 2.6 m from the enemy (it walks in meanwhile)
    send("heroat %s 0 2.6" % who)
    time.sleep(0.3)
    aim(who)
    shot("%s_%s_4nearlydead" % (px, style.lower()), "%s - nearly dead (%s)" % (style, place), what[3])
    send("timescale 1")
    send("enemyhp %s 1" % who)
    far_steps(px, place, style, who, mx)
    send("face track off")


FAR = 4.5
STEPS = ((1.0, "a", "full"), (0.67, "b", "one hit (67 %)"), (0.4, "c", "two hits (40 %)"), (0.2, "d", "nearly broken (20 %)"))


def far_steps(px, place, style, who, mx):
    """The guard's steps from 4.5 m (2026-10-19: "full" and "worn" must read apart at a glance at 2-5 m): one enemy at full,
    67, 40 and 20 % guard; then a strip of the left eye's middle, side by side."""
    _, p = enemy(who)
    if p.get("phase") != "Up":
        return
    stand(who, FAR)
    names = []
    for share, tag, txt in STEPS:
        send("poiseset %s %s" % (who, "-1" if share >= 1 else "%.3f" % (mx * share)))
        time.sleep(1.0)
        send("timescale 0")
        send("heroat %s 0 %.2f" % (who, FAR))
        time.sleep(0.3)
        aim(who)
        n = "%s_%s_far%s" % (px, style.lower(), tag)
        if shot(n, "%s - %s at %.1f m (%s)" % (style, txt, FAR, place), "the guard at %s, seen from %.1f m" % (txt, FAR)):
            names.append((n, txt))
        send("timescale 1")
    send("poiseset %s -1" % who)
    try:
        from PIL import Image, ImageDraw
        crops = []
        for n, txt in names:
            im = Image.open(OUT + "/" + n + ".jpg").convert("RGB")
            w, h = im.size
            cx, cy = w // 4, h // 2
            c = im.crop((cx - 130, cy - 170, cx + 130, cy + 150)).resize((390, 480))
            ImageDraw.Draw(c).text((8, 6), txt, fill=(255, 255, 255))
            crops.append(c)
        if crops:
            strip = Image.new("RGB", (390 * len(crops), 480))
            for i, c in enumerate(crops):
                strip.paste(c, (390 * i, 0))
            strip.save(OUT + "/%s_%s_steps.jpg" % (px, style.lower()), quality=92)
            log("saved %s_%s_steps (strip of %d)" % (px, style.lower(), len(crops)))
    except Exception as ex:
        log("strip: %r" % ex)


def part_force(px, place):
    """The shell's steps side by side from one frozen moment (`shell tune force <share>`: every shell shows that guard): the
    same enemies at the same places, near and far, full / one hit / two hits / nearly broken."""
    send("vrstatus shell")
    who, p = enemy()
    stand(who, 3.2)
    time.sleep(0.6)
    send("timescale 0")
    names = []
    try:
        for share, tag, txt in STEPS:
            send("shell tune force %s" % ("-1" if share >= 1 else "%.2f" % share))
            for old in (0, 1):
                send("shell tune old %d" % old)
                time.sleep(1.2)   # the shell eases on unscaled time
                n = "%s_shell_force%s%s" % (px, tag, "_before" if old else "")
                if shot(n, "Shell - every enemy at %s (%s)%s" % (txt, place, ", BEFORE (bd1946c)" if old else ""),
                        "one frozen moment, every shell forced to the guard at %s%s" % (txt, "; bd1946c's look (shell tune old 1)" if old else "; this build")):
                    names.append((n, txt + (" - before" if old else "")))
    finally:
        send("shell tune force -1")
        send("shell tune old 0")
        send("timescale 1")
    try:
        from PIL import Image, ImageDraw
        for suffix, keep in (("", lambda t: "before" not in t), ("_before", lambda t: "before" in t)):
            tiles = []
            for n, txt in names:
                if not keep(txt):
                    continue
                im = Image.open(OUT + "/" + n + ".jpg").convert("RGB")
                w, h = im.size
                t = im.crop((0, 0, w // 2, h)).resize((w // 4, h // 2))
                ImageDraw.Draw(t).text((8, 6), txt, fill=(255, 255, 255))
                tiles.append(t)
            if tiles:
                tw, th = tiles[0].size
                strip = Image.new("RGB", (tw * 2, th * ((len(tiles) + 1) // 2)))
                for i, t in enumerate(tiles):
                    strip.paste(t, ((i % 2) * tw, (i // 2) * th))
                strip.save(OUT + "/%s_shell_force_steps%s.jpg" % (px, suffix), quality=92)
                log("saved %s_shell_force_steps%s" % (px, suffix))
    except Exception as ex:
        log("force strip: %r" % ex)


NEAR = (1.0, 2.0, 4.0)


def part_near(px, place):
    """v3 (2026-10-19): the shell near the player, before (bd1946c's look: `shell tune old 1`) and after, from one frozen moment
    at each of 1, 2 and 4 m; at 1 m the fight is frozen during a blow when one comes in 25 s (the wind-up the shell hid)."""
    send("vrstatus shell")
    who, p = enemy()
    if "max" not in p:
        log("no guard: " + p["raw"][:100])
        return
    send("enemyhp %s 1" % who)
    send("poiseset %s -1" % who)
    names = []
    try:
        for d in NEAR:
            stand(who, d)
            send("face track " + who)
            caught = ""
            if d <= 1.0:
                t0 = time.time()
                while time.time() - t0 < 25 and not caught:
                    first = send("strike?", 4).split(" | ")[0]
                    m = re.search(r"eta (-?[\d.]+)", first)
                    if m and 0.12 <= float(m.group(1)) <= 0.8 and "offview" not in first:
                        caught = first[:60]
                        break
                    send("heroat %s 0 %.2f" % (who, d))
                    time.sleep(0.05)
                log("1 m: blow %s" % (caught or "none in 25 s"))
            send("face track off")   # (v3's second run: the tracking kept turning the view between before and after)
            send("timescale 0")
            send("heroat %s 0 %.2f" % (who, d))
            time.sleep(0.3)
            aim(who)
            for old, tag in ((1, "before"), (0, "after")):
                send("shell tune old %d" % old)
                time.sleep(1.0)
                n = "%s_near_%dm_%s" % (px, d, tag)
                what = ("BEFORE (bd1946c's look)" if old else "AFTER (this build)") + (", during a blow's wind-up" if caught else "")
                if shot(n, "Shell at %.0f m - %s (%s)" % (d, "before" if old else "after", place), "full guard, one frozen moment: " + what):
                    names.append(n)
            log("%.0f m: %s" % (d, re.sub(r".*?(shell \S+ .*?rim [^|]*).*", r"\1", send("bars?"))[:200]))
            send("shell tune old 0")
            send("timescale 1")
            send("face track off")
    finally:
        send("shell tune old 0")
        send("timescale 1")
        send("face track off")
    try:
        from PIL import Image, ImageDraw
        rows = []
        for d in NEAR:
            pair = [OUT + "/%s_near_%dm_%s.jpg" % (px, d, t) for t in ("before", "after")]
            if not all(os.path.exists(x) for x in pair):
                continue
            ims = []
            for x, t in zip(pair, ("before", "after")):
                im = Image.open(x).convert("RGB")
                w, h = im.size
                c = im.crop((0, 0, w // 2, h))
                ImageDraw.Draw(c).text((8, 6), "%.0f m - %s" % (d, t), fill=(255, 255, 255))
                ims.append(c)
            row = Image.new("RGB", (ims[0].size[0] * 2, ims[0].size[1]))
            row.paste(ims[0], (0, 0))
            row.paste(ims[1], (ims[0].size[0], 0))
            rows.append(row)
        if rows:
            out = Image.new("RGB", (rows[0].size[0], rows[0].size[1] * len(rows)))
            for i, r in enumerate(rows):
                out.paste(r, (0, i * rows[0].size[1]))
            out.save(OUT + "/%s_near_before_after.jpg" % px, quality=90)
            log("saved %s_near_before_after" % px)
    except Exception as ex:
        log("near strip: %r" % ex)


def part_kill(px, place):
    """v4 (the HUD audit of 2026-10-19, item 1): an enemy killed while its guard is broken, during its death fade - Shell, then
    Rings (another enemy each). Before the fix the corpse's shell grew back whole as it fell (Rings: a full blue ring)."""
    for style in ("Shell", "Rings"):
        send("vrstatus " + style.lower())
        who, p = enemy()
        if "max" not in p:
            log("kill: no guard: " + p["raw"][:100])
            continue
        send("enemyhp %s 1" % who)
        stand(who, 2.6)
        send("face track " + who)
        send("poiseset %s 0" % who)
        time.sleep(0.7)   # the break's flash and shards over: broken (no shell / the guard ring empty and flashing)
        send("heroat %s 0 2.6" % who)
        aim(who)
        log("kill %s: before %s" % (style, send("bars?")[:200]))
        log("kill: " + send("enemyhp %s kill" % who))
        time.sleep(0.35)
        send("timescale 0")
        shot("%s_%s_5kill_fade" % (px, style.lower()), "%s - killed while broken, falling (%s)" % (style, place),
             "Killed with its guard broken: nothing grows back as it falls and fades (the shell / a full blue ring came back before).")
        log("kill %s: fade %s" % (style, send("bars?")[:300]))
        send("timescale 1")
        send("face track off")
        time.sleep(1.5)
    send("vrstatus shell")


def part_recover(px, place):
    """v4 (the HUD audit of 2026-10-19, item 4): the shell during the recovery hold after a break - it re-forms from the feet
    up to the thin nearly-gone step (before: no shell at all, as if still broken, then a whole bright one flashing on). The
    hero is placed while the enemy stands stunned (broken), the view kept on it (`face track`); frozen at the hold's middle."""
    send("vrstatus shell")
    who, p = enemy()
    if "max" not in p:
        log("recover: no guard: " + p["raw"][:100])
        return
    send("enemyhp %s 1" % who)
    stand(who, 2.6)
    send("poiseset %s 0" % who)
    time.sleep(0.8)   # broken: stunned where it stands
    send("heroat %s 0 2.6" % who)
    send("face track " + who)
    YAW[0] = 0.0
    sim("head 0 1.7 0 0 -6 0; " + HANDS)
    t0 = time.time()
    while time.time() - t0 < 25 and poise(who).get("phase") != "Recover":
        time.sleep(0.1)
    if poise(who).get("phase") != "Recover":
        log("recover: no Recover phase: " + poise(who)["raw"][:120])
        send("face track off")
        return
    t0 = time.time()
    while time.time() - t0 < 6:
        e = [x for x in send("bars?").split(" | ") if who in x]
        m = re.search(r" shell reforming ([\d.]+)", e[0]) if e else None
        if m and float(m.group(1)) >= 0.45:
            send("timescale 0")
            shot("%s_shell_6recover_half" % px, "Shell - recovering after a break (%s)" % place,
                 "It re-forms from the feet up while it recovers, thin: one hit breaks it again when it fights on.")
            log("recover: %s | %s" % (e[0][:200], send("enemy? " + who)[:70]))
            send("timescale 1")
            break
        time.sleep(0.05)
    send("face track off")


def part_bodycombo(px, place):
    """v4 (the HUD audit of 2026-10-19, item 3): On you (Body) in a fight with a combo goal (the `fighthud combo 8 3` seam: the
    fixtures' fights have none) - the gold band beside the timer on the shield wrist, "Combo 3 / 8" on the cuff."""
    send("vrhud body")
    send("fighthud combo 8 3")
    send("fighthud timer 75 Reach a combo")
    who, p = enemy()
    stand(who, 3.2)
    send("face " + who)   # the enemy straight ahead of the rig: the head's yaw 0 looks at it, the hands are placed in front
    # the shield wrist turned like a watch in front of the eyes (the head looking down at it; v4's first run: with the head
    # turned toward the enemy, the wrist sat at the view's edge), then held lower with the enemies ahead
    for i, (pose, pitch) in enumerate((("hand l -0.05 1.25 -0.32 90 0 -90", -45), ("hand l -0.12 1.22 -0.36 90 0 -90", -30))):
        sim("head 0 1.7 0 0 %d 0; %s; hand r 0.3 0.95 0.0 0 -70 0" % (pitch, pose))
        time.sleep(0.8)
        send("timescale 0")
        shot(px + "_body_combo%d" % i, "On you - a combo goal (%s)" % place,
             "A fight that needs a combo of 8: the gold band on the shield wrist fills toward it; look at the wrist: \"Combo 3 / 8\".")
        log("bodycombo: " + send("fighthud?")[-120:])
        send("timescale 1")
    send("fighthud combo 0")
    send("fighthud timer 0")
    send("vrhud board")


ORDER = ["board", "shell", "rings", "body"]
PARTS = {"near": part_near, "force": part_force, "board": part_board, "body": part_body, "shell": lambda px, pl: states(px, pl, "Shell"), "rings": lambda px, pl: states(px, pl, "Rings"),
         "kill": part_kill, "recover": part_recover, "bodycombo": part_bodycombo}


def sheet():
    from PIL import Image, ImageDraw, ImageFont
    d = json.load(open(LABELS)) if os.path.exists(LABELS) else {}
    names = [n for n in sorted(d) if os.path.exists(OUT + "/" + n + ".jpg")]
    tw, cols = 900, 2
    try:
        ft = ImageFont.truetype("/usr/share/fonts/google-noto/NotoSans-Bold.ttf", 26)
        fs = ImageFont.truetype("/usr/share/fonts/google-noto/NotoSans-Regular.ttf", 20)
    except OSError:
        ft = fs = ImageFont.load_default()
    tiles = []
    for n in names:
        im = Image.open(OUT + "/" + n + ".jpg").convert("RGB")
        th = int(im.size[1] * tw / im.size[0])
        im = im.resize((tw, th))
        t = Image.new("RGB", (tw, th + 74), (24, 18, 12))
        t.paste(im, (0, 74))
        dr = ImageDraw.Draw(t)
        dr.text((12, 6), d[n][0], fill=(255, 226, 160), font=ft)
        dr.text((12, 42), d[n][1][:110], fill=(235, 230, 220), font=fs)
        tiles.append(t)
    if not tiles:
        return None
    rows = (len(tiles) + cols - 1) // cols
    th = tiles[0].size[1]
    sh = Image.new("RGB", (cols * tw + (cols + 1) * 10, 70 + rows * (th + 10) + 10), (14, 10, 6))
    dr = ImageDraw.Draw(sh)
    dr.text((14, 16), os.environ.get("SHEET_TITLE", "Hand of Fate 2 VR - the fight HUD (Board / On you) and the enemy guard (Shell / Rings), both eyes, built 2026-10-18"), fill=(255, 226, 160), font=ft)
    for i, t in enumerate(tiles):
        sh.paste(t, (10 + (i % cols) * (tw + 10), 70 + (i // cols) * (th + 10)))
    p = OUT + "/sheet.jpg"
    sh.save(p, quality=85)
    print(p, sh.size, len(tiles), "pictures")
    return p


def main():
    a = sys.argv[1:]
    if a and a[0] == "sheet":
        sheet()
        return 0
    px = a[0] if a else "fight"
    place = a[a.index("--place") + 1] if "--place" in a else px
    if "--jump" in a:
        boot = ["--boot"] if "GameState_Map" not in send("state?") else []
        r = subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--jump", a[a.index("--jump") + 1], "--with", a[a.index("--with") + 1]] + boot +
                           ["--god", "--physical", "--equip", "BasicSword_noToken,BasicShield"], capture_output=True, text=True)
        log("fixture: " + ((r.stdout or "").strip().splitlines() or ["?"])[-1])
    if "Level_Play" not in send("state?"):
        log("no fight: " + send("state?"))
        return 1
    if "--env" in a:
        want = a[a.index("--env") + 1]
        log("envstate before: " + send("envstate")[:400])
        log("envstate: " + send("envstate " + want)[:200])
        t0 = time.time()
        while time.time() - t0 < 30 and "loading True" in send("envstate"):
            time.sleep(0.5)
        time.sleep(2.0)
        log("envstate after: " + send("envstate")[:400])
    send("god on")
    send("vrlook Gilded")
    parts = a[a.index("--parts") + 1].split(",") if "--parts" in a else ORDER
    for p in parts:
        if user_in_vr():
            log("the user is in VR: stopping")
            break
        try:
            PARTS[p](px, place)
        except Exception as ex:
            log("part %s: error %r" % (p, ex))
    send("timescale 1")
    send("fighthud timer 0")
    send("vrhud setting")
    send("vrstatus setting")
    return 0


if __name__ == "__main__":
    sys.exit(main())

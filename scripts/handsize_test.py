#!/usr/bin/env python3
"""The glove cut to HL:A's proportions (2026-10-20, env 3; docs/hand-size-hla.md): the drawn size and pictures, before and after.

  handsize_test.py --tag before|after [--parts table,fit,fight] [--pics DIR]
Parts (from a test session at the map: session.py start --snapshot magician_mid --to table):
  table  `handsize` (the glove to the eyes and next to the map's cards, the figurine, the table, the Dealer's hands); the
         player's own fit (glovefit: GloveAngle 25, turn -9.5, roll -23, up -1.35, along 0.73, side 1.75 - this run only, never
         saved); `hands probe` (the index tip in the controller frame: the cut must not move it); pictures: both hands
         reaching over the map (backs, then palms), each hand's back raised to the eyes (the left: the gems and the numbers),
         the left hand from its thumb side and from its little-finger side (the cuff's length), the right hand over the
         figurine; `hands back` (the numbers' printed size), `fonts` (the mod's text check) while the numbers are up
  fit    the hand check's glove page (`handcheck start`) with the player's fit: the fingertips 1.2 cm apart -> the guide's
         gap; its picture; Close
  fight  thieves (Physical; a sword with a special, an artefact with an ability, the shield): `handsize`; the guard; the
         shield glove's back (gems, super meter, the artefact badge - lit and ready), its thumb side, its inner wrist; the
         weapon glove's back (its badge); Body: the wrist timer, then a combo goal (the gold band, "Combo 3 / 8"); left
  tunetable, tunefight  `hands back` candidates for the numbers (HS_STATS "y:font,...") and the artefact badge (HS_BADGE
         "y:size,...") with pictures (tune_*)
Pictures (both eyes): DIR (default captures/hands-2026-10-20)/<tag>_<name>.jpg. PASS/FAIL lines on stdout and in
logs/handsize_test.log, the last line "N/M passed"; the measured numbers as "SIZE <part> ..." lines.
"""
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hof2env  # noqa: E402  (this worktree's test environment)
import playtest as PT  # noqa: E402

H = hof2env.H
LOG = H + "/logs/handsize_test.log"
PT.LOG = LOG
PICS = H + "/captures/hands-2026-10-20"
SHOTS = hof2env.TESTDATA + "/shots"
TAG = "x"
send, sim, log = PT.send, PT.sim, PT.log
RES = []
USER_FIT = "glovefit tilt 25 turn -9.5 roll -23 up -1.35 along 0.73 side 1.75"
REST = "hand r 0.25 1.0 -0.2 0 -60 0; hand l -0.25 1.0 -0.2 0 -60 0; trig r 0; trig l 0; touch r 0; touch l 0"


def result(name, ok, ev):
    RES.append((name, ok))
    log(("PASS " if ok else "FAIL ") + name + " - " + ev[:700])


def pic(name):
    """Both eyes now -> PICS/<tag>_<name>.jpg."""
    os.makedirs(PICS, exist_ok=True)
    shot = "HS_" + TAG + "_" + name
    src = SHOTS + "/" + shot + "_eyes.png"
    before = os.path.getmtime(src) if os.path.exists(src) else 0
    send("shot " + shot)
    for _ in range(40):
        time.sleep(0.25)
        if os.path.exists(src) and os.path.getmtime(src) > before and os.path.getsize(src) > 0:
            time.sleep(0.5)
            break
    try:
        from PIL import Image
        Image.open(src).convert("RGB").save(PICS + "/" + TAG + "_" + name + ".jpg", quality=88)
        log("picture " + PICS + "/" + TAG + "_" + name + ".jpg")
    except Exception as e:
        log("picture %s: %s" % (name, e))


def still(name, cmds, settle=0.9, freeze=False):
    sim(cmds)
    time.sleep(settle)
    if freeze:
        send("timescale 0")
    pic(name)
    if freeze:
        send("timescale 1")


def size(part):
    s = send("handsize", 10)
    log("SIZE %s %s" % (part, s))
    return s


def num(pat, s, default=None):
    m = re.search(pat, s)
    return float(m.group(1)) if m else default


def part_table():
    log("fit: %s" % send(USER_FIT))
    if TAG != "before":
        set_size("1.06")   # (the before build's ReachBoost 1.06: the glove's before / after at the same table scale)
    sim("head 0 1.66 0 0 -40 0; " + REST)
    time.sleep(1.0)
    s = size("table")
    k = num(r"-> k ([\d.]+)", s)
    eyes = num(r"glove L: lossy [\d.]+, to the eyes x([\d.]+)", s)
    whole = num(r"glove L: .*?whole ([\d.]+) cm", s)
    result("table_glove_life_size", eyes is not None and abs(eyes - 1.0) < 0.01, "k %s, the glove to the eyes x%s, whole %s cm | %s" % (k, eyes, whole, s[:400]))
    log("PROBE table %s" % send("hands probe"))
    # both hands reaching over the map (as candidate 9's w02_map_hands_reaching), backs up, then palms up
    still("table_reach", "head 0 1.66 0 0 -45 0; hand r 0.18 1.05 -0.38 0 -35 0; hand l -0.18 1.05 -0.38 0 -35 0")
    still("table_reach_palms", "head 0 1.66 0 0 -45 0; hand r 0.16 1.08 -0.36 0 -20 180; hand l -0.16 1.08 -0.36 0 -20 180")
    # each hand's back raised to the eyes like a watch (candidate 9's glove pose, 0.33 m under the eyes)
    still("table_back_left", "head 0 1.66 0 0 -20 0; hand l -0.02 1.33 -0.28 -70 -10 -90; hand r 0.25 0.95 -0.2 0 -60 0", 1.2)
    hb = send("hands back")
    fo = send("fonts")
    log("BACK table %s" % hb)
    log("FONTS table %s" % fo)
    shown = " shown" in hb
    m = re.search(r"in view (\d+): the smallest line ([\d.]+) deg .*?the most turned (\d+) deg", fo)
    ok = m is not None and (int(m.group(1)) == 0 or (float(m.group(2)) >= 0.45 and int(m.group(3)) <= 70))
    result("table_numbers_read", shown and ok, "numbers %s | %s" % (hb[:260], fo[:260]))
    still("table_back_left_far", "head 0 1.66 0 0 -30 0; hand l -0.08 1.2 -0.38 -70 -10 -90; hand r 0.25 0.95 -0.2 0 -60 0", 1.0)
    still("table_back_right", "head 0 1.66 0 0 -20 0; hand r 0.02 1.33 -0.28 70 -10 90; hand l -0.25 0.95 -0.2 0 -60 0", 1.0)
    # the left hand from its thumb side, then from its little-finger side: how far the cuff runs up the arm
    still("table_side_left_thumb", "head 0 1.66 0 0 -25 0; hand l -0.04 1.36 -0.33 -90 0 0; hand r 0.25 0.95 -0.2 0 -60 0")
    still("table_side_left_pinky", "head 0 1.66 0 0 -25 0; hand l -0.04 1.36 -0.33 90 0 180; hand r 0.25 0.95 -0.2 0 -60 0")
    # the right hand over the figurine (the figurine and the cards beside it)
    sim("head 0 1.66 0 0 -50 0; hand r 0.15 1.05 -0.38 0 -45 0; hand l -0.25 0.95 -0.2 0 -60 0")
    time.sleep(0.6)
    mv = send("handto r palm figurine")
    if mv.startswith("move"):
        sim(mv + " 400")
        time.sleep(0.8)
        sim("move r 0 0.06 0 200")
        time.sleep(0.6)
    log("figurine: %s" % mv)
    pic("table_figurine")
    sim("head 0 1.66 0 0 0 0; " + REST)
    time.sleep(0.5)


def hc():
    send("state")
    return PT.st().get("handcheck", "")


def part_fit():
    log("fit: %s" % send(USER_FIT))
    sim("head 0 1.75 0 0 -20 0; hand r 0.22 1.25 -0.25 0 -30 0; hand l -0.22 1.25 -0.25 0 -30 0; trig r 0; touch r 0; trig l 0; touch l 0")
    send("handcheck close")
    time.sleep(0.5)
    r = send("handcheck start")
    t0 = time.time()
    while time.time() - t0 < 8 and not hc().startswith("Fit"):
        time.sleep(0.4)
    page = hc()
    log("handcheck: %s | %s" % (r, page[:200]))
    if not page.startswith("Fit"):
        result("fit_page", False, page[:200])
        return
    p = send("handcheck pose 1 15").split("|")[0].split()
    x, y, z = (float(v) for v in p)
    ml = send("handto l tip %.4f %.4f %.4f" % (x - 0.006, y, z))
    mr = send("handto r tip %.4f %.4f %.4f" % (x + 0.006, y, z))
    sim(ml + " 300; " + mr + " 300")
    time.sleep(2.5)
    page = hc()
    m = re.search(r"gap ([\d.]+) cm", page)
    gap = float(m.group(1)) if m else -1
    log("PROBE fit %s" % send("hands probe"))
    result("fit_tips_guide", m is not None and 0.6 <= gap <= 2.0 and "tilt 25" in page,
           "the guide's gap %s cm (the simulated tips 1.2 cm apart); %s" % (m.group(1) if m else "-", page[:260]))
    pic("fit_tips")
    sim("hand r 0.22 1.25 -0.25 0 -30 0; hand l -0.22 1.25 -0.25 0 -30 0")
    time.sleep(0.8)
    pic("fit_page")
    send("handcheck close")
    time.sleep(1.0)


def part_fight():
    import p4_explore as X
    X.PT.LOG = LOG
    X.OUT = PICS
    X.TAG = TAG
    log("fit: %s" % send(USER_FIT))
    if not X.into_fight():
        result("fight_reached", False, PT.state()[:80])
        return
    time.sleep(3)
    send("vrhud setting")
    s = size("fight")
    eyes = num(r"glove L: lossy [\d.]+, to the eyes x([\d.]+)", s)
    result("fight_glove_life_size", eyes is not None and abs(eyes - 1.0) < 0.01, s[:500])
    GUARD = "head 0 1.75 0 0 0 0; hand r 0.25 1.2 -0.25 0 -20 0; hand l -0.25 1.2 -0.25 0 -20 0"
    still("fight_guard", GUARD, 1.2, True)
    still("fight_shield_up", "head 0 1.75 0 0 -20 0; hand r 0.25 1.2 -0.35 0 5 0; hand l -0.3 1.15 -0.35 0 0 0", 1.2, True)
    send("super? set 100")
    time.sleep(1.0)
    hi = send("handicons")
    log("HANDICONS %s" % hi)
    result("fight_badges", "weapon shown" in hi and "artefact shown" in hi, hi[:300])
    # the shield glove's back turned to the eyes (candidate 9/10's glove pose and p4's badge pose), its thumb side, its inner wrist
    still("fight_shield_back", "head 0 1.75 0 0 0 0; hand l -0.02 1.42 -0.28 -70 -10 -90; hand r 0.25 1.0 -0.25 0 -60 0", 1.0, True)
    still("fight_shield_back_p4", "head 0 1.7 0 0 -25 0; hand l -0.1 1.33 -0.3 180 -10 -90; hand r 0.25 1.0 -0.25 0 -60 0", 1.0, True)
    still("fight_shield_thumb", "head 0 1.75 0 0 -20 0; hand l -0.06 1.4 -0.32 -90 0 0; hand r 0.25 1.0 -0.25 0 -60 0", 1.0, True)
    still("fight_shield_inner", "head 0 1.75 0 0 -20 0; hand l -0.04 1.4 -0.3 -70 -10 90; hand r 0.25 1.0 -0.25 0 -60 0", 1.0, True)
    still("fight_weapon_back", "head 0 1.7 0 0 -25 0; hand r 0.12 1.35 -0.32 180 -10 90; hand l -0.25 1.0 -0.25 0 -60 0", 1.0, True)
    log("PROBE fight %s" % send("hands probe"))
    # Body: the wrist timer, then a combo goal (the timer set first: the thieves fixture has no timer of its own, so the wrist
    # timer is off without it - both builds' first run, 10-20)
    send("vrhud body")
    send("fighthud timer 75 Reach a combo")
    time.sleep(0.5)
    for i, pose in enumerate(("hand l -0.1 1.35 -0.34 -60 20 -90", "hand l -0.22 1.2 -0.3 -70 30 -90")):
        still("fight_body_wrist%d" % i, "head 0 1.7 0 0 %d 0; %s; hand r 0.22 1.25 -0.4 0 10 0" % (-30 if i == 0 else -45, pose), 0.9, True)
    fh = send("fighthud?")
    result("fight_body_wrist_timer", " wrist on " in fh or "wrist on" in fh, fh[-200:])
    send("fighthud combo 8 3")
    send("fighthud timer 75 Reach a combo")
    time.sleep(0.5)
    still("fight_body_combo", "head 0 1.7 0 0 -45 0; hand l -0.05 1.25 -0.32 90 0 -90; hand r 0.3 0.95 0.0 0 -70 0", 0.9, True)
    fh = send("fighthud?")
    log("BODYCOMBO %s" % fh[-200:])
    result("fight_body_combo", "combo 3 label 'Combo 3 / 8'" in fh, fh[-200:])
    send("fighthud combo 0")
    send("fighthud timer 0")
    send("vrhud setting")
    send("super? set 0")
    sim(GUARD)
    time.sleep(0.5)


def set_size(v, timeout=20):
    """`playersize v` (this run only) and wait until the place has been taken again at that scale (the fade back in). The head
    at 1.66 m first: the place takes the eye height from the head when it is taken again (the first run, 10-20: 1.75 m at 1.06
    from the session's start pose, 1.66 m at 1.0 - the two sizes' reach not comparable)."""
    sim("head 0 1.66 0 0 0 0; " + REST)
    time.sleep(0.6)
    r = send("playersize " + v)
    want = 1.2 * float(v)
    t0 = time.time()
    while time.time() - t0 < timeout:
        time.sleep(0.8)
        p = send("place")
        m = re.search(r"scale ([\d.]+)", p)
        if m and abs(float(m.group(1)) - want) < 0.006 and "taking the place again" not in send("playersize"):
            break
    time.sleep(2.0)
    send("tablecomfort pull 1")   # the map drawn toward the player again at this scale
    time.sleep(1.5)
    p = send("place")
    log("player size %s: %s | %s" % (v, r, p))
    return p


def reach(tag):
    """`reach tag` -> the report's lines (testdata/reach/<tag>.txt)."""
    f = hof2env.TESTDATA + "/reach/" + tag + ".txt"
    before = os.path.getmtime(f) if os.path.exists(f) else 0
    send("reach " + tag)
    for _ in range(20):
        time.sleep(0.3)
        if os.path.exists(f) and os.path.getmtime(f) > before:
            break
    try:
        return open(f).read()
    except OSError:
        return ""


ITEM = re.compile(r"^\s+(.+?)\s+\(([-\d.]+),([-\d.]+),([-\d.]+)\) d=([\d.]+) m \(ahead ([-\d.]+), below ([-\d.]+), right ([-\d.]+)\) (OK|LEAN|FAR)(.*)$")


def reach_summary(text):
    """The family -> [(label, d, ahead, below, rating, extra)] of a reach report; the player line."""
    fam, out, player = None, {}, ""
    for line in text.splitlines():
        if line.startswith("player:"):
            player = line
        elif re.match(r"^[a-z0-9 ()D-]+:", line) and not line.startswith(" "):
            fam = line.split(":")[0]
        else:
            m = ITEM.match(line)
            if m and fam:
                out.setdefault(fam, []).append((m.group(1).strip(), float(m.group(5)), float(m.group(6)), float(m.group(7)), m.group(9), m.group(10)))
    return player, out


def reach_report(where, v, text):
    player, fams = reach_summary(text)
    log("REACH %s %s %s" % (where, v, player))
    for fam, items in fams.items():
        if fam in ("hands", "table", "dice", "pendulum", "wheel", "2D panel", "tokens (coins)", "deck builder"):
            continue
        cnt = {k: sum(1 for i in items if i[4] == k) for k in ("OK", "LEAN", "FAR")}
        touch = [i for i in items if "[touch]" in i[5] or "movable" in i[5] or fam != "map"]
        far = max(touch, key=lambda i: i[1]) if touch else None
        log("REACH %s %s | %s: %d items OK %d LEAN %d FAR %d%s" % (where, v, fam, len(items), cnt["OK"], cnt["LEAN"], cnt["FAR"],
            (" | farthest touch target %s d %.2f ahead %.2f below %.2f %s" % (far[0], far[1], far[2], far[3], far[4])) if far else ""))
        for i in items:
            log("REACH %s %s |   %s d %.2f ahead %.2f below %.2f %s%s" % (where, v, i[0], i[1], i[2], i[3], i[4], i[5][:60]))
    rows = sorted(set(round(i[2], 2) for i in fams.get("map", []) if i[0].startswith("slot (")))
    mid = rows[len(rows) // 2] if rows else 9
    return fams, rows, mid


def part_size():
    """The player's size at the table (the user 2026-10-20: "Try it at 1.0"): at 1.06 (the old ReachBoost) and at 1.0 the
    drawn size, the reach to the map's rows and the figurine and the touch plates, and the hands over the cards."""
    log("fit: %s" % send(USER_FIT))
    mids = {}
    for v in ("1.06", "1.0"):
        set_size(v)
        sim("head 0 1.66 0 0 -40 0; " + REST)
        time.sleep(1.0)
        size("map_%s" % v)
        fams, rows, mid = reach_report("map", v, reach("HS_map_%s" % v.replace(".", "")))
        mids[v] = mid
        result("size_%s_map_middle_row" % v, mid <= 0.5, "map rows ahead %s m; middle %.2f m (table_reach_middle: <= 0.5)" % (rows, mid))
        tag = v.replace(".", "")
        still("size%s_reach" % tag, "head 0 1.66 0 0 -45 0; hand r 0.18 1.05 -0.38 0 -35 0; hand l -0.18 1.05 -0.38 0 -35 0")
        sim("head 0 1.66 0 0 -50 0; hand r 0.15 1.05 -0.38 0 -45 0; hand l -0.25 0.95 -0.2 0 -60 0")
        time.sleep(0.6)
        mv = send("handto r palm figurine")
        if mv.startswith("move"):
            sim(mv + " 400")
            time.sleep(0.8)
            sim("move r 0 0.06 0 200")
            time.sleep(0.6)
        pic("size%s_figurine" % tag)
    log("SIZE middle rows: %s" % mids)


def part_encounter():
    """The fight card (an encounter's card presented at the table: `jump fight Gnomes`) at 1.06 and 1.0."""
    sim("head 0 1.75 0 0 0 0; " + REST)
    r = send("jump fight " + os.environ.get("HS_ENC", "Gnomes"))
    log("encounter jump: %s" % r)
    t0 = time.time()
    while time.time() - t0 < 30 and not re.search(r"nav:(Continue|Inventory)", send("boards")):
        time.sleep(1)
    time.sleep(3)
    for v in ("1.06", "1.0"):
        set_size(v)
        fams, rows, mid = reach_report("encounter", v, reach("HS_enc_%s" % v.replace(".", "")))
        pc = fams.get("presented cards", [])
        result("size_%s_fight_card" % v, bool(pc) and all(i[4] != "FAR" for i in pc), "; ".join("%s d %.2f %s" % (i[0], i[1], i[4]) for i in pc)[:300] or "no presented card")
        still("size%s_encounter" % v.replace(".", ""), "head 0 1.66 0 0 -30 0; hand r 0.2 1.1 -0.4 0 -30 0; hand l -0.25 0.95 -0.2 0 -60 0")


def part_hub():
    """The side table (a session at the caravan: --snapshot magician) at 1.06 and 1.0: the caravan's Start first (the session
    comes up at the caravan's title, Attract, at the card table's spot - the first run measured the side table from there,
    behind the player), then the challenge map at the side table's spot (Places.HubSpot)."""
    for _ in range(3):
        # (as the chains' to_challenge_map: one press at the title's first seconds was lost - 10-20, 36 s still at Attract)
        if "Attract" not in PT.state() or "ChallengeMap" in PT.st().get("menu", ""):
            break
        time.sleep(2.0)
        send("press A 6", 5)
        t0 = time.time()
        while time.time() - t0 < 25 and "ChallengeMap" not in PT.st().get("menu", ""):
            time.sleep(1.0)
    time.sleep(5.0)
    log("hub: %s / %s, place %s" % (PT.state()[:40], PT.st().get("menu", ""), send("place")[:120]))
    for v in ("1.06", "1.0"):
        set_size(v)
        sim("head 0 1.66 0 0 -40 0; " + REST)
        time.sleep(1.0)
        fams, rows, mid = reach_report("hub", v, reach("HS_hub_%s" % v.replace(".", "")))
        st_items = fams.get("side table", []) + fams.get("touch plates (every board shown now)", [])
        result("size_%s_side_table" % v, bool(st_items) and all(i[4] != "FAR" for i in st_items),
               "; ".join("%s d %.2f %s" % (i[0], i[1], i[4]) for i in st_items)[:400] or "no side table items")
        still("size%s_hub" % v.replace(".", ""), "head 0 1.66 0 0 -40 0; hand r 0.18 1.05 -0.38 0 -35 0; hand l -0.18 1.05 -0.38 0 -35 0")


# `hands back` candidates (the numbers at the table: "y:font"; the artefact badge in a fight: "y:size", cm from the wrist joint)
TUNE_STATS = os.environ.get("HS_STATS", "0.9:22,0.4:22,0.3:20,0.6:20").split(",")
TUNE_BADGE = os.environ.get("HS_BADGE", "-0.15:2.8,2.5:2.0,2.4:2.2").split(",")
WATCH_L = "head 0 1.66 0 0 -20 0; hand l -0.02 1.33 -0.28 -70 -10 -90; hand r 0.25 0.95 -0.2 0 -60 0"
WATCH_L_FAR = "head 0 1.66 0 0 -30 0; hand l -0.08 1.2 -0.38 -70 -10 -90; hand r 0.25 0.95 -0.2 0 -60 0"


def part_tunetable():
    """Where the numbers lie on the glove (seam `hands back stats <y> font <n>`, this run only): each candidate's printed size,
    the fonts check and both-eye pictures of the left hand raised like a watch, near and at arm's length."""
    log("fit: %s" % send(USER_FIT))
    for c in TUNE_STATS:
        y, f = c.split(":")
        r = send("hands back stats %s font %s" % (y, f))
        tag = "y%s_f%s" % (y.replace(".", "").replace("-", "m"), f)
        still("tune_stats_%s" % tag, WATCH_L, 1.2)
        fo = send("fonts")
        log("TUNE stats %s font %s | %s | FONTS %s" % (y, f, send("hands back"), fo[:260]))
        still("tune_stats_%s_far" % tag, WATCH_L_FAR, 1.0)
    send("hands back stats 0.3 font 22")
    sim("head 0 1.66 0 0 0 0; " + REST)


def part_tunefight():
    """Where the artefact badge lies on the shield glove (seam `hands back badge <y> badgesize <cm>`): in the Body style with the
    wrist timer and a combo goal up (the bands on the cuff) and in the Setting style; both-eye pictures from the wrist and
    combo poses."""
    import p4_explore as X
    X.PT.LOG = LOG
    X.OUT = PICS
    X.TAG = TAG
    log("fit: %s" % send(USER_FIT))
    if not X.into_fight():
        result("tunefight_reached", False, PT.state()[:80])
        return
    time.sleep(3)
    send("super? set 100")
    WR = "head 0 1.7 0 0 -30 0; hand l -0.1 1.35 -0.34 -60 20 -90; hand r 0.22 1.25 -0.4 0 10 0"
    CO = "head 0 1.7 0 0 -45 0; hand l -0.05 1.25 -0.32 90 0 -90; hand r 0.3 0.95 0.0 0 -70 0"
    for c in TUNE_BADGE:
        y, sz = c.split(":")
        send("hands back badge %s badgesize %s" % (y, sz))
        tag = "b%s_s%s" % (y.replace(".", "").replace("-", "m"), sz.replace(".", ""))
        send("vrhud body")
        send("fighthud timer 75 Reach a combo")
        send("fighthud combo 8 3")
        time.sleep(0.4)
        still("tune_badge_%s_body_wrist" % tag, WR, 0.9, True)
        still("tune_badge_%s_body_combo" % tag, CO, 0.9, True)
        log("TUNE badge %s size %s | %s | HANDICONS %s | FIGHTHUD %s" % (y, sz, send("hands back"), send("handicons")[:200], send("fighthud?")[-160:]))
        send("fighthud combo 0")
        send("fighthud timer 0")
        send("vrhud setting")
        time.sleep(0.4)
        still("tune_badge_%s_setting_wrist" % tag, WR, 0.9, True)
    send("hands back badge -0.15 badgesize 2.8")
    send("super? set 0")
    sim("head 0 1.75 0 0 0 0; hand r 0.25 1.2 -0.25 0 -20 0; hand l -0.25 1.2 -0.25 0 -20 0")
    time.sleep(0.5)


PARTS = {"tunetable": part_tunetable, "tunefight": part_tunefight, "table": part_table, "fit": part_fit, "fight": part_fight, "size": part_size, "encounter": part_encounter, "hub": part_hub}


def main():
    global TAG, PICS
    a = sys.argv[1:]
    if "--tag" in a:
        TAG = a[a.index("--tag") + 1]
    if "--pics" in a:
        PICS = a[a.index("--pics") + 1]
    parts = a[a.index("--parts") + 1].split(",") if "--parts" in a else ["table", "fit", "fight"]
    log("=== handsize_test %s %s (state %s)" % (TAG, ",".join(parts), PT.state()[:50]))
    for p in parts:
        try:
            PARTS[p]()
        except Exception as e:
            result(p + "_ran", False, "%r" % e)
    send("timescale 1")
    n = sum(1 for _, ok in RES if ok)
    log("%d/%d passed" % (n, len(RES)))


if __name__ == "__main__":
    main()

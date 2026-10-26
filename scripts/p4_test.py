#!/usr/bin/env python3
"""Playtest 4's fixes (env 1, 2026-10-19), from a test session at the table (session.py start --snapshot magician_mid --to table).

  p4_test.py [--parts calib,...] [--pics DIR]
Parts:
  calib   P4-3: pause, VR Settings, Hands, "Calibrate hands" (real pokes by the simulated right hand) -> the hand check's glove
          page in front, the pause board and the VR settings page hidden; Tilt + poked once -> the fit follows; Back -> the hand
          check closes, the VR settings page is back on Hands in the same place, the fit kept (a test run never writes the
          settings file: it holds for the run - the log's "(test run: this run only)"); Back, Resume. The player's settings file
          unchanged.
Pictures (both eyes): DIR (default captures/p4)/<name>.jpg, from testdata/shots/<name>_eyes.png.
Results: PASS/FAIL lines on stdout and in logs/p4_test.log; the last line "N/M passed".
"""
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hof2env  # noqa: E402  (this worktree's test environment: env 3's copy drives env 3's game)
import playtest as PT  # noqa: E402

H = hof2env.H

PT.LOG = H + "/logs/p4_test.log"
PICS = H + "/captures/p4"
SHOTS = H + "/testdata/shots"
send, sim, st, log = PT.send, PT.sim, PT.st, PT.log
RES = []


def result(name, ok, ev):
    RES.append((name, ok))
    log(("PASS " if ok else "FAIL ") + name + " - " + ev[:600])


def pic(name):
    """Both eyes now -> PICS/name.jpg."""
    os.makedirs(PICS, exist_ok=True)
    src = SHOTS + "/" + name + "_eyes.png"
    before = os.path.getmtime(src) if os.path.exists(src) else 0
    send("shot " + name)
    for _ in range(40):
        time.sleep(0.25)
        if os.path.exists(src) and os.path.getmtime(src) > before and os.path.getsize(src) > 0:
            time.sleep(0.5)
            break
    try:
        from PIL import Image
        Image.open(src).convert("RGB").save(PICS + "/" + name + ".jpg", quality=88)
        log("picture " + PICS + "/" + name + ".jpg")
    except Exception as e:
        log("picture %s: %s" % (name, e))


def hc():
    send("state")
    return st().get("handcheck", "")


def hc_page():
    return hc().split(" |")[0].strip()


def wait(pred, probe, timeout):
    t0 = time.time()
    while time.time() - t0 < timeout:
        v = probe()
        if pred(v):
            return v
        time.sleep(0.4)
    return None


def fit_now():
    m = re.search(r"fit tilt ([-\d.]+)\D+turn ([-\d.]+)\D+roll ([-\d.]+)\D+up ([-\d.]+) cm, forward ([-\d.]+) cm, side ([-\d.]+) cm", hc())
    return [float(v) for v in m.groups()] if m else None


def gamelog_tail(n0):
    try:
        with open(PT.GAMELOG, errors="replace") as f:
            return f.read().splitlines()[n0:]
    except OSError:
        return []


def gamelog_len():
    try:
        with open(PT.GAMELOG, errors="replace") as f:
            return len(f.read().splitlines())
    except OSError:
        return 0


def geo_centre(key):
    g = send("vrset geo " + key)
    m = re.search(r"centre (-?[\d.]+) (-?[\d.]+) (-?[\d.]+)", g)
    return [float(v) for v in m.groups()] if m else None


def open_vrsettings(tab):
    """Pause (START), then the VR Settings button and the tab, by pokes; the page string or None."""
    b = PT.wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 2)
    if not b:
        send("press START 6")
        b = PT.wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 10)
    v = PT.wait_vrset(lambda v: v.startswith("entry"), 8) if b else None
    if v is None:
        return None
    PT.poke_vrset("VR_Settings")
    v = PT.wait_vrset(lambda v: v.startswith("page"), 8)
    if v and not v.startswith("page " + tab):
        PT.poke_vrset(tab)
        v = PT.wait_vrset(lambda v: v.startswith("page " + tab), 8)
    return v


def close_all():
    v = PT.vrset_now()
    if "hand check over the page" in v or hc_page() not in ("Off", ""):
        send("handcheck close")
        time.sleep(1.0)
    if PT.vrset_now().startswith("page"):
        send("vrset close")
        time.sleep(1.0)
    if "menu board pause" in PT.boards_now():
        send("press START 6")
        time.sleep(1.5)


def part_calib():
    h0 = PT.file_sha1(PT.GAMECFG)
    sim(PT.REST + "; head 0 1.75 0 0 0 0; trig r 0; trig l 0; touch r 0; touch l 0; squeeze r 0; squeeze l 0")
    time.sleep(0.5)
    send("handcheck close")
    fit0 = fit_now()
    log("state: %s | fit before %s" % (PT.state()[:60], fit0))
    v = open_vrsettings("Hands")
    plates = re.findall(r"\[(\d+) ([^\]]*)\]", v or "")
    btn = next((t for i, t in plates if t.startswith("Glove fit/Calibrate hands")), None)
    result("calib_button_on_hands_tab", v is not None and btn is not None and not btn.endswith("-"),
           "page %s; the button %s" % ((v or PT.vrset_now())[:40], btn))
    if v is None or btn is None:
        close_all()
        return
    sim("head 0 1.75 0 0 -20 0")
    time.sleep(0.6)
    pic("p4_3_hands_tab_button")
    sim("head 0 1.75 0 0 0 0")
    time.sleep(0.3)
    back0 = geo_centre("Back")
    n0 = gamelog_len()
    PT.poke_vrset("Calibrate_hands")
    p = wait(lambda p: p == "Fit", hc_page, 6)
    v = PT.vrset_now()
    b = PT.boards_now()
    result("calib_opens_fit_page", p == "Fit" and "hand check over the page" in v and "menu board" not in b,
           "hand check %s; vr settings: %s; boards: %s" % (p or hc_page(), v[v.find("hand check"):][:60], b[:80]))
    if p != "Fit":
        close_all()
        return
    paused = send("state?", 4)
    sim("head 0 1.75 0 0 -20 0")
    time.sleep(0.8)
    pic("p4_3_fit_page")
    sim("head 0 1.75 0 0 0 0")
    time.sleep(0.4)
    # Tilt + (button 2) by a poke: the fit follows at once
    f1a = fit_now()
    PT.poke_pose("handcheck pose 2 6")
    time.sleep(0.6)
    f1 = fit_now()
    ok = f1a is not None and f1 is not None and abs((f1[0] - f1a[0]) - 1.0) < 0.05 and all(abs(f1[i] - f1a[i]) < 0.01 for i in range(1, 6))
    result("calib_tilt_once", ok, "fit %s -> %s (the game paused meanwhile: %s)" % (f1a, f1, paused[:50]))
    # Back (button 13): the check closes, the VR settings page comes back where it was
    PT.poke_pose("handcheck pose 13 6")
    p = wait(lambda p: p == "Off", hc_page, 6)
    v = PT.wait_vrset(lambda v: v.startswith("page Hands") and "hand check -" in v, 6)
    back1 = geo_centre("Back") if v else None
    same = back0 is not None and back1 is not None and max(abs(a - b) for a, b in zip(back0, back1)) < 0.002
    result("calib_back_returns", p == "Off" and v is not None and same,
           "hand check %s; page %s; Back button at %s then %s" % (p or hc_page(), (v or PT.vrset_now())[:30], back0, back1))
    f2 = fit_now()
    lines = gamelog_tail(n0)
    saved = [l for l in lines if "handcheck: glove fit" in l]
    back = [l for l in lines if "back from the hand check" in l]
    result("calib_fit_kept", f2 is not None and f1 is not None and all(abs(f2[i] - f1[i]) < 0.01 for i in range(6)) and saved and back,
           "fit after Back %s; log: %s | %s" % (f2, saved[-1][-120:] if saved else "no 'glove fit' line", back[-1][-90:] if back else "no 'back from' line"))
    sim("head 0 1.75 0 0 -20 0")
    time.sleep(0.6)
    pic("p4_3_after_back")
    sim("head 0 1.75 0 0 0 0")
    # Back to the pause menu, then Resume
    PT.poke_vrset("Back")
    b2 = PT.wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 8)
    PT.poke_plate("Resume") if b2 else send("press START 6")
    b3 = PT.wait_boards(lambda b: "menu board" not in b, 8)
    h1 = PT.file_sha1(PT.GAMECFG)
    result("calib_resume_and_cfg", b2 is not None and b3 is not None and h0 == h1,
           "Back -> %s, Resume -> %s; the settings file %s -> %s" % ("the pause board" if b2 else "no", "closed" if b3 else "no", h0, h1))
    close_all()


# ---- P4-5, P4-1, P4-2, P4-4, P4-6 (playtest 4) ------------------------------------------------------------------------
def boards():
    return PT.boards_now()


def plate_list():
    return [(int(i), t) for i, t in re.findall(r"\[(\d+) ([^\]]*)\]", st().get("boards", ""))]


def plate_index(label, exact=False):
    for i, t in plate_list():
        t2 = t.rstrip(" *")
        if (t2.lower() == label.lower()) if exact else (label.lower() in t2.lower()):
            return i
    return None


def poke_i(i, settle=1.5, fx=None):
    """A poke at plate i without the tour's grip habit (no trigger touch reported); fx: across the plate (-0.5..0.5)."""
    return PT._poke_pose("platepose %d 6 r%s" % (i, (" %.2f" % fx) if fx is not None else ""), settle)


def poke(label, settle=1.5, fx=None):
    i = plate_index(label)
    if i is None:
        log("no plate '%s' in %s" % (label, boards()[:200]))
        return False
    return poke_i(i, settle, fx)


def hover_i(i, cm=2.0):
    r = send("platepose %d %.1f r" % (i, cm))
    if "|" not in r:
        return False
    m = send("handto r tip " + r.split("|")[0].strip())
    if not m.startswith("move"):
        return False
    sim(m + " 300")
    time.sleep(1.2)
    return True


def away():
    sim(PT.REST)
    time.sleep(0.6)


def plate_rects():
    """Plate number -> (label, (x0, y0, x1, y1)) from the `boards` seam."""
    b = send("boards")
    labels = dict(re.findall(r"\[(\d+) ([^\]]*)\]", b))
    out = {}
    for i, x0, y0, x1, y1 in re.findall(r"plate (\d+) px \((-?\d+),(-?\d+)\)-\((-?\d+),(-?\d+)\)", b):
        out[int(i)] = (labels.get(i, ""), (int(x0), int(y0), int(x1), int(y1)))
    return out, b


def ensure_pause():
    b = PT.wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 1)
    if not b:
        send("press START 6")
        b = PT.wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 10)
    return b


def close_pause():
    if "menu board" in boards():
        send("press START 6")
        PT.wait_boards(lambda b: "menu board" not in b, 6)


def part_grip():
    """P4-5, first in a fresh session (no trigger touch reported yet, as the user's restarted game): the grip squeezed with
    the index lifted before the pause opens, a poke at Resume with it still held."""
    send("state")
    seen = "seen trigger=0" in send("fingers")
    res = {}
    for mode in ("off", "on"):
        close_pause()
        send("fingers assume " + mode)
        sim(PT.REST + "; squeeze r 0; ctouch r trigger 0")
        time.sleep(0.5)
        sim("squeeze r 1")
        time.sleep(0.6)
        send("state")
        g = st().get("grips", "")
        send("press START 6")
        PT.wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 10)
        time.sleep(0.4)   # (right after the board opens)
        poke("Resume", 2.0)
        res[mode] = ("menu board" not in boards(), g)
        sim("squeeze r 0")
        time.sleep(0.5)
    result("grip_old_rule_refuses", seen and not res["off"][0], "trigger touch never seen: %s; the old rule (assume off): grip '%s', the poke %s (the user's first touch)" % (
        seen, res["off"][1][:60], "pressed" if res["off"][0] else "refused"))
    result("grip_held_pointing_presses", res["on"][0], "Quest Touch (trigger touch known without a touch seen): grip '%s', the poke at Resume %s" % (
        res["on"][1][:60], "pressed: the menu closed" if res["on"][0] else "REFUSED"))
    # a fist (the index on the trigger) that lifts the index to point: a page first, then a pointing hand
    close_pause()
    sim(PT.REST + "; ctouch r trigger 1; wait 200; squeeze r 1")
    time.sleep(0.5)
    send("state")
    g1 = st().get("grips", "")
    sim("ctouch r trigger 0")
    time.sleep(0.8)
    send("state")
    g2 = st().get("grips", "")
    send("press START 6")
    PT.wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 10)
    poke("Resume", 2.0)
    ok = "menu board" not in boards()
    result("grip_fist_then_point", "R page" in g1 and "R point" in g2 and ok, "fist '%s' -> lifted 0.8 s '%s' -> the poke %s" % (g1[:50], g2[:50], "pressed" if ok else "REFUSED"))
    sim("squeeze r 0; ctouch r trigger 0")
    send("fingers assume auto")
    close_pause()


def part_credits():
    """P4-1: hovering Credits shows the credits beside the list (the board grows); the hover gone, it shrinks back."""
    away()
    sim("head 0 1.75 0 0 0 0")
    ensure_pause()
    i = plate_index("Credits")
    hover_i(i)
    b1 = boards()
    sim("head 0 1.75 0 0 -20 0")
    time.sleep(0.5)
    pic("p4_1_pause_credits_hover")
    sim("head 0 1.75 0 0 0 0")
    hover_i(plate_index("Exit"))
    time.sleep(1.8)
    b2 = boards()
    m1 = re.search(r"menu board pause \((-?\d+),\d+\)-\((\d+),", b1)
    m2 = re.search(r"menu board pause \((-?\d+),\d+\)-\((\d+),", b2)
    w1 = int(m1.group(2)) - int(m1.group(1)) if m1 else -1
    w2 = int(m2.group(2)) - int(m2.group(1)) if m2 else -1
    h2 = send("holes")
    result("credits_far_screen_out", "(0,0)-(1920,1080)" in h2, h2[:120])
    result("credits_hover_grows_then_shrinks", w1 > 1500 and 0 < w2 < 1000 and "shrinks" in b2,
           "hovering Credits: the menu %d px wide (%s); the hover on Exit 1.8 s: %d px | %s" % (w1, "ScrollBar" in b1, w2, re.search(r"\(pause[^)]*\)", b2).group(0) if re.search(r"\(pause[^)]*\)", b2) else b2[:120]))
    sim("head 0 1.75 0 0 -20 0")
    time.sleep(0.5)
    pic("p4_1_pause_after_hover")
    away()


def row_rects(rects):
    """The settings list's rows (right of x 860, not the scroll bar, not the tabs)."""
    return {i: v for i, v in rects.items() if v[1][0] > 860 and "ScrollBar" not in v[0] and v[1][2] - v[1][0] > 200}


def check_settings_page(tag, where):
    rects, b = plate_rects()
    rows = row_rects(rects)
    arrows = [v[0] for v in rects.values() if "Arrow" in v[0]]
    # every row (but the sliders' bars) on one column; nothing overlaps
    nonbar = [v[1] for v in rows.values() if "=" not in v[0]]
    xs0 = set(r[0] for r in nonbar)
    xs1 = set(r[2] for r in nonbar)
    overl = []
    items = list(rects.items())
    for a in range(len(items)):
        for c in range(a + 1, len(items)):
            r1, r2 = items[a][1][1], items[c][1][1]
            if r1[0] < r2[2] - 1 and r2[0] < r1[2] - 1 and r1[1] < r2[3] - 1 and r2[1] < r1[3] - 1:
                overl.append("%s/%s" % (items[a][1][0][:14], items[c][1][0][:14]))
    ok = len(nonbar) >= 2 and len(xs0) == 1 and len(xs1) == 1 and not arrows and not overl
    result("settings_%s_%s_column" % (where, tag), ok, "%d rows, left edges %s, right edges %s, arrow plates %s, overlaps %s" % (
        len(nonbar), sorted(xs0), sorted(xs1), arrows, overl))
    return rects


def part_settings():
    """P4-1: the Settings page's tabs: every row one plate on one column, the arrows inside their rows, nothing overlapping;
    a ◁ value ▷ row pressed on its right half goes on, on its value's left half back; a checkbox row toggles."""
    away()
    sim("head 0 1.75 0 0 0 0")
    ensure_pause()
    poke("Settings", 1.0)
    PT.wait_plates_still(10)
    rects = check_settings_page("general", "pause")
    sim("head 0 1.75 0 0 -20 0")
    time.sleep(0.5)
    pic("p4_1_settings_general")
    sim("head 0 1.75 0 0 0 0")
    # a ◁ ▷ row: Attack Indicator Colour, on and back (by where the finger presses)
    i = plate_index("Attack Indicator")
    if i:
        v0 = send("boards val %d" % i)
        poke_i(i, 1.5, 0.42)
        i = plate_index("Attack Indicator")
        v1 = send("boards val %d" % i)
        poke_i(i, 1.5, 0.08)
        i = plate_index("Attack Indicator")
        v2 = send("boards val %d" % i)
        val = lambda v: v.split("=")[-1].strip()
        result("settings_enum_row_on_and_back", val(v0) != val(v1) and val(v2) == val(v0), "Attack Indicator Colour: %s -> (right end) %s -> (the value's left half) %s" % (val(v0), val(v1), val(v2)))
    else:
        result("settings_enum_row_on_and_back", False, "no Attack Indicator row: %s" % boards()[:200])
    i = plate_index("No Blood")
    if i:
        v0 = send("boards val %d" % i)
        poke_i(i, 1.5)
        v1 = send("boards val %d" % plate_index("No Blood"))
        poke_i(plate_index("No Blood"), 1.5)
        v2 = send("boards val %d" % plate_index("No Blood"))
        val = lambda v: v.split("=")[-1].strip()
        result("settings_toggle_row", val(v0) != val(v1) and val(v2) == val(v0), "No Blood %s -> %s -> %s" % (val(v0), val(v1), val(v2)))
    for tab in ("Controls", "Audio", "Display", "Quality"):
        i = plate_index(tab, exact=True)
        if not i:
            result("settings_%s_tab" % tab.lower(), False, "no tab plate")
            continue
        hover_i(i)
        PT.wait_plates_still(6)
        check_settings_page(tab.lower(), "pause")
        sim("head 0 1.75 0 0 -20 0")
        time.sleep(0.5)
        pic("p4_1_settings_" + tab.lower())
        sim("head 0 1.75 0 0 0 0")
    away()
    poke("Back", 1.5)
    close_pause()


def part_dialogues():
    """P4-2: a question over the pause menu stands alone - the far screen (the game's dimmed screen behind it) left out."""
    away()
    sim("head 0 1.75 0 0 0 0")
    for item in ("Exit", "Forfeit"):
        ensure_pause()
        if not poke(item, 1.5):
            result("dialogue_" + item.lower(), False, "no %s plate" % item)
            continue
        b = PT.wait_boards(lambda b: "menu board dialogue" in b, 6)
        h = send("holes")
        sim("head 0 1.75 0 0 -20 0")
        time.sleep(0.6)
        pic("p4_2_dialogue_" + item.lower())
        sim("head 0 1.75 0 0 0 0")
        result("dialogue_%s_far_screen_out" % item.lower(), b is not None and "(0,0)-(1920,1080)" in h, "board %s; holes %s" % ((b or "-")[:80], h[:160]))
        if not poke("Cancel", 1.5):
            send("press B 6")
            time.sleep(1.0)
    away()
    close_pause()


def part_sliders():
    """P4-4: the VR settings' sliders (Hands: Hand sounds, Touch buzz) from the side, before (one queue) and now."""
    sim("head 0 1.75 0 0 0 0")
    ensure_pause()
    send("vrset open Hands")
    v = PT.wait_vrset(lambda v: v.startswith("page"), 4)
    if v is None:
        PT.wait_vrset(lambda v: v.startswith("entry"), 6)
        send("vrset open Hands")
        v = PT.wait_vrset(lambda v: v.startswith("page Hands"), 6)
    q = send("vrslider 3 0.5")
    result("sliders_own_queues", "queues 5106/5107/5108" in q, q[-60:])
    # (the user's mark 4: the head 21 cm right of where it was when the page opened - the eyes' place decides the order of
    # the transparent quads, not where they look)
    for name, x, yaw in (("eyes_right25cm", 0.25, 27), ("eyes_left25cm", -0.25, -27), ("eyes_right40cm", 0.40, 38)):
        for mode in ("old", "new"):
            log("sliderqueues %s: %s" % (mode, send("sliderqueues " + mode)[:120]))
            sim("head %.2f 1.75 0 %d -20 0" % (x, yaw))
            time.sleep(0.6)
            pic("p4_4_sliders_%s_%s" % (name, "before" if mode == "old" else "now"))
    send("sliderqueues new")
    sim("head 0 1.75 0 0 0 0")
    send("vrset close")
    time.sleep(1.0)
    close_pause()


def part_encounter():
    """P4-6: an encounter's card and the controls column (Inventory / Continue) never overlap as the eyes see them."""
    close_pause()
    away()
    sim("head 0 1.75 0 0 0 0")
    r = send("jump fight " + os.environ.get("P4_ENC", "Gnomes"))
    log("encounter jump: %s" % r)
    t0 = time.time()
    while time.time() - t0 < 30 and not re.search(r"nav:(Continue|Inventory)", boards()):
        time.sleep(1)
    worst = None
    for k in range(8):
        time.sleep(1.0)
        nc = send("navcards")
        n = int(nc.split()[0]) if nc[:1].isdigit() else -1
        worst = n if worst is None else max(worst, n)
    g = send("navgeo")
    b = send("boards")
    clear = re.search(r"column [^|]*", b)
    # the user's card (P4-6): a card-sized box where the column stands, a little to its left - the column must step clear
    r1 = send("navobstacle add 0.24 0.22 0.47 0.16 0.24")
    time.sleep(0.8)
    nc = send("navobstacle")
    g2 = send("navgeo")
    send("navobstacle clear")
    result("encounter_column_steps_clear", "clear of the shown cards" in nc, "%s | %s" % (nc[:160], g2[:120]))
    result("encounter_column_clear_of_cards", worst == 0, "cards meeting the column over 8 s: max %s; %s; %s" % (worst, clear.group(0) if clear else "-", g[:160]))
    for name, yaw, pitch in (("straight", 0, -20), ("right28", -28, -25)):
        sim("head 0 1.75 0 %d %d 0" % (yaw, pitch))
        time.sleep(0.8)
        pic("p4_6_encounter_" + name)
    sim("head 0 1.75 0 0 0 0")


def part_title():
    """P4-1 from the title: its Settings, the same column (a session at the title)."""
    sim("head 0 1.75 0 0 -10 0; " + PT.REST)
    time.sleep(1.0)
    if not poke("Settings", 1.5):
        result("title_settings", False, "no Settings plate at the title: %s" % boards()[:200])
        return
    PT.wait_plates_still(10)
    check_settings_page("general", "title")
    pic("p4_1_title_settings_general")
    for tab in ("Controls", "Audio", "Display", "Quality"):
        i = plate_index(tab, exact=True)
        if i:
            hover_i(i)
            PT.wait_plates_still(6)
            check_settings_page(tab.lower(), "title")
            pic("p4_1_title_settings_" + tab.lower())
    away()
    poke("Back", 1.5)


def part_fight():
    """P4-10, P4-11, P4-17 in one fight (thieves; a weapon with a special and an artefact equipped)."""
    import p4_explore as X
    X.PT.LOG = PT.LOG
    X.OUT = PICS
    X.TAG = "p4"
    if not X.into_fight(showcase_pic="p4_11_showcase_now"):
        result("fight_reached", False, PT.state()[:80])
        return
    time.sleep(3)
    import hud_pics as HP
    HP.OUT = PICS
    HP.LABELS = PICS + "/labels.json"
    # P4-17: the board, the old look then the game's art (the same fight, the same place)
    send("fighthud art old")
    time.sleep(1.5)
    HP.part_board("p4_17_board_before", "thral")
    send("fighthud art game")
    time.sleep(2.5)
    HP.part_board("p4_17_board_now", "thral")
    lg = "".join(gamelog_tail(0)[-4000:])
    fh = send("fighthud?")
    mirrors = lg.count("HudMirror: the game's")
    result("board_game_art", "board placed True" in fh and mirrors >= 2 and "question box parchment" in lg,
           "mirrors made %d, %s | %s" % (mirrors, "parchment face" if "question box parchment" in lg else "NO parchment", fh[:200]))
    # P4-10: the special full -> the weapon glove's badge lit, the prompt by the hand; empty -> no prompt
    send("super? set 100")
    time.sleep(1.0)
    hi = send("handicons")
    send("super? set 0")
    time.sleep(1.0)
    hi0 = send("handicons")
    w_ok = "weapon shown" in hi and "READY prompt" in hi.split(" | ")[0]
    w_off = "weapon shown" in hi0 and "prompt" not in hi0.split(" | ")[0]
    result("hand_badges_and_prompt", w_ok and w_off and "artefact shown" in hi, "full: %s || empty: %s" % (hi[:220], hi0[:160]))
    send("super? set 100")
    sim("head 0 1.7 0 0 -25 0; hand r 0.12 1.35 -0.32 180 -10 90; hand l -0.25 1.0 -0.25 0 -60 0")
    time.sleep(1.0)
    send("timescale 0")
    pic("p4_10_badge_weapon")
    send("timescale 1")
    sim("head 0 1.7 0 0 -15 0; hand r 0.22 1.25 -0.35 0 -20 0; hand l -0.25 1.15 -0.3 0 -20 0")
    time.sleep(1.0)
    send("timescale 0")
    pic("p4_10_prompt_guard")
    send("timescale 1")
    sim("head 0 1.7 0 0 -25 0; hand l -0.1 1.33 -0.3 180 -10 -90; hand r 0.25 1.0 -0.25 0 -60 0")
    time.sleep(1.0)
    send("timescale 0")
    pic("p4_10_badge_artefact")
    send("timescale 1")
    # P4-11: one enemy at 2.6 m, one frozen moment: as before (glow 1) and now (0.45, health glow 0.3), by day and by night
    def shells(where):
        who, p = HP.enemy()
        HP.stand(who, 2.6)
        send("face track off")
        time.sleep(1.0)
        send("timescale 0")
        for label, g, ge, rk in (("before", 1.0, 1.0, 1.0), ("now", 0.45, 0.25, 0.3)):
            send("shell tune glow %s" % g)
            send("shell tune glowedge %s" % ge)
            send("shell tune rimk %s" % rk)
            time.sleep(0.4)
            pic("p4_11_shell_%s_%s" % (where, label))
        send("timescale 1")
    shells("day")


def part_night():
    """P4-11 and P4-17 in a dark arena: EL_Ambush_Empire at night (`envstate Night`), skeletons."""
    import p4_explore as X
    X.PT.LOG = PT.LOG
    X.OUT = PICS
    X.TAG = "p4"
    if not X.into_fight(spec=os.environ.get("P4_NIGHT_SPEC", "ThievesThugREF*3"), fight="EL_Ambush_Empire", showcase_pic="p4_11_showcase_night"):
        result("night_fight_reached", False, PT.state()[:80])
        return
    r = send("envstate Night")
    t0 = time.time()
    while time.time() - t0 < 30 and "loading True" in send("envstate"):
        time.sleep(0.5)
    time.sleep(2.5)
    e = send("envstate")
    result("night_arena", "Night" in e.split("|")[0], e[:160])
    import hud_pics as HP
    HP.OUT = PICS
    HP.LABELS = PICS + "/labels.json"
    who, p = HP.enemy()
    HP.stand(who, 2.6)
    send("face track off")
    time.sleep(1.0)
    send("timescale 0")
    for label, g, ge, rk in (("before", 1.0, 1.0, 1.0), ("now", 0.45, 0.25, 0.3)):
        send("shell tune glow %s" % g)
        send("shell tune glowedge %s" % ge)
        send("shell tune rimk %s" % rk)
        time.sleep(0.4)
        log("bars? (%s): %s" % (label, send("bars?")[:300]))
        pic("p4_11_shell_night_%s" % label)
    send("timescale 1")
    log("shell parts: %s" % send("shell parts nearest")[:400])
    send("fighthud art old")
    time.sleep(1.5)
    HP.part_board("p4_17_board_night_before", "night")
    send("fighthud art game")
    time.sleep(2.5)
    HP.part_board("p4_17_board_night_now", "night")


PARTS = {"night": part_night, "fight": part_fight, "calib": part_calib, "grip": part_grip, "credits": part_credits, "settings": part_settings, "dialogues": part_dialogues,
         "sliders": part_sliders, "encounter": part_encounter, "title": part_title}


def main():
    global PICS
    a = sys.argv[1:]
    parts = ["calib"]
    if "--parts" in a:
        parts = a[a.index("--parts") + 1].split(",")
    if "--pics" in a:
        PICS = a[a.index("--pics") + 1]
    log("=== p4_test %s (state %s)" % (",".join(parts), PT.state()[:60]))
    for p in parts:
        try:
            PARTS[p]()
        except Exception as e:
            result(p, False, "exception: %r" % e)
            close_all()
    ok = sum(1 for _, o in RES if o)
    log("p4_test: %d/%d passed" % (ok, len(RES)))
    sys.exit(0 if ok == len(RES) else 1)


if __name__ == "__main__":
    main()

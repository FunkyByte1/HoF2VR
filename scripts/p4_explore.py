#!/usr/bin/env python3
"""Playtest 4 (env 1): look at the menus, the encounter card and the grips before the fixes (from a session at the table).

  p4_explore.py [--parts grip,pause,settings,dialogues,sliders,encounter] [--tag X]
Pictures -> captures/p4/explore/<tag>_<name>.jpg (both eyes), the UI texture -> testdata/hof2vr_test/shots/<name>_ui.png
(copied next to the pictures), widget dumps -> the game log ("uiwhere ..."); a summary on stdout / logs/p4_explore.log.
  grip       P4-5 first (no trigger touch in the session yet): the grip held from before the pause opens, the pause, a poke at
             Resume with the grip still held - with `fingers assume off` (the old rule) and `on` (Quest Touch); then a fist
             (trigger touched) that lifts its index (the lift-to-point)
  pause      the pause board; hover Credits (the board grows: what shows)
  settings   Settings: General, then each tab hovered (Controls, Audio, Display, Quality)
  dialogues  Exit's question, Forfeit's question (the far screen's holes)
  sliders    VR Settings > Hands from three head yaws (P4-4)
  encounter  `jump fight EL_Ambush_Goblins`: the encounter page's card and the controls column (P4-6), `navcards`
"""
import os
import re
import shutil
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import playtest as PT  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H   # (this worktree's: env N's copy drives env N's game)

PT.LOG = H + "/logs/p4_explore.log"
OUT = H + "/captures/p4/explore"
SHOTS = hof2env.TESTDATA + "/shots"
send, sim, st, log = PT.send, PT.sim, PT.st, PT.log
TAG = "x"


def pic(name, ui=True):
    os.makedirs(OUT, exist_ok=True)
    if ui:
        log("BOARDS %s: %s" % (name, send("boards")))
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
        Image.open(src).convert("RGB").save(OUT + "/" + TAG + "_" + name + ".jpg", quality=88)
    except Exception as e:
        log("picture %s: %s" % (name, e))
    if ui:
        r = send("rtshot " + name)
        time.sleep(0.8)
        try:
            shutil.copy(SHOTS + "/" + name + "_ui.png", OUT + "/" + TAG + "_" + name + "_ui.png")
        except OSError as e:
            log("rtshot copy %s: %s" % (name, e))
        log("rtshot %s: %s" % (name, r))


def boards():
    return PT.boards_now()


def hover(idx, cm=2.0):
    """The right fingertip `cm` in front of plate idx (1-based): the game's hover, no press."""
    r = send("platepose %d %.1f r" % (idx, cm))
    if "|" not in r:
        return False
    pos = r.split("|")[0].strip()
    m = send("handto r tip " + pos)
    if not m.startswith("move"):
        return False
    sim(m + " 300")
    time.sleep(1.2)
    return True


def away():
    sim(PT.REST)
    time.sleep(0.6)


def plate_index(label):
    b = st().get("boards", "")
    for i, t in re.findall(r"\[(\d+) ([^\]]*)\]", b):
        if label.lower() in t.lower():
            return int(i)
    return None


def poke(label, settle=1.5):
    """A poke without the tour's grip habit (no trigger touch is ever reported by it)."""
    i = plate_index(label)
    if i is None:
        log("no plate '%s' in %s" % (label, boards()[:200]))
        return False
    return PT._poke_pose("platepose %d 6 r" % i, settle)


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
    send("state")
    log("grips at the start: %s | fingers: %s" % (st().get("grips", ""), send("fingers")[:120]))
    for mode in ("off", "on"):
        close_pause()
        log("fingers assume %s: %s" % (mode, send("fingers assume " + mode)))
        sim(PT.REST + "; squeeze r 0; ctouch r trigger 0")
        time.sleep(0.5)
        sim("squeeze r 1")   # the grip closes with the index lifted (no trigger touch)
        time.sleep(0.6)
        send("state")
        g0 = st().get("grips", "")
        send("press START 6")
        b = PT.wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 10)
        n0 = PT.st().get("boards", "")
        sel0 = re.search(r"selects (\d+)", b or "")
        t = poke("Resume", 2.0)
        b2 = boards()
        closed = "menu board" not in b2
        send("state")
        log("GRIP assume %s: grip before START '%s'; the poke at Resume with the grip held -> %s (boards now: %s)" % (
            mode, g0, "the menu CLOSED (pressed)" if closed else "still open (refused)", b2[:160]))
        sim("squeeze r 0")
        time.sleep(0.5)
    # a fist (the index on the trigger) that lifts its index: a page first, then a pointing hand
    close_pause()
    send("fingers assume on")
    sim(PT.REST + "; ctouch r trigger 1; wait 200; squeeze r 1")
    time.sleep(0.5)
    send("state")
    g1 = st().get("grips", "")
    sim("ctouch r trigger 0")
    time.sleep(0.8)
    send("state")
    g2 = st().get("grips", "")
    log("GRIP fist then lift: with the index on the trigger '%s'; lifted 0.8 s '%s'" % (g1, g2))
    send("press START 6")
    PT.wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 10)
    poke("Resume", 2.0)
    log("GRIP fist then lift, the poke at Resume -> %s" % ("pressed (closed)" if "menu board" not in boards() else "refused"))
    sim("squeeze r 0; ctouch r trigger 0")
    send("fingers assume auto")
    close_pause()


def part_pause():
    away()
    sim("head 0 1.75 0 0 0 0")
    b = ensure_pause()
    log("PAUSE board: %s" % (b or "-")[:400])
    log("holes: %s" % send("holes"))
    send("uiwhere SecondaryMenus/Pause 400")
    sim("head 0 1.75 0 0 -20 0")
    time.sleep(0.6)
    pic("pause")
    i = plate_index("Credits")
    if i:
        hover(i)
        log("PAUSE hovering Credits: %s" % boards()[:400])
        send("uiwhere SecondaryMenus/Pause 400")
        pic("pause_credits_hover")
    away()


def part_settings():
    sim("head 0 1.75 0 0 0 0")
    ensure_pause()
    poke("Settings", 1.0)
    b = PT.wait_plates_still(10)
    log("SETTINGS (General): %s" % (b or boards())[:900])
    log("holes: %s" % send("holes"))
    send("uiwhere Pause/Right 600")
    sim("head 0 1.75 0 0 -20 0")
    time.sleep(0.6)
    pic("settings_general")
    for tab in ("Controls", "Audio", "Display", "Quality"):
        i = plate_index(tab)
        if not i:
            log("no tab %s" % tab)
            continue
        hover(i)
        b = PT.wait_plates_still(6) or boards()
        log("SETTINGS (%s): %s" % (tab, b[:900]))
        pic("settings_" + tab.lower())
        send("uiwhere Pause/Right 600")
    away()
    i = plate_index("Back")
    if i:
        poke("Back", 1.5)
    log("after Back: %s" % boards()[:200])


def part_dialogues():
    sim("head 0 1.75 0 0 0 0")
    ensure_pause()
    for item in ("Exit", "Forfeit"):
        ensure_pause()
        if not poke(item, 1.5):
            continue
        b = PT.wait_boards(lambda b: "menu board dialogue" in b, 6)
        log("DIALOGUE %s: %s" % (item, (b or boards())[:400]))
        log("holes: %s" % send("holes"))
        send("uiwhere DialogueRoot 300")
        sim("head 0 1.75 0 0 -20 0")
        time.sleep(0.6)
        pic("dialogue_" + item.lower())
        sim("head 0 1.75 0 0 0 0")
        # the cancelling answer (never OK: Exit quits, Forfeit forfeits)
        if not poke("Cancel", 1.5):
            send("press B 6")
            time.sleep(1.0)
        log("after Cancel: %s" % boards()[:160])
    away()


def part_sliders():
    sim("head 0 1.75 0 0 0 0")
    ensure_pause()
    send("vrset open Hands")
    v = PT.wait_vrset(lambda v: v.startswith("page"), 4)
    if v is None:
        PT.wait_vrset(lambda v: v.startswith("entry"), 6)
        send("vrset open Hands")
        v = PT.wait_vrset(lambda v: v.startswith("page Hands"), 6)
    log("SLIDERS page: %s" % (v or PT.vrset_now())[:200])
    for r in range(1, 6):
        log("vrslider %d: %s" % (r, send("vrslider %d 0.5" % r)))
    for name, yaw, pitch in (("straight", 0, -20), ("left25", 25, -20), ("right25", -25, -20), ("left40", 40, -15), ("right40", -40, -15)):
        sim("head 0 1.75 0 %d %d 0" % (yaw, pitch))
        time.sleep(0.8)
        pic("sliders_" + name, ui=False)
    sim("head 0 1.75 0 0 0 0")
    send("vrset close")
    time.sleep(1.0)
    close_pause()


def part_encounter():
    close_pause()
    away()
    sim("head 0 1.75 0 0 0 0")
    r = send("jump fight EL_Ambush_Goblins")
    log("ENCOUNTER jump: %s" % r)
    t0 = time.time()
    b = ""
    while time.time() - t0 < 30:
        b = boards()
        if "Continue" in b or "Inventory" in b:
            break
        time.sleep(1)
    time.sleep(2)
    b = boards()
    log("ENCOUNTER boards: %s" % b[:600])
    log("navgeo: %s" % send("navgeo")[:600])
    log("navcards: %s" % send("navcards"))
    for name, yaw, pitch in (("straight", 0, -15), ("right20", -20, -15), ("down", 0, -30)):
        sim("head 0 1.75 0 %d %d 0" % (yaw, pitch))
        time.sleep(0.8)
        pic("encounter_" + name, ui=False)
    sim("head 0 1.75 0 0 0 0")


def menu_now():
    try:
        return PT.cmd.status().get("menu") or ""
    except Exception:
        return ""


def into_fight(spec="ThievesThugREF*3", fight="EL_Ambush_Thral", equip="BasicSword,bombBasic,BasicShield", showcase_pic=None):
    """From the map: the cards equipped, the fight's enemies, the jump; through the gate; a picture of the game's enemy
    showcase (P4-11); then the fight (god on)."""
    send("vrcombat physical")
    for c in equip.split(","):
        log("equip %s: %s" % (c, send("equip " + c)))
        time.sleep(3.5)
    log("fightwith: %s" % send("fightwith " + spec))
    r = ""
    t0 = time.time()
    while time.time() - t0 < 60:
        r = send("jump fight " + fight)
        if r.startswith("started"):
            break
        time.sleep(2)
    log("jump: %s" % r)
    send("gate auto 0")
    t0 = time.time()
    shown = False
    while time.time() - t0 < 150:
        s = PT.state()
        m = menu_now()
        if "Level_Play" in s:
            break
        if m in ("Encounter", "Dialogue", "Activation") or m.startswith("Encounter"):
            send("press A 6")
            time.sleep(2.0)
            continue
        if "Showcase" in m:
            if showcase_pic and not shown:
                time.sleep(2.5)
                sim("head 0 1.75 0 0 -5 0; " + PT.REST)
                time.sleep(0.8)
                pic(showcase_pic, ui=False)
                send("widgets 0 Showcase")
                shown = True
            sim("btn r a 150")
            time.sleep(1.5)
            continue
        if send("gate", 4).startswith("gate waiting"):
            sim("btn r a 150")
            time.sleep(1.5)
        time.sleep(0.5)
    s = PT.state()
    log("fight: %s" % s[:60])
    send("god on")
    return "Level_Play" in s


def part_fight():
    """The flat HUD's art (P4-17), the board and the hands' badges (P4-10), the shells by day and by night (P4-11)."""
    if not into_fight(showcase_pic="showcase_before"):
        return
    time.sleep(3)
    send("widgets 0 CombatHUD")
    send("widgets 0 ShowcaseCard")
    log("fighthud?: %s" % send("fighthud?")[:400])
    log("super?: %s" % send("super? set 100")[:200])
    time.sleep(1.0)
    log("handicons: %s" % send("handicons"))
    log("ability?: %s" % send("abilitylog")[:300])
    import hud_pics as HP
    HP.OUT = OUT
    HP.LABELS = OUT + "/labels.json"
    try:
        HP.part_board("hud_before", "thral")
    except Exception as e:
        log("board pictures: %r" % e)
    # P4-17: the game's own flat HUD dials, copied onto a test panel in front (HudMirror)
    sim("head 0 1.7 0 0 -5 0; " + PT.REST)
    time.sleep(0.6)
    log("mirror: %s" % send("fighthud mirror")[:300])
    time.sleep(1.0)
    send("timescale 0")
    pic("mirror_test", ui=False)
    send("timescale 1")
    send("fighthud mirror off")
    # the weapon hand turned to the eyes (its back), then the shield hand's cuff: the badges, and the prompt beside the hand
    sim("head 0 1.7 0 0 -25 0; hand r 0.12 1.35 -0.32 180 -10 90; hand l -0.25 1.0 -0.25 0 -60 0")
    time.sleep(1.0)
    log("handicons (weapon hand up): %s" % send("handicons"))
    send("timescale 0")
    pic("badge_weapon", ui=False)
    send("timescale 1")
    sim("head 0 1.7 0 0 -25 0; hand l -0.12 1.35 -0.32 180 -10 -90; hand r 0.25 1.0 -0.25 0 -60 0")
    time.sleep(1.0)
    log("handicons (shield hand up): %s" % send("handicons"))
    send("timescale 0")
    pic("badge_artefact", ui=False)
    send("timescale 1")
    # the shells: one enemy from 2.6 m, as they are, then fainter
    who, p = HP.enemy()
    HP.stand(who, 2.6)
    send("face track off")
    def shells(where):
        for label, g, rk in (("before", 1.0, 1.0), ("now", 0.45, 0.45), ("fainter", 0.3, 0.3)):
            send("shell tune glow %s" % g)
            send("shell tune rimk %s" % rk)
            time.sleep(0.8)
            send("timescale 0")
            pic("shell_%s_%s" % (where, label), ui=False)
            send("timescale 1")
        send("shell tune glow 0.45")
        send("shell tune rimk 0.45")
    shells("day")
    log("envstate: %s" % send("envstate")[:300])
    log("envstate night: %s" % send("envstate Night")[:200])
    t0 = time.time()
    while time.time() - t0 < 30 and "loading True" in send("envstate"):
        time.sleep(0.5)
    time.sleep(2.0)
    who, p = HP.enemy()
    HP.stand(who, 2.6)
    shells("night")
    # P4-17: the game's own props and textures a board could use
    for k in ("sign", "board", "plank", "banner", "post"):
        log("fxfind %s: %s" % (k, send("fxfind %s 12" % k)[:200]))


def part_title():
    """Settings from the title screen (a session at the title): the title board's Settings, then each tab."""
    sim("head 0 1.75 0 0 -10 0; " + PT.REST)
    time.sleep(1.0)
    b = boards()
    log("TITLE boards: %s" % b[:300])
    if not poke("Settings", 1.5):
        log("no Settings plate at the title")
        return
    b = PT.wait_plates_still(10) or boards()
    log("TITLE SETTINGS: %s" % b[:600])
    pic("title_settings_general")
    for tab in ("Controls", "Audio", "Display", "Quality"):
        i = plate_index(tab)
        if i:
            hover(i)
            PT.wait_plates_still(6)
            pic("title_settings_" + tab.lower())
    away()
    poke("Back", 1.5)


PARTS = {"title": part_title, "fight": part_fight, "grip": part_grip, "pause": part_pause, "settings": part_settings, "dialogues": part_dialogues, "sliders": part_sliders,
         "encounter": part_encounter}


def main():
    global TAG
    a = sys.argv[1:]
    parts = "grip,pause,settings,dialogues,sliders,encounter".split(",")
    if "--parts" in a:
        parts = a[a.index("--parts") + 1].split(",")
    if "--tag" in a:
        TAG = a[a.index("--tag") + 1]
    log("=== p4_explore %s (state %s)" % (",".join(parts), PT.state()[:60]))
    for p in parts:
        try:
            log("--- " + p)
            PARTS[p]()
        except Exception as e:
            log("part %s: exception %r" % (p, e))
    log("=== p4_explore done")


if __name__ == "__main__":
    main()

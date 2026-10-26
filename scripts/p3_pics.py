#!/usr/bin/env python3
"""Playtest 3 fixes: both-eye pictures of each new or changed fight marker in a live fight of this environment, the user's
look (Gilded), from the player's place -> the main worktree's HandOfFate2/captures/p3-fixes-2026-10-18/.

  p3_pics.py melee     a blow's bar with the shield's circle; the sword lined up (green bar, the circle faded); the shield
                       on the circle (green circle, the bar faded); the outcome's flash; a hit fading grey
  p3_pics.py glow      the weapon hand's glow (the super full), the hand raised in view
  p3_pics.py aim       the stick held: the ring where the hand points
  p3_pics.py offview   the edge arrows (an attacker behind)
  p3_pics.py shieldlow the shield with the wrist pointing down (mark 29)
  p3_pics.py ground    (a fight without a shield and lob throwers) the ground arrow of a lob's wind-up
  p3_pics.py bomb      (the tutorial skeletons) the bomb mark
"""
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
os.environ.setdefault("LINES_OUT", "/home/bara/Projects/flat2vr/HandOfFate2/captures/p3-fixes-2026-10-18")
sys_argv = sys.argv[:]
sys.argv = [sys.argv[0]]
import lines_pics as L  # noqa: E402
import stage1_test as S1  # noqa: E402
sys.argv = sys_argv

HEAD = "head 0 1.7 0 0 0 0"
GUARD = "hand r 0.25 1.35 -0.35 0 5 0; hand l -0.25 1.2 -0.3 0 0 0"


def prep(enemy=None):
    L.send("vrlook Gilded")
    if enemy:
        L.send("face track " + enemy)
        L.send("heroat %s 0 2.0" % enemy)


def done():
    L.send("face track off")
    L.send("timescale 1")


def melee():
    prep("EmpireSoldier" if L.send("enemy? EmpireSoldier").startswith("EmpireSoldier") else "nearest")
    try:
        # 1: the bar with the circle (guard held, off the bar)
        L.sim(HEAD + "; " + S1.HAND_R + "; " + S1.SHIELD_AWAY)
        st = L.strike(fresh=0.6)
        if st:
            time.sleep(st["eta"] * 0.35)
            L.shot("m1_bar_and_circle")
            time.sleep(st["eta"] + 1.0)
        # 2: the sword lined up along the bar
        for _ in range(4):
            L.sim(HEAD + "; " + S1.HAND_R + "; " + S1.SHIELD_AWAY)
            S1.blade_rest()
            st = L.strike(fresh=0.8)
            if not st:
                break
            g = L.send("guardat blade")
            m = re.search(r"axis \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", g)
            if m:
                S1.blade_along([float(m.group(i)) for i in (1, 2, 3)])
            S1.guard_to("blade")
            time.sleep(0.1)
            s = L.send("strike?")
            if "valid True" in s.split(" | ")[0]:
                L.shot("m2_sword_lined_up_green")
                c0 = L.counts()
                what = L.wait_outcome(c0)
                time.sleep(0.04)
                L.shot("m3_after_sword_" + str(what))
                break
            time.sleep(st["eta"] + 0.8)
        # 3: the shield on the circle
        for _ in range(4):
            L.sim(HEAD + "; " + S1.HAND_R + "; " + S1.SHIELD_AWAY)
            st = L.strike(fresh=0.8)
            if not st:
                break
            S1.guard_to("shield")
            time.sleep(0.12)
            s = L.send("strike?")
            if "onShield True" in s.split(" | ")[0]:
                L.shot("m4_shield_on_circle_green")
                c0 = L.counts()
                what = L.wait_outcome(c0)
                time.sleep(0.04)
                L.shot("m5_after_shield_" + str(what))
                break
            time.sleep(st["eta"] + 0.8)
        # 4: a hit (hands down): the bar fades grey
        L.sim(HEAD + "; " + S1.HANDS_DOWN + "; " + S1.SHIELD_AWAY)
        for _ in range(3):
            st = L.strike(fresh=0.5)
            if not st:
                break
            c0 = L.counts()
            what = L.wait_outcome(c0)
            if what == "hitsTaken":
                time.sleep(0.06)
                L.shot("m6_hit_fades_grey")
                break
    finally:
        done()


def shieldon():
    """v3 (2026-10-19): the shield ON its circle in a real fight, placed by script (live timing never caught it): when a blow
    shows its circle, the fight slows to 5 %, the simulator puts the shield hand on the circle (`guardat shield`: xrsim's
    pose), and once the game judges it on the circle (strike?'s onShield True: the circle green, the bar faded) the fight is
    frozen for the picture; then the blow lands on the shield."""
    prep("EmpireSoldier" if L.send("enemy? EmpireSoldier").startswith("EmpireSoldier") else "nearest")
    # left-handed (`cfg LeftHanded true`, v4): the shield in the right hand - the poses mirrored
    away = S1.SWORD_AWAY + "; " + S1.SHIELD_AWAY
    if "LeftHanded=true" in L.send("cfg LeftHanded"):
        away = "hand l -0.45 0.95 0.3 0 -80 0; hand r 0.45 1.0 0.25 0 0 0"
        print("shieldon: left-handed (the shield in the right hand)", flush=True)
    tag = os.environ.get("SHOT_TAG", "")   # appended to the pictures' names (v4: _buckler, _lefthanded)
    print("shieldon: " + L.send("ringframe"), flush=True)
    try:
        for i in range(8):
            # the sword away too (v3's second run: the sword held up was judged the guard, onShield False)
            L.sim(HEAD + "; " + away)
            st = L.strike(fresh=0.6)
            if not st:
                break
            if "ring True" not in st["raw"] or "offview" in st["raw"]:
                time.sleep(st["eta"] + 0.8)
                continue
            L.send("timescale 0.05")
            # the soldier that strikes, in the middle of the view (v3's first run: off to the side; v4: `face track` kept the view
            # on the other soldier, the striker off-view)
            L.send("face track off")
            L.send("face striker")
            time.sleep(0.3)
            # v4: the ring waiting for the shield (slim, the look's colour), the shield still away
            if "offview" not in L.send("strike?").split(" | ")[0]:
                L.send("timescale 0")
                time.sleep(0.3)
                L.shot("m3w_ring_waiting" + tag)
                L.send("timescale 0.05")
            S1.guard_to("shield", ms=80)
            ok = False
            for _ in range(20):
                time.sleep(0.1)
                if "onShield True" in L.send("strike?").split(" | ")[0]:
                    ok = True
                    break
            if ok:
                time.sleep(0.25)   # the circle's colour eases
                first = L.send("strike?").split(" | ")[0]
                L.send("timescale 0")
                time.sleep(0.3)
                if "onShield True" not in first:
                    print("shieldon: try %d left the circle before the picture: %s" % (i + 1, first[:200]), flush=True)
                    L.send("timescale 1")
                    time.sleep(2.0)
                    continue
                L.shot("m4_shield_on_circle_green_scripted" + tag)
                print("shieldon: on the circle at try %d (just before the picture): %s" % (i + 1, first[:200]), flush=True)
                # (v3: the circle was the shield's size, held right on it the shield hid it; since v4 a ring frames the shield.)
                # The shield lowered as far as the guard still counts as on the circle: the attacker in view above it
                L.send("timescale 0.05")
                for dy in (-0.14, -0.11, -0.08, -0.05):
                    S1.guard_to("shield", (0.0, dy, 0.0), ms=60)
                    time.sleep(0.35)
                    f2 = L.send("strike?").split(" | ")[0]
                    if "onShield True" in f2:
                        L.send("timescale 0")
                        time.sleep(0.3)
                        L.shot("m4b_shield_on_circle_low_green_scripted" + tag)
                        print("shieldon: still on the circle %.0f cm lower: %s" % (-dy * 100, f2[:200]), flush=True)
                        break
                    print("shieldon: %.0f cm lower is off the circle" % (-dy * 100), flush=True)
                L.send("timescale 1")
                c0 = L.counts()
                print("shieldon: outcome", L.wait_outcome(c0), flush=True)
                break
            print("shieldon: try %d not on the circle: %s" % (i + 1, L.send("strike?").split(" | ")[0][:200]), flush=True)
            L.send("timescale 1")
            time.sleep(st["eta"] + 0.8)
    finally:
        done()


def glow():
    prep()
    L.send("face nearest")
    L.send("super? set 100")
    L.sim(HEAD + "; hand r 0.15 1.45 -0.35 0 30 0; hand l -0.25 1.2 -0.3 0 0 0")
    time.sleep(0.5)
    L.shot("d1_super_ready_hand_glow")


def aim():
    prep()
    L.send("face nearest")
    L.send("super? set 100")
    L.sim(HEAD + "; hand r 0.25 1.3 -0.4 0 -25 0")
    L.sim("hold r stick 1")
    time.sleep(0.9)
    L.shot("s1_stick_held_aim_ring")
    L.sim("hold r stick 0")
    time.sleep(0.5)


def offview():
    prep()
    L.sim(HEAD + "; " + S1.HANDS_DOWN)
    e = L.send("enemy? nearest")
    m = re.search(r"bearing (-?[\d.]+)", e)
    # turn away from the nearest enemy, then wait for a warning
    L.send("face nearest")
    L.sim("head 0 1.7 0 160 0 0")
    t0 = time.time()
    while time.time() - t0 < 30:
        w = L.send("warn?")
        if re.search(r"markersShown (\d+)", w):
            pass
        s = L.send("strike?")
        if "offview" in s:
            time.sleep(0.4)
            L.shot("w1_attack_from_behind_arrow")
            break
        time.sleep(0.1)
    L.sim(HEAD)


def shieldlow():
    prep()
    L.send("face nearest")
    L.sim(HEAD + "; hand r 0.3 1.0 -0.2 0 -60 0; hand l -0.2 1.15 -0.45 0 -80 0")
    time.sleep(0.4)
    L.shot("x1_shield_wrist_down")
    L.sim(HEAD + "; hand l -0.25 1.3 -0.4 0 0 0")
    time.sleep(0.4)
    L.shot("x2_shield_guard")


def ground():
    prep("ThievesMolotovThrower")
    L.sim(HEAD + "; " + S1.HANDS_DOWN)
    t0 = time.time()
    try:
        while time.time() - t0 < 60:
            s = L.send("shots?", 4)
            if "ground True" in s:
                time.sleep(0.25)
                L.sim("head 0 1.7 0 0 -25 0")   # a look down toward the thrower's feet
                L.shot("c1_cant_block_ground_arrow")
                L.sim(HEAD)
                break
            time.sleep(0.05)
    finally:
        done()


def bomb():
    prep()
    L.send("face SkeletonNEW")
    time.sleep(0.4)
    L.shot("t1_tutorial_skeletons_bomb_mark")


if __name__ == "__main__":
    for what in sys.argv[1:]:
        globals()[what]()

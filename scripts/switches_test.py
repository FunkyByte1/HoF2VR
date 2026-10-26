#!/usr/bin/env python3
"""The headset switches for merge-ready #2's new defaults (the coordinator 2026-10-14 ~07:30: every new default's earlier
answer switchable in Pause > VR Settings, not only in the settings file).

  switches_test.py [--start]

In a live fight (combat_fixture.py --fixture thieves --god; --start boots the dungeon map first), the VR settings page is
opened from the pause menu and each row is poked by the simulated hand; the setting (`cfg <Key>`) must follow at once:
  Fights tab: Enemy toughness - / + (EnemyHealth 0.15 -> 0.10 -> 0.15 since F2, 5 % steps; applies from the next fight), Lead Slow / Hold,
              Dash Short / UYF, Dash flick Off / On
  Looks tab:  Your info On you / Board, Enemy guard Rings / Shell, Dash look Blink / Slide, Difficulty flash Off / On
  Enemies tab (merge-ready #4's switches, 2026-10-16): Shoves, Varied pauses, Boss slow motion Off / On - the setting
              follows at both pokes, and so do the fight's own VariedPauses / BossSlowMotion (`vrpauses`, `vrbossslow`;
              the seams' test overrides cleared first); Shoves stay off in test runs unless a test asks (`vrshoves`), so
              only its setting is judged
and in the fight itself: with Dash = Short a back dash goes 2 m, with Dash = UYF again 3 m (`dash?`).
Results: PASS/FAIL in logs/switches_test.log; a shot of each tab (SW_fights, SW_looks, SW_enemies).
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import playtest as pt  # noqa: E402

H = pt.H
pt.LOG = H + "/logs/switches_test.log"
log, sim, send, st, result = pt.log, pt.sim, pt.send, pt.st, pt.result


def cfgv(key):
    r = send("cfg " + key)   # "Key=value" (or "Key=value (this run only)")
    return r.split("=", 1)[1].split()[0] if "=" in r else r


def open_page(tab):
    sim(pt.REST + "; head 0 1.75 0 0 0 0")
    send("press START 6")
    if not pt.wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 10):
        return False
    if not pt.wait_vrset(lambda v: v.startswith("entry"), 6):
        return False
    pt.poke_vrset("VR_Settings")
    if not pt.wait_vrset(lambda v: v.startswith("page"), 6):
        return False
    return go_tab(tab)


def go_tab(tab):
    pt.poke_vrset(tab)
    return pt.wait_vrset(lambda v: v.startswith("page " + tab), 6) is not None


def close_page():
    pt.poke_vrset("Back")
    pt.wait_boards(lambda b: "menu board pause" in b and "Resume" in b, 8)
    pt.poke_plate("Resume")
    pt.wait_boards(lambda b: "menu board" not in b, 8)
    time.sleep(1.0)


def poke_row(key, cfg_key, want):
    """Pokes the page's button `key` ("Lead/Slow", "Enemy_toughness/-"); the setting must read `want` at once."""
    before = cfgv(cfg_key)
    pt.poke_vrset(key)
    time.sleep(0.6)
    now = cfgv(cfg_key)
    ok = now.lower() == want.lower()
    return ok, "%s: %s -> %s" % (key.replace("_", " "), before, now)


def room_behind(m=6.0):
    """(merge-ready #4: UYF's dash lands 1 m before the floor's end and fails with under 0.5 m of room - candidate 7's first
    run: the UYF dash right after the Short one met the arena's end) The hero placed with `m` m of floor behind the gaze
    (`dash? edge`, as audit_test's backdash), the head turned until it can be. Returns the seam's answer."""
    e = ""
    for yaw in (0, 90, 180, 270):
        sim("head 0 1.75 0 %d 0 0" % yaw)
        time.sleep(0.3)
        e = send("dash? edge %.2f 180" % m)
        if "edge now" in e or "no edge" in e:
            return e
    return e


def back_dash():
    time.sleep(4.3)   # the charges back (4 s after any earlier dash)
    log("  room behind: " + room_behind()[:100])
    sim("stick l 0 -1 0")
    sim("btn r a 80")
    time.sleep(0.5)
    sim("stick l 0 0 0")
    time.sleep(0.4)
    d = send("dash?")
    m = re.search(r"last \w+ ([\d.]+) m of ([\d.]+)", d)
    return (float(m.group(1)) if m else -1.0, float(m.group(2)) if m else -1.0), d


def main():
    a = sys.argv[1:]
    open(pt.LOG, "a").close()
    log("==== switches test")
    if "--start" in a:
        subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"], capture_output=True, text=True, timeout=4200)
    r = subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--fixture", "thieves", "--god"], capture_output=True, text=True, timeout=600)
    log("fixture: " + (r.stdout.strip().splitlines() or ["-"])[-1])
    if "Level_Play" not in pt.state():
        result("switches_fight", False, "not in a fight: %s" % pt.state())
        return finish()
    send("vrknock off")
    send("vrmagnet off")
    # the Fights tab
    if not open_page("Fights"):
        result("switches_page", False, "the VR settings page did not open: %s" % pt.vrset_now()[:80])
        return finish()
    time.sleep(0.5)
    pt.shot("SW_fights")
    for key, ck, want in (("Enemy_toughness/-", "EnemyHealth", "0.1"), ("Enemy_toughness/+", "EnemyHealth", "0.15"),
                          ("Lead/Slow", "Lead", "Slow"), ("Lead/Hold", "Lead", "Hold"),
                          ("Dash_flick/Off", "DashFlick", "false"), ("Dash_flick/On", "DashFlick", "true"),
                          ("Dash/Short", "Dash", "Short")):
        ok, msg = poke_row(key, ck, want)
        result("switch_" + key.split("/")[0].lower(), ok, msg) if key.endswith(("+", "Hold", "On", "Short")) else log("  " + msg)
    # the Looks tab
    if go_tab("Looks"):
        time.sleep(0.5)
        pt.shot("SW_looks")
        # (Health look Pips / Bar went with the pips, 2026-10-18: Your info Board / On you and Enemy guard Shell / Rings)
        for key, ck, want in (("Your_info/On_you", "FightHud", "Body"), ("Your_info/Board", "FightHud", "Board"),
                              ("Enemy_guard/Rings", "EnemyStatus", "Rings"), ("Enemy_guard/Shell", "EnemyStatus", "Shell"),
                              ("Dash_look/Blink", "DashStyle", "Blink"), ("Dash_look/Slide", "DashStyle", "Slide"),
                              ("Difficulty_flash/Off", "DifficultyFlash", "false"), ("Difficulty_flash/On", "DifficultyFlash", "true")):
            ok, msg = poke_row(key, ck, want)
            result("switch_" + key.split("/")[0].lower(), ok, msg) if key.endswith(("Board", "Shell", "Slide", "/On")) else log("  " + msg)
    else:
        result("switches_looks_tab", False, "the Looks tab did not open: %s" % pt.vrset_now()[:80])
    # the Enemies tab (merge-ready #4): each row Off, then On (the defaults again); the fight's own value too
    for seam in ("vrshoves", "vrpauses", "vrbossslow"):
        send(seam + " setting")   # (no test override left from an earlier test: the setting rules)
    if go_tab("Enemies"):
        time.sleep(0.5)
        pt.shot("SW_enemies")
        for row, ck, seam, says in (("Shoves", "Shoves", "vrshoves", None),
                                    ("Varied_pauses", "VariedPauses", "vrpauses", "varied "),
                                    ("Boss_slow_motion", "BossSlowMotion", "vrbossslow", "BossSlowMotion ")):
            oks, msgs = [], []
            for choice, want in (("Off", "false"), ("On", "true")):
                ok, msg = poke_row(row + "/" + choice, ck, want)
                live = send(seam)
                if says is not None:
                    ok = ok and live.startswith(says + ("True" if want == "true" else "False"))
                oks.append(ok)
                msgs.append("%s (live: %s)" % (msg, live[:40]))
            result("switch_" + row.lower(), all(oks), " | ".join(msgs))
    else:
        result("switches_enemies_tab", False, "the Enemies tab did not open: %s" % pt.vrset_now()[:80])
    close_page()
    # Dash = Short now: a back dash goes 2 m
    (moved, of), d = back_dash()
    result("switch_dash_short_in_fight", 1.5 <= of <= 2.5, "Dash = Short: a back dash %.2f m of %.2f | %s" % (moved, of, d.split("| last ")[-1][:120]))
    # and UYF again: 3 m back
    if open_page("Fights"):
        ok, msg = poke_row("Dash/UYF", "Dash", "UYF")
        log("  " + msg)
        close_page()
    (moved2, of2), d2 = back_dash()
    result("switch_dash_uyf_in_fight", 2.5 <= of2 <= 3.5 and cfgv("Dash") == "UYF",
           "Dash = UYF again: a back dash %.2f m of %.2f | %s" % (moved2, of2, d2.split("| last ")[-1][:120]))
    send("vrknock setting")
    send("vrmagnet setting")
    finish()


def finish():
    fails = [n for n, ok in pt.RESULTS if not ok]
    log("==== %d/%d PASS%s" % (len(pt.RESULTS) - len(fails), len(pt.RESULTS), (" - FAIL: " + ", ".join(fails)) if fails else ""))
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""F8, the player's gnome fight (decisions.md "Gnomes", 2026-10-17; Game/GnomeFight.cs) in a gnome swarm fight.

  gnome_test.py [--tag T] [--no-fixture] [--stay] [--parts watch,cut,dash,cling]

Fixture: the thieves arena with Card_MO_Gnome_Special (a swarm sharing one boss bar), god and tough on.
  watch   40 s: never two gnomes in the air at once; leaps begin at least 1 s apart; two lines open at once are
          perpendicular; slow motion while one is in the air within reach
  watch   also: a leap right after another (under 2 s) has the perpendicular line; no leap while one clings
  cut     a gnome in the air 1.0-2.6 m away: a cut along its line through it knocks it (knocks +1)
  dash    a gnome winding up, or in the air at least 0.9 m away, none clinging: A with the stick to the side with the most
          floor (a sideways dash) makes it miss (dodged +1)
  cling   a gnome lands and clings (hands down): the ring is up, the dash is refused; five cuts through the ring free the
          hero (freed +1, clinging False)
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
TAG = args[args.index("--tag") + 1] if "--tag" in args else "GN"
OUT = []
HAND_R = "hand r 0.25 1.35 -0.35 0 5 0"
DOWN = "hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0"


def log(m):
    line = time.strftime("%H:%M:%S") + " " + m
    print(line, flush=True)


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


def g():
    r = send("gnomes?", 4)
    d = {"_raw": r}
    for k in ("begun", "gated", "knocks", "wrong", "dodged", "clings", "ringHits", "freed", "slowFrames", "ring"):
        m = re.search(r"\b%s (\d+)" % k, r)
        d[k] = int(m.group(1)) if m else 0
    d["clinging"] = " clinging True" in r
    m = re.search(r"ringT (-?[\d.]+),(-?[\d.]+),(-?[\d.]+)", r)
    d["ringT"] = [float(m.group(i)) for i in (1, 2, 3)] if m else None
    d["live"] = []
    live = r.split("| live", 1)[1].split("| last", 1)[0] if "| live" in r else ""
    for m in re.finditer(r"(-?\d+):(-?\d+)\((\w[\w -]*)\)@([\d.]+)(\[posT (-?[\d.]+),(-?[\d.]+),(-?[\d.]+) axisT (-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\])?", live):
        e = {"id": m.group(1), "angle": int(m.group(2)), "phase": m.group(3), "dist": float(m.group(4))}
        if m.group(5):
            e["pos"] = [float(m.group(i)) for i in (6, 7, 8)]
            e["axis"] = [float(m.group(i)) for i in (9, 10, 11)]
        d["live"].append(e)
    return d


def wait_air(maxd=2.6, mind=0.0, timeout=40.0, free=False):
    """A gnome in the air between mind and maxd m (free: and none clinging to the hero at the time)."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        if user_in_vr():
            return None, None
        d = g()
        if free and d["clinging"]:
            time.sleep(0.05)
            continue
        for e in d["live"]:
            if e["phase"] == "air" and mind <= e["dist"] <= maxd and "pos" in e:
                return d, e
        time.sleep(0.05)
    return None, None


def part_watch():
    d0 = g()
    t0 = time.time()
    maxair, gaps, begins, perp, notperp, last_b, last_t = 0, [], 0, 0, 0, d0["begun"], None
    seen, news = set(e["id"] for e in d0["live"]), []   # each new leap's (time seen, line)
    clingsamp, during = 0, 0
    while time.time() - t0 < 40:
        if user_in_vr():
            return
        d = g()
        air = sum(1 for e in d["live"] if e["phase"] == "air")
        maxair = max(maxair, air)
        if d["begun"] > last_b:
            now = time.time()
            if last_t is not None:
                gaps.append(now - last_t)
            last_t = now
            begins += d["begun"] - last_b
            last_b = d["begun"]
        for e in d["live"]:
            if e["id"] not in seen:
                seen.add(e["id"])
                if e["phase"] in ("air", "wind-up"):
                    news.append((time.time(), e["angle"]))
        opn = [e for e in d["live"] if e["phase"] in ("air", "wind-up")]
        if d["clinging"]:
            clingsamp += 1
            if opn:
                during += 1                # GN6: no leap while one clings
        if len(opn) >= 2:
            a, b = opn[0]["angle"] % 180, opn[1]["angle"] % 180
            if abs(a - b) % 180 == 90:
                perp += 1
            else:
                notperp += 1
        time.sleep(0.1)
    d1 = g()
    # a leap right after another (under 2 s apart as seen here; the mod's FollowOn is 2.5 s) has the perpendicular line
    follow = [(a[1], b[1]) for a, b in zip(news, news[1:]) if b[0] - a[0] < 2.0]
    fbad = [p for p in follow if abs(p[0] % 180 - p[1] % 180) % 180 != 90]
    ok = begins >= 2 and maxair <= 1 and (not gaps or min(gaps) >= 0.9) and notperp == 0 and not fbad and during == 0 and d1["slowFrames"] > d0["slowFrames"]
    result("watch", ok, "40 s: leaps begun %d, gated %d, most in the air at once %d, gaps min %s, two lines open %d samples (perpendicular %d, not %d), one right after another %d (not perpendicular %d%s), clinging samples %d (a leap open meanwhile %d), slow frames +%d | %s" % (
        begins, d1["gated"] - d0["gated"], maxair, ("%.2f" % min(gaps)) if gaps else "-", perp + notperp, perp, notperp, len(follow), len(fbad),
        (": " + str(fbad[:4])) if fbad else "", clingsamp, during, d1["slowFrames"] - d0["slowFrames"], d1["_raw"].split("| last")[-1][:120]))


def wait_dodgeable(timeout=30.0):
    """A gnome winding up (any distance) or in the air at least 0.9 m away, none clinging (the mod's dodge: a wind-up
    always, in the air while DodgeGap 0.6 m away)."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        if user_in_vr():
            return None, None
        d = g()
        if not d["clinging"]:
            for e in d["live"]:
                if e["phase"] == "wind-up" or (e["phase"] == "air" and e["dist"] >= 0.9):
                    return d, e
        time.sleep(0.05)
    return None, None


def part_cut():
    for attempt in range(6):
        if g()["clinging"]:
            ring_cuts()                    # no leap comes while one clings (GN6: the gate)
        sim("head 0 1.7 0 0 0 0; " + HAND_R)
        d0 = g()
        d, e = wait_air(2.6, 1.0, 15, free=True)
        if e is None:
            # (candidate 11.1's r1 N: the one leap in 30 s began 0.9 m out, under the 1.0 m this cut wants, and that gnome
            # clung for the rest of the wait - no leap comes while one clings, so the old single 30 s wait ended the part)
            if user_in_vr():
                break
            clung = g()["clinging"]
            if clung:
                ring_cuts()
            log("  cut: attempt %d - no gnome in the air 1.0-2.6 m in 15 s%s" % (attempt + 1, " (one clung: cut free)" if clung else ""))
            continue
        p, ax = e["pos"], e["axis"]
        # the hand 0.6 m short of the gnome's middle on the line from the eyes, the cut 1.0 m along its line (xrsim z = -z)
        hx, hy, hz = p[0], p[1], -p[2]
        dx, dy, dz = hx, hy - 1.7, hz
        n = (dx * dx + dy * dy + dz * dz) ** 0.5 or 1.0
        k = max(0.0, (n - 0.6) / n)
        cx, cy, cz = dx * k, 1.7 + dy * k, dz * k
        L = 1.0
        # the blade pointed at the gnome (G1: straight ahead it passed beside one off to the side)
        yaw = math.degrees(math.atan2(-dx, -dz))
        pitch = math.degrees(math.asin(max(-1.0, min(1.0, dy / n))))
        sim("hand r %.3f %.3f %.3f %.1f %.1f 0; move r %.3f %.3f %.3f 120" % (cx - ax[0] * L / 2, cy - ax[1] * L / 2, cz + ax[2] * L / 2, yaw, pitch,
                                                                         ax[0] * L, ax[1] * L, -ax[2] * L))
        time.sleep(0.5)
        d1 = g()
        if d1["knocks"] > d0["knocks"]:
            return result("cut", True, "a cut along a leaping gnome's line (%d deg, %.1f m): knocked | %s" % (e["angle"], e["dist"], d1["_raw"].split("| last")[-1][:140]))
        log("  cut: attempt %d (%d deg, %.1f m) - knocks +0, wrong +%d | %s" % (attempt + 1, e["angle"], e["dist"], d1["wrong"] - d0["wrong"], d1["_raw"].split("| live")[-1][:240]))
        time.sleep(1.0)
    result("cut", False, "no knock in 6 tries")


def ring_cuts(n=8):
    """Cuts through the ring while a gnome clings (the cling part's cuts); the cuts made."""
    d = g()
    rt = d["ringT"]
    hits = 0
    for i in range(n):
        d = g()
        if not d["clinging"] or rt is None:
            break
        rt = d["ringT"] or rt
        x, y, z = rt[0], rt[1], -rt[2]
        hz = z * 0.45                      # the hand 45 % of the way: the blade's middle crosses the ring's plane
        side = 1 if i % 2 == 0 else -1
        sim("hand r %.3f %.3f %.3f 0 0 0" % (x - 0.5 * side, y, hz))
        time.sleep(0.15)
        sim("move r %.3f 0 0 110" % (1.0 * side))
        time.sleep(0.45)
        hits += 1
    return hits


def part_dash():
    # the design: a clinging gnome holds the hero - no dash until the ring breaks (CombatMove refuses it: clingRefused).
    # G5's six tries pressed A with the hands down, where every landed gnome clung: each press met a clinging one. So
    # the press comes with none clinging (the ring broken first), a gnome in the air 1.2-6 m away.
    send("vrdash uyf")
    for attempt in range(6):
        if g()["clinging"]:
            ring_cuts()
        sim("head 0 1.7 0 0 0 0; " + DOWN)
        d0 = g()
        dash0 = nums_of(send("dash?"))
        d, e = wait_dodgeable(30)
        if e is None:
            break
        # sideways, to the side with the most floor (G5's last try: the floor ended behind the hero - a back dash failed)
        side = 1 if room(90) >= room(-90) else -1
        sim("stick l %d 0 0; btn r a 120" % side)
        time.sleep(0.25)
        sim("stick l 0 0 0")
        time.sleep(0.55)
        d1 = g()
        dash1 = send("dash?")
        if d1["dodged"] > d0["dodged"]:
            return result("dash", True, "A with a gnome %s %.1f m away, none clinging: it missed | %s | %s" % (
                "winding up" if e["phase"] == "wind-up" else "in the air", e["dist"], dash1.split("| last")[-1][:120], d1["_raw"].split("| last")[-1][:140]))
        n1 = nums_of(dash1)
        log("  dash: attempt %d - dodged +0, dashes +%d, cling refused +%d, busy refused +%d, clinging %s | %s | %s" % (
            attempt + 1, n1.get("dashes", 0) - dash0.get("dashes", 0), n1.get("clingRefused", 0) - dash0.get("clingRefused", 0),
            n1.get("busyRefused", 0) - dash0.get("busyRefused", 0), d1["clinging"], dash1.split("| last")[-1][:120], d1["_raw"].split("| last")[-1][:100]))
        time.sleep(1.5)
    result("dash", False, "no dodge in 6 tries")


def room(deg):
    m = re.search(r"free ([\d.]+)(\+?) m", send("dash? free %d" % deg, 4))
    return 99.0 if m and m.group(2) else (float(m.group(1)) if m else 0.0)


def nums_of(s):
    d = {}
    for k, v in re.findall(r"([A-Za-z]+) (-?[\d.]+)", s):
        if k not in d:
            try:
                d[k] = float(v)
            except ValueError:
                pass
    return d


def part_cling():
    sim("head 0 1.7 0 0 0 0; " + DOWN)
    d0 = g()
    t0 = time.time()
    d = d0
    while time.time() - t0 < 60 and not d["clinging"]:
        if user_in_vr():
            return
        time.sleep(0.2)
        d = g()
    if not d["clinging"]:
        return result("cling", False, "no gnome clung in 60 s | " + d["_raw"][:160])
    nd = lambda: int((re.search(r"dashes (\d+)", send("dash?")) or [0, 0])[1])
    n0 = nd()
    sim("btn r a 120")
    time.sleep(0.5)
    refused = nd() == n0
    rt = d["ringT"]
    hits = 0
    for i in range(8):
        d = g()
        if not d["clinging"] or rt is None:
            break
        rt = d["ringT"] or rt
        x, y, z = rt[0], rt[1], -rt[2]
        hz = z * 0.45                      # the hand 45 % of the way: the blade's middle crosses the ring's plane
        side = 1 if i % 2 == 0 else -1
        sim("hand r %.3f %.3f %.3f 0 0 0" % (x - 0.5 * side, y, hz))
        time.sleep(0.15)
        sim("move r %.3f 0 0 110" % (1.0 * side))
        time.sleep(0.45)
        hits += 1
    d1 = g()
    ok = d1["freed"] > d0["freed"] and not d1["clinging"] and d1["ringHits"] - d0["ringHits"] >= 5 and refused
    result("cling", ok, "clung (clings +%d); A while clinging: %s; %d cuts at the ring: ring hits +%d, freed +%d, clinging %s | %s" % (
        d1["clings"] - d0["clings"], "no dash" if refused else "A DASH", hits, d1["ringHits"] - d0["ringHits"], d1["freed"] - d0["freed"], d1["clinging"], d1["_raw"].split("| last")[-1][:140]))


def shot(name):
    r = subprocess.run(["python3", H + "/scripts/chains/pf/pics.py", "shot", name], capture_output=True, text=True)
    return (r.stdout or "").strip().splitlines()[-1:] or ["?"]


def part_pics():
    """Both-eye pictures: a gnome winding up with its line, the same one in the air (slow motion), the ring as a cling
    begins, the moment it breaks; after each, what is drawn within 0.6-0.8 m of the eyes (`eyenear`: GNP's ring pictures
    had broad green bands across the view - the game's snare effect on the hero's spine, hidden since)."""
    took = []
    sim("head 0 1.7 0 0 0 0; " + DOWN)
    t_end = time.time() + 150
    while time.time() < t_end and not ("windup" in took and any(t.startswith("air") for t in took)):
        if user_in_vr():
            return
        d = g()
        if d["clinging"]:
            ring_cuts()                    # no leap comes while one clings
            sim("head 0 1.7 0 0 0 0; " + DOWN)
            continue
        e = next((x for x in d["live"] if x["phase"] == "wind-up" and 1.0 <= x["dist"] <= 4.5), None)
        if e is None:
            time.sleep(0.05)
            continue
        send("gnomeface")
        time.sleep(0.1)
        if "windup" not in took:
            shot("f8_line_windup")
            took.append("windup")
            log("  pics: wind-up %s deg at %.1f m; near the eyes: %s" % (e["angle"], e["dist"], send("eyenear 0.6", 4)[:300]))
        # the same gnome in the air (the view kept on it)
        t1 = time.time()
        while time.time() - t1 < 4:
            d = g()
            f = next((x for x in d["live"] if x["id"] == e["id"]), None)
            if f is None or f["phase"] not in ("wind-up", "air"):
                break
            if f["phase"] == "air":
                send("gnomeface")
                shot("f8_leap_in_air_slowmo")
                ts = re.search(r"timescale ([\d.]+)", d["_raw"])
                took.append("air at %.1f m (timescale %s just before)" % (f["dist"], ts.group(1) if ts else "?"))
                log("  pics: in the air at %.1f m; near the eyes: %s" % (f["dist"], send("eyenear 0.6", 4)[:300]))
                break
            time.sleep(0.03)
    # the ring as a new cling begins
    if g()["clinging"]:
        ring_cuts()
    sim("head 0 1.7 0 0 0 0; " + DOWN)
    c0 = g()["clings"]
    t0 = time.time()
    while time.time() - t0 < 60:
        d = g()
        if d["clings"] > c0 and d["clinging"]:
            time.sleep(0.15)
            shot("f8_ring_clinging")
            took.append("ring")
            log("  pics: the ring; near the eyes: %s" % send("eyenear 0.8", 4)[:1500])
            # broken: the game's snare effect bursts at the hero's spine (GNP: green vine bands across the view; hidden now)
            ring_cuts()
            time.sleep(0.2)
            shot("f8_ring_broken")
            took.append("broken")
            fx = re.search(r"snareFxHidden (\d+)", send("pcombat?", 4))
            log("  pics: the ring broken (snare effects hidden %s); near the eyes: %s" % (fx.group(1) if fx else "?", send("eyenear 0.8", 4)[:1500]))
            break
        time.sleep(0.05)
    result("pics", len(took) == 4, "shots: " + ", ".join(took))


def part_airpic():
    """The in-the-air picture alone, the head pitched down toward the gnome as a player would look (GNP5: a leap from
    0.9 m flew below a level view)."""
    sim("head 0 1.7 0 0 0 0; " + DOWN)
    t_end = time.time() + 150
    while time.time() < t_end:
        if user_in_vr():
            return
        d = g()
        if d["clinging"]:
            ring_cuts()
            sim("head 0 1.7 0 0 0 0; " + DOWN)
            continue
        e = next((x for x in d["live"] if x["phase"] == "wind-up" and 1.0 <= x["dist"] <= 4.5), None)
        if e is None:
            time.sleep(0.05)
            continue
        send("gnomeface")
        t1 = time.time()
        while time.time() - t1 < 4:
            d = g()
            f = next((x for x in d["live"] if x["id"] == e["id"]), None)
            if f is None or f["phase"] not in ("wind-up", "air"):
                break
            if f["phase"] == "air" and "pos" in f:
                p = f["pos"]
                pitch = -math.degrees(math.atan2(1.7 - p[1], max(0.3, math.hypot(p[0], p[2]))))
                pitch = max(-40.0, min(0.0, pitch * 0.8))
                send("gnomeface")
                sim("head 0 1.7 0 0 %.1f 0" % pitch)
                shot("f8_leap_in_air_slowmo")
                ts = re.search(r"timescale ([\d.]+)", d["_raw"])
                log("  airpic: in the air at %.1f m, the head pitched %.0f deg; near the eyes: %s" % (f["dist"], pitch, send("eyenear 0.6", 4)[:300]))
                sim("head 0 1.7 0 0 0 0")
                return result("airpic", True, "in the air at %.1f m (timescale %s just before), head pitch %.0f" % (f["dist"], ts.group(1) if ts else "?", pitch))
            time.sleep(0.03)
    result("airpic", False, "no leap caught in the air")


PARTS = {"watch": part_watch, "cut": part_cut, "dash": part_dash, "cling": part_cling, "pics": part_pics, "airpic": part_airpic}


def main():
    parts = args[args.index("--parts") + 1].split(",") if "--parts" in args else ["watch", "cut", "dash", "cling"]
    if "--no-fixture" not in args:
        boot = ["--boot"] if "GameState_Map" not in send("state?") else []
        r = subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--jump", "EL_Ambush_Thral", "--with", "Card_MO_Gnome_Special*1"] + boot +
                           ["--god", "--physical", "--equip", "BasicSword_noToken,BasicShield"], capture_output=True, text=True)
        log("fixture gnomes: %s" % (((r.stdout or "").strip().splitlines() or ["?"])[-1]))
    if "Level_Play" not in send("state?"):
        result("fixture", False, "no fight: " + send("state?"))
        return 1
    send("god on")
    send("tough on")
    send("vrcombat physical")
    send("vrgnomes on")
    for p in parts:
        if user_in_vr():
            log("the user is in VR: stopping")
            break
        send("tough on")
        try:
            PARTS[p]()
        except Exception as ex:
            result(p, False, "error " + repr(ex))
    sim("head 0 1.7 0 0 0 0; " + HAND_R)
    if "--stay" not in args:
        subprocess.run(["python3", "-c", "import sys; sys.path.insert(0, %r); import indicator_shots; indicator_shots.leave()" % (H + "/scripts")])
    log("%s: %d/%d PASS" % (TAG, sum(OUT), len(OUT)))
    return 0 if OUT and all(OUT) else 1


if __name__ == "__main__":
    sys.exit(main())

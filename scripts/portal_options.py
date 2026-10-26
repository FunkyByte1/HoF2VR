#!/usr/bin/env python3
"""The exit portal's looks side by side (round 5 item 3e; the user 2026-10-12: "is there a way to make it more
portal/spherical-like? Or can we use a different asset entirely? Also, can we make it a radiant light source so you have
an idea where it is even when it spawns behind you?").

  portal_options.py [--start] [--arenas EL_Ambush_Thral,EL_Ambush_Empire] [--bigger]
  --bigger: only the 2 m Orb (2026-10-13) in each arena, as a section at the top of the page (the other rows stay)

In each arena: a one-enemy fight from the dungeon map, won at once (`killall`); when the portal opens the head turns to
it and it is shot in every look ([Comfort] PortalLook: Orb, Swirl, Rift - `exitcard look X` rebuilds it in place); then
the head turns away and looks at the ground (the portal's light round the player), with the light on and off; and 10 s of
the game's own perf numbers with the light on and off (its cost). captures/portal-options-2026-10-12/index.html.
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
OUT = os.path.join(H, "captures", "portal-options-2026-10-12")
SHOTS = os.path.join(hof2env.TESTDATA, "shots")
LOOKS = [("Orb", "the game's wormhole layers on nested spheres turning round a dark core"),
         ("Swirl", "the flat purple vortex (the previous look)"),
         ("Rift", "the vortex squeezed into an upright tear in the air"),
         ("Halo", "a different asset: the game's magic shield bubble (a companion's spell, PFX_malaclypse_shield) round a dark core")]
REST = "hand r 0.2 1.0 -0.3 0 -60 0; hand l -0.2 1.0 -0.3 0 -60 0"
LOG = H + "/logs/portal_options.log"


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG, "a") as f:
        f.write(line + "\n")


def send(c, t=10):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def state():
    return send("state?", 5)


def st():
    send("state")
    try:
        return cmd.status()
    except Exception:
        return {}


def touch_on():
    """Whatever the table waits for, by touch and by the plate's label (a numbered touch once landed on the map's Inventory
    plate when the page closed under it): an equip question No, a page's first option, a choice's Select, Continue; the
    inventory closed; an encounter's dice thrown."""
    s = st()
    b = s.get("boards", "").split(" | touch")[0]
    menu = s.get("menu")
    if "Dice" in (menu or ""):
        send("dice throw 0 0.5 2.0")
    elif menu == "Inventory":
        send("ptouch nav:Back")
    elif menu == "Choice" and "nav:Select" in b and not re.search(r"\[\d+ (?!nav:)", b):
        send("ptouch nav:Select")
    elif menu == "Choice" and not any(k in b for k in ("nav:Continue", "nav:Back", "nav:No", "nav:Select")):
        send("touch CardChoiceContainer/Card_ r tap")   # a choice with nothing chosen yet: a card touched
    elif menu not in ("Map", None, ""):
        if "nav:No" in b:
            send("ptouch nav:No")
        elif re.search(r"\[\d+ (?!nav:)", b):
            send("ptouch opt")
        elif "nav:Continue" in b:
            send("ptouch nav:Continue")


def to_fight(arena):
    send("fightwith ThievesThugREF*1")
    r = send("jump fight " + arena)
    log("jump %s: %s" % (arena, r))
    t0 = time.time()
    while time.time() - t0 < 120:
        s = state()
        if "Level_Play" in s:
            return True
        if not r.startswith("started"):
            # something left open on the map (an encounter's last page, an equip question): finished, then the jump again
            touch_on()
            time.sleep(2)
            send("fightwith ThievesThugREF*1")
            r = send("jump fight " + arena)
            if r.startswith("started"):
                log("jump %s: %s" % (arena, r))
                t0 = time.time()   # the fight's own two minutes from here
            continue
        if "GameState_Map" in s:
            touch_on()
        elif "Level_In" in s and "waiting" not in send("gate", 4):
            send("press A 6")
        time.sleep(2)
    return False


def perf_lines(n0):
    with open(hof2env.GLOG, errors="replace") as f:
        lines = f.read().splitlines()[n0:]
    out = []
    for l in lines:
        m = re.search(r"perf 10s .*avg ([\d.]+) .*main gap ([\d.]+) logic ([\d.]+) wait ([\d.]+) render ([\d.]+)", l)
        if m:
            out.append(tuple(float(x) for x in m.groups()))
    return out


def nlines():
    with open(hof2env.GLOG, errors="replace") as f:
        return sum(1 for _ in f)


def crop(name):
    from PIL import Image
    src = os.path.join(SHOTS, name + "_eyes.png")
    if not os.path.exists(src):
        return None
    im = Image.open(src)
    w, h = im.size
    im.crop((0, 0, w // 2, h)).save(os.path.join(OUT, name + ".png"))
    return name + ".png"


def main():
    a = sys.argv[1:]
    os.makedirs(OUT, exist_ok=True)
    open(LOG, "a").close()
    if "--start" in a:
        subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"], capture_output=True, text=True, timeout=900)
    arenas = a[a.index("--arenas") + 1].split(",") if "--arenas" in a else ["EL_Ambush_Thral", "EL_Ambush_Empire"]
    send("gate auto 1.5")
    if "--bigger" in a:
        bigger(arenas)
        return
    if "--fresh" in a:
        # a clean start whatever was left open: the dev save's fresh mid-run map (Scenarios "Fresh dev run"); the head
        # forward first (arriving recentres on the head's turn)
        sim(REST + "; head 0 1.75 0 0 0 0")
        time.sleep(0.5)
        send("cfg Scenarios true")
        log("fresh dev run: " + send("scen run Fresh_dev_run"))
        t0 = time.time()
        while time.time() - t0 < 240 and not ("GameState_Map" in state() and send("jump encounter zz_ready_probe").startswith("no encounter like")):
            time.sleep(2)
    sections = []
    for arena in arenas:
        sim(REST + "; head 0 1.75 0 0 0 0")
        if "GameState_Map" not in state():
            log("not on the map for %s: %s" % (arena, state()))
            break
        if not to_fight(arena):
            log("no fight in %s" % arena)
            continue
        send("god")
        time.sleep(2)
        send("killall")
        t0 = time.time()
        while time.time() - t0 < 25 and "card (" not in st().get("fightExit", ""):
            time.sleep(1)
        time.sleep(2)
        face = send("exitcard face")
        log("%s: %s | %s" % (arena, face, send("exitcard last")))
        m = re.search(r"turn (-?\d+) deg, ([\d.]+) m, pitch (-?\d+)", face)
        turn, dist, pitch = (float(m.group(1)), float(m.group(2)), float(m.group(3))) if m else (0.0, 5.0, 0.0)
        # from 2.5 m: close enough to see the shape (the hero is moved there; the portal stays)
        send("exitcard near 2.5")
        time.sleep(1.0)
        face = send("exitcard face")
        m = re.search(r"turn (-?\d+) deg, ([\d.]+) m, pitch (-?\d+)", face)
        if m:
            turn, pitch = float(m.group(1)), float(m.group(3))
        # aimed at the portal itself (the mod's head-aim seam: OpenXR yaw/pitch from where the eyes stand)
        h = send("headlook HoF2VR_ExitPortal from 0 1.75 0")
        log("%s: aim %s" % (arena, h))
        if h.startswith("head"):
            sim(h)
            m = re.search(r"head \S+ \S+ \S+ (\S+) (\S+)", h)
            if m:
                turn = float(m.group(1))   # xrsim yaw (left positive): the shots turned away use turn + 180
        else:
            sim("head 0 1.75 0 %.0f %.0f 0" % (turn, max(-30.0, min(15.0, pitch))))
        time.sleep(1.0)
        cells = []
        for look, desc in LOOKS:
            send("exitcard look " + look)
            time.sleep(2.2)
            tag = "PO_%s_%s" % (arena, look)
            send("shot " + tag)
            time.sleep(2.0)
            cells.append((crop(tag), "PortalLook = " + look, desc))
        send("exitcard look Orb")
        time.sleep(1.5)
        # the light round the player: turned away from the portal, looking down at the ground
        sim("head 0 1.75 0 %.0f -45 0" % (turn + 180))
        time.sleep(1.0)
        for on in ("on", "off"):
            send("exitcard light " + on)
            time.sleep(1.2)
            tag = "PO_%s_behind_light_%s" % (arena, on)
            send("shot " + tag)
            time.sleep(2.0)
            cells.append((crop(tag), "turned away, light " + on, "the ground round the player, the portal behind"))
        # its cost: the game's 10 s perf numbers with the light on and off (the simulator's GPU; the same scene)
        costs = {}
        for on in ("on", "off"):
            send("exitcard light " + on)
            n0 = nlines()
            time.sleep(21)
            p = perf_lines(n0)
            costs[on] = p[-1] if p else None
        send("exitcard light on")
        log("%s perf (avg, gap, logic, wait, render ms): light on %s, off %s" % (arena, costs.get("on"), costs.get("off")))
        sections.append((arena, dist, cells, costs))
        send("exitcard touch")
        t0 = time.time()
        # back at the table: the encounter's last page (its options / Continue) until the map takes jumps again
        while time.time() - t0 < 90:
            s = st()
            if "GameState_Map" in state() and s.get("menu") == "Map":
                break
            b = s.get("boards", "")
            m2 = re.findall(r"\[(\d+) ([^\]]*)\]", b)
            opt = next((i for i, t in m2 if not t.startswith("nav:")), None)
            if opt:
                send("ptouch %s tap" % opt)
            elif "nav:" in b:
                send("press A 6")
            time.sleep(2)
        time.sleep(3)
    sim(REST + "; head 0 1.75 0 0 0 0")
    page(sections)


def leave_portal():
    """Out through the portal and back to a dungeon map that takes jumps (the encounter's last page by touch)."""
    send("exitcard touch")
    t0 = time.time()
    while time.time() - t0 < 90:
        s = st()
        if "GameState_Map" in state() and s.get("menu") == "Map" and send("jump encounter zz_ready_probe").startswith("no encounter like"):
            return True
        touch_on()
        time.sleep(2)
    return False


def bigger(arenas):
    """The bigger Orb (the user, 2026-10-13: "Orb is great. Make it bigger."): 2 m across, its bottom just above the ground.
    In each arena: the placement probe (24 stand points), then the portal where it opened, and from 2.5 m as the 1 m Orb's
    row was shot (the same distance: the size side by side)."""
    send("cfg PortalLook Orb")
    rows = []
    for arena in arenas:
        sim(REST + "; head 0 1.75 0 0 0 0")
        if not to_fight(arena):
            log("no fight in %s" % arena)
            continue
        send("god")
        time.sleep(2)
        probe = send("exitcard probe 24", 40)
        log("%s probe: %s" % (arena, probe[:400]))
        send("killall")
        t0 = time.time()
        while time.time() - t0 < 25 and "card (" not in st().get("fightExit", ""):
            time.sleep(1)
        time.sleep(2)
        spawn = send("exitcard last")
        log("%s: %s" % (arena, spawn[:300]))
        cells = [("PO_%s_Orb.png" % arena, "before: the 1 m Orb, from 2.5 m", "12 October")]
        for tag, near in (("OrbBig", 2.5), ("OrbBig_spawn", None)):
            if near:
                send("exitcard near %.1f" % near)
                time.sleep(1.0)
            h = send("headlook HoF2VR_ExitPortal from 0 1.75 0")
            if h.startswith("head"):
                sim(h)
            time.sleep(1.2)
            name = "PO_%s_%s" % (arena, tag)
            send("shot " + name)
            time.sleep(2.0)
            cells.append((crop(name), "the 2 m Orb, " + ("from 2.5 m" if near else "from where you stood when it opened"), ""))
            if near:
                # back where the hero stood is not needed: the second shot is from the placement's own distance, so the hero
                # goes back out to it
                m = re.search(r"portal ([\d.]+) m away", spawn)
                send("exitcard near %s" % (m.group(1) if m else "5.5"))
                time.sleep(1.0)
        rows.append((arena, probe, spawn, cells))
        leave_portal()
        time.sleep(3)
    sim(REST + "; head 0 1.75 0 0 0 0")
    path = os.path.join(OUT, "index.html")
    html = open(path).read() if os.path.exists(path) else ""
    sec = ['<h2 id="bigger">The bigger Orb (13 October)</h2><p>Your note: "Orb is great. Make it bigger." The Orb is now 2 m across '
           '(it was 1 m), its bottom just above the ground, so it reads as a doorway you walk into. Its touch and walk-in zone, '
           'its light\'s reach (28 m), its ground glow and its hum grow with it. It opens 0.5 m further away than before, so its '
           'near edge is no closer to you than the old one\'s, and never inside anything solid.</p>']
    for arena, probe, spawn, cells in rows:
        figs = "".join('<figure><a href="%s"><img src="%s"></a><figcaption><b>%s</b><br>%s</figcaption></figure>' % (n, n, t, d) for n, t, d in cells if n)
        m = re.search(r"probe \S+: (.*?)(\[|$)", probe)
        sec.append("<h3>%s</h3><div class='row'>%s</div><p style='font-size:13px'>Placement check (24 places to stand): %s</p>" % (
            arena, figs, (m.group(1) if m else probe)[:300]))
    block = "\n".join(sec)
    if '<h2 id="bigger">' in html:
        html = re.sub(r'<h2 id="bigger">.*?(?=<h2>|<p style="margin-top:30px)', block + "\n", html, flags=re.S)
    elif "<h2>" in html:
        i = html.index("<h2>")
        html = html[:i] + block + "\n" + html[i:]
    else:
        html += block
    with open(path, "w") as f:
        f.write(html)
    log("page: %s (bigger Orb section)" % path)


def page(sections):
    rows = []
    for arena, dist, cells, costs in sections:
        figs = "".join('<figure><a href="%s"><img src="%s"></a><figcaption><b>%s</b><br>%s</figcaption></figure>' % (n, n, t, d) for n, t, d in cells if n)
        c = ""
        if costs.get("on") and costs.get("off"):
            c = "<p>Frame cost of the light (the game's own 10 s numbers, ms): render %.2f with the light vs %.2f without; main thread busy %.2f vs %.2f.</p>" % (
                costs["on"][4], costs["off"][4], costs["on"][1] + costs["on"][2], costs["off"][1] + costs["off"][2])
        rows.append("<h2>%s (the portal opened %.1f m away; the shots from 2.5 m)</h2><div class='row'>%s</div>%s" % (arena, dist, figs, c))
    html = """<!doctype html><html><head><meta charset="utf-8"><title>Exit portal looks (2026-10-12)</title>
<style>body{background:#120f16;color:#e6dcf0;font-family:Georgia,serif;margin:24px}h1{color:#c9a2ff}h2{color:#b38cf0;margin-top:28px}
.row{display:flex;gap:10px;flex-wrap:wrap}figure{margin:0;width:19%%;min-width:240px}img{width:100%%;border:1px solid #5a3f80}
figcaption{font-size:14px;margin-top:4px}code{color:#d8b8ff}</style></head><body>
<h1>The exit portal: four looks, and its light</h1>
<p>Your note (12 October): "is there a way to make it more portal/spherical-like? Or can we use a different asset
entirely? Also, can we make it a radiant light source so you have an idea where it is even when it spawns behind you?"
Orb, Swirl and Rift are made from the game's own wormhole (the swirl the Dealer pulls you into); Halo is a different
asset of the game, the magic shield bubble one of its companions casts. Nothing from other games.
The setting: <code>[Comfort] PortalLook</code> (also on the VR settings page, Comfort tab). Default in this build:
<code>Orb</code>. The portal now lights the ground around you in its colour (one light, no shadows, about 9 m) and hums.</p>
%s
<p style="margin-top:30px;font-size:13px">Made by scripts/portal_options.py in the simulator (Monado, Quest 2 field of view,
1.75 m eyes), %s. Left eye.</p></body></html>""" % ("\n".join(rows), time.strftime("%Y-%m-%d %H:%M"))
    with open(os.path.join(OUT, "index.html"), "w") as f:
        f.write(html)
    log("page: " + os.path.join(OUT, "index.html"))


if __name__ == "__main__":
    main()

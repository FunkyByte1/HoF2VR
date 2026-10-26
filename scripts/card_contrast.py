#!/usr/bin/env python3
"""Card readability options side by side (user 2026-10-12 evening: "All the cards are kind of hard to see and read...
Can we brighten them all and add more contrast to them? Take screenshots of some options for me.").

  card_contrast.py [--start]      (--start: a session from the magician_mid snapshot first)

At four spots - the board with visited tiles, a hand of cards (the inventory row), an encounter card close up (a weapon
dealt as a reward), the shop - the same view is shot once per [Table] CardLook option (Game, Bright, Lit, Marked,
Strong; the setting applies live). Then captures/card-contrast-2026-10-12/index.html: the left eye of each shot, side by
side per spot, labelled with the setting value.
"""
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H
OUT = os.path.join(H, "captures", "card-contrast-2026-10-12")
SHOTS = os.path.join(hof2env.TESTDATA, "shots")
OPTIONS = [("Game", "as the game draws them"),
           ("Bright", "all cards x1.25; a visited one x0.92"),
           ("Lit", "x1.15 / x0.9 and a small warm light over the table"),
           ("Marked", "x1.2; a visited card greyed cool instead of darkened"),
           ("Strong", "x1.45 / x1.0 and the light")]
REST = "hand r 0.2 1.0 -0.3 0 -60 0; hand l -0.2 1.0 -0.3 0 -60 0"


def send(c, t=10):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def sim(c):
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def state():
    return send("state?", 5)


def wait(pred, timeout):
    t0 = time.time()
    while time.time() - t0 < timeout:
        if pred():
            return True
        time.sleep(0.5)
    return False


def menu():
    send("state")
    try:
        return cmd.status().get("menu", "")
    except Exception:
        return ""


def shoot(spot):
    for opt, _ in OPTIONS:
        send("cfg CardLook " + opt)
        time.sleep(1.4)
        send("shot CC_%s_%s" % (spot, opt))
        time.sleep(2.0)
    send("cfg CardLook Game")


def crop(spot, opt):
    from PIL import Image
    src = os.path.join(SHOTS, "CC_%s_%s_eyes.png" % (spot, opt))
    if not os.path.exists(src):
        return None
    im = Image.open(src)
    w, h = im.size
    left = im.crop((0, 0, w // 2, h))
    name = "%s_%s.png" % (spot, opt)
    left.save(os.path.join(OUT, name))
    return name


def page(spots):
    rows = []
    for spot, title in spots:
        cells = []
        for opt, desc in OPTIONS:
            n = crop(spot, opt)
            if n is None:
                continue
            cells.append('<figure><a href="%s"><img src="%s"></a><figcaption><b>CardLook = %s</b><br>%s</figcaption></figure>' % (n, n, opt, desc))
        rows.append('<h2>%s</h2><div class="row">%s</div>' % (title, "".join(cells)))
    html = """<!doctype html><html><head><meta charset="utf-8"><title>Card readability options (2026-10-12)</title>
<style>body{background:#16120e;color:#e8dcc4;font-family:Georgia,serif;margin:24px}h1{color:#f0c879}h2{color:#d9b46a;margin-top:28px}
.row{display:flex;gap:10px;flex-wrap:wrap}figure{margin:0;width:19%%;min-width:240px}img{width:100%%;border:1px solid #6b5432}
figcaption{font-size:14px;margin-top:4px}code{color:#f0c879}</style></head><body>
<h1>Card readability: five options</h1>
<p>Your note (12 October, evening): "All the cards are kind of hard to see and read. Especially darkened ones like places
you already visited on the board." Each row is the same view with one setting changed: <code>[Table] CardLook</code> in the
settings file (the VR settings page gets it too). Left eye, as the headset shows it. Click a picture for full size.</p>
<p>Tell me which one (or a mix, e.g. "Bright, but visited like Marked"). The default in this build: <code>%s</code>.</p>
%s
<p style="margin-top:30px;font-size:13px">Made by scripts/card_contrast.py in the simulator (Monado, Quest 2 field of view,
1.75 m eyes), %s.</p></body></html>""" % (DEFAULT, "\n".join(rows), time.strftime("%Y-%m-%d %H:%M"))
    with open(os.path.join(OUT, "index.html"), "w") as f:
        f.write(html)


DEFAULT = "Strong"


def main():
    os.makedirs(OUT, exist_ok=True)
    if "--start" in sys.argv:
        subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"], capture_output=True, text=True, timeout=900)
    if "GameState_Map" not in state():
        raise SystemExit("not on the dungeon map: " + state())
    sim(REST)
    spots = []
    # 1. the board with visited tiles
    sim("head 0 1.75 0 0 -48 0")
    time.sleep(1.0)
    shoot("board")
    spots.append(("board", "The board, with places already visited"))
    # 2. a hand of cards: the inventory row
    sim("head 0 1.75 0 0 -20 0")
    send("press BACK 4")
    if wait(lambda: menu() == "Inventory", 8):
        time.sleep(1.5)
        shoot("hand")
        spots.append(("hand", "A hand of cards (the inventory)"))
        send("press B 6")
        wait(lambda: menu() == "Map", 8)
    # 3. an encounter card close up: a weapon dealt as a reward (the game asks whether to equip it)
    sim("head 0 1.75 0 0 -12 0")
    r = send("scen here Gain_a_weapon")
    if r.startswith("running") and wait(lambda: "nav:Yes" in (cmd.status().get("boards", "") if send("state") else ""), 15):
        time.sleep(1.5)
        shoot("closeup")
        spots.append(("closeup", "A card close up (a weapon dealt as a reward)"))
        send("press B 6")   # No: keep the old one
        time.sleep(3)
    # 4. the shop
    wait(lambda: "GameState_Map" in state() and menu() == "Map", 15)
    r = send("scen here Shop")
    if r.startswith("running") and wait(lambda: "Buy equipment" in (cmd.status().get("boards", "") if send("state") else ""), 20):
        b = cmd.status().get("boards", "")
        import re
        m = re.findall(r"\[(\d+) ([^\]]*)\]", b)
        idx = next((i for i, t in m if "buy equipment" in t.lower()), None)
        if idx:
            send("ptouch %s tap" % idx)
            if wait(lambda: menu() == "Shop", 10):
                sim("head 0 1.75 0 0 -20 0")
                time.sleep(2.0)
                shoot("shop")
                spots.append(("shop", "The shop"))
                send("press B 6")
    page(spots)
    print("page: " + os.path.join(OUT, "index.html") + " (%d spots)" % len(spots))


if __name__ == "__main__":
    main()

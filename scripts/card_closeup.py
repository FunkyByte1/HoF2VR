#!/usr/bin/env python3
"""Cards raised to the player for reading, before / after the reading light (the user 2026-10-13, CardLook = Strong: "On a
card close up, they are all in shadows or something. Please fix.").

  card_closeup.py [--start] [--only reward,life,shop,encounter,choice]

From the dungeon map of a test session (--start: session.py start --snapshot magician_mid --to table first), each moment
is set up from the Scenarios catalog (`scen here`) and shot with `[Table] ReadingLight` false (before) and true (after),
CardLook Strong both times: gear with its Yes / No (Gain a weapon), a gained card (Gain life: its zoom confirms itself, so
it is run once per setting), the shop's cards, an encounter card (Dialogue), a choice of three (Choose 1 of 3). The head
looks at the raised card. Status lines (cardlook: the reading light's cone, reach and the raised cards) in
logs/card_closeup.log; the page captures/card-closeup-2026-10-13/index.html (left eyes side by side).
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
OUT = os.path.join(H, "captures", "card-closeup-2026-10-13")
SHOTS = os.path.join(hof2env.TESTDATA, "shots")
LOG = H + "/logs/card_closeup.log"
REST = "hand r 0.2 1.0 -0.3 0 -60 0; hand l -0.2 1.0 -0.3 0 -60 0"
SETTINGS = [("before", "false"), ("after", "true")]


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
    for _ in range(5):
        try:
            return cmd.status()
        except Exception:
            time.sleep(0.2)
    return {}


def wait(pred, timeout):
    t0 = time.time()
    while time.time() - t0 < timeout:
        s = st()
        if pred(s):
            return s
        time.sleep(0.4)
    return None


def map_ready():
    return "GameState_Map" in state() and st().get("menu") == "Map" and send("jump encounter zz_ready_probe").startswith("no encounter like")


def finish(timeout=60):
    """Whatever is open answered by touch (No to an equip question, a page's first option, Continue, Leave in the shop)."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        if map_ready():
            return True
        s = st()
        b = s.get("boards", "").split(" | touch")[0]
        menu = s.get("menu")
        if menu == "Shop" or "Leave" in b:
            m = re.findall(r"\[(\d+) ([^\]]*)\]", b)
            leave = next((i for i, t in m if t.lower().startswith("leave")), None)
            send("ptouch %s tap" % leave if leave else "ptouch nav:Back")
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
        time.sleep(2)
    return False


def look_at(path_part):
    """The simulated head aimed at a card (the mod's head-aim seam), else ahead and a little down."""
    h = send("headlook %s from 0 1.75 0" % path_part)
    if h.startswith("head"):
        sim(h)
    else:
        sim("head 0 1.75 0 0 -15 0")
    time.sleep(0.8)
    return h


def shoot(moment, which):
    name = "CU_%s_%s" % (moment, which)
    log("  %s %s: %s | %s" % (moment, which, st().get("cardlook", "")[-150:], send("readlight")[:230]))
    send("shot " + name, 15)
    time.sleep(2.0)
    return name


def both(moment, aim):
    """The same view with the reading light off and on (it fades in a quarter second)."""
    names = []
    for which, val in SETTINGS:
        send("cfg ReadingLight " + val)
        time.sleep(0.9)
        if aim:
            look_at(aim)
        names.append(shoot(moment, which))
    send("cfg ReadingLight true")
    # calibration (--intensities a,b,c): the same view at other reading-light strengths (not on the page)
    if INTENSITIES:
        base = send("readlight")
        for v in INTENSITIES:
            send("readlight %s" % v)
            time.sleep(0.6)
            shoot(moment, "i" + v)
        m = re.search(r"intensity ([\d.]+)", base)
        send("readlight %s" % (m.group(1) if m else "1.1"))
    return names


INTENSITIES = []


def crop(name):
    from PIL import Image
    src = os.path.join(SHOTS, name + "_eyes.png")
    if not os.path.exists(src):
        return None
    im = Image.open(src)
    w, h = im.size
    im.crop((0, 0, w // 2, h)).save(os.path.join(OUT, name + ".png"))
    return name + ".png"


def m_reward():
    r = send("scen here Gain_a_weapon")
    if not r.startswith("running") or not wait(lambda s: "nav:Yes" in s.get("boards", "") or "nav:Equip" in s.get("boards", ""), 20):
        return None
    time.sleep(1.2)
    names = both("reward", "ZoomContainer")
    # what the game's 2D screen holds now (the comparison panels, a dark backdrop, the corner shapes): its widgets and texture
    log("  reward: widgets %s; %s" % (send("widgets 300"), send("rtshot CU_reward_rt")))
    return names


def m_life():
    """The gained card shows for about two seconds before it flies to the health card: the head looks where the zoom's
    centre spot is before the scenario starts, and the shot is taken as soon as the card is there."""
    names = []
    for which, val in SETTINGS:
        send("cfg ReadingLight " + val)
        if not map_ready():
            finish(40)
        sim(REST + "; head 0 1.75 0 0 -8 0")
        time.sleep(0.6)
        r = send("scen here Gain_life")
        t0 = time.time()
        h = "none"
        while r.startswith("running") and time.time() - t0 < 8:
            h = send("headlook CardZoom/ZoomContainer from 0 1.75 0", 3)
            if h.startswith("head"):
                break
            time.sleep(0.1)
        if h.startswith("head"):
            sim(h)
        time.sleep(0.35)   # the card arrives at its spot (and the reading light fades in)
        names.append(shoot("life", which))
        finish(40)
    send("cfg ReadingLight true")
    return names


def m_shop():
    r = send("scen here Shop")
    if not r.startswith("running") or not wait(lambda s: "Buy equipment" in s.get("boards", ""), 25):
        return None
    b = st().get("boards", "")
    m = re.findall(r"\[(\d+) ([^\]]*)\]", b)
    idx = next((i for i, t in m if "buy equipment" in t.lower()), None)
    if not idx:
        return None
    send("ptouch %s tap" % idx)
    if not wait(lambda s: s.get("menu") == "Shop", 12):
        return None
    time.sleep(2.0)
    sim("head 0 1.75 0 0 -20 0")
    return both("shop", None)


def m_encounter():
    r = send("scen here Dialogue")
    if not r.startswith("running") or not wait(lambda s: s.get("menu") == "Encounter" and "[1 " in s.get("boards", ""), 25):
        return None
    time.sleep(1.0)
    return both("encounter", "EncounterContainer")


def m_choice():
    r = send("scen here Choose_1_of_3")
    if not r.startswith("running") or not wait(lambda s: s.get("menu") == "Choice", 25):
        return None
    time.sleep(2.0)
    return both("choice", "CardChoiceContainer")


MOMENTS = [("reward", "Gear dealt as a reward, with its question (Gain a weapon: Yes / No)", m_reward),
           ("life", "A gained card (Gain life: the game shows it, then it goes to your health card)", m_life),
           ("shop", "The shop's cards", m_shop),
           ("encounter", "An encounter card (a story with choices)", m_encounter),
           ("choice", "Choosing one card of three", m_choice)]


def page(rows):
    html = ['<!doctype html><html><head><meta charset="utf-8"><title>Cards close up (2026-10-13)</title>',
            '<style>body{background:#16120e;color:#efe3cf;font-family:Georgia,serif;margin:24px}h1{color:#e8c37a}'
            'h2{color:#d9b062;margin-top:26px}.row{display:flex;gap:12px;flex-wrap:wrap}figure{margin:0;width:45%;min-width:300px}'
            'img{width:100%;border:1px solid #6b5332}figcaption{font-size:14px;margin-top:4px}code{color:#f0d090}</style></head><body>',
            '<h1>Cards close up: before and after</h1>',
            '<p>Your note on the card page: "On a card close up, they are all in shadows or something. Please fix." A card the '
            'game raises to you to be read now gets a soft light from your side that travels with it (<code>[Table] '
            'ReadingLight</code>, on); the board, your hand and the shop keep the Strong look you picked. Left: before, '
            'right: after. Same moment, same view.</p>']
    for moment, title, names in rows:
        figs = "".join('<figure><a href="%s"><img src="%s"></a><figcaption>%s</figcaption></figure>' % (n, n, w)
                       for n, w in zip(names, ("before", "after")) if n)
        html.append("<h2>%s</h2><div class='row'>%s</div>" % (title, figs))
    html.append('<p style="margin-top:30px;font-size:13px">Made by scripts/card_closeup.py in the simulator (Monado, Quest 2 '
                'field of view, 1.75 m eyes), %s. Left eye.</p></body></html>' % time.strftime("%Y-%m-%d %H:%M"))
    with open(os.path.join(OUT, "index.html"), "w") as f:
        f.write("\n".join(html))


def main():
    a = sys.argv[1:]
    os.makedirs(OUT, exist_ok=True)
    open(LOG, "a").close()
    log("==== card close-up")
    if "--start" in a:
        subprocess.run(["python3", H + "/scripts/session.py", "start", "--snapshot", "magician_mid", "--to", "table"], capture_output=True, text=True, timeout=7200)
    only = a[a.index("--only") + 1].split(",") if "--only" in a else None
    if "--intensities" in a:
        INTENSITIES.extend(a[a.index("--intensities") + 1].split(","))
    send("cfg Scenarios true")
    send("cfg CardLook Strong")
    send("gate auto 1.5")
    sim(REST + "; head 0 1.75 0 0 -15 0")
    if not map_ready():
        finish(60)
    rows = []
    for moment, title, fn in MOMENTS:
        if only and moment not in only:
            continue
        sim(REST + "; head 0 1.75 0 0 -15 0")
        try:
            names = fn()
        except Exception as e:  # noqa: BLE001
            log("%s: error %s" % (moment, e))
            names = None
        if names:
            rows.append((moment, title, [crop(n) for n in names]))
            log("%s: shot %s" % (moment, " ".join(names)))
        else:
            log("%s: not reached (%s / %s)" % (moment, state(), st().get("menu")))
        finish(60)
    send("cfg ReadingLight true")
    # the page shows every moment shot so far (a pass with --only replaces just its own)
    done = {m for m, _, _ in rows}
    for moment, title, _ in MOMENTS:
        if moment not in done and all(os.path.exists(os.path.join(OUT, "CU_%s_%s.png" % (moment, w))) for w, _ in SETTINGS):
            rows.append((moment, title, ["CU_%s_%s.png" % (moment, w) for w, _ in SETTINGS]))
    order = [m for m, _, _ in MOMENTS]
    rows.sort(key=lambda r: order.index(r[0]))
    page(rows)
    log("page: %s (%d moments)" % (os.path.join(OUT, "index.html"), len(rows)))


if __name__ == "__main__":
    main()

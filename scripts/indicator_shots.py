#!/usr/bin/env python3
"""The combat indicator looks side by side (the user, 2026-10-13: "Can you show me some screenshots for different aesthetics
options for the combat indicators?"): `[Combat] IndicatorLook` = Pill, Arc, Ghost, Gilded, Ring, Bracket.

  indicator_shots.py --arena NAME [--page]      (in a live fight of this environment; Physical mode)
  indicator_shots.py --page                      (only rebuild the page from the shots taken)

For each look, the same moments through the `premo` seam (a still marker 0.45 m ahead, 0.12 m below the eyes, its line
60 deg from level; the fight frozen with timescale 0, the hands down): early warning (fill 0.15), halfway (0.5), the blow
blocked (0.06 s into the block's flash), a heavy blow with IndicatorByType (orange, halfway), and an unblockable blow's
dodge warning (the arrow, half lit). Shots of both eyes -> testdata/shots/IND_<arena>_<look>_<moment>_eyes.png; the left
eye's middle cropped -> the main worktree's HandOfFate2/captures/combat-indicators-2026-10-13/<arena>_<look>_<moment>.png;
a check that the marker shows in both eyes (pixels changed against the same view without it). --page writes index.html
there: one row per look, the moments across, each arena in its own table.
"""
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H
OUT = "/home/bara/Projects/flat2vr/HandOfFate2/captures/combat-indicators-2026-10-13"
LOOKS = ["Pill", "Arc", "Ghost", "Gilded", "Ring", "Bracket"]
MOMENTS = [("early", "bar 0.15 live 1 60 0 -0.12", "Early warning"),
           ("half", "bar 0.5 live 1 60 0 -0.12", "Halfway"),
           ("blocked", "bar 1 ok 0.06 60 0 -0.12", "Blocked (the flash)"),
           ("heavy", "bar 0.5 live 1 60 0 -0.12 heavy", "Heavy blow, by type"),
           ("dodge", "arrow 0.5 0.3 live left", "Dodge warning")]
CRITS = [("critbreak", "crit 0.85 break live 0.08 -135", "Guard broken: the first slash"),
         ("critcombo", "crit 0.3 combo live 0.08 -45", "Next slash, time running out"),
         ("critok", "crit 0 combo ok 0.08 180", "Slash landed"),
         ("guard", None, "The guard bar, chipped"),
         ("guardbroken", None, "The guard bar, broken")]
DESC = {
    "Pill": "The glowing pill filling from the middle (today's marker, the baseline).",
    "Arc": "A crescent bowed toward where the blade comes from, filling along its curve, speed streaks behind it.",
    "Ghost": "A see-through copy of the enemy's own weapon where it will pass, solidifying as the blow nears; brackets mark the block spot.",
    "Gilded": "Hand of Fate's card gilding: a gold double line with diamond end caps, molten gold filling, embers rising.",
    "Ring": "A ring closing onto the block spot (the timing at a glance) around a short line across the blow.",
    "Bracket": "Quiet: two corner brackets and a thin dashed line that turns solid as the blow nears.",
}
args = sys.argv[1:]


def send(c, t=8):
    try:
        return cmd.send(c, t)
    except SystemExit as e:
        return "ERR " + str(e)


def sim(c):
    import subprocess
    subprocess.run(["python3", H + "/scripts/xrsim.py", c], capture_output=True, text=True)


def shot(name):
    p = H + "/testdata/shots/%s_eyes.png" % name
    try:
        os.remove(p)
    except OSError:
        pass
    send("shot " + name, 20)
    t0 = time.time()
    while time.time() - t0 < 5.0 and not os.path.exists(p):
        time.sleep(0.1)
    time.sleep(0.2)
    return p if os.path.exists(p) else None


def crop_left(src, dst):
    from PIL import Image
    im = Image.open(src).convert("RGB")
    w, h = im.size[0] // 2, im.size[1]
    # the whole left eye (the marker 0.45 m ahead is ~70 deg long: it fills the view, as in the headset)
    box = (int(w * 0.02), int(h * 0.03), int(w * 0.98), int(h * 0.97))
    im.crop(box).save(dst)


def eyes_differ(a, b):
    """Changed pixels (left, right eye) between a shot with the marker and the same view without it."""
    from PIL import Image, ImageChops
    ia, ib = Image.open(a).convert("L"), Image.open(b).convert("L")
    w = ia.size[0] // 2
    out = []
    for x0 in (0, w):
        d = ImageChops.difference(ia.crop((x0, 0, x0 + w, ia.size[1])), ib.crop((x0, 0, x0 + w, ib.size[1])))
        out.append(sum(1 for v in d.getdata() if v > 24))
    return out


def take(arena):
    os.makedirs(OUT, exist_ok=True)
    if "Level_Play" not in send("state?"):
        sys.exit("not in a fight: " + send("state?"))
    send("vrcombat physical")
    send("god on")
    send("tough on")
    sim("head 0 1.7 0 0 0 0; hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0")
    time.sleep(0.5)
    # freeze between blows: no live marker of a real strike in the shots
    t0 = time.time()
    while time.time() - t0 < 20 and not send("strike?").startswith("none"):
        time.sleep(0.1)
    send("timescale 0")
    send("premo off")
    time.sleep(0.4)
    base = shot("IND_%s_base" % arena)
    meta = {"arena": arena, "shots": {}, "eyes": {}}
    for look in LOOKS:
        send("vrlook " + look.lower())
        for key, spec, _ in MOMENTS + CRITS:
            if spec is None:
                continue
            send("vrbytype " + ("on" if key == "heavy" else "off"))
            send("premo " + spec)
            time.sleep(0.35)
            name = "IND_%s_%s_%s" % (arena, look, key)
            p = shot(name)
            if p:
                dst = os.path.join(OUT, "%s_%s_%s.png" % (arena, look, key))
                crop_left(p, dst)
                meta["shots"]["%s/%s" % (look, key)] = os.path.basename(dst)
                if base:
                    meta["eyes"]["%s/%s" % (look, key)] = eyes_differ(p, base)
            print(look, key, "->", p, meta["eyes"].get("%s/%s" % (look, key)), flush=True)
    send("premo off")
    send("vrbytype setting")
    # the guard bar on the nearest enemy (its meter 4x: the pips are 2.2 cm), chipped and broken, in each look: 1.8 m in
    # front of it, facing it
    send("timescale 1")
    send("heroat nearest 0 1.8")
    send("face nearest")
    sim("head 0 1.7 0 0 0 0; hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0")
    time.sleep(0.4)
    send("timescale 0")
    send("enemybars zoom 4")
    for look in LOOKS:
        send("vrlook " + look.lower())
        send("poiseset nearest -1")
        send("poiseset nearest 20")
        time.sleep(0.5)
        for key, guard in (("guard", None), ("guardbroken", "0")):
            if guard is not None:
                send("poiseset nearest " + guard)
                time.sleep(0.6)
            p = shot("IND_%s_%s_%s" % (arena, look, key))
            if p:
                dst = os.path.join(OUT, "%s_%s_%s.png" % (arena, look, key))
                crop_left(p, dst)
                meta["shots"]["%s/%s" % (look, key)] = os.path.basename(dst)
        send("poiseset nearest -1")
    send("enemybars zoom 1")
    send("vrlook setting")
    send("timescale 1")
    json.dump(meta, open(os.path.join(OUT, "%s.json" % arena), "w"), indent=1)
    # both eyes: every marker changed pixels in each eye
    bad = [k for k, v in meta["eyes"].items() if min(v) < 200]
    print("both eyes: %d shots, %d with fewer than 200 changed pixels in an eye: %s" % (len(meta["eyes"]), len(bad), bad), flush=True)


DARK_CANDIDATES = ["EL_Ambush_Undead", "EL_Ambush_Corrupt", "EL_Ambush_Mage", "EL_Ambush_Treants"]


def luminance(p):
    from PIL import Image, ImageStat
    im = Image.open(p).convert("L")
    w, h = im.size
    return ImageStat.Stat(im.crop((0, 0, w // 2, h))).mean[0]


def leave():
    """End the fight, walk out (hold A at the exit portal), press A through the encounter card, its reward choice and loot
    until the map's own menu is up. The dev run's encounter can go on into another fight (its campfire, Level_In: "Into
    battle - Last Stand", S2T5/S2T6/S2T7): that fight is started and won at once (`jump win`) and the walk out goes on, up to
    three times. Every step is logged to logs/indicator_leave.log."""
    def note(m):
        with open(H + "/logs/indicator_leave.log", "a") as f:
            f.write(time.strftime("%H:%M:%S ") + m + "\n")
    send("tough off")
    send("timescale 1")
    send("premo off")
    send("god on")
    send("killall")
    time.sleep(3)
    strays = 0
    seen_menus = set()
    t1 = time.time()
    while time.time() - t1 < 240:
        try:
            menu = cmd.status().get("menu") or ""
        except Exception:  # noqa: BLE001
            menu = ""
        st = send("state?", 4)
        if "GameState_Map" in st and menu in ("Map", ""):
            time.sleep(1.0)
            try:
                menu = cmd.status().get("menu") or ""
            except Exception:  # noqa: BLE001
                menu = ""
            if menu in ("Map", ""):
                note("on the map (" + st[:40] + ")")
                return True
        if "Level_Play" in st:
            f = send("fight?", 4)
            m = re.search(r"alive (\d+)", f)
            if m and int(m.group(1)) > 0:
                note("in the fight, %s alive: killall" % m.group(1))
                send("tough off")
                send("killall")
                time.sleep(2)
                continue
            note("in the fight, none alive: hold A (the exit portal)")
            sim("btn r a 2500")
            time.sleep(3)
            continue
        if "Level_In" in st:
            strays += 1
            if strays > 3:
                note("an encounter's campfire again (" + menu + "): giving up")
                return False
            note("an encounter's campfire (%s, %s): start it and win it (%d)" % (st[:40], menu, strays))
            send("gate auto 0")
            sim("head 0 1.75 0 0 0 0; hand r 0.25 1.0 -0.25 0 -60 0; hand l -0.25 1.0 -0.25 0 -60 0")
            t2 = time.time()
            while time.time() - t2 < 30 and "Level_Play" not in send("state?", 4):
                sim("btn r a 150")
                time.sleep(1.5)
            time.sleep(1.0)
            note("jump win: " + send("jump win", 5))
            time.sleep(2.0)
            continue
        if "ChallengeMap" in st or "StartScreen" in st:
            note("out of the dungeon (" + st[:40] + ", " + menu + "): giving up (the next fixture boots)")
            return False
        if menu and menu not in seen_menus and len(seen_menus) < 8:
            # each menu of the walk out once on a shot (what the encounter asks: logs/indicator_leave.log names it)
            seen_menus.add(menu)
            send("shot LEAVE_%s_%s" % (time.strftime("%H%M%S"), re.sub(r"[^A-Za-z0-9]", "", menu)), 15)
            time.sleep(1.0)
        note("press A: " + st[:40] + " menu " + menu)
        send("press A 6")
        time.sleep(2.0)
    return False


def fight_in(arena):
    import subprocess
    subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--jump", arena, "--with", "ThievesThugREF*2", "--physical", "--god"],
                   capture_output=True, text=True, timeout=600)
    return "Level_Play" in send("state?")


def find_dark():
    """A fight in the darkest of DARK_CANDIDATES (the first whose left eye averages under 90 of 255, else the darkest);
    left running for take("dark")."""
    seen = []
    for arena in DARK_CANDIDATES:
        if not fight_in(arena):
            print(arena, "no fight:", send("state?"), flush=True)
            leave()
            continue
        send("tough on")
        sim("head 0 1.7 0 0 0 0; hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0")
        time.sleep(1.5)
        p = shot("IND_lum_" + arena)
        lum = luminance(p) if p else 999.0
        print(arena, "left-eye luminance %.0f" % lum, flush=True)
        if lum < 90:
            return arena
        seen.append((lum, arena))
        leave()
    if not seen:
        return None
    lum, arena = min(seen)
    return arena if fight_in(arena) else None


def page():
    arenas = []
    for f in sorted(os.listdir(OUT)):
        if f.endswith(".json"):
            arenas.append(json.load(open(os.path.join(OUT, f))))
    h = ["<!doctype html><html><head><meta charset='utf-8'><title>Combat indicator looks (2026-10-13)</title>",
         "<style>body{background:#16120e;color:#eadfc8;font-family:Georgia,serif;margin:24px}h1{color:#f2c46b}h2{color:#e6b15a;margin-top:36px}",
         "table{border-collapse:collapse}td,th{padding:6px;vertical-align:top}th{color:#d8b979;font-weight:normal;text-align:left}",
         "img{width:260px;border:1px solid #4a3a26;border-radius:4px}.look{width:220px}.look b{color:#f2c46b;font-size:1.15em}",
         ".note{color:#b8a888;font-size:0.9em}</style></head><body>",
         "<h1>Combat indicators: six looks</h1>",
         "<p>The same moments for each look, seen from the left eye (both eyes checked). The marker keeps its size and place "
         "(0.6 m across the blow, between you and the attacker); only its look changes. Setting: <code>[Combat] IndicatorLook = "
         "Pill | Arc | Ghost | Gilded | Ring | Bracket</code>; any look can also colour blows by type "
         "(<code>IndicatorByType = true</code>: normal in the look's colour, heavy orange, unblockable red).</p>"]
    for ar in arenas:
        base = H + "/testdata/shots/IND_%s_base_eyes.png" % ar["arena"]
        lum = ""
        if os.path.exists(base):
            try:
                lum = " <span class='note'>(the left eye's mean brightness %.0f of 255)</span>" % luminance(base)
            except Exception:  # noqa: BLE001
                pass
        h.append("<h2>%s arena%s</h2><table>" % (ar["arena"].capitalize(), lum))
        for look in LOOKS:
            for row, moments in (("block", MOMENTS), ("crit", CRITS)):
                h.append("<tr><th></th>%s</tr>" % "".join("<th>%s</th>" % m[2] for m in moments))
                cells = []
                for key, _, _ in moments:
                    f = ar["shots"].get("%s/%s" % (look, key))
                    cells.append("<td>%s</td>" % ("<a href='%s'><img src='%s'></a>" % (f, f) if f else "-"))
                label = ("<b>%s</b><br><span class='note'>%s</span>" % (look, DESC[look])) if row == "block" else "<span class='note'>%s: the slashes, the guard bar</span>" % look
                h.append("<tr><td class='look'>%s</td>%s</tr>" % (label, "".join(cells)))
        h.append("</table>")
    h.append("<p class='note'>Click a picture for its full size. Made by scripts/indicator_shots.py in the simulator (env 2): the fight "
             "frozen, a still marker 0.45 m ahead (the `premo` seam), the left eye's whole view (the right eye checked too: every "
             "marker shows in both). The guard bars: 1.8 m in front of the enemy, its meters drawn 4x (`enemybars zoom 4`; in play "
             "they are a quarter of that). Ghost uses the nearest enemy's own weapon mesh.</p></body></html>")
    open(os.path.join(OUT, "index.html"), "w").write("\n".join(h))
    print("page:", os.path.join(OUT, "index.html"))


if __name__ == "__main__":
    if "--find-dark" in args:
        a = find_dark()
        print("dark arena:", a, flush=True)
        if a:
            take("dark")
            leave()
    if "--arena" in args:
        take(args[args.index("--arena") + 1])
    if "--page" in args:
        page()

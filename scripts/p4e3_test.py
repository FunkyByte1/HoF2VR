#!/usr/bin/env python3
"""Playtest 4, env 3's items (2026-10-19; decisions.md "playtest 4 of candidate 10"; NOTES "Playtest 4 crit and gnome fixes
(env 3, 2026-10-19)"): the crit arrows (P4-9) and the gnome fight (P4-7/P4-8).

  p4e3_test.py [--parts crit,angles,...] [--tag T] [--no-fixture] [--stay] [--pics]

Parts (each in its own fixture fight unless --no-fixture):
  crit     skeletons (EL_Ambush_Undead, SkeletonNEWREF*2), god + tough: a guard-broken skeleton's crit cut by a scripted swing
           (1.0 m in 150 ms) from several stands - straight on 1.3 m, close 0.8 m (looking down at the marker), far 2.3 m
           (the blade never reaches the body: the line only), the body turned 45 deg right / 45 deg left of the line of
           sight - each swing following the arrow as a player swings it: the arrow's way upright plus a part along the BODY's
           forward (a chop goes down and forward). Each stand: the cut that follows the arrow lands (crits +1), then the same
           cut reversed on the next crit is wrong (wrong +1, crits +0).
  critpics pictures only (--pics): four of crit's stands, the arrow before the cut and the blade in the middle of a cut
           that follows it (captures/p4e3/p4e3_crit_<n>_<stand>_marker|_cut.jpg); the outcome logged
  angles   the same skeleton, straight on: a cut 40 deg off the arrow lands (the leeway is 50), one 75 deg off is wrong
  posts    gnomes (EL_Ambush_Thral + Card_MO_Gnome_Special): after 12 s, 10 s of `gnomeposts?`: the waiting gnomes spread
           round the hero (the widest gap between them under 160 deg, at most half within 45 deg of the gaze), 1.6-3.4 m
           off, facing him (within 60 deg) when at their posts; the posts are dealt again and walked to (moves, arrivals)
  ground   a waiting gnome on the ground: a scripted swing through its body hits it (hits +1, groundHits +1)
  cling    a gnome clings (hands down): it is shown in front (its body's middle 0.6-1.2 m from the eyes, within 35 deg of
           the gaze), nothing of it within 0.45 m of the eyes, the ring round its body; five cuts through the ring free the
           hero and the gnome is drawn where the game keeps it again
Pictures (--pics): both eyes into captures/p4e3/ (posts ahead / left / right, the clinging gnome and its ring, a crit
marker seen from close by).
Results: PASS/FAIL lines on stdout; exit 1 on any FAIL.
"""
import math
import os
import re
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmd  # noqa: E402
import hof2env  # noqa: E402

H = hof2env.H
args = sys.argv[1:]
TAG = args[args.index("--tag") + 1] if "--tag" in args else "P4E3"
PICS = "--pics" in args
OUT = []
HEAD = "head 0 1.7 0 0 0 0"
HAND_R = "hand r 0.25 1.35 -0.35 0 5 0"
DOWN = "hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0"
os.environ.setdefault("PICS_OUT", H + "/captures/p4e3")


def log(m):
    print(time.strftime("%H:%M:%S") + " " + m, flush=True)


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


def shot(name):
    if not PICS:
        return
    r = subprocess.run(["python3", H + "/scripts/chains/pf/pics.py", "shot", name], capture_output=True, text=True)
    log("  picture %s: %s" % (name, ((r.stdout or r.stderr or "").strip().splitlines() or ["?"])[-1][:120]))


def user_in_vr():
    try:
        return '"user": true' in open("/run/user/1000/vr-session/status.json").read()
    except OSError:
        return True


def counts():
    s = send("pcombat?")
    d = {"_raw": s}
    for k, v in re.findall(r"([A-Za-z]+) (-?[\d.]+)", s):
        if k not in d:
            try:
                d[k] = float(v)
            except ValueError:
                pass
    return d


def v3(m, i):
    return [float(m.group(i)), float(m.group(i + 1)), float(m.group(i + 2))]


def poise(who="nearest"):
    p = send("poise? " + who)
    d = {"raw": p}
    m = re.search(r"guard ([\d.]+)/(\d+) phase (\w+) step (\d+) crit (\S+)", p)
    if m:
        d.update(guard=float(m.group(1)), phase=m.group(3), step=int(m.group(4)), kind=m.group(5))
    m = re.search(r"crit \S+ (-?\d+) left (-?[\d.]+)", p)
    if m:
        d["line"], d["left"] = int(m.group(1)), float(m.group(2))
    m = re.search(r"axisT (-?[\d.]+),(-?[\d.]+),(-?[\d.]+)", p)
    if m:
        d["axis"] = v3(m, 1)
    m = re.search(r"atT \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", p)
    if m:
        d["at"] = v3(m, 1)
    m = re.search(r"id (#-?\d+)", p)
    if m:
        d["id"] = m.group(1)
    return d


# ---- vector helpers (Unity tracking space: +z ahead; xrsim's z is the opposite)
def add(a, b): return [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
def sub(a, b): return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
def mul(a, k): return [a[0] * k, a[1] * k, a[2] * k]
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def norm(a):
    n = math.sqrt(dot(a, a)) or 1.0
    return [a[0] / n, a[1] / n, a[2] / n]
def cross(a, b): return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]
def xr(a): return [a[0], a[1], -a[2]]


def upright_axis(axis, eye, at):
    """The drawn arrow (in the plane facing the eyes) as a player reads it: its sideways part on the horizontal right, its
    up/down part on the world's up (Poise.CutAngle's own reading)."""
    v = norm(sub(at, eye))
    f = norm([v[0], 0.0, v[2]])
    right = cross([0, 1, 0], f)
    up_drawn = norm(cross(v, right))
    return norm(add(mul(right, dot(axis, right)), mul([0, 1, 0], dot(axis, up_drawn))))


def rot_up(a, deg):
    """a turned deg about the world's up (Unity: +deg turns +z toward +x)."""
    r = math.radians(deg)
    c, s = math.cos(r), math.sin(r)
    return [a[0] * c + a[2] * s, a[1], -a[0] * s + a[2] * c]


def rot_in_plane(a, n, deg):
    """a turned deg about n (Rodrigues)."""
    r = math.radians(deg)
    n = norm(n)
    return add(add(mul(a, math.cos(r)), mul(cross(n, a), math.sin(r))), mul(n, dot(n, a) * (1 - math.cos(r))))


def swing(start, d, L=1.0, ms=150, aim=None, blade=None):
    """The right hand from `start` along d for L m in ms (tracking space), the blade pointed at `aim` or along `blade` (else
    ahead). The hand gets to the start by two jumps over 1 m (the hit tracking takes a jump over 0.5 m for a tracking jump:
    no swing - a smaller one was a fast swing up through the line, a wrong cut before the real one)."""
    yaw = pitch = 0.0
    a = blade if blade is not None else (sub(aim, start) if aim is not None else None)
    if a is not None:
        yaw = math.degrees(math.atan2(a[0], a[2]))           # xrsim yaw: the blade's turn right of ahead
        pitch = math.degrees(math.atan2(a[1], math.hypot(a[0], a[2])))
    far = xr(add(start, [0.0, 1.3, 0.0]))
    s = xr(start)
    sim("hand r %.3f %.3f %.3f %.1f %.1f 0" % (far[0], far[1], far[2], -yaw, pitch))
    time.sleep(0.15)
    sim("hand r %.3f %.3f %.3f %.1f %.1f 0" % (s[0], s[1], s[2], -yaw, pitch))
    time.sleep(0.3)
    # the move eased out at its end (a full-speed stop let the simulated controller's predicted pose snap back ~0.35 m: a
    # second swing, the other way): the first 1.0 m at full speed, then 0.25 m at under half, 0.15 m at a fifth
    if SNAP:
        # (critpics) both eyes in the middle of the move: the shot command goes out from a timer while xrsim moves the hand
        threading.Timer(SNAP[1], send, ["shot " + SNAP[0]]).start()
    if L > 1.05:
        a1, a2, a3 = xr(mul(d, 1.0)), xr(mul(d, 0.25)), xr(mul(d, L - 1.25))
        t1 = int(ms * 1.0 / L)
        sim("move r %.3f %.3f %.3f %d; move r %.3f %.3f %.3f %d; move r %.3f %.3f %.3f %d" % (
            a1[0], a1[1], a1[2], t1, a2[0], a2[1], a2[2], int(t1 * 0.55), a3[0], a3[1], a3[2], int(t1 * 0.8)))
    else:
        m = xr(mul(d, L))
        sim("move r %.3f %.3f %.3f %d" % (m[0], m[1], m[2], ms))


def ensure_crit(who, min_left=2.0):
    p = poise(who)
    if p.get("phase") == "Broken" and "axis" in p and p.get("left", 0) >= min_left:
        return p
    if p.get("phase") not in ("Up", "Broken"):
        t0 = time.time()
        while time.time() - t0 < 8 and poise(who).get("phase") not in ("Up",):
            time.sleep(0.25)
    send("poiseset %s 0" % who)
    t0 = time.time()
    while time.time() - t0 < 3:
        p = poise(who)
        if p.get("phase") == "Broken" and "axis" in p and p.get("left", 0) >= min_left:
            return p
        time.sleep(0.1)
    return None


def stand(who, dist):
    """The hero `dist` m in front of it (else beside or behind it: the first side with room), facing it."""
    # (8 m: env 3's hands turn 4 - with 4 m the fixture's second skeleton walked back during the cut's settling and hit the
    # hero 1.4 m back in the middle of the angles part's -40 deg cut, which then met nothing)
    send("aside %s 8" % who)
    r = ""
    for deg in (0, 180, 90, -90, 45, -45):
        r = send("heroat %s %d %.2f" % (who, deg, dist))
        send("face " + who)
        time.sleep(0.2)
        m = re.search(r"dist ([\d.]+)", send("enemy? " + who))
        if m and abs(float(m.group(1)) - (dist + 0.35)) <= 0.45:
            break
    return r


SNAP = None   # (name, seconds after the move starts): a picture in the middle of the next swing (critpics only)


def settle_enemy(who, timeout=2.5):
    """Until the enemy stands still (three looks 0.2 s apart within 2 cm, after the break's 0.15 s freeze), at most timeout s."""
    last, still, t0 = None, 0, time.time()
    while time.time() - t0 < timeout:
        m = re.search(r" at \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", send("enemy? " + who))
        p = [float(m.group(i)) for i in (1, 2, 3)] if m else None
        still = still + 1 if p and last and math.sqrt(sum((a - b) ** 2 for a, b in zip(p, last))) < 0.02 else 0
        if still >= 2 and time.time() - t0 >= 0.5:
            return True
        last = p
        time.sleep(0.2)
    return False


def on_stand(who, want):
    """The enemy still where the stand put it (`enemy?` dist within 0.45 m of the stand's distance + 0.35, as stand() checks):
    candidate 11's chain - a staggered skeleton stepped back from 1.3 to 2.4 m, and from 2.3 m to 5 m (the line 'too far'),
    between the stand and the cut, and those cuts never met the line (no try: neither a crit nor a wrong cut)."""
    m = re.search(r"dist ([\d.]+)", send("enemy? " + who))
    return (m is not None and abs(float(m.group(1)) - (want + 0.35)) <= 0.45), (m.group(1) if m else "?")


def cut_at(who, body_turn, depth, reverse=False, off=0.0, fwd_part=0.7, snap=None, want=None):
    """One scripted cut at the open crit of `who`: the arrow's way upright (turned `off` deg in the plane facing the player),
    plus fwd_part along the body's forward (turned body_turn deg from the line of sight); reversed: the whole move backwards.
    Returns (counts before, counts after, the crit before, the cut's direction)."""
    # the drawn weapon follows the hand with UYF's lag in the world: after `heroat` / `face` moved the rig it catches up -
    # a slow sweep (2.5 m/s, 0.3-0.6 m) through the line that landed or spoilt crits before the scripted cut (the cut's
    # counts are taken once it has settled)
    time.sleep(0.9)
    p = ensure_crit(who)
    if p is None:
        return None
    # the guard break's stagger carries the enemy back in its first second (candidate 11's close stand: the marker went from
    # 0.92 to 1.90 m during the reversed cut, which then met neither the line nor the body) - wait until it stands still
    settle_enemy(who)
    p = poise(who)
    if p.get("phase") != "Broken" or "axis" not in p or "at" not in p:
        return None
    if want is not None and not on_stand(who, want)[0]:
        # candidate 11's chains (the far stand failed in both runs, every try "off its stand"): the skeleton walked in (2.65
        # -> 1.0-1.75 m) during the weapon's settling and the break, or the stagger threw it back (4.3 m). It is stunned now
        # (held in place): the hero takes the stand again (heroat moves him along the line to it: the drawn weapon's catch-up
        # runs along the line of sight, so it crosses no line as the eyes see it), the weapon settles, and the cut goes if the
        # crit is still open long enough for it (a guard break's crit is open 7 s)
        stand(who, want)
        time.sleep(0.9)
        p = poise(who)
        if p.get("phase") != "Broken" or "axis" not in p or p.get("left", 0) < 0.8:
            log("  the stand taken again after the break: the crit %s (left %s) - the cut again" % (p.get("kind", "-"), p.get("left", "-")))
            return None
    eye = [0.0, 1.7, 0.0]
    at = p["at"]
    # the head looks at the marker (a player looks at what they cut)
    hv = sub(at, eye)
    pitch = math.degrees(math.atan2(hv[1], math.hypot(hv[0], hv[2])))
    sim("head 0 1.7 0 0 %.1f 0" % pitch)
    time.sleep(0.15)
    p = poise(who)
    if "axis" not in p or "at" not in p:
        return None
    if want is not None:
        ok, d = on_stand(who, want)
        if not ok:
            log("  the enemy is off its stand (%s m, the stand %.1f m): the cut again from a fresh stand" % (d, want))
            return None
    if snap:
        shot(snap + "_marker")   # the arrow as the player sees it from this stand, before the cut
        p = poise(who)
        if "axis" not in p or "at" not in p:
            return None
    at = p["at"]
    au = upright_axis(p["axis"], eye, at)
    if off:
        au = norm(rot_in_plane(au, norm([at[0], 0, at[2]]), off))
    view_flat = norm([at[0], 0.0, at[2]])
    body_fwd = rot_up(view_flat, body_turn)
    # reversed: against the arrow, still forward as a player swings (an upswing on a down arrow)
    d = norm(add(mul(au, -1 if reverse else 1), mul(body_fwd, fwd_part)))
    # the hand's path through the eyes' line to the marker, at most 0.75 m out (an arm's reach: a scripted hand 2.4 m out was
    # pulled back by the weapon's arm-length tether - a sweep the wrong way through the line)
    far_ = math.sqrt(dot(sub(at, eye), sub(at, eye))) or 1.0
    c = add(eye, mul(sub(at, eye), min(depth, 0.75 / far_)))
    # a 1.4 m path with the marker at 35 % of it: when a scripted move stops, the simulated controller's predicted pose snaps
    # back ~0.35 m (Monado extrapolates its last speed) - on a 1.0 m path centred on the marker that return went back
    # through it, the arrow's way after a reversed cut (it landed: the rest of a swing may still follow the arrow)
    L = 1.4
    start = sub(c, mul(d, L * 0.35))
    # the blade square to the cut, pointing away from the player (a chop's blade, not a thrust's: along the move it was a stab)
    v = norm(sub(at, eye))
    bl = sub(v, mul(d, dot(v, d)))
    bl = norm(bl) if dot(bl, bl) > 1e-4 else v
    c0 = counts()
    global SNAP
    SNAP = (snap + "_cut", 0.06) if snap else None
    try:
        swing(start, d, L, 210, blade=bl)
    finally:
        SNAP = None
    time.sleep(0.75)   # the swing ends, then 0.35 s: a wrong cut is settled
    c1 = counts()
    rest()
    if snap:
        save_shot(snap + "_cut")
    return c0, c1, p, d


def save_shot(name):
    """A shot sent by the swing's timer: its both-eye PNG -> PICS_OUT/name.jpg (+ _left), as pics.py does."""
    src = H + "/testdata/shots/" + name + "_eyes.png"
    for _ in range(24):
        if os.path.exists(src) and time.time() - os.path.getmtime(src) < 30 and os.path.getsize(src) > 0:
            break
        time.sleep(0.25)
    else:
        log("  picture %s: not written" % name)
        return
    time.sleep(0.5)
    try:
        from PIL import Image
        out = os.environ["PICS_OUT"]
        os.makedirs(out, exist_ok=True)
        im = Image.open(src).convert("RGB")
        im.save(out + "/" + name + ".jpg", quality=88)
        w, h = im.size
        im.crop((0, 0, w // 2, h)).save(out + "/" + name + "_left.jpg", quality=88)
        log("  picture %s: saved %s/%s.jpg" % (name, out, name))
    except Exception as ex:  # noqa: BLE001
        log("  picture %s: %r" % (name, ex))


def rest():
    """The hand back at rest by a jump over 0.5 m (no swing on the way: a short jump back is a fast swing through the next
    crit's line)."""
    sim("hand r 0.25 2.9 -0.35 0 5 0")
    time.sleep(0.12)
    sim(HAND_R)
    time.sleep(0.12)


STANDS = [
    # name, distance, the body turned (deg, + = right of the line of sight), the swing's depth (1 = through the marker)
    ("straight 1.3 m", 1.3, 0, 1.0),
    ("close 0.8 m, looking down", 0.8, 0, 1.0),
    ("far 2.3 m (the line only)", 2.3, 0, 0.42),
    ("body turned 45 deg right", 1.3, 45, 1.0),
    ("body turned 45 deg left, 1.0 m", 1.0, -45, 1.0),
]


def skeleton():
    p = poise("Skeleton")
    return p.get("id")


def part_crit():
    send("vrmagnet off")
    who = skeleton()
    if not who:
        return result("crit", False, "no skeleton: " + send("enemy? nearest")[:120])
    try:
        for name, dist, turn, depth in STANDS:
            if user_in_vr():
                return
            ok_f = ok_r = None
            detail = []
            for attempt in range(5):
                stand(who, dist)
                r = cut_at(who, turn, depth, want=dist)
                if r is None:
                    detail.append("no crit open")
                    continue
                c0, c1, p, d = r
                dl = lambda k: c1.get(k, 0) - c0.get(k, 0)
                if dl("swings") < 1:
                    detail.append("no legal swing")
                    continue
                ok_f = dl("critsLanded") >= 1
                if not ok_f:
                    detail.append("(missed: swings +%d illegal +%d drawnJumps +%d lineLast [%s])" % (dl("swings"), dl("illegal"), dl("drawnJumps"), re.sub(r".*lineLast \[([^]]*)\].*", r"\1", c1.get("_raw", ""))[:120]))
                detail.append("follow %s deg crit (axisT %s, the cut %s): crits +%d wrong +%d hits +%d lineHits +%d | %s" % (
                    p.get("line"), [round(x, 2) for x in p["axis"]], [round(x, 2) for x in d], dl("critsLanded"), dl("critsWrong"), dl("hits"), dl("lineHits"), last_try()))
                break
            for attempt in range(5):
                stand(who, dist)
                r = cut_at(who, turn, depth, reverse=True, want=dist)
                if r is None:
                    detail.append("no crit open")
                    continue
                c0, c1, p, d = r
                dl = lambda k: c1.get(k, 0) - c0.get(k, 0)
                if dl("swings") < 1:
                    detail.append("no legal swing")
                    continue
                ok_r = dl("critsLanded") == 0 and dl("critsWrong") >= 1
                detail.append("reversed %s deg crit (axisT %s, the cut %s): crits +%d wrong +%d swings +%d | %s" % (
                    p.get("line"), [round(x, 2) for x in p["axis"]], [round(x, 2) for x in d], dl("critsLanded"), dl("critsWrong"), dl("swings"), last_try()))
                break
            result("crit " + name, bool(ok_f) and bool(ok_r), " ; ".join(detail))
    finally:
        send("vrmagnet setting")
        sim(HEAD + "; " + HAND_R)


def part_critpics():
    """Pictures only (--pics): at four of the crit part's stands, the arrow before the cut (`_marker`) and the blade in the
    middle of a cut that follows it (`_cut`, the shot sent ~60 ms into the move). The outcome is logged, not judged (the
    crit part judges these cuts without a picture in the middle: a shot can cost a frame)."""
    if not PICS:
        return result("critpics", False, "needs --pics")
    send("vrmagnet off")
    who = skeleton()
    if not who:
        return result("critpics", False, "no skeleton: " + send("enemy? nearest")[:120])
    took = []
    try:
        for i, (name, dist, turn, depth) in enumerate([STANDS[0], STANDS[1], STANDS[2], STANDS[3]]):
            if user_in_vr():
                return
            tag = "p4e3_crit_%d_%s" % (i + 1, re.sub(r"[^a-z0-9]+", "_", name.lower()).strip("_")[:24])
            for attempt in range(5):
                stand(who, dist)
                r = cut_at(who, turn, depth, snap=tag, want=dist)
                if r is None:
                    continue
                c0, c1, p, d = r
                dl = lambda k: c1.get(k, 0) - c0.get(k, 0)
                took.append("%s: crits +%d wrong +%d swings +%d" % (name, dl("critsLanded"), dl("critsWrong"), dl("swings")))
                log("  critpics %s: crits +%d wrong +%d swings +%d | %s" % (name, dl("critsLanded"), dl("critsWrong"), dl("swings"), last_try()))
                break
        result("critpics", len(took) >= 3, " ; ".join(took) or "no crit open")
    finally:
        send("vrmagnet setting")
        sim(HEAD + "; " + HAND_R)


def last_try():
    """The game log's last crit try line (Poise: crit try OK|MISS ...)."""
    logf = hof2env.GLOG
    try:
        with open(logf, errors="replace") as f:
            lines = [l for l in f.readlines()[-400:] if "crit try" in l]
        return lines[-1].split("crit try", 1)[1].strip()[:260] if lines else "-"
    except OSError:
        return "-"


def part_angles():
    send("vrmagnet off")
    who = skeleton()
    if not who:
        return result("angles", False, "no skeleton")
    try:
        got = {}
        for off, want in ((40, True), (-40, True), (75, False)):
            for attempt in range(5):
                stand(who, 1.3)
                r = cut_at(who, 0, 1.0, off=off, fwd_part=0.3, want=1.3)
                if r is None:
                    continue
                c0, c1, p, d = r
                dl = lambda k: c1.get(k, 0) - c0.get(k, 0)
                if dl("swings") < 1:
                    continue
                landed = dl("critsLanded") >= 1
                got[off] = (landed == want, "%+d deg: crits +%d wrong +%d | %s" % (off, dl("critsLanded"), dl("critsWrong"), last_try()))
                break
        ok = len(got) == 3 and all(v[0] for v in got.values())
        result("angles", ok, " ; ".join(v[1] for v in got.values()) or "no cut made")
    finally:
        send("vrmagnet setting")
        sim(HEAD + "; " + HAND_R)


# ------------------------------------------------------------------------------------------------ gnomes

def posts():
    r = send("gnomeposts?", 6)
    d = {"_raw": r, "g": []}
    for k in ("deals", "moves", "arrivals", "gazeGated", "groundHits"):
        m = re.search(r"\b%s (\d+)" % k, r)
        d[k] = int(m.group(1)) if m else 0
    for m in re.finditer(r"\| (-?\d+) (waiting|busy) post (-?\d+) deg ([\d.]+) m; at (-?\d+) deg ([\d.]+) m, ([\d.]+) m off its post( going)? faces (\d+) deg(?: off the hero act \S+ mark ([\d.]+) m)?", r):
        d["g"].append({"id": m.group(1), "waiting": m.group(2) == "waiting", "post": int(m.group(3)), "r": float(m.group(4)), "at": int(m.group(5)),
                       "dist": float(m.group(6)), "off": float(m.group(7)), "going": bool(m.group(8)), "faces": int(m.group(9)),
                       "mark": float(m.group(10)) if m.group(10) else -1.0})
    return d


def turns(d):
    """GnomeFight.PostTurns (a post short of floor moved round the hero; 0 before the 10-20 build)."""
    return int((re.search(r"postTurns (\d+)", d["_raw"]) or [0, 0])[1])


def part_posts():
    """The leaps held (`vrgnomes hold on`: the waiting formation alone), any cling broken: after 14 s the posts' spread round
    the hero and the gnomes at them, facing him; the posts dealt again and walked to."""
    sim(HEAD + "; " + HAND_R)
    send("vrgnomes hold on")
    try:
        _posts()
    finally:
        send("vrgnomes hold off")


def _posts():
    t0 = time.time()
    while gn()["clinging"] and time.time() - t0 < 20:
        ring_cuts()
        time.sleep(0.5)
    p0 = posts()
    time.sleep(14)
    p1 = posts()
    w = [g for g in p1["g"] if g["waiting"]]
    # the spread is the posts' (where each is sent); where they stand: each settled one's own place
    ats = sorted(g["post"] % 360 for g in w)
    gaps = [(ats[(i + 1) % len(ats)] - ats[i]) % 360 for i in range(len(ats))] if len(ats) > 1 else [360]
    ahead = sum(1 for g in w if abs(g["post"]) <= 45)
    settled = [g for g in w if not g["going"] and g["off"] <= 0.8]
    facing = [g for g in settled if g["faces"] <= 60]
    dists_ok = sum(1 for g in settled if 1.6 <= g["dist"] <= 3.4)
    moved = p1["moves"] > p0["moves"] or p1["arrivals"] > p0["arrivals"]
    # the widest gap: under 160 deg - under 220 where the floor's edge turned posts round (GnomeFight.PostTurns: the hero's back
    # near the arena's edge, no gnome can stand behind him)
    gap_max = 220 if turns(p1) > turns(p0) else 160
    ok = len(w) >= 3 and max(gaps) < gap_max and ahead <= (len(w) + 1) // 2 and dists_ok >= len(settled) - 1 and moved and len(facing) >= max(1, len(settled) - 1)
    result("posts", ok, "%d waiting: posts at %s deg off the gaze (standing at %s), the widest gap %d deg, %d within 45 deg of the gaze; settled at their posts %d of them %d 1.6-3.4 m off (all: %s; their marks %s), facing the hero %d (%s); deals +%d moves +%d arrivals +%d gaze-gated %d%s" % (
        len(w), [g["post"] for g in w], [g["at"] for g in w], max(gaps), ahead, len(settled), dists_ok, ["%.1f" % g["dist"] for g in w], ["%.1f" % g["mark"] for g in w], len(facing),
        [g["faces"] for g in settled], p1["deals"] - p0["deals"], p1["moves"] - p0["moves"], p1["arrivals"] - p0["arrivals"], p1["gazeGated"],
        "; posts turned round from the floor's edge +%d" % (turns(p1) - turns(p0))))
    if PICS:
        sim(HEAD + "; " + HAND_R)
        sim("head 0 1.7 0 0 -12 0")
        time.sleep(0.4)
        shot("p4e3_posts_ahead")
        sim("head 0 1.7 0 -90 -12 0")
        time.sleep(0.4)
        shot("p4e3_posts_left")
        sim("head 0 1.7 0 90 -12 0")
        time.sleep(0.4)
        shot("p4e3_posts_right")
        sim("head 0 1.7 0 180 -12 0")
        time.sleep(0.4)
        shot("p4e3_posts_behind")
        sim(HEAD)


def part_ground():
    sim(HEAD + "; " + HAND_R)
    for attempt in range(6):
        if user_in_vr():
            return
        d = posts()
        w = [g for g in d["g"] if g["waiting"] and not g["going"]]
        if not w:
            time.sleep(1.0)
            continue
        g = min(w, key=lambda x: abs(x["at"]))
        who = "#" + g["id"]
        e = send("enemy? " + who)
        if "dist" not in e:
            # (FindEnemy keys on the Targetable's id: the post lists the AI controller's - try the nearest gnome instead)
            who = "Gnome"
            e = send("enemy? Gnome")
        send("heroat %s 0 1.0" % who)
        send("face " + who)
        time.sleep(0.2)
        e = send("enemy? " + who)
        m = re.search(r"dist ([\d.]+).* at \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\) head \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)", e)
        if not m:
            continue
        dist = float(m.group(1))
        gy = float(m.group(3)) - float(m.group(6)) + 1.7          # its feet in tracking height
        mid = [0.0, gy + 0.45, max(0.5, dist)]                     # its body's middle ahead
        c0 = counts()
        pc0 = posts()
        start = add(mid, [0.5, 0.0, -0.35])
        swing(start, norm([-1.0, -0.05, 0.0]), 1.0, 150, aim=add(mid, [0, -0.05, 0.6]))
        time.sleep(0.6)
        c1 = counts()
        pc1 = posts()
        dl = lambda k: c1.get(k, 0) - c0.get(k, 0)
        sim(HAND_R)
        if dl("hits") >= 1 and pc1["groundHits"] > pc0["groundHits"]:
            return result("ground", True, "a sweep through a standing gnome %.2f m off: hits +%d, ground hits +%d | %s" % (
                dist, dl("hits"), pc1["groundHits"] - pc0["groundHits"], re.sub(r".* last ", "", c1["_raw"])[:160]))
        log("  ground: attempt %d at %.2f m: swings +%d hits +%d ground +%d illegal +%d | %s" % (attempt + 1, dist, dl("swings"), dl("hits"),
            pc1["groundHits"] - pc0["groundHits"], dl("illegal"), e[:120]))
        time.sleep(1.0)
    result("ground", False, "no hit on a standing gnome in 6 tries")


def gn():
    r = send("gnomes?", 4)
    d = {"_raw": r, "clinging": " clinging True" in r}
    for k in ("clings", "ringHits", "freed", "posed"):
        m = re.search(r"\b%s (\d+)" % k, r)
        d[k] = int(m.group(1)) if m else 0
    m = re.search(r"ringT (-?[\d.]+),(-?[\d.]+),(-?[\d.]+)", r)
    d["ringT"] = v3(m, 1) if m else None
    m = re.search(r"midT \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\) eyes ([\d.]+) hiddenNear (\d+) posedNow (\w+) nearest ([\d.-]+)", r)
    if m:
        d["mid"], d["eyes"], d["hiddenNear"], d["posedNow"], d["nearest"] = v3(m, 1), float(m.group(4)), int(m.group(5)), m.group(6) == "True", float(m.group(7))
    return d


def ring_cuts(n=8):
    hits = 0
    for i in range(n):
        d = gn()
        if not d["clinging"] or d["ringT"] is None:
            break
        x, y, z = d["ringT"][0], d["ringT"][1], -d["ringT"][2]
        hz = z * 0.45
        side = 1 if i % 2 == 0 else -1
        sim("hand r %.3f %.3f %.3f 0 0 0" % (x - 0.5 * side, y, hz))
        time.sleep(0.15)
        sim("move r %.3f 0 0 110" % (1.0 * side))
        time.sleep(0.45)
        hits += 1
    return hits


def part_cling():
    sim(HEAD + "; " + DOWN)
    # a cling begun before this part is cut free first: the ground part turns the hero to its gnome, and a gnome posed ahead
    # of the gaze as it was stays there until the view turns 60 deg away (env 3's hands turn 5: one clung, the ground part's
    # next try turned the hero 55 deg to another gnome, the measure found it 54 deg off the gaze - test-side). The one
    # measured clings while the view stays put
    t0 = time.time()
    while gn()["clinging"] and time.time() - t0 < 20:
        ring_cuts()
        time.sleep(0.5)
    d0 = gn()
    t0 = time.time()
    d = d0
    while time.time() - t0 < 75 and not d["clinging"]:
        if user_in_vr():
            return
        time.sleep(0.2)
        d = gn()
    if not d["clinging"]:
        return result("cling", False, "no gnome clung in 75 s | " + d["_raw"][:200])
    time.sleep(0.5)
    d = gn()
    mid, eyes = d.get("mid"), d.get("eyes", 0)
    near = send("eyenear 0.45", 4)
    gnome_near = [x for x in near.split(" | ")[1:] if "nome" in x]
    ang = math.degrees(math.atan2(mid[0], mid[2])) if mid else 999
    ring_off = math.dist(mid, d["ringT"]) if mid and d["ringT"] else 99
    # nothing of it within 0.45 m of the eyes: its bones' nearest less 0.1 m (the renderer's box, `eyenear`, is a loose box)
    shown = d.get("posedNow") and 0.6 <= eyes <= 1.25 and abs(ang) <= 35 and d.get("nearest", 0) >= 0.45 and ring_off <= 0.3
    log("  cling: the gnome's middle %s, %.2f m from the eyes, %.0f deg off the gaze; the ring %.2f m from it; near the eyes: %s" % (
        mid, eyes, ang, ring_off, near[:300]))
    if PICS:
        shot("p4e3_cling_front")
        sim("head 0 1.7 0 0 -20 0")
        time.sleep(0.3)
        shot("p4e3_cling_front_looking_down")
        sim(HEAD)
    hits = ring_cuts()
    time.sleep(0.5)
    d1 = gn()
    freed = d1["freed"] > d0["freed"] and not d1["clinging"]
    result("cling", bool(shown) and freed, "shown in front: %s (posed %s, middle %.2f m from the eyes, %.0f deg off the gaze, its nearest part %.2f m from them, the ring %.2f m from it; renderer boxes within 0.45 m of the eyes: %d); %d cuts: ring hits +%d, freed +%d | %s" % (
        bool(shown), d.get("posedNow"), eyes, ang, d.get("nearest", -1), ring_off, len(gnome_near), hits, d1["ringHits"] - d0["ringHits"], d1["freed"] - d0["freed"], d1["_raw"].split("| last")[-1][:140]))


def part_facts():
    log("  gnomefacts: " + send("gnomefacts", 6)[:3000])
    result("facts", True, "logged")


PARTS = {"crit": part_crit, "critpics": part_critpics, "angles": part_angles, "posts": part_posts, "ground": part_ground, "cling": part_cling, "facts": part_facts}
FIX = {"crit": ("EL_Ambush_Undead", "SkeletonNEWREF*2"), "critpics": ("EL_Ambush_Undead", "SkeletonNEWREF*2"), "angles": ("EL_Ambush_Undead", "SkeletonNEWREF*2"),
       "posts": ("EL_Ambush_Thral", "Card_MO_Gnome_Special*1"), "ground": ("EL_Ambush_Thral", "Card_MO_Gnome_Special*1"),
       "cling": ("EL_Ambush_Thral", "Card_MO_Gnome_Special*1"), "facts": ("EL_Ambush_Thral", "Card_MO_Gnome_Special*1")}


def fixture(jump, spec):
    st = send("state?")
    if "Level_Play" in st:
        subprocess.run(["python3", "-c", "import sys; sys.path.insert(0, %r); import indicator_shots; indicator_shots.leave()" % (H + "/scripts")])
        time.sleep(2)
    boot = ["--boot"] if "GameState_Map" not in send("state?") else []
    r = subprocess.run(["python3", H + "/scripts/combat_fixture.py", "--jump", jump, "--with", spec] + boot +
                       ["--god", "--physical", "--equip", "BasicSword_noToken,BasicShield"], capture_output=True, text=True)
    log("fixture %s %s: %s" % (jump, spec, ((r.stdout or "").strip().splitlines() or ["?"])[-1]))
    for c in ("god on", "tough on", "vrcombat physical", "vrgnomes on", "pcombat? reset"):
        send(c)
    return "Level_Play" in send("state?")


def main():
    parts = args[args.index("--parts") + 1].split(",") if "--parts" in args else ["crit", "angles", "posts", "ground", "cling"]
    cur = None
    for p in parts:
        if user_in_vr():
            log("the user is in VR: stopping")
            break
        if "--no-fixture" not in args and FIX[p] != cur:
            if not fixture(*FIX[p]):
                result(p, False, "no fight: " + send("state?"))
                continue
            cur = FIX[p]
        send("tough on")
        try:
            PARTS[p]()
        except Exception as ex:  # noqa: BLE001
            result(p, False, "error " + repr(ex))
    sim(HEAD + "; " + HAND_R)
    if "--stay" not in args:
        subprocess.run(["python3", "-c", "import sys; sys.path.insert(0, %r); import indicator_shots; indicator_shots.leave()" % (H + "/scripts")])
    log("%s: %d/%d PASS" % (TAG, sum(OUT), len(OUT)))
    return 0 if OUT and all(OUT) else 1


if __name__ == "__main__":
    sys.exit(main())

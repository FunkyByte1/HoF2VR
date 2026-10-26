#!/usr/bin/env python3
"""Drive Monado's "remote" test headset (TCP 4242; environment N: 4241+N): head/hand poses, buttons, sticks, motions.

  xrsim.py "<cmd>; <cmd>; ..."        run commands in order, then disconnect (state persists)
  xrsim.py --daemon                    keep ONE connection open and serve commands on testdata/xrsim.sock
                                       (monado.sh starts it; Monado's remote driver leaks a socket per
                                       connection and aborts at fd 1024, so tests must not reconnect)

Poses are in the runtime's stage space (metres; y up; -z forward). Angles in degrees.
Commands:
  head X Y Z [YAW PITCH ROLL]            set the HMD pose
  turn Y0 Y1 P0 P1 MS [EYE]              smooth head turn (yaw Y0->Y1, pitch P0->P1 over MS, eased) about a neck
                                         pivot, eyes at EYE m (1.75)
  hand l|r X Y Z [YAW PITCH ROLL]        set a controller pose
  rel l|r X Y Z                          controller pose relative to the head (head yaw only)
  btn l|r NAME [MS]                      press NAME (a b system trigger grip stick) for MS (default 150)
  hold l|r NAME 0|1                      press/release without timing
  stick l|r X Y [MS]                     thumbstick deflection for MS (0 = leave it there)
  trig l|r VALUE                         analog trigger 0..1
  swing r|l [right|left|down|up|thrust] [MS]   sweep the hand through a fast arc (default 160 ms)
  move l|r DX DY DZ MS                   move a controller linearly by (DX,DY,DZ) over MS
  path l|r DX DY DZ [DX DY DZ ...] MS    consecutive relative moves (a shake, an arc), MS in total
  fling l|r DX DY DZ MS [FRAC]           throw: constant-speed move, the grip opens at FRAC (0.75) of it
  touch l|r 0|1                          finger on the trigger (capacitive touch) without pulling it
  squeeze l|r VALUE                      analog grip 0..1 (no click: the runtime decides when it counts as pressed)
  ctouch l|r a|b|stick|trigger 0|1       a capacitive touch without a press (thumb on A/B or the stick)
  rot l|r YAW PITCH ROLL                 controller orientation only (the mod's `aim` prints one)
  wait MS                                sleep
  reset                                  default pose: head 1.6 m, hands in front
  seat DY                                a seated player: every absolute head / hand height (head, turn, hand) is DY m
                                         lower than written (`seat 0.55`: a script's standing 1.75 m eyes become 1.20 m);
                                         `seat 0` = standing (session.py start resets it)
  noise on [MM] [DEG] | noise off        (daemon) live-tracking jitter: every 11 ms the head and the controllers are
                                         sent with gaussian noise (default 0.3 mm, 0.03 deg) on top of their pose, like
                                         a worn headset; `noise off` = the poses freeze exactly (a paused headset app)
  fov LEFT RIGHT UP DOWN [IPD]           per-eye field of view in degrees for the LEFT eye (the right eye is
                                         mirrored) and the eye separation in metres (default 0.063);
                                         `fov quest2` = Quest 2-like (-52 42 44 -50), `fov off` = Monado's own
  show                                   print the current state
Indices / Touch mapping on the game side: a=A/X, b=B/Y, grip=squeeze, stick=thumbstick click.
"""
import math
import random
import socket
import struct
import sys
import threading
import time

import hof2env  # this worktree's test environment: its Monado's port and its daemon socket (docs/two-environments.md)

HOST, PORT = "127.0.0.1", hof2env.XRSIM_PORT
HEADER = b"mndrmt3\0"
CTRL_FMT = "<4f3f" + "3f3f" + "5f" + "fff" + "2f" + "f" + "2f" + "16?"  # 120 bytes
HEAD_FMT = "<" + ("4f4f3fI" * 2) + "4f3f" + "?3x"  # 128 bytes
SIZE = 8 + struct.calcsize(HEAD_FMT) + 2 * struct.calcsize(CTRL_FMT)
assert struct.calcsize(CTRL_FMT) == 120 and struct.calcsize(HEAD_FMT) == 128 and SIZE == 376

BOOLS = ["hand_tracking_active", "active", "system_click", "system_touch", "a_click", "a_touch",
         "b_click", "b_touch", "trigger_click", "trigger_touch", "thumbstick_click", "thumbstick_touch",
         "trackpad_touch", "_p0", "_p1", "_p2"]
NAMES = {"a": "a_click", "b": "b_click", "system": "system_click", "trigger": "trigger_click",
         "stick": "thumbstick_click", "grip": None}


def quat_from_euler(yaw, pitch, roll):
    """Yaw about +Y, pitch about +X, roll about -Z (degrees), OpenXR conventions."""
    y, p, r = (math.radians(a) / 2 for a in (yaw, pitch, roll))
    cy, sy, cp, sp, cr, sr = math.cos(y), math.sin(y), math.cos(p), math.sin(p), math.cos(r), math.sin(r)
    # q = qYaw * qPitch * qRoll
    qy = (0, sy, 0, cy)
    qp = (sp, 0, 0, cp)
    qr = (0, 0, sr, cr)
    return qmul(qmul(qy, qp), qr)


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


class Ctrl:
    def __init__(self, vals):
        v = list(vals)
        self.rot = v[0:4]
        self.pos = v[4:7]
        self.linvel = v[7:10]
        self.angvel = v[10:13]
        self.curl = v[13:18]
        self.trigger, self.squeeze, self.squeeze_force = v[18], v[19], v[20]
        self.stick = v[21:23]
        self.trackpad_force = v[23]
        self.trackpad = v[24:26]
        self.b = dict(zip(BOOLS, v[26:42]))

    def pack(self):
        return struct.pack(CTRL_FMT, *self.rot, *self.pos, *self.linvel, *self.angvel, *self.curl,
                           self.trigger, self.squeeze, self.squeeze_force, *self.stick, self.trackpad_force,
                           *self.trackpad, *[self.b[k] for k in BOOLS])


class State:
    def __init__(self, raw):
        assert raw[:8] == HEADER, raw[:8]
        hs = struct.calcsize(HEAD_FMT)
        self.head = list(struct.unpack(HEAD_FMT, raw[8:8 + hs]))
        cs = struct.calcsize(CTRL_FMT)
        self.left = Ctrl(struct.unpack(CTRL_FMT, raw[8 + hs:8 + hs + cs]))
        self.right = Ctrl(struct.unpack(CTRL_FMT, raw[8 + hs + cs:8 + hs + 2 * cs]))

    # head: views (2 x 12 values) then center rot(4) pos(3), valid
    def head_pose(self):
        return self.head[24:28], self.head[28:31]

    def set_head(self, rot, pos):
        self.head[24:28] = rot
        self.head[28:31] = pos

    def pack(self):
        return HEADER + struct.pack(HEAD_FMT, *self.head) + self.left.pack() + self.right.pack()


class Sim:
    def __init__(self):
        self.s = socket.create_connection((HOST, PORT), timeout=5)
        self.s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        reset = self._recv()
        latest = self._recv()
        self.reset_state = State(reset)
        self.st = State(latest)
        self.head_yaw = 0.0
        self.seat = 0.0            # seated tests: absolute head / hand heights this much lower
        self.lock = threading.Lock()
        self.noise = None          # (metres, degrees) while live-tracking jitter is on
        self.noise_thread = None
        self.alive = True

    def jitter_loop(self):
        while self.alive:
            n = self.noise
            if n:
                pos, rot = n
                with self.lock:
                    c = State(self.st.pack())
                    hr, hp = c.head_pose()
                    hp = [v + random.gauss(0, pos) for v in hp]
                    hr = qmul(tuple(hr), quat_from_euler(*(random.gauss(0, rot) for _ in range(3))))
                    c.set_head(list(hr), hp)
                    for ctl in (c.left, c.right):
                        ctl.pos = [v + random.gauss(0, pos * 2) for v in ctl.pos]
                        ctl.rot = list(qmul(tuple(ctl.rot), quat_from_euler(*(random.gauss(0, rot * 2) for _ in range(3)))))
                    try:
                        self.s.sendall(c.pack())
                    except OSError:
                        self.alive = False
                        return
            time.sleep(0.011)

    def _recv(self):
        buf = b""
        while len(buf) < SIZE:
            chunk = self.s.recv(SIZE - len(buf))
            if not chunk:
                raise SystemExit("remote hub closed the connection")
            buf += chunk
        return buf

    def send(self):
        with self.lock:
            self.s.sendall(self.st.pack())

    def ctrl(self, side):
        return self.st.left if side.startswith("l") else self.st.right

    def press(self, c, name, on):
        if name == "grip":
            c.squeeze = 1.0 if on else 0.0
            c.squeeze_force = 1.0 if on else 0.0
            return
        if name == "trigger":
            c.trigger = 1.0 if on else 0.0
        key = NAMES[name]
        c.b[key] = on
        touch = key.replace("_click", "_touch")
        if touch in c.b:
            c.b[touch] = on

    def animate(self, ms, fn):
        steps = max(2, int(ms / 11))
        for i in range(1, steps + 1):
            fn(i / steps)
            self.send()
            time.sleep(ms / 1000 / steps)

    def run(self, cmd):
        a = cmd.split()
        if not a:
            return
        op = a[0]
        def nums(i):
            return [float(x) for x in a[i:]]

        f = nums(2) if op in ("hand", "rel", "move") else []
        if op == "head":
            x, y, z = map(float, a[1:4])
            y -= self.seat
            ang = [float(v) for v in a[4:7]] + [0] * (3 - len(a[4:7]))
            self.head_yaw = ang[0]
            self.st.set_head(quat_from_euler(*ang), [x, y, z])
            self.send()
        elif op == "turn":
            # turn YAW0 YAW1 PITCH0 PITCH1 MS [EYE]: a smooth head turn about a neck pivot 0.10 m behind and below the
            # eyes (eased in and out), like a person looking round; EYE = eye height (default 1.75)
            y0, y1, p0, p1, ms = (float(v) for v in a[1:6])
            eye = (float(a[6]) if len(a) > 6 else 1.75) - self.seat

            def pose(t):
                e = 0.5 - 0.5 * math.cos(math.pi * t)
                yaw, pitch = y0 + (y1 - y0) * e, p0 + (p1 - p0) * e
                p, y = math.radians(pitch), math.radians(yaw)
                yy = 0.10 * math.cos(p) + 0.10 * math.sin(p)
                zz = 0.10 * math.sin(p) - 0.10 * math.cos(p)
                self.head_yaw = yaw
                self.st.set_head(quat_from_euler(yaw, pitch, 0), [zz * math.sin(y), eye - 0.10 + yy, zz * math.cos(y) + 0.10])
            # by the clock, a fresh pose every ~4 ms: every rendered frame sees the head move (a fixed step count
            # with sleeps drifted to every other frame at times, which hid per-frame effects)
            t0 = time.perf_counter()
            while True:
                t = (time.perf_counter() - t0) * 1000.0 / ms
                pose(min(1.0, t))
                self.send()
                if t >= 1.0:
                    break
                time.sleep(0.004)
        elif op == "hand":
            c = self.ctrl(a[1])
            ang = f[3:6] + [0] * (3 - len(f[3:6]))
            c.pos = [f[0], f[1] - self.seat, f[2]]
            c.rot = list(quat_from_euler(*ang))
            c.b["active"] = True
            self.send()
        elif op == "rel":
            c = self.ctrl(a[1])
            hrot, hpos = self.st.head_pose()
            yaw = math.radians(self.head_yaw)
            dx, dy, dz = f[0:3]
            c.pos = [hpos[0] + dx * math.cos(yaw) + dz * math.sin(yaw), hpos[1] + dy,
                     hpos[2] - dx * math.sin(yaw) + dz * math.cos(yaw)]
            c.rot = list(quat_from_euler(self.head_yaw, 0, 0))
            self.send()
        elif op in ("btn", "hold"):
            c = self.ctrl(a[1])
            name = a[2]
            if op == "hold":
                self.press(c, name, a[3] == "1")
                self.send()
            else:
                ms = float(a[3]) if len(a) > 3 else 150
                self.press(c, name, True)
                self.send()
                time.sleep(ms / 1000)
                self.press(c, name, False)
                self.send()
        elif op == "stick":
            c = self.ctrl(a[1])
            c.stick = [float(a[2]), float(a[3])]
            c.b["thumbstick_touch"] = True
            self.send()
            ms = float(a[4]) if len(a) > 4 else 0
            if ms > 0:
                time.sleep(ms / 1000)
                c.stick = [0.0, 0.0]
                self.send()
        elif op == "trig":
            c = self.ctrl(a[1])
            c.trigger = float(a[2])
            c.b["trigger_touch"] = c.trigger > 0
            c.b["trigger_click"] = c.trigger > 0.9
            self.send()
        elif op == "move":
            c = self.ctrl(a[1])
            start = list(c.pos)
            d = f[0:3]
            ms = f[3]
            self.animate(ms, lambda t: setattr(c, "pos", [start[i] + d[i] * t for i in range(3)]))
        elif op == "rot":
            c = self.ctrl(a[1])
            ang = [float(v) for v in a[2:5]] + [0] * (3 - len(a[2:5]))
            c.rot = list(quat_from_euler(*ang))
            self.send()
        elif op == "touch":
            c = self.ctrl(a[1])
            c.b["trigger_touch"] = a[2] == "1"
            self.send()
        elif op == "squeeze":
            c = self.ctrl(a[1])
            c.squeeze = float(a[2])
            c.squeeze_force = float(a[2])
            self.send()
        elif op == "ctouch":
            c = self.ctrl(a[1])
            key = {"a": "a_touch", "b": "b_touch", "stick": "thumbstick_touch", "trigger": "trigger_touch"}[a[2]]
            c.b[key] = a[3] == "1"
            self.send()
        elif op == "path":
            c = self.ctrl(a[1])
            vals = [float(x) for x in a[2:]]
            ms, pts = vals[-1], vals[:-1]
            segs = [pts[i:i + 3] for i in range(0, len(pts) - len(pts) % 3, 3)]
            lens = [max(1e-6, math.sqrt(sum(v * v for v in sg))) for sg in segs]
            total = sum(lens)
            for sg, ln in zip(segs, lens):
                st0 = list(c.pos)
                self.animate(ms * ln / total, lambda t, sg=sg, st0=st0: setattr(c, "pos", [st0[i] + sg[i] * t for i in range(3)]))
        elif op == "fling":
            c = self.ctrl(a[1])
            d = [float(x) for x in a[2:5]]
            ms = float(a[5])
            frac = float(a[6]) if len(a) > 6 else 0.75
            st0 = list(c.pos)
            steps = max(2, int(ms / 11))
            released = False
            for i in range(1, steps + 1):
                t = i / steps
                c.pos = [st0[k] + d[k] * t for k in range(3)]
                if not released and t >= frac:
                    self.press(c, "grip", False)
                    released = True
                self.send()
                time.sleep(ms / 1000 / steps)
        elif op == "swing":
            c = self.ctrl(a[1])
            kind = a[2] if len(a) > 2 else "right"
            ms = float(a[3]) if len(a) > 3 else 160
            start = list(c.pos)
            yaw = math.radians(self.head_yaw)
            right = (math.cos(yaw), 0, -math.sin(yaw))
            fwd = (-math.sin(yaw), 0, -math.cos(yaw))
            arcs = {
                "right": [(-0.35, 0.15, 0.0), (0.35, -0.10, 0.25)],   # backhand to forehand across the body
                "left": [(0.35, 0.15, 0.0), (-0.35, -0.10, 0.25)],
                "down": [(0.0, 0.40, 0.05), (0.0, -0.30, 0.35)],
                "up": [(0.0, -0.30, 0.10), (0.0, 0.35, 0.30)],
                "thrust": [(0.0, 0.0, -0.05), (0.0, 0.0, 0.55)],
            }
            p0, p1 = arcs[kind]

            def world(o):
                return [start[i] + o[0] * right[i] + o[2] * fwd[i] + (o[1] if i == 1 else 0) for i in range(3)]

            a0, a1 = world(p0), world(p1)
            s0 = list(c.pos)
            self.animate(220, lambda t: setattr(c, "pos", [s0[i] + (a0[i] - s0[i]) * t for i in range(3)]))   # slow wind-up
            time.sleep(0.12)
            self.animate(ms, lambda t: setattr(c, "pos", [a0[i] + (a1[i] - a0[i]) * (0.5 - 0.5 * math.cos(math.pi * t)) for i in range(3)]))
            time.sleep(0.15)
            self.animate(600, lambda t: setattr(c, "pos", [a1[i] + (start[i] - a1[i]) * t for i in range(3)]))   # slow recovery
        elif op == "fov":
            # per-view data of the remote driver's head: views[i] = fov(4f) rot(4f) pos(3f) pad, then
            # center rot(4f) pos(3f), per_view_data_valid
            if a[1] == "off":
                self.st.head[31] = False
            else:
                vals = [-52.0, 42.0, 44.0, -50.0] if a[1] == "quest2" else [float(v) for v in a[1:5]]
                ipd = float(a[5]) if len(a) > 5 else (float(a[2]) if a[1] == "quest2" and len(a) > 2 else 0.063)
                l, r, u, d = [math.radians(v) for v in vals]
                for i, (fl, fr, x) in enumerate(((l, r, -ipd / 2), (-r, -l, ipd / 2))):
                    self.st.head[i * 12:i * 12 + 12] = [fl, fr, u, d, 0.0, 0.0, 0.0, 1.0, x, 0.0, 0.0, 0]
                self.st.head[31] = True
            self.send()
        elif op == "noise":
            if a[1] == "off":
                self.noise = None
                time.sleep(0.03)
                self.send()          # the exact pose again, frozen from now on
            else:
                self.noise = (float(a[2]) / 1000 if len(a) > 2 else 0.0003, float(a[3]) if len(a) > 3 else 0.03)
                if self.noise_thread is None:
                    self.noise_thread = threading.Thread(target=self.jitter_loop, daemon=True)
                    self.noise_thread.start()
        elif op == "wait":
            time.sleep(float(a[1]) / 1000)
        elif op == "seat":
            # sitting down / standing up: the head and the hands where they are now move by the change too
            new = float(a[1]) if len(a) > 1 else 0.0
            dy = new - self.seat
            self.seat = new
            if abs(dy) > 1e-6:
                hrot, hpos = self.st.head_pose()
                self.st.set_head(list(hrot), [hpos[0], hpos[1] - dy, hpos[2]])
                for c in (self.st.left, self.st.right):
                    c.pos = [c.pos[0], c.pos[1] - dy, c.pos[2]]
                self.send()
            print("seat", self.seat)
        elif op == "reset":
            self.st = State(self.reset_state.pack())
            self.head_yaw = 0
            self.send()
        elif op == "show":
            hrot, hpos = self.st.head_pose()
            print("head", [round(v, 3) for v in hpos], [round(v, 3) for v in hrot], "seat", self.seat)
            for side, c in (("left", self.st.left), ("right", self.st.right)):
                print(side, [round(v, 3) for v in c.pos], "trig", c.trigger, "grip", c.squeeze, "stick", c.stick,
                      {k: v for k, v in c.b.items() if v and not k.startswith("_")})
        else:
            raise SystemExit("unknown command: " + op)


SOCK = hof2env.XRSIM_SOCK


def daemon():
    import contextlib
    import io
    import os
    try:
        os.unlink(SOCK)
    except FileNotFoundError:
        pass
    srv = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    srv.bind(SOCK)
    srv.listen(8)
    sim = None
    print("xrsim daemon on", SOCK, flush=True)
    while True:
        conn, _ = srv.accept()
        with conn:
            data = b""
            while not data.endswith(b"\n"):
                chunk = conn.recv(65536)
                if not chunk:
                    break
                data += chunk
            out = io.StringIO()
            try:
                if sim is None:
                    sim = Sim()
                with contextlib.redirect_stdout(out):
                    for cmd in data.decode().strip().split(";"):
                        sim.run(cmd.strip())
                reply = "ok\n" + out.getvalue()
            except (SystemExit, OSError) as e:
                if sim is not None:
                    sim.alive = False
                sim = None   # Monado gone or restarted: reconnect on the next command
                reply = "err %s\n" % e
            except Exception as e:
                reply = "err %r\n" % e
            try:
                conn.sendall(reply.encode())
            except OSError:
                pass


def via_daemon(line):
    """Returns the daemon's output, or None when no daemon is running."""
    try:
        c = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        c.connect(SOCK)
    except OSError:
        return None
    with c:
        c.sendall(line.encode() + b"\n")
        buf = b""
        while True:
            chunk = c.recv(65536)
            if not chunk:
                break
            buf += chunk
    return buf.decode()


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    if sys.argv[1] == "--daemon":
        daemon()
        return
    line = " ".join(sys.argv[1:])
    r = via_daemon(line)
    if r is not None:
        status, _, rest = r.partition("\n")
        if rest:
            print(rest, end="")
        if status != "ok":
            raise SystemExit("xrsim: " + status)
        return
    sim = Sim()
    for cmd in line.split(";"):
        sim.run(cmd.strip())
    sim.s.close()


if __name__ == "__main__":
    main()

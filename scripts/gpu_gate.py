#!/usr/bin/env python3
"""GPU room for a test game of environment N >= 2 (coordinator 2026-10-11 23:20, after a GPU reset: a 9 GB voice job
plus both games filled the 16 GB of VRAM and the ring timed out).

  gpu_gate.py wait [--min-free GB] [--max-used GB] [--timeout S]
      Start the quiet holder (below), so GPU voice jobs yield and free their memory, then wait until the GPU has
      room: free VRAM >= --min-free (4 GB) and used VRAM <= --max-used (12.5 GB: the user's Ollama model holds 8.4 GB
      from boot, 2026-10-17; a test game about 1.5 GB). Exit 0 when it has; exit 3 with
      a clear message after --timeout (600 s), the holder stopped again.
  gpu_gate.py hold
      (detached; started by `wait`) keep /run/user/UID/vr-session/quiet fresh (every 30 s) while this environment's
      test game runs: from before its boot (4 min grace) until 60 s after it has gone; then delete the flag.
      One holder per environment (testdata/quiethold.pid).
  gpu_gate.py status      VRAM used / free, the holder's pid, the flag's age

run.sh calls `wait` before every launch in environment N >= 2 (environment 1 is unchanged).
"""
import os
import signal
import subprocess
import sys
import time

import hof2env  # this worktree's test environment (docs/two-environments.md)

UID = os.getuid()
FLAG = "/run/user/%d/vr-session/quiet" % UID
PIDFILE = os.path.join(hof2env.TESTDATA, "quiethold.pid")
GB = 1024 ** 3


def vram():
    """(used, total) bytes of the GPU's VRAM."""
    cands = ["/sys/bus/pci/devices/0000:0d:00.0"]
    try:
        cands += ["/sys/class/drm/%s/device" % c for c in sorted(os.listdir("/sys/class/drm")) if c.startswith("card") and "-" not in c]
    except OSError:
        pass
    for d in cands:
        try:
            used = int(open(d + "/mem_info_vram_used").read())
            total = int(open(d + "/mem_info_vram_total").read())
            if total > 0:
                return used, total
        except (OSError, ValueError):
            continue
    return None, None


def touch():
    try:
        os.makedirs(os.path.dirname(FLAG), exist_ok=True)
        with open(FLAG, "a"):
            os.utime(FLAG, None)
    except OSError:
        pass


def holder_pid():
    try:
        pid = int(open(PIDFILE).read().strip())
        os.kill(pid, 0)
        with open("/proc/%d/cmdline" % pid, "rb") as f:
            if b"gpu_gate.py" in f.read():
                return pid
    except (OSError, ValueError):
        pass
    return None


def hold():
    if holder_pid() and holder_pid() != os.getpid():
        return 0
    with open(PIDFILE, "w") as f:
        f.write(str(os.getpid()))
    stop = []
    signal.signal(signal.SIGTERM, lambda *a: stop.append(1))
    start = time.time()
    last_alive = None
    try:
        while not stop:
            touch()
            if hof2env.game_alive():
                last_alive = time.time()
            elif last_alive is None:
                if time.time() - start > 240:
                    break          # never booted
            elif time.time() - last_alive > 60:
                break              # the game has gone
            for _ in range(30):
                if stop:
                    break
                time.sleep(1)
    finally:
        try:
            os.remove(FLAG)
        except OSError:
            pass
        try:
            if open(PIDFILE).read().strip() == str(os.getpid()):
                os.remove(PIDFILE)
        except OSError:
            pass
    return 0


def start_holder():
    if holder_pid():
        touch()
        return
    touch()
    subprocess.Popen([sys.executable, os.path.abspath(__file__), "hold"], stdin=subprocess.DEVNULL,
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
    time.sleep(0.3)


def stop_holder():
    pid = holder_pid()
    if pid:
        os.kill(pid, signal.SIGTERM)


def wait(min_free, max_used, timeout):
    start_holder()
    t0 = time.time()
    said = 0
    while True:
        used, total = vram()
        if used is None:
            print("gpu_gate: VRAM not readable - not waiting", flush=True)
            return 0
        free = total - used
        if free >= min_free * GB and used <= max_used * GB:
            print("gpu_gate: VRAM used %.1f / %.1f GB, free %.1f GB - room for the game" % (used / GB, total / GB, free / GB), flush=True)
            return 0
        waited = time.time() - t0
        if waited > timeout:
            stop_holder()
            print("gpu_gate: GIVING UP after %.0f s: VRAM used %.1f / %.1f GB (free %.1f GB; need >= %.1f GB free and <= %.1f GB "
                  "used). Something else holds the GPU's memory (a voice job that ignores the quiet flag?); not booting into a "
                  "full GPU." % (waited, used / GB, total / GB, free / GB, min_free, max_used), file=sys.stderr, flush=True)
            return 3
        if waited >= said:
            print("gpu_gate: waiting for VRAM: used %.1f / %.1f GB, free %.1f GB (quiet flag held)" % (used / GB, total / GB, free / GB), flush=True)
            said = waited + 30
        time.sleep(2)


def main(a):
    cmd = a[0] if a else "status"
    if cmd == "hold":
        return hold()
    if cmd == "wait":
        def opt(name, d):
            return float(a[a.index(name) + 1]) if name in a else d
        return wait(opt("--min-free", 4.0), opt("--max-used", 12.5), opt("--timeout", 600))
    if cmd == "status":
        used, total = vram()
        age = time.time() - os.path.getmtime(FLAG) if os.path.exists(FLAG) else None
        print("VRAM used %s / %s GB; holder %s; quiet flag %s" % (
            "%.1f" % (used / GB) if used else "?", "%.1f" % (total / GB) if total else "?", holder_pid() or "-",
            ("%.0f s old" % age) if age is not None else "absent"))
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

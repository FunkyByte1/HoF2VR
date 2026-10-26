"""Which test environment this copy of the harness drives (docs/two-environments.md); the rules of hof2env.sh.

  H           this worktree's HandOfFate2 folder (from this file's location; HOF2VR_ROOT overrides)
  ENV         1 = the main worktree: the Steam install, Monado on $XDG_RUNTIME_DIR/monado_comp_ipc, xrsim TCP 4242
              N >= 2 = a linked worktree whose HandOfFate2/.hof2vr-env says N: its own game copy, Monado and port
  MAIN        the main worktree's HandOfFate2 (shared read-only material)
  G, GLOG     the game folder and its BepInEx log
  TESTDATA    the mod's test folder (command channel, status.json, shots, isolated saves)
  MONADO_RT   the runtime dir of this environment's Monado (its socket and pid file)
  XRSIM_PORT  this environment's Monado remote-driver port; XRSIM_SOCK the xrsim daemon's socket
"""
import os
import sys


def _die(msg):
    sys.stderr.write("hof2env: %s\n" % msg)
    raise SystemExit(2)


H = os.path.realpath(os.environ.get("HOF2VR_ROOT") or os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
_wt = os.path.dirname(H)
if os.path.isdir(os.path.join(_wt, ".git")):
    MAIN = H
    _env = 1
elif os.path.isfile(os.path.join(_wt, ".git")):
    with open(os.path.join(_wt, ".git")) as _f:
        _gd = _f.read().strip()
    if not _gd.startswith("gitdir: ") or "/.git/worktrees/" not in _gd:
        _die("cannot find the main worktree from %s/.git" % _wt)
    MAIN = os.path.join(_gd[len("gitdir: "):].split("/.git/worktrees/")[0], "HandOfFate2")
    try:
        with open(os.path.join(H, ".hof2vr-env")) as _f:
            _env = int("".join(c for c in _f.read() if c.isdigit()) or 0)
    except OSError:
        _env = 0
    if not _env:
        _die("the worktree %s has no test environment yet (HOF2VR_ENV=N scripts/testenv.sh setup)" % _wt)
    if _env < 2:
        _die("environment 1 belongs to the main worktree %s" % os.path.dirname(MAIN))
else:
    _die("%s is not inside a git worktree of the Flat2VR workspace" % H)
if os.environ.get("HOF2VR_ENV") and os.environ["HOF2VR_ENV"] != str(_env):
    _die("HOF2VR_ENV=%s, but %s is environment %d - run the scripts of environment %s's own worktree"
         % (os.environ["HOF2VR_ENV"], H, _env, os.environ["HOF2VR_ENV"]))
ENV = _env
os.environ["HOF2VR_ENV"] = str(ENV)   # children (run.sh, monado.sh ...) agree with us
SHARED = os.path.join(os.path.dirname(MAIN), "shared")
REAL_RT = os.environ.get("XDG_RUNTIME_DIR") or "/run/user/%d" % os.getuid()
if "/hof2vr-env" in REAL_RT:
    REAL_RT = REAL_RT.split("/hof2vr-env")[0]
STEAM_G = os.path.expanduser("~/.local/share/Steam/steamapps/common/Hand of Fate 2")   # the user's = env 1's
if ENV == 1:
    G = STEAM_G
    MONADO_RT = REAL_RT
    XRSIM_PORT = 4242
else:
    G = os.path.expanduser("~/Games/hof2-env%d/Hand of Fate 2" % ENV)
    MONADO_RT = os.path.join(REAL_RT, "hof2vr-env%d" % ENV)
    XRSIM_PORT = 4241 + ENV
GLOG = os.path.join(G, "BepInEx", "LogOutput.log")
XRSIM_SOCK = os.path.join(H, "testdata", "xrsim.sock")
MONADO_JSON = "/usr/share/openxr/1/openxr_monado.json"

# The mod's test folder. Env 1 keeps honouring HOF2VR_TEST_DIR as before; env N only accepts its own (a stray
# variable must never point a worker's commands at the other environment's game).
TESTDATA = os.path.join(H, "testdata")
_td = os.environ.get("HOF2VR_TEST_DIR")
if _td:
    if ENV == 1 or os.path.realpath(_td) == os.path.realpath(TESTDATA):
        TESTDATA = _td
    else:
        sys.stderr.write("hof2env: ignoring HOF2VR_TEST_DIR=%s (environment %d uses %s)\n" % (_td, ENV, TESTDATA))


def _environ(pid):
    try:
        with open("/proc/%s/environ" % pid, "rb") as f:
            return f.read().split(b"\0")
    except OSError:
        return None


def pid_env(pid, environ=None):
    """The HOF2VR_ENV the harness put in a process's environment (1 without one). Only for helpers the game starts
    (zenity); games are told apart by their folder (game_env), Monados by their runtime dir (monado_pids)."""
    for kv in (environ if environ is not None else _environ(pid)) or []:
        if kv.startswith(b"HOF2VR_ENV="):
            try:
                return int(kv[11:])
            except ValueError:
                return 1
    return 1


def _comm_pids(comm):
    out = []
    for d in os.listdir("/proc"):
        if d.isdigit():
            try:
                with open("/proc/%s/comm" % d) as f:
                    if f.read().strip() == comm:
                        out.append(int(d))
            except OSError:
                pass
    return out


def _exe(pid):
    try:
        return os.readlink("/proc/%s/exe" % pid)
    except OSError:
        return ""


def game_env(pid):
    """A game's environment, by the folder it runs from: the Steam install = 1, ~/Games/hof2-envN = N (else None)."""
    exe = _exe(pid)
    if exe.startswith(STEAM_G + "/"):
        return 1
    pre = os.path.expanduser("~/Games/hof2-env")
    if exe.startswith(pre):
        n = exe[len(pre):].split("/")[0]
        return int(n) if n.isdigit() else None
    return None


def game_pids(this_env=True):
    """Test instances of the game (HOF2VR_TEST=1), by default only this environment's (running from its folder G)."""
    out = []
    for p in _comm_pids("Hand of Fate 2."):
        env = _environ(p)
        if not env or b"HOF2VR_TEST=1" not in env:
            continue
        if this_env and not _exe(p).startswith(G + "/"):
            continue
        out.append(p)
    return out


def game_alive():
    return bool(game_pids())


def monado_pids():
    """This environment's Monado: the pid in its runtime dir's monado.pid and the owner of its socket (never by name:
    the other environment runs a monado-service too)."""
    import re
    import subprocess
    out = set()
    try:
        with open(os.path.join(MONADO_RT, "monado.pid")) as f:
            p = int("".join(c for c in f.read() if c.isdigit()))
        with open("/proc/%d/comm" % p) as f:
            if f.read().strip() == "monado-service":
                out.add(p)
    except (OSError, ValueError):
        pass
    sock = os.path.join(MONADO_RT, "monado_comp_ipc")
    r = subprocess.run(["ss", "-xlpH"], capture_output=True, text=True)
    for line in r.stdout.splitlines():
        f = line.split()
        if len(f) >= 5 and f[4] == sock:
            out.update(int(x) for x in re.findall(r"pid=(\d+)", line))
    return sorted(out)


def monado_up():
    """This environment's Monado runs and its socket is there."""
    return bool(monado_pids()) and os.path.exists(os.path.join(MONADO_RT, "monado_comp_ipc"))


def xr_env(base=None):
    """Environment for an OpenXR client of this environment's Monado (monado-ctl, hello_xr); env 1: unchanged."""
    env = dict(os.environ if base is None else base)
    env["XR_RUNTIME_JSON"] = MONADO_JSON
    if ENV != 1:
        env["XDG_RUNTIME_DIR"] = MONADO_RT
        env.setdefault("HOF2VR_VRSESSION_STATUS", os.path.join(REAL_RT, "vr-session", "status.json"))
        env.setdefault("PULSE_SERVER", "unix:" + os.path.join(REAL_RT, "pulse", "native"))
        env.setdefault("DBUS_SESSION_BUS_ADDRESS", "unix:path=" + os.path.join(REAL_RT, "bus"))
        wd = env.get("WAYLAND_DISPLAY")
        if wd and not wd.startswith("/"):
            env["WAYLAND_DISPLAY"] = os.path.join(REAL_RT, wd)
    return env


def kill_own_zenity():
    """Close dialogs (zenity) of this environment's game; env 1 also every zenity without an environment (as before)."""
    import signal
    for p in _comm_pids("zenity"):
        if pid_env(p) == ENV:
            try:
                os.kill(p, signal.SIGTERM)
            except OSError:
                pass


def restart_xrsim_daemon(log_path=None):
    """(Re)start this environment's xrsim daemon (after xrsim.py changed): only this worktree's daemon is stopped."""
    import subprocess
    import time
    r = subprocess.run(["bash", "-c", '. "$1/scripts/hof2env.sh"; hof2vr_script_pids xrsim.py --daemon', "-", H],
                       capture_output=True, text=True)
    for p in r.stdout.split():
        try:
            os.kill(int(p), 15)
        except OSError:
            pass
    time.sleep(0.5)
    out = open(log_path or os.path.join(H, "logs", "xrsimd.log"), "a")
    subprocess.Popen(["setsid", "python3", os.path.join(H, "scripts", "xrsim.py"), "--daemon"], stdout=out,
                     stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)
    time.sleep(0.5)


if __name__ == "__main__":
    for k in ("H", "ENV", "MAIN", "G", "GLOG", "TESTDATA", "MONADO_RT", "XRSIM_PORT", "XRSIM_SOCK"):
        print("%s=%s" % (k, globals()[k]))

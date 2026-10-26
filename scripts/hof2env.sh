# hof2env.sh - which test environment this copy of the harness drives (docs/two-environments.md).
# Sourced by the harness scripts (directly or through userguard.sh); scripts/hof2env.py has the same rules for Python.
#   env 1  the main worktree (the one with the .git folder): the Steam install, Monado on
#          $XDG_RUNTIME_DIR/monado_comp_ipc, xrsim on TCP 4242 - exactly as before environments existed.
#   env N  (N >= 2) a linked git worktree whose HandOfFate2/.hof2vr-env says N (scripts/testenv.sh setup): its own
#          reflinked game copy ~/Games/hof2-envN/Hand of Fate 2, its own Monado (runtime dir
#          $XDG_RUNTIME_DIR/hof2vr-envN, so its socket is .../hof2vr-envN/monado_comp_ipc), xrsim on TCP 4241+N,
#          and the worktree's own testdata/ and logs/.
# HOF2VR_ENV, if set, must name this worktree's environment (a mismatch is refused, so a worker can never drive the
# other environment's game with its own build). HOF2VR_ROOT overrides the HandOfFate2 folder derived from this file.
# Sets: H (this worktree's HandOfFate2), HOF2VR_ENV (exported), HOF2VR_MAIN (the main worktree's HandOfFate2: shared
# read-only material), HOF2VR_SHARED (the workspace's shared/: toolchains, caches), G and HOF2VR_GAME_DIR (the game
# folder), HOF2VR_REAL_RT (the session's runtime dir), HOF2VR_MONADO_RT (this env's Monado runtime dir),
# HOF2VR_XRSIM_PORT (this env's Monado remote-driver port), HOF2VR_VD_ENV / HOF2VR_VD_PID (its virtual screen's files).
hof2vr__die() { echo "hof2env: $*" >&2; exit 2; }
H=$(cd "${HOF2VR_ROOT:-$(dirname "${BASH_SOURCE[0]}")/..}" 2>/dev/null && pwd -P) || hof2vr__die "no HandOfFate2 folder"
hof2vr__wt=${H%/*}
if [ -d "$hof2vr__wt/.git" ]; then
  HOF2VR_MAIN=$H
  hof2vr__env=1
elif [ -f "$hof2vr__wt/.git" ]; then
  hof2vr__main=$(sed -n 's|^gitdir: \(.*\)/\.git/worktrees/.*$|\1|p' "$hof2vr__wt/.git")
  [ -d "$hof2vr__main/HandOfFate2" ] || hof2vr__die "cannot find the main worktree from $hof2vr__wt/.git"
  HOF2VR_MAIN=$hof2vr__main/HandOfFate2
  hof2vr__env=$({ tr -dc 0-9 < "$H/.hof2vr-env"; } 2>/dev/null) || true
  [ -n "$hof2vr__env" ] || hof2vr__die "the worktree $hof2vr__wt has no test environment yet (HOF2VR_ENV=N scripts/testenv.sh setup)"
  [ "$hof2vr__env" -ge 2 ] || hof2vr__die "environment 1 belongs to the main worktree ${HOF2VR_MAIN%/*}"
else
  hof2vr__die "$H is not inside a git worktree of the Flat2VR workspace"
fi
if [ -n "${HOF2VR_ENV:-}" ] && [ "$HOF2VR_ENV" != "$hof2vr__env" ]; then
  hof2vr__die "HOF2VR_ENV=$HOF2VR_ENV, but $H is environment $hof2vr__env - run the scripts of environment $HOF2VR_ENV's own worktree"
fi
export HOF2VR_ENV=$hof2vr__env
HOF2VR_SHARED=${HOF2VR_MAIN%/*}/shared
# the session's runtime dir (a shell that already carries an environment's Monado dir gets the session's back)
HOF2VR_REAL_RT=${XDG_RUNTIME_DIR:-/run/user/$(id -u)}
case $HOF2VR_REAL_RT in */hof2vr-env[0-9]*) HOF2VR_REAL_RT=${HOF2VR_REAL_RT%/hof2vr-env*} ;; esac
HOF2VR_STEAM_G="$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2"   # the user's install = environment 1's
if [ "$HOF2VR_ENV" = 1 ]; then
  G=$HOF2VR_STEAM_G
  HOF2VR_GAME_DIR="${HOF2VR_GAME_DIR:-$G}"
  HOF2VR_MONADO_RT=$HOF2VR_REAL_RT
  HOF2VR_XRSIM_PORT=4242
else
  G="$HOME/Games/hof2-env$HOF2VR_ENV/Hand of Fate 2"
  HOF2VR_GAME_DIR=$G   # never inherited: env N only ever touches its own copy
  HOF2VR_MONADO_RT=$HOF2VR_REAL_RT/hof2vr-env$HOF2VR_ENV
  HOF2VR_XRSIM_PORT=$((4241 + HOF2VR_ENV))
fi

# Env N >= 2 writes only into a game folder that says it is env N's copy (testenv.sh setup puts hof2vr_env.txt there);
# env 1's folder is the Steam install and must not carry such a file.
hof2vr_check_game_dir() {
  local mark
  mark=$({ tr -dc 0-9 < "$G/hof2vr_env.txt"; } 2>/dev/null) || true
  if [ "$HOF2VR_ENV" = 1 ]; then
    [ -z "$mark" ] || hof2vr__die "$G carries the marker of environment $mark - not the Steam install"
  else
    [ "$mark" = "$HOF2VR_ENV" ] || hof2vr__die "$G is not environment $HOF2VR_ENV's game copy (no hof2vr_env.txt = $HOF2VR_ENV; scripts/testenv.sh setup)"
  fi
}

# Which environment a process belongs to is told by where it runs from, never by its name (every environment's game
# is "Hand of Fate 2.", every Monado "monado-service"):
#   a game     by its executable: the Steam install = env 1, ~/Games/hof2-envN/ = env N (hof2vr_pid_in_dir, _game_env);
#   a Monado   by its runtime dir: the pid in $HOF2VR_MONADO_RT/monado.pid and the owner of its socket;
#   a script   by its path (hof2vr_script_pids).
# A process that is gone or exiting has no readable executable or environment: it belongs to nobody.
hof2vr_pid_in_dir() {
  local exe
  exe=$(readlink "/proc/$1/exe" 2>/dev/null) || return 1
  case $exe in "$2"/*) return 0 ;; esac
  return 1
}

hof2vr_game_env() {
  local exe n
  exe=$(readlink "/proc/$1/exe" 2>/dev/null) || { echo "?"; return 0; }
  case $exe in
    "$HOF2VR_STEAM_G"/*) echo 1 ;;
    "$HOME"/Games/hof2-env*/*) n=${exe#"$HOME"/Games/hof2-env}; echo "${n%%/*}" ;;
    *) echo "?" ;;
  esac
}

# This environment's Monado service.
hof2vr_monado_pids() {
  local p
  {
    p=$({ tr -dc 0-9 < "$HOF2VR_MONADO_RT/monado.pid"; } 2>/dev/null)
    [ -n "$p" ] && [ "$(cat "/proc/$p/comm" 2>/dev/null)" = monado-service ] && echo "$p"
    ss -xlpH 2>/dev/null | awk -v s="$HOF2VR_MONADO_RT/monado_comp_ipc" '$5 == s' | grep -o 'pid=[0-9]*' | cut -d= -f2
  } | sort -u
  return 0
}

# Processes that run THIS worktree's scripts/FILE (e.g. xrsim.py, monado.sh; the script argument is resolved against
# the process's working directory), optionally only those with ARG among their arguments (e.g. --daemon). Never matches
# the other environment's copy of the script, or a shell whose command line merely mentions it.
hof2vr_script_pids() {
  local file=$1 want=${2:-} me p a cwd hit has
  me=$(realpath -m "$H/scripts/$file")
  for p in $(pgrep -f "${file//./\\.}" 2>/dev/null); do
    [ "$p" = "$$" ] && continue
    cwd=$(readlink "/proc/$p/cwd" 2>/dev/null) || continue
    hit= has=
    while IFS= read -r -d '' a; do
      case $a in
        *"$file") case $a in /*) ;; *) a=$cwd/$a ;; esac
                  [ "$(realpath -m "$a")" = "$me" ] && hit=1 ;;
      esac
      [ -n "$want" ] && [ "$a" = "$want" ] && has=1
    done < "/proc/$p/cmdline" 2>/dev/null
    [ -n "$hit" ] && { [ -z "$want" ] || [ -n "$has" ]; } && echo "$p"
  done
  return 0
}

# Environment for an OpenXR client of this environment's Monado (the game, monado-ctl, hello_xr). Env 1: unchanged.
# Env N: Monado's client library finds the service only through XDG_RUNTIME_DIR, so the client gets env N's
# runtime dir; what else lives in the session's runtime dir is passed by absolute path.
hof2vr_xr_env() {
  [ "$HOF2VR_ENV" = 1 ] && return 0
  export XDG_RUNTIME_DIR="$HOF2VR_MONADO_RT"
  export HOF2VR_VRSESSION_STATUS="${HOF2VR_VRSESSION_STATUS:-$HOF2VR_REAL_RT/vr-session/status.json}"
  export PULSE_SERVER="${PULSE_SERVER:-unix:$HOF2VR_REAL_RT/pulse/native}"
  export PIPEWIRE_RUNTIME_DIR="${PIPEWIRE_RUNTIME_DIR:-$HOF2VR_REAL_RT}"
  export DBUS_SESSION_BUS_ADDRESS="${DBUS_SESSION_BUS_ADDRESS:-unix:path=$HOF2VR_REAL_RT/bus}"
  case "${WAYLAND_DISPLAY:-}" in ""|/*) ;; *) export WAYLAND_DISPLAY="$HOF2VR_REAL_RT/$WAYLAND_DISPLAY" ;; esac
  return 0
}

# A test game keeps its own per-user files: Unity's prefs and analytics (XDG_CONFIG_HOME/unity3d, seeded once from the
# user's prefs so the graphics settings match; the user's own file is only read) in testdata/, and env N also
# OpenComposite's log (XDG_STATE_HOME). OpenVR still finds OpenComposite through ~/.config/openvr (a link), libpulse its
# cookie through ~/.config/pulse. Env 1 too since 2026-10-18 (the bug hunt's finding 3: env 1's test runs wrote the user's
# own prefs - a test quitting for the user flushed the game's save-scum mark there, so the user's next title could get the
# Dealer's save-scum taunt; any game option a test changed carried over into the user's game). Only run.sh and gdbrun.sh
# (test games, HOF2VR_TEST=1) call this; the user's own launches never do.
hof2vr_game_files_env() {
  local x="$H/testdata/xdg-config" u="unity3d/Defiant Development/Hand of Fate 2" d
  mkdir -p "$x/$u"
  for d in openvr pulse; do [ -e "$x/$d" ] || ln -sfn "$HOME/.config/$d" "$x/$d"; done
  if [ ! -f "$x/$u/prefs" ] && [ -f "$HOME/.config/$u/prefs" ]; then cp "$HOME/.config/$u/prefs" "$x/$u/prefs"; fi
  export XDG_CONFIG_HOME="$x"
  [ "$HOF2VR_ENV" = 1 ] && return 0
  mkdir -p "$H/testdata/xdg-state"
  export XDG_STATE_HOME="$H/testdata/xdg-state"
}

# The virtual screen (scripts/vdisplay.sh, 2026-10-17): this environment's headless gamescope. Its X display holds the
# test windows (Monado's compositor, the test game), so they never appear on the user's desktop and KWin never composites
# them. HOF2VR_VDISPLAY=0 turns it off (the windows go on the desktop as before).
HOF2VR_VD_ENV="$H/testdata/vdisplay.env"   # DISPLAY= and XAUTHORITY= for its clients (written by gamescope's child)
HOF2VR_VD_PID="$H/testdata/vdisplay.pid"   # gamescope's pid
hof2vr_vdisplay_on() { [ "${HOF2VR_VDISPLAY:-1}" != 0 ]; }

# This environment's gamescope, then the child it started: the pid in vdisplay.pid when it is a gamescope whose child
# (through gamescope's reaper) carries HOF2VR_VDISPLAY_OF=<this HandOfFate2>. Gamescope itself has file capabilities
# (cap_sys_nice), so its own environment cannot be read; a gamescope the user starts never has such a child.
hof2vr_vdisplay_pids() {
  local g c cc
  g=$({ tr -dc 0-9 < "$HOF2VR_VD_PID"; } 2>/dev/null) || true
  [ -n "$g" ] || return 0
  case $(cat "/proc/$g/comm" 2>/dev/null) in gamescope*) ;; *) return 0 ;; esac
  for c in $(pgrep -P "$g" 2>/dev/null); do
    for cc in "$c" $(pgrep -P "$c" 2>/dev/null); do
      if { tr '\0' '\n' < "/proc/$cc/environ"; } 2>/dev/null | grep -qxF "HOF2VR_VDISPLAY_OF=$H"; then
        echo "$g"; echo "$cc"; return 0
      fi
    done
  done
  return 0
}

# Points this shell's X clients at the virtual screen: its DISPLAY (and XAUTHORITY when its Xwayland has one; gamescope's
# has none), and no WAYLAND_DISPLAY (a client that finds one opens its window on the desktop). Returns 1 when it is not up.
hof2vr_vdisplay_env() {
  local d= x= l
  [ -n "$(hof2vr_vdisplay_pids)" ] && [ -f "$HOF2VR_VD_ENV" ] || return 1
  while IFS= read -r l; do
    case $l in DISPLAY=*) d=${l#DISPLAY=} ;; XAUTHORITY=*) x=${l#XAUTHORITY=} ;; esac
  done < "$HOF2VR_VD_ENV"
  [ -n "$d" ] && [ -S "/tmp/.X11-unix/X${d#:}" ] || return 1
  export DISPLAY="$d"
  if [ -n "$x" ]; then export XAUTHORITY="$x"; else unset XAUTHORITY; fi
  unset WAYLAND_DISPLAY
  return 0
}

# One expected real-save hash for every environment: env N runs the main worktree's savecheck.sh, whose EXPECT the
# game owner keeps current after each of the user's sessions (a branch's copy can be stale).
hof2vr_savecheck() {
  if [ "$HOF2VR_ENV" = 1 ]; then "$H/scripts/savecheck.sh"; else "$HOF2VR_MAIN/scripts/savecheck.sh"; fi
}

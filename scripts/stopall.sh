#!/usr/bin/env bash
# stopall.sh - the emergency stop for the Hand of Fate 2 VR test harness (the coordinator, 2026-10-17). Stops every test
# environment's test games, Monado, xrsim daemon, virtual screen (headless gamescope), test chains and drivers, and
# releases their slot reservations: environment 1, 2 and every other worktree of the workspace. No arguments; works from
# anywhere (a terminal, a desktop shortcut, a hotkey):
#   bash /home/bara/Projects/flat2vr/HandOfFate2/scripts/stopall.sh        (-n: only list what it would stop)
# Only our own processes, each by its PID after checking what it is (never a pattern kill):
#   a test game       "Hand of Fate 2." with HOF2VR_TEST=1 in its environment (any environment's folder);
#   a Monado          monado-service started by monado.sh (P_OVERRIDE_ACTIVE_CONFIG=remote in its environment) that is an
#                     environment's: the pid in its runtime dir's monado.pid, or the owner of its socket;
#   a virtual screen  the gamescope in a worktree's testdata/vdisplay.pid whose child carries that worktree's mark
#                     (scripts/vdisplay.sh);
#   a script          bash / python running a script of a worktree's HandOfFate2/scripts/ (chains, tests, autoplay, the
#                     xrsim daemon, run.sh's keepers, monado.sh's guard, reserve.sh's keeper), except install.sh,
#                     uninstall.sh, handover.sh, build.sh and savecheck.sh (never cut off half-way) and this script.
# Never the user's game, Steam, WiVRn, or a gamescope the user starts. One line per thing it stopped; without a terminal
# (a shortcut, a hotkey) also a desktop notification. The main worktree's logs/stopall.log keeps every run.
set -u
chrt -o -p 0 $$ 2>/dev/null || true   # a hotkey's process may inherit keyd's real-time policy
DRY=; [ "${1:-}" = -n ] || [ "${1:-}" = --dry-run ] && DRY=1
ME=$(readlink -f "${BASH_SOURCE[0]}")
RT=${XDG_RUNTIME_DIR:-/run/user/$(id -u)}; RT=${RT%/hof2vr-env*}
VRS=$RT/vr-session
OUT=()
say() { OUT+=("$*"); echo "$*"; }

# --- the environments: the main worktree (env 1) and every linked worktree with a HandOfFate2/.hof2vr-env
WT=$(cd "$(dirname "$ME")/../.." && pwd -P)
if [ -d "$WT/.git" ]; then MAIN=$WT; else MAIN=$(sed -n 's|^gitdir: \(.*\)/\.git/worktrees/.*$|\1|p' "$WT/.git" 2>/dev/null); fi
[ -d "$MAIN/HandOfFate2" ] || { echo "stopall: cannot find the workspace from $ME" >&2; exit 2; }
declare -A ENVOF=()   # HandOfFate2 folder -> environment number
ENVOF[$MAIN/HandOfFate2]=1
for w in $(git -C "$MAIN" worktree list --porcelain 2>/dev/null | sed -n 's/^worktree //p') "$MAIN"-env*; do
  [ -d "$w/HandOfFate2" ] && [ "$w" != "$MAIN" ] || continue
  n=$({ tr -dc 0-9 < "$w/HandOfFate2/.hof2vr-env"; } 2>/dev/null) || true
  [ -n "$n" ] && [ "$n" -ge 2 ] 2>/dev/null && ENVOF[$(cd "$w/HandOfFate2" && pwd -P)]=$n
done
mrt() { if [ "$1" = 1 ]; then echo "$RT"; else echo "$RT/hof2vr-env$1"; fi; }
gdir() { if [ "$1" = 1 ]; then echo "$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2"; else echo "$HOME/Games/hof2-env$1/Hand of Fate 2"; fi; }

environ_has() { { tr '\0' '\n' < "/proc/$1/environ"; } 2>/dev/null | grep -qxF "$2"; }
game_env() {
  local exe n
  exe=$(readlink "/proc/$1/exe" 2>/dev/null) || { echo "?"; return; }
  case $exe in
    "$(gdir 1)"/*) echo 1 ;;
    "$HOME"/Games/hof2-env*/*) n=${exe#"$HOME"/Games/hof2-env}; echo "${n%%/*}" ;;
    *) echo "?" ;;
  esac
}

# --- what to stop
ANC=" "; p=$$; while [ -n "$p" ] && [ "$p" -gt 1 ]; do ANC+="$p "; p=$(awk '/^PPid:/ {print $2}' "/proc/$p/status" 2>/dev/null); done
declare -A SCRIPT_PIDS=()   # "env<TAB>label" -> pids
scan_scripts() {
  local d p a0 base a i rel h hit label cwd
  SCRIPT_PIDS=()
  for d in /proc/[0-9]*; do
    p=${d#/proc/}
    case $ANC in *" $p "*) continue ;; esac
    mapfile -d '' a 2>/dev/null < "$d/cmdline" || continue
    [ ${#a[@]} -ge 2 ] || continue
    base=${a[0]##*/}
    case $base in bash|sh|python|python3|python3.[0-9]*) ;; *) continue ;; esac
    hit=
    for ((i = 1; i < ${#a[@]}; i++)); do   # the first argument that is not an option is the script
      case ${a[i]} in -c) break ;; -*) continue ;; esac
      hit=${a[i]}; break
    done
    case $hit in *.sh|*.py) ;; *) continue ;; esac
    if [ "${hit#/}" = "$hit" ]; then cwd=$(readlink "$d/cwd" 2>/dev/null) || continue; hit=$cwd/$hit; fi
    hit=$(realpath -m "$hit" 2>/dev/null) || continue
    for h in "${!ENVOF[@]}"; do
      case $hit in "$h"/scripts/*) ;; *) continue ;; esac
      rel=${hit#"$h"/scripts/}
      case $rel in stopall.sh|install.sh|uninstall.sh|handover.sh|build.sh|savecheck.sh) continue 2 ;; esac
      label=$rel
      [ "$rel" = xrsim.py ] && [[ " ${a[*]} " == *" --daemon "* ]] && label="xrsim daemon"
      [ "$rel" = monado.sh ] && [[ " ${a[*]} " == *" guard "* ]] && label="Monado's user-first guard"
      [ "$rel" = reserve.sh ] && label="slot reservation keeper"
      SCRIPT_PIDS["${ENVOF[$h]}	$label"]+="$p "
      continue 2
    done
  done
}
scan_games() {   # "pid env" of every test instance (HOF2VR_TEST=1), never the user's game
  local p
  for p in $(pgrep -x 'Hand of Fate 2.' 2>/dev/null); do environ_has "$p" HOF2VR_TEST=1 && echo "$p $(game_env "$p")"; done
}
scan_monados() {   # "pid env" of every environment's Monado that monado.sh started
  local h n r p
  for h in "${!ENVOF[@]}"; do
    n=${ENVOF[$h]}; r=$(mrt "$n")
    {
      p=$({ tr -dc 0-9 < "$r/monado.pid"; } 2>/dev/null) && [ -n "$p" ] && echo "$p"
      ss -xlpH 2>/dev/null | awk -v s="$r/monado_comp_ipc" '$5 == s' | grep -o 'pid=[0-9]*' | cut -d= -f2
    } | sort -u | while read -r p; do
      [ "$(cat "/proc/$p/comm" 2>/dev/null)" = monado-service ] && environ_has "$p" P_OVERRIDE_ACTIVE_CONFIG=remote && echo "$p $n"
    done
  done
}
vd_of() {   # "gamescope child" of the worktree H's virtual screen (as hof2env.sh's hof2vr_vdisplay_pids)
  local g c cc
  g=$({ tr -dc 0-9 < "$1/testdata/vdisplay.pid"; } 2>/dev/null) || true
  [ -n "$g" ] || return 0
  case $(cat "/proc/$g/comm" 2>/dev/null) in gamescope*) ;; *) return 0 ;; esac
  for c in $(pgrep -P "$g" 2>/dev/null); do
    for cc in "$c" $(pgrep -P "$c" 2>/dev/null); do
      environ_has "$cc" "HOF2VR_VDISPLAY_OF=$1" && { echo "$g $cc"; return 0; }
    done
  done
}

alive() { local p; for p in "$@"; do kill -0 "$p" 2>/dev/null && echo "$p"; done; }
waitgone() {   # up to $1 s for the pids to end
  local t=$1 i; shift
  for ((i = 0; i < t * 5; i++)); do [ -n "$(alive "$@")" ] || return 0; sleep 0.2; done
  return 1
}

pass() {   # one sweep: scripts first (so nothing starts again), then the games and the Monados
  local k pids p e all=() line
  scan_scripts
  for k in "${!SCRIPT_PIDS[@]}"; do
    pids=${SCRIPT_PIDS[$k]}
    say "env ${k%%	*}: ${DRY:+would stop }${k#*	} (pid ${pids% })"
    [ -n "$DRY" ] || kill -TERM $pids 2>/dev/null
    all+=($pids)
  done
  [ -z "$DRY" ] && [ ${#all[@]} -gt 0 ] && sleep 0.5
  while read -r p e; do
    [ -n "$p" ] || continue
    say "env $e: ${DRY:+would stop }test game (pid $p)"; [ -n "$DRY" ] || kill -TERM "$p" 2>/dev/null; all+=("$p")
  done <<< "$(scan_games)"
  while read -r p e; do
    [ -n "$p" ] || continue
    say "env $e: ${DRY:+would stop }Monado (pid $p)"; [ -n "$DRY" ] || kill -TERM "$p" 2>/dev/null; all+=("$p")
  done <<< "$(scan_monados)"
  [ -n "$DRY" ] && return 0
  if [ ${#all[@]} -gt 0 ] && ! waitgone 4 "${all[@]}"; then
    for p in $(alive "${all[@]}"); do kill -KILL "$p" 2>/dev/null; done
    waitgone 2 "${all[@]}" || say "STILL RUNNING after SIGKILL: pid $(alive "${all[@]}" | tr '\n' ' ')"
  fi
  STOPPED=$(( ${STOPPED:-0} + ${#all[@]} ))
}

STOPPED=0
pass
[ -z "$DRY" ] && [ "$STOPPED" -gt 0 ] && { STOPPED=0; pass; }   # anything a dying chain or run.sh started meanwhile
# the virtual screens (once their clients are gone)
for h in "${!ENVOF[@]}"; do
  read -r g c <<< "$(vd_of "$h")"
  [ -n "${g:-}" ] || continue
  n=${ENVOF[$h]}
  say "env $n: ${DRY:+would stop }virtual screen (gamescope pid $g)"
  [ -n "$DRY" ] && continue
  kill -TERM "$c" 2>/dev/null
  waitgone 5 "$g" || { kill -TERM "$g" 2>/dev/null; waitgone 2 "$g" || kill -KILL "$g" 2>/dev/null; }
  rm -f "$h/testdata/vdisplay.env" "$h/testdata/vdisplay.pid"
done
# the slot: reservations, game and waiting flags; Monado sockets and the game folders' heartbeats
for h in "${!ENVOF[@]}"; do
  n=${ENVOF[$h]}
  if [ -e "$VRS/reserved-env$n" ]; then
    if [ -n "$DRY" ]; then say "env $n: would release the slot reservation"; else say "env $n: released the slot reservation"; fi
  fi
  [ -n "$DRY" ] && continue
  rm -f "$VRS/reserved-env$n" "$h/testdata/reserve.pid" "$VRS/game-env$n" "$VRS/waiting-env$n" "$(gdir "$n")/hof2vr_test_running"
  [ -n "$(scan_monados | awk -v n="$n" '$2 == n')" ] || rm -f "$(mrt "$n")/monado_comp_ipc"
done
[ ${#OUT[@]} -gt 0 ] || say "nothing to stop: no test game, Monado, virtual screen or test script runs"
[ -n "$DRY" ] && exit 0
TS=$(date '+%F %T')
mkdir -p "$MAIN/HandOfFate2/logs" && for l in "${OUT[@]}"; do printf '%s stopall: %s\n' "$TS" "$l"; done >> "$MAIN/HandOfFate2/logs/stopall.log"
if [ ! -t 1 ] && command -v notify-send > /dev/null; then
  notify-send -a "Hand of Fate 2 VR" "Test harness stopped" "$(printf '%s\n' "${OUT[@]}" | head -12)" 2>/dev/null || true
fi
exit 0

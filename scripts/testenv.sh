#!/usr/bin/env bash
# Test environments: two (or more) workers build and test in the simulator side by side (docs/two-environments.md).
#   testenv.sh status           this worktree's environment, and what every environment runs right now
#   testenv.sh check            isolation check of what runs now: each game on its own Monado, port, folders and
#                               files; no file open in two environments' games. Exit 1 on a problem.
#   testenv.sh savecheck        the user's real save, against the main worktree's expected hash
#   testenv.sh smoke [MINUTES]  this environment end to end: Monado, install, boot to the title, a shot, autoplay
#                               (default 2 min), a shot, `check` while it runs, stop, savecheck
#   testenv.sh quiet CMD...     run CMD (a timing or load measurement) while heavy background jobs pause: keeps
#                               $XDG_RUNTIME_DIR/vr-session/quiet fresh (every 30 s), deletes it afterwards
#   HOF2VR_ENV=N testenv.sh setup [--refresh-game]
#                               (once, in a linked worktree; N >= 2) environment N: the worktree marker, read-only
#                               material reflinked from the main worktree, the game copy ~/Games/hof2-envN/ (reflinks
#                               of the Steam install; --refresh-game copies it again, e.g. after a game update)
# Run it OUTSIDE the Bash sandbox (it looks at the host's processes).
set -uo pipefail
SCRIPTS=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)
STEAM_G="$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2"

setup() {
  local wt n
  wt=$(cd "$SCRIPTS/../.." && pwd -P)
  [ -f "$wt/.git" ] || { echo "setup is for a linked worktree (git worktree add ...); $wt is the main worktree = environment 1" >&2; exit 2; }
  n=${HOF2VR_ENV:-$({ tr -dc 0-9 < "$SCRIPTS/../.hof2vr-env"; } 2>/dev/null)}
  [ -n "$n" ] && [ "$n" -ge 2 ] 2>/dev/null || { echo "HOF2VR_ENV=N (N >= 2) testenv.sh setup" >&2; exit 2; }
  echo "$n" > "$SCRIPTS/../.hof2vr-env"
  . "$SCRIPTS/userguard.sh"   # H, G, HOF2VR_ENV, HOF2VR_MAIN ... now for environment N
  hof2vr_guard
  echo "environment $HOF2VR_ENV: worktree $wt (branch $(git -C "$wt" branch --show-current 2>/dev/null))"
  # read-only material the harness and the build need (ignored by git, so not in a fresh worktree): reflinks of the
  # main worktree's copies, so nothing here can ever change environment 1's files
  local r
  for r in reference downloads/bepinex downloads/openvr downloads/unity-linux-support downloads/steamaudio-4.8.1 \
           backups/20261010-063954-pre-mod dist/data.unity3d dist/data-hrtf.unity3d dist/hof2vr_limbo.pack; do
    if [ -e "$H/$r" ]; then echo "  have $r"; continue; fi
    [ -e "$HOF2VR_MAIN/$r" ] || { echo "  (the main worktree has no $r)"; continue; }
    mkdir -p "$(dirname "$H/$r")"
    cp -a --reflink=always "$HOF2VR_MAIN/$r" "$H/$r" && echo "  copied $r (reflinks)"
  done
  # save snapshots: the ones this environment does not have yet (never overwritten)
  mkdir -p "$H/testdata/snapshots" "$H/logs"
  local s k=0
  for s in "$HOF2VR_MAIN/testdata/snapshots/"*.profile; do
    [ -e "$s" ] && [ ! -e "$H/testdata/snapshots/${s##*/}" ] && cp --reflink=auto "$s" "$H/testdata/snapshots/" && k=$((k + 1))
  done
  echo "  snapshots: $k new, $(ls "$H/testdata/snapshots" | wc -l) in testdata/snapshots"
  # the game copy
  if [ "${1:-}" = "--refresh-game" ] && [ -d "$G" ]; then
    [ -z "$(hof2vr_test_pids)" ] || { echo "environment $HOF2VR_ENV's game runs: scripts/stop.sh first" >&2; exit 2; }
    [ "$({ tr -dc 0-9 < "$G/hof2vr_env.txt"; } 2>/dev/null)" = "$HOF2VR_ENV" ] || { echo "$G is not environment $HOF2VR_ENV's copy - not removing it" >&2; exit 2; }
    rm -rf "$G" && echo "  removed the old game copy"
  fi
  if [ ! -d "$G" ]; then
    [ -x "$STEAM_G/Hand of Fate 2.x86_64" ] || { echo "no Steam install at $STEAM_G" >&2; exit 2; }
    mkdir -p "${G%/*}"
    cp -a --reflink=always "$STEAM_G" "${G%/*}/" || { echo "reflink copy failed (same btrfs filesystem?)" >&2; exit 1; }
    # environment 1's run state and the user's own session logs stay behind
    rm -f "$G/hof2vr_test_running" "$G/hof2vr_stop_tests" "$G/hof2vr_testmode.txt" "$G/BepInEx/LogOutput.log"
    rm -rf "$G/BepInEx/hof2vr_sessions"
    echo "$HOF2VR_ENV" > "$G/hof2vr_env.txt"
    echo "  game copy: $G ($(du -sh --apparent-size "$G" | cut -f1) apparent, shared on disk with the Steam install)"
  else
    echo "  game copy: $G (exists)"
  fi
  hof2vr_check_game_dir
  echo "next: scripts/build.sh && scripts/monado.sh start && python3 scripts/session.py start   (see docs/two-environments.md)"
}

envof_path() {   # which environment a worktree's HandOfFate2 folder is: 1 for the main worktree, else its marker
  local h=$1
  if [ -d "${h%/*}/.git" ]; then echo 1; else { tr -dc 0-9 < "$h/.hof2vr-env"; } 2>/dev/null || echo "?"; fi
}
envof_rt() {     # which environment a Monado runtime dir is
  case $1 in
    "$HOF2VR_REAL_RT") echo 1 ;;
    "$HOF2VR_REAL_RT"/hof2vr-env*) echo "${1##*/hof2vr-env}" ;;
    *) echo "?" ;;
  esac
}
envof_xrsim() {  # pid of an xrsim daemon -> "<env> <its HandOfFate2>"
  local a h
  a=$({ tr '\0' '\n' < "/proc/$1/cmdline"; } 2>/dev/null | grep 'xrsim\.py$' | head -1)
  [[ $a == /* ]] || a=$(readlink "/proc/$1/cwd")/$a
  h=$(realpath -m "${a%/scripts/xrsim.py}")
  echo "$(envof_path "$h") $h"
}
port_of_env() { if [ "$1" = 1 ]; then echo 4242; else echo $((4241 + $1)); fi; }
envvar() { { tr '\0' '\n' < "/proc/$1/environ"; } 2>/dev/null | sed -n "s/^$2=//p" | head -1; }
tcp_listen() { ss -ltnpH 2>/dev/null | grep "pid=$1," | awk '{print $4}' | sed 's/.*://' | head -1; }
xrsim_daemons() { local p; for p in $(pgrep -f 'xrsim\.py'); do { tr '\0' '\n' < "/proc/$p/cmdline"; } 2>/dev/null | grep -qx -- --daemon && echo "$p"; done; }

status() {
  . "$SCRIPTS/userguard.sh"
  local p rt
  echo "this worktree: environment $HOF2VR_ENV"
  echo "  harness   $H"
  echo "  game      $G$([ -d "$G" ] || echo '  (missing: testenv.sh setup)')"
  echo "  monado    $HOF2VR_MONADO_RT/monado_comp_ipc, remote driver TCP $HOF2VR_XRSIM_PORT"
  echo "running now (every environment):"
  for p in $(hof2vr_game_pids); do
    if hof2vr_is_test_pid "$p"; then
      echo "  game    pid $p  env $(hof2vr_game_env "$p") test instance, test dir $(envvar "$p" HOF2VR_TEST_DIR)"
    elif hof2vr_user_game_pids | grep -qx "$p"; then
      echo "  game    pid $p  THE USER'S GAME"
    else
      echo "  game    pid $p  not a test instance: $(readlink "/proc/$p/exe")"
    fi
  done
  for p in $(pgrep -x monado-service); do
    rt=$(envvar "$p" XDG_RUNTIME_DIR); rt=${rt:-$HOF2VR_REAL_RT}
    echo "  monado  pid $p  env $(envof_rt "$rt"), runtime dir $rt, TCP $(tcp_listen "$p")"
  done
  for p in $(xrsim_daemons); do echo "  xrsim   pid $p  env $(envof_xrsim "$p")"; done
  echo "  vr-session: $(cat "$HOF2VR_REAL_RT/vr-session/status.json" 2>/dev/null || echo 'no status file')"
}

# Who is connected to which Monado: "<client pid> <socket path>" for every client of a socket ending in
# /monado_comp_ipc (server side from /proc/net/unix, client pid from `ss -xp`, which knows the peer).
monado_clients() {
  local ssout; ssout=$(ss -xpH state established 2>/dev/null)
  awk 'NR > 1 && $6 == "03" && $8 ~ /\/monado_comp_ipc$/ {print $7, $8}' /proc/net/unix | while read -r ino path; do
    local peer owners
    peer=$(awk -v i="$ino" '$5 == i {print $7; exit}' <<< "$ssout")   # (state filter: no State column)
    [ -n "$peer" ] || continue
    owners=$(awk -v i="$peer" '$5 == i {print; exit}' <<< "$ssout" | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u)
    for o in $owners; do echo "$o $path"; done
  done
}

check() {
  . "$SCRIPTS/userguard.sh"
  local bad=0 p c e etd ert econ td rt mine port clients files
  say() { echo "  $*"; }
  no() { echo "  PROBLEM: $*"; bad=1; }
  clients=$(monado_clients)
  echo "test instances (game folder / test dir / Monado runtime dir / connected Monado must be one environment):"
  for p in $(hof2vr_all_test_pids); do
    e=$(hof2vr_game_env "$p"); td=$(envvar "$p" HOF2VR_TEST_DIR); rt=$(envvar "$p" XDG_RUNTIME_DIR); rt=${rt:-$HOF2VR_REAL_RT}
    etd=$(envof_path "${td%/testdata}"); ert=$(envof_rt "$rt")
    mine=$(awk -v p="$p" '$1 == p {print $2}' <<< "$clients" | sort -u)
    if [ -n "$mine" ]; then econ=$(envof_rt "${mine%/monado_comp_ipc}"); else econ=-; fi
    say "pid $p: folder env $e, test dir env $etd, runtime dir env $ert, connected to env $econ's Monado"
    [ "$e" != "?" ] || no "pid $p runs from an unknown folder: $(readlink "/proc/$p/exe")"
    [ "$etd" = "$e" ] || no "env $e's game uses environment $etd's test dir ($td)"
    [ "$ert" = "$e" ] || no "env $e's game looks for Monado in environment $ert's runtime dir ($rt)"
    [ "$econ" = - ] || [ "$econ" = "$e" ] || no "env $e's game is connected to environment $econ's Monado"
  done
  echo "monado services:"
  for p in $(pgrep -x monado-service); do
    rt=$(envvar "$p" XDG_RUNTIME_DIR); rt=${rt:-$HOF2VR_REAL_RT}; e=$(envof_rt "$rt"); port=$(tcp_listen "$p")
    say "pid $p: env $e (runtime dir $rt), TCP ${port:-none}"
    [ "$port" = "$(port_of_env "$e")" ] || no "env $e's Monado listens on TCP ${port:-none} (want $(port_of_env "$e"))"
    for c in $(awk -v s="$rt/monado_comp_ipc" '$2 == s {print $1}' <<< "$clients" | sort -u); do
      [ "$(cat "/proc/$c/comm" 2>/dev/null)" = "Hand of Fate 2." ] || continue
      [ "$(hof2vr_game_env "$c")" = "$e" ] || no "env $e's Monado serves env $(hof2vr_game_env "$c")'s game (pid $c)"
    done
  done
  echo "xrsim daemons:"
  for p in $(xrsim_daemons); do
    set -- $(envof_xrsim "$p"); e=$1
    port=$(ss -tnpH state established 2>/dev/null | grep "pid=$p," | awk '{print $4}' | sed 's/.*://' | head -1)
    say "pid $p: env $e ($2), TCP ${port:-not connected yet}"
    [ -z "$port" ] || [ "$port" = "$(port_of_env "$e")" ] || no "env $e's xrsim daemon drives TCP $port (want $(port_of_env "$e"))"
  done
  echo "files open in two environments' games (system files, sockets and pipes left out):"
  files=$(for p in $(hof2vr_all_test_pids); do
    e=$(hof2vr_game_env "$p")
    for c in /proc/$p/fd/*; do readlink "$c"; done 2>/dev/null | grep '^/' | grep -v -E '^/(dev|proc|sys|usr|memfd)' | sed "s|^|$e |"
  done | sort -u | awk '{e=$1; $1=""; f=substr($0,2); n[f]++; w[f]=w[f] " " e} END {for (f in n) if (n[f] > 1) print f " (envs" w[f] ")"}')
  if [ -n "$files" ]; then while read -r l; do no "shared: $l"; done <<< "$files"; else say "none"; fi
  [ $bad = 0 ] && echo "isolation OK" || echo "ISOLATION PROBLEMS"
  return $bad
}

smoke() {
  . "$SCRIPTS/userguard.sh"
  local mins=${1:-2} tag="E${HOF2VR_ENV}smoke" t0
  hof2vr_guard
  hof2vr_savecheck || exit 3
  [ -f "$H/build/bin/HoF2VR.dll" ] || "$H/scripts/build.sh" || exit 1
  [ -n "$(hof2vr_monado_pids)" ] || "$H/scripts/monado.sh" start || exit 1
  t0=$(date +%s)
  # (session.py: stop, the magician save, install, boot, the title - shot -, Continue, the table)
  python3 "$H/scripts/session.py" start --runtime monado --snapshot magician --shot "${tag}_title" --to table \
    || { echo "SMOKE: no table"; exit 1; }
  echo "SMOKE: title and table after $(( $(date +%s) - t0 )) s"
  echo "SMOKE: autoplay $mins min"
  python3 "$H/scripts/autoplay.py" "$mins" > "$H/logs/${tag}-autoplay.out" 2>&1 &
  local ap=$!
  sleep 30; check; sleep 1
  wait $ap; echo "SMOKE: autoplay exit $?; states: $(grep -o 'states visited: .*' "$H/logs/autoplay.log" | tail -1)"
  python3 "$H/scripts/cmd.py" "shot ${tag}_end" 10; sleep 3   # (the capture is written a few frames later)
  ls -la "$H/testdata/shots/${tag}_"* 2>/dev/null | awk '{print "SMOKE: shot " $NF " " $5 " bytes"}'
  "$H/scripts/stop.sh"
}

# Heavy background jobs (voice renders on CPU/GPU) keep running during simulator sessions, except while this flag is
# fresher than 120 s (shared/tools/vr-session/README.md): measurements keep it fresh and delete it afterwards. (If the
# other environment measures at the same time, its own loop puts the flag back within 30 s.)
quiet() {
  local f rc
  f="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"; f="${f%/hof2vr-env*}/vr-session/quiet"
  ( while :; do touch "$f" 2>/dev/null; sleep 30; done ) &
  local tp=$!
  sleep 0.2
  "$@"; rc=$?
  kill "$tp" 2>/dev/null; wait "$tp" 2>/dev/null
  rm -f "$f"
  return $rc
}

case ${1:-status} in
  setup) shift; setup "$@" ;;
  quiet) shift; quiet "$@" ;;
  status) status ;;
  check) check ;;
  savecheck) . "$SCRIPTS/userguard.sh"; hof2vr_savecheck ;;
  smoke) shift; smoke "$@" ;;
  *) sed -n '2,15p' "$0"; exit 2 ;;
esac

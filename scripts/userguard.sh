# The user always wins. Sourced by the test harness (run.sh, install.sh, stop.sh, monado.sh, build.sh,
# quest.sh). The user counts as playing when
#   - a VR app is connected to WiVRn's compositor (their game, or any other VR app),
#   - the coordinator's vr-session-watch says the user is in VR: its marker process `vr-session` runs (test
#     sessions get `vr-session-test` instead), or $XDG_RUNTIME_DIR/vr-session/status.json has "user": true
#     (older versions of that file: "wivrn" among the sources),
#   - a Hand of Fate 2 process runs that is not one of our test instances (those have HOF2VR_TEST=1 in
#     their environment; run.sh sets it).
# Process and socket checks need the host's PID namespace: run the harness OUTSIDE the Bash sandbox
# (inside it these checks see nothing, so the guard refuses to run there).
# Test environments (docs/two-environments.md): hof2env.sh sets H, G, HOF2VR_ENV, HOF2VR_GAME_DIR ... for this worktree.
. "$(dirname "${BASH_SOURCE[0]}")/hof2env.sh"

# Normal scheduling for the harness and everything it starts (builds, test games, Monado). An app started from a
# hyperlayer key inherits keyd's real-time SCHED_FIFO 49 (2026-10-17: T3 Code, so every agent shell). Under it MSBuild
# hangs (its worker thread's "Unable to set thread priority" goes unhandled), and test games would run above KWin.
# Dropping a real-time policy needs no privilege.
chrt -o -p 0 $$ 2>/dev/null || true

hof2vr_game_pids() { pgrep -x 'Hand of Fate 2.' 2>/dev/null; }

hof2vr_is_test_pid() { { tr '\0' '\n' < "/proc/$1/environ"; } 2>/dev/null | grep -qx 'HOF2VR_TEST=1'; }

# Test instances of every environment (none of them is the user).
hof2vr_all_test_pids() {
  local p
  for p in $(hof2vr_game_pids); do hof2vr_is_test_pid "$p" && echo "$p"; done
  return 0
}

# This environment's test instances (HOF2VR_TEST=1, running from this environment's game folder $G): the only ones
# the harness stops, waits for or kills. (The Steam launcher's user-first step stops every environment's.)
hof2vr_test_pids() {
  local p
  for p in $(hof2vr_all_test_pids); do hof2vr_pid_in_dir "$p" "$G" && echo "$p"; done
  return 0
}

# The user's game: a game process without HOF2VR_TEST=1 that does not run from a test environment's copy (the user
# only plays the Steam install). A process that is gone or exiting (no environment left) is nobody's game - before,
# a game quitting at the moment of the check counted as the user's.
hof2vr_user_game_pids() {
  local p env exe
  for p in $(hof2vr_game_pids); do
    env=$({ tr '\0' '\n' < "/proc/$p/environ"; } 2>/dev/null) || continue
    [ -n "$env" ] || continue
    grep -qx 'HOF2VR_TEST=1' <<< "$env" && continue
    exe=$(readlink "/proc/$p/exe" 2>/dev/null)
    case $exe in "$HOME"/Games/hof2-env*/*) continue ;; esac
    echo "$p"
  done
  return 0
}

# A test instance that runs on WiVRn itself (run.sh wivrn): its own compositor connection is not the user.
hof2vr_test_on_wivrn() {
  local p
  for p in $(hof2vr_all_test_pids); do
    { tr '\0' '\n' < "/proc/$p/environ"; } 2>/dev/null | grep -q '^XR_RUNTIME_JSON=.*wivrn' && return 0
  done
  return 1
}

# Prints why and returns 0 when the user is (or may be) playing; returns 1 when the machine is ours.
hof2vr_user_playing() {
  if [ "$(cat /proc/1/comm 2>/dev/null)" != systemd ]; then
    echo "cannot see the system's processes (sandboxed shell?) - run the harness outside the sandbox"
    return 0
  fi
  if ss -xH state established 2>/dev/null | grep -q '/wivrn/comp_ipc' && ! hof2vr_test_on_wivrn; then
    echo "a VR app is connected to WiVRn"
    return 0
  fi
  if pgrep -x vr-session >/dev/null 2>&1; then echo "vr-session is running (the user is in VR)"; return 0; fi
  local st="${HOF2VR_VRSESSION_STATUS:-$HOF2VR_REAL_RT/vr-session/status.json}" j
  if [ -f "$st" ]; then
    j=$(tr -d ' \n' < "$st")
    if [[ "$j" == *'"user":'* ]]; then
      [[ "$j" == *'"user":true'* ]] && { echo "vr-session-watch: the user is in VR"; return 0; }
    elif [[ "$j" =~ \"sources\":\[[^]]*\"wivrn\" ]] && ! hof2vr_test_on_wivrn; then
      echo "vr-session-watch: a VR app runs on WiVRn (the user is in VR)"; return 0
    fi
  fi
  local p
  for p in $(hof2vr_user_game_pids); do
    echo "Hand of Fate 2 is running (pid $p) and it is not a test instance"; return 0
  done
  return 1
}

# Exit 4 with a message when the user is playing (the caller does not start anything).
hof2vr_guard() {
  local why
  if why=$(hof2vr_user_playing); then
    echo "USER FIRST: not starting ($why)" >&2
    exit 4
  fi
}

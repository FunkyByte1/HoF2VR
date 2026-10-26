#!/usr/bin/env bash
# Checks for "the user always wins" (run OUTSIDE the Bash sandbox; uses Monado test instances).
#   userfirst_test.sh [launcher|stopfile|vrsession|parse ...]   (default: all)
#  launcher   the Steam launcher's make-room step stops a running test instance, removes test files and puts
#             the release DLL back
#  stopfile   a stop request file alone (a launcher that cannot see the process) -> the instance quits itself
#  vrsession  the coordinator's vr-session-watch status (a stand-in file via HOF2VR_VRSESSION_STATUS) lists
#             "monado" (our own test session: the instance stays), then "wivrn" (the user's Quest runs a VR app):
#             the instance quits itself; run.sh, install.sh and monado.sh start refuse while it says so
#  parse      the instance's WiVRn-client check on a sample socket table (1 connected client)
# Runs in this worktree's test environment (docs/two-environments.md; userguard.sh sets H, G and HOF2VR_ENV).
. "$(dirname "${BASH_SOURCE[0]}")/userguard.sh"
S=$H/testdata/userfirst; mkdir -p $S
FAIL=0
ok() { echo "  PASS $*"; }
bad() { echo "  FAIL $*"; FAIL=1; }

up() {   # test build + test instance, wait for the mod (isolation verified)
  $H/scripts/install.sh >/dev/null || { bad "install refused ($?)"; return 1; }
  $H/scripts/run.sh monado gl >/dev/null || { bad "run.sh refused/failed ($?)"; return 1; }
  for i in $(seq 1 60); do grep -q "test isolation\] verified" "$G/BepInEx/LogOutput.log" 2>/dev/null && break; sleep 0.5; done
  sleep 2
  echo "  up: test pids [$(hof2vr_test_pids)] heartbeat $( [ -f "$G/hof2vr_test_running" ] && echo "$(( $(date +%s) - $(stat -c %Y "$G/hof2vr_test_running") )) s old" || echo none)"
}
gone_within() {  # seconds
  local t0=$(date +%s%N)
  for i in $(seq 1 $(( $1 * 4 ))); do [ -z "$(hof2vr_test_pids)" ] && break; sleep 0.25; done
  local ms=$(( ($(date +%s%N) - t0) / 1000000 ))
  if [ -z "$(hof2vr_test_pids)" ]; then ok "$2: gone after $ms ms"; else bad "$2: still running after $1 s"; fi
}
want() { [ $# -eq 0 ] || [[ " $* " == *" $T "* ]]; }

[ -n "$(hof2vr_monado_pids)" ] || $H/scripts/monado.sh start
ARGS="$*"
# the launcher stops EVERY environment's test instances (as it must for the user): not while another one runs
others() { local p; for p in $(hof2vr_all_test_pids); do hof2vr_pid_in_dir "$p" "$G" || echo "$p"; done; }
T=launcher; if want $ARGS; then
  echo "launcher: the Steam launcher's make-room step"
  if [ -n "$(others)" ]; then
    echo "  SKIP: another environment's test instance runs ($(others)); the launcher would stop it too"
  elif up; then
    echo "$H/testdata" > "$G/hof2vr_testmode.txt"
    ( cd "$G" && ./hof2vr_launch.sh --make-room-only )
    gone_within 10 "launcher"
    ls -a "$G" | grep -qE 'hof2vr_(testmode|stop|test_run)' && bad "test files left" || ok "no test files left"
    cmp -s "$G/BepInEx/hof2vr_release/HoF2VR.dll" "$G/BepInEx/plugins/HoF2VR/HoF2VR.dll" && ok "release DLL in place" || bad "release DLL not restored"
  fi
fi
T=stopfile; if want $ARGS; then
  echo "stopfile: only the stop request file"
  if up; then
    touch "$G/hof2vr_stop_tests"
    gone_within 12 "stop file"
    grep -a "USER FIRST" "$G/BepInEx/LogOutput.log" | tail -1
    [ -f "$G/hof2vr_test_running" ] && bad "heartbeat left behind" || ok "heartbeat removed"
    rm -f "$G/hof2vr_stop_tests"
  fi
fi
T=vrsession; if want $ARGS; then
  echo "vrsession: the coordinator's vr-session-watch reports a VR app on WiVRn (stand-in status file)"
  export HOF2VR_VRSESSION_STATUS=$S/vrs_status.json
  printf '{"active": true, "user": false, "test": true, "sources": ["monado"], "since": 1, "updated": 2}' > $HOF2VR_VRSESSION_STATUS
  if up; then
    sleep 3
    [ -n "$(hof2vr_test_pids)" ] && ok "a Monado session (our own test) is not the user: instance stays" || bad "instance quit on a monado source"
    printf '{"active": true, "user": true, "test": true, "sources": ["monado", "wivrn"], "since": 1, "updated": 3}' > $HOF2VR_VRSESSION_STATUS
    gone_within 12 "vr-session wivrn"
    grep -a "USER FIRST" "$G/BepInEx/LogOutput.log" | tail -1
    $H/scripts/run.sh monado gl >/dev/null 2>$S/run.err; rc=$?; [ $rc = 4 ] && ok "run.sh refused: $(cat $S/run.err)" || bad "run.sh rc $rc"
    $H/scripts/install.sh >/dev/null 2>$S/inst.err; rc=$?; [ $rc = 4 ] && ok "install.sh refused" || bad "install.sh rc $rc"
    $H/scripts/monado.sh start >/dev/null 2>&1; rc=$?; [ $rc = 4 ] && ok "monado.sh start refused" || bad "monado.sh start rc $rc"
    [ -n "$(hof2vr_monado_pids)" ] || HOF2VR_VRSESSION_STATUS= $H/scripts/monado.sh start
  fi
  unset HOF2VR_VRSESSION_STATUS
fi
T=parse; if want $ARGS; then
  echo "parse: WiVRn client count from a socket table"
  if up; then
    printf 'Num       RefCount Protocol Flags    Type St Inode Path\n000000007872e33d: 00000002 00000000 00010000 0001 01 9008504 /run/user/1000/wivrn/comp_ipc\n00000000aaaa: 00000003 00000000 00000000 0001 03 9008999 /run/user/1000/wivrn/comp_ipc\n' > $S/unix_sample.txt
    r=$(python3 $H/scripts/cmd.py "userfirst check $S/unix_sample.txt"); [[ "$r" == *"1 VR app(s) connected to WiVRn"* ]] && ok "sample: $r" || bad "sample: $r"
    r=$(python3 $H/scripts/cmd.py "userfirst check /proc/net/unix"); [[ "$r" == *clear* ]] && ok "live table: $r" || bad "live table: $r"
    $H/scripts/stop.sh >/dev/null; gone_within 8 "stop.sh"
  fi
fi
left=$(echo $(hof2vr_test_pids) $(hof2vr_user_game_pids))
[ -n "$left" ] && bad "a game process is left ($left)" || ok "no game process left"
ls -a "$G" | grep -E 'hof2vr_(testmode|stop|test_run)' && bad "test files left in the game folder" || ok "no test files in the game folder"
hof2vr_savecheck || FAIL=1
exit $FAIL

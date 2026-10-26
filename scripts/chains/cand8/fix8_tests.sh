#!/usr/bin/env bash
# Candidate 8's held items, the tests (env 1): b1 seated, switches_test --start, the tours standing and seated (the mod's
# log kept after each for the PROXIMITY details). Output: logs/fix8_tests.log. No reservation (each test takes the slot).
H=/home/bara/Projects/flat2vr/HandOfFate2
cd "$H" || exit 1
export XAUTHORITY=${XAUTHORITY:-$(ls -t /run/user/1000/xauth_* | head -1)}
LOG=$H/logs/fix8_tests.log
GL="/home/bara/.local/share/Steam/steamapps/common/Hand of Fate 2/BepInEx/LogOutput.log"
say() { echo "=== $* ($(date +%T))" >> "$LOG"; }
t() { local n=$1 o=$2; shift 2; grep -q '"user": true' /run/user/1000/vr-session/status.json && { say "the user is in VR - stopping"; exit 2; }; "$@" > "$o" 2>&1; say "$n rc $? | $(tail -1 "$o" | cut -c1-160)"; grep -E '^..:..:.. FAIL' "$o" | cut -c1-300 | sed 's/^/    /' >> "$LOG"; }
say "fix8 tests: DLL $(sha1sum < build/bin/HoF2VR.dll | cut -c1-8)"
${FIX8_PRE:+eval "$FIX8_PRE"}
python3 scripts/session.py start --snapshot magician_mid --to table > logs/fix8_sessions.out 2>&1
t "b1 seated" logs/fix8_b1_seated.out python3 scripts/b1_test.py --seated
cp "$GL" logs/fix8_b1_seated_LogOutput.log
t "switches_test --start" logs/fix8_switches.out python3 scripts/switches_test.py --start
t "tour standing" logs/fix8_tour.out python3 scripts/playtest.py
cp "$GL" logs/fix8_tour_LogOutput.log
t "tour seated" logs/fix8_tour_seated.out python3 scripts/playtest.py --seated
cp "$GL" logs/fix8_tour_seated_LogOutput.log
grep -a "PROXIMITY" logs/fix8_tour_seated_LogOutput.log | cut -c1-260 | sed 's/^/    seated /' >> "$LOG"
python3 scripts/session.py stop > /dev/null 2>&1
say "savecheck: $(scripts/savecheck.sh | tail -1); cfg $(sha1sum < "/home/bara/.local/share/Steam/steamapps/common/Hand of Fate 2/BepInEx/config/flat2vr.hof2vr.cfg" | cut -c1-12)"
say "fix8 tests done"

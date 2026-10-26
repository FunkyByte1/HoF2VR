#!/usr/bin/env bash
# env 3 (2026-10-19): the shell's frame-time cost in a big fight (combat_fixture "big": 10 enemies), the hero kept alive.
#   perf.sh hud   this build: Shell (full guards), Shell worn (every shell forced to 40 %), Rings; 900 frames (10 s) each, twice
#   perf.sh p3c   p3combat's DLL (installed by the caller): four windows, no switch
# One line per window (measure.sh: avg, p95, p99, >11.1 ms, miss %), wrapped in testenv.sh quiet. Run from the chain (lib.sh's cwd).
set -u
L=${1:-hud}
python3 scripts/combat_fixture.py --fixture big --god --equip BasicSword_noToken,BasicShield 2>&1 | tail -1
python3 scripts/cmd.py "vrknock off" > /dev/null
python3 scripts/xrsim.py "head 0 1.7 0 0 0 0; hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0" > /dev/null
sleep 12   # the shells build (one mesh a frame) and the enemies close in
python3 scripts/cmd.py "bars?" | cut -c1-300
for r in 1 2; do
  for st in shell worn rings; do
    # worn: every shell shown at 40 % guard (step 2: the band shapes and two crack sets on - the blend-shape worst case)
    if [ "$L" = hud ]; then
      python3 scripts/cmd.py "vrstatus $([ $st = rings ] && echo rings || echo shell)" > /dev/null
      python3 scripts/cmd.py "shell tune force $([ $st = worn ] && echo 0.4 || echo -1)" > /dev/null; sleep 3
    else [ $st = worn ] && continue; st=p3c; fi
    python3 scripts/cmd.py "face nearest" > /dev/null
    echo "$(python3 scripts/cmd.py 'shell' | cut -c1-160) | $(python3 scripts/cmd.py 'state?' | cut -c1-40)"
    scripts/testenv.sh quiet scripts/measure.sh "perf $L $st $r" 900
  done
done
[ "$L" = hud ] && python3 scripts/cmd.py "vrstatus setting" > /dev/null && python3 scripts/cmd.py "shell tune force -1" > /dev/null
echo "perf done"

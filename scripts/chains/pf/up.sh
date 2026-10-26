#!/usr/bin/env bash
# env 2, playtest fixes 2026-10-17: Monado, a fresh table session (magician_mid), then a fixture fight.
#   up.sh [FIXTURE|jump:FIGHT[:SPEC]]   (default thieves) -> logs/pf/up.out
set -u
H=/home/bara/Projects/Flat2VR-env2/HandOfFate2
cd "$H" || exit 1
. "$H/scripts/chains/r3/env.sh"
F=${1:-thieves}
grep -q '"user": true' /run/user/1000/vr-session/status.json 2>/dev/null && { echo "the user is in VR"; exit 2; }
scripts/monado.sh start
python3 scripts/session.py start --snapshot magician_mid --to table
case $F in
  jump:*) J=${F#jump:}; FIGHT=${J%%:*}; SPEC=${J#*:}; [ "$SPEC" = "$J" ] && SPEC=""
          if [ -n "$SPEC" ]; then python3 scripts/combat_fixture.py --jump "$FIGHT" --with "$SPEC" --god --physical --equip BasicSword_noToken,BasicShield
          else python3 scripts/combat_fixture.py --jump "$FIGHT" --god --physical --equip BasicSword_noToken,BasicShield; fi ;;
  none) ;;
  *) python3 scripts/combat_fixture.py --fixture "$F" --god --physical --equip BasicSword_noToken,BasicShield ;;
esac
python3 scripts/cmd.py 'state?'

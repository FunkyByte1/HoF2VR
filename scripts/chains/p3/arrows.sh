#!/usr/bin/env bash
# env 2, the ruling of 17:40 (2026-10-18): pictures of the ground arrow - red (nothing can stop it), gold with the circle
# (the shield can), a melee blow nothing can block (LeanDodge) - at 0, ~50, ~95 % fill, a bright and a dark arena.
#   arrows.sh STAGE   (bright | brightshield | dark)   -> captures/arrows-2026-10-18/   (in a running env 2 session at the map)
set -u
H=/home/bara/Projects/Flat2VR-env2/HandOfFate2
cd "$H" || exit 1
. "$H/scripts/chains/r3/env.sh"
export LINES_OUT=/home/bara/Projects/flat2vr/HandOfFate2/captures/arrows-2026-10-18
P="python3 scripts/lines_pics.py"
c() { python3 scripts/cmd.py "$@"; }
fixture() { timeout 1500 python3 scripts/combat_fixture.py --jump "$1" --with "$2" --god --physical --equip "$3" 2>&1 | tail -1; }
leave() { python3 -c "import sys; sys.path.insert(0, 'scripts'); import indicator_shots; print('left:', indicator_shots.leave())" 2>&1 | tail -1; }
stills() {   # NAME COLOUR ENEMY DIST
  c "vrlook Gilded" > /dev/null; c "timescale 0" > /dev/null
  c "heroat $3 0 $4" > /dev/null; c "face $3" > /dev/null
  python3 scripts/xrsim.py "head 0 1.7 0 0 0 0; hand r 0.32 0.85 0.05 0 -80 0; hand l -0.32 0.85 0.05 0 -80 0"
  sleep 0.4
  for f in 0.02 0.5 0.95; do c "groundarrow $f $2 $3" > /dev/null; sleep 0.3; $P now "$1_fill$(echo $f | tr -d .)" | tail -1; done
  python3 scripts/xrsim.py "head 0 1.7 0 0 -20 0"; sleep 0.3
  c "groundarrow 0.5 $2 $3" > /dev/null; sleep 0.3; $P now "$1_fill05_lookdown" | tail -1
  c "groundarrow 0 off" > /dev/null; c "timescale 1" > /dev/null
  python3 scripts/xrsim.py "head 0 1.7 0 0 0 0"
}
G_LOG="/home/bara/Games/hof2-env2/Hand of Fate 2/BepInEx/LogOutput.log"
night() {
  # a night arena (the game's pick): fights until a new "fight_in done" line names a Night scene, the game in Level_Play
  local k n0 sc ok=0
  for k in 1 2 3 4 5 6 7 8; do
    n0=$(grep -c "progress: fight_in done" "$G_LOG" 2>/dev/null)
    fixture EL_Ambush_Empire "ThievesMolotovThrowerREF*1,ShadowWarriorREF*1" "BasicSword_noToken,BasicShield"
    [ "$(grep -c "progress: fight_in done" "$G_LOG" 2>/dev/null)" -gt "${n0:-0}" ] && sc=$(grep "progress: fight_in done" "$G_LOG" | tail -1 | grep -o "scene [^ ]*") || sc="(no new fight)"
    echo "try $k: $sc / $(c 'state?' | cut -c1-30)"
    case "$sc" in *Night*|*night*) c 'state?' | grep -q Level_Play && { ok=1; break; } ;; esac
    leave
  done
  [ $ok = 1 ] || { echo "no night arena"; return; }
  stills v2_night_gold_lob_shield gold ThievesMolotovThrower 5.5
  stills v2_night_red_melee red ShadowWarrior 3.5
  leave
}
case ${1:-bright} in
  bright)
    fixture EL_Ambush_Thral "ThievesMolotovThrowerREF*1,ShadowWarriorREF*1" HugeHammer
    c "shots?" | grep -o "shield [a-zA-Z]*"
    stills bright_red_lob red ThievesMolotovThrower 5.5
    stills bright_red_melee red ShadowWarrior 3.5
    # live: a lob's wind-up without a shield (red)
    c "face track ThievesMolotovThrower" > /dev/null; c "heroat ThievesMolotovThrower 0 5.5" > /dev/null
    timeout 70 python3 - <<'PY'
import sys, time, re
sys.path.insert(0, 'scripts'); sys.argv = ['x']
import lines_pics as L
t0 = time.time()
while time.time() - t0 < 60:
    s = L.send("shots?", 4)
    m = re.search(r"lastGround \[([^\]]*) fill ([\d.]+)\]", s)
    if "ground True" in s and m and 0.35 < float(m.group(2)) < 0.8:
        L.shot("bright_red_lob_live"); print(m.group(0)); break
    time.sleep(0.03)
PY
    c "face track off" > /dev/null
    # live: a melee blow nothing can block (Lean dodge on)
    c "vrlean on" > /dev/null; c "face track ShadowWarrior" > /dev/null; c "heroat ShadowWarrior 0 2.5" > /dev/null
    timeout 70 python3 - <<'PY'
import sys, time
sys.path.insert(0, 'scripts'); sys.argv = ['x']
import lines_pics as L
st = L.strike(timeout=60, fresh=0.5, colours=("Red",))
if st:
    time.sleep(st["eta"] * 0.45); L.shot("bright_red_melee_live_lean_dodge_on"); print(st["raw"][:120])
PY
    c "vrlean setting" > /dev/null; c "face track off" > /dev/null
    leave ;;
  brightshield)
    fixture EL_Ambush_Thral "ThievesMolotovThrowerREF*2" "BasicSword_noToken,BasicShield"
    c "shots?" | grep -o "shield [a-zA-Z]*"
    stills bright_gold_lob_shield gold ThievesMolotovThrower 5.5
    c "face track ThievesMolotovThrower" > /dev/null; c "heroat ThievesMolotovThrower 0 5.5" > /dev/null
    python3 scripts/xrsim.py "head 0 1.7 0 0 0 0; hand r 0.32 0.85 0.05 0 -80 0; hand l -0.25 1.2 -0.3 0 0 0"
    timeout 70 python3 - <<'PY'
import sys, time, re
sys.path.insert(0, 'scripts'); sys.argv = ['x']
import lines_pics as L
t0 = time.time()
while time.time() - t0 < 60:
    s = L.send("shots?", 4)
    m = re.search(r"lastGround \[([^\]]*) fill ([\d.]+)\]", s)
    if "ground True" in s and "ring True" in s and m and 0.35 < float(m.group(2)) < 0.85:
        L.shot("bright_gold_lob_shield_live"); print(m.group(0)); break
    time.sleep(0.03)
PY
    c "face track off" > /dev/null
    leave ;;
  gold)
    fixture EL_Ambush_Thral "ThievesMolotovThrowerREF*2" "BasicSword_noToken,BasicShield"
    stills bright_gold_lob_shield gold ThievesMolotovThrower 5.5
    leave ;;
  night)
    # a night arena: fights until the scene's name says Night (the arena and its time are the game's pick)
    for k in 1 2 3 4 5; do
      fixture EL_Ambush_Empire "ThievesMolotovThrowerREF*1,ShadowWarriorREF*1" "BasicSword_noToken,BasicShield"
      sc=$(grep "progress: fight_in done" "/home/bara/Games/hof2-env2/Hand of Fate 2/BepInEx/LogOutput.log" | tail -1 | grep -o "scene [^ ]*")
      echo "try $k: $sc"
      case "$sc" in *Night*|*night*) break ;; esac
      leave
    done
    stills night_gold_lob_shield gold ThievesMolotovThrower 5.5
    stills night_red_lob red ThievesMolotovThrower 5.5
    stills night_red_melee red ShadowWarrior 3.5
    leave ;;
  v2)
    # the coordinator's review: a grey body, a saturated gold, the head filled at the end - one row of each kind again
    fixture EL_Ambush_Thral "ThievesMolotovThrowerREF*1,ShadowWarriorREF*1" "BasicSword_noToken,BasicShield"
    stills v2_snow_red_lob red ThievesMolotovThrower 5.5
    leave
    night ;;
  v2night)
    night ;;
  dark)
    fixture EL_Ambush_Empire "ThievesMolotovThrowerREF*1,ShadowWarriorREF*1" "BasicSword_noToken,BasicShield"
    stills dark_gold_lob_shield gold ThievesMolotovThrower 5.5
    stills dark_red_lob red ThievesMolotovThrower 5.5
    stills dark_red_melee red ShadowWarrior 3.5
    leave ;;
esac

#!/usr/bin/env bash
# Watch a test environment's virtual screen live (2026-10-18, the user: "view the virtual monitor for a minute just to
# see what's going on"). Opens a small window on the desktop that shows the simulated headset's view (Monado's window,
# both eyes) of the test game running on the headless virtual screen (vdisplay.sh). Read-only: it only grabs frames
# with ffplay's x11grab, and closing its window (or pressing q, or Ctrl+C here) changes nothing in the test.
#   vwatch.sh            the environment whose test is running now (the first one found)
#   vwatch.sh 1|2|3      that environment
#   vwatch.sh 2 game     the test game's own flat window instead of the headset view
# A test run ends with its Monado window: the watch window then closes; run this again for the next run.
set -u
ROOT=/home/bara/Projects
want_env="${1:-}"
what="${2:-eyes}"
[ "$want_env" = game ] && { want_env=""; what=game; }

worktree() { case "$1" in 1) echo "$ROOT/Flat2VR";; *) echo "$ROOT/Flat2VR-env$1";; esac; }

find_win() {   # DISPLAY name -> window id
  local d="$1" name="$2" w
  for w in $(DISPLAY="$d" xdotool search --maxdepth 2 --name "^$name\$" 2>/dev/null); do echo "$w"; return 0; done
  return 1
}

for e in ${want_env:-1 2 3}; do
  envf="$(worktree "$e")/HandOfFate2/testdata/vdisplay.env"
  [ -f "$envf" ] || continue
  d="$(sed -n 's/^DISPLAY=//p' "$envf")"
  [ -n "$d" ] || continue
  if [ "$what" = game ]; then win="$(find_win "$d" 'Hand of Fate 2')"; else win="$(find_win "$d" Monado)"; fi
  [ -n "${win:-}" ] || continue
  echo "environment $e, display $d, window $win ($what) - close the window or press q to stop watching"
  exec ffplay -loglevel error -window_title "HoF2 test env $e ($what) - watch only" \
       -f x11grab -framerate 15 -window_id "$win" -i "$d"
done
echo "no test is showing on a virtual screen right now${want_env:+ in environment $want_env} (nothing to watch)"
exit 1

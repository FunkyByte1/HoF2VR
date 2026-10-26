#!/usr/bin/env bash
# The virtual screen (2026-10-17, the user: "Can you have it run on an imaginary screen?"): one headless gamescope per
# test environment. Its own X display (gamescope's Xwayland) holds this environment's Monado compositor window and its
# test game's window, so neither appears on the user's desktop and KWin never composites them. (That day Monado's
# full-screen Wayland window covered the user's second monitor; since 2026-10-10 kwin_wayland hit amdgpu page faults three
# times while test games ran on the desktop.) docs/two-environments.md "The virtual screen".
#   vdisplay.sh start     start it unless it runs (refuses while the user plays); prints its DISPLAY.
#                         monado.sh start and run.sh call it.
#   vdisplay.sh stop      stop it (monado.sh stop calls it; stop.sh when no Monado runs). A client still on it loses its display.
#   vdisplay.sh status    running or not, its DISPLAY, the windows on it
#   vdisplay.sh windows [PID...]
#                         the proof: the windows on the user's desktop - KWin's own list of every window (a one-off KWin
#                         script over D-Bus) and, for each PID, its windows on the desktop's X display :0 (xdotool) and on
#                         the virtual screen. Exit 1 when a window of PID (or of Monado / the game / gamescope) is on the
#                         desktop.
# HOF2VR_VDISPLAY=0 turns it off: `start` does nothing and monado.sh / run.sh put their windows on the desktop as before.
# HOF2VR_VDISPLAY_SIZE=WxH: gamescope's output (default 1280x720, the test game's window); 60 Hz (Monado presents FIFO
# at the display's refresh: 16.7 ms as on the desktop).
# Files: testdata/vdisplay.env (DISPLAY=, XAUTHORITY= for its clients), testdata/vdisplay.pid, logs/vdisplay.log.
# Run it OUTSIDE the Bash sandbox (it starts a GPU process and looks at the host's processes).
set -uo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/userguard.sh"

vd_start() {
  local sz=${HOF2VR_VDISPLAY_SIZE:-1280x720} i
  hof2vr_vdisplay_on || { echo "virtual screen off (HOF2VR_VDISPLAY=0)"; return 0; }
  if hof2vr_vdisplay_env; then echo "virtual screen up: DISPLAY $DISPLAY (gamescope pid $(hof2vr_vdisplay_pids | head -1))"; return 0; fi
  hof2vr_guard   # never while the user plays
  command -v gamescope > /dev/null || { echo "vdisplay: gamescope is not installed" >&2; return 1; }
  vd_stop > /dev/null   # a half-dead one (its display gone)
  mkdir -p "$H/testdata" "$H/logs"
  rm -f "$HOF2VR_VD_ENV" "$HOF2VR_VD_PID"
  # A clean environment: none of the caller's AppImage libraries (T3 Code's LD_LIBRARY_PATH) and no desktop display, so
  # nothing of it can open on the desktop; the session's runtime dir (an env-N shell may carry its Monado's). The child
  # writes the display for the clients, then waits; when the child ends, gamescope ends ("Primary child shut down").
  setsid env -u LD_LIBRARY_PATH -u WAYLAND_DISPLAY -u DISPLAY -u XAUTHORITY \
    XDG_RUNTIME_DIR="$HOF2VR_REAL_RT" HOF2VR_VDISPLAY_OF="$H" \
    gamescope --backend headless -W "${sz%x*}" -H "${sz#*x}" -r 60 -- \
    sh -c 'printf "DISPLAY=%s\nXAUTHORITY=%s\n" "$DISPLAY" "${XAUTHORITY:-}" > "$1.tmp" && mv -f "$1.tmp" "$1" && exec tail -f /dev/null' \
    vdisplay "$HOF2VR_VD_ENV" > "$H/logs/vdisplay.log" 2>&1 < /dev/null &
  local gp=$!   # setsid and env exec (no fork): this is gamescope
  echo "$gp" > "$HOF2VR_VD_PID"
  for i in $(seq 1 50); do hof2vr_vdisplay_env && break; sleep 0.2; done
  if ! hof2vr_vdisplay_env; then
    echo "vdisplay: gamescope did not come up within 10 s (logs/vdisplay.log):" >&2; tail -5 "$H/logs/vdisplay.log" >&2
    vd_stop > /dev/null; kill -TERM "$gp" 2>/dev/null; return 1
  fi
  echo "virtual screen up: DISPLAY $DISPLAY (gamescope pid $(hof2vr_vdisplay_pids | head -1), env $HOF2VR_ENV)"
}

vd_stop() {
  local g c i
  read -r g c <<< "$(hof2vr_vdisplay_pids | tr '\n' ' ')"
  if [ -z "${g:-}" ]; then
    rm -f "$HOF2VR_VD_ENV" "$HOF2VR_VD_PID"; echo "virtual screen not running"; return 0
  fi
  kill -TERM "$c" 2>/dev/null   # its child ends: gamescope's reaper and gamescope (with its Xwayland) end with it
  for i in $(seq 1 25); do kill -0 "$g" 2>/dev/null || break; sleep 0.2; done
  if kill -0 "$g" 2>/dev/null; then kill -TERM "$g" 2>/dev/null; sleep 1; fi
  kill -0 "$g" 2>/dev/null && kill -KILL "$g" 2>/dev/null
  rm -f "$HOF2VR_VD_ENV" "$HOF2VR_VD_PID"
  echo "virtual screen stopped (gamescope pid $g)"
}

# KWin's own list of the desktop's windows (Wayland and X): a one-off script, loaded, run and unloaded over D-Bus; it
# prints to KWin's log (the journal). Lines: pid<TAB>class<TAB>output<TAB>geometry<TAB>caption.
kwin_windows() {
  local js="$H/testdata/vdisplay-kwin.js" tag="hof2vr-win-$$-$(date +%s%N)" since id
  mkdir -p "$H/testdata"
  cat > "$js" <<EOF
const ws = workspace.windowList ? workspace.windowList() : workspace.clientList();
for (let i = 0; i < ws.length; i++) {
  const w = ws[i], g = w.frameGeometry;
  console.info("$tag\t" + w.pid + "\t" + w.resourceClass + "\t" + (w.output ? w.output.name : "") + "\t" +
        Math.round(g.width) + "x" + Math.round(g.height) + "+" + Math.round(g.x) + "+" + Math.round(g.y) + "\t" + w.caption);
}
console.info("$tag\tend\t" + ws.length);
EOF
  since=$(date '+%Y-%m-%d %H:%M:%S')
  id=$(gdbus call --session --dest org.kde.KWin --object-path /Scripting --method org.kde.kwin.Scripting.loadScript "$js" "$tag" 2>/dev/null | tr -dc 0-9)
  [ -n "$id" ] || { echo "kwin: could not load a script (no session D-Bus?)" >&2; return 1; }
  gdbus call --session --dest org.kde.KWin --object-path "/Scripting/Script$id" --method org.kde.kwin.Script.run > /dev/null 2>&1
  for i in 1 2 3 4 5 6 7 8 9 10; do
    sleep 0.3
    journalctl --user -b --since "$since" -o cat 2>/dev/null | grep -q "$tag	end" && break
  done
  gdbus call --session --dest org.kde.KWin --object-path /Scripting --method org.kde.kwin.Scripting.unloadScript "$tag" > /dev/null 2>&1
  rm -f "$js"
  journalctl --user -b --since "$since" -o cat 2>/dev/null | grep -a "$tag	" | sed "s/^.*$tag	//" | grep -v '^end	'
}

vd_windows() {
  local bad=0 p xa list n vd
  xa=$(ls -t "$HOF2VR_REAL_RT"/xauth_* 2>/dev/null | head -1)
  list=$(kwin_windows) || bad=2
  echo "KWin: $(printf '%s\n' "$list" | grep -c .) windows on the desktop"
  # anything of ours by name (Monado's window is titled "Monado"; the game's "Hand of Fate 2"; gamescope's "gamescope")
  if printf '%s\n' "$list" | grep -aiE 'monado|hand of fate|gamescope' | sed 's/^/  ON THE DESKTOP: /' | grep .; then bad=1; fi
  hof2vr_vdisplay_env > /dev/null 2>&1 && vd=$DISPLAY || vd=
  for p in "$@"; do
    if printf '%s\n' "$list" | awk -F'\t' -v p="$p" '$1 == p' | sed "s/^/  ON THE DESKTOP (pid $p): /" | grep .; then bad=1; fi
    n=$(DISPLAY=${HOF2VR_DESKTOP_DISPLAY:-:0} XAUTHORITY=$xa xdotool search --pid "$p" 2>/dev/null | wc -l)
    [ "$n" -gt 0 ] && bad=1
    echo "pid $p ($(cat "/proc/$p/comm" 2>/dev/null || echo gone)): desktop X ${HOF2VR_DESKTOP_DISPLAY:-:0} $n windows, KWin $(printf '%s\n' "$list" | awk -F'\t' -v p="$p" '$1 == p' | grep -c .)$(
      [ -n "$vd" ] && echo ", virtual screen $vd $(env -u XAUTHORITY DISPLAY="$vd" xdotool search --pid "$p" 2>/dev/null | wc -l)") windows"
  done
  [ $bad = 0 ] && echo "desktop clear: no window of ours on it" || echo "NOT CLEAR (exit $bad)"
  return $bad
}

case ${1:-status} in
  start) vd_start ;;
  stop) vd_stop ;;
  status)
    if hof2vr_vdisplay_env; then
      echo "virtual screen up: DISPLAY $DISPLAY (gamescope pid $(hof2vr_vdisplay_pids | head -1), env $HOF2VR_ENV)"
      for w in $(xdotool search --onlyvisible --name . 2>/dev/null); do echo "  window $w: $(xdotool getwindowname "$w" 2>/dev/null) (pid $(xdotool getwindowpid "$w" 2>/dev/null))"; done
    else
      echo "virtual screen not running (env $HOF2VR_ENV$(hof2vr_vdisplay_on || echo ', HOF2VR_VDISPLAY=0'))"
    fi ;;
  windows) shift; vd_windows "$@" ;;
  *) echo "usage: vdisplay.sh start|stop|status|windows [PID...]" >&2; exit 2 ;;
esac

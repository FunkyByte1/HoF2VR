#!/usr/bin/env bash
# Scriptable test headset (Monado "remote" driver, TCP 4242). Does NOT touch the system's
# active OpenXR runtime: the game is pointed at Monado with XR_RUNTIME_JSON by run.sh.
#   monado.sh start|stop|status
# One Monado per test environment (docs/two-environments.md), and only this worktree's is ever started or stopped:
#   env 1  runtime dir $XDG_RUNTIME_DIR (socket monado_comp_ipc), remote driver on TCP 4242 - as always;
#   env N  runtime dir $XDG_RUNTIME_DIR/hof2vr-envN, TCP 4241+N (set in its own config_v0.json). Its start also leaves
#          a small guard behind that stops env N's Monado and xrsim daemon as soon as the user is in VR.
# Monado's compositor window goes to this environment's virtual screen (scripts/vdisplay.sh: a headless gamescope, X11
# through XRT_COMPOSITOR_FORCE_XCB=1), never onto the user's desktop; `stop` stops the virtual screen too.
# HOF2VR_VDISPLAY=0: the window on the desktop as before 2026-10-17 (Wayland, full screen on a monitor).
. "$(dirname "${BASH_SOURCE[0]}")/userguard.sh"
SOCK="$HOF2VR_MONADO_RT/monado_comp_ipc"

stop_own() {   # this environment's xrsim daemon and Monado; prints whether a Monado was running
  local p
  p=$(hof2vr_script_pids xrsim.py --daemon); [ -n "$p" ] && kill $p 2>/dev/null
  p=$(hof2vr_monado_pids)
  if [ -n "$p" ]; then kill $p 2>/dev/null; return 0; fi
  return 1
}

case ${1:-status} in
  start)
    hof2vr_guard   # never while the user plays
    stop_own; sleep 0.5
    rm -f "$SOCK"
    if [ "$HOF2VR_ENV" != 1 ]; then
      # env N: its own runtime dir (socket + pid file) and its own remote-driver port (Monado 25.1 takes the remote
      # node only with all of version, port and view_count; without view_count it silently stays on 4242)
      mkdir -p "$HOF2VR_MONADO_RT/config/monado" && chmod 700 "$HOF2VR_MONADO_RT"
      printf '{\n\t"active": "remote",\n\t"remote": {\n\t\t"version": 0,\n\t\t"port": %d,\n\t\t"view_count": 2\n\t}\n}\n' "$HOF2VR_XRSIM_PORT" \
        > "$HOF2VR_MONADO_RT/config/monado/config_v0.json"
      if [ -n "$(ss -ltnH "sport = :$HOF2VR_XRSIM_PORT" 2>/dev/null)" ]; then
        echo "monado failed: TCP $HOF2VR_XRSIM_PORT is already in use"; exit 1
      fi
      hof2vr_xr_env
      export XDG_CONFIG_HOME="$HOF2VR_MONADO_RT/config"
    fi
    if hof2vr_vdisplay_on; then
      # the virtual screen's DISPLAY, no WAYLAND_DISPLAY, and Monado's X11 window target: its window opens there only
      "$H/scripts/vdisplay.sh" start > /dev/null && hof2vr_vdisplay_env || {
        echo "monado failed: no virtual screen (scripts/vdisplay.sh start; HOF2VR_VDISPLAY=0 puts Monado's window on the desktop)"; exit 1; }
      export XRT_COMPOSITOR_FORCE_XCB=1
    fi
    P_OVERRIDE_ACTIVE_CONFIG=remote XRT_COMPOSITOR_LOG=info XRT_COMPOSITOR_NULL=${MONADO_NULL:-0} \
      setsid bash -c 'tail -f /dev/null | exec monado-service' > "$H/logs/monado.log" 2>&1 < /dev/null &
    for i in $(seq 1 50); do [ -S "$SOCK" ] && break; sleep 0.2; done
    [ -S "$SOCK" ] || { echo "monado failed"; tail -20 "$H/logs/monado.log"; exit 1; }
    if [ "$HOF2VR_ENV" != 1 ]; then
      # its remote driver must listen on this environment's port (never fall back to another environment's)
      for i in $(seq 1 25); do
        owner=$(ss -ltnpH "sport = :$HOF2VR_XRSIM_PORT" 2>/dev/null | sed -n 's/.*pid=\([0-9]*\).*/\1/p' | head -1)
        [ -n "$owner" ] && break; sleep 0.2
      done
      if [ -z "$owner" ] || ! hof2vr_monado_pids | grep -qx "$owner"; then
        echo "monado failed: this environment's Monado does not listen on TCP $HOF2VR_XRSIM_PORT (owner: ${owner:-none})"
        stop_own; tail -20 "$H/logs/monado.log"; exit 1
      fi
    fi
    setsid python3 "$H/scripts/xrsim.py" --daemon > "$H/logs/xrsimd.log" 2>&1 < /dev/null &
    xp=$!
    if [ "$HOF2VR_ENV" != 1 ]; then
      setsid "$H/scripts/monado.sh" guard > "$H/logs/monado-guard.log" 2>&1 < /dev/null &
    fi
    sleep 0.5; echo "monado up (env $HOF2VR_ENV, socket $SOCK, TCP $HOF2VR_XRSIM_PORT, xrsim daemon pid $xp)" ;;
  stop)
    g=$(hof2vr_script_pids monado.sh guard); [ -n "$g" ] && kill $g 2>/dev/null
    if stop_own; then echo "monado stopped"; else echo "monado not running"; fi
    rm -f "$SOCK"
    # its window's screen goes with it (once Monado has let go of it)
    for i in $(seq 1 30); do [ -z "$(hof2vr_monado_pids)" ] && break; sleep 0.1; done
    "$H/scripts/vdisplay.sh" stop ;;
  status)
    p=$(hof2vr_monado_pids)
    if [ -n "$p" ]; then ps -o pid=,comm= -p "$(echo $p | tr ' ' ,)"; else echo "not running"; fi ;;
  guard)
    # (env N, started by `start`) the user always wins: once they are in VR, this environment's simulator goes quiet
    # (its test instance quits by itself). Ends with this environment's Monado.
    [ "$HOF2VR_ENV" = 1 ] && exit 0
    for g in $(hof2vr_script_pids monado.sh guard); do [ "$g" != "$$" ] && [ "$g" != "$BASHPID" ] && kill "$g" 2>/dev/null; done
    hits=0
    while [ -n "$(hof2vr_monado_pids)" ]; do
      # two checks in a row, 2 s apart (a game starting or quitting at the moment of one check is no evidence)
      if why=$(hof2vr_user_playing); then
        hits=$((hits + 1))
        if [ $hits -ge 2 ]; then
          echo "$(date +%T) USER FIRST: $why - stopping environment $HOF2VR_ENV's Monado and xrsim daemon"
          stop_own; rm -f "$SOCK"; sleep 1; "$H/scripts/vdisplay.sh" stop; exit 0
        fi
      else
        hits=0
      fi
      sleep 2
    done ;;
esac

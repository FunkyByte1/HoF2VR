export HOF2VR_ENV=2
[ -n "${XAUTHORITY:-}" ] || { XA=$(ls -t /run/user/$(id -u)/xauth_* 2>/dev/null | head -1); [ -n "$XA" ] && export XAUTHORITY=$XA; }
export DISPLAY=${DISPLAY:-:0}

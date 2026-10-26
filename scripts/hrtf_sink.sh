#!/usr/bin/env bash
# A temporary PipeWire null sink for the HRTF tests (docs/hrtf-plan.md §6): a test game's sound goes there (PULSE_SINK) and
# is recorded from it (pw-record); nobody hears it. Nothing persists: `down` unloads it and checks that the sinks and the
# default sink are as before. Every command it runs, and how to undo it: docs/system-changes.md ("HRTF tests").
#   hrtf_sink.sh up       refuses while the user is in VR; notes the default sink; loads module-null-sink "hof2vr_null";
#                         if the default sink moved (it should not), unloads it again at once and fails
#   hrtf_sink.sh down     unloads it (the noted module, and any other module that made a hof2vr_null); checks
#   hrtf_sink.sh status   the sink, its module, the default sink, what plays into it and what records from it
#   hrtf_sink.sh game PID which sink the game process PID plays into (prints the sink's name; exit 1 if none)
# Run it OUTSIDE the Bash sandbox (pactl talks to the session's PipeWire).
set -uo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/userguard.sh"
NAME=hof2vr_null
ST="$H/testdata/hrtf/sink.state"

sink_index() { pactl list short sinks | awk -v n="$NAME" '$2 == n {print $1}'; }
our_modules() { pactl list short modules | awk -v n="sink_name=$NAME" 'index($0, n) {print $1}'; }

case ${1:-status} in
  up)
    hof2vr_guard
    mkdir -p "${ST%/*}"
    if [ -n "$(sink_index)" ]; then echo "sink $NAME is already up (module $(our_modules | tr '\n' ' '))"; exit 0; fi
    def=$(pactl get-default-sink)
    sinks_before=$(pactl list short sinks | awk '{print $2}' | sort | tr '\n' ' ')
    # state.restore-*=false: WirePlumber does not remember this node's volume or target (it otherwise keeps a line per new
    # node in ~/.local/state/wireplumber/stream-properties for good - the first test on 2026-10-13 left two, see
    # docs/system-changes.md)
    id=$(pactl load-module module-null-sink sink_name=$NAME rate=48000 channels=2 \
         "sink_properties='device.description=HoF2VR-test state.restore-props=false state.restore-target=false'") \
      || { echo "load-module failed" >&2; exit 1; }
    printf 'module=%s\ndefault=%s\nsinks=%s\nup=%s\n' "$id" "$def" "$sinks_before" "$(date +%FT%T)" > "$ST"
    sleep 1
    now=$(pactl get-default-sink)
    if [ "$now" != "$def" ]; then
      pactl unload-module "$id"
      echo "the default sink moved to '$now' when $NAME appeared - unloaded it again (default now '$(pactl get-default-sink)')" >&2
      exit 1
    fi
    echo "sink $NAME up (module $id, $(pactl list short sinks | awk -v n="$NAME" '$2 == n {print $4, $5, $6}')); the default sink stays $def" ;;
  down)
    def=$(sed -n 's/^default=//p' "$ST" 2>/dev/null)
    ids="$(sed -n 's/^module=//p' "$ST" 2>/dev/null) $(our_modules)"
    for id in $(echo $ids | tr ' ' '\n' | sort -u); do
      pactl unload-module "$id" 2>/dev/null && echo "unloaded module $id"
    done
    sleep 0.5
    bad=0
    [ -z "$(sink_index)" ] || { echo "PROBLEM: $NAME is still there" >&2; bad=1; }
    [ -z "$(our_modules)" ] || { echo "PROBLEM: a module for $NAME is still loaded" >&2; bad=1; }
    now=$(pactl get-default-sink)
    if [ -n "$def" ] && [ "$now" != "$def" ]; then echo "PROBLEM: the default sink is '$now', was '$def'" >&2; bad=1; fi
    before=$(sed -n 's/^sinks=//p' "$ST" 2>/dev/null)
    after=$(pactl list short sinks | awk '{print $2}' | sort | tr '\n' ' ')
    if [ -n "$before" ] && [ "$before" != "$after" ]; then echo "note: sinks before '$before', now '$after'" >&2; fi
    [ $bad = 0 ] && { echo "sink $NAME gone; default sink $now (as before)"; rm -f "$ST"; }
    exit $bad ;;
  status)
    echo "sink: $(pactl list short sinks | awk -v n="$NAME" '$2 == n' || true)"
    echo "modules: $(our_modules | tr '\n' ' ')"
    echo "default sink: $(pactl get-default-sink)"
    [ -f "$ST" ] && sed 's/^/state: /' "$ST"
    idx=$(sink_index)
    if [ -n "$idx" ]; then
      echo "playing into it:"
      pactl list sink-inputs | awk -v i="$idx" '/^Sink Input #/{n=$3} /^\tSink: /{s=$2} /application.name = /{a=$0} /application.process.id = /{p=$3; if (s == i) print "  input " n ": " a " pid " p}'
    fi ;;
  game)
    pid=${2:?game pid}
    sink=$(pactl list sink-inputs | awk -v p="\"$pid\"" '/^\tSink: /{s=$2} /application.process.id = /{if ($3 == p) {print s; exit}}')
    [ -n "$sink" ] || { echo "no stream of pid $pid" >&2; exit 1; }
    pactl list short sinks | awk -v s="$sink" '$1 == s {print $2}' ;;
  *) sed -n '2,12p' "$0"; exit 2 ;;
esac

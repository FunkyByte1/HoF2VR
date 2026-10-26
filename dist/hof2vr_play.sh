#!/bin/sh
# "Hand of Fate 2 VR" in WiVRn's app list runs this. The player always comes first: a test instance of the
# game (started by the developer's tools) would make Steam think the game is already running, so it is
# stopped first; then Steam starts the game as usual (its launch option runs hof2vr_launch.sh).
# HOF2VR_PLAY_DRYRUN=1: make room, then only print what would be started.
G="$HOME/.local/share/Steam/steamapps/common/Hand of Fate 2"
[ -x "$G/hof2vr_launch.sh" ] && "$G/hof2vr_launch.sh" --make-room-only
if [ -n "$HOF2VR_PLAY_DRYRUN" ]; then echo "would run: steam steam://rungameid/456670"; exit 0; fi
exec steam steam://rungameid/456670

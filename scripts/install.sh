#!/usr/bin/env bash
# Install / update the HoF2 VR mod in the Steam game folder (idempotent).
#   install.sh            BepInEx + plugin + libopenvr_api.so + patched data.unity3d + play wrapper (the launcher only if
#                         missing) (a TEST install: test instances run it; the player's launches put the release back)
#   install.sh --release  the same plus the launcher, and this build becomes the RELEASE the player gets: hof2vr_launch.sh copies
#                         BepInEx/hof2vr_release/* into BepInEx/plugins/HoF2VR/ before every launch of theirs
#   install.sh --no-hrtf  (with either) no 3D-sound spatializer (docs/hrtf-plan.md): the Steam Audio files are left out (and
#                         removed if there) and data.unity3d gets the VR-only patch. Without it: Steam Audio's two binaries
#                         (downloads/steamaudio-4.8.1, scripts/steamaudio.sh) go to <Data>/Plugins/x86_64/, its licence
#                         notices to BepInEx/plugins/HoF2VR/licenses/steamaudio/, and data.unity3d names the spatializer and
#                         preloads its plugin. The mod switches HRTF on per sound, and falls back to the game's own panning
#                         by itself if anything is missing ([Audio] Hrtf turns it off live).
# Refuses while the user plays (scripts/userguard.sh). Test mode is never installed: it is HOF2VR_TEST=1 in
# the environment of a test launch (run.sh), never a file in the game folder.
# Installs into this worktree's environment (docs/two-environments.md): env 1 = the Steam install; env N = its game
# copy only (no release, no desktop entry: those belong to the user's Steam install).
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/userguard.sh"
hof2vr_guard
hof2vr_check_game_dir
REL=0; HRTF=1
for a in "$@"; do
  case $a in
    --release) REL=1 ;;
    --no-hrtf) HRTF=0 ;;
    --testflag) echo "install.sh --testflag is gone: test mode is HOF2VR_TEST=1 from run.sh only (a Steam launch is never a test run)" >&2
                exit 2 ;;
    *) echo "install.sh: unknown option $a (--release, --no-hrtf)" >&2; exit 2 ;;
  esac
done
if [ "$HOF2VR_ENV" != 1 ] && [ "$REL" = 1 ]; then
  echo "environment $HOF2VR_ENV: --release is for the user's Steam install (environment 1, after merging to main)" >&2; exit 2
fi
D="$G/Hand of Fate 2_Data"
BEP=$H/downloads/bepinex
ORIG_SHA=$(cut -d' ' -f1 < "$H/backups/20261010-063954-pre-mod/game-sha256.txt" | head -1)

# BepInEx (core files only; keeps BepInEx/config and plugins)
mkdir -p "$G/BepInEx/core" "$G/BepInEx/plugins/HoF2VR"
cp -a "$BEP/BepInEx/core/." "$G/BepInEx/core/"
cp -a "$BEP/libdoorstop.so" "$BEP/run_bepinex.sh" "$BEP/.doorstop_version" "$G/"; chmod +x "$G/run_bepinex.sh"
# atomic replace (a new file renamed over the old one): a running test instance keeps its mapped copy
install -m 644 "$H/build/bin/HoF2VR.dll" "$G/BepInEx/plugins/HoF2VR/.HoF2VR.dll.new"
mv -f "$G/BepInEx/plugins/HoF2VR/.HoF2VR.dll.new" "$G/BepInEx/plugins/HoF2VR/HoF2VR.dll"
# the player's launcher (how their game starts) changes only with a release, so a test install never changes it before the
# coordinator's "release" (2026-10-13: the collector settings went into it); a missing one is put in place at once
if [ "$REL" = 1 ] || [ ! -e "$G/hof2vr_launch.sh" ]; then
  install -m 755 "$H/dist/hof2vr_launch.sh" "$G/.hof2vr_launch.sh.new"
  mv -f "$G/.hof2vr_launch.sh.new" "$G/hof2vr_launch.sh"
fi
# no test leftovers in the game folder (a flag file no longer enables test mode, but it must not linger)
rm -f "$G/hof2vr_testmode.txt"

# the waiting space's asset pack (the title's campfire clearing), built read-only from the game's own bundles
# (docs/loading-v2-design.md §1.4) - rebuilt when the game's title bundle is newer; without it the mod shows the
# simple procedural space
PACK="$H/dist/hof2vr_limbo.pack"
if [ ! -s "$PACK" ] || [ "$D/StreamingAssets/AssetBundles/scene/start_scene" -nt "$PACK" ]; then
  "$HOF2VR_MAIN/work/uyf/py312/bin/python3.12" -I "$HOF2VR_MAIN/work/limbo2/tools/extract_limbo_pack.py" "$D/StreamingAssets/AssetBundles" "$PACK" >/dev/null \
    || echo "limbo pack failed: the simple waiting space will be used"
fi
if [ -s "$PACK" ] && ! cmp -s "$PACK" "$G/BepInEx/plugins/HoF2VR/hof2vr_limbo.pack"; then
  install -m 644 "$PACK" "$G/BepInEx/plugins/HoF2VR/.hof2vr_limbo.pack.new"
  mv -f "$G/BepInEx/plugins/HoF2VR/.hof2vr_limbo.pack.new" "$G/BepInEx/plugins/HoF2VR/hof2vr_limbo.pack"
fi

# MonoMod (HarmonyX) looks for libmonosgen-2.0.so to call mono_mprotect; without it it falls back to
# libMonoPosixHelper.so, which Mono loads RTLD_GLOBAL and whose 222 eglib g_* exports then hijack
# GLib for every later library (GTK2 ScreenSelector.so crashed in gobject_init_ctor). Point it at
# the game's own libmono.so, which exports mono_mprotect/mono_pagesize.
ln -sfn "Hand of Fate 2_Data/Mono/x86_64/libmono.so" "$G/libmonosgen-2.0.so"

# OpenVR client library for Unity's built-in OpenVR device
cp "$H/downloads/openvr/bin/linux64/libopenvr_api.so" "$D/Plugins/x86_64/libopenvr_api.so"

# 3D sound (HRTF, docs/hrtf-plan.md) and data.unity3d. Two patched data files, both made from the original by
# scripts/patch_vr.py: dist/data.unity3d (the VR device list only: --no-hrtf) and dist/data-hrtf.unity3d (also the
# spatializer's name and its plugin in the preload list; made here the first time). A missing or wrong Steam Audio download
# or no HRTF data file installs without HRTF.
SA="$H/downloads/steamaudio-4.8.1"
PATCH() { "$HOF2VR_SHARED/tools-venv/bin/python" -I "$H/scripts/patch_vr.py" "$@" > "$H/logs/patch_vr.log" 2>&1 || { cat "$H/logs/patch_vr.log" >&2; exit 1; }; }
sha() { { sha256sum < "$1" | cut -d' ' -f1; } 2>/dev/null || true; }
mkdir -p "$H/logs"
if [ "$HRTF" = 1 ] && ! "$H/scripts/steamaudio.sh" >/dev/null; then
  echo "HRTF: Steam Audio files missing or failing their checksums (scripts/steamaudio.sh) - installing without 3D sound" >&2
  HRTF=0
fi
if [ ! -s "$H/dist/data-hrtf.unity3d" ] && [ "$(sha "$H/backups/20261010-063954-pre-mod/data.unity3d")" = "$ORIG_SHA" ]; then
  PATCH "$H/backups/20261010-063954-pre-mod/data.unity3d" "$H/dist/data-hrtf.unity3d"
  echo "data.unity3d: made dist/data-hrtf.unity3d from the original"
fi
if [ "$HRTF" = 1 ] && [ ! -s "$H/dist/data-hrtf.unity3d" ]; then
  echo "HRTF: no dist/data-hrtf.unity3d (and no original to make it from) - installing without 3D sound" >&2
  HRTF=0
fi
# Steam Audio's two binaries next to libopenvr_api.so (Unity preloads every .so in Plugins/ at start, so a running game
# maps them: only ever replaced by a rename, never written in place); its notices with the mod
if [ "$HRTF" = 1 ]; then
  for f in libaudioplugin_phonon.so libphonon.so; do
    cmp -s "$SA/linux-x86_64/$f" "$D/Plugins/x86_64/$f" && continue
    install -m 755 "$SA/linux-x86_64/$f" "$D/Plugins/x86_64/.$f.new"
    mv -f "$D/Plugins/x86_64/.$f.new" "$D/Plugins/x86_64/$f"
  done
  mkdir -p "$G/BepInEx/plugins/HoF2VR/licenses/steamaudio"
  install -m 644 "$SA/LICENSE.md" "$SA/THIRDPARTY.md" "$SA/TRADEMARK_RIGHTS.md" "$G/BepInEx/plugins/HoF2VR/licenses/steamaudio/"
  echo "HRTF: Steam Audio 4.8.1 installed (Plugins/x86_64; licences in BepInEx/plugins/HoF2VR/licenses/steamaudio)"
else
  rm -f "$D/Plugins/x86_64/libaudioplugin_phonon.so" "$D/Plugins/x86_64/libphonon.so"
  rm -rf "$G/BepInEx/plugins/HoF2VR/licenses/steamaudio"
  echo "HRTF: not installed - the game's own panning"
fi
# data.unity3d: the wanted patched file; re-patch both from the current file if the game was updated
WANT="$H/dist/data.unity3d"; [ "$HRTF" = 1 ] && WANT="$H/dist/data-hrtf.unity3d"
WHAT="VR only"; [ "$HRTF" = 1 ] && WHAT="VR + HRTF"
cur=$(sha "$D/data.unity3d")
if [ "$cur" = "$(sha "$WANT")" ]; then
  echo "data.unity3d: already patched ($WHAT)"
elif [ "$cur" = "$ORIG_SHA" ] || [ "$cur" = "$(sha "$H/dist/data.unity3d")" ] || [ "$cur" = "$(sha "$H/dist/data-hrtf.unity3d")" ]; then
  cp "$WANT" "$D/.data.unity3d.new" && mv -f "$D/.data.unity3d.new" "$D/data.unity3d"
  echo "data.unity3d: patched ($WHAT)"
else
  echo "data.unity3d: game file changed (update?) -> re-patching from it"
  cp "$D/data.unity3d" "$H/reference/data.unity3d.current"
  PATCH --no-hrtf "$H/reference/data.unity3d.current" "$H/dist/data.unity3d"
  PATCH "$H/reference/data.unity3d.current" "$H/dist/data-hrtf.unity3d"
  cp "$WANT" "$D/.data.unity3d.new" && mv -f "$D/.data.unity3d.new" "$D/data.unity3d"
  echo "data.unity3d: patched ($WHAT)"
fi

# "Hand of Fate 2 VR" in WiVRn's app list (and the desktop menu). It starts the play wrapper, which makes
# room for the player (stops a test instance that Steam would otherwise count as "running") and then asks
# Steam to start the game. The wrapper lives in a folder without spaces (desktop Exec quoting).
# (Environment 1 only: the entry and the wrapper belong to the user's Steam install.)
if [ "$HOF2VR_ENV" = 1 ]; then
mkdir -p "$HOME/.local/share/hof2vr"
install -m 755 "$H/dist/hof2vr_play.sh" "$HOME/.local/share/hof2vr/hof2vr_play.sh"
sed "s|@PLAY@|$HOME/.local/share/hof2vr/hof2vr_play.sh|" "$H/dist/Hand of Fate 2 VR.desktop" > "$HOME/.local/share/applications/.hof2vr.desktop.new"
mv -f "$HOME/.local/share/applications/.hof2vr.desktop.new" "$HOME/.local/share/applications/Hand of Fate 2 VR.desktop"
fi
if [ "$REL" = 1 ]; then
  mkdir -p "$G/BepInEx/hof2vr_release"
  install -m 644 "$G/BepInEx/plugins/HoF2VR/HoF2VR.dll" "$G/BepInEx/hof2vr_release/.HoF2VR.dll.new"
  mv -f "$G/BepInEx/hof2vr_release/.HoF2VR.dll.new" "$G/BepInEx/hof2vr_release/HoF2VR.dll"
  for f in "$G/BepInEx/plugins/HoF2VR/"*.pack; do
    [ -f "$f" ] && install -m 644 "$f" "$G/BepInEx/hof2vr_release/${f##*/}"
  done
  echo "RELEASE: the player's launches use this build"
fi
echo "installed $(date +%T) in env $HOF2VR_ENV (DLL sha1 $(sha1sum < "$G/BepInEx/plugins/HoF2VR/HoF2VR.dll" | cut -c1-8); release $(sha1sum < "$G/BepInEx/hof2vr_release/HoF2VR.dll" 2>/dev/null | cut -c1-8))"

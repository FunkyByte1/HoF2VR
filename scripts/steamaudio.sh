#!/usr/bin/env bash
# Steam Audio 4.8.1 (Valve, Apache-2.0) for the HRTF spatializer (docs/hrtf-plan.md): the two Linux x86_64 binaries and
# the three licence notices in downloads/steamaudio-4.8.1/ (ignored by git: binaries never go into git), checked against
# the sums pinned below.
#   steamaudio.sh            make sure the folder is there and right (copy or fetch it if not)
#   steamaudio.sh --check    only check it (exit 1 if missing or wrong)
# Where the files come from, first that works:
#   1. the main worktree's downloads/steamaudio-4.8.1/ (reflinks; another environment's install);
#   2. the research download in the main worktree's work/hrtf/downloads/ (docs/hrtf-plan.md, Appendix A);
#   3. GitHub: the v4.8.1 release zip (its sha256 pinned) and the v4.8.1 tag's LICENSE.md and TRADEMARK_RIGHTS.md. The zip
#      goes into a new empty folder and is only read (python -I, zipfile + tarfile: the unitypackage's two entries by
#      their pinned GUIDs, their pathnames checked); nothing from it is run.
# install.sh copies the binaries to <Data>/Plugins/x86_64/ and the notices to BepInEx/plugins/HoF2VR/licenses/steamaudio/.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/hof2env.sh"
SA="$H/downloads/steamaudio-4.8.1"
ZIP_URL=https://github.com/ValveSoftware/steam-audio/releases/download/v4.8.1/steamaudio_unity_4.8.1.zip
ZIP_SHA=9b7e3689561670e6b4b8e0c579a2d951bb4a0589f7d46c23bc9df923d62940f0
RAW_URL=https://raw.githubusercontent.com/ValveSoftware/steam-audio/v4.8.1
SUMS='0fb5d1662c3b2144abc9977d8ab88ad94ba02f5c68dfc5fe4855aa25a8dfe592  linux-x86_64/libaudioplugin_phonon.so
faf7c4d69e0f104366f6553cda38d3f946f761f7db223baf12663f6fbf2387da  linux-x86_64/libphonon.so
cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30  LICENSE.md
a95e143e7d2466e82a28d6680c7287d9fc7d772136c323ab538a6d24472e70f0  THIRDPARTY.md
b768a7787a6456b7bbd8f8bfa872331c661cf52577ea699117fa7c667a06b76b  TRADEMARK_RIGHTS.md'

check() {   # the folder holds exactly the pinned files
  [ -d "$SA" ] && [ "$(cat "$SA/SHA256SUMS" 2>/dev/null)" = "$SUMS" ] && (cd "$SA" && sha256sum --quiet -c SHA256SUMS) >/dev/null 2>&1
}
place() {   # place SRC_DIR (with linux-x86_64/ and the .md files): copy into a new folder, check, then swap it in
  local src=$1 tmp="$SA.new.$$"
  rm -rf "$tmp"; mkdir -p "$tmp/linux-x86_64"
  cp --reflink=auto "$src/linux-x86_64/libaudioplugin_phonon.so" "$src/linux-x86_64/libphonon.so" "$tmp/linux-x86_64/" &&
    cp --reflink=auto "$src/LICENSE.md" "$src/THIRDPARTY.md" "$src/TRADEMARK_RIGHTS.md" "$tmp/" || { rm -rf "$tmp"; return 1; }
  chmod 644 "$tmp/linux-x86_64/"*.so "$tmp/"*.md
  printf '%s\n' "$SUMS" > "$tmp/SHA256SUMS"
  if ! (cd "$tmp" && sha256sum --quiet -c SHA256SUMS); then rm -rf "$tmp"; return 1; fi
  rm -rf "$SA"; mv "$tmp" "$SA"
}

if [ "${1:-}" = "--check" ]; then
  if check; then echo "steamaudio: $SA OK"; exit 0; else echo "steamaudio: $SA missing or wrong" >&2; exit 1; fi
fi
if check; then echo "steamaudio: $SA OK"; exit 0; fi
if [ "$HOF2VR_MAIN" != "$H" ] && [ -d "$HOF2VR_MAIN/downloads/steamaudio-4.8.1" ] && place "$HOF2VR_MAIN/downloads/steamaudio-4.8.1"; then
  echo "steamaudio: copied from the main worktree"; exit 0
fi
R="$HOF2VR_MAIN/work/hrtf/downloads/steamaudio-unity-4.8.1-x"
if [ -d "$R/linux-x86_64" ] && place "$R"; then echo "steamaudio: copied from the research download ($R)"; exit 0; fi
# 3. GitHub (a new empty folder for the download; only read, never run)
DL="$H/downloads/steamaudio-4.8.1-fetch.$$"
mkdir "$DL" "$DL/out" "$DL/out/linux-x86_64"
trap 'rm -rf "$DL"' EXIT
echo "steamaudio: downloading $ZIP_URL (139 MB)"
curl -fL --retry 2 -o "$DL/steamaudio_unity_4.8.1.zip" "$ZIP_URL"
[ "$(sha256sum < "$DL/steamaudio_unity_4.8.1.zip" | cut -d' ' -f1)" = "$ZIP_SHA" ] || { echo "steamaudio: the zip's sha256 is not the pinned one" >&2; exit 1; }
curl -fL --retry 2 -o "$DL/out/LICENSE.md" "$RAW_URL/LICENSE.md"
curl -fL --retry 2 -o "$DL/out/TRADEMARK_RIGHTS.md" "$RAW_URL/TRADEMARK_RIGHTS.md"
python3 -I - "$DL/steamaudio_unity_4.8.1.zip" "$DL/out" <<'PY'
import io, sys, tarfile, zipfile
zpath, out = sys.argv[1], sys.argv[2]
want = {"45e1d12e441bb9a4d875c1c402cc3bab": "Assets/Plugins/SteamAudio/Binaries/Linux/x86_64/libaudioplugin_phonon.so",
        "c9ba4a34d9f98f045ac750ca5a13cc0a": "Assets/Plugins/SteamAudio/Binaries/Linux/x86_64/libphonon.so"}
with zipfile.ZipFile(zpath) as z:
    open(out + "/THIRDPARTY.md", "wb").write(z.read("steamaudio_unity/THIRDPARTY.md"))
    pkg = z.read("steamaudio_unity/unity/SteamAudio.unitypackage")
with tarfile.open(fileobj=io.BytesIO(pkg), mode="r:gz") as t:
    for guid, path in want.items():
        name = t.extractfile(guid + "/pathname").read().decode().splitlines()[0].strip()
        if name != path:
            raise SystemExit("unexpected pathname for %s: %s" % (guid, name))
        data = t.extractfile(guid + "/asset").read()
        open(out + "/linux-x86_64/" + path.rsplit("/", 1)[1], "wb").write(data)
print("extracted 2 binaries and THIRDPARTY.md")
PY
place "$DL/out" || { echo "steamaudio: the fetched files fail the pinned sums" >&2; exit 1; }
echo "steamaudio: fetched from GitHub into $SA"

# HoF2VR: Hand of Fate 2 in VR

A from-scratch VR mod for the native Linux version of **Hand of Fate 2**, built as a BepInEx 5 plugin.

- You sit or stand at the Dealer's table in first person and play the cards with your hands.
- Fights are first person, with motion-controlled sword and shield.
- You are never moved without your own input: places change with a short fade, and every view is set to your own
  height and position.
- Settings, comfort options and posture (standing or seated) can be changed in VR.

**Status: alpha, work in progress.** It is developed and tested on one machine: Linux (Nobara), an AMD GPU and a Meta
Quest 2 streamed with [WiVRn](https://github.com/WiVRn/WiVRn). The scripts still assume that machine's folders, and
there is no ready-made download yet.

## How it works

The game's Unity OpenVR device talks to [OpenComposite](https://gitlab.com/znixian/OpenOVR), which hands it on to an
OpenXR runtime (WiVRn or Monado). The game runs with OpenGL Core (`-force-glcore`). The plugin hooks the game with
Harmony. See [DESIGN.md](DESIGN.md) for the rendering and platform decisions.

## Building

You need your own copy of Hand of Fate 2 on Steam (the Linux version) and the .NET SDK.

1. Copy the game's managed DLLs (`Hand of Fate 2_Data/Managed/*.dll`) to `reference/managed/`.
2. Unpack BepInEx 5.4.23.5 for Linux x64 to `downloads/bepinex/`.
3. Run `scripts/build.sh`. The plugin lands in `build/bin/HoF2VR.dll`.

`scripts/install.sh` installs BepInEx, the plugin and the OpenVR library into the Steam game folder. It also patches
the game's `data.unity3d` on your machine so that Unity starts in VR, working from an untouched copy of the original
that it expects under `backups/`. It still uses paths from the author's machine, so read it before running it. Some optional parts, such as the new narration
voices, are not in this repository.

## Combat

The combat system is modelled closely on **Until You Fall** (Schell Games). Its rules and numbers (from that game's
data) were used as the reference for blocking, dodging, enemy turns and weapon feel. No code, art, sounds or meshes
from Until You Fall are included.

## Third-party files

- `src/HoF2VR/ThirdParty/openvr_api.cs`: Valve's OpenVR C# bindings (BSD 3-Clause, `OPENVR_LICENSE`).
- `src/HoF2VR/ThirdParty/SteamAudio/`: Valve's Steam Audio C# API (Apache 2.0, `LICENSE.md`, `THIRDPARTY.md`). Its
  native libraries are downloaded at install time (`scripts/steamaudio.sh`).
- BepInEx and Harmony are not included; they keep their own licences.

## Notes

- Not affiliated with or endorsed by Defiant Development. No game files are included: you need your own copy.
- Most of the code was written by Claude (Anthropic's AI model) under the author's direction; the commit messages say
  so.
- Licence: MIT, see [LICENSE](LICENSE). Third-party files keep their own licences.

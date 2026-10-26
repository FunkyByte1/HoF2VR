#!/usr/bin/env python3
"""Placeholder narration clips for the tests (the real voice is not chosen yet): a tiny clip folder in the layout the
mod reads (Narration/ClipStore.cs), made from round-9 sample readings, keyed to real string hashes.

  <tts venv python> -I scripts/narr_testclips.py      (numpy for the motion data; ffmpeg for Vorbis and the paces)
  -> testdata/narration/clips/{Normal,Slow,Fast}/<hash>.ogg + <hash>.motion, vo/<line>.ogg + .motion
     testdata/narration/clips/Normal/<page0 hash>.curves.tsv   (motion9.curves() at 60 Hz: the C# parity reference)
     testdata/narration/clips/index.tsv                       (what each clip stands for)
     testdata/narration/hash_all.tsv                          (every corpus text + its hash: `narr selftest`)

Pages: the encounter "Forgotten Dreams" (the playtester's narration stage jumps there): its pages 0-7, each with a
round-9 narrator sample cut to a few seconds. The Dealer's lines: his activation line of that card, the dice lines (one
by its name under vo/, one only by its transcript's hash, to test both lookups) and round 9's other re-voiced lines.
Slow / Fast = Normal time-stretched to 136 / 180 wpm from 158 (ffmpeg atempo), their motion data analysed again.
"""
import json
import os
import subprocess
import sys
import tempfile
import wave

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import hof2env  # noqa: E402

WORK = "/home/bara/Projects/flat2vr/HandOfFate2/work/narration"   # shared by the environments (docs/two-environments.md)
sys.path.insert(0, WORK)
import motion9  # noqa: E402
from narr_text import text_hash  # noqa: E402

OUT = os.path.join(hof2env.H, "testdata", "narration", "clips")
S9 = os.path.join(WORK, "samples9")
PACES = {"Normal": 1.0, "Slow": 136.0 / 158.0, "Fast": 180.0 / 158.0}


def ff(*a):
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y"] + list(a), check=True)


def read24(path):
    with wave.open(path) as w:
        assert w.getframerate() == 24000 and w.getnchannels() == 1 and w.getsampwidth() == 2, path
        return np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0


def motion_file(path, m, extra=None):
    cols = motion9.to_tsv(m)
    with open(path, "w") as f:
        f.write("dur\t%.3f\n" % m["dur"])
        for k in ("beats", "still", "phrases", "gestures", "env25"):
            f.write("%s\t%s\n" % (k, cols[k]))
        for k, v in (extra or {}).items():
            f.write("%s\t%s\n" % (k, v))


def make(src, start, length, text, rel, key, tmp, orig=None):
    """One clip in every pace folder: src[start:start+length] (seconds), 30 ms fades, Vorbis q3 mono 24 kHz.
    orig = (triggers, duration) of the game's line: its gestures, re-timed onto the clip (design §7.3), replace the
    generated ones (names and order kept, as the voice side will ship them)."""
    out = []
    for pace, tempo in PACES.items():
        wav = os.path.join(tmp, "%s_%s.wav" % (pace, key))
        af = "afade=t=in:d=0.03,afade=t=out:st=%.3f:d=0.25" % max(0.0, length - 0.25)
        if tempo != 1.0:
            af += ",atempo=%.4f" % tempo
        ff("-ss", "%.3f" % start, "-t", "%.3f" % length, "-i", src, "-ac", "1", "-ar", "24000", "-af", af, wav)
        dst = os.path.join(OUT, pace, rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        ff("-i", wav, "-c:a", "libvorbis", "-q:a", "3", dst + ".ogg")
        m = motion9.analyze(read24(wav), text, key)
        if orig is not None:
            spans = [(a, b) for a, b, _s in m["phrases"]]
            m["gestures"], _how = motion9.retime(orig[0], [], spans, orig[1], m["dur"])
        motion_file(dst + ".motion", m)
        out.append((pace, m))
    return out


def main():
    corpus = json.load(open(os.path.join(WORK, "corpus.json")))["strings"]
    inv = {c["name"]: c for c in json.load(open(os.path.join(S9, "vo_inventory.json")))["clips"]}
    pages = sorted(((v.get("page"), v["hash"], v["text"]) for k, v in corpus.items()
                    if k.startswith("ForgottenDreams|") and v["category"] == "encounter_page" and v.get("narrate")),
                   key=lambda r: r[0])
    seen, rows = set(), []
    voices = ["mystic_storyteller", "grandma", "ancient_female_narrator", "old_lady_deep", "crone", "mrs_witch"]
    os.makedirs(OUT, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        for i, (page, h, text) in enumerate(pages):
            if h in seen:
                continue
            seen.add(h)
            v = voices[i % len(voices)]
            src = os.path.join(S9, "narrators", v + ".wav")
            length = 14.0 if page == 0 else 5.0 + (i % 3)
            res = make(src, 0.0, length, text, h, h, tmp)
            rows.append((h, "page", "ForgottenDreams p%s" % page, v, "%.2f" % res[0][1]["dur"], text[:70]))
            if page == 0:
                c = motion9.curves(res[0][1], fps=60, gain=1.0)
                with open(os.path.join(OUT, "Normal", h + ".curves.tsv"), "w") as f:
                    f.write("# t pitch roll yaw (motion9.curves, 60 Hz, gain 1)\n")
                    for k in range(len(c["t"])):
                        f.write("%.5f\t%.6f\t%.6f\t%.6f\n" % (c["t"][k], c["pitch"][k], c["roll"][k], c["yaw"][k]))
        # the Dealer's own lines: by name (vo/<name>) and one only by its transcript's hash
        lines = [("VO_ENCOUNTER_ForgottenDreams_1", "vo/mystic_storyteller__VO_ENCOUNTER_ForgottenDreams_1.wav", "name"),
                 ("VO_DYN_PLAYDICE_1", "vo/mystic_storyteller__VO_MINIGAME_DICE_1.wav", "name"),
                 ("VO_MINIGAME_DICE_1", "vo/mystic_storyteller__VO_MINIGAME_DICE_1.wav", "name"),
                 ("VO_EQUIPMENT_EXQUISITE_BLADE_2", "vo/mystic_storyteller__VO_EQUIPMENT_EXQUISITE_BLADE_2.wav", "name"),
                 ("VO_EQUIPMENT_ABSOLVED_PLIGHT_1", "vo/mystic_storyteller__VO_EQUIPMENT_ABSOLVED_PLIGHT_1.wav", "name"),
                 ("VO_DYN_WINDICE_1", "vo/grandma__VO_MINIGAME_DICE_1.wav", "hash")]
        for name, wav, how in lines:
            c = inv[name]
            src = os.path.join(S9, wav)
            n = float(subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", src],
                                     capture_output=True, text=True).stdout)
            h = text_hash(c["text"])
            rel = os.path.join("vo", name) if how == "name" else h
            res = make(src, 0.0, n, c["text"], rel, name, tmp, orig=(c["triggers"], c["dur"]))
            rows.append((h if how == "hash" else name, "line by " + how, name, os.path.basename(wav), "%.2f (original %.2f)" % (res[0][1]["dur"], c["dur"]), c["text"][:70]))
    # every corpus text with its hash (the mod's `narr selftest` reads it: the C# hash against the pipeline's)
    seen = set()
    with open(os.path.join(os.path.dirname(OUT), "hash_all.tsv"), "w", encoding="utf-8", newline="") as f:
        for v in corpus.values():
            t = v["text"]
            if t in seen:
                continue
            seen.add(t)
            f.write(text_hash(t) + "\t" + t.replace("\\", "\\\\").replace("\n", "\\n").replace("\t", "\\t") + "\n")
    with open(os.path.join(OUT, "index.tsv"), "w") as f:
        f.write("# key\tkind\twhat\tsource\tdur_normal\ttext\n")
        for r in rows:
            f.write("\t".join(r) + "\n")
    print("%d placeholder clips x %d paces -> %s" % (len(rows), len(PACES), OUT))
    for r in rows:
        print("  " + "  ".join(r))


if __name__ == "__main__":
    main()

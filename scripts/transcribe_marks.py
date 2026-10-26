#!/usr/bin/env python3
"""Transcribe the voice memos of a play session's bug marks (local faster-whisper, CPU; the model from the Hugging Face
cache, nothing downloaded at run time).

  shared/tools-venv/bin/python HandOfFate2/scripts/transcribe_marks.py <session folder> [out.tsv]

Prints one line per mark: its folder name, the game state from state.txt, and what was said. With out.tsv, writes the
same as tab-separated lines.
"""
import os
import sys
import wave

import numpy as np

from faster_whisper import WhisperModel

MODEL = "mobiuslabsgmbh/faster-whisper-large-v3-turbo"


def load16k(path):
    """The memo as 16 kHz mono float32 (faster-whisper's own decoder needs a newer PyAV than this venv has)."""
    with wave.open(path) as w:
        ch, rate, n = w.getnchannels(), w.getframerate(), w.getnframes()
        a = np.frombuffer(w.readframes(n), dtype=np.int16).astype(np.float32) / 32768.0
    if ch > 1:
        a = a.reshape(-1, ch).mean(axis=1)
    if rate != 16000:
        t_old = np.arange(len(a)) / rate
        t_new = np.arange(int(len(a) * 16000 / rate)) / 16000.0
        a = np.interp(t_new, t_old, a).astype(np.float32)
    return a


def state_of(d):
    try:
        lines = open(os.path.join(d, "state.txt")).read().split("\n")
    except OSError:
        return "?"
    head = lines[1] if len(lines) > 1 else ""
    menu = next((l for l in lines if l.startswith("menu ")), "")
    return (head.replace("game state ", "") + " " + menu).strip()


def main():
    sess = sys.argv[1]
    marks = os.path.join(sess, "marks")
    model = WhisperModel(MODEL, device="cpu", compute_type="int8", local_files_only=True)
    rows = []
    for name in sorted(os.listdir(marks), key=lambda n: n.split("_")[0]):
        wav = os.path.join(marks, name, "voice.wav")
        if not os.path.exists(wav):
            text = "(no voice clip)"
        else:
            segs, _ = model.transcribe(load16k(wav), language="en", vad_filter=True, beam_size=5)
            text = " ".join(s.text.strip() for s in segs).strip() or "(nothing said)"
        row = (name, state_of(os.path.join(marks, name)), text)
        rows.append(row)
        print("%s | %s | %s" % row, flush=True)
    if len(sys.argv) > 2:
        with open(sys.argv[2], "w") as f:
            for r in rows:
                f.write("\t".join(r) + "\n")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Playtest 3 (env 1): stay at the table N seconds (the Steam check: a test run with nothing else going on)."""
import sys
import time
time.sleep(float(sys.argv[1]) if len(sys.argv) > 1 else 60)

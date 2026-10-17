#!/usr/bin/env python3
"""Second-machine helper for multi-machine tests: run with TLD_REMOTE set. Protects that machine's saves for the
whole session, then executes bridge commands read from stdin (one per line) and prints each JSON result on one line.
Special lines: `!seeded` (fresh world with the test seed), `!sleep <s>`. Ends (restoring autosaves) on EOF."""
import sys, json, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge

with game.ProtectedSession():
    if not game.bridge_up(): game.launch()
    print(json.dumps({"ready": True}), flush=True)
    for line in sys.stdin:
        line = line.strip()
        if not line: continue
        try:
            if line == "!seeded":
                seeded.restart_same_seed(); Bridge()("set TLDRevamp.TestSafety.BlockAutoSave True"); r = {"ok": True}
            elif line.startswith("!sleep"):
                time.sleep(float(line.split()[1])); r = {"ok": True}
            else:
                r = Bridge()(line)
        except Exception as e:
            r = {"error": str(e)[:300]}
        print(json.dumps(r), flush=True)

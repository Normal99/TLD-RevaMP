#!/usr/bin/env python3
"""Car group round trip (itemsnap group) with a structural comparison: every field of every record, world attach
points (gpos) ignored (they move with the copy), floats compared to 1e-3."""
import sys, json, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from determinism import go_global, glob

diffs = []
def cmp(a, b, path):
    if isinstance(a, dict) and isinstance(b, dict):
        for k in sorted(set(a) | set(b)):
            if k == "gpos": continue
            if k not in a or k not in b: diffs.append((path + "/" + k, "missing")); continue
            cmp(a[k], b[k], path + "/" + k)
    elif isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b): diffs.append((path, f"len {len(a)} vs {len(b)}")); return
        for i, (x, y) in enumerate(zip(a, b)): cmp(x, y, f"{path}[{i}]")
    elif isinstance(a, float) or isinstance(b, float):
        if abs(float(a) - float(b)) > 1e-3: diffs.append((path, f"{a} vs {b}"))
    elif a != b: diffs.append((path, f"{a} vs {b}"))

with game.ProtectedSession():
    seeded.restart_same_seed()
    b = Bridge(); b("set TLDRevamp.TestSafety.BlockAutoSave True"); b("set kaposztaleves.s.settings.god True")
    h = glob(b); go_global(b, h[0] + 60, h[2] + 40); time.sleep(3)
    me = glob(b); g = b(f"ground {me[0] + 6} {me[2]}")
    b(f"spawnpile 2 1 {me[0] + 6} {g['y']} {me[2]} 1 1 1.0 4242"); time.sleep(5)
    b("set TLDRevamp.Net.ItemSnapshot.Full True")
    r = b("itemsnap group")
    print({k: v for k, v in r.items() if k not in ("origJson", "copyJson", "orig", "copy")})
    cmp(r["origJson"], r["copyJson"], "")
    counts = {k: len(v) for k, v in r["origJson"].items() if isinstance(v, list) and v}
    print("record lists:", counts)
    print(f"differences beyond tolerance (gpos ignored): {len(diffs)}")
    for d in diffs[:25]: print("  ", d)

#!/usr/bin/env python3
"""A joiner can't save the host's world into their own slots (JoinerSaveBlock, v0.66.9). Two machines over the relay:
  A  the laptop, joined, presses the Save menu's button under a test name: no file is written, the block counts it
  B  the host does the same: its save is written (and deleted again by the probe), the host saves as before
  tools/joinersave.py [--no-build] [--relay iad|off] [--old: the block off, the joiner's save is written]
The probe (`mp trysave`) deletes any file it made and puts the game's "last save" back."""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
NAME = "revamp-joinersave-probe"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        L(f"set TLDRevamp.JoinerSaveBlock.Enabled {not OLD}")
        print(f"  {'OLD (a joiner saves the host world)' if OLD else 'CURRENT BUILD'}", flush=True)
        time.sleep(3)
        r = L(f"mp trysave {NAME}"); print("  laptop:", r, flush=True)
        verdict("A THE JOINER'S SAVE IS NOT WRITTEN", r.get("written") is False and r.get("blocked") == 1 and not r.get("fault"), str(r))
        r = h(f"mp trysave {NAME}"); print("  host:", r, flush=True)
        verdict("B THE HOST'S SAVE IS WRITTEN", r.get("written") is True and r.get("blocked") == 0, str(r))
        er = [x_("errors") for x_ in (h, L)]
        print("  errors:", [(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er], flush=True)
    finally:
        try: L("set TLDRevamp.JoinerSaveBlock.Enabled True")
        except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"JOINERSAVE: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")

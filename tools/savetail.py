#!/usr/bin/env python3
"""The host's save when a session ends (Mp.SaveTail) against the game's own autosave rules. Desktop only.
  A  `mp save` with the old behaviour (RotateOnSaveTail False): autosave1 written over, autosave2 untouched — the
     previous autosave1 is lost (expected)
  B  `mp save` with the fix: the game's rotation first — autosave2 is now what autosave1 was, autosave3 what 2 was
  C  ending a session with the player's autosave off (TestAutosaveOff: the real setting is never touched): no save
  D  ending a session with autosave on: saved (rotated)
The real autosave slots are backed up and restored (ProtectedSession).
  tools/savetail.py [--no-build]"""
import sys, time, hashlib, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import ensure_both

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def slots():
    """autosave data files: slot -> md5 (None: missing)"""
    out = {}
    for i in (1, 2, 3):
        p = game.SAVES / f"TLDsaveData_autosave{i}.tlds"
        out[i] = hashlib.md5(p.read_bytes()).hexdigest()[:10] if p.exists() else None
    return out

ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    seeded.restart_same_seed(); b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True")   # the game's periodic autosave off; the saves below are ours
    try:
        b("mp host 27070"); time.sleep(3)
        # make sure all three slots exist and differ (a fresh world may have fewer)
        for _ in range(3):
            b("mp save"); time.sleep(1.5)
        s0 = slots(); print("  slots at start:", s0, flush=True)

        b("set TLDRevamp.Net.Mp.RotateOnSaveTail False")
        r = b("mp save"); time.sleep(1); s1 = slots(); print("  A old:", r, s1, flush=True)
        verdict("A OLD: AUTOSAVE1 WRITTEN OVER, THE PREVIOUS ONE LOST (expected)", s1[2] == s0[2] and s1[1] != s0[1] and s0[1] not in s1.values(),
                f"before {s0}, after {s1}")

        b("set TLDRevamp.Net.Mp.RotateOnSaveTail True")
        r = b("mp save"); time.sleep(1); s2 = slots(); print("  B fix:", r, s2, flush=True)
        verdict("B FIX: THE GAME'S ROTATION (1 -> 2 -> 3), THEN A NEW AUTOSAVE1", r.get("rotated") is True and s2[2] == s1[1] and s2[3] == s1[2] and s2[1] != s1[1],
                f"before {s1}, after {s2}")

        b("set TLDRevamp.Net.Mp.TestAutosaveOff True"); b("set TLDRevamp.TestSafety.BlockAutoSave False")
        k0 = b("get TLDRevamp.Net.Mp.SaveTailSkipped")["value"]
        b("mp stop"); time.sleep(1.5); s3 = slots()
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        verdict("C AUTOSAVE OFF: ENDING THE SESSION WRITES NOTHING", s3 == s2 and b("get TLDRevamp.Net.Mp.SaveTailSkipped")["value"] == k0 + 1,
                f"before {s2}, after {s3}")

        b("set TLDRevamp.Net.Mp.TestAutosaveOff False")
        b("mp host 27070"); time.sleep(3)
        b("set TLDRevamp.TestSafety.BlockAutoSave False")
        b("mp stop"); time.sleep(1.5); s4 = slots()
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        verdict("D AUTOSAVE ON: ENDING THE SESSION SAVES, ROTATED", s4[2] == s3[1] and s4[1] != s3[1], f"before {s3}, after {s4}")
    finally:
        for k, v in (("TLDRevamp.Net.Mp.TestAutosaveOff", False), ("TLDRevamp.Net.Mp.RotateOnSaveTail", True), ("TLDRevamp.TestSafety.BlockAutoSave", True)):
            try: b(f"set {k} {v}")
            except Exception as ex: print("restore", k, ex)
        try: b("mp stop")
        except Exception: pass
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

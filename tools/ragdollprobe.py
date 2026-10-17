#!/usr/bin/env python3
"""Which items have multi-body poses (tosaveitemscript.ragdollPos) or creature ragdolls, and what is near the start.
Desktop only.  tools/ragdollprobe.py"""
import sys, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
game.deploy_local()
game.quit_game(); game.launch()
with game.ProtectedSession():
    seeded.restart_same_seed()
    b = Bridge(); b("set TLDRevamp.TestSafety.BlockAutoSave True")
    r = b("mp ragdolls")
    print("[index, name, ragdollPos childs, ragdollPos pickupables, creature RBs, kind]")
    for row in r.get("ragdolls", []): print(" ", row)
    if "error" in r: print(r)

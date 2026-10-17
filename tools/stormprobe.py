#!/usr/bin/env python3
"""Sandstorms on the beta: where is the spawner, what is the storm prefab, which biome is the start in. Desktop only.
  tools/stormprobe.py"""
import sys, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
game.deploy_local()
game.quit_game(); game.launch()
with game.ProtectedSession():
    seeded.restart_same_seed()
    b = Bridge(); b("set TLDRevamp.TestSafety.BlockAutoSave True")
    print(b("mp storms"))
    print("mainscript.s.sandStormSpawn:", b("get mainscript.s.sandStormSpawn"))
    print("prefab:", b("mp storms prefab"))
    print("biome setting:", b("get gamemodesettingsscript.s.S.biome"))

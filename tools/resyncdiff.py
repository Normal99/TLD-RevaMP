#!/usr/bin/env python3
"""Single machine: host a session, share a car, drive it, and list which parts of its items' records keep changing
(the resync sends an item whenever its record changes — only real state should)."""
import sys, time, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import running_build, local_build_hash

if "--no-build" not in sys.argv: game.build()
if running_build() != local_build_hash(): game.quit_game(); game.launch()
with game.ProtectedSession():
    seeded.restart_same_seed(); b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True"); b("set kaposztaleves.s.settings.god True")
    b("mp host 27070")
    rp = b("roadpoint"); x, z = rp["x"], rp["z"]; g = b(f"ground {x} {z}")
    b(f"teleportyaw {x} {g['y'] + 1.5} {z + 4} 0")
    b(f"spawnpile 2 1 {x} {g['y']} {z} 1 1 1.0 4242"); time.sleep(4)
    b(f"carface {x} {g['y']} {z} {rp['yaw'] + 180}"); time.sleep(2)
    b(f"drivein {x} {g['y']} {z}"); time.sleep(2); b(f"drivein {x} {g['y']} {z}"); time.sleep(3)
    net = max(e["net"] for e in b("mp entities")["entities"] if e["part"] < 0)
    print("parked:", b(f"mp resyncdiff {net}")["changed"]); time.sleep(3)
    r = b(f"mp resyncdiff {net}"); print("parked, 3 s later:", r["changed"], json.dumps(r["diffs"], indent=1)[:3000])
    b("autodrive on 20"); time.sleep(5); b(f"mp resyncdiff {net}"); time.sleep(3)
    r = b(f"mp resyncdiff {net}"); print("driving:", r["changed"]); print(json.dumps(r["diffs"], indent=1)[:6000])
    b("autodrive off"); b("mp stop"); b("set kaposztaleves.s.settings.god False")

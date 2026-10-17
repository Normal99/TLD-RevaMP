#!/usr/bin/env python3
"""One game: does a parked car keep its parts when the player leaves and comes back across the item removal
distance? (Labs/StreamLab.cs). Runs N cars/margins; prints parts before → after."""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import running_build, local_build_hash

if "--no-build" not in sys.argv: game.build()
if running_build() != local_build_hash(): game.quit_game(); game.launch()
sets = [a for a in sys.argv[1:] if a.startswith("set:")]
with game.ProtectedSession():
    seeded.restart_same_seed(); b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True"); b("set kaposztaleves.s.settings.god True")
    for s_ in sets: b("set " + s_[4:])
    rp = b("roadpoint")
    results = []
    for n, margin in enumerate([0.5, 0.3, 0.7, 0.5]):
        x, z = rp["x"] + 30 * n, rp["z"] + 25
        g = b(f"ground {x} {z}")
        b(f"teleportyaw {x} {g['y'] + 1.5} {z - 8} 0")
        b(f"spawnpile 30 1 {x} {g['y']} {z} 1 1 1.0 {4242 + n}"); time.sleep(4)
        b(f"carface {x} {g['y']} {z} {90 * n}"); time.sleep(3)
        try: print(" ", b(f"streamlab start {margin}")["log"])
        except RuntimeError as ex:
            b.f.write("streamlab status\n"); b.f.flush(); b.f.readline()
            print("  ", ex); break
        t = time.time()
        while b("streamlab status")["running"] and time.time() - t < 30: time.sleep(0.5)
        st = b("streamlab status"); results.append(st)
        print(f"  car {n}: {st['log']}")
    b("set kaposztaleves.s.settings.god False")
    lost = sum(r["before"] - r["after"] for r in results if r["after"] >= 0)
    print(f"parts lost over {len(results)} cars: {lost}")

#!/usr/bin/env python3
"""One machine: spawn car06 on the road, autodrive 90 s, report stability (off-road, obstacles, U-turns)
and probe pois along the way (where are the buildings?)."""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge

def pos(b):
    p = b("get mainscript.s.player.transform.position")["value"]
    return tuple(float(v) for v in p.strip("()").split(","))

with game.ProtectedSession():
    seeded.restart_same_seed(); b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True"); b("set kaposztaleves.s.settings.god True")
    rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
    F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
    print("pois near start:", [(p_["name"][:20], p_["d"], p_["items"]) for p_ in b(f"pois {rp['x']} {rp['z']} 700")["pois"]][:10])
    cx, cz = rp["x"] + F[0] * 8, rp["z"] + F[1] * 8
    g = b(f"ground {cx} {cz}")
    b(f"spawnpile 223 1 {cx} {g['y']} {cz} 1 1 1.0 4242"); time.sleep(3)
    b(f"carface {cx} {g['y']} {cz} {yaw}"); time.sleep(1)

    b(f"drivein {cx} {g['y']} {cz}"); time.sleep(2); b(f"drivein {cx} {g['y']} {cz}"); time.sleep(3)
    print("  fuelup:", b("fuelup"))   # seated now: the spawner can roll diesel + petrol, fuel what the engine drinks
    p0 = pos(b)
    b("autodrive on 20")
    for i in range(6):
        time.sleep(15)
        p = pos(b); st = b("autodrive")
        pois = b(f"pois {p[0]} {p[2]} 400")["pois"]
        print(f"  {15*(i+1):>3} s: {math.dist(p[:2], p0[:2]):>4.0f} m, speed {st['speed']:.0f}, offRoad {st['offRoadFrames']}, "
              f"cross {st['maxCrossTrack']:.0f} m, stuck {st['stuckSeconds']:.0f} s, obstacle '{st['lastObstacle']}', pois {len(pois)}")
        if st.get("stuckSeconds", 0) > 6: b("nudge 12")
    b("autodrive off")
    print("final:", b("autodrive"))

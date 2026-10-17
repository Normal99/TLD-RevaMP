#!/usr/bin/env python3
"""One machine: drive WEST along the main road and probe pois every 15 s — where do buildings start?"""
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
    rp = b("roadpoint"); yaw = rp["yaw"]          # west = the bone's own direction (we drove yaw+180 east before)
    F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
    print("start pois:", [(p_["name"][:16], int(p_["d"]), p_["items"]) for p_ in b(f"pois {rp['x']} {rp['z']} 700")["pois"]])
    cx, cz = rp["x"] + F[0] * 8, rp["z"] + F[1] * 8
    g = b(f"ground {cx} {cz}")
    b(f"spawnpile 223 1 {cx} {g['y']} {cz} 1 1 1.0 4242"); time.sleep(3)
    b(f"carface {cx} {g['y']} {cz} {yaw}"); time.sleep(1)
    b(f"drivein {cx} {g['y']} {cz}"); time.sleep(2); b(f"drivein {cx} {g['y']} {cz}"); time.sleep(3)
    print("fuelup:", b("fuelup"))
    p0 = pos(b); b("autodrive on 25")
    for i in range(12):
        time.sleep(15)
        p = pos(b); st = b("autodrive")
        pois = b(f"pois {p[0]} {p[2]} 500")["pois"]
        far = b(f"pois {rp['x'] + F[0] * (200 + 250 * i)} {rp['z'] + F[1] * (200 + 250 * i)} 300")["pois"]
        print(f"  {15*(i+1):>3} s: net {st['distance']:>5.0f} m, speed {st['speed']:.0f}, stuck {st['stuckSeconds']:.0f}, "
              f"pois@car {len(pois)} {[(x['name'][:12], int(x['d']), x['items']) for x in pois[:3]]} | ahead {len(far)}")
        if st.get("stuckSeconds", 0) > 6: b("nudge 12")
    b("autodrive off")

#!/usr/bin/env python3
"""Physics benchmark on the fixed seed: a reproducible pile of items next to the spawn point.

  tools/physbench.py [label]                    one pass (fresh seeded world)
  tools/physbench.py --set Path=Value [label]   one pass with a runtime value (repeatable)
  tools/physbench.py --ab Path.To.Bool [label]  off,on,on,off with the frame-phase profiler
  add --cars for a car park (16 full cars) instead of the item pile

Scenario: 300 everyday items (boxes, barrels, cans, tyres, planks, pallets, TV, fridge) spawned via the game's dev-menu
spawn path onto a 10×10 grid (2.2 m apart), 3 layers, starting 4 m above the ground, in a dedicated flat, empty
test area found deterministically ≥150 m from spawn (never inside buildings: items spawned into walls glitch). Measured phases:
  falling  15 s right after spawning (collisions, stacking)
  settled  20 s afterwards (resting pile, sleeping bodies)
"""
import json, sys, time, pathlib, datetime, statistics as st
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import bench, game, seeded
from bench import vec, place_on_ground

ITEM_MIX = "62,70,71,72,60,44,87,81,152,231"   # Box, BARREL, Beer, benzineskanna, OilCan, TireGumi, deszka, Raklap, ujtv, Fridge
COUNT, COLS, SPACING, HEIGHT, SEED = 300, 10, 2.2, 4.0, 4242
AREA = 30.0  # the pile needs COLS*SPACING = 22 m; check a 30 m square


def find_test_area(b, home):
    """Deterministic search for a flat, empty patch: outward from spawn along +x/+z, every sample must hit bare
    Terrain (no building, rock, road) and the height spread must stay under 2 m. Same seed → same spot."""
    for step in range(0, 40):
        for dx, dz in ((1, 0), (0, 1), (-1, 0), (0, -1)):
            cx, cz = home[0] + dx * (150 + step * 50), home[2] + dz * (150 + step * 50)
            place_on_ground(b, cx, cz, home[1] + 300, timeout=30)  # make sure the area is generated
            ys, ok = [], True
            for i in range(7):
                for j in range(7):
                    g = b(f"ground {cx + (i - 3) * AREA / 6} {cz + (j - 3) * AREA / 6}")
                    if not g["hit"] or g["collider"] != "Terrain":
                        ok = False
                        break
                    ys.append(g["y"])
                if not ok:
                    break
            if ok and max(ys) - min(ys) < 2.0:
                return cx, min(ys), cz
    raise RuntimeError("no flat empty test area found")


def configure(items=None, count=None, cols=None, spacing=None, height=None):
    global ITEM_MIX, COUNT, COLS, SPACING, HEIGHT
    if items: ITEM_MIX = items
    if count: COUNT = count
    if cols: COLS = cols
    if spacing: SPACING = spacing
    if height is not None: HEIGHT = height


def scenario_pile(b):
    home = vec(b("get mainscript.s.player.transform.position")["value"])
    cx, gy, cz = find_test_area(b, home)
    # Pile centred on the area; player stands 10 m to the side, on the ground
    ox, oz = cx - COLS * SPACING / 2, cz - COLS * SPACING / 2
    place_on_ground(b, cx - COLS * SPACING / 2 - 10, cz, home[1] + 300, timeout=30)
    time.sleep(3)
    before = b("physics")
    print("   ", b(f"spawnpile {ITEM_MIX} {COUNT} {ox} {gy} {oz} {COLS} {SPACING} {HEIGHT} {SEED}"))
    time.sleep(0.5)  # don't count the spawn frame itself (300 instantiations) as physics
    # Frame-phase profile per window, so falling (collisions) and settled (resting) are separated
    b("reset"); b("loopprof start"); time.sleep(15)
    falling = b("stats"); falling["breakdown"] = b("breakdown"); falling["phases"] = b("loopprof stop 12")
    mid = b("physics")
    b("reset"); b("loopprof start"); time.sleep(20)
    settled = b("stats"); settled["breakdown"] = b("breakdown"); settled["phases"] = b("loopprof stop 12")
    after = b("physics")
    awake = b("awake 25")
    b("clearpile")
    b("teleport {} {} {}".format(home[0], home[1] + 2, home[2]))
    b("set mainscript.s.player.godModeTillGrounded True")
    return {"area": [round(cx, 1), round(gy, 1), round(cz, 1)], "falling": falling, "settled": settled, "physicsBefore": before, "physicsAfterFall": mid, "physicsSettled": after, "awakeSettled": awake}


def one_pass(label, sets, loopprof=False, profile=False):
    seeded.restart_same_seed()
    return bench.run(label, sets=sets, scenarios=[("pile", scenario_pile)], loopprof=loopprof, profile=profile)


def show(r):
    p = r["scenarios"]["pile"]
    print(f"  test area {p.get('area')}")
    for ph in ("falling", "settled"):
        s = p[ph]
        print(f"  {ph:8} fps {s['fps']:6} p50 {s['p50ms']:6} p99 {s['p99ms']:6} max {s['maxms']:6} >50 {s['hitches50ms']}")
        for q in s.get("phases", {}).get("phases", [])[:4]:
            print(f"      {q['p'][:44]:44} avg {q['avgMsAll']:>7} slow {q['avgMsSlow']:>7} max {q['maxMs']:>7}")
    print(f"  awake bodies: before {p['physicsBefore']['awake']}, after fall {p['physicsAfterFall']['awake']}, settled {p['physicsSettled']['awake']}"
          f" (total {p['physicsSettled']['rigidbodies']})")
    for g in p.get("awakeSettled", {}).get("groups", [])[:12]:
        print(f"    awake {g['n']:4} × {g['root'][:40]:40} v avg {g['vAvg']:8} max {g['vMax']:8}  w avg {g['wAvg']:8}  e {g['eAvg']}")


def main():
    args = sys.argv[1:]
    sets = []
    ab = None
    prof = False
    rest = []
    it = iter(args)
    for a in it:
        if a == "--set": sets.append(tuple(next(it).split("=", 1)))
        elif a == "--ab": ab = next(it)
        elif a == "--profile": prof = True
        elif a == "--cars":
            # 16 full cars (Car01/02/03/04 Full), 4×4 grid, 9 m apart, lowered from 1 m: a parked car park
            configure(items="2,16,30,123", count=16, cols=4, spacing=9.0, height=1.0)
        elif a == "--profile": prof = True
        elif a == "--cars":
            # 16 full cars (Car01/02/03/04 Full), 4×4 grid, 9 m apart, lowered from 1 m: a parked car park
            configure(items="2,16,30,123", count=16, cols=4, spacing=9.0, height=1.0)
        else: rest.append(a)
    label = rest[0] if rest else "physbench"
    with game.ProtectedSession():
        if not game.bridge_up():
            game.launch()
        if ab is None:
            show(one_pass(label, sets, profile=prof))
            return
        res = {"False": [], "True": []}
        for val in ("False", "True", "True", "False"):
            r = one_pass(f"{label}-{val}", sets + [(ab, val)])
            show(r)
            res[val].append(r)
        print(f"\n{ab}: pile benchmark, 2 passes each")
        for ph in ("falling", "settled"):
            for k in ("fps", "p99ms", "maxms"):
                a = [r["scenarios"]["pile"][ph][k] for r in res["False"]]
                b = [r["scenarios"]["pile"][ph][k] for r in res["True"]]
                print(f"  {ph}/{k:6} off {a}  on {b}   mean {st.mean(a):.2f} → {st.mean(b):.2f}")


if __name__ == "__main__":
    main()

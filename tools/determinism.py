#!/usr/bin/env python3
"""World determinism check: generate the same target area via different routes (fresh seeded world each time) and
compare the world fingerprint (`worldhash`) per category. Different hashes = timing-dependent world generation, which
would make multiplayer clients see different worlds.

  tools/determinism.py [label] [--target-km 20] [--radius 450] [--settle 30]
"""
import sys, time, json, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge, vec, place_on_ground

def vz(b):
    return vec(b("get mainscript.s.visszarakva")["value"].strip("() "))

def glob(b):
    p = vec(b("get mainscript.s.player.transform.position")["value"]); v = vz(b)
    return [p[0] - v[0], p[1] - v[1], p[2] - v[2]]

def go_global(b, gx, gz, timeout=40):
    """Teleport to a GLOBAL position and wait until standing on generated ground there. Re-aims after every
    floating-origin shift (the game moves the world back to the origin every ~4.5 km)."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        v = vz(b)
        g = b(f"ground {gx + v[0]} {gz + v[2]}")
        y = g["y"] + 1.5 if g.get("hit") and g.get("collider") in ("Terrain", "RoadCollider") else 400
        b(f"teleport {gx + v[0]} {y} {gz + v[2]}")
        b("set mainscript.s.player.godModeTillGrounded True")
        time.sleep(0.6)
        p = glob(b)
        if abs(p[0] - gx) < 30 and abs(p[2] - gz) < 30 and y != 400:
            return p
    raise RuntimeError(f"could not reach global {gx},{gz}")

TARGET = None
DETAIL = False
ROUTES = ("direct", "north", "south", "direct")

def step(b, gx, gz):
    """Intermediate route point: only sets the generation order, so a spot we can't stand on (water, building) is fine."""
    try: go_global(b, gx, gz, timeout=20)
    except RuntimeError: print(f"    (step {gx:.0f},{gz:.0f} not reachable, continuing)", flush=True)

def run(route, target_km, radius, settle):
    seeded.restart_same_seed()
    b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True"); b("set mainscript.s.player.godModeTillGrounded True"); b("set kaposztaleves.s.settings.god True")
    home = glob(b)
    tx, tz = (home[0] + target_km * 1000, home[2]) if TARGET is None else TARGET
    sx = tx + 80  # stand beside the target (the target itself may be inside a building)
    if route == "direct":
        go_global(b, sx, tz)
    elif route == "north":      # arrive from 3 km north in 150 m steps
        go_global(b, sx, tz + 3000); time.sleep(8)
        for i in range(20):
            step(b, sx, tz + 3000 - (i + 1) * 150); time.sleep(0.4)
    elif route == "south":      # from 3 km south, faster steps
        go_global(b, sx, tz - 3000); time.sleep(8)
        for i in range(10):
            step(b, sx, tz - 3000 + (i + 1) * 300); time.sleep(0.2)
    b("set mainscript.s.player.godModeTillGrounded True")
    time.sleep(settle)
    h = b(f"worldhash {tx} {tz} {radius}" + (" detail" if DETAIL else ""))
    st = b("stale"); print(f"    stale caches: {st}", flush=True)
    try: print("    sync:", b("get TLDRevamp.Fixes.SceneryTileSync.Stats")["value"], b("get TLDRevamp.Fixes.TerrainSync.Stats")["value"], flush=True)
    except RuntimeError: pass
    b("set kaposztaleves.s.settings.god False")
    return h

def decode(row):
    """Hex double/float bits in a worldhash row -> readable numbers."""
    import struct
    out = []
    for field in row.split("|"):
        vals = []
        for v in field.split(","):
            try:
                n = int(v, 16)
                vals.append(f"{struct.unpack('<d', struct.pack('<q', n - (1 << 64) if n >= 1 << 63 else n))[0]:.2f}" if len(v) > 8
                            else f"{struct.unpack('<f', struct.pack('<i', n - (1 << 32) if n >= 1 << 31 else n))[0]:.2f}")
            except (ValueError, struct.error): vals.append(v)
        out.append(",".join(vals))
    return " | ".join(out)

def main():
    label, target_km, radius, settle = "determinism", 20.0, 450.0, 30
    it = iter(sys.argv[1:])
    for a in it:
        if a == "--target-km": target_km = float(next(it))
        elif a == "--radius": radius = float(next(it))
        elif a == "--settle": settle = int(next(it))
        elif a == "--detail":
            global DETAIL
            DETAIL = True
        elif a == "--routes":
            global ROUTES
            ROUTES = tuple(next(it).split(","))
        elif a == "--target":
            global TARGET
            TARGET = tuple(float(v) for v in next(it).split(","))
        else: label = a
    res = {}
    with game.ProtectedSession():
        if not game.bridge_up(): game.launch()
        for route in ROUTES:
            key = route if route not in res else route + "2"
            res[key] = run(route, target_km, radius, settle)
            print(f"  {key:8} " + "  ".join(f"{k.split(':')[-1][:10]}={v['n']}/{v['h'][:6]}" for k, v in res[key].items()), flush=True)
    first = next(iter(res))
    cats = list(res[first].keys())
    print("\nper category (same hash in every run = deterministic):")
    for c in cats:
        hs = {k: res[k][c]["h"] for k in res}; ns = {k: res[k][c]["n"] for k in res}
        same = len(set(hs.values())) == 1
        print(f"  {'OK  ' if same else 'DIFF'} {c:24} counts {ns}")
        if not same and DETAIL:
            base = set(res[first][c]["rows"])
            for k in res:
                if k == first: continue
                rs = set(res[k][c]["rows"])
                for r in sorted(base - rs): print(f"      only in {first:7}: {decode(r)}")
                for r in sorted(rs - base): print(f"      only in {k:7}: {decode(r)}")
    path = pathlib.Path(__file__).resolve().parent.parent / "bench" / (time.strftime("%Y%m%d-%H%M%S") + f"-{label}.json")
    path.write_text(json.dumps(res, indent=1))

if __name__ == "__main__":
    main()

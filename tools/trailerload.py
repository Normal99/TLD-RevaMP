#!/usr/bin/env python3
"""Does a hitched trailer jump or come off when a save loads? (Long Haul Overhaul's load stabiliser is for that bug.)
Single player on this machine, the player's saves protected (ProtectedSession restores the autosave slots): a car01
and the game's trailer hitched to it on a road, the game's own AutoSave, then that save loaded LOADS times; after each
load, where the car and the trailer are against where they were saved, and whether the trailer is still hitched.
  tools/trailerload.py [--no-build] [--loads 3] [--item 53]"""
import sys, time, math, pathlib, re
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
from roadkit import clear_road, CAR01

def arg(name, default): return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default
LOADS = int(arg("--loads", "3")); ITEM = int(arg("--item", "53"))
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def rows(b, name):
    out = []
    for r in b(f"whereis {name}").get("items", []):
        v = [float(t) for t in re.findall(r"-?\d+(?:\.\d+)?(?:[eE]-?\d+)?", r[2])]
        if len(v) >= 3: out.append((v[:3], r[3]))
    return out

def state(b, near):
    """the trailer nearest `near` (global x, z) and the car01 nearest it: positions, hitched"""
    tr = [r for r in rows(b, "Utanfuto")]
    if not tr: return None
    t = min(tr, key=lambda r: math.dist((r[0][0], r[0][2]), near))
    cars = rows(b, "Car01")
    c = min(cars, key=lambda r: math.dist(r[0], t[0])) if cars else None
    return {"trailer": t[0], "hitched": "attached True" in t[1], "car": c[0] if c else None, "info": t[1]}

with game.ProtectedSession():
    if "--no-build" not in sys.argv: game.build()
    else: game.local_mods()
    game.quit_game(); game.launch(); game.newgame()
    b = Bridge(); time.sleep(3)
    b("mp host 27070"); time.sleep(3)
    rp = b("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
    r0 = clear_road(b, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
    F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw))); R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
    cx, cz = r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8
    before = {e["net"] for e in b("mp entities")["entities"]}
    print(" car:", b(f"spawncarg {CAR01} 0 {cx} {cz} {yaw}"), flush=True); time.sleep(3)
    print(" trailer:", b(f"spawncarg {ITEM} 0 {cx - F[0] * 7} {cz - F[1] * 7} {yaw}"), flush=True); time.sleep(4)
    new = [e for e in b("mp entities")["entities"] if e["net"] not in before and e["part"] < 0]
    car = next((e["net"] for e in new if e["car"]), None); trailer = next((e["net"] for e in new if not e["car"]), None)
    print(" nets: car", car, "trailer", trailer, flush=True)
    if not car or not trailer: sys.exit("SPAWN/SHARE FAILED")
    print(" grip:", b(f"mount grip {trailer}"), flush=True)
    print(" hitch:", b(f"mount hitch {car} {trailer}"), flush=True)
    b(f"tpground {cx + R[0] * 6} {cz + R[1] * 6}"); time.sleep(8)
    b("mp stop"); time.sleep(2)
    s0 = state(b, (cx - F[0] * 7, cz - F[1] * 7))
    print(" saved as:", s0, flush=True)
    if not s0 or not s0["hitched"]: sys.exit("NOT HITCHED BEFORE SAVING")
    gap0 = math.dist(s0["trailer"], s0["car"])
    b("set TLDRevamp.TestSafety.BlockAutoSave False")
    print(" autosave:", b("call savedatascript.s.saveScreen.AutoSave"), flush=True); time.sleep(4)
    b("set TLDRevamp.TestSafety.BlockAutoSave True")
    newest = max(game.SAVES.glob("TLDsaveData_autosave*.tlds"), key=lambda p: p.stat().st_mtime)
    slot = newest.name[len("TLDsaveData_"):-len(".tlds")]
    print(" saved to", slot, flush=True)
    for i in range(LOADS):
        b = game.loadsave(slot)
        s = state(b, (s0["trailer"][0], s0["trailer"][2]))
        if not s: verdict(f"load {i + 1}: trailer there", False, "no trailer"); continue
        moved = math.dist(s["trailer"], s0["trailer"]); cmoved = math.dist(s["car"], s0["car"]) if s["car"] else -1
        gap = math.dist(s["trailer"], s["car"]) if s["car"] else -1
        print(f" load {i + 1}: trailer moved {moved:.2f} m, car {cmoved:.2f} m, gap {gap0:.2f} -> {gap:.2f} m, hitched {s['hitched']}", flush=True)
        verdict(f"load {i + 1}: trailer still hitched", s["hitched"], s["info"])
        verdict(f"load {i + 1}: trailer where it was saved (< 0.3 m)", moved < 0.3, f"{moved:.2f} m (car {cmoved:.2f} m)")
    if "--keep" not in sys.argv: game.quit_game()

print(f"{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed")

#!/usr/bin/env python3
"""A trailer's drawbar onto a car's tow ball (Fixes/TrailerGrip.cs, v0.67.0). The host alone: a Car01 and the game's
trailer (item 53) behind it, shared; `mount grip` shows what the drawbar is made of, `mount hitch` attaches it as a
player carrying it to the ball would. Then again with the fix off, for a second trailer.
  A  fix on: the drawbar has the trailer's save record, the trailer's record has the drawbar as P
  B  fix on: the drawbar hitches to the car
  C  fix off (the bug): the drawbar has no record, the trailer no P, and hitching throws
  tools/trailerhitch.py [--no-build]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g, clear_road, car_ready

TRAILER = 53
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def roots(h): return {e["net"] for e in h("mp entities")["entities"] if e["part"] < 0}

def trailer_behind(h, x, z, F, R, yaw):
    before = roots(h)
    print("  trailer:", h(f"spawncarg {TRAILER} 0 {x - F[0] * 7} {z - F[1] * 7} {yaw}"), flush=True)
    until(lambda: roots(h) - before, 15)
    new = roots(h) - before
    time.sleep(2)
    return next(iter(new), None)

def hitch(h, car, tr):
    try: return h(f"mount hitch {car} {tr}")
    except RuntimeError as e: return {"threw": str(e)[:400]}

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    h("set TLDRevamp.TestSafety.BlockAutoSave True")
    try:
        h("mp host 27070"); time.sleep(2)
        h("set kaposztaleves.s.settings.god True")
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
        R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw))); F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        x, z = r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8
        if not car_ready(h, x, z, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        me = h("mp players")["me"]
        car = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
        h("driveout"); time.sleep(2)

        print("  fix on:", g(h, "TLDRevamp.Fixes.TrailerGrip.Enabled"), flush=True)
        tr = trailer_behind(h, x, z, F, R, yaw)
        if not tr: sys.exit("TRAILER NOT SHARED")
        gr = h(f"mount grip {tr}"); print("  grip:", gr, flush=True)
        verdict("A FIX ON: THE DRAWBAR HAS THE TRAILER'S RECORD, THE TRAILER ITS P",
                gr["P"] is True and "tosave null" not in gr["grip"], f"P {gr['P']}; {gr['grip'][-60:]}")
        r = hitch(h, car, tr); print("  hitch:", r, flush=True)
        verdict("B FIX ON: THE DRAWBAR HITCHES TO THE CAR", "hitched" in r, str(r)[:120])

        h("set TLDRevamp.Fixes.TrailerGrip.Enabled False")
        h(f"tpground {x + R[0] * 12} {z + R[1] * 12}"); time.sleep(2)
        tr2 = trailer_behind(h, x + R[0] * 12, z + R[1] * 12, F, R, yaw)
        if not tr2: sys.exit("SECOND TRAILER NOT SHARED")
        gr2 = h(f"mount grip {tr2}"); print("  grip (fix off):", gr2, flush=True)
        car2 = None
        h(f"tpground {x + R[0] * 12 + F[0] * 3} {z + R[1] * 12 + F[1] * 3}")
        if car_ready(h, x + R[0] * 12, z + R[1] * 12, yaw, "host"):
            me = h("mp players")["me"]
            car2 = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"] and e["net"] != car), key=lambda e: math.dist(e["pos"], me))["net"]
            h("driveout"); time.sleep(2)
        r2 = hitch(h, car2, tr2) if car2 else {"threw": "no second car"}
        print("  hitch (fix off):", r2, flush=True)
        verdict("C FIX OFF: NO RECORD, NO P, HITCHING THROWS",
                gr2["P"] is False and "tosave null" in gr2["grip"] and "hitched" not in r2, f"P {gr2['P']}; {str(r2)[:120]}")
        print("  wired:", g(h, "TLDRevamp.Fixes.TrailerGrip.Wired"), flush=True)
        er = h("errors"); print("  errors:", er["errors"], er["exceptions"], (er.get("kinds") or [])[:3], flush=True)
    finally:
        try: h("set TLDRevamp.Fixes.TrailerGrip.Enabled True")
        except Exception: pass
        h("mp stop")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"TRAILERHITCH: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")

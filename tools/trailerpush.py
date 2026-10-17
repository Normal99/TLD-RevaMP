#!/usr/bin/env python3
"""A car nudging a parked, unhitched trailer (TLDRevamp.Fixes.TrailerCreep holds a slow trailer as a brake would):
single player, the game's trailer on a road, a car01 behind it driven into it by the autopilot at a walking pace,
with the fix off and then on. Prints how far the trailer was pushed and how fast the car got.
  tools/trailerpush.py [--no-build] [--ms 1.0] [--secs 20]"""
import sys, time, math, pathlib, re
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
from roadkit import clear_road, car_ready, g

def arg(name, default): return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default
MS = float(arg("--ms", "1.0")); SECS = float(arg("--secs", "20"))
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def trailer_at(b, near):
    pts = []
    for r in b("whereis Utanfuto").get("items", []):
        v = [float(t) for t in re.findall(r"-?\d+(?:\.\d+)?(?:[eE]-?\d+)?", r[2])]
        if len(v) >= 3: pts.append(v[:3])
    return min(pts, key=lambda v: math.dist((v[0], v[2]), near)) if pts else None

with game.ProtectedSession():
    if "--no-build" not in sys.argv: game.build()
    else: game.local_mods()
    results = {}
    for on in (False, True):
        game.quit_game(); game.launch(); game.newgame()
        b = Bridge(); time.sleep(3)
        b("set TLDRevamp.DriveLab.Recover False")
        b(f"set TLDRevamp.Fixes.TrailerCreep.Enabled {on}")
        rp = b("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(b, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw))); R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        tx, tz = r0["gx"] + R[0] * 1.8 + F[0] * 8, r0["gz"] + R[1] * 1.8 + F[1] * 8
        # the trailer faces away from the car: the car meets its back
        print(" trailer:", b(f"spawncarg 53 0 {tx} {tz} {yaw}"), flush=True); time.sleep(5)
        if not car_ready(b, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "player"): sys.exit("NOT IN THE CAR")
        p0 = trailer_at(b, (tx, tz)); held0 = b("get TLDRevamp.Fixes.TrailerCreep.Held")["value"]
        b("handbrake off"); b(f"autodrive on {MS}")
        top = 0.0; t0 = time.time()
        while time.time() - t0 < SECS:
            time.sleep(1)
            try: top = max(top, float(g(b, "mainscript.s.player.Car.speed") or 0))
            except Exception: pass
        b("autodrive off"); time.sleep(2)
        p1 = trailer_at(b, (tx, tz)); held = b("get TLDRevamp.Fixes.TrailerCreep.Held")["value"] - held0
        pushed = math.dist((p0[0], p0[2]), (p1[0], p1[2])) if p0 and p1 else -1
        results[on] = pushed
        print(f"  fix {'on' if on else 'off'}: trailer pushed {pushed:.2f} m in {SECS:.0f} s at {MS} m/s asked; held {held} steps", flush=True)
    verdict("the car pushes it without the fix", results[False] > 3, f"{results[False]:.2f} m")
    verdict("the car pushes it as far with the fix (within 25 %)", results[True] > 0.75 * results[False], f"{results[True]:.2f} m vs {results[False]:.2f} m")
    if "--keep" not in sys.argv: game.quit_game()

print(f"{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed")

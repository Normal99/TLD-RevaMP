#!/usr/bin/env python3
"""Trailer brakes and the driving assists (TLDRevamp.Fixes.TrailerBrakes / DrivingAssists), single player on this
machine: a car01 with the game's trailer hitched, driven by the autopilot along the road.
  1. stopping from 15 m/s with the car's brake, trailer brakes off / on / off / on (distance)
  2. coasting from 12 m/s with the trailers-only brake held, off / on (distance to 4 m/s)
  3. the assists on (sway, roll, traction) for a stretch at speed: they act, nothing breaks
  tools/trailerbrakes.py [--no-build]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
from roadkit import clear_road, car_ready, g

def arg(name, default): return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default
ITEM = int(arg("--item", "53")); TOW = int(arg("--tow", "2")); BACK = float(arg("--back", "7"))   # --item 194 --tow 174 --back 10: the IFA's trailer behind the IFA
import roadkit
roadkit.CAR01 = TOW   # 53 the small trailer, 194 the bus trailer, 9201 the car trailer (TLD_TEST_MODS=CarTrailer)
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def vec(v): return [float(x) for x in str(v).strip("()").split(",")]
def speed(b):
    # the body's speed (carscript.speed is the wheels': locked wheels read 0 while the car still slides)
    try: v = vec(b("get mainscript.s.player.Car.RB.velocity")["value"]); return math.hypot(v[0], v[2])
    except Exception: return 0.0
def pos(b): return vec(b("get mainscript.s.player.Car.transform.position")["value"])

def reach(b, ms, timeout=60):
    b(f"autodrive on {ms}"); t0 = time.time()
    while time.time() - t0 < timeout:
        if speed(b) >= ms * 0.95: return True
        time.sleep(0.3)
    return False

def run_down(b, until_ms, how, timeout=60):
    """distance driven until the speed is under until_ms; how: 'brake' (autopilot asks 0) or 'coast' (autopilot off)"""
    p0 = pos(b); t0 = time.time()
    if how == "brake": b("autodrive on 0")
    else: b("autodrive off")
    while time.time() - t0 < timeout and speed(b) > until_ms: time.sleep(0.1)
    d = math.dist((p0[0], p0[2]), (pos(b)[0], pos(b)[2])); b("autodrive off")
    return d, time.time() - t0

with game.ProtectedSession():
    if "--no-build" not in sys.argv: game.build()
    else: game.local_mods()
    game.quit_game(); game.launch(); game.newgame()
    b = Bridge(); time.sleep(3)
    b("set TLDRevamp.DriveLab.Recover False"); b("set settingsscript.s.S.BAutoShift True")
    b("mp host 27070"); time.sleep(3)
    rp = b("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
    r0 = clear_road(b, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
    F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw))); R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
    cx, cz = r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8
    before = {e["net"] for e in b("mp entities")["entities"]}
    if not car_ready(b, cx, cz, yaw, "player"): sys.exit("NOT IN THE CAR")
    print(" trailer:", b(f"spawncarg {ITEM} 0 {cx - F[0] * BACK} {cz - F[1] * BACK} {yaw}"), flush=True); time.sleep(4)
    new = [e for e in b("mp entities")["entities"] if e["net"] not in before and e["part"] < 0]
    car = next((e["net"] for e in new if e["car"]), None); trailer = next((e["net"] for e in new if not e["car"]), None)
    print(" nets: car", car, "trailer", trailer, flush=True)
    print(" grip:", b(f"mount grip {trailer}"), flush=True)
    print(" hitch:", b(f"mount hitch {car} {trailer}"), flush=True); time.sleep(3)
    b("fuelup")
    print(" car: mass", g(b, "mainscript.s.player.Car.RB.mass"), "brakePower", g(b, "mainscript.s.player.Car.brakePower"), "wheels", g(b, "mainscript.s.player.Car.allWH.Count"),
          "| trailer:", b(f"mount grip {trailer}").get("root", "")[:0], flush=True)
    stops = {False: [], True: []}
    for on in (False, True, False, True, False, True):
        b(f"set TLDRevamp.Fixes.TrailerBrakes.Enabled {on}")
        if not reach(b, 15): print("  didn't reach 15 m/s", flush=True); continue
        d, t = run_down(b, 4, "brake")
        stops[on].append(d)
        print(f"  stop from 15 to 4 m/s, trailer brakes {'on' if on else 'off'}: {d:.1f} m in {t:.1f} s", flush=True)
    print(" trailer brakes:", b("get TLDRevamp.Fixes.TrailerBrakes.Last")["value"], flush=True)
    b("set TLDRevamp.Fixes.TrailerBrakes.Enabled True")
    coast = {}
    for alone in (False, True, False, True):
        if not reach(b, 12): continue
        b(f"set TLDRevamp.Fixes.TrailerBrakes.TestAlone {alone}")   # held only once at speed
        v0 = speed(b); b("autodrive off"); time.sleep(3); v1 = speed(b)
        b("set TLDRevamp.Fixes.TrailerBrakes.TestAlone False")
        coast.setdefault(alone, []).append(v0 - v1)
        print(f"  coasting 3 s from {v0:.1f} m/s, trailer-only brake {'held' if alone else 'not held'}: lost {v0 - v1:.1f} m/s", flush=True)
    b("set TLDRevamp.Fixes.TrailerBrakes.TestAlone False")
    for k in ("Sway", "Roll", "Traction"): b(f"set TLDRevamp.Fixes.DrivingAssists.{k} True")
    reach(b, 18); time.sleep(20); b("autodrive off")
    ticks = {k: b(f"get TLDRevamp.Fixes.DrivingAssists.{k}")["value"] for k in ("SwayTicks", "RollTicks", "TractionCuts")}
    print("  assists:", ticks, "errors:", b("errors").get("count"), flush=True)
    for k in ("Sway", "Roll", "Traction"): b(f"set TLDRevamp.Fixes.DrivingAssists.{k} False")
    off, on = stops[False], stops[True]
    if off and on:
        import statistics
        verdict("trailer brakes shorten the stop (median)", statistics.median(on) < statistics.median(off), f"off {[round(x, 1) for x in off]}, on {[round(x, 1) for x in on]} m")
    else: verdict("trailer brakes shorten the stop", False, f"runs: off {off}, on {on}")
    import statistics
    verdict("the trailers-only brake slows the car (median speed lost in 3 s)", True in coast and False in coast and statistics.median(coast[True]) > 1.5 * statistics.median(coast[False]), f"{coast}")
    verdict("the sway and roll assists act", ticks["SwayTicks"] > 0 and ticks["RollTicks"] > 0, str(ticks))
    print(" hitched still:", b(f"mount grip {trailer}").get("grip", ""), flush=True)
    b("mp stop")
    if "--keep" not in sys.argv: game.quit_game()

print(f"{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed")

#!/usr/bin/env python3
"""The Car Trailer example mod (src/Mods/CarTrailer) in a session, two machines over the relay: a Car01 tows the car
trailer with another Car01 on its deck. As transporter.py, with the trailer as the carrier.
  A. the deck holds the car;  B. the laptop sees it there;  C. the host tows (laptop rides along): the cargo stays on the
  deck and the trailer behind the car, on both machines;  D. the same with the laptop driving (host rides along)
  TLD_TEST_MODS=CarTrailer tools/cartrailer.py [--no-build] [--relay iad|off] [--kmh 40] [--old] [--assists]"""
import os
os.environ.setdefault("TLD_TEST_MODS", "CarTrailer")
import sys, time, math, pathlib, statistics
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, g, clear_road, car_ready

def arg(name, default): return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default
RELAY = arg("--relay", "iad"); KMH = float(arg("--kmh", "40")); TRAILER = 9201; CARGO = 2
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def new_car(side, before):
    until(lambda: any(e["net"] not in before and e["car"] and e["part"] < 0 for e in side("mp entities")["entities"]), 15)
    return next((e["net"] for e in side("mp entities")["entities"] if e["net"] not in before and e["car"] and e["part"] < 0), None)

def spawn_shared(h, item, gx, gz, yaw):
    before = {e["net"] for e in h("mp entities")["entities"]}
    print(f"  spawn {item}:", h(f"spawncarg {item} 0 {gx} {gz} {yaw}"), flush=True); time.sleep(3)
    h(f"tpground {gx + math.cos(math.radians(yaw)) * 4} {gz - math.sin(math.radians(yaw)) * 4}"); time.sleep(2)
    print("  share:", h("mp share"), flush=True)
    return new_car(h, before)

def sit_driver(side, carrier, who):
    """mp sitdriver steps next to the seat first when far, then sits: call until seated"""
    for _ in range(5):
        print(f"  {who} sits:", side(f"mp sitdriver {carrier}"), flush=True); time.sleep(2)
        me = side("mp players")["me"]; ent = next((e for e in side("mp entities")["entities"] if e["net"] == carrier), None)
        print(f"    {who} at {[round(v, 1) for v in me]}, carrier at {[round(v, 1) for v in ent['pos']] if ent else None}, proxy {ent and ent.get('proxy')}", flush=True)
        if g(side, "mainscript.s.player.Bsitting") is True: break
    try: side("fuelup")
    except RuntimeError as e: print(f"  {who}: {e}", flush=True)
    return g(side, "mainscript.s.player.Bsitting") is True

def ride_along(side, carrier, who):
    for _ in range(4):
        print(f"  {who} rides along:", side(f"mp sitpassenger {carrier}"), flush=True); time.sleep(2)
        if g(side, "mainscript.s.player.Bsitting") is True: return True
    return False

def rel(side, carrier, cargo):
    try: return side(f"mp relpos {carrier} {cargo}")["items"].get(str(cargo))
    except Exception: return None

SERIES = []
def ride(driver, watcher, carrier, cargo, seconds, label):
    """drive the carrier with the autopilot; sample the cargo in the carrier's frame on both machines"""
    p0 = rel(driver, carrier, cargo)
    driver("handbrake off")   # the last driver left it on
    driver(f"autodrive on {KMH / 3.6:.1f}")
    t0 = time.monotonic(); diffs = []; drift = []; vmax = 0.0; shot = False; hitch = []
    while time.monotonic() - t0 < seconds:
        a = rel(driver, carrier, cargo); b = rel(watcher, carrier, cargo)
        vmax = max(vmax, abs(float(g(driver, "mainscript.s.player.Car.speed") or 0)))
        if a and p0: drift.append(math.dist(a, p0))
        t = rel(driver, tow, carrier)
        if t: hitch.append(t)
        if a and b: diffs.append(math.dist(a, b))
        # per moment: which copy is off on the watcher, the carrier against the tow car or the cargo against the carrier
        wt = rel(watcher, tow, carrier)
        SERIES.append({"label": label, "t": round(time.monotonic() - t0, 2), "cargo_driver": a, "cargo_watcher": b, "trailer_driver": t, "trailer_watcher": wt,
                       "diff": round(math.dist(a, b), 3) if a and b else None})
        if a and b and math.dist(a, b) > 0.5:
            print(f"    {label} t {time.monotonic() - t0:.1f}: watcher cargo {b} vs driver {a}; trailer behind tow: watcher {wt} driver {t}", flush=True)
        if not shot and time.monotonic() - t0 > seconds / 2:
            print(f"    {label} autodrive:", str(driver("autodrive"))[:300], "starter:", g(driver, "mainscript.s.player.Car.Engine.running"), flush=True)
            watcher(f"mp lookat {cargo}"); time.sleep(0.3); watcher(f"shot cartrailer_{label}_watcher"); shot = True
        time.sleep(0.3)
    driver("autodrive off"); driver("handbrake on")
    until(lambda: abs(float(g(driver, "mainscript.s.player.Car.speed") or 0)) < 1, 20)   # stopped before anyone gets out
    time.sleep(1)
    print(f"  {label}: top {vmax:.0f} km/h; cargo moved on the bed (driver) max {max(drift) if drift else -1:.2f} m; "
          f"watcher vs driver median {statistics.median(diffs) if diffs else -1:.3f} max {max(diffs) if diffs else -1:.3f} m ({len(diffs)} samples)", flush=True)
    if hitch: print(f"  {label}: trailer behind the car (car's frame) z from {min(p[2] for p in hitch):.2f} to {max(p[2] for p in hitch):.2f} m", flush=True)
    return vmax, drift, diffs

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
    if "--assists" in sys.argv:   # the optional driving assists on for both drivers (Fixes.DrivingAssists)
        for s_ in (h, L):
            for k in ("Sway", "Roll", "Traction"): s_(f"set TLDRevamp.Fixes.DrivingAssists.{k} True")
    if "--old" in sys.argv:
        for s_ in (h, L): s_("set TLDRevamp.Net.Entities.CarCargoClaims False")   # A/B: a carried car stays with its last owner
    print("  carried cars follow the carrier:", g(h, "TLDRevamp.Net.Entities.CarCargoClaims"), flush=True)
    h("set settingsscript.s.S.BAutoShift True"); L("set settingsscript.s.S.BAutoShift True")
    for s_ in (h, L): s_("set TLDRevamp.DriveLab.Recover False")   # backing up to get unstuck jackknifes a trailer (runs 13-14)
    print("  physics lock (host):", g(h, "settingsscript.s.S.IPhysicsLock"), flush=True)
    rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
    r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
    F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
    if not car_ready(h, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
    me = h("mp players")["me"]
    tow = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
    before = {e["net"] for e in h("mp entities")["entities"]}
    print("  trailer:", h(f"spawncarg {TRAILER} 0 {r0['gx'] + R[0] * 1.8 - F[0] * 7} {r0['gz'] + R[1] * 1.8 - F[1] * 7} {yaw}"), flush=True)
    until(lambda: any(e["net"] not in before and e["part"] < 0 for e in h("mp entities")["entities"]), 15)
    carrier = next((e["net"] for e in h("mp entities")["entities"] if e["net"] not in before and e["part"] < 0), None)
    if not carrier: sys.exit("TRAILER NOT SHARED")
    time.sleep(2)
    print("  grip:", h(f"mount grip {carrier}"), flush=True)
    if "--probe" in sys.argv:
        before = {e["net"] for e in h("mp entities")["entities"]}
        print("  game trailer:", h(f"spawncarg 53 0 {r0['gx'] + R[0] * 6} {r0['gz'] + R[1] * 6} {yaw}"), flush=True)
        until(lambda: any(e["net"] not in before and e["part"] < 0 for e in h("mp entities")["entities"]), 15)
        van = next((e["net"] for e in h("mp entities")["entities"] if e["net"] not in before and e["part"] < 0), None)
        print("  game trailer grip:", h(f"mount grip {van}") if van else "not shared", flush=True)
        sys.exit("PROBE DONE")
    try: print("  hitch:", h(f"mount hitch {tow} {carrier}"), flush=True)
    except RuntimeError as e: sys.exit(f"HITCH FAILED: {e}")
    time.sleep(3)
    h("driveout"); time.sleep(2)
    cargo = spawn_shared(h, CARGO, r0["gx"] + R[0] * 9, r0["gz"] + R[1] * 9, yaw)
    print("  tow", tow, "trailer", carrier, "cargo", cargo, flush=True)
    if not cargo: sys.exit("SPAWN/SHARE FAILED")
    areas = h(f"mount areas {carrier}")
    print("  areas:", areas, flush=True)
    big = [a for a in areas["areas"] if a["name"] == "CarMount"]
    if not big: sys.exit("NO AREA BIG ENOUGH FOR A CAR")
    bed = max(big, key=lambda a: a["size"][0] * a["size"][2])
    print("  bed:", bed, flush=True)
    print("  place:", h(f"mount placecar {carrier} {cargo} {bed['i']} 0.6"), flush=True)
    time.sleep(6)
    held = until(lambda: cargo in h(f"mount areas {carrier}")["areas"][bed["i"]]["cars"], 10)
    verdict("A. THE BED HOLDS THE CAR (host)", held, str(h(f"mount areas {carrier}")["areas"][bed["i"]]))
    # the carried car's wheels lock as on the Brakas: the car area locks them while its handbrake is on
    print("  cargo handbrake on:", h(f"mount carlock {cargo} 1"), flush=True)
    locked = until(lambda: h(f"mount carlock {cargo}")["whlocked"], 6)
    verdict("A2. ITS WHEELS LOCK (handbrake on)", locked, str(h(f"mount carlock {cargo}")))
    until(lambda: rel(L, carrier, cargo) is not None, 30)
    a, b = rel(h, carrier, cargo), rel(L, carrier, cargo)
    print("  cargo in the carrier's frame: host", a, "laptop", b, flush=True)
    verdict("B. THE LAPTOP SEES IT ON THE BED", a is not None and b is not None and math.dist(a, b) < 0.3, f"{a} vs {b}")
    ce = next(e for e in h("mp entities")["entities"] if e["net"] == carrier)
    h(f"tpground {ce['pos'][0] + R[0] * 7 - F[0] * 3} {ce['pos'][2] + R[1] * 7 - F[1] * 3}"); time.sleep(2)
    h(f"mp lookat {carrier}"); time.sleep(0.5); h("shot cartrailer_look")   # how it looks, for the thread
    L(f"tpground {r0['gx'] - R[0] * 6} {r0['gz'] - R[1] * 6}"); time.sleep(2)
    sit_driver(h, tow, "host")
    print("  fuel (host):", h("fuelup"), flush=True)
    ride_along(L, tow, "laptop")
    v, drift, diffs = ride(h, L, carrier, cargo, 30, "host_drives")
    verdict("C1. HOST DRIVES", v > KMH * 0.6, f"top {v:.0f} km/h")
    verdict("C2. THE CARGO STAYS ON THE BED (host)", drift and max(drift) < 0.6, f"max {max(drift) if drift else -1:.2f} m")
    verdict("C3. THE LAPTOP SEES IT WHERE THE HOST HAS IT", diffs and statistics.median(diffs) < 0.2 and max(diffs) < 0.8,
            f"median {statistics.median(diffs) if diffs else -1:.3f} max {max(diffs) if diffs else -1:.3f}")
    print("  laptop out:", L("driveout"), flush=True); time.sleep(2)
    print("  host out:", h("driveout"), flush=True); time.sleep(2)
    me = h("mp players")["me"]
    L(f"tpground {me[0] + R[0] * 2} {me[2] + R[1] * 2}"); time.sleep(3)
    sit_driver(L, tow, "laptop")
    print("  fuel (laptop):", L("fuelup"), flush=True)
    ride_along(h, tow, "host")
    v, drift, diffs = ride(L, h, carrier, cargo, 30, "laptop_drives")
    own = {e["net"]: e.get("owner") for e in L("mp entities")["entities"]}
    print(f"  owners on the laptop: tow {own.get(tow)}, trailer {own.get(carrier)}, cargo {own.get(cargo)}", flush=True)
    verdict("D1. LAPTOP DRIVES (it owns the transporter now)", v > KMH * 0.6, f"top {v:.0f} km/h")
    verdict("D2. THE CARGO STAYS ON THE BED (laptop)", drift and max(drift) < 0.6, f"max {max(drift) if drift else -1:.2f} m")
    verdict("D3. THE HOST SEES IT WHERE THE LAPTOP HAS IT", diffs and statistics.median(diffs) < 0.2 and max(diffs) < 0.8,
            f"median {statistics.median(diffs) if diffs else -1:.3f} max {max(diffs) if diffs else -1:.3f}")
    print("  trailer brakes host/laptop:", [x_("get TLDRevamp.Fixes.TrailerBrakes.Braking")["value"] for x_ in (h, L)],
          "assists host/laptop:", [x_("get TLDRevamp.Fixes.DrivingAssists.SwayTicks")["value"] for x_ in (h, L)], flush=True)
    er = [x_("errors") for x_ in (h, L)]
    print("  errors host/laptop:", er[0].get("count"), er[1].get("count"), flush=True)
    an = [x_("mp anomalies") for x_ in (h, L)]
    print("  anomalies:", an, flush=True)
    for s_ in (h, L):
        try: s_("mp stop")
        except Exception: pass
L.close()
n_ok = sum(1 for _, ok in VERDICTS if ok)
import json
if "--series" in sys.argv: pathlib.Path(arg("--series", "")).write_text(json.dumps(SERIES))
print(f"\nSUMMARY cartrailer: {n_ok}/{len(VERDICTS)} PASS")

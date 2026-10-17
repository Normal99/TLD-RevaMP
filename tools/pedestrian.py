#!/usr/bin/env python3
"""A car hits a player on foot (two machines, relay iad by default). The game has no car-hits-player code: the car's
body shoves the player's, and fpscontroller.FallDamage kills on a one-frame speed change > deathVelocityChange — the
same rule as a fall. In MP the walker's machine has the driver's car as a copy, the driver's machine has the walker
as a drawn body without colliders: the hit happens on the walker's machine only (each side simulates its own body).
Per speed (--kmh, default 15 40 70): the host drives (test driver), once at speed the laptop steps into its path
~2.5 s ahead. On the laptop: did the copy reach it (closest distance), was it shoved (displacement), did it die. On
the host: the car's speed before/after (a walker isn't a wall for the driver).
  tools/pedestrian.py [--no-build] [--relay iad|off] [--kmh 15,40,70] [--tries 3]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP
from roadkit import until, g, car_ready

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY = arg("--relay", "iad")
if RELAY == "off": RELAY = None
KMHS = [float(k) for k in arg("--kmh", "15,40,70").split(",")]
TRIES = arg("--tries", 3, int)

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def unit(yaw): return (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))

def carpos(s, net):
    e = next((e for e in s("mp entities")["entities"] if e["net"] == net), None)
    return e["pos"] if e else None

def one_pass(kmh, car):
    """one drive past the walker: closest distance seen, shove, deaths, the driver's speeds"""
    b(f"autodrive on {kmh / 3.6}")
    if not until(lambda: b("autodrive")["speed"] > kmh / 3.6 * 0.85, 40): print("  never reached speed", flush=True); return None
    p1 = b("gpos"); time.sleep(0.5); p2 = b("gpos")
    dx, dz = p2["gx"] - p1["gx"], p2["gz"] - p1["gz"]; d = math.hypot(dx, dz) or 1
    v = d / 0.5; ahead = v * 2.5
    tx, tz = p2["gx"] + dx / d * ahead, p2["gz"] + dz / d * ahead
    v0 = b("autodrive")["speed"]
    L(f"tpground {tx} {tz}"); t_place = time.time()
    me0 = L("gpos"); d0 = g(L, "TLDRevamp.Diagnostics.DeathLog.Deaths")
    closest, vmin_host = 1e9, v0
    while time.time() - t_place < 5:
        cp = carpos(L, car); lp = L("gpos")
        if cp: closest = min(closest, math.hypot(cp[0] - lp["gx"], cp[2] - lp["gz"]))
        vmin_host = min(vmin_host, b("autodrive")["speed"])
        time.sleep(0.15)
    me1 = L("gpos"); d1 = g(L, "TLDRevamp.Diagnostics.DeathLog.Deaths")
    shove = math.hypot(me1["gx"] - me0["gx"], me1["gz"] - me0["gz"])
    b("autodrive off"); b("brakecar")
    print(f"  {kmh:.0f} km/h: car at {v0 * 3.6:.0f} km/h; laptop's copy of the car came within {closest:.2f} m; laptop shoved "
          f"{shove:.2f} m, deaths +{d1 - d0}; host car's slowest during the pass {vmin_host * 3.6:.0f} km/h", flush=True)
    return {"closest": closest, "shove": shove, "died": d1 - d0, "v0": v0, "vmin": vmin_host}

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        b = seeded_both(L)
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("relay only via", RELAY, b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
        b("mp host 27070")
        if RELAY: L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("route:", L("net route"), flush=True)
        print("deathVelocityChange (laptop):", g(L, "mainscript.s.player.deathVelocityChange"), flush=True)
        time.sleep(4)
        rp = b("roadpoint"); yaw = rp["yaw"]; D = unit(yaw); R = (D[1], -D[0])
        cx, cz = rp["gx"], rp["gz"]
        b(f"tpground {cx - R[0] * 10} {cz - R[1] * 10}"); time.sleep(2)
        L(f"tpground {cx + R[0] * 12} {cz + R[1] * 12}"); time.sleep(2)
        if not car_ready(b, cx, cz, yaw, "host (its car)"): sys.exit("host car not ready")
        me = b("mp entities")["me"]; gp = b("gpos")
        cars = [e for e in b("mp entities")["entities"] if e["car"] and e["owner"] == me and not e["dormant"]]
        car = min(cars, key=lambda e: math.hypot(e["pos"][0] - gp["gx"], e["pos"][2] - gp["gz"]))["net"]
        b("handbrake off")
        for kmh in KMHS:
            for k in range(TRIES):
                r = one_pass(kmh, car)
                if r is None: break
                time.sleep(2)
                if g(L, "mainscript.s.player.died") is True: until(lambda: g(L, "mainscript.s.player.died") is not True, 30)
                # a hit is what happened to the walker: the closest distance is sampled every few tenths of a second
                # over the relay — at 10 m/s it misses the contact (run 1: 2.27 m "closest", walker thrown 28 m)
                if r["shove"] > 0.5 or r["died"] > 0:
                    verdict(f"{kmh:.0f} KM/H: THE WALKER WAS HIT", r["shove"] > 0.5 or r["died"] > 0,
                            f"closest {r['closest']:.2f} m, shoved {r['shove']:.2f} m, died {r['died']}")
                    verdict(f"{kmh:.0f} KM/H: THE DRIVER DRIVES ON", r["vmin"] > 0.7 * r["v0"], f"{r['v0'] * 3.6:.0f} → slowest {r['vmin'] * 3.6:.0f} km/h")
                    break
                print(f"  missed (closest seen {r['closest']:.2f} m, not shoved) — again", flush=True)
            else:
                verdict(f"{kmh:.0f} KM/H: A PASS CLOSE ENOUGH", False, f"{TRIES} tries missed the walker")
        L("mp stop"); b("mp stop")
        if RELAY: b("net relay off"); L("net relay off")
finally:
    L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")

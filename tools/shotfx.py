#!/usr/bin/env python3
"""Gunshots in MP (two machines, relay iad by default): does the other player see what a shot did? (Entities.ShotFx)
  1 CAR   : the laptop shoots the host's parked car at 3 parts → the same bullet holes on the owner's car (each laptop
            hole has an owner hole within 3 cm, car-relative)
  2 GROUND: the laptop shoots the road → the same holes at the same global spot on the host
  4 OWN   : (run first) the host shoots its own jerry can full of water (fuel may explode) — the single-machine reference leak rate
  3 TANK  : the laptop shoots another of the host's cans → a hole on both machines; the can leaks on its owner, the
            laptop's copy follows, and at the reference rate (a copy's hole is for show: no double draining)
Plus the gunshot itself on the other machine (mp shotfx: applied, the gun resolved there).
  tools/shotfx.py [--no-build] [--relay iad|off]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP
from roadkit import until, g, car_ready

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY = arg("--relay", "iad")
if RELAY == "off": RELAY = None
MAGNUM, JERRYCAN = 64, 72

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def unit(yaw): return (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))

def new_item(side, who, idn):
    me = side("mp entities")["me"]
    before = {e["net"] for e in side("mp entities")["entities"]}
    print(f"  {who} spawns {idn}:", side(f"devspawn {idn}"), flush=True)
    got = []
    until(lambda: got.extend(e["net"] for e in side("mp entities")["entities"]
                             if e["net"] not in before and e["owner"] == me and not e["car"]) or got, 10)
    return got[0] if got else None

def pos(s, net):
    e = next((e for e in s("mp entities")["entities"] if e["net"] == net), None)
    return e["pos"] if e else None

def amt(s, net):
    t = s(f"mp fluids {net}")["tanks"]
    return t[0].get("amount") if t else None

def match(a, b, tol):
    """every decal in a has one in b within tol (positions in the same frame)"""
    worst = 0.0
    for d in a:
        near = min((math.dist(d["p"], e["p"]) for e in b), default=9e9)
        worst = max(worst, near)
    return worst <= tol, worst

def shoot(side, who, p):
    r = side(f"mp shootpoint {p[0]} {p[1]} {p[2]}")
    print(f"  {who} shoots {[round(x, 2) for x in p]}: {r}", flush=True)
    return r

def leak(tag, shooter, can, owner_rate_ref=None):
    shooter_name = "laptop" if shooter is L else "host"
    # water: a shot fuel can may EXPLODE (the revolver has explodeStuffs) — the leak is what's measured here
    print(f"  {tag}: fill the can:", b(f"mp fill {can} 0 water 15"), flush=True); time.sleep(4)
    p = pos(shooter, can)
    a0 = amt(b, can)
    # low: the gun lies on the road; 0.2 m up flew over a can lying on its side (run 4: hit a house 32 m behind)
    r = shoot(shooter, shooter_name, [p[0], p[1] + 0.08, p[2]])
    if not any("benzineskanna" in h for h in r.get("shotHits", [])):
        print(f"  {tag}: missed the can — again, at its middle", flush=True)
        shoot(shooter, shooter_name, [p[0], p[1] + 0.03, p[2]])
    time.sleep(1.0)
    hb, hl = b(f"mp decals {can}")["holes"], L(f"mp decals {can}")["holes"]
    t0 = time.time(); a1 = amt(b, can)
    time.sleep(8)
    a2 = amt(b, can); dt = time.time() - t0
    time.sleep(4)
    ha, la = amt(b, can), amt(L, can)
    rate = (a1 - a2) / dt if a1 is not None and a2 is not None else 0
    print(f"  {tag}: holes owner {hb} / laptop {hl}; owner {a0} → {ha}, laptop's copy {la}; leak {rate:.3f} L/s", flush=True)
    verdict(f"{tag}: A HOLE ON BOTH", hb >= 1 and hl >= 1, f"owner {hb} / laptop {hl}")
    verdict(f"{tag}: IT LEAKS ON THE OWNER", ha is not None and a0 - ha > 0.2, f"{a0} → {ha}")
    verdict(f"{tag}: BOTH SEE THE SAME CAN", ha is not None and la is not None and abs(ha - la) < 0.1, f"owner {ha} / laptop {la}")
    if owner_rate_ref is not None:
        verdict(f"{tag}: LEAKS AS FAST AS THE OWNER'S OWN SHOT (no double drain)", owner_rate_ref > 0 and abs(rate - owner_rate_ref) <= 0.3 * owner_rate_ref,
                f"{rate:.3f} vs {owner_rate_ref:.3f} L/s")
    return rate

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
        time.sleep(4)
        rp = b("roadpoint"); yaw = rp["yaw"]; D = unit(yaw); R = (D[1], -D[0])
        cx, cz = rp["gx"], rp["gz"]
        b(f"tpground {cx - R[0] * 10} {cz - R[1] * 10}"); time.sleep(2)
        if not car_ready(b, cx, cz, yaw, "host (its car)"): sys.exit("host car not ready")
        me = b("mp entities")["me"]; gp = b("gpos")
        cars = [e for e in b("mp entities")["entities"] if e["car"] and e["owner"] == me and not e["dormant"]]
        car = min(cars, key=lambda e: math.hypot(e["pos"][0] - gp["gx"], e["pos"][2] - gp["gz"]))["net"]
        b("brakecar"); b("handbrake on"); time.sleep(1)
        b("driveout"); time.sleep(1)
        b(f"tpground {cx - R[0] * 12} {cz - R[1] * 12}"); time.sleep(2)
        L(f"tpground {cx + R[0] * 7} {cz + R[1] * 7}"); time.sleep(2)
        print("laptop magnum:", L(f"devspawn {MAGNUM}"), flush=True); time.sleep(2)
        if not until(lambda: "parts" in L(f"mp carbreak {car}"), 15): sys.exit("the laptop has no copy of the car")
        s0b, s0l = b("mp shotfx"), L("mp shotfx")

        # ---- 1: car
        cp = pos(L, car)   # a car01 has no breakable parts (mp carbreak lists none): shoot its body
        for k in (-1.2, 0.0, 1.2):
            shoot(L, "laptop", [cp[0] + D[0] * k, cp[1] + 0.7, cp[2] + D[1] * k]); time.sleep(1.2)
        time.sleep(3)
        db, dl = b(f"mp decals {car}")["decals"], L(f"mp decals {car}")["decals"]
        print(f"  1 CAR: holes owner {len(db)} {[d['on'] for d in db]}", flush=True)
        print(f"  1 CAR: holes laptop {len(dl)} {[d['on'] for d in dl]}", flush=True)
        ok, worst = match(dl, db, 0.03)
        verdict("1 CAR: THE OWNER SEES THE SAME HOLES", len(dl) > 0 and len(db) == len(dl) and ok, f"laptop {len(dl)}, owner {len(db)}; worst gap {worst:.3f} m")
        s1b = b("mp shotfx")
        verdict("1 CAR: THE OWNER GOT THE SHOTS WITH THE GUN (bang + flash)", s1b["applied"] - s0b["applied"] >= 3 and s1b["noGun"] == s0b["noGun"],
                f"applied {s1b['applied'] - s0b['applied']}, without the gun {s1b['noGun'] - s0b['noGun']}")

        # ---- 2: ground
        lp = L("gpos")
        tx, tz = lp["gx"] - D[0] * 3, lp["gz"] - D[1] * 3
        gy = L(f"ground {tx} {tz}")["y"]             # the road's height there (tpground no longer reports it since v0.61.1)
        L(f"tpground {lp['gx']} {lp['gz']}"); time.sleep(1.5)
        print("laptop magnum (in reach):", L(f"devspawn {MAGNUM}"), flush=True); time.sleep(2)
        tgt = [tx, gy - 0.05, tz]
        shoot(L, "laptop", tgt); time.sleep(3)
        gb = b(f"mp decals {tgt[0]} {tgt[1]} {tgt[2]} 1.5")["decals"]; gl = L(f"mp decals {tgt[0]} {tgt[1]} {tgt[2]} 1.5")["decals"]
        print(f"  2 GROUND: owner {gb}\n  2 GROUND: laptop {gl}", flush=True)
        # terrain is sand to Shot (layers 17/19): sand particles + a sound, never a hole — in single player too
        sg = b("mp shotfx")
        verdict("2 GROUND: THE HOST GOT THE SHOT, NO HOLE ON EITHER (as the game: sand)", sg["applied"] - s1b["applied"] == 1 and not gb and not gl,
                f"applied {sg['applied'] - s1b['applied']}, holes host {len(gb)} / laptop {len(gl)}")

        # ---- 4 first (the reference): the host shoots its own can
        b(f"tpground {cx + R[0] * 7 - D[0] * 15} {cz + R[1] * 7 - D[1] * 15}"); time.sleep(2)
        print("host magnum:", b(f"devspawn {MAGNUM}"), flush=True); time.sleep(2)
        hcan = new_item(b, "host", JERRYCAN)
        time.sleep(3)
        ref = leak("4 OWN (reference)", b, hcan)
        # ---- 3: the laptop shoots the host's can
        b(f"tpground {cx - R[0] * 12} {cz - R[1] * 12}"); time.sleep(2)
        b2 = new_item(b, "host", JERRYCAN)
        time.sleep(2)
        L(f"tpground {pos(b, b2)[0] + R[0] * 1.5} {pos(b, b2)[2] + R[1] * 1.5}"); time.sleep(2)
        print("laptop magnum again:", L(f"devspawn {MAGNUM}"), flush=True); time.sleep(2)
        leak("3 TANK", L, b2, owner_rate_ref=ref)
        print("shotfx host:", b("mp shotfx"), "laptop:", L("mp shotfx"), flush=True)
        L("mp stop"); b("mp stop")
        if RELAY: b("net relay off"); L("net relay off")
finally:
    L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")

#!/usr/bin/env python3
"""A car's contents go with the car (playtest 2026-10-16: when the other player took over the host's car the host's items in it
didn't go with it, and items the other player touched in the host's car didn't come back; the joiner drove the host's car with
the host's food, jerry cans and katana in it as kinematic copies: crashes of Δv 9-668, a death). Two machines over the
relay, the host's physics lock at 3 (the passenger room holds things), a car01 on the road:
  A  the host's lamp and jerry can on the back seats (stored by the car), the host moves to the passenger seat, the
     laptop takes the wheel: the items become the laptop's on both machines; the laptop drives --kmh for 15 s and
     stops — no crash cause on the laptop, the items still in the car on both machines
  B  the host takes the wheel back (the items come back to the host); the laptop takes the lamp lying in the host's car
     (`mp claim`, what a touch or a grab does): the host's again; the host drives --kmh for 15 s — no crash cause on the
     host, the items still in the car on both machines
  tools/takeover.py [--no-build] [--relay iad|off] [--kmh 50] [--items 320,72] [--old: before v0.68.1: no contents claims, stored copies drawn at their own states]"""
import sys, os, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import g, car_ready, clear_road

os.environ.setdefault("TLD_TEST_MODS", "CarTrailer,HeadUnit")   # the example mods stay installed

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD, KMH = arg("--relay", "iad"), "--old" in sys.argv, arg("--kmh", 50.0, float)
ITEMS = [int(x) for x in arg("--items", "320,72").split(",")]
T = "TLDRevamp.Net.Entities"; CC = "TLDRevamp.Diagnostics.CrashCause"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None
def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
def inside(p): return abs(p[0]) < 1.3 and -0.6 < p[1] < 2.0 and abs(p[2]) < 3.0   # in the car's box (car frame, m)
def crashes(s): return int(s(f"get {CC}.Logged")["value"])
def speed(s): return abs(float(g(s, "mainscript.s.player.Car.speed") or 0))

def drive(s, other, car, nets, tag):
    """`s` drives the car at KMH for 15 s and stops; the items sampled in the car's frame on both machines"""
    c0, o0 = crashes(s), crashes(other); ids = " ".join(map(str, nets))
    s(f"autodrive on {KMH / 3.6:.1f}")
    out, vmax, t0 = [], 0.0, time.monotonic()
    while time.monotonic() - t0 < 15:
        vmax = max(vmax, speed(s))
        for who, x in (("driver", s), ("other", other)):
            for n, p in x(f"mp relpos {car} {ids}")["items"].items():
                if not inside(p): out.append((who, round(time.monotonic() - t0, 1), n, [round(v, 2) for v in p]))
        time.sleep(0.3)
    s("autodrive on 0")   # ease to a halt, then brake: `brakecar` alone zeroes the speed in one frame (a crash by itself)
    until(lambda: speed(s) < 3, 20, 0.3); s("autodrive off"); s("brakecar"); s("handbrake on"); time.sleep(2)
    dc = crashes(s) - c0
    verdict(f"{tag}: DRIVEN AT ~{KMH:.0f} KM/H, NO CRASH", dc == 0 and vmax > KMH * 0.7,
            f"top {vmax:.0f} km/h, crash causes {dc} {s(f'get {CC}.Last')['value'][:400] if dc else ''}")
    verdict(f"{tag}: THE ITEMS STAYED IN THE CAR ON BOTH MACHINES", not out, str(out[:6]))

def owners(nets):
    return {n: ((ents(h).get(n) or {}).get("owner"), (ents(L).get(n) or {}).get("owner")) for n in nets}

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    gods = {}; shift = {}; lock_h = g(h, "settingsscript.s.S.IPhysicsLock")
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_(f"set {T}.ContentsFollowCar {not OLD}"); s_(f"set {T}.StoredRidesCar {not OLD}")
        print(f"  {'OLD (before v0.68.1)' if OLD else 'CURRENT BUILD'}", flush=True)
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
            shift[name] = (s_, g(s_, "settingsscript.s.S.BAutoShift")); s_("set settingsscript.s.S.BAutoShift True")
        h("set settingsscript.s.S.IPhysicsLock 3")
        hid, lid = h("mp entities")["me"], L("mp entities")["me"]
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        if not car_ready(h, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        me = h("mp players")["me"]
        car = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
        nets = []
        for k, item in enumerate(ITEMS):
            before = set(ents(h))
            h(f"devspawn {item}")
            n = until(lambda: next((n for n, e in ents(h).items() if n not in before and not e["car"]), None), 15)
            print(f"  item {item} = net {n} on back seat {k}:", h(f"mp putincar {car} {n} {k}"), flush=True); nets.append(n)
        time.sleep(5)
        ids = " ".join(map(str, nets))
        until(lambda: all(str(n) in L(f"mp relpos {car} {ids}")["items"] for n in nets), 30)
        locks = {r[8]: r[5] + " in " + r[7] for r in h(f"mp physlocks {me[0]} {me[2]} 10")["items"] if r[8] in nets}
        print("  host: held by containers", locks, flush=True)
        # ---- A: the laptop takes the wheel, the host rides along
        print("  host to the passenger seat:", h("driveout"), flush=True); time.sleep(1)
        for _ in range(3):
            h(f"mp sitpassenger {car}"); time.sleep(1.5)
            if g(h, "mainscript.s.player.Bsitting") is True: break
        L(f"tpground {me[0] + R[0] * 3} {me[2] + R[1] * 3}"); time.sleep(2)
        for _ in range(4):
            L(f"mp sitdriver {car}"); time.sleep(1.5)
            if g(L, "mainscript.s.player.Bsitting") is True: break
        L("fuelup")
        got = until(lambda: (ents(h).get(car) or {}).get("owner") == lid and (ents(L).get(car) or {}).get("owner") == lid, 15)
        verdict("A: THE LAPTOP HAS THE CAR", bool(got), f"host riding {g(h, 'mainscript.s.player.Bsitting')}")
        ok = until(lambda: all(o == (lid, lid) for o in owners(nets).values()), 6)
        verdict("A: THE HOST'S ITEMS WENT WITH THE CAR", bool(ok), f"owners (host's view, laptop's view): {owners(nets)}")
        drive(L, h, car, nets, "A")
        # ---- B: the host takes the wheel back; the laptop touches the lamp
        h("driveout"); L("driveout"); time.sleep(1.5)
        for _ in range(4):
            h(f"mp sitdriver {car}"); time.sleep(1.5)
            if g(h, "mainscript.s.player.Bsitting") is True: break
        got = until(lambda: (ents(h).get(car) or {}).get("owner") == hid, 15)
        ok = until(lambda: all(o == (hid, hid) for o in owners(nets).values()), 6)
        verdict("B: THE HOST HAS THE CAR AND ITS ITEMS BACK", bool(got) and bool(ok), f"owners {owners(nets)}")
        lamp = nets[0]
        # the laptop takes the lamp (`mp claim`: what its player's touch or grab does) while it lies in the host's car
        print("  laptop takes the lamp:", L(f"mp claim {lamp}"), flush=True)
        took = until(lambda: owners([lamp])[lamp] == (lid, lid), 3, 0.2)
        back = until(lambda: owners([lamp])[lamp] == (hid, hid), 10)
        verdict("B: THE LAMP THE LAPTOP TOOK IN THE HOST'S CAR IS THE HOST'S AGAIN", bool(took) and bool(back), f"the laptop's for a moment {bool(took)}, owners now {owners([lamp])}")
        time.sleep(2)
        drive(h, L, car, nets, "B")
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
        for name, s_ in (("host", h), ("laptop", L)): print(f"  {name}: contents claimed {s_(f'get {T}.ContentsClaimed')['value']}, stored {s_(f'get {T}.ContentsStored')['value']}, on owner {s_(f'get {T}.ContentsOnOwner')['value']}, on place {s_(f'get {T}.ContentsOnPlace')['value']}", flush=True)
    finally:
        for s_ in (h, L):
            try: s_("autodrive off")
            except Exception: pass
        for s_, v in list(gods.values()): s_(f"set kaposztaleves.s.settings.god {v}")
        for s_, v in list(shift.values()): s_(f"set settingsscript.s.S.BAutoShift {v}")
        h(f"set settingsscript.s.S.IPhysicsLock {lock_h}")
        for s_ in (h, L):
            try: s_(f"set {T}.ContentsFollowCar True"); s_(f"set {T}.StoredRidesCar True")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"TAKEOVER: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")

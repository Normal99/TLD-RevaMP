#!/usr/bin/env python3
"""PALLET + RADIATOR (the two parts tools/oct17.py left: C failed, D never ran) with what the copy-lock rules see, two
machines over the relay:
  C  the host's jerry can on its car's back seat, taken out and set down ~3 m (C2: 1.5 m) to the side: out of the car on the laptop too
     (mp lockdiag on the laptop's copy, sampled while it waits)
  D  RADIATOR on the laptop's own car (--car, default Car02; the Car01's needs Fixes.SliceGuard): its radiator
     holds a little gas and goes off: gone on the laptop and on the host
  tools/pallet.py [--no-build] [--relay iad|off] [--old] [--car <prefab part>] (switches off: MemberGoneSync, CopyLockByBox, ProxyByOwnRoot, MountBox, Fixes.SliceGuard)"""
import sys, os, time, math, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import g, car_ready, clear_road

os.environ.setdefault("TLD_TEST_MODS", "CarTrailer,HeadUnit")   # the example mods stay installed

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
CARNAME = arg("--car", "Car02")
T = "TLDRevamp.Net.Entities"; REP = "TLDRevamp.Net.Reports"
SWITCHES = [f"{T}.MemberGoneSync", f"{T}.CopyLockByBox", f"{T}.ProxyByOwnRoot", f"{T}.MountBox", "TLDRevamp.Fixes.SliceGuard.Enabled"]
JERRYCAN, LAMP = 72, "regizseblampa"
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
def new_item(s, what):
    before = set(ents(s)); s(f"devspawn {what}")
    return until(lambda: next((n for n, e in ents(s).items() if n not in before and not e["car"]), None), 15)
def tanks(s, car): return s(f"mp fluids {car}").get("tanks", [])
def coolant(s, car): return next((t for t in tanks(s, car) if "Coolant" in t.get("item", "")), None)
def num(s, k): return int(s(f"get {k}")["value"])
def same_light(a, b): return a["lightsOn"] == b["lightsOn"] and a["turn"] == b["turn"]

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    gods = {}; lock_h = g(h, "settingsscript.s.S.IPhysicsLock")
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L):
            for sw in SWITCHES: s_(f"set {sw} {not OLD}")
        print(f"  {'OLD (switches off)' if OLD else 'CURRENT BUILD'}", flush=True)
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
        h("set settingsscript.s.S.IPhysicsLock 3")
        hid, lid = h("mp entities")["me"], L("mp entities")["me"]
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        cx, cz = r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8
        if not car_ready(h, cx, cz, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        me = h("mp players")["me"]
        car = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
        L(f"tpground {cx + R[0] * 6} {cz + R[1] * 6}"); time.sleep(2)
        until(lambda: car in ents(L), 20)


        # ---- C: out of the container, with what the laptop's copy-lock rules see (3 m, then 1.5 m to the side)
        for step, off in (("C", 3.0), ("C2", 1.5)):
            can = new_item(h, JERRYCAN)
            print(f"  {step}: jerry can {can} on the back seat:", h(f"mp putincar {car} {can} 0"), flush=True); time.sleep(5)
            lp0 = until(lambda: L(f"mp relpos {car} {can}")["items"].get(str(can)), 20)
            print(f"  host: the can in the car's frame {h(f'mp relpos {car} {can}')['items'].get(str(can))}, laptop {lp0}", flush=True)
            print("  laptop's copy:", L(f"mp lockdiag {can}"), flush=True)
            if g(h, "mainscript.s.player.Car") is not None: h("driveout"); time.sleep(1.5)
            print(f"  host takes it out, {off} m to the side:", h(f"mp takeout {can} {R[0] * off:.2f} {R[1] * off:.2f}"), flush=True)
            seen = []; t0 = time.time()
            def out_on_laptop():
                d = L(f"mp lockdiag {can}"); k = json.dumps({x: d.get(x) for x in ("parent", "body", "recv", "lockOffSince", "ownerPoseOff", "lock")})
                if not seen or seen[-1][1] != k: seen.append((round(time.time() - t0, 1), k))
                a, b = ents(h).get(can), ents(L).get(can)
                return d.get("lock") is None and a and b and math.dist(a["pos"], b["pos"]) < 0.6 and [round(math.dist(a["pos"], b["pos"]), 2)]
            got = until(out_on_laptop, 12)
            for ts, k in seen[:8]: print(f"   laptop +{ts}s {k}", flush=True)
            hp = h(f"mp relpos {car} {can}")["items"].get(str(can)); lp = L(f"mp relpos {car} {can}")["items"].get(str(can))
            verdict(f"{step}: TAKEN OUT ON THE HOST ({off} M), OUT OF THE CAR ON THE LAPTOP", bool(got), f"car frame: host {hp}, laptop {lp}, apart {got}")

        # ---- D: the laptop's own car's radiator goes off
        ids = L(f"itemids {CARNAME}")["items"]
        print(f"  {CARNAME} prefabs:", ids, flush=True)
        cid = next((i for i, n in ids if n.lower().startswith(CARNAME.lower()) and "full" in n.lower()), ids[0][0] if ids else None)
        dx, dz = cx - F[0] * 30, cz - F[1] * 30
        before = set(ents(L))
        print("  laptop spawns:", L(f"spawncarg {cid} 0 {dx} {dz} {yaw}"), flush=True)
        car2 = until(lambda: next((n for n, e in ents(L).items() if n not in before and e["car"] and e["part"] < 0 and not e["proxy"]), None), 15)
        L(f"tpground {dx + R[0] * 4} {dz + R[1] * 4}"); h(f"tpground {dx + R[0] * 8} {dz + R[1] * 8}"); time.sleep(3)
        until(lambda: car2 in ents(h), 20)
        ct = coolant(L, car2) if car2 else None
        if not ct: verdict("D: THE CAR HAS A RADIATOR TANK", False, f"car {car2}: {car2 and tanks(L, car2)}")
        else:
            hc0 = until(lambda: coolant(h, car2), 10)
            print(f"  radiator: tank {ct['ti']} of item {ct['item']} (index {ct['index']}); host sees {hc0 and hc0['item']}", flush=True)
            print("  fill:", L(f"mp fill {car2} {ct['ti']} gas 2"), flush=True)
            L(f"tpground {dx + R[0] * 25} {dz + R[1] * 25}"); h(f"tpground {dx + R[0] * 28} {dz + R[1] * 28}"); time.sleep(2)
            try: print("  set off:", L(f"mp tankexplode {car2} {ct['ti']} 1000"), flush=True)
            except RuntimeError as ex: print("  set off: the game threw:", str(ex)[:200], flush=True)
            gone_l = until(lambda: coolant(L, car2) is None, 5)
            gone_h = until(lambda: coolant(h, car2) is None, 10)
            verdict("D: RADIATOR GONE ON THE LAPTOP (OWNER)", bool(gone_l), str(L("mp membergone")))
            verdict("D: RADIATOR GONE ON THE HOST TOO", bool(gone_h), f"host {h('mp membergone')}")
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        for s_, v in list(gods.values()): s_(f"set kaposztaleves.s.settings.god {v}")
        try: h(f"set settingsscript.s.S.IPhysicsLock {lock_h}")
        except Exception: pass
        for s_ in (h, L):
            try:
                for sw in SWITCHES: s_(f"set {sw} True")
            except Exception: pass
        try: L("mp stop"); h("mp stop")
        except Exception: pass
    print(f"\n{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed", flush=True)

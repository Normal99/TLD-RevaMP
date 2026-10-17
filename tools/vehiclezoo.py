#!/usr/bin/env python3
"""Every vehicle the game can spawn, on two machines (relay iad by default). The bus lost its rear wheels on the joining
player's machine (slots outside its body's hierarchy, v0.65.13) — any vehicle can have its own such layout. Per vehicle:
the host spawns it (dev menu), the laptop stands by; once both have it, every part slot (filled + attached) and the
share group's size compared between the machines. Then both players leave 5 km (everything into the far store on both)
and come back: compared again, and against what the host had before.
  tools/vehiclezoo.py [--no-build] [--relay iad|off] [--only id,id,...] [--no-store] [--spawner host|laptop] [--drive: the laptop drives each Full vehicle 10 s; no far store]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import tp

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
ONLY = {int(x) for x in sys.argv[sys.argv.index("--only") + 1].split(",")} if "--only" in sys.argv else None
VERDICTS = []

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def ent(x, net):
    return next((e for e in x("mp entities")["entities"] if e["net"] == net), None)

def slots(x, net):
    return [(r[0], bool(r[1]), r[2]) for r in x(f"mount slots {net}")["slots"]]

def compare(net, name, base=None):
    """differences: host vs laptop slot by slot (and the host vs `base` if given), group sizes"""
    sh, sl = slots(h, net), slots(L, net)
    d = []
    if len(sh) != len(sl): d.append(f"slot count host {len(sh)} laptop {len(sl)}")
    for i, (a, b) in enumerate(zip(sh, sl)):
        if a[1:] != b[1:]: d.append(f"#{i} {a[0]}: host {a[1:]} laptop {b[1:]}")
    if base is not None:
        for i, (a, b) in enumerate(zip(base, sh)):
            if a[1:] != b[1:]: d.append(f"#{i} {a[0]}: host before {a[1:]} after {b[1:]}")
    # every tank's contents (fuel, oil, coolant, ...): the copy must hold what the owner's does
    # active ones only: a fresh spawn keeps the prefab's unused alternate parts (Car06Full's spare diesel engine, inactive)
    # that a copy built from the record never has
    th = [t for t in h(f"mp carbreak {net}").get("tanks", []) if t.get("active", True)]
    tl = [t for t in L(f"mp carbreak {net}").get("tanks", []) if t.get("active", True)]
    inactive = [t.get("path") for t in h(f"mp carbreak {net}").get("tanks", []) if not t.get("active", True)]
    if inactive: print(f"         (inactive on the host, left out: {inactive})", flush=True)
    if len(th) != len(tl): d.append(f"tank count host {len(th)} laptop {len(tl)}: host {[(t.get('path'), t.get('item'), round(t['amount'], 2)) for t in th]} laptop {[(t.get('path'), t.get('item'), round(t['amount'], 2)) for t in tl]}")
    for a_, b_ in zip(th, tl):
        if abs(a_["amount"] - b_["amount"]) > 0.05: d.append(f"tank {a_['name']}: host {a_['amount']:.2f} laptop {b_['amount']:.2f}")
    nh, nl = h(f"mount items {net}").get("n"), L(f"mount items {net}").get("n")
    if nh != nl: d.append(f"group host {nh} laptop {nl}")
    return d, sh

DRIVE = "--drive" in sys.argv
SPAWNER = sys.argv[sys.argv.index("--spawner") + 1] if "--spawner" in sys.argv else "host"   # who spawns (and shares) each vehicle
DRIVER = sys.argv[sys.argv.index("--driver") + 1] if "--driver" in sys.argv else "laptop"   # A/B: host = no other player

def dists(x, net):
    """each group item's distance from the vehicle's root (mount items rows end in "<d>m")"""
    out = {}
    for row in x(f"mount items {net}").get("items", []):
        parts = row.split(" ")
        try: out[parts[0]] = (parts[1], float(parts[-1].rstrip("m")))
        except ValueError: pass
    return out

def drive(net, cname):
    """the laptop drives it ~10 s and brakes: every item must travel with the vehicle on both machines (the bus's rear
    wheels, saved as a world point, stayed where it stood)"""
    dh0, dl0 = dists(h, net), dists(L, net)
    p0 = ent(h, net)["pos"]
    D = h if DRIVER == "host" else L
    r = None
    for _ in range(6):
        r = D(f"mp sitdriver {net}")
        if "seat" in r and not r.get("stepped"): break
        time.sleep(1.5)
    if not D("get mainscript.s.player.Bsitting")["value"]: return [f"the laptop couldn't sit ({r})"], 0.0
    # a vehicle just taken over: its engine links the tank on its first own update (the copy's engine was the owner's
    # mirror) — top up after that, the same for either driver (a dev-spawned Bus02 comes with an empty tank)
    until(lambda: D("get mainscript.s.player.Car.Engine.Fuel")["value"] is not None, 5)
    try: print("        fuelup:", D("fuelup"), flush=True)
    except RuntimeError as ex: print("        fuelup:", ex, flush=True)
    if "--probe" in sys.argv:
        for q in ("mainscript.s.player.Car", "mainscript.s.player.Car.Engine", "mainscript.s.player.Car.Engine.Fuel", "mainscript.s.player.Car.Engine.iFuel",
                  "mainscript.s.player.Car.Engine.running", "mainscript.s.player.Car.Tank", "mainscript.s.player.Car.tankslot",
                  "mainscript.s.player.Car.Engine.enabled", "TLDRevamp.Net.Entities.ProxyEngineOwner.Count"):
            try: print(f"        {q} = {D('get ' + q)}", flush=True)
            except Exception as ex: print(f"        {q}: {ex}", flush=True)
    D("autodrive on 8")
    for k in range(5):
        time.sleep(2)
        if "--probe" in sys.argv:
            full = D(f"mount bodies {net}"); print(f"        t{2*k+2}s car {full.get('car')} fuel {D('get mainscript.s.player.Car.Engine.Fuel')} iFuel {D('get mainscript.s.player.Car.Engine.iFuel')}", flush=True)
            if k == 2: print(f"        wheels {full.get('wheels(name,en,active,motor,brake,rpm,grounded,rb)')}", flush=True)
    D("autodrive on 0"); until(lambda: D(f"mount bodies {net}")["car"]["rbVel"] < 0.3, 10, 1.0); D("autodrive off")
    time.sleep(2)
    moved = math.dist(p0, ent(h, net)["pos"])
    d = []
    for nm, x, before in (("host", h, dh0), ("laptop", L, dl0)):
        after = dists(x, net)
        for k, (name, dist0) in before.items():
            if k in after and abs(after[k][1] - dist0) > 1.5: d.append(f"{nm}: item {k} {name} {dist0:.1f} m from the vehicle -> {after[k][1]:.1f} m")
    try: D("driveout")
    except RuntimeError: pass
    return d, moved

L = Agent()
ensure_both("--no-build" not in sys.argv)
RESULTS = {}
DRIVE_BAD = []
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    gods = {}
    try:
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, s_("get kaposztaleves.s.settings.god").get("value")); s_("set kaposztaleves.s.settings.god True")
        cars = [c for c in h("carprefabs")["cars"] if ONLY is None or c[0] in ONLY]
        print(f"{len(cars)} vehicles:", [(c[0], c[1]) for c in cars], flush=True)
        rp = h("roadpoint"); x0, z0 = rp["gx"] + 30, rp["gz"]   # a line beside the road, 30 m apart
        for i, (cid, cname, nslots, outside) in enumerate(cars):
            x, z = x0 + 30 * (i % 6), z0 + 30 * (i // 6)
            S = L if SPAWNER == "laptop" else h
            if S is L: tp(L, x, z); h(f"tpground {x + 12} {z}")
            else: tp(h, x, z); L(f"tpground {x + 12} {z}")
            time.sleep(2)
            before = {e["net"] for e in h("mp entities")["entities"]}
            r = S(f"devspawn {cid}")
            ok = until(lambda: any(e["net"] not in before and e["car"] for e in h("mp entities")["entities"]), 20)
            if not ok: print(f"  {cid} {cname}: no shared car after the spawn ({r})", flush=True); RESULTS[cid] = (cname, None, ["no shared car"], None); continue
            net = next(e["net"] for e in h("mp entities")["entities"] if e["net"] not in before and e["car"])
            got = until(lambda: ent(L, net) is not None and len(slots(L, net)) > 0, 30)
            time.sleep(3)
            d, sh = compare(net, cname) if got else (["the laptop never got it"], slots(h, net))
            RESULTS[cid] = (cname, net, d, sh)
            filled = sum(1 for s in sh if s[1])
            print(f"  {cid:>4} {cname:<28} net {net:<5} slots {len(sh):>3} filled {filled:>3} | {len(d)} differences", flush=True)
            for x_ in d[:6]: print("        ", x_, flush=True)
            if DRIVE and got and "full" in cname.lower():
                dd, moved = drive(net, cname)
                print(f"        driven by the {DRIVER}: {moved:.0f} m on the host's view, {len(dd)} parts left behind", flush=True)
                for x_ in dd[:8]: print("          ", x_, flush=True)
                if dd or moved < 15: DRIVE_BAD.append(f"{cname} ({moved:.0f} m, {len(dd)} left behind)")
        if DRIVE: verdict("DRIVEN BY THE OTHER PLAYER: EVERY PART TRAVELS WITH ITS VEHICLE", not DRIVE_BAD, f"bad: {DRIVE_BAD}")
        bad = [cid for cid, (n, net, d, sh) in RESULTS.items() if d]
        verdict("SPAWNED: EVERY VEHICLE THE SAME ON BOTH MACHINES", not bad, f"{len(RESULTS) - len(bad)}/{len(RESULTS)}; differing: {[RESULTS[c][0] for c in bad]}")
        if "--no-store" not in sys.argv and not DRIVE:
            for nm, s_ in (("host", h), ("laptop", L)): print(f"  {nm} 5 km away:", tp(s_, x0 + 5000, z0 + (8 if s_ is L else 0)), flush=True)
            time.sleep(12)
            bad2 = []
            for i, (cid, (cname, net, d0, sh0)) in enumerate(RESULTS.items()):
                if net is None: continue
                if i % 6 == 0:   # walk the line back, a row at a time
                    x, z = x0 + 60, z0 + 30 * (i // 6)
                    tp(h, x, z); L(f"tpground {x + 6} {z}"); time.sleep(8)
                until(lambda: ent(h, net) is not None and ent(L, net) is not None, 20)
                d, _ = compare(net, cname, base=sh0)
                if d: bad2.append(cid)
                print(f"  back {cid:>4} {cname:<28} {len(d)} differences", flush=True)
                for x_ in d[:6]: print("        ", x_, flush=True)
            verdict("FAR STORE: EVERY VEHICLE COMES BACK THE SAME, ON BOTH MACHINES", not bad2, f"differing: {[RESULTS[c][0] for c in bad2]}")
    finally:
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

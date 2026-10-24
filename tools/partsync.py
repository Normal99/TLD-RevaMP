#!/usr/bin/env python3
"""Part and item sync when another player works on your car (2026-10-24 playtest: a hubcap that stayed on, a tyre
that couldn't be taken off a wheel that had come off, a bus engine taken out and put into another car, a physlocked
item put in, mounted parts still sending states). Two games: the host owns a Car05Full and a Bus01; the joiner (not
their owner) does the work as a player does it (TestAsPlayer).
  A the joiner takes a hubcap off the host's car             → off on both screens
  B ... then that wheel                                     → off on both (its own object)
  C ... then the tyre off that loose wheel                  → off on both (refused as "slot parent" before)
  C2 ... and bolts that wheel back on                       → on on both
  C3 a wheel falls off the host's car; the host takes its hubcap off → off on both (refused before)
  D ... the engine out of the host's bus, into the host's car (its own engine taken out first) → the same on both
  E items put into the host's car (physics lock on): by either player, with the owner far away, while it rolls
  A0 the joiner takes a wheel off with its hubcap on → both agree (the game keeps it on)
  F 10 s with nothing touched: no part mounted on the car or bus sends or gets states, no copy of one is moved by its
    own body; the host drives its car off: the bolted-back wheel keeps its place on it on the joiner's screen
  tools/partsync.py [--no-build] [--relay off|iad] [--old: Entities.SplitParentsCarry, MountedCopiesRide, PrefabChildSave False]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "off"), "--old" in sys.argv
T = "TLDRevamp.Net.Entities"; CAR05, BUS, LAMP = 154, 106, 320
HUB, TYRE, ENGINE = "DisztarcsaSlotCol", "GumiSLotCol", "EngineSlotCollider"
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
def ent(s, net): return ents(s).get(net)

def travel(s, gx, gz, who, settle=6):
    s("set mainscript.s.player.godModeTillGrounded True")
    s(f"tpg {gx} {gz} hold")
    def grounded():
        q = s("gpos"); return "y" in s(f"ground {q['ux']} {q['uz']}")
    if not until(grounded, 90, 1.0): s("tprelease"); raise RuntimeError(f"{who}: no ground at {gx:.0f},{gz:.0f}")
    r = s(f"tpground {gx} {gz}")
    if r.get("tpground") == "hop": until(grounded, 30, 1.0); s(f"tpground {gx} {gz}")
    time.sleep(settle)

def slots(s, net):
    try: return s(f"mount slots {net}").get("slots") or []
    except RuntimeError: return None

def on(s, net, slot):
    """how many slots called `slot` on the object hold an attached part here"""
    rows = slots(s, net)
    return None if rows is None else sum(1 for r in rows if r[0] == slot and r[1] and r[2])

def as_player(s, cmd):
    s(f"set {T}.TestAsPlayer True")
    try: return s(cmd)
    except RuntimeError as ex: return {"error": str(ex)}
    finally: s(f"set {T}.TestAsPlayer False")

def same_slot(a, b, net, slot, want, label, wait=12):
    got = until(lambda: (lambda x, y: (x, y) if x == want and y == want else None)(on(a, net, slot), on(b, net, slot)), wait)
    verdict(label, bool(got), f"{slot} on {net}: host {on(h, net, slot)}, joiner {on(L, net, slot)} (want {want})")

def drop(s):
    try: s("mp drop")
    except RuntimeError: pass

def mounted(s, near=None):
    """own objects (not cars) that are attached: a part bolted on, an engine in a slot — optionally within 4 m of a point"""
    return {n: e for n, e in ents(s).items() if not e["car"] and e.get("attached", 0) > 0 and (near is None or math.dist(e["pos"], near) < 4)}

def held(s, net, who):
    """how each attached member is held on (bridge `mount held`): the jointed or not-car-point ones, and a count"""
    try: r = s(f"mount held {net}")
    except RuntimeError as ex: print(f"  held {who} {net}: {ex}", flush=True); return
    rows = r.get("held", [])
    odd = [(x["name"], "joint" if not x["parented"] else "parented", "carPoint" if x["carPoint"] else "-", "slot" if x["slot"] else "-") for x in rows if not x["parented"] or not x["carPoint"]]
    print(f"  held {who} {r.get('root')} ({net}): {len(rows)} attached, not parented or not on a car point: {odd}", flush=True)

def new_ent(s, before, pred, timeout=12):
    return until(lambda: next((n for n, e in ents(s).items() if n not in before and pred(e)), None), timeout)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    SAVED = {}
    for n_, s_ in (("h", h), ("L", L)):
        s_("set TLDRevamp.TestSafety.BlockAutoSave True")
        SAVED[n_] = (s_("get kaposztaleves.s.settings.god").get("value"), s_("get settingsscript.s.S.IPhysicsLock").get("value"))
        s_("set kaposztaleves.s.settings.god True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("JOINER NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True"); L("set kaposztaleves.s.settings.god True")
        h("set settingsscript.s.S.IPhysicsLock 3")   # containers hold what is put in (the host's setting rules)
        for s_ in (h, L):
            for sw in ("SplitParentsCarry", "MountedCopiesRide", "PrefabChildSave"):
                try: s_(f"set {T}.{sw} {not OLD}")
                except RuntimeError as ex: print(f"  no switch {sw} in this build: {ex}", flush=True)
        print(f"  {'OLD (SplitParentsCarry, MountedCopiesRide, PrefabChildSave off)' if OLD else 'CURRENT BUILD'}", flush=True)
        hid, lid = h("mp entities")["me"], L("mp entities")["me"]
        time.sleep(6)
        q = h("gpos"); gx, gz = q["gx"] + 8, q["gz"] + 8
        hb = set(ents(h))
        print("  car:", h(f"spawncarg {CAR05} 0 {gx} {gz} 0"), flush=True)
        car = new_ent(h, hb, lambda e: e["car"] and e["owner"] == hid, 20)
        hb = set(ents(h))
        print("  bus:", h(f"spawncarg {BUS} 0 {gx + 9} {gz} 0"), flush=True)
        bus = new_ent(h, hb, lambda e: e["car"] and e["owner"] == hid, 20)
        if not car or not bus: sys.exit(f"cars not shared (car {car}, bus {bus})")
        travel(L, gx - 3, gz - 3, "joiner")
        until(lambda: ent(L, car) and not ent(L, car)["dormant"] and ent(L, bus) and not ent(L, bus)["dormant"], 30)
        time.sleep(4)
        print(f"  host's car {car}, bus {bus}; joiner {lid}, host {hid}", flush=True)
        held(h, car, "host"); held(h, bus, "host")

        # A0: the joiner takes a wheel off with its hubcap on. The game refuses that (partslotscript.UnCraft: a covered
        # part stays); both screens must still agree that wheel and hubcap are on
        w0 = next((r[0] for r in slots(L, car) if "WheelSlot" in r[0] and r[1]), None)
        n0, w_on = on(h, car, HUB), on(h, car, w0) if w0 else None
        print("  A0:", w0, as_player(L, f"mount off {car} {w0}") if w0 else "no wheel slot", flush=True)
        time.sleep(4)
        same_slot(h, L, car, HUB, n0, "A0: A WHEEL WITH ITS HUBCAP ON — HUBCAPS AGREE ON BOTH", 8)
        if w0: same_slot(h, L, car, w0, w_on, "A0: ... AND THAT WHEEL AGREES ON BOTH", 8)
        # A: a hubcap
        n0 = on(h, car, HUB)
        lb = set(ents(L))
        print("  A:", as_player(L, f"mount off {car} {HUB}"), flush=True)
        same_slot(h, L, car, HUB, n0 - 1, "A: THE JOINER TOOK A HUBCAP OFF THE HOST'S CAR — OFF ON BOTH")
        # B: that wheel (the first wheel slot whose hubcap is off)
        # (a wheel under its hubcap doesn't come off: try each wheel slot until one does)
        rim = wslot = None
        for wslot in [r[0] for r in slots(L, car) if "WheelSlot" in r[0] and r[1]]:
            lb = set(ents(L))
            print("  B:", wslot, as_player(L, f"mount off {car} {wslot}"), flush=True)
            rim = new_ent(L, lb, lambda e: "Felni" in e["name"] and e["part"] >= 0, 5)
            if rim: break
        same_slot(h, L, car, wslot, 0, "B: ... AND THAT WHEEL — OFF ON BOTH")
        # C: the tyre off the loose wheel
        if rim:
            until(lambda: ent(h, rim), 10); time.sleep(2)
            t0 = on(h, rim, TYRE)
            print("  C:", as_player(L, f"mount off {rim} {TYRE}"), flush=True)
            same_slot(h, L, rim, TYRE, 0, "C: ... AND THE TYRE OFF THAT LOOSE WHEEL — OFF ON BOTH", 15)
            print(f"    tyres on the rim before: {t0}; host part rejects: {h('mp entities').get('partRejects', '')[-200:]}", flush=True)
        else: verdict("C: ... AND THE TYRE OFF THAT LOOSE WHEEL — OFF ON BOTH", False, "no rim entity after B")
        # C2: the joiner bolts that wheel (its own object since B) back onto the host's car (2026-10-24: bolted-back wheels
        # "simulated independently")
        if rim:
            drop(L); time.sleep(2)
            print("  C2:", as_player(L, f"mount on {rim} {car} {wslot}"), flush=True)
            same_slot(h, L, car, wslot, 1, "C2: THE JOINER BOLTED THE WHEEL BACK ONTO THE HOST'S CAR — ON ON BOTH", 15)

        # C3 (the friend's hubcap): a wheel thrown off the host's car with its hubcap (a crash's fall-off), then the host,
        # its owner, takes that hubcap off the loose wheel
        hb = set(ents(h))
        aps = h(f"mp attachedparts {car}").get("parts", [])
        names = {p_["i"]: p_["name"] for p_ in aps}   # the car's rims are listed as "Wheel", each followed by its hubcap
        w2 = next((p_ for p_ in aps if p_["name"] == "Wheel" and "Disztarcsa" in names.get(p_["i"] + 1, "")), None)
        print("  C3 attached parts:", [(p_["i"], p_["name"]) for p_ in aps], flush=True)
        if w2:
            print("  C3 fall-off:", w2, h(f"mp falloff {car} {w2['i']}"), flush=True)
            rim2 = new_ent(h, hb, lambda e: e["part"] == w2["i"])
            if rim2:
                until(lambda: ent(L, rim2), 10); time.sleep(3)
                print("  C3 hubcap off:", h(f"mount off {rim2} {HUB}"), flush=True)
                same_slot(h, L, rim2, HUB, 0, "C3: THE HOST TOOK THE HUBCAP OFF ITS WHEEL THAT CAME OFF — OFF ON BOTH", 15)
            else: verdict("C3: THE HOST TOOK THE HUBCAP OFF ITS WHEEL THAT CAME OFF — OFF ON BOTH", False, "no wheel entity after the fall-off")
        else: verdict("C3: THE HOST TOOK THE HUBCAP OFF ITS WHEEL THAT CAME OFF — OFF ON BOTH", False, "no wheel left on the car")

        # D: the bus engine into the car
        lb = set(ents(L))
        print("  D car engine out:", as_player(L, f"mount off {car} {ENGINE}"), flush=True)
        same_slot(h, L, car, ENGINE, 0, "D: THE JOINER TOOK THE ENGINE OUT OF THE HOST'S CAR — OUT ON BOTH")
        time.sleep(1); drop(L); time.sleep(2)   # the car's engine came off into the joiner's hands
        lb = set(ents(L))
        print("  D bus engine out:", as_player(L, f"mount off {bus} {ENGINE}"), flush=True)
        same_slot(h, L, bus, ENGINE, 0, "D: ... AND THE ENGINE OUT OF THE HOST'S BUS — OUT ON BOTH")
        beng = new_ent(L, lb, lambda e: "Engine" in e["name"] and e["part"] >= 0)
        if beng:
            time.sleep(1); drop(L); time.sleep(2)
            print("  D bus engine into the car:", as_player(L, f"mount on {beng} {car} {ENGINE}"), flush=True)
            same_slot(h, L, car, ENGINE, 1, "D: ... AND PUT THE BUS ENGINE INTO THE HOST'S CAR — IN ON BOTH", 15)
            ph, pl = h(f"mount pose {beng} {car} {ENGINE}"), L(f"mount pose {beng} {car} {ENGINE}")
            verdict("D: THE BUS ENGINE SITS IN THE CAR'S SLOT ON BOTH", bool(ph.get("inSlot")) and bool(pl.get("inSlot")),
                    f"host {ph}, joiner {pl}")
        else: verdict("D: ... AND PUT THE BUS ENGINE INTO THE HOST'S CAR — IN ON BOTH", False, "no engine entity")

        held(h, car, "host"); held(L, car, "joiner")
        # E: physlocked items put into the host's car (2026-10-24: "some physlocked items did not sync when put in"): the
        # items the playtest put in (oil can, beer, paint spray), by the car's owner and by the other player, with the car
        # rolling and with its owner 1.2 km away
        def locked_in(s_, item):
            c_ = ent(s_, car)
            if not c_: return None
            rows = [r for r in s_(f"mp physlocks {c_['pos'][0]} {c_['pos'][2]} 15")["items"] if r[8] == item]
            return rows and rows[0][5] != "" and car == next((e["net"] for e in ents(s_).values() if e.get("saveId") == rows[0][6]), None) and rows[0]
        def put_case(label, s_, sid, name, at, before=None, after=None, far=False):
            other = L if s_ is h else h
            sb = set(ents(s_)); r = s_(f"devspawn {name}")
            if "error" in r:   # no prefab of exactly that name: the first whose name contains it
                hits = s_(f"itemids {name}").get("items") or []
                if hits: r = s_(f"devspawn {hits[0][0]}")
            item = new_ent(s_, sb, lambda e: not e["car"] and e["owner"] == sid and e["part"] < 0, 15) if "error" not in r else None
            if not item: verdict(f"E: {label} — LOCKED IN THE CAR ON BOTH", False, f"no {name}: {r}"); return
            if not far: until(lambda: ent(other, item), 15); time.sleep(2)
            if before: before()
            try: print(f"  E {name}:", s_(f"mp putincar {car} {item} {at}"), flush=True)   # dropped onto the seat: the game locks it
            except RuntimeError as ex:
                verdict(f"E: {label} — LOCKED IN THE CAR ON BOTH", False, f"put-in refused: {ex}; item here {ent(s_, item)}"); return
            if after: time.sleep(4); after()
            got = until(lambda: locked_in(h, item) and locked_in(L, item), 20)
            verdict(f"E: {label} — LOCKED IN THE CAR ON BOTH", bool(got), f"host {locked_in(h, item)}, joiner {locked_in(L, item)}")
            eh, el = ent(h, item), ent(L, item)
            d = math.dist(eh["pos"], el["pos"]) if eh and el else None
            verdict(f"E: ... {name} AT THE SAME PLACE ON BOTH (< 0.3 m)", d is not None and d < 0.3, f"{d}")
            try: s_(f"deleteshare {item}"); time.sleep(2)   # the seat free for the next one
            except RuntimeError as ex: print("  E delete:", ex, flush=True)
        put_case("THE JOINER'S LAMP INTO THE HOST'S CAR", L, lid, LAMP, 1)
        put_case("THE HOST'S OIL CAN INTO ITS OWN CAR", h, hid, "OilCan", 1)
        cp = ent(h, car)["pos"]
        put_case("THE JOINER'S PAINT SPRAY INTO THE HOST'S CAR WITH THE HOST 1.2 KM AWAY", L, lid, "PaintSpray", 1,
                 before=lambda: travel(h, cp[0] + 1200, cp[2], "host", settle=3), after=lambda: travel(h, cp[0] - 3, cp[2] - 3, "host", settle=4), far=True)
        def roll():
            c_ = ent(h, car)["pos"]; travel(h, c_[0] - 3, c_[2] - 3, "host", settle=2)
            c_ = ent(h, car)["pos"]; o = h(f"groundg {c_[0]} {c_[2]}")["origin"]
            print("  E car rolling:", h(f"carmove {c_[0] + o[0]} {c_[1] + o[1] + 0.3} {c_[2] + o[2]} 0 0 6"), flush=True); time.sleep(0.5)
        put_case("THE JOINER'S OIL CAN INTO THE HOST'S CAR WHILE IT ROLLS", L, lid, "OilCan", 1, before=roll, after=lambda: time.sleep(6))

        # F: mounted parts quiet. Car parts (on the car or the bus) are the bug; an item hung in the world is reported apart.
        time.sleep(4)
        def near_cars(s_):
            ps = [ent(s_, n)["pos"] for n in (car, bus) if ent(s_, n)]
            return {n: e for n, e in mounted(s_).items() if any(math.dist(e["pos"], p) < 6 for p in ps)}
        a = {"host": mounted(h), "joiner": mounted(L)}
        oncar = {"host": set(near_cars(h)), "joiner": set(near_cars(L))}
        time.sleep(10)
        b = {"host": mounted(h), "joiner": mounted(L)}
        busy = [(who, n, v["name"], b[who][n]["seqOut"] - v["seqOut"], b[who][n]["recv"] - v["recv"]) for who in a for n, v in a[who].items()
                if n in b[who] and (b[who][n]["seqOut"] - v["seqOut"] > 2 or b[who][n]["recv"] - v["recv"] > 2)]
        verdict("F: NO PART MOUNTED ON THE CAR OR BUS SENDS OR GETS STATES IN 10 S", not [x for x in busy if x[1] in oncar[x[0]]],
                f"{sum(len(x) for x in oncar.values())} mounted on them; busy {[x for x in busy if x[1] in oncar[x[0]]]}")
        print(f"  F: attached elsewhere and busy (info): {[x for x in busy if x[1] not in oncar[x[0]]]}", flush=True)
        # a bolted-on copy still moved by its own body (BodyMotionTick) instead of riding its car
        moved = [(who, n, e["name"]) for who, s_ in (("host", h), ("joiner", L)) for n, e in near_cars(s_).items() if e["proxy"] and e.get("byBody")]
        verdict("F: NO COPY BOLTED ON THE CAR OR BUS IS MOVED BY ITS OWN BODY", not moved, f"{moved}")
        # the host drives its car off at 8 m/s: the joiner's copy of the bolted-back wheel keeps its place on the car
        cp = ent(h, car)["pos"]
        travel(h, cp[0] - 3, cp[2] - 3, "host", settle=3)
        cp = ent(h, car)["pos"]; o = h(f"groundg {cp[0]} {cp[2]}")["origin"]   # unity = global + this machine's origin
        def offset(s_):
            es = ents(s_); c_, r_ = es.get(car), es.get(rim)
            if not c_ or not r_: return None
            yaw = math.radians(c_.get("yaw", 0.0)); dx, dz = r_["pos"][0] - c_["pos"][0], r_["pos"][2] - c_["pos"][2]
            return (dx * math.cos(yaw) - dz * math.sin(yaw), r_["pos"][1] - c_["pos"][1], dx * math.sin(yaw) + dz * math.cos(yaw)), c_["pos"]
        rest = offset(L) if rim else None
        print("  F car driven:", h(f"carmove {cp[0] + o[0]} {cp[1] + o[1] + 0.3} {cp[2] + o[2]} 0 0 8"), flush=True)
        worst, travelled, t0 = 0.0, 0.0, time.time()
        while rest and time.time() - t0 < 3:
            got = offset(L)
            if got:
                worst = max(worst, math.dist(got[0], rest[0])); travelled = max(travelled, math.dist(got[1], cp))
            time.sleep(0.05)
        verdict("F: THE BOLTED-BACK WHEEL STAYS ON THE MOVING CAR ON THE JOINER'S SCREEN (< 0.1 M)", bool(rest) and travelled > 3 and worst < 0.1,
                f"car moved {travelled:.1f} m on the joiner; wheel off its place by up to {worst:.3f} m")
        time.sleep(5)
        eh, el = ents(h), ents(L)
        on_car = oncar["host"]
        off = [(n, eh[n]["name"], round(math.dist(eh[n]["pos"], el[n]["pos"]), 2)) for n in on_car
               if n in eh and n in el and math.dist(eh[n]["pos"], el[n]["pos"]) > 0.3]
        verdict("F: PARTS MOUNTED ON THE CAR OR BUS AT THE SAME PLACE ON BOTH AFTER IT STOPPED (< 0.3 M)", bool(on_car) and not off,
                f"car moved {math.dist(cp, eh[car]['pos']):.1f} m; {len(on_car)} mounted; off {off}")
        er = [s_("errors")["errors"] for s_ in (h, L)]
        verdict("NO ERRORS", er == [0, 0], str(er))
    finally:
        for n_, s_ in (("h", h), ("L", L)):
            try:
                s_(f"set kaposztaleves.s.settings.god {SAVED[n_][0]}")
                if n_ == "h" and SAVED[n_][1] is not None: s_(f"set settingsscript.s.S.IPhysicsLock {SAVED[n_][1]}")
            except Exception: pass
        try: L("mp stop"); h("mp stop")
        except Exception: pass
passed = sum(1 for _, ok in VERDICTS if ok)
print(f"\nSUMMARY partsync {'OLD' if OLD else 'NEW'}: {passed}/{len(VERDICTS)} passed", flush=True)
sys.exit(0 if passed == len(VERDICTS) else 1)

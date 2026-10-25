#!/usr/bin/env python3
"""Multiplayer stress parts, driven from the player who does NOT own the vehicle (the joiner, "J"; the host "H" owns
and drives). Every part checks the same things on both machines: where each item is in the vehicle's frame (the two
games must agree), that cargo stays in the vehicle, that no car is thrown (CrashCause log), that no copy is teleported,
and the frame hitches each game had meanwhile.
  C  CARGO     J puts its own items into H's vehicle (floor, seats; spots varied by --seed), H drives --secs s with turns
               and stops. Run with the physics lock on and off (--plock 0|1, default the save's).
  T  STOOL     J sits on a bar stool in H's Bus03, holds an item while H drives, lets go on the floor
  P  PASSENGER J in H's Car01's passenger seat holds an item while H drives, lets go in the car
  W  SWAP      after C: J takes the wheel with the cargo aboard, drives, stops; H takes it back and drives
  L  LATE      H drives with J's cargo aboard, J leaves and joins again mid-drive: its view of the cargo once in
  B  BIKE      J rides its own bike holding an item; H's view of the item stays at J's hands
  D  MOVING    H drives slowly, J on foot alongside puts its items into the moving vehicle, H drives on and brakes hard
  mpstress.py [--no-build] [--only C,T,P,W,L,B,D,F] [--vehicle bus|car|pickup] [--speed 13] [--secs 20] [--seed N] [--plock 0|3]
              [--set Entities.Flag=Value ...] (A/B flags on both games)
              [--relay iad|off] (default iad: the Steam relay's real delay)"""
import sys, os, re, time, math, random, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
ONLY = arg("--only", "C,T,P,W,L,B").split(",")
SPEED, SECS, SEED = arg("--speed", 13.0, float), arg("--secs", 20, int), arg("--seed", 1, int)
VEH = arg("--vehicle", "bus"); PLOCK = arg("--plock", None); RELAY = arg("--relay", "iad"); CRAWL = arg("--crawl", 1.0, float)
T = "TLDRevamp.Net.Entities"
VERDICTS, NOTES = [], []
rnd = random.Random(SEED)

REPEAT, AB = arg("--repeat", 1, int), arg("--ab", None)   # --repeat N: the parts N times in ONE world and session (no
# new world and join per run: ~70 s each); --ab Entities.Flag=True,False: that flag set on both, alternating per repeat
REP = ""
def verdict(name, ok, detail=""):
    name = REP + name
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            r = fn()
            if r: return r
        except Exception: pass
        time.sleep(step)
    return None

def ents(x): return {e["net"]: e for e in x("mp entities")["entities"]}
def gv(x, k):
    try: return x(f"get {k}")["value"]
    except RuntimeError: return None

def spawn(x, ident, car=False):
    before = set(ents(x))
    r = x(f"devspawn {ident}")
    got = until(lambda: [n for n, e in ents(x).items() if n not in before and e["part"] < 0 and e["car"] == car and not e.get("proxy")], 20)
    if not got: raise RuntimeError(f"nothing spawned for {ident}: {r}")
    return got[0]

def sit_driver(x, net):
    r = {}
    for _ in range(6):
        r = x(f"mp sitdriver {net}")
        if "seat" in r and not r.get("stepped"): return r
        time.sleep(1.5)
    return r

def sit_pass(x, net):
    r = {}
    for _ in range(4):
        r = x(f"mp sitpassenger {net}"); time.sleep(1.5)
        if gv(x, "mainscript.s.player.Bsitting") is True: return r
    return r

def out(x):
    try: x("driveout")
    except RuntimeError: pass
    if gv(x, "mainscript.s.player.pickedUp"):
        try: x("mp drop")
        except RuntimeError: pass

def beside(x, net, side=4.0):
    p = ents(h)[net]["pos"]; x(f"tpground {p[0] + side} {p[2]}"); time.sleep(2.5)

def speed(x, car):
    try: return x(f"mount bodies {car}")["car"]["rbVel"]
    except Exception: pass
    try:
        v = ents(x)[car].get("vel"); return math.sqrt(sum(c * c for c in v)) if isinstance(v, list) else float(v)
    except Exception: return 0.0

def rel(x, car, nets):
    """each item where this game DRAWS it, in the car's frame: the bridge runs before the copies are placed for the
    frame (the car's copy already moved, its cargo not yet: 0.2-0.4 m at 12 m/s), so a second call returns the first
    call's frame as read in LateUpdate"""
    if not nets: return {}
    cmd = f"mp relpos {car} {' '.join(map(str, nets))}"
    x(cmd); r = x(cmd)
    late = {int(k): v for k, v in r.get("late", {}).items()}
    return late if len(late) == len(r["items"]) else {int(k): v for k, v in r["items"].items()}

def crashes(x): return int(gv(x, "TLDRevamp.Diagnostics.CrashCause.Logged") or 0)
def last_crash(x): return str(gv(x, "TLDRevamp.Diagnostics.CrashCause.Last") or "")
def sweeps(x): return int(gv(x, f"{T}.CopySweeps") or 0)   # a copy put > 50 m on in one step without its owner teleporting it
def hitches(x):
    s = x("stats"); return s["hitches50ms"], s["maxms"]

class Watch:
    """counters on both machines over a part: car throws, copy teleports, frames over 50 ms"""
    def __init__(self, tag):
        self.tag = tag; self.c = {s: (crashes(x), sweeps(x), hitches(x)[0]) for s, x in (("H", h), ("J", L))}
    def end(self, crash_ok=0):
        rows = {}
        for s, x in (("H", h), ("J", L)):
            c0, t0, h0 = self.c[s]; rows[s] = (crashes(x) - c0, sweeps(x) - t0, hitches(x)[0] - h0)
        thrown = rows["H"][0] + rows["J"][0]
        # the test's autodrive steering a bus into a roadside rock is a crash of its own, not a sync fault: one crash whose
        # newest hit (under 0.25 s before it) is scenery ("[local static]") is a note
        if thrown == 1 and thrown > crash_ok:
            last = last_crash(h if rows["H"][0] else L)
            hits = re.findall(r"; ([^;|]*?) \[([^\]]*)\] @([\d.]+)m/s ([\d.]+)s ago", "; " + last.split("| last hits:")[-1].split("| inside")[0])
            newest = min(hits, key=lambda x: float(x[3])) if hits else None
            if newest and float(newest[3]) < 0.25 and newest[1].strip() == "local static" and not last.split("inside it now:")[-1].strip():
                NOTES.append(f"{self.tag}: the driver hit scenery: {newest[0].strip()} at {newest[2]} m/s ({last[:120]})")
                print("  NOTE", NOTES[-1], flush=True); crash_ok += 1
        verdict(f"{self.tag}: NO CAR THROWN OR WRECKED", thrown <= crash_ok,
                f"crash log entries host {rows['H'][0]}, joiner {rows['J'][0]}" + (f"; last on host: {last_crash(h)[:2500]}" if rows["H"][0] else "") +
                (f"; last on joiner: {last_crash(L)[:2500]}" if rows["J"][0] else ""))
        verdict(f"{self.tag}: NO COPY SWEPT (JUMPED > 50 M)", rows["H"][1] + rows["J"][1] == 0, f"host {rows['H'][1]}, joiner {rows['J'][1]}")
        NOTES.append(f"{self.tag}: frames over 50 ms during the part: host {rows['H'][2]}, joiner {rows['J'][2]}")
        print("  " + NOTES[-1], flush=True)

def stop(x, car):
    try: x("autodrive on 0")
    except RuntimeError: pass
    until(lambda: speed(x, car) < 0.4, 20, 0.5)
    try: x("autodrive off"); x("handbrake on")
    except RuntimeError: pass
    time.sleep(1)

def keep_up(y, car, viewer, d=25.0):
    """y (on foot, not riding) stays within d m of the car: teleported beside it when it falls behind"""
    if gv(y, "mainscript.s.player.Bsitting") is True or gv(y, "mainscript.s.player.seat") is not None: return
    cp, me = ents(viewer)[car]["pos"], y("gpos")
    if math.dist((cp[0], cp[2]), (me["gx"], me["gz"])) > d: y(f"tpground {cp[0] + 6} {cp[2] + 6}")

def drive(x, car, secs, tag, sample=None, every=0.5):
    """x drives car for secs at SPEED (the autodrive follows the road: turns, hills); sample(t) each `every` s; the other
    player, when not riding, follows on foot (teleported along: a copy 250 m off is streamed out)"""
    y = L if x is h else h
    try: x("handbrake off")
    except RuntimeError: pass
    x("fuelup"); x(f"autodrive on {SPEED}")
    t0 = time.time(); vmax = 0.0
    try:
        while time.time() - t0 < secs:
            time.sleep(every)
            v = speed(x, car); vmax = max(vmax, v)
            keep_up(y, car, x)
            if sample: sample(time.time() - t0, v)
    finally:
        stop(x, car)
    verdict(f"{tag}: IT DROVE", vmax > SPEED * 0.5, f"top speed {vmax:.1f} m/s (asked {SPEED})")
    return vmax

def fresh_spot(x, k):
    """k*500 m on along the road from where x is (every part its own clean stretch of road): 100 m steps, each snapped
    back onto the road (a straight line off the first road point left the road)"""
    rp = x("roadpoint")
    for _ in range(k * 5):
        a = math.radians(rp["yaw"])
        for step in (100, 60, 30):
            x(f"tpground {rp['gx'] + math.sin(a) * step} {rp['gz'] + math.cos(a) * step}"); time.sleep(1.5)
            try: rp = x("roadpoint"); break
            except RuntimeError: continue
    x(f"tpground {rp['gx']} {rp['gz']}"); time.sleep(3)

def road_spawn(x, ident):
    """a vehicle on the road where x stands, along it (as bikeride.py: devspawn put a bike down that couldn't ride)"""
    rp = x("roadpoint"); yaw = rp["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
    bx, bz = rp["gx"] + R[0] * 1.8, rp["gz"] + R[1] * 1.8
    x(f"tpground {bx + R[0] * 5} {bz + R[1] * 5}"); time.sleep(2)
    before = set(ents(x))
    r = x(f"spawncarg {ident} 0 {bx} {bz} {yaw}")
    got = until(lambda: [n for n, e in ents(x).items() if n not in before and e["part"] < 0 and e["car"] and not e.get("proxy")], 20)
    if not got: raise RuntimeError(f"no vehicle {ident}: {r}")
    return got[0]

def vehicle(kind, k):
    out(h); out(L); fresh_spot(h, k)
    v = road_spawn(h, {"bus": 179, "car": 2, "bus01": 106, "pickup": 244}[kind]); time.sleep(2)
    beside(L, v, 5.0)
    if not until(lambda: v in ents(L), 30): raise RuntimeError("the joiner never got the vehicle")
    return v

# where J's items go, in the vehicle's frame (x right, y up, z forward): the bus's aisle and floor, a car's seats
SPOTS = {"bus": [(0, 0.9, z) for z in (1.6, 0.4, -0.8, -2.0, -3.2, -4.4)] + [(x_, 0.9, -1.5) for x_ in (-0.55, 0.55)],
         "car": [(-0.4, 0.85, -0.6), (0.4, 0.85, -0.6), (0.4, 0.8, 0.35), (0.0, 0.75, -1.8)],
         "pickup": [(-0.4, 0.9, -1.8), (0.4, 0.9, -2.2), (0.0, 0.9, -2.6)]}
ITEMS = [39, 320, 60, 66, 211]
BIKE = arg("--bike", 141, int)   # Bike01Full (bikeride.py rides it at 8.3 m/s)   # Radio01, regizseblampa, OilCan, ujlamp, GinLamp

def cargo_part(kind):
    tag = f"C CARGO {kind.upper()} plock {gv(h, 'settingsscript.s.S.IPhysicsLock')}"
    V = vehicle(kind, 1)
    W = Watch(tag)
    spots = rnd.sample(SPOTS[kind], min(len(SPOTS[kind]), 4)); nets = []
    beside(L, V, 10.0)   # spawned beside the vehicle, an item can land in it and be the host's before it is put in
    for sp in spots:
        n = spawn(L, rnd.choice(ITEMS)); time.sleep(0.4)
        r = L(f"mp putrel {V} {n} {sp[0]} {sp[1]} {sp[2]} clear"); nets.append(n); time.sleep(0.6)
    time.sleep(4)
    if not until(lambda: all(n in ents(h) for n in nets), 20): verdict(f"{tag}: HOST HAS THE CARGO", False, str(nets)); return V, nets
    a0, b0 = rel(h, V, nets), rel(L, V, nets)
    print(f"  at rest: host {a0}\n           joiner {b0}", flush=True)
    # the game's own storage areas in the vehicle (its physics lock holds only what lies in one) and what holds each item
    print("  storage areas:", [(r_["name"], r_["type"], r_["size"], r_["items"], "manual" if r_["manual"] else "", "noLock" if r_["noLock"] else "") for r_ in h(f"mount areas {V}")["areas"]], flush=True)
    print("  physlock per item (host):", {n: h(f"mount probe {n}").get("physlock") or "loose" for n in nets}, flush=True)
    names = {n: e["name"] for n, e in ents(L).items() if n in nets}
    print("  items:", names, "owners", [(n, ents(h)[n]["owner"]) for n in nets], flush=True)
    print("  host sits at the wheel:", sit_driver(h, V), flush=True)
    beside(L, V, 3.0)
    worst = {n: 0.0 for n in nets}; outside = set(); rows = []; gaps = []
    def sample(t, v):
        a, b = rel(h, V, nets), rel(L, V, nets)
        for n in nets:
            if n not in a or n not in b: continue
            d = math.dist(a[n], b[n]); worst[n] = max(worst[n], d)
            if d > 1.0 and len(gaps) < 12:   # why the two games disagree while it moves: the frames each side uses
                cs, ow = L(f"mp copystate {n}"), h(f"mp copystate {n}")
                gaps.append(f"t{t:4.1f} v {v:4.1f} {n} {d:.2f} m: host {a[n]} rideCar {ow.get('rideCar')} | joiner {b[n]} carriedBy {cs.get('carriedBy')} skip {cs.get('skip')!r} atRest {cs.get('atRest')}")
            if abs(a[n][0]) > 2.2 or abs(a[n][2]) > 8 or a[n][1] < -1 or abs(b[n][0]) > 2.2 or abs(b[n][2]) > 8: outside.add(n)
        if int(t * 2) % 4 == 0:
            hs = ents(h); vp = hs[V]["pos"]
            def srv(n):
                r = h(f"mp sent {n}"); w = r.get("where")
                return f"o{r.get('owner')}{'R' if r.get('atRest') else ''}{'L' if r.get('locked') else ''}@{math.dist((w[0], w[2]), (vp[0], vp[2])):.0f}m" if w else f"o{r.get('owner')}"
            rows.append(f"t{t:4.1f} v {v:4.1f} " + " ".join(f"{n}:{math.dist(a[n], b[n]):.2f}[{srv(n)}]" for n in nets if n in a and n in b))
    drive(h, V, SECS, tag, sample)
    owners_after = {n: ents(h)[n]["owner"] for n in nets}
    verdict(f"{tag}: THE CARGO STAYED THE DRIVER'S (THE HOST'S)", all(o == 0 for o in owners_after.values()),
            f"owners now {owners_after}; host's log: " + "; ".join(f"{n}: {h(f'mp ownerlog {n}')['log'][-3:]}" for n in nets))
    for r in rows: print("  ", r, flush=True)
    for g_ in gaps: print("   gap", g_, flush=True)
    time.sleep(3)
    a, b = rel(h, V, nets), rel(L, V, nets)
    # fallen out at the stop (after the last sample): the game's own physics throws Car01's front-seat item out when it
    # brakes, session or not (2 of 3 drives either way, stress campaign 2026-10-25) — a note; the two games disagreeing on it fails below
    gone = [n for n in nets if any(n in r_ and (abs(r_[n][0]) > 2.2 or abs(r_[n][2]) > 8 or r_[n][1] < -1) for r_ in (a, b))]
    if gone: NOTES.append(f"{tag}: fell out at the stop (after the drive): {gone}"); print("  NOTE", NOTES[-1], flush=True)
    rest = {n: round(math.dist(a[n], b[n]), 2) for n in nets if n in a and n in b}
    print(f"  stopped: host {a}\n           joiner {b}", flush=True)
    for n, d in rest.items():   # an item the two games disagree on: why (F7)
        if d > 1.0:
            cs = L(f"mp copystate {n}"); se = h(f"mp sent {n}")
            print(f"  {n} {d} m apart — joiner's copy: carriedBy {cs.get('carriedBy')} atRest {cs.get('atRest')} skip {cs.get('skip')} recv {cs.get('recv')} body {cs.get('body')}"
                  f" | host's record: where {se.get('where')} atRest {se.get('atRest')} locked {se.get('locked')} | host: {h(f'mount probe {n}').get('inCar')}", flush=True)
    verdict(f"{tag}: THE CARGO STAYED IN THE VEHICLE ON BOTH", not outside and len(a) == len(nets), f"left it: {sorted(outside)}")
    verdict(f"{tag}: BOTH GAMES AGREE WHERE THE CARGO IS WHILE MOVING", max(worst.values()) < 0.6, "worst gap per item " + str({n: round(w, 2) for n, w in worst.items()}))
    verdict(f"{tag}: BOTH GAMES AGREE WHERE THE CARGO IS ONCE STOPPED", rest and max(rest.values()) < 0.25, f"gap per item {rest}")
    W.end()
    out(h)
    return V, nets

def hold_part(kind):
    """J holds an item in H's moving vehicle (on a stool in a bus, or a passenger seat in a car), then lets go"""
    tag = f"{'T STOOL' if kind == 'bus' else 'P PASSENGER'} HOLD"
    V = vehicle(kind, 2 if kind == "bus" else 3)
    if kind == "bus":
        print("  host sits at the wheel:", sit_driver(h, V), flush=True); drv = h(f"mp relplayers {V}")["me"]; out(h)
        beside(h, V)
        S = spawn(h, 201)
        print("  stool:", h(f"mp putrel {V} {S} 0 {drv[1] - 0.6:.2f} {drv[2] - 2.5:.2f} clear"), flush=True); time.sleep(3)
        until(lambda: S in ents(L), 20); time.sleep(2)
    n = spawn(L, rnd.choice(ITEMS)); time.sleep(0.5)
    if kind == "bus":
        beside(L, V); print("  joiner sits on the stool:", L(f"mp sitobj {S}"), flush=True)
    else:
        print("  joiner sits:", sit_pass(L, V), flush=True)
    time.sleep(1)
    print("  joiner takes it:", L(f"mp pickupnet {n}"), flush=True); time.sleep(1.5)
    print("  host sits at the wheel:", sit_driver(h, V), flush=True); time.sleep(1)
    W = Watch(tag)
    worst, lost, rows = 0.0, [], []
    def sample(t, v):
        nonlocal worst
        a, b = rel(h, V, [n]).get(n), rel(L, V, [n]).get(n)
        if a and b: worst = max(worst, math.dist(a, b))
        if gv(L, "mainscript.s.player.pickedUp") is None and not lost: lost.append(round(t, 1))
        if int(t * 2) % 4 == 0: rows.append(f"t{t:4.1f} v {v:4.1f} host {a} joiner {b}")
    drive(h, V, SECS / 2, tag, sample)
    for r in rows[::2]: print("  ", r, flush=True)
    verdict(f"{tag}: THE JOINER STILL HOLDS IT AFTER THE DRIVE", not lost, f"dropped at t {lost}")
    verdict(f"{tag}: BOTH GAMES AGREE WHERE THE HELD ITEM IS", worst < 0.8, f"worst gap {worst:.2f} m")
    # let go while moving
    try: h("handbrake off")
    except RuntimeError: pass
    h(f"autodrive on {SPEED}"); until(lambda: speed(h, V) > SPEED * 0.6, 15)
    print("  joiner lets go at", round(speed(h, V), 1), "m/s:", L("mp drop"), flush=True)
    time.sleep(4); a, b = rel(h, V, [n]).get(n), rel(L, V, [n]).get(n)
    print(f"  4 s later: host {a} joiner {b}, owner {ents(h)[n]['owner']}", flush=True)
    stop(h, V); time.sleep(3)
    a, b = rel(h, V, [n]).get(n), rel(L, V, [n]).get(n)
    inside = a and abs(a[0]) < 2.2 and abs(a[2]) < 8 and a[1] > -1
    verdict(f"{tag}: LET GO IN THE MOVING VEHICLE, IT LANDS IN IT", bool(inside), f"host {a}")
    verdict(f"{tag}: BOTH GAMES AGREE WHERE IT LANDED", a and b and math.dist(a, b) < 0.25, f"host {a} joiner {b}")
    W.end()
    out(L); out(h)

def swap_part(V, nets):
    tag = "W SWAP"
    W = Watch(tag)
    out(h); out(L); time.sleep(2)
    beside(L, V, 3.0); print("  joiner takes the wheel:", sit_driver(L, V), flush=True)
    until(lambda: ents(h)[V]["owner"] != 0, 15)
    print("  owners after the joiner sat:", ents(h)[V]["owner"], [(n, ents(h)[n]["owner"]) for n in nets], flush=True)
    beside(h, V, 3.0)
    try: sit_pass(h, V)
    except RuntimeError as ex: print("  host on foot:", ex, flush=True)   # a gutted bus has no passenger seat: it follows
    worst = 0.0
    def sample(t, v):
        nonlocal worst
        a, b = rel(h, V, nets), rel(L, V, nets)
        for n in nets:
            if n in a and n in b: worst = max(worst, math.dist(a[n], b[n]))
    drive(L, V, SECS / 2, tag + " (joiner drives)", sample)
    out(L); out(h); time.sleep(2)
    print("  host takes it back:", sit_driver(h, V), flush=True)
    until(lambda: ents(L)[V]["owner"] == 0, 15)
    drive(h, V, SECS / 2, tag + " (host drives again)", sample)
    time.sleep(3)
    a, b = rel(h, V, nets), rel(L, V, nets)
    rest = {n: round(math.dist(a[n], b[n]), 2) for n in nets if n in a and n in b}
    verdict(f"{tag}: BOTH GAMES AGREE WHERE THE CARGO IS WHILE MOVING", worst < 0.6, f"worst {worst:.2f} m")
    verdict(f"{tag}: AND ONCE STOPPED", rest and max(rest.values()) < 0.25 and len(rest) == len(nets), str(rest))
    verdict(f"{tag}: THE CARGO STAYED IN THE VEHICLE", all(abs(p[0]) < 2.2 and abs(p[2]) < 8 for p in a.values()) and len(a) == len(nets), str(a))
    W.end()
    out(h)

def late_part(V, nets):
    tag = "L LATE JOIN"
    out(h); out(L); time.sleep(2)
    print("  host at the wheel:", sit_driver(h, V), flush=True)
    print("  joiner leaves:", L("mp stop"), flush=True); time.sleep(3)
    try: h("handbrake off")
    except RuntimeError: pass
    h("fuelup"); h(f"autodrive on {SPEED}"); until(lambda: speed(h, V) > SPEED * 0.6, 20)
    vp = ents(h)[V]["pos"]; L(f"tpground {vp[0] + 20} {vp[2] + 20}"); time.sleep(2)
    ok = join_both(h, L, rehost=False)
    verdict(f"{tag}: REJOINED WHILE THE HOST DROVE", ok)
    if not ok: stop(h, V); return
    L("set TLDRevamp.TestSafety.BlockAutoSave True"); L("set kaposztaleves.s.settings.god True")
    W = Watch(tag)
    got = until(lambda: keep_up(L, V, h) or all(n in ents(L) for n in nets) and V in ents(L), 40)
    worst = 0.0; rows = []
    for i in range(16):
        time.sleep(0.5); keep_up(L, V, h)
        a, b = rel(h, V, nets), rel(L, V, nets)
        gaps = {n: round(math.dist(a[n], b[n]), 2) for n in nets if n in a and n in b}
        if gaps: worst = max(worst, max(gaps.values()))
        if i % 4 == 0: rows.append(f"v {speed(h, V):4.1f} {gaps} joiner has {sorted(b)}")
    for r in rows: print("  ", r, flush=True)
    stop(h, V); time.sleep(3)
    a, b = rel(h, V, nets), rel(L, V, nets)
    rest = {n: round(math.dist(a[n], b[n]), 2) for n in nets if n in a and n in b}
    verdict(f"{tag}: THE REJOINED PLAYER HAS THE CARGO", bool(got) and len(b) == len(nets), f"has {sorted(b)} of {nets}")
    verdict(f"{tag}: ITS VIEW OF THE CARGO MATCHES WHILE MOVING", worst < 0.6, f"worst {worst:.2f} m")
    verdict(f"{tag}: AND ONCE STOPPED", rest and max(rest.values()) < 0.25, str(rest))
    W.end()
    out(h)

def bike_part():
    tag = "B BIKE CARRY"
    out(h); out(L); fresh_spot(h, 4)
    me = h("gpos"); L(f"tpground {me['gx'] + 4} {me['gz']}"); time.sleep(3)
    bike = road_spawn(L, BIKE); time.sleep(2)
    n = spawn(L, rnd.choice(ITEMS)); time.sleep(0.5)
    print("  joiner on its bike:", sit_driver(L, bike), flush=True); time.sleep(1)
    print("  joiner takes the item:", L(f"mp pickupnet {n}"), flush=True); time.sleep(1.5)
    until(lambda: bike in ents(h) and n in ents(h), 20)
    W = Watch(tag)
    worst = [0.0]; lost = []
    def sample(t, v):
        a, b = rel(h, bike, [n]).get(n), rel(L, bike, [n]).get(n)
        if a and b: worst[0] = max(worst[0], math.dist(a, b))
        if gv(L, "mainscript.s.player.pickedUp") is None and not lost: lost.append(round(t, 1))
        bp, me = ents(h)[bike]["pos"], h("gpos")   # the host keeps up (on foot), a few metres off the road
        if math.dist((bp[0], bp[2]), (me["gx"], me["gz"])) > 30: h(f"tpground {bp[0] + 6} {bp[2] + 6}")
    global SPEED
    sp, SPEED = SPEED, 8.3
    try: drive(L, bike, SECS / 2, tag, sample)
    finally: SPEED = sp
    verdict(f"{tag}: THE JOINER STILL HOLDS IT", not lost, f"dropped at t {lost}")
    verdict(f"{tag}: THE HOST SEES THE ITEM AT THE RIDER'S HANDS", worst[0] < 1.0, f"worst gap in the bike's frame {worst[0]:.2f} m")
    W.end()
    out(L)

def moving_load_part(kind):
    """D: H drives slowly; J, on foot alongside, puts its items into the MOVING vehicle one by one (bed, floor, seats),
    then H brakes hard (--brake) and drives on: both games must agree where the cargo is, none thrown out, no car thrown"""
    tag = f"D MOVING LOAD {kind.upper()} plock {gv(h, 'settingsscript.s.S.IPhysicsLock')}"
    V = vehicle(kind, 5)
    print("  host sits at the wheel:", sit_driver(h, V), flush=True)
    W = Watch(tag)
    try: h("handbrake off")
    except RuntimeError: pass
    h("fuelup"); h("autodrive on 7"); until(lambda: speed(h, V) > 4, 20)
    nets = []
    for sp in rnd.sample(SPOTS[kind], min(len(SPOTS[kind]), 4)):
        keep_up(L, V, h, 12)
        n = spawn(L, rnd.choice(ITEMS)); time.sleep(0.3)
        keep_up(L, V, h, 12)
        try: print(f"  joiner puts {n} in at {sp} (v {speed(h, V):.1f}):", L(f"mp putrel {V} {n} {sp[0]} {sp[1]} {sp[2]} clear")["items"], flush=True); nets.append(n)
        except RuntimeError as ex: print("  put refused:", ex, flush=True)
        time.sleep(2.5)
    worst = {n: 0.0 for n in nets}; outside = set()
    def sample(t, v):
        a, b = rel(h, V, nets), rel(L, V, nets)
        for n in nets:
            if n in a and n in b: worst[n] = max(worst[n], math.dist(a[n], b[n]))
            p = a.get(n)
            if p and (abs(p[0]) > 2.2 or abs(p[2]) > 8 or p[1] < -1): outside.add(n)
    t0 = time.time()
    while time.time() - t0 < 8: time.sleep(0.5); keep_up(L, V, h); sample(0, 0)
    h(f"autodrive on {SPEED}"); t0 = time.time()
    while time.time() - t0 < SECS / 2: time.sleep(0.5); keep_up(L, V, h); sample(0, 0)
    print(f"  hard brake at {speed(h, V):.1f} m/s:", h("brakecar"), flush=True); h("autodrive off")
    t0 = time.time()
    while time.time() - t0 < 5: time.sleep(0.5); keep_up(L, V, h); sample(0, 0)
    stop(h, V); time.sleep(3)
    a, b = rel(h, V, nets), rel(L, V, nets)
    rest = {n: round(math.dist(a[n], b[n]), 2) for n in nets if n in a and n in b}
    print(f"  stopped: host {a}\n           joiner {b}\n  owners {[(n, ents(h)[n]['owner']) for n in nets]}", flush=True)
    verdict(f"{tag}: ALL PUT IN", len(nets) == 4, f"{len(nets)} of 4")
    verdict(f"{tag}: THE CARGO STAYED IN THE VEHICLE", not outside and len(a) == len(nets), f"left it: {sorted(outside)}; host {a}")
    verdict(f"{tag}: BOTH GAMES AGREE WHILE MOVING AND BRAKING", worst and max(worst.values()) < 0.6, str({n: round(w, 2) for n, w in worst.items()}))
    verdict(f"{tag}: AND ONCE STOPPED", rest and max(rest.values()) < 0.25, str(rest))
    W.end()
    out(h)

def fallout_part(kind):
    """F: items that fall out of a moving vehicle stop on the road inside its box (behind it, below the floor line); the
    other player must see them there, not riding along in the vehicle's frame (log 32: 17 m off at the stop, 283 m)"""
    tag = f"F FALL OUT {kind.upper()}"
    V = vehicle(kind, 6)
    print("  host sits at the wheel:", sit_driver(h, V), flush=True)
    W = Watch(tag)
    try: h("handbrake off")
    except RuntimeError: pass
    # crawling (as at a stop): what falls out comes to rest on the road still inside the vehicle's box; then it drives off
    h("fuelup")
    if CRAWL > 0: h(f"autodrive on {CRAWL}"); until(lambda: speed(h, V) > CRAWL * 0.5, 25)
    back = {"car": -2.2, "bus": -6.0, "pickup": -3.0}.get(kind, -2.5)
    nets = {}
    for who, x in (("host", h), ("joiner", L)):
        keep_up(L, V, h, 8)
        n = spawn(x, rnd.choice(ITEMS)); time.sleep(0.2)
        try:
            # in the vehicle first (its states in the vehicle's frame), then out of the back onto the road
            x(f"mp putrel {V} {n} {SPOTS[kind][1][0]} {SPOTS[kind][1][1]} {SPOTS[kind][1][2]} clear"); time.sleep(2.5)
            # lying in the host's vehicle, the joiner's item is the host's by now (CargoNear): only the machine that
            # simulates it can move it (the joiner's putrel moved its copy only, and the host kept it on the seat)
            o = ents(h)[n]["owner"]; y = h if o == 0 else L
            print(f"  {who}'s {n} (owner now {'host' if y is h else 'joiner'}) dropped behind at v {speed(h, V):.1f}:", y(f"mp putrel {V} {n} 0 -0.3 {back} clear")["items"], flush=True); nets[n] = who
        except RuntimeError as ex: print(f"  {who}'s put refused:", ex, flush=True)
    time.sleep(4); h(f"autodrive on {SPEED}")
    worst = {n: 0.0 for n in nets}; t0 = time.time()
    while time.time() - t0 < 10:
        time.sleep(0.5); keep_up(L, V, h, 30)
        if time.time() - t0 < 3: continue   # still rolling on the road
        a, b = ents(h), ents(L)
        for n in nets:
            if n in a and n in b: worst[n] = max(worst[n], math.dist(a[n]["pos"], b[n]["pos"]))
    stop(h, V)
    a = ents(h); spots = {n: a[n]["pos"] for n in nets if n in a}
    print("  vehicle now", [round(c, 1) for c in a[V]["pos"]], "items on the host:", {n: [round(c, 1) for c in p] for n, p in spots.items()}, flush=True)
    rest = {}
    for n, p in spots.items():   # the joiner walks back to each: its copy must lie there
        L(f"tpground {p[0] + 3} {p[2]}"); time.sleep(3)
        b = ents(L).get(n)
        rest[n] = round(math.dist(p, b["pos"]), 2) if b else None
    print("  owners", {n: ents(h)[n]["owner"] for n in spots}, flush=True)
    verdict(f"{tag}: THEY FELL OUT (host)", len(spots) == len(nets) and all(math.dist(p, a[V]["pos"]) > 15 for p in spots.values()),
            f"{ {n: round(math.dist(p, a[V]['pos']), 1) for n, p in spots.items()} } m from the vehicle")
    verdict(f"{tag}: THE OTHER SCREEN AGREES WHILE THE VEHICLE DRIVES AWAY", worst and max(worst.values()) < 1.0, str({n: round(w, 2) for n, w in worst.items()}))
    verdict(f"{tag}: AND AT THE SPOT WHERE THEY LIE", rest and all(v is not None and v < 0.3 for v in rest.values()), str(rest))
    W.end()
    out(h)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    plock0 = None
    try:
        if RELAY != "off": relay_both(h, L, RELAY)
        if not join_both(h, L): sys.exit("JOINER NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
        h("set settingsscript.s.S.BAutoShift True"); L("set settingsscript.s.S.BAutoShift True")
        for i, a in enumerate(sys.argv):   # --set Entities.RidingFrames=False (A/B flags under TLDRevamp.Net, both games)
            if a == "--set":
                k, v = sys.argv[i + 1].split("=", 1)
                for s_ in (h, L): s_(f"set TLDRevamp.Net.{k} {v}")
                print(f"set {k} = {v} on both", flush=True)
        plock0 = gv(h, "settingsscript.s.S.IPhysicsLock")
        if PLOCK is not None: h(f"set settingsscript.s.S.IPhysicsLock {PLOCK}"); L(f"set settingsscript.s.S.IPhysicsLock {PLOCK}")
        print(f"seed {SEED}, physics lock host {gv(h, 'settingsscript.s.S.IPhysicsLock')} joiner {gv(L, 'settingsscript.s.S.IPhysicsLock')}", flush=True)
        for rep in range(REPEAT):
          V = nets = None
          if AB:
              k, vals = AB.split("=", 1); v = vals.split(",")[rep % len(vals.split(","))]
              for s_ in (h, L): s_(f"set TLDRevamp.Net.{k} {v}")
              print(f"--- repeat {rep + 1}/{REPEAT}: {k} = {v} on both", flush=True)
          elif REPEAT > 1: print(f"--- repeat {rep + 1}/{REPEAT}", flush=True)
          REP = f"[{rep + 1}{' ' + v if AB else ''}] " if REPEAT > 1 else ""
          for part in ONLY:
            print(f"=== part {part}", flush=True)
            try:
                if part == "C": V, nets = cargo_part(VEH)
                elif part == "T": hold_part("bus")
                elif part == "P": hold_part("car")
                elif part == "W" and V: swap_part(V, nets)
                elif part == "L" and V: late_part(V, nets)
                elif part == "B": bike_part()
                elif part == "D": moving_load_part(VEH)
                elif part == "F": fallout_part(VEH)
            except Exception as ex:
                import traceback; traceback.print_exc()
                verdict(f"{part}: RAN TO THE END", False, repr(ex)[:300])
    finally:
        for s_ in (h, L):
            try: s_("autodrive off"); s_("set kaposztaleves.s.settings.god False")
            except Exception: pass
        if plock0 is not None and PLOCK is not None:
            for s_ in (h, L):
                try: s_(f"set settingsscript.s.S.IPhysicsLock {plock0}")
                except Exception: pass
        try: L("mp stop"); h("mp stop")
        except Exception: pass
for n in NOTES: print("NOTE", n)
print("RESULT:", "ALL PASS" if VERDICTS and all(ok for _, ok in VERDICTS) else "FAILED: " + "; ".join(n for n, ok in VERDICTS if not ok), flush=True)

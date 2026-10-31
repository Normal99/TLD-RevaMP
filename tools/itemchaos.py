#!/usr/bin/env python3
"""Try to break items on two machines (playtest 2026-10-28/29: "items don't physlock and don't sync when I pick them up",
"some items never physlock until the save is loaded again"). Every action goes the game's own way as the player
(repro act: detach, pickup, drop, throw, seat; MountAnything's glue press), in the cars of BOTH players: a world save with
glued things on the car (TLD_PLAYSAVE_DIR holds TLDsave*_<name>.*, TLD_PLAYSAVE the name, default autosave1), both
copies with the same mods. After every step the two machines are compared; what still differs after the settle time
is a finding, with the steps that led to it.
  An object both have: the same owner, exactly one real one, the same number of parts on, the same parent, the same
  place at rest, the same physics-lock container. A member of a car: the same on and off on both. An item at rest whose
  centre is in a storage area that can store it and that nobody holds: held by a container (on both machines).
  Nothing refused (part-off), no exceptions.
Scripted runs of what the reports did, then a random monkey (--seed, --steps).
  tools/itemchaos.py [--no-build] [--relay off|iad] [--seed 1] [--steps 60] [--only NAME[,NAME]] [--no-monkey] [--oldglue]"""
import sys, os, time, math, json, random, shutil, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, join_both, DESKTOP_IP
from roadkit import until

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY = arg("--relay", "off")
if RELAY == "off": RELAY = None
SEED, STEPS = arg("--seed", 1, int), arg("--steps", 60, int)
ONLY = [x for x in arg("--only", "").split(",") if x]
SRC = pathlib.Path(os.environ.get("TLD_PLAYSAVE_DIR", "")).expanduser()
SRC_NAME = os.environ.get("TLD_PLAYSAVE", "autosave1")
SAVE = "itemchaos"
T = "TLDRevamp.Net.Entities"
SETTLE = 5.0        # after a step
PERSIST = 12.0      # a difference must still be there this much later
LOOSE = {"can": 60, "lamp": 66, "sponge": 119, "jerry": 72}     # itemdatabase ids, what gets spawned to play with

KIND = dict(pose=1, seat_in=2, seat_out=3, pickup=4, drop=5, throw=6, inv_switch=7, inv_store=9, inv_unstore=10, detach=16)
HISTORY, FINDINGS = [], []
def note(s):
    HISTORY.append(s); print(f"  [{len(HISTORY):3}] {s}", flush=True)
def finding(tag, detail):
    FINDINGS.append({"tag": tag, "detail": detail, "steps": HISTORY[-8:]})
    print(f"FINDING  {tag}: {detail}", flush=True)

class Player:
    def __init__(self, s, label):
        self.s, self.label = s, label
    def __call__(self, cmd): return self.s(cmd)
    def act(self, kind, net=0, other=0, item=-1, slot=-1, x=0, y=0, z=0, a=0):
        r = self.s(f"repro act {KIND[kind]} {net} {other} {item} {slot} {x} {y} {z} {a} 0 0 0 0 0 0 0")
        note(f"{self.label} {kind} net {net} item {item} slot {slot} -> {str(r)[:70]}")
        return r
    def try_act(self, *a, **k):
        try: return self.act(*a, **k)
        except RuntimeError as ex:
            note(f"{self.label} {a[0]} refused: {str(ex)[:90]}"); return None
    def ents(self): return {e["net"]: e for e in self.s("mp entities 400")["entities"]}
    def me(self): return self.s("mp entities")["me"]
    def members(self, car):
        try: return self.s(f"mp entitems {car}")["items"]
        except RuntimeError: return []
    def lock(self, net):
        try: return self.s(f"mount whynot {net}")
        except RuntimeError: return None
    def held(self):
        try: return self.s("get mainscript.s.player.pickedUp.name").get("value")
        except RuntimeError: return None
    def seated(self):
        try: return str(self.s("get mainscript.s.player.Bsitting").get("value")) == "True"
        except RuntimeError: return False

def qrot(q, v):
    x, y, z, w = q
    t = (2 * (y * v[2] - z * v[1]), 2 * (z * v[0] - x * v[2]), 2 * (x * v[1] - y * v[0]))
    return (v[0] + w * t[0] + (y * t[2] - z * t[1]), v[1] + w * t[1] + (z * t[0] - x * t[2]), v[2] + w * t[2] + (x * t[1] - y * t[0]))

# ---- what must be the same
def differences(h, L, tracked, cars):
    """every difference between the machines now, as {key: text}"""
    eh, el = h.ents(), L.ents()
    out = {}
    for n in set(tracked) | set(cars):
        a, b = eh.get(n), el.get(n)
        if a is None and b is None: continue
        if a is None or b is None:
            near = a or b
            if near.get("dormant") or not near.get("name"): continue    # far away: the game has it streamed out, on one machine
            out[f"exists {n}"] = f"{near['name']} only on the {'host' if a else 'joiner'} at {near['pos']}"
            continue
        if a.get("dormant") or b.get("dormant"): continue
        name = a["name"]
        if a["owner"] != b["owner"]: out[f"owner {n}"] = f"{name}: host says {a['owner']}, joiner says {b['owner']}"
        if a["proxy"] == b["proxy"]: out[f"real {n}"] = f"{name}: copy on both or real on both ({a['proxy']})"
        if a["items"] != b["items"]: out[f"members {n}"] = f"{name}: {a['items']} members on the host, {b['items']} on the joiner"
        if a["attached"] != b["attached"]: out[f"on {n}"] = f"{name}: {a['attached']} attached on the host, {b['attached']} on the joiner"
        if a["parent"] != b["parent"]: out[f"parent {n}"] = f"{name}: parent {a['parent']} on the host, {b['parent']} on the joiner"
        held = a.get("held") or b.get("held")
        if not held and not a.get("stored") and not b.get("stored"):
            d = math.dist(a["pos"], b["pos"])
            moving = max(abs(x) for x in (a.get("vel") or [0])) > 0.3 or max(abs(x) for x in (b.get("vel") or [0])) > 0.3
            if d > 0.6 and not moving: out[f"place {n}"] = f"{name}: {d:.2f} m apart at rest ({a['pos']} / {b['pos']})"
        if n in tracked and not held and not a["car"] and not a.get("stored") and not b.get("stored"):
            la, lb = h.lock(n), L.lock(n)
            if la and lb:
                if la.get("physlock") != lb.get("physlock"):
                    out[f"lock {n}"] = f"{name}: container '{la.get('physlock')}' on the host, '{lb.get('physlock')}' on the joiner (owner {a['owner']})"
                for who, l, e in (("host", la, a), ("joiner", lb, b)):
                    if not l.get("physlock") and not a["attached"] and not l.get("dontMount"):
                        ar = [x["name"] for x in l.get("areas", []) if x["centreInBox"] and x["canStore"] and not x["manual"] and not x["noLock"]]
                        if ar: out[f"never {n} {who}"] = f"{name}: in {ar[0]} on the {who} and not held (owner {a['owner']}, copy {e['proxy']}, in scan {[x['inScan'] for x in l['areas'] if x['centreInBox']][:1]})"
    for car in cars:
        ma, mb = h.members(car), L.members(car)
        for x, y in zip(ma, mb):
            if not x.get("loaded") or not y.get("loaded"): continue
            if x.get("name") != y.get("name"): out[f"member {car}/{x['i']}"] = f"member {x['i']}: {x.get('name')} on the host, {y.get('name')} on the joiner"
            elif x.get("attached") != y.get("attached"): out[f"member-on {car}/{x['i']}"] = f"{x['name']} (member {x['i']}): attached {x['attached']} on the host, {y['attached']} on the joiner"
            elif x.get("phys") != y.get("phys"): out[f"member-lock {car}/{x['i']}"] = f"{x['name']} (member {x['i']}): container '{x.get('phys')}' on the host, '{y.get('phys')}' on the joiner"
    return out

_counts = {}
def check(h, L, tracked, cars, tag, settle=SETTLE):
    time.sleep(settle)
    first = differences(h, L, tracked, cars)
    if first:
        time.sleep(PERSIST)
        again = differences(h, L, tracked, cars)
        for k, v in again.items():
            if k in first: finding(tag + " / " + k.split()[0], v)
    for who, s in (("host", h), ("joiner", L)):
        e = s("errors")
        n = e.get("errors", 0) + e.get("exceptions", 0)
        if n > _counts.get(who, 0):
            finding(tag + " / errors", f"{who}: {n - _counts.get(who, 0)} new: {str(e.get('kinds'))[:300]}")
            _counts[who] = n
    for who, s in (("host", h),):
        r = s(f"get {T}.PartOffRejected").get("value", 0)
        if r > _counts.get("partoff", 0):
            finding(tag + " / part-off", f"{r - _counts.get('partoff', 0)} refused: {s(f'get {T}.PartRejects').get('value')}")
            _counts["partoff"] = r

# ---- the world
def spawn(p, what):
    before = set(p.ents()); me = p.me()
    p(f"devspawn {LOOSE.get(what, what)}")
    got = []
    until(lambda: got.extend(n for n, e in p.ents().items() if n not in before and e["owner"] == me and not e["car"]) or got, 10)
    if not got: raise RuntimeError(f"{what} never appeared")
    note(f"{p.label} spawned {what}: net {got[0]}")
    return got[0]

def glued(p, car):
    return [m for m in p.members(car) if m.get("loaded") and m.get("attached") and not m.get("slot")]
def bolted(p, car):
    return [m for m in p.members(car) if m.get("loaded") and m.get("attached") and m.get("slot")]

def beside(p, car_e, off=(2.6, 0.0, 0.0)):
    v = qrot(car_e["rot"], off)
    p(f"tpground {car_e['pos'][0] + v[0]} {car_e['pos'][2] + v[2]}"); time.sleep(1.5)

def bed(h, car):
    a = h(f"mount areas {car}")["areas"]
    t = next((x for x in a if x["type"] == "trunk"), None) or next((x for x in a if x["type"] == "passengerRoom"), None)
    return t

def car_frame(c, pos):
    q = c["rot"]; inv = (-q[0], -q[1], -q[2], q[3])
    return qrot(inv, [pos[i] - c["pos"][i] for i in range(3)])

def into_car(p, car, net, ents_of):
    """let what the player holds go over the car's bed, as a player does: standing beside the bed, facing it, the thing held
    out over it (seated: inside the cab, in front of the seat)"""
    if not p.seated():
        t = bed(p, car); c = p.ents()[car]
        if t is None: note(f"{car}: no bed"); return
        v = qrot(c["rot"], (t["local"][0] + 1.25, 0, t["local"][2])); r = qrot(c["rot"], (1, 0, 0))
        p(f"tpground {c['pos'][0] + v[0]} {c['pos'][2] + v[2]}"); time.sleep(0.5)
        g = p("gpos")
        p.act("pose", x=g["gx"], y=g["gy"], z=g["gz"], a=math.degrees(math.atan2(-r[0], -r[2])))
        time.sleep(0.5)
    try: note(f"{p.label} holds {net} out: {str(p(f'mp holdnet {net}'))[:50]}")
    except RuntimeError as ex: note(f"{p.label} holdnet {net}: {str(ex)[:80]}")
    time.sleep(0.8)
    es = p.ents(); c = es[car]; e = es.get(net)
    if e: note(f"  {net} in the car's frame {[round(x, 2) for x in car_frame(c, e['pos'])]}")
    p.try_act("drop", net=net, item=0)
    if "--trace" in sys.argv:
        for w in (0.2, 0.4, 0.8, 1.5, 3):
            time.sleep(w); es = p.ents(); e2 = es.get(net)
            if e2: print(f"     trace +{w}: in the car's frame {[round(x, 2) for x in car_frame(es[car], e2['pos'])]}, owner {e2['owner']}, copy {e2['proxy']}, the car moved {math.dist(es[car]['pos'], c['pos']):.2f} m", flush=True)
    time.sleep(1.2)

def take_car(p, car):
    for _ in range(4):
        p(f"mp sitdriver {car}"); time.sleep(2)
        if p.seated(): break
    time.sleep(2)
    try: p("driveout")
    except RuntimeError: pass
    note(f"{p.label} sat in the driver's seat of {car} and got out"); time.sleep(3)

# ---- scripted: what the reports did
def run_glue_off(h, L, A, car, tracked, cars, tag, drop_in):
    """A takes a glued thing off car `car` the way a player does: detach, in the hand, let go. Report 191428: the same
    member could be taken off four times in a row: it was still on, for the owner and for the game."""
    g = glued(A, car)
    if not g: note(f"{tag}: nothing glued on car {car}"); return None
    m = g[0]; i = m["i"]; name = m["name"]
    e = A.ents()[car]; beside(A, e); time.sleep(1)
    known = set(A.ents())
    A.try_act("detach", net=car, item=i)
    A.try_act("pickup", net=car, item=i)
    time.sleep(1.5)
    held = A.held()
    new = [(n, x["name"]) for n, x in A.ents().items() if n not in known and x.get("name")]
    note(f"{tag}: {A.label} took off {name} (member {i}): in the hand {held}; new objects {new}")
    part = next((n for n, x in new if x == name), None)
    if held is None: finding(tag + " / hand", f"{name}: not in {A.label}'s hand after the detach")
    if part is None: finding(tag + " / split", f"{name}: no object of its own after the detach (still a member of {car})")
    else: tracked.add(part)
    time.sleep(1)
    if drop_in and part: into_car(A, car, part, None)
    else: A.try_act("drop", net=part or car, item=0 if part else i)
    check(h, L, tracked, cars, tag)
    again = next((x for x in A.members(car) if x["i"] == i), None)
    if again and again.get("attached"): finding(tag + " / still on", f"{name}: still attached on {A.label} after it was taken off and let go")
    return part

def run_pick_replace(h, L, A, car, net, tracked, cars, tag, seated=None):
    """A takes an item out of a car and puts it back, as many times as the player tries"""
    if seated is not None: A.try_act("seat_in", net=car, slot=seated); time.sleep(1)
    else: beside(A, A.ents()[car])
    for k in range(3):
        A.try_act("pickup", net=net, item=0); time.sleep(0.7 if k else 1.5)
        into_car(A, car, net, None)
        check(h, L, tracked, cars, f"{tag} round {k + 1}")
        la, lb = h.lock(net), L.lock(net)
        note(f"{tag} round {k + 1}: container host '{la and la.get('physlock')}', joiner '{lb and lb.get('physlock')}', owner {h.ents().get(net, {}).get('owner')}")
        if not seated and la and not la.get("physlock"): finding(tag + " / not in", f"net {net} put in the bed, in no container on the host; areas with its centre: {[x['name'] for x in la.get('areas', []) if x['centreInBox']]}")
    if seated is not None: A.try_act("seat_out", net=car, slot=seated); time.sleep(1)

def scripted(h, L, car, tracked, cars):
    def want(n): return not ONLY or n in ONLY
    hid, lid = h.me(), L.me()
    if want("own-glue"):
        print("\n=== the car is the host's: the host takes a glued thing off", flush=True)
        run_glue_off(h, L, h, car, tracked, cars, "own-glue", False)
    print("\n=== the joiner takes the car", flush=True)
    beside(L, L.ents()[car]); take_car(L, car)
    check(h, L, tracked, cars, "car changes hands")
    if want("other-glue"):
        print("\n=== the joiner's car: the host takes a glued thing off (report 191428)", flush=True)
        for k in range(2):
            run_glue_off(h, L, h, car, tracked, cars, f"other-glue {k + 1}", k == 1)
    if want("owner-glue"):
        print("\n=== the joiner's car: the joiner takes a glued thing off, the host watches", flush=True)
        beside(h, h.ents()[car], (5, 0, 0))
        run_glue_off(h, L, L, car, tracked, cars, "owner-glue", False)
    if want("other-can"):
        print("\n=== the joiner's car: the host puts a can in, takes it out, puts it back (report 211758)", flush=True)
        beside(h, h.ents()[car]); n = spawn(h, "can"); tracked.add(n)
        into_car(h, car, n, None); check(h, L, tracked, cars, "other-can in")
        run_pick_replace(h, L, h, car, n, tracked, cars, "other-can")
    if want("other-can-seat"):
        print("\n=== the same, the host sitting in the joiner's car", flush=True)
        beside(h, h.ents()[car]); n = spawn(h, "lamp"); tracked.add(n)
        into_car(h, car, n, None); check(h, L, tracked, cars, "seated lamp in")
        run_pick_replace(h, L, h, car, n, tracked, cars, "other-lamp-seated", seated=1)
    if want("found"):
        print("\n=== found in the world, never touched: the joiner's item put in the joiner's car by the host", flush=True)
        beside(L, L.ents()[car], (4, 0, 4)); n = spawn(L, "sponge"); tracked.add(n); time.sleep(3)
        beside(h, h.ents()[n], (1.2, 0, 0))
        h.try_act("pickup", net=n, item=0); time.sleep(1); into_car(h, car, n, None)
        check(h, L, tracked, cars, "found item")
    if want("flip"):
        print("\n=== the car changes hands with things in it, and back", flush=True)
        beside(h, h.ents()[car]); take_car(h, car); check(h, L, tracked, cars, "flip to host")
        beside(L, L.ents()[car]); take_car(L, car); check(h, L, tracked, cars, "flip to joiner")
    if want("drive"):
        print("\n=== the joiner drives with the things in the car, the host rides along and takes one out on the way", flush=True)
        beside(L, L.ents()[car]); L(f"mp sitdriver {car}"); time.sleep(2)
        h.try_act("seat_in", net=car, slot=1); time.sleep(1)
        note(f"joiner drives: {str(L('autodrive on 8'))[:60]}"); time.sleep(10)
        check(h, L, tracked, cars, "driving", 0.5)
        c = next((n for n in sorted(tracked) if h.ents().get(n) and not h.ents()[n].get("stored") and not h.ents()[n]["attached"] and h.lock(n) and h.lock(n).get("physlock")), None)
        if c:
            h.try_act("pickup", net=c, item=0); time.sleep(3); h.try_act("drop", net=c, item=0); time.sleep(5)
        g = glued(h, car)
        if g: h.try_act("detach", net=car, item=g[-1]["i"]); time.sleep(4)
        try: L("autodrive off")
        except RuntimeError: pass
        time.sleep(6); h.try_act("seat_out", net=car, slot=1); L("driveout"); time.sleep(3)
        check(h, L, tracked, cars, "after the drive")
    if want("rejoin"):
        print("\n=== the joiner leaves and joins again (the owner of the car goes away)", flush=True)
        L("mp stop"); note("joiner left"); time.sleep(6)
        L(f"mp joinid {h('net me')['steamId']}") if RELAY else L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 120); note("joiner back"); time.sleep(10)
        L("set TLDRevamp.ReproPlayback.Active True"); L("set kaposztaleves.s.settings.god True")
        check(h, L, tracked, cars, "after the rejoin")
    if want("mount"):
        print("\n=== the host glues an item on the joiner's car, takes it off again", flush=True)
        beside(h, h.ents()[car]); n = spawn(h, "sponge"); tracked.add(n)
        h(f"mp holdnet {n}"); h(f"mp lookat {car}"); time.sleep(1)
        h("set MountAnything.Mounting.TestPress True"); time.sleep(4)
        note(f"host glue press: {h('get MountAnything.Mounting.LastMount').get('value')}")
        check(h, L, tracked, cars, "host glued on the joiner's car")

# ---- the monkey
def monkey(h, L, car, tracked, cars, seed, steps):
    rng = random.Random(seed)
    print(f"\n=== the monkey, seed {seed}, {steps} steps", flush=True)
    players = (h, L)
    def loose(p): return [n for n in tracked if n in p.ents() and not p.ents()[n].get("dormant") and not p.ents()[n].get("held")]
    for step in range(steps):
        p = rng.choice(players)
        e = p.ents()
        if car not in e: note("car gone"); break
        heldname = p.held()
        ops = ["pickup", "pickup", "spawn", "walk", "seat", "away", "both", "detach", "drive"] if heldname is None else ["drop", "drop_car", "drop_car", "throw", "store", "glue"]
        if p.seated(): ops = ["seat_out", "pickup", "drop_car", "drop_car"] if heldname else ["seat_out", "pickup", "detach"]
        op = rng.choice(ops)
        try:
            if op == "spawn" and len(tracked) < 14:
                beside(p, e[car], (rng.uniform(1.5, 4), 0, rng.uniform(-3, 3))); tracked.add(spawn(p, rng.choice(list(LOOSE))))
            elif op == "pickup":
                c = loose(p)
                if c:
                    n = rng.choice(c)
                    if not p.seated(): beside(p, e[n], (1.2, 0, 0))
                    p.try_act("pickup", net=n, item=0)
            elif op == "drop" and heldname: p("dropheld"); note(f"{p.label} let go")
            elif op == "drop_car" and heldname:
                ms = [n for n in tracked if e.get(n, {}).get("held")]
                if ms: into_car(p, car, ms[0], None)
            elif op == "throw" and heldname:
                g = p("gpos"); note(f"{p.label} throws: {str(p(f'mp throwpoint {e[car]['pos'][0]} {e[car]['pos'][2]} 0.8'))[:60]}")
            elif op == "store" and heldname: p.try_act("inv_store", slot=rng.randrange(0, 3))
            elif op == "glue" and heldname:
                p(f"mp lookat {car}"); time.sleep(0.5); p("set MountAnything.Mounting.TestPress True"); note(f"{p.label} glues: {p('get MountAnything.Mounting.LastMount').get('value')}")
            elif op == "walk":
                c = loose(p)
                if c:
                    n = rng.choice(c); beside(p, e[n], (2.5, 0, 0)); note(f"{p.label} walks into {n}: {p(f'mp walkat {n} 3')}"); time.sleep(3.5)
            elif op == "seat":
                p.try_act("seat_in", net=car, slot=rng.choice([0, 1, 4]))
            elif op == "seat_out": p.try_act("seat_out", net=car, slot=0)
            elif op == "away":
                g = p("gpos"); note(f"{p.label} walks 700 m away and back"); p(f"tpground {g['gx'] + 700} {g['gz']}"); time.sleep(15); beside(p, p.ents()[car] if car in p.ents() else e[car], (4, 0, 0))
            elif op == "both":
                c = [n for n in loose(h) if n in loose(L)]
                if c:
                    n = rng.choice(c); beside(h, h.ents()[n], (1.2, 0, 0)); beside(L, L.ents()[n], (-1.2, 0, 0))
                    h.try_act("pickup", net=n, item=0); L.try_act("pickup", net=n, item=0)
                    note(f"both grabbed {n}: host holds {h.held()}, joiner holds {L.held()}")
                    if h.held() and L.held(): finding("both hold", f"net {n}: in both players' hands")
            elif op == "drive":
                beside(p, e[car]); p(f"mp sitdriver {car}"); time.sleep(2); note(f"{p.label} drives: {str(p('autodrive on 6'))[:50]}"); time.sleep(6)
                try: p("autodrive off")
                except RuntimeError: pass
                time.sleep(3); p("driveout"); note(f"{p.label} parked and got out")
            elif op == "detach":
                m = glued(p, car) or bolted(p, car)
                if m:
                    x = rng.choice(m); 
                    if not p.seated(): beside(p, e[car])
                    known = set(e)
                    p.try_act("detach", net=car, item=x["i"]); time.sleep(0.5)
                    for n, y in p.ents().items():
                        if n not in known and not y["car"] and y.get("name"): tracked.add(n)
        except RuntimeError as ex:
            note(f"{p.label} {op}: {str(ex)[:120]}")
        check(h, L, tracked, cars, f"monkey {step + 1}", 4.0)

# ---- run
if not SRC.is_dir() or not list(SRC.glob(f"TLDsave*_{SRC_NAME}.*")): sys.exit(f"no TLDsave*_{SRC_NAME}.* in TLD_PLAYSAVE_DIR ({SRC})")
Lag = Agent()
ensure_both("--no-build" not in sys.argv)
copied = []
with game.ProtectedSession():
    try:
        for f in SRC.glob(f"TLDsave*_{SRC_NAME}.*"):
            dst = game.SAVES / f.name.replace(f"_{SRC_NAME}.", f"_{SAVE}.")
            shutil.copy2(f, dst); copied.append(dst)
        Lag("!seeded")
        hb = game.loadsave(SAVE)
        for s_ in (hb, Lag):
            s_("set TLDRevamp.TestSafety.BlockAutoSave True"); s_("bg on"); s_("set UnityEngine.QualitySettings.vSyncCount 0"); s_("set kaposztaleves.s.settings.god True")
        if RELAY: print("relay:", hb(f"net relay {RELAY}"), Lag(f"net relay {RELAY}"), flush=True)
        if not join_both(hb, Lag): sys.exit("JOINER NEVER JOINED")
        h, L = Player(hb, "host"), Player(Lag, "joiner")
        for s_ in (h, L):
            s_(f"set TLDRevamp.ReproPlayback.Active True"); s_("set TLDRevamp.TestSafety.BlockAutoSave True")
            s_("set UnityEngine.Application.runInBackground True")
        if "--oldglue" in sys.argv: h(f"set {T}.GluedPartOff False")
        time.sleep(8)
        g = h("gpos")
        cs = sorted((math.hypot(e["pos"][0] - g["gx"], e["pos"][2] - g["gz"]), n) for n, e in h.ents().items() if e["car"] and not e.get("dormant"))
        if not cs: sys.exit("no car near the host")
        car = cs[0][1]
        ce = h.ents()[car]
        print(f"the car: net {car} {ce['name']}, owner {ce['owner']}, {len(glued(h, car))} glued, {len(bolted(h, car))} bolted: {[m['name'] for m in glued(h, car)][:10]}", flush=True)
        beside(h, ce); L(f"tpground {ce['pos'][0] - 5} {ce['pos'][2]}"); time.sleep(10)
        tracked, cars = set(), {car}
        check(h, L, tracked, cars, "as they join")
        scripted(h, L, car, tracked, cars)
        if "--no-monkey" not in sys.argv: monkey(h, L, car, tracked, cars, SEED, STEPS)
        L("mp stop"); h("mp stop")
    finally:
        for f in copied:
            try: f.unlink()
            except OSError: pass
        Lag.close()
print("\nFINDINGS", len(FINDINGS))
seen = {}
for f in FINDINGS:
    k = f["tag"].split(" / ")[-1] + " " + f["detail"].split(":")[0]
    seen.setdefault(k, 0); seen[k] += 1
    print(f"  {f['tag']}: {f['detail']}")
pathlib.Path(os.environ.get("TLD_CHAOS_OUT", "/dev/null")).write_text(json.dumps({"findings": FINDINGS, "history": HISTORY}, indent=1))

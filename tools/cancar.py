#!/usr/bin/env python3
"""A small item put into a car the OTHER player owns (playtest 2026-10-29: "small oil can in the pickup does not
physlock"; its owner kept changing between the two players). The host loads a world save
(TLD_PLAYSAVE_DIR holds TLDsave*_<name>.*, TLD_PLAYSAVE the name, default autosave1) and hosts it; the joiner joins.
  A  CONTROL : the car is the host's: the host puts a can onto the car's spare rim (or where the car's rim is) —
               it must end up held by a container on both machines
  B  JOINER'S CAR : the joiner sits in the car (it becomes the joiner's, and stays so when the joiner gets out), the
               host puts a can in the same place — held by a container on both machines, and the same owner
  C  WALKING PAST : the host walks into the can that sits held in the joiner's car (playtest 2026-10-29: the walker's
               body claim took it, the car's owner took it back, over and over) — the can keeps its owner and stays
               held on both (it passes before and after: the claim path it was meant for is not reached this way)
  D  NESTED (--nest): a spare rim in the trunk, a can put at the rim — held on both (it lands in the trunk's container;
               a rim has no container of its own, so the playtest's can-in-a-rim state is not reproduced)
Each step looks at the can ten seconds later on both: owner, copy or not, the container that holds it.
  tools/cancar.py [--no-build] [--relay iad|off] [--car-prefab Car06P] [--item 60] [--edge m] [--nest] [--look=<name prefix>]"""
import sys, os, time, math, shutil, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, join_both
from roadkit import until

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY = arg("--relay", "off")
if RELAY == "off": RELAY = None
ITEM = arg("--item", 60, int)           # OilCan
CAR = arg("--car-prefab", "Car06P")
NEST = "--nest" in sys.argv
EDGE = arg("--edge", 0.0, float)        # B: the can goes this far inside the bed's tail end (0 = its middle); C then walks in from behind the car
SRC = pathlib.Path(os.environ.get("TLD_PLAYSAVE_DIR", "")).expanduser()
SRC_NAME = os.environ.get("TLD_PLAYSAVE", "autosave1")
SAVE = "cancar"

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def ents(s): return {e["net"]: e for e in s("mp entities 400")["entities"]}

def qrot(q, v):
    """rotate v by the unit quaternion q = (x, y, z, w)"""
    x, y, z, w = q
    t = (2 * (y * v[2] - z * v[1]), 2 * (z * v[0] - x * v[2]), 2 * (x * v[1] - y * v[0]))
    return (v[0] + w * t[0] + (y * t[2] - z * t[1]), v[1] + w * t[1] + (z * t[0] - x * t[2]), v[2] + w * t[2] + (x * t[1] - y * t[0]))

def local_of(car, p):
    q = car["rot"]; inv = (-q[0], -q[1], -q[2], q[3])
    return qrot(inv, (p[0] - car["pos"][0], p[1] - car["pos"][1], p[2] - car["pos"][2]))

def view(s, n):
    e = ents(s).get(n)
    if not e: return None
    try: w = s(f"mount whynot {n}")
    except RuntimeError: w = {}
    return {"owner": e["owner"], "copy": e["proxy"], "parent": e["parent"], "container": w.get("physlock", "?")}

def put_can(h, L, car_net, tag):
    es = ents(h); car = es[car_net]
    areas = h(f"mount areas {car_net}")["areas"]
    bed = next((x for x in areas if x["type"] == "trunk"), None) or next((x for x in areas if x["type"] == "passengerRoom"), None)
    if not bed: print(f"  {tag}: the car has no trunk or passenger room", flush=True); return None
    loc = bed["local"]
    spot = (loc[0], loc[1] + 0.1, loc[2])
    if EDGE and tag == "B": spot = (loc[0], loc[1] + 0.1, loc[2] - bed["size"][2] / 2 + EDGE)   # at the tail end of the bed
    print(f"  {tag}: the can goes into the {bed['type']} ({bed['name']}) at {tuple(round(x, 2) for x in spot)} (car frame), box {bed['size']}", flush=True)
    me = h("mp entities")["me"]; before = set(es)
    h(f"devspawn {ITEM}")
    got = []
    until(lambda: got.extend(n for n, e in ents(h).items() if n not in before and e["owner"] == me and not e["car"]) or got, 10)
    if not got: return None
    n = got[0]
    print(f"  {tag}: can net {n}:", str(h(f"mp putrel {car_net} {n} {spot[0]} {spot[1]} {spot[2]}"))[:100], flush=True)
    return n

def put_item(h, car_net, item, tag, edge):
    es = ents(h)
    areas = h(f"mount areas {car_net}")["areas"]
    bed = next((x for x in areas if x["type"] == "trunk"), None)
    if not bed: return None
    loc = bed["local"]; spot = (loc[0], loc[1] + 0.1, loc[2])
    me = h("mp entities")["me"]; before = set(es)
    h(f"devspawn {item}")
    got = []
    until(lambda: got.extend(n for n, e in ents(h).items() if n not in before and e["owner"] == me and not e["car"]) or got, 10)
    if not got: return None
    print(f"  {tag}: net {got[0]}:", str(h(f"mp putrel {car_net} {got[0]} {spot[0]} {spot[1]} {spot[2]}"))[:100], flush=True)
    return got[0]

def judge(tag, h, L, n, wait=12):
    time.sleep(wait)
    a, b = view(h, n), view(L, n)
    print(f"   {tag}: host {a} | joiner {b}", flush=True)
    held = lambda v: v is not None and v["container"] not in ("", "?")
    verdict(f"{tag}: HELD BY A CONTAINER ON THE HOST", held(a), str(a))
    verdict(f"{tag}: HELD BY A CONTAINER ON THE JOINER", held(b), str(b))
    verdict(f"{tag}: SAME OWNER ON BOTH", a is not None and b is not None and a["owner"] == b["owner"], f"{a and a['owner']} / {b and b['owner']}")

if not SRC.is_dir() or not list(SRC.glob(f"TLDsave*_{SRC_NAME}.*")): sys.exit(f"no TLDsave*_{SRC_NAME}.* in TLD_PLAYSAVE_DIR ({SRC})")
L = Agent()
ensure_both("--no-build" not in sys.argv)
copied = []
with game.ProtectedSession():
    try:
        for f in SRC.glob(f"TLDsave*_{SRC_NAME}.*"):
            dst = game.SAVES / f.name.replace(f"_{SRC_NAME}.", f"_{SAVE}.")
            shutil.copy2(f, dst); copied.append(dst)
        L("!seeded")
        h = game.loadsave(SAVE)
        for s_ in (h, L):
            s_("set TLDRevamp.TestSafety.BlockAutoSave True"); s_("bg on"); s_("set UnityEngine.QualitySettings.vSyncCount 0")
        if RELAY: print("relay:", h(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
        if not join_both(h, L): sys.exit("JOINER NEVER JOINED")
        time.sleep(8)
        me = h("gpos")
        cars = [(math.hypot(e["pos"][0] - me["gx"], e["pos"][2] - me["gz"]), n) for n, e in ents(h).items() if e["car"] and e["name"].startswith(CAR) and not e.get("dormant")]
        if not cars: sys.exit(f"no {CAR} near the host")
        car_net = min(cars)[1]
        c = ents(h)[car_net]
        print(f"  the car: net {car_net}, owner {c['owner']}, at {c['pos']}", flush=True)
        L(f"tpground {c['pos'][0] + 4} {c['pos'][2]}"); time.sleep(6)
        h("handbrake on") if False else None

        print("\n=== A CONTROL: the host's own car", flush=True)
        n = put_can(h, L, car_net, "A")
        if n: judge("A", h, L, n)

        print("\n=== B THE JOINER'S CAR", flush=True)
        for _ in range(4):
            L(f"mp sitdriver {car_net}"); time.sleep(2)
            if str(L("get mainscript.s.player.Bsitting").get("value")) == "True": break
        time.sleep(3)
        L("driveout"); time.sleep(3)
        print(f"  car owner now: host sees {ents(h)[car_net]['owner']}, joiner sees {ents(L)[car_net]['owner']}", flush=True)
        c = ents(h)[car_net]; h(f"tpground {c['pos'][0] + 3} {c['pos'][2]}"); time.sleep(5)   # the host beside the car as it is now
        n = put_can(h, L, car_net, "B")
        if n: judge("B", h, L, n)

        for want in [x[7:] for x in sys.argv if x.startswith("--look=")]:
            for k, e in sorted(ents(h).items()):
                if not e["name"].startswith(want): continue
                print(f"   look {want} {k}: host {view(h, k)} | joiner {view(L, k)}", flush=True)
                for who, s_ in (("host", h), ("joiner", L)):
                    try: print(f"     {who} lockdiag:", str(s_(f"mp lockdiag {k}"))[:600], flush=True)
                    except RuntimeError as ex: print(f"     {who} lockdiag: {str(ex)[:120]}", flush=True)

        if n:
            print("\n=== C THE HOST WALKS INTO THE CAN", flush=True)
            h("set kaposztaleves.s.settings.god True")
            before = view(h, n); log0 = len(h(f"mp ownerlog {n}")["log"])
            for k in range(3):
                c = ents(h)[car_net]; cn = ents(h)[n]
                if EDGE:
                    bx = qrot(c["rot"], (0.0, 0.0, -4.5)); h(f"tpground {c['pos'][0] + bx[0]} {c['pos'][2] + bx[2]}")
                else: h(f"tpground {c['pos'][0] + 4} {cn['pos'][2]}")
                time.sleep(2)
                print(f"  walk {k + 1}:", h(f"mp walkat {n} 4"), flush=True); time.sleep(6)
            time.sleep(5)
            a, b = view(h, n), view(L, n)
            changes = h(f"mp ownerlog {n}")["log"][log0:]
            print(f"   C: host {a} | joiner {b}; owner log since: {changes}", flush=True)
            verdict("C: THE OWNER DID NOT CHANGE", a is not None and before is not None and a["owner"] == before["owner"] and not any("BodyNear" in x or "BodyContact" in x for x in changes), str(changes[-4:]))
            verdict("C: STILL HELD BY A CONTAINER ON BOTH", all(v is not None and v["container"] not in ("", "?") for v in (a, b)), f"{a} / {b}")

        if NEST:
            print("\n=== D A CAN IN A RIM IN THE JOINER'S CAR (the playtest 2026-10-29 can lay in a spare rim: host's copy never held it)", flush=True)
            rim = put_item(h, car_net, 43, "D rim", 0.0)
            if rim:
                time.sleep(8)
                a, b = view(h, rim), view(L, rim)
                print(f"   D rim: host {a} | joiner {b}", flush=True)
                try: ra = h(f"mount areas {rim}"); print("   D rim areas on the host:", str(ra)[:900], flush=True)
                except RuntimeError as ex: print("   D rim areas:", str(ex)[:200], flush=True)
                for k in range(3):
                    es = ents(h); r = es.get(rim)
                    if not r: break
                    before = set(es); me = h("mp entities")["me"]
                    h(f"devspawn {ITEM}")
                    got = []
                    until(lambda: got.extend(n for n, e in ents(h).items() if n not in before and e["owner"] == me and not e["car"]) or got, 10)
                    if not got: break
                    can = got[0]
                    print(f"  D: can net {can} into the rim {rim}:", str(h(f"mp putrel {rim} {can} 0 0.12 0"))[:100], flush=True)
                    judge(f"D{k + 1}", h, L, can, 14)
                    if view(h, can) and view(h, can)["container"] not in ("", "?"): break
        L("mp stop"); h("mp stop")
    finally:
        for f in copied:
            try: f.unlink()
            except OSError: pass
        L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")

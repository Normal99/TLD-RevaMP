#!/usr/bin/env python3
"""A small item put into a car the OTHER player owns (playtest 2026-10-29: "small oil can in the pickup does not
physlock"; the host's copy of the friend's car refused it, 275 refusals in a report). The host loads a world save
(TLD_PLAYSAVE_DIR holds TLDsave*_<name>.*, TLD_PLAYSAVE the name, default autosave1) and hosts it; the joiner joins.
  A  CONTROL : the car is the host's: the host puts a can onto the car's spare rim (or where the car's rim is) —
               it must end up held by a container on both machines
  B  JOINER'S CAR : the joiner sits in the car (it becomes the joiner's, and stays so when the joiner gets out), the
               host puts a can in the same place — held by a container on both machines, and the same owner
Each step looks at the can ten seconds later on both: owner, copy or not, the container that holds it.
  tools/cancar.py [--no-build] [--relay iad|off] [--car-prefab Car06P] [--item 60]"""
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
    print(f"  {tag}: the can goes into the {bed['type']} ({bed['name']}) at {tuple(round(x, 2) for x in spot)} (car frame), box {bed['size']}", flush=True)
    me = h("mp entities")["me"]; before = set(es)
    h(f"devspawn {ITEM}")
    got = []
    until(lambda: got.extend(n for n, e in ents(h).items() if n not in before and e["owner"] == me and not e["car"]) or got, 10)
    if not got: return None
    n = got[0]
    print(f"  {tag}: can net {n}:", str(h(f"mp putrel {car_net} {n} {spot[0]} {spot[1]} {spot[2]}"))[:100], flush=True)
    return n

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

        L("mp stop"); h("mp stop")
    finally:
        for f in copied:
            try: f.unlink()
            except OSError: pass
        L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")

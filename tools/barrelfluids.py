#!/usr/bin/env python3
"""A barrel's fluids on two machines (the shipwreck barrels: "the fluids in barrels do not sync", playtest 2026-10-28).
Host and joiner in one seeded world; a barrel (item 70) spawned on the host, then looked at on both machines after
  1 SPAWN     : as the game spawns it (random type preset)
  2 FILL      : the owner fills it (gas 30)
  3 JOINER    : the joiner picks it up and puts it down (the owner changes), then the host does the same
  4 STREAM    : the joiner walks 700 m away and back (its copy goes out of range and is placed again from a record)
  5 REJOIN    : the joiner leaves and joins again (every object arrives from a record)
Each step: `mp fluids` of the barrel on both machines must show the same fluids to 0.05 L.
  tools/barrelfluids.py [--no-build] [--relay iad|off]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP
from roadkit import until, g

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY = arg("--relay", "iad")
if RELAY == "off": RELAY = None
BARREL = 70

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def tanks(s, net):
    try: r = s(f"mp fluids {net}")
    except RuntimeError: return None   # no such object here: out of range
    if "error" in r: return None
    return [(t["ti"], t.get("by", ""), t.get("amount")) for t in r["tanks"]]

def same(a, b):
    if a is None or b is None or len(a) != len(b): return False
    for x, y in zip(a, b):
        if x[2] is None or y[2] is None or abs(x[2] - y[2]) > 0.05: return False
        if sorted(x[1].split()) != sorted(y[1].split()): return False   # the same fluid types (and amounts to 3 digits)
    return True

def look(tag, h, L, net, wait=6):
    time.sleep(wait)
    a, c = tanks(h, net), tanks(L, net)
    ok = same(a, c)
    eh, el = h(f"mp entities 400"), L(f"mp entities 400")
    own = lambda r: next((f"owner {e['owner']} proxy {e['proxy']}" for e in r["entities"] if e["net"] == net), "absent")
    verdict(f"{tag}: BOTH SEE THE SAME BARREL FLUIDS", ok, f"host [{own(eh)}] {a} | joiner [{own(el)}] {c}")
    return ok

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
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("JOINER NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        time.sleep(4)
        rp = b("roadpoint"); cx, cz = rp["gx"], rp["gz"]
        b(f"tpground {cx} {cz}"); L(f"tpground {cx + 6} {cz}"); time.sleep(3)
        me = b("mp entities")["me"]
        before = {e["net"] for e in b("mp entities 400")["entities"]}
        print("spawn:", b(f"devspawn {BARREL}"), flush=True)
        got = []
        until(lambda: got.extend(e["net"] for e in b("mp entities 400")["entities"] if e["net"] not in before and e["owner"] == me) or got, 10)
        if not got: sys.exit("no barrel appeared")
        net = got[0]
        print("barrel net", net, flush=True)
        if not until(lambda: tanks(L, net) is not None, 15): print("  the joiner has no copy yet", flush=True)
        look("1 SPAWN", b, L, net)

        print("fill:", b(f"mp fill {net} 0 gas 30"), flush=True)
        look("2 FILL", b, L, net)

        # 3: the joiner picks it up and puts it down, then the host
        for who, name in ((L, "joiner"), (b, "host")):
            ent = next(e for e in who("mp entities 400")["entities"] if e["net"] == net)
            gx, gz = ent["pos"][0], ent["pos"][2]
            who(f"tpground {gx + 1.5} {gz}"); time.sleep(2)
            print(f"  {name} picks up:", who("picknearest 4"), flush=True); time.sleep(3)
            print(f"  {name} lets go:", who("dropheld"), flush=True)
            look(f"3 {name.upper()} CARRIED IT", b, L, net, 8)

        # 4: the joiner leaves the range and comes back
        ent = next(e for e in L("mp entities 400")["entities"] if e["net"] == net)
        gx, gz = ent["pos"][0], ent["pos"][2]
        L(f"tpground {gx + 700} {gz}"); time.sleep(20)
        print("  the joiner is away; its copy:", tanks(L, net), flush=True)
        L(f"tpground {gx + 3} {gz}"); time.sleep(3)
        look("4 STREAM OUT AND BACK", b, L, net, 10)

        # 5: the joiner leaves and joins again
        L("mp stop"); time.sleep(3)
        if RELAY: L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("JOINER NEVER JOINED AGAIN")
        time.sleep(6)
        L(f"tpground {gx + 3} {gz}"); time.sleep(3)
        look("5 REJOIN", b, L, net, 12)
        L("mp stop"); b("mp stop")
        if RELAY: b("net relay off"); L("net relay off")
finally:
    L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")

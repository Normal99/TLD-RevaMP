#!/usr/bin/env python3
"""I4: a bullet stops in a player's body (two machines, relay iad by default). The host's car is parked; the laptop's
player stands 1.2 m in front of its nose; the host shoots the laptop's player from 9 m out, the car right behind them.
  A  the shot hits the laptop's player, and NO new hole appears on the car behind (on either machine)
  B  control: the laptop steps aside, the host fires at the same spot — now the car gets a hole (the line does reach it)
  tools/shotstop.py [--no-build] [--relay iad|off]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
MAGNUM = 64
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def holes(x, net):
    return len(x(f"mp decals {net}")["decals"])

L = Agent()
ensure_both("--no-build" not in sys.argv)
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
        rp = h("roadpoint"); h(f"tpground {rp['gx']} {rp['gz']}"); time.sleep(3)
        before = {e["net"] for e in h("mp entities")["entities"]}
        print("car:", h("devspawn Car01Full"), flush=True)
        if not until(lambda: any(e["car"] and e["net"] not in before for e in h("mp entities")["entities"]), 20): sys.exit("NO CAR")
        net = next(e["net"] for e in h("mp entities")["entities"] if e["car"] and e["net"] not in before)
        time.sleep(3)
        print("  laptop in front of the nose:", L(f"mount front {net} 1.2"), flush=True)
        print("  host 9 m out:", h(f"mount front {net} 9"), flush=True); time.sleep(3)
        print("  magnum:", h(f"devspawn {MAGNUM}"), flush=True); time.sleep(2)
        if not until(lambda: "decals" in L(f"mp decals {net}"), 15): sys.exit("the laptop has no copy of the car")
        h0, l0 = holes(h, net), holes(L, net)
        lp = L("gpos")
        print(f"  laptop's player at {lp}; car holes before: host {h0} laptop {l0}", flush=True)
        r = h("mp shootat 1 1.0"); time.sleep(3)
        h1, l1 = holes(h, net), holes(L, net)
        print(f"  A shot at the laptop's player: {r}; car holes after: host {h1} laptop {l1}", flush=True)
        verdict("A THE SHOT HITS THE PLAYER", r.get("hits") == 1, f"hits {r.get('hits')}")
        verdict("A NO HOLE IN THE CAR BEHIND THEM (BOTH MACHINES)", h1 == h0 and l1 == l0, f"host {h0}->{h1}, laptop {l0}->{l1}")
        # B: the same line with nobody on it
        L(f"tpground {lp['gx'] + 6} {lp['gz'] + 6}"); time.sleep(3)
        gy = lp.get("gy", lp.get("y"))
        r2 = h(f"mp shootpoint {lp['gx']} {gy + 0.0} {lp['gz']}"); time.sleep(3)
        h2, l2 = holes(h, net), holes(L, net)
        print(f"  B the same spot, nobody there: {r2}; car holes: host {h1}->{h2} laptop {l1}->{l2}", flush=True)
        verdict("B CONTROL: WITHOUT THE PLAYER THE SAME SHOT HOLES THE CAR", h2 > h1, f"host {h1}->{h2}, laptop {l1}->{l2}")
    finally:
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

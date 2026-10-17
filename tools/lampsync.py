#!/usr/bin/env python3
"""Hand lamps seen by the other player (two machines, relay iad by default). For each lamp (the old pocket lamp
regizseblampa, ujlamp): the laptop spawns it, picks it up and switches it through its states (usablescript.Turn, the
switch a player clicks); the host's copy must show the same switch state and the same Lights shining.
  tools/lampsync.py [--no-build] [--relay iad|off] [--items 320,66]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
ITEMS = [int(x) for x in (sys.argv[sys.argv.index("--items") + 1] if "--items" in sys.argv else "320,66").split(",")]
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    try:
        rp = h("roadpoint"); x, z = rp["gx"], rp["gz"]
        h(f"tpground {x} {z}"); L(f"tpground {x + 4} {z}"); time.sleep(3)
        for item in ITEMS:
            lb = {e["net"] for e in L("mp entities")["entities"]}
            print(f"item {item}:", L(f"devspawn {item}"), flush=True)
            if not until(lambda: any(e["net"] not in lb and not e["car"] for e in L("mp entities")["entities"]), 20): verdict(f"ITEM {item} SPAWNED", False); continue
            net = next(e["net"] for e in L("mp entities")["entities"] if e["net"] not in lb and not e["car"])
            until(lambda: any(e["net"] == net for e in h("mp entities")["entities"]), 20); time.sleep(1)
            print("  picks it up:", L(f"mp pickupnet {net}"), flush=True); time.sleep(2)
            print("  before: laptop", L(f"mp lamp {net}"), "| host", h(f"mp lamp {net}"), flush=True)
            for step in range(3):
                a = L(f"mp turn {net}"); time.sleep(2.5)
                a = L(f"mp lamp {net}"); b = h(f"mp lamp {net}")
                same = a["switch"] == b["switch"] and a["states"] == b["states"] and a["lit"][0] == b["lit"][0]
                print(f"  turn {step + 1}: laptop {a} | host {b}", flush=True)
                if step == 0: h(f"mp lookat {net}"); h(f"shot lamp{item}")
                verdict(f"ITEM {item} SWITCH AT {a['switch']}: THE HOST SEES THE SAME", same, "")
            L("dropheld"); time.sleep(1)
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

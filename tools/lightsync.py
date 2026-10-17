#!/usr/bin/env python3
"""Headlights seen by the other player (two machines, relay iad by default). The laptop's player sits in their car,
ignition on, and turns the light switch through its states (the game's own InteractSwitchLights: the dashboard knob,
a usable). On the host the copy must show the same: the switch state, and the same headlamp Lights shining.
  tools/lightsync.py [--no-build] [--relay iad|off]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g, car_ready

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
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
    gods = {}
    try:
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
        rp = h("roadpoint"); yaw = rp["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        if not car_ready(L, rp["gx"] + R[0] * 1.8, rp["gz"] + R[1] * 1.8, yaw, "laptop"): sys.exit("LAPTOP NOT IN ITS CAR")
        me = L("mp players")["me"]
        net = min((e for e in L("mp entities")["entities"] if e["part"] < 0 and e["car"]), key=lambda e: math.dist(e["pos"][::2], me[::2]))["net"]
        until(lambda: any(e["net"] == net for e in h("mp entities")["entities"]), 20)
        h(f"mount front {net} 7"); time.sleep(2); h(f"mp lookat {net}")
        print("ignition:", L(f"mp carset {net} ignition 1"), flush=True); time.sleep(2)
        def sig(x): return x(f"mp carsig {net}")
        print("before: laptop", {k: sig(L).get(k) for k in ("lightSwitch", "headlampStates", "lampLights")}, "| host", {k: sig(h).get(k) for k in ("lightSwitch", "headlampStates", "lampLights")}, flush=True)
        for step in range(3):
            L("call mainscript.s.player.Car.InteractSwitchLights"); time.sleep(2.5)
            a, b = sig(L), sig(h)
            ka = {k: a.get(k) for k in ("lightSwitch", "headlampStates", "lampLights")}; kb = {k: b.get(k) for k in ("lightSwitch", "headlampStates", "lampLights")}
            print(f"  switch turned ({step + 1}): laptop {ka} | host's copy {kb}", flush=True)
            h(f"mp lookat {net}"); h(f"shot lights{ka['lightSwitch']}")
            verdict(f"LIGHT SWITCH AT {ka['lightSwitch']}: THE HOST SEES THE SAME", ka == kb, "")
        er = [x("errors") for x in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"]) for e in er]))
    finally:
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

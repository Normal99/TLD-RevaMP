#!/usr/bin/env python3
"""Car doors, hood and trunk seen by the other player (two machines, relay iad by default). The host's start car,
parked. A: the laptop (not the owner) opens each door the player's way (its usable: a click or a drag) — the host's
car must show it open. B: the host (the owner) closes each again — the laptop's copy must show it closed. Doors
compared ~3 s after each action (the door's pose on the car, both machines); screenshots of the car from both sides.
  tools/cardoors.py [--no-build] [--relay iad|off]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until

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
    try:
        me = h("mp players")["me"]
        car = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"] and "Bike" not in e.get("name", "")), key=lambda e: math.dist(e["pos"][::2], me[::2]))
        net = car["net"]; print("car:", car["name"] if "name" in car else net, car["pos"], flush=True)
        until(lambda: any(e["net"] == net for e in L("mp entities")["entities"]), 30)
        # both right by the car, on its floor (the start car stands in the garage: from outside the shots were walls)
        cy = car["pos"][1] + 1.0
        L(f"tpground {car['pos'][0] + 2.6} {car['pos'][2]} {cy}"); h(f"tpground {car['pos'][0] - 2.6} {car['pos'][2]} {cy}"); time.sleep(3)
        d0h, d0l = h(f"mp cardoors {net}"), L(f"mp cardoors {net}")
        n = len(d0h["doors"])
        idx = [i for i, d in enumerate(d0h["doors"]) if any(k in d["name"].lower() for k in ("door", "hood", "trunk"))]
        print(f"doors, hood, trunk: {[d0h['doors'][i]['name'] for i in idx]} (of {n} hinged parts)", flush=True)
        verdict("THE CAR HAS DOORS ON BOTH", len(idx) >= 4 and len(d0l["doors"]) == n and [d["name"] for d in d0l["doors"]] == [d["name"] for d in d0h["doors"]], f"host {n}, laptop {len(d0l['doors'])}")
        def same(i):
            a, b = L(f"mp cardoors {net}")["doors"][i], h(f"mp cardoors {net}")["doors"][i]
            ang = max(abs((x - y + 180) % 360 - 180) for x, y in zip(a["rot"], b["rot"]))
            return ang < 2 and math.dist(a["pos"], b["pos"]) < 0.02 and a["turn"] == b["turn"] and abs(a["slide"] - b["slide"]) < 0.05, a, b
        for phase, actor, who in (("A", L, "the laptop (not the owner) opens"), ("B", h, "the host (the owner) closes")):
            if phase == "B": h(f"mp lookat {net}"); L(f"mp lookat {net}"); time.sleep(1); h("shot cardoorsOpen_host"); L("shot cardoorsOpen_laptop")
            for i in idx:
                before = actor(f"mp cardoors {net}")["doors"][i]
                actor(f"mp opendoor {net} {i}"); time.sleep(3)
                ok, a, b = same(i)
                moved = max(abs((x - y + 180) % 360 - 180) for x, y in zip(a["rot"], before["rot"])) > 5 or math.dist(a["pos"], before["pos"]) > 0.05
                verdict(f"{phase} {who} {a['name']}: IT MOVED, BOTH SEE IT", ok and moved, f"before {before} | laptop {a} | host {b}")
            if phase == "A": h(f"mp lookat {net}"); L(f"mp lookat {net}"); time.sleep(1); h(f"shot cardoors{phase}_host"); L(f"shot cardoors{phase}_laptop")
        o = next(e for e in h("mp entities")["entities"] if e["net"] == net)
        verdict("THE CAR STAYED THE HOST'S, IN PLACE", not o["proxy"] and math.dist(o["pos"], car["pos"]) < 0.5, f"proxy {o['proxy']}, moved {math.dist(o['pos'], car['pos']):.2f} m")
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

#!/usr/bin/env python3
"""One part of the multiplayer frame throwing (two machines, relay iad by default). Before v0.65.50 any exception in it
ended the session for everyone (Mp.Tick → Stop). `mp fault <part> <s>`: that part throws every frame (5 = entities).
  1 HOST entities fault 2 s: the session stays; afterwards the host's item still reaches the laptop
  2 LAPTOP entities fault 2 s: the same the other way
  3 HOST entities fault 7 s (longer than StopAfterFaultsS 5): the session stops, with a notice
  tools/faulttick.py [--no-build] [--relay iad|off]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def row(x, n): return next((e for e in x("mp entities")["entities"] if e["net"] == n), None)
def up(x, side): st = x("mp status"); return ("server" in st) if side == "host" else bool(st.get("client", {}).get("ready"))

def moves_across(owner, viewer, net, tag):
    """the owner steps 6 m away and takes the item to itself (a test teleport would drop a held item); it puts it down;
    the viewer's copy must be where the owner has it"""
    me = owner("mp players")["me"]
    owner(f"tpground {me[0] + 6} {me[2]}"); time.sleep(2)
    p0 = row(owner, net)["pos"]
    owner(f"mp pickupnet {net}"); time.sleep(1.5); owner("dropheld"); time.sleep(3)
    a, b = row(owner, net), row(viewer, net)
    moved = math.dist(p0, a["pos"]) if a else -1
    print(f"    {tag}: the item moved {moved:.1f} m on its owner", flush=True)
    return math.dist(a["pos"], b["pos"]) if a and b and moved > 2 else -1

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
        h(f"tpground {x} {z}"); L(f"tpground {x + 3} {z}"); time.sleep(3)
        before = {e["net"] for e in h("mp entities")["entities"]}
        h("devspawn 320")
        until(lambda: any(e["net"] not in before and not e["car"] for e in h("mp entities")["entities"]), 15)
        net = next(e["net"] for e in h("mp entities")["entities"] if e["net"] not in before and not e["car"])
        until(lambda: row(L, net) is not None, 20); time.sleep(1)
        for tag, side, x_, other in (("1 HOST", "host", h, L), ("2 LAPTOP", "laptop", L, h)):
            f0 = g(x_, "TLDRevamp.Net.Mp.TickFaults")
            print(f"  {tag}: entities fault 2 s:", x_("mp fault 5 2"), flush=True); time.sleep(3)
            f1 = g(x_, "TLDRevamp.Net.Mp.TickFaults")
            verdict(f"{tag} FAULT 2 s: THE SESSION STAYS", up(h, "host") and up(L, "laptop"), f"faults {f0} -> {f1}")
            d = moves_across(x_, other, net, tag)
            verdict(f"{tag} AFTERWARDS: ITS ITEM STILL REACHES THE OTHER", 0 <= d < 0.5, f"{d:.2f} m apart after a 6 m carry")
        print("  3 HOST: entities fault 7 s:", h("mp fault 5 7"), flush=True); time.sleep(9)
        verdict("3 HOST FAULT 7 s: THE SESSION STOPS", not up(h, "host"), str(h("mp status")))
        logs = game.log_tail(400) if hasattr(game, "log_tail") else ""
        print("  host's log:", [l for l in (logs or "").split("\n") if "Mp.Tick entities" in l or "Multiplayer stopped" in l][-3:], flush=True)
    finally:
        h("mp fault -1 0"); L("mp fault -1 0")
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

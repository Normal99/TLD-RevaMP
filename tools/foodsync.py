#!/usr/bin/env python3
"""Food eaten by one player, seen by the other (two machines, relay iad by default). The host's food (it spawns it);
the laptop picks it up and eats it the game's way (one bite per click).
  A  a bite every 1.5 s: after each, the host's copy shows the same bite (index, parts); the last bite: gone on both,
     still gone 10 s later (not fetched back), gone from the host's save
  B  a fast eater: bites every 0.1 s right after the pickup — maybe before the server confirmed the item is the
     laptop's — same end: gone on both, not back
  tools/foodsync.py [--no-build] [--relay iad|off] [--food 227]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
FOOD = int(sys.argv[sys.argv.index("--food") + 1]) if "--food" in sys.argv else 227
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def food(x, n):
    try: return x(f"mp food {n}")
    except RuntimeError as e: return {"error": str(e)}

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
        h(f"tpground {x} {z}"); L(f"tpground {x + 2} {z}"); time.sleep(3)
        for phase in ("A", "B"):
            before = {e["net"] for e in h("mp entities")["entities"]}
            print(f"{phase}: host spawns", h(f"devspawn {FOOD}"), flush=True)
            if not until(lambda: any(e["net"] not in before and not e["car"] for e in h("mp entities")["entities"]), 15): verdict(f"{phase} FOOD SPAWNED", False); continue
            net = next(e["net"] for e in h("mp entities")["entities"] if e["net"] not in before and not e["car"])
            until(lambda: "gone" in food(L, net) and not food(L, net)["gone"], 20); time.sleep(1)
            f0 = food(h, net); print("  food:", f0, flush=True)
            idsave = h(f"mp entities")  # (owner/save check below)
            print("  laptop picks it up:", L(f"mp pickupnet {net}"), flush=True)
            bites = 0
            if phase == "A":
                time.sleep(2)
                while bites < 20 and not food(L, net).get("gone"):
                    r = L("mp bite"); bites += 1; time.sleep(1.5)
                    a, b = food(L, net), food(h, net)
                    same = a.get("gone") == b.get("gone") and (a.get("gone") or (a["index"] == b["index"] and a["parts"] == b["parts"]))
                    print(f"  bite {bites}: {r} | laptop {a} | host {b}", flush=True)
                    if not a.get("gone"): verdict(f"A BITE {bites}: THE HOST SEES THE SAME", same, "")
            else:
                while bites < 20:
                    try: L("mp bite")
                    except RuntimeError: break   # gone from the hand
                    bites += 1; time.sleep(0.1)
                print(f"  {bites} quick bites", flush=True)
            gone_h = until(lambda: food(h, net).get("gone") is True, 5)
            verdict(f"{phase} EATEN ({bites} bites): GONE ON THE HOST TOO", gone_h, str(food(h, net)))
            time.sleep(10)
            a, b = food(L, net), food(h, net)
            verdict(f"{phase} STILL GONE 10 S LATER ON BOTH (not fetched back)", a.get("gone") is True and b.get("gone") is True, f"laptop {a} | host {b}")
            near = [e for e in h("mp entities")["entities"] if e["net"] not in before and not e["car"]]
            verdict(f"{phase} NO LEFTOVER COPY ANYWHERE", not near and not [e for e in L("mp entities")["entities"] if e["net"] == net], str(near[:3]))
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

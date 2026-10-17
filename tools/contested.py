#!/usr/bin/env python3
"""Two players grab the same item at the same moment (two machines, relay iad by default). Each game picks it up at
once (its own player's grab; the claim goes to the server); the server gives it to one. Afterwards exactly ONE
player may hold it — the owner — with the other's hands empty, and both screens agree where it is (in the owner's
hands). A: the host's item; B: the laptop's item. Several rounds each (the grab order varies with the relay).
  tools/contested.py [--no-build] [--relay iad|off] [--rounds 3] [--item 320]"""
import sys, time, math, pathlib, threading
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
ROUNDS = int(sys.argv[sys.argv.index("--rounds") + 1]) if "--rounds" in sys.argv else 3
ITEM = int(sys.argv[sys.argv.index("--item") + 1]) if "--item" in sys.argv else 320
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def holding(x):
    v = g(x, "mainscript.s.player.pickedUp.name")
    return v if isinstance(v, str) and v not in ("null", "") else None
def row(x, n): return next((e for e in x("mp entities")["entities"] if e["net"] == n), None)

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
        h(f"tpground {x} {z}"); L(f"tpground {x + 1.5} {z}"); time.sleep(3)
        for phase, spawner in (("A", h), ("B", L)):
            for rnd in range(1, ROUNDS + 1):
                if holding(h): h("dropheld")
                if holding(L): L("dropheld")
                before = {e["net"] for e in spawner("mp entities")["entities"]}
                spawner(f"devspawn {ITEM}")
                if not until(lambda: any(e["net"] not in before and not e["car"] for e in spawner("mp entities")["entities"]), 15): verdict(f"{phase}{rnd} SPAWNED", False); continue
                net = next(e["net"] for e in spawner("mp entities")["entities"] if e["net"] not in before and not e["car"])
                other = L if spawner is h else h
                until(lambda: row(other, net) is not None, 20); time.sleep(2)
                res = {}
                def grab(name, side): res[name] = side(f"mp pickupnet {net}")
                ts = [threading.Thread(target=grab, args=("host", h)), threading.Thread(target=grab, args=("laptop", L))]
                for t in ts: t.start()
                for t in ts: t.join()
                time.sleep(4)
                hh, lh = holding(h), holding(L)
                rh, rl = row(h, net), row(L, net)
                owner = rh["owner"] if rh else None
                holders = [n for n, v in (("host", hh), ("laptop", lh)) if v]
                own_name = {0: "host"}.get(owner, "laptop")
                print(f"  {phase}{rnd}: grabs {res} | host holds {hh}, laptop holds {lh} | owner {owner} (laptop sees {rl and rl['owner']})", flush=True)
                lp, hp_ = L("gpos"), h("gpos")
                pos_h, pos_l = (rh or {}).get("pos"), (rl or {}).get("pos")
                d = math.dist(pos_h, pos_l) if pos_h and pos_l else 99
                verdict(f"{phase}{rnd} ONE HOLDER, THE OWNER", holders == [own_name], f"holders {holders}, owner {own_name}")
                verdict(f"{phase}{rnd} BOTH SCREENS AGREE WHERE IT IS", d < 0.6 and rl and rl["owner"] == owner, f"{d:.2f} m apart")
        print("  grabs lost (let go): host", g(h, "TLDRevamp.Net.Entities.GrabsLost"), "laptop", g(L, "TLDRevamp.Net.Entities.GrabsLost"), flush=True)
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        for s_ in (h, L):
            try:
                if holding(s_): s_("dropheld")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

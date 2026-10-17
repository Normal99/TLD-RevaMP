#!/usr/bin/env python3
"""Putting an item away and taking it out, seen by the other player (two machines, relay iad by default). The laptop
picks up the host's lamp, then switches inventory slot the game's way (fpscontroller.InvSwitchTo: the held item goes
into its slot, hidden), back again (in the hand), then drops it. The host's copy at each step: hidden while stored
(not floating where the laptop's inventory point is), in the laptop's hand when taken out, and where it lands.
  tools/invsync.py [--no-build] [--relay iad|off] [--item 320]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
ITEM = int(sys.argv[sys.argv.index("--item") + 1]) if "--item" in sys.argv else 320
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
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
        h(f"tpground {x} {z}"); L(f"tpground {x + 3} {z}"); time.sleep(3)
        before = {e["net"] for e in h("mp entities")["entities"]}
        print("host spawns:", h(f"devspawn {ITEM}"), flush=True)
        until(lambda: any(e["net"] not in before and not e["car"] for e in h("mp entities")["entities"]), 15)
        net = next(e["net"] for e in h("mp entities")["entities"] if e["net"] not in before and not e["car"])
        until(lambda: row(L, net) is not None, 20); time.sleep(1)
        print("laptop picks it up:", L(f"mp pickupnet {net}"), flush=True); time.sleep(2.5)
        n_inv = g(L, "mainscript.s.player.inventory.Count"); s0 = g(L, "mainscript.s.player.selectedInv")
        s1 = (int(s0) + 1) % int(n_inv)
        print(f"  slots {n_inv}, selected {s0}, held {g(L, 'mainscript.s.player.pickedUp.name')}", flush=True)
        def state(tag):
            a, b = row(L, net), row(h, net); lp = L("mp players")["me"]
            print(f"  {tag}: laptop stored {a['stored']} shown {a['shown']} owner {a['owner']} | host's copy stored {b['stored']} shown {b['shown']} at {math.dist(b['pos'], lp):.2f} m from the laptop's player", flush=True)
            return a, b, lp
        # put away
        L(f"call mainscript.s.player.InvSwitchTo {s1} False"); time.sleep(2.5)
        a, b, lp = state("put away")
        h(f"mp lookplayer 1"); time.sleep(0.5); h("shot inv_stored")
        verdict("PUT AWAY: STORED ON THE LAPTOP", a["stored"] and a["shown"] is False, "")
        verdict("PUT AWAY: HIDDEN ON THE HOST TOO", b["stored"] and b["shown"] is False, f"stored {b['stored']} shown {b['shown']}")
        if "--carry" in sys.argv:
            # carried away in the inventory: the laptop goes 400 m off, the host stands where it was put away. It stays
            # the laptop's (a handoff would take it out of the inventory). First with the guard off (the old behaviour),
            # then on; where the server thinks the stored item is tells whether its position follows the holder.
            for keep in (False, True):
                h(f"set TLDRevamp.Net.Entities.HandoffKeepCarried {keep}")
                L(f"tpground {x + 400} {z}"); time.sleep(8)
                a, b, lp = state(f"carried 400 m away (guard {keep})")
                hp = h("mp players")["me"]
                print(f"    the host's view of the item is {math.dist(b['pos'], lp):.0f} m from the laptop, {math.dist(b['pos'], hp):.0f} m from the host; handoffs {h('get TLDRevamp.Net.Entities.Handoffs')['value']}, kept {h('get TLDRevamp.Net.Entities.HandoffKeptCarried')['value']}", flush=True)
                verdict(f"CARRIED AWAY (guard {keep}): STILL THE LAPTOP'S, STILL IN ITS INVENTORY", a["owner"] == b["owner"] and a["stored"] and b["owner"] != 0,
                        f"owner laptop's view {a['owner']} host's view {b['owner']}, stored {a['stored']}")
                L(f"tpground {x + 3} {z}"); time.sleep(4)
            h("set TLDRevamp.Net.Entities.HandoffKeepCarried True")
        # take out
        L(f"call mainscript.s.player.InvSwitchTo {s0} False"); time.sleep(2.5)
        a, b, lp = state("taken out")
        # out of the inventory it is held in the right hand (fpscontroller.inHandP), not as a picked-up item
        held = g(L, "mainscript.s.player.pickedUp.name"); inhand = g(L, "mainscript.s.player.inHandP.name")
        hand = held if isinstance(held, str) and held not in ("", "null") else inhand
        verdict("TAKEN OUT: IN THE LAPTOP'S HAND", isinstance(hand, str) and "(Clone)" in hand and not a["stored"], f"picked {held}, right hand {inhand}")
        verdict("TAKEN OUT: SHOWN ON THE HOST, AT THE LAPTOP'S PLAYER", not b["stored"] and b["shown"] is not False and math.dist(b["pos"], lp) < 2.0, f"{math.dist(b['pos'], lp):.2f} m")
        # drop
        if isinstance(held, str) and held not in ("", "null"): L("dropheld")
        else: L("call mainscript.s.player.RightHandRelease True")   # the game letting go of a right-hand item
        time.sleep(3)
        a, b = row(L, net), row(h, net)
        verdict("DROPPED: SAME PLACE ON BOTH", math.dist(a["pos"], b["pos"]) < 0.3, f"{math.dist(a['pos'], b['pos']):.2f} m apart")
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

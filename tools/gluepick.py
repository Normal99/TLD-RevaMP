#!/usr/bin/env python3
"""An item stuck on someone else's car, grabbed off it (2026-10-22 bug hunt, commit review: d71ad45 made the server drop
a DetachSync whose epoch isn't the item's current one, and a grab claims the item first — the server bumps the epoch
at the claim — so the grab's detach arrived with the old epoch and was dropped: the item stayed stuck on the car on
every other screen, and for anyone joining later). Two machines over the relay; the host's Car06Full.
  A  the host glues its lamp onto its car's roof (the game's glue); the laptop sees it stuck there
  B  the laptop's player grabs it off the car → on the host it comes off too (not stuck on the car), the laptop's now
  C  the laptop drops it beside the car → both have it at the same place, loose
  D  the server's record: the laptop leaves and joins again → its new copy is loose, where the host has it
  E  and that is where it was dropped (nothing moved it while the laptop was away)
  tools/gluepick.py [--no-build] [--relay iad|off] [--item <name to spawn>: else the nearest glueable item]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, ITEM, CAR = arg("--relay", "iad"), arg("--item", ""), arg("--car", "Car06Full")
T = "TLDRevamp.Net.Entities"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, bool(ok))); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
def stuck(s, net): return s(f"mp attachpose {net}").get("attached") is True

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
        lid, hid = L("mp entities")["me"], h("mp entities")["me"]
        hp = h("gpos")
        h(f"tpground {hp['gx'] + 15} {hp['gz']}"); L(f"tpground {hp['gx'] + 15} {hp['gz'] + 6}"); time.sleep(4)
        before = set(ents(h))
        print("  host car:", h(f"devspawn {CAR}"), flush=True)
        car = until(lambda: next((n for n, e in ents(h).items() if n not in before and e["car"] and not e["proxy"]), None), 20)
        if not car or not until(lambda: car in ents(L), 30): sys.exit("laptop never got the car")
        time.sleep(6)
        cp = ents(h)[car]["pos"]
        h(f"tpground {cp[0] + 3} {cp[2]}"); time.sleep(2)
        if ITEM:
            hb = set(ents(h))
            print("  host spawns:", h(f"devspawn {ITEM}"), flush=True)
            item = until(lambda: next((n for n, e in ents(h).items() if n not in hb and not e["car"] and not e["proxy"]), None), 20)
        else:   # the nearest loose glueable item the host has (a bike mirror, a clock, a pumpkin...)
            gl = [r for r in h("mp glue")["items"] if not r[2] and r[1] != "Hand"]
            print("  glueable here:", gl[:8], flush=True)
            item = gl[0][0] if gl else None
        if not item or not until(lambda: item in ents(L), 30): sys.exit("the laptop never got the item")
        time.sleep(3)
        print("  host glues it on the roof:", h(f"mp glue {item} {car} 0.2"), flush=True)
        verdict("A STUCK ON THE CAR ON BOTH", until(lambda: stuck(h, item) and stuck(L, item), 20),
                f"host {h(f'mp attachpose {item}')}, laptop {L(f'mp attachpose {item}')}")
        L(f"tpground {cp[0] + 2} {cp[2] + 1}"); time.sleep(3)
        d0 = (L("mp attach"), h("mp attach"))
        print("  laptop grabs it:", L(f"mp pickupnet {item}"), flush=True)
        time.sleep(6)
        el, eh = ents(L).get(item, {}), ents(h).get(item, {})
        d1 = (L("mp attach"), h("mp attach"))
        print(f"  laptop detach sent {d0[0]['detachSent']}→{d1[0]['detachSent']}, host applied {d0[1]['detachApplied']}→{d1[1]['detachApplied']}", flush=True)
        verdict("B GRABBED OFF THE CAR: OFF ON THE HOST TOO, THE LAPTOP'S", not stuck(L, item) and not stuck(h, item) and eh.get("owner") == lid,
                f"laptop stuck {stuck(L, item)}, host stuck {stuck(h, item)}; host sees owner {eh.get('owner')} (laptop {lid}), held {el.get('held')}")
        L("mp drop"); time.sleep(6)
        el, eh = ents(L).get(item, {}), ents(h).get(item, {})
        gap = math.dist(el["pos"], eh["pos"]) if el.get("pos") and eh.get("pos") else None
        verdict("C DROPPED: LOOSE ON BOTH, AT THE SAME PLACE", not stuck(L, item) and not stuck(h, item) and gap is not None and gap < 0.5,
                f"laptop stuck {stuck(L, item)}, host stuck {stuck(h, item)}; {gap and round(gap, 2)} m apart")
        drop = el.get("pos")
        L("mp stop"); time.sleep(5)
        print("  after the laptop left, the host has it at", ents(h).get(item, {}).get("pos"), "owner", ents(h).get(item, {}).get("owner"), "(dropped at", drop, ")", flush=True)
        track = {"stop": False, "seen": []}
        def watch_item():
            from bench import Bridge
            hw = Bridge()   # a connection of its own: the main thread talks to the host meanwhile
            while not track["stop"]:
                try:
                    p_ = ents(hw).get(item, {}).get("pos")
                    if p_ and (not track["seen"] or math.dist(p_, track["seen"][-1][1]) > 0.3): track["seen"].append((round(time.time() - t_w, 1), p_))
                except Exception: pass
                time.sleep(0.5)
        import threading; t_w = time.time(); th = threading.Thread(target=watch_item, daemon=True); th.start()
        rj = join_both(h, L, rehost=False)
        time.sleep(8); track["stop"] = True
        print("  the host's copy while the laptop joined again (s, pos):", track["seen"][:12], flush=True)
        if not rj: verdict("D REJOINED", False, "the laptop never joined again")
        else:
            L("set TLDRevamp.TestSafety.BlockAutoSave True")
            got = until(lambda: item if item in ents(L) and L(f"mp attachpose {item}") is not None else None, 45)   # placed here, not only listed
            time.sleep(3)
            e2, eh2 = (ents(L).get(got, {}) if got else {}), ents(h).get(item, {})
            gap = math.dist(e2["pos"], eh2["pos"]) if e2.get("pos") and eh2.get("pos") else None
            moved = math.dist(eh2["pos"], drop) if eh2.get("pos") and drop else None
            verdict("D JOINED AGAIN: ITS NEW COPY IS LOOSE, WHERE THE HOST HAS IT", got and not stuck(L, got) and gap is not None and gap < 0.5,
                    f"stuck {got and stuck(L, got)}; {gap and round(gap, 2)} m from the host's; the host's {moved and round(moved, 2)} m from the drop")
            verdict("E NOBODY MOVED IT: STILL WHERE IT WAS DROPPED", moved is not None and moved < 1.0, f"{moved and round(moved, 2)} m")
            if moved is None or moved >= 1.0:
                print("  the host's copy now", eh2.get("pos"), "owner", eh2.get("owner"), "| laptop player at", L("gpos"), "host player at", h("gpos"), flush=True)
                print("  owner log:", h(f"mp ownerlog {item}").get("log"), flush=True)
        print("  errors:", [s_("errors") for s_ in (h, L)], flush=True)
    finally:
        try: L("mp stop"); h("mp stop")
        except Exception: pass
        if RELAY != "off":
            for s_ in (h, L):
                try: s_("net relay off")
                except Exception: pass
        L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
print(f"GLUEPICK: {sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} PASS")

#!/usr/bin/env python3
"""Paint in MP (two machines, relay iad by default). The host's car; every paintable part's colour compared host vs laptop.
  1 the laptop (not the owner) paints a part red with the game's own spray stroke (partconditionscript.Paint)
  2 the host (the owner) paints the same part blue
  3 both players leave 5 km (the car into the far store on both) and come back
  4 the laptop leaves the session and joins again
Each time: the colours the same on both machines, and the painted part the colour last sprayed.
  tools/paintsync.py [--no-build] [--relay iad|off] [--ab: first with the v0.65.20 fix off]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import tp

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
VERDICTS = []
SHOWN = True   # compare what's on screen (the part's material colour), not only the stored colour
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def colours(x):
    """part path -> colour; the object itself as <root> (a fresh dev spawn is Car01Full(Clone) with the prefab's part
    names, a copy built from the record Car01(Clone): paths compared where both machines have them)"""
    rows = x(f"mp paint {NET}")["parts"]
    def key(p):
        head, _, idx = p.rpartition("#")
        return (head or "<root>") + "#" + idx
    return {key(r[0]): (tuple(r[5:8]) if SHOWN else tuple(r[1:4])) for r in rows}

def check(tag, part, want):
    """both machines' colours equal; `part` the wanted colour on both"""
    ok = until(lambda: colours(L).get(part) is not None and all(abs(a - b) < 0.02 for a, b in zip(colours(L)[part], want))
               and all(abs(a - b) < 0.02 for a, b in zip(colours(h)[part], want)), 15, 1.0)
    ch, cl = colours(h), colours(L)
    diff = [k for k in ch if k in cl and any(abs(a - b) > 0.02 for a, b in zip(ch[k], cl[k]))]
    missing = sorted(set(ch) ^ set(cl)); both = len(set(ch) & set(cl))
    print(f"  {tag}: {part} host {ch.get(part)} laptop {cl.get(part)}; differing parts {diff[:6]}; compared {both}, only on one {len(missing)}", flush=True)
    t_ = tag.split(" ")[0]; h(f"shot paint{t_}"); L(f"shot paint{t_}")
    verdict(f"{tag}: THE PART IS {want} ON BOTH, EVERY PART THE SAME", ok and not diff and both >= 5, f"{len(diff)} of {both} parts differ ({len(missing)} named differently)")

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
        rp = h("roadpoint"); x0, z0 = rp["gx"], rp["gz"]
        h(f"tpground {x0} {z0}"); time.sleep(3)
        before = {e["net"] for e in h("mp entities")["entities"]}
        print("car:", h("devspawn Car01Full"), flush=True)
        if not until(lambda: any(e["car"] and e["net"] not in before for e in h("mp entities")["entities"]), 20): sys.exit("NO CAR")
        NET = next(e["net"] for e in h("mp entities")["entities"] if e["car"] and e["net"] not in before)
        L(f"tpground {x0 + 6} {z0}"); time.sleep(3)
        if not until(lambda: len(colours(L)) > 0, 30): sys.exit("THE LAPTOP HAS NO COPY")
        print(f"  {len(colours(h))} paintable parts on the host, {len(colours(L))} on the laptop", flush=True)
        if "--hover" in sys.argv:   # the car's height and what's under it, on both machines (screenshots looked raised)
            for k in range(3):
                eh = next(e for e in h("mp entities")["entities"] if e["net"] == NET); el = next(e for e in L("mp entities")["entities"] if e["net"] == NET)
                print(f"  car y host {eh['pos'][1]:.2f} laptop {el['pos'][1]:.2f}; under host {h(f'mount probe {NET}').get('under')} | laptop {L(f'mount probe {NET}').get('under')}", flush=True)
                time.sleep(2)
            for nm, s_ in (("host", h), ("laptop", L)): print(f"  {nm} wheels: {s_(f'mount bodies {NET}').get('wheels(name,en,active,motor,brake,rpm,grounded,rb)')}", flush=True)
            raise SystemExit
        if "--ab" in sys.argv:
            for s_ in (h, L): s_("set TLDRevamp.Net.Entities.RepaintOnApply False")
            r = L(f"mp paint {NET} 0.2 0.8 0.2"); print("  (fix off) laptop paints green:", r, flush=True)
            hd, _, ix = r["painted"].rpartition("#"); P0 = (hd or "<root>") + "#" + ix
            time.sleep(6)
            print(f"  (fix off) on screen: host {colours(h).get(P0)} laptop {colours(L).get(P0)}", flush=True)
            verdict("0 (FIX OFF, the old behaviour) THE OWNER SHOWS THE OTHER PLAYER'S PAINT", all(abs(a - b) < 0.02 for a, b in zip(colours(h).get(P0) or (9, 9, 9), (0.2, 0.8, 0.2))))
            for s_ in (h, L): s_("set TLDRevamp.Net.Entities.RepaintOnApply True")
        r = L(f"mp paint {NET} 0.9 0.05 0.05"); print("  laptop paints:", r, flush=True)
        head, _, idx = r["painted"].rpartition("#")
        PART = (head or "<root>") + "#" + idx
        check("1 THE OTHER PLAYER PAINTS", PART, (0.9, 0.05, 0.05))
        print("  host paints:", h(f"mp paint {NET} 0.05 0.1 0.9"), flush=True)
        check("2 THE OWNER PAINTS", PART, (0.05, 0.1, 0.9))
        for nm, s_ in (("host", h), ("laptop", L)): print(f"  {nm} 5 km away:", tp(s_, x0 + 5000, z0 + (8 if s_ is L else 0)), flush=True)
        time.sleep(12)
        h(f"tpground {x0 - 6} {z0}"); L(f"tpground {x0 + 6} {z0}"); time.sleep(8)
        until(lambda: len(colours(L)) > 0 and len(colours(h)) > 0, 30)
        check("3 AFTER THE FAR STORE", PART, (0.05, 0.1, 0.9))
        L("mp stop"); time.sleep(4)
        L(f"mp joinid {h('net me')['steamId']}")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER REJOINED")
        L(f"tpground {x0 + 6} {z0}"); time.sleep(6)
        until(lambda: len(colours(L)) > 0, 30)
        check("4 AFTER A REJOIN", PART, (0.05, 0.1, 0.9))
    finally:
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

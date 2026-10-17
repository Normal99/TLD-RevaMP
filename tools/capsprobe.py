#!/usr/bin/env python3
"""Tank caps on a shared car (playtest 2026-10-09: "radiator caps sometimes untouchable by both players and the mouse
gets locked"). Two machines over the relay. The host spawns a car; both list its caps (`mp caps`: usable kind, state,
colliders, layer). Then each cap is used once by the laptop (on its copy) and once by the host (its own): after each,
both machines' cap state must agree and the cap's colliders must be on where they were on.
  tools/capsprobe.py [--no-build] [--relay iad|off] [--car 2] [--old]"""
import sys, time, math, pathlib, json
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, CARID = arg("--relay", "iad"), arg("--car", 2, int)
VERDICTS = []
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

def ent(s, net): return next((e for e in s("mp entities")["entities"] if e["net"] == net), None)
def key(c): return (c["cap"], c["usable"].split(" x ")[0] if c["usable"] else None)   # kind + turn state (x is a drag angle)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    try:
        h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        OLD = "--old" in sys.argv
        for s_ in (h, L): s_(f"set TLDRevamp.Net.Entities.ScopedEdits {not OLD}")
        print(f"  {'OLD (whole-item edits)' if OLD else 'NEW (scoped edits)'}", flush=True)
        time.sleep(5)
        hp = h("gpos"); me = h("mp entities")["me"]
        cx, cz = hp["gx"] + 8, hp["gz"] + 8
        print("  host car:", h(f"spawncarg {CARID} 0 {cx} {cz} 0"), flush=True)
        car = []
        until(lambda: bool(car.extend(e["net"] for e in h("mp entities")["entities"] if e["car"] and e["owner"] == me
                                      and math.hypot(e["pos"][0] - cx, e["pos"][2] - cz) < 15) or car), 20)
        car = max(car)
        until(lambda: (ent(L, car) or {}).get("dormant") is False, 20); time.sleep(5)
        L(f"tpground {cx - 3} {cz - 3}"); time.sleep(3)
        hc, lc = h(f"mp caps {car}")["caps"], L(f"mp caps {car}")["caps"]
        print(f"  host's caps ({len(hc)}):"); [print("    ", json.dumps(c)) for c in hc]
        print(f"  laptop's copy ({len(lc)}):"); [print("    ", json.dumps(c)) for c in lc]
        verdict("0 THE SAME CAPS ON BOTH", [key(c) for c in hc] == [key(c) for c in lc])
        for i, c in enumerate(hc):
            for who, s_ in (("laptop", L), ("host", h)):
                g_ = lambda s2, k: s2(f"get TLDRevamp.Net.Entities.{k}")["value"]
                e0 = (g_(L, "EditsSent"), g_(h, "EditsApplied"), g_(h, "EditsSent"), g_(L, "EditsApplied"))
                L("set TLDRevamp.Net.Entities.EditMarks ''"); h("set TLDRevamp.Net.Entities.EditMarks ''")
                try: r = s_(f"mp usecap {car} {i}")
                except RuntimeError as ex: r = {"error": str(ex)}
                time.sleep(3.5)
                a, b = h(f"mp caps {car}")["caps"][i], L(f"mp caps {car}")["caps"][i]
                e1 = (g_(L, "EditsSent"), g_(h, "EditsApplied"), g_(h, "EditsSent"), g_(L, "EditsApplied"))
                print(f"    edits: copy tank sim skipped {g_(L, 'CopyTankSimSkipped')}, laptop sent +{e1[0]-e0[0]}, host applied +{e1[1]-e0[1]}, host sent +{e1[2]-e0[2]}; marks laptop [{g_(L, 'EditMarks')}] host [{g_(h, 'EditMarks')}]", flush=True)
                same = key(a) == key(b)
                cols_ok = all(":on" in x or ":off" in y for x, y in zip(b["cols"], lc[i]["cols"]))
                print(f"  cap {i} {c['item']}/{c['cap']} used by the {who}: {r} → host {a['usable']} valve {a['valve']} | laptop {b['usable']} valve {b['valve']} cols {b['cols']}", flush=True)
                if "error" not in r:
                    verdict(f"{i} {c['item']} USED BY THE {who.upper()}: THE SAME ON BOTH", same, f"host {a['usable']}, laptop {b['usable']}")
                    st = lambda c2: int(c2["usable"].split("state ")[1].split()[0])
                    verdict(f"{i} {c['item']} USED BY THE {who.upper()}: IT STAYS AS SET", st(a) == r.get("state") and st(b) == r.get("state"),
                            f"set {r.get('state')}, host {st(a)}, laptop {st(b)}")
                    verdict(f"{i} {c['item']} USED BY THE {who.upper()}: THE LAPTOP'S COLLIDERS AS BEFORE", cols_ok, str(b["cols"]))
    finally:
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

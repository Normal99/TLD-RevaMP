#!/usr/bin/env python3
"""A part's random look on the other player's screen (playtest 2026-10-09: "whitewall tire desynced, probably don't have
all parts synced variety wise"). A part condition may roll a random look when the part starts (partconditionscript.FStart:
slectedRandomTipus → the material type it shows, tipus) — after the spawn the mod shares it from. Two machines over the
relay: the host spawns --n loose tyres (TireGumi) and a Car05Full; after the copies arrive and again 8 s later (resyncs),
every part condition's chosen look and shown material compared host vs laptop (`mp pcs`).
  tools/tirelook.py [--no-build] [--relay iad|off] [--n 16]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, N = arg("--relay", "iad"), arg("--n", 16, int)
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
        time.sleep(5)
        me = h("mp entities")["me"]
        before = {e["net"] for e in h("mp entities")["entities"]}
        u = h("gpos"); gy = h(f"ground {u['ux'] + 6} {u['uz'] + 6}").get("y")
        print("  tyres:", h(f"spawnpile 44 {N} {u['ux'] + 6} {gy + 0.6} {u['uz'] + 6} 4 1.2 0 11"), flush=True)
        g = h("gpos")
        print("  car:", h(f"spawncarg 154 0 {g['gx'] - 10} {g['gz'] + 10} 0"), flush=True)
        time.sleep(4)
        nets = [e["net"] for e in h("mp entities")["entities"] if e["net"] not in before and e["owner"] == me and e["part"] < 0]
        until(lambda: all(any(x["net"] == n and not x["dormant"] for x in L("mp entities")["entities"]) for n in nets), 30)
        print(f"  {len(nets)} new objects shared", flush=True)
        for label, wait in (("ON ARRIVAL", 0), ("AFTER THE RESYNCS", 8)):
            time.sleep(wait)
            diff, total, rolled = [], 0, 0
            for n in nets:
                a, b = h(f"mp pcs {n}").get("pcs", []), L(f"mp pcs {n}").get("pcs", [])
                total += len(a); rolled += sum(1 for r in a if r[3] != 0)
                for ra, rb in zip(a, b):
                    if ra[3:5] != rb[3:5]: diff.append((n, ra[2], f"host {ra[3]}/{ra[4]}", f"laptop {rb[3]}/{rb[4]}"))
                if len(a) != len(b): diff.append((n, "count", len(a), len(b)))
            verdict(f"{label}: EVERY PART'S RANDOM LOOK THE SAME", not diff, f"{total} part conditions, {rolled} with a rolled look; differ: {diff[:12]}")
        # everything else both machines have loaded: world-generated cars and parts (their looks rolled at generation)
        hl = {e["net"] for e in h("mp entities")["entities"] if not e["dormant"]}
        ll = {e["net"] for e in L("mp entities")["entities"] if not e["dormant"]}
        diff, total, rolled = [], 0, 0
        for n in sorted(hl & ll):
            try: a, b = h(f"mp pcs {n}").get("pcs", []), L(f"mp pcs {n}").get("pcs", [])
            except RuntimeError: continue
            total += len(a); rolled += sum(1 for r in a if r[3] != 0)
            for ra, rb in zip(a, b):
                if ra[3:5] != rb[3:5]: diff.append((n, ra[2], f"host {ra[3]}/{ra[4]}", f"laptop {rb[3]}/{rb[4]}"))
        verdict("WORLD: EVERY LOADED PART'S RANDOM LOOK THE SAME", not diff, f"{len(hl & ll)} objects, {total} part conditions, {rolled} with a rolled look; differ: {diff[:12]}")
    finally:
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

#!/usr/bin/env python3
"""Every shared object both machines have loaded, compared by place (the crates.py runs found a host's plank whose copy
on the laptop lay 14 m away and fell when a crate under it broke — a copy simulated where it shouldn't be). Two machines
over the relay: the laptop joins next to the host; at T+20 s and T+80 s every object loaded on both (non-dormant),
host vs laptop: more than 1 m apart → listed with both machines' flags (owner, proxy, physical, moved by body, last state
sent / received, at rest). Nothing is moved by the test.
  tools/worldcensus.py [--no-build] [--relay iad|off] [--tol 1.0]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, TOL = arg("--relay", "iad"), arg("--tol", 1.0, float)
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

KEYS = ("owner", "proxy", "physical", "byBody", "sentRest", "lastSent", "lastIn", "held", "stored", "parent", "attached", "recv", "seqOut")
def census(h, L, label):
    he = {e["net"]: e for e in h("mp entities")["entities"] if not e["dormant"]}
    le = {e["net"]: e for e in L("mp entities")["entities"] if not e["dormant"]}
    both = sorted(set(he) & set(le))
    off = []
    for n in both:
        d = math.dist(he[n]["pos"], le[n]["pos"])
        if d > TOL: off.append((round(d, 2), n, he[n]["name"]))
    off.sort(reverse=True)
    verdict(f"{label}: EVERY OBJECT AT THE SAME PLACE (±{TOL} m)", not off, f"{len(both)} on both, {len(off)} apart: {off[:15]}")
    for d, n, nm in off[:10]:
        print(f"    net {n} {nm} {d} m — host {[round(v, 2) for v in he[n]['pos']]} {{{', '.join(f'{k}: {he[n].get(k)}' for k in KEYS)}}}", flush=True)
        print(f"    {' ' * (len(str(n)) + len(nm) + len(str(d)) + 9)}laptop {[round(v, 2) for v in le[n]['pos']]} {{{', '.join(f'{k}: {le[n].get(k)}' for k in KEYS)}}}", flush=True)
    only_h = [he[n]["name"] for n in set(he) - set(le) if math.dist(he[n]["pos"], L("gpos") and [L("gpos")["gx"], he[n]["pos"][1], L("gpos")["gz"]]) < 150]
    print(f"  loaded on the host only, within 150 m of the laptop: {len(only_h)} {sorted(only_h)[:20]}", flush=True)
    return off

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
        hp = h("gpos"); L(f"tpground {hp['gx'] + 4} {hp['gz'] - 6}")
        time.sleep(20); census(h, L, "T+20 s")
        time.sleep(60); census(h, L, "T+80 s")
        print("  errors:", [s_("errors")["errors"] for s_ in (h, L)], flush=True)
    finally:
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

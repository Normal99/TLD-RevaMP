#!/usr/bin/env python3
"""The missing diner (playtest 2026-10-09/10 (b)): the diner's items on bare ground for one player. Its lease shows
every machine generated a building at that spot; a machine with ANOTHER building type there (a different game build)
would show exactly that — the holder's contents, someone else's building — and nothing in the logs said so. Two
machines over the relay: a building ~0.5-3 km away; on the laptop it is made another type before it is spawned there
(`poiretype`: what a different game generates); the host goes there first (it spawns the contents: the lease holder),
then the laptop → an [anomaly] "building differs at <key>" line on the host and on the laptop, with both types.
  tools/leasetype.py [--no-build] [--relay iad|off] [--old: no type in the lease request, before v0.66.8]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
T = "TLDRevamp.Net.Entities"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None
def travel(s, gx, gz, who, settle=4):
    s("set mainscript.s.player.godModeTillGrounded True")
    s(f"tpg {gx} {gz} hold")
    def grounded():
        q = s("gpos"); return "y" in s(f"ground {q['ux']} {q['uz']}")
    if not until(grounded, 90, 1.0): s("tprelease"); raise RuntimeError(f"{who}: no ground at {gx:.0f},{gz:.0f}")
    r = s(f"tpground {gx} {gz}")
    if r.get("tpground") == "hop": until(grounded, 30, 1.0); s(f"tpground {gx} {gz}")
    time.sleep(settle)
    q = s("gpos"); print(f"  {who} at {q['gx']:.0f},{q['gz']:.0f}", flush=True)
def pois(s, r=3000):
    u = s("gpos"); return {p["key"]: p for p in s(f"pois {u['ux']} {u['uz']} {r}")["pois"]}
def anomalies(s, key):
    return [x for x in s("mp anomalies")["lines"] if f"building differs at {key}" in x]

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    gods = {}
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, s_("get kaposztaleves.s.settings.god").get("value")); s_("set kaposztaleves.s.settings.god True")
            s_(f"set {T}.LeaseTypeCheck {not OLD}")
        print(f"  {'OLD (no type in the lease request)' if OLD else 'CURRENT BUILD'}", flush=True)
        time.sleep(10)
        ph, pl = pois(h), pois(L)
        cand = sorted((p for k, p in ph.items() if k in pl and 400 < p["d"] < 3000 and not p["items"] and not pl[k]["items"]), key=lambda p: p["d"])
        if not cand: sys.exit(f"no building 0.4-3 km away with its contents unspawned on both (host {[(p['name'], p['d'], p['items']) for p in ph.values()]})")
        b = cand[0]; key = b["key"]
        other = 16 if b["id"] != 16 else 1   # the diner (YellowFoodKut), or the start house
        print(f"  building {b['name']} (type {b['id']}) at {key}, {b['d']} m away", flush=True)
        r = L(f"poiretype {key} {other}"); print("  [laptop] retyped:", r, flush=True)
        verdict("SETUP: THE LAPTOP HAS ANOTHER BUILDING THERE", "error" not in r)
        a0h, a0l = len(anomalies(h, key)), len(anomalies(L, key))
        travel(h, b["gx"] + 25, b["gz"] + 25, f"host (at {b['name']})")
        until(lambda: pois(h, 200).get(key, {}).get("items"), 40)
        time.sleep(10)
        print("  leases:", [x for x in h("leasetable").get("leases", []) if x.get("key") == key], flush=True)
        travel(L, b["gx"] + 28, b["gz"] + 22, "laptop (there too)")
        until(lambda: anomalies(L, key) or anomalies(h, key), 30)
        time.sleep(5)
        ah, al = anomalies(h, key)[a0h:], anomalies(L, key)[a0l:]
        print(f"  laptop's building there: {pois(L, 200).get(key)}", flush=True)
        verdict("THE HOST LOGGED THE DIFFERENT BUILDING", len(ah) > 0, str(ah[:2]))
        verdict("THE LAPTOP LOGGED THE DIFFERENT BUILDING", len(al) > 0, str(al[:2]))
        er = [x_("errors") for x_ in (h, L)]
        print("  errors:", [(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er], flush=True)
    finally:
        for s_, v in gods.values():
            try: s_(f"set kaposztaleves.s.settings.god {v}"); s_(f"set {T}.LeaseTypeCheck True")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"LEASETYPE: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")

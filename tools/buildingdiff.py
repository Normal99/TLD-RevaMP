#!/usr/bin/env python3
"""Buildings one player has and the other hasn't (playtests 2026-10-10 and 2026-10-16: "some buildings show up for me
but not for the other player", same seed; nothing in the logs). Two machines over the relay: a building 0.4-3 km away; on the
laptop it is taken out of the world (`poihide`: what the joiner's game had there); both players go next to it → the
host logs "[anomaly] building differs between players" naming the building, the spot, and that the laptop has no
building there (Entities.PoiCensus); nothing logged for the buildings both have.
  tools/buildingdiff.py [--no-build] [--relay iad|off] [--old: no building check, before v0.68.1]"""
import sys, time, math, pathlib, os
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both

os.environ.setdefault("TLD_TEST_MODS", "CarTrailer,HeadUnit")   # the example mods stay installed
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

def census(s):
    return [x for x in s("mp anomalies")["lines"] if "building differs between players" in x]

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
            s_(f"set {T}.PoiCensusOn {not OLD}")
        print(f"  {'OLD (no building check)' if OLD else 'CURRENT BUILD'}", flush=True)
        time.sleep(10)
        ph, pl = pois(h), pois(L)
        cand = sorted((p for k, p in ph.items() if k in pl and 400 < p["d"] < 3000), key=lambda p: p["d"])
        if not cand: sys.exit(f"no building 0.4-3 km away on both (host {[(p['name'], p['d']) for p in ph.values()]})")
        b = cand[0]; key = b["key"]
        print(f"  building {b['name']} (type {b['id']}) at {key}, {b['d']} m away", flush=True)
        r = L(f"poihide {key}"); print("  [laptop] taken out:", r, flush=True)
        verdict("SETUP: THE LAPTOP HAS NO BUILDING THERE", "error" not in r)
        a0 = len(census(h))
        travel(h, b["gx"] + 25, b["gz"] + 25, f"host (at {b['name']})")
        travel(L, b["gx"] + 28, b["gz"] + 22, "laptop (there too)")
        until(lambda: len(census(h)) > a0, 30)
        new = census(h)[a0:]
        hit = [x for x in new if b["name"].replace("(Clone)", "") in x and "player 1" in x and "no building there" in x]
        verdict("THE HOST NAMED THE BUILDING THE LAPTOP HASN'T", bool(hit), str(new[:2]))
        verdict("NOTHING ELSE LOGGED (BUILDINGS BOTH HAVE)", len(new) == len(hit), str([x for x in new if x not in hit][:2]))
        print("  sent/in:", h(f"get {T}.PoiCensusIn"), L(f"get {T}.PoiCensusSent"), flush=True)
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        for s_, v in gods.values():
            try: s_(f"set kaposztaleves.s.settings.god {v}"); s_(f"set {T}.PoiCensusOn True")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"BUILDINGDIFF: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")

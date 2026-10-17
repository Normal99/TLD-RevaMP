#!/usr/bin/env python3
"""The missing diner, another route (NOTES 2026-10-14 (b): not reproduced by arrival or rejoin). A building the game
unloads when the player leaves and generates again on return: does a joiner who goes away and comes back always get
its buildings back? Two machines over the relay; the host stays at the site the whole time. The laptop (joiner) stands
at the site, goes --km away (teleport, then --away s there), comes back, --rounds times, by teleport and by a slow
staged walk back in 1 km hops. Each return: every building within 250 m of the site the same type and shown on both,
and none missing on the laptop that it had before.
  tools/poireturn.py [--no-build] [--relay iad|off] [--km 3,8,25] [--rounds 2] [--away 20] [--sites 2]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, ROUNDS, AWAY, SITES = arg("--relay", "iad"), arg("--rounds", 2, int), arg("--away", 20, int), arg("--sites", 2, int)
KMS = [float(k) for k in arg("--km", "3,8,25").split(",")]
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def vz(s):
    v = s("get mainscript.s.visszarakva")["value"].strip("() ").split(",")
    return [float(x) for x in v]

def go_global(s, gx, gz, who, timeout=60):
    t0 = time.time()
    while time.time() - t0 < timeout:
        v = vz(s)
        gr = s(f"ground {gx + v[0]} {gz + v[2]}")
        y = gr["y"] + 1.5 if gr.get("hit") and gr.get("collider") in ("Terrain", "RoadCollider") else 400
        s(f"teleport {gx + v[0]} {y} {gz + v[2]}"); s("set mainscript.s.player.godModeTillGrounded True")
        time.sleep(0.6)
        q = s("gpos")
        if abs(q["gx"] - gx) < 30 and abs(q["gz"] - gz) < 30 and y != 400: return q
    raise RuntimeError(f"{who}: could not reach {gx:.0f},{gz:.0f}")

def shown(s, gx, gz):
    u = s("gpos"); ux, uz = u["ux"] + (gx - u["gx"]), u["uz"] + (gz - u["gz"])
    return {p["key"]: p for p in s(f"pois {ux} {uz} 250")["pois"]}

def nearest_sites(h, n):
    u = h("gpos")
    ps = sorted(h(f"pois {u['ux']} {u['uz']} 3000")["pois"], key=lambda p: p["d"])
    out = []
    for p in ps:
        if p["d"] < 200: continue
        gx, gz = p["gx"], p["gz"]
        if any(math.hypot(gx - a, gz - b) < 400 for a, b, _ in out): continue
        out.append((gx, gz, p["name"]))
        if len(out) >= n: break
    return out

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
        sites = nearest_sites(h, SITES)
        print("  sites:", sites, flush=True)
        if not sites: sys.exit("NO BUILDINGS NEAR THE START")
        for si, (sx, sz, name) in enumerate(sites):
            go_global(h, sx + 15, sz + 15, "host"); go_global(L, sx + 18, sz + 12, "laptop"); time.sleep(20)
            base_h, base_l = shown(h, sx, sz), shown(L, sx, sz)
            print(f"  site {si} ({name}): host {sorted((p['name'], p['shown']) for p in base_h.values())}", flush=True)
            print(f"  site {si}: laptop {sorted((p['name'], p['shown']) for p in base_l.values())}", flush=True)
            for km in KMS:
                for rnd in range(ROUNDS):
                    staged = rnd % 2 == 1
                    go_global(L, sx + km * 1000, sz, "laptop"); time.sleep(AWAY)
                    if staged:
                        for k in range(int(km), 0, -1): go_global(L, sx + k * 1000 - 500, sz, "laptop"); time.sleep(4)
                    go_global(L, sx + 18, sz + 12, "laptop"); time.sleep(20)
                    now_h, now_l = shown(h, sx, sz), shown(L, sx, sz)
                    bad = [(k, (now_h.get(k) or {}).get("name"), (now_h.get(k) or {}).get("shown"), (now_l.get(k) or {}).get("name"), (now_l.get(k) or {}).get("shown"))
                           for k in set(now_h) | set(now_l)
                           if (now_h.get(k) or {}).get("name") != (now_l.get(k) or {}).get("name") or not (now_l.get(k) or {}).get("shown")]
                    lost = [k for k, p in base_l.items() if p.get("shown") and not (now_l.get(k) or {}).get("shown")]
                    verdict(f"SITE {si} BACK FROM {km:g} KM ({'staged' if staged else 'teleport'}, round {rnd + 1}): THE LAPTOP HAS EVERY BUILDING",
                            not bad and not lost, f"{len(now_l)} buildings; differing {bad[:3]}; lost since the start {lost[:3]}")
                    an = L("mp anomalies")
                    if "building" in str(an).lower(): print("  laptop anomalies:", str(an)[:400], flush=True)
        er = [x("errors") for x in (h, L)]
        print("  errors:", [(e["errors"], e["exceptions"], (e.get("kinds") or [])[:3]) for e in er], flush=True)
        verdict("NO ERRORS", all(e["exceptions"] == 0 for e in er))
    finally:
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"POIRETURN: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")

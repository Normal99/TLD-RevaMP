#!/usr/bin/env python3
"""The missing diner (playtest 2026-10-09/10 (b)): one player saw the diner's items on bare ground. All three machines
had asked the server for that building's lease, so each had generated a building at that spot. Two machines over the
relay, the same game build: does a player who arrives the way joiners do (teleported next to the host, or rejoining —
a rejoin regenerates the world) get the same buildings as the host? At each site, within --radius:
  same buildings: spot, type, position (worldhash "pois" rows, exact) and the roads they align to
  shown: every building within 250 m of both players has its building object loaded and active on both
Sites: the nearest --buildings buildings around the start and around each --km east (the host teleports there, the\nlaptop teleports next to it), then a rejoin at the last one.
  tools/poicensus.py [--no-build] [--relay iad|off] [--km 20] [--buildings 2] [--radius 600] [--settle 30]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, RADIUS, SETTLE = arg("--relay", "iad"), arg("--radius", 600.0, float), arg("--settle", 30, int)
KMS = [float(k) for k in arg("--km", "20").split(",")]
BUILDINGS = arg("--buildings", 2, int)   # visited per area (the nearest more than 300 m away)
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def vz(s):
    v = s("get mainscript.s.visszarakva")["value"].strip("() ").split(",")
    return [float(x) for x in v]

def go_global(s, gx, gz, who, timeout=60):
    """teleport to a GLOBAL spot and wait for ground there (re-aimed after floating-origin shifts)"""
    t0 = time.time()
    while time.time() - t0 < timeout:
        v = vz(s)
        g = s(f"ground {gx + v[0]} {gz + v[2]}")
        y = g["y"] + 1.5 if g.get("hit") and g.get("collider") in ("Terrain", "RoadCollider") else 400
        s(f"teleport {gx + v[0]} {y} {gz + v[2]}"); s("set mainscript.s.player.godModeTillGrounded True")
        time.sleep(0.6)
        q = s("gpos")
        if abs(q["gx"] - gx) < 30 and abs(q["gz"] - gz) < 30 and y != 400:
            print(f"  {who} at {q['gx']:.0f},{q['gz']:.0f}", flush=True); return q
    raise RuntimeError(f"{who}: could not reach {gx:.0f},{gz:.0f}")

def rows(s, gx, gz, cat):
    h = s(f"worldhash {gx} {gz} {RADIUS} detail")
    for k, v in h.items():
        if k.split(":")[-1] == cat: return v.get("rows", []), v["n"]
    return [], 0

def shown(s, near_gx, near_gz):
    u = s("gpos"); ux, uz = u["ux"] + (near_gx - u["gx"]), u["uz"] + (near_gz - u["gz"])
    return {p["key"]: p for p in s(f"pois {ux} {uz} 250")["pois"]}

def compare(tag, h, L, gx, gz):
    for cat in ("pois", "roads"):
        a, na = rows(h, gx, gz, cat); b, nb = rows(L, gx, gz, cat)
        sa, sb = set(a), set(b)
        verdict(f"{tag}: THE SAME {cat.upper()} ON BOTH ({na} here)", sa == sb,
                f"host {na}, laptop {nb}; only on the host {sorted(sa - sb)[:4]}, only on the laptop {sorted(sb - sa)[:4]}")
    ph, pl = shown(h, gx, gz), shown(L, gx, gz)
    print(f"  {tag}: buildings within 250 m — host {[(p['name'], p['id'], p['d'], p['shown']) for p in ph.values()]}", flush=True)
    print(f"  {tag}: buildings within 250 m — laptop {[(p['name'], p['id'], p['d'], p['shown']) for p in pl.values()]}", flush=True)
    bad = [(k, (ph.get(k) or {}).get("name"), (ph.get(k) or {}).get("shown"), (pl.get(k) or {}).get("name"), (pl.get(k) or {}).get("shown"))
           for k in set(ph) | set(pl)
           if (ph.get(k) or {}).get("id") != (pl.get(k) or {}).get("id") or not (ph.get(k) or {}).get("shown") or not (pl.get(k) or {}).get("shown")]
    verdict(f"{tag}: EVERY BUILDING NEARBY THE SAME TYPE AND SHOWN ON BOTH ({len(ph)})", not bad, f"differs (key, host name/shown, laptop name/shown): {bad[:4]}")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    gods = {}
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, s_("get kaposztaleves.s.settings.god").get("value")); s_("set kaposztaleves.s.settings.god True")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        home = h("gpos")
        time.sleep(SETTLE)
        compare("START (joined next to the host)", h, L, home["gx"], home["gz"])
        gx, gz = home["gx"], home["gz"]
        for km in [0.0] + KMS:
            if km > 0:   # a far point: the host goes first, its buildings are listed from there
                go_global(h, home["gx"] + km * 1000, home["gz"], f"host ({km:.0f} km)"); time.sleep(10)
            u = h("gpos")
            near = sorted((p for p in h(f"pois {u['ux']} {u['uz']} 4000")["pois"] if p["d"] > 300), key=lambda p: p["d"])[:BUILDINGS]
            print(f"  {km:.0f} km: buildings to visit {[(p['name'], p['d']) for p in near]}", flush=True)
            for p in near:
                gx, gz = p["gx"], p["gz"]
                go_global(h, gx + 40, gz + 40, f"host (at {p['name']})")
                go_global(L, gx + 46, gz + 40, f"laptop (teleported next to the host)")
                time.sleep(SETTLE)
                compare(f"{km:.0f} KM {p['name']} (teleported)", h, L, gx, gz)
        # a rejoin there: the laptop's world is loaded again and it is put next to the host (the JoinSpot)
        print("  laptop leaves:", L("mp stop"), flush=True); time.sleep(10)
        sid = h("net me")["steamId"]
        L(f"mp joinid {sid}"); t0 = time.time(); ok = False
        while time.time() - t0 < 120:
            try:
                if L("mp status")["client"]["ready"]: ok = True; break
            except Exception: pass
            time.sleep(1)
        verdict("REJOINED", ok)
        if ok:
            L("set TLDRevamp.TestSafety.BlockAutoSave True"); L("set kaposztaleves.s.settings.god True")
            q = L("gpos"); print(f"  laptop placed at {q['gx']:.0f},{q['gz']:.0f} (host at {h('gpos')['gx']:.0f})", flush=True)
            time.sleep(SETTLE)
            compare("LAST BUILDING (rejoined)", h, L, gx, gz)
        er = [x_("errors") for x_ in (h, L)]
        print("  errors:", [(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er], flush=True)
    finally:
        for s_, v in gods.values():
            try: s_(f"set kaposztaleves.s.settings.god {v}")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"POICENSUS: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")

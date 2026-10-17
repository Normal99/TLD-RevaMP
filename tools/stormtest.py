#!/usr/bin/env python3
"""Sandstorms are the host's (v0.64.14, Entities.StormSync; storms back on the beta: v0.64.15, Fixes.Sandstorms). Two machines over the relay: desktop hosts, laptop joins.
Each machine ran its own spawner (random storms around its own player): one player stood in a storm the other could
not see. Storm rows: [id, prefab, gx, gz, size, speed, xdir, ydir, life, distanceLerp, dist (fade measure), destroyDist]
  0 after joining: the laptop has exactly the host's storms (its own, from its save, gone)
  1 the host's storm comes up between the players: the laptop has it - same place (< 2 m), size, direction, speed
  2 20 s later, drifting: still together (< 2 m)
  3 the laptop's spawner comes due: no storm of its own
  4 the laptop stands in the storm, the host 300 m off: the host fades it by the nearest player (the laptop)
  4b the host's spawner comes due (the host off on its own, the laptop on sand): a storm comes up 1500 m from a
     player, on both machines
  5 a storm off sand fades on the host: gone on the laptop too
  6 the host's storms end: gone on the laptop
  Both start on sand (storms fade off it).
  tools/stormtest.py [--no-build] [--relay iad|off]"""
import sys, os, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
VERDICTS = []

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def compare(tag, h, L):
    hs, ls = h("mp storms"), L("mp storms")
    hb = {r[0]: r for r in hs["storms"]}; lb = {r[0]: r for r in ls["storms"]}
    worst = 0.0; same = set(hb) == set(lb) and len(hb) == len(hs["storms"]) and len(lb) == len(ls["storms"])
    for i in hb:
        if i in lb:
            # the two machines are read one after the other (the relay: ~0.25 s): a storm drifting 10 m/s moves 2.5 m
            # in between — measured beyond that drift
            worst = max(worst, max(0.0, math.hypot(hb[i][2] - lb[i][2], hb[i][3] - lb[i][3]) - hb[i][5] * 0.3))
            same = same and abs(hb[i][4] - lb[i][4]) < 1e-3 and abs(hb[i][5] - lb[i][5]) < 1e-2 and abs(hb[i][6] - lb[i][6]) < 1e-3
    print(f"  {tag}: host {hs['storms']}\n  {' ' * len(tag)}  laptop {ls['storms']}\n  {' ' * len(tag)}  worst {worst:.2f} m beyond the drift between the reads; host sent {hs['sent']}, laptop applied {ls['applied']} made {ls['made']} removed {ls['removed']} snaps {ls['snaps']}", flush=True)
    return same, worst, hs, ls

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    start = h("gpos")
    sand = h("mp storms sand"); print("  sand:", sand, flush=True)
    h(f"tpground {sand['gx']} {sand['gz']}"); L(f"tpground {sand['gx'] + 10} {sand['gz']}"); time.sleep(8)
    print("  before: host", h("mp storms"), "\n          laptop", L("mp storms"), flush=True)
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    gods = {}
    for nm, s_ in (("host", h), ("laptop", L)):
        gods[nm] = (s_, s_("get kaposztaleves.s.settings.god").get("value"))
        s_("set kaposztaleves.s.settings.god True")
    try:
        time.sleep(4)
        same, worst, hs, ls = compare("0 joined", h, L)
        verdict("0 THE LAPTOP HAS THE HOST'S STORMS", same and worst < 2, f"host {len(hs['storms'])}, laptop {len(ls['storms'])}")
        hp = h("gpos")
        print("  host spawns a storm 20 m off:", h(f"mp storms spawn {hp['gx'] + 20} {hp['gz']}"), flush=True)
        time.sleep(3)
        same, worst, hs, ls = compare("1 after the spawn", h, L)
        verdict("1 THE LAPTOP SEES THE HOST'S NEW STORM", same and len(hs["storms"]) >= 1 and worst < 2, f"worst {worst:.2f} m")
        time.sleep(20)
        same, worst, hs, ls = compare("2 20 s later", h, L)
        verdict("2 STILL TOGETHER, DRIFTING", same and worst < 2, f"worst {worst:.2f} m")
        n0 = len(hs["storms"])
        print("  laptop spawner due:", L("mp storms timer"), flush=True)
        time.sleep(4)
        same, worst, hs, ls = compare("3 the laptop's spawner came due", h, L)
        verdict("3 NO STORM OF THE LAPTOP'S OWN", same and len(ls["storms"]) == len(hs["storms"]) and ls["sandstorms"]["spawns"] == 0,
                f"host {len(hs['storms'])}, laptop {len(ls['storms'])}, laptop spawns {ls['sandstorms']['spawns']}")
        # 4: the laptop at the newest storm, the host 300 m off
        st = max(hs["storms"], key=lambda r: r[0])
        L(f"tpground {st[2]} {st[3]}")
        h(f"tpground {st[2] - 600} {st[3]}"); time.sleep(6)   # from the storm itself (run 4: 300 m from the host's spot left it 196 m off the drifting storm)
        hs = h("mp storms"); row = [r for r in hs["storms"] if r[0] == st[0]]
        lp = L("gpos"); hp2 = h("gpos")
        if not row: verdict("4 THE HOST FADES BY THE NEAREST PLAYER", False, "the storm is gone on the host")
        else:
            r = row[0]
            dl = math.hypot(r[2] - lp["gx"], r[3] - lp["gz"]); dh = math.hypot(r[2] - hp2["gx"], r[3] - hp2["gz"])
            print(f"  4: storm {r}; laptop {dl:.0f} m from it, host {dh:.0f} m; the host's fade measure {r[10]} (destroyDist {r[11]})", flush=True)
            verdict("4 THE HOST FADES BY THE NEAREST PLAYER", abs(r[10] - dl) < 15 and dh > 250, f"measure {r[10]} m = the laptop's {dl:.0f} m, not the host's {dh:.0f} m")
        n1 = len(h("mp storms")["storms"])
        print("  host spawner due:", h("mp storms timer"), flush=True)
        time.sleep(4)
        same, worst, hs, ls = compare("4b the host's spawner came due", h, L)
        new = [r for r in hs["storms"] if r[0] > st[0]]
        ok = False
        if new:
            r = new[0]; dl = math.hypot(r[2] - lp["gx"], r[3] - lp["gz"]); dh = math.hypot(r[2] - hp2["gx"], r[3] - hp2["gz"])
            print(f"  4b: new storm {r}: {dl:.0f} m from the laptop, {dh:.0f} m from the host", flush=True)
            ok = same and min(abs(dl - 1500), abs(dh - 1500)) < 40 + r[5] * 5   # spawned 1500 m off, drifting since (~4 s)
        verdict("4b THE HOST'S SPAWNER: A STORM ON BOTH", ok, f"storms {n1} -> host {len(hs['storms'])}, laptop {len(ls['storms'])}")
        print("  host clears its storms:", h("mp storms clear"), flush=True)
        time.sleep(3)
        # 5: off sand (the start)
        h(f"tpground {start['gx']} {start['gz']}"); time.sleep(6)
        h(f"mp storms spawn {start['gx'] + 20} {start['gz']}")
        seen_l = until(lambda: len(L("mp storms")["storms"]) > 0, 6, 0.3)
        gone = until(lambda: len(h("mp storms")["storms"]) == 0 and len(L("mp storms")["storms"]) == 0, 30, 0.5)
        hs = h("mp storms")
        verdict("5 OFF SAND: FADES ON THE HOST, GONE ON THE LAPTOP", gone and hs["sandstorms"]["fadedOffBiome"] >= 1,
                f"laptop saw it first {bool(seen_l)}; faded off sand {hs['sandstorms']['fadedOffBiome']}")
        L(f"tpground {start['gx'] + 10} {start['gz']}"); time.sleep(4)
        h(f"tpground {sand['gx']} {sand['gz']}"); time.sleep(6)
        h(f"mp storms spawn {sand['gx'] + 20} {sand['gz']}"); time.sleep(3)
        print("  6: storms", h("mp storms")["storms"], L("mp storms")["storms"], flush=True)
        print("  host clears its storms:", h("mp storms clear"), flush=True)
        time.sleep(4)
        same, worst, hs, ls = compare("6 the host's storms ended", h, L)
        verdict("6 GONE ON THE LAPTOP", len(ls["storms"]) == len(hs["storms"]) == 0, f"host {len(hs['storms'])}, laptop {len(ls['storms'])}")
    finally:
        for nm, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", nm, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

#!/usr/bin/env python3
"""Single player, seed 141641351: the start gas station (NagyBenzinkut, 246.5,622) came up EMPTY on both machines in
leasepair --site 245 620 (the host spawned it at world start, left 3 km, came back). Here without multiplayer: items at
the station right after the start, after leaving 3 km, after coming back.
  TLD_SEED=141641351 tools/stationprobe.py"""
import sys, time, math, collections, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
SX, SZ, R = 246.5, 622.0, 110

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def travel(b, gx, gz, who):
    b("set mainscript.s.player.godModeTillGrounded True"); b(f"tpg {gx} {gz} hold")
    def grounded():
        g = b("gpos"); return "y" in b(f"ground {g['ux']} {g['uz']}")
    if until(grounded, 90, 1.0): b(f"tpground {gx} {gz}")
    else: b("tprelease")
    time.sleep(8); g = b("gpos"); print(f"  {who} at {g['gx']:.0f},{g['gz']:.0f}", flush=True)

def count(b, tag):
    v = b(f"mp sitestate {SX} {SZ} {R}")["items"]
    c = collections.Counter(it[5].replace("(Clone)", "") for it in v)
    p = [w for w in b(f"pois {b('gpos')['ux']} {b('gpos')['uz']} 4000")["pois"] if w["name"] == "NagyBenzinkut"]
    print(f"  {tag}: {len(v)} items within {R} m; station poi {[(round(w['gx']), round(w['gz']), w['items'], w['fixed']) for w in p]}; {c.most_common(8)}", flush=True)
    return len(v)

game.quit_game(); game.launch()
with game.ProtectedSession():
    seeded.restart_same_seed()
    b = Bridge(); b("set TLDRevamp.TestSafety.BlockAutoSave True")
    god = b("get kaposztaleves.s.settings.god").get("value"); b("set kaposztaleves.s.settings.god True")
    g = b("gpos"); print(f"  seed {seeded.SEED}, start at {g['gx']:.0f},{g['gz']:.0f}", flush=True)
    time.sleep(10); n0 = count(b, "start (+10 s)")
    travel(b, SX + 3000, SZ, "away")
    travel(b, SX - 8, SZ - 8, "back"); time.sleep(5); n1 = count(b, "back (+13 s)")
    print("VERDICT:", f"start {n0}, after leaving and coming back {n1}", flush=True)
    b(f"set kaposztaleves.s.settings.god {god if god is not None else False}")

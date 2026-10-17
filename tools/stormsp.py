#!/usr/bin/env python3
"""Sandstorms are back (v0.64.15, Fixes.Sandstorms). Desktop only, no session. The beta had the storm but no spawner
in the world: no storm ever came up. Storm rows: [id, prefab, gx, gz, size, speed, xdir, ydir, life, distanceLerp,
fadeDist, destroyDist]
  1 a new world has the spawner; its first storm is 30-75 min off (Normal, 0.68.4; 15-45 before)
  2 at the start (not sand): the spawner comes due, no storm (unless sand is within reach)
  3 on sand: the spawner comes due - a storm comes up 800 m off, heading at the player (within 25 deg)
  4 it reaches the player: the player stands in it (distanceLerp > 0.2); a screenshot
  5 a storm made off sand fades and is gone (lifeLossOnOtherBiome: ~10 s)
  tools/stormsp.py"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge

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

game.deploy_local()
game.quit_game(); game.launch()
with game.ProtectedSession():
    seeded.restart_same_seed()
    b = Bridge(); b("set TLDRevamp.TestSafety.BlockAutoSave True")
    god = b("get kaposztaleves.s.settings.god").get("value"); b("set kaposztaleves.s.settings.god True")
    try:
        s = b("mp storms"); print(" ", s, flush=True)
        sp = s.get("spawner")
        verdict("1 THE WORLD HAS A SPAWNER", sp is not None and s["sandstorms"]["spawners"] >= 1 and 0 < sp["next"] <= sp["max"] and (sp["min"], sp["max"]) == (1800, 4500),
                f"first storm in {sp and sp['next']} s (min {sp and sp['min']}, max {sp and sp['max']})")
        start = me = b("gpos"); here = b(f"mp storms biome {me['gx']} {me['gz']}")["biome"]
        print(f"  start biome {here}", flush=True)
        n0 = s["sandstorms"]["noSand"]
        b("mp storms timer"); time.sleep(2)
        s = b("mp storms")
        verdict("2 NOT SAND: NO STORM", here != 0 and len(s["storms"]) == 0 and s["sandstorms"]["noSand"] == n0 + 1 or here == 0,
                f"storms {len(s['storms'])}, no-sand tries {n0} -> {s['sandstorms']['noSand']}")
        sand = b("mp storms sand"); print("  sand:", sand, flush=True)
        b(f"tpground {sand['gx']} {sand['gz']}"); time.sleep(8)
        me = b("gpos"); print("  here:", me, "biome", b(f"mp storms biome {me['gx']} {me['gz']}"), flush=True)
        b("set mainscript.s.sandStormSpawn.radius 800"); b("set TLDRevamp.Fixes.Sandstorms.AimChance 1")   # aimed (0.68.4: 4 in 10 by default; stormfreq.py counts those)
        b("mp storms timer"); time.sleep(2)
        s = b("mp storms"); print(" ", s["storms"], s["sandstorms"], flush=True)
        st = s["storms"][0] if s["storms"] else None
        if st:
            d = math.hypot(st[2] - me["gx"], st[3] - me["gz"])
            to = math.degrees(math.atan2(me["gz"] - st[3], me["gx"] - st[2])); hd = math.degrees(math.atan2(st[7], st[6]))
            off = abs((hd - to + 180) % 360 - 180)
            verdict("3 ON SAND: A STORM COMES UP, HEADING AT THE PLAYER", abs(d - 800) < 30 and off <= 25.5, f"{d:.0f} m off, heading {off:.1f} deg off the player")
            got = until(lambda: next((r for r in b("mp storms")["storms"] if r[9] > 0.2), None), 200, 2)
            if got: print("  in it:", got, b("screenshot"), flush=True)
            verdict("4 IT REACHES THE PLAYER", got is not None, f"distanceLerp {got and got[9]}")
        else:
            verdict("3 ON SAND: A STORM COMES UP, HEADING AT THE PLAYER", False, "no storm")
        b("mp storms clear"); time.sleep(1)
        # 5: back at the start (not sand): a storm made there fades
        b(f"tpground {start['gx']} {start['gz']}"); time.sleep(6)
        f0 = b("mp storms")["sandstorms"]["fadedOffBiome"]
        b(f"mp storms spawn {start['gx']} {start['gz']}")
        t0 = time.time()
        gone = until(lambda: len(b("mp storms")["storms"]) == 0, 30, 0.5)
        s = b("mp storms")
        verdict("5 OFF SAND A STORM FADES", here != 0 and gone and s["sandstorms"]["fadedOffBiome"] == f0 + 1,
                f"gone after {time.time() - t0:.1f} s; faded off sand {f0} -> {s['sandstorms']['fadedOffBiome']}")
    finally:
        b(f"set kaposztaleves.s.settings.god {god if god is not None else False}")
        b("set TLDRevamp.Fixes.Sandstorms.AimChance 0.4")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

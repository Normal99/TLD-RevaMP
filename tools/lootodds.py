#!/usr/bin/env python3
"""How often items spawn in the world, from the game's own loot chances (TLDRevamp.LootLab.Odds): the expected count of
each item over the loot spawners of every building type in memory (prefabs), and over those in the world around
--stops places --km apart (scene, summed). Single player on this machine.
  TLD_TEST_MODS=CarTrailer,HeadUnit tools/lootodds.py [--no-build] [--stops 6] [--km 3] [--ids 39,267,9100,...]"""
import os
os.environ.setdefault("TLD_TEST_MODS", "CarTrailer,HeadUnit")
import sys, time, pathlib, re
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
STOPS, KM = arg("--stops", 6, int), arg("--km", 3.0, float)
IDS = arg("--ids", "39,267,9100,9101,9102,52,53,9200,174,194")
NAMES = {"39": "radio", "267": "radio", "9100": "head unit", "9101": "head unit", "9102": "head unit",
         "52": "game trailer", "53": "game trailer", "9200": "car trailer", "174": "IFA", "194": "IFA trailer"}

def parse(s):
    return {m.group(1): float(m.group(2)) for m in re.finditer(r"(\d+) \S+ ([\d.]+)", s.split(":", 1)[1])}

def groups(d):
    out = {}
    for k, v in d.items(): out[NAMES.get(k, k)] = out.get(NAMES.get(k, k), 0) + v
    return out

with game.ProtectedSession():
    if "--no-build" not in sys.argv: game.build()
    else: game.local_mods()
    game.quit_game(); game.launch(); game.newgame()
    b = Bridge(); time.sleep(5)
    pre = b(f"call TLDRevamp.LootLab.Odds {IDS} prefabs")["value"]
    print(" prefabs:", pre, flush=True)
    print(" prefabs by kind:", {k: round(v, 3) for k, v in groups(parse(pre)).items()}, flush=True)
    total = {}
    g0 = b("gpos")
    for i in range(STOPS):
        if i: b(f"tpground {g0['gx'] + i * KM * 1000} {g0['gz']}"); time.sleep(25)
        sc = b(f"call TLDRevamp.LootLab.Odds {IDS} scene")["value"]
        print(f" stop {i}:", sc, flush=True)
        for k, v in parse(sc).items(): total[k] = total.get(k, 0) + v
    print(" world (summed over the stops) by kind:", {k: round(v, 3) for k, v in groups(total).items()}, flush=True)
    game.quit_game()

#!/usr/bin/env python3
"""Single player: suspension travel of each wheel as the network state packs it (1 byte, 2 mm steps, ±25.4 cm).
Copies showed wheels 7-20 cm off (leasepair 3b) with front/rear always 2.5 cm apart → a clamp? Spawns the cars from
leasepair, lets them settle, prints travel per wheel and how much a ±0.254 m clamp would cut.
  tools/wheelprobe.py [names...]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
CARS = sys.argv[1:] or ["Car09full", "Car10Full", "Car09full_slide", "Car06Full"]
game.quit_game(); game.launch()
with game.ProtectedSession():
    game.newgame(); b = Bridge(); b("set TLDRevamp.TestSafety.BlockAutoSave True")
    god = b("get kaposztaleves.s.settings.god").get("value"); b("set kaposztaleves.s.settings.god True")
    time.sleep(5)
    worst = 0.0
    for name in CARS:
        print(f"  {name}: {b(f'devspawn {name}')}", flush=True); time.sleep(12)   # settle on its suspension
        cars = b("wheelinfo 15")["cars"]
        for c in cars:
            for w in c["wheels"]:
                cut = max(0.0, abs(w[1]) - 0.254); worst = max(worst, cut)
                print(f"    {c['name']:<24} {w[0]:<14} travel {w[1]:+.3f}  center.y {w[2]:+.3f}  suspension {w[3]:.3f}  grounded {w[4]}  pair {w[5]}  frac {w[6]:.3f} mesh {w[7]:.3f}  clamp cuts {cut:.3f}", flush=True)
        g = b("gpos"); b(f"tpground {g['gx'] + 60} {g['gz']}"); time.sleep(4)   # next car on clear ground (loaded, on the ground)
    print(f"VERDICT: the ±0.254 m clamp cuts up to {worst:.3f} m", flush=True)
    b(f"set kaposztaleves.s.settings.god {god if god is not None else False}")
game.quit_game()

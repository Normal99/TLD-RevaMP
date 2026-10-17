#!/usr/bin/env python3
"""Desktop, running game: which setup step leaves a car unable to pull away under power?
A new car01; each phase drives it with our driver (autodrive) for 4 s from rest and reports metres moved plus the
engine's output/starter state. Phases: 1 straight away (engine cranked by the same drive); 2 after autodrive off +
brakecar; 3 after autodrive off alone. No MP session needed.  tools/pullaway.py"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from bench import Bridge

b = Bridge()
def g(p):
    try: return b("get " + p)["value"]
    except Exception: return None
def pos(): return tuple(float(x) for x in g("mainscript.s.player.Car.transform.position").split(","))
C, E = "mainscript.s.player.Car.", "mainscript.s.player.Car.Engine."

if g("mainscript.s.player.Bsitting") is True: b("driveout"); time.sleep(1)
b("set kaposztaleves.s.settings.god True"); shift0 = g("settingsscript.s.S.BAutoShift"); b("set settingsscript.s.S.BAutoShift True")
rp = b("roadpoint"); gy = b(f"ground {rp['x']} {rp['z']}")["y"]
print("spawn:", b(f"spawncar 2 0 {rp['x']} {gy + 0.6} {rp['z']} {rp['yaw']}")); time.sleep(3)
b(f"tpg {rp['x'] + 3} {rp['z']}"); time.sleep(3)
seats = b("seatraw")["seats"]; tgt = next(s["i"] for s in seats if s["main"] == "null" and s["d0"]); idx = next(s["i"] for s in seats if s["main"] == f"#{tgt}")
for _ in range(4):
    b(f"sitseat {idx} redirect"); time.sleep(1)
    if g("mainscript.s.player.Bsitting") is True: break
print("fuel:", b("fuelup"), "handbrake:", b("handbrake off"))

def drive(label):
    p0 = pos(); b("autodrive on 10")
    for _ in range(8): time.sleep(0.5)
    moved = math.dist(p0, pos())
    state = {k: g(E + k) for k in ("running", "rpm", "actualOutput", "start", "starting")}
    state.update({k: g(C + k) for k in ("starting", "startingValue", "ignition", "gear", "ithrottle")})
    print(f"{label}: moved {moved:.1f} m  {{{', '.join(f'{k}: {round(v, 2) if isinstance(v, float) else v}' for k, v in state.items())}}}", flush=True)
    return moved

m1 = drive("1 straight away")
b("autodrive off"); b("brakecar"); time.sleep(2)
m2 = drive("2 after off + brakecar")
b("autodrive off"); time.sleep(2)
m3 = drive("3 after off alone")
b("autodrive off"); b("brakecar"); b("handbrake on")
b("set kaposztaleves.s.settings.god False"); b(f"set settingsscript.s.S.BAutoShift {shift0}")
print(f"\nPULL-AWAY: straight {m1:.1f} m | after off+brakecar {m2:.1f} m | after off {m3:.1f} m")

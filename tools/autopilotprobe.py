#!/usr/bin/env python3
"""Desktop only: Killenger's Autopilot (beta port, run by TLDRevamp's ModHost) drives a car01 on the road for 60 s.
Prints the mod host status, the autopilot's state/messages and the car's progress every 3 s; the verdict is distance
driven, average speed and time spent off the road (distance to the nearest road bone).
Test tooling only — the port is never part of TLD Revamp's public release.
  tools/autopilotprobe.py [--no-build] [seconds]"""
import sys, time, math, subprocess, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge

ROOT = pathlib.Path(__file__).resolve().parents[1]
SECONDS = next((int(a) for a in sys.argv[1:] if a.isdigit()), 60)

if game.running(): game.quit_game()   # first: never copy DLLs under a running game
if "--no-build" not in sys.argv:
    subprocess.run(["bash", str(ROOT / "tools/build.sh")], check=True)
game.launch()
with game.ProtectedSession():
    seeded.restart_same_seed(); b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True")
    print("mods:", b("mods"), flush=True)
    shift0 = b("get settingsscript.s.S.BAutoShift")["value"]
    b("set settingsscript.s.S.BAutoShift True"); b("set kaposztaleves.s.settings.god True")
    rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
    cx, cz = rp["x"], rp["z"]   # ON the road: the nearest main-road point (20 m further along left curvy roads)
    g = b(f"ground {cx} {cz}")
    b(f"teleportyaw {cx + 5} {g['y'] + 1.5} {cz} 270"); time.sleep(1)
    b(f"spawncar 2 0 {cx} {g['y'] + 0.6} {cz} {yaw}"); time.sleep(4)   # a new car01 (spawnpile's plain Spawn left random wrecks)
    for _ in range(4):
        b("drivein"); time.sleep(1)
        if b("get mainscript.s.player.Bsitting")["value"] is True: break
    print("fuel:", b("fuelup"), "handbrake:", b("handbrake off"), flush=True)
    # the autopilot only engages with the engine running and the car not rolling back: crank it the way our driver does
    # (auto gearbox: holding throttle starts the engine), then stop
    b("autodrive on 3")
    for _ in range(20):
        time.sleep(0.5)
        if b("get mainscript.s.player.Car.Engine.running")["value"] is True: break
    b("autodrive off"); b("brakecar"); time.sleep(1.5)
    print("engine running:", b("get mainscript.s.player.Car.Engine.running")["value"], "car at", b("get mainscript.s.player.Car.transform.position")["value"], "road point", (round(cx, 1), round(cz, 1)), flush=True)
    if "--handover" in sys.argv:
        # the player takes it from here (presses F10); the player's own settings back as they were
        b(f"set settingsscript.s.S.BAutoShift {shift0}"); b("set kaposztaleves.s.settings.god False")
        b("banner 20 AUTOPILOT TEST — you're in the driver seat, engine running, on the road.\\nPress F10 to engage Killenger's Autopilot" + ("" if shift0 is True else " (needs the AUTOMATIC gearbox: switch it on in settings)") + ".")
        print("HANDED OVER — player's auto gearbox setting:", shift0, flush=True)
        sys.exit(0)
    try:
        print("autopilot toggle:", b("call TLDAutopilot.Plugin.Host.Session.Toggle"), flush=True)
    except Exception as e:
        print("TOGGLE FAILED:", e, flush=True)
    # the first 6 s engaged: what the autopilot commands next to what the car does (why "stuck, wheels not moving")
    def g(path):
        try: return b("get " + path)["value"]
        except Exception: return "ERR"
    C = "mainscript.s.player.Car."
    for i in range(12):
        time.sleep(0.5)
        cmd = {k: g("TLDAutopilot.Plugin.Host.Session.LastCommand." + k) for k in ("ApplyControls", "Throttle", "Brake", "Handbrake")}
        car = {k: g(C + k) for k in ("ithrottle", "ibrake", "BhandBrake", "gear", "speed")}
        car["rpm"] = g(C + "Engine.rpm")
        try: wh = [(w["n"], w["motor"], w["brake"], w["rpm"]) for w in b("autodrive")["wheels"]]
        except Exception: wh = "?"
        fmt = lambda d: {k: (round(v, 2) if isinstance(v, float) else v) for k, v in d.items()}
        print(f"  {i * 0.5 + 0.5:3.1f}s {g('TLDAutopilot.Plugin.Host.Ap.State')} cmd {fmt(cmd)} car {fmt(car)} wheels {wh}", flush=True)
    t0 = time.time(); last = None; dist = 0.0; off = 0; samples = 0; vmax = 0.0
    while time.time() - t0 < SECONDS:
        time.sleep(3)
        pos = b("get mainscript.s.player.transform.position")["value"]
        p = tuple(float(x) for x in pos.split(","))
        if last: dist += math.dist(p[::2], last[::2])
        last = p
        r = b("roadpoint"); d_road = math.dist((p[0], p[2]), (r["x"], r["z"]))
        samples += 1; off += d_road > 8
        try: state = b("get TLDAutopilot.Plugin.Host.Ap.State")["value"]
        except Exception as e: state = f"? {e}"
        try: msg = b("get TLDAutopilot.Plugin.Host.Session.LastError")["value"]
        except Exception: msg = ""
        kmh = abs(float(b("get mainscript.s.player.Car.speed")["value"] or 0))
        vmax = max(vmax, kmh)
        print(f"  t={time.time() - t0:4.0f}s  state {state}  {kmh:5.1f} km/h  driven {dist:6.0f} m  road centre {d_road:4.1f} m away  {msg}", flush=True)
    avg = dist / max(1.0, time.time() - t0) * 3.6
    ok = dist > 200 and off <= samples * 0.2
    print(f"\nAUTOPILOT {'DRIVES' if ok else 'FAILED'}: {dist:.0f} m in {SECONDS} s (avg {avg:.0f} km/h, max {vmax:.0f}), off the road {off}/{samples} samples")
    try: b("call TLDAutopilot.Plugin.Host.Session.Toggle")
    except Exception: pass
    b("set kaposztaleves.s.settings.god False"); b(f"set settingsscript.s.S.BAutoShift {shift0}")

#!/usr/bin/env python3
"""H4 (bench/CORE-CHECKLIST.md): the autopilot sees other players on foot. Two machines over the relay; the laptop's
car01 driven by Killenger's Autopilot (the beta port in the laptop's BepInEx/TLDLoaderMods: test tooling only), the
host standing on foot in the car's lane ahead. Twice: NEW (AutopilotBridge.SeePlayers on) and OLD (off: the autopilot
is told of no players).
  A  NEW: the autopilot is told of the host (Traffic.PedestriansAdded rises) and treats them as it treats a creature:
     it slows (its speed within 20 m of the host under 85 % of its speed 60-90 m before) and doesn't touch them (closest
     2 m or more). Run 3 showed it dodges and slows rather than stopping (Kind = Creature in Traffic.AddPedestrians).
  B  OLD: the autopilot is told of nobody (the count doesn't move)
  C  no errors
  tools/pedestrians.py [--no-build] [--relay iad|off] [--ahead 220]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, tp, clear_road, car_ready, g, road_at

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, AHEAD = arg("--relay", "iad"), arg("--ahead", 220.0, float)
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def pos(s): q = s("gpos"); return q["gx"], q["gz"]
def kmh(s): return abs(float(g(s, "mainscript.s.player.Car.speed") or 0))

def run(h, L, label, see):
    L(f"set TLDRevamp.AutopilotBridge.SeePlayers {see}")
    rp = L("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
    r0 = clear_road(L, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
    F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw))); R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
    cx, cz = r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8
    if not car_ready(L, cx, cz, yaw, f"{label} laptop"): return None
    L("set settingsscript.s.S.BAutoShift True"); L("handbrake off")
    L("autodrive on 3")   # the autopilot engages only with the engine running: crank it as our driver does, then stop
    until(lambda: g(L, "mainscript.s.player.Car.Engine.running") is True, 10)
    L("autodrive off"); L("brakecar"); time.sleep(1.5)
    # the host on foot in the car's lane, AHEAD m on ALONG THE ROAD (run 1: straight ahead was 90 m off a bending road)
    px, pz, fx, fz = r0["gx"], r0["gz"], F[0], F[1]
    for _ in range(int(AHEAD // 40)):
        rr = road_at(h, px + fx * 40, pz + fz * 40)
        hy = (math.sin(math.radians(rr["yaw"])), math.cos(math.radians(rr["yaw"])))
        if hy[0] * fx + hy[1] * fz < 0: hy = (-hy[0], -hy[1])
        px, pz, (fx, fz) = rr["gx"], rr["gz"], hy
    hx, hz = px + fz * 1.8, pz - fx * 1.8   # the right lane, as the car drives
    tp(h, hx, hz); time.sleep(3)
    hx, hz = pos(h)
    # the autopilot counts an unlocked cursor as the game paused (SessionGuards.IsGameTicking: mainscript.crsrLocked);
    # the laptop's window without focus has it unlocked (run 2: "game paused", a car engaged and never moving)
    L("set mainscript.s.crsrLocked True")
    p0 = L("traffic").get("pedestrians") or 0
    for k in range(3):   # run 1, OLD: "Autopilot: rolling backwards" (parked on a slope): brake, then again
        r = L("call TLDAutopilot.Plugin.Host.Session.Toggle"); print(f"  {label}: autopilot:", r, flush=True)
        if "ENGAGED" in str(r): break
        L("brakecar"); L("handbrake on"); time.sleep(2); L("handbrake off")
    t0, rows, passed, dmin = time.time(), [], False, 1e9
    d0 = math.hypot(cx - hx, cz - hz)
    while time.time() - t0 < 60:
        time.sleep(0.5)
        L("set mainscript.s.crsrLocked True")
        lx, lz = pos(L); v = kmh(L)
        d = math.hypot(lx - hx, lz - hz)
        dmin = min(dmin, d)
        rows.append((round(time.time() - t0, 1), round(d, 1), round(v, 1), round(dmin, 1)))
        if dmin < 12 and d > dmin + 5: passed = True; break   # it came close and went on by (the road bends: no "ahead" axis)
        if v < 3 and d < 60 and time.time() - t0 > 8: time.sleep(3); rows.append(("rest", round(math.hypot(pos(L)[0] - hx, pos(L)[1] - hz), 1), round(kmh(L), 1), None)); break
    try: L("call TLDAutopilot.Plugin.Host.Session.Toggle")
    except Exception: pass
    L("brakecar"); time.sleep(1)
    ped = L("traffic")
    print(f"  {label}: start {d0:.0f} m away; samples {rows[-8:]}; traffic {ped}", flush=True)
    at20 = next((v for (_, d, v, a) in rows if isinstance(d, float) and d <= 20), None)
    last = rows[-1]
    L("driveout"); time.sleep(1)
    far = [v for (_, d, v, _m) in rows if isinstance(d, float) and 60 <= d <= 90]
    near = [v for (_, d, v, _m) in rows if isinstance(d, float) and d <= 20]
    return {"fed": (ped.get("pedestrians") or 0) - p0, "kmhFar": max(far) if far else None, "kmhNear": min(near) if near else None, "dmin": dmin, "passed": passed, "restDist": last[1], "restKmh": last[2], "kmhAt20": at20, "rows": rows, "pedestrians": ped.get("pedestrians")}

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
        print("  laptop's mods:", L("mods"), flush=True)
        new = run(h, L, "NEW", True)
        old = run(h, L, "OLD", False)
        if new: verdict("A NEW: TOLD OF THE HOST, SLOWS, DOESN'T TOUCH THEM", new["fed"] > 0 and new["kmhFar"] and new["kmhNear"] is not None
                        and new["kmhNear"] < 0.85 * new["kmhFar"] and new["dmin"] >= 2,
                        f"fed {new['fed']}, {new['kmhFar']} km/h at 60-90 m -> {new['kmhNear']} within 20 m, closest {new['dmin']:.1f} m, passed {new['passed']}")
        else: verdict("A NEW: TOLD OF THE HOST, SLOWS, DOESN'T TOUCH THEM", False, "no car")
        if old: verdict("B OLD: TOLD OF NOBODY", old["fed"] == 0, f"fed {old['fed']}, {old['kmhFar']} -> {old['kmhNear']} km/h, closest {old['dmin']:.1f} m")
        else: verdict("B OLD: TOLD OF NOBODY", False, "no car")
        er = [x_("errors") for x_ in (h, L)]
        verdict("C NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        L("set TLDRevamp.AutopilotBridge.SeePlayers True")
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
print(f"PEDESTRIANS: {sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} PASS")

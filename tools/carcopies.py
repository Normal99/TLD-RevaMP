#!/usr/bin/env python3
"""What other players' cars cost the host (architecture §9a "profiling leads": ~1,000 objects a car, no LOD, lamps with
shadows). Two machines over the relay; the laptop spawns N cars around the host (its own: copies on the host). The host
measures its frame phases (FrameBreakdown: Camera.Render, physics, scripts, the main thread) for WINDOW s each:
  1  far spot, no copies          2  near spot, no copies        3  near, N copies 20-45 m around
  4  near, N copies, far copies without shadows (Entities.CopyFarShadowsM 10: all of them)
  5  night: copies with shadows   6  night: without              7  the far spot (copies 600 m away)
Prints a table and what the copies have live (`mp copycost`). A measurement: no PASS/FAIL beyond the run being valid.
  tools/carcopies.py [--no-build] [--relay iad|off] [--cars 15] [--window 20]"""
import sys, time, math, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, tp, CAR01

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, N, WINDOW = arg("--relay", "iad"), arg("--cars", 15, int), arg("--window", 20, int)
PH = ["Main Thread", "PlayerLoop", "Camera.Render", "BehaviourUpdate", "FixedBehaviourUpdate", "Physics.Simulate", "Physics.Processing"]

def measure(h, label):
    h("reset"); time.sleep(WINDOW)
    bd = h("breakdown"); st = h("stats")
    m = {x["m"]: float(x["avgMsAll"]) for x in bd["markers"] if x["valid"]}
    row = {"label": label, "p50": st.get("p50ms"), "p99": st.get("p99ms"), **{k: m.get(k) for k in PH}}
    print("  " + label.ljust(34) + "  ".join(f"{k.split('.')[-1][:10]} {row[k]:.2f}" if row[k] is not None else f"{k[:10]} -" for k in PH) + f"  p99 {row['p99']}", flush=True)
    return row

def night(s, on, keep={}):
    if on:
        keep["tek"] = float(s("get napszakvaltakozas.s.tekeres")["value"])
        t, dt_ = float(s("get napszakvaltakozas.s.t")["value"]), float(s("get napszakvaltakozas.s.dt")["value"])
        s(f"set napszakvaltakozas.s.tekeres {keep['tek'] + (dt_ + 20 - t)}")
    elif "tek" in keep: s(f"set napszakvaltakozas.s.tekeres {keep['tek']}")

L = Agent()
ensure_both("--no-build" not in sys.argv)
rows, valid = [], True
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    # keep drawing without focus (run 1: the host's window lost it and ran at 1 fps in 6 of 7 windows)
    for s_ in (h, L): s_("bg on")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
        rp = h("roadpoint"); x, z, yaw = rp["gx"], rp["gz"], rp["yaw"]
        fx, fz = math.sin(math.radians(yaw)), math.cos(math.radians(yaw))
        far = (x + fx * 600, z + fz * 600)
        tp(h, *far); tp(L, x + 5, z); time.sleep(8)
        rows.append(measure(h, "1 far spot, no copies"))
        tp(h, x, z); time.sleep(8)
        rows.append(measure(h, "2 near, no copies"))
        for i in range(N):
            a = i * 2 * math.pi / N; r = 20 + 25 * (i % 2)
            L(f"spawncarg {CAR01} 0 {x + math.cos(a) * r:.1f} {z + math.sin(a) * r:.1f} {math.degrees(a) % 360:.0f}"); time.sleep(0.7)
        time.sleep(15)
        cc = h("mp copycost"); print("  copies on the host:", json.dumps(cc), flush=True)
        if cc["cars"] < N: valid = False; print(f"  INVALID: the host has {cc['cars']} copies of {N}", flush=True)
        rows.append(measure(h, f"3 near, {cc['cars']} copies"))
        h("set TLDRevamp.Net.Entities.CopyFarShadowsM 10"); time.sleep(3)
        print("  lever:", json.dumps(h("mp copycost")), flush=True)
        rows.append(measure(h, "4 near, copies without shadows"))
        h("set TLDRevamp.Net.Entities.CopyFarShadowsM 0"); time.sleep(2)
        night(h, True); time.sleep(8)
        rows.append(measure(h, "5 night, copies with shadows"))
        h("set TLDRevamp.Net.Entities.CopyFarShadowsM 10"); time.sleep(3)
        rows.append(measure(h, "6 night, copies without shadows"))
        h("set TLDRevamp.Net.Entities.CopyFarShadowsM 0"); night(h, False); time.sleep(3)
        tp(h, *far); time.sleep(8)
        rows.append(measure(h, "7 far spot, copies 600 m away"))
        er = [x_("errors") for x_ in (h, L)]
        print("  errors:", [(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er], flush=True)
        lost = not L("mp status")["client"]["ready"]
        if lost: valid = False; print("  INVALID: the laptop dropped", flush=True)
        if any((r["p50"] or 0) > 100 for r in rows): valid = False; print("  INVALID: a window ran under 10 fps (the game throttled?)", flush=True)
    finally:
        h("set TLDRevamp.Net.Entities.CopyFarShadowsM 0")
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nCARCOPIES" + ("" if valid else " (INVALID)"))
print(json.dumps(rows, indent=1))

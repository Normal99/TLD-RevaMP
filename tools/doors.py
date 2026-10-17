#!/usr/bin/env python3
"""Building doors and gates in MP (two machines, relay iad by default). Finds a building with a door/gate near the
road (walking the host along it), both players next to it, then:
  1 OPEN   : the laptop swings it to one end (the game's own input call) → the host's is the same
  2 CLOSE  : the host swings it to the other end → the laptop's follows
  3 RETURN : the laptop goes 3 km away (its building unloads), the host opens it again, the laptop comes back → the
             re-spawned building shows the host's state, not a fresh closed door
  4 REJOIN : the laptop leaves the session, the host closes it, the laptop rejoins → it sees the closed door
  tools/doors.py [--no-build] [--relay iad|off]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP
from roadkit import until, g, tp

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY = arg("--relay", "iad")
if RELAY == "off": RELAY = None

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def unit(yaw): return (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
def addr(u): return f"{u['gen']} {u['id'][0]} {u['id'][1]} {u['id'][2]} {u['u']}"
def state(s, u):
    r = s(f"mp poiu {addr(u)}")
    return None if "error" in r else r
def short(st): return "not spawned" if st is None else f"x {st['x']:>7} y {st['y']:>7} slide {st['slide']:>5} turn {st['turn']}"

def axis(u):
    """the axis a player swings: rot X or Y (the wider range), else slide, else turn"""
    if "rot" in u["kind"]:
        return "x" if u["maxX"] - u["minX"] >= u["maxY"] - u["minY"] else "y"
    return "slide" if "slide" in u["kind"] else "turn"

def swing(s, u, to_max):
    """the game's own input calls, as a player dragging the mouse: 40 small moves (the game clamps at the ends)"""
    ax = axis(u)
    for _ in range(40):
        if ax == "x": s(f"mp poiuse {addr(u)} rot {-25 if to_max else 25} 0")      # xRot -= X * rotSens (no invX)
        elif ax == "y": s(f"mp poiuse {addr(u)} rot 0 {25 if to_max else -25}")
        elif ax == "slide": s(f"mp poiuse {addr(u)} slide {0.2 if to_max else -0.2}")
        else: s(f"mp poiuse {addr(u)} turn"); break
        time.sleep(0.03)
    return state(s, u)

def same(a, b):
    return a is not None and b is not None and all(abs(a[k] - b[k]) < 0.6 for k in ("x", "y", "slide")) and a["turn"] == b["turn"]
def val(st, u):
    ax = axis(u)
    return st[ax] if ax in ("x", "y", "slide") else st["turn"]

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        b = seeded_both(L)
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("relay only via", RELAY, b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
        b("mp host 27070")
        def join():
            if RELAY: L(f"mp joinid {b('net me')['steamId']}")
            else: L(f"mp join {DESKTOP_IP}:27070")
            return until(lambda: L("mp status")["client"]["ready"], 150)
        if not join(): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")

        # ---- a building with a door/gate
        rp = b("roadpoint"); D = unit(rp["yaw"]); x, z = rp["gx"], rp["gz"]
        u = None
        for step in range(14):
            time.sleep(4)
            us = [c for c in b("mp poius 250")["usables"] if c["kind"] and
                  ("rot" in c["kind"] and max(c["maxX"] - c["minX"], c["maxY"] - c["minY"]) >= 30 or "slide" in c["kind"])]
            if us:
                us.sort(key=lambda c: ("rot" not in c["kind"], c["dist"]))
                u = us[0]; break
            x, z = x + D[0] * 400, z + D[1] * 400
            r = b(f"tpground {x} {z}"); time.sleep(1); rp = b("roadpoint"); D = unit(rp["yaw"]); x, z = rp["gx"], rp["gz"]
            print(f"  no door yet, host 400 m on along the road (step {step + 1}): {tp(b, x, z)}", flush=True)
        if u is None: sys.exit("no building with a door/gate found along 5.6 km of road")
        print("door:", u, flush=True)
        ux, uz = u["at"][0], u["at"][2]
        print("  host next to it:", tp(b, ux + 3, uz + 3), "| laptop next to it:", tp(L, ux - 3, uz + 3), flush=True)
        if not until(lambda: state(L, u) is not None, 40): sys.exit("the laptop never spawned that building")
        s0b, s0l = state(b, u), state(L, u)
        print(f"  start — host: {short(s0b)}\n           laptop: {short(s0l)}", flush=True)
        to_max = True
        if axis(u) == "x": to_max = s0b["x"] - u["minX"] < u["maxX"] - s0b["x"]
        elif axis(u) == "y": to_max = s0b["y"] - u["minY"] < u["maxY"] - s0b["y"]
        elif axis(u) == "slide": to_max = s0b["slide"] < 0.5

        # ---- 1 OPEN by the laptop
        print("\n1 OPEN (the laptop)", flush=True)
        sl = swing(L, u, to_max); time.sleep(2.5); sb = state(b, u)
        print(f"  laptop: {short(sl)}\n  host  : {short(sb)}", flush=True)
        moved = abs(val(sl, u) - val(s0l, u)) > 1e-3
        verdict("1 OPEN: THE LAPTOP'S DOOR MOVED (the test's input works)", moved)
        verdict("1 OPEN: THE HOST SEES IT THE SAME", moved and same(sl, sb))

        # ---- 2 CLOSE by the host
        print("\n2 CLOSE (the host)", flush=True)
        opened = sl
        sb = swing(b, u, not to_max); time.sleep(2.5); sl = state(L, u)
        print(f"  host  : {short(sb)}\n  laptop: {short(sl)}", flush=True)
        verdict("2 CLOSE: THE LAPTOP SEES IT THE SAME", abs(val(sb, u) - val(opened, u)) > 1e-3 and same(sb, sl))

        # ---- 3 RETURN: the laptop away, the host opens, the laptop back
        print("\n3 RETURN (the laptop beyond the remove distance while the host opens it)", flush=True)
        rd = max([d for d in L("mp poiusables")["removeDist"] if isinstance(d, (int, float))] or [0])
        far = max(12000, rd + 2000)
        print(f"  the game removes buildings beyond {rd} m — laptop {far} m away:", tp(L, ux + far, uz), flush=True)
        gone = until(lambda: state(L, u) is None, 150)
        print("  its building unloaded on the laptop:", gone, flush=True)
        un0 = L("mp poiusables")["unplaced"]
        sb = swing(b, u, to_max); time.sleep(2)
        un1 = L("mp poiusables")["unplaced"]
        print(f"  host opened: {short(sb)}  (changes the laptop couldn't place, its building gone: +{un1 - un0})", flush=True)
        on0 = L("mp poiusables")["onSpawn"]
        print("  laptop back:", tp(L, ux - 3, uz + 3), flush=True)
        until(lambda: state(L, u) is not None, 40); time.sleep(2)
        sl = state(L, u); on1 = L("mp poiusables")["onSpawn"]
        print(f"  laptop: {short(sl)}  (re-applied on spawn +{on1 - on0})", flush=True)
        verdict("3 RETURN: THE BUILDING WAS UNLOADED ON THE LAPTOP WHEN THE HOST OPENED IT", gone or un1 > un0,
                f"poll saw it gone {gone}, host's changes unplaced +{un1 - un0}")
        verdict("3 RETURN: THE LAPTOP SEES THE HOST'S OPEN DOOR", same(sb, sl))

        # ---- 4 REJOIN
        print("\n4 REJOIN (the host closes it while the laptop is out)", flush=True)
        L("mp stop"); until(lambda: not L("mp status")["client"]["ready"], 15); time.sleep(2)
        sb = swing(b, u, not to_max); time.sleep(1)
        print(f"  host closed: {short(sb)}  | laptop (offline): {short(state(L, u))}", flush=True)
        ok = join(); time.sleep(4)
        sl = state(L, u)
        print(f"  rejoined {ok} — laptop: {short(sl)}", flush=True)
        verdict("4 REJOIN: THE LAPTOP SEES THE HOST'S CLOSED DOOR", ok and same(sb, sl))
        print("  host:", b("mp poiusables"), "\n  laptop:", L("mp poiusables"), flush=True)
        L("mp stop"); b("mp stop")
        if RELAY: b("net relay off"); L("net relay off")
finally:
    L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")

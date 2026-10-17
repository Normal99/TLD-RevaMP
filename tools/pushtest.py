#!/usr/bin/env python3
"""Pushing another player's object (v0.64.8, Entities.PushIn). Two machines over the relay: desktop hosts, laptop joins.
The game's push (hold the push key on a car) puts a force on the body; on a copy that did nothing - a friend's stuck car
could not be pushed. Now the push goes to the owner, who moves the real body; the copy follows.
  A the laptop pushes a car the HOST owns (client -> server -> host)
  B the host pushes a car the LAPTOP owns (host -> server -> client)
Each: the car must move on its owner (away from the pusher, > 0.3 m), and the copy must be where the owner has it.
The push goes through the game's own pushablescript.Push every frame (bridge `mp push net secs`).
  tools/pushtest.py [--no-build] [--relay iad|off] [--car Car06Full]"""
import sys, os, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
CAR = sys.argv[sys.argv.index("--car") + 1] if "--car" in sys.argv else "Car06Full"
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

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
def pushes(s): return s("mp entities")["pushes"]

def spawn_car(s, who):
    before = set(ents(s))
    print(f"  {who} spawns:", s(f"devspawn {CAR}"), flush=True)
    until(lambda: any(e["car"] and not e["proxy"] for n, e in ents(s).items() if n not in before), 20)
    new = [n for n, e in ents(s).items() if n not in before and e["car"] and not e["proxy"]]
    if not new: raise RuntimeError(f"{who}: the spawned car never became an entity")
    return new[0]

def push_case(tag, owner, pusher, oname, pname, net):
    if not until(lambda: net in ents(pusher) and ents(pusher)[net]["proxy"], 30):
        verdict(tag, False, f"{pname} never got the copy"); return
    time.sleep(8)   # the spawned car settles on its wheels
    o0 = ents(owner)[net]["pos"]
    # the pusher stands 2.5 m from the car, on the side away from the owner's player
    op = owner("gpos"); ox, oz = op["gx"], op["gz"]
    dx, dz = o0[0] - ox, o0[2] - oz; d = math.hypot(dx, dz) or 1.0
    px, pz = o0[0] + dx / d * 3.0, o0[2] + dz / d * 3.0
    print(f"  {pname} to {px:.1f},{pz:.1f}:", pusher(f"tpground {px} {pz}"), flush=True)
    time.sleep(3)
    o0 = ents(owner)[net]["pos"]; pp = pusher("gpos")
    # run 2, A: the host's car spawned wedged in a house (touching only haz02, no ground) - nothing could move it.
    # A car that touches anything but the terrain is not a push test.
    aw = [r for r in owner(f"mp awake {o0[0]} {o0[2]} 4")["awake"] if r[1] == net]
    touch = aw[0][6] if aw else []
    if any("TerrainParent" not in t and "Player" not in t for t in touch):
        verdict(tag + " (INVALID: the car is held by " + ", ".join(touch) + ")", False); return
    s0, a0 = pushes(pusher), pushes(owner)
    print(f"  {pname} at height {pp['gy']:.2f}, car at {o0[1]:.2f}", flush=True)
    print(f"  {pname} pushes:", pusher(f"mp push {net} 5"), flush=True)
    for k in range(3):   # A run 1: 85 pushes applied on the host, the car moved 5 cm - what holds it?
        time.sleep(1.5)
        c = ents(owner)[net]["pos"]
        ie = owner(f"mp itement {c[0]} {c[1]} {c[2]}")
        aw = [r for r in owner(f"mp awake {c[0]} {c[2]} 4")["awake"] if r[1] == net]
        print(f"    {oname} car t+{1.5 * (k + 1):.1f}s: kin {ie.get('kinematic')} sleeping {ie.get('sleeping')} root {ie.get('root')} | "
              f"awake row {aw[0][3:] if aw else 'none (asleep/kinematic)'}", flush=True)
    time.sleep(4.5)
    o1 = ents(owner)[net]["pos"]; c1 = ents(pusher)[net]["pos"]
    s1, a1 = pushes(pusher), pushes(owner)
    moved = math.dist(o0, o1)
    away = (o1[0] - o0[0]) * (o0[0] - pp["gx"]) + (o1[2] - o0[2]) * (o0[2] - pp["gz"]) > 0
    off = math.dist(o1, c1)
    print(f"  {pname} sent {s1[0] - s0[0]} pushes ({s1[2] - s0[2]} frames); {oname} applied {a1[1] - a0[1]}", flush=True)
    print(f"  car on {oname}: {[round(x, 2) for x in o0]} -> {[round(x, 2) for x in o1]} ({moved:.2f} m, away from the pusher {away}); "
          f"copy on {pname} {off:.2f} m from it", flush=True)
    verdict(tag, moved > 0.3 and away and off < 0.3, f"moved {moved:.2f} m, away {away}, copy off {off:.2f} m")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    gods = {}
    for name, s_ in (("host", h), ("laptop", L)):
        gods[name] = (s_, s_("get kaposztaleves.s.settings.god").get("value"))
        s_("set kaposztaleves.s.settings.god True")
    print("god mode on both (was", {k: v[1] for k, v in gods.items()}, ")", flush=True)
    try:
        hp = h("gpos"); print(f"  host at {hp['gx']:.0f},{hp['gz']:.0f}; laptop at", {k: round(L('gpos')[k]) for k in ('gx', 'gz')}, flush=True)
        # A: the host's car, pushed by the laptop
        h(f"tpground {hp['gx'] + 15} {hp['gz']}"); time.sleep(3)   # out of the start building (run 2: the car spawned inside a house)
        net = spawn_car(h, "host")
        push_case("A THE LAPTOP PUSHES THE HOST'S CAR", h, L, "host", "laptop", net)
        # B: the laptop's car, 15 m away, pushed by the host
        L(f"tpground {hp['gx'] + 15} {hp['gz'] + 15}"); time.sleep(3)
        net = spawn_car(L, "laptop")
        push_case("B THE HOST PUSHES THE LAPTOP'S CAR", L, h, "laptop", "host", net)
    finally:
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

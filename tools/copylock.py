#!/usr/bin/env python3
"""A copy stored in a container on one machine only (playround.py + the anomaly watch, 2026-10-09: crate loot lying at
home got stored into the host's copy of the laptop's car; shown at its owner's pose it was dragged 700 m while parented
in that car, and the game's streaming removed the whole car every second, 35 m from the host). Two machines over the
relay: the laptop's car01 and the laptop's lamp lying 6 m from it; on the host the lamp's copy is stored into the copy
of the car (`mp copystore`: what the copy's own scan did by mistake); the laptop moves its car 800 m; the host goes to
it → the host's copy of the car placed and it stays, the lamp's copy where the laptop has it.
  tools/copylock.py [--no-build] [--relay iad|off] [--old: no release, before v0.66.7]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import CAR01

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
T = "TLDRevamp.Net.Entities"; LAMP = 320
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

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
def ent(s, net): return ents(s).get(net)

def travel(s, gx, gz, who, settle=4):
    s("set mainscript.s.player.godModeTillGrounded True")
    s(f"tpg {gx} {gz} hold")
    def grounded():
        q = s("gpos"); return "y" in s(f"ground {q['ux']} {q['uz']}")
    if not until(grounded, 90, 1.0): s("tprelease"); raise RuntimeError(f"{who}: no ground at {gx:.0f},{gz:.0f}")
    r = s(f"tpground {gx} {gz}")
    if r.get("tpground") == "hop": until(grounded, 30, 1.0); s(f"tpground {gx} {gz}")
    time.sleep(settle)
    q = s("gpos"); print(f"  {who} at {q['gx']:.0f},{q['gz']:.0f}", flush=True)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_(f"set {T}.CopyLockRelease {not OLD}")
        print(f"  {'OLD (no release)' if OLD else 'NEW'}", flush=True)
        time.sleep(5)
        lid = L("mp entities")["me"]
        hp = h("gpos"); L(f"tpground {hp['gx'] + 3} {hp['gz'] + 3}"); time.sleep(3)
        before = set(ents(L))
        print("  laptop's car:", L(f"spawncarg {CAR01} 0 {hp['gx'] + 12} {hp['gz'] + 12} 0"), flush=True)
        car = until(lambda: next((n for n, e in ents(L).items() if n not in before and e["car"] and e["owner"] == lid), None), 20)
        if not car: sys.exit("the laptop's car was not shared")
        until(lambda: (ent(h, car) or {}).get("dormant") is False, 30); time.sleep(4)
        cp = ent(L, car)["pos"]
        before = set(ents(L)); u = L("gpos")
        lx, lz = u["ux"] + (cp[0] - u["gx"]) + 6, u["uz"] + (cp[2] - u["gz"])
        gy = L(f"ground {lx} {lz}").get("y")
        L(f"spawnpile {LAMP} 1 {lx} {gy + 0.4} {lz} 1 1 0 5")
        lamp = until(lambda: next((n for n, e in ents(L).items() if n not in before and e["owner"] == lid and not e["car"]), None), 15)
        if not lamp: sys.exit("no lamp")
        until(lambda: (ent(h, lamp) or {}).get("dormant") is False, 20); time.sleep(4)
        print(f"  car {car}, lamp {lamp} (6 m from it, on the ground on the laptop)", flush=True)
        print("  [host] the lamp's copy stored into the copy of the car:", h(f"mp copystore {lamp} {car}"), flush=True)
        time.sleep(3)
        print(f"  [host] after 3 s: lamp copy {[round(v, 1) for v in ent(h, lamp)['pos']]}, laptop has it at {[round(v, 1) for v in ent(L, lamp)['pos']]}", flush=True)
        print("  laptop moves its car 800 m:", L(f"mp moveitem {car} 800 0"), flush=True)
        until(lambda: (ent(h, car) or {}).get("dormant") is True, 30); time.sleep(3)
        cp = ent(L, car)["pos"]
        travel(h, cp[0] - 25, cp[2] - 25, "host (to the laptop's car)")
        placed = until(lambda: (ent(h, car) or {}).get("dormant") is False, 30)
        verdict("THE HOST PLACED ITS COPY OF THE CAR", bool(placed))
        stay = placed and not until(lambda: (ent(h, car) or {}).get("dormant") is True, 15, 0.3)
        an = h("mp anomalies")
        verdict("AND IT STAYS (15 s, the host 35 m away)", bool(stay), f"host anomalies {an['count']} {an['lines'][-2:]}")
        el, eh = ent(L, lamp), ent(h, lamp)
        gap = math.dist(el["pos"], eh["pos"]) if el and eh and not eh.get("dormant") else None
        verdict("THE LAMP'S COPY WHERE THE LAPTOP HAS IT", gap is not None and gap < 1.0 or (eh or {}).get("dormant") is True,
                f"{gap and round(gap, 2)} m apart (host copy dormant {(eh or {}).get('dormant')}); released {h(f'get {T}.CopyLocksReleased')['value']}")
        print("  errors:", [s_("errors")["errors"] for s_ in (h, L)], flush=True)
    finally:
        for s_ in (h, L):
            try: s_(f"set {T}.CopyLockRelease True")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

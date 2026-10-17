#!/usr/bin/env python3
"""What do other players' cars cost each machine? loadtest showed 14 car01 copies taking the host from 8 to 31 ms a frame
and the laptop from 20 to 87 ms (v0.57.69) while our network code is ~1 ms. With BOTS bot cars circling (as loadtest),
frame times on both machines over WINDOW-second windows, each with one kind of work switched off on every copy:
  all on | collider LOD off | rendering off | colliders off (not wheels) | the game's own scripts off | all three off
plus a profile of the game's scripts with everything on (which scripts, how many ms a second).
Test switches only (Entities.CopiesToggle); both games are restarted before the next test anyway.
  tools/copycost.py [--no-build] [--bots 14] [--window 12]"""
import sys, time, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP
from roadkit import CAR01, until

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
BOTS, WINDOW = arg("--bots", 14, int), arg("--window", 12.0, float)

def frames(sides):
    for s in sides: s("reset"); s("mp entstats")
    time.sleep(WINDOW)
    out = []
    for s in sides:
        st = s("stats")
        cps = [e for e in s("mp entities")["entities"] if e["car"] and e["proxy"]]
        st["jumps"] = sum(e.get("jumps", 0) for e in cps); st["maxDevCm"] = max([e.get("maxDevCm", 0) for e in cps] or [0])
        st["lodOff"] = sum(1 for e in cps if e.get("partsColOff")); st["physical"] = sum(1 for e in cps if e.get("physical"))
        out.append(st)
    return out

ensure_both("--no-build" not in sys.argv)
L = Agent()
res = {}
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        b("mp host 27070"); L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        rp = b("roadpoint")
        b(f"spawncarg {CAR01} 0 {rp['gx'] + 6} {rp['gz']} 0"); time.sleep(3)
        b(f"tpground {rp['gx'] + 8} {rp['gz'] + 1}"); time.sleep(2)
        bc = b("mp botcar"); b("clearpile")
        if "Car01" not in bc.get("car", ""): sys.exit(f"bots would drive {bc}")
        L(f"tpground {rp['gx'] + 3} {rp['gz'] + 3}"); time.sleep(3)
        side_names = (("desktop", b), ("laptop", L))
        base = frames([b, L])
        res["no bots (2 players)"] = base
        b("set TLDRevamp.Net.Mp.BotCars True")
        print("bots:", b(f"mp bots {BOTS} 100 16")["bots"], flush=True)
        until(lambda: sum(1 for e in L("mp entities")["entities"] if e["car"] and e["proxy"]) >= BOTS, 60, 1.0)
        time.sleep(8)
        print("copies on the laptop:", L("mp copies render on"), flush=True)
        for s in (b, L): s("set TLDRevamp.Net.Entities.CopiesMoveByBody False")
        time.sleep(2)
        res["all on, transform writes"] = frames([b, L])
        for s in (b, L): s("set TLDRevamp.Net.Entities.CopiesMoveByBody True")
        time.sleep(2)
        res["all on, moved as bodies"] = frames([b, L])
        print("laptop physical/LOD:", L("mp physical"), flush=True)
        for s in (b, L): s("set TLDRevamp.Net.Entities.CopyColliderLod False")
        time.sleep(2)
        res["collider LOD off"] = frames([b, L])
        for s in (b, L): s("set TLDRevamp.Net.Entities.CopyColliderLod True")
        time.sleep(2)
        for what in ("render", "colliders", "scripts"):
            for s in (b, L): s(f"mp copies {what} off")
            res[f"{what} off"] = frames([b, L])
            for s in (b, L): s(f"mp copies {what} on")
            time.sleep(2)
        for s in (b, L):
            for what in ("render", "colliders", "scripts"): s(f"mp copies {what} off")
        res["all three off"] = frames([b, L])
        for s in (b, L):
            for what in ("render", "colliders", "scripts"): s(f"mp copies {what} on")
        time.sleep(3)
        for s in (b, L): s("profile start")
        time.sleep(WINDOW)
        prof = {n: s("profile stop 15") for n, s in side_names}
        b("mp stop"); L("mp stop")
finally:
    L.close()

print(f"\nCOPY COST ({BOTS} bot car01s within 100 m) — frame p50 / p99 ms")
print(f"{'':28} {'desktop (host)':>18} {'jumps':>6} {'laptop':>18} {'jumps':>6} {'max dev cm':>10} {'LOD off d/l':>11}")
for k, (d, l) in res.items():
    print(f"{k:28} {d['p50ms']:>8} / {d['p99ms']:<8} {d.get('jumps', 0):>6} {l['p50ms']:>8} / {l['p99ms']:<8} {l.get('jumps', 0):>6} {l.get('maxDevCm', 0):>10} {d.get('lodOff', 0):>5}/{l.get('lodOff', 0):<5}")
for n, p in prof.items():
    print(f"\n{n}: game scripts {p.get('totalMsPerSec')} ms/s total; top:")
    for m in p.get("byTime", [])[:15]: print(f"   {m['msPerSec']:>8} ms/s  {m['callsPerSec']:>7} calls/s  {m['m']}")

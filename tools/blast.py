#!/usr/bin/env python3
"""Explosions in MP (two machines, relay iad by default; Entities.Explosions). A fuel can shot with a revolver
(explodeStuffs, explodeChance) goes off: weaponscript.Shot → tankscript.TryExplode → explosionscript.Explode.
  1 VISITOR: the laptop shoots the host's fuel can (a second host can 1.2 m away for the chain) until it goes off.
             The owner's can must explode ON THE OWNER (the request goes there), everyone sees that one blast, the
             chained can goes off on the owner too, both cans are gone on both machines, the laptop sends no blast
             of its own (no double explosion)
  2 OWNER  : the host shoots its own can (+ a second) — the reference: same counts, the laptop replays them
Reported, not judged: deaths (a player near a blast dies by the game's own rule).
  tools/blast.py [--no-build] [--relay iad|off] [--shots 12]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP
from roadkit import until, g

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY, SHOTS = arg("--relay", "iad"), arg("--shots", 12, int)
if RELAY == "off": RELAY = None
MAGNUM, JERRYCAN = 64, 72

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def unit(yaw): return (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))

def nets(s): return {e["net"]: e for e in s("mp entities")["entities"]}

def new_item(side, who, idn):
    me = side("mp entities")["me"]
    before = set(nets(side))
    print(f"  {who} spawns {idn}:", side(f"devspawn {idn}"), flush=True)
    got = []
    until(lambda: got.extend(n for n, e in nets(side).items() if n not in before and e["owner"] == me and not e["car"]) or got, 10)
    return got[0] if got else None

def phase(tag, shooter, x, z):
    """the host's two cans at (x, z), 1.2 m apart along the road; the shooter 8 m to the side with a magnum"""
    b(f"tpground {x} {z}"); time.sleep(1.5)
    a = new_item(b, "host", JERRYCAN)
    b(f"tpground {x + D[0] * 1.2} {z + D[1] * 1.2}"); time.sleep(1.5)
    c = new_item(b, "host", JERRYCAN)
    if a is None or c is None: verdict(f"{tag}: CANS", False); return
    for n in (a, c): print(f"  {tag}: fuel can {n}:", b(f"mp fill {n} 0 gas 15"), flush=True)
    b(f"tpground {x - R[0] * 30} {z - R[1] * 30}"); time.sleep(1)       # the host away (or stands to shoot below)
    sx, sz = x + R[0] * 8, z + R[1] * 8
    shooter(f"tpground {sx} {sz}"); time.sleep(1.5)
    print(f"  {tag}: shooter's magnum:", shooter(f"devspawn {MAGNUM}"), flush=True); time.sleep(3)
    if not until(lambda: a in nets(L) and c in nets(L), 15): verdict(f"{tag}: THE LAPTOP HAS BOTH CANS", False); return
    s0 = {n: s("mp blasts") for n, s in (("host", b), ("laptop", L))}
    d0 = {n: g(s, "TLDRevamp.Diagnostics.DeathLog.Deaths") for n, s in (("host", b), ("laptop", L))}
    shots = 0
    for k in range(SHOTS):
        p = nets(shooter).get(a)
        if p is None: break
        p = p["pos"]
        r = shooter(f"mp shootpoint {p[0]} {p[1] + 0.15} {p[2]}"); shots += 1
        print(f"    shot {k + 1}: first hit {r.get('firstHit')}, hits {r.get('shotHits')}, in hand {r.get('inHand')}", flush=True)
        time.sleep(1.5)
        if a not in nets(b): break
    time.sleep(4)
    s1 = {n: s("mp blasts") for n, s in (("host", b), ("laptop", L))}
    d1 = {n: g(s, "TLDRevamp.Diagnostics.DeathLog.Deaths") for n, s in (("host", b), ("laptop", L))}
    dd = lambda n, k: s1[n][k] - s0[n][k]
    print(f"  {tag}: {shots} shots; host {s1['host']}, laptop {s1['laptop']}; deaths host +{d1['host'] - d0['host']}, laptop +{d1['laptop'] - d0['laptop']}", flush=True)
    gone = {n: (a not in nets(s), c not in nets(s)) for n, s in (("host", b), ("laptop", L))}
    if a in nets(b): verdict(f"{tag}: THE CAN WENT OFF", False, f"{shots} shots, never exploded (explodeChance)"); return
    verdict(f"{tag}: THE CAN IS GONE ON BOTH", gone["host"][0] and gone["laptop"][0], str(gone))
    verdict(f"{tag}: THE CHAINED CAN WENT OFF ON BOTH", gone["host"][1] and gone["laptop"][1], str(gone))
    verdict(f"{tag}: THE OWNER'S MACHINE MADE THE BLASTS", dd("host", "sent") >= 2 and dd("laptop", "sent") == 0,
            f"host sent {dd('host', 'sent')}, laptop sent {dd('laptop', 'sent')}")
    verdict(f"{tag}: THE LAPTOP SAW EACH OF THEM", dd("laptop", "applied") == dd("host", "sent"),
            f"laptop applied {dd('laptop', 'applied')} of {dd('host', 'sent')}")
    if shooter is L:
        verdict(f"{tag}: THE SHOT WENT TO THE OWNER", dd("laptop", "reqSent") >= 1 and dd("host", "reqRun") >= 1,
                f"laptop requests {dd('laptop', 'reqSent')}, host ran {dd('host', 'reqRun')}")

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        b = seeded_both(L)
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("relay only via", RELAY, b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
        b("mp host 27070")
        if RELAY: L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("route:", L("net route"), flush=True)
        time.sleep(4)
        rp = b("roadpoint"); yaw = rp["yaw"]; D = unit(yaw); R = (D[1], -D[0])
        cx, cz = rp["gx"], rp["gz"]
        L(f"tpground {cx + R[0] * 40} {cz + R[1] * 40}"); time.sleep(1)
        phase("1 VISITOR", L, cx, cz)
        L(f"tpground {cx + D[0] * 60 + R[0] * 40} {cz + D[1] * 60 + R[1] * 40}"); time.sleep(1)
        phase("2 OWNER", b, cx + D[0] * 40, cz + D[1] * 40)
        L("mp stop"); b("mp stop")
        if RELAY: b("net relay off"); L("net relay off")
finally:
    L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")

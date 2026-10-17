#!/usr/bin/env python3
"""Objects simulated where players are (Entities.Handoff): two machines, relay iad by default, god mode on both.
The host spawns a creature (the host owns it), the laptop 60 m away.
  1 HANDOFF : the host travels 3 km away → the creature passes to the laptop (not kinematic there), no jump, and it
              hunts the laptop's player on the laptop's own machine
  2 NO BOUNCE: the host comes next to it, the laptop 180 m off → it stays the laptop's, the host's copy follows, its
              attack reaches the host (remote damage)
  3 BACK     : the laptop goes 400 m away → the host (next to it) runs it
  tools/handoff.py [--no-build] [--relay iad|off]"""
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
def net(a): return a["who"].split(" owner")[0]
def owner(a):
    try: return int(a["who"].split("owner ")[1].split()[0])
    except Exception: return None
def find(s, n): return next((x for x in s("mp ai")["ai"] if net(x) == n), None)
def short(a): return (f"{a['who']:<22} {a['pos']} kin {a['kin']!s:<5} chase {a['chasing']!s:<5} attack {a['attacking']!s:<5} "
                      f"died {a['died']!s:<5} fwd {a['fwd']:>5} target {a['target']}") if a else "—"
def front(a, d):
    lk = a["look"]; n = math.hypot(*lk) or 1
    return a["pos"][0] + lk[0] / n * d, a["pos"][2] + lk[1] / n * d
def hp(s):
    v = g(s, "mainscript.s.player.survival.hp")
    return v if isinstance(v, (int, float)) else None

ensure_both("--no-build" not in sys.argv)
L = Agent()
gods = {}
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
        for name, s in (("host", b), ("laptop", L)):
            gods[name] = (s, g(s, "kaposztaleves.s.settings.god")); s("set kaposztaleves.s.settings.god True")
        aid = b("mp aiids")["ai"][0][0]
        time.sleep(4)
        rp = b("roadpoint"); D = unit(rp["yaw"]); R = (D[1], -D[0]); cx, cz = rp["gx"], rp["gz"]
        print("host on the road:", tp(b, cx, cz), "| laptop 60 m off:", tp(L, cx + R[0] * 60, cz + R[1] * 60), flush=True)
        time.sleep(2)
        before = {net(a) for a in b("mp ai")["ai"]}
        print("host spawns a creature:", b(f"devspawn {aid}"), flush=True)
        if not until(lambda: any(net(a) not in before for a in b("mp ai")["ai"]), 15): sys.exit("no creature")
        c = next(net(a) for a in b("mp ai")["ai"] if net(a) not in before)
        if not until(lambda: find(L, c) is not None, 20): sys.exit("the laptop has no copy")
        time.sleep(2)
        print(f"  host's  : {short(find(b, c))}\n  laptop's: {short(find(L, c))}", flush=True)
        h0 = g(b, "TLDRevamp.Net.Entities.Handoffs")

        # ---- 1 HANDOFF
        print("\n1 HANDOFF (the host travels 3 km away)", flush=True)
        la0 = find(L, c)
        print("  host away:", tp(b, cx + R[0] * -3000, cz + R[1] * -3000), flush=True)
        t0 = time.time()
        got = until(lambda: (lambda a: a is not None and a["kin"] is False and owner(a) == 1)(find(L, c)), 40)
        la1 = find(L, c); took = time.time() - t0
        h1 = g(b, "TLDRevamp.Net.Entities.Handoffs")
        print(f"  after {took:.1f}s (handoffs +{(h1 or 0) - (h0 or 0)}): laptop's {short(la1)}", flush=True)
        verdict("1 HANDOFF: THE CREATURE IS THE LAPTOP'S NOW (simulated there)", got)
        jump = math.dist(la0["pos"], la1["pos"]) if la0 and la1 else 99
        verdict("1 HANDOFF: NO JUMP", jump < 6.0, f"{jump:.1f} m between before and after (it may walk meanwhile)")
        print("  the laptop steps in front of it", tp(L, *front(la1, 3.5)), flush=True)
        hpmin, att, seen = 99.0, False, False
        for k in range(15):
            time.sleep(1)
            a = find(L, c); v = hp(L)
            if v is not None: hpmin = min(hpmin, v)
            if a: att |= a["attacking"]; seen |= a["chasing"] and "Player(Clone)" in a["target"]
            if k % 4 == 0: print(f"  t{k + 1}s laptop hp {v} — {short(a)}", flush=True)
        print(f"  the laptop's creature after the hand-off: {L('mp aihead')}", flush=True)
        verdict("1 HANDOFF: IT HUNTS THE LAPTOP'S PLAYER LOCALLY", seen and att and hpmin < 4.9, f"seen {seen} attack {att} lowest hp {hpmin:.2f}")

        # ---- 2 NO BOUNCE: both near (the laptop inside the game's 200 m freeze distance) — it stays the laptop's
        print("\n2 NO BOUNCE (the host comes to it, the laptop 180 m off)", flush=True)
        a = find(L, c)
        print("  laptop 180 m away:", tp(L, a["pos"][0] + R[0] * 180, a["pos"][2] + R[1] * 180), flush=True)
        time.sleep(1); a = find(L, c)
        if a is None: sys.exit("the laptop dropped it at 180 m")
        # on its way: it runs to where it last saw the laptop's player (chasing), so the host stands 8 m ahead on that line —
        # in its field of view (placed "in front" of where it looked, it had already turned: 108-116 deg off, fov 70)
        lp_ = L("gpos"); dx, dz = lp_["gx"] - a["pos"][0], lp_["gz"] - a["pos"][2]; dn = math.hypot(dx, dz) or 1
        print("  host on its way:", tp(b, a["pos"][0] + dx / dn * 8, a["pos"][2] + dz / dn * 8), flush=True)
        if not until(lambda: find(b, c) is not None, 40): print("  the host has no copy of it yet", flush=True)
        s0 = b("mp aistats"); hmin = 99.0; owners = set(); gaps = []; hpos = []
        for k in range(15):
            time.sleep(1)
            a = find(L, c); v = hp(b)
            if v is not None: hmin = min(hmin, v)
            if a: owners.add(owner(a))
            ha = find(b, c)
            if a and ha: gaps.append(math.dist(a["pos"], ha["pos"])); hpos.append(ha["pos"])
            if k % 4 == 0: print(f"  t{k + 1}s host hp {v}\n     laptop's: {short(a)}\n     host's  : {short(ha)}", flush=True)
            if k in (0, 1, 2, 5): print(f"     laptop's creature sees the host: {L('mp aisee')}\n     its parts: {L('mp aihead')}", flush=True)
        s1 = b("mp aistats"); h2 = g(b, "TLDRevamp.Net.Entities.Handoffs")
        print("  laptop's creature side (it runs it):", L("mp aistats"), flush=True)
        moved = max((math.dist(hpos[0], p) for p in hpos), default=0)
        med = sorted(gaps)[len(gaps) // 2] if gaps else -1
        verdict("2 NO BOUNCE: STILL THE LAPTOP'S", owners == {1}, f"owners seen {owners} (other objects handed meanwhile: +{(h2 or 0) - (h1 or 0)})")
        verdict("2 NO BOUNCE: THE HOST'S COPY FOLLOWS IT", len(gaps) >= 10 and 0 <= med < 2.5, f"median gap {med:.2f} m, max {max(gaps, default=-1):.2f} m, moved {moved:.1f} m")
        verdict("2 NO BOUNCE: ITS ATTACK REACHES THE HOST", s1["damageTaken"] > s0["damageTaken"] and hmin < 4.9,
                f"+{s1['damageTaken'] - s0['damageTaken']} damage messages, lowest hp {hmin:.2f}")

        # ---- 3 BACK: the laptop leaves (beyond 200 m: frozen/stored there) → the host, next to it, takes it over
        print("\n3 BACK TO THE HOST (the laptop goes 400 m away)", flush=True)
        a = find(L, c) or find(b, c)
        print("  laptop away:", tp(L, a["pos"][0] + R[0] * 400, a["pos"][2] + R[1] * 400), flush=True)
        got = until(lambda: (lambda x: x is not None and x["kin"] is False and owner(x) == 0)(find(b, c)), 20)
        x = find(b, c)
        if x: print("  host in front of it again:", tp(b, *front(x, 3.5)), flush=True)
        h3 = g(b, "TLDRevamp.Net.Entities.Handoffs")
        verdict("3 BACK: THE HOST RUNS IT NOW", got, f"host's {short(find(b, c))}, handoffs +{(h3 or 0) - (h2 or 0)}")
        seen = False
        for k in range(8):
            time.sleep(1); x = find(b, c)
            if x: seen |= x["chasing"] and "Player(Clone)" in x["target"]
        verdict("3 BACK: IT HUNTS THE HOST LOCALLY", seen, short(find(b, c)))
        L("mp stop"); b("mp stop")
        if RELAY: b("net relay off"); L("net relay off")
finally:
    for name, (s, v) in gods.items():
        try: s(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
        except Exception as e: print("restore god", name, e)
    L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")

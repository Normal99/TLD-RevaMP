#!/usr/bin/env python3
"""Creatures in MP, on an open road (two machines, relay iad by default; god mode on both players — the game's own
dev toggle, runtime-only, restored after). The host spawns two creatures (the dev menu's spawn) — it owns them.
  A HUNT  : the laptop stands in front of creature 1 (the host 30 m away) → the host's creature sees, chases and
            attacks the laptop's body; the laptop's hp goes down (god mode: no death); the laptop's copy shows
            chase/attack/walking and plays its voice
  B KILL  : the laptop shoots creature 1 (magnum) until it dies → every hit on the copy reaches the owner; dead on
            the host AND on the laptop's copy
  C REF   : the host shoots creature 2 itself the same way — how many shots the game needs; dead on the copy too
  A also: it stays on the laptop — never turns to the host 30 m off while at the laptop (v0.65.42; --no-sticky: before)
  C uses a fresh creature (creature 2 chased the laptop beside creature 1: the laptop's volley killed it — run 7)
  tools/zombies.py [--no-build] [--relay iad|off] [--ai <itemdatabase index>] [--no-sticky]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP
from roadkit import until, g, tp

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY = arg("--relay", "iad")
if RELAY == "off": RELAY = None
MAGNUM, MAXSHOTS = 64, 80

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def unit(yaw): return (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
def ais(s): return s("mp ai")["ai"]
def net(a): return a["who"].split(" owner")[0]
def find(s, n): return next((x for x in ais(s) if net(x) == n), None)
def short(a): return (f"{a['pos']} hp {a['health']} sleep {a['sleeping']!s:<5} chase {a['chasing']!s:<5} attack {a['attacking']!s:<5} "
                      f"died {a['died']!s:<5} fwd {a['fwd']:>5} v {a['speed']:>5} target {a['target']}") if a else "—"
def front(a, d):
    lk = a["look"]; n = math.hypot(*lk) or 1
    return a["pos"][0] + lk[0] / n * d, a["pos"][2] + lk[1] / n * d

def kill(tag, shooter, other, n):
    """shoot creature n from `shooter`'s eye at 7 m (stepping back to 7 m whenever it closes within 3 m) until the
    owner's is dead; returns shots"""
    print(f"  {tag} magnum:", shooter(f"devspawn {MAGNUM}"), flush=True); time.sleep(2)
    shots, backs = 0, 0
    for k in range(MAXSHOTS):
        ha = find(b, n)
        if ha is None or ha["died"]: break
        me = shooter("gpos")
        if k == 0 or math.hypot(me["gx"] - ha["pos"][0], me["gz"] - ha["pos"][2]) < 3.0:
            x, z = front(ha, 7.0); r = tp(shooter, x, z); backs += 1
            if k == 0: print(f"   {tag} 7 m in front of it: {r}", flush=True)
            time.sleep(0.6); ha = find(b, n)
        r = shooter(f"mp shootpoint {ha['aim'][0]} {ha['aim'][1]} {ha['aim'][2]} eye"); shots += 1
        if k < 3 or k % 5 == 4: print(f"   shot {k + 1}: inHand {r.get('inHand')} hits {r.get('shotHits')} → owner {short(find(b, n))}", flush=True)
        time.sleep(0.5)
    time.sleep(4)
    print(f"   stepped back {backs - 1} times", flush=True)
    return shots

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
            gods[name] = (s, g(s, "kaposztaleves.s.settings.god"))
            s("set kaposztaleves.s.settings.god True")
        print("god mode on both (was", {k: v[1] for k, v in gods.items()}, ")", flush=True)
        if "--no-sticky" in sys.argv: b("set TLDRevamp.Net.Entities.AiStickyRemote False")
        ids = b("mp aiids")["ai"]
        print("creatures in the item database:", ids, flush=True)
        aid = arg("--ai", ids[0][0] if ids else None, int)
        if aid is None: sys.exit("no creature in the item database")
        time.sleep(4)
        rp = b("roadpoint"); D = unit(rp["yaw"]); R = (D[1], -D[0]); cx, cz = rp["gx"], rp["gz"]
        print("host on the road:", tp(b, cx, cz), "| laptop 25 m off:", tp(L, cx + R[0] * 25, cz + R[1] * 25), flush=True)
        time.sleep(2)
        before = {net(a) for a in ais(b)}
        for _ in range(2): print("host spawns a creature:", b(f"devspawn {aid}"), flush=True); time.sleep(1.5)
        if not until(lambda: len([a for a in ais(b) if net(a) not in before]) >= 2, 15): sys.exit("the creatures didn't spawn")
        mine = [net(a) for a in ais(b) if net(a) not in before]
        if not until(lambda: all(find(L, n) for n in mine), 20): sys.exit("the laptop has no copies of the creatures")
        c1, c2 = mine[0], mine[1]
        print("creatures:", c1, c2, "| host's:", [short(find(b, n)) for n in mine], "| laptop's:", [short(find(L, n)) for n in mine], flush=True)

        # ---- A: HUNT
        print("\nA HUNT", flush=True)
        print("  host 30 m back along the road:", tp(b, cx - D[0] * 30, cz - D[1] * 30), flush=True)
        a1 = find(b, c1); x, z = front(a1, 3.5)
        print("  laptop in front of creature 1:", tp(L, x, z), flush=True)
        st0, hs0 = L("mp aistats"), b("mp aistats")
        hpmin, targeted, cchase, cattack, cfwd = 99.0, False, False, False, 0.0
        turned = []
        for k in range(20):
            time.sleep(1)
            ha, la = find(b, c1), find(L, c1)
            if ha and "Player(Clone)" in ha["target"]:   # the host's own player is its target
                lp, hp_ = L("gpos"), b("gpos")
                dl = math.hypot(lp["gx"] - ha["pos"][0], lp["gz"] - ha["pos"][2]); dh = math.hypot(hp_["gx"] - ha["pos"][0], hp_["gz"] - ha["pos"][2])
                if dl < dh: turned.append((k + 1, round(dl, 1), round(dh, 1)))
            hpv = g(L, "mainscript.s.player.survival.hp")
            if isinstance(hpv, (int, float)): hpmin = min(hpmin, hpv)
            targeted |= bool(ha) and ha["chasing"] and "Head" in ha["target"] and "Player" in ha["target"]
            if la: cchase |= la["chasing"]; cattack |= la["attacking"]; cfwd = max(cfwd, abs(la["fwd"]))
            if k % 3 == 0: print(f"  t{k + 1:>3}s laptop hp {hpv}\n     host's  : {short(ha)}\n     laptop's: {short(la)}", flush=True)
        st1, hs1 = L("mp aistats"), b("mp aistats")
        print("  host:", hs1, "\n  laptop:", st1, flush=True)
        verdict("A HUNT: THE OWNER'S CREATURE SAW THE LAPTOP", hs1["remoteSeen"] > hs0["remoteSeen"], f"+{hs1['remoteSeen'] - hs0['remoteSeen']} frames")
        verdict("A HUNT: IT HUNTS THE LAPTOP'S BODY", targeted)
        verdict("A HUNT: THE LAPTOP TOOK THE DAMAGE", st1["damageTaken"] > st0["damageTaken"] and hpmin < 4.9,
                f"+{st1['damageTaken'] - st0['damageTaken']} damage messages, lowest hp {hpmin:.2f}")
        verdict("A HUNT: THE COPY SHOWS IT (chase, attack, walking)", cchase and cattack and cfwd > 0.05, f"chase {cchase} attack {cattack} max |fwd| {cfwd:.2f}")
        verdict("A HUNT: IT STAYS ON THE LAPTOP (never turns to the farther host)", not turned, f"turned at (s, laptop m, host m): {turned[:5]}; kept on the remote {hs1.get('keptOnRemote')}")
        verdict("A HUNT: ITS VOICE ON THE LAPTOP", st1["soundsApplied"] > st0["soundsApplied"], f"+{st1['soundsApplied'] - st0['soundsApplied']}")

        # ---- B: the laptop kills creature 1
        print("\nB KILL (the laptop shoots creature 1)", flush=True)
        st0, hs0 = L("mp aistats"), b("mp aistats")
        shots = kill("laptop", L, b, c1)
        ha, la = find(b, c1), find(L, c1)
        st1, hs1 = L("mp aistats"), b("mp aistats")
        print(f"  after {shots} shots — host's: {short(ha)}\n  laptop's: {short(la)}\n  host: {hs1}\n  laptop: {st1}", flush=True)
        verdict("B KILL: THE HITS REACHED THE OWNER", st1["breakHitsSent"] > st0["breakHitsSent"] and hs1["breakHitsRun"] - hs0["breakHitsRun"] == st1["breakHitsSent"] - st0["breakHitsSent"],
                f"sent +{st1['breakHitsSent'] - st0['breakHitsSent']}, ran on the owner +{hs1['breakHitsRun'] - hs0['breakHitsRun']}")
        verdict("B KILL: DEAD ON THE OWNER", bool(ha) and ha["died"], f"{shots} shots")
        verdict("B KILL: DEAD ON THE COPY", bool(la) and la["died"], f"break replays +{st1['breakFxApplied'] - st0['breakFxApplied']}")
        verdict("B KILL: THE LAPTOP SHOT ONLY THROUGH THE OWNER (copy never broke itself)", st1["breakFxSent"] == st0["breakFxSent"])

        # ---- C: the host kills creature 2 (reference)
        print("\nC REFERENCE (the host shoots creature 2)", flush=True)
        print("  laptop away:", tp(L, cx + R[0] * 60, cz + R[1] * 60), "| host 40 m down the road:", tp(b, cx + D[0] * 40, cz + D[1] * 40), flush=True)
        time.sleep(2); before = {net(a) for a in ais(b)}
        print("  host spawns a fresh creature:", b(f"devspawn {aid}"), flush=True)
        if not until(lambda: [a for a in ais(b) if net(a) not in before], 15): sys.exit("the fresh creature didn't spawn")
        c2 = [net(a) for a in ais(b) if net(a) not in before][0]
        until(lambda: find(L, c2), 20)
        a2 = find(b, c2)
        print("  host in front of it:", tp(b, *front(a2, 4)), flush=True)
        st0 = L("mp aistats")
        shots2 = kill("host", b, L, c2)
        ha, la = find(b, c2), find(L, c2)
        st1 = L("mp aistats")
        print(f"  after {shots2} shots — host's: {short(ha)}\n  laptop's: {short(la)}", flush=True)
        verdict("C REF: THE OWNER'S SHOTS KILL IT", bool(ha) and ha["died"], f"{shots2} shots (laptop needed {shots})")
        verdict("C REF: DEAD ON THE COPY", bool(la) and la["died"], f"break replays +{st1['breakFxApplied'] - st0['breakFxApplied']}")
        L("mp stop"); b("mp stop")
        if RELAY: b("net relay off"); L("net relay off")
finally:
    for name, (s, v) in gods.items():
        try: s(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
        except Exception as e: print("restore god", name, e)
    L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")

#!/usr/bin/env python3
"""Creatures in MP (two machines, relay iad by default) — the haunted mansion's workers (the user's crowdedtest save;
the host loads it and owns them). Entities.Ai: the owner's creature sees, chases and attacks remote players; copies
show the owner's state and voice; hits on a copy's breakable go to the owner, its Break is replayed on the copies.
  1 ATTACK : the laptop stands in front of a sleeping creature (on its floor, in its view) for 15 s → the host's creature
             wakes and targets the laptop's body, the laptop takes the damage; the laptop's copy shows chase/attack and
             walks (animation input), its voice plays on the laptop
  2 SHOOT  : the laptop shoots another creature from 4 m in front (up to 12 magnum shots at its chest) → the hit
             reaches the owner (it wakes there), it dies on the host AND on the laptop's copy
  3 REF    : the host shoots a third one of its own the same way (how many shots the game needs)
  tools/aimp.py [--no-build] [--relay iad|off]"""
import sys, time, math, pathlib, threading
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, DESKTOP_IP
from bench import Bridge
from roadkit import until, g, tp

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY = arg("--relay", "iad")
if RELAY == "off": RELAY = None
MAGNUM = 64

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def ais(s): return s("mp ai")["ai"]
def same(lst, a): return min(lst, key=lambda x: math.dist(x["pos"], a["pos"]))
def byname(lst, who): return next((x for x in lst if x["who"].split(" owner")[0] == who.split(" owner")[0]), None)
def short(a): return f"{a['pos']} hp {a['health']} sleep {a['sleeping']!s:<5} chase {a['chasing']!s:<5} attack {a['attacking']!s:<5} died {a['died']!s:<5} fwd {a['fwd']:>5} v {a['speed']:>5} target {a['target']}"

def spot(a, d, side=0.0):
    lk = a["look"]; n = math.hypot(*lk) or 1; lk = (lk[0] / n, lk[1] / n)
    return a["pos"][0] + lk[0] * d - lk[1] * side, a["pos"][2] + lk[1] * d + lk[0] * side

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        t = threading.Thread(target=lambda: L("!seeded")); t.start()
        game.loadsave("crowdedtest")
        t.join()
        b = Bridge()
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("relay only via", RELAY, b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
        b("mp host 27070")
        if RELAY: L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 180): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        time.sleep(8)
        hp = b("gpos")
        # on the host's own ground level (run 5: a plain tpground there put the laptop on something high by the gate —
        # it fell and died before the test began; a dead player's teleport only moves its body)
        print("  laptop to the host:", tp(L, hp['gx'] + 3, hp['gz'] + 3, hp['gy'] + 1.5)); time.sleep(6)
        if g(L, "mainscript.s.player.died") is True: sys.exit("INVALID: the laptop died before the test began")
        # asleep or not is the game's dice at load (startSleepChange): sleeping ones first
        ground = sorted([a for a in ais(b) if not a["died"] and a["pos"][1] < 3],
                        key=lambda a: (not a["sleeping"], math.hypot(a["pos"][0] - hp["gx"], a["pos"][2] - hp["gz"])))
        print("ground-floor creatures (host):", [(a["who"], a["pos"], "asleep" if a["sleeping"] else "awake") for a in ground], flush=True)
        if len(ground) < 2: sys.exit("need two live creatures on the ground floor")
        a1, a2 = ground[0], ground[1]
        print("  host away:", tp(b, a1['pos'][0] - 25, a1['pos'][2] - 25, hp['gy'] + 1.5)); time.sleep(1)   # the host away from both (out of their sight)

        # ---- 1: ATTACK
        print("\n1 ATTACK: creature", a1["who"], a1["pos"], flush=True)
        fy = a1["pos"][1] + 1.6
        x, z = spot(a1, 3.5)
        print(f"  laptop steps in front of it, to ({x:.2f}, {z:.2f}) floor below gy {fy:.2f}:", tp(L, x, z, fy), flush=True)
        for k in range(10):
            p_ = L("gpos")
            print(f"    +{0.3 * k:.1f}s laptop at ({p_['gx']:.2f}, {p_['gy']:.2f}, {p_['gz']:.2f}) died {g(L, 'mainscript.s.player.died')} "
                  f"deaths {g(L, 'TLDRevamp.Diagnostics.DeathLog.Deaths')} seat {g(L, 'mainscript.s.player.seat')}", flush=True)
            time.sleep(0.3)
        lp = L("gpos"); print("  laptop is at", lp, "\n  the host shows it:", b("mp players")["players"], flush=True)
        for row in b("mp aisee")["see"]:
            if row["ai"].split(" owner")[0] == a1["who"].split(" owner")[0]: print("  host: its view of the laptop:", row, flush=True)
        st0 = L("mp aistats"); hs0 = b("mp aistats"); d0 = g(L, "TLDRevamp.Diagnostics.DeathLog.Deaths")
        hp_min, woke, targeted, cchase, cattack, cfwd = 5.0, False, False, False, False, 0.0
        for k in range(15):
            time.sleep(1)
            ha = byname(ais(b), a1["who"]); la = byname(ais(L), a1["who"])
            if not ha or not la: print("  creature missing", ha, la); break
            hpv = g(L, "mainscript.s.player.survival.hp")
            if isinstance(hpv, (int, float)): hp_min = min(hp_min, hpv)
            woke |= not ha["sleeping"]; targeted |= "Player" in ha["target"] and "Head" in ha["target"] and ha["chasing"]
            cchase |= la["chasing"]; cattack |= la["attacking"]; cfwd = max(cfwd, abs(la["fwd"]))
            print(f"  t{k + 1:>3}s laptop hp {hpv}\n     host's  : {short(ha)}\n     laptop's: {short(la)}  gap {math.dist(ha['pos'], la['pos']):.2f} m", flush=True)
        st1 = L("mp aistats"); hs1 = b("mp aistats"); d1 = g(L, "TLDRevamp.Diagnostics.DeathLog.Deaths")
        print("  host ai stats:", hs1, "\n  laptop ai stats:", st1, flush=True)
        verdict("1 ATTACK: THE OWNER'S CREATURE SAW THE LAPTOP", hs1["remoteSeen"] > hs0["remoteSeen"] and woke, f"remoteSeen +{hs1['remoteSeen'] - hs0['remoteSeen']}, woke {woke}")
        verdict("1 ATTACK: IT HUNTS THE LAPTOP'S BODY", targeted, "")
        verdict("1 ATTACK: THE LAPTOP TOOK THE DAMAGE", st1["damageTaken"] > st0["damageTaken"] and (hp_min < 4.9 or d1 > d0),
                f"damage messages +{st1['damageTaken'] - st0['damageTaken']}, lowest hp {hp_min:.2f}, deaths +{d1 - d0}")
        verdict("1 ATTACK: THE COPY SHOWS IT (chase, attack, walking)", cchase and cattack and cfwd > 0.05, f"chase {cchase} attack {cattack} max |fwd| {cfwd:.2f}")
        verdict("1 ATTACK: ITS VOICE ON THE LAPTOP", st1["soundsApplied"] > st0["soundsApplied"], f"+{st1['soundsApplied'] - st0['soundsApplied']} sounds")

        # ---- 2: SHOOT
        if g(L, "mainscript.s.player.died") is True: until(lambda: g(L, "mainscript.s.player.died") is not True, 40)
        print("\n2 SHOOT: creature", a2["who"], a2["pos"], flush=True)
        fy = a2["pos"][1] + 1.6
        x, z = spot(a2, 4)   # in front (run 2: 'behind' was through the mansion's inner wall)
        print("  laptop in front of it:", tp(L, x, z, fy), flush=True); time.sleep(1)
        print("  laptop magnum:", L(f"devspawn {MAGNUM}"), flush=True); time.sleep(2)
        st0 = L("mp aistats"); hs0 = b("mp aistats")
        woke2 = not byname(ais(b), a2["who"])["sleeping"]
        print("  it was", "awake" if woke2 else "asleep", flush=True)
        for k in range(12):
            ha = byname(ais(b), a2["who"])
            if ha is None or ha["died"]: break
            r = L(f"mp shootpoint {ha['pos'][0]} {ha['pos'][1] + 1.2} {ha['pos'][2]} near")
            print(f"  shot {k + 1}: {str(r)[:140]} → owner {short(byname(ais(b), a2['who']))}", flush=True)
            time.sleep(1.2)
            ha = byname(ais(b), a2["who"]); woke2 |= ha is not None and not ha["sleeping"]
        time.sleep(4)
        ha = byname(ais(b), a2["who"]); la = byname(ais(L), a2["who"])
        st1 = L("mp aistats"); hs1 = b("mp aistats")
        print("  host's  :", short(ha) if ha else None, "\n  laptop's:", short(la) if la else None, flush=True)
        print("  host ai stats:", hs1, "\n  laptop ai stats:", st1, flush=True)
        verdict("2 SHOOT: THE HITS REACHED THE OWNER", hs1["breakHitsRun"] > hs0["breakHitsRun"] and st1["breakHitsSent"] > st0["breakHitsSent"],
                f"laptop sent +{st1['breakHitsSent'] - st0['breakHitsSent']}, host ran +{hs1['breakHitsRun'] - hs0['breakHitsRun']}")
        verdict("2 SHOOT: AWAKE ON THE OWNER (woke on the hit, or was)", woke2)
        verdict("2 SHOOT: DEAD ON THE OWNER", ha is not None and ha["died"])
        verdict("2 SHOOT: DEAD ON THE COPY", la is not None and la["died"], f"break replays on the laptop +{st1['breakFxApplied'] - st0['breakFxApplied']}")
        # ---- 3: the reference — the OWNER shoots one of its own creatures the same way
        if len(ground) > 2:
            a3 = ground[2]
            print("\n3 REFERENCE: the host shoots", a3["who"], a3["pos"], flush=True)
            tp(L, a3['pos'][0] - 25, a3['pos'][2] - 25, hp['gy'] + 1.5); time.sleep(1)
            x, z = spot(a3, 4)
            print("  host in front of it:", tp(b, x, z, a3['pos'][1] + 1.6), flush=True); time.sleep(1)
            print("  host magnum:", b(f"devspawn {MAGNUM}"), flush=True); time.sleep(2)
            shots3 = 0
            for k in range(12):
                ha = byname(ais(b), a3["who"])
                if ha is None or ha["died"]: break
                r = b(f"mp shootpoint {ha['pos'][0]} {ha['pos'][1] + 1.2} {ha['pos'][2]} near"); shots3 += 1
                print(f"  shot {k + 1}: {str(r)[:140]} → {short(byname(ais(b), a3['who']))}", flush=True)
                time.sleep(1.2)
            time.sleep(4)
            ha = byname(ais(b), a3["who"]); la = byname(ais(L), a3["who"])
            print("  host's  :", short(ha) if ha else None, "\n  laptop's:", short(la) if la else None, flush=True)
            verdict("3 REFERENCE: THE OWNER'S OWN SHOTS KILL IT", ha is not None and ha["died"], f"{shots3} shots")
            verdict("3 REFERENCE: DEAD ON THE COPY", la is not None and la["died"])
        L("mp stop"); b("mp stop")
        if RELAY: b("net relay off"); L("net relay off")
finally:
    L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")

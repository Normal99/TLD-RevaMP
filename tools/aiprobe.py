#!/usr/bin/env python3
"""What does the other player see of the AI characters? (two machines, relay iad by default) — a probe, not a test.
The host loads the user's crowdedtest save (the haunted mansion, 4 AI workers), the laptop joins. Both machines'
view of every AI (mp ai: shared/owner/copy, kinematic, chasing, target, animation input), then the laptop walks up to
one AI and stays 20 s: does it hunt the laptop, does the laptop take damage? Then the reference: the host on the same
spot (the laptop watching) — does it hunt the host, does the laptop's copy animate and move like the owner's?
  tools/aiprobe.py [--no-build] [--relay iad|off]"""
import sys, time, math, pathlib, threading
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, DESKTOP_IP
from bench import Bridge
from roadkit import until, g

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY = arg("--relay", "iad")
if RELAY == "off": RELAY = None

def ais(s): return s("mp ai")["ai"]
def short(a): return f"{a['who']:<26} kin {a['kin']!s:<5} died {a['died']!s:<5} sleep {a['sleeping']!s:<5} chase {a['chasing']!s:<5} " \
                     f"attack {a['attacking']!s:<5} fwd {a['fwd']:>5} v {a['speed']:>5} target {a['target']}"

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        t = threading.Thread(target=lambda: L("!seeded")); t.start()
        game.loadsave("crowdedtest")
        t.join()
        b = Bridge()
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        print("peacefulEnemies:", g(b, "gamemodesettingsscript.s.S.peacefulEnemies"), flush=True)
        if RELAY: print("relay only via", RELAY, b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
        b("mp host 27070")
        if RELAY: L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 180): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        time.sleep(8)
        hp = b("gpos")
        print("host at", hp, flush=True)
        print("HOST's AI:"); [print("  ", a["name"], a["pos"], short(a)) for a in ais(b)]
        L(f"tpground {hp['gx'] + 3} {hp['gz'] + 3}"); time.sleep(6)
        print("LAPTOP's AI (next to the host):"); [print("  ", a["name"], a["pos"], short(a)) for a in ais(L)]
        live = [a for a in ais(b) if not a["died"]]
        if not live: sys.exit("no live AI on the host")
        a0 = min(live, key=lambda a: math.hypot(a["pos"][0] - hp["gx"], a["pos"][2] - hp["gz"]))
        lk = a0["look"]; n = math.hypot(*lk) or 1; lk = (lk[0] / n, lk[1] / n)
        front = lambda d, side=0.0: (a0["pos"][0] + lk[0] * d - lk[1] * side, a0["pos"][2] + lk[1] * d + lk[0] * side)
        print("target AI:", a0["name"], a0["pos"], "looking", a0["look"], "fov", a0["fov"], "range", a0["range"], flush=True)
        fy = a0["pos"][1] + 1.6   # its floor, not the mansion's roof (run 4: tpground put us 11 m above it)
        L(f"tpground {front(4)[0]} {front(4)[1]} {fy}"); time.sleep(1)   # in front of its head, in its view
        print("laptop: the AI's vision test:", L("mp aivision"), flush=True)
        hp0 = g(L, "mainscript.s.player.survival.hp"); d0 = g(L, "TLDRevamp.Diagnostics.DeathLog.Deaths")
        for k in range(10):
            time.sleep(2)
            ha = min(ais(b), key=lambda a: math.dist(a["pos"], a0["pos"]))
            la = min(ais(L), key=lambda a: math.dist(a["pos"], ha["pos"]))
            lp = L("gpos")
            print(f"  t{2 * k + 2:>3}s laptop hp {g(L, 'mainscript.s.player.survival.hp')} at ({lp['gx']:.1f},{lp['gz']:.1f})"
                  f"\n     host's  : {ha['pos']} {short(ha)}\n     laptop's: {la['pos']} {short(la)}  gap {math.dist(ha['pos'], la['pos']):.2f} m", flush=True)
        print("laptop hp", hp0, "→", g(L, "mainscript.s.player.survival.hp"), "deaths +", g(L, "TLDRevamp.Diagnostics.DeathLog.Deaths") - d0, flush=True)
        # ---- the reference: the HOST on that spot (the laptop watches from 7 m)
        L(f"tpground {front(-6, 5)[0]} {front(-6, 5)[1]} {fy}"); time.sleep(1)    # behind it, out of its view
        b(f"tpground {front(4)[0]} {front(4)[1]} {fy}"); time.sleep(1)
        bh0 = g(b, "mainscript.s.player.survival.hp")
        print("host: the AI's vision test:", b("mp aivision"), flush=True)
        print("HOST next to it (laptop watching):", flush=True)
        for k in range(10):
            time.sleep(2)
            ha = min(ais(b), key=lambda a: math.dist(a["pos"], a0["pos"]))
            la = min(ais(L), key=lambda a: math.dist(a["pos"], ha["pos"]))
            print(f"  t{2 * k + 2:>3}s host hp {g(b, 'mainscript.s.player.survival.hp')}"
                  f"\n     host's  : {ha['pos']} {short(ha)}\n     laptop's: {la['pos']} {short(la)}  gap {math.dist(ha['pos'], la['pos']):.2f} m", flush=True)
        print("host hp", bh0, "→", g(b, "mainscript.s.player.survival.hp"), flush=True)
        L("mp stop"); b("mp stop")
        if RELAY: b("net relay off"); L("net relay off")
finally:
    L.close()

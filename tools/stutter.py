#!/usr/bin/env python3
"""The periodic ~110–128 ms frame: loadtest showed it on BOTH machines in every row, also with no bots (2 players).
Per machine, WINDOW seconds with nobody touching the bridge, the frame profiler's hitch log (PlayerLoopProfiler,
v0.57.83): every frame over HITCH ms wall time with its PlayerLoop total, 3 costliest phases, GC that frame, bridge work.
  single player : each machine its own fresh seeded world, the player standing at spawn
  MP            : the laptop joins the desktop over the Steam relay (--relay iad by default; --relay off = LAN)
  cmds          : single player, the test tools' own bridge commands one by one under the hitch log (what does each
                  cost the frame it runs in?)
  tools/stutter.py [--no-build] [--window 60] [--hitch 50] [--relay iad] [--only sp,mp,cmds]"""
import sys, time, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP
from roadkit import until

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
WINDOW, HITCH, RELAY, ONLY = arg("--window", 60.0, float), arg("--hitch", 50.0, float), arg("--relay", "iad"), arg("--only", "sp,mp")
if RELAY == "off": RELAY = None

def window(sides):
    for s in sides.values():
        s(f"set TLDRevamp.PlayerLoopProfiler.HitchMs {HITCH}"); s("reset"); s("loopprof start")
    time.sleep(WINDOW)   # nobody calls the bridge in here
    out = {}
    for n, s in sides.items():
        lp = s("loopprof stop 6"); st = s("stats")
        out[n] = {"stats": st, "lp": lp}
    return out

def report(label, res):
    print(f"\n=== {label}", flush=True)
    for n, r in res.items():
        st, lp = r["stats"], r["lp"]
        hs = lp.get("hitches", [])
        print(f"  {n}: frames {st['frames']} p50 {st['p50ms']} p99 {st['p99ms']} max {st['maxms']} ms | GCs {st['gc0']} heap {st['managedMB']} MB "
              f"| hitches > {HITCH:.0f} ms: {lp.get('hitchCount')}", flush=True)
        prev = None
        for h in hs:
            gap = f"+{h['t'] - prev:5.1f}s" if prev is not None else "      "
            prev = h["t"]
            top = ", ".join(f"{p.split('.')[-1]} {ms}" for p, ms in h["top"])
            print(f"    t {h['t']:8.2f} {gap}  wall {h['wall']:6.1f}  loop {h['loop']:6.1f}  gc {h['gc']}  bridge {h['bridge']:4.1f} | {top}", flush=True)

ensure_both("--no-build" not in sys.argv)
L = Agent()
results = {}
try:
    with game.ProtectedSession():
        b = seeded_both(L)
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        time.sleep(10)   # past the arrival (world generation around the spawn)
        sides = {"desktop": b, "laptop": L}
        if "sp" in ONLY:
            results["single player (each its own world)"] = window(sides)
            report("single player (each its own world)", results["single player (each its own world)"])
        if "cmds" in ONLY:
            print("\n=== the tools' bridge commands, one every 2 s (each its own hitch-log window; single player)", flush=True)
            prof = "TLDRevamp.Net.Mp.Tick TLDRevamp.Net.Entities.Tick TLDRevamp.Net.Entities.FixedTick"
            for n, s in sides.items():
                for cmd in ["stats", "mp entities", "mp physical", "mp contacts", "physics", f"profile start {prof}", "profile stop 20",
                            "profile start", "profile stop 20", "scene"]:
                    s(f"set TLDRevamp.PlayerLoopProfiler.HitchMs 30"); s("loopprof start"); time.sleep(1)
                    s(cmd); time.sleep(1)
                    lp = s("loopprof stop 3")
                    worst = max(lp.get("hitches", []), key=lambda h: h["wall"], default=None)
                    print(f"  {n:8} {cmd[:40]:40} hitches {lp.get('hitchCount')}" + (f"  worst wall {worst['wall']} loop {worst['loop']} bridge {worst['bridge']} gc {worst['gc']}" if worst else ""), flush=True)
        if "mp" in ONLY:
            if RELAY: print("relay only via", RELAY, b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
            b("mp host 27070")
            if RELAY: L(f"mp joinid {b('net me')['steamId']}")
            else: L(f"mp join {DESKTOP_IP}:27070")
            if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
            L("set TLDRevamp.TestSafety.BlockAutoSave True")
            if RELAY: print("route:", L("net route"), flush=True)
            time.sleep(10)
            label = f"MP, 2 players ({'relay ' + RELAY if RELAY else 'LAN'})"
            results[label] = window(sides)
            report(label, results[label])
            b("mp stop"); L("mp stop")
            if RELAY: b("net relay off"); L("net relay off")
finally:
    L.close()
print("RAW " + json.dumps(results), flush=True)

#!/usr/bin/env python3
"""Load at 10–16 players: the desktop hosts, the laptop joins as a real client, and bots that DRIVE join in steps —
each bot shares a car01 (the same record a real player shares) and streams its state at the players' rate, wheels,
engine and (as when near other players) wheel torques included, circling the host at SPEED within RADIUS: every car
near every player, the worst case for the host's relaying. Bots run inside the host's game (loopback connections):
their own CPU (MpClient.Tick) is timed separately and left out of the host's cost.
Per step (players = bots + host + laptop), over a WINDOW-second window after the new cars have arrived:
  host   : frame p50/p99/max, CPU in our networking (Mp.Tick incl. the server's receive/relay, Entities.Tick/FixedTick),
           upload/download KB/s, messages/s, car states relayed/s, worst Steam send queue
  laptop : frame p50/p99/max, download/upload KB/s, the bot cars' copies (count, visible jumps, extrapolated frames)
  tools/loadtest.py [--no-build] [--steps 0,3,7,11,14] [--radius 100] [--speed 16] [--window 20] [--relay <dc>] [--no-torques] [--away 250] [--prof Method,Type.Method,...]"""
import sys, time, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP
from roadkit import CAR01, until, road_at
import math

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
STEPS = [int(x) for x in arg("--steps", "0,3,7,11,14").split(",")]
RADIUS, SPEED, WINDOW = arg("--radius", 100.0, float), arg("--speed", 16.0, float), arg("--window", 20.0, float)
RELAY = arg("--relay", None)
AWAY = arg("--away", 250.0, float)
PROFILE = ["TLDRevamp.Net.Mp.Tick", "TLDRevamp.Net.MpServer.Tick", "TLDRevamp.Net.MpClient.Tick",
           "TLDRevamp.Net.Entities.Tick", "TLDRevamp.Net.Entities.FixedTick", "TLDRevamp.Net.Entities.ServerReceive",
           "TLDRevamp.Net.Entities.ClientReceive", "TLDRevamp.Net.RemotePlayers.Tick"]
PROFILE += [m if "." in m else "TLDRevamp.Net.Entities." + m for m in arg("--prof", "").split(",") if m]   # drill-down: more methods timed

def netsum(a, z, dt):
    return {"up_KBps": round((z["bytesOut"] - a["bytesOut"]) / 1024 / dt, 1), "down_KBps": round((z["bytesIn"] - a["bytesIn"]) / 1024 / dt, 1),
            "msgOut_s": round((z["msgOut"] - a["msgOut"]) / dt), "msgIn_s": round((z["msgIn"] - a["msgIn"]) / dt)}

def watch(seconds, b):
    """Every 2 s a line of the host's copy-physics/crash counters and frame time (a hang shows what led up to it)."""
    t0 = time.time()
    while time.time() - t0 < seconds:
        ph, ca, st, co = b("mp physical"), b("mp crashauth"), b("stats"), b("mp contacts")
        print(f"   t+{time.time() - t0:4.0f}s host p50 {st['p50ms']} max {st['maxms']} | physicalNow {ph['physicalNow']} enters {ph['enters']} "
              f"freeWindows {ph['freeWindows']} | crash claims {ca['claims']} granted {ca['granted']} refused {ca['refused']} held {ca['held']} {ca['last']} "
              f"| contacts {co['total']} sounds {co['soundClips']} | audio {co['audioSources']} ({co['audioPlaying']} playing) {co['audioRoots']}", flush=True)
        for k, n in co["top"][:5]: print(f"        {n:>7}  {k}", flush=True)
        time.sleep(2)

rows = []
ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("relay only via", RELAY, b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
        b(f"set TLDRevamp.Net.MpServer.CarTiers {'False' if '--no-cartiers' in sys.argv else 'True'}")   # A/B (v0.57.89)
        b("mp host 27070")
        if RELAY: L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("route:", L("net route"), flush=True)
        # the car every bot drives: a car01 next to the host, captured, then removed
        # away from spawn: the start house and the host's start bike (a car it simulates: copies near it turn physical)
        # sat on the bots' circle — they drove through the house (census, v0.57.81); a hang twice in 3 convoy runs
        rp = b("roadpoint")
        if AWAY > 0:
            hy = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
            rp = road_at(b, rp["gx"] + hy[0] * AWAY, rp["gz"] + hy[1] * AWAY); time.sleep(4)
            rp = b("roadpoint")
            print(f"test site {AWAY:.0f} m along the road from spawn:", rp, flush=True)
        print("template car:", b(f"spawncarg {CAR01} 0 {rp['gx'] + 6} {rp['gz']} 0"), flush=True); time.sleep(3)
        b(f"tpground {rp['gx'] + 8} {rp['gz'] + 1}"); time.sleep(2)   # right next to it: `mp botcar` takes the nearest car
        bc = b("mp botcar"); print("bot car:", bc, flush=True)
        print("bot car:", bc, b("clearpile"), flush=True)
        if "Car01" not in bc.get("car", ""): sys.exit(f"the bots would drive {bc.get('car')}, not the car01")
        b("set TLDRevamp.Net.Mp.BotCars True"); b("mp contacts on")
        b(f"set TLDRevamp.Net.Mp.BotTorques {'--no-torques' not in sys.argv}")
        # the laptop stands next to the host: every bot car is near it too
        L(f"tpground {rp['gx'] + 3} {rp['gz'] + 3}"); time.sleep(3)
        have = 0
        for target in STEPS:
            if target > have:
                print(f"\n+{target - have} bots", b(f"mp bots {target - have} {RADIUS} {SPEED}")["bots"], flush=True)
                have = target
            players = have + 2
            # the new cars reach both machines (Add → spawn) and the interpolators settle
            want = have
            until(lambda: sum(1 for e in L("mp entities")["entities"] if e["car"] and e["proxy"]) >= want, 40, 1.0)
            watch(8, b)
            # the profiler first, then the frame counters: hooking methods (Harmony) stalls the frame it runs in —
            # 8 methods ~110 ms; inside the window it was the "periodic ~110–128 ms stutter" (stutter.py, v0.57.83)
            b("profile start " + " ".join(PROFILE)); L("profile start " + " ".join(PROFILE))
            time.sleep(1)
            for s in (b, L): s("reset"); s("mp entstats")
            n0 = {id(s): s("net stats") for s in (b, L)}; srv0 = b("mp status")["server"]; t0 = time.time()
            qmax = 0
            while time.time() - t0 < WINDOW:
                time.sleep(2)
                for c in b("net stats")["conns"]: qmax = max(qmax, c["pendingUnreliable"] + c["pendingReliable"], c["queueUs"] // 1000)
            dt = time.time() - t0
            n1 = {id(s): s("net stats") for s in (b, L)}; srv1 = b("mp status")["server"]
            fb, fl = b("stats"), L("stats")   # before unhooking (same stall)
            ents = L("mp entities")["entities"]; jl = L("mp jumps")["jumps"]   # the stall made every copy "jump" (v0.57.84 log)
            prof = b("profile stop 20"); lprof = L("profile stop 20")
            ms = {p["m"].split("(")[0]: p["msPerSec"] for p in prof.get("byTime", [])}
            copies = [e for e in ents if e["car"] and e["proxy"]]
            row = {"players": players, "bots": have,
                   "host": {"p50ms": fb["p50ms"], "p99ms": fb["p99ms"], "maxms": fb["maxms"], "gc": fb["gc0"],
                            "net": netsum(n0[id(b)], n1[id(b)], dt), "statesRelayed_s": round((srv1["statesRelayed"] - srv0["statesRelayed"]) / dt),
                            "carStatesSent_s": round((srv1.get("carStatesSent", 0) - srv0.get("carStatesSent", 0)) / dt),
                            "carStatesThinned_s": round((srv1.get("carStatesThinned", 0) - srv0.get("carStatesThinned", 0)) / dt),
                            "carTiers": [b_ - a_ for a_, b_ in zip(srv0.get("carTiers", [0, 0, 0, 0]), srv1.get("carTiers", [0, 0, 0, 0]))],
                            "worstQueue": qmax, "cpu_ms_per_s": ms},
                   "laptop": {"p50ms": fl["p50ms"], "p99ms": fl["p99ms"], "maxms": fl["maxms"], "gc": fl["gc0"],
                              "net": netsum(n0[id(L)], n1[id(L)], dt), "carCopies": len(copies),
                              "jumps": sum(e.get("jumps", 0) for e in copies), "extrapFrames": sum(e.get("extrapFrames", 0) for e in copies),
                              "frames": sum(e.get("frames", 0) for e in copies),
                              "cpu_ms_per_s": {p["m"].split("(")[0]: p["msPerSec"] for p in lprof.get("byTime", [])}}}
            srv = b("mp status")["server"]; row["host"]["batches"] = srv.get("batches"); row["host"]["batchedMsgs"] = srv.get("batchedMsgs")
            # what the laptop's copies were doing in each visible jump (v0.57.84 jump log)
            if jl:
                held = sum(1 for j in jl if j[5] > 250); ex = sum(1 for j in jl if 0 < j[5] <= 250)
                corr = sum(1 for j in jl if j[6] > 5); vert = sum(1 for j in jl if j[3] > 0.7 * j[2])
                slow = sum(1 for j in jl if j[4] > 2 * fl["p50ms"])
                row["laptop"]["jumpKinds"] = {"n": len(jl), "held(>250ms ahead)": held, "extrapolating": ex, "correcting(>5cm)": corr,
                                              "vertical": vert, "slowFrame(>2x p50)": slow, "body": sum(1 for j in jl if j[7]),
                                              "nets": len({j[1] for j in jl})}
                print("  jump kinds:", row["laptop"]["jumpKinds"], flush=True)
                for j in jl[:12]: print(f"    t {j[0]:8.2f} net {j[1]:>3} dev {j[2]:>4} cm (vert {j[3]:>3}) frame {j[4]:5.1f} ms ahead {j[5]:>5} ms corr {j[6]:>4} cm body {j[7]}", flush=True)
            rows.append(row)
            print(json.dumps(row), flush=True)
        b("mp stop"); L("mp stop")
finally:
    try:
        if RELAY: b("net relay off"); L("net relay off")
    except Exception: pass
    L.close()

print("\nLOAD TABLE (window %.0f s, bots circling within %.0f m at %.0f m/s)" % (WINDOW, RADIUS, SPEED))
print(f"{'players':>7} | {'host p50/p99 ms':>15} {'host up KB/s':>12} {'down':>6} {'msg out/s':>9} {'net CPU ms/s':>12} {'queue':>5} | {'laptop p50/p99':>14} {'down KB/s':>9} {'net CPU':>7} {'copies':>6} {'jumps':>5}")
for r in rows:
    h, l = r["host"], r["laptop"]
    print(f"        car states/s sent {h.get('carStatesSent_s')} thinned {h.get('carStatesThinned_s')} by tier {h.get('carTiers')} | player states/s {h['statesRelayed_s']}")
    cpu = sum(v for k, v in h["cpu_ms_per_s"].items() if k.endswith("Mp.Tick") or "FixedTick" in k) - sum(v for k, v in h["cpu_ms_per_s"].items() if "MpClient.Tick" in k)
    print(f"{r['players']:>7} | {h['p50ms']:>6}/{h['p99ms']:<8} {h['net']['up_KBps']:>12} {h['net']['down_KBps']:>6} {h['net']['msgOut_s']:>9} {cpu:>12.2f} {h['worstQueue']:>5} | "
          f"{l['p50ms']:>6}/{l['p99ms']:<7} {l['net']['down_KBps']:>9} {l['cpu_ms_per_s'].get('Mp.Tick', 0) + l['cpu_ms_per_s'].get('Entities.FixedTick', 0):>7.1f} {l['carCopies']:>6} {l['jumps']:>5}")

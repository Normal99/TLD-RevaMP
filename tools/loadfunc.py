#!/usr/bin/env python3
"""F2: interactions at 10 players (two machines + 8 driving bots, relay iad by default). The bots join and circle the
host's spot in cars (the worst case for relaying: every car near every player); with them running, the real players do
what players do and both machines must agree:
  1 the laptop picks up an item the host spawned, holds it, drops it: the host's copy follows and rests where it fell
  2 the laptop rides as a passenger while the host drives: each sees the other seated in the car
  3 the car's parts on both machines unchanged; frame times; errors
  tools/loadfunc.py [--no-build] [--relay iad|off] [--bots 8]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g, car_ready, clear_road

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
BOTS = int(sys.argv[sys.argv.index("--bots") + 1]) if "--bots" in sys.argv else 8
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    gods = {}; shift_h = g(h, "settingsscript.s.S.BAutoShift")
    try:
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
        h("set settingsscript.s.S.BAutoShift True")
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        h(f"tpground {r0['gx'] + R[0] * 6} {r0['gz'] + R[1] * 6}"); L(f"tpground {r0['gx'] + R[0] * 8} {r0['gz'] + R[1] * 8}"); time.sleep(3)
        # the bots' cars: a car01 template, as loadtest.py sets them up (`mp botcar` takes the nearest car)
        print("template car:", h(f"spawncarg 2 0 {r0['gx'] + R[0] * 30} {r0['gz'] + R[1] * 30} {yaw}"), flush=True); time.sleep(3)
        h(f"tpground {r0['gx'] + R[0] * 33} {r0['gz'] + R[1] * 33}"); time.sleep(2)
        print("bot car:", h("mp botcar"), h("clearpile"), flush=True)
        h("set TLDRevamp.Net.Mp.BotCars True")
        h(f"tpground {r0['gx'] + R[0] * 6} {r0['gz'] + R[1] * 6}"); time.sleep(2)
        print(f"+{BOTS} bots:", h(f"mp bots {BOTS} 100 16").get("bots"), flush=True)
        t0 = time.monotonic()
        while time.monotonic() - t0 < 120:
            nl = len([e for e in L("mp entities")["entities"] if e["car"] and e["part"] < 0]); nh = len([e for e in h("mp entities")["entities"] if e["car"] and e["part"] < 0])
            st = h("mp status"); bs = st.get("bots") or {k: v for k, v in st.items() if "bot" in k.lower()}
            print(f"  +{time.monotonic() - t0:.0f}s cars: host {nh}, laptop {nl}; bots {bs}", flush=True)
            if nl >= BOTS: break
            time.sleep(10)
        time.sleep(10)
        print("players on the host:", len(h("mp players")["players"]), "| cars the laptop sees:", len([e for e in L("mp entities")["entities"] if e["car"] and e["part"] < 0]), flush=True)
        for s_ in (h, L): s_("stats reset") if False else None

        # 1 an item: the host spawns it, the laptop picks it up and drops it
        hb = {e["net"] for e in h("mp entities")["entities"]}
        print("host spawns a can:", h("devspawn 117"), flush=True)
        until(lambda: any(e["net"] not in hb and not e["car"] for e in h("mp entities")["entities"]), 20)
        can = next(e["net"] for e in h("mp entities")["entities"] if e["net"] not in hb and not e["car"])
        until(lambda: any(e["net"] == can for e in L("mp entities")["entities"]), 20); time.sleep(2)
        def erow(x, n): return next((e for e in x("mp entities")["entities"] if e["net"] == n), None)
        ce = erow(L, can); L(f"tpground {ce['pos'][0] + 1.0} {ce['pos'][2]}"); time.sleep(2)
        print("  laptop picks it up:", L(f"mp pickupnet {can}"), flush=True); time.sleep(3)
        held_gap = math.dist(erow(L, can)["pos"], erow(h, can)["pos"])
        print("  laptop drops it:", L("dropheld"), flush=True); time.sleep(5)
        a, b = erow(L, can), erow(h, can)
        verdict("1 ITEM PICKED UP AND DROPPED UNDER LOAD: BOTH AGREE", held_gap < 1.0 and math.dist(a["pos"], b["pos"]) < 0.1,
                f"held: copy {held_gap:.2f} m off; dropped: {math.dist(a['pos'], b['pos']):.3f} m apart; owner {b['owner']}")

        # 2 the laptop rides along while the host drives
        if not car_ready(h, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        me = h("mp players")["me"]
        net = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"]), key=lambda e: math.dist(e["pos"][::2], me[::2]))["net"]
        until(lambda: erow(L, net) is not None, 20)
        def census(x): return {(r[0], r[1]) for r in x(f"mount slots {net}")["slots"] if r[2]}
        c0 = {"host": census(h), "laptop": census(L)}
        seats = L(f"seats {net}")["seats(i,name,body,driver,redirect,free,active)"]
        pas = next(s[0] for s in seats if not s[3] and not s[4] and s[5])
        e = erow(L, net); L(f"tpground {e['pos'][0] - R[0] * 2.5} {e['pos'][2] - R[1] * 2.5}"); time.sleep(2)
        print("  laptop sits in seat", pas, ":", L(f"sitseat {pas} redirect net={net}"), flush=True); time.sleep(2)
        h("autodrive on 10")
        dh, dl, fl = [], [], []
        t0 = time.monotonic()
        while time.monotonic() - t0 < 25:
            for p in h("mp players")["players"]:
                if p["id"] == 1 and p["seatNet"] == net and p.get("seatCarDist", -1) >= 0: dh.append(p["seatCarDist"])
            for p in L("mp players")["players"]:
                if p["id"] == 0 and p["seatNet"] == net and p.get("seatCarDist", -1) >= 0: dl.append(p["seatCarDist"])
            time.sleep(1)
        print(f"  host's speed {g(h, 'mainscript.s.player.Car.speed')} km/h; laptop sitting {g(L, 'mainscript.s.player.Bsitting')}", flush=True)
        h("mp lookat " + str(net)) if False else None
        L("shot loadfunc_ride"); h("shot loadfunc_ride")
        h("autodrive off"); h("brakecar"); time.sleep(3)
        c1 = {"host": census(h), "laptop": census(L)}
        verdict("2 RIDING ALONG UNDER LOAD: EACH SEES THE OTHER SEATED", len(dh) >= 15 and len(dl) >= 15 and max(dh) < 1.5 and max(dl) < 1.5,
                f"laptop's body on the host: max {max(dh) if dh else None} m ({len(dh)}); host's body on the laptop: max {max(dl) if dl else None} m ({len(dl)})")
        verdict("3 NO PART LOST", c0 == c1, f"lost host {sorted(c0['host'] - c1['host'])} laptop {sorted(c0['laptop'] - c1['laptop'])}")
        st = {n: x("net stats") for n, x in (("host", h), ("laptop", L))}
        print("  net stats:", {n: {k: v.get(k) for k in ("ping", "downKBs", "upKBs") if k in v} for n, v in st.items()}, flush=True)
        er = [x("errors") for x in (h, L)]
        verdict("NO ERRORS", all(x["errors"] == 0 and x["exceptions"] == 0 for x in er), str([(x["errors"], x["exceptions"], (x.get('kinds') or [])[:3]) for x in er]))
        print("players at the end:", len(h("mp players")["players"]) + 1, flush=True)
    finally:
        try: h("set TLDRevamp.Net.Mp.BotCars False")
        except Exception: pass
        try: h(f"set settingsscript.s.S.BAutoShift {shift_h}")
        except Exception as ex: print("shift restore", ex)
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

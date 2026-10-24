#!/usr/bin/env python3
"""Engine temperature on car copies (protocol 56, Entities.Proxy WriteEngine/ReadEngine). A copy skips
enginescript.Update (it would burn the watcher's fuel), so before protocol 56 nothing heated its engine and the owner's
temperature was never sent: the copy's gauge sat at its load value and its coolant never boiled. Two clients over the
relay: the host drives its car01, the joiner stands next to it; both read the car's engine temperature, coolant boil
and steam (`mp entities`: engTemp, boil, steam).
  1 running         the engine runs; the owner's temperature set warm, then left to the game: the copy follows within
                    a degree (sampled every second)
  2 boiling         the owner's temperature set above the steam point: the copy boils and steams too
  3 parked, cooling ignition off: the cooling engine (no rpm, no motion) still reaches the copy, a degree at a time
  4 late joiner     the joiner leaves and joins again: its new copy starts at the owner's temperature
  tools/coolantsync.py [--no-build] [--relay iad|off] [--old: copies ignore the temperature (EngineTempSync off)]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import g, car_ready, clear_road

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
T = "TLDRevamp.Net.Entities"
E = "mainscript.s.player.Car.Engine."
VERDICTS, MISSING = [], [0]
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def row(s, net): return next((e for e in s("mp entities")["entities"] if e["net"] == net), {})

def track(h, L, net, secs, tag):
    """owner and copy temperatures once a second: the largest gap"""
    gaps = []
    for _ in range(int(secs)):
        o, c = row(h, net), row(L, net)
        if "engTemp" in o and "engTemp" in c: gaps.append(abs(o["engTemp"] - c["engTemp"]))
        elif MISSING[0] < 3:   # the copy not loaded on the joiner: why (its row, where the joiner stands)
            MISSING[0] += 1; me = L("gpos")
            print(f"  {tag}: NO LOADED COPY on the joiner: row {({k: c.get(k) for k in ('net', 'proxy', 'dormant', 'stored', 'held', 'pos', 'owner')} if c else None)}, "
                  f"joiner at {me.get('gx')},{me.get('gz')}, car at {o.get('pos')}, joiner entities {len(L('mp entities')['entities'])}", flush=True)
        print(f"  {tag}: owner {o.get('engTemp')} running {o.get('engRunning')} boil {o.get('boil')} steam {o.get('steam')} | "
              f"copy {c.get('engTemp')} boil {c.get('boil')} steam {c.get('steam')}", flush=True)
        time.sleep(1)
    return gaps

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    gods = {}
    try:
        if not join_both(h, L): sys.exit("JOINER NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for name, s_ in (("host", h), ("joiner", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
        L(f"set {T}.EngineTempSync {not OLD}")
        print(f"  {'OLD (copies ignore the temperature)' if OLD else 'CURRENT BUILD'}", flush=True)
        steam_at, top = float(g(h, "mainscript.s.minSteamTemp")), float(g(h, "mainscript.s.maxCarTemp"))
        print(f"  steam from {steam_at}, max {top}", flush=True)
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
        R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        if not car_ready(h, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        me = h("mp players")["me"]
        net = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
        L(f"tpground {me[0] + R[0] * 4} {me[2] + R[1] * 4}"); time.sleep(4)
        # the engine started by the test driver, then the car stopped with it running
        h("autodrive on 6"); time.sleep(8); h("autodrive off")
        h(f"set {T}.TestHandbrake 1"); time.sleep(3)   # left unbraked it rolled on, ~300 m from the joiner by phase 3
        def near():   # the joiner next to the car (a copy far away is only a record: no engine to read)
            at_ = row(h, net)["pos"]; L(f"tpground {at_[0] + R[0] * 4} {at_[2] + R[1] * 4}"); time.sleep(3)
        near()
        print("  coolant in the tank: owner", row(h, net).get("coolant"), "copy", row(L, net).get("coolant"), flush=True)
        verdict("SETUP: THE ENGINE RUNS", g(h, E + "running") is True and row(L, net).get("engRunning") is True)
        # 1 running: warm, then the game's own heating
        h(f"set {E}temp {steam_at - 15:.1f}")
        gaps = track(h, L, net, 12, "1 running")
        verdict("1 RUNNING: THE COPY'S TEMPERATURE FOLLOWS THE OWNER'S", len(gaps) >= 8 and max(gaps[2:]) < 1.5,
                f"largest gap after 2 s: {max(gaps[2:]) if len(gaps) > 2 else None:.2f} degrees ({len(gaps)} samples)")
        # 2 boiling: held above the steam point (the running engine's coolant pulls it down by degrees a second)
        smoke = [g(s_, "settingsscript.s.S.BSmoke") for s_ in (h, L)]
        print(f"  smoke setting (steam is drawn only with it on): owner {smoke[0]} copy {smoke[1]}", flush=True)
        boiled = []
        for _ in range(6):
            h(f"set {E}temp {(steam_at + top) / 2:.1f}"); time.sleep(1.2)
            o, c = row(h, net), row(L, net)
            boiled.append((o.get("boil"), c.get("boil"), o.get("steam", 0) > 0, c.get("steam", 0) > 0))
            print(f"  2 boiling: owner {o.get('engTemp')} boil {o.get('boil')} steam {o.get('steam')} | copy {c.get('engTemp')} boil {c.get('boil')} steam {c.get('steam')}", flush=True)
        both = sum(1 for b in boiled if b[0] and b[1])
        steam_ok = smoke[0] != smoke[1] or all(b[2] == b[3] for b in boiled if b[0] and b[1])
        verdict("2 BOILING: THE COPY BOILS AND STEAMS LIKE THE OWNER", both >= 4 and steam_ok,
                f"both boiling in {both}/{len(boiled)} samples; steam alike {steam_ok} (smoke owner {smoke[0]} copy {smoke[1]})")
        # 3 parked, cooling (ignition off; the car at rest, so only the temperature makes a state)
        h(f"set {E}temp {steam_at - 5:.1f}")
        h(f"set {T}.TestIgnition 0"); near()
        t0 = row(h, net).get("engTemp")
        gaps = track(h, L, net, 20, "3 cooling")
        o = row(h, net)
        verdict("3 PARKED, COOLING: THE COPY COOLS WITH THE OWNER", o.get("engRunning") is False and max(gaps[3:]) < 1.5,
                f"owner {t0} -> {o.get('engTemp')}, largest gap {max(gaps[3:]):.2f} degrees")
        # 4 late joiner
        L("mp stop"); time.sleep(3)
        h(f"set {E}temp {steam_at - 25:.1f}"); time.sleep(2)
        if not join_both(h, L, rehost=False): sys.exit("JOINER NEVER JOINED AGAIN")
        L(f"set {T}.EngineTempSync {not OLD}")
        at = row(h, net)["pos"]
        me = L("gpos"); print(f"  4 rejoined at {me.get('gx')},{me.get('gz')}: {math.dist((me['gx'], me['gz']), (at[0], at[2])):.0f} m from the car; walking up to it", flush=True)
        L(f"tpground {at[0] + R[0] * 4} {at[2] + R[1] * 4}")
        def copy_car():   # the joiner's ids for the car: found by where it stands
            cars = [e for e in L("mp entities")["entities"] if e["part"] < 0 and e["car"] and "engTemp" in e]
            return min(cars, key=lambda e: math.dist(e["pos"], at), default={})
        t_end = time.time() + 60
        while time.time() < t_end and not copy_car(): time.sleep(1)
        time.sleep(2)
        o, c = row(h, net), copy_car()
        print(f"  4 rejoined: owner net {net} {o.get('engTemp')} | copy net {c.get('net')} {c.get('engTemp')} at {math.dist(c['pos'], at) if c else None} m", flush=True)
        verdict("4 LATE JOINER: THE NEW COPY HAS THE OWNER'S TEMPERATURE", "engTemp" in c and abs(o["engTemp"] - c["engTemp"]) < 1.5,
                f"owner {o.get('engTemp')} copy {c.get('engTemp')}")
        er = [x_("errors") for x_ in (h, L)]
        print("  errors:", [(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er], flush=True)
    finally:
        try: h("autodrive off"); h(f"set {T}.TestIgnition -1"); h(f"set {T}.TestHandbrake -1"); L(f"set {T}.EngineTempSync True")
        except Exception: pass
        for s_, v_ in gods.values():
            try: s_(f"set kaposztaleves.s.settings.god {v_}")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"COOLANTSYNC: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")
sys.exit(1 if fails else 0)

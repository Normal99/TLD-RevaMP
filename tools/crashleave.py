#!/usr/bin/env python3
"""A joining player's game crashes mid-drive (two machines, relay iad by default). The laptop drives its own car at
~50 km/h, then its game process is killed: no goodbye, the host only learns from Steam's connection timeout. On the
host the car must go on as a car does with nobody at the wheel — no freeze-then-lurch, no jump when the host takes it
over, upright, every part still on — and the player's body must go. The laptop's game is restarted afterwards.
  tools/crashleave.py [--no-build] [--relay iad|off] [--kmh 50] [--silent <s>: the host's silent take-over, 0 = off] [--hang <s>: pause the laptop's game instead of killing it]"""
import sys, time, math, pathlib, subprocess
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
import tldenv
from mplib import Agent, ensure_both, seeded_both, REMOTE_HOST
from roadkit import until, g, car_ready, clear_road

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
KMH = float(sys.argv[sys.argv.index("--kmh") + 1]) if "--kmh" in sys.argv else 50.0
SHOTS = tldenv.GAME_DIR / "TheLongDrive_Data"
VERDICTS = []
import atexit
atexit.register(lambda: (print("\nSUMMARY"), [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]))
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

HANG = float(sys.argv[sys.argv.index("--hang") + 1]) if "--hang" in sys.argv else 0.0
SSH = tldenv.ssh_args(REMOTE_HOST)

def hang_test(net, hrow, parts0):
    """--hang: the laptop's game stops answering for HANG seconds (the whole process paused, as a hang), then goes on.
    The host takes the car over meanwhile (past SilentTakeoverS); the driver must get it back and drive on."""
    def lrow(): return next((e for e in L("mp entities")["entities"] if e["net"] == net), None)
    t0 = time.monotonic()
    subprocess.run(SSH + [f"pkill -STOP -f 'TheLongDrive[.]exe'; sleep {HANG}; pkill -CONT -f 'TheLongDrive[.]exe'"])
    print(f"  the laptop's game was paused {time.monotonic() - t0:.1f} s; host's copy now owner {hrow()['owner']}", flush=True)
    back = None
    t1 = time.monotonic()
    while time.monotonic() - t1 < 20:
        e, le = hrow(), lrow()
        if back is None and e and le and e["owner"] == 1 and not le["proxy"]: back = time.monotonic() - t1; print(f"  +{back:.1f}s the driver has its car back", flush=True)
        time.sleep(0.5)
    time.sleep(2)
    e, le = hrow(), lrow(); spd = abs(float(g(L, "mainscript.s.player.Car.speed") or 0))
    verdict("A HANGING DRIVER GETS THEIR CAR BACK", back is not None, f"after {back if back is None else round(back, 1)} s; took over {h('mp entities').get('silentTakeovers')}x")
    verdict("AND DRIVES ON", spd > KMH * 0.6 and g(L, "mainscript.s.player.Bsitting") is True, f"laptop at {spd:.0f} km/h, sitting {g(L, 'mainscript.s.player.Bsitting')}")
    verdict("STILL IN THE SESSION, EVERY PART ON", len(h("mp players")["players"]) == 1 and e["attached"] == parts0 and le["attached"] == parts0,
            f"players on the host {len(h('mp players')['players'])}, parts host {e['attached']} laptop {le['attached']} (before {parts0})")
    h(f"mp lookat {net}"); h("shot hang_after")
    for n, x in (("host", h), ("laptop", L)):
        er = x("errors"); verdict(f"NO ERRORS ({n})", er["errors"] == 0 and er["exceptions"] == 0, str((er.get('kinds') or [])[:4]))
    L("autodrive off"); L("brakecar")

L = Agent()
ensure_both("--no-build" not in sys.argv)
killed = False
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    god_h = g(h, "kaposztaleves.s.settings.god"); h("set kaposztaleves.s.settings.god True")
    if "--silent" in sys.argv: h(f"set TLDRevamp.Net.Entities.SilentTakeoverS {sys.argv[sys.argv.index('--silent') + 1]}")   # 0 = off (A)
    shift_l = g(L, "settingsscript.s.S.BAutoShift"); print("laptop's automatic shifting (the player's setting):", shift_l, flush=True)
    try:
        L("set kaposztaleves.s.settings.god True")
        if shift_l is not True: L("set settingsscript.s.S.BAutoShift True")   # DriveLab needs it; not persisted by a kill — checked after the restart
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(L, rp["gx"], rp["gz"], F[0], F[1])
        yaw = r0["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        if not car_ready(L, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "laptop"): sys.exit("LAPTOP NOT IN ITS CAR")
        me = L("mp players")["me"]
        net = min((e for e in L("mp entities")["entities"] if e["part"] < 0 and e["car"]), key=lambda e: math.dist(e["pos"][::2], me[::2]))["net"]
        until(lambda: any(e["net"] == net for e in h("mp entities")["entities"]), 20)
        h(f"tpground {r0['gx'] - R[0] * 8} {r0['gz'] - R[1] * 8}"); time.sleep(2)   # the host beside the road, the car in its near tier
        def hrow(): return next((e for e in h("mp entities")["entities"] if e["net"] == net), None)
        parts0 = hrow()["attached"]; print("host's copy before:", {k: hrow()[k] for k in ("owner", "proxy", "attached", "upy")}, flush=True)
        print("laptop drives:", L(f"autodrive on {KMH / 3.6:.1f}"), flush=True)
        until(lambda: abs(float(g(L, "mainscript.s.player.Car.speed") or 0)) > KMH * 0.8, 40, 0.5)
        # the host follows the car at a walk's distance: keep it in the near tier, and in view for the screenshots
        for _ in range(4):
            e = hrow(); h(f"tpground {e['pos'][0] - R[0] * 10} {e['pos'][2] - R[1] * 10}"); time.sleep(1.5)
        e = hrow(); v0 = math.hypot(e["vel"][0], e["vel"][2]) * 3.6 if "vel" in e else float("nan")
        print(f"at the kill: host's copy {v0:.0f} km/h, owner {e['owner']}, players on the host {len(h('mp players')['players'])}", flush=True)
        if HANG:
            hang_test(net, hrow, parts0)
            raise SystemExit   # the laptop goes on: cleaned up below (finally), summary at exit
        L.close()
        t_kill = time.monotonic()
        subprocess.run(SSH + ["pkill -f 'TheLongDrive[.]exe'"])
        killed = True
        rows, t_gone, t_owner = [], None, None
        while time.monotonic() - t_kill < 45:
            t = time.monotonic() - t_kill; e = hrow(); pl = h("mp players")["players"]
            if e: rows.append((t, e))
            if t_gone is None and not pl: t_gone = t; print(f"  t+{t:.1f}s the host dropped the player", flush=True)
            if t_owner is None and e and e["owner"] == 0 and not e["proxy"]:
                print("  ", h("mp entities").get("silentTakeovers"), "silent take-over(s)", flush=True)
                t_owner = t; print(f"  t+{t:.1f}s the host owns the car: {e['pos']} vel {e.get('vel')}", flush=True)
                h(f"mp lookat {net}"); h("shot crash_takeover")
            if t_owner is not None and t - t_owner > 12: break
            time.sleep(0.25)
        h(f"mp lookat {net}"); h("shot crash_after")
        # speed between samples (global positions): the copy's path before and after the take-over
        steps = []
        for (ta, a), (tb, b) in zip(rows, rows[1:]):
            if tb > ta: steps.append((tb, math.dist(a["pos"], b["pos"]) / (tb - ta) * 3.6, a["owner"], b["owner"]))
        for t, kmh, oa, ob in steps:
            if oa != ob or kmh > KMH * 1.5 or int(t * 4) % 8 == 0: print(f"    t+{t:5.1f}s {kmh:6.1f} km/h owner {oa}->{ob}", flush=True)
        end = rows[-1][1] if rows else {}
        verdict("THE HOST NOTICED THE CRASH", t_gone is not None and t_gone < 30, f"player gone after {t_gone if t_gone is None else round(t_gone, 1)} s")
        verdict("THE HOST TOOK THE CAR OVER", t_owner is not None, f"after {t_owner if t_owner is None else round(t_owner, 1)} s")
        # the stall: the longest stretch the moving car stood still on the host's screen (v0.65.23: 12.5 s, all of
        # Steam's timeout — then it rolled off driverless)
        stall, run0 = 0.0, None
        for t, kmh, oa, ob in steps:
            if kmh < 5: run0 = t if run0 is None else run0; stall = max(stall, t - run0)
            else: run0 = None
        verdict("THE CAR DOESN'T FREEZE MID-ROAD", stall < 3.0, f"longest standstill {stall:.1f} s (silent take-over after {g(h, 'TLDRevamp.Net.Entities.SilentTakeoverS')} s)")
        jump = max((kmh for t, kmh, oa, ob in steps if oa != ob), default=0)
        verdict("NO JUMP AT THE TAKE-OVER", jump < KMH * 1.5, f"speed across the hand-over step {jump:.0f} km/h (driving {KMH:.0f})")
        wild = [round(kmh) for t, kmh, oa, ob in steps if kmh > KMH * 1.6]
        verdict("NO WILD MOTION", not wild, f"steps over {KMH * 1.6:.0f} km/h: {wild[:8]}")
        verdict("UPRIGHT, EVERY PART ON", end.get("upy", 0) > 0.8 and end.get("attached") == parts0, f"upy {end.get('upy')}, parts {end.get('attached')} (before {parts0})")
        er = h("errors"); verdict("NO ERRORS ON THE HOST", er["errors"] == 0 and er["exceptions"] == 0, str({k: v for k, v in er.items() if k != 'kinds'}) + " " + str((er.get('kinds') or [])[:4]))
    finally:
        try: h(f"set kaposztaleves.s.settings.god {god_h if god_h is not None else False}")
        except Exception as ex: print("god restore", ex)
        h("mp stop")
        if RELAY != "off": h("net relay off")
        if not killed:
            try: L(f"set settingsscript.s.S.BAutoShift {shift_l}"); L("set kaposztaleves.s.settings.god False"); L("mp stop"); L("net relay off"); L.close()
            except Exception as ex: print("laptop restore", ex)
if killed:
    # the laptop's game comes back up; its saves were restored by its agent on close; the setting must be the player's
    import os
    env = dict(os.environ, TLD_REMOTE=REMOTE_HOST)
    subprocess.run([sys.executable, str(pathlib.Path(__file__).resolve().parent / "game.py"), "launch"], env=env)
    L2 = Agent()
    try:
        until(lambda: L2("ping") is not None, 120, 3)
        now = g(L2, "settingsscript.s.S.BAutoShift"); print("laptop after the restart: automatic shifting", now, "(was", shift_l, ")", flush=True)
        if shift_l is not None and now != shift_l: L2(f"set settingsscript.s.S.BAutoShift {shift_l}"); print("  restored", flush=True)
    finally: L2.close()

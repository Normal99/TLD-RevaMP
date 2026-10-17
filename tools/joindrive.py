#!/usr/bin/env python3
"""Joining while the host drives (two machines, relay iad by default). The host drives a car01 along the main road at
~50 km/h; the laptop joins mid-drive. Where does the joiner appear (not on the road in front of the car, not under the
world), and once in: does it see the host's car moving with the host seated in it, where the host has it? Screenshots.
  tools/joindrive.py [--no-build] [--relay iad|off] [--no-hide: A, before v0.65.30]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g, car_ready, clear_road

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    god_h = g(h, "kaposztaleves.s.settings.god"); shift_h = g(h, "settingsscript.s.S.BAutoShift")
    god_l = None
    try:
        h("set kaposztaleves.s.settings.god True"); h("set settingsscript.s.S.BAutoShift True")
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]; R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        if not car_ready(h, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        h("mp host 27070")
        h("autodrive on 13.9"); until(lambda: abs(float(g(h, "mainscript.s.player.Car.speed") or 0)) > 42, 45, 0.5)
        print("host driving at", g(h, "mainscript.s.player.Car.speed"), "km/h — the laptop joins", flush=True)
        L(f"mp joinid {h('net me')['steamId']}")
        if not until(lambda: L("mp status")["client"]["ready"], 150, 1): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if "--no-hide" in sys.argv: L("set TLDRevamp.Net.RemotePlayers.HideFarSeated False")   # A: before v0.65.30
        god_l = g(L, "kaposztaleves.s.settings.god")   # not set: a fall on arrival must show
        time.sleep(3)
        me = h("mp players")["me"]
        net = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"]), key=lambda e: math.dist(e["pos"][::2], me[::2]))["net"]
        lp = L("mp players")["me"]
        car = next((e for e in h("mp entities")["entities"] if e["net"] == net), None)
        print(f"  the joiner appeared at {lp}, {math.dist(lp[::2], car['pos'][::2]):.0f} m from the host's car; host at {me}", flush=True)
        ground = L(f"ground {lp[0]} {lp[2]}") if False else None
        ds, gaps, spd, glide = [], [], [], []
        t0 = time.monotonic()
        while time.monotonic() - t0 < 35:
            if int(time.monotonic() - t0) % 4 == 0:
                cars_l = [(e["net"], [round(x) for x in e["pos"]], e.get("dormant")) for e in L("mp entities")["entities"] if e["car"] and e["part"] < 0]
                eh0 = next((e for e in h("mp entities")["entities"] if e["net"] == net), None)
                print(f"   +{time.monotonic() - t0:.0f}s laptop's cars {cars_l}; the host's car {net} at {[round(x) for x in eh0['pos']]}, {math.dist(eh0['pos'][::2], L('mp players')['me'][::2]):.0f} m from the joiner", flush=True)
            el = next((e for e in L("mp entities")["entities"] if e["net"] == net), None); eh = next((e for e in h("mp entities")["entities"] if e["net"] == net), None)
            hp0 = next((p for p in L("mp players")["players"] if p["id"] == 0), None)
            if el and el.get("dormant"): el = None   # in the far store here, not in the world
            if el and eh:
                gaps.append(math.dist(el["pos"], eh["pos"])); spd.append(math.hypot(*el["vel"][::2]) * 3.6 if "vel" in el else 0)
                if hp0: ds.append(hp0.get("seatCarDist", -1) if hp0["seatNet"] == net else 99)
            elif hp0:
                # the car not loaded here (beyond the game's 300 m): the friend must not glide on alone
                far = math.dist(hp0["target"][::2], L("mp players")["me"][::2])
                glide.append((round(far), hp0["visible"], hp0["hidden"]))
            time.sleep(1)
        try: L(f"mp lookat {net}")
        except RuntimeError as ex: print("  ", ex, flush=True)
        hp0 = next((p for p in L("mp players")["players"] if p["id"] == 0), None)
        if hp0:   # the laptop looks where its friend is (car or not) — what a player would see
            L(f"mp lookat {net}") if False else None
        L("shot joindrive_laptop"); h("shot joindrive_host")
        alive = g(L, "mainscript.s.player.died") if False else None
        hp = next((p for p in h("mp players")["players"] if p["id"] == 1), None)
        print(f"  host's car on the laptop: speeds {[round(x) for x in spd]}; gap to the host's own (same moment, ~lag) median {sorted(gaps)[len(gaps)//2] if gaps else float('nan'):.1f} m", flush=True)
        verdict("THE JOINER SEES THE HOST'S CAR DRIVING (within the game's 300 m)", spd and sorted(spd)[len(spd)//2] > 35, f"median {sorted(spd)[len(spd)//2] if spd else 0:.0f} km/h over {len(spd)} samples")
        verdict("WITH THE HOST SEATED IN IT (while loaded here)", ds and max(ds) < 1.5, f"host's body to its car on the laptop: max {max(ds) if ds else None} m over {len(ds)}")
        # back in range: the laptop's player goes to within 150 m of the car; the car is placed again, the friend in it
        eh = next(e for e in h("mp entities")["entities"] if e["net"] == net)
        lme = L("mp players")["me"]; dx, dz = eh["pos"][0] - lme[0], eh["pos"][2] - lme[2]; dd = math.hypot(dx, dz)
        L(f"tpground {eh['pos'][0] - dx / dd * 150} {eh['pos'][2] - dz / dd * 150}")
        back = until(lambda: any(e["net"] == net and not e.get("dormant") for e in L("mp entities")["entities"]), 30, 1)
        time.sleep(3)
        hp1 = next((p for p in L("mp players")["players"] if p["id"] == 0), None)
        verdict("BACK IN RANGE: THE CAR AGAIN, THE FRIEND SEATED IN IT", back and hp1 and hp1["visible"] and hp1["seated"] and 0 <= hp1["seatCarDist"] < 1.5,
                str(hp1 and {k: hp1[k] for k in ("visible", "hidden", "seated", "seatCarDist")}))
        print(f"  with the car not loaded here: (distance, body visible, hidden) {glide}", flush=True)
        verdict("NO FRIEND GLIDING ON WITHOUT THEIR CAR", glide and not any(v for d, v, hd in glide if d > 260), f"{sum(1 for d, v, hd in glide if v)} of {len(glide)} samples visible")
        verdict("THE HOST SEES THE JOINER", hp is not None and hp["has"], str(hp and {k: hp[k] for k in ("has", "shown", "seated")}))
        h_ = L("get mainscript.s.player.transform.position")["value"]
        st = L("stats") if False else None
        er = [x("errors") for x in (h, L)]
        verdict("NO ERRORS", all(x["errors"] == 0 and x["exceptions"] == 0 for x in er), str([(x["errors"], x["exceptions"], (x.get('kinds') or [])[:3]) for x in er]))
    finally:
        try: h("autodrive off"); h("brakecar")
        except Exception: pass
        h(f"set settingsscript.s.S.BAutoShift {shift_h}"); h(f"set kaposztaleves.s.settings.god {god_h if god_h is not None else False}")
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

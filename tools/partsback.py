#!/usr/bin/env python3
"""A joiner's car back in view without its parts (playtest 2026-10-16: the joiner's Car06 came into the host's view as a
bare body — every wheel, door, the hood, trunk, engine "missing here" — after the host had held it for a while and gone
on, the server had handed it back to the joiner and the joiner had driven it 1.5 km to the host). Two machines over the
relay:
  the laptop's car01 at the start; the host takes its wheel (the host's now) and gets out; the host goes 1 km away (its
  copy streams out while the host owns it, the server hands it to the laptop, standing by it); in the host's far store
  one part's record is put 2 km from the car, still bolted to it (`mp farstray`: what the host's save had for the
  joiner's car — wheels, doors, engine 1–3.5 km off); the laptop drives it ~1 km; the host comes to it → the host's
  copy has the laptop's parts (`mp carparts`).
  tools/partsback.py [--no-build] [--relay iad|off] [--plain: the host never takes the car] [--old: before v0.68.1]"""
import sys, os, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import CAR01, g

os.environ.setdefault("TLD_TEST_MODS", "CarTrailer,HeadUnit")   # the example mods stay installed
def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, PLAIN, OLD = arg("--relay", "iad"), "--plain" in sys.argv, "--old" in sys.argv
T = "TLDRevamp.Net.Entities"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None
def ent(s, net): return next((e for e in s("mp entities")["entities"] if e["net"] == net), None)
def speed(s): return abs(float(g(s, "mainscript.s.player.Car.speed") or 0))

def travel(s, gx, gz, who, settle=8):
    """ground-safe teleport to a GLOBAL point (as meetagain.travel)"""
    s("set mainscript.s.player.godModeTillGrounded True")
    s(f"tpg {gx} {gz} hold")
    def grounded():
        p = s("gpos"); return "y" in s(f"ground {p['ux']} {p['uz']}")
    if not until(grounded, 90, 1.0): s("tprelease"); raise RuntimeError(f"{who}: no ground at {gx:.0f},{gz:.0f}")
    r = s(f"tpground {gx} {gz}")
    if r.get("tpground") == "hop": until(grounded, 30, 1.0); s(f"tpground {gx} {gz}")
    time.sleep(settle)
    p = s("gpos"); print(f"  {who} at {p['gx']:.0f},{p['gz']:.0f}", flush=True)
    return p

def sit(s, car, who):
    for _ in range(4):
        s(f"mp sitdriver {car}"); time.sleep(1.5)
        if g(s, "mainscript.s.player.Bsitting") is True: return True
    print(f"  {who} never sat", flush=True); return False

def parts(s, car):
    c = s(f"mp carparts {car}")
    return None if c.get("dormant") or "error" in c else c

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    gods = {}
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_(f"set {T}.FarPartsByAttach {not OLD}"); s_(f"set TLDRevamp.Fixes.PlaceWithParts.Enabled {not OLD}")
        print(f"  {'OLD (before v0.68.1)' if OLD else 'CURRENT BUILD'}{', plain' if PLAIN else ''}", flush=True)
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
        hid, lid = h("mp entities")["me"], L("mp entities")["me"]
        rp = L("roadpoint"); yaw = rp["yaw"]
        L(f"tpground {rp['gx'] + 4} {rp['gz'] + 4}"); time.sleep(2)
        print("  laptop's car:", L(f"spawncarg {CAR01} 0 {rp['gx']} {rp['gz']} {yaw}"), flush=True)
        car = until(lambda: next((e["net"] for e in L("mp entities")["entities"] if e["car"] and e["owner"] == lid and not e["dormant"]
                                  and math.hypot(e["pos"][0] - rp["gx"], e["pos"][2] - rp["gz"]) < 15), None), 20)
        if not car: sys.exit("the laptop's car was not shared")
        until(lambda: parts(h, car), 30); time.sleep(3)
        p0l, p0h = parts(L, car), parts(h, car)
        verdict("START: THE SAME CAR ON BOTH", p0l and p0h and set(p0l["parts"]) == set(p0h["parts"]), f"{len(p0l['parts'])} vs {len(p0h['parts'])} parts")
        home = h("gpos")
        if not PLAIN:
            h(f"tpground {rp['gx'] - 4} {rp['gz'] - 4}"); time.sleep(2)
            sit(h, car, "host")
            verdict("THE HOST TAKES THE CAR", bool(until(lambda: (ent(h, car) or {}).get("owner") == hid and (ent(L, car) or {}).get("owner") == hid, 15)))
            time.sleep(2); h("driveout"); time.sleep(2)
        # the host goes away: its copy (its own car, in the PLAIN run the laptop's) streams out there
        travel(h, home["gx"] + 1200, home["gz"] + 300, "host")
        dormant = until(lambda: (ent(h, car) or {}).get("dormant") is True, 30)
        handed = until(lambda: (ent(L, car) or {}).get("owner") == lid, 30)
        verdict("AWAY: THE HOST'S COPY STREAMED OUT, THE CAR THE LAPTOP'S", bool(dormant) and bool(handed),
                f"dormant {dormant}, owner on the laptop {(ent(L, car) or {}).get('owner')}")
        # the playtest's state: a part still bolted to the car in the host's far store, its record left 2 km away
        print("  host's stored copy, a part left behind:", h(f"mp farstray {car} 2000"), flush=True)
        # the laptop drives it on ~1 km
        sit(L, car, "laptop"); L("fuelup"); L("set settingsscript.s.S.BAutoShift True")
        L("autodrive on 22"); t0 = time.time(); vmax = 0
        while time.time() - t0 < 50: vmax = max(vmax, speed(L)); time.sleep(1)
        L("autodrive on 0"); until(lambda: speed(L) < 3, 20, 0.5); L("autodrive off"); L("brakecar"); L("handbrake on"); time.sleep(3)
        el = ent(L, car); d = math.hypot(el["pos"][0] - rp["gx"], el["pos"][2] - rp["gz"])
        print(f"  the laptop drove it {d:.0f} m (top {vmax:.0f} km/h)", flush=True)
        pl = parts(L, car)
        # the host comes to it
        travel(h, el["pos"][0] + 12, el["pos"][2] + 12, "host")
        until(lambda: parts(h, car), 40); time.sleep(10)   # the census asks 8 s after the copy is placed
        ph = parts(h, car)
        missing = sorted(set(pl["parts"]) - set(ph["parts"])) if ph else "not placed"
        extra = sorted(set(ph["parts"]) - set(pl["parts"])) if ph else []
        verdict("BACK: THE HOST'S COPY HAS THE LAPTOP'S PARTS", ph is not None and not missing and not extra,
                f"laptop {len(pl['parts'])} parts, host {len(ph['parts']) if ph else '-'}: missing here {missing}, extra here {extra}")
        gap = math.dist(ent(L, car)["pos"], ent(h, car)["pos"]) if ph else None
        verdict("BACK: THE CAR WHERE THE LAPTOP HAS IT", gap is not None and gap < 2, f"{gap and round(gap, 2)} m")
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        for s_ in (h, L):
            try: s_("autodrive off")
            except Exception: pass
        for s_, v in list(gods.values()): s_(f"set kaposztaleves.s.settings.god {v}")
        for s_ in (h, L):
            try: s_(f"set {T}.FarPartsByAttach True"); s_("set TLDRevamp.Fixes.PlaceWithParts.Enabled True")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"PARTSBACK: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")

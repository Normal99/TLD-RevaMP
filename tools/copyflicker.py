#!/usr/bin/env python3
"""Flicker: a copy held in a car's container here, drawn at its owner's pose far away (playtest 2026-10-09/10, client 1:
the host's ammo box lay still 800 m back while its copy sat in client 1's copy of the host's car — the game's streaming
removed the whole car every second, "copy placed again 1.0 s after a displaced store-out" six times running). Two
machines over the relay: the host's item on the ground next to its car; on the laptop the item's copy stored into the
laptop's copy of that car (`mp copystore`); the host drives off at --kmh for --drive s with the laptop riding along as a
passenger → the laptop's copy of the car is never stored out, the item's copy ends up where the host has it.
  tools/copyflicker.py [--no-build] [--relay iad|off] [--kmh 60] [--drive 40] [--item 320] [--old: v0.66.7]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import g, car_ready, clear_road

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD, KMH, DRIVE_S, ITEM = arg("--relay", "iad"), "--old" in sys.argv, arg("--kmh", 60.0, float), arg("--drive", 40.0, float), arg("--item", 320, int)
T = "TLDRevamp.Net.Entities"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def until(fn, timeout=30, step=0.5):
    """fn's first truthy value (roadkit.until returns only True)"""
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None
def travel(s, gx, gz, who, settle=4):
    s("set mainscript.s.player.godModeTillGrounded True")
    s(f"tpg {gx} {gz} hold")
    def grounded():
        q = s("gpos"); return "y" in s(f"ground {q['ux']} {q['uz']}")
    if not until(grounded, 90, 1.0): s("tprelease"); raise RuntimeError(f"{who}: no ground at {gx:.0f},{gz:.0f}")
    r = s(f"tpground {gx} {gz}")
    if r.get("tpground") == "hop": until(grounded, 30, 1.0); s(f"tpground {gx} {gz}")
    time.sleep(settle)
    q = s("gpos"); print(f"  {who} at {q['gx']:.0f},{q['gz']:.0f}", flush=True)
def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
def ent(s, net): return ents(s).get(net)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    gods = {}; shift_h = g(h, "settingsscript.s.S.BAutoShift")
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L):
            s_(f"set {T}.CopyLockSafe {not OLD}"); s_(f"set {T}.CopyLockByContainer {not OLD}"); s_(f"set {T}.DriveClaimRootOnly {not OLD}")
        print(f"  {'OLD (v0.66.7)' if OLD else 'CURRENT BUILD'}", flush=True)
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
        h("set settingsscript.s.S.BAutoShift True")
        hid = h("mp entities")["me"]
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
        R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        if not car_ready(h, r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        me = h("mp players")["me"]
        car = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
        # the host's item on the verge, 5 m to the side
        before = set(ents(h)); u = h("gpos")
        ix, iz = me[0] + R[0] * 5, me[2] + R[1] * 5
        ux, uz = u["ux"] + (ix - u["gx"]), u["uz"] + (iz - u["gz"])
        h(f"spawnpile {ITEM} 1 {ux} {h(f'ground {ux} {uz}').get('y') + 0.4} {uz} 1 1 0 5")
        it = until(lambda: next((n for n, e in ents(h).items() if n not in before and e["owner"] == hid and not e["car"]), None), 15)
        if not it: sys.exit("no item")
        L(f"tpground {me[0] + R[0] * 3} {me[2] + R[1] * 3}"); time.sleep(3)
        until(lambda: (ent(L, it) or {}).get("dormant") is False and (ent(L, car) or {}).get("dormant") is False, 30); time.sleep(4)
        print(f"  host car {car}, host's item {it} on the verge", flush=True)
        print("  [laptop] the item's copy into the copy of the host's car:", L(f"mp copystore {it} {car}"), flush=True)
        # the laptop away and back: its copies of the car and the item are stored and placed again — the car's record
        # puts the item back into its container (how the playtest's copy came to be drawn at its owner's pose)
        lp = L("gpos")
        travel(L, lp["gx"] + 600, lp["gz"], "laptop (600 m away)")
        until(lambda: (ent(L, car) or {}).get("dormant") is True, 30)
        travel(L, me[0] + R[0] * 3, me[2] + R[1] * 3, "laptop (back at the host's car)")
        until(lambda: (ent(L, car) or {}).get("dormant") is False and (ent(L, it) or {}).get("dormant") is False, 30); time.sleep(3)
        print("  [laptop] after coming back: item", {k: (ent(L, it) or {}).get(k) for k in ("dormant", "byBody", "pos")}, flush=True)
        for _ in range(3):
            print("  passenger:", L(f"mp sitpassenger {car}"), flush=True); time.sleep(2)
            if g(L, "mainscript.s.player.Bsitting") is True: break
        verdict("THE LAPTOP RIDES ALONG", g(L, "mainscript.s.player.Bsitting") is True)
        an0 = L("mp anomalies")["count"]
        h(f"autodrive on {KMH / 3.6:.1f}")
        t0, dormant_seen, vmax, far = time.monotonic(), 0, 0.0, 0.0
        while time.monotonic() - t0 < DRIVE_S:
            e_ = ent(L, car) or {}
            if e_.get("dormant"): dormant_seen += 1
            vmax = max(vmax, abs(float(g(h, "mainscript.s.player.Car.speed") or 0)))
            try: far = math.dist(ent(h, car)["pos"], ent(h, it)["pos"])
            except Exception: pass
            time.sleep(0.25)
        h("autodrive off"); time.sleep(3)
        an = L("mp anomalies")
        lines = an["lines"][an0:] if an["count"] > an0 else []
        car_lines = [x for x in lines if f"net {car}" in x and ("placed again" in x or "stored out" in x)]
        verdict(f"DRIVEN AWAY FROM THE ITEM (top {vmax:.0f} km/h)", far > 350, f"car {far:.0f} m from the item")
        verdict("THE LAPTOP'S COPY OF THE CAR WAS NEVER STORED OUT", dormant_seen == 0 and not car_lines,
                f"dormant in {dormant_seen} samples; anomalies {car_lines[:4]}")
        el, eh = ent(L, it), ent(h, it)
        gap = math.dist(el["pos"], eh["pos"]) if el and eh and not el.get("dormant") else None
        verdict("THE ITEM'S COPY WHERE THE HOST HAS IT (or stored away there)", (gap is not None and gap < 1.0) or (el or {}).get("dormant") is True,
                f"{gap and round(gap, 2)} m apart; released {L(f'get {T}.CopyLocksReleased')['value']}")
        er = [x_("errors") for x_ in (h, L)]
        print("  errors:", [(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er], flush=True)
    finally:
        try: h("autodrive off")
        except Exception: pass
        for s_, v in gods.values(): s_(f"set kaposztaleves.s.settings.god {v}")
        h(f"set settingsscript.s.S.BAutoShift {shift_h}")
        for s_ in (h, L):
            try: s_(f"set {T}.CopyLockSafe True"); s_(f"set {T}.CopyLockByContainer True"); s_(f"set {T}.DriveClaimRootOnly True")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"COPYFLICKER: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")

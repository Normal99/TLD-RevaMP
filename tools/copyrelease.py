#!/usr/bin/env python3
"""A copy taken out of a container must not wreck a car (playtest 2026-10-09/10, v0.66.7: a lamp's copy taken out of
the host's car on client 2, a jerry can's on the host: crashes of Δv 56-459, two deaths). The release rule left the copy
where the container had it, a kinematic body inside a moving car. Two machines over the relay: the laptop's item lies
on the road; on the host its copy is stored into the host's OWN car (`mp copystore`: what a container's scan did in
the field), the host drives off at --kmh; a second later the rule takes the copy out → the host's car drives on (no
crash cause logged, speed kept), the copy where the laptop has it.
  tools/copyrelease.py [--no-build] [--relay iad|off] [--kmh 60] [--items 320,72: lamp, jerry can] [--old: v0.66.7 release]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import g, car_ready, clear_road

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD, KMH = arg("--relay", "iad"), "--old" in sys.argv, arg("--kmh", 60.0, float)
ITEMS = [int(x) for x in arg("--items", "320,72").split(",")]
T = "TLDRevamp.Net.Entities"; CC = "TLDRevamp.Diagnostics.CrashCause"
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
        print(f"  {'OLD (v0.66.7 release)' if OLD else 'CURRENT BUILD'}", flush=True)
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
        h("set settingsscript.s.S.BAutoShift True")
        lid = L("mp entities")["me"]
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw))); R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        for k, item in enumerate(ITEMS):
            tag = f"ITEM {item} #{k + 1}"
            # the host's car on the road, the host in it; the laptop next to it drops its item 8 m behind the car
            cx, cz = r0["gx"] + R[0] * 1.8 - F[0] * 120 * k, r0["gz"] + R[1] * 1.8 - F[1] * 120 * k
            if not car_ready(h, cx, cz, yaw, "host"): verdict(f"{tag} SETUP: THE HOST IN ITS CAR", False); break
            me = h("mp players")["me"]
            car = min((e for e in h("mp entities")["entities"] if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], me))["net"]
            ix, iz = me[0] - F[0] * 8 + R[0] * 4, me[2] - F[1] * 8 + R[1] * 4
            L(f"tpground {ix + R[0] * 2} {iz + R[1] * 2}"); time.sleep(3)
            before = set(ents(L)); u = L("gpos")
            ux, uz = u["ux"] + (ix - u["gx"]), u["uz"] + (iz - u["gz"])
            gy = L(f"ground {ux} {uz}").get("y")
            L(f"spawnpile {item} 1 {ux} {gy + 0.4} {uz} 1 1 0 5")
            it = until(lambda: next((n for n, e in ents(L).items() if n not in before and e["owner"] == lid and not e["car"]), None), 15)
            if not it: verdict(f"{tag} SETUP: THE LAPTOP'S ITEM", False); break
            until(lambda: (ent(h, it) or {}).get("dormant") is False, 20); time.sleep(4)
            print(f"  {tag}: host car {car}, laptop's item {it} on the ground behind it", flush=True)
            c0, r_0 = int(h(f"get {CC}.Logged")["value"]), int(h(f"get {T}.CopyLocksReleased")["value"])
            spot = ent(L, it)["pos"]
            print(f"  {tag}: [host] its copy into the host's own car: {h(f'mp copystore {it} {car}')}", flush=True)
            time.sleep(1)
            h(f"autodrive on {KMH / 3.6:.1f}")
            vmax, vmin_after, released_at, t0 = 0.0, None, None, time.monotonic()
            while time.monotonic() - t0 < 20:
                v = abs(float(g(h, "mainscript.s.player.Car.speed") or 0)); vmax = max(vmax, v)
                if released_at is None and int(h(f"get {T}.CopyLocksReleased")["value"]) > r_0: released_at = time.monotonic() - t0
                if released_at is not None: vmin_after = v if vmin_after is None else min(vmin_after, v)
                if released_at is not None and time.monotonic() - t0 > released_at + 6: break
                if int((time.monotonic() - t0) * 4) % 12 == 0:
                    e_, c_ = ent(h, it) or {}, ent(h, car) or {}
                    print(f"    t {time.monotonic() - t0:4.1f}: host's copy owner {e_.get('owner')} "
                          f"{(round(math.dist(e_['pos'], c_['pos']), 1) if e_.get('pos') and c_.get('pos') else '?')} m from the car, byBody {e_.get('byBody')} dormant {e_.get('dormant')}, released {h(f'get {T}.CopyLocksReleased')['value']}", flush=True)
                time.sleep(0.25)
            h("autodrive off"); time.sleep(2)
            crashes = int(h(f"get {CC}.Logged")["value"]) - c0
            last = h(f"get {CC}.Last")["value"] if crashes else ""
            rel = int(h(f"get {T}.CopyLocksReleased")["value"]) - r_0
            verdict(f"{tag}: THE COPY WAS TAKEN OUT OF THE HOST'S CAR", rel > 0, f"{rel} released, top {vmax:.0f} km/h")
            verdict(f"{tag}: THE HOST'S CAR DROVE ON, NO CRASH", crashes == 0 and vmax > KMH * 0.6,
                    f"crash causes {crashes} {last[:300]}; top {vmax:.0f} km/h")
            el, eh = ent(L, it), ent(h, it)
            verdict(f"{tag}: STILL THE LAPTOP'S, WHERE IT LEFT IT (not taken along as the host's cargo)",
                    el is not None and el["owner"] == lid and math.dist(el["pos"], spot) < 1.0, f"owner {el and el['owner']}, {el and round(math.dist(el['pos'], spot), 1)} m from where it lay")
            gap = math.dist(el["pos"], eh["pos"]) if el and eh and not eh.get("dormant") else None
            verdict(f"{tag}: THE COPY WHERE THE LAPTOP HAS IT", gap is not None and gap < 1.0 or (eh or {}).get("dormant") is True,
                    f"{gap and round(gap, 2)} m apart; placed {h(f'get {T}.ReleasesPlaced')['value']}, ghosts cleared {h(f'get {T}.ReleaseGhostsCleared')['value']}")
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
print(f"COPYRELEASE: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")

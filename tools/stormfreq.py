#!/usr/bin/env python3
"""How often sandstorms come and how many reach a player (0.68.4, Fixes.Sandstorms). Two machines over the relay:
desktop hosts, laptop joins, both on sand. Playtest: "I see them at every other building and they almost always
arrive there" - every storm was aimed at a player (within 25 deg from 1.5 km: always within its reach) and lived
until it left the sand, one every 15-45 min.
  A  the host's setting (Normal): the spawner waits 30-75 min; the laptop's setting changes nothing
  B  NSPAWN storms from the host's spawner (each one cleared after it is seen on both): AimChance of them come at a
     player (heading within 25 deg of one); the others head 60+ deg past the player they came up by (45+ past both). Normal: 4 in 10 aimed
     (binomial bounds); with --old (AimChance 1, as before) every one does
  C  a storm whose time is up (LifeMinS/LifeMaxS 20 s, DieDownS 5 s for the test) dies down: gone on the host and
     the laptop; --old (AgeOut off): still there
  D  the setting Off: the spawner comes due, no storm on either machine; Rare: the wait follows (60-150 min, the next
     storm rolled again from now)
  Storm rows: [id, prefab, gx, gz, size, speed, xdir, ydir, life, distanceLerp, fadeDist, destroyDist]
  tools/stormfreq.py [--no-build] [--relay iad|off] [--old] [--n NSPAWN]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, relay_both, join_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
OLD = "--old" in sys.argv
NSPAWN = int(sys.argv[sys.argv.index("--n") + 1]) if "--n" in sys.argv else 30
S = "TLDRevamp.Fixes.Sandstorms"
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

def heading_off(r, p):
    """degrees between the storm's heading and the line from it to the player p"""
    to = math.atan2(p["gz"] - r[3], p["gx"] - r[2]); hd = math.atan2(r[7], r[6])
    return abs((math.degrees(hd - to) + 180) % 360 - 180)

def passes(r, p):
    """closest the storm's straight path comes to p (m)"""
    dx, dz = p["gx"] - r[2], p["gz"] - r[3]
    along = dx * r[6] + dz * r[7]
    return math.hypot(dx, dz) if along < 0 else abs(dx * r[7] - dz * r[6])

def binom_ok(k, n, p):   # within ~3 sigma of n*p
    sd = math.sqrt(n * p * (1 - p)) or 0.5
    return abs(k - n * p) <= 3 * sd

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    freq0 = {nm: s_(f"get {S}.Frequency")["value"] for nm, s_ in (("host", h), ("laptop", L))}
    print("  settings before:", freq0, flush=True)
    sand = h("mp storms sand"); print("  sand:", sand, flush=True)
    h(f"tpground {sand['gx']} {sand['gz']}"); L(f"tpground {sand['gx'] + 300} {sand['gz']}"); time.sleep(8)
    if RELAY != "off": relay_both(h, L, RELAY)
    if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    gods = {}
    for nm, s_ in (("host", h), ("laptop", L)):
        gods[nm] = (s_, s_("get kaposztaleves.s.settings.god").get("value"))
        s_("set kaposztaleves.s.settings.god True")
    try:
        for s_ in (h, L): s_(f"call {S}.SetFrequency 2")
        L(f"call {S}.SetFrequency 3")   # the laptop's own setting: not the host's
        if OLD:
            h(f"set {S}.AimChance 1"); h(f"set {S}.AgeOut False")
        time.sleep(1)
        h("mp storms clear"); time.sleep(3)
        prefab = h("mp storms prefab")["prefabs"][0]; reach = prefab["radius"]
        print("  storm prefab:", prefab, flush=True)
        sp = h("mp storms")["spawner"]
        verdict("A THE HOST'S WAIT IS NORMAL'S (30-75 MIN)", sp["min"] == 1800 and sp["max"] == 4500 and 0 < sp["next"] <= 4500,
                f"spawner {sp}")

        # B: the spawner, NSPAWN times
        aimed = passing = bad = 0; worst = 0.0; rows = []
        for i in range(NSPAWN):
            hs0 = h("mp storms"); n0 = hs0["sandstorms"]["spawns"]
            ps = [h("gpos"), L("gpos")]
            h("mp storms timer")
            # its id once the host's sync has numbered it (0 before that)
            r = until(lambda: next((x for x in h("mp storms")["storms"] if x[0] != 0), None), 6, 0.3)
            if not r: bad += 1; print(f"  {i}: no storm (no sand around?)", flush=True); continue
            seen = until(lambda: any(x[0] == r[0] for x in L("mp storms")["storms"]), 8, 0.3)
            offs = [heading_off(r, p) for p in ps]; near = [passes(r, p) for p in ps]
            a = min(offs) <= 25.5; ok = a or min(offs) >= 45   # 60+ off the one it was rolled for; the players are 300 m apart (~12 deg from 1.5 km)
            aimed += a; passing += (not a) and ok; bad += not ok or not seen
            worst = max(worst, 0 if a else reach - min(near))
            rows.append((round(min(offs), 1), round(min(near))))
            print(f"  {i}: {'AIMED  ' if a else 'passing'} heading {min(offs):5.1f} deg off a player, passes {min(near):5.0f} m"
                  f" (reach {reach:.0f}); laptop saw it {bool(seen)}", flush=True)
            h("mp storms clear")
            until(lambda: not h("mp storms")["storms"] and not L("mp storms")["storms"], 8, 0.3)
        n = aimed + passing
        st = h("mp storms")["sandstorms"]
        print(f"  B: {aimed} aimed, {passing} passing, {bad} bad; host counters {st}", flush=True)
        if OLD:
            verdict("B (OLD) EVERY STORM COMES AT A PLAYER", n > 0 and aimed == n and bad == 0, f"{aimed}/{n} aimed")
        else:
            verdict("B 4 IN 10 COME AT A PLAYER, THE OTHERS PASS BY", n >= NSPAWN - 2 and bad == 0 and binom_ok(aimed, n, 0.4),
                    f"{aimed}/{n} aimed (expected {0.4 * n:.0f} +- {3 * math.sqrt(n * 0.24):.0f}); passing ones miss the "
                    f"players' spots by {min([x[1] for x in rows if x[0] > 25.5] or [0])} m or more")

        # C: dying down
        h(f"set {S}.LifeMinS 20"); h(f"set {S}.LifeMaxS 20"); h(f"set {S}.DieDownS 5")
        hp = h("gpos"); h(f"mp storms spawn {hp['gx'] + 30} {hp['gz']}")
        until(lambda: L("mp storms")["storms"], 6, 0.3)
        a0 = h("mp storms")["sandstorms"]["agedOut"]
        gone = until(lambda: not h("mp storms")["storms"] and not L("mp storms")["storms"], 45, 1)
        hs, ls = h("mp storms"), L("mp storms")
        if OLD:
            verdict("C (OLD) NO DYING DOWN: STILL THERE AFTER 45 S", not gone and len(hs["storms"]) == 1 and len(ls["storms"]) == 1,
                    f"host {len(hs['storms'])}, laptop {len(ls['storms'])}")
        else:
            verdict("C ITS TIME UP, THE STORM DIES DOWN ON BOTH", bool(gone) and hs["sandstorms"]["agedOut"] == a0 + 1,
                    f"agedOut {a0} -> {hs['sandstorms']['agedOut']}; host {len(hs['storms'])}, laptop {len(ls['storms'])}")
        h("mp storms clear"); time.sleep(2)
        h(f"set {S}.LifeMinS 900"); h(f"set {S}.LifeMaxS 1800"); h(f"set {S}.DieDownS 60")

        # D: Off, then Rare
        h(f"call {S}.SetFrequency 0")
        o0 = h("mp storms")["sandstorms"]["off"]
        h("mp storms timer"); time.sleep(4)
        hs, ls = h("mp storms"), L("mp storms")
        verdict("D OFF: THE SPAWNER COMES DUE, NO STORM", not hs["storms"] and not ls["storms"] and hs["sandstorms"]["off"] == o0 + 1,
                f"host {len(hs['storms'])}, laptop {len(ls['storms'])}, skipped {o0} -> {hs['sandstorms']['off']}")
        h(f"call {S}.SetFrequency 1"); time.sleep(1)
        sp = h("mp storms")["spawner"]
        verdict("D RARE: THE WAIT FOLLOWS (60-150 MIN, ROLLED FROM NOW)", sp["min"] == 3600 and sp["max"] == 9000 and 3590 <= sp["next"] <= 9000,
                f"spawner {sp}")
    finally:
        for nm, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", nm, ex)
        for nm, s_ in (("host", h), ("laptop", L)):
            try: s_(f"call {S}.SetFrequency {freq0[nm]}")
            except Exception as ex: print("setting restore", nm, ex)
        try: h(f"set {S}.AimChance 0.4"); h(f"set {S}.AgeOut True")
        except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
print(f"{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed", flush=True)

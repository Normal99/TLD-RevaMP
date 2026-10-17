#!/usr/bin/env python3
"""The host checks what players report (Net/Plausibility.cs). Two machines over the relay.
  A  normal play is never flagged: the laptop walks about, then drives its own car for 30 s
  B  a player teleporting itself (the laptop moves itself 3 km on foot, no request): flagged, one line in the host's chat
  C  a car teleported by its owner (the laptop moves itself and its car 3 km, no request): the car flagged
  D  a teleport the host accepted (/tpa, /y) with the car: not flagged (grace)
  E  --enforce: the self-teleport's states are dropped: the host keeps the laptop where it was
  tools/plausibility.py [--no-build] [--relay iad|off] [--old: check off — nothing flagged]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, g, clear_road, car_ready

def arg(n, d): return sys.argv[sys.argv.index(n) + 1] if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def pl(h): return h("mp plausibility")
def flags(s): return s["playerJumps"] + s["carJumps"] + s["carSpeeding"]
def pos(s):
    q = s("gpos"); return q["gx"], q.get("gy", q.get("y")), q["gz"]
def flat(a, b): return math.hypot(a[0] - b[0], a[2] - b[2])
def host_sees(h, pid):
    p = next((p for p in h("mp players")["players"] if p["id"] == pid), None)
    return p and p.get("target")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        h(f"set TLDRevamp.Net.Plausibility.Enabled {not OLD}")
        h("set TLDRevamp.Net.Tpa.CooldownS 0")
        for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
        print(f"  {'OLD (check off)' if OLD else 'CURRENT BUILD'}; host: {pl(h)}", flush=True)
        me = L("mp status")["client"]["id"]
        until(lambda: any(p["id"] == 0 for p in L("mp players")["players"]), 30)
        host = next(p["name"] for p in L("mp players")["players"] if p["id"] == 0)

        # A normal play
        rp = h("roadpoint"); F = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F[0], F[1]); yaw = r0["yaw"]
        R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw))); F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        x, z = r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8
        h(f"tpground {x - R[0] * 8} {z - R[1] * 8}"); time.sleep(2)
        L(f"tpground {x - R[0] * 4} {z - R[1] * 4}"); time.sleep(6)
        f0 = flags(pl(h))
        for k in range(6):   # a few steps about
            L(f"tpground {x - R[0] * 4 + F[0] * 3 * k} {z - R[1] * 4 + F[1] * 3 * k}"); time.sleep(1)
        seated = car_ready(L, x, z, yaw, "laptop")
        if seated:
            L("handbrake off"); L("autodrive on 15"); time.sleep(30); L("autodrive off"); L("handbrake on")
            until(lambda: abs(float(g(L, "mainscript.s.player.Car.speed") or 0)) < 1, 20); time.sleep(2)
        sa = pl(h)
        verdict("A NORMAL PLAY (walking, 30 s driving): NOT FLAGGED", seated and flags(sa) == f0, f"seated {seated}; {sa}")

        # B self-teleport on foot
        L("driveout"); time.sleep(2)
        lp = pos(L)
        f0 = pl(h)["playerJumps"]
        L(f"tpground {lp[0] + 3000} {lp[2]}"); time.sleep(8)
        sb = pl(h)
        if OLD: verdict("B OLD: NOT FLAGGED", sb["playerJumps"] == f0, str(sb))
        else: verdict("B A PLAYER TELEPORTING ITSELF: FLAGGED", sb["playerJumps"] > f0 and "on foot" in sb["last"], sb["last"])

        # C self-teleport with the car (the laptop goes back to its car first)
        if not OLD:
            L(f"tpground {lp[0]} {lp[2]}"); time.sleep(8)
            seated = False
            for _ in range(4):
                car = min((e for e in L("mp entities")["entities"] if e["car"] and not e["proxy"] and e["part"] < 0), key=lambda e: flat(e["pos"], pos(L)), default=None)
                if car: L(f"mp sitdriver {car['net']}"); time.sleep(2)
                if g(L, "mainscript.s.player.Bsitting") is True: seated = True; break
            f0 = pl(h)["carJumps"]
            cp = pos(L)
            if seated:
                print("  self car teleport:", L(f"call TLDRevamp.Net.Tpa.Go {cp[0] + 3000} {cp[2]} {yaw}"), flush=True); time.sleep(10)
            sc = pl(h)
            print(f"  C: seated {seated}, laptop now {pos(L)}, was {cp}", flush=True)
            verdict("C A CAR TELEPORTED BY ITS OWNER: FLAGGED", seated and sc["carJumps"] > f0, f"seated {seated}; {sc['last']}")

            # D accepted teleport request with the car: graced
            f0 = flags(pl(h)); gr0 = pl(h)["graced"]
            h(f"tpground {cp[0]} {cp[2] + 2000}"); time.sleep(8)
            L(f"mp say /tpa {host}"); time.sleep(3); h("mp say /y"); time.sleep(12)
            sd = pl(h)
            print("  after /tpa:", sd, "laptop", pos(L), "host", pos(h), flush=True)
            verdict("D AN ACCEPTED TELEPORT REQUEST: NOT FLAGGED", flags(sd) == f0 and flat(pos(L), pos(h)) < 30, f"graced {sd['graced'] - gr0}")

            # E enforce
            if "--enforce" in sys.argv:
                L("driveout"); time.sleep(2)
                h("set TLDRevamp.Net.Plausibility.Enforce True")
                before = host_sees(h, me)
                lp = pos(L); L(f"tpground {lp[0] + 3000} {lp[2]}"); time.sleep(8)
                after = host_sees(h, me)
                print("  enforce: host sees laptop", before, "→", after, pl(h), flush=True)
                verdict("E ENFORCE: THE HOST KEEPS THE LAPTOP WHERE IT WAS", before and after and flat(before, after) < 100, "")
                h("set TLDRevamp.Net.Plausibility.Enforce False")
        er = [x_("errors") for x_ in (h, L)]
        print("  errors:", [(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er], flush=True)
        verdict("NO ERRORS", all(e["exceptions"] == 0 for e in er))
    finally:
        try: h("set TLDRevamp.Net.Plausibility.Enabled True"); h("set TLDRevamp.Net.Plausibility.Enforce False"); h("set TLDRevamp.Net.Tpa.CooldownS 300")
        except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"PLAUSIBILITY: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")

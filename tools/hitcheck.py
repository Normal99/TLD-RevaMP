#!/usr/bin/env python3
"""The host checks hits by rewinding time (Net/HitCheck.cs, v0.66.9). Two machines over the relay (~190 ms ping):
  A  the host shoots the laptop's player standing 9 m away: every hit arrives, none refused
  B  the laptop's player walks across the host's line of fire (sideways, the hard case for lag): the host shoots it
     every 0.4 s — every hit the host's screen saw arrives on the laptop, none refused
  C  the laptop shoots the host's player walking (the host as the target, the laptop's ping in the rewind)
  D  forged hits from the laptop (what a modified client could send), 1 hp each: from 500 m away; from the laptop's
     own eye but aimed 90° off; a blade 20 m from the host; a creature that isn't the laptop's — all refused; a correct
     one passes
  F  forged creature hits naming the laptop's own creature (spawned there, so the laptop owns it): placed at the host
     while the creature is 30 m away — refused; with the host next to the creature, at the creature — arrives
  E a bot circling the host at 15 m, 25 m/s (90 km/h sideways: the copy drawn well behind its last state): the host
     shoots it every 0.3 s — none refused
  tools/hitcheck.py [--no-build] [--relay iad|off] [--old: the host forwards every hit, before v0.66.9]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
MAGNUM = 64
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def combat(s): return s("mp combat")
def hc(s): return s("mp hitcheck")
def pos(s):
    q = s("gpos"); return q["gx"], q.get("gy", q.get("y")), q["gz"]

def volley(shooter, victim, target_id, n, gap, what):
    """n shots, `gap` s apart; hits the shooter's screen saw vs damage the victim took vs refusals on the host."""
    c0, r0 = combat(victim)["damageTaken"], hc(h)["refused"]
    hits = 0; tried = 0
    for _ in range(n):
        r = shooter(f"mp shootat {target_id} 1.0"); tried += 1
        hits += r.get("hits", 0) or 0
        time.sleep(gap)
    time.sleep(2.5)
    took = combat(victim)["damageTaken"] - c0; refused = hc(h)["refused"] - r0
    print(f"  {what}: {tried} shots, {hits} hits on the shooter's screen, {took} taken by the victim, {refused} refused; host {hc(h)}", flush=True)
    return hits, took, refused

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
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, s_("get kaposztaleves.s.settings.god").get("value")); s_("set kaposztaleves.s.settings.god True")
        h(f"set TLDRevamp.Net.HitCheck.Enabled {not OLD}")
        print(f"  {'OLD (the host forwards every hit)' if OLD else 'CURRENT BUILD'}", flush=True)
        rp = h("roadpoint"); h(f"tpground {rp['gx']} {rp['gz']}"); time.sleep(3)
        before = {e["net"] for e in h("mp entities")["entities"]}
        print("car:", h("devspawn Car01Full"), flush=True)
        if not until(lambda: any(e["car"] and e["net"] not in before for e in h("mp entities")["entities"]), 20): sys.exit("NO CAR")
        net = next(e["net"] for e in h("mp entities")["entities"] if e["car"] and e["net"] not in before)
        time.sleep(3)
        print("  laptop 9 m out:", L(f"mount front {net} 9"), flush=True)
        print("  host 2 m out:", h(f"mount front {net} 2"), flush=True); time.sleep(3)
        print("  host's magnum:", h(f"devspawn {MAGNUM}"), flush=True); time.sleep(3)
        print("  ping:", h("mp status").get("server", {}), flush=True)

        # A standing
        hits, took, refused = volley(h, L, 1, 6, 0.6, "A standing")
        verdict("A STANDING: EVERY HIT ARRIVES, NONE REFUSED", hits > 0 and took == hits and refused == 0, f"{hits} hits, {took} taken, {refused} refused")

        # B the laptop walks sideways across the host's line of fire: from 12 m to one side of the car's nose line to the
        # other, past the host 9 m out
        cx, cy, cz = pos(h)
        lx, ly, lz = pos(L)
        fx, fz = lx - cx, lz - cz; fl = math.hypot(fx, fz) or 1; fx, fz = fx / fl, fz / fl
        sx, sz = -fz, fx   # sideways
        L(f"tpground {lx + sx * 14} {lz + sz * 14}"); time.sleep(3)
        # a marker to walk at: the host's car is behind the host; walk at a spot on the other side — use the car's own net
        # (the walk faces the object's centre each frame, so walk at the car from the side: the path crosses the line)
        L(f"mp walkat {net} 12")
        hits, took, refused = volley(h, L, 1, 20, 0.4, "B walking across")
        lp2 = pos(L)
        print(f"  laptop walked {math.dist((lx + sx * 14, lz + sz * 14), (lp2[0], lp2[2])):.1f} m", flush=True)
        verdict("B MOVING: EVERY HIT THE HOST SAW ARRIVES, NONE REFUSED", hits > 0 and took == hits and refused == 0, f"{hits} hits, {took} taken, {refused} refused")

        # C the laptop shoots the host, the host walking
        L(f"mount front {net} 12"); time.sleep(2)
        print("  laptop's magnum:", L(f"devspawn {MAGNUM}"), flush=True); time.sleep(3)
        hp = pos(h)
        h(f"tpground {hp[0] + sx * 10} {hp[2] + sz * 10}"); time.sleep(3)
        h(f"mp walkat {net} 10")
        hits, took, refused = volley(L, h, 0, 16, 0.4, "C the host walking, the laptop shooting")
        verdict("C THE HOST AS THE TARGET: EVERY HIT ARRIVES, NONE REFUSED", hits > 0 and took == hits and refused == 0, f"{hits} hits, {took} taken, {refused} refused")

        # D forged hits from the laptop on the host's player
        time.sleep(2)
        hx, hy, hz = pos(h); ex, ey, ez = pos(L); ey += 0.65
        dx, dy, dz = hx - ex, hy - ey, hz - ez; dn = math.sqrt(dx * dx + dy * dy + dz * dz) or 1
        cases = [
            ("from 500 m away", 0, (ex + 500, ey, ez), (dx / dn, dy / dn, dz / dn), False),
            ("aimed 90° off", 0, (ex, ey, ez), (-dz / dn, 0, dx / dn), False),
            ("a blade 20 m away", 2, (hx + 20, hy, hz), (1, 0, 0), False),
            ("a creature that isn't the laptop's", 1, (hx + 1, hy, hz), (1, 0, 0), False),
            ("control: from the laptop's eye at the host", 0, (ex, ey, ez), (dx / dn, dy / dn, dz / dn), True),
        ]
        for what, kind, o, d, ok in cases:
            c0, r0 = combat(h)["damageTaken"], hc(h)["refused"]
            L(f"mp forgehit 0 {kind} {o[0]:.2f} {o[1]:.2f} {o[2]:.2f} {d[0]:.4f} {d[1]:.4f} {d[2]:.4f}"); time.sleep(2)
            took, refused = combat(h)["damageTaken"] - c0, hc(h)["refused"] - r0
            print(f"  D {what}: taken {took}, refused {refused}; last: {hc(h)['last']}", flush=True)
            verdict(f"D {what.upper()}: {'ARRIVES' if ok else 'REFUSED'}", (took == 1 and refused == 0) if ok else (took == 0 and refused == 1), f"taken {took}, refused {refused}")
        # E a fast sideways target (a bot on loopback, drawn through the same interpolation as a player)
        h(f"mount front {net} 25"); time.sleep(2)
        h(f"devspawn {MAGNUM}"); time.sleep(2)
        known = {p_["id"] for p_ in h("mp players")["players"]}
        print("  bot:", h("mp bots 1 15 25"), flush=True)
        newp = lambda: [p_["id"] for p_ in h("mp players")["players"] if p_["id"] not in known and p_["has"]]
        bot = newp()[0] if until(lambda: newp(), 20) else None
        if bot is None: verdict("E SETUP: A BOT", False)
        else:
            time.sleep(4)
            c0, r0 = hc(h)["checked"], hc(h)["refused"]; hits = 0
            for _ in range(30):
                hits += h(f"mp shootat {bot} 1.0").get("hits", 0) or 0; time.sleep(0.3)
            time.sleep(1.5)
            chk, ref = hc(h)["checked"] - c0, hc(h)["refused"] - r0
            print(f"  E bot at 25 m/s: 30 shots, {hits} hits, {chk} checked, {ref} refused; {hc(h)['last']}", flush=True)
            verdict("E 90 KM/H SIDEWAYS: NONE REFUSED", hits > 0 and chk == hits and ref == 0, f"{hits} hits, {chk} checked, {ref} refused")
        # F creature hits naming the laptop's own creature
        aid = L("mp aiids")["ai"][0][0]
        lbefore = {e["net"] for e in L("mp entities")["entities"]}
        print("  laptop's creature:", L(f"devspawn {aid}"), flush=True)
        newc = lambda: [e for e in L("mp entities")["entities"] if e["net"] not in lbefore and not e["car"] and not e["proxy"]]
        cnet = newc()[0]["net"] if until(lambda: newc(), 20) else None
        if cnet is None: verdict("F SETUP: THE LAPTOP'S CREATURE", False)
        else:
            time.sleep(4)
            def cpos():
                lp_ = pos(L); return min((a["pos"] for a in L("mp ai")["ai"]), key=lambda q: math.dist(q, lp_))
            def forge_creature(what, o, ok):
                c0, r0 = combat(h)["damageTaken"], hc(h)["refused"]
                L(f"mp forgehit 0 1 {o[0]:.2f} {o[1]:.2f} {o[2]:.2f} 1 0 0 {cnet} 0"); time.sleep(2)
                took, refused = combat(h)["damageTaken"] - c0, hc(h)["refused"] - r0
                print(f"  F {what}: taken {took}, refused {refused}; last: {hc(h)['last']}", flush=True)
                verdict(f"F {what.upper()}: {'ARRIVES' if ok else 'REFUSED'}", (took >= 1 and refused == 0) if ok else (took == 0 and refused == 1), f"taken {took}, refused {refused}")
            cp = cpos(); print(f"  creature net {cnet} at {cp}", flush=True)
            h(f"tpground {cp[0] + 30} {cp[2]}"); time.sleep(4)
            hx, hy, hz = pos(h)
            forge_creature(f"the laptop's creature {math.dist(cp, (hx, hy, hz)):.0f} m off, the hit placed at the host", (hx, hy, hz), False)
            h(f"tpground {cp[0] + 3} {cp[2]}"); time.sleep(4)
            forge_creature("the laptop's creature next to the host, at the creature", cpos(), True)
        an = [x for x in h("mp anomalies")["lines"] if "hit refused" in x]
        print("  host anomaly lines:", an[:6], flush=True)
        er = [x_("errors") for x_ in (h, L)]
        print("  errors:", [(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er], flush=True)
    finally:
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        try: h("set TLDRevamp.Net.HitCheck.Enabled True")
        except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"HITCHECK: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")

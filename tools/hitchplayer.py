#!/usr/bin/env python3
"""The IFA trailer (Bus02UtanfutoFull) hitched the way a player does it: stand at the drawbar, pick it up, press attach
at the car's tow ball (`mount pickgrip` + `mount playermount`: fpscontroller's drop + slot craft as the player's own
action). The user, 2026-10-24: "sometimes won't couple". A Car01 on the host, the trailer 0.7 m short of its ball.
  S  the host alone (a session of one: the mount commands take network ids)
  C  the joiner's copy of the loose trailer has its drawbar on the trailer (Entities.HitchProxyGripFollows)
  J  the joiner hitches the host's trailer to the host's car (the non-owner side), ROUNDS times
     (Entities.DrawbarPickupClaim: the pick-up takes the trailer over), then once with a quick press
  H  the host hitches it after the joiner carried the drawbar last
Each try: hitched on both machines within WAIT s, with the hitch events of both games.
  tools/hitchplayer.py [--no-build] [--rounds N]   (two players: run2.sh / TLD_SECOND)"""
import sys, time, math, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both
from roadkit import clear_road, car_ready, until

ROUNDS = int(sys.argv[sys.argv.index("--rounds") + 1]) if "--rounds" in sys.argv else 4
WAIT = 8.0
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
def roots(s): return {n for n, e in ents(s).items() if e["part"] < 0}

def spawn_trailer(s, x, z, yaw):
    before = roots(s)
    for k in range(10):   # the ground there may still be generating (after the join)
        try: print("  trailer:", s(f"spawncarg {IFA} 0 {x} {z} {yaw}"), flush=True); break
        except RuntimeError as e:
            if "no ground" not in str(e) or k == 9: raise
            s(f"tpground {x + 4} {z}"); time.sleep(3)
    until(lambda: roots(s) - before, 15)
    time.sleep(2)
    # the nearest new root to the spot: other objects can be shared in the same moment (net 144 was one, run 6)
    es = ents(s); new = [es[n] for n in roots(s) - before]
    return min(new, key=lambda e: math.dist((e["pos"][0], e["pos"][2]), (x, z)))["net"] if new else None

def hitched(s, tr):
    try: st = s(f"mount hitchstate {tr}")
    except Exception as e: return {"error": str(e)[:120]}
    return st

def events(s, since=0):
    try: ev = s("mount hitchevents")
    except Exception as e: return [str(e)[:120]]
    rows = ev.get("events", ev) if isinstance(ev, dict) else ev
    out = [r for r in (rows or []) if "hitch" in json.dumps(r)]
    return out[since:]

def try_hitch(who, side, others, tr, car, tag, hold=1.5):
    """`side` picks up the drawbar and presses attach at the ball; done when every side shows it hitched to `car`"""
    n0 = {id(s): len(events(s)) for s in others}
    side(f"mount griptrace {tr} 3")
    side(f"mount pickgrip {tr} stand"); time.sleep(1.5)   # settled first: the hold point is fixed in the camera's frame
    try: print(f"  [{tag}] {who} picks up the drawbar:", side(f"mount pickgrip {tr}"), flush=True)
    except Exception as e: print(f"  [{tag}] pick-up failed: {e}", flush=True); return False
    time.sleep(hold)
    try: res = side("mount playermount 3")
    except Exception as e: res = {"error": str(e)[:200]}
    print(f"  [{tag}] {who} presses attach:", res, flush=True)
    if "holding nothing" in json.dumps(res):   # the drawbar left the hand: its body and the trailer's, step by step
        for line in side("mount griplog")["log"].splitlines()[:40]: print("    trace", line, flush=True)
    t0 = time.monotonic(); states = {}
    while time.monotonic() - t0 < WAIT:
        states = {lbl: hitched(s, tr) for lbl, s in others.items()}
        if all(st.get("attached") and st.get("car") == car for st in states.values()): break
        time.sleep(0.5)
    took = time.monotonic() - t0
    ok = all(st.get("attached") and st.get("car") == car for st in states.values())
    print(f"  [{tag}] after {took:.1f} s:", json.dumps(states), flush=True)
    for lbl, s in others.items():
        for r in events(s, n0.get(id(s), 0))[-8:]: print(f"    {lbl} event:", json.dumps(r)[:240], flush=True)
    return ok

def unhitch(owner_side, car, tr):
    try: print("  unhitch:", owner_side(f"mount unhitch {car} {tr}"), flush=True)
    except Exception as e: print("  unhitch failed:", e, flush=True)
    time.sleep(3)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    try:
        IFA = next(i for i, n in h("itemids Bus02UtanfutoFull")["items"] if n.lower() == "bus02utanfutofull")
        for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
        rp = h("roadpoint"); F0 = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        r0 = clear_road(h, rp["gx"], rp["gz"], F0[0], F0[1]); yaw = r0["yaw"]
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw))); R = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        x, z = r0["gx"] + R[0] * 1.8, r0["gz"] + R[1] * 1.8

        # S: solo
        if not car_ready(h, x, z, yaw, "host"): sys.exit("HOST NOT IN ITS CAR")
        h("driveout"); time.sleep(2)
        # solo: no network ids; the bridge's mount commands take net ids, so solo goes through a session of one
        h("mp host 27070"); time.sleep(3)
        pos = h("mp players")["me"]
        car = min((e for e in ents(h).values() if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], pos))["net"]
        probe = spawn_trailer(h, x + R[0] * 15, z + R[1] * 15, yaw)
        gp = h(f"mount gap {car} {probe}"); print("  probe gap:", gp, flush=True)
        ahead = gp["ahead"]; ball = gp["ball"]
        def place():
            # the car can have moved since the ball was measured (run 7: 8 m after the solo unhitch): measured again
            # against the new trailer, and once more placed from there
            global ball
            d = ahead + 0.7
            t = spawn_trailer(h, ball[0] - F[0] * d, ball[2] - F[1] * d, yaw)
            g2 = h(f"mount gap {car} {t}")
            if g2["dist"] < 1.2: return t
            ball = g2["ball"]
            return spawn_trailer(h, ball[0] - F[0] * d, ball[2] - F[1] * d, yaw)
        tr = place(); print("  gap:", h(f"mount gap {car} {tr}"), flush=True)
        verdict("S ALONE (A SESSION OF ONE): THE HOST HITCHES BY HAND", try_hitch("host", h, {"host": h}, tr, car, "S"))
        unhitch(h, car, tr)
        h("mp stop"); time.sleep(2)

        if not join_both(h, L): sys.exit("JOINER NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
        time.sleep(4)
        pos = h("mp players")["me"]
        car = min((e for e in ents(h).values() if e["part"] < 0 and e["car"] and not e["proxy"]), key=lambda e: math.dist(e["pos"], pos))["net"]
        print("  car", car, flush=True)
        # a fresh trailer for the session (net ids restart at every hosting)
        tr = place(); print("  gap:", h(f"mount gap {car} {tr}"), flush=True)
        L(f"tpground {ball[0] - F[0] * 2 + R[0] * 1.5} {ball[2] - F[1] * 2 + R[1] * 1.5}"); time.sleep(4)
        h(f"tpground {ball[0] + R[0] * 6} {ball[2] + R[1] * 6}"); time.sleep(2)
        sides = {"host": h, "joiner": L}
        gj = L(f"mount gap {car} {tr}"); print("  the joiner's copy:", gj, flush=True)
        verdict("C THE JOINER'S COPY HAS THE DRAWBAR ON THE TRAILER", 0 <= gj.get("gripOff", -1) < 0.3, f"gripOff {gj.get('gripOff')}")
        okJ = 0
        for k in range(ROUNDS):
            ok = try_hitch("joiner", L, sides, tr, car, f"J{k + 1}")
            okJ += ok
            st = hitched(h, tr)
            unhitch(h if st.get("owner") == 0 else L, car, tr)
            time.sleep(2)
        verdict(f"J THE JOINER HITCHES THE HOST'S TRAILER ({okJ}/{ROUNDS})", okJ == ROUNDS)
        # a quick press right after the pick-up, before the hand moves on (how it sometimes worked)
        okQ = try_hitch("joiner", L, sides, tr, car, "Q", hold=0.05)
        print("  quick press hitched:", okQ, flush=True)
        st = hitched(h, tr)
        if st.get("attached"): unhitch(h if st.get("owner") == 0 else L, car, tr); time.sleep(2)
        h(f"tpground {ball[0] - F[0] * 2 - R[0] * 1.5} {ball[2] - F[1] * 2 - R[1] * 1.5}"); time.sleep(3)
        L(f"tpground {ball[0] + R[0] * 6} {ball[2] + R[1] * 6}"); time.sleep(2)
        okH = try_hitch("host", h, sides, tr, car, "H")
        verdict("H THE HOST HITCHES IT AFTER THE JOINER", okH)
        er = h("errors"); print("  host errors:", er["errors"], er["exceptions"], (er.get("kinds") or [])[:3], flush=True)
        er = L("errors"); print("  joiner errors:", er["errors"], er["exceptions"], (er.get("kinds") or [])[:3], flush=True)
    finally:
        for s_ in (h, L):
            try: s_("set kaposztaleves.s.settings.god False")
            except Exception: pass
        try: L("mp stop")
        except Exception: pass
        try: h("mp stop")
        except Exception: pass
print("RESULT:", json.dumps(VERDICTS), flush=True)

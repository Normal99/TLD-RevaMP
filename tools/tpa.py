#!/usr/bin/env python3
"""Teleport requests (Net/Tpa.cs, v0.67.0). Two machines over the relay; the host allows TPA with a 2 min cooldown.
  A  on foot: the laptop is 3 km down the road, types /tpa <host>, the host types /y → the laptop stands next to the
     host (< 15 m)
  B  cooldown: asking again at once is refused (no new request reaches the host)
  C  refused: with no cooldown, the laptop 2 km away asks, the host types /n → it stays where it is
  D  with its car: the laptop drives its own car 2 km away (seated), asks, the host accepts → the car is next to the
     host, set down on the ground (upright, < 3 m above it), the laptop still in it; the host's copy there too
  tools/tpa.py [--no-build] [--relay iad|off] [--old: TPA off — every request refused]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, g

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def pos(s):
    q = s("gpos"); return q["gx"], q.get("gy", q.get("y")), q["gz"]
def flat(a, b): return math.hypot(a[0] - b[0], a[2] - b[2])
def ents(s): return s("mp entities")["entities"]

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        h(f"set TLDRevamp.Net.Tpa.Enabled {not OLD}"); h("set TLDRevamp.Net.Tpa.CooldownS 120")
        print(f"  {'OLD (TPA off)' if OLD else 'CURRENT BUILD'}", flush=True)
        host = next(p["name"] for p in L("mp players")["players"] if p["id"] == 0)
        rp = h("roadpoint"); x, z = rp["gx"], rp["gz"]
        h(f"tpground {x} {z}"); time.sleep(3)

        def ask_and(answer, wait=10):
            a0 = h("mp tpa")["asked"]
            print("  laptop:", L(f"mp say /tpa {host}"), flush=True); time.sleep(3)
            asked = h("mp tpa")["asked"] - a0
            if answer: print("  host:", h(f"mp say {answer}"), flush=True)
            time.sleep(wait)
            return asked

        # A on foot, 3 km away
        L(f"tpground {x + 3000} {z}"); time.sleep(8)
        before = pos(L)
        asked = ask_and("/y", 12)
        after, hp = pos(L), pos(h)
        print(f"  A: asked {asked}, laptop {flat(before, hp):.0f} m → {flat(after, hp):.1f} m from the host; host {h('mp tpa')}; laptop {L('mp tpa')}", flush=True)
        if OLD: verdict("A OLD: REFUSED, THE LAPTOP STAYS", asked == 0 and flat(after, hp) > 1000, f"{flat(after, hp):.0f} m")
        else: verdict("A ON FOOT: NEXT TO THE HOST", asked == 1 and flat(after, hp) < 15, f"{flat(after, hp):.1f} m")

        if not OLD:
            # B cooldown
            asked = ask_and(None, 1)
            verdict("B COOLDOWN: A SECOND REQUEST AT ONCE IS REFUSED", asked == 0, f"requests that reached the host: {asked}")
            h("set TLDRevamp.Net.Tpa.CooldownS 0")

            # C refused
            L(f"tpground {x + 2000} {z}"); time.sleep(8)
            before = pos(L)
            asked = ask_and("/n", 6)
            after = pos(L)
            verdict("C REFUSED: THE LAPTOP STAYS", asked == 1 and flat(before, after) < 5, f"moved {flat(before, after):.1f} m")

            # D with its car
            lb = {e["net"] for e in ents(L)}
            print("  car:", L("devspawn Car01Full"), flush=True)
            if not until(lambda: any(e["car"] and e["net"] not in lb and not e["proxy"] for e in ents(L)), 20): verdict("D SETUP: A CAR", False)
            else:
                car = next(e["net"] for e in ents(L) if e["car"] and e["net"] not in lb and not e["proxy"])
                time.sleep(4)
                print("  laptop drives:", L(f"mp sitdriver {car}"), flush=True); time.sleep(3)
                asked = ask_and("/y", 20)
                lp, hp = pos(L), pos(h)
                crow = next((e for e in ents(L) if e["net"] == car), None)
                hrow = next((e for e in ents(h) if e["net"] == car), None)
                seated = g(L, "mainscript.s.player.Bsitting")
                ground = L(f"ground {crow['pos'][0]} {crow['pos'][2]}") if crow else {}
                print(f"  D: laptop {flat(lp, hp):.1f} m from the host, seated {seated}; car {crow and crow['pos']} upy {crow and crow.get('upy')}; host's copy {hrow and hrow['pos']}; ground {ground}; laptop tpa {L('mp tpa')}", flush=True)
                verdict("D WITH ITS CAR: NEXT TO THE HOST, STILL IN IT", asked == 1 and flat(lp, hp) < 25 and seated is True, f"{flat(lp, hp):.1f} m, seated {seated}")
                verdict("D THE CAR IS SET DOWN UPRIGHT", crow is not None and float(crow.get("upy", 0)) > 0.9 and L("mp tpa")["carsSetDown"] >= 1, f"upy {crow and crow.get('upy')}")
                verdict("D THE HOST'S COPY IS THERE TOO", hrow is not None and flat(hrow["pos"], hp) < 30, f"{hrow and flat(hrow['pos'], hp):.1f} m from the host" if hrow else "no copy")
        er = [x_("errors") for x_ in (h, L)]
        print("  errors:", [(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er], flush=True)
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er))
    finally:
        try: h("set TLDRevamp.Net.Tpa.Enabled True"); h("set TLDRevamp.Net.Tpa.CooldownS 300")
        except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"TPA: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")

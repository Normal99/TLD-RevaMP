#!/usr/bin/env python3
"""Players walking into each other's loose items (2026-10-22 bug hunt, commit review: fb55874/ba1c75a, Entities.BodyPush:
walking into a loose item another player owns claims it just before the body reaches it). Two machines over the relay;
a row of the host's jerry cans on flat ground, the laptop's player beside them.
  A  ONE WALKER: the laptop walks into the middle can → it is the laptop's, pushed (not frozen); after the push both
     screens have every can at the same place; the cans nobody touched keep their owner
  B  BOTH WALK INTO THE SAME CAN from opposite sides at once → it ends at the same place on both, not flung or sunk,
     and the ownership did not churn (owner changes counted in the host's owner log)
  C  STANDING STILL AGAINST A CAN (the laptop leans on one) → no claim: standing against a copy took it every step before
  D  no errors on either machine
  --old: Entities.BodyClaims False on both (the A/B: items stay frozen until their owner moves them)
  tools/bodypush.py [--no-build] [--relay iad|off] [--old]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
JERRYCAN, N = 72, 5
T = "TLDRevamp.Net.Entities"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, bool(ok))); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}

def compare(cans):
    eh, el = ents(h), ents(L)
    rows = []
    for c in cans:
        a, b = eh.get(c), el.get(c)
        rows.append((c, a and a["owner"], b and b["owner"], round(math.dist(a["pos"], b["pos"]), 2) if a and b else None, a and a["pos"]))
    return rows

def owner_changes(net): return [l for l in h(f"mp ownerlog {net}")["log"] if "->" in l and l.split("->")[0].split()[-1] != l.split("->")[1].split(":")[0].strip()]

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_("set kaposztaleves.s.settings.god True"); s_(f"set {T}.BodyClaims {not OLD}")
        print(f"  {'OLD (no body claims)' if OLD else 'NEW (body claims)'}", flush=True)
        lid, hid = L("mp entities")["me"], h("mp entities")["me"]
        rp = h("roadpoint"); x, z = rp["gx"] + 12, rp["gz"] + 12
        h(f"tpground {x} {z}"); L(f"tpground {x} {z + 6}"); time.sleep(4)
        cans = []
        for i in range(N):
            hb = set(ents(h))
            h(f"tpground {x + (i - N // 2) * 1.2} {z}"); time.sleep(1.5)
            h(f"devspawn {JERRYCAN}")
            c = until(lambda: next((n for n, e in ents(h).items() if n not in hb and not e["car"] and not e["proxy"]), None), 15)
            if c: cans.append(c)
        h(f"tpground {x} {z - 6}"); time.sleep(8)
        if len(cans) < 3 or not until(lambda: all(c in ents(L) for c in cans), 30): sys.exit(f"the cans never reached the laptop ({cans})")
        mid = cans[len(cans) // 2]
        before = compare(cans)
        print("  cans (net, host's owner, laptop's owner, gap m):", [r[:4] for r in before], flush=True)

        # A: the laptop walks into the middle can
        L(f"tpground {ents(h)[mid]['pos'][0]} {ents(h)[mid]['pos'][2] + 3}"); time.sleep(3)
        p0 = ents(h)[mid]["pos"]
        print("  [A] laptop walks into the middle can:", L(f"mp walkat {mid} 4"), flush=True)
        time.sleep(4.5); time.sleep(4)
        rows = compare(cans)
        moved = math.dist(p0, ents(h)[mid]["pos"])
        print("  [A] after:", [r[:4] for r in rows], f"middle can moved {moved:.2f} m on the host", flush=True)
        if not OLD:
            verdict("A1 THE LAPTOP'S NOW, AND PUSHED", rows[len(cans) // 2][1] == lid and moved > 0.05, f"host sees owner {rows[len(cans) // 2][1]}, moved {moved:.2f} m")
        verdict("A2 EVERY CAN AT THE SAME PLACE ON BOTH", all(r[3] is not None and r[3] < 0.3 for r in rows), str([(r[0], r[3]) for r in rows]))
        others = [r for r in rows if r[0] != mid]
        verdict("A3 THE CANS NOBODY WALKED INTO KEPT THEIR OWNER", all(r[1] == hid and r[2] == hid for r in others),
                str([(r[0], r[1], r[2]) for r in others]))

        # B: both walk into the same can from opposite sides
        tgt = cans[0]
        tp = ents(h)[tgt]["pos"]
        h(f"tpground {tp[0] - 3} {tp[2]}"); L(f"tpground {tp[0] + 3} {tp[2]}"); time.sleep(3)
        n0 = len(owner_changes(tgt)); g0 = tp[1]
        print("  [B] both walk into can", tgt, ":", h(f"mp walkat {tgt} 5"), L(f"mp walkat {tgt} 5"), flush=True)
        peak = 0.0
        for _ in range(12):
            time.sleep(0.5)
            try: peak = max(peak, math.dist(tp, ents(h)[tgt]["pos"]))
            except Exception: pass
        time.sleep(5)
        rows = compare([tgt]); e = ents(h)[tgt]
        flips = owner_changes(tgt)[n0:]
        gr = h(f"ground {e['pos'][0]} {e['pos'][2]}")
        print(f"  [B] owner changes: {flips}", flush=True)
        verdict("B1 THE SAME PLACE ON BOTH", rows[0][3] is not None and rows[0][3] < 0.3, f"{rows[0][3]} m apart")
        verdict("B2 NOT FLUNG OR SUNK", peak < 6 and (not gr.get("hit") or e["pos"][1] > gr["y"] - 0.3), f"furthest {peak:.1f} m from the start, y {e['pos'][1]:.2f} ground {gr.get('y')}")
        verdict("B3 THE OWNERSHIP DID NOT CHURN", len(flips) <= 4, f"{len(flips)} owner changes in 5 s")

        # C: the laptop leans on a can (walks into it, then stands against it with no input)
        tgt = cans[-1]
        tp = ents(h)[tgt]["pos"]
        L(f"tpground {tp[0]} {tp[2] + 0.9}"); time.sleep(4)
        n0 = len(owner_changes(tgt))
        time.sleep(6)
        flips = owner_changes(tgt)[n0:]
        verdict("C STANDING STILL AGAINST A CAN DOES NOT TAKE IT", not flips, str(flips))
        er = [s_("errors") for s_ in (h, L)]
        verdict("D NO ERRORS", all(x_["errors"] == 0 and x_["exceptions"] == 0 for x_ in er), str([(x_["errors"], x_["exceptions"], (x_.get("kinds") or [])[:3]) for x_ in er]))
    finally:
        for s_ in (h, L):
            try: s_(f"set {T}.BodyClaims True")
            except Exception: pass
        try: L("mp stop"); h("mp stop")
        except Exception: pass
        if RELAY != "off":
            for s_ in (h, L):
                try: s_("net relay off")
                except Exception: pass
        L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
print(f"BODYPUSH: {sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} PASS")

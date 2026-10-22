#!/usr/bin/env python3
"""Joining next to a big base (2026-10-22 bug hunt, commit review: d9036d6's join-loading burst sends every object within
300 m at once; when the host's Steam send buffer is full a send fails, but the object was already marked as known to the
joiner, so it is never sent again — the joiner misses it for good). Two machines over the relay.
  The host piles --count items (a mix of small things) next to itself, then the laptop joins there.
  A  after the join settled, the laptop has every item of the pile where the host has it (none missing, none extra)
  B  the host's send errors during the join (reported; a count above 0 with A failing is the bug)
  C  no errors on either machine
  tools/joinbase.py [--no-build] [--relay iad|off] [--count 400]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, COUNT, R = arg("--relay", "iad"), arg("--count", 400, int), 25
NAMES = ("Box", "benzineskanna", "regizseblampa", "kanna", "uveg", "konzerv", "sor", "kolbasz")
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, bool(ok))); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def site(s, gx, gz): return [it for it in s(f"mp sitestate {gx} {gz} {R}")["items"] if "Ragdol" not in it[5]]

def compare(a, b, tol=1.0):
    left, missing = list(b), []
    for it in a:
        best, bi = None, -1
        for i, o in enumerate(left):
            if o[0] != it[0]: continue
            d = math.dist(it[1:4], o[1:4])
            if best is None or d < best: best, bi = d, i
        if best is None or best > tol: missing.append((it[5], best and round(best, 2)))
        else: left.pop(bi)
    return missing, [(o[5], [round(v, 1) for v in o[1:4]]) for o in left]

def send_errors(s):
    st = s("mp status")
    return (st.get("server") or {}).get("sendErrors", (st.get("net") or {}).get("sendErrors"))

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    try:
        h("set kaposztaleves.s.settings.god True")
        rp = h("roadpoint"); x, z = rp["gx"] + 15, rp["gz"] + 15
        h(f"tpground {x} {z}"); time.sleep(4)
        ids = []
        for n in NAMES:
            try: ids += [str(i["id"]) for i in h(f"items {n}")["items"]][:1]
            except Exception: pass
        if not ids: sys.exit("no item ids for the pile")
        u = h("gpos"); gy = h(f"ground {u['ux'] + 6} {u['uz'] + 6}").get("y", u.get("uy", 0))
        print("  pile:", ",".join(ids), h(f"spawnpile {','.join(ids)} {COUNT} {u['ux'] + 3} {gy + 0.5} {u['uz'] + 3} 20 0.7 0.5 3"), flush=True)
        time.sleep(15)
        hs = site(h, x, z)
        print(f"  host has {len(hs)} items within {R} m", flush=True)
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        time.sleep(25)
        hs, ls = site(h, x, z), site(L, x, z)
        missing, extra = compare(hs, ls)
        verdict("A THE LAPTOP HAS THE WHOLE PILE", not missing and not extra,
                f"host {len(hs)} items, laptop {len(ls)}: missing on the laptop {len(missing)} {missing[:8]}, extra {len(extra)} {extra[:5]}")
        se = send_errors(h)
        verdict("B THE HOST'S SENDS DIDN'T FAIL", not se, f"send errors {se}")
        er = [s_("errors") for s_ in (h, L)]
        verdict("C NO ERRORS", all(x_["errors"] == 0 and x_["exceptions"] == 0 for x_ in er), str([(x_["errors"], x_["exceptions"], (x_.get("kinds") or [])[:3]) for x_ in er]))
    finally:
        try: L("mp stop"); h("mp stop")
        except Exception: pass
        if RELAY != "off":
            for s_ in (h, L):
                try: s_("net relay off")
                except Exception: pass
        try: h("clearpile")
        except Exception: pass
        L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
print(f"JOINBASE: {sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} PASS")

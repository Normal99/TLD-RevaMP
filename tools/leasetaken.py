#!/usr/bin/env python3
"""LeaseTaken handover (two machines, both at the start): the start house's lease is holder 0, done. The joining
client must ask, receive LeaseTaken, disable its own built-in copies and hold exactly the host's items there."""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP

def until(fn, timeout=25, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        b("mp host 27070")
        rp = b("roadpoint")
        hx, hz = rp["x"], rp["z"]          # the start house (haz02) is right at the start
        time.sleep(5)
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 150)
        time.sleep(8)                       # let the join-time sync settle
        hb = b(f"mp itemsnear {hx} {hz} 120")
        hl = L(f"mp itemsnear {hx} {hz} 120")
        lt = b("leasetable")
        ent = hl["leases"]   # builtInDisabled/reEnabled live under the leases block of itemsnear
        print(f"lease table: {[(r['key'][:12], r['holder'], r['done']) for r in lt['leases'][:3]]}")
        print(f"start house: host count {hb['count']} shared {hb['shared']} hash {hb['hash']}")
        print(f"             client count {hl['count']} shared {hl['shared']} hash {hl['hash']}")
        lk = L("mp entities")["leaseKeys"]
        print(f"client built-ins disabled {ent['builtInDisabled']}, reEnabled {ent['reEnabled']}, leaseKeys {lk[:60]}")
        same = hb["hash"] == hl["hash"] and hb["count"] == hl["count"]
        asked = len(lk) > 0
        print("VERDICT:", "PASS — the client holds exactly the host's items (LeaseTaken handover, no doubles)"
              if same and hb["count"] > 0 and asked else
              ("FAIL — hash mismatch (double items or missing content)" if not same else "client never asked — test setup"))
        b("mp stop"); L("mp stop")
finally:
    L.close()

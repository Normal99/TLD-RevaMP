#!/usr/bin/env python3
"""Lease persistence across a server restart (two machines): boot 1 (fresh, -tldseed) grants the client a lease
for a far-away poi key and marks it done; `mp save`; server restarts (no seed — continues the save). The lease
table must be RELOADED from the sidecar: the same key held by 0, done — so a client arriving there later gets
LeaseTaken instead of a duplicate spawn."""
import sys, time, os, subprocess, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
import tldenv
from mplib import Agent, ensure_both, DESKTOP_IP

def until(fn, timeout=25, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def launch_headless(seed=None):
    game.start(["-batchmode", "-nographics"] + (["-tldseed", str(seed)] if seed else []))

def wait_server(timeout=300):
    t0 = time.time()
    while time.time() - t0 < timeout:
        try:
            b = Bridge()
            if b("mp status").get("server"): return b
        except Exception: pass
        time.sleep(3)
    return None

if "--no-build" not in sys.argv: game.build()
env = dict(os.environ, TLD_REMOTE=tldenv.need_second())
subprocess.run([sys.executable, "tools/game.py", "deploy"], env=env, check=True)
game.quit_game(); subprocess.run([sys.executable, "tools/game.py", "quit"], env=env)

# ---------- boot 1: fresh world; a distant lease granted to the client and completed
launch_headless(20260924)
b = wait_server()
if b is None: sys.exit("FAIL: server did not come up")
print("boot 1: server up")
L = Agent()
try:
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    L(f"mp join {DESKTOP_IP}:27070")
    until(lambda: L("mp status")["client"]["ready"], 150)
    b("call TLDRevamp.Net.Entities.ServerLease 1 500000,900000")   # a poi 500 km out
    b("call TLDRevamp.Net.Entities.ServerLeaseDone 1 500000,900000")
    lt = b("leasetable")
    print("  granted+done:", [(r["key"], r["holder"], r["done"]) for r in lt["leases"] if r["key"] == "500000,900000"])
    print("  mp save:", b("mp save"))
    L("mp stop"); b("mp stop"); time.sleep(2)

    # ---------- boot 2: continues the save; the lease must come back from the sidecar
    game.quit_game(); time.sleep(4)
    launch_headless()
    b = wait_server()
    if b is None: sys.exit("FAIL: restarted server did not come up")
    time.sleep(3)
    lt = b("leasetable")
    row = [(r["key"], r["holder"], r["done"]) for r in lt["leases"] if r["key"] == "500000,900000"]
    print("  after restart:", row or "GONE")
    loaded = bool(row) and row[0][1] == 0 and row[0][2] is True
    # and the client asking for that key now gets LeaseTaken (no fresh grant)
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    L(f"mp join {DESKTOP_IP}:27070")
    until(lambda: L("mp status")["client"]["ready"], 150)
    granted_before = b("leasetable")["granted"]
    L("call TLDRevamp.Net.Entities.ServerLease 1 500000,900000")   # a client arriving there would ask exactly this
    granted_after = b("leasetable")["granted"]
    print(f"  client asks for the far poi: fresh grant issued {granted_after > granted_before} (must be False)")
    print("LEASE-PERSIST VERDICT:", "PASS — distant leases survive the restart and hand out LeaseTaken"
          if loaded and granted_after == granted_before else "FAIL")
    b("mp stop"); L("mp stop")
finally:
    L.close()

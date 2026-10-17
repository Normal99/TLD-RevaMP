#!/usr/bin/env python3
"""Lease tables belong to their world (v0.64.5). Single machine, read-only on the player's saves (ProtectedSession):
  A  a NEW world hosts with none of the last save's leases (before: autosave1's table → start buildings stayed empty)
  B  a LOADED save hosts with its own table (autosave1 here, if it has one)
  C  a blocked autosave moves no lease files (the first fix rotated them anyway)
  D  a new world started after that load drops the loaded table again
  tools/leaseworld.py [--save autosave1]"""
import sys, time, argparse, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
ap = argparse.ArgumentParser(); ap.add_argument("--save", default="autosave1"); a = ap.parse_args()
LF = game.SAVES / f"TLDsaveData_{a.save}.tlds.leases"
saved = [l.split("\t")[0] for l in LF.read_text().splitlines() if l.strip()] if LF.exists() else []
print(f"  {LF.name}: {saved}", flush=True)
VERDICTS = []
def verdict(n, ok, info): VERDICTS.append((n, ok)); print(f"{'PASS' if ok else 'FAIL'}  {n}  {info}", flush=True)
def leases(b): return [r["key"] for r in b("leasetable")["leases"]]
def files(): return sorted(p.name for p in game.SAVES.glob("*autosave*.leases"))

game.quit_game(); game.launch()
with game.ProtectedSession():
    game.newgame(); b = Bridge(); b("set TLDRevamp.TestSafety.BlockAutoSave True")
    time.sleep(8); b("mp host 27070"); time.sleep(3)
    own = leases(b); g = b("gpos")
    # saved keys the new world can't have spawned itself (far from its start): any of them in its table = inherited
    far = [k for k in saved if ((float(k.split(",")[0]) - g["gx"]) ** 2 + (float(k.split(",")[1]) - g["gz"]) ** 2) ** 0.5 > 1000]
    print(f"  new world, hosting: table {own}", flush=True)
    b("mp stop"); time.sleep(2)
    # C: the game's AutoSave, blocked
    f0 = files(); n0 = b("get TLDRevamp.TestSafety.Blocked")["value"]
    print("  autosave call:", b("call savedatascript.s.saveScreen.AutoSave"), flush=True); time.sleep(2)
    f1 = files(); n1 = b("get TLDRevamp.TestSafety.Blocked")["value"]
    verdict("C A BLOCKED AUTOSAVE MOVES NO LEASE FILES", f0 == f1 and n1 == n0 + 1, f"files {f0} → {f1}, blocked {n0} → {n1}")
    if saved:
        b = game.loadsave(a.save); b("mp host 27070"); time.sleep(3)
        mine = leases(b); print(f"  {a.save} loaded, hosting: table {mine}", flush=True)
        b("mp stop")
        verdict("B A LOADED SAVE HOSTS WITH ITS OWN LEASES", all(k in mine for k in saved), f"{len([k for k in saved if k in mine])}/{len(saved)} of its table")
        # D: a new world right after that load must not keep the loaded save's table
        game.newgame(); b = Bridge(); time.sleep(8); b("mp host 27070"); time.sleep(3)
        after = leases(b); g2 = b("gpos"); b("mp stop")
        far2 = [k for k in saved if ((float(k.split(",")[0]) - g2["gx"]) ** 2 + (float(k.split(",")[1]) - g2["gz"]) ** 2) ** 0.5 > 1000]
        print(f"  new world after the load, hosting: table {after}", flush=True)
        verdict("D A NEW WORLD AFTER A LOAD DROPS THE LOADED SAVE'S LEASES", len(far2) > 0 and not any(k in after for k in far2),
                f"of the far saved keys in its table: {[k for k in far2 if k in after]}")
    # A last: a new world's table must not contain the saved keys it did not spawn itself — checked against B's keys
    verdict("A A NEW WORLD HOSTS WITHOUT THE LAST SAVE'S LEASES", len(far) > 0 and not any(k in own for k in far),
            f"far saved keys {far}, of them in the new world's table: {[k for k in far if k in own]}")
    print("  lease files at the end:", files(), flush=True)
game.quit_game()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]

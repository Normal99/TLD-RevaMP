#!/usr/bin/env python3
"""I5 (bench/CORE-CHECKLIST.md): bullet holes on a player's body seen by a third player. Three players with two
machines: the desktop hosts, the laptop joins over the relay, and a second copy of the game on the desktop (its own
Proton prefix, TLD_THIRD_COMPAT, default ~/tld-revamp-testprefix3, bridge port 27052) joins the host by IP. The host
shoots the laptop's player standing 9 m away; the third copy looks for flesh holes on the laptop's player's body.
  A  the third copy joined (a third player in the game)
  B  the host's hits made holes on the laptop's player's body on the third copy (as many as the host's screen hit)
  C  no errors on the three
  tools/thirdplayer.py [--no-build] [--relay iad|off]"""
import os, sys, time, json, math, shutil, socket, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, tldenv
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, tp

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY = arg("--relay", "iad")
COMPAT3 = os.path.expanduser(os.environ.get("TLD_THIRD_COMPAT", ""))
GAME3 = os.environ.get("TLD_THIRD_DIR")
if not GAME3 or not COMPAT3 or not os.environ.get("TLD_THIRD_BRIDGE_PORT"):
    sys.exit("third copy requires TLD_THIRD_DIR, TLD_THIRD_COMPAT and TLD_THIRD_BRIDGE_PORT")
PORT3, MAGNUM = int(os.environ["TLD_THIRD_BRIDGE_PORT"]), 64
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

class Third:
    def __init__(self): self.f = None
    def connect(self, timeout=240):
        from bench import Bridge
        t0 = time.monotonic()
        while time.monotonic() - t0 < timeout:
            try:
                self.bridge = Bridge(port=PORT3, game_dir=GAME3, compat=COMPAT3)
                import testdisplay
                testdisplay.configure(self.bridge)
                testdisplay.check(testdisplay.verify(self.bridge))
                self.f = self.bridge.f
                return True
            except (OSError, RuntimeError):
                time.sleep(3)
        return False
    def __call__(self, cmd): return self.bridge(cmd)

def pos(s): q = s("gpos"); return q["gx"], q.get("gy", q.get("y")), q["gz"]

if not tldenv.TEST_COMPAT: sys.exit("needs the tests' own prefix (TLD_TEST_COMPAT): the third copy's is made from it")
L = Agent()
ensure_both("--no-build" not in sys.argv)
if not os.path.isdir(COMPAT3):
    print("  making the third copy's prefix from the tests' own:", shutil.copytree(tldenv.TEST_COMPAT, COMPAT3, symlinks=True), flush=True)
T = Third()
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
        # the third copy plays on the host's Steam account: a test host lets it in more than once (without player saves)
        h("set TLDRevamp.Net.MpServer.TestSameAccount True")
        r = game.sh(game.prefix_launch_cmd(["-tldbridge", str(PORT3)], compat=COMPAT3, game_dir=GAME3, port=PORT3))
        if r.returncode != 0: sys.exit("third copy: guarded launch refused; no fallback")
        if not T.connect(): sys.exit("THE THIRD COPY'S BRIDGE NEVER ANSWERED")
        print("  third copy:", T("ping"), flush=True)
        T("bg on"); T("set UnityEngine.QualitySettings.vSyncCount 0")   # full speed unfocused (mplib.seeded_both)
        until(lambda: T("get DataFromMenuScript.s.mainmenu")["value"] is True, 120)
        joined = False
        for _try in range(3):
            T("mp join 127.0.0.1:27070")
            if until(lambda: (T("mp status").get("client") or {}).get("ready"), 150): joined = True; break
            T("mp stop"); time.sleep(3)
        players = h("mp players")["players"]
        # the host's list is the others (run 1: [1, 2] with all three in, read as a FAIL)
        verdict("A THE THIRD COPY JOINED", joined and len(players) >= 2, f"host players {[p_.get('id') for p_ in players]} (and the host)")
        if joined:
            T("set TLDRevamp.TestSafety.BlockAutoSave True"); T("set kaposztaleves.s.settings.god True")
            rp = h("roadpoint"); x, z = rp["gx"], rp["gz"]
            tp(h, x, z); tp(L, x + 9, z); tp(T, x + 4, z + 6); time.sleep(6)
            lid = L("mp status")["client"]["id"]
            print("  host's magnum:", h(f"devspawn {MAGNUM}"), flush=True); time.sleep(3)
            hits = 0
            for _ in range(6):
                hits += h(f"mp shootat {lid} 1.0").get("hits", 0) or 0; time.sleep(0.7)
            time.sleep(3)
            lx, ly, lz = pos(L)
            seen = T(f"mp decals {lx} {ly + 0.9} {lz} 2.5")["decals"]
            print(f"  host hit {hits} times; the third copy has {len(seen)} decals near the laptop's player: {seen[:6]}", flush=True)
            verdict("B THE THIRD PLAYER SEES THE HOLES ON THE LAPTOP'S PLAYER", hits > 0 and len(seen) >= hits, f"{hits} hits, {len(seen)} holes on the third copy")
        er = [x_("errors") for x_ in (h, L) + ((T,) if T.f else ())]
        verdict("C NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        try:
            if T.f: T("mp stop")
        except Exception: pass
        time.sleep(5); game.kill_prefix(COMPAT3, game_dir=GAME3, port=PORT3)
        h("set TLDRevamp.Net.MpServer.TestSameAccount False")
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
print(f"THIRDPLAYER: {sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} PASS")

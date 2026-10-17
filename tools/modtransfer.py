#!/usr/bin/env python3
"""A joiner gets a mod from the host (Net/ModTransfer.cs), two machines over the relay. The host runs the released Head
Unit (dist/HeadUnit-<v>.zip: the file packaging/approved-mods.txt approves); the laptop has no Head Unit.
  A  the laptop joins: it is asked (the mod's name, version and size), the join waits for the answer
  B  yes: the file arrives, is the approved one (sha256), lands in BepInEx/plugins/TLDRevampMods, and the join ends
     with "restart the game, then join again"
  C  after a restart the laptop loads the mod and joins: in the world, the host's mod list matched
  D  unapproved: the laptop's approved list without the Head Unit line, its downloaded copy gone again: the offer is
     refused with a message, nothing downloaded
  E  no: answered no, the join ends with what to install, nothing downloaded
  tools/modtransfer.py [--no-build] [--relay iad|off]"""
import os
os.environ.pop("TLD_TEST_MODS", None)   # no example mods from the test harness: the host gets the released one below
import sys, time, pathlib, subprocess, hashlib, zipfile, glob
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, bench
import tldenv
from mplib import Agent, ensure_both, seeded_both, relay_both, REMOTE_HOST, local_build_hash, running_build

ROOT = pathlib.Path(__file__).resolve().parent.parent
def arg(n, d): return sys.argv[sys.argv.index(n) + 1] if n in sys.argv else d
RELAY = arg("--relay", "iad")
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

GAME = tldenv.GAME_DIR
RPLUG = tldenv.REMOTE_GAME + "/BepInEx/plugins"
ENV = dict(os.environ, TLD_REMOTE=REMOTE_HOST)

def rsh(cmd):
    return subprocess.run(tldenv.ssh_args(REMOTE_HOST)[:-1] + bench.SSH_MUX + [REMOTE_HOST, cmd],
                          capture_output=True, text=True)

def laptop_restart():
    subprocess.run([sys.executable, str(ROOT / "tools/game.py"), "quit"], env=ENV)
    subprocess.run([sys.executable, str(ROOT / "tools/game.py"), "launch"], env=ENV)
    want = local_build_hash(); t0 = time.time()
    while time.time() - t0 < 180:
        try:
            if running_build(REMOTE_HOST) == want: return True
        except Exception: pass
        time.sleep(3)
    return False

def join(h, L, wait_ask=90):
    """join; wait for a question or the end of the join. Returns (ask, client status)"""
    L("mp stop"); h("mp stop"); time.sleep(2)
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    t0 = time.time()
    while time.time() - t0 < wait_ask:
        a = L("mp ask")
        if a["open"]: return a, L("mp status").get("client") or {}
        c = ended(L)
        if c.get("ready") or c.get("reject"): return a, c
        time.sleep(1)
    print("  join stalled: laptop", str(L("mp status"))[:400], "| host", str(h("mp status"))[:300], flush=True)
    return L("mp ask"), L("mp status").get("client") or {}

def ended(L):
    """the client's state; once the session has ended (the client is gone) its reason is kept by Session"""
    c = L("mp status").get("client")
    if c: return c
    return {"reject": L("mp session").get("endReason")}

def wait_end(L, t=60):
    t0 = time.time()
    while time.time() - t0 < t:
        c = ended(L)
        if c.get("reject") or c.get("ready"): return c
        time.sleep(1)
    return ended(L)

zips = sorted(glob.glob(str(ROOT / "dist/HeadUnit-*.zip")))
if not zips: sys.exit("no dist/HeadUnit-*.zip: run tools/package-mods.sh")
with zipfile.ZipFile(zips[-1]) as z: HU = z.read("BepInEx/plugins/HeadUnit/HeadUnit.dll")
HU_SHA = hashlib.sha256(HU).hexdigest()
approved = (ROOT / "packaging/approved-mods.txt").read_text()
print(f"  released Head Unit {pathlib.Path(zips[-1]).name}: sha256 {HU_SHA[:12]}, approved: {HU_SHA in approved}", flush=True)
if HU_SHA not in approved: sys.exit("the released Head Unit is not on packaging/approved-mods.txt")

L = Agent()
ensure_both("--no-build" not in sys.argv)
# the host: the released Head Unit; the laptop: none, nothing downloaded before
if game.running(): game.quit_game()
(GAME / "BepInEx/plugins/HeadUnit").mkdir(parents=True, exist_ok=True)
(GAME / "BepInEx/plugins/HeadUnit/HeadUnit.dll").write_bytes(HU)
game.launch()
rsh(f'rm -rf "{RPLUG}/TLDRevampMods" "{RPLUG}/HeadUnit"')
laptop_restart()
try:
    with game.ProtectedSession():
        h = seeded_both(L)
        for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
        print("  host mods:", h("mp mods"), flush=True)
        if RELAY != "off": relay_both(h, L, RELAY)

        # A / B
        ask, c = join(h, L)
        print("  ask:", ask, "client:", {k: c.get(k) for k in ("ready", "reject")}, flush=True)
        verdict("A THE LAPTOP IS ASKED (name, version, size)", ask["open"] and "Head Unit" in ask["text"] and "0.1.0" in ask["text"], ask["text"][:140])
        if ask["open"]:
            L("mp ask yes")
            c = wait_end(L)
            got = rsh(f'sha256sum "{RPLUG}"/TLDRevampMods/*.dll').stdout.strip()
            print("  after yes:", c.get("reject"), "| on disk:", got, "| laptop:", L("mp modtransfer"), "| host:", h("mp modtransfer"), flush=True)
            verdict("B DOWNLOADED, THE APPROVED FILE, IN TLDRevampMods; RESTART ASKED",
                    HU_SHA in got and "Restart the game" in (c.get("reject") or ""), (c.get("reject") or "")[:100])
        L("mp stop"); h("mp stop")

        # C after a restart
        if not laptop_restart(): verdict("C SETUP: THE LAPTOP RESTARTED", False)
        else:
            L("!seeded"); time.sleep(3)
            L("set TLDRevamp.TestSafety.BlockAutoSave True")
            if RELAY != "off": relay_both(h, L, RELAY)
            ask, c = join(h, L, 60)
            c = c if c.get("ready") or c.get("reject") else wait_end(L, 60)
            lm = L("mp mods")
            print("  rejoin:", {k: c.get(k) for k in ("ready", "reject")}, "ask", ask["open"], "laptop mods:", lm, flush=True)
            verdict("C AFTER A RESTART THE LAPTOP HAS IT AND JOINS", bool(c.get("ready")) and not ask["open"] and lm.get("mods", 0) >= 1, str(c.get("reject"))[:100])
            L("mp stop"); h("mp stop")

        # D unapproved: the laptop's own list without the line, its copy gone
        rsh(f'rm -rf "{RPLUG}/TLDRevampMods"; cp "{RPLUG}/TLDRevamp/approved-mods.txt" /tmp/approved.bak; grep -v {HU_SHA} /tmp/approved.bak > "{RPLUG}/TLDRevamp/approved-mods.txt"')
        if laptop_restart():
            L("!seeded"); time.sleep(3); L("set TLDRevamp.TestSafety.BlockAutoSave True")
            if RELAY != "off": relay_both(h, L, RELAY)
            ask, c = join(h, L)
            c = c if c.get("reject") else wait_end(L, 30)
            on = rsh(f'ls "{RPLUG}/TLDRevampMods" 2>/dev/null').stdout.strip()
            print("  unapproved:", c.get("reject"), "ask", ask["open"], "files", repr(on), flush=True)
            verdict("D NOT ON THE LAPTOP'S LIST: REFUSED, NOTHING DOWNLOADED",
                    not ask["open"] and "approved list" in (c.get("reject") or "") and not on, (c.get("reject") or "")[:100])
            L("mp stop"); h("mp stop")
        else: verdict("D SETUP: THE LAPTOP RESTARTED", False)
        rsh(f'cp /tmp/approved.bak "{RPLUG}/TLDRevamp/approved-mods.txt"')

        # E no
        if laptop_restart():
            L("!seeded"); time.sleep(3); L("set TLDRevamp.TestSafety.BlockAutoSave True")
            if RELAY != "off": relay_both(h, L, RELAY)
            ask, c = join(h, L)
            if ask["open"]: L("mp ask no")
            c = wait_end(L, 30)
            on = rsh(f'ls "{RPLUG}/TLDRevampMods" 2>/dev/null').stdout.strip()
            print("  no:", c.get("reject"), "files", repr(on), flush=True)
            verdict("E ANSWERED NO: THE JOIN ENDS WITH WHAT TO INSTALL, NOTHING DOWNLOADED",
                    ask["open"] and "You need" in (c.get("reject") or "") and not on, (c.get("reject") or "")[:100])
        else: verdict("E SETUP: THE LAPTOP RESTARTED", False)
        er = [x("errors") for x in (h, L)]
        print("  errors:", [(e["errors"], e["exceptions"], (e.get("kinds") or [])[:3]) for e in er], flush=True)
        verdict("NO ERRORS", all(e["exceptions"] == 0 for e in er))
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
finally:
    rsh(f'rm -rf "{RPLUG}/TLDRevampMods"')
    if game.running(): game.quit_game()
    import shutil; shutil.rmtree(GAME / "BepInEx/plugins/HeadUnit", ignore_errors=True)
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"MODTRANSFER: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")

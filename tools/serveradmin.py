#!/usr/bin/env python3
"""The dedicated server's admin side (Net/Admin.cs, DedicatedServer.StripVisuals; architecture §8), two machines: the
desktop runs HEADLESS (-batchmode -nographics, autohosts a fresh seeded world), the laptop joins by IP.
  A  the console file: "players" in commands.txt is answered in console.txt (the laptop's Steam ID listed)
  B  a player who isn't an admin types /players: told it is for admins, nothing run
  C  the laptop's Steam ID made an admin ([Server] Admins, set here through MpServer.SetAdmins): /say reaches the chat
  D  ban by Steam ID from the console: the laptop is put out ("banned"), bans.txt has it, a new join is refused;
     unban: the laptop joins again
  E  stripping: the server's audio sources and particle systems are off (`serverstrip` sweeps; counts)
  F  "stop" from the console: the server saves and quits
  G  no errors on the laptop
  tools/serveradmin.py [--no-build]"""
import sys, time, os, shutil, subprocess, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, tldenv
from bench import Bridge
from mplib import Agent, DESKTOP_IP, ensure_both
from roadkit import until

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

SDIR = pathlib.Path(tldenv.GAME_DIR) / "BepInEx" / "TLDRevampServer"
CMDS, OUT, BANS = SDIR / "commands.txt", SDIR / "console.txt", SDIR / "bans.txt"

def console(line, expect=None, timeout=12):
    """one command into the console file; the answer lines that came after it in console.txt"""
    n0 = OUT.stat().st_size if OUT.exists() else 0
    with open(CMDS, "a", encoding="utf-8") as f: f.write(line + "\n")
    got = ""
    def seen():
        nonlocal got
        if not OUT.exists(): return False
        with open(OUT, encoding="utf-8", errors="replace") as f: f.seek(n0); got = f.read()
        return ("> " + line) in got and (expect is None or expect in got)
    until(seen, timeout)
    return got

def wait_server(timeout=300):
    t0 = time.time()
    while time.time() - t0 < timeout:
        try:
            b = Bridge()
            if b("mp status").get("server"): return b
        except Exception: pass
        time.sleep(3)
    return None

def lines(L): return L("mp session").get("lines") or []
SERVER_ID = None
def join(L, tries=3):
    """the laptop joins the server by its Steam ID (P2P over Steam, as join_both: runs 1-2 by IP never got in — the
    laptop isn't always on the desktop's network)"""
    for k in range(tries):
        L(f"mp joinid {SERVER_ID}")
        if until(lambda: (L("mp status").get("client") or {}).get("ready"), 60): return True
        print(f"  join try {k + 1}: {(L('mp status').get('client') or {}).get('last')} | {L('mp session').get('endReason')}", flush=True)
        L("mp stop"); time.sleep(3)
    return False

L = Agent()
ensure_both("--no-build" not in sys.argv)   # both on this build, the laptop's game up (run 1: deployed, never started)
with game.ProtectedSession():
    game.quit_game(); time.sleep(3)
    SDIR.mkdir(parents=True, exist_ok=True)
    bans_keep = BANS.read_bytes() if BANS.exists() else None
    if BANS.exists(): BANS.unlink()
    b = None
    try:
        game.start(["-batchmode", "-nographics", "-tldseed", "20260924"])
        b = wait_server()
        if b is None: sys.exit("THE HEADLESS SERVER NEVER CAME UP")
        print("  server:", b("admin status"), flush=True)
        SERVER_ID = b("net me")["steamId"]
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if not join(L): sys.exit("LAPTOP NEVER JOINED")
        sid = L("net me")["steamId"]
        time.sleep(4)
        # A
        got = console("players", expect=sid)
        verdict("A THE CONSOLE FILE ANSWERS", sid in got, got.strip().replace("\n", " | ")[:200])
        # B
        r0 = int(b("get TLDRevamp.Net.MpServer.AdminRefused")["value"])
        L("mp chat say /players"); time.sleep(3)
        r1 = int(b("get TLDRevamp.Net.MpServer.AdminRefused")["value"])
        told = any("is for the server's admins" in str(x) for x in lines(L))
        verdict("B NOT AN ADMIN: REFUSED", r1 == r0 + 1 and told, f"refused {r0} -> {r1}, told {told}")
        # C
        b(f"call TLDRevamp.Net.MpServer.SetAdmins {sid}")
        L("mp chat say /say hello from an admin"); time.sleep(3)
        said = any("hello from an admin" in str(x) for x in lines(L))
        verdict("C AN ADMIN'S /say REACHES THE CHAT", said, f"laptop lines {[str(x)[:60] for x in lines(L)[-3:]]}")
        # D
        got = console(f"ban {sid}", expect="banned")
        out = until(lambda: not (L("mp status").get("client") or {}).get("ready"), 15)
        why = L("mp session").get("endReason")
        in_file = BANS.exists() and sid in BANS.read_text()
        L("mp stop"); time.sleep(2)
        L(f"mp joinid {SERVER_ID}")
        refused = until(lambda: "banned" in str(L("mp session").get("endReason") or ""), 60)
        L("mp stop"); time.sleep(2)
        console(f"unban {sid}", expect="no longer banned")
        back = join(L)
        verdict("D BAN: OUT, IN bans.txt, REFUSED; UNBAN: BACK", out and "banned" in str(why) and in_file and refused and back,
                f"out {out} ({why}), in bans.txt {in_file}, new join refused {refused}, back after unban {back}")
        # E
        st = b("serverstrip")
        print("  strip:", st, flush=True)
        verdict("E THE SERVER'S AUDIO AND PARTICLES ARE OFF", isinstance(st, dict) and (st.get("audioOn", 1) == 0 and st.get("particlesOn", 1) == 0), str(st)[:200])
        er = L("errors")
        verdict("G NO ERRORS ON THE LAPTOP", er["errors"] == 0 and er["exceptions"] == 0, str((er["errors"], er["exceptions"], (er.get("kinds") or [])[:3])))
        # F
        L("mp stop"); time.sleep(2)
        got = console("stop", expect="stopping")
        quit_ = until(lambda: not game.running(), 60, 2)
        verdict("F STOP: SAVED AND QUIT", "stopping" in got.lower() and quit_, got.strip().replace("\n", " | ")[:200])
    finally:
        try: L("mp stop")
        except Exception: pass
        if game.running(): game.quit_game()
        if BANS.exists(): BANS.unlink()
        if bans_keep is not None: BANS.write_bytes(bans_keep)
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
print(f"SERVERADMIN: {sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} PASS")

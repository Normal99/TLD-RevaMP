#!/usr/bin/env python3
"""The dedicated server folder (tools/makeserver.sh) and its settings, two machines. The folder is made from this
machine's install, started with its own launcher (start-server.sh: Proton, outside the Steam launcher), on a fresh
world; its [Server] settings: a password, 6 players at most, autosave every minute; the bridge on for the test only.
  A  the folder's server comes up headless and hosts
  B  the settings apply: a wrong password is refused, the right one gets in
  C  it saves by itself (an autosave within ~70 s)
  D  per-player saves on a dedicated server: the laptop leaves, the server has its record on disk
  tools/serverfolder.py [--no-build] [--out ~/TLD-Server-test]"""
import sys, time, os, subprocess, pathlib, json
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP
from roadkit import until

ROOT = pathlib.Path(__file__).resolve().parent.parent
def arg(n, d): return sys.argv[sys.argv.index(n) + 1] if n in sys.argv else d
OUT = pathlib.Path(os.path.expanduser(arg("--out", "~/TLD-Server-test")))
PW = "folder-test"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

ensure_both("--no-build" not in sys.argv)
game.quit_game()   # one game at a time on this machine: the folder's server takes the bridge port
subprocess.run([str(ROOT / "tools/makeserver.sh"), str(OUT)], check=True)
cfg = OUT / "BepInEx/config/tldrevamp.core.cfg"
txt = cfg.read_text().replace("Bridge = false", "Bridge = true").replace("Password =\n", f"Password = {PW}\n")
txt = txt.replace("MaxPlayers = 16", "MaxPlayers = 6").replace("AutoSaveMinutes = 5", "AutoSaveMinutes = 1")
cfg.write_text(txt)
log = open(OUT / "server-test.log", "w")
srv = subprocess.Popen([str(OUT / "start-server.sh"), "-tldseed", "20260924"], stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
L = Agent()
try:
    b = None; t0 = time.time()
    while time.time() - t0 < 300 and b is None:
        try:
            x = Bridge()
            if x("mp status").get("server"): b = x
        except Exception: pass
        if b is None: time.sleep(3)
    verdict("A THE FOLDER'S SERVER COMES UP AND HOSTS", b is not None, f"after {time.time() - t0:.0f} s")
    if b is None: sys.exit(1)
    print("  server:", str(b("mp session"))[:300], flush=True)
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    L(f"mp join {DESKTOP_IP}:27070 wrong"); time.sleep(12)
    s1 = L("mp session"); refused = "password" in (s1.get("endReason") or "").lower()
    L(f"mp join {DESKTOP_IP}:27070 {PW}")
    ok = until(lambda: (L("mp status").get("client") or {}).get("ready"), 150, 2)
    verdict("B SETTINGS: A WRONG PASSWORD IS REFUSED, THE RIGHT ONE GETS IN", refused and ok, f"wrong: {s1.get('endReason')!r}; joined {ok}")
    a0 = b("get TLDRevamp.Net.DedicatedServer.AutoSaves")["value"]
    time.sleep(75)
    a1 = b("get TLDRevamp.Net.DedicatedServer.AutoSaves")["value"]
    verdict("C IT SAVES BY ITSELF", int(a1) > int(a0), f"autosaves {a0} -> {a1}")
    L("mp stop"); time.sleep(5)
    ps = b("mp playersave")
    files = list(OUT.glob("prefix/pfx/drive_c/users/steamuser/Documents/TheLongDrive/Saves/*.players"))
    print("  playersave:", str(ps)[:300], "files", files, flush=True)
    verdict("D THE LEAVER'S RECORD IS KEPT (on disk)", len(ps.get("players", [])) >= 1 and bool(files), f"{len(ps.get('players', []))} records, {len(files)} files")
    er = b("errors"); print("  server errors:", er["errors"], er["exceptions"], (er.get("kinds") or [])[:3], flush=True)
    verdict("NO EXCEPTIONS ON THE SERVER", er["exceptions"] == 0)
finally:
    try: Bridge()("call UnityEngine.Application.Quit")
    except Exception: pass
    time.sleep(8)
    try: os.killpg(srv.pid, 15)
    except Exception: pass
    subprocess.run("pkill -f 'TLD-Server-test/TheLongDrive[.]exe'", shell=True)
    L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"SERVERFOLDER: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")

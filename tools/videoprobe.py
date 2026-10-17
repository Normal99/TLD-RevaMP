#!/usr/bin/env python3
"""Which video files the game's player decodes on a machine (HeadUnit.Probe.PlayFile): each named file from this
machine's head unit cache (or --send FILE first: copied into the target's cache) played on its own, its state after
--wait seconds. Under stock Valve Proton an H.264 mp4 plays a 4 s stand-in clip (length 4.0) instead of the video.
  TLD_REMOTE=user@laptop TLD_TEST_MODS=HeadUnit tools/videoprobe.py [--no-build] [--send a.webm] FILE..."""
import os
os.environ.setdefault("TLD_TEST_MODS", "HeadUnit")
import sys, time, pathlib, subprocess
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, tldenv
from bench import Bridge, REMOTE, ssh_base

def arg(n, d): return sys.argv[sys.argv.index(n) + 1] if n in sys.argv else d
WAIT = float(arg("--wait", "8"))
skip = {"--no-build", "--send", "--wait"}
files = [a for i, a in enumerate(sys.argv[1:], 1) if a not in skip and sys.argv[i - 1] not in ("--send", "--wait")]
send = [sys.argv[i + 1] for i, a in enumerate(sys.argv) if a == "--send"]
cache = (tldenv.rsync_path(tldenv.REMOTE_GAME) if REMOTE else str(tldenv.GAME_DIR)) + "/BepInEx/cache/HeadUnit/"
for f in send:
    if REMOTE: subprocess.run(["rsync", "-a", "-s", "--mkpath", "-e", " ".join(ssh_base()[:-1]), f, f"{REMOTE}:{cache}"], check=True)
    else: subprocess.run(["cp", f, cache], check=True)
    files.append(pathlib.Path(f).name)

with game.ProtectedSession():
    if "--no-build" not in sys.argv: game.build()
    else: game.local_mods()
    game.quit_game(); game.launch(); game.newgame()
    b = Bridge(); time.sleep(3)
    for f in files:
        print(f, "→", b(f"call HeadUnit.Probe.PlayFile {f}")["value"], flush=True)
        time.sleep(WAIT)
        print("  ", b("call HeadUnit.Probe.FileState")["value"], flush=True)
    game.quit_game()

#!/usr/bin/env python3
"""Voice playback on the desktop alone (v0.65.1): a host and one still bot 5 m off; the test tone fed straight into the
bot's speaker (no network). Prints every speaker snapshot: does the AudioSource pull the stream (fills, level)?
  tools/voiceprobe.py [--no-build]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

with game.ProtectedSession():
    if game.running(): game.quit_game()
    if "--no-build" not in sys.argv: game.build()
    else: game.deploy_local()
    game.launch(); game.newgame()
    b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True")
    try:
        time.sleep(3)
        b("mp host 27070")
        print("bots:", b("mp bots 1 5 0"), flush=True)
        time.sleep(6)
        pl = b("mp players"); print("players:", pl, flush=True)
        ids = [p["id"] for p in pl.get("players", []) if isinstance(p, dict) and p.get("id")] or [1]
        bid = ids[0]
        print("feed:", b(f"mp voice feed {bid} 3"), flush=True)
        for _ in range(16):
            v = b("mp voice"); print(f"  {time.strftime('%X')} played {v['played']} speakers {v['speakers']}", flush=True)
            time.sleep(0.25)
        time.sleep(1.5); print("after:", b("mp voice"), flush=True)
    finally:
        try: b("mp stop")
        except Exception as e: print("stop", e)

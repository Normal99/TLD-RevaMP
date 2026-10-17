#!/usr/bin/env python3
"""The multiplayer screen and chat HUD on the desktop alone (v0.65.0): screenshots of each state, for layout checks.
  1 pause menu → the game's own Multiplayer button: our screen (not the game's lobby window)
  2 Join by address
  3 Host with a password (typed into the screen's field)
  4 chat lines on the HUD, then the chat input open
  tools/mpuisp.py [--no-build]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, tldenv
from bench import Bridge

SHOTS = tldenv.GAME_DIR / "BepInEx/tldrevamp-feedback"

def shot(b, tag):
    time.sleep(1.2)
    p = b("screenshot")["path"].split("\\")[-1]
    time.sleep(1.0)
    print(f"  shot {tag}: {SHOTS / p}", flush=True)

with game.ProtectedSession():
    if game.running(): game.quit_game()
    if "--no-build" not in sys.argv: game.build()
    else: game.deploy_local()
    game.launch(); game.newgame()
    b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True")
    try:
        time.sleep(3)
        b("call mainscript.s.PressedEscape False False"); time.sleep(1)
        b("call menuhandler.s.PressedMultiplayerInGame"); time.sleep(1.5)
        print("1 screen:", b("mp screen"), flush=True)
        print("  game lobby window open:", b("get SteamP2PMenuHandlerScript.ss.menuOpen"), flush=True)
        shot(b, "1 screen")
        print("2 address:", b("mp screen secondary"), flush=True); shot(b, "2 address")
        b("mp screen secondary")
        b("mp screen text secret")
        print("3 host:", b("mp screen primary"), flush=True)
        time.sleep(2)
        print("  session:", b("mp session"), flush=True)
        shot(b, "3 hosting")
        b("mp screen close"); b("call mainscript.s.PressedEscape False False"); time.sleep(1)
        print("  paused:", b("get mainscript.s.pauseMenuOpen"), flush=True)
        for t in ("hello from the host", "a second, longer line to see how the chat wraps when somebody writes a lot more than usual in one go", "third"):
            b(f"mp chat say {t}")
        print("4 chat:", b("mp chat"), flush=True); shot(b, "4 chat lines")
        b("mp chat open"); time.sleep(0.5)
        print("  typing:", b("mp chat"), flush=True); shot(b, "4b chat input")
        b("mp chat close")
        print("  voice:", b("mp voice"), flush=True)
    finally:
        try: b("mp stop")
        except Exception as e: print("stop", e)

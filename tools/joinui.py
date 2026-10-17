#!/usr/bin/env python3
"""Desktop alone: the host's own card in the Multiplayer screen (opened the player's way) has the button for where
joiners appear (v0.65.36); clicking flips MpServer.JoinAtHost and the label. Screenshots in BepInEx/tldrevamp-feedback."""
import sys, time
import pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
with game.ProtectedSession():
    if game.running(): game.quit_game()
    game.deploy_local(); game.launch(); game.newgame()
    b = Bridge(); b("set TLDRevamp.TestSafety.BlockAutoSave True")
    try:
        b("mp host 27070"); time.sleep(2)
        time.sleep(2); b("call mainscript.s.PressedEscape False False"); time.sleep(1)
        b("call menuhandler.s.PressedMultiplayerInGame"); time.sleep(1.5)
        r = b("mp screen"); print("open:", {k: r.get(k) for k in ("secondary", "primary")}, "JoinAtHost", b("get TLDRevamp.Net.MpServer.JoinAtHost")["value"], flush=True)
        time.sleep(1); print("shot on:", b("screenshot")["path"], flush=True)
        r = b("mp screen secondary"); print("click 1:", r.get("secondary"), "JoinAtHost", b("get TLDRevamp.Net.MpServer.JoinAtHost")["value"], flush=True)
        time.sleep(1); print("shot off:", b("screenshot")["path"], flush=True)
        r = b("mp screen secondary"); print("click 2:", r.get("secondary"), "JoinAtHost", b("get TLDRevamp.Net.MpServer.JoinAtHost")["value"], flush=True)
    finally:
        b("mp stop")

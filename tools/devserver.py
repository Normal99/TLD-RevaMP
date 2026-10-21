#!/usr/bin/env python3
"""Retired: TLD dedicated server launcher (headless dedicated server from the local game install).

It used to prepare a headless dedicated server from the local game install (nothing was shipped or
redistributed) and run it: -batchmode -nographics, optional -tldseed and -tldport, then a bridge poll
loop and a Ctrl-C save. Every invocation now refuses with a non-zero exit and does nothing else: no
import of game/tldenv/bench, no process, no network, no file and no game action. There is no --help
and no auto-launch; the supported GUI guarded workflow is named in the message.
"""
import sys

TOOL = "tools/devserver.py"

WHY = """\
The old flow started the game with -batchmode -nographics. The guarded launcher owns the display
arguments and refuses them (tools/testlaunch.py, bridge_args), so the old script could only quit the
running game first and then fail at the launch: it could kill the current session and still not
work. This tool therefore refuses before it imports game/tldenv/bench and before any process,
network, file or game action. Nothing was started, quit, deployed or connected.
"""

SUPPORTED = """\
Supported workflow (GUI, hidden and unfocused, one isolated copy):
  tools/game.py launch        start the isolated hidden test copy and wait for its bridge
  tools/game.py newgame       start a seeded world in that copy
  tools/game.py quit          quit it cleanly
  tools/README.md             setup, the three identities and the launch composition
  tools/LAUNCH-COMPOSITION.md the launch layers and the measurements behind them
  docs/REPRO-FORMAT.md        two-copy multiplayer checks (windowed; never -batchmode)

The mod's own dedicated-server mode has no supported launch path yet: it would need a separately
proven route, so it is not started from here.
"""


def main() -> int:
    sys.stderr.write(f"{TOOL} refused: headless mode is not supported under the current guarded "
                     "launch contract.\n\n" + WHY + "\n" + SUPPORTED +
                     "\nThe original source of this tool is preserved in the worker evidence archive.\n")
    return 2


if __name__ == "__main__":
    sys.exit(main())

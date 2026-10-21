#!/usr/bin/env python3
"""Legacy direct Proton server-folder test: disabled until it has a proven hidden display route.

The generated start-server.sh cannot meet automated test launch/ownership requirements.
Use tools/game.py with a dedicated game directory, initialized prefix and explicit port.
"""
import sys

if __name__ == "__main__":
    sys.exit("serverfolder automated launch refused: direct Proton/headless route has no hidden-window proof")

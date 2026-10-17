#!/usr/bin/env python3
"""Talk to the in-game debug bridge.  Usage: tools/tld.py stats | tools/tld.py "teleport 0 100 0" """
import socket, sys
cmds = sys.argv[1:] or ["ping"]
with socket.create_connection(("127.0.0.1", 27050), timeout=35) as s:
    f = s.makefile("rw", encoding="utf-8")
    for c in cmds:
        f.write(c + "\n"); f.flush()
        print(f.readline().rstrip())

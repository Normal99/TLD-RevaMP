#!/usr/bin/env python3
"""Talk to the in-game debug bridge.  Usage: tools/tld.py stats | tools/tld.py "teleport 0 100 0"
With TLD_REMOTE=user@host it talks to that machine's game, through an SSH tunnel (tools/README.md)."""
import socket, sys, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import bench
cmds = sys.argv[1:] or ["ping"]
if bench.REMOTE: bench.ensure_tunnel()
with socket.create_connection(("127.0.0.1", bench.PORT), timeout=35) as s:
    f = s.makefile("rw", encoding="utf-8")
    for c in cmds:
        f.write(c + "\n"); f.flush()
        print(f.readline().rstrip())

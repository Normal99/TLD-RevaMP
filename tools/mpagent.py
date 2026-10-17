#!/usr/bin/env python3
"""Second-machine helper for multi-machine tests: run with TLD_REMOTE set. Protects that machine's saves for the
whole session, then executes bridge commands read from stdin (one per line) and prints each JSON result on one line.
Special lines: `!seeded` (fresh world with the test seed), `!sleep <s>`. Ends (restoring autosaves) on EOF.
One bridge connection (through the SSH tunnel) serves every command; a dropped one (the game restarted, the tunnel
went) is made again before the next command. The game is started on the first command that finds it not running."""
import sys, os, json, time, select, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge

_b = None


def _drop():
    global _b
    if _b is not None:
        try: _b.s.close()
        except OSError: pass
    _b = None


def bridge():
    """The open connection, or a new one. Between commands nothing may arrive: anything readable (the other end closed
    it, or a late answer to a command that timed out here) means it is no longer in step, so it is made again."""
    global _b
    if _b is not None:
        try:
            if select.select([_b.s], [], [], 0)[0]: _drop()
        except (OSError, ValueError): _drop()
    if _b is None:
        try: _b = Bridge()
        except OSError:
            if game.running(): raise
            game.launch(); _b = Bridge()
    return _b


with game.ProtectedSession():
    print(json.dumps({"ready": True}), flush=True)
    for line in sys.stdin:
        line = line.strip()
        if not line: continue
        try:
            if line == "!seeded":
                bridge(); _drop()   # (the game started if it isn't) the restart below polls with connections of its own
                seeded.restart_same_seed(); bridge()("set TLDRevamp.TestSafety.BlockAutoSave True"); r = {"ok": True}
            elif line.startswith("!sleep"):
                time.sleep(float(line.split()[1])); r = {"ok": True}
            else:
                r = bridge()(line)
        except RuntimeError as e:   # the bridge answered with an error: the connection is fine
            r = {"error": str(e)[:300]}
        except Exception as e:      # closed, timed out, garbled: a new connection for the next command
            _drop(); r = {"error": str(e)[:300]}
        try: print(json.dumps(r), flush=True)
        except BrokenPipeError: break   # the test ended without closing us (its exit): the saves are still restored
# nothing more to write to a closed pipe (otherwise Python reports the failed flush at exit)
try: sys.stdout.flush()
except BrokenPipeError: os.dup2(os.open(os.devnull, os.O_WRONLY), sys.stdout.fileno())

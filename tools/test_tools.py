#!/usr/bin/env python3
"""Offline tests of the test tools' own helpers (no game needed): python3 -m unittest tools/test_tools.py"""
import os, pathlib, subprocess, sys, tempfile, unittest

TOOLS = pathlib.Path(__file__).resolve().parent


def run(code, env_extra=None, home=None):
    """Run `code` in a fresh interpreter with a clean TLD_ environment and HOME (the module reads both at import)."""
    env = {k: v for k, v in os.environ.items() if not k.startswith("TLD_")}
    env["HOME"] = home or tempfile.mkdtemp()
    env.update(env_extra or {})
    return subprocess.run([sys.executable, "-c", f"import sys; sys.path.insert(0, {str(TOOLS)!r}); " + code],
                          capture_output=True, text=True, env=env)


class TldEnv(unittest.TestCase):
    def test_defaults(self):
        r = run("import tldenv as t; print(t.GAME_DIR); print(repr(t.SECOND)); print(t.ssh_args('h'))", home="/nonexistent-home")
        lines = r.stdout.splitlines()
        self.assertEqual(lines[0], "/nonexistent-home/.local/share/Steam/steamapps/common/The Long Drive")
        self.assertEqual(lines[1], "''")
        self.assertEqual(lines[2], "['ssh', '-o', 'BatchMode=yes', 'h']")

    def test_config_file_and_precedence(self):
        home = tempfile.mkdtemp()
        cfg = pathlib.Path(home) / ".config/tld-revamp"; cfg.mkdir(parents=True)
        (cfg / "tools.env").write_text('# comment\nexport TLD_SECOND="me@pc"\nTLD_DIR=~/games/tld\nTLD_SSH_KEY=~/.ssh/k\n'
                                       "TLD_REMOTE_DIR=$HOME/x y\nTLD_CACHE=/c\n")
        r = run("import tldenv as t, os; print(t.SECOND, t.GAME_DIR, t.SSH_KEY, t.REMOTE_GAME, t.CACHE, os.environ['TLD_DIR'], t.SECOND_IP)",
                {"TLD_CACHE": "/from-env"}, home=home)
        self.assertEqual(r.stdout.split(" ", 1)[0], "me@pc")
        self.assertIn(f"{home}/games/tld {home}/.ssh/k $HOME/x y /from-env {home}/games/tld pc", r.stdout)

    def test_paths_for_the_second_machine(self):
        r = run("import tldenv as t; print(t.rsync_path('$HOME/a b')); print(t.rsync_path('/abs')); print(t.remote_quote('$HOME/a \"b'))")
        self.assertEqual(r.stdout.splitlines(), ["a b", "/abs", '"$HOME/a \\"b"'])

    def test_need_second_explains(self):
        r = run("import tldenv; tldenv.need_second()")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("TLD_SECOND=user@host", r.stderr)


class PrivacyScan(unittest.TestCase):
    def test_catches_and_allows(self):
        sys.path.insert(0, str(TOOLS)); import privacy_scan
        d = pathlib.Path(tempfile.mkdtemp())
        privacy_scan.ROOT = d
        # written in pieces, so this file passes the scan itself
        (d / "bad.py").write_text("p = '/ho" + "me/alice/x'\nip = '192." + "168.1.5'\nid = 7656119" + "8000000000\nclass main" + "script : MonoBehaviour {}\n")
        (d / "ok.py").write_text("p = '$HOME/x'; ip = '127.0.0.1'; doc = '198.51.100.1'; w = 'C:\\\\users\\\\steamuser\\\\Documents'\n")
        (d / "x.dll").write_bytes(b"MZ")
        found = privacy_scan.scan(["bad.py", "ok.py", "x.dll"])
        kinds = sorted({(f[0], f[2]) for f in found})
        self.assertEqual(kinds, [("bad.py", "a LAN or private IP address"), ("bad.py", "a Steam ID"), ("bad.py", "a home folder path"),
                                 ("bad.py", "decompiled game code"), ("x.dll", "a binary file")])


if __name__ == "__main__":
    unittest.main()

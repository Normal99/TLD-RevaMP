#!/usr/bin/env python3
"""Offline regression: the five retired headless tools refuse, and refuse before they can act.

Every tool is executed as a real subprocess under a trapping interpreter: the modules the old
scripts imported (game, tldenv, bench, mplib, roadkit) and the dangerous calls they used
(subprocess, sockets, file writes, os.system) are all wired to record a violation and fail.  A
passing run therefore proves the refusal happened before any import, quit, start, network or
file work -- with no real side effect, no game and no service.
"""
import ast
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

TOOLS = pathlib.Path(__file__).resolve().parent
RETIRED = ("devserver.py", "leasepersist.py", "servermode.py", "serveradmin.py", "headless.py")

# The modules the retired scripts imported, and the calls they used to make.
FORBIDDEN_IMPORTS = {"game", "tldenv", "bench", "mplib", "roadkit", "launchguard", "seeded"}
FORBIDDEN_CALLS = {"quit_game", "start", "build", "running", "launch", "kill_prefix"}

# sitecustomize.py: installed on PYTHONPATH, so the interpreter imports it before the tool runs.
TRAPPER = r'''
import builtins, io, os, socket, subprocess, sys

LOG = os.environ["TLD_TRAP_LOG"]

def _record(kind, name):
    try:
        fd = os.open(LOG, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
        os.write(fd, (kind + ":" + name + "\n").encode())
        os.close(fd)
    except OSError:
        pass
    raise AssertionError("trapped " + kind + ": " + name)

class _TrapFinder:
    FORBIDDEN = __import__("json").loads(os.environ["TLD_TRAP_MODULES"])

    def find_module(self, fullname, path=None):
        return self.find_spec(fullname, path)

    def find_spec(self, fullname, path=None, target=None):
        root = fullname.split(".")[0]
        if root in self.FORBIDDEN:
            _record("import", fullname)
        return None

sys.meta_path.insert(0, _TrapFinder())

_real_open = builtins.open

def _open(file, mode="r", *args, **kwargs):
    if any(c in mode for c in "wxa+"):
        _record("file", str(file))
    return _real_open(file, mode, *args, **kwargs)

builtins.open = _open

class _TrappedSocket(socket.socket):
    def __init__(self, *args, **kwargs):
        _record("socket", "socket")

socket.socket = _TrappedSocket
socket.create_connection = lambda *a, **k: _record("socket", "create_connection")
socket.getaddrinfo = lambda *a, **k: _record("socket", "getaddrinfo")

def _popen(*args, **kwargs):
    _record("subprocess", str(args[0]) if args else "")

subprocess.Popen = _popen
subprocess.run = _popen
subprocess.call = _popen
subprocess.check_output = _popen
os.system = lambda *a, **k: _record("os", "system")
os.popen = lambda *a, **k: _record("os", "popen")
os.remove = lambda *a, **k: _record("file", "remove")
os.rename = lambda *a, **k: _record("file", "rename")
'''


class TrapEnvironment:
    """A directory on PYTHONPATH whose sitecustomize records every forbidden import and call."""

    def __init__(self, root):
        self.root = pathlib.Path(root)
        self.root.mkdir(parents=True, exist_ok=True)
        (self.root / "sitecustomize.py").write_text(TRAPPER, encoding="utf-8")
        self.log = self.root / "violations.log"

    def env(self):
        environment = dict(os.environ)
        environment["PYTHONPATH"] = str(self.root)
        environment["TLD_TRAP_LOG"] = str(self.log)
        environment["TLD_TRAP_MODULES"] = json.dumps(sorted(FORBIDDEN_IMPORTS))
        # A clean environment: no TLD_ configuration, so nothing can be reached even by accident.
        for key in list(environment):
            if key.startswith("TLD_") and key not in ("TLD_TRAP_LOG", "TLD_TRAP_MODULES"):
                del environment[key]
        return environment

    def violations(self):
        if not self.log.exists():
            return []
        return [line for line in self.log.read_text(encoding="utf-8").splitlines() if line.strip()]


class HeadlessRetirementTests(unittest.TestCase):
    def run_tool(self, name, trap, *args):
        return subprocess.run([sys.executable, str(TOOLS / name), *args], capture_output=True,
                              text=True, env=trap.env(), cwd=str(TOOLS), timeout=60)

    def test_every_retired_tool_refuses_without_acting(self):
        for name in RETIRED:
            with self.subTest(tool=name), tempfile.TemporaryDirectory() as directory:
                trap = TrapEnvironment(directory)
                result = self.run_tool(name, trap)
                self.assertEqual(result.returncode, 2, result.stderr)
                self.assertIn("refused", result.stderr)
                self.assertIn("headless mode is not supported", result.stderr)
                self.assertEqual(trap.violations(), [],
                                 f"{name} reached a forbidden import or call: {trap.violations()}")
                self.assertEqual(result.stdout, "", "the refusal must not print a run banner")

    def test_the_refusal_names_the_supported_guarded_workflow(self):
        for name in RETIRED:
            with self.subTest(tool=name), tempfile.TemporaryDirectory() as directory:
                result = self.run_tool(name, TrapEnvironment(directory))
                self.assertIn("tools/game.py launch", result.stderr)
                self.assertIn("tools/README.md", result.stderr)
                self.assertIn("tools/LAUNCH-COMPOSITION.md", result.stderr)
                self.assertIn("docs/REPRO-FORMAT.md", result.stderr)

    def test_arguments_do_not_reach_the_old_flow(self):
        """--help, --seed, --port, --watch and --no-build must all land on the same refusal."""
        for name in RETIRED:
            for args in (("--help",), ("--seed", "1"), ("--port", "27070"), ("--watch",), ("--no-build",)):
                with self.subTest(tool=name, args=args), tempfile.TemporaryDirectory() as directory:
                    trap = TrapEnvironment(directory)
                    result = self.run_tool(name, trap, *args)
                    self.assertEqual(result.returncode, 2, result.stderr)
                    self.assertIn("refused", result.stderr)
                    self.assertNotIn("usage:", result.stdout)
                    self.assertEqual(trap.violations(), [])

    def test_the_source_imports_and_calls_nothing_dangerous(self):
        for name in RETIRED:
            with self.subTest(tool=name):
                source = (TOOLS / name).read_text(encoding="utf-8")
                tree = ast.parse(source, filename=name)
                for node in ast.walk(tree):
                    if isinstance(node, ast.Import):
                        for alias in node.names:
                            self.assertNotIn(alias.name.split(".")[0], FORBIDDEN_IMPORTS,
                                             f"{name} imports {alias.name}")
                    elif isinstance(node, ast.ImportFrom):
                        root = (node.module or "").split(".")[0]
                        self.assertNotIn(root, FORBIDDEN_IMPORTS, f"{name} imports from {node.module}")
                    elif isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute):
                        self.assertNotIn(node.func.attr, FORBIDDEN_CALLS,
                                         f"{name} calls .{node.func.attr}()")
                # The only module the retired tools may import is the standard library's sys.
                imported = {a.name.split(".")[0] for n in ast.walk(tree) if isinstance(n, ast.Import)
                            for a in n.names}
                imported |= {(n.module or "").split(".")[0] for n in ast.walk(tree)
                             if isinstance(n, ast.ImportFrom)}
                self.assertEqual(imported - {"sys"}, set(),
                                 f"{name} imports more than sys: {imported}")

    def test_the_refusal_is_not_a_silent_success(self):
        for name in RETIRED:
            with self.subTest(tool=name), tempfile.TemporaryDirectory() as directory:
                result = self.run_tool(name, TrapEnvironment(directory))
                self.assertNotEqual(result.returncode, 0)
                self.assertTrue(result.stderr.strip(), "the refusal must be visible on stderr")


if __name__ == "__main__":
    unittest.main()

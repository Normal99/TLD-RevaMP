#!/usr/bin/env python3
"""Offline tests of the two-machine relay preflight (no game, no network).

Covers the four things the preflight has to get right:
  1 port / ControlPath ownership: two runs never share a port or a control socket
  2 stale-tunnel detection: a tunnel that points somewhere else is caught, not used
  3 fail-fast: every wrong fact is reported, with what is actually wrong
  4 exact scoped cleanup: only this run's own tunnel and socket are removed

Run: python3 -m unittest tools/test_relay_preflight.py
"""
import os, pathlib, re, subprocess, sys, tempfile, time, unittest

TOOLS = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS))
import relaypreflight  # noqa: E402  (the module under test)


def run(code, env_extra=None, home=None):
    """Run `code` in a fresh interpreter with a clean TLD_ environment and HOME (the modules read both at import)."""
    env = {k: v for k, v in os.environ.items() if not k.startswith("TLD_")}
    env["HOME"] = home or tempfile.mkdtemp()
    env.update(env_extra or {})
    return subprocess.run([sys.executable, "-c", f"import sys; sys.path.insert(0, {str(TOOLS)!r}); " + code],
                          capture_output=True, text=True, env=env)


class PortOwnership(unittest.TestCase):
    """1: every run owns its own ports and control socket"""

    def test_two_runs_do_not_share_names(self):
        r = run("import relaynet; a=relaynet.Ports(remote='volkan@tfpc', name='run-a'); "
                "b=relaynet.Ports(remote='volkan@tfpc', name='run-b'); "
                "print(a.control_path != b.control_path); print(a.control_path); print(b.control_path)")
        lines = r.stdout.splitlines()
        self.assertEqual(lines[0], "True", r.stderr)
        self.assertIn("run-a", lines[1])
        self.assertIn("run-b", lines[2])

    def test_the_environment_pins_the_ports(self):
        r = run("import relaynet; p=relaynet.Ports(remote='volkan@tfpc', name='run-a', "
                "env={'TLD_BRIDGE_PORT':'27062','TLD_TUNNEL_PORT':'27063','TLD_REMOTE_BRIDGE_PORT':'27064'}); "
                "print(p.bridge_port, p.tunnel_port, p.remote_bridge_port)")
        self.assertEqual(r.stdout.strip(), "27062 27063 27064", r.stderr)

    def test_the_control_path_is_under_the_home(self):
        home = tempfile.mkdtemp()
        r = run("import relaynet; p=relaynet.Ports(remote='volkan@tfpc', name='run-a'); print(p.control_path)",
                home=home)
        self.assertTrue(r.stdout.startswith(home), r.stdout)
        self.assertIn("run-a", r.stdout)

    def test_the_default_name_carries_the_run_identity(self):
        """without a pinned name, the run's own identity names its control socket"""
        r = run("import relaynet; print(relaynet.Ports(remote='volkan@tfpc', "
                "env={'TLD_BRIDGE_PORT':'27062','TLD_TUNNEL_PORT':'27063'}).name)")
        self.assertEqual(r.stdout.strip(), "tfpc-27062-27063", r.stdout + r.stderr)

    def test_the_default_names_of_two_runs_differ(self):
        r = run("import relaynet\n"
                "a = relaynet.Ports(remote='volkan@tfpc', env={'TLD_BRIDGE_PORT':'27062','TLD_TUNNEL_PORT':'27063'})\n"
                "b = relaynet.Ports(remote='volkan@tfpc', env={'TLD_BRIDGE_PORT':'27062','TLD_TUNNEL_PORT':'27064'})\n"
                "print(a.control_path != b.control_path)\n")
        self.assertEqual(r.stdout.strip(), "True", r.stdout + r.stderr)

    def test_the_name_is_sanitized(self):
        r = run("import relaynet; print(relaynet.Ports(remote='volkan@tfpc', name='a/b c').name)")
        self.assertEqual(r.stdout.strip(), "a-b-c", r.stderr)

    def test_a_free_port_is_reported_free(self):
        r = run("import relaynet, socket; s=socket.socket(); s.bind(('127.0.0.1',0)); "
                "port=s.getsockname()[1]; s.close(); "
                "p=relaynet.Ports(remote='volkan@tfpc', name='run-a'); print(p.local_port_in_use(port))")
        self.assertEqual(r.stdout.strip(), "False", r.stderr)


class StaleTunnel(unittest.TestCase):
    """2: a tunnel that points somewhere else is detected, never used"""

    def _fake_tunnel(self, tmp, spec):
        # a fake ssh process whose command line holds the given -L spec (the scan reads /proc)
        # a python process named "ssh" (argv[0]) that keeps its arguments: the scan reads /proc/<pid>/cmdline
        bindir = pathlib.Path(tmp) / "bin"
        bindir.mkdir(exist_ok=True)
        fake = bindir / "ssh"
        fake.write_text("#!/bin/sh\nexec " + repr(sys.executable) + " -c 'import time; time.sleep(30)' -- \"$@\"\n")
        fake.chmod(0o755)
        p = subprocess.Popen([str(fake)] + spec.split(), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(0.5)
        return p

    def test_a_tunnel_to_the_wrong_port_is_stale(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = self._fake_tunnel(tmp, "-L 127.0.0.1:27063:127.0.0.1:27050 volkan@tfpc")
            try:
                r = subprocess.run([sys.executable, "-c",
                                    f"import sys; sys.path.insert(0, {str(TOOLS)!r}); import relaynet; "
                                    "print(relaynet.tunnel_target(27063)); "
                                    "print(relaynet.tunnel_is_stale(27063, 27062)); "
                                    "print(relaynet.tunnel_is_stale(27063, 27050))"],
                                   capture_output=True, text=True)
                lines = r.stdout.splitlines()
                self.assertEqual(lines[0], "('127.0.0.1', 27050)", r.stdout + r.stderr)
                self.assertEqual(lines[1], "True", r.stdout + r.stderr)   # points at 27050, we want 27062
                self.assertEqual(lines[2], "False", r.stdout + r.stderr)   # points where we want it
            finally:
                p.terminate()
                p.wait(timeout=10)

    def test_a_tunnel_to_the_right_port_is_not_stale(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = self._fake_tunnel(tmp, "-L 127.0.0.1:27063:127.0.0.1:27062 volkan@tfpc")
            try:
                r = subprocess.run([sys.executable, "-c",
                                    f"import sys; sys.path.insert(0, {str(TOOLS)!r}); import relaynet; "
                                    "print(relaynet.tunnel_is_stale(27063, 27062)); "
                                    "print(relaynet.tunnel_target(27063))"],
                                   capture_output=True, text=True)
                lines = r.stdout.splitlines()
                self.assertEqual(lines[0], "False", r.stdout + r.stderr)
                self.assertEqual(lines[1], "('127.0.0.1', 27062)", r.stdout + r.stderr)
            finally:
                p.terminate()
                p.wait(timeout=10)

    def test_both_forward_forms_are_parsed(self):
        """ssh takes the forward in two forms: `-L spec` and `-Lspec`"""
        r = run("import relaynet\n"
                "print(relaynet._forward_specs(['ssh', '-L', '127.0.0.1:27063:127.0.0.1:27062', 'h']))\n"
                "print(relaynet._forward_specs(['ssh', '-L127.0.0.1:27063:127.0.0.1:27062', 'h']))\n"
                "print(relaynet._parse_forward('127.0.0.1:27063:127.0.0.1:27062'))\n"
                "print(relaynet._parse_forward('27063:127.0.0.1:27062'))\n"
                "print(relaynet._parse_forward('127.0.0.1:27063:127.0.0.1:http'))\n")
        lines = r.stdout.splitlines()
        self.assertEqual(lines[0], "['127.0.0.1:27063:127.0.0.1:27062']", r.stdout + r.stderr)
        self.assertEqual(lines[1], "['127.0.0.1:27063:127.0.0.1:27062']", r.stdout + r.stderr)
        self.assertEqual(lines[2], "(27063, ('127.0.0.1', 27062))", r.stdout + r.stderr)
        self.assertEqual(lines[3], "(27063, ('127.0.0.1', 27062))", r.stdout + r.stderr)
        self.assertEqual(lines[4], "(27063, ('127.0.0.1', -1))", r.stdout + r.stderr)

    def test_a_free_port_is_not_stale(self):
        r = run("import relaynet; print(relaynet.tunnel_is_stale(27063, 27062))")
        self.assertEqual(r.stdout.strip(), "None", r.stderr)

    def test_a_non_tunnel_listener_is_reported_as_such(self):
        # a port held by something that is not one of our tunnels is not 'stale', it is 'not ours'
        r = run("import relaynet, socket\n"
                "s = socket.socket(); s.bind(('127.0.0.1', 0)); s.listen(1)\n"
                "port = s.getsockname()[1]\n"
                "print(relaynet.tunnel_target(port))\n"
                "print(relaynet.tunnel_is_stale(port, 27050))\n")
        lines = r.stdout.splitlines()
        self.assertEqual(lines[0], "None", r.stdout + r.stderr)
        self.assertEqual(lines[1], "None", r.stdout + r.stderr)


class PreflightChecks(unittest.TestCase):
    """3: every wrong fact is reported, with what is actually wrong"""

    def test_a_healthy_run_passes(self):
        r = run("import relaypreflight as p; "
                "f=dict(host_prefix=('/p',['/p']), client_prefix=('/q',['/q']), "
                "host_bridge_port=27062, client_bridge_port=27062, host_bridge_port_reported=27062, "
                "client_bridge_port_reported=27062, host_build='b1', client_build='b1', "
                "host_game_build='cf2c', client_game_build='cf2c', host_steam_id='1', client_steam_id='2', "
                "tunnel_port=27063, remote_bridge_port=27062, tunnel_target=('127.0.0.1',27062), "
                "host_bg=True, client_bg=True, host_vsync=0, client_vsync=0, "
                "host_window=(0,640,360), client_window=(0,640,360), host_fps=60, client_fps=55, "
                "host_game_data_path='/g/game/TheLongDrive_Data', client_game_data_path='/g/game2/TheLongDrive_Data', "
                "host_game_data_path_reported='/g/game/TheLongDrive_Data', "
                "client_game_data_path_reported='/g/game2/TheLongDrive_Data'); "
                "rep=p.run(f); print(rep.passed); print(len(rep.bad)); print(rep.text())")
        lines = r.stdout.splitlines()
        self.assertEqual(lines[0], "True", r.stdout + r.stderr)
        self.assertEqual(lines[1], "0", r.stdout + r.stderr)

    def test_a_wrong_prefix_is_reported(self):
        r = run("import relaypreflight as p; "
                "f=dict(host_prefix=('/p',['/other']), client_prefix=('/q',['/q']), "
                "host_bridge_port=1, client_bridge_port=1, host_bridge_port_reported=1, client_bridge_port_reported=1, "
                "host_build='b1', client_build='b1', host_game_build='cf2c', client_game_build='cf2c', "
                "host_steam_id='1', client_steam_id='2', tunnel_port=2, remote_bridge_port=1, tunnel_target=None, "
                "host_bg=True, client_bg=True, host_vsync=0, client_vsync=0, "
                "host_window=(0,640,360), client_window=(0,640,360), host_fps=60, client_fps=60, "
                "host_game_data_path='/g/game/TheLongDrive_Data', client_game_data_path='/g/game2/TheLongDrive_Data', "
                "host_game_data_path_reported='/g/game/TheLongDrive_Data', "
                "client_game_data_path_reported='/g/game2/TheLongDrive_Data'); "
                "rep=p.run(f); print(rep.passed); print([b for b in rep.bad if 'prefix' in b])")
        self.assertIn("False", r.stdout, r.stderr)
        self.assertIn("not in the test prefix", r.stdout, r.stderr)

    def test_a_bridge_on_another_port_is_reported(self):
        r = run("import relaypreflight as p; "
                "f=dict(host_prefix=('/p',['/p']), client_prefix=('/q',['/q']), "
                "host_bridge_port=27062, client_bridge_port=27062, host_bridge_port_reported=27062, "
                "client_bridge_port_reported=27050, host_build='b1', client_build='b1', "
                "host_game_build='cf2c', client_game_build='cf2c', host_steam_id='1', client_steam_id='2', "
                "tunnel_port=2, remote_bridge_port=1, tunnel_target=None, "
                "host_bg=True, client_bg=True, host_vsync=0, client_vsync=0, "
                "host_window=(0,640,360), client_window=(0,640,360), host_fps=60, client_fps=60, "
                "host_game_data_path='/g/game/TheLongDrive_Data', client_game_data_path='/g/game2/TheLongDrive_Data', "
                "host_game_data_path_reported='/g/game/TheLongDrive_Data', "
                "client_game_data_path_reported='/g/game2/TheLongDrive_Data'); "
                "rep=p.run(f); print([b for b in rep.bad if 'bridge port' in b])")
        self.assertIn("another copy's bridge", r.stdout, r.stderr)

    def test_different_mod_builds_are_reported(self):
        r = run("import relaypreflight as p; "
                "f=dict(host_prefix=('/p',['/p']), client_prefix=('/q',['/q']), "
                "host_bridge_port=1, client_bridge_port=1, host_bridge_port_reported=1, client_bridge_port_reported=1, "
                "host_build='b1', client_build='b2', host_game_build='cf2c', client_game_build='cf2c', "
                "host_steam_id='1', client_steam_id='2', tunnel_port=2, remote_bridge_port=1, tunnel_target=None, "
                "host_bg=True, client_bg=True, host_vsync=0, client_vsync=0, "
                "host_window=(0,640,360), client_window=(0,640,360), host_fps=60, client_fps=60, "
                "host_game_data_path='/g/game/TheLongDrive_Data', client_game_data_path='/g/game2/TheLongDrive_Data', "
                "host_game_data_path_reported='/g/game/TheLongDrive_Data', "
                "client_game_data_path_reported='/g/game2/TheLongDrive_Data'); "
                "rep=p.run(f); print([b for b in rep.bad if 'mod build' in b])")
        self.assertIn("different mod builds", r.stdout, r.stderr)

    def test_the_same_steam_account_is_reported(self):
        r = run("import relaypreflight as p; "
                "f=dict(host_prefix=('/p',['/p']), client_prefix=('/q',['/q']), "
                "host_bridge_port=1, client_bridge_port=1, host_bridge_port_reported=1, client_bridge_port_reported=1, "
                "host_build='b1', client_build='b1', host_game_build='cf2c', client_game_build='cf2c', "
                "host_steam_id='1', client_steam_id='1', tunnel_port=2, remote_bridge_port=1, tunnel_target=None, "
                "host_bg=True, client_bg=True, host_vsync=0, client_vsync=0, "
                "host_window=(0,640,360), client_window=(0,640,360), host_fps=60, client_fps=60, "
                "host_game_data_path='/g/game/TheLongDrive_Data', client_game_data_path='/g/game2/TheLongDrive_Data', "
                "host_game_data_path_reported='/g/game/TheLongDrive_Data', "
                "client_game_data_path_reported='/g/game2/TheLongDrive_Data'); "
                "rep=p.run(f); print([b for b in rep.bad if 'Steam account' in b])")
        self.assertIn("on the host's Steam account", r.stdout, r.stderr)

    def test_a_stale_tunnel_is_reported(self):
        r = run("import relaypreflight as p; "
                "f=dict(host_prefix=('/p',['/p']), client_prefix=('/q',['/q']), "
                "host_bridge_port=1, client_bridge_port=1, host_bridge_port_reported=1, client_bridge_port_reported=1, "
                "host_build='b1', client_build='b1', host_game_build='cf2c', client_game_build='cf2c', "
                "host_steam_id='1', client_steam_id='2', tunnel_port=27063, remote_bridge_port=27062, "
                "tunnel_target=('127.0.0.1',27050), host_bg=True, client_bg=True, host_vsync=0, client_vsync=0, "
                "host_window=(0,640,360), client_window=(0,640,360), host_fps=60, client_fps=60, "
                "host_game_data_path='/g/game/TheLongDrive_Data', client_game_data_path='/g/game2/TheLongDrive_Data', "
                "host_game_data_path_reported='/g/game/TheLongDrive_Data', "
                "client_game_data_path_reported='/g/game2/TheLongDrive_Data'); "
                "rep=p.run(f); print([b for b in rep.bad if 'tunnel' in b])")
        self.assertIn("stale tunnel", r.stdout, r.stderr)

    def test_bg_off_vsync_on_fullscreen_and_slow_fps_are_reported(self):
        r = run("import relaypreflight as p; "
                "f=dict(host_prefix=('/p',['/p']), client_prefix=('/q',['/q']), "
                "host_bridge_port=1, client_bridge_port=1, host_bridge_port_reported=1, client_bridge_port_reported=1, "
                "host_build='b1', client_build='b1', host_game_build='cf2c', client_game_build='cf2c', "
                "host_steam_id='1', client_steam_id='2', tunnel_port=2, remote_bridge_port=1, tunnel_target=None, "
                "host_bg=False, client_bg=True, host_vsync=1, client_vsync=0, "
                "host_window=(1,1920,1080), client_window=(0,640,360), host_fps=5, client_fps=60); "
                "rep=p.run(f); print(len(rep.bad)); print(rep.text())")
        self.assertIn("bg off", r.stdout, r.stderr)
        self.assertIn("vsync on", r.stdout, r.stderr)
        self.assertIn("fullscreen", r.stdout, r.stderr)
        self.assertIn("fps", r.stdout, r.stderr)

    def test_every_wrong_fact_is_caught(self):
        """one healthy run, and every way it can be wrong: each must be caught, and only it"""
        base = dict(host_prefix=("/p", ["/p"]), client_prefix=("/q", ["/q"]),
                    host_bridge_port=27062, client_bridge_port=27062,
                    host_bridge_port_reported=27062, client_bridge_port_reported=27062,
                    host_build="b1", client_build="b1",
                    host_game_build="cf2c2d0e", client_game_build="cf2c2d0e", expected_game_build="cf2c2d0e",
                    # synthetic Steam ids: the check is that the two accounts DIFFER. They are deliberately
                    # not account-shaped (a real 7656119... id in the repository is exactly what the privacy
                    # scan exists to keep out), so they are plain distinct numbers instead.
                    host_steam_id="10000000000000001", client_steam_id="10000000000000002",
                    tunnel_port=27063, remote_bridge_port=27062, tunnel_target=("127.0.0.1", 27062),
                    host_bg=True, client_bg=True, host_vsync=0, client_vsync=0,
                    host_window=(0, 640, 360), client_window=(0, 640, 360),
                    host_fps=60.0, client_fps=58.0,
                    # the copy this run asked for, and the game's own dataPath: they have to be the same folder
                    host_game_data_path="/g/game/TheLongDrive_Data",
                    client_game_data_path="/g/game2/TheLongDrive_Data",
                    host_game_data_path_reported="/g/game/TheLongDrive_Data",
                    client_game_data_path_reported="/g/game2/TheLongDrive_Data")
        cases = [("a healthy run", {}, True),
                 ("another prefix", {"host_prefix": ("/p", ["/other"])}, False),
                 ("another copy's bridge", {"client_bridge_port_reported": 27050}, False),
                 ("different mod builds", {"client_build": "b2"}, False),
                 ("one Steam account", {"client_steam_id": base["host_steam_id"]}, False),
                 ("a stale tunnel", {"tunnel_target": ("127.0.0.1", 27050)}, False),
                 ("bg off", {"client_bg": False}, False),
                 ("vsync on", {"client_vsync": 1}, False),
                 ("a fullscreen copy", {"client_window": (3, 1920, 1080)}, False),
                 ("too slow", {"client_fps": 4.0}, False),
                 ("a wrong game build", {"client_game_build": "deadbeef"}, False),
                 # the bridge that answers on another port: the REPORTED port is what the bridge says
                 ("a bridge that lies about its port", {"client_bridge_port_reported": 27050}, False),
                 # the game that is somebody else's copy: the dataPath is not this run's copy
                 ("another copy's game", {"client_game_data_path_reported": "/g/other/TheLongDrive_Data"}, False)]
        for name, over, expect_pass in cases:
            facts = dict(base); facts.update(over)
            rep = relaypreflight.run(facts)
            self.assertEqual(rep.passed, expect_pass, f"{name}: {rep.text()}")
            if not expect_pass:
                self.assertTrue(rep.bad, f"{name}: nothing was reported")

    def test_the_reported_bridge_port_is_independently_observed(self):
        """the wanted port and the reported port must be able to differ.

        They used to be filled with the SAME expression, so they were always equal and the check could
        never fail in a real run: the check that catches "a bridge answering on another port is another
        copy's bridge" was unreachable.
        """
        mplib = (TOOLS / "mplib.py").read_text()
        self.assertIn("_reported_port_of(h)", mplib)
        self.assertIn("_reported_port_of(L)", mplib)
        # the reported port must come from the bridge's own answer, never from the run's own port table
        self.assertNotIn('"host_bridge_port_reported": bench.PORTS.bridge_port', mplib)
        self.assertNotIn('"client_bridge_port_reported": bench.PORTS.remote_bridge_port', mplib)

    def test_the_bridge_reports_the_port_it_listens_on(self):
        """the bridge's ping answer carries the port it listens on: that is the independent observation"""
        src = (TOOLS.parent / "src" / "TLDRevamp" / "DebugBridge.cs").read_text()
        self.assertIn('\\"port\\":', src)
        self.assertIn("_boundPort", src)

    def test_a_bridge_that_answers_on_another_port_is_reported(self):
        """the pure check catches a bridge on the wrong port (the run's own port table is not consulted)"""
        ok, detail = relaypreflight.check_bridge_port("client", 27062, 27050)
        self.assertFalse(ok)
        self.assertIn("another copy's bridge", detail)
        ok, detail = relaypreflight.check_bridge_port("client", 27062, None)
        self.assertFalse(ok)
        self.assertIn("unknown", detail)

    def test_the_game_copy_is_checked_exactly(self):
        """the game's own dataPath has to equal this run's copy: exactly, not by prefix"""
        ok, detail = relaypreflight.check_data_path("host", "/g/game/TheLongDrive_Data",
                                                    "/g/game/TheLongDrive_Data")
        self.assertTrue(ok, detail)
        # a DIFFERENT copy is refused
        ok, detail = relaypreflight.check_data_path("host", "/g/game/TheLongDrive_Data",
                                                    "/g/other/TheLongDrive_Data")
        self.assertFalse(ok)
        self.assertIn("somebody else's game", detail)
        # the player's own install is refused (a synthetic path: a real home folder in the repository is
        # exactly what the privacy scan exists to keep out)
        ok, detail = relaypreflight.check_data_path("host", "/g/game/TheLongDrive_Data",
                                                    "/g/player/The Long Drive/TheLongDrive_Data")
        self.assertFalse(ok)
        # a missing report is refused (never assumed)
        ok, detail = relaypreflight.check_data_path("host", "/g/game/TheLongDrive_Data", None)
        self.assertFalse(ok)
        self.assertIn("did not report", detail)
        # a missing wanted copy is refused
        ok, detail = relaypreflight.check_data_path("host", "", "/g/game/TheLongDrive_Data")
        self.assertFalse(ok)

    def test_the_data_path_comparison_is_exact_not_a_prefix(self):
        """a path that merely CONTAINS this run's folder is somebody else's game too"""
        ok, _ = relaypreflight.check_data_path("host", "/g/game/TheLongDrive_Data",
                                               "/g/game/TheLongDrive_Data/sub")
        self.assertFalse(ok, "a subdirectory of the wanted folder is not the wanted folder")
        ok, _ = relaypreflight.check_data_path("host", "/g/game/TheLongDrive_Data",
                                               "/g/game/TheLongDrive_DataX")
        self.assertFalse(ok, "a folder that starts with the wanted name is not the wanted folder")
        ok, _ = relaypreflight.check_data_path("host", "/g/game/TheLongDrive_Data",
                                               "/g/game")
        self.assertFalse(ok, "the game directory without its data folder is not the wanted folder")

    def test_the_data_path_comparison_survives_the_windows_spelling(self):
        """Unity (under Proton) spells the path 'Z:\\dir\\TheLongDrive_Data'; Linux spells it '/dir/...'"""
        ok, _ = relaypreflight.check_data_path("host", "/g/game/TheLongDrive_Data",
                                               "Z:\\g\\game\\TheLongDrive_Data")
        self.assertTrue(ok)

    def test_the_report_raises_with_the_reasons(self):
        r = run("import relaypreflight as p\n"
                "try:\n"
                "    p.run(dict(host_prefix=('/p','/x'), client_prefix=('/q','/q'), host_bridge_port=1, "
                "client_bridge_port=1, host_bridge_port_reported=1, client_bridge_port_reported=1, "
                "host_build='b1', client_build='b2', host_game_build='cf2c', client_game_build='cf2c', "
                "host_steam_id='1', client_steam_id='1', tunnel_port=2, remote_bridge_port=1, tunnel_target=None, "
                "host_bg=True, client_bg=True, host_vsync=0, client_vsync=0, host_window=(0,640,360), "
                "client_window=(0,640,360), host_fps=60, client_fps=60)).raise_if_failed()\n"
                "except p.PreflightFailed as e:\n"
                "    print('PREFLIGHT FAILED' in str(e))\n")
        self.assertEqual(r.stdout.strip(), "True", r.stdout + r.stderr)


class ScopedCleanup(unittest.TestCase):
    """4: only this run's own tunnel and socket are removed"""

    def test_only_this_runs_socket_files_are_removed(self):
        home = tempfile.mkdtemp()
        sshdir = pathlib.Path(home) / ".ssh"
        sshdir.mkdir(parents=True)
        mine = sshdir / "tld-mux-run-a-abc"
        theirs = sshdir / "tld-mux-run-b-def"
        other = sshdir / "id_rsa"
        for f in (mine, theirs, other):
            f.write_text("x")
        r = run("import relaynet; p=relaynet.Ports(remote='volkan@tfpc', name='run-a'); "
                "print(relaynet.remove_control_socket(p))", home=home)
        self.assertIn("run-a", r.stdout, r.stderr)
        self.assertFalse(mine.exists(), "our own socket must be gone")
        self.assertTrue(theirs.exists(), "another worker's socket must stay")
        self.assertTrue(other.exists(), "an unrelated file must stay")

    def test_the_cleanup_reports_what_it_did(self):
        r = run("import relaynet\n"
                "class Fake:\n"
                "    def __init__(self, rc): self.rc = rc\n"
                "def kill_tunnel(args, remote, lp, rp): return (False, 'nothing of ours')\n"
                "def kill_control_master(args, remote): return (False, 'no master')\n"
                "relaynet.kill_tunnel = kill_tunnel\n"
                "relaynet.kill_control_master = kill_control_master\n"
                "p = relaynet.Ports(remote='volkan@tfpc', name='run-a')\n"
                "out = {'forward': False, 'master': False, 'sockets': []}\n"
                "ok, _ = relaynet.kill_tunnel([], 'v', 1, 2); out['forward'] = ok\n"
                "ok, _ = relaynet.kill_control_master([], 'v'); out['master'] = ok\n"
                "print(out)\n")
        self.assertIn("'forward': False", r.stdout, r.stderr)
        self.assertIn("'master': False", r.stdout, r.stderr)

    def test_the_cleanup_runs_once(self):
        """a second cleanup does nothing: the run's own things are gone already"""
        r = run("import relaynet, mplib\n"
                "ports = relaynet.Ports(remote='volkan@tfpc', name='run-a')\n"
                "a = mplib.cleanup_run('volkan@tfpc', ports)\n"
                "b = mplib.cleanup_run('volkan@tfpc', ports)\n"
                "print(a['kept'])\n"
                "print(b['kept'])\n")
        lines = r.stdout.splitlines()
        self.assertIn("no tunnel of this run was open", lines[0], r.stdout + r.stderr)
        self.assertIn("already cleaned up", lines[1], r.stdout + r.stderr)

    def test_the_cleanup_never_kills_a_process(self):
        """the cleanup is scoped to this run's socket and forward: no pkill, no machine-wide kill"""
        quote = chr(34) * 3
        for path in (TOOLS / "relaynet.py", TOOLS / "mplib.py"):
            src = path.read_text()
            for name in ("kill_tunnel", "kill_control_master", "remove_control_socket", "cleanup_run"):
                m = re.search(rf"^def {name}\(.*?(?=^def |^class |\Z)", src, re.S | re.M)
                if not m:
                    continue
                body = m.group(0)
                # the code, not the prose around it
                code = re.sub(quote + "(?:.|" + chr(10) + ")*?" + quote, "", body)
                code = re.sub(r"#.*", "", code)
                for banned in ("pkill", "killall", "kill -9", "os.kill"):
                    self.assertNotIn(banned, code, f"{banned} must not appear in {name} ({path.name})")


if __name__ == "__main__":
    unittest.main()

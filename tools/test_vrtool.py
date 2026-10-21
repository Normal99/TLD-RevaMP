#!/usr/bin/env python3
"""Offline tests of the VR tool (tools/quest3vr.py): the bridge conversation, the frame keys and the promise that
the XR session is always stopped. No game, no headset, no ADB: a stub bridge answers what the real one answers.

  python3 -m unittest tools.test_vrtool -v
"""
import contextlib, io, json, os, pathlib, socket, subprocess, sys, tempfile, threading, unittest

TOOLS = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS))


class StubBridge:
    """A bridge that answers like the game's: one JSON line per command. It records what it was asked."""

    def __init__(self, running=True, fail_first_start=False):
        self.commands = []
        self.running = running
        self.fail_first_start = fail_first_start
        self.starts = 0
        self.stops = 0
        self.recenters = 0
        # the runtime's tracking-origin mode: a configuration flag, never a pose
        self.origin_mode = "device" if running else ""
        # the lifecycle's own state: idle / running / failed. The bridge reports THIS, not a guess.
        self.state = "running" if running else "idle"
        self.failed = False
        self.sock = socket.socket()
        self.sock.bind(("127.0.0.1", 0))
        self.sock.listen(4)
        self.port = self.sock.getsockname()[1]
        self.thread = threading.Thread(target=self._serve, daemon=True)
        self.thread.start()

    def _serve(self):
        while True:
            try:
                client, _ = self.sock.accept()
            except OSError:
                return
            threading.Thread(target=self._session, args=(client,), daemon=True).start()

    def _session(self, client):
        with client, client.makefile("rwb") as stream:
            for raw in stream:
                text = raw.decode("utf-8").strip()
                if not text:
                    continue
                self.commands.append(text)
                reply = self.answer(text)
                stream.write((json.dumps(reply) + "\n").encode("utf-8"))
                stream.flush()

    def answer(self, text):
        if text == "openxr":
            # the real state carries every field the live gate reads: the loader, the manager, the
            # subsystems, the profiles, the controllers and the counters.
            return {"running": self.running, "failed": self.failed, "state": self.state,
                    "controllers": [{"name": "stub", "valid": True, "left": True, "right": True}],
                    "error": "the loader refused" if self.failed else "",
                    "failure": "initialize" if self.failed else "none",
                    "enabled": True, "loader": "openxr" if self.running else "none",
                    "loaderName": "UnityEngine.XR.OpenXR.OpenXRLoader", "loaderUsable": True,
                    "managerActive": self.running, "managerInitialized": self.running,
                    "featuresConfigured": self.running, "runtime": "WiVRn", "api": "1.0",
                    "originMode": self.origin_mode,
                    "originSupported": "device|floor",
                    "display": self.running, "input": self.running,
                    "recenter": self.recenters, "starts": self.starts, "stops": self.stops,
                    "failures": 0, "retries": 0}
        if text == "openxr start":
            self.starts += 1
            if self.fail_first_start and self.starts == 1:
                self.running = False; self.failed = True; self.state = "failed"
                return self.answer("openxr")
            self.running = True; self.failed = False; self.state = "running"
            return self.answer("openxr")
        if text == "openxr retry":
            self.running = True; self.failed = False; self.state = "running"
            return self.answer("openxr")
        if text == "openxr stop":
            self.running = False; self.failed = False; self.state = "idle"
            self.stops += 1
            return self.answer("openxr")
        if text == "openxr recenter":
            self.recenters += 1
            return self.answer("openxr")
        if text == "openxr recenter origin":
            # the explicit origin request: it moves the runtime's tracking origin, and it is the ONLY
            # path that does (the plain recenter above leaves the mode alone).
            self.recenters += 1
            self.origin_mode = "floor"
            return self.answer("openxr")
        if text == "stats":
            # the real key names of Telemetry.FrameStatsJson
            return {"fps": 30, "p50ms": 12.5, "p99ms": 20.0, "p999ms": 25.0, "hitches50ms": 1}
        if text == "mp status":
            return {"server": None, "client": None}
        if text == "errors":
            return {"errors": 0, "exceptions": 0}
        return {"error": "unknown command, try help"}

    def close(self):
        try:
            self.sock.close()
        except OSError:
            pass


class VrTool(unittest.TestCase):
    def setUp(self):
        import quest3vr
        self.tool = quest3vr
        self.bridge = StubBridge()
        self.addCleanup(self.bridge.close)

    def test_state_read_needs_no_developer_access(self):
        """`openxr` on its own is a read: the tool must be able to ask without a mutation."""
        reply = self.tool.bridge(self.bridge.port, "openxr")
        self.assertTrue(reply["running"])
        self.assertIn("openxr", self.bridge.commands)

    def test_a_refused_bridge_is_an_error(self):
        with self.assertRaises(RuntimeError):
            self.tool.bridge(self.bridge.port, "nonsense")

    def test_the_frame_keys_are_the_real_ones(self):
        """The tool must read the keys FrameStatsJson really has, or the samples are empty."""
        stats = self.tool.bridge(self.bridge.port, "stats")
        keys = [k for k in ("fps", "p50ms", "p99ms", "p999ms", "hitches50ms") if k in stats]
        self.assertEqual(sorted(keys), sorted(["fps", "hitches50ms", "p50ms", "p99ms", "p999ms"]))
        # the keys the first wave asked for do not exist
        for old in ("p50", "p95", "p99", "hitches"):
            self.assertNotIn(old, stats)

    def test_a_failed_start_is_retried_explicitly(self):
        """A start that failed is retried only when it is asked for: the tool asks."""
        self.bridge.fail_first_start = True
        args = self.tool.argparse.Namespace(port=self.bridge.port, seconds=1, sample=0.2, out=str(pathlib.Path(tempfile.mkdtemp()) / "out.json"))
        # the stub's first start fails, the retry works: the run must go through, not raise
        self.tool.run_session(args)
        self.assertIn("openxr retry", self.bridge.commands)
        self.assertIn("openxr start", self.bridge.commands)

    def test_the_session_is_always_stopped(self):
        """Whatever happens during the run, the XR session is stopped: a leftover session is a live game state."""
        out = pathlib.Path(tempfile.mkdtemp()) / "out.json"
        args = self.tool.argparse.Namespace(port=self.bridge.port, seconds=1, sample=0.2, out=str(out))
        self.tool.run_session(args)
        self.assertIn("openxr stop", self.bridge.commands)
        self.assertLess(self.bridge.commands.index("openxr stop"), len(self.bridge.commands) - 1 + 1)

    def test_the_session_is_stopped_even_when_the_probe_fails(self):
        """A failing health probe must not leave the session running."""
        class Broken(StubBridge):
            def answer(self, text):
                if text == "mp status":
                    return {"error": "not hosting"}
                return super().answer(text)
        broken = Broken()
        self.addCleanup(broken.close)
        out = pathlib.Path(tempfile.mkdtemp()) / "out.json"
        args = self.tool.argparse.Namespace(port=broken.port, seconds=2, sample=0.2, out=str(out))
        with self.assertRaises(RuntimeError):
            self.tool.run_session(args)
        self.assertIn("openxr stop", broken.commands)

    def test_the_status_is_read_only(self):
        """`status` must never mutate the session: it sends only the pure read `openxr`."""
        args = self.tool.argparse.Namespace(port=self.bridge.port)
        out = pathlib.Path(tempfile.mkdtemp()) / "status.json"
        with contextlib.redirect_stdout(io.StringIO()) as buf:
            rc = self.tool.status(args)
        self.assertEqual(rc, 0)
        self.assertEqual(self.bridge.commands, ["openxr"], "status sent something other than the pure read")
        report = json.loads(buf.getvalue())
        self.assertIn("setup", report)
        self.assertIn("bridge", report)
        self.assertEqual(report["bridge"]["state"], "running")
        self.assertEqual(report["bridge"]["running"], True)
        self.assertEqual(report["bridge"]["controllers"]["count"], 1)
        self.assertEqual(report["bridge"]["controllers"]["valid"], 1)

    def test_the_status_reports_the_tracking_origin(self):
        """`status` reports the runtime's tracking-origin MODE: the hardware check needs it.

        It is a configuration flag (device, floor, a tracking reference), never a pose: the bridge reports
        no coordinate, so nothing about where the player is leaves the machine.
        """
        args = self.tool.argparse.Namespace(port=self.bridge.port)
        with contextlib.redirect_stdout(io.StringIO()) as buf:
            rc = self.tool.status(args)
        self.assertEqual(rc, 0)
        report = json.loads(buf.getvalue())
        self.assertEqual(report["bridge"]["originMode"], "device")
        self.assertIn("floor", report["bridge"]["originSupported"])
        # the origin is a mode, not a pose: no coordinate leaves the machine
        blob = json.dumps(report).lower()
        for forbidden in ("position", "rotation", "headposition", "worldposition"):
            self.assertNotIn(forbidden, blob, "the status leaked a " + forbidden)

    def test_the_status_reports_a_dead_bridge(self):
        """A bridge that does not answer is an error, not a crash."""
        args = self.tool.argparse.Namespace(port=1)   # nothing listens there
        with contextlib.redirect_stdout(io.StringIO()) as buf:
            rc = self.tool.status(args)
        self.assertEqual(rc, 1)
        report = json.loads(buf.getvalue())
        self.assertIsNotNone(report["error"])

    def test_the_status_counts_an_invalid_controller(self):
        """A controller the runtime cannot use is counted as invalid: that is the "did not bind" shape."""
        class NoControllers(StubBridge):
            def answer(self, text):
                if text == "openxr":
                    return {"running": True, "failed": False, "state": "running",
                            "controllers": [{"name": "Quest 3 Controller", "valid": False, "left": True, "right": False}]}
                return super().answer(text)
        stub = NoControllers()
        self.addCleanup(stub.close)
        args = self.tool.argparse.Namespace(port=stub.port)
        with contextlib.redirect_stdout(io.StringIO()) as buf:
            self.tool.status(args)
        report = json.loads(buf.getvalue())
        self.assertEqual(report["bridge"]["controllers"]["count"], 1)
        self.assertEqual(report["bridge"]["controllers"]["valid"], 0)
        self.assertEqual(report["bridge"]["controllers"]["invalid"], 1)

    def test_the_probe_finds_the_installed_and_the_active_runtime(self):
        """The probe must say which runtime is INSTALLED and which one is ACTIVE.

        The Unity OpenXR loader finds its runtime through $XR_RUNTIME_JSON or the active-runtime file
        (the WiVRn server points it at ~/.local/share/openxr/1/active_runtime when a headset connects).
        Only the INSTALLED list is not enough: a game that finds no active runtime starts a session with
        no runtime, which is exactly the "the headset is on but nothing happens" failure.
        """
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            (root / "openxr" / "1").mkdir(parents=True)
            manifest = root / "openxr" / "1" / "openxr_wivrn.json"
            manifest.write_text(json.dumps({"runtime": {"name": "WiVRn", "library_path": "libopenxr_wivrn.so"}}))
            active = root / "openxr" / "1" / "active_runtime"
            active.write_text(str(manifest))
            old_data = os.environ.get("XDG_DATA_HOME")
            old_cfg = os.environ.get("XDG_CONFIG_HOME")
            old_json = os.environ.pop("XR_RUNTIME_JSON", None)
            os.environ["XDG_DATA_HOME"] = str(root)
            os.environ["XDG_CONFIG_HOME"] = str(root / "cfg")
            try:
                manifests, found = self.tool.openxr_runtimes()
            finally:
                if old_data is None: os.environ.pop("XDG_DATA_HOME", None)
                else: os.environ["XDG_DATA_HOME"] = old_data
                if old_cfg is None: os.environ.pop("XDG_CONFIG_HOME", None)
                else: os.environ["XDG_CONFIG_HOME"] = old_cfg
                if old_json is not None: os.environ["XR_RUNTIME_JSON"] = old_json
            self.assertTrue(any(m["name"] == "WiVRn" for m in manifests), "the installed runtime was not found")
            self.assertIsNotNone(found, "the active runtime was not found")
            self.assertEqual(found["source"], "XDG_DATA_HOME/openxr/1/active_runtime")
            self.assertTrue(found["exists"])

    def test_the_probe_reports_no_active_runtime_when_there_is_none(self):
        """With no active runtime, the probe says so instead of guessing."""
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            old_data = os.environ.get("XDG_DATA_HOME")
            old_cfg = os.environ.get("XDG_CONFIG_HOME")
            old_json = os.environ.pop("XR_RUNTIME_JSON", None)
            os.environ["XDG_DATA_HOME"] = str(root)
            os.environ["XDG_CONFIG_HOME"] = str(root / "cfg")
            try:
                _, found = self.tool.openxr_runtimes()
            finally:
                if old_data is None: os.environ.pop("XDG_DATA_HOME", None)
                else: os.environ["XDG_DATA_HOME"] = old_data
                if old_cfg is None: os.environ.pop("XDG_CONFIG_HOME", None)
                else: os.environ["XDG_CONFIG_HOME"] = old_cfg
                if old_json is not None: os.environ["XR_RUNTIME_JSON"] = old_json
            self.assertIsNone(found, "an active runtime was reported when there is none")

    def test_the_probe_reports_the_active_runtime_from_the_environment(self):
        """$XR_RUNTIME_JSON is the strongest signal: it is what the loader uses first."""
        with tempfile.TemporaryDirectory() as tmp:
            manifest = pathlib.Path(tmp) / "openxr_wivrn.json"
            manifest.write_text(json.dumps({"runtime": {"name": "WiVRn"}}))
            old_json = os.environ.get("XR_RUNTIME_JSON")
            os.environ["XR_RUNTIME_JSON"] = str(manifest)
            try:
                _, found = self.tool.openxr_runtimes()
            finally:
                if old_json is None: os.environ.pop("XR_RUNTIME_JSON", None)
                else: os.environ["XR_RUNTIME_JSON"] = old_json
            self.assertIsNotNone(found)
            self.assertEqual(found["source"], "XR_RUNTIME_JSON")
            self.assertTrue(found["exists"])

    def test_the_live_gate_passes_against_a_faithful_bridge(self):
        """The live gate must hold against a bridge that behaves like the real one.

        The gate is the script the coordinator runs on the headset. If it cannot pass against a faithful
        stub, it will fail against the real bridge for the wrong reason.
        """
        import quest3vr_live
        stub = StubBridge()
        self.addCleanup(stub.close)
        out = pathlib.Path(tempfile.mkdtemp()) / "gate.txt"
        with contextlib.redirect_stdout(io.StringIO()) as buf:
            rc = quest3vr_live.gate_run(stub.port)
        text = buf.getvalue()
        out.write_text(text, encoding="utf-8")
        self.assertEqual(rc, 0, "the live gate failed against a faithful bridge:\n" + text)
        self.assertIn("assertions held", text)
        # the gate drove the bridge through the whole session
        for command in ("openxr", "openxr start", "openxr recenter", "openxr stop"):
            self.assertIn(command, stub.commands, "the gate never sent " + command)

    def test_the_live_gate_fails_when_the_bridge_lies(self):
        """A bridge that reports a session it does not have must fail the gate."""
        import quest3vr_live
        class Lying(StubBridge):
            def answer(self, text):
                if text == "openxr stop":
                    # the real bridge reports "idle" here; a bridge that says "failed" after a stop is
                    # exactly the state desync the F3 fix removed, and the gate must catch it.
                    return {"running": False, "failed": True, "state": "failed", "stops": 0}
                return super().answer(text)
        lying = Lying()
        self.addCleanup(lying.close)
        with contextlib.redirect_stdout(io.StringIO()) as buf:
            rc = quest3vr_live.gate_run(lying.port)
        self.assertNotEqual(rc, 0, "the gate passed a bridge that lied about the stop")
        self.assertIn("FAIL", buf.getvalue())

    def test_the_live_gate_catches_a_moved_tracking_origin(self):
        """A plain recenter must not move the runtime's tracking origin.

        The gate can only check this because the state reports the origin MODE. A bridge that moves the
        origin on a plain recenter (the old double-recenter defect) must fail the gate.
        """
        import quest3vr_live
        class MovesOrigin(StubBridge):
            def answer(self, text):
                reply = super().answer(text)
                if text == "openxr recenter":
                    # the origin moved even though nobody asked for it
                    reply = dict(reply)
                    reply["originMode"] = "floor"
                return reply
        mover = MovesOrigin()
        self.addCleanup(mover.close)
        with contextlib.redirect_stdout(io.StringIO()) as buf:
            rc = quest3vr_live.gate_run(mover.port)
        self.assertNotEqual(rc, 0, "the gate passed a bridge that moved the origin on a plain recenter")
        self.assertIn("did not move the tracking origin mode", buf.getvalue())

    def test_the_live_gate_checks_the_origin_when_asked(self):
        """The optional origin step must run, and it must show the origin moving.

        The plain recenter leaves the runtime's tracking origin alone; the explicit `recenter origin` is
        the only path that moves it. The gate's origin step proves both halves.
        """
        import quest3vr_live
        stub = StubBridge()
        self.addCleanup(stub.close)
        with contextlib.redirect_stdout(io.StringIO()) as buf:
            rc = quest3vr_live.gate_run(stub.port, check_origin=True)
        text = buf.getvalue()
        self.assertEqual(rc, 0, "the gate's origin step failed against a faithful bridge:\n" + text)
        self.assertIn("openxr recenter origin", stub.commands,
                      "the gate never sent the explicit origin request")
        self.assertIn("the origin mode was device before the explicit request", text)
        self.assertIn("the runtime reports an origin mode after the explicit request", text)

    def test_the_tool_runs_as_a_script(self):
        """The tool's own entry point works (the help at least: no import error, no argument error)."""
        r = subprocess.run([sys.executable, str(TOOLS / "quest3vr.py"), "--help"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("quest3vr", r.stdout.lower())


class WiVRnPackageDetection(unittest.TestCase):
    """Which WiVRn package the probe reports: the exact ids, or nothing.

    The headset carries the Play Store build `org.meumeu.wivrn` and the GitHub build
    `org.meumeu.wivrn.github`. Nothing else is WiVRn. A `pm path` answer that belongs to another
    app, a failed query or an error answer must never be reported as an install: the probe says
    "absent" or "unknown" instead. These tests drive the probe with a stubbed ADB, so no device,
    no daemon and no real package manager are involved.
    """

    def setUp(self):
        import quest3vr
        self.tool = quest3vr
        self.original_run = quest3vr.run
        self.original_device = quest3vr.adb_device
        quest3vr.adb_device = lambda: {"serial": "stub-serial", "model": "Quest 3"}
        self.addCleanup(self._restore)

    def _restore(self):
        self.tool.run = self.original_run
        self.tool.adb_device = self.original_device

    def stub_adb(self, answers):
        """answers: {package id: {"ok": bool, "stdout": str}} for `pm path <id>`."""
        asked = []

        def fake_run(args, timeout=30):
            if args[:1] == ["adb"] and "pm" in args and "path" in args:
                package_id = args[-1]
                asked.append(package_id)
                reply = answers.get(package_id, {"ok": True, "stdout": ""})
                return {"ok": reply["ok"], "stdout": reply["stdout"], "stderr": reply.get("stderr", "")}
            if args[:1] == ["adb"] and "reverse" in args:
                return {"ok": True, "stdout": "", "stderr": ""}
            return {"ok": True, "stdout": "", "stderr": ""}

        self.tool.run = fake_run
        self.asked = asked
        return asked

    def test_the_github_build_is_detected(self):
        """The GitHub id is a WiVRn install like any other."""
        path = "package:/data/app/~~hash==/org.meumeu.wivrn.github-abc123==/base.apk"
        self.stub_adb({"org.meumeu.wivrn.github": {"ok": True, "stdout": path}})
        setup = self.tool.probe()
        self.assertEqual(setup["wivrnPackageState"], "installed")
        self.assertEqual(setup["wivrnPackageId"], "org.meumeu.wivrn.github")
        self.assertEqual(setup["wivrnPackage"], path)

    def test_the_play_build_is_detected(self):
        """The Play id is a WiVRn install like any other."""
        path = "package:/data/app/~~hash==/org.meumeu.wivrn-abc123==/base.apk"
        self.stub_adb({"org.meumeu.wivrn": {"ok": True, "stdout": path}})
        setup = self.tool.probe()
        self.assertEqual(setup["wivrnPackageState"], "installed")
        self.assertEqual(setup["wivrnPackageId"], "org.meumeu.wivrn")
        self.assertEqual(setup["wivrnPackage"], path)

    def test_neither_build_installed_is_reported_absent(self):
        """Two successful queries with no answer: WiVRn is genuinely absent."""
        self.stub_adb({})
        setup = self.tool.probe()
        self.assertIsNone(setup["wivrnPackage"])
        self.assertIsNone(setup["wivrnPackageId"])
        self.assertEqual(setup["wivrnPackageState"], "absent")

    def test_a_failed_query_is_unknown_not_absent(self):
        """A query that failed says nothing about the package: the state is unknown."""
        self.stub_adb({"org.meumeu.wivrn": {"ok": False, "stdout": "", "stderr": "error: device offline"}})
        setup = self.tool.probe()
        self.assertIsNone(setup["wivrnPackage"])
        self.assertEqual(setup["wivrnPackageState"], "unknown")

    def test_an_error_answer_is_not_an_install(self):
        """An error on stdout is not a package path: nothing is installed."""
        self.stub_adb({"org.meumeu.wivrn": {"ok": True, "stdout": "error: package not found"}})
        setup = self.tool.probe()
        self.assertIsNone(setup["wivrnPackage"])
        self.assertEqual(setup["wivrnPackageState"], "absent")

    def test_an_unrelated_package_path_is_not_accepted(self):
        """A path to another app is never reported as WiVRn."""
        path = "package:/data/app/~~hash==/com.example.notwivrn-abc123==/base.apk"
        self.stub_adb({"org.meumeu.wivrn": {"ok": True, "stdout": path}})
        setup = self.tool.probe()
        self.assertIsNone(setup["wivrnPackage"])
        self.assertIsNone(setup["wivrnPackageId"])
        self.assertEqual(setup["wivrnPackageState"], "absent")

    def test_the_ids_are_matched_exactly_not_as_substrings(self):
        """The GitHub id must not satisfy a query for the Play id, and the other way round.

        The two ids are prefixes of each other, so a substring match would report the GitHub build
        as the Play build. The answer is only accepted for the id that was actually requested.
        """
        github_path = "package:/data/app/~~hash==/org.meumeu.wivrn.github-abc123==/base.apk"
        asked = self.stub_adb({"org.meumeu.wivrn": {"ok": True, "stdout": github_path},
                               "org.meumeu.wivrn.github": {"ok": True, "stdout": github_path}})
        setup = self.tool.probe()
        # the Play id answered with the GitHub path: that answer is about the GitHub package only
        self.assertIn("org.meumeu.wivrn", asked)
        self.assertEqual(setup["wivrnPackageId"], "org.meumeu.wivrn.github")
        self.assertEqual(setup["wivrnPackage"], github_path)

        # and the reverse: the Play path must not be accepted for the GitHub id
        play_path = "package:/data/app/~~hash==/org.meumeu.wivrn-abc123==/base.apk"
        self.stub_adb({"org.meumeu.wivrn": {"ok": True, "stdout": play_path},
                       "org.meumeu.wivrn.github": {"ok": True, "stdout": play_path}})
        setup = self.tool.probe()
        self.assertEqual(setup["wivrnPackageId"], "org.meumeu.wivrn")
        self.assertEqual(setup["wivrnPackage"], play_path)

    def test_the_matcher_rejects_a_bare_prefix(self):
        """`org.meumeu.wivrn` must not match `org.meumeu.wivrn.github` (or any other extension).

        The hyphen form is only a build hash, so the suffix has to look like one: a digit-carrying
        base64/hex token. A word (`-extra`), a bare extension (`X`) and the GitHub id are all
        rejected.
        """
        self.assertTrue(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/org.meumeu.wivrn-XyZ987==/base.apk", "org.meumeu.wivrn"))
        self.assertFalse(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/org.meumeu.wivrn.github-XyZ987==/base.apk", "org.meumeu.wivrn"))
        self.assertFalse(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/org.meumeu.wivrnX-1==/base.apk", "org.meumeu.wivrn"))
        self.assertFalse(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/org.meumeu.wivrn-extra-1==/base.apk", "org.meumeu.wivrn"))
        self.assertFalse(self.tool.wivrn_path_matches("package:", "org.meumeu.wivrn"))
        self.assertFalse(self.tool.wivrn_path_matches("", "org.meumeu.wivrn"))
        self.assertFalse(self.tool.wivrn_path_matches("error: failed", "org.meumeu.wivrn"))


    def test_the_android_package_directory_style_is_accepted(self):
        """The real `pm path` answer names the package as a DIRECTORY: `<id>-<build>==`.

        The build hash suffix belongs to that directory component, so the id is still the id.
        """
        path = "package:/data/app/~~aBcD1234==/org.meumeu.wivrn.github-XyZ987==/base.apk"
        self.stub_adb({"org.meumeu.wivrn.github": {"ok": True, "stdout": path}})
        setup = self.tool.probe()
        self.assertEqual(setup["wivrnPackageState"], "installed")
        self.assertEqual(setup["wivrnPackageId"], "org.meumeu.wivrn.github")
        self.assertEqual(setup["wivrnPackage"], path)

    def test_a_plain_id_directory_is_accepted(self):
        """A directory whose component IS the id is the same package."""
        path = "package:/data/app/org.meumeu.wivrn/base.apk"
        self.stub_adb({"org.meumeu.wivrn": {"ok": True, "stdout": path}})
        setup = self.tool.probe()
        self.assertEqual(setup["wivrnPackageState"], "installed")
        self.assertEqual(setup["wivrnPackageId"], "org.meumeu.wivrn")
        self.assertEqual(setup["wivrnPackage"], path)

    def test_a_split_apk_under_the_canonical_directory_is_accepted(self):
        """A split APK sits UNDER the canonical package directory: it is the same package."""
        path = "package:/data/app/~~aBcD1234==/org.meumeu.wivrn-XyZ987==/split_config.arm64.v8a.apk"
        self.stub_adb({"org.meumeu.wivrn": {"ok": True, "stdout": path}})
        setup = self.tool.probe()
        self.assertEqual(setup["wivrnPackageState"], "installed")
        self.assertEqual(setup["wivrnPackageId"], "org.meumeu.wivrn")
        self.assertEqual(setup["wivrnPackage"], path)

    def test_a_hyphenated_directory_of_another_package_is_rejected(self):
        """`org.meumeu.wivrn-extra` is another package, not the WiVRn Play build."""
        path = "package:/data/app/~~aBcD1234==/org.meumeu.wivrn-extra-1==/base.apk"
        self.stub_adb({"org.meumeu.wivrn": {"ok": True, "stdout": path}})
        setup = self.tool.probe()
        self.assertIsNone(setup["wivrnPackage"])
        self.assertEqual(setup["wivrnPackageState"], "absent")

    def test_a_hex_build_hash_directory_is_accepted(self):
        """A hex build token is a build token: the package is the same one."""
        path = "package:/data/app/~~h==/org.meumeu.wivrn-1a2b3c4d/base.apk"
        self.stub_adb({"org.meumeu.wivrn": {"ok": True, "stdout": path}})
        setup = self.tool.probe()
        self.assertEqual(setup["wivrnPackageState"], "installed")
        self.assertEqual(setup["wivrnPackageId"], "org.meumeu.wivrn")

    def test_an_x_extension_directory_is_not_accepted(self):
        """`org.meumeu.wivrnX` is another package, not the WiVRn Play build."""
        path = "package:/data/app/~~h==/org.meumeu.wivrnX-1==/base.apk"
        self.stub_adb({"org.meumeu.wivrn": {"ok": True, "stdout": path}})
        setup = self.tool.probe()
        self.assertIsNone(setup["wivrnPackage"])
        self.assertEqual(setup["wivrnPackageState"], "absent")

    def test_the_github_directory_is_not_the_play_id(self):
        """The GitHub directory must not satisfy the Play id, in the directory style too."""
        path = "package:/data/app/~~aBcD1234==/org.meumeu.wivrn.github-XyZ987==/base.apk"
        self.stub_adb({"org.meumeu.wivrn": {"ok": True, "stdout": path},
                       "org.meumeu.wivrn.github": {"ok": True, "stdout": path}})
        setup = self.tool.probe()
        self.assertEqual(setup["wivrnPackageId"], "org.meumeu.wivrn.github")

    def test_the_matcher_accepts_the_directory_styles_and_rejects_the_rest(self):
        """The rule, spelled out: exact component, or `id` plus an opaque build token.

        Accepted: the id as a whole path component, the id plus the base64/hex build hash Android
        appends, and a base or split APK under that canonical directory.
        Rejected: a cross-package directory, a bare prefix, an arbitrary alphabetic extension
        (`-extra`), an unrelated package and any error output.
        """
        play = "org.meumeu.wivrn"
        github = "org.meumeu.wivrn.github"

        # --- accepted: the exact style, the id as a whole component
        self.assertTrue(self.tool.wivrn_path_matches(
            "package:/data/app/org.meumeu.wivrn/base.apk", play))
        self.assertTrue(self.tool.wivrn_path_matches(
            "package:/data/app/org.meumeu.wivrn.github/base.apk", github))

        # --- accepted: the hyphen build-hash style, for BOTH canonical ids
        self.assertTrue(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/org.meumeu.wivrn-XyZ987==/base.apk", play))
        self.assertTrue(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/org.meumeu.wivrn.github-XyZ987==/base.apk", github))
        # a hex build token is a build token too
        self.assertTrue(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/org.meumeu.wivrn-1a2b3c4d/base.apk", play))

        # --- accepted: a base or split APK UNDER the canonical directory
        self.assertTrue(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/org.meumeu.wivrn-XyZ987==/base.apk", play))
        self.assertTrue(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/org.meumeu.wivrn-XyZ987==/split_config.arm64.v8a.apk", play))
        self.assertTrue(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/org.meumeu.wivrn.github-XyZ987==/split_config.arm64.v8a.apk", github))

        # --- rejected: cross-package, in both directions
        self.assertFalse(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/org.meumeu.wivrn.github-XyZ987==/base.apk", play))
        self.assertFalse(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/org.meumeu.wivrn-XyZ987==/base.apk", github))

        # --- rejected: a bare prefix and an `X` extension
        self.assertFalse(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/org.meumeu.wivrnX-1==/base.apk", play))
        self.assertFalse(self.tool.wivrn_path_matches(
            "package:/data/app/org.meumeu.wivrnX/base.apk", play))

        # --- rejected: an arbitrary alphabetic extension (`-extra`), with and without a build part
        self.assertFalse(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/org.meumeu.wivrn-extra-1==/base.apk", play))
        self.assertFalse(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/org.meumeu.wivrn-extra==/base.apk", play))
        self.assertFalse(self.tool.wivrn_path_matches(
            "package:/data/app/org.meumeu.wivrn-extra/base.apk", play))

        # --- rejected: an unrelated package
        self.assertFalse(self.tool.wivrn_path_matches(
            "package:/data/app/~~h==/com.example.notwivrn-1==/base.apk", play))
        self.assertFalse(self.tool.wivrn_path_matches(
            "package:/data/app/com.example.notwivrn/base.apk", play))

        # --- rejected: malformed and error output
        self.assertFalse(self.tool.wivrn_path_matches("package:", play))
        self.assertFalse(self.tool.wivrn_path_matches("", play))
        self.assertFalse(self.tool.wivrn_path_matches("error: failed", play))
        self.assertFalse(self.tool.wivrn_path_matches("package:/data/app", play))

if __name__ == "__main__":
    unittest.main()

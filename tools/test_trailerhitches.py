"""Offline regression checks for spawn/measure/cleanup and restart verification; never contacts a game."""
import contextlib
import io
import itertools
import json
import pathlib
import socket
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

import trailerhitches as hitch


class SaveVerification(unittest.TestCase):
    def setUp(self):
        hitch.VERDICTS.clear()

    def verify(self, trailer_info, removed, present=True, slot=8):
        commands = []

        def bridge(command):
            commands.append(command)
            if command == "whereis ":
                rows = [[42, "Car09", "", "attached False"]]
                if present:
                    rows.append([43, "UtanFutoFull", "", trailer_info])
                return {"items": rows}
            if command == "get TrailerHitches.Plugin.Version":
                raise RuntimeError("no type found in path TrailerHitches.Plugin.Version")
            if command.startswith("call TrailerHitches.Probe.Catalog"):
                return {"value": json.dumps({"cars": [{"id": 371, "slot": slot, "slotName": "VonoHorogCollider"}]})}
            if command.startswith("call TrailerHitches.Probe.Measure"):
                return {"value": "{}"}
            if command == "errors":
                return {"count": 0}
            raise AssertionError(command)

        with tempfile.TemporaryDirectory() as folder:
            path = pathlib.Path(folder) / "pair.json"
            hitch.write_json(path, {"item": 371, "car": 42, "trailer": 43, "slot": 8})
            with contextlib.redirect_stdout(io.StringIO()):
                hitch.verify_save(bridge, path, removed)
        return commands

    def test_host_port_is_released_before_game_binds(self):
        def bridge(command):
            self.assertTrue(command.startswith("mp host "))
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as transport:
                transport.bind(("127.0.0.1", int(command.split()[-1])))
            return {"hosted": True}
        self.assertEqual(hitch.host_local(bridge), {"hosted": True})

    def test_removed_uses_stock_diagnostics_without_loading_mod(self):
        commands = self.verify("attached False slot -", True)
        self.assertTrue(all(ok for _, ok in hitch.VERDICTS))
        self.assertFalse(any("call TrailerHitches" in command for command in commands))

    def test_generic_attachment_fallback_is_a_failure(self):
        self.verify("attached True slot -", True)
        self.assertIn(("loaded attachment state", False), hitch.VERDICTS)

    def test_lost_trailer_is_a_failure(self):
        self.verify("", True, present=False)
        self.assertIn(("saved trailer survives", False), hitch.VERDICTS)

    def test_reinstalled_slot_must_keep_index(self):
        self.verify("attached True slot VonoHorogCollider", False, slot=9)
        self.assertIn(("slot index stable after restart", False), hitch.VERDICTS)

    def test_restart_uses_actual_slot_name_and_exact_saved_car(self):
        commands = self.verify("attached True slot VonoHorogCollider", False)
        self.assertTrue(all(ok for _, ok in hitch.VERDICTS))
        self.assertIn("call TrailerHitches.Probe.Measure 42", commands)


class SpawnVerification(unittest.TestCase):
    def test_null_slot_error_keeps_instance_diagnostics(self):
        def bridge(command):
            return {"value": json.dumps({"error": "null slot in spawned car at index 21",
                                         "receiver": {"component": False, "listEntry": False, "canCraft": False}})}
        with self.assertRaisesRegex(RuntimeError, 'index 21.*"component": false.*"listEntry": false'):
            hitch.probe(bridge, "SpawnState", 1)

    def test_bridge_job_timeout_is_classified_as_unavailable(self):
        with patch.object(hitch.socket, "create_connection") as connect:
            connect.return_value.__enter__.return_value.makefile.return_value = io.StringIO('{"error":"timeout"}\n')
            with self.assertRaises(hitch.BridgeUnavailable):
                hitch.Bridge(27060)("errors")

    def test_waits_for_loaded_exact_root_despite_canonical_item_id_and_unrelated_spawns(self):
        for blocked in ({"dormant": True}, {"stored": True}, {"shown": False}, {"proxy": True}, {}):
            with self.subTest(blocked=blocked):
                commands, owned = [], []
                states = iter([{"ready": False}, {"ready": True, "save": 502, "runtimeItem": 243}])
                polls = 0

                def bridge(command):
                    nonlocal polls
                    commands.append(command)
                    if command == "mp entities":
                        polls += 1
                        base = {"part": -1, "dormant": False, "stored": False, "shown": True, "proxy": False}
                        entities = [] if polls == 1 else [dict(base, net=1), dict(base, net=22)]
                        if polls == 2:
                            entities[1].update(blocked)
                        return {"entities": entities}
                    if command.startswith("call TrailerHitches.Probe.Spawn "):
                        return {"value": '{"ticket":7}'}
                    if command == "call TrailerHitches.Probe.SpawnState 7":
                        return {"value": json.dumps(next(states))}
                    if command.startswith("mp entitems "):
                        net = int(command.split()[-1])
                        return {"items": [{"i": 0, "id": 502 if net == 22 else 999, "loaded": polls > 2}]}
                    raise AssertionError(command)

                with patch.object(hitch.time, "sleep"):
                    car = hitch.spawn(bridge, 244, 1, 2, 0, owned)
                self.assertEqual((car["net"], car["save"], car["runtimeItem"]), (22, 502, 243))
                self.assertEqual(owned, [7])
                self.assertEqual(commands.count("call TrailerHitches.Probe.SpawnState 7"), 2)
                self.assertFalse(any(c.startswith("spawncarg") for c in commands))

    def test_unready_spawn_times_out_but_keeps_cleanup_ticket(self):
        owned = []

        def bridge(command):
            if command == "mp entities":
                return {"entities": []}
            if command.startswith("call TrailerHitches.Probe.Spawn "):
                return {"value": '{"ticket":7}'}
            return {"value": '{"ready":false}'}

        ticks = itertools.count(0, 10)
        with patch.object(hitch.time, "monotonic", side_effect=lambda: next(ticks)), patch.object(hitch.time, "sleep"):
            with self.assertRaisesRegex(RuntimeError, "SpawnState.*not ready"):
                hitch.spawn(bridge, 244, 1, 2, 0, owned)
        self.assertEqual(owned, [7])


class FakeWorld:
    """Bridge responses model Full prefab aliases and independently delayed spawning/sharing."""
    def __init__(self):
        self.commands = []
        self.spawns = {}
        self.next_ticket = 0
        self.safety = True
        self.catalog = [{"id": item, "name": name, "added": True, "structure": True,
                         "slot": 21, "slotName": "VonoHorogCollider", "position": [0, -0.1, -2.26]}
                        for item, name in ((243, "Car06P"), (244, "Car06PFull"))]

    def __call__(self, command):
        self.commands.append(command)
        words = command.split()
        if words[:1] == ["call"]:
            method = words[1].split(".")[-1]
            args = words[2:]
            return {"value": json.dumps(self.probe(method, args))}
        if command == "roadpoint":
            return {"gx": 20, "gz": 30, "yaw": 0}
        if command.startswith("get "):
            return {"value": False if "BlockAutoSave" in command else True}
        if command.startswith("set "):
            if "MissingTrailerSlot" in command:
                self.safety = words[-1] == "True"
            return {"value": words[-1]}
        if command.startswith("mp host ") or command == "mp stop":
            return {"ok": True}
        if command.startswith("tpground"):
            return {"waiting": False}
        if command == "mp entities":
            return {"entities": [{"net": t + 1000, "part": -1, "dormant": False, "stored": False,
                                  "shown": True, "proxy": False} for t in self.spawns]}
        if command.startswith("mp entitems "):
            ticket = int(words[-1]) - 1000
            return {"items": [{"i": 0, "id": self.spawns[ticket]["save"], "loaded": True}]}
        if command.startswith("mount hitch "):
            return {"hitched": "VonoHorogCollider"}
        if command == "errors":
            return {"count": 0}
        raise AssertionError(command)

    def probe(self, method, args):
        if method == "Ready":
            return {"ready": True}
        if method == "Catalog":
            return {"cars": self.catalog}
        if method == "Spawn":
            self.next_ticket += 1
            ticket = self.next_ticket
            self.spawns[ticket] = {"save": ticket + 500, "runtimeItem": 243, "ready": True,
                                   "receiver": {"assembly": True, "component": True, "listEntry": True,
                                                "owned": True, "canCraft": True, "slot": 21}}
            return {"ticket": ticket}
        if method == "SpawnState":
            return self.spawns[int(args[0])]
        if method == "LevelFixture":
            assert int(args[0]) in self.spawns
            return {"rotationHeld": True}
        if method == "ReleaseFixture":
            return {"rotationHeld": False}
        if method == "Measure":
            return {"item": 243, "save": int(args[0]), "slot": 21, "local": [0, -0.1, -2.26],
                    "carBasis": [[1, 0, 0], [0, 1, 0], [0, 0, 1]], "geometry": {"groundNormal": [0, 1, 0]},
                    "hitchHeight": 0.4, "drawbarHeight": 0.4, "pitch": 0, "roll": 0, "gap": 0, "attached": True}
        if method == "RecordRoundTrip":
            missing = args[1] == "True"
            return {"attached": not missing or not self.safety, "inSlot": not missing, "index": 21, "trailer": 502}
        if method == "Cleanup":
            self.spawns.pop(int(args[0]))
            return {"cleaned": True}
        raise AssertionError((method, args))


class MeasurementRun(unittest.TestCase):
    def setUp(self):
        hitch.VERDICTS.clear()

    def run_world(self, world):
        with tempfile.TemporaryDirectory() as folder:
            args = SimpleNamespace(photos=False, keep_save_pair=None, settle=0, max_tilt=3,
                                   out=str(pathlib.Path(folder) / "result.json"))
            with patch.object(hitch, "EXPECTED", {243, 244}), patch.object(hitch.time, "sleep"), contextlib.redirect_stdout(io.StringIO()):
                hitch.run(world, args)
            return json.loads(pathlib.Path(args.out).read_text(encoding="utf-8"))

    def test_full_sweep_measures_exact_spawns_and_cleans_both_vehicles(self):
        world = FakeWorld()
        results = self.run_world(world)
        self.assertEqual([r["prefab"]["id"] for r in results], [243, 244])
        self.assertEqual([r["measurement"]["item"] for r in results], [243, 243])
        self.assertEqual([r["measurement"]["save"] for r in results], [501, 503])
        self.assertTrue(all(ok for _, ok in hitch.VERDICTS), hitch.VERDICTS)
        self.assertEqual(world.spawns, {})
        self.assertEqual(sum(".Cleanup " in c for c in world.commands), 4)
        self.assertIn("call TrailerHitches.Probe.Measure 501", world.commands)
        self.assertLess(world.commands.index("call TrailerHitches.Probe.LevelFixture 1"),
                        next(i for i, c in enumerate(world.commands) if c.startswith("mount hitch ")))
        self.assertIn("call TrailerHitches.Probe.RecordRoundTrip 503 False", world.commands)
        self.assertFalse(any(".RecordRoundTrip 244" in c for c in world.commands))
        self.assertLess(next(i for i, c in enumerate(world.commands) if c.startswith("tpground ")),
                        next(i for i, c in enumerate(world.commands) if ".Spawn " in c))

    def test_partial_trailer_spawn_failure_cleans_car_and_failed_spawn(self):
        world = FakeWorld()
        native_probe = world.probe

        def fail_trailer(method, args):
            if method == "SpawnState" and int(args[0]) % 2 == 0:
                return {"error": "part initialization failed"}
            return native_probe(method, args)

        world.probe = fail_trailer
        self.assertEqual(self.run_world(world), [])
        self.assertEqual(world.spawns, {})
        self.assertEqual(sum(".Cleanup " in c for c in world.commands), 4)

    def test_tilted_car_cannot_pass_as_a_level_hitch_fixture(self):
        world = FakeWorld()
        native_probe = world.probe

        def tilted(method, args):
            result = native_probe(method, args)
            if method == "Measure":
                result["carBasis"] = [[1, 0, 0], [0, 0.8, 0.6], [0, -0.6, 0.8]]
            return result

        world.probe = tilted
        self.run_world(world)
        self.assertTrue(any("upright car fixture" in name and not ok for name, ok in hitch.VERDICTS))

    def test_retained_save_pair_releases_rotation_constraint(self):
        world = FakeWorld()
        world.catalog = [dict(world.catalog[0], id=371)]
        with tempfile.TemporaryDirectory() as folder:
            manifest = str(pathlib.Path(folder) / "pair.json")
            args = SimpleNamespace(photos=False, keep_save_pair=manifest, settle=0, max_tilt=3, out=None)
            with patch.object(hitch, "EXPECTED", {371}), patch.object(hitch.time, "sleep"), contextlib.redirect_stdout(io.StringIO()):
                hitch.run(world, args)
            self.assertTrue(pathlib.Path(manifest).is_file())
        self.assertTrue(all(ok for _, ok in hitch.VERDICTS), hitch.VERDICTS)
        self.assertIn("call TrailerHitches.Probe.ReleaseFixture 1", world.commands)
        self.assertFalse(any(".Cleanup " in c for c in world.commands))

    def test_sloping_fixture_is_rejected_before_hitching_and_cleans_car(self):
        world = FakeWorld()
        native_probe = world.probe

        def slope(method, args):
            return {"error": "fixture requires flat ground"} if method == "LevelFixture" else native_probe(method, args)

        world.probe = slope
        self.assertEqual(self.run_world(world), [])
        self.assertFalse(any(c.startswith("mount hitch ") for c in world.commands))
        self.assertEqual(world.spawns, {})

    def test_prefab_success_cannot_hide_a_broken_spawned_receiver(self):
        for field in ("assembly", "component", "listEntry", "owned", "canCraft", "slot"):
            with self.subTest(field=field):
                hitch.VERDICTS.clear()
                world = FakeWorld()
                native_probe = world.probe

                def broken_receiver(method, args):
                    result = native_probe(method, args)
                    if method == "SpawnState":
                        result["receiver"][field] = False if field != "slot" else -1
                    return result

                world.probe = broken_receiver
                self.run_world(world)
                self.assertTrue(any("spawned receiver" in name and not ok for name, ok in hitch.VERDICTS))
                self.assertEqual(world.spawns, {})

    def test_bridge_timeout_aborts_sweep_preserves_results_and_attempts_all_restores(self):
        world = FakeWorld()
        world.catalog.append({**world.catalog[0], "id": 397, "name": "Car09Tfull"})

        def bridge(command):
            if command == "call TrailerHitches.Probe.Spawn 244 21.800 30.000 0.000":
                raise hitch.BridgeUnavailable("timeout")
            if command == "mp stop":
                world.commands.append(command)
                raise hitch.BridgeUnavailable("timeout")
            return world(command)

        results = self.run_world(bridge)
        self.assertEqual(len(results), 1)
        self.assertIn("set TLDRevamp.Fixes.MissingTrailerSlot.Enabled True", world.commands)
        self.assertEqual(world.commands[-1], "set TLDRevamp.TestSafety.BlockAutoSave False")
        self.assertIn(("measurement run completes", False), hitch.VERDICTS)
        self.assertFalse(any(".Spawn 397 " in c for c in world.commands))

    def test_unready_world_does_not_spawn_and_still_writes_output(self):
        commands = []

        def bridge(command):
            commands.append(command)
            return {"value": '{"ready":false}'}

        ticks = itertools.count(0, 10)
        with patch.object(hitch.time, "monotonic", side_effect=lambda: next(ticks)):
            self.assertEqual(self.run_world(bridge), [])
        self.assertTrue(all(".Ready" in c for c in commands))
        self.assertIn(("measurement run completes", False), hitch.VERDICTS)

    def test_cleanup_failure_does_not_skip_other_vehicle_or_restoration(self):
        world = FakeWorld()
        native_probe = world.probe

        def fail_one_cleanup(method, args):
            if method == "Cleanup" and args == ["2"]:
                return {"error": "cleanup failed"}
            return native_probe(method, args)

        world.probe = fail_one_cleanup
        self.run_world(world)
        self.assertIn("call TrailerHitches.Probe.Cleanup 1", world.commands)
        self.assertEqual(world.commands[-1], "set TLDRevamp.TestSafety.BlockAutoSave False")
        self.assertIn(("item 243 Car06P: cleanup", False), hitch.VERDICTS)


if __name__ == "__main__":
    unittest.main()

"""Offline checks for the restart verification harness; never contacts a game."""
import contextlib
import io
import json
import pathlib
import socket
import tempfile
import unittest

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
                return {"value": json.dumps({"cars": [{"id": 371, "slot": slot}]})}
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
        self.verify("attached True slot TrailerReceiverHitch", False, slot=9)
        self.assertIn(("slot index stable after restart", False), hitch.VERDICTS)


if __name__ == "__main__":
    unittest.main()

"""Offline validation and fake-bridge tests; no game is started or contacted."""
import contextlib
import io
import json
import pathlib
import tempfile
import unittest

import replay_repro as repro


def row(action, **values):
    result = [0] * len(repro.COLS)
    result[2] = repro.KINDS.index(action)
    for key, value in values.items():
        result[repro.COLS.index(key)] = value
    return result


def document(rows):
    return dict(format=1, protocol=40, sampleHz=10, complete=True, dropped=0, cols=repro.COLS, rows=rows)


class FakeBridge:
    def __init__(self, mismatch=False, protocol=41):
        self.commands = []
        self.mismatch = mismatch
        self.protocol = protocol

    def __call__(self, command):
        self.commands.append(command)
        if command == "repro status":
            return dict(isolated=True, batch=False, dedicated=False, bridge=True, localPlayer=True, protocol=self.protocol, session="test", recorder=dict(recording=False))
        if command == "net conditions status":
            return dict(conditions=dict(latencyMs=0, jitterMs=0, lossPct=0, duplicationPct=0, reorderPct=0))
        if command == "repro peer":
            return dict(peers=1)
        if command.startswith("repro resolve"):
            return dict(net=99)
        if command.startswith("repro check"):
            return dict(pos=[100 if self.mismatch else 0, 0, 0], parent=0, stored=False, held=False, items=[])
        if command == "repro players":
            player = dict(pos=[0, 0, 0], net=0, seat=-1)
            return dict(local=player, remote=player)
        if command.startswith("repro poicheck"):
            return dict(state=[1 if self.mismatch else 0, 0, 0, 0, 0])
        return dict(ok=True)


class ReplayTests(unittest.TestCase):
    def load(self, data):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "repro.json"
            path.write_text(json.dumps(data), encoding="utf-8")
            return repro.load_recording(path)

    def test_numeric_schema(self):
        rows = [row("network"), row("bind", net=7, a=11), row("pickup", net=7), row("pose", t=0.1, x=2)]
        self.assertEqual(self.load(document(rows)), rows)
        for change in (dict(complete=False), dict(dropped=1), dict(cols=[]), dict(secret="private")):
            with self.assertRaises(ValueError):
                self.load(dict(document(rows), **change))

    def test_malformed_rows(self):
        for bad in (row("pose", x=float("nan")), row("pose", x="private"), row("pose", net=-1), row("pose", net=1),
                    row("command", a=0), row("command", a=1.5), row("network", x=-1), [0], row("pose", kind=100), row("pose", x=True)):
            with self.assertRaises(ValueError):
                self.load(document([bad]))
        with self.assertRaises(ValueError):
            self.load(document([row("pose", t=1), row("pose", t=0)]))

    def test_map_and_cleanup(self):
        a, b = FakeBridge(), FakeBridge()
        result = repro.replay(a, b, [row("bind", net=7, a=11), row("pickup", net=7)])
        self.assertTrue(result["ok"])
        self.assertTrue(any(c.startswith("repro act 4 99 ") for c in a.commands))
        for bridge in (a, b):
            self.assertEqual(bridge.commands[-1], "repro end")
            self.assertIn("net conditions set 0 0 0 0 0 0", bridge.commands)

    def test_mismatch_fails_and_restores(self):
        a, b = FakeBridge(), FakeBridge(mismatch=True)
        with self.assertRaises(RuntimeError):
            repro.replay(a, b, [row("bind", net=7, a=11), row("drop", net=7)], timeout=0)
        self.assertEqual(a.commands[-1], "repro end")
        self.assertEqual(b.commands[-1], "repro end")

    def test_peer_timeline(self):
        a, b = FakeBridge(), FakeBridge()
        repro.replay(a, b, [row("command", a=1)], [row("command", a=2)])
        self.assertTrue(any(c.startswith("repro act 21 ") for c in a.commands))
        self.assertTrue(any(c.startswith("repro act 21 ") for c in b.commands))

    def test_building_mismatch_fails(self):
        a, b = FakeBridge(), FakeBridge(mismatch=True)
        with self.assertRaises(RuntimeError):
            repro.replay(a, b, [row("poi_usable", item=1, slot=2, e=3, f=4, g=5)], timeout=0)
        self.assertIn("repro poicheck 1 3 4 5 2", a.commands)
        self.assertEqual(a.commands[-1], "repro end")

    def test_protocol_is_informational(self):
        for protocol in (40, 41, 99):
            self.assertEqual(self.load(dict(document([row("pose")]), protocol=protocol)), [row("pose")])
            repro.replay(FakeBridge(protocol=protocol), FakeBridge(protocol=protocol), self.load(document([row("pose")])))
        repro.replay(FakeBridge(), FakeBridge(), self.load(document([row("pose")])))

    def test_preflight_rejects_unsafe_or_mismatched_copies(self):
        for fields in (dict(dedicated=True), dict(isolated=False), dict(bridge=False), dict(localPlayer=False), dict(protocol=42)):
            a, b = FakeBridge(), FakeBridge()
            def unsafe(command):
                result = b(command)
                if command == "repro status":
                    result.update(fields)
                return result
            with self.assertRaises(RuntimeError):
                repro.replay(a, unsafe, [row("pose")])
            self.assertNotIn("repro arm", a.commands)

    def test_cumulative_ignition_counter_and_skips(self):
        a, b = FakeBridge(), FakeBridge()
        def historical_refusal(command):
            result = a(command)
            if command.startswith("repro act"):
                return dict(refused=9, skipped=True, ok=True)
            return result
        result = repro.replay(historical_refusal, b, [row("bind", net=7, a=11), row("ignition", net=7, a=1)])
        self.assertEqual(result["skipped"], 1)
        def refused(command):
            result = a(command)
            return dict(ok=False) if command.startswith("repro act") else result
        with self.assertRaises(RuntimeError):
            repro.replay(refused, b, [row("pose")])

    def test_unresolvable_rows_warn_but_real_loss_refused(self):
        warning = io.StringIO()
        with contextlib.redirect_stderr(warning):
            self.load(dict(document([row("pose")]), skipped=2))
        self.assertEqual(json.loads(warning.getvalue()), dict(warning="unresolvable recording rows skipped", skipped=2))
        with self.assertRaises(ValueError):
            self.load(dict(document([row("pose")]), skipped=-1))

    def test_legacy_network_restored(self):
        a, b = FakeBridge(), FakeBridge()
        def legacy(command):
            result = a(command)
            if command == "net conditions status":
                result["conditions"].update(latencyMs=90, jitterMs=40, lossPct=2, reorderPct=10, sendOnly=True)
            return result
        repro.replay(legacy, b, [row("pose")])
        self.assertIn("net conditions set 90 40 2 0 10 1", a.commands)

    def test_no_string_coercion_or_structural_false_positive(self):
        self.assertFalse(repro.close_enough(True, 1, 0.1))
        self.assertFalse(repro.close_enough([1], [1, 2], 0.1))
        self.assertFalse(repro.close_enough({"x": 1}, {"x": 1, "y": 2}, 0.1))


if __name__ == "__main__":
    unittest.main()

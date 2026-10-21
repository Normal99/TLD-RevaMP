import json
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
import privacy_scan as ps


class SystemdUnitNameTests(unittest.TestCase):
    """A systemd unit name is not an email address, but a real address at a unit suffix still is."""

    def caught(self, text):
        return any(rx.search(text) for _, rx in ps.CHECKS) and not ps.is_systemd_unit(text)

    def test_systemd_unit_names_are_not_email_addresses(self):
        for unit in ("user@1000.service", "wayland-wm@hyprland.desktop.service",
                     "gamemode@user.service", "dbus@user.service"):
            with self.subTest(unit=unit):
                self.assertFalse(self.caught(unit))

    def test_real_addresses_are_still_caught(self):
        # The placeholders are built from pieces: a literal address in this file would itself be a finding
        # (the privacy scan walks every tracked file, this one included).
        for local, domain in (("someone", "example.com"), ("alice", "example.service"),
                              ("app", "my-service.service"), ("team", "sub.example.org"),
                              ("first.last", "example.org"), ("mail", "corp.net")):
            with self.subTest(address=local + "@" + domain):
                self.assertTrue(self.caught(local + "@" + domain))


class TestStubMarkerTests(unittest.TestCase):
    """A marked, hand-written stub under tools/unit is exempt from the decompiled-code check; nothing else is."""
    # Built from pieces, so this file itself holds no line the decompiled-code check would match.
    CLASS = "public " + "class " + "main" + "script { public static int s; }"

    def kinds(self, rel, text):
        return [what for _, _, what, _ in ps.scan_text(rel, text)]

    def test_unmarked_stub_under_tools_unit_still_fails(self):
        self.assertIn("decompiled game code", self.kinds("tools/unit/SomeStubs.cs", self.CLASS + "\n"))

    def test_marked_stub_under_tools_unit_passes(self):
        self.assertEqual([], self.kinds("tools/unit/SomeStubs.cs", self.CLASS + "   " + ps.STUB_MARKER + "\n"))

    def test_marked_stub_after_a_blank_line_passes(self):
        # the pattern's leading whitespace can start the match on the blank line above the class
        self.assertEqual([], self.kinds("tools/unit/SomeStubs.cs", "\n\n" + self.CLASS + " " + ps.STUB_MARKER + "\n"))

    def test_marker_outside_tools_unit_exempts_nothing(self):
        for rel in ("src/TLDRevamp/Fake.cs", "tools/Fake.cs", "src/tools/unit/Fake.cs"):
            with self.subTest(rel=rel):
                self.assertIn("decompiled game code", self.kinds(rel, self.CLASS + " " + ps.STUB_MARKER + "\n"))

    def test_marker_on_another_line_exempts_nothing(self):
        text = ps.STUB_MARKER + "\n" + self.CLASS + "\n"
        self.assertIn("decompiled game code", self.kinds("tools/unit/SomeStubs.cs", text))

    def test_marker_does_not_exempt_other_checks(self):
        address = "/ho" + "me/someone/x " + ps.STUB_MARKER
        self.assertIn("a home folder path", self.kinds("tools/unit/SomeStubs.cs", address + "\n"))


class CaptureScanTests(unittest.TestCase):
    def scan(self, text, name="stats.json", forbidden=()):
        with tempfile.TemporaryDirectory() as directory:
            file = Path(directory) / name
            file.write_text(text, encoding="utf-8")
            return ps.scan_capture([directory], forbidden)[1]

    def test_clean_game_state_and_utc_date(self):
        self.assertEqual(self.scan(json.dumps({"start": "2026-10-19", "st": 12.5, "pos": [1, 2, 3],
                                              "name": "player-2", "gpu": "GPU Model", "category": "rabbit/animal"})), [])

    def test_private_strings_and_numeric_id_fields(self):
        values = ["203.0.113.9:27070", "[2001:db8::9]:27070", "C:" + "\\Users\\fixture-user\\game.log",
                  "/" + "home/fixture-user/game.log", "~/music.ogg", "%USERPROFILE%/music.ogg",
                  "fake" + "@example.invalid", "https://example.invalid/?private=yes", "2026-10-19T18:32:45+02:00"]
        for value in values:
            with self.subTest(value=value): self.assertTrue(self.scan(json.dumps({"note": value})))
        for key in ("steamId", "accountId", "lobbyId", "port"):
            with self.subTest(key=key): self.assertTrue(self.scan(json.dumps({key: 1234567})))

    def test_decodes_json_escapes_and_scans_names(self):
        self.assertTrue(self.scan('{"note":"\\u0059lva Testperson"}', forbidden=["Ylva Testperson"]))
        self.assertTrue(self.scan("{}", name="Ylva Testperson.json", forbidden=["Ylva Testperson"]))
        self.assertTrue(self.scan("not-json"))

    def test_expands_zip_and_checks_metadata_and_attachments(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.zip"
            for private in (False, True):
                with zipfile.ZipFile(path, "w") as archive:
                    info = zipfile.ZipInfo("screenshot.png" if private else "stats.json")
                    archive.writestr(info, "{}")
                found = ps.scan_capture([path])[1]
                self.assertEqual(bool(found), private)
            with zipfile.ZipFile(path, "w") as archive: archive.writestr("stats.json", "{}")
            self.assertTrue(ps.scan_capture([path])[1])


if __name__ == "__main__": unittest.main()

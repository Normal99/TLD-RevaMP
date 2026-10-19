import json
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
import privacy_scan as ps


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

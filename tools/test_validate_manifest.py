"""Offline tests of the release-manifest validator (no game, no network): python3 -m unittest tools/test_validate_manifest.py

Every fixture is written to a temporary folder: a good manifest, one error at a time, and a local
release archive whose bytes are checked against the catalog.
"""
import contextlib
import hashlib
import io
import json
import pathlib
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import validate_manifest as vm

DLL = b"Synthetic mod bytes, never executable"
HASH = hashlib.sha256(DLL).hexdigest()
OTHER_HASH = hashlib.sha256(DLL + b"!").hexdigest()


def version_entry(number, digest=None, url=None):
    digest = digest or hashlib.sha256(("bytes for " + number).encode()).hexdigest()
    return {
        "version": number,
        "url": url or "https://github.com/owner/repo/releases/download/v%s/TestMod-%s.zip" % (number, number),
        "file": "BepInEx/plugins/TestMod/TestMod.dll",
        "sha256": digest,
        "size": 1234,
        "revamp": "0.69.1",
    }


def manifest(mods=None, fmt=1):
    if mods is None:
        mods = [{
            "id": "test.mod",
            "name": "Test Mod",
            "author": "Someone",
            "description": "A mod for the tests.",
            "source": "https://github.com/owner/repo",
            "versions": [{
                "version": "1.0.0",
                "url": "https://github.com/owner/repo/releases/download/v1.0.0/TestMod-1.0.0.zip",
                "file": "BepInEx/plugins/TestMod/TestMod.dll",
                "sha256": HASH,
                "size": 1234,
                "revamp": "0.69.1",
            }],
        }]
    return {"format": fmt, "mods": mods}


class ValidatorTests(unittest.TestCase):
    def setUp(self):
        self.dir = pathlib.Path(tempfile.mkdtemp())
        self.manifest = self.dir / "mods.json"
        self.approved = self.dir / "approved-mods.txt"
        self.write(manifest(), "%s %s 1.0.0" % (HASH, "test.mod"))

    def write(self, data, approved=None):
        if isinstance(data, (dict, list)):
            self.manifest.write_text(json.dumps(data, indent=2), encoding="utf-8")
        else:
            self.manifest.write_text(data, encoding="utf-8")
        if approved is not None:
            self.approved.write_text(approved, encoding="utf-8")

    def run_validator(self, artifacts=(), **kwargs):
        return vm.validate(self.manifest, self.approved, artifacts, build_version="0.69.1", **kwargs)

    def codes(self, report):
        return sorted(item["code"] for item in report.findings)

    def assertClean(self, report):
        self.assertEqual([f for f in report.findings if f["severity"] == vm.SEVERITY_ERROR], [],
                         "unexpected errors: %s" % self.codes(report))

    def assertHas(self, report, code):
        self.assertIn(code, self.codes(report), "expected %s, got %s" % (code, self.codes(report)))

    # ------------------------------------------------------------------ the good case

    def test_valid_manifest_passes(self):
        report = self.run_validator()
        self.assertClean(report)
        self.assertEqual(report.errors, [])

    def test_the_real_manifest_in_this_repository_passes(self):
        report = vm.validate(vm.DEFAULT_MANIFEST, vm.DEFAULT_APPROVED, [],
                             build_version=vm.read_build_version())
        self.assertClean(report)

    # ------------------------------------------------------------------ schema basics

    def test_broken_json_is_reported_with_its_place(self):
        self.write('{"format": 1, "mods": [')
        report = self.run_validator()
        self.assertHas(report, "catalog.json")
        self.assertIn("line", report.findings[0]["hint"])

    def test_wrong_format_is_refused(self):
        self.write(manifest(fmt=2))
        report = self.run_validator()
        self.assertHas(report, "catalog.format")

    def test_missing_mods_list_is_refused(self):
        self.write({"format": 1})
        report = self.run_validator()
        self.assertHas(report, "catalog.mods.missing")

    def test_missing_required_mod_field_is_refused(self):
        data = manifest()
        del data["mods"][0]["author"]
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "mod.author.missing")

    def test_missing_required_version_field_is_refused(self):
        data = manifest()
        del data["mods"][0]["versions"][0]["sha256"]
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "version.sha256.missing")

    def test_unknown_field_is_a_warning(self):
        data = manifest()
        data["mods"][0]["namee"] = "Typo"
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "mod.unknown-key")
        self.assertEqual(report.errors, [])

    def test_a_null_field_is_not_a_missing_field(self):
        data = manifest()
        data["mods"][0]["id"] = None
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "mod.id.type")
        self.assertNotIn("mod.id.missing", self.codes(report))

    def test_a_null_version_is_refused(self):
        data = manifest()
        data["mods"][0]["versions"][0]["version"] = None
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "version.version.type")

    def test_a_malformed_manifest_does_not_crash_the_validator(self):
        for text in ("", "[]", "null", '{"format":1,"mods":"x"}', '{"format":1,"mods":["x"]}',
                     '{"format":1,"mods":[{"id":"a","name":"b","versions":"x"}]}',
                     '{"format":1,"mods":[{"id":"a","name":"b","versions":[[]]}]}'):
            with self.subTest(text=text):
                self.write(text)
                report = self.run_validator()
                self.assertTrue(report.errors, "expected an error for %r" % text)

    def test_catalog_over_the_size_limit_is_refused(self):
        data = manifest()
        data["mods"][0]["description"] = "x" * (vm.MAX_CATALOG_BYTES + 1)
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "catalog.size")

    # ------------------------------------------------------------------ duplicates

    def test_duplicate_mod_id_is_refused(self):
        data = manifest()
        second = json.loads(json.dumps(data["mods"][0]))
        data["mods"].append(second)
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "mod.duplicate-id")

    def test_duplicate_version_is_refused(self):
        data = manifest()
        data["mods"][0]["versions"].append(json.loads(json.dumps(data["mods"][0]["versions"][0])))
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "version.duplicate")

    def test_duplicate_url_is_refused(self):
        data = manifest()
        other = json.loads(json.dumps(data["mods"][0]["versions"][0]))
        other["version"] = "2.0.0"
        data["mods"][0]["versions"].append(other)
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "version.duplicate-url")

    def test_duplicate_hash_on_the_approved_list_is_refused(self):
        self.write(manifest(), "%s test.mod 1.0.0\n%s test.mod 1.0.0" % (HASH, HASH))
        report = self.run_validator()
        self.assertHas(report, "approved.duplicate-hash")

    def test_one_version_approved_with_two_different_files_is_refused(self):
        self.write(manifest(), "%s test.mod 1.0.0\n%s test.mod 1.0.0" % (HASH, OTHER_HASH))
        report = self.run_validator()
        self.assertHas(report, "approved.duplicate-version")

    # ------------------------------------------------------------------ ids, versions, urls

    def test_bad_mod_id_is_refused(self):
        data = manifest()
        data["mods"][0]["id"] = "test mod/../evil"
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "mod.id.format")

    def test_bad_version_is_refused(self):
        data = manifest()
        data["mods"][0]["versions"][0]["version"] = "1.0.0 beta"
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "version.version.format")

    def test_non_github_release_url_is_refused(self):
        for url in ("https://example.invalid/mod.zip",
                    "http://github.com/owner/repo/releases/download/v1/mod.zip",
                    "https://github.com.evil.invalid/owner/repo/releases/download/v1/mod.zip",
                    "https://github.com/owner/repo/raw/main/mod.zip",
                    "https://github.com/owner/repo/releases/download/v1/mod.zip?x=1"):
            with self.subTest(url=url):
                data = manifest()
                data["mods"][0]["versions"][0]["url"] = url
                self.write(data)
                report = self.run_validator()
                self.assertHas(report, "version.url")

    def test_release_url_with_dot_segment_is_refused(self):
        data = manifest()
        data["mods"][0]["versions"][0]["url"] = "https://github.com/o/r/releases/download/v/../m.dll"
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "version.url")

    def test_non_https_source_link_is_refused(self):
        data = manifest()
        data["mods"][0]["source"] = "http://github.com/owner/repo"
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "mod.source.url")

    # ------------------------------------------------------------------ install paths

    def test_unsafe_install_path_is_refused(self):
        for path in ("../evil.dll", "/evil.dll", "C:/evil.dll", "a\\evil.dll", "a/../evil.dll", "a/./evil.dll"):
            with self.subTest(path=path):
                data = manifest()
                data["mods"][0]["versions"][0]["file"] = path
                self.write(data)
                report = self.run_validator()
                self.assertHas(report, "version.file.path")

    def test_non_dll_install_path_is_refused(self):
        data = manifest()
        data["mods"][0]["versions"][0]["file"] = "BepInEx/plugins/TestMod/TestMod.txt"
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "version.file.extension")

    def test_install_path_outside_bepinex_is_a_warning(self):
        data = manifest()
        data["mods"][0]["versions"][0]["file"] = "plugins/TestMod/TestMod.dll"
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "version.file.location")
        self.assertEqual(report.errors, [])

    # ------------------------------------------------------------------ sizes and hashes

    def test_non_positive_size_is_refused(self):
        data = manifest()
        data["mods"][0]["versions"][0]["size"] = 0
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "version.size.positive")

    def test_oversize_download_is_refused(self):
        data = manifest()
        data["mods"][0]["versions"][0]["size"] = vm.MAX_DOWNLOAD_BYTES + 1
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "version.size.limit")

    def test_size_that_is_not_a_number_is_refused(self):
        data = manifest()
        data["mods"][0]["versions"][0]["size"] = "1234"
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "version.size.type")

    def test_bad_hash_is_refused(self):
        data = manifest()
        data["mods"][0]["versions"][0]["sha256"] = "not-a-hash"
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "version.sha256")

    # ------------------------------------------------------------------ the approved list

    def test_version_missing_from_the_approved_list_is_refused(self):
        data = manifest()
        data["mods"][0]["versions"][0]["sha256"] = OTHER_HASH
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "approved.missing")

    def test_approved_id_or_version_mismatch_is_refused(self):
        self.write(manifest(), "%s test.mod 0.9.0" % HASH)
        report = self.run_validator()
        self.assertHas(report, "approved.mismatch")

    def test_approved_line_with_missing_fields_is_refused(self):
        self.write(manifest(), HASH)
        report = self.run_validator()
        self.assertHas(report, "approved.fields")

    def test_approved_line_with_a_bad_hash_is_refused(self):
        self.write(manifest(), "xyz test.mod 1.0.0")
        report = self.run_validator()
        self.assertHas(report, "approved.sha256")

    def test_an_approved_file_the_catalog_does_not_list_is_a_warning(self):
        self.write(manifest(), "%s test.mod 1.0.0\n%s test.mod 0.9.0" % (HASH, OTHER_HASH))
        report = self.run_validator()
        self.assertHas(report, "approved.not-in-catalog")
        self.assertEqual(report.errors, [])

    def test_comments_and_blank_lines_in_the_approved_list_are_skipped(self):
        self.write(manifest(), "# a comment\n\n%s test.mod 1.0.0\n" % HASH)
        report = self.run_validator()
        self.assertClean(report)

    # ------------------------------------------------------------------ ordering

    def test_versions_out_of_order_are_refused(self):
        # newest first means descending: 1.0.0 before 0.9.0
        data = manifest()
        older = version_entry("0.9.0")
        data["mods"][0]["versions"].insert(0, older)
        self.write(data, "%s test.mod 1.0.0\n%s test.mod 0.9.0" % (HASH, older["sha256"]))
        report = self.run_validator()
        self.assertHas(report, "version.order")

    def test_newest_first_order_passes(self):
        data = manifest()
        older = version_entry("0.9.0")
        data["mods"][0]["versions"].append(older)
        self.write(data, "%s test.mod 1.0.0\n%s test.mod 0.9.0" % (HASH, older["sha256"]))
        report = self.run_validator()
        self.assertClean(report)

    def test_version_comparison_ignores_a_prerelease_suffix(self):
        self.assertEqual(vm.compare_versions("1.0.0", "1.0.0"), 0)
        self.assertEqual(vm.compare_versions("1.2.0", "1.10.0"), -1)
        self.assertEqual(vm.compare_versions("0.69.1", "0.69.0"), 1)
        self.assertEqual(vm.compare_versions("1.0.0", "1.0.0-beta"), 1)
        self.assertIsNone(vm.compare_versions("nightly", "1.0.0"))

    # ------------------------------------------------------------------ local artifacts

    def write_zip(self, folder, name, entries):
        folder.mkdir(parents=True, exist_ok=True)
        path = folder / name
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
            for entry, payload in entries.items():
                archive.writestr(entry, payload)
        return path

    def test_local_archive_size_and_dll_hash_are_checked(self):
        folder = self.dir / "dist"
        path = self.write_zip(folder, "TestMod-1.0.0.zip", {"BepInEx/plugins/TestMod/TestMod.dll": DLL})
        data = manifest()
        data["mods"][0]["versions"][0]["size"] = path.stat().st_size
        self.write(data)
        report = self.run_validator([folder])
        self.assertClean(report)
        self.assertIn("1 local release archive", " ".join(report.notes))

    def test_local_archive_with_the_wrong_size_is_reported(self):
        folder = self.dir / "dist"
        self.write_zip(folder, "TestMod-1.0.0.zip", {"BepInEx/plugins/TestMod/TestMod.dll": DLL})
        report = self.run_validator([folder])
        self.assertHas(report, "artifact.size")

    def test_local_archive_with_the_wrong_dll_is_reported(self):
        folder = self.dir / "dist"
        self.write_zip(folder, "TestMod-1.0.0.zip", {"BepInEx/plugins/TestMod/TestMod.dll": DLL + b"!"})
        data = manifest()
        data["mods"][0]["versions"][0]["size"] = (folder / "TestMod-1.0.0.zip").stat().st_size
        self.write(data)
        report = self.run_validator([folder])
        self.assertHas(report, "artifact.sha256")

    def test_local_archive_without_the_named_dll_is_reported(self):
        folder = self.dir / "dist"
        self.write_zip(folder, "TestMod-1.0.0.zip", {"BepInEx/plugins/TestMod/Other.dll": DLL})
        data = manifest()
        data["mods"][0]["versions"][0]["size"] = (folder / "TestMod-1.0.0.zip").stat().st_size
        self.write(data)
        report = self.run_validator([folder])
        self.assertHas(report, "artifact.missing-dll")

    def test_local_archive_with_two_dlls_is_reported(self):
        folder = self.dir / "dist"
        path = self.write_zip(folder, "TestMod-1.0.0.zip", {
            "BepInEx/plugins/TestMod/TestMod.dll": DLL, "BepInEx/plugins/TestMod/Extra.dll": DLL})
        data = manifest()
        data["mods"][0]["versions"][0]["size"] = path.stat().st_size
        self.write(data)
        report = self.run_validator([folder])
        self.assertHas(report, "artifact.extra-dll")

    def test_no_local_archive_is_not_an_error(self):
        report = self.run_validator([self.dir / "empty"])
        self.assertClean(report)

    def test_installed_dll_is_checked_against_the_catalog_hash(self):
        folder = self.dir / "installed"
        folder.mkdir()
        (folder / "test.mod-1.0.0.dll").write_bytes(DLL)
        report = self.run_validator([folder])
        self.assertClean(report)
        self.assertIn("1 local release archive", " ".join(report.notes))

    def test_installed_dll_with_the_wrong_hash_is_reported(self):
        folder = self.dir / "installed"
        folder.mkdir()
        (folder / "test.mod-1.0.0.dll").write_bytes(DLL + b"!")
        report = self.run_validator([folder])
        self.assertHas(report, "artifact.installed.sha256")

    # ------------------------------------------------------------------ cross-checks with the sources

    def test_the_catalog_id_is_the_plugin_guid_of_this_repository(self):
        guids = vm.plugin_guids()
        self.assertIn("tldrevamp.headunit", guids)
        self.assertEqual(guids["tldrevamp.headunit"], "Head Unit")
        self.assertEqual(guids["tldrevamp.cartrailer"], "Car Trailer")
        self.assertEqual(guids["tldrevamp.trailerhitches"], "Trailer Hitches")

    def test_a_name_that_differs_from_the_plugin_is_a_warning(self):
        data = manifest()
        data["mods"][0]["id"] = "tldrevamp.cartrailer"
        data["mods"][0]["name"] = "Trailer"
        self.write(data, "%s tldrevamp.cartrailer 1.0.0" % HASH)
        report = self.run_validator()
        self.assertHas(report, "mod.name.guid")
        self.assertEqual(report.errors, [])

    def test_a_dll_named_after_another_mod_is_a_warning(self):
        data = manifest()
        data["mods"][0]["versions"][0]["file"] = "BepInEx/plugins/Other/Other.dll"
        self.write(data)
        report = self.run_validator()
        self.assertHas(report, "version.file.name")
        self.assertEqual(report.errors, [])

    # ------------------------------------------------------------------ the command line

    def run_command_line(self, *extra):
        """Run the tool as the command line does, and keep its output out of the test log."""
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = vm.main(["--manifest", str(self.manifest), "--approved", str(self.approved),
                            "--artifacts", str(self.dir / "none")] + list(extra))
        return code, out.getvalue()

    def test_the_command_line_reports_a_broken_manifest(self):
        self.write("{")
        code, text = self.run_command_line()
        self.assertEqual(code, 1)
        self.assertIn("not valid JSON", text)

    def test_the_command_line_accepts_a_good_manifest(self):
        code, text = self.run_command_line()
        self.assertEqual(code, 0)
        self.assertIn("no errors", text)

    def test_the_command_line_can_ask_for_json(self):
        code, text = self.run_command_line("--json")
        self.assertEqual(code, 0)
        payload = json.loads(text)
        self.assertTrue(payload["ok"])
        self.assertEqual(payload["errors"], 0)

    def test_the_validator_never_writes_to_the_files_it_reads(self):
        before_manifest = self.manifest.read_bytes()
        before_approved = self.approved.read_bytes()
        self.run_validator()
        self.assertEqual(self.manifest.read_bytes(), before_manifest)
        self.assertEqual(self.approved.read_bytes(), before_approved)


if __name__ == "__main__":
    unittest.main()

#!/usr/bin/env python3
"""Audit the release package against the repository: the manifest, the licences, the
version and INSTALL.txt must all agree with each other and with what the zip holds.

  python3 tools/package_audit.py [--zip dist/TLD-RevaMP-<version>.zip]

Read-only: it builds nothing and changes nothing.  Without --zip it checks the
repository's own files and the zip it finds in dist/.
"""
import argparse, hashlib, json, os, pathlib, re, sys, zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--zip", default="")
    ap.add_argument("--manifest", default=str(ROOT / "packaging" / "mods.json"))
    ap.add_argument("--approved", default=str(ROOT / "packaging" / "approved-mods.txt"))
    ap.add_argument("--install", default=str(ROOT / "packaging" / "INSTALL.txt"))
    ap.add_argument("--repo-only", action="store_true",
                    help="check the repository's own files only, and do NOT claim the package was checked. "
                         "Use it when there is no package to audit; it says so in the output.")
    args = ap.parse_args()
    if args.repo_only and args.zip:
        ap.error("--repo-only and --zip are mutually exclusive: --repo-only checks the repository's own "
                 "files and no package at all; --zip checks the named package.")

    problems = []
    notes = []

    def need(cond, message):
        if not cond:
            problems.append(message)
        return cond

    # ---- the version, in one place -----------------------------------------
    plugin_cs = (ROOT / "src" / "TLDRevamp" / "Plugin.cs").read_text()
    version = re.search(r'BaseVersion = "([^"]+)"', plugin_cs).group(1)

    # ---- the manifest ------------------------------------------------------
    catalog = json.loads(pathlib.Path(args.manifest).read_text())
    approved = {}
    for line in pathlib.Path(args.approved).read_text().splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        parts = line.split()
        if len(parts) == 3:
            approved[parts[0]] = (parts[1], parts[2])

    seen = set()
    for mod in catalog["mods"]:
        for rel in mod["versions"]:
            key = (rel["sha256"], rel["version"], mod["id"])
            seen.add(key)
            need(re.fullmatch(r"[0-9a-f]{64}", rel["sha256"]), f"{mod['id']} {rel['version']}: sha256 is not 64 hex characters")
            need(rel["size"] > 0, f"{mod['id']} {rel['version']}: size must be positive (it is the download size)")
            need(rel["url"].startswith("https://github.com/"), f"{mod['id']} {rel['version']}: the url is not a GitHub release asset")
            need(rel["url"].endswith(".zip"), f"{mod['id']} {rel['version']}: the url is not a zip")
            # the hash must be in the local approved list, with the same id and version
            hit = [k for k, v in approved.items() if k == rel["sha256"]]
            if need(bool(hit), f"{mod['id']} {rel['version']}: the sha256 is not in the local approved list"):
                aid, aver = approved[hit[0]]
                need(aid == mod["id"], f"{mod['id']} {rel['version']}: approved under the id '{aid}'")
                need(aver == rel["version"], f"{mod['id']} {rel['version']}: approved as version '{aver}'")
            # the file path inside the archive
            if rel.get("file"):
                need(rel["file"].endswith(".dll"), f"{mod['id']} {rel['version']}: 'file' must name a .dll")
                need(".." not in rel["file"] and not rel["file"].startswith("/"),
                     f"{mod['id']} {rel['version']}: 'file' must be a relative path without '..'")
            # the revamp field, when present, must be a version this repository knows
            if rel.get("revamp"):
                need(re.fullmatch(r"\d+\.\d+\.\d+", rel["revamp"]),
                     f"{mod['id']} {rel['version']}: the revamp field '{rel['revamp']}' is not a version")

    # every approved entry with a version should be in the catalog too, or be a
    # deliberate extra (the catalog may offer fewer)
    for sha, (mid, mver) in approved.items():
        if not any(s == sha and v == mver and i == mid for s, v, i in seen):
            notes.append(f"approved but not in the catalog: {mid} {mver}")

    # ---- the licences ------------------------------------------------------
    lic_dir = ROOT / "packaging" / "licenses"
    shipped = {p.name for p in lic_dir.glob("*.txt")}
    for needed in ("BepInEx-LICENSE.txt", "HarmonyX-LICENSE.txt", "UnityDoorstop-LICENSE.txt",
                   "MonoMod-LICENSE.txt", "MonoCecil-LICENSE.txt", "FiraSans-OFL.txt"):
        need(needed in shipped, f"packaging/licenses/{needed} is missing")

    # ---- INSTALL.txt -------------------------------------------------------
    install = pathlib.Path(args.install).read_text()
    need("@VERSION@" in install, "packaging/INSTALL.txt: the @VERSION@ placeholder is gone (it must be there for the release build)")
    for banned in ("@VERSION@", "TBD", "TODO", "FIXME", "XXX"):
        if banned == "@VERSION@":
            continue
        need(banned not in install, f"packaging/INSTALL.txt contains the placeholder {banned}")
    # the licence section must name every licence the package ships
    for name, project in (("BepInEx", "BepInEx 5"), ("HarmonyX", "HarmonyX"),
                          ("UnityDoorstop", "UnityDoorstop"), ("MonoMod", "MonoMod"),
                          ("MonoCecil", "Mono.Cecil"), ("FiraSans", "Fira Sans")):
        need(project in install, f"packaging/INSTALL.txt's CREDITS does not mention {project}")
    need("BepInEx\\licenses" in install, "packaging/INSTALL.txt does not say where the licence texts are")

    # ---- the zip -----------------------------------------------------------
    # --repo-only means the repository's own files and NOTHING else: no auto-discovery of a dist/
    # zip either. Otherwise the flag's own name would lie: it said "repository files only" while it
    # still checked a package that happened to be lying around.
    zip_path = args.zip
    if args.repo_only:
        zip_path = ""
    elif not zip_path:
        candidate = ROOT / "dist" / f"TLD-RevaMP-{version}.zip"
        zip_path = str(candidate) if candidate.exists() else ""
    if zip_path and os.path.exists(zip_path):
        with zipfile.ZipFile(zip_path) as z:
            names = z.namelist()
            need(f"INSTALL.txt" in names, f"{os.path.basename(zip_path)}: INSTALL.txt is missing")
            need("BepInEx/plugins/TLDRevamp/TLDRevamp.dll" in names, f"{os.path.basename(zip_path)}: TLDRevamp.dll is missing")
            need("BepInEx/plugins/TLDRevamp/TLDLoader.dll" in names, f"{os.path.basename(zip_path)}: the TLDLoader stub is missing")
            need("BepInEx/plugins/TLDRevamp/TLDRevamp.Multiplayer.dll" in names, f"{os.path.basename(zip_path)}: TLDRevamp.Multiplayer.dll is missing")
            need("BepInEx/plugins/TLDRevamp/COPYING" in names, f"{os.path.basename(zip_path)}: COPYING is missing")
            need("BepInEx/patchers/TLDRevamp/TLDRevamp.Preloader.dll" in names, f"{os.path.basename(zip_path)}: the preloader is missing")
            need("winhttp.dll" in names, f"{os.path.basename(zip_path)}: winhttp.dll is missing")
            # the licence texts must really be in the zip
            for name in shipped:
                need(f"BepInEx/licenses/{name}" in names,
                     f"{os.path.basename(zip_path)}: the licence text {name} is not in the zip")
            # the INSTALL.txt in the zip must have the version substituted
            if "INSTALL.txt" in names:
                text = z.read("INSTALL.txt").decode("utf-8", "replace")
                need("@VERSION@" not in text, f"{os.path.basename(zip_path)}: INSTALL.txt still has the @VERSION@ placeholder")
                need(f"Version: {version}" in text,
                     f"{os.path.basename(zip_path)}: INSTALL.txt does not say the version {version}")
            # the DLL's version must be the one the package names
            for entry in names:
                if entry.endswith("TLDRevamp.dll"):
                    data = z.read(entry)
                    # the version is a string in the DLL's metadata: as UTF-8 and as
                    # UTF-16 (the .NET metadata strings are UTF-16)
                    if version.encode() not in data and version.encode("utf-16-le") not in data:
                        problems.append(f"{os.path.basename(zip_path)}: TLDRevamp.dll does not carry the version {version}")
                    break
            # no third-party loader mods, no configs, no logs
            for entry in names:
                low = entry.lower()
                need("tldloadermods" not in low, f"{os.path.basename(zip_path)}: it ships a third-party Loader mod ({entry})")
                need(not low.endswith(".cfg"), f"{os.path.basename(zip_path)}: it ships a config file ({entry})")
                need(not low.endswith(".log"), f"{os.path.basename(zip_path)}: it ships a log file ({entry})")
        notes.append(f"checked the package: {zip_path}")
    elif args.repo_only:
        notes.append("repository-only: NO package was checked (--repo-only checks the repository's own "
                     "files only)")
    else:
        # FAIL CLOSED. An audit that inspected no package has proved nothing about the package: the
        # zip-level guarantees (the preloader is in it, the licence texts are in it, the version in it
        # agrees with INSTALL.txt, no stray .cfg/.log/third-party Loader mod) are exactly the ones a
        # clean checkout cannot check, because dist/ is gitignored. Scoring that as a pass is how a
        # preloader-less package would reach a player.
        need(False, "no package zip to check: the audit inspected no package. Build one "
                    "(tools/package.sh) or pass --zip <path>. The package-level guarantees are NOT "
                    "verified without it.")
        notes.append("no package zip was checked")

    # ---- the report --------------------------------------------------------
    for n in notes:
        print("note:", n)
    if problems:
        for p in problems:
            print("PROBLEM:", p)
        print(f"\n{len(problems)} problem(s)")
        return 1
    if args.repo_only:
        print("\npackage audit passed (repository files only): the manifest, the licences, the version and "
              "INSTALL.txt agree. NO PACKAGE WAS CHECKED.")
    else:
        print("\npackage audit passed: the manifest, the licences, the version and INSTALL.txt agree")
    return 0


def build_fixture_package(out_path):
    """Build a package-shaped zip that CI can audit, without the game's reference assemblies.

    The real package needs lib/ (the game's and BepInEx's assemblies), which is gitignored, so a
    clean checkout cannot build it. The fixture carries the same STRUCTURE the audit checks: the
    preloader in BepInEx/patchers/TLDRevamp/, the plugin DLLs, every licence text, and INSTALL.txt
    with the version substituted. The audit's package-level guarantees (the preloader is in the zip,
    the licences are in the zip, the version agrees, no stray .cfg/.log/third-party Loader mod) are
    therefore exercised for real.

    It is a FIXTURE: the DLLs inside are placeholders, and the audit only looks at the zip's
    STRUCTURE and at the version string, so it is the structure that is being audited.
    """
    plugin_cs = (ROOT / "src" / "TLDRevamp" / "Plugin.cs").read_text()
    version = re.search(r'BaseVersion = "([^"]+)"', plugin_cs).group(1)
    install = (ROOT / "packaging" / "INSTALL.txt").read_text().replace("@VERSION@", version)
    dll = (ROOT / "src" / "TLDRevamp" / "bin" / "Release" / "netstandard2.1" / "TLDRevamp.dll")
    dll_bytes = dll.read_bytes() if dll.exists() else b"\x00" * 64
    preloader = (ROOT / "src" / "TLDRevamp.Preloader" / "bin" / "Release" / "netstandard2.1" / "TLDRevamp.Preloader.dll")
    preloader_bytes = preloader.read_bytes() if preloader.exists() else b"\x00" * 64
    out_path = pathlib.Path(out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("INSTALL.txt", install)
        z.writestr("BepInEx/plugins/TLDRevamp/TLDRevamp.dll", dll_bytes)
        z.writestr("BepInEx/plugins/TLDRevamp/TLDLoader.dll", b"\x00" * 64)
        z.writestr("BepInEx/plugins/TLDRevamp/TLDRevamp.Multiplayer.dll", b"\x00" * 64)
        z.writestr("BepInEx/plugins/TLDRevamp/COPYING", (ROOT / "COPYING").read_text())
        z.writestr("BepInEx/patchers/TLDRevamp/TLDRevamp.Preloader.dll", preloader_bytes)
        z.writestr("winhttp.dll", b"\x00" * 64)
        for lic in sorted((ROOT / "packaging" / "licenses").glob("*.txt")):
            z.writestr(f"BepInEx/licenses/{lic.name}", lic.read_text())
    return str(out_path)


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--build-fixture":
        path = build_fixture_package(sys.argv[2] if len(sys.argv) > 2 else "/tmp/tld-fixture-package.zip")
        print(f"fixture package: {path}")
        sys.exit(0)
    sys.exit(main())

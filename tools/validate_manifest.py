#!/usr/bin/env python3
"""Check the release manifest before it goes out: packaging/mods.json and packaging/approved-mods.txt.

The in-game mod browser (src/TLDRevamp/Net/ModCatalog.cs) reads the catalog, and a joiner
(src/TLDRevamp/Net/ModTransfer.cs) trusts the approved list that ships with RevaMP. Both refuse
anything they do not understand, and they refuse it in the player's face: an unapproved version is
greyed out, a catalog entry with a bad URL disappears, a version list in the wrong order offers the
wrong file. This tool finds those mistakes before a release, offline, without touching either file.

  tools/validate_manifest.py                                  # this repository's own manifest
  tools/validate_manifest.py --manifest a.json --approved b.txt
  tools/validate_manifest.py --artifacts dist                 # also check local release zips
  tools/validate_manifest.py --check-links                    # opt-in: ask the web whether the links answer
  tools/validate_manifest.py --json                           # machine-readable output

The rules mirror the game, so a manifest that passes here is one the game can actually use:
  * catalog format 1, a "mods" list, at most 1 MiB (ModCatalog.MaxCatalogBytes)
  * ids are A-Za-z0-9_.- up to 64 characters (MpApi.ValidId)
  * versions are A-Za-z0-9_.- up to 64 characters (ModFiles.SafeVersion)
  * release URLs are HTTPS GitHub release assets only (ModCatalog.ReleaseUrl)
  * "file" is a safe relative path ending in .dll (ModCatalog.SafeEntry)
  * "size" is the download size, at most 16 MiB (ModTransfer.MaxBytes); "sha256" is the DLL's
  * every catalog version must be approved with the same id and version (ModCatalog.ApprovalReason)
  * versions are newest first, so the newest approved one is what the browser offers

By default it never opens a network connection and never writes to the files it reads. Local
release zips are read only when they are already on disk and named after the asset in the URL.
--check-links is the one exception: it asks the web whether each link answers, so use it only when
a network is wanted.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
import sys
import unicodedata
import urllib.parse
import zipfile
from typing import Dict, List, Optional, Sequence, Tuple

ROOT = pathlib.Path(__file__).resolve().parent.parent
DEFAULT_MANIFEST = ROOT / "packaging" / "mods.json"
DEFAULT_APPROVED = ROOT / "packaging" / "approved-mods.txt"
DEFAULT_ARTIFACTS = ROOT / "dist"
PLUGIN_CS = ROOT / "src" / "TLDRevamp" / "Plugin.cs"

CATALOG_FORMAT = 1
MAX_CATALOG_BYTES = 1 << 20          # ModCatalog.MaxCatalogBytes
MAX_DOWNLOAD_BYTES = 16 << 20        # ModTransfer.MaxBytes
ID_RE = re.compile(r"\A[A-Za-z0-9_.-]{1,64}\Z")
VERSION_RE = re.compile(r"\A[A-Za-z0-9_.-]{1,64}\Z")
SHA256_RE = re.compile(r"\A[0-9a-f]{64}\Z")
RELEASE_URL_RE = re.compile(
    r"\Ahttps://github\.com/[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+/releases/download/[^/\s?#\\]+/[^/\s?#\\]+\Z")
SEMVER_RE = re.compile(r"\A(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?\Z")
BASE_VERSION_RE = re.compile(r'BaseVersion\s*=\s*"([^"]+)"')

MODS_DIR = ROOT / "src" / "Mods"
GUID_RE = re.compile(r'BepInPlugin\(\s*"(?P<guid>[A-Za-z0-9_.-]{1,64})"')
GUID_CONST_RE = re.compile(r'BepInPlugin\(\s*(?P<name_const>Guid)\s*,')
GUID_VALUE_RE = re.compile(r'const\s+string\s+(?P<name>Guid)\s*=\s*"(?P<guid>[A-Za-z0-9_.-]{1,64})"')
PLUGIN_NAME_RE = re.compile(r'BepInPlugin\(\s*(?:"[^"]*"|Guid)\s*,\s*"(?P<name>[^"]+)"')

MOD_KEYS = {"id", "name", "author", "description", "source", "versions"}
VERSION_KEYS = {"version", "url", "file", "sha256", "size", "revamp"}
REQUIRED_MOD_KEYS = ("id", "name", "versions")
REQUIRED_VERSION_KEYS = ("version", "url", "sha256", "size")

SEVERITY_ERROR = "error"
SEVERITY_WARNING = "warning"


def finding(severity: str, code: str, path: str, message: str, hint: str = "") -> Dict[str, str]:
    return {"severity": severity, "code": code, "path": path, "message": message, "hint": hint}


# ----------------------------------------------------------------------------- the game's own rules

def safe_entry(path: str) -> bool:
    """ModCatalog.SafeEntry: no rooted path, no backslash, no drive letter, no dot segment."""
    if not path or path[0] == "/" or "\\" in path or ":" in path:
        return False
    for part in path.rstrip("/").split("/"):
        if not part or part.endswith(".") or part.endswith(" "):
            return False
        if any(unicodedata.category(c) == "Cc" for c in part):
            return False
    return True


def release_url_problem(url: str) -> Optional[str]:
    """None when the URL is an acceptable GitHub release asset, else why it is not."""
    if not RELEASE_URL_RE.match(url):
        return ("not an HTTPS GitHub release asset URL "
                "(https://github.com/owner/repo/releases/download/tag/asset.zip)")
    try:
        parts = urllib.parse.urlsplit(url)
        port = parts.port
    except ValueError:
        return "the URL could not be parsed"
    if parts.scheme != "https" or parts.hostname != "github.com":
        return "the host is not github.com"
    if port not in (None, 443):
        return "the port is not 443"
    if parts.username or parts.password:
        return "the URL carries credentials"
    if parts.query or parts.fragment:
        return "the URL has a query or a fragment"
    rest = url[len("https://github.com/"):]
    if not safe_entry(rest) or not safe_entry(urllib.parse.unquote(rest)):
        return "the path has a dot segment or an unsafe character"
    return None


def source_url_problem(url: str) -> Optional[str]:
    """None when the link is a usable HTTPS source page, else why it is not."""
    try:
        parts = urllib.parse.urlsplit(url)
        port = parts.port
    except ValueError:
        return "the link could not be parsed"
    if parts.scheme != "https":
        return "the link is not HTTPS"
    if not parts.hostname:
        return "the link has no host"
    if parts.username or parts.password:
        return "the link carries credentials"
    if port not in (None, 443):
        return "the port is not 443"
    return None


def _prerelease_key(pre: str) -> Tuple:
    parts = []
    for ident in pre.split("."):
        if ident.isdigit():
            parts.append((0, int(ident), ""))
        else:
            parts.append((1, 0, ident))
    return tuple(parts)


def semver_key(version: str) -> Optional[Tuple]:
    match = SEMVER_RE.match(version)
    if not match:
        return None
    major, minor, patch, pre, _build = match.groups()
    return (int(major), int(minor or 0), int(patch or 0), 1 if pre is None else 0,
            _prerelease_key(pre) if pre is not None else ())


def compare_versions(left: str, right: str) -> Optional[int]:
    """-1/0/1, or None when either version is not a plain number.version[.patch]."""
    a, b = semver_key(left), semver_key(right)
    if a is None or b is None:
        return None
    return (a > b) - (a < b)


def github_owner_repo(url: str) -> Optional[Tuple[str, str]]:
    """The owner and repository of a github.com/owner/repo URL."""
    if not url.startswith("https://github.com/"):
        return None
    parts = url[len("https://github.com/"):].split("/")
    if len(parts) < 2 or not parts[0] or not parts[1]:
        return None
    return parts[0], parts[1]


# ----------------------------------------------------------------------------- the approved list

class ApprovedEntry:
    __slots__ = ("sha256", "mod_id", "version", "line")

    def __init__(self, sha256: str, mod_id: str, version: str, line: int) -> None:
        self.sha256, self.mod_id, self.version, self.line = sha256, mod_id, version, line

    def describe(self) -> str:
        return "%s %s %s (line %d)" % (self.mod_id, self.version, self.sha256[:12], self.line)


def parse_approved(path: pathlib.Path) -> Tuple[List[ApprovedEntry], List[Dict[str, str]]]:
    """Read approved-mods.txt the way ModTransfer.Approved() does, and report the lines it cannot use."""
    entries: List[ApprovedEntry] = []
    problems: List[Dict[str, str]] = []
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as exc:
        problems.append(finding(SEVERITY_ERROR, "approved.unreadable", "approved-mods.txt",
                                "the approved list could not be read: %s" % exc,
                                "check the path and the file's permissions"))
        return entries, problems
    for number, raw in enumerate(text.splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        where = "approved-mods.txt:%d" % number
        parts = line.split()
        if len(parts) != 3:
            problems.append(finding(SEVERITY_ERROR, "approved.fields", where,
                                    "expected 'sha256 mod-id version', found %d field(s)" % len(parts),
                                    "the game reads a hash and then exactly the id and the version"))
            continue
        digest, mod_id, version = parts
        if not SHA256_RE.match(digest):
            problems.append(finding(SEVERITY_ERROR, "approved.sha256", where,
                                    "'%s' is not 64 lower-case hex characters" % digest,
                                    "the hash is the SHA-256 of the mod DLL"))
        if not ID_RE.match(mod_id):
            problems.append(finding(SEVERITY_ERROR, "approved.id", where,
                                    "'%s' is not a valid mod id" % mod_id,
                                    "ids use letters, digits, dot, dash and underscore"))
        if not VERSION_RE.match(version):
            problems.append(finding(SEVERITY_ERROR, "approved.version", where,
                                    "'%s' is not a valid mod version" % version,
                                    "versions use letters, digits, dot, dash and underscore"))
        entries.append(ApprovedEntry(digest, mod_id, version, number))
    return entries, problems


# ----------------------------------------------------------------------------- the checks

def check_catalog_shape(raw: bytes, findings: List[Dict[str, str]]) -> Optional[dict]:
    if len(raw) > MAX_CATALOG_BYTES:
        findings.append(finding(SEVERITY_ERROR, "catalog.size", "catalog",
                                "the catalog is %d bytes, the game refuses more than %d" % (len(raw), MAX_CATALOG_BYTES),
                                "split the catalog or publish fewer mods"))
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as exc:
        findings.append(finding(SEVERITY_ERROR, "catalog.encoding", "catalog",
                                "the catalog is not UTF-8: %s" % exc, "save the file as UTF-8"))
        return None
    try:
        data = json.loads(text)
    except json.JSONDecodeError as exc:
        findings.append(finding(SEVERITY_ERROR, "catalog.json", "catalog",
                                "the catalog is not valid JSON: %s" % exc,
                                "line %d column %d" % (exc.lineno, exc.colno)))
        return None
    if not isinstance(data, dict):
        findings.append(finding(SEVERITY_ERROR, "catalog.type", "catalog",
                                "the catalog must be a JSON object, found %s" % type(data).__name__, ""))
        return None
    for key in sorted(data):
        if key not in ("format", "mods"):
            findings.append(finding(SEVERITY_WARNING, "catalog.unknown-key", key,
                                    "'%s' is not a field the game reads" % key,
                                    "the game ignores unknown fields, so this is likely a typo"))
    if "format" not in data:
        findings.append(finding(SEVERITY_ERROR, "catalog.format.missing", "format",
                                "the catalog has no 'format' field",
                                "the game only reads format 1"))
    elif data["format"] != CATALOG_FORMAT:
        findings.append(finding(SEVERITY_ERROR, "catalog.format", "format",
                                "format is %r, the game only reads %d" % (data["format"], CATALOG_FORMAT),
                                "the catalog format is not changed by hand"))
    if "mods" not in data:
        findings.append(finding(SEVERITY_ERROR, "catalog.mods.missing", "mods",
                                "the catalog has no 'mods' list", "the game needs a list of mods"))
        return None
    if not isinstance(data["mods"], list):
        findings.append(finding(SEVERITY_ERROR, "catalog.mods.type", "mods",
                                "'mods' must be a list, found %s" % type(data["mods"]).__name__, ""))
        return None
    if not data["mods"]:
        findings.append(finding(SEVERITY_WARNING, "catalog.mods.empty", "mods",
                                "the catalog lists no mods", "the browser would say the catalog is empty"))
    return data


def check_mod(mod: dict, index: int, findings: List[Dict[str, str]]) -> None:
    where = "mods[%d]" % index
    for key in sorted(mod):
        if key not in MOD_KEYS:
            findings.append(finding(SEVERITY_WARNING, "mod.unknown-key", "%s.%s" % (where, key),
                                    "'%s' is not a field the game reads" % key,
                                    "the game ignores unknown fields, so this is likely a typo"))
    for key in REQUIRED_MOD_KEYS:
        if key not in mod:
            findings.append(finding(SEVERITY_ERROR, "mod.%s.missing" % key, "%s.%s" % (where, key),
                                    "the mod has no '%s'" % key, "every mod needs an id, a name and versions"))
    for key in ("author", "description", "source"):
        if key not in mod:
            findings.append(finding(SEVERITY_ERROR, "mod.%s.missing" % key, "%s.%s" % (where, key),
                                    "the mod has no '%s'" % key,
                                    "the browser shows it to the player"))
        elif not isinstance(mod[key], str):
            findings.append(finding(SEVERITY_ERROR, "mod.%s.type" % key, "%s.%s" % (where, key),
                                    "'%s' must be a string, found %s" % (key, type(mod[key]).__name__), ""))
    for key in ("id", "name"):
        if key not in mod:
            continue
        value = mod[key]
        if not isinstance(value, str):
            findings.append(finding(SEVERITY_ERROR, "mod.%s.type" % key, "%s.%s" % (where, key),
                                    "'%s' must be a string, found %s" % (key, type(value).__name__), ""))
        elif not value.strip():
            findings.append(finding(SEVERITY_ERROR, "mod.%s.empty" % key, "%s.%s" % (where, key),
                                    "'%s' is empty" % key, ""))
    for key in ("author", "description"):
        value = mod.get(key)
        if isinstance(value, str) and not value.strip():
            findings.append(finding(SEVERITY_WARNING, "mod.%s.empty" % key, "%s.%s" % (where, key),
                                    "'%s' is empty" % key, "the browser shows it to the player"))
    mod_id = mod.get("id")
    if isinstance(mod_id, str):
        if not ID_RE.match(mod_id):
            findings.append(finding(SEVERITY_ERROR, "mod.id.format", "%s.id" % where,
                                    "'%s' is not a valid mod id" % mod_id,
                                    "ids use letters, digits, dot, dash and underscore, at most 64 characters"))
        elif mod_id != mod_id.lower():
            findings.append(finding(SEVERITY_WARNING, "mod.id.case", "%s.id" % where,
                                    "the id '%s' has upper-case letters" % mod_id,
                                    "a BepInEx plugin GUID is normally lower-case"))
    source = mod.get("source")
    if isinstance(source, str) and source.strip():
        problem = source_url_problem(source)
        if problem:
            findings.append(finding(SEVERITY_ERROR, "mod.source.url", "%s.source" % where,
                                    "the source link is not usable: %s" % problem,
                                    "the browser only opens HTTPS links"))
    versions = mod.get("versions")
    if versions is None:
        return
    if not isinstance(versions, list):
        findings.append(finding(SEVERITY_ERROR, "mod.versions.type", "%s.versions" % where,
                                "'versions' must be a list, found %s" % type(versions).__name__, ""))
        return
    if not versions:
        findings.append(finding(SEVERITY_WARNING, "mod.versions.empty", "%s.versions" % where,
                                "the mod has no versions", "the browser drops a mod with no versions"))
    seen_versions = set()
    for position, version in enumerate(versions):
        check_version(mod, version, position, where, seen_versions, findings)


def check_version(mod: dict, version: object, position: int, where: str,
                  seen_versions: set, findings: List[Dict[str, str]]) -> None:
    spot = "%s.versions[%d]" % (where, position)
    if not isinstance(version, dict):
        findings.append(finding(SEVERITY_ERROR, "version.type", spot,
                                "a version must be an object, found %s" % type(version).__name__, ""))
        return
    for key in sorted(version):
        if key not in VERSION_KEYS:
            findings.append(finding(SEVERITY_WARNING, "version.unknown-key", "%s.%s" % (spot, key),
                                    "'%s' is not a field the game reads" % key,
                                    "the game ignores unknown fields, so this is likely a typo"))
    for key in REQUIRED_VERSION_KEYS:
        if key not in version:
            findings.append(finding(SEVERITY_ERROR, "version.%s.missing" % key, "%s.%s" % (spot, key),
                                    "the version has no '%s'" % key,
                                    "a release needs a version, a URL, a SHA-256 and a size"))
    if "version" not in version:
        number = None
    else:
        number = version["version"]
        if not isinstance(number, str):
            findings.append(finding(SEVERITY_ERROR, "version.version.type", "%s.version" % spot,
                                    "'version' must be a string, found %s" % type(number).__name__, ""))
        else:
            if not VERSION_RE.match(number):
                findings.append(finding(SEVERITY_ERROR, "version.version.format", "%s.version" % spot,
                                        "'%s' is not a valid mod version" % number,
                                        "versions use letters, digits, dot, dash and underscore, "
                                        "at most 64 characters"))
            if number in seen_versions:
                findings.append(finding(SEVERITY_ERROR, "version.duplicate", "%s.version" % spot,
                                        "version %s is listed twice for this mod" % number,
                                        "each version of a mod appears once"))
            seen_versions.add(number)
    url = version.get("url")
    if isinstance(url, str):
        problem = release_url_problem(url)
        if problem:
            findings.append(finding(SEVERITY_ERROR, "version.url", "%s.url" % spot,
                                    "the release URL is not allowed: %s" % problem,
                                    "only GitHub release assets can be downloaded"))
        else:
            asset = url.rsplit("/", 1)[-1]
            if isinstance(number, str) and number and number not in asset:
                findings.append(finding(SEVERITY_WARNING, "version.url.asset-name", "%s.url" % spot,
                                        "the asset name '%s' does not mention version %s" % (asset, number),
                                        "the wrong asset was uploaded when a name does not match the version"))
            owner_repo = github_owner_repo(url)
            source = mod.get("source")
            if owner_repo and isinstance(source, str) and source.startswith("https://github.com/"):
                source_repo = github_owner_repo(source)
                if source_repo and source_repo != owner_repo:
                    findings.append(finding(SEVERITY_WARNING, "version.url.repository", "%s.url" % spot,
                                            "the release is on %s/%s but the source link points at %s/%s" % (
                                                owner_repo[0], owner_repo[1], source_repo[0], source_repo[1]),
                                            "the source link is where a player asks for help"))
    elif url is not None:
        findings.append(finding(SEVERITY_ERROR, "version.url.type", "%s.url" % spot,
                                "'url' must be a string, found %s" % type(url).__name__, ""))
    digest = version.get("sha256")
    if isinstance(digest, str):
        if not SHA256_RE.match(digest):
            findings.append(finding(SEVERITY_ERROR, "version.sha256", "%s.sha256" % spot,
                                    "'%s' is not 64 lower-case hex characters" % digest,
                                    "the hash is the SHA-256 of the mod DLL, not of the archive"))
    elif digest is not None:
        findings.append(finding(SEVERITY_ERROR, "version.sha256.type", "%s.sha256" % spot,
                                "'sha256' must be a string, found %s" % type(digest).__name__, ""))
    size = version.get("size")
    if isinstance(size, bool) or not isinstance(size, int):
        findings.append(finding(SEVERITY_ERROR, "version.size.type", "%s.size" % spot,
                                "'size' must be a whole number of bytes, found %r" % (size,), ""))
    elif size <= 0:
        findings.append(finding(SEVERITY_ERROR, "version.size.positive", "%s.size" % spot,
                                "the size is %d bytes" % size, "a release has at least one byte"))
    elif size > MAX_DOWNLOAD_BYTES:
        findings.append(finding(SEVERITY_ERROR, "version.size.limit", "%s.size" % spot,
                                "the size is %d bytes, the game refuses more than %d" % (size, MAX_DOWNLOAD_BYTES),
                                "publish a smaller archive"))
    file_name = version.get("file")
    if file_name is not None:
        if not isinstance(file_name, str):
            findings.append(finding(SEVERITY_ERROR, "version.file.type", "%s.file" % spot,
                                    "'file' must be a string, found %s" % type(file_name).__name__, ""))
        elif file_name:
            if not safe_entry(file_name):
                findings.append(finding(SEVERITY_ERROR, "version.file.path", "%s.file" % spot,
                                        "'%s' is not a safe relative path" % file_name,
                                        "no drive letter, no leading slash, no '..' and no backslash"))
            elif not file_name.lower().endswith(".dll"):
                findings.append(finding(SEVERITY_ERROR, "version.file.extension", "%s.file" % spot,
                                        "'%s' does not end in .dll" % file_name,
                                        "the game only installs a DLL"))
            elif not file_name.startswith("BepInEx/"):
                findings.append(finding(SEVERITY_WARNING, "version.file.location", "%s.file" % spot,
                                        "'%s' is outside BepInEx/" % file_name,
                                        "a plugin DLL belongs under BepInEx/plugins/"))
    revamp = version.get("revamp")
    if revamp is not None:
        if not isinstance(revamp, str):
            findings.append(finding(SEVERITY_ERROR, "version.revamp.type", "%s.revamp" % spot,
                                    "'revamp' must be a string, found %s" % type(revamp).__name__, ""))
        elif not VERSION_RE.match(revamp):
            findings.append(finding(SEVERITY_WARNING, "version.revamp.format", "%s.revamp" % spot,
                                    "'%s' is not a version number" % revamp,
                                    "'revamp' is the RevaMP version the mod needs"))


def check_ordering(data: dict, findings: List[Dict[str, str]]) -> None:
    """The browser offers the first approved version in the list, so the list must be newest first."""
    for index, mod in enumerate(data.get("mods") or []):
        if not isinstance(mod, dict):
            continue
        versions = mod.get("versions")
        if not isinstance(versions, list):
            continue
        previous = None
        for position, version in enumerate(versions):
            if not isinstance(version, dict):
                continue
            number = version.get("version")
            if not isinstance(number, str):
                continue
            spot = "mods[%d].versions[%d].version" % (index, position)
            if previous is not None:
                order = compare_versions(previous, number)
                if order is None:
                    if semver_key(previous) is None or semver_key(number) is None:
                        findings.append(finding(SEVERITY_WARNING, "version.order.unknown", spot,
                                                "'%s' and '%s' are not plain version numbers, so the order "
                                                "cannot be checked" % (previous, number),
                                                "the browser offers the first approved version it finds"))
                elif order <= 0:
                    findings.append(finding(SEVERITY_ERROR, "version.order", spot,
                                            "version %s comes after %s, the list must be newest first" % (
                                                number, previous),
                                            "the browser offers the newest approved version, and that is the "
                                            "first one the player sees"))
            previous = number


def check_approved_against_catalog(data: dict, entries: Sequence[ApprovedEntry],
                                   findings: List[Dict[str, str]]) -> Dict[str, int]:
    by_hash: Dict[str, ApprovedEntry] = {}
    for entry in entries:
        first = by_hash.get(entry.sha256)
        if first is not None:
            findings.append(finding(SEVERITY_ERROR, "approved.duplicate-hash", "approved-mods.txt:%d" % entry.line,
                                    "the hash of %s is already on line %d" % (entry.mod_id, first.line),
                                    "one hash approves one file; a second line silently replaces the first"))
        else:
            by_hash[entry.sha256] = entry
    by_pair: Dict[Tuple[str, str], ApprovedEntry] = {}
    for entry in entries:
        pair = (entry.mod_id, entry.version)
        first = by_pair.get(pair)
        if first is not None and first.sha256 != entry.sha256:
            findings.append(finding(SEVERITY_ERROR, "approved.duplicate-version", "approved-mods.txt:%d" % entry.line,
                                    "%s %s is approved twice with different files" % pair,
                                    "one version is one file, so one hash"))
        else:
            by_pair[pair] = entry
    catalog_pairs = set()
    for mod in data.get("mods") or []:
        if not isinstance(mod, dict):
            continue
        mod_id = mod.get("id")
        versions = mod.get("versions")
        if not isinstance(mod_id, str) or not isinstance(versions, list):
            continue
        for version in versions:
            if isinstance(version, dict) and isinstance(version.get("version"), str):
                catalog_pairs.add((mod_id, version["version"]))
    used = 0
    for index, mod in enumerate(data.get("mods") or []):
        if not isinstance(mod, dict):
            continue
        mod_id = mod.get("id")
        versions = mod.get("versions")
        if not isinstance(versions, list) or not isinstance(mod_id, str):
            continue
        for position, version in enumerate(versions):
            if not isinstance(version, dict):
                continue
            digest = version.get("sha256")
            number = version.get("version")
            spot = "mods[%d].versions[%d]" % (index, position)
            if not isinstance(digest, str) or not isinstance(number, str):
                continue
            entry = by_hash.get(digest)
            if entry is None:
                findings.append(finding(SEVERITY_ERROR, "approved.missing", "%s.sha256" % spot,
                                        "the file of %s %s is not on the approved list" % (mod_id, number),
                                        "a joiner never downloads a file that is not approved; add the line to "
                                        "packaging/approved-mods.txt (tools/package-mods.sh prints it)"))
                continue
            if entry.mod_id != mod_id or entry.version != number:
                findings.append(finding(SEVERITY_ERROR, "approved.mismatch", "%s.sha256" % spot,
                                        "the approved list has %s %s for this file, the catalog says %s %s" % (
                                            entry.mod_id, entry.version, mod_id, number),
                                        "the game refuses a file whose id or version differs"))
                continue
            used += 1
    for entry in entries:
        if (entry.mod_id, entry.version) not in catalog_pairs:
            findings.append(finding(SEVERITY_WARNING, "approved.not-in-catalog", "approved-mods.txt:%d" % entry.line,
                                    "%s %s is approved but the catalog does not list it" % (entry.mod_id, entry.version),
                                    "an approved file the catalog does not offer is never installed from the browser; "
                                    "it stays approved for a host transfer"))
    return {"approved": len(entries), "used": used}


def check_artifacts(data: dict, directories: Sequence[pathlib.Path],
                    findings: List[Dict[str, str]]) -> int:
    """Check local release zips that are already on disk: their size, their DLL and its hash."""
    checked = 0
    for index, mod in enumerate(data.get("mods") or []):
        if not isinstance(mod, dict):
            continue
        versions = mod.get("versions")
        if not isinstance(versions, list):
            continue
        for position, version in enumerate(versions):
            if not isinstance(version, dict):
                continue
            url = version.get("url")
            if not isinstance(url, str) or not url:
                continue
            asset = url.rsplit("/", 1)[-1]
            if not asset:
                continue
            local = find_artifact(directories, asset)
            spot = "mods[%d].versions[%d]" % (index, position)
            if local is None:
                # no archive: the mod may still be installed as a DLL (ModFiles.Target names it <id>-<version>.dll)
                installed = find_installed_dll(directories, mod, version)
                if installed is not None:
                    checked += 1
                    check_installed_dll(installed, version, spot, findings)
                continue
            checked += 1
            declared_size = version.get("size")
            actual_size = local.stat().st_size
            if isinstance(declared_size, int) and not isinstance(declared_size, bool) and declared_size != actual_size:
                findings.append(finding(SEVERITY_ERROR, "artifact.size", "%s.size" % spot,
                                        "the local %s is %d bytes, the catalog says %d" % (
                                            local.name, actual_size, declared_size),
                                        "'size' is the download size; the browser refuses a download of a different "
                                        "length"))
            declared_hash = version.get("sha256")
            file_name = version.get("file")
            if not isinstance(declared_hash, str) or not isinstance(file_name, str):
                continue
            if file_name:
                problems = check_zip(local, file_name, declared_hash, spot, findings)
                if problems is not None:
                    continue
            else:
                digest = hashlib.sha256(local.read_bytes()).hexdigest()
                if digest != declared_hash:
                    findings.append(finding(SEVERITY_ERROR, "artifact.sha256", "%s.sha256" % spot,
                                            "the local %s hashes to %s, the catalog says %s" % (
                                                local.name, digest, declared_hash),
                                            "the game refuses a file whose hash differs"))
    return checked


def check_zip(local: pathlib.Path, file_name: str, declared_hash: str, spot: str,
              findings: List[Dict[str, str]]) -> Optional[str]:
    try:
        archive = zipfile.ZipFile(local)
    except (zipfile.BadZipFile, OSError) as exc:
        findings.append(finding(SEVERITY_ERROR, "artifact.zip", spot,
                                "the local %s is not a readable zip: %s" % (local.name, exc),
                                "the catalog's URL points at a zip"))
        return "bad"
    with archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            findings.append(finding(SEVERITY_ERROR, "artifact.duplicate-entry", "%s.file" % spot,
                                    "the local %s has the same entry twice" % local.name,
                                    "the game refuses an archive with a duplicate entry"))
        for name in names:
            if not safe_entry(name):
                findings.append(finding(SEVERITY_ERROR, "artifact.unsafe-entry", "%s.file" % spot,
                                        "the local %s has the unsafe entry '%s'" % (local.name, name),
                                        "the game refuses an archive with an unsafe path"))
        dlls = [name for name in names if name.lower().endswith(".dll")]
        if len(dlls) > 1:
            findings.append(finding(SEVERITY_ERROR, "artifact.extra-dll", "%s.file" % spot,
                                    "the local %s holds %d DLLs, the game allows one" % (local.name, len(dlls)),
                                    "a mod with more than one DLL is installed by hand"))
        if file_name not in names:
            findings.append(finding(SEVERITY_ERROR, "artifact.missing-dll", "%s.file" % spot,
                                    "the local %s has no '%s'" % (local.name, file_name),
                                    "the catalog names the DLL inside the archive"))
            return "missing"
        digest = hashlib.sha256(archive.read(file_name)).hexdigest()
        if digest != declared_hash:
            findings.append(finding(SEVERITY_ERROR, "artifact.sha256", "%s.sha256" % spot,
                                    "the '%s' in the local %s hashes to %s, the catalog says %s" % (
                                        file_name, local.name, digest, declared_hash),
                                    "the game refuses a file whose hash differs"))
    return None


def find_installed_dll(directories: Sequence[pathlib.Path], mod: dict, version: dict) -> Optional[pathlib.Path]:
    """The managed install of a mod: <id>-<version>.dll (ModFiles.Target), or the DLL the catalog names."""
    mod_id = mod.get("id")
    number = version.get("version")
    names = []
    if isinstance(mod_id, str) and isinstance(number, str):
        names.append("%s-%s.dll" % (mod_id, number))
    file_name = version.get("file")
    if isinstance(file_name, str) and file_name:
        names.append(file_name.rsplit("/", 1)[-1])
    for directory in directories:
        if not directory.is_dir():
            continue
        for name in names:
            for found in sorted(directory.rglob(name)):
                if found.is_file():
                    return found
    return None


def check_installed_dll(local: pathlib.Path, version: dict, spot: str,
                        findings: List[Dict[str, str]]) -> None:
    """The installed DLL must hash to the catalog's SHA-256: it is what a host would send."""
    declared_hash = version.get("sha256")
    if not isinstance(declared_hash, str):
        return
    digest = hashlib.sha256(local.read_bytes()).hexdigest()
    if digest != declared_hash:
        findings.append(finding(SEVERITY_ERROR, "artifact.installed.sha256", "%s.sha256" % spot,
                                "the installed %s hashes to %s, the catalog says %s" % (
                                    local.name, digest, declared_hash),
                                "a host sends the installed file; a joiner refuses a file whose hash differs"))


def find_artifact(directories: Sequence[pathlib.Path], asset: str) -> Optional[pathlib.Path]:
    for directory in directories:
        if not directory.is_dir():
            continue
        candidate = directory / asset
        if candidate.is_file():
            return candidate
        for found in sorted(directory.rglob(asset)):
            if found.is_file():
                return found
    return None


def check_links(data: dict, timeout: float, findings: List[Dict[str, str]]) -> None:
    """Opt-in: ask the web whether a link answers. Never called unless --check-links is given."""
    import urllib.error
    import urllib.request

    def head(url: str) -> Optional[str]:
        request = urllib.request.Request(url, method="HEAD", headers={"User-Agent": "TLD-RevaMP-manifest-check"})
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return None if 200 <= response.status < 400 else "the server answered %d" % response.status
        except urllib.error.HTTPError as exc:
            if exc.code in (403, 405):   # some hosts refuse HEAD but serve the file
                return head_get(url)
            return "the server answered %d" % exc.code
        except Exception as exc:
            return "the link could not be reached (%s)" % exc.__class__.__name__

    def head_get(url: str) -> Optional[str]:
        request = urllib.request.Request(url, headers={"User-Agent": "TLD-RevaMP-manifest-check", "Range": "bytes=0-0"})
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return None if 200 <= response.status < 400 else "the server answered %d" % response.status
        except urllib.error.HTTPError as exc:
            return "the server answered %d" % exc.code
        except Exception as exc:
            return "the link could not be reached (%s)" % exc.__class__.__name__

    for index, mod in enumerate(data.get("mods") or []):
        if not isinstance(mod, dict):
            continue
        source = mod.get("source")
        if isinstance(source, str) and source.strip() and source_url_problem(source) is None:
            problem = head(source)
            if problem:
                findings.append(finding(SEVERITY_WARNING, "link.source", "mods[%d].source" % index,
                                        "the source link does not answer: %s" % problem,
                                        "the browser opens this link for the player"))
        versions = mod.get("versions")
        if not isinstance(versions, list):
            continue
        for position, version in enumerate(versions):
            if not isinstance(version, dict):
                continue
            url = version.get("url")
            if not isinstance(url, str) or not url or release_url_problem(url) is not None:
                continue
            problem = head(url)
            if problem:
                findings.append(finding(SEVERITY_WARNING, "link.url", "mods[%d].versions[%d].url" % (index, position),
                                        "the release link does not answer: %s" % problem,
                                        "a player cannot download what is not there"))


def plugin_guids() -> Dict[str, str]:
    """The plugin GUIDs of this repository's own example mods: GUID -> the name in the plugin attribute."""
    found: Dict[str, str] = {}
    if not MODS_DIR.is_dir():
        return found
    for plugin in sorted(MODS_DIR.glob("*/Plugin.cs")):
        try:
            source = plugin.read_text(encoding="utf-8")
        except OSError:
            continue
        name = PLUGIN_NAME_RE.search(source)
        if not name:
            continue
        guid = GUID_RE.search(source)
        if guid is None:
            # the plugin names the GUID through a constant: [BepInPlugin(Guid, "Head Unit", Version)]
            const = GUID_CONST_RE.search(source)
            value = GUID_VALUE_RE.search(source) if const else None
            if value and const and value.group("name") == const.group("name_const"):
                guid = value
        if guid:
            found[guid.group("guid")] = name.group("name")
    return found


def check_against_sources(data: dict, guids: Dict[str, str], findings: List[Dict[str, str]]) -> None:
    """For the mods built in this repository, the catalog id is the plugin GUID and the name must match."""
    if not guids:
        return
    for index, mod in enumerate(data.get("mods") or []):
        if not isinstance(mod, dict):
            continue
        mod_id = mod.get("id")
        if not isinstance(mod_id, str) or mod_id not in guids:
            continue
        expected = guids[mod_id]
        name = mod.get("name")
        if isinstance(name, str) and name.strip() and name != expected:
            findings.append(finding(SEVERITY_WARNING, "mod.name.guid", "mods[%d].name" % index,
                                    "the catalog calls '%s' '%s', the plugin says '%s'" % (mod_id, name, expected),
                                    "the id is the plugin GUID; the name is what the player reads"))


def check_dll_name(data: dict, findings: List[Dict[str, str]]) -> None:
    """The DLL inside the archive is normally named after the mod, so a wrong file is caught early."""
    for index, mod in enumerate(data.get("mods") or []):
        if not isinstance(mod, dict):
            continue
        mod_id = mod.get("id")
        versions = mod.get("versions")
        if not isinstance(mod_id, str) or not isinstance(versions, list):
            continue
        tail = mod_id.rsplit(".", 1)[-1].lower()
        for position, version in enumerate(versions):
            if not isinstance(version, dict):
                continue
            file_name = version.get("file")
            if not isinstance(file_name, str) or not file_name:
                continue
            stem = file_name.rsplit("/", 1)[-1].rsplit(".", 1)[0].lower()
            if stem and tail and stem != tail:
                findings.append(finding(SEVERITY_WARNING, "version.file.name", "mods[%d].versions[%d].file" % (
                    index, position),
                    "the DLL is '%s' but the mod is '%s'" % (stem, mod_id),
                    "the DLL is usually named after the mod; a different name may mean the wrong file"))


def read_build_version() -> Optional[str]:
    """The RevaMP version in this checkout, so 'revamp' can be compared with it."""
    try:
        text = PLUGIN_CS.read_text(encoding="utf-8")
    except OSError:
        return None
    match = BASE_VERSION_RE.search(text)
    return match.group(1) if match else None


def check_revamp(data: dict, build: Optional[str], findings: List[Dict[str, str]]) -> None:
    if not build:
        return
    for index, mod in enumerate(data.get("mods") or []):
        if not isinstance(mod, dict):
            continue
        versions = mod.get("versions")
        if not isinstance(versions, list):
            continue
        for position, version in enumerate(versions):
            if not isinstance(version, dict):
                continue
            revamp = version.get("revamp")
            if not isinstance(revamp, str) or not revamp:
                continue
            order = compare_versions(revamp, build)
            if order is not None and order > 0:
                findings.append(finding(SEVERITY_WARNING, "version.revamp.future", "mods[%d].versions[%d].revamp" % (
                    index, position),
                    "the mod needs RevaMP %s, this build is %s" % (revamp, build),
                    "the field is a hint; the version cannot be installed before that RevaMP"))


# ----------------------------------------------------------------------------- the run

class Report:
    def __init__(self) -> None:
        self.findings: List[Dict[str, str]] = []
        self.notes: List[str] = []

    def add(self, item: Dict[str, str]) -> None:
        self.findings.append(item)

    def extend(self, items: Sequence[Dict[str, str]]) -> None:
        self.findings.extend(items)

    @property
    def errors(self) -> List[Dict[str, str]]:
        return [f for f in self.findings if f["severity"] == SEVERITY_ERROR]

    @property
    def warnings(self) -> List[Dict[str, str]]:
        return [f for f in self.findings if f["severity"] == SEVERITY_WARNING]


def validate(manifest_path: pathlib.Path, approved_path: pathlib.Path,
             artifact_dirs: Sequence[pathlib.Path] = (), build_version: Optional[str] = None,
             check_link: bool = False, timeout: float = 15.0) -> Report:
    report = Report()
    try:
        raw = manifest_path.read_bytes()
    except OSError as exc:
        report.add(finding(SEVERITY_ERROR, "manifest.unreadable", str(manifest_path),
                            "the manifest could not be read: %s" % exc,
                            "check the path and the file's permissions"))
        return report
    data = check_catalog_shape(raw, report.findings)
    if data is None:
        return report
    entries, problems = parse_approved(approved_path)
    report.extend(problems)
    seen_ids: Dict[str, int] = {}
    seen_urls: Dict[str, str] = {}
    for index, mod in enumerate(data.get("mods") or []):
        if not isinstance(mod, dict):
            report.add(finding(SEVERITY_ERROR, "mod.type", "mods[%d]" % index,
                                "a mod must be an object, found %s" % type(mod).__name__, ""))
            continue
        check_mod(mod, index, report.findings)
        mod_id = mod.get("id")
        if isinstance(mod_id, str):
            first = seen_ids.get(mod_id)
            if first is not None:
                report.add(finding(SEVERITY_ERROR, "mod.duplicate-id", "mods[%d].id" % index,
                                    "the id '%s' is already used by mods[%d]" % (mod_id, first),
                                    "every mod has its own id; the browser tells them apart by it"))
            else:
                seen_ids[mod_id] = index
        versions = mod.get("versions")
        if isinstance(versions, list):
            for position, version in enumerate(versions):
                if not isinstance(version, dict):
                    continue
                url = version.get("url")
                if not isinstance(url, str) or not url:
                    continue
                spot = "mods[%d].versions[%d].url" % (index, position)
                first = seen_urls.get(url)
                if first is not None:
                    report.add(finding(SEVERITY_ERROR, "version.duplicate-url", spot,
                                        "the same URL is used by %s and by mods[%d].versions[%d]" % (
                                            first, index, position),
                                        "one asset is one file and one version"))
                else:
                    seen_urls[url] = spot
    check_ordering(data, report.findings)
    check_dll_name(data, report.findings)
    check_against_sources(data, plugin_guids(), report.findings)
    counts = check_approved_against_catalog(data, entries, report.findings)
    if counts["used"] < counts["approved"]:
        report.notes.append("%d of %d approved files are not listed in the catalog"
                            % (counts["approved"] - counts["used"], counts["approved"]))
    if artifact_dirs:
        checked = check_artifacts(data, artifact_dirs, report.findings)
        if checked:
            report.notes.append("%d local release archive(s) checked" % checked)
        else:
            report.notes.append("no local release archive found in %s" % ", ".join(str(d) for d in artifact_dirs))
    if build_version:
        check_revamp(data, build_version, report.findings)
    if check_link:
        check_links(data, timeout, report.findings)
    return report


def print_report(report: Report, manifest_path: pathlib.Path, approved_path: pathlib.Path,
                 as_json: bool, quiet: bool) -> None:
    if as_json:
        payload = {
            "manifest": str(manifest_path),
            "approved": str(approved_path),
            "ok": not report.errors,
            "errors": len(report.errors),
            "warnings": len(report.warnings),
            "notes": report.notes,
            "findings": report.findings,
        }
        print(json.dumps(payload, indent=2, sort_keys=True))
        return
    for item in report.findings:
        location = item["path"]
        text = "%-7s %s: %s" % (item["severity"].upper(), location, item["message"])
        if item["hint"]:
            text += "\n         %s" % item["hint"]
        print(text)
    for note in report.notes:
        print("note    %s" % note)
    if quiet:
        return
    if report.errors:
        print("\n%s: %d error(s), %d warning(s). The manifest is not ready for a release."
              % (manifest_path, len(report.errors), len(report.warnings)))
    else:
        print("\n%s: no errors, %d warning(s). The manifest is consistent with %s."
              % (manifest_path, len(report.warnings), approved_path))


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Check the release manifest and the approved list. Offline, and it never writes to them.")
    parser.add_argument("--manifest", type=pathlib.Path, default=DEFAULT_MANIFEST,
                        help="the catalog to check (default: %s)" % DEFAULT_MANIFEST)
    parser.add_argument("--approved", type=pathlib.Path, default=DEFAULT_APPROVED,
                        help="the approved list to check (default: %s)" % DEFAULT_APPROVED)
    parser.add_argument("--artifacts", type=pathlib.Path, action="append", default=None, metavar="DIR",
                        help="a folder with local release archives; may be given more than once")
    parser.add_argument("--revamp-version", default=None,
                        help="the RevaMP version to compare 'revamp' with (default: this checkout's)")
    parser.add_argument("--check-links", action="store_true",
                        help="ask the web whether each source and release link answers (off by default)")
    parser.add_argument("--link-timeout", type=float, default=15.0,
                        help="seconds to wait for one link (default: 15)")
    parser.add_argument("--json", action="store_true", help="print the findings as JSON")
    parser.add_argument("--quiet", action="store_true", help="print only the findings, not the summary")
    parser.add_argument("--strict", action="store_true", help="let warnings fail the run")
    args = parser.parse_args(argv)
    if args.artifacts is None:
        args.artifacts = [DEFAULT_ARTIFACTS]
    build = args.revamp_version if args.revamp_version is not None else read_build_version()
    report = validate(args.manifest, args.approved, args.artifacts, build, args.check_links, args.link_timeout)
    print_report(report, args.manifest, args.approved, args.json, args.quiet)
    if report.errors or (args.strict and report.warnings):
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

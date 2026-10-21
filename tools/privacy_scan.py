#!/usr/bin/env python3
"""Scan the tracked files for things that must not be in this repository: personal paths, LAN addresses, Steam IDs,
keys and tokens, binaries, and decompiled game code. CI runs it on every pull request; run it before you push.

  tools/privacy_scan.py [files...]     default: every file git tracks (and untracked new ones)
  tools/privacy_scan.py --capture <feedback-folder-or-zip> [--forbid "player/persona name" ...]
"""
import argparse, io, json, os, re, subprocess, sys, pathlib, zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
SKIP = {"COPYING", "tools/privacy_scan.py"}
BINARY = re.compile(r"\.(dll|exe|so|dylib|pdb|zip|7z|rar|unity3d|assets|resource)$", re.I)
CHECKS = [
    ("a home folder path", re.compile(r"/ho[m]e/(?!runner/)[a-z_][\w.-]*/|/Users/(?!Shared/)\w[\w.-]*/|"
                                      r"[A-Za-z]:\\{1,2}[Uu]sers\\{1,2}(?!steamuser\b|Public\b|Default\b)\w[\w.-]*")),
    ("a LAN or private IP address", re.compile(r"\b(?:10\.\d{1,3}|192\.168|172\.(?:1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}\b")),
    ("a Steam ID", re.compile(r"\b7656119\d{10}\b")),
    ("a private key", re.compile(r"BEGIN [A-Z ]*PRIVATE KEY")),
    ("a GitHub or Steam API token", re.compile(r"\bgh[opsur]_[A-Za-z0-9]{20,}|\bgithub_pat_\w{20,}|\b[0-9A-F]{32}\b(?=.*(?:key|token))", re.I)),
    ("an email address", re.compile(r"[\w.+-]+@(?!users\.noreply\.github\.com|anthropic\.com)[\w-]+\.[\w.-]*[a-z]{2,}\b", re.I)),
    # the game's own classes, as a decompiler writes them: the mod patches these, it never defines them
    ("decompiled game code", re.compile(r"^\s*(?:public |internal )?(?:sealed )?class (?:mainscript|carscript|objGenScript|menuhandler|"
                                        r"DataFromMenuScript|tosaveitemscript|kaposztaleves)\b", re.M)),
]
# A hand-written test stub may need a class with a game class's name (the code under test refers to it). Such a
# stub is exempt from the decompiled-code check only when it is under tools/unit/ AND the class's own line carries
# this marker, visibly, so a reviewer sees each exemption. The marker exempts nothing else and nothing elsewhere.
STUB_MARKER = "// test stub, not game code"
STUB_DIR = "tools/unit/"


def stub_exempt(rel, what, text, m):
    """Whether a finding is a marked, hand-written test stub under tools/unit (decompiled-code check only)."""
    if what != "decompiled game code" or not rel.replace("\\", "/").startswith(STUB_DIR):
        return False
    end = m.end() - 1   # the class name's line (the pattern may start on a blank line above it)
    start = text.rfind("\n", 0, end) + 1
    stop = text.find("\n", end)
    return STUB_MARKER in text[start:stop if stop >= 0 else len(text)]

# addresses the docs and tools may show: loopback, the documentation ranges (RFC 5737)
ALLOWED_IP = re.compile(r"^(?:127\.|0\.0\.0\.0|192\.0\.2\.|198\.51\.100\.|203\.0\.113\.)")

# A systemd unit name is not an email address. The two shapes systemd itself produces are
# "user@<uid>.service" (the per-UID user manager) and "<unit>@<unit-path>.<suffix>" (a session scope). The
# exclusion is on those shapes only, so a real address at a unit suffix ("someone@example.service") is still
# caught. It is a shape test, not a list of allowed strings: a new unit name is excluded without a code
# change, and a person's address never is.
# The instance form systemd writes is "<unit>@<instance>", where the instance names a unit that already
# exists: "wayland-wm@hyprland.desktop.service" is the unit "wayland-wm.service" with the instance
# "hyprland.desktop". So the local part repeats the LAST element of the domain, which is what tells a unit
# name from a person's address ("alice@example.service" has no such repetition).
# The unit names systemd's session machinery uses. A name outside this set is not excluded, so a real
# address is never hidden by the unit-name rule.
SYSTEMD_UNIT_NAMES = {
    "wayland-wm", "gnome-shell", "plasmashell", "kwin_wayland", "sway", "Hyprland", "mutter",
    "xfce4-session", "lxqt-session", "mate-session", "cinnamon", "systemd", "user", "init",
    "dbus", "pipewire", "wireplumber", "gamemode", "gamescope", "vkbasalt", "gamescope-fsr",
}

SYSTEMD_UNIT = re.compile(
    r"^(?:user@\d+\.service"
    r"|[A-Za-z0-9_-]+@(?:[A-Za-z0-9_-]+\.)*([A-Za-z0-9_-]+)\.(?:service|scope|socket|mount|timer|slice))$")


def is_systemd_unit(text):
    """True when the text is a systemd unit name rather than an email address.

    systemd's own names here are "user@<uid>.service" (the per-UID user manager) and
    "<unit>@<instance>.<suffix>" (a unit instance, e.g. "wayland-wm@hyprland.desktop.service" is the unit
    "wayland-wm.service" with the instance "hyprland.desktop"). The unit names systemd uses for these are
    well-known words, so the exclusion is limited to a small set of them: a person's address, even at a
    ".service" domain ("alice@example.service"), is still caught.
    """
    m = SYSTEMD_UNIT.match(text.strip())
    if not m:
        return False
    local, _, domain = text.strip().partition("@")
    if local == "user" and re.match(r"^\d+\.service$", domain):
        return True
    return local in SYSTEMD_UNIT_NAMES


def tracked():
    out = subprocess.run(["git", "-C", str(ROOT), "ls-files", "--cached", "--others", "--exclude-standard"],
                         capture_output=True, text=True, check=True).stdout
    return [p for p in out.splitlines() if p]


# Generated images the mod needs, by exact path: each must be a PNG with nothing but image data in it (no text,
# EXIF or other metadata chunks that could carry a name or a path).
IMAGES = {"src/TLDRevamp/Assets/WipFont.png"}   # tools/wipfont.py


def bare_png(p):
    data = p.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n": return False
    i, kinds = 8, set()
    while i + 8 <= len(data):
        n = int.from_bytes(data[i:i + 4], "big"); kinds.add(data[i + 4:i + 8]); i += 12 + n
    return i == len(data) and kinds <= {b"IHDR", b"IDAT", b"IEND"}


def scan(paths):
    found = []
    for rel in paths:
        if rel in SKIP: continue
        p = ROOT / rel
        if not p.is_file(): continue
        if BINARY.search(rel):
            found.append((rel, 0, "a binary file", "")); continue
        if p.stat().st_size > 1_000_000:
            found.append((rel, 0, "a file over 1 MB", "")); continue
        if rel in IMAGES:
            if not bare_png(p): found.append((rel, 0, "an image with metadata (or not a PNG)", ""))
            continue
        try: text = p.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            found.append((rel, 0, "a non-text file", "")); continue
        found.extend(scan_text(rel, text))
    return found


def scan_text(rel, text):
    """The content checks for one file's text; rel is its repository path (it decides the stub exemption)."""
    found = []
    for what, rx in CHECKS:
        for m in rx.finditer(text):
            if what.startswith("a LAN") and ALLOWED_IP.match(m.group(0)): continue
            if what == "an email address" and is_systemd_unit(m.group(0)): continue
            if stub_exempt(rel, what, text, m): continue
            line = text.count("\n", 0, m.start()) + 1
            found.append((rel, line, what, m.group(0)[:60]))
    return found


CAPTURE_CHECKS = [
    ("an absolute path", re.compile(r"""[A-Za-z]:[\\/]|\\\\[^\s\\]+\\|(?:^|[\s=\("'])/[^\s"'<>]+|~[/\\]|%[A-Za-z_]+%|\$\{?(?:HOME|USERPROFILE)\}?""")),
    ("an IPv4 address/port", re.compile(r"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?")),
    ("an IPv6 address/port", re.compile(r"(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:.%]*(?:\]:\d+)?")),
    ("an account identifier", re.compile(r"\b\d{17,20}\b|\bSTEAM_\d:\d:\d+|\[U:\d:\d+\]")),
    ("an email address", re.compile(r"[\w.+-]+@[\w.-]+\.[a-z]{2,}", re.I)),
    ("a URL", re.compile(r"[a-z][a-z0-9+.-]*://\S+|\bwww\.\S+|\b(?:[a-z0-9-]+\.)+[a-z]{2,}(?:/[^\s?]*)?\?\S+", re.I)),
    ("a wall-clock time", re.compile(r"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}|\b\d{1,2}:\d{2}:\d{2}(?:\s*(?:[+-]\d{2}:?\d{2}|[A-Z]{2,5}))?")),
]
CAPTURE_FORBIDDEN = re.compile(r"^(?:screenshot.*\.(?:png|jpg)|.*\.(?:png|jpg|jpeg)|LogOutput(?:\.prev)?\.log|Player(?:-prev)?\.log|BepInEx\.cfg)$", re.I)
PRIVATE_KEYS = {"steamid", "steamid64", "accountid", "lobbyid", "address", "ip", "port", "chat", "username", "machinename", "path", "hostname", "persona"}


def scan_capture(paths, forbidden=()):
    """Inspect bytes, decoded JSON strings/keys, relative filenames and expanded ZIP members. Never extract."""
    found, count = [], 0
    needles = [s for s in (*forbidden, os.environ.get("USERNAME"), os.environ.get("USER"), os.environ.get("COMPUTERNAME")) if s]

    def check_text(label, text):
        for what, rx in CAPTURE_CHECKS:
            for m in rx.finditer(text):
                found.append((label, text.count("\n", 0, m.start()) + 1, what, ""))
        for needle in needles:
            if needle.casefold() in text.casefold(): found.append((label, 0, "a forbidden identity", ""))

    def walk(label, value):
        if isinstance(value, dict):
            for key, child in value.items():
                check_text(label, key)
                if key.casefold() in PRIVATE_KEYS and child not in (None, "", "[removed]"):
                    found.append((label, 0, "a private field: " + key, ""))
                walk(label, child)
        elif isinstance(value, list):
            for child in value: walk(label, child)
        elif isinstance(value, str): check_text(label, value)
        elif isinstance(value, int) and abs(value) >= 10**16: found.append((label, 0, "an account-sized number", ""))

    def inspect(label, name, data, depth=0):
        nonlocal count
        count += 1
        check_text(label, name)
        if CAPTURE_FORBIDDEN.match(pathlib.PurePosixPath(name).name): found.append((label, 0, "an excluded attachment", ""))
        if name.lower().endswith(".zip"):
            if depth > 2 or len(data) > 24 << 20: raise ValueError("archive size/nesting limit")
            with zipfile.ZipFile(io.BytesIO(data)) as archive:
                if archive.comment: found.append((label, 0, "a ZIP comment", ""))
                if len(archive.infolist()) > 256 or sum(e.file_size for e in archive.infolist()) > 96 << 20: raise ValueError("archive expansion limit")
                for entry in archive.infolist():
                    if entry.comment or entry.extra: found.append((label, 0, "ZIP entry comment/extra metadata", ""))
                    if entry.date_time != (1980, 1, 1, 0, 0, 0): found.append((label, 0, "a non-fixed ZIP timestamp", ""))
                    if entry.file_size > 32 << 20: raise ValueError("entry size limit")
                    inspect(label + "!" + entry.filename, entry.filename, archive.read(entry), depth + 1)
            return
        if len(data) > 32 << 20: raise ValueError("file size limit")
        text = data.decode("utf-8-sig")
        check_text(label, text)
        if name.lower().endswith(".jsonl"):
            for line in text.splitlines():
                if line.strip(): walk(label, json.loads(line))
        elif name.lower().endswith(".json"): walk(label, json.loads(text))

    for arg in paths:
        path = pathlib.Path(arg)
        files = sorted(p for p in path.rglob("*") if p.is_file()) if path.is_dir() else [path]
        for file in files:
            name = file.relative_to(path).as_posix() if path.is_dir() else file.name
            try: inspect(name, name, file.read_bytes())
            except (OSError, UnicodeError, ValueError, zipfile.BadZipFile, RuntimeError, NotImplementedError): found.append((name, 0, "unreadable/invalid capture", ""))
    return count, found


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="*")
    parser.add_argument("--capture", action="store_true", help="scan a feedback folder or ZIP, including filenames and expanded contents")
    parser.add_argument("--forbid", action="append", default=[], help="additional player/persona/user/machine name (repeatable, capture mode)")
    args = parser.parse_args()
    paths = args.paths or ([] if args.capture else tracked())
    if args.capture and not paths: parser.error("--capture needs a feedback folder or ZIP")
    count, found = scan_capture(paths, args.forbid) if args.capture else (len(paths), scan(paths))
    for rel, line, what, hit in found:
        print(f"{rel}:{line}: {what}" + (f" ({hit})" if hit else ""))
    print(f"privacy scan: {count} files, " + (f"{len(found)} problem(s)" if found else "clean"))
    sys.exit(1 if found else 0)

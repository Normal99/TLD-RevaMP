#!/usr/bin/env python3
"""Scan the tracked files for things that must not be in this repository: personal paths, LAN addresses, Steam IDs,
keys and tokens, binaries, and decompiled game code. CI runs it on every pull request; run it before you push.

  tools/privacy_scan.py [files...]     default: every file git tracks (and staged new ones)
"""
import re, subprocess, sys, pathlib

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
# addresses the docs and tools may show: loopback, the documentation ranges (RFC 5737)
ALLOWED_IP = re.compile(r"^(?:127\.|0\.0\.0\.0|192\.0\.2\.|198\.51\.100\.|203\.0\.113\.)")


def tracked():
    out = subprocess.run(["git", "-C", str(ROOT), "ls-files", "--cached", "--others", "--exclude-standard"],
                         capture_output=True, text=True, check=True).stdout
    return [p for p in out.splitlines() if p]


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
        try: text = p.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            found.append((rel, 0, "a non-text file", "")); continue
        for what, rx in CHECKS:
            for m in rx.finditer(text):
                if what.startswith("a LAN") and ALLOWED_IP.match(m.group(0)): continue
                line = text.count("\n", 0, m.start()) + 1
                found.append((rel, line, what, m.group(0)[:60]))
    return found


if __name__ == "__main__":
    paths = sys.argv[1:] or tracked()
    found = scan(paths)
    for rel, line, what, hit in found:
        print(f"{rel}:{line}: {what}" + (f" ({hit})" if hit else ""))
    print(f"privacy scan: {len(paths)} files, " + (f"{len(found)} problem(s)" if found else "clean"))
    sys.exit(1 if found else 0)

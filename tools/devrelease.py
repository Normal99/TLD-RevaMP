#!/usr/bin/env python3
"""Dev build release: builds the checked-out commit as TLD RevaMP <version>-dev.<commit> and uploads it as a GitHub
release of its own, so testers can try dev without building it. By default it is a DRAFT: only people with write
access to the repository see it and can download it, and its tag (v<version>-dev.<commit>) is only created if someone
publishes it. --public publishes it right away as a pre-release (never marked latest) that anyone can download.

The version suffix keeps dev builds apart: multiplayer only lets the exact same version join, so a dev build plays
with the same dev build and never with a release (or another dev build). The overlay shows the full version;
BepInEx gets the plain one (it skips a plugin whose version isn't plain numbers).
The zip has the player release layout of tools/package.sh (BepInEx 5 from this machine's game, TLD_DIR; the mod;
INSTALL.txt) and the same private-string check, in Python so it also runs where zip/strings are missing (Git Bash).
The installers stay pinned to the official release (tools/pin-installer.sh); a dev build is installed from its zip.

  tools/devrelease.py            build, check, zip and upload a draft (a clean checkout whose commit is pushed; gh signed in)
  tools/devrelease.py --public   the same, published as a public pre-release
  tools/devrelease.py --dry-run  everything but the upload; the zip stays in dist/
A draft is downloaded from its release page (signed in) or with: gh release download <tag> --repo <owner/name>
"""
import argparse, getpass, hashlib, pathlib, re, subprocess, sys, tempfile, zipfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import tldenv

ROOT = tldenv.TOOLS.parent
PLUGIN_CS = ROOT / "src/TLDRevamp/Plugin.cs"
OUT = "bin/Release/netstandard2.1"
VERSION_RE = re.compile(r'(public const string Version = ")([^"]+)(")')
BEPINPLUGIN_RE = re.compile(r'(\[BepInPlugin\(Guid, "TLD Revamp", )Version(\)\])')


def git(*args):
    return subprocess.run(["git", "-C", str(ROOT)] + list(args), check=True, capture_output=True, text=True).stdout.strip()


def fail(msg):
    sys.exit("devrelease: " + msg)


def private_hits(data):
    """The strings of a DLL (ASCII and UTF-16LE, like strings -a / -el) that name this machine or its user."""
    home = str(pathlib.Path.home())
    pats = [re.escape(home), re.escape(home.replace("\\", "/")), re.escape(getpass.getuser()),
            r"192\.168\.", r"7656119[0-9]{10}", r"[A-Z]:\\Users"]
    rx = re.compile("|".join(pats), re.I)
    found = [m.decode("ascii") for m in re.findall(rb"[\x20-\x7e]{4,}", data)]
    found += [m.decode("utf-16-le") for m in re.findall(rb"(?:[\x20-\x7e]\x00){4,}", data)]
    return [s for s in found if rx.search(s)]


def build(base, version):
    """Builds the plugin with Plugin.cs's version set to `version`, then puts Plugin.cs back as it was. BepInEx 5 skips a
    plugin whose BepInPlugin version isn't plain numbers ("its version is invalid"), so that one keeps `base`."""
    original = PLUGIN_CS.read_bytes()
    src = VERSION_RE.sub(lambda m: m.group(1) + version + m.group(3), original.decode("utf-8"), 1)
    src, n = BEPINPLUGIN_RE.subn(lambda m: m.group(1) + '"' + base + '"' + m.group(2), src, 1)
    if n != 1:
        fail("Plugin.cs has no [BepInPlugin(Guid, \"TLD Revamp\", Version)] to give BepInEx the plain version")
    PLUGIN_CS.write_bytes(src.encode("utf-8"))
    try:
        for proj in ("TLDRevamp/TLDRevamp.csproj", "TLDLoaderShim/TLDLoaderShim.csproj"):
            subprocess.run(["dotnet", "build", str(ROOT / "src" / proj), "-c", "Release", "-nologo", "-v", "q"], check=True)
    finally:
        PLUGIN_CS.write_bytes(original)


def package(version, commit):
    game = tldenv.GAME_DIR
    plugin = {
        "TLDRevamp.dll": ROOT / "src/TLDRevamp" / OUT / "TLDRevamp.dll",
        "TLDRevamp.Multiplayer.dll": ROOT / "src/TLDRevamp.Multiplayer" / OUT / "TLDRevamp.Multiplayer.dll",
        "TLDLoader.dll": ROOT / "src/TLDLoaderShim" / OUT / "TLDLoader.dll",
        "approved-mods.txt": ROOT / "packaging/approved-mods.txt",
        "COPYING": ROOT / "COPYING",
    }
    # the version RevaMP shows and compares (a UTF-16 string literal); BepInPlugin's UTF-8 blob carries the plain one
    dll = plugin["TLDRevamp.dll"].read_bytes()
    if version.encode("utf-16-le") not in dll or version.encode() in dll:
        fail(f"the built TLDRevamp.dll doesn't carry the version {version} (or gives it to BepInEx too)")
    for name, path in plugin.items():
        if name.endswith(".dll"):
            hits = private_hits(path.read_bytes())
            if hits:
                fail(f"private string in {name}: " + "; ".join(hits[:5]))
    files = {name: game / name for name in ("winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt")}
    core = sorted((game / "BepInEx/core").iterdir())
    if not core:
        fail(f"no BepInEx/core in {game} (set TLD_DIR to a game with BepInEx 5)")
    files.update({"BepInEx/core/" + p.name: p for p in core if p.is_file()})
    files.update({"BepInEx/plugins/TLDRevamp/" + n: p for n, p in plugin.items()})
    install = (ROOT / "packaging/INSTALL.txt").read_text(encoding="utf-8").replace("@VERSION@", version)
    install = (f"DEV BUILD of commit {commit}, for testing; not an official release. In multiplayer it only joins\n"
               f"players on this exact build ({version}).\n\n") + install
    (ROOT / "dist").mkdir(exist_ok=True)
    out = ROOT / "dist" / f"TLD-RevaMP-{version}.zip"
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name, path in sorted(files.items()):
            z.write(path, name)
        z.writestr("INSTALL.txt", install)
    return out


def notes(version, commit, sha256):
    last = git("describe", "--tags", "--abbrev=0", "--match", "v[0-9]*", "--exclude", "*-dev*", "HEAD")
    log = git("log", "--no-merges", "--format=- %s (%h)", f"{last}..HEAD")
    return (f"Dev build of commit {commit}, for testing; not an official release.\n\n"
            f"In multiplayer everyone needs this exact build: it shows as **{version}** in the overlay and won't join "
            f"a release or another dev build. Install: copy everything from the zip into the game folder (INSTALL.txt). "
            f"Back up your saves first.\n\n"
            f"Changes since {last}:\n{log or '- none'}\n\n"
            f"SHA-256 of TLD-RevaMP-{version}.zip: `{sha256}`\n")


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--dry-run", action="store_true", help="build and zip, but don't upload")
    ap.add_argument("--public", action="store_true", help="publish as a public pre-release instead of a draft")
    ap.add_argument("--repo", help="owner/name to publish to (default: the origin remote)")
    a = ap.parse_args()

    if git("status", "--porcelain", "--untracked-files=no"):
        fail("the checkout has uncommitted changes: a dev build is exactly one commit")
    full = git("rev-parse", "HEAD")
    commit = full[:7]
    base = VERSION_RE.search(PLUGIN_CS.read_text(encoding="utf-8")).group(2)
    version = f"{base}-dev.{commit}"
    tag = "v" + version
    if not a.dry_run:
        if not git("branch", "-r", "--contains", full):
            fail(f"{commit} isn't pushed: push it first, so the release's tag points at a commit others can see")
        repo = a.repo or re.sub(r"\.git$", "", re.split(r"github\.com[:/]", git("remote", "get-url", "origin"))[-1])
        # every release's tag name, drafts included (`gh release view <tag>` doesn't find a draft: it has no tag yet)
        tags = subprocess.run(["gh", "api", "--paginate", f"repos/{repo}/releases", "--jq", ".[].tag_name"],
                              check=True, capture_output=True, text=True).stdout.split()
        if tag in tags:
            fail(f"{tag} is already released (or a draft) on {repo}")

    build(base, version)
    out = package(version, commit)
    sha256 = hashlib.sha256(out.read_bytes()).hexdigest()
    print(f"{out} ({out.stat().st_size // 1024} KB)  sha256 {sha256}")
    if a.dry_run:
        print(notes(version, commit, sha256))
        return

    with tempfile.NamedTemporaryFile("w", suffix=".md", delete=False, encoding="utf-8") as f:
        f.write(notes(version, commit, sha256))
    subprocess.run(["gh", "release", "create", tag, str(out), "--repo", repo, "--target", full, "--prerelease",
                    "--latest=false", "--title", f"TLD RevaMP {version} (dev build)", "--notes-file", f.name]
                   + ([] if a.public else ["--draft"]), check=True)
    pathlib.Path(f.name).unlink()


if __name__ == "__main__":
    main()

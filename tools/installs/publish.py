#!/usr/bin/env python3
"""TLD Copies as single files that run without .NET installed: the window (TLDCopies) and the command line
(tld-installs), for Linux and Windows, into dist/installs/<linux-x64|win-x64>/.

  tools/installs/publish.py            both platforms
  tools/installs/publish.py win-x64    one (linux-x64 or win-x64)

Needs the .NET 10 SDK. Each file carries its own .NET runtime (about 40-50 MB, compressed)."""
import pathlib, subprocess, sys

HERE = pathlib.Path(__file__).resolve().parent
OUT = HERE.parent.parent / "dist" / "installs"
rids = sys.argv[1:] or ["linux-x64", "win-x64"]
for rid in rids:
    for proj in ("Installs.App", "Installs.Cli"):
        r = subprocess.run(["dotnet", "publish", str(HERE / proj), "-c", "Release", "-r", rid, "--self-contained", "-nologo", "-v", "q",
                            "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true",
                            "-p:DebugType=none", "-o", str(OUT / rid)])
        if r.returncode != 0: sys.exit(f"publishing {proj} for {rid} failed")
    for f in sorted((OUT / rid).iterdir()): print(f"{f}  {f.stat().st_size // (1 << 20)} MB")

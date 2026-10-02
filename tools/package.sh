#!/usr/bin/env bash
# Player release zip: BepInEx 5 (unmodified, from this machine's game install) + the mod + INSTALL.txt.
# Never includes: configs, logs, caches, third-party TLDLoader mods (Killenger's autopilot is test tooling), saves.
#   tools/package.sh  ->  dist/TLD-RevaMP-<version>.zip
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="${TLD_DIR:-$HOME/.local/share/Steam/steamapps/common/The Long Drive}"
VER=$(grep -oP 'Version = "\K[^"]+' "$ROOT/src/TLDRevamp/Plugin.cs")
dotnet build "$ROOT/src/TLDRevamp/TLDRevamp.csproj" -c Release -nologo -v q | grep -E "error|Build succeeded" | sort -u
dotnet build "$ROOT/src/TLDLoaderShim/TLDLoaderShim.csproj" -c Release -nologo -v q | grep -E "error|Build succeeded" | sort -u
DLL="$ROOT/src/TLDRevamp/bin/Release/netstandard2.1/TLDRevamp.dll"
got=$(strings "$DLL" | grep -oE "^0\.[0-9]+\.[0-9]+$" | head -1)
[ "$got" = "$VER" ] || { echo "DLL version '$got' != Plugin.cs '$VER'" >&2; exit 1; }
STAGE=$(mktemp -d); trap 'rm -rf "$STAGE"' EXIT
P="$STAGE/TLD-RevaMP-$VER"; mkdir -p "$P/BepInEx/core" "$P/BepInEx/plugins/TLDRevamp"
for f in winhttp.dll doorstop_config.ini .doorstop_version changelog.txt; do cp "$GAME/$f" "$P/"; done
cp "$GAME"/BepInEx/core/* "$P/BepInEx/core/"
cp "$DLL" "$ROOT/src/TLDLoaderShim/bin/Release/netstandard2.1/TLDLoader.dll" "$P/BepInEx/plugins/TLDRevamp/"
sed "s/@VERSION@/$VER/g" "$ROOT/packaging/INSTALL.txt" > "$P/INSTALL.txt"
# nothing personal or third-party slipped in
if find "$P" -iname "*.cfg" -o -iname "*.log" -o -ipath "*TLDLoaderMods*" -o -iname "*autopilot*" | grep -q .; then echo "package contains configs/logs/third-party mods" >&2; exit 1; fi
# and no build-machine paths, names or addresses inside our DLLs (string literals, PDB path)
for f in "$P"/BepInEx/plugins/TLDRevamp/*.dll; do
  # (not grep -q: it quits at the first match, strings dies of SIGPIPE, and pipefail turned every match into "clean")
  hits=$( (strings -a "$f"; strings -a -el "$f") | grep -iE "$HOME|$(whoami)|192\.168\.|7656119[0-9]{10}|[A-Z]:\\\\Users" || true)
  if [ -n "$hits" ]; then echo "private string in $f:" >&2; echo "$hits" | head -5 >&2; exit 1; fi
done
mkdir -p "$ROOT/dist"; OUT="$ROOT/dist/TLD-RevaMP-$VER.zip"; rm -f "$OUT"
(cd "$P" && zip -qr9 "$OUT" . -x '.*' && zip -q "$OUT" .doorstop_version)
echo "$OUT ($(du -h "$OUT" | cut -f1))"; unzip -l "$OUT" | tail -n +4 | head -n -2 | awk '{print "   " $4}'

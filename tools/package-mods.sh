#!/usr/bin/env bash
# Example mod zips for a release (src/Mods): BepInEx/plugins/<Mod>/<Mod>.dll each, with the same private-string check
# as package.sh. Prints each DLL's SHA-256 line for packaging/approved-mods.txt (run before package.sh, so the list
# that ships approves exactly these files).
#   tools/package-mods.sh  ->  dist/<Mod>-<version>.zip
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$ROOT/dist"
for m in HeadUnit CarTrailer; do
  dotnet build "$ROOT/src/Mods/$m/$m.csproj" -c Release -nologo -v q | grep -E "error|Build succeeded" | sort -u
  VER=$(grep -oP 'Version = "\K[^"]+' "$ROOT/src/Mods/$m/Plugin.cs")
  DLL="$ROOT/src/Mods/$m/bin/Release/netstandard2.1/$m.dll"
  hits=$( (strings -a "$DLL"; strings -a -el "$DLL") | grep -iE "$HOME|$(whoami)|192\.168\.|7656119[0-9]{10}|[A-Z]:\\\\Users" || true)
  if [ -n "$hits" ]; then echo "private string in $DLL:" >&2; echo "$hits" | head -5 >&2; exit 1; fi
  STAGE=$(mktemp -d); mkdir -p "$STAGE/BepInEx/plugins/$m"; cp "$DLL" "$STAGE/BepInEx/plugins/$m/"
  OUT="$ROOT/dist/$m-$VER.zip"; rm -f "$OUT"; (cd "$STAGE" && zip -qr9 "$OUT" BepInEx); rm -rf "$STAGE"
  echo "$OUT ($(du -h "$OUT" | cut -f1))"
  echo "approved: $(sha256sum "$DLL" | cut -d' ' -f1) $m $VER"
done

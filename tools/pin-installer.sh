#!/usr/bin/env bash
# Pins installer/install.sh and installer/install.ps1 to a release zip: writes its version, download URL and SHA-256
# into the "Pinned downloads" block of both scripts. Also checks that the zip's BepInEx files are byte-identical to the
# official BepInEx release the scripts download (they take BepInEx from there, and only TLDRevamp/ from our zip), so a
# BepInEx update in the game can't slip past the installer: update BEPINEX_URL/SHA256 in both scripts when it fails.
#   tools/pin-installer.sh [zip]   (default: dist/TLD-RevaMP-<Plugin.cs version>.zip); then commit both scripts and
#   attach them to the release next to the zip (docs/HANDOVER.md, release steps).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SH="$ROOT/installer/install.sh"; PS="$ROOT/installer/install.ps1"
VER=$(grep -oP 'Version = "\K[^"]+' "$ROOT/src/TLDRevamp/Plugin.cs")
ZIP="${1:-$ROOT/dist/TLD-RevaMP-$VER.zip}"
[ -f "$ZIP" ] || { echo "no $ZIP (tools/package.sh makes it)" >&2; exit 1; }
VER=$(basename "$ZIP" | sed -n 's/^TLD-RevaMP-\(.*\)\.zip$/\1/p')
SUM=$(sha256sum "$ZIP" | cut -d' ' -f1)
URL="https://github.com/Normal99/TLD-RevaMP/releases/download/v$VER/TLD-RevaMP-$VER.zip"

# the official BepInEx the scripts pin, fetched once into the cache
BURL=$(sed -n 's/^BEPINEX_URL="\(.*\)"$/\1/p' "$SH"); BSUM=$(sed -n 's/^BEPINEX_SHA256="\(.*\)"$/\1/p' "$SH")
grep -qF "\$BEPINEX_URL = \"$BURL\"" "$PS" && grep -qF "\$BEPINEX_SHA256 = \"$BSUM\"" "$PS" || { echo "install.sh and install.ps1 pin different BepInEx" >&2; exit 1; }
CACHE="${TLD_CACHE:-$HOME/tld-revamp-cache}/bepinex"; mkdir -p "$CACHE"; BZIP="$CACHE/$(basename "$BURL")"
[ -f "$BZIP" ] || curl -fsSL -o "$BZIP" "$BURL"
[ "$(sha256sum "$BZIP" | cut -d' ' -f1)" = "$BSUM" ] || { echo "$BZIP doesn't match the pinned BepInEx sha256" >&2; exit 1; }
T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
unzip -q "$BZIP" -d "$T/bep"; unzip -q "$ZIP" -d "$T/rel"
(cd "$T/bep" && find . -type f) | while read -r f; do
  cmp -s "$T/bep/$f" "$T/rel/$f" || { echo "$f in $(basename "$ZIP") differs from $(basename "$BZIP"): the game's BepInEx changed, pin the new one in both scripts" >&2; exit 1; }
done
[ -f "$T/rel/BepInEx/plugins/TLDRevamp/TLDRevamp.dll" ] || { echo "no BepInEx/plugins/TLDRevamp/TLDRevamp.dll in $ZIP" >&2; exit 1; }

sed -i -e "s|^REVAMP_VERSION=\".*\"|REVAMP_VERSION=\"$VER\"|" -e "s|^REVAMP_URL=\".*\"|REVAMP_URL=\"$URL\"|" \
       -e "s|^REVAMP_SHA256=\".*\"|REVAMP_SHA256=\"$SUM\"|" "$SH"
sed -i -e "s|^\$REVAMP_VERSION = \".*\"|\$REVAMP_VERSION = \"$VER\"|" -e "s|^\$REVAMP_URL = \".*\"|\$REVAMP_URL = \"$URL\"|" \
       -e "s|^\$REVAMP_SHA256 = \".*\"|\$REVAMP_SHA256 = \"$SUM\"|" "$PS"
for f in "$SH" "$PS"; do grep -qF "$SUM" "$f" && grep -qF "$URL" "$f" || { echo "couldn't pin $f" >&2; exit 1; }; done
echo "installer pinned to TLD RevaMP $VER ($SUM); BepInEx files match $(basename "$BZIP")"

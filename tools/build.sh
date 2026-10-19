#!/usr/bin/env bash
# Build the plugin and copy it into the game's BepInEx/plugins folder.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="${TLD_DIR:-$HOME/.local/share/Steam/steamapps/common/The Long Drive}"
dotnet build "$ROOT/src/TLDRevamp/TLDRevamp.csproj" -c Release -nologo -v q
dotnet build "$ROOT/src/TLDLoaderShim/TLDLoaderShim.csproj" -c Release -nologo -v q
mkdir -p "$GAME/BepInEx/plugins/TLDRevamp"
cp "$ROOT/src/TLDRevamp/bin/Release/netstandard2.1/TLDRevamp.dll" "$GAME/BepInEx/plugins/TLDRevamp/"
# the multiplayer API other mods reference (built with TLDRevamp, which depends on it)
cp "$ROOT/src/TLDRevamp.Multiplayer/bin/Release/netstandard2.1/TLDRevamp.Multiplayer.dll" "$ROOT/packaging/approved-mods.txt" "$GAME/BepInEx/plugins/TLDRevamp/"
# TLDLoader mods under BepInEx: the API stub next to our plugin, the mods (third-party, not in git) in BepInEx/TLDLoaderMods
cp "$ROOT/src/TLDLoaderShim/bin/Release/netstandard2.1/TLDLoader.dll" "$GAME/BepInEx/plugins/TLDRevamp/"
if compgen -G "$ROOT/thirdparty/TLDLoaderMods/*.dll" > /dev/null; then mkdir -p "$GAME/BepInEx/TLDLoaderMods"; cp "$ROOT"/thirdparty/TLDLoaderMods/*.dll "$GAME/BepInEx/TLDLoaderMods/"; fi
# test machine: the bridge (off in a player's install) on in this game's own config
CFG="$GAME/BepInEx/config/tldrevamp.core.cfg"
if [ -f "$CFG" ] && grep -q "^Bridge = " "$CFG"; then sed -i 's/^Bridge = .*/Bridge = true/' "$CFG"; else mkdir -p "$GAME/BepInEx/config"; printf '\n[Debug]\nBridge = true\n' >> "$CFG"; fi
echo "Deployed to $GAME/BepInEx/plugins/TLDRevamp/"
# guard: the built DLL must carry Plugin.cs's version — a silent no-op version bump dies here
want=$(grep -oP 'BaseVersion = "\K[^"]+' "$ROOT/src/TLDRevamp/Plugin.cs")
got=$(strings "$ROOT/src/TLDRevamp/bin/Release/netstandard2.1/TLDRevamp.dll" | grep -oE "^0\.[0-9]+\.[0-9]+$" | head -1)
if [ "$want" != "$got" ]; then echo "BUILD GUARD: DLL version '$got' != Plugin.cs '$want'" >&2; exit 1; fi
echo "version $got verified"

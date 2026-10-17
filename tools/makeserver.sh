#!/usr/bin/env bash
# A dedicated server folder made from THIS machine's game install (nothing is downloaded or redistributed: the server
# is the owner's own copy of the game, run headless by TLD Revamp's server mode, DedicatedServer.cs).
#   tools/makeserver.sh [out dir, default ~/TLD-Server]
# The folder gets: the game (without saves, logs, caches or other players' configs), BepInEx with TLD Revamp only (no
# example or third-party mods), steam_appid.txt (the exe starts outside the Steam launcher; Steam must still be running
# with an account that owns the game), a config with the [Server] settings and the bridge off, and two launchers:
#   start-server.bat   Windows
#   start-server.sh    Linux, through Proton (PROTON=/path/to/proton, default: the newest Proton in Steam's folders)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="${TLD_DIR:-$HOME/.local/share/Steam/steamapps/common/The Long Drive}"
OUT="${1:-$HOME/TLD-Server}"
[ -f "$GAME/TheLongDrive.exe" ] || { echo "no game at $GAME (set TLD_DIR)" >&2; exit 1; }
DLL="$ROOT/src/TLDRevamp/bin/Release/netstandard2.1/TLDRevamp.dll"
[ -f "$DLL" ] || { echo "build the mod first (tools/build.sh or tools/package.sh)" >&2; exit 1; }
mkdir -p "$OUT"
rsync -a --delete \
  --exclude 'BepInEx/plugins/' --exclude 'BepInEx/config/' --exclude 'BepInEx/cache/' --exclude 'BepInEx/TLDLoaderMods/' \
  --exclude 'BepInEx/tldrevamp-*' --exclude '*.log' --exclude 'BepInEx/LogOutput*' --exclude 'TheLongDrive_Data/prefab_*.txt' \
  --exclude 'TheLongDrive_Data/*.png' --exclude 'INSTALL.txt' \
  "$GAME/" "$OUT/"
mkdir -p "$OUT/BepInEx/plugins/TLDRevamp" "$OUT/BepInEx/config"
cp "$DLL" "$ROOT/src/TLDRevamp.Multiplayer/bin/Release/netstandard2.1/TLDRevamp.Multiplayer.dll" \
   "$ROOT/src/TLDLoaderShim/bin/Release/netstandard2.1/TLDLoader.dll" "$ROOT/packaging/approved-mods.txt" "$OUT/BepInEx/plugins/TLDRevamp/"
echo 1017180 > "$OUT/steam_appid.txt"
CFG="$OUT/BepInEx/config/tldrevamp.core.cfg"
[ -f "$CFG" ] || cat > "$CFG" <<'EOF'
[Debug]
Bridge = false

[Server]
## Dedicated server: the port it hosts on (-tldport overrides it).
Port = 27070
## Dedicated server: players must type this to join (empty: no password).
Password =
## Dedicated server: players at most (the server itself counts as one).
MaxPlayers = 16
## Dedicated server: players may ask to teleport to each other (/tpa).
TeleportRequests = true
## Dedicated server: seconds between one player's teleport requests (0: none).
TeleportCooldownSeconds = 300
## Dedicated server: saves the world (and every player's record) this often (0: only when it stops).
AutoSaveMinutes = 5
EOF
cat > "$OUT/start-server.bat" <<'EOF'
@echo off
rem TLD Revamp dedicated server. Steam must be running. Add -tldseed <number> once for a fresh world with that seed;
rem without it the server continues its last save. Settings: BepInEx\config\tldrevamp.core.cfg, [Server].
cd /d "%~dp0"
TheLongDrive.exe -batchmode -nographics %*
EOF
cat > "$OUT/start-server.sh" <<'EOF'
#!/usr/bin/env bash
# TLD Revamp dedicated server on Linux, through Proton. Steam must be running. Add -tldseed <number> once for a fresh
# world with that seed; without it the server continues its last save. Settings: BepInEx/config/tldrevamp.core.cfg.
cd "$(dirname "$0")"
STEAM="${STEAM_DIR:-$HOME/.local/share/Steam}"
PROTON="${PROTON:-$(ls -d "$STEAM"/steamapps/common/Proton*/proton "$STEAM"/compatibilitytools.d/*/proton 2>/dev/null | sort -V | tail -1)}"
[ -x "$PROTON" ] || { echo "no Proton found: set PROTON=/path/to/proton" >&2; exit 1; }
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM"
export STEAM_COMPAT_DATA_PATH="${STEAM_COMPAT_DATA_PATH:-$PWD/prefix}"; mkdir -p "$STEAM_COMPAT_DATA_PATH"
export WINEDLLOVERRIDES="winhttp=n,b"
exec "$PROTON" run ./TheLongDrive.exe -batchmode -nographics "$@"
EOF
chmod +x "$OUT/start-server.sh"
echo "server folder: $OUT ($(du -sh "$OUT" | cut -f1))"
echo "  start: $OUT/start-server.sh   (Windows: start-server.bat)   settings: $CFG"

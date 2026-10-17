#!/usr/bin/env bash
# The installer scripts on a stand-in game folder (no game needed): install, break two files and repair, uninstall,
# and check the folder ends up exactly as it started. Downloads the pinned zips unless --from DIR has them.
#   tools/installer-test.sh sh|ps1 [--from DIR]      (ps1 needs pwsh)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
KIND="$1"; shift
T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
G="$T/The Long Drive"; mkdir -p "$G/BepInEx/config"
echo game > "$G/TheLongDrive.exe"; echo "someone else's" > "$G/winhttp.dll"; echo settings > "$G/BepInEx/config/my.cfg"
snapshot() { (cd "$G" && find . -type f -exec sha256sum {} + | sort -k2); }
snapshot > "$T/before"
run() {   # run install|repair|uninstall [installer options...]
  local a="$1"; shift
  if [ "$KIND" = sh ]; then bash "$ROOT/installer/install.sh" --game-dir "$G" --"$a" --yes "$@"
  else pwsh -NoProfile -File "$ROOT/installer/install.ps1" -GameDir "$G" -"${a^}" -Yes "${@/--from/-From}"; fi
}
run install "$@" > "$T/out"
for f in winhttp.dll doorstop_config.ini BepInEx/core/BepInEx.Preloader.dll BepInEx/plugins/TLDRevamp/TLDRevamp.dll; do
  [ -f "$G/$f" ] || { cat "$T/out"; echo "FAIL: $f not installed"; exit 1; }
done
grep -qx "someone else's" "$G/RevaMP-installer/backup/winhttp.dll" || { echo "FAIL: the replaced winhttp.dll wasn't backed up"; exit 1; }
good=$(sha256sum "$G/BepInEx/core/BepInEx.dll" | cut -d' ' -f1)
echo broken > "$G/BepInEx/core/BepInEx.dll"; rm "$G/BepInEx/plugins/TLDRevamp/TLDRevamp.dll"
run repair "$@" > "$T/out"
[ "$(sha256sum "$G/BepInEx/core/BepInEx.dll" | cut -d' ' -f1)" = "$good" ] && [ -f "$G/BepInEx/plugins/TLDRevamp/TLDRevamp.dll" ] ||
  { cat "$T/out"; echo "FAIL: repair didn't put the files back"; exit 1; }
run uninstall > "$T/out"
snapshot > "$T/after"
diff "$T/before" "$T/after" || { cat "$T/out"; echo "FAIL: the folder isn't back to how it was"; exit 1; }
echo "installer ($KIND): install, repair and uninstall ok"

#!/usr/bin/env bash
# TLD RevaMP installer for Linux and Steam Deck (the game runs through Steam's Proton).
#
# Run it with no options and it asks what to do: install or update, repair, or uninstall. Then it shows exactly
# what it will download (with each file's SHA-256) and asks once more before changing anything.
#
# What an install does, in order (it prints each step):
#   1. Finds The Long Drive's folder through Steam's library list (or uses --game-dir).
#   2. Downloads BepInEx from BepInEx's own GitHub release and TLD RevaMP from this project's GitHub release, and
#      refuses to go on unless each file's SHA-256 matches the one written below.
#   3. Copies the files into the game folder. Any file it would replace is first copied to
#      <game folder>/RevaMP-installer/backup, and every file it adds is listed in RevaMP-installer/installed-files.txt.
#   4. Tells Proton to load BepInEx's winhttp.dll for this game only (the same thing as the launch option
#      WINEDLLOVERRIDES="winhttp=n,b" %command%), in the game's own Proton prefix.
#
# It never touches your saves, your settings, other games, or anything outside the game folder and the game's
# Proton prefix. It needs no root/sudo. `--repair` checks every installed file against the release and fixes what
# differs; `--uninstall` removes what it added and puts the backup back.
#
# Usage:  bash install.sh [options]      (all options are optional; without --install/--repair/--uninstall it asks)
#   --game-dir DIR   the game folder (the one with TheLongDrive.exe), if it isn't found by itself
#   --prefix DIR     the game's Proton folder (steamapps/compatdata/1017180), if it isn't next to the game
#   --from DIR       use zips you already downloaded into DIR instead of downloading (they are still checked)
#   --install        install or update, without the menu
#   --repair         check the installed files and put back any that are missing or changed, clear BepInEx's cache,
#                    move stray second copies of RevaMP out of BepInEx/plugins, and set the Proton override again
#   --uninstall      remove TLD RevaMP and BepInEx again and restore the backup
#   --yes            don't ask "Go ahead?" (needs --install, --repair or --uninstall, since there's no menu)
set -euo pipefail

# ---- Pinned downloads. tools/pin-installer.sh rewrites this block for each release; check the hashes against the
# ---- release page (GitHub shows each file's sha256) or against `sha256sum` of a zip you downloaded yourself.
REVAMP_VERSION="0.69.1"
REVAMP_URL="https://github.com/Normal99/TLD-RevaMP/releases/download/v0.69.1/TLD-RevaMP-0.69.1.zip"
REVAMP_SHA256="80cafe6ce09f581c01413c41fa5adfba4777471479f5d247ea5309451f4e9403"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip"
BEPINEX_SHA256="82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4"
# ---- end of pinned downloads

APP_ID=1017180          # The Long Drive's Steam app id
STATE=RevaMP-installer  # folder inside the game folder: the list of added files and the backup

say()  { printf '%s\n' "$*"; }
step() { printf '\n==> %s\n' "$*"; }
die()  { printf '\nSTOPPED: %s\n' "$*" >&2; exit 1; }

GAME="" COMPAT="" FROM="" ACTION="" YES=0
while [ $# -gt 0 ]; do
  case "$1" in
    --game-dir)  GAME="${2:?--game-dir needs a folder}"; shift 2 ;;
    --prefix)    COMPAT="${2:?--prefix needs a folder}"; shift 2 ;;
    --from)      FROM="${2:?--from needs a folder}"; shift 2 ;;
    --install)   ACTION=install; shift ;;
    --repair)    ACTION=repair; shift ;;
    --uninstall) ACTION=uninstall; shift ;;
    --yes|-y)    YES=1; shift ;;
    -h|--help)   sed -n '2,/^set -euo/p' "$0" | sed '$d; s/^# \{0,1\}//'; exit 0 ;;
    *)           die "unknown option '$1' (see --help)" ;;
  esac
done

# Questions are read from the terminal, also when the script itself came in through a pipe. Without a terminal to
# ask on, it stops rather than guess.
ask() { printf '%s' "$1"; read -r ANSWER < /dev/tty || die "no terminal to ask on: give --install, --repair or --uninstall, and --yes"; }
confirm() {   # the last question before anything changes (skipped with --yes)
  [ "$YES" = 1 ] && return 0
  ask $'\n'"$1 [y/N] "
  case "$ANSWER" in [yY]*) ;; *) say "Nothing was changed."; exit 0 ;; esac
}

# ---- 1. Find the game -------------------------------------------------------------------------------------------
# Steam keeps a list of its library folders in libraryfolders.vdf; the library with appmanifest_1017180.acf holds the
# game, in steamapps/common/<installdir>. These are the places Steam itself lives (normal, Flatpak, Snap).
steam_roots() {
  for d in "$HOME/.local/share/Steam" "$HOME/.steam/steam" "$HOME/.steam/root" \
           "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam" "$HOME/snap/steam/common/.local/share/Steam"; do
    [ -d "$d/steamapps" ] && (cd "$d" && pwd -P)
  done | sort -u
}
find_game() {
  local root lib acf dir
  for root in $(steam_roots); do
    # every `"path"  "/some/library"` line, plus the Steam folder itself
    { echo "$root"; sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$root/steamapps/libraryfolders.vdf" 2>/dev/null; } |
    while IFS= read -r lib; do
      acf="$lib/steamapps/appmanifest_$APP_ID.acf"
      [ -f "$acf" ] || continue
      dir=$(sed -n 's/^[[:space:]]*"installdir"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$acf" | head -1)
      [ -n "$dir" ] && [ -f "$lib/steamapps/common/$dir/TheLongDrive.exe" ] && echo "$lib/steamapps/common/$dir"
    done
  done | head -1
}

step "Looking for The Long Drive"
if [ -z "$GAME" ]; then
  GAME=$(find_game)
  [ -n "$GAME" ] || die "couldn't find the game through Steam. Give its folder with --game-dir (Steam > right-click The Long Drive > Manage > Browse local files shows it)."
fi
[ -f "$GAME/TheLongDrive.exe" ] || die "'$GAME' has no TheLongDrive.exe, so it isn't the game folder."
GAME=$(cd "$GAME" && pwd -P)
say "Game folder: $GAME"

# The game's Proton prefix sits in the same Steam library: <library>/steamapps/compatdata/1017180.
if [ -z "$COMPAT" ]; then COMPAT="$(dirname "$(dirname "$GAME")")/compatdata/$APP_ID"; fi
REG="$COMPAT/pfx/user.reg"
if [ -f "$REG" ]; then say "Proton prefix: $COMPAT"; else say "Proton prefix: not made yet ($COMPAT)"; fi

# (the process name Wine gives the game is TheLongDrive.ex)
if pgrep TheLongDrive >/dev/null 2>&1; then die "The Long Drive is running. Close it first."; fi

# ---- What to do: the menu ---------------------------------------------------------------------------------------
if [ -z "$ACTION" ]; then
  [ "$YES" = 1 ] && die "--yes needs --install, --repair or --uninstall (there's no menu to answer)."
  if [ -f "$GAME/$STATE/version" ]; then now="TLD RevaMP $(cat "$GAME/$STATE/version"), installed by this installer"
  elif [ -f "$GAME/BepInEx/plugins/TLDRevamp/TLDRevamp.dll" ]; then now="TLD RevaMP, installed by hand"
  else now="no TLD RevaMP"; fi
  step "What do you want to do?   (the game folder has $now)"
  say "  1) Install or update to TLD RevaMP $REVAMP_VERSION"
  say "  2) Repair: check every file against the release, fix what's missing or changed"
  say "  3) Uninstall: remove what the installer added, put back what it replaced"
  say "  4) Quit"
  while :; do
    ask "Choose 1-4: "
    case "$ANSWER" in 1) ACTION=install; break ;; 2) ACTION=repair; break ;; 3) ACTION=uninstall; break ;;
                      4|q|Q) say "Nothing was changed."; exit 0 ;; esac
  done
fi

# ---- The Proton DLL override ------------------------------------------------------------------------------------
# BepInEx starts through its own winhttp.dll next to the game. Proton prefers its built-in winhttp, so it has to be
# told to use the game folder's one first. That is a line "winhttp"="native,builtin" in the section
# [Software\\Wine\\DllOverrides] of the prefix's registry (pfx/user.reg, a text file). Only this game's prefix has it.
export REG_SECTION='[Software\\Wine\\DllOverrides]'   # exactly as user.reg writes it
export OVERRIDE_LINE='"winhttp"="native,builtin"'
set_override() {    # add our line under the section's header (and its #time line); drop any other winhttp line there
  local tmp; tmp=$(mktemp)
  if grep -qF "$REG_SECTION" "$REG"; then
    awk '/^\[/ { insec = (index($0, ENVIRON["REG_SECTION"]) == 1) }
         insec && /^\[/ { print; pending = 1; next }
         pending && /^#/ { print; next }
         pending { print ENVIRON["OVERRIDE_LINE"]; pending = 0 }
         insec && /^"winhttp"=/ { next }
         { print }
         END { if (pending) print ENVIRON["OVERRIDE_LINE"] }' "$REG" > "$tmp"
  else
    { cat "$REG"; printf '\n%s %s\n%s\n' "$REG_SECTION" "$(date +%s)" "$OVERRIDE_LINE"; } > "$tmp"
  fi
  cat "$tmp" > "$REG"; rm -f "$tmp"
}
winhttp_line() {    # the section's current winhttp line, if any
  awk '/^\[/ { insec = (index($0, ENVIRON["REG_SECTION"]) == 1) } insec && /^"winhttp"=/ { print; exit }' "$REG"
}
remove_override() {
  local tmp; tmp=$(mktemp)
  awk '/^\[/ { insec = (index($0, ENVIRON["REG_SECTION"]) == 1) } !(insec && /^"winhttp"=/) { print }' "$REG" > "$tmp"
  cat "$tmp" > "$REG"; rm -f "$tmp"
}

# ---- Uninstall ----------------------------------------------------------------------------------------------------
if [ "$ACTION" = uninstall ]; then
  [ -f "$GAME/$STATE/installed-files.txt" ] || die "this installer hasn't installed anything in $GAME (no $STATE/installed-files.txt)."
  step "Uninstall: this removes the $(wc -l < "$GAME/$STATE/installed-files.txt") files the installer added and puts back the ones it replaced"
  say "Left in place: BepInEx/config (your RevaMP settings), logs, reports, and any other mods you added yourself."
  confirm "Uninstall TLD RevaMP from $GAME?"
  while IFS= read -r f; do
    rm -f "$GAME/$f"; say "  removed   $f"
    d=$(dirname "$f")   # empty folders the installer made go too
    while [ "$d" != . ] && rmdir "$GAME/$d" 2>/dev/null; do d=$(dirname "$d"); done
  done < "$GAME/$STATE/installed-files.txt"
  # everything in the backup goes back where it was (files the installer replaced, and stray copies --repair moved)
  while IFS= read -r f; do
    mkdir -p "$(dirname "$GAME/$f")"; mv "$GAME/$STATE/backup/$f" "$GAME/$f"; say "  restored  $f"
  done < <(cd "$GAME/$STATE/backup" 2>/dev/null && find . -type f | sed 's|^\./||')
  if [ -f "$GAME/$STATE/proton-override" ] && [ -f "$REG" ]; then
    remove_override; say "  removed   the winhttp override from $REG"
    # if a different winhttp override was there before, put that one back
    old=$(cat "$GAME/$STATE/proton-override")
    if [ -n "$old" ]; then OVERRIDE_LINE="$old" set_override; say "  restored  the earlier override: $old"; fi
  fi
  rm -rf "${GAME:?}/$STATE"
  step "Done. The game is back to how it was before the installer first ran."
  exit 0
fi

# ---- 2. Download and check --------------------------------------------------------------------------------------
step "Plan"
if [ "$ACTION" = repair ]; then
  say "Repair TLD RevaMP $REVAMP_VERSION in $GAME: compare every file with these downloads and replace the ones that differ:"
else
  say "Install TLD RevaMP $REVAMP_VERSION with BepInEx into $GAME from:"
fi
say "  $BEPINEX_URL"; say "      sha256 $BEPINEX_SHA256"
say "  $REVAMP_URL";  say "      sha256 $REVAMP_SHA256"
if [ -f "$GAME/$STATE/version" ]; then say "(The installer put RevaMP $(cat "$GAME/$STATE/version") there before.)"; fi
say "Files it replaces that it didn't put there itself are backed up to $GAME/$STATE/backup first."
if [ -f "$REG" ]; then say "It also sets the winhttp DLL override in this game's Proton prefix ($REG)."; fi
confirm "Go ahead?"

TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
fetch() {   # fetch <url> <sha256>: into $TMP, checked; prints the file's path
  local url="$1" sum="$2" name; name=$(basename "$url")
  if [ -n "$FROM" ]; then
    [ -f "$FROM/$name" ] || die "$name isn't in $FROM"
    cp "$FROM/$name" "$TMP/$name"; say "  using     $FROM/$name" >&2
  else
    say "  download  $url" >&2
    if command -v curl >/dev/null; then curl -fsSL --retry 3 -o "$TMP/$name" "$url" || die "couldn't download $url"
    elif command -v wget >/dev/null; then wget -q -O "$TMP/$name" "$url" || die "couldn't download $url"
    else die "neither curl nor wget is installed"; fi
  fi
  local got; got=$(sha256sum "$TMP/$name" | cut -d' ' -f1)
  [ "$got" = "$sum" ] || die "$name has sha256 $got, expected $sum. The file isn't the one this installer was written for, so nothing was installed."
  say "  checked   sha256 ok" >&2
  echo "$TMP/$name"
}
unpack() {  # unpack <zip> <folder>: unzip, or bsdtar, or Python, whichever this system has
  mkdir -p "$2"
  if command -v unzip >/dev/null; then unzip -qo "$1" -d "$2"
  elif command -v bsdtar >/dev/null; then bsdtar -xf "$1" -C "$2"
  elif command -v python3 >/dev/null; then python3 -m zipfile -e "$1" "$2"
  else die "no unzip, bsdtar or python3 to open the zip with"; fi
}

step "Downloading"
BEPINEX_ZIP=$(fetch "$BEPINEX_URL" "$BEPINEX_SHA256")
REVAMP_ZIP=$(fetch "$REVAMP_URL" "$REVAMP_SHA256")
unpack "$BEPINEX_ZIP" "$TMP/bepinex"
unpack "$REVAMP_ZIP" "$TMP/revamp"

# What goes in: all of BepInEx (winhttp.dll, doorstop_config.ini, BepInEx/core), and from the RevaMP zip only its own
# plugin folder and mod-action preloader (the zip's BepInEx core duplicates BepInEx's release).
STAGE="$TMP/stage"; mkdir -p "$STAGE/BepInEx/plugins"
cp -a "$TMP/bepinex/." "$STAGE/"
cp -a "$TMP/revamp/BepInEx/plugins/TLDRevamp" "$STAGE/BepInEx/plugins/"
if [ -f "$TMP/revamp/BepInEx/patchers/TLDRevamp/TLDRevamp.Preloader.dll" ]; then
  mkdir -p "$STAGE/BepInEx/patchers/TLDRevamp"
  cp "$TMP/revamp/BepInEx/patchers/TLDRevamp/TLDRevamp.Preloader.dll" "$STAGE/BepInEx/patchers/TLDRevamp/"
fi

# ---- 3. Copy in, backing up -----------------------------------------------------------------------------------
step "Installing into $GAME"
mkdir -p "$GAME/$STATE/backup"; touch "$GAME/$STATE/installed-files.txt"
(cd "$STAGE" && find . -type f | sed 's|^\./||' | sort) | while IFS= read -r f; do
  if cmp -s "$STAGE/$f" "$GAME/$f"; then
    say "  same      $f"                       # already the right file
    grep -qxF "$f" "$GAME/$STATE/installed-files.txt" || echo "$f" >> "$GAME/$STATE/installed-files.txt"
    continue
  fi
  if grep -qxF "$f" "$GAME/$STATE/installed-files.txt"; then
    if [ -e "$GAME/$f" ]; then say "  replace   $f   (it differed)"; else say "  put back  $f   (it was missing)"; fi
  else
    if [ -e "$GAME/$f" ]; then                 # a file the installer didn't put there: keep a copy before replacing it
      mkdir -p "$(dirname "$GAME/$STATE/backup/$f")"; cp -a "$GAME/$f" "$GAME/$STATE/backup/$f"
      say "  replace   $f   (old one backed up)"
    else
      say "  add       $f"
    fi
    echo "$f" >> "$GAME/$STATE/installed-files.txt"
  fi
  mkdir -p "$(dirname "$GAME/$f")"; cp "$STAGE/$f" "$GAME/$f"
done
echo "$REVAMP_VERSION" > "$GAME/$STATE/version"

if [ "$ACTION" = repair ]; then
  step "Repair: BepInEx's cache and stray copies"
  # BepInEx rebuilds its cache at the next start; a stale one can keep it from loading plugins.
  if [ -d "$GAME/BepInEx/cache" ]; then rm -rf "${GAME:?}/BepInEx/cache"; say "  cleared   BepInEx/cache"; else say "  no BepInEx/cache to clear"; fi
  # A second TLDRevamp.dll anywhere else under plugins (an older version unzipped into the wrong folder) makes BepInEx
  # load two copies. Those are moved into the backup folder, not deleted.
  found=0
  while IFS= read -r f; do
    found=1; mkdir -p "$(dirname "$GAME/$STATE/backup/$f")"; mv "$GAME/$f" "$GAME/$STATE/backup/$f"
    say "  moved     $f   (a second copy of RevaMP; now in $STATE/backup)"
  done < <(cd "$GAME" && find BepInEx/plugins -iname 'TLDRevamp*.dll' -not -path 'BepInEx/plugins/TLDRevamp/*' 2>/dev/null)
  [ "$found" = 1 ] || say "  no stray copies of RevaMP in BepInEx/plugins"
fi

# ---- 4. Proton --------------------------------------------------------------------------------------------------
step "Proton"
if [ -f "$REG" ]; then
  if [ ! -f "$GAME/$STATE/proton-override" ]; then   # remember what was there, for --uninstall
    winhttp_line > "$GAME/$STATE/proton-override"
  fi
  set_override
  say "Set $OVERRIDE_LINE in $REG"
  say "No launch option is needed. (If the game ever starts without the RevaMP overlay, add the launch option below.)"
else
  say "The game's Proton prefix doesn't exist yet (the game has never been started). Either start the game once, close"
  say "it, and run  bash install.sh --repair  to set the override there, or add this launch option yourself:"
fi
say "  Steam > The Long Drive > Properties > General > Launch options:   WINEDLLOVERRIDES=\"winhttp=n,b\" %command%"

if [ "$ACTION" = repair ]; then step "Done: TLD RevaMP $REVAMP_VERSION is repaired"; else step "Done: TLD RevaMP $REVAMP_VERSION is installed"; fi
say "You need the game's beta branch: Steam > The Long Drive > Properties > Betas > beta."
say "Start the game: the top-left overlay shows \"TLD Revamp $REVAMP_VERSION\"."
say "If something is off, run  bash install.sh  again and choose Repair. Uninstall is in the same menu."

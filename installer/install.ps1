<#
TLD RevaMP installer for Windows.

Run it with no options and it asks what to do: install or update, repair, or uninstall. Then it shows exactly what
it will download (with each file's SHA-256) and asks once more before changing anything.

What an install does, in order (it prints each step):
  1. Finds The Long Drive's folder through Steam's library list (or uses -GameDir).
  2. Downloads BepInEx from BepInEx's own GitHub release and TLD RevaMP from this project's GitHub release, and
     refuses to go on unless each file's SHA-256 matches the one written below.
  3. Copies the files into the game folder. Any file it would replace is first copied to
     <game folder>\RevaMP-installer\backup, and every file it adds is listed in RevaMP-installer\installed-files.txt.

It never touches your saves, your settings, other games, the registry, or anything outside the game folder. It needs
no administrator rights (unless the game is installed somewhere only an administrator can write to).
-Repair checks every installed file against the release and fixes what differs; -Uninstall removes what it added and
puts the backup back.

How to run it: right-click install.ps1 > "Run with PowerShell". Or in a PowerShell window:
    powershell -ExecutionPolicy Bypass -File .\install.ps1 [options]
All options are optional; without -Install, -Repair or -Uninstall it asks.
  -Install       install or update, without the menu
  -GameDir DIR   the game folder (the one with TheLongDrive.exe), if it isn't found by itself
  -From DIR      use zips you already downloaded into DIR instead of downloading (they are still checked)
  -Repair        check the installed files and put back any that are missing or changed, clear BepInEx's cache,
                 and move stray second copies of RevaMP out of BepInEx\plugins
  -Uninstall     remove TLD RevaMP and BepInEx again and restore the backup
  -Yes           don't ask "Go ahead?" and don't wait for Enter at the end (needs -Install, -Repair or -Uninstall)
#>
param([string]$GameDir = "", [string]$From = "", [switch]$Install, [switch]$Repair, [switch]$Uninstall, [switch]$Yes)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # PowerShell 5's download progress bar makes downloads very slow

# ---- Pinned downloads. tools/pin-installer.sh rewrites this block for each release; check the hashes against the
# ---- release page (GitHub shows each file's sha256) or against Get-FileHash of a zip you downloaded yourself.
$REVAMP_VERSION = "0.69.1"
$REVAMP_URL = "https://github.com/Normal99/TLD-RevaMP/releases/download/v0.69.1/TLD-RevaMP-0.69.1.zip"
$REVAMP_SHA256 = "80cafe6ce09f581c01413c41fa5adfba4777471479f5d247ea5309451f4e9403"
$BEPINEX_URL = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip"
$BEPINEX_SHA256 = "82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4"
# ---- end of pinned downloads

$APP_ID = "1017180"           # The Long Drive's Steam app id
$STATE = "RevaMP-installer"   # folder inside the game folder: the list of added files and the backup

function Say([string]$text) { Write-Host $text }
function Step([string]$text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Done { if (-not $Yes) { Read-Host "`nPress Enter to close" | Out-Null }; exit 0 }
function Die([string]$text) {
    Write-Host ""; Write-Host "STOPPED: $text" -ForegroundColor Red
    if (-not $Yes) { Read-Host "`nPress Enter to close" | Out-Null }
    exit 1
}
# Ask once (skipped with -Yes).
function Confirm([string]$question) {
    if ($Yes) { return }
    $a = Read-Host "`n$question [y/N]"
    if ($a -notmatch '^[yY]') { Say "Nothing was changed."; Done }
}
function Sha256([string]$file) { (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLower() }

# ---- 1. Find the game -------------------------------------------------------------------------------------------
# Steam's folder is in the registry; it keeps a list of its library folders in steamapps\libraryfolders.vdf. The
# library with appmanifest_1017180.acf holds the game, in steamapps\common\<installdir>.
function Find-Game {
    $roots = @()
    foreach ($key in "HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam") {
        $p = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($p.SteamPath) { $roots += $p.SteamPath }
        if ($p.InstallPath) { $roots += $p.InstallPath }
    }
    foreach ($root in $roots) {
        $libs = @($root)
        $vdf = Join-Path $root "steamapps\libraryfolders.vdf"
        if (Test-Path -LiteralPath $vdf) {
            # lines like:   "path"   "D:\\SteamLibrary"   (backslashes are doubled in the file)
            foreach ($m in Select-String -LiteralPath $vdf -Pattern '^\s*"path"\s*"(.*)"\s*$') {
                $libs += $m.Matches[0].Groups[1].Value.Replace('\\', '\')
            }
        }
        foreach ($lib in $libs) {
            $acf = Join-Path $lib "steamapps\appmanifest_$APP_ID.acf"
            if (-not (Test-Path -LiteralPath $acf)) { continue }
            $m = Select-String -LiteralPath $acf -Pattern '^\s*"installdir"\s*"(.*)"\s*$' | Select-Object -First 1
            if (-not $m) { continue }
            $dir = Join-Path $lib ("steamapps\common\" + $m.Matches[0].Groups[1].Value)
            if (Test-Path -LiteralPath (Join-Path $dir "TheLongDrive.exe")) { return $dir }
        }
    }
    return ""
}

Step "Looking for The Long Drive"
if (-not $GameDir) {
    $GameDir = Find-Game
    if (-not $GameDir) { Die "couldn't find the game through Steam. Give its folder with -GameDir (Steam > right-click The Long Drive > Manage > Browse local files shows it)." }
}
if (-not (Test-Path -LiteralPath (Join-Path $GameDir "TheLongDrive.exe"))) { Die "'$GameDir' has no TheLongDrive.exe, so it isn't the game folder." }
$GameDir = (Resolve-Path -LiteralPath $GameDir).ProviderPath
Say "Game folder: $GameDir"
if (Get-Process -Name TheLongDrive -ErrorAction SilentlyContinue) { Die "The Long Drive is running. Close it first." }

$StateDir = Join-Path $GameDir $STATE
$Manifest = Join-Path $StateDir "installed-files.txt"
$BackupDir = Join-Path $StateDir "backup"

# Paths in installed-files.txt use / like the Linux installer's; these turn one into a full path.
function Under([string]$base, [string]$rel) { Join-Path $base ($rel -replace '/', [IO.Path]::DirectorySeparatorChar) }
function In-Game([string]$rel) { Under $GameDir $rel }
function In-Backup([string]$rel) { Under $BackupDir $rel }
function Ensure-Parent([string]$file) { New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file) | Out-Null }

# ---- What to do: the menu ---------------------------------------------------------------------------------------
if (-not ($Install -or $Repair -or $Uninstall)) {
    if ($Yes) { Die "-Yes needs -Install, -Repair or -Uninstall (there's no menu to answer)." }
    $versionFile = Join-Path $StateDir "version"
    if (Test-Path -LiteralPath $versionFile) { $now = "TLD RevaMP $(Get-Content -LiteralPath $versionFile), installed by this installer" }
    elseif (Test-Path -LiteralPath (In-Game "BepInEx/plugins/TLDRevamp/TLDRevamp.dll")) { $now = "TLD RevaMP, installed by hand" }
    else { $now = "no TLD RevaMP" }
    Step "What do you want to do?   (the game folder has $now)"
    Say "  1) Install or update to TLD RevaMP $REVAMP_VERSION"
    Say "  2) Repair: check every file against the release, fix what's missing or changed"
    Say "  3) Uninstall: remove what the installer added, put back what it replaced"
    Say "  4) Quit"
    while ($true) {
        $a = Read-Host "Choose 1-4"
        if ($a -eq "1") { $Install = $true; break }
        if ($a -eq "2") { $Repair = $true; break }
        if ($a -eq "3") { $Uninstall = $true; break }
        if ($a -eq "4" -or $a -eq "q") { Say "Nothing was changed."; Done }
    }
}

# ---- Uninstall ----------------------------------------------------------------------------------------------------
if ($Uninstall) {
    if (-not (Test-Path -LiteralPath $Manifest)) { Die "this installer hasn't installed anything in $GameDir (no $STATE\installed-files.txt)." }
    $files = @(Get-Content -LiteralPath $Manifest | Where-Object { $_ })
    Step "Uninstall: this removes the $($files.Count) files the installer added and puts back the ones it replaced"
    Say "Left in place: BepInEx\config (your RevaMP settings), logs, reports, and any other mods you added yourself."
    Confirm "Uninstall TLD RevaMP from ${GameDir}?"
    foreach ($f in $files) {
        Remove-Item -LiteralPath (In-Game $f) -Force -ErrorAction SilentlyContinue; Say "  removed   $f"
        # empty folders the installer made go too
        $d = Split-Path -Parent (In-Game $f)
        while ($d.Length -gt $GameDir.Length -and (Test-Path -LiteralPath $d) -and -not (Get-ChildItem -LiteralPath $d -Force)) {
            Remove-Item -LiteralPath $d -Force; $d = Split-Path -Parent $d
        }
    }
    # everything in the backup goes back where it was (files the installer replaced, and stray copies -Repair moved)
    if (Test-Path -LiteralPath $BackupDir) {
        foreach ($b in Get-ChildItem -LiteralPath $BackupDir -Recurse -File -Force) {
            $rel = $b.FullName.Substring($BackupDir.Length + 1) -replace '\\', '/'
            Ensure-Parent (In-Game $rel); Move-Item -LiteralPath $b.FullName -Destination (In-Game $rel) -Force; Say "  restored  $rel"
        }
    }
    Remove-Item -LiteralPath $StateDir -Recurse -Force
    Step "Done. The game is back to how it was before the installer first ran."
    Done
}

# ---- 2. Download and check --------------------------------------------------------------------------------------
Step "Plan"
if ($Repair) { Say "Repair TLD RevaMP $REVAMP_VERSION in ${GameDir}: compare every file with these downloads and replace the ones that differ:" }
else { Say "Install TLD RevaMP $REVAMP_VERSION with BepInEx into $GameDir from:" }
Say "  $BEPINEX_URL"; Say "      sha256 $BEPINEX_SHA256"
Say "  $REVAMP_URL"; Say "      sha256 $REVAMP_SHA256"
if (Test-Path -LiteralPath (Join-Path $StateDir "version")) { Say "(The installer put RevaMP $(Get-Content -LiteralPath (Join-Path $StateDir 'version')) there before.)" }
Say "Files it replaces that it didn't put there itself are backed up to $BackupDir first."
Confirm "Go ahead?"

$Tmp = Join-Path ([IO.Path]::GetTempPath()) ("revamp-install-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $Tmp | Out-Null
# GitHub needs TLS 1.2, which PowerShell 5 doesn't turn on by itself
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
function Fetch([string]$url, [string]$sum) {   # into $Tmp, checked; returns the file's path
    $name = [IO.Path]::GetFileName($url); $out = Join-Path $Tmp $name
    if ($From) {
        $src = Join-Path $From $name
        if (-not (Test-Path -LiteralPath $src)) { Die "$name isn't in $From" }
        Copy-Item -LiteralPath $src -Destination $out; Say "  using     $src"
    } else {
        Say "  download  $url"
        try { Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $out } catch { Die "couldn't download ${url}: $($_.Exception.Message)" }
    }
    $got = Sha256 $out
    if ($got -ne $sum) { Die "$name has sha256 $got, expected $sum. The file isn't the one this installer was written for, so nothing was installed." }
    Say "  checked   sha256 ok"
    return $out
}

try {
    Step "Downloading"
    $bepinexZip = Fetch $BEPINEX_URL $BEPINEX_SHA256
    $revampZip = Fetch $REVAMP_URL $REVAMP_SHA256
    Expand-Archive -LiteralPath $bepinexZip -DestinationPath (Join-Path $Tmp "bepinex") -Force
    Expand-Archive -LiteralPath $revampZip -DestinationPath (Join-Path $Tmp "revamp") -Force

    # What goes in: all of BepInEx (winhttp.dll, doorstop_config.ini, BepInEx\core), and from the RevaMP zip only its
    # own folder BepInEx\plugins\TLDRevamp (the zip's copy of BepInEx is the same files as BepInEx's release).
    $stage = Join-Path $Tmp "stage"
    Copy-Item -LiteralPath (Join-Path $Tmp "bepinex") -Destination $stage -Recurse
    New-Item -ItemType Directory -Force -Path (Under $stage "BepInEx/plugins") | Out-Null
    Copy-Item -LiteralPath (Under $Tmp "revamp/BepInEx/plugins/TLDRevamp") -Destination (Under $stage "BepInEx/plugins") -Recurse

    # ---- 3. Copy in, backing up -------------------------------------------------------------------------------
    Step "Installing into $GameDir"
    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
    $listed = @(); if (Test-Path -LiteralPath $Manifest) { $listed = @(Get-Content -LiteralPath $Manifest | Where-Object { $_ }) }
    $files = Get-ChildItem -LiteralPath $stage -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($stage.Length + 1) -replace '\\', '/' } | Sort-Object
    foreach ($f in $files) {
        $src = Under $stage $f; $dst = In-Game $f
        $exists = Test-Path -LiteralPath $dst
        if ($exists -and (Sha256 $src) -eq (Sha256 $dst)) {
            Say "  same      $f"   # already the right file
            if ($listed -notcontains $f) { $listed += $f }
            continue
        }
        if ($listed -contains $f) {
            if ($exists) { Say "  replace   $f   (it differed)" } else { Say "  put back  $f   (it was missing)" }
        } else {
            if ($exists) {   # a file the installer didn't put there: keep a copy before replacing it
                Ensure-Parent (In-Backup $f); Copy-Item -LiteralPath $dst -Destination (In-Backup $f) -Force
                Say "  replace   $f   (old one backed up)"
            } else { Say "  add       $f" }
            $listed += $f
        }
        Ensure-Parent $dst; Copy-Item -LiteralPath $src -Destination $dst -Force
        # write the list after every file, so an interrupted install can still be uninstalled
        Set-Content -LiteralPath $Manifest -Value $listed -Encoding ASCII
    }
    Set-Content -LiteralPath $Manifest -Value $listed -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $StateDir "version") -Value $REVAMP_VERSION -Encoding ASCII

    if ($Repair) {
        Step "Repair: BepInEx's cache and stray copies"
        # BepInEx rebuilds its cache at the next start; a stale one can keep it from loading plugins.
        $cache = In-Game "BepInEx/cache"
        if (Test-Path -LiteralPath $cache) { Remove-Item -LiteralPath $cache -Recurse -Force; Say "  cleared   BepInEx\cache" } else { Say "  no BepInEx\cache to clear" }
        # A second TLDRevamp.dll anywhere else under plugins (an older version unzipped into the wrong folder) makes
        # BepInEx load two copies. Those are moved into the backup folder, not deleted.
        $ours = In-Game "BepInEx/plugins/TLDRevamp"
        $strays = @(Get-ChildItem -LiteralPath (In-Game "BepInEx/plugins") -Recurse -File -Filter "TLDRevamp*.dll" -ErrorAction SilentlyContinue |
                    Where-Object { -not $_.FullName.StartsWith($ours + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })
        foreach ($s in $strays) {
            $rel = $s.FullName.Substring($GameDir.Length + 1) -replace '\\', '/'
            Ensure-Parent (In-Backup $rel); Move-Item -LiteralPath $s.FullName -Destination (In-Backup $rel) -Force
            Say "  moved     $rel   (a second copy of RevaMP; now in $STATE\backup)"
        }
        if ($strays.Count -eq 0) { Say "  no stray copies of RevaMP in BepInEx\plugins" }
    }
} finally {
    Remove-Item -LiteralPath $Tmp -Recurse -Force -ErrorAction SilentlyContinue
}

if ($Repair) { Step "Done: TLD RevaMP $REVAMP_VERSION is repaired" } else { Step "Done: TLD RevaMP $REVAMP_VERSION is installed" }
Say "You need the game's beta branch: Steam > The Long Drive > Properties > Betas > beta."
Say "Start the game: the top-left overlay shows `"TLD Revamp $REVAMP_VERSION`"."
Say "If something is off, run install.ps1 again and choose Repair. Uninstall is in the same menu."
Done

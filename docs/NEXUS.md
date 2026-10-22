# Nexus Mods listing copy

Ready-to-paste copy for the Nexus Mods pages of TLD RevaMP and its add-ons, plus the checklist a person works
through when uploading.

**Nothing here has been uploaded yet.** These are drafts of the text. No Nexus page for TLD RevaMP or its add-ons has
been created or updated by this work, and no file has been sent to Nexus Mods. Every step in the upload checklist
below still has to be done by a person, by hand.

Where the copy names a version, a file name or a checksum, it names the one the project currently has:

| | |
| --- | --- |
| Core mod | **TLD RevaMP 0.69.1** |
| Core file | `TLD-RevaMP-0.69.1.zip` |
| Add-ons in the catalog | Head Unit 0.2.0, Car Trailer 0.1.1, World Map 0.2.0, Trailer Hitches 0.1.0, BusGut 0.2.0, MountAnything 0.2.1, TerrainChoice 0.4.1 |
| Game | The Long Drive (Steam app id `1017180`), **beta branch** only |
| Wire protocol | **51** |
| Source | <https://github.com/Normal99/TLD-RevaMP> |
| Issues and support | <https://github.com/Normal99/TLD-RevaMP/issues> |

If the version numbers below no longer match the newest release when you upload, update them first. The source of
truth for the catalog is `packaging/mods.json`; the source of truth for what a player may install is
`packaging/approved-mods.txt`.

---

## Listing 1 — TLD RevaMP (core mod)

### Title

```
TLD RevaMP - Performance fixes and seamless multiplayer for The Long Drive
```

### Category and game

- Game: **The Long Drive** (Steam, PC)
- Mod type: **Gameplay / Multiplayer / Performance**
- Mod loader: **BepInEx 5**
- The game must be on the **beta branch**.

### Short description (for the file listing)

```
Makes The Long Drive run smoother and adds real multiplayer: the other players are in your world, not in a lobby. BepInEx 5 mod, PC, beta branch of The Long Drive.
```

### Full description

```
TLD RevaMP is a mod for The Long Drive on Steam (PC). It does two things: it makes the game faster, and it gives
you multiplayer that feels like the single-player game.

PERFORMANCE
Mirrors draw a second view of the world every frame, the sun's shadows are drawn in four layers, and cars and items
are built from hundreds of small parts that are each drawn separately. RevaMP trims that work back. Every option has
a default that was checked in the game and chosen so that it looks the same as the original while you play, and
every option is explained where you change it. You can also slow the game down a little in exchange for a little
speed, if you want to.

MULTIPLAYER THAT LOOKS LIKE THE GAME
This is not a lobby game with abstract cars. The other players are there. They drive past you, sit in your passenger
seat, shoot the same rabbits, open the same door, and their car falls apart in the same way yours does. Every machine
simulates the cars, items and animals near its own player, so you can drive off in opposite directions and still be
in the same world.

You can share: players (walking, sitting, driving, riding, footsteps), cars (driving, crashes, damage, parts coming
off, fuel and fluids), loose items (carry, throw, drop), doors and gates, animals (hunting, attacks, bodies), guns,
bullet holes, explosions, sandstorms, tumbleweeds and the radio.

Multiplayer runs over Steam's own networking, so there is no port forwarding to set up and no server to rent. If you
are friends on Steam, you can play.

TLD LOADER MODS
RevaMP also runs existing TLD Loader mods: put their DLLs in
BepInEx\TLDLoaderMods and they load on the next start, with their logger, their
config folder and their usual lifecycle calls (OnMenuLoad in the menu, OnLoad when
the world is up, OnUnload when it goes away, Update, FixedUpdate and OnGUI). There
must be no other mod loader installed at the same time.

REQUIREMENTS
- Your own copy of The Long Drive on Steam, on the BETA branch.
  Steam > Library > right-click The Long Drive > Properties > Betas > choose "beta".
- Everyone in a multiplayer session must run the same version. The game refuses the join and tells you when the
  versions differ, because two different builds genuinely disagree about the world.
- No other mod loader in the game folder (remove an old TLDLoader or ModLoader install first).

INSTALLATION - THE EASY WAY
The recommended install is the installer script on the GitHub releases page. It finds your game, checks every
download against a checksum written into the script, backs up anything it replaces, and can also repair or uninstall.

  Windows:    download install.ps1, right-click it, choose "Run with PowerShell".
  Linux / Steam Deck: download install.sh, then run "bash install.sh" in a terminal
  (on the Steam Deck, use Desktop Mode).

It asks what you want to do (install or update, repair, uninstall), shows you what it will download, and asks once
before it changes anything. It never touches your saves, your settings or other games, and needs no administrator or
root rights. On Linux it also sets one Proton setting for this game only, so you do not need a launch option.

MANUAL INSTALLATION
Download TLD-RevaMP-<version>.zip and unpack it into your game folder (Steam > right-click The Long Drive > Manage >
Browse local files shows you where that is). winhttp.dll has to end up next to TheLongDrive.exe. The zip contains
INSTALL.txt with the same steps, and how to uninstall.

On Linux and the Steam Deck, also set this launch option unless the installer did it for you:
  Steam > The Long Drive > Properties > General > Launch options
  WINEDLLOVERRIDES="winhttp=n,b" %command%

Start the game. A small overlay in the top-left corner shows "TLD Revamp" and the version. That means it loaded.

PLAYING TOGETHER
Press F11 (or use the game's own Multiplayer button in the pause menu or main menu).
  Host: load or start your world, then press Host. A password is optional.
  Join: pick the friend who is hosting and press Join. Type the password if there is one.
Your game loads the HOST's world. The host's machine owns the world and the save; joining never touches your own
saves. While the join loads, a muted loading screen shows each stage and the object counts until the ground under
you and the objects around you are really there. Escape cancels; after 60 seconds without progress you can continue
anyway.

Text chat is Enter. Voice is the game's own Voice chat key (K if you never set one) and only nearby players (about
60 m) hear you. The host can mute and kick players, and players can ask to teleport to each other with /tpa.

UPDATING
Run the new release's installer and choose "Install or update", or close the game and copy the new zip's files over
the old ones. Your saves and settings stay. Everyone in your session should update too.

THIS IS A WORK IN PROGRESS
Expect bugs. Please back up your saves before playing a test build. Some things may still be missing or occasionally
wrong - a car slightly out of place for a moment, a door that does not quite line up. If you find a bug, press
Page Down in the game while it happens: that writes a small zip with the game state, the performance and network
numbers and your hardware models, with screenshots, logs, player names and file paths left out.

Please DO NOT attach those report zips to a public issue or upload them anywhere. GitHub issues and Nexus comments
are public. Describe the bug and when it happened in the issue, and keep the file on your own computer - we will
ask for it privately if we need it.

SUPPORT AND BUG REPORTS
https://github.com/Normal99/TLD-RevaMP/issues

Use that tracker for bugs, questions and anything serious. This Nexus page is for the download and the description;
we do not read support requests here. Include your RevaMP version and whether you hosted or joined.

SOURCE CODE AND LICENCE
The project is open source under the GNU General Public License v3:
https://github.com/Normal99/TLD-RevaMP

BepInEx 5 (MIT), HarmonyX (MIT) and UnityDoorstop (LGPL-2.1) are bundled unmodified; their licence texts ship in
the release zip as BepInEx/licenses. The Long Drive is by Gab and Genesz.
```

### Version notes for the listing

Paste the release's own notes here when you upload. The current release page carries them, and they are written for
players rather than for developers.

### Files to attach

| File | Notes |
| --- | --- |
| `TLD-RevaMP-<version>.zip` | The mod itself. **This is the file users download.** It is already the installer-friendly layout: `winhttp.dll`, `doorstop_config.ini`, `BepInEx/`, `INSTALL.txt`. |
| `install.ps1` | Optional extra, for the Windows one-click install. |
| `install.sh` | Optional extra, for Linux and the Steam Deck. |
| `SHA256SUMS` | Optional extra. |

Do **not** attach the add-on zips to the core listing. They have their own listings (below) and the mod browser
already offers them.

---

## Listing 2 — Head Unit

### Title

```
Head Unit - a digital head unit for The Long Drive, shared in multiplayer
```

### Short description

```
Rare loot for some cars: a digital head unit with a radio, a YouTube queue everyone in the car shares, and navigation. Works in multiplayer.
```

### Full description

```
A digital head unit that fits in the radio slot of some cars. It is rare loot, found in a few models, and each
model fits only some cars. The game's own radio keeps working inside it.

The unit has a touch screen (look at a button and click, as with the game's knobs) with three tabs:
  - RADIO: the game's own radio.
  - YOUTUBE: a queue everyone in the car shares. Everyone in or near the car sees the same video at the same moment,
    and each player has their own volume. Add a link with the + ADD box; you can also search YouTube from the box
    and read the results from the seat.
  - NAV: a map of the roads around the car and the bends and descents ahead, with the main road in its own colour.

A unit can also be glued anywhere in the car, like any item - then only the screen shows.

The first time you add a link, the unit offers to download yt-dlp (and, for anything above 360p, ffmpeg and Deno)
from their official GitHub releases, each checked against that release's checksums. Nothing is downloaded until you
accept on the screen.

REQUIRES
- TLD RevaMP (this is an add-on for it).
- The Long Drive on Steam, beta branch.
- Everyone in a multiplayer session must run the same version of this mod.

INSTALLATION
Install the mod browser inside RevaMP: Settings > Revamp > Mods, pick Head Unit, press Install, restart the game.
It is also installed by hand: unzip HeadUnit-<version>.zip into your game folder, so that the file ends up at
BepInEx/plugins/HeadUnit/HeadUnit.dll.

Supports
https://github.com/Normal99/TLD-RevaMP/issues
```

### Version notes

```
0.2.0
- YouTube queue shared with everyone in the car, with per-player volume.
- Navigation tab: the roads around the car and the bends and descents ahead.
- Links still work; YouTube search results readable from the seat.
- Videos are converted where needed so they also play on Steam's own Proton.
- Joiners on an older Head Unit are offered 0.2.0 by the host.
```

### Files to attach

| File | Notes |
| --- | --- |
| `HeadUnit-<version>.zip` | Contains `BepInEx/plugins/HeadUnit/HeadUnit.dll`. |

---

## Listing 3 — Car Trailer

### Title

```
Car Trailer - a trailer for carrying a car, shared in multiplayer
```

### Short description

```
A trailer for hauling a second car, built from the game's own parts and shared between players like the game's own trailer.
```

### Full description

```
A trailer for carrying a car, built from the game's own parts: the Brakas transporter's bed (meshes, colliders and
hinged ramp) low between four wheels, on the game's own trailer, with its own hitching, brakes, lights and saving.

A car driven up the ramp is held the way the Brakas holds one: it is listed in the trailer's storage area with its
wheels locked. It is rare loot, found at the places the game's trailers and vehicles spawn.

The trailer is shared in multiplayer with no extra code: it is a game item, so the game saves and loads it, and RevaMP
shares it like the game's own trailer, including a car carried on it. A carried car follows whichever machine is
simulating its trailer.

Note for multiplayer: the join only checks mods that register with the multiplayer API, and this one does not, so
every player has to install it by hand.

REQUIRES
- TLD RevaMP (this is an add-on for it).
- The Long Drive on Steam, beta branch.

INSTALLATION
Install the mod browser inside RevaMP: Settings > Revamp > Mods, pick Car Trailer, press Install, restart the game.
It is also installed by hand: unzip CarTrailer-<version>.zip into your game folder, so that the file ends up at
BepInEx/plugins/CarTrailer/CarTrailer.dll.

Supports
https://github.com/Normal99/TLD-RevaMP/issues
```

### Files to attach

| File | Notes |
| --- | --- |
| `CarTrailer-<version>.zip` | Contains `BepInEx/plugins/CarTrailer/CarTrailer.dll`. |

---

## Listing 4 — World Map

### Title

```
World Map - a carryable map for The Long Drive, with live player markers
```

### Short description

```
A paper map item with your explored routes, the places you have been, and live markers for the other players. Works in multiplayer.
```

### Full description

```
A map you can carry, drawn on paper, with a north-up global-coordinate map. Hold or equip it and press M to open the
larger view; the map on the item in your hand shows it live.

It draws:
  - the routes you have travelled, as sampled travel trails,
  - places you have visited, as ochre markers,
  - the other players in the session, as live markers.

Zoom and pan controls let you browse beyond the default 6 km view: the arrow keys pan, Page Up and Page Down zoom,
Home centres, and M (or Escape) closes the larger view. In the larger view you can drag with the mouse and use the
wheel or the buttons.

Route and landmark data is saved by map seed in BepInEx/config/WorldMap/. The rendered map images stay on your own
computer.

In multiplayer, only compact coordinate updates cross the network: positions are sent at one metre resolution, live
player markers update once a second, and route samples are sent every 24 metres. A late joiner receives a snapshot.

REQUIRES
- TLD RevaMP (this is an add-on for it).
- The Long Drive on Steam, beta branch.
- Everyone in a multiplayer session must run the same version of this mod.

INSTALLATION
Install the mod browser inside RevaMP: Settings > Revamp > Mods, pick World Map, press Install, restart the game.
It is also installed by hand: unzip WorldMap-<version>.zip into your game folder, so that the file ends up at
BepInEx/plugins/WorldMap/WorldMap.dll.

Supports
https://github.com/Normal99/TLD-RevaMP/issues
```

### Files to attach

| File | Notes |
| --- | --- |
| `WorldMap-<version>.zip` | Contains `BepInEx/plugins/WorldMap/WorldMap.dll`. |

---

## Listing 5 — Trailer Hitches

### Title

```
Trailer Hitches - receiver hitches for cars that cannot take the stock drawbar
```

### Short description

```
Adds a visible receiver hitch to driveable cars whose existing slots cannot accept the game's stock trailer. Works in multiplayer.
```

### Full description

```
Adds a visible receiver hitch to driveable cars whose existing slots cannot accept the stock trailer drawbar, so
those cars can tow the game's trailer after all.

The receiver is the game's own stock assembly, so it looks and behaves like the one on a car that already has it.
Cars that already have a compatible stock receiver are left unchanged. Bicycles and motorcycles are excluded.

Known limits, so there are no surprises: the hitch position is a table of measured positions per car model, and a
model that is not in the table is skipped and logged rather than guessed at. New game models need a reviewed table
entry.

Removing this mod while a trailer is hitched needs the current RevaMP core: RevaMP leaves a trailer drawbar loose
when the car it was attached to has no such slot any more, so the next load does not break. If you remove both this
mod and RevaMP, detach the trailer by hand first.

REQUIRES
- TLD RevaMP (this is an add-on for it).
- The Long Drive on Steam, beta branch.
- Everyone in a multiplayer session must run the same version of this mod, and the same hitch configuration: the
  mod includes a fingerprint of its placement settings in its multiplayer identity, and a session with different
  configurations is refused.

INSTALLATION
Install the mod browser inside RevaMP: Settings > Revamp > Mods, pick Trailer Hitches, press Install, restart the
game. It is also installed by hand: unzip TrailerHitches-<version>.zip into your game folder, so that the file ends
up at BepInEx/plugins/TrailerHitches/TrailerHitches.dll.

Supports
https://github.com/Normal99/TLD-RevaMP/issues
```

### Version notes

```
0.1.0
- First release: receiver hitches for the car models that could not take the stock trailer drawbar.
```

### Files to attach

| File | Notes |
| --- | --- |
| `TrailerHitches-<version>.zip` | Contains `BepInEx/plugins/TrailerHitches/TrailerHitches.dll`. |

---

## Listings 6–8 — the third-party add-ons

**BusGut**, **MountAnything** and **TerrainChoice** are written by **YujiroX2**, not by this project. They are in
the mod browser because their authors asked for it and their file hashes are on the approved list. Do not upload
these to a Nexus account that does not represent their author without saying so.

Two options, and the second is better:

1. **Point to the author's own page.** The canonical location for each is the GitHub release its catalog entry names,
   and the catalog records the author's own repository as the source. Put that link in the description.
2. **Post them here with a clear credit.** Only with YujiroX2's agreement, and with the author's own
   description and their own screenshots. Keep the author credited by name in the title or the first line, and keep
   the support link pointing at whoever owns the mod.

What each one does, from `packaging/mods.json`:

| Add-on | Version | What it does |
| --- | --- | --- |
| **BusGut** | 0.2.0 | Empties the buses into cargo space: passenger seats and handrails removed, driver seat and dashboard kept. |
| **MountAnything** | 0.2.1 | Mount any held item anywhere: on a car, the ground, a building or another item, with a placement preview. |
| **TerrainChoice** | 0.4.1 | Pick the terrain for a new game: crossroads, main road only, or the old 2023 terrain. |

If you post them, each listing needs the same structure as the others: a title with the author's name, a short
description, a full description saying who made it, requirements, installation, a support line pointing at the right
tracker, the version notes, and the file to attach. Do not claim these are tested by this project beyond what the
catalog records.

---

## Version policy

This is what to say in every listing, and it is what the project actually does:

- **A version means a build.** Everyone in a multiplayer session must run the same version, and the game says so
  when they do not. A join across versions is refused, because two builds would each see a slightly different world.
- **The wire protocol is part of that.** The current protocol is 53. Two builds with the same version number but
  different protocol numbers are also refused, with a message that says "different builds of the same version".
- **Stable releases** are published from the project's `main` branch as a GitHub release tagged `v<version>`. The
  installer scripts are pinned to that release and their checksums are written into the script.
- **Pre-releases** come from the `dev` branch. Their version says so, and they are never marked as the latest
  release. A Nexus page should list the stable release as the current version and mention pre-releases as testing
  builds if you link them at all.
- **Dev builds** are drafts or public pre-releases tagged `v<version>-dev.<commit>`. Only people with write access
  can see a draft. Do not publish these on Nexus as the current version.
- **One file per version.** When a new version is out, the old file stays downloadable as an old version, so people
  mid-session are not left without a working download. Say in the description which version is current.

So: when you upload, put the **newest stable release** as the current file, keep the previous one as an old file, and
never present a dev build as the current version.

---

## Image and screenshot checklist

Nexus pages look empty without images. The repository has **no image files at all** today, so every image below has
to be taken and uploaded by a person.

Take them from the real game, on the beta branch, with RevaMP installed. Do not use images from the game's own
store page or from another mod.

**For the core listing, at least:**

- [ ] **The multiplayer screen** (F11) with a session running - players listed, so the reader sees the multiplayer
      is a real screen in the game, not a separate app.
- [ ] **Two players in the same world** - the clearest possible shot of the point of the mod: one car, another
      player's car or another player standing next to it.
- [ ] **The performance overlay** in the top-left corner (Page Up) while driving, showing the frame rate.
- [ ] **Settings > Revamp** open, so the reader knows the settings exist and that they are explained.
- [ ] **Settings > Revamp > Mods**, the mod browser, with the approved add-ons listed.

**For each add-on listing:**

- [ ] The add-on in the game: the head unit in a car, the trailer with a car on it, the map open, a hitched trailer.
- [ ] A second shot of the thing it actually does: the head unit's YouTube tab, the map with player markers, the
      trailer hitched and about to be towed.

**For every image, all of these:**

- [ ] Taken by you, from your own playthrough, in the real game.
- [ ] Readable at Nexus's usual thumbnail size - the interesting part is large in the frame, not a small detail.
- [ ] No other player's Steam name, no Steam ID, no Windows user name, no file path, no chat text, no other
      modder's logo.
- [ ] No watermarks or branding that is not yours, and nothing from the game's store page or another mod's page.
- [ ] Saved as PNG, sized reasonably (a full-screen 1080p PNG is fine; do not upload 4K originals).

---

## Human upload checklist

Everything below is a manual action. Work down it for each listing, one page at a time.

**Before you start**

- [ ] Confirm the version numbers you are about to publish match the newest release on the GitHub releases page, and
      update the copy in this file if they do not.
- [ ] Confirm the game is on the **beta branch** and that you have installed the release you are about to publish.

**Files**

- [ ] Download the release zip from the GitHub release.
- [ ] Check its SHA-256 against the one on the release page (the release also carries a `SHA256SUMS` file).
- [ ] Upload the zip to Nexus as the main file for the listing.
- [ ] Upload `install.ps1` and `install.sh` as optional extra files on the core listing only.
- [ ] For each add-on, upload its own zip (for example `HeadUnit-0.2.0.zip`). **Do not upload add-on zips on the core
      listing**, and do not upload a different add-on's file under the wrong name.
- [ ] Give every uploaded file a version in Nexus's version field. Set the newest stable release as the current
      version, and the previous one as an old version.

**Text**

- [ ] Paste the title, short description and full description from the matching listing above, and adjust only the
      version numbers if they have moved on.
- [ ] Paste the version notes for the version you are publishing.
- [ ] Check that every support link in the copy points at <https://github.com/Normal99/TLD-RevaMP/issues>.
- [ ] Check that the description says clearly that support and bug reports go to GitHub issues, and that Nexus
      comments are not read for support. This is the whole point of issue #38: **users are directed to GitHub for any
      issues or serious posts.**
- [ ] Check that the description keeps the warning not to attach report zips, marks or play logs publicly, and not
      to upload them anywhere.
- [ ] Check that the licence is named correctly (GPL-3.0 for RevaMP and the add-ons that use its code) and that the
      bundled third-party licences (BepInEx MIT, HarmonyX MIT, UnityDoorstop LGPL-2.1) are named correctly.

**Images**

- [ ] Take every screenshot listed in the image checklist above, in game, on the beta branch.
- [ ] Check each one for other people's names, Steam IDs, file paths and chat text, and crop or retake anything that
      shows them.
- [ ] Upload them, with a short caption each that says what is on screen.

**Third-party add-ons**

- [ ] If you post BusGut, MountAnything or TerrainChoice, get YujiroX2's agreement first, and credit them by
      name in the title or first line.
- [ ] Point their support line at the tracker that belongs to their author, not at this project's.

**After publishing**

- [ ] Open the published page and read it as a new player would. Check the install steps, the version numbers and
      the support link.
- [ ] Follow your own install steps on a clean game folder, to prove the text works.
- [ ] Record the Nexus page URL and the file id somewhere, so the next version can be uploaded to the same page
      rather than a new one.
- [ ] Tell the project where the pages live, so the README and the release notes can link to them.

**Do not**

- [ ] Do not claim that a version was uploaded to Nexus unless you have actually done it and seen the file listed.
- [ ] Do not upload a dev build as the current version.
- [ ] Do not upload the game, any game files, or anything from the game's own installation.

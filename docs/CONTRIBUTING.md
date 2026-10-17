# Contributing

Issues and pull requests are welcome. This page covers reading the game's code, building, and testing a change.

## Read the game's code: decompile it yourself

Most of the mod is Harmony patches on the game's own methods, so you will want the game's code next to the mod's. The
repository never contains it (no game files, no decompiled code: that code belongs to the game's developers), but you
can decompile your own copy, and we recommend it before working on anything that patches the game.

The game is a Unity Mono build, so its scripts are plain .NET assemblies in `TheLongDrive_Data/Managed/`:
`Assembly-CSharp.dll` (almost everything) and `Assembly-CSharp-firstpass.dll`.

With [ILSpy](https://github.com/icsharpcode/ILSpy)'s command line (any OS with the .NET SDK):

```sh
dotnet tool install -g ilspycmd
GAME="$HOME/.local/share/Steam/steamapps/common/The Long Drive"     # your install
ilspycmd -p -o ~/tld-revamp-cache/game-src "$GAME/TheLongDrive_Data/Managed/Assembly-CSharp.dll"
```

`-p` writes a project with one `.cs` file per type, which you can open in an editor or IDE and search. The ILSpy GUI
(Windows) or dnSpyEx work too. Some starting points: `mainscript` (the game's main state, `mainscript.s`),
`carscript` (cars), `objGenScript` (world generation and streaming), `menuhandler` / `DataFromMenuScript` (menu, seed).

Rules for the decompiled output:

- Keep it **outside this repository** (as above, or anywhere else) and never commit it, attach it or paste it into
  issues or pull requests. Refer to code by type and method name (`carscript.Update`) instead.
- It is for reading. The mod's code is written anew; don't copy the game's methods into it.
- Decompile again after a game update. `tools/gamediff.py` does that into its own cache and lists which methods the
  mod patches or calls have changed (see [tools/README.md](../tools/README.md)).

## Build

The game must be on Steam's **beta** branch, with BepInEx 5.4.23. Then, as in the [README](../README.md#building-from-source):

```sh
tools/libs.sh     # copies the game's and BepInEx's reference assemblies into lib/ (never committed)
tools/build.sh    # builds, copies the DLLs into the game's BepInEx/plugins/TLDRevamp/, turns the debug bridge on
```

`TLD_DIR=/path/to/The Long Drive` points both at an install outside Steam's default library. `build.sh` turns the
debug bridge on in that install's config; set `Bridge = false` in `BepInEx/config/tldrevamp.core.cfg` again before
playing normally there.

## Test

`tools/doctor.py` checks your setup, read-only. [tools/README.md](../tools/README.md) explains the test tools: launching the game, seeded test worlds, benchmarks and
the two-machine multiplayer scenarios. A pull request is easiest to review with the test you ran, or a new script in
`tools/` that shows the bug and the fix.

## Pull requests

The **Settings → Revamp → Mods** browser uses `packaging/mods.json` (format 1, unknown fields ignored). Versions are
newest first; URLs must be HTTPS GitHub release assets. `file` names the exact DLL inside a ZIP; omit it for a bare
DLL. `size` is the download size; `sha256` hashes the DLL, not the archive. `revamp` is a displayed minimum version.
The catalog grants no approval: the hash, id and version must match the local `packaging/approved-mods.txt` shipped
with RevaMP. Adding catalog metadata and approving executable code are separate review decisions. Keep third-party
DLLs out of the repository. Run `dotnet run --project tools/unit/ModCatalogTest.csproj` for the offline browser tests.
To try an unpublished catalog in game, set `[Mods] CatalogUrl` to a `file:///absolute/path/to/packaging/mods.json`
URI in `BepInEx/config/tldrevamp.core.cfg`, then open Mods. Mod downloads still require GitHub release-asset URLs
and local approval; this changes only where discovery metadata is read.

CI checks every pull request: the tools compile (Python 3.9 and the latest) and their helper tests pass, the network
reader's unit test passes, and `tools/privacy_scan.py` finds nothing. Run that scan before you push; it looks at what
git tracks.

- One change per pull request, and say what you saw in game before and after.
- Contributions are under the project's licence, GPL-3.0 ([COPYING](../COPYING)).
- Multiplayer changes: everyone in a session runs the same version, so describe what happens with players on
  different machines (who owns the object, what the other player sees).
- No game files, decompiled code, other mods' DLLs or personal data (paths, Steam IDs, addresses) in commits.

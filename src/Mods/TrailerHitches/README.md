# Trailer Hitches

Adds a visible receiver hitch only to driveable car prefabs whose existing slots cannot accept the stock trailer,
including `car06p` and `car09` variants. The receiver is a normal `partslotscript` registered in the car's save record
and accepts the stock trailer drawbar. Cars with a compatible stock receiver are left unchanged. The trailer keeps the
game's own `attachgripscript`, coupler, attachment, and save behavior.

The mod registers as `trailerhitches` with TLD RevaMP's multiplayer API when available. RevaMP synchronizes the
result through the game's ordinary attachment records; everyone in a multiplayer session must use the same mod
version. Without RevaMP, it works in single player.

## Build

From the TLD RevaMP repository root:

```sh
dotnet build src/Mods/TrailerHitches/TrailerHitches.csproj -c Release
```

Install `src/Mods/TrailerHitches/bin/Release/netstandard2.1/TrailerHitches.dll` under
`BepInEx/plugins/TrailerHitches/`. Source licensing follows the repository's GPL-3.0 license in
[COPYING](../../../COPYING).

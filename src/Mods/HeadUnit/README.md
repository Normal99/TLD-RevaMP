# Head Unit

A digital head unit for a car's radio slot, and the main example of TLD RevaMP's multiplayer mod API.

In the game: rare loot in a few models, each fitting only some cars. The game's radio keeps working inside it. Its
touch screen (look at a button and click, as with the game's knobs) has three tabs: the radio, YouTube with a queue,
and navigation (a map of the roads around the car and the bends and descents ahead). A unit can also be glued
anywhere in the car, like any item. In a session, everyone in or near the car sees the same video at the same moment.

## What it shows

- **Registering a mod and a shared type.** `Plugin.Awake` calls `MpApi.RegisterMod` and `MpApi.RegisterType`; the
  join then checks every player has the same mod.
- **State as a record.** `State.cs`: what a car's unit shares (the queue, what plays and from when, the volume) is
  one record, written and read by `UnitStateType`, with a size cap (`MaxRecordBytes`). Edits from players go
  through `OnEdit`, which checks them before applying.
- **Sharing an object tied to a game object.** `UnitBehaviour.cs` makes sure the car is shared
  (`MpApi.ShareWorldObject`) and shares the unit's state under the car's session id (`MpApi.Share`,
  `MpApi.WorldObjectId`).
- **Shared timing, no streaming.** Playback is a start position on the session clock (`MpApi.SessionTime`). Each
  machine downloads and plays the video itself and stays in step by playing slightly faster or slower, or seeking
  (`Video.cs`). Only video ids pass between players.
- **Running without RevaMP.** With no RevaMP (`MpApi.Available` false) it works in single player.
- **Game items from the game's own.** `Units.cs` clones the game's radio item into new items with their own ids,
  so the game saves, loads and spawns them, and RevaMP shares them with the car.
- Two things every BepInEx mod for this game meets: per-frame work runs on the mod's own `DontDestroyOnLoad`
  object (the game destroys BepInEx's), and the player's camera is `mainscript.s.player.Cam` (no `Camera.main`).

## Files

| File | What it does |
| --- | --- |
| `Plugin.cs` | Settings, API registration, Harmony patches, the per-frame runner |
| `Units.cs` | The head unit models as game items, and which cars each fits |
| `UnitBehaviour.cs` | On every unit: finds its car and that car's shared state, and shows it |
| `State.cs` | The shared state record and its type (queue, playback position, volume) |
| `Touch.cs`, `Controls.cs`, `Panel.cs` | The touch screen, crosshair clicks, typing a link, the passengers' big picture |
| `Look.cs` | The unit's body colour, screen and text |
| `Video.cs` | Plays the video on this machine and keeps it at the shared position |
| `Ytdlp.cs` | Fetches yt-dlp (and optionally ffmpeg and Deno) and downloads each video to a temporary file that is deleted once nothing plays it |
| `Spotify.cs` | Turns a Spotify playlist, album or song link into YouTube Music searches (nothing plays from Spotify) |
| `Nav.cs`, `MapView.cs` | Navigation: providers for things on the road ahead, and the map |
| `Stations.cs` | Names for the game's radio stations |
| `Glue.cs` | A unit glued anywhere: only the screen shows |
| `Probe.cs` | Checks for `tools/headunit.py`, called through RevaMP's debug bridge |

## Settings

`BepInEx/config/tldrevamp.headunit.cfg`, section `[Video]`: `Volume` (this machine only), `Resolution` (above 360
needs ffmpeg), `ConvertToVP8` (for Steam's own Proton, which can't decode H.264; turns itself on when needed).

Videos are streamed through a temporary file in `BepInEx/cache/HeadUnit`, because the game's video player can't
open YouTube's address under Proton. That file is not a library: it is deleted as soon as no unit nearby plays it or
has it next, and the folder is emptied at game start and when the game closes. What you play is your choice and your
responsibility: follow YouTube's terms of service and the rights of whoever made the video.

When a player first adds a link (+ ADD), the unit offers to download yt-dlp and Deno from their official GitHub
releases, and ffmpeg (an LGPL build) for anything above 360p, each checked against its release's checksums. Nothing
is downloaded until the player accepts on the screen.

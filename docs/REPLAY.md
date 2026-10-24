# Replays

Record gameplay and watch it again with the filming camera (issue #36, "sort of like Replay Mod on Minecraft").

## Using it

| | |
| --- | --- |
| **Ctrl+F9** | start or stop recording (`Keys.ReplayRecord` in `tldrevamp.core.cfg`, or Settings → Revamp → Filmmaking → Record a replay) |
| **Ctrl+F10** | play the latest recording, or leave the one playing (`Keys.ReplayPlay`, or Settings → Revamp → Filmmaking → Watch the latest replay) |
| Space | play / pause (play at the end starts over) |
| ← → | jump back / forward 5 s |
| ↑ ↓ | speed: 0.1×, 0.25×, 0.5×, 1×, 2×, 4× |
| 1 2 3 4 | free camera, orbit around the watched player, through the player's eyes, follow the player |
| Tab | watch the next player |
| Alt + click | scrub on the timeline (holding Alt frees the cursor) |
| H | hide the timeline for clean footage |
| Page Up / Page Down | the newer / older recording |
| Backspace | leave |

The free and orbit views are the [cinematic camera](CINEMATIC-CAMERA.md): W A S D fly, Q and E change the height,
the mouse looks, Shift hurries and Ctrl creeps. A small "Recording" banner shows while recording; it can be turned
off under Settings → Revamp → Filmmaking.

## What is recorded

Twenty times a second:

- **Every player**: the body and its six animation targets (head, hands, feet, pelvis, the same pose the multiplayer
  body uses), the head's pitch, and what the player sees (the local player's own camera, a remote player's head).
  The outfit is recorded once, as the game's own numbers.
- **Every car, part and item within 250 m of a player** (the game's save registry): where it is, what it is
  attached to or held by, and whether it is shown. A key is written only when something changed, and a part riding
  on a car is kept relative to the car, so a parked car costs one key and a car's parts cost nothing while it drives.
  Objects that go further than 300 m stop being sampled and stay where they were last seen. An object destroyed
  during the recording is hidden from that moment; one that appears close to a player is hidden before it appeared.
- **Moving pieces** of objects within 30 m of a player (steering wheel, pedals, needles, levers, doors): each of the
  object's own renderers relative to the object, with a bounded number of reads per sample.

- **Lights**: which of an object's own lights (headlights, lamps, a torch) are on. The copies carry copies of those
  lights; the game's own lights light nothing (culling mask 0) while the replay plays.

- **Mods' own data** (replay channels, API 4): a mod records small keys about an object (what a screen shows, a
  dial's state) while a recording runs, and gets the latest key at the playhead back with the object's copy during
  playback. The TV Browser example mod records each TV's page this way (a half-size picture every 2 s while the page
  changes). Up to 64 channels, 256 KB a key, 500,000 keys and 96 MB in all; past that keys are dropped and counted
  (status: `modKeys`, `modMB`, `modDropped`). See [MODDING-MULTIPLAYER.md](MODDING-MULTIPLAYER.md#replay-channels).

Not recorded yet: sounds, particles (smoke, dust, sparks), animals and NPCs, damage deformation, fluids, and the time
of day (the sky is the current one).

A recording stops by itself after an hour or at 2,000,000 object keys or 1,000,000 piece keys (status: `approxMB`).

## How playback works

Nothing in the world is moved. Every recorded object is drawn as a copy (its own meshes and materials) at its
recorded pose, and the game's own renderers of those objects are hidden with `Renderer.forceRenderingOff`, a flag
separate from `Renderer.enabled`, so the game's show/hide logic keeps working underneath; the tiny-part culler, which
uses the same flag, pauses until the replay ends. Recorded players are drawn with the game's multiplayer body (its
colliders kept off). The player's own body and the other players' live bodies are hidden while the replay plays.
A car's copy never carries the player sitting in it (the player hangs under the car's transform while seated; the
recorded body draws the player). Through the eyes, the watched player's body is left out, and so is any small piece
sitting on the eyes.

While a replay plays, every input the player has (walking, looking, using, the inventory keys, the car's controls) is
zeroed right after the game's input script runs, and the movement keys go to the camera instead. The player's
character does not move, and nothing is sent over the network: in multiplayer the others see you standing still.

Leaving destroys the copies and bodies, gives every hidden renderer its own flag back, resumes the culler and closes
the camera (if the replay opened it).

The recording just made plays with the looks captured while it was recorded. A recording from a file finds each
object by the game's save id (the same object in the same save) and item index; an object that is not in the world
is drawn with its item's default look; one whose item is unknown is left out (status: `live`, `fromItem`, `notFound`).

## Files

`BepInEx/tldrevamp-replays/replay-<yyyyMMdd-HHmmss>.tldreplay`: gzip of a binary stream (`TLDRPLAY`, format 2; format 1
files, from before mod channels, still play). RevaMP's own part is numbers only: there are no names, account ids, chat,
paths or notes in it. The one text is the mod channel names (`modid/name`), and a mod's keys are whatever that mod
records: with TV Browser, pictures of the pages that were on a TV (and so whatever those pages showed). Objects are save ids and item indexes,
players are numbered (the recording player is flagged), outfits are the game's own numbers, lights are bits, and positions are
relative to an origin rounded to a metre. The reader refuses a file with any count over the limits, a time running
backwards, a non-finite number, a rotation that is not a unit quaternion, a parent that does not exist, more than one
recording player or trailing bytes. Recordings are local; nothing shares them.

## Bridge and tests

```
replay [status] | list | near [m]             read-only (near: the copied pieces by the camera, for checking a view)
replay record | stop
replay play [name [disk]]                     disk: read the file even when it is the recording just made
replay pause | resume | toggle | end | seek <s> | jump <s> | speed <x>
replay camera free|orbit|eyes|follow | subject <n> | ui on|off
```

```
dotnet run --project tools/unit/ReplayCoreTest.csproj   # keys, sampling, the file format and its refusals, the clock
python3 tools/replay.py check                           # on a running test game, in a world
python3 tools/replay.py run                             # launch the test copy, new world, check, quit
```

`tools/replay.py` drives a car for about 20 s while recording, then plays it back from memory and from the file. It
checks that the recorded player is where the real player was at the start, middle and end (1.5 m plus 0.4 s at the
car's speed there), the copies,
the hidden renderers, the paused culler, the blocked controls, the 2× clock, each camera view (with screenshots, and nothing on the
eyes in the eyes view), that
the player did not move, that leaving gives everything back, and that the game logged no exceptions.

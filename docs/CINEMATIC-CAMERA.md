# The cinematic camera

A separate camera for filming, in the mod. It is **local presentation only**: it changes nothing that the game or
the network can see, and it never touches the game's own camera.

## Turning it on

The camera's key is **unbound by default**, so it never steals a key the game or the player uses. Assign
`Keys.CinematicCamera` in `BepInEx/config/tldrevamp.core.cfg` (or Settings → Revamp → Filmmaking) to a key; press
it to open the camera and press it again to close it. The key is ignored while the chat box or the marks panel has
the keyboard, while a menu is open and while the world is still loading.

The camera is also driven from the mod's debug bridge (developer tooling, off in a player's install):

```
cinema                 the camera's status            (a query: any peer may ask)
cinema status          the same                       (a query)
cinema on              open in the default mode       (a mutation)
cinema free            open in the free (fly) mode    (a mutation)
cinema orbit           open in the orbit mode         (a mutation)
cinema off / toggle    close the camera               (a mutation)
cinema recentre        put the orbit target back      (a mutation)
cinema speed <m/s>     the camera's speed             (a mutation)
cinema fov <degrees>   the camera's field of view     (a mutation)
cinema radius <metres> the orbit distance             (a mutation)
cinema shot            the shot list's status         (a mutation)
cinema shot load <text>  load a shot list             (a mutation)
cinema shot write        the canonical form of the take (a mutation)
cinema shot unload       forget the shot list          (a mutation)
cinema screenshot        a screenshot of the camera's view (a mutation)
```

A **query** changes nothing and is safe for any peer. Every other command **changes what this machine shows**, so
it is refused when the host's developer policy does not allow it, and the refusal is counted. The arguments are
only mode names and numbers: the bridge echoes the command it last ran into every anomaly log line, so a free-text
argument would put whatever was typed into the log.

## The controls

| | free (fly) | orbit |
|---|---|---|
| W A S D | fly forward, back and sideways | W and S move the camera nearer or further, A and D turn it |
| Q E | up and down | raise and lower the target |
| the mouse, or the arrow keys | look around | orbit around the target |
| Shift | three times faster | three times faster |
| Ctrl | a quarter speed | a quarter speed |

While the camera is on, **W A S D fly the camera instead of walking your character**. The character must not move:
its position is what the network reports, and the other players would see this one walk away in the middle of a
take. The car's own throttle and brakes are separate keys, so filming from a driven car still works.

## What the camera never does

- **It never touches the game's cameras.** The player's world camera, main camera, distant camera, sky camera and
  screenshot camera are only ever *read* (for the rendering setup) and never moved, reparented, repointed, given a
  different field of view or disabled. The camera renders through its own `Camera` component on its own root, in
  front of the game's.
- **It never moves the player**, nor the player's body rotation or head transform, so the position the network
  reports is unchanged.
- **It never sends anything.** The feature has no protocol, no message and no shared state.
- **It never switches VR on or off.** In VR the game re-parents its camera rig to the headset and rewrites the head
  transform every frame, so the camera refuses to run there with a clear message. The OpenXR bridge owns the XR
  lifecycle.
- **It never leaves the cursor locked.** The camera takes the cursor only while no panel wants it, and gives it back
  only while the cursor is still the one it set: the marks panel, the chat box and the join-loading screen each
  own the cursor while they are open, and the camera leaves their state alone.

The camera puts itself away by itself when you die, when you respawn, when the world is replaced or the main menu
comes up, when a session stops, when the world is still loading, and when you are in VR. Every one of those is
idempotent: a death and then a scene change both fire, and the second changes nothing.

## The bounds

Everything the camera can be set to is clamped, and the clamp is applied to the *result*, so a key-repeat burst or
a long frame cannot throw the camera somewhere absurd.

| | min | max | default |
|---|---|---|---|
| speed | 0.25 m/s | 60 m/s | 8 m/s |
| field of view | 10° | 120° | 60° |
| orbit radius | 1 m | 400 m | 12 m |
| orbit polar angle | −89° | +89° | 20° |
| camera pitch | −89° | +89° | 0° |
| a frame's step | — | 12 m | — |

The polar angle stays strictly inside ±90°, so the view can never flip over the pole.

## Shot lists

A shot list is a text file of camera moves, cut points and playback speeds. It is a **separate sidecar** with its
own version and its own parser: it is not the game's diagnostic repro format, and it never goes near the network.

```
# TLD Revamp cinematic shot list
version 1

at 0 move 120 45 -80 35 -12 60     # x y z yaw pitch fov
at 0 target 120 2 -80              # the orbit target
at 2.5 cut 2.5                     # a hard cut
at 2.5 move 128 4 -74 60 -5 35
at 6 speed 0.5                     # half speed until the next directive
at 10 speed 1
```

The positions are **global** — the world's own coordinates, the ones the map shows — so a take written today still
frames the same place after the world has shifted. The camera's status reports both (`position` and `global`), so a
take can be written from what the tool prints. `docs/examples/cinematic-shot.txt` is a worked example; the unit
test parses it with the production codec and checks that it describes the take it claims to.

Load one with `cinema shot load <directives>` (newlines written as `\n`) or with
`tools/cinematic.py shot <file>`. `cinema shot write` prints the canonical form, so the file on disk and the take
in the game cannot drift apart.

## Screenshots

`cinema screenshot` (or `tools/cinematic.py screenshot`) saves a picture of the camera's view to
`BepInEx/tldrevamp-cinematic/`, and answers with the path. The file's name is made of the camera's own state and
the time — never of anything typed — because a file name ends up in a folder listing. The screenshots are **local
only**: they are never part of a debug report and never shared with the other players (the repo's telemetry
deliberately holds no screenshots; docs/TELEMETRY.md).

## The tests

```
dotnet run --project tools/unit/CinematicCameraTest.csproj      # the rules: bounds, modes, the cursor, the take
dotnet run --project tools/unit/CinematicIsolationTest.csproj    # the static proof that nothing else is touched
python3 -m unittest tools/test_cinematic.py                       # the tool's own command surface
python3 tools/cinematic.py check                                 # the check, with the game running
python3 tools/cinematic.py screenshot                            # a picture of the camera's view
python3 tools/cinematictwomachine.py                             # the two-machine check
```

`tools/cinematic.py check` reads the game's own values and requires the five game cameras and the player to be
*identical* before, during and after the camera was used.
`tools/cinematictwomachine.py` watches the host's own traffic, the position the joiner sees the host at, and the
anomaly counters on both machines while the host films.

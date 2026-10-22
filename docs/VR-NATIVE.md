# Native VR

The game's own VR is flat play with a headset on: the controllers feed the walk, the look and "use", every interaction
ray comes from the head, the left hand does nothing, items can't be picked up with a hand, and the flat-screen camera
effects stay on. `src/TLDRevamp/VR/Native/` makes the hands the way you play, on top of the game's own VR code
(docs/VR-REWRITE.md describes that code and the loader). Nothing here changes the multiplayer protocol.

Settings → Revamp → VR. Every option only acts while the headset is on; flat play is untouched, and the player's saved
settings are never changed.

## The hands — `VrHands`, `VrPointer`, `VrControls`

| control | what it does |
|---|---|
| grip | grab what that hand touches; or pull in what it points at (the game's reach). Let go: drop, or throw with a swing |
| grip, other hand on the held item | the other hand takes it |
| trigger | use what that hand points at (doors, buttons, seats, keys); on the hand holding an item, use the item |
| A / X | attach the held part where the hand points, or detach the part it points at |
| sticks, B, stick clicks | as in the game (walk, turn, recenter, jump, crouch) |

- `VrControlMap` (pure, unit tested in `tools/unit/VrControlsTest.cs`) turns both controllers into the game's input
  fields. The pointing hand is the one that pressed last; a trigger press keeps its meaning (use or item) until it is
  released.
- `VrPointer` swaps the camera transform in `fpscontroller.RayStuff` and `LookPointCalc` for the pointing hand's, so
  every rule the game has about reach, layers and what is usable stays the game's.
- `VrHands` holds the item at the spot the hand grabbed it (`PickedUpForceAdd` target), drops it with the hand's speed,
  and draws the game's laser from the pointing hand.

## Comfort — `VrComfort`

Walking and driving head bob read as off for exactly the frames the game reads them (a prefix and a finalizer around
`fpscontroller.Update`); motion blur and depth of field are off while the headset is on and come back when it stops.

## Height — `VrHeight`

The camera rig is a child of the head-lean transform and the game places it once, at its recenter. The body others see
takes its head from the camera, so a recenter with the head higher than you play, or bending down, sent a head far
below a standing body: a short player. Now:

- the standing height is learned at each recenter (B) and rises on its own when the head stays above it;
- the camera height is the real head relative to that, on top of the body's standing eye height (the game's crouch and
  lean no longer move it);
- the body crouches as far as the head is below the standing height, through the game's own crouch and ceiling check.

Seated in a vehicle the game's placement stays.

## Frame rate — `VrPerf`

- **Show VR on the monitor** (off by default): Unity's copy of the left eye to the window.
- **VR render scale**: `XRSettings.eyeTextureResolutionScale`. 100% is the runtime's recommended size, which already
  includes a stream scale such as WiVRn's.
- **Car mirrors render once in VR** (on): `camscript.OnPreRender` re-renders every visible car mirror once per eye.
  Shared, each mirror renders once per frame from the middle of the head.

- **Draw both eyes at once** (on): single-pass instanced stereo. The game's build supports it for its own shaders, not
  for its cloud shaders: `VrSky` renders the sky once into the six faces of a cube (one face a frame, as an ordinary
  camera with a texture) and the sky camera draws that cube in both eyes. Applies when VR starts.
- **No temporal anti-aliasing in VR** (on): TAA smears with every head movement and costs ~3 ms at headset size.
- Empty post-processing passes: the distant-terrain camera and the hands/UI camera each carry a post-processing layer
  that no volume can reach (their volume layers hold none). Each copies the picture through an empty stack; in the
  headset they are off while that stays true (rechecked every 2 s), back when VR stops.

Measured in the open on a Quest 3 at WiVRn 150% (3096x3244 per eye), full Ultra settings: 33 fps with single-pass
alone, 47 with the empty passes off and the sky capture in the camera loop. Looking at a wall in the starting house:
27 -> 34.5. What is left is the game's own content (Standard materials, terrain, glass with a grab pass each, shadows,
realtime reflection probes) and a ~11 ms GPU / ~12 ms main-thread floor with nothing drawn at all.

## Testing on a headset nobody wears — `tools/vrnative_live.py`

```
tools/vrnative_live.py up        # headset sensor off over ADB, deploy, hidden launch, new world, VR on
tools/vrnative_live.py check     # grab, pull, throw, use, comfort assertions (hands driven by `vrnative sim`)
tools/vrnative_live.py perf --seconds 20 [--set "scale 0.8"]
tools/vrnative_live.py shot out.png       # what the headset shows (ADB)
tools/vrnative_live.py eyes out.png       # both eyes side by side, as the game drew them
tools/vrnative_live.py down      # VR off, quit, sensor back to normal
```

Bridge: `vrnative` (state), `vrnative body` (every height from the feet to the head the network sends), `vrnative
items [r]`, `vrnative sim ...` (a test places a hand and presses its buttons), and the switches `comfort|hands|height|
mirror|sharedmirrors on|off`, `scale <0.5..1.5>`. Frame-cost measurements: `cost` (this code's main-thread time),
`post list|layer|fx`, `idlepost`, `sky on|off|faces n`, `cull list|cull <camera|all> <layer> on|off|cull restore`,
`visible [n]` (shaders drawn now), `hide <shader part>|show`. VR runs alone on the machine.

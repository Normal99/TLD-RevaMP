# Trailer Hitches

Created by Normal99. Version 0.2.0.

Adds a visible receiver hitch to driveable cars whose existing slots cannot accept the stock trailer. Eligibility
requires a root `carscript` and save record, a driver seat and at least four distinct road wheels, and excludes
trailers (`attachgripscript`). It does not depend on prefab names. Bicycles and motorcycles are excluded. Cars with
a compatible stock receiver are left unchanged. Unknown models without a measured position are logged and skipped;
new game models need a reviewed table entry rather than a bounds-based guess.

The receiver is the game's stock assembly, discovered in ascending item id order by `partslotscript.CanCraft` against
the stock trailer's drawbar grip. In the current beta the donor is item 1, `Car01/VonoHorog`. Its separate
`VonoHorogCollider` slot, `VonoHorogTarget`, mounting bar, cylinder neck and ball are cloned together. Unity remaps
the internal references and copies every serialized slot field, layer, collider and trigger. Only the owner,
pickupable owner, appended index and assembly position change. The slot has empty `maintipus`, `tipus=[utanfuto]`,
empty parts/usables/random lists, no detach target, spawn part or installed-object toggle, and no receiver joint.
Snapping uses its enabled 0.1 m sphere trigger; the small bar, neck and ball retain their native solid colliders.
Joint/attach/break settings stay on the unchanged stock drawbar. An additional open receiver tube is visual only.
All receiver renderers share the stock neck's `fem3` metal material; the mod creates no materials.

## Cars and placement

Positions are the slot's ball target, in metres relative to the car root: X right, Y up, Z forward. Each body and full
variant uses the same position. Runtime placement uses only this table (or the config), never renderer bounds.

| Prefabs | Item IDs | X | Y | Z | Fixed body/frame rear Z |
| --- | --- | ---: | ---: | ---: | ---: |
| `Car06P`, `Car06PFull` | 243, 244 | 0 | -0.0945 | -2.26 | -2.0597 |
| `Car09`, `Car09full`, `Car09full_slide` | 371, 372, 388 | 0 | -0.2891 | -2.34 | -2.1379 |
| `Car09P`, `Car09Pfull` | 390, 391 | 0 | -0.2891 | -2.45 | -2.2496 (fixed bed) |
| `Car09T`, `Car09Tfull` | 396, 397 | 0 | -0.2891 | -4.01 | -3.8092 |

These are **initial static-geometry measurements**, read offline from the beta's prefab data, not claimed playtest
results. The rear comes from solid centre-line body/frame colliders owned by the main body, excluding detachable
parts. The pickup's fixed bed overhang is included so the drawbar clears it. About 0.20 m separates that rear from
the target. Rear wheel centres are Y=-0.0222 for Car06P and -0.2168 for Car09 variants. The stock trailer's level
target-to-wheel-centre difference is 0.4362 - 0.5085 = -0.0723 m, which sets the table's Y. As a stock comparison,
Car01's target is (0,-0.2331,-2.2644), with rear wheel centres at -0.13. Wheel radii, tyre condition, load and settled
suspension affect actual ground height; the table still needs the bridge measurements below before release.

## Config

After the first start, edit `BepInEx/config/tldrevamp.trailerhitches.cfg` and restart:

```ini
[Positions]
car06p = 0,-0.0945,-2.26
car09 = 0,-0.2891,-2.34
car09p = 0,-0.2891,-2.45
car09t = 0,-0.2891,-4.01
```

Values are absolute car-local X,Y,Z targets, with decimal points and commas as separators, shared by the listed
variants. Non-finite values, malformed triples and coordinates outside +/-20 m fall back to that model's built-in
position with a warning. There is no live config reload, so changing a file cannot move an already attached trailer.

## Saves and multiplayer

The nine database prefabs are replaced with whole-car templates under an inactive `DontDestroyOnLoad` holder.
The receiver is a normal `partslotscript` appended once to each template's existing save slot list after database setup.
Parenting a runtime receiver directly under a stock prefab asset left it in the setup scene; scene unloading destroyed
it and left a null slot. The persistent template keeps the receiver alive and lets Unity remap the receiver, target,
owner and native slots together when spawning. Database aliases and serialized item ids are preserved.
Existing entries are never sorted or renumbered; repeated setup does not append duplicates. The game assigns the
new slot its saved index during normal item initialization. This preserves the single appended slot used by 0.1.0
on these nine variants. Changing a position moves the attachment target, not its saved index. Other mods that alter
the same slot lists must have the same load order on all machines and across reloads.

**Removing TrailerHitches requires the updated RevaMP core for automatic detachment.** The game's
`save_attachable.Load` checks the saved slot index. An out-of-range item slot falls back to a generic attachment to
the car at the saved local position; stock does not detach it. RevaMP's narrowly scoped `MissingTrailerSlot` hook
leaves a trailer drawbar loose when its loaded car has no such appended slot. The loaded car and trailer records,
positions and contents are retained. Valid stock slots, non-trailers, collider attachments and POI records use the
normal load path. This core hook is necessary because code in an uninstalled mod cannot handle the next load.
Without RevaMP (or with older RevaMP), detach before uninstalling; automatic loose-trailer recovery cannot be
promised if both mods are removed. A different mod reusing the missing index also prevents out-of-range detection.

The mod registers as `trailerhitches` with TLD RevaMP's multiplayer API when available. RevaMP synchronizes the
result through the game's ordinary attachment records; everyone in a multiplayer session must use the same mod
version and DLL. The registered version also includes a SHA-256 fingerprint of the effective placement config;
different overrides fail the existing join check. Every machine uses the same constants and stock clone, and
hitching uses the stock drawbar/slot path, so RevaMP's existing trailer handling applies unchanged. Without RevaMP,
it works in single player subject to the game's existing drawbar initialization bug (RevaMP fixes that bug).

## Verification on a disposable test copy

No game or TLD Copies was run for this change. Offline tests cover the table, config parsing, multiplayer identity,
template lifetime/clone ownership and missing-slot hook. The probe also reports the spawned receiver component,
slot-list entry, owner and stock drawbar `CanCraft` result before hitching. The following game checks remain required.
Start an isolated test copy manually with this
mod and the updated RevaMP core, enable its loopback debug bridge, and use a clear, level road in a disposable world:

```sh
python tools/trailerhitches.py --port 27050 --out bench/hitches.json
```

The script connects to that copy; it never builds, deploys or launches anything. It requires
nearby generated ground. After staged initialization, cars use the same transform and velocity reset as the
core `spawncar` command and settle freely before hitching and measurement.
A separate car pitch/roll check prevents a tilted car from being treated as a hitch-placement failure.
Disposable spawns are destroyed during cleanup unless retained as a save pair.
It enumerates driveable prefabs using the mod's eligibility rule and prints the computed car-local target,
centre-line body rear, hitch height above
ground, hitched drawbar height, drawbar-to-target gap, and trailer pitch/roll relative to the ground normal. It tests
stock and added receivers, checks the cloned slot fields/collider hierarchy/shared material, round-trips the real
attachment record, and temporarily removes the appended slot to exercise the game's unknown-index branch. With
the recovery hook temporarily disabled it expects a generic attachment; enabled, it expects a loose trailer.
The list is restored and the trailer rehitched after each probe. Added hitches must settle within 3 degrees of level
and within 5 cm of the target. Use `--photos` for added models only and inspect bumper/drawbar clearance visually.

For a real process-restart and disk-save test:

1. Run `python tools/trailerhitches.py --port 27050 --keep-save-pair bench/hitch-save.json`. It retains one hitched
   pair and records their save IDs and slot index. Save manually to a disposable slot, exit, restart and load it.
2. Run `python tools/trailerhitches.py --port 27050 --verify-save bench/hitch-save.json` to check survival, hitching
   and index stability. Keep the original save for the next check.
3. Exit, remove only TrailerHitches from that copy, restart and load the same original save. Run the previous command
   with `--removed` to check that both saved objects survive and the trailer is loose. Check its cargo too.

These tests still need a game run, as do player-driven snapping/unhitching, break forces, moving-road clearance,
full cargo persistence and a two-machine test (same config accepted, different config refused, peer hitch/unhitch,
ownership transfer, streaming and reconnect). An in-memory attachment round trip is not a disk-save/restart test.

### Full variant investigation

The saved hitch sweep contains only post-attachment poses. It cannot establish whether the car tilted before
attachment, during the impulse, or stayed tilted afterwards. The separate upright-car sweep used condition 100
and a higher drop; this probe used condition 0 (new) and a 0.6 m drop. Those fixtures are not equivalent.

Offline inspection of the matching beta prefabs found the following geometry (metres). The rear includes
centre-line slot hitboxes and installed parts, so it is conservative. Trailer body clearance assumes aligned,
level, closed prefab poses: trailer tongue front Z=1.60138 minus ball Z=1.64830. It excludes the separate grip.

| Variant | Rear Z | Ball-to-rear clearance | Trailer body clearance | Lowest solid Y | Initial ground clearance, old → new drop |
| --- | ---: | ---: | ---: | ---: | ---: |
| 244 Car06PFull | -2.13284 | 0.12716 | 0.17408 | -0.47770 | 0.12230 → 1.02230 |
| 372 Car09full | -2.19589 | 0.14411 | 0.19103 | -0.61680 | -0.01680 → 0.88320 |
| 388 Car09full_slide | -2.19589 | 0.14411 | 0.19103 | -0.61680 | -0.01680 → 0.88320 |
| 391 Car09Pfull | -2.26100 | 0.18900 | 0.23592 | -0.61680 | -0.01680 → 0.88320 |
| 397 Car09Tfull | -3.80917 | 0.20083 | 0.24775 | -0.61680 | -0.01680 → 0.88320 |

The Full variants add assembled parts, including wheels and rear doors/tailgates; no serialized rear cargo
overhang explains these failures. Four variants' tyres initially penetrate level ground by 16.8 mm at the old
drop height. The diagnostic drop is now 1.5 m. This fixes an initial-pose defect, not a proven cause of the
post-hitch tilt. Receiver coordinates remain unchanged pending equivalent before/after measurements.

The sweep now settles both spawns for 10 seconds by default, records `beforeHitch`, three `afterHitch` samples
(immediate, 1 second, and the settle deadline), then `measurement` one second later. It checks pre-hitch tilt
and final-second tilt stability separately. Every sample includes car pitch/roll against ground independently
of attachment, linear/angular speed, wheel poses/radii, solid bounds including installed parts/cargo, and
car/trailer geometric penetration pairs with paths/depths. Penetration queries exclude triggers, wheels,
shared rigidbodies and explicit collision ignores; they do not establish actual contact impulses or account
for every joint's collision suppression. Static clearance does not prove dynamic clearance. No new live
before/after tilt results are available; the receiver placement fix is still unresolved.

## Build

From the TLD RevaMP repository root:

```sh
dotnet build src/Mods/TrailerHitches/TrailerHitches.csproj -c Release --disable-build-servers
```

Install `src/Mods/TrailerHitches/bin/Release/netstandard2.1/TrailerHitches.dll` under
`BepInEx/plugins/TrailerHitches/`. Source licensing follows the repository's GPL-3.0 license in
[COPYING](../../../COPYING).

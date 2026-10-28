# Repro recordings (format 1)

Recordings come from three places: a **bug report** (Ctrl+Insert or `/bug note`; docs/BUG-REPORTS.md) puts the automatic
recording of this session into the bundle as `repro.json`; Settings → Revamp → Debugging → *Repro recorder (save to a file)*
or bridge `repro start` / `repro stop` record by hand. `Keys.ReproRecorder` in `tldrevamp.core.cfg` is the bug report key.
It requires a multiplayer world. Leaving that world stops it. A hand recording's Stop writes UTF-8 JSON on a worker to
`BepInEx/tldrevamp-feedback/marks/<session-folder>/repro-<random>.json`. `repro status` reports `recording`, `saving`,
`failed`, `rows`, `skipped`, `dropped`, capacity and timings. Wait for `saving:false`; check `failed:false` before using the file.
Recordings are local only; mark/report sharing does not send them. The automatic recording starts with the session, and the
bundle's `world/start/` holds the save it started from, so `repro.json` replays from that state.

There are no names, account/player identifiers, chat text, arbitrary strings, notes or paths in the file. Object
net ids, prefab ids and canonical item/slot indexes are necessary action addresses. Unknown chat commands become
code 0; their spelling and arguments are discarded. `/tpa` targets are replaced with the test's sole peer during
replay. `/mark` notes are discarded. The JSON is constructed from typed numbers and compiled field names.

Replayed marks use the current mark-time focus. In protocol 47, message 92 carries up to 16 marked net ids to
every observer, saved as `head.markedNets` in the separate black box. State capture prioritizes seated cars,
then marked and local focus nets (regardless of distance), then other entities within 300 m nearest first.
Motion traces have 32 focus slots and a 65,536-row ring (35 seconds for 16 busy entities), including remote held items and four
recent releases; received marker nets stay in focus through the five-second aftermath. Repro format 1 is unchanged.

The envelope is `{format:1, protocol:<recording build>, sampleHz:10, complete:true, dropped:0, skipped:0, cols:[...], rows:[...]}` (JSON keys
are quoted in actual files). `cols`, in order:

```
t, st, kind, net, other, item, slot, x, y, z, a, b, c, d, e, f, g, h
```

Every row has exactly 18 finite numbers. `t` is monotonic seconds since recording started; `st` is session time.
Rows with equal `t` keep their recorded order. `net` / `other` are object net ids; 0 means no object reference.
`item` and `slot` are canonical indexes, with -1 meaning absent. Unused numbers are zero. Global coordinates are
metres, angles degrees. A `bind` precedes the first use of each nonzero object reference. Unresolvable action
addresses increment `skipped`; the remaining recording stays complete and the runner warns.
Malformed rows, backwards time and buffer exhaustion increment `dropped` and make `complete:false`; replay refuses
these files. Stop shows both counts. Older format-1 files without `skipped` remain supported. The recorder retains the
beginning, never a ring-buffer tail with missing actions. It stops at 60,000 rows.

| kind | Action | Values beyond the common addresses |
|---|---|---|
| 0 | bind | `net`: recorded address; `a`: prefab id; `x/y/z`: global root position when first referenced. |
| 1 | pose | `x/y/z`: player global position; `a`: body yaw; `b/c`: local look pitch/yaw. Sampled at 10 Hz. |
| 2, 3 | seat enter, leave | `net`: vehicle; `slot`: seat index. |
| 4 | pickup | `net/item`: picked item. |
| 5, 6 | drop, throw | `net/item`: held item; `a`: throw charge; `x/y/z`: throw direction. |
| 7–10 | inventory switch, swap, store, unstore | `slot`: inventory slot; `net/item`: its content, or 0/-1 for empty. Nested calls are collapsed. |
| 11, 12 | right-hand grip, release | `net/item`: item in the hand. |
| 13 | usable | `net/item/slot`: item and usable index; `x/y/z`: slide/x rotation/y rotation; `a/b`: turn state/tuning. |
| 14 | ignition hold | `net`: car; `a`: 1 pressed, 0 released. Stop closes an unfinished hold. |
| 15 | attach | `net`: part; `other`: parent group; `item`: 0 for parent root, otherwise parent member index + 1; `slot`: collider/part slot index. `a/b`: game index/attach types; `x/y/z`: global attach point; `c/d/e`: point in parent coordinates; `f`: POI generator; `g/h`: POI position x/z. Preceded by an entity pose only for a locally owned part. |
| 16 | detach | `net/item`: attached item, via the game's unmount path. |
| 17, 18 | physlock, unlock | `net/item`: item; `other`: mount group; `a`: mount's item index; `slot`: mount index. |
| 19, 20 | hitch, unhitch | `net`: trailer; `other`: car; `slot`: recorded hitch slot. Uses the existing owner-routed hitch request. |
| 21 | command | `a`: 0 unknown/manual, 1 `/tpa`, 2 `/tpaccept` or `/y`, 3 `/tpdeny` or `/n`, 4 `/mark`. No arguments. |
| 22 | mark | Local mark accepted/received; no note. Replays an empty mark through the normal mark route; rate-limited marks are non-fatal, counted skips. |
| 23 | entity pose | `net`: locally owned item; `x/y/z`: global position; `a/b/c`: Euler rotation, before attaching. |
| 24 | POI usable | `item/slot`: generator/usable; `e/f/g`: POI position id; usable values as kind 13. |
| 25 | network conditions | `x/y`: added latency/jitter ms; `a/b/c`: loss/duplication/reorder %. Initial conditions and changes; `d`: 1 for legacy send-only conditions, otherwise 0. |
| 26 | driving input | `net`: driven car; `x/y/z`: throttle/brake/steer; `a/b`: clutch/handbrake; `c`: button-edge mask; `d/e`: ignition held/horn. Axes at 10 Hz, button edges immediately. `f/g/h` (from build 0.69.1-dev after 2026-10-28; replay ignores them): what the car did with it. `g`: average engine rpm; `h`: the car's speed (game unit); `f`: flags, low to high bit: 0 engine running, 1 body kinematic, 2 body asleep, 3 car.driving, 4 handbrake latched, 5-8 driven wheels on the ground, 9-12 driven wheels, 13-16 gear + 1 (0 = reverse, 1 = neutral), 17-23 clutch % the car applies, 24-30 handbrake % applied, 31 motor torque on a driven wheel, 32 brake torque on a driven wheel. Older recordings have 0 in all three. |

Driving button bits, low to high: handbrake toggle, reverse, neutral, gears 1–6, upshift, downshift, ignition down,
lights down. A replay edge is consumed once; axes expire after 350 ms without a sample. Only the current driver and
owner can apply these inputs. While armed, rows waiting for driver/ownership readiness are skipped and counted
in the runner result; physlock, unlock and entity pose use the same policy. Missing targets still fail. Neither
recording nor replay changes ownership rules or network budgets.
Usable changes coalesce at 10 Hz, with a bounded 32-entry pending table and a final flush on stop. The game still
executes all interactions normally; this recorder observes them.

## Running a check

Use two **isolated test copies** running normally, windowed (including on a hidden desktop), connected in the same
multiplayer world on different loopback bridge ports. **Do not use `-batchmode`**: RevaMP turns it into a dedicated
server with a phantom host player. A typical pair is a host copy on bridge 27060 and a joiner copy on bridge 27061,
hosting on MP port 27180. The existing `smoke.py` / `telcheck.py`
setup uses `bg on`, starts the host world, `mp host 27180`, then `mp join 127.0.0.1:27180` on the joiner.
Use a disposable copy of the repro's starting world and the same initial inventories.
The script does not create, launch, stop or reload games. `CopyIsolation` must be installed on both copies
(TLD Copies installs it as `tldrevamp.copyisolation`; any plugin id ending in `.copyisolation` is accepted). Arming requires isolation, the bridge enabled,
a local player, a multiplayer world, recording off and dedicated-server mode off. Arming temporarily blocks
autosave and permits background updates. `repro end` restores those flags and releases forced held items,
driving input and ignition holds. Both live copies must use the same wire protocol; the recording's protocol is
informational and need not match the live builds. Format 1 does not depend on the wire protocol.

```
python tools/replay_repro.py repro.json --validate-only
python tools/replay_repro.py repro.json --actor-port 27060 --observer-port 27061
python tools/replay_repro.py actor.json --peer-recording peer.json --peer-offset 0.5
```

Use a second recording when the other participant must act (for example accepting a TPA request). Relative start
times align at zero unless `--peer-offset` supplies the other recording's start offset. Network presets are local
to the recorded participant. `--speed` scales the action timeline, not game physics; use its default 1 for repros.
The script restores each copy's original network conditions and calls `repro end` in `finally`, including failures.

`repro resolve <prefab> <x> <y> <z>` matches exactly one loaded shared root within 3 m. Ambiguous or missing
objects fail; there is no nearest-object guess. `--net-map map.json` overrides actor bindings with a JSON object
of old-to-current numeric net ids. It does not change saved recordings. Repeated runs need the starting world
restored independently. Newly detached parts resolve when their binding is reached.

`repro act <kind> <net> <other> <item> <slot> <x> <y> <z> <a> ... <h>` executes one validated numeric row through
normal game/owner paths. `repro check <net>` returns numeric root, attachment, lock, ignition, engine, radio and usable
state; `repro poicheck <gen> <x> <y> <z> <usable>` checks building interactions; `repro players`
returns anonymous local/sole-peer positions and seats. The driver checks both copies converge after the timeline
and that no new errors/exceptions occurred. Default settle timeout is 8 s and root-position tolerance 0.5 m.
Held/stored root positions are excluded from the position comparison because render clocks and inventory frames
differ; their flags and member state are still compared. This is a synchronization check, not a deterministic
physics replay or a proof that the recorded bug has been fixed. Private/unknown commands fail validation, so they
must be reproduced manually. Missing POIs, non-shared action targets and ambiguous world-attach colliders also
fail explicitly. Visual appearance and real Steam impairment behavior still need an in-game check.

## Cost

Off: added hooks return before scans, sampling, clocks, formatting or disk work; no per-frame allocations. On:
60,000 × 128-byte rows = **7,680,000 bytes** plus bounded pending state and first-reference binding bookkeeping.
The net9 Release logic benchmark on the development machine measured **0.25 µs/append, zero allocated bytes**;
this excludes Unity hooks and target resolution. `repro status` exposes `selfMs/maxMs` for frame sampling and
`appendMs/appendMaxMs` for appends including first-reference binding work (sampling includes its own appends,
so these timings are not additive). JSON formatting and disk I/O run on a worker. Banners use cached text and
styles and draw only during Repaint. No game was launched for these measurements.

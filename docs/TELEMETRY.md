# Telemetry

What TLD RevaMP records about how the game runs, so problems can be found and compared between players. Everything
stays on the player's own PC, in `BepInEx/tldrevamp-feedback/`, unless they opt into session sharing or send files
themselves. *Settings → Revamp → Debugging → Share my marks and reports with the players in this session* is **off
by default**, including on hosts. It retains the `shareReports` settings key and its saved value. Sharing sends
new `/mark` black boxes and Page Down report zips through the host to the other sharing players currently in the
session. It does not upload to a service or replay old captures when someone joins/enables sharing.

**Reciprocity:** only someone sharing their own files receives others' files. A non-sharing host still validates
and relays between sharing players, using temporary memory, but saves no received copy and sends none of its own.
The host necessarily sees bytes it relays; this is not end-to-end encryption. Turning sharing off cancels queued
transfers and incomplete incoming files; an already accepted disk write can finish. Existing saved files remain.

New captures contain game state, positions, performance/network measurements, numeric settings and hardware model
names. Player identity, chat, paths, screenshots and raw logs/configuration files are excluded. The author's mark
or report note is the only retained user text, after privacy cleaning. Session sharing exposes these captures to
other opted-in players, including strangers in a public session.

## What telemetry contains

`TelemetryPrivacy` is the export gate. Fixed schema/compiled diagnostic labels and typed numeric game observations
are constructed by the recorder. Object labels must match the content vocabulary below; unknown labels, warning
messages and stack text become `[removed]` (empty values stay empty). Notes and CPU/GPU descriptions are cleaned before
JSON escaping. Cleaning registers the local user/machine, session nicknames and observed Steam personas/account
identifiers (including asynchronous persona replies and renames); it removes identifiers, IPv4/IPv6 endpoints, absolute paths (drive/UNC/Unix/home variables), email,
URLs and wall-clock times. Oversize text or cleaner failure yields `[removed]`. No cleaning runs in frame/event/
motion append hooks; it runs at capture/record boundaries or on workers.

Object names use an exact, case-sensitive allowlist seeded on the main thread from the game's
`itemdatabase.items` and `itemdatabase.pois` prefab tables (the same tables used for prefab ids and building
spawns). It contains names in those prefab Transform hierarchies, including inactive children, and the prefabs'
`partslotscript.tipus` values. RevaMP's fixed `bus door` label is also allowed. Live scene instances, saved item
records, player text and custom music never seed the list. Trailing `(Clone)`, `(n)` (decimal copy number, with
optional preceding whitespace), and `Full` suffixes are stripped repeatedly from both reference and observed
names; the canonical result is exported on both machines. A nonempty result must be at most 64 characters,
match the allowlist and pass the privacy cleaner unchanged. Thus `Car06Full(Clone)` and `Car06` both export
`Car06`, while a renamed item's unknown/private name exports `[removed]`, even if its prefab id is valid.
The sharing validator uses the same vocabulary for names, slot types and content labels. Sign contents and
custom music labels/paths are excluded. Notes are cleaned before the 500-character limit and retained in mark
play-log lines, black-box heads, report metadata and shared file metadata; only cleaned, path-budgeted ASCII note slugs enter mark filenames.

The inventory below applies to newly generated files and every shared copy. `st` and `t` are monotonic/session
seconds. `start` and report `time` are UTC **dates only**, `yyyy-MM-dd`. Filename timestamps are UTC, without a
zone. ZIP entries use the fixed DOS epoch (1980-01-01); GZip headers carry no creation time. `recording` is a fresh
random identifier for each local play log, so machines with identical hardware and UTC dates remain distinct.
`head.privacy:1` identifies the new policy; black-box `format` remains 3. Protocol.Version is **41** (pickup Claim now carries epoch and an owner pose-clock fence; relayed renames carry a host-authenticated renamer index); protocol 40: message 92 now carries the originating session player index; reliable message 104
renames notes by `sid`, `momentSt` and origin. The legacy transfer sender-name slot still carries `player-<index>`.

| Output / section | Fields or files | Decision |
|---|---|---|
| Every play line | `k`, `st`, `t` | Keep: literal kind, session/file-relative seconds. |
| Play / black-box head | `start`, `recording`, `mod`, `build`, `commit`, `game`, `role`, `seed`, `sid`, `format`, `privacy`, `stSource` | Keep: UTC date, random recording/session ids, build information, role/seed and format/clock metadata. No local time or zone. |
| Head `settings` | `width`, `height`, `fullscreen`, `quality`, `vsync`, `targetFps`, optional `viewDistance`, `revamp` | Keep: quality index and numeric live RevaMP `key:value` entries. Remove runtime quality-name text. |
| Head `hw` | `cpu`, `cores`, `ramMB`, `gpu`, `vramMB`, `os` | Keep: cleaned CPU/GPU models, capacity/counts, OS family/major (`Windows 10`, `Linux`, `macOS`, or unknown). Remove full OS/build/kernel description. |
| Head `telemetry` | `scriptTimers`, `timerAlloc`, `timerColumns`, `coarseTimers`, `coarseCallLimit`, `spawnMethods`, `spawnSkipped`, `physicsAllOwned`, `allocCounter` | Keep: numeric/boolean recorder configuration. |
| Play `tick` | `p50ms`, `p95ms`, `p99ms`, `maxms`, `frames`, `hitches50`, `hitchesDropped`, `longHitchesDropped`, `gc0/1/2`, `allocKBps`, `managedMB`, `unityAllocMB`, `unityReservedMB`, `pos`, `speedKmh`, `driving`, `physicsAllOwned`, `physicsSkipped`, `selfMs`, `selfAllocBytesPerFrame`, `handoffs` | Keep: measurements, global game position and scope/counter information. |
| Tick/hitch `gen`, `genDone`, `counts` | `terrain`, `roads`, `buildings`, `buildingsDeferred`, `scenery`, `items`, `sampleAgeS`; completed terrain/roads/scenery/buildings; `items`, `carsNearby`, `awakeRigidbodies` | Keep: numeric backlog/completion/object counts. |
| Tick/hitch performance | `bound.cpu/gpu`; `frame.busyMs/mainThreadMs/renderThreadMs/gpuMs`; `render.drawCalls/batches/setPass/trianglesK/shadowCasters`; `scripts.frames/totalMsPerFrame/top` with `m`, `msPerFrame`, `callsPerFrame`, `maxMs`, optional `kbPerS`; `loop.frames/top` with `p`, `msPerFrame`; hitch method/phase-to-ms dictionaries | Keep: numeric measurements and compiled method/phase labels. |
| Tick multiplayer | `network`, `net`, `mp`, `ai`, `combat`, `fixes` | Keep: traffic described below, filtered `net stats`, `mp.remotePlayers/statesIn/statesSent/stale/spawned/ownerChanges`, filtered AI/combat counters (bridge table below), numeric `FixType.Field` deltas. Remove transport summary/rejection/event strings. |
| Play `hitch` | `ms`, `phases`, `gc`, `rebase`, `stream`, `saving`, `loading`, `gen`, `genDone`, `speedKmh`, `gpuBound`, `frame`, `render`, optional `scripts`, `loop`, `long`, `longFailed` | Keep: frame measurements and flags; `phases` uses compiled marker labels. |
| Hitch `scene` | `rigidbodies`, `rigidbodiesDynamic`, `rigidbodiesAwake`, `renderers`, `renderersVisible`, `lights`, `shadowLights`, `particleSystems`, `particleSystemsPlaying`, `cars`, `carCopies`, `scanMs`, `sampleSt`, `ageS` | Keep: numeric scene counts and sample cost/age. |
| Play `load`, `save` | Load `kind`, `stage`, `firstUsableS`, optional `connectS`, `worldDataS`, `firstStateS`; save `ms`, `auto`, optional `bytes` | Keep: fixed milestone labels, durations and sizes. Never the save path/name. |
| Play `mark` | `note`, `by`, `origin`, `marksFolder` | Keep: full cleaned note (500 characters), anonymous origin index, `me`/`host`/`player`, relative session folder. |
| Play `warn`; report `errors.json` | `warnings`, `errors`, `exceptions`, `droppedKinds`, `kinds`; warning rows `kind`, `msg`, `at`, `n` | Keep counts/severity/grouping. Remove arbitrary `msg` and `at` text, replacing nonempty values with `[removed]`. Warn lines retain the top-ten array; reports retain all groups/counts. |
| Black-box envelope/frame data | `head` (also `momentSt`, `date`, `note`, `origin`, `focus`, `complete`), `overwritten`, `cols`, `rows` | Keep: cleaned note (max 500 characters), anonymous `origin`, and `focus`, numeric frame rows and fixed/compiled column labels (full list below), captured clock and completeness. |
| Black-box head `focus` | `crosshair`, `car`, `seat`, `held`, `dragging`, `closestPlayer`; targets `net`, `name`, `prefab`, `distance`; player `id`, `name`, `distance` | Keep numeric addresses/observations; object names pass the content allowlist, player names use aliases. |
| Marks indexes / comparison | UTC date, session time/id, anonymous origin/source, role, full cleaned note, relative file, received flag; privacy-safe focused state | Keep. No absolute paths or identity-derived text. |
| Black-box `state` coverage/world | `st`, `localPlayer`, `captureMs`, `radiusM`, `maxBytes`, `budgetBytes`, `dropped`, `outside`, `failed`, `hasCenter`, `centerCm`, `time`, `day`, `cloud`, `sand`, `physicsAllOwned`, `physicsSkipped` | Keep: indices, global game positions, world time/weather and scope. `time` here is game time of day. |
| State `players` | `id`, `name`, `hasPos`, `posCm`, `st`, `poseSt`, `net`, `seat`, `inventoryComplete`, `inventoryCount`, `held`, `inventory` | Keep indices, poses, seat and carried state. Replace personal `name` with the constructed `player-<id>` alias. |
| State `held` / `inventory` entries | `net`, `item`, `prefab`, `name`, `kind`, `slot` | Keep canonical addresses/prefab/kind/slot and content-allowlisted `name`. |
| State `entities` | `net`, `epoch`, `owner`, `proxy`, `prefab`, `name`, `posCm`, `poseSt`, `vel`, `yaw`, `speed`, `stored`, `dormant`, `parent`, `part`, `car`; live car `fuel`, `engine`, `ignition`, `key`, `radio`, `radioOn`, `radioAm` | Keep numeric/boolean game observations, session player indices and content-allowlisted `name`. |
| State `slots` | `net`, `item`, `index`, `name`, `type`, `contentNet`, `contentItem`, `localItem`, `prefab`, `content`, `attached`, `condition`, `condition2`, `condition3` | Keep numeric addresses/conditions/flags and content-allowlisted `name`, `type` and `content` labels. |
| State `seats` | `net`, `index`, `name`, `inUse`, `inUseMulti`, `players` | Keep seat indices/flags, claimant player indices and content-allowlisted seat `name`. |
| State `usables` | `net`, `item`, `index`, `name`, `busDoor`, `building`, `gen`, `poi`, `x`, `y`, `slide`, `turn`, `tuned`, `locked` | Keep numeric/boolean observations (`locked` unknown/null on this beta) and content-allowlisted `name`. |
| State `storms`, `stations` | Storm `prefab`, `posCm`, `yaw`, `size`, `speed`, `life`; station `am`, `index`, `clip`, `time` | Keep numeric game weather/broadcast state. Remove/exclude custom music stations, filenames and paths. |
| Black-box `events` | `overwritten`, `sampled`, `rows`: `st`, `kind`, `net`, `other`, `epoch`, `item`, `slot`, `from`, `to`, `count`, `value`, `dx/dy/dz`, `result` | Keep numeric observations and approved literal event kinds/results. Unrecognized free-form result/kind becomes `[removed]`; no chat/TPA command arguments. |
| Black-box `trace` | `overwritten`, `focusDropped`, `focus`, `cols`, `rows`; row columns `local`, `st`, `net`, `epoch`, `owner`, `kind`, `clock`, `xCm/yCm/zCm`, `yaw`, `speed`, `interval`, `age`, `mode`, `correction`, `item`, `slot`; `rates` with `net`, `seconds`, `sent`, `received`, `sentHz`, `receivedHz`, `usables` (`item`, `slot`, `sent`, `applied`, `sendHz`, `applyHz`) | Keep: numeric motion/address/rate observations and fixed column labels. |
| Tick / black-box `network` | Envelope `overwritten`, `rows`; interval `st`, `seconds`, `types`, `categories`, `peersDropped`, `peers`; type/category `name`, `sent`, `received`, `outBytes`, `inBytes`, `handleMs`, category `owned`, census and send-shape/rest counters below; peer `player` plus counts/bytes/time | Keep: literal protocol/category labels and session player indices only. No connection handles/account ids/endpoints/payloads. |
| Hitch / black-box `long` | `overwritten`, `rows`; row `st`, `ms`, `instantiated`, `destroyed`, `activated`, `bodiesAdded`, `bodiesDropped`, `rebaseMoved`, `genDone`, `network`, `timers` | Keep: numeric long-frame work and literal message/compiled timer labels. |
| Report ZIP / temporary report folder | `stats.json`, `blackbox.json`, `breakdown.json`, `errors.json`, `fixes.json`, regenerated `tldrevamp-settings.txt`, `sessions/<current-log>.jsonl` | Keep these entries only. Current log only; older on-disk logs are not copied into new reports. |
| Report excluded attachments | `screenshot.png` or any image; `LogOutput.log`, `LogOutput.prev.log`, `Player.log`, `Player-prev.log`, `BepInEx.cfg`; arbitrary other attachments | Removed. Raw logs/configs cannot reliably distinguish arbitrary names/chat/file text; rely on structured `errors.json` and numeric diagnostics. The bridge screenshot/shot commands return a privacy error. |
| Report `stats.json` metadata/system | `note`, `mod`, `build`, `commit`, `game`, `time`, `system` (`os`, `cpu`, `cores`, `ramMB`, `gpu`, `gpuMB`, `screen`, `unity`), `frame`, `scene`, bridge command properties below, `telemetrySnapshotMs` | Keep cleaned note/models, build/version, UTC date, capacities/resolution and numeric data. Remove graphics API description. |
| Stats `frame`, `scene` | `frame`: filtered `stats` below; scene `focused`, `rigidbodies`, `rigidbodiesAwake`, `renderers`, `monobehaviours`, `scanMs`, `sampleSt`, `playerPos` | Keep numeric/boolean/structural values. Remove runtime scene-name text and exception messages. Malformed replies/exceptions become null; unlisted error fields are omitted. |
| `breakdown.json` | `frames`, `slowFrames`, `slowThresholdMs`, `slowWithGc`, `gcFrames`, `avgMsGcFrames`, `avgMsNoGcFrames`, `gpuBoundFrames`, `slowGpuBound`, `countersValid`, `perFrame` (`m`, `avgAll`, `avgSlow`), `markers` (`m`, `valid`, `avgMsAll`, `avgMsSlow`) | Keep numeric aggregates and compiled profiler labels. |
| `fixes.json`; settings attachment | `FixType.Field` public static int/long values; current known RevaMP `key=int` settings entries | Keep: regenerated from typed live values. Saved comments, unknown keys, names and paths are never copied. Settings help text is not an attachment. |
| Local filenames | `sessions/<UTC stamp>-<role>.jsonl`, `marks/<UTC date>-<sid>/mark-<sid>-<momentSt>-<note-slug>.json`, `marks/<UTC stamp>-bridge.json`, `report-<UTC stamp>/`, `TLDRevamp-report-<UTC stamp>.zip`; numeric collision suffixes and temporary `.tmp` files | Keep random session id/relative moment or UTC timestamp. Cleaned note slugs fit the actual folder path; identity-derived text is removed. Temporary files follow the same content policy. |
| Received files/folders | `marks/<UTC date>-<sid>/mark-<sid>-<st>-<note-slug>-from-p<index>-<hash8>.json`; `players/player-<index>/report-from-p<index>-<SHA256>.zip` | Keep authenticated session index and content hash only. Remove player/persona names and sender-provided base filenames. |
| Relay metadata / arrival notice | Source index, constructed `player-<index>` in legacy name slot, canonical basename, cleaned note, relative moment, random sid, content hash | Keep. Incoming sender names are consumed/discarded; toast/chat notice uses the index and cleaned note. No names are stored for attribution. |

`stats.json` bridge replies are projected through the following **closed field lists**, including nested objects/
arrays. Only numbers, booleans, null and containers survive; every string value becomes null and every unlisted
key is discarded. This also excludes numeric Steam/account/lobby ids and listening ports. Bridge command names
and behavior outside report export are unchanged (`mp share` stays).

| Bridge reply | Kept numeric/structural fields (at any recorded level) | Removed |
|---|---|---|

| `stats` | `frames`, `fps`, `p50ms`, `p99ms`, `p999ms`, `maxms`, `hitches50ms`, `gc0`, `managedMB`, `bridgeMsTotal`, `bridgeMaxMs`, `time` | All other keys and all string values. |
| `mp status` | `server`, `client`, `bots`, `remotePlayers`, `id`, `ready`, `serverSeed`, `mySeed`, `statesIn`, `posesIn`, `bytesIn`, `bytesOut`, `ping`, `peers`, `statesRelayed`, `posesRelayed`, `batches`, `batchedMsgs`, `skipped`, `tiers`, `carStatesSent`, `carStatesThinned`, `carTiers`, `rejected`, `msgIn`, `msgOut`, `sendErrors`, `count`, `pingAvg`, `pingMax` | All other keys and all string values. |
| `net stats` | `bytesIn`, `bytesOut`, `msgIn`, `msgOut`, `sendErrors`, `conns`, `ping`, `qualityLocal`, `qualityRemote`, `outBps`, `inBps`, `sendRate`, `pendingUnreliable`, `pendingReliable`, `unackedReliable`, `queueUs` | All other keys and all string values. |
| `mp entities` | `me`, `count`, `pendingShare`, `serverEnts`, `statesSent`, `statesIn`, `stale`, `spawned`, `ownerChanges`, `handoffs`, `claimsSent`, `claimsRefused`, `rebound`, `membersRebound`, `farRecordUpdates`, `farAdds`, `farResyncs`, `farResyncItems`, `farRechunked`, `farAttachedMoved`, `reshared`, `orphansCleared`, `leasesDone`, `leasesFreed`, `leaseRefusedWhy`, `malformed`, `requesterNotReady`, `buildingKey`, `buildingReach`, `buildingType`, `buildingTypeMismatch`, `doneMalformed`, `doneHolder`, `spawnShared`, `contactSent`, `contactRelayed`, `contactApplied`, `partsReported`, `partsApplied`, `partsAppliedStored`, `suppressedDamage`, `suppressedFallOff`, `silentTakeovers`, `entities`, `net`, `car`, `owner`, `parent`, `part`, `attached`, `epoch`, `proxy`, `upy`, `items`, `driven`, `held`, `dormant`, `stored`, `shown`, `pos`, `delayMs`, `jitterMs`, `extrapolating`, `clk`, `rt`, `yaw`, `vel`, `engRunning`, `engRpm`, `fuel`, `frames`, `extrapFrames`, `jumps`, `maxDevCm`, `physical`, `partsColOff`, `byBody`, `seqOut`, `recv`, `why`, `sentRest`, `lastSent`, `lastIn` | All other keys and all string values. |
| `mp aistats` | `breakHitsSent`, `breakHitsRun`, `breakFxSent`, `breakFxApplied`, `statesSent`, `statesApplied`, `soundsSent`, `soundsApplied`, `remoteSeen`, `keptOnRemote`, `remoteAttackFrames`, `damageSent`, `damageTaken` | All other keys and all string values. |
| `mp combat` | `shotsTested`, `shotHits`, `meleeHits`, `damageSent`, `damageTaken`, `damageRefused`, `hitCheck`, `enabled`, `checked`, `allowed`, `refused`, `noHistory`, `objects`, `objChecked`, `objAllowed`, `objRefused` | All other keys and all string values. |
| `mp shotfx` | `sent`, `applied`, `decals`, `holes`, `unplaced`, `noGun`, `displayHoles` | All other keys and all string values. |
| `mp blasts` | `sent`, `applied`, `reqSent`, `reqRun`, `copySkipped` | All other keys and all string values. |
| `mp membergone` | `sent`, `applied`, `rejected`, `playerDeleteSync`, `playerDeletes`, `memberDeletesSent`, `memberDeletesForwarded`, `memberDeletesApplied`, `keepPartsOnRemove`, `partsKept`, `partsOrphaned`, `partsOrphanedNoRecord` | All other keys and all string values. |
| `mp poiusables` | `sent`, `applied`, `onSpawn`, `unplaced`, `known`, `serverKnown`, `removeDist` | All other keys and all string values. |

Sharing uses one scrubber on preparation and receipt: unknown numeric/boolean/null fields and their nested
objects/arrays pass unchanged when unknown keys are ASCII identifiers (`A-Z`, `a-z`, `0-9`, `_`, `.`, `:`, `/`, `-`,
1–64 characters). Unknown strings become `[removed]`; unsafe keys and known identity/port fields are dropped.
Known strings retain their existing privacy rules. `mp sharing` and play ticks expose total and last-file
`fieldsScrubbed`/`keysDropped` counts; the first scrub each session logs only a safe key path. Transfer headers,
hashes, syntax, archive paths and size/depth/entry limits remain mandatory.

Sharing validates the allowed attachment names, decoded JSON keys/string roles, numeric identifier bounds,
settings keys and archive timestamps on preparation and receipt, before saving or relaying. New black boxes must
carry `privacy:1`. Older captures remain readable by the comparer, but cannot be reshared through this gate.
Existing files on disk are not rewritten or deleted by this change.

Offline gate: `tools/unit/TelemetryPrivacyTest.csproj` exercises the real `SessionLog`, `FrameBreakdown`,
`ErrorLog`, black-box/history writers and `FeedbackReporter` with fake Unity/network/bridge observations seeded
with private fixtures. It scans play lines, all black-box sections, ZIP members/metadata and shared filenames.
`ReportSharingTest` checks the real relay/save state machine and index-only attribution; `ReportTransferTest`
checks scrubbing of additive fields and rejection of unsafe attachments and legacy sharing. `ReportCaptureSharingTest` runs the real
SessionLog capture/completion and report zipper through sender preparation, packets, receiver validation
and disk save in both directions. It reproduces 62528d8's additive numeric network schema in a completed
format-3 mark and a report session log, and exercises all approved literal results and seeded private values.

For real captures (Python 3.9+, standard library), run:

```
python tools/privacy_scan.py --capture <feedback-folder-or-zip> --forbid "a player/persona name" --forbid "another name"
```

Repeat `--forbid` for everyone seen in that session and for machine/user names not covered by the current host's
environment. The scanner checks relative filenames, raw and decoded JSON strings/numbers, expanded archive members,
excluded attachments and ZIP timestamps without extracting. It fails on malformed/unreadable inputs. The ordinary
repository scanner mode is unchanged. An in-game capture is still needed to confirm Unity/Mono observations,
real Steam persona registration and the whole-game cost; offline tests do not run the game.

## Clock

Every record carries `st`: **session time** in seconds (double, 3 decimals). In a multiplayer session it is the
host's clock as estimated on each machine (`Net.ModObjects.SessionTime`, round-trip corrected), so the same `st` is
the same moment on every machine (to a few ms). Outside a session `st` is this machine's
`Time.realtimeSinceStartupAsDouble`. Each file's header also gives `stSource`: `"host"` or `"local"`, and the session id
(`sid`, a random 64-bit hex string the host makes when it starts hosting and sends to joiners) so files from one
session can be grouped. Records also carry `t`: seconds since this file was opened.

## Play log (single player and multiplayer)

`BepInEx/tldrevamp-feedback/sessions/<yyyyMMdd-HHmmss-fff>-<role>.jsonl`, `role` = `solo`, `host`, `client`, `dedicated`.
A new file when a world is entered (solo) or a session starts; closed on leaving. Newest 20 kept. One JSON object per
line, flushed per line. Kinds (`k`):

- `"head"`: first line: `start` (UTC date), `recording` (random local recording id), `mod`, `build`, `commit`, `game`, `role`, `seed`, `sid` (or null), `stSource`,
  `settings` (resolution, fullscreen, quality index, vsync, target fps, the game's view distance if available, RevaMP's
  settings that affect performance), `hw` (CPU name, cores, RAM MB, GPU name, VRAM MB, OS).
- `"tick"`: every 5 s: frame `p50ms`, `p95ms`, `p99ms`, `maxms` over the interval (not a 10 s ring), `frames`,
  `hitches50` in the interval, `gc0`/`gc1`/`gc2` GC evidence summed over the interval (see below), `allocKBps` (estimated managed allocation
  rate: sum of positive managed-heap deltas per frame / seconds; a lower bound), `managedMB`, `unityAllocMB`,
  `unityReservedMB`, `pos` [x, z] in global metres (rounded to 10 m), `speedKmh` (player or their car), `driving`
  (bool), `gen` (world generation backlog, see below), `counts` (cheap counters: tracked items, cars nearby, awake
  rigidbodies — sampled, see below), `net` (multiplayer only: as today), `mp`, `ai`, `combat`, `handoffs` (as today),
  `fixes` (counter deltas of every fix that has counters, only the non-zero ones, e.g. {"FrozenFlyers.Skipped": 120}).
  `selfMs` is telemetry's measured main-thread milliseconds per frame over the interval;
  `selfAllocBytesPerFrame` includes the amortized allocations from periodic records (`null` where the runtime has no
  per-thread allocation counter, as on the beta: see Performance telemetry). Also `bound`, `frame`, `render`, and with
  the script timers `scripts` and `loop` (see Performance telemetry).
- `"hitch"`: one per frame over 50 ms (at most 2 per second; further ones counted in the next tick's `hitchesDropped`):
  `ms`, `phases` (that frame's FrameBreakdown phases in ms, non-zero only), `gc` (gen0/1/2 GC evidence that frame),
  `rebase` (bool: the floating-origin shift ran this frame), `saving` (bool), `loading` (bool), `stream` (the game's
  item streaming pass ran: bool), `gen` (backlog at that frame), `genDone` (terrain tiles / road batches / scenery
  chunks / buildings finished this frame), `speedKmh`, `gpuBound`, `frame`, `render`, `scene`, and with the script
  timers `scripts` and `loop` (see Performance telemetry).
- `"load"`: world load milestones from pressing Start/Load or joining: `kind` (new, load, join), `firstUsableS`,
  and for a join the client's stages (connect, world data, first state) when known.
- `"save"`: each save: `ms` (duration of the game's save), `auto` (bool), `bytes` if known.
- `"mark"`: a marker: `note`, `by` (player name is NOT stored: `by` is `"me"`, `"host"` or `"player"`), `st`.
- `"warn"`: every 60 s, the top 10 log warnings/errors of the interval by count: `[{"kind": "warning"|"error"|
  "exception", "msg": "[removed]", "at": "[removed]" or "", "n": count}]`. Full counts since start are in the
  report.

`gen` (cheap fields of the game's world generators, without RevaMP-only state where possible): pending terrain tiles
around generation centres, road chunks not yet generated within the needed area, buildings (POIs) waiting for
placement or roads inside the current road generation area, scenery chunks waiting, items waiting in the streaming
queue — each an int; `-1` when unknown. `buildingsDeferred` counts POI candidates blocked by unfinished roads outside
that area; it is excluded from the black-box `gen` total. These candidates remain in the game's queue until travel
or a higher draw distance brings their roads into scope.
`counts` is sampled at most every 5 s and must not walk the whole scene every time: use the game's own lists
(`savedatascript.s.items`, the car list RevaMP keeps, the physics count if available); a full FindObjectsOfType walk
is allowed at most once per 60 s and must be measured (its own cost logged).

## Black box

Every machine keeps the last **30 s** of per-frame data in a fixed ring (allocation-free): `st`, frame ms, the
FrameBreakdown phases (ms), GC collections, rebase/stream/save flags, generation backlog total, bytes sent/received
this frame (multiplayer), speed. When a report is made (Page Down, the host's synced ask, or `/mark`), the ring is
written as `blackbox.json` into the report (and for `/mark` into
`BepInEx/tldrevamp-feedback/marks/<UTC date>-<sid>/mark-<sid>-<momentSt>-<note-slug>.json`,
with privacy-cleaned slugs budgeted to fit the actual folder path), continuing for **5 s after** the moment so the
aftermath is included: `{"head": {...as the play log head..., "momentSt": st}, "cols": ["st","ms",...], "rows": [[...],...]}`
(columns + rows: compact).

### State and network history (black-box format 3)

`head.format` is now `3`; files without it are format 1. The comparer accepts formats 1, 2 and 3. No multiplayer wire change is
needed: the existing synchronized mark already supplies `momentSt` on every machine.

Each capture also freezes a `state` section **on the main thread when the mark/report arrives**, before waiting for
the five-second aftermath. `state.st` is the actual capture time; `head.momentSt` remains the host's original mark.
The comparer calls out capture skew above 50 ms. Capture reads existing objects and records without resolving,
spawning or repairing them. Four preallocated state/event buffers follow the existing four frame capture buffers;
the worker owns a buffer until it finishes writing the same black-box file.

- Scope: shared entities within **300 m** of the local player, plus every car named by any player's seat. Seated
  cars are captured first, then other nearby cars, then nearby items. With no local player, only seated cars are
  included. Limits: **512 entities, 2,048 slot entries, 128 players, 256 seats, 64 storms, 128 radio stations** and
  a conservative **512 KiB serialized-state budget**, whichever fills first. Runtime labels are removed; any retained diagnostic strings are privacy-gated and
  limited to 64 characters; worst-case JSON escaping is included in budget charges. The byte budget usually binds
  before the entry limits. `dropped`, `outside`, `failed`, `budgetBytes`, `hasCenter`, `centerCm`, `radiusM` and
  `captureMs` explain coverage, truncation and the one-off capture cost. A missing field is unknown.
  A preallocated index of at most 32,768 far-store item records avoids one linear save search per dormant object;
  overflow is counted in `dropped`. Seated-car record lookups may fall back to the save's normal search.
- `entities`: `net`, ownership `epoch`, `owner` (session player index), `proxy`, numeric `prefab`, `name`, global
  `posCm` (centimetres), `yaw` (degrees), `speed` / `vel` (m/s), `stored` (inventory), `dormant` (far store), actual
  attachment `parent` net id and original group `part` index. `poseSt` is the **owning sender's unscaled clock**:
  the owner's physics-step clock or a proxy's interpolator render time. It is deliberately not called `st` and
  must only be compared within the same owner and epoch. Parent-riding copies without an independent clock use null.
- Loaded cars additionally give `fuel` (litres), `engine`, `ignition`, ignition usable `key` turn state, and the
  installed radio's `radio` tuning, `radioOn`, `radioAm`. No installed radio gives null tuning. These fields are
  omitted for dormant cars: the save does not preserve every live engine/radio value. All available slot lists
  in the car's group are walked, including slots on attached members. Each slot is `(net,item,index)`, with name,
  first compatible type, `contentNet` + canonical `contentItem`, prefab/name, attached/loose and condition state.
  Empty slots are explicit. A slot accepting multiple parts has multiple entries. `localItem` preserves the local
  save id for diagnosing an unshared part; it is never used as cross-machine identity. `condition2`/`condition3`
  retain the other condition values. Dormant slots are read from prefab definitions and existing far-store attachments.
- `players`: session player indices, constructed player-index aliases, known positions and seat `(net,seat)`; `seats` retains
  each loaded car seat's index/name, vanilla `inUse`/`inUseMulti` flags and all player claims, including conflicts.
  `localPlayer` lets the comparer identify each machine's own seat claim. Remote positions are the latest received
  positions, not an invented simultaneous sample; `poseSt` retains the packet's sender clock separately from `st`.
- World `time` is seconds through a 24-hour day (the sky's normalized time), `cloud` is low-cloud density, `sand`
  is local storm exposure; `storms` gives prefab, global position, yaw, size, speed and life. `stations` gives
  synchronized FM/AM broadcast clip indices and playhead seconds. Custom music names/paths are excluded. `day` is
  null in this beta: its sky wraps `t` and its save has no elapsed-day counter; telemetry does not invent one.

`events` holds `rows` and an `overwritten` count. A preallocated **2,000-record ring** retains up to **35 s** by the
local monotonic clock. It is copied at the mark, then receives events through the five-second aftermath, still
subject to the same capacity/age bounds. Session changes clear it. Each record has session `st`, literal `kind`,
`net`, `other`, `epoch`, canonical `item` and `slot`/usable index, `from`/`to` session player indices, `count`, and
`result`. Unknown addresses are -1 (or net 0). Send/apply/refuse are separate observations, not proof of delivery;
the edit wire message does not retain the originating player at the receiver, so that `from` is unknown.

Hooks record attach/detach/part-off and part-condition changes, spawn/add, remove, ownership decisions/claims and
refusals, handovers, edits (including the actual `Entities.EditCheck` refusal reason), live-usable send/apply,
resync send/apply counts (no payloads), seat enter/leave, join/leave and TPA decisions/results. Live-usables use a fixed
512-cell sampling table: at most one record per hashed object/item/usable/direction every 500 ms; collisions can
sample more sparsely. Hooks append value records without allocating, formatting strings or serializing payloads.
Approved literal result reasons are retained; unrecognized free text becomes `[removed]` on worker output.

State differences appear first in each moment's HTML and stdout. The comparer joins by net id and checks ownership,
epoch, inventory/parent state, slots/conditions, fuel (>0.1 L), engine/ignition/key, seats, tuning (>0.05), broadcast
clips/playheads (>2 s), time of day (>2 game seconds, wrapping midnight), cloud/exposure (>0.1). Positions use a
**1 m** residual tolerance after a constant-velocity correction in the same sender clock (at most 1 s); larger
clock gaps are reported as stale/uncertain. Without that clock it adds speed times 150 ms plus capture skew to the
tolerance. Acceleration, correction blending and handovers can still require human interpretation. Object absence
is called missing only within the other machine's coverage and without truncation/failure; absent slot data is
unknown, never treated as an empty slot. Related events from **10 s before to 5 s after** the mark are merged by
`st`, from both machines. Stdout prints the first 40 related events; HTML retains all matching records.

Privacy follows the inventory above: player indices preserve relationships; runtime labels and arbitrary log
messages are removed. Hardware model strings remain permitted after cleaning. Buffers and worker output stay
inside the existing local report/mark and sharing flow.

### Motion, traffic and long frames (format 3)

`trace` adds a fixed **32,768-row**, **35 s** motion ring on every machine. At most **8 entities** are in focus:
the seated car or ground contact parent, crosshair target, held/dragged items, then the last **4** locally
picked-up, used, pushed or attached entities. The focus set refreshes at most every **100 ms**; duplicate ids
take one place. The crosshair hook reuses the first interaction ray in `fpscontroller.RayStuff`. Ground contact
uses `grounded.T` and the last existing `LocalFooting` result, including the bus's separate rear body. Telemetry
casts no rays. Unshared objects have no net id and cannot enter the trace.

Rows use `cols` plus numeric arrays: local monotonic `local`, session `st`, `net`, `epoch`, `owner`, `kind`,
sender `clock`, global `xCm`/`yCm`/`zCm`, `yaw`, `speed` (m/s), receive `interval` (s), `age` of the newest
received sample (s), `mode`, `correction` (m), canonical `item` and usable `slot`. Kinds: **0 sent, 1 received,
2 rendered, 3 live-usable sent, 4 live-usable applied, 5 focus entered, 6 focus left**. Modes: **0 interpolate,
1 extrapolate, 2 hold, 3 snap, 4 parent/physics** (the parent or local physics determines the displayed pose).
The rendered position is read in LateUpdate, including body-moved, carried and bolted copies. Sender clocks stay
separate from `st`; receipt/render timestamps include network and interpolation delay. Live-usable rows carry
addresses and rates; their pose columns are unused. Receipt corrections describe curve changes; render corrections
include the remaining blend, servo error or actual body snap. No sample age is invented before a receipt.

`focus` lists current ids; `rates` gives sent/received counts and Hz over time in focus, including quiet time,
plus each observed `(item,slot)` live-usable send/apply rate. These counts run before event sampling. Different
focus windows, resting objects, handovers and overwritten rows limit rate comparisons; rates after overflow are
lower bounds. `overwritten` and `focusDropped` expose bounds. The capture worker writes the trace with the existing
black box; no Unity objects or live arrays are read on that worker. Bridge `trace` returns focus ids and rates
without the sample arrays. Session changes clear the rings and recent touches.

Riding diagnostics (protocol 38) add kinds **7 rider root, 8 rider pelvis target**. For these kinds `net` is the
vehicle, `item` is the anonymous session player index and `slot` is the seat index (-1 standing). Local and remote
riders are recorded; focused cars also record their rendered pose on the owner. Their `clock` is the vehicle
owner's displayed time, including Rigidbody interpolation, so a same-frame kind-2 car row and kind-7/8 rider rows
can be compared without mixing the passenger's clock with the driver's. Positions remain global centimetres.
These rows share the existing bounded ring; many riders can therefore shorten its retained window.

The **5 s tick** now has `network`: interval `seconds`, `st`, non-zero message `types` (literal `name`, `sent`,
`received`, `outBytes`, `inBytes`, `handleMs`), anonymous `peers`, and `categories`. Each category has the same
counts/bytes and an `owned` gauge: **car, trailer, item, rabbit/animal, AI/munka, ragdoll/corpse, player, other**.
Category counts cover pose and creature animation state traffic, including player pose packets. Ownership counts
are entity roots plus the local player, excluding the dedicated host's phantom. Categories use existing roots,
prefabs and ragdoll/death state; a dormant entity retains its known category. Unknown records are `other`.
The transport counts real sends, receives and host relays, including batch length prefixes; local host delivery
is excluded. Thus fan-out increases sent category counts. Trace sent rates count owner state production once.
Only totals and addresses are inspected; packet contents are not retained.

Black-box `network.rows` keeps at most **8 intervals / 35 s**, with `overwritten`. Peer intervals use **128 cells**,
at most **4 probes** per message; `peersDropped` counts collisions/overflow without losing type/category totals.
Player indices are used, never account ids or connection handles. These interval counters are independent of
`net meter reset` and the overlay. Bridge `netcat` returns the current category interval and ownership gauges.
Per-frame category bytes are omitted; the existing total byte columns remain.

Every category also includes `loaded`, `dormant`, `proxy`, `moving`, `resting`, `alive`, `limp`, `dead`,
and `receivers`. Life counts are exclusive: upright live creatures, living ragdolls, and dead creatures/bodies.
Activity counts cover loaded roots; owner rest follows the final-state decision, proxy rest follows the received
final/stored flag (including limb-only motion). Dormant entries have no inferred life/activity. `owned` includes
dormant roots. `receivers` is the host's sum of known non-owner viewers within 1 km, including the local host;
clients report zero because they lack the server's Known sets. These are end-of-interval gauges, not unique sends.

`avgOutBytes` is `outBytes/sent` (zero without sends), including batch prefixes. `fullSent/fullOutBytes`,
`compactSent/compactOutBytes`, `rabbitSent/rabbitOutBytes`, `aiSent/aiOutBytes` split actual outgoing category
traffic by full pose, compact pose, RabbitState and AiState. `maxOutBytes` is the largest outgoing message.
`restSent/restOutBytes` count sends whose source is at its final/stored rest state, excluding driven cars;
`restPoseSent` excludes animation changes. `restObjectsSending` counts distinct resting network roots sent in
that interval; relays/fan-out count again as messages but once as objects. A final, interest re-entry snapshot,
new epoch or genuine flag/animation change is legitimate and is included. No object names or payloads are saved.
These numeric fields pass the existing privacy validator; they need no string allowlist additions.

`wastecheck.py` (private two-copy checks folder) waits 30 s, then records at least 30 s of complete intervals and per-object
state counters (35 s wall time avoids partial five-second intervals). Settled, unchanged, unoccupied objects have no recurring pose keepalive: one final ends their
visible-change tail (at most 5 s). A driven parked car retains its 0.5 s lease refresh; the fixture parks and exits.
RabbitState has reliable changes and no periodic refresh; other live AI may refresh once per second. Objects
with no remote viewer retain only changed far-store maintenance, at most once per 5 s, plus real transitions;
unchanged resting objects stay silent. The far phase asserts zero host pose relays after warmup and checks
accepted owner uploads for a moving rabbit and two tumbleweeds against the five-second maintenance interval.
The host caches owner maintenance and essential pose transitions for re-entry instead of relaying them to
distant viewers. Ownership/control messages keep their existing paths.
Numbers and raw bridge evidence go only into the requested private out directory.

`network.bandwidth` (also in `netcat`) keeps cumulative counters since the session counter reset: skipped states
`identical`, `corpseRoot`, `unknown`, `creatureRange`, `deadAnimation`. The skip counters count suppressed
candidates/recipients, not bytes saved; `deadAnimation` counts only candidates whose original dead animation
inputs would have sent. The one-second live non-rabbit creature refresh is retained; dead and rabbit animation have no periodic refresh. Network history copies these
values with its intervals. `owned` remains the existing all-owned gauge, including resting roots.
Compare counter deltas and category bytes with `Entities.WasteFixes` off and on; the switch changes neither
the wire protocol nor `mp share`.

Every frame **over 500 ms** bypasses the ordinary hitch-line rate limit and adds `long` to its hitch line and a
record to black-box `long.rows` (**64 records / 35 s**, with `overwritten`). It keeps `st`, `ms`, `instantiated`,
`destroyed`, `activated`, `bodiesAdded`, `rebaseMoved`, `genDone`, per-message-type `network` counts/bytes/handler ms,
and `timers` (the 15 existing phases and eight coarse groups plus the truncation flag). Counter deltas bracket
Runner.Update, matching the wall-frame interval, including work before this Update. A stall crossing the aftermath
boundary is kept.
Hitch JSON for `long` is serialized from detached buffers on a worker, then appended by the main-thread writer;
up to **4** such jobs are pending. `longHitchesDropped` on ticks counts exhausted hitch-job slots. The black-box
long record is retained even if that play-log detail job is dropped.

Object counts instrument managed Instantiate/Destroy/SetActive call sites in spawn, save-item, streaming and
world-generation paths, ItemSnapshot, Entities and replacement fixes, including their iterator bodies. They count
successful instantiate calls, destroy requests, and transitions to activeSelf true in those paths, not every Unity
object or pooled activation in the scene. No global native Object hook or scene scan is added. `bodiesAdded`
counts registered item-root bodies at Awake and new `mainscript.AddRB` bodies; prefab limbs without an item root
are outside this count. Header `telemetry.spawnMethods` counts inspected call-site candidates; `spawnSkipped`
counts methods whose IL could not be inspected. A **512-cell / 4-probe** per-frame table deduplicates body
registrations; `bodiesDropped` counts collisions/overflow. `rebaseMoved` counts actual non-zero `visszarako` moves,
excluding parent-riding objects.
These are work counters with explicit coverage, not before/after whole-scene object totals. `genDone` retains
terrain, roads, scenery and buildings, including the replacement generation paths.

`state.players` adds `held` and `inventory` arrays with net/canonical item, prefab/name, held/inventory `kind`
(0/2; legacy 1), inventory `slot`, `inventoryCount` and `inventoryComplete`. Local contents use `fpscontroller.inventory`,
`inHandP` and `pickedUp`. Kind 0 now covers both equipped and grabbed items, and kind 2 means inventory;
older format-3 captures used 1 for a locally grabbed item while observers always wrote 0. The wire carries
only a Held flag, so the hand/drag distinction cannot be compared across machines. Names come from the
prefab table for a known prefab id, through the same content privacy gate; instance names are fallback display
labels only. The comparer includes prefab ids in item identity and ignores names and legacy hand kinds.
Remote contents are observed shared held/stored entities attributed to their owner;
their inventory is explicitly partial. These values may be stale, as are remote poses. Limits: **1,024 carried
entries**, within the existing **512 KiB** state budget. The comparer reports observed differences and calls out
partial inventories or truncated captures; absence there is unknown.

`state.usables` captures loaded cars' group members and loaded buildings within the existing scope: canonical
`(net,item,index)` or building `(gen,poi,index)`, name, `x`/`y` angles, `slide`, `turn`, `tuned`, and `locked`.
The beta's `usablescript` exposes no lock flag: `locked` is null, not inferred from `disable`. Bus doors have
`busDoor:true`, their commanded open `turn` and mean visible opening fraction in `slide`; their index is separate
from normal usable indices. Radio tuning remains on the car and on its dial usable. Limit: **2,048 usables**,
also charged to the state byte budget. Comparison tolerances: **1 degree** for angles, **0.02** for slide/open
fraction, **0.05** for tuning; turn/known lock states compare exactly. Missing usable rows are unknown.

Physics events add `speed-jump` (**>25 m/s velocity change in one physics step**), `speed-high` (**>80 m/s** for
non-cars), `owner-teleport` (step/sent displacement beyond **25 m + speed times elapsed time**), `proxy-snap` and
`proxy-correction` (**>2 m**, including servo return/rest snaps), `player-damage`, `player-damage-queued`,
`player-death`, `joint-created` and `joint-broken`. `value` carries m, m/s or damage; joints carry both known net
ids (0 for an unshared end). Damage/death retain fall or remote creature/shot/melee context where known; the
game's general damage API has no cause argument. Queued damage and later health application are separate
observations. Ownership epochs fence physics comparisons. Events use the fixed **512-cell / 500 ms** sampler;
`events.sampled` counts suppressed observations, including collisions. Snap/usable trace counts are independent.

Physics-step velocity/displacement probes default to the **8 focus entities**. `[Telemetry] AllOwnedPhysics = true`
(bridge `set TLDRevamp.Net.Entities.TelemetryAllOwnedPhysics True`) extends them to at most **4,096 loaded owned
bodies per physics step**. This is **off by default**: reading every body's Unity velocity/position scales with
the ownership pile and cannot be kept within the always-on budget for arbitrary scenes. `physicsAllOwned` and
`physicsSkipped` in ticks/state record the scope and observations omitted by that cap. Non-consecutive observations
are never called a one-step velocity change. Sent-state teleports, proxy corrections, joints and player damage/death
still run for every observed entity, independent of that switch.

TPA events cover client command sends, server requests/pending/accept/refuse, invalid or missing destinations,
disabled/cooldown/rate limits, missing/ambiguous targets, expiration/disconnect, failed sends, missing local
player/car and missing ground. Command arguments and notice/chat text are never recorded. `tpa-arrived.value`
is distance from the requested destination **500 ms after placement/car release**, with a **2 m** grounded arrival
check; `dx`/`dy`/`dz` retain the signed offset in metres, including the height error. The requester emits the event
after its five-second object observation window and deferred entity replay; `from` is its session player index and
`count` counts distinct applied destination Add/state groups. A separate host `tpa-arrived` uses the same `from`,
records the observed displacement and counts delivered destination groups (new records or known-copy state refreshes).
This host event proves delivery was queued, not that the client rendered those objects. Compare both captures and
actual player/entity positions. `tpa-failure` records invalid destinations, lost players/cars, floor/2 m check failures,
and delivery timeouts; it is separate from the subsequent observed arrival event.
Strict placement tries stationary vehicle roofs/beds and clear spots on 1-3 m rings. Static ground readiness starts
its three-second window before the ordinary any-floor fallback; lower landings retry for up to 30 seconds so late
building floors can correct them. Moved bodies and unrefreshed proxy vehicles cannot establish readiness or floors.
Missing geometry can still require the ordinary ground wait. A car accepts any available static
floor below or above the target's level, searching rings up to 24 m with clearance for the moved group's bounds,
and is lowered onto it before release. Absent floor/bounds keeps it held against the game's streamer, including after
the 30 s failure event; failed arrival telemetry still completes. A repeat TPA restores the original body modes first.
Known destination copies retain their far-store records: arrival refreshes poses/snapshots and only stale item records.
Missing copies receive full Add groups; arrival states for an absent client net request one Refetch per arrival
(detached parts refetch their parent). The game's `DoUpd/PlaceStuff` loads stored copies after `TpSettle` refreshes
generation centres; duplicate Adds do not synchronously call `GetOne`. Geometry-dependent entity updates wait for ground;
control traffic such as clock, census, lease and existing-copy ownership updates continues. Ownership following a queued
Add waits for that Add, as do its hitch freeze/frozen pose messages. Requests, successful outcomes and failures are
not sampled. No arrival acknowledgement is added
to the wire. Protocol remains **41**.

`D:/TLD-copies/test-scripts/tpalongcheck.py` checks a 9,400 km return into a newly populated destination, paired arrival
events, actual distance (allowing the seated target's 7 m side offset) and identical live net-id sets within 300 m,
plus roof, indoor/wall and seated targets. A pre-known car changes pose and part state while the joiner is away;
both return passes must show those changes and all earlier fixtures live. It repeats
the long jump with `net lag 250 1 5 30` on both copies when available. Client `stats` snapshots and the mark's frame
`cols/rows` and `long` rows record arrival frame times; bridge latency is recorded separately. `tpalanding` reports
floor/gap/room at the actual player position (the room capsule includes vehicle colliders); `tpaindoor` finds a loaded
building within 300 m with a floor, ceiling and wall 1.0-1.5 m to the host's yaw-right blocking the initial side spot, or returns a missing reason. These are test probes,
not network messages. The script only connects to already running copies; `--selftest` is offline.

The comparer plots owner sends, proxy receipts and rendered x/y/z on the shared session axis, prints update and
usable rates/snaps, a category traffic table, long-frame work and handler/timer details, carried/usable differences
and physics/TPA events. Sender/epoch fences prevent comparing different owners. Formats 1/2 keep their earlier
frame/state views; missing format-3 sections remain unknown.

Offline Release measurement (2026-10-19, .NET 9 `TelemetryTest`, **100,000 warmed frames**): **0.0050-0.0060 ms/frame,
0 allocated bytes**, including eight focus entities (render plus 20 Hz sent/received samples), 128 simulated
messages with peer-table updates, the early-message counter copy, 128 coarse probes and frame/event/long buffers.
The coarse/frame/event baseline in the same runs was **0.0042-0.0046 ms/frame**. This measures buffer operations,
not Unity lookups, transport clock reads, Harmony dispatch, generator hooks or Mono. The total main-thread **0.05 ms/frame target still needs
an in-game A/B measurement**; the existing measured game baseline is unchanged. `telemetrytest` now includes
eight synthetic focus entities and 128 synthetic messages in addition to its Unity sampler. Real focus selection,
physics/category sampling and hook costs need `selfMs` plus whole-frame A/B measurements. Capture/JSON costs stay
at record boundaries; `state.captureMs` still exposes state capture cost. The five trace buffers add about **15 MiB**;
long histories and pending hitch buffers add about **3.2 MiB**, excluding the existing frame/state buffers.

## Markers: key, list and /mark

**Scroll Lock** (configurable `[Keys] Mark` in `BepInEx/config/tldrevamp.core.cfg`) opens a note box and this
session's marks. The RevaMP tab also has a Session marks button showing the configured key. Enter saves;
Escape cancels/closes. Empty notes are allowed. Notes are privacy-cleaned before the **500-character** limit;
the complete cleaned note is retained in `head.note`, the play log, and the indexes. A successful disk write
confirms `Mark saved: <note>`. Captures include five seconds of aftermath, so confirmation is delayed until then.
The list groups observer copies by mark and shows session seconds, anonymous author and role, and the full note, including received teammates'
marks while sharing is enabled. Edit appears only on the local player's marks and selects a note for renaming; Generate comparison refreshes the HTML page.

Chat `/mark <note>` and bridge `mark <note>` use the same capture route (chat's own input length limit still
applies). The host assigns the moment and anonymous originating player index and broadcasts to every player
(message 92). Each machine records its own observed state. The limit remains one mark per player per 10 s.
Bridge `markpanel save <note>` and `markpanel rename <sid> <st> <origin> <note>` use the panel's submission path.
`markrename rename <sid> <st> <origin> <note>` bypasses the panel gate to test the host's authorization.
Solo marks receive a random session id and retain their local clock; multiplayer uses host session time.

Local and received marks live side by side in `marks/<UTC date>-<sid>/`. The folder date is the host session's starting
UTC date (solo: local session start), carried in Welcome and fixed across midnight and reconnects. Received
files use the same folder. `head.date` records the capture's UTC date separately for the index. Names are
`mark-<sid>-<st>-<note-slug>.json`, with `-from-p<index>-<hash8>` before `.json` for received captures.
Slugs use as much cleaned ASCII prose as fits the **actual absolute folder**, reserving space for `.tmp` and
collision suffixes; final and temporary paths stay below **240 characters**. An impossibly long installation
folder reports a file error. Short filenames do not truncate stored notes. No personal attribution or absolute
paths enter these files. `marks/index.json` and readable `marks/index.txt` list every saved mark across sessions:
UTC date, session seconds, sid, role, origin, observer/source, full note, relative filename and received flag.
The play log references the stable relative session folder. Existing flat marks are indexed without moving them.

Each new black-box head includes `focus`: `crosshair`, `car`, `seat`, `held`, `dragging`, `closestPlayer`.
Object targets contain `net`, canonical content-allowlisted `name`, `prefab` and distance in metres. The ray uses
the player's world camera and a 300 m range; unavailable targets are null, unknown prefab/seat is -1 and
unshared object net id is 0. The closest player uses only a session index, constructed alias and distance.
Unity observations are detached on the main thread; JSON, file naming/collisions, indexes, renames and HTML run
on a serialized worker queue. Both comparison pages put focus first and highlight recorded state differences
for these objects/players. Missing state is unknown; observers can capture at slightly different arrival times.

Edits update all this machine's copies keyed by **sid + momentSt (milliseconds) + origin**: head note, filename,
indexes and comparison page. The date/sid folder stays stable. Received suffixes retain the original transfer
hash as provenance; each changed file must still pass the normal content validator. With sharing enabled,
reliable message **104** sends the small rename through the host to current sharing players. A sharing
participant may edit their own known session marks; the host may also rename another origin's mark. The host authenticates the editor, checks origin, consent and known mark key,
cleans the note, rate-limits edits (one per 0.5 s per editor), and orders relays. A non-sharing host relays without
editing its disk copies. Failed sends retry in order per target under the transfer byte budget; consent/session changes cancel queued deliveries. Renames arriving
before a pending capture or transfer finishes are applied when that file is registered. Opt-out edits stay local;
there is no rename replay when someone later joins or enables sharing. Clients reject relays whose renamer
is neither the origin nor host (index 0). Protocol **41** appends that authenticated renamer index to message 104;
protocol 40 added the stable UTC session date in Welcome, the exact origin index in message 92 and rename synchronization.

## Reports

The report zip (FeedbackReporter) additionally contains: `blackbox.json`, the current play log, `breakdown.json`
(FrameBreakdown since start, all vs slow frames), `errors.json` (errors and warnings by kind since start, counts) and
`fixes.json` (every fix counter since start).

With sharing on, a report made locally goes to every other sharing player. `askReports` (default on) lets a host
ask sharing players to capture reports at its report's session moment, even when that host's sharing is off.
Non-sharing clients ignore asks without making a report. A host receives the answers only if it shares too.

### Transfer files, limits and counters (introduced in protocol 33; current protocol 40)

The receiver saves marks as `marks/<UTC date>-<sid>/mark-<sid>-<st>-<note-slug>-from-p<index>-<hash8>.json`, and reports as
`players/player-<index>/report-from-p<index>-<hash8>.zip`. Mark filenames include only privacy-cleaned,
path-budgeted note slugs and anonymous session indices. Archives are never extracted. Received files
share a **200 MiB** disk budget, oldest received files removed first; local captures/reports are not pruned by
sharing. Prefixes, numeric indices and hex content hashes prevent traversal/device-name collisions.

- Reliable chunks: **16 KiB**, **160 KiB/s** globally out of each machine (host fan-out shares that budget), at
  most one chunk per target per frame and two chunks of accumulated budget. A send waits while Steam has pending
  reliable traffic or estimates a queue delay of 20 ms or more. Failed sends retain their offset for retry.
- Host incoming traffic: per authenticated account **160 KiB/s**, with a **64 KiB** burst. Starts: at most one
  mark per **10 s**, one report per **30 s**; at most **80 marks + 20 reports** and **64 MiB** announced bytes per
  sender per session. Aborted/rejected starts still consume quota; reconnecting does not reset an authenticated
  account's allowance. Unauthenticated connections are limited by their connection player id.
- File limits: mark **2 MiB compressed / 16 MiB JSON**; report **24 MiB compressed**, at most **256 entries**,
  **32 MiB per entry / 96 MiB total expanded** (JSON attachments additionally capped at 16 MiB). Both host and
  receivers check hashes, strict UTF-8/JSON syntax and depth (64), black-box session/moment/header, GZip checksum,
  archive paths, duplicate entry names, entry checksums, expanded sizes and required `stats.json` / `blackbox.json`
  attachments. Report zips must be single-part, without ZIP64, with at most 128 KiB of central directory metadata.
- Receivers enforce the same per-sender counts/byte limits, allowing relay arrivals to be batched. Each machine
  accepts at most **256 MiB** announced incoming bytes per session. Host delivery reservations (file bytes times
  recipients, including its own outbound files and interrupted deliveries) are capped at **256 MiB per session**.
- At most **64 MiB** reserved incoming compressed data, **4** preparation/validation workers, **32** queued
  deliveries / **64 MiB** queued output, **256** sender identities and **4** disk jobs. Buffers grow with received chunks. Idle partial
  transfers expire after **120 s**. Opt-out/disconnect cancels that player's input/output; workers are checked
  against session and consent generations before use. There is no replay to a rejoining player.
- SHA-256 suppresses repeated files for each sender for the session, including authenticated reconnects at the
  host. The relay replaces client attribution with the authenticated connection's session index; receivers validate
  content again. An untrusted host can still inspect or misattribute data, as it controls the session transport.

Bridge `mp sharing` (`mp reports` alias) returns sent/received/relayed files and compressed payload bytes, refusals,
duplicates, queued deliveries and reserved relay bytes. `marksSent`, `reportsSent`, `marksReceived` and
`reportsReceived` split the totals by file kind. `refusedByReason` counts refusal reasons; `lastDrop` identifies
preparation versus incoming validation, mark versus report, and the JSON character offset without echoing
untrusted values or filesystem error paths. Host-created deliveries count as sent; forwarding another
player's file counts as relayed, once per recipient. Received counts advance only after a successful disk write.
The chat/overlay announces a teammate's saved black box and mark note. (`mp share` still shares the nearest car.)

## Comparison page

The mod automatically writes a self-contained **compact C# summary** to the session folder's `comparison.html`
on session end or on demand from the panel; an existing page is refreshed after subsequent saves/edits. It needs no Python, browser process or other
external program. It shows notes, focus first, and per-observer focused entity/slot/seat/usable/player state;
different values are highlighted. It does not port the full timeline, performance or coverage analysis below.

`tools/telemetry_compare.py` (Python 3.9+, standard library only):

    python tools/telemetry_compare.py <folder or zips...> [-o out_dir]

Input: one machine's entire `BepInEx/tldrevamp-feedback/` folder, including local and received marks, nested
`players/<player>/*.zip`, its own report zips and play logs (or individual files). Recurses automatically. Groups
everything by `sid` (or one local machine/session identity) and moment (`momentSt`, within 2 s), and writes one self-contained HTML page per moment
(inline SVG, no internet): one row per machine (role, hardware, settings), frame times of every machine on one
shared session-time axis (the 30 s before + 5 s after), phases stacked for slow frames, GC/rebase/stream/save marks,
generation backlog, network bytes and ping per machine, and a **differences** section that states, in plain words,
what differs between machines at the moment (e.g. "Only `client 2` had frames over 50 ms: 3 of them, all with a
garbage collection and 40 terrain tiles waiting"). New filenames and state use player indices only; legacy files remain readable. Also prints a short text summary.

A solo/local-clock file is a consistent timeline for its own machine; `sid:null` does not discard session seconds.
Role/recording/hardware headers (start as a legacy fallback) associate its loose marks, log and report attachments. Two marks of one moment are
one machine: one complete snapshot is selected, including every row even when millisecond timestamps repeat.
Play-log hitch lines are deduplicated against those frames and against copies inside reports. Each page reports
its moment's 30+5 second window, while stdout also counts distinct hitch records across the whole input logs.
Missing timestamps use file order with a `session time unavailable` notice. `timeline not synced` is reserved for
multi-machine comparisons without a common host clock.

## Performance telemetry (where the time goes)

Unity's own profiler markers mostly report invalid in the shipped player (only `PlayerLoop`, `Main Thread`,
`GC.Collect` and `Gfx.WaitForPresentOnGfxThread` of the list above). Bridge `counters list|start|read|gc` shows what
the player does fill; checked 2026-10-18 on the beta: the render statistics, FrameTimingManager's thread/GPU times
and memory totals, but no physics counters and no `GC Allocated In Frame`.

**Frame time** is wall time between two frames (Stopwatch, Runner.Update to Runner.Update), the same measure as
TLDBench. It used to be `Time.unscaledDeltaTime`, which with VSync on reports the display's refresh interval
instead (measured: a steady 2.77 ms on a 360 Hz display while frames took 12.8 ms), so slow frames were invisible.

**Always on** (inside the 5 s tick, the hitch line and the black box; cost included in `selfMs`):

- `busyMs`: the frame minus the wait for the GPU (the larger of the `Gfx.WaitForPresentOnGfxThread` marker and, when
  the player-loop phases run, `TimeUpdate.WaitForLastPresentationAndUpdateTime`; in this player the marker stays near
  0 while the main thread waits in that phase).
- `mainThreadMs`, `renderThreadMs`, `gpuMs`: FrameTimingManager's CPU main thread, CPU render thread and GPU frame
  times (`CPU Main Thread Frame Time`, `CPU Render Thread Frame Time`, `GPU Frame Time`; the GPU time lags a few frames).
- `gpuBound`: 1 when `gpuMs` is at least 85% of the frame (without a GPU time: the GPU wait above 0.5 ms and 20% of the
  frame), else 0 = CPU-bound (main or render thread; compare the two thread times).
- Render counters of the previous frame: `drawCalls`, `batches`, `setPass`, `trianglesK` (thousands), `shadowCasters`.

Switches (bridge `set`, `-tldset`): `TLDRevamp.FrameBreakdown.FrameTiming` (the three times) and `.Render` (the five
render counters), both on; switching them off made no measurable difference (car park and items scenes, 2026-10-18).

Tick fields: `bound` {`cpu`, `gpu`} frame counts; `frame` {`busyMs`, `mainThreadMs`, `renderThreadMs`, `gpuMs`}
interval averages; `render` {`drawCalls`, `batches`, `setPass`, `trianglesK`, `shadowCasters`} interval averages.
Hitch fields: `gpuBound`, `frame`, `render` of that frame, and `scene` (below). `breakdown.json` adds
`gpuBoundFrames`, `slowGpuBound`, `countersValid` and `perFrame` (each derived value's average over all and slow frames).

**Scene counts on slow frames**: a hitch line carries `scene`: `rigidbodies`, `rigidbodiesDynamic` (not kinematic),
`rigidbodiesAwake`, `renderers`, `renderersVisible` (`Renderer.isVisible`: needed by a camera, shadows included),
`lights` (enabled), `shadowLights`, `particleSystems`, `particleSystemsPlaying`, `cars` (`mainscript.Cars`),
`carCopies` (other players' cars shown here), `scanMs` (the scan's own cost), `sampleSt`, `ageS`. One scan of the
scene (`FindObjectsByType`, unsorted) runs at most every 30 s (`SceneCounts.MinIntervalS`), triggered by a hitch;
hitches in between repeat the last counts with their age. Measured: 2.3 ms for 4,700 renderers / 150 bodies; it
allocates the arrays it walks. Bridge `scenecount` runs one now.

**Script timers** (off unless switched on; config `[Telemetry]`, or bridge `timers`):

    [Telemetry]
    ScriptTimers = false          ; true: time the list below from startup (also the -tldtimers launch flag)
    ScriptTimerMethods = default  ; Type.Method names, "default" (the built-in hot list), "all", "coroutines",
                                  ; "callees:Type.Method" (every method of the game's assembly that one calls directly)
    ScriptTimerAlloc = false      ; also estimate allocations per method (see below)
    ScriptTimerColumns = 16       ; how many of the list (in order) get a black-box column (0-32)
    ScriptTimerTop = 10           ; methods per tick line

**Coarse timers** are separate and on by default (`[Telemetry] CoarseTimers = true`; bridge `timers coarse on|off`).
Eight fixed groups use the same closed-generic Harmony probes: `carscript.Update`, `mapSettingScript.Update`,
`fpscontroller.Update`, `aiscript.Update`, `Net.Mp.Tick`, `Entities.FixedTick`, `Entities.LateSample`,
`TinyRendererCull.Tick`. They have `coarse:<method>` black-box columns even when detailed timers are off. Times are
inclusive and nested groups overlap. There is no allocation tracking, sorting, dictionary lookup or player-loop
instrumentation. Only the first **128 method invocations per frame** read timestamps; `coarse:truncated = 1` means
the values are lower bounds; -1 means coarse timing is off. Idle groups reset to zero each frame. Detailed `script:` columns remain opt-in.

Offline Release measurement (2026-10-19, .NET 9 copy of `TelemetryTest`, 100,000 warmed frames): **0.0042-0.0044 ms/frame,
0 allocated bytes**, for 128 coarse probes, rolling eight groups, copying the nine new columns, one event append
and the frame/event rings combined. This excludes Harmony dispatch, Unity sampling and Mono. The earlier in-game
probe calibration below was 48 ns/call: 128 calls would add about 0.0061 ms, before the small frame roll-up, to the
measured 0.014–0.016 ms baseline. That estimate fits 0.05 ms, but the **total in-game budget is not re-verified**:
Codex cannot run the game here. Method probes execute outside `selfMs`; use `timers calibrate` and an on/off A/B to
include their cost. If the reviewer measures telemetry at or above 0.05 ms/frame, keep `CoarseTimers = false` for
that build. Capture/JSON costs occur at marks, not in the steady per-frame path; `state.captureMs` reports capture.
The nine fixed coarse columns add 5.625 MiB across the live frame ring and its four capture buffers.

`default` is the game's hottest scripts in the profiled scenes (car, wheel, item, AI, world generation, player) and
RevaMP's own per-frame work (`Runner.Update/FixedUpdate/LateUpdate/OnGUI`, `Net.Mp.Tick`, `Net.Entities.FixedTick`,
`Net.Entities.LateSample`, `TinyRendererCull.Tick`); `ScriptProfiler.DefaultTimers` holds the list. `all` is every
Update/LateUpdate/FixedUpdate/OnGUI and physics callback (OnCollision*/OnTrigger*) declared in the game.
Each timed method gets a Harmony prefix (first of all patches) and postfix (last), so a fix's own prefix, including
one that skips the original, counts in that method's time. Times are inclusive (a timed method called by another
counts in both), main thread only. The same probes serve the on-demand profiler (`profile start [alloc]
[Type.Method ...|all|coroutines|callees:Type.Method]` / `profile stop [n]`), which now costs the same per call.

With the timers on, the player-loop phases run too (`PlayerLoopProfiler`: a timestamp between every PlayerLoop
subsystem, i.e. physics, script updates, animation, rendering, audio, the wait for present).

- Tick: `scripts` {`frames`, `totalMsPerFrame`, `top`: [{`m`, `msPerFrame`, `callsPerFrame`, `maxMs`, `kbPerS` with
  alloc}]} and `loop` {`frames`, `top`: [{`p`, `msPerFrame`}]} (the 12 costliest phases).
- Hitch: `scripts` {method: ms} (that frame's five slowest timed methods) and `loop` {phase: ms} (its three costliest).
- Black box: one column per timed method, `script:<Type.Method>` (ms in that frame), after the derived columns.
- Head: `telemetry` {`scriptTimers`, `timerAlloc`, `timerColumns`, `allocCounter`}.

Bridge `timers on [alloc] [Type.Method ...|all|default] | off | show [n] | calibrate`; `il Type.Method` prints a
method's original IL (for writing exact transpilers).

**Allocations per method**: this Mono (Boehm GC) has no per-thread allocation counter. `GC.GetAllocatedBytesForCurrentThread`
returns 0 (checked live: an 8 KB array doesn't move it), the player has no `GC Allocated In Frame` counter, and
Mono's allocation profiler (`mono_profiler_enable_allocations`) can only be enabled before the runtime starts. With
`ScriptTimerAlloc` (or `profile start alloc ...`) each probe reads `GC.GetTotalMemory` around the call instead:
process-wide heap growth while the method ran. It moves in heap-block steps, includes worker threads' allocations and
takes the GC lock twice per call: an estimate for ranking main-thread allocators, averaged over many calls. The same
finding means telemetry's own `selfAllocBytesPerFrame` (tick) and `allocBytesPerFrame` (bridge `telemetry`) were
always 0 here; they are now `null` when the runtime has no such counter (`allocCounter: false`).

**Telemetry's own cost** (2026-10-18, RevaMP on the beta, PerfA test copy, bridge):

| what | measured |
|---|---|
| `selfMs`, everything telemetry does per frame on the main thread (timers off) | 0.014-0.016 ms/frame |
| per-frame sampling path alone (`telemetrytest`: FrameBreakdown with the new counters, columns, black-box ring; 3 × 20,000 frames) | 0.0006 ms/frame, 0 bytes heap growth |
| one timed call (`timers calibrate`: empty method, 200,000 calls, probed − unprobed) | 48 ns (55 ns with the allocation estimate) |
| timers on, `default` list, idle | about 1,000 timed calls a frame: ~0.05 ms/frame |
| timers on, `all` + `coroutines` (289 methods), idle | p50 frame 5.55 ms before and after switching on |

Bridge `telemetrytest [n]` repeats the sampling-path measurement (best of three runs, heap growth over the whole loop,
since there is no per-thread counter). Memory: the black box keeps 34 values a frame (15 phases + 10 derived + 9 coarse) in a
ring of 32,768 frames plus four capture buffers; each script column adds 0.64 MB.

## Run vs run

`tools/telemetry_compare.py --runs <runA>[,<repeat>...] <runB>[,...] [--names A B] [-o out_dir]` compares two or more
runs, e.g. the base game vs RevaMP, or a fix off vs on. A run is a folder (or files) with a TLDBench result
(`TLDBench-*.json`) and/or RevaMP play logs (`*.jsonl`); repeats of one configuration are joined with commas and
averaged (min/max kept). TLDBench 1.2.0 stamps each scenario with `realtimeStart`/`realtimeEnd`
(Unity's `realtimeSinceStartup`), the clock of a solo play log's `st`, so every scenario gets the play-log ticks
whose 5 s interval lies at least half inside it. Without a TLDBench result the whole play log is one scenario,
`all`. Writes `runs.html` (tables, light/dark) and `runs.json`, and prints a text summary. Per scenario: TLDBench's
FPS, frame-time percentiles, frames over 33/50 ms, allocation and GC rate; the ticks' busy/thread/GPU times,
GPU-bound share, render counters, heap growth, telemetry and timed-script cost; the engine phases; scripts by largest
change and by cost (ms/frame, calls/frame, heap growth); fix counters. Percentages are against the first run. Script
rows come from each tick's top list (a method missing from a tick counts as 0 there), so raise `ScriptTimerTop` for
complete rankings.

Launch flag for A/B runs: `-tldset Path=Value[,Path=Value...]` sets runtime switches before the first frame, like
bridge `set` (only where the bridge is on), e.g. `-tldbench fixoff -tldset TLDRevamp.Fixes.KeyNameCache.Enabled=False`.

## Game-side format and beta details (protocol 30)

JSON numbers always use a decimal point, regardless of the player's locale. Non-finite measurements serialize as
`null`. Files with colliding timestamps receive a numeric suffix. `sid` is 16 lowercase hexadecimal digits from
eight random bytes, carried in Welcome along with an initial host clock estimate and the stable UTC session date. The existing round-trip clock
messages refine that estimate during loading and play, even with the mod-object API disabled. The initial Welcome
estimate does not yet compensate for one-way network delay. `ReportAsk` also carries `momentSt`, so a synced report
uses the host's original moment rather than its arrival time.

`head.settings` uses `width`, `height`, `fullscreen`, `quality`, `vsync`, `targetFps`, optional `viewDistance`, and
`revamp` (the numeric values of RevaMP's settings). `head.hw` uses `cpu`, `cores`, `ramMB`, `gpu`, `vramMB`, `os`.
The tick's frame percentiles use a fresh interval histogram with 0.1 ms resolution up to 1000 ms (the overflow
bucket reports the interval maximum). `maxms` is the actual maximum. Hitches use a rolling one-second limit.
`selfMs` and bridge `telemetry` time the existing frame/GC/FrameBreakdown sampler plus the play recorder, interval
summaries, line flushes and capture snapshots with Stopwatch; their allocation count uses
`GC.GetAllocatedBytesForCurrentThread`, which returns 0 in the beta's Mono (reported as `null` there; bridge
`telemetrytest` measures the sampling path's heap growth instead). The current tick's own serialization cost is charged to the next interval.
The bridge average restarts when the world/session changes. Background serialization/compression and the few
integer assignments in Harmony hooks are outside this main-thread sampler measurement. A live Unity/Mono run is
needed to validate the 0.05 ms average target; the offline ring benchmark is not a whole-game measurement.

The beta's backlog sources are `terrainGenScript.coordsNeeded` minus placed `terrains`, needed road chunks whose
`roadGenScript.dotChunkClass.Done()` is false (including missing chunks), active `poiGenScript.chunkClass.poiToPlace`
candidates in needed POI chunks, and needed scenery chunks whose `objGenScript.chunkClass.generated` is false.
`gen` keys are `terrain`, `roads`, `buildings`, `buildingsDeferred`, `scenery`, `items`, plus `sampleAgeS`.
These lists are sampled at ticks and admitted
hitches. The black box reuses the latest sample
between those occasions: it does not scan all generation lists every frame. This is a deliberate cost tradeoff.
`items` remains `-1`: this beta exposes no persistent pending streaming queue; `itemPlaceRemoveScript.addFromFar`
is scratch storage that stays populated after placement and would falsely report completed work as pending.
`genDone` uses `terrain`, `roads`, `scenery`, `buildings`; roads counts completed roads, including each road of a
parallel batch, rather than batches. Completion callbacks also cover the replacement generation paths in fixes.

`Fixes.PoiBacklog.Enabled` (bridge `set TLDRevamp.Fixes.PoiBacklog.Enabled False`) restores the old raw POI count
in `buildings` and the black-box total. Bridge `genbacklog` samples immediately and returns `candidates`, `ready`,
`waitingForRoads`, `deferred`, and counters `samples` / `deferredObserved` (sum of deferred observations, not unique
buildings). The first four are gauges, with `candidates = ready + waitingForRoads + deferred`. A candidate is deferred
only if one of `RoadsDoneAtPlace`'s four dependencies is unfinished and outside `roadgen.chunksNeeded`. Candidates
whose unfinished dependencies are all inside that set remain pending, including missing chunks. Completed
dependencies outside it do not defer placement. The
`buildingsDeferred` diagnostic remains available with the switch off. Neither mode changes generation or leases.
This is a local telemetry change; Protocol.Version is unchanged.

The October 18 internet test's 1,454 client ticks all had `roads = 0`; `buildings` stayed 24, 27 or 28. The host's
view distance was 15 km and the client's 5 km. In the later format-2 run, **both roles** had a settled `gen = 29`
at 5 km. The fixed 3×3 POI candidate area and draw-distance-based
road area explain why the old raw queue length did not drain. Historical captures have no dependency snapshots,
so the exact partition of those old candidates still needs the in-game A/B check; the old total alone is not
evidence of missing client buildings.

Lease validation uses the largest available POI generator chunk size: reach is
`chunkSize * (1.5 * sqrt(2) + 1)` metres, covering the fixed 3x3 POI candidate area
plus a one-chunk margin. Without a positive, finite chunk size it uses 5 km.
`leasetable` and `mp entities` expose cumulative `leaseRefusedWhy` counts by reason
(including malformed requests and invalid completion messages); `mp entities`
telemetry retains these numeric counts. Counts clear with the other entity counters.

`counts.items` uses `savedatascript.s.items.Count`; `carsNearby` counts entries in `mainscript.Cars` within 1 km;
`awakeRigidbodies` counts non-kinematic, non-sleeping rigidbodies registered by saved items. This last count covers
registered items rather than every body in the scene: no cheap complete physics count exists here. The existing
on-demand `scene` report/bridge scan is cached for 60 seconds and reports `scanMs` and `sampleSt` separately.
`mp` keeps anonymous numeric entity counters (`remotePlayers`, `statesIn`, `statesSent`, `stale`, `spawned`,
`ownerChanges`) instead of the old free-text connection event/rejection fields, which can contain identities.
`net`, `ai`, `combat` and `handoffs` retain their existing numeric summaries.

Load records use the beta's actual action hooks: `menuhandler.PressedStart`, `PressedNewStart`, `PressedRestart`,
`AutoContinue`, and `newSaveScreenScript.Load` for an existing save. This beta has no `mainmenuscript` type.
The later `menuhandler.Load` call during initialization does not restart the timer. Join timing starts before
`ConnectIP`/`ConnectP2P`. A missed action produces no load duration: world initialization and file opening are
never substituted for the player's action.

`firstUsableS` is observed after the local player's Update, once `currentMainMap.firstRun`, `loadedmove`, an active
non-kinematic player body, the game's cursor/input gate, positive time scale, and (for clients) Ready permit play.
The saved-game load flag must be clear and the pause menu closed. `stage` is `firstUsable`, or a later `firstState`.
`startAreaReadyS` is omitted: zero sampled backlogs can occur before generators initialize, needed POI chunks can
be absent, and this beta has no persistent streaming queue or complete starting-area readiness signal.
Join stage keys are `connectS` (transport connected), `worldDataS` (Welcome received), `firstStateS` (first accepted
remote player/entity state); unobserved join stages remain null.

The beta captures show `GC.Collect` work without corresponding `GC.CollectionCount` advances. Runtime counters
count completed collections and do not reliably describe each profiler frame (including incremental GC work);
opening a log also resets their baseline. Per-frame counter deltas are retained, but when all three are zero
and the completed frame's `GC.Collect` marker is positive, `gc0` is set to one as a GC-work flag, without claiming
a known generation. Ticks sum those same normalized per-frame values, and FrameBreakdown uses the same marker
evidence. These fields are GC evidence rather than exact generation collection totals on Mono. The comparison
tool recognizes the marker too, so older captures with zero counters are still flagged.

`settings.viewDistance` uses the beta settings menu's `S.GetDrawDistance()` (its slider is stored in
`S.FDrawDistance`), only when positive and finite. The observed zero is not a usable distance and is omitted;
telemetry does not substitute a camera clipping plane for a setting.

Save hooks surround `newSaveScreenScript.Save`, with `AutoSave` and dedicated/session-end save scopes identifying
automatic saves. For binary/JSON asynchronous data writes they observe the returned `pathscript.SaveBinaryAsync`
or `SaveJSONAsync` task, without replacing the game's task or save behavior. `ms` includes task completion and the
save entry point's work; `bytes` is the data file length when readable. Properties and thumbnail files are not
included in that byte count. Save, rebase, streaming and generation hooks are installed independently of fix
switches. Rebase/stream/completion events are attributed by Unity frame number to the completed frame, rather than
whatever script happens to update first.
Rebase flags now require an actual change to `visszarakva`. Older captures (including the October 18 playtest and
statecheck format-2 captures) flagged every periodic `VisszaRakas` call, even its no-shift branch; that flag alone
cannot attribute a hitch to a floating-origin shift. `RebaseLab.ActualShiftFlags = false` restores the old diagnostic
for comparison. `RebaseLab.Checks` and `.Shifts` distinguish the two; `rebaseprof stop` retains the last **actual**
shift's timing instead of overwriting it at the next no-op check. Use `PerObject = false` for performance runs
(the per-object profiler allocates regex strings), and `TimeSync = false` to avoid adding a forced physics sync.
Particle scan/pause maxima (`RebaseParticles.ScanMaxUs`, `.PauseMaxUs`) and client packet work maxima
(`StreamReceiveBudget.MaxMessageUs`) are microseconds; a single indivisible packet/group or placement may exceed
its budget. `StreamReceiveBudget.Deferred` counts exhausted polling opportunities, not a measured queue length.


The fixed ring holds 32,768 frames, with 40 seconds of internal retention to cover 30 seconds before, 5 seconds
after and a finishing-frame margin. `[momentSt - 30, momentSt + 5]` is exported (selected using the local
monotonic clock, so offset corrections cannot evict history), including a frame that crosses the ending boundary:
a freeze longer than five seconds must not disappear merely because its ending timestamp is later. Extremely
high frame rates can exhaust capacity;
`overwritten` records that loss. Early captures naturally have less than 30 seconds of history. Leaving a world
finishes pending captures with `head.complete=false`; ordinary captures have `true`. Four snapshot buffers are
allocated at startup and reused; at most four captures can be pending/writing.
At completion the main thread copies the live rows and phase arrays in at most two contiguous segments each,
without per-row filtering or snapshot allocation. The worker filters the requested window, serializes and writes
JSON; marker ZIP compression also runs on workers. A temporary file is
renamed after writing, so the report zipper does not copy a partially written black box.
Report creation copies numeric breakdown/fix data and warning entries on the main thread, then formats and writes
those attachments, copies the current privacy-safe play log and compresses the report on workers. Tick polls completion without waiting on
Unity's thread; upload callbacks and UI updates stay on that thread. `stats.json.telemetrySnapshotMs` measures
copying the new attachment data. Bridge stats and scene scan still require
the main thread (`scene.scanMs` measures the latter). In the first real report that scan alone cost 15.823 ms;
the 77 ms capture frame cannot be attributed entirely to a single attachment from that recording.

Black-box columns, in order, are:

```
st, ms,
PlayerLoop, BehaviourUpdate, LateBehaviourUpdate, FixedBehaviourUpdate,
Physics.Simulate, Physics.Processing, Physics.BakeCollisionMeshes,
Camera.Render, RenderPipelineManager.DoRenderLoop_Internal,
Gfx.WaitForPresentOnGfxThread, Gfx.PresentFrame, Mesh.UploadMeshData,
GC.Collect, Loading.UpdatePreloading, Main Thread,
gc0, gc1, gc2, rebase, stream, saving, loading, gen, bytesSent, bytesReceived, speedKmh,
busyMs, mainThreadMs, renderThreadMs, gpuMs, gpuBound, drawCalls, batches, setPass, trianglesK, shadowCasters,
[script:<Type.Method> ... with the script timers on]
```

Phase values and `ms` are milliseconds; flags are numeric 0/1; `gen` is the sum of known sampled backlog fields,
or `-1` when all are unknown. Byte counters are per-frame deltas, never cumulative totals. Missing profiler
markers have zero values. Ping is available in the surrounding play log's `net.conns[].ping`.

`warn.warnings` is the top-ten array. `errors.json` contains `warnings`, `errors`, `exceptions`, `droppedKinds`
and `kinds` (the full array since process start, independent of bridge `errors reset`). Arbitrary messages and stack lines are removed rather than heuristically anonymized. Marker/report notes
receive privacy cleaning; no other free-form user text is exported. `fixes.json` is a flat
object of public static int/long fields in every fix, including numeric tuning fields and gauges. Field discovery
runs once at startup; the cached fields are read for five-second deltas and on report creation.

Message 92: client-to-host `str note`; host-to-client `str note, f64 st, i32 origin, u8 self`
(`origin` is the originating session player index; `self` is 1 for that recipient). Message 104 in either
direction starts with `str sid, f64 momentSt, i32 origin, str note` (reliable); host-to-client relays
append `i32 renamer`, authenticated by the host. Chat commands also arrive through
the existing chat message. Server-side limits use the authenticated account when available and connection-local
identity otherwise; only anonymous session indices enter the marker broadcast. Solo chat accepts `/mark` too. Protocol 33
shares the completed black boxes as GZip JSON via Reports (61/95, kind 1); this replaces the old protocol-30
host-only ZIP wrapper. Consent, relay metadata, quotas, output names and queues follow the transfer section
above. Local captures remain available if sharing is refused or delayed. A report already gathering aftermath
is reused rather than overwritten by repeated report requests.

Bridge `telemetry` returns sampler `selfMs`, `maxMs`, `allocBytesPerFrame`, `frames`, `blackboxFrames`,
`overwritten`, `pending`. `mark <note>` follows the normal marker route. `blackbox` schedules a standalone local
capture in `marks/` and returns `path`, `readyAfterS` and the current frame count, without broadcasting a marker.
All writes are fail-safe; telemetry logs its first failure and suppresses repeats. A failed play-log writer is
disabled until the next world/session rather than retrying disk access every frame.

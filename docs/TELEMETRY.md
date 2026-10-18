# Telemetry

What TLD RevaMP records about how the game runs, so problems can be found and compared between players. Everything
stays on the player's own PC, in `BepInEx/tldrevamp-feedback/`. Nothing is uploaded. Data only leaves a PC inside a
report the player sends themselves (Page Down), or, for players who turned on *Settings → Revamp → Debugging → Send my
reports to the host*, as a copy of their report to the host when a report is made. No new identifying data: no Steam
names or IDs in telemetry files (the report's logs may contain them, as before).

## Clock

Every record carries `st`: **session time** in seconds (double, 3 decimals). In a multiplayer session it is the
host's clock as estimated on each machine (`Net.ModObjects.SessionTime`, round-trip corrected), so the same `st` is
the same moment on every machine (to a few ms). Outside a session `st` is this machine's
`Time.realtimeSinceStartupAsDouble`. Each file's header also gives `stSource`: `"host"` or `"local"`, and the session id
(`sid`, a random 64-bit hex string the host makes when it starts hosting and sends to joiners) so files from one
session can be grouped. Records also carry `t`: seconds since this file was opened.

## Play log (single player and multiplayer)

`BepInEx/tldrevamp-feedback/sessions/<yyyyMMdd-HHmmss>-<role>.jsonl`, `role` = `solo`, `host`, `client`, `dedicated`.
A new file when a world is entered (solo) or a session starts; closed on leaving. Newest 20 kept. One JSON object per
line, flushed per line. Kinds (`k`):

- `"head"`: first line: `start` (ISO time), `mod`, `build`, `game`, `role`, `seed`, `sid` (or null), `stSource`,
  `settings` (resolution, fullscreen, quality name, vsync, target fps, the game's view distance if available, RevaMP's
  settings that affect performance), `hw` (CPU name, cores, RAM MB, GPU name, VRAM MB, OS).
- `"tick"`: every 5 s: frame `p50ms`, `p95ms`, `p99ms`, `maxms` over the interval (not a 10 s ring), `frames`,
  `hitches50` in the interval, `gc0`/`gc1`/`gc2` GC evidence summed over the interval (see below), `allocKBps` (estimated managed allocation
  rate: sum of positive managed-heap deltas per frame / seconds; a lower bound), `managedMB`, `unityAllocMB`,
  `unityReservedMB`, `pos` [x, z] in global metres (rounded to 10 m), `speedKmh` (player or their car), `driving`
  (bool), `gen` (world generation backlog, see below), `counts` (cheap counters: tracked items, cars nearby, awake
  rigidbodies — sampled, see below), `net` (multiplayer only: as today), `mp`, `ai`, `combat`, `handoffs` (as today),
  `fixes` (counter deltas of every fix that has counters, only the non-zero ones, e.g. {"FrozenFlyers.Skipped": 120}).
  `selfMs` is telemetry's measured main-thread milliseconds per frame over the interval;
  `selfAllocBytesPerFrame` includes the amortized allocations from periodic records.
- `"hitch"`: one per frame over 50 ms (at most 2 per second; further ones counted in the next tick's `hitchesDropped`):
  `ms`, `phases` (that frame's FrameBreakdown phases in ms, non-zero only), `gc` (gen0/1/2 GC evidence that frame),
  `rebase` (bool: the floating-origin shift ran this frame), `saving` (bool), `loading` (bool), `stream` (the game's
  item streaming pass ran: bool), `gen` (backlog at that frame), `genDone` (terrain tiles / road batches / scenery
  chunks / buildings finished this frame), `speedKmh`.
- `"load"`: world load milestones from pressing Start/Load or joining: `kind` (new, load, join), `firstUsableS`,
  and for a join the client's stages (connect, world data, first state) when known.
- `"save"`: each save: `ms` (duration of the game's save), `auto` (bool), `bytes` if known.
- `"mark"`: a marker: `note`, `by` (player name is NOT stored: `by` is `"me"`, `"host"` or `"player"`), `st`.
- `"warn"`: every 60 s, the top 10 log warnings/errors of the interval by count: `[{"kind": "warning"|"error"|
  "exception", "msg": first 160 chars, "at": first stack line or "", "n": count}]`. Full counts since start are in the
  report.

`gen` (cheap fields of the game's world generators, without RevaMP-only state where possible): pending terrain tiles
around generation centres, road chunks not yet generated within the needed area, buildings (POIs) waiting to be
placed, scenery chunks waiting, items waiting in the streaming queue — each an int; `-1` when unknown.
`counts` is sampled at most every 5 s and must not walk the whole scene every time: use the game's own lists
(`savedatascript.s.items`, the car list RevaMP keeps, the physics count if available); a full FindObjectsOfType walk
is allowed at most once per 60 s and must be measured (its own cost logged).

## Black box

Every machine keeps the last **30 s** of per-frame data in a fixed ring (allocation-free): `st`, frame ms, the
FrameBreakdown phases (ms), GC collections, rebase/stream/save flags, generation backlog total, bytes sent/received
this frame (multiplayer), speed. When a report is made (Page Down, the host's synced ask, or `/mark`), the ring is
written as `blackbox.json` into the report (and for `/mark` into
`BepInEx/tldrevamp-feedback/marks/<yyyyMMdd-HHmmss>-<note>.json`), continuing for **5 s after** the moment so the
aftermath is included: `{"head": {...as the play log head..., "momentSt": st}, "cols": ["st","ms",...], "rows": [[...],...]}`
(columns + rows: compact).

## Markers: /mark

In chat: `/mark <note>` (note optional, max 60 chars, cleaned like chat text). In a session it goes to the host, which
stamps it with session time and sends it to every player (protocol: one new message; bump `Protocol.Version`);
every machine writes a `mark` line into its play log and saves its black box under `marks/` (players who share
reports also send that black box to the host, like a report: reuse Net.Reports, folder `players/`). Limited: one
`/mark` per player per 10 s. Solo: written locally. Also on the bridge (`mark <note>`) for tests.

## Reports

The report zip (FeedbackReporter) additionally contains: `blackbox.json`, the current play log, `breakdown.json`
(FrameBreakdown since start, all vs slow frames), `errors.json` (errors and warnings by kind since start, counts) and
`fixes.json` (every fix counter since start).

## Comparison page

`tools/telemetry_compare.py` (Python 3.9+, standard library only):

    python tools/telemetry_compare.py <folder or zips...> [-o out_dir]

Input: report zips (from `players/` on the host plus the host's own) and/or `marks/*.json` and play logs. Groups
everything by `sid` (or one local machine/session identity) and moment (`momentSt`, within 2 s), and writes one self-contained HTML page per moment
(inline SVG, no internet): one row per machine (role, hardware, settings), frame times of every machine on one
shared session-time axis (the 30 s before + 5 s after), phases stacked for slow frames, GC/rebase/stream/save marks,
generation backlog, network bytes and ping per machine, and a **differences** section that states, in plain words,
what differs between machines at the moment (e.g. "Only `client 2` had frames over 50 ms: 3 of them, all with a
garbage collection and 40 terrain tiles waiting"). Player names from the reports' file names may be shown (they are
the host's own files). Also prints a short text summary.

A solo/local-clock file is a consistent timeline for its own machine; `sid:null` does not discard session seconds.
Role/start/hardware headers associate its loose marks, log and report attachments. Two marks of one moment are
one machine: one complete snapshot is selected, including every row even when millisecond timestamps repeat.
Play-log hitch lines are deduplicated against those frames and against copies inside reports. Each page reports
its moment's 30+5 second window, while stdout also counts distinct hitch records across the whole input logs.
Missing timestamps use file order with a `session time unavailable` notice. `timeline not synced` is reserved for
multi-machine comparisons without a common host clock.

## Game-side format and beta details (protocol 30)

JSON numbers always use a decimal point, regardless of the player's locale. Non-finite measurements serialize as
`null`. Files with colliding timestamps receive a numeric suffix. `sid` is 16 lowercase hexadecimal digits from
eight random bytes, carried in Welcome along with an initial host clock estimate. The existing round-trip clock
messages refine that estimate during loading and play, even with the mod-object API disabled. The initial Welcome
estimate does not yet compensate for one-way network delay. `ReportAsk` also carries `momentSt`, so a synced report
uses the host's original moment rather than its arrival time.

`head.settings` uses `width`, `height`, `fullscreen`, `quality`, `vsync`, `targetFps`, optional `viewDistance`, and
`revamp` (the numeric values of RevaMP's settings). `head.hw` uses `cpu`, `cores`, `ramMB`, `gpu`, `vramMB`, `os`.
The tick's frame percentiles use a fresh interval histogram with 0.1 ms resolution up to 1000 ms (the overflow
bucket reports the interval maximum). `maxms` is the actual maximum. Hitches use a rolling one-second limit.
`selfMs` and bridge `telemetry` time the existing frame/GC/FrameBreakdown sampler plus the play recorder, interval
summaries, line flushes and capture snapshots with Stopwatch; their allocation count uses
`GC.GetAllocatedBytesForCurrentThread`. The current tick's own serialization cost is charged to the next interval.
The bridge average restarts when the world/session changes. Background serialization/compression and the few
integer assignments in Harmony hooks are outside this main-thread sampler measurement. A live Unity/Mono run is
needed to validate the 0.05 ms average target; the offline ring benchmark is not a whole-game measurement.

The beta's backlog sources are `terrainGenScript.coordsNeeded` minus placed `terrains`, needed road chunks whose
`roadGenScript.dotChunkClass.Done()` is false (including missing chunks), `poiGenScript.chunkClass.poiToPlace` in
needed chunks, and needed scenery chunks whose `objGenScript.chunkClass.generated` is false. `gen` keys are
`terrain`, `roads`, `buildings`, `scenery`, `items`, plus `sampleAgeS`. These lists are sampled at ticks and admitted
hitches. The black box reuses the latest sample
between those occasions: it does not scan all generation lists every frame. This is a deliberate cost tradeoff.
`items` remains `-1`: this beta exposes no persistent pending streaming queue; `itemPlaceRemoveScript.addFromFar`
is scratch storage that stays populated after placement and would falsely report completed work as pending.
`genDone` uses `terrain`, `roads`, `scenery`, `buildings`; roads counts completed roads, including each road of a
parallel batch, rather than batches. Completion callbacks also cover the replacement generation paths in fixes.

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
those attachments, copies logs and compresses the report on workers. Tick polls completion without waiting on
Unity's thread; upload callbacks and UI updates stay on that thread. `stats.json.telemetrySnapshotMs` measures
copying the new attachment data. The pre-existing Unity screenshot, bridge stats and scene scan still require
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
gc0, gc1, gc2, rebase, stream, saving, loading, gen, bytesSent, bytesReceived, speedKmh
```

Phase values and `ms` are milliseconds; flags are numeric 0/1; `gen` is the sum of known sampled backlog fields,
or `-1` when all are unknown. Byte counters are per-frame deltas, never cumulative totals. Missing profiler
markers have zero values. Ping is available in the surrounding play log's `net.conns[].ping`.

`warn.warnings` is the top-ten array. `errors.json` contains `warnings`, `errors`, `exceptions`, `droppedKinds`
and `kinds` (the full array since process start, independent of bridge `errors reset`). Messages and stack lines
are redacted for known player names, account identifiers, network addresses and personal paths before being
truncated to 160 characters. Marker notes receive the same redaction after chat cleaning. Identifying text a user
explicitly types that is not recognizable as such cannot be inferred by the recorder. `fixes.json` is a flat
object of public static int/long fields in every fix, including numeric tuning fields and gauges. Field discovery
runs once at startup; the cached fields are read for five-second deltas and on report creation.

Message 92 is the one new marker message: client-to-host `str note`; host-to-client `str note, f64 st, u8 origin,
u8 self` (`origin` 0 host / 1 player; `self` 1 for the originating recipient). Chat commands also arrive through
the existing chat message. Server-side limits use the authenticated account when available and connection-local
identity otherwise; no identity is written into a marker. Solo chat accepts `/mark` too. Shared markers are ZIPs
containing `blackbox.json`, sent through ReportChunk; protocol 30 adds a mark byte after its offset. They retain
the existing transfer size, per-session count and players-folder disk limits, with a ten-second marker start
limit. Uploads queue behind active transfers (up to eight waiting; local captures are kept if sharing is delayed
or the queue is full), with conservative pacing. A report already gathering aftermath is reused rather than
overwritten by repeated report requests.

Bridge `telemetry` returns sampler `selfMs`, `maxMs`, `allocBytesPerFrame`, `frames`, `blackboxFrames`,
`overwritten`, `pending`. `mark <note>` follows the normal marker route. `blackbox` schedules a standalone local
capture in `marks/` and returns `path`, `readyAfterS` and the current frame count, without broadcasting a marker.
All writes are fail-safe; telemetry logs its first failure and suppresses repeats. A failed play-log writer is
disabled until the next world/session rather than retrying disk access every frame.

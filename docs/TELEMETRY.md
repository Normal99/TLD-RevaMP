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

### State and network history (black-box format 2)

`head.format` is now `2`; files without it are format 1. The comparer accepts both. No multiplayer wire change is
needed: the existing synchronized mark already supplies `momentSt` on every machine.

Each capture also freezes a `state` section **on the main thread when the mark/report arrives**, before waiting for
the five-second aftermath. `state.st` is the actual capture time; `head.momentSt` remains the host's original mark.
The comparer calls out capture skew above 50 ms. Capture reads existing objects and records without resolving,
spawning or repairing them. Four preallocated state/event buffers follow the existing four frame capture buffers;
the worker owns a buffer until it finishes writing the same black-box file.

- Scope: shared entities within **300 m** of the local player, plus every car named by any player's seat. Seated
  cars are captured first, then other nearby cars, then nearby items. With no local player, only seated cars are
  included. Limits: **512 entities, 2,048 slot entries, 128 players, 256 seats, 64 storms, 128 radio stations** and
  a conservative **512 KiB serialized-state budget**, whichever fills first. Strings are privacy-cleaned and
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
- `players`: session player indices, privacy-cleaned names, known positions and seat `(net,seat)`; `seats` retains
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
resync send/apply counts (no payloads), seat enter/leave, join/leave and accepted TPA. Live-usables use a fixed
512-cell sampling table: at most one record per hashed object/item/usable/direction every 500 ms; collisions can
sample more sparsely. Hooks append value records without allocating, formatting strings or serializing payloads.
Existing rejection/owner reason strings are reused; worker output privacy-cleans and limits them to 64 characters.

State differences appear first in each moment's HTML and stdout. The comparer joins by net id and checks ownership,
epoch, inventory/parent state, slots/conditions, fuel (>0.1 L), engine/ignition/key, seats, tuning (>0.05), broadcast
clips/playheads (>2 s), time of day (>2 game seconds, wrapping midnight), cloud/exposure (>0.1). Positions use a
**1 m** residual tolerance after a constant-velocity correction in the same sender clock (at most 1 s); larger
clock gaps are reported as stale/uncertain. Without that clock it adds speed times 150 ms plus capture skew to the
tolerance. Acceleration, correction blending and handovers can still require human interpretation. Object absence
is called missing only within the other machine's coverage and without truncation/failure; absent slot data is
unknown, never treated as an empty slot. Related events from **10 s before to 5 s after** the mark are merged by
`st`, from both machines. Stdout prints the first 40 related events; HTML retains all matching records.

Privacy remains the existing policy: player names go through `TelemetryPrivacy` and appear as `[player]`, while
numeric session player indices preserve relationships. No account identifiers, file paths, custom music paths,
hardware serials, packet payloads, chat or save records are added. Existing hardware descriptions in the header
remain unchanged. Buffers and worker output stay inside the existing local report/mark and sharing flow.

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
`GC.GetAllocatedBytesForCurrentThread`, which returns 0 in the beta's Mono (reported as `null` there; bridge
`telemetrytest` measures the sampling path's heap growth instead). The current tick's own serialization cost is charged to the next interval.
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
gc0, gc1, gc2, rebase, stream, saving, loading, gen, bytesSent, bytesReceived, speedKmh,
busyMs, mainThreadMs, renderThreadMs, gpuMs, gpuBound, drawCalls, batches, setPass, trianglesK, shadowCasters,
[script:<Type.Method> ... with the script timers on]
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

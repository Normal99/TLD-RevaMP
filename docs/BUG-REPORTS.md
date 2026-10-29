# Bug reports

A bug report is one local bundle that an agent (or a developer) can read first and replay later.

## Taking one

- **Ctrl+Insert** (either Ctrl; `Keys.ReproRecorder` in `tldrevamp.core.cfg`) opens a note box. Write what went wrong
  (optional, 500 characters) and press **Enter**. Escape cancels.
- Or type **`/bug`** or **`/bug the barrel fell through the floor`** in chat. It is never sent as chat.
- Or Settings → Revamp → **Report a bug**.

Do it right after the bug: the recording is already running, so there is nothing to start beforehand.

## What the player sees

- While the action recorder runs, a banner says **Recording actions · for bug reports**. It starts by itself when a
  multiplayer session starts and ends with the session. Switch it off with *Record actions for bug reports*
  (Settings → Revamp → Debugging); then reports have no actions.
- When the recorder is full (60,000 rows, about 100 minutes) the banner says so; it keeps the first part of the session.
- Taking a report shows **Bug report · saving…**, then **saved** with the file name for twelve seconds, and a toast with
  the folder. A failure says **failed** the same way.

## What is in the bundle

`BepInEx/tldrevamp-feedback/TLDRevamp-bug-<time>.zip`:

| File | What |
|---|---|
| `REPORT.md` | The note, build, commit, wire protocol, role, position, what the recording covers, and how to replay |
| `repro.json` | The actions since the session started (format 1, docs/REPRO-FORMAT.md) |
| `world/start/` | The save the session started from (host only), copied before autosaves can replace it |
| `world/latest-autosave/` | The newest autosave (host or solo; flagged when over an hour old), not what the actions replay from |
| `car.json` | The car the reporter sits in (else the nearest within 15 m) as the physics engine has it at report time: body (kinematic, constraints, mass, drag), every wheel (torque, brake, rpm, mass, grounded), joints, colliders inside the body, the wheel lock and the mod's authority over the car. Take the report while still sitting in the stuck car |
| `car-stuck.json` | The same snapshot, plus the engine's own numbers (rpm, wanted rpm, wheel rpm, clutch, gas, brake output), for the first moment the car stood still with the throttle past half for 1.5 s while the reporter drove it. Present only when that happened since the last report |
| `lock.json` | Why the item under the crosshair (else the one in the hand) is or is not held by a physics-lock container ("some items never physlock"): every storage area within 4 m (did its last two scans see the item, can it store, did it start), the item's flags, its colliders and mount mask, who owns it here and whether it is a copy. Take the report while looking at the item |
| `blackbox.json`, `stats.json`, `errors.json`, `fixes.json`, `breakdown.json`, `sessions/` | The usual report data |
| `tldrevamp-settings.txt` | The player's mod settings |

The usual report zip (`TLDRevamp-report-<time>.zip`) is made next to it and is what *Share my marks and reports* sends;
it is unchanged. The bundle is **never** sent to other players: it is yours to hand over. It holds no names, paths or raw
logs, and the world's player records (`.players`) are left out. It does hold the world itself, so do not post it publicly.
A client's bundle has no world (the host's has it).

## Other players

A bug report takes part in report sharing exactly as Page Down does. With *Share my marks and reports* on, the normal report zip
(`TLDRevamp-report-<time>.zip`, the whitelisted data only) goes to the other sharing players, and when the host reports, the
host asks every sharing player to take theirs: their zips arrive under `players/`. What never travels is the bundle itself
(`repro.json`, the world, `REPORT.md`): each player's stays on their own computer. A client who wants their actions in the
picture presses Ctrl+Insert themselves and sends their own bundle (`--peer-recording` replays it as the second participant).

## Replaying one

```
python tools/bugrepro.py TLDRevamp-bug-<time>.zip --validate
python tools/bugrepro.py TLDRevamp-bug-<time>.zip --install "<a test copy's Saves folder>"
python tools/bugrepro.py TLDRevamp-bug-<time>.zip --drive
```

`--validate` unpacks the zip, prints `REPORT.md` and checks `repro.json` with `tools/replay_repro.py --validate-only`.
`--drive` answers "why did the car not move": for the samples with the throttle down and the car standing still it says how often the engine was off, the clutch applied, the handbrake on, the body frozen or asleep, no driven wheel on the ground, and so on, next to the same numbers while the car moved (needs a recording made after the car state was added; older ones have inputs only).
`--install` copies the starting world into a **disposable test copy** as the save `bugrepro`. Load it there, host it, join with a
second test copy, then run `tools/replay_repro.py` (docs/REPRO-FORMAT.md, *Running a check*). The replay checks that both copies
end in the same state; it is not a deterministic physics replay and does not prove a bug fixed.

## Limits

- Needs a multiplayer session for actions and the starting world (the recorder is part of the multiplayer code): in a solo game the bundle has the report data, the note and the newest autosave only. Host the world, even alone, to get a replayable bundle.
- A world that was never saved has no starting save. Save it first next time, or describe how the world was made.
- Only the reporter's own actions are recorded; a second recording from the other player (`--peer-recording`) is needed when theirs matter.
- Visual problems and feel still need someone looking at the game.

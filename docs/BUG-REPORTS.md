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
| `blackbox.json`, `stats.json`, `errors.json`, `fixes.json`, `breakdown.json`, `sessions/` | The usual report data |
| `tldrevamp-settings.txt` | The player's mod settings |

The usual report zip (`TLDRevamp-report-<time>.zip`) is made next to it and is what *Share my marks and reports* sends;
it is unchanged. The bundle is **never** sent to other players: it is yours to hand over. It holds no names, paths or raw
logs, and the world's player records (`.players`) are left out. It does hold the world itself, so do not post it publicly.
A client's bundle has no world (the host's has it).

## Replaying one

```
python tools/bugrepro.py TLDRevamp-bug-<time>.zip --validate
python tools/bugrepro.py TLDRevamp-bug-<time>.zip --install "<a test copy's Saves folder>"
```

`--validate` unpacks the zip, prints `REPORT.md` and checks `repro.json` with `tools/replay_repro.py --validate-only`.
`--install` copies the starting world into a **disposable test copy** as the save `bugrepro`. Load it there, host it, join with a
second test copy, then run `tools/replay_repro.py` (docs/REPRO-FORMAT.md, *Running a check*). The replay checks that both copies
end in the same state; it is not a deterministic physics replay and does not prove a bug fixed.

## Limits

- Needs a multiplayer session for actions and the starting world (the recorder is part of the multiplayer code): in a solo game the bundle has the report data, the note and the newest autosave only. Host the world, even alone, to get a replayable bundle.
- A world that was never saved has no starting save. Save it first next time, or describe how the world was made.
- Only the reporter's own actions are recorded; a second recording from the other player (`--peer-recording`) is needed when theirs matter.
- Visual problems and feel still need someone looking at the game.

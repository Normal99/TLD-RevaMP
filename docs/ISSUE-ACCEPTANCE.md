# Issue acceptance ledger (open issues #31-#42, except #35)

The acceptance criteria for the open issues of [Normal99/TLD-RevaMP](../README.md), written so that
each one can be called *done* on evidence rather than on intent. Every criterion is measurable, and
every criterion names where its evidence lives.

This file is a ledger, not a status report. Nothing here is marked complete that is not complete in
the working tree.

- The issue bodies are quoted from the GitHub issues on 2026-10-20. `#35` is **excluded on purpose**:
  it wants a website (`tldrevamp.com`) and submission forms, which is a domain, a host and a policy
  decision, not a change in this repository. It is not tracked here and no code in this repository can
  close it.
- `tools/issue_evidence.py` checks this ledger against the working tree and prints a matrix. It only
  reads files: it never calls GitHub, never opens an issue, never labels, never comments and never
  pushes. Refreshing the issue bodies is a manual step (`gh issue view N`).

## How to read the states

Three states are kept apart on purpose, because they carry very different weight.

| State | Meaning |
| --- | --- |
| **committed** | The file is in `HEAD` (the committed `dev` baseline) and unchanged in the working tree. This is what a release contains. |
| **patched** | The file exists in the working tree but is not in `HEAD` (a new, uncommitted file), or it is in `HEAD` and also locally modified. This is an **uncommitted worker snapshot**: real work that nobody has reviewed and that a fresh clone does not have. |
| **missing** | The file is nowhere. The gate still needs work. |
| **open** | A human step: a two-machine run, a headset, a screenshot, or an external account. The validator can report it; it cannot close it. |

The committed baseline for this ledger is `dev` at `d1ebfb7` ("In-game debug tools ... protocol 46").
Everything marked **patched** in this document is an uncommitted snapshot from a parallel work
session. It is *not* part of the dev baseline and must be reviewed, landed and rebuilt before any of
it can be called done.

**Ownership.** `repo` means a contributor can land the work in this repository. `account` means the
step belongs to the account owner (Normal99) on an external service — a NexusMods submission, a
domain, a published README. No code change in this repository can close an `account` gate.

**Command conventions.** Two machines means the two-machine setup in `tools/README.md`: the second
machine is `TLD_SECOND`, and `--relay iad` routes the session through a real Valve relay. Screenshots
and captures go to `bench/`, which is gitignored. Every command below is read-only with respect to
GitHub.

Run the matrix with:

```sh
python3 tools/issue_evidence.py            # the whole matrix
python3 tools/issue_evidence.py --issue 31 # one issue, in detail
python3 tools/issue_evidence.py --json     # machine-readable
python3 tools/issue_evidence.py --run-tests # also runs the offline unit projects
```

---

## #31 Player Ragdoll sync

> Player ragdoll sync where when you die a ragdoll shows needs to be synced better. Items need to be
> dropped where you have died not where you flung off to with your ragdoll.

**Owner:** repo.

### Acceptance criteria

1. The host sees a corpse for a dying player, and it is the game's own `PlayerRagdol` object, not a
   synthetic proxy body. The remote entity's name starts with `PlayerRagdol`.
2. The corpse's limbs move while the body falls and settle at a rest pose; the host's view of the
   body is not a single static position.
3. Items the dying player carried are released **at the death point**, measured from the accepted
   `fpscontroller.Death()` pose, not from the ragdoll's final position. The distance from the
   recorded anchor to each dropped item is at most 2 m in the test scenario.
4. The anchor is captured exactly once per death, before the death camera moves the player
   transform, and a periodic per-player save made during the death screen uses the anchor rather
   than the moving camera position.
5. A respawn puts the player back in the session; the session is not left.
6. The behaviour is switchable at runtime for an A/B run:
   `Entities.DeathRagdollShare` and `SessionRules.DeathDropAnchor` both default to on.

### Evidence

| Gate | Where | State |
| --- | --- | --- |
| `anchor` | `src/TLDRevamp/Net/SessionRules.Death.cs` | patched |
| `corpse` | `src/TLDRevamp/Net/Entities.Death.cs` | patched |
| `twomachine` | `tools/deathsync.py --ab` on two machines | open |

The offline unit projects do not cover this: the anchor and the corpse need the game's own
`fpscontroller` and the item database, so they are game-only. `tools/deathsync.py --ab` runs the
four switch combinations and the deterministic `mp deathfling` bridge command.

### Blockers

- The two files above are uncommitted. They are not in `d1ebfb7` and are not in a release.
- `tools/deathsync.py` is itself modified in the working tree and needs the same review.
- Criterion 3 is measured against a 2 m radius; that number is a test threshold, not a physical
  claim, and a human should confirm the drop feels right.

---

## #32 TerrainGeneration mod merged into RevaMP as a added feature.

> It also has to have aspects of it fixed and tested.

**Owner:** repo.

### Acceptance criteria

1. Terrain tiles are rebuilt when a road or building appears next to them after the tile was built,
   so no building stands on unflattened ground. The rebuild happens in place (the mesh is swapped,
   never a hole) and at most one tile per generator per frame.
2. Tile building runs off the main thread, so a new tile does not freeze the game for about 5 ms.
3. Road and POI generation is parallel where it is safe, and the result is the same world as the
   single-threaded generator.
4. A stale asynchronous job (one whose road/POI lists changed while it ran) is discarded and
   re-requested rather than placed.
5. The POI/road backlog ordering is covered by an offline unit test.

### Evidence

| Gate | Where | State |
| --- | --- | --- |
| `terrainfixes` | `src/TLDRevamp/Fixes/TerrainSync.cs` | committed |
| `async` | `src/TLDRevamp/Fixes/AsyncTerrain.cs` | committed |
| `poi` | `tools/unit/PoiBacklogTest.csproj` | committed |

`dotnet run --project tools/unit/PoiBacklogTest.csproj --configuration Release` covers criterion 5
offline. The other criteria are game-only: they need a world that generates a building next to a
tile that was already built.

### Blockers

- The original "TerrainGeneration mod" is not in this repository and no code here references it by
  name. The issue reads as "fold a third-party terrain mod's features into RevaMP, fixed and
  tested"; the criteria above describe the terrain work that **is** in the tree. Whether that
  satisfies the issue needs the maintainer's answer.
- Criterion 1 has no dedicated offline test; `TerrainSync` is only covered indirectly.
- Criterion 3 ("the same world") needs a world-hash comparison across two runs, which
  `tools/worldcensus.py` and the `worldhash` bridge command can supply, but that check is not
  scripted for this issue.

---

## #33 Make it easier for TLDloader devs to port over mods.

> Self explanatory.

**Owner:** repo.

### Acceptance criteria

1. A developer with a TLD Loader mod can run a **static** scan that starts no game and executes no
   mod code, and get a report naming: the mod's lifecycle overrides, its dependencies (and which
   ones could not be resolved), its type-load failures, and the count of resolved and unresolved
   member references.
2. The scan also produces a skeleton source file showing what the mod must register to work in a
   multiplayer session.
3. The report states clearly that it is a compatibility assessment, not a converter, and that a
   zero unresolved-member count does not prove the mod's gameplay is correct.
4. The scanner runs offline against a real mod DLL, and a representative fixture covers a lifecycle
   override, an unresolved dependency and an added component.
5. The porting instructions are in the modding document.

### Evidence

| Gate | Where | State |
| --- | --- | --- |
| `shim` | `src/TLDLoaderShim/TLDLoader.cs` | committed |
| `scanner` | `src/TLDRevamp/LoaderScan.cs` | patched |
| `scanner-cli` | `tools/modscan/Program.cs` | patched |
| `docs` | `docs/MODDING-MULTIPLAYER.md` §7 | patched |

```sh
TLD_DIR="/path/to/The Long Drive" dotnet run --project tools/modscan -- MyMod.dll
```

### Blockers

- **There is no offline test for the scanner.** `tools/unit/` has no modscan or loader project, and
  CI runs every `tools/unit/*.csproj` — so nothing would catch a scanner regression. This is the
  largest gap in this issue.
- The `scanner`, `scanner-cli` and `docs` gates are uncommitted local changes.
- The scanner's report claims need a real third-party mod as the subject. The repository has no
  TLD Loader mod fixture; using one from a real player's folder is not an option for CI, so a
  synthetic fixture is needed.

---

## #34 Multiplayer settings

> A proper settings menu for the multiplayer session inside of the multiplayer menu screen. Here TPA
> can be set among other options like spawns etc. Cheats/dev menu can also be disabled or enabled
> here. More options can be added in the long run too.

**Owner:** repo.

### Acceptance criteria

1. The host can open a settings panel **inside the multiplayer menu screen** and change: the TPA
   setting (off, or a cooldown), the joiner's placement, the developer-access policy, and the host
   password.
2. The password value itself never goes on the wire. The snapshot carries only a "has a password"
   flag.
3. The host's changes apply immediately and are published to the joiners as one reliable snapshot.
   Joiners see the values read-only.
4. A snapshot is accepted only if it moves forward inside one session (a session token and a rising
   revision). A stale or replayed snapshot is ignored.
5. A malformed snapshot (unknown schema, zero token, unknown policy value, a non-canonical boolean)
   is rejected, and the reader is left with no leftover bytes.
6. The developer-access policy is one of three values (everyone, host only, disabled) and is applied
   to the game's own dev-mode gate. When it is denied, the saved user preference is preserved and
   restored — the runtime override never overwrites the player's saved setting.
7. The developer policy resets when the session ends; it is deliberately not persisted.

### Evidence

| Gate | Where | State |
| --- | --- | --- |
| `snapshot` | `src/TLDRevamp/Net/SessionSettings.cs` | patched |
| `wire` | `src/TLDRevamp/Net/SessionSettingsData.cs` | patched |
| `panel` | `src/TLDRevamp/Net/MpScreen.cs` | patched |
| `offline` | `tools/unit/SessionSettingsTest.csproj` | patched |
| `twomachine` | host and joiner on two machines | open |

```sh
dotnet run --project tools/unit/SessionSettingsTest.csproj --configuration Release
```

The offline test covers criteria 2, 4, 5 and 6's decision matrix (20 checks). Criterion 6's
"the saved preference survives" half and criterion 7 are not covered offline: they need the game's
own `settingsscript`.

### Blockers

- The wire format changed: `Protocol.Version` goes from 46 to 47 and message 111 is new. Every
  client and the dedicated server must be on the same build. The version bump touches
  `README.md` and five documents, which are outside this ledger's scope.
- `tools/unit/DocsAccuracyTest.cs` was weakened in the working tree: it used to assert the protocol
  was exactly 46 and now only asserts the number is non-empty. That change removes a real check and
  should not land as it stands.
- The password is stored in `BepInEx/config/tldrevamp-settings.txt` as a host default. That is a
  plain-text file; a reviewer should confirm the report and the telemetry paths never include it.
- The panel is built at runtime by instantiating the game's own prefab objects. A narrow window
  (720p and below) has not been checked.

---

## #36 More filmmaking tools

> More tools to make videos and other content easily sort of like Replay Mod on Minecraft or just
> more camera options. This can be a main feature on RevaMP or a standalone mod with revamp API.

**Owner:** repo.

### Acceptance criteria

1. A free/orbit camera exists, and it can be turned on and off without leaving the game.
2. The camera restores everything it took over: the player camera, the input mode, the cursor and
   the mouse look. Leaving the camera returns the game to exactly the state before it was entered.
3. The camera does not change anything for the other player: no network message, no change to the
   player's own position or seat.
4. The camera survives a scene change, a death and a session start or end without leaving the game
   in a broken state (the camera is off and the controls are back).
5. It works in VR: the camera does not fight the headset for the camera transform, and the UI
   events still reach the game.
6. The camera costs little: the frame time with it on is not worse than a small fixed budget, and it
   is off by default.

### Evidence

| Gate | Where | State |
| --- | --- | --- |
| `camera` | `src/TLDRevamp/CinematicCamera.cs` | missing |
| `twomachine` | a second player sees no change | open |

### Blockers

- **No camera code exists in this repository.** The closest work is a
  `src/TLDRevamp/Cinematic/CinematicCameraMath.cs` in a separate parallel snapshot, which holds the
  camera maths but not the lifecycle, the input handling or the network isolation. It is not in this
  tree, and it is uncommitted where it exists.
- The issue leaves a product decision open: "main feature on RevaMP or a standalone mod with the
  RevaMP API". That decision changes where the code lives and should be made before implementation
  continues; the criteria above are written to hold either way.
- Criterion 5 cannot be checked without a headset.
- Criterion 3 needs a two-machine run to be evidence rather than an argument.

---

## #37 Render terrain further

> Terrain doesn't render that far what is seen by the player is "Distant terrain" this results in
> floating roads at crossroads or roads going into the ground at close view. This would also require
> optimization if possible so that it can be used without a drastic performance hit.

**Owner:** repo.

### Acceptance criteria

1. The game's near 500 m of world is left intact; the fix does not raise the draw distance and so
   does not load the full road, prop and physics far away.
2. Where a road crosses the coarse far mesh, the far mesh's vertices are pulled onto the road's
   height within a 38 m blend radius, so a distant road no longer floats over an unrelated hill.
3. Beyond the near area, roads are drawn as lightweight ribbons (no road pieces, no poles, no
   physics), so a crossroads reads correctly at a distance.
4. The two behaviours are separately switchable, so a user can turn either one off:
   `TerrainHorizon.CorrectFar` and `TerrainHorizon.DistantRoads`.
5. The ribbon geometry is correct and bounded: a strip is four vertices and two triangles, and
   degenerate, non-finite and absurd input is rejected without touching the buffer.
6. The work is pooled and bounded: one rebuild per 35 m of movement, a fixed vertex cap per batch,
   and stale batches are destroyed rather than leaked.
7. The performance claim is measured: frame timing and memory with the setting on and off, with no
   drastic performance hit.

### Evidence

| Gate | Where | State |
| --- | --- | --- |
| `horizon` | `src/TLDRevamp/Fixes/TerrainHorizon.cs` | patched |
| `lab` | `src/TLDRevamp/Fixes/TerrainHorizonLab.cs` | patched |
| `tool` | `tools/terrainhorizon.py` | patched |
| `offline` | `tools/unit/TerrainHorizonTest.csproj` | patched |
| `perf` | frame timing and memory, on and off | open |

```sh
dotnet run --project tools/unit/TerrainHorizonTest.csproj --configuration Release   # 30 checks
python3 tools/terrainhorizon.py --out bench/horizon-before.json
python3 tools/terrainhorizon.py --out bench/horizon-after.json
python3 tools/terrainhorizon.py --compare bench/horizon-before.json bench/horizon-after.json
```

The offline test covers criterion 5 and the batching bound in criterion 6. The comparison tool is a
structural snapshot, not a visual proof: criteria 2, 3 and 7 need screenshots and a frame-time
capture.

### Blockers

- **The diagnostic is not reachable.** `TerrainHorizonLab.Snapshot` exists, but the debug bridge has
  no `terrainhorizon` command, so `tools/terrainhorizon.py` will be refused by the bridge as it
  stands. One line in `src/TLDRevamp/DebugBridge.cs` (a `case "terrainhorizon":` that calls
  `Fixes.TerrainHorizonLab.Snapshot(arg)`) closes this. The tool's own docstring claims the command
  works; today it does not.
- Criterion 7 is not measured. The `bench.py` benchmark harness exists (`tools/bench.py
  --scenarios idle,drive`), but no before/after run with the setting toggled is recorded.
- A reviewer should check the far-mesh write path for z-fighting at the 38 m blend boundary, and
  the behaviour when the player teleports (a jump of more than 35 m forces a rebuild, but the
  rebuild reads road chunks that may not be generated yet at the destination).

---

## #38 RevaMP on other platforms

> Uploading RevaMP and the other mods on NexusMods with included instructions and other help. Users
> will be directed to github for any issues or serious posts.

**Owner:** **account** (the NexusMods account, held by Normal99). No code change in this repository
can close this issue.

### Acceptance criteria

1. `packaging/INSTALL.txt` and the release's own instructions are complete enough for a NexusMods
   reader: what the mod does, what it needs (BepInEx 5, the Steam beta branch, the game's own copy),
   how to install and how to uninstall.
2. The licence texts and credits are correct in the release (`packaging/licenses/`, and the credit
   line naming BepInEx as MIT).
3. The NexusMods page is written: a description, a screenshot, the requirement list, and a link
   back to this repository for issues and support.
4. The example mods (Head Unit, Car Trailer, Trailer Hitches) are each published or linked.

### Evidence

| Gate | Where | State |
| --- | --- | --- |
| `install-doc` | `packaging/INSTALL.txt` | committed |
| `nexus-page` | the NexusMods page | open |

### Blockers

- The account step is a person, not a script. This is the clearest "account-owned" item in the
  ledger: the page, the screenshots and the account access belong to the maintainer.
- Criterion 2 depends on the licence work in `29f0876`, which is committed.

---

## #39 Mod disable/enable

> Disable/enable mods from the mod browser without uninstalling them.

**Owner:** repo.

### Acceptance criteria

1. A mod can be disabled from the mod browser. The DLL moves out of the plugins tree entirely, into
   `BepInEx/disabled-mods/<id>/`, so it is not loaded at the next start.
2. A disabled mod can be enabled again, and it is loaded at the next start.
3. The disabled mod's *intent* is stored separately from its file, so an update or a repair cannot
   silently re-enable it, and a missing disabled folder is reported rather than ignored.
4. A crash in the middle of a move never loses a DLL: every file is either in the plugins tree or
   safely set aside, and the pending intent survives.
5. RevaMP's own DLLs and the protected ids (the core plugin, the multiplayer API, the preloader,
   TLDLoader, BepInEx) cannot be disabled, moved or replaced. Path escapes (`../escape`, `.`,
   `bad/dir`) are refused.
6. Removing a disabled mod keeps the DLL as a backup instead of deleting it.

### Evidence

| Gate | Where | State |
| --- | --- | --- |
| `activation` | `src/TLDRevamp/Net/ModActivation.cs` | committed |
| `preloader` | `src/TLDRevamp.Preloader/ModActions.cs` | committed |
| `offline` | `tools/unit/ModActivationTest.csproj` | committed |
| `in-game` | disable, restart, enable, restart in the real game | open |

```sh
dotnet run --project tools/unit/ModActivationTest.csproj --configuration Release   # 20 checks
```

This is the one issue in the range whose code is already in the committed baseline (commit
`9a79b0c`, "Mod browser can disable and enable mods without uninstalling them (#39)"), and whose
offline test passes. It is the closest to done.

### Blockers

- Criterion 6's "in-game" part is not covered offline: the offline test proves the file moves and
  the intent survives, but not that the game loads the mod again after a restart. That needs a real
  game and a restart.
- The `tools/modtogglecheck.py` script that the evidence tooling brief describes is **not** in this
  repository, and `tools/README.md` does not reference it. If it is added, it must refuse owner
  prefixes and work on isolated test copies only.

---

## #40 Add WIP watermark

> Add WIP wateramrks similar to My Summer Car mod WreckMP. This way videos showcasing the mod will
> clearly show that it is WIP. Decision has to be made if this will be a thing on all branches or
> not.

**Owner:** repo for the mark; the maintainer for the "all branches?" decision.

### Acceptance criteria

1. A small "TLD RevaMP <version> · WIP" mark (bold white Fira Sans with a soft shadow, styled after Unity's
   "Development Build" watermark) is drawn in the bottom-right corner of the game view.
2. The mark is drawn **independently of the performance overlay**: hiding the overlay (Page Up) does
   not hide the mark, and showing the overlay does not duplicate it.
3. The mark is on by default and can be turned off under `Settings → Revamp → Branding`, and the
   choice survives a restart.
4. The mark is legible but unobtrusive, and it does not scale with the UI so it stays in the corner
   at every resolution.
5. The "all branches or not" decision is recorded: the mark's scope (every branch, `dev` only, or
   release only) is written down next to the setting.

### Evidence

| Gate | Where | State |
| --- | --- | --- |
| `overlay` | `src/TLDRevamp/Overlay.cs` | patched |
| `setting` | `src/TLDRevamp/RevampSettings.cs` | patched |
| `screenshot` | one capture with the mark on, one with it off | open |

### Blockers

- **The mark is drawn unconditionally at the top of `Overlay.Draw`, before the `if (!Visible) return;`
  check.** That is the correct behaviour for criterion 2, and it is worth a comment saying so,
  because the placement looks like a mistake otherwise. It also means the mark is not affected by
  the overlay's own visibility, which is exactly what criterion 2 asks for.
- The `Overlay.cs` and `RevampSettings.cs` changes are uncommitted.
- Criterion 5 is a decision, not a code change. The issue says it has to be made; nothing in the
  tree records it.
- Criterion 3's "survives a restart" is not covered by any offline test: the setting is bound in
  `Plugin` through BepInEx's config, and there is no test for it.
- The screenshot gate needs two captures. They belong in `bench/`, not in the repository.

---

## #41 VR Rewrite

> VR support in The Long Drive is a buggy mess we have to rewrite it and make it usable and playable
> with. It should also be fully multiplayer compatible, optimized and efficiently.

**Owner:** repo.

### Acceptance criteria

1. The OpenXR loader is installed and started through the game's own XR management, without
   replacing the game's `vrHandsScript`/input bridge. The legacy path stays available as a fallback.
2. The runtime falls back to the legacy path when the OpenXR loader cannot initialize or start, and
   the reason is recorded instead of the game failing to start.
3. **No headset pose is sent over the network.** The player's movement and seats are synchronized
   through the ordinary player state; the VR pose stays local.
4. The headset and the controllers are tracked; the in-game UI events still reach the game.
5. A session works in VR: a player joins, plays and respawns in a headset without leaving the
   session.
6. A repeatable check exists: `tools/quest3vr.py probe` reports the setup, and `run` starts OpenXR,
   samples the state, the frame times and the error counters, recenters, and captures a screen.
7. The tool never changes the headset's system settings.
8. Rendering cost is measured rather than assumed.

### Evidence

| Gate | Where | State |
| --- | --- | --- |
| `openxr` | `src/TLDRevamp/Fixes/OpenXrBridge.cs` | patched |
| `tool` | `tools/quest3vr.py` | patched |
| `libs` | `tools/libs.sh` | patched |
| `headset` | a Quest 3 session with the comfort and visual check | open |

```sh
python3 tools/quest3vr.py probe
python3 tools/quest3vr.py run --seconds 600 --out bench/quest3-openxr.json
```

### Blockers

- **The bridge commands the tool uses do not exist.** `tools/quest3vr.py` calls `openxr`,
  `openxr start`, `openxr stop` and `openxr recenter`, but `src/TLDRevamp/DebugBridge.cs` has no
  `openxr` case, and `OpenXrBridge`'s methods are `internal`. The tool will be refused by the
  bridge until a `case "openxr":` dispatch is added. This is the same shape of gap as #37.
- `OpenXrBridge.Tick` starts OpenXR by itself when the game's VR setting is on. A rewrite that
  owns `SetVR`/`SetXR` must decide whether the mod keeps that ownership or yields it; the review
  brief for this issue asks the same question. Until that is settled, "the game's own VR path"
  and "the mod's OpenXR path" can fight over the same loader.
- `tools/libs.sh` copies the OpenXR assemblies only when the game has them, so a player with an
  older game copy builds without VR support. That is deliberate, but it means "VR works" is a
  property of a game build, and the release notes have to say so.
- The whole `openxr` gate is uncommitted. `lib/` is gitignored, so the OpenXR assemblies are not
  in the repository either; the build depends on the local game copy.
- Criterion 8 is not measured.

---

## #42 No Clank README

> Human README for humans. Things will be dumbed down and much better then.

**Owner:** **account** for where it is published; repo for the writing.

### Acceptance criteria

1. A README written for a player who does not know what a protocol number is: what the mod does,
   how to install it, how to play together, how to get help.
2. It keeps the existing safety and licence facts: the beta branch, the backup warning, the LGPL
   licence, the credits and the third-party licences.
3. It does not contradict the technical README. Where both exist, the human one is the front door
   and the technical one is the reference.
4. The decision on where the human README lives is recorded (a separate file in the repository, a
   page on the project's site, or the NexusMods page from #38).

### Evidence

| Gate | Where | State |
| --- | --- | --- |
| `readme` | `README.md` | patched |
| `account` | the publication decision | open |

### Blockers

- The current `README.md` is the technical one: it documents protocol numbers, the debug bridge,
   telemetry and the building steps. Rewriting it in place would delete the reference material the
   contributors use. A separate document (for example `docs/HUMAN-README.md`) keeps both, and the
   ledger's validator accepts either.
- The `README.md` in the working tree is modified (the protocol 46 → 47 line) and is outside this
  ledger's scope, so nothing here depends on it.
- Criterion 4 is a decision for the maintainer, and it interacts with #38: the NexusMods page
  wants the same text.

---

## Summary

| Issue | Owner | Committed gates | Patched gates | Missing gates | Open (human) gates |
| --- | --- | --- | --- | --- | --- |
| #31 Player Ragdoll sync | repo | 0 | 2 | 0 | 1 |
| #32 TerrainGeneration merge | repo | 3 | 0 | 0 | 0 |
| #33 TLDLoader porting | repo | 1 | 3 | 0 | 0 |
| #34 Multiplayer settings | repo | 0 | 4 | 0 | 1 |
| #36 Filmmaking tools | repo | 0 | 0 | 1 | 1 |
| #37 Render terrain further | repo | 0 | 4 | 0 | 1 |
| #38 RevaMP on other platforms | account | 1 | 0 | 0 | 1 |
| #39 Mod disable/enable | repo | 3 | 0 | 0 | 1 |
| #40 WIP watermark | repo | 0 | 2 | 0 | 1 |
| #41 VR rewrite | repo | 0 | 3 | 0 | 1 |
| #42 Human README | account | 0 | 1 | 0 | 1 |

`python3 tools/issue_evidence.py` prints this table from the working tree, so it cannot drift away
from reality.

**No issue in this range is complete.** #39 is the closest: its code is in the committed baseline
and its offline test passes, but the in-game restart cycle is still unverified. #36 has no
implementation here at all. Two of the ranges' issues (#37 and #41) have tools that call debug
bridge commands the bridge does not implement yet.

**Excluded:** #35 (tldrevamp.com with submission forms for devs) needs a website, a domain and a
policy for submissions. It is not tracked by this ledger and is not counted in the table above.

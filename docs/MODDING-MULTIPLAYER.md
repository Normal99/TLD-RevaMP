# Making other mods work in multiplayer (outline)

> **Status:** every step of section 9 is built. `TLDRevamp.Multiplayer.dll` (API 4) has registration, records,
> edits, ownership hooks, events, a shared clock, pose sync, state records sent by distance, the far store,
> session-wide settings and replay channels; the session side is `src/TLDRevamp/Net/ModObjects.cs`. Mods must match at join (section 3a)
> and a joiner can get an approved mod from the host. The Head Unit and Car Trailer example mods run in two-machine
> tests, and `tools/modapi2.py` (with the test mod `src/Mods/ApiProbe`) tests API 2. The Loader-mod scanner
> (section 7) writes a report and a code skeleton.

## 0. Using it

A mod references `TLDRevamp.Multiplayer.dll` (shipped next to `TLDRevamp.dll`). It has no other dependency than
Unity's core module, so a mod built against it loads without TLD Revamp; every call then works on this machine only.

**Licence.** TLD Revamp, `TLDRevamp.Multiplayer.dll` included, is GPL-3.0 ([COPYING](../COPYING)). A mod that
references it or uses TLD Revamp's code must be GPL-3.0 compatible: release it under GPL-3.0 (or a licence that
can be combined with it) and give its source to the people you give the mod to. Closed-source mods can't use this
API. The same goes for the skeleton the Loader-mod scanner writes (section 7) once it is filled in and shared.

```csharp
using TLDRevamp.Multiplayer;

class BeaconType : SharedType
{
    public override string Name => "beacon";
    public override GameObject Create(RecordReader r, Vector3 pos, Quaternion rot)
    {
        var go = MakeBeacon(pos, rot);   // the mod's own prefab
        Read(go, r);
        return go;
    }
    public override void Write(GameObject go, RecordWriter w) { w.Color(go.GetComponent<Light>().color); }
    public override void Read(GameObject go, RecordReader r)  { var c = r.Color(); if (r.IsValid) go.GetComponent<Light>().color = c; }
    // the owner decides on changes asked by others; returning true sends the new record to everyone
    public override bool OnEdit(SharedObject o, RecordReader edit, int from)
    {
        var c = edit.Color(); if (!edit.IsValid) return false;
        o.GameObject.GetComponent<Light>().color = c; return true;
    }
}

// once, at startup
var mod = MpApi.RegisterMod("beacons", "Beacons", "1.0.0");
var beacon = MpApi.RegisterType(mod, new BeaconType());
// when the player places one
var shared = MpApi.Share(beacon, go);
// any player recolours it (applied by the owner)
shared.RequestEdit(w => w.Color(Color.red));
```

What the session does for a shared object: a network id, one owner (the creator; the host when they leave; another
player through `RequestOwnership` if the type allows it), copies on every other machine built by `Create`, joiners
get it from the host's latest record, removal everywhere. `SyncPose` types get their position sent by the owner and
played back smoothly on copies (which are kinematic). Objects parented to something else (a part in a car) are not
moved by the session: they ride their parent. `MpApi.WorldObjectId` / `FindWorldObject` let a record point at one
of the game's shared objects (a car). `MpApi.SessionTime` is the host's clock on every machine, for things that must
happen at the same moment everywhere (a video's position). `MpApi.SendEvent` / `OnEvent` carry short messages that
are not object state.

Limits, checked by the host before anything is passed on: records up to `MaxRecordBytes` (8 KB by default), edits
and events up to 1 KB, 60 messages a second per player (bursts of 120), 1,000 objects per player, finite positions,
records and poses only from the current owner. Bridge: `mp mods` (counters, objects); switch
`set TLDRevamp.Net.ModObjects.Enabled False` keeps mod objects local (A/B).

Two things about this game that every BepInEx mod meets (found with the head unit example mod):
- The game destroys BepInEx's manager object when its scene loads, so a plugin's own `Update`, `OnGUI` and the like
  never run. Put per-frame work on a `GameObject` of the mod's own, made in `Awake` with `DontDestroyOnLoad`.
  TLD Revamp and the example mods do this.
- No camera is tagged MainCamera (`Camera.main` is null). The player's view is `mainscript.s.player.Cam`.

## Ragdolls

Game-style ragdolls with `tosaveitemscript.ragdollPos` need nothing. RevaMP uses the game's limb list.
For a custom ragdoll with its own list, call `MpApi.RegisterRagdoll(root, limbs, isLimp)` (API 3) when it is
created on each machine, before or after sharing it. `root` is the object RevaMP syncs, with the item's
`tosaveitemscript`. This registers the limbs; the object must still be shared through the game's save system.

Pass 1-63 limb transforms, with no nulls or duplicates. Their order must match on every machine: use the same
prefab and mod version. RevaMP sends only the limbs that moved. It checks a hash of the limb names so a
mismatched hierarchy is ignored rather than moving the wrong limbs. Limbs are sent while limp: `isLimp` returns
whether they move on their own. Leave it null for always limp.

The API stores a copy of the array. Call again to replace it, or `MpApi.UnregisterRagdoll(root)` to remove it.
Destroyed roots are removed lazily. Registration works without a session or TLD Revamp installed.

## Replay channels

A mod's own state can be in TLD Revamp's [replays](REPLAY.md) (API 4). Register a channel once, in `Awake`:

```csharp
var ch = MpApi.RegisterReplayChannel(mod, "screen");   // "tvbrowser/screen" in the file
ch.RecordingStarted += () => { /* record every object's current state once */ };
ch.Shown += k => { /* k.Copy: the object's copy; k.Data: the latest key at the playhead; k.CopyOf(liveRenderer) */ };
ch.PlaybackEnded += () => { /* free what Shown made */ };
// while ch.Recording: ch.Record(gameObjectOfAnItem, bytes) whenever the state changes
```

A key is about a game item (one with `tosaveitemscript`, or a child of one) that the recording samples, at most
256 KB. Record only on change: the latest key at the playhead is what `Shown` gets, once each time it changes and
once when playback starts. `Shown` gets the copy (a GameObject the replay draws), the live object when it is in this
world, and `CopyOf(renderer)`, the copy's renderer for one of the live object's: set a material of the mod's own on
it (the copies share the game's materials). Nothing is sent to other players; replays are local. Keep
`RegisterReplayChannel` in a method of its own, so the mod still loads with an older RevaMP (`MissingMethodException`
there means no channels). Example: `src/Mods/TvBrowser/ReplayTv.cs`.

## 1. Why this is needed

TLD Revamp shares the game's own things: cars, items, parts, building contents, doors and switches, the radio,
weather and time. It finds them through the game's save system: anything the game can save (a save script on the
object, with its save records) can be shared, because a save record already describes everything needed to build
the same object on another machine.

A mod's objects are usually invisible to that:

- objects a mod creates from its own assets have no game save script, so the session never sees them;
- objects that do use the game's save script but carry extra state in the mod's own components lose that state
  on the other machines (it isn't in any game save record);
- a mod that reacts to player input (a button, a key, a menu) runs only on the machine of the player who pressed it.

The plan: a small public API so a mod can say "this object is shared", "this is its state" and "this happened",
and the session does the rest (ownership, joiners, players far away, saving).

## 2. Principles

1. **No changes to our multiplayer code per mod.** A mod registers; it never patches or calls our internals.
2. **Same rules as the game's objects.** A registered object gets what a car or an item gets: one owner at a
   time who simulates it, copies everywhere else, hand-off when the owner leaves, a place in the far store when
   no player is near, a record for players who join later.
3. **State travels as records.** A mod describes its state as a record (a plain serialisable class), the same way
   the game describes a car part or a tank. Records are versioned and size-capped.
4. **Optional.** A mod that doesn't use the API keeps working as it does today (local only).
5. **Safe by default.** Every record and event from another player is checked (size, version, rate, value ranges)
   before the mod sees it, as our own messages are today. A broken mod can't end a session.

## 3. Registering an object type as shared

- A mod registers a **type key** (its mod id plus a type name) and a **factory**: given a record, build the
  object here (from the mod's own prefab or assets).
- When the mod creates an object of that type in a session, it hands the object to the session, which gives it a
  network id and an owner (the creating player) and sends its first record to everyone near.
- Removal goes through the session too, so the object disappears everywhere (and from the far store), not only on
  one screen.
- Objects that come from the game's own save system need only the extra-state part (section 4): the session
  already shares them.

### 3a. Mods must match (built)

Every player's mods that use this API must be the same as the host's: the same mod ids, versions and DLL files
(SHA-256 of each mod's DLL, computed when it registers). The joiner's list rides in Hello.

Disabling a managed mod in the browser takes effect at the next start. Until then its already loaded code and
Hello registration remain active. After restart the DLL is outside `BepInEx/plugins`, never loads or registers,
and is absent from Hello. The ordinary missing-mod check still applies. If a required mod is installed but
disabled, the join screen tells the player to enable it in the mod browser and restart before joining again.
Host offers recognize the multiplayer registration id even when it differs from the browser's plugin GUID;
the browser records that association when queuing a change. No wire fields were added (protocol 45).

- **The host has a mod the joiner lacks, or another version or file of it:** the host offers it
  (`Net/ModTransfer.cs`). The joiner checks each offered hash against `approved-mods.txt`, which ships next to
  `TLDRevamp.dll` and lists the approved mod files (SHA-256, id, version). An unlisted file is never downloaded:
  the join ends with its name. Otherwise the player is asked (names, versions, sizes); the bytes that arrive are
  hashed again and must be the approved file that was offered. The mod is saved to
  `BepInEx/plugins/TLDRevampMods/`, an older copy is set aside (`.replaced`), and the join ends with "restart the
  game, then join again" (BepInEx loads plugins once). Single-file mods only. `ModTransfer.HostSends = false`
  turns sending off: such joiners are refused with what to install.
- **The joiner has a mod the host doesn't, or a type version differs:** refused, with what to remove or update, and
  an `[anomaly] mods differ` line in the host's log.

This differs on purpose from the game's own build, which is only announced: a player who can't build a mod's
objects, or reads their records with another layout, has a broken world. The hash proves a file is the approved
one, so a host can't slip in its own code; it can't prove an approved mod is harmless, which is what approving it
means. A modified game can also lie about its own hashes: this is a consistency check, not anti-cheat.

Open questions:
- Whether a type can declare that it rides a parent (a part bolted to a car, cargo in a container), so it moves
  with the parent instead of sending its own states.

## 4. State as save records

Three kinds of state, matching what the session already does for cars and items:

| Kind | When it is sent | Example |
|---|---|---|
| **Full record** | on creation, to joiners, when a player arrives near it, after a hand-off | everything needed to build it |
| **State record** | at a fixed rate while it changes, by its owner | position, speed, a gauge value |
| **Edit record** | when a player changes something, from that player | a switch flipped, a dial turned |

- The mod provides: write record, read record, and (for state records) write and read state.
- The session provides: rates, interest (only players near get state records), the far store, and replay for
  joiners.

Built (API 2):
- **State records:** a type with `HasState` writes its state in `WriteState`. The owner calls it up to `StateHz`
  times a second (5 by default, at most 20) and sends it only when the bytes changed (`MaxStateBytes`, 256 by
  default). The host passes it to players within `StateRange` metres (500 by default; 0 = everyone) and keeps the
  latest, so a player who comes within range gets it then. Copies get it in `ReadState`.
- **The far store:** with `ViewRange` above 0, a player's machine has the object only while the player is within that
  many metres. At 1.25 times the range the copy goes (`OnStored`, which destroys it by default); the host keeps the
  record and the latest state. Back within range, the copy is built again from them (`Create`, then `OnPlaced`). An
  owner beyond the range hands the object to the nearest player within it, or to the host when nobody is near. If
  nobody is near at all, the host's copy is stored too, and the first player who comes near owns it. `ViewRange` 0
  (the default) keeps API 1's behaviour: everyone has every object.
- Records must be deterministic to read: the same record gives the same object on every machine. Random values
  (loot, wear) are chosen once by the owner and sent, never rolled again on each machine.

## 5. Events and ownership hooks

Callbacks a mod can implement, all optional:

- **Owner changed** (became owner / lost ownership): start or stop simulating; copies should not run physics or
  game logic that changes state.
- **Edit requested** (another player used the object): the owner decides and applies; the result travels as an
  edit record. Gives the mod a single place to validate input.
- **Placed / stored**: the object came into view on this machine or went into the far store.
- **Player joined / left**: for mods with session-wide state.

And one way to send a **mod event** (a short message to everyone or to the owner) for things that aren't object
state: a sound, an effect, a chat-like notice. Events are rate-limited and size-capped.

## 6. Session-wide settings

A mod with settings that must match for everyone (rules, difficulty, spawn rates) registers them so the host's
values are sent at join and applied for the session, the way the host's physics-lock setting is. Local-only settings
(graphics, keys) stay local.

Built (API 2): `MpApi.RegisterSetting(mod, name, get, apply)`. Values are strings of at most 1024 bytes, in a format
the mod chooses. The host checks its values about once a second and sends any that changed. A joiner applies the
host's value with `apply`, shows it as the host's while `SessionSetting.FromHost` is true, and gets its own value
back when the session ends.

## 7. A converter for existing TLD Loader mods

TLD Revamp already runs TLD Loader mods under BepInEx on the beta (the mod's lifecycle calls, its logger and its
config folder). Most of those mods were written for single player. A converter would help them into multiplayer
without rewriting them:

1. **Scan** a Loader mod's assembly and report what it does that matters in a session: objects it creates,
   components it adds to game objects, input it reacts to, game state it changes directly.
2. **Classify** each finding: works as is (purely local or cosmetic), works through the game's save system
   already, or needs registration (section 3) or records (section 4).
3. **Generate a starting point**: a registration and record skeleton for the mod's author to fill in, never a
   silent rewrite of someone else's code.
4. **Report**, in plain words, which features will be local only until the author adopts the API.

Out of scope: changing a mod's behaviour without its author, and mods that replace the game's own systems (another
multiplayer, another mod loader).

Built: `src/TLDRevamp/LoaderScan.cs` reads a mod's code (its IL; nothing is run) and writes `<mod>.md` and
`<mod>.MpSkeleton.cs` to `BepInEx/TLDRevampModScan`. To use it, set `[Mods] ScanLoaderMods = true` in
`BepInEx/config/tldrevamp.core.cfg`: every mod in `BepInEx/TLDLoaderMods` is scanned at startup. The report lists:
- its lifecycle overrides, dependencies that could not be resolved, type-load failures and counts of resolved and
  unresolved member references, so a beta API change is visible before the author rewrites a feature;
- objects the mod makes from its own assets;
- components it adds;
- keys and buttons it reacts to;
- world-wide values it changes (time scale, gravity);
- random numbers it rolls;
- game state it writes directly;
- files it writes;
- the game's own things it spawns (shared already);
- what only draws on screen (works as is).

The same scan runs without starting the game (build Revamp first):

```sh
TLD_DIR="/path/to/The Long Drive" dotnet run --project tools/modscan -- MyMod.dll
```

The report and the skeleton are written **next to the scanned DLL** (`MyMod.md` and `MyMod.MpSkeleton.cs` there),
which is where the in-game scanner writes them too. `--out <folder>` sends them somewhere else. The tool never writes
into the current working directory, so running it from the repository root cannot leave build output in the tree.

The report is deliberately a compatibility assessment, not a converter: it never runs or rewrites the mod. A zero
unresolved-member count proves that the assemblies needed to resolve its references were available to the scanner; it
does not prove that the mod's lifecycle or gameplay is correct. Test the mod's own features in single player, then in
the two-machine test after porting its multiplayer behavior.

Before changing game-facing code, compile the same DLL against the scanner's `TLD_DIR` (or the supplied `--lib`
folders). Missing dependencies and changed game members are then errors in the report and in the build, rather than a
runtime surprise after players install it.

Each item says what it means in a session. The skeleton registers the mod, has a `SharedType` for each component
the mod adds, a setting for each world-wide value and an event for each input handler, all to be filled in by the
mod's author.

## 8. Testing

Every registered type would get the same two-machine checks the game's objects have: created on one machine and
seen on the other, edited by the non-owner, owner leaves (hand-off), both players far away and back (far store),
a late joiner sees it, and the session saved and loaded. The two-machine test tools in `tools/` run the game's own
objects through these today ([tools/README.md](../tools/README.md)); to see what the game does with an object, decompile
it yourself as [CONTRIBUTING.md](CONTRIBUTING.md) explains.

## Mod browser catalog

The in-game browser reads `packaging/mods.json` from `dev`, where the browser is developed. Its `id` is the
BepInEx plugin GUID: `tldrevamp.headunit` and `tldrevamp.cartrailer`. Head Unit's multiplayer registration id,
`headunit`, stays unchanged; it is a separate identifier used by the session protocol. Using the plugin GUID in
the catalog lets the browser recognize mods installed by hand, including Car Trailer, which does not register
with the multiplayer API.

Managed installs have **Disable / Enable** and **Remove** controls, including local entries when the catalog is
offline. States are **Enabled**, **Disabled (takes effect at next start)**, **Disabled**, and **Enabled at next
start**. Hand-installed mods are read-only because one DLL or folder can serve several plugins or dependencies.
RevaMP (`TLDRevamp`, `TLDRevamp.Multiplayer`, `TLDLoader`, its preloader) and BepInEx core cannot be toggled.

`TLDRevamp.Preloader.dll` is installed under `BepInEx/patchers/TLDRevamp/`. Its initializer replays durable intent
before the chainloader scans plugins. BepInEx 5.4.23's `TypeLoader.FindPluginTypes` uses
`Directory.GetFiles(..., "*.dll", SearchOption.AllDirectories)`; naming a subfolder "disabled" does not disable it.
We move managed DLLs between `BepInEx/plugins/TLDRevampMods/` and `BepInEx/disabled-mods/<id>/` instead. Each
same-volume file rename is atomic, never overwrites a destination, and can be replayed after a partial batch or
crash. The intent file `<id>.state` is atomically replaced and flushed before any moves. It lives outside the DLL
folder, so losing that folder leaves a visible **Disabled (DLL folder or file missing)** entry. Conflicts preserve
both copies and leave the action pending. Linked files/folders and paths outside the managed locations are refused.

Updates and new installs of a disabled mod write to its disabled folder and keep its intent. Old versions remain
as `.replaced`. Remove queues a move to a unique `.removed` backup at next start, from either location; no DLL is
deleted. Preserve `BepInEx/disabled-mods` during RevaMP upgrades. Build references include BepInEx's local
`Mono.Cecil.dll` (`tools/libs.sh` copies it); the preloader is built automatically with the main project and included
by the package, source/test installer, and deployment scripts.

Bridge commands: bare `mods` retains the Loader-mod status command; `mods list` returns browser ids, versions,
`installed`, desired `enabled`, `pending` (null or enabled/disabled/removed), `loaded`, `registered` (the live API
registration used by Hello), and a readable state. `mods enable <id>` / `mods disable <id>` queue changes;
`mods remove <id>` queues removal using the same file path as the screen. Existing `mp mods` is unchanged.
The external `modtogglecheck.py --selftest` is offline; its live mode owns only hidden LiveTestA on bridge 27060
and verifies loading, disabling, enabling and removal while disabled across restarts. It must be run separately.

Head Unit 0.2.0 first shipped with RevaMP 0.68.0; the catalog's `revamp: "0.68.0"` records that compatibility
baseline. The field is an explanatory hint when a file is unapproved, not a runtime version check. The local
approved list and the downloaded DLL's SHA-256 decide whether installation is allowed.

Car Trailer 0.1.1 is approved by the hash of its existing release DLL. Its catalog ZIP size and both example mods'
DLL hashes were checked against the v0.69.0 assets. The original assets are reused without rebuilding them.
Car Trailer can be installed from the browser and loads after restarting the game; every player still needs it, since it
does not register for the host's missing-mod check or transfer.

BusGut 0.2.0, MountAnything 0.2.1 and TerrainChoice 0.4.1 (by YujiroX2, `anonymoustld.*` GUIDs) are hosted in
their own repositories and approved by the hashes of their release DLLs. All three register with the multiplayer API
(`busgut`, `mountanything`, `terrainchoice`), so a join with a different set is refused. Their catalog baseline is
RevaMP 0.69.1, the version their self-tests passed on.

## 9. Order (all built)

1. Registration + full records for objects a mod creates (sections 3 and 4).
2. Edit records and ownership hooks (section 5).
3. State records with rates and interest.
4. Session-wide settings (section 6).
5. The Loader-mod scanner and report (section 7), then the skeleton generator.

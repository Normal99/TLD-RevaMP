# Making other mods work in multiplayer (outline)

> **Status:** every step of section 9 is built. `TLDRevamp.Multiplayer.dll` (API 2) has registration, records,
> edits, ownership hooks, events, a shared clock, pose sync, state records sent by distance, the far store and
> session-wide settings; the session side is `src/TLDRevamp/Net/ModObjects.cs`. Mods must match at join (section 3a)
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
- objects the mod makes from its own assets;
- components it adds;
- keys and buttons it reacts to;
- world-wide values it changes (time scale, gravity);
- random numbers it rolls;
- game state it writes directly;
- files it writes;
- the game's own things it spawns (shared already);
- what only draws on screen (works as is).

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

Head Unit 0.2.0 first shipped with RevaMP 0.68.0; the catalog's `revamp: "0.68.0"` records that compatibility
baseline. The field is an explanatory hint when a file is unapproved, not a runtime version check. The local
approved list and the downloaded DLL's SHA-256 decide whether installation is allowed.

Car Trailer 0.1.1 is approved by the hash of its existing release DLL. Its catalog ZIP size and both example mods'
DLL hashes were checked against the v0.69.0 assets. The original assets are reused without rebuilding them.
Car Trailer can be installed from the browser and loads after restarting the game; every player still needs it, since it
does not register for the host's missing-mod check or transfer.

BusGut 0.2.0, MountAnything 0.2.1 and TerrainChoice 0.4.1 (by AnonymousTLD, `anonymoustld.*` GUIDs) are hosted in
their own repositories and approved by the hashes of their release DLLs. All three register with the multiplayer API
(`busgut`, `mountanything`, `terrainchoice`), so a join with a different set is refused. Their catalog baseline is
RevaMP 0.69.1, the version their self-tests passed on.

## 9. Order (all built)

1. Registration + full records for objects a mod creates (sections 3 and 4).
2. Edit records and ownership hooks (section 5).
3. State records with rates and interest.
4. Session-wide settings (section 6).
5. The Loader-mod scanner and report (section 7), then the skeleton generator.

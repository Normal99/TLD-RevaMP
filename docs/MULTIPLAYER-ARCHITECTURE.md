# TLD Revamp — Multiplayer architecture (proposal, 2026-09-27)

Goal: **10+ players, stable**, later **dedicated servers**. Rewritten from scratch as part of the mod (the beta's own
multiplayer is not extended). Every player needs their own copy of the game; the mod ships no game files.

## 1. What we build on (facts from the code and measurements)

- **The world is generated from the seed, locally, on every machine.** Terrain, roads, buildings (POIs), scenery and
  road pieces all come from the seed + deterministic code. So the world itself is never sent over the network.
  All our world-generation fixes are therefore kept **bit-exact** (verified against the game) — a client with the mod and
  the server must produce the identical world.
- **The host generates around every player.** The game adds each remote player as a generation centre
  (`syncScript → mapSettingScript.AddGenAround`). A dedicated server must do the same (it needs terrain and colliders
  wherever a player is). Measured (`tools/mpbench.py`, 10 centres): vanilla 27 FPS / p99 160 ms; mod v0.31 42 FPS /
  p99 35 ms, no frame > 50 ms. This benchmark is the dedicated-server workload and stays a standard test.
- **The official multiplayer shows the event surface.** Its `syncScript.Send*` calls (player/car/item position, item
  removal, usables, tanks, attachables, part condition, food, weapons, doors, licence plates, time, …) sit exactly where
  the game changes shared state. That list is our checklist of what must be replicated, and those call sites are where
  our layer hooks in (Harmony), instead of re-discovering every interaction.
- **Floating origin.** Each machine shifts its world back to the origin every ~4.5 km (`mainscript.visszarakva`), at
  different moments. Network positions are therefore always **global double-precision coordinates** (`Vector3d`), never
  Unity positions.
- Official MP weaknesses we don't repeat: star with re-broadcast of every message to everyone, no distance filtering,
  boxing/allocating serializer on every send.

## 2. Topology

Client–server, one authority:
- **Listen server first** (a player hosts), **dedicated server later** — the same server code; the dedicated server is
  the game started headless (`-batchmode -nographics`) with the mod in server mode (no rendering, no audio, no local player).
  The dedicated-server *launch* has no supported path yet: the guarded launcher owns the display arguments and refuses
  `-batchmode`, so the headless entry points (`tools/devserver.py`, `tools/servermode.py`, `tools/serveradmin.py`,
  `tools/leasepersist.py`, `tools/headless.py`) are retired and refuse instead of starting anything. The server code
  itself is unchanged; it needs a separately proven launch route.
- Clients only talk to the server. The server decides what each client receives (interest management, §5).

## 3. Transport

- **Steam Networking Sockets** (reliable + unreliable channels over one connection, NAT traversal and relay via Steam,
  encryption, connection quality stats). Works for listen servers without port forwarding, and for dedicated servers.
- Ownership: clients connect with their Steam identity; the server checks it (Steam auth ticket). "Must own the game"
  is then enforced naturally.
- Our own compact binary protocol: no boxing, pooled buffers, bit-packed/quantised values, per-message-type channel
  (reliable ordered for state changes, unreliable sequenced for movement).

### 3a. Transport status (2026-09-27, v0.35.0)
- The game already contains Steamworks.NET 20.2 → `SteamNetworkingSockets` is usable from the mod, nothing shipped.
- **Pitfall found:** the game's steam_api64.dll is older (ISteamNetworkingSockets008 / Utils003) than its Steamworks.NET
  (asks for Sockets012). Old flat functions + newer function table = calls landing in the wrong function
  (CreatePollGroup killed the game; GetConnectionRealTimeStatus missing). `Net/SteamCompat` requests the DLL's own
  versions (v008/v003) for Steamworks.NET; the game itself never uses these interfaces. Status via the v008
  GetQuickConnectionStatus.
- `Net/SteamTransport`: listen/connect by IP or Steam P2P, in-process socket pair; per-connection receive with
  native field reads (no marshalling garbage); reliable/unreliable sends from one reusable unmanaged buffer.
  `Net/NetBuffer`: little-endian writer/reader, varints, strings; reads past the end never throw.
- One-process test (`net pair`): 1,000–5,000 messages each way, reliable + unreliable, through the real network stack
  (loopback): all delivered, reliable in order, every byte correct. Bursts above the default 512 KB send buffer are
  refused (k_EResultLimitExceeded) → the protocol must budget sends (send-rate config per connection, next step).

### 3b. Milestone 1 skeleton (v0.36.0)
- `Net/Protocol` + `Net/Mp`: listen server (IP) + clients; Hello (protocol + mod version must match) → Welcome (id,
  seed) / Reject; PlayerJoined/Left; 20 Hz player state (global f64 position + yaw, unreliable, sequence-checked);
  server relays to the others (no interest management yet); remote players as capsules placed through each machine's
  own floating origin.
- Bots = clients inside the host's process over real UDP loopback (`mp bots n`). **16-player session (host + 15 bots):
  all handshakes, ~3,800 relayed states/s, 0 send errors; per client 8.6 KB/s payload (unquantised, everyone to
  everyone).** Visual check: capsules circle the player in game.
- **Quantised states + interest tiers (v0.37.0, protocol 2):** position as integer cm (±21,474 km), yaw 16 bit → 18-byte
  state message. Server relays by distance between the two players: < 1 km every tick (20 Hz), < 10 km every 5th
  (4 Hz), beyond every 40th (0.5 Hz), phased by player id. 16 players: all near → 4,498 states/s (exact 20 Hz),
  **5.3 KB/s per client**; spread over 2/20/200 km → **0.23 KB/s per client**. 0 send errors.
- Next: Steam P2P join + ownership check (Steam auth ticket), real second machine / instance, then vehicles.

### 3c. First real two-machine sessions (v0.42.0, 2026-09-27, `tools/mp2.py`)
Desktop + the user's laptop (second Steam account) over the LAN, both in the seeded world, saves protected on both.
Laptop hosting and desktop hosting: handshake OK (same build, same seed), each machine shows the other player at the
true global position (0.0 m at the protocol's 1 cm precision) standing, after a 15 m move, and under simulated
180 ms / 2 % loss / jitter on both sides; 0 send errors. `tools/mpagent.py` runs the remote side (protected session,
bridge commands over stdin) so every multi-machine scenario can be scripted.

### 3d. Player models (v0.43.0, protocol 3)
Remote players use the game's own character (`syncScript.playerPrefab`, set up like the official MP: IK started,
multiplayer outfit, name tag) — without its per-player world-generation centre and with its rigidbodies kinematic.
Limbs: the local player's six IK targets (head, hands, feet, pelvis; relative to the body) + head pitch, zoom, open
menus, ~80 B at 20 Hz, relayed only to players within 300 m, applied through the game's `setPose`. Outfit: the game's
`GetData`/`SetData` triple, reliable, on join and on change, stored by the server for newcomers. Body facing from
`BodyRot` (as the official MP). Two-machine check: each side sees the other's character, facing correctly, in their
own outfit; ~300 poses applied per side in the window. Open: sitting in seats/cars (with vehicles), name tags.
**User play test (2026-09-27, desktop + laptop, joined as Steam friends over P2P, then 180 ms / 2 % loss / jitter
simulated on both):** "very smooth, no overcorrecting or stuttering that is noticeable; the delay is there but won't
hinder gameplay" — ~11,000 states and poses each way, 0 send errors.

Held items now also send their root pose relative to the holder's right-hand IK target (protocol 49 State flag
128: 13 bytes, signed millimetre coordinates ±2 m per axis and smallest-three rotation, following frame/lock data).
Copies with that holder model place the item after riding/driver hand targets are final, in `HandItemRender` at
LateUpdate order -9999, before IK; walking uses the targets already animated in Update. Grip changes ease over
80 ms, and switching between hand placement and normal playback glides for 120 ms. Proxy body/transform movement
yields during that placement. The original world state and Carried car-frame state are retained for server records,
distance checks, missing-model fallback and normal drops. `mp hands` exposes `heldItems` with net id, active status,
root-to-hand-relative-target distance and the received local pose for live validation.

### 3e. Shared cars, first cut (v0.44.0, protocol 4) — watchable scenarios (`tools/scenario_cars.py`)
Sitting in a driver seat shares the car (or `mp share`): its record group (35 items for the test car, 5.9 KB) goes
to the server once, which assigns a network id, keeps it for later joiners and sends it to everyone; they spawn it
through the game's load path with local ids. The owner simulates and sends 20 Hz states (stamped with the physics
step); everyone else shows it kinematic through PoseInterpolator; ownership moves only via the server (claim when
sitting in someone else's driver seat), stale-epoch states ignored. Two machines, watched live by the user with
captions on both screens:
- car appears on the other machine: 35/35 items, position difference 0.00 m;
- drive-by at 90 km/h past the watcher (5 m): **0 visible jumps** (largest deviation 4.1 cm), shown ~79 ms behind;
- same at 180 ms / 2 % loss / jitter: **0 visible jumps** (4.3 cm), ~170 ms behind.
Staging lesson: the test driver takes the road direction the car faces where it reaches the road; the viewer is now
placed from the measured direction, snapped to the real road.
**v0.45.0 (user-watched):**
- Display copies never run the game's physical consequences: all bodies of the group kinematic (incl. jointed parts,
  which aren't children of the car) and `carscript.DamageStuff` / `attachablescript.FallOFf` / `Detach` skipped on
  copies. Before: parts flew off the watcher's copy while driving (the vanilla MP symptom: teleported car → collision
  spikes → the game's crash roll). After: 34/34 parts stay on; 11,163 crash calls suppressed in one drive.
- Parts that come off on the owner (crash or wrench, `attachablescript.Detach`) are reported by index in the record
  group; the server gives the part its own network id; everyone detaches the same part through the game's `Detach`
  and shows it from the owner's stream; later joiners get the car + the same detaches. Crash scenario (head-on into a
  shared parked car at 90 km/h — the game's crash damage is a sudden change of *forward* speed, drops don't trigger
  it): 5 parts off on the owner, the same 5 on the watcher, landed at the same positions (0.00 m).
- Wheels on copies: the owner sends suspension travel, steer angle and rpm per wheel; the copy rebuilds the exact
  WheelCollider pose the game's wheelgraphicsscript would use (spin integrated from rpm). User had seen wheels
  hovering without it.
**v0.46.0 — user takeover test ("started glitching") fixed:** data showed 9 ownership changes (both machines' players
in the driver seat claimed every 0.5 s, the server granted every claim) and a world car doubled on the watcher (both
worlds generate it; two cars in one spot). Now: the server refuses a claim while the owner's player sits in the
driver seat (reported in every state); one claim per sit-down; when a car is shared, the other machines hide their
own copy of that world car (same model, within 4 m of where it was shared — parented to its building or not) with its
parts for the session and restore it at the end. Scenario 5: refused while the owner drives (0 changes), then one
clean hand-over, the laptop's copy smooth (0 jumps, 7.6 cm); scenario 6: one car at the spot (was two).
Not yet: engine sound on copies, passengers seated in another player's car, damage state (part conditions) after a
crash; hiding duplicates is a stand-in for spawn leases.

### 3f. World contents exist once per session (v0.47.0, protocol 5) — replaces the duplicate hiding
User feedback (2026-09-27): "getting messier than expected" — the duplicate-hiding stand-in kept producing edge cases.
Root causes from the code, and the foundation that replaces it:
- **Join = load the host's world.** The seed alone doesn't define a world: the start car ("random") is chosen from the
  clock (menuhandler.RandomStartCar), so two machines starting the same seed a minute apart differ. Welcome now carries
  seed + start car + map type; the client loads a fresh game with them (the connection survives the scene load; the
  client's world never autosaves) and sends Ready; only then the server sends players, outfits and objects.
- **Spawn leases.** All building contents come from poiScript.SpawnStuff (ItemSpawn with an unseeded
  UnityEngine.Random, FixedSpawn for fixed sets). In a session every machine asks the server first; only the lease
  holder runs it and shares everything it spawned (captured for 4 s — spawning can be spread over frames). Hosting
  starts: the host's spawned buildings count as done, its loaded items and cars are shared (things attached to
  buildings — bulbs, hooks — shared on their own; parts go with their item).
- **Shared objects follow the game's item streaming.** Far from every player the game writes items into the save's
  far store and destroys their GameObjects (itemPlaceRemoveScript); PlaceOne recreates them with the same idInSave.
  Entities are tracked by id: re-bound on recreation (display-copy mode re-applied), dormant while stored; states for
  a stored copy move its far-store record. (Before: shared objects silently lost their objects — seen as unshared
  duplicates at a building.)
- Any item can be shared; state is sent only while it moves (one final state when it stops); picking up another
  machine's item takes it at once (provisional ownership).
Results (two machines, tolerant item matching: same model within 0.3 m): **start area 168/168 items matched, nothing
extra on either side; a building first spawned by the laptop 50/50 matched (worst 0.10 m)**; car suite unchanged
(drive-bys 0 jumps at 0/180 ms, crash same parts, takeover), world start car taken over and moved: one car, where it
stopped. Join + load of the host's world: ~22 s.
Open: the host's far-store items aren't streamed to clients yet (only loaded items are shared at hosting start);
an intermittent part-off mismatch (2 of ~7 crash runs) → owners should periodically re-send attached-parts state.

### 3g. Passengers and items (v0.48.0, protocol 6)
- **Passengers:** a player's global position comes from their own machine, where someone else's car is only a delayed
  display copy; shown on a third machine with its own delay, a passenger would trail the car by metres at speed. So
  the player state carries the seat (shared object id + seat index in hierarchy order) and every machine parents that
  player's model to its own copy of the seat (as the official MP does with seats). Two machines, desktop driving the
  laptop as passenger: each showed the other seated in its copy of the car in 23/23 samples. A model destroyed with
  a streamed-out car is re-created on the next state.
- **Items:** picking up another machine's item takes ownership at once (W3: owner moved to the picker, the moved item
  shown at 0.00 m on the other machine). Scripted pickups can't be held (the game drops unless the button is held),
  so tests move the item as the new owner.
- **Crash fix (v0.47.1):** ConditionalWeakTable.GetOrCreateValue threw under the game's Mono (Activator) in road chunk
  reuse → world stopped loading after a few restarts; all uses construct directly now.

## 4. Authority and simulation

### The terrain mode (protocol 51; the old terrain in 52)

The world is generated from the seed alone, on every machine, so a joiner that generated a different shape than the
host would be driving on roads the host has never heard of. The **host owns the layout** and sends it in `Welcome` as
**one byte, immediately after the session settings snapshot** (the same offset the joiner reads it at). An old host
sends no byte, and the joiner then keeps the game's own terrain.

Two layouts are offered: **Crossroads** (the game as it ships) and **Main road only**. Both are the game's own road
generator driven by the two flags it already has (`roadGenScript.hasMainRoad` / `hasSideRoad`), so the world stays
deterministic from the seed alone. The **2023 terrain is not offered**: it is a different generator that cannot be
reproduced faithfully, and the only implementation of it is a third-party mod whose source is not published and
carries no licence, so it cannot be copied or re-derived here. `docs/TERRAIN-MODES.md` has the details and the
provenance.

**The save format changes.** The layout is written into the save's map name as a tag (`Dust Belt [MainRoadOnly]`),
because that is the name the game stores in the save file. The tag is stripped before the game's own map lookup
(and from the map instance's own record), so the game never sees it; a save written before this feature has no tag
and plays on the game's own world, exactly as before.

- **Release note:** saves written with the terrain mode carry a tag in the map name and **cannot be read by builds
  before 0.70**. The new build reads saves from **both** tag formats (`[MainRoadOnly]` and the older
  `[Terrain: MainRoadOnly]`), so a save written by either format loads here.
- **Anyone who touches the map name must know this.** The tag is stripped with `TerrainModes.StripTag`; a lookup
  that used the raw name would not find the map.


Protocol 54 adds Frame / Key / Delta ragdoll blocks to State. Protocol 53 adds two claim reasons, ItemNear and ItemContact: a loose item moving on one machine takes the still loose item it is about to hit, so both are simulated there (Entities.ItemPush). Protocol 52 uses uniform six-byte Claims: message, net id and a diagnostic producer byte. It also carries the host's terrain mode in Welcome (one byte, after the session settings snapshot): the world is generated from the seed alone, so the joiner has to generate the host's shape. Pickup takes
provisional ownership only for a display copy; the server applies the ordinary held/stored, hitch, crash,
worn and driver checks. Held transitions come from owner poses, and an Owner decision applies immediately.

Trailers (protocol 38): a hitch request names the car, trailer, slot and both epochs. The server checks reach and
epochs and routes the joint change to the car's current owner. Before granting a trailer to that owner it asks the
old simulator to freeze the trailer and its separate drawbar, remove the hitch constraint immediately, and acknowledge
with their final pose. It then grants a new epoch and delivers that pose reliably; the new owner stays frozen
until it arrives, aligns the coupler and matches the car's velocity before making the joint. Display copies keep the
hitch record without a physics constraint. Car ownership changes repeat this transfer for hitched trailers; independent
pickup, proximity and crash claims cannot split the pair. Unhitching leaves ownership as it is. A brief hitch handover
guard covers the game's speed-change crash and fall-death checks; `speed-anomaly` identifies hitch-related spikes.
`hitch-request`, `hitch-accept`, `hitch-refuse`, `hitch-frozen` and `hitch-ownership-claim` record decisions and reasons.
If the old simulator does not acknowledge within five real-time seconds, the server grants a fresh epoch using its
last pose and zero velocity; late old-epoch packets cannot reclaim authority. A joint command also has five seconds
to finish before the server releases the hitch. Required car/trailer Add records go to the new owner before the
control messages even outside the ordinary 3 km interest range. Deleting a tow car clears its trailers' saved hitch
records and unmounts loaded and far-store copies, leaving them claimable; a pending transfer is fenced and thawed.
`HitchTransferTimeout`, `HitchEnsureKnown`, `HitchReleaseOnRemove` and `HitchTransferredGripPose` are default-on A/B
switches for these recovery paths and destination-based drawbar placement. These changes retain the existing wire format.
Bridge tests: `mount hitchrequest <car> <trailer>`, `mount unhitch <car> <trailer>`, `mount hitchstate <trailer>`,
`mount hitchhealth <car>` and `mount hitchevents`. `mount hitch` also uses the owner route in a session.

- **Server is authoritative** for all shared state: which items exist and where, car state, doors/usables, fluids,
  inventories, time/weather, the save.
- **Distributed physics with ownership** so the server isn't the only simulator:
  - a car is simulated by its **driver's client** (smooth control, client prediction); the server validates plausibility
    and relays it;
  - loose items are owned by the nearest player (or the server when nobody is near); ownership moves when players move;
  - the owner sends state, everyone else interpolates.
- On a dedicated server (no local player) what no client owns is paused (see §4a); if the server ever simulates regions
  itself it needs the world generated there — the reason the multi-centre world generation is still kept fast.

## 4a. Players far apart (user requirement 2026-09-27: "hundreds of km", Minecraft-like feel)

Players must be able to play anywhere — together, or hundreds of kilometres apart — in one shared, persistent world,
joining and leaving at will. The *feel* is Minecraft's; the *mechanism* can't be, for reasons in the code:

- **Float precision.** Unity positions and PhysX use 32-bit floats; spacing between representable positions:
  5 km ≈ 0.5 mm, 100 km ≈ 8 mm, 300 km ≈ 3 cm, 1,000 km ≈ 6 cm. Cars/wheels/stacked items jitter at cm level —
  why the game shifts its origin (`mainscript.visszarakva`) whenever the player is 2.5 km out.
- **One origin per process.** `visszarakva` is a single global; every generator, spawner and item converts through
  `GlobalFromUnityPos`/`UnityPosFromGlobal` with it. One game process can't keep two areas 300 km apart both near
  an origin without rewriting large parts of the game.
- **Measured cost** (mpbench): generating around every remote player is what loads the host (105 FPS alone → 43 FPS
  with 10 generation centres).

Consequence (Minecraft's server simulates all loaded chunks itself — with doubles and simple physics; we can't):
- **Each client simulates its own surroundings** in its own origin (terrain, colliders, physics of what it owns). The
  world there is generated locally and must be bit-identical on every machine — hence the determinism work
  (`tools/determinism.py`, SceneryTileSync v0.33.0).
- **The server is the state authority**, not a physics simulator for far areas: it holds entity state, ownership,
  the save; validates what owners report. A listen host no longer generates around remote players, so its cost is
  its own area + networking.
- **Areas nobody is near are paused** (like unloaded chunks): state stays in the server's save, nothing simulates.
- **Dedicated server option (untested):** Unity supports several physics scenes; one per far-apart player group, each
  with its own origin, could let a dedicated server simulate regions itself. Collides with the one-origin game code —
  needs its own investigation before we rely on it.

**Open challenge, to be designed carefully: players together / overlapping.** When players meet, drive in one car, or
split up, shared objects need exactly one simulating owner with smooth hand-offs; passengers ride in a car simulated
by another client; both sides must agree on who owns an item in contact with two players' objects; latency between
two nearby owners (collisions between two players' cars) needs a policy. Anti-cheat: owner-reported state needs
plausibility checks on the server.

## 4c. Why the official MP desyncs (read from its code, 2026-09-27) — and what we do instead

Symptoms reported by the user: cars jumping, items at different places on different clients, buildings different,
items spawned differently, "black shiny cars without anything on them".

| Official MP (code) | Effect | Ours |
|---|---|---|
| Non-driving machines apply car updates with `GotSync`: transform position/rotation **set directly**, no interpolation (only carried items are smoothed); the car's rigidbody stays **dynamic** and `RecCarVelocity` injects the sender's velocity | local physics + periodic teleports → jumping cars | one simulator per object (driver/owner); everyone else displays it kinematically, interpolated ~100 ms behind, never simulating it |
| Who's in charge of a car is decided **per machine** (`ThisOneInChargeCar`: compare "what doing" levels driving/pushing/nothing, 1 s timeout), no arbiter | both or neither machine in charge → fighting updates | ownership granted by the server, explicit hand-over messages |
| Clients never spawn building contents; they create items from host messages and then **ask for each item's state separately** (`SendAskItemData`, rate-limited, one id at a time) | an item/car shown before its state arrived stays default (hypothesis for the black cars without parts) | complete save record, sent reliably as a group (car + attached/locked parts), applied through the game's load path in one step |
| World generation had timing-dependent scenery (game bug, fixed in v0.33/0.34) | buildings/scenery differ between clients | deterministic generation verified with tools/determinism.py; version + seed + schema checked at join |

## 4b. Entities: identity, spawning, state (from the code, 2026-09-27)

- **Identity.** Every item/car is a `tosaveitemscript` with `idInSave` (uint, allocated from `savedata.lastID`). The
  game's own cross-references use it (seats, usables, tanks, tank caps, phys locks, attachment parents, the official
  MP's messages). The official MP works because only the host creates items (`poiScript.SpawnStuff`: clients return
  early), so everyone shares the host's id space.
- **Our rule:** `idInSave` = network id on every machine. The server grants each client an **id block** (e.g. 2^16 ids);
  a client creating an item (building contents it was given the lease for, a purchase, a crafted item…) allocates from
  its block — globally unique without a round trip, and every id-based reference in the game keeps working.
- **Spawn authority for places (Minecraft's "generate once, then serve"):** the host can't spawn building contents
  200 km away (it doesn't load that area). The server gives a building's **spawn lease** to the first client that
  arrives; that client runs the game's own ItemSpawn/FixedSpawn and reports the items; the server stores them. Later
  visitors get the stored items instead of spawning (`poiClass.spawnedItems/spawnedFixed` already mark this).
- **State = the save record.** `savedatascript.SaveToDictionary(item, itemDataClass)` writes an item's complete state
  (part conditions, fluids, usables, car/engine, attachable, colours, randoms, phys locks, …) and
  `LoadStuff(savedata, id)` loads it into a spawned item. An item's network snapshot is its save record: exact and
  complete by construction, the same data the save file keeps. Frequent changes (positions, car inputs, doors) go as
  small messages; the record is for join/spawn/area entry and resync.
- **Verified (v0.38.0, `itemsnap test`):** record → RecordCodec → spawn a copy under a new id via the game's own
  SpawnItem/InitAfterSpawn/LoadStuff → re-capture: **117 of 120 items identical** (incl. a complete car); the rest are
  cross-item relations (physLocks: items locked on a pallet / in a car reference each other's ids) and rope segment
  positions — so snapshots go out as **groups** (an item + everything locked/attached to it), which our shared-id rule
  makes work unchanged.
- **RecordCodec** instead of JSON/BinaryFormatter: JsonUtility isn't round-trip exact for doubles (printed
  −947.0699462890625 as …0624); BinaryFormatter from the network can run a peer's code. RecordCodec walks the same
  fields BinaryFormatter saves, doubles as raw bits, types only from the local structure; schema hash of the field graph
  compared at join. Records: 94–277 bytes (avg 126) vs ~600 in JSON.
- The game already streams items by these records: far items live in `savedata.itemData` (+ `itemChunks` index) and
  `itemPlaceRemoveScript.PlaceOne` spawns them when a player comes near — the network can feed that store directly.
- **The server's world save** is then just the union of these records (+ per-player data), i.e. the game's own
  save format — dedicated server saves are ordinary saves.

## 4d. Players together (overlap) — design agreed 2026-09-27

Foundations:
1. **One writer per object, granted by the server**, with an ownership epoch; updates from an old owner are dropped.
   No machine decides ownership by itself (the official MP's `ThisOneInChargeCar` mistake).
2. **Ownership groups**: physically connected things share one owner (car + attached parts + items locked in/on it,
   from the game's own physLocks/attachments).
3. **Non-owners only display**: kinematic proxy, adaptive interpolation buffer, sequence-checked; collides as a moving
   wall, never pushed locally.
4. **Ownership follows interaction** (drive, hold, push), provisional locally, settled by the server.

| Situation | Simulated by |
|---|---|
| Driving | the driver (own car instant at any ping) |
| Passenger | the driver; the passenger's machine shows the car as proxy and the passenger is parented to the seat (game code: `transform.parent = sitPos`) → rides smoothly |
| Picking up an item | the holder (instant local grab; the server only settles same-instant conflicts, loser snaps back) |
| Taking from someone's trunk | ownership moves from the car's group to the taker |
| Car hits loose items / a parked car | the moving car's owner takes the hit objects on contact (one simulation) |
| Nobody driving / rolling | nearest player with hysteresis; asleep = nobody (stored state) |
| Driver exits while rolling | ex-driver until it stops or they leave; then hand-over with full state |
| Owner disconnects | server reassigns from last state to the nearest player, or freezes into the save |
| Two driven cars touch | **A**: each simulates own car, contact impulses forwarded to the other owner; **B** (slow sustained contact, e.g. pushing): one machine simulates both until they separate |
| Standing on someone's moving car | OPEN: the game doesn't parent a standing player to a vehicle — needs a test |

Proof, not claims: simulated 150–200 ms lag + jitter + loss on every test, a desync checker comparing clients' objects
with the server's records, scripted scenarios (collision, hand-over, disconnect mid-drive) on two instances, then real
players.

### Remote car display, measured (v0.41.0, `tools/vehlab.py`)
Real car driven at 90 km/h, its pose streamed 20×/s through a real Steam socket pair under simulated conditions; a
ghost moved only by what arrives (`Net/PoseInterpolator`: adaptive delay from measured jitter, Hermite with velocities,
capped extrapolation, clock steering ±5 %, corrections blended out; samples stamped with the physics-step time).
~2,600 frames per condition, deviation = per-frame departure from smooth motion:

| Network | visible jumps > 20 cm: ours / snap-to-latest (official MP) | deviation p99 ours / snap |
|---|---|---|
| 0 ms | 0 / 997 | 1.6 cm / 176 cm |
| 180 ms RTT, 2 % loss, 10 % +40 ms | 1 / 937 | 1.5 cm / 234 cm |
| 300 ms RTT, 5 % loss, 20 % +80 ms | 0 / 934 | 1.4 cm / 332 cm |

Display delay ≈ 85–130 ms + one-way latency. Side finding: the game's cars have no rigidbody interpolation (transform
moves on 50 Hz physics steps, frames at ~100 FPS), so a remote car shown through the interpolator is smoother than
the real one looks to a bystander. INVALID runs noted in bench/NOTES.md (a failed build hidden by the tool).

## 5. Replication

- **Entities**: players, vehicles (+ their parts), loose items, usables (doors, switches, taps), tanks/fluids, NPC/AI,
  world clock/weather.
- **Interest management**: each client only receives entities within its relevance range (players far apart along the
  road don't cost each other bandwidth). Needed for 10+ players.
- **Send rates by relevance/motion**: near + moving often, far/sleeping rarely or never; sleeping items are not sent.
- **Snapshots + deltas** with quantised global positions; late joiners get a baseline of their area.
- **World-changing events** (item picked up / destroyed, POI item spawned, part attached) are reliable, ordered, and
  validated by the server.
  An attach (`Entities.AttachSync`) is the game's own save record (where the attach point is) plus, since protocol
  31, the attached object's pose in the frame of what the point hangs on: the game's loader leaves the object where it
  is, so other copies stayed where their last smoothed pose had them (0.9 m off with MountAnything). A/B: `AttachPose`.

### Visible state changes (2026-10-19, protocol 37)

Every owner category (including cars/trailers) compares with the last **sent** pose at `Protocol.StateHz`: more than
1 cm or 0.3 degrees sends, so slow drift accumulates. An unoccupied car already at rest uses a 2 cm position
threshold to restart the stream, then returns to 1 cm while moving. Awake alone never sends. Final rest, velocity crossing rest
(0.05 m/s), held/stored/driver changes, owner confirmation and signal/spray repeats remain exempt. Already resting/
sleeping, stored, attached and dormant objects stay silent after their final state.
Only awake jitter gets one-second pose keepalives, for at most five seconds since its last real visual change;
keepalives do not reset that deadline. The tail ends with one reliable zero-velocity final state, then silence until
another visible change. Driven cars refresh every 0.5 s only while the local player occupies the driver seat,
before the unchanged one-second driver lease expires. Visible movement keeps its full 20 Hz opportunities; existing relay interest and ownership rules remain. Door/light/usables records keep
their independent Edit/LiveUsable/Resync paths; these filters do not thin them.

The marks sample positions in centimetres: Car06P in the quiet 1661 s mark varies by one bin and 0.198 degrees yaw;
Car04's 698 quiet samples have unchanged position/yaw, speed at most 0.001 m/s, yet send 19.9 Hz. They cannot measure
sub-centimetre jitter, pitch/roll or suspension. Parked wheel rpm within +/-2 is sent as zero; suspension must change
by more than 2% of travel, steering by one encoded step (0.5 degrees), engine rpm by more than 50, horn by more than
4/255. Engine/signal flags and gears stay exact; parked torque jitter alone does not send. Wheel/engine tolerances
still need visual confirmation. Physlock transitions always send a reliable final pose, even while moving. Protocol
39 adds an explicit lock flag and container reference; a locked item uses its shared parent's frame, and suppresses
keepalives only while that relative pose stays unchanged. Unshared parents use world poses. Unlock clears the quiet
tail and sends immediately; a new ownership epoch confirms the lock again. Final states are retained for joiners,
with world fallbacks and far records following the parent. Telemetry includes `physlock-lock`/`physlock-unlock`.
Slotted car
parts also skip loose poses when attached by a joint (`parentAtAttach` can be false); vehicle/trailer roots retain poses.

Rabbit animation compares flags/epoch and visible run (clamped at 0.5 where the animator saturates; delta >2/200).
Protocol 39 RabbitState (90) sends reliable changes only: u32 net/epoch, flags, run, seen, 3 x i32 global target
centimetres (24 bytes). Target motion alone does not send: the current global target travels with the next
flag/run/seen/epoch snapshot. At handover the departing owner sends its current target once with the
new epoch; the server accepts it only from the previous owner within the handover window, and the new
owner applies it while freshly in control. Seen changes also send. Other old-owner updates are fenced. The server caches the snapshot for Add/first interest and relays changes to known viewers within 1 km;
clients cache it through stream-out and restore it on placement. No periodic animation refresh is needed for
live or dead rabbits. Other dead AI animation also has no periodic refresh. Limp limbs use 1 cm/0.3 degree thresholds.

Protocol 54 puts a ragdoll block after the car signals at the end of every State. It starts with `u8 n`.
Zero means no limbs and nothing follows. Otherwise `u8 mode` selects one of three forms. `rot` is a seven-byte
smallest-three quaternion.

| Mode | Bytes after n and mode | Pose frame |
| --- | --- | --- |
| 0 Frame | n x (3 x f32 + rot) | The state's frame |
| 1 Key | u8 anchor, u16 limb-name hash, 3 x f64 anchor position + rot, n x (3 x i16 mm + rot) | The Key's global anchor pose |
| 2 Delta | u8 anchor, ceil(n/8) mask bytes, masked limbs x (3 x i16 mm + rot) | The matching Key's anchor |

A Delta is cumulative: it includes every limb that moved since its Key. The Key plus any one Delta rebuilds the
whole pose, so losing a Delta does not lose the changes in it. Unmasked limbs come from the Key.
The anchor byte identifies the Key. A Frame block is 2 + 19n bytes; a Key is 36 + 13n;
a Delta is 3 + ceil(n/8) + 13 bytes per masked limb.

Owners send a Key on the first state of an epoch, on entering a player's interest and on the final at-rest state.
While streaming they send one every `RagdollKeyEveryS` (2 s), or when a Delta exceeds `RagdollDeltaMaxShare`
(0.75) of a Key. A limb outside the i16 millimetre range falls back to Frame if a fresh anchor cannot fit it.
Vehicles' limb sets and `Entities.RagdollDelta = false` also use Frame. A Key becomes the baseline only when its
State is actually sent. Key States travel reliably; the server caches each entity's latest Key and forwards it
reliably without thinning. A stored Delta is replayed only after its matching Key.

Copies keep the Key while streamed out and rebuild every limb from it plus the latest Delta. A missing Key
discards the Delta. A limb-name hash mismatch is ignored and logged. Mods can supply limbs with API 3
`MpApi.RegisterRagdoll`; see [MODDING-MULTIPLAYER.md](MODDING-MULTIPLAYER.md).
The unused drifting creature root gets an identity placeholder, preventing a spurious 50 m bounds rejection.
Copies skip that root slot and capture the frame before placing any limb, then converge exactly to the received
pose. This avoids root drift or a root slot changing subsequent limbs' frame.

Measured on two copies with one dead rabbit (14 limbs, twitching at about 20 states/s), ragdoll data fell from
5.5 KB/s to about 1.9 KB/s. These are ragdoll block rates, not total network traffic.

`CompactState` (98) selects `randomFlyer` roots without pickup/car/rabbit/NPC/ragdoll state, and live upright
`nyulscript` ground animals (including pickupable rabbits while loose): varint net/epoch, u16
sequence, u32 sender milliseconds, 3 x i32 global centimetres, u16 yaw and a moving bit. It is 24 bytes with one-byte
ids, 25 at net 595, versus the marks' 40/41-byte normal state. Receivers reconstruct velocity from stamped positions
for the existing interpolator; rest is zero velocity, old stamps are rejected and epochs reset that history. Both
peers require protocol 49; the RabbitState layout dates from 39, and CompactState itself has no layout or flag changes.
Held, stored, spraying, dead/ragdolled animals and NPCs retain the full state path. Ground copies derive all three
velocity components, preserving hops in the existing Hermite interpolator. In-place eating/sitting and solver
velocities cannot restart the pose tail or velocity-transition sends; real position/yaw changes retain full cadence.
Item traffic and category telemetry include both messages.

`netcat`/telemetry `bandwidth` adds cumulative `idlePose`, `physlocked`, `attachedPart`, `rabbitAnimation` and
`ragdollPose` skip counters. Only numeric counts and a fixed message label are added. `Entities.WasteFixes` still
bypasses pose filtering. No send budget, priority scheduler, rate cap or ownership policy is introduced.
Final states carry a flag in the existing flags byte (normal bit 32, compact bit 2), adding no payload bytes. They
travel reliably owner-to-server and server-to-known-viewers, bypassing relay thinning. Both hops reject older
same-epoch timestamps, including a delayed final overtaken by new motion. The server retains the complete final
pose/visual/limb payload and replays it reliably after Add/Resync on join or first interest; copies retain it for
stream-in. The existing stored pose and changed-only reliable records/resync remain the late-join baseline, so no
periodic pose is needed from silent owners. Retained finals are cleared by new motion and ignored across epochs.

Expected owner payload rates for the marked cases (B/s, excluding records/events, transport framing and reliable
retransmissions). These are conditional estimates after visual settling, not a replay of the marks' incomplete
pose samples. A tail has at most four one-second refreshes plus one reliable final, at most five payloads total:

| Marked traffic | Before | During awake tail | After tail |
|---|---:|---:|---:|
| Parked car, unoccupied | ~1,180 | 59-60/s; <=295-300 total | 0 |
| Driven parked car | ~1,180 | 118-120/s while driver seated | 118-120/s while driver seated |
| Bat at 20 Hz / visually still | ~800 | 500 moving / 25 still; <=125 tail total | 500 moving / 0 still |
| Locked or attached item | ~800 | No keepalive tail | 0 |
| Loose item | ~128-356 | 40-41/s; <=200-205 total | 0 |
| 18-36 loaded quiet live rabbits | ~2,500 | One 25-byte final each, plus actual animation/target changes | 0 while unchanged |
| Corpse scene (~249 bytes/state, 2 owned bodies) | ~4,900 | 249/s per awake body; <=1,245 total each | 0 |
| 140-290 previously silent owned items | 0 | 0 | 0 |

The live-rabbit mark's final window has 647 rabbit-category messages and 24,300 outgoing bytes, including 79
20-byte RabbitStates: 568 normal 40-byte poses. Keeping every message gives 568 x 25 + 79 x 24 = 16,096 bytes
with protocol 39, or approximately 810 -> 536.53 payload bytes per rabbit-second (six owned, nominal five-second
window). Removing unchanged animation refreshes saves more, but the mark cannot identify that count. If treating
647 as pose packets alone, the codec comparison is 862.67 -> 539.17 bytes per rabbit-second (37.5% fewer pose bytes).
These projections exclude transport framing, ACKs and retransmissions. Counts alone cannot identify which owned items are loaded or visibly moving.
Two-machine tests must confirm parked visuals, loss/late joins, final corpses and whole-scene totals.

### Live item and building usables (protocol 48)

`Entities.LiveUsables` hooks the game's `usablescript.SyncMulti(true)` point. Items use entity id, ownership epoch,
item index and usable index in the save's registered order. Buildings use their existing `PoiKey`: generator,
position id (three doubles) and usable index. Both reuse `LocalUsable`, `RemoteUsable` and `UsableMotion<PoiState>`:
changed quantized values send unreliably at up to 60 Hz, with a 0.3 s quiet period and 0.1 s render delay. The
cadence preserves opportunities through uneven frames without replaying missed samples. Unchanged values send nothing.

Message 93 retains its item format. A zero net id now selects a building address followed by F32 xRot/yRot/slide,
varint turn and F32 tuned, then u16 sequence and F64 session stamp. These floats are expanded from the same u16
quantization used by item controls, allowing an unloaded host to validate, relay and retain motion without prefab
bounds. Reliable building message 52 retains its address/raw state and appends u16 sequence and F64 session stamp.
This wire extension requires protocol 48 on both copies.

Item validation and ownership remain unchanged. Buildings still use `CheckPoiUsable`: whoever moves a control may
send it, subject to the existing generator/index/value checks and 150 m building reach. Its per-player bucket is
180/s with burst 240 to cover live linked gates and final records. Server sender sequences and stamps reject old
motion, and a per-building stamp fence prevents a late previous actor from overwriting newer input. The server
assigns one relay sequence across live and final packets. Existing POI interest rules exclude the actor, relay to
nearby players and always update the host; distant players catch up from `ServerPoiUsables` on approach.

Receivers sample the shared timestamp motion buffer each frame. Building poses use Refresh; switch input events
use UpdFromMulti once on receipt. Its unconditional JustTurn is never repeated for rendered motion. Rotation and
slide endpoint sounds use transitions of the rendered values, so leaving/reaching a limit sounds once, including
on the exact final pose. The local player's held control is protected and remote applications cannot echo input.

After 0.3 s without change, items send their exact reliable Edit/Resync record as before; buildings send their exact
raw state reliably in 52. Both paths reject delayed unreliable motion after that final state. The server retains
each accepted building state for joiners/interest catch-up, and `KnownPoiUsables` still silently restores unloaded
buildings after both FStart and save_poi.Load. No ownership lease is added to a building control.

`mp liveusables [on|off]` controls live sending (default on); buildings still send reliable changes with it off.
`mp usables <net>`, `mp dragusable <net> <item> <usable> <seconds> [hz]` and `mp usableprobe ...` remain available.
For buildings use `mp poius [radius]`, `mp poiu <gen> <x> <y> <z> <index>`, `mp poidrag <address> <seconds> [hz]`
and `mp poiprobe <address> <seconds>` / `mp poiprobe`. The driver uses the real game Rot/Slide calls every frame;
building swings run from the current pose to the opposite endpoint, without an initial jump. `poiu` exposes exact
values, last-change age, live send/receive Hz over the trailing second, accepted live count and interpolation state.
Building probe rows are `[st, [xRot,yRot,slide,turn,tuned], receiveHz, interpolating, receivedCount]`; item rows are unchanged.

Focus gives a crosshair building usable its own card before entity fallback: content-allowlisted GameObject name,
"building part", full PoiKey, current values, change age, trailing-second live rates and interpolation status.
Text refreshes at the existing half-second cadence; rates use fixed rings and no per-frame allocation.
The two-copy door check (doorsmoothcheck) checks a 1.5 s host swing on already running copies: >=30 accepted
live updates/s, interpolation, <=3.5 degrees per rendered frame at steady 60 FPS, and exact final convergence.
Its `--selftest` is offline; real two-copy measurements require running games and were not run for this change.

`ResyncBudgetMs` (0.5 ms by default) now covers all entity captures together in a frame. Initial phases remain spread
over the interval, and the first deferred entity gets the next frame's first turn. Each scheduled pass advances at
least one item; a single capture can exceed the allowance, then remaining entities wait until the next frame.
`mp entities` reports `resync.budgetFrames` alongside the existing capture counters when the shared budget runs out.

## 6. Determinism risks to close (from our code analysis)

World geometry is exact, but some things depend on **timing**, which differs between machines:
- scenery tiles cache "roads/POIs near" at the moment they're created — if a road or building near them isn't generated
  yet, scenery can differ (vanilla has this race too);
- POI placement waits until "roads done" at that place;
- items spawned inside buildings.

Plan: make these order-independent (e.g. an area only builds scenery once its roads/POIs are complete) and make the
server authoritative for anything persistent (items). A checksum of each generated chunk lets client and server detect
divergence in testing.

## 7. Saves and death

The server owns the shared world save (items, cars, buildings' state, time). Per-player data (inventory, body, stats)
saved server-side per Steam ID. Clients never write the shared world.

A multiplayer death is accepted at the public `fpscontroller.Death` call. The mod records the player's global position
and yaw there, before `Death2` creates and launches the ragdoll and before the death screen moves the player transform
under the corpse. The corpse itself is shared through the ordinary item lifecycle only after `Death` completes, so the
existing limb transport carries its fall and final rest pose.

The per-player record is not a periodic stream during a death. The death sends exactly one record, at the recorded
death point; the periodic record waits for the respawn, so the moving follow-camera pose can never replace it. The
respawn sends a fresh alive record at the new point. The rules are `PlayerSaveBudget` (a pure state machine, tested in
`tools/unit/DeathStateTest.cs`); the counters (`deathRecords`, `suppressedWhileDead`, `respawnRecords`) are in the
bridge's `mp playersave` output.

When the session respawns the player, the player and its normal carry transforms return to that point just before the
original `Respawn` calls `Revive` and `DropAll`. The game's own drop path releases the right-hand, dragged and inventory
items there instead of at the body's flung endpoint. The ordinary 40–60 m respawn movement follows afterwards.
`Entities.DeathRagdollShare` and `SessionRules.DeathDropAnchor` are runtime A/B switches; both default on. Seated deaths
use the same public hooks and leave seat exit to the game. `tools/deathsync.py --ab` exercises the corpse and drop paths
on a real death over the normal Steam relay.

The anchor is a state machine too (`DeathAnchorState`): a non-finite position is refused, a conversion or placement
failure is counted as a fallback and named in `mp deathstats` (`fallback`, `refusal`), and nothing claims an anchored
drop that did not happen. The anchor belongs to one death and one session: a new session, a rehost and a session stop
clear it.

### Session settings and developer access (protocol 50)

The host owns the session's settings: the teleport-request switch and its cooldown, where joiners are placed, and the
developer-access policy (everyone, host only, disabled). The policy is **session-only** and is not saved. The snapshot
(message 111) carries the settings and a session token with a revision; the password itself is never on the wire, only
the flag that there is one.

A peer that was admitted but is not ready yet (it is loading the host's world) can miss a change published while it was
loading, so the host sends the current snapshot again when the peer says Ready. A client acts on the developer policy
only when it is ready and holds a snapshot.

The client is **bound to the host's session token**: the token from `Welcome` is the only one whose snapshots the client
applies. A snapshot with a foreign token is refused even when its revision is higher — a client has exactly one host, so
a different token is not an update. A different host is only taken on the explicit new-host path.

**What the developer-access policy covers.** The policy (everyone / host only / disabled) governs the *developer
commands*: the debug bridge's mutating commands and the game's own dev-mode flag (`BDevMode`), which the mod forces off
for a peer the policy does not permit and restores on teardown. The panel row says "Developer commands" for that reason.
It does **not** cover the mod's own `Settings → Revamp` page, the bridge's read-only queries, or the marks/reports
tooling: those are not developer commands and stay available to every peer. The policy is session-only and is never
written to the settings file.

The host's password is not written to disk. The old settings file's plaintext `hostPassword=` line is removed on startup
(the value is kept in memory for that session only) and the settings file keeps only the key. The password never appears
in the admin log, in the anomaly watch's last-dev-command line, in the bridge's diagnostics or in a report: a command
that carries it is logged with the value replaced.

## 8. Dedicated server

Same game + mod started headless; server mode disables rendering/audio/local player, generates around all players, runs
the physics it owns, saves, and exposes an admin console (the mod's debug bridge becomes the admin interface).
Performance budget is world generation + physics only → the multi-centre world-generation work is the main lever.

## 9. Testing at 10+ players

- **Fake generation centres** (`fakeplayers`, done): server world-generation load for any player count.
- **Bot clients**: lightweight processes speaking our protocol (no game), driving scripted routes and interacting — for
  bandwidth, server CPU and ownership hand-offs at 10–30 players.
- **Real instances**: 2–3 game instances on one PC/LAN for correctness; real group tests last.
- **Divergence checks**: chunk checksums client vs server.

## 9a. 10+ players: load, bandwidth budget, profiling leads (2026-10-14)

The target is 10 players tested and stable, with headroom to 16 (§11). What is measured, what is estimated, and
what is only a lead.

**Load test (measured).** `tools/loadtest.py`: the desktop hosts, the laptop joins as a real client, and bots join in
steps. Each bot shares a full car (the same record a real player's car uses) and streams states at the players' rate,
all circling the host within 30–100 m: every car near every player, the worst case for the host's relaying. Bots run
inside the host's game on loopback connections; their own CPU is timed separately and left out of the host's cost.
Baseline at 16 players (2026-10-08, Steam relay): host frame 19.1 ms median / 34.9 ms p99, host upload 324 KB/s,
client frame 22.7 / 32.9 ms, no visible jumps on the client's copies; networking CPU 56 ms/s on the host, 48 ms/s on
the client. Most of the networking CPU is moving copies as physics bodies — real work, accepted as the baseline.

Still to do for 10+:
- bots as separate processes or on another machine, so the host's game isn't also running them;
- players spread out (different areas, not all near the host), which exercises the far store, hand-offs and
  building leases instead of only relaying;
- a session of 2–3 hours with joins and leaves, watching memory, the far store and the anomaly log;
- the same steps at 150–200 ms latency with loss (§11.7).

**Bandwidth budget per player (measured and estimated).**
- Measured: in a real three-player session (host + 2 clients) the host sent a median of 24–40 KB/s, peaks
  (p95) 40–68 KB/s.
- Record sizes: a full car's share record is about 6 KB raw, 2 KB compressed; its state records about 7 KB raw,
  0.7 KB compressed; its parts list (census) about 0.9 KB raw, 0.3 KB compressed, and 4 bytes as a hash.
- Estimated (owner reconcile design, not built): each car copy re-checks its parts with its owner every 30 s using
  the 4-byte hash; at 10 players with about 2 cars each and every player seeing about 18 other cars, that is about
  **27 B/s per player** upstream, and about 270 B/s each way through the host. Owners answer only differences. A
  repair (a copy rebuilt from the owner's full record) costs about 2 KB; the worst case, every copy of one car wrong at
  once, is about 18 KB through the host once, bounded by a repair budget of 32 KB/s on the host.
- So the steady cost of checking is far below 1 % of the measured traffic. The budget to watch is the host's
  upload, which grows roughly with (players − 1) × what each player sees; interest management (only players near get
  state records) and send-rate tiers are the levers.

**Profiling leads from the game's assets (not measured costs).** A scan of the game's serialized assets gives
upper-envelope counts. They say where to look, not what anything costs at run time:
- **Cars are large hierarchies.** A full car is about 1,000 objects (985–1,211 for the full cars scanned), with
  hundreds of colliders and 200–280 mesh renderers, and **no LOD groups**. Every player's car is a full copy on every
  other machine. Lead: count the active components of a car copy at run time and the cost of drawing and moving
  10–16 of them, near and far.
- **Lights with shadows.** 875 of the 887 lights in the assets have a shadow type set, many of them vehicle lights.
  Lead: count live shadow-casting lights with several cars at night; a copy's lights may not need shadows at a
  distance.
- **Buildings.** Building hierarchies have hundreds of renderers and colliders; a few have LOD groups. Lead: frame
  cost when several players load different buildings at the same time (each loads only what is near them, but the
  host relays all of it).
- None of these were measured in a session yet; profile before optimizing.

## 10. Build order

1. Transport + handshake (Steam identity, mod version check, seed) + players visible and moving (listen server).
2. Vehicles (driver-owned) incl. entering/leaving, parts.
3. Items and usables (ownership, pickup, spawn/remove authority).
4. World events + time/weather + saves.
5. Interest management + send-rate tiers; bot clients; 10+ scale tests.
6. Dedicated server mode (headless) + admin console.

## 11. Decisions (user, 2026-09-27)

1. Transport: **Steam Networking Sockets**.
2. The official multiplayer is **replaced** while the mod is active.
3. Target: **10 players tested and stable, designed with headroom to 16**.
4. Join model: **drop in / drop out** any time; the server keeps the shared save.
5. Players far apart (hundreds of km), Minecraft-like feel (2026-09-27) → §4a.
6. **Overlap / car-vs-car contact: A+B** (2026-09-27): impulse sharing by default (each driver simulates their own
   car, contacts forwarded to the other owner), contact takeover (one machine simulates both) for slow sustained
   contact such as pushing. Worst case = a softer crash, never teleporting.
7. **Target network: stable at 150–200 ms ping** (across continents; the host too). User reference: Garry's Mod.
   Consequences: adaptive interpolation buffer (~150–250 ms behind, grows with jitter) + capped prediction; provisional
   local ownership (a server round trip is 300–400 ms); every MP test runs at 150–200 ms fake lag + jitter + 1–2 % loss
   (Steam sockets' built-in fake lag/loss) in addition to a clean baseline. Lag compensation (server rewind) for
   interactions with others' objects is on the list, as in Source/GMod.
   **Hits on players (v0.66.9, Net/HitCheck.cs):** a hit is decided on the attacker's screen against the body drawn
   there (what you see is what you hit); the host keeps ~3 s of every player's positions and rewinds the target by the
   attacker's ping + the draw delay + slack, then checks the shot's line passed the body (a blade or a creature: within
   reach) and that it came from where the attacker was. A creature's hit names the creature: the attacker must own it,
   and the host's copy of it must be near where the hit says it was. Impossible hits are refused and logged ([anomaly]). Walls are
   not checked (the host doesn't have every player's surroundings). Hits on objects (cars, creatures, breakables) are
   still the attacker's word.

### Join loading (introduced in protocol 45; current protocol 54)

`MpClient.Ready` starts replication; it is not gameplay readiness. With `joinLoading` enabled, `JoinLoadingState` gates a joiner-only overlay and audio lease. The scene/player initialization, requested terrain meshes/colliders and road chunks, a static floor stable under the player for 0.5 s, and initial object application must all hold. `fpscontroller.Move` is suppressed and the player body held during loading; teardown always releases the body and restores audio/cursor state.

Ready optionally appends flag 1 and the quantized join position (flag 0/absent retains the old path). Reliable message 110 sends phase 0: varuint count + u32 net IDs for the host snapshot within 300 m, including parents of detached parts, then ordinary Add/part/resync/state records through existing Known/far-store paths, then phase 1 as an end marker. The client checks registry application and nearby far-store placement, plus the TPA inbox. Geometry is ready before requesting records. No timer grants readiness and no send limit is added. The existing player-joined message supplies the host display name before Welcome.

Escape/`mp loading cancel` leaves to the menu; 60 s without a changed fact, backlog, download byte count or object count offers `mp loading continue`/Continue anyway. Continue bypasses the loading gate explicitly, restores audio and resumes the old join/ground behavior; `ready` remains false. `mp loading` exposes stage timings/counts and scene, geometry, ground and initialSync evidence. Off keeps the old join path with no overlay, audio lease, body hold or manifest request.

The joining screen leases the game's `Canvas/UIOVERLAY/2/TextLoad` once per join, retaining its localized title and driving its road Slider forwards with real stage progress. A full-screen opaque child sits behind the game artwork; all touched objects' original active states and the Slider value are restored on every close path. Missing or unavailable native UI uses a sprite-based IMGUI loading screen over an opaque dark backdrop. Other RevaMP overlays remain suppressed. A `newpaper` nine-sliced paper panel, placed clear of the top-left title and road-bar strip, shows stage details and elapsed seconds on the left, received/applied/expected object counters and a six-entry recent receipt feed on the right; narrow or short windows stack the columns and scroll. Mod approval retains its own scrollable text and Download mods / Decline actions. Buttons use `newmenubutton2` with outlined labels; the panel uses LiberationSans when available and a small Multiplayer · RevaMP credit. The footer shows total seconds, decimal MB received from the host, decimal KB/s and percent; these transport measurements include handshake, mods and shared records. The scene is generated locally from the host's seed/settings, not a downloaded save file. The percentage measures ordered stage progress, with applied/expected progress within shared objects; it is not a byte percentage.

`mp loading` retains all existing fields and adds `bytesReceived`, `receiveBytesPerSecond`, `elapsedMs`, `terrainDone`/`terrainNeeded`, `roadsDone`/`roadsNeeded`, `geometrySampled`, `receivedObjects`, `receivedVehicles`, `receivedItemsParts`, `receivedOther`, `expectedObjects`, `manifestReceived`, `initialRecordsEnded`, `recentObjects` (newest first: net, prefab, kind, privacy-safe prefab name), `hostName`, `route` (unknown/direct/relay), `pingMs` (-1 unknown), `versionCheck`, `worldActivity`, `placementStatus`, `loadingMod`/`loadingModBytes`/`loadingModTotal` and `progressPercent`, plus `loadingScreen` (`native` or `fallback`, retaining the last choice after close). The manifest contains IDs only, so kind counts describe received entities, not guessed expected kinds; attached members remain part of their parent entity, vehicles include trailers and other includes creatures/unknown roots. Records already known before the manifest count as received without inventing a new feed arrival. Text and published feed names refresh at most four times per second; geometry and placement retain the existing 0.2 s gate sampling. No display counter grants readiness.

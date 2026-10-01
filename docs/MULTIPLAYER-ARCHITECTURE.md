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

## 6. Determinism risks to close (from our code analysis)

World geometry is exact, but some things depend on **timing**, which differs between machines:
- scenery tiles cache "roads/POIs near" at the moment they're created — if a road or building near them isn't generated
  yet, scenery can differ (vanilla has this race too);
- POI placement waits until "roads done" at that place;
- items spawned inside buildings.

Plan: make these order-independent (e.g. an area only builds scenery once its roads/POIs are complete) and make the
server authoritative for anything persistent (items). A checksum of each generated chunk lets client and server detect
divergence in testing.

## 7. Saves

The server owns the shared world save (items, cars, buildings' state, time). Per-player data (inventory, body, stats)
saved server-side per Steam ID. Clients never write the shared world.

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

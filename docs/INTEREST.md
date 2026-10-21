# Entity interest

Pose relevance uses the existing 2,000 m `CarFarM` distance and lifetime `Known` sets.
Unknown copies receive no pose stream. Known far copies receive five-second maintenance and
essential states (rest, owner epoch, lock, inventory, held and driver transitions), keeping their
saved records current even when the object has moved far from its old location. The host also
replays its complete cache on viewer re-entry. Ownership/control messages keep their existing paths.
Compact animal poses and limb-only motion retain their previous full cadence inside that range;
the existing car tiers are unchanged. Rabbit/AI animation uses `StateInterestM`.

Client owners start streaming in the existing 3,000 m add range, using received global player
positions, including hidden seated players. Unknown player positions are treated conservatively.
Without another viewer, sampling/rest detection continues locally but only essential poses and
five-second maintenance uploads reach the host. Record resyncs and ownership messages retain
their existing paths. The host still applies every accepted owner upload to its world/far store.

A previously moving object needs one second without visible pose, limb or car-visual changes
before its reliable final rest state. Sub-threshold samples between moving steps do not end the
stream. Solver speed threshold crossings alone are not control changes. Ordinary unchanged awake physics
still has the existing five-second quiet tail: at most four one-second keepalives, then one final
and silence. Release, unlock and new-epoch startup retain their existing physics cadence within
that same tail. Lock, inventory, held, driver and ownership transitions remain immediate; visible
motion retains its full cadence. Known far copies still receive five-second moving maintenance
and confirmed transitions, so zero nearby receivers does not imply zero maintenance relays.

An unoccupied car already at rest needs more than 2 cm from its last sent position to restart
the stream; moving cars retain the 1 cm threshold. Suppressed samples never move that baseline.
Rotation, controls, wheel/engine visuals and reliable finals keep their existing rules.

Entering range triggers a complete pose on the owner's next sample, even for a resting object.
The server caches complete moving poses as well as finals. Its existing half-second interest
tick replays the latest pose (including limbs), rabbit snapshot and AI snapshots when a known
viewer returns. New copies use the add/resync path with those same snapshots. Epoch fences
prevent replaying an earlier owner's pose. As with all network state, an abrupt teleport is
recognized when its player-state update arrives; it does not wait for the five-second refresh.

The interest selection itself changes no wire format; protocol is **48** (uniform six-byte Claims with diagnostic
producer, ordinary held-state transitions, and the retained world-pose physlock mount address, stable mark session
date/origin and authenticated rename relays). Interest changes are local
send selection and cache/replay behavior; cached AI snapshots now replay reliably. Existing player states supply the
positions used by owners.

`netcat` and the play log's network rows expose `interestSkipped`, an array of session-cumulative
`{player, skipped}` counters. On clients, receiver 0 counts suppressed host uploads; on hosts,
each destination counts states/records withheld for unknown copies or distance. These are
message candidates, not bytes, and do not include existing near/mid tier thinning. Detached
peers are forgotten; starting another session resets the counters. Captured history owns its
counter copies.

## Rabbit population findings

`Fixes.Rabbits` starts the otherwise inactive game `aispawn` coroutine, using the game's own
chance, interval, batch size and population setting. There is no additional RevaMP rabbit
creation loop. Each local spawner counts other players' nearby rabbits before `TrySpawn`.
The game's `TrySpawn` checks its population before a batch, not between individual batch
members, so a batch can overshoot that setting. Its list tracks its own spawned objects, not
the entire saved population. `nyulscript.Die` disables the rabbit and leaves the saved ragdoll;
it does not destroy the item. Existing world streaming can unload/reload those saved items.
No spawning, despawn or ownership policy is changed here.

The reported 235 is an **owned category count**, including dormant far-store entries, not a
count of nearby active simulations. The host's 152.5 s snapshot includes 36 `Nyul` entries in
its 300 m capture area, but also reports 76 omitted entities and 1,159 outside the area.
It contains neither a complete population/activity census nor spawner counters. Consequently
the evidence cannot establish that 235 rabbits were alive nearby, or distinguish how many
were newly spawned, saved/reloaded or dead. The existing nearest-player ownership rule also
explains why co-located players need not divide those owned counts evenly.

The inherited dead-animation suppression, visible-limb rest detection and compact upright
rabbit path remain enabled and covered by `IdleTrafficTest`. Dead bodies retain their final
full limb state; real visible motion still resumes the full stream.

## Byte projections

Rates below are application payload, before transport overhead. The two machines' five-second
windows differ; their send/receive totals are not expected to match exactly. These are offline
projections, not game measurements of this patch.

| Scene / direction | Recorded | Projection |
| --- | ---: | --- |
| 152.5 s host rabbit/animal out | 380,166 B / 5.003 s = 74.21 KiB/s | Depends on live/limp motion and distance; formula below |
| 152.5 s host corpse out | 272,266 B / 5.003 s = 53.14 KiB/s | 0 recurring B/s for settled bodies; one full final each; real nearby limb motion remains |
| 152.5 s client rabbit / corpse in | 48.80 / 83.03 KiB/s | Same relevance/motion rules, with a different sampling window |
| 473.5 s client item + rabbit out | 7,960 + 7,720 B / 5.001 s = 3,135.37 B/s | 0 for quiet unchanged objects; about 303.4 B/s even if all 22 items and 13 rabbits need continuous far maintenance, plus actual transitions and changed records |

For two-byte network IDs and epoch 1, an ordinary pose is 40 B, a compact live rabbit pose
25 B, and a rabbit snapshot 24 B. In the separated scene the conservative maintenance model
is `(22*40 + 13*(25+24))/5 = 303.4 B/s`, about **90.3% less** than the recorded two categories.
Only moving/changed entities actually refresh; dormant and unchanged resting entities do not.
The separately recorded two resyncs (576 B) still go to the host; they are not included in
those categories or claimed as savings. Short-lived movement can also add reliable finals.

For the co-located scene, interest alone must not suppress the rabbits both players can see.
The recorded host rabbit category contains 1,563 messages. Its 227 rabbit animation messages
leave 1,336 pose messages. **If** all those poses were upright live rabbits and all animation
messages were genuine nearby changes, preserving every message with the current codecs gives
`(1336*25 + 227*24)/5.003 = 7,764.94 B/s` (**7.58 KiB/s**). This is a live-only
counterfactual, not an exact forecast: the observed category's much larger mean pose size
indicates it cannot safely be treated as 1,336 plain live poses. Limp bodies retain complete
limb packets while visibly moving near viewers; the host retains far owner maintenance.

The general projection is `25*L + 24*A + D + F` bytes per window, where `L` is relevant live
pose count, `A` is relevant genuine rabbit changes, `D` is relevant visibly moving limb-pose
bytes, and `F` is finals/far-store maintenance. Exact values require a complete activity and
distance census absent from these marks. As a deliberate no-cap check, if all 235 rabbits
really move near a viewer at 20 Hz, their compact poses alone still cost **117,500 B/s**.
The patch removes waste; it does not promise a fixed bandwidth for an active swarm.

`InterestTest.cs` runs these packet-size projections alongside production source gating,
re-entry replay, host snapshot retention, rabbit routing, epoch checks and per-receiver
counters. `TelemetryTest` verifies detached history and JSON preserve the counters.

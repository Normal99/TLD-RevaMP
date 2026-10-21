# Riding (#9, #11)

The game DLL's `playeranimationscript.Update` writes IK targets in world space, with smoothing timed by pose
arrivals. `IKscript.LateUpdate` consumes those targets. Parenting the remote model root to a seat did not make
those writes share the car's render sample. Standing roots were also placed by `RemotePlayers.Tick` before
`Entities.Tick` moved transform-driven cars, using a separate passenger-clock interpolator.

Protocol 38 puts the frame id, seat, vehicle epoch, vehicle-owner clock, body offset and facing in Pose/OtherPose.
Body and limbs are sampled together, then composed with the drawn vehicle/seat before IK. The clock includes
Unity's blend between physics samples; reading it does not evaluate or advance the car again. Missing passenger
samples hold the local pose. Seat, vehicle and owner changes reset the sample history. Global player states and
the existing footing/seat messages remain available for distance rules and missing-vehicle fallback.

Protocol 49 also extends item State flag 128 with the held item's root pose relative to its owner's right-hand IK
target: three signed 16-bit millimetre coordinates (each within ±2 m), then the existing smallest-three rotation
(13 bytes, after any car-frame/lock fields). World poses and Carried flag 16 remain available for server records,
distance rules and fallback. Only actual `inHandP` / `pickedUp` items carry this extension; stored/locked items do not.
`HandItemRender.LateUpdate` runs at -9999, after every `RidingRender` at -10000 has applied riding and driver hands,
and before IK consumes the targets. Walking targets have already been played back by the game's Update. The item
uses the newest received grip offset, with an 80 ms ease for re-grips. Normal proxy transform/body movers yield
while this placement or its release glide is active. Pickup, drop, handover and missing-model switches glide for
120 ms from the last drawn pose; the ordinary world/car state stream continues underneath.

On test machines, `mp hands` includes each player's `heldItems`: `net`, `active`, `distance` in metres from the root
to `RHTarget.TransformPoint(lp)`, and `lp` / `lq` (quaternion x/y/z/w). Distance should settle near zero after the
short grip/switch ease, seated or walking. Check rotation/re-grips, drops, inventory storage, handovers and model
loss/reappearance on both copies. Unit checks cover the optional flag and state tail, car/world fallback fields,
offset composition/inversion and moving-destination switch glides; these checks do not replace a live two-copy run.

`AboardDrawnAtPhysics` previously disabled interpolation for a seated passenger's copy to align interaction rays
with colliders. Its default is now false, restoring smooth vehicle drawing. Passenger interaction aiming at speed
needs a real playtest because the collider and drawn poses can differ by one physics step. The existing switch
still restores the old behavior. Cars retain their adaptive delay and one-second coasting; no delay tuning or send
policy changes were justified by the deterministic moving-stream replay.

`RidingTest` runs production pose serialization, sample selection, target application, vehicle-clock selection,
PoseInterpolator and the visible-change decision. A 20 m/s translating bus with unchanged wheel/engine/signals
still sends all 200 states over ten seconds. At 120 rendered frames/s, 150 ms one-way delay, deterministic jitter
and a 400 ms outage, the measured replay has 1,034 interpolated frames, 46 extrapolated frames and zero held
frames after warm-up; constant-speed corrections are zero. Passenger local residual stays below the 3 mm test
tolerance. These are synthetic replay results, not measurements from the internet playtest.

The bridge `ridingcheck.py` tests front/rear seats, the roof and rear aisle, with both host and joiner driving. It
captures synchronized format-3 marks on both machines and checks sent/received cadence, held frames, corrections,
render pauses, matching car/rider clocks and local pelvis/root offsets. Its before phase switches off RidingFrames
and restores physics-only seated drawing on this build; its after phase enables the fix. The offline selftest
rejects missing traces, clock mismatch, starvation, held motion, jitter and a stable 0.4 m trailing pelvis; the
synthetic fixed-frame pair has zero offset. Actual bus articulation, ground contact, camera comfort, interaction
aim and internet loss/jitter still require the two-copy run or a real playtest. No game was run for this change.

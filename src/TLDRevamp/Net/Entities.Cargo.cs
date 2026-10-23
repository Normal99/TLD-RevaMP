using UnityEngine;

namespace TLDRevamp.Net
{
    /// Loose things and other players' cars (bugs 4, 5, 6, 7, 2026-10-03: "placing items in cars is dangerous if you do
    /// not own the car", "items don't stay in the car if you switch owners", wheels and doors falling off, players killed
    /// standing next to a friend).
    ///
    /// A copy is a kinematic body: infinite mass, moved to where its owner had it a network delay ago. Two cases made
    /// that a battering ram against the car simulated here:
    ///  - Cargo. An item put into another player's car stays its placer's: on the car owner's machine it is a kinematic
    ///    copy following the placer's (late) view of it, while the car under it is real. Every start, turn and bump moves
    ///    the car into the copy — which does not give. The game's crash damage reads the car's change of speed
    ///    (carscript.DamageStuff): doors, the engine and wheels came off (friend, t 743: Δv 432 against a lamp copy).
    ///  - Held and stored items. The game puts an item in a player's hands on its pickedUp layer (pickupable.Pickup);
    ///    the copies elsewhere kept their normal layer and collided with everything. Taken out of the inventory the copy
    ///    comes back where the stored model was parked and is put at the holder's hands in one step (copy sweep stopped:
    ///    the lamp, the binoculars, the katana, 738 m) — into the car the holder sits in, or into the player next to them.
    ///
    /// Now, as in single player, one machine simulates a car and what rides in it: a loose item that this machine's car
    /// touches becomes this machine's (the server's Claim; at once here, like a pickup). An item in someone's hands is
    /// theirs (the server refuses claims on it), and its copies collide with nothing — on the holder's machine it is the
    /// game's held item, a gentle force-driven body; a kinematic stand-in of it can only shove.
    public static partial class Entities
    {
        public static bool CargoClaims = true;          // A/B: false = items stay with whoever placed them
        /// A/B: a part that came off its car and is bolted to nothing is a loose item for the touch and car claims (walking
        /// into it, an item or a car coming at it, lying in a car). Mark 2026-10-22 ("why is this tire not changing
        /// ownership"): a wheel the joiner took off the host's car lay by it, a frozen copy to the host for good - every
        /// such claim skipped parts, so only picking it up could take it.
        public static bool LoosePartClaims = true;
        /// Not a car's part, or one that came off and is bolted to nothing now.
        private static bool LooseItem(Ent e) => e.PartIndex < 0 ||
            LoosePartClaims && e.Root != null && (e.Root.attachable == null || !e.Root.attachable.attached) && !RidesParent(e);
        public static float CargoClaimCooldownS = 2f;   // per item: no claim tug-of-war between two cars touching it
        /// A/B: an item its owner just let go of is ours at once when it is in or coming at our car — no cooldown. Marks
        /// 2026-10-22 ("again"): the joiner grabbed a bus tyre out of the host's car for a moment and let go; for the rest
        /// of the cooldown its copy on the host, kinematic and moved by the joiner's simulation, lay in the host's real car
        /// and shoved it 30-60 cm, bouncing, until the claim came 2.0 s after the grab, five times in a row. Letting go
        /// is no tug-of-war. (Copies still without collisions after leaving a container count too.)
        public static bool LetGoClaims = true;
        public static float LetGoClaimS = 1f;
        public static long LetGoClaimTries, LetGoSeen;
        public static long CargoClaimed, HeldShown;

        /// In the local player's hands: picked up (fpscontroller.pickedUp) or in the right hand.
        /// Test only (bridge `mp forceheld <net>`): this item counts as in the local player's hands — a player carrying
        /// a part holds the mouse button down, which a test can't (the bridge's pickup is dropped the next frame).
        public static tosaveitemscript ForceHeld;
        private static bool IsHeld(tosaveitemscript it)
        {
            if (ForceHeld != null && it == ForceHeld) return true;
            var p = it != null ? it.P : null;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            return p != null && (p.inRightHand || pl != null && (pl.pickedUp == p || pl.inHandP == p)) ||
                   it != null && it.ragdollPos != null && it.ragdollPos.PickedUp();
        }

        /// A copy of an item in its owner's hands: no collisions here (raycasts still see it).
        private static void ShowHeld(Ent e, bool held)
        {
            if (e.Held != held) HeldShown++;
            if (HeldLayer && (held || e.Held)) ShowHeldLayer(e, held);
            e.Held = held;
            ShowCarriedCollisions(e);
        }

        /// A/B: a copy in its owner's hands is on the game's picked-up layer, as the item is on its owner's machine
        /// (pickupable.Pickup): the layer the physics settings keep away from wheels. Collisions off alone left it to every
        /// wheel here, since a wheel feels the ground with a query and queries see bodies with collisions off: another
        /// player's car rode up on an item someone held, its body passing through (user, 2026-10-22, 0.69.0 and dev).
        public static bool HeldLayer = true;
        public static long HeldLayersSet;
        private static void ShowHeldLayer(Ent e, bool held)
        {
            var p = e.Root != null ? e.Root.P : null;
            if (p == null) return;
            if (p.cols != null) foreach (var c in p.cols) if (c != null) c.gameObject.layer = held ? mainscript.pickedUpLayer : c.defLayer;
            if (held) p.ChangeLayer2(mainscript.pickedUpLayer); else p.ResetLayer2();
            if (held) HeldLayersSet++;
        }

        private static void ShowCarriedCollisions(Ent e)
        {
            if (e.Root == null) return;
            bool collide = !e.Held && !e.Stored && !e.ReleaseGhost && !e.CargoGhost;
            foreach (var b in e.Root.GetComponentsInChildren<Rigidbody>(true))
                if (b.detectCollisions != collide) b.detectCollisions = collide;
            var limbs = Limbs(e);
            if (limbs == null) return;
            foreach (var l in limbs)
            {
                var b = l != null ? l.GetComponent<Rigidbody>() : null;
                if (b != null && b.detectCollisions != collide) b.detectCollisions = collide;
            }
        }

        /// From ContactRelay (on every root simulated here): a car of this machine touched a copy of a loose item.
        internal static void CargoContact(uint carNet, Collision c)
        {
            if (!CargoClaims) return;
            var orb = c.rigidbody;
            if (orb == null || !orb.isKinematic) return;
            if (!ByNet.TryGetValue(carNet, out var car) || car.Proxy || car.Root == null || car.Root.car == null) return;
            var e = ProxyOf(orb.transform.root);
            if (e == null || e.Root == null || e.Root.car != null || e.Held || e.Stored || e.Physical || e.CrashTaking) return;
            if (ClaimCargo(e, reason: ClaimReason.CargoContact)) CargoClaimedOnContact++;
        }

        /// A loose item's copy that may become this machine's (cooldown, not attached, not a creature): claimed now.
        private static bool ClaimCargo(Ent e, bool released = false, ClaimReason reason = ClaimReason.CargoNear)
        {
            float now = Time.realtimeSinceStartup;
            bool letGo = LetGoClaims && now - e.LetGoAt < LetGoClaimS;
            if (!released && !letGo && now - e.OwnerAt < CargoClaimCooldownS) return false;   // a holder letting go in our car is no tug-of-war
            if (letGo) { if (LetGoClaimTries < 20) Plugin.Log.LogInfo($"let-go claim ({reason}): {e.Root.name} net {e.NetId}, let go {now - e.LetGoAt:F2} s ago, owner since {now - e.OwnerAt:F2} s"); e.LetGoAt = -100f; LetGoClaimTries++; }   // one try
            e.OwnerAt = now;   // one try per cooldown, claimed or not
            var it = e.Root;
            if (!CargoBody(e) || (it.attachable != null && it.attachable.attached) || RidesParent(e)) return false;
            if (e.WornBy >= 0) return false;   // on another player's head (Entities.Worn): theirs
            if (it.GetComponentInChildren<newAiScript>() != null && (Limbs(e) == null || !Limp(e))) return false;   // a living creature thinks where it is owned (Entities.Ai)
            // like a pickup (PickupClaim): ours at once, so it is a real body in this car from the next physics step; a
            // refusal (held there after all) hands it back
            if (SeamlessClaims) PlaceAtNewest(e);
            SetProxy(e, false);
            if (ClaimTrace) TraceClaim(e, reason);
            e.OwnerId = MyId;
            W.Reset(); WriteClaim(W, e.NetId, reason); ToServer(W, true);
            ClaimsSent++; CargoClaimed++;
            return true;
        }

        /// Diagnostics (test copies, off): the cargo scan's view of copies in a car's box, and the first five cargo claims
        /// frame by frame for a second (position in the car's frame, speeds, the car's speed). It showed the joiner's can
        /// moved through the host's tailgate launching the parked pickup at 32.7 m/s (stackcargo, 2026-10-23).
        public static bool ClaimTrace = false;
        private static int _scanTraces, _claimTraces;
        private static Ent _trace; private static carscript _traceCar; private static float _traceUntil;
        private static void TraceClaim(Ent e, ClaimReason reason)
        {
            if (_claimTraces >= 5) return;
            _claimTraces++; _trace = e; _traceCar = OverlappedCar(e); _traceUntil = Time.realtimeSinceStartup + 1f;
            Plugin.Log.LogInfo($"cargo claim ({reason}): {e.Root.name} net {e.NetId}, its speed {e.LastVel.magnitude:F2} m/s, touching {(_traceCar != null ? _traceCar.name : "no car")}");
        }
        private static void ClaimTraceTick()
        {
            if (_trace == null) return;
            var rb = _trace.Root != null ? _trace.Root.GetComponent<Rigidbody>() : null;
            if (rb == null || Time.realtimeSinceStartup > _traceUntil) { _trace = null; return; }
            var ct = _traceCar != null ? _traceCar.transform : null;
            var lp = ct != null ? ct.InverseTransformPoint(rb.position) : rb.position;
            Plugin.Log.LogInfo($"claim trace: {_trace.Root.name} net {_trace.NetId} t {Time.time:F3} at [{lp.x:F2}, {lp.y:F2}, {lp.z:F2}] v {rb.velocity.magnitude:F2} w {rb.angularVelocity.magnitude:F1} kin {rb.isKinematic} col {rb.detectCollisions} depen {rb.maxDepenetrationVelocity:F1}" +
                               (_traceCar != null && _traceCar.RB != null ? $", the car v {_traceCar.RB.velocity.magnitude:F2}" : "") + $", owner {_trace.OwnerId}, proxy {_trace.Proxy}");
        }
        private static carscript OverlappedCar(Ent e)
        {
            var own = e.Root.transform.root;
            foreach (var c in e.Root.GetComponentsInChildren<Collider>(false))
            {
                if (c == null || c.isTrigger) continue;
                int n = Physics.OverlapBoxNonAlloc(c.bounds.center, c.bounds.extents + Vector3.one * 0.1f, _clearHits, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++) { var r = _clearHits[i].transform.root; if (r != own && r.GetComponent<carscript>() != null) return r.GetComponent<carscript>(); }
            }
            return null;
        }

        /// A/B: a claimed copy starts where its owner has it now - the newest state carried forward at its speed (at most
        /// the interpolator's 0.25 s) - not where it was shown, a send interval and the jitter margin behind. Marks
        /// 2026-10-22 ("the ownership change is clearly visible"): a bus tyre the host's car claimed from the joiner jumped
        /// back up to 1.53 m on the joiner's screen, where the host's simulation of it began.
        public static bool SeamlessClaims = true;
        public static long ClaimsPlacedAtNewest;
        private static void PlaceAtNewest(Ent e)
        {
            if (e.Root == null || e.Ip == null || e.PoseLocked || e.Stored || e.CarriedBy != 0 || Limbs(e) != null) return;
            if (!e.Ip.Newest(Time.realtimeSinceStartupAsDouble, out var gp, out var rot, out var vel)) return;
            var t = e.Root.transform;
            t.SetPositionAndRotation(mainscript.UnityPosFromGlobal(gp), rot);
            var rb = e.Root.GetComponent<Rigidbody>();
            if (rb != null) { rb.position = t.position; rb.rotation = t.rotation; }
            e.LastVel = vel;
            ClaimsPlacedAtNewest++;
        }

        /// Claimed on contact, the copy had already hit: a kinematic body gives nothing, so the car took the whole
        /// impulse in that physics step (buglist2m, two machines: a lamp dropped onto the host's seat spun the parked
        /// car 371 deg/s, left it on its side, a hubcap off; the lamp, made real while sunk into the seat, fell through
        /// the floor). Now an item is claimed while it COMES: a copy within a margin of a car simulated here — 0.3 m plus
        /// three physics steps of their closing speed — is this machine's before the step that would touch. The touch is
        /// then the game's own physics between a 1 kg lamp and a car. Contact stays as the fallback.
        public static float CargoNearM = 0.3f, CargoNearSteps = 3f;
        public static long CargoClaimedNear, CargoClaimedOnContact;
        /// A/B: outside the car's own box, only an item coming at the car is claimed (closing at CargoApproachMin or more);
        /// one dropped beside it stays its owner's. Marks 2026-10-22 ("dropping item near car makes it the other player's
        /// ownership"): anything within the margin was taken, even falling straight down next to the car.
        public static bool CargoApproachOnly = true;
        public static float CargoApproachMin = 0.3f;
        public static long CargoNearSkippedNotComing;
        /// A/B: a copy in a car's zone that this machine may not take yet (the cooldown, not coming at the car, a refusal)
        /// has no collisions here until it is ours or out of the zone: kinematic and moved by its owner, it met our car with
        /// no mass limit. Marks 2026-10-22: the host's parked car shoved 30-60 cm by the joiner's bus tyre lying in it, and
        /// thrown with a crash of dv 26 m/s by one sunk into it ("last hits: BusTireGumi ... copy kin, inside it now").
        /// The owner still simulates the item against its copy of the car, so it lands and rests as before.
        public static bool CargoGhosts = true;
        public static long CargoGhostsMade;
        /// The items made a ghost (network ids, the last 256): a test asks about its own items, not every copy at rest
        /// against some car simulated here (a joiner's bus tyre against a parked bike by the road).
        internal static readonly System.Collections.Generic.HashSet<uint> CargoGhostNets = new System.Collections.Generic.HashSet<uint>();
        private static int _cargoTick;
        private static readonly System.Collections.Generic.Dictionary<carscript, Bounds> _carBounds = new System.Collections.Generic.Dictionary<carscript, Bounds>();
        private static float _carBoundsAt;
        private static void CargoNearTick(float dt)
        {
            _cargoTick++;
            if (CargoClaims && _localCars.Count > 0) CargoNearScan(dt);
            foreach (var e in ByNet.Values)   // out of every zone (or ours, or the rule off): collisions back
                if (e.CargoGhost && (e.CargoGhostTick != _cargoTick || !e.Proxy || !CargoGhosts)) { e.CargoGhost = false; ShowCarriedCollisions(e); }
        }

        private static void CargoNearScan(float dt)
        {
            if (Time.time - _carBoundsAt > 10f) { _carBoundsAt = Time.time; _carBounds.Clear(); }
            foreach (var e in ByNet.Values)
            {
                if (!e.Proxy || e.Root == null || e.Root.car != null || !LooseItem(e) || e.Held || e.Stored || e.Physical || e.CrashTaking || !CargoBody(e)) continue;
                var irb = e.Root.GetComponent<Rigidbody>();
                if (irb == null || !irb.isKinematic || !irb.detectCollisions && !e.CargoGhost && !(e.ReleaseGhost && LetGoClaims && Time.realtimeSinceStartup - e.LetGoAt < LetGoClaimS)) continue;
                var p = irb.position;
                float iv = e.LastVel.magnitude;
                foreach (var car in _localCars)
                {
                    if (car == null || car.RB == null) continue;
                    var b = CarLocalBounds(car);
                    float reach = b.extents.magnitude + CargoNearM + 2f + (car.RB.velocity.magnitude + iv) * dt * CargoNearSteps;
                    if ((p - car.transform.position).sqrMagnitude > reach * reach) continue;
                    // the item's size, then the car's box (its own space) grown by it and the margin
                    float ir = 0f;
                    foreach (var c in e.Root.GetComponentsInChildren<Collider>()) if (c.enabled && !c.isTrigger) { ir = Mathf.Max(ir, c.bounds.extents.magnitude); }
                    float m = ir + CargoNearM + ((car.RB.GetPointVelocity(p) - e.LastVel).magnitude) * dt * CargoNearSteps;
                    var lb = b; lb.Expand(2f * m / Mathf.Max(0.01f, car.transform.lossyScale.x));
                    var lp = car.transform.InverseTransformPoint(p);
                    if (!lb.Contains(lp)) continue;
                    // in the car's own box (lying in it, landed on it) or coming at it: the car's; beside it and not coming: not
                    bool coming = b.Contains(lp) && (!ContentsOverCar || OverCar(car, e));
                    if (ClaimTrace && b.Contains(lp) && _scanTraces < 60 && Time.realtimeSinceStartup - e.ScanTracedAt > 0.1f)
                    {
                        e.ScanTracedAt = Time.realtimeSinceStartup; _scanTraces++;
                        var su = SurfaceUnder(e, out _);
                        Plugin.Log.LogInfo($"scan trace: {e.Root.name} net {e.NetId} at [{lp.x:F2}, {lp.y:F2}, {lp.z:F2}] coming {coming} under {(su != null ? su.name : "-")} cooldown {Mathf.Max(0f, CargoClaimCooldownS - (Time.realtimeSinceStartup - e.OwnerAt)):F2} s overlaps {OverlapsCar(e, car, CargoGhostMarginM)} ghost {e.CargoGhost} v {e.LastVel.magnitude:F2} body {CargoBody(e)} rides {RidesParent(e)}");
                    }
                    if (!coming && CargoApproachOnly && !b.Contains(lp))
                    {
                        // the car's box is only a first filter (a pickup's measured 3.4 m wide: mirrors, doors): the item's
                        // own shape swept along its motion relative to the car, three physics steps ahead, must meet the car
                        var rel = e.LastVel - car.RB.GetPointVelocity(p);
                        coming = rel.magnitude >= CargoApproachMin && SweepMeetsCar(e, car, rel, rel.magnitude * dt * CargoNearSteps + CargoNearM);
                        if (!coming) CargoNearSkippedNotComing++;
                    }
                    else if (!CargoApproachOnly) coming = true;
                    if (coming && ClaimCargo(e))
                    {
                        CargoClaimedNear++;
                        if (CargoClaimedNear <= 20) Plugin.Log.LogInfo($"cargo near: {e.Root.name} net {e.NetId} claimed for {car.name}, {Vector3.Distance(p, car.transform.position):F1} m from its centre, its box {b.size.x:F1} x {b.size.y:F1} x {b.size.z:F1} m");
                    }
                    else if (CargoGhosts && (!CargoGhostOnlyOverlapping || OverlapsCar(e, car, e.CargoGhost ? 3f * CargoGhostMarginM : CargoGhostMarginM)))
                    {
                        e.CargoGhostTick = _cargoTick;
                        if (!e.CargoGhost)
                        {
                            e.CargoGhost = true; ShowCarriedCollisions(e); CargoGhostsMade++;
                            if (CargoGhostNets.Count >= 256) CargoGhostNets.Clear();
                            CargoGhostNets.Add(e.NetId);
                            if (CargoGhostsMade <= 20)
                                Plugin.Log.LogInfo($"cargo ghost: {e.Root.name} net {e.NetId} (owner {e.OwnerId}) in {car.name}'s zone at [{lp.x:F2}, {lp.y:F2}, {lp.z:F2}] (its frame), closing {(e.LastVel - car.RB.GetPointVelocity(p)).magnitude:F2} m/s, the car at {car.RB.velocity.magnitude:F1} m/s");
                        }
                    }
                    break;
                }
            }
        }

        /// The car's own box: every solid collider under it (body, parts), in its local space; cached for 10 s.
        private static Bounds CarLocalBounds(carscript car)
        {
            if (_carBounds.TryGetValue(car, out var b)) return b;
            var t = car.transform; bool any = false;
            foreach (var c in t.GetComponentsInChildren<Collider>())
            {
                if (!c.enabled || c.isTrigger || c is WheelCollider) continue;
                var cp = c.GetComponentInParent<pickupable>();
                if (cp != null && cp.physlock != null) continue;   // what it carries (stored, parented): not the car; a copy stored here may be drawn where its owner has it, metres away
                var wb = c.bounds;
                for (int k = 0; k < 8; k++)
                {
                    var corner = wb.center + Vector3.Scale(wb.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
                    var lp = t.InverseTransformPoint(corner);
                    if (!any) { b = new Bounds(lp, Vector3.zero); any = true; } else b.Encapsulate(lp);
                }
            }
            if (!any) b = new Bounds(Vector3.zero, Vector3.one * 3f);
            _carBounds[car] = b;
            return b;
        }

        /// A car carried on a car (the transporter's bed, a trunk: mountStuff.storedcars) rides with its carrier, so it is
        /// simulated where the carrier is. It stayed its last owner's (cargo claims skip cars): with another player at the
        /// carrier's wheel it was a kinematic copy on a real bed, and the first start threw it off (transporter.py, laptop
        /// driving: the carried car 17.9 m off the bed, a 93 km/h jolt). Now a car stored on a car simulated here becomes
        /// this machine's, unless someone sits at its own wheel.
        public static bool CarCargoClaims = true;   // A/B: false = a carried car stays with its last owner
        public static long CarCargoClaimed;
        private static readonly System.Collections.Generic.List<Transform> _carriers = new System.Collections.Generic.List<Transform>();
        private static void CarCargoTick()
        {
            if (!CargoClaims || !CarCargoClaims) return;
            foreach (var car in _localCars)
            {
                if (car == null) continue;
                _carriers.Clear(); _carriers.Add(car.transform.root);
                // trailers hitched to it: their load goes where the car goes (a car on a car trailer stayed the
                // other machine's, a kinematic copy that held the trailer still: cartrailer run 15, D1 0 km/h)
                foreach (var g in Fixes.TrailerGrip.All)
                {
                    var at = g != null && g.tosaveitem != null ? g.tosaveitem.attachable : null;
                    if (at != null && at.attached && at.point != null && at.point.root == car.transform.root) _carriers.Add(g.tosaveitem.transform.root);
                }
                foreach (var carrier in _carriers)
                foreach (var m in carrier.GetComponentsInChildren<mountStuff>())
                    foreach (var sc in m.storedcars)
                    {
                        if (sc == null || sc == car) continue;
                        var e = ProxyOf(sc.transform.root);
                        if (e == null || !e.Proxy || e.Root == null || e.Driven || e.Held || e.Stored || e.CrashTaking || e.PartIndex >= 0) continue;
                        if (Time.realtimeSinceStartup - e.OwnerAt < CargoClaimCooldownS) continue;
                        e.OwnerAt = Time.realtimeSinceStartup;
                        SetProxy(e, false);
                        e.OwnerId = MyId;
                        W.Reset(); WriteClaim(W, e.NetId, ClaimReason.CarCargo); ToServer(W, true);
                        ClaimsSent++; CarCargoClaimed++;
                        Plugin.Log.LogInfo($"cargo car {e.NetId} ({e.Root.name}) on {car.name}: claimed (its carrier is simulated here)");
                    }
            }
        }

        /// A car's contents go with the car (playtest 2026-10-16: the joiner drove the host's car with the host's food,
        /// jerry cans, barrel and katana in it — kinematic copies of the host's on the joiner's machine, inside the car
        /// it simulated: crashes of Δv 9-668, a death, the car thrown apart). Three holes in the claims above:
        ///  - an item stored in a container (parented, its body removed: mountStuff.Store) has no body, so the near-claim
        ///    never looked at it, and the car's new owner drew it at its old owner's pose, a collider of its own car moved
        ///    around by transform writes;
        ///  - taking the wheel claimed the car only (DriveClaimRootOnly), never what lay in it;
        ///  - a copy placed again inside a car (the game's streaming, a respawn: "copy placed again 1.0 s after a
        ///    displaced store-out", then the death) appears overlapping it, before any claim could come.
        /// Now our car's contents are claimed when the car becomes ours, when a copy is placed inside it, and every
        /// half second: what its containers hold and every loose item inside its box (not one its owner holds, has in
        /// the inventory, or carried away from the container: LockedAway — the release rule takes that one out).
        public static bool ContentsFollowCar = true;   // A/B: false = before v0.68.1 (takeover.py --old)
        public static float ContentsMarginM = 0.05f;
        public static long ContentsClaimed, ContentsOnOwner, ContentsOnPlace, ContentsStored;
        private static readonly System.Collections.Generic.List<carscript> _ownCars = new System.Collections.Generic.List<carscript>();

        /// Our cars loaded here, frozen or not (FixedTick's half-second refresh): a frozen car gets unfrozen with the
        /// copies inside it.
        private static void RefreshOwnCars()
        {
            _ownCars.Clear();
            foreach (var o in ByNet.Values)
                if (!o.Proxy && o.Root != null && o.PartIndex < 0 && o.Root.car != null && o.Root.gameObject.activeInHierarchy) _ownCars.Add(o.Root.car);
        }

        private static bool ContentsCandidate(Ent e) =>
            e.Proxy && e.Root != null && e.Root.car == null && LooseItem(e) && CargoBody(e) && !e.Held && !e.Stored && e.WornBy < 0 && !e.CrashTaking;   // (worn: a passenger's hat)

        /// Stored in one of the car's containers, or lying inside the car's box and in no other container.
        private static bool InCar(carscript car, Ent e, out bool stored)
        {
            var pl = e.Root.P != null ? e.Root.P.physlock : null;
            stored = pl != null && pl.transform.root == car.transform.root;
            if (stored) return !LockedAway(e);
            if (pl != null) return false;
            var b = CarLocalBounds(car);
            b.Expand(2f * ContentsMarginM / Mathf.Max(0.01f, car.transform.lossyScale.x));
            return b.Contains(car.transform.InverseTransformPoint(e.Root.transform.position)) && (!ContentsOverCar || OverCar(car, e));
        }

        private static readonly RaycastHit[] _sweepHits = new RaycastHit[16];
        /// The item's colliders' box swept `dist` along `rel` meets one of this car's colliders before anything else.
        private static bool SweepMeetsCar(Ent e, carscript car, Vector3 rel, float dist)
        {
            bool any = false; var b = default(Bounds);
            e.Root.GetComponentsInChildren(false, _ghostCols);
            foreach (var c in _ghostCols)
            {
                if (c == null || c.isTrigger) continue;
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            if (!any || rel.sqrMagnitude < 1e-6f) return false;
            var own = e.Root.transform.root;
            int n = Physics.BoxCastNonAlloc(b.center, b.extents, rel.normalized, _sweepHits, Quaternion.identity, dist, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; Transform first = null;
            for (int i = 0; i < n; i++)
            {
                var r = _sweepHits[i].collider.transform.root;
                if (r == own || _sweepHits[i].distance >= best) continue;
                best = _sweepHits[i].distance; first = r;
            }
            return first != null && first == car.transform.root;
        }

        /// A/B: a loose item is in the car only when the car is under it (its bed, a seat, the roof), not merely inside the
        /// box of all its colliders: a pickup's box measured 3.4 m wide (mirrors, doors) and a can on the ground beside it
        /// was claimed as its contents (cargocheck C, 2026-10-22).
        public static bool ContentsOverCar = true;
        private static readonly RaycastHit[] _overHits = new RaycastHit[16];
        /// The first surface under the item belongs to this car.
        private static bool OverCar(carscript car, Ent e)
        {
            var first = SurfaceUnder(e, out _);
            return first != null && first == car.transform.root;
        }

        /// The root of the first surface under the item, its own colliders, the local player and other loose items aside:
        /// what a stack stands on counts. A can on a crate or a tyre on a tyre in our bed met the other item first, was
        /// never our car's and stayed a ghost of the other player's for good (review 2026-10-22, #21).
        internal static Transform SurfaceUnder(Ent e, out Rigidbody body)
        {
            body = null;
            var own = e.Root.transform.root;
            var me = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.transform.root : null;
            var rb = e.Root.GetComponent<Rigidbody>();
            var from = (rb != null ? rb.worldCenterOfMass : e.Root.transform.position) + Vector3.up * 0.05f;
            int n = Physics.RaycastNonAlloc(from, Vector3.down, _overHits, 3f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; Transform first = null;
            for (int i = 0; i < n; i++)
            {
                var r = _overHits[i].collider.transform.root;
                if (r == own || r == me || _overHits[i].distance >= best) continue;
                var it = r.GetComponent<tosaveitemscript>();
                if (SurfaceSkipsItems && it != null && it.car == null && (it.attachable == null || it.attachable.utanfuto == null)) continue;   // an item, not a vehicle
                best = _overHits[i].distance; first = r; body = _overHits[i].collider.attachedRigidbody;
            }
            return first;
        }

        public static bool SurfaceSkipsItems = true;   // A/B: false = the first item under it counts (before #21)
        private static readonly Collider[] _ghostHits = new Collider[32];
        private static readonly System.Collections.Generic.List<Collider> _ghostCols = new System.Collections.Generic.List<Collider>();
        /// The copy's colliders, grown by margin m, overlap one of this car's: only such a copy shoves our car. One merely in
        /// the car's box (a pickup's is 3.4 m wide: beside the body, under the chassis, at the roadside as it drives past)
        /// had its collisions off while there (review 2026-10-22, #21). A ghost already gets a wider margin: no flicker.
        private static bool OverlapsCar(Ent e, carscript car, float m)
        {
            bool any = false; var b = default(Bounds);
            e.Root.GetComponentsInChildren(false, _ghostCols);
            foreach (var c in _ghostCols)
            {
                if (c == null || c.isTrigger) continue;
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            if (!any) return false;
            int n = Physics.OverlapBoxNonAlloc(b.center, b.extents + Vector3.one * m, _ghostHits, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            var root = car.transform.root;
            for (int i = 0; i < n; i++) if (_ghostHits[i].transform.root == root) return true;
            return false;
        }
        public static float CargoGhostMarginM = 0.1f;
        public static bool CargoGhostOnlyOverlapping = true;   // A/B: false = every copy in the car's box is a ghost

        /// Every copy in our car `car` claimed; `now`: no cooldown (the car just became ours, a copy just appeared in it).
        private static int ClaimContents(carscript car, bool now)
        {
            if (!CargoClaims || !ContentsFollowCar || car == null) return 0;
            int n = 0;
            _contentsScratch.Clear();
            foreach (var e in ByNet.Values) if (ContentsCandidate(e)) _contentsScratch.Add(e);   // ClaimCargo changes owners: not while iterating
            foreach (var e in _contentsScratch)
            {
                if (!InCar(car, e, out bool stored) || !ClaimCargo(e, released: now, reason: ClaimReason.Contents)) continue;
                n++; ContentsClaimed++; if (stored) ContentsStored++;
                if (ContentsClaimed <= 20) { var cb = CarLocalBounds(car); Plugin.Log.LogInfo($"car contents: {e.Root.name} net {e.NetId} (owner {e.OwnerId}{(stored ? ", stored" : "")}) claimed with {car.name}, {Vector3.Distance(e.Root.transform.position, car.transform.position):F1} m from its centre, its box {cb.size.x:F1} x {cb.size.y:F1} x {cb.size.z:F1} m"); }
            }
            return n;
        }
        private static readonly System.Collections.Generic.List<Ent> _contentsScratch = new System.Collections.Generic.List<Ent>();

        /// A copy just placed (Resolve): inside one of our cars, ours before the next physics step.
        private static void ClaimIfPlacedInOwnCar(Ent e)
        {
            if (!CargoClaims || !ContentsFollowCar || !ContentsCandidate(e)) return;
            foreach (var car in _ownCars)
            {
                if (car == null || !InCar(car, e, out bool stored)) continue;
                if (ClaimCargo(e, released: true, reason: ClaimReason.ContentsPlaced))
                {
                    ContentsClaimed++; ContentsOnPlace++; if (stored) ContentsStored++;
                    if (ContentsClaimed <= 20) Plugin.Log.LogInfo($"car contents: {e.Root.name} net {e.NetId} placed in {car.name}, claimed");
                }
                return;
            }
        }

        /// The display copy whose root is `root` (per-frame map, shared with ProxyVelocity).
        private static Ent ProxyOf(Transform root)
        {
            if (root == null) return null;
            ProxyVelocity(root);   // refreshes the map once per frame
            return _proxyByRoot.TryGetValue(root, out var e) ? e : null;
        }
    }
}

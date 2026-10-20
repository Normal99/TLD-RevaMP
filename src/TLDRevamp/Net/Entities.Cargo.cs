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
        public static float CargoClaimCooldownS = 2f;   // per item: no claim tug-of-war between two cars touching it
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
            return p != null && (p.inRightHand || pl != null && pl.pickedUp == p) ||
                   it != null && it.ragdollPos != null && it.ragdollPos.PickedUp();
        }

        /// A copy of an item in its owner's hands: no collisions here (raycasts still see it).
        private static void ShowHeld(Ent e, bool held)
        {
            if (e.Held != held) HeldShown++;
            e.Held = held;
            ShowCarriedCollisions(e);
        }

        private static void ShowCarriedCollisions(Ent e)
        {
            if (e.Root == null) return;
            bool collide = !e.Held && !e.Stored && !e.ReleaseGhost;
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

        /// The host knows a reliable pickup before its copy has received a held pose.
        private static bool CargoHeld(Ent e) => e.Held || IsHost && Server.TryGetValue(e.NetId, out var se) &&
            (se.Held || se.PickupHeld && se.PickupEpoch == se.Epoch);

        /// A loose item's copy that may become this machine's (cooldown, not attached, not a creature): claimed now.
        private static bool ClaimCargo(Ent e, bool released = false, ClaimReason reason = ClaimReason.CargoNear)
        {
            if (!e.Proxy || e.Root == null || CargoHeld(e) || e.Stored) return false;
            float now = Time.realtimeSinceStartup;
            if (!released && now - e.OwnerAt < CargoClaimCooldownS) return false;   // a holder letting go in our car is no tug-of-war
            e.OwnerAt = now;   // one try per cooldown, claimed or not
            var it = e.Root;
            if (!CargoBody(e) || (it.attachable != null && it.attachable.attached) || RidesParent(e)) return false;
            if (e.WornBy >= 0) return false;   // on another player's head (Entities.Worn): theirs
            if (it.GetComponentInChildren<newAiScript>() != null && (Limbs(e) == null || !Limp(e))) return false;   // a living creature thinks where it is owned (Entities.Ai)
            // like a pickup (PickupClaim): ours at once, so it is a real body in this car from the next physics step; a
            // refusal (held there after all) hands it back
            SetProxy(e, false);
            e.OwnerId = MyId;
            W.Reset(); WriteClaim(W, e.NetId, reason); ToServer(W, true);
            ClaimsSent++; CargoClaimed++;
            return true;
        }

        /// Claimed on contact, the copy had already hit: a kinematic body gives nothing, so the car took the whole
        /// impulse in that physics step (buglist2m, two machines: a lamp dropped onto the host's seat spun the parked
        /// car 371 deg/s, left it on its side, a hubcap off; the lamp, made real while sunk into the seat, fell through
        /// the floor). Now an item is claimed while it COMES: a copy within a margin of a car simulated here — 0.3 m plus
        /// three physics steps of their closing speed — is this machine's before the step that would touch. The touch is
        /// then the game's own physics between a 1 kg lamp and a car. Contact stays as the fallback.
        public static float CargoNearM = 0.3f, CargoNearSteps = 3f;
        public static long CargoClaimedNear, CargoClaimedOnContact;
        private static readonly System.Collections.Generic.Dictionary<carscript, Bounds> _carBounds = new System.Collections.Generic.Dictionary<carscript, Bounds>();
        private static float _carBoundsAt;
        private static void CargoNearTick(float dt)
        {
            if (!CargoClaims || _localCars.Count == 0) return;
            if (Time.time - _carBoundsAt > 10f) { _carBoundsAt = Time.time; _carBounds.Clear(); }
            foreach (var e in ByNet.Values)
            {
                if (!e.Proxy || e.Root == null || e.Root.car != null || e.PartIndex >= 0 || e.Held || e.Stored || e.Physical || e.CrashTaking || !CargoBody(e)) continue;
                var irb = e.Root.GetComponent<Rigidbody>();
                if (irb == null || !irb.isKinematic || !irb.detectCollisions) continue;
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
                    if (!lb.Contains(car.transform.InverseTransformPoint(p))) continue;
                    if (ClaimCargo(e)) CargoClaimedNear++;
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
                        if (e == null || !e.Proxy || e.Root == null || e.Driven || CargoHeld(e) || e.Stored || e.CrashTaking || e.PartIndex >= 0) continue;
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
            e.Proxy && e.Root != null && e.Root.car == null && e.PartIndex < 0 && CargoBody(e) && !e.Held && !e.Stored && e.WornBy < 0 && !e.CrashTaking;   // (worn: a passenger's hat)

        /// Stored in one of the car's containers, or lying inside the car's box and in no other container.
        private static bool InCar(carscript car, Ent e, out bool stored)
        {
            var pl = e.Root.P != null ? e.Root.P.physlock : null;
            stored = pl != null && pl.transform.root == car.transform.root;
            if (stored) return !LockedAway(e);
            if (pl != null) return false;
            var b = CarLocalBounds(car);
            b.Expand(2f * ContentsMarginM / Mathf.Max(0.01f, car.transform.lossyScale.x));
            return b.Contains(car.transform.InverseTransformPoint(e.Root.transform.position));
        }

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
                if (ContentsClaimed <= 20) Plugin.Log.LogInfo($"car contents: {e.Root.name} net {e.NetId} (owner {e.OwnerId}{(stored ? ", stored" : "")}) claimed with {car.name}");
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

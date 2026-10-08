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
        private static bool IsHeld(tosaveitemscript it)
        {
            var p = it != null ? it.P : null;
            if (p == null) return false;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            return p.inRightHand || (pl != null && pl.pickedUp == p);
        }

        /// A copy of an item in its owner's hands: no collisions here (raycasts still see it).
        private static void ShowHeld(Ent e, bool held)
        {
            if (e.Held != held) HeldShown++;
            e.Held = held;
            if (e.Root == null) return;
            foreach (var b in e.Root.GetComponentsInChildren<Rigidbody>(true))
                if (b.detectCollisions == held) b.detectCollisions = !held;
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
            if (ClaimCargo(e)) CargoClaimedOnContact++;
        }

        /// A loose item's copy that may become this machine's (cooldown, not attached, not a creature): claimed now.
        private static bool ClaimCargo(Ent e, bool released = false)
        {
            float now = Time.realtimeSinceStartup;
            if (!released && now - e.OwnerAt < CargoClaimCooldownS) return false;   // a holder letting go in our car is no tug-of-war
            e.OwnerAt = now;   // one try per cooldown, claimed or not
            var it = e.Root;
            if (it.P == null || (it.attachable != null && it.attachable.attached) || RidesParent(e)) return false;
            if (it.GetComponentInChildren<newAiScript>() != null) return false;   // a creature thinks where it is owned (Entities.Ai)
            // like a pickup (PickupClaim): ours at once, so it is a real body in this car from the next physics step; a
            // refusal (held there after all) hands it back
            SetProxy(e, false);
            e.OwnerId = MyId;
            W.Reset(); W.U8(Claim); W.U32(e.NetId); ToServer(W, true);
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
                if (!e.Proxy || e.Root == null || e.Root.car != null || e.PartIndex >= 0 || e.Held || e.Stored || e.Physical || e.CrashTaking || e.Root.P == null) continue;
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

        /// The display copy whose root is `root` (per-frame map, shared with ProxyVelocity).
        private static Ent ProxyOf(Transform root)
        {
            if (root == null) return null;
            ProxyVelocity(root);   // refreshes the map once per frame
            return _proxyByRoot.TryGetValue(root, out var e) ? e : null;
        }
    }
}

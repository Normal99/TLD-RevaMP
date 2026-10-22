using UnityEngine;

namespace TLDRevamp.Net
{
    /// A loose item simulated here, moving into a loose item another player owns (dropped onto their tyre, say). Each
    /// machine held a kinematic copy of the other's item, so each simulated half the touch: here ours landed on a copy that
    /// can't move; there our copy, moving at our pose with no mass limit, shoved their real item out of the way, and the
    /// relayed contact impulse pushed it a second time. The two screens then disagreed until the next states arrived.
    /// Now, like walking into one (Entities.BodyPush), the moving item takes the still one before it touches: a loose copy
    /// within a margin ahead of an item moving here is claimed, and both are simulated on this machine. A still item's copy
    /// is where its owner has it, so its handover is seamless (handing the moving one over would restart it at a pose its
    /// new owner saw ~0.1 s late). Cars keep their own rule (Entities.Cargo: a car takes what lands on it); creatures
    /// theirs (Entities.Rabbits). Contact stays as the fallback. Items in hands or inventories, worn, attached or bodies
    /// still alive are never claimed (Pushable, ClaimCargo, and the server refuses).
    public static partial class Entities
    {
        public static bool ItemClaims = true;          // A/B: false = a moving item meets a frozen copy of the other
        public static float ItemNearM = 0.15f, ItemMinSpeed = 0.5f, ItemNearSteps = 3f;
        public static long ItemClaimedNear, ItemClaimedOnContact;
        private static readonly Collider[] _itemNear = new Collider[32];

        /// Per physics step, before the copies move (Entities.Physical): loose copies ahead of loose items moving here.
        private static void ItemNearTick(float dt)
        {
            if (!ItemClaims || !InSession) return;
            foreach (var e in ByNet.Values)
            {
                var rb = MovingItemBody(e);
                if (rb == null) continue;
                var v = rb.velocity;
                bool any = false; var b = default(Bounds);
                foreach (var c in e.Root.GetComponentsInChildren<Collider>(false))
                {
                    if (c == null || c.isTrigger || !c.enabled) continue;
                    if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
                }
                if (!any) continue;
                float margin = ItemNearM + v.magnitude * dt * ItemNearSteps;
                int n = Physics.OverlapBoxNonAlloc(b.center, b.extents + Vector3.one * margin, _itemNear, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                var own = e.Root.transform.root;
                for (int i = 0; i < n; i++)
                {
                    var orb = _itemNear[i].attachedRigidbody;
                    if (orb == null || !orb.isKinematic || orb.transform.root == own || Vector3.Dot(v, orb.worldCenterOfMass - rb.worldCenterOfMass) <= 0f) continue;   // only what it moves toward
                    var other = ProxyOf(orb.transform.root);
                    if (Pushable(other) && NyOf(other) == null && ClaimCargo(other, reason: ClaimReason.ItemNear)) ItemClaimedNear++;
                }
            }
        }

        /// From ContactRelay: a loose item simulated here touched a loose copy (it moved there faster than the margin covered).
        internal static void ItemContact(uint net, Collision c)
        {
            if (!ItemClaims || !InSession) return;
            var orb = c.rigidbody;
            if (orb == null || !orb.isKinematic || !ByNet.TryGetValue(net, out var mine)) return;
            var rb = MovingItemBody(mine);
            if (rb == null || Vector3.Dot(rb.velocity, orb.worldCenterOfMass - rb.worldCenterOfMass) <= 0f) return;
            var other = ProxyOf(orb.transform.root);
            if (Pushable(other) && NyOf(other) == null && ClaimCargo(other, reason: ClaimReason.ItemContact)) ItemClaimedOnContact++;
        }

        /// The body of a loose item simulated here that is moving (dropped, thrown, falling, knocked): not a car or a car's
        /// part, not in this player's hands, not a creature; null otherwise. Resting items claim nothing (two touching
        /// items at rest would otherwise take each other in turn).
        private static Rigidbody MovingItemBody(Ent e)
        {
            if (e == null || e.Proxy || e.Root == null || e.Root.car != null || !LooseItem(e) || e.Stored || e.Root.P == null || NyOf(e) != null || IsHeld(e.Root)) return null;
            var rb = e.Root.GetComponent<Rigidbody>();
            if (rb == null || rb.isKinematic || !rb.detectCollisions || rb.IsSleeping() || rb.velocity.sqrMagnitude < ItemMinSpeed * ItemMinSpeed) return null;
            return rb;
        }
    }
}

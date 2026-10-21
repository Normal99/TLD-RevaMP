using UnityEngine;

namespace TLDRevamp.Net
{
    /// Walking into a loose item another player owns. Its copy here is kinematic - moved to where the owner has it - so
    /// the player's body met a wall and the item stayed frozen. Now, like a car's cargo (Entities.Cargo), it becomes this
    /// machine's before the body touches it: a loose item within a margin ahead of the walking player is claimed (at once
    /// here, the server confirms or hands it back), and the game's own physics pushes it. Contact stays as the fallback.
    /// Items in hands or inventories, worn, attached or creatures are never claimed (ClaimCargo, and the server refuses).
    public static partial class Entities
    {
        public static bool BodyClaims = true;          // A/B: false = items stay frozen until their owner moves them
        public static float BodyNearM = 0.25f, BodyMinSpeed = 0.3f;
        public static long BodyClaimedNear, BodyClaimedOnContact;
        private static readonly Collider[] _bodyNear = new Collider[32];

        /// Per physics step, before the copies move (Entities.Physical): loose copies ahead of the walking local player.
        private static void BodyNearTick(float dt)
        {
            if (!BodyClaims || !InSession) return;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || pl.Bsitting || pl.RB == null || pl.CBody == null) return;
            if (pl.GetComponent<BodyContactRelay>() == null) pl.gameObject.AddComponent<BodyContactRelay>();
            var v = pl.RB.velocity; v.y = 0f;
            float speed = v.magnitude;
            if (speed < BodyMinSpeed) return;
            var c = pl.CBody; var t = c.transform;
            float r = c.radius * Mathf.Max(t.lossyScale.x, t.lossyScale.z), half = Mathf.Max(0f, c.height * t.lossyScale.y * .5f - r);
            var center = t.TransformPoint(c.center); var up = t.up;
            float margin = BodyNearM + speed * dt * 3f;
            int n = Physics.OverlapCapsuleNonAlloc(center - up * half, center + up * half, r + margin, _bodyNear, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var rb = _bodyNear[i].attachedRigidbody;
                if (rb == null || !rb.isKinematic || Vector3.Dot(v, rb.worldCenterOfMass - center) <= 0f) continue;   // only what the player walks into
                var e = ProxyOf(rb.transform.root);
                if (e == null || e.Root == null || e.Root.car != null || e.Held || e.Stored || e.Physical) continue;
                if (ClaimCargo(e, reason: ClaimReason.BodyNear)) BodyClaimedNear++;
            }
        }

        /// The fallback: the local player's body touched a loose copy (it moved there faster than the margin covered).
        internal static void BodyContact(Collision c)
        {
            if (!BodyClaims || !InSession) return;
            var rb = c.rigidbody;
            if (rb == null || !rb.isKinematic) return;
            var e = ProxyOf(rb.transform.root);
            if (e == null || e.Root == null || e.Root.car != null || e.Held || e.Stored || e.Physical) return;
            if (ClaimCargo(e, reason: ClaimReason.BodyContact)) BodyClaimedOnContact++;
        }
    }

    /// On the local player's body: its collisions with copies (Entities.BodyContact).
    internal sealed class BodyContactRelay : MonoBehaviour
    {
        private void OnCollisionEnter(Collision c) => Entities.BodyContact(c);
        private void OnCollisionStay(Collision c) => Entities.BodyContact(c);
    }
}

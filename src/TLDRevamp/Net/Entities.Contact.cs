using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Contact impulses (contact A+B, part A): when a machine's own car collides with another machine's car, the
    /// physics impulse is real only on the simulator's side — the other machine holds a kinematic proxy, which
    /// blocks but never reacts. The simulator sends the reaction impulse (Newton's third law: -collision.impulse)
    /// plus the contact point; the owner applies it to their real body with AddForceAtPosition. Damage needs no
    /// message: the proxy's motion already produces real collision events on the owner's machine.
    ///
    /// Slow sustained contact (shoving) rides the same path: OnCollisionStay sends throttled follow-up impulses.
    public static partial class Entities
    {
        public const byte ContactImpulse = 36;
        public const byte DetachReq = 37;
        public const byte SleepSync = 38;   // a player slept: the world clock jumped for them, everyone follows
        internal const float MinImpulse = 0.5f;        // Ns: below this a contact is noise
        internal const float StayResendS = 0.1f;
        public static long ContactSent, ContactRelayed, ContactApplied, ContactMisses;
        public static float ContactImpulseMaxSent, ContactImpulseMaxApplied;   // Ns, reset by CrashLab

        /// The synced entity whose root is `root`, and whether it's a display copy here.
        internal static bool NetOf(Transform root, out uint net, out bool proxy)
        {
            net = 0; proxy = false;
            if (root == null) return false;
            foreach (var e in ByNet.Values)
                if (e.Root != null && e.Root.transform.root == root) { net = e.NetId; proxy = e.Proxy; return true; }
            return false;
        }

        /// Applied on a shared entity's root while it is simulated here; removed when it becomes a proxy.
        public class ContactRelay : MonoBehaviour
        {
            [System.NonSerialized] public uint NetId;
            private float _nextStay;

            private void OnCollisionEnter(Collision c) => Handle(c, true);
            private void OnCollisionStay(Collision c) { if (Time.unscaledTime >= _nextStay) Handle(c, false); }

            private void Handle(Collision c, bool enter)
            {
                if (!InSession || NetId == 0 || !ContactRelayOn) return;   // off: both machines simulate the crash (Entities.Physical)
                var otherRb = c.rigidbody;
                if (otherRb == null) return;
                // the other body must belong to another machine's entity
                Ent other = null;
                var otherGo = otherRb.transform.root.gameObject;
                foreach (var e in ByNet.Values)
                    if (e.Root != null && (e.Root.gameObject == otherGo || e.Root.transform == otherRb.transform.root))
                    { other = e; break; }
                if (other == null || !other.Proxy || other.NetId == NetId) return;
                var impulse = -c.impulse;                       // what the other body receives (equal and opposite)
                if (impulse.sqrMagnitude < MinImpulse * MinImpulse) return;
                if (!enter) { if (Time.unscaledTime < _nextStay) return; _nextStay = Time.unscaledTime + StayResendS; }
                var point = c.GetContact(0).point;
                var gp = mainscript.GlobalFromUnityPos(point);
                W.Reset(); W.U8(ContactImpulse); W.U32(other.NetId);
                W.F32(impulse.x); W.F32(impulse.y); W.F32(impulse.z);
                W.F64(gp.x); W.F64(gp.y); W.F64(gp.z);
                ToServer(W, true);
                ContactSent++;
                if (impulse.magnitude > ContactImpulseMaxSent) ContactImpulseMaxSent = impulse.magnitude;
            }
        }

        /// Ownership flips and proxy flips drive the relay's lifetime.
        internal static void AttachContact(Ent e)
        {
            if (e.Root == null || e.Root.gameObject.GetComponent<ContactRelay>() != null) return;
            var relay = e.Root.gameObject.AddComponent<ContactRelay>();
            relay.NetId = e.NetId;
        }

        internal static void DetachContact(Ent e)
        {
            if (e.Root == null) return;
            var relay = e.Root.gameObject.GetComponent<ContactRelay>();
            if (relay != null) UnityEngine.Object.Destroy(relay);
        }

        /// Server: the crasher's reaction impulse goes to the hit entity's owner.
        internal static void ServerContactImpulse(int from, uint net, Vector3 impulse, Vector3d globalPoint)
        {
            if (!Server.TryGetValue(net, out var se))
            {
                if (ContactMisses++ < 5) Plugin.Log.LogInfo($"contact relay miss: net {net} not in Server ({Server.Count} entities, from {from})");
                return;
            }
            W.Reset(); W.U8(ContactImpulse); W.U32(net);
            W.F32(impulse.x); W.F32(impulse.y); W.F32(impulse.z);
            W.F64(globalPoint.x); W.F64(globalPoint.y); W.F64(globalPoint.z);
            ServerSendTo(se.OwnerId, W, false);
            ContactRelayed++;
        }

        private static void ApplyContactImpulse(uint net, Vector3 impulse, Vector3d globalPoint)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null || e.Proxy) return;
            var rb = e.Root.GetComponent<Rigidbody>();
            if (rb == null || rb.isKinematic) return;
            var point = mainscript.UnityPosFromGlobal(globalPoint);
            rb.AddForceAtPosition(impulse, point, ForceMode.Impulse);
            ContactApplied++;
            if (impulse.magnitude > ContactImpulseMaxApplied) ContactImpulseMaxApplied = impulse.magnitude;
        }

        /// Server: a sleep ended — everyone's clock jumps to the sleeper's world time.
        internal static void ServerSleepSync(int from, float worldTime)
        {
            W.Reset(); W.U8(SleepSync); W.F32(worldTime);
            ServerSendAll(W, true, from);   // except==from: the sleeper is already there (Deliver covers the host)
        }

        /// Server: forward a non-owner's wrench request to the car's owner.
        internal static void ServerDetachReq(int from, uint net, int idx)
        {
            if (!Server.TryGetValue(net, out var se)) return;
            if (se.OwnerId == from) return;   // the owner detaches for real itself; a request comes from someone else
            W.Reset(); W.U8(DetachReq); W.U32(net); W.VarU32((uint)idx);
            ServerSendTo(se.OwnerId, W, true);
        }

        /// The owner runs the real detach; its DetachHook postfix broadcasts the PartOff to everyone.
        public static long DetachReqsApplied;
        internal static void ApplyDetachReq(int from, uint net, int idx)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null || e.Proxy) return;
            var it = idx >= 0 && idx < e.Items.Count ? e.Items[idx] : idx == 0 ? e.Root : null;
            if (it == null) return;
            var at = it.attachable;
            if (at == null || !at.attached) return;
            // the game's own dismount (un-bolt, then Detach): DetachHook's postfix reports the PartOff to everyone
            if (Unmount(at, false)) DetachReqsApplied++;
        }
    }
}

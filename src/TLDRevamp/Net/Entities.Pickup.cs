using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private static bool PickupLimb(Ent e, pickupable p) =>
            e.Root != null && e.Root.ragdollPos != null && e.Root.ragdollPos.ps != null && System.Array.IndexOf(e.Root.ragdollPos.ps, p) >= 0;

        internal enum ClaimReason : byte
        {
            Driver = 1, DriverRetry, Pickup, CargoNear, CargoContact, CargoRelease, Contents, ContentsPlaced,
            CarCargo, RabbitNear, RabbitContact, RabbitObjectNear, RabbitObjectContact, PushGrip, Wear, Debug
        }

        private static string ClaimName(ClaimReason reason) => "claim: " + reason.ToString();
        private static void WriteClaim(NetWriter w, uint net, ClaimReason reason)
        {
            w.U8(Claim); w.U32(net); w.U8((byte)reason);
        }

        // Protocol 42: every Claim identifies its producer; pickups also carry the epoch and owner-clock fence.
        internal static void WritePickupClaim(NetWriter w, uint net, uint epoch, double stamp)
        {
            WriteClaim(w, net, ClaimReason.Pickup); w.U32(epoch); w.U32((uint)(long)System.Math.Round(stamp * 1000.0));
        }

        private static void ServerClaim(int from, NetReader r)
        {
            uint net = r.U32();
            var reason = (ClaimReason)r.U8();
            bool pickup = reason == ClaimReason.Pickup;
            uint pickupEpoch = pickup ? r.U32() : 0, pickupStamp = pickup ? r.U32() : 0;
            if (r.Bad || r.Remaining != 0 || reason < ClaimReason.Driver || reason > ClaimReason.Debug || !Server.TryGetValue(net, out var se)) return;
            if (se.OwnerId == from)
            {
                if (pickup && pickupEpoch == se.Epoch) ServerPickupHeld(se, pickupStamp);
                return;
            }
            if (se.HitchCar != 0) { RefuseClaim(from, se, "hitched to car", reason); return; }
            // a crash is being simulated on another machine: its car comes back when the crash is over
            if (se.CrashReturnTo >= 0)
            {
                RefuseClaim(from, se, "crash lease (returns to " + se.CrashReturnTo + ")", reason);
                return;
            }
            // in the owner's hands or inventory: nobody takes it by touch (a car's cargo claim, Entities.Cargo) or grab
            // (a stored copy is hidden, colliders off, and the claimers skip it — the server refuses it anyway)
            if (se.Held || se.Stored)
            {
                RefuseClaim(from, se, se.Held ? "held" : "stored", reason);
                return;
            }
            // on its owner's head (Entities.Worn): theirs until they take it off
            if (WornSync && ServerWornNow(se)) { RefuseClaim(from, se, "worn", reason); return; }
            // the first driver keeps the car: no takeover while the owner's player sits in its driver seat
            // (the owner reports it with every state; a stale report counts as not driving)
            if (se.Driven && Time.realtimeSinceStartup - se.DrivenAt < 1f)
            {
                // refused: the claimer may already simulate it (PickupClaim takes provisional ownership) —
                // tell it who owns it, or both machines simulate the object and their copies drift apart
                RefuseClaim(from, se, "driven", reason);
                return;
            }
            string seatTaken = DriverSeatClaimRefusal(se, from);   // someone else at its wheel (Entities.Seat)
            if (seatTaken != null) { DriverSeatRefused++; RefuseClaim(from, se, seatTaken, reason); return; }
            se.ClaimedBy = from; se.ClaimedAt = Time.realtimeSinceStartup;
            OwnerLog(se, from, ClaimName(reason) + (se.Driven ? " (owner's driver report " + (Time.realtimeSinceStartup - se.DrivenAt).ToString("F1") + " s old)" : ""));
            se.OwnerId = from; se.Epoch++;
            if (pickup)
            {
                ServerPickupHeld(se, pickupStamp);
                // The new epoch rejects pre-claim poses; its first physics stamp can precede the pickup frame.
                se.ReceivedStateAt = 0;
            }
            if (se.Rabbit) se.RabbitHoldUntil = Time.realtimeSinceStartup + RabbitHoldS;   // a car about to hit it: the nearest-player rule waits
            W.Reset(); W.U8(Owner); W.U32(net); W.VarU32((uint)from); W.U32(se.Epoch);
            ServerSendAll(W, true, -1);
            CascadeOwner(se);
        }

        private static void ServerPickupHeld(SEnt se, uint stamp)
        {
            double at = stamp / 1000.0;
            se.Held = true; se.PickupHeld = true; se.PickupEpoch = se.Epoch; se.PickupPending = true;
            // The reliable owner notification is authoritative even if an unreliable pose overtook it.
            // Keep the pose fence monotonic; a same-millisecond pose must not veto the pickup itself.
            if (se.ReceivedStateEpoch == se.Epoch) at = System.Math.Max(at, se.ReceivedStateAt);
            // Older unreliable poses must not clear a reliable pickup before the next held pose arrives.
            se.ReceivedStateEpoch = se.Epoch; se.ReceivedStateAt = at;
        }

        private static bool ServerPickupPose(SEnt se, bool held, bool released)
        {
            // Ordinary poses cannot revoke a reliable pickup; a forced new owner starts a new lease.
            return !se.PickupHeld || se.PickupEpoch != se.Epoch || held || released;
        }

        private static void ServerHeldState(SEnt se, bool held)
        {
            se.Held = held;
            if (!held) se.PickupHeld = false;
        }

        // ------------------------------------------------------------------ picking up someone else's item

        /// Picking up an item another machine owns: take it at once (provisional ownership, a server round trip at 150–
        /// 200 ms would be felt) and tell the server; the rare same-instant conflict is settled there.
        [HarmonyLib.HarmonyPatch(typeof(pickupable), nameof(pickupable.Pickup))]
        private static class PickupClaim
        {
            [HarmonyLib.HarmonyPrefix]
            internal static void Prefix(pickupable __instance, out Ent __state)
            {
                __state = null;
                if (!InSession) return;
                var limbOf = RagdollOf(__instance);   // a limb of a dead creature: not under the item any more
                Ent picked = null;
                foreach (var e in ByNet.Values)
                    if (e.Root != null && (e == limbOf || e.Root.P == __instance || PickupLimb(e, __instance) || e.Root.gameObject == __instance.gameObject))
                    { picked = e; break; }
                if (picked == null)
                    foreach (var e in ByNet.Values)
                        if (e.Root != null && e.Root.car == null && !(HitchTrailer(e) && e.Root.attachable.attached) && e.Root.transform == __instance.transform.root)
                        { picked = e; break; }
                if (picked == null || picked.Root.car != null || HitchTrailer(picked) && picked.Root.attachable.attached) return;
                __state = picked;
                if (picked.Proxy) { SetProxy(picked, false); picked.OwnerId = MyId; }
            }

            [HarmonyLib.HarmonyPostfix]
            internal static void Postfix(pickupable __instance, Ent __state)
            {
                if (__state == null || !__instance.pickedUp) return;
                __state.SentHeld = true;   // even a release before the first pose must explicitly end this pickup
                __state.LastPoseStamp = PoseStamp(Time.unscaledTimeAsDouble, __state.LastPoseStamp, false);
                W.Reset(); WritePickupClaim(W, __state.NetId, __state.Epoch, __state.LastPoseStamp);
                ToServer(W, true); ClaimsSent++;
            }
        }
    }
}

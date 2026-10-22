using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        internal enum ClaimReason : byte
        {
            Driver = 1, DriverRetry, Pickup, CargoNear, CargoContact, CargoRelease, Contents, ContentsPlaced,
            CarCargo, RabbitNear, RabbitContact, RabbitObjectNear, RabbitObjectContact, PushGrip, Wear, BodyNear, BodyContact, Debug, ItemNear, ItemContact
        }

        private static string ClaimName(ClaimReason reason) => "claim: " + reason.ToString();
        private static void WriteClaim(NetWriter w, uint net, ClaimReason reason)
        {
            w.U8(Claim); w.U32(net); w.U8((byte)reason);
        }

        private static void ServerClaim(int from, NetReader r)
        {
            uint net = r.U32();
            var reason = (ClaimReason)r.U8();
            if (r.Bad || r.Remaining != 0 || reason < ClaimReason.Driver || reason > ClaimReason.ItemContact || !Server.TryGetValue(net, out var se) || se.OwnerId == from) return;
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
            se.ClaimedBy = from; se.ClaimedAt = Time.realtimeSinceStartup; se.ClaimedFromEpoch = se.Epoch;
            OwnerLog(se, from, ClaimName(reason) + (se.Driven ? " (owner's driver report " + (Time.realtimeSinceStartup - se.DrivenAt).ToString("F1") + " s old)" : ""));
            se.OwnerId = from; se.Epoch++;
            if (se.Rabbit) se.RabbitHoldUntil = Time.realtimeSinceStartup + RabbitHoldS;   // a car about to hit it: the nearest-player rule waits
            W.Reset(); W.U8(Owner); W.U32(net); W.VarU32((uint)from); W.U32(se.Epoch);
            ServerSendAll(W, true, -1);
            CascadeOwner(se);
        }
    }
}

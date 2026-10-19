using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// While a player is seated in a shared car that is a PROXY on their machine, the car's colliders are kinematic
    /// and the player's CharacterController depenetrates against them — shoving the player until the game's
    /// reposition method force-ejects them 40-60 m away (the passenger flicker/eject). The player's capsule ignores
    /// the seated car's colliders for the duration of the sit (restored on exit).
    public static partial class Entities
    {
        private static readonly List<(Collider pc, Collider cc)> _ignoredPairs = new List<(Collider, Collider)>();
        public static bool SeatIgnoreOnProxy = true;   // A/B: false = before v0.69.2 (only cars that were copies at GetIn)
        public static bool SeatIgnoreGroup = true;     // A/B: false = before v0.69.2 (the car's hierarchy only, not its jointed doors)
        public static bool DrivenSeatTaken = true;     // A/B: false = before v0.69.2 (a seat is taken only by a bound remote body)
        public static bool ClaimAtGetIn = true;        // A/B: false = before v0.69.2 (the driver claim up to 0.5 s after sitting)
        public static bool TwoAtWheelOut = true;       // A/B: false = before v0.69.2 (a second player at a driven wheel stays)
        public static bool DrivenChangeSent = true;    // A/B: false = before v0.69.2 (sitting down at a parked car's wheel sent nothing)
        public static long TwoAtWheelOuts;
        private static float _claimedAt;

        /// A copy whose owner's states say its player sits at the wheel: lately (each state carries it), or the last one
        /// did and the owner's player still sits in this car (a parked car sends no more states).
        internal static bool DrivenByOwner(Ent e) =>
            e.Driven && (Time.realtimeSinceStartup - e.DrivenSeenAt < 1f || RemotePlayers.SeatedIn(e.OwnerId, e.NetId));

        /// Our claim on the car at our wheel went out lately: a refused one leaves the car someone else's copy.
        private static bool ClaimPending() => Time.realtimeSinceStartup - _claimedAt < 3f;

        private static Collider PlayerCollider(fpscontroller pl)
        {
            var cc = pl.GetComponent<CharacterController>();
            if (cc != null) return cc;
            return pl.GetComponent<CapsuleCollider>();
        }

        internal static void SeatCollisionIgnore(fpscontroller pl, seatscript seat, Ent e = null)
        {
            var pc = PlayerCollider(pl);
            if (pc == null) return;
            var root = seat.transform.root;
            foreach (var col in root.GetComponentsInChildren<Collider>(true)) IgnorePair(pc, col);
            // jointed parts (doors, lids: parentAtAttach off) are members of the car but not under it
            if (SeatIgnoreGroup && e != null)
                foreach (var it in e.Items)
                    if (it != null && it.transform.root != root)
                        foreach (var col in it.GetComponentsInChildren<Collider>(true)) IgnorePair(pc, col);
        }

        private static void IgnorePair(Collider pc, Collider col)
        {
            if (col == null || col == pc) return;
            Physics.IgnoreCollision(pc, col, true);
            _ignoredPairs.Add((pc, col));
        }

        /// Our player sat in a car that was OURS at GetIn (nothing ignored) and it became a copy under them: the other
        /// player took the wheel as we got in, a rabbit or cargo claim, a hand-off. Kinematic colliders around a seated
        /// controller: the shove/eject above (playtest 2026-10-18, "he killed himself by getting in the car").
        internal static void SeatedBecameProxy(Ent e)
        {
            if (!SeatIgnoreOnProxy || !InSession || e.Root == null || e.Root.transform != e.Root.transform.root) return;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !pl.Bsitting || pl.seat == null || pl.seat.transform.root != e.Root.transform) return;
            SeatCollisionIgnore(pl, pl.seat, e);
        }

        /// Both players at one wheel, the other one's car: out on the seat's own side (as DriveLab.GetOut: let out into
        /// the body, a player was flung to death - wearsync.py C).
        private static void LeaveTakenWheel(fpscontroller pl)
        {
            var st = pl.seat.transform; var car = st.root;
            float side = Vector3.Dot(st.position - car.position, car.right) > 0.2f ? 1f : -1f;
            pl.GetOut(st.position + st.up * 1.5f + car.right * (2f * side), true);
            TwoAtWheelOuts++;
        }

        internal static void SeatCollisionRestore(fpscontroller pl)
        {
            var pc = PlayerCollider(pl);
            if (pc == null) { _ignoredPairs.Clear(); return; }
            foreach (var pair in _ignoredPairs)
                if (pair.pc == pc && pair.cc != null) Physics.IgnoreCollision(pair.pc, pair.cc, false);
            _ignoredPairs.RemoveAll(pair => pair.pc == pc);
        }

        [HarmonyLib.HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.GetIn), new[] { typeof(seatscript) })]
        private static class SeatEnterCollisionFix
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(fpscontroller __instance, seatscript _u)
            {
                if (!InSession) return;
                if (ClaimAtGetIn && _u != null && _u.driverSeat0) _driverCheck = 1f;   // CheckDriverSeat claims on the next tick
                Ent any = null;
                foreach (var e in ByNet.Values)
                    if (e.Root != null && e.Proxy && _u.transform.root == e.Root.transform.root)
                    {
                        if (e.Root.transform == e.Root.transform.root) { any = e; break; }   // the car, not a copy riding in it
                        if (any == null) any = e;
                    }
                if (any != null) SeatCollisionIgnore(__instance, _u, any.Root.transform == any.Root.transform.root ? any : null);
            }
        }

        /// Seat availability is OURS: vanilla's FreeCanSit consults inUseMulti, the old MP's seat-reservation
        /// flag — its messages never flow in our session, so a seat the host once used stays locked on clients
        /// forever. Decide from our own remote-player seat bindings instead (the local player's occupancy is
        /// still handled by inUse, which the prefix keeps).
        [HarmonyLib.HarmonyPatch(typeof(seatscript), nameof(seatscript.FreeCanSit))]
        private static class SeatFreeOverride
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(seatscript __instance, ref bool __result)
            {
                if (!InSession || !__result) return;
                var main = __instance.mainseat != null ? __instance.mainseat : __instance;   // where E on this seat puts you
                if (RemotePlayers.AnyRemoteInSeat(__instance) || RemotePlayers.AnyRemoteInSeat(main)) { __result = false; return; }
                // a copy's driver seat its owner reports driving: taken, whether or not this machine bound the owner's body
                // to it (both pressed E within a state or two: both sat at one wheel, playtest 2026-10-18)
                if (DrivenSeatTaken && main.driverSeat0)
                {
                    var e = ProxyOf(main.transform.root);
                    if (e != null && e.Root != null && e.Root.transform == main.transform.root && DrivenByOwner(e)) __result = false;
                }
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.GetOut), new[] { typeof(Vector3), typeof(bool) })]
        private static class SeatExitCollisionRestore
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(fpscontroller __instance, Vector3 pos, bool forced)
            {
                if (!InSession || !Mp.RemoteTrace) return;
                Plugin.Log.LogInfo($"SeatTrace: GetOut forced={forced} seated={__instance.Bsitting} seat={(__instance.seat != null ? __instance.seat.name : "null")} at={__instance.transform.position:F1}\n{System.Environment.StackTrace}");
            }

            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(fpscontroller __instance) => SeatCollisionRestore(__instance);
        }
    }
}

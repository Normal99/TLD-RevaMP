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

        private static Collider PlayerCollider(fpscontroller pl)
        {
            var cc = pl.GetComponent<CharacterController>();
            if (cc != null) return cc;
            return pl.GetComponent<CapsuleCollider>();
        }

        internal static void SeatCollisionIgnore(fpscontroller pl, seatscript seat)
        {
            var pc = PlayerCollider(pl);
            if (pc == null) return;
            foreach (var col in seat.transform.root.GetComponentsInChildren<Collider>(true))
            {
                if (col == null || col == pc) continue;
                Physics.IgnoreCollision(pc, col, true);
                _ignoredPairs.Add((pc, col));
            }
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
                if (mainscript.s != null && mainscript.s.player == __instance)
                { LocalSeat(out uint net, out int seat); if (net != 0) SessionLog.Event("seat-enter", net, slot: seat, from: MyId); }
                foreach (var e in ByNet.Values)
                    if (e.Root != null && e.Proxy && _u.transform.root == e.Root.transform.root)
                    { SeatCollisionIgnore(__instance, _u); return; }
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
                if (RemotePlayers.AnyRemoteInSeat(__instance)) __result = false;
            }
        }

        /// One driver per car (playtest 2026-10-18, "managed to sit in the same driver seat"). FreeCanSit can only see a
        /// seat as taken once the other player's seated state has come in; two players sitting down within a state of each
        /// other both sat at the wheel (seatrace: 150 ms lag, both in seat 4, the host owning the car). The server now
        /// refuses a car claim while another player's reported seat is its driver seat, or while another player's claim of
        /// it is fresh (their seat report is still on its way: seated players send 2 states a second). The one whose claim
        /// was refused sits in the driver seat of a car someone else drives: they are moved to a free seat of it (out of the
        /// car if none is free), with a note why.
        public static bool DriverSeatGuard = true;   // A/B: false = before v0.69.2 (two players at one wheel)
        public static float DriveClaimHoldS = 1.5f;
        public static long DriverSeatRefused, DriverSeatMoved, DriverSeatOut;
        private static readonly List<KeyValuePair<int, int>> _seatedIn = new List<KeyValuePair<int, int>>();

        /// Server: who other than `from` sits in this car's driver seat (-1: nobody, or the car isn't loaded on the host).
        private static int DriverSeatTakenBy(SEnt se, int from)
        {
            if (!ByNet.TryGetValue(se.NetId, out var e) || !Resolve(e) || e.Root == null || e.Root.car == null) return -1;
            var seats = VehicleSeats(e.Root);
            bool Driver(int i) => i >= 0 && i < seats.Length && seats[i] != null && seats[i].driverSeat0;
            if (from != 0 && !DedicatedServer.Enabled)
            {
                LocalSeat(out uint n, out int i);
                if (n == se.NetId && Driver(i)) return 0;
            }
            Mp.Server?.SeatedIn(se.NetId, _seatedIn);
            foreach (var kv in _seatedIn) if (kv.Key != from && Driver(kv.Value)) return kv.Key;
            return -1;
        }

        /// Server, a claim of a car: null = may take it, else why not.
        private static string DriverSeatClaimRefusal(SEnt se, int from)
        {
            if (!DriverSeatGuard || !se.Car || se.PartIndex >= 0) return null;
            int by = DriverSeatTakenBy(se, from);
            if (by >= 0) return "driven (player " + by + " sits at the wheel)";
            if (se.ClaimedBy >= 0 && se.ClaimedBy != from && se.OwnerId == se.ClaimedBy && Time.realtimeSinceStartup - se.ClaimedAt < DriveClaimHoldS)
                return "driven (player " + se.ClaimedBy + " just took the wheel)";
            return null;
        }

        /// Client, every 0.5 s while the local player sits in a driver seat: in someone else's car that someone else
        /// drives (its owner says so, or another player sits in this very seat), after our claim was answered.
        private static void DriverSeatConflictTick(fpscontroller pl, Ent e)
        {
            if (!DriverSeatGuard || e == null || !e.Proxy || e.OwnerId == MyId || _claimedFor != e.NetId) return;
            var seats = VehicleSeats(e.Root);
            int mine = System.Array.IndexOf(seats, pl.seat);
            bool other = e.Driven;
            string who = RemotePlayers.NameOf(e.OwnerId);
            foreach (var (id, name, _) in RemotePlayers.Each())
                if (RemotePlayers.SeatNetOf(id) == e.NetId && RemotePlayers.SeatIdxOf(id) == mine) { other = true; who = name; }
            if (!other) return;
            seatscript to = null;
            foreach (var st in seats)
                if (st != null && st != pl.seat && !st.driverSeat0 && !st.driverSeat1 && (st.mainseat == null || st.mainseat == st) && st.sitPos != null &&
                    st.gameObject.activeInHierarchy && st.FreeCanSit()) { to = st; break; }
            string note = (string.IsNullOrEmpty(who) ? "Someone else" : who) + " is driving this car.";
            if (to != null) { pl.GetIn(to); DriverSeatMoved++; }
            else
            {
                var st = pl.seat.transform; var car = st.root;
                float side = Vector3.Dot(st.position - car.position, car.right) > 0.2f ? 1f : -1f;
                pl.GetOut(st.position + st.up * 1.5f + car.right * (2f * side), true);
                DriverSeatOut++;
            }
            _claimedFor = 0;
            Session.Show(1, "", note);
            Plugin.Log.LogInfo($"driver seat of net {e.NetId}: {note} Moved {(to != null ? "to " + to.name : "out of the car")}.");
        }

        public static string SeatGuardStats() =>
            "{\"enabled\":" + (DriverSeatGuard ? "true" : "false") + ",\"refused\":" + DriverSeatRefused + ",\"moved\":" + DriverSeatMoved + ",\"out\":" + DriverSeatOut + "}";

        [HarmonyLib.HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.GetOut), new[] { typeof(Vector3), typeof(bool) })]
        private static class SeatExitCollisionRestore
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(fpscontroller __instance, Vector3 pos, bool forced)
            {
                if (InSession && mainscript.s != null && mainscript.s.player == __instance)
                { LocalSeat(out uint net, out int seat); if (net != 0) SessionLog.Event("seat-leave", net, slot: seat, from: MyId, result: forced ? "forced" : "ok"); }
                if (!InSession || !Mp.RemoteTrace) return;
                Plugin.Log.LogInfo($"SeatTrace: GetOut forced={forced} seated={__instance.Bsitting} seat={(__instance.seat != null ? __instance.seat.name : "null")} at={__instance.transform.position:F1}\n{System.Environment.StackTrace}");
            }

            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(fpscontroller __instance) => SeatCollisionRestore(__instance);
        }
    }
}

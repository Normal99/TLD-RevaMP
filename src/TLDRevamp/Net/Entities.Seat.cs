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

using System.Collections.Generic;

namespace TLDRevamp.Net
{
    // Shared by the placement check and offline tests. All distances are in global metres.
    internal static class TpaArrival
    {
        internal const double ToleranceM = 2;
        internal static bool Close(double dx, double dy, double dz) =>
            dx * dx + dy * dy + dz * dz <= ToleranceM * ToleranceM;
        internal static bool Arrived(bool groundReady, double dx, double dy, double dz) => groundReady && Close(dx, dy, dz);
        internal static bool Floor(double floorY, double bodyY) =>
            floorY >= bodyY - 1.7 && floorY <= bodyY - .4;
        internal const double StrictWaitS = 3;
        internal static bool Fallback(double elapsed) => elapsed >= StrictWaitS;
        internal static bool Landing(bool hasFloor, bool room, double floorY, double bodyY, double elapsed) =>
            hasFloor && room && (Floor(floorY, bodyY) || Fallback(elapsed));
        internal static int ChooseFloor(IReadOnlyList<double?> floors, double bodyY)
        {
            for (int i = 0; i < floors.Count; i++)
                if (Landing(floors[i].HasValue, true, floors[i] ?? 0, bodyY, 0)) return i;
            return -1;
        }
        internal static bool Retry(bool grounded, double dx, double dy, double dz, double elapsed) =>
            !Arrived(grounded, dx, dy, dz) && !Fallback(elapsed);
        internal static bool CarLanding(bool hasFloor, bool hasBounds, double floorY, double bottomY, out double shift)
        {
            shift = floorY + .4 - bottomY;
            return hasFloor && hasBounds && !double.IsNaN(shift) && !double.IsInfinity(shift);
        }
        internal static bool InRange(double x, double z, double cx, double cz, double range) =>
            (x - cx) * (x - cx) + (z - cz) * (z - cz) < range * range;
    }

    // Known copies retain their records in the game's far store; arrival only refreshes their state.
    internal sealed class TpaDelivery
    {
        internal readonly HashSet<uint> Sent = new HashSet<uint>();
        internal readonly List<uint> Due = new List<uint>();
        internal void Offer(uint net, double x, double z, double cx, double cz, double range, int budget)
        {
            if (Due.Count < budget && !Sent.Contains(net) && !Due.Contains(net) && TpaArrival.InRange(x, z, cx, cz, range)) Due.Add(net);
        }
    }
}

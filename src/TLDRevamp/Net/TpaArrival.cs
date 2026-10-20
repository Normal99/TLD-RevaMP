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
        internal static bool InRange(double x, double z, double cx, double cz, double range) =>
            (x - cx) * (x - cx) + (z - cz) * (z - cz) < range * range;
    }

    // A teleport has its own delivery set: Known is a lifetime set, not proof that a copy still exists.
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

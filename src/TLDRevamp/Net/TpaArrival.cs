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
        internal static bool Fallback(bool ready, double elapsed) => ready && elapsed >= StrictWaitS;
        internal readonly struct Candidate
        {
            internal readonly double Y;
            internal readonly bool Room, Static;
            internal Candidate(double y, bool room, bool isStatic) { Y = y; Room = room; Static = isStatic; }
        }
        // -1 waits; -2 selects the ordinary static-floor tiers. Vehicles never establish world readiness.
        internal static int ChooseFloor(IReadOnlyList<Candidate> floors, double bodyY, bool ready, double elapsed)
        {
            if (!ready) return -1;
            for (int i = 0; i < floors.Count; i++)
                if (floors[i].Room && Floor(floors[i].Y, bodyY)) return i;
            return Fallback(ready, elapsed) ? -2 : -1;
        }
        internal static bool WorldFloor(Candidate floor) => floor.Static && floor.Room;
        internal static bool Surface(bool rigidbody, bool vehicle, bool atRest, bool excluded, bool vehicles) =>
            !excluded && (!rigidbody || (vehicles && vehicle && atRest));
        internal static void TerrainHeight(bool terrain, double y, ref double terrainY)
        { if (terrain) terrainY = System.Math.Max(terrainY, y); }
        internal static bool AboveTerrain(double y, double terrainY) => y >= terrainY - .3;
        internal static bool Retry(bool grounded, double dx, double dy, double dz, double elapsed, double remaining) =>
            remaining > 0 && (dy < -.4 || (!Arrived(grounded, dx, dy, dz) && elapsed < StrictWaitS));
        internal static bool CarLanding(double floorY, double bottomY, out double shift)
        {
            shift = floorY + .4 - bottomY;
            return !double.IsNaN(shift) && !double.IsInfinity(shift);
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

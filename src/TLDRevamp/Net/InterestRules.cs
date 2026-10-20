namespace TLDRevamp.Net
{
    // Far-store maintenance, not a send budget: visible motion always retains its existing cadence.
    internal static class InterestRules
    {
        internal static bool Within(bool hasPosition, double dx, double dz, double range) =>
            !hasPosition || dx * dx + dz * dz <= range * range;

        internal static bool Due(bool visible, bool essential, double now, double sentAt, double refresh) =>
            visible || essential || now - sentAt >= refresh;
    }
}

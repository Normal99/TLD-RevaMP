namespace TLDRevamp.Fixes
{
    /// Independent A/B switches and recorder counters for the two host-authoritative clocks.
    public static class SessionClocks
    {
        public static bool WorldEnabled = true, RadioEnabled = true;
        public static long WorldSent, WorldReceived, WorldSlewed, WorldSnapped, WorldStale;
        public static long RadioProjected, RadioStale, RadioClamped, RadioLocalAdvancesBlocked;
    }
}

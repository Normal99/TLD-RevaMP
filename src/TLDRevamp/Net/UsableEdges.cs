namespace TLDRevamp.Net
{
    internal static class UsableEdges
    {
        internal const int EnterMin = 1, EnterMax = 2, LeaveMin = 4, LeaveMax = 8;
        internal static int Changed(float before, float after, float min, float max) => before == after ? 0 :
            (after == min ? EnterMin : 0) | (after == max ? EnterMax : 0) |
            (before == min ? LeaveMin : 0) | (before == max ? LeaveMax : 0);
    }
}

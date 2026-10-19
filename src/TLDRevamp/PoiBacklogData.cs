namespace TLDRevamp
{
    internal enum PoiRoadState { Done, Pending, Outside }
    internal enum PoiBacklogKind { Ready, Roads, Deferred }

    internal interface IPoiRoadChunks
    {
        PoiRoadState At(double x, double z);
    }

    /// Match RoadsDoneAtPlace's four dependencies, so distant candidates do not look like stalled work.
    internal static class PoiBacklogData
    {
        internal static PoiBacklogKind Classify<T>(double x, double z, double cx, double cz, double size, T chunks)
            where T : struct, IPoiRoadChunks
        {
            double nx = cx + (x < cx ? -size : size), nz = cz + (z < cz ? -size : size);
            var a = chunks.At(cx, cz);
            var b = chunks.At(nx, cz);
            var c = chunks.At(cx, nz);
            var d = chunks.At(nx, nz);
            if (a == PoiRoadState.Outside || b == PoiRoadState.Outside || c == PoiRoadState.Outside || d == PoiRoadState.Outside)
                return PoiBacklogKind.Deferred;
            if (a == PoiRoadState.Pending || b == PoiRoadState.Pending || c == PoiRoadState.Pending || d == PoiRoadState.Pending)
                return PoiBacklogKind.Roads;
            return PoiBacklogKind.Ready;
        }
    }
}

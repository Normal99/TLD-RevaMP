using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// POI candidates outlive the road generation area at low draw distances; count that waiting separately.
    public static class PoiBacklog
    {
        public static bool Enabled = true;
        public static long Samples, DeferredObserved;
        public static int Candidates, Ready, WaitingForRoads, Deferred;

        internal static int Sample(List<poiGenScript> generators)
        {
            Candidates = Ready = WaitingForRoads = Deferred = 0;
            Samples++;
            foreach (var gen in generators)
            {
                if (gen == null) continue;
                var roads = new RoadChunks(gen.roadgen);
                foreach (var key in gen.chunksNeeded)
                {
                    if (!gen.chunks.TryGetValue(key, out var chunk) || chunk.poiToPlace == null) continue;
                    foreach (var poi in chunk.poiToPlace)
                    {
                        Candidates++;
                        var kind = PoiBacklogKind.Ready;
                        if (poi != null && gen.roadAffects && gen.roadgen != null)
                        {
                            var center = mainscript.GetChunkPos(poi.pos, gen.roadgen.chunkSize);
                            kind = PoiBacklogData.Classify(poi.pos.x, poi.pos.z, center.x, center.z, gen.roadgen.chunkSize, roads);
                        }
                        if (kind == PoiBacklogKind.Deferred) Deferred++;
                        else if (kind == PoiBacklogKind.Roads) WaitingForRoads++;
                        else Ready++;
                    }
                }
            }
            DeferredObserved += Deferred;
            return Enabled ? Ready + WaitingForRoads : Candidates;
        }

        private readonly struct RoadChunks : IPoiRoadChunks
        {
            private readonly roadGenScript _gen;
            internal RoadChunks(roadGenScript gen) { _gen = gen; }

            public PoiRoadState At(double x, double z)
            {
                var key = new Vector3d(x, 0, z);
                if (_gen.ChunkDone(key)) return PoiRoadState.Done;
                return _gen.chunksNeeded.Contains(key) ? PoiRoadState.Pending : PoiRoadState.Outside;
            }
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"samples\":" + Samples +
            ",\"candidates\":" + Candidates + ",\"ready\":" + Ready + ",\"waitingForRoads\":" + WaitingForRoads +
            ",\"deferred\":" + Deferred + ",\"deferredObserved\":" + DeferredObserved + "}";
    }
}

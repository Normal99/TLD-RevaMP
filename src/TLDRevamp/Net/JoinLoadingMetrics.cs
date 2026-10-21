using System;
using System.Collections.Generic;
using System.Text;

namespace TLDRevamp.Net
{
    // Display facts only. None of these counters participates in the join gate.
    internal struct JoinGeometryCounts
    {
        internal int TerrainDone, TerrainNeeded, RoadsDone, RoadsNeeded;
        internal void Terrain(bool done) { TerrainNeeded++; if (done) TerrainDone++; }
        internal void Road(bool done) { RoadsNeeded++; if (done) RoadsDone++; }
        internal int Pending => TerrainNeeded - TerrainDone + RoadsNeeded - RoadsDone;
        internal bool Ready => TerrainNeeded > 0 && Pending == 0;
    }

    internal enum JoinObjectKind { Vehicle, ItemPart, Other }
    internal struct JoinReceipt
    {
        internal uint Net;
        internal int Prefab;
        internal JoinObjectKind Kind;
    }

    internal sealed class JoinLoadingMetrics
    {
        internal const int FeedCapacity = 6;
        private readonly HashSet<uint> _seen = new HashSet<uint>();
        private readonly JoinReceipt[] _feed = new JoinReceipt[FeedCapacity];
        private readonly JoinReceipt[] _shown = new JoinReceipt[FeedCapacity];
        private int _head;
        internal int FeedCount, ShownCount, Vehicles, ItemsParts, Other;
        internal int Received => _seen.Count;
        internal bool Contains(uint net) => _seen.Contains(net);
        internal void Reserve(int expected) => _seen.EnsureCapacity(expected);
        internal JoinGeometryCounts Geometry;
        internal long BytesReceived;
        internal double Rate, ElapsedMs;
        internal readonly string[] Names = new string[FeedCapacity];
        internal void Clear()
        {
            _seen.Clear(); Array.Clear(_feed, 0, _feed.Length); Array.Clear(Names, 0, Names.Length);
            _head = FeedCount = ShownCount = Vehicles = ItemsParts = Other = 0;
            Geometry = default; BytesReceived = 0; Rate = ElapsedMs = 0;
        }
        internal bool Record(uint net, int prefab, JoinObjectKind kind, bool recent = true)
        {
            if (!_seen.Add(net)) return false;
            if (recent)
            {
                _feed[_head] = new JoinReceipt { Net = net, Prefab = prefab, Kind = kind };
                _head = (_head + 1) % FeedCapacity; FeedCount = Math.Min(FeedCapacity, FeedCount + 1);
            }
            if (kind == JoinObjectKind.Vehicle) Vehicles++; else if (kind == JoinObjectKind.ItemPart) ItemsParts++; else Other++;
            return true;
        }
        // Most recent first; no strings or enumerators on the receive path.
        internal JoinReceipt Recent(int index) => _feed[(_head - 1 - index + FeedCapacity) % FeedCapacity];
        internal void PublishFeed(Func<JoinReceipt, string> name)
        {
            ShownCount = FeedCount;
            for (int i = 0; i < ShownCount; i++) { _shown[i] = Recent(i); Names[i] = name(_shown[i]); }
        }
        internal void AppendProbe(StringBuilder b)
        {
            b.Append(",\"bytesReceived\":").Append(BytesReceived).Append(",\"receiveBytesPerSecond\":").Append(Json.Num(Rate))
                .Append(",\"elapsedMs\":").Append(Json.Num(ElapsedMs))
                .Append(",\"terrainDone\":").Append(Geometry.TerrainDone).Append(",\"terrainNeeded\":").Append(Geometry.TerrainNeeded)
                .Append(",\"roadsDone\":").Append(Geometry.RoadsDone).Append(",\"roadsNeeded\":").Append(Geometry.RoadsNeeded)
                .Append(",\"receivedObjects\":").Append(Received).Append(",\"receivedVehicles\":").Append(Vehicles)
                .Append(",\"receivedItemsParts\":").Append(ItemsParts).Append(",\"receivedOther\":").Append(Other)
                .Append(",\"recentObjects\":[");
            for (int i = 0; i < ShownCount; i++)
            {
                if (i > 0) b.Append(',');
                var row = _shown[i];
                b.Append("{\"net\":").Append(row.Net).Append(",\"prefab\":").Append(row.Prefab)
                    .Append(",\"kind\":").Append(Json.Str(row.Kind == JoinObjectKind.Vehicle ? "vehicle" : row.Kind == JoinObjectKind.ItemPart ? "itemPart" : "other"))
                    .Append(",\"name\":").Append(Json.Str(Names[i])).Append('}');
            }
            b.Append(']');
        }
    }
}

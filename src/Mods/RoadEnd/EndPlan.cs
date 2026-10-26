using System;
using System.Collections.Generic;
using UnityEngine;

namespace RoadEnd
{
    /// Where the main road ends, worked out from the game's own main road. Distances along the travel direction are "u":
    /// u = Sign * global z, so the end is always at a larger u than the start.
    ///
    /// The main road runs through 10 km road chunks along x = 0 (roadGenScript, chunkSize 10000, chunks centred on
    /// multiples of it). Inside a chunk it wanders between the chunk's dots, and may turn back for a while, so a straight
    /// line across the world can cross it more than once. Between two chunks it can't: the game joins one chunk's
    /// farthest dot (on x = 0) to the next chunk's nearest one, and every other dot of each chunk lies on its own side.
    /// So the end goes on such a joining stretch: at the configured distance when it falls on one, else at the nearest
    /// point of the nearest one, a little away from both dots (at most about 5 km off).
    public static class EndPlan
    {
        public struct State
        {
            public double Cut;          // u of the line where the roads stop
            public bool Known;          // false: not worked out yet (no world): nothing is cut
            public bool HasRoadEnd;
            public Vector3d RoadEnd;    // where the main road stops (global)
            public Vector3d RoadDir;    // its direction there (travel direction, flat)
        }

        public static State Current;
        public static event Action Changed;

        /// Room left between the end and the dots of the main road on both sides (metres along u).
        public const double DotClearance = 250.0;

        /// The end line between the last dot before a border (nearU) and the first one after it (farU).
        public static double CutBetween(double wantU, double nearU, double farU)
        {
            if (farU - nearU < 2 * DotClearance) return (nearU + farU) * 0.5;
            return Math.Max(nearU + DotClearance, Math.Min(farU - DotClearance, wantU));
        }

        public static void Reset()
        {
            Current = new State { Cut = double.MaxValue };
            Roads.Trimmed = Roads.Dropped = 0;
            Changed?.Invoke();
        }

        /// Called after the game made its road chunks this frame. Works the end out once per world: the dots of the main
        /// road chunk the configured distance is in and of its two neighbours (made here with the game's own
        /// UpdDotChunk when the game hasn't made them yet, the same dots it makes later), then the joining stretch on
        /// either side of that chunk, whichever end comes closer to the distance asked for.
        public static void Update(roadGenScript rg)
        {
            if (!Plugin.Enabled || rg == null || !rg.hasMainRoad || Current.Known || rg.mapSetting == null) return;
            double want = Plugin.EndKm * 1000.0, cs = rg.chunkSize;
            double k = Math.Max(1, Math.Round(want / cs));
            var u = new double[3, 2];   // per chunk k-1, k, k+1: its nearest and farthest dot along u
            for (int i = 0; i < 3; i++)
            {
                var key = new Vector3d(0, 0, Plugin.Sign * (k - 1 + i) * cs);
                bool made = !rg.chunks.ContainsKey(key);
                if (made) rg.UpdDotChunk(key);
                if (!rg.chunks.TryGetValue(key, out var ch) || ch.dots.Count == 0) return;
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (var d in ch.dots) { lo = Math.Min(lo, Plugin.Sign * d.pos.z); hi = Math.Max(hi, Plugin.Sign * d.pos.z); }
                u[i, 0] = lo; u[i, 1] = hi;
                // a chunk made only to read it goes again; the game makes it itself when it needs it
                if (made && !rg.chunksNeeded.Contains(key)) rg.chunks.Remove(key);
            }
            double before = CutBetween(want, u[0, 1], u[1, 0]);   // between chunk k-1 and chunk k
            double after = CutBetween(want, u[1, 1], u[2, 0]);    // between chunk k and chunk k+1
            double cut = Math.Abs(before - want) <= Math.Abs(after - want) ? before : after;
            Current.Cut = cut;
            Current.Known = true;
            Changed?.Invoke();
            Plugin.Log.LogInfo($"the main road ends at {cut / 1000.0:0.000} km (asked for {Plugin.EndKm:0.###} km); the land and the side roads go on");
        }

        public static void SetRoadEnd(Vector3d at, Vector3d dir)
        {
            if (Current.HasRoadEnd && (Current.RoadEnd - at).magnitude < 0.5) return;
            Current.RoadEnd = at;
            dir.y = 0;
            Current.RoadDir = dir.magnitude > 1e-6 ? dir.normalized : new Vector3d(0, 0, Plugin.Sign);
            Current.HasRoadEnd = true;
            Changed?.Invoke();
        }

        public static double U(Vector3d p) => Plugin.Sign * p.z;
        public static bool Past(Vector3d p) => Plugin.Sign * p.z > Current.Cut;
    }
}

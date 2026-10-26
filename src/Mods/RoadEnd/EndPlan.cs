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
    /// So the end goes on that joining stretch: at the configured distance when it falls there, else at the nearest
    /// point of the stretch the chunk border is in, a little away from both dots.
    public static class EndPlan
    {
        public struct State
        {
            public double Cut;          // u of the line where the roads stop
            public bool Known;          // false: worked out from the chunk border alone (the two chunks don't exist yet)
            public bool HasRoadEnd;
            public Vector3d RoadEnd;    // where the main road stops (global)
            public Vector3d RoadDir;    // its direction there (travel direction, flat)
        }

        public static State Current;
        public static event Action Changed;

        /// Room left between the end and the dots of the main road on both sides (metres along u).
        public const double DotClearance = 250.0;

        /// The chunk border nearest to the configured distance (u), for a chunk size.
        public static double Border(double wantU, double chunkSize)
        {
            double k = Math.Round(wantU / chunkSize - 0.5);
            if (k < 0) k = 0;
            return (k + 0.5) * chunkSize;
        }

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

        /// Called after the game made its road chunks this frame.
        public static void Update(roadGenScript rg)
        {
            if (!Plugin.Enabled || rg == null || !rg.hasMainRoad || Current.Known) return;
            double want = Plugin.EndKm * 1000.0;
            double border = Border(want, rg.chunkSize);
            var near = new Vector3d(0, 0, Plugin.Sign * (border - rg.chunkSize * 0.5));
            var far = new Vector3d(0, 0, Plugin.Sign * (border + rg.chunkSize * 0.5));
            double cut;
            bool known = false;
            if (rg.chunks.TryGetValue(near, out var a) && rg.chunks.TryGetValue(far, out var b) && a.dots.Count > 0 && b.dots.Count > 0)
            {
                double nearU = double.MinValue, farU = double.MaxValue;
                foreach (var d in a.dots) nearU = Math.Max(nearU, Plugin.Sign * d.pos.z);
                foreach (var d in b.dots) farU = Math.Min(farU, Plugin.Sign * d.pos.z);
                cut = CutBetween(want, nearU, farU);
                known = true;
            }
            else cut = border;
            if (cut == Current.Cut && known == Current.Known) return;
            Current.Cut = cut;
            Current.Known = known;
            Changed?.Invoke();
            if (known) Plugin.Log.LogInfo($"the road ends at {cut / 1000.0:0.000} km (asked for {Plugin.EndKm:0.###} km), the land and the side roads go on");
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

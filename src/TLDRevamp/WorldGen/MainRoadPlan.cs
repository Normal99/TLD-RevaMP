using System;
using System.Collections.Generic;

namespace TLDRevamp.WorldGen
{
    /// The rules that turn the game's own "main road only" generator into one unbroken road (issue #32 follow-up).
    /// Pure logic, no game or Unity types, so tools/unit/TerrainMainRoadTest.cs runs exactly this code.
    ///
    /// What goes wrong in the game itself with hasSideRoad = false (roadGenScript, read from the shipped game):
    ///
    ///  - Each main-road chunk (x = 0) still gets 2 to 4 dots, and ConnectDots still joins every pair of neighbouring
    ///    dots inside it. Only the path from the southmost dot to the northmost one becomes the main road; the other
    ///    connections stay side roads, so every chunk shows a crossroad.
    ///  - ConnectChunks only joins two chunks when the diagonal chunk next to them exists too (cpos3 = x +/- 1), and in
    ///    this mode the chunks off x = 0 are never made. So no chunk is ever joined to the next one: the main road ends
    ///    at the northmost dot of every chunk, and a new piece starts at the southmost dot of the next.
    ///
    /// The fix keeps the game's own dots and its own road inside a chunk, so the road still wanders the way the
    /// game's main road does:
    ///  - inside a main-road chunk only the roads on the main path are kept;
    ///  - each chunk's northmost dot is joined to the next chunk's southmost dot with a main road, exactly the pair the
    ///    game itself joins as the main road when it can (ConnectDots: i == 0 and j == the other chunk's last dot).
    public static class MainRoadPlan
    {
        /// Two dots closer than this (on the ground, metres) are the same point: a generator that already puts the
        /// chunk ends on the chunk edge (the old 2023 road) needs no joining road, and a zero-length road would give
        /// the game's road builder a direction of zero.
        public const double SamePointM = 1.0;

        /// Is this generator in the mode the fix is for: a main road, no side roads.
        public static bool Applies(bool hasMainRoad, bool hasSideRoad) => hasMainRoad && !hasSideRoad;

        /// Which roads of a main-road chunk survive, in their original order.
        ///
        /// mainPath: the indices the game put in the chunk's mainRoad list (the path from the southmost to the
        /// northmost dot). isMainType: each road's own type is mainRoad. When the game found a path, exactly the path
        /// is kept: the direct southmost-northmost road the game also types as main is left out when the path does not
        /// use it, or the chunk would show a fork. When the game found no path (a chunk with one dot, or its search gave
        /// up), the roads it typed as main are kept, so nothing that was a main road disappears.
        public static List<int> KeptRoads(int roadCount, IList<int> mainPath, IList<bool> isMainType)
        {
            var keep = new List<int>();
            if (roadCount <= 0) return keep;
            var onPath = new bool[roadCount];
            bool anyPath = false;
            if (mainPath != null)
                foreach (int i in mainPath)
                    if (i >= 0 && i < roadCount) { onPath[i] = true; anyPath = true; }
            for (int i = 0; i < roadCount; i++)
            {
                bool main = isMainType != null && i < isMainType.Count && isMainType[i];
                if (anyPath ? onPath[i] : main) keep.Add(i);
            }
            return keep;
        }

        /// The new index of each old road after KeptRoads (-1: removed).
        public static int[] Remap(int roadCount, IList<int> kept)
        {
            var map = new int[Math.Max(0, roadCount)];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            for (int n = 0; n < kept.Count; n++) map[kept[n]] = n;
            return map;
        }

        /// Should the chunk below be joined to the chunk above with a road? Both have to be chunks the game keeps
        /// (needed, with their own road done), so the joining road lives as long as the chunks do and is never made
        /// again every frame; both need a dot; and their facing dots must be apart.
        public static bool WantLink(bool lowerReady, bool upperReady, bool alreadyLinked, int lowerDots, int upperDots,
            double lowerLastX, double lowerLastZ, double upperFirstX, double upperFirstZ, out bool samePoint)
        {
            samePoint = false;
            if (!lowerReady || !upperReady || alreadyLinked || lowerDots <= 0 || upperDots <= 0) return false;
            double dx = upperFirstX - lowerLastX, dz = upperFirstZ - lowerLastZ;
            samePoint = dx * dx + dz * dz < SamePointM * SamePointM;
            return !samePoint;
        }

        // ------------------------------------------------------------------ the check the bridge and the test use

        /// One main-road chunk as the diagnostics see it.
        public sealed class ChunkView
        {
            public double Z;               // the chunk's centre z
            public int Dots;
            public int Roads, MainRoads, SideRoads;
            public bool InsideDone;        // insideConnected
            /// The main roads of the chunk as (from, to) endpoints: "z:dot" keys, so a road joining two chunks names
            /// both.
            public readonly List<KeyValuePair<string, string>> MainEdges = new List<KeyValuePair<string, string>>();
            public string South, North;    // the keys of the chunk's southmost and northmost dot
            /// Where those two dots are on the ground (NaN: not known). Two chunks whose facing dots are the same point
            /// meet without a road (the old 2023 road puts them on the chunk edge).
            public double SouthX = double.NaN, SouthZ = double.NaN, NorthX = double.NaN, NorthZ = double.NaN;
        }

        /// Walk the main road from the southmost chunk's southmost dot to the northmost chunk's northmost dot over
        /// main roads only. Returns the number of places the walk cannot continue (0 = one unbroken road) and the
        /// number of side roads seen.
        public static int Gaps(IList<ChunkView> chunks, out int sideRoads, out string firstGap)
        {
            sideRoads = 0; firstGap = "";
            if (chunks == null || chunks.Count == 0) return 0;
            var ordered = new List<ChunkView>(chunks);
            ordered.Sort((a, b) => a.Z.CompareTo(b.Z));
            var adj = new Dictionary<string, List<string>>();
            void Edge(string a, string b)
            {
                if (!adj.TryGetValue(a, out var la)) adj[a] = la = new List<string>();
                if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new List<string>();
                la.Add(b); lb.Add(a);
            }
            foreach (var c in ordered)
            {
                sideRoads += c.SideRoads;
                foreach (var e in c.MainEdges) Edge(e.Key, e.Value);
            }
            int gaps = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                var c = ordered[i];
                if (c.Dots <= 0 || c.South == null || c.North == null) { gaps++; if (firstGap.Length == 0) firstGap = "chunk z " + c.Z + " has no dots"; continue; }
                // inside: south reaches north
                if (c.South != c.North && !Reaches(adj, c.South, c.North))
                {
                    gaps++;
                    if (firstGap.Length == 0) firstGap = "inside chunk z " + c.Z;
                }
                // to the next chunk
                if (i + 1 < ordered.Count)
                {
                    var n = ordered[i + 1];
                    if (n.South == null) continue;   // counted when n is visited
                    if (!Reaches(adj, c.North, n.South) && !SameKey(c.North, n.South) && !SamePoint(c, n))
                    {
                        gaps++;
                        if (firstGap.Length == 0) firstGap = "between chunk z " + c.Z + " and z " + n.Z;
                    }
                }
            }
            return gaps;
        }

        private static bool SameKey(string a, string b) => a != null && a == b;

        private static bool SamePoint(ChunkView lower, ChunkView upper)
        {
            double dx = upper.SouthX - lower.NorthX, dz = upper.SouthZ - lower.NorthZ;
            return dx * dx + dz * dz < SamePointM * SamePointM;   // false when a position is not known (NaN)
        }

        private static bool Reaches(Dictionary<string, List<string>> adj, string from, string to)
        {
            if (from == to) return true;
            if (!adj.ContainsKey(from)) return false;
            var seen = new HashSet<string> { from };
            var queue = new Queue<string>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                var k = queue.Dequeue();
                foreach (var n in adj[k])
                {
                    if (n == to) return true;
                    if (seen.Add(n)) queue.Enqueue(n);
                }
            }
            return false;
        }
    }
}

using System;
using System.Collections.Generic;

namespace TLDRevamp.Fixes
{
    /// The tile maths of the detailed terrain ring (DetailedTerrain), without Unity types, so the headless unit test
    /// (tools/unit/DetailedTerrainTest) runs exactly this code. A tile is named by its offset from the player's tile in
    /// whole tiles; its centre is that offset times the tile size.
    internal static class DetailedTerrainRing
    {
        internal readonly struct Tile : IEquatable<Tile>
        {
            public readonly int X, Z;
            public Tile(int x, int z) { X = x; Z = z; }
            public double Metres(double tileSize) => Math.Sqrt((double)X * X + (double)Z * Z) * tileSize;
            public bool Equals(Tile o) => X == o.X && Z == o.Z;
            public override bool Equals(object o) => o is Tile t && Equals(t);
            public override int GetHashCode() => X * 73856093 ^ Z * 19349663;
            public override string ToString() => "(" + X + "," + Z + ")";
        }

        /// The game's own sample grid (terrainGenScript.FStart): tileDist = ceil(drawDistance / tileSize), a square of
        /// 2*tileDist+1 tiles shifted by -(tileDist+1) tiles, every centre within drawDistance. The shift makes it
        /// run from -(tileDist+1) to tileDist-1: one tile shorter towards +x and +z than towards -x and -z.
        internal static List<Tile> GameGrid(double drawDistance, double tileSize)
        {
            var list = new List<Tile>();
            if (!(tileSize > 0) || !(drawDistance > 0) || double.IsInfinity(drawDistance)) return list;
            int tileDist = (int)Math.Ceiling(drawDistance / tileSize);
            int border = tileDist * 2 + 1;
            for (int k = 0; k < border; k++)
                for (int l = 0; l < border; l++)
                {
                    var t = new Tile(l - tileDist - 1, k - tileDist - 1);
                    if (t.Metres(tileSize) <= drawDistance) list.Add(t);
                }
            return list;
        }

        /// The centred ring out to `target`: every tile whose centre is within it, minus the tiles the game's own grid
        /// already asks for, nearest first (ties in a fixed order, so every run asks in the same order).
        internal static List<Tile> Extra(double tileSize, double target, List<Tile> game)
        {
            var list = new List<Tile>();
            if (!(tileSize > 0) || !(target > 0) || double.IsInfinity(target) || double.IsInfinity(tileSize)) return list;
            var have = new HashSet<Tile>(game ?? new List<Tile>());
            int n = (int)Math.Ceiling(target / tileSize);
            for (int z = -n; z <= n; z++)
                for (int x = -n; x <= n; x++)
                {
                    var t = new Tile(x, z);
                    if (t.Metres(tileSize) > target || have.Contains(t)) continue;
                    list.Add(t);
                }
            list.Sort((a, b) =>
            {
                int c = ((long)a.X * a.X + (long)a.Z * a.Z).CompareTo((long)b.X * b.X + (long)b.Z * b.Z);
                if (c != 0) return c;
                c = a.Z.CompareTo(b.Z);
                return c != 0 ? c : a.X.CompareTo(b.X);
            });
            return list;
        }

        /// How far the ground reaches along a direction (dx, dz in whole tiles, e.g. (1,0)): the furthest tile centre
        /// on that line plus half a tile. For the diagnostics and the test.
        internal static double Reach(IEnumerable<Tile> tiles, int dx, int dz, double tileSize)
        {
            int best = int.MinValue;
            foreach (var t in tiles)
            {
                if (dx == 0 && t.X != 0 || dz == 0 && t.Z != 0) continue;
                int along = dx != 0 ? t.X * dx : t.Z * dz;
                if (dx != 0 && dz != 0 && t.X * dx != t.Z * dz) continue;
                if (along > best) best = along;
            }
            if (best == int.MinValue) return 0;
            double step = dx != 0 && dz != 0 ? Math.Sqrt(2) : 1;
            return best * step * tileSize + tileSize * 0.5;
        }
    }
}

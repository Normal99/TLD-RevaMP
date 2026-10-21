using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TLDRevamp.Fixes;
using Tile = TLDRevamp.Fixes.DetailedTerrainRing.Tile;

// Offline unit test of the detailed terrain ring (issue #37).
// dotnet run --project tools/unit/DetailedTerrainTest.csproj
//
// It links the production DetailedTerrainRing, so the tiles asked for here are the tiles the game is asked for.
static class DetailedTerrainTest
{
    static int checks, fails;

    static void Check(string name, bool ok)
    {
        checks++;
        if (!ok) { fails++; Console.WriteLine("FAIL " + name); }
        else Console.WriteLine("PASS " + name);
    }

    static int Main()
    {
        const double Tile = 100;   // the game's detailed tiles (terrainhorizon: tileSize 100, resolution 40)

        // --- the game's own grid, as terrainGenScript.FStart builds it
        var game = DetailedTerrainRing.GameGrid(300, Tile);
        Check("the game's 300 m grid has the 27 tiles the game asks for (terrainhorizon: needed 27)", game.Count == 27);
        Check("the game's grid reaches 350 m towards -x", DetailedTerrainRing.Reach(game, -1, 0, Tile) == 350);
        Check("the game's grid reaches only 250 m towards +x", DetailedTerrainRing.Reach(game, 1, 0, Tile) == 250);
        Check("the game's grid reaches only 250 m towards +z", DetailedTerrainRing.Reach(game, 0, 1, Tile) == 250);
        var game320 = DetailedTerrainRing.GameGrid(320, Tile);   // the slider's top end: tileDist 4, so the shift no longer cuts
        Check("the game's 320 m grid has 37 tiles", game320.Count == 37);
        Check("the game's 320 m grid reaches 350 m every way",
            new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.All(d => DetailedTerrainRing.Reach(game320, d.Item1, d.Item2, Tile) == 350));

        // --- the ring
        foreach (double target in new[] { 500.0, 750, 1000 })
        {
            var extra = DetailedTerrainRing.Extra(Tile, target, game);
            var all = new HashSet<Tile>(game.Concat(extra));
            Check($"{target} m: no tile is asked for twice", all.Count == game.Count + extra.Count);
            Check($"{target} m: no ring tile is the game's", !extra.Any(t => game.Contains(t)));
            Check($"{target} m: every ring tile is within the distance", extra.All(t => t.Metres(Tile) <= target + 1e-9));
            int disk = 0;
            int n = (int)Math.Ceiling(target / Tile);
            for (int z = -n; z <= n; z++) for (int x = -n; x <= n; x++) if (new Tile(x, z).Metres(Tile) <= target) disk++;
            int gameInside = game.Count(t => t.Metres(Tile) <= target);
            Check($"{target} m: the game's grid and the ring together are the whole centred disk", all.Count(t => t.Metres(Tile) <= target) == disk && extra.Count == disk - gameInside);
            Check($"{target} m: nearest first", extra.Zip(extra.Skip(1), (a, b) => a.Metres(Tile) <= b.Metres(Tile)).All(x => x));
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                Check($"{target} m: reaches the same distance towards ({dx},{dz})",
                    DetailedTerrainRing.Reach(all, dx, dz, Tile) == Math.Floor(target / Tile) * Tile + Tile / 2);
            Check($"{target} m: the same order on every call", extra.SequenceEqual(DetailedTerrainRing.Extra(Tile, target, game)));
        }
        Check("750 m asks for 150 more tiles than the game", DetailedTerrainRing.Extra(Tile, 750, game).Count == 150);

        // --- nothing when it would not reach further, or for nonsense input
        Check("a distance inside the game's own asks for at most the game's short side",
            DetailedTerrainRing.Extra(Tile, 250, game).All(t => t.X > 1 || t.Z > 1) );
        Check("0 m asks for nothing", DetailedTerrainRing.Extra(Tile, 0, game).Count == 0);
        Check("NaN asks for nothing", DetailedTerrainRing.Extra(Tile, double.NaN, game).Count == 0);
        Check("infinity asks for nothing", DetailedTerrainRing.Extra(Tile, double.PositiveInfinity, game).Count == 0);
        Check("a zero tile size asks for nothing", DetailedTerrainRing.Extra(0, 750, game).Count == 0);
        Check("no game grid: the whole disk", DetailedTerrainRing.Extra(Tile, 300, null).Count == 29);
        Check("a NaN game distance gives no game grid", DetailedTerrainRing.GameGrid(double.NaN, Tile).Count == 0);

        // --- the production patch keeps the game's path when it is off, and puts every player's own ring first
        // the repo from this build folder: every project builds into its own bin/<name>/ (d75a4ee), so a fixed ../ depth
        // pointed outside the checkout in CI
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "src/TLDRevamp/Fixes/DetailedTerrain.cs"))) root = root.Parent;
        Check("the repo is found above the build folder", root != null);
        var src = File.ReadAllText(Path.Combine(root.FullName, "src/TLDRevamp/Fixes/DetailedTerrain.cs"));
        int own = src.IndexOf("foreach (var s in gen.sampleCoords) needed.Add(currentp + s);", StringComparison.Ordinal);
        int ring = src.IndexOf("foreach (var s in ring.Extra) needed.Add(centre + s);", StringComparison.Ordinal);
        Check("CheckPlace adds every player's own ring before the local ring", own > 0 && ring > own);
        Check("CheckPlace runs the game's own code when the ring is off",
            src.Contains("if (!On || !IsDetailed(gen) || gen.mapSetting == null || gen.mapSetting.useAsync2 || gen.sampleCoords == null) return true;"));

        Console.WriteLine($"{checks - fails}/{checks} checks passed");
        return fails == 0 ? 0 : 1;
    }
}

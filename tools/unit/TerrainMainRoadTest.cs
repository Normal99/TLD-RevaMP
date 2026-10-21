using System;
using System.Collections.Generic;
using TLDRevamp.WorldGen;

// Offline unit test of the main road only fix (issue #32 follow-up): which roads of a chunk survive, when two chunks
// are joined, and the gap check the bridge's `terrain roads` and tools/mainroad.py report.
// dotnet run --project tools/unit/TerrainMainRoadTest.csproj
//
// It links the production MainRoadPlan.cs, which has no Unity or game types.
static class TerrainMainRoadTest
{
    static int checks, fails;

    static void Check(string name, bool ok)
    {
        checks++;
        if (!ok) { fails++; Console.WriteLine("FAIL " + name); }
        else Console.WriteLine("PASS " + name);
    }

    static MainRoadPlan.ChunkView Chunk(double z, int dots, params (string a, string b)[] main)
    {
        var c = new MainRoadPlan.ChunkView { Z = z, Dots = dots, Roads = main.Length, MainRoads = main.Length, InsideDone = true };
        if (dots > 0) { c.South = z + ":0"; c.North = z + ":" + (dots - 1); }
        foreach (var e in main) c.MainEdges.Add(new KeyValuePair<string, string>(e.a, e.b));
        return c;
    }

    static int Main()
    {
        // ---------------------------------------------------------------- which mode the fix is for
        Check("the fix is for a main road with no side roads", MainRoadPlan.Applies(true, false));
        Check("Crossroads is the game's own", !MainRoadPlan.Applies(true, true));
        Check("no main road: nothing to fix", !MainRoadPlan.Applies(false, false) && !MainRoadPlan.Applies(false, true));

        // ---------------------------------------------------------------- the roads a chunk keeps
        // 4 roads: 0 = dot0-dot1 (path), 1 = dot0-dot2 (side), 2 = dot1-dot2 (path), 3 = dot0-dot2 direct, typed main
        var kept = MainRoadPlan.KeptRoads(4, new[] { 0, 2 }, new[] { true, false, true, true });
        Check("only the path is kept", kept.Count == 2 && kept[0] == 0 && kept[1] == 2);
        Check("a direct road typed main but off the path is dropped (no fork)", !kept.Contains(3));
        var noPath = MainRoadPlan.KeptRoads(3, new int[0], new[] { false, true, false });
        Check("with no path the roads typed main are kept", noPath.Count == 1 && noPath[0] == 1);
        Check("a null path is no path", MainRoadPlan.KeptRoads(2, null, new[] { true, false }).Count == 1);
        Check("an out-of-range path index is ignored", MainRoadPlan.KeptRoads(2, new[] { 1, 7, -1 }, null).Count == 1);
        Check("no roads keep nothing", MainRoadPlan.KeptRoads(0, new[] { 0 }, new[] { true }).Count == 0);
        var map = MainRoadPlan.Remap(4, kept);
        Check("kept roads get new indices in order", map[0] == 0 && map[2] == 1);
        Check("removed roads map to -1", map[1] == -1 && map[3] == -1);

        // ---------------------------------------------------------------- joining two chunks
        Check("two ready chunks with apart dots are joined",
            MainRoadPlan.WantLink(true, true, false, 3, 2, 10, 100, 30, 160, out bool same) && !same);
        Check("a chunk that is not ready is not joined", !MainRoadPlan.WantLink(false, true, false, 3, 2, 0, 0, 0, 100, out _)
            && !MainRoadPlan.WantLink(true, false, false, 3, 2, 0, 0, 0, 100, out _));
        Check("a pair already joined is not joined again", !MainRoadPlan.WantLink(true, true, true, 3, 2, 0, 0, 0, 100, out _));
        Check("a chunk with no dots is not joined", !MainRoadPlan.WantLink(true, true, false, 0, 2, 0, 0, 0, 100, out _)
            && !MainRoadPlan.WantLink(true, true, false, 2, 0, 0, 0, 0, 100, out _));
        Check("dots on the same point need no road (the 2023 road ends on the chunk edge)",
            !MainRoadPlan.WantLink(true, true, false, 2, 2, 5, 250, 5.3, 250.4, out bool same2) && same2);

        // ---------------------------------------------------------------- the gap check
        // three chunks of 2 dots each, every chunk's own road and the joins between them: one unbroken road
        var whole = new List<MainRoadPlan.ChunkView>
        {
            Chunk(0, 2, ("0:0", "0:1"), ("0:1", "100:0")),
            Chunk(100, 2, ("100:0", "100:1"), ("100:1", "200:0")),
            Chunk(200, 2, ("200:0", "200:1")),
        };
        Check("an unbroken road has no gaps", MainRoadPlan.Gaps(whole, out int side, out string first) == 0 && side == 0 && first == "");

        // the game's own main road only: every chunk's road, no joins between them
        var broken = new List<MainRoadPlan.ChunkView>
        {
            Chunk(200, 2, ("200:0", "200:1")),
            Chunk(0, 2, ("0:0", "0:1")),
            Chunk(100, 2, ("100:0", "100:1")),
        };
        int gaps = MainRoadPlan.Gaps(broken, out _, out string firstBroken);
        Check("the game's own road breaks between every chunk", gaps == 2);
        Check("the first gap is the southmost one (chunks are sorted)", firstBroken == "between chunk z 0 and z 100");

        var inside = new List<MainRoadPlan.ChunkView> { Chunk(0, 3, ("0:0", "0:1")) };
        Check("a chunk whose road stops inside it is a gap", MainRoadPlan.Gaps(inside, out _, out string fi) == 1 && fi == "inside chunk z 0");

        var empty = new List<MainRoadPlan.ChunkView> { Chunk(0, 0) };
        Check("a chunk with no dots is a gap", MainRoadPlan.Gaps(empty, out _, out string fe) == 1 && fe.Contains("no dots"));

        var withSide = Chunk(0, 1); withSide.SideRoads = 2;
        MainRoadPlan.Gaps(new List<MainRoadPlan.ChunkView> { withSide }, out int sides, out _);
        Check("side roads are counted", sides == 2);

        Check("no chunks, no gaps", MainRoadPlan.Gaps(new List<MainRoadPlan.ChunkView>(), out _, out _) == 0 && MainRoadPlan.Gaps(null, out _, out _) == 0);

        // a join that goes the long way round through a third chunk still counts as reaching
        var detour = new List<MainRoadPlan.ChunkView>
        {
            Chunk(0, 2, ("0:0", "0:1"), ("0:1", "100:1")),
            Chunk(100, 2, ("100:1", "100:0")),
        };
        Check("roads are walked both ways", MainRoadPlan.Gaps(detour, out _, out _) == 0);

        // the old 2023 road: neighbouring chunks meet at the same point, with no road between them
        var meet = new List<MainRoadPlan.ChunkView> { Chunk(0, 2, ("0:0", "0:1")), Chunk(100, 2, ("100:0", "100:1")) };
        meet[0].NorthX = 3; meet[0].NorthZ = 50; meet[1].SouthX = 3.4; meet[1].SouthZ = 50.2;
        Check("chunks that meet at the same point have no gap", MainRoadPlan.Gaps(meet, out _, out _) == 0);
        meet[1].SouthX = 30;
        Check("chunks whose ends are apart and not joined are a gap", MainRoadPlan.Gaps(meet, out _, out _) == 1);

        Console.WriteLine(fails == 0 ? $"ALL PASS ({checks} checks)" : $"{fails} FAILED of {checks}");
        return fails == 0 ? 0 : 1;
    }
}

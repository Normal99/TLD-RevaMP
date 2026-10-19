using System;
using System.Collections.Generic;
using TLDRevamp;

static class P
{
    private static int _checks, _fails;
    private static void Check(string name, bool ok)
    {
        _checks++;
        if (ok) return;
        Console.WriteLine("FAIL " + name);
        _fails++;
    }

    private struct Chunks : IPoiRoadChunks
    {
        internal Dictionary<(double, double), PoiRoadState> States;
        public PoiRoadState At(double x, double z) => States[(x, z)];
    }

    private static int Main()
    {
        // Every combination of done, scheduled and outside dependencies, including mixed blockers.
        foreach (double cx in new[] { 0.0, -20000.0, 300000.0 })
        foreach (double cz in new[] { 0.0, -10000.0 })
        foreach (int sx in new[] { -1, 0, 1 })
        foreach (int sz in new[] { -1, 0, 1 })
        {
            const double size = 10000;
            var keys = new[] { (cx, cz), (cx + (sx < 0 ? -size : size), cz),
                (cx, cz + (sz < 0 ? -size : size)), (cx + (sx < 0 ? -size : size), cz + (sz < 0 ? -size : size)) };
            for (int mask = 0; mask < 81; mask++)
            {
                var chunks = new Chunks { States = new Dictionary<(double, double), PoiRoadState>() };
                bool outside = false, pending = false;
                int n = mask;
                foreach (var key in keys)
                {
                    var state = (PoiRoadState)(n % 3); n /= 3;
                    chunks.States[key] = state;
                    outside |= state == PoiRoadState.Outside;
                    pending |= state == PoiRoadState.Pending;
                }
                var expected = outside ? PoiBacklogKind.Deferred : pending ? PoiBacklogKind.Roads : PoiBacklogKind.Ready;
                var got = PoiBacklogData.Classify(cx + sx, cz + sz, cx, cz, size, chunks);
                Check("dependency combination " + mask, got == expected);
            }
        }

        var moving = new Chunks { States = new Dictionary<(double, double), PoiRoadState>
            { [(0, 0)] = PoiRoadState.Done, [(100, 0)] = PoiRoadState.Done,
              [(0, 100)] = PoiRoadState.Done, [(100, 100)] = PoiRoadState.Outside } };
        Check("outer diagonal defers", PoiBacklogData.Classify(1, 1, 0, 0, 100, moving) == PoiBacklogKind.Deferred);
        moving.States[(100, 100)] = PoiRoadState.Pending;
        Check("entering scope exposes unfinished work", PoiBacklogData.Classify(1, 1, 0, 0, 100, moving) == PoiBacklogKind.Roads);
        moving.States[(100, 100)] = PoiRoadState.Done;
        Check("completed outside dependency stays ready", PoiBacklogData.Classify(1, 1, 0, 0, 100, moving) == PoiBacklogKind.Ready);
        Console.WriteLine(_checks + " checks, " + _fails + " failures");
        return _fails == 0 ? 0 : 1;
    }
}

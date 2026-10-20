using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using TLDRevamp;

static class WorldOverlayTest
{
    static int checks;
    static void Check(string name, bool ok) { checks++; if (!ok) throw new Exception(name); }
    static void Main()
    {
        Check("green below 10 cm", WorldOverlayLogic.Colour(.099999) == 0);
        Check("10 cm is yellow", WorldOverlayLogic.Colour(.1) == 1);
        Check("yellow below 50 cm", WorldOverlayLogic.Colour(.499999) == 1);
        Check("50 cm is red", WorldOverlayLogic.Colour(.5) == 2);
        Check("missing sample is neutral", WorldOverlayLogic.Colour(double.NaN) == -1);
        Check("one second is not stale", !WorldOverlayLogic.Stale(1, true));
        Check("keepalive plus tick and jitter is healthy", !WorldOverlayLogic.Stale(1.2, true));
        Check("stale margin inclusive", !WorldOverlayLogic.Stale(1.5, true));
        Check("moving stall", WorldOverlayLogic.Stale(1.50001, true));
        Check("rest is never stale", !WorldOverlayLogic.Stale(100, false));
        Check("final overrides residual motion", !WorldOverlayLogic.Moving(true, false, true, true));
        Check("stationary nonfinal is not moving", !WorldOverlayLogic.Moving(false, false, false, false));
        Check("angular motion counts", WorldOverlayLogic.Moving(false, false, false, true));
        Check("linear motion counts", WorldOverlayLogic.Moving(false, false, true, false));
        Check("parent constraint excludes stale", !WorldOverlayLogic.Moving(false, true, true, true));
        Check("no invented age", !WorldOverlayLogic.Stale(double.NaN, true) && WorldOverlayLogic.Age(double.NaN) == "age unknown");
        Check("self host", WorldOverlayLogic.Owner(0, 0) == "you");
        Check("remote host", WorldOverlayLogic.Owner(0, 3) == "host");
        Check("remote player", WorldOverlayLogic.Owner(12, 3) == "player 12");
        Check("local player", WorldOverlayLogic.Owner(3, 3) == "you");
        Check("unknown owner", WorldOverlayLogic.Owner(-1, 3) == "unshared");
        Check("locked state wins", WorldOverlayLogic.SendState(true, true, true, true) == "locked");
        Check("rest state", WorldOverlayLogic.SendState(false, true, true, false) == "at rest");
        Check("quiet tail", WorldOverlayLogic.SendState(false, false, true, false) == "quiet tail");
        Check("visible motion in tail", WorldOverlayLogic.SendState(false, false, true, true) == "moving");
        Check("initial motion", WorldOverlayLogic.SendState(false, false, false, false) == "moving");
        Check("modes", WorldOverlayLogic.Mode(0) == "interpolating" && WorldOverlayLogic.Mode(1) == "extrapolating" && WorldOverlayLogic.Mode(2) == "holding" && WorldOverlayLogic.Mode(3) == "snap");
        Check("split owners", WorldOverlayLogic.BadLink(1, 2, true, true));
        Check("passenger owners may differ", !WorldOverlayLogic.BadLink(1, 2, false, true));
        Check("one sided rider", WorldOverlayLogic.BadLink(1, 2, false, false));
        Check("one sided same owner", WorldOverlayLogic.BadLink(1, 1, true, false));
        Check("unknown owner alone is not an error", !WorldOverlayLogic.BadLink(1, -1, true, true));
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Check("invariant error", WorldOverlayLogic.Error(.125, 12.5f, 2, true) == "12 cm / 12.5 deg STALE");
            Check("no sample", WorldOverlayLogic.Error(double.NaN, 0, 4, true) == "no owner sample");
            Check("invariant age", WorldOverlayLogic.Age(.25) == "age 0.25 s");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        var nearest = new OverlayNearest<uint>(40);
        var rng = new Random(42);
        var distances = Enumerable.Range(0, 8192).Select(_ => (float)rng.NextDouble() * 100000).ToArray();
        for (uint i = 0; i < distances.Length; i++) nearest.Offer(i, i, distances[i]);
        var expected = Enumerable.Range(0, distances.Length).Where(i => distances[i] <= 90000).OrderBy(i => distances[i]).ThenBy(i => i).Take(40).Select(i => (uint)i).ToArray();
        Check("nearest 40 across full population", nearest.Count == 40 && nearest.Items.SequenceEqual(expected));
        nearest.Clear();
        nearest.Offer(9, 9, 5); nearest.Offer(1, 1, 5); nearest.Offer(7, 7, float.NaN); nearest.Offer(8, 8, -1); nearest.Offer(10, 10, 90001);
        Check("ties and rejected candidates", nearest.Count == 2 && nearest.Items[0] == 1 && nearest.Items[1] == 9);
        nearest.Clear(); nearest.Offer(3, 3, 90000);
        Check("radius inclusive", nearest.Count == 1);
        var empty = new OverlayNearest<object>(0); empty.Offer(new object(), 1, 1);
        Check("empty capacity", empty.Count == 0);
        // Warmed bounded selection measures only the production selection logic, without Unity projection or drawing.
        for (int n = 0; n < 100; n++) Select(nearest, distances);
        long bytes = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        const int samples = 1000;
        for (int n = 0; n < samples; n++) Select(nearest, distances);
        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency / samples;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Check("selection allocates nothing", allocated == 0);
        Console.WriteLine($"PASS {checks} world overlay checks");
        Console.WriteLine("8192 candidates -> 40: " + ms.ToString("F4", CultureInfo.InvariantCulture) + " ms/selection, " + allocated + " bytes/1000 selections (.NET 9, no Unity drawing)");
    }
    static void Select(OverlayNearest<uint> nearest, float[] distances)
    {
        nearest.Clear();
        for (uint i = 0; i < distances.Length; i++) nearest.Offer(i, i, distances[i]);
    }
}

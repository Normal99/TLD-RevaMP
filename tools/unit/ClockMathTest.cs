using System;
using TLDRevamp.Net;

static class ClockMathTest
{
    private static int _failed;
    private static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _failed++;
    }
    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.000001;
    private static int Main()
    {
        Check("day wrap takes shortest path both ways", Near(ClockMath.Difference(1, 2399, 2400), 2) && Near(ClockMath.Difference(2399, 1, 2400), -2));
        Check("projection includes transit and queued time", Near(ClockMath.Project(100, 50, 55.03, 1), 105.03));
        Check("future stamps never rewind playback", Near(ClockMath.Project(100, 51, 50, 1), 100));
        Check("reordered old anchors are ignored", !ClockMath.AcceptWorldSample(50, 51, -1, false, false, 0, 0));
        Check("pre-wake anchor cannot undo sleep", !ClockMath.AcceptWorldSample(50, -1, 51, true, true, 1, 0));
        Check("post-wake anchor before host accepts sleep is ignored", !ClockMath.AcceptWorldSample(52, -1, 51, false, true, 0, 0));
        Check("periodic anchor with new sleep epoch acknowledges wake", ClockMath.AcceptWorldSample(53, -1, 51, false, true, 1, 0));
        Check("sleep epoch wrap still acknowledges wake", ClockMath.AcceptWorldSample(53, -1, 51, false, true, 0, ushort.MaxValue));
        Check("forced anchor at same timestamp still applies", ClockMath.AcceptWorldSample(51, 51, -1, true, false, 0, 0));
        double next = ClockMath.Follow(100, 1, 104, 1, 2400, false, out bool snap);
        Check("small error slews at bounded speed", !snap && Near(next, 101.1));
        next = ClockMath.Follow(100, 1, 98, 1, 2400, false, out snap);
        Check("client ahead slows rather than jumping back", !snap && Near(next, 100.9));
        next = ClockMath.Follow(2399.9, 0.2, 0.1, 1, 2400, false, out snap);
        Check("midnight crosses without snapping", !snap && Near(next, 0.1));
        next = ClockMath.Follow(100, 0.02, 600, 1, 2400, true, out snap);
        Check("sleep and load force a snap", snap && Near(next, 600));
        next = ClockMath.Follow(100, 0.02, 140, 1, 2400, false, out snap);
        Check("large errors snap", snap && Near(next, 140));
        next = ClockMath.Follow(100, 10, 110, 1, 2400, false, out snap);
        Check("long local pause resumes at host time without an extra snap", !snap && Near(next, 110));
        next = ClockMath.Follow(100, 2, 100, 0, 2400, false, out snap);
        Check("paused host does not advance", !snap && Near(next, 100));

        foreach (double step in new[] { 0.02, 0.1, 0.37 })
        {
            double value = 95, now = 0;
            for (int i = 0; i < (int)Math.Ceiling(120 / step); i++)
            {
                now += step;
                value = ClockMath.Follow(value, step, ClockMath.Wrap(100 + now, 2400), 1, 2400, false, out _);
            }
            Check("converges independently of local frame/time scale " + step, Math.Abs(ClockMath.Difference(100 + now, value, 2400)) < 0.01);
        }
        Check("radio latency is not a five second playback offset", Near(ClockMath.RadioPosition(16.141, 47.1, 52.13, 1, 180), 21.171));
        Check("radio follows host scaled rate", Near(ClockMath.RadioPosition(10, 100, 104, 0.5, 180), 12));
        Check("radio freezes with host", Near(ClockMath.RadioPosition(10, 100, 104, 0, 180), 10));
        Check("late clip waits at end rather than choosing another clip", Near(ClockMath.RadioPosition(179, 100, 105, 1, 180), 179.99));
        Check("new clip anchor replaces previous position", Near(ClockMath.RadioPosition(0, 105, 105.18, 1, 180), 0.18));
        Console.WriteLine(_failed == 0 ? "ALL PASS" : _failed + " FAILED");
        return _failed == 0 ? 0 : 1;
    }
}

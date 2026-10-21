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
        Layout();
        Check("default off", DebugView.Mode == DebugViewMode.Off);
        var mode = DebugViewMode.Off;
        foreach (var expectedMode in new[] { DebugViewMode.Focus, DebugViewMode.Problems, DebugViewMode.Everything, DebugViewMode.Off })
        {
            mode = DebugView.Next(mode); Check("mode cycle " + expectedMode, mode == expectedMode);
        }
        Check("healthy copy", WorldOverlayLogic.Problem(.099, 329, false, false, null, false) == null);
        Check("error threshold inclusive", WorldOverlayLogic.Problem(.1, 0, false, false, null, false) == "10 cm off");
        Check("stale moving copy", WorldOverlayLogic.Problem(0, 2.1, true, false, null, false) == "no update 2.1 s");
        Check("bad link", WorldOverlayLogic.Problem(0, 0, false, true, null, false) == "owner differs");
        Check("recent refusal priority", WorldOverlayLogic.Problem(1, 4, true, true, "refused: wrong owner", true) == "refused: wrong owner");
        Check("rest send", WorldOverlayLogic.Problem(0, 0, false, false, null, true) == "unexpected send at rest");
        foreach (string state in new[] { "held", "stored", "rides net 3", "at rest" })
        {
            string age = WorldOverlayLogic.UpdateAge(329, false, state == "held", state == "stored", state.StartsWith("rides") ? "net 3" : null);
            Check("meaningful old age " + state, age == state && WorldOverlayLogic.Problem(0, 329, false, false, null, false) == null);
        }
        var layout = new DebugLabelLayout();
        var a = new DebugBox(500, 500, 100, 20); var b = a;
        Check("first label placed", layout.Place(ref a, 1920, 1080));
        Check("overlap displaced", layout.Place(ref b, 1920, 1080) && !a.Overlaps(b));
        layout.Clear(); layout.Reserve(new DebugBox(0, 0, 610, 180)); a = new DebugBox(500, 50, 90, 20);
        Check("reserved overlay blocks label", !layout.Place(ref a, 1920, 1080));
        layout.Clear(); a = new DebugBox(500, 500, 100, 20); layout.Reserve(a);
        bool dropped = false;
        for (int i = 0; i < 20; i++) { b = a; dropped |= !layout.Place(ref b, 1920, 1080); }
        Check("dense labels are dropped", dropped);
        var restProbe = new OverlayRestProbe();
        Check("first rest sample healthy", !restProbe.Observe(1, 1, 1, 7, true, 0));
        for (int i = 1; i < 8; i++) Check("no send at rest", !restProbe.Observe(1, 1, 1, 7, true, i * .25));
        Check("one final is healthy", !restProbe.Observe(1, 1, 2, 7, true, 2));
        Check("repeated rest sends flagged", restProbe.Observe(1, 1, 3, 7, true, 2.25));
        Check("control change resets observer", !restProbe.Observe(1, 1, 4, 8, true, 2.5));
        Check("moving excluded", !restProbe.Observe(1, 1, 5, 8, false, 2.75));
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
    static void Layout()
    {
        foreach (var sample in new[] { (720, 10, 11, 14, 24), (1080, 12, 14, 18, 30), (2160, 24, 28, 36, 60) })
        {
            Check("type scale " + sample.Item1, DebugLayout.FontSize(sample.Item1, 12) == sample.Item2 && DebugLayout.FontSize(sample.Item1, 14) == sample.Item3 && DebugLayout.FontSize(sample.Item1, 18) == sample.Item4 && DebugLayout.FontSize(sample.Item1, 30) == sample.Item5);
        }
        foreach (var screen in new[] { (640, 360), (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160) })
        {
            float scale = DebugLayout.Scale(screen.Item2);
            for (int flags = 0; flags < 512; flags++)
            {
                var r = new DebugLayout.Request { Performance = (flags & 1) != 0 ? 210 * scale : 0, Packets = (flags & 2) != 0 ? 310 * scale : 0,
                    Card = 180 * scale, Compare = (flags & 4) != 0 ? 900 * scale : 0, Toast = (flags & 8) != 0 ? 60 * scale : 0,
                    Network = (flags & 16) != 0 ? 48 * scale : 0, Recording = (flags & 32) != 0 ? 32 * scale : 0, More = (flags & 64) != 0 ? 30 * scale : 0, Notice = 32 * scale, Mark = (flags & 128) != 0 ? 210 * scale : 0, Banner = (flags & 256) != 0 ? 60 * scale : 0,
                    NetworkWidth = 430 * scale, RecordingWidth = 340 * scale, MoreWidth = 210 * scale, NoticeWidth = 380 * scale, ToastWidth = 350 * scale };
                DebugLayout.Arrange(screen.Item1, screen.Item2, r);
                var boxes = new[] { DebugLayout.Banner, DebugLayout.Performance, DebugLayout.Packets, DebugLayout.Card, DebugLayout.Compare, DebugLayout.Mark, DebugLayout.Toast, DebugLayout.Notice, DebugLayout.Network, DebugLayout.Recording, DebugLayout.More };
                for (int i = 0; i < boxes.Length; i++)
                {
                    var b = boxes[i]; if (b.W <= 0 || b.H <= 0) continue;
                    Check("region in screen", b.X >= 0 && b.Y >= 0 && b.X + b.W <= screen.Item1 + .01f && b.Y + b.H <= screen.Item2 + .01f);
                    for (int j = i + 1; j < boxes.Length; j++) Check("regions never overlap", !b.Overlaps(boxes[j]));
                }
                float last = -1;
                foreach (var b in new[] { DebugLayout.Performance, DebugLayout.Packets, DebugLayout.Card, DebugLayout.Compare })
                    if (b.H > 0) { Check("left order stable across toggles", b.Y > last); last = b.Y; }
                last = -1;
                foreach (var b in new[] { DebugLayout.Toast, DebugLayout.Notice, DebugLayout.Network, DebugLayout.Recording, DebugLayout.More })
                    if (b.H > 0) { Check("bottom order stable across toggles", b.Y > last); last = b.Y; }
                float cardY = DebugLayout.Card.Y, recordingY = DebugLayout.Recording.Y;
                DebugLayout.Arrange(screen.Item1, screen.Item2, r);
                Check("layout deterministic", cardY == DebugLayout.Card.Y && recordingY == DebugLayout.Recording.Y);
            }
        }
        var measured = new DebugLayout.Request { Performance = 166, Packets = 241, Banner = 61, Mark = 205,
            Toast = 31, ToastWidth = 355, Network = 48, NetworkWidth = 430, Recording = 30, RecordingWidth = 338, More = 28, MoreWidth = 219 };
        DebugLayout.Arrange(1920, 1080, measured);
        Check("measured panel heights preserved", DebugLayout.Performance.H == 166 && DebugLayout.Packets.H == 241 && DebugLayout.Banner.H == 61 && DebugLayout.Mark.H == 205);
        Check("compact bottom dimensions preserved", DebugLayout.More.W == 219 && DebugLayout.More.H == 28 && DebugLayout.Recording.W == 338 && DebugLayout.Recording.H == 30 && DebugLayout.Network.W == 430 && DebugLayout.Network.H == 48 && DebugLayout.Toast.W == 355 && DebugLayout.Toast.H == 31);
        Check("bottom bars centred with shared gap", DebugLayout.More.X == (1920 - 219) / 2f && DebugLayout.More.Y - (DebugLayout.Recording.Y + DebugLayout.Recording.H) == 8 && DebugLayout.Recording.Y - (DebugLayout.Network.Y + DebugLayout.Network.H) == 8 && DebugLayout.Network.Y - (DebugLayout.Toast.Y + DebugLayout.Toast.H) == 8);
        Check("mark remains centred in available region", Math.Abs(DebugLayout.Mark.Y + DebugLayout.Mark.H / 2 - (DebugLayout.Banner.Y + DebugLayout.Banner.H + 8 + DebugLayout.Footer.Y) / 2) < .01f);
        DebugLayout.Arrange(1920, 1080, default);
    }    static void Select(OverlayNearest<uint> nearest, float[] distances)
    {
        nearest.Clear();
        for (uint i = 0; i < distances.Length; i++) nearest.Offer(i, i, distances[i]);
    }
}

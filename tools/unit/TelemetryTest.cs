using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using TLDRevamp;

static class TelemetryTest
{
    private static int _fails;
    private static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) _fails++; }
    private static TelemetryFrame Frame(double t) => new TelemetryFrame { Local = t, St = t + 100, Ms = 16.25f, Gen = -1, Speed = 12.5f, Sent = 123, Received = 456, Gc0 = 1, Gc1 = 2, Gc2 = 3, Flags = 5 };
    private static string Write(FrameRing ring, double from = double.MinValue, double to = double.MaxValue)
    {
        using var writer = new StringWriter(CultureInfo.CurrentCulture);
        ring.Write(writer, "{\"sid\":null,\"momentSt\":130.000}", new[] { "phase\"one", "phaseTwo" }, from, to);
        return writer.ToString();
    }
    private static void RingTests()
    {
        var phases = new[] { 2.125f, 4.25f };
        var ring = new FrameRing(3, 2);
        Check("empty JSON", JsonDocument.Parse(Write(ring)).RootElement.GetProperty("rows").GetArrayLength() == 0);
        for (int i = 0; i < 4; i++) { phases[0] = i; ring.Add(Frame(i), phases); }
        Check("wrap is chronological", ring.Count == 3 && ring.Row(0).Local == 1 && ring.Row(2).Local == 3 && ring.Overwritten == 1);
        Check("phases copied with their own frame", ring.Phase(0, 0) == 1 && ring.Phase(2, 0) == 3);
        var copied = new FrameRing(4, 2);
        ring.CopyTo(copied);
        Check("bulk copy preserves wrap, phases and overflow", copied.Count == 3 && copied.Row(0).Local == 1 && copied.Phase(2, 0) == 3 && copied.Overwritten == 1);
        var gc = new TelemetryFrame(); gc.ObserveGc(50);
        Check("profiler GC without counter advance is flagged", gc.Gc0 == 1 && gc.Gc1 == 0 && gc.Gc2 == 0);
        gc = new TelemetryFrame { Gc0 = 2, Gc1 = 2, Gc2 = 2 }; gc.ObserveGc(50);
        Check("profiler GC does not inflate known counts", gc.Gc0 == 2 && gc.Gc1 == 2 && gc.Gc2 == 2);
        gc = new TelemetryFrame(); gc.ObserveGc(0);
        Check("no GC evidence stays zero", gc.Gc0 == 0);
        var snapshot = ring.Snapshot(2, 3);
        ring.Add(Frame(34), phases);
        Check("time window expires old data", ring.Count == 1 && ring.Row(0).Local == 34);
        Check("bulk copy survives live writes", copied.Row(0).Local == 1 && copied.Phase(2, 0) == 3);
        Check("snapshot survives live writes", snapshot.Count == 2 && snapshot.Row(0).Local == 2 && snapshot.Phase(0, 0) == 2);
        Check("snapshot accepts more frames", AddSnapshot(snapshot, phases));
        var doc = JsonDocument.Parse(Write(snapshot));
        var root = doc.RootElement; var cols = root.GetProperty("cols").EnumerateArray().Select(x => x.GetString()).ToArray();
        var rows = root.GetProperty("rows");
        Check("column and row widths match", rows.EnumerateArray().All(x => x.GetArrayLength() == cols.Length));
        Check("named phase column escapes", cols[2] == "phase\"one");
        var row = rows[0];
        int At(string col) => Array.IndexOf(cols, col);
        Check("flags have stable column order", row[At("rebase")].GetInt32() == 1 && row[At("stream")].GetInt32() == 0 && row[At("saving")].GetInt32() == 1 && row[At("loading")].GetInt32() == 0);
        Check("GC and byte counters preserved", row[At("gc2")].GetInt32() == 3 && row[At("bytesSent")].GetInt64() == 123 && row[At("bytesReceived")].GetInt64() == 456);
        Check("filtered rows include boundaries", JsonDocument.Parse(Write(snapshot, 3, 3)).RootElement.GetProperty("rows").GetArrayLength() == 1);
        Check("empty filtered output stays valid", JsonDocument.Parse(Write(snapshot, 10, 20)).RootElement.GetProperty("rows").GetArrayLength() == 0);
        ring.Clear(); Check("reset clears history and overflow", ring.Count == 0 && ring.Overwritten == 0);
        var timed = new FrameRing(10, 2);
        timed.Add(Frame(0), phases); timed.Add(Frame(30), phases); Check("full 30 second boundary retained", timed.Count == 2);
        timed.Add(Frame(30.01), phases); Check("expired boundary removed", timed.Count == 2 && timed.Row(0).Local == 30);
        var freeze = new FrameRing(3, 2); var frozenFrame = Frame(40); frozenFrame.Ms = 8000;
        freeze.Add(frozenFrame, phases);
        Check("freeze crossing aftermath boundary retained", freeze.Snapshot(0, 35).Count == 1);
        Check("writer includes crossing freeze", JsonDocument.Parse(Write(freeze, 0, 35)).RootElement.GetProperty("rows").GetArrayLength() == 1);
        var empty = new FrameRing(3, 2); empty.CopyTo(copied);
        Check("empty bulk copy clears destination", copied.Count == 0 && copied.Overwritten == 0);
        timed.CopyTo(copied);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) timed.CopyTo(copied);
        Check("preallocated bulk copy allocates zero bytes", GC.GetAllocatedBytesForCurrentThread() == before);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) timed.Add(Frame(i / 60.0), phases);
        Check("ring hot path allocates zero bytes", GC.GetAllocatedBytesForCurrentThread() == before);
    }
    private static bool AddSnapshot(FrameRing ring, float[] phases) { ring.Add(Frame(4), phases, 35); return ring.Row(ring.Count - 1).Local == 4; }
    private static void JsonTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        Check("invariant decimal", Json.Num(12.125) == "12.125");
        Check("non-finite JSON is null", Json.Num(double.NaN) == "null" && Json.Num(double.PositiveInfinity) == "null");
        string value = "\"\\\r\n\t\0\u001f雪";
        Check("JSON escape roundtrip", JsonDocument.Parse(Json.Str(value)).RootElement.GetString() == value);
        RingTests();
    }
    private static void WarningTests()
    {
        var counter = new WarningCounter();
        for (int i = 0; i < 20; i++) for (int n = 0; n <= i; n++) counter.Add("warning", "message" + i, "first\nsecond");
        var rows = JsonDocument.Parse(counter.Json(10)).RootElement;
        Check("top ten sorted by frequency", rows.GetArrayLength() == 10 && rows[0].GetProperty("n").GetInt32() == 20 && rows[9].GetProperty("n").GetInt32() == 11);
        Check("only first stack line", rows[0].GetProperty("at").GetString() == "first");
        Check("top query preserves full counts", JsonDocument.Parse(counter.Json()).RootElement.GetArrayLength() == 20);
        counter.Add("error", "same", ""); counter.Add("exception", "same", "");
        Check("warning/error/exception separated", JsonDocument.Parse(counter.Json()).RootElement.GetArrayLength() == 22);
        var snapshot = counter.Snapshot();
        counter.Clear();
        Check("warning report snapshot owns its counts", JsonDocument.Parse(snapshot.Json()).RootElement.GetArrayLength() == 22 && counter.Json() == "[]");
        counter.Add("warning", new string('x', 200), "");
        Check("messages limited to 160", JsonDocument.Parse(counter.Json()).RootElement[0].GetProperty("msg").GetString().Length == 160);
        counter.Clear(); Check("interval reset", counter.Json(10) == "[]");
        var bounded = new WarningCounter(1); bounded.Add("error", "a", ""); bounded.Add("error", "b", ""); bounded.Add("error", "a", "");
        Check("optional capacity reports overflow", bounded.Dropped == 1 && JsonDocument.Parse(bounded.Json()).RootElement[0].GetProperty("n").GetInt32() == 2);
    }
    private static void MarkTests()
    {
        var limit = new MarkLimiter();
        Check("first mark including zero timestamp", limit.Try(1, 0));
        Check("burst and 9.999 seconds refused", !limit.Try(1, 0) && !limit.Try(1, 9.999));
        Check("players have independent limits", limit.Try(2, 1));
        Check("exact ten second boundary", limit.Try(1, 10));
        Check("clock moving backward cannot bypass", !limit.Try(1, 9));
        Check("invalid clocks rejected", !limit.Try(3, double.NaN) && !limit.Try(3, double.PositiveInfinity));
        limit.Clear(); Check("new session resets limits", limit.Try(1, 0));
    }
    private static void PrivacyTests()
    {
        TelemetryPrivacy.Name("Example Tester");
        string text = TelemetryPrivacy.Clean("Example Tester at 203.0.113.9 id " + new string('7', 17));
        Check("names and connection identities redacted", !text.Contains("Example Tester") && !text.Contains("203.0.113.9") && !text.Contains(new string('7', 17)));
        Check("IPv6 redacted", !TelemetryPrivacy.Clean("endpoint [2001:db8::9]:123").Contains("2001:db8"));
        var warnings = new WarningCounter(); warnings.Add("warning", "LaterKnownName failed", "");
        TelemetryPrivacy.Name("LaterKnownName");
        Check("late-known names redacted before JSON escaping", !warnings.Json(redact: TelemetryPrivacy.Clean).Contains("LaterKnownName"));
    }
    private static void Benchmark()
    {
        var ring = new FrameRing(32768, 15); var phases = new float[15]; var frame = Frame(0);
        for (int i = 0; i < 10000; i++) ring.Add(frame, phases);
        const int frames = 500000;
        var watch = Stopwatch.StartNew(); long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++)
        {
            frame.Local = i / 60.0; frame.St = frame.Local + 100;
            ring.Add(frame, phases, 40);
        }
        watch.Stop(); bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Console.WriteLine("BENCH ring: " + (watch.Elapsed.TotalMilliseconds / frames).ToString("F6", CultureInfo.InvariantCulture) + " ms/frame; " + bytes + " bytes over " + frames + " frames (.NET, not Unity/Mono)");
    }
    private static void CopyBenchmark()
    {
        var ring = new FrameRing(32768, 15); var snapshot = new FrameRing(32768, 15); var phases = new float[15];
        for (int i = 0; i < 6000; i++) ring.Add(Frame(i / 200.0), phases, 40);
        ring.CopyTo(snapshot);
        var watch = new Stopwatch(); long bytes = GC.GetAllocatedBytesForCurrentThread(); watch.Start();
        for (int i = 0; i < 1000; i++) ring.CopyTo(snapshot);
        watch.Stop(); bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Console.WriteLine("BENCH capture copy (6000 frames): " + (watch.Elapsed.TotalMilliseconds / 1000).ToString("F6", CultureInfo.InvariantCulture) + " ms/copy; " + bytes + " bytes (.NET, not Unity/Mono)");
    }
    static int Main()
    {
        JsonTests(); WarningTests(); MarkTests(); PrivacyTests(); Benchmark(); CopyBenchmark();
        Console.WriteLine(_fails == 0 ? "ALL PASS" : _fails + " FAILED"); return _fails == 0 ? 0 : 1;
    }
}

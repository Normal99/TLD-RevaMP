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
        var wide = new FrameRing(2, 4); wide.Add(Frame(1), new[] { 1f, 2f, 3f, 4f });
        using (var w = new StringWriter())
        {
            wide.Write(w, "{}", new[] { "p1", "p2" }, tailNames: new[] { "busyMs", "script:carscript.Update" });
            var wdoc = JsonDocument.Parse(w.ToString()).RootElement;
            var wcols = wdoc.GetProperty("cols").EnumerateArray().Select(c => c.GetString()).ToArray();
            var wrow = wdoc.GetProperty("rows")[0].EnumerateArray().Select(c => c.ToString()).ToArray();
            Check("tail columns after the fixed fields", wcols.Length == 17 && wcols[2] == "p1" && wcols[4] == "gc0" && wcols[15] == "busyMs" && wcols[16] == "script:carscript.Update");
            Check("tail values in tail columns", wrow.Length == 17 && wrow[2] == "1.000" && wrow[15] == "3.000" && wrow[16] == "4.000");
        }
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
    private static void EventTests()
    {
        var ring = new EventRing(3); var copy = new EventRing(3);
        var e = new TelemetryEvent { Kind = "attach-sent", Net = 41, Other = 42, Item = 3, Slot = 4, From = 0, To = 1, Result = "ok", Epoch = 7, Count = 2 };
        for (int i = 0; i < 4; i++) { e.St = 100 + i; e.Local = i; ring.Add(e); }
        Check("event ring wraps in chronological order", ring.Count == 3 && ring.Row(0).St == 101 && ring.Overwritten == 1);
        ring.CopyTo(copy, 3);
        e.Local = 40; ring.Add(e);
        Check("event ring expires quiet history", ring.Count == 1);
        Check("mark event copy owns history", copy.Count == 3 && copy.Row(0).Item == 3 && copy.Row(2).Epoch == 7);
        using (var w = new StringWriter())
        {
            copy.Write(w); var doc = JsonDocument.Parse(w.ToString()).RootElement;
            Check("event JSON preserves addresses/results", doc.GetProperty("rows")[0].GetProperty("other").GetUInt32() == 42 && doc.GetProperty("overwritten").GetInt32() == 1);
        }
        ring.CopyTo(copy, 100);
        Check("mark copy expires events even without new traffic", copy.Count == 0);
        for (int i = 0; i < 1000; i++) ring.Add(e);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) { e.Local = i * .001; ring.Add(e); ring.CopyTo(copy, e.Local); }
        Check("event insert/copy allocates zero bytes", before == GC.GetAllocatedBytesForCurrentThread());
        ring.Clear(); Check("event reset drops history and overflow", ring.Count == 0 && ring.Overwritten == 0);
    }
    private static void StateTests()
    {
        var state = new TelemetryState(); state.Clear(); state.St = 123.456; state.Day = double.NaN; state.LocalPlayer = 0;
        TelemetryPrivacy.Name("State Tester");
        state.Add(new StatePlayer { Id = 0, Name = "State Tester", Net = 41, Seat = 0, HasPos = true, X = 0, Y = 0, Z = 0, St = 123.456, PoseSt = 500 });
        state.Add(new StatePlayer { Id = 1, Name = "Other Tester", Net = 41, Seat = 0 });
        state.Add(new StateSeat { Net = 41, Index = 0, Name = "Driver", InUse = true, InUseMulti = false });
        state.Add(new StateEntity { Net = 41, Owner = 0, Epoch = 7, Parent = 0, Part = -1, Prefab = 5, Name = "Car\"test", Car = true, X = 1.125, Y = 0, Z = 0,
            PoseSt = 500, Yaw = 0, Speed = 0, Vx = 0, Vy = 0, Vz = 0, Fuel = 10.25f, Engine = false, Ignition = true, Key = 1, Radio = float.NaN,
            Proxy = false, Stored = false, Dormant = false, RadioOn = false, RadioAm = false });
        state.Add(new StateSlot { Net = 41, Item = 0, Index = 3, Name = "RL", ContentNet = 41, ContentItem = 12, LocalItem = 234, Prefab = 99, Content = "wheel", Attached = true, Condition = 2, Condition2 = 1, Condition3 = 3 });
        state.Add(new StateSlot { Net = 41, Item = 0, Index = 4, Name = "RR", ContentItem = -1, Prefab = -1 });
        state.Add(new StateStation { Am = false, Index = 2, Clip = 3, Time = 5.5f });
        state.Add(new StateStorm { X = 10, Z = 0, Prefab = 1, Size = 100, Speed = 2, Life = 20, Yaw = 90 });
        using (var w = new StringWriter())
        {
            state.Write(w); string json = w.ToString(); var root = JsonDocument.Parse(json).RootElement;
            Check("state rounds global metres to cm", root.GetProperty("entities")[0].GetProperty("posCm")[0].GetDouble() == 112);
            Check("state names use privacy redaction", !json.Contains("State Tester") && root.GetProperty("players")[0].GetProperty("name").GetString() == "[player]");
            Check("state unknown values are null", root.GetProperty("day").ValueKind == JsonValueKind.Null && root.GetProperty("entities")[0].GetProperty("radio").ValueKind == JsonValueKind.Null);
            Check("state slots preserve canonical identity and emptiness", root.GetProperty("slots")[0].GetProperty("contentItem").GetInt32() == 12 && root.GetProperty("slots")[1].GetProperty("prefab").GetInt32() == -1);
            Check("state seats retain both conflicting occupants", root.GetProperty("seats")[0].GetProperty("players").GetArrayLength() == 2);
            var frame = new FrameRing(1, 0); var events = new EventRing();
            using var combined = new StringWriter(); frame.Write(combined, "{\"format\":2}", Array.Empty<string>(), state: state, events: events);
            Check("one black box contains state and events", JsonDocument.Parse(combined.ToString()).RootElement.GetProperty("state").GetProperty("st").GetDouble() == 123.456);
        }
        state.Clear();
        string worst = new string('\u0001', 64);
        for (int i = 0; i < 3000; i++) state.Add(new StateSlot { Name = worst, Type = worst, Content = worst, Net = uint.MaxValue, ContentNet = uint.MaxValue, Item = int.MinValue, Index = int.MaxValue });
        using (var w = new StringWriter())
        {
            state.Write(w);
            Check("escaped state fits its byte cap", System.Text.Encoding.UTF8.GetByteCount(w.ToString()) <= TelemetryState.MaxBytes && state.Bytes <= TelemetryState.MaxBytes && state.Dropped > 0);
        }
        state.Clear();
        for (int i = 0; i < 3000; i++) state.Add(new StateEntity { Name = worst, X = double.MaxValue, Y = double.MinValue, Z = double.MaxValue, Speed = float.MaxValue, PoseSt = double.MaxValue });
        using (var w = new StringWriter()) { state.Write(w); Check("extreme numeric values also fit byte cap", System.Text.Encoding.UTF8.GetByteCount(w.ToString()) <= TelemetryState.MaxBytes); JsonDocument.Parse(w.ToString()); }
        state.Clear(); Check("pooled state does not retain entries", state.EntityCount == 0 && state.SlotCount == 0 && state.SeatCount == 0 && state.Entities[0].Name == null && state.Slots[0].Name == null);
    }
    private static void CoarseTests()
    {
        var clock = new CoarseClock(8);
        for (int i = 0; i < 129; i++) { long t = clock.Start(); clock.Stop(i % 8, t); }
        clock.FrameEnd();
        Check("coarse saturation is explicit", clock.Last[8] == 1 && clock.Last.Take(8).All(x => x >= 0));
        clock.FrameEnd(); Check("coarse idle frame clears old values", clock.Last.All(x => x == 0));
        const int frames = 100000;
        var ring = new EventRing(); var phases = new float[24]; var frameRing = new FrameRing(32768, 24);
        var frame = Frame(0); var e = new TelemetryEvent { Kind = "live-sent", Result = "ok" };
        void Run(int count)
        {
            for (int f = 0; f < count; f++)
            {
                for (int i = 0; i < CoarseClock.CallLimit; i++) { long t = clock.Start(); clock.Stop(i % 8, t); }
                clock.FrameEnd(); Array.Copy(clock.Last, 0, phases, 15, 9);
                frame.Local = f / 60.0; frameRing.Add(frame, phases, 40);
                e.Local = frame.Local; e.St = frame.Local + 100; ring.Add(e);
            }
        }
        Run(10000);
        long before = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp(); Run(frames);
        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency / frames;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("coarse/frame/event hot path allocates zero bytes", bytes == 0);
        Console.WriteLine("BENCH coarse (128 probes) + frame/event rings: " + ms.ToString("F6", CultureInfo.InvariantCulture) + " ms/frame; " + bytes + " bytes (.NET, not Unity/Mono or Harmony dispatch)");
    }
    static int Main()
    {
        JsonTests(); WarningTests(); MarkTests(); PrivacyTests(); EventTests(); StateTests(); CoarseTests(); Benchmark(); CopyBenchmark();
        Console.WriteLine(_fails == 0 ? "ALL PASS" : _fails + " FAILED"); return _fails == 0 ? 0 : 1;
    }
}

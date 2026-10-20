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
        Check("bridge pickup drop survives recording and sharing", TelemetryPrivacy.EventKind("itemcheck-drop") == "itemcheck-drop" && TelemetryPrivacy.ShareString("kind", "itemcheck-drop"));
        foreach (var kind in new[] { "physlock-lock", "physlock-unlock" })
            Check("physlock event survives privacy filter: " + kind, TelemetryPrivacy.EventKind(kind) == kind);
        foreach (var kind in new[] { "hitch-request", "hitch-accept", "hitch-refuse", "hitch-frozen", "hitch-ownership-claim", "speed-anomaly" })
            Check("hitch event survives privacy filter: " + kind, TelemetryPrivacy.EventKind(kind) == kind);
        foreach (var reason in new[] { "stale epoch", "edit reach", "slot taken or incompatible", "old simulator frozen", "hitch handover" })
            Check("hitch reason survives privacy filter: " + reason, TelemetryPrivacy.EventResult(reason) == reason);
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
        Check("free-text stacks removed", rows[0].GetProperty("at").GetString() == TelemetryPrivacy.Removed);
        Check("top query preserves full counts", JsonDocument.Parse(counter.Json()).RootElement.GetArrayLength() == 20);
        counter.Add("error", "same", ""); counter.Add("exception", "same", "");
        Check("warning/error/exception separated", JsonDocument.Parse(counter.Json()).RootElement.GetArrayLength() == 22);
        var snapshot = counter.Snapshot();
        counter.Clear();
        Check("warning report snapshot owns its counts", JsonDocument.Parse(snapshot.Json()).RootElement.GetArrayLength() == 22 && counter.Json() == "[]");
        counter.Add("warning", new string('x', 200), "");
        Check("free-text messages removed", JsonDocument.Parse(counter.Json()).RootElement[0].GetProperty("msg").GetString() == TelemetryPrivacy.Removed);
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
        var e = new TelemetryEvent { Kind = "attach-sent", Net = 41, Other = 42, Item = 3, Slot = 4, From = 0, To = 1, Result = "ok", Epoch = 7, Count = 2, Dx = 1, Dy = -2, Dz = 3 };
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
        state.Add(new StateSlot { Net = 41, Item = 0, Index = 3, Name = "RL", ContentNet = 41, ContentItem = 12, LocalItem = 234, Prefab = 99, Content = "wheel", Attached = true, HasMount = true, Crafted = true, MountX = .25f, MountY = -.5f, MountZ = .75f, MountRx = 45, MountRy = 90, MountRz = 180, Condition = 2, Condition2 = 1, Condition3 = 3 });
        state.Add(new StateSlot { Net = 41, Item = 0, Index = 4, Name = "RR", ContentItem = -1, Prefab = -1 });
        state.Add(new StateStation { Am = false, Index = 2, Clip = 3, Time = 5.5f });
        state.Add(new StateStorm { X = 10, Z = 0, Prefab = 1, Size = 100, Speed = 2, Life = 20, Yaw = 90 });
        using (var w = new StringWriter())
        {
            state.Write(w); string json = w.ToString(); var root = JsonDocument.Parse(json).RootElement;
            Check("state rounds global metres to cm", root.GetProperty("entities")[0].GetProperty("posCm")[0].GetDouble() == 112);
            Check("state names use privacy redaction", !json.Contains("State Tester") && root.GetProperty("players")[0].GetProperty("name").GetString() == "player-0");
            Check("state unknown values are null", root.GetProperty("day").ValueKind == JsonValueKind.Null && root.GetProperty("entities")[0].GetProperty("radio").ValueKind == JsonValueKind.Null);
            Check("state slots preserve canonical identity and emptiness", root.GetProperty("slots")[0].GetProperty("contentItem").GetInt32() == 12 && root.GetProperty("slots")[1].GetProperty("prefab").GetInt32() == -1);
            Check("slot mount pose exposes visual displacement", root.GetProperty("slots")[0].GetProperty("mountPosCm")[0].GetDouble() == 25 && root.GetProperty("slots")[0].GetProperty("mountRot")[1].GetDouble() == 90 && root.GetProperty("slots")[0].GetProperty("crafted").GetBoolean());
            Check("empty slots have no invented mount pose", !root.GetProperty("slots")[1].TryGetProperty("mountPosCm", out _));
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
    private static void HistoryTests()
    {
        var trace = new MotionTrace(8); var copy = new MotionTrace(8);
        var ids = new uint[] { 1, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        trace.SetFocus(ids, 0, 100);
        Check("focus deduplicates and limits to eight", trace.Contains(1) && trace.Contains(8) && !trace.Contains(9) && trace.FocusDropped == 1);
        trace.Clear(); trace.SetFocus(new uint[] { 1 }, 0, 100);
        for (int i = 0; i < 3; i++) trace.Add(new MotionRow { Net = 1, Epoch = 4, Owner = 0, Kind = 0, Local = i + 1, St = 101 + i, X = 1.25, Y = 0, Z = 0, Yaw = 90, Interval = .05f, Clock = 500 + i });
        trace.Add(new MotionRow { Net = 2, Kind = 0 });
        trace.Add(new MotionRow { Net = 1, Kind = 3, Item = 0, Slot = 2, Local = 3.5, St = 103.5 });
        trace.Add(new MotionRow { Net = 1, Kind = 4, Item = 0, Slot = 2, Local = 3.6, St = 103.6 });
        trace.SetFocus(new uint[] { 2 }, 4, 104);
        trace.CopyTo(copy, 5);
        using (var w = new StringWriter())
        {
            copy.Write(w, 5); var data = JsonDocument.Parse(w.ToString()).RootElement;
            var rate = data.GetProperty("rates").EnumerateArray().First(r => r.GetProperty("net").GetUInt32() == 1);
            Check("rates use time in focus including quiet time", rate.GetProperty("sentHz").GetDouble() == .75 && rate.GetProperty("seconds").GetDouble() == 4);
            Check("live usable rates are not event sampled", rate.GetProperty("usables")[0].GetProperty("sendHz").GetDouble() == .25 && rate.GetProperty("usables")[0].GetProperty("applyHz").GetDouble() == .25);
            Check("trace preserves sender clock and centimetres", data.GetProperty("rows")[1][6].GetDouble() == 500 && data.GetProperty("rows")[1][7].GetDouble() == 125);
            Check("trace records have consistent width", data.GetProperty("rows").EnumerateArray().All(r => r.GetArrayLength() == data.GetProperty("cols").GetArrayLength()));
        }
        trace.CopyTo(copy, 40);
        Check("trace expires quiet history on capture", copy.Ring.Count == 0);
        using (var w = new StringWriter())
        {
            copy.Write(w, 100, false); var data = JsonDocument.Parse(w.ToString()).RootElement;
            Check("quiet active focus reports zero over a bounded window", data.GetProperty("rates")[0].GetProperty("seconds").GetDouble() == 35 && data.GetProperty("rates")[0].GetProperty("sentHz").GetDouble() == 0);
        }
        trace.SetFocus(new uint[] { 1 }, 41, 141);
        for (int i = 0; i < 12; i++) trace.Add(new MotionRow { Net = 1, Kind = 2, Local = 41 + i * .01, Mode = 3, Correction = 4 });
        Check("trace overwrite is explicit", trace.Ring.Count == 8 && trace.Ring.Overwritten > 0);
        trace.CopyTo(copy, 41.05);
        Check("aftermath upper bound filters trace", Enumerable.Range(0, copy.Ring.Count).All(i => copy.Ring.Row(i).Local <= 41.05));
        var gate = new EventSampler(1);
        Check("sample gate accepts first and exact boundary", gate.Take(1, 0, 0, 1, 0) && !gate.Take(1, 0, 0, 1, .499) && gate.Take(1, 0, 0, 1, .5));
        Check("sample collisions only reduce coverage", !gate.Take(2, 0, 0, 2, .6));
        gate.Clear(); Check("sampling resets between sessions", gate.Take(1, 0, 0, 1, 0));
        var longFrames = new LongHistory(); var longCopy = new LongHistory();
        var traffic = new TrafficCount[256]; var timers = new float[24]; timers[15] = 2;
        var work = TrafficCount.Between(new TrafficCount { Received = 10, InBytes = 100, Ticks = 50 }, new TrafficCount { Received = 4, InBytes = 40, Ticks = 20 }, new TrafficCount { Received = 7, InBytes = 70, Ticks = 30 });
        Check("wall-frame traffic includes early current work without recounting prior early work", work.Received == 13 && work.InBytes == 130 && work.Ticks == 60);
        traffic[23] = new TrafficCount { Received = 2, InBytes = 100, Ticks = Stopwatch.Frequency / 1000 };
        for (int i = 0; i < 70; i++) longFrames.Add(new LongFrame { Local = i * .01, St = 100 + i * .01, Ms = 2600, Instantiated = i, Bodies = 3, BodiesDropped = 0, Moved = 4, Destroyed = 1, Activated = 2, Terrain = 3, Roads = 4, Scenery = 5, Buildings = 6 }, traffic, timers);
        Check("long history has explicit capacity/overwrites", longFrames.Ring.Count == 64 && longFrames.Ring.Overwritten == 6);
        longFrames.CopyTo(longCopy, 1);
        traffic[23].Received = 999; timers[15] = 999;
        using (var w = new StringWriter())
        {
            longCopy.Write(w, b => "State", Enumerable.Range(0, 24).Select(i => "timer" + i).ToArray());
            var row = JsonDocument.Parse(w.ToString()).RootElement.GetProperty("rows")[0];
            Check("long snapshot owns handler counts and timers", row.GetProperty("network")[0].GetProperty("received").GetInt32() == 2 && row.GetProperty("timers").GetProperty("timer15").GetDouble() == 2);
            Check("long snapshot preserves work counts", row.GetProperty("instantiated").GetInt32() == 6 && row.GetProperty("bodiesAdded").GetInt32() == 3 && row.GetProperty("rebaseMoved").GetInt32() == 4);
        }
        var one = new LongHistory(1); longFrames.CopyLastTo(one);
        Check("deferred hitch copies only the last frame", one.Ring.Count == 1 && one.Ring.Row(0).Instantiated == 69);
        longFrames.CopyTo(longCopy, 40); Check("long capture expires quiet history", longCopy.Ring.Count == 0);
        var history = new TelemetryHistory(); var frozen = new TelemetryHistory();
        var interval = new TrafficInterval { Local = 10, St = 110, Seconds = 5 };
        interval.Types[23] = new TrafficCount { Sent = 3, OutBytes = 150 };
        interval.InterestCount = 2; interval.InterestPlayers[0] = 0; interval.InterestPlayers[1] = 2;
        interval.InterestSkipped[0] = 99; interval.InterestSkipped[1] = 101;
        interval.CategoriesCount[3] = new TrafficCount { Sent = 3, OutBytes = 729 }; interval.Owned[3] = 4; interval.Bandwidth[0] = 13; interval.Bandwidth[4] = 2;
        interval.Census[3] = new EntityCensus { Loaded = 4, Alive = 2, Limp = 1, Dead = 1, Moving = 3, Resting = 1, Proxy = 2, Receivers = 6 };
        interval.Details[3].Add(0, 680, false); interval.Details[3].Add(1, 25, true); interval.Details[3].Add(2, 24, true);
        interval.Details[3].RestObjects = 1;
        var detached = new TrafficInterval(); interval.CopyTo(detached);
        interval.Census[3].Alive = 99; interval.Details[3].RestSent = 99;
        using (var censusWriter = new StringWriter())
        {
            detached.Write(censusWriter, _ => "State");
            var category = JsonDocument.Parse(censusWriter.ToString()).RootElement.GetProperty("categories")[3];
            Check("detached census retains life/activity/receiver counts", category.GetProperty("alive").GetInt32() == 2 && category.GetProperty("limp").GetInt32() == 1 && category.GetProperty("receivers").GetInt32() == 6);
            Check("traffic distinguishes large full poses from compact/animation and resting sends", category.GetProperty("fullOutBytes").GetInt32() == 680 && category.GetProperty("compactOutBytes").GetInt32() == 25 && category.GetProperty("rabbitOutBytes").GetInt32() == 24 && category.GetProperty("restPoseSent").GetInt32() == 1 && category.GetProperty("restSent").GetInt32() == 2 && category.GetProperty("maxOutBytes").GetInt32() == 680);
            Check("average uses measured category bytes", category.GetProperty("avgOutBytes").GetDouble() == 243);
        }
        interval.Census[3] = detached.Census[3]; interval.Details[3] = detached.Details[3];

        for (int i = 0; i < 10; i++) history.Add(interval);
        history.CopyTo(frozen, 10); interval.Clear(); history.Clear();
        using (var w = new StringWriter())
        {
            new FrameRing(1, 0).Write(w, "{\"format\":3}", Array.Empty<string>(), history: frozen, messageName: b => "State", timerNames: Array.Empty<string>());
            var root = JsonDocument.Parse(w.ToString()).RootElement;
            var net = root.GetProperty("network"); var row = net.GetProperty("rows")[0];
            Check("format three sections serialize together", root.TryGetProperty("trace", out _) && root.TryGetProperty("long", out _) && net.GetProperty("overwritten").GetInt32() == 2);
            Check("bandwidth counters survive detached history and reset", row.GetProperty("bandwidth").GetProperty("identical").GetInt32() == 13 && row.GetProperty("bandwidth").GetProperty("deadAnimation").GetInt32() == 2 && interval.Bandwidth[0] == 0);
            Check("per-receiver interest counters survive history and reset", row.GetProperty("interestSkipped")[1].GetProperty("player").GetInt32() == 2 && row.GetProperty("interestSkipped")[1].GetProperty("skipped").GetInt64() == 101 && interval.InterestCount == 0);
            Check("network serializes nonzero types and owned categories", row.GetProperty("types").GetArrayLength() == 1 && row.GetProperty("categories")[3].GetProperty("owned").GetInt32() == 4 && row.GetProperty("categories")[3].GetProperty("outBytes").GetInt32() == 729);
        }
        history.Clear();
        for (int i = 0; i < 20; i++) { interval.Local = i * 5; history.Add(interval); }
        Check("network age expiry is not counted as overflow", history.NetworkCount == 8 && history.NetworkOverwritten == 0);
        var state = new TelemetryState();
        state.Add(new StatePlayer { Id = 0, InventoryComplete = true, InventoryCount = 1 });
        state.Add(new StateCarry { Player = 0, Net = 4, Item = 2, Kind = 0, Name = "State Tester", Prefab = 5 });
        state.Add(new StateCarry { Player = 0, Net = 5, Item = 0, Kind = 2, Slot = 1 });
        state.Add(new StateUsable { Net = 4, Item = 0, Index = 2, Name = "door", Slide = .9f, XRot = 20, YRot = 10, Turn = 1, Gen = -1, Building = false, BusDoor = false, Tuned = 96 });
        using (var w = new StringWriter())
        {
            state.Write(w); var root = JsonDocument.Parse(w.ToString()).RootElement;
            Check("carried items retain canonical identities and privacy", root.GetProperty("players")[0].GetProperty("held")[0].GetProperty("name").GetString() == TelemetryPrivacy.Removed && root.GetProperty("players")[0].GetProperty("inventory")[0].GetProperty("net").GetInt32() == 5);
            Check("usable angles and unknown lock serialize", root.GetProperty("usables")[0].GetProperty("x").GetDouble() == 20 && root.GetProperty("usables")[0].GetProperty("locked").ValueKind == JsonValueKind.Null);
        }
        state.Clear();
        for (int i = 0; i < 3000; i++) state.Add(new StateUsable { Name = new string('\u0001', 64), X = double.MaxValue, Y = double.MinValue, Z = double.MaxValue });
        using (var w = new StringWriter()) { state.Write(w); Check("usable state respects escaped byte budget", state.Dropped > 0 && System.Text.Encoding.UTF8.GetByteCount(w.ToString()) <= TelemetryState.MaxBytes); }
        var peers = new PeerTrafficTable();
        peers.Add(1, true, 100); peers.Add(1, false, 50);
        int cell = Array.IndexOf(peers.Keys, 1u);
        Check("peer table keeps both directions", peers.Counts[cell].Sent == 1 && peers.Counts[cell].Received == 1 && peers.Counts[cell].OutBytes == 100 && peers.Counts[cell].InBytes == 50);
        for (uint i = 1; i < 1000; i++) peers.Add(i * 128 + 1, true, 10);
        Check("peer probes and collisions are bounded", peers.Used.Count(x => x) == 4 && peers.Dropped > 0);
        peers.Clear(); Check("peer intervals reset independently", peers.Dropped == 0 && peers.Used.All(x => !x));
        var seen = new FrameSet(8);
        Check("body registration deduplicates within a frame", seen.Add(1, 0) == 1 && seen.Add(1, 0) == 0 && seen.Add(1, 1) == 1);
        Check("body registration reports bounded collisions", seen.Add(9, 1) == 1 && seen.Add(17, 1) == 1 && seen.Add(25, 1) == 1 && seen.Add(33, 1) == -1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) seen.Add(i & 7, i / 8);
        Check("body deduplication allocates zero bytes", before == GC.GetAllocatedBytesForCurrentThread());
    }
    private static void HistoryBenchmark()
    {
        const int frames = 100000;
        var trace = new MotionTrace(); var longFrames = new LongHistory(); var types = new TrafficCount[256]; var sampled = new TrafficCount[256]; var columns = new float[34];
        var frameRing = new FrameRing(32768, 34); var events = new EventRing(); var gate = new EventSampler(); var coarse = new CoarseClock(8); var peers = new PeerTrafficTable();
        var focus = new uint[] { 1, 2, 3, 4, 5, 6, 7, 8 }; trace.SetFocus(focus, 0, 100);
        void Run(int count)
        {
            for (int f = 0; f < count; f++)
            {
                double now = f / 60.0;
                for (int i = 0; i < 128; i++) { long t = coarse.Start(); coarse.Stop(i % 8, t); }
                coarse.FrameEnd(); Array.Copy(coarse.Last, 0, columns, 25, 9);
                Array.Clear(types, 0, types.Length);
                for (int i = 0; i < 128; i++) { int type = i & 31; types[type].Received++; types[type].InBytes += 60; types[type].Ticks += 10; peers.Add((uint)(i & 7), false, 60); }
                Array.Copy(types, sampled, 256);
                if (f % 6 == 0) trace.SetFocus(focus, now, now + 100);
                for (int i = 0; i < 8; i++)
                {
                    trace.Add(new MotionRow { Local = now, St = now + 100, Net = (uint)i + 1, Kind = 2, X = f * .1, Speed = 6, Age = .05f });
                    if (f % 3 == 0) { trace.Add(new MotionRow { Local = now, St = now + 100, Net = (uint)i + 1, Kind = 0 }); trace.Add(new MotionRow { Local = now, St = now + 100, Net = (uint)i + 1, Kind = 1 }); }
                    if (gate.Take((uint)i, 0, 1, 2, now)) events.Add(new TelemetryEvent { Local = now, St = now + 100, Kind = "speed-jump", Value = 30 });
                }
                if (f % 1000 == 0) longFrames.Add(new LongFrame { Local = now, St = now + 100, Ms = 600 }, types, columns);
                frameRing.Add(new TelemetryFrame { Local = now, St = now + 100, Ms = 16 }, columns, 40);
            }
        }
        Run(10000); long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp(); Run(frames);
        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency / frames;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("format three hot buffers allocate zero bytes", bytes == 0);
        Console.WriteLine("BENCH format 3 (8 focus, 128 messages, 128 coarse probes, trace/frame/event/long buffers): " + ms.ToString("F6", CultureInfo.InvariantCulture) + " ms/frame; " + bytes + " bytes (.NET, excludes Unity/Mono/Harmony)");
    }
    static int Main()
    {
        JsonTests(); WarningTests(); MarkTests(); PrivacyTests(); EventTests(); StateTests(); CoarseTests(); HistoryTests(); HistoryBenchmark(); Benchmark(); CopyBenchmark();
        Console.WriteLine(_fails == 0 ? "ALL PASS" : _fails + " FAILED"); return _fails == 0 ? 0 : 1;
    }
}

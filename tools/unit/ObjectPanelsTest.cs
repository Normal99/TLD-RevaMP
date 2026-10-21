using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using TLDRevamp;
using TLDRevamp.Net;
using UnityEngine;

static class ObjectPanelsTest
{
    static int checks;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void OffFrames(int n) { for (int i = 0; i < n; i++) { ObjectPanels.Tick(); ObjectPanels.Draw(); } }
    static void Check(string name, bool ok) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static NetReader Reader(byte[] b) => new NetReader { Buf = b, Pos = 0, End = b.Length };
    static byte[] Bytes(NetWriter w) => w.Buf.Take(w.Len).ToArray();
    static NetReader Reader(NetWriter w) => Reader(Bytes(w));
    static ObjectPanelState Full()
    {
        var s = new ObjectPanelState { Net = 7, Epoch = 99, Owner = 3, Parent = 9, Part = 4, LockedTo = 10, Flags = (ObjectPanelState.Bits)4095,
            X = 12345.678, Y = -23.4, Z = 1, Qy = .5f, Qw = .8660254f, Vx = 1, Vy = -2, Vz = 3, Fuel = 8, Key = 1, Rpm = 2200, Radio = 92.5f,
            UsableCount = 64, SlotCount = 64, EventCount = 20, Truncated = 2 };
        for (int i = 0; i < 64; i++)
        {
            s.Usables[i] = new ObjectPanelState.Usable { Item = i / 4, Index = i % 4, Bus = i % 2 == 0, X = i, Y = -i, Slide = i / 64f, Turn = i, Tuned = .7f };
            s.Slots[i] = new ObjectPanelState.Slot { Item = i / 4, Index = i % 4, Content = (uint)i + 8, Member = i, Attached = true, Wheel = i < 8, DrivenWheel = i < 4 };
        }
        for (int i = 0; i < 20; i++) s.Events[i] = new ObjectPanelState.Recent { Age = i, Kind = (ushort)i, Reason = (ushort)i, Net = 7, Other = 9, Epoch = 99, From = 2, To = 3, Item = -1, Slot = i, Count = i + 1, Value = i * .5f, Dx = 1, Dy = -2, Dz = 3 };
        return s;
    }
    static void Data()
    {
        var s = Full(); var w = new NetWriter(); s.Write(w); var b = Bytes(w);
        var decoded = ObjectPanelState.Read(Reader(w));
        Check("bounded snapshot round trip preserves every field", decoded != null && decoded.Json(s, 2) == s.Json(s, 2));
        Check("maximum snapshot stays below 6 KiB", w.Len < 6144);
        Check("all truncated snapshots rejected", Enumerable.Range(0, b.Length).All(n => ObjectPanelState.Read(Reader(b.Take(n).ToArray())) == null));
        var trailing = b.Concat(new byte[] { 0 }).ToArray(); Check("trailing bytes rejected", ObjectPanelState.Read(Reader(trailing)) == null);
        var oversized = (byte[])b.Clone(); oversized[100] = 65;
        Check("oversize usable count rejected", ObjectPanelState.Read(Reader(oversized)) == null);
        s.Events[0].Kind = ushort.MaxValue; w.Reset(); s.Write(w); Check("unknown event code rejected", ObjectPanelState.Read(Reader(w)) == null);
        s = Full(); s.Events[0].Net = 8; s.Events[0].Other = 9; w.Reset(); s.Write(w); Check("unrelated event rejected", ObjectPanelState.Read(Reader(w)) == null);
        s = Full(); s.X = double.NaN; w.Reset(); s.Write(w); Check("invalid pose rejected", ObjectPanelState.Read(Reader(w)) == null);
        s = new ObjectPanelState { Net = 7 }; w.Reset(); s.Write(w); Check("absent object round trip", ObjectPanelState.Read(Reader(w))?.Flags == 0);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        var root = JsonDocument.Parse(s.Json(null, 0)).RootElement;
        Check("JSON uses invariant decimals and null for unknown fuel", root.GetProperty("fuel").ValueKind == JsonValueKind.Null && ObjectPanelState.N(1.25) == "1.25");
        s.Flags = ObjectPanelState.Bits.Found; var mine = new ObjectPanelState { Net = 7, Flags = ObjectPanelState.Bits.Found, X = 3, Y = 4, Qw = -1 };
        Check("distance and quaternion sign invariant rotation difference", s.Distance(mine) == 5 && s.Angle(mine) == 0);
        Check("difference colour boundaries", ObjectPanelState.Severity(.199, 2.99) == 0 && ObjectPanelState.Severity(.2, 0) == 1 && ObjectPanelState.Severity(0, 3) == 1 && ObjectPanelState.Severity(2, 0) == 2 && ObjectPanelState.Severity(0, 15) == 2);
        Check("focus priority and empty focus", ObjectPanelState.Select(1, 2, 3) == 1 && ObjectPanelState.Select(0, 2, 3) == 2 && ObjectPanelState.Select(0, 0, 3) == 3 && ObjectPanelState.Select(0, 0, 0) == 0);
        Check("text covers absent, delta, car, usable and wheel values", new ObjectPanelState().Text(null) == "Object absent" && Full().Text(mine).Contains("delta") && Full().Text(mine).Contains("ignition") && Full().Text(mine).Contains("wheel"));
        var same = Full();
        Check("equal comparison collapses", same.Differences(same) == "truncated 2 / 2");
        same.Truncated = 0; var other = ObjectPanelState.Read(Reader(BytesFor(same)));
        Check("matching fields omitted", other.Differences(same) == "No differences");
        other.Owner++; other.Fuel++; other.Usables[0].Slide += .5f; other.Slots[0].Attached = false;
        string differences = other.Differences(same);
        Check("differences cover owner, fuel, usable and slot without repeated global pose", differences.Contains("owner") && differences.Contains("fuel") && differences.Contains("usable 0/0") && differences.Contains("slot 0/0") && !differences.Contains("quat"));
    }
    static byte[] BytesFor(ObjectPanelState s) { var w = new NetWriter(); s.Write(w); return Bytes(w); }
    static void DrawPanels()
    {
        ObjectPanels.ReserveLayout();
        DebugLayout.Arrange(Screen.width, Screen.height, new DebugLayout.Request { Card = ObjectPanels.CardHeight, Compare = ObjectPanels.CompareHeight });
        ObjectPanels.Draw();
    }    static void CompactCard()
    {
        ObjectPanels.Compare = false; ObjectPanels.Events = false; SessionLog.PanelEventsRing.Clear();
        Mp.Server = null; Mp.Client = new MpClient(); Entities.InSession = true;
        Array.Clear(Entities.TelemetryFocus, 0, Entities.TelemetryFocus.Length);
        DebugView.Mode = DebugViewMode.Focus; Time.realtimeSinceStartupAsDouble += 1; ObjectPanels.Tick();
        GUI.Labels.Clear(); Event.current.type = EventType.Repaint; DrawPanels();
        Check("no target is one compact hint", GUI.Labels.Count == 2 && GUI.Labels[0].Text == "RevaMP · Debug view: Focus" && !GUI.Labels[1].Text.Contains('\n') && DebugLayout.Card.H == 58 && DebugLayout.Card.W <= 500);
        Check("empty events have no feed box or heading", !GUI.Labels[1].Text.Contains("events") && JsonDocument.Parse(ObjectPanels.Json()).RootElement.GetProperty("feed").ValueKind == JsonValueKind.Null);
        Entities.TelemetryFocus[0] = 8; Entities.TelemetryFocus[3] = 9;
        Check("actual focus falls back to car before held item", Entities.PanelFocus(true) == 8);
        Entities.TelemetryFocus[0] = 0; Check("actual focus falls back to held item", Entities.PanelFocus(true) == 9);
        Entities.TelemetryFocus[2] = 7; Check("actual focus prefers crosshair", Entities.PanelFocus(true) == 7);
        ObjectPanels.Command(true, new[] { "compare", "on" });
        var request = Reader(Mp.Client.Sent.Last()); request.U8(); uint token = request.U32(), round = request.U32();
        Entities.TelemetryFocus[2] = 10; Time.realtimeSinceStartupAsDouble += .5; ObjectPanels.Tick();
        ObjectPanels.Receive(token, round, 2, new ObjectPanelState { Net = 7 });
        var json = JsonDocument.Parse(ObjectPanels.Json()).RootElement;
        Check("automatic comparison follows focus and fences previous replies", json.GetProperty("net").GetUInt32() == 10 && json.GetProperty("partners").GetArrayLength() == 0);
        ObjectPanels.Compare = false; Entities.TelemetryFocus[2] = 7; Entities.TelemetryFocus[3] = 0;
        ObjectPanels.ReserveLayout(); DebugLayout.Arrange(Screen.width, Screen.height, new DebugLayout.Request { Packets = 220, Card = ObjectPanels.CardHeight });
        Check("card stacks below packet table", DebugLayout.Card.Y >= 240 && DebugLayout.Card.X == 12);
        ObjectPanels.Compare = true; DebugView.Mode = DebugViewMode.Off;
        int sent = Mp.Client.Sent.Count, reads = tosaveitemscript.Reads; Event.current = null;
        OffFrames(10000);
        Check("Off gates enabled comparison before input, capture and traffic", Mp.Client.Sent.Count == sent && tosaveitemscript.Reads == reads);
        Event.current = new Event { type = EventType.Repaint }; ObjectPanels.Compare = false;
        DebugLayout.Packets = default; DebugView.Mode = DebugViewMode.Off;
    }
    static void CardCosts()
    {
        ObjectPanels.Compare = false; ObjectPanels.Events = false;
        foreach (var mode in new[] { DebugViewMode.Focus, DebugViewMode.Problems })
        {
            DebugView.Mode = mode;
            for (int i = 0; i < 100; i++) { Time.realtimeSinceStartupAsDouble += .5; ObjectPanels.Tick(); GUI.Labels.Clear(); DrawPanels(); }
            long start = Stopwatch.GetTimestamp(), bytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { Time.realtimeSinceStartupAsDouble += .5; ObjectPanels.Tick(); GUI.Labels.Clear(); DrawPanels(); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency / 1000;
            Console.WriteLine("BENCH " + mode + " local card refresh+paint: " + ms.ToString("F4", CultureInfo.InvariantCulture) + " ms; " + allocated / 1000 + " B/refresh (real event path, game/GUI doubles)");
            bytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { ObjectPanels.Tick(); GUI.Labels.Clear(); DrawPanels(); }
            allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            Check(mode + " card repaint between refreshes allocates zero", allocated == 0);
        }
        DebugView.Mode = DebugViewMode.Off;
    }
    static void Events()
    {
        var ring = new EventRing(100); var s = new ObjectPanelState { Net = 7 };
        for (int i = 0; i < 30; i++) ring.Add(new TelemetryEvent { Local = i, Net = i % 2 == 0 ? 7u : 99u, Other = i % 2 == 0 ? 0u : 7u, Kind = "edit-refused", Result = "edit reach", Count = i });
        ring.Add(new TelemetryEvent { Local = 30, Net = 8, Kind = "owner-change" });
        ring.Add(new TelemetryEvent { Local = 30, Net = 7, Kind = "untrusted private text" });
        ring.Add(new TelemetryEvent { Local = 30, Net = 7, Kind = "joint-created", Result = "private user text" });
        ObjectPanelEvents.Capture(ring, s, 30);
        Check("last twenty includes both net and other, newest first", s.EventCount == 20 && ObjectPanelEvents.Kinds[s.Events[0].Kind] == "joint-created" && s.Events[1].Count == 29 && s.Events[19].Count == 11);
        Check("unknown reasons replaced before sending", ObjectPanelEvents.Reasons[s.Events[0].Reason] == "reason unavailable" && !s.Json(null, 0).Contains("private"));
        ObjectPanelEvents.Capture(ring, s, 66); Check("quiet history expires", s.EventCount == 0);
        s.Net = 0; ObjectPanelEvents.Capture(ring, s, 30); Check("no global events when focus absent", s.EventCount == 0);
    }
    static void Routing()
    {
        var route = new ObjectPanelRequest(); route.Set(9, 10, 7, 1);
        Check("route rejects stale token, future round, wrong net and expired replies", route.Accept(9, 3, 7, 4) && !route.Accept(9, 2, 7, 4) && !route.Accept(9, 11, 7, 4) && !route.Accept(8, 10, 7, 4) && !route.Accept(9, 10, 8, 4) && !route.Accept(9, 10, 7, 6));
        route.Set(3, 1, 7, 1); Check("round wrap is handled", route.Accept(3, uint.MaxValue, 7, 1));
        var server = new MpServer(); server.Add(1); server.Add(2); server.Add(3, false);
        Time.realtimeSinceStartupAsDouble = 1;
        server.PanelAsk(1, 9, 10, 7);
        Check("host queries every other ready player", server.T.Sent.Count(p => p.bytes[0] == ObjectPanelWire.Query) == 1 && server.T.Sent.Single(p => p.bytes[0] == ObjectPanelWire.Query).target == 2);
        var hostRow = server.T.Sent.Single(p => p.bytes[0] == ObjectPanelWire.Row);
        var r = Reader(hostRow.bytes); r.U8(); r.U32(); r.U32(); r.U32(); Check("requester also receives host view as player zero", r.I32() == 0 && ObjectPanelState.Read(r)?.Net == 7);
        var w = new NetWriter(); ObjectPanelWire.Header(w, ObjectPanelWire.Answer, 9, 10, 7); w.I32(1); Full().Write(w); r = Reader(w); r.U8();
        server.T.Sent.Clear(); server.PanelMessage(2, ObjectPanelWire.Answer, r);
        r = Reader(server.T.Sent.Single().bytes); r.U8(); r.U32(); r.U32(); r.U32(); Check("relay authenticates source from connection player number", r.I32() == 2 && server.T.Sent[0].target == 1 && ObjectPanelState.Read(r)?.Net == 7);
        server.PanelAsk(1, 9, 11, 0); server.T.Sent.Clear(); r = Reader(w); r.U8(); server.PanelMessage(2, ObjectPanelWire.Answer, r);
        Check("cancelled request cannot relay delayed replies", server.T.Sent.Count == 0);
        Mp.Server = null; Mp.Client = new MpClient(); Entities.MyId = 1; Time.realtimeSinceStartupAsDouble = 2;
        ObjectPanels.Compare = false; ObjectPanels.Events = false;
        ObjectPanels.Command(true, new[] { "compare", "on", "23" });
        var json = JsonDocument.Parse(ObjectPanels.Json()).RootElement;
        Check("bridge selects explicit net id on first session use", json.GetProperty("net").GetUInt32() == 23);
        var req = Reader(Mp.Client.Sent.Last()); req.U8(); uint token = req.U32(), round = req.U32();
        ObjectPanels.Expect(token, round, 23, 2); var state = new ObjectPanelState { Net = 23, Owner = 2, Flags = ObjectPanelState.Bits.Found };
        ObjectPanels.Receive(token, round, 2, state); ObjectPanels.Receive(token - 1, round, 3, state);
        Check("only matching panel replies are displayed", JsonDocument.Parse(ObjectPanels.Json()).RootElement.GetProperty("partners").GetArrayLength() == 1);
        w.Reset(); ObjectPanelWire.Header(w, ObjectPanelWire.Query, 100, 3, 23); w.I32(8);
        r = Reader(w); r.U8(); ObjectPanelWire.Client(ObjectPanelWire.Query, r);
        r = Reader(Mp.Client.Sent.Last());
        Check("client query replies only to requester via host", r.U8() == ObjectPanelWire.Answer && r.U32() == 100 && r.U32() == 3 && r.U32() == 23 && r.I32() == 8 && ObjectPanelState.Read(r)?.Net == 23);
        int before = Mp.Client.Sent.Count; w.U8(0); r = Reader(w); r.U8(); ObjectPanelWire.Client(ObjectPanelWire.Query, r);
        Check("malformed query cannot trigger an upload", Mp.Client.Sent.Count == before);
        ObjectPanelWire.Header(w, ObjectPanelWire.Roster, token, round, 23); w.I32(4); r = Reader(w); r.U8(); ObjectPanelWire.Client(ObjectPanelWire.Roster, r);
        Check("roster shows players awaiting replies", JsonDocument.Parse(ObjectPanels.Json()).RootElement.GetProperty("partners")[1].GetProperty("stale").GetBoolean());
        ObjectPanelWire.Header(w, ObjectPanelWire.Row, token, round, 23); w.I32(4); state.Write(w); r = Reader(w); r.U8(); ObjectPanelWire.Client(ObjectPanelWire.Row, r);
        Check("wire row updates expected player's view", JsonDocument.Parse(ObjectPanels.Json()).RootElement.GetProperty("partners")[1].GetProperty("state").GetProperty("owner").GetInt32() == 2);
        int sent = Mp.Client.Sent.Count; ObjectPanels.Tick(); Time.realtimeSinceStartupAsDouble += .49; ObjectPanels.Tick();
        Check("refresh is throttled to twice per second", Mp.Client.Sent.Count == sent);
        Time.realtimeSinceStartupAsDouble += .02; ObjectPanels.Tick(); Check("next refresh sends a request", Mp.Client.Sent.Count == sent + 1);
        ObjectPanels.Receive(token, round + 1, 2, new ObjectPanelState { Net = 23, Owner = 5 });
        ObjectPanels.Receive(token, round, 2, state);
        Check("older replies cannot replace a newer view", JsonDocument.Parse(ObjectPanels.Json()).RootElement.GetProperty("partners")[0].GetProperty("state").GetProperty("owner").GetInt32() == 5);
        Time.realtimeSinceStartupAsDouble += 3;
        Check("missing replies become stale", JsonDocument.Parse(ObjectPanels.Json()).RootElement.GetProperty("partners")[0].GetProperty("stale").GetBoolean());
        ObjectPanels.Events = true; ObjectPanels.Compare = false; sent = Mp.Client.Sent.Count;
        Time.realtimeSinceStartupAsDouble += 1; ObjectPanels.Tick(); Check("local event feed sends no messages", Mp.Client.Sent.Count == sent);
        ObjectPanels.Events = false; DebugView.Mode = DebugViewMode.Off; int reads = tosaveitemscript.Reads;
        OffFrames(100000);
        long alloc = GC.GetAllocatedBytesForCurrentThread(); var sw = Stopwatch.StartNew(); alloc = GC.GetAllocatedBytesForCurrentThread();
        OffFrames(1000000);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - alloc; sw.Stop();
        Check("off performs no object reads or sends", tosaveitemscript.Reads == reads && Mp.Client.Sent.Count == sent);
        Check("off allocates zero after warmup (" + bytes + " bytes)", bytes == 0);
        Console.WriteLine("BENCH disabled Tick+Draw: " + (sw.Elapsed.TotalMilliseconds / 1000000).ToString("F6", CultureInfo.InvariantCulture) + " ms/frame; " + bytes + " B/frame (.NET, GUI stub)");
        Mp.Server = server; Mp.Client = null; Entities.MyId = 0;
        server.T.Sent.Clear(); ObjectPanels.Command(true, new[] { "compare", "on", "7" });
        Check("host comparison includes all ready remote players", JsonDocument.Parse(ObjectPanels.Json()).RootElement.GetProperty("partners").GetArrayLength() == 2 && server.T.Sent.Count(p => p.bytes[0] == ObjectPanelWire.Query) == 2);
        ObjectPanels.Compare = false;
    }
    static void Benchmark()
    {
        var s = Full(); var w = new NetWriter(); var ring = new EventRing();
        for (int i = 0; i < 2000; i++) ring.Add(new TelemetryEvent { Local = 1, Net = 7, Kind = "edit-refused", Result = "edit reach" });
        for (int i = 0; i < 100; i++) { ObjectPanelEvents.Capture(ring, s, 2); w.Reset(); s.Write(w); s.Text(s); }
        var sw = Stopwatch.StartNew(); long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) { ObjectPanelEvents.Capture(ring, s, 2); w.Reset(); s.Write(w); s.Text(s); }
        long alloc = GC.GetAllocatedBytesForCurrentThread() - start; sw.Stop();
        Console.WriteLine("BENCH max snapshot event selection+encode+text: " + (sw.Elapsed.TotalMilliseconds / 10000).ToString("F4", CultureInfo.InvariantCulture) + " ms/refresh; " + alloc / 10000 + " B/refresh; " + w.Len + " wire bytes (.NET, excludes Unity/transport)");
    }
    static void Captures()
    {
        var root = new tosaveitemscript { partslotscripts = new System.Collections.Generic.List<partslotscript>() };
        var group = new Entities.Ent { NetId = 7, Root = root }; group.Items.Add(root);
        Entities.ByNet[7] = group;
        for (uint i = 100; i < 2100; i++)
        {
            var it = new tosaveitemscript(); var e = new Entities.Ent { NetId = i, Root = it }; e.Items.Add(it); Entities.ByNet[i] = e;
        }
        for (int i = 0; i < 64; i++)
        {
            var it = new tosaveitemscript(); group.Items.Add(it);
            root.partslotscripts.Add(new partslotscript { Part = new attachablescript { tosave = it, attached = true }, tipus = new[] { "wheel" } });
        }
        Entities.ByNet.Walks = 0;
        var s = Entities.PanelCapture(7);
        Check("real capture resolves 64 local slot members without global scans", s.SlotCount == 64 && s.Slots.Select((slot, i) => slot.Content == 7 && slot.Member == i + 1 && slot.Wheel).All(b => b) && Entities.ByNet.Walks == 0 && Entities.GroupScans == 0);
        group.Split = new System.Collections.Generic.HashSet<int> { 1 };
        var loose = new Entities.Ent { NetId = 8, Root = group.Items[1] }; loose.Items.Add(loose.Root); Entities.ByNet[8] = loose;
        var external = Entities.ByNet[100];
        for (int i = 1; i < 64; i++) root.partslotscripts[i].Part.tosave = external.Root;
        s = Entities.PanelCapture(7);
        Check("external slots share one index and split members use their own group", s.Slots[0].Content == 8 && s.Slots[0].Member == 0 && s.Slots.Skip(1).All(slot => slot.Content == 100 && slot.Member == 0) && Entities.ByNet.Walks == 2 && Entities.GroupScans == 0);
        Mp.Server = null; Mp.Client = new MpClient(); ObjectPanels.Compare = false;
        Time.realtimeSinceStartupAsDouble = 100;
        int reads = tosaveitemscript.Reads;
        SessionLog.PanelEventsRing.Add(new TelemetryEvent { Local = 100, Net = 7, Kind = "owner-change" });
        ObjectPanels.Command(false, new[] { "events", "on" });
        var feed = JsonDocument.Parse(ObjectPanels.Json()).RootElement.GetProperty("feed");
        Check("feed-only refresh reads real ring without object capture", tosaveitemscript.Reads == reads && feed.GetProperty("events").GetArrayLength() == 1);
        ObjectPanels.Events = false;
        Entities.ByNet.Clear(); SessionLog.PanelEventsRing.Clear();
    }
    static void RoundComparisons()
    {
        Mp.Server = null; Mp.Client = new MpClient(); Entities.MyId = 1;
        var root = new tosaveitemscript(); var e = new Entities.Ent { NetId = 23, Root = root }; e.Items.Add(root); Entities.ByNet[23] = e;
        ObjectPanels.Compare = false; Time.realtimeSinceStartupAsDouble = 200;
        ObjectPanels.Command(true, new[] { "compare", "on", "23" });
        var request = Reader(Mp.Client.Sent.Last()); request.U8(); uint token = request.U32(), round = request.U32();
        var reply = Entities.PanelCapture(23);
        root.transform.position = new Vector3 { x = 12.5f }; root.transform.rotation = new Quaternion { y = .7071068f, w = .7071068f };
        Time.realtimeSinceStartupAsDouble += .5; ObjectPanels.Tick();
        ObjectPanels.Receive(token, round, 2, reply);
        void SameRound(string name)
        {
            var partner = JsonDocument.Parse(ObjectPanels.Json()).RootElement.GetProperty("partners")[0]; var state = partner.GetProperty("state");
            GUI.Labels.Clear(); Event.current.type = EventType.Repaint; DrawPanels();
            var label = GUI.Labels.Single(l => l.Text.Contains("Player 2"));
            Check(name, state.GetProperty("distanceM").GetDouble() == 0 && state.GetProperty("angleDeg").GetDouble() == 0 && label.Text.Contains("No differences") && label.Color.g == OverlayStyle.Text.g && GUI.LastDot.g == (partner.GetProperty("stale").GetBoolean() ? OverlayStyle.Warning.g : OverlayStyle.Ok.g));
        }
        SameRound("delayed reply immediately updates text, colour and JSON against its local round");
        int sent = Mp.Client.Sent.Count;
        ObjectPanels.Command(true, new[] { "compare", "all" }); GUI.Labels.Clear(); DrawPanels();
        Check("all fields is presentation only", Mp.Client.Sent.Count == sent && GUI.Labels.Any(l => l.Text.Contains("quat")) && JsonDocument.Parse(ObjectPanels.Json()).RootElement.GetProperty("allFields").GetBoolean());
        ObjectPanels.Command(true, new[] { "compare", "differences" });
        for (int i = 0; i < 9; i++) { Time.realtimeSinceStartupAsDouble += .5; ObjectPanels.Tick(); ObjectPanels.Expect(token, round + (uint)i + 2, 23, 2); }
        SameRound("stored reply keeps its round reference after local history wraps");
        ObjectPanels.Receive(token, round, 3, reply);
        Check("expired rounds cannot bind to overwritten local history", JsonDocument.Parse(ObjectPanels.Json()).RootElement.GetProperty("partners").GetArrayLength() == 1);
        request = Reader(Mp.Client.Sent.Last()); request.U8(); request.U32(); uint latest = request.U32();
        reply = Entities.PanelCapture(23); ObjectPanels.Receive(token, latest, 2, reply);
        root.transform.position = new Vector3 { x = 25 }; Time.realtimeSinceStartupAsDouble += .5; ObjectPanels.Tick();
        SameRound("refresh while awaiting the next reply keeps matched deltas");
        ObjectPanels.Compare = false; Entities.ByNet.Clear();
    }
    static void Consent()
    {
        Mp.Server = null; Mp.Client = new MpClient(); Time.realtimeSinceStartupAsDouble = 300;
        SessionLog.PanelEventsRing.Add(new TelemetryEvent { Local = 300, Net = 7, Kind = "owner-change" });
        ObjectPanelState Query(bool share)
        {
            Reports.Share = share; var w = new NetWriter(); ObjectPanelWire.Header(w, ObjectPanelWire.Query, 1, 1, 7); w.I32(8);
            var r = Reader(w); r.U8(); ObjectPanelWire.Client(ObjectPanelWire.Query, r);
            r = Reader(Mp.Client.Sent.Last()); r.U8(); r.U32(); r.U32(); r.U32(); r.I32(); return ObjectPanelState.Read(r);
        }
        var s = Query(false);
        Check("client answer withholds events and encodes consent flag", s.EventCount == 0 && s.Has(ObjectPanelState.Bits.EventsNotShared) && !JsonDocument.Parse(s.Json(null, 0)).RootElement.GetProperty("eventsShared").GetBoolean());
        Check("client sharing answer includes real ring events", Query(true).EventCount == 1);
        var server = new MpServer(); server.Add(1);
        foreach (bool share in new[] { false, true })
        {
            Reports.Share = share; server.T.Sent.Clear(); server.PanelAsk(1, 1, 1, 7);
            var r = Reader(server.T.Sent.Single(p => p.bytes[0] == ObjectPanelWire.Row).bytes); r.U8(); r.U32(); r.U32(); r.U32(); r.I32(); s = ObjectPanelState.Read(r);
            Check("host answer follows sharing consent: " + share, s.EventCount == (share ? 1 : 0) && s.Has(ObjectPanelState.Bits.EventsNotShared) != share);
        }
        Reports.Share = false;
        Check("local feed retains events with sharing off", ObjectPanels.Capture(7, eventsOnly: true).EventCount == 1);
        ObjectPanels.Command(true, new[] { "compare", "on", "7" });
        var req = Reader(Mp.Client.Sent.Last()); req.U8(); uint token = req.U32(), round = req.U32();
        ObjectPanels.Receive(token, round, 2, Query(false)); ObjectPanels.Events = true; ObjectPanels.Tick();
        GUI.Labels.Clear(); Event.current.type = EventType.Repaint; DrawPanels();
        Check("feed distinguishes withheld events from empty history", GUI.Labels.Any(l => l.Text != null && l.Text.Contains("Player 2: events not shared")));
        ObjectPanels.Compare = false; ObjectPanels.Events = false; SessionLog.PanelEventsRing.Clear();
    }
    static void Docs()
    {
        string protocol = System.IO.File.ReadAllText("src/TLDRevamp/Net/Protocol.cs"), docs = System.IO.File.ReadAllText("docs/TELEMETRY.md");
        string version = System.Text.RegularExpressions.Regex.Match(protocol, @"Version\s*=\s*(\d+)").Groups[1].Value;
        Check("transfer heading matches actual protocol", docs.Contains("introduced in protocol 33; current protocol " + version + ")"));
        Check("panel wire documentation matches actual protocol", docs.Contains("Protocol **" + version + "** uses reliable request/cancel"));
        Check("README has one debug tools paragraph", System.Text.RegularExpressions.Regex.Matches(System.IO.File.ReadAllText("README.md"), @"\*\*Debug tools:\*\*").Count == 1);
        foreach (string path in System.IO.Directory.GetFiles("docs", "*.md").Concat(new[] { "README.md" }))
        {
            string text = System.IO.File.ReadAllText(path).Replace("**", "");
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text,
                @"(?:Protocol\.Version is |[Pp]rotocol (?:is |stays )|current protocol )(\d+)|Protocol (\d+) is required"))
                Check("current protocol in " + path, (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) == version);
        }
    }
    static void Allocations()
    {
        SessionLog.AllocCounter = false;
        var field = typeof(ObjectPanels).GetField("_alloc", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        long before = (long)field.GetValue(null);
        ObjectPanels.Capture(0);
        Check("unavailable allocation counter reports null", JsonDocument.Parse(ObjectPanels.Json()).RootElement.GetProperty("cost").GetProperty("captureAllocBytes").ValueKind == JsonValueKind.Null);
        Check("unavailable allocation counter is not accumulated", (long)field.GetValue(null) == before);
        SessionLog.AllocCounter = true;
        ObjectPanels.Capture(0);
        Check("available allocation counter measures real capture allocations", JsonDocument.Parse(ObjectPanels.Json()).RootElement.GetProperty("cost").GetProperty("captureAllocBytes").GetInt64() > before);
        SessionLog.AllocCounter = false;
    }
    static void Main() { Data(); Events(); Routing(); Captures(); RoundComparisons(); Consent(); CompactCard(); Allocations(); Docs(); Benchmark(); CardCosts(); Console.WriteLine("ALL PASS (" + checks + " checks)"); }
}

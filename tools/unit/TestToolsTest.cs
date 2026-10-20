using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using TLDRevamp;

static class TestToolsTest
{
    private static int _checks;
    private static void Check(bool ok, string why) { _checks++; if (!ok) throw new Exception(why); }
    private static void Main()
    {
        Check(!NetworkSimulation.Preset(0).Active, "off preset");
        Check(!new NetworkSimulation { SendOnly = true, Jitter = 40 }.Active, "legacy zero jitter percentage has no impairment");
        int[] ms = { 0, 120, 60, 200 }, jitter = { 0, 15, 40, 60 }; float[] loss = { 0, 0.5f, 3, 5 };
        for (int i = 0; i < 4; i++)
        {
            var p = NetworkSimulation.Preset(i);
            Check(p.Valid && p.Latency == ms[i] && p.Jitter == jitter[i] && p.Loss == loss[i] && p.Duplication == 0 && p.Reorder == 0, "preset values");
            using var json = JsonDocument.Parse(p.Json()); Check(json.RootElement.GetProperty("lossPct").GetSingle() == loss[i], "preset JSON");
        }
        Check(!new NetworkSimulation { Loss = float.NaN }.Valid && !new NetworkSimulation { Duplication = float.PositiveInfinity }.Valid, "nonfinite conditions refused");
        Check(!new NetworkSimulation { Latency = -1 }.Valid && !new NetworkSimulation { Jitter = 2001 }.Valid && !new NetworkSimulation { Reorder = 101 }.Valid, "bounds");
        Check(new NetworkSimulation { Reorder = 1 }.Active && new NetworkSimulation { Duplication = 1 }.Active, "reorder and duplication indicator");
        var data = new ReproData(3);
        Check(data.Add(new ReproRow { Kind = ReproKind.Pose, T = 0, St = 42, X = 10.25, Y = -4.5, Z = 1e7, A = 20, B = -70 }), "pose accepted");
        Check(data.Add(new ReproRow { Kind = ReproKind.Pickup, T = 1, St = 43, Net = uint.MaxValue, Other = 2, Item = 3, Slot = 4, C = 5, D = 6, E = 7, F = 8, G = 9, H = 10 }), "net id round trip");
        Check(!data.Add(new ReproRow { Kind = ReproKind.Pose, T = 0.5 }), "backwards time refused");
        Check(!data.Add(new ReproRow { Kind = ReproKind.Pose, T = 2, X = double.NaN }), "nonfinite row refused");
        Check(!data.Add(new ReproRow { T = 2, Kind = (ReproKind)1000 }), "unknown kind refused");
        Check(data.Add(new ReproRow { Kind = ReproKind.Command, T = 2, A = 1 }), "command accepted");
        Check(!data.Add(new ReproRow { Kind = ReproKind.Pose, T = 3 }) && data.Count == 3 && data.Dropped == 4, "cap keeps beginning, counts losses");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        using var writer = new StringWriter(); data.Write(writer, 40);
        using var doc = JsonDocument.Parse(writer.ToString()); var root = doc.RootElement;
        Check(root.GetProperty("format").GetInt32() == 1 && !root.GetProperty("complete").GetBoolean(), "version and completeness");
        Check(root.GetProperty("cols").GetArrayLength() == 18 && root.GetProperty("rows")[0].GetArrayLength() == 18, "schema width");
        Check(root.GetProperty("rows")[0][7].GetDouble() == 10.25 && root.GetProperty("rows")[1][3].GetUInt32() == uint.MaxValue, "locale independent numeric JSON");
        foreach (var row in root.GetProperty("rows").EnumerateArray()) foreach (var value in row.EnumerateArray()) Check(value.ValueKind == JsonValueKind.Number, "numeric-only event payload");
        Check(ReproData.Command("ordinary private message") == -1 && ReproData.Command(null) == -1, "chat excluded");
        Check(ReproData.Command("/tpa private-person") == 1 && ReproData.Command("/Y") == 2 && ReproData.Command("/tpdeny") == 3, "command aliases, no arguments");
        Check(ReproData.Command("/password sensitive") == 0 && ReproData.Command("/mark private note") == 4, "unknown/private arguments not retained");
        Check(ReproData.Command("/tpa off") == 0 && ReproData.Command("/tpa on") == 0, "admin tpa toggles cannot become teleport requests");
        var partial = new ReproData();
        foreach (var unresolvable in new[] { new ReproRow { Kind = ReproKind.Usable }, new ReproRow { Kind = ReproKind.PhysLock, Net = 1 },
            new ReproRow { Kind = ReproKind.Hitch, Net = 1 }, new ReproRow { Kind = ReproKind.Bind, Net = 1, A = -1 } })
            Check(!partial.Add(unresolvable), "unresolvable row skipped");
        Check(partial.Add(new ReproRow { Kind = ReproKind.Pose }) && partial.Skipped == 4 && partial.Dropped == 0, "useful recording survives missing addresses");
        using var partialWriter = new StringWriter(); partial.Write(partialWriter, 123);
        using var partialJson = JsonDocument.Parse(partialWriter.ToString());
        Check(partialJson.RootElement.GetProperty("complete").GetBoolean() && partialJson.RootElement.GetProperty("skipped").GetInt32() == 4, "skips do not mark incomplete");
        Check(!partial.Add(new ReproRow { Kind = ReproKind.Bind, X = double.NaN }) && partial.Dropped == 1, "malformed unresolved row is still real loss");
        var full = new ReproData(); var rowValue = new ReproRow { Kind = ReproKind.Pose, T = 1 };
        for (int i = 0; i < 1000; i++) full.Add(rowValue);
        var clock = new Stopwatch(); long allocated = GC.GetAllocatedBytesForCurrentThread(); clock.Start();
        for (int i = 0; i < 50000; i++) full.Add(rowValue);
        clock.Stop(); long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Check(bytes == 0, "append allocates no managed memory");
        Console.WriteLine("append ns/row: " + (clock.Elapsed.TotalMilliseconds * 1000000 / 50000).ToString("F1", CultureInfo.InvariantCulture) +
            "; row bytes: " + Marshal.SizeOf<ReproRow>() + "; capacity bytes: " + Marshal.SizeOf<ReproRow>() * ReproData.Capacity);
        Console.WriteLine("ALL PASS (" + _checks + " test-tool checks)");
    }
}

// Real format-3 capture, report zipper, transfer validation and disk save; game and transport are fakes.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using TLDRevamp;
using TLDRevamp.Net;

static class ReportCaptureSharingTest
{
    private static int _fails;
    private static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) _fails++; }
    private static void Pump(Func<bool> done, Action tick)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < 5000)
        { UnityEngine.Time.realtimeSinceStartupAsDouble += .02; tick(); Thread.Sleep(1); }
    }
    private static NetReader Reader(byte[] packet) => new NetReader { Buf = packet, Pos = 1, End = packet.Length };
    private static void Consent()
    {
        var w = new NetWriter(); w.U8(1);
        Reports.ServerConsent(1, new NetReader { Buf = w.Buf, End = w.Len });
    }
    private static void Scan(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        Check("seeded private values absent", TelemetryPrivacyTest.Secrets.All(s => !text.Contains(s, StringComparison.OrdinalIgnoreCase)));
    }
    private static void Transfer(string root, bool host, bool mark, bool scrub = false)
    {
        string label = (host ? "host" : "client") + (mark ? " mark" : " report") + (scrub ? " scrub" : "");
        string expectedMark = null;
        Mp.Server = null; Mp.Client = null; Reports.Reset();
        if (host) Mp.Server = new Server(); else Mp.Client = new Client();
        Reports.Share = true;
        if (host) Consent();
        BepInEx.Paths.BepInExRootPath = Path.Combine(root, label.Replace(' ', '-'));
        var telemetry = new Telemetry();
        Plugin.Instance.Feedback = new FeedbackReporter(telemetry);
        UnityEngine.Time.realtimeSinceStartupAsDouble += 30;
        SessionLog.Frame(16, telemetry.Breakdown);
        foreach (string result in (System.Collections.Generic.HashSet<string>)typeof(TelemetryPrivacy).GetField("Results", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null))
            SessionLog.Event("part-state", 4, result: result);
        SessionLog.Event("part-state", 4, result: TelemetryPrivacyTest.Private);
        string note = "held " + TelemetryPrivacyTest.Secrets[0];
        double moment = SessionLog.St;
        if (mark)
        {
            string basename = ReportTransfer.MomentName(SessionLog.Sid, moment, "");
            var completion = Reports.MarkCompletion(basename, note, moment);
            SessionLog.Capture(Path.Combine(FeedbackReporter.Root, "marks", basename + ".json"), moment, path =>
            {
                // Reproduce 62528d8's additive network schema on the completed capture from this branch.
                var box = JsonNode.Parse(File.ReadAllText(path));
                box["network"]["rows"][0]["bandwidth"] = JsonNode.Parse("{\"identical\":1,\"corpseRoot\":2,\"unknown\":3,\"creatureRange\":4,\"deadAnimation\":5}");
                box["futureCounter"] = 42;
                box["futureTree"] = JsonNode.Parse("{\"samples\":[1,-2.50,true,false,null,{\"nestedCounter\":3}]}");
                if (scrub)
                {
                    box["futureText"] = string.Join(" ", TelemetryPrivacyTest.Secrets);
                    box[TelemetryPrivacyTest.Secrets[0]] = 9;
                }
                expectedMark = box.ToJsonString();
                File.WriteAllText(path, expectedMark, new UTF8Encoding(false));
                completion(path);
            }, note);
        }
        else Reports.LocalReport(note);
        Pump(() => Reports.FilesSent == 1 || Reports.Refusals > 0, () =>
        {
            SessionLog.Frame(16, telemetry.Breakdown); Plugin.Instance.Feedback.Tick(); Reports.Tick();
        });
        Check(label + " completed capture sends", Reports.FilesSent == 1 && Reports.Refusals == 0);
        if (Reports.FilesSent != 1) { Console.WriteLine(Reports.Stats()); return; }
        Check(label + " per-file scrub counts", Reports.LastFieldsScrubbed == (scrub ? 1 : 0) && (!mark || Reports.LastKeysDropped == (scrub ? 1 : 0)) && Reports.FieldsScrubbed == (scrub ? 1 : 0));
        string tick = (string)typeof(SessionLog).GetMethod("TickLine", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { SessionLog.St });
        using (var line = JsonDocument.Parse(tick))
            Check(label + " play tick scrub counts", line.RootElement.GetProperty("sharing").GetProperty("lastFieldsScrubbed").GetInt32() == (scrub ? 1 : 0));
        var packets = host ? Mp.Server.Packets.Select(p => p.Bytes).ToArray() : Mp.Client.Packets.ToArray();
        string sid = SessionLog.Sid;
        Mp.Server = null; Mp.Client = null; Reports.Reset();
        BepInEx.Paths.BepInExRootPath = Path.Combine(root, label.Replace(' ', '-') + "-receiver");
        if (host) Mp.Client = new Client(); else Mp.Server = new Server();
        Reports.Share = true;
        if (!host) Consent();
        foreach (byte[] packet in packets)
        {
            UnityEngine.Time.realtimeSinceStartupAsDouble += .12;
            if (host) Reports.ClientChunk(Reader(packet)); else Reports.ServerChunk(1, 1234, TelemetryPrivacyTest.Secrets[0], Reader(packet));
        }
        Pump(() => Reports.FilesReceived == 1 || Reports.Refusals > 0, Reports.Tick);
        Check(label + " receiver saves", Reports.FilesReceived == 1 && Reports.Refusals == 0);
        if (Reports.FilesReceived != 1) { Console.WriteLine(Reports.Stats()); return; }
        Check(label + " indexed filename", Path.GetFileName(Reports.LastReceived).Contains("-from-p" + (host ? 0 : 1) + "-") &&
            (!mark || Path.GetFileName(Reports.LastReceived).StartsWith(ReportTransfer.MomentName(sid, moment, ""))));
        byte[] json;
        if (mark) json = File.ReadAllBytes(Reports.LastReceived);
        else
        {
            using var zip = ZipFile.OpenRead(Reports.LastReceived);
            using var memory = new MemoryStream();
            foreach (var entry in zip.Entries) { using var stream = entry.Open(); stream.CopyTo(memory); }
            Scan(memory.ToArray()); memory.SetLength(0);
            using var box = zip.GetEntry("blackbox.json").Open(); box.CopyTo(memory); json = memory.ToArray();
        }
        Scan(json); Scan(Encoding.UTF8.GetBytes(Path.GetFileName(Reports.LastReceived)));
        using var doc = JsonDocument.Parse(json);
        if (mark)
        {
            Check(label + " future flat and nested numeric values survive", doc.RootElement.GetProperty("futureCounter").GetInt32() == 42 &&
                doc.RootElement.GetProperty("futureTree").GetProperty("samples")[5].GetProperty("nestedCounter").GetInt32() == 3);
            if (scrub) Check(label + " unknown string removed and unsafe key dropped", doc.RootElement.GetProperty("futureText").GetString() == TelemetryPrivacy.Removed && !doc.RootElement.TryGetProperty(TelemetryPrivacyTest.Secrets[0], out _));
            else Check(label + " numeric-only capture saved unchanged", Encoding.UTF8.GetString(json) == expectedMark);
        }
        Check(label + " completed format 3 and cleaned note", doc.RootElement.GetProperty("head").GetProperty("complete").GetBoolean() &&
            doc.RootElement.GetProperty("head").GetProperty("format").GetInt32() == 3 &&
            doc.RootElement.GetProperty("head").GetProperty("note").GetString() == "held [removed]");
        Check(label + " diagnostic counters", Reports.Stats().Contains("\"received\":1") && Reports.Stats().Contains("\"refusals\":0"));
    }
    static int Main(string[] args)
    {
        string root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "capture-share-" + Guid.NewGuid().ToString("N")));
        foreach (string secret in TelemetryPrivacyTest.Secrets) TelemetryPrivacy.Name(secret);
        SessionLog.Sid = "0123456789abcdef";
        SessionLog.Install(); TLDRevamp.Diagnostics.ErrorLog.Install();
        foreach (bool host in new[] { true, false }) foreach (bool mark in new[] { true, false }) Transfer(root, host, mark);
        foreach (bool host in new[] { true, false }) Transfer(root, host, true, true);
        SessionLog.Shutdown();
        Console.WriteLine("Failures: " + _fails + "; " + root); return _fails == 0 ? 0 : 1;
    }
}

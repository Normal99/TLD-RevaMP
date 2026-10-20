// Offline exercise of the real Reports state machine. Only the game/network/UI surfaces below are fakes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using TLDRevamp;
using TLDRevamp.Net;

static class ReportSharingTest
{
    private static int _fails;
    private const string Box = "{\"head\":{\"privacy\":1,\"sid\":\"0123456789abcdef\",\"momentSt\":100.125},\"cols\":[\"st\"],\"rows\":[[100]]}";
    private static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) _fails++; }
    private static NetReader Reader(NetWriter w) => new NetReader { Buf = w.Buf.Take(w.Len).ToArray(), End = w.Len };
    private static void Consent(int id, bool share) { var w = new NetWriter(); w.U8(share ? (byte)1 : (byte)0); Reports.ServerConsent(id, Reader(w)); }
    private static NetWriter Rename(string note, double st = 100.125, int origin = 1)
    {
        var w = new NetWriter(); w.Str(SessionLog.Sid); w.F64(st); w.I32(origin); w.Str(note); return w;
    }
    private static NetWriter RelayRename(string note, int renamer = 1)
    {
        var w = Rename(note); w.I32(renamer); return w;
    }
    private static int Renamer(byte[] packet)
    {
        var r = new NetReader { Buf = packet, Pos = 1, End = packet.Length }; r.Str(); r.F64(); r.I32(); r.Str(); return r.I32();
    }
    private static string RenameNote(byte[] packet)
    {
        var r = new NetReader { Buf = packet, Pos = 1, End = packet.Length }; r.Str(); r.F64(); r.I32(); return r.Str();
    }
    private static void Pump(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < 5000) { UnityEngine.Time.realtimeSinceStartup += .02f; Reports.Tick(); Thread.Sleep(1); }
    }
    private static void Start(bool host, bool share, string root)
    {
        Mp.Server = null; Mp.Client = null; Reports.Reset();
        UnityEngine.Time.realtimeSinceStartup = 100.125f;
        FeedbackReporter.Root = root; Session.Notices.Clear();
        if (host) Mp.Server = new MpServer(); else Mp.Client = new MpClient();
        Reports.Share = share;
    }
    private static void RenameTests(string root)
    {
        Start(true, true, Path.Combine(root, "rename-host")); Consent(1, true); Consent(2, true); Consent(3, false);
        Reports.KnowMark(SessionLog.Sid, 100.125, 1);
        string markDir = SessionLog.MarksFolder;
        MarkFiles.Run(() =>
        {
            string file = Path.Combine(markDir, MarkFiles.Name(markDir, SessionLog.Sid, 100.125, "old note"));
            string box = Box.Replace("\"momentSt\":100.125", "\"momentSt\":100.125,\"origin\":1,\"start\":\"2026-10-20\",\"role\":\"host\",\"note\":\"old note\"");
            MarkFiles.Atomic(file, box); return MarkFiles.Register(FeedbackReporter.Root, file);
        }).GetAwaiter().GetResult();
        Reports.ServerRename(2, Reader(Rename("foreign edit"))); Reports.Tick(); MarkFiles.Pending.GetAwaiter().GetResult();
        Check("foreign rename of known mark refused without disk write or relay", Mp.Server.Packets.Count == 0 && MarkFiles.Snapshot.Single().Note == "old note");
        TelemetryPrivacy.Name("PrivateRenamePersona");
        Reports.ServerRename(1, Reader(Rename("new PrivateRenamePersona")));
        Pump(() => Mp.Server.Packets.Count == 2); MarkFiles.Pending.GetAwaiter().GetResult();
        Check("rename cleans and updates host copy", MarkFiles.Snapshot.Single().Note == "new [removed]");
        Check("rename reliably relayed only to sharing peers", Mp.Server.Packets.Select(p => p.Target).OrderBy(n => n).SequenceEqual(new[] { 1, 2 }) && Mp.Server.Packets.All(p => p.Bytes[0] == Reports.MarkRename && Renamer(p.Bytes) == 1 && !Encoding.UTF8.GetString(p.Bytes).Contains("PrivateRenamePersona")));
        Reports.ServerRename(3, Reader(Rename("opted out")));
        Reports.ServerRename(1, Reader(Rename("unknown key", origin: 7)));
        Reports.ServerRename(1, Reader(Rename("too soon")));
        Reports.Tick(); Check("rename rejects opted-out editor, unknown origin and rapid edits", Mp.Server.Packets.Count == 2);
        UnityEngine.Time.realtimeSinceStartup += 1; Mp.Server.BlockedTargets.Add(2);
        Reports.ServerRename(1, Reader(Rename("first edit")));
        UnityEngine.Time.realtimeSinceStartup += 1; Reports.ServerRename(1, Reader(Rename("second edit")));
        Pump(() => Mp.Server.Packets.Count(p => p.Target == 1) == 3);
        Check("busy target does not block other rename recipients", Mp.Server.Packets.Count(p => p.Target == 2) == 1);
        Mp.Server.BlockedTargets.Remove(2); Pump(() => Mp.Server.Packets.Count(p => p.Target == 2) == 3);
        Check("rename retry preserves order per recipient", Mp.Server.Packets.Where(p => p.Target == 2).Skip(1).Select(p => RenameNote(p.Bytes)).SequenceEqual(new[] { "first edit", "second edit" }));
        Mp.Server.Packets.Clear(); Reports.ServerRename(0, Reader(Rename("host edit")));
        Pump(() => Mp.Server.Packets.Count == 2); MarkFiles.Pending.GetAwaiter().GetResult();
        Check("host may rename another origin and relay authenticated host index", MarkFiles.Snapshot.Single().Note == "host edit" && Mp.Server.Packets.Count == 2 && Mp.Server.Packets.All(p => Renamer(p.Bytes) == 0));
        Start(true, false, Path.Combine(root, "rename-relay")); Consent(1, true); Consent(2, true);
        Reports.KnowMark(SessionLog.Sid, 100.125, 1);
        Reports.ServerRename(1, Reader(Rename("relay only"))); Pump(() => Mp.Server.Packets.Count == 2);
        MarkFiles.Pending.GetAwaiter().GetResult();
        Check("non-sharing host relays rename without writing files", Mp.Server.Packets.Count == 2 && !Directory.Exists(FeedbackReporter.Root));
        Start(false, true, Path.Combine(root, "rename-client")); Reports.KnowMark(SessionLog.Sid, 100.125, 1);
        Mp.Client.Blocked = true; Reports.RenameMark(SessionLog.Sid, 100.125, 1, "client edit"); Reports.Tick();
        Check("rename send retained for retry", Mp.Client.Packets.Count == 0);
        Mp.Client.Blocked = false; Reports.Tick();
        Check("rename retries reliable send", Mp.Client.Packets.Count == 1 && Mp.Client.Packets[0][0] == Reports.MarkRename);
        Mp.Client.Blocked = true; Reports.RenameMark(SessionLog.Sid, 100.125, 1, "cancelled"); Reports.Share = false; Mp.Client.Blocked = false; Reports.Tick();
        Check("opt-out cancels queued rename", Mp.Client.Packets.Count == 1);
        Reports.Share = true;
        Reports.ClientRename(Reader(RelayRename("received edit")));
        byte[] oldPayload = ReportTransfer.Compress(Encoding.UTF8.GetBytes(Box.Replace("\"momentSt\":100.125", "\"momentSt\":100.125,\"origin\":1,\"note\":\"old note\"")));
        var arrival = new NetWriter(); arrival.U32(9); arrival.U32((uint)oldPayload.Length); arrival.U32(0); arrival.U8(1); arrival.I32(1); arrival.Str("player-1");
        arrival.Str(ReportTransfer.MomentBase(SessionLog.Sid, 100.125)); arrival.Str("old note"); arrival.F64(100.125); arrival.Str(SessionLog.Sid); arrival.Str(ReportTransfer.Hash(oldPayload)); arrival.Bytes(oldPayload, 0, oldPayload.Length);
        Reports.ClientChunk(Reader(arrival)); Pump(() => Reports.FilesReceived == 1 || Reports.Refusals > 0);
        Check("client rename applies to a later validated received copy", Reports.FilesReceived == 1 && MarkFiles.Snapshot.Single().Note == "received edit" && File.ReadAllText(Reports.LastReceived).Contains("received edit"));
        foreach (var bad in new[] { RelayRename("foreign relay", 2), RelayRename("negative renamer", -1), Rename("missing renamer") }) Reports.ClientRename(Reader(bad));
        MarkFiles.Pending.GetAwaiter().GetResult();
        Check("client rejects foreign, negative and missing renamer without changing file", MarkFiles.Snapshot.Single().Note == "received edit" && File.ReadAllText(Reports.LastReceived).Contains("received edit"));
        Reports.ClientRename(Reader(RelayRename("host relay", 0))); MarkFiles.Pending.GetAwaiter().GetResult();
        Check("client accepts host rename of foreign origin", MarkFiles.Snapshot.Single().Note == "host relay");
        Reports.ClientRename(Reader(RelayRename("received edit"))); MarkFiles.Pending.GetAwaiter().GetResult();
        Reports.Share = false; Reports.ClientRename(Reader(RelayRename("ignored after opt-out"))); MarkFiles.Pending.GetAwaiter().GetResult();
        Check("client ignores rename after opt-out", MarkFiles.Snapshot.Single().Note == "received edit");
        Start(false, true, Path.Combine(root, "rename-before-upload"));
        string captureRoot = FeedbackReporter.Root;
        string oldPath = MarkFiles.Run(() =>
        {
            string file = Path.Combine(SessionLog.MarksFolder, MarkFiles.Name(SessionLog.MarksFolder, SessionLog.Sid, 100.125, "old note"));
            MarkFiles.Atomic(file, Box.Replace("\"momentSt\":100.125", "\"momentSt\":100.125,\"origin\":1,\"start\":\"2026-10-20\",\"note\":\"old note\""));
            return MarkFiles.Register(captureRoot, file);
        }).GetAwaiter().GetResult();
        var completion = Reports.MarkCompletion("ignored", "old note", 100.125, 1);
        MarkFiles.Rename(captureRoot, SessionLog.Sid, 100.125, 1, "edited before upload");
        completion(oldPath); Pump(() => Reports.FilesSent == 1 || Reports.Refusals > 0);
        Check("completion resolves capture renamed before worker reads it", Reports.FilesSent == 1 && Reports.Refusals == 0 && !File.Exists(oldPath));
    }
    private static NetWriter Chunk(byte[] data, uint id = 1, int offset = 0, int count = -1, bool mark = true, string note = null)
    {
        var w = new NetWriter(); w.U32(id); w.U32((uint)data.Length); w.U32((uint)offset); w.U8(mark ? (byte)1 : (byte)0);
        if (offset == 0)
        {
            w.Str(mark ? ReportTransfer.MomentBase("0123456789abcdef", 100.125) : "report-safe"); w.Str(note ?? (mark ? "wheelbug" : "")); w.F64(100.125); w.Str("0123456789abcdef"); w.Str(ReportTransfer.Hash(data));
        }
        w.Bytes(data, offset, count < 0 ? data.Length - offset : count); return w;
    }
    private static ZipArchiveEntry Entry(ZipArchive archive, string name)
    {
        var entry = archive.CreateEntry(name); entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero); return entry;
    }
    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "tld-share-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        byte[] payload = ReportTransfer.Compress(Encoding.UTF8.GetBytes(Box));
        Start(true, false, Path.Combine(root, "host-off"));
        Consent(1, true); Consent(2, true); Consent(3, false);
        Reports.ServerChunk(1, 1234, "Ylva Testperson", Reader(Chunk(payload)));
        Pump(() => Reports.FilesRelayed == 1);
        Check("non-sharing host relays to sharing teammate only", Mp.Server.Packets.Count == 1 && Mp.Server.Packets[0].Target == 2 && Reports.FilesReceived == 0 && !Directory.Exists(FeedbackReporter.Root));
        byte[] relayed = Mp.Server.Packets[0].Bytes;
        Check("relay metadata contains index only", !Encoding.UTF8.GetString(relayed).Contains("Ylva Testperson") && Encoding.UTF8.GetString(relayed).Contains("player-1"));
        Reports.ServerChunk(1, 1234, "Ylva Testperson", Reader(Chunk(payload, 2)));
        Check("host ignores same file twice", Reports.Duplicates == 1 && Mp.Server.Packets.Count == 1);
        Reports.ServerLeft(1); Consent(4, true);
        Reports.ServerChunk(4, 1234, "Ylva Testperson", Reader(Chunk(payload, 3)));
        Check("dedupe survives authenticated reconnect", Reports.Duplicates == 2);

        Start(false, true, Path.Combine(root, "client"));
        var incoming = new NetReader { Buf = relayed, Pos = 1, End = relayed.Length };
        Reports.ClientChunk(incoming);
        Pump(() => Reports.FilesReceived == 1);
        Check("sharing client saves original teammate JSON", Reports.FilesReceived == 1 && File.ReadAllText(Reports.LastReceived) == Box && Path.GetFileName(Reports.LastReceived) == TelemetryPrivacy.ReceivedName(1, ReportTransfer.Hash(payload), true, ReportTransfer.MomentName("0123456789abcdef", 100.125, "wheelbug")));
        Check("arrival notice uses player index and mark", Session.Notices.Any(n => n == "received player-1's black box for mark 'wheelbug'"));
        Reports.ClientChunk(new NetReader { Buf = relayed, Pos = 1, End = relayed.Length });
        Check("receiver dedupe", Reports.Duplicates == 1);
        Reports.Share = false;
        Reports.ClientChunk(new NetReader { Buf = relayed, Pos = 1, End = relayed.Length });
        Check("opt-out receiver refuses files", Reports.FilesReceived == 1 && Reports.Refusals == 1);

        string path = Path.Combine(root, "local.json"); File.WriteAllText(path, Box, new UTF8Encoding(false));
        Start(false, true, Path.Combine(root, "uploader"));
        Mp.Client.Blocked = true;
        Reports.MarkCompletion(ReportTransfer.MomentName("0123456789abcdef", 100.125, "wheelbug"), "wheelbug", 100.125)(path);
        Pump(() => Reports.Stats().Contains("\"queued\":1"));
        for (int i = 0; i < 10; i++) Reports.Tick();
        Check("busy reliable queue retains offset", Reports.FilesSent == 0 && Mp.Client.Packets.Count == 0);
        Mp.Client.Blocked = false; Pump(() => Reports.FilesSent == 1);
        Check("local client mark uploads after queue clears", Mp.Client.Packets.Count == 1 && Mp.Client.Packets[0][0] == Reports.ReportChunk && Reports.BytesSent == payload.Length);
        var oldCompletion = Reports.MarkCompletion(ReportTransfer.MomentName("0123456789abcdef", 100.125, "wheelbug"), "wheelbug", 100.125);
        Reports.Share = false; Reports.Share = true; oldCompletion(path); Reports.Tick();
        Check("off/on does not resurrect old capture", Reports.FilesSent == 1 && Reports.Stats().Contains("\"queued\":0"));

        Start(true, true, Path.Combine(root, "host-on")); Consent(1, true);
        Reports.MarkCompletion(ReportTransfer.MomentName("0123456789abcdef", 100.125, "wheelbug"), "wheelbug", 100.125)(path);
        Pump(() => Reports.FilesSent == 1);
        Check("sharing host sends its own mark", Mp.Server.Packets.Count == 1 && Mp.Server.Packets[0].Target == 1 && Reports.FilesRelayed == 0);
        Reports.ServerChunk(1, 1234, "Ylva Testperson", Reader(Chunk(payload)));
        Pump(() => Reports.FilesReceived == 1);
        Check("sharing host also saves teammate mark", Reports.FilesReceived == 1 && File.Exists(Reports.LastReceived));

        byte[] report;
        using (var memory = new MemoryStream())
        {
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                using (var entry = Entry(zip, "blackbox.json").Open()) entry.Write(Encoding.UTF8.GetBytes(Box));
                using (var entry = Entry(zip, "stats.json").Open()) entry.Write(Encoding.UTF8.GetBytes("{}"));
            }
            report = memory.ToArray();
        }
        Start(true, true, Path.Combine(root, "report-host")); Consent(1, true); Consent(2, true);
        Reports.ServerChunk(1, 1234, "Ylva Testperson", Reader(Chunk(report, mark: false)));
        Pump(() => Reports.FilesReceived == 1 && Reports.FilesRelayed == 1);
        Check("report zip saved in sender subfolder and relayed", Reports.FilesReceived == 1 && Reports.FilesRelayed == 1 && Path.GetFileName(Path.GetDirectoryName(Reports.LastReceived)) == "player-1" && File.ReadAllBytes(Reports.LastReceived).SequenceEqual(report));
        string zipPath = Path.Combine(root, "report.zip"); File.WriteAllBytes(zipPath, report);
        Start(false, true, Path.Combine(root, "report-uploader")); Reports.LocalReport("test"); Reports.OnZipped(zipPath);
        Pump(() => Reports.FilesSent == 1);
        Check("local report completion uploads zip", Reports.FilesSent == 1 && Reports.BytesSent == report.Length);

        Start(true, false, Path.Combine(root, "cancel")); Consent(1, true); Consent(2, true);
        Reports.ServerChunk(1, 1234, "Ylva Testperson", Reader(Chunk(payload)));
        Consent(1, false);
        for (int i = 0; i < 20; i++) { Reports.Tick(); Thread.Sleep(1); }
        Check("source opt-out cancels validation completion", Reports.FilesRelayed == 0 && Reports.FilesReceived == 0);

        var random = new Random(4);
        string rows = string.Join(",", Enumerable.Range(0, 15000).Select(_ => "[" + random.Next() + "]"));
        byte[] large = ReportTransfer.Compress(Encoding.UTF8.GetBytes(Box.Replace("[[100]]", "[" + rows + "]")));
        Start(true, false, Path.Combine(root, "chunked")); Consent(1, true); Consent(2, true);
        for (int offset = 0; offset < large.Length; offset += ReportTransfer.ChunkBytes)
        {
            UnityEngine.Time.realtimeSinceStartup += .12f;
            Reports.ServerChunk(1, 1234, "Ylva Testperson", Reader(Chunk(large, offset: offset, count: Math.Min(ReportTransfer.ChunkBytes, large.Length - offset))));
        }
        Pump(() => Reports.FilesRelayed == 1);
        Check("multi-chunk relay completes with monotonic offsets", Mp.Server.Packets.Count > 1 && Reports.FilesRelayed == 1 && Mp.Server.Packets.Select((p, i) => BitConverter.ToUInt32(p.Bytes, 9) == i * ReportTransfer.ChunkBytes).All(ok => ok));
        string unicodeNote = new string('\u4e2d', 500);
        byte[] unicodePayload = ReportTransfer.Compress(Encoding.UTF8.GetBytes(Box.Replace("\"momentSt\":100.125", "\"momentSt\":100.125,\"note\":" + Json.Str(unicodeNote)).Replace("[[100]]", "[" + rows + "]")));
        Start(true, true, Path.Combine(root, "unicode-note")); Consent(1, true);
        for (int offset = 0; offset < unicodePayload.Length; offset += ReportTransfer.ChunkBytes)
        {
            UnityEngine.Time.realtimeSinceStartup += .12f;
            Reports.ServerChunk(1, 1234, "Ylva Testperson", Reader(Chunk(unicodePayload, offset: offset, count: Math.Min(ReportTransfer.ChunkBytes, unicodePayload.Length - offset), note: unicodeNote)));
        }
        Pump(() => Reports.FilesReceived == 1 || Reports.Refusals > 0);
        Check("500-character UTF-8 note fits full chunk metadata and index", Reports.FilesReceived == 1 && Reports.Refusals == 0 && MarkFiles.Snapshot.Single().Note == unicodeNote);

        Start(true, false, Path.Combine(root, "disconnect")); Consent(1, true); Consent(2, true);
        Reports.ServerChunk(1, 1234, "Ylva Testperson", Reader(Chunk(payload, count: payload.Length / 2)));
        Reports.ServerLeft(1);
        Reports.ServerChunk(1, 1234, "Ylva Testperson", Reader(Chunk(payload, offset: payload.Length / 2)));
        for (int i = 0; i < 10; i++) Reports.Tick();
        Check("leaving drops partial input cleanly", Reports.FilesRelayed == 0 && Reports.FilesReceived == 0 && Mp.Server.Packets.Count == 0);
        Consent(5, false); Reports.ServerChunk(5, 5678, "off", Reader(Chunk(payload)));
        Check("non-sharing source refused", Reports.Refusals >= 1 && Reports.FilesRelayed == 0);
        string unsafeBox = Box.Replace("\"rows\":[[100]]", "\"rows\":[[100]],\"events\":{\"rows\":[{\"result\":\"Ylva Testperson\"}]}");
        byte[] unsafePayload = ReportTransfer.Compress(Encoding.UTF8.GetBytes(unsafeBox));
        Start(true, true, Path.Combine(root, "validation-refusal")); Consent(1, true);
        Reports.ServerChunk(1, 1234, "Ylva Testperson", Reader(Chunk(unsafePayload)));
        Pump(() => Reports.Refusals == 1);
        Check("receiver exposes validation refusal without private literal", Reports.FilesReceived == 0 && Reports.Stats().Contains("\"validate mark: unapproved JSON string\":1") && !Reports.LastDrop.Contains("Ylva"));
        File.WriteAllText(path, unsafeBox, new UTF8Encoding(false));
        Start(false, true, Path.Combine(root, "preparation-refusal"));
        Reports.MarkCompletion(ReportTransfer.MomentName("0123456789abcdef", 100.125, ""), "", 100.125)(path);
        Pump(() => Reports.Refusals == 1);
        Check("sender exposes preparation refusal before sending", Reports.FilesSent == 0 && Reports.Stats().Contains("\"prepare mark: unapproved JSON string\":1"));

        string futureBox = Box.TrimEnd('}') + ",\"futureCounter\":17,\"futureTree\":{\"samples\":[true,null,3]},\"futureText\":\"Ylva Testperson\",\"unsafe key\":1}";
        byte[] futurePayload = ReportTransfer.Compress(Encoding.UTF8.GetBytes(futureBox));
        Start(true, true, Path.Combine(root, "scrub-receiver")); Consent(1, true); Consent(2, true);
        Plugin.Log.Lines.Clear();
        Reports.ServerChunk(1, 1234, "Ylva Testperson", Reader(Chunk(futurePayload)));
        Pump(() => Reports.FilesReceived == 1 && Reports.FilesRelayed == 1);
        Check("receiver scrubs and relays additive schema", Reports.FilesReceived == 1 && Reports.FilesRelayed == 1 && Reports.Refusals == 0);
        string saved = File.ReadAllText(Reports.LastReceived);
        Check("receiver saved numeric fields and removed private string/key", saved.Contains("\"futureCounter\":17") && saved.Contains("[removed]") && !saved.Contains("Ylva") && !saved.Contains("unsafe key"));
        Check("receiver exposes per-file and total scrub counts", Reports.FieldsScrubbed == 1 && Reports.KeysDropped == 1 && Reports.LastFieldsScrubbed == 1 && Reports.LastKeysDropped == 1 && Reports.Stats().Contains("\"scrubbedFiles\":1"));
        Check("one notice includes safe key path only", Plugin.Log.Lines.Count == 1 && Plugin.Log.Lines[0].Contains("$.futureText") && !Plugin.Log.Lines[0].Contains("Ylva"));
        relayed = Mp.Server.Packets[0].Bytes;
        Reports.ServerChunk(1, 1234, "Ylva Testperson", Reader(Chunk(futurePayload, 2)));
        Check("receiver remembers original hash after scrubbing", Reports.Duplicates == 1);
        UnityEngine.Time.realtimeSinceStartup += 11;
        Reports.ServerChunk(1, 1234, "Ylva Testperson", Reader(Chunk(ReportTransfer.Compress(Encoding.UTF8.GetBytes(futureBox.Replace("Ylva Testperson", "Another private value"))), 3)));
        Pump(() => Reports.Duplicates == 2);
        Check("different private strings deduplicate after scrubbing", Reports.Duplicates == 2 && Reports.FilesReceived == 1 && Reports.Refusals == 0);
        UnityEngine.Time.realtimeSinceStartup += 11;
        Reports.ServerChunk(1, 1234, "Ylva Testperson", Reader(Chunk(ReportTransfer.Compress(Encoding.UTF8.GetBytes(futureBox.Replace(":17", ":18"))), 3)));
        Pump(() => Reports.FilesReceived == 2);
        Check("unknown scrub notice only once per session", Plugin.Log.Lines.Count == 1 && Reports.FieldsScrubbed == 3);
        Start(false, true, Path.Combine(root, "scrub-relayed"));
        Reports.ClientChunk(new NetReader { Buf = relayed, Pos = 1, End = relayed.Length });
        Pump(() => Reports.FilesReceived == 1);
        Check("relay hash covers sanitized bytes and receiver is idempotent", Reports.FilesReceived == 1 && Reports.Refusals == 0 && File.ReadAllText(Reports.LastReceived) == saved && Reports.FieldsScrubbed == 0);

        File.WriteAllText(path, futureBox, new UTF8Encoding(false));
        Start(false, true, Path.Combine(root, "scrub-uploader"));
        Reports.MarkCompletion(ReportTransfer.MomentName("0123456789abcdef", 100.125, ""), "", 100.125)(path);
        Pump(() => Reports.FilesSent == 1);
        Check("sender scrubs rather than refusing unknown text", Reports.FilesSent == 1 && Reports.Refusals == 0 && Reports.FieldsScrubbed == 1 && Reports.KeysDropped == 1);
        byte[] uploaded = Mp.Client.Packets[0];
        Start(true, true, Path.Combine(root, "scrub-uploaded")); Consent(1, true);
        Reports.ServerChunk(1, 1234, "Ylva Testperson", new NetReader { Buf = uploaded, Pos = 1, End = uploaded.Length });
        Pump(() => Reports.FilesReceived == 1);
        Check("prepared sanitized payload passes receipt and save", Reports.FilesReceived == 1 && Reports.Refusals == 0 && File.ReadAllText(Reports.LastReceived) == saved);

        using (var memory = new MemoryStream())
        {
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                using (var entry = Entry(zip, "blackbox.json").Open()) entry.Write(Encoding.UTF8.GetBytes(futureBox));
                using (var entry = Entry(zip, "stats.json").Open()) entry.Write(Encoding.UTF8.GetBytes("{\"futureText\":\"Ylva Testperson\"}"));
            }
            report = memory.ToArray();
        }
        Start(true, true, Path.Combine(root, "scrub-report")); Consent(1, true); Consent(2, true);
        Reports.ServerChunk(1, 1234, "Ylva Testperson", Reader(Chunk(report, mark: false)));
        Pump(() => Reports.FilesReceived == 1 && Reports.FilesRelayed == 1);
        using (var zip = ZipFile.OpenRead(Reports.LastReceived))
        {
            using var boxReader = new StreamReader(zip.GetEntry("blackbox.json").Open());
            using var statsReader = new StreamReader(zip.GetEntry("stats.json").Open());
            Check("receiver saves scrubbed report attachments", boxReader.ReadToEnd() == saved && !statsReader.ReadToEnd().Contains("Ylva") && Reports.FieldsScrubbed == 2 && Reports.KeysDropped == 1);
        }
        relayed = Mp.Server.Packets[0].Bytes;
        Start(false, true, Path.Combine(root, "scrub-report-relayed"));
        Reports.ClientChunk(new NetReader { Buf = relayed, Pos = 1, End = relayed.Length });
        Pump(() => Reports.FilesReceived == 1);
        Check("scrubbed report relay has valid archive and hash", Reports.FilesReceived == 1 && Reports.Refusals == 0 && Reports.FieldsScrubbed == 0);
        File.WriteAllBytes(zipPath, report);
        Start(false, true, Path.Combine(root, "scrub-report-uploader")); Reports.LocalReport("test"); Reports.OnZipped(zipPath);
        Pump(() => Reports.FilesSent == 1);
        Check("sender prepares scrubbed report archive", Reports.FilesSent == 1 && Reports.Refusals == 0 && Reports.FieldsScrubbed == 2 && Reports.KeysDropped == 1);
        Mp.Server = null; Mp.Client = null; Reports.Reset();
        RenameTests(root);
        MarkFiles.Pending.GetAwaiter().GetResult();
        // root is a newly created test-owned temporary directory, never supplied by the caller.
        Directory.Delete(root, true);
        Console.WriteLine("Failures: " + _fails); return _fails == 0 ? 0 : 1;
    }
}

namespace UnityEngine
{
    static class Time { internal static float realtimeSinceStartup { get; set; } internal static float unscaledDeltaTime => .02f; }
    static class Mathf { internal static float Min(float a, float b) => Math.Min(a, b); }
}
namespace TLDRevamp
{
    static class SessionLog { internal static string Sid => "0123456789abcdef"; internal static double St => UnityEngine.Time.realtimeSinceStartup; internal static string MarksFolder => MarkFiles.Folder(FeedbackReporter.Root, TelemetryPrivacy.UtcDate(), Sid); }
    sealed class FeedbackReporter
    {
        internal static string Root { get; set; } = "";
        internal bool Pending => false;
        internal string Capture(string note, double moment) => "report";
    }
    sealed class Plugin
    {
        internal static Plugin Instance { get; } = new Plugin();
        internal static Logger Log { get; } = new Logger();
        internal FeedbackReporter Feedback { get; } = new FeedbackReporter();
    }
    sealed class Logger
    {
        internal readonly List<string> Lines = new List<string>();
        internal void LogInfo(string line) => Lines.Add(line);
    }
    static class Json { internal static string Str(string s) => System.Text.Json.JsonSerializer.Serialize(s); }
}
namespace TLDRevamp.Net
{
    static class Mp { internal static MpClient Client { get; set; } internal static MpServer Server { get; set; } }
    static class Session
    {
        internal static readonly List<string> Notices = new List<string>();
        internal static string MyName() => "tester";
        internal static string CleanName(string s) => s;
        internal static void Show(int kind, string who, string text) => Notices.Add(text);
    }
    static class TelemetryMarks { internal static string Note(string note) => note; }
    sealed class MpServer
    {
        internal readonly List<(int Target, byte[] Bytes)> Packets = new List<(int, byte[])>();
        internal readonly HashSet<int> BlockedTargets = new HashSet<int>();
        internal bool SendReport(int target, NetWriter w) { if (BlockedTargets.Contains(target)) return false; Packets.Add((target, w.Buf.Take(w.Len).ToArray())); return true; }
        internal void SendToAll(NetWriter w, bool reliable, int except, object near, double range) { }
        internal int ReadyCount() => 3;
    }
    sealed class MpClient
    {
        internal int MyId => 2;
        internal bool Ready => true;
        internal bool Blocked { get; set; }
        internal readonly List<byte[]> Packets = new List<byte[]>();
        internal bool SendReport(NetWriter w) { if (Blocked) return false; Packets.Add(w.Buf.Take(w.Len).ToArray()); return true; }
        internal bool SendReportState(NetWriter w) => true;
    }
}

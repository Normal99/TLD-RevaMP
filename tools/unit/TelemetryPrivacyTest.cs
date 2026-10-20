// Real recorder/report writers; only Unity, bridge and network observation surfaces are fakes.
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using TLDRevamp;
using TLDRevamp.Net;

static class TelemetryPrivacyTest
{
    internal static readonly string[] Secrets = {
        "Ylva Testperson", "fixture-user", "fixture-machine", new string('7', 17),
        "203.0.113.9:27070", "[2001:db8::9]:27070", @"C:\" + @"Users\fixture-user\song.ogg",
        "/" + "home/fixture-user/song.ogg", "fixture" + "@example.invalid", "2026-10-19T18:32:45+02:00",
        @"\\fixture-server\music\song.ogg", "~/secret-song.ogg", "%USERPROFILE%/secret-song.ogg",
        "https://example.invalid/game?token=private", "3987654321", "3987654322", "Unregistered chat or custom song title"
    };
    internal static string Private => string.Join(" ", Secrets.Take(Secrets.Length - 1));
    internal static string Reply => "{\"frames\":12,\"fps\":60,\"port\":27070,\"steamId\":" + Secrets[3] +
        ",\"accountId\":3987654321,\"lobbyId\":3987654322,\"name\":" + JsonSerializer.Serialize(Private) +
        ",\"chat\":" + JsonSerializer.Serialize(Secrets.Last()) + ",\"sent\":3,\"applied\":2,\"count\":1," +
        "\"entities\":[{\"net\":4,\"owner\":1,\"name\":" + JsonSerializer.Serialize(Private) + ",\"pos\":[1,2,3]}]," +
        "\"server\":{\"port\":27070,\"peers\":1},\"conns\":[{\"ping\":25,\"ip\":" + JsonSerializer.Serialize(Private) + "}]}";
    private static int _checks;
    private static void Check(bool ok, string message) { _checks++; if (!ok) throw new Exception(message); }
    private static void Scan(string name, byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        foreach (string secret in Secrets.Concat(new[] { Environment.UserName, Environment.MachineName }))
            if (!string.IsNullOrEmpty(secret)) Check(!text.Contains(secret, StringComparison.OrdinalIgnoreCase) &&
                !text.Contains(JsonSerializer.Serialize(secret).Trim('"'), StringComparison.OrdinalIgnoreCase), name + " leaked fixture");
        Check(!bytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), name + " has BOM");
        if (!name.EndsWith(".zip")) Check(!text.Contains("\r"), name + " has CRLF");
    }
    private static void Call(string method, params object[] args) => typeof(SessionLog).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
    static int Main(string[] args)
    {
        BepInEx.Paths.BepInExRootPath = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "telprivacy-" + Guid.NewGuid().ToString("N")));
        TelemetryPrivacy.Name(Secrets[0]); TelemetryPrivacy.Name(Secrets[1]); TelemetryPrivacy.Name(Secrets[2]);
        TelemetryPrivacy.Identity(3987654321); TelemetryPrivacy.Identity(3987654322);
        foreach (string token in new[] { "overlay", "player", "host", "Net" }) TelemetryPrivacy.Name(token);
        foreach (string secret in Secrets.Take(Secrets.Length - 1)) Check(!TelemetryPrivacy.Clean(secret).Contains(secret), "cleaner fixture");
        Check(TelemetryPrivacy.Clean(new string('x', 20000)) == TelemetryPrivacy.Removed, "oversize fails closed");
        Check(TelemetryPrivacy.Os("Windows 10 (build 19045) " + Private) == "Windows 10", "OS is family/major only");
        Check(TelemetryPrivacy.Clean(TelemetryPrivacy.Clean(Private)) == TelemetryPrivacy.Clean(Private), "cleaning is idempotent");
        Check(TelemetryPrivacy.Numbers("{bad", "frames") == "null", "malformed bridge fails closed");
        TelemetryContent.Refresh();
        Check(TelemetryPrivacy.ObjectName("Car06(Clone)") == TelemetryPrivacy.Removed, "uninitialized vocabulary fails closed");
        TelemetryPrivacyFakes.Database.s = new TelemetryPrivacyFakes.Database {
            items = new[] { new UnityEngine.GameObject {
                Children = new[] { "Car06Full", "TÁVCSO", "Ignition", "WheelSlot", "DriverSeat", Secrets[0] }.Select(n => new UnityEngine.Transform { name = n }).ToArray(),
                Slots = new[] { new TelemetryPrivacyFakes.PartSlot { tipus = new[] { "Wheel" } } }
            } },
            pois = new[] { new UnityEngine.GameObject { Children = new[] { new UnityEngine.Transform { name = "BuildingDoor" } } } }
        };
        TelemetryContent.Refresh(); TelemetryContent.Refresh();
        foreach (string value in new[] { "Car06", "Car06(Clone)", "Car06Full", "Car06Full(Clone)", "Car06 (12)(Clone)" })
            Check(TelemetryState.Text(value) == "Car06", "prefab suffixes normalize consistently");
        foreach (string value in new[] { "TÁVCSO", "Ignition", "WheelSlot", "Wheel", "DriverSeat", "BuildingDoor", "bus door" })
            Check(TelemetryState.Text(value) == value && TelemetryPrivacy.ShareString("name", value), "trusted hierarchy/fixed label retained and shareable");
        foreach (string value in Secrets.Concat(new[] { Secrets[0] + "(Clone)", "Car06 " + Secrets[0], "Car06Full private", "car06" }))
            Check(TelemetryState.Text(value) == TelemetryPrivacy.Removed, "renamed/untrusted object removed");
        TelemetryPrivacy.ContentName("LaterPrivatePrefab");
        var pending = new TelemetryState(); pending.Add(new StateEntity { Name = "LaterPrivatePrefab" });
        TelemetryPrivacy.Name("LaterPrivatePrefab");
        using (var writer = new StringWriter()) { pending.Write(writer); Check(!writer.ToString().Contains("LaterPrivatePrefab"), "late identity blocks even an allowlisted prefab before writing"); }
        Check(!TelemetryPrivacy.ShareString("name", Secrets.Last()) && !TelemetryPrivacy.ShareString("content", Secrets.Last()), "share validator rejects unknown content");
        var database = TelemetryPrivacyFakes.Database.s;
        var prefabs = new UnityEngine.GameObject[77]; prefabs[76] = new UnityEngine.GameObject { name = "BBGunAmmo" };
        TelemetryPrivacyFakes.Database.s = new TelemetryPrivacyFakes.Database { items = prefabs };
        TelemetryContent.Refresh();
        Check(TelemetryContent.ItemName(76, "LegPuskaTolteny") == "BBGunAmmo" && TelemetryContent.ItemName(76, "BBGunAmmo(Clone)") == "BBGunAmmo", "same prefab uses table name across machines");
        var carried = new TelemetryState();
        carried.Add(new StateCarry { Kind = 0 }); carried.Add(new StateCarry { Kind = 1 }); carried.Add(new StateCarry { Kind = 2 });
        Check(carried.Carried[0].Kind == 0 && carried.Carried[1].Kind == 0 && carried.Carried[2].Kind == 2, "equipped and grabbed holds have one observable kind; inventory stays separate");
        TelemetryPrivacyFakes.Database.s = database; TelemetryContent.Refresh();
        string note = "braking " + Secrets[0] + " " + Secrets[3] + " before " + Secrets[6];
        string expectedNote = "braking [removed] [removed] before [removed]";
        string padding = new string('x', 499 - expectedNote.Length) + " ";
        note = padding + note; expectedNote = padding + expectedNote;
        Check(TelemetryPrivacy.Note(note) == expectedNote, "note keeps prose and removes seeded name/id/path");
        Check(!TelemetryPrivacy.ShareString("note", note), "sharing rejects an unclean note");
        Check(TelemetryPrivacy.Note(new string('x', 600)).Length == 500 && !TelemetryPrivacy.ShareString("note", new string('x', 501)), "note length bounded at export and sharing");
        string[] commands = { "stats", "mp status", "net stats", "mp entities", "mp aistats", "mp combat", "mp shotfx", "mp blasts", "mp membergone", "mp poiusables" };
        foreach (string command in commands)
        {
            string projected = TelemetryPrivacy.Bridge(command, Reply); Scan(command, Encoding.UTF8.GetBytes(projected));
            Check(!projected.Contains("27070") && !projected.Contains("3987654321") && !projected.Contains("3987654322"), "numeric identities/ports dropped");
        }
        string[] leaseReasons = { "malformed", "requesterNotReady", "buildingKey", "buildingReach", "buildingType", "buildingTypeMismatch", "doneMalformed", "doneHolder" };
        string leaseCounts = "{\"leaseRefusedWhy\":{" + string.Join(",", leaseReasons.Select(reason => "\"" + reason + "\":3")) + "}}";
        Check(TelemetryPrivacy.Bridge("mp entities", leaseCounts) == leaseCounts, "lease refusal counts survive telemetry projection");
        foreach (string reason in leaseReasons.Append("leaseRefusedWhy")) Check(TelemetryPrivacy.ShareKey(reason), "lease refusal schema is shareable");
        foreach (string kind in new[] { "tpa-request-sent", "tpa-accept-sent", "tpa-refuse-sent", "part-off-refused", "lease-refused", "lease-done-refused" }) Check(TelemetryPrivacy.EventKind(kind) == kind, "command/refusal kinds retained");
        foreach (string result in new[] { "physics servo", "far store", "held", "usable value", "remote shot",
            "malformed", "missing car", "wrong owner", "epoch", "non-finite pose", "pose too far", "invalid rotation", "item index",
            "already detached or gone", "already detached", "item record", "not attached to a slot", "prefab slot", "slot parent", "not attached",
            "requester not ready", "building key", "building reach", "building type", "building type mismatch", "missing lease or wrong holder" })
            Check(TelemetryPrivacy.EventResult(result) == result && TelemetryPrivacy.ShareString("result", result), "literal diagnostic results retained");
        SessionLog.Sid = "abcdefabcdefabcd";
        SessionLog.Install(); TLDRevamp.Diagnostics.ErrorLog.Install();
        var telemetry = new Telemetry();
        UnityEngine.Time.realtimeSinceStartupAsDouble = 100;
        SessionLog.Frame(16, telemetry.Breakdown);
        SessionLog.Event("speed-jump", 4, from: 1, result: Private, value: 30);
        SessionLog.Event(Private, 4, result: Secrets.Last());
        SessionLog.Mark(note, Secrets[0], SessionLog.St);
#if !REPORT_SHARING
        Check(Reports.Note == expectedNote, "shared file metadata keeps cleaned note");
#endif
        // Seed even unregistered names/chat into every state label and warning; no heuristic may retain them.
        UnityEngine.Application.Log(Private, Secrets.Last(), UnityEngine.LogType.Warning);
        UnityEngine.Application.Log(Secrets.Last(), Private, UnityEngine.LogType.Exception);
        SessionLog.Save(5, true, 12, SessionLog.CurrentPath, 100, 0);
        Call("BeginLoad", "load");
        SessionLog.WorldEntered(); Call("Open", 100d);
        typeof(SessionLog).GetField("_usable", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 1d);
        Call("WriteLoad", "firstUsable");
        UnityEngine.Time.realtimeSinceStartupAsDouble = 106;
        SessionLog.Frame(600, telemetry.Breakdown);
        SessionLog.Event("tpa-failure", from: 1, result: Private);
        SessionLog.History.Trace.SetFocus(new uint[] { 4 }, 106, 106);
        SessionLog.History.Trace.Add(new MotionRow { Net = 4, Local = 106, St = 106, Kind = 2 });
        var interval = new TrafficInterval { Local = 106, St = 106, Seconds = 5 };
        interval.Types[10].Sent = 1;
        interval.Census[3] = new EntityCensus { Alive = 3, Limp = 1, Dead = 2, Receivers = 4 };
        interval.CategoriesCount[3] = new TrafficCount { Sent = 1, OutBytes = 243 }; interval.Details[3].Add(0, 243, true);
        SessionLog.History.Add(interval);
        var report = new FeedbackReporter(telemetry);
        string directory = report.Capture(note, 106);
        Check(directory != null, "report capture");
        foreach (string excluded in new[] { "screenshot.png", "LogOutput.log", "Player.log", "BepInEx.cfg" })
            File.WriteAllText(Path.Combine(directory, excluded), Private);
        UnityEngine.Time.realtimeSinceStartupAsDouble = 112;
        typeof(SessionLog).GetField("_warnAt", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 0d);
        SessionLog.Frame(20, telemetry.Breakdown);
        Call("RecordFrame", 112d, 20f, telemetry.Breakdown, default(TelemetryHooks.Events), new TrafficCount[256]);
        // Complete detached black boxes and long-frame writes, then the real report zipper.
        for (int i = 0; i < 5000 && report.Pending; i++)
        { SessionLog.Frame(16, telemetry.Breakdown); report.Tick(); Thread.Sleep(1); }
        SessionLog.Shutdown();
        Check(!report.Pending && File.Exists(FeedbackReporter.LastZip), "report completed");
        Check(ReportTransfer.Validate(File.ReadAllBytes(FeedbackReporter.LastZip), false, SessionLog.Sid, 106) == null, "report passes share privacy validation");
        var kinds = Directory.GetFiles(SessionLog.Dir, "*.jsonl").SelectMany(f => File.ReadLines(f)).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("k").GetString()).ToHashSet();
        foreach (string kind in new[] { "head", "tick", "hitch", "warn", "mark", "save", "load" }) Check(kinds.Contains(kind), "missing play line " + kind);
        var marks = Directory.GetFiles(SessionLog.Dir, "*.jsonl").SelectMany(File.ReadLines).Select(l => JsonDocument.Parse(l).RootElement).Where(l => l.GetProperty("k").GetString() == "mark").ToArray();
        Check(marks.Length > 0 && marks.All(m => m.GetProperty("note").GetString() == expectedNote), "play log keeps cleaned mark note");
        var boxes = Directory.GetFiles(FeedbackReporter.Root, "*.json", SearchOption.AllDirectories);
        Check(boxes.Length > 0, "standalone black box");
        foreach (string path in boxes.Where(p => Path.GetFileName(p) != "stats.json" && File.ReadAllText(p).Contains("\"head\"")))
        {
            using var box = JsonDocument.Parse(File.ReadAllText(path));
            foreach (string section in new[] { "head", "cols", "rows", "state", "events", "trace", "long", "network" }) Check(box.RootElement.TryGetProperty(section, out _), "missing section " + section);
            Check(box.RootElement.GetProperty("head").GetProperty("note").GetString() == expectedNote, "standalone/report black box keeps cleaned note");
            byte[] json = File.ReadAllBytes(path);
            Check(ReportTransfer.Validate(ReportTransfer.Compress(json), true, SessionLog.Sid, box.RootElement.GetProperty("head").GetProperty("momentSt").GetDouble()).SequenceEqual(json), "named black box passes share validation");
        }
        using (var zip = ZipFile.OpenRead(FeedbackReporter.LastZip))
        {
            foreach (var entry in zip.Entries)
            {
                Check(TelemetryPrivacy.Attachment(entry.FullName), "unexpected attachment");
                Check(entry.LastWriteTime.Year == 1980, "zip timestamp");
                using var source = entry.Open(); using var bytes = new MemoryStream(); source.CopyTo(bytes);
                Scan(entry.FullName, bytes.ToArray()); Scan("entry name", Encoding.UTF8.GetBytes(entry.FullName));
                if (entry.FullName == "stats.json")
                {
                    using var stats = JsonDocument.Parse(bytes.ToArray());
                    Check(stats.RootElement.GetProperty("note").GetString() == expectedNote, "report metadata keeps cleaned note");
                }
                if (entry.FullName == "blackbox.json")
                {
                    using var box = JsonDocument.Parse(bytes.ToArray()); var root = box.RootElement;
                    Check(root.GetProperty("head").GetProperty("note").GetString() == expectedNote, "zipped black box keeps cleaned note");
                    foreach (string section in new[] { "events", "trace", "long", "network" }) Check(root.GetProperty(section).GetProperty("rows").GetArrayLength() > 0, "empty fixture " + section);
                    foreach (string section in new[] { "players", "entities", "slots", "seats", "usables", "storms", "stations" }) Check(root.GetProperty("state").GetProperty(section).GetArrayLength() > 0, "empty state fixture " + section);
                    var category = root.GetProperty("network").GetProperty("rows")[0].GetProperty("categories")[3];
                    Check(category.GetProperty("alive").GetInt32() == 3 && category.GetProperty("restPoseSent").GetInt32() == 1 && category.GetProperty("avgOutBytes").GetDouble() == 243, "numeric census and traffic measurements survive report export gate");
                    var state = root.GetProperty("state");
                    Check(state.GetProperty("entities")[1].GetProperty("name").GetString() == "Car06", "entity name retained");
                    Check(state.GetProperty("entities")[2].GetProperty("name").GetString() == TelemetryPrivacy.Removed, "renamed prefab removed");
                    Check(state.GetProperty("players")[0].GetProperty("held")[1].GetProperty("name").GetString() == "TÁVCSO", "held prefab name retained");
                    Check(state.GetProperty("usables")[1].GetProperty("name").GetString() == "Ignition", "usable hierarchy name retained");
                    Check(state.GetProperty("slots")[1].GetProperty("type").GetString() == "Wheel" && state.GetProperty("slots")[1].GetProperty("content").GetString() == "Car06", "slot type/content retained");
                    Check(state.GetProperty("seats")[1].GetProperty("name").GetString() == "DriverSeat", "seat hierarchy name retained");
                }
            }
            foreach (string entry in new[] { "stats.json", "blackbox.json", "errors.json", "breakdown.json", "fixes.json", "tldrevamp-settings.txt" }) Check(zip.GetEntry(entry) != null, "missing attachment " + entry);
        }
        foreach (string file in Directory.GetFiles(FeedbackReporter.Root, "*", SearchOption.AllDirectories))
        { Scan("filename", Encoding.UTF8.GetBytes(Path.GetRelativePath(FeedbackReporter.Root, file))); Scan(file, File.ReadAllBytes(file)); }
        string hash = ReportTransfer.Hash(Encoding.UTF8.GetBytes(Private));
        foreach (bool mark in new[] { true, false }) Scan("received filename", Encoding.UTF8.GetBytes(TelemetryPrivacy.ReceivedName(3, hash, mark, ReportTransfer.MomentName(SessionLog.Sid, 106, ""))));
        Check(ReportTransfer.MomentName(SessionLog.Sid, 106, note).EndsWith("braking-removed-removed-before-removed"), "mark filename retains cleaned prose");
        Check(!ReportTransfer.MomentName(SessionLog.Sid, 106, Private).Contains("Ylva"), "mark filename omits private note data");
        TelemetryPrivacy.Name("player"); TelemetryPrivacy.Name("Net");
        Check(TelemetryPrivacy.Str(TelemetryPrivacy.Player(3)) == Json.Str("player-3") && TelemetryPrivacy.CodeStr("Net.Mp.Tick") == Json.Str("Net.Mp.Tick"), "constructed indices and compiled symbols are not nicknames");
        Console.WriteLine("PASS telemetry output privacy gate: " + _checks + " assertions; " + FeedbackReporter.Root);
        return 0;
    }
}

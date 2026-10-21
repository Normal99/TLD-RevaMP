using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TLDRevamp;
using TLDRevamp.Net;

static class MarkFilesTest
{
    private static int _fails;
    private const string Sid = "0123456789abcdef", Date = "2026-10-20", Secret = "PrivateFixturePersona";
    private static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) _fails++; }
    private static string Box(string note, int origin = 2, int fuel = 5)
    {
        var target = new MarkFocus.Target { Net = 4, Prefab = 12, Name = "Car06Full(Clone)", Distance = 2.5 };
        var focus = new MarkFocus { Crosshair = target, Car = target, Seat = 1, Held = target, Dragging = target, ClosestPlayer = 3, PlayerDistance = 6.25 };
        return "{\"head\":{\"privacy\":1,\"format\":3,\"start\":\"2026-10-19\",\"date\":\"" + Date + "\",\"sid\":\"" + Sid + "\",\"role\":\"host\",\"momentSt\":100.125,\"origin\":" + origin + ",\"note\":" + Json.Str(TelemetryPrivacy.Note(note)) + ",\"markedNets\":[279],\"focus\":" + focus.Json() + "},\"cols\":[\"st\"],\"rows\":[[100]],\"state\":{\"entities\":[{\"net\":4,\"fuel\":" + fuel + "},{\"net\":279,\"fuel\":" + fuel + "}]},\"trace\":{\"focus\":[279],\"rows\":[[1,2,3]]}}";
    }
    private static async Task<string> Save(string root, string folder, string note, bool received = false, int origin = 2, int fuel = 5)
    {
        return await MarkFiles.Run(() =>
        {
            string file = Path.Combine(folder, MarkFiles.Name(folder, Sid, 100.125, note, received ? 3 : -1, new string('a', 64)));
            MarkFiles.Atomic(file, Box(note, origin, fuel));
            return MarkFiles.Register(root, file, received ? 3 : -1, received);
        });
    }
    static async Task<int> Main()
    {
        TelemetryPrivacy.Name(Secret); TelemetryPrivacy.ContentName("Car06");
        string root = Path.Combine(Path.GetTempPath(), "marks-" + Guid.NewGuid().ToString("N"));
        string folder = MarkFiles.Folder(root, "2026-10-19", Sid);
        string note = new string('x', 250) + " " + new string('y', 249);
        Check("500 characters retained", TelemetryPrivacy.Note(note) == note);
        string largeBox = Box(note).Replace("[[100]]", "[" + string.Join(",", Enumerable.Repeat("[100,16,0]", 20000)) + "]");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var projected = MarkJson.Object(MarkJson.Box(largeBox, true));
        Check("index/comparison skips frame object graphs", !projected.ContainsKey("rows") && projected.ContainsKey("head") && projected.ContainsKey("state") && GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
        string tail = largeBox.Substring(largeBox.IndexOf(",\"cols\"", StringComparison.Ordinal));
        Check("rename preserves raw frames and additive payload", MarkJson.RenameNote(largeBox, "new note").EndsWith(tail, StringComparison.Ordinal));
        Check("clean before truncation", !TelemetryPrivacy.Note(new string('a', 490) + Secret).Contains(Secret));
        Check("surrogate boundary intact", !char.IsHighSurrogate(TelemetryPrivacy.Note(new string('a', 499) + "\U0001f697").Last()));
        for (int n = 0; n < 70; n += 7)
        {
            string deep = Path.Combine(folder, new string('d', n + 1));
            try
            {
                foreach (int source in new[] { -1, 123456 })
                {
                    string name = MarkFiles.Name(deep, Sid, 123456.125, note, source, new string('a', 64));
                    Check("actual folder path budget " + n + "/" + source, Path.Combine(deep, name + ".tmp").Length < 240 && name.Length < 255 && name.Contains("xxxx"));
                }
            }
            catch (PathTooLongException) { Check("overlong root rejected " + n, Path.Combine(deep, ReportTransfer.MomentBase(Sid, 123456.125) + "-unknown-from-p123456-aaaaaaaa.json.tmp").Length >= 228); }
        }
        string local = await Save(root, folder, note);
        string received = await Save(root, folder, note, true, fuel: 8);
        Check("one session folder for local and received", Path.GetDirectoryName(local) == folder && Path.GetDirectoryName(received) == folder);
        using (var box = JsonDocument.Parse(File.ReadAllText(local)))
        {
            var head = box.RootElement.GetProperty("head"); var focus = head.GetProperty("focus");
            Check("head retains full note", head.GetProperty("note").GetString() == note);
            Check("focus serialization", focus.GetProperty("crosshair").GetProperty("net").GetUInt32() == 4 && focus.GetProperty("car").GetProperty("prefab").GetInt32() == 12 && focus.GetProperty("seat").GetInt32() == 1 && focus.GetProperty("closestPlayer").GetProperty("id").GetInt32() == 3 && focus.GetProperty("dragging").GetProperty("name").GetString() == "Car06");
        }
        using (var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "marks", "index.json"))))
        {
            var rows = index.RootElement.EnumerateArray().ToArray();
            Check("index full metadata and note", rows.Length == 2 && rows.All(e => e.GetProperty("note").GetString() == note && e.GetProperty("date").GetString() == Date && e.GetProperty("sid").GetString() == Sid && e.GetProperty("st").GetDouble() == 100.125 && e.GetProperty("role").GetString() == "host" && File.Exists(Path.Combine(root, "marks", e.GetProperty("file").GetString()))));
            Check("received attribution", rows.Count(e => e.GetProperty("received").GetBoolean()) == 1);
        }
        Check("readable index", File.ReadAllText(Path.Combine(root, "marks", "index.txt")).Contains(note));
        MarkFiles.Compare(root, Sid); await MarkFiles.Pending;
        string html = File.ReadAllText(Path.Combine(folder, "comparison.html"));
        Check("focus first and state differences highlighted", html.IndexOf("crosshair", StringComparison.Ordinal) < html.IndexOf("net 4 entities", StringComparison.Ordinal) && html.Contains("<tr class=\"diff\"><th>net 4 entities"));
        await MarkFiles.Rename(root, Sid, 100.125, 2, "renamed " + Secret);
        var renamed = MarkFiles.Snapshot.Where(e => e.Sid == Sid).ToArray();
        Check("rename local and received", renamed.Length == 2 && renamed.All(e => e.Note == "renamed [removed]" && e.File.Contains("renamed-removed")) && !File.Exists(local) && !File.Exists(received));
        Check("received suffix survives rename", renamed.Single(e => e.Received).File.EndsWith("-from-p3-aaaaaaaa.json"));
        foreach (var e in renamed)
        {
            string path = Path.Combine(root, e.Folder, e.File);
            byte[] bytes = File.ReadAllBytes(path);
            Check("rename head and validation", JsonDocument.Parse(bytes).RootElement.GetProperty("head").GetProperty("note").GetString() == "renamed [removed]" && ReportTransfer.Json(bytes, true, Sid, 100.125, true).SequenceEqual(bytes));
        }
        await MarkFiles.Rename(root, Sid, 100.125, 7, "arrived before capture");
        await Save(root, folder, "pending", origin: 7);
        Check("rename before capture/transfer completion", MarkFiles.Snapshot.Single(e => e.Origin == 7).Note == "arrived before capture");
        await MarkFiles.Rename(root, Sid, 100.125, 2, "");
        Check("empty rename", MarkFiles.Snapshot.Where(e => e.Origin == 2).All(e => e.Note == "" && e.File.Contains("unknown")));
        MarkFiles.Initialize(Path.Combine(root, "other")); await MarkFiles.Pending;
        MarkFiles.Initialize(root); await MarkFiles.Pending;
        Check("persistent index rebuilt from local and received heads", MarkFiles.Snapshot.Length == 3 && MarkFiles.Snapshot.Count(e => e.Received) == 1 && MarkFiles.Snapshot.Single(e => e.Origin == 7).Note == "arrived before capture");
        var privateTarget = new MarkFocus.Target { Name = Secret, Prefab = 12, Net = 4, Distance = 1 };
        var privateFocus = new MarkFocus { Crosshair = privateTarget, Car = privateTarget, Held = privateTarget, Dragging = privateTarget };
        Check("focus privacy gate", !privateFocus.Json().Contains(Secret) && privateFocus.Json().Contains("[removed]"));
        foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(path);
            Check("output privacy and UTF-8 without BOM", !Encoding.UTF8.GetString(bytes).Contains(Secret) && !(bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191));
        }
        Check("comparison includes marker and trace focus nets", File.ReadAllText(Path.Combine(folder, "comparison.html")).Contains("net 279 entities") && File.ReadAllText(Path.Combine(folder, "comparison.html")).Contains("trace focus"));
        Check("worker errors absent", MarkFiles.Error == null);
        // This directory is created by this test and never comes from caller input.
        await MarkFiles.Pending; Directory.Delete(root, true);
        Console.WriteLine("Failures: " + _fails); return _fails == 0 ? 0 : 1;
    }
}

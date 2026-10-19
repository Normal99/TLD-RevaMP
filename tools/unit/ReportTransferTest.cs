using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using TLDRevamp.Net;

static class ReportTransferTest
{
    private static int _fails;
    private static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) _fails++; }
    private static void Reject(string name, Action action)
    {
        try { action(); Check(name, false); }
        catch (Exception e) when (e is InvalidDataException || e is System.Xml.XmlException || e is System.Runtime.Serialization.SerializationException) { Check(name, true); }
    }
    private static byte[] Utf(string s) => Encoding.UTF8.GetBytes(s);
    private const string Box = "{\"head\":{\"privacy\":1,\"sid\":\"0123456789abcdef\",\"momentSt\":100.125},\"cols\":[\"st\",\"ms\"],\"rows\":[[100,16]],\"state\":{}}";
    private static byte[] Zip(string extra = null, byte[] bytes = null, bool box = true)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using (var entry = Entry(archive, "stats.json").Open()) entry.Write(Utf("{}"));
            if (box) using (var entry = Entry(archive, "blackbox.json").Open()) entry.Write(Utf(Box));
            if (extra != null) using (var entry = Entry(archive, extra).Open()) entry.Write(bytes ?? Utf("hello"));
        }
        return memory.ToArray();
    }
    private static ZipArchiveEntry Entry(ZipArchive archive, string name)
    {
        var entry = archive.CreateEntry(name); entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero); return entry;
    }
    static int Main(string[] args)
    {
        Check("slug removes paths, Unicode and controls", ReportTransfer.Slug("../A\\B:C\né") == "ABC");
        Check("empty slug fallback", ReportTransfer.Slug("../") == "unknown");
        Check("slug bounded", ReportTransfer.Slug(new string('x', 100)).Length == 32);
        Check("moment name stable, no wall clock", ReportTransfer.MomentName("0123456789abcdef", 100.125, "wheel bug") == "mark-0123456789abcdef-100-125");
        for (int source = 0; source <= 1; source++) for (int target = 0; target <= 1; target++)
            Check("reciprocity " + source + target, ReportTransfer.Receives(source == 1, target == 1, 1, 2) == (source == 1 && target == 1));
        Check("no self echo", !ReportTransfer.Receives(true, true, 1, 1));
        Check("sharing clients relay without host consent", ReportTransfer.Receives(true, true, 1, 2) && !ReportTransfer.Receives(true, false, 1, 0));
        Reject("negative size", () => new ReportAssembly(1, -1, false));
        Reject("oversized mark", () => new ReportAssembly(1, ReportTransfer.MarkMax + 1, true));
        Reject("oversized zip", () => new ReportAssembly(1, ReportTransfer.ReportMax + 1, false));
        var data = new byte[ReportTransfer.ChunkBytes + 3]; data[data.Length - 1] = 9;
        var parts = new ReportAssembly(7, data.Length, true);
        Check("partial assembly", parts.Add(7, data.Length, 0, data, 0, ReportTransfer.ChunkBytes) == null);
        var complete = parts.Add(7, data.Length, ReportTransfer.ChunkBytes, data, ReportTransfer.ChunkBytes, 3);
        Check("complete exact bytes", complete.Length == data.Length && complete[complete.Length - 1] == 9);
        Reject("duplicate tail", () => parts.Add(7, data.Length, ReportTransfer.ChunkBytes, data, 0, 3));
        Reject("skipped offset", () => new ReportAssembly(1, 20, true).Add(1, 20, 1, data, 0, 10));
        Reject("changed transfer", () => new ReportAssembly(1, 20, true).Add(2, 20, 0, data, 0, 10));
        Reject("changed total", () => new ReportAssembly(1, 20, true).Add(1, 19, 0, data, 0, 10));
        Reject("oversized chunk", () => new ReportAssembly(1, data.Length, true).Add(1, data.Length, 0, data, 0, data.Length));
        Reject("overflowing offset", () => new ReportAssembly(1, 20, true).Add(1, 20, int.MaxValue, data, 0, 10));
        var allowance = new ReportAllowance();
        Check("first report", allowance.Start(false, 100, 0));
        Check("report gap", !allowance.Start(false, 100, 29) && allowance.Start(false, 100, 30));
        Check("independent mark gap", allowance.Start(true, 100, 30) && !allowance.Start(true, 100, 39) && allowance.Start(true, 100, 40));
        Check("dedupe retained with allowance across reconnect", allowance.Remember("hash") && allowance.Has("hash") && !allowance.Remember("hash"));
        allowance = new ReportAllowance();
        for (int i = 0; i < 20; i++) Check("report count " + i, allowance.Start(false, 1, i * 30));
        Check("report count cap", !allowance.Start(false, 1, 1000));
        allowance = new ReportAllowance();
        for (int i = 0; i < 80; i++) if (!allowance.Start(true, 1, i * 10)) _fails++;
        Check("mark count cap", !allowance.Start(true, 1, 1000));
        allowance = new ReportAllowance();
        Check("player byte cap", allowance.Start(false, 24 << 20, 0) && allowance.Start(false, 24 << 20, 30) && !allowance.Start(false, 24 << 20, 60));
        allowance = new ReportAllowance();
        Check("four chunk burst", allowance.Chunk(16384, 0) && allowance.Chunk(16384, 0) && allowance.Chunk(16384, 0) && allowance.Chunk(16384, 0));
        Check("rate refuses excess", !allowance.Chunk(1, 0));
        Check("tokens refill", allowance.Chunk(16384, 0.1));
        Check("relay batching does not need original start gap", allowance.Start(false, 1, 0, false) && allowance.Start(false, 1, 0, false));
        var relay = new ReportRelayBudget();
        for (int i = 0; i < 16; i++) if (!relay.Reserve(16 << 20)) _fails++;
        Check("fan-out session cap includes each recipient", relay.Used == ReportTransfer.RelayMax && !relay.Reserve(1));
        Check("relay rejects invalid reservations", !relay.Reserve(-1) && !relay.Reserve(int.MaxValue));
        relay.Clear(); Check("new session resets relay cap", relay.Reserve(1));
        var compressed = ReportTransfer.Compress(Utf(Box));
        var timed = (byte[])compressed.Clone(); timed[4] = 1;
        Reject("GZip creation time excluded", () => ReportTransfer.Validate(timed, true, "0123456789abcdef", 100.125));
        Check("GZip black-box round trip", Encoding.UTF8.GetString(ReportTransfer.Validate(compressed, true, "0123456789abcdef", 100.125)) == Box);
        Reject("legacy captures cannot be reshared", () => ReportTransfer.Validate(ReportTransfer.Compress(Utf(Box.Replace("\"privacy\":1,", ""))), true, "0123456789abcdef", 100.125));
        Reject("wrong session", () => ReportTransfer.Validate(compressed, true, "other", 100.125));
        Reject("wrong moment", () => ReportTransfer.Validate(compressed, true, "0123456789abcdef", 101));
        Reject("invalid JSON", () => ReportTransfer.Validate(ReportTransfer.Compress(Utf("{ broken")), true, "0123456789abcdef", 100.125));
        Reject("trailing JSON", () => ReportTransfer.Json(Utf(Box + "{}"), true, "0123456789abcdef", 100.125));
        Reject("duplicate JSON head", () => ReportTransfer.Json(Utf(Box.Replace("\"state\":{}", "\"head\":{}")), true, "0123456789abcdef", 100.125));
        foreach (string value in new[] { "{\"x\":01}", "{\"x\":NaN}", "{\"x\":1e}", "{\"x\":true,}", "{\"x\":\"\\q\"}", "{\"x\":[1,]}", "[]" })
            Reject("strict JSON " + value, () => ReportTransfer.Json(Utf(value), false, "0123456789abcdef", 100.125));
        Reject("JSON depth", () => ReportTransfer.Json(Utf("{\"x\":" + new string('[', 65) + "0" + new string(']', 65) + "}"), false, "0123456789abcdef", 100.125));
        Reject("invalid UTF8", () => ReportTransfer.Json(new byte[] { 123, 255, 125 }, false, "0123456789abcdef", 100.125));
        Check("JSON field order and escapes", ReportTransfer.Validate(ReportTransfer.Compress(Utf("{\"rows\":[],\"cols\":[],\"head\":{\"privacy\":1,\"momentSt\":100.125,\"s\\u0069d\":\"0123456789abcdef\"}}")), true, "0123456789abcdef", 100.125) != null);
        Reject("truncated GZip", () => ReportTransfer.Validate(compressed.AsSpan(0, compressed.Length - 4).ToArray(), true, "0123456789abcdef", 100.125));
        Reject("not black box", () => ReportTransfer.Validate(ReportTransfer.Compress(Utf("{}")), true, "0123456789abcdef", 100.125));
        Reject("expanded GZip limit", () => ReportTransfer.Validate(ReportTransfer.Compress(new byte[ReportTransfer.JsonMax + 1]), true, "0123456789abcdef", 100.125));
        Check("report zip sane", ReportTransfer.Validate(Zip(), false, "0123456789abcdef", 100.125) == null);
        Check("empty report directory is valid", ReportTransfer.Validate(Zip("sessions/", Array.Empty<byte>()), false, "0123456789abcdef", 100.125) == null);
        Reject("missing black box", () => ReportTransfer.Validate(Zip(box: false), false, "0123456789abcdef", 100.125));
        foreach (string name in new[] { "../evil", "/evil", "C:evil", "dir\\evil", "a/./evil", "a//evil", "stats.json" })
            Reject("zip path " + name, () => ReportTransfer.Validate(Zip(name), false, "0123456789abcdef", 100.125));
        Reject("zip expansion limit", () => ReportTransfer.Validate(Zip("bomb", new byte[(32 << 20) + 1]), false, "0123456789abcdef", 100.125));
        foreach (string name in new[] { "screenshot.png", "LogOutput.log", "Player.log", "BepInEx.cfg", "unknown.json" })
            Reject("private attachment " + name, () => ReportTransfer.Validate(Zip(name), false, "0123456789abcdef", 100.125));
        foreach (string field in new[] { "name", "msg", "at", "chat", "path" })
            Reject("free text " + field, () => ReportTransfer.Validate(ReportTransfer.Compress(Utf(Box.Replace("\"state\":{}", "\"state\":{\"" + field + "\":\"Ylva Testperson\"}"))), true, "0123456789abcdef", 100.125));
        foreach (string field in new[] { "steamId", "accountId", "lobbyId", "port" })
            Reject("numeric identity " + field, () => ReportTransfer.Validate(ReportTransfer.Compress(Utf(Box.Replace("\"state\":{}", "\"state\":{\"" + field + "\":1234567}"))), true, "0123456789abcdef", 100.125));
        Reject("broken zip", () => ReportTransfer.Validate(Utf("PKbad"), false, "0123456789abcdef", 100.125));
        var commented = Zip(); int oldLength = commented.Length; byte[] comment = Utf("Ylva Testperson");
        Array.Resize(ref commented, oldLength + comment.Length); commented[oldLength - 2] = (byte)comment.Length;
        Buffer.BlockCopy(comment, 0, commented, oldLength, comment.Length);
        Reject("ZIP comments excluded", () => ReportTransfer.Validate(commented, false, "0123456789abcdef", 100.125));
        var localTime = Zip(); localTime[10] = 1;
        Reject("ZIP local timestamps excluded", () => ReportTransfer.Validate(localTime, false, "0123456789abcdef", 100.125));
        var corruptZip = Zip();
        int central = -1;
        for (int i = 0; i < corruptZip.Length - 4; i++) if (corruptZip[i] == 80 && corruptZip[i + 1] == 75 && corruptZip[i + 2] == 1 && corruptZip[i + 3] == 2) { central = i; break; }
        corruptZip[central + 16] ^= 1;
        Reject("zip CRC checked", () => ReportTransfer.Validate(corruptZip, false, "0123456789abcdef", 100.125));
        // Optional real captures passed from an offline playtest remain valid input to the same validator.
        foreach (string path in args)
        {
            byte[] json = File.ReadAllBytes(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var head = doc.RootElement.GetProperty("head");
            ReportTransfer.Json(json, true, head.GetProperty("sid").GetString(), head.GetProperty("momentSt").GetDouble());
            Check("real capture " + Path.GetFileName(path), true);
        }
        Console.WriteLine("Failures: " + _fails); return _fails == 0 ? 0 : 1;
    }
}

namespace TLDRevamp { static class Json { internal static string Str(string value) => System.Text.Json.JsonSerializer.Serialize(value); } }

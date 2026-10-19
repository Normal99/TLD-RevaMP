using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TLDInstalls;

public enum ReportFileKind { Mark, Report, PlayLog }

public sealed record ReportFile(string RelativePath, ReportFileKind Kind, DateTime TimeUtc, string Note = "", bool Selected = false)
{
    public string Label => $"{Kind switch { ReportFileKind.Mark => "Mark", ReportFileKind.Report => "Report zip", _ => "Newest play log" }} · {TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}" +
                           (Note.Length > 0 ? " · " + Note : "") + "\n" + RelativePath;
}

public sealed record PrivateReport(string Path, int FileCount, string Sha256)
{
    public string Reference => Sha256[..12];
}

/// <summary>Local files only. The public issue receives only the packed file count and hash reference.</summary>
public static class BugReportFiles
{
    public const long MaxBytes = 100_000_000;
    public const string Notice = "This file stays on your PC. Don't attach it to the GitHub issue (issues are public); send it privately to the developers if they ask for it.";
    public const string Contents = "Marks and play logs contain hardware, settings, positions and game state. Report zips also contain a screenshot and logs that can show player names, account ids and Windows user paths. Nothing here is uploaded.";
    public const string DescriptionNotice = "Describe the bug and when it happened. Keep telemetry, file contents and summaries out of the public description.";
    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint crc = (uint)i;
        for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        return crc;
    }).ToArray();

    public static string Root(CopyInfo c) => Path.Combine(c.GameDir, "BepInEx", "tldrevamp-feedback");

    public static List<ReportFile> Discover(CopyInfo c, Action<string> log, DateTime? nowUtc = null)
    {
        var root = Root(c);
        var marks = new List<ReportFile>();
        foreach (var path in Find(Path.Combine(root, "marks"), "*.json", false, log))
        {
            try
            {
                using var data = Read(path, MaxBytes);
                using var doc = JsonDocument.Parse(data);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("head", out var head) || head.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException();
                var note = Text(r, "note") ?? Text(head, "note");
                if (note == null)
                {
                    var m = Regex.Match(Path.GetFileNameWithoutExtension(path), @"^mark-[^-]+-\d+-\d+-(.*?)(?:-from-player-.*)?$");
                    note = m.Success ? m.Groups[1].Value : "(no note)";
                }
                marks.Add(new(Path.GetRelativePath(root, path), ReportFileKind.Mark, MarkTime(head, File.GetLastWriteTimeUtc(path)), note));
            }
            catch (Exception e) when (BadFile(e)) { Skipped(path, log); }
        }
        marks = marks.OrderByDescending(f => f.TimeUtc).ThenBy(f => f.RelativePath, StringComparer.Ordinal).ToList();
        var now = nowUtc ?? DateTime.UtcNow;
        var recent = marks.FindIndex(f => f.TimeUtc <= now && f.TimeUtc >= now.AddMinutes(-30));
        if (recent >= 0) marks[recent] = marks[recent] with { Selected = true };

        var reports = Find(root, "*.zip", false, log).Concat(Find(Path.Combine(root, "players"), "*.zip", true, log))
            .Select(p => new ReportFile(Path.GetRelativePath(root, p), ReportFileKind.Report, File.GetLastWriteTimeUtc(p)))
            .OrderByDescending(f => f.TimeUtc).ThenBy(f => f.RelativePath, StringComparer.Ordinal);
        marks.AddRange(reports);
        var play = Find(Path.Combine(root, "sessions"), "*.jsonl", false, log)
            .Select(p => new ReportFile(Path.GetRelativePath(root, p), ReportFileKind.PlayLog, File.GetLastWriteTimeUtc(p)))
            .OrderByDescending(f => f.TimeUtc).ThenBy(f => f.RelativePath, StringComparer.Ordinal).FirstOrDefault();
        if (play != null) marks.Add(play);
        return marks;
    }

    static string? Text(JsonElement r, string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static DateTime MarkTime(JsonElement head, DateTime fallback)
    {
        if (DateTimeOffset.TryParse(Text(head, "start"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) &&
            head.TryGetProperty("st", out var st) && st.ValueKind == JsonValueKind.Number && st.TryGetDouble(out var from) &&
            head.TryGetProperty("momentSt", out var moment) && moment.ValueKind == JsonValueKind.Number && moment.TryGetDouble(out var to) &&
            double.IsFinite(to - from))
        {
            try { return start.UtcDateTime.AddSeconds(to - from); }
            catch (ArgumentOutOfRangeException) { }
        }
        return fallback;
    }

    static List<string> Find(string dir, string pattern, bool recursive, Action<string> log)
    {
        try
        {
            if (!Directory.Exists(dir)) return new();
            return Directory.GetFiles(dir, pattern, new EnumerationOptions { RecurseSubdirectories = recursive, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).ToList();
        }
        catch (Exception e) when (BadFile(e)) { log("  skipped an unreadable telemetry folder"); return new(); }
    }

    static bool BadFile(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or DecoderFallbackException;
    static void Skipped(string path, Action<string> log) => log($"  skipped {Path.GetFileName(path)} (corrupt, unreadable or over the 100 MB limit)");

    static MemoryStream Read(string path, long remaining)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > remaining) throw new InvalidDataException();
        var data = new MemoryStream();
        try
        {
            var buffer = new byte[81920]; int n;
            while ((n = input.Read(buffer)) != 0)
            {
                if (data.Length + n > remaining) throw new InvalidDataException();
                data.Write(buffer, 0, n);
            }
            data.Position = 0;
            return data;
        }
        catch { data.Dispose(); throw; }
    }

    static void Validate(MemoryStream data, ReportFileKind kind)
    {
        if (kind == ReportFileKind.Report)
        {
            using var zip = new ZipArchive(data, ZipArchiveMode.Read, leaveOpen: true);
            long expanded = 0;
            var buffer = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                uint crc = uint.MaxValue;
                long length = 0;
                using var input = entry.Open(); int n;
                while ((n = input.Read(buffer)) != 0)
                {
                    expanded += n; length += n;
                    if (expanded > MaxBytes) throw new InvalidDataException();
                    for (int i = 0; i < n; i++) crc = CrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
                }
                if (length != entry.Length || ~crc != entry.Crc32) throw new InvalidDataException();
            }
        }
        else if (kind == ReportFileKind.Mark)
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("head", out var head) || head.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException();
        }
        else
        {
            using var reader = new StreamReader(data, new UTF8Encoding(false, true), leaveOpen: true);
            bool any = false;
            while (reader.ReadLine() is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
                any = true;
            }
            if (!any) throw new InvalidDataException();
        }
        data.Position = 0;
    }

    public static PrivateReport? Pack(CopyInfo c, string text, IEnumerable<ReportFile> selected, Action<string> log)
    {
        var files = selected.DistinctBy(f => f.RelativePath).ToList();
        if (files.Count == 0) return null;
        var root = Path.GetFullPath(Root(c)) + Path.DirectorySeparatorChar;
        var dir = Path.Combine(c.Dir, "bug-reports");
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            int count = 0;
            long budget = 128;
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    MemoryStream data;
                    var name = file.RelativePath.Replace('\\', '/');
                    long overhead = 128 + 2 * Encoding.UTF8.GetByteCount(name);
                    try
                    {
                        var path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
                        if (!path.StartsWith(root, Env.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException();
                        data = Read(path, MaxBytes - budget - overhead);
                        try { Validate(data, file.Kind); }
                        catch { data.Dispose(); throw; }
                    }
                    catch (Exception e) when (BadFile(e)) { Skipped(file.RelativePath, log); continue; }
                    using (data)
                    {
                        // Stored entries keep the size bound independent of compression ratios and ZIP overhead.
                        using var output = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                        data.CopyTo(output);
                        budget += data.Length + overhead;
                        count++;
                    }
                }
            }
            if (count == 0) { log("  no files packed"); return null; }
            if (new FileInfo(tmp).Length > MaxBytes) throw new UserError("the private zip exceeds 100 MB: select fewer files");
            var slug = Regex.Replace(BugReports.Title(text).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (slug.Length == 0) slug = "bug";
            if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
            var basename = $"{DateTime.Now:yyyyMMdd-HHmm}-{slug}";
            var pathOut = Path.Combine(dir, basename + ".zip");
            for (int i = 2; File.Exists(pathOut); i++) pathOut = Path.Combine(dir, $"{basename}-{i}.zip");
            File.Move(tmp, pathOut);
            var result = new PrivateReport(pathOut, count, Files.Sha256(pathOut));
            log("  private zip: " + pathOut);
            log(Notice);
            return result;
        }
        finally { File.Delete(tmp); }
    }
}

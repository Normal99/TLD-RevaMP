using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace TLDInstalls.Tests;

public sealed class BugReportFileTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("tld-private-report-test").FullName;
    readonly List<string> log = new();
    static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    internal const string Mark = """
        {"head":{"sid":"fixture-private-session","momentSt":3456.789,"start":"2031-03-04T05:06:07Z","hw":{"gpu":"fixture-private-gpu"}},"note":"fixture private note","cols":["st","ms"],"rows":[[3456.789,123.456]],"state":{"posCm":[987654,12345,67890]}}
        """;
    public void Dispose() => Directory.Delete(tmp, true);
    CopyInfo Copy => new() { Name = "test", Kind = Kind.Release, Dir = tmp, RevampVersion = "0.69.1" };

    string Write(string relative, string text = Mark, DateTime? time = null)
    {
        var path = Path.Combine(BugReportFiles.Root(Copy), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, time ?? Now);
        return path;
    }

    string Report(string relative = "TLDRevamp-report.zip", DateTime? time = null)
    {
        var path = Write(relative, "", time);
        File.Delete(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("stats.json", CompressionLevel.NoCompression).Open());
            writer.Write(Mark);
        }
        File.SetLastWriteTimeUtc(path, time ?? Now);
        return path;
    }

    List<ReportFile> Discover() => BugReportFiles.Discover(Copy, log.Add, Now);

    [Fact]
    public void DiscoversMarksNewestFirstWithNotesAndLocalTimesAndOnlyNewestPlayLog()
    {
        Write("marks/old.json", time: Now.AddHours(-1));
        Write("marks/new.json", time: Now.AddMinutes(-1));
        Write("marks/middle.json", time: Now.AddMinutes(-10));
        Write("marks/ignored.json.tmp", "unfinished");
        Report(time: Now.AddMinutes(-20));
        Report("players/player-fixture/received.zip");
        Write("sessions/old.jsonl", "{}\n", Now.AddHours(-1));
        Write("sessions/new.jsonl", "{}\n", Now.AddMinutes(-1));
        var files = Discover();
        Assert.Equal(new[] { "new.json", "middle.json", "old.json", "received.zip", "TLDRevamp-report.zip", "new.jsonl" }, files.Select(f => Path.GetFileName(f.RelativePath)));
        Assert.Equal("fixture private note", files[0].Note);
        Assert.Contains(Now.AddMinutes(-1).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), files[0].Label);
        Assert.Equal(files[0], Assert.Single(files, f => f.Selected));
        Assert.False(files[^1].Selected);
        Assert.Empty(log);
    }

    [Theory]
    [InlineData(-29, true)]
    [InlineData(-30, true)]
    [InlineData(-31, false)]
    [InlineData(1, false)]
    public void DefaultSelectionOnlyUsesAMarkWithinTheLastThirtyMinutes(int minutes, bool selected)
    {
        Write("marks/mark-solo-3456-789-door-stuck.json", "{\"head\":{}}", Now.AddMinutes(minutes));
        Report();
        Write("sessions/new.jsonl", "{}\n");
        var files = Discover();
        Assert.Equal(selected, files[0].Selected);
        Assert.Equal("door-stuck", files[0].Note);
        Assert.All(files.Skip(1), f => Assert.False(f.Selected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PacksOnlyTickedFilesWithOriginalBytesAndHash(bool playLog)
    {
        Write("marks/a.json"); Write("marks/not-ticked.json");
        Report(); Report("players/player-fixture/received.zip");
        Write("sessions/play.jsonl", "{\"k\":\"head\",\"hw\":\"fixture-private-gpu\"}\n{}");
        var selected = Discover().Where(f => Path.GetFileName(f.RelativePath) != "not-ticked.json" && (playLog || f.Kind != ReportFileKind.PlayLog)).ToList();
        var result = BugReportFiles.Pack(Copy, "Doors stay shut", selected.Concat(selected), log.Add)!;
        Assert.Equal(playLog ? 4 : 3, result.FileCount);
        Assert.Equal(Path.Combine(tmp, "bug-reports"), Path.GetDirectoryName(result.Path));
        Assert.EndsWith("-doors-stay-shut.zip", result.Path);
        Assert.True(new FileInfo(result.Path).Length <= BugReportFiles.MaxBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(result.Path))), result.Sha256);
        Assert.Equal(result.Sha256[..12], result.Reference);
        using var zip = ZipFile.OpenRead(result.Path);
        Assert.Equal(selected.Select(f => f.RelativePath.Replace('\\', '/')), zip.Entries.Select(e => e.FullName));
        foreach (var entry in zip.Entries)
        {
            using var bytes = new MemoryStream(); using var input = entry.Open(); input.CopyTo(bytes);
            Assert.Equal(File.ReadAllBytes(Path.Combine(BugReportFiles.Root(Copy), entry.FullName)), bytes.ToArray());
        }
        Assert.Contains(BugReportFiles.Notice, log);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(result.Path)!, "*.part"));
    }

    [Fact]
    public void NothingTickedCreatesNoFolderOrZipAndNoReference()
    {
        Write("marks/a.json");
        Assert.Null(BugReportFiles.Pack(Copy, "bug", Array.Empty<ReportFile>(), log.Add));
        Assert.False(Directory.Exists(Path.Combine(tmp, "bug-reports")));
        var body = BugReports.Body(Copy, "bug");
        Assert.Contains("Telemetry: kept private (0 files).", body);
        Assert.DoesNotContain("Reference:", body);
    }

    [Fact]
    public void SkipsCorruptMarksAtDiscoveryAndReportsWhy()
    {
        Write("marks/broken.json", "{unfinished"); Write("marks/not-a-mark.json", "{}");
        Write("marks/good.json");
        Assert.Single(Discover());
        Assert.Equal(2, log.Count);
        Assert.All(log, l => Assert.Contains("skipped", l));
    }

    [Fact]
    public void SkipsFilesThatBecameCorruptUnreadableMissingOrOversizedBeforePacking()
    {
        var corrupt = Write("marks/corrupt.json");
        var missing = Write("marks/missing.json");
        var locked = Write("marks/locked.json");
        var oversized = Write("marks/oversized.json");
        Write("marks/good.json");
        var files = Discover();
        File.WriteAllText(corrupt, "bad"); File.Delete(missing);
        using (var huge = File.OpenWrite(oversized)) huge.SetLength(BugReportFiles.MaxBytes + 1);
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = BugReportFiles.Pack(Copy, "bug", files, log.Add)!;
        Assert.Equal(1, result.FileCount);
        using var zip = ZipFile.OpenRead(result.Path);
        Assert.Equal("marks/good.json", Assert.Single(zip.Entries).FullName);
        Assert.Equal(4, log.Count(l => l.Contains("skipped")));
    }

    [Fact]
    public void SkipsInvalidZipsBadChecksumsAndCorruptPlayLogsWithoutLeavingAnEmptyZip()
    {
        Report("valid.zip");
        var crc = Report("bad-crc.zip");
        var bytes = File.ReadAllBytes(crc);
        int dataStart = 30 + BitConverter.ToUInt16(bytes, 26) + BitConverter.ToUInt16(bytes, 28);
        bytes[dataStart] ^= 1; File.WriteAllBytes(crc, bytes);
        Write("broken.zip", "not a zip");
        Write("sessions/broken.jsonl", "{}\nbroken\n");
        var files = Discover().Where(f => f.RelativePath != "valid.zip").ToList();
        Assert.Null(BugReportFiles.Pack(Copy, "bug", files, log.Add));
        Assert.Equal(3, log.Count(l => l.Contains("skipped")));
        Assert.Empty(Directory.GetFiles(Path.Combine(tmp, "bug-reports")));
    }

    [Fact]
    public void TotalZipStaysUnderOneHundredMegabytesAndLaterSmallFilesStillFit()
    {
        var files = new List<ReportFile>();
        foreach (var name in new[] { "large-a.json", "large-b.json" })
        {
            var path = Write("marks/" + name, "{\"head\":{}}\n");
            using (var output = new FileStream(path, FileMode.Append))
            {
                var spaces = Encoding.UTF8.GetBytes(new string(' ', 1_000_000));
                for (int i = 0; i < 55; i++) output.Write(spaces);
            }
            files.Add(new("marks/" + name, ReportFileKind.Mark, Now));
        }
        Write("marks/small.json"); files.Add(new("marks/small.json", ReportFileKind.Mark, Now));
        var result = BugReportFiles.Pack(Copy, "bug", files, log.Add)!;
        Assert.Equal(2, result.FileCount);
        Assert.InRange(new FileInfo(result.Path).Length, 55_000_000, BugReportFiles.MaxBytes);
        using var zip = ZipFile.OpenRead(result.Path);
        Assert.Equal(new[] { "marks/large-a.json", "marks/small.json" }, zip.Entries.Select(e => e.FullName));
        Assert.Single(log, l => l.Contains("skipped"));
    }

    [Fact]
    public void MarkTimesUseTheCaptureMomentEvenWhenAnOldFileArrivesNow()
    {
        Write("marks/mark-solo-220-000-new-note.json",
            "{\"head\":{\"start\":\"2026-10-19T13:55:00+02:00\",\"st\":100,\"momentSt\":220}}", Now.AddDays(-1));
        Write("marks/mark-solo-400-000-old-note-from-player-fixture-1.json",
            "{\"head\":{\"start\":\"2026-10-18T13:55:00+02:00\",\"st\":100,\"momentSt\":400}}", Now);
        var files = Discover();
        Assert.Equal("new-note", files[0].Note);
        Assert.Equal(Now.AddMinutes(-3), files[0].TimeUtc);
        Assert.True(files[0].Selected);
        Assert.Equal("old-note", files[1].Note);
        Assert.False(files[1].Selected);
    }

    [Fact]
    public void RepeatedReportsKeepBothZips()
    {
        Write("marks/a.json"); var files = Discover();
        var first = BugReportFiles.Pack(Copy, "bug", files, log.Add)!;
        var second = BugReportFiles.Pack(Copy, "bug", files, log.Add)!;
        Assert.NotEqual(first.Path, second.Path);
        Assert.True(File.Exists(first.Path)); Assert.True(File.Exists(second.Path));
    }
}

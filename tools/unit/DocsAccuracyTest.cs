using System;
using System.IO;
using System.Text.RegularExpressions;

static class DocsAccuracyTest
{
    static int failures;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string Read(string path) => File.ReadAllText(Path.Combine(root, path));
        string protocol = Regex.Match(Read("src/TLDRevamp/Net/Protocol.cs"), @"const ushort Version = (\d+);").Groups[1].Value;
        string readme = Read("README.md"), sync = Read("docs/SYNC-CONTROLS.md");
        Check("README requires the current wire protocol", protocol.Length > 0 && readme.Contains($"Protocol **{protocol}** is required"));
        Check("README limits panel renames to own notes", readme.Contains("rename your own notes in the panel") && !readme.Contains("Browse and rename notes in the panel"));
        Check("sync controls describes both protocol 41 changes", sync.Contains("41 adds the pickup Claim epoch/owner pose-clock fence") && sync.Contains("host-authenticated renamer index"));
        string telemetry = Read("docs/TELEMETRY.md");
        string tpa = telemetry.Substring(telemetry.IndexOf("TPA events cover", StringComparison.Ordinal));
        Check("TPA protocol statement survives a combined-branch bump", tpa.Contains("no protocol change") && !Regex.IsMatch(tpa, @"Protocol remains \*\*\d+\*\*"));
        Check("client arrival count includes pre-validation known-entity states", tpa.Contains("distinct net IDs") && tpa.Contains("State/CompactState headers for known entities") && tpa.Contains("before proxy, epoch and pose-clock checks"));
        Check("client arrival count does not prove destination application", tpa.Contains("no client destination-distance filter") && tpa.Contains("stale/rejected states") && tpa.Contains("old area") && tpa.Contains("does not prove destination objects") && !tpa.Contains("distinct applied destination Add/state groups"));
        return failures == 0 ? 0 : 1;
    }
}

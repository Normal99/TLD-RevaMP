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
        Check("current protocol is 47", protocol == "47");
        string wire = Read("src/TLDRevamp/Net/ObjectPanelWire.cs");
        Check("comparison retains message ids 105-109", wire.Contains("Ask = 105, Query = 106, Answer = 107, Row = 108, Roster = 109"));
        Check("join manifest and end marker avoid comparison ids", Read("src/TLDRevamp/Net/Protocol.cs").Contains("JoinLoading = 110;") &&
            Read("docs/MULTIPLAYER-ARCHITECTURE.md").Contains("Reliable message 110 sends phase 0"));
        string readme = Read("README.md"), sync = Read("docs/SYNC-CONTROLS.md");
        Check("README requires the current wire protocol", protocol.Length > 0 && readme.Contains($"Protocol **{protocol}** is required"));
        Check("README limits panel renames to own notes", readme.Contains("rename your own notes in the panel") && !readme.Contains("Browse and rename notes in the panel"));
        Check("sync controls retains authenticated rename relays", sync.Contains("host-authenticated renamer index"));
        Check("sync controls describes uniform diagnostic claims", sync.Contains("every Claim is six bytes") && sync.Contains("producer byte to every Claim") && sync.Contains("held transitions use ordinary State flags") && !sync.Contains("pickup Claim epoch") && !sync.Contains("held-release flag"));
        Check("sync controls describes current protocol and retained lock address", sync.Contains($"Protocol.Version is {protocol}") && sync.Contains("physlocks retain their mount address"));
        string telemetry = Read("docs/TELEMETRY.md");
        Check("telemetry describes current protocol and Claim size", telemetry.Contains($"Protocol.Version is **{protocol}**") && telemetry.Contains("6 for every Claim") && telemetry.Contains($"current protocol {protocol})"));
        Check("interest describes current protocol", Read("docs/INTEREST.md").Contains($"protocol is **{protocol}**"));
        Check("architecture describes current claims", Read("docs/MULTIPLAYER-ARCHITECTURE.md").Contains($"Protocol {protocol} uses uniform six-byte Claims"));
        string tpa = telemetry.Substring(telemetry.IndexOf("TPA events cover", StringComparison.Ordinal));
        Check("TPA protocol statement survives a combined-branch bump", tpa.Contains("no protocol change") && !Regex.IsMatch(tpa, @"Protocol remains \*\*\d+\*\*"));
        Check("client arrival count includes pre-validation known-entity states", tpa.Contains("distinct net IDs") && tpa.Contains("State/CompactState headers for known entities") && tpa.Contains("before proxy, epoch and pose-clock checks"));
        Check("client arrival count does not prove destination application", tpa.Contains("no client destination-distance filter") && tpa.Contains("stale/rejected states") && tpa.Contains("old area") && tpa.Contains("does not prove destination objects") && !tpa.Contains("distinct applied destination Add/state groups"));
        Check("panel TPA history uses the same observation semantics", tpa.Contains("`tpa-object-applied`") && tpa.Contains("pre-validation observation path as the aggregate count") && !tpa.Contains("means applied by the arriving client"));
        return failures == 0 ? 0 : 1;
    }
}

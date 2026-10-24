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
        Check("current protocol is " + protocol, protocol.Length > 0);
        // The explicit pin: the protocol this branch works with. The derived comparisons below follow the code, but
        // the pin says which protocol the docs and the tools are written for, so a bump that misses a doc fails here.
        const string pinned = "56";
        Check("the protocol is pinned to " + pinned, protocol == pinned);
        Check("the pin is in the protocol's own history", Read("src/TLDRevamp/Net/Protocol.cs").Contains("56:"));
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

        // ---- the status document must stay true about the tree it describes
        {
            string status = Read("docs/PROJECT-STATUS.md");
            Check("the status document exists and names the portable evidence root",
                status.Contains("${XDG_STATE_HOME:-$HOME/.local/state}/tld-revamp"));
            // the recovery archive is a SIBLING of workers/, not inside it: the path must not repeat it
            Check("the status document does not repeat workers/ in its path rows",
                !status.Contains("workers/workers") && !status.Contains("workers/results/workers"));
            Check("the status document names the recovery archive as a sibling",
                status.Contains("`recovery/`"));
            Check("the status document names the one check entrypoint", status.Contains("tools/verify.sh"));
            // live evidence is dynamic and must be kept separate from offline evidence and the run manifest
            Check("the status document separates live evidence, offline evidence, and the run manifest",
                status.Contains("## Live evidence") &&
                status.Contains("## Offline evidence") &&
                status.Contains("run manifest"));
            // the heavy example must go through the admission broker
            Check("the status document's heavy example is admitted",
                status.Contains("tools/tld-admit run --profile test --name verify -- tools/verify.sh"));
            // the entrypoint runs the UNITS only: it must not claim to run every gate
            Check("the status document says the entrypoint runs the units only",
                status.Contains("units only"));
            // projects are enumerated dynamically; no project count is written in the status document.
            int projects = Directory.GetFiles(Path.Combine(root, "tools", "unit"), "*.csproj").Length;
            Check("the status document describes dynamic project enumeration without a written project count",
                projects > 0 &&
                status.Contains("`tools/unit/*.csproj`") &&
                status.Contains("does not state a project count"));
            // the entrypoint must exist and be runnable
            Check("the check entrypoint exists", File.Exists(Path.Combine(root, "tools", "verify.sh")));
            // and the developer docs must link the status
            Check("the developer docs link the status", Read("docs/DEVELOPERS.md").Contains("PROJECT-STATUS.md"));
            Check("the contributing docs link the status", Read("docs/CONTRIBUTING.md").Contains("PROJECT-STATUS.md"));
        }
        Check("telemetry describes current protocol and Claim size", telemetry.Contains($"Protocol.Version is **{protocol}**") && telemetry.Contains("6 for every Claim") && telemetry.Contains($"current protocol {protocol})"));
        Check("interest describes current protocol", Read("docs/INTEREST.md").Contains($"protocol is **{protocol}**"));
        string architecture = Read("docs/MULTIPLAYER-ARCHITECTURE.md"), interest = Read("docs/INTEREST.md");
        Check("architecture describes current ragdoll protocol and retained claims", architecture.Contains($"Protocol {protocol} adds the owner's engine temperature") && architecture.Contains("Protocol 55 adds voice to everyone") && architecture.Contains("Protocol 54 adds Frame / Key / Delta ragdoll blocks") && architecture.Contains("Protocol 53 adds two claim reasons") && architecture.Contains("Protocol 52 uses uniform six-byte Claims"));
        Check("architecture describes cumulative ragdoll blocks", architecture.Contains("u8 n") && architecture.Contains("u16 limb-name hash") && architecture.Contains("ceil(n/8)") && architecture.Contains("Key plus any one Delta") && architecture.Contains("36 + 13n"));
        Check("interest keeps Keys reliable and replays them before Deltas", interest.Contains("Keys travel reliably and are never thinned") && interest.Contains("Delta is replayed only after its matching Key") && interest.Contains("keep their Key while streamed out"));
        foreach (string counter in new[] { "RagdollKeysSent", "RagdollDeltasSent", "RagdollFramesSent", "RagdollLimbsSent", "RagdollBlockBytes", "RagdollKeysIn", "RagdollDeltasIn", "RagdollDeltasNoKey", "RagdollHashMismatch", "StateTailMisses" })
            Check("telemetry describes " + counter, telemetry.Contains(counter));
        string tpa = telemetry.Substring(telemetry.IndexOf("TPA events cover", StringComparison.Ordinal));
        Check("TPA protocol statement survives a combined-branch bump", tpa.Contains("no protocol change") && !Regex.IsMatch(tpa, @"Protocol remains \*\*\d+\*\*"));
        Check("client arrival count includes pre-validation known-entity states", tpa.Contains("distinct net IDs") && tpa.Contains("State/CompactState headers for known entities") && tpa.Contains("before proxy, epoch and pose-clock checks"));
        Check("client arrival count does not prove destination application", tpa.Contains("no client destination-distance filter") && tpa.Contains("stale/rejected states") && tpa.Contains("old area") && tpa.Contains("does not prove destination objects") && !tpa.Contains("distinct applied destination Add/state groups"));
        Check("panel TPA history uses the same observation semantics", tpa.Contains("`tpa-object-applied`") && tpa.Contains("pre-validation observation path as the aggregate count") && !tpa.Contains("means applied by the arriving client"));
        return failures == 0 ? 0 : 1;
    }
}

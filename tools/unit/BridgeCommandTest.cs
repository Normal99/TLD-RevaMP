using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// Offline check that every command the shipped tools send to the debug bridge is actually dispatched by it.
//
// A tool that sends a command the bridge does not know gets {"error":"unknown command, try help"} and stops
// at its first call, so the tool is silently non-functional: tools/terrainhorizon.py sent "terrainhorizon" and
// tools/quest3vr.py sent "openxr", and neither existed in the bridge's switch. The failure was invisible until
// somebody ran the tool against a game.
//
// The check is on the source: the commands the tools send must appear as a case label in the bridge, and the
// commands that change something must NOT be in the bridge's read-only set (a session that denies developer
// access must still refuse them).

static class BridgeCommandTest
{
    static int checks, fails;

    static void Check(bool ok, string name)
    {
        checks++;
        if (!ok) { fails++; Console.WriteLine("FAIL " + name); }
        else Console.WriteLine("PASS " + name);
    }

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string bridge = File.ReadAllText(Path.Combine(root, "src", "TLDRevamp", "DebugBridge.cs"));

        // the case labels of the bridge's command switch
        var cases = new HashSet<string>(
            Regex.Matches(bridge, @"case\s+""([a-z0-9]+)"":")
                .Cast<Match>().Select(m => m.Groups[1].Value), StringComparer.Ordinal);

        // the read-only set
        var read = new HashSet<string>(
            Regex.Match(bridge, @"_readCmds\s*=\s*new[^;]*?\{(?<body>[^}]*)\}").Groups["body"].Value
                .Split(',').Select(x => Regex.Match(x, @"""([a-z0-9]+)""").Groups[1].Value)
                .Where(x => !string.IsNullOrEmpty(x)), StringComparer.Ordinal);

        // the tools that talk to the bridge, and the commands each of them sends
        var toolCommands = new Dictionary<string, string[]>
        {
            { "tools/terrainhorizon.py", new[] { "terrainhorizon" } },
            { "tools/quest3vr.py", new[] { "openxr", "stats", "errors", "mp" } },
        };

        foreach (var tool in toolCommands)
        {
            string path = Path.Combine(root, tool.Key);
            if (!File.Exists(path)) { Check(true, tool.Key + " is not in this tree"); continue; }
            string text = File.ReadAllText(path);
            foreach (string cmd in toolCommands[tool.Key])
            {
                // the tool really sends it
                bool sends = text.Contains("\"" + cmd + "\"") || text.Contains("'" + cmd + "'");
                Check(sends, tool.Key + " sends " + cmd);
                // and the bridge dispatches it
                Check(cases.Contains(cmd), "the bridge dispatches " + cmd + " (needed by " + tool.Key + ")");
            }
        }

        // the commands that change something are not in the read-only set
        foreach (string mutating in new[] { "openxr start", "openxr stop", "openxr recenter" })
        {
            string head = mutating.Split(' ')[0];
            // the read-only set holds command names, so the sub-command is handled by ReadOnlyCmd itself
            Check(!read.Contains(head) || mutating.EndsWith("state"),
                "the mutating command " + mutating + " is not read-only");
        }
        // the pure reads are in the read-only set
        Check(read.Contains("terrainhorizon"), "terrainhorizon is a pure read");
        Check(read.Contains("terrainhorizonstats"), "terrainhorizonstats is a pure read");
        Check(read.Contains("vr"), "vr is a pure read");
        Check(read.Contains("devaccess"), "devaccess is a pure read");

        // the horizon diagnostics are dispatched to the Lab, and both of them exist there
        Check(bridge.Contains("case \"terrainhorizon\": return Fixes.TerrainHorizonLab.Snapshot(arg);"),
            "terrainhorizon dispatches to the Lab's Snapshot");
        Check(bridge.Contains("case \"terrainhorizonstats\": return Fixes.TerrainHorizonLab.Stats();"),
            "terrainhorizonstats dispatches to the Lab's Stats");
        string lab = File.ReadAllText(Path.Combine(root, "src", "TLDRevamp", "Fixes", "TerrainHorizonLab.cs"));
        Check(lab.Contains("internal static string Stats()"), "the Lab has the Stats the command calls");
        Check(lab.Contains("internal static string Snapshot(string arg)"), "the Lab has the Snapshot the command calls");
        // the counters the command reports come from the renderer itself
        string horizon = File.ReadAllText(Path.Combine(root, "src", "TLDRevamp", "Fixes", "TerrainHorizon.cs"));
        Check(horizon.Contains("internal static string Stats()"), "the renderer has the counters the Lab reports");

        // the bridge's own read-only rule lets the READ forms of "openxr" through but not its mutations.
        // The rule is the policy's (VrBridgeCommandPolicy), so the bridge and the policy cannot drift apart.
        Check(bridge.Contains(@"if (cmd == ""openxr"") return VR.VrBridgeCommandPolicy.IsQuery(arg)"),
            "openxr alone is a read, its sub-commands are not");
        Check(bridge.Contains("VR.VrBridgeCommandPolicy"), "the bridge asks the policy for the split");

        // the policy's own rule: the documented query verbs read, the mutations do not.
        var policy = File.ReadAllText(Path.Combine(root, "src", "TLDRevamp", "VR", "VrLoaderPolicy.cs"));
        Check(policy.Contains("class VrBridgeCommandPolicy"), "the policy is the pure core");
        Check(policy.Contains("\"state\""), "the policy knows the state query");
        Check(policy.Contains("\"start\"") && policy.Contains("\"stop\"") && policy.Contains("\"retry\""),
            "the policy knows the mutating verbs");


        // ---- the local screenshot (issue #36/#37/#39/#40 need a local capture; the SHARED report must not
        // carry one). The three properties the coordinator names:
        //   (a) the command returns a path,
        //   (b) the path is NOT under the shared report root,
        //   (c) the payload contains no user-supplied text.
        {
            string reporter = File.ReadAllText(Path.Combine(root, "src", "TLDRevamp", "FeedbackReporter.cs"));
            string sharedRoot = Regex.Match(reporter, @"Root => Path\.Combine\(Paths\.BepInExRootPath, ""([^""]+)""\)")
                                    .Groups[1].Value;
            Check(sharedRoot.Length > 0, "the shared report root is named in the source");
            Check(sharedRoot != "tldrevamp-evidence",
                "the local evidence folder is not the shared report root");

            // the capture writes into the mod-owned folder, not the shared report root
            Check(bridge.Contains("tldrevamp-evidence"), "the local capture has its own mod-owned folder");
            Check(bridge.Contains("\"tldrevamp-evidence\""), "the folder is a literal in the source");

            // (a) the command returns a path
            Check(bridge.Contains("case \"shot\": return CaptureLocalShot(arg);"),
                "the shot command captures locally");
            Check(bridge.Contains("case \"screenshot\": return CaptureLocalShot(\""),
                "the screenshot command captures locally");
            Check(bridge.Contains("\\\"path\\\":"), "the capture answer carries a path");
            Check(bridge.Contains("\\\"shared\\\":false"), "the capture answer says it is not shared");

            // (b) the path is NOT under the shared report root: the folder the capture uses is a different
            // name than the shared one, and the answer carries that folder
            Check(!bridge.Contains("Path.Combine(FeedbackReporter.Root"),
                "the local capture does not write into the shared report root");
            Check(!bridge.Contains("FeedbackReporter.Root +"), "the local capture does not write into the shared report root");

            // (c) the payload contains no user-supplied text: the name is SANITISED to [A-Za-z0-9_-]
            Check(bridge.Contains("char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'"),
                "the capture's file name is sanitised to letters, digits, dash and underscore");
            Check(bridge.Contains("clean.Length >= 32"), "the sanitised name is bounded");
            // and the answer carries the FILE name, not the raw argument
            Check(bridge.Contains("\\\"name\\\":") && bridge.Contains("Json.Str(file)"),
                "the answer carries the file name it wrote, not the raw argument");

            // the refusal that used to block every capture is gone
            Check(!bridge.Contains("screenshots are excluded from telemetry"),
                "the local capture is not refused (the sharing rule is about SHARED reports)");
            // and it is still not part of the telemetry: the read-only set must not have gained it
            var readSet = Regex.Match(bridge, @"_readCmds\s*=\s*new[^;]*?\{(?<body>[^}]*)\}").Groups["body"].Value;
            Check(!read.Contains("screenshot") && !read.Contains("shot"),
                "the capture commands are not read-only commands (they change the disk)");
        }

        Console.WriteLine(fails == 0 ? $"all {checks} checks passed" : $"{fails} of {checks} checks failed");
        return fails == 0 ? 0 : 1;
    }
}

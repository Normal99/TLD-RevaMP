using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// The loader scanner's report is what a mod's author reads (docs/MODDING-MULTIPLAYER.md
/// §7).  The documentation lists what the report contains; the scanner decides what it
/// really contains.  This test keeps the two in step: every section the scanner can
/// write must be in the documentation's list, and the documentation must not promise a
/// section the scanner never writes.
static class LoaderScanDocTest
{
    static int failures;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string Read(string rel) => File.ReadAllText(Path.Combine(root, rel));
        string scan = Read("src/TLDRevamp/LoaderScan.cs");
        string docs = Read("docs/MODDING-MULTIPLAYER.md");

        // ---- 1. the scanner's section kinds ----
        var kinds = Regex.Matches(scan, @"\[Kind\.(\w+)\] = \(""([^""]+)""")
                          .Cast<Match>()
                          .Select(m => (Kind: m.Groups[1].Value, Head: m.Groups[2].Value))
                          .ToList();
        Check("the scanner has section kinds", kinds.Count >= 9);

        // ---- 2. the documentation's list of what the report contains ----
        int start = docs.IndexOf("The report lists:", StringComparison.Ordinal);
        int end = docs.IndexOf("The same scan runs without starting the game", start);
        Check("the documentation lists what the report contains", start >= 0 && end > start);
        string list = start >= 0 && end > start ? docs.Substring(start, end - start) : "";

        // Each section the scanner can write must be described in the list.  The
        // section head and the wording in the list are compared loosely on purpose:
        // the list is written in prose, the scanner in short headings.
        var expect = new (string Head, string[] Words)[]
        {
            ("Makes its own objects", new[] { "makes from its own assets" }),
            ("Adds components to objects", new[] { "components it adds" }),
            ("Reacts to keys or buttons", new[] { "keys and buttons it reacts to" }),
            ("Changes world-wide values", new[] { "world-wide values" }),
            ("Rolls random numbers", new[] { "random numbers" }),
            ("Writes the game's state directly", new[] { "game state it writes" }),
            ("Writes its own files", new[] { "files it writes" }),
            ("Makes the game's own things", new[] { "game's own things it spawns" }),
            ("Draws on screen", new[] { "only draws on screen" }),
        };
        foreach (var e in expect)
        {
            bool inCode = kinds.Any(k => k.Head == e.Head);
            bool inDocs = e.Words.Any(w => list.Contains(w));
            Check("the scanner writes '" + e.Head + "'", inCode);
            Check("the documentation lists '" + e.Words[0] + "'", inDocs);
            Check("the documentation's '" + e.Words[0] + "' matches a real section", inCode && inDocs);
        }

        // ---- 2b. the documentation must not promise a section the scanner never writes ----
        // Every bullet of the list must map onto a section the scanner can produce.
        // The list's first bullet is the porting check (checked below); the rest are
        // the section kinds.
        var listBullets = list.Split('\n')
                                .Select(l => l.Trim())
                                .Where(l => l.StartsWith("- "))
                                .ToList();
        Check("the list has a bullet for every section", listBullets.Count >= kinds.Count);
        foreach (string bullet in listBullets)
        {
            // the porting check is not a section kind; it is checked separately
            if (bullet.Contains("lifecycle overrides")) continue;
            // a bullet is satisfied when the scanner has a section whose wording the
            // bullet uses
            string b = bullet.TrimStart('-', ' ').ToLowerInvariant();
            bool covered = expect.Any(e => e.Words.Any(w => b.Contains(w.ToLowerInvariant())))
                           || expect.Any(e => b.Contains(e.Head.ToLowerInvariant()));
            Check("the list's bullet is a section the scanner writes: \"" + b.Substring(0, Math.Min(40, b.Length)) + "\"", covered);
        }

        // ---- 3. the report's first section: the porting check ----
        Check("the scanner writes the porting check", scan.Contains("## Porting check"));
        Check("the documentation mentions the porting check's contents",
            list.Contains("lifecycle overrides") && list.Contains("unresolved") && list.Contains("type-load failures"));
        Check("the scanner really reports the lifecycle overrides", scan.Contains("Lifecycle overrides:"));
        Check("the scanner really reports the unresolved count", scan.Contains("unresolved:"));
        Check("the scanner really reports type-load errors", scan.Contains("Type-load errors"));

        // ---- 4. the output paths and the way to run it ----
        Check("the scanner writes to BepInEx/TLDRevampModScan", scan.Contains("TLDRevampModScan"));
        Check("the documentation names the output folder", docs.Contains("BepInEx/TLDRevampModScan"));
        Check("the documentation gives the modscan command", docs.Contains("dotnet run --project tools/modscan"));
        Check("the modscan tool exists", File.Exists(Path.Combine(root, "tools/modscan/Program.cs")));

        // ---- 5. the startup switch ----
        string plugin = Read("src/TLDRevamp/Plugin.cs");
        Check("the scanner runs at startup when the setting is on",
            plugin.Contains("ScanLoaderMods") && plugin.Contains("LoaderScan.ScanAll()"));
        Check("the documentation names the setting", docs.Contains("[Mods] ScanLoaderMods = true"));

        // ---- 6. the promise that the scan never runs the mod ----
        Check("the scanner says it never runs the mod", scan.Contains("nothing is run") || scan.Contains("its code isn't run") || scan.Contains("never run"));
        Check("the documentation says the same", docs.Contains("never runs or rewrites the mod"));

        // ---- 7. the skeleton generator ----
        Check("the scanner writes a skeleton", scan.Contains("MpSkeleton"));
        Check("the documentation names the skeleton", docs.Contains("MpSkeleton.cs"));
        Check("the skeleton registers the mod", scan.Contains("RegisterMod"));

        // ---- 7b. the portability workflow: the report side of the claim ----
        // "Missing dependencies and changed game members are then errors in the
        // report and in the build": the report side is the unresolved count and
        // the UNRESOLVED marker on a dependency.
        Check("the scanner counts the member sites it cannot resolve",
            scan.Contains("rep.UnresolvedMembers++") && scan.Contains("unresolved:"));
        Check("the report marks a dependency it could not load",
            scan.Contains("UNRESOLVED") && scan.Contains("TypeLoadErrors"));
        Check("the report prints the unresolved count",
            scan.Contains("Referenced member sites:") && scan.Contains("unresolved: {r.UnresolvedMembers}"));
        // the build side of the same claim: the scanner's own tool compiles a mod
        // against the game's assemblies, so a renamed member is a build error
        Check("the modscan tool takes the game's assemblies", Read("tools/modscan/Program.cs").Contains("TLD_DIR"));

        // ---- 8. the mod browser catalog section of the same document ----
        string catalog = Read("src/TLDRevamp/Net/ModCatalog.cs");
        // the default catalog URL points at the dev branch's packaging/mods.json
        string url = Regex.Match(catalog, @"DefaultUrl = ""([^""]+)""").Groups[1].Value;
        Check("the default catalog URL is the dev branch's packaging/mods.json",
            url.Contains("refs/heads/dev/packaging/mods.json"));
        Check("the documentation says where the catalog comes from", docs.Contains("packaging/mods.json"));
        // the catalog's ids are the BepInEx plugin GUIDs
        Check("the catalog's Head Unit id is the plugin GUID",
            Read("src/Mods/HeadUnit/Plugin.cs").Contains("\"tldrevamp.headunit\"") &&
            Read("packaging/mods.json").Contains("tldrevamp.headunit"));
        // Head Unit's multiplayer registration id is a separate identifier
        Check("Head Unit's multiplayer id is 'headunit', not the GUID",
            Read("src/Mods/HeadUnit/Plugin.cs").Contains("RegisterMod(\"headunit\""));
        // the four states the browser shows
        string activation = Read("src/TLDRevamp/Net/ModActivation.cs");
        foreach (string state in new[] { "Enabled", "Disabled (takes effect at next start)", "Disabled", "Enabled at next start" })
        {
            bool inCode = state == "Enabled"
                ? activation.Contains("\"Enabled\"")
                : activation.Contains("\"" + state + "\"");
            Check("the browser really shows the state '" + state + "'", inCode);
        }
        // the protected components
        Check("RevaMP's own DLLs and BepInEx core are protected in the code",
            activation.Contains("TLDRevamp.Preloader") && activation.Contains("TLDLoader") && activation.Contains("bepinex"));
        // the preloader really is a patcher
        Check("the preloader is built as a patcher", File.Exists(Path.Combine(root, "src/TLDRevamp.Preloader/TLDRevamp.Preloader.csproj")));

        Console.WriteLine(failures == 0 ? "ALL PASS" : (failures + " FAILURE(S)"));
        return failures == 0 ? 0 : 1;
    }
}

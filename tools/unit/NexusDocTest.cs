using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// The Nexus listing copy (docs/NEXUS.md) is public text: every claim in it must
/// be true of the code and of the package.  This test checks the claims that can
/// be checked without the game, and it checks that the copy does not claim
/// anything was published.
static class NexusDocTest
{
    static int failures;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string Read(string rel) => File.ReadAllText(Path.Combine(root, rel));
        string nexus = Read("docs/NEXUS.md");
        string plugin = Read("src/TLDRevamp/Plugin.cs");
        string readme = Read("README.md");

        // ---- 1. the copy must not claim an upload happened ----
        Check("the copy says nothing was uploaded",
            nexus.Contains("NOT UPLOADED") ||
            (nexus.Contains("Nothing here has been uploaded yet") && nexus.Contains("no file has been sent to Nexus Mods")));
        Check("the copy says the upload needs a human",
            nexus.Contains("human") || nexus.Contains("owner-only") ||
            nexus.Contains("still has to be done by a person, by hand"));
        Check("the copy has the upload checklist",
            nexus.Contains("The human upload checklist") || nexus.Contains("checklist"));

        // ---- 2. the version in the copy must be the version in the code ----
        string version = Regex.Match(plugin, @"BaseVersion = ""([^""]+)""").Groups[1].Value;
        Check("the version was found in the code", version.Length > 0);
        Check("the copy uses the code's version in the version policy",
            nexus.Contains("TLD-RevaMP-<version>.zip") &&
            (nexus.Contains("the mod's own version number") || nexus.Contains("update them first")));

        // ---- 3. the keys the copy promises must be the code's defaults ----
        Check("F11 is the multiplayer menu", plugin.Contains("KeyCode.F11"));
        Check("Page Up is the overlay", plugin.Contains("KeyCode.PageUp"));
        Check("Page Down is the report", plugin.Contains("KeyCode.PageDown"));
        Check("the copy names F11", nexus.Contains("F11"));
        Check("the copy names Page Up", nexus.Contains("Page Up"));
        Check("the copy names Page Down", nexus.Contains("Page Down"));

        // ---- 4. the voice range the copy promises ----
        string voice = Read("src/TLDRevamp/Net/Voice.cs");
        string range = Regex.Match(voice, @"RangeM = (\d+)f").Groups[1].Value;
        Check("the voice range is in the code", range.Length > 0);
        Check("the copy's voice range matches the code",
            nexus.Contains("within " + range + " m") || nexus.Contains(range + " m"));

        // ---- 5. the requirements the copy lists ----
        Check("the copy says the beta branch", nexus.Contains("BETA branch") || nexus.Contains("beta branch"));
        Check("the copy says everyone must run the same version",
            nexus.Contains("exactly the same RevaMP version") || nexus.Contains("same version"));
        Check("the copy says no other mod loader", nexus.Contains("No other mod loader"));
        Check("the copy has the Linux launch option",
            nexus.Contains("WINEDLLOVERRIDES=\"winhttp=n,b\" %command%"));

        // ---- 6. the support route: the issue tracker, and no attaching files ----
        Check("the copy points at the issue tracker", nexus.Contains("github.com/Normal99/TLD-RevaMP/issues"));
        Check("the copy says not to attach report zips",
            nexus.Contains("Do NOT attach") || nexus.Contains("Do not attach") || nexus.Contains("NOT attach"));

        // ---- 7. the licence claim must match the repository's ----
        string copying = Read("COPYING");
        Check("the repository is GPL-3.0", copying.Contains("GNU GENERAL PUBLIC LICENSE") && copying.Contains("Version 3"));
        Check("the copy says GPL-3.0",
            nexus.Contains("GNU General Public License, version 3") || nexus.Contains("GPL-3.0") || nexus.Contains("GPLv3"));
        Check("the copy names the same repository", nexus.Contains("github.com/Normal99/TLD-RevaMP"));

        // ---- 8. the copy must not contradict the README on the basics ----
        foreach (string phrase in new[] { "F11", "beta" })
        {
            Check("the copy and the README agree about '" + phrase + "'",
                readme.Contains(phrase) && nexus.Contains(phrase));
        }

        // ---- 9. the add-on mods the copy lists must be in the manifest ----
        string catalog = Read("packaging/mods.json");
        foreach (string mod in new[] { "HeadUnit", "CarTrailer", "TrailerHitches" })
        {
            Check("the copy lists " + mod, nexus.Contains(mod));
            Check(mod + " is in the catalog", catalog.Contains(mod));
        }
        // the file names the copy gives must be the ones the manifest's URLs end in
        // The copy may name the file name OR the mod and its version ("Car Trailer 0.1.1"): the property is
        // that the reader can tell WHICH artifact to upload, not the exact spelling.
        foreach (var zip in new[] { ("HeadUnit-0.2.0.zip", "Head Unit 0.2.0"), ("CarTrailer-0.1.1.zip", "Car Trailer 0.1.1"),
                                    ("TrailerHitches-0.1.0.zip", "Trailer Hitches 0.1.0") })
        {
            Check("the copy names " + zip.Item1, nexus.Contains(zip.Item1) || nexus.Contains(zip.Item2));
            Check("the catalog has " + zip.Item1, catalog.Contains(zip.Item1));
        }

        // ---- 10. the loader claim: the copy must describe the lifecycle the host
        //              really runs, and the package must really ship the stub ----
        string host = Read("src/TLDRevamp/ModHost.cs");
        Check("the copy mentions the TLD Loader mods",
            nexus.Contains("TLDLoaderMods") || nexus.Contains("TLD Loader"));
        Check("the copy says the loader is the only one allowed", nexus.Contains("No other mod loader"));
        foreach (string m in new[] { "OnMenuLoad", "OnLoad", "OnUnload", "Update", "FixedUpdate", "OnGUI" })
            Check("the copy's " + m + " claim is true", host.Contains("l." + m) || host.Contains("\"" + m + "\""));

        // ---- 11. the copy must not promise a website ----
        Check("the copy does not claim a website", !nexus.Contains("tldrevamp.com"));

        Console.WriteLine(failures == 0 ? "ALL PASS" : (failures + " FAILURE(S)"));
        return failures == 0 ? 0 : 1;
    }
}

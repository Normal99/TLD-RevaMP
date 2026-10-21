using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// The beginner README must not promise a command that does not exist, and its
/// claims about the game and the package must be true.  This test checks the
/// commands and the paths it names, and the few claims that can be checked
/// without the game.
static class ReadmeCommandsTest
{
    static int failures;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }


    /// The executable bit, read from the file mode (the test runs on Linux).
    static bool IsExecutable(string path)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch { return false; }
    }

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string Read(string rel) => File.ReadAllText(Path.Combine(root, rel));
        string readme = Read("README.md");

        // ---- 1. every file and command the README names must exist ----
        // the commands in the "Building from source" block
        foreach (string cmd in new[] { "tools/libs.sh", "tools/package.sh" })
        {
            Check("the README's command exists: " + cmd, File.Exists(Path.Combine(root, cmd)));
            // the README must actually name it
            Check("the README names the command " + cmd, readme.Contains(cmd));
            // and it must be runnable: the executable bit is set
            var info = new FileInfo(Path.Combine(root, cmd));
            bool executable = false;
            try { executable = (info.Attributes & FileAttributes.ReadOnly) == 0 && IsExecutable(info.FullName); }
            catch { executable = false; }
            Check("the README's command is executable: " + cmd, executable);
        }
        foreach (string f in new[] { "installer/install.sh", "installer/install.ps1", "tools/pin-installer.sh" })
            Check("the README names an existing file: " + f, File.Exists(Path.Combine(root, f)));
        Check("the README names packaging/mods.json", File.Exists(Path.Combine(root, "packaging/mods.json")));
        Check("the README names packaging/approved-mods.txt", File.Exists(Path.Combine(root, "packaging/approved-mods.txt")));
        Check("the README names the licences folder", Directory.Exists(Path.Combine(root, "packaging/licenses")));

        // ---- 2. the launch option the README gives must be the one the installer sets ----
        string installSh = Read("installer/install.sh");
        Check("the README's launch option is the one the installer uses",
            readme.Contains("WINEDLLOVERRIDES=\"winhttp=n,b\" %command%") &&
            installSh.Contains("winhttp=n,b"));

        // ---- 3. the requirements ----
        Check("the README says the beta branch", readme.Contains("beta branch"));
        Check("the README says everyone needs the same version",
            readme.Contains("Everyone in a session needs the same version") ||
            readme.Contains("everyone in a session needs the same version") ||
            readme.Contains("same RevaMP version"));
        // the requirement is in the README in the beginner's words and in INSTALL.txt
        // in the installer's words; both must be there
        Check("the README says no other mod loader",
            readme.Contains("no other mod loader") || readme.Contains("No other mod loader") ||
            readme.Contains("no other MOD LOADER"));
        Check("INSTALL.txt says no other mod loader",
            Read("packaging/INSTALL.txt").Contains("No other mod loader"));

        // ---- 4. the keys the README names must be the code's defaults ----
        string plugin = Read("src/TLDRevamp/Plugin.cs");
        foreach (var pair in new[] { ("F11", "MultiplayerScreen"), ("PageUp", "PerformanceOverlay"),
                                     ("PageDown", "DebugReport"), ("Insert", "ItemTraffic"),
                                     ("ScrollLock", "Mark"), ("LeftControl", "ReproRecorder") })
        {
            Check("the README's key " + pair.Item1 + " is bound in the code",
                plugin.Contains("KeyCode." + pair.Item1) && plugin.Contains("\"" + pair.Item2 + "\""));
        }
        foreach (string key in new[] { "F11", "Page Up", "Page Down", "Insert", "Scroll Lock" })
            Check("the README names the key " + key, readme.Contains(key));

        // ---- 5. the paths the README names must be the code's paths ----
        string feedback = Read("src/TLDRevamp/FeedbackReporter.cs");
        Check("the report folder claim is the code's folder", feedback.Contains("tldrevamp-feedback"));
        Check("the README names the report folder", readme.Contains("BepInEx/tldrevamp-feedback/"));
        string activation = Read("src/TLDRevamp/Net/ModActivation.cs");
        Check("the disabled-mods claim is the code's folder", activation.Contains("disabled-mods"));
        Check("the README names the disabled-mods folder", readme.Contains("BepInEx/disabled-mods/"));
        Check("the README names the managed plugins folder", readme.Contains("BepInEx/plugins/TLDRevampMods"));
        Check("the README names the preloader folder", readme.Contains("BepInEx/patchers/TLDRevamp"));

        // ---- 6. the catalogue's own rules the README states ----
        string catalog = Read("src/TLDRevamp/Net/ModCatalog.cs");
        Check("the catalogue really only takes GitHub release assets",
            catalog.Contains("releases/download") &&
            (readme.Contains("Only GitHub release assets are accepted") ||
             readme.Contains("GitHub release assets") || readme.Contains("release assets")));
        Check("the catalogue really needs the local approved list",
            catalog.Contains("approved.TryGetValue") &&
            (readme.Contains("approved-mods.txt") || readme.Contains("approved list")));
        Check("the README's approved list name is the real file",
            readme.Contains("approved-mods.txt") || readme.Contains("approved list"));

        // ---- 7. the licence claim ----
        Check("the README says GPL-3.0", readme.Contains("GNU General Public License, version 3"));
        Check("the README points at the licence file", readme.Contains("COPYING") && File.Exists(Path.Combine(root, "COPYING")));

        // ---- 8. the support route ----
        Check("the README points at the issue tracker", readme.Contains("issues"));
        Check("the README tells the player not to attach files to a public issue",
            readme.Contains("do not attach them") || readme.Contains("Do not attach"));

        // ---- 9. the troubleshooting section: every remedy it names must be real ----
        // The README must name the installer's OWN option. The prose may also say "game folder" in
        // words, but the OPTION has to be there: a reader cannot type "game folder".
        Check("the README's game-dir remedy is the installer's option",
            installSh.Contains("--game-dir") && readme.Contains("--game-dir"));
        Check("the README's from-folder remedy is the installer's option",
            installSh.Contains("--from") && (readme.Contains("--from") || readme.Contains("from a folder")));
        Check("the README's 'the game is running' message is the installer's",
            installSh.Contains("The Long Drive is running") &&
            (readme.Contains("says the game is running") || readme.Contains("game is running") ||
             readme.Contains("close the game")));
        Check("the README's SHA-256 message is the installer's",
            installSh.Contains("isn't the one this installer was written for") &&
            (readme.Contains("SHA-256 does not match") || readme.Contains("checksum") ||
             readme.Contains("SHA-256")));
        // the overlay: the README must not claim a behaviour the overlay does not have
        string overlay = Read("src/TLDRevamp/Overlay.cs");
        Check("the README does not claim the overlay hides during a loading screen",
            !readme.ToLowerInvariant().Contains("overlay is off during a loading screen"));
        Check("the overlay is only drawn on Repaint", overlay.Contains("EventType.Repaint"));
        // the joining screen's own words
        string joining = Read("src/TLDRevamp/Net/JoinLoadingView.cs"), joiningState = Read("src/TLDRevamp/Net/JoinLoadingState.cs");
        Check("the README's 60 seconds is the joining screen's", joining.Contains("No progress for 60 s") && joiningState.Contains("now - Changed >= 60") && readme.Contains("60 seconds without progress"));
        Check("the README's continue-anywhere is the joining screen's", joining.Contains("Continue anyway") && readme.Contains("Continue anyway"));
        Check("the README's Esc cancel is the joining screen's", joining.Contains("Cancel (Esc)") && readme.Contains("Esc"));
        // the version refusal
        string join = Read("src/TLDRevamp/Net/JoinChecks.cs");
        Check("the README's version-mismatch claim is the code's", join.Contains("Both need the same version"));
        // the catalog's grey entry
        string catalog2 = Read("src/TLDRevamp/Net/ModCatalog.cs");
        Check("the README's grey-entry claim is the code's",
            catalog2.Contains("Not approved in this RevaMP version") &&
            (readme.Contains("grey entry") || readme.Contains("greyed out") || readme.Contains("not approved")));

        // ---- 10. the first-session walkthrough: every step must be a real action ----
        Check("the README walks a beginner through a first session",
            readme.Contains("Your first session") || readme.Contains("Your first multiplayer session"));
        Check("the walkthrough opens the multiplayer menu with F11", readme.Contains("F11"));
        Check("the walkthrough mentions hosting and joining", readme.Contains("Host") && readme.Contains("Join"));
        // the host password is optional, and the menu says so
        Check("the host password is optional in the code", Read("src/TLDRevamp/Net/MpScreen.cs").Contains("blank = none"));
        Check("the README says the password is optional", readme.Contains("password is optional"));
        // the version is in the top-left overlay
        string overlaySrc = Read("src/TLDRevamp/Overlay.cs");
        Check("the overlay carries the version", overlaySrc.Contains("{Plugin.Version} ({Plugin.CommitLabel})"));
        Check("the overlay is drawn at the top left", overlaySrc.Contains("DebugLayout.LeftWidth(") && overlaySrc.Contains("DebugLayout.Performance"));
        Check("the README says the version is in the top-left overlay",
            readme.Contains("top-left overlay") || readme.Contains("top-left corner"));
        // chat and voice keys
        Check("the README says Enter chats",
            readme.Contains("**Enter** to chat") || readme.Contains("**Enter**") ||
            readme.Contains("Press **Enter**"));
        Check("the README says the voice key is K if unbound",
            readme.Contains("K if it is not bound") || readme.Contains("**K** if"));
        Check("the voice range is the code's", Read("src/TLDRevamp/Net/Voice.cs").Contains("RangeM = 60f") && readme.Contains("60 m"));

        // ---- 11. the README must not promise a website ----
        Check("the README does not claim a website", !readme.Contains("tldrevamp.com"));

        // ---- 10. the building block: the two commands must really be the ones that build the release ----
        string packageSh = Read("tools/package.sh");
        Check("package.sh really writes the zip the README names",
            packageSh.Contains("TLD-RevaMP-$VER.zip") && readme.Contains("dist/TLD-RevaMP-<version>.zip"));
        Check("libs.sh really writes lib/", Read("tools/libs.sh").Contains("lib/") && readme.Contains("lib/"));

        Console.WriteLine(failures == 0 ? "ALL PASS" : (failures + " FAILURE(S)"));
        return failures == 0 ? 0 : 1;
    }
}

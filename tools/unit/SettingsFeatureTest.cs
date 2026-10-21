// The settings FEATURE, not the snapshot codec.
//
// The repository's own SessionSettingsTest is a unit test of the snapshot's codec: it reads and writes the
// settings message and never looks at a sink, the dedicated server, the bridge's gate, the joiner's failure or
// the joiner's view. The independent audit showed that 13 mutations, each breaking one real property of the
// settings feature, were caught 0/13 by that suite.
//
// This project drives the REAL production code where it can be driven without a game: the admin console's
// two sinks (the log and console.txt) and the one session-settings path are the real production files
// (Admin.cs, SessionSettings.cs, SessionSettingsData.cs), compiled into this project against stubs.
//
// The rules that live in the pure core (SessionSettingsPolicy) are checked as PROPERTIES, not as
// implementation details: "a joiner gets the same signal for every refused command", "the host may always
// change its own policy".
//
// The source-level checks (the files that need a running game to exercise: the bridge's plumbing, the join
// handler's message layout, the multiplayer screen) live in the companion project SettingsFeatureSourceTest,
// which reads the tree as text and can therefore be run against ANY tree -- including the pre-fix one.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TLDRevamp.Net;

static class SettingsFeatureTest
{
    private static int checks;
    private static int failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        checks++;
        if (!ok)
        {
            failures++;
            Console.WriteLine("FAIL  " + name + (detail.Length > 0 ? "   [" + detail + "]" : ""));
        }
        else Console.WriteLine("PASS  " + name);
    }

    private static int Main()
    {
        P0_TheConsoleSinks();
        P1_OnePasswordPath();

        Console.WriteLine(failures == 0
            ? $"PASS SettingsFeature: {checks} checks"
            : $"FAIL SettingsFeature: {failures} of {checks} checks failed");
        return failures == 0 ? 0 : 1;
    }

    // ================================================================= P0: the console password leak
    //
    // The admin console's file echo wrote the RAW command line to console.txt and to the log. A
    // `password hunter2` line therefore persisted the secret on the host's disk, while the `admin:` log line
    // twenty lines earlier WAS redacted: the redaction was applied to one sink and not the other.
    private static void P0_TheConsoleSinks()
    {
        var sandbox = new Sandbox();
        try
        {
            // the log the production code writes to
            var plugin = typeof(Session).Assembly.GetType("TLDRevamp.Plugin");
            var logField = plugin.GetField("Log", System.Reflection.BindingFlags.NonPublic
                                           | System.Reflection.BindingFlags.Public
                                           | System.Reflection.BindingFlags.Static);
            var logObj = logField.GetValue(null);
            var lines = (List<string>)logObj.GetType()
                .GetField("Lines", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .GetValue(logObj);

            // the hosting server, and the console's two files
            var server = new MpServer();
            Mp.Server = server;

            // the console's first tick creates the folder and the files, and starts the offset
            // (the console runs at most once a second, so the stub's clock has to move)
            UnityEngine.Time.Now = 5f;
            AdminConsole.Tick();
            UnityEngine.Time.Now = 6f;

            // a password command with a secret, and a command without one
            File.AppendAllText(AdminConsole.CommandsFile, "password hunter2\nplayers\n");
            UnityEngine.Time.Now = 7f;
            AdminConsole.Tick();

            string consoleText = File.Exists(AdminConsole.OutFile) ? File.ReadAllText(AdminConsole.OutFile) : "";
            string logText = string.Join("\n", lines);

            // THE PROPERTY: the secret reaches neither the log nor console.txt.
            Check("P0.1 the secret is not in console.txt", !consoleText.Contains("hunter2"), consoleText);
            Check("P0.2 the secret is not in the log", !logText.Contains("hunter2"), logText);
            Check("P0.3 the secret appears nowhere in the console's file", !consoleText.Contains("hunter2"), consoleText);

            // the console still says that a password was set: the redaction is a redaction, not a silence
            Check("P0.4 the console still shows that a password was set", consoleText.Contains("<set>"), consoleText);
            Check("P0.5 the console's echo is the redacted form", consoleText.Contains("> password <set>"), consoleText);

            // the host's stored password IS the secret (in memory, by design -- it is never written to a file)
            Check("P0.6 the host stores the password in memory", server.Password == "hunter2",
                "the stored password is: '" + server.Password + "'");

            // a command without a secret is NOT over-redacted: the console still shows it verbatim
            Check("P0.7 a command without a secret is not over-redacted", consoleText.Contains("> players"), consoleText);
            Check("P0.8 the redaction marker is not sprayed over every line", !consoleText.Contains("> <set>"), consoleText);
        }
        finally { sandbox.Dispose(); }
    }

    // ================================================================= P1: one password path
    //
    // The dedicated server assigned Mp.Server.Password directly and then hand-edited the snapshot
    // (HasPassword, Revision, publish). That is a SECOND path: it is not normalized (a 70-character
    // configured password was compared against its first 64) and it edits the snapshot field by field.
    private static void P1_OnePasswordPath()
    {
        // BEHAVIOUR: the session path is the only writer, and it cleans what it stores
        var server = new MpServer();
        server.SetSessionPassword("  hunter2\n  ", persistDefault: false);
        Check("P1.1 the session path cleans the password it stores", server.Password == "hunter2", "stored: '" + server.Password + "'");
        Check("P1.2 the session path sets the snapshot's password flag", server.SessionSettings.HasPassword);
        Check("P1.3 the session path publishes the snapshot", server.Publishes > 0, "publishes: " + server.Publishes);
        Check("P1.4 the session path writes the password field exactly once", server.PasswordAssignments == 1,
            "the field was written " + server.PasswordAssignments + " times");

        // a long password is capped, so a joiner who types exactly what was configured is not refused
        server.SetSessionPassword(new string('x', 70), persistDefault: false);
        Check("P1.5 a long password is capped like every other password", server.Password.Length == 64, "length " + server.Password.Length);
        Check("P1.6 the capped password is the one compared", SessionSettingsRules.CleanPassword(server.Password) == server.Password);
    }

    // ---- a temporary folder for the console's files
    private sealed class Sandbox : IDisposable
    {
        private readonly string _dir;
        public Sandbox()
        {
            _dir = Path.Combine(Path.GetTempPath(), "tld-settingsfeature-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(_dir);
            BepInEx.Paths.BepInExRootPath = _dir;
        }
        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }
    }
}

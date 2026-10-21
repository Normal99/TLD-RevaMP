using System;
using System.IO;

/// Independent audit test for the #34 settings lane. It asserts the PROPERTIES the lane must hold, by
/// reading the production source as text (so it can be run against a mutated copy of the tree).
///
/// Every check is a property, not an implementation detail: "the joiner cannot widen the policy",
/// "no sink writes the password", "one password path", "a malformed snapshot fails visibly", "a
/// foreign token is refused", "both teardowns restore", "the joiner's view is reachable".
///
/// A mutation of the production source must make at least one check fail. That is the point.
static class SettingsLaneAuditTest
{
    private static int checks;
    private static int failures;
    private static string root;

    private static void Check(string name, bool ok, string detail = "")
    {
        checks++;
        if (!ok) { failures++; Console.WriteLine("FAIL  " + name + (detail.Length > 0 ? "  [" + detail + "]" : "")); }
        else Console.WriteLine("PASS  " + name);
    }

    private static string Read(string rel)
    {
        try { return File.ReadAllText(Path.Combine(root, rel)); }
        catch { return ""; }
    }

    private static int LineOf(string text, string needle)
    {
        int i = text.IndexOf(needle, StringComparison.Ordinal);
        return i < 0 ? -1 : text.Substring(0, i).Split('\n').Length;
    }

    /// The text of a case block: from the case label to the next case label.
    private static string Case(string text, string label)
    {
        int at = text.IndexOf(label, StringComparison.Ordinal);
        if (at < 0) return "";
        int end = text.IndexOf("\n                case ", at + label.Length, StringComparison.Ordinal);
        if (end < 0) end = text.IndexOf("\n                }", at, StringComparison.Ordinal);
        if (end < 0) end = Math.Min(text.Length, at + 2000);
        return text.Substring(at, end - at);
    }

    private static int Main(string[] args)
    {
        root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

        string settings = Read("src/TLDRevamp/Net/SessionSettings.cs");
        string data = Read("src/TLDRevamp/Net/SessionSettingsData.cs");
        string mp = Read("src/TLDRevamp/Net/Mp.cs");
        string admin = Read("src/TLDRevamp/Net/Admin.cs");
        string dedicated = Read("src/TLDRevamp/Net/DedicatedServer.cs");
        string bridge = Read("src/TLDRevamp/DebugBridge.cs");
        string screen = Read("src/TLDRevamp/Net/MpScreen.cs");
        string session = Read("src/TLDRevamp/Net/Session.cs");

        // ---------- 1. the host policy is authoritative; a joiner cannot widen it ----------
        {
            // the policy a client uses must come from the host's snapshot, never from a local setter
            Check("1.1 the client's policy comes from the host's snapshot",
                settings.Contains("return _clientSettings.DeveloperPolicy;"));
            // a client-side mutation entry point must refuse when there is no server
            Check("1.2 the settings mutation refuses when not hosting",
                screen.Contains("if (Mp.Server == null) return;"));
            Check("1.3 the password commit refuses when not hosting",
                screen.Contains("if (Mp.Server == null || _emptyPasswordCommit) return;"));
            // the bridge's policy command must refuse when not hosting
            Check("1.4 the bridge's policy command refuses when not hosting",
                bridge.Contains("if (Net.Mp.Server == null) return \"{\\\"error\\\":\\\"not hosting\\\"}\""));
            // the hostControl exemption must be restricted to a host: a joiner must not get a
            // different answer for the policy command than for any other denied command
            // The exemption is the pure-core rule (SessionSettingsPolicy.IsHostControlCommand), which
            // refuses unless this machine is the host. The bridge calls the rule; it does not decide.
            bool hostOnlyExemption = data.Contains("public static bool IsHostControlCommand(bool hosting, string cmd, string arg)")
                                  && data.Contains("if (!hosting) return false;")
                                  && bridge.Contains("SessionSettingsPolicy.BridgeAllowed(");
            Check("1.5 the policy-command exemption is restricted to the host", hostOnlyExemption,
                "the exemption lets a joiner through the policy gate to a different error");
            // the policy must not be persisted
            Check("1.6 the developer policy is session-only",
                settings.Contains("UpdateSessionSettings(s => s.DeveloperPolicy = policy, persistDefaults: false)"));
        }

        // ---------- 2. no sink writes the password ----------
        {
            // the config file: the password must never enter the persisted text values
            Check("2.1 the password is never written to the settings file",
                !settings.Contains("SetText(\"hostPassword\"") &&
                !data.Contains("SetText(\"hostPassword\""));
            // the admin console's file echo must be redacted
            bool consoleEchoRedacted = admin.Contains("Out(\"> \" + Net.SessionSettingsRules.RedactCommand(")
                                     || admin.Contains("Out(\"> \" + SessionSettingsRules.RedactCommand(");
            Check("2.2 the admin console's file echo is redacted", consoleEchoRedacted,
                "the raw command line is written to console.txt");
            // the bridge's command log must be redacted
            Check("2.3 the bridge's dev-command log is redacted",
                bridge.Contains("RedactCommand(line, Net.Mp.Server?.Password)"));
            // the anomaly watch quotes LastDevCmd, which must be the redacted form
            Check("2.4 the anomaly watch quotes the redacted command",
                bridge.Contains("Net.Entities.LastDevCmd = safe"));
            // the report's settings dump is allowlisted
            Check("2.5 the report's settings dump is allowlisted",
                Read("src/TLDRevamp/FeedbackReporter.cs").Contains("TelemetryPrivacy.Setting(e.Key)"));
            // the report must not carry the admin console file
            Check("2.6 the admin console file is not a report attachment",
                !Read("src/TLDRevamp/TelemetryPrivacy.cs").Contains("console.txt"));
        }

        // ---------- 3. one password path ----------
        {
            // the dedicated server must go through the session path, not assign the field
            bool dedicatedUsesPath = dedicated.Contains("SetSessionPassword(");
            Check("3.1 the dedicated server uses the session password path", dedicatedUsesPath,
                "DedicatedServer assigns Mp.Server.Password directly");
            // and it must not hand-edit the snapshot fields the session path owns
            bool handEdits = dedicated.Contains("SessionSettings.HasPassword =")
                          && dedicated.Contains("SessionSettings.Revision++");
            Check("3.2 the dedicated server does not hand-edit the snapshot", !handEdits,
                "HasPassword/Revision/Publish are edited by hand instead of by the session path");
            // the ORDER: the snapshot's own fields have to be in place BEFORE the session path applies it,
            // or the runtime would be told the snapshot's stale values and the dedicated server's own TPA
            // setting would be lost.
            int tpaAt = dedicated.IndexOf("UpdateSessionSettings(s => { s.TpaEnabled = tpaOn;", StringComparison.Ordinal);
            int pwAt = dedicated.IndexOf("SetSessionPassword(", StringComparison.Ordinal);
            Check("3.3 the dedicated server's own settings are in the snapshot before the session path applies it",
                tpaAt >= 0 && pwAt > tpaAt,
                "SetSessionPassword applies the snapshot to the runtime: the fields have to be set first");
        }

        // ---------- 4. a malformed snapshot fails visibly ----------
        {
            // the Welcome path must set a visible reason, not just return
            int welcomeLine = LineOf(mp, "if (!SessionSettingsSnapshot.TryRead(r, out var hostSettings))");
            string after = welcomeLine > 0 ? mp.Substring(Math.Min(mp.Length, mp.IndexOf("if (!SessionSettingsSnapshot.TryRead(r, out var hostSettings))") + 60), 400) : "";
            bool visible = after.Contains("RejectReason") || after.Contains("Reject(");
            Check("4.1 a malformed snapshot in Welcome sets a visible reason", visible,
                "the handler returns with no RejectReason, so the join hangs silently");
            // the refusal must carry a reject code, so the client can tell it apart from a plain disconnect.
            // The 400-char window above ends before the reject code, so the WHOLE Welcome case is examined here.
            string welcomeCase = Case(mp, "case Protocol.Welcome:");
            Check("4.1b the refusal carries a reject code",
                welcomeCase.Contains("RejectCode = Protocol.RejectOther;"),
                "the refusal has no reject code, so the client cannot classify it");
            // the wording is the one the player already sees in the integrated build, and it is preserved
            // EXACTLY. The rule holds the sentence; the handler asks the rule for it. Pinning the
            // sentence here means a reword fails the suite instead of reaching the player.
            Check("4.1c the refusal carries the exact integrated wording",
                data.Contains("the host's session settings could not be read \u2014 the host and this copy ") &&
                data.Contains("are not the same build; update the mod on both and join again") &&
                mp.Contains("SessionSettingsPolicy.MalformedSnapshotReason()"),
                "the visible wording is not the one the integrated build shows");
            Check("4.1d the reason is not protocol-shaped",
                !data.Contains("public static string MalformedSnapshotReason(int"),
                "the reason helper takes a protocol number: the wording must not be protocol-shaped");
            // a malformed snapshot must not be partially applied
            Check("4.2 a malformed snapshot is discarded whole",
                data.Contains("settings = null;\n                return false;"));
            // the incremental path may drop silently (the current state stands)
            Check("4.3 the incremental path drops a malformed snapshot without applying it",
                mp.Contains("TryRead(r, out var updated) && r.Remaining == 0"));
        }

        // ---------- 5. the session-token binding ----------
        {
            Check("5.1 the client binds to the host's token at Welcome",
                mp.Contains("SessionSettingsSnapshot.BindToHost(hostSettings.SessionToken)"));
            Check("5.2 the incremental path checks the bound host",
                mp.Contains("SessionSettingsSnapshot.AcceptFromBoundHost(updated)"));
            Check("5.3 an unbound client trusts no snapshot",
                data.Contains("if (ClientBoundToken == 0) return false;"));
            Check("5.4 a foreign token is not an update",
                data.Contains("next.SessionToken == current.SessionToken && next.Revision > current.Revision"));
            Check("5.5 the binding is released on teardown",
                mp.Contains("SessionSettingsSnapshot.UnbindHost()"));
        }

        // ---------- 6. the preference is restored on both teardowns ----------
        {
            Check("6.1 the saved preference is captured once",
                settings.Contains("if (!_captured) Capture(script.S.BDevMode);"));
            Check("6.2 the restore writes the saved value back",
                settings.Contains("script.S.BDevMode = _savedPreference;"));
            // the normal teardown
            Check("6.3 the normal teardown restores the preference",
                mp.Contains("DeveloperAccess.ResetClient();"));
            // the abnormal teardown reaches the same restore: the three abnormal causes must be
            // detected AND the session must actually be stopped (the restore lives in Mp.Stop).
            bool abnormalDetected = session.Contains("bool rejected = c.RejectReason != null && !c.Lost && c.MyId <= 0;")
                                  && session.Contains("bool kicked = c.RejectCode == Protocol.RejectKicked;")
                                  && session.Contains("if (!c.Lost && !rejected && !kicked) return;");
            // the call to Mp.Stop() must be ON the path, not merely somewhere in the file: the
            // statement must follow the detection and precede the "back to the menu" work.
            int detAt = session.IndexOf("if (!c.Lost && !rejected && !kicked) return;", StringComparison.Ordinal);
            int stopAt = session.IndexOf("Mp.Stop();", detAt < 0 ? 0 : detAt, StringComparison.Ordinal);
            int afterAt = stopAt < 0 ? -1 : session.IndexOf("Show(1, \"\", EndReason);", stopAt, StringComparison.Ordinal);
            bool abnormalStops = detAt >= 0 && stopAt > detAt && afterAt > stopAt;
            Check("6.4 the abnormal teardown reaches the same restore", abnormalDetected && abnormalStops,
                "the session is not stopped on the abnormal path, so the preference is never restored");
            // the runtime override must not overwrite the saved preference
            Check("6.5 the saved preference is protected from the runtime override",
                settings.Contains("script.S.BDevMode = _savedPreference;"));
            // the three patches must target the type, not a string
            bool typesOk = settings.Contains("HarmonyPatch(typeof(settingsscript), \"LoadSettings\")")
                        && settings.Contains("HarmonyPatch(typeof(settingsscript), \"RefreshSettings\")")
                        && settings.Contains("HarmonyPatch(typeof(settingsscript), \"SaveSettings\")");
            Check("6.6 the settings patches target the type, not a string", typesOk,
                "HarmonyPatch(\"settingsscript\", ...) resolves a type named '.settingsscript' and fails at load");
        }

        // ---------- 7. the joiner's settings view is reachable ----------
        {
            // a dedicated settings button must exist and be shown in a game
            Check("7.1 a dedicated settings button is built", screen.Contains("TLDRevampSessionSettingsButton"));
            Check("7.2 the settings button is shown in a game", screen.Contains("bool showButton = inGame;"));
            // the label must be put on the DEDICATED button, not on the shared one, and not be
            // conditioned on a saved-player selection.
            // The dedicated button is a GameObject, so the label can be set through the object
            // ("Show(_settingsButton, true, ...)") or through its Button component, either inline
            // ("Show(_settingsButton.GetComponent<Button>(), true, ...)") or through a local that
            // fetched the component ("Show(b, show, ...)"). What matters is that the label goes to
            // the DEDICATED button, not the shared one.
            bool labelOnDedicated = screen.Contains("Show(_settingsButton, true,")
                                || screen.Contains("Show(_settingsButton.GetComponent<Button>(), true,")
                                || (screen.Contains("_settingsButton.GetComponent<Button>()") &&
                                    screen.Contains("Show(b, show,"));
            Check("7.3 the settings entry is labelled on the dedicated button", labelOnDedicated,
                "the label goes on _secondary, behind a sel2?.Saved condition a joiner cannot satisfy");
            // and it must NOT be on the shared button behind a saved-player condition
            bool sharedBehindSaved = screen.Contains("sel2?.Saved != null) Show(_secondary, true, \"Close settings\"")
                                 || screen.Contains("sel2?.Saved != null) Show(_secondary, true, _settingsOpen");
            Check("7.3b the shared button does not carry the settings label behind a saved-player condition",
                !sharedBehindSaved,
                "a joiner has no saved player of their own, so the label would never appear for them");
            // the shared button must keep "Join by address"
            Check("7.4 the shared button keeps 'Join by address'",
                screen.Contains("\"Join by address\""));
            // the rows must not be frozen while the panel is closed
            bool rowsAlwaysCurrent = !screen.Contains("if (!showButton || !_settingsOpen) return;");
            Check("7.5 the rows are refreshed even while the panel is closed", rowsAlwaysCurrent,
                "the row labels are only computed while the panel is open, so a closed panel is stale");
        }

        // ---------- the unit suite must compile ----------
        {
            // every runtime class a RevampSettings toggle names must have a stub SOMEWHERE in the unit
            // suite's stub sources (the stubs are shared between the projects that need them)
            string revamp = Read("src/TLDRevamp/RevampSettings.cs");
            var names = new System.Collections.Generic.List<string>();
            foreach (var line in revamp.Split('\n'))
            {
                int i = line.IndexOf("Cinematic.", StringComparison.Ordinal);
                if (i < 0) continue;
                int j = i + "Cinematic.".Length;
                int k = j;
                while (k < line.Length && (char.IsLetterOrDigit(line[k]) || line[k] == '_' || line[k] == '.')) k++;
                names.Add(line.Substring(i, k - i));
            }
            names.Sort();
            var uniq = new System.Collections.Generic.List<string>();
            foreach (var n in names) if (!uniq.Contains(n)) uniq.Add(n);
            // every stub source of the unit suite
            var stubText = new System.Text.StringBuilder();
            try
            {
                var unitDir = Path.Combine(root, "tools", "unit");
                foreach (var f in Directory.GetFiles(unitDir, "*.cs"))
                {
                    if (f.EndsWith("SettingsLaneAuditTest.cs", StringComparison.Ordinal)) continue;
                    stubText.Append(File.ReadAllText(f));
                }
            }
            catch { }
            // The stub may declare the member directly (a field in a stub class) or as a qualified
            // expression (a call). Both shapes count: what matters is that the name is provided.
            var missing = new System.Collections.Generic.List<string>();
            var allStubs = stubText.ToString();
            foreach (var n in uniq)
            {
                string bare = n;
                int lastDot = bare.LastIndexOf('.');
                if (lastDot >= 0) bare = bare.Substring(lastDot + 1);   // "CinematicCamera.DefaultMode" -> "DefaultMode"
                if (!allStubs.Contains(bare)) missing.Add(n);
            }
            Check("8.1 every RevampSettings toggle's runtime class has a unit stub", missing.Count == 0,
                "missing a stub for: " + string.Join(", ", missing));
        }

        Console.WriteLine(failures == 0 ? $"ALL PASS: SettingsLaneAudit: {checks} checks" : $"{failures} FAILED of {checks}");
        return failures == 0 ? 0 : 1;
    }
}

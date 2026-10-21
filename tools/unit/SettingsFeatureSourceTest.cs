// The settings feature's source-level properties.
//
// These are the parts of the feature that need a running game to exercise: the bridge's plumbing, the join
// handler's message layout, the multiplayer screen and the dedicated server. The project reads the TREE as
// text, so it can be run against ANY tree -- including the pre-fix one -- and it is the proof that each fix
// is caught by a test rather than by a review.
//
// A check here is a PROPERTY, not an implementation detail. Where a property could be expressed in the pure
// core instead, it is: the companion project SettingsFeatureTest drives that core as code, and these checks
// only cover what genuinely needs the game's plumbing.

using System;
using System.IO;
using System.Linq;

static class SettingsFeatureSourceTest
{
    private static int checks;
    private static int failures;
    private static string root;

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

    private static string Read(string rel)
    {
        try { return File.ReadAllText(Path.Combine(root, rel)); }
        catch { return ""; }
    }

    /// The text of a method: from its signature to the next method at the same indentation.
    private static string Method(string text, string signature)
    {
        int at = text.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return "";
        int end = text.IndexOf("\n        }", at, StringComparison.Ordinal);
        if (end < 0) return text.Substring(at, Math.Min(2000, text.Length - at));
        return text.Substring(at, Math.Min(4000, end - at));
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
        root = args.Length > 0 ? args[0] : FindRoot();
        Console.WriteLine("root: " + root);

        string admin = Read("src/TLDRevamp/Net/Admin.cs");
        string dedicated = Read("src/TLDRevamp/Net/DedicatedServer.cs");
        string bridge = Read("src/TLDRevamp/DebugBridge.cs");
        string mp = Read("src/TLDRevamp/Net/Mp.cs");
        string screen = Read("src/TLDRevamp/Net/MpScreen.cs");
        string settings = Read("src/TLDRevamp/Net/SessionSettings.cs");
        string data = Read("src/TLDRevamp/Net/SessionSettingsData.cs");

        P0_Sinks(admin, data);
        P1_OnePasswordPath(dedicated, settings);
        P1_TheGate(bridge, data);
        P1_MalformedSnapshot(mp, data);
        P1_TheJoinsView(screen, mp);

        Console.WriteLine(failures == 0
            ? $"PASS SettingsFeatureSource: {checks} checks"
            : $"FAIL SettingsFeatureSource: {failures} of {checks} checks failed");
        return failures == 0 ? 0 : 1;
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "TLDRevamp"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    // ---------------------------------------------------------------- P0: the console's sinks
    private static void P0_Sinks(string admin, string data)
    {
        // The console's echo of the command line is redacted. Without this, `password hunter2` is written
        // verbatim to console.txt AND to the log, while the `admin:` log line twenty lines earlier is
        // redacted: the redaction was applied to one sink and not the other.
        Check("S-P0.1 the admin console's echo is redacted",
            admin.Contains("Out(\"> \" + Net.SessionSettingsRules.RedactCommand(line, Mp.Server?.Password))")
            || admin.Contains("Out(\"> \" + SessionSettingsRules.RedactCommand(line, Mp.Server?.Password))"),
            "the raw command line is written to console.txt");

        // there is exactly ONE echo of the command line (a second one would be unredacted)
        int echoes = admin.Split('\n').Count(l => l.Contains("Out(\"> \""));
        Check("S-P0.2 the console echoes the command line exactly once", echoes == 1, "found " + echoes + " echoes");

        // the `admin:` log line is redacted too (it already was, but the two must agree)
        Check("S-P0.3 the admin log line is redacted",
            admin.Contains("SessionSettingsRules.RedactSecret(arg, Password)")
            || admin.Contains("SessionSettingsRules.RedactedPassword"));

        // the console's file is not a report attachment
        Check("S-P0.4 the console's file is not in the report", !Read("src/TLDRevamp/TelemetryPrivacy.cs").Contains("console.txt"));

        // the redaction helper the echo uses is the one that exists
        Check("S-P0.5 the redaction helper exists",
            data.Contains("public static string RedactCommand(string line, string currentPassword, string[] secretCommands = null)"));
    }

    // ---------------------------------------------------------------- P1: one password path
    private static void P1_OnePasswordPath(string dedicated, string settings)
    {
        Check("S-P1.1 the dedicated server uses the session password path",
            dedicated.Contains("SetSessionPassword("),
            "DedicatedServer assigns Mp.Server.Password directly");
        Check("S-P1.2 the dedicated server does not assign the password field",
            !dedicated.Contains("Mp.Server.Password = "),
            "the raw assignment is a second, unnormalized path");
        Check("S-P1.3 the dedicated server does not hand-edit the snapshot's password flag",
            !dedicated.Contains("SessionSettings.HasPassword"),
            "HasPassword is owned by the session path");
        Check("S-P1.4 the dedicated server does not hand-edit the snapshot's revision",
            !dedicated.Contains("SessionSettings.Revision++"),
            "Revision is owned by the session path");
        Check("S-P1.5 the dedicated server does not hand-edit the snapshot's teleport setting",
            !dedicated.Contains("SessionSettings.TpaEnabled ="),
            "the teleport setting is owned by the session path");

        // the session path is the one that cleans the password, sets the flag, bumps the revision and publishes
        Check("S-P1.6 the session path cleans the password", settings.Contains("Password = SessionSettingsRules.CleanPassword(password);"));
        Check("S-P1.7 the session path sets the snapshot's password flag", settings.Contains("SessionSettings.HasPassword = Password.Length > 0;"));
        Check("S-P1.8 the session path bumps the snapshot's revision", settings.Contains("SessionSettings.Revision++;"));
        Check("S-P1.9 the session path publishes the snapshot", settings.Contains("PublishSessionSettings(old);"));

        // The ORDER matters: the session path applies the snapshot locally (which sets Tpa.Enabled from the
        // snapshot), so the dedicated server's configured teleport setting has to be in the snapshot BEFORE the
        // password path runs. The other order would leave the session with the snapshot's teleport setting
        // instead of the configured one.
        int tpaAt = dedicated.IndexOf("UpdateSessionSettings(s => { s.TpaEnabled = tpaOn;", StringComparison.Ordinal);
        int pwAt = dedicated.IndexOf("SetSessionPassword(Password, persistDefault: false);", StringComparison.Ordinal);
        Check("S-P1.9b the dedicated server's teleport setting reaches the snapshot before the password path runs",
            tpaAt >= 0 && pwAt >= 0 && tpaAt < pwAt,
            "the password path applies the snapshot before the teleport setting is in it");
    }

    // ---------------------------------------------------------------- P1: the bridge's gate
    private static void P1_TheGate(string bridge, string data)
    {
        // The gate is ONE rule, in the pure core, so it can be tested as a property. A gate spelled out in
        // the bridge can only be checked by reading it, and it can drift from the rule it claims to follow.
        Check("S-P1.10 the gate is one rule", bridge.Contains("SessionSettingsPolicy.BridgeAllowed("),
            "the gate is spelled out in the bridge, so it cannot be tested on its own");
        // the refusal must be the ONE answer: the bridge must not spell out its own error string, or it can
        // drift from the rule that the rest of the code (and the client) reads.
        Check("S-P1.11 the refusal is one answer",
            bridge.Contains("SessionSettingsPolicy.PolicyDenied")
            && !bridge.Contains("developer access is disabled by the host"),
            "the bridge spells out its own refusal, so it can drift from the rule");
        Check("S-P1.12 the exemption is not spelled out in the bridge again",
            !bridge.Contains("bool hostControl ="),
            "the exemption is decided in the bridge again, where it can drift from the rule");

        // the rule itself: the exemption is the HOST's own
        Check("S-P1.13 the exemption requires the host",
            data.Contains("public static bool IsHostControlCommand(bool hosting, string cmd, string arg)")
            && data.Contains("if (!hosting) return false;"),
            "the exemption does not require the host, so a joiner reaches the handler");
        Check("S-P1.14 the rule is in the pure core", data.Contains("public static class SessionSettingsPolicy"));

        // the bridge calls the rule with the host's own state
        Check("S-P1.15 the bridge tells the rule whether it is hosting",
            bridge.Contains("Net.SessionSettingsPolicy.BridgeAllowed(Net.Mp.Server != null,"),
            "the bridge does not say whether it is hosting");
    }

    // ---------------------------------------------------------------- P1: a malformed snapshot fails visibly
    private static void P1_MalformedSnapshot(string mp, string data)
    {
        string welcome = Case(mp, "case Protocol.Welcome:");
        Check("S-P1.16 the Welcome path reads the host's snapshot",
            welcome.Contains("if (!SessionSettingsSnapshot.TryRead(r, out var hostSettings))"));

        // the refusal must set a visible reason: a join that hangs on the loading screen with no explanation
        // is the worst outcome, and the player cannot tell a broken host from a network problem.
        // the refusal must set a visible reason, and it must set it INSIDE the Welcome case (a reason set
        // somewhere else would not reach the joiner).
        int tryAt = welcome.IndexOf("if (!SessionSettingsSnapshot.TryRead(r, out var hostSettings))", StringComparison.Ordinal);
        string afterTry = tryAt >= 0 ? welcome.Substring(tryAt, Math.Min(900, welcome.Length - tryAt)) : "";
        Check("S-P1.17 a malformed snapshot in Welcome sets a visible reason",
            afterTry.Contains("RejectReason"),
            "the handler returns with no RejectReason, so the join hangs silently");
        // The refusal carries the EXACT wording the integrated build already shows the player. The rule
        // holds the sentence; the handler asks the rule for it. The check pins the sentence itself, so a
        // reword (a protocol-number-shaped message, say) fails here instead of reaching the player.
        Check("S-P1.18 the refusal names the reason",
            welcome.Contains("MalformedSnapshotReason()"));
        Check("S-P1.18b the refusal carries the exact integrated wording",
            data.Contains("the host's session settings could not be read \u2014 the host and this copy ") &&
            data.Contains("are not the same build; update the mod on both and join again"),
            "the visible wording is not the one the integrated build shows");
        Check("S-P1.18c the refusal is a visible, player-facing sentence",
            data.Contains("public static string MalformedSnapshotReason()") &&
            !data.Contains("public static string MalformedSnapshotReason(int"),
            "the reason helper still takes a protocol number: the wording must not be protocol-shaped");
        Check("S-P1.19 the refusal carries a reject code",
            welcome.Contains("RejectCode = Protocol.RejectOther"));

        // the refusal ABORTS the handler: the rest of Welcome (the terrain mode, the world load) is not
        // processed, so a malformed snapshot is neither applied nor half-applied.
        int refuseAt = welcome.IndexOf("MalformedSnapshotReason", StringComparison.Ordinal);
        int breakAt = welcome.IndexOf("break;", Math.Max(0, refuseAt), StringComparison.Ordinal);
        int applyAt = welcome.IndexOf("ApplyClientSnapshot", StringComparison.Ordinal);
        Check("S-P1.20 the malformed snapshot is not applied",
            refuseAt >= 0 && breakAt > refuseAt && (applyAt < 0 || breakAt < applyAt),
            "the handler continues after the refusal");

        // the reason is a sentence a player can read, and it is built by ONE rule (so the text cannot drift)
        Check("S-P1.21 the reason is built by one rule",
            data.Contains("public static string MalformedSnapshotReason()")
            && welcome.Contains("SessionSettingsPolicy.MalformedSnapshotReason()"),
            "the reason is not built by the one rule");

        // a malformed snapshot is discarded whole: no field is applied from it
        Check("S-P1.22 a malformed snapshot is discarded whole",
            data.Contains("settings = null;\n                return false;"));

        // the incremental path may still drop a bad update silently (the current state stands)
        Check("S-P1.23 the incremental path drops a malformed snapshot without applying it",
            mp.Contains("TryRead(r, out var updated) && r.Remaining == 0"));
    }

    // ---------------------------------------------------------------- P1: the joiner's settings view
    private static void P1_TheJoinsView(string screen, string mp)
    {
        Check("S-P1.24 a dedicated settings button is built", screen.Contains("TLDRevampSessionSettingsButton"));
        Check("S-P1.25 the settings button is shown in a game", screen.Contains("bool showButton = inGame;"));

        // The settings LABEL must be on the dedicated button, for everybody in a game. It used to sit on the
        // shared button, behind a condition about the selected player's save slot -- a condition a joiner
        // never satisfies, so a joiner saw an unlabelled button.
        Check("S-P1.26 the settings label is on the dedicated button",
            screen.Contains("Show(_settingsButton.GetComponent<Button>(), true,") || screen.Contains("Show(_settingsButton, true,"),
            "the label goes on the shared button, behind a saved-player condition a joiner cannot satisfy");
        Check("S-P1.27 the settings label is not behind a saved-player condition",
            !screen.Contains("if (inGame && sel2?.Saved != null) Show(_secondary, true,"),
            "the label is behind a condition about the selected player's save slot");
        Check("S-P1.28 the settings label is shown for everybody in a game",
            screen.Contains("if (inGame && _settingsButton != null) Show(_settingsButton.GetComponent<Button>(), true,")
            || screen.Contains("if (inGame) Show(_settingsButton"),
            "the label is shown only in some case");

        // the shared button keeps "Join by address": the label used to be taken from it
        string inGame = Method(screen, "else if (inGame)");
        Check("S-P1.29 the shared button says 'Join by address' in a game",
            inGame.Contains("JoinByAddress") || inGame.Contains("\"Join by address\""),
            "the shared button in a game does not say 'Join by address'");
        bool sharedCarriesPanelLabel = screen.Split('\n').Any(l =>
            l.Contains("Show(_secondary,") && (l.Contains("\"Session settings\"") || l.Contains("\"Close settings\"")
                                           || l.Contains("SettingsButtonLabel")));
        Check("S-P1.30 the settings label is never put on the shared button", !sharedCarriesPanelLabel,
            "the settings label is put on the shared button");

        // the shared button's click must not open the panel
        string secondary = Method(screen, "private static void Secondary()");
        Check("S-P1.31 the shared button is not taken over by the settings panel",
            !secondary.Contains("_settingsOpen = !_settingsOpen"),
            "clicking 'Join by address' opens the settings panel instead");

        // the rows are not frozen while the panel is closed: the rows are filled BEFORE the early return that
        // skips the rest of the panel's work. A closed panel must not be stale, and the panel's own button must
        // open it with the CURRENT values.
        // The rows must be filled even while the panel is closed. Two spellings satisfy that: the fill
        // happens BEFORE the early return that skips the rest of the panel's work, or there is no such
        // early return at all. What must NOT happen is a fill that sits behind the early return.
        string refresh = Method(screen, "private static void RefreshSessionSettings(bool inGame)");
        int fillAt = refresh.IndexOf("FillSettingRows();", StringComparison.Ordinal);
        int fillAlt = refresh.IndexOf("FillSettingsRows(", StringComparison.Ordinal);
        int returnAt = refresh.IndexOf("if (!showButton || !_settingsOpen) return;", StringComparison.Ordinal);
        int fill = fillAt >= 0 ? fillAt : fillAlt;
        bool filledWhileClosed = fill >= 0 && (returnAt < 0 || fill < returnAt);
        Check("S-P1.32 the rows are refreshed even while the panel is closed", filledWhileClosed,
            "the row labels are only computed while the panel is open, so a closed panel is stale");
        Check("S-P1.33 the rows have their own method", screen.Contains("internal static void FillSettingRows()"));

        // only the host may change the rows
        Check("S-P1.34 only the host may change the rows",
            screen.Contains("Label(_settingsRows[0], \"TPA: \" + s.TpaName); _settingsRows[0].interactable = host;"));
        Check("S-P1.35 a joiner's rows are not interactive",
            screen.Contains("_settingsRows[2].interactable = host;"));

        // the joiner's rows are filled when a snapshot arrives, not only when the panel is opened
        // The rows must be filled when the snapshot arrives, not only when the panel is opened. The
        // integrated build's call is MpScreen.RefreshSessionRows(); the source lane's name for the same
        // call is FillSettingRows(). Either satisfies the property, so either is accepted.
        string snap = Case(mp, "case Protocol.SessionSettings:");
        int fillInSnap = snap.IndexOf("MpScreen.RefreshSessionRows();", StringComparison.Ordinal);
        if (fillInSnap < 0) fillInSnap = snap.IndexOf("MpScreen.FillSettingRows();", StringComparison.Ordinal);
        int applyInSnap = snap.IndexOf("ApplyClientSnapshot(updated, false);", StringComparison.Ordinal);
        Check("S-P1.36 the joiner's rows are filled when the snapshot arrives",
            fillInSnap >= 0 && applyInSnap >= 0 && fillInSnap > applyInSnap,
            "the rows are only filled when the panel is opened");
    }
}

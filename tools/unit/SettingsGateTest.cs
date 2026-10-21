// The bridge's policy gate, as a property of the rule.
//
// The hostControl exemption used to let a JOINER past the policy gate, to a handler that then answered
// "not hosting". Every other refused command answers with the policy signal, so the exemption gave a joiner
// a DIFFERENT answer for `mp policy` than for any other denied command -- and a client that reads the
// policy denial as "the host denied this" could not tell the two apart.
//
// The rule therefore lives in the pure core (SessionSettingsPolicy) and is driven here as code, so the
// property is checked and not merely reviewed. The bridge calls the rule (see S-P1.10 .. S-P1.15 in
// SettingsFeatureSourceTest).

using System;
using TLDRevamp.Net;

static class SettingsGateTest
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
        const string policyCmd = "mp";

        // ---- a joiner gets the policy signal for the policy command, like for anything else it may not run
        Check("G.1 a joiner may not run the policy command",
            !SessionSettingsPolicy.BridgeAllowed(false, false, false, policyCmd, "policy everyone"),
            "the joiner reached the handler");
        Check("G.2 a joiner may not run the policy command (no argument either)",
            !SessionSettingsPolicy.BridgeAllowed(false, false, false, policyCmd, "policy"));
        Check("G.3 a joiner's policy command is refused for the policy, not for anything else",
            SessionSettingsPolicy.BridgeAllowed(false, false, false, policyCmd, "policy everyone")
            == SessionSettingsPolicy.BridgeAllowed(false, false, false, policyCmd, "anything else"),
            "the joiner gets a different signal for the policy command");
        // The property is that the gate treats the policy command like EVERY other command: the exemption
        // depends on whether this machine is the host and on nothing else. Under a permissive policy the
        // joiner may run any developer command, the policy command included -- and the handler still refuses
        // to change a policy it does not own, so a joiner can never widen its own policy.
        Check("G.4 the policy command is gated exactly like every other command on a joiner",
            SessionSettingsPolicy.BridgeAllowed(false, true, false, policyCmd, "policy everyone")
            == SessionSettingsPolicy.BridgeAllowed(false, true, false, "set", "TLDRevamp.X 1"),
            "the policy command is gated differently from the other commands");
        Check("G.4b the policy command is gated exactly like every other command on the host",
            SessionSettingsPolicy.BridgeAllowed(true, true, false, policyCmd, "policy everyone")
            == SessionSettingsPolicy.BridgeAllowed(true, true, false, "set", "TLDRevamp.X 1"),
            "the policy command is gated differently from the other commands");

        // ---- the host: its own session control always runs, even when the policy denies developer access
        Check("G.5 the host may change its own policy even when the policy denies it",
            SessionSettingsPolicy.BridgeAllowed(true, false, false, policyCmd, "policy everyone"));
        Check("G.6 the host may read its own policy",
            SessionSettingsPolicy.BridgeAllowed(true, false, false, policyCmd, "policy"));

        // ---- a read-only command always runs, host or joiner
        Check("G.7 a read-only command runs on a joiner",
            SessionSettingsPolicy.BridgeAllowed(false, false, true, "devaccess", ""));
        Check("G.8 a read-only command runs on a host",
            SessionSettingsPolicy.BridgeAllowed(true, false, true, "devaccess", ""));

        // ---- the exemption is the policy command and nothing else
        Check("G.9 another mp command is not exempt on the host",
            !SessionSettingsPolicy.BridgeAllowed(true, false, false, policyCmd, "players"));
        Check("G.10 another command is not exempt on the host",
            !SessionSettingsPolicy.BridgeAllowed(true, false, false, "set", "TLDRevamp.X 1"));
        Check("G.11 a command that merely starts with 'policy' is not exempt",
            !SessionSettingsPolicy.BridgeAllowed(true, false, false, policyCmd, "policies everyone"));
        Check("G.12 a command whose name merely starts with 'mp' is not exempt",
            !SessionSettingsPolicy.BridgeAllowed(true, false, false, "mpolicy", "everyone"));
        Check("G.13 the exemption is the exact policy command",
            SessionSettingsPolicy.IsHostControlCommand(true, policyCmd, "policy")
            && SessionSettingsPolicy.IsHostControlCommand(true, policyCmd, "policy everyone"));

        // ---- the policy signal is the one the client reads
        Check("G.14 the refusal names the host's session policy",
            SessionSettingsPolicy.PolicyDenied.Contains("host's session policy"));
        Check("G.15 the refusal is a JSON error", SessionSettingsPolicy.PolicyDenied.StartsWith("{\"error\":\""));
        Check("G.16 the refusal is one answer for every refused command",
            SessionSettingsPolicy.PolicyDenied == "{\"error\":\"developer access is disabled by the host's session policy\"}");

        // ---- the malformed snapshot's reason is a sentence a player can read
        //
        // The wording is the one the integrated build ALREADY shows the player. It is preserved EXACTLY:
        // this helper exists so the text has one home, not so the text may change. The checks below
        // therefore pin the exact sentence, so a reword (for example a protocol-number-shaped message)
        // fails here instead of reaching the player.
        string reason = SessionSettingsPolicy.MalformedSnapshotReason();
        Check("G.17 the reason is the exact integrated wording",
            reason == "the host's session settings could not be read \u2014 the host and this copy are not the same build; update the mod on both and join again",
            reason);
        Check("G.18 the reason says what went wrong", reason.Contains("session settings could not be read"), reason);
        Check("G.19 the reason is not empty", reason.Trim().Length > 20, reason);
        Check("G.20 the reason is not a bare error code", !reason.StartsWith("{"), reason);
        // the reason stays player-facing: it names the cause and the way out, and it carries no protocol number
        Check("G.20b the reason names the way out", reason.Contains("update the mod on both and join again"), reason);
        Check("G.20c the reason carries no protocol number", !reason.Contains("protocol"), reason);

        // ---- the panel's own button
        Check("G.21 the button says 'Session settings' when it is closed",
            SessionSettingsPolicy.SettingsButtonLabel(false) == "Session settings");
        Check("G.22 the button says 'Close settings' when it is open",
            SessionSettingsPolicy.SettingsButtonLabel(true) == "Close settings");
        Check("G.23 the shared button keeps 'Join by address'",
            SessionSettingsPolicy.JoinByAddress == "Join by address");

        Console.WriteLine(failures == 0
            ? $"PASS SettingsGate: {checks} checks"
            : $"FAIL SettingsGate: {failures} of {checks} checks failed");
        return failures == 0 ? 0 : 1;
    }
}

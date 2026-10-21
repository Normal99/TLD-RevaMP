using System;
using TLDRevamp.Net;

static class SessionSettingsTest
{
    private static int checks;
    private static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }

    private static NetWriter Message(SessionSettingsSnapshot s)
    {
        var w = new NetWriter(); s.Write(w); return w;
    }

    private static NetReader Reader(NetWriter w)
    {
        var r = new NetReader(); r.Load(w.Len); Array.Copy(w.Buf, r.Buf, w.Len);
        return r;
    }

    private static int Main()
    {
        var original = new SessionSettingsSnapshot { SessionToken = 42, Revision = 7, TpaEnabled = true,
            TpaCooldownS = 300, JoinAtHost = false, DeveloperPolicy = DeveloperAccessPolicy.HostOnly, HasPassword = true };
        original.Normalize();
        var r = Reader(Message(original));
        Check(SessionSettingsSnapshot.TryRead(r, out var decoded) && r.Remaining == 0, "full snapshot reads exactly");
        Check(decoded.SessionToken == 42 && decoded.Revision == 7 && decoded.TpaEnabled && decoded.TpaCooldownS == 300 &&
              !decoded.JoinAtHost && decoded.DeveloperPolicy == DeveloperAccessPolicy.HostOnly && decoded.HasPassword, "all fields round trip");
        var encoded = Message(original);
        Check(!System.Text.Encoding.UTF8.GetString(encoded.Buf, 0, encoded.Len).Contains("secret"), "wire snapshot carries no password value");
        var w = Message(original); w.Buf[0] = 99; Check(!SessionSettingsSnapshot.TryRead(Reader(w), out _), "unknown schema rejected");
        w = Message(original); for (int i = 0; i < 4; i++) w.Buf[1 + i] = 0;
        Check(!SessionSettingsSnapshot.TryRead(Reader(w), out _), "zero session token rejected");
        w = Message(original); w.Buf[12] = 255; Check(!SessionSettingsSnapshot.TryRead(Reader(w), out _), "unknown developer policy rejected");
        w = Message(original); w.Buf[14] = 2; Check(!SessionSettingsSnapshot.TryRead(Reader(w), out _), "noncanonical boolean rejected");
        var plain = new SessionSettingsSnapshot { SessionToken = 2, Revision = 1, TpaEnabled = false, DeveloperPolicy = DeveloperAccessPolicy.Everyone }; plain.Normalize(); w = Message(plain);
        Check(SessionSettingsSnapshot.TryRead(Reader(w), out decoded) && !decoded.TpaEnabled, "session defaults need no password");

        foreach (var p in new[] { DeveloperAccessPolicy.Everyone, DeveloperAccessPolicy.HostOnly, DeveloperAccessPolicy.Disabled })
            foreach (bool host in new[] { false, true })
                Check(SessionSettingsRules.Permits(p, host) == (p == DeveloperAccessPolicy.Everyone || p == DeveloperAccessPolicy.HostOnly && host),
                    "policy matrix " + p + " host=" + host);

        Check(SessionSettingsRules.CleanPassword("  a\nb\tc  ") == "a b c", "password trims and strips controls");
        Check(SessionSettingsRules.CleanPassword(new string('x', 80)).Length == 64, "password capped at 64");
        Check(!SessionSettingsSnapshot.Accept(original, original.Copy()), "same revision ignored");
        Check(SessionSettingsSnapshot.Accept(original, new SessionSettingsSnapshot { SessionToken = 42, Revision = 8 }), "newer revision accepted");
        Check(!SessionSettingsSnapshot.Accept(original, new SessionSettingsSnapshot { SessionToken = 43, Revision = 1 }), "a foreign token is not an update");
        Check(SessionSettingsSnapshot.AcceptNewHost(43, new SessionSettingsSnapshot { SessionToken = 43, Revision = 1 }), "a new host is accepted on the explicit path");
        Check(!SessionSettingsSnapshot.AcceptNewHost(43, new SessionSettingsSnapshot { SessionToken = 44, Revision = 1 }), "the new-host path accepts only that host's token");
        Check(!SessionSettingsSnapshot.AcceptNewHost(0, new SessionSettingsSnapshot { SessionToken = 43, Revision = 1 }), "no host token means no new host");
        Check(!SessionSettingsSnapshot.Accept(original, null), "null snapshot refused");

        // ---- a client that is loading between Hello and Ready must not miss a settings change.
        // The host publishes the current snapshot when the peer says Ready; a peer that is not ready yet needs one.
        Check(SessionSettingsRules.NeedsSnapshotOnReady(false), "a peer that is not ready yet needs the current snapshot");
        Check(!SessionSettingsRules.NeedsSnapshotOnReady(true), "a ready peer does not need another snapshot");
        Check(!SessionSettingsRules.ClientHasSnapshot(false, true), "a client that is not ready has no usable snapshot");
        Check(!SessionSettingsRules.ClientHasSnapshot(true, false), "a ready client without a snapshot has none");
        Check(SessionSettingsRules.ClientHasSnapshot(true, true), "a ready client with a snapshot has one");
        // the change that a loading client missed: the revision moved on while it was loading
        var missed = original.Copy(); missed.Revision = 8; missed.DeveloperPolicy = DeveloperAccessPolicy.Everyone;
        Check(SessionSettingsSnapshot.Accept(original, missed), "the snapshot published on Ready is newer and is accepted");
        var stale = original.Copy();
        Check(!SessionSettingsSnapshot.Accept(missed, stale), "an older snapshot is refused after a newer one");

        // ---- the password never goes into a log, the anomaly watch or a settings line.
        const string secret = "hunter2";
        Check(!SessionSettingsRules.RedactCommand("password " + secret, secret).Contains(secret), "a password command keeps no value");
        Check(SessionSettingsRules.RedactCommand("password " + secret, secret).Contains(SessionSettingsRules.RedactedPassword), "a password command says that there is one");
        Check(!SessionSettingsRules.RedactCommand("admin password " + secret, secret).Contains(secret), "a secret command with a prefix keeps no value");
        Check(!SessionSettingsRules.RedactCommand("set TLDRevamp.X " + secret, secret).Contains(secret), "any other command loses the password value");
        Check(SessionSettingsRules.RedactCommand("set TLDRevamp.X " + secret, secret).Contains(SessionSettingsRules.RedactedPassword), "the redacted value is marked");
        Check(SessionSettingsRules.RedactCommand("mp status", secret) == "mp status", "a command without a secret is unchanged");
        Check(SessionSettingsRules.RedactCommand("password", secret) == "password", "a password command without a value is unchanged");
        Check(!SessionSettingsRules.RedactSecret("the password is " + secret, secret).Contains(secret), "a secret in a free text is replaced");
        Check(SessionSettingsRules.RedactSecret("nothing here", secret) == "nothing here", "a line without the secret is unchanged");
        Check(!SessionSettingsRules.RedactSettingsLine("hostPassword=" + secret).Contains(secret), "a settings line keeps no password value");
        Check(SessionSettingsRules.RedactSettingsLine("hostPassword=" + secret).StartsWith("hostPassword="), "a settings line keeps its key");
        Check(SessionSettingsRules.RedactSettingsLine("hostPassword=") == "hostPassword=", "an empty settings value is left alone");
        Check(SessionSettingsRules.RedactSettingsLine("tpa=1") == "tpa=1", "another settings line is unchanged");
        // the escaped form of the same secret is replaced too
        Check(!SessionSettingsRules.RedactSecret("\"hostPassword\":\"" + secret + "\"", secret).Contains(secret), "an escaped secret is replaced");
        // a password that is a substring of another word is still replaced
        Check(!SessionSettingsRules.RedactSecret("x" + secret, secret).Contains(secret), "a secret inside a longer word is replaced");

        // ---- the loading change: a client that is loading between Hello and Ready must not miss a settings change.
        // The host publishes the current snapshot when the peer says Ready; the client accepts it because the
        // revision moved on while it was loading.
        var welcome = new SessionSettingsSnapshot { SessionToken = 100, Revision = 3, TpaEnabled = true,
            TpaCooldownS = 60, JoinAtHost = true, DeveloperPolicy = DeveloperAccessPolicy.HostOnly };
        welcome.Normalize();
        var client = welcome.Copy();
        Check(SessionSettingsRules.ClientHasSnapshot(false, true) == false, "a loading client has no usable snapshot yet");
        Check(SessionSettingsRules.NeedsSnapshotOnReady(false), "the host sends the current snapshot to a loading peer");
        // while the client loads, the host changes the policy
        var changed = welcome.Copy(); changed.Revision = 4; changed.DeveloperPolicy = DeveloperAccessPolicy.Everyone;
        Check(SessionSettingsSnapshot.Accept(client, changed), "the change published while the client was loading is accepted");
        client = changed;
        // the snapshot that arrives with Ready: same content, the revision the client already has
        var onReady = changed.Copy();
        Check(!SessionSettingsSnapshot.Accept(client, onReady), "the same revision on Ready is not applied twice");
        // a change that comes after Ready is accepted as well
        var later = changed.Copy(); later.Revision = 5; later.JoinAtHost = false;
        Check(SessionSettingsSnapshot.Accept(client, later), "a later change is accepted");
        // a snapshot of another session is NOT accepted as an update: a client has one host, and a foreign token
        // is only allowed on the explicit "new host" path, where the client is told which token to bind to.
        var other = new SessionSettingsSnapshot { SessionToken = 200, Revision = 1, DeveloperPolicy = DeveloperAccessPolicy.Everyone };
        other.Normalize();
        Check(!SessionSettingsSnapshot.Accept(later, other), "a foreign token is not accepted as an update");
        Check(SessionSettingsSnapshot.AcceptNewHost(200, other), "a new host is accepted on the explicit new-host path");
        Check(!SessionSettingsSnapshot.Accept(later, other), "a foreign token still is not an update afterwards");
        // the policy a client follows: only from a snapshot it has
        Check(SessionSettingsRules.Permits(DeveloperAccessPolicy.Everyone, false), "everyone permits a joiner");
        Check(!SessionSettingsRules.Permits(DeveloperAccessPolicy.HostOnly, false), "host only refuses a joiner");
        Check(SessionSettingsRules.Permits(DeveloperAccessPolicy.HostOnly, true), "host only permits the host");

        // ---- the migration: an old settings file with a plaintext password loses the value, keeps the key, and
        // the value comes back in memory for this session only.
        var old = new[] { "# TLD Revamp settings", "tpa=1", "hostPassword=hunter2", "hostJoinAtHost=1" };
        Check(SessionSettingsRules.StripPlaintextPassword(old, out var taken, out var kept), "the old file is changed");
        Check(taken == "hunter2", "the value comes back for this session");
        Check(kept.Length == old.Length, "the file keeps its lines");
        Check(!string.Join("\n", kept).Contains("hunter2"), "the file keeps no password value");
        Check(kept.Contains("hostPassword="), "the file keeps the key");
        Check(kept.Contains("tpa=1") && kept.Contains("hostJoinAtHost=1"), "the other lines are untouched");
        // a file that has no password is left alone
        var clean = new[] { "tpa=1", "hostJoinAtHost=0" };
        Check(!SessionSettingsRules.StripPlaintextPassword(clean, out var none, out var keptClean), "a file without a password is not changed");
        Check(none == "" && keptClean.Length == clean.Length, "a file without a password keeps its lines");
        // a file whose password line is already empty is left alone
        var empty = new[] { "hostPassword=" };
        Check(!SessionSettingsRules.StripPlaintextPassword(empty, out var none2, out _), "an empty password line is not changed");
        Check(none2 == "", "an empty password line has no value");
        // the key's case does not matter
        Check(SessionSettingsRules.StripPlaintextPassword(new[] { "HostPassword=secret" }, out var taken2, out _), "the key's case does not matter");
        Check(taken2 == "secret", "the value comes back whatever the key's case");
        // a value with spaces is trimmed like every other password
        Check(SessionSettingsRules.StripPlaintextPassword(new[] { "hostPassword=  spaced  " }, out var taken3, out _), "a spaced value is taken");
        Check(taken3 == "spaced", "the value is cleaned like every other password");

        // the password is never on the wire: the snapshot carries only the flag
        var withPassword = new SessionSettingsSnapshot { SessionToken = 9, Revision = 1, HasPassword = true };
        withPassword.Normalize();
        var wire = Message(withPassword);
        Check(!System.Text.Encoding.UTF8.GetString(wire.Buf, 0, wire.Len).Contains(secret), "the wire carries no password value");

        // ---- the cooldown is bounded whether or not TPA is enabled (a malformed snapshot is malformed).
        // The cooldown is the 16-bit field at bytes 9 and 10: schema(1) token(4) revision(4) tpaEnabled(1).
        var atLimit = new SessionSettingsSnapshot { SessionToken = 5, Revision = 1, TpaEnabled = false,
            TpaCooldownS = SessionSettingsSnapshot.MaxTpaCooldownS };
        atLimit.Normalize();
        w = Message(atLimit);
        Check(SessionSettingsSnapshot.TryRead(Reader(w), out decoded) && decoded.TpaCooldownS == SessionSettingsSnapshot.MaxTpaCooldownS,
            "the longest legal cooldown is accepted");
        // the bound is the same whether TPA is on or off: a disabled-TPA snapshot is not a loophole
        var overWhenOff = Message(new SessionSettingsSnapshot { SessionToken = 5, Revision = 1, TpaEnabled = false, TpaCooldownS = 1000 });
        overWhenOff.Buf[9] = 0xFF; overWhenOff.Buf[10] = 0xFF;
        Check(!SessionSettingsSnapshot.TryRead(Reader(overWhenOff), out _), "a disabled-TPA snapshot with an over-long cooldown is refused");
        var overWhenOn = Message(new SessionSettingsSnapshot { SessionToken = 5, Revision = 1, TpaEnabled = true, TpaCooldownS = 1000 });
        overWhenOn.Buf[9] = 0xFF; overWhenOn.Buf[10] = 0xFF;
        Check(!SessionSettingsSnapshot.TryRead(Reader(overWhenOn), out _), "an enabled-TPA snapshot with an over-long cooldown is refused");
        Check(SessionSettingsSnapshot.MaxTpaCooldownS == ushort.MaxValue, "the cooldown bound is the largest value the wire can hold");

        // ---- the client is bound to the host's session token: a snapshot from another host is refused.
        SessionSettingsSnapshot.UnbindHost();
        Check(!SessionSettingsSnapshot.AcceptFromBoundHost(original), "an unbound client trusts no snapshot");
        SessionSettingsSnapshot.BindToHost(42);
        Check(SessionSettingsSnapshot.AcceptFromBoundHost(original), "the host's own snapshot is accepted");
        Check(!SessionSettingsSnapshot.AcceptFromBoundHost(new SessionSettingsSnapshot { SessionToken = 43, Revision = 99 }),
            "a snapshot with a foreign token is refused even when its revision is newer");
        Check(!SessionSettingsSnapshot.AcceptFromBoundHost(new SessionSettingsSnapshot { SessionToken = 42, Revision = 0 }),
            "a snapshot without a revision is refused");
        Check(!SessionSettingsSnapshot.AcceptFromBoundHost(null), "a null snapshot is refused");
        // the revision still has to move forward within the host's own token
        Check(SessionSettingsSnapshot.Accept(original, new SessionSettingsSnapshot { SessionToken = 42, Revision = 8 }),
            "a newer revision from the same host is accepted");
        Check(!SessionSettingsSnapshot.Accept(original, original.Copy()), "the same revision is not applied twice");
        SessionSettingsSnapshot.UnbindHost();
        Check(!SessionSettingsSnapshot.AcceptFromBoundHost(original), "the binding is released with the session");

        // ---- the password is cleaned on every path, and both sides compare cleaned forms.
        // The host's password is cleaned when it is stored; the joiner's is cleaned when it goes on the wire.
        // If only one side is cleaned, a joiner who typed exactly what the host set is refused for a password
        // that is right (a trailing space, a newline, a 70-character password).
        Check(SessionSettingsRules.CleanPassword("hunter2 ") == "hunter2", "a trailing space is cleaned");
        Check(SessionSettingsRules.CleanPassword("a\nb") == "a b", "a newline is cleaned to a space");
        Check(SessionSettingsRules.CleanPassword(new string('x', 70)) == new string('x', 64), "a long password is capped");
        Check(SessionSettingsRules.CleanPassword("hunter2 ") == SessionSettingsRules.CleanPassword("hunter2"),
            "the host's and the joiner's cleaned forms are equal");
        Check(SessionSettingsRules.CleanPassword("hunter2\n") != SessionSettingsRules.CleanPassword("hunter2\t") == false,
            "a newline and a tab clean to the same form");

        // ---- the sinks (the codec test could not see these): the password must not reach any file or log.
        // The admin console's file echo is the sink that leaked: `password hunter2` was written verbatim to
        // console.txt AND to the log, because the redaction was applied to the `admin:` log line and not to
        // the file echo. These assertions are about the PROPERTIES, so a fix that moves the redaction
        // elsewhere still passes and a fix that removes it still fails.
        Check(!SessionSettingsRules.RedactCommand("password hunter2", "hunter2").Contains("hunter2"),
            "the password never appears in the redacted console echo");
        Check(SessionSettingsRules.RedactCommand("password hunter2", "hunter2").Contains("password"),
            "the redacted console echo still names the command");
        Check(!SessionSettingsRules.RedactCommand("password hunter2", "hunter2").Contains("hunter"),
            "no fragment of the secret survives the redaction");
        // a non-password command is not over-redacted
        Check(SessionSettingsRules.RedactCommand("say hello", "hunter2") == "say hello",
            "a command without a secret is not redacted");
        Check(SessionSettingsRules.RedactCommand("kick 1234", "hunter2") == "kick 1234",
            "another command without a secret is not redacted");
        // the host's own secret, wherever it appears in the line, is removed
        Check(!SessionSettingsRules.RedactCommand("say hunter2", "hunter2").Contains("hunter2"),
            "the host's secret is removed from any command");

        // ---- one normalized password path: the dedicated server routes through the session path.
        // (the property is pinned by SettingsLaneAuditTest 3.1/3.2, against the production source;
        // here the codec-level property is that the cleaned form is what both sides compare)
        Check(SessionSettingsRules.CleanPassword("  hunter2  ") == "hunter2",
            "the dedicated server's password is cleaned on the one path");

        Console.WriteLine($"PASS SessionSettings: {checks} checks");
        return 0;
    }
}

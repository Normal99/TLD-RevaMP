using System;

namespace TLDRevamp.Net
{
    public enum DeveloperAccessPolicy : byte
    {
        Everyone = 0,
        HostOnly = 1,
        Disabled = 2,
    }

    /// One host-owned setting snapshot. The password itself is never on the wire; joiners only need to know whether
    /// the host currently requires one.
    public sealed class SessionSettingsSnapshot
    {
        internal const byte Schema = 1;

        /// The longest teleport-request cooldown a snapshot may carry, in seconds. The wire field is 16 bits, so
        /// 65535 s (about 18 h 12 m) is the largest value that can exist at all: the bound is checked in both
        /// directions, so a snapshot that carries more than this is malformed rather than silently truncated.
        /// The host's own UI only ever offers 0, 60, 300 and 900 s.
        internal const int MaxTpaCooldownS = ushort.MaxValue;

        public uint SessionToken;
        public uint Revision;
        public bool TpaEnabled;
        public int TpaCooldownS;
        public bool JoinAtHost;
        public DeveloperAccessPolicy DeveloperPolicy;
        public bool HasPassword;

        internal SessionSettingsSnapshot Copy() => new SessionSettingsSnapshot
        {
            SessionToken = SessionToken, Revision = Revision, TpaEnabled = TpaEnabled, TpaCooldownS = TpaCooldownS,
            JoinAtHost = JoinAtHost, DeveloperPolicy = DeveloperPolicy, HasPassword = HasPassword,
        };

        internal void Normalize()
        {
            TpaCooldownS = Math.Max(0, TpaCooldownS);
            if (DeveloperPolicy < DeveloperAccessPolicy.Everyone || DeveloperPolicy > DeveloperAccessPolicy.Disabled)
                DeveloperPolicy = DeveloperAccessPolicy.HostOnly;
        }

        internal void Write(NetWriter w)
        {
            w.U8(Schema);
            w.U32(SessionToken);
            w.U32(Revision);
            w.Bool(TpaEnabled);
            w.U16((ushort)Math.Max(0, Math.Min(ushort.MaxValue, TpaCooldownS)));
            w.Bool(JoinAtHost);
            w.U8((byte)DeveloperPolicy);
            w.Bool(HasPassword);
        }

        internal static bool TryRead(NetReader r, out SessionSettingsSnapshot settings)
        {
            settings = new SessionSettingsSnapshot();
            byte schema = r.U8();
            settings.SessionToken = r.U32();
            settings.Revision = r.U32();
            byte tpaEnabled = r.U8();
            settings.TpaEnabled = tpaEnabled != 0;
            settings.TpaCooldownS = r.U16();
            byte joinAtHost = r.U8();
            settings.JoinAtHost = joinAtHost != 0;
            byte policy = r.U8();
            byte hasPassword = r.U8();
            settings.HasPassword = hasPassword != 0;
            if (r.Bad || schema != Schema || settings.SessionToken == 0 || settings.Revision == 0 ||
                policy > (byte)DeveloperAccessPolicy.Disabled ||
                tpaEnabled > 1 || joinAtHost > 1 || hasPassword > 1 ||
                // The cooldown is bounded whether or not TPA is enabled: a snapshot is the trust boundary, and a
                // disabled-TPA snapshot that carries an arbitrary 16-bit cooldown is still a malformed snapshot.
                settings.TpaCooldownS > MaxTpaCooldownS)
            {
                settings = null;
                return false;
            }
            settings.DeveloperPolicy = (DeveloperAccessPolicy)policy;
            settings.Normalize();
            return true;
        }

        public bool PermitsDeveloper(bool host) => SessionSettingsRules.Permits(DeveloperPolicy, host);

        /// Full snapshots only move forward inside one host session.
        /// The token of the host this client is bound to. A snapshot from another host (another session) is refused
        /// even if its revision is newer: the client has exactly one host, so a different token is not an update.
        internal static uint ClientBoundToken;

        /// Bind the client to the host's token (from Welcome). A later snapshot must carry this token.
        internal static void BindToHost(uint token) => ClientBoundToken = token;

        /// Unbind (the session ended): the client accepts a snapshot from any host again.
        internal static void UnbindHost() => ClientBoundToken = 0;

        /// A snapshot is accepted when it belongs to the same host (the same session token) and its revision moved
        /// on. A different token is a different host: that is not an update, and it is only allowed on the explicit
        /// "new host" path (AcceptFromBoundHost) where the client knows the host it is bound to.
        internal static bool Accept(SessionSettingsSnapshot current, SessionSettingsSnapshot next) =>
            next != null && next.SessionToken != 0 && next.Revision != 0 &&
            (current == null || next.SessionToken == current.SessionToken && next.Revision > current.Revision);

        /// The explicit "a new host took over" case: the client is told which token to bind to, and only that
        /// token is accepted afterwards. This is the only path on which a different token is allowed.
        internal static bool AcceptNewHost(uint newHostToken, SessionSettingsSnapshot next) =>
            newHostToken != 0 && next != null && next.SessionToken == newHostToken && next.Revision != 0;

        /// The rule a client applies to a snapshot that arrives after Welcome: the snapshot must belong to the host
        /// the client is bound to. A snapshot from any other token is refused, whatever its revision says.
        internal static bool AcceptFromBoundHost(SessionSettingsSnapshot next)
        {
            if (next == null || next.SessionToken == 0 || next.Revision == 0) return false;
            if (ClientBoundToken == 0) return false;   // not bound to a host yet: nothing is trusted
            return next.SessionToken == ClientBoundToken;
        }

        public string PolicyName => DeveloperPolicy == DeveloperAccessPolicy.Everyone ? "Everyone"
            : DeveloperPolicy == DeveloperAccessPolicy.HostOnly ? "Host only" : "Disabled";

        public string TpaName => !TpaEnabled ? "Off" : TpaCooldownS <= 0 ? "On, no wait"
            : TpaCooldownS % 60 == 0 ? "On, every " + Math.Max(1, TpaCooldownS / 60) + " min"
            : "On, every " + TpaCooldownS + " sec";
        public string PlacementName => JoinAtHost ? "Next to host" : "At world start";
        public string PasswordName => HasPassword ? "Set" : "None";

        internal string Json()
        {
            return "{\"token\":" + SessionToken + ",\"revision\":" + Revision + ",\"tpaEnabled\":" + (TpaEnabled ? "true" : "false") +
                   ",\"tpaCooldownS\":" + TpaCooldownS + ",\"joinAtHost\":" + (JoinAtHost ? "true" : "false") +
                   ",\"developerPolicy\":\"" + DeveloperPolicy + "\",\"hasPassword\":" + (HasPassword ? "true" : "false") + "}";
        }
    }

    /// The rules that decide whether a command may run, and what a refused command is told. They are pure, so the
    /// rule itself can be tested offline: the bridge's gate is a security boundary, and "every refused command gets
    /// the same answer" is a property of the rule, not of the bridge's plumbing.
    public static class SessionSettingsPolicy
    {
        /// The one answer a refused command gets. Every refusal says the same thing, so a client that is refused can
        /// tell "the host's policy denied this" from any other answer (an unknown command, a bad argument, a
        /// handler's own error).
        public const string PolicyDenied = "{\"error\":\"developer access is disabled by the host's session policy\"}";

        /// The shared button's label in a game: "Join by address". A joiner needs it to reach a host by address; it
        /// is the one thing the shared button is for in a game.
        public const string JoinByAddress = "Join by address";

        /// The host's own session control: the command that changes the developer policy. The host must be able to
        /// change its own policy even when the policy denies developer access (the test restores it), so this one
        /// command is exempt from the gate -- but only where the host actually is. A joiner is refused with the
        /// policy signal like every other command it may not run, instead of reaching a handler that answers
        /// "not hosting": a different answer for the same kind of refusal.
        public static bool IsHostControlCommand(bool hosting, string cmd, string arg)
        {
            if (!hosting) return false;
            if (!string.Equals(cmd, "mp", StringComparison.Ordinal)) return false;
            if (arg == null) arg = "";
            return arg == "policy" || arg.StartsWith("policy ");
        }

        /// The bridge's gate as one rule: a read-only command always runs; the host's own session control runs on
        /// the host; every other command needs the host's policy to allow it here.
        public static bool BridgeAllowed(bool hosting, bool policyAllows, bool readOnly, string cmd, string arg)
        {
            if (readOnly) return true;
            if (IsHostControlCommand(hosting, cmd, arg)) return true;
            return policyAllows;
        }

        /// The joiner's answer to a settings snapshot it cannot read: the join fails, with a reason the player can
        /// read. A join that hangs with no message is the worst outcome: the loading screen sits there and the
        /// player cannot tell a broken host from a network problem.
        ///
        /// The wording is the one the integrated build already shows the player. It is kept EXACTLY: this helper
        /// exists so the text has ONE home, not so the text may change. Do not reword it.
        public static string MalformedSnapshotReason()
        {
            return "the host's session settings could not be read — the host and this copy " +
                   "are not the same build; update the mod on both and join again";
        }

        /// The session-settings panel's own button: everybody in a game sees it, and it is the only thing that
        /// opens the panel. The shared button keeps "Join by address" -- the settings label used to sit on the
        /// shared button, behind a condition about the selected player's save slot, which a joiner never satisfies.
        public static string SettingsButtonLabel(bool open) => open ? "Close settings" : "Session settings";
    }

    public static class SessionSettingsRules
    {
        public static bool Permits(DeveloperAccessPolicy policy, bool host) =>
            policy == DeveloperAccessPolicy.Everyone || policy == DeveloperAccessPolicy.HostOnly && host;

        public static string CleanPassword(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var s = value.Trim();
            if (s.Length > 64) s = s.Substring(0, 64);
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++) if (char.IsControl(chars[i])) chars[i] = ' ';
            return new string(chars).Trim();
        }

        /// What a log line may say about a password: never the value, only that there is one.
        public const string RedactedPassword = "<set>";

        /// The commands whose argument is a secret.
        public static readonly string[] DefaultSecretCommands = { "password", "passwd", "pass", "hostpassword" };

        /// A line that may carry a secret (a bridge command, an admin command) with the secret replaced.
        public static string RedactSecret(string line, string secret)
        {
            if (string.IsNullOrEmpty(line)) return line ?? "";
            if (string.IsNullOrEmpty(secret)) return line;
            // the plain value first, then the common escaped forms (a JSON or a config line)
            var escaped = secret.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var outText = line.Replace(secret, RedactedPassword);
            if (escaped != secret) outText = outText.Replace(escaped, RedactedPassword);
            return outText;
        }

        /// A bridge or admin command line with every password value removed: the argument of a `password` command is
        /// dropped whatever it looks like, and any other occurrence of the current password is redacted.
        public static string RedactCommand(string line, string currentPassword, string[] secretCommands = null)
        {
            if (string.IsNullOrEmpty(line)) return line ?? "";
            var cmds = secretCommands ?? DefaultSecretCommands;
            var trimmed = line.TrimStart();
            var space = trimmed.IndexOf(' ');
            var head = (space < 0 ? trimmed : trimmed.Substring(0, space)).TrimStart('/').ToLowerInvariant();
            if (Array.IndexOf(cmds, head) >= 0)
            {
                var cut = line.IndexOf(' ');
                return cut < 0 ? line : line.Substring(0, cut + 1) + RedactedPassword;
            }
            return RedactSecret(line, currentPassword);
        }

        /// A settings file line: `hostPassword=<value>` keeps the key, loses the value.
        public static string RedactSettingsLine(string line, string currentPassword = null)
        {
            if (string.IsNullOrEmpty(line)) return line ?? "";
            var eq = line.IndexOf('=');
            if (eq <= 0) return line;
            var key = line.Substring(0, eq).Trim();
            if (!key.Equals("hostPassword", StringComparison.OrdinalIgnoreCase)) return line;
            var value = line.Substring(eq + 1);
            if (value.Length == 0) return line;   // already empty: nothing to hide
            var text = line.Substring(0, eq + 1) + RedactedPassword;
            if (!string.IsNullOrEmpty(currentPassword)) text = RedactSecret(text, currentPassword);
            return text;
        }

        /// A peer that was admitted but is not ready yet (it is loading the host's world) can have missed a settings
        /// change published while it was loading: it gets a fresh snapshot when it says Ready.
        public static bool NeedsSnapshotOnReady(bool ready) => !ready;

        /// A client may act on the developer policy only when it is ready and holds a snapshot.
        public static bool ClientHasSnapshot(bool ready, bool hasSnapshot) => ready && hasSnapshot;

        /// The settings file's lines without the plaintext password: the key stays, the value goes. The value that
        /// was in the file is returned so the caller can keep it in memory for this session. `changed` says whether
        /// the file has to be rewritten.
        public static bool StripPlaintextPassword(string[] lines, out string password, out string[] kept)
        {
            password = "";
            kept = lines ?? new string[0];
            if (lines == null) return false;
            var outLines = new System.Collections.Generic.List<string>(lines.Length);
            bool changed = false;
            foreach (var line in lines)
            {
                var eq = line.IndexOf('=');
                var key = eq > 0 ? line.Substring(0, eq).Trim() : "";
                if (eq > 0 && key.Equals("hostPassword", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line.Substring(eq + 1).Trim();
                    if (value.Length > 0)
                    {
                        if (password.Length == 0) password = CleanPassword(value);
                        changed = true;
                        outLines.Add("hostPassword=");
                        continue;
                    }
                }
                outLines.Add(line);
            }
            kept = outLines.ToArray();
            return changed;
        }
    }
}

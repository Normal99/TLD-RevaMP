using System.Text;

namespace TLDRevamp.Net
{
    /// Clean builds of one commit share an identity across SDKs and platforms. Local edits require the identical DLL.
    public static class BuildCheck
    {
        /// A/B: false = a different build is announced (Notice + [anomaly]) instead of refused, as GameBuild is.
        public static bool Refuse = true;

        public enum Result { Same, Unknown, Differs }

        /// The joiner's build hash: the field after the mod list, "" from a joiner built before this check.
        public static string Read(NetReader r)
        {
            if (r.Bad || r.Remaining <= 0) return "";
            string s = r.Str();
            return r.Bad || s == null ? "" : s;
        }

        public static Result Compare(string theirs, string mine)
        {
            var t = Parts(theirs); var m = Parts(mine);
            if (Known(t[0]) && Known(m[0]))
            {
                if (t[0] != m[0]) return Result.Differs;
                if (t[1] == "clean" && m[1] == "clean") return Result.Same;
            }
            theirs = t[2]; mine = m[2];
            if (string.IsNullOrEmpty(theirs) || theirs == "unknown" || string.IsNullOrEmpty(mine) || mine == "unknown")
                return Result.Unknown;
            return theirs == mine ? Result.Same : Result.Differs;
        }

        static bool Known(string commit) => commit != null && commit.Length == 40 && IsHex(commit);
        static bool IsHex(string s)
        {
            foreach (char c in s) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }
        static string[] Parts(string value)
        {
            var p = (value ?? "").Split(':');
            return p.Length == 3 ? p : new[] { "unknown", "dirty", value ?? "" };
        }
        static string Describe(string value)
        {
            var p = Parts(value);
            return "commit " + (Known(p[0]) ? p[0].Substring(0, 7) : "unknown") +
                (p[1] == "clean" ? "" : "+changes") + " (DLL " + Show(p[2]) + ")";
        }

        /// A hash as it may be shown: hex digits only, at most 16 (it comes off the network).
        public static string Show(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "(none)";
            var sb = new StringBuilder();
            foreach (char ch in hash)
            {
                if ((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F')) sb.Append(char.ToLowerInvariant(ch));
                if (sb.Length == 16) break;
            }
            return sb.Length > 0 ? sb.ToString() : "(unreadable)";
        }

        /// What the refused joiner reads.
        public static string Refusal(string version, string theirs, string mine) =>
            $"The host plays TLD Revamp {version} at {Describe(mine)}, yours {Describe(theirs)}. Both need the same clean commit; for local changes, copy the same DLL to both.";

        /// What everyone reads when the builds can't be compared, or differ and the host lets it in (Refuse off).
        public static string Announcement(string name, Result res) => res == Result.Unknown
            ? name + "'s TLD Revamp build could not be checked (a build from before the check). If the world goes wrong for them, both install the same release."
            : name + " has another build of TLD Revamp than the host (same version number, different code). Their world can load wrong; both install the same release.";

        public static string AnomalyLine(string name, Result res, string theirs, string mine) =>
            $"[anomaly] mod build {(res == Result.Unknown ? "unknown" : "differs")}: {name} has TLD Revamp {Describe(theirs)}, the host {Describe(mine)}";
    }
}

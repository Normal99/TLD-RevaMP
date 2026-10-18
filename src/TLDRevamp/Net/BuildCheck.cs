using System.Text;

namespace TLDRevamp.Net
{
    /// Two builds with the same mod version AND the same protocol can still run different code: two dev branches both at
    /// protocol 31, both "0.69.1". They passed the Hello check, and the joiner then decoded the host's records another
    /// way (2026-10-18: RecordCodec.Decode NullReferenceException in ApplyResync, then a lost connection). So the joiner
    /// sends Plugin.BuildHash after the mod list (older hosts stop reading before it) and the host compares it.
    ///
    /// Refused, not announced like Plugin.GameBuild: another game build only moves buildings, another mod build breaks the
    /// world it loads. Official builds always match: a release DLL is deterministic (a rebuild of the same commit is byte
    /// for byte the same), and dev releases carry their commit in the version. Two people building the same commit
    /// themselves can still differ (another SDK, or other line endings: the DLL carries its PDB's checksum), so testers
    /// share one built file. A joiner that sends no hash (built before this check) or a game that couldn't hash its own
    /// DLL can't be told apart: announced, not refused.
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
            if (string.IsNullOrEmpty(theirs) || theirs == "unknown" || string.IsNullOrEmpty(mine) || mine == "unknown")
                return Result.Unknown;
            return theirs == mine ? Result.Same : Result.Differs;
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
            $"The host plays another build of TLD Revamp {version} (build {Show(mine)}, yours {Show(theirs)}): the same version number, different code. Both need the same file: the same release, or one build copied to both.";

        /// What everyone reads when the builds can't be compared, or differ and the host lets it in (Refuse off).
        public static string Announcement(string name, Result res) => res == Result.Unknown
            ? name + "'s TLD Revamp build could not be checked (a build from before the check). If the world goes wrong for them, both install the same release."
            : name + " has another build of TLD Revamp than the host (same version number, different code). Their world can load wrong; both install the same release.";

        public static string AnomalyLine(string name, Result res, string theirs, string mine) =>
            $"[anomaly] mod build {(res == Result.Unknown ? "unknown" : "differs")}: {name} has TLD Revamp build {Show(theirs)}, the host {Show(mine)}";
    }
}

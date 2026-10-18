using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TLDRevamp
{
    internal static class TelemetryPrivacy
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Names = new HashSet<string>();
        private static readonly Regex Identifiers = new Regex(@"\b\d{17}\b|\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?|(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:.%]*|[A-Za-z]:[\\/][^\r\n]*|/(?:home|Users)/[^\r\n]*", RegexOptions.CultureInvariant);
        internal static void Name(string value) { if (!string.IsNullOrEmpty(value)) lock (Gate) { Names.Add(value); } }
        internal static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string result = Identifiers.Replace(text, "[redacted]");
            lock (Gate) foreach (string name in Names) result = result.Replace(name, "[player]");
            return result;
        }
    }
}

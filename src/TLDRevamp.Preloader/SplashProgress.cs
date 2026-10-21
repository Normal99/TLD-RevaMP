using System;
using System.IO;

namespace TLDRevamp.Preloader
{
    internal readonly struct SplashSnapshot
    {
        internal readonly double Fraction;
        internal readonly string Status;
        internal readonly bool Closing, Failed;
        internal SplashSnapshot(double fraction, string status, bool closing, bool failed)
        { Fraction = fraction; Status = status; Closing = closing; Failed = failed; }
    }

    internal sealed class SplashProgress
    {
        readonly object sync = new object();
        int stage, completed, total;
        double fraction;
        bool failed;
        string status = "Loading BepInEx";

        internal void Report(int next, int n, int count)
        {
            lock (sync)
            {
                if (next == StartupSplash.Failed) { failed = true; return; }
                if (failed || stage == StartupSplash.Finished || next < stage || next > StartupSplash.Finished) return;
                double value;
                string text;
                // ~2.6 s Awake, ~1.8 s to first Update: 5% setup, 50% classes, 5% rest of Awake, 40% handoff.
                switch (next)
                {
                    case StartupSplash.Loading: value = 0; text = "Loading BepInEx"; break;
                    case StartupSplash.Patching:
                        count = Math.Max(0, count);
                        n = Math.Max(0, Math.Min(n, count));
                        if (stage == next && (count != total || n < completed)) return;
                        value = .05 + .50 * (count == 0 ? 1 : (double)n / count);
                        text = "Patching the game  " + n + " / " + count;
                        break;
                    case StartupSplash.Starting: value = .55; text = "Starting RevaMP"; break;
                    case StartupSplash.Waiting: value = .60; text = "Starting the game"; break;
                    default: value = 1; text = "Starting the game"; break;
                }
                fraction = Math.Max(fraction, value);
                stage = next; completed = n; total = count; status = text;
            }
        }

        internal SplashSnapshot Snapshot()
        {
            lock (sync) return new SplashSnapshot(fraction, status, failed || stage == StartupSplash.Finished, failed);
        }
    }

    internal static class SplashOptions
    {
        internal static bool Skip(string[] args)
        {
            foreach (string arg in args)
                if (string.Equals(arg, "-batchmode", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "-nographics", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static int MonitorIndex(string[] args, int count, int primary)
        {
            if (count <= 0) return -1;
            primary = Math.Max(0, Math.Min(primary, count - 1));
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-monitor", StringComparison.OrdinalIgnoreCase))
                    return int.TryParse(args[i + 1], out int n) && n > 0 && n <= count ? n - 1 : primary;
            return primary;
        }

        internal static bool EnabledFile(string path)
        {
            try { using (var reader = File.OpenText(path)) return Enabled(reader); }
            catch { return true; }
        }

        internal static bool Enabled(TextReader reader)
        {
            string section = "", value = null, line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                { section = line.Substring(1, line.Length - 2); continue; }
                int eq = line.IndexOf('=');
                if (section == "General" && eq >= 0 && line.Substring(0, eq).Trim() == "StartupSplash")
                    value = line.Substring(eq + 1).Trim();
            }
            return !bool.TryParse(value, out bool enabled) || enabled;
        }
    }
}

using System.Diagnostics;

namespace TLDInstalls;

/// <summary>This user's processes on Linux, from /proc: the name, the command line and the Proton prefix
/// (STEAM_COMPAT_DATA_PATH / WINEPREFIX) each one runs in. That prefix is how a copy's game is told apart from another.</summary>
public sealed record Proc(int Pid, string Comm, string CmdLine, string? Compat, string? WinePrefix)
{
    public bool IsGame => CmdLine.Contains("TheLongDrive.exe", StringComparison.OrdinalIgnoreCase) && !CmdLine.Contains("UnityCrashHandler", StringComparison.OrdinalIgnoreCase);

    /// <summary>Does it run in this compatdata folder (or its pfx)?</summary>
    public bool InPrefix(string compat)
    {
        var c = Norm(compat);
        return (Compat != null && Norm(Compat) == c) || (WinePrefix != null && (Norm(WinePrefix) == c || Norm(WinePrefix) == Norm(Path.Combine(compat, "pfx"))));
    }

    internal static string Norm(string p) => Path.GetFullPath(p).TrimEnd('/');
}

public static class Procs
{
    public static IEnumerable<Proc> All()
    {
        if (!OperatingSystem.IsLinux()) yield break;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId) continue;
            Proc? p = null;
            try
            {
                var comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                var cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ').Trim();
                string? compat = null, wine = null;
                try
                {
                    foreach (var kv in File.ReadAllText(Path.Combine(dir, "environ")).Split('\0'))
                    {
                        if (kv.StartsWith("STEAM_COMPAT_DATA_PATH=")) compat = kv[23..];
                        else if (kv.StartsWith("WINEPREFIX=")) wine = kv[11..];
                    }
                }
                catch (UnauthorizedAccessException) { }   // another user's process
                p = new Proc(pid, comm, cmd, compat, wine);
            }
            catch (IOException) { }                       // it ended meanwhile
            catch (UnauthorizedAccessException) { }
            if (p != null) yield return p;
        }
    }

    /// <summary>Kill everything running in a prefix: the game, Proton's steam.exe, wineserver, the runtime (a game killed
    /// alone left them, and the next launch in that prefix waited on the old wineserver for good).</summary>
    public static int KillPrefix(string compat)
    {
        int n = 0;
        foreach (var p in All().Where(p => p.InPrefix(compat)).ToList())
        {
            try { Process.GetProcessById(p.Pid).Kill(); n++; }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return n;
    }
}

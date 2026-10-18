using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace TLDInstalls;

/// <summary>Starting, watching and stopping a copy's game. Steam must be running (the game signs in through it).
/// Linux: through Proton in the copy's own prefix, the way Steam starts it (Steam Linux Runtime entry point → proton
/// waitforexitandrun), as tools/game.py does for the tests. Windows: the copy's TheLongDrive.exe itself (steam_appid.txt
/// next to it), with the copy's save folder swapped in for the run (<see cref="Saves"/>).</summary>
public static class Launcher
{
    public static bool IsRunning(CopyInfo c) => GamePids(c).Count > 0;

    static List<int> GamePids(CopyInfo c)
    {
        if (Env.Windows) return WindowsGames().Where(g => SamePath(g.Path, c.Exe)).Select(g => g.Pid).ToList();
        if (c.Compat == null) return new();
        return Procs.All().Where(p => p.IsGame && p.InPrefix(c.Compat)).Select(p => p.Pid).ToList();
    }

    static bool SamePath(string? a, string b) => a != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Windows: every running TheLongDrive.exe and its path.</summary>
    static List<(int Pid, string? Path)> WindowsGames()
    {
        var l = new List<(int, string?)>();
        foreach (var p in Process.GetProcessesByName("TheLongDrive"))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            l.Add((p.Id, path));
        }
        return l;
    }

    /// <summary>Any game of The Long Drive running on this machine (a copy's or Steam's own).</summary>
    public static bool AnyGame() => Env.Windows ? WindowsGames().Count > 0 : Procs.All().Any(p => p.IsGame);

    /// <summary>Linux: a test run holds the machines (tools/mplib.py's lock): a second game would spoil it.</summary>
    public static string? TestRunHolding()
    {
        if (Env.Windows) return null;
        var lockFile = Path.Combine(Env.CacheDir, "machines.lock");
        if (!File.Exists(lockFile)) return null;
        try
        {
            // .NET takes an flock for FileShare.None on Linux: it fails while the test tools hold theirs
            using var f = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.None);
            return null;
        }
        catch (IOException)
        {
            try { return File.ReadAllText(lockFile).Trim() is { Length: > 0 } t ? t : "a test run"; } catch (IOException) { return "a test run"; }
        }
    }

    /// <summary>Start the copy's game. Windows: returns once the game is up; <paramref name="onExit"/> runs when it closes
    /// (after its saves are put back).</summary>
    public static Process? Launch(Store store, CopyInfo c, Action<string> log, bool force = false, IEnumerable<string>? args = null, Action? onExit = null)
    {
        if (IsRunning(c)) throw new UserError($"'{c.Name}' is already running");
        if (!Steam.ClientRunning()) throw new UserError("Steam isn't running: start Steam first (the game signs in through it)");
        if (!force && TestRunHolding() is { } who) throw new UserError($"a test run is using this machine ({who}): wait for it, or launch with --force");
        var g = Steam.FindGame() ?? throw new UserError("couldn't find Steam's copy of the game (for Proton and the runtime)");
        Process? p;
        if (Env.Windows) p = LaunchWindows(store, c, log, args, onExit);
        else { LaunchLinux(g, c, args); p = null; }
        c.LastLaunched = DateTime.UtcNow;
        c.Save();
        log($"Started '{c.Name}' ({Builder.Describe(c)})");
        return p;
    }

    static void LaunchLinux(SteamGame g, CopyInfo c, IEnumerable<string>? args)
    {
        var compat = c.Compat ?? throw new UserError($"'{c.Name}' has no Proton prefix in its copy.json");
        Directory.CreateDirectory(compat);
        string steam = g.SteamRoot, proton = Steam.Proton(g), slr = Steam.Runtime(g), gdir = c.GameDir;
        if (!File.Exists(Path.Combine(proton, "proton"))) throw new UserError($"no Proton at {proton} (set TLD_PROTON)");
        var env = new Dictionary<string, string>
        {
            ["STEAM_COMPAT_DATA_PATH"] = compat, ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = steam, ["STEAM_COMPAT_INSTALL_PATH"] = gdir,
            ["STEAM_COMPAT_APP_ID"] = Steam.AppId, ["SteamAppId"] = Steam.AppId, ["SteamGameId"] = Steam.AppId,
            ["STEAM_COMPAT_LIBRARY_PATHS"] = Path.Combine(steam, "steamapps"), ["STEAM_COMPAT_TOOL_PATHS"] = proton + ":" + slr,
            ["STEAM_COMPAT_MOUNTS"] = $"{Path.Combine(steam, "steamapps", "common", "Steamworks Shared")}:{proton}:{slr}",
            ["STEAM_COMPAT_FLAGS"] = "search-cwd", ["STEAM_COMPAT_PROTON"] = "1", ["STEAM_COMPAT_SHADER_PATH"] = Path.Combine(steam, "steamapps", "shadercache", Steam.AppId),
            ["SteamClientLaunch"] = "1", ["SteamEnv"] = "1", ["SteamOverlayGameId"] = Steam.AppId,
            ["TLD_COPY_LOG"] = Path.Combine(compat, "launch.log"),
        };
        if (c.Kind != Kind.Vanilla) env["WINEDLLOVERRIDES"] = "winhttp=n,b";   // BepInEx's doorstop
        // started from a shell without the desktop session (ssh): its display and bus, from the running Steam client
        if (Environment.GetEnvironmentVariable("DISPLAY") == null && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") == null)
            foreach (var kv in SessionFromSteam()) env[kv.Key] = kv.Value;
        // setsid: the game outlives this program; its output goes to the prefix's launch.log
        var psi = new ProcessStartInfo("setsid") { WorkingDirectory = gdir, UseShellExecute = false };
        foreach (var a in new[] { "-f", "sh", "-c", "exec \"$@\" >\"$TLD_COPY_LOG\" 2>&1 </dev/null", "sh",
                                  Path.Combine(slr, "_v2-entry-point"), "--verb=waitforexitandrun", "--", Path.Combine(proton, "proton"), "waitforexitandrun", c.Exe })
            psi.ArgumentList.Add(a);
        foreach (var a in args ?? []) psi.ArgumentList.Add(a);
        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    static Dictionary<string, string> SessionFromSteam()
    {
        var d = new Dictionary<string, string>();
        var steam = Procs.All().FirstOrDefault(p => p.Comm == "steam");
        if (steam == null) return d;
        try
        {
            foreach (var kv in File.ReadAllText($"/proc/{steam.Pid}/environ").Split('\0'))
                foreach (var k in new[] { "DISPLAY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "XAUTHORITY", "DBUS_SESSION_BUS_ADDRESS", "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP" })
                    if (kv.StartsWith(k + "=")) d[k] = kv[(k.Length + 1)..];
        }
        catch (IOException) { }
        return d;
    }

    static Process LaunchWindows(Store store, CopyInfo c, Action<string> log, IEnumerable<string>? args, Action? onExit)
    {
        if (AnyGame()) throw new UserError("another game of The Long Drive is running: on Windows every copy uses the same save folder, so one at a time");
        var saves = Saves.ForThisMachine(store);
        saves.SwapIn(c, log);
        File.WriteAllText(Path.Combine(c.GameDir, "steam_appid.txt"), Steam.AppId);
        var psi = new ProcessStartInfo(c.Exe) { WorkingDirectory = c.GameDir, UseShellExecute = false };
        foreach (var a in args ?? []) psi.ArgumentList.Add(a);
        psi.Environment["SteamAppId"] = Steam.AppId;
        psi.Environment["SteamGameId"] = Steam.AppId;
        Process p;
        try { p = Process.Start(psi)!; }
        catch { saves.Recover(log); throw; }
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) =>
        {
            // the game may hand over to a second process of itself: put the saves back once none is left
            for (int i = 0; i < 600 && AnyGame(); i++) Thread.Sleep(1000);
            try { saves.Recover(log); } catch (Exception e) when (e is IOException or UserError) { log("  putting the saves back failed: " + e.Message + " (the next launch tries again)"); }
            onExit?.Invoke();
        };
        return p;
    }

    /// <summary>Close the copy's game at once (the game doesn't save; Windows: its saves go back afterwards).</summary>
    public static int Stop(Store store, CopyInfo c, Action<string> log)
    {
        int n;
        if (Env.Windows)
        {
            n = 0;
            foreach (var pid in GamePids(c))
                try { Process.GetProcessById(pid).Kill(true); n++; } catch (ArgumentException) { }
            for (int i = 0; i < 30 && IsRunning(c); i++) Thread.Sleep(500);
            if (!AnyGame()) Saves.ForThisMachine(store).Recover(log);
        }
        else n = c.Compat != null ? Procs.KillPrefix(c.Compat) : 0;
        log(n > 0 ? $"Stopped '{c.Name}'" : $"'{c.Name}' wasn't running");
        return n;
    }

    // ---- what a started game reports

    public sealed record CheckResult(bool Started, string? Loaded, string? GameBuild, bool? LogBridge, bool BridgeAnswers, string Note);

    public static string PlayerLog(CopyInfo c) => Env.Windows
        ? Path.Combine(Env.Home, "AppData", "LocalLow", "Genesz", "TheLongDrive", "Player.log")
        : Path.Combine(c.Compat ?? "", "pfx", "drive_c", "users", "steamuser", "AppData", "LocalLow", "Genesz", "TheLongDrive", "Player.log");

    /// <summary>Start the copy, wait for the main menu (the game's log, and RevaMP's "loaded" line in a modded copy), read the
    /// build it reports and whether the debug bridge answers, then close it.</summary>
    public static CheckResult Check(Store store, CopyInfo c, Action<string> log, int timeout = 300, bool force = false)
    {
        var t0 = DateTime.UtcNow;
        Launch(store, c, log, force);
        try
        {
            string? loaded = null, build = null; bool? bridgeLine = null;
            bool menu = false;
            DateTime? up = null;
            while ((DateTime.UtcNow - t0).TotalSeconds < timeout)
            {
                Thread.Sleep(2000);
                if (!IsRunning(c) && (DateTime.UtcNow - t0).TotalSeconds > 60)
                    return new(false, loaded, build, bridgeLine, false, "the game isn't running (it closed or never started: see " + (c.Compat != null ? Path.Combine(c.Compat, "launch.log") : PlayerLog(c)) + ")");
                menu = menu || Fresh(PlayerLog(c), t0) is { } pl && pl.Contains("menuhandler START");   // the game's main menu
                if (c.Kind != Kind.Vanilla && Fresh(c.LogFile, t0) is { } bl)
                {
                    var m = Regex.Match(bl, @"TLD Revamp (\S+) loaded\..*?game build (\w+)\. [Bb]ridge (\w+)");
                    if (m.Success) (loaded, build, bridgeLine) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value == "on");
                }
                bool ready = menu && (c.Kind == Kind.Vanilla || loaded != null);
                if (ready && up == null) { up = DateTime.UtcNow; log("  up after " + (int)(up.Value - t0).TotalSeconds + " s"); }
                if (up != null && (DateTime.UtcNow - up.Value).TotalSeconds >= 15)   // still in the menu a while later
                {
                    if (!IsRunning(c)) return new(false, loaded, build, bridgeLine, false, "it closed shortly after starting");
                    return new(true, loaded, build, bridgeLine, BridgeAnswers(), "running");
                }
            }
            return new(false, loaded, build, bridgeLine, false, $"not up after {timeout} s");
        }
        finally { Stop(store, c, log); }
    }

    static string? Fresh(string file, DateTime since)
    {
        try
        {
            if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < since.AddSeconds(-2)) return null;
            using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(f).ReadToEnd();
        }
        catch (IOException) { return null; }
    }

    /// <summary>Does a debug bridge answer on 127.0.0.1:27050 (RevaMP's test bridge, only in a test copy)?</summary>
    public static bool BridgeAnswers()
    {
        try
        {
            using var tcp = new TcpClient();
            if (!tcp.ConnectAsync("127.0.0.1", 27050).Wait(2000)) return false;
            using var s = tcp.GetStream();
            s.ReadTimeout = 5000;
            var w = new StreamWriter(s) { NewLine = "\n" }; w.WriteLine("ping"); w.Flush();
            return new StreamReader(s).ReadLine() is { Length: > 0 };
        }
        catch (Exception e) when (e is SocketException or IOException or AggregateException) { return false; }
    }
}

using System.Diagnostics;

namespace TLDInstalls;

/// <summary>Windows: a copy's game a second time, signed in to a second Steam account, inside a Sandboxie-Plus box (one
/// Windows session runs one Steam; the box keeps the second account's login). For two players on one PC: host in one
/// game, join the other at 127.0.0.1.
/// The second game runs from its own folder in the copy (account2/game: CopyIsolation keeps its saves, settings and log
/// there, apart from the copy's), mirrored from the copy before every start - game files, BepInEx, RevaMP and its
/// add-ons - so both run exactly the same build and mod set (multiplayer refuses anything else). Its BepInEx settings
/// start as the copy's and are its own after that.</summary>
public static class SecondAccount
{
    /// <summary>The Sandboxie box (TLD_SANDBOX_BOX; default TLD2). Made on first use; its Steam login stays.</summary>
    public static string Box => Env.Get("TLD_SANDBOX_BOX") is { Length: > 0 } b ? b : "TLD2";
    public static string Dir(CopyInfo c) => Path.Combine(c.Dir, "account2");
    public static string GameDir(CopyInfo c) => Path.Combine(Dir(c), "game");
    public static string Exe(CopyInfo c) => Path.Combine(GameDir(c), "TheLongDrive.exe");

    /// <summary>Sandboxie-Plus's folder (the one with Start.exe), or null.</summary>
    public static string? Sandboxie()
    {
        if (!Env.Windows) return null;
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new[] { Path.Combine(pf, "Sandboxie-Plus"), Path.Combine(pf, "Sandboxie") }.FirstOrDefault(d => File.Exists(Path.Combine(d, "Start.exe")));
    }

    /// <summary>Clean copies keep their saves in a profile folder only their own launch sets up: not for a second account.</summary>
    public static bool Supported(CopyInfo c) => Env.Windows && c.Kind != Kind.Clean;
    public static bool IsRunning(CopyInfo c) => Launcher.PidsAt(Exe(c)).Count > 0;

    /// <summary>The second game's folder made the same as the copy's game (first time: a full copy of the game files).</summary>
    public static void Mirror(CopyInfo c, Action<string> log)
    {
        var dst = GameDir(c);
        if (!File.Exists(Exe(c)))
        {
            log($"  first start: copying the game files to {dst}");
            Files.CopyGame(c.GameDir, dst, reflink: false, log);
        }
        else if (Files.SyncGame(c.GameDir, dst, reflink: false, log) > 0) log("  game files brought up to the copy's");
        // the loader next to the exe
        foreach (var f in new[] { "winhttp.dll", "doorstop_config.ini", ".doorstop_version" })
        {
            string s = Path.Combine(c.GameDir, f), d = Path.Combine(dst, f);
            if (File.Exists(s)) Files.CopyFile(s, d);
            else if (File.Exists(d)) File.Delete(d);
        }
        // BepInEx loads every DLL under plugins and patchers: exactly the copy's, nothing left over
        foreach (var rel in new[] { "core", "plugins", "patchers" })
        {
            string s = Path.Combine(c.GameDir, "BepInEx", rel), d = Path.Combine(dst, "BepInEx", rel);
            Files.DeleteDir(d);
            if (Directory.Exists(s)) Files.CopyDir(s, d);
        }
        string cs = Path.Combine(c.GameDir, "BepInEx", "config"), cd = Path.Combine(dst, "BepInEx", "config");
        if (Directory.Exists(cs) && !Directory.Exists(cd)) Files.CopyDir(cs, cd);
        File.WriteAllText(Path.Combine(dst, "steam_appid.txt"), Steam.AppId);
        log($"  second game matches '{c.Name}' ({Builder.Describe(c)})");
    }

    /// <summary>Start the second game in the box: its Steam first (signed in), then the game. Returns once the game runs.</summary>
    public static void Launch(Store store, CopyInfo c, Action<string> log, int monitor = 0)
    {
        if (!Supported(c)) throw new UserError(Env.Windows ? "a clean copy can't run on a second account (no save isolation): use a vanilla or RevaMP copy"
                                                           : "a second Steam account needs Windows and Sandboxie-Plus");
        var sbie = Sandboxie() ?? throw new UserError("Sandboxie-Plus isn't installed (sandboxie-plus.com): it runs the second Steam account");
        if (IsRunning(c)) throw new UserError($"'{c.Name}' is already running on the second account");
        // both games the same build: the copy updates itself before ITS launch, so do that first while it isn't running
        if (c.Kind == Kind.Source && c.UpdateBeforeLaunch && !Launcher.IsRunning(c))
            c = new Builder(store, log).Build(c.Name, force: false).GetAwaiter().GetResult();
        Mirror(c, log);
        string start = Path.Combine(sbie, "Start.exe"), box = Box;
        Configure(sbie, box, store.Root, log);
        SignedIn(start, box, log);
        var args = new List<string> { "/box:" + box, Exe(c) };
        if (monitor > 0) args.AddRange(["-monitor", monitor.ToString(), "-screen-fullscreen", "1", "-window-mode", "borderless"]);
        var r = Files.RunIn(GameDir(c), start, [.. args]);
        if (r.Code != 0) throw new UserError($"Sandboxie couldn't start the game (exit {r.Code}): {(r.Err + r.Out).Trim()}");
        for (int i = 0; i < 60 && !IsRunning(c); i++) Thread.Sleep(500);
        if (!IsRunning(c)) throw new UserError($"the second game didn't start: see {Path.Combine(GameDir(c), "BepInEx", "LogOutput.log")}");
        log($"Started '{c.Name}' on the second Steam account (Sandboxie box {box}" + (monitor > 0 ? $", monitor {monitor})" : ")"));
    }

    public static int Stop(CopyInfo c, Action<string> log)
    {
        int n = 0;
        foreach (var pid in Launcher.PidsAt(Exe(c)))
            try { Process.GetProcessById(pid).Kill(true); n++; } catch (ArgumentException) { }
        for (int i = 0; i < 30 && IsRunning(c); i++) Thread.Sleep(500);
        log(n > 0 ? $"Stopped '{c.Name}' on the second account" : $"'{c.Name}' wasn't running on the second account");
        return n;
    }

    /// <summary>The box: kept between runs (the Steam login), and the copies folder opened to it, so the game writes its
    /// saves and log in place rather than into the box.</summary>
    static void Configure(string sbie, string box, string root, Action<string> log)
    {
        var ini = Path.Combine(sbie, "SbieIni.exe");
        var open = Path.Combine(Path.GetFullPath(root), "*");
        var changed = false;
        if (Query(ini, box, "Enabled").FirstOrDefault() != "y") { Ini(ini, "set", box, "Enabled", "y"); changed = true; }
        if (Query(ini, box, "AutoDelete").FirstOrDefault() != "n") { Ini(ini, "set", box, "AutoDelete", "n"); changed = true; }
        if (!Query(ini, box, "OpenFilePath").Any(v => v.Equals(open, StringComparison.OrdinalIgnoreCase)))
        { Ini(ini, "append", box, "OpenFilePath", open); changed = true; log($"  box {box}: {open} writable from inside"); }
        if (changed) Files.Run(Path.Combine(sbie, "Start.exe"), "/reload");
    }

    static List<string> Query(string ini, string box, string key) =>
        Files.Run(ini, "query", box, key).Out.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    static void Ini(string ini, params string[] args)
    {
        var r = Files.Run(ini, args);
        if (r.Code != 0) throw new UserError($"Sandboxie's settings: {string.Join(' ', args)} failed: {(r.Err + r.Out).Trim()}");
    }

    /// <summary>Steam running in the box and signed in. The first time it asks for the second account's login there.</summary>
    static void SignedIn(string start, string box, Action<string> log)
    {
        if (ActiveUser(box) is > 0 && SteamInBox(start, box)) return;
        if (!SteamInBox(start, box))
        {
            var steam = Steam.Roots(true).Select(r => Path.Combine(r, "steam.exe")).FirstOrDefault(File.Exists)
                        ?? throw new UserError("couldn't find steam.exe");
            log($"Starting Steam in Sandboxie box {box}: the first time, sign in there with the SECOND account");
            Files.Run(start, "/box:" + box, steam);
        }
        log("  waiting for the second account's Steam to sign in…");
        for (int i = 0; i < 300; i++)   // 5 minutes: time to type a password and a Steam Guard code
        {
            Thread.Sleep(1000);
            if (ActiveUser(box) is > 0) { Thread.Sleep(3000); return; }   // a moment for Steam to settle before the game asks it
            if (ActiveUser(box) == null && i >= 25 && SteamInBox(start, box)) return;   // can't see the login: Steam's had 25 s
        }
        throw new UserError("the second account's Steam didn't sign in within 5 minutes");
    }

    static bool SteamInBox(string start, string box)
    {
        foreach (var line in Files.Run(start, "/box:" + box, "/listpids").Out.Split('\n'))
            if (int.TryParse(line.Trim(), out var pid))
                try { using var p = Process.GetProcessById(pid); if (p.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase)) return true; }
                catch (ArgumentException) { }
        return false;
    }

    /// <summary>The box's Steam's signed-in account id (0 = not signed in), from the box's registry while it runs; null when
    /// that can't be read.</summary>
    static int? ActiveUser(string box)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var hive = Microsoft.Win32.Registry.Users.OpenSubKey($"Sandbox_{Environment.UserName}_{box}");   // loaded while the box runs
            if (hive == null) return null;
            using var k = hive.OpenSubKey(@"user\current\Software\Valve\Steam\ActiveProcess");
            return k?.GetValue("ActiveUser") is int v ? v : 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}

using System.Diagnostics;

namespace TLDInstalls;

/// <summary>The game as Steam installed it: the source every copy is made from (never changed by this tool).</summary>
public sealed record SteamGame(string SteamRoot, string Library, string GameDir, string? BuildId, string? Branch = null)
{
    public string Manifest => Path.Combine(Library, "steamapps", $"appmanifest_{Steam.AppId}.acf");
    /// <summary>The game's own Proton prefix (Linux): only read, as a source for a copy's prefix.</summary>
    public string Compat => Path.Combine(Library, "steamapps", "compatdata", Steam.AppId);
}

public static class Steam
{
    public const string AppId = "1017180";

    /// <summary>Reads Steam's folder from the Windows registry (HKCU\Software\Valve\Steam, SteamPath). Tests replace it.</summary>
    public static Func<string?> RegistrySteamPath = ReadRegistry;

    static string? ReadRegistry()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return k?.GetValue("SteamPath") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>Steam folders to look in: TLD_STEAM; on Windows the registry's SteamPath, then the default install folder;
    /// elsewhere the places Steam lives (normal, Flatpak, Snap), as installer/install.sh looks.</summary>
    public static List<string> Roots(bool? windows = null, string? home = null)
    {
        bool win = windows ?? Env.Windows;
        home ??= Env.Home;
        var c = new List<string>();
        if (Env.GetPath("TLD_STEAM") is { } s) c.Add(s);
        if (win)
        {
            if (RegistrySteamPath() is { Length: > 0 } r) c.Add(r);   // Steam writes it with / (Windows takes either)
            c.Add(@"C:\Program Files (x86)\Steam");
        }
        else
            foreach (var d in new[] { ".local/share/Steam", ".steam/steam", ".steam/root", ".var/app/com.valvesoftware.Steam/.local/share/Steam", "snap/steam/common/.local/share/Steam" })
                c.Add(Path.Combine(home, d));
        var seen = new HashSet<string>(win ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var outp = new List<string>();
        foreach (var d in c)
        {
            if (!Directory.Exists(Path.Combine(d, "steamapps"))) continue;
            var real = Resolve(d);
            if (seen.Add(real)) outp.Add(d);
        }
        return outp;
    }

    static string Resolve(string d)
    {
        try { return new DirectoryInfo(d).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(d); }
        catch (IOException) { return Path.GetFullPath(d); }
    }

    /// <summary>The Steam folder itself, then every "path" of steamapps/libraryfolders.vdf.</summary>
    public static List<string> Libraries(string root)
    {
        var libs = new List<string> { root };
        var vdf = Vdf.Load(Path.Combine(root, "steamapps", "libraryfolders.vdf"));
        var top = vdf?.Block("libraryfolders") ?? vdf;
        if (top != null)
            foreach (var (_, b) in top.Order)
                if (b["path"] is { Length: > 0 } p && !libs.Contains(p)) libs.Add(p);
        return libs;
    }

    /// <summary>The game's install: the library whose appmanifest_1017180.acf names a folder with TheLongDrive.exe.</summary>
    public static SteamGame? FindGame(IEnumerable<string>? roots = null)
    {
        foreach (var root in roots ?? Roots())
            foreach (var lib in Libraries(root))
            {
                var acf = Vdf.Load(Path.Combine(lib, "steamapps", $"appmanifest_{AppId}.acf"));
                var st = acf?.Block("AppState") ?? acf;
                if (st?["installdir"] is not { Length: > 0 } dir) continue;
                var game = Path.Combine(lib, "steamapps", "common", dir);
                if (File.Exists(Path.Combine(game, "TheLongDrive.exe"))) return new SteamGame(root, lib, game, st["buildid"], Branch(st));
            }
        return null;
    }

    /// <summary>Steam's current build and branch, read together when copying or displaying the game.</summary>
    public static (string? BuildId, string? Branch) BuildInfo(SteamGame g)
    {
        var acf = Vdf.Load(g.Manifest);
        var st = acf?.Block("AppState") ?? acf;
        return (st?["buildid"], st == null ? null : Branch(st));
    }

    public static string? BuildId(SteamGame g) => BuildInfo(g).BuildId;

    static string Branch(Vdf st)
    {
        var key = st.Block("UserConfig")?["BetaKey"] ?? st.Block("MountedConfig")?["BetaKey"];
        return string.IsNullOrEmpty(key) ? "public" : key;
    }

    /// <summary>The Proton the copies run with (Linux): TLD_PROTON, else the one Steam is set to use for the game
    /// (config.vdf CompatToolMapping), else Proton Experimental.</summary>
    public static string Proton(SteamGame g)
    {
        if (Env.GetPath("TLD_PROTON") is { } p) return p;
        var cfg = Vdf.Load(Path.Combine(g.SteamRoot, "config", "config.vdf"));
        var name = cfg?.Block("InstallConfigStore")?.Block("Software")?.Block("Valve")?.Block("Steam")?.Block("CompatToolMapping")?.Block(AppId)?["name"];
        if (name is { Length: > 0 })
        {
            var custom = Path.Combine(g.SteamRoot, "compatibilitytools.d", name);
            if (File.Exists(Path.Combine(custom, "proton"))) return custom;
            // Valve's own: proton_experimental, proton_10, proton_9, … in steamapps/common
            var official = name == "proton_experimental" ? "Proton - Experimental"
                         : name.StartsWith("proton_") ? "Proton " + name[7..].Replace('_', '.') + (name.Count(ch => ch == '_') == 1 ? ".0" : "") : null;
            foreach (var lib in Libraries(g.SteamRoot))
                if (official != null && File.Exists(Path.Combine(lib, "steamapps", "common", official, "proton")))
                    return Path.Combine(lib, "steamapps", "common", official);
        }
        return Path.Combine(g.SteamRoot, "steamapps", "common", "Proton - Experimental");
    }

    /// <summary>The Steam Linux Runtime Proton runs in: TLD_SLR, else SteamLinuxRuntime_4 (or _sniper) next to the game.</summary>
    public static string Runtime(SteamGame g)
    {
        if (Env.GetPath("TLD_SLR") is { } p) return p;
        foreach (var n in new[] { "SteamLinuxRuntime_4", "SteamLinuxRuntime_sniper" })
            foreach (var lib in Libraries(g.SteamRoot))
            {
                var d = Path.Combine(lib, "steamapps", "common", n);
                if (File.Exists(Path.Combine(d, "_v2-entry-point"))) return d;
            }
        return Path.Combine(g.SteamRoot, "steamapps", "common", "SteamLinuxRuntime_4");
    }

    /// <summary>Is the Steam client running? The game needs it (it signs in through Steam).</summary>
    public static bool ClientRunning()
    {
        if (Env.Windows) return Process.GetProcessesByName("steam").Length > 0;
        return Procs.All().Any(p => p.Comm == "steam");
    }
}

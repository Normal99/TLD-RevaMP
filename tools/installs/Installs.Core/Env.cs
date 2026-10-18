using System.Text.Json;

namespace TLDInstalls;

/// <summary>Where things are on this machine. Settings come from environment variables, then the test tools' files
/// (tools/local.env of the checkout, then ~/.config/tld-revamp/tools.env: KEY=value lines, see tools/local.env.example),
/// then this tool's own settings file (the copies folder chosen in the window).</summary>
public static class Env
{
    public static bool Windows => OperatingSystem.IsWindows();
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>This tool's settings (installs.json): %APPDATA%\tld-revamp on Windows, ~/.config/tld-revamp elsewhere.</summary>
    public static string ConfigDir => Windows
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tld-revamp")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config"), "tld-revamp");

    static Dictionary<string, string>? _file;

    /// <summary>The checkout this tool belongs to (src/TLDRevamp/Plugin.cs above the program or the current folder), or
    /// TLD_REPO_DIR; null when it runs on its own.</summary>
    public static string? RepoDir { get; set; } = FindRepo();

    static string? FindRepo()
    {
        if (Environment.GetEnvironmentVariable("TLD_REPO_DIR") is { Length: > 0 } r) return r;
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "src", "TLDRevamp", "Plugin.cs"))) return d.FullName;
        return null;
    }

    /// <summary>A setting: the environment variable, else tools/local.env, else ~/.config/tld-revamp/tools.env.</summary>
    public static string? Get(string key)
    {
        if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } v) return v;
        _file ??= LoadFiles();
        return _file.TryGetValue(key, out var f) && f.Length > 0 ? f : null;
    }

    /// <summary>A path setting with ~ and $HOME expanded.</summary>
    public static string? GetPath(string key) => Get(key) is { } v ? ExpandUser(v) : null;

    public static string ExpandUser(string p)
    {
        if (p == "~") p = Home;
        else if (p.StartsWith("~/") || p.StartsWith("~\\")) p = Path.Combine(Home, p[2..]);
        return p.Replace("$HOME", Home);
    }

    static Dictionary<string, string> LoadFiles()
    {
        var d = new Dictionary<string, string>();
        var files = new List<string>();
        if (RepoDir != null) files.Add(Path.Combine(RepoDir, "tools", "local.env"));
        files.Add(Path.Combine(Home, ".config", "tld-revamp", "tools.env"));
        foreach (var f in files) foreach (var kv in ParseEnvFile(f)) d.TryAdd(kv.Key, kv.Value);
        return d;
    }

    /// <summary>KEY=value lines (# comments, optional `export `, quotes stripped), as tools/tldenv.py reads them.</summary>
    public static Dictionary<string, string> ParseEnvFile(string path)
    {
        var d = new Dictionary<string, string>();
        if (!File.Exists(path)) return d;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('=')) continue;
            var i = line.IndexOf('=');
            var k = line[..i].Trim();
            if (k.StartsWith("export ")) k = k[7..].Trim();
            d.TryAdd(k, line[(i + 1)..].Trim().Trim('"', '\''));
        }
        return d;
    }

    /// <summary>Downloads and the release list: TLD_CACHE (the test tools' cache), else ~/tld-revamp-cache, or
    /// %LOCALAPPDATA%\tld-revamp\cache on Windows.</summary>
    public static string CacheDir => GetPath("TLD_CACHE") ?? (Windows
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tld-revamp", "cache")
        : Path.Combine(Home, "tld-revamp-cache"));

    // ---- this tool's own settings file

    sealed class Settings { public string? Root { get; set; } }

    static string SettingsFile => Path.Combine(ConfigDir, "installs.json");

    static Settings ReadSettings()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>The folder the copies live in: TLD_INSTALLS, else the one chosen in the window, else ~/tld-installs
    /// (%USERPROFILE%\tld-installs on Windows).</summary>
    public static string DefaultRoot => GetPath("TLD_INSTALLS") ?? ReadSettings().Root ?? Path.Combine(Home, "tld-installs");

    public static void SaveRoot(string root)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new Settings { Root = root }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

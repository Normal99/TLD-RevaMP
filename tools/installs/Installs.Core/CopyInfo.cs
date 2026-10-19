using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TLDInstalls;

public enum Kind { Clean = -1, Vanilla, Release, Dev, Test, Source }

/// <summary>What one copy holds, written to &lt;copy&gt;/copy.json whenever it is made or updated.</summary>
public sealed class CopyInfo
{
    public string Name { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter<Kind>))] public Kind Kind { get; set; }
    /// <summary>The RevaMP version the game shows (e.g. 0.69.1, 0.69.1-dev.7d4cb63); null in a clean or vanilla copy.</summary>
    public string? RevampVersion { get; set; }
    /// <summary>Where it came from: the release tag, or the commit a dev/test build was built from.</summary>
    public string? RevampSource { get; set; }
    public CommitInfo? Commit { get; set; }
    public string? SourceRef { get; set; }
    public bool UpdateBeforeLaunch { get; set; } = true;
    public int? NewerCommits { get; set; }
    /// <summary>SHA-256 of the release or dev zip it was installed from (a test build: of TLDRevamp.dll).</summary>
    public string? RevampSha256 { get; set; }
    public string? BepInEx { get; set; }
    /// <summary>Steam's build id of the game files when they were copied, and the SHA-256 of Assembly-CSharp.dll.</summary>
    public string? GameBuildId { get; set; }
    /// <summary>Steam's branch when the game files were copied (public if no BetaKey); null in older copies.</summary>
    public string? GameBranch { get; set; }
    public string? GameDllSha256 { get; set; }
    public string? GameSource { get; set; }
    public bool Bridge { get; set; }
    /// <summary>All of RevaMP's telemetry was turned on in its config (Builder.EnsureTelemetry; once per copy).</summary>
    public bool Telemetry { get; set; }
    /// <summary>Linux: the Proton prefix (compatdata folder) the copy runs in; a test copy shares the tests' own.</summary>
    public string? Compat { get; set; }
    /// <summary>The copy's files share their disk blocks with Steam's (a reflink copy on btrfs/XFS).</summary>
    public bool Reflink { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>Bytes only this copy uses (btrfs; null where it can't be measured).</summary>
    public long? OwnBytes { get; set; }
    public DateTime Made { get; set; }
    public DateTime? Updated { get; set; }
    public DateTime? LastLaunched { get; set; }

    [JsonIgnore] public string Dir { get; set; } = "";
    [JsonIgnore] public string GameDir => Path.Combine(Dir, "game");
    [JsonIgnore] public string Exe => Path.Combine(GameDir, "TheLongDrive.exe");
    /// <summary>Windows: the old save layout, migrated once on launch or update.</summary>
    [JsonIgnore] public string SavesStore => Path.Combine(Dir, "saves", "TheLongDrive");
    [JsonIgnore] public string Profile => Path.Combine(Dir, "profile");
    [JsonIgnore] public string UserData => Kind == Kind.Clean ? Path.Combine(Profile, "Documents", "TheLongDrive") : Path.Combine(Dir, "UserData");
    [JsonIgnore] public string LogFile => Path.Combine(GameDir, "BepInEx", "LogOutput.log");
    [JsonIgnore] public string GameLabel => $"{GameBranch ?? "unknown"} branch, game build {GameBuildId ?? "?"}";
    [JsonIgnore] public bool HasRevamp => Kind is Kind.Release or Kind.Dev or Kind.Test or Kind.Source;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static CopyInfo? Load(string dir)
    {
        try
        {
            var c = JsonSerializer.Deserialize<CopyInfo>(File.ReadAllText(Path.Combine(dir, "copy.json")), Json);
            if (c != null) c.Dir = dir;
            return c;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Save() => File.WriteAllText(Path.Combine(Dir, "copy.json"), JsonSerializer.Serialize(this, Json) + "\n");

    [JsonIgnore] public string KindLabel => Kind switch
    {
        Kind.Clean => "clean",
        Kind.Vanilla => "vanilla",
        Kind.Release => "release",
        Kind.Dev => "dev build",
        Kind.Source => "source",
        _ => "test",
    };
}

/// <summary>The copies folder: one subfolder per copy (game/, compat/ on Linux, UserData/ or profile/ on Windows, copy.json).</summary>
public sealed class Store
{
    public string Root { get; }
    public Store(string? root = null) { Root = Path.GetFullPath(root ?? Env.DefaultRoot); }

    static readonly Regex NameRx = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,39}$");
    public static bool ValidName(string n) => NameRx.IsMatch(n);

    public List<CopyInfo> List()
    {
        if (!Directory.Exists(Root)) return new();
        return Directory.EnumerateDirectories(Root).Select(CopyInfo.Load).OfType<CopyInfo>()
                        .OrderBy(c => c.Kind).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public CopyInfo? Get(string name) => ValidName(name) ? CopyInfo.Load(Path.Combine(Root, name)) : null;

    public CopyInfo Need(string name) => Get(name) ?? throw new UserError($"no copy called '{name}' in {Root}");

    public string PathOf(string name) => Path.Combine(Root, name);
}

/// <summary>A failure to show as it is (no stack trace): what went wrong and what to do.</summary>
public sealed class UserError(string message) : Exception(message);

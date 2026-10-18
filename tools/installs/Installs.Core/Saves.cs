namespace TLDInstalls;

/// <summary>One-release cleanup for save folders left swapped by older versions. New launches use UserData and
/// never swap Documents. Recovery only runs while no game uses the old shared folder (the caller checks).</summary>
public sealed class Saves(string documents, Store store)
{
    public const string Marker = ".tld-copy";
    public const string SteamOwner = "steam";

    /// <summary>The save folder the game uses.</summary>
    public string Live => Path.Combine(documents, "TheLongDrive");
    /// <summary>Steam's own save folder while a copy's is in its place.</summary>
    public string Parked => Path.Combine(documents, "TheLongDrive (Steam's, set aside while a copy runs)");

    public static Saves ForThisMachine(Store store) => new(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), store);

    public static string Owner(string folder)
    {
        var m = Path.Combine(folder, Marker);
        return File.Exists(m) ? File.ReadAllText(m).Trim() : SteamOwner;
    }

    /// <summary>Put a stranded copy's data into UserData, then restore Steam's Documents folder.</summary>
    public void Recover(Action<string> log)
    {
        if (Directory.Exists(Live) && Owner(Live) is var owner && owner != SteamOwner)
        {
            var c = store.Get(owner);
            var dest = c?.UserData ?? Path.Combine(store.Root, "orphaned-saves", $"{owner}-{DateTime.Now:yyyyMMdd-HHmmss}");
            log($"  '{owner}' saves back to {dest}");
            if (Directory.Exists(dest) && !Directory.EnumerateFileSystemEntries(dest).Any()) Directory.Delete(dest);
            Files.MoveDir(Live, dest);
        }
        if (!Directory.Exists(Live) && Directory.Exists(Parked))
        {
            log($"  Steam's saves back in place: {Live}");
            Files.MoveDir(Parked, Live);
        }
    }
}

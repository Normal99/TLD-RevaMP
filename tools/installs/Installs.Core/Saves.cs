namespace TLDInstalls;

/// <summary>Windows: every copy of the game keeps its saves and settings in the same folder, Documents\TheLongDrive
/// (on Linux each copy has a Proton prefix of its own instead). So a copy's folder is swapped in for its launch and back
/// out when the game closes: Steam's own folder is set aside next to it while a copy runs, and every folder carries a
/// marker file saying whose it is. Folders are only renamed (or copied, checked, then deleted), never overwritten, so a
/// save is never lost; a swap left over (the tool closed before the game) is undone the next time the tool runs.
/// The game's PlayerPrefs (registry, HKCU\Software\Genesz\TheLongDrive: a few options) stay shared.</summary>
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

    /// <summary>Put the copy's save folder in place (before its game starts).</summary>
    public void SwapIn(CopyInfo c, Action<string> log)
    {
        Recover(log);
        if (Directory.Exists(Live))
        {
            var owner = Owner(Live);
            if (owner == c.Name) return;
            if (owner != SteamOwner) throw new UserError($"{Live} holds the saves of the copy '{owner}': close its game first");
            if (Directory.Exists(Parked)) throw new UserError($"both {Live} and {Parked} exist: move one of them away by hand (nothing was changed)");
            log($"  Steam's saves set aside: {Parked}");
            Files.MoveDir(Live, Parked);
        }
        if (Directory.Exists(c.SavesStore)) Files.MoveDir(c.SavesStore, Live);
        else Directory.CreateDirectory(Live);
        File.WriteAllText(Path.Combine(Live, Marker), c.Name);
        log($"  '{c.Name}' saves in place: {Live}");
    }

    /// <summary>Put every save folder back where it belongs: a copy's into its own folder, Steam's back in place. Only
    /// while no game runs (the caller checks).</summary>
    public void Recover(Action<string> log)
    {
        if (Directory.Exists(Live) && Owner(Live) is var owner && owner != SteamOwner)
        {
            var c = store.Get(owner);
            var dest = c?.SavesStore ?? Path.Combine(store.Root, "orphaned-saves", $"{owner}-{DateTime.Now:yyyyMMdd-HHmmss}");
            log($"  '{owner}' saves back to {dest}");
            Files.MoveDir(Live, dest);
        }
        if (!Directory.Exists(Live) && Directory.Exists(Parked))
        {
            log($"  Steam's saves back in place: {Live}");
            Files.MoveDir(Parked, Live);
        }
    }
}

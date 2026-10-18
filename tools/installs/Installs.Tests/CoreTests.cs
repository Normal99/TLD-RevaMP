using System.IO.Compression;
using Xunit;

namespace TLDInstalls.Tests;

/// <summary>What can be checked without the game, Steam or Windows: Steam's files and the registry value faked, the plain
/// (Windows) copy forced, old Windows save recovery on a fake Documents folder.</summary>
public sealed class CoreTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("tld-installs-test").FullName;
    public void Dispose() { Steam.RegistrySteamPath = () => null; Directory.Delete(tmp, true); }

    string Write(string rel, string text = "x")
    {
        var p = Path.Combine(tmp, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    /// <summary>A Steam folder with the game in a second library, as libraryfolders.vdf lists it.</summary>
    (string Steam, string Game) FakeSteam(string libPath)
    {
        var steam = Path.Combine(tmp, "Steam");
        var esc = libPath.Replace("\\", "\\\\");
        Write("Steam/steamapps/libraryfolders.vdf", $$"""
            "libraryfolders"
            {
            	"0" { "path"		"{{steam.Replace("\\", "\\\\")}}" "apps" { "228980" "0" } }
            	"1" { "path"		"{{esc}}"  "apps" { "1017180" "123" } }
            }
            """);
        var game = Path.Combine(libPath, "steamapps", "common", "The Long Drive");
        Directory.CreateDirectory(libPath);
        File.WriteAllText(Path.Combine(libPath, "steamapps", "appmanifest_1017180.acf").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"1017180\"\n\t\"installdir\"\t\t\"The Long Drive\"\n\t\"buildid\"\t\t\"16539689\"\n}\n");
        foreach (var f in new[] { "TheLongDrive.exe", "UnityPlayer.dll", "UnityCrashHandler64.exe", "TheLongDrive_Data/Managed/Assembly-CSharp.dll",
                                  "TheLongDrive_Data/news.txt", "TheLongDrive_Data/mpshot_A.png", "MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll",
                                  "BepInEx/core/BepInEx.dll", "winhttp.dll", "doorstop_config.ini", "SomethingNew.dll" })
            Write(Path.GetRelativePath(tmp, Path.Combine(game, f)), f);
        return (steam, game);
    }

    [Fact]
    public void VdfUnescapesWindowsPaths()
    {
        var v = Vdf.Parse("\"libraryfolders\" { \"1\" { \"path\" \"D:\\\\SteamLibrary\" } }");
        Assert.Equal(@"D:\SteamLibrary", v.Block("libraryfolders")!.Block("1")!["path"]);
    }

    [Fact]
    public void FindsTheGameInASecondLibrary()
    {
        var (steam, game) = FakeSteam(Path.Combine(tmp, "Library2"));
        var g = Steam.FindGame(new[] { steam });
        Assert.NotNull(g);
        Assert.Equal(game, g!.GameDir);
        Assert.Equal("16539689", g.BuildId);
    }

    [Fact]
    public void WindowsRootsComeFromTheRegistry()
    {
        var (steam, _) = FakeSteam(Path.Combine(tmp, "Library2"));
        Steam.RegistrySteamPath = () => steam.Replace('\\', '/');
        var roots = Steam.Roots(windows: true, home: tmp);
        Assert.Contains(roots, r => Path.GetFullPath(r) == Path.GetFullPath(steam));
        Steam.RegistrySteamPath = () => null;
        Assert.DoesNotContain(Steam.Roots(windows: true, home: tmp), r => Path.GetFullPath(r) == Path.GetFullPath(steam));
    }

    [Fact]
    public void ProtonFromSteamsCompatToolMapping()
    {
        var (steam, game) = FakeSteam(Path.Combine(tmp, "Library2"));
        Write("Steam/config/config.vdf", "\"InstallConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"CompatToolMapping\" { \"1017180\" { \"name\" \"GE-Proton11-3\" } } } } } }");
        Write("Steam/compatibilitytools.d/GE-Proton11-3/proton");
        if (Env.Get("TLD_PROTON") != null) return;   // this machine's setting wins
        Assert.Equal(Path.Combine(steam, "compatibilitytools.d", "GE-Proton11-3"), Steam.Proton(new SteamGame(steam, Path.Combine(tmp, "Library2"), game, null)));
    }

    [Fact]
    public void PlainCopyTakesOnlyTheGamesFilesAndSyncFollowsSteam()
    {
        var (_, game) = FakeSteam(Path.Combine(tmp, "Library2"));
        var dst = Path.Combine(tmp, "copy", "game");
        var log = new List<string>();
        Files.CopyGame(game, dst, reflink: false, log.Add);   // the Windows path (no reflinks there)
        Assert.True(File.Exists(Path.Combine(dst, "TheLongDrive_Data", "Managed", "Assembly-CSharp.dll")));
        Assert.True(File.Exists(Path.Combine(dst, "MonoBleedingEdge", "EmbedRuntime", "mono-2.0-bdwgc.dll")));
        Assert.False(File.Exists(Path.Combine(dst, "TheLongDrive_Data", "mpshot_A.png")));
        Assert.False(Directory.Exists(Path.Combine(dst, "BepInEx")));
        Assert.False(File.Exists(Path.Combine(dst, "winhttp.dll")));
        Assert.Contains(log, l => l.Contains("not copied: SomethingNew.dll"));
        Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(game, "UnityPlayer.dll")), File.GetLastWriteTimeUtc(Path.Combine(dst, "UnityPlayer.dll")));

        Assert.Equal(0, Files.SyncGame(game, dst, false, log.Add));
        File.WriteAllText(Path.Combine(game, "TheLongDrive_Data", "news.txt"), "a longer text: Steam updated the game");
        File.Delete(Path.Combine(game, "UnityCrashHandler64.exe"));
        Write(Path.GetRelativePath(tmp, Path.Combine(game, "TheLongDrive_Data", "level1")));
        File.WriteAllText(Path.Combine(dst, "TheLongDrive_Data", "mpshot_B.png"), "a test screenshot stays");
        Assert.Equal(2, Files.SyncGame(game, dst, false, log.Add));   // news.txt, level1 (a deleted root file stays: it's the game's own list)
        Assert.Equal("a longer text: Steam updated the game", File.ReadAllText(Path.Combine(dst, "TheLongDrive_Data", "news.txt")));
        Assert.True(File.Exists(Path.Combine(dst, "TheLongDrive_Data", "mpshot_B.png")));
    }

    [Fact]
    public void MoveDirNeverOverwrites()
    {
        Write("a/save.txt", "mine"); Write("b/save.txt", "other");
        Assert.Throws<UserError>(() => Files.MoveDir(Path.Combine(tmp, "a"), Path.Combine(tmp, "b")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(tmp, "a", "save.txt")));
    }

    [Fact]
    public void RecoversOldWindowsSwapIntoUserData()
    {
        var docs = Path.Combine(tmp, "Documents");
        var store = new Store(Path.Combine(tmp, "copies"));
        CopyInfo Copy(string n)
        {
            var c = new CopyInfo { Name = n, Kind = Kind.Release, Dir = store.PathOf(n) };
            Directory.CreateDirectory(c.Dir); c.Save();
            return c;
        }
        var a = Copy("a");
        var s = new Saves(docs, store);
        var log = new List<string>();
        Directory.CreateDirectory(Path.Combine(s.Parked, "Saves"));
        File.WriteAllText(Path.Combine(s.Parked, "Saves", "steam.tld"), "steam's world");
        Write("Documents/TheLongDrive/" + Saves.Marker, "a");
        Write("Documents/TheLongDrive/Saves/a.tld", "a's world");
        s.Recover(log.Add);
        Assert.Equal("steam's world", File.ReadAllText(Path.Combine(s.Live, "Saves", "steam.tld")));
        Assert.Equal("a's world", File.ReadAllText(Path.Combine(a.UserData, "Saves", "a.tld")));
        Assert.False(Directory.Exists(s.Parked));
        s.Recover(log.Add);
        Assert.Equal("steam", Saves.Owner(s.Live));
        Assert.Equal("steam's world", File.ReadAllText(Path.Combine(s.Live, "Saves", "steam.tld")));
    }

    [Fact]
    public void SavesOfARemovedCopyAreKept()
    {
        var docs = Path.Combine(tmp, "Documents");
        var store = new Store(Path.Combine(tmp, "copies"));
        Write("Documents/TheLongDrive/" + Saves.Marker, "gone");
        Write("Documents/TheLongDrive/w.tld", "gone's world");
        new Saves(docs, store).Recover(_ => { });
        var kept = Directory.GetDirectories(Path.Combine(store.Root, "orphaned-saves")).Single();
        Assert.Equal("gone's world", File.ReadAllText(Path.Combine(kept, "w.tld")));
    }

    [Fact]
    public void ReleaseListOffersOnlyPublishedZipsWithAChecksum()
    {
        var json = """
        [ {"tag_name":"v0.70.0-dev.abc","draft":true,"prerelease":true,"published_at":null,"assets":[]},
          {"tag_name":"v0.69.1","draft":false,"prerelease":true,"published_at":"2026-10-17T20:00:00Z","assets":[
             {"name":"install.sh","digest":"sha256:00","browser_download_url":"u1"},
             {"name":"TLD-RevaMP-0.69.1.zip","digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","browser_download_url":"https://x/TLD-RevaMP-0.69.1.zip"}]},
          {"tag_name":"v0.60.0","draft":false,"prerelease":false,"published_at":"2026-09-01T00:00:00Z","assets":[
             {"name":"TLD-RevaMP-0.60.0.zip","digest":null,"browser_download_url":"u3"}]} ]
        """;
        var l = Releases.Parse(json);
        Assert.Single(l);
        Assert.Equal(("0.69.1", new string('a', 64)), (l[0].Version, l[0].ZipSha256));
    }

    [Fact]
    public void PluginComesOutOfItsFolderInTheZip()
    {
        var zip = Path.Combine(tmp, "r.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var n in new[] { "BepInEx/core/BepInEx.dll", "BepInEx/plugins/TLDRevamp/TLDRevamp.dll", "BepInEx/plugins/TLDRevamp/approved-mods.txt", "INSTALL.txt" })
                using (var w = new StreamWriter(z.CreateEntry(n).Open())) w.Write(n);
        var game = Path.Combine(tmp, "game");
        var files = Releases.ExtractPlugin(zip, game);
        Assert.Equal(new[] { "TLDRevamp.dll", "approved-mods.txt" }, files);
        Assert.True(File.Exists(Path.Combine(game, "BepInEx", "plugins", "TLDRevamp", "TLDRevamp.dll")));
        Assert.False(File.Exists(Path.Combine(game, "INSTALL.txt")));

        var evil = Path.Combine(tmp, "evil.zip");
        using (var z = ZipFile.Open(evil, ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry("../outside.txt").Open())) w.Write("x");
        Assert.Throws<UserError>(() => Releases.Extract(evil, Path.Combine(tmp, "dest")));
        Assert.False(File.Exists(Path.Combine(tmp, "outside.txt")));
    }

    [Fact]
    public void BepInExPinMatchesTheInstaller()
    {
        var sh = Path.Combine(Env.RepoDir ?? throw new Exception("run from the checkout"), "installer", "install.sh");
        var pins = Releases.InstallerPins(sh);
        Assert.Equal(Releases.BepInExUrlDefault, pins["BEPINEX_URL"]);
        Assert.Equal(Releases.BepInExShaDefault, pins["BEPINEX_SHA256"]);
    }

    [Fact]
    public void StrippedPrefixLosesTheGamesSettingsOnly()
    {
        var c = Path.Combine(tmp, "compat");
        Write("compat/pfx/drive_c/users/steamuser/Documents/TheLongDrive/settings.tldc");
        Write("compat/pfx/drive_c/users/steamuser/AppData/LocalLow/Genesz/TheLongDrive/Player.log");
        Write("compat/pfx/user.reg", "WINE REGISTRY Version 2\n\n[Software\\\\Genesz\\\\TheLongDrive] 1760000000\n#time=1\n\"FPS\"=dword:0000003c\n\n[Software\\\\Wine\\\\DllOverrides] 1\n\"winhttp\"=\"native,builtin\"\n");
        Builder.StripPrefix(c);
        var reg = File.ReadAllText(Path.Combine(c, "pfx", "user.reg"));
        Assert.DoesNotContain("Genesz", reg);
        Assert.Contains("\"winhttp\"=\"native,builtin\"", reg);
        Assert.False(Directory.Exists(Path.Combine(c, "pfx", "drive_c", "users", "steamuser", "Documents", "TheLongDrive")));
    }

    [Fact]
    public void BridgeLineKeepsTheConfigsLineEndings()
    {
        var cfg = Write("g/BepInEx/config/tldrevamp.core.cfg", "[Debug]\r\n\r\n## the bridge\r\nBridge = false\r\nOther = 1\r\n");
        Builder.SetBridge(Path.Combine(tmp, "g"), true);
        Assert.Equal("[Debug]\r\n\r\n## the bridge\r\nBridge = true\r\nOther = 1\r\n", File.ReadAllText(cfg));
    }

    [Fact]
    public void EnvFileAsTldenvReadsIt()
    {
        var f = Write("tools.env", "# c\nexport TLD_A=\"~/x\"\nTLD_B = 'y'\nnot a line\n");
        var d = Env.ParseEnvFile(f);
        Assert.Equal("~/x", d["TLD_A"]);
        Assert.Equal("y", d["TLD_B"]);
        Assert.Equal(Path.Combine(Env.Home, "x"), Env.ExpandUser("~/x"));
    }

    [Fact]
    public void CopyNamesAreSafeFolderNames()
    {
        Assert.True(Store.ValidName("release-0.68.5"));
        foreach (var bad in new[] { "", "..", "a/b", "a\\b", ".hidden", "with space", new string('a', 41) }) Assert.False(Store.ValidName(bad));
    }
}

static class Ext
{
    public static T Also<T>(this T x, Action<T> f) { f(x); return x; }
}

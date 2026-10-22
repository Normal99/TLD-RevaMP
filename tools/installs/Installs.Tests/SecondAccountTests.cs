using Xunit;

namespace TLDInstalls.Tests;

public sealed class SecondAccountTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("tld-copies-second-test").FullName;
    public void Dispose() => Directory.Delete(tmp, true);

    static void Put(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    CopyInfo Copy()
    {
        var c = new CopyInfo { Name = "dev", Dir = Path.Combine(tmp, "dev"), Kind = Kind.Dev, RevampVersion = "0.69.1-dev.abc" };
        foreach (var f in new[] { "TheLongDrive.exe", "UnityPlayer.dll", "UnityCrashHandler64.exe" }) Put(Path.Combine(c.GameDir, f), f);
        Put(Path.Combine(c.GameDir, "TheLongDrive_Data", "level0"), "level");
        Put(Path.Combine(c.GameDir, "MonoBleedingEdge", "mono.dll"), "mono");
        Put(Path.Combine(c.GameDir, "winhttp.dll"), "doorstop");
        Put(Path.Combine(c.GameDir, "doorstop_config.ini"), "[General]");
        Put(Path.Combine(c.GameDir, "BepInEx", "core", "BepInEx.dll"), "core");
        Put(Path.Combine(c.GameDir, "BepInEx", "plugins", "TLDRevamp", "TLDRevamp.dll"), "revamp new");
        Put(Path.Combine(c.GameDir, "BepInEx", "patchers", "TLDRevamp", "TLDRevamp.Preloader.dll"), "preloader");
        Put(Path.Combine(c.GameDir, "BepInEx", "config", "revamp.cfg"), "copy's settings");
        Put(Path.Combine(c.GameDir, "BepInEx", "LogOutput.log"), "the copy's log");
        c.Save();
        return c;
    }

    [Fact]
    public void MirrorMatchesTheCopysBuildAndKeepsItsOwnSettings()
    {
        var c = Copy();
        var twin = SecondAccount.GameDir(c);
        SecondAccount.Mirror(c, _ => { });
        Assert.Equal("revamp new", File.ReadAllText(Path.Combine(twin, "BepInEx", "plugins", "TLDRevamp", "TLDRevamp.dll")));
        Assert.Equal("preloader", File.ReadAllText(Path.Combine(twin, "BepInEx", "patchers", "TLDRevamp", "TLDRevamp.Preloader.dll")));
        Assert.Equal("doorstop", File.ReadAllText(Path.Combine(twin, "winhttp.dll")));
        Assert.Equal("level", File.ReadAllText(Path.Combine(twin, "TheLongDrive_Data", "level0")));
        Assert.Equal("copy's settings", File.ReadAllText(Path.Combine(twin, "BepInEx", "config", "revamp.cfg")));
        Assert.Equal(Steam.AppId, File.ReadAllText(Path.Combine(twin, "steam_appid.txt")));
        Assert.False(File.Exists(Path.Combine(twin, "BepInEx", "LogOutput.log")));

        // the second game's own settings stay; an add-on the copy no longer has goes; a new build comes over
        File.WriteAllText(Path.Combine(twin, "BepInEx", "config", "revamp.cfg"), "second account's settings");
        Put(Path.Combine(twin, "BepInEx", "plugins", "TLDRevampMods", "Old.dll"), "old add-on");
        File.WriteAllText(Path.Combine(c.GameDir, "BepInEx", "plugins", "TLDRevamp", "TLDRevamp.dll"), "revamp newer");
        File.Delete(Path.Combine(c.GameDir, "doorstop_config.ini"));
        SecondAccount.Mirror(c, _ => { });
        Assert.Equal("revamp newer", File.ReadAllText(Path.Combine(twin, "BepInEx", "plugins", "TLDRevamp", "TLDRevamp.dll")));
        Assert.False(File.Exists(Path.Combine(twin, "BepInEx", "plugins", "TLDRevampMods", "Old.dll")));
        Assert.False(File.Exists(Path.Combine(twin, "doorstop_config.ini")));
        Assert.Equal("second account's settings", File.ReadAllText(Path.Combine(twin, "BepInEx", "config", "revamp.cfg")));
    }

    [Fact]
    public void TheSecondGameKeepsItsSavesApartFromTheCopys()
    {
        var c = Copy();
        // CopyIsolation keeps saves next to the game, or in its parent when that holds a copy.json: never the copy's UserData
        var parent = Directory.GetParent(SecondAccount.GameDir(c))!.FullName;
        Assert.NotEqual(Path.GetFullPath(c.Dir), Path.GetFullPath(parent));
        Assert.False(File.Exists(Path.Combine(parent, "copy.json")));
        Assert.StartsWith(Path.GetFullPath(c.Dir), Path.GetFullPath(SecondAccount.GameDir(c)));   // removed with the copy
    }
}

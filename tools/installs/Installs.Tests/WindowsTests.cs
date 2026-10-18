using Xunit;

namespace TLDInstalls.Tests;

public sealed class WindowsTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("tld-copies-windows-test").FullName;
    public void Dispose() => Directory.Delete(tmp, true);

    CopyInfo Copy(string name)
    {
        var c = new CopyInfo { Name = name, Dir = Path.Combine(tmp, name), Kind = Kind.Vanilla };
        Directory.CreateDirectory(c.GameDir);
        c.Save();
        return c;
    }

    [Fact]
    public void WindowsPlanAllowsDifferentCopiesAndSteamWithoutSwapping()
    {
        var a = Copy("a"); var b = Copy("b");
        var docs = Path.Combine(tmp, "Documents", "TheLongDrive");
        Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(docs, "world.tld"), "Steam's world");
        var first = Launcher.WindowsStartInfo(a, [Path.Combine(tmp, "Steam", "TheLongDrive.exe")]);
        var second = Launcher.WindowsStartInfo(b, [a.Exe, Path.Combine(tmp, "Steam", "TheLongDrive.exe")], ["-screen-fullscreen", "0"]);
        Assert.Equal(a.Exe, first.FileName);
        Assert.Equal(b.GameDir, second.WorkingDirectory);
        Assert.False(second.UseShellExecute);
        Assert.Equal(new[] { "-screen-fullscreen", "0" }, second.ArgumentList);
        Assert.Equal(Steam.AppId, second.Environment["SteamAppId"]);
        Assert.Equal("Steam's world", File.ReadAllText(Path.Combine(docs, "world.tld")));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(docs)!));
        Assert.Throws<UserError>(() => Launcher.WindowsStartInfo(a, [a.Exe.ToUpperInvariant(), b.Exe]));
    }

    [Theory]
    [InlineData(Kind.Vanilla)]
    [InlineData(Kind.Release)]
    [InlineData(Kind.Dev)]
    [InlineData(Kind.Test)]
    public async Task OldCopyGetsEmbeddedPluginAndMigratesOnlyOnce(Kind kind)
    {
        var c = Copy("old-copy"); c.Kind = kind;
        Directory.CreateDirectory(Path.Combine(c.SavesStore, "Saves"));
        File.WriteAllText(Path.Combine(c.SavesStore, "Saves", "world.tld"), "old world");
        File.WriteAllText(Path.Combine(c.SavesStore, "settings.tldc"), "settings");
        Directory.CreateDirectory(Path.Combine(c.GameDir, "BepInEx", "core"));
        File.WriteAllText(Path.Combine(c.GameDir, "BepInEx", "core", "BepInEx.dll"), "existing BepInEx");
        await Builder.PrepareWindows(c, _ => { });
        Assert.False(Directory.Exists(c.SavesStore));
        Assert.Equal("old world", File.ReadAllText(Path.Combine(c.UserData, "Saves", "world.tld")));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(c.UserData, "settings.tldc")));
        var dll = Path.Combine(c.GameDir, "BepInEx", "plugins", "CopyIsolation.dll");
        Assert.Equal(new byte[] { (byte)'M', (byte)'Z' }, File.ReadAllBytes(dll)[..2]);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(dll)!, "*", SearchOption.AllDirectories));
        File.WriteAllText(Path.Combine(c.UserData, "Saves", "world.tld"), "new world");
        await Builder.PrepareWindows(c, _ => { });
        Assert.Equal("new world", File.ReadAllText(Path.Combine(c.UserData, "Saves", "world.tld")));
    }

    [Fact]
    public void ConcurrentLaunchesLockOnlyTheSameCopy()
    {
        var a = Copy("a"); var b = Copy("b");
        using (Launcher.WindowsLaunchLock(a))
        {
            using var other = Launcher.WindowsLaunchLock(b);
            Assert.Throws<UserError>(() => Launcher.WindowsLaunchLock(a));
        }
        using var retry = Launcher.WindowsLaunchLock(a);
    }

    [Fact]
    public void MigrationNeverOverwritesExistingUserData()
    {
        var c = Copy("conflict");
        Directory.CreateDirectory(c.SavesStore); Directory.CreateDirectory(c.UserData);
        File.WriteAllText(Path.Combine(c.SavesStore, "world.tld"), "old");
        File.WriteAllText(Path.Combine(c.UserData, "world.tld"), "new");
        Assert.Throws<UserError>(() => Builder.InstallIsolation(c, _ => { }));
        Assert.Equal("old", File.ReadAllText(Path.Combine(c.SavesStore, "world.tld")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(c.UserData, "world.tld")));
    }
}

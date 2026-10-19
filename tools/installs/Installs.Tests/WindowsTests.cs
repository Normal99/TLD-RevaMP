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
    public async Task CleanLaunchUsesOnlyItsOwnProfileAndNeverInstallsIsolation()
    {
        var a = Copy("clean-a"); a.Kind = Kind.Clean;
        var b = Copy("clean-b"); b.Kind = Kind.Clean;
        var docs = Path.Combine(tmp, "Documents", "TheLongDrive");
        Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(docs, "world.tld"), "Steam's world");
        var keys = new[] { "USERPROFILE", "HOMEDRIVE", "HOMEPATH" };
        var before = keys.Select(Environment.GetEnvironmentVariable).ToArray();
        var first = Launcher.WindowsStartInfo(a, [Path.Combine(tmp, "Steam", "TheLongDrive.exe")]);
        var second = Launcher.WindowsStartInfo(b, [a.Exe]);
        Assert.Equal(Path.GetFullPath(a.Profile), first.Environment["USERPROFILE"]);
        Assert.Equal(Path.GetFullPath(b.Profile), second.Environment["USERPROFILE"]);
        Assert.Equal(Path.GetFullPath(a.Profile), first.Environment["HOMEDRIVE"] + first.Environment["HOMEPATH"]);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.GetPathRoot(a.Profile)!.TrimEnd('\\'), first.Environment["HOMEDRIVE"]);
            Assert.Equal(a.Profile[Path.GetPathRoot(a.Profile)!.TrimEnd('\\').Length..], first.Environment["HOMEPATH"]);
        }
        Assert.Equal(before, keys.Select(Environment.GetEnvironmentVariable).ToArray());
        Assert.True(Directory.Exists(Path.Combine(a.Profile, "Documents")));
        Assert.Equal(Path.Combine(a.Profile, "Documents", "TheLongDrive"), a.UserData);
        Assert.Equal(Steam.AppId, first.Environment["SteamAppId"]);
        Assert.False(first.UseShellExecute);
        Assert.Throws<UserError>(() => Launcher.WindowsStartInfo(a, [a.Exe.ToUpperInvariant(), b.Exe]));
        await Builder.PrepareWindows(a, _ => { });
        Builder.InstallIsolation(a, _ => { });
        Assert.False(Directory.Exists(Path.Combine(a.GameDir, "BepInEx")));
        Assert.Null(a.BepInEx);
        Assert.Equal("Steam's world", File.ReadAllText(Path.Combine(docs, "world.tld")));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(docs)!));
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
        second.Environment.TryGetValue("USERPROFILE", out var profile);
        Assert.Equal(Environment.GetEnvironmentVariable("USERPROFILE"), profile);
        Assert.Equal("Steam's world", File.ReadAllText(Path.Combine(docs, "world.tld")));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(docs)!));
        Assert.Throws<UserError>(() => Launcher.WindowsStartInfo(a, [a.Exe.ToUpperInvariant(), b.Exe]));
    }

    [Theory]
    [InlineData(Kind.Vanilla)]
    [InlineData(Kind.Release)]
    [InlineData(Kind.Dev)]
    [InlineData(Kind.Test)]
    [InlineData(Kind.Source)]
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
    public void DuplicateLaunchIsRefusedUnlessForcedWithoutStartingAnyProcess()
    {
        var c = Copy("already-open");
        var error = Assert.Throws<UserError>(() => Launcher.WindowsStartInfo(c, [c.Exe]));
        Assert.Contains("already running", error.Message);
        Assert.Equal(c.Exe, Launcher.WindowsStartInfo(c, [c.Exe], force: true).FileName);
        Assert.Contains("already running", Assert.Throws<UserError>(() => Launcher.RefuseRunning(c, true, false)).Message);
        Launcher.RefuseRunning(c, true, true);
        Launcher.RefuseRunning(c, false, false);
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

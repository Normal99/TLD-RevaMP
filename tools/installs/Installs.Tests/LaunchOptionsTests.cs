using Xunit;

namespace TLDInstalls.Tests;

/// <summary>Per-copy launch options, written like Steam's (LaunchOptions): what a line means, and what each platform's
/// start does with it.</summary>
public sealed class LaunchOptionsTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("tld-copies-options-test").FullName;
    public void Dispose() => Directory.Delete(tmp, true);

    static Dictionary<string, string> Vars(LaunchOptions o) => o.Variables.ToDictionary(kv => kv.Key, kv => kv.Value);

    [Fact]
    public void EmptyLineIsNoOptions()
    {
        Assert.True(LaunchOptions.Parse(null).IsEmpty);
        Assert.True(LaunchOptions.Parse("   ").IsEmpty);
    }

    [Fact]
    public void WithoutCommandTheLineIsTheGamesArguments()
    {
        var o = LaunchOptions.Parse("-screen-fullscreen 0  -screen-width 1280");
        Assert.Equal(["-screen-fullscreen", "0", "-screen-width", "1280"], o.Args);
        Assert.Empty(o.Wrapper);
        Assert.Empty(o.Variables);
    }

    [Fact]
    public void SteamsFormSplitsVariablesWrapperAndArguments()
    {
        var o = LaunchOptions.Parse("PROTON_LOG=1 WINEDLLOVERRIDES=\"dxgi=n,b\" gamemoderun mangohud %command% -screen-fullscreen 0");
        Assert.Equal(new Dictionary<string, string> { ["PROTON_LOG"] = "1", ["WINEDLLOVERRIDES"] = "dxgi=n,b" }, Vars(o));
        Assert.Equal(["gamemoderun", "mangohud"], o.Wrapper);
        Assert.Equal(["-screen-fullscreen", "0"], o.Args);
    }

    [Fact]
    public void VariablesWorkWithoutCommandToo()
    {
        var o = LaunchOptions.Parse("DXVK_HUD=fps -popupwindow");
        Assert.Equal("fps", Vars(o)["DXVK_HUD"]);
        Assert.Equal(["-popupwindow"], o.Args);
    }

    [Fact]
    public void AssignmentsAfterTheWrapperBelongToTheWrapper()
    {
        var o = LaunchOptions.Parse("env FOO=1 %command%");
        Assert.Empty(o.Variables);
        Assert.Equal(["env", "FOO=1"], o.Wrapper);
    }

    [Fact]
    public void QuotesKeepSpacesAndWindowsPathsKeepBackslashes()
    {
        var o = LaunchOptions.Parse(@"MANGOHUD_CONFIG='fps,gpu_temp, cpu_temp' %command% -logFile ""C:\TLD logs\player.log"" C:\x\y a\ b");
        Assert.Equal("fps,gpu_temp, cpu_temp", Vars(o)["MANGOHUD_CONFIG"]);
        Assert.Equal([@"-logFile", @"C:\TLD logs\player.log", @"C:\x\y", "a b"], o.Args);
    }

    [Fact]
    public void QuotedCommandIsAnArgumentNotTheMarker()
    {
        var o = LaunchOptions.Parse("'%command%' -x");
        Assert.Equal(["%command%", "-x"], o.Args);
    }

    [Theory]
    [InlineData("gamemoderun %command% %command%", "more than once")]
    [InlineData("-name 'unclosed", "isn't closed")]
    [InlineData("STEAM_COMPAT_DATA_PATH=/elsewhere %command%", "STEAM_COMPAT_DATA_PATH")]
    [InlineData("USERPROFILE=C:\\other", "USERPROFILE")]
    public void MistakesAreRefusedWithTheReason(string line, string why)
    {
        Assert.Contains(why, Assert.Throws<UserError>(() => LaunchOptions.Parse(line)).Message);
    }

    [Theory]
    [InlineData("gamemoderun", "%command%", "-screen-fullscreen", "0")]
    [InlineData("A=two words", "%command%", "it's", "say \"hi\"", @"C:\dir\")]
    public void JoinParsesBackToTheSameWords(params string[] words)
    {
        var o = LaunchOptions.Parse(LaunchOptions.Join(words));
        var back = o.Variables.Select(kv => kv.Key + "=" + kv.Value).Concat(o.Wrapper).Append("%command%").Concat(o.Args);
        Assert.Equal(words, back);
    }

    [Fact]
    public void ALaterVariableReplacesAnEarlierOne()
    {
        Assert.Equal("2", Vars(LaunchOptions.Parse("X=1 X=2 %command%"))["X"]);
    }

    [Theory]
    [InlineData("dxgi=n,b", "dxgi=n,b;winhttp=n,b")]
    [InlineData("", "winhttp=n,b")]
    [InlineData("winhttp=n,b;dxgi=n", "winhttp=n,b;dxgi=n")]
    [InlineData("d3d11,winhttp=b", "d3d11,winhttp=b")]
    public void AModdedCopyKeepsBepInExsDoorstop(string mine, string expected)
    {
        var env = new Dictionary<string, string?> { ["WINEDLLOVERRIDES"] = "winhttp=n,b" };
        LaunchOptions.Parse($"WINEDLLOVERRIDES=\"{mine}\" %command%").ApplyEnv(env, keepDoorstop: true);
        Assert.Equal(expected, env["WINEDLLOVERRIDES"]);
        var plain = new Dictionary<string, string?>();
        LaunchOptions.Parse($"WINEDLLOVERRIDES=\"{mine}\" %command%").ApplyEnv(plain, keepDoorstop: false);
        Assert.Equal(mine, plain["WINEDLLOVERRIDES"]);
    }

    [Fact]
    public void SavedWithTheCopy()
    {
        var c = new CopyInfo { Name = "a", Dir = Path.Combine(tmp, "a"), Kind = Kind.Release, LaunchOptions = "gamemoderun %command% -x" };
        Directory.CreateDirectory(c.Dir);
        c.Save();
        Assert.Equal("gamemoderun %command% -x", CopyInfo.Load(c.Dir)!.LaunchOptions);
        c.LaunchOptions = null; c.Save();
        Assert.DoesNotContain("LaunchOptions", File.ReadAllText(Path.Combine(c.Dir, "copy.json")));
    }

    [Fact]
    public void WindowsStartGetsArgumentsAndVariablesButNoWrapper()
    {
        var c = new CopyInfo { Name = "w", Dir = Path.Combine(tmp, "w"), Kind = Kind.Clean };
        Directory.CreateDirectory(c.GameDir);
        var psi = Launcher.WindowsStartInfo(c, [], ["-extra"], opts: LaunchOptions.Parse("DXVK_HUD=1 -screen-fullscreen 0"));
        Assert.Equal(["-screen-fullscreen", "0", "-extra"], psi.ArgumentList);
        Assert.Equal("1", psi.Environment["DXVK_HUD"]);
        Assert.Equal(Path.GetFullPath(c.Profile), psi.Environment["USERPROFILE"]);   // the copy's own save folder still wins
        Assert.Contains("Linux only", Assert.Throws<UserError>(() => Launcher.WindowsStartInfo(c, [], opts: LaunchOptions.Parse("gamemoderun %command%"))).Message);
    }

    [Fact]
    public void LinuxStartWrapsTheWholeChainAndKeepsThePrefix()
    {
        var steam = Path.Combine(tmp, "Steam");
        var protonDir = Path.Combine(steam, "steamapps", "common", "Proton - Experimental");
        Directory.CreateDirectory(protonDir);
        File.WriteAllText(Path.Combine(protonDir, "proton"), "");
        var g = new SteamGame(steam, steam, Path.Combine(steam, "steamapps", "common", "The Long Drive"), "1");
        var c = new CopyInfo { Name = "l", Dir = Path.Combine(tmp, "l"), Kind = Kind.Release, Compat = Path.Combine(tmp, "l", "compat") };
        var psi = Launcher.LinuxStartInfo(g, c, ["-extra"], LaunchOptions.Parse("PROTON_LOG=1 WINEDLLOVERRIDES=dxgi=n gamemoderun mangohud %command% -screen-fullscreen 0"));
        var argv = psi.ArgumentList.ToList();
        int wrapper = argv.IndexOf("gamemoderun"), entry = argv.FindIndex(a => a.EndsWith("_v2-entry-point")), exe = argv.IndexOf(c.Exe);
        Assert.True(argv.IndexOf("sh") < wrapper && wrapper + 1 == argv.IndexOf("mangohud") && wrapper + 2 == entry && entry < exe, string.Join(" ", argv));
        Assert.Equal(["-screen-fullscreen", "0", "-extra"], argv[(exe + 1)..]);
        Assert.Equal("1", psi.Environment["PROTON_LOG"]);
        Assert.Equal("dxgi=n;winhttp=n,b", psi.Environment["WINEDLLOVERRIDES"]);
        Assert.Equal(c.Compat, psi.Environment["STEAM_COMPAT_DATA_PATH"]);
    }

    [Fact]
    public void DescribeSaysWhatTheLineDoes()
    {
        Assert.Equal("no launch options", LaunchOptions.Parse("").Describe());
        var d = LaunchOptions.Parse("PROTON_LOG=1 gamemoderun %command% -x").Describe();
        Assert.Contains("variables PROTON_LOG=1", d);
        Assert.Contains("game arguments -x", d);
        Assert.Contains("wrapped in gamemoderun", d);
    }
}

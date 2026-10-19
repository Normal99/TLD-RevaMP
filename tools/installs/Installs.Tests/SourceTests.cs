using System.ComponentModel;
using Xunit;

[assembly: System.Reflection.AssemblyMetadata("SourceCommit", "abcdefabcdefabcdefabcdefabcdefabcdefabcd")]
[assembly: System.Reflection.AssemblyMetadata("SourceDirty", "true")]

namespace TLDInstalls.Tests;

public sealed class SourceTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("tld-source-test").FullName;
    readonly List<(string Cwd, string Exe, string[] Args)> calls = new();
    string head = new('a', 40);
    bool failBuild;
    string? missing;
    string sdks = "9.0.316 [sdk]";
    public void Dispose() => Directory.Delete(tmp, true);
    string Repo => Path.Combine(tmp, "source", "TLD-RevaMP");
    string Managed => Path.Combine(tmp, "game", "Managed");
    string Core => Path.Combine(tmp, "bep", "core");
    string Write(string path, string text = "reference")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path;
    }
    CopyInfo Copy()
    {
        foreach (var n in SourceBuild.ManagedLibs) Write(Path.Combine(Managed, n + ".dll"));
        foreach (var n in new[] { "0Harmony", "BepInEx" }) Write(Path.Combine(Core, n + ".dll"));
        var c = new CopyInfo { Name = "source-test", Kind = Kind.Source, Dir = Path.Combine(tmp, "copy") };
        Directory.CreateDirectory(c.Dir); c.Save(); return c;
    }
    Files.Result Run(string cwd, string exe, string[] args)
    {
        calls.Add((cwd, exe, args));
        if (missing == exe) throw new Win32Exception("not installed");
        string output = "";
        if (args[0] == "clone")
        {
            Directory.CreateDirectory(Path.Combine(Repo, ".git"));
            Write(Path.Combine(Repo, "src", "TLDRevamp", "Plugin.cs"), "public const string BaseVersion = \"0.69.1\";");
            Write(Path.Combine(Repo, "packaging", "approved-mods.txt")); Write(Path.Combine(Repo, "COPYING"));
        }
        if (args[0] == "remote") output = SourceBuild.Remote;
        if (args[0] == "rev-parse") output = head;
        if (args[0] == "rev-list") output = "3";
        if (args[0] == "show") output = args.Contains("--format=%s") ? "Fix the bus" : "2026-10-19T12:34:00+02:00";
        if (args[0] == "--list-sdks") output = sdks;
        if (args[0] == "build")
        {
            if (failBuild) return new(1, "", "build failed");
            var project = Path.GetFileNameWithoutExtension(args[1]);
            Write(Path.Combine(Repo, "src", project, "bin", "Release", "netstandard2.1", project == "TLDLoaderShim" ? "TLDLoader.dll" : project + ".dll"), head);
        }
        return new(0, output, "");
    }
    SourceBuild Builder() => new(_ => { }, tmp, Run);

    [Fact]
    public void CloneFetchCheckoutBuildAndInstallUseOnlyTheCache()
    {
        var c = Copy(); var b = Builder();
        Assert.True(b.Build(c, Managed, Core));
        Assert.True(c.UpdateBeforeLaunch);
        Assert.Equal(new[] { "--version", "clone", "remote", "fetch", "check-ref-format", "rev-parse", "--list-sdks", "checkout", "status", "build", "build", "build", "show", "show" }, calls.Select(x => x.Args[0]));
        Assert.Equal(new[] { "checkout", "--detach", "--force", head }, calls.Single(x => x.Args[0] == "checkout").Args);
        Assert.Equal("refs/remotes/origin/dev^{commit}", calls.Single(x => x.Args[0] == "rev-parse").Args[^1]);
        Assert.All(calls.Where(x => x.Exe == "dotnet" && x.Args[0] == "build"), x =>
        {
            Assert.Equal(Repo, x.Cwd); Assert.Contains("Release", x.Args); Assert.Contains("-p:RevampVersion=0.69.1-dev.aaaaaaa", x.Args);
        });
        Assert.Equal("0.69.1-dev.aaaaaaa", c.RevampVersion);
        Assert.Equal(head, c.Commit!.Sha); Assert.Equal("Fix the bus", c.Commit.Title); Assert.False(c.Commit.Dirty);
        Assert.Equal(SourceBuild.DefaultRef, c.SourceRef); Assert.False(c.Bridge);
        var plugin = Path.Combine(c.GameDir, "BepInEx", "plugins", "TLDRevamp");
        Assert.Equal(5, Directory.GetFiles(plugin).Length);
        Assert.Contains("Bridge = false", File.ReadAllText(Path.Combine(c.GameDir, "BepInEx", "config", "tldrevamp.core.cfg")));
        Assert.Equal(c.Commit, CopyInfo.Load(c.Dir)!.Commit);
    }

    [Fact]
    public void FetchCountsNewCommitsSkipsUnchangedBuildsAndSwitchesRefs()
    {
        var c = Copy(); var b = Builder(); b.Build(c, Managed, Core); calls.Clear();
        Assert.False(b.Build(c, Managed, Core));
        Assert.DoesNotContain(calls, x => x.Args[0] is "build" or "checkout");
        var installed = head; head = new('b', 40);
        Assert.Equal(3, b.Newer(c));
        Assert.Equal(installed + ".." + head, calls.Single(x => x.Args[0] == "rev-list").Args[^1]);
        calls.Clear();
        Assert.True(b.Build(c, Managed, Core, "feature/bus"));
        Assert.Equal("refs/remotes/origin/feature/bus^{commit}", calls.Single(x => x.Args[0] == "rev-parse").Args[^1]);
        Assert.Equal("feature/bus", c.SourceRef); Assert.Equal(head, c.Commit!.Sha);
        calls.Clear(); Assert.False(b.Build(c, Managed, Core, head));
        Assert.Equal(head + "^{commit}", calls.Single(x => x.Args[0] == "rev-parse").Args[^1]);
        calls.Clear(); Assert.True(b.Build(c, Managed, Core, force: true));
        Assert.Equal(3, calls.Count(x => x.Args[0] == "build"));
        Assert.Equal(1, calls.Count(x => x.Args[0] == "fetch"));
        Assert.DoesNotContain(calls, x => x.Args[0] == "clone");
    }

    [Fact]
    public void FailedBuildKeepsInstalledCommitAndDll()
    {
        var c = Copy(); var b = Builder(); b.Build(c, Managed, Core);
        var before = c.Commit; head = new('b', 40); failBuild = true;
        Assert.Throws<UserError>(() => b.Build(c, Managed, Core, "other"));
        Assert.Equal(before, CopyInfo.Load(c.Dir)!.Commit);
        Assert.Equal(before!.Sha, File.ReadAllText(Path.Combine(c.GameDir, "BepInEx", "plugins", "TLDRevamp", "TLDRevamp.dll")));
    }

    [Theory]
    [InlineData("git")]
    [InlineData("dotnet")]
    [InlineData("runtime-only")]
    public void MissingPrerequisitesExplainExactlyWhatToInstall(string tool)
    {
        var c = Copy(); missing = tool; if (tool == "runtime-only") sdks = "";
        var error = Assert.Throws<UserError>(() => Builder().Build(c, Managed, Core));
        Assert.Equal(tool == "git" ? SourceBuild.GitHelp : SourceBuild.SdkHelp, error.Message);
    }

    [Fact]
    public void LibListMatchesLibsShellAndCopiesThePinnedCore()
    {
        Copy(); SourceBuild.PopulateLib(Repo, Managed, Core);
        Assert.Equal(23, Directory.GetFiles(Path.Combine(Repo, "lib")).Length);
        var script = File.ReadAllText(Path.Combine(Env.RepoDir!, "tools", "libs.sh"));
        foreach (var name in SourceBuild.ManagedLibs) Assert.Contains(name, script);
        Assert.Equal("reference", File.ReadAllText(Path.Combine(Repo, "lib", "0Harmony.dll")));
        File.Delete(Path.Combine(Managed, "UnityEngine.dll"));
        Assert.Contains("UnityEngine.dll", Assert.Throws<UserError>(() => SourceBuild.PopulateLib(Repo, Managed, Core)).Message);
    }

    [Theory]
    [InlineData(Kind.Source)]
    [InlineData(Kind.Release)]
    [InlineData(Kind.Dev)]
    [InlineData(Kind.Test)]
    public void BugReportIncludesOnlyTheExactCommitAndExistingFields(Kind kind)
    {
        var c = Copy(); c.Kind = kind; c.Commit = new(head, "Fix the bus", "2026-10-19T12:34:00+02:00", true);
        c.SourceRef = "origin/dev"; c.GameSource = Path.Combine(tmp, "private");
        var body = BugReports.Body(c, "Doors stay shut");
        Assert.Contains(head, body); Assert.Contains("aaaaaaa", body); Assert.Contains("Fix the bus", body);
        Assert.Contains(c.Commit.Date, body); Assert.Contains("**Ref:** origin/dev", body); Assert.Contains("**Dirty:** true", body);
        Assert.Contains("https://github.com/Normal99/TLD-RevaMP/commit/" + head, body);
        Assert.DoesNotContain(tmp, body); Assert.DoesNotContain("LogOutput", body);
    }

    [Fact]
    public void ReadsTheModMetadataWithoutLoadingTheGame()
    {
        var (sha, dirty) = SourceBuild.Identity(typeof(SourceTests).Assembly.Location);
        Assert.Equal("abcdefabcdefabcdefabcdefabcdefabcdefabcd", sha);
        Assert.True(dirty);
    }

    [Fact]
    public void OlderRefUsesGeneratedFilesWithoutEditingItsPlugin()
    {
        var c = Copy(); var b = Builder(); b.Build(c, Managed, Core);
        var file = Path.Combine(Repo, "src", "TLDRevamp", "Plugin.cs");
        var original = "[BepInPlugin(Guid, \"TLD Revamp\", Version)]\npublic const string Version = \"0.69.1\";";
        File.WriteAllText(file, original); calls.Clear();
        b.Build(c, Managed, Core, force: true);
        Assert.Equal(original, File.ReadAllText(file));
        var generated = File.ReadAllText(Path.Combine(Repo, "src", "TLDRevamp", "obj", "SourcePlugin.cs"));
        Assert.Contains("Version = \"0.69.1-dev.aaaaaaa\"", generated);
        Assert.Contains("\"TLD Revamp\", \"0.69.1\"", generated);
        Assert.All(calls.Where(x => x.Args[0] == "build"), x => Assert.Contains(x.Args, a => a.StartsWith("-p:CustomAfterMicrosoftCommonTargets=")));
    }

    [Theory]
    [InlineData("--upload-pack=other")]
    [InlineData("other..dev")]
    [InlineData("../checkout")]
    public void RefCannotBecomeAnOptionOrPath(string reference)
    {
        var c = Copy(); Assert.Throws<UserError>(() => Builder().Build(c, Managed, Core, reference));
        Assert.DoesNotContain(calls, x => x.Args[0] is "checkout" or "build");
    }
}

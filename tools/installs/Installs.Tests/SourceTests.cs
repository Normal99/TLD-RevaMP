using System.ComponentModel;
using System.Reflection;
using System.Reflection.Emit;
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
    int checkoutFailures;
    bool failFresh, failReset, rejectDisable, legacy, buildDirty, checkoutDirty;
    string? buildCommit, checkoutHead;
    Action? beforeCopy;
    readonly List<string> logs = new();
    public void Dispose() => Directory.Delete(tmp, true);
    string Repo => Path.Combine(tmp, "source", "TLD-RevaMP");
    string Managed => Path.Combine(tmp, "game", "Managed");
    string Core => Path.Combine(tmp, "bep", "core");
    string BuiltDll => Path.Combine(Repo, "src", "TLDRevamp", "bin", "Release", "netstandard2.1", "TLDRevamp.dll");
    static string InstalledDll(CopyInfo c) => Path.Combine(c.GameDir, "BepInEx", "plugins", "TLDRevamp", "TLDRevamp.dll");

    static void BuildDll(string path, string? commit, string? dirty = "false")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var assembly = new PersistedAssemblyBuilder(new AssemblyName("SourceFixture"), typeof(object).Assembly);
        var constructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        if (commit != null) assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, ["SourceCommit", commit]));
        if (dirty != null) assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, ["SourceDirty", dirty]));
        assembly.DefineDynamicModule("SourceFixture").DefineType("Fixture").CreateType();
        assembly.Save(path);
    }
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
            var repo = args[^1];
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            Write(Path.Combine(repo, "src", "TLDRevamp", "Plugin.cs"), "public const string BaseVersion = \"0.69.1\";");
            Write(Path.Combine(repo, "packaging", "approved-mods.txt")); Write(Path.Combine(repo, "COPYING"));
        }
        if (args[0] == "checkout" && (checkoutFailures-- > 0 || failFresh && cwd != Repo)) return new(1, "", "unable to create file: File exists");
        if (args[0] == "reset" && failReset) return new(1, "", "reset failed");
        if (args[0] == "remote") output = SourceBuild.Remote;
        if (args[0] == "rev-parse") output = args[^1] == "HEAD" ? checkoutHead ?? head : head;
        if (args[0] == "rev-list") output = "3";
        if (args[0] == "status" && checkoutDirty) output = " M src/TLDRevamp/Plugin.cs";
        if (args[0] == "show")
        {
            output = args.Contains("--format=%s") ? "Fix the bus" : "2026-10-19T12:34:00+02:00";
            beforeCopy?.Invoke();
        }
        if (args[0] == "--list-sdks") output = sdks;
        if (args[0] == "build")
        {
            if (rejectDisable && args.Contains("--disable-build-servers")) return new(1, "MSBUILD : error MSB1001: Unknown switch.\nSwitch: --disable-build-servers", "");
            if (failBuild) return new(1, "", "build failed");
            var project = Path.GetFileNameWithoutExtension(args[1]);
            var dll = Path.Combine(Repo, "src", project, "bin", "Release", "netstandard2.1", project == "TLDLoaderShim" ? "TLDLoader.dll" : project + ".dll");
            BuildDll(dll, legacy ? null : buildCommit ?? head, legacy ? null : buildDirty ? "true" : "false");
        }
        return new(0, output, "");
    }
    SourceBuild Builder() => new(logs.Add, tmp, Run);

    [Fact]
    public void CloneFetchCheckoutBuildAndInstallUseOnlyTheCache()
    {
        var c = Copy(); var b = Builder();
        Assert.True(b.Build(c, Managed, Core));
        Assert.True(c.UpdateBeforeLaunch);
        Assert.Equal(new[] { "--version", "clone", "remote", "fetch", "check-ref-format", "rev-parse", "--list-sdks", "checkout", "rev-parse", "status", "build", "build", "build", "show", "show" }, calls.Select(x => x.Args[0]));
        Assert.Equal(new[] { "checkout", "--detach", "--force", head }, calls.Single(x => x.Args[0] == "checkout").Args);
        Assert.Equal("refs/remotes/origin/dev^{commit}", calls.First(x => x.Args[0] == "rev-parse").Args[^1]);
        Assert.All(calls.Where(x => x.Exe == "dotnet" && x.Args[0] == "build"), x =>
        {
            Assert.Equal(Repo, x.Cwd); Assert.Contains("Release", x.Args); Assert.Contains("-p:RevampVersion=0.69.1-dev.aaaaaaa", x.Args);
            Assert.Contains("--disable-build-servers", x.Args); Assert.Contains("-p:UseSharedCompilation=false", x.Args);
            Assert.Contains("-nodeReuse:false", x.Args); Assert.Contains("-t:Rebuild", x.Args);
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
        Assert.Equal("refs/remotes/origin/feature/bus^{commit}", calls.First(x => x.Args[0] == "rev-parse").Args[^1]);
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
        Assert.Equal(before!.Sha, SourceBuild.Identity(InstalledDll(c)).Commit);
    }

    [Fact]
    public void FailedCheckoutResetsCleansAndRetriesOnce()
    {
        var c = Copy(); checkoutFailures = 1;
        Assert.True(Builder().Build(c, Managed, Core));
        var recovery = calls.SkipWhile(x => x.Args[0] != "checkout").Take(4).ToArray();
        Assert.Equal(new[] { "checkout", "reset", "clean", "checkout" }, recovery.Select(x => x.Args[0]));
        Assert.Equal(new[] { "reset", "--hard" }, recovery[1].Args);
        Assert.Equal(new[] { "clean", "-ffdx", "-e", "lib/" }, recovery[2].Args);
        Assert.Single(calls, x => x.Args[0] == "clone");
        Assert.Contains(logs, l => l.Contains("reset --hard"));
        Assert.Contains(logs, l => l.Contains("clean -ffdx -e lib/"));
        Assert.Contains(logs, l => l.Contains("Retrying"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRecoverySwapsFreshCloneAndKeepsOldCache(bool resetFails)
    {
        var c = Copy(); var b = Builder(); b.Build(c, Managed, Core); calls.Clear();
        Write(Path.Combine(Repo, "keep-me.txt"), "old cache");
        checkoutFailures = 2; failReset = resetFails;
        // When reset itself fails there is no checkout retry in the damaged folder.
        if (resetFails) checkoutFailures = 1;
        Assert.True(b.Build(c, Managed, Core, force: true));
        var fresh = calls.Single(x => x.Args[0] == "clone").Args[^1];
        Assert.Equal(Path.GetDirectoryName(Repo), Path.GetDirectoryName(fresh));
        Assert.False(Directory.Exists(fresh));
        var old = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(Repo)!, "TLD-RevaMP.old-*"));
        Assert.Equal("old cache", File.ReadAllText(Path.Combine(old, "keep-me.txt")));
        Assert.True(File.Exists(Path.Combine(Repo, "lib", "BepInEx.dll")));
        Assert.Contains(calls, x => x.Cwd == fresh && x.Args[0] == "checkout");
        Assert.All(calls.Where(x => x.Args[0] == "build"), x => Assert.Equal(Repo, x.Cwd));
        Assert.Contains(logs, l => l.Contains("Cloning fresh"));
        Assert.Contains(logs, l => l.Contains("Keeping the old"));
        Assert.Contains(logs, l => l.Contains("Swapping fresh"));
    }

    [Fact]
    public void FailedFreshCheckoutKeepsOriginalCacheAndCopyRecord()
    {
        var c = Copy(); var b = Builder(); b.Build(c, Managed, Core); calls.Clear();
        var json = File.ReadAllText(Path.Combine(c.Dir, "copy.json"));
        Write(Path.Combine(Repo, "keep-me.txt")); checkoutFailures = 2; failFresh = true;
        Assert.Throws<UserError>(() => b.Build(c, Managed, Core, force: true));
        Assert.True(File.Exists(Path.Combine(Repo, "keep-me.txt")));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(Repo)!, "TLD-RevaMP.old-*"));
        Assert.DoesNotContain(calls, x => x.Args[0] == "build");
        Assert.Equal(json, File.ReadAllText(Path.Combine(c.Dir, "copy.json")));
    }

    [Fact]
    public void WrongHeadNeverBuildsEvenWhenGitCheckoutReportsSuccess()
    {
        var c = Copy(); checkoutHead = new('b', 40);
        Assert.Contains("HEAD differs", Assert.Throws<UserError>(() => Builder().Build(c, Managed, Core)).Message);
        Assert.DoesNotContain(calls, x => x.Args[0] == "build");
    }

    [Fact]
    public void DirtyCheckoutNeverBuildsEvenWhenGitCheckoutReportsSuccess()
    {
        var c = Copy(); checkoutDirty = true;
        Assert.Contains("uncommitted src changes", Assert.Throws<UserError>(() => Builder().Build(c, Managed, Core)).Message);
        Assert.DoesNotContain(calls, x => x.Args[0] == "build");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrongOrDirtyBuiltCommitCannotReplaceInstallationOrRecord(bool dirty)
    {
        var c = Copy(); var b = Builder(); b.Build(c, Managed, Core);
        var json = File.ReadAllText(Path.Combine(c.Dir, "copy.json"));
        var hash = Files.Sha256(InstalledDll(c));
        head = new('b', 40); buildDirty = dirty; buildCommit = dirty ? head : new('c', 40);
        var error = Assert.Throws<UserError>(() => b.Build(c, Managed, Core));
        Assert.Contains("source identity mismatch", error.Message);
        Assert.Equal(json, File.ReadAllText(Path.Combine(c.Dir, "copy.json")));
        Assert.Equal(hash, Files.Sha256(InstalledDll(c)));
        Assert.Equal(new string('a', 40), c.Commit!.Sha);
    }

    [Fact]
    public void InstalledDllIsVerifiedAgainBeforeSaving()
    {
        var c = Copy(); var b = Builder(); b.Build(c, Managed, Core);
        var json = File.ReadAllText(Path.Combine(c.Dir, "copy.json"));
        head = new('b', 40);
        beforeCopy = () => BuildDll(BuiltDll, new string('c', 40));
        Assert.Contains("source identity mismatch", Assert.Throws<UserError>(() => b.Build(c, Managed, Core)).Message);
        Assert.Equal(json, File.ReadAllText(Path.Combine(c.Dir, "copy.json")));
        Assert.Equal(new string('a', 40), c.Commit!.Sha);
    }

    [Fact]
    public void LegacyDllUsesFreshBuildHashAndRejectsChangedInstalledBytes()
    {
        var c = Copy(); legacy = true; var b = Builder(); b.Build(c, Managed, Core);
        Assert.Null(SourceBuild.Identity(InstalledDll(c)).Commit);
        Assert.Equal(Files.Sha256(BuiltDll), c.RevampSha256);
        calls.Clear(); Assert.True(b.Build(c, Managed, Core));
        Assert.Equal(3, calls.Count(x => x.Args[0] == "build"));
        BuildDll(InstalledDll(c), null, null);
        Assert.True(b.Build(c, Managed, Core));
        var json = File.ReadAllText(Path.Combine(c.Dir, "copy.json"));
        beforeCopy = () => BuildDll(BuiltDll, null, null);
        Assert.Contains("differs from the freshly built DLL", Assert.Throws<UserError>(() => b.Build(c, Managed, Core, force: true)).Message);
        Assert.Equal(json, File.ReadAllText(Path.Combine(c.Dir, "copy.json")));
    }

    [Fact]
    public void OldInstalledCommitCannotSkipBuildJustBecauseItsLabelMatches()
    {
        var c = Copy(); var b = Builder(); b.Build(c, Managed, Core); calls.Clear();
        BuildDll(InstalledDll(c), new string('b', 40));
        Assert.True(b.Build(c, Managed, Core));
        Assert.Equal(head, SourceBuild.Identity(InstalledDll(c)).Commit);
        Assert.Equal(3, calls.Count(x => x.Args[0] == "build"));
    }

    [Fact]
    public void UnreadableBuiltDllIsAClearErrorWithoutChangingTheRecord()
    {
        var c = Copy(); var json = File.ReadAllText(Path.Combine(c.Dir, "copy.json"));
        var dll = Write(Path.Combine(tmp, "invalid.dll"));
        Assert.Contains("couldn't verify source DLL", Assert.Throws<UserError>(() => SourceBuild.Verify(dll, head)).Message);
        Assert.Equal(json, File.ReadAllText(Path.Combine(c.Dir, "copy.json")));
    }

    [Fact]
    public void OldSdkFallsBackOnlyToArgumentsThatDisableServers()
    {
        var c = Copy(); rejectDisable = true;
        Assert.True(Builder().Build(c, Managed, Core));
        var builds = calls.Where(x => x.Args[0] == "build").ToArray();
        Assert.Equal(4, builds.Length);
        Assert.Contains("--disable-build-servers", builds[0].Args);
        Assert.All(builds.Skip(1), x => Assert.DoesNotContain("--disable-build-servers", x.Args));
        Assert.All(builds, x =>
        {
            Assert.Contains("-p:UseSharedCompilation=false", x.Args);
            Assert.Contains("-nodeReuse:false", x.Args);
        });
        Assert.Contains(logs, l => l.Contains("SDK rejects"));
    }

    [Theory]
    [InlineData(null, "false")]
    [InlineData("invalid", "false")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null)]
    public void PartialOrMalformedMetadataCannotUseLegacyFallback(string? commit, string? dirty)
    {
        var dll = Path.Combine(tmp, "partial.dll"); BuildDll(dll, commit, dirty);
        Assert.Contains("identity mismatch", Assert.Throws<UserError>(() => SourceBuild.Verify(dll, head)).Message);
    }

    [Fact]
    public void CardAndListDescriptionReadInstalledCommitAndFlagWrongLabels()
    {
        var c = Copy(); Builder().Build(c, Managed, Core);
        Assert.Contains(head, c.InstalledSourceLabel);
        Assert.DoesNotContain("label differs", BuilderDescribe(c));
        var actual = new string('b', 40); BuildDll(InstalledDll(c), actual);
        Assert.Equal((actual, false, true), c.InstalledIdentity());
        Assert.Contains(actual, c.InstalledSourceLabel);
        Assert.Contains("label differs", BuilderDescribe(c));
        Assert.Equal(head, CopyInfo.Load(c.Dir)!.Commit!.Sha);
        c.Commit = new(actual, "", ""); c.RevampSource = actual;
        Assert.Contains("label differs", c.InstalledSourceLabel);
        c.RevampVersion = "0.69.1-dev.bbbbbbb";
        Assert.DoesNotContain("label differs", c.InstalledSourceLabel);
        File.Delete(InstalledDll(c));
        Assert.Equal("installed commit unknown", c.InstalledSourceLabel);
    }

    static string BuilderDescribe(CopyInfo c) => TLDInstalls.Builder.Describe(c);

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

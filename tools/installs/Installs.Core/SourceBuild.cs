using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TLDInstalls;

public sealed record CommitInfo(string Sha, string Title, string Date, bool Dirty = false)
{
    public string Short => Sha[..7];
}

/// <summary>The app's own clone. Every fetch, checkout and build holds its cache lock, including across processes.</summary>
public sealed class SourceBuild
{
    public const string Remote = "https://github.com/Normal99/TLD-RevaMP";
    public const string DefaultRef = "origin/dev";
    public const string GitHelp = "Source builds need Git: install Git from https://git-scm.com/downloads and restart TLD Copies.";
    public const string SdkHelp = "Source builds need the .NET SDK: install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0 (the SDK, not just the runtime) and restart TLD Copies.";
    readonly string cache;
    readonly Action<string> log;
    readonly Func<string, string, string[], Files.Result> run;
    public string Repo => Path.Combine(cache, "source", "TLD-RevaMP");

    public SourceBuild(Action<string> log, string? cache = null, Func<string, string, string[], Files.Result>? run = null)
    {
        this.cache = cache ?? Env.CacheDir;
        this.log = log;
        this.run = run ?? Run;
    }

    Files.Result Run(string cwd, string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        var output = new List<string>(); var error = new List<string>();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (output) output.Add(e.Data); log("  " + e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (error) error.Add(e.Data); log("  " + e.Data); } };
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine(); p.WaitForExit();
        return new(p.ExitCode, string.Join('\n', output), string.Join('\n', error));
    }

    string Exec(string cwd, string exe, params string[] args)
    {
        Files.Result r;
        try { r = run(cwd, exe, args); }
        catch (System.ComponentModel.Win32Exception) { throw new UserError(exe == "git" ? GitHelp : SdkHelp); }
        if (r.Code != 0) throw new UserError($"{exe} {string.Join(' ', args)} failed (exit {r.Code}; see the build log)");
        return r.Out.Trim();
    }

    FileStream Lock()
    {
        var dir = Path.Combine(cache, "source");
        Directory.CreateDirectory(dir);
        var until = DateTime.UtcNow.AddMinutes(5); bool waiting = false;
        while (true)
        {
            try { return new FileStream(Path.Combine(dir, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                if (DateTime.UtcNow >= until) throw new UserError("another source build or fetch is still using the cache: wait for it to finish");
                if (!waiting) { waiting = true; log("Waiting for the source cache's current build or fetch"); }
                Thread.Sleep(100);
            }
        }
    }

    void Fetch()
    {
        Exec(cache, "git", "--version");
        if (!Directory.Exists(Path.Combine(Repo, ".git")))
        {
            if (Directory.Exists(Repo)) throw new UserError("the source cache contains an incomplete clone: remove its TLD-RevaMP folder and retry");
            Exec(cache, "git", "clone", "--no-checkout", "--origin", "origin", Remote, Repo);
        }
        if (Exec(Repo, "git", "remote", "get-url", "origin").TrimEnd('/') != Remote)
            throw new UserError("the source cache has another origin: not used");
        Exec(Repo, "git", "fetch", "--prune", "origin");
    }

    string Resolve(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith('-') || reference.Contains("..") || reference.Any(char.IsControl))
            throw new UserError("source ref must be a branch name or commit SHA");
        string target;
        if (Regex.IsMatch(reference, @"^[a-fA-F0-9]{7,40}$")) target = reference;
        else
        {
            var branch = reference.StartsWith("origin/") ? reference[7..] : reference;
            Exec(Repo, "git", "check-ref-format", "refs/heads/" + branch);
            target = "refs/remotes/origin/" + branch;
        }
        var sha = Exec(Repo, "git", "rev-parse", "--verify", "--end-of-options", target + "^{commit}");
        if (!Regex.IsMatch(sha, @"^[a-f0-9]{40}$")) throw new UserError("git did not resolve the ref to a full commit SHA");
        return sha;
    }

    CommitInfo ReadCommit(string sha) => new(sha,
        Exec(Repo, "git", "show", "-s", "--format=%s", sha),
        Exec(Repo, "git", "show", "-s", "--format=%cI", sha));

    public int Newer(CopyInfo c)
    {
        using var held = Lock();
        Fetch();
        var sha = Resolve(c.SourceRef ?? DefaultRef);
        return c.Commit == null ? 0 : int.Parse(Exec(Repo, "git", "rev-list", "--count", c.Commit.Sha + ".." + sha));
    }

    public bool Build(CopyInfo c, string managed, string core, string? reference = null, bool force = false)
    {
        using var held = Lock();
        Fetch();
        reference ??= c.SourceRef ?? DefaultRef;
        var sha = Resolve(reference);
        var dst = Path.Combine(c.GameDir, "BepInEx", "plugins", "TLDRevamp");
        if (!force && c.Commit?.Sha == sha && File.Exists(Path.Combine(dst, "TLDRevamp.dll")))
        {
            c.SourceRef = reference; c.NewerCommits = 0; c.Save();
            log("  already built commit " + sha[..7]);
            return false;
        }
        string sdks;
        try { sdks = Exec(cache, "dotnet", "--list-sdks"); }
        catch (UserError) { throw new UserError(SdkHelp); }
        if (!Regex.IsMatch(sdks, @"(?m)^\d+\.\d+\.\d+")) throw new UserError(SdkHelp);
        Exec(Repo, "git", "checkout", "--detach", "--force", sha);
        if (Exec(Repo, "git", "status", "--porcelain", "--", "src").Length != 0)
            throw new UserError("the source cache has uncommitted src changes: not building it");
        PopulateLib(Repo, managed, core);
        var plugin = File.ReadAllText(Path.Combine(Repo, "src", "TLDRevamp", "Plugin.cs"));
        var version = Regex.Match(plugin, @"public const string (?:BaseVersion|Version) = ""([^""]+)""").Groups[1].Value;
        if (version.Length == 0) throw new UserError("couldn't read the source's RevaMP version");
        version += "-dev." + sha[..7];
        var extra = new List<string> { "-p:RevampVersion=" + version };
        if (!plugin.Contains("public const string BaseVersion"))
            extra.Add("-p:CustomAfterMicrosoftCommonTargets=" + LegacyTargets(plugin, version, sha));
        foreach (var project in new[] { "TLDRevamp", "TLDRevamp.Multiplayer", "TLDLoaderShim" })
            Exec(Repo, "dotnet", ["build", Path.Combine("src", project, project + ".csproj"), "-c", "Release", "-nologo", .. extra]);
        var files = new[] {
            ("TLDRevamp", "TLDRevamp.dll"), ("TLDRevamp.Multiplayer", "TLDRevamp.Multiplayer.dll"), ("TLDLoaderShim", "TLDLoader.dll") };
        var commit = ReadCommit(sha);
        // Build everything before replacing a working installation.
        Directory.CreateDirectory(dst);
        foreach (var (project, file) in files)
            Files.CopyFile(Path.Combine(Repo, "src", project, "bin", "Release", "netstandard2.1", file), Path.Combine(dst, file));
        foreach (var file in new[] { "packaging/approved-mods.txt", "COPYING" })
            Files.CopyFile(Path.Combine(Repo, file), Path.Combine(dst, Path.GetFileName(file)));
        Builder.SetCfg(c.GameDir, "Debug", "Bridge", "false"); c.Bridge = false;
        c.Commit = commit; c.SourceRef = reference; c.NewerCommits = 0;
        c.RevampVersion = version; c.RevampSource = sha; c.RevampSha256 = Files.Sha256(Path.Combine(dst, "TLDRevamp.dll"));
        c.Updated = DateTime.UtcNow;
        c.Save();
        return true;
    }

    // Older refs predate MSBuild version/identity support. Compile an ignored generated file, keeping src clean.
    string LegacyTargets(string plugin, string version, string sha)
    {
        var generated = Path.Combine(Repo, "src", "TLDRevamp", "obj", "SourcePlugin.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
        var baseVersion = version.Split('-')[0];
        var source = Regex.Replace(plugin, @"(public const string Version = "")[^""]+("";)", m => m.Groups[1].Value + version + m.Groups[2].Value)
            .Replace("[BepInPlugin(Guid, \"TLD Revamp\", Version)]", $"[BepInPlugin(Guid, \"TLD Revamp\", \"{baseVersion}\")]");
        File.WriteAllText(generated, source);
        var target = new XElement("Project",
            new XElement("ItemGroup", new XAttribute("Condition", "'$(MSBuildProjectName)' == 'TLDRevamp'"),
                new XElement("Compile", new XAttribute("Remove", "Plugin.cs")),
                new XElement("Compile", new XAttribute("Include", generated))),
            new XElement("Target", new XAttribute("Name", "CopiesSourceIdentity"), new XAttribute("BeforeTargets", "GetAssemblyAttributes"),
                new XElement("ItemGroup",
                    new XElement("AssemblyMetadata", new XAttribute("Include", "SourceCommit"), new XAttribute("Value", sha)),
                    new XElement("AssemblyMetadata", new XAttribute("Include", "SourceDirty"), new XAttribute("Value", "false")))));
        var file = Path.Combine(cache, "source", "legacy.targets");
        File.WriteAllText(file, target.ToString() + "\n");
        return file;
    }

    public static readonly string[] ManagedLibs = {
        "Assembly-CSharp", "com.rlabrecque.steamworks.net", "Unity.TextMeshPro", "UnityEngine", "UnityEngine.CoreModule",
        "UnityEngine.AudioModule", "UnityEngine.AnimationModule", "UnityEngine.ImageConversionModule", "UnityEngine.IMGUIModule",
        "UnityEngine.InputLegacyModule", "UnityEngine.JSONSerializeModule", "UnityEngine.ParticleSystemModule", "UnityEngine.PhysicsModule",
        "UnityEngine.ScreenCaptureModule", "UnityEngine.TerrainModule", "UnityEngine.TextRenderingModule", "UnityEngine.UI", "UnityEngine.UIModule",
        "UnityEngine.VehiclesModule", "UnityEngine.VideoModule", "UnityEngine.UnityWebRequestModule" };

    internal static void PopulateLib(string repo, string managed, string core)
    {
        var lib = Path.Combine(repo, "lib"); Directory.CreateDirectory(lib);
        foreach (var (folder, name) in ManagedLibs.Select(n => (managed, n)).Concat(new[] { (core, "0Harmony"), (core, "BepInEx") }))
        {
            var file = Path.Combine(folder, name + ".dll");
            if (!File.Exists(file)) throw new UserError($"missing build reference {name}.dll: verify The Long Drive in Steam and reinstall the pinned BepInEx");
            Files.CopyFile(file, Path.Combine(lib, name + ".dll"));
        }
    }

    /// <summary>Read metadata without loading the game or its dependencies.</summary>
    public static (string? Commit, bool Dirty) Identity(string dll)
    {
        using var stream = File.OpenRead(dll); using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader(); string? commit = null; bool dirty = true;
        foreach (var h in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            var a = md.GetCustomAttribute(h);
            if (a.Constructor.Kind != HandleKind.MemberReference) continue;
            var member = md.GetMemberReference((MemberReferenceHandle)a.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = md.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (md.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
            var blob = md.GetBlobReader(a.Value); if (blob.ReadUInt16() != 1) continue;
            var key = blob.ReadSerializedString(); var value = blob.ReadSerializedString();
            if (key == "SourceCommit" && value != null && Regex.IsMatch(value, @"^[a-f0-9]{40}$")) commit = value;
            if (key == "SourceDirty") dirty = value != "false";
        }
        return (commit, dirty);
    }
}

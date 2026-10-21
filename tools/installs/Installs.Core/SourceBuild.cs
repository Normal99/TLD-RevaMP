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

    Files.Result Invoke(string cwd, string exe, string[] args)
    {
        try { return run(cwd, exe, args); }
        catch (System.ComponentModel.Win32Exception) { throw new UserError(exe == "git" ? GitHelp : SdkHelp); }
    }

    string Exec(string cwd, string exe, params string[] args)
    {
        var r = Invoke(cwd, exe, args);
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

    void CheckoutAt(string repo, string sha)
    {
        log("Checking out source commit " + sha + " in " + repo);
        Exec(repo, "git", "checkout", "--detach", "--force", sha);
        if (Exec(repo, "git", "rev-parse", "HEAD") != sha)
            throw new UserError("the source cache HEAD differs from the requested commit");
        if (Exec(repo, "git", "status", "--porcelain", "--", "src").Length != 0)
            throw new UserError("the source cache has uncommitted src changes: not building it");
    }

    void Checkout(string sha)
    {
        try { CheckoutAt(Repo, sha); return; }
        catch (UserError e) { log("Checkout failed: " + e.Message); }
        try
        {
            log("Recovering source cache: git reset --hard");
            Exec(Repo, "git", "reset", "--hard");
            log("Recovering source cache: git clean -ffdx -e lib/");
            Exec(Repo, "git", "clean", "-ffdx", "-e", "lib/");
            log("Retrying source checkout once");
            CheckoutAt(Repo, sha); return;
        }
        catch (UserError e) { log("Source cache recovery failed: " + e.Message); }
        var fresh = Repo + ".fresh-" + Guid.NewGuid().ToString("N");
        log("Cloning fresh source cache into " + fresh);
        Exec(cache, "git", "clone", "--no-checkout", "--origin", "origin", Remote, fresh);
        CheckoutAt(fresh, sha);
        var old = Repo + ".old-" + Guid.NewGuid().ToString("N");
        log("Keeping the old source cache at " + old);
        Directory.Move(Repo, old);
        try
        {
            log("Swapping fresh source cache into " + Repo);
            Directory.Move(fresh, Repo);
        }
        catch { Directory.Move(old, Repo); throw; }
    }

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
        var installed = Path.Combine(dst, "TLDRevamp.dll");
        if (!force && c.Commit?.Sha == sha && File.Exists(installed) && IsCurrent(installed, sha))
        {
            c.SourceRef = reference; c.NewerCommits = 0; c.Save();
            log("  already built commit " + sha[..7]);
            return false;
        }
        string sdks;
        try { sdks = Exec(cache, "dotnet", "--list-sdks"); }
        catch (UserError) { throw new UserError(SdkHelp); }
        if (!Regex.IsMatch(sdks, @"(?m)^\d+\.\d+\.\d+")) throw new UserError(SdkHelp);
        Checkout(sha);
        PopulateLib(Repo, managed, core);
        var plugin = File.ReadAllText(Path.Combine(Repo, "src", "TLDRevamp", "Plugin.cs"));
        var version = Regex.Match(plugin, @"public const string (?:BaseVersion|Version) = ""([^""]+)""").Groups[1].Value;
        if (version.Length == 0) throw new UserError("couldn't read the source's RevaMP version");
        version += "-dev." + sha[..7];
        var extra = new List<string> { "-p:RevampVersion=" + version };
        if (!plugin.Contains("public const string BaseVersion"))
            extra.Add("-p:CustomAfterMicrosoftCommonTargets=" + LegacyTargets(plugin, version, sha));
        bool disableServers = true;
        foreach (var project in new[] { "TLDRevamp", "TLDRevamp.Multiplayer", "TLDLoaderShim" })
        {
            string[] args = ["build", Path.Combine("src", project, project + ".csproj"), "-c", "Release", "-nologo", "-t:Rebuild",
                "-p:UseSharedCompilation=false", "-nodeReuse:false", .. extra];
            var result = Invoke(Repo, "dotnet", disableServers ? [.. args, "--disable-build-servers"] : args);
            var output = result.Out + "\n" + result.Err;
            if (result.Code != 0 && disableServers && output.Contains("--disable-build-servers", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(output, "unknown|unrecognized|not recognized", RegexOptions.IgnoreCase))
            {
                log("SDK rejects --disable-build-servers; retrying with UseSharedCompilation=false and nodeReuse:false");
                disableServers = false;
                result = Invoke(Repo, "dotnet", args);
            }
            if (result.Code != 0) throw new UserError("dotnet build failed (exit " + result.Code + "; see the build log)");
        }
        var files = new[] {
            ("TLDRevamp", "TLDRevamp.dll"), ("TLDRevamp.Multiplayer", "TLDRevamp.Multiplayer.dll"), ("TLDLoaderShim", "TLDLoader.dll") };
        var built = Path.Combine(Repo, "src", "TLDRevamp", "bin", "Release", "netstandard2.1", "TLDRevamp.dll");
        Verify(built, sha);
        var hash = Files.Sha256(built);
        var commit = ReadCommit(sha);
        // Build everything before replacing a working installation.
        Directory.CreateDirectory(dst);
        var preloader = Path.Combine(Repo, "src", "TLDRevamp.Preloader", "bin", "Release", "netstandard2.1", "TLDRevamp.Preloader.dll");
        if (File.Exists(preloader))
        {
            var patchers = Path.Combine(c.GameDir, "BepInEx", "patchers", "TLDRevamp"); Directory.CreateDirectory(patchers);
            Files.CopyFile(preloader, Path.Combine(patchers, "TLDRevamp.Preloader.dll"));
        }
        foreach (var (project, file) in files)
            Files.CopyFile(Path.Combine(Repo, "src", project, "bin", "Release", "netstandard2.1", file), Path.Combine(dst, file));
        Verify(installed, sha, hash);
        foreach (var file in new[] { "packaging/approved-mods.txt", "COPYING" })
            Files.CopyFile(Path.Combine(Repo, file), Path.Combine(dst, Path.GetFileName(file)));
        Builder.SetCfg(c.GameDir, "Debug", "Bridge", "false"); c.Bridge = false;
        c.Commit = commit; c.SourceRef = reference; c.NewerCommits = 0;
        c.RevampVersion = version; c.RevampSource = sha; c.RevampSha256 = hash;
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

    /// <summary>The OpenXR/VR support the game ships (issue #41), as tools/libs.sh copies it: only when present, since older
    /// supported game copies may not have every one. Commits with the VR code don't build without them.</summary>
    public static readonly string[] OptionalManagedLibs = {
        "Unity.XR.OpenXR", "Unity.XR.Management", "Unity.XR.CoreUtils", "UnityEngine.XRModule", "UnityEngine.VRModule",
        "UnityEngine.SubsystemsModule" };

    internal static void PopulateLib(string repo, string managed, string core)
    {
        var lib = Path.Combine(repo, "lib"); Directory.CreateDirectory(lib);
        foreach (var (folder, name) in ManagedLibs.Select(n => (managed, n)).Concat(new[] { (core, "0Harmony"), (core, "BepInEx"), (core, "Mono.Cecil") }))
        {
            var file = Path.Combine(folder, name + ".dll");
            if (!File.Exists(file)) throw new UserError($"missing build reference {name}.dll: verify The Long Drive in Steam and reinstall the pinned BepInEx");
            Files.CopyFile(file, Path.Combine(lib, name + ".dll"));
        }
        foreach (var name in OptionalManagedLibs)
        {
            var file = Path.Combine(managed, name + ".dll");
            if (File.Exists(file)) Files.CopyFile(file, Path.Combine(lib, name + ".dll"));
        }
    }

    /// <summary>Read metadata without loading the game or its dependencies.</summary>
    public static (string? Commit, bool Dirty) Identity(string dll)
    {
        var identity = ReadIdentity(dll);
        return (identity.Commit, identity.Dirty);
    }

    internal static void Verify(string dll, string sha, string? builtHash = null)
    {
        try
        {
            var identity = ReadIdentity(dll);
            if (identity.HasMetadata && (identity.Commit != sha || identity.Dirty))
                throw new UserError($"source identity mismatch in {dll}: expected clean {sha}, found {identity.Commit ?? "unknown"} (dirty: {identity.Dirty}); copy.json unchanged");
            if (builtHash != null && Files.Sha256(dll) != builtHash)
                throw new UserError($"installed DLL differs from the freshly built DLL: {dll}; copy.json unchanged");
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            throw new UserError($"couldn't verify source DLL {dll}: {e.Message}; copy.json unchanged");
        }
    }

    static bool IsCurrent(string dll, string sha)
    {
        try
        {
            var identity = ReadIdentity(dll);
            // Legacy DLLs need a fresh build for a trustworthy hash comparison.
            return identity.HasMetadata && identity.Commit == sha && !identity.Dirty;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException) { return false; }
    }

    static (string? Commit, bool Dirty, bool HasMetadata) ReadIdentity(string dll)
    {
        using var stream = File.OpenRead(dll); using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader(); string? commit = null; bool dirty = true, hasMetadata = false;
        foreach (var h in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            var a = md.GetCustomAttribute(h);
            if (a.Constructor.Kind != HandleKind.MemberReference) continue;
            var member = md.GetMemberReference((MemberReferenceHandle)a.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = md.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (md.GetString(type.Namespace) != "System.Reflection" || md.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
            var blob = md.GetBlobReader(a.Value); if (blob.ReadUInt16() != 1) continue;
            var key = blob.ReadSerializedString(); var value = blob.ReadSerializedString();
            if (key is "SourceCommit" or "SourceDirty") hasMetadata = true;
            if (key == "SourceCommit" && value != null && Regex.IsMatch(value, @"^[a-f0-9]{40}$")) commit = value;
            if (key == "SourceDirty") dirty = value != "false";
        }
        return (commit, dirty, hasMetadata);
    }
}

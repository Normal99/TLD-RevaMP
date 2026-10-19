using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TLDInstalls;

/// <summary>Making, updating and removing copies. A copy is Steam's game files (never Steam's folder itself) plus, by
/// kind: no mods (clean), no gameplay mods (vanilla), BepInEx and a RevaMP release (release), a dev build of the checkout
/// (dev), or the checkout's test build with the debug bridge on (test, the copy the test tools run).</summary>
public sealed class Builder(Store store, Action<string> log)
{
    public SteamGame Steam() => TLDInstalls.Steam.FindGame()
        ?? throw new UserError("couldn't find The Long Drive through Steam (set TLD_STEAM to Steam's folder if it is somewhere unusual)");

    /// <summary>What a new copy will take on disk, before it is made: (bytes of game files, shared with Steam's?).</summary>
    public (long Bytes, bool Reflink) Estimate()
    {
        var g = Steam();
        long b = Files.GameEntries.Sum(e => File.Exists(Path.Combine(g.GameDir, e)) ? new FileInfo(Path.Combine(g.GameDir, e)).Length : Files.Size(Path.Combine(g.GameDir, e)));
        Directory.CreateDirectory(store.Root);
        return (b, Files.CanReflink(Path.Combine(g.GameDir, "UnityPlayer.dll"), store.Root));
    }

    public async Task<CopyInfo> Make(string name, Kind kind, string? version = null, string? reference = null)
    {
        if (!Store.ValidName(name)) throw new UserError($"'{name}' can't be a copy's name: letters, digits, . _ - (up to 40)");
        var dir = store.PathOf(name);
        if (Directory.Exists(dir)) throw new UserError($"{dir} already exists");
        var g = Steam();
        Directory.CreateDirectory(store.Root);
        var reflink = Files.CanReflink(Path.Combine(g.GameDir, "UnityPlayer.dll"), store.Root);
        log($"Making '{name}' ({kind.ToString().ToLowerInvariant()}) in {dir}");
        log($"Game files from {g.GameDir} (Steam build {g.BuildId ?? "?"}), " + (reflink ? "as a reflink copy (shares disk blocks with Steam's)" : "as a full copy"));
        var c = new CopyInfo { Name = name, Kind = kind, Dir = dir, Made = DateTime.UtcNow, Reflink = reflink, SourceRef = kind == Kind.Source ? reference ?? SourceBuild.DefaultRef : null };
        Directory.CreateDirectory(dir);
        try
        {
            Files.CopyGame(g.GameDir, c.GameDir, reflink, log);
            RecordGame(c, g);
            if (!Env.Windows) c.Compat = MakePrefix(c);
            await InstallMod(c, version);
            Finish(c);
        }
        catch
        {
            log("Failed: removing the half-made copy");
            try { Files.DeleteDir(dir); } catch (Exception e) when (e is IOException or UserError) { log("  couldn't remove it: " + e.Message); }
            throw;
        }
        log($"Done: '{name}' — {Describe(c)}");
        return c;
    }

    /// <summary>Game files brought up to Steam's, and the mod installed again: a release copy at <paramref name="version"/>
    /// (default: the one it has), a dev or test copy rebuilt from the checkout.</summary>
    public async Task<CopyInfo> Update(string name, string? version = null, string? reference = null)
    {
        var c = store.Need(name);
        if (reference != null && c.Kind != Kind.Source) throw new UserError("--ref is for source copies (--source)");
        if (Launcher.IsRunning(c)) throw new UserError($"'{name}' is running: close the game first");
        var g = Steam();
        log($"Updating '{name}' from {g.GameDir} (Steam build {g.BuildId ?? "?"})");
        var n = Files.SyncGame(g.GameDir, c.GameDir, c.Reflink, log);
        log(n == 0 ? "  game files already match Steam's" : $"  {n} game file(s) changed");
        RecordGame(c, g);
        if (c.HasRevamp && c.Kind != Kind.Source)
        {
            var plugin = Path.Combine(c.GameDir, "BepInEx", "plugins", "TLDRevamp");
            if (Directory.Exists(plugin)) Directory.Delete(plugin, true);
        }
        await InstallMod(c, version ?? (c.Kind == Kind.Release ? c.RevampVersion : null), reference);
        c.Updated = DateTime.UtcNow;
        Finish(c);
        log($"Done: '{name}' — {Describe(c)}");
        return c;
    }

    /// <summary>Only the game files, brought up to Steam's (the test tools do this before a launch when Steam updated the
    /// game). Returns how many files changed.</summary>
    public int Sync(string name)
    {
        var c = store.Need(name);
        if (Launcher.IsRunning(c)) throw new UserError($"'{name}' is running: close the game first");
        var g = Steam();
        var n = Files.SyncGame(g.GameDir, c.GameDir, c.Reflink, log);
        RecordGame(c, g);
        if (n > 0) { c.Updated = DateTime.UtcNow; Finish(c); }
        else c.Save();
        log(n == 0 ? $"'{name}': game files match Steam's (build {c.GameBuildId})" : $"'{name}': {n} game file(s) updated to Steam's build {c.GameBuildId}");
        return n;
    }

    public void Remove(string name)
    {
        var c = store.Need(name);
        if (Launcher.IsRunning(c)) throw new UserError($"'{name}' is running: close the game first");
        if (Env.Windows && !Launcher.AnyGame()) Saves.ForThisMachine(store).Recover(log);   // its saves out of Documents first
        var full = Path.GetFullPath(c.Dir);
        if (!full.StartsWith(store.Root + Path.DirectorySeparatorChar) || !File.Exists(Path.Combine(full, "copy.json")))
            throw new UserError($"{full} isn't a copy in {store.Root}: not removing it");
        log($"Removing '{name}' ({full})");
        Files.DeleteDir(full);
        log("Done");
    }

    // ---- parts

    static void RecordGame(CopyInfo c, SteamGame g)
    {
        c.GameSource = g.GameDir;
        var build = TLDInstalls.Steam.BuildInfo(g);
        c.GameBuildId = build.BuildId ?? g.BuildId;
        c.GameBranch = build.Branch ?? g.Branch;
        var dll = Path.Combine(c.GameDir, "TheLongDrive_Data", "Managed", "Assembly-CSharp.dll");
        c.GameDllSha256 = File.Exists(dll) ? Files.Sha256(dll) : null;
    }

    void Finish(CopyInfo c)
    {
        if (Env.Windows) File.WriteAllText(Path.Combine(c.GameDir, "steam_appid.txt"), TLDInstalls.Steam.AppId);   // starts outside Steam's folder
        c.SizeBytes = Files.Size(c.Dir);
        c.OwnBytes = Files.OwnSize(c.Dir);
        c.Save();
    }

    public static string Describe(CopyInfo c)
    {
        var mod = c.Kind == Kind.Clean ? "no BepInEx, no mods" : c.Kind == Kind.Vanilla ? (Env.Windows ? "no gameplay mods (BepInEx + save isolation)" : "no mods") : $"TLD RevaMP {c.RevampVersion}" + (c.RevampSource is { } s && !s.TrimStart('v').Equals(c.RevampVersion) ? $" ({s})" : "");
        return mod + (c.HasRevamp ? ", " + c.InstalledSourceLabel : "") + $", {c.GameLabel}, bridge {(c.Bridge ? "on" : "off")}" + (c.Telemetry ? ", all telemetry on" : "") + $", {Files.Human(c.SizeBytes)}" + (c.OwnBytes is { } o ? $" ({Files.Human(o)} of its own)" : "");
    }

    async Task InstallMod(CopyInfo c, string? version, string? reference = null)
    {
        if (c.Kind == Kind.Clean) return;
        if (Env.Windows && !Launcher.AnyGame()) Saves.ForThisMachine(store).Recover(log);
        if (c.Kind == Kind.Vanilla && !Env.Windows) return;
        var (burl, bsha, bver) = Releases.BepInEx();
        log($"BepInEx {bver}");
        var bzip = await Releases.Download(burl, bsha, log);
        Releases.Extract(bzip, c.GameDir);
        c.BepInEx = bver;
        if (Env.Windows) InstallIsolation(c, log);
        switch (c.Kind)
        {
            case Kind.Source:
                new SourceBuild(log).Build(c, Path.Combine(Steam().GameDir, "TheLongDrive_Data", "Managed"), Path.Combine(c.GameDir, "BepInEx", "core"), reference);
                break;
            case Kind.Release:
            {
                var list = await Releases.List(log: log);
                var r = version != null ? Releases.Find(list, version) : Releases.Current(list);
                log($"TLD RevaMP {r.Version} ({r.Tag})");
                var zip = await Releases.Download(r, log);
                Releases.ExtractPlugin(zip, c.GameDir);
                (c.RevampVersion, c.RevampSource, c.RevampSha256) = (r.Version, r.Tag, r.ZipSha256);
                SetBridge(c.GameDir, false);
                c.Bridge = false;
                break;
            }
            case Kind.Dev:
            {
                var (zip, ver, sha, commit) = DevBuild();
                Releases.ExtractPlugin(zip, c.GameDir);
                (c.RevampVersion, c.RevampSource, c.RevampSha256) = (ver, commit, sha);
                SetBridge(c.GameDir, false);
                c.Bridge = false;
                break;
            }
            case Kind.Test:
            {
                var (ver, commit, sha) = TestBuild(c.GameDir);
                (c.RevampVersion, c.RevampSource, c.RevampSha256) = (ver, commit, sha);
                c.Bridge = true;
                break;
            }
        }
        if (c.HasRevamp && c.Kind != Kind.Source) await RecordCommit(c);
        EnsureTelemetry(c, log);
    }

    public async Task<CopyInfo> Build(string name, string? reference = null, bool force = true)
    {
        var c = store.Need(name);
        if (c.Kind != Kind.Source) throw new UserError("Build is for source copies (--source)");
        if (Launcher.IsRunning(c)) throw new UserError($"'{name}' is running: close the game first");
        var (url, sha, ver) = Releases.BepInEx();
        Releases.Extract(await Releases.Download(url, sha, log), c.GameDir);
        c.BepInEx = ver;
        if (Env.Windows) InstallIsolation(c, log);
        new SourceBuild(log).Build(c, Path.Combine(Steam().GameDir, "TheLongDrive_Data", "Managed"), Path.Combine(c.GameDir, "BepInEx", "core"), reference, force);
        EnsureTelemetry(c, log); Finish(c);
        log($"Done: '{name}' — {Describe(c)}");
        return c;
    }

    async Task RecordCommit(CopyInfo c)
    {
        c.Commit = null;
        var dll = Path.Combine(c.GameDir, "BepInEx", "plugins", "TLDRevamp", "TLDRevamp.dll");
        var identity = SourceBuild.Identity(dll);
        if (c.Kind is Kind.Dev or Kind.Test && Env.RepoDir is { } repo)
        {
            var sha = Files.RunIn(repo, "git", "rev-parse", "HEAD").Out.Trim();
            if (sha.Length != 40) return;
            // Legacy DLLs have no metadata: the checkout is still known for builds made here.
            var dirty = identity.Commit != null ? identity.Dirty : Files.RunIn(repo, "git", "status", "--porcelain", "--", "src").Out.Length > 0;
            c.Commit = new(sha, Files.RunIn(repo, "git", "show", "-s", "--format=%s", sha).Out.Trim(),
                Files.RunIn(repo, "git", "show", "-s", "--format=%cI", sha).Out.Trim(), dirty);
        }
        else c.Commit = await Releases.Commit(c.RevampSource!, identity.Commit, identity.Commit != null && identity.Dirty);
    }

    /// <summary>Upgrade an old Windows copy before launching it, including vanilla copies without BepInEx.</summary>
    public static async Task PrepareWindows(CopyInfo c, Action<string> log)
    {
        if (c.Kind == Kind.Clean) return;
        if (!File.Exists(Path.Combine(c.GameDir, "BepInEx", "core", "BepInEx.dll")))
        {
            var (url, sha, version) = Releases.BepInEx();
            Releases.Extract(await Releases.Download(url, sha, log), c.GameDir);
            c.BepInEx = version;
        }
        InstallIsolation(c, log);
        c.Save();
    }

    internal static void InstallIsolation(CopyInfo c, Action<string> log)
    {
        if (c.Kind == Kind.Clean) return;
        if (Directory.Exists(c.SavesStore))
        {
            if (Directory.Exists(c.UserData) && !Directory.EnumerateFileSystemEntries(c.UserData).Any()) Directory.Delete(c.UserData);
            log($"  moving old saves to {c.UserData}");
            Files.MoveDir(c.SavesStore, c.UserData);
        }
        Directory.CreateDirectory(c.UserData);
        var dll = Path.Combine(c.GameDir, "BepInEx", "plugins", "CopyIsolation.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        using var resource = typeof(Builder).Assembly.GetManifestResourceStream("TLDInstalls.CopyIsolation.dll")
            ?? throw new UserError("this build is missing the copy isolation plugin");
        using var output = File.Create(dll);
        resource.CopyTo(output);
    }

    // ---- the Linux Proton prefix of a copy

    /// <summary>A Proton prefix of the copy's own (its saves and settings), copied from a working one with the game's
    /// settings, saves, logs and PlayerPrefs left out: the tests' prefix (TLD_TEST_COMPAT) or the game's own Steam
    /// prefix, whichever isn't in use (only read). A test copy uses the tests' prefix itself.</summary>
    string MakePrefix(CopyInfo c)
    {
        var tests = Env.GetPath("TLD_TEST_COMPAT");
        if (c.Kind == Kind.Test && tests != null)
        {
            log($"Proton prefix: the tests' own, {tests} (TLD_TEST_COMPAT)");
            return tests;
        }
        var dest = Path.Combine(c.Dir, "compat");
        var procs = Procs.All().ToList();
        foreach (var src in new[] { tests, Steam().Compat })
        {
            if (src == null || !Directory.Exists(Path.Combine(src, "pfx"))) continue;
            if (procs.Any(p => p.InPrefix(src))) { log($"  (not copying the prefix {src}: a game runs in it)"); continue; }
            log($"Proton prefix: copied from {src} without the game's settings and saves");
            Directory.CreateDirectory(dest);
            var r = Files.Run("cp", "-a", "--reflink=auto", src + "/.", dest + "/");
            if (r.Code != 0) throw new UserError($"copying the prefix failed: {r.Err.Trim()}");
            StripPrefix(dest);
            return dest;
        }
        log("Proton prefix: Proton makes a new one on the first launch (that start takes a few minutes)");
        Directory.CreateDirectory(dest);
        return dest;
    }

    /// <summary>The game's settings and saves (Documents/TheLongDrive), its Unity logs (AppData/LocalLow/Genesz) and its
    /// PlayerPrefs (registry key Software\Genesz\TheLongDrive in user.reg) out of a copied prefix, as tools/testprefix.py does.</summary>
    public static void StripPrefix(string compat)
    {
        var u = Path.Combine(compat, "pfx", "drive_c", "users", "steamuser");
        foreach (var d in new[] { Path.Combine(u, "Documents", "TheLongDrive"), Path.Combine(u, "AppData", "LocalLow", "Genesz") })
            if (Directory.Exists(d)) Directory.Delete(d, true);
        File.Delete(Path.Combine(compat, "launch.log"));
        var reg = Path.Combine(compat, "pfx", "user.reg");
        if (!File.Exists(reg)) return;
        var t = File.ReadAllText(reg);
        // a registry section runs from its [key] line to the next blank line
        var t2 = Regex.Replace(t, @"(?ms)^\[Software\\\\Genesz\\\\TheLongDrive\][^\n]*\n.*?(?:\n\n|\z)", "");
        if (t2 != t) File.WriteAllText(reg, t2);
    }

    // ---- builds from the checkout

    string Repo() => Env.RepoDir ?? throw new UserError("dev and test copies are built from a checkout of the repository: run this from one (or set TLD_REPO_DIR)");

    /// <summary>tools/devrelease.py --dry-run: the checked-out commit as TLD RevaMP &lt;version&gt;-dev.&lt;commit&gt;, in the
    /// player release layout (dist/TLD-RevaMP-&lt;version&gt;.zip). Returns (zip, version, sha256, commit).</summary>
    (string, string, string, string) DevBuild()
    {
        var repo = Repo();
        NeedLib(repo);
        log("Building a dev build of the checkout (tools/devrelease.py --dry-run)");
        var (exe, pre) = Env.Windows ? ("py", new[] { "-3" }) : ("python3", Array.Empty<string>());
        var lines = Stream(repo, exe, [.. pre, Path.Combine("tools", "devrelease.py"), "--dry-run"]);
        var m = lines.Select(l => Regex.Match(l, @"^(.*TLD-RevaMP-(\S+)\.zip) \(\d+ KB\)\s+sha256 ([0-9a-f]{64})")).LastOrDefault(x => x.Success)
                ?? throw new UserError("tools/devrelease.py didn't make a zip (its output is above)");
        var commit = Files.RunIn(repo, "git", "rev-parse", "--short=7", "HEAD").Out.Trim();
        return (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, commit);
    }

    /// <summary>The checkout's build as the test tools deploy it (tools/build.sh): TLDRevamp and the TLDLoader shim built,
    /// copied into BepInEx/plugins/TLDRevamp, third-party TLDLoader mods into BepInEx/TLDLoaderMods, the bridge on.</summary>
    (string, string, string) TestBuild(string game)
    {
        var repo = Repo();
        NeedLib(repo);
        log("Building the checkout's test build (dotnet build)");
        foreach (var proj in new[] { "TLDRevamp/TLDRevamp.csproj", "TLDLoaderShim/TLDLoaderShim.csproj" })
        {
            var outp = Stream(repo, "dotnet", "build", Path.Combine(repo, "src", proj), "-c", "Release", "-nologo", "-v", "q");
            if (!outp.Any(l => l.Contains("0 Error(s)"))) throw new UserError($"building {proj} failed (output above)");
        }
        var bin = Path.Combine("bin", "Release", "netstandard2.1");
        var dst = Path.Combine(game, "BepInEx", "plugins", "TLDRevamp");
        Directory.CreateDirectory(dst);
        foreach (var f in new[] { Path.Combine(repo, "src", "TLDRevamp", bin, "TLDRevamp.dll"), Path.Combine(repo, "src", "TLDRevamp.Multiplayer", bin, "TLDRevamp.Multiplayer.dll"),
                                  Path.Combine(repo, "packaging", "approved-mods.txt"), Path.Combine(repo, "src", "TLDLoaderShim", bin, "TLDLoader.dll") })
            if (File.Exists(f)) Files.CopyFile(f, Path.Combine(dst, Path.GetFileName(f)));
        var mods = Path.Combine(repo, "thirdparty", "TLDLoaderMods");
        if (Directory.Exists(mods))
            foreach (var m in Directory.EnumerateFiles(mods, "*.dll"))
            {
                Directory.CreateDirectory(Path.Combine(game, "BepInEx", "TLDLoaderMods"));
                Files.CopyFile(m, Path.Combine(game, "BepInEx", "TLDLoaderMods", Path.GetFileName(m)));
            }
        SetBridge(game, true);
        var ver = Regex.Match(File.ReadAllText(Path.Combine(repo, "src", "TLDRevamp", "Plugin.cs")), @"(?:BaseVersion|Version) = ""([^""]+)""").Groups[1].Value;
        var commit = Files.RunIn(repo, "git", "rev-parse", "--short=7", "HEAD").Out.Trim();
        if (Files.RunIn(repo, "git", "status", "--porcelain", "--untracked-files=no").Out.Trim().Length > 0) commit += "+changes";
        return (ver, commit, Files.Sha256(Path.Combine(dst, "TLDRevamp.dll")));
    }

    static void NeedLib(string repo)
    {
        if (!Directory.Exists(Path.Combine(repo, "lib")) || !Directory.EnumerateFiles(Path.Combine(repo, "lib"), "*.dll").Any())
            throw new UserError($"{Path.Combine(repo, "lib")} has no game DLLs to build against: run tools/libs.sh in the checkout first (README, Building from source)");
    }

    static string RevampCfg(string game) => Path.Combine(game, "BepInEx", "config", "tldrevamp.core.cfg");

    /// <summary>The bridge line of RevaMP's config (BepInEx/config/tldrevamp.core.cfg), keeping its line endings.</summary>
    public static void SetBridge(string game, bool on)
    {
        if (on || File.Exists(RevampCfg(game))) SetCfg(game, "Debug", "Bridge", on ? "true" : "false");
    }

    /// <summary>All of RevaMP's telemetry on (docs/TELEMETRY.md): the play log, black box and frame counters are always on,
    /// so this turns on what is off by default: the script timers with the engine phases, their allocation estimate, and
    /// the most black-box columns. The timed list (ScriptTimerMethods) stays as it is: "default", the game's hottest scripts
    /// and RevaMP's own per-frame work.</summary>
    public static void SetTelemetry(string game)
    {
        SetCfg(game, "Telemetry", "ScriptTimers", "true");
        SetCfg(game, "Telemetry", "ScriptTimerAlloc", "true");
        SetCfg(game, "Telemetry", "ScriptTimerColumns", "32");
    }

    /// <summary>A copy with RevaMP gets all telemetry on once: when it is made, or at its next update or launch if it is older.
    /// After that the config is the copy's own (a value changed by hand stays).</summary>
    public static void EnsureTelemetry(CopyInfo c, Action<string> log)
    {
        if (!c.HasRevamp || c.Telemetry) return;
        SetTelemetry(c.GameDir);
        c.Telemetry = true;
        log("  all telemetry on (script timers, engine phases, allocations: BepInEx/config/tldrevamp.core.cfg [Telemetry])");
    }

    /// <summary>One "key = value" line in a section of RevaMP's config, added (with the section) when missing; the rest of the
    /// file and its line endings stay as they are.</summary>
    public static void SetCfg(string game, string section, string key, string value)
    {
        var cfg = RevampCfg(game);
        var t = File.Exists(cfg) ? File.ReadAllText(cfg) : "";
        var nl = t.Contains("\r\n") ? "\r\n" : "\n";
        var head = Regex.Match(t, $@"(?m)^\[{Regex.Escape(section)}\][ \t]*\r?$");
        if (head.Success)
        {
            int start = head.Index + head.Length;
            var next = Regex.Match(t[start..], @"(?m)^\[");
            int end = next.Success ? start + next.Index : t.Length;
            var body = t[start..end];
            var line = Regex.Match(body, $@"(?m)^{Regex.Escape(key)} = [^\r\n]*");
            body = line.Success ? body[..line.Index] + $"{key} = {value}" + body[(line.Index + line.Length)..]
                 : body.TrimEnd('\r', '\n') + nl + $"{key} = {value}" + nl + (end < t.Length ? nl : "");
            t = t[..start] + body + t[end..];
        }
        else t += (t.Length > 0 && !t.EndsWith('\n') ? nl : "") + nl + $"[{section}]" + nl + $"{key} = {value}" + nl;
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        File.WriteAllText(cfg, t);
    }

    /// <summary>Run a program in a folder, passing its output on line by line; returns the lines.</summary>
    List<string> Stream(string cwd, string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var lines = new List<string>();
        using var p = new Process { StartInfo = psi };
        DataReceivedEventHandler h = (_, e) => { if (e.Data == null) return; lock (lines) lines.Add(e.Data); log("  " + e.Data); };
        p.OutputDataReceived += h; p.ErrorDataReceived += h;
        try { p.Start(); }
        catch (System.ComponentModel.Win32Exception e) { throw new UserError($"couldn't run {exe}: {e.Message}"); }
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new UserError($"{exe} {string.Join(' ', args)} failed (exit {p.ExitCode}, output above)");
        return lines;
    }
}

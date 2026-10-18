using System.Diagnostics;
using System.Security.Cryptography;

namespace TLDInstalls;

/// <summary>Copying the game's files. On Linux `cp --reflink` makes a copy that shares its disk blocks with Steam's
/// on btrfs and XFS (almost no extra space until one side changes a file); everywhere else it is a full copy.</summary>
public static class Files
{
    /// <summary>The game's own files in its folder. Everything else there (BepInEx, winhttp.dll, doorstop, RevaMP's
    /// installer folder, …) belongs to mods and stays out of a copy.</summary>
    public static readonly string[] GameEntries = { "TheLongDrive.exe", "TheLongDrive_Data", "MonoBleedingEdge", "UnityPlayer.dll", "UnityCrashHandler64.exe" };

    /// <summary>Files mods put in the game folder (left out of a copy without a warning).</summary>
    static readonly string[] ModEntries = { "BepInEx", "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt", "INSTALL.txt", "RevaMP-installer", "steam_appid.txt" };

    /// <summary>Left out inside TheLongDrive_Data: screenshots the test bridge saves there (mpshot_*.png).</summary>
    public static bool Skipped(string rel) =>
        rel.StartsWith("TheLongDrive_Data" + Path.DirectorySeparatorChar) && Path.GetDirectoryName(rel) == "TheLongDrive_Data" && rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    /// <summary>Entries of Steam's game folder that are neither the game's nor a known mod's: not copied, reported.</summary>
    public static List<string> Unknown(string gameDir) =>
        Directory.EnumerateFileSystemEntries(gameDir).Select(Path.GetFileName).OfType<string>()
                 .Where(n => !GameEntries.Contains(n, StringComparer.OrdinalIgnoreCase) && !ModEntries.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>Tests force the plain copy (TLD_INSTALLS_NO_REFLINK=1) to check the Windows path.</summary>
    public static bool NoReflink => Environment.GetEnvironmentVariable("TLD_INSTALLS_NO_REFLINK") == "1";

    /// <summary>Can a file be reflinked from <paramref name="srcDir"/> into <paramref name="dstDir"/>? (Linux, same btrfs/XFS.)</summary>
    public static bool CanReflink(string srcFile, string dstDir)
    {
        if (!OperatingSystem.IsLinux() || NoReflink) return false;
        Directory.CreateDirectory(dstDir);
        var probe = Path.Combine(dstDir, ".reflink-probe");
        try { return Run("cp", "--reflink=always", srcFile, probe).Code == 0; }
        finally { File.Delete(probe); }
    }

    /// <summary>Copy the game's files from <paramref name="src"/> into <paramref name="dst"/> (made if missing).</summary>
    public static void CopyGame(string src, string dst, bool reflink, Action<string> log)
    {
        Directory.CreateDirectory(dst);
        foreach (var e in GameEntries)
        {
            var s = Path.Combine(src, e);
            if (!File.Exists(s) && !Directory.Exists(s)) throw new UserError($"{s} is missing: is the game fully installed?");
            log("  copy " + e);
            if (reflink)
            {
                var r = Run("cp", "-a", "--reflink=always", s, dst + "/");
                if (r.Code != 0) throw new UserError($"copying {e} failed: {r.Err.Trim()}");
            }
            else if (Directory.Exists(s)) CopyDir(s, Path.Combine(dst, e));
            else CopyFile(s, Path.Combine(dst, e));
        }
        var data = Path.Combine(dst, "TheLongDrive_Data");
        foreach (var png in Directory.EnumerateFiles(data, "*.png")) File.Delete(png);
        foreach (var u in Unknown(src)) log($"  not copied: {u} (neither the game's nor a known mod's file)");
    }

    /// <summary>Bring a copy's game files up to Steam's: files that differ in size or time are copied again, files Steam
    /// no longer has are removed. Returns how many files changed.</summary>
    public static int SyncGame(string src, string dst, bool reflink, Action<string> log)
    {
        int n = 0;
        foreach (var e in GameEntries)
        {
            var s = Path.Combine(src, e);
            var d = Path.Combine(dst, e);
            if (File.Exists(s)) { if (Differs(s, d)) { Copy1(s, d, reflink); n++; log("  updated " + e); } continue; }
            if (!Directory.Exists(s)) continue;
            var want = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in Directory.EnumerateFiles(s, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src, f);
                if (Skipped(rel)) continue;
                want.Add(rel);
                var t = Path.Combine(dst, rel);
                if (!Differs(f, t)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                Copy1(f, t, reflink); n++; log("  updated " + rel);
            }
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).ToList())
            {
                var rel = Path.GetRelativePath(dst, f);
                if (!want.Contains(rel) && !Skipped(rel)) { File.Delete(f); n++; log("  removed " + rel + " (Steam's game no longer has it)"); }
            }
        }
        return n;
    }

    static bool Differs(string a, string b)
    {
        var fb = new FileInfo(b);
        if (!fb.Exists) return true;
        var fa = new FileInfo(a);
        return fa.Length != fb.Length || Math.Abs((fa.LastWriteTimeUtc - fb.LastWriteTimeUtc).TotalSeconds) >= 2;
    }

    static void Copy1(string s, string d, bool reflink)
    {
        if (reflink)
        {
            var r = Run("cp", "--preserve=mode,timestamps", "--reflink=always", s, d);
            if (r.Code != 0) throw new UserError($"copying {s} failed: {r.Err.Trim()}");
        }
        else CopyFile(s, d);
    }

    public static void CopyFile(string s, string d)
    {
        File.Copy(s, d, overwrite: true);
        File.SetLastWriteTimeUtc(d, File.GetLastWriteTimeUtc(s));
    }

    public static void CopyDir(string s, string d)
    {
        Directory.CreateDirectory(d);
        foreach (var f in Directory.EnumerateFiles(s)) CopyFile(f, Path.Combine(d, Path.GetFileName(f)));
        foreach (var sub in Directory.EnumerateDirectories(s)) CopyDir(sub, Path.Combine(d, Path.GetFileName(sub)));
    }

    /// <summary>Move a folder: a rename on the same drive, else copy, check every file arrived, then delete the original
    /// (so a save folder is never lost halfway).</summary>
    public static void MoveDir(string s, string d)
    {
        if (Directory.Exists(d)) throw new UserError($"{d} already exists: not overwriting it");
        Directory.CreateDirectory(Path.GetDirectoryName(d)!);
        try { Directory.Move(s, d); return; }
        catch (IOException) when (Path.GetPathRoot(Path.GetFullPath(s)) != Path.GetPathRoot(Path.GetFullPath(d))) { }
        CopyDir(s, d);
        var (n1, b1) = Count(s); var (n2, b2) = Count(d);
        if (n1 != n2 || b1 != b2) throw new UserError($"copying {s} to {d} came out different ({n2}/{n1} files): the original is kept");
        Directory.Delete(s, true);
    }

    static (int, long) Count(string dir)
    {
        int n = 0; long b = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) { n++; b += new FileInfo(f).Length; }
        return (n, b);
    }

    /// <summary>The bytes of every file below a folder (symbolic links not followed).</summary>
    public static long Size(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long b = 0;
        var stack = new Stack<DirectoryInfo>(); stack.Push(new DirectoryInfo(dir));
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            IEnumerable<FileSystemInfo> items;
            try { items = d.EnumerateFileSystemInfos().ToList(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var i in items)
            {
                if (i.LinkTarget != null) continue;
                if (i is DirectoryInfo sub) stack.Push(sub); else if (i is FileInfo f) b += f.Length;
            }
        }
        return b;
    }

    /// <summary>Bytes only this folder uses on btrfs (`btrfs filesystem du`: blocks not shared with Steam's copy), else null.</summary>
    public static long? OwnSize(string dir)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(dir)) return null;
        try
        {
            var r = Run("btrfs", "filesystem", "du", "-s", "--raw", dir);
            if (r.Code != 0) return null;
            var last = r.Out.Trim().Split('\n').Last().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return last.Length >= 2 && long.TryParse(last[1], out var excl) ? excl : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }   // no btrfs tool
    }

    /// <summary>Delete a copy's folder. On Linux with rm (never follows the prefix's links, e.g. dosdevices/z: → /).</summary>
    public static void DeleteDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        if (OperatingSystem.IsWindows()) { Directory.Delete(dir, true); return; }
        var r = Run("rm", "-rf", "--one-file-system", "--", dir);
        if (r.Code != 0) throw new UserError($"removing {dir} failed: {r.Err.Trim()}");
    }

    public static string Sha256(string file)
    {
        using var f = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    public static string Human(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" : $"{Math.Max(1, b / 1024)} KB";

    public sealed record Result(int Code, string Out, string Err);

    /// <summary>Run a program, wait, capture its output.</summary>
    public static Result Run(string exe, params string[] args) => RunIn(null, exe, args);

    public static Result RunIn(string? cwd, string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (cwd != null) psi.WorkingDirectory = cwd;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return new Result(p.ExitCode, outp, err.Result);
    }
}

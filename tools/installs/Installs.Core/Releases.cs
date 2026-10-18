using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TLDInstalls;

public sealed record Release(string Tag, string Version, bool Prerelease, DateTime Published, string ZipUrl, string ZipSha256);

/// <summary>Downloads, each checked against its SHA-256 before use: BepInEx as installer/install.sh pins it, and TLD
/// RevaMP's release zips with the SHA-256 GitHub publishes for each release file.</summary>
public static class Releases
{
    /// <summary>The GitHub repository of the releases (TLD_REPO to use a fork).</summary>
    public static string Repo => Env.Get("TLD_REPO") ?? "Normal99/TLD-RevaMP";

    // BepInEx as installer/install.sh pins it (BEPINEX_URL / BEPINEX_SHA256); read from the checkout's install.sh when
    // there is one. tools/installs/Installs.Tests checks these match it.
    public const string BepInExUrlDefault = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip";
    public const string BepInExShaDefault = "82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4";

    public static (string Url, string Sha256, string Version) BepInEx()
    {
        var pins = InstallerPins();
        var url = pins.GetValueOrDefault("BEPINEX_URL") ?? BepInExUrlDefault;
        var sha = pins.GetValueOrDefault("BEPINEX_SHA256") ?? BepInExShaDefault;
        var ver = Regex.Match(url, @"_(\d+(\.\d+)+)\.zip$").Groups[1].Value;
        return (url, sha, ver);
    }

    /// <summary>The "Pinned downloads" block of the checkout's installer/install.sh (KEY="value" lines).</summary>
    public static Dictionary<string, string> InstallerPins(string? installSh = null)
    {
        installSh ??= Env.RepoDir is { } r ? Path.Combine(r, "installer", "install.sh") : null;
        var d = new Dictionary<string, string>();
        if (installSh == null || !File.Exists(installSh)) return d;
        foreach (Match m in Regex.Matches(File.ReadAllText(installSh), @"(?m)^(REVAMP_VERSION|REVAMP_URL|REVAMP_SHA256|BEPINEX_URL|BEPINEX_SHA256)=""([^""]*)"""))
            d[m.Groups[1].Value] = m.Groups[2].Value;
        return d;
    }

    static readonly HttpClient Http = MakeClient();

    static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TLD-copies", "1"));
        return h;
    }

    static string ListCache => Path.Combine(Env.CacheDir, "releases.json");

    /// <summary>The published releases (not drafts) that have a TLD-RevaMP-&lt;version&gt;.zip with a published SHA-256,
    /// newest first. Without the network: the list from the last time.</summary>
    public static async Task<List<Release>> List(bool refresh = true)
    {
        string json;
        try
        {
            if (!refresh) throw new HttpRequestException("cached");
            json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=100");
            Directory.CreateDirectory(Env.CacheDir);
            await File.WriteAllTextAsync(ListCache, json);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            if (!File.Exists(ListCache)) throw new UserError($"couldn't get the release list from GitHub ({e.Message})");
            json = await File.ReadAllTextAsync(ListCache);
        }
        return Parse(json);
    }

    public static List<Release> Parse(string json)
    {
        var list = new List<Release>();
        using var doc = JsonDocument.Parse(json);
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean()) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            var ver = tag.TrimStart('v');
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                if (a.GetProperty("name").GetString() != $"TLD-RevaMP-{ver}.zip") continue;
                var digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                if (digest is not { } dgs || !dgs.StartsWith("sha256:")) continue;   // no checksum published: not offered
                var pub = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTime() : DateTime.MinValue;
                list.Add(new Release(tag, ver, r.GetProperty("prerelease").GetBoolean(), pub, a.GetProperty("browser_download_url").GetString()!, dgs[7..]));
            }
        }
        return list.OrderByDescending(r => r.Published).ToList();
    }

    /// <summary>The release installer/install.sh is pinned to (what players install), else the newest.</summary>
    public static Release Current(List<Release> list)
    {
        var pinned = InstallerPins().GetValueOrDefault("REVAMP_VERSION");
        return list.FirstOrDefault(r => r.Version == pinned) ?? list.FirstOrDefault() ?? throw new UserError("no releases found");
    }

    public static Release Find(List<Release> list, string version) =>
        list.FirstOrDefault(r => r.Version == version.TrimStart('v')) ?? throw new UserError($"no release {version} with a published checksum (tld-installs releases lists them)");

    /// <summary>A file from the download cache, downloaded first if needed; refused unless its SHA-256 matches.</summary>
    public static async Task<string> Download(string url, string sha256, Action<string> log)
    {
        var dir = Path.Combine(Env.CacheDir, "downloads");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Path.GetFileName(new Uri(url).AbsolutePath));
        if (File.Exists(file) && Files.Sha256(file) == sha256) { log("  cached  " + Path.GetFileName(file)); return file; }
        log("  download " + url);
        var tmp = file + ".part";
        await using (var s = await Http.GetStreamAsync(url))
        await using (var f = File.Create(tmp)) await s.CopyToAsync(f);
        var got = Files.Sha256(tmp);
        if (got != sha256)
        {
            File.Delete(tmp);
            throw new UserError($"{Path.GetFileName(file)} has SHA-256 {got}, expected {sha256}: not used");
        }
        File.Move(tmp, file, true);
        log("  checked sha256 ok");
        return file;
    }

    /// <summary>Unzip into a folder; refuses entries that would land outside it. Returns the files written (relative).</summary>
    public static List<string> Extract(string zip, string dest, Func<string, string?>? map = null)
    {
        var written = new List<string>();
        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        using var z = ZipFile.OpenRead(zip);
        foreach (var e in z.Entries)
        {
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue;
            var rel = map != null ? map(e.FullName.Replace('\\', '/')) : e.FullName.Replace('\\', '/');
            if (rel == null) continue;
            var target = Path.GetFullPath(Path.Combine(dest, rel));
            if (!target.StartsWith(root, StringComparison.Ordinal)) throw new UserError($"{Path.GetFileName(zip)} has an entry outside its folder ({e.FullName}): not used");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, true);
            written.Add(rel);
        }
        return written;
    }

    /// <summary>The RevaMP plugin folder of a release or dev zip (the folder holding TLDRevamp.dll), unpacked into the
    /// game's BepInEx/plugins/TLDRevamp. Returns the files written.</summary>
    public static List<string> ExtractPlugin(string zip, string gameDir)
    {
        string? prefix;
        using (var z = ZipFile.OpenRead(zip))
        {
            var dll = z.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith("TLDRevamp.dll", StringComparison.Ordinal) &&
                                                    (e.FullName.Length == 13 || e.FullName.Replace('\\', '/')[^14] == '/'))
                      ?? throw new UserError($"{Path.GetFileName(zip)} has no TLDRevamp.dll");
            var f = dll.FullName.Replace('\\', '/');
            prefix = f[..^"TLDRevamp.dll".Length];
        }
        return Extract(zip, Path.Combine(gameDir, "BepInEx", "plugins", "TLDRevamp"),
                       n => n.StartsWith(prefix, StringComparison.Ordinal) && n.Length > prefix.Length ? n[prefix.Length..] : null);
    }
}

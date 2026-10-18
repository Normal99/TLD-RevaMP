using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TLDInstalls;

public sealed record Release(string Tag, string Version, bool Prerelease, DateTime Published, string ZipUrl, string ZipSha256, bool Draft = false)
{
    public string Label => Version + (Draft ? " (draft)" : "") + (Prerelease ? " (pre-release)" : "");
}

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

    public const string SignInNotice = "Draft dev builds need a GitHub sign-in with repository write access (gh auth login).";

    /// <summary>Checksummed releases, including drafts when signed in. Offline: the last list for this repository
    /// and sign-in mode. Tokens only live in memory and request headers.</summary>
    public static async Task<List<Release>> List(bool refresh = true, Action<string>? log = null)
        => await List(Http, await GitHubAuth.Token(), Env.CacheDir, Repo, refresh, log);

    internal static async Task<List<Release>> List(HttpClient http, string? token, string cache, string repo, bool refresh = true, Action<string>? log = null)
    {
        if (token == null) log?.Invoke(SignInNotice);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(repo)));
        var file = Path.Combine(cache, $"releases-{key}-{(token == null ? "public" : "signed-in")}.json");
        string json;
        try
        {
            if (!refresh) throw new HttpRequestException("cached");
            var items = new List<JsonElement>();
            for (int page = 1; ; page++)
            {
                using var request = Request($"https://api.github.com/repos/{repo}/releases?per_page=100&page={page}", token, asset: false);
                using var response = await http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                foreach (var item in doc.RootElement.EnumerateArray()) items.Add(item.Clone());
                if (doc.RootElement.GetArrayLength() < 100) break;
            }
            json = JsonSerializer.Serialize(items);
            // Validate before replacing the working cache; never persist a reflected credential either.
            if (token != null) json = json.Replace(token, "[redacted]", StringComparison.Ordinal);
            _ = Parse(json, token != null);
            Directory.CreateDirectory(cache);
            await File.WriteAllTextAsync(file, json);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or FormatException or ArgumentException)
        {
            if (!File.Exists(file)) throw new UserError("couldn't get the release list from GitHub (check the connection and GitHub sign-in)");
            json = await File.ReadAllTextAsync(file);
        }
        return Parse(json, token != null);
    }

    static HttpRequestMessage Request(string url, string? token, bool asset)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(asset ? "application/octet-stream" : "application/vnd.github+json"));
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public static List<Release> Parse(string json, bool includeDrafts = false)
    {
        var list = new List<Release>();
        using var doc = JsonDocument.Parse(json);
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            bool draft = r.GetProperty("draft").GetBoolean();
            if (draft && !includeDrafts) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            var ver = tag.TrimStart('v');
            if (!Regex.IsMatch(ver, @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$") || !System.Version.TryParse(ver.Split('-')[0], out _)) continue;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                if (a.GetProperty("name").GetString() != $"TLD-RevaMP-{ver}.zip") continue;
                var digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                var sha = digest != null && Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$") ? digest[7..] : null;
                if (sha == null && r.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String)
                {
                    var match = Regex.Match(body.GetString()!, @"SHA-256 of " + Regex.Escape($"TLD-RevaMP-{ver}.zip") + @":\s*`([0-9a-fA-F]{64})`");
                    if (match.Success) sha = match.Groups[1].Value;
                }
                if (sha == null) continue;   // no checksum published: not offered
                var pub = DateTime.MinValue;
                foreach (var date in new[] { "published_at", "created_at" })
                    if (r.TryGetProperty(date, out var p) && p.ValueKind == JsonValueKind.String && p.TryGetDateTime(out pub)) break;
                var url = a.TryGetProperty(draft ? "url" : "browser_download_url", out var u) ? u.GetString() : null;
                if (url == null) continue;
                list.Add(new Release(tag, ver, r.GetProperty("prerelease").GetBoolean(), pub, url, sha.ToLowerInvariant(), draft));
            }
        }
        return list.OrderByDescending(r => r.Published).ThenByDescending(r => System.Version.Parse(r.Version.Split('-')[0]))
                   .ThenBy(r => r.Version.Contains('-')).ThenByDescending(r => r.Version, StringComparer.Ordinal).ToList();
    }

    /// <summary>The release installer/install.sh is pinned to (what players install), else the newest public release.</summary>
    public static Release Current(List<Release> list)
    {
        var pinned = InstallerPins().GetValueOrDefault("REVAMP_VERSION");
        return list.FirstOrDefault(r => r.Version == pinned) ?? list.FirstOrDefault(r => !r.Draft)
            ?? list.FirstOrDefault() ?? throw new UserError("no releases found");
    }

    public static Release Find(List<Release> list, string version) =>
        list.FirstOrDefault(r => r.Version == version.TrimStart('v')) ?? throw new UserError($"no release {version} with a published checksum (tld-installs releases lists them)");

    /// <summary>A file from the download cache, downloaded first if needed; refused unless its SHA-256 matches.</summary>
    public static async Task<string> Download(string url, string sha256, Action<string> log)
        => await Download(Http, url, sha256, log, Env.CacheDir);

    public static async Task<string> Download(Release release, Action<string> log)
    {
        var token = release.Draft ? await GitHubAuth.Token() : null;
        if (release.Draft && token == null) throw new UserError(SignInNotice);
        return await Download(Http, release.ZipUrl, release.ZipSha256, log, Env.CacheDir, token);
    }

    internal static async Task<string> Download(HttpClient http, string url, string sha256, Action<string> log, string cache, string? token = null)
    {
        if (!Regex.IsMatch(sha256, @"^[0-9a-fA-F]{64}$")) throw new UserError("download has no valid SHA-256: not used");
        sha256 = sha256.ToLowerInvariant();
        if (token != null && (!Uri.TryCreate(url, UriKind.Absolute, out var api) || api.Scheme != "https" || api.Host != "api.github.com" || api.UserInfo.Length != 0))
            throw new UserError("draft download is not a GitHub API asset: not used");
        var dir = Path.Combine(cache, "downloads");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, sha256 + ".zip");
        if (File.Exists(file) && Files.Sha256(file) == sha256) { log("  cached  " + Path.GetFileName(file)); return file; }
        log("  download " + (token != null ? "GitHub release asset" : url));
        var tmp = file + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var request = Request(url, token, asset: true);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var s = await response.Content.ReadAsStreamAsync())
            await using (var f = File.Create(tmp)) await s.CopyToAsync(f);
            var got = Files.Sha256(tmp);
            if (got != sha256) throw new UserError($"download has SHA-256 {got}, expected {sha256}: not used");
            File.Move(tmp, file, true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or FormatException or ArgumentException)
        { throw new UserError("couldn't download the release asset from GitHub (check the connection and GitHub sign-in)"); }
        finally { File.Delete(tmp); }
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

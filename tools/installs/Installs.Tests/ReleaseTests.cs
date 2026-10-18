using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TLDInstalls.Tests;

public sealed class ReleaseTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("tld-copies-releases-test").FullName;
    const string Token = "test-credential-never-emit";
    static readonly string Sha = new('a', 64);
    public void Dispose() => Directory.Delete(tmp, true);

    static object Item(string version, bool draft = false, bool prerelease = false, string? digest = null, string? body = null, string? date = "2026-10-18T12:00:00Z") => new
    {
        tag_name = "v" + version, draft, prerelease, published_at = draft ? null : date, created_at = date,
        body = body ?? $"SHA-256 of TLD-RevaMP-{version}.zip: `{Sha}`\n",
        assets = new[] { new { name = $"TLD-RevaMP-{version}.zip", digest,
            url = "https://api.github.com/repos/example/game/releases/assets/42",
            browser_download_url = $"https://github.com/example/game/releases/download/v{version}/TLD-RevaMP-{version}.zip" } }
    };

    static string Fixture() => JsonSerializer.Serialize(new[]
    {
        Item("0.9.0", digest: "sha256:" + Sha),
        Item("0.69.1-dev.fea22b0", draft: true, prerelease: true, date: "2026-10-18T16:00:00Z"),
        Item("0.69.1-dev.abc1234", prerelease: true, date: "2026-10-18T14:00:00Z"),
        Item("0.10.0", digest: "sha256:" + Sha),
        Item("0.70.0", body: "no checksum")
    });

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task TokenSourcesHaveTheRequiredPriority()
    {
        int calls = 0;
        Task<string?> Gh() { calls++; return Task.FromResult<string?>("cli-credential"); }
        Assert.Equal("first", await GitHubAuth.Token(k => k == "GH_TOKEN" ? "first" : "second", Gh));
        Assert.Equal("second", await GitHubAuth.Token(k => k == "GITHUB_TOKEN" ? "second" : null, Gh));
        Assert.Equal(0, calls);
        Assert.Equal("cli-credential", await GitHubAuth.Token(_ => " ", Gh));
        Assert.Equal(1, calls);
        Assert.Null(await GitHubAuth.Token(_ => null, () => Task.FromResult<string?>(null)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListUsesAuthIncludesDraftsAndKeepsTokensOutOfCache(bool signedIn)
    {
        var log = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal(signedIn ? Token : null, request.Headers.Authorization?.Parameter);
            Assert.Equal(signedIn ? "Bearer" : null, request.Headers.Authorization?.Scheme);
            Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);
            return Json(signedIn ? Fixture().Replace("SHA-256 of", Token + " SHA-256 of") : Fixture());
        }));
        var list = await Releases.List(http, signedIn ? Token : null, tmp, "example/game", log: log.Add);
        Assert.Equal(signedIn ? 4 : 3, list.Count);
        Assert.Equal(signedIn ? "0.69.1-dev.fea22b0" : "0.69.1-dev.abc1234", list[0].Version);
        Assert.Equal(new[] { "0.10.0", "0.9.0" }, list.TakeLast(2).Select(r => r.Version));
        Assert.All(list, r => Assert.Equal(Sha, r.ZipSha256));
        if (signedIn)
        {
            Assert.True(list[0].Draft);
            Assert.Contains("draft", list[0].Label);
            Assert.Contains("pre-release", list[0].Label);
            Assert.StartsWith("https://api.github.com/", list[0].ZipUrl);
            Assert.Equal(list[0], Releases.Find(list, "v0.69.1-dev.fea22b0"));
            Assert.Empty(log);
        }
        else Assert.Equal(Releases.SignInNotice, Assert.Single(log));
        Assert.All(Directory.GetFiles(tmp), f => Assert.DoesNotContain(Token, File.ReadAllText(f)));
        Assert.All(log, line => Assert.DoesNotContain(Token, line));
    }

    [Fact]
    public void DigestTakesPrecedenceAndBodyMustNameTheExactAsset()
    {
        var other = new string('b', 64);
        var json = JsonSerializer.Serialize(new[]
        {
            Item("0.69.1", digest: "sha256:" + other.ToUpperInvariant()),
            Item("0.69.2", body: $"SHA-256 of TLD-RevaMP-0.69.1.zip: `{Sha}`"),
            Item("0.69.3", digest: "sha256:invalid", body: "no checksum"),
            Item("bad-version")
        });
        var r = Assert.Single(Releases.Parse(json));
        Assert.Equal(other, r.ZipSha256);
    }

    [Fact]
    public async Task AnonymousCacheCannotExposeDraftsAndForksHaveSeparateCaches()
    {
        using var http = new HttpClient(new Handler(_ => Json(Fixture())));
        await Releases.List(http, Token, tmp, "example/game");
        await Assert.ThrowsAsync<UserError>(() => Releases.List(http, null, tmp, "example/game", refresh: false));
        var publicList = await Releases.List(http, null, tmp, "example/game");
        Assert.DoesNotContain(publicList, r => r.Draft);
        var cached = await Releases.List(http, null, tmp, "example/game", refresh: false);
        Assert.Equal(publicList, cached);
        await Assert.ThrowsAsync<UserError>(() => Releases.List(http, Token, tmp, "example/fork", refresh: false));
    }

    [Fact]
    public async Task ListReadsMoreThanOnePage()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Assert.Contains("page=" + calls, request.RequestUri!.Query);
            return Json(calls == 1 ? JsonSerializer.Serialize(Enumerable.Repeat(Item("0.69.0"), 100))
                                  : JsonSerializer.Serialize(new[] { Item("0.69.1-dev.fea22b0", draft: true) }));
        }));
        var list = await Releases.List(http, Token, tmp, "example/game");
        Assert.Equal(2, calls);
        Assert.Contains(list, r => r.Draft);
    }

    [Fact]
    public async Task DraftDownloadUsesApiWithTokenAndChecksTheBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("fake release zip");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        int calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Assert.Equal("https://api.github.com/repos/example/game/releases/assets/42", request.RequestUri!.AbsoluteUri);
            Assert.Equal(Token, request.Headers.Authorization?.Parameter);
            Assert.Equal("application/octet-stream", Assert.Single(request.Headers.Accept).MediaType);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var log = new List<string>();
        var file = await Releases.Download(http, "https://api.github.com/repos/example/game/releases/assets/42", sha, log.Add, tmp, Token);
        Assert.Equal(bytes, File.ReadAllBytes(file));
        await Releases.Download(http, "https://api.github.com/repos/example/game/releases/assets/42", sha, log.Add, tmp, Token);
        Assert.Equal(1, calls);
        var mismatch = await Assert.ThrowsAsync<UserError>(() => Releases.Download(http, "https://api.github.com/repos/example/game/releases/assets/42", Sha, log.Add, tmp, Token));
        Assert.Contains("not used", mismatch.Message);
        Assert.False(File.Exists(Path.Combine(tmp, "downloads", Sha + ".zip")));
        Assert.Empty(Directory.GetFiles(Path.Combine(tmp, "downloads"), "*.part"));
        Assert.All(log, line => Assert.DoesNotContain(Token, line));
    }

    [Fact]
    public async Task FailuresNeverExposeTokenOrUnderlyingException()
    {
        var log = new List<string>();
        using var http = new HttpClient(new Handler(_ => throw new HttpRequestException("reflected " + Token)));
        var listError = await Assert.ThrowsAsync<UserError>(() => Releases.List(http, Token, tmp, "example/game", log: log.Add));
        var downloadError = await Assert.ThrowsAsync<UserError>(() => Releases.Download(http, "https://api.github.com/repos/example/game/releases/assets/42", Sha, log.Add, tmp, Token));
        foreach (var error in new[] { listError, downloadError })
        {
            Assert.DoesNotContain(Token, error.ToString());
            Assert.Null(error.InnerException);
        }
        Assert.All(log, line => Assert.DoesNotContain(Token, line));
        Assert.Empty(Directory.GetFiles(tmp, "*", SearchOption.AllDirectories));
    }
}

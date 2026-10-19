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

    static object Item(string version, bool draft = false, bool prerelease = false, string? digest = null, string? body = null, string? date = "2026-10-18T12:00:00Z", long id = 1) => new
    {
        id, tag_name = "v" + version, draft, prerelease, published_at = draft ? null : date, created_at = date,
        body = body ?? $"SHA-256 of TLD-RevaMP-{version}.zip: `{Sha}`\n",
        assets = new[] { new { name = $"TLD-RevaMP-{version}.zip", digest,
            url = "https://api.github.com/repos/example/game/releases/assets/42",
            browser_download_url = $"https://github.com/example/game/releases/download/v{version}/TLD-RevaMP-{version}.zip" } }
    };

    static string Fixture() => JsonSerializer.Serialize(new[]
    {
        Item("0.9.0", digest: "sha256:" + Sha),
        Item("0.69.1-dev.fea22b0", draft: true, prerelease: true, date: "2026-10-18T16:00:00Z", id: 2),
        Item("0.69.1-dev.abc1234", prerelease: true, date: "2026-10-18T14:00:00Z", id: 3),
        Item("0.10.0", digest: "sha256:" + Sha, id: 4),
        Item("0.70.0", body: "no checksum", id: 5)
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
            if (request.RequestUri.AbsolutePath == "/graphql")
            {
                Assert.True(signedIn);
                return Json("""{"data":{"repository":{"releases":{"nodes":[]}}}}""");
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/repos/example/game/releases", request.RequestUri.AbsolutePath);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GraphQLFindsDraftsMissingFromRestAndDeduplicatesReleaseIds(bool restHasDraft)
    {
        var log = new List<string>();
        var fetched = new List<long>();
        int queries = 0;
        var first = Item("0.69.1-dev.first", draft: true, prerelease: true, digest: "sha256:" + Sha,
                         body: Token, date: "2026-10-18T16:00:00Z", id: 2);
        var second = Item("0.69.1-dev.second", draft: true, prerelease: true,
                          body: Token + $" SHA-256 of TLD-RevaMP-0.69.1-dev.second.zip: `{Sha}`",
                          date: "2026-10-18T14:00:00Z", id: 3);
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Token, request.Headers.Authorization?.Parameter);
            Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);
            if (request.RequestUri.AbsolutePath == "/graphql")
            {
                queries++;
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.DoesNotContain(Token, body);
                using var doc = JsonDocument.Parse(body);
                Assert.Equal("example", doc.RootElement.GetProperty("variables").GetProperty("owner").GetString());
                Assert.Equal("game", doc.RootElement.GetProperty("variables").GetProperty("name").GetString());
                var query = doc.RootElement.GetProperty("query").GetString()!;
                Assert.Contains("first:50", query);
                Assert.Contains("orderBy:{field:CREATED_AT,direction:DESC}", query);
                Assert.Contains("isDraft databaseId", query);
                return Json("""{"data":{"repository":{"releases":{"nodes":[{"isDraft":true,"databaseId":2},{"isDraft":true,"databaseId":3},{"isDraft":true,"databaseId":2},{"isDraft":false,"databaseId":1}]}}}}""");
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            if (request.RequestUri.AbsolutePath == "/repos/example/game/releases")
                return Json(JsonSerializer.Serialize(restHasDraft ? new[] { Item("0.69.0"), first, Item("0.69.0") }
                                                                 : new[] { Item("0.69.0"), Item("0.69.0") }));
            var id = long.Parse(request.RequestUri.Segments[^1]);
            fetched.Add(id);
            return Json(JsonSerializer.Serialize(id == 2 ? first : second));
        }));
        var list = await Releases.List(http, Token, tmp, "example/game", log: log.Add);
        Assert.Equal(1, queries);
        Assert.Equal(restHasDraft ? new long[] { 3 } : new long[] { 2, 3 }, fetched);
        Assert.Equal(new[] { "0.69.1-dev.first", "0.69.1-dev.second", "0.69.0" }, list.Select(r => r.Version));
        Assert.All(list.Take(2), r =>
        {
            Assert.True(r.Draft);
            Assert.True(r.Prerelease);
            Assert.Equal(Sha, r.ZipSha256);
            Assert.Equal("https://api.github.com/repos/example/game/releases/assets/42", r.ZipUrl);
        });
        Assert.Empty(log);
        var cached = await Releases.List(http, Token, tmp, "example/game", refresh: false);
        Assert.Equal(list, cached);
        Assert.All(Directory.GetFiles(tmp), f => Assert.DoesNotContain(Token, File.ReadAllText(f)));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("errors")]
    [InlineData("json")]
    [InlineData("repository")]
    [InlineData("draft")]
    public async Task DraftLookupFailureKeepsPublishedReleasesAndLogsOnce(string failure)
    {
        var log = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/repos/example/game/releases")
                return Json(JsonSerializer.Serialize(new[] { Item("0.69.0") }));
            if (failure == "http" || request.RequestUri.AbsolutePath != "/graphql")
                return new(HttpStatusCode.Forbidden) { Content = new StringContent(Token) };
            if (failure == "errors") return Json(JsonSerializer.Serialize(new { errors = new[] { new { message = Token } } }));
            if (failure == "json") return Json("invalid " + Token);
            if (failure == "repository") return Json("""{"data":{"repository":null}}""");
            return Json("""{"data":{"repository":{"releases":{"nodes":[{"isDraft":true,"databaseId":2}]}}}}""");
        }));
        var list = await Releases.List(http, Token, tmp, "example/game", log: log.Add);
        Assert.Equal("0.69.0", Assert.Single(list).Version);
        Assert.False(list[0].Draft);
        Assert.Equal("Drafts couldn't be listed from GitHub (check the connection and GitHub sign-in).", Assert.Single(log));
        Assert.All(Directory.GetFiles(tmp), f => Assert.DoesNotContain(Token, File.ReadAllText(f)));
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
        using var http = new HttpClient(new Handler(request => Json(request.RequestUri!.AbsolutePath == "/graphql"
            ? """{"data":{"repository":{"releases":{"nodes":[]}}}}""" : Fixture())));
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
            if (request.RequestUri!.AbsolutePath == "/graphql") return Json("""{"data":{"repository":{"releases":{"nodes":[]}}}}""");
            calls++;
            Assert.Contains("page=" + calls, request.RequestUri.Query);
            return Json(calls == 1 ? JsonSerializer.Serialize(Enumerable.Repeat(Item("0.69.0"), 100))
                                  : JsonSerializer.Serialize(new[] { Item("0.69.1-dev.fea22b0", draft: true, id: 2) }));
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

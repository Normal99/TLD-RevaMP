using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TLDInstalls.Tests;

public sealed class BugReportTests
{
    const string Token = "test-credential-never-emit";

    static CopyInfo Copy(Kind kind = Kind.Release) => new()
    {
        Name = "release-0.69.1", Kind = kind, RevampVersion = kind == Kind.Vanilla ? null : "0.69.1-dev.6874798",
        RevampSource = "v0.69.1-dev.6874798", BepInEx = "5.4.23.5", GameBranch = "beta", GameBuildId = "16539689",
    };

    sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<(string Url, JsonElement Body)> Seen = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Token, request.Headers.Authorization?.Parameter);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Seen.Add((request.RequestUri!.ToString(), JsonDocument.Parse(body).RootElement.Clone()));
            return respond(request, body);
        }
    }

    static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };

    static HttpResponseMessage GitHub(HttpRequestMessage r, string body, bool scopes = true)
    {
        if (r.RequestUri!.AbsolutePath == "/repos/example/game/issues")
            return Json(new { number = 30, html_url = "https://github.com/example/game/issues/30", node_id = "I_30" }, HttpStatusCode.Created);
        if (!scopes) return Json(new { errors = new[] { new { type = "INSUFFICIENT_SCOPES", message = "no" } } });
        if (body.Contains("projectV2(number"))
            return Json(new { data = new { user = new { projectV2 = new { id = "PVT_1", field = new { id = "F_status", options = new[] {
                new { id = "o_review", name = "Needs Review" }, new { id = "o_triage", name = "To triage" } } } } } } });
        if (body.Contains("addProjectV2ItemById")) return Json(new { data = new { addProjectV2ItemById = new { item = new { id = "PVTI_1" } } } });
        if (body.Contains("updateProjectV2ItemFieldValue")) return Json(new { data = new { updateProjectV2ItemFieldValue = new { projectV2Item = new { id = "PVTI_1" } } } });
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SendsAnIssueWithTheVersionAndPutsItInTriage()
    {
        var handler = new Handler((r, b) => GitHub(r, b));
        var log = new List<string>();
        var url = await BugReports.Send(new HttpClient(handler), Token, "example/game", "someone/1", Copy(), "Bus doors not synced\r\nthey stay shut for the passenger", log.Add);

        Assert.Equal("https://github.com/example/game/issues/30", url);
        var issue = handler.Seen[0].Body;
        Assert.Equal("Bus doors not synced", issue.GetProperty("title").GetString());
        var text = issue.GetProperty("body").GetString()!;
        Assert.StartsWith("Bus doors not synced\nthey stay shut for the passenger\n", text);
        Assert.Contains("TLD RevaMP 0.69.1-dev.6874798 (release v0.69.1-dev.6874798)", text);
        Assert.Contains("BepInEx:** 5.4.23.5", text);
        Assert.Contains("beta branch, game build 16539689", text);

        Assert.Equal(4, handler.Seen.Count);
        Assert.Equal("someone", handler.Seen[1].Body.GetProperty("variables").GetProperty("login").GetString());
        Assert.Equal("I_30", handler.Seen[2].Body.GetProperty("variables").GetProperty("c").GetString());
        var status = handler.Seen[3].Body.GetProperty("variables");
        Assert.Equal("o_triage", status.GetProperty("o").GetString());
        Assert.Equal("F_status", status.GetProperty("f").GetString());
        Assert.Contains(log, l => l.Contains("in To triage"));
        Assert.All(log, l => Assert.DoesNotContain(Token, l));
    }

    [Fact]
    public async Task WithoutBoardAccessTheIssueIsStillSent()
    {
        var handler = new Handler((r, b) => GitHub(r, b, scopes: false));
        var log = new List<string>();
        var url = await BugReports.Send(new HttpClient(handler), Token, "example/game", "someone/1", Copy(), "TPA is broken", log.Add);
        Assert.Equal("https://github.com/example/game/issues/30", url);
        Assert.Contains(log, l => l.Contains("gh auth refresh -s project"));
    }

    [Fact]
    public async Task RefusesEmptyReportsCopiesWithoutRevampAndNoSignIn()
    {
        var http = new HttpClient(new Handler((r, b) => throw new InvalidOperationException("no request expected")));
        await Assert.ThrowsAsync<UserError>(() => BugReports.Send(http, Token, "example/game", "none", Copy(), "  ", _ => { }));
        await Assert.ThrowsAsync<UserError>(() => BugReports.Send(http, Token, "example/game", "none", Copy(Kind.Vanilla), "bug", _ => { }));
        var e = await Assert.ThrowsAsync<UserError>(() => BugReports.Send(http, null, "example/game", "none", Copy(), "bug", _ => { }));
        Assert.Equal(BugReports.SignInNotice, e.Message);
    }

    [Fact]
    public void TitleIsTheFirstLineCutShort()
    {
        Assert.Equal("Radio not synced", BugReports.Title("\n  Radio not synced  \nmore detail"));
        var t = BugReports.Title(new string('x', 200));
        Assert.Equal(90, t.Length);
        Assert.EndsWith("…", t);
    }
}

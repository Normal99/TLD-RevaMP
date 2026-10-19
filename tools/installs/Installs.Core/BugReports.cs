using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TLDInstalls;

/// <summary>A bug report from a copy: an issue in the releases' repository whose body says which RevaMP build, BepInEx
/// and game build the copy runs, then a card for it on the bug board in its first column (To triage).</summary>
public static class BugReports
{
    /// <summary>The bug board as OWNER/NUMBER (TLD_BUG_PROJECT to use another; "none" for issues only).</summary>
    public static string Project => Env.Get("TLD_BUG_PROJECT") ?? "Normal99/1";

    public const string SignInNotice = "Sending a bug report needs a GitHub sign-in (gh auth login; gh auth refresh -s project for the board).";

    static readonly HttpClient Http = MakeClient();

    static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TLD-copies", "1"));
        return h;
    }

    /// <summary>The issue's title: the report's first line, cut to 90 characters.</summary>
    public static string Title(string text)
    {
        var first = text.Trim().Split('\n')[0].Trim();
        return first.Length <= 90 ? first : first[..89].TrimEnd() + "…";
    }

    public static string Body(CopyInfo c, string text)
    {
        var src = c.Kind switch
        {
            Kind.Release => $"release {c.RevampSource}",
            Kind.Dev => $"dev build of commit {c.RevampSource}",
            Kind.Test => $"test build of commit {c.RevampSource}",
            _ => c.KindLabel,
        };
        return text.Trim().Replace("\r\n", "\n") + "\n\n---\n" +
               $"**Version:** TLD RevaMP {c.RevampVersion ?? "none"} ({src})  \n" +
               $"**BepInEx:** {c.BepInEx ?? "?"}  \n" +
               $"**Game:** {c.GameLabel}  \n" +
               $"_Sent from TLD Copies (copy `{c.Name}`)._\n";
    }

    /// <summary>Sends the report; returns the new issue's URL.</summary>
    public static async Task<string> Send(CopyInfo c, string text, Action<string> log)
        => await Send(Http, await GitHubAuth.Token(), Releases.Repo, Project, c, text, log);

    internal static async Task<string> Send(HttpClient http, string? token, string repo, string project, CopyInfo c, string text, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new UserError("the bug report is empty");
        if (!c.HasRevamp) throw new UserError($"'{c.Name}' has no TLD RevaMP: bug reports are for RevaMP copies");
        if (token == null) throw new UserError(SignInNotice);

        int number; string url, nodeId;
        try
        {
            using var request = Request(HttpMethod.Post, $"https://api.github.com/repos/{repo}/issues", token,
                                        new { title = Title(text), body = Body(c, text) });
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                throw new UserError($"GitHub refused the issue ({(int)response.StatusCode} {response.ReasonPhrase}): " +
                                    (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                                        ? $"the GitHub sign-in can't open issues in {repo}" : "try again"));
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            number = doc.RootElement.GetProperty("number").GetInt32();
            url = doc.RootElement.GetProperty("html_url").GetString()!;
            nodeId = doc.RootElement.GetProperty("node_id").GetString()!;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new UserError("couldn't send the bug report to GitHub (check the connection and GitHub sign-in)"); }
        log($"  issue #{number}: {url}");

        if (project == "none") return url;
        try
        {
            var column = await AddToBoard(http, token, project, nodeId);
            log($"  on the board ({project}) in {column}");
        }
        catch (Exception e) when (e is UserError or HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // the issue is there either way; only the card is missing
            log($"  ✗ not put on the board ({project}): {(e is UserError ? e.Message : "GitHub didn't answer")}. The issue is still sent.");
        }
        return url;
    }

    /// <summary>Adds the issue to the project and puts it in the Status column that says "triage" (else the first one).
    /// Returns that column's name.</summary>
    static async Task<string> AddToBoard(HttpClient http, string token, string project, string issueId)
    {
        var m = Regex.Match(project, @"^([A-Za-z0-9-]+)/(\d+)$");
        if (!m.Success) throw new UserError("TLD_BUG_PROJECT must be OWNER/NUMBER");
        var board = await GraphQL(http, token,
            """
            query($login:String!,$n:Int!){user(login:$login){projectV2(number:$n){id field(name:"Status"){... on ProjectV2SingleSelectField{id options{id name}}}}}}
            """, new { login = m.Groups[1].Value, n = int.Parse(m.Groups[2].Value) });
        var p = board.GetProperty("user").GetProperty("projectV2");
        var projectId = p.GetProperty("id").GetString()!;
        var item = await GraphQL(http, token,
            "mutation($p:ID!,$c:ID!){addProjectV2ItemById(input:{projectId:$p,contentId:$c}){item{id}}}",
            new { p = projectId, c = issueId });
        var itemId = item.GetProperty("addProjectV2ItemById").GetProperty("item").GetProperty("id").GetString()!;

        if (!p.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.Object || !field.TryGetProperty("options", out var options))
            return "no Status column";
        var opts = options.EnumerateArray().Select(o => (Id: o.GetProperty("id").GetString()!, Name: o.GetProperty("name").GetString()!)).ToList();
        if (opts.Count == 0) return "no Status column";
        var col = opts.FirstOrDefault(o => o.Name.Contains("triage", StringComparison.OrdinalIgnoreCase));
        if (col.Id == null) col = opts[0];
        await GraphQL(http, token,
            "mutation($p:ID!,$i:ID!,$f:ID!,$o:String!){updateProjectV2ItemFieldValue(input:{projectId:$p,itemId:$i,fieldId:$f,value:{singleSelectOptionId:$o}}){projectV2Item{id}}}",
            new { p = projectId, i = itemId, f = field.GetProperty("id").GetString(), o = col.Id });
        return col.Name;
    }

    static async Task<JsonElement> GraphQL(HttpClient http, string token, string query, object variables)
    {
        using var request = Request(HttpMethod.Post, "https://api.github.com/graphql", token, new { query, variables });
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new UserError($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            var msg = errors[0].TryGetProperty("message", out var mm) ? mm.GetString() : "error";
            var scopes = errors[0].TryGetProperty("type", out var t) && t.GetString() == "INSUFFICIENT_SCOPES";
            throw new UserError(scopes ? "the GitHub sign-in has no project access (gh auth refresh -s project)" : msg ?? "error");
        }
        return doc.RootElement.GetProperty("data").Clone();
    }

    static HttpRequestMessage Request(HttpMethod method, string url, string token, object json)
    {
        var request = new HttpRequestMessage(method, url) { Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}

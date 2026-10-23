using System.Text;
using System.Text.RegularExpressions;

namespace TLDInstalls;

/// <summary>A copy's launch options, one line written like Steam's: `PROTON_LOG=1 gamemoderun %command% -nolog`.
/// NAME=VALUE words at the start are environment variables; with %command%, the other words before it wrap the game
/// (Linux only: gamemoderun, mangohud, …) and the words after it are the game's arguments; without %command% the whole
/// line (after the variables) is the game's arguments. Words split on spaces; '…' and "…" keep spaces, and a backslash
/// escapes a space or a quote (other backslashes stay, for Windows paths). There is no $VAR or ~ expansion.</summary>
public sealed record LaunchOptions(IReadOnlyList<KeyValuePair<string, string>> Variables, IReadOnlyList<string> Wrapper, IReadOnlyList<string> Args)
{
    public const string Command = "%command%";
    public static readonly LaunchOptions None = new([], [], []);

    public bool IsEmpty => Variables.Count == 0 && Wrapper.Count == 0 && Args.Count == 0;

    /// <summary>Variables the copy itself depends on (its prefix, its save folder, which game is which): not settable here.</summary>
    static readonly string[] Owned =
    [
        "STEAM_COMPAT_DATA_PATH", "STEAM_COMPAT_INSTALL_PATH", "STEAM_COMPAT_CLIENT_INSTALL_PATH", "WINEPREFIX", "TLD_COPY_LOG",
        "SteamAppId", "SteamGameId", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
    ];

    static readonly Regex Assignment = new(@"^([A-Za-z_][A-Za-z0-9_]*)=(.*)$", RegexOptions.Singleline);

    public static LaunchOptions Parse(string? line)
    {
        var words = Split(line ?? "");
        var at = words.FindIndex(w => w.Text == Command && !w.Quoted);
        if (at >= 0 && words.FindLastIndex(w => w.Text == Command && !w.Quoted) != at)
            throw new UserError($"launch options: {Command} appears more than once");
        var head = at >= 0 ? words.Take(at).ToList() : words;
        var env = new List<KeyValuePair<string, string>>();
        int i = 0;
        for (; i < head.Count && Assignment.Match(head[i].Text) is { Success: true } m; i++)
        {
            var name = m.Groups[1].Value;
            if (Owned.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new UserError($"launch options: {name} is set by TLD Copies for this copy and can't be changed");
            env.RemoveAll(kv => kv.Key == name);
            env.Add(new(name, m.Groups[2].Value));
        }
        var rest = head.Skip(i).Select(w => w.Text).ToList();
        return at >= 0
            ? new(env, rest, words.Skip(at + 1).Select(w => w.Text).ToList())
            : new(env, [], rest);
    }

    readonly record struct Word(string Text, bool Quoted);

    static List<Word> Split(string line)
    {
        var words = new List<Word>();
        var sb = new StringBuilder();
        bool inWord = false, quoted = false;
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else if (ch == '\\' && quote == '"' && i + 1 < line.Length && line[i + 1] is '"' or '\\') sb.Append(line[++i]);
                else sb.Append(ch);
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (inWord) words.Add(new(sb.ToString(), quoted));
                sb.Clear(); inWord = quoted = false;
            }
            else if (ch is '"' or '\'') { quote = ch; inWord = quoted = true; }
            else if (ch == '\\' && i + 1 < line.Length && (char.IsWhiteSpace(line[i + 1]) || line[i + 1] is '"' or '\''))
            { sb.Append(line[++i]); inWord = true; }
            else { sb.Append(ch); inWord = true; }
        }
        if (quote != '\0') throw new UserError($"launch options: a {quote} quote isn't closed");
        if (inWord) words.Add(new(sb.ToString(), quoted));
        return words;
    }

    /// <summary>Words back into one line that parses to the same words (for `tld-installs options NAME -- WORDS…`).</summary>
    public static string Join(IEnumerable<string> words) => string.Join(" ", words.Select(Quote));

    static string Quote(string w) =>
        w.Length > 0 && !w.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '\'') ? w
        : !w.Contains('\'') ? $"'{w}'"
        : "\"" + w.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>The game's environment with these variables on top. A modded copy keeps BepInEx's winhttp override when
    /// WINEDLLOVERRIDES is set here (Linux), or the mod wouldn't load.</summary>
    public void ApplyEnv(IDictionary<string, string?> env, bool keepDoorstop)
    {
        foreach (var (k, v) in Variables)
            env[k] = k == "WINEDLLOVERRIDES" && keepDoorstop && !Regex.IsMatch(v, @"(^|[;,])winhttp([=,;]|$)", RegexOptions.IgnoreCase)
                ? (v.Length > 0 ? v + ";" : "") + "winhttp=n,b" : v;
    }

    /// <summary>Linux: the wrapper's program, checked before the launch (a missing one would fail silently in the background).</summary>
    public void CheckWrapper()
    {
        if (Wrapper.Count == 0) return;
        if (Env.Windows) throw new UserError($"launch options: a wrapper around {Command} ({Wrapper[0]}) works on Linux only; on Windows use game arguments and variables");
        if (Which(Wrapper[0]) == null) throw new UserError($"launch options: {Wrapper[0]} isn't installed (not found on PATH)");
    }

    internal static string? Which(string program)
    {
        if (program.Contains('/')) return File.Exists(program) ? program : null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (Path.Combine(dir, program) is var p && File.Exists(p)) return p;
        return null;
    }

    /// <summary>What the line does, in words (the window's preview and `tld-installs options`).</summary>
    public string Describe()
    {
        if (IsEmpty) return "no launch options";
        var parts = new List<string>();
        if (Variables.Count > 0) parts.Add("variables " + string.Join(" ", Variables.Select(kv => Quote(kv.Key + "=" + kv.Value))));
        if (Wrapper.Count > 0) parts.Add("wrapped in " + Join(Wrapper) + (Env.Windows ? " (Linux only: refused here)" : ""));
        if (Args.Count > 0) parts.Add("game arguments " + Join(Args));
        return string.Join("; ", parts);
    }
}

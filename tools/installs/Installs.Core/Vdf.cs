using System.Text;

namespace TLDInstalls;

/// <summary>Steam's text KeyValues files (libraryfolders.vdf, appmanifest_*.acf, config.vdf): quoted keys, each followed
/// by a quoted value or a { … } block. Keys are matched without regard to case, as Steam does.</summary>
public sealed class Vdf
{
    public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, Vdf> Blocks = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The blocks in file order (libraryfolders.vdf's "0", "1", …).</summary>
    public readonly List<(string Key, Vdf Block)> Order = new();

    public string? this[string key] => Values.TryGetValue(key, out var v) ? v : null;
    public Vdf? Block(string key) => Blocks.TryGetValue(key, out var b) ? b : null;

    public static Vdf Parse(string text)
    {
        int i = 0;
        var root = new Vdf();
        ParseInto(root, text, ref i, top: true);
        return root;
    }

    public static Vdf? Load(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    static void ParseInto(Vdf into, string t, ref int i, bool top)
    {
        while (true)
        {
            var key = Token(t, ref i);
            if (key == null) return;                       // end of the file
            if (key == "}") { if (top) continue; return; }
            var next = Token(t, ref i);
            if (next == null) return;
            if (next == "{")
            {
                var b = new Vdf();
                ParseInto(b, t, ref i, top: false);
                into.Blocks[key] = b; into.Order.Add((key, b));
            }
            else into.Values[key] = next;
        }
    }

    /// <summary>The next token: a quoted string (\\ \" \n \t unescaped), "{" or "}", a bare word; null at the end.
    /// // comments and [$WIN32]-style conditions are skipped.</summary>
    static string? Token(string t, ref int i)
    {
        while (i < t.Length)
        {
            char c = t[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; continue; }
            if (c == '[') { while (i < t.Length && t[i] != ']') i++; i++; continue; }
            if (c == '{' || c == '}') { i++; return c.ToString(); }
            var sb = new StringBuilder();
            if (c == '"')
            {
                i++;
                while (i < t.Length && t[i] != '"')
                {
                    if (t[i] == '\\' && i + 1 < t.Length)
                    {
                        i++;
                        sb.Append(t[i] switch { 'n' => '\n', 't' => '\t', _ => t[i] });
                    }
                    else sb.Append(t[i]);
                    i++;
                }
                i++;
                return sb.ToString();
            }
            while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] != '{' && t[i] != '}' && t[i] != '"') sb.Append(t[i++]);
            return sb.ToString();
        }
        return null;
    }
}

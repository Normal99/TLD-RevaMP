using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TvBrowser
{
    /// The little JSON the browser connection needs (the DevTools protocol is JSON): objects are
    /// Dictionary<string, object>, arrays List<object>, numbers double, plus string, bool and null. Parse never throws:
    /// anything broken is null.
    public static class Json
    {
        public static object Parse(string s)
        {
            if (s == null) return null;
            try
            {
                int i = 0;
                var v = Value(s, ref i, 0);
                Ws(s, ref i);
                return i == s.Length ? v : null;
            }
            catch (FormatException) { return null; }
            catch (ArgumentOutOfRangeException) { return null; }
            catch (IndexOutOfRangeException) { return null; }
            catch (OverflowException) { return null; }
        }

        private const int MaxDepth = 64;

        private static object Value(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("too deep");
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("end");
            char c = s[i];
            if (c == '{')
            {
                var o = new Dictionary<string, object>();
                i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return o; }
                while (true)
                {
                    Ws(s, ref i);
                    if (s[i] != '"') throw new FormatException("key");
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    if (s[i++] != ':') throw new FormatException(":");
                    o[k] = Value(s, ref i, depth + 1);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return o; }
                    throw new FormatException("object");
                }
            }
            if (c == '[')
            {
                var a = new List<object>();
                i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Value(s, ref i, depth + 1));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return a; }
                    throw new FormatException("array");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            if (s[i] == '-') i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
            if (i == start) throw new FormatException("value");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string Str(string s, ref int i)
        {
            i++;   // the opening quote
            StringBuilder sb = null;
            int run = i;
            while (true)
            {
                char c = s[i];
                if (c == '"')
                {
                    string tail = s.Substring(run, i - run);
                    i++;
                    return sb == null ? tail : sb.Append(tail).ToString();
                }
                if (c != '\\') { i++; continue; }
                if (sb == null) sb = new StringBuilder();
                sb.Append(s, run, i - run);
                char e = s[i + 1];
                i += 2;
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 4; break;
                    default: throw new FormatException("escape");
                }
                run = i;
            }
        }

        private static void Ws(string s, ref int i) { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++; }

        public static string Write(object v) { var sb = new StringBuilder(); Write(sb, v); return sb.ToString(); }

        public static void Write(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: Quote(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); return;
                case long n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); return;
                case uint n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); return;
                case float f: sb.Append(Finite(f).ToString("R", CultureInfo.InvariantCulture)); return;
                case double d: sb.Append(Finite(d).ToString("R", CultureInfo.InvariantCulture)); return;
                case IDictionary<string, object> o:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in o) { if (!first) sb.Append(','); first = false; Quote(sb, kv.Key); sb.Append(':'); Write(sb, kv.Value); }
                    sb.Append('}');
                    return;
                }
                case System.Collections.IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var x in list) { if (!first) sb.Append(','); first = false; Write(sb, x); }
                    sb.Append(']');
                    return;
                }
                default: Quote(sb, v.ToString()); return;
            }
        }

        private static double Finite(double d) => double.IsNaN(d) || double.IsInfinity(d) ? 0 : d;

        private static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ' || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static string Str(Dictionary<string, object> o, string key) => o != null && o.TryGetValue(key, out var v) ? v as string : null;
        public static double Num(Dictionary<string, object> o, string key, double fallback = 0) => o != null && o.TryGetValue(key, out var v) && v is double d ? d : fallback;
        public static int Int(Dictionary<string, object> o, string key, int fallback = 0) => o != null && o.TryGetValue(key, out var v) && v is double d && d >= int.MinValue && d <= int.MaxValue ? (int)d : fallback;
        public static Dictionary<string, object> Obj(Dictionary<string, object> o, string key) => o != null && o.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        /// An object from key/value pairs: Json.O("url", u, "transitionType", "typed").
        public static Dictionary<string, object> O(params object[] kv)
        {
            var o = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) o[(string)kv[i]] = kv[i + 1];
            return o;
        }
    }
}

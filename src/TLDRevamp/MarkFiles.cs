using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace TLDRevamp
{
    // All disk operations, including index recovery and comparisons, share one worker queue.
    internal static class MarkFiles
    {
        internal sealed class Entry
        {
            internal string Sid, Date, Role, Note, File, Folder;
            internal double St;
            internal int Origin, Source;
            internal bool Received;
            internal string Key => Identity(Sid, St, Origin);
        }
        private static readonly object Gate = new object();
        private static Task _tail = Task.CompletedTask;
        private static Entry[] _snapshot = Array.Empty<Entry>();
        private static readonly Dictionary<string, string> Renames = new Dictionary<string, string>();
        private static readonly HashSet<string> Comparisons = new HashSet<string>();
        private static readonly List<Entry> Entries = new List<Entry>();
        private static string _root;
        internal static string Error { get; private set; }
        internal static Entry[] Snapshot => System.Threading.Volatile.Read(ref _snapshot);
        internal static string Identity(string sid, double st, int origin) => sid + ":" + st.ToString("F3", CultureInfo.InvariantCulture) + ":" + origin;
        internal static bool Date(string value) => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        internal static string Folder(string root, string date, string sid)
        {
            if (!TelemetryPrivacy.SessionId(sid) || !Date(date)) throw new InvalidDataException("mark session");
            return Path.Combine(root, "marks", date + "-" + sid);
        }
        internal static string Name(string folder, string sid, double st, string note, int source = -1, string hash = null)
        {
            string suffix = source < 0 ? ".json" : "-from-p" + source.ToString(CultureInfo.InvariantCulture) + "-" + hash.Substring(0, 8) + ".json";
            string basis = Net.ReportTransfer.MomentBase(sid, st);
            // Leave room for .tmp and a collision suffix; every final and temporary path stays below 240.
            int budget = Math.Min(240 - Path.GetFullPath(folder).Length - 1, 240) - basis.Length - 1 - suffix.Length - 12;
            if (budget < 7) throw new PathTooLongException("mark folder leaves no filename budget");
            return Net.ReportTransfer.MomentName(sid, st, note, budget) + suffix;
        }
        internal static Task Queue(Action action)
        {
            lock (Gate) return _tail = _tail.ContinueWith(_ =>
            {
                try { action(); }
                catch (Exception e) { Error = "Mark file work failed: " + e.GetType().Name; }
            }, System.Threading.CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        internal static Task Pending { get { lock (Gate) return _tail; } }
        internal static Task<T> Run<T>(Func<T> action)
        {
            lock (Gate)
            {
                var result = _tail.ContinueWith(_ => action(), System.Threading.CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                _tail = result.ContinueWith(t => { if (t.IsFaulted) Error = "Mark file work failed: " + t.Exception.GetBaseException().GetType().Name; }, TaskScheduler.Default);
                return result;
            }
        }
        private static void Load(string root)
        {
            if (_root == root) return;
            _root = root; Entries.Clear(); Renames.Clear(); Comparisons.Clear();
            string marks = Path.Combine(root, "marks");
            if (Directory.Exists(marks)) foreach (string path in Directory.GetFiles(marks, "mark-*.json", SearchOption.AllDirectories))
                try { Entries.Add(Read(root, path)); } catch { }
            Publish();
        }
        internal static void Initialize(string root) => Queue(() => { Load(root); Refresh(Array.Empty<string>()); });
        internal static string LocalPath(string root, string sid, double st, int origin)
        {
            Load(root); string key = Identity(sid, st, origin);
            var entry = Entries.LastOrDefault(e => e.Key == key && !e.Received);
            return entry == null ? null : PathOf(entry);
        }
        internal static void Failed(Exception error) => Error = "Mark file work failed: " + error.GetType().Name;
        private static Entry Read(string root, string path, int source = -1, bool received = false)
        {
            var head = MarkJson.Object(MarkJson.Object(MarkJson.Box(File.ReadAllText(path)))["head"]);
            string sid = MarkJson.Text(head, "sid");
            if (!TelemetryPrivacy.SessionId(sid)) sid = "0000000000000000";
            string date = MarkJson.Text(head, "date");
            if (!Date(date)) date = MarkJson.Text(head, "start");
            if (!Date(date)) date = "1970-01-01";
            string name = Path.GetFileName(path);
            var match = System.Text.RegularExpressions.Regex.Match(name, @"-from-p(\d+)-[0-9a-f]{8}(?:-\d+)?\.json$");
            if (match.Success) { received = true; source = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); }
            string role = MarkJson.Text(head, "role");
            return new Entry { Sid = sid, Date = date, Role = role == "solo" || role == "client" || role == "dedicated" ? role : "host",
                St = MarkJson.Number(head, "momentSt"), Origin = (int)MarkJson.Number(head, "origin"), Source = source,
                Note = TelemetryPrivacy.Note(MarkJson.Text(head, "note")), Received = received,
                Folder = Path.GetDirectoryName(path).Substring(Path.GetFullPath(root).Length + 1).Replace('\\', '/'), File = name };
        }
        private static string PathOf(Entry entry) => Path.Combine(_root, entry.Folder.Replace('/', Path.DirectorySeparatorChar), entry.File);
        internal static string Register(string root, string path, int source = -1, bool received = false)
        {
            Load(root);
            var entry = Read(root, path, source, received);
            Entries.RemoveAll(e => PathOf(e) == path);
            Entries.Add(entry);
            if (Renames.TryGetValue(entry.Key, out string note)) RenameEntry(entry, note);
            Refresh(new[] { entry.Folder });
            return PathOf(entry);
        }
        internal static Task Rename(string root, string sid, double st, int origin, string note)
        {
            note = TelemetryPrivacy.Note(note);
            return Queue(() =>
            {
                Load(root); string key = Identity(sid, st, origin); Renames[key] = note;
                var matches = Entries.Where(e => e.Key == key).ToArray();
                foreach (var entry in matches) RenameEntry(entry, note);
                Refresh(matches.Select(e => e.Folder));
            });
        }
        private static void RenameEntry(Entry entry, string note)
        {
            string old = PathOf(entry);
            string box = MarkJson.RenameNote(File.ReadAllText(old), note);
            string folder = Path.GetDirectoryName(old);
            string hash = entry.Received ? System.Text.RegularExpressions.Regex.Match(entry.File, @"-([0-9a-f]{8})(?:-\d+)?\.json$").Groups[1].Value : null;
            string name = Name(folder, entry.Sid, entry.St, note, entry.Received ? entry.Source : -1, hash);
            string target = Path.Combine(folder, name);
            if (target != old) target = Unique(target);
            Atomic(target, box);
            if (old != target) File.Delete(old);
            entry.Note = TelemetryPrivacy.Note(note); entry.File = Path.GetFileName(target);
        }
        internal static string Unique(string path)
        {
            string stem = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path)); int n = 1;
            while (File.Exists(path)) path = stem + "-" + n++.ToString(CultureInfo.InvariantCulture) + ".json";
            return path;
        }
        internal static void Atomic(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path + ".tmp", text.Replace("\r\n", "\n").Replace('\r', '\n'), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
            else File.Move(path + ".tmp", path);
        }
        private static void Publish() => System.Threading.Volatile.Write(ref _snapshot, Entries.Select(e => new Entry { Sid = e.Sid, Date = e.Date, Role = e.Role, Note = e.Note, File = e.File, Folder = e.Folder, St = e.St, Origin = e.Origin, Source = e.Source, Received = e.Received }).ToArray());
        private static void Refresh(IEnumerable<string> folders)
        {
            Entries.RemoveAll(e => !File.Exists(PathOf(e)));
            var rows = new List<object>(); var text = new StringBuilder();
            foreach (var e in Entries.OrderBy(e => e.Date).ThenBy(e => e.St).ThenBy(e => e.Source))
            {
                rows.Add(new Dictionary<string, object> { ["date"] = e.Date, ["st"] = e.St, ["sid"] = e.Sid, ["role"] = e.Role,
                    ["origin"] = e.Origin, ["source"] = e.Source, ["note"] = e.Note, ["file"] = (e.Folder == "marks" ? "" : e.Folder.Substring("marks/".Length) + "/") + e.File, ["received"] = e.Received });
                text.Append(e.Date).Append(" UTC + ").Append(e.St.ToString("F3", CultureInfo.InvariantCulture)).Append(" s | ").Append(e.Sid).Append(" | ").Append(e.Role)
                    .Append(e.Received ? " received player-" + e.Source : " local").Append(" | ").Append(e.Folder).Append('/').Append(e.File).Append('\n').Append(e.Note).Append("\n\n");
            }
            Atomic(Path.Combine(_root, "marks", "index.json"), MarkJson.Write(rows) + "\n");
            Atomic(Path.Combine(_root, "marks", "index.txt"), text.ToString());
            foreach (string folder in folders.Distinct())
                if (Entries.Any(e => e.Folder == folder && Comparisons.Contains(e.Sid)) || File.Exists(Path.Combine(_root, folder, "comparison.html"))) Summary(folder);
            Publish();
        }
        internal static void Compare(string root, string sid) => Queue(() =>
        {
            Load(root); Comparisons.Add(sid);
            foreach (string folder in Entries.Where(e => e.Sid == sid).Select(e => e.Folder).Distinct()) Summary(folder);
        });
        private static void Summary(string folder)
        {
            var entries = Entries.Where(e => e.Folder == folder).OrderBy(e => e.St).ThenBy(e => e.Source).ToArray();
            var html = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>Mark comparison</title><style>body{font:16px sans-serif;background:#17202b;color:#eee;margin:2em}table{border-collapse:collapse;width:100%}th,td{border:1px solid #657080;padding:.5em;text-align:left;vertical-align:top}pre{white-space:pre-wrap;overflow-wrap:anywhere}a{color:#8cd}.diff{background:#694319}</style><h1>Session marks</h1><p>Compact comparison. Missing state is unknown; snapshots may arrive at different times. The full timelines and coverage analysis are available with tools/telemetry_compare.py.</p>");
            foreach (var group in entries.GroupBy(e => e.Key))
            {
                var boxes = group.Select(e => (Entry: e, Box: MarkJson.Object(MarkJson.Box(File.ReadAllText(PathOf(e)), true)))).ToArray();
                html.Append("<h2>").Append(WebUtility.HtmlEncode(group.First().Date + " UTC + " + group.First().St.ToString("F3", CultureInfo.InvariantCulture) + " s: " + group.First().Note)).Append("</h2><table><tr><th>Focus / state</th>");
                foreach (var b in boxes) html.Append("<th>").Append(WebUtility.HtmlEncode(b.Entry.Role + (b.Entry.Received ? " player-" + b.Entry.Source : " local"))).Append(" <a href=\"").Append(WebUtility.HtmlEncode(Uri.EscapeDataString(b.Entry.File))).Append("\">JSON</a></th>");
                html.Append("</tr>");
                var focusNets = new HashSet<double>();
                var focusPlayers = new HashSet<double>();
                foreach (string key in new[] { "crosshair", "car", "seat", "held", "dragging", "closestPlayer" })
                {
                    var values = boxes.Select(b => MarkJson.Get(MarkJson.Get(b.Box, "head"), "focus")).Select(f => MarkJson.Get(f, key)).ToArray();
                    foreach (var v in values)
                    {
                        double net = MarkJson.Number(MarkJson.Object(v), "net"); if (net > 0) focusNets.Add(net);
                        if (key == "closestPlayer" && MarkJson.Object(v).ContainsKey("id")) focusPlayers.Add(MarkJson.Number(MarkJson.Object(v), "id"));
                    }
                    HtmlRow(html, key, values.Select(v => MarkJson.Write(SafeValue(v, key))).ToArray());
                }
                foreach (double net in focusNets)
                {
                    foreach (string key in new[] { "entities", "slots", "seats", "usables" })
                    {
                        var values = boxes.Select(b => MarkJson.Get(MarkJson.Get(b.Box, "state"), key) as List<object>)
                            .Select(list => list == null ? "unknown" : MarkJson.Write(SafeValue(list.Where(v => MarkJson.Number(MarkJson.Object(v), "net") == net).ToList(), key))).ToArray();
                        HtmlRow(html, "net " + net + " " + key, values);
                    }
                }
                foreach (double player in focusPlayers)
                {
                    var values = boxes.Select(b => MarkJson.Get(MarkJson.Get(b.Box, "state"), "players") as List<object>)
                        .Select(list => list == null ? "unknown" : MarkJson.Write(SafeValue(list.Where(v => MarkJson.Number(MarkJson.Object(v), "id") == player).ToList(), "players"))).ToArray();
                    HtmlRow(html, "player " + player, values);
                }
                html.Append("</table>");
            }
            html.Append("</html>"); Atomic(Path.Combine(_root, folder.Replace('/', Path.DirectorySeparatorChar), "comparison.html"), html.ToString());
        }
        private static void HtmlRow(StringBuilder html, string label, string[] values)
        {
            html.Append(values.Distinct().Count() > 1 ? "<tr class=\"diff\">" : "<tr>").Append("<th>").Append(WebUtility.HtmlEncode(label)).Append("</th>");
            foreach (string value in values) html.Append("<td><pre>").Append(WebUtility.HtmlEncode(value)).Append("</pre></td>");
            html.Append("</tr>");
        }
        private static object SafeValue(object value, string field)
        {
            if (value is string text) return TelemetryPrivacy.ShareString(field, text) ? text : TelemetryPrivacy.Removed;
            if (value is Dictionary<string, object> map) return map.Where(p => TelemetryPrivacy.ShareKey(p.Key)).ToDictionary(p => p.Key, p => SafeValue(p.Value, p.Key));
            if (value is List<object> list) return list.Select(v => SafeValue(v, field)).ToList();
            return value;
        }
    }

    // Bounded reader for already validated telemetry and our own files; never runs on the game thread.
    internal sealed class MarkJson
    {
        private readonly string _text;
        private readonly bool _project, _state;
        private int _at;
        private int _headStart = -1, _headEnd;
        private MarkJson(string text, bool project = false, bool state = false) { if (text.Length > 16 << 20) throw new InvalidDataException("mark JSON size"); _text = text; _project = project; _state = state; }
        internal static object Parse(string text) { var reader = new MarkJson(text); object result = reader.Read(0); reader.Space(); if (reader._at != text.Length) throw new InvalidDataException("mark JSON tail"); return result; }
        internal static object Box(string text, bool state = false)
        {
            var reader = new MarkJson(text, true, state); object result = reader.Read(0); reader.Space();
            if (reader._at != text.Length) throw new InvalidDataException("mark JSON tail"); return result;
        }
        internal static string RenameNote(string text, string note)
        {
            var reader = new MarkJson(text, true); var box = Object(reader.Read(0));
            reader.Space(); if (reader._at != text.Length) throw new InvalidDataException("mark JSON tail");
            if (reader._headStart < 0) throw new InvalidDataException("mark head missing");
            var head = Object(box["head"]); head["note"] = TelemetryPrivacy.Note(note);
            return text.Substring(0, reader._headStart) + Write(head) + text.Substring(reader._headEnd);
        }
        internal static Dictionary<string, object> Object(object value) => value as Dictionary<string, object> ?? new Dictionary<string, object>();
        internal static object Get(object value, string key) => Object(value).TryGetValue(key, out var result) ? result : null;
        internal static string Text(Dictionary<string, object> value, string key) => Get(value, key) as string ?? "";
        internal static double Number(Dictionary<string, object> value, string key) => Get(value, key) is double n ? n : 0;
        private char Peek => _at < _text.Length ? _text[_at] : '\0';
        private void Space() { while (char.IsWhiteSpace(Peek)) _at++; }
        private bool Take(char c) { Space(); if (Peek != c) return false; _at++; return true; }
        private void Need(char c) { if (!Take(c)) throw new InvalidDataException("mark JSON syntax"); }
        private string String()
        {
            Need('"'); var b = new StringBuilder();
            while (_at < _text.Length)
            {
                char c = _text[_at++]; if (c == '"') return b.ToString();
                if (c == '\\')
                {
                    c = _text[_at++];
                    switch (c)
                    {
                        case '"': case '\\': case '/': break;
                        case 'n': c = '\n'; break; case 'r': c = '\r'; break; case 't': c = '\t'; break; case 'b': c = '\b'; break; case 'f': c = '\f'; break;
                        case 'u': c = (char)int.Parse(_text.Substring(_at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture); _at += 4; break;
                        default: throw new InvalidDataException("mark JSON escape");
                    }
                }
                b.Append(c);
            }
            throw new InvalidDataException("mark JSON string");
        }
        private object Read(int depth)
        {
            if (depth > 64) throw new InvalidDataException("mark JSON depth");
            Space(); if (Peek == '"') return String();
            if (Take('{'))
            {
                var result = new Dictionary<string, object>();
                if (!Take('}'))
                {
                    do
                    {
                        string key = String(); Need(':'); Space(); int valueStart = _at;
                        if (_project && depth == 0 && key != "head" && (!_state || key != "state")) { Skip(depth + 1); continue; }
                        result.Add(key, Read(depth + 1));
                        if (depth == 0 && key == "head") { _headStart = valueStart; _headEnd = _at; }
                    } while (Take(','));
                    Need('}');
                }
                return result;
            }
            if (Take('['))
            {
                var result = new List<object>(); if (!Take(']')) { do { result.Add(Read(depth + 1)); } while (Take(',')); Need(']'); } return result;
            }
            int start = _at; while (Peek != '\0' && Peek != ',' && Peek != ']' && Peek != '}' && !char.IsWhiteSpace(Peek)) _at++;
            string token = _text.Substring(start, _at - start);
            if (token == "null") return null; if (token == "true") return true; if (token == "false") return false;
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && !double.IsNaN(n) && !double.IsInfinity(n)) return n;
            throw new InvalidDataException("mark JSON value");
        }
        // Frame/history arrays can contain millions of numbers. Walk past them without boxing each sample.
        private void Skip(int depth)
        {
            if (depth > 64) throw new InvalidDataException("mark JSON depth");
            Space();
            if (Peek == '"')
            {
                _at++;
                while (_at < _text.Length)
                {
                    char c = _text[_at++]; if (c == '"') return;
                    if (c == '\\') _at++;
                    else if (c < 32) throw new InvalidDataException("mark JSON string");
                }
                throw new InvalidDataException("mark JSON string");
            }
            if (Take('{')) { if (!Take('}')) { do { Skip(depth + 1); Need(':'); Skip(depth + 1); } while (Take(',')); Need('}'); } return; }
            if (Take('[')) { if (!Take(']')) { do { Skip(depth + 1); } while (Take(',')); Need(']'); } return; }
            int start = _at; while (Peek != '\0' && Peek != ',' && Peek != ']' && Peek != '}' && Peek != ':' && !char.IsWhiteSpace(Peek)) _at++;
            if (_at == start) throw new InvalidDataException("mark JSON value");
        }
        internal static string Write(object value)
        {
            if (value == null) return "null";
            if (value is string text) return Json.Str(text);
            if (value is bool boolean) return boolean ? "true" : "false";
            if (value is Dictionary<string, object> map) return "{" + string.Join(",", map.Select(p => Json.Str(p.Key) + ":" + Write(p.Value))) + "}";
            if (value is IEnumerable<object> list) return "[" + string.Join(",", list.Select(Write)) + "]";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}

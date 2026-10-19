using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace TLDRevamp.Net
{
    // Pure transfer policy and validation: shared by the game and tools/unit.
    internal static class ReportTransfer
    {
        internal const int ReportMax = 24 << 20, MarkMax = 2 << 20, JsonMax = 16 << 20;
        internal const int ChunkBytes = 16 << 10, Rate = 160 << 10, Burst = 64 << 10;
        internal const long PlayerMax = 64L << 20, RelayMax = 256L << 20, QueueMax = 64L << 20;
        internal static bool Receives(bool sourceShares, bool targetShares, int source, int target) => sourceShares && targetShares && source != target;
        internal static string Slug(string value, int max = 32)
        {
            var b = new StringBuilder();
            foreach (char c in value ?? "")
                if (b.Length < max && (c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_')) b.Append(c);
            // Prefixes at use sites also avoid Windows reserved device names.
            return b.Length == 0 ? "unknown" : b.ToString();
        }
        internal static string MomentBase(string sid, double moment) => "mark-" + (TelemetryPrivacy.SessionId(sid) ? sid : "local") + "-" + moment.ToString("F3", CultureInfo.InvariantCulture).Replace('.', '-');
        internal static string MomentName(string sid, double moment, string note)
        {
            var b = new StringBuilder();
            foreach (char c in TelemetryPrivacy.Note(note))
            {
                bool ascii = c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9';
                if (ascii) b.Append(c);
                else if (b.Length > 0 && b[b.Length - 1] != '-') b.Append('-');
                if (b.Length >= 40) break;
            }
            string slug = b.ToString().TrimEnd('-');
            return MomentBase(sid, moment) + "-" + (slug.Length == 0 ? "unknown" : slug);
        }
        internal static string Hash(byte[] data)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
        internal static byte[] ReadBounded(Stream input, int max)
        {
            using (var output = new MemoryStream())
            {
                var buf = new byte[16384]; int n;
                while ((n = input.Read(buf, 0, Math.Min(buf.Length, max - (int)output.Length + 1))) != 0)
                {
                    if (output.Length + n > max) throw new InvalidDataException("expanded size limit");
                    output.Write(buf, 0, n);
                }
                return output.ToArray();
            }
        }
        internal static byte[] Compress(byte[] json)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(json, 0, json.Length);
                byte[] bytes = output.ToArray();
                for (int i = 4; i < 8; i++) bytes[i] = 0;
                return bytes;
            }
        }
        private static readonly uint[] CrcTable = MakeCrcTable();
        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint b = 0; b < table.Length; b++)
            {
                uint crc = b;
                for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320U : 0);
                table[b] = crc;
            }
            return table;
        }
        private static uint Crc(byte[] bytes)
        {
            uint crc = uint.MaxValue;
            foreach (byte b in bytes) crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 255];
            return ~crc;
        }
        private static uint U32(byte[] bytes, int at) => (uint)(bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16 | bytes[at + 3] << 24);
        private static int U16(byte[] bytes, int at) => bytes[at] | bytes[at + 1] << 8;
        private static uint[] ZipCrcs(byte[] data)
        {
            // Bound the central directory before ZipArchive materializes entries. No split/ZIP64 archives are needed
            // for a 24 MiB report, and the end record must account for every byte (including an optional comment).
            int end = -1;
            for (int at = data.Length - 22; at >= Math.Max(0, data.Length - 65557); at--)
                if (U32(data, at) == 0x06054b50 && at + 22 + U16(data, at + 20) == data.Length) { end = at; break; }
            if (end < 0 || U16(data, end + 4) != 0 || U16(data, end + 6) != 0 || U16(data, end + 20) != 0) throw new InvalidDataException("zip end record");
            int count = U16(data, end + 10);
            uint size = U32(data, end + 12), offset = U32(data, end + 16);
            if (count == 0 || count > 256 || U16(data, end + 8) != count || size > 128 << 10 || (long)offset + size != end) throw new InvalidDataException("zip directory limit");
            var crcs = new uint[count]; int pos = (int)offset;
            for (int i = 0; i < count; i++)
            {
                if (pos > end - 46 || U32(data, pos) != 0x02014b50) throw new InvalidDataException("zip directory entry");
                int nameBytes = U16(data, pos + 28);
                uint local = U32(data, pos + 42);
                if (U16(data, pos + 30) != 0 || U16(data, pos + 32) != 0 || U16(data, pos + 12) != 0 || U16(data, pos + 14) != 33 ||
                    local > offset || (long)local + 30 + nameBytes > offset || pos + 46 + nameBytes > end)
                    throw new InvalidDataException("private zip metadata");
                int start = (int)local;
                if (U32(data, start) != 0x04034b50 || U16(data, start + 10) != 0 || U16(data, start + 12) != 33 ||
                    U16(data, start + 26) != nameBytes || U16(data, start + 28) != 0) throw new InvalidDataException("private zip local metadata");
                for (int n = 0; n < nameBytes; n++) if (data[pos + 46 + n] != data[start + 30 + n]) throw new InvalidDataException("zip local name");
                crcs[i] = U32(data, pos + 16);
                pos += 46 + U16(data, pos + 28) + U16(data, pos + 30) + U16(data, pos + 32);
            }
            if (pos != end) throw new InvalidDataException("zip directory length");
            return crcs;
        }
        // Strict syntax walk: no object graph, bounded nesting, no trailing documents or duplicate headers.
        internal sealed class ScrubCounts
        {
            internal int FieldsScrubbed, KeysDropped;
            internal string FirstPath;
            internal bool Changed => FieldsScrubbed != 0 || KeysDropped != 0;
        }
        internal static byte[] Json(byte[] json, bool blackbox, string sid, double moment, bool privacy = false, ScrubCounts counts = null, string path = "$")
        {
            if (json.Length > JsonMax) throw new InvalidDataException("JSON size limit");
            try
            {
                var check = new JsonCheck(new UTF8Encoding(false, true).GetString(json), blackbox, sid, moment, privacy, counts ?? new ScrubCounts());
                return check.Check(json, path);
            }
            catch (DecoderFallbackException) { throw new InvalidDataException("invalid UTF-8"); }
        }
        private sealed class JsonCheck
        {
            private readonly string _s, _sid;
            private readonly bool _box, _privacy;
            private readonly double _moment;
            private int _at;
            private bool _head, _cols, _rows, _session, _stamp, _safe;
            private readonly ScrubCounts _counts;
            private readonly List<(int Start, int End, string Text)> _edits = new List<(int, int, string)>();
            internal JsonCheck(string s, bool box, string sid, double moment, bool privacy, ScrubCounts counts)
            { _s = s; _box = box; _sid = sid; _moment = moment; _privacy = privacy; _counts = counts; }
            private char Peek => _at < _s.Length ? _s[_at] : '\0';
            private void Bad(string why = "invalid JSON or black-box header") => throw new InvalidDataException(why + " at char " + _at);
            private void Space() { while (Peek == ' ' || Peek == '\t' || Peek == '\r' || Peek == '\n') _at++; }
            private bool Take(char c) { Space(); if (Peek != c) return false; _at++; return true; }
            private void Need(char c) { if (!Take(c)) Bad(); }
            internal byte[] Check(byte[] original, string path)
            {
                Space(); if (Peek != '{') Bad(); Value(0, 1, path: path); Space();
                if (_at != _s.Length) Bad("trailing JSON");
                if (_box && (!(_head && _cols && _rows && _session && _stamp) || _privacy && !_safe)) Bad("black-box required fields");
                if (_edits.Count == 0) return original;
                var b = new StringBuilder(); int at = 0;
                foreach (var edit in _edits) { b.Append(_s, at, edit.Start - at).Append(edit.Text); at = edit.End; }
                b.Append(_s, at, _s.Length - at);
                byte[] bytes = Encoding.UTF8.GetBytes(b.ToString());
                if (bytes.Length > JsonMax) Bad("JSON size limit");
                return bytes;
            }
            private string String(bool keep = true)
            {
                Need('"'); var b = keep ? new StringBuilder() : null;
                while (_at < _s.Length)
                {
                    char c = _s[_at++];
                    if (c == '"') return b?.ToString();
                    if (c < 32) Bad();
                    if (c == '\\')
                    {
                        if (_at == _s.Length) Bad(); c = _s[_at++];
                        switch (c)
                        {
                            case '"': case '\\': case '/': break;
                            case 'b': c = '\b'; break; case 'f': c = '\f'; break; case 'n': c = '\n'; break; case 'r': c = '\r'; break; case 't': c = '\t'; break;
                            case 'u':
                                int code = 0;
                                for (int i = 0; i < 4; i++)
                                {
                                    if (_at == _s.Length) Bad(); char h = _s[_at++];
                                    int digit = h >= '0' && h <= '9' ? h - '0' : h >= 'a' && h <= 'f' ? h - 'a' + 10 : h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1;
                                    if (digit < 0) Bad(); code = code * 16 + digit;
                                }
                                c = (char)code; break;
                            default: Bad(); break;
                        }
                    }
                    b?.Append(c);
                }
                Bad(); return null;
            }
            private string Number(bool keep = false)
            {
                Space(); int start = _at;
                if (Peek == '-') _at++;
                if (Peek == '0') _at++;
                else { if (Peek < '1' || Peek > '9') Bad(); while (Peek >= '0' && Peek <= '9') _at++; }
                if (Peek == '.') { _at++; Digits(); }
                if (Peek == 'e' || Peek == 'E') { _at++; if (Peek == '+' || Peek == '-') _at++; Digits(); }
                return keep ? _s.Substring(start, _at - start) : null;
            }
            private void Digits() { if (Peek < '0' || Peek > '9') Bad(); while (Peek >= '0' && Peek <= '9') _at++; }
            // context 1 = root object, 2 = root head, 0 = other values.
            private void Value(int depth, int context = 0, string field = "", string path = "$", bool unknown = false, bool discard = false)
            {
                if (depth > 64) Bad(); Space();
                if (Take('{'))
                {
                    if (Take('}')) return;
                    bool kept = false; int comma = -1;
                    do
                    {
                        Space(); int start = _at;
                        string key = String(); Need(':'); Space();
                        bool known = TelemetryPrivacy.ShareKey(key);
                        bool drop = _privacy && (TelemetryPrivacy.PrivateKey(key) || !TelemetryPrivacy.PlainKey(key));
                        string child = path + "." + (TelemetryPrivacy.PlainKey(key) ? key : "[unsafe-key]");
                        if (!discard && comma >= 0 && (drop || !kept)) _edits.Add((comma, comma + 1, ""));
                        if (drop || discard)
                        {
                            Value(depth + 1, path: child, discard: true);
                            if (!discard)
                            {
                                _edits.Add((start, _at, "")); _counts.KeysDropped++;
                                if (_counts.FirstPath == null && !TelemetryPrivacy.PrivateKey(key)) _counts.FirstPath = child;
                            }
                        }
                        else if (_box && context == 1 && key == "head") { if (_head || Peek != '{') Bad(); _head = true; Value(depth + 1, 2, path: child); }
                        else if (_box && context == 1 && key == "cols") { if (_cols || Peek != '[') Bad(); _cols = true; Value(depth + 1, 0, key, child); }
                        else if (_box && context == 1 && key == "rows") { if (_rows || Peek != '[') Bad(); _rows = true; Value(depth + 1, path: child); }
                        else if (_privacy && _box && context == 2 && key == "privacy") { if (_safe || Number(true) != "1") Bad("black-box privacy version"); _safe = true; }
                        else if (_box && context == 2 && key == "sid") { if (_session || String() != _sid) Bad("black-box session"); _session = true; }
                        else if (_box && context == 2 && key == "momentSt")
                        {
                            if (_stamp || !double.TryParse(Number(true), NumberStyles.Float, CultureInfo.InvariantCulture, out double st) || double.IsInfinity(st) || Math.Abs(st - _moment) >= 0.002) Bad("black-box moment");
                            _stamp = true;
                        }
                        else Value(depth + 1, 0, key, child, unknown || !known);
                        if (!drop) kept = true;
                        Space(); comma = _at;
                    } while (Take(','));
                    Need('}');
                }
                else if (Take('['))
                {
                    if (Take(']')) return;
                    int index = 0;
                    do { Value(depth + 1, 0, field, path + "[" + index++ + "]", unknown, discard); } while (Take(',')); Need(']');
                }
                else if (Peek == '"')
                {
                    int start = _at; string value = String();
                    if (_privacy && !discard)
                    {
                        if (unknown && value != TelemetryPrivacy.Removed)
                        {
                            _edits.Add((start, _at, TLDRevamp.Json.Str(TelemetryPrivacy.Removed))); _counts.FieldsScrubbed++;
                            if (_counts.FirstPath == null) _counts.FirstPath = path;
                        }
                        else if (!unknown && !TelemetryPrivacy.ShareString(field, value)) Bad("unapproved JSON string");
                    }
                }
                else if (Peek == '-' || Peek >= '0' && Peek <= '9')
                {
                    string value = Number(_privacy);
                    if (_privacy && !unknown && !discard && (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || Math.Abs(n) >= 1e15)) Bad();
                }
                else
                {
                    string token = Peek == 't' ? "true" : Peek == 'f' ? "false" : "null";
                    if (_at > _s.Length - token.Length || string.CompareOrdinal(_s, _at, token, 0, token.Length) != 0) Bad();
                    _at += token.Length;
                }
            }
        }
        // A mark is GZip JSON; a report is a zip, never extracted. Both ends apply the same checks.
        internal static byte[] Validate(byte[] data, bool mark, string sid, double moment)
            => Validate(ref data, mark, sid, moment, out _);
        internal static byte[] Validate(ref byte[] data, bool mark, string sid, double moment, out ScrubCounts counts)
        {
            counts = new ScrubCounts();
            if (data.Length == 0 || data.Length > (mark ? MarkMax : ReportMax)) throw new InvalidDataException("file size limit");
            if (mark)
            {
                if (data.Length < 18 || data[0] != 31 || data[1] != 139 || data[2] != 8 || data[3] != 0 || U32(data, 4) != 0) throw new InvalidDataException("GZip header");
                using (var input = new MemoryStream(data))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                {
                    var json = ReadBounded(gzip, JsonMax);
                    if (U32(data, data.Length - 4) != json.Length || U32(data, data.Length - 8) != Crc(json)) throw new InvalidDataException("GZip checksum/length");
                    byte[] clean = Json(json, true, sid, moment, true, counts);
                    if (!ReferenceEquals(clean, json)) data = Compress(clean);
                    if (data.Length > MarkMax) throw new InvalidDataException("file size limit");
                    return clean;
                }
            }
            uint[] crcs = ZipCrcs(data);
            using (var input = new MemoryStream(data))
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
            using (var output = new MemoryStream())
            {
                using (var cleanZip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    if (zip.Entries.Count != crcs.Length) throw new InvalidDataException("zip entry count");
                    long total = 0, cleanTotal = 0; bool stats = false, box = false; int index = 0;
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in zip.Entries)
                    {
                        if ((e.ExternalAttributes >> 16 & 0xf000) == 0xa000 || (e.ExternalAttributes & 0x400) != 0) throw new InvalidDataException("zip link entry");
                        string name = e.FullName;
                        if (name.Length == 0 || name.Length > 180 || name.StartsWith("/") || name.Contains("\\") || name.Contains(":") || !names.Add(name)) throw new InvalidDataException("zip name");
                        bool directory = name.EndsWith("/");
                        if (directory ? name != "sessions/" : !TelemetryPrivacy.Attachment(name)) throw new InvalidDataException("excluded report attachment");
                        if (e.LastWriteTime.DateTime != new DateTime(1980, 1, 1)) throw new InvalidDataException("private zip timestamp");
                        string path = directory ? name.Substring(0, name.Length - 1) : name;
                        foreach (string part in path.Split('/')) if (part == ".." || part == "." || part.Length == 0) throw new InvalidDataException("zip path");
                        if (directory && e.Length != 0) throw new InvalidDataException("zip directory data");
                        foreach (char c in name) if (char.IsControl(c)) throw new InvalidDataException("zip control character");
                        total += e.Length;
                        if (e.Length < 0 || e.Length > 32 << 20 || total > 96L << 20) throw new InvalidDataException("zip expanded size limit");
                        using (var stream = e.Open())
                        {
                            var bytes = ReadBounded(stream, (int)e.Length);
                            if (bytes.Length != e.Length || Crc(bytes) != crcs[index++]) throw new InvalidDataException("zip checksum/length");
                            if (name.EndsWith(".json")) bytes = Json(bytes, name == "blackbox.json", sid, moment, true, counts, name + ":$");
                            else if (!directory)
                            {
                                string text = new UTF8Encoding(false, true).GetString(bytes);
                                if (name.EndsWith(".jsonl"))
                                {
                                    string[] lines = text.Split('\n');
                                    for (int line = 0; line < lines.Length; line++)
                                        if (lines[line].Trim().Length != 0)
                                            lines[line] = Encoding.UTF8.GetString(Json(Encoding.UTF8.GetBytes(lines[line]), false, sid, moment, true, counts, name + ":$[" + line + "]"));
                                    bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines));
                                }
                                else TelemetryPrivacy.CheckSettings(text);
                            }
                            cleanTotal += bytes.Length;
                            if (bytes.Length > 32 << 20 || cleanTotal > 96L << 20) throw new InvalidDataException("zip expanded size limit");
                            var cleanEntry = cleanZip.CreateEntry(name, CompressionLevel.Optimal);
                            cleanEntry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                            using (var target = cleanEntry.Open()) target.Write(bytes, 0, bytes.Length);
                            if (name == "stats.json") stats = true;
                            if (name == "blackbox.json") box = true;
                        }
                    }
                    if (!stats || !box) throw new InvalidDataException("report attachments missing");
                }
                if (counts.Changed)
                {
                    if (output.Length > ReportMax) throw new InvalidDataException("file size limit");
                    data = output.ToArray();
                }
            }
            return null;
        }
    }

    internal sealed class ReportRelayBudget
    {
        internal long Used { get; private set; }
        internal bool Reserve(int bytes)
        {
            if (bytes <= 0 || bytes > ReportTransfer.ReportMax || Used + bytes > ReportTransfer.RelayMax) return false;
            Used += bytes; return true;
        }
        internal void Clear() => Used = 0;
    }

    internal sealed class ReportAssembly
    {
        internal readonly uint Id;
        internal readonly int Total;
        internal int Got;
        private byte[] _buffer;
        internal ReportAssembly(uint id, int total, bool mark)
        {
            if (total <= 0 || total > (mark ? ReportTransfer.MarkMax : ReportTransfer.ReportMax)) throw new InvalidDataException("size limit");
            Id = id; Total = total; _buffer = new byte[Math.Min(total, 256 << 10)];
        }
        internal byte[] Add(uint id, int total, int offset, byte[] bytes, int start, int count)
        {
            if (id != Id || total != Total || offset != Got || count <= 0 || count > ReportTransfer.ChunkBytes || count > Total - Got || start < 0 || start > bytes.Length - count) throw new InvalidDataException("chunk order/size");
            if (Got + count > _buffer.Length) Array.Resize(ref _buffer, Math.Min(Total, Math.Max(Got + count, _buffer.Length * 2)));
            Buffer.BlockCopy(bytes, start, _buffer, Got, count); Got += count;
            return Got == Total ? _buffer : null;
        }
    }

    internal sealed class ReportAllowance
    {
        private int _reports, _marks;
        private long _bytes;
        private double _reportAt = -1000, _markAt = -1000, _at;
        private double _tokens = ReportTransfer.Burst;
        private readonly HashSet<string> _seen = new HashSet<string>();
        internal bool Start(bool mark, int total, double now, bool gap = true)
        {
            if (total <= 0 || total > (mark ? ReportTransfer.MarkMax : ReportTransfer.ReportMax) || _bytes + total > ReportTransfer.PlayerMax ||
                (mark ? _marks >= 80 || gap && now - _markAt < 10 : _reports >= 20 || gap && now - _reportAt < 30)) return false;
            _bytes += total;
            if (mark) { _marks++; _markAt = now; } else { _reports++; _reportAt = now; }
            return true;
        }
        internal bool Chunk(int bytes, double now)
        {
            _tokens = Math.Min(ReportTransfer.Burst, _tokens + Math.Max(0, now - _at) * ReportTransfer.Rate); _at = now;
            if (bytes <= 0 || bytes > ReportTransfer.ChunkBytes || bytes > _tokens) return false;
            _tokens -= bytes; return true;
        }
        internal bool Has(string hash) => _seen.Contains(hash);
        internal bool Remember(string hash) => _seen.Add(hash);
    }
}

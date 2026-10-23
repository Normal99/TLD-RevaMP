using System;
using System.Collections.Generic;
using System.Text;
using TLDRevamp.Multiplayer;

namespace TvBrowser
{
    /// What everyone near one TV shares: the page, how far down it is scrolled, and who has the keyboard. Each machine
    /// runs its own browser and keeps it on this (TvScreen.cs): only addresses, scroll positions and keys pass between
    /// players, never pictures.
    ///
    /// Pure (no Unity): unit-tested by tools/unit/TvBrowserCoreTest.cs.
    public sealed class TvState
    {
        public uint Tv;                 // the TV's id in the session (MpApi.WorldObjectId); alone, a local number
        public string Url = "";         // "" = the TV's own start page
        public uint NavSeq;             // counts the addresses typed and the reloads: the same address loads again
        public int Driver = -1;         // the player at the keyboard, -1: nobody
        public float ScrollX, ScrollY;  // the driver's page position (CSS pixels)

        public void Write(RecordWriter w)
        {
            w.UInt(Tv); w.String(Url ?? ""); w.UInt(NavSeq); w.VarInt(Driver); w.Float(ScrollX); w.Float(ScrollY);
        }

        public bool Read(RecordReader r)
        {
            uint tv = r.UInt(); string url = r.String(); uint seq = r.UInt(); int driver = r.VarInt(); float x = r.Float(), y = r.Float();
            if (r.Bad || driver < -1 || !Address.ValidScroll(x) || !Address.ValidScroll(y)) return false;
            if (url.Length > 0 && Address.Clean(url) != url) return false;
            Tv = tv; Url = url; NavSeq = seq; Driver = driver; ScrollX = x; ScrollY = y;
            return true;
        }
    }

    /// The edits a player can ask the TV state's owner for (TvStateType.OnEdit).
    public enum Op : byte { Take = 1, Release = 2, Navigate = 3, Url = 4, Scroll = 5, Reload = 6 }

    public static class TvEdits
    {
        /// Apply one edit (the owner, or this machine alone). False: refused, nothing changed (no new record goes out).
        /// Only the player at the keyboard moves the page; anyone may take the keyboard.
        public static bool Apply(TvState s, RecordReader e, int from)
        {
            var op = (Op)e.Byte();
            if (e.Bad) return false;
            switch (op)
            {
                case Op.Take:
                    if (s.Driver == from) return false;
                    s.Driver = from;
                    return true;
                case Op.Release:
                    if (s.Driver != from) return false;
                    s.Driver = -1;
                    return true;
                case Op.Navigate:
                {
                    string url = e.String();
                    if (e.Bad || s.Driver != from) return false;
                    if (url.Length > 0 && (url = Address.Clean(url)) == null) return false;
                    s.Url = url; s.NavSeq++; s.ScrollX = 0f; s.ScrollY = 0f;
                    return true;
                }
                case Op.Url:
                {
                    // the driver's page went somewhere by itself (a link, a redirect, back): the others follow
                    string url = e.String();
                    if (e.Bad || s.Driver != from) return false;
                    if (url.Length > 0 && (url = Address.Clean(url)) == null) return false;
                    if (url == s.Url) return false;
                    s.Url = url; s.ScrollX = 0f; s.ScrollY = 0f;
                    return true;
                }
                case Op.Scroll:
                {
                    float x = e.Float(), y = e.Float();
                    if (e.Bad || s.Driver != from || !Address.ValidScroll(x) || !Address.ValidScroll(y)) return false;
                    if (Math.Abs(x - s.ScrollX) < 1f && Math.Abs(y - s.ScrollY) < 1f) return false;
                    s.ScrollX = x; s.ScrollY = y;
                    return true;
                }
                case Op.Reload:
                    if (s.Driver != from) return false;
                    s.NavSeq++;
                    return true;
            }
            return false;
        }
    }

    /// Addresses a TV may show: http and https only, never this machine or its network (a player can't point
    /// everyone's browser at their routers or at programs listening on their own machine).
    public static class Address
    {
        public const int MaxLength = 2048;

        public static bool ValidScroll(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && v >= 0f && v <= 10000000f;

        /// The address cleaned up (a scheme added), or null when a TV may not show it.
        public static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (text.Length > MaxLength) return null;
            foreach (char c in text) if (c < ' ' || c == 0x7f) return null;
            if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (text.Contains("://")) return null;
                text = "https://" + text;
            }
            if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return null;
            if (!string.IsNullOrEmpty(u.UserInfo)) return null;
            if (Private(u.Host)) return null;
            string s = u.AbsoluteUri;
            return s.Length <= MaxLength ? s : null;
        }

        /// What the address bar's text means: an address (it looks like one) or words to search for with `search`
        /// (the words appended, escaped). Null when it can't be shown.
        public static string FromBar(string text, string search)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            bool looksLikeAddress = text.IndexOf(' ') < 0 && (text.Contains("://") || text.Contains(".") && !text.EndsWith("."));
            if (looksLikeAddress) { var a = Clean(text); if (a != null) return a; }
            if (text.Contains("://")) return null;
            return Clean((search ?? "") + Uri.EscapeDataString(text));
        }

        /// Hosts on this machine or its local network: loopback, private, link-local and unspecified addresses, and
        /// names that only mean something locally.
        public static bool Private(string host)
        {
            if (string.IsNullOrEmpty(host)) return true;
            host = host.Trim('[', ']').ToLowerInvariant();
            if (host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".internal") || host.EndsWith(".lan") || host.IndexOf('.') < 0 && host.IndexOf(':') < 0)
                return true;
            if (System.Net.IPAddress.TryParse(host, out var ip))
            {
                if (System.Net.IPAddress.IsLoopback(ip)) return true;
                var b = ip.GetAddressBytes();
                if (b.Length == 4)
                    return b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 ||
                           b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] >= 64 && b[1] <= 127 || b[0] >= 224;
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.Equals(System.Net.IPAddress.IPv6None)) return true;
                if (b[0] == 0xfc || b[0] == 0xfd) return true;   // unique local
                if (ip.IsIPv4MappedToIPv6) return Private(ip.MapToIPv4().ToString());
            }
            return false;
        }

        /// The same hosts for the browser's own blocking (Network.setBlockedURLs), so a page can't reach them either
        /// (links, redirects, images, scripts): as host patterns, written out both as the old wildcard addresses
        /// (`urls`, older Chromium) and as URLPattern strings (`urlPatterns`, which newer Chromium reads instead).
        public static readonly string[] BlockedHosts = BuildHosts();

        private static string[] BuildHosts()
        {
            var h = new List<string> { "localhost", "*.localhost", "*.local", "127.*", "10.*", "192.168.*", "169.254.*", "0.*" };
            for (int i = 16; i <= 31; i++) h.Add("172." + i + ".*");
            for (int i = 64; i <= 127; i++) h.Add("100." + i + ".*");
            return h.ToArray();
        }

        public static readonly string[] BlockedPatterns = BuildPatterns();

        private static string[] BuildPatterns()
        {
            var p = new List<string>();
            foreach (var h in BlockedHosts) { p.Add("*://" + h + "/*"); p.Add("*://" + h + ":*"); }
            p.Add("*://[*"); p.Add("file:*"); p.Add("ftp:*");
            return p.ToArray();
        }

        /// Network.setBlockedURLs's arguments: both spellings (a Chromium reads the one it knows).
        public static Dictionary<string, object> BlockArgs()
        {
            var patterns = new List<object>();
            foreach (var h in BlockedHosts) patterns.Add(Json.O("urlPattern", "*://" + h + ":*/*", "block", true));
            patterns.Add(Json.O("urlPattern", "*://[*]:*/*", "block", true));
            patterns.Add(Json.O("urlPattern", "file://*", "block", true));
            return Json.O("urls", BlockedPatterns, "urlPatterns", patterns);
        }

        /// Fetch.enable's arguments: setBlockedURLs leaves the page's own navigations alone (a link, a redirect, a
        /// typed address), so those requests to the same hosts are held and failed (Tab.OnEvent). Only they stop;
        /// every other request goes on without a round trip.
        public static Dictionary<string, object> FetchArgs()
        {
            var patterns = new List<object>();
            foreach (var u in BlockedPatterns) patterns.Add(Json.O("urlPattern", u));
            return Json.O("patterns", patterns);
        }
    }

    /// A key as the page gets it: the Windows key code and what the browser calls it (Input.dispatchKeyEvent). Only
    /// these, letters and digits (with Ctrl or Alt) and typed text pass between players.
    public readonly struct KeyInfo
    {
        public readonly int Code;
        public readonly string Key, CodeName, Text;
        public KeyInfo(int code, string key, string codeName, string text) { Code = code; Key = key; CodeName = codeName; Text = text; }
    }

    public static class Keys
    {
        public const byte Alt = 1, Ctrl = 2, Meta = 4, Shift = 8;

        private static readonly Dictionary<int, KeyInfo> Named = new Dictionary<int, KeyInfo>
        {
            { 8, new KeyInfo(8, "Backspace", "Backspace", null) },
            { 9, new KeyInfo(9, "Tab", "Tab", null) },
            { 13, new KeyInfo(13, "Enter", "Enter", "\r") },
            { 27, new KeyInfo(27, "Escape", "Escape", null) },
            { 32, new KeyInfo(32, " ", "Space", " ") },
            { 33, new KeyInfo(33, "PageUp", "PageUp", null) },
            { 34, new KeyInfo(34, "PageDown", "PageDown", null) },
            { 35, new KeyInfo(35, "End", "End", null) },
            { 36, new KeyInfo(36, "Home", "Home", null) },
            { 37, new KeyInfo(37, "ArrowLeft", "ArrowLeft", null) },
            { 38, new KeyInfo(38, "ArrowUp", "ArrowUp", null) },
            { 39, new KeyInfo(39, "ArrowRight", "ArrowRight", null) },
            { 40, new KeyInfo(40, "ArrowDown", "ArrowDown", null) },
            { 46, new KeyInfo(46, "Delete", "Delete", null) },
            { 113, new KeyInfo(113, "F2", "F2", null) },   // the TV's link hints (Hints.Script)
        };

        /// The key for a code, or null if it may not be sent. Letters and digits only come with Ctrl or Alt (typed
        /// text goes as text).
        public static KeyInfo? Get(int code, byte mods)
        {
            if (mods > 15) return null;
            if (Named.TryGetValue(code, out var k)) return k;
            bool combo = (mods & (Ctrl | Alt)) != 0;
            if (combo && code >= 65 && code <= 90)
            {
                string l = ((char)('a' + code - 65)).ToString();
                return new KeyInfo(code, (mods & Shift) != 0 ? l.ToUpperInvariant() : l, "Key" + (char)code, null);
            }
            if (combo && code >= 48 && code <= 57) return new KeyInfo(code, ((char)code).ToString(), "Digit" + (char)code, null);
            return null;
        }
    }

    /// What a player does at a TV's keyboard, as it goes to every machine near the TV: typed text, a key, the wheel,
    /// back / forward. Batched (Command.Write) in one mod event a tenth of a second.
    public enum CmdKind : byte { Text = 1, Key = 2, Wheel = 3, Back = 4, Forward = 5 }

    public struct Command
    {
        public CmdKind Kind;
        public string Text;    // Text: what was typed (at most MaxText characters)
        public int Code;       // Key: the Windows key code
        public byte Mods;      // Key: Keys.Alt / Ctrl / Meta / Shift
        public int Delta;      // Wheel: pixels, + down

        public const int MaxText = 64, MaxWheel = 2000, MaxBatch = 32;

        public static void Write(RecordWriter w, List<Command> cmds, int from, int count)
        {
            w.Byte((byte)count);
            for (int i = from; i < from + count; i++)
            {
                var c = cmds[i];
                w.Byte((byte)c.Kind);
                switch (c.Kind)
                {
                    case CmdKind.Text: w.String(c.Text ?? ""); break;
                    case CmdKind.Key: w.Byte((byte)c.Code); w.Byte(c.Mods); break;
                    case CmdKind.Wheel: w.VarInt(c.Delta); break;
                }
            }
        }

        /// Reads a batch; null if any command in it is not one a player could send (the whole batch is dropped).
        public static List<Command> Read(RecordReader r)
        {
            int n = r.Byte();
            if (r.Bad || n == 0 || n > MaxBatch) return null;
            var list = new List<Command>(n);
            for (int i = 0; i < n; i++)
            {
                var c = new Command { Kind = (CmdKind)r.Byte() };
                switch (c.Kind)
                {
                    case CmdKind.Text:
                        c.Text = r.String();
                        if (r.Bad || c.Text.Length == 0 || c.Text.Length > MaxText) return null;
                        foreach (char ch in c.Text) if (char.IsControl(ch)) return null;
                        break;
                    case CmdKind.Key:
                        c.Code = r.Byte(); c.Mods = r.Byte();
                        if (r.Bad || Keys.Get(c.Code, c.Mods) == null) return null;
                        break;
                    case CmdKind.Wheel:
                        c.Delta = r.VarInt();
                        if (r.Bad || c.Delta == 0 || Math.Abs(c.Delta) > MaxWheel) return null;
                        break;
                    case CmdKind.Back: case CmdKind.Forward: break;
                    default: return null;
                }
                if (r.Bad) return null;
                list.Add(c);
            }
            return list;
        }

        /// How many of `cmds` from `from` fit in one event of at most `maxBytes` (at least one, at most MaxBatch).
        public static int Fit(List<Command> cmds, int from, int maxBytes)
        {
            int bytes = 1 + 8, n = 0;   // the count, plus the event's own header (the TV and a sequence number)
            for (int i = from; i < cmds.Count && n < MaxBatch; i++)
            {
                var c = cmds[i];
                int size = 1 + (c.Kind == CmdKind.Text ? 2 + Encoding.UTF8.GetByteCount(c.Text ?? "") : c.Kind == CmdKind.Key ? 2 : c.Kind == CmdKind.Wheel ? 5 : 0);
                if (n > 0 && bytes + size > maxBytes) break;
                bytes += size; n++;
            }
            return n;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace HeadUnit
{
    /// Spotify links (playlists, albums, songs): the songs read from Spotify's own embed page (the one a playlist
    /// shows on other websites: up to 100 songs, no account or key), each then found on YouTube Music by yt-dlp's
    /// search, as spotDL does, and the videos go into the queue like any other. Nothing plays from Spotify.
    ///
    /// spotDL itself was too slow under Proton (2026-10-15: 30 s to read a 13-song album, then all 13 at once after
    /// 80 s, nothing to see before). Here the list takes a fraction of a second and one yt-dlp run searches the songs
    /// in order, the first in about 3 s, then under a second each; songs go into the queue as they are found.
    public static class Spotify
    {
        public static bool Busy;

        /// A Spotify playlist, album or song link (open.spotify.com, any language path; spotify:kind:id), cleaned; else null.
        public static string CleanLink(string text)
        {
            return Parse(text, out string kind, out string id) ? $"https://open.spotify.com/{kind}/{id}" : null;
        }

        private static bool Parse(string text, out string kind, out string id)
        {
            kind = id = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.StartsWith("spotify:"))
            {
                var p = text.Split(':');
                if (p.Length != 3) return false;
                kind = p[1]; id = p[2];
            }
            else
            {
                if (!text.StartsWith("http://") && !text.StartsWith("https://")) text = "https://" + text;
                if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Host.ToLowerInvariant() != "open.spotify.com") return false;
                var seg = u.AbsolutePath.Trim('/').Split('/');
                int k = seg.Length > 0 && seg[0].StartsWith("intl-") ? 1 : 0;
                if (seg.Length < k + 2) return false;
                kind = seg[k]; id = seg[k + 1];
            }
            return (kind == "playlist" || kind == "album" || kind == "track") && Regex.IsMatch(id, "^[A-Za-z0-9]{10,40}$");
        }

        public static bool Dump;   // a page with no songs saved to the cache folder (bridge set HeadUnit.Spotify.Dump True)

        private sealed class Song { public string title, subtitle; public float duration; }

        /// The songs in an embed page: its data block (__NEXT_DATA__) → props.pageProps.state.data.entity, its
        /// trackList (a playlist, an album) or the entity itself (one song). Read by hand: JsonUtility left every level
        /// below the top empty in game (2026-10-15).
        private static List<Song> Songs(string html)
        {
            var songs = new List<Song>();
            var m = Regex.Match(html, "<script id=\"__NEXT_DATA__\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline);
            if (!m.Success) return songs;
            string j = m.Groups[1].Value;
            const string EntityKey = "\"entity\":", ListKey = "\"trackList\":";
            int ent = j.IndexOf(EntityKey + "{", StringComparison.Ordinal);
            if (ent < 0) return songs;
            ent += EntityKey.Length;
            int entEnd = End(j, ent);
            if (entEnd < 0) return songs;
            int list = j.IndexOf(ListKey + "[", ent, StringComparison.Ordinal);
            if (list >= 0 && list < entEnd)
            {
                int i = list + ListKey.Length + 1;
                while (i < entEnd)
                {
                    while (i < entEnd && (j[i] == ',' || char.IsWhiteSpace(j[i]))) i++;
                    if (i >= entEnd || j[i] != '{') break;
                    int end = End(j, i);
                    if (end < 0) break;
                    songs.Add(Fields(j, i, end));
                    i = end + 1;
                }
            }
            else
            {
                // one song: its artists are a list of their own
                var song = Fields(j, ent, entEnd);
                int artists = j.IndexOf("\"artists\":[", ent, StringComparison.Ordinal);
                if (string.IsNullOrEmpty(song.subtitle) && artists >= 0 && artists < entEnd)
                {
                    int aEnd = End(j, artists + "\"artists\":".Length);
                    var names = new List<string>();
                    if (aEnd > 0)
                        foreach (Match n in Regex.Matches(j.Substring(artists, aEnd - artists), "\"name\":\"((?:[^\"\\\\]|\\\\.)*)\""))
                            names.Add(Unescape(n.Groups[1].Value));
                    song.subtitle = string.Join(", ", names);
                }
                songs.Add(song);
            }
            return songs;
        }

        /// The index of the } or ] closing the object or list that opens at `start`.
        private static int End(string j, int start)
        {
            int depth = 0;
            for (int i = start; i < j.Length; i++)
            {
                char c = j[i];
                if (c == '"') { i = StringEnd(j, i); if (i < 0) return -1; continue; }
                if (c == '{' || c == '[') depth++;
                else if ((c == '}' || c == ']') && --depth == 0) return i;
            }
            return -1;
        }

        private static int StringEnd(string j, int quote)
        {
            for (int i = quote + 1; i < j.Length; i++)
            {
                if (j[i] == '\\') { i++; continue; }
                if (j[i] == '"') return i;
            }
            return -1;
        }

        /// title, subtitle and duration (ms) at the top level of the object j[start..end].
        private static Song Fields(string j, int start, int end)
        {
            var song = new Song();
            int depth = 0;
            for (int i = start; i < end; i++)
            {
                char c = j[i];
                if (c == '{' || c == '[') { depth++; continue; }
                if (c == '}' || c == ']') { depth--; continue; }
                if (c != '"') continue;
                int close = StringEnd(j, i);
                if (close < 0) break;
                if (depth == 1 && close + 1 < end && j[close + 1] == ':')
                {
                    string key = j.Substring(i + 1, close - i - 1);
                    int v = close + 2;
                    if ((key == "title" || key == "subtitle") && v < end && j[v] == '"')
                    {
                        int ve = StringEnd(j, v);
                        if (ve < 0) break;
                        string text = Unescape(j.Substring(v + 1, ve - v - 1));
                        if (key == "title") song.title = text; else song.subtitle = text;
                        i = ve; continue;
                    }
                    if (key == "duration")
                    {
                        int ve = v;
                        while (ve < end && (char.IsDigit(j[ve]) || j[ve] == '.')) ve++;
                        float.TryParse(j.Substring(v, ve - v), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out song.duration);
                    }
                }
                i = close;
            }
            return song;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': case 'b': case 'f': break;
                    case 'u':
                        if (i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out int code)) { sb.Append((char)code); i += 4; }
                        break;
                    default: sb.Append(n); break;   // \" \\ \/
                }
            }
            return sb.ToString();
        }

        /// What a saved page gives (Probe).
        public static string ParseCheck(string html)
        {
            var songs = Songs(html);
            return $"{songs.Count} songs" + (songs.Count > 0 ? $": first {songs[0].subtitle} - {songs[0].title} {songs[0].duration / 1000f:F0} s, last {songs[songs.Count - 1].title}" : "");
        }

        /// The songs of a Spotify link, each as it is found on YouTube Music (`found`, on the main thread, in the
        /// link's order), then `done` with how many songs the link had and why it stopped early (null: it didn't).
        public static void Find(string link, Action<Ytdlp.Entry> found, Action<int, string> done)
        {
            if (Busy) { done(0, "still looking up the last Spotify link"); return; }
            if (!Parse(link, out string kind, out string id)) { done(0, "that isn't a Spotify link"); return; }
            if (Plugin.Runner == null) { done(0, "couldn't start"); return; }
            Busy = true;
            Plugin.Runner.StartCoroutine(Read(kind, id, found, (n, why) => { Busy = false; done(n, why); }));
        }

        private static System.Collections.IEnumerator Read(string kind, string id, Action<Ytdlp.Entry> found, Action<int, string> done)
        {
            string html = null, err = null;
            using (var req = UnityWebRequest.Get($"https://open.spotify.com/embed/{kind}/{id}"))
            {
                req.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) html = req.downloadHandler.text; else err = req.error;
            }
            if (html == null) { done(0, "Spotify: " + err); yield break; }
            var songs = new List<Song>();
            try { songs = Songs(html); }
            catch (Exception ex) { Plugin.Log.LogWarning("Spotify page: " + ex.Message); }
            songs.RemoveAll(s => s == null || string.IsNullOrEmpty(s.title));
            if (songs.Count == 0)
            {
                Plugin.Log.LogWarning($"Spotify {kind} {id}: no songs in a page of {html.Length} characters"
                    + (html.Contains("__NEXT_DATA__") ? " (with its data block)" : " (no data block)"));
                if (Dump) try { System.IO.File.WriteAllText(System.IO.Path.Combine(Ytdlp.CacheDir, "spotify-page.html"), html); } catch { }
                done(0, "Spotify: no songs found in that link (is it private?)"); yield break;
            }
            Panel.Flash($"Spotify: {songs.Count} songs, finding them on YouTube Music...");

            // one yt-dlp run, a YouTube Music song search per song, in order; each result names its search ("<query> - songs")
            var byQuery = new Dictionary<string, Song>();
            var args = new StringBuilder("--ignore-config --flat-playlist --no-warnings --ignore-errors --playlist-end 1 --print \"%(id)s\t%(playlist)s\"");
            foreach (var s in songs)
            {
                string q = Clean((s.subtitle ?? "") + " " + s.title);
                if (q.Length == 0 || byQuery.ContainsKey(q)) continue;
                byQuery[q] = s;
                args.Append(" \"https://music.youtube.com/search?q=").Append(Uri.EscapeDataString(q)).Append("#songs\"");
            }
            bool finished = false; string why = null;
            var env = new Dictionary<string, string> { { "PYTHONIOENCODING", "utf-8" } };   // song names as they are
            Ytdlp.Run(Ytdlp.Exe, args.ToString(), 15 * 60000, (code, outp, stderr) =>
            {
                if (code != 0 && code != 1) why = "yt-dlp: " + Ytdlp.FirstLine(stderr);   // 1: some searches failed (--ignore-errors)
                finished = true;
            }, line =>
            {
                var p = line.TrimEnd('\r').Split('\t');
                if (p.Length < 2 || !Ytdlp.ValidVideoId(p[0])) return;
                string q = p[1].EndsWith(" - songs") ? p[1].Substring(0, p[1].Length - 8) : p[1];
                if (!byQuery.TryGetValue(q, out var s)) return;
                var entry = new Ytdlp.Entry { Id = p[0], Title = s.subtitle + " - " + s.title, Duration = s.duration / 1000f };
                Ytdlp.OnMain(() => found(entry));
            }, env);
            while (!finished) yield return null;
            yield return null;   // the last songs' callbacks (queued on the main thread) first
            done(songs.Count, why);
        }

        /// A search text: no quotes or line breaks (it goes on yt-dlp's command line inside quotes).
        private static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) if (c != '"' && c != '\\' && !char.IsControl(c)) sb.Append(c);
            return Regex.Replace(sb.ToString(), "\\s+", " ").Trim();
        }
    }
}

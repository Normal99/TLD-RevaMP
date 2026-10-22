using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace HeadUnit
{
    /// yt-dlp, run on this machine: a link → the videos in it (id, title, length), and a video id → a URL the game's
    /// video player can open (a single file with sound, up to the configured height). Nothing passes between players
    /// but video ids: each machine asks YouTube itself.
    ///
    /// The programs: BepInEx/config/HeadUnit/yt-dlp.exe and deno.exe (the game runs as a Windows program, under Proton
    /// too). YouTube's files can only be fetched after solving its JavaScript challenges, which yt-dlp hands to Deno
    /// (without it: "Requested format is not available", or 403 on the file; playtest 2026-10-15). If either is
    /// missing, the player is offered a download of the official releases, each checked against its release's own
    /// SHA-256 before it is kept.
    ///
    /// A video is downloaded to BepInEx/cache/HeadUnit/<id>_<lines>.mp4 and played from there: the game's video player
    /// can't read YouTube's address under Proton (WindowsVideoMedia error 0xc00d36bb), a local file it plays.
    ///
    /// Quality: YouTube offers one file with both picture and sound only at 360p (format 18). Anything better comes as
    /// separate picture and sound, which ffmpeg joins into one mp4 without re-encoding (H.264 picture up to the
    /// configured lines, AAC sound at 128 kb/s: the best sound YouTube gives without Premium that the player decodes).
    /// ffmpeg is offered as a third download (BtbN's LGPL Windows build, checked the same way); without it, 360p.
    ///
    /// Valve's Proton (Steam's own, not GE-Proton) ships no H.264 or AAC decoder (no libavcodec beside its GStreamer:
    /// patents): for any such video the media framework hands the game a 4 s stand-in clip (Video.StandIn). The game's
    /// player decodes VP8 picture with Vorbis sound in a .webm itself, without the media framework (the laptop, Proton
    /// 11.0: a VP8 copy played its full 19.1 s where the mp4 gave the 4.0 s clip, videoprobe.py 2026-10-15). So once a
    /// stand-in is seen on a machine (Plugin.Convert, kept in its config), each video is converted there after the
    /// download, by the same ffmpeg (its LGPL build has libvpx and libvorbis), to <id>_<lines>.webm, and the mp4 deleted.
    public static class Ytdlp
    {
        public static string Dir, Exe, Deno, FfmpegDir, Ffmpeg, CacheDir;
        public const string FfmpegBase = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/";
        public const string FfmpegZip = "ffmpeg-master-latest-win64-lgpl-shared.zip";
        public static bool HasFfmpeg => Ffmpeg != null && File.Exists(Ffmpeg);
        /// The player said NOT NOW to ffmpeg (this game session): 360p without asking again.
        public static bool FfmpegDeclined;
        /// Offer the ffmpeg download (+ LINK) before the next link.
        public static bool OfferFfmpeg => Present && !HasFfmpeg && !FfmpegDeclined;
        public const string DenoZip = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
        /// The cache's size cap (the CacheGB setting): older videos go first.
        public static long CacheMaxBytes => (long)(Mathf.Max(0f, Plugin.CacheGB.Value) * 1024f * 1024f * 1024f);
        /// Download progress of the videos being fetched (by id): "40%".
        public static readonly ConcurrentDictionary<string, string> Progress = new ConcurrentDictionary<string, string>();
        public const string ReleaseBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
        public static bool Present => Exe != null && File.Exists(Exe) && File.Exists(Deno);
        public static string Status = "";
        public static bool Downloading;
        /// The yt-dlp / Deno download's progress, 0–1 (-1: none running).
        public static float DownloadFrac = -1f;

        private static readonly ConcurrentQueue<Action> MainThread = new ConcurrentQueue<Action>();
        /// The videos being fetched, and who waits for each (a prefetch, then the player when it comes up).
        private static readonly Dictionary<string, List<Action<string>>> Resolving = new Dictionary<string, List<Action<string>>>();

        /// Fetch a video ahead (the next in the queue while one plays: no wait between songs). One at a time, and only
        /// while nothing else is being fetched.
        public static bool PrefetchOn = true;   // runtime switch (bridge set HeadUnit.Ytdlp.PrefetchOn)
        public static void Prefetch(string videoId)
        {
            if (!PrefetchOn || !ValidVideoId(videoId) || !Present || Resolving.Count > 0 || PrefetchFailed.Contains(videoId)) return;
            bool merge = HasFfmpeg;
            int h = merge ? Mathf.Clamp(Plugin.MaxHeight.Value, 144, 1080) : 360;
            if (File.Exists(CachedFile(videoId, h))) return;
            Resolve(videoId, url => { if (url == null) PrefetchFailed.Add(videoId); });   // not again ahead: it is tried when it comes up
        }
        private static readonly HashSet<string> PrefetchFailed = new HashSet<string>();

        public static void Init(string configDir, string cacheDir)
        {
            Dir = Path.Combine(configDir, "HeadUnit");
            Directory.CreateDirectory(Dir);
            Exe = Path.Combine(Dir, "yt-dlp.exe");
            Deno = Path.Combine(Dir, "deno.exe");
            FfmpegDir = Path.Combine(Dir, "ffmpeg");
            Ffmpeg = Path.Combine(FfmpegDir, "ffmpeg.exe");
            CacheDir = Path.Combine(cacheDir, "HeadUnit");
            Directory.CreateDirectory(CacheDir);
        }

        /// `a` on the main thread (next frame).
        internal static void OnMain(Action a) => MainThread.Enqueue(a);

        public static void Tick() { while (MainThread.TryDequeue(out var a)) { try { a(); } catch (Exception e) { Plugin.Log.LogError("yt-dlp callback: " + e); } } }

        public static bool ValidVideoId(string id) => id != null && id.Length == 11 && Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$");

        /// A YouTube link (video, short, playlist, youtu.be), or null if it isn't one. Only these hosts are passed to
        /// yt-dlp: a link typed by a player never makes it fetch anything else.
        public static string CleanLink(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (ValidVideoId(text)) return "https://www.youtube.com/watch?v=" + text;
            if (!text.StartsWith("http://") && !text.StartsWith("https://")) text = "https://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var u)) return null;
            string host = u.Host.ToLowerInvariant();
            bool yt = host == "youtube.com" || host.EndsWith(".youtube.com") || host == "youtu.be" || host == "youtube-nocookie.com" || host.EndsWith(".youtube-nocookie.com");
            return yt ? u.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped) : null;
        }

        public struct Entry { public string Id, Title; public float Duration; }

        /// YouTube's videos for words (yt-dlp's ytsearch: its first SearchCount results, as YouTube's own search would
        /// list them), live streams left out (nothing to download). Callback on the main thread; null on failure.
        public static int SearchCount = 15;
        public static void Search(string words, Action<List<Entry>> done)
        {
            string q = Regex.Replace(words ?? "", "[\"\r\n\t]", " ").Trim();
            if (q.Length == 0) { done(new List<Entry>()); return; }
            List($"ytsearch{SearchCount}:{q}", list => done(list?.FindAll(e => e.Duration > 0)));
        }

        /// The videos in a link (a playlist gives up to 100). Callback on the main thread; null on failure (Status says why).
        public static void List(string link, Action<List<Entry>> done)
        {
            if (!Present) { Status = "yt-dlp is not installed"; done(null); return; }
            var args = "--flat-playlist --playlist-end 100 --no-warnings --print \"%(id)s\t%(title)s\t%(duration)s\" \"" + link + "\"";
            Run(args, 60000, (code, outp, err) =>
            {
                if (code != 0) { Status = "yt-dlp: " + FirstLine(err); done(null); return; }
                var list = new List<Entry>();
                foreach (var line in outp.Split('\n'))
                {
                    var p = line.TrimEnd('\r').Split('\t');
                    if (p.Length < 2 || !ValidVideoId(p[0])) continue;
                    float.TryParse(p.Length > 2 ? p[2] : "", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dur);
                    list.Add(new Entry { Id = p[0], Title = p[1], Duration = dur });
                }
                if (list.Count == 0) Status = "no videos in that link";
                done(list);
            });
        }

        /// A playable file for a video: downloaded once to the cache, then its file:/// address. Callback on the main
        /// thread; null on failure (Status says why). Progress[videoId] meanwhile.
        public static void Resolve(string videoId, Action<string> done)
        {
            if (!ValidVideoId(videoId)) { done(null); return; }
            bool merge = HasFfmpeg;
            int h = merge ? Mathf.Clamp(Plugin.MaxHeight.Value, 144, 1080) : 360;
            string file = Path.Combine(CacheDir, $"{videoId}_{h}.mp4");
            bool convert = Plugin.Convert.Value;
            string ready = CachedFile(videoId, h);
            if (File.Exists(ready)) { try { File.SetLastWriteTimeUtc(ready, DateTime.UtcNow); } catch { } done(FileUrl(ready)); return; }
            if (convert && !HasFfmpeg) { Status = Video.NoCodecs; done(null); return; }
            if (!Present && !File.Exists(file)) { Status = "yt-dlp is not installed"; done(null); return; }
            if (Resolving.TryGetValue(videoId, out var waiting)) { waiting.Add(done); return; }   // already coming
            Resolving[videoId] = new List<Action<string>> { done };
            void Done(string url)
            {
                Progress.TryRemove(videoId, out _);
                if (!Resolving.TryGetValue(videoId, out var all)) return;
                Resolving.Remove(videoId);
                foreach (var d in all) { try { d(url); } catch (Exception e) { Plugin.Log.LogError("video callback: " + e); } }
            }
            Trim();
            void Got()
            {
                if (!convert) { Done(FileUrl(file)); return; }
                ToVp8(videoId, file, ready, h, ok => { if (!ok) Status = "couldn't convert the video: " + ConvertWhy; Done(ok ? FileUrl(ready) : null); });
            }
            if (File.Exists(file)) { Got(); return; }   // downloaded before this machine needed the copy
            // H.264 in mp4 with AAC sound (what the game's player decodes under Proton), in one file: picture and sound
            // joined by ffmpeg when it's there, else YouTube's one combined file (18 = 360p)
            string single = $"best[height<={h}][ext=mp4][vcodec^=avc1][acodec!=none]/18";
            string fmt = merge ? $"bv*[height<={h}][vcodec^=avc1][ext=mp4]+ba[acodec^=mp4a][ext=m4a]/{single}" : single;
            var clients = merge ? MergeClients : Clients;
            Progress[videoId] = "0%";
            Try(0);
            void Try(int k)
            {
                string client = k < clients.Length ? clients[k] : null;
                string extra = client != null ? $"--extractor-args \"youtube:player_client={client}\" " : "";
                string ff = merge ? $"--ffmpeg-location \"{FfmpegDir}\" --merge-output-format mp4 " : "";
                string args = $"--ignore-config -f \"{fmt}\" -o \"{file}\" --no-playlist --no-warnings --no-update --newline --progress-template \"download:%(progress._percent_str)s\" "
                    + $"--js-runtimes \"deno:{Deno}\" {ff}{extra}\"https://www.youtube.com/watch?v={videoId}\"";
                Run(args, 20 * 60000, (code, outp, err) =>
                {
                    if (code != 0 || !File.Exists(file))
                    {
                        Plugin.Log.LogWarning($"yt-dlp {videoId} ({client ?? "default clients"}): {FirstLine(err)}");
                        if (k < clients.Length) { Try(k + 1); return; }
                        Status = "yt-dlp: " + FirstLine(err); Done(null); return;
                    }
                    Got();
                }, line => { var t = line.Trim(); if (t.EndsWith("%")) Progress[videoId] = t; });
            }
        }

        /// The file a video plays from on this machine: the mp4, or its VP8 copy where the mp4 doesn't play.
        public static string CachedFile(string videoId, int h) => Path.Combine(CacheDir, $"{videoId}_{h}.{(Plugin.Convert.Value ? "webm" : "mp4")}");

        /// The VP8 copy's picture bitrate by its lines; at most 720 lines (VP8 is slow to encode).
        public static int ConvertMaxLines = 720;
        private static string ConvertWhy = "";

        /// mp4 → VP8/Vorbis webm (ffmpeg, its fastest VP8 setting), then the mp4 deleted. Progress[videoId] "c<percent>".
        private static void ToVp8(string videoId, string mp4, string webm, int lines, Action<bool> done)
        {
            string tmp = webm.Substring(0, webm.Length - 5) + ".tmp.webm";
            double seconds = Video.Mp4Seconds(mp4);
            int h = Mathf.Min(lines, ConvertMaxLines);
            int kbps = h <= 360 ? 1000 : h <= 480 ? 1500 : 2500;
            string scale = lines > ConvertMaxLines ? $"-vf \"scale=-2:min({ConvertMaxLines}\\,ih)\" " : "";   // the downloaded file is at most `lines` high
            Progress[videoId] = "c0";
            var t0 = DateTime.UtcNow;
            string args = $"-hide_banner -nostats -v error -y -i \"{mp4}\" {scale}-c:v libvpx -deadline realtime -cpu-used 8 -threads {Mathf.Clamp(SystemInfo.processorCount, 1, 8)} "
                + $"-b:v {kbps}k -maxrate {kbps * 3 / 2}k -bufsize {kbps * 2}k -c:a libvorbis -q:a 5 -progress pipe:1 \"{tmp}\"";
            Run(Ffmpeg, args, 30 * 60000, (code, outp, err) =>
            {
                bool ok = code == 0 && File.Exists(tmp);
                try
                {
                    if (ok) { if (File.Exists(webm)) File.Delete(webm); File.Move(tmp, webm); File.Delete(mp4); }
                    else if (File.Exists(tmp)) File.Delete(tmp);
                }
                catch (Exception e) { ok = ok && File.Exists(webm); Plugin.Log.LogWarning("video copy: " + e.Message); }
                ConvertWhy = ok ? "" : FirstLine(err);
                Plugin.Log.LogInfo($"video {videoId}: {(ok ? "VP8 copy made" : "VP8 copy failed: " + ConvertWhy)} ({seconds:F0} s of video, {h} lines, in {(DateTime.UtcNow - t0).TotalSeconds:F0} s)");
                done(ok);
            }, line =>
            {
                // -progress: out_time_us=<microseconds> (out_time_ms is the same in microseconds too)
                if (seconds > 0 && line.StartsWith("out_time_us=") && long.TryParse(line.Substring(12), out long us))
                    Progress[videoId] = "c" + Mathf.Clamp(Mathf.RoundToInt((float)(us / 1e6 / seconds * 100.0)), 0, 99);
            });
        }

        /// A video's conversion progress, 0–1, or -1 if it isn't being converted.
        public static float ConvertFrac(string id)
        {
            if (id == null || !Progress.TryGetValue(id, out var p) || !p.StartsWith("c")) return -1f;
            return int.TryParse(p.Substring(1), out int v) ? v / 100f : 0f;
        }

        /// The YouTube clients asked in turn for a video's file, then yt-dlp's own choice (mweb gave the 360p file
        /// with Deno when the default ones were refused, 2026-10-15).
        public static string[] Clients = { "mweb" };
        /// With ffmpeg: yt-dlp's own clients first (they offer the separate 720p/1080p picture and the AAC sound; mweb
        /// doesn't without a token), then mweb's 360p file.
        public static string[] MergeClients = { null, "mweb" };

        private static string FileUrl(string path) => "file:///" + path.Replace('\\', '/');

        /// The cache kept under CacheMaxBytes: the least recently played videos go first. Never one that plays or comes
        /// next on a unit nearby (UnitBehaviour.VideosInUse), whatever the cap (0 keeps only those).
        private static void Trim()
        {
            try
            {
                var inUse = UnitBehaviour.VideosInUse();
                var files = new List<FileInfo>();
                foreach (var f in new DirectoryInfo(CacheDir).GetFiles())
                    // finished videos only (not a download's pieces: name.f136.mp4, nor a copy being made: name.tmp.webm)
                    if (Regex.IsMatch(f.Name, @"^[A-Za-z0-9_-]{11}(_\d+)?\.(mp4|webm)$")) files.Add(f);
                long total = 0; foreach (var f in files) total += f.Length;
                files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                foreach (var f in files)
                {
                    if (total <= CacheMaxBytes) break;
                    if (inUse.Contains(f.Name.Substring(0, 11))) continue;
                    total -= f.Length;
                    try { f.Delete(); } catch (Exception e) { Plugin.Log.LogWarning("video cache: " + f.Name + ": " + e.Message); }
                }
                // what a stopped download leaves: parts, the separate picture and sound before they're joined
                foreach (var pat in new[] { "*.part", "*.m4a", "*.f*.mp4", "*.temp.mp4", "*.ytdl", "*.tmp.webm" })
                    foreach (var f in new DirectoryInfo(CacheDir).GetFiles(pat)) if (DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromHours(1)) f.Delete();
            }
            catch (Exception e) { Plugin.Log.LogWarning("video cache: " + e.Message); }
        }

        private static void Run(string args, int timeoutMs, Action<int, string, string> done, Action<string> line = null) =>
            Run(Exe, args, timeoutMs, done, line);

        /// The programs running now (yt-dlp, ffmpeg): ended with the game (KillAll), not left converting after it.
        private static readonly ConcurrentDictionary<int, Process> Running = new ConcurrentDictionary<int, Process>();

        /// Runs a program on a worker thread; `done` on the main thread, `line` (each line of its output) on the worker.
        /// Its output is read as it comes, so the timeout holds even when the program hangs with its output open (read
        /// to the end first, a hung yt-dlp never timed out: its video never loaded and prefetching stopped for good).
        internal static void Run(string exe, string args, int timeoutMs, Action<int, string, string> done, Action<string> line = null,
                                 IDictionary<string, string> env = null)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int code = -1; string outp = "", err = "";
                try
                {
                    var psi = new ProcessStartInfo(exe, args)
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = Dir,
                    };
                    if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
                    var outSb = new StringBuilder(); var errSb = new StringBuilder();
                    using (var p = new Process { StartInfo = psi })
                    {
                        p.OutputDataReceived += (o, e) =>
                        {
                            if (e.Data == null) return;
                            lock (outSb) outSb.AppendLine(e.Data);
                            if (line != null) try { line(e.Data); } catch { }
                        };
                        p.ErrorDataReceived += (o, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
                        p.Start();
                        Running[p.Id] = p;
                        p.BeginOutputReadLine(); p.BeginErrorReadLine();
                        try
                        {
                            if (!p.WaitForExit(timeoutMs)) { KillTree(p); err = "timed out"; }
                            else
                            {
                                p.WaitForExit();   // the output read to its end
                                code = p.ExitCode;
                            }
                        }
                        finally { Running.TryRemove(p.Id, out var _); }
                    }
                    lock (outSb) outp = outSb.ToString();
                    if (err.Length == 0) lock (errSb) err = errSb.ToString();
                }
                catch (Exception e) { err = e.Message; }
                MainThread.Enqueue(() => done(code, outp, err));
            });
        }

        /// The program and what it started (yt-dlp's Deno and ffmpeg): Process.Kill ends only the one.
        private static void KillTree(Process p)
        {
            try
            {
                using (var k = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {p.Id}") { UseShellExecute = false, CreateNoWindow = true }))
                    k?.WaitForExit(5000);
            }
            catch { }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        /// The game is closing: every program still running ends with it.
        public static void KillAll() { foreach (var p in Running.Values) KillTree(p); }

        internal static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            foreach (var l in s.Split('\n')) { var t = l.Trim(); if (t.Length > 0) return t.Length > 160 ? t.Substring(0, 160) : t; }
            return "";
        }

        // ------------------------------------------------------------------ getting yt-dlp

        /// Download the official yt-dlp.exe and keep it only if its SHA-256 is the one in the same release's SHA2-256SUMS.
        /// Only after the player agreed (Panel).
        public static void Download()
        {
            if (Downloading) return;
            if (Plugin.Runner == null) { Status = "yt-dlp download couldn't start"; Panel.Flash(Status); return; }
            Downloading = true; Status = "downloading yt-dlp...";
            Plugin.Runner.StartCoroutine(DownloadRoutine());
        }

        internal static void Fail(string why) { Status = why; Downloading = false; DownloadFrac = -1f; Panel.Flash(why); Plugin.Log.LogWarning(why); }

        /// A video's download progress, 0–1, or -1 if it isn't downloading.
        public static float VideoFrac(string id)
        {
            if (id == null || !Progress.TryGetValue(id, out var p) || p.StartsWith("c")) return -1f;
            return float.TryParse(p.Trim().TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f / 100f : 0f;
        }

        private static System.Collections.IEnumerator DownloadRoutine()
        {
            if (!File.Exists(Exe)) { yield return GetYtdlp(); if (!File.Exists(Exe)) yield break; }
            if (!File.Exists(Deno)) { yield return GetDeno(); if (!File.Exists(Deno)) yield break; }
            if (!HasFfmpeg) { yield return GetFfmpeg(); if (!HasFfmpeg) yield break; }
            Status = "ready: press + ADD"; Panel.Flash(Status);
            Downloading = false;
        }

        private static System.Collections.IEnumerator GetYtdlp()
        {
            string sums = null;
            Plugin.Log.LogInfo("downloading yt-dlp from " + ReleaseBase);
            using (var req = UnityWebRequest.Get(ReleaseBase + "SHA2-256SUMS"))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) Plugin.Log.LogWarning("yt-dlp checksums: " + req.error);
                if (req.result == UnityWebRequest.Result.Success) sums = req.downloadHandler.text;
            }
            string want = null;
            if (sums != null)
                foreach (var line in sums.Split('\n'))
                {
                    var p = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 2 && p[1].TrimStart('*') == "yt-dlp.exe") want = p[0].ToLowerInvariant();
                }
            if (want == null) { Fail("could not read yt-dlp's checksums: not downloaded"); yield break; }
            byte[] data = null;
            yield return Fetch(ReleaseBase + "yt-dlp.exe", "yt-dlp", want, d => data = d);
            if (data == null) yield break;
            try { Keep(Exe, data); }
            catch (Exception ex) { Fail("yt-dlp couldn't be saved: " + ex.Message); yield break; }
            Plugin.Log.LogInfo($"yt-dlp downloaded and verified (sha256 {want.Substring(0, 12)}) to {Exe}");
        }

        /// Deno (Windows build, official release) for yt-dlp's JavaScript challenges: the zip checked against the
        /// release's .sha256sum, deno.exe taken out of it.
        private static System.Collections.IEnumerator GetDeno()
        {
            string want = null;
            Plugin.Log.LogInfo("downloading Deno from " + DenoZip);
            using (var req = UnityWebRequest.Get(DenoZip + ".sha256sum"))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    var m = Regex.Match(req.downloadHandler.text, "[0-9A-Fa-f]{64}");
                    if (m.Success) want = m.Value.ToLowerInvariant();
                }
                else Plugin.Log.LogWarning("Deno checksum: " + req.error);
            }
            if (want == null) { Fail("could not read Deno's checksum: not downloaded"); yield break; }
            byte[] data = null;
            yield return Fetch(DenoZip, "Deno", want, d => data = d);
            if (data == null) yield break;
            try
            {
                using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(data)))
                {
                    var e = zip.GetEntry("deno.exe");
                    if (e == null) { Fail("Deno's download has no deno.exe"); yield break; }
                    using (var src = e.Open()) using (var dst = File.Create(Deno + ".part")) src.CopyTo(dst);
                }
                if (File.Exists(Deno)) File.Delete(Deno);
                File.Move(Deno + ".part", Deno);
                Plugin.Log.LogInfo($"Deno downloaded and verified (sha256 {want.Substring(0, 12)}) to {Deno}");
            }
            catch (Exception ex) { Fail("Deno couldn't be unpacked: " + ex.Message); }
        }

        /// ffmpeg (BtbN's LGPL build with shared libraries, the smallest: about 80 MB) for joining picture and sound: the
        /// zip checked against the release's checksums.sha256, its programs and libraries (not ffplay) taken out.
        private static System.Collections.IEnumerator GetFfmpeg()
        {
            string want = null;
            Plugin.Log.LogInfo("downloading ffmpeg from " + FfmpegBase + FfmpegZip);
            using (var req = UnityWebRequest.Get(FfmpegBase + "checksums.sha256"))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                    foreach (var line in req.downloadHandler.text.Split('\n'))
                    {
                        var p = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length == 2 && p[1].TrimStart('*') == FfmpegZip) want = p[0].ToLowerInvariant();
                    }
                else Plugin.Log.LogWarning("ffmpeg checksums: " + req.error);
            }
            if (want == null) { Fail("could not read ffmpeg's checksums: not downloaded"); yield break; }
            byte[] data = null;
            yield return Fetch(FfmpegBase + FfmpegZip, "ffmpeg", want, d => data = d);
            if (data == null) yield break;
            Status = "unpacking ffmpeg...";
            yield return null;
            try
            {
                string tmp = FfmpegDir + ".part";
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                Directory.CreateDirectory(tmp);
                using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(data)))
                    foreach (var e in zip.Entries)
                    {
                        var parts = e.FullName.Split('/');
                        if (parts.Length != 3 || parts[1] != "bin" || parts[2].Length == 0 || parts[2] == "ffplay.exe") continue;
                        if (!parts[2].EndsWith(".exe") && !parts[2].EndsWith(".dll")) continue;
                        using (var src = e.Open()) using (var dst = File.Create(Path.Combine(tmp, parts[2]))) src.CopyTo(dst);
                    }
                if (!File.Exists(Path.Combine(tmp, "ffmpeg.exe"))) { Fail("ffmpeg's download has no ffmpeg.exe"); yield break; }
                if (Directory.Exists(FfmpegDir)) Directory.Delete(FfmpegDir, true);
                Directory.Move(tmp, FfmpegDir);
                Plugin.Log.LogInfo($"ffmpeg downloaded and verified (sha256 {want.Substring(0, 12)}) to {FfmpegDir}");
            }
            catch (Exception ex) { Fail("ffmpeg couldn't be unpacked: " + ex.Message); }
        }

        internal static System.Collections.IEnumerator Fetch(string url, string what, string sha256, Action<byte[]> got)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    DownloadFrac = Mathf.Clamp01(req.downloadProgress);
                    Status = $"downloading {what}... {req.downloadedBytes / 1048576f:0.0} MB" + (req.downloadProgress > 0f ? $" ({Mathf.RoundToInt(req.downloadProgress * 100f)}%)" : "");
                    yield return null;
                }
                DownloadFrac = -1f;
                if (req.result != UnityWebRequest.Result.Success) { Fail($"{what} download failed: " + req.error); yield break; }
                var data = req.downloadHandler.data;
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
                if (hash != sha256) { Fail($"{what}'s file didn't match its checksum: thrown away"); yield break; }
                got(data);
            }
        }

        internal static void Keep(string path, byte[] data)
        {
            File.WriteAllBytes(path + ".part", data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(path + ".part", path);
        }
    }
}

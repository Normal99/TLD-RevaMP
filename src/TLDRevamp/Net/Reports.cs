using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Debug reports between players. A player's report (FeedbackReporter: Page Down) stays on their machine unless they
    /// turned on "Send my reports to the host" (Share, opt-in): then a copy of the zip goes to the host, in chunks,
    /// throttled so it never holds up the game's own reliable traffic (one ordered stream per connection). The host
    /// saves it in BepInEx/tldrevamp-feedback/players. A host saving a report asks the players for theirs (AskPlayers), so
    /// a problem is captured on every machine at the same moment; only sharing players answer.
    public static class Reports
    {
        public const byte ReportAsk = 60;     // server → client: str note, f64 momentSt
        public const byte ReportChunk = 61;   // client → server: u32 transfer, u32 total, u32 offset, u8 mark, bytes

        public static bool Share;              // Settings → Revamp "Send my reports to the host" (off by default)
        public static bool AskPlayers = true;  // Settings → Revamp "Ask players for their reports"
        public const int MaxBytes = 24 << 20;  // a report is ~3 MB (most of it the screenshot)
        public const int ChunkBytes = 16 * 1024;
        public static int BytesPerSecond = 160 * 1024;   // ~3 MB in 20 s; the game's traffic is a few KB/s
        public const int MaxPerPlayer = 20;    // per session, on the host: a stuck key doesn't fill its disk
        /// The host: reports STARTED per player (by their authenticated Steam account: a reconnect doesn't reset it),
        /// at most one start every StartGapS, the buffer grown as the chunks come (a start alone allocated the whole
        /// announced size, up to 24 MiB), and the players folder kept under PlayersFolderMaxBytes (oldest first out).
        public static float StartGapS = 30f;
        public static long PlayersFolderMaxBytes = 200L << 20;
        /// A client: the host's asks at most every AskGapS (each makes a report here: screenshot, logs, disk).
        public static float AskGapS = 30f;
        private static float _lastAskAt = -1000f;

        public static long AsksSent, AsksIn, AsksIgnored, ChunksSent, ChunksIn, ReportsSent, ReportsReceived, ReportsDropped;
        public static string LastReceived = "", LastDrop = "";

        // ---- this machine making a report
        private static bool _sendNext;         // the zip being made goes to the host when it's done
        private static bool _askHintShown;

        /// The local player's report (the key, the bridge): saved here; to the host too when shared; asks the players
        /// when this machine hosts.
        public static void LocalReport(string note)
        {
            if (Plugin.Instance.Feedback.Pending) return;
            double moment = SessionLog.St;
            if (Plugin.Instance.Feedback.Capture(note, moment) == null) return;
            if (Mp.Server != null && AskPlayers && Mp.Server.ReadyCount() > 0)
            {
                var w = new NetWriter(); w.U8(ReportAsk); w.Str(note); w.F64(moment);
                Mp.Server.SendToAll(w, true, -1, null, 0);
                AsksSent++;
                Session.Show(1, "", "Debug report saved. Asked the other players for theirs.");
            }
            else if (Mp.Client != null && Share) { _sendNext = true; Session.Show(1, "", "Debug report saved. Sending a copy to the host…"); }
        }

        /// Client: the host saved a report.
        internal static void ClientAsk(NetReader r)
        {
            string note = r.Str();
            double moment = r.F64();
            if (r.Bad || double.IsNaN(moment) || double.IsInfinity(moment)) return;
            AsksIn++;
            if (Plugin.Instance.Feedback.Pending) { AsksIgnored++; return; }
            if (Time.realtimeSinceStartup - _lastAskAt < AskGapS) { AsksIgnored++; return; }
            _lastAskAt = Time.realtimeSinceStartup;
            if (!Share)
            {
                AsksIgnored++;
                if (!_askHintShown)
                {
                    _askHintShown = true;
                    Session.Show(1, "", "The host saved a debug report. To send them yours too, turn on Settings → Revamp → \"Send my reports to the host\".");
                }
                return;
            }
            Plugin.Instance.Feedback.Capture("asked by the host" + (string.IsNullOrEmpty(note) ? "" : ": " + note), moment);
            _sendNext = true;
            Session.Show(1, "", "The host saved a debug report: sending yours.");
        }

        /// FeedbackReporter: a report's zip is written.
        internal static void OnZipped(string zip)
        {
            if (!_sendNext) return;
            _sendNext = false;
            if (Mp.Client == null || !Share) return;
            try
            {
                var b = File.ReadAllBytes(zip);
                if (b.Length == 0 || b.Length > MaxBytes) { Session.Show(1, "", "The report is too big to send (" + Mb(b.Length) + "); it's saved here."); return; }
                QueueUpload(b);
            }
            catch (Exception e) { Plugin.Log.LogWarning("report upload: " + e.Message); }
        }

        // ---- upload (client)
        private static byte[] _up; private static int _upOff; private static uint _upId; private static float _budget;
        private static readonly NetWriter _w = new NetWriter();
        private sealed class Upload { internal byte[] Bytes; internal MpClient Owner; internal bool Mark; }
        private static readonly ConcurrentQueue<Upload> Uploads = new ConcurrentQueue<Upload>();
        private static bool _upMark;
        private static float _nextUpload;
        private static void QueueUpload(byte[] bytes, bool mark = false, MpClient owner = null)
        {
            if (Uploads.Count < 8) Uploads.Enqueue(new Upload { Bytes = bytes, Owner = owner ?? Mp.Client, Mark = mark });
        }
        internal static void MarkSaved(string path)
        {
            if (Mp.Client == null || !Share) return;
            var owner = Mp.Client;
            Task.Run(() =>
            {
                try
                {
                    using (var memory = new MemoryStream())
                    {
                        using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, true))
                        using (var entry = zip.CreateEntry("blackbox.json").Open())
                        using (var file = File.OpenRead(path)) file.CopyTo(entry);
                        if (memory.Length <= MaxBytes) QueueUpload(memory.ToArray(), true, owner);
                    }
                }
                catch (Exception e) { SessionLog.Fail("mark upload", e); }
            });
        }

        /// Runner, every frame.
        public static void Tick()
        {
            if (Mp.Client == null || !Share) { _up = null; if (!Uploads.IsEmpty) Uploads.Clear(); return; }
            if (_up == null && Time.realtimeSinceStartup >= _nextUpload && Uploads.TryDequeue(out var upload))
            {
                if (upload.Owner != Mp.Client) return;
                _up = upload.Bytes; _upMark = upload.Mark; _upOff = 0; _upId = (uint)UnityEngine.Random.Range(1, int.MaxValue); _budget = ChunkBytes;
                _nextUpload = Time.realtimeSinceStartup + Math.Max(10.1f, StartGapS);
            }
            if (_up == null) return;
            if (Mp.Client == null) { _up = null; return; }   // the session ended: the report stays on this machine
            _budget = Mathf.Min(_budget + BytesPerSecond * Time.unscaledDeltaTime, ChunkBytes * 4);
            while (_up != null && _budget >= ChunkBytes)
            {
                int n = Math.Min(ChunkBytes, _up.Length - _upOff);
                _w.Reset(); _w.U8(ReportChunk); _w.U32(_upId); _w.U32((uint)_up.Length); _w.U32((uint)_upOff); _w.U8(_upMark ? (byte)1 : (byte)0); _w.Bytes(_up, _upOff, n);
                Mp.Client.SendRaw(_w, SteamTransport.SendReliable);
                ChunksSent++; _upOff += n; _budget -= ChunkBytes;
                if (_upOff >= _up.Length)
                {
                    ReportsSent++;
                    Session.Show(1, "", "Report sent to the host (" + Mb(_up.Length) + ").");
                    _up = null;
                }
            }
        }

        // ---- the host receiving
        private sealed class Incoming { public uint Id; public int Total; public byte[] Buf; public int Got; public bool Mark; }
        private static readonly Dictionary<int, Incoming> _in = new Dictionary<int, Incoming>();
        private static readonly Dictionary<ulong, int> _count = new Dictionary<ulong, int>();
        private static readonly Dictionary<ulong, float> _startedAt = new Dictionary<ulong, float>();
        private static readonly Dictionary<ulong, float> _markStartedAt = new Dictionary<ulong, float>();
        private static readonly Dictionary<int, uint> _droppedId = new Dictionary<int, uint>();   // its later chunks: ignored, not logged one by one

        /// `who`: the player's authenticated Steam account (0 without one: counted by the connection's player id).
        internal static void ServerChunk(int from, ulong who, string name, NetReader r)
        {
            uint id = r.U32(); int total = (int)r.U32(), off = (int)r.U32();
            byte mark = r.U8();
            int n = r.Remaining;
            if (r.Bad || n <= 0 || mark > 1) return;
            ChunksIn++;
            ulong key = who != 0 ? who : 0xFFFFFFFF00000000UL | (uint)from;
            if (off != 0 && _droppedId.TryGetValue(from, out uint dropped) && dropped == id) return;
            _in.TryGetValue(from, out var cur);
            if (off == 0)
            {
                // a new report from this player (one at a time: a newer one replaces an unfinished one)
                _count.TryGetValue(key, out int c);
                float now = Time.realtimeSinceStartup;
                if (total <= 0 || total > MaxBytes || c >= MaxPerPlayer) { Drop(from, id, name, "size " + total + ", " + c + " this session"); return; }
                var started = mark == 1 ? _markStartedAt : _startedAt;
                float gap = mark == 1 ? 10f : StartGapS;
                if (started.TryGetValue(key, out float at) && now - at < gap) { Drop(from, id, name, "another one " + (now - at).ToString("F0") + " s after the last"); return; }
                started[key] = now; _count[key] = c + 1;
                cur = new Incoming { Id = id, Total = total, Buf = new byte[Math.Min(total, 256 << 10)], Mark = mark == 1 };
                _in[from] = cur;
            }
            // reliable and ordered: anything else is a broken transfer
            if (cur == null || cur.Id != id || off != cur.Got || total != cur.Total || cur.Mark != (mark == 1) || n > cur.Total - cur.Got) { Drop(from, id, name, "out of order at " + off); return; }
            if (cur.Got + n > cur.Buf.Length)
            {
                var grown = new byte[Math.Min(cur.Total, Math.Max(cur.Got + n, cur.Buf.Length * 2))];
                Buffer.BlockCopy(cur.Buf, 0, grown, 0, cur.Got);
                cur.Buf = grown;
            }
            Buffer.BlockCopy(r.Buf, r.Pos, cur.Buf, cur.Got, n);
            cur.Got += n;
            if (cur.Got < cur.Total) return;
            _in.Remove(from);
            try
            {
                string dir = Path.Combine(FeedbackReporter.Root, "players");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, FileName(name, from) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
                KeepFolderUnder(dir, PlayersFolderMaxBytes - cur.Total);
                File.WriteAllBytes(file, cur.Buf);
                ReportsReceived++; LastReceived = file;
                Plugin.Log.LogInfo("Report from " + name + " (player " + from + "): " + file);
                Session.Show(1, "", "Debug report from " + name + " saved (" + Mb(cur.Buf.Length) + "): BepInEx/tldrevamp-feedback/players");
            }
            catch (Exception e) { Plugin.Log.LogWarning("report from " + name + ": " + e.Message); }
        }

        /// The oldest reports go until the folder's zips take at most `max` bytes.
        private static void KeepFolderUnder(string dir, long max)
        {
            var files = new List<FileInfo>(new DirectoryInfo(dir).GetFiles("*.zip"));
            long sum = 0; foreach (var f in files) sum += f.Length;
            if (sum <= max) return;
            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            foreach (var f in files)
            {
                if (sum <= max) break;
                try { long len = f.Length; f.Delete(); sum -= len; Plugin.Log.LogInfo("players' reports over " + (PlayersFolderMaxBytes >> 20) + " MB: removed the oldest, " + f.Name); }
                catch (Exception e) { Plugin.Log.LogWarning("players' reports: " + f.Name + " not removed: " + e.Message); }
            }
        }

        private static void Drop(int from, uint id, string name, string why)
        {
            _in.Remove(from); _droppedId[from] = id;
            ReportsDropped++; LastDrop = name + ": " + why;
            Plugin.Log.LogWarning("report from " + name + " dropped: " + why);
        }

        internal static void ServerLeft(int id) { _in.Remove(id); _droppedId.Remove(id); }   // an unfinished report from a player who left

        internal static void Reset()
        {
            _in.Clear(); _count.Clear(); _startedAt.Clear(); _markStartedAt.Clear(); _droppedId.Clear(); _up = null; Uploads.Clear(); _nextUpload = 0; _lastAskAt = -1000;
            _sendNext = false; _askHintShown = false;
        }

        /// A player's name as a file name: letters, digits, '-' and '_' only.
        private static string FileName(string name, int id)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in name ?? "") if (sb.Length < 32 && (char.IsLetterOrDigit(ch) && ch < 128 || ch == '-' || ch == '_')) sb.Append(ch);
            return sb.Length > 0 ? sb.ToString() : "player" + id;
        }

        private static string Mb(int bytes) => (bytes / 1048576f).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " MB";

        public static string Stats() =>
            "{\"share\":" + (Share ? "true" : "false") + ",\"askPlayers\":" + (AskPlayers ? "true" : "false") + ",\"asksSent\":" + AsksSent +
            ",\"asksIn\":" + AsksIn + ",\"asksIgnored\":" + AsksIgnored + ",\"chunksSent\":" + ChunksSent + ",\"chunksIn\":" + ChunksIn +
            ",\"sent\":" + ReportsSent + ",\"received\":" + ReportsReceived + ",\"dropped\":" + ReportsDropped +
            ",\"uploading\":" + (_up != null ? Json.Str(_upOff + "/" + _up.Length) : "null") +
            ",\"lastReceived\":" + Json.Str(LastReceived) + ",\"lastDrop\":" + Json.Str(LastDrop) + "}";
    }
}

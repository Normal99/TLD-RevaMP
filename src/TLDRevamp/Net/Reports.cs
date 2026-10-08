using System;
using System.Collections.Generic;
using System.IO;
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
        public const byte ReportAsk = 60;     // server → client: str note
        public const byte ReportChunk = 61;   // client → server: u32 transfer, u32 total, u32 offset, bytes (the rest)

        public static bool Share;              // Settings → Revamp "Send my reports to the host" (off by default)
        public static bool AskPlayers = true;  // Settings → Revamp "Ask players for their reports"
        public const int MaxBytes = 24 << 20;  // a report is ~3 MB (most of it the screenshot)
        public const int ChunkBytes = 16 * 1024;
        public static int BytesPerSecond = 160 * 1024;   // ~3 MB in 20 s; the game's traffic is a few KB/s
        public const int MaxPerPlayer = 20;    // per session, on the host: a stuck key doesn't fill its disk

        public static long AsksSent, AsksIn, AsksIgnored, ChunksSent, ChunksIn, ReportsSent, ReportsReceived, ReportsDropped;
        public static string LastReceived = "", LastDrop = "";

        // ---- this machine making a report
        private static bool _sendNext;         // the zip being made goes to the host when it's done
        private static bool _askHintShown;

        /// The local player's report (the key, the bridge): saved here; to the host too when shared; asks the players
        /// when this machine hosts.
        public static void LocalReport(string note)
        {
            Plugin.Instance.Feedback.Capture(note);
            if (Mp.Server != null && AskPlayers && Mp.Server.ReadyCount() > 0)
            {
                var w = new NetWriter(); w.U8(ReportAsk); w.Str(note);
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
            if (r.Bad) return;
            AsksIn++;
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
            Plugin.Instance.Feedback.Capture("asked by the host" + (string.IsNullOrEmpty(note) ? "" : ": " + note));
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
                _up = b; _upOff = 0; _upId = (uint)UnityEngine.Random.Range(1, int.MaxValue); _budget = ChunkBytes;
            }
            catch (Exception e) { Plugin.Log.LogWarning("report upload: " + e.Message); }
        }

        // ---- upload (client)
        private static byte[] _up; private static int _upOff; private static uint _upId; private static float _budget;
        private static readonly NetWriter _w = new NetWriter();

        /// Runner, every frame.
        public static void Tick()
        {
            if (_up == null) return;
            if (Mp.Client == null) { _up = null; return; }   // the session ended: the report stays on this machine
            _budget = Mathf.Min(_budget + BytesPerSecond * Time.unscaledDeltaTime, ChunkBytes * 4);
            while (_up != null && _budget >= ChunkBytes)
            {
                int n = Math.Min(ChunkBytes, _up.Length - _upOff);
                _w.Reset(); _w.U8(ReportChunk); _w.U32(_upId); _w.U32((uint)_up.Length); _w.U32((uint)_upOff); _w.Bytes(_up, _upOff, n);
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
        private sealed class Incoming { public uint Id; public byte[] Buf; public int Got; }
        private static readonly Dictionary<int, Incoming> _in = new Dictionary<int, Incoming>();
        private static readonly Dictionary<int, int> _count = new Dictionary<int, int>();

        internal static void ServerChunk(int from, string name, NetReader r)
        {
            uint id = r.U32(); int total = (int)r.U32(), off = (int)r.U32();
            int n = r.Remaining;
            if (r.Bad || n <= 0) return;
            ChunksIn++;
            _in.TryGetValue(from, out var cur);
            if (off == 0)
            {
                // a new report from this player (one at a time: a newer one replaces an unfinished one)
                _count.TryGetValue(from, out int c);
                if (total <= 0 || total > MaxBytes || c >= MaxPerPlayer) { Drop(from, name, "size " + total + ", " + c + " this session"); return; }
                cur = new Incoming { Id = id, Buf = new byte[total] };
                _in[from] = cur;
            }
            // reliable and ordered: anything else is a broken transfer
            if (cur == null || cur.Id != id || off != cur.Got || total != cur.Buf.Length || n > cur.Buf.Length - cur.Got) { Drop(from, name, "out of order at " + off); return; }
            Buffer.BlockCopy(r.Buf, r.Pos, cur.Buf, cur.Got, n);
            cur.Got += n;
            if (cur.Got < cur.Buf.Length) return;
            _in.Remove(from);
            _count[from] = (_count.TryGetValue(from, out int k) ? k : 0) + 1;
            try
            {
                string dir = Path.Combine(FeedbackReporter.Root, "players");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, FileName(name, from) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
                File.WriteAllBytes(file, cur.Buf);
                ReportsReceived++; LastReceived = file;
                Plugin.Log.LogInfo("Report from " + name + " (player " + from + "): " + file);
                Session.Show(1, "", "Debug report from " + name + " saved (" + Mb(cur.Buf.Length) + "): BepInEx/tldrevamp-feedback/players");
            }
            catch (Exception e) { Plugin.Log.LogWarning("report from " + name + ": " + e.Message); }
        }

        private static void Drop(int from, string name, string why)
        {
            _in.Remove(from);
            ReportsDropped++; LastDrop = name + ": " + why;
            Plugin.Log.LogWarning("report from " + name + " dropped: " + why);
        }

        internal static void ServerLeft(int id) => _in.Remove(id);   // an unfinished report from a player who left

        internal static void Reset()
        {
            _in.Clear(); _count.Clear(); _up = null; _sendNext = false; _askHintShown = false;
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

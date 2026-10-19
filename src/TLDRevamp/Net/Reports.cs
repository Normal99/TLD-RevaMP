using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Reciprocal opt-in sharing. A non-sharing host validates and relays, without keeping a local copy.
    public static class Reports
    {
        public const byte ReportAsk = 60, ReportChunk = 61, ShareState = 94, SharedChunk = 95;
        public const int MaxBytes = ReportTransfer.ReportMax, ChunkBytes = ReportTransfer.ChunkBytes;
        public static bool AskPlayers = true;
        private static bool _share;
        public static bool Share
        {
            get => _share;
            set
            {
                if (_share == value) return;
                _share = value; _generation++; _announced = null;
                if (Mp.Client != null)
                {
                    _out.Clear();
                    foreach (var incoming in _incoming.Values) _reserved -= incoming.Parts.Total;
                    _incoming.Clear();
                }
            }
        }
        private static int _generation;
        private static bool? _announced;
        private static float _lastAsk = -1000;
        private static object Owner => (object)Mp.Server ?? Mp.Client;
        private static readonly NetWriter Writer = new NetWriter();
        private sealed class Consent { internal bool Share; internal int Generation; }
        private sealed class FileData
        {
            internal bool Mark;
            internal int Source;
            internal string Base, Note, Sid, Hash;
            internal double Moment;
            internal byte[] Bytes;
        }
        private sealed class Outgoing
        {
            internal FileData File;
            internal int Target, SourceGeneration, TargetGeneration, Offset;
            internal uint Id;
        }
        private sealed class Incoming
        {
            internal FileData File;
            internal ReportAssembly Parts;
            internal float At;
            internal ulong Account;
        }
        private sealed class Work
        {
            internal object Owner;
            internal int Generation, SourceGeneration;
            internal FileData File;
            internal Task<byte[]> Task;
            internal bool Local;
            internal ulong Account;
        }
        private sealed class DiskWork
        {
            internal object Owner;
            internal int Generation;
            internal FileData File;
            internal Task<string> Task;
        }
        private static readonly Dictionary<int, Consent> Peers = new Dictionary<int, Consent>();
        private static readonly Dictionary<ulong, ReportAllowance> Allowances = new Dictionary<ulong, ReportAllowance> { { 0, new ReportAllowance() } };
        private static ReportAllowance LocalAllowance = new ReportAllowance();
        private static readonly Dictionary<int, Incoming> _incoming = new Dictionary<int, Incoming>();
        private static readonly List<Outgoing> _out = new List<Outgoing>();
        private static readonly List<Work> WorkItems = new List<Work>();
        private static readonly List<DiskWork> DiskItems = new List<DiskWork>();
        private static readonly object DiskLock = new object();
        private static readonly List<int> Expired = new List<int>();
        private static readonly HashSet<int> Sent = new HashSet<int>();
        private static readonly ReportRelayBudget RelayBudget = new ReportRelayBudget();
        private static readonly HashSet<string> Seen = new HashSet<string>();
        private static readonly Dictionary<int, float> NextStart = new Dictionary<int, float>();
        private static long _reserved, _receiveReserved;
        private static float _budget;
        private static uint _nextId;
        private static double _reportMoment;
        private static object _reportOwner;
        private static int _reportGeneration;
        public static long FilesSent, BytesSent, FilesReceived, BytesReceived, FilesRelayed, BytesRelayed, Refusals, Duplicates;
        public static long AsksSent, AsksIn, AsksIgnored;
        public static string LastReceived = "", LastDrop = "";
        private static int SourceGeneration(int id) => id == 0 ? _generation : Peers.TryGetValue(id, out var p) ? p.Generation : -1;
        private static bool Sharing(int id) => id == 0 ? Share : Peers.TryGetValue(id, out var p) && p.Share;
        private static void Refuse(string why) { Refusals++; LastDrop = why; }

        public static void LocalReport(string note)
        {
            if (Plugin.Instance.Feedback.Pending) return;
            double moment = SessionLog.St;
            if (Plugin.Instance.Feedback.Capture(note, moment) == null) return;
            ReportStarted(moment);
            if (Mp.Server != null && AskPlayers && Mp.Server.ReadyCount() > 0)
            {
                var w = new NetWriter(); w.U8(ReportAsk); w.Str(TelemetryMarks.Note(note)); w.F64(moment);
                Mp.Server.SendToAll(w, true, -1, null, 0); AsksSent++;
                Session.Show(1, "", "Debug report saved. Asked sharing players for theirs.");
            }
        }
        private static void ReportStarted(double moment)
        {
            _reportMoment = moment; _reportOwner = Share ? Owner : null; _reportGeneration = _generation;
        }
        internal static void ClientAsk(NetReader r)
        {
            string note = r.Str(); double moment = r.F64(); AsksIn++;
            if (r.Bad || r.Remaining != 0 || double.IsNaN(moment) || double.IsInfinity(moment) || moment < 0 || moment > SessionLog.St + 10 || note == null || note.Length > 60 || !Share ||
                Plugin.Instance.Feedback.Pending || Time.realtimeSinceStartup - _lastAsk < 30) { AsksIgnored++; return; }
            _lastAsk = Time.realtimeSinceStartup;
            if (Plugin.Instance.Feedback.Capture("asked by the host: " + TelemetryMarks.Note(note), moment) != null) ReportStarted(moment);
        }
        internal static void OnZipped(string zip)
        {
            if (_reportOwner != Owner || _reportOwner == null || _reportGeneration != _generation || !Share) return;
            _reportOwner = null;
            Prepare(zip, false, "report-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"), "", _reportMoment);
        }
        // Consent and session are frozen when capture begins, not five seconds later when writing finishes.
        internal static Action<string> MarkCompletion(string basename, string note, double moment)
        {
            object owner = Owner; int generation = _generation; bool sharing = Share;
            return path => { if (sharing && Share && owner != null && owner == Owner && generation == _generation) Prepare(path, true, basename, note, moment); };
        }
        private static void Prepare(string path, bool mark, string basename, string note, double moment)
        {
            if (!Share || Owner == null || WorkItems.Count >= 4) { Refuse("preparation queue full or not sharing"); return; }
            var f = new FileData { Mark = mark, Base = basename, Note = TelemetryMarks.Note(note), Moment = moment, Sid = SessionLog.Sid,
                Source = Mp.Client != null ? Mp.Client.MyId : 0 };
            WorkItems.Add(new Work { Owner = Owner, Generation = _generation, File = f, Local = true, Task = Task.Run(() =>
            {
                using (var input = File.OpenRead(path)) f.Bytes = ReportTransfer.ReadBounded(input, mark ? ReportTransfer.JsonMax : MaxBytes);
                if (mark) f.Bytes = ReportTransfer.Compress(f.Bytes);
                byte[] json = ReportTransfer.Validate(f.Bytes, mark, f.Sid, moment);
                f.Hash = ReportTransfer.Hash(f.Bytes); return json;
            }) });
        }
        internal static void ServerConsent(int from, NetReader r)
        {
            byte share = r.U8();
            if (r.Bad || r.Remaining != 0 || share > 1) { Refuse("invalid consent"); return; }
            if (!Peers.TryGetValue(from, out var p)) Peers[from] = p = new Consent();
            if (p.Share == (share == 1)) return;
            p.Share = share == 1; p.Generation++;
            RemoveIncoming(from); _out.RemoveAll(o => o.Target == from || o.File.Source == from);
        }
        private static ReportAllowance Allowance(ulong who)
        {
            if (!Allowances.TryGetValue(who, out var a))
            {
                if (Allowances.Count >= 256) throw new InvalidDataException("sender identity limit");
                Allowances[who] = a = new ReportAllowance();
            }
            return a;
        }
        internal static void ServerChunk(int from, ulong who, string name, NetReader r) => Receive(from, who != 0 ? who : 0xFFFFFFFF00000000UL | (uint)from, name, r, false);
        internal static void ClientChunk(NetReader r)
        {
            if (!Share) { Refuse("receive while sharing off"); return; }
            Receive(-1, 0, "", r, true);
        }
        private static void Receive(int from, ulong who, string name, NetReader r, bool client)
        {
            if (r.Remaining > ChunkBytes + 1024) { Refuse("message size"); RemoveIncoming(from); return; }
            uint id = r.U32(); int total = (int)r.U32(), offset = (int)r.U32(); byte kind = r.U8();
            FileData f = null;
            if (offset == 0)
            {
                int source = client ? r.I32() : from;
                if (client) r.Str(); // Consume the legacy name slot; attribution is the session index only.
                f = new FileData { Mark = kind == 1, Source = source, Base = r.Str(), Note = r.Str(), Moment = r.F64(), Sid = r.Str(), Hash = r.Str() };
            }
            try
            {
                if (r.Bad || kind > 1 || !client && !Sharing(from)) throw new InvalidDataException("not sharing or bad header");
                var a = Allowance(who);
                if (!a.Chunk(r.Remaining, Time.realtimeSinceStartup)) throw new InvalidDataException("chunk rate/size");
                if (offset == 0)
                {
                    RemoveIncoming(from);
                    if (f.Base.Length > 100 || f.Base != ReportTransfer.Slug(f.Base, 100) || f.Note.Length > 60 ||
                        f.Sid != SessionLog.Sid || f.Hash.Length != 64 || f.Hash != ReportTransfer.Slug(f.Hash, 64) ||
                        double.IsNaN(f.Moment) || double.IsInfinity(f.Moment) || f.Moment < 0 || f.Moment > SessionLog.St + 10 ||
                        client && (f.Source < 0 || f.Source == Mp.Client.MyId)) throw new InvalidDataException("metadata");
                    if (f.Mark && f.Base != ReportTransfer.MomentName(f.Sid, f.Moment, f.Note)) throw new InvalidDataException("mark basename");
                    f.Base = f.Mark ? ReportTransfer.MomentName(f.Sid, f.Moment, "") : "report";
                    f.Note = TelemetryMarks.Note(f.Note);
                    ulong account = client ? 0x8000000000000000UL | (uint)f.Source : who;
                    var source = Allowance(account);
                    if (source.Has(f.Hash)) { Duplicates++; return; }
                    if (_reserved + total > ReportTransfer.QueueMax || WorkItems.Count >= 4 || _receiveReserved + total > ReportTransfer.RelayMax ||
                        !source.Start(f.Mark, total, Time.realtimeSinceStartup, !client)) throw new InvalidDataException("session/queue/start limit");
                    var incoming = new Incoming { File = f, Parts = new ReportAssembly(id, total, f.Mark), At = Time.realtimeSinceStartup, Account = account };
                    _incoming[from] = incoming; _reserved += total;
                    _receiveReserved += total;
                }
                if (!_incoming.TryGetValue(from, out var cur)) return;
                if (cur.File.Mark != (kind == 1)) throw new InvalidDataException("kind changed");
                cur.At = Time.realtimeSinceStartup;
                var bytes = cur.Parts.Add(id, total, offset, r.Buf, r.Pos, r.Remaining);
                if (bytes == null) return;
                _incoming.Remove(from); cur.File.Bytes = bytes;
                if (WorkItems.Count >= 4) { _reserved -= cur.Parts.Total; Refuse("validation queue full"); return; }
                var file = cur.File;
                WorkItems.Add(new Work { Owner = Owner, Generation = _generation, SourceGeneration = SourceGeneration(from), Account = cur.Account, File = file, Task = Task.Run(() =>
                {
                    if (ReportTransfer.Hash(bytes) != file.Hash) throw new InvalidDataException("content hash");
                    return ReportTransfer.Validate(bytes, file.Mark, file.Sid, file.Moment);
                }) });
            }
            catch (Exception e) { RemoveIncoming(from); Refuse(e.Message); }
        }
        private static void RemoveIncoming(int from)
        {
            if (_incoming.TryGetValue(from, out var cur)) { _reserved -= cur.Parts.Total; _incoming.Remove(from); }
        }
        private static void Queue(FileData f, int target)
        {
            long queued = 0; foreach (var o in _out) queued += o.File.Bytes.Length;
            if (_out.Count >= 32 || queued + f.Bytes.Length > ReportTransfer.QueueMax || target != -1 && !RelayBudget.Reserve(f.Bytes.Length))
            { Refuse("outgoing queue/relay session limit"); return; }
            _out.Add(new Outgoing { File = f, Target = target, Id = ++_nextId, SourceGeneration = target == -1 ? _generation : SourceGeneration(f.Source), TargetGeneration = SourceGeneration(target) });
        }
        private static void Relay(FileData f)
        {
            foreach (var p in Peers) if (ReportTransfer.Receives(Sharing(f.Source), p.Value.Share, f.Source, p.Key)) Queue(f, p.Key);
        }
        private static void Save(FileData f, byte[] json)
        {
            if (DiskItems.Count >= 4) { Refuse("disk queue full"); return; }
            string player = TelemetryPrivacy.Player(f.Source);
            string dir = Path.Combine(FeedbackReporter.Root, f.Mark ? "marks" : Path.Combine("players", player));
            string file = Path.Combine(dir, TelemetryPrivacy.ReceivedName(f.Source, f.Hash, f.Mark));
            byte[] bytes = f.Mark ? json : f.Bytes;
            DiskItems.Add(new DiskWork { Owner = Owner, Generation = _generation, File = f, Task = Task.Run(() =>
            {
                lock (DiskLock)
                {
                    Directory.CreateDirectory(dir);
                    KeepFolderUnder(FeedbackReporter.Root, (200L << 20) - bytes.Length);
                    string temp = file + ".tmp";
                    try { File.WriteAllBytes(temp, bytes); File.Move(temp, file); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    return file;
                }
            }) });
        }
        private static void KeepFolderUnder(string root, long max)
        {
            var files = new List<FileInfo>();
            string players = Path.Combine(root, "players"), marks = Path.Combine(root, "marks");
            if (Directory.Exists(players)) files.AddRange(new DirectoryInfo(players).GetFiles("*.zip", SearchOption.AllDirectories));
            if (Directory.Exists(marks)) files.AddRange(new DirectoryInfo(marks).GetFiles("mark-player-*.json"));
            long sum = 0; foreach (var file in files) sum += file.Length;
            if (sum <= max) return;
            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            foreach (var file in files)
            {
                if (sum <= max) break;
                long size = file.Length; file.Delete(); sum -= size;
            }
        }
        /// Poll workers, then send at most one chunk per target per frame under a global byte budget.
        public static void Tick()
        {
            if (Mp.Client != null && Mp.Client.Ready && _announced != Share)
            {
                Writer.Reset(); Writer.U8(ShareState); Writer.U8(Share ? (byte)1 : (byte)0);
                if (Mp.Client.SendReportState(Writer)) _announced = Share;
            }
            for (int i = DiskItems.Count - 1; i >= 0; i--)
            {
                var disk = DiskItems[i]; if (!disk.Task.IsCompleted) continue;
                DiskItems.RemoveAt(i);
                string diskError = disk.Task.IsFaulted ? disk.Task.Exception.GetBaseException().Message : null;
                if (disk.Owner != Owner) continue;
                if (diskError != null) { Refuse("disk: " + diskError); continue; }
                var f = disk.File;
                FilesReceived++; BytesReceived += f.Bytes.Length; LastReceived = disk.Task.Result;
                if (Share && disk.Generation == _generation) Session.Show(1, "", f.Mark ? "received " + TelemetryPrivacy.Player(f.Source) + "'s black box for mark '" + TelemetryMarks.Note(f.Note) + "'" : "received " + TelemetryPrivacy.Player(f.Source) + "'s debug report");
            }
            for (int i = WorkItems.Count - 1; i >= 0; i--)
            {
                var work = WorkItems[i]; if (!work.Task.IsCompleted) continue;
                WorkItems.RemoveAt(i);
                if (!work.Local && work.Owner == Owner) _reserved = Math.Max(0, _reserved - work.File.Bytes.Length);
                string error = work.Task.IsFaulted ? work.Task.Exception.GetBaseException().Message : null;
                if (work.Owner != Owner || Owner == null) continue;
                if (error != null) { Refuse(error); continue; }
                var f = work.File;
                if (work.Local)
                {
                    if (!Share || work.Generation != _generation) continue;
                    if (Seen.Contains(f.Source + ":" + f.Hash)) { Duplicates++; continue; }
                    if (!LocalAllowance.Start(f.Mark, f.Bytes.Length, Time.realtimeSinceStartup, false)) { Refuse("local capture limit"); continue; }
                    Seen.Add(f.Source + ":" + f.Hash);
                    if (Mp.Client != null) Queue(f, -1); else Relay(f);
                }
                else if (Mp.Server != null)
                {
                    if (!Sharing(f.Source) || work.SourceGeneration != SourceGeneration(f.Source)) continue;
                    if (!Allowance(work.Account).Remember(f.Hash)) { Duplicates++; continue; }
                    if (Share && work.Generation == _generation) Save(f, work.Task.Result);
                    Relay(f);
                }
                else if (Share && work.Generation == _generation)
                {
                    if (!Allowance(work.Account).Remember(f.Hash)) { Duplicates++; continue; }
                    Save(f, work.Task.Result);
                }
            }
            Expired.Clear();
            foreach (var p in _incoming) if (Time.realtimeSinceStartup - p.Value.At > 120) Expired.Add(p.Key);
            foreach (int id in Expired) { RemoveIncoming(id); Refuse("transfer timeout"); }
            _budget = Mathf.Min(_budget + ReportTransfer.Rate * Time.unscaledDeltaTime, ChunkBytes * 2);
            Sent.Clear();
            for (int i = 0; i < _out.Count && _budget >= ChunkBytes;)
            {
                var o = _out[i]; var f = o.File; bool client = o.Target == -1;
                bool allowed = client ? Mp.Client != null && Share && o.SourceGeneration == _generation : Mp.Server != null &&
                    ReportTransfer.Receives(Sharing(f.Source), Sharing(o.Target), f.Source, o.Target) &&
                    o.SourceGeneration == SourceGeneration(f.Source) && o.TargetGeneration == SourceGeneration(o.Target);
                if (!allowed) { _out.RemoveAt(i); continue; }
                if (!Sent.Add(o.Target)) { i++; continue; }
                if (client && o.Offset == 0 && NextStart.TryGetValue(f.Mark ? 1 : 0, out float at) && Time.realtimeSinceStartup < at) { i++; continue; }
                int n = Math.Min(ChunkBytes, f.Bytes.Length - o.Offset);
                Writer.Reset(); Writer.U8(client ? ReportChunk : SharedChunk); Writer.U32(o.Id); Writer.U32((uint)f.Bytes.Length); Writer.U32((uint)o.Offset); Writer.U8(f.Mark ? (byte)1 : (byte)0);
                if (o.Offset == 0)
                {
                    if (!client) { Writer.I32(f.Source); Writer.Str(TelemetryPrivacy.Player(f.Source)); }
                    Writer.Str(f.Base); Writer.Str(f.Note); Writer.F64(f.Moment); Writer.Str(f.Sid); Writer.Str(f.Hash);
                }
                Writer.Bytes(f.Bytes, o.Offset, n);
                if (!(client ? Mp.Client.SendReport(Writer) : Mp.Server.SendReport(o.Target, Writer))) { i++; continue; }
                if (client && o.Offset == 0) NextStart[f.Mark ? 1 : 0] = Time.realtimeSinceStartup + (f.Mark ? 10.1f : 30.1f);
                _budget -= ChunkBytes; o.Offset += n;
                if (client || f.Source == 0) BytesSent += n; else BytesRelayed += n;
                if (o.Offset == f.Bytes.Length)
                {
                    if (client || f.Source == 0) FilesSent++; else FilesRelayed++;
                    _out.RemoveAt(i);
                }
                else i++;
            }
        }
        internal static void ServerLeft(int id)
        {
            RemoveIncoming(id); Peers.Remove(id); _out.RemoveAll(o => o.Target == id || o.File.Source == id);
        }
        internal static void Reset()
        {
            _generation++; Peers.Clear(); Allowances.Clear(); _incoming.Clear(); _out.Clear(); Seen.Clear(); NextStart.Clear();
            Allowances[0] = new ReportAllowance(); LocalAllowance = new ReportAllowance();
            // Old workers finish independently; owner checks discard their completions after a session change.
            _reserved = _receiveReserved = 0; RelayBudget.Clear(); _announced = null; _reportOwner = null; _lastAsk = -1000; _budget = 0;
            FilesSent = BytesSent = FilesReceived = BytesReceived = FilesRelayed = BytesRelayed = Refusals = Duplicates = AsksSent = AsksIn = AsksIgnored = 0;
            LastReceived = LastDrop = "";
        }
        public static string Stats() => "{\"share\":" + (Share ? "true" : "false") + ",\"askPlayers\":" + (AskPlayers ? "true" : "false") +
            ",\"sent\":" + FilesSent + ",\"sentBytes\":" + BytesSent + ",\"received\":" + FilesReceived + ",\"receivedBytes\":" + BytesReceived +
            ",\"relayed\":" + FilesRelayed + ",\"relayedBytes\":" + BytesRelayed + ",\"refusals\":" + Refusals + ",\"duplicates\":" + Duplicates +
            ",\"relayReservedBytes\":" + RelayBudget.Used + ",\"queued\":" + _out.Count + ",\"asksSent\":" + AsksSent + ",\"asksIn\":" + AsksIn + ",\"asksIgnored\":" + AsksIgnored +
            ",\"lastReceived\":" + Json.Str(LastReceived) + ",\"lastDrop\":" + Json.Str(LastDrop) + "}";
    }
}

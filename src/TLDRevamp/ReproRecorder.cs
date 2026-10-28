using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace TLDRevamp
{
    internal static class ReproRecorder
    {
        private static ReproData _data;
        private static readonly HashSet<uint> Bound = new HashSet<uint>();
        private static string _sid, _folder;
        private static fpscontroller _player;
        private static double _start, _next;
        private static Task _save;
        private static bool _stopping;
        private static volatile bool _failed;
        private static long _ticks, _calls, _max;
        private static long _appendTicks, _appendCalls, _appendMax;
        private static int _count, _dropped, _skipped;
        private static int _hitchFrame = -1;
        private static uint _hitchNet;
        internal static bool Recording => _data != null;
        /// Settings → Revamp → Debugging → "Record actions for bug reports": on, the recorder starts by itself when a
        /// multiplayer session starts, so a bug can be reported after it happened. Saved with the settings.
        internal static bool Auto = true;
        private static bool _auto, _full;
        private static string _autoSid;
        /// The folder holding a copy of the world save this session started from (host only), made when recording began.
        internal static string StartWorldDir { get; private set; }
        internal static string StartWorldFile { get; private set; }
        internal static bool Full => _full;
        internal static string State() => "{\"recording\":" + (Recording ? "true" : "false") + ",\"saving\":" + (_save != null && !_save.IsCompleted ? "true" : "false") +
            ",\"failed\":" + (_failed ? "true" : "false") + ",\"rows\":" + (_data != null ? _data.Count : _count) + ",\"dropped\":" + (_data != null ? _data.Dropped : _dropped) +
            ",\"skipped\":" + (_data != null ? _data.Skipped : _skipped) + ",\"sampleHz\":10,\"capacity\":60000,\"selfMs\":" + Json.Num(_calls == 0 ? 0 : _ticks * 1000.0 / Stopwatch.Frequency / _calls, "F4") +
            ",\"maxMs\":" + Json.Num(_max * 1000.0 / Stopwatch.Frequency, "F4") + ",\"appendMs\":" +
            Json.Num(_appendCalls == 0 ? 0 : _appendTicks * 1000.0 / Stopwatch.Frequency / _appendCalls, "F4") + ",\"appendMaxMs\":" + Json.Num(_appendMax * 1000.0 / Stopwatch.Frequency, "F4") + "}";
        internal static string Start() => StartCore(false);
        private static string StartCore(bool auto)
        {
            if (ReproPlayback.Active) return "{\"error\":\"end playback before recording\"}";
            if (Recording) { if (!auto) { _auto = false; _full = false; } return State(); }   // a hand start takes over the automatic one: it saves a file on stop
            if (_save != null && !_save.IsCompleted) return "{\"error\":\"previous repro is saving\"}";
            var p = mainscript.s != null ? mainscript.s.player : null;
            if (p == null || !Net.Entities.InSession) return "{\"error\":\"join a multiplayer world first\"}";
            _data = new ReproData(); Bound.Clear(); _player = p; _sid = SessionLog.Sid; _folder = SessionLog.MarksFolder;
            _auto = auto; _full = false; _autoSid = _sid;
            _start = Time.realtimeSinceStartupAsDouble; _next = _start; _count = _dropped = _skipped = 0; _failed = false; _ticks = _calls = _max = 0;
            _appendTicks = _appendCalls = _appendMax = 0;
            Net.Entities.ReproReset();
            Add(NetworkConditions.ReproRow(NetworkConditions.Current));
            Net.Entities.LocalSeat(out uint seatNet, out int seat);
            if (seatNet != 0) Add(new ReproRow { Kind = ReproKind.SeatEnter, Net = seatNet, Slot = seat });
            CopyStartWorld();
            Tick();
            Plugin.Instance.Overlay.Toast(auto ? "Recording your actions for bug reports (" + Plugin.KeyName(Plugin.KeyRepro) + " reports a bug)" : "Repro recording started", auto ? 5f : 2f);
            return State();
        }
        internal static string Stop() => Stop(true);
        /// An automatic recording that ends with the session is dropped unless a bug report took it: the files would pile up.
        private static string Stop(bool save)
        {
            if (!Recording || _stopping) return State();
            _stopping = true;
            if (_data.Count < ReproData.Capacity - 64) Net.Entities.ReproFinish();
            var data = _data; _data = null; _count = data.Count; _dropped = data.Dropped; _skipped = data.Skipped; _player = null; Bound.Clear();
            Net.Entities.ReproClear();
            _stopping = false;
            string folder = _folder;
            if (!save) { Plugin.Log.LogInfo("repro: automatic recording ended with the session (" + data.Count + " rows), not saved"); return State(); }
            _save = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, "repro-" + Guid.NewGuid().ToString("N") + ".json");
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var w = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" }) data.Write(w, Net.Protocol.Version);
                }
                catch { _failed = true; }
            });
            Plugin.Instance.Overlay.Toast("Repro stopped: " + data.Skipped + " skipped, " + data.Dropped + " dropped; saving in the marks session folder", 3f); return State();
        }
        internal static void Toggle()
        {
            if (Recording) { Stop(); return; }
            string reply = Start();
            const string error = "{\"error\":\"";
            if (reply.StartsWith(error, StringComparison.Ordinal)) Plugin.Instance.Overlay.Toast("Repro: " + reply.Substring(error.Length, reply.Length - error.Length - 2), 3f);
        }
        internal static void Tick()
        {
            if (!Recording) { AutoStart(); return; }
            long begin = Stopwatch.GetTimestamp();
            if (_sid != SessionLog.Sid || mainscript.s == null || mainscript.s.player != _player || !Net.Entities.InSession) { Stop(!_auto); return; }
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= _next)
            {
                _next = now + 0.1;
                Net.Entities.ReproFlush();
                if (!Recording) return;
                var pos = mainscript.GlobalFromUnityPos(_player.transform.position);
                Add(new ReproRow { Kind = ReproKind.Pose, X = pos.x, Y = pos.y, Z = pos.z,
                    A = _player.BodyRot != null ? _player.BodyRot.eulerAngles.y : _player.transform.eulerAngles.y, B = _player.FyRot, C = _player.FxRot });
            }
            Cost(begin);
        }
        private static void AutoStart()
        {
            if (!Auto || ReproPlayback.Active || SessionLog.Sid == null || SessionLog.Sid == _autoSid) return;
            if (mainscript.s == null || mainscript.s.player == null || !Net.Entities.InSession) return;
            if (_save != null && !_save.IsCompleted) return;
            StartCore(true);
        }
        /// A replay run arms playback on a copy that is already recording by itself: drop that recording, keep the hand-started one.
        internal static void DiscardAutomatic() { if (Recording && _auto) Stop(false); }
        /// The recording so far, copied, while it keeps going. Null when nothing is recording.
        internal static ReproData Snapshot() => _data?.Copy();
        private static void Cost(long begin) { long elapsed = Stopwatch.GetTimestamp() - begin; _ticks += elapsed; _calls++; _max = Math.Max(_max, elapsed); }
        internal static void Add(ReproRow row)
        {
            if (!Recording || ReproPlayback.Active) return;
            long begin = Stopwatch.GetTimestamp();
            if (_full) return;
            row.T = Math.Max(0, Time.realtimeSinceStartupAsDouble - _start); row.St = SessionLog.St;
            if (Bind(row.Net, row.T, row.St) && Bind(row.Other, row.T, row.St)) _data.Add(row);
            else _data.Skip();
            long elapsed = Stopwatch.GetTimestamp() - begin; _appendTicks += elapsed; _appendCalls++; _appendMax = Math.Max(_appendMax, elapsed);
            if (_data.Count >= ReproData.Capacity)
            {
                // A report keeps the beginning, which replays; an automatic recording stays on screen as full.
                if (_auto) { _full = true; Plugin.Instance.Overlay.Toast("The action recorder is full; it keeps the first part of this session", 6f); }
                else { _data.Truncated(); Stop(); }
            }
        }
        private static bool Bind(uint net, double t, double st)
        {
            if (net == 0 || Bound.Contains(net)) return true;
            var row = Net.Entities.ReproBinding(net); row.T = t; row.St = st;
            if (row.Unresolved || !_data.Add(row)) return false;
            Bound.Add(net); return true;
        }
        internal static void Skip() { if (Recording) _data.Skip(); }
        internal static void Command(string text)
        {
            if (!Recording || ReproPlayback.Active) return;
            int code = ReproData.Command(text);
            if (code >= 0 && code != 4) Add(new ReproRow { Kind = ReproKind.Command, A = code });
        }
        internal static void Event(string kind, uint net, uint other, int item, int slot, int from, string result)
        {
            if (!Recording || ReproPlayback.Active || from != Net.Entities.MyId) return;
            ReproKind action;
            switch (kind)
            {
                case "detach-request": action = ReproKind.Detach; break;
                case "seat-enter": action = ReproKind.SeatEnter; break;
                case "seat-leave": action = ReproKind.SeatLeave; break;
                case "attach-sent": case "attach-request": Net.Entities.ReproAttach(net, other, item, slot); return;
                case "hitch-request":
                    if (_hitchFrame == Time.frameCount && _hitchNet == net) return;
                    _hitchFrame = Time.frameCount; _hitchNet = net; action = result == "connect" ? ReproKind.Hitch : ReproKind.Unhitch; break;
                default: return;
            }
            Add(new ReproRow { Kind = action, Net = net, Other = other, Item = item, Slot = slot });
        }

        /// Copies the save this world was loaded from before the game's autosaves can overwrite it. Host only: a client has no world file.
        private static void CopyStartWorld()
        {
            StartWorldDir = null; StartWorldFile = null;
            string file = Net.Mp.Client == null ? Net.Entities.WorldSaveFile : null;
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
            string dest = Path.Combine(Path.Combine(FeedbackReporter.Root, "bug-world"), _sid ?? "0000000000000000");
            StartWorldDir = dest; StartWorldFile = Path.GetFileName(file);
            var sources = BugReportLogic.WorldFiles(file);
            Task.Run(() =>
            {
                try
                {
                    string root = Path.GetDirectoryName(dest);
                    Directory.CreateDirectory(dest);
                    BugReport.CopyFiles(sources, dest);
                    // keep this one and the one before it
                    var old = new DirectoryInfo(root).GetDirectories();
                    Array.Sort(old, (a, b) => b.CreationTimeUtc.CompareTo(a.CreationTimeUtc));
                    for (int i = 2; i < old.Length; i++) try { old[i].Delete(true); } catch { }
                }
                catch (Exception e) { SessionLog.Fail("bug world copy", e); StartWorldDir = null; }
            });
        }

        private static readonly CompactOverlay Bar = new CompactOverlay();
        private static string _keyName, _autoStatus, _fullStatus;
        private static bool BarText(out string title, out string status, out OverlayKind kind)
        {
            if (BugReport.BarText(out title, out status, out kind)) return true;
            if (!Recording) return false;
            kind = OverlayKind.Warning;
            string key = Plugin.KeyName(Plugin.KeyRepro);
            if (_keyName != key) { _keyName = key; _autoStatus = "for bug reports · " + key + " reports a bug"; _fullStatus = "keeps the first part · " + key + " reports a bug"; }
            if (_full) { title = "RevaMP · Action recorder full"; status = _fullStatus; return true; }
            if (_auto) { title = "RevaMP · Recording actions"; status = _autoStatus; return true; }
            title = "RevaMP · Recording repro"; status = "Recording actions"; return true;
        }
        internal static float BarHeight => BarText(out _, out _, out _) ? Bar.Height : 0;
        internal static float BarWidth => Bar.Width;
        internal static void Measure() { if (BarText(out string title, out string status, out _)) Bar.Measure(title, status); }
        internal static void Draw()
        {
            if (UnityEngine.Event.current.type != EventType.Repaint || !BarText(out _, out _, out var kind)) return;
            if (DebugLayout.Recording.H <= 0) return;
            Color color = GUI.color; int depth = GUI.depth; GUI.depth = -10000;
            Bar.Draw(DebugLayout.Recording, kind);
            GUI.color = color; GUI.depth = depth;
        }
    }
}

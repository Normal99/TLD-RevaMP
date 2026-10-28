using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace TLDRevamp
{
    /// One bug report: the usual report zip (black box, stats, logs, settings) plus the recorded actions, the world save the
    /// session started from and a REPORT.md that says what is in it. Taken with the report key, from the mark panel in bug mode
    /// (Ctrl+Insert) or with `/bug <note>`. The player always sees what is being recorded and when a report is saved.
    internal static class BugReport
    {
        private enum Phase { Idle, Saving, Saved, Failed }
        private static Phase _phase;
        private static string _name = "";
        private static float _until;
        private static string _status = "";

        /// Takes a bug report now. Returns false when one is already being saved.
        internal static bool Request(string note)
        {
            var feedback = Plugin.Instance.Feedback;
            if (feedback.Pending) { Plugin.Instance.Overlay.Toast("A bug report is already being saved", 3f); return false; }
            if (!Net.Reports.LocalReport(note, true)) { Fail("could not start"); return false; }   // as Page Down: asks sharing players, shares the report zip
            _phase = Phase.Saving; _name = ""; _until = float.MaxValue; _status = "taking the snapshot";
            Plugin.Instance.Overlay.Toast("Saving bug report…", 3f);
            return true;
        }

        /// `bug status` / `bug report [note]`: what the player sees (phase, bundle name) and the recorder, for the tests.
        internal static string Bridge(string arg)
        {
            arg = (arg ?? "").Trim();
            if (arg == "panel") MarkPanel.Open(true);   // the Ctrl+Insert note box; `markpanel save <note>` then takes the report
            if (arg == "report" || arg.StartsWith("report ", StringComparison.Ordinal))
            {
                if (mainscript.s == null || mainscript.s.player == null) return "{\"error\":\"no player\"}";
                if (!Request(arg.Length > 6 ? arg.Substring(7) : "")) return "{\"error\":\"a report is already being saved\"}";
            }
            bool bar = BarText(out string title, out string status, out _);
            return "{\"phase\":\"" + _phase + "\",\"name\":" + Json.Str(_name) + ",\"banner\":" + (bar ? Json.Str(title + " " + status) : "null") + ",\"panel\":" + (MarkPanel.Opened ? "true" : "false") + ",\"auto\":" + (ReproRecorder.Auto ? "true" : "false") +
                ",\"recording\":" + (ReproRecorder.Recording ? "true" : "false") + ",\"full\":" + (ReproRecorder.Full ? "true" : "false") +
                ",\"startWorld\":" + Json.Str(ReproRecorder.StartWorldFile ?? "") + ",\"recorder\":" + ReproRecorder.State() + "}";
        }

        internal static bool Chat(string text)
        {
            if (!BugReportLogic.IsBugCommand(text, out string note)) return false;
            if (mainscript.s == null || mainscript.s.player == null) return true;
            Request(note);
            return true;
        }

        internal static void Saved(string zipPath)
        {
            _phase = Phase.Saved; _name = Path.GetFileName(zipPath); _until = Time.realtimeSinceStartup + 12f;
            Plugin.Instance.Overlay.Toast("Bug report saved: BepInEx/tldrevamp-feedback/" + _name, 8f);
        }

        internal static void Fail(string why)
        {
            _phase = Phase.Failed; _name = why; _until = Time.realtimeSinceStartup + 12f;
            Plugin.Instance.Overlay.Toast("Bug report failed: " + why + " (see the BepInEx log)", 8f);
        }

        /// The banner while a report is being saved, and for a while after it was saved or failed.
        internal static bool BarText(out string title, out string status, out OverlayKind kind)
        {
            title = status = ""; kind = OverlayKind.Text;
            if (_phase == Phase.Idle) return false;
            if (_phase != Phase.Saving && Time.realtimeSinceStartup > _until) { _phase = Phase.Idle; return false; }
            switch (_phase)
            {
                case Phase.Saving: title = "RevaMP · Bug report"; status = "saving…"; kind = OverlayKind.Warning; return true;
                case Phase.Saved:
                    if (_status.Length == 0 || !_status.EndsWith(_name, StringComparison.Ordinal)) _status = "saved · " + _name;
                    title = "RevaMP · Bug report"; status = _status; kind = OverlayKind.Ok; return true;
                default:
                    title = "RevaMP · Bug report"; status = "failed · " + _name; kind = OverlayKind.Problem; return true;
            }
        }

        /// Runs on the main thread inside the report capture: copies what must be copied now and returns the work for the worker
        /// that writes the extra files into the report folder.
        internal static Action Prepare(string dir, string note)
        {
            var repro = ReproRecorder.Snapshot();
            var facts = new BugReportLogic.Facts
            {
                Note = note ?? "", Version = Plugin.Version, Commit = Plugin.CommitLabel, Build = Plugin.BuildHash, Game = Plugin.GameBuild,
                Protocol = Net.Protocol.Version, Time = TelemetryPrivacy.DateStr().Trim('"'), InSession = Net.Entities.InSession,
                Role = Net.Mp.Client != null ? "client" : Net.Entities.InSession ? "host" : "solo",
                ReproPresent = repro != null, ReproFull = ReproRecorder.Full,
            };
            if (repro != null) { facts.ReproRows = repro.Count; facts.ReproSkipped = repro.Skipped; facts.ReproDropped = repro.Dropped; facts.ReproComplete = repro.Dropped == 0; facts.ReproSeconds = repro.Seconds; }
            try
            {
                var p = mainscript.s != null ? mainscript.s.player : null;
                if (p != null)
                {
                    var g = mainscript.GlobalFromUnityPos(p.transform.position); facts.X = g.x; facts.Y = g.y; facts.Z = g.z;
                    Net.Entities.LocalSeat(out uint seatNet, out int _); facts.Seated = seatNet != 0;
                }
            }
            catch (Exception e) { SessionLog.Fail("bug report position", e); }
            string startDir = ReproRecorder.StartWorldDir, startFile = ReproRecorder.StartWorldFile;
            facts.WorldFile = startFile;
            string[] autosave = Array.Empty<string>(); string autosaveData = null;
            if (Net.Mp.Client == null)
            {
                try { autosaveData = pathscript.SaveDataName(pathscript.autosaveName1); autosave = BugReportLogic.WorldFiles(autosaveData); }
                catch (Exception e) { SessionLog.Fail("bug report autosave", e); autosaveData = null; }
            }
            return () =>
            {
                try
                {
                    if (repro != null)
                        using (var stream = new FileStream(Path.Combine(dir, BugReportLogic.ReproFile), FileMode.Create, FileAccess.Write))
                        using (var w = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" }) repro.Write(w, Net.Protocol.Version);
                    if (startDir != null && Directory.Exists(startDir))
                        facts.WorldStartCopied = CopyFiles(Directory.GetFiles(startDir), Path.Combine(dir, "world", "start")) > 0;
                    if (autosaveData != null && File.Exists(autosaveData))
                    {
                        facts.LatestAutosaveCopied = CopyFiles(autosave, Path.Combine(dir, "world", "latest-autosave")) > 0;
                        facts.AutosaveAgeSeconds = (DateTime.UtcNow - File.GetLastWriteTimeUtc(autosaveData)).TotalSeconds;
                    }
                }
                catch (Exception e) { SessionLog.Fail("bug report files", e); }
                File.WriteAllText(Path.Combine(dir, "REPORT.md"), BugReportLogic.Report(facts), new UTF8Encoding(false));
            };
        }

        /// The local bundle: everything in the report folder, including what the shared report's whitelist leaves out.
        internal static string Bundle(string dir, string stamp)
        {
            string path = Path.Combine(FeedbackReporter.Root, "TLDRevamp-bug-" + stamp + ".zip");
            using (var output = File.Create(path))
            using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create))
                foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var entry = zip.CreateEntry(file.Substring(dir.Length + 1).Replace('\\', '/'), System.IO.Compression.CompressionLevel.Optimal);
                    using (var source = File.OpenRead(file)) using (var target = entry.Open()) source.CopyTo(target);
                }
            return path;
        }

        /// Copies the files that exist; the game may hold its own save open, so the read is shared. Returns how many were copied.
        internal static int CopyFiles(IEnumerable<string> sources, string destination)
        {
            int copied = 0;
            Directory.CreateDirectory(destination);
            foreach (var src in sources)
            {
                try
                {
                    if (!File.Exists(src)) continue;
                    using (var a = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var b = File.Create(Path.Combine(destination, Path.GetFileName(src)))) a.CopyTo(b);
                    copied++;
                }
                catch (Exception e) { SessionLog.Fail("bug report copy", e); }
            }
            return copied;
        }
    }
}

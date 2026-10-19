using System;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace TLDRevamp
{
    /// Page Down: detached telemetry, numeric diagnostics and settings, zipped on a worker.
    public class FeedbackReporter
    {
        public static string Root => Path.Combine(Paths.BepInExRootPath, "tldrevamp-feedback");
        private readonly Telemetry _t;
        private string _pendingDir; private float _zipAt;
        private Task _gather; private Task<string> _zip;
        public static string LastZip;
        internal bool Pending => _pendingDir != null;

        public FeedbackReporter(Telemetry t) => _t = t;

        /// Bridge commands whose replies go into stats.json (each on its own: one failing doesn't spoil the rest).
        private static readonly string[] StatCmds = { "stats", "mp status", "net stats", "mp entities", "mp aistats", "mp combat", "mp shotfx", "mp blasts", "mp membergone", "mp poiusables" };

        public string Capture(string note, double? momentSt = null)
        {
            if (_pendingDir != null) return _pendingDir;
            try { return Gather(note, momentSt ?? SessionLog.St); }
            catch (Exception e) { SessionLog.Fail("report", e); return null; }
        }

        private string Gather(string note, double momentSt)
        {
            note = TelemetryPrivacy.Note(note);
            string stamp = TelemetryPrivacy.Stamp();
            string dir = Path.Combine(Root, "report-" + stamp);
            Directory.CreateDirectory(dir);

            var sb = new System.Text.StringBuilder();
            sb.Append("{\"note\":").Append(TelemetryPrivacy.Str(TelemetryPrivacy.Clean(note))).Append(",\"mod\":").Append(TelemetryPrivacy.Str(Plugin.Version)).Append(",\"build\":").Append(TelemetryPrivacy.Str(Plugin.BuildHash)).Append(",\"commit\":").Append(TelemetryPrivacy.Str(Plugin.CommitLabel)).Append(",\"game\":").Append(TelemetryPrivacy.Str(Plugin.GameBuild))
              .Append(",\"time\":").Append(TelemetryPrivacy.DateStr()).Append(",\"system\":").Append(SystemJson())
              .Append(",\"frame\":").Append(Safe(() => TelemetryPrivacy.Bridge("stats", _t.FrameStatsJson()))).Append(",\"scene\":").Append(Safe(() => TelemetryPrivacy.Numbers(_t.SceneStatsJson(), "focused rigidbodies rigidbodiesAwake renderers monobehaviours scanMs sampleSt playerPos")));
            foreach (var c in StatCmds) sb.Append(",").Append(TelemetryPrivacy.Str(c)).Append(":").Append(Safe(() => TelemetryPrivacy.Bridge(c, DebugBridge.Execute(c))));
            long snapshotStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var breakdown = _t.Breakdown.ReportSnapshot();
            var errors = Diagnostics.ErrorLog.ReportSnapshot();
            var fixes = SessionLog.FixesSnapshot();
            double snapshotMs = (System.Diagnostics.Stopwatch.GetTimestamp() - snapshotStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            sb.Append(",\"telemetrySnapshotMs\":").Append(Json.Num(snapshotMs)).Append('}');
            string stats = sb.ToString();
            SessionLog.Capture(Path.Combine(dir, "blackbox.json"), momentSt, note: note);
            var settings = new System.Text.StringBuilder();
            foreach (var e in RevampSettings.Entries) if (TelemetryPrivacy.Setting(e.Key) && e.Get != null)
                settings.Append(TelemetryPrivacy.SettingLine(e.Key, e.Get()));
            string settingsText = settings.ToString();
            string playLog = SessionLog.CurrentPath;
            SessionLog.Flush();
            _gather = Task.Run(() =>
            {
                File.WriteAllText(Path.Combine(dir, "stats.json"), stats, new System.Text.UTF8Encoding(false));
                WriteExtra(dir, "breakdown.json", breakdown);
                WriteExtra(dir, "errors.json", errors);
                WriteExtra(dir, "fixes.json", fixes);
                File.WriteAllText(Path.Combine(dir, "tldrevamp-settings.txt"), settingsText, new System.Text.UTF8Encoding(false));
                if (playLog != null) CopySession(dir, playLog);
            });

            _pendingDir = dir; _zipAt = Time.realtimeSinceStartup + 5.2f;
            Plugin.Instance.Overlay.Toast("Saving report…");
            Plugin.Log.LogInfo("Feedback report: " + dir);
            return dir;
        }

        /// Poll workers without waiting on Unity's thread; transfers and UI completion stay here.
        public void Tick()
        {
            if (_pendingDir == null) return;
            string dir = _pendingDir;
            if (_zip != null)
            {
                if (!_zip.IsCompleted) return;
                _pendingDir = null;
                if (_zip.IsFaulted)
                {
                    Plugin.Instance.Overlay.Toast("Report saved (folder): BepInEx/tldrevamp-feedback/" + Path.GetFileName(dir));
                    SessionLog.Fail("report zip", _zip.Exception);
                }
                else
                {
                    LastZip = _zip.Result;
                    Net.Reports.OnZipped(LastZip);
                    Plugin.Instance.Overlay.Toast("Report saved: BepInEx/tldrevamp-feedback/" + Path.GetFileName(LastZip));
                    Plugin.Log.LogInfo("Feedback report zipped: " + LastZip);
                }
                _zip = null; _gather = null;
                return;
            }
            if (Time.realtimeSinceStartup < _zipAt || !_gather.IsCompleted ||
                SessionLog.CapturePending(Path.Combine(dir, "blackbox.json"))) return;
            SessionLog.Flush();
            string playLog = SessionLog.CurrentPath;
            var gather = _gather;
            _zip = Task.Run(() =>
            {
                gather.GetAwaiter().GetResult();
                // Include aftermath lines after the black box has finished writing.
                if (playLog != null) CopySession(dir, playLog);
                string zip = Path.Combine(Root, "TLDRevamp-report-" + Path.GetFileName(dir).Substring("report-".Length) + ".zip");
                TelemetryPrivacy.Zip(dir, zip);
                Directory.Delete(dir, true);
                return zip;
            });
        }

        private static void CopySession(string dir, string path)
        {
            string sd = Path.Combine(dir, "sessions"); Directory.CreateDirectory(sd);
            CopyShared(path, Path.Combine(sd, Path.GetFileName(path)));
        }

        /// Opens the reports folder in the system's file browser.
        public static void OpenFolder()
        {
            try { Directory.CreateDirectory(Root); Application.OpenURL("file:///" + Root.Replace('\\', '/')); } catch { }
        }

        private static string SystemJson() =>
            "{\"os\":" + TelemetryPrivacy.OsStr(SystemInfo.operatingSystem) + ",\"cpu\":" + TelemetryPrivacy.Str(SystemInfo.processorType) + ",\"cores\":" + SystemInfo.processorCount +
            ",\"ramMB\":" + SystemInfo.systemMemorySize + ",\"gpu\":" + TelemetryPrivacy.Str(SystemInfo.graphicsDeviceName) + ",\"gpuMB\":" + SystemInfo.graphicsMemorySize +
            ",\"screen\":" + TelemetryPrivacy.Str(Screen.width + "x" + Screen.height) +
            ",\"unity\":" + TelemetryPrivacy.Str(Application.unityVersion) + "}";

        private static string Safe(Func<string> f)
        {
            try { var r = f(); return string.IsNullOrEmpty(r) ? "null" : r.TrimStart().StartsWith("{") || r.TrimStart().StartsWith("[") ? r : TelemetryPrivacy.Str(r); }
            catch { return "null"; }
        }
        private static void WriteExtra(string dir, string name, Func<string> json)
        {
            try { File.WriteAllText(Path.Combine(dir, name), json(), new System.Text.UTF8Encoding(false)); }
            catch (Exception e) { SessionLog.Fail("report " + name, e); }
        }

        private static void CopyShared(string src, string dst)
        {
            try
            {
                if (!File.Exists(src)) return;
                // the game and BepInEx hold their logs open: copy through a shared read
                using (var a = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var b = File.Create(dst))
                    a.CopyTo(b);
            }
            catch (Exception e) { SessionLog.Fail("report copy", e); }
        }
    }

}

using System;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace TLDRevamp
{
    /// Page Down (Plugin.KeyReport; Net.Reports) or bridge `report`: one zip to send to the developer, in
    /// BepInEx/tldrevamp-feedback/TLDRevamp-report-<time>.zip — screenshot, the mod's and the game's logs (this run and
    /// the previous one: after a crash the previous run is the one that matters), the session telemetry files
    /// (SessionLog), the mod's stats at that moment, settings and the machine's hardware. Unity writes the screenshot at
    /// the end of the frame, so the files are gathered now and zipped a moment later (Tick).
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
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string dir = Path.Combine(Root, "report-" + stamp);
            Directory.CreateDirectory(dir);
            try { ScreenCapture.CaptureScreenshot(Path.Combine(dir, "screenshot.png")); } catch { }

            var sb = new System.Text.StringBuilder();
            sb.Append("{\"note\":").Append(Json.Str(note)).Append(",\"mod\":").Append(Json.Str(Plugin.Version)).Append(",\"build\":").Append(Json.Str(Plugin.BuildHash)).Append(",\"game\":").Append(Json.Str(Plugin.GameBuild))
              .Append(",\"time\":").Append(Json.Str(DateTime.Now.ToString("o"))).Append(",\"system\":").Append(SystemJson())
              .Append(",\"frame\":").Append(Safe(() => _t.FrameStatsJson())).Append(",\"scene\":").Append(Safe(() => _t.SceneStatsJson()));
            foreach (var c in StatCmds) sb.Append(",").Append(Json.Str(c)).Append(":").Append(Safe(() => DebugBridge.Execute(c)));
            long snapshotStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var breakdown = _t.Breakdown.ReportSnapshot();
            var errors = Diagnostics.ErrorLog.ReportSnapshot();
            var fixes = SessionLog.FixesSnapshot();
            double snapshotMs = (System.Diagnostics.Stopwatch.GetTimestamp() - snapshotStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            sb.Append(",\"telemetrySnapshotMs\":").Append(Json.Num(snapshotMs)).Append('}');
            string stats = sb.ToString();
            SessionLog.Capture(Path.Combine(dir, "blackbox.json"), momentSt);
            string bepinex = Paths.BepInExRootPath, config = Paths.ConfigPath;
            string player = SafeStr(() => Application.consoleLogPath);
            SessionLog.Flush();
            _gather = Task.Run(() =>
            {
                File.WriteAllText(Path.Combine(dir, "stats.json"), stats);
                WriteExtra(dir, "breakdown.json", breakdown);
                WriteExtra(dir, "errors.json", errors);
                WriteExtra(dir, "fixes.json", fixes);
                CopyShared(Path.Combine(bepinex, "LogOutput.log"), Path.Combine(dir, "LogOutput.log"));
                CopyShared(Path.Combine(bepinex, "LogOutput.prev.log"), Path.Combine(dir, "LogOutput.prev.log"));
                if (!string.IsNullOrEmpty(player))
                {
                    CopyShared(player, Path.Combine(dir, "Player.log"));
                    CopyShared(Path.Combine(Path.GetDirectoryName(player), "Player-prev.log"), Path.Combine(dir, "Player-prev.log"));
                }
                CopyShared(Path.Combine(config, "tldrevamp-settings.txt"), Path.Combine(dir, "tldrevamp-settings.txt"));
                CopyShared(Path.Combine(config, "BepInEx.cfg"), Path.Combine(dir, "BepInEx.cfg"));
                foreach (string f in SessionLog.Recent(5)) CopySession(dir, f);
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
                System.IO.Compression.ZipFile.CreateFromDirectory(dir, zip, System.IO.Compression.CompressionLevel.Optimal, false);
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
            "{\"os\":" + Json.Str(SystemInfo.operatingSystem) + ",\"cpu\":" + Json.Str(SystemInfo.processorType) + ",\"cores\":" + SystemInfo.processorCount +
            ",\"ramMB\":" + SystemInfo.systemMemorySize + ",\"gpu\":" + Json.Str(SystemInfo.graphicsDeviceName) + ",\"gpuMB\":" + SystemInfo.graphicsMemorySize +
            ",\"api\":" + Json.Str(SystemInfo.graphicsDeviceVersion) + ",\"screen\":" + Json.Str(Screen.width + "x" + Screen.height) +
            ",\"unity\":" + Json.Str(Application.unityVersion) + "}";

        private static string Safe(Func<string> f)
        {
            try { var r = f(); return string.IsNullOrEmpty(r) ? "null" : r.TrimStart().StartsWith("{") || r.TrimStart().StartsWith("[") ? r : Json.Str(r); }
            catch (Exception e) { return Json.Str("error: " + e.Message); }
        }
        private static string SafeStr(Func<string> f) { try { return f(); } catch { return null; } }
        private static void WriteExtra(string dir, string name, Func<string> json)
        {
            try { File.WriteAllText(Path.Combine(dir, name), json()); }
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

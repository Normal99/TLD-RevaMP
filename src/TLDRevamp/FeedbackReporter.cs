using System;
using System.IO;
using BepInEx;
using UnityEngine;

namespace TLDRevamp
{
    /// F9 (or the F7 panel, or bridge `report`): one zip to send to the developer, in
    /// BepInEx/tldrevamp-feedback/TLDRevamp-report-<time>.zip — screenshot, the mod's and the game's logs (this run and
    /// the previous one: after a crash the previous run is the one that matters), the session telemetry files
    /// (SessionLog), the mod's stats at that moment, settings and the machine's hardware. Unity writes the screenshot at
    /// the end of the frame, so the files are gathered now and zipped a moment later (Tick).
    public class FeedbackReporter
    {
        public static string Root => Path.Combine(Paths.BepInExRootPath, "tldrevamp-feedback");
        private readonly Telemetry _t;
        private string _pendingDir; private float _zipAt;
        public static string LastZip;

        public FeedbackReporter(Telemetry t) => _t = t;

        /// Bridge commands whose replies go into stats.json (each on its own: one failing doesn't spoil the rest).
        private static readonly string[] StatCmds = { "stats", "mp status", "net stats", "mp entities", "mp aistats", "mp combat", "mp shotfx", "mp blasts", "mp poiusables" };

        public string Capture(string note)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dir = Path.Combine(Root, "report-" + stamp);
            Directory.CreateDirectory(dir);
            try { ScreenCapture.CaptureScreenshot(Path.Combine(dir, "screenshot.png")); } catch { }

            var sb = new System.Text.StringBuilder();
            sb.Append("{\"note\":").Append(Json.Str(note)).Append(",\"mod\":").Append(Json.Str(Plugin.Version)).Append(",\"build\":").Append(Json.Str(Plugin.BuildHash))
              .Append(",\"time\":").Append(Json.Str(DateTime.Now.ToString("o"))).Append(",\"system\":").Append(SystemJson())
              .Append(",\"frame\":").Append(Safe(() => _t.FrameStatsJson())).Append(",\"scene\":").Append(Safe(() => _t.SceneStatsJson()));
            foreach (var c in StatCmds) sb.Append(",").Append(Json.Str(c)).Append(":").Append(Safe(() => DebugBridge.Execute(c)));
            sb.Append("}");
            File.WriteAllText(Path.Combine(dir, "stats.json"), sb.ToString());

            CopyShared(Path.Combine(Paths.BepInExRootPath, "LogOutput.log"), Path.Combine(dir, "LogOutput.log"));
            CopyShared(Path.Combine(Paths.BepInExRootPath, "LogOutput.prev.log"), Path.Combine(dir, "LogOutput.prev.log"));
            string player = SafeStr(() => Application.consoleLogPath);
            if (!string.IsNullOrEmpty(player))
            {
                CopyShared(player, Path.Combine(dir, "Player.log"));
                CopyShared(Path.Combine(Path.GetDirectoryName(player), "Player-prev.log"), Path.Combine(dir, "Player-prev.log"));
            }
            CopyShared(Path.Combine(Paths.ConfigPath, "tldrevamp-settings.txt"), Path.Combine(dir, "tldrevamp-settings.txt"));
            CopyShared(Path.Combine(Paths.ConfigPath, "BepInEx.cfg"), Path.Combine(dir, "BepInEx.cfg"));
            SessionLog.Flush();
            var sessions = SessionLog.Recent(5);
            if (sessions.Length > 0)
            {
                string sd = Path.Combine(dir, "sessions"); Directory.CreateDirectory(sd);
                foreach (var f in sessions) CopyShared(f, Path.Combine(sd, Path.GetFileName(f)));
            }

            _pendingDir = dir; _zipAt = Time.realtimeSinceStartup + 1.5f;
            Plugin.Instance.Overlay.Toast("Saving report…");
            Plugin.Log.LogInfo("Feedback report: " + dir);
            return dir;
        }

        /// Runner, every frame: zip a gathered report once its screenshot is written (or after 5 s without one).
        public void Tick()
        {
            if (_pendingDir == null || Time.realtimeSinceStartup < _zipAt) return;
            string dir = _pendingDir;
            if (!File.Exists(Path.Combine(dir, "screenshot.png")) && Time.realtimeSinceStartup < _zipAt + 3.5f) return;
            _pendingDir = null;
            string zip = Path.Combine(Root, "TLDRevamp-report-" + Path.GetFileName(dir).Substring("report-".Length) + ".zip");
            try
            {
                if (File.Exists(zip)) File.Delete(zip);
                System.IO.Compression.ZipFile.CreateFromDirectory(dir, zip, System.IO.Compression.CompressionLevel.Optimal, false);
                Directory.Delete(dir, true);
                LastZip = zip;
                Plugin.Instance.Overlay.Toast("Report saved: BepInEx/tldrevamp-feedback/" + Path.GetFileName(zip));
                Plugin.Log.LogInfo("Feedback report zipped: " + zip);
            }
            catch (Exception e)
            {
                // the folder stays: it can be sent as it is
                Plugin.Instance.Overlay.Toast("Report saved (folder): BepInEx/tldrevamp-feedback/" + Path.GetFileName(dir));
                Plugin.Log.LogWarning("Report zip failed: " + e.Message);
            }
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
            catch (Exception e) { Plugin.Log.LogWarning("Report: copy " + Path.GetFileName(src) + " failed: " + e.Message); }
        }
    }

    internal static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "\"\"";
            var sb = new System.Text.StringBuilder(s.Length + 2).Append('"');
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}

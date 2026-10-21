using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TLDRevamp.Replay
{
    /// The bridge surface of the replay (tools/replay.py). Arguments are a verb and numbers, or a replay file name
    /// that ReplayFiles.SafeName accepts; nothing free-text is taken.
    ///
    ///   replay [status]              - recorder and playback state (read-only)
    ///   replay list                  - the saved recordings, newest first (read-only)
    ///   replay near [m]              - the copied pieces near the camera (read-only; for checking a view)
    ///   replay record | stop         - start or stop recording
    ///   replay play [name [disk]]    - play the recording just made, the newest file, or the named one (disk: read the file
    ///                                  even when it is the one just made, as a later session would)
    ///   replay pause | resume | toggle | end
    ///   replay seek <s> | jump <s> | speed <x>
    ///   replay camera free|orbit|eyes|follow
    ///   replay subject <player index>
    ///   replay ui on|off
    public static class ReplayBridge
    {
        public static string Command(string arg)
        {
            arg = (arg ?? "").Trim();
            if (arg.Length > 128) return "{\"error\":\"the replay command is too long\"}";
            var a = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string verb = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
            switch (verb)
            {
                case "status": return "{\"recorder\":" + ReplayRecorder.Status() + ",\"viewer\":" + ReplayViewer.Status() + "}";
                case "list": return List();
                case "near": return Number(a, out double range) ? ReplayViewer.Near((float)range) : ReplayViewer.Near(3f);
                case "record": case "start": return ReplayRecorder.Start();
                case "stop": return ReplayRecorder.Stop();
                case "play":
                    if (a.Length == 3 && a[2] == "disk") return ReplayViewer.PlayFile(a[1], fromDisk: true);
                    return a.Length == 2 ? ReplayViewer.PlayFile(a[1]) : a.Length == 1 ? ReplayViewer.PlayLatest() : "{\"error\":\"replay play [name [disk]]\"}";
                case "pause": return ReplayViewer.Pause();
                case "resume": return ReplayViewer.Resume();
                case "toggle": return ReplayViewer.Toggle();
                case "end": return ReplayViewer.End("ended from the bridge");
                case "seek": return Number(a, out double t) ? ReplayViewer.Seek(t) : "{\"error\":\"replay seek <seconds>\"}";
                case "jump": return Number(a, out double j) ? ReplayViewer.Jump(j) : "{\"error\":\"replay jump <seconds>\"}";
                case "speed": return Number(a, out double x) ? ReplayViewer.Speed((float)x) : "{\"error\":\"replay speed <0.1|0.25|0.5|1|2|4>\"}";
                case "camera": return a.Length == 2 ? ReplayViewer.Camera(a[1].ToLowerInvariant()) : "{\"error\":\"replay camera free|orbit|eyes|follow\"}";
                case "subject": return Number(a, out double s) && s == Math.Floor(s) ? ReplayViewer.Subject((int)s) : "{\"error\":\"replay subject <index>\"}";
                case "ui":
                    if (a.Length == 2 && (a[1] == "on" || a[1] == "off")) { ReplayViewer.ShowUi = a[1] == "on"; return ReplayViewer.Status(); }
                    return "{\"error\":\"replay ui on|off\"}";
                default:
                    return "{\"error\":\"replay [status|list|record|stop|play [name]|pause|resume|toggle|end|seek s|jump s|speed x|camera free|orbit|eyes|follow|subject n|ui on|off]\"}";
            }
        }

        private static bool Number(string[] a, out double n)
        {
            n = 0;
            return a.Length == 2 && double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out n) && ReplayMath.Finite(n);
        }

        private static string List()
        {
            var sb = new StringBuilder("{\"folder\":\"BepInEx/tldrevamp-replays\",\"replays\":[");
            bool first = true;
            foreach (var f in ReplayFiles.List(ReplayRecorder.Folder))
            {
                long size = 0;
                try { size = new FileInfo(f).Length; } catch { }
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(Json.Str(Path.GetFileName(f))).Append(",\"bytes\":").Append(size).Append('}');
            }
            return sb.Append("]}").ToString();
        }
    }
}

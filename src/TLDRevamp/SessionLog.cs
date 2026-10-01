using System;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace TLDRevamp
{
    /// Session telemetry: while a multiplayer session runs, one JSON line every 5 s into
    /// BepInEx/tldrevamp-feedback/sessions/<start>-<role>.jsonl — frame times, ping/loss/bandwidth per connection,
    /// players, and the sync counters (states, stale, jumps, hand-offs, creatures, combat). The F9 report carries the
    /// latest five files, so a player can send the whole evening's numbers after playing. The newest 20 are kept.
    public static class SessionLog
    {
        public static float Interval = 5f;
        private const int Keep = 20;
        private static StreamWriter _w;
        private static string _path;
        private static float _next, _start;
        private static long _hitches0;

        public static string Dir => Path.Combine(FeedbackReporter.Root, "sessions");

        /// Runner, every frame.
        public static void Tick()
        {
            bool on = Net.Mp.Server != null || Net.Mp.Client != null;
            if (!on) { if (_w != null) Close("session ended"); return; }
            float now = Time.realtimeSinceStartup;
            if (_w == null) Open(now);
            if (_w == null || now < _next) return;
            _next = now + Interval;
            try { _w.WriteLine(Line(now)); _w.Flush(); }   // every line on disk: a crash loses nothing
            catch (Exception e) { Plugin.Log.LogWarning("Session log write failed: " + e.Message); Close("write failed"); }
        }

        private static void Open(float now)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string role = Net.Mp.Server != null ? (Net.DedicatedServer.Enabled ? "dedicated" : "host") : "client";
                _path = Path.Combine(Dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + role + ".jsonl");
                _w = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
                _start = now; _next = now + 1f;
                _hitches0 = Plugin.Instance.Telemetry.Hitches;
                _w.WriteLine("{\"start\":" + Json.Str(DateTime.Now.ToString("o")) + ",\"mod\":" + Json.Str(Plugin.Version) + ",\"build\":" + Json.Str(Plugin.BuildHash) +
                             ",\"role\":" + Json.Str(role) + ",\"seed\":" + Net.Mp.Seed() + "}");
                _w.Flush();
                Prune();
                Plugin.Log.LogInfo("Session log: " + _path);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Session log open failed: " + e.Message); _w = null; _next = now + 30f; }
        }

        private static string Line(float now)
        {
            var t = Plugin.Instance.Telemetry;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            Vector3d me = default;
            try { if (mainscript.s != null && mainscript.s.player != null) me = mainscript.GlobalFromUnityPos(mainscript.s.player.transform.position); } catch { }
            string line = "{\"t\":" + (now - _start).ToString("F0", ic) +
                          ",\"fps\":{\"p50ms\":" + t.Percentile(0.5f).ToString("F1", ic) + ",\"p99ms\":" + t.Percentile(0.99f).ToString("F1", ic) +
                          ",\"hitches\":" + (t.Hitches - _hitches0) + ",\"managedMB\":" + t.ManagedMB + "}" +
                          ",\"pos\":[" + me.x.ToString("F0", ic) + "," + me.z.ToString("F0", ic) + "]" +
                          ",\"net\":" + Safe(Net.Mp.NetStats) + ",\"mp\":" + Safe(Net.Mp.Status) +
                          ",\"ai\":" + Safe(Net.Entities.AiStats) + ",\"combat\":" + Safe(Net.PlayerCombat.Stats) +
                          ",\"handoffs\":" + Net.Entities.Handoffs + "}";
            return line;
        }

        /// Pushes buffered lines to disk (the report copies the file).
        public static void Flush() { try { _w?.Flush(); } catch { } }

        private static void Close(string why)
        {
            try { _w.WriteLine("{\"end\":" + Json.Str(DateTime.Now.ToString("o")) + ",\"why\":" + Json.Str(why) + "}"); _w.Dispose(); } catch { }
            _w = null;
        }

        public static string[] Recent(int n)
        {
            try { return Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.jsonl").OrderByDescending(f => f).Take(n).ToArray() : new string[0]; }
            catch { return new string[0]; }
        }

        private static void Prune()
        {
            try { foreach (var f in Directory.GetFiles(Dir, "*.jsonl").OrderByDescending(f => f).Skip(Keep)) File.Delete(f); } catch { }
        }

        private static string Safe(Func<string> f)
        {
            try { var r = f(); return string.IsNullOrEmpty(r) ? "null" : r; } catch (Exception e) { return Json.Str("error: " + e.Message); }
        }
    }
}

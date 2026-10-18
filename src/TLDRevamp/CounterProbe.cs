using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;

namespace TLDRevamp
{
    /// Bridge `counters list [filter] | start [filter] | read | stop | gc`: which Unity profiler counters the shipped player
    /// really fills (most markers report invalid outside development builds), and the runtime's GC mode. Test tooling only.
    public static class CounterProbe
    {
        private static readonly List<(string name, string cat, ProfilerRecorder rec)> Started = new List<(string, string, ProfilerRecorder)>();

        private static IEnumerable<(ProfilerRecorderHandle h, ProfilerRecorderDescription d)> Available(string filter)
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            foreach (var h in handles)
            {
                var d = ProfilerRecorderHandle.GetDescription(h);
                if (string.IsNullOrEmpty(filter) || d.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || d.Category.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    yield return (h, d);
            }
        }

        public static string Run(string arg)
        {
            var a = arg.Split(new[] { ' ' }, 2);
            string filter = a.Length > 1 ? a[1] : "";
            var sb = new StringBuilder();
            switch (a[0])
            {
                case "list":
                    sb.Append('[');
                    foreach (var (h, d) in Available(filter))
                    {
                        if (sb.Length > 1) sb.Append(',');
                        sb.Append("{\"n\":").Append(Json.Str(d.Name)).Append(",\"c\":").Append(Json.Str(d.Category.Name))
                          .Append(",\"u\":").Append(Json.Str(d.UnitType.ToString())).Append(",\"f\":").Append(Json.Str(d.Flags.ToString())).Append('}');
                    }
                    return sb.Append(']').ToString();
                case "start":
                    foreach (var (h, d) in Available(filter))
                        try { Started.Add((d.Name, d.Category.Name, ProfilerRecorder.StartNew(d.Category, d.Name, 1))); } catch { }
                    return "{\"started\":" + Started.Count + "}";
                case "read":
                    sb.Append('[');
                    foreach (var (name, cat, rec) in Started)
                    {
                        if (!rec.Valid || rec.LastValue == 0) continue;
                        if (sb.Length > 1) sb.Append(',');
                        sb.Append("{\"n\":").Append(Json.Str(name)).Append(",\"c\":").Append(Json.Str(cat)).Append(",\"last\":").Append(rec.LastValue).Append(",\"cur\":").Append(rec.CurrentValue).Append('}');
                    }
                    return sb.Append(']').ToString();
                case "stop":
                    foreach (var s in Started) s.rec.Dispose();
                    Started.Clear();
                    return "{\"ok\":true}";
                case "gc":
                    string inc;
                    try { inc = MonoIsIncremental() ? "true" : "false"; } catch (Exception e) { inc = Json.Str(e.GetType().Name); }
                    return "{\"unityIncremental\":" + (UnityEngine.Scripting.GarbageCollector.isIncremental ? "true" : "false") +
                        ",\"timeSliceNs\":" + UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds +
                        ",\"gcMode\":" + Json.Str(UnityEngine.Scripting.GarbageCollector.GCMode.ToString()) +
                        ",\"monoIncremental\":" + inc +
                        ",\"allocatedForThread\":" + GC.GetAllocatedBytesForCurrentThread() + "}";
            }
            return "{\"error\":\"counters list [filter]|start [filter]|read|stop|gc\"}";
        }

        [DllImport("mono-2.0-bdwgc.dll", EntryPoint = "mono_gc_is_incremental")]
        private static extern bool MonoIsIncremental();
    }
}

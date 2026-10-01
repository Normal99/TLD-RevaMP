using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace TLDRevamp
{
    /// On-demand profiler: wraps every Update/LateUpdate/FixedUpdate/OnGUI declared in the game's own
    /// assembly with a timing + allocation probe. Start, play or bench for a while, stop, read the top list.
    /// Patching is removed on stop, so there's no cost when not profiling.
    public static class ScriptProfiler
    {
        private static readonly string[] Targets = { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };
        private static Harmony _harmony;
        private static Stopwatch _wall;

        private class Stat { public long Ticks, Alloc, Calls, MaxTicks, Over5ms, FrameTicks, SlowTicks; }
        private static readonly List<Stat> Touched = new List<Stat>();
        /// Slow-frame attribution: a frame window (Runner.Update to Runner.Update) longer than SlowFrameMs adds each
        /// method's time in that window to SlowTicks. Nested probed methods count in both (inclusive times).
        public static float SlowFrameMs = 25f;
        private static long _slowFrames, _frames, _lastFrameEnd;
        private static readonly Dictionary<MethodBase, Stat> Stats = new Dictionary<MethodBase, Stat>();

        public static bool Running => _harmony != null;

        /// methods == null: every Update/LateUpdate/FixedUpdate/OnGUI in the game.
        /// Otherwise "Type.Method" names; every overload of each is probed (drill-down without a rebuild).
        public static string Start(string[] methods)
        {
            if (Running) return "{\"error\":\"already running\"}";
            Stats.Clear();
            Touched.Clear();
            _slowFrames = _frames = 0;
            _lastFrameEnd = 0;
            _harmony = new Harmony(Plugin.Guid + ".profiler");
            var prefix = new HarmonyMethod(typeof(ScriptProfiler), nameof(Prefix));
            var postfix = new HarmonyMethod(typeof(ScriptProfiler), nameof(Postfix));
            int patched = 0, failed = 0;
            if (methods != null && methods.Length == 1 && methods[0] == "coroutines")
            {
                // Coroutines: compiler-generated iterator classes (<Name>d__N) implementing IEnumerator in the game
                var asms = AppDomain.CurrentDomain.GetAssemblies().Where(x => { var n = x.GetName().Name; return !n.StartsWith("System") && n != "mscorlib" && !n.StartsWith("Mono.") && !n.StartsWith("0Harmony") && !n.StartsWith("BepInEx") && !n.StartsWith("TLDRevamp") && !n.StartsWith("netstandard"); });
                foreach (var type in asms.SelectMany(x => { try { return x.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); } }))
                {
                    if (!type.Name.StartsWith("<") || !typeof(System.Collections.IEnumerator).IsAssignableFrom(type) || type.IsGenericTypeDefinition || type.ContainsGenericParameters) continue;
                    var m = type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (m == null) continue;
                    try { _harmony.Patch(m, prefix, postfix); Stats[m] = new Stat(); patched++; }
                    catch { failed++; }
                }
                _wall = Stopwatch.StartNew();
                return "{\"patched\":" + patched + ",\"failed\":" + failed + "}";
            }
            if (methods != null)
            {
                foreach (var spec in methods)
                {
                    int dot = spec.LastIndexOf('.');
                    var type = dot > 0 ? Reflect.FindType(spec.Substring(0, dot)) : null;
                    if (type == null) { failed++; continue; }
                    foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.Name != spec.Substring(dot + 1) || m.IsAbstract || m.IsGenericMethodDefinition) continue;
                        try { _harmony.Patch(m, prefix, postfix); Stats[m] = new Stat(); patched++; }
                        catch { failed++; }
                    }
                }
                _wall = Stopwatch.StartNew();
                return "{\"patched\":" + patched + ",\"failed\":" + failed + "}";
            }
            foreach (var type in typeof(mainscript).Assembly.GetTypes())
            {
                if (!typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(type) || type.IsGenericTypeDefinition) continue;
                foreach (var name in Targets)
                {
                    var m = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                        null, Type.EmptyTypes, null);
                    if (m == null || m.IsAbstract) continue;
                    try { _harmony.Patch(m, prefix, postfix); Stats[m] = new Stat(); patched++; }
                    catch { failed++; }
                }
            }
            _wall = Stopwatch.StartNew();
            return "{\"patched\":" + patched + ",\"failed\":" + failed + "}";
        }

        public static string Stop(int top)
        {
            if (!Running) return "{\"error\":\"not running\"}";
            _harmony.UnpatchSelf();
            _harmony = null;
            double seconds = _wall.Elapsed.TotalSeconds;
            double tickMs = 1000.0 / Stopwatch.Frequency;

            string List(IEnumerable<KeyValuePair<MethodBase, Stat>> rows) =>
                string.Join(",", rows.Take(top).Select(kv =>
                    "{\"m\":" + Json.Str(Name(kv.Key) + "(" + string.Join(",", kv.Key.GetParameters().Select(p => p.ParameterType.Name)) + ")") +
                    ",\"msPerSec\":" + (kv.Value.Ticks * tickMs / seconds).ToString("F3") +
                    ",\"kbPerSec\":" + (kv.Value.Alloc / 1024.0 / seconds).ToString("F1") +
                    ",\"callsPerSec\":" + (kv.Value.Calls / seconds).ToString("F0") +
                    ",\"maxMs\":" + (kv.Value.MaxTicks * tickMs).ToString("F2") +
                    ",\"over5ms\":" + kv.Value.Over5ms + "}"));

            var live = Stats.Where(kv => kv.Value.Calls > 0).ToList();
            var sb = new StringBuilder();
            sb.Append("{\"seconds\":").Append(seconds.ToString("F1"))
              .Append(",\"totalMsPerSec\":").Append((live.Sum(kv => kv.Value.Ticks) * tickMs / seconds).ToString("F2"))
              .Append(",\"totalKbPerSec\":").Append((live.Sum(kv => kv.Value.Alloc) / 1024.0 / seconds).ToString("F1"))
              .Append(",\"byTime\":[").Append(List(live.OrderByDescending(kv => kv.Value.Ticks))).Append(']')
              .Append(",\"byAlloc\":[").Append(List(live.OrderByDescending(kv => kv.Value.Alloc))).Append(']')
              .Append(",\"bySpike\":[").Append(List(live.OrderByDescending(kv => kv.Value.MaxTicks))).Append(']')
              .Append(",\"frames\":").Append(_frames).Append(",\"slowFrames\":").Append(_slowFrames).Append(",\"slowFrameMs\":").Append(SlowFrameMs)
              .Append(",\"bySlowFrames\":[").Append(string.Join(",", live.Where(kv => kv.Value.SlowTicks > 0).OrderByDescending(kv => kv.Value.SlowTicks).Take(top).Select(kv =>
                  "{\"m\":" + Json.Str(Name(kv.Key)) +
                  ",\"msPerSlowFrame\":" + (kv.Value.SlowTicks * tickMs / Math.Max(1, _slowFrames)).ToString("F3") +
                  ",\"msPerFrameAll\":" + (kv.Value.Ticks * tickMs / Math.Max(1, _frames)).ToString("F3") + "}"))).Append("]}");
            return sb.ToString();
        }

        private static string Name(MethodBase m)
        {
            var t = m.DeclaringType;
            return t.DeclaringType != null && t.Name.StartsWith("<") ? t.DeclaringType.Name + "." + t.Name : t.Name + "." + m.Name;
        }

        private static readonly long Over5msTicks = Stopwatch.Frequency / 200;

        /// Called once per frame from the mod's Runner (main thread).
        public static void FrameEnd()
        {
            if (!Running) return;
            long now = Stopwatch.GetTimestamp();
            if (_lastFrameEnd != 0)
            {
                _frames++;
                bool slow = (now - _lastFrameEnd) * 1000.0 / Stopwatch.Frequency > SlowFrameMs;
                if (slow) _slowFrames++;
                foreach (var s in Touched) { if (slow) s.SlowTicks += s.FrameTicks; s.FrameTicks = 0; }
            }
            else foreach (var s in Touched) s.FrameTicks = 0;
            Touched.Clear();
            _lastFrameEnd = now;
        }

        private struct Probe { public long Ticks, Mem; }

        /// Unity's main thread; set at plugin load. Probes ignore other threads (the Stats dictionary isn't
        /// thread-safe, and terrain/road workers call some of the profiled game functions).
        public static int MainThreadId = -1;

        private static void Prefix(out Probe __state)
        {
            __state.Mem = GC.GetTotalMemory(false);
            __state.Ticks = Stopwatch.GetTimestamp();
        }

        private static void Postfix(MethodBase __originalMethod, Probe __state)
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != MainThreadId) return;
            long ticks = Stopwatch.GetTimestamp() - __state.Ticks;
            long alloc = GC.GetTotalMemory(false) - __state.Mem;
            if (!Stats.TryGetValue(__originalMethod, out var s)) return;
            s.Ticks += ticks;
            s.Calls++;
            if (ticks > s.MaxTicks) s.MaxTicks = ticks;
            if (ticks > Over5msTicks) s.Over5ms++;
            if (s.FrameTicks == 0) Touched.Add(s);
            s.FrameTicks += ticks;
            if (alloc > 0) s.Alloc += alloc; // negative = a GC ran mid-call; skip that sample
        }
    }
}

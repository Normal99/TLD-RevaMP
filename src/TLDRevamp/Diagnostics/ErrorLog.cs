using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Diagnostics
{
    /// Every error and exception the game logs (Unity's own log: the game's code, ours, patched methods), counted by kind —
    /// the message and the first stack line — so a test can read what went wrong on each machine during its run (bridge
    /// `errors [reset]`). BepInEx logs are overwritten at each launch; a two-machine run restarts both games.
    public static class ErrorLog
    {
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, int> Kinds = new Dictionary<string, int>();
        private static readonly List<string> Order = new List<string>();
        public static long Errors, Exceptions;
        private const int MaxKinds = 60;

        public static void Install() => Application.logMessageReceivedThreaded += OnLog;

        private static void OnLog(string msg, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            string first = "";
            if (!string.IsNullOrEmpty(stack)) { int nl = stack.IndexOf('\n'); first = (nl > 0 ? stack.Substring(0, nl) : stack).Trim(); }
            string key = (msg ?? "").Trim();
            if (key.Length > 200) key = key.Substring(0, 200);
            if (first.Length > 0) key += " @ " + (first.Length > 160 ? first.Substring(0, 160) : first);
            lock (Lock)
            {
                if (type == LogType.Exception) Exceptions++; else Errors++;
                if (Kinds.TryGetValue(key, out int n)) Kinds[key] = n + 1;
                else if (Kinds.Count < MaxKinds) { Kinds[key] = 1; Order.Add(key); }
            }
        }

        public static string Json(bool reset)
        {
            lock (Lock)
            {
                var rows = new List<string>();
                foreach (var k in Order) rows.Add("{\"n\":" + Kinds[k] + ",\"what\":" + TLDRevamp.Json.Str(k) + "}");
                string s = "{\"errors\":" + Errors + ",\"exceptions\":" + Exceptions + ",\"kinds\":[" + string.Join(",", rows) + "]}";
                if (reset) { Kinds.Clear(); Order.Clear(); Errors = Exceptions = 0; }
                return s;
            }
        }
    }
}

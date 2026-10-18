using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace TLDRevamp
{
    internal sealed class TelemetryCounters
    {
        private readonly FieldInfo[] _fields;
        private readonly string[] _keys;
        private readonly long[] _last;
        public TelemetryCounters()
        {
            var fields = new List<FieldInfo>();
            foreach (var t in typeof(Plugin).Assembly.GetTypes())
                if (t.Namespace == "TLDRevamp.Fixes")
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                        if (!f.IsLiteral && (f.FieldType == typeof(long) || f.FieldType == typeof(int))) fields.Add(f);
            fields.Sort((a, b) => string.CompareOrdinal(a.DeclaringType.FullName + a.Name, b.DeclaringType.FullName + b.Name));
            _fields = fields.ToArray(); _keys = new string[_fields.Length]; _last = new long[_fields.Length];
            for (int i = 0; i < _fields.Length; i++) _keys[i] = Json.Str(_fields[i].DeclaringType.Name + "." + _fields[i].Name);
        }
        public Func<string> ReportSnapshot()
        {
            var values = new long[_fields.Length];
            for (int i = 0; i < values.Length; i++)
                try { values[i] = Convert.ToInt64(_fields[i].GetValue(null)); } catch { }
            return () =>
            {
                var b = new StringBuilder("{");
                for (int i = 0; i < values.Length; i++)
                { if (i > 0) b.Append(','); b.Append(_keys[i]).Append(':').Append(values[i]); }
                return b.Append('}').ToString();
            };
        }
        public string Read(bool delta)
        {
            var b = new StringBuilder("{"); bool first = true;
            for (int i = 0; i < _fields.Length; i++)
            {
                long value; try { value = Convert.ToInt64(_fields[i].GetValue(null)); } catch { continue; }
                long n = delta ? value - _last[i] : value;
                if (delta) _last[i] = value;
                if (delta && n == 0) continue;
                if (!first) b.Append(','); first = false;
                b.Append(_keys[i]).Append(':').Append(n);
            }
            return b.Append('}').ToString();
        }
    }
}

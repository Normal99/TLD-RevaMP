using HarmonyLib;

namespace TLDRevamp.Fixes
{
    /// mainscript.RefreshHelpTexts runs every frame while the player looks at something with a help text (a car: the
    /// fpscontroller's "help text shown" flag is cleared every frame, so the refresh is never skipped). Each call starts with
    /// RefreshHelpTextLanguage, which builds the nine car/motorcycle/bike help arrays again: ~75 strings from the
    /// language table and the key bindings, each binding name through KeyCode.ToString() (Mono's enum formatting:
    /// reflection plus a new string). Measured standing at the start car: 76 GetName calls a frame, ~0.1 ms/frame and
    /// ~25 KB/s of garbage.
    ///
    /// Those strings depend only on the language table (ls.l.ww, replaced when a language loads) and the bindings
    /// (settingsscript.s.S.inputs: every key's KeyCode, axis flags and axis name). The skip happens only when all of that
    /// is unchanged since the last build, so the arrays already hold equal strings; RefreshHelpTexts then hands them to
    /// the help texts exactly as before. Exact: the arrays keep their contents, only their identity is reused.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.HelpTextCache.Enabled
    [HarmonyPatch(typeof(mainscript), "RefreshHelpTextLanguage")]
    public static class HelpTextCache
    {
        public static bool Enabled = true;
        public static long Built, Skipped;

        private struct Key { public object Keys; public UnityEngine.KeyCode Code; public bool Axis, Invert; public int Joy, JoyAxis; public string AxisName; }
        private static Key[] _seen = new Key[0];
        private static object _lang, _words, _inputs, _owner;
        private static int _count = -1;
        private static bool _building;

        [HarmonyPrefix]
        private static bool Prefix(mainscript __instance)
        {
            if (!Enabled) { _count = -1; return true; }
            if (Unchanged(__instance)) { Skipped++; return false; }
            Built++;
            _building = true;
            return true; // the original builds the arrays; the postfix remembers what they were built from
        }

        [HarmonyPostfix]
        private static void Postfix(mainscript __instance)
        {
            if (_building) Remember(__instance); // postfixes also run after a skip
            _building = false;
        }

        private static bool Unchanged(mainscript m)
        {
            var inputs = settingsscript.s?.S?.inputs;
            if (_count < 0 || inputs == null || !ReferenceEquals(m, _owner) || !ReferenceEquals(inputs, _inputs) || !ReferenceEquals(ls.l, _lang) ||
                (ls.l != null && !ReferenceEquals(ls.l.ww, _words))) return false;
            int n = 0;
            for (int i = 0; i < inputs.Length; i++)
            {
                var keys = inputs[i]?.keys;
                if (keys == null) { if (n >= _count || _seen[n++].Keys != null) return false; continue; }
                for (int j = 0; j < keys.Length; j++)
                {
                    if (n >= _count) return false;
                    var k = keys[j]; var s = _seen[n++];
                    if (!ReferenceEquals(s.Keys, keys) || k == null || k.key != s.Code || k.axis != s.Axis || k.invertAxis != s.Invert ||
                        k.joy != s.Joy || k.joyaxis != s.JoyAxis || !string.Equals(k.inputAxis, s.AxisName)) return false;
                }
            }
            return n == _count;
        }

        private static void Remember(mainscript m)
        {
            var inputs = settingsscript.s?.S?.inputs;
            if (inputs == null) { _count = -1; return; }
            int n = 0;
            foreach (var input in inputs) n += input?.keys?.Length ?? 1;
            if (_seen.Length < n) _seen = new Key[n];
            n = 0;
            foreach (var input in inputs)
            {
                var keys = input?.keys;
                if (keys == null) { _seen[n++] = default; continue; }
                foreach (var k in keys)
                    _seen[n++] = k == null ? new Key { Keys = keys, AxisName = "\0null" } :
                        new Key { Keys = keys, Code = k.key, Axis = k.axis, Invert = k.invertAxis, Joy = k.joy, JoyAxis = k.joyaxis, AxisName = k.inputAxis };
            }
            _count = n; _inputs = inputs; _owner = m; _lang = ls.l; _words = ls.l?.ww;
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"built\":" + Built + ",\"skipped\":" + Skipped + "}";
    }
}

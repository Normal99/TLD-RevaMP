using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// inputscript.oneKey.GetName (a key binding's name for the on-screen hints, "[E]: drive") runs several times every
    /// frame from mainscript's hint texts (4 a frame standing still, ~10 while interacting, ~75 a frame while looking at a
    /// car's help text) and formats the KeyCode with Enum.ToString each time: Mono's reflection-based enum formatting,
    /// about a microsecond and a new string per call.
    ///
    /// The name depends only on the key's fields (key, axis, joy, joyaxis, inputAxis, invertAxis) and on the language
    /// table (ls.l.ww: "Mouse ScrollWheel", Mouse0-2 are translated; the table is replaced when a language loads). The
    /// prefix returns the string made last time for this key while all of those are the same, else runs the original
    /// and remembers its result. Exact (the private field the original also writes, s, already holds that string).
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.KeyNameCache.Enabled
    [HarmonyPatch(typeof(inputscript.oneKey), nameof(inputscript.oneKey.GetName))]
    public static class KeyNameCache
    {
        public static bool Enabled = true;
        public static long Named, Reused;

        private sealed class Last { public KeyCode Key; public bool Axis, Invert; public int Joy, JoyAxis; public string AxisName; public object Words; public string Name; }
        private static readonly ConditionalWeakTable<inputscript.oneKey, Last> Cache = new ConditionalWeakTable<inputscript.oneKey, Last>();

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(inputscript.oneKey __instance, ref string __result, out Last __state)
        {
            __state = null;
            if (!Enabled || ls.l == null) return true;
            var k = __instance;
            var last = Cache.GetValue(k, _ => new Last());
            if (last.Name != null && last.Key == k.key && last.Axis == k.axis && last.Invert == k.invertAxis && last.Joy == k.joy &&
                last.JoyAxis == k.joyaxis && ReferenceEquals(last.AxisName, k.inputAxis) && ReferenceEquals(last.Words, ls.l.ww))
            {
                Reused++;
                __result = last.Name;
                return false;
            }
            __state = last;
            return true;
        }

        [HarmonyPostfix]
        private static void Postfix(inputscript.oneKey __instance, string __result, Last __state)
        {
            if (__state == null) return; // skipped, or not cached
            var k = __instance;
            Named++;
            __state.Key = k.key; __state.Axis = k.axis; __state.Invert = k.invertAxis; __state.Joy = k.joy; __state.JoyAxis = k.joyaxis;
            __state.AxisName = k.inputAxis; __state.Words = ls.l?.ww; __state.Name = __result;
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"named\":" + Named + ",\"reused\":" + Reused + "}";
    }
}

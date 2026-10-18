using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace TLDRevamp.Fixes
{
    /// inputscript.DriveStuff, every frame, writes the controller calibration labels of the settings menu:
    ///   mainscript.s.menu.TSteer.text = $"Steer: {num:0.00}";   (and Throttle, Brake, Clutch, HandBrake)
    /// five string.Format calls with a boxed float: ~465 bytes of garbage a frame (~90 KB/s at 200 FPS), menu open or not.
    /// UI Text ignores an equal string, so only the formatting does anything when the value hasn't changed.
    ///
    /// Transpiler on those five calls only: "box float; call string.Format(string, object)" becomes
    /// Format(format, value, site), which returns the string it made last time at that call site when the format, the
    /// float (bit for bit) and the culture are the same, and calls string.Format exactly as before otherwise. Exact.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.AxisLabelCache.Enabled
    [HarmonyPatch(typeof(inputscript), "DriveStuff")]
    public static class AxisLabelCache
    {
        public static bool Enabled = true;
        public static long Formatted, Reused, Patched;

        private static readonly MethodInfo StringFormat = AccessTools.Method(typeof(string), nameof(string.Format), new[] { typeof(string), typeof(object) });
        private static readonly MethodInfo Helper = AccessTools.Method(typeof(AxisLabelCache), nameof(Format));

        private sealed class Site { public string Format, Text; public int Bits; public CultureInfo Culture; }
        private static readonly List<Site> Sites = new List<Site>();

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Box && Equals(list[i].operand, typeof(float)) && list[i + 1].Calls(StringFormat))
                {
                    // same stack (format, float): drop the box, pass the call site's number, call Format(string, float, int)
                    list[i].opcode = OpCodes.Ldc_I4; list[i].operand = Sites.Count;
                    list[i + 1].operand = Helper;
                    Sites.Add(new Site());
                    Patched++;
                }
            }
            if (Patched != 5) Plugin.Log.LogWarning("AxisLabelCache: expected 5 label formats in inputscript.DriveStuff, found " + Patched);
            return list;
        }

        public static string Format(string format, float value, int site)
        {
            var s = Sites[site];
            int bits = BitConverter.SingleToInt32Bits(value);
            var culture = CultureInfo.CurrentCulture;
            if (Enabled && s.Text != null && bits == s.Bits && ReferenceEquals(format, s.Format) && ReferenceEquals(culture, s.Culture))
            {
                Reused++;
                return s.Text;
            }
            Formatted++;
            string text = string.Format(format, value);
            s.Text = text; s.Bits = bits; s.Format = format; s.Culture = culture;
            return text;
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"patched\":" + Patched + ",\"formatted\":" + Formatted + ",\"reused\":" + Reused + "}";
    }
}

using System;
using HarmonyLib;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private static bool BusControl(tosaveitemscript it, usablescript u)
        {
            var bus = it.busdoorscript;
            if (bus == null || bus.doors == null) return false;
            foreach (var d in bus.doors)
                if (d != null && (Has(d.UBoth) || Has(d.Uopen) || Has(d.Uclose) || Has(d.Usign))) return true;
            return false;
            bool Has(usablescript[] controls) => controls != null && Array.IndexOf(controls, u) >= 0;
        }

        /// SendMulti is gated by vanilla Steam networking; observe the consumed buttons in RevaMP too.
        [HarmonyPatch(typeof(busdoorscripts), "Update")]
        private static class BusDoorChanged
        {
            [HarmonyPrefix]
            private static void Prefix(busdoorscripts __instance, out bool __state)
            { __state = InSession && !_applyingState && !__instance.noMultiSync && __instance.Electronics(); }

            [HarmonyPostfix]
            private static void Postfix(busdoorscripts __instance, bool __state)
            {
                if (__state && __instance.somethingChanged) MarkEdit(__instance.tosave, kind: "busdoor");
            }
        }

        public static string BusDoors(uint net, int item = -1, int index = -1)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"entity is not loaded\"}";
            var rows = new System.Collections.Generic.List<string>();
            for (int i = 0; i < e.Items.Count; i++)
            {
                var bus = e.Items[i] != null ? e.Items[i].busdoorscript : null;
                if (bus == null || bus.doors == null) continue;
                for (int j = 0; j < bus.doors.Length; j++)
                {
                    var d = bus.doors[j]; if (d == null) continue;
                    if (i == item && j == index)
                    {
                        if (!bus.Electronics()) return "{\"error\":\"bus door electronics are off\"}";
                        var controls = d.UBoth != null && d.UBoth.Length > 0 ? d.UBoth : d.Bopened ? d.Uclose : d.Uopen;
                        if (controls == null || controls.Length == 0 || controls[0] == null) return "{\"error\":\"no door button\"}";
                        controls[0].Turn();
                    }
                    float fraction = 0; int n = 0;
                    if (d.doors != null) foreach (var leaf in d.doors) if (leaf != null)
                    {
                        var span = leaf.opened - leaf.closed;
                        if (span.sqrMagnitude > .0001f) { fraction += UnityEngine.Mathf.Clamp01(UnityEngine.Vector3.Dot(leaf.current - leaf.closed, span) / span.sqrMagnitude); n++; }
                    }
                    rows.Add("{\"item\":" + i + ",\"index\":" + j + ",\"open\":" + (d.Bopened ? "true" : "false") + ",\"powered\":" + (bus.Electronics() ? "true" : "false") + ",\"fraction\":" + UN(n > 0 ? fraction / n : 0) + "}");
                }
            }
            return "{\"doors\":[" + string.Join(",", rows) + "]}";
        }
    }
}

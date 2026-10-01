using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp
{
    public partial class DebugBridge
    {
        /// `mp pois x z r`: pois near a spot — key, name, distance, spawned flags, whether its building object is
        /// loaded. x,z are UNITY coordinates (what callers have); they're converted to global here, because the
        /// floating origin shifts on every far hop and poi.pos is global — comparing them raw finds nothing.
        private static string Pois(double x, double z, double r)
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || map.poiGens == null) return "{\"error\":\"no map\"}";
            var g = mainscript.GlobalFromUnityPos(new Vector3((float)x, 0f, (float)z));
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            foreach (var gen in map.poiGens)
            {
                if (gen == null) continue;
                foreach (var ch in gen.chunks.Values)
                    foreach (var poi in ch.pois.Values)
                    {
                        var d = poi.pos - g; d.y = 0;
                        if (d.magnitude > r) continue;
                        string key = poi.posID.x.ToString("R", ci) + "," + poi.posID.z.ToString("R", ci);
                        rows.Add("{\"key\":" + Json.Str(key) + ",\"name\":" + Json.Str(poi.poiName) +
                                 ",\"d\":" + d.magnitude.ToString("F0", ci) +
                                 ",\"items\":" + (poi.spawnedItems ? "true" : "false") +
                                 ",\"fixed\":" + (poi.spawnedFixed ? "true" : "false") +
                                 ",\"loaded\":" + (poi.pobj != null ? "true" : "false") +
                                 ",\"ux\":" + mainscript.UnityPosFromGlobal(poi.pos).x.ToString("F1", ci) +
                                 ",\"uz\":" + mainscript.UnityPosFromGlobal(poi.pos).z.ToString("F1", ci) +
                                 ",\"gx\":" + poi.pos.x.ToString("F2", ci) +
                                 ",\"gz\":" + poi.pos.z.ToString("F2", ci) + "}");
                    }
            }
            rows.Sort((a, b) => ParseD(a).CompareTo(ParseD(b)));
            return "{\"pois\":[" + string.Join(",", rows) + "]}";
        }

        private static double ParseD(string row)
        {
            int i = row.IndexOf("\"d\":") + 4, j = row.IndexOf(',', i);
            double.TryParse(row.Substring(i, j - i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v);
            return v;
        }
    }
}

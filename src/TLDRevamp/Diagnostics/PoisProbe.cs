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
                        rows.Add("{\"key\":" + Json.Str(key) + ",\"name\":" + Json.Str(poi.poiName) + ",\"id\":" + poi.id +
                                 ",\"shown\":" + (poi.pobj != null && poi.pobj.gameObject.activeInHierarchy ? "true" : "false") +
                                 ",\"gy\":" + poi.pos.y.ToString("F2", ci) +
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

        /// `poiretype <key> <id>`: test (tools/leasetype.py) — this machine's building at that spot becomes another type
        /// before it is spawned here: what a different game build would generate there (the missing diner).
        private static string PoiRetype(string key, int id)
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || map.poiGens == null) return "{\"error\":\"no map\"}";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var gen in map.poiGens)
            {
                if (gen == null) continue;
                foreach (var ch in gen.chunks.Values)
                    foreach (var poi in ch.pois.Values)
                    {
                        if (poi.posID.x.ToString("R", ci) + "," + poi.posID.z.ToString("R", ci) != key) continue;
                        if (poi.spawnedItems || poi.spawnedFixed) return "{\"error\":\"its contents are already spawned here\"}";
                        bool wasLoaded = poi.pobj != null;
                        if (wasLoaded) { gen.RemovePoi(poi); poi.save = null; }   // the game builds it again (as the new type) when near
                        var pf = itemdatabase.s.Building(id);
                        string was = poi.poiName; int wasId = poi.id;
                        poi.id = id; poi.poiName = pf.name;
                        return "{\"key\":" + Json.Str(key) + ",\"was\":" + Json.Str(was) + ",\"wasId\":" + wasId + ",\"now\":" + Json.Str(pf.name) + ",\"id\":" + id + ",\"wasLoaded\":" + (wasLoaded ? "true" : "false") + "}";
                    }
            }
            return "{\"error\":\"no building at " + key + "\"}";
        }

        private static double ParseD(string row)
        {
            int i = row.IndexOf("\"d\":") + 4, j = row.IndexOf(',', i);
            double.TryParse(row.Substring(i, j - i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v);
            return v;
        }
    }
}

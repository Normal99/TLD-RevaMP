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
                                 ",\"aligned\":" + (poi.alignedToRoad ? "true" : "false") +
                                 ",\"ux\":" + mainscript.UnityPosFromGlobal(poi.pos).x.ToString("F1", ci) +
                                 ",\"uz\":" + mainscript.UnityPosFromGlobal(poi.pos).z.ToString("F1", ci) +
                                 ",\"gx\":" + poi.pos.x.ToString("F2", ci) +
                                 ",\"gz\":" + poi.pos.z.ToString("F2", ci) + "}");
                    }
            }
            rows.Sort((a, b) => ParseD(a).CompareTo(ParseD(b)));
            return "{\"pois\":[" + string.Join(",", rows) + "]}";
        }

        /// `poiwait gx gz r`: test (poicheck.py) — around a GLOBAL spot: the buildings still waiting to be placed (each
        /// generator's poiToPlace: candidate, type, road rule, whether RoadsDoneAtPlace lets it go now) and the road chunks
        /// there (exists, needed, roads, generated, Done). Two machines that disagree about a building show here why.
        private static string PoiWait(double gx, double gz, double r)
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || map.poiGens == null) return "{\"error\":\"no map\"}";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string N(double v) => v.ToString("0.##", ci);
            var waiting = new List<string>();
            foreach (var gen in map.poiGens)
            {
                if (gen == null || gen.chunks == null) continue;
                foreach (var kv in gen.chunks)
                {
                    var ch = kv.Value;
                    if (ch == null || ch.poiToPlace == null) continue;
                    foreach (var t in ch.poiToPlace)
                    {
                        double dx = t.pos.x - gx, dz = t.pos.z - gz;
                        if (dx * dx + dz * dz > r * r) continue;
                        bool done = !gen.roadAffects || gen.roadgen == null || gen.roadgen.RoadsDoneAtPlace(t.pos);
                        waiting.Add("{\"gx\":" + N(t.pos.x) + ",\"gz\":" + N(t.pos.z) + ",\"type\":" + t.tipus + ",\"road\":" + Json.Str(t.roadType.ToString()) +
                                    ",\"roadsDone\":" + (done ? "true" : "false") + ",\"chunkNeeded\":" + (gen.chunksNeeded.Contains(kv.Key) ? "true" : "false") + "}");
                    }
                }
            }
            var roads = new List<string>();
            var rg = map.poiGens.Count > 0 && map.poiGens[0] != null ? map.poiGens[0].roadgen : null;
            if (rg != null && rg.chunks != null)
                foreach (var kv in rg.chunks)
                {
                    double dx = kv.Key.x - gx, dz = kv.Key.z - gz;
                    if (dx * dx + dz * dz > (r + rg.chunkSize) * (r + rg.chunkSize)) continue;
                    var c = kv.Value; int gen = 0;
                    var list = new List<string>();
                    foreach (var rd in c.roads)
                    {
                        if (rd.generated) gen++;
                        list.Add("[" + N(rd.start.x) + "," + N(rd.start.z) + "," + N(rd.end.x) + "," + N(rd.end.z) + "," + Json.Str(rd.roadType.ToString()) + "]");
                    }
                    roads.Add("{\"x\":" + N(kv.Key.x) + ",\"z\":" + N(kv.Key.z) + ",\"needed\":" + (rg.chunksNeeded.Contains(kv.Key) ? "true" : "false") +
                              ",\"roads\":" + c.roads.Count + ",\"generated\":" + gen + ",\"done\":" + (rg.ChunkDone(kv.Key) ? "true" : "false") +
                              ",\"inner\":" + (c.innerChunkGenerated ? "true" : "false") + ",\"linkedUp\":" + (c.outsideUConnected ? "true" : "false") + ",\"list\":[" + string.Join(",", list) + "]}");
                }
            return "{\"waiting\":[" + string.Join(",", waiting) + "],\"roadChunkSize\":" + (rg != null ? N(rg.chunkSize) : "null") + ",\"roadChunks\":[" + string.Join(",", roads) + "]}";
        }

        /// `poiretype <key> <id>`: test (tools/leasetype.py) — this machine's building at that spot becomes another type
        /// before it is spawned here: what a different game build would generate there (the missing diner).
        /// Test (`poihide <key>`): this machine has no building at that spot — what a player who "doesn't see the
        /// building" has (Entities.PoiCensus, buildingdiff.py).
        private static string PoiHide(string key)
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || map.poiGens == null) return "{\"error\":\"no map\"}";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var gen in map.poiGens)
            {
                if (gen == null) continue;
                foreach (var ch in gen.chunks.Values)
                    foreach (var kv in ch.pois)
                    {
                        var poi = kv.Value;
                        if (poi.posID.x.ToString("R", ci) + "," + poi.posID.z.ToString("R", ci) != key) continue;
                        if (poi.pobj != null) gen.RemovePoi(poi);
                        ch.pois.Remove(kv.Key);
                        return "{\"hidden\":" + Json.Str(poi.poiName ?? "") + ",\"id\":" + poi.id + "}";
                    }
            }
            return "{\"error\":\"no building at " + key + "\"}";
        }

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

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace TLDRevamp
{
    /// World fingerprint for determinism tests (bridge `worldhash <x> <z> <radius>`, global coordinates): everything the
    /// world generators have produced within `radius` of (x, z), hashed per category in a canonical order (sorted by
    /// position), independent of generation order/timing:
    ///  - roads: every generated bone of roads with a bone inside the radius (+ road type)
    ///  - buildings (POIs): position, type id, rotation
    ///  - scenery (objGen, per category): position, prefab, rotation, scale — only chunks already generated
    /// Two machines/runs that generated the same area must print identical hashes (counts tell how much was covered).
    public static class WorldHash
    {
        public static string Hash(double x, double z, double radius, bool detail)
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null) return "{\"error\":\"no map\"}";
            var center = new Vector3d(x, 0, z);
            var sb = new StringBuilder("{");
            var parts = new List<string>();

            // roads
            var roadRows = new List<string>();
            if (map.roadGens != null)
                foreach (var g in map.roadGens)
                {
                    if (g == null) continue;
                    foreach (var ch in g.chunks.Values)
                        foreach (var r in ch.roads)
                        {
                            if (!r.generated) continue;
                            bool near = false;
                            foreach (var el in r.roadElements) { foreach (var b in el.bonePos) if (Near(b, center, radius)) { near = true; break; } if (near) break; }
                            if (!near) continue;
                            var rb = new StringBuilder();
                            rb.Append((int)r.roadType).Append('|');
                            foreach (var el in r.roadElements) { rb.Append(el.roadID).Append(':'); foreach (var b in el.bonePos) rb.Append(B(b.x)).Append(',').Append(B(b.y)).Append(',').Append(B(b.z)).Append(';'); }
                            roadRows.Add(rb.ToString());
                        }
                }
            parts.Add(Part("roads", roadRows, detail));

            // buildings
            var poiRows = new List<string>();
            if (map.poiGens != null)
                foreach (var g in map.poiGens)
                {
                    if (g == null) continue;
                    foreach (var ch in g.chunks.Values)
                        foreach (var p in ch.pois.Values)
                            if (Near(p.pos, center, radius))
                                poiRows.Add(B(p.pos.x) + "," + B(p.pos.y) + "," + B(p.pos.z) + "|" + p.id + "|" + BF(p.rot.x) + "," + BF(p.rot.y) + "," + BF(p.rot.z) + "|" + (p.alignedToRoad ? 1 : 0));
                }
            parts.Add(Part("pois", poiRows, detail));

            // scenery per category
            if (map.objGens != null)
                foreach (var og in map.objGens)
                {
                    if (og == null) continue;
                    foreach (var cat in og.categories)
                    {
                        var rows = new List<string>();
                        foreach (var ch in cat.generatedChunks.Values)
                        {
                            if (!ch.generated) continue;
                            foreach (var kv in ch.objs)
                            {
                                var o = kv.Value;
                                if (!Near(o.gpos, center, radius)) continue;
                                rows.Add(B(o.gpos.x) + "," + B(o.gpos.y) + "," + B(o.gpos.z) + "|" + o.prefabIndex + "|" + BF(o.rot.x) + "," + BF(o.rot.y) + "," + BF(o.rot.z) + "|" + BF(o.scale) + "|" + o.seed);
                            }
                        }
                        parts.Add(Part("obj:" + cat.name, rows, detail));
                    }
                }
            // terrain: vertex heights of every built tile whose position is within the radius, per terrain generator
            if (map.terrainGen != null)
                for (int ti = 0; ti < map.terrainGen.Count; ti++)
                {
                    var tg = map.terrainGen[ti];
                    if (tg == null || tg.terrains == null) continue;
                    var rows = new List<string>();
                    foreach (var kv in tg.terrains)
                    {
                        if (!Near(kv.Key, center, radius) || kv.Value == null || kv.Value.mf == null || kv.Value.mf.sharedMesh == null) continue;
                        ulong vh = 14695981039346656037UL;
                        foreach (var v in kv.Value.mf.sharedMesh.vertices) { vh ^= (uint)System.BitConverter.SingleToInt32Bits(v.y); vh *= 1099511628211UL; }
                        rows.Add(B(kv.Key.x) + "," + B(kv.Key.z) + "|" + vh.ToString("X16"));
                    }
                    parts.Add(Part("terrain:" + ti + ":" + tg.name, rows, detail));
                }
            sb.Append(string.Join(",", parts));
            return sb.Append('}').ToString();
        }

        /// Scenery tile caches (objGenScript.tiles: roads/buildings near each 100 m tile, computed once when the tile is
        /// first used and never refreshed) compared with what is near now. Missing entries = scenery there was (and will
        /// be, for the rest of the session) generated as if that road/building didn't exist. Bridge `stale [detail]`.
        public static string Stale(bool detail)
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || map.objGens == null) return "{\"error\":\"no map\"}";
            int tiles = 0, poiStale = 0, roadStale = 0;
            var ex = new List<string>();
            foreach (var og in map.objGens)
                foreach (var kv in og.tiles)
                {
                    tiles++;
                    double half = og.tileSize * 0.5;
                    var cached = kv.Value;
                    int miss = 0;
                    if (map.poiGens != null)
                        foreach (var g in map.poiGens)
                            foreach (var ch in g.chunks.Values)
                                foreach (var p in ch.pois.Values)
                                {
                                    double dx = System.Math.Max(0, System.Math.Abs(p.pos.x - kv.Key.x) - half), dz = System.Math.Max(0, System.Math.Abs(p.pos.z - kv.Key.z) - half);
                                    if (dx * dx + dz * dz < (double)p.maxAlignEffectRange * p.maxAlignEffectRange && !cached.poisNear.Contains(p)) miss++;
                                }
                    if (miss > 0) { poiStale++; if (ex.Count < 20) ex.Add("\"poi " + kv.Key.x.ToString("F0") + "," + kv.Key.z.ToString("F0") + " missing " + miss + "\""); }
                    var nowRoads = mapSettingScript.RoadsNear(kv.Key);
                    int rmiss = 0;
                    foreach (var r in nowRoads) if (!cached.nearRoads.Contains(r)) rmiss++;
                    if (rmiss > 0) { roadStale++; if (ex.Count < 20) ex.Add("\"road " + kv.Key.x.ToString("F0") + "," + kv.Key.z.ToString("F0") + " missing " + rmiss + "\""); }
                }
            return "{\"tiles\":" + tiles + ",\"poiStale\":" + poiStale + ",\"roadStale\":" + roadStale + (detail ? ",\"examples\":[" + string.Join(",", ex) + "]" : "") + "}";
        }

        /// What the scenery height at (x, z) is computed from: the cached tile's road list in order, each road's nearest
        /// point and distance (ties = order decides). Bridge `heightdbg <x> <z>`.
        public static string HeightDbg(double x, double z)
        {
            var map = menuhandler.s.currentMainMap;
            var g = new Vector3d(x, 0, z);
            var og = map.objGens[0];
            var tile = mainscript.GetChunkPos(g, og.tileSize);
            if (!og.tiles.TryGetValue(tile, out var tc)) return "{\"error\":\"no tile\"}";
            var rows = new List<string>();
            foreach (var r in tc.nearRoads)
            {
                string id = "?";
                foreach (var rg in map.roadGens) foreach (var ck in rg.chunks) { int ix = ck.Value.roads.IndexOf(r); if (ix >= 0) id = ck.Key.x.ToString("F0") + "," + ck.Key.z.ToString("F0") + "#" + ix; }
                var np = roadGenScript.NearestPointOnRoadBone(g, r);
                var h = np; h.y = 0;
                rows.Add(Json.Str(id + " d=" + (h - g).magnitude.ToString("R") + " y=" + B(np.y) + " type=" + r.roadType + " align=" + r.terrainAlignMinDist + "/" + r.terrainAlignMaxDist));
            }
            return "{\"tile\":\"" + tile.x + "," + tile.z + "\",\"pois\":" + tc.poisNear.Count + ",\"roads\":[" + string.Join(",", rows) + "]}";
        }

        /// Generator parameters that decide what depends on what (bridge `gensettings`).
        public static string GenSettings()
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null) return "{\"error\":\"no map\"}";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder("{");
            if (map.roadGens != null)
                foreach (var g in map.roadGens)
                    sb.Append("\"road\":{\"chunk\":").Append(g.chunkSize.ToString(ic)).Append(",\"inner\":").Append(g.innerChunkSize.ToString(ic))
                      .Append(",\"iaround\":").Append(HarmonyLib.Traverse.Create(g).Field("iaround").GetValue())
                      .Append(",\"baround\":").Append(HarmonyLib.Traverse.Create(g).Field("baround").GetValue()).Append("},");
            if (map.poiGens != null)
                foreach (var g in map.poiGens)
                {
                    float maxR = 0f;
                    foreach (var ch in g.chunks.Values) foreach (var p in ch.pois.Values) maxR = Mathf.Max(maxR, p.maxAlignEffectRange);
                    sb.Append("\"poi\":{\"chunk\":").Append(g.chunkSize.ToString(ic)).Append(",\"roadAffects\":").Append(g.roadAffects ? "true" : "false")
                      .Append(",\"maxAlignEffectRangeSeen\":").Append(maxR.ToString(ic)).Append("},");
                }
            if (map.objGens != null)
                foreach (var og in map.objGens)
                {
                    sb.Append("\"obj\":{\"tileSize\":").Append(og.tileSize.ToString(ic)).Append(",\"tiles\":").Append(og.tiles.Count).Append(",\"cats\":[");
                    foreach (var c in og.categories)
                        sb.Append("{\"name\":").Append(Json.Str(c.name)).Append(",\"useTile\":").Append(c.useTile ? 1 : 0)
                          .Append(",\"terrainH\":").Append(c.useTerrainHeight ? 1 : 0).Append(",\"insidePOI\":").Append(c.useRemoveInsidePOI ? 1 : 0)
                          .Append(",\"minRoad\":").Append(c.minDistFromRoad.ToString(ic)).Append(",\"maxRoad\":").Append(c.maxDistFromRoad.ToString(ic))
                          .Append(",\"minPOI\":").Append(c.minDistFromPOI.ToString(ic)).Append(",\"grid\":").Append(c.gridSize.ToString(ic))
                          .Append(",\"chunk\":").Append(HarmonyLib.Traverse.Create(c).Field("currentChunkSize").GetValue<float>().ToString(ic))
                          .Append(",\"chunks\":").Append(c.generatedChunks.Count).Append("},");
                    sb.Length--; sb.Append("]},");
                }
            sb.Length--;
            return sb.Append('}').ToString();
        }

        private static bool Near(Vector3d p, Vector3d c, double r) { double dx = p.x - c.x, dz = p.z - c.z; return dx * dx + dz * dz <= r * r; }
        private static string B(double v) => System.BitConverter.DoubleToInt64Bits(v).ToString("X");
        private static string BF(float v) => System.BitConverter.SingleToInt32Bits(v).ToString("X");

        private static string Part(string name, List<string> rows, bool detail)
        {
            rows.Sort(System.StringComparer.Ordinal);
            ulong h = 14695981039346656037UL;
            foreach (var r in rows) { foreach (char c in r) { h ^= c; h *= 1099511628211UL; } h ^= 0xFF; h *= 1099511628211UL; }
            var s = Json.Str(name) + ":{\"n\":" + rows.Count + ",\"h\":\"" + h.ToString("X16") + "\"";
            if (detail) s += ",\"rows\":[" + string.Join(",", rows.Select(Json.Str)) + "]";
            return s + "}";
        }
    }
}

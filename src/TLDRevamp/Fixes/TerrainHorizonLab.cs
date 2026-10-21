using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Read-only horizon diagnostics for issue #37. The near terrain and distant terrain are separate generators; bridge
    /// `terrainhorizon [globalX globalZ]` reports what each has produced around a point without changing either one.
    /// `terrainhorizonstats` adds this fix's own counters (batches, vertices, rebuild cost, road signature).
    public static class TerrainHorizonLab
    {
        internal static string Snapshot(string arg)
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null) return "{\"error\":\"no world\"}";

            var ci = CultureInfo.InvariantCulture;
            double gx = double.NaN, gz = double.NaN;
            var fields = (arg ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && (!double.TryParse(fields[0], NumberStyles.Float, ci, out gx) ||
                                      !double.TryParse(fields[1], NumberStyles.Float, ci, out gz)))
                return "{\"error\":\"terrainhorizon [globalX globalZ]\"}";
            if (fields.Length < 2 && mainscript.s != null && mainscript.s.player != null)
            {
                var p = mainscript.GlobalFromUnityPos(mainscript.s.player.transform.position);
                gx = p.x; gz = p.z;
            }

            var sb = new StringBuilder();
            sb.Append("{\"x\":").Append(Json(gx)).Append(",\"z\":").Append(Json(gz));
            sb.Append(",\"setting\":{\"drawDistance\":").Append(Json(settingsscript.s != null ? settingsscript.s.S.FDrawDistance : 0f))
              .Append(",\"distant\":").Append(settingsscript.s != null && settingsscript.s.S.GetDistantTerrain() ? "true" : "false").Append('}');

            var near = new List<terrainGenScript>();
            var distant = new List<terrainGenScript>();
            if (map.terrainGen != null)
                foreach (var gen in map.terrainGen)
                    if (gen != null) (gen.isDistantTerrain ? distant : near).Add(gen);

            sb.Append(",\"near\":"); Terrains(sb, near, gx, gz);
            sb.Append(",\"distantTerrain\":"); Terrains(sb, distant, gx, gz);

            var far = UnityEngine.Object.FindObjectOfType<newFarTerrainScript>();
            sb.Append(",\"farGenerator\":");
            if (far == null) sb.Append("null");
            else
            {
                sb.Append("{\"farViewDistance\":").Append(Json(far.farViewDistance)).Append(",\"resolution\":").Append(far.resolution)
                  .Append(",\"fromPlayerDist\":").Append(Json(far.fromPlayerDist)).Append(",\"nearDownPush\":").Append(Json(far.nearDownPush))
                  .Append(",\"meshes\":").Append(Count(far.mf, x => x != null && x.mesh != null))
                  .Append(",\"vertices\":").Append(Sum(far.mf, x => x != null && x.mesh != null ? x.mesh.vertexCount : 0))
                  .Append(",\"triangles\":").Append(Sum(far.mf, x => x != null && x.mesh != null ? x.mesh.triangles.Length / 3 : 0)).Append('}');
            }

            var cameras = new List<string>();
            if (mainscript.s != null && mainscript.s.player != null)
            {
                AddCamera(cameras, "near", mainscript.s.player.Cam);
                AddCamera(cameras, "nearMain", mainscript.s.player.mainCam);
                AddCamera(cameras, "far", mainscript.s.player.farCamera);
                AddCamera(cameras, "sky", mainscript.s.player.skyCamera);
            }
            sb.Append(",\"cameras\":[").Append(string.Join(",", cameras)).Append(']');

            // The road generators: how far the game itself places road pieces, and the terrain
            // alignment distances the game uses to flatten the ground under a road. The far-mesh
            // correction uses the same numbers, so the horizon follows the game's own road.
            var roadGens = new List<string>();
            if (map.roadGens != null)
                foreach (var rg in map.roadGens)
                {
                    if (rg == null) continue;
                    float minDist = 0f, maxDist = 0f; int seen = 0;
                    foreach (var kv in rg.chunks)
                    {
                        if (kv.Value == null || kv.Value.roads == null) continue;
                        foreach (var r in kv.Value.roads)
                        {
                            if (r == null) continue;
                            if (seen == 0) { minDist = r.terrainAlignMinDist; maxDist = r.terrainAlignMaxDist; }
                            else { minDist = Math.Min(minDist, r.terrainAlignMinDist); maxDist = Math.Max(maxDist, r.terrainAlignMaxDist); }
                            seen++;
                        }
                    }
                    roadGens.Add("{\"chunkSize\":" + Json(rg.chunkSize) + ",\"roadPlaceDist\":" + Json(rg.roadPlaceDist) +
                                 ",\"chunks\":" + rg.chunks.Count + ",\"roads\":" + seen +
                                 ",\"alignMinDist\":" + Json(minDist) + ",\"alignMaxDist\":" + Json(maxDist) + "}");
                }
            sb.Append(",\"roadGenerators\":[").Append(string.Join(",", roadGens)).Append(']');

            var roads = new List<string>();
            double closest = double.MaxValue;
            int generated = 0, placed = 0, chunks = 0, roadCount = 0;
            if (map.roadGens != null)
            {
                foreach (var gen in map.roadGens)
                {
                    if (gen == null || gen.chunks == null) continue;
                    chunks += gen.chunks.Count;
                    foreach (var kv in gen.chunks)
                    {
                        if (kv.Value == null || kv.Value.roads == null) continue;
                        foreach (var road in kv.Value.roads)
                        {
                            if (road == null) continue;
                            roadCount++;
                            if (road.generated) generated++;
                            int objects = 0;
                            foreach (var element in road.roadElements)
                                if (element != null && element.obj != null) objects++;
                            placed += objects;
                            double d = double.MaxValue;
                            if (road.generated && road.roadElements != null)
                                foreach (var element in road.roadElements)
                                    if (element != null && element.bonePos != null)
                                        foreach (var bone in element.bonePos)
                                        {
                                            double dx = bone.x - gx, dz = bone.z - gz;
                                            d = Math.Min(d, dx * dx + dz * dz);
                                        }
                            if (d < closest) closest = d;
                            if (roads.Count < 12 && d < 1000.0 * 1000.0)
                                roads.Add("{\"type\":" + TLDRevamp.Json.Str(road.roadType.ToString()) + ",\"generated\":" + (road.generated ? "true" : "false") +
                                          ",\"elements\":" + (road.roadElements != null ? road.roadElements.Count : 0) + ",\"placed\":" + objects +
                                          ",\"distanceM\":" + (d == double.MaxValue ? "null" : Json(Math.Sqrt(d))) + "}");
                        }
                    }
                }
            }
            sb.Append(",\"roads\":{\"chunks\":").Append(chunks).Append(",\"count\":").Append(roadCount).Append(",\"generated\":").Append(generated)
              .Append(",\"placedElements\":").Append(placed).Append(",\"closestM\":").Append(closest == double.MaxValue ? "null" : Json(Math.Sqrt(closest)))
              .Append(",\"near\":[").Append(string.Join(",", roads)).Append("]}");
            return sb.Append('}').ToString();
        }

        /// This fix's own counters: what it drew, what it cost and how often it ran.
        internal static string Stats() => TerrainHorizon.Stats();

        private static void Terrains(StringBuilder sb, List<terrainGenScript> list, double gx, double gz)
        {
            sb.Append('[');
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var g = list[i];
                double closest = double.MaxValue;
                int expected = 0, present = 0, pending = g.toGen != null ? g.toGen.Count : 0;
                if (g.terrains != null)
                {
                    present = g.terrains.Count;
                    foreach (var kv in g.terrains)
                    {
                        if (kv.Value == null) continue;
                        double dx = kv.Key.x - gx, dz = kv.Key.z - gz;
                        closest = Math.Min(closest, dx * dx + dz * dz);
                    }
                }
                if (g.coordsNeeded != null) expected = g.coordsNeeded.Count;
                sb.Append("{\"name\":").Append(TLDRevamp.Json.Str(g.terrainName)).Append(",\"enabled\":").Append(g.isActiveAndEnabled ? "true" : "false")
                  .Append(",\"drawDistance\":").Append(Json(g.drawDistance)).Append(",\"tileSize\":").Append(Json(g.tileSize))
                  .Append(",\"resolution\":").Append(g.resolution).Append(",\"needed\":").Append(expected).Append(",\"present\":").Append(present)
                  .Append(",\"pending\":").Append(pending).Append(",\"removeQueue\":").Append(g.coordsToRemove != null ? g.coordsToRemove.Count : 0)
                  .Append(",\"closestTileM\":").Append(closest == double.MaxValue ? "null" : Json(Math.Sqrt(closest)))
                  .Append(",\"collider\":").Append(g.hasCollider ? "true" : "false")
                  .Append(",\"roadAffects\":").Append(g.roadAffectsThis ? "true" : "false").Append('}');
            }
            sb.Append(']');
        }

        private static void AddCamera(List<string> rows, string role, Camera c)
        {
            if (c == null) return;
            rows.Add("{\"role\":" + TLDRevamp.Json.Str(role) + ",\"name\":" + TLDRevamp.Json.Str(c.name) + ",\"enabled\":" + (c.enabled && c.gameObject.activeInHierarchy ? "true" : "false") +
                     ",\"near\":" + Json(c.nearClipPlane) + ",\"far\":" + Json(c.farClipPlane) + ",\"depth\":" + Json(c.depth) +
                     ",\"layers\":" + c.cullingMask + "}");
        }

        private static int Count<T>(T[] values, Func<T, bool> ok)
        {
            if (values == null) return 0;
            int n = 0; foreach (var v in values) if (ok(v)) n++; return n;
        }

        private static long Sum<T>(T[] values, Func<T, int> value)
        {
            if (values == null) return 0;
            long n = 0; foreach (var v in values) n += value(v); return n;
        }

        private static string Json(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        private static string Json(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}

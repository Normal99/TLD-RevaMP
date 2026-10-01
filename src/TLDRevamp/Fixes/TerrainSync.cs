using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Terrain counterpart of SceneryTileSync. terrainGenScript.GenerateTileMesh takes RoadsNear/POIsNear once, when the
    /// tile is built, and tiles are only rebuilt after leaving the area. Nothing rebuilds a tile when a building or road
    /// appears next to it later, so a building placed after its ground was built stands on unflattened ground (without
    /// its foundation hole) until the player leaves and comes back — and only on the machines where it happened in that
    /// order. The game's gate (DoPlace waits for RoadsDoneAtPlace) checks the tile's own road chunk and the neighbours
    /// towards it, but RoadsNear reads all 9 surrounding chunks, and buildings wait for roads at *their* spot, not the tile's.
    ///
    /// Fix: every placed tile remembers the road/building lists it was built from. On a road/building event
    /// (SceneryTileSync) tiles whose lists can have changed are compared with the current lists; changed tiles are rebuilt
    /// in place (new mesh swapped into the existing tile object, so there's never a hole), one per frame per generator.
    /// AsyncTerrain jobs that finish with outdated lists are dropped and requested again.
    /// Runtime switch: TLDRevamp.Fixes.TerrainSync.Enabled.
    [HarmonyPatch]
    public static class TerrainSync
    {
        public static bool Enabled = true;
        public static long Checked, Queued, Rebuilt, JobsRedone, Recorded;

        private sealed class Rec { public List<roadGenScript.roadClass> Roads; public List<poiGenScript.poiClass> Pois; }
        private static readonly ConditionalWeakTable<newTerrainTileScript, Rec> Recs = new ConditionalWeakTable<newTerrainTileScript, Rec>();
        private sealed class Q { public readonly List<Vector3d> Keys = new List<Vector3d>(); }
        private static readonly ConditionalWeakTable<terrainGenScript, Q> Queues = new ConditionalWeakTable<terrainGenScript, Q>();

        // Lists of the tile about to be placed (set by GenerateTileMesh or by AsyncTerrain before PlaceMesh)
        private static terrainGenScript _nextGen;
        private static Vector3d _nextKey;
        private static List<roadGenScript.roadClass> _nextRoads;
        private static List<poiGenScript.poiClass> _nextPois;

        private static readonly Func<terrainGenScript, Vector3d, terrainGenScript.smesh> GenerateTileMesh =
            AccessTools.MethodDelegate<Func<terrainGenScript, Vector3d, terrainGenScript.smesh>>(AccessTools.Method(typeof(terrainGenScript), "GenerateTileMesh"));
        private static readonly Func<terrainGenScript, terrainGenScript.smesh, Mesh> MeshFromSmesh =
            AccessTools.MethodDelegate<Func<terrainGenScript, terrainGenScript.smesh, Mesh>>(AccessTools.Method(typeof(terrainGenScript), "MeshFromSmesh"));

        internal static List<roadGenScript.roadClass> RoadsFor(terrainGenScript gen, Vector3d key) => gen.roadAffectsThis ? mapSettingScript.RoadsNear(key) : null;
        internal static List<poiGenScript.poiClass> PoisFor(terrainGenScript gen, Vector3d key) => gen.poiAffectsThis ? mapSettingScript.POIsNear(key, gen.tileSize) : null;

        /// The original computes its lists inside GenerateTileMesh; the same calls in the same frame give the same lists.
        [HarmonyPatch(typeof(terrainGenScript), "GenerateTileMesh")]
        [HarmonyPrefix]
        private static void GeneratePrefix(terrainGenScript __instance, Vector3d _gpos)
        {
            if (!Enabled) return;
            Next(__instance, _gpos, RoadsFor(__instance, _gpos), PoisFor(__instance, _gpos));
        }

        internal static void Next(terrainGenScript gen, Vector3d key, List<roadGenScript.roadClass> roads, List<poiGenScript.poiClass> pois)
        {
            _nextGen = gen; _nextKey = key; _nextRoads = roads; _nextPois = pois;
        }

        [HarmonyPatch(typeof(terrainGenScript), "PlaceMesh")]
        [HarmonyPostfix]
        private static void PlacePostfix(terrainGenScript __instance, Vector3d _gpos)
        {
            if (!Enabled || _nextGen != __instance || !_nextKey.Equals(_gpos)) return;
            if (__instance.terrains.TryGetValue(_gpos, out var tile) && tile != null)
            {
                Recs.Remove(tile);
                Recs.Add(tile, new Rec { Roads = _nextRoads, Pois = _nextPois });
                Recorded++;
            }
            _nextGen = null;
        }

        /// AsyncTerrain: a job computed with lists that have changed since dispatch must be computed again.
        internal static bool Outdated(terrainGenScript gen, Vector3d key, List<roadGenScript.roadClass> roads, List<poiGenScript.poiClass> pois)
        {
            if (!Enabled) return false;
            bool stale = !Same(roads, RoadsFor(gen, key)) || !Same(pois, PoisFor(gen, key));
            if (stale) JobsRedone++;
            return stale;
        }

        private static bool Same<T>(List<T> a, List<T> b) where T : class
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }

        /// Called by SceneryTileSync's once-per-frame flush with this frame's events.
        internal static void Apply(mapSettingScript map, HashSet<Vector3d> roadInner, double innerSize, int around, List<poiGenScript.poiClass> newPois)
        {
            if (!Enabled || map.terrainGen == null) return;
            foreach (var gen in map.terrainGen)
            {
                if (gen == null || gen.terrains == null || !(gen.roadAffectsThis || gen.poiAffectsThis)) continue;
                Q q = null;
                foreach (var kv in gen.terrains)
                {
                    var key = kv.Key;
                    bool maybe = false;
                    if (gen.poiAffectsThis)
                        foreach (var p in newPois)
                        {
                            double dx = p.pos.x - key.x, dz = p.pos.z - key.z, r = p.maxAlignEffectRange + gen.tileSize;
                            if (dx * dx + dz * dz < r * r) { maybe = true; break; }
                        }
                    if (!maybe && gen.roadAffectsThis && roadInner.Count > 0)
                    {
                        var ki = mainscript.GetChunkPos(key, (float)innerSize);
                        for (int i = -around; i <= around && !maybe; i++)
                            for (int j = -around; j <= around && !maybe; j++)
                                if (roadInner.Contains(mainscript.GetChunkPos(ki, j, i, (float)innerSize))) maybe = true;
                    }
                    if (!maybe || kv.Value == null || !Recs.TryGetValue(kv.Value, out var rec)) continue;
                    Checked++;
                    if (Same(rec.Roads, RoadsFor(gen, key)) && Same(rec.Pois, PoisFor(gen, key))) continue;
                    if (q == null) q = Queues.GetValue(gen, _ => new Q());
                    if (!q.Keys.Contains(key)) { q.Keys.Add(key); Queued++; }
                }
            }
        }

        /// One queued tile per generator per frame: new mesh into the existing tile object (collider included).
        [HarmonyPatch(typeof(terrainGenScript), nameof(terrainGenScript.Upd))]
        [HarmonyPostfix]
        private static void UpdPostfix(terrainGenScript __instance)
        {
            if (!Enabled || !Queues.TryGetValue(__instance, out var q) || q.Keys.Count == 0) return;
            try
            {
                var gen = __instance;
                var key = q.Keys[0];
                q.Keys.RemoveAt(0);
                if (!gen.terrains.TryGetValue(key, out var tile) || tile == null) return;
                var roads = RoadsFor(gen, key);
                var pois = PoisFor(gen, key);
                if (Recs.TryGetValue(tile, out var rec) && Same(rec.Roads, roads) && Same(rec.Pois, pois)) return;
                var mesh = MeshFromSmesh(gen, GenerateTileMesh(gen, key));
                var old = tile.mf != null ? tile.mf.sharedMesh : null;
                tile.UpdMesh(mesh);
                if (gen.destroyMesh && old != null && old != mesh) UnityEngine.Object.Destroy(old);
                Recs.Remove(tile);
                Recs.Add(tile, new Rec { Roads = roads, Pois = pois });
                _nextGen = null;
                Rebuilt++;
            }
            catch (Exception e)
            {
                Enabled = false;
                Plugin.Log.LogError("TerrainSync disabled after error: " + e);
            }
        }

        /// Every placed tile: built from lists that are still current? (bridge `get TLDRevamp.Fixes.TerrainSync.Audit`)
        public static string Audit
        {
            get
            {
                var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
                if (map == null || map.terrainGen == null) return "{}";
                var parts = new List<string>();
                foreach (var gen in map.terrainGen)
                {
                    if (gen == null || gen.terrains == null) continue;
                    int tiles = 0, noRec = 0, stale = 0;
                    foreach (var kv in gen.terrains)
                    {
                        tiles++;
                        if (kv.Value == null || !Recs.TryGetValue(kv.Value, out var rec)) { noRec++; continue; }
                        if (!Same(rec.Roads, RoadsFor(gen, kv.Key)) || !Same(rec.Pois, PoisFor(gen, kv.Key))) stale++;
                    }
                    parts.Add("\"" + gen.name + "\":{\"tiles\":" + tiles + ",\"noRec\":" + noRec + ",\"stale\":" + stale + "}");
                }
                return "{" + string.Join(",", parts) + "}";
            }
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"checked\":" + Checked + ",\"queued\":" + Queued +
            ",\"rebuilt\":" + Rebuilt + ",\"jobsRedone\":" + JobsRedone + ",\"recorded\":" + Recorded + "}";
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Scenery (trees, rocks, grass, cacti) must come out the same no matter in which order the world around it was
    /// generated — two multiplayer clients, or one player before and after a reload, reach places by different routes.
    /// Found with tools/determinism.py (same place reached directly / from 3 km north / from 3 km south): one tree next to
    /// a building existed only when arriving directly; dozens of objects near another building had different heights.
    ///
    /// Game bugs (objGenScript.categoryClass.UpdGen):
    ///  1. Each 100 m tile caches "roads near" and "buildings near" (objGenScript.tiles) the first time any object in it is
    ///     generated, and never updates them. A building is only placed once the roads at its spot are finished
    ///     (poiGenScript.AddPois → RoadsDoneAtPlace), and roads are generated a few per frame — so whether a tile knows
    ///     about a building/road depends on timing. Scenery generated from a stale tile ignores that building/road for the
    ///     whole session: objects inside a building's no-object area, on a road, or at the unflattened height.
    ///     Uncached categories (BigRocks) have the same race without the cache.
    ///  2. The cached lists are computed around the first object that hit the tile, not the tile itself.
    ///  3. Even buildings/roads "out of range" change heights: the flattening curve converts degrees with 0.01745329
    ///     (truncated π/180), so sin(−90°) isn't exactly −1 and "no effect" is a lerp by ~5e-14. Harmless, but it makes
    ///     presence in the list visible in the last bits.
    ///  4. The lists' order follows generation order; the nearest-road / building-flattening choice keeps the first on an
    ///     exact tie (roads meeting at a junction).
    ///
    /// Fix (the final world depends only on the final roads/buildings, like it would with everything generated upfront):
    ///  - tile lists are computed at the tile's own position (every object a building/road can affect is still inside:
    ///    tile half-diagonal 71 m < the 100 m margin the game adds), and RoadsNear/POIsNear results are put in a
    ///    canonical order;
    ///  - every change of those inputs is an event: a road registering in an inner chunk (roadGenScript.UpdInnerChunk,
    ///    main thread also with ParallelRoadGen) or a new building (poiGenScript.AddPoi). Affected tile lists are
    ///    recomputed and already generated scenery chunks in the affected area are regenerated (ObjGenFast.Regenerate):
    ///    unchanged objects keep their GameObject, changed heights are moved, objects that are no longer allowed are removed.
    /// In normal driving roads and buildings are generated kilometres ahead, so events rarely touch generated scenery;
    /// they matter after teleports, loads and at arrival.
    ///
    /// Requires ObjGenFast (the mod's scenery generation path). Runtime switch: TLDRevamp.Fixes.SceneryTileSync.Enabled.
    public static class SceneryTileSync
    {
        public static bool Enabled = true;
        public static long RoadEvents, PoiEvents, TilesRecomputed, TilesChanged, ChunksQueued, ChunksRegenerated, ObjsRemoved, ObjsMoved, ObjsAdded;

        private struct RoadEv { public Vector3d Inner; public double Size; public int Around; }
        private static readonly List<RoadEv> RoadEvs = new List<RoadEv>();
        private static readonly HashSet<Vector3d> RoadEvSeen = new HashSet<Vector3d>();
        private static readonly List<poiGenScript.poiClass> NewPois = new List<poiGenScript.poiClass>();
        private static readonly ConditionalWeakTable<poiGenScript.poiClass, object> KnownPois = new ConditionalWeakTable<poiGenScript.poiClass, object>();
        private static readonly AccessTools.FieldRef<roadGenScript, int> IAround = AccessTools.FieldRefAccess<roadGenScript, int>("iaround");

        private sealed class Pending { public readonly List<Vector3d> Keys = new List<Vector3d>(); }
        private static readonly ConditionalWeakTable<objGenScript.categoryClass, Pending> Queue = new ConditionalWeakTable<objGenScript.categoryClass, Pending>();

        private static bool Active => Enabled && ObjGenFast.Enabled && !ParallelObjGen.Enabled;
        private static bool Collect => Active || TerrainSync.Enabled;

        // ------------------------------------------------------------------ lists

        /// The roads/buildings a scenery cell at `gpos` is generated against (the game's tile cache, filled canonically).
        internal static void Lists(objGenScript script, bool useTile, Vector3d gpos, out List<roadGenScript.roadClass> roads, out List<poiGenScript.poiClass> pois)
        {
            if (!useTile)
            {
                roads = mapSettingScript.RoadsNear(gpos);
                pois = mapSettingScript.POIsNear(gpos, script.tileSize);
                return;
            }
            Vector3d tile = mainscript.GetChunkPos(gpos, script.tileSize);
            if (script.tiles.TryGetValue(tile, out var tc))
            {
                roads = tc.nearRoads;
                pois = tc.poisNear;
                return;
            }
            Vector3d at = Active ? tile : gpos;
            tc = new objGenScript.tileClass { nearRoads = mapSettingScript.RoadsNear(at), poisNear = mapSettingScript.POIsNear(at, script.tileSize) };
            script.tiles.Add(tile, tc);
            roads = tc.nearRoads;
            pois = tc.poisNear;
        }

        private static int CompareRoads(roadGenScript.roadClass a, roadGenScript.roadClass b)
        {
            if (ReferenceEquals(a, b)) return 0;
            int c = a.start.x.CompareTo(b.start.x); if (c != 0) return c;
            c = a.start.z.CompareTo(b.start.z); if (c != 0) return c;
            c = a.end.x.CompareTo(b.end.x); if (c != 0) return c;
            c = a.end.z.CompareTo(b.end.z); if (c != 0) return c;
            c = ((int)a.roadType).CompareTo((int)b.roadType); if (c != 0) return c;
            return a.selfID.CompareTo(b.selfID);
        }

        private static int ComparePois(poiGenScript.poiClass a, poiGenScript.poiClass b)
        {
            if (ReferenceEquals(a, b)) return 0;
            int c = a.pos.x.CompareTo(b.pos.x); if (c != 0) return c;
            c = a.pos.z.CompareTo(b.pos.z); if (c != 0) return c;
            return a.id.CompareTo(b.id);
        }

        [HarmonyPatch(typeof(mapSettingScript), nameof(mapSettingScript.RoadsNear))]
        private static class RoadsNearOrder
        {
            [HarmonyPostfix]
            private static void Postfix(List<roadGenScript.roadClass> __result)
            {
                if (Active && __result != null && __result.Count > 1) __result.Sort(CompareRoads);
            }
        }

        [HarmonyPatch(typeof(mapSettingScript), nameof(mapSettingScript.POIsNear))]
        private static class PoisNearOrder
        {
            [HarmonyPostfix]
            private static void Postfix(List<poiGenScript.poiClass> __result)
            {
                if (Active && __result != null && __result.Count > 1) __result.Sort(ComparePois);
            }
        }

        // ------------------------------------------------------------------ events

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.UpdInnerChunk))]
        private static class RoadEvent
        {
            [HarmonyPostfix]
            private static void Postfix(roadGenScript __instance, Vector3d _currentPos)
            {
                if (!Collect) return;
                var inner = mainscript.GetChunkPos(_currentPos, __instance.innerChunkSize);
                if (!RoadEvSeen.Add(inner)) return;
                RoadEvs.Add(new RoadEv { Inner = inner, Size = __instance.innerChunkSize, Around = IAround(__instance) });
                RoadEvents++;
            }
        }

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPoi), new[] { typeof(Vector3d), typeof(Vector3), typeof(int), typeof(bool) })]
        private static class PoiEvent
        {
            [HarmonyPostfix]
            private static void Postfix(poiGenScript.poiClass __result)
            {
                if (!Collect || __result == null || KnownPois.TryGetValue(__result, out _)) return;
                KnownPois.Add(__result, null);
                NewPois.Add(__result);
                PoiEvents++;
            }
        }

        /// Once per frame, before scenery generation (roads and buildings of this frame are done by then).
        [HarmonyPatch(typeof(objGenScript), nameof(objGenScript.Upd))]
        private static class Flush
        {
            [HarmonyPrefix]
            private static void Prefix(objGenScript __instance)
            {
                if (RoadEvs.Count == 0 && NewPois.Count == 0) return;
                try
                {
                    var map = __instance.mapSetting;
                    if (Active && map != null && map.objGens != null)
                        foreach (var og in map.objGens)
                            if (og != null) Apply(og);
                    if (TerrainSync.Enabled && map != null)
                        TerrainSync.Apply(map, RoadEvSeen, RoadEvs.Count > 0 ? RoadEvs[0].Size : 100.0, RoadEvs.Count > 0 ? RoadEvs[0].Around : 1, NewPois);
                }
                catch (Exception e) { Plugin.Log.LogError("SceneryTileSync: " + e); }
                RoadEvs.Clear();
                RoadEvSeen.Clear();
                NewPois.Clear();
            }
        }

        private struct Rect
        {
            public double X0, Z0, X1, Z1;
            public Rect(double x0, double z0, double x1, double z1) { X0 = x0; Z0 = z0; X1 = x1; Z1 = z1; }
            public bool Hits(Rect o) => X0 <= o.X1 && o.X0 <= X1 && Z0 <= o.Z1 && o.Z0 <= Z1;
        }

        private static readonly List<Rect> Changed = new List<Rect>();   // changed tiles (tile categories)
        private static readonly List<Rect> Regions = new List<Rect>();   // event areas (uncached categories)
        private static readonly HashSet<Vector3d> Recomputed = new HashSet<Vector3d>();

        private static void Apply(objGenScript og)
        {
            double t = og.tileSize, half = t * 0.5 + 1e-6;
            Changed.Clear(); Regions.Clear(); Recomputed.Clear();

            // 1. tile lists that can differ now
            foreach (var p in NewPois)
            {
                double r = p.maxAlignEffectRange + t;   // POIsNear(x, tileSize) includes p when |x − p| < r
                Regions.Add(new Rect(p.pos.x - r, p.pos.z - r, p.pos.x + r, p.pos.z + r));
                for (double x = p.pos.x - r - t; x <= p.pos.x + r + t; x += t)
                    for (double z = p.pos.z - r - t; z <= p.pos.z + r + t; z += t)
                    {
                        var key = mainscript.GetChunkPos(new Vector3d(x, 0, z), (float)t);
                        double dx = key.x - p.pos.x, dz = key.z - p.pos.z;
                        if (dx * dx + dz * dz < r * r) Recompute(og, key, half);
                    }
            }
            foreach (var e in RoadEvs)
            {
                // RoadsNear(x) sees this inner chunk when x's inner chunk is within ±iaround of it
                double reach = (e.Around + 0.5) * e.Size + 1e-6;
                Regions.Add(new Rect(e.Inner.x - reach, e.Inner.z - reach, e.Inner.x + reach, e.Inner.z + reach));
                for (double x = e.Inner.x - reach - t; x <= e.Inner.x + reach + t; x += t)
                    for (double z = e.Inner.z - reach - t; z <= e.Inner.z + reach + t; z += t)
                    {
                        var key = mainscript.GetChunkPos(new Vector3d(x, 0, z), (float)t);
                        var ki = mainscript.GetChunkPos(key, (float)e.Size);
                        if (Math.Abs(ki.x - e.Inner.x) <= e.Around * e.Size + 1e-6 && Math.Abs(ki.z - e.Inner.z) <= e.Around * e.Size + 1e-6)
                            Recompute(og, key, half);
                    }
            }

            // 2. generated chunks that used a changed tile / lie in an event area → regenerate
            foreach (var cat in og.categories)
            {
                if (!(cat.useTerrainHeight || cat.useRemoveInsidePOI || cat.minDistFromRoad > 0f || cat.maxDistFromRoad > 0f || cat.minDistFromPOI > 0f)) continue;
                var areas = cat.useTile ? Changed : Regions;
                if (areas.Count == 0) continue;
                Pending q = null;
                foreach (var kv in cat.generatedChunks)
                {
                    if (!kv.Value.generated) continue;
                    var box = Bounds(cat, kv.Key);
                    foreach (var a in areas)
                        if (box.Hits(a))
                        {
                            if (q == null) q = Queue.GetValue(cat, _ => new Pending());
                            if (!q.Keys.Contains(kv.Key)) { q.Keys.Add(kv.Key); ChunksQueued++; }
                            break;
                        }
                }
                ObjGenFast.DiscardJob(cat); // a chunk being built over several frames may have used an old list
            }
        }

        private static void Recompute(objGenScript og, Vector3d key, double half)
        {
            if (!Recomputed.Add(key) || !og.tiles.TryGetValue(key, out var tc)) return;
            TilesRecomputed++;
            var roads = mapSettingScript.RoadsNear(key);
            var pois = mapSettingScript.POIsNear(key, og.tileSize);
            if (Same(roads, tc.nearRoads) && Same(pois, tc.poisNear)) return;
            tc.nearRoads = roads;
            tc.poisNear = pois;
            TilesChanged++;
            Changed.Add(new Rect(key.x - half, key.z - half, key.x + half, key.z + half));
        }

        private static bool Same<T>(List<T> a, List<T> b) where T : class
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }

        /// Everywhere a cell of this chunk can land (cell grid + jitter).
        private static Rect Bounds(objGenScript.categoryClass cat, Vector3d key)
        {
            double g = cat.gridSize, j = Math.Abs(cat.tollas) * g + 1e-6;
            double x0 = key.x + cat.offset, z0 = key.z + cat.offset;
            double span = (cat.resolution - 1) * g;
            return new Rect(x0 - j, z0 - j, x0 + span + j, z0 + span + j);
        }

        // ------------------------------------------------------------------ regeneration

        /// Called by ObjGenFast before generating new chunks: rebuilds one queued chunk (all when !moreFrame).
        internal static bool RegenPending(objGenScript.categoryClass cat, bool moreFrame)
        {
            if (!Queue.TryGetValue(cat, out var q) || q.Keys.Count == 0) return false;
            while (q.Keys.Count > 0)
            {
                var key = q.Keys[0];
                q.Keys.RemoveAt(0);
                if (!cat.generatedChunks.TryGetValue(key, out var chunk) || !chunk.generated) continue;
                ObjGenFast.Regenerate(cat, key, chunk);
                ChunksRegenerated++;
                if (moreFrame) return true;
            }
            return true;
        }

        /// Replace a chunk's objects with a fresh generation, keeping the GameObjects of objects that didn't change.
        internal static void Merge(objGenScript.categoryClass cat, objGenScript.chunkClass chunk, Dictionary<Vector3d, objGenScript.objClass> fresh)
        {
            List<Vector3d> gone = null;
            foreach (var kv in chunk.objs)
            {
                var old = kv.Value;
                if (fresh.TryGetValue(kv.Key, out var nw) && nw.prefabIndex == old.prefabIndex && nw.seed == old.seed && nw.rot.Equals(old.rot) && nw.scale == old.scale)
                {
                    if (!nw.gpos.Equals(old.gpos))
                    {
                        old.gpos = nw.gpos;
                        if (old.obj != null) old.obj.transform.position = mainscript.UnityPosFromGlobal(old.gpos);
                        ObjsMoved++;
                    }
                    fresh.Remove(kv.Key);
                }
                else
                {
                    if (old.obj != null) UnityEngine.Object.Destroy(old.obj);
                    (gone ?? (gone = new List<Vector3d>())).Add(kv.Key);
                    ObjsRemoved++;
                }
            }
            if (gone != null) foreach (var k in gone) chunk.objs.Remove(k);
            foreach (var kv in fresh) { chunk.objs.Add(kv.Key, kv.Value); ObjsAdded++; }
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"active\":" + (Active ? "true" : "false") +
            ",\"roadEvents\":" + RoadEvents + ",\"poiEvents\":" + PoiEvents + ",\"tilesRecomputed\":" + TilesRecomputed +
            ",\"tilesChanged\":" + TilesChanged + ",\"chunksQueued\":" + ChunksQueued + ",\"chunksRegenerated\":" + ChunksRegenerated +
            ",\"objsRemoved\":" + ObjsRemoved + ",\"objsMoved\":" + ObjsMoved + ",\"objsAdded\":" + ObjsAdded + "}";
    }
}

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.WorldGen
{
    /// Makes the game's own "main road only" generator (hasMainRoad on, hasSideRoad off) one unbroken road with no
    /// crossroads. The rules and why they are needed are in MainRoadPlan; this file puts them into roadGenScript.
    ///
    /// It works on the generator's flags, not on our terrain setting, so it also fixes a world whose flags another mod
    /// set (TerrainChoice's "main road only"). Crossroads worlds are never touched.
    [HarmonyPatch]
    public static class MainRoadOnlyRoads
    {
        /// Bridge switch: TLDRevamp.WorldGen.MainRoadOnlyRoads.Enabled (off = the game's own broken behaviour, for A/B).
        public static bool Enabled = true;

        public static long ChunksPruned, RoadsPruned, Links, SamePoint, Errors;
        public static string LastError = "";

        internal static bool Applies(roadGenScript g) =>
            Enabled && g != null && MainRoadPlan.Applies(g.hasMainRoad, g.hasSideRoad);

        public static long OffLineRemoved;

        /// How many roads the chunk that will hold this call's roads has before the call (-1: none to watch).
        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.ConnectDots))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void BeforeConnectDots(roadGenScript __instance, Vector3d _roadSelfChunk, out int __state)
        {
            __state = -1;
            try
            {
                if (Applies(__instance) && __instance.chunks.TryGetValue(_roadSelfChunk, out var chunk)) __state = chunk.roads.Count;
            }
            catch (Exception e) { Fail("count", e); }
        }

        /// Inside a main-road chunk: only the main path survives. Runs after the game's (or RevaMP's memo of the game's)
        /// ConnectDots, which typed the path as mainRoad and listed it in the chunk's mainRoad.
        ///
        /// Between chunks: no road may lead off the main road's line. The game makes the chunks beside the line (x != 0)
        /// for a frame while it looks at the needed chunks' neighbours, and its ConnectChunks joins a main-road chunk to
        /// one of them when it finds it: a side road out of the main road, or a road kept in the side chunk itself,
        /// which is never placed (the chunk is never needed) but is drawn far away (a road you can see and never reach).
        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.ConnectDots))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void AfterConnectDots(roadGenScript __instance, Vector3d _c1, Vector3d _c2, Vector3d _roadSelfChunk, int __state)
        {
            try
            {
                if (!Applies(__instance)) return;
                if (!_c1.Equals(_c2))
                {
                    if (__state >= 0 && (_c1.x != 0.0 || _c2.x != 0.0 || _roadSelfChunk.x != 0.0)
                        && __instance.chunks.TryGetValue(_roadSelfChunk, out var self))
                        RemoveOffLine(self, __state);
                    return;
                }
                if (_c1.x != 0.0 || !_c1.Equals(_roadSelfChunk)) return;
                if (!__instance.chunks.TryGetValue(_roadSelfChunk, out var chunk)) return;
                Prune(chunk);
            }
            catch (Exception e) { Fail("prune", e); }
        }

        /// Remove the roads this call added (from index `from` on) that touch a chunk off the main road's line. They are
        /// the last roads of the chunk and not generated yet, so nothing else points at them but the chunk's mainRoad.
        private static void RemoveOffLine(roadGenScript.dotChunkClass chunk, int from)
        {
            for (int i = chunk.roads.Count - 1; i >= from && i >= 0; i--)
            {
                var r = chunk.roads[i];
                if (r == null || (r.chunk1.x == 0.0 && r.chunk2.x == 0.0 && r.selfChunk.x == 0.0) || r.generated) continue;
                chunk.roads.RemoveAt(i);
                chunk.mainRoad.Remove(i);
                for (int k = 0; k < chunk.mainRoad.Count; k++) if (chunk.mainRoad[k] > i) chunk.mainRoad[k]--;
                for (int k = i; k < chunk.roads.Count; k++) if (chunk.roads[k] != null) chunk.roads[k].selfID = k;
                OffLineRemoved++;
            }
        }

        internal static void Prune(roadGenScript.dotChunkClass chunk)
        {
            var roads = chunk.roads;
            int n = roads.Count;
            if (n == 0) return;
            var isMain = new bool[n];
            for (int i = 0; i < n; i++) isMain[i] = roads[i] != null && roads[i].roadType == roadGenScript.roadTypeEmu.mainRoad;
            var kept = MainRoadPlan.KeptRoads(n, chunk.mainRoad, isMain);
            if (kept.Count == n) return;
            var map = MainRoadPlan.Remap(n, kept);
            var newRoads = new List<roadGenScript.roadClass>(kept.Count);
            foreach (int i in kept)
            {
                var r = roads[i];
                r.selfID = newRoads.Count;
                // Nothing has been generated yet (the game generates a chunk's roads only after it is connected), but a
                // road that somehow is keeps working: its elements are listed in the chunk's inner chunks by its index.
                newRoads.Add(r);
            }
            var newMain = new List<int>();
            foreach (int i in chunk.mainRoad) if (i >= 0 && i < n && map[i] >= 0 && !newMain.Contains(map[i])) newMain.Add(map[i]);
            if (newMain.Count == 0) for (int k = 0; k < newRoads.Count; k++) newMain.Add(k);
            foreach (var ic in chunk.innerChunks.Values)
            {
                for (int k = ic.roads.Count - 1; k >= 0; k--)
                {
                    int old = ic.roads[k].roadId;
                    if (old < 0 || old >= n || map[old] < 0) ic.roads.RemoveAt(k);
                    else ic.roads[k].roadId = map[old];
                }
            }
            // The in-chunk neighbour lists the game keeps (HasCommonNeighbor): only what is still connected.
            foreach (var d in chunk.dots) d.connectedDotIDsInSameChunk.Clear();
            foreach (var r in newRoads)
                if (r.chunk1.Equals(r.chunk2) && r.id1 >= 0 && r.id2 >= 0 && r.id1 < chunk.dots.Count && r.id2 < chunk.dots.Count)
                {
                    chunk.dots[r.id1].connectedDotIDsInSameChunk.Add(r.id2);
                    chunk.dots[r.id2].connectedDotIDsInSameChunk.Add(r.id1);
                }
            roads.Clear(); roads.AddRange(newRoads);
            chunk.mainRoad.Clear(); chunk.mainRoad.AddRange(newMain);
            ChunksPruned++;
            RoadsPruned += n - kept.Count;
        }

        /// Join each needed main-road chunk to the one north of it. The game's own ConnectChunks just ran and, in this
        /// mode, joined nothing.
        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.ConnectChunks))]
        [HarmonyPostfix]
        private static void AfterConnectChunks(roadGenScript __instance)
        {
            try
            {
                var g = __instance;
                if (!Applies(g) || !g.isActiveAndEnabled) return;
                var needed = g.chunksNeeded;
                for (int i = 0; i < needed.Count; i++)
                {
                    var lowerPos = needed[i];
                    if (lowerPos.x != 0.0) continue;
                    if (!g.chunks.TryGetValue(lowerPos, out var lower)) continue;
                    var upperPos = mainscript.GetChunkPos(lowerPos, 0, 1, g.chunkSize);
                    if (!g.chunks.TryGetValue(upperPos, out var upper)) continue;
                    if (!needed.Contains(upperPos)) continue;   // the ring chunk is remade every frame: wait until it is kept
                    if (!LinkSeen.TryGetValue(lower, out _)) LinkSeen.Add(lower, Seen);   // this pair is decided now (PoiLinksSettled)
                    bool want = MainRoadPlan.WantLink(lower.insideConnected, upper.insideConnected, lower.outsideUConnected,
                        lower.dots.Count, upper.dots.Count,
                        lower.dots.Count > 0 ? lower.dots[lower.dots.Count - 1].pos.x : 0, lower.dots.Count > 0 ? lower.dots[lower.dots.Count - 1].pos.z : 0,
                        upper.dots.Count > 0 ? upper.dots[0].pos.x : 0, upper.dots.Count > 0 ? upper.dots[0].pos.z : 0,
                        out bool same);
                    if (same && !lower.outsideUConnected) { lower.outsideUConnected = true; SamePoint++; continue; }
                    if (!want) continue;
                    // The pair the game itself makes the main road between two chunks (ConnectDots: the upper chunk's
                    // first dot and the lower chunk's last). ConnectWithRoad turns it to run south to north and keeps it
                    // in the lower chunk, as the game's own ConnectChunks does for the chunk below.
                    g.ConnectWithRoad(upperPos, 0, lowerPos, lower.dots.Count - 1, lowerPos, roadGenScript.roadTypeEmu.mainRoad);
                    lower.mainRoad.Add(lower.roads.Count - 1);
                    lower.outsideUConnected = true;
                    // The game generates a chunk's roads once (UpdGenRoad, right after this) and then marks the chunk
                    // done; a chunk whose upper neighbour became needed later is already done, and the new road would
                    // never be generated or placed. Open it again so UpdGenRoad generates just the new road.
                    lower.innerChunkGenerated = false;
                    Links++;
                }
            }
            catch (Exception e) { Fail("link", e); }
        }

        /// Bridge switch: TLDRevamp.WorldGen.MainRoadOnlyRoads.PoisWaitForLinks (off = before v0.69.1: buildings may align
        /// to a main road whose link to the next chunk isn't made yet).
        public static bool PoisWaitForLinks = true;
        public static long PoisHeldForLinks;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> LinkSeen = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();
        private static readonly object Seen = new object();
        [ThreadStatic] private static int _inAddPois;

        /// A building is placed once RoadsDoneAtPlace says the roads at its spot are done, and is aligned to the nearest
        /// road the generator offers then (RoadsNear: the road index around the spot; for a spot kilometres off the road,
        /// every main road of the chunks around it). The game's test is Done(): every road of the chunk generated, which is
        /// also true for a chunk whose dots aren't joined into roads yet (no roads at all). Measured (main road only, two
        /// copies arriving from opposite ends, 2026-10-23, poicheck.py + bridge `poialign`): a building 2.6 km off the road
        /// at z -27581 was offered only the chunk to the north's road on one machine, whose chunk -30000 had no roads yet,
        /// and went 7.5 km north to that road's end; the other machine aligned it to chunk -30000's road. Three of a row
        /// differed that way, for good. Buildings now wait until every main-line chunk they depend on has its roads
        /// (insideConnected), is needed (not a ring chunk the game makes for a frame), and when the chunk above is needed
        /// too, has had its link up decided (AfterConnectChunks): they always see the same, finished road.
        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPois))]
        [HarmonyPrefix]
        private static void BeforeAddPois() => _inAddPois++;

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPois))]
        [HarmonyFinalizer]
        private static void AfterAddPois() => _inAddPois--;

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.RoadsDoneAtPlace))]
        [HarmonyPostfix]
        private static void PoiLinksSettled(roadGenScript __instance, Vector3d _gpos, ref bool __result)
        {
            if (!__result || _inAddPois == 0 || !PoisWaitForLinks) return;
            try
            {
                var g = __instance;
                if (!Applies(g)) return;
                // the four chunks the game's own test looks at
                var c = mainscript.GetChunkPos(_gpos, 0, 0, g.chunkSize);
                int sx = Math.Sign(_gpos.x - c.x), sz = Math.Sign(_gpos.z - c.z);
                if (sx == 0) sx = 1;
                if (sz == 0) sz = 1;
                if (Settled(g, c) && Settled(g, mainscript.GetChunkPos(_gpos, sx, 0, g.chunkSize)) &&
                    Settled(g, mainscript.GetChunkPos(_gpos, 0, sz, g.chunkSize)) && Settled(g, mainscript.GetChunkPos(_gpos, sx, sz, g.chunkSize))) return;
                __result = false;
                PoisHeldForLinks++;
            }
            catch (Exception e) { Fail("poi wait", e); }
        }

        /// Bridge switch: TLDRevamp.WorldGen.MainRoadOnlyRoads.LineAhead (off = the game's own needed chunks).
        public static bool LineAhead = true;

        /// Buildings are generated farther out than the needed road chunks reach (with a long draw distance, 12-17 km
        /// ahead in 10 km road chunks), and the game lets them wait on a ring chunk; with PoisWaitForLinks they would
        /// wait until the player came closer. So on the main line each needed chunk's neighbours north and south are
        /// needed too: kept and linked, and the ring one chunk farther out than any building there looks.
        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.UpdChunksNeeded), new Type[0])]   // the per-frame one, not (Vector3d)
        [HarmonyPostfix]
        private static void NeedLineAhead(roadGenScript __instance)
        {
            try
            {
                var g = __instance;
                if (!LineAhead || !Applies(g) || g.chunksNeeded == null) return;
                int n = g.chunksNeeded.Count;
                for (int i = 0; i < n; i++)
                {
                    var c = g.chunksNeeded[i];
                    if (c.x != 0.0) continue;
                    var up = mainscript.GetChunkPos(c, 0, 1, g.chunkSize); var down = mainscript.GetChunkPos(c, 0, -1, g.chunkSize);
                    if (!g.chunksNeeded.Contains(up)) g.chunksNeeded.Add(up);
                    if (!g.chunksNeeded.Contains(down)) g.chunksNeeded.Add(down);
                }
                if (g.chunksNeeded.Count != n) g.UpdChunksToRemove();
            }
            catch (Exception e) { Fail("line ahead", e); }
        }

        /// Off the main line nothing is ever linked (the game's own test says done). On it: needed (not a ring chunk), and
        /// when the chunk above is needed too, their link decided.
        private static bool Settled(roadGenScript g, Vector3d pos)
        {
            if (pos.x != 0.0) return true;
            // insideConnected: its dots joined into roads. Before that its road list is empty, and the game's Done() (every
            // road generated) is true for no roads at all.
            if (!g.chunksNeeded.Contains(pos) || !g.chunks.TryGetValue(pos, out var chunk) || !chunk.insideConnected) return false;
            return !g.chunksNeeded.Contains(mainscript.GetChunkPos(pos, 0, 1, g.chunkSize)) || LinkSeen.TryGetValue(chunk, out _);
        }

        private static void Fail(string what, Exception e)
        {
            Errors++;
            LastError = what + ": " + e.Message;
            if (Errors <= 5) Plugin.Log.LogWarning("main road only: " + what + " failed: " + e);
        }

        // ------------------------------------------------------------------ diagnostics

        /// The main road of a generator as the check sees it: every main-road chunk the game keeps, its roads and
        /// whether the main road runs unbroken from the southmost to the northmost of them.
        internal static string Stats(roadGenScript g)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"enabled\":").Append(Enabled ? "true" : "false")
              .Append(",\"applies\":").Append(g != null && Applies(g) ? "true" : "false")
              .Append(",\"chunksPruned\":").Append(ChunksPruned)
              .Append(",\"roadsPruned\":").Append(RoadsPruned)
              .Append(",\"links\":").Append(Links)
              .Append(",\"samePoint\":").Append(SamePoint)
              .Append(",\"offLineRemoved\":").Append(OffLineRemoved)
              .Append(",\"errors\":").Append(Errors)
              .Append(",\"lastError\":").Append(Json.Str(LastError));
            if (g != null)
            {
                var views = new List<MainRoadPlan.ChunkView>();
                int ungenerated = 0;
                foreach (var pos in g.chunksNeeded)
                {
                    if (pos.x != 0.0 || !g.chunks.TryGetValue(pos, out var c)) continue;
                    var v = new MainRoadPlan.ChunkView { Z = pos.z, Dots = c.dots.Count, Roads = c.roads.Count, InsideDone = c.insideConnected };
                    if (c.dots.Count > 0)
                    {
                        v.South = Key(pos, 0); v.North = Key(pos, c.dots.Count - 1);
                        v.SouthX = c.dots[0].pos.x; v.SouthZ = c.dots[0].pos.z;
                        v.NorthX = c.dots[c.dots.Count - 1].pos.x; v.NorthZ = c.dots[c.dots.Count - 1].pos.z;
                    }
                    foreach (var r in c.roads)
                    {
                        if (r == null) continue;
                        if (r.roadType == roadGenScript.roadTypeEmu.mainRoad)
                        {
                            v.MainRoads++;
                            if (!r.generated) ungenerated++;
                            v.MainEdges.Add(new KeyValuePair<string, string>(Key(r.chunk1, r.id1), Key(r.chunk2, r.id2)));
                        }
                        else v.SideRoads++;
                    }
                    views.Add(v);
                }
                int gaps = MainRoadPlan.Gaps(views, out int side, out string firstGap);
                sb.Append(",\"mainChunks\":").Append(views.Count)
                  .Append(",\"gaps\":").Append(gaps)
                  .Append(",\"sideRoads\":").Append(side)
                  .Append(",\"mainNotGenerated\":").Append(ungenerated)
                  .Append(",\"firstGap\":").Append(Json.Str(firstGap))
                  .Append(",\"chunks\":[");
                views.Sort((a, b) => a.Z.CompareTo(b.Z));
                for (int i = 0; i < views.Count; i++)
                {
                    var v = views[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"z\":").Append(v.Z.ToString("0", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"dots\":").Append(v.Dots).Append(",\"roads\":").Append(v.Roads)
                      .Append(",\"main\":").Append(v.MainRoads).Append(",\"side\":").Append(v.SideRoads)
                      .Append(",\"inside\":").Append(v.InsideDone ? "true" : "false").Append('}');
                }
                sb.Append(']');
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// Every road generator of the map, plus any other roadGenScript in the scene (a generator the map does not list
        /// still makes roads if something drives it): its flags, whether it runs, its chunks and roads, and how many of
        /// its generated roads sit in chunks the game no longer keeps (those are never placed up close).
        internal static string Report(mapSettingScript map)
        {
            var gens = new List<roadGenScript>();
            if (map != null && map.roadGens != null) foreach (var g in map.roadGens) if (g != null && !gens.Contains(g)) gens.Add(g);
            int listed = gens.Count;
            foreach (var g in UnityEngine.Object.FindObjectsOfType<roadGenScript>(true)) if (g != null && !gens.Contains(g)) gens.Add(g);
            var sb = new System.Text.StringBuilder("{\"listed\":").Append(listed).Append(",\"generators\":[");
            for (int i = 0; i < gens.Count; i++)
            {
                var g = gens[i];
                int chunks = 0, roads = 0, generated = 0, placed = 0, outsideNeeded = 0, outsideGenerated = 0, offLineChunks = 0, offLineGenerated = 0;
                var offLine = new System.Text.StringBuilder();
                foreach (var kv in g.chunks)
                {
                    chunks++;
                    bool needed = g.chunksNeeded.Contains(kv.Key);
                    // a chunk off the main road's line (x != 0): in main road only mode the game never needs it, so a
                    // road generated in it is never placed, yet the distant ribbons (TerrainHorizon) would draw it
                    bool off = Math.Abs(kv.Key.x) > 0.5;
                    if (off) offLineChunks++;
                    foreach (var r in kv.Value.roads)
                    {
                        if (r == null) continue;
                        roads++;
                        if (r.generated) generated++;
                        if (!needed) { outsideNeeded++; if (r.generated) outsideGenerated++; }
                        if (off && r.generated)
                        {
                            offLineGenerated++;
                            if (offLineGenerated <= 6)
                            {
                                var a = r.start; var z = r.end;
                                if (offLine.Length > 0) offLine.Append(',');
                                offLine.Append("{\"chunk\":\"").Append(kv.Key.x.ToString("0")).Append(',').Append(kv.Key.z.ToString("0"))
                                  .Append("\",\"type\":").Append(Json.Str(r.roadType.ToString()))
                                  .Append(",\"from\":[").Append(a.x.ToString("0")).Append(',').Append(a.z.ToString("0"))
                                  .Append("],\"to\":[").Append(z.x.ToString("0")).Append(',').Append(z.z.ToString("0")).Append("]}");
                            }
                        }
                        foreach (var e in r.roadElements) if (e != null && e.obj != null) placed++;
                    }
                }
                // the roads the player can never reach: generated, so the distant ribbons draw them, but with road
                // pieces no inner chunk points at (the game only places pieces through its inner chunks), and pieces
                // in range of the player that are still not placed
                int unregistered = 0, unplacedInRange = 0;
                string unregisteredAt = "";
                foreach (var kv in g.chunks)
                {
                    if (!g.chunksNeeded.Contains(kv.Key)) continue;
                    var pointed = new HashSet<long>();
                    foreach (var ic in kv.Value.innerChunks)
                    {
                        bool inRange = false;
                        foreach (var ga in g.mapSetting.genArounds)
                        {
                            var d = ic.Key - mainscript.GlobalFromUnityPos(ga.upos); d.y = 0.0;
                            if (d.magnitude < g.roadPlaceDist) { inRange = true; break; }
                        }
                        foreach (var rc in ic.Value.roads)
                            foreach (int e in rc.roadElements)
                            {
                                pointed.Add(((long)rc.roadId << 32) | (uint)e);
                                if (inRange && rc.roadId < kv.Value.roads.Count && e < kv.Value.roads[rc.roadId].roadElements.Count
                                    && kv.Value.roads[rc.roadId].roadElements[e].obj == null) unplacedInRange++;
                            }
                    }
                    for (int ri = 0; ri < kv.Value.roads.Count; ri++)
                    {
                        var r = kv.Value.roads[ri];
                        if (r == null || !r.generated) continue;
                        for (int e = 0; e < r.roadElements.Count; e++)
                            if (!pointed.Contains(((long)ri << 32) | (uint)e))
                            {
                                unregistered++;
                                if (unregisteredAt.Length == 0 && r.roadElements[e].bonePos.Count > 0)
                                    unregisteredAt = r.roadElements[e].bonePos[0].x.ToString("0") + "," + r.roadElements[e].bonePos[0].z.ToString("0");
                            }
                    }
                }
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(Json.Str(g.gameObject.name))
                  .Append(",\"listed\":").Append(i < listed ? "true" : "false")
                  .Append(",\"active\":").Append(g.isActiveAndEnabled ? "true" : "false")
                  .Append(",\"hasMainRoad\":").Append(g.hasMainRoad ? "true" : "false")
                  .Append(",\"hasSideRoad\":").Append(g.hasSideRoad ? "true" : "false")
                  .Append(",\"chunkSize\":").Append(g.chunkSize.ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"needed\":").Append(g.chunksNeeded.Count)
                  .Append(",\"chunks\":").Append(chunks).Append(",\"roads\":").Append(roads)
                  .Append(",\"generated\":").Append(generated).Append(",\"placedElements\":").Append(placed)
                  .Append(",\"roadsOutsideNeeded\":").Append(outsideNeeded)
                  .Append(",\"generatedOutsideNeeded\":").Append(outsideGenerated)
                  .Append(",\"roadPlaceDist\":").Append(g.roadPlaceDist.ToString("0", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"unregisteredElements\":").Append(unregistered)
                  .Append(",\"unregisteredAt\":").Append(Json.Str(unregisteredAt))
                  .Append(",\"unplacedInRange\":").Append(unplacedInRange)
                  .Append(",\"offLineChunks\":").Append(offLineChunks)
                  .Append(",\"offLineGenerated\":").Append(offLineGenerated)
                  .Append(",\"offLine\":[").Append(offLine).Append(']')
                  .Append(",\"mainRoad\":").Append(Stats(g)).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        private static string Key(Vector3d chunk, int dot) =>
            chunk.z.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + ":" + dot;
    }
}

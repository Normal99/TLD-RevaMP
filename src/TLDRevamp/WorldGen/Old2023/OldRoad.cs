// Part of the old 2023 terrain (see OldTerrain.cs): the road is the 2023 build's own roadGenScript.CalcOneRoad.
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.WorldGen.Old2023
{
    /// The road of the public build v2023.05.02d, computed by its own roadGenScript.CalcOneRoad on the stand-in road
    /// generators of OldScene. The road starts at the world origin heading +z; each 100 m segment has 10 bones of 10 m;
    /// every bone turns towards a lateral target from two noise layers and pitches towards the averaged terrain
    /// height, limited per bone. A second road runs from the origin towards -z (see OldScene).
    /// This class keeps the computed points (every 10 m) and maps them into the beta's frame. The old code reads its
    /// Transforms, so segments are computed on the main thread (the road hooks below run there).
    internal sealed class OldRoadPath
    {
        private readonly OldScene _scene;
        private readonly Dir _fwd, _back;

        public OldRoadPath(OldScene scene)
        {
            _scene = scene;
            _fwd = new Dir(scene, scene.Forward, false);
            _back = new Dir(scene, scene.Back, true);
        }

        // public points are in beta global coordinates: the old world shifted by OldNoise.OffsetX
        public Vector3d Start => Shift(_fwd.Points[0]);
        public int ForwardSegments => _fwd.CalcNum;

        private static Vector3d Shift(Vector3d p) => new Vector3d(p.x + OldNoise.OffsetX, p.y, p.z);

        /// The road's points (every 10 m) from z0 to z1 (z0 < z1), with exact interpolated end points.
        public List<Vector3d> Between(double z0, double z1)
        {
            var list = new List<Vector3d>();
            lock (_scene.Lock)
            {
                list.Add(AtLocked(z0));
                if (z0 < 0)
                {
                    var bk = _back.Through(-z0);   // [0] is the origin, which the forward road holds
                    for (int i = bk.Count - 1; i > 0; i--) if (bk[i].z > z0 && bk[i].z < z1) list.Add(Shift(bk[i]));
                }
                if (z1 > 0)
                {
                    var f = _fwd.Through(z1);
                    int i0 = First(f, Math.Max(z0, 0.0), 1.0);
                    for (int i = Math.Max(0, i0 - 1); i < f.Count && f[i].z < z1; i++) if (f[i].z > z0) list.Add(Shift(f[i]));
                }
                list.Add(AtLocked(z1));
            }
            return list;
        }

        /// The point of the road at this z (interpolated between its 10 m points).
        public Vector3d At(double z) { lock (_scene.Lock) return AtLocked(z); }

        private Vector3d AtLocked(double z)
        {
            var pts = z >= 0 ? _fwd.Through(z) : _back.Through(-z);
            double s = z >= 0 ? 1 : -1;
            int i = First(pts, z, s);
            if (i >= pts.Count) return Shift(pts[pts.Count - 1]);   // past the end of the old road (5000 km)
            if (i == 0) return Shift(pts[0]);
            double a = pts[i - 1].z * s, b = pts[i].z * s, t = z * s;
            double f = b > a ? (t - a) / (b - a) : 1.0;
            return Shift(new Vector3d(pts[i - 1].x + (pts[i].x - pts[i - 1].x) * f, pts[i - 1].y + (pts[i].y - pts[i - 1].y) * f, z));
        }

        /// The first point whose z (times s, so the -z road counts too) reaches z * s: the points only move away from the origin.
        private static int First(List<Vector3d> pts, double z, double s)
        {
            int lo = 0, hi = pts.Count;
            double t = z * s;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (pts[mid].z * s < t) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// One direction of the old road: its stand-in generator and the points computed so far.
        private sealed class Dir
        {
            public readonly List<Vector3d> Points = new List<Vector3d>();   // old frame, every 10 m; [0] = the start
            public int CalcNum;
            private readonly OldScene _scene;
            private readonly object _road;
            private readonly bool _mirror;   // the -z road: computed on the turned terrain, points stored as (-x, y, -z)
            private readonly Vector3d _start;
            private readonly int _max;
            private readonly Vector3[] _bones = new Vector3[10];

            public Dir(OldScene scene, object road, bool mirror)
            {
                _scene = scene; _road = road; _mirror = mirror;
                _start = scene.Start(road);
                _max = scene.SegmentCount(road);
                Points.Add(_start);
            }

            /// Computed at least to this distance along its direction.
            public List<Vector3d> Through(double dist)
            {
                while (CalcNum < _max && Math.Abs(Points[Points.Count - 1].z - _start.z) < dist + 20.0)
                {
                    if (!_scene.CalcOne(_road, _mirror, out var seg, _bones)) { CalcNum = _max; break; }
                    // a bone's position: the road's start + the segment's start + the bone's offset (coords keep y
                    // less roadAboveTerrain)
                    foreach (var b in _bones)
                    {
                        double x = _start.x + seg.x + b.x, y = _start.y + seg.y + ((double)b.y + OldScene.RoadAboveTerrain), z = _start.z + seg.z + b.z;
                        Points.Add(_mirror ? new Vector3d(-x, y, -z) : new Vector3d(x, y, z));
                    }
                    CalcNum++;
                }
                return Points;
            }
        }
    }

    /// Puts the old road into the beta's road generator (old terrain mode only, which also turns side roads off):
    /// - every main-column dot chunk gets exactly two dots, on the old road at the chunk's south and north edges, so
    ///   the beta makes one main road per chunk and neighbouring chunks meet at the same point;
    /// - those roads get their bones from the old road (every 10 m) instead of the beta's meander.
    /// Everything else (road pieces, power poles, terrain blending, building alignment, RoadsNear) is the beta's own.
    internal static class OldRoad
    {
        private static volatile OldRoadPath _path;
        private static volatile roadGenScript[] _owners = new roadGenScript[0];
        public static long Chunks, Roads, Elements;

        public static OldRoadPath Path => _path;

        public static void Set(mapSettingScript ms, OldScene scene, List<roadGenScript> gens)
        {
            if (scene == null) { _path = null; _owners = new roadGenScript[0]; return; }
            _owners = gens.ToArray();
            _path = new OldRoadPath(scene);
            Chunks = Roads = Elements = 0;
            Plugin.Log.LogInfo("Old road: the 2023 road from " + _path.Start + " on " + _owners.Length + " road generator(s)");
        }

        private static bool Owns(roadGenScript g)
        {
            if (_path == null) return false;
            foreach (var o in _owners) if (ReferenceEquals(o, g)) return true;
            return false;
        }

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.UpdDotChunk))]
        private static class Dots
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First)]
            private static bool Prefix(roadGenScript __instance, Vector3d _cPos)
            {
                if (!Owns(__instance) || __instance.chunks.ContainsKey(_cPos) || _cPos.x != 0.0) return true;
                var path = _path;
                double half = __instance.chunkSize * 0.5;
                var a = path.At(_cPos.z - half);
                var b = path.At(_cPos.z + half);
                a.y += __instance.offsetty; b.y += __instance.offsetty;
                var c = new roadGenScript.dotChunkClass();
                c.seed = _cPos.GetHashCode() + __instance.mapSetting.seed;
                c.rnd = new System.Random(c.seed);
                c.dotCount = 2;
                c.dots.Add(new roadGenScript.dotClass(a));
                c.dots.Add(new roadGenScript.dotClass(b));
                __instance.chunks.Add(_cPos, c);
                Chunks++;
                return false;
            }
        }

        [HarmonyPatch(typeof(roadGenScript.roadClass), nameof(roadGenScript.roadClass.Generate))]
        private static class Bones
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First)]
            private static bool Prefix(roadGenScript.roadClass __instance, roadGenScript _roadgen)
            {
                if (__instance.roadType != roadGenScript.roadTypeEmu.mainRoad || __instance.generated || __instance.roadElements.Count != 0 || !Owns(_roadgen)) return true;
                if (!_roadgen.chunks.TryGetValue(__instance.chunk1, out var c1) || !_roadgen.chunks.TryGetValue(__instance.chunk2, out var c2)
                    || !_roadgen.chunks.TryGetValue(__instance.selfChunk, out var self)) return true;   // the game's/RevaMP's own handling
                var from = c1.dots[__instance.id1].pos;
                var to = c2.dots[__instance.id2].pos;
                bool reverse = from.z > to.z;
                var pts = _path.Between(Math.Min(from.z, to.z), Math.Max(from.z, to.z));
                if (reverse) pts.Reverse();
                double off = _roadgen.offsetty;
                for (int i = 1; i < pts.Count - 1; i++) { var p = pts[i]; p.y += off; pts[i] = p; }
                pts[0] = from; pts[pts.Count - 1] = to;   // the dots already carry offsetty
                var ts = _roadgen.terrainSettings;
                for (int k = 0; k < pts.Count - 1; k += 10)
                {
                    var el = new roadGenScript.roadElementClass();
                    __instance.roadElements.Add(el);
                    _roadgen.UpdInnerChunk(pts[k], self, __instance, __instance.selfID);
                    if (ts != null)
                    {
                        int biome = ts.GetBiomeInt(pts[k]);
                        el.biome = biome;
                        el.roadID = itemdatabase.s.RoadID(ts.GetBiomeIntNoSnow(pts[k]), biome == 1, __instance.roadType);
                    }
                    int end = Math.Min(k + 10, pts.Count - 1);
                    for (int i = k; i <= end; i++) el.bonePos.Add(pts[i]);
                    Elements++;
                }
                __instance.generated = true;
                Roads++;
                return false;
            }
        }
    }
}

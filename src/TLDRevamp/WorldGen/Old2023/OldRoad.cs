// The old 2023 terrain, ported from AnonymousTLD's TerrainChoice 0.4.1 (github.com/AnonymousTLD/TLD-TerrainChoice,
// GPL-3.0, the same licence as TLD RevaMP), with AnonymousTLD's permission as a project collaborator. The formulas are
// theirs: re-implemented from the public game v2023.05.02d and checked against it (terrain heights equal, the road within
// 1 mm over 40 km, all 612 roadside buildings of seed 12345 in place). Changes for RevaMP: the namespace, the switch-on
// (TerrainHooks -> OldWorld.Apply instead of the mod's own LoadMap/FStart patches) and RevaMP's worker-thread terrain
// (Fixes.AsyncTerrain asks OldTerrain.TryHeight/TryBiome directly instead of being patched by reflection).
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.WorldGen.Old2023
{
    /// The road of the public build v2023.05.02d: a port of its roadGenScript.CalcOneRoad (P:39450). The road starts at
    /// the world origin heading +z; each 100 m segment has 10 bones of 10 m; every bone turns towards a lateral target
    /// from two noise layers (OneValue x width) and pitches towards the averaged terrain height, limited per bone.
    /// The numbers below are the public build's scene values (seed-independent), read from the user's own copy with
    /// tools\OldRoadDump; the seed only enters through the terrain and roadSeed, as in the old game.
    /// The old world had no road behind the start; here a second road runs from the origin towards -z with the same
    /// rules (its own noise seed), because the beta's world goes on in both directions. It is computed by the unchanged
    /// algorithm on the terrain turned by 180 degrees around the origin, and its points are turned back (rotating the
    /// road's frame instead would flip the sign of the pitch correction).
    internal sealed class OldRoadPath
    {
        // public build scene values (roadGenScript on the map, v2023.05.02d)
        private const float Length = 5000000f, Width = 1000f, Scale = 9.99999975E-05f, RoadAboveTerrain = -0.0199999996f;
        private const float XMaxAngle = 6f, YMaxAngle = 10f, BoneLength = 10f, StraightModifier = 0.200000003f;
        private const int BoneNum = 10, StartStraight = 1, MaxSegments = 50000;   // Length / roadSegmentLength (100 m)
        private static readonly float[,] Noises = { { 1f, 10f, 3f, 0.5f, 2f }, { 1f, 5f, 3f, 0.5f, 2f } };   // amplitude, frequency, octaves, persistence, lacunarity

        private readonly Dir _fwd, _back;
        private readonly object _lock = new object();

        public OldRoadPath(OldNoise terrain)
        {
            var rnd = new System.Random(terrain.Seed);   // old FStart: rnd = new Random(settings.seed); roadSeed = first draw
            float seed1 = (float)rnd.Next(-100000, 100000) * 1E-05f;
            float seed2 = (float)rnd.Next(-100000, 100000) * 1E-05f;
            // old FStart: the road's world sits at the terrain height of its position (the origin)
            var start = new Vector3(0f, terrain.Height(0f, 0f), 0f);
            _fwd = new Dir(terrain, seed1, false, start);
            _back = new Dir(terrain, seed2, true, start);
        }

        // public points are in beta global coordinates: the old world shifted by OldNoise.OffsetX
        public Vector3d Start => Shift(_fwd.Points[0]);
        public int ForwardSegments => _fwd.CalcNum;
        public Vector3d ForwardSegmentPos(int i) => _fwd.SegPos[i];   // old-game frame, for checks against the old game

        private static Vector3d Shift(Vector3d p) => new Vector3d(p.x + OldNoise.OffsetX, p.y, p.z);

        /// The road's ideal line at this distance from the start (OneValue x width; the old building plan follows it).
        /// back: the -z road, in its own turned frame.
        public float TargetX(bool back, float dist) { lock (_lock) return (back ? _back : _fwd).OneValue(dist) * Width; }

        /// The road's points (every 10 m) from z0 to z1 (z0 < z1), with exact interpolated end points.
        public List<Vector3d> Between(double z0, double z1)
        {
            var list = new List<Vector3d>();
            lock (_lock)
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
        public Vector3d At(double z) { lock (_lock) return AtLocked(z); }

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

        /// One direction of the old road: CalcOneRoad, unchanged apart from the frame (identity = the old road).
        private sealed class Dir
        {
            public readonly List<Vector3d> Points = new List<Vector3d>();   // global, every 10 m; [0] = the start
            public readonly List<Vector3d> SegPos = new List<Vector3d>();   // roads[i].posX/Y/Z, for checks against the old game
            public int CalcNum;
            private readonly OldNoise _terrain;
            private readonly float _roadSeed;
            private readonly Vector3 _fwd = Vector3.forward, _right = Vector3.right, _start;
            private readonly bool _mirror;   // the -z road: terrain sampled at (-x, -z), points stored as (-x, y, -z)
            private double _px, _py, _pz;      // doublePos
            private Vector3 _sumRot;
            private bool _first;
            private float _offset;

            public Dir(OldNoise terrain, float roadSeed, bool mirror, Vector3 start)
            {
                _terrain = terrain; _roadSeed = roadSeed; _start = start; _mirror = mirror;
                Points.Add(new Vector3d(start.x, start.y, start.z));
            }

            /// Computed at least to this distance along its direction.
            public List<Vector3d> Through(double dist)
            {
                while (CalcNum < MaxSegments && Math.Abs(Points[Points.Count - 1].z - _start.z) < dist + 20.0) CalcOne();
                return Points;
            }

            private float H(float x, float z) => _mirror ? _terrain.Height(-x, -z) : _terrain.Height(x, z);   // GetTerrainHeightWorld: no road/building blending on the map's road

            private static float TurnAngle(float a, float c, float minmax) =>
                Mathf.Clamp(Mathf.Asin(Mathf.Clamp(a / c, -1f, 1f)) * 57.29578f, -minmax, minmax);

            private float NoiseGet(int n, float x, float l, float s, float seed)
            {
                float v = 0f, a = Noises[n, 0], f = Noises[n, 1], persistence = Noises[n, 3], lacunarity = Noises[n, 4];
                int octaves = (int)Noises[n, 2];
                for (int i = 1; i <= octaves + 1; i++)
                {
                    float num = Mathf.PerlinNoise(seed + x / (l * s * f), (seed + ((float)i / (float)octaves + 1f) + x) / (l * s * f)) * a;
                    v += num;
                    a *= persistence;
                    f *= lacunarity;
                }
                return v;
            }

            public float OneValue(float pos)
            {
                if (!_first) { _first = true; _offset = 0f; _offset = 0f - OneValue(0f); }
                float v = 0f;
                for (int i = 0; i < Noises.GetLength(0); i++) v += NoiseGet(i, pos, Length, Scale, _roadSeed);
                return v + _offset;
            }

            private float OneValue(Vector3 objInWorld) => OneValue(Vector3.Project(objInWorld - _start, _fwd).magnitude);

            private Vector3 OneCoord(Vector3 objInWorld) => _start + Vector3.Project(objInWorld - _start, _fwd) + _right * OneValue(objInWorld) * Width;

            private void CalcOne()
            {
                double sx = _px, sy = _py, sz = _pz;   // roads[calcNum].posX/Y/Z
                SegPos.Add(new Vector3d(sx, sy, sz));
                Vector3 lastPos = Quaternion.Euler(_sumRot.x, _sumRot.y, _sumRot.z) * (_fwd * BoneLength);
                _px += lastPos.x; _py += lastPos.y; _pz += lastPos.z;
                Add(sx, sy, sz, lastPos);
                for (int i = 1; i < BoneNum; i++)
                {
                    Vector3 lastRot = Vector3.zero;
                    Vector3 svector = new Vector3((float)(_start.x + sx + (double)lastPos.x), (float)(_start.y + sy + (double)lastPos.y), (float)(_start.z + sz + (double)lastPos.z))
                                      + Quaternion.Euler(_sumRot.x, _sumRot.y, _sumRot.z) * (_fwd * BoneLength);
                    float height = RoadAboveTerrain + H(svector.x, svector.z);
                    Vector3 vector = Quaternion.Euler(_sumRot.x, _sumRot.y, _sumRot.z) * (_fwd * BoneLength);
                    height = (height + RoadAboveTerrain + H(svector.x + vector.x, svector.z + vector.z)) * 0.5f;
                    height = (height + RoadAboveTerrain + H(svector.x + vector.x + vector.x, svector.z + vector.z + vector.z)) * 0.5f;
                    height = (height + RoadAboveTerrain + H(svector.x - vector.x, svector.z - vector.z)) * 0.5f;
                    height = (height + RoadAboveTerrain + H(svector.x - vector.x - vector.x, svector.z - vector.z - vector.z)) * 0.5f;
                    lastRot.x = TurnAngle((float)((double)svector.y - (0.0 + (double)height)), BoneLength, XMaxAngle);   // mainWorld.coord.y = 0 in the old frame
                    lastRot.y = TurnAngle(Vector3.Dot(OneCoord(svector) - svector, _right), BoneLength, YMaxAngle);
                    if (CalcNum < StartStraight) lastRot.y *= StraightModifier;
                    lastRot.z = 0f;
                    _sumRot += lastRot;
                    _sumRot = new Vector3(Mathf.Clamp(_sumRot.x, -90f, 90f), Mathf.Clamp(_sumRot.y, -90f, 90f), Mathf.Clamp(_sumRot.z, -90f, 90f));
                    Vector3 tempPos = Quaternion.Euler(_sumRot.x, _sumRot.y, _sumRot.z) * (_fwd * BoneLength);
                    lastPos += tempPos;
                    _px += tempPos.x; _py += tempPos.y; _pz += tempPos.z;
                    Add(sx, sy, sz, lastPos);
                }
                CalcNum++;
            }

            private void Add(double sx, double sy, double sz, Vector3 lastPos)
            {
                double x = _start.x + sx + lastPos.x, y = _start.y + sy + lastPos.y, z = _start.z + sz + lastPos.z;
                Points.Add(_mirror ? new Vector3d(-x, y, -z) : new Vector3d(x, y, z));
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

        public static void Set(mapSettingScript ms, OldNoise noise, List<roadGenScript> gens)
        {
            if (noise == null) { _path = null; _owners = new roadGenScript[0]; return; }
            _owners = gens.ToArray();
            _path = new OldRoadPath(noise);
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

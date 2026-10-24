using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.WorldGen.Old2023
{
    /// The few objects of the 2023 map that its road and building code read, as stand-ins: old-build objects with no
    /// constructor run and no Unity object behind them (OldBuild.StandIn), their fields set to the values the old map
    /// gives them. The 2023 code then runs on them unchanged: roadGenScript.FStart/CalcOneRoad (the road),
    /// roadBuildingScript.FStart/GenAll (the roadside building plan) and TerrainGenerationJob.randomize (the ground,
    /// through TerrainGenerator.GetTerrainHeightWorld).
    ///
    /// The numbers below are the public build's scene values (seed-independent), read from the user's own copy with
    /// tools\OldRoadDump; the seed only enters through the terrain, the road's seed and the building plan's random,
    /// as in the old game. The old world's own transforms (the road's frame, the main world) are real Transforms of
    /// one hidden object, so the old code must run on the main thread.
    ///
    /// The 2023 world had no road behind the start. Because the beta's world goes on in both directions, a second road
    /// runs from the origin towards -z by the same code: it is computed on the terrain turned by 180 degrees around
    /// the origin (Mirror) and its points are turned back (rotating the road's frame instead would flip the sign of the
    /// pitch correction). Its noise seed is the second draw of the road's random, its building plan uses seed + 1.
    internal sealed class OldScene : IDisposable
    {
        // the map's roadGenScript (v2023.05.02d)
        public const float RoadAboveTerrain = -0.0199999996f;
        private const float Length = 5000000f, Width = 1000f, Scale = 9.99999975E-05f, XMaxAngle = 6f, YMaxAngle = 10f;
        private const float BoneLength = 10f, SegmentLength = 100f, StraightModifier = 0.200000003f;
        private const int BoneNum = 10, StartStraight = 1;
        private static readonly float[,] Noises = { { 1f, 10f, 3f, 0.5f, 2f }, { 1f, 5f, 3f, 0.5f, 2f } };   // amplitude, frequency, octaves, persistence, lacunarity

        // the map's roadBuildingScript: groups of building types (old itemdatabase.b ids) with their distances and the
        // sides as the old FStart leaves them (the defaults times each type's xDistModifier)
        private const float DistModifier = 1f, MinStartDist = 3000f, MaxStartDist = 10000f;
        private sealed class TypeRow { public int Type; public float MinDist, MaxDist; public float[][] Sides; }
        private static float[][] Sides(float near, float far) => new[] { new[] { -far, -near, 165f, 195f }, new[] { near, far, -15f, 15f } };
        private static TypeRow T(int type, float minDist, float maxDist, float[][] sides) => new TypeRow { Type = type, MinDist = minDist, MaxDist = maxDist, Sides = sides };
        private static readonly (int Min, int Max, TypeRow[] Types)[] Groups =
        {
            (3, 6, new[] {
                T(4, 5000f, 10000f, Sides(10f, 20f)),     // benzinkut01
                T(5, 3000f, 6000f, Sides(5f, 10f)),       // buszmegallo
                T(6, 6000f, 10000f, Sides(15f, 30f)),     // legjobbbenzinkut
                T(7, 5000f, 15000f, Sides(15f, 30f)),     // muhely
                T(10, 5000f, 10000f, Sides(15f, 30f)),    // oldpetrolstation
                T(11, 5000f, 8000f, Sides(15f, 30f)) }),  // turrethaz
            (1, 1, new[] {
                T(8, 8000f, 15000f, Sides(30f, 60f)),     // nagybenzinkut
                T(9, 8000f, 15000f, Sides(30f, 60f)),     // oldhouse
                T(12, 8000f, 15000f, new[] { new[] { 0f, 0f, -10f, 10f }, new[] { 0f, 0f, 170f, 190f } }),   // vizmu, on the line
                T(16, 8000f, 15000f, Sides(8f, 16f)),     // yellowfoodkut
                T(18, 8000f, 15000f, Sides(80f, 160f)) }), // mansion
        };

        /// The -z road's computations: the old height lookups see the terrain turned by 180 degrees.
        [ThreadStatic] public static bool Mirror;

        /// Every call into the old road and building code holds this: they share the old singletons.
        public readonly object Lock = new object();
        public readonly int Seed;
        public readonly object Forward, Back;   // the two roadGenScript stand-ins
        private readonly object _main, _generator;
        private readonly GameObject _root;

        /// Main thread (the old code reads its Transforms).
        public OldScene(int seed)
        {
            Seed = seed;
            _root = new GameObject("TLDRevamp old 2023 world") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_root);

            // mainscript.M: the seed, the main world at the origin, an empty menu, no load
            _main = OldBuild.StandIn(OldBuild.Main);
            OldBuild.Set(_main, "seed", seed);
            OldBuild.Set(_main, "load", false);
            OldBuild.Set(_main, "mainWorld", OldBuild.StandIn(OldBuild.Coord2));
            OldBuild.Set(_main, "world", Frame("main world"));
            OldBuild.Set(_main, "menu", OldBuild.StandIn(OldBuild.Menu));
            OldBuild.Set(_main, "DDS", OldBuild.StandIn(OldBuild.DrawDistance));
            OldBuild.SetStatic(OldBuild.Main, "M", _main);

            // TerrainGenerator.TG: the seeded job; its manager's settings hold the seed and the terrain's world (origin)
            var settings = OldBuild.StandIn(OldBuild.Settings);
            OldBuild.Set(settings, "seed", seed);
            OldBuild.Set(settings, "world2", OldBuild.StandIn(OldBuild.Coord2));
            var manager = OldBuild.StandIn(OldBuild.Manager);
            OldBuild.Set(manager, "settings", settings);
            _generator = OldBuild.StandIn(OldBuild.Generator);
            OldBuild.Set(_generator, "manager", manager);
            OldBuild.Set(_generator, "tjob", OldBuild.NewJob(seed));
            OldBuild.Set(_generator, "roads", NewList(OldBuild.Road));
            OldBuild.SetStatic(OldBuild.Generator, "TG", _generator);
            var temporary = OldBuild.StandIn(OldBuild.Temporary);
            OldBuild.Set(temporary, "RoadGens", NewList(OldBuild.Road));

            Forward = NewRoad("road +z", settings, temporary, false);
            Back = NewRoad("road -z", settings, temporary, true);
            var rnd = new System.Random(seed);   // the -z road: the second draw of the random the old FStart draws roadSeed from
            rnd.Next(-100000, 100000);
            OldBuild.Set(Back, "roadSeed", (float)rnd.Next(-100000, 100000) * 1E-05f);
        }

        private Transform Frame(string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(_root.transform, false);
            return t;
        }

        private static IList NewList(Type item) => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(item));

        /// The map's road generator: its scene values, then the old FStart (start height, roadSeed, segment array).
        private object NewRoad(string name, object settings, object temporary, bool mirror)
        {
            var road = OldBuild.StandIn(OldBuild.Road);
            OldBuild.Set(road, "length", Length);
            OldBuild.Set(road, "width", Width);
            OldBuild.Set(road, "scale", Scale);
            OldBuild.Set(road, "roadAboveTerrain", RoadAboveTerrain);
            OldBuild.Set(road, "XmaxAngle", XMaxAngle);
            OldBuild.Set(road, "YmaxAngle", YMaxAngle);
            OldBuild.Set(road, "roadBoneLength", BoneLength);
            OldBuild.Set(road, "roadSegmentLength", SegmentLength);
            OldBuild.Set(road, "roadBoneNum", BoneNum);
            OldBuild.Set(road, "startStraight", StartStraight);
            OldBuild.Set(road, "straightModifier", StraightModifier);
            var noises = Array.CreateInstance(OldBuild.RoadNoise, Noises.GetLength(0));
            for (int i = 0; i < noises.Length; i++)
            {
                var n = Activator.CreateInstance(OldBuild.RoadNoise);
                OldBuild.Set(n, "amplitude", Noises[i, 0]);
                OldBuild.Set(n, "frequency", Noises[i, 1]);
                OldBuild.Set(n, "octaves", (int)Noises[i, 2]);
                OldBuild.Set(n, "persistence", Noises[i, 3]);
                OldBuild.Set(n, "lacunarity", Noises[i, 4]);
                noises.SetValue(n, i);
            }
            OldBuild.Set(road, "noises", noises);
            OldBuild.Set(road, "genAtStart", 0);   // segments are computed as the world needs them
            OldBuild.Set(road, "world", Frame(name));
            OldBuild.Set(road, "world2", OldBuild.StandIn(OldBuild.Coord2));
            OldBuild.Set(road, "terrainGenerator", _generator);
            OldBuild.Set(road, "settings", settings);
            OldBuild.Set(road, "temporary", temporary);
            Mirror = mirror;
            try { OldBuild.RoadStart(road); }
            finally { Mirror = false; }
            return road;
        }

        /// The road's start (its world2.coord after FStart: the origin at the terrain's height there).
        public Vector3d Start(object road)
        {
            object c = OldBuild.Get(OldBuild.Get(road, "world2"), "coord");
            return new Vector3d((double)OldBuild.Get(c, "x"), (double)OldBuild.Get(c, "y"), (double)OldBuild.Get(c, "z"));
        }

        public int SegmentCount(object road) => ((Array)OldBuild.Get(road, "roads")).Length;

        /// One more 100 m segment of the road (roadGenScript.CalcOneRoad): its start (posX/Y/Z, relative to the road's
        /// start) and its 10 bones (coords: the bone positions, relative to the segment's start, y less roadAboveTerrain).
        public bool CalcOne(object road, bool mirror, out Vector3d segPos, Vector3[] bones)
        {
            segPos = default;
            int n = (int)OldBuild.Get(road, "calcNum");
            Mirror = mirror;
            bool ok;
            try { ok = OldBuild.CalcOneRoad(road); }
            finally { Mirror = false; }
            if (!ok) return false;
            object seg = ((Array)OldBuild.Get(road, "roads")).GetValue(n);
            segPos = new Vector3d((double)OldBuild.Get(seg, "posX"), (double)OldBuild.Get(seg, "posY"), (double)OldBuild.Get(seg, "posZ"));
            var coords = (Array)OldBuild.Get(seg, "coords");
            for (int i = 0; i < bones.Length && i < coords.Length; i++)
            {
                object c = coords.GetValue(i);
                bones[i] = new Vector3((float)OldBuild.Get(c, "x"), (float)OldBuild.Get(c, "y"), (float)OldBuild.Get(c, "z"));
            }
            return true;
        }

        public struct Building { public double X, Y, Z; public float Ry; public int OldType; }

        /// The road's building plan (roadBuildingScript.FStart -> GenAll): positions relative to the road's start,
        /// y relative to the start's height (the old buildinginmemory). back: the -z road's, in its turned frame.
        public List<Building> BuildingPlan(bool back)
        {
            var b = OldBuild.StandIn(OldBuild.Buildings);
            var groups = Array.CreateInstance(OldBuild.BuildingGroup, Groups.Length);
            for (int g = 0; g < Groups.Length; g++)
            {
                var group = Activator.CreateInstance(OldBuild.BuildingGroup);   // its constructor makes the available lists
                OldBuild.Set(group, "minCount", Groups[g].Min);
                OldBuild.Set(group, "maxCount", Groups[g].Max);
                var types = Array.CreateInstance(OldBuild.BuildingType, Groups[g].Types.Length);
                for (int t = 0; t < types.Length; t++)
                {
                    var row = Groups[g].Types[t];
                    var type = Activator.CreateInstance(OldBuild.BuildingType);
                    OldBuild.Set(type, "type", Enum.ToObject(OldBuild.BuildingEnum, row.Type));
                    OldBuild.Set(type, "minDist", row.MinDist);
                    OldBuild.Set(type, "maxDist", row.MaxDist);
                    var sides = Array.CreateInstance(OldBuild.Side, row.Sides.Length);
                    for (int s = 0; s < sides.Length; s++)
                    {
                        var side = Activator.CreateInstance(OldBuild.Side);
                        OldBuild.Set(side, "minXpos", row.Sides[s][0]);
                        OldBuild.Set(side, "maxXpos", row.Sides[s][1]);
                        OldBuild.Set(side, "minYAngle", row.Sides[s][2]);
                        OldBuild.Set(side, "maxYAngle", row.Sides[s][3]);
                        sides.SetValue(side, s);
                    }
                    OldBuild.Set(type, "sides", sides);
                    types.SetValue(type, t);
                }
                OldBuild.Set(group, "types", types);
                groups.SetValue(group, g);
            }
            OldBuild.Set(b, "groups", groups);
            OldBuild.Set(b, "defaultSides", Array.CreateInstance(OldBuild.Side, 0));
            OldBuild.Set(b, "distModifier", DistModifier);
            OldBuild.Set(b, "minStartDist", MinStartDist);
            OldBuild.Set(b, "maxStartDist", MaxStartDist);
            OldBuild.Set(b, "buildings", NewList(OldBuild.BuildingInMemory));
            OldBuild.Set(b, "road", back ? Back : Forward);
            OldBuild.Set(_main, "seed", back ? unchecked(Seed + 1) : Seed);   // the old FStart seeds its random from mainscript.M.seed
            Mirror = back;
            try { OldBuild.BuildingsStart(b); }
            finally { Mirror = false; OldBuild.Set(_main, "seed", Seed); }
            var list = new List<Building>();
            foreach (var m in (IList)OldBuild.Get(b, "buildings"))
                list.Add(new Building
                {
                    X = (double)OldBuild.Get(m, "px"), Y = (double)OldBuild.Get(m, "py"), Z = (double)OldBuild.Get(m, "pz"),
                    Ry = (float)OldBuild.Get(m, "ry"), OldType = (int)OldBuild.Get(m, "tipus"),
                });
            return list;
        }

        public void Dispose()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
        }
    }
}

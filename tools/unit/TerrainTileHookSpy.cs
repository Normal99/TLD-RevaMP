#pragma warning disable CS0649, CS8981
using System;
using System.Collections.Generic;
using TLDRevamp.Fixes;
using UnityEngine;

/// The other half of the TerrainHorizon partial class, for the hook test.
///
/// In the shipped assembly the hook file (src/TLDRevamp/Fixes/TerrainHorizon.TileHook.cs) is
/// joined by the real TerrainHorizon.cs, which supplies these members. Here the real file is not
/// compiled (it needs the whole game), so this spy supplies them instead: it records what the
/// hook asked for, so the test can look at the mappings and the search origin the hook chose.
///
/// This spy is NOT a model of the hook. The hook is the shipped file, and it is what is tested.
namespace TLDRevamp.Fixes
{
public static partial class TerrainHorizon
{
    /// What the hook asked AlignMesh for.
    internal sealed class AlignCall
    {
        public Mesh Mesh;
        public Func<Vector3, Vector3> ToWorld;
        public Func<Vector3, Vector3> WorldToLocal;
        public Vector3d SearchOrigin;
        public double BlendRadius;
    }

    internal static readonly List<AlignCall> Calls = new List<AlignCall>();
    internal static int AlignResult;

    /// The blend width, as the production code computes it.
    internal static double BlendRadius(double spacing) =>
        TerrainHorizonGeometry.BlendRadius(spacing, 38.0, 260.0);

    /// The far grid's vertex spacing, as the production code computes it.
    internal static double FarGridSpacing(newNewRandomMapGen gen)
    {
        if (gen == null) return 38.0;
        return TerrainHorizonGeometry.FarGridSpacing(gen.gridSize, gen.resolution, 38.0);
    }

    /// The correction itself: the spy only records the call.
    internal static int AlignMesh(Mesh mesh, Func<Vector3, Vector3> toWorld, Func<Vector3, Vector3> worldToLocal, Vector3d searchOrigin, double blendRadius)
    {
        Calls.Add(new AlignCall
        {
            Mesh = mesh,
            ToWorld = toWorld,
            WorldToLocal = worldToLocal,
            SearchOrigin = searchOrigin,
            BlendRadius = blendRadius,
        });
        return AlignResult;
    }

    /// Clears what the spy recorded.
    internal static void ResetSpy() { Calls.Clear(); AlignResult = 0; }
}
}

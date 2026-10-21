#pragma warning disable CS0649, CS8981
using System;
using System.Collections.Generic;
using System.Reflection;
using TLDRevamp.Fixes;
using UnityEngine;

/// The behavioural test of the far-grid hook of issue #37.
///
/// The hook under test is the SHIPPED file (src/TLDRevamp/Fixes/TerrainHorizon.TileHook.cs),
/// linked into this project unchanged. Only the external Unity and game types are stubbed, and
/// the other half of the TerrainHorizon partial class is a spy that records the mappings and the
/// search origin the hook chose. The private Harmony postfix is invoked through reflection, so
/// the test runs the hook exactly as the game calls it.
///
/// The rule being proved: the mesh's vertices are local to the TILE object, so both mappings and
/// the search origin must come from the tile's own transform (F.transform) - never from the
/// generator's transform and never from pM.p, which is the tile's noise seed.
static class TerrainTileHookTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static void Near(double a, double b, double tol, string why) { checks++; if (Math.Abs(a - b) > tol) throw new Exception(why + " (" + a + " != " + b + ")"); }

    static int Main()
    {
        PlaceTerrain();
        MoveTerrain();
        FarTerrainScript();
        Transformed();
        WorldOrigin();
        HookRegistration();
        Console.WriteLine("Terrain tile hook: " + checks + " checks passed");
        return 0;
    }

    // ------------------------------------------------------------------ the hook itself
    /// The private Harmony postfix of the far-grid hook, invoked the way the game invokes it.
    static void InvokePostfix(newNewRandomMapGen instance, MeshFilter F, newNewRandomMapGen.MeshToGenerate pM)
    {
        TerrainHorizon.ResetSpy();
        var patch = typeof(TerrainHorizon)
            .GetNestedType("AlignGenerated", BindingFlags.NonPublic | BindingFlags.Public);
        if (patch == null) throw new Exception("the far-grid hook class is missing from the production source");
        var postfix = patch.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (postfix == null) throw new Exception("the far-grid hook has no postfix");
        postfix.Invoke(null, new object[] { instance, F, pM });
    }

    static Transform NewTransform(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        var t = new Transform();
        t.position = position;
        t.rotation = rotation;
        t.localScale = scale;
        return t;
    }

    /// A generator whose own transform is somewhere else entirely: it is a different object from
    /// the tiles it places.
    static newNewRandomMapGen NewGenerator(Transform generatorTransform, float gridSize = 2000f, int resolution = 33)
    {
        var gen = new newNewRandomMapGen();
        gen.nfts = new newFarTerrainScript();
        gen.gridSize = gridSize;
        gen.resolution = resolution;
        gen.transform = generatorTransform;
        return gen;
    }

    static MeshFilter NewTile(Transform tileTransform, int vertexCount)
    {
        var mesh = new Mesh();
        var vertices = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++) vertices[i] = new Vector3(i * 10f, 0f, 0f);
        mesh.vertices = vertices;
        var filter = new MeshFilter();
        filter.mesh = mesh;
        filter.transform = tileTransform;
        return filter;
    }

    // ------------------------------------------------------------------ case 1: PlaceTerrain
    static void PlaceTerrain()
    {
        // newNewRandomMapGen.PlaceTerrain instantiates the tile prefab at
        // transform.TransformPoint(pM.p) and hands the tile's MeshFilter to UpdMesh. The tile is a
        // separate object, so its vertices are local to the TILE, not to the generator.
        var generatorTransform = NewTransform(new Vector3(100f, 0f, 200f), Quaternion.identity, Vector3.one);
        var gen = NewGenerator(generatorTransform);

        var seed = new Vector3(1500f, 0f, -1500f);        // the tile's noise seed, far from the origin
        var tileTransform = NewTransform(generatorTransform.TransformPoint(seed), Quaternion.identity, Vector3.one);
        var filter = NewTile(tileTransform, 16);
        var pM = new newNewRandomMapGen.MeshToGenerate { p = seed, v = new Vector3[0] };

        InvokePostfix(gen, filter, pM);

        Check(TerrainHorizon.Calls.Count == 1, "the hook corrects the tile it is given");
        var call = TerrainHorizon.Calls[0];
        Check(call.Mesh == filter.mesh, "the hook corrects the tile's own mesh");

        // The vertex mapping must use the TILE's transform.
        var local = new Vector3(12f, 3f, -7f);
        var world = call.ToWorld(local);
        var expected = tileTransform.TransformPoint(local);
        Near(world.x, expected.x, 1e-4, "the hook reads a vertex in the tile's own space");
        Near(world.y, expected.y, 1e-4, "the hook reads a vertex in the tile's own space");
        Near(world.z, expected.z, 1e-4, "the hook reads a vertex in the tile's own space");

        // The generator's transform is a different object, so using it would move every vertex.
        var wrong = generatorTransform.TransformPoint(local);
        Check(Math.Abs(world.x - wrong.x) > 1.0 || Math.Abs(world.z - wrong.z) > 1.0,
              "the hook does not read the vertex in the generator's space");

        // The inverse mapping must be the tile's inverse, so the corrected vertex lands back in
        // the tile's own vertex space.
        var back = call.WorldToLocal(world);
        Near(back.x, local.x, 1e-3, "the corrected vertex goes back into the tile's vertex space");
        Near(back.z, local.z, 1e-3, "the corrected vertex goes back into the tile's vertex space");

        // The search origin is the tile's own centre, in global space.
        var origin = call.SearchOrigin;
        Near(origin.x, tileTransform.position.x, 1e-4, "the hook searches around the tile's own centre");
        Near(origin.z, tileTransform.position.z, 1e-4, "the hook searches around the tile's own centre");
        // and not around the generator's, nor around the raw seed
        Check(Math.Abs(origin.x - generatorTransform.position.x) > 1.0,
              "the hook does not search around the generator");
        Check(Math.Abs(origin.x - seed.x) > 1.0 || Math.Abs(origin.z - seed.z) > 1.0,
              "the hook does not search around the noise seed");
    }

    // ------------------------------------------------------------------ case 2: MoveTerrain
    static void MoveTerrain()
    {
        // newNewRandomMapGen.MoveTerrain moves an EXISTING tile to
        // base.transform.TransformPoint(pM.p) and hands its MeshFilter to UpdMesh. The tile keeps
        // its own MeshFilter, so the mapping must still be the tile's.
        var generatorTransform = NewTransform(new Vector3(-300f, 0f, 50f), Quaternion.identity, Vector3.one);
        var gen = NewGenerator(generatorTransform);

        var seed = new Vector3(-2000f, 0f, 2000f);
        var moved = generatorTransform.TransformPoint(seed);
        var tileTransform = NewTransform(moved, Quaternion.identity, Vector3.one);
        var filter = NewTile(tileTransform, 16);
        var pM = new newNewRandomMapGen.MeshToGenerate { p = seed, v = new Vector3[0] };

        InvokePostfix(gen, filter, pM);

        Check(TerrainHorizon.Calls.Count == 1, "the hook corrects the tile it moved");
        var call = TerrainHorizon.Calls[0];
        var local = new Vector3(-3f, 0f, 9f);
        var world = call.ToWorld(local);
        Near(world.x, tileTransform.TransformPoint(local).x, 1e-4, "the moved tile's vertices are read in the tile's own space");
        Near(world.z, tileTransform.TransformPoint(local).z, 1e-4, "the moved tile's vertices are read in the tile's own space");
        Near(call.SearchOrigin.x, moved.x, 1e-4, "the moved tile is searched around its own centre");
        Near(call.SearchOrigin.z, moved.z, 1e-4, "the moved tile is searched around its own centre");
    }

    // ------------------------------------------------------------------ case 3: the far script
    static void FarTerrainScript()
    {
        // newFarTerrainScript.Update hands the MeshFilter of a tile that sits at its own V[i]
        // offset to nrmg.UpdMesh. The tile's local position is the grid offset (not the noise
        // seed), and it is a child of something else, so the mapping must be the tile's.
        var generatorTransform = NewTransform(new Vector3(0f, 0f, 0f), Quaternion.identity, Vector3.one);
        var gen = NewGenerator(generatorTransform);

        // the ninth tile of the far grid: its own offset is the grid step
        float gridSize = 2000f;
        var tileTransform = NewTransform(new Vector3(gridSize, 0f, -gridSize), Quaternion.identity, Vector3.one);
        var filter = NewTile(tileTransform, 16);
        // the noise seed is somewhere else entirely, and must not be used as the tile's position
        var seed = new Vector3(-1234f, 0f, 5678f);
        var pM = new newNewRandomMapGen.MeshToGenerate { p = seed, v = new Vector3[0] };

        InvokePostfix(gen, filter, pM);

        Check(TerrainHorizon.Calls.Count == 1, "the hook corrects the far grid's tile");
        var call = TerrainHorizon.Calls[0];
        var local = new Vector3(5f, 0f, -11f);
        var world = call.ToWorld(local);
        Near(world.x, tileTransform.TransformPoint(local).x, 1e-4, "the far tile's vertices are read in the tile's own space");
        Near(world.z, tileTransform.TransformPoint(local).z, 1e-4, "the far tile's vertices are read in the tile's own space");
        Near(call.SearchOrigin.x, gridSize, 1e-4, "the far tile is searched around its own centre");
        Near(call.SearchOrigin.z, -gridSize, 1e-4, "the far tile is searched around its own centre");
        // the seed is not the tile's position
        Check(Math.Abs(call.SearchOrigin.x - seed.x) > 1.0, "the far tile is not searched around the noise seed");
    }

    // ------------------------------------------------------------------ rotation and scale
    static void Transformed()
    {
        // A tile with a rotation and a scale: both mappings must still be exact inverses of each
        // other, and the search origin must be the tile's own centre.
        var generatorTransform = NewTransform(new Vector3(10f, 0f, 10f), Quaternion.AngleAxis(37f, new Vector3(0f, 1f, 0f)), Vector3.one);
        var gen = NewGenerator(generatorTransform);
        var tileTransform = NewTransform(new Vector3(400f, 7f, -900f), Quaternion.AngleAxis(23f, new Vector3(0f, 1f, 0f)), new Vector3(2f, 1f, 2f));
        var filter = NewTile(tileTransform, 16);
        var pM = new newNewRandomMapGen.MeshToGenerate { p = new Vector3(1f, 0f, 2f), v = new Vector3[0] };

        InvokePostfix(gen, filter, pM);

        Check(TerrainHorizon.Calls.Count == 1, "the hook corrects the transformed tile");
        var call = TerrainHorizon.Calls[0];
        foreach (var local in new[] { new Vector3(12f, 3f, -7f), new Vector3(0f, 0f, 0f), new Vector3(-30f, 1f, 44f) })
        {
            var world = call.ToWorld(local);
            var expected = tileTransform.TransformPoint(local);
            Near(world.x, expected.x, 1e-3, "a rotated and scaled vertex is read in the tile's own space");
            Near(world.y, expected.y, 1e-3, "a rotated and scaled vertex is read in the tile's own space");
            Near(world.z, expected.z, 1e-3, "a rotated and scaled vertex is read in the tile's own space");
            var back = call.WorldToLocal(world);
            Near(back.x, local.x, 1e-2, "the corrected vertex goes back into the tile's vertex space");
            Near(back.z, local.z, 1e-2, "the corrected vertex goes back into the tile's vertex space");
        }
        Near(call.SearchOrigin.x, tileTransform.position.x, 1e-4, "the transformed tile is searched around its own centre");
        Near(call.SearchOrigin.z, tileTransform.position.z, 1e-4, "the transformed tile is searched around its own centre");
        // The blend must be wide enough for the far grid's vertex spacing.
        Check(call.BlendRadius >= 38.0, "the far grid's blend reaches its own vertices");
    }

    // ------------------------------------------------------------------ the world's origin
    static void WorldOrigin()
    {
        // The game moves the world in Unity space when the player strays past 2500 m. The hook's
        // search origin is converted into global space, so a shift must move the origin with the
        // world and not leave it behind.
        mainscript.visszarakva = new Vector3(2500f, 0f, -2500f);
        try
        {
            var generatorTransform = NewTransform(new Vector3(0f, 0f, 0f), Quaternion.identity, Vector3.one);
            var gen = NewGenerator(generatorTransform);
            var tileTransform = NewTransform(new Vector3(300f, 0f, 300f), Quaternion.identity, Vector3.one);
            var filter = NewTile(tileTransform, 16);
            var pM = new newNewRandomMapGen.MeshToGenerate { p = new Vector3(0f, 0f, 0f), v = new Vector3[0] };

            InvokePostfix(gen, filter, pM);

            var call = TerrainHorizon.Calls[0];
            Near(call.SearchOrigin.x, tileTransform.position.x - mainscript.visszarakva.x, 1e-4,
                 "the search origin is in global space, so the world's origin is taken off");
            Near(call.SearchOrigin.z, tileTransform.position.z - mainscript.visszarakva.z, 1e-4,
                 "the search origin is in global space, so the world's origin is taken off");
        }
        finally
        {
            mainscript.visszarakva = Vector3.zero;
        }
    }

    // ------------------------------------------------------------------ the hook is registered
    static void HookRegistration()
    {
        // The hook must still be a Harmony patch of the game's own UpdMesh, or it never runs.
        var patch = typeof(TerrainHorizon).GetNestedType("AlignGenerated", BindingFlags.NonPublic | BindingFlags.Public);
        Check(patch != null, "the far-grid hook is a nested class of the production fix");
        var attrs = patch.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatchAttribute), false);
        Check(attrs.Length == 1, "the far-grid hook carries exactly one Harmony patch attribute");
        var attr = (HarmonyLib.HarmonyPatchAttribute)attrs[0];
        Check(attr.DeclaringType == typeof(newNewRandomMapGen), "the far-grid hook patches the game's own generator");
        Check(attr.MethodName == "UpdMesh", "the far-grid hook patches the game's own UpdMesh");
    }
}

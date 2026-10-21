using System;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// The far-grid hook of issue #37, in a file of its own so a small headless unit project can
    /// compile the *actual* production hook (tools/unit/TerrainTileHookTest.csproj). Only the
    /// external Unity and game types are stubbed there; this file is the shipped one.
    ///
    /// The hook corrects the coarse far-grid tiles the game places around the player. The mesh's
    /// vertices are local to the TILE object, so both mappings (vertex->world and world->vertex)
    /// must come from the tile's own transform, `F.transform`:
    ///
    ///  - newNewRandomMapGen.PlaceTerrain instantiates the tile prefab at
    ///    `transform.TransformPoint(pM.p)` and hands its MeshFilter to UpdMesh,
    ///  - newNewRandomMapGen.MoveTerrain moves an existing tile to
    ///    `base.transform.TransformPoint(pM.p)` and hands its MeshFilter to UpdMesh,
    ///  - newFarTerrainScript.Update hands the MeshFilter of a tile that sits at its own
    ///    `V[i]` offset to `nrmg.UpdMesh`.
    ///
    /// The generator's own transform is a different object, and `pM.p` is the noise seed of the
    /// tile, not a vertex offset: using either of them moves the lookup by a whole grid step, so
    /// the tile would be corrected against a road that belongs to another tile.
    public static partial class TerrainHorizon
    {
        /// The far grid: nine coarse tiles around the player, drawn by the far camera beyond 200 m.
        [HarmonyPatch(typeof(newNewRandomMapGen), nameof(newNewRandomMapGen.UpdMesh))]
        private static class AlignGenerated
        {
            private static void Postfix(newNewRandomMapGen __instance, MeshFilter F, newNewRandomMapGen.MeshToGenerate pM)
            {
                // Only the far generator's own grid: the near grids belong to the game's detailed
                // terrain, which already follows the roads.
                if (__instance == null || __instance.nfts == null || F == null || F.mesh == null || pM == null) return;
                // The tile's own transform: the mesh's vertices are local to the tile object.
                var tile = F.transform;
                // The far grid's vertices are far apart too: the blend has to be wide enough to
                // reach the nearest one, or the road would never be reached at all.
                double spacing = FarGridSpacing(__instance);
                AlignMesh(F.mesh,
                    local => tile.TransformPoint(local),
                    world => tile.InverseTransformPoint(world),
                    // The search origin is the tile's own centre, in global space.
                    mainscript.GlobalFromUnityPos(tile.position),
                    BlendRadius(spacing));
            }
        }
    }
}

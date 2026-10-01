using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// terrainGenScript (non-async2 path) with many players (the host generates around every one):
    ///  - CheckRemove (every 9th frame): for every existing tile, coordsNeeded.Contains — a linear search over all
    ///    players' sample coordinates → tiles × coords, both growing with the player count (quadratic).
    ///  - DoPlace (every 2nd frame): walks every player's coordinates, for each asks every road generator
    ///    RoadsDoneAtPlace (4 chunk lookups) and only then Tile(), whose first action is "already exists → false".
    ///    Nearly all coordinates already have a tile, and coordinates shared by nearby players are visited again.
    /// Exact: CheckRemove with a hash set (same membership as the dictionary of tiles uses; Verify compares with the
    /// original), same result order. DoPlace checks "tile exists" first — RoadsDoneAtPlace has no side effects and
    /// Tile() returns false for an existing tile, so skipping both changes nothing — and skips a repeated coordinate
    /// (it gives the same answer as its first visit, which didn't stop the loop).
    /// Runtime switches: TLDRevamp.Fixes.TerrainPlaceFast.Enabled / .Verify
    [HarmonyPatch]
    public static class TerrainPlaceFast
    {
        public static bool Enabled = true;
        public static bool Verify = false;
        public static long VerifyChecks, VerifyMismatches, Removes, Places;

        private static readonly AccessTools.FieldRef<terrainGenScript, List<Vector3d>> CoordsNeeded = AccessTools.FieldRefAccess<terrainGenScript, List<Vector3d>>("coordsNeeded");
        private static readonly AccessTools.FieldRef<terrainGenScript, List<Vector3d>> CoordsToRemove = AccessTools.FieldRefAccess<terrainGenScript, List<Vector3d>>("coordsToRemove");
        private static readonly Func<terrainGenScript, Vector3d, bool, bool, bool> Tile =
            AccessTools.MethodDelegate<Func<terrainGenScript, Vector3d, bool, bool, bool>>(
                AccessTools.Method(typeof(terrainGenScript), "Tile", new[] { typeof(Vector3d), typeof(bool), typeof(bool) }));

        private static readonly HashSet<Vector3d> Needed = new HashSet<Vector3d>();
        private static readonly HashSet<Vector3d> Seen = new HashSet<Vector3d>();

        [HarmonyPatch(typeof(terrainGenScript), "CheckRemove")]
        [HarmonyPrefix]
        private static bool CheckRemove(terrainGenScript __instance)
        {
            if (!Enabled) return true;
            var needed = CoordsNeeded(__instance);
            var remove = CoordsToRemove(__instance);
            Needed.Clear();
            for (int i = 0; i < needed.Count; i++) Needed.Add(needed[i]);
            remove.Clear();
            foreach (var kv in __instance.terrains)
                if (!Needed.Contains(kv.Key)) remove.Add(kv.Key);
            Removes++;
            if (Verify)
            {
                VerifyChecks++;
                int k = 0;
                bool bad = false;
                foreach (var kv in __instance.terrains)
                    if (!needed.Contains(kv.Key) && (k >= remove.Count || !remove[k++].Equals(kv.Key))) { bad = true; break; }
                if (bad || k != remove.Count) VerifyMismatches++;
            }
            return false;
        }

        [HarmonyPatch(typeof(terrainGenScript), "DoPlace")]
        [HarmonyPrefix]
        private static bool DoPlace(terrainGenScript __instance)
        {
            if (!Enabled) return true;
            var gen = __instance;
            var needed = CoordsNeeded(gen);
            var roadGens = gen.roadAffectsThis ? gen.setting.mapSetting.roadGens : null;
            Seen.Clear();
            Places++;
            for (int i = 0; i < needed.Count; i++)
            {
                var c = needed[i];
                if (gen.terrains.ContainsKey(c) || !Seen.Add(c)) continue;
                bool skip = false;
                if (roadGens != null)
                    for (int j = 0; j < roadGens.Count; j++)
                        if (!roadGens[j].RoadsDoneAtPlace(c)) { skip = true; break; }
                if (!skip && Tile(gen, c, false, gen.mapSetting.useAsync)) break;
            }
            return false;
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"removes\":" + Removes + ",\"places\":" + Places +
            ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches + "}";
    }
}

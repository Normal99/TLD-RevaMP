using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// The player's own model (its outfit's characters) gets the shader value _Dist set before every camera render and
    /// reset after it (setsubshaderplayercamscript.OnPreRender/OnPostRender → playermodeloutfitscript.SetDistance /
    /// ReSetDistance). Both read `renderer.materials` per renderer, which allocates a new array on every call, and pass
    /// the property by name. Measured standing at spawn: 256 KB/s of garbage, ~60% of the game's whole garbage there.
    ///
    /// Exact copy of both methods: Renderer.GetMaterials into a reused list (the same instantiated materials, no array),
    /// and the property ID looked up once (SetFloat(string) does the same lookup every call).
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.OutfitDistanceNoAlloc.Enabled / .Verify (every call also reads
    /// `materials` and compares them, element by element, with what GetMaterials returned)
    [HarmonyPatch]
    public static class OutfitDistanceNoAlloc
    {
        public static bool Enabled = true;
        public static bool Verify = false;
        public static long Calls, VerifyChecks, VerifyMismatches;

        private static readonly int DistId = Shader.PropertyToID("_Dist");
        private static readonly List<Material> Mats = new List<Material>();

        [HarmonyPrefix, HarmonyPatch(typeof(playermodeloutfitscript), nameof(playermodeloutfitscript.SetDistance))]
        private static bool Set(playermodeloutfitscript __instance)
        {
            if (!Enabled) return true;
            Apply(__instance, mainscript.IsVR() ? __instance.VRdistance : __instance.distance);
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(playermodeloutfitscript), nameof(playermodeloutfitscript.ReSetDistance))]
        private static bool Reset(playermodeloutfitscript __instance)
        {
            if (!Enabled) return true;
            Apply(__instance, 0f);
            return false;
        }

        private static void Apply(playermodeloutfitscript o, float value)
        {
            Calls++;
            var characters = o.characters;
            for (int i = 0; i < characters.Length; i++)
            {
                if (!(characters[i] != null)) continue;
                var renderers = characters[i].ch.renderers;
                for (int j = 0; j < renderers.Length; j++)
                {
                    renderers[j].GetMaterials(Mats);
                    if (Verify) Check(renderers[j]);
                    for (int k = 0; k < Mats.Count; k++) Mats[k].SetFloat(DistId, value);
                }
            }
            Mats.Clear();
        }

        private static void Check(Renderer r)
        {
            VerifyChecks++;
            var arr = r.materials;
            bool same = arr.Length == Mats.Count;
            for (int k = 0; same && k < arr.Length; k++) same = ReferenceEquals(arr[k], Mats[k]);
            if (same) return;
            if (++VerifyMismatches <= 5) Plugin.Log.LogWarning($"OutfitDistanceNoAlloc: {r.name} materials {arr.Length} vs GetMaterials {Mats.Count}");
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"calls\":" + Calls + ",\"verifyChecks\":" + VerifyChecks +
            ",\"verifyMismatches\":" + VerifyMismatches + "}";
    }
}

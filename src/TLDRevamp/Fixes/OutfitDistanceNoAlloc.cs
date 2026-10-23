using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// The player's own model (its outfit's characters) gets the shader value _Dist set before every camera render and
    /// reset after it (setsubshaderplayercamscript.OnPreRender/OnPostRender → playermodeloutfitscript.SetDistance /
    /// ReSetDistance). Both read `renderer.materials` per renderer, which allocates a new array on every call, and pass
    /// the property by name. Measured standing at spawn: 256 KB/s of garbage, ~60% of the game's whole garbage there.
    ///
    /// The game's own two methods with two calls swapped at runtime (GamePatch): `materials` becomes
    /// Renderer.GetMaterials into an array kept per renderer (the same instantiated materials, no new array), and
    /// SetFloat("_Dist") uses the property ID looked up once (SetFloat(string) does the same lookup every call).
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.OutfitDistanceNoAlloc.Enabled / .Verify (every call also reads
    /// `materials` and compares them, element by element, with what GetMaterials returned)
    [HarmonyPatch]
    public static class OutfitDistanceNoAlloc
    {
        public static bool Enabled = true;
        public static bool Verify = false;
        public static long Calls, VerifyChecks, VerifyMismatches;
        public static int Patched;

        private static readonly int DistId = Shader.PropertyToID("_Dist");
        private static readonly List<Material> Mats = new List<Material>();
        private static readonly ConditionalWeakTable<Renderer, Material[]> Arrays = new ConditionalWeakTable<Renderer, Material[]>();

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(playermodeloutfitscript), nameof(playermodeloutfitscript.SetDistance));
            yield return AccessTools.Method(typeof(playermodeloutfitscript), nameof(playermodeloutfitscript.ReSetDistance));
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var p = new GamePatch(nameof(OutfitDistanceNoAlloc) + "." + original.Name, instructions);
            p.Calls(AccessTools.PropertyGetter(typeof(Renderer), nameof(Renderer.materials)), AccessTools.Method(typeof(OutfitDistanceNoAlloc), nameof(Materials)), 1);
            p.Calls(AccessTools.Method(typeof(Material), nameof(Material.SetFloat), new[] { typeof(string), typeof(float) }), AccessTools.Method(typeof(OutfitDistanceNoAlloc), nameof(SetFloat)), 1);
            int ok = 0;
            var code = p.Result(ref ok);
            Patched += ok;
            return code;
        }

        [HarmonyPrefix]
        private static void Count() { if (Enabled) Calls++; }

        /// `renderer.materials` without a new array: the renderer's own kept array, refilled by GetMaterials.
        public static Material[] Materials(Renderer r)
        {
            if (!Enabled) return r.materials;
            r.GetMaterials(Mats);
            if (!Arrays.TryGetValue(r, out var arr) || arr.Length != Mats.Count)
            {
                arr = new Material[Mats.Count];
                Arrays.Remove(r); Arrays.Add(r, arr);
            }
            for (int k = 0; k < arr.Length; k++) arr[k] = Mats[k];
            Mats.Clear();
            if (Verify) Check(r, arr);
            return arr;
        }

        public static void SetFloat(Material m, string name, float value)
        {
            if (Enabled && name == "_Dist") m.SetFloat(DistId, value);
            else m.SetFloat(name, value);
        }

        private static void Check(Renderer r, Material[] ours)
        {
            VerifyChecks++;
            var arr = r.materials;
            bool same = arr.Length == ours.Length;
            for (int k = 0; same && k < arr.Length; k++) same = ReferenceEquals(arr[k], ours[k]);
            if (same) return;
            if (++VerifyMismatches <= 5) Plugin.Log.LogWarning($"OutfitDistanceNoAlloc: {r.name} materials {arr.Length} vs GetMaterials {ours.Length}");
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"patched\":" + Patched + ",\"calls\":" + Calls + ",\"verifyChecks\":" + VerifyChecks +
            ",\"verifyMismatches\":" + VerifyMismatches + "}";
    }
}

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        [HarmonyLib.HarmonyPatch(typeof(attachablescript), nameof(attachablescript.FallOFf))]
        private static class NoProxyFallOff
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(attachablescript __instance) { if (!PartIsProxy(__instance)) return true; SuppressedFallOff++; return false; }
        }
    }
}

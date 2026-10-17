using System;
using HarmonyLib;

namespace TLDRevamp.Fixes
{
    /// A fuel tank going off (tankscript.TryExplode) slices its model into flying pieces (randomSlice.Slicc → SSSLiceee per
    /// renderer → MeshSlicer.NewSlice) before it destroys the tank. On some meshes the slicer throws (the Car01's radiator:
    /// IndexOutOfRangeException in MeshSlicer.SliceM, every time, single player too) and the exception ends TryExplode
    /// there: the explosion happened, but the radiator never goes and can't explode again (alreadyExploded).
    ///
    /// A renderer the slicer can't cut is left uncut: the exception is caught there, the other renderers are sliced and
    /// TryExplode goes on to destroy the tank as the game means to. Logged once per mesh name.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.SliceGuard.Enabled
    [HarmonyPatch(typeof(randomSlice), nameof(randomSlice.SSSLiceee))]
    public static class SliceGuard
    {
        public static bool Enabled = true;
        public static long Caught;
        private static readonly System.Collections.Generic.HashSet<string> _logged = new System.Collections.Generic.HashSet<string>();

        [HarmonyFinalizer]
        private static Exception Finalizer(randomSlice __instance, Exception __exception)
        {
            if (__exception == null || !Enabled) return __exception;
            Caught++;
            string name = __instance != null ? __instance.name : "?";
            if (_logged.Add(name)) Plugin.Log.LogWarning($"slice failed, left whole: {name} ({__exception.GetType().Name}: {__exception.Message})");
            return null;
        }
    }
}

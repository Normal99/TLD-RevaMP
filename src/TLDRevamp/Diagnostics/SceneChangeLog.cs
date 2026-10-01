using System;
using HarmonyLib;

namespace TLDRevamp.Diagnostics
{
    /// Logs every scene load the game requests (back to menu, restart, new game) with the caller's stack and the
    /// player's death state, so unexplained returns to the main menu can be traced.
    [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.LoadScene))]
    public static class SceneChangeLog
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            string died = "?";
            try { died = mainscript.s != null ? mainscript.s.died.ToString() : "no mainscript"; } catch { }
            Plugin.Log.LogWarning("Scene load requested. player died=" + died +
                                  " god=" + SafeGod() + "\n" + Environment.StackTrace);
        }

        private static string SafeGod()
        {
            try { return kaposztaleves.s != null ? kaposztaleves.s.settings.god.ToString() : "?"; } catch { return "?"; }
        }
    }
}

using UnityEngine;

namespace TLDRevamp.Net
{
    /// The game branches on syncScript.IsConnected() — "the official MP is on" — for rules that must differ in
    /// multiplayer. Our sessions replace the official MP, so IsConnected() is false and the single-player rule runs.
    /// Where that rule breaks a shared world, the multiplayer rule is applied here at the exact exit point (faking
    /// IsConnected would also switch on the official MP's network sends everywhere).
    public static class SessionRules
    {
        static bool InSession => Entities.InSession;
        public static long Respawns, PauseKeptRunning;

        /// Death: single player goes back to the main menu (LoadMainMenu(false) — its only caller is the death screen
        /// in mainscript.Update); the official MP respawns the player in the world (fpscontroller.Respawn: revive,
        /// drop what they carried, 40–60 m away). In a session the menu would end the session — for the host, for
        /// everyone.
        [HarmonyLib.HarmonyPatch(typeof(menuhandler), nameof(menuhandler.LoadMainMenu))]
        private static class RespawnInSession
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(bool _noAutoContinue)
            {
                if (!InSession || _noAutoContinue || mainscript.s == null || !mainscript.s.died || mainscript.s.player == null) return true;
                mainscript.s.player.Respawn();
                Respawns++;
                Plugin.Log.LogInfo("session: player died — respawned in the world (the official MP's rule) instead of the main menu");
                return false;
            }
        }

        /// Pause menu: single player stops time (timeScale 0, physics by script); the official MP keeps the world
        /// running ("died || IsConnected → timeScale 1"). In a session a paused player's machine would freeze
        /// everything it simulates — its car stops dead on every other screen.
        [HarmonyLib.HarmonyPatch(typeof(mainscript), "Update")]
        private static class KeepRunningWhenPaused
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(mainscript __instance)
            {
                if (!InSession || !__instance.pauseMenuOpen || DataFromMenuScript.s == null || DataFromMenuScript.s.load) return;
                if (Time.timeScale != 1f || Physics.simulationMode != SimulationMode.FixedUpdate)
                {
                    Time.timeScale = 1f;
                    Physics.simulationMode = SimulationMode.FixedUpdate;
                    PauseKeptRunning++;
                }
            }
        }
    }
}

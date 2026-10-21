using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Cinematic
{
    /// The moments when the cinematic camera must put itself away.
    ///
    /// The camera is a local view of the world; the world can end under it without warning. Each hook below
    /// takes the camera down and puts everything back. They are all idempotent: the teardown can run twice (a
    /// death and then a scene change), and the second run changes nothing.
    ///
    /// The camera's own per-frame check (CinematicCamera.Tick) catches most of these anyway — it refuses to run
    /// while the player is dead, while the world is loading, while a menu is open and while the scene is being
    /// torn down. These hooks make the teardown happen *at* the event rather than a frame later, so a camera never
    /// draws one frame from a world that is being replaced.
    public static class CinematicLifecycle
    {
        /// How many times the camera was taken down by the game rather than by the player.
        public static long ForcedTeardowns;

        internal static void Forced(string why)
        {
            if (!CinematicCamera.Active) return;
            ForcedTeardowns++;
            CinematicCamera.SetOff(why);
        }

        /// The player died: the game's death screen moves the camera rig to the corpse, so the cinematic camera has
        /// nothing to film and would be left pointing at a world it is about to leave.
        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Death))]
        private static class OnDeath
        {
            [HarmonyPrefix]
            private static void Prefix() => Forced("death");
        }

        /// The player is back: the world is new, the camera's old position means nothing.
        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Respawn))]
        private static class OnRespawn
        {
            [HarmonyPrefix]
            private static void Prefix() => Forced("respawn");
        }

        /// The world is being replaced (a scene load, a session stopping): everything the camera holds is about to
        /// be destroyed.
        [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.LoadScene))]
        private static class OnSceneLoad
        {
            [HarmonyPrefix]
            private static void Prefix() => Forced("scene load");
        }

        /// The scene is being torn down (a world swap, a return to the menu): everything the camera holds is about
        /// to be destroyed.
        [HarmonyPatch(typeof(mainscript), nameof(mainscript.PressedEscape))]
        private static class OnEscape
        {
            [HarmonyPrefix]
            private static void Prefix(bool _screenshot, bool _mainMenu)
            {
                // Only a real menu (not a screenshot) ends the world.
                if (!_mainMenu) return;
                Forced("main menu");
            }
        }
    }
}

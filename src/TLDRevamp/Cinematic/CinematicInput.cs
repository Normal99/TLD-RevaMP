using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Cinematic
{
    /// Keeps the player's own movement out of the way while the cinematic camera is driving.
    ///
    /// The game's movement keys (W A S D) are the same keys the camera uses to fly. If they reached the player,
    /// the character would walk while the camera flew — and the character's position is what the network reports,
    /// so the other players would see this one walking away mid-take. That is exactly the side effect this
    /// feature must not have.
    ///
    /// So while the camera is on, the movement axes are taken from the camera instead of the keyboard. The camera
    /// reads the same values the player would have produced, and the player reads zeroes. Nothing else about the
    /// game's input changes: looking, using things, the menus and the car's own controls are untouched.
    ///
    /// The car's throttle and brakes are separate axes (inputscript.i.throttle / .brake), so driving a car while
    /// filming it still works.
    public static class CinematicInput
    {
        /// Whether the movement axes are currently taken over.
        public static bool Active;

        /// How many frames the takeover has been in place, for the tool to read back.
        public static long Frames;

        /// The values the player would have had this frame, kept so the camera can use them.
        public static float Forward, Backward, Left, Right;

        /// The camera's own speed multiplier: Shift to hurry, Ctrl to creep.
        public static float SpeedFactor = 1f;

        /// Whether the player is sitting in a car: the car's own controls are separate and stay untouched.
        public static bool InCar;

        /// The camera's speed factor for the current frame, from the modifier keys.
        public static float SpeedFactorFor(bool run, bool duck) => CinematicInputPolicy.SpeedFactor(run, duck);
    }

    /// The takeover itself: the player's movement axes become zero while the camera is on.
    ///
    /// The patch is a **prefix on the player's update**, not on the input script's. Unity does not promise the
    /// order of two components' Update calls, and the input script and the player are two separate components:
    /// patching the input script could zero the axes either before or after the player had read them, and
    /// patching the player's update as a postfix would be after the player had already moved.
    ///
    /// A prefix on the player's update runs after the input script has filled the axes for this frame (the input
    /// script is a separate component that has already run) but before the player moves. That is the one place
    /// where the values are known and the movement has not happened yet.
    ///
    /// If the input script has not run yet this frame, the axes are the previous frame's values: one frame of
    /// stale input is harmless (it is a frame of walking at the old speed, and the values are still bounded), and
    /// the camera reads the same values, so the camera and the player always agree.
    [HarmonyPatch(typeof(fpscontroller), "Update")]
    public static class CinematicInputPatch
    {
        [HarmonyPrefix]
        private static void Prefix(fpscontroller __instance)
        {
            if (!CinematicCamera.Active) { CinematicInput.Active = false; return; }
            // While a replay plays, the replay viewer has already taken every input from the player (and handed the
            // movement axes to the camera) right after the input script ran: nothing is left to take here.
            if (Replay.ReplayViewer.Active) { CinematicInput.Active = true; CinematicInput.Frames++; return; }
            var input = __instance != null ? __instance.input : null;
            if (input == null) return;
            // The values the player had this frame, kept for the camera.
            var cameraAxes = CinematicInputPolicy.ForCamera(input.forward, input.backward, input.left, input.right);
            CinematicInput.Forward = cameraAxes.Forward;
            CinematicInput.Backward = cameraAxes.Backward;
            CinematicInput.Left = cameraAxes.Left;
            CinematicInput.Right = cameraAxes.Right;
            CinematicInput.SpeedFactor = CinematicInput.SpeedFactorFor(input.run, input.duck);
            try { CinematicInput.InCar = __instance.Car != null; }
            catch { CinematicInput.InCar = false; }
            // And now the player's own movement is taken away: the camera has those keys.
            var playerAxes = CinematicInputPolicy.ForPlayer(input.forward, input.backward, input.left, input.right);
            input.forward = playerAxes.Forward;
            input.backward = playerAxes.Backward;
            input.left = playerAxes.Left;
            input.right = playerAxes.Right;
            CinematicInput.Active = true;
            CinematicInput.Frames++;
        }
    }
}

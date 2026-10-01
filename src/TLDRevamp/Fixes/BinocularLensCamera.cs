using HarmonyLib;

namespace TLDRevamp.Fixes
{
    /// tavcsoscript (binoculars) keeps a camera (CamR) rendering the view in front of the binoculars into a small texture
    /// shown on the lens renderers (R), every frame, whenever the binoculars aren't being looked through, even when
    /// they're nowhere on screen (e.g. lying in a house 150 m away). Measured: switching CamR off +2–3% FPS in the car park.
    ///
    /// After the game's Update decides the camera should be on, keep it off while no lens renderer is visible. When a
    /// lens becomes visible the camera resumes on the next frame (at most one frame of an older lens image).
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.BinocularLensCamera.Enabled
    [HarmonyPatch(typeof(tavcsoscript), "Update")]
    public static class BinocularLensCamera
    {
        public static bool Enabled = true;
        public static long Suppressed;

        [HarmonyPostfix]
        private static void Postfix(tavcsoscript __instance)
        {
            var t = __instance;
            if (t.CamR == null) return;
            if (!Enabled || t.R == null) { t.CamR.enabled = true; return; }
            if (!t.CamR.gameObject.activeSelf) return; // the game has it off (looking through / paused)
            // Toggle only the Camera component (cheap flag). The game re-activates the GameObject every frame, so
            // deactivating the object would flip it on and off each frame.
            bool lensVisible = false;
            for (int i = 0; i < t.R.Length && !lensVisible; i++)
                lensVisible = t.R[i] != null && t.R[i].isVisible;
            if (t.CamR.enabled != lensVisible) t.CamR.enabled = lensVisible;
            if (!lensVisible) Suppressed++;
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"suppressed\":" + Suppressed + "}";
    }
}

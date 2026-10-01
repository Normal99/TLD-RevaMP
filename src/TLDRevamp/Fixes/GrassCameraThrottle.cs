using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// The StixGames grass shader uses a camera 20 m above the player looking down (mainscript.grassCam, "GrassCamera") to
    /// render grass displacers (what bends the grass) into a 256×256 texture every frame. Measured (car park): switching it
    /// off entirely took main-thread rendering 6.6 → 4.7 ms (+12% FPS). Hypothesis: the cost is the camera's per-frame scene pass.
    ///
    /// Throttle (NOT bit-exact; visual trade-off to be judged by the user): render the displacement texture only every
    /// EveryN frames (manual Camera.Render), and optionally clamp its far plane. Grass bending updates at FPS/EveryN.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.GrassCameraThrottle.Enabled / .EveryN / .FarClip (0 = keep)
    public static class GrassCameraThrottle
    {
        /// On, every 3rd frame, since 2026-09-25: car park +9% FPS; user compared every frame / 2nd / 3rd live while walking
        /// and driving through grass: "all of them looked the same".
        public static bool Enabled = true;
        public static int EveryN = 3;
        public static float FarClip = 0f;

        public static long Rendered, Skipped;

        private static Camera _cam;
        private static float _origFar;
        private static bool _taken; // we disabled the camera and render it manually

        /// Called once per frame from the mod's runner (LateUpdate timing is not needed: Render() is immediate).
        public static void Tick()
        {
            var gc = mainscript.s != null ? mainscript.s.grassCam : null;
            if (gc == null) { Release(); return; }
            if (_cam == null || _cam.gameObject != gc.gameObject)
            {
                Release();
                _cam = gc.GetComponent<Camera>();
                if (_cam == null) return;
                _origFar = _cam.farClipPlane;
            }
            if (!Enabled || !gc.gameObject.activeInHierarchy) { Release(); return; }

            if (!_taken) { _cam.enabled = false; _taken = true; }
            _cam.farClipPlane = FarClip > 0f ? FarClip : _origFar;
            if (Time.frameCount % Mathf.Max(1, EveryN) == 0)
            {
                _cam.Render();
                Rendered++;
            }
            else Skipped++;
        }

        private static void Release()
        {
            if (_cam != null && _taken)
            {
                _cam.enabled = true;
                _cam.farClipPlane = _origFar;
            }
            _taken = false;
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"everyN\":" + EveryN + ",\"farClip\":" + FarClip +
            ",\"rendered\":" + Rendered + ",\"skipped\":" + Skipped + "}";
    }
}

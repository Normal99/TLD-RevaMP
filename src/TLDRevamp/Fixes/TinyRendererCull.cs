using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Cars and items have no LODGroups: every part (door, bolt, gauge, interior piece) is its own renderer. Measured: 16
    /// parked cars in view add ~2,500 visible renderers (~160 per car) and ~5.5 ms/frame of main-thread rendering.
    ///
    /// Parts whose on-screen size is below ThresholdPx are marked Renderer.forceRenderingOff (a separate flag from
    /// Renderer.enabled, so the game's own show/hide logic is untouched), and drawn again once they're big enough. Only
    /// renderers under movable objects (cars, items/parts with tosaveitemscript) that aren't in an LODGroup. NOT bit-exact:
    /// sub-threshold parts (at 1–2 px essentially invisible) are skipped. Needs a visual check.
    ///
    /// Work is spread: new objects arrive via the tosaveitemscript.FStart hook (registered RegisterPerFrame per frame, one
    /// initial scan when first enabled); PerFrame renderers re-evaluated per frame (round robin). No periodic scene scans
    /// (they caused 99th-percentile spikes).
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.TinyRendererCull.Enabled / .ThresholdPx / .PerFrame
    [HarmonyPatch]
    public static class TinyRendererCull
    {
        /// Both FStart overloads (spawned items: FStart(); loaded items: FStart(uint key)). A name-only patch was
        /// ambiguous and silently failed in v0.16.2 (only the one-time initial scan registered anything).
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(tosaveitemscript), nameof(tosaveitemscript.FStart), new System.Type[0]);
            yield return AccessTools.Method(typeof(tosaveitemscript), nameof(tosaveitemscript.FStart), new[] { typeof(uint) });
        }

        /// Objects waiting to be registered (from the FStart hook), processed a few per frame to avoid spikes.
        private static readonly Queue<GameObject> Pending = new Queue<GameObject>();
        public static int RegisterPerFrame = 8;
        private static bool _initialScanDone;

        [HarmonyPostfix]
        private static void OnItemStarted(tosaveitemscript __instance)
        {
            // Every spawned or loaded item/part/car calls FStart: pick it up here instead of scanning the scene.
            // The item itself, not transform.root: at FStart items are often still parented (to a house, a POI) and
            // unparent a frame later (tosaveitemscript.NextFrameUnparent). Queuing the root registered only what was
            // under it at registration time and then skipped every later item with the same root (v0.16.4: the
            // start house's items were never tracked after a new world).
            if (__instance != null) Pending.Enqueue(__instance.gameObject);
        }

        /// On at 4 px since 2026-09-25: car park facing 16 cars +12% FPS (48.6→54.5), p99 slightly better; user checked
        /// live (A off / B 2 px / C 4 px) walking around and approaching: "the cars still look well from a distance".
        public static bool Enabled = true;
        public static float ThresholdPx = 4f;
        public static int PerFrame = 800;


        public static int Tracked, Hidden;

        /// Re-check a part only when it could have crossed its threshold distance: a part at distance d with threshold
        /// distance d* = (pixels per metre at 1 m) × size / ThresholdPx can't change state before camera and part have
        /// closed |d − d*| at relative speed VMaxMs, so its next check is |d − d*| / VMaxMs later (at most MaxWaitS).
        /// Everything is re-checked at once when the camera jumps (teleport, world rebase) or the view's scale changes
        /// (zoom, resolution, threshold). Spawn, 744 parts: 0.43 ms/frame re-checking 800 parts every frame.
        /// false = every part round robin, PerFrame per frame (before v0.69.2).
        public static bool Adaptive = true;
        public static float VMaxMs = 120f, MaxWaitS = 0.5f;
        public static long Checks, Frames;   // parts evaluated / frames (bridge: Checks / Frames = parts per frame)

        private static readonly List<Renderer> Rends = new List<Renderer>();
        private static readonly List<float> Radius = new List<float>(); // world-space bounding radius at registration
        private static readonly List<float> Due = new List<float>();    // Adaptive: realtime of the part's next check
        private static Vector3 _lastCamPos;
        private static float _lastK = -1f, _lastNow;
        private static readonly HashSet<int> KnownRoots = new HashSet<int>(); // registered objects
        private static readonly HashSet<int> KnownRends = new HashSet<int>(); // a part is inside its car's and its own subtree
        private static int _sceneHandle = -1;
        private static int _cursor;

        private static bool _wasEnabled;

        /// Set while a replay plays (Replay.ReplayViewer): the replay hides the game's renderers with the same flag, so
        /// the culler steps aside until the replay has given them back, then re-checks every part.
        public static bool Paused { get; private set; }
        public static void Pause(bool on)
        {
            if (on == Paused) return;
            Paused = on;
            if (on) { RestoreAll(); _wasEnabled = false; }
            else for (int i = 0; i < Due.Count; i++) Due[i] = 0f;
        }

        public static void Tick()
        {
            if (Paused) return;
            if (!Enabled)
            {
                if (_wasEnabled) RestoreAll();
                _wasEnabled = false;
                return;
            }
            _wasEnabled = true;
            if (mainscript.s == null || mainscript.s.player == null) return;
            // The game tags no camera "MainCamera" (Camera.main is null); use the player's world camera
            var cam = mainscript.s.player.Cam != null ? mainscript.s.player.Cam : mainscript.s.player.mainCam;
            if (cam == null) return;

            // New world / scene reload: start over with one scan (objects from before the load are gone)
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scene != _sceneHandle) { _sceneHandle = scene; ResetTracking(); }
            if (!_initialScanDone) { InitialScan(); _initialScanDone = true; } // once per scene, when enabled
            for (int q = 0; q < RegisterPerFrame && Pending.Count > 0; q++)
            {
                var root = Pending.Dequeue();
                if (root != null) Register(root);
            }
            Tracked = Rends.Count;
            if (Rends.Count == 0) return;

            Vector3 camPos = cam.transform.position;
            // pixels per metre at distance 1: screenHeight / (2 tan(fov/2))
            float k = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            Frames++;
            if (Adaptive) { TickAdaptive(camPos, k); return; }
            _lastK = -1f;   // switching Adaptive on later starts with every part due
            int n = Mathf.Min(PerFrame, Rends.Count);
            Checks += n;
            for (int i = 0; i < n; i++)
            {
                if (_cursor >= Rends.Count) _cursor = 0;
                var r = Rends[_cursor];
                if (r == null) { RemoveAt(_cursor); continue; }
                float dist = Vector3.Distance(camPos, r.bounds.center);
                float px = dist > 0.01f ? Radius[_cursor] * 2f * k / dist : float.MaxValue;
                bool hide = px < ThresholdPx;
                if (r.forceRenderingOff != hide)
                {
                    r.forceRenderingOff = hide;
                    Hidden += hide ? 1 : -1;
                }
                _cursor++;
            }
        }

        private static void TickAdaptive(Vector3 camPos, float k)
        {
            float now = Time.realtimeSinceStartup;
            float kk = k / ThresholdPx;   // d* = Radius × 2 × kk
            // A jump the speed bound doesn't cover, or a different view scale: every part is due now
            float moved = (camPos - _lastCamPos).magnitude;
            if (_lastK < 0f || Mathf.Abs(kk - _lastK) > _lastK * 0.001f || moved > VMaxMs * (now - _lastNow) + 1f)
                for (int i = 0; i < Due.Count; i++) Due[i] = 0f;
            _lastK = kk; _lastCamPos = camPos; _lastNow = now;
            int count = Rends.Count, done = 0;
            for (int seen = 0; seen < count && done < PerFrame; seen++)
            {
                if (_cursor >= Rends.Count) _cursor = 0;
                if (Due[_cursor] > now) { _cursor++; continue; }
                var r = Rends[_cursor];
                if (r == null) { RemoveAt(_cursor); continue; }
                done++;
                float dist = Vector3.Distance(camPos, r.bounds.center);
                float dStar = Radius[_cursor] * 2f * kk;
                bool hide = dist > 0.01f && dist > dStar;   // same test as px < ThresholdPx
                if (r.forceRenderingOff != hide)
                {
                    r.forceRenderingOff = hide;
                    Hidden += hide ? 1 : -1;
                }
                Due[_cursor] = now + Mathf.Min(MaxWaitS, Mathf.Abs(dist - dStar) / VMaxMs);
                _cursor++;
            }
            Checks += done;
        }

        /// Test (bridge `call TLDRevamp.Fixes.TinyRendererCull.VerifyNow`): every tracked part's state against the
        /// exact test right now, changing nothing. Off by up to one round robin pass at most without Adaptive.
        public static string VerifyNow()
        {
            var cam = mainscript.s?.player == null ? null : mainscript.s.player.Cam != null ? mainscript.s.player.Cam : mainscript.s.player.mainCam;
            if (cam == null) return "{\"error\":\"no camera\"}";
            Vector3 camPos = cam.transform.position;
            float k = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            int n = 0, wrongHidden = 0, wrongShown = 0;
            float worstPx = 0f;
            for (int i = 0; i < Rends.Count; i++)
            {
                var r = Rends[i];
                if (r == null) continue;
                n++;
                float dist = Vector3.Distance(camPos, r.bounds.center);
                float px = dist > 0.01f ? Radius[i] * 2f * k / dist : float.MaxValue;
                bool hide = px < ThresholdPx;
                if (r.forceRenderingOff == hide) continue;
                if (r.forceRenderingOff) { wrongHidden++; if (px > worstPx) worstPx = px; } else wrongShown++;
            }
            return "{\"parts\":" + n + ",\"wrongHidden\":" + wrongHidden + ",\"wrongShown\":" + wrongShown + ",\"worstHiddenPx\":" +
                   worstPx.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        /// Objects that already existed before the feature was switched on: queued (registered gradually), found once.
        private static void InitialScan()
        {
            foreach (var t in Object.FindObjectsOfType<tosaveitemscript>()) Pending.Enqueue(t.gameObject);
            foreach (var c in Object.FindObjectsOfType<carscript>()) Pending.Enqueue(c.gameObject);
        }

        private static void Register(GameObject root)
        {
            if (!KnownRoots.Add(root.GetInstanceID())) return;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || !KnownRends.Add(r.GetInstanceID()) || r.GetComponentInParent<LODGroup>() != null) continue;
                Rends.Add(r);
                Radius.Add(r.bounds.extents.magnitude);
                Due.Add(0f);
            }
        }

        private static void RemoveAt(int i)
        {
            int last = Rends.Count - 1;
            Rends[i] = Rends[last]; Radius[i] = Radius[last]; Due[i] = Due[last];
            Rends.RemoveAt(last); Radius.RemoveAt(last); Due.RemoveAt(last);
        }

        private static void ResetTracking()
        {
            RestoreAll();
            Rends.Clear(); Radius.Clear(); Due.Clear(); KnownRoots.Clear(); KnownRends.Clear();
            _cursor = 0;
            _initialScanDone = false;
        }

        private static void RestoreAll()
        {
            foreach (var r in Rends) if (r != null && r.forceRenderingOff) r.forceRenderingOff = false;
            Hidden = 0;
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"thresholdPx\":" + ThresholdPx + ",\"tracked\":" + Rends.Count +
            ",\"pending\":" + Pending.Count +
            ",\"hidden\":" + Hidden + ",\"roots\":" + KnownRoots.Count + ",\"adaptive\":" + (Adaptive ? "true" : "false") +
            ",\"checks\":" + Checks + ",\"frames\":" + Frames + "}";
    }
}

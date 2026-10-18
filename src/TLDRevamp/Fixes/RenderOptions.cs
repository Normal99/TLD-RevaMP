using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace TLDRevamp.Fixes
{
    /// Opt-in rendering trade-offs (Revamp tab, "Visual trade-offs", off unless the player turns them on). They change
    /// what you see; measured at the start town (tools/rendersweep.py, fresh seeded world, each against the baselines
    /// before and after it, 2 windows each):
    ///
    ///  - ShadowCascades: the sun's shadow map split into fewer cascades (the game uses 4). 2: +2.1/+2.9% FPS, 1:
    ///    +3.4/+5.9%; shadows near you get blurrier. Re-applied once a second (a quality change in the game's settings
    ///    sets its own value: that becomes the value restored when the option is off). The implementation in place at the
    ///    start (3–4 alternating rounds): 133.8 → 135.4 (2) / 137.4 (1) fps; off gives back 4.
    ///  - SmallItemShadows: renderers of saved items (items, cars and their parts) smaller than RadiusM (0.5 m, bounds
    ///    extents) cast no sun shadow: bolts, gauges, cans, small car parts. +4.2/+5.0% FPS (at the start ~490 renderers).
    ///    Items are picked up when they start (the same FStart hook as TinyRendererCull), a few per frame; switching off
    ///    gives every changed renderer its shadow back. The implementation in place (bench.py --toggle-idle, 4 rounds):
    ///    134.7 → 139.4 fps (+3.5%), p99 9.30 → 9.05 ms.
    ///
    /// Not worth an option (same sweep): shadow distance 50/30 m (−1.3/+1.4%, noise), reflection probes off (+0.6%),
    /// distant/sky camera off (+0.7/−1.4%); the car park is CPU-bound and none of them changed it.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.RenderOptions.ShadowCascades (0 = the game's) /
    /// .SmallItemShadows / .RadiusM
    [HarmonyPatch]
    public static class RenderOptions
    {
        public static int ShadowCascades = 0;
        public static bool SmallItemShadows = false;
        public static float RadiusM = 0.5f;
        public static long ShadowsOff;

        private static bool _cascadesApplied;
        private static int _gameCascades, _ourCascades;
        private static float _cascadesAt;

        private static readonly Queue<GameObject> Pending = new Queue<GameObject>();
        private static readonly List<Renderer> Changed = new List<Renderer>();
        private static readonly HashSet<int> Seen = new HashSet<int>();
        private static bool _scanned;
        private static int _scene = -1;

        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(tosaveitemscript), nameof(tosaveitemscript.FStart), new System.Type[0]);
            yield return AccessTools.Method(typeof(tosaveitemscript), nameof(tosaveitemscript.FStart), new[] { typeof(uint) });
        }

        [HarmonyPostfix]
        private static void OnItemStarted(tosaveitemscript __instance)
        {
            if (SmallItemShadows && __instance != null) Pending.Enqueue(__instance.gameObject);
        }

        public static void Tick()
        {
            TickCascades();
            TickSmallShadows();
        }

        private static void TickCascades()
        {
            if (Time.unscaledTime < _cascadesAt) return;
            _cascadesAt = Time.unscaledTime + 1f;
            int cur = QualitySettings.shadowCascades;
            if (ShadowCascades <= 0)
            {
                if (_cascadesApplied) { QualitySettings.shadowCascades = _gameCascades; _cascadesApplied = false; }
                return;
            }
            if (cur == ShadowCascades) return;
            if (!_cascadesApplied || cur != _ourCascades) _gameCascades = cur;   // first time, or the game set its own value since
            QualitySettings.shadowCascades = ShadowCascades;
            _ourCascades = ShadowCascades;
            _cascadesApplied = true;
        }

        private static void TickSmallShadows()
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scene != _scene) { _scene = scene; Changed.Clear(); Seen.Clear(); Pending.Clear(); _scanned = false; }
            if (!SmallItemShadows)
            {
                if (Changed.Count > 0) RestoreShadows();
                Pending.Clear();
                _scanned = false;
                return;
            }
            if (mainscript.s == null || savedatascript.s == null) return;
            if (!_scanned)
            {
                _scanned = true;
                foreach (var t in Object.FindObjectsOfType<tosaveitemscript>()) Pending.Enqueue(t.gameObject);
            }
            for (int q = 0; q < 16 && Pending.Count > 0; q++)
            {
                var go = Pending.Dequeue();
                if (go == null) continue;
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    if (r is ParticleSystemRenderer || !r.enabled || !Seen.Add(r.GetInstanceID())) continue;
                    if (r.shadowCastingMode != ShadowCastingMode.On || r.bounds.extents.magnitude >= RadiusM) continue;
                    r.shadowCastingMode = ShadowCastingMode.Off;
                    Changed.Add(r);
                    ShadowsOff++;
                }
            }
        }

        private static void RestoreShadows()
        {
            foreach (var r in Changed)
                if (r != null && r.shadowCastingMode == ShadowCastingMode.Off) r.shadowCastingMode = ShadowCastingMode.On;
            Changed.Clear();
            Seen.Clear();
        }

        public static string Stats =>
            "{\"shadowCascades\":" + ShadowCascades + ",\"current\":" + QualitySettings.shadowCascades + ",\"smallItemShadows\":" +
            (SmallItemShadows ? "true" : "false") + ",\"changed\":" + Changed.Count + ",\"pending\":" + Pending.Count + ",\"shadowsOff\":" + ShadowsOff + "}";
    }
}

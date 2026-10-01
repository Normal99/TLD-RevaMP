using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Car mirrors: mainscript.MirrorsUpdate renders up to 5 nearest visible mirrors with their own camera
    /// (mirror.Turn → Camera.Render) every frame. Each mirror render is a full scene render of what's behind the car
    /// within the mirror draw distance (~80 m), including its own sun shadow maps. Measured: 3 renders/frame,
    /// 1.06–1.34 ms/frame main thread (seated at the start: the mirrors see the start houses: 226+130+107 parts).
    ///
    /// Throttle (NOT bit-exact; visual trade-off to be judged by the user): each mirror re-renders every EveryN frames,
    /// staggered so mirrors don't all render in the same frame; in between it keeps showing its last image (the
    /// render texture keeps it). A mirror that wasn't rendered for longer (just turned on / came into view) renders
    /// immediately. Everything else (material switch, far plane, camera placement) stays exactly as the game does it.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.MirrorThrottle.Enabled / .EveryN
    [HarmonyPatch(typeof(mainscript.mirror), nameof(mainscript.mirror.Turn))]
    public static class MirrorThrottle
    {
        /// On by default since v0.26.0 (user check 2026-09-26: every 2nd frame not noticeable, every 3rd noticeable).
        public static bool Enabled = true;
        public static int EveryN = 2;
        /// Render mirror images without sun shadows (each mirror render otherwise builds its own shadow maps).
        public static bool NoShadows = false;
        public static long Rendered, Skipped;

        private sealed class State { public int LastRender = -1000; public int Phase; }
        private static readonly ConditionalWeakTable<mainscript.mirror, State> States = new ConditionalWeakTable<mainscript.mirror, State>();
        private static int _nextPhase;

        [HarmonyPrefix]
        private static bool Prefix(mainscript.mirror __instance, bool turnOn, bool _render)
        {
            if (!Enabled || !turnOn || !_render || (EveryN <= 1 && !NoShadows)) return true;
            var m = __instance;
            var st = States.GetValue(m, _ => new State { Phase = _nextPhase++ });
            int f = Time.frameCount;
            // The game's Turn(true, true): material on, camera component off (rendered manually), then Render()
            m.R.material = m.mat;
            m.C.enabled = false;
            bool stale = f - st.LastRender > EveryN;               // just turned on / came back into view
            if (EveryN <= 1 || stale || (f + st.Phase) % EveryN == 0)
            {
                if (NoShadows)
                {
                    var q = QualitySettings.shadows;
                    QualitySettings.shadows = ShadowQuality.Disable;
                    try { m.C.Render(); } finally { QualitySettings.shadows = q; }
                }
                else m.C.Render();
                st.LastRender = f;
                Rendered++;
            }
            else Skipped++;
            return false;
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"everyN\":" + EveryN + ",\"rendered\":" + Rendered + ",\"skipped\":" + Skipped + "}";
    }
}

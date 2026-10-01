using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Every container (tankcapscript: canisters, barrels, oil cans, beer…) has a pour particle system that is always
    /// "playing" (looping) with emission off and no particles while nothing is poured. Unity still updates each one every
    /// frame. Measured: 300 resting items (≈150 such systems) → ParticleSystemBeginUpdateAll +0.29 ms/frame.
    ///
    /// After tankcapscript.Update: pause the pour system while emission is off and it has no particles; when the game
    /// switches emission on (pouring starts), play it again in the same Update, i.e. before this frame's particle update,
    /// so pouring starts on the same frame as before. NOT bit-exact (particle internals like the emitter's movement
    /// reference are reset on resume); nothing visible should change. Disabling resumes every system we paused.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.IdlePourPause.Enabled
    [HarmonyPatch(typeof(tankcapscript), "Update")]
    public static class IdlePourPause
    {
        /// On by default since v0.26.0 (user check 2026-09-26: pour looks normal).
        public static bool Enabled = true;
        public static long Pauses, Resumes;
        private static readonly HashSet<ParticleSystem> PausedByUs = new HashSet<ParticleSystem>();
        private static bool _wasEnabled;

        [HarmonyPostfix]
        private static void Postfix(tankcapscript __instance)
        {
            var ps = __instance.ps;
            if (ps == null || !__instance.started) return;
            if (!Enabled)
            {
                if (_wasEnabled) ResumeAll();
                return;
            }
            _wasEnabled = true;
            if (__instance.em.enabled)
            {
                if (PausedByUs.Remove(ps) && ps.isPaused) { ps.Play(false); Resumes++; }
            }
            else if (!PausedByUs.Contains(ps) && ps.isPlaying && ps.particleCount == 0)
            {
                ps.Pause(false);
                PausedByUs.Add(ps);
                Pauses++;
            }
        }

        private static void ResumeAll()
        {
            foreach (var ps in PausedByUs) if (ps != null && ps.isPaused) ps.Play(false);
            PausedByUs.Clear();
            _wasEnabled = false;
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"paused\":" + PausedByUs.Count + ",\"pauses\":" + Pauses + ",\"resumes\":" + Resumes + "}";
    }
}

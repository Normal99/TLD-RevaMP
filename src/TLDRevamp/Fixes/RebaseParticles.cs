using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Floating-origin rebase (mainscript.VisszaRakas, once per second: when the player is > 2,500 m from the origin every
    /// registered object is shifted so the player ends 2,000 m on the other side → about every 4.5 km of driving).
    /// Measured (2,145 objects, 300 items + 8 cars nearby): rebase frame 163 ms = the game's loop 2 ms + physics re-sync
    /// 25 ms + ~120 ms in ParticleSystemEndUpdateAll: every playing particle system sees its emitter jump ~4.5 km in one
    /// frame (emission over distance, world-space simulation/collision), i.e. invisible work far away.
    ///
    /// Pausing the playing systems across the rebase and resuming them at the start of the next frame (before particle
    /// update) removed it: 163 → 41 ms. Pause→Play resets the emitter's movement reference, so the spurious emission
    /// along the jump isn't simulated. NOT bit-exact (particle internals are reset once per rebase); nothing visible
    /// changes (the skipped work is the jump trail, kilometres away). Only systems still paused at resume are resumed, so
    /// a system the game stopped meanwhile stays stopped.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.RebaseParticles.Enabled
    [HarmonyPatch(typeof(mainscript), nameof(mainscript.VisszaRakas))]
    public static class RebaseParticles
    {
        /// On by default since v0.26.0 (user check 2026-09-26: nothing noticeable at the world shift).
        public static bool Enabled = true;
        public static long Rebases, PausedTotal;
        private static readonly List<ParticleSystem> Paused = new List<ParticleSystem>();
        private static int _pausedFrame = -1;

        [HarmonyPrefix]
        private static void Prefix(mainscript __instance)
        {
            // Same condition as the game's shift branch
            if (!Enabled || __instance.player == null || !(__instance.player.transform.position.sqrMagnitude > 6250000f)) return;
            Rebases++;
            foreach (var ps in Object.FindObjectsOfType<ParticleSystem>())
                if (ps.isPlaying) { ps.Pause(false); Paused.Add(ps); }
            PausedTotal += Paused.Count;
            _pausedFrame = Time.frameCount;
        }

        /// Runner.Update (next frame, before the particle update).
        public static void Tick()
        {
            if (Paused.Count == 0 || Time.frameCount == _pausedFrame) return;
            foreach (var ps in Paused) if (ps != null && ps.isPaused) ps.Play(false);
            Paused.Clear();
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"rebases\":" + Rebases + ",\"pausedTotal\":" + PausedTotal + "}";
    }
}

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Every item impact (pickupable.OnCollisionEnter) plays a sound through mainscript.PlayClipAtPoint, which
    /// instantiates a whole audio GameObject, plays it and destroys it after the clip. When many items collide at once
    /// (a falling pile: ~1,700 in one session), Unity runs out of voices, drops most of them and logs
    /// "Ran out of virtual channels" for each one. Hypothesis under test: creating/destroying those objects and the log spam
    /// are a large part of collision hitches.
    ///
    /// Mode 0: game behaviour. Mode 1: mute item impact sounds (EXPERIMENT ONLY, upper bound).
    /// Mode 2: limit, at most PerFrame new impact sounds per frame and MaxAlive playing at once; the rest are skipped,
    /// as Unity would drop them anyway once its voices run out.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.ImpactSoundLimiter.Mode / .PerFrame / .MaxAlive
    [HarmonyPatch(typeof(pickupable), "OnCollisionEnter")]
    public static class ImpactSoundLimiter
    {
        /// Default 2 (limit) since 2026-09-25: removes the sound/log-spam noise from collision measurements (user request).
        /// Audio quality still needs the user's listening check (parked).
        public static int Mode = 2;
        public static int PerFrame = 4;
        public static int MaxAlive = 32;

        public static long Allowed, Skipped;

        private static int _frame = -1, _thisFrame;
        private static readonly Queue<float> AliveUntil = new Queue<float>();

        [HarmonyPrefix]
        private static bool Prefix(pickupable __instance, Collision c)
        {
            if (Mode == 0) return true;
            var p = __instance;
            // Same condition as the game: only impacts that would actually play a sound count against the limits
            if (p.collideSound == null || !(!p.inInterior || p.pickedUp)) return true;
            float num = c.relativeVelocity.magnitude * p.collisionSoundM * (!p.pickedUp ? 0.3f : 1f);
            if (!(num > 0.075f)) return true;
            if (Mode == 1) { Skipped++; return false; }

            float now = Time.time;
            while (AliveUntil.Count > 0 && AliveUntil.Peek() <= now) AliveUntil.Dequeue();
            if (Time.frameCount != _frame) { _frame = Time.frameCount; _thisFrame = 0; }
            if (_thisFrame >= PerFrame || AliveUntil.Count >= MaxAlive) { Skipped++; return false; }

            // Allowed: let the game decide volume/threshold and play it; account for it as potentially alive
            _thisFrame++;
            Allowed++;
            AliveUntil.Enqueue(now + p.collideSound.length);
            return true;
        }

        public static string Stats =>
            "{\"mode\":" + Mode + ",\"perFrame\":" + PerFrame + ",\"maxAlive\":" + MaxAlive + ",\"allowed\":" + Allowed + ",\"skipped\":" + Skipped + "}";
    }
}

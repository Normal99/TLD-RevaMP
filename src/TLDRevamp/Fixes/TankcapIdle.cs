using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// tankcapscript (the cap/spout of every canister, barrel, can, bottle) runs its fluid logic every frame. For a closed,
    /// untouched container it still makes engine calls with no effect: psc.bounce/psc.dampen set to the same values,
    /// emission set to off again, AudioSource.Stop() on a silent source. Measured (300 mixed items resting, looking
    /// away): tankcapscript.Update grew 12.9 → 29.1 ms/s (profiled), the largest per-item script cost.
    ///
    /// The game's own Update with only its no-op writes skipped (GamePatch: these call sites swapped at runtime):
    ///  - psc.bounce/psc.dampen: written only when different from this script's last write. The only other writer
    ///    (tankcapparticledetectionscript.Tankolas) either writes the same defaults or writes other values together
    ///    with V = 5; any frame that counts V down clears the cache, so the defaults are written again when V reaches 0.
    ///  - em.enabled = false: skipped when emission is already off (only tankcapscript writes em.enabled).
    ///  - SSource.Stop(): skipped when not playing (Stop on a stopped source does nothing; no scheduled plays exist).
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.TankcapIdle.Enabled / .Verify (a skipped psc write reads the
    /// collision module back and compares it with the value the game would have written)
    [HarmonyPatch(typeof(tankcapscript), "Update")]
    public static class TankcapIdle
    {
        public static bool Enabled = false;
        public static bool Verify = false;
        public static long Calls, SkippedWrites, VerifyChecks, VerifyMismatches;
        public static int Patched;

        private sealed class Last { public bool Known; public float Bounce, Dampen; }
        private static readonly ConditionalWeakTable<tankcapscript, Last> Cache = new ConditionalWeakTable<tankcapscript, Last>();

        private static MethodInfo Our(string name) => AccessTools.Method(typeof(TankcapIdle), name);

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var p = new GamePatch(nameof(TankcapIdle), instructions);
            p.Stores(AccessTools.Field(typeof(tankcapscript), nameof(tankcapscript.V)), Our(nameof(StoreV)), 1);
            p.Calls(AccessTools.PropertySetter(typeof(ParticleSystem.CollisionModule), nameof(ParticleSystem.CollisionModule.bounce)), Our(nameof(SetBounce)), 1, withThis: true);
            p.Calls(AccessTools.PropertySetter(typeof(ParticleSystem.CollisionModule), nameof(ParticleSystem.CollisionModule.dampen)), Our(nameof(SetDampen)), 1, withThis: true);
            p.CallsWithConst(AccessTools.PropertySetter(typeof(ParticleSystem.EmissionModule), nameof(ParticleSystem.EmissionModule.enabled)), 0, Our(nameof(EmissionOff)), 2);
            p.Calls(AccessTools.Method(typeof(AudioSource), nameof(AudioSource.Stop)), Our(nameof(Stop)), 2);
            return p.Result(ref Patched);
        }

        [HarmonyPrefix]
        private static void Count() { if (Enabled) Calls++; }

        /// `V--` (Tankolas wrote psc lately): the last write is no longer known.
        public static void StoreV(tankcapscript t, int v)
        {
            t.V = v;
            if (Enabled && Cache.TryGetValue(t, out var last)) last.Known = false;
        }

        public static void SetBounce(ref ParticleSystem.CollisionModule psc, ParticleSystem.MinMaxCurve v, tankcapscript t)
        {
            if (!Enabled) { psc.bounce = v; return; }
            var last = Cache.GetValue(t, _ => new Last());
            if (last.Known && last.Bounce == t.bounce) { SkippedWrites++; if (Verify) Check(psc.bounce, v); return; }
            psc.bounce = v;
            last.Bounce = t.bounce;
        }

        // the game writes dampen right after bounce: this one completes what's known
        public static void SetDampen(ref ParticleSystem.CollisionModule psc, ParticleSystem.MinMaxCurve v, tankcapscript t)
        {
            if (!Enabled) { psc.dampen = v; return; }
            var last = Cache.GetValue(t, _ => new Last());
            if (last.Known && last.Dampen == t.dampen) { SkippedWrites++; if (Verify) Check(psc.dampen, v); }
            else psc.dampen = v;
            last.Dampen = t.dampen;
            last.Known = true;
        }

        private static void Check(ParticleSystem.MinMaxCurve now, ParticleSystem.MinMaxCurve wanted)
        {
            VerifyChecks++;
            if (now.mode != wanted.mode || now.constant != wanted.constant) VerifyMismatches++;
        }

        /// `em.enabled = false`, skipped when it's already off.
        public static void EmissionOff(ref ParticleSystem.EmissionModule em, bool value)
        {
            if (Enabled && !em.enabled) { SkippedWrites++; return; }
            em.enabled = value;
        }

        public static void Stop(AudioSource s)
        {
            if (Enabled && !s.isPlaying) { SkippedWrites++; return; }
            s.Stop();
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"patched\":" + Patched + ",\"calls\":" + Calls +
            ",\"skippedWrites\":" + SkippedWrites + ",\"verifyChecks\":" + VerifyChecks + ",\"verifyMismatches\":" + VerifyMismatches + "}";
    }
}

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
    /// Exact copy of tankcapscript.Update where only no-op writes are skipped:
    ///  - psc.bounce/psc.dampen: written only when different from this script's last write. The only other writer
    ///    (tankcapparticledetectionscript.Tankolas) either writes the same defaults or writes other values together
    ///    with V = 5; any frame with V > 0 clears the cache, so the defaults are written again when V reaches 0.
    ///  - em.enabled = false: skipped when emission is already off (only tankcapscript writes em.enabled).
    ///  - SSource.Stop(): skipped when not playing (Stop on a stopped source does nothing; no scheduled plays exist).
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.TankcapIdle.Enabled
    [HarmonyPatch(typeof(tankcapscript), "Update")]
    public static class TankcapIdle
    {
        public static bool Enabled = false;
        public static long Calls, SkippedWrites;

        private sealed class Last { public bool Known; public float Bounce, Dampen; }
        private static readonly ConditionalWeakTable<tankcapscript, Last> Cache = new ConditionalWeakTable<tankcapscript, Last>();
        private static readonly AccessTools.FieldRef<tankcapscript, float> TempFloat = AccessTools.FieldRefAccess<tankcapscript, float>("tempFloat");

        [HarmonyPrefix]
        private static bool Prefix(tankcapscript __instance)
        {
            if (!Enabled) return true;
            var t = __instance;
            Calls++;
            if (t.Tank == null || !t.started) return false;
            if (t.usable != null)
            {
                if (t.usable.turnable) t.valve = (t.usable.currentTurnState == 0) ? 0f : 1f;
                else if (t.usable.tuneAble) t.valve = t.usable.tunedFloat;
            }
            if (t.fillingV > 0) t.fillingV--;
            else t.filling = false;

            var last = Cache.GetValue(t, _ => new Last());
            if (t.V > 0)
            {
                t.V--;
                last.Known = false; // someone else (Tankolas) wrote psc
            }
            else
            {
                if (!last.Known || last.Bounce != t.bounce) { t.psc.bounce = t.bounce; } else SkippedWrites++;
                if (!last.Known || last.Dampen != t.dampen) { t.psc.dampen = t.dampen; } else SkippedWrites++;
                last.Known = true; last.Bounce = t.bounce; last.Dampen = t.dampen;
            }
            t.angle = Vector3.Angle(Vector3.up, t.transform.forward);
            t.cLerp = Mathf.InverseLerp(0f, t.Tank.F.maxC, t.Tank.F.GetAmount());
            if (t.noAngle)
            {
                t.angLerp = 1f;
            }
            else
            {
                t.pourAngle = Mathf.Lerp(t.defPourAngle, 100f, 1f - t.cLerp);
                t.angLerp = Mathf.InverseLerp(t.pourAngle, t.defPourAngle + 90f, t.angle);
                if (t.angle > 90f)
                {
                    TempFloat(t) = Mathf.InverseLerp(90f, 180f, t.angle);
                    if (TempFloat(t) > t.angLerp) t.angLerp = TempFloat(t);
                }
            }
            t.speed = t.angLerp * t.flowA * t.valve * mainscript.s.pourSpeed;
            if (t.valve < 1E-06f) t.valve = 0f;
            if ((t.angle > t.pourAngle || t.noAngle) && t.valve > 0f && !t.noPour && !t.tube)
            {
                if (t.Tank.F.GetAmount() > 0f)
                {
                    t.psmain.startSpeed = Mathf.Lerp(0f, t.maxSpeed, t.cLerp * t.angLerp);
                    if (t.BforceColor) t.psmain.startColor = Color.Lerp(t.Tank.F.GetColor(), t.forceColor, t.forceColorM);
                    else t.psmain.startColor = t.Tank.F.GetColor();
                    t.em.rateOverTime = Mathf.Lerp(t.minRate, t.maxRate, t.speed);
                    t.em.rateOverDistance = Mathf.Lerp(t.minRate, t.maxRate, t.speed) * 2f;
                    t.em.enabled = true;
                    t.poured = t.speed * Time.deltaTime;
                    t.Tank.F.ChangeWithoutMix(0f - t.poured);
                    t.Tank.Upd();
                    t.SSource.volume = Mathf.InverseLerp(mainscript.s.minPourSpeedSound, mainscript.s.maxPourSpeedSound, t.speed);
                    if (!t.SSource.isPlaying) t.SSource.Play();
                }
                else
                {
                    Idle(t);
                }
            }
            else if (!t.filling)
            {
                Idle(t);
            }
            return false;
        }

        /// The original's "em.enabled = false; poured = 0f; SSource.Stop();" without the no-op engine calls.
        private static void Idle(tankcapscript t)
        {
            if (t.em.enabled) t.em.enabled = false; else SkippedWrites++;
            t.poured = 0f;
            if (t.SSource.isPlaying) t.SSource.Stop(); else SkippedWrites++;
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"calls\":" + Calls + ",\"skippedWrites\":" + SkippedWrites + "}";
    }
}

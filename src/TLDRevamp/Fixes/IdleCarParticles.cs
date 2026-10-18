using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Every car keeps its exhaust smoke (carscript.psSmoke), coolant steam (carscript.CoolantSteam) and one dust system
    /// per wheel (talajnezo.ps, plus its grass trails) "playing" with emission off and no particles while it stands still
    /// with the engine off. Unity still updates each of them every frame (ParticleSystemBeginUpdateAll).
    ///
    /// The same rule as IdlePourPause (approved 2026-09-26), after the script that drives each system: pause it while its
    /// emission is off and it has no particles; play it again as soon as that script switches emission on, in the same
    /// Update/FixedUpdate, i.e. before this frame's particle update, so smoke, steam and dust start on the same frame as
    /// before. The game's own "Play() when !IsAlive()" checks are left alone: if one of them plays a system we paused,
    /// the system just counts as playing again (re-paused after that script if still idle). Disabling resumes every
    /// system we paused. NOT bit-exact (particle internals like the emitter's movement reference are reset on resume).
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.IdleCarParticles.Enabled
    [HarmonyPatch]
    public static class IdleCarParticles
    {
        /// On by default: car park (tools/carpark.py --ab, 16 cars, 3 runs of off,on,on,off): ParticleSystemBeginUpdateAll
        /// 0.72 → 0.44 ms/frame (−0.28 of ~11.5 ms), FixedUpdate scripts +0.02; FPS 84.8 → 85.2 / 83.8 → 91.4 (noisy, the
        /// first window of each run is low). ~100 systems paused (smoke + steam per car, dust of resting wheels).
        /// Functional (sit in a parked car, start, drive off over sand): smoke resumes with the engine (7 particles after
        /// 1 s, 378 after 8 s), each wheel's dust resumes as it rolls (17–18 particles after 8 s); with the fix off the same.
        public static bool Enabled = true;
        public static long Pauses, Resumes, GamePlays;
        private static readonly HashSet<ParticleSystem> PausedByUs = new HashSet<ParticleSystem>();
        private static bool _wasEnabled;

        [HarmonyPostfix, HarmonyPatch(typeof(carscript), "Update")]
        private static void CarPostfix(carscript __instance)
        {
            if (!Check()) return;
            var c = __instance;
            var smoke = c.psSmoke;
            var em = c.psSmokeEmission;
            if (smoke != null && em != null && em.Length == smoke.Length)
                for (int i = 0; i < smoke.Length; i++) Step(smoke[i], em[i].enabled);
            if (c.CoolantSteam != null && c.CoolantBoil != null) Step(c.CoolantSteam, c.CoolantSteamEmission.enabled);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(talajnezo), "FixedUpdate")]
        private static void WheelPostfix(talajnezo __instance)
        {
            if (!Check()) return;
            var t = __instance;
            if (t.ps != null) Step(t.ps, t.em.enabled && Emits(t.em));
            if (t.grasstrail != null) Step(t.grasstrail, t.gem.enabled);
        }

        /// A parked car's wheels rest on sand with dust emission on at a rate from the body's tiny resting jitter
        /// (velocity × rateM, measured 0.0002–0.0004 particles/s: one particle every ~40 min). Below MinRate counts as
        /// not emitting; anything not a plain constant rate counts as emitting.
        public static float MinRate = 0.01f;

        private static bool Emits(ParticleSystem.EmissionModule e)
        {
            var r = e.rateOverTime;
            if (r.mode != ParticleSystemCurveMode.Constant || r.constant >= MinRate || e.burstCount > 0) return true;
            var d = e.rateOverDistance;
            return d.mode != ParticleSystemCurveMode.Constant || d.constant > 0f;
        }

        private static bool Check()
        {
            if (Enabled) { _wasEnabled = true; return true; }
            if (_wasEnabled) ResumeAll();
            return false;
        }

        private static void Step(ParticleSystem ps, bool emitting)
        {
            if (ps == null) return;
            if (emitting)
            {
                if (PausedByUs.Remove(ps) && ps.isPaused) { ps.Play(false); Resumes++; }
                return;
            }
            if (PausedByUs.Contains(ps))
            {
                if (ps.isPaused) return;
                PausedByUs.Remove(ps);       // the game played or stopped it since: start over
                if (ps.isPlaying) GamePlays++;
            }
            if (ps.isPlaying && ps.particleCount == 0)
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

        /// Test helper: of the systems we hold paused, how many the game's IsAlive() calls dead (they would be replayed).
        public static string Probe()
        {
            int n = 0, alive = 0, paused = 0, stopped = 0;
            foreach (var ps in PausedByUs)
            {
                if (ps == null) continue;
                n++;
                if (ps.IsAlive()) alive++;
                if (ps.isPaused) paused++;
                if (ps.isStopped) stopped++;
            }
            return "{\"held\":" + n + ",\"isAlive\":" + alive + ",\"isPaused\":" + paused + ",\"isStopped\":" + stopped + "}";
        }

        /// Test helper: emission settings of the wheels' dust systems that are playing without particles.
        public static string DustProbe()
        {
            var sb = new System.Text.StringBuilder("[");
            int n = 0;
            foreach (var t in Object.FindObjectsOfType<talajnezo>())
            {
                var ps = t.ps;
                if (ps == null || n >= 6) continue;
                var e = ps.emission;
                if (n++ > 0) sb.Append(',');
                sb.Append("{\"en\":").Append(e.enabled ? 1 : 0).Append(",\"rot\":").Append(e.rateOverTime.constantMax).Append(",\"rotMode\":\"").Append(e.rateOverTime.mode)
                  .Append("\",\"rod\":").Append(e.rateOverDistance.constantMax).Append(",\"bursts\":").Append(e.burstCount).Append(",\"count\":").Append(ps.particleCount)
                  .Append(",\"playing\":").Append(ps.isPlaying ? 1 : 0).Append(",\"rateM\":").Append(t.rateM).Append('}');
            }
            return sb.Append(']').ToString();
        }

        /// Test helper: the player's car's smoke, steam and wheel dust right now.
        public static string CarProbe()
        {
            var car = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.Car : null;
            if (car == null) return "{\"error\":\"not in a car\"}";
            var sb = new System.Text.StringBuilder("{\"rpm\":").Append(car.Engine != null ? car.Engine.rpm : -1f).Append(",\"systems\":[");
            var list = new List<ParticleSystem>();
            if (car.psSmoke != null) list.AddRange(car.psSmoke);
            if (car.CoolantSteam != null) list.Add(car.CoolantSteam);
            foreach (var t in car.GetComponentsInChildren<talajnezo>()) if (t.ps != null) list.Add(t.ps);
            for (int i = 0; i < list.Count; i++)
            {
                var ps = list[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"n\":").Append(Json.Str(ps.name)).Append(",\"paused\":").Append(ps.isPaused ? 1 : 0).Append(",\"count\":").Append(ps.particleCount)
                  .Append(",\"en\":").Append(ps.emission.enabled ? 1 : 0).Append(",\"rate\":").Append(ps.emission.rateOverTime.constant).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"paused\":" + PausedByUs.Count + ",\"pauses\":" + Pauses +
            ",\"resumes\":" + Resumes + ",\"gamePlays\":" + GamePlays + "}";
    }
}

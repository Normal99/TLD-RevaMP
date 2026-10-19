using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        public const byte WorldClock = 97;   // f64 st, f32 world seconds, f32 rate, u16 sleep epoch, bool snap
        private static double _hostRateAt = -1, _hostScaledAt;
        private static float _hostRate = 1f, _hostScale = -1f;
        private static double _worldSentAt = -100, _radioSentAt = -100;
        private static double _worldStamp = -1, _worldPosition, _worldRate, _worldFollow, _worldFollowAt;
        private static double _worldWakeStamp = -1;
        private static bool _worldFollowing, _worldSnap;
        private static bool _worldHasEpoch, _worldAwaitingWake;
        private static ushort _worldEpoch, _worldSeenEpoch;
        private static napszakvaltakozas _worldInstance;

        /// Sample at the send frame, rather than exporting the last physics step's stale t.
        private static float WorldTimeNow()
        {
            var sw = napszakvaltakozas.s;
            return sw == null ? -1f : (float)ClockMath.Wrap(Time.timeSinceLevelLoadAsDouble + sw.tekeres - sw.startTime, sw.dt + sw.nt);
        }

        /// Hitches can clamp scaled time even at timeScale 1; measure that rate against session time.
        private static void HostClockTick()
        {
            if (!IsHost) return;
            double now = ModObjects.ClockTime, scaled = Time.timeAsDouble;
            if (_hostRateAt < 0 || _hostScale != Time.timeScale)
            {
                _hostRateAt = now; _hostScaledAt = scaled; _hostScale = Time.timeScale; _hostRate = _hostScale;
                _timeSyncAcc = 10f; _radioAcc = RadioResendS;
                _worldSentAt = _radioSentAt = -100;
            }
            else if (now - _hostRateAt >= 2)
            {
                _hostRate = Mathf.Max(0f, (float)((scaled - _hostScaledAt) / (now - _hostRateAt)));
                _hostRateAt = now; _hostScaledAt = scaled;
            }
        }

        private static void WriteWorldClock(bool snap)
        {
            W.Reset(); W.U8(WorldClock); W.F64(ModObjects.ClockTime); W.F32(WorldTimeNow());
            bool frozen = settingsscript.s != null && settingsscript.s.S.BDevMode && kaposztaleves.s != null && !kaposztaleves.s.settings.daynight;
            W.F32(frozen ? 0f : _hostRate); W.U16(_worldEpoch); W.Bool(snap);
        }

        private static void ReceiveWorldClock(NetReader r)
        {
            double stamp = r.F64(); float position = r.F32(), rate = r.F32(); ushort epoch = r.U16(); bool snap = r.Bool();
            if (r.Bad || IsHost || !Finite(position) || !Finite(rate) || rate < 0f || double.IsNaN(stamp) || double.IsInfinity(stamp)) return;
            if (!Fixes.SessionClocks.WorldEnabled) { ApplyWorldTime(position, snap); return; }
            if (!ClockMath.AcceptWorldSample(stamp, _worldStamp, _worldWakeStamp, snap, _worldAwaitingWake, epoch, _worldSeenEpoch)) { Fixes.SessionClocks.WorldStale++; return; }
            _worldStamp = stamp; _worldPosition = position; _worldRate = rate;
            _worldSnap |= snap || !_worldHasEpoch || epoch != _worldSeenEpoch;
            _worldSeenEpoch = epoch; _worldHasEpoch = true; _worldAwaitingWake = false;
            Fixes.SessionClocks.WorldReceived++;
        }

        /// Replace only the sky's local time advance; physics and gameplay keep their own time scale.
        [HarmonyLib.HarmonyPatch(typeof(napszakvaltakozas), "FixedUpdate")]
        private static class WorldFollowsHost
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(napszakvaltakozas __instance)
            {
                if (!InSession || IsHost || !Fixes.SessionClocks.WorldEnabled || _worldStamp < 0 || mainscript.s == null) { _worldFollowing = false; return; }
                if (mainscript.s.sleeping || _wasSleeping) { _worldFollowing = false; return; }
                double now = ModObjects.ClockTime, length = __instance.dt + __instance.nt;
                double target = ClockMath.Wrap(ClockMath.Project(_worldPosition, _worldStamp, now, _worldRate), length);
                bool force = _worldSnap || !_worldFollowing || _worldInstance != __instance;
                double next = ClockMath.Follow(_worldFollow, now - _worldFollowAt, target, _worldRate, length, force, out bool snapped);
                double natural = Time.timeSinceLevelLoadAsDouble + __instance.tekeres - __instance.startTime;
                __instance.tekeres += (float)(next - natural);
                _worldFollow = next; _worldFollowAt = now; _worldInstance = __instance; _worldFollowing = true; _worldSnap = false;
                if (snapped) Fixes.SessionClocks.WorldSnapped++; else Fixes.SessionClocks.WorldSlewed++;
            }
        }

        private static void ClockReset()
        {
            _hostRateAt = -1; _hostScale = -1; _hostRate = 1f;
            _worldStamp = _worldWakeStamp = -1; _worldFollowing = _worldSnap = false; _worldInstance = null;
            _worldHasEpoch = _worldAwaitingWake = false; _worldEpoch = _worldSeenEpoch = 0;
            _timeSyncAcc = 0f; _wasSleeping = false; _wokeAt = -100f;
            _worldSentAt = _radioSentAt = -100;
            _radioLast = new int[0]; _radioStartLast = new float[0]; _radioAcc = 0f; _radioAnchors.Clear(); _radioStamp = -1;
        }
    }
}

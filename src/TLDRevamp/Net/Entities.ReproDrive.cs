using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private static carscript _reproDrive;
        private static ReproRow _reproDriveInput;
        private static float _reproDriveUntil, _reproDriveNext;
        internal static void ReproDriveEnd() { if (_reproDrive != null) _reproDrive.ResetInput(); _reproDrive = null; _reproDriveNext = 0; }
        private static string ReproDriveAction(ReproRow r)
        {
            var it = ReproTarget(r.Net);
            if (it == null || it.car == null || !ByNet.TryGetValue(r.Net, out var e)) return "{\"error\":\"drive target absent\"}";
            if (r.X < 0 || r.X > 1 || r.Y < 0 || r.Y > 1 || r.Z < -1 || r.Z > 1 || r.A < 0 || r.A > 1 || r.B < 0 || r.B > 1 || r.C < 0 || r.C > 8191)
                return "{\"error\":\"invalid drive input\"}";
            if (!it.car.isPlayerDriving || e.Proxy || e.OwnerId != MyId) return ReproNotOwner();
            _reproDrive = it.car; _reproDriveInput = r; _reproDriveUntil = Time.realtimeSinceStartup + 0.35f; return "{\"ok\":true}";
        }
        [HarmonyPatch(typeof(carscript), nameof(carscript.GetPlayerInput))]
        private static class ReproDriveInput
        {
            private static void Postfix(carscript __instance)
            {
                if (ReproPlayback.Active)
                {
                    if (__instance != _reproDrive || !__instance.isPlayerDriving || Time.realtimeSinceStartup > _reproDriveUntil) return;
                    var c = __instance; var r = _reproDriveInput; int bits = (int)r.C;
                    c.ithrottle = (float)r.X; c.ibrake = (float)r.Y; c.isteer = (float)r.Z; c.iclutch = (float)r.A; c.ihandbrake = (float)r.B;
                    c.iignition = r.D != 0; c.ihorn = r.E != 0;
                    c.ihandbrakeBDown = (bits & 1) != 0; c.igrDown = (bits & 2) != 0; c.ignDown = (bits & 4) != 0;
                    c.ig1Down = (bits & 8) != 0; c.ig2Down = (bits & 16) != 0; c.ig3Down = (bits & 32) != 0;
                    c.ig4Down = (bits & 64) != 0; c.ig5Down = (bits & 128) != 0; c.ig6Down = (bits & 256) != 0;
                    c.iupshiftDown = (bits & 512) != 0; c.idownshiftDown = (bits & 1024) != 0; c.iignitionDown = (bits & 2048) != 0; c.iLightsBDown = (bits & 4096) != 0;
                    _reproDriveInput.C = 0; return;
                }
                if (!ReproRecorder.Recording || !__instance.isPlayerDriving) return;
                var car = __instance;
                int mask = (car.ihandbrakeBDown ? 1 : 0) | (car.igrDown ? 2 : 0) | (car.ignDown ? 4 : 0) | (car.ig1Down ? 8 : 0) |
                    (car.ig2Down ? 16 : 0) | (car.ig3Down ? 32 : 0) | (car.ig4Down ? 64 : 0) | (car.ig5Down ? 128 : 0) | (car.ig6Down ? 256 : 0) |
                    (car.iupshiftDown ? 512 : 0) | (car.idownshiftDown ? 1024 : 0) | (car.iignitionDown ? 2048 : 0) | (car.iLightsBDown ? 4096 : 0);
                if (mask == 0 && Time.realtimeSinceStartup < _reproDriveNext) return;
                _reproDriveNext = Time.realtimeSinceStartup + 0.1f;
                ReproRecorder.Add(new ReproRow { Kind = ReproKind.Drive, Net = TelemetryNet(car), X = car.ithrottle, Y = car.ibrake, Z = car.isteer,
                    A = car.iclutch, B = car.ihandbrake, C = mask, D = car.iignition ? 1 : 0, E = car.ihorn ? 1 : 0 });
            }
        }
    }
}

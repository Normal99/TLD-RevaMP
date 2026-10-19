using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        /// The clock of the vehicle pose actually drawn here, including Unity's interpolation between physics steps.
        /// Passenger samples use this owner's clock, never the passenger machine's unrelated monotonic clock.
        internal static bool RidingClock(uint net, out uint epoch, out double clock)
        {
            epoch = 0; clock = 0;
            if (!ByNet.TryGetValue(net, out var e) || e.Root == null) return false;
            epoch = e.Epoch;
            if (!e.Proxy) clock = Time.unscaledTimeAsDouble - (Time.timeAsDouble - Time.fixedTimeAsDouble);
            else
            {
                if (e.Ip == null || !e.Ip.Ready) return false;
                clock = e.Ip.RenderTime;
                var rb = e.Root.RB;
                if (e.MovedByBody && rb != null && rb.interpolation == RigidbodyInterpolation.Interpolate)
                {
                    double alpha = Mathf.Clamp((float)((Time.timeAsDouble - Time.fixedTimeAsDouble) / Time.fixedDeltaTime), 0, 1);
                    clock = e.RidingPreviousClock + (clock - e.RidingPreviousClock) * alpha;
                }
            }
            return true;
        }

        internal static void TelemetryRider(uint net, int player, int seat, double clock, Vector3 root, Vector3 pelvis)
        {
            if (!ByNet.TryGetValue(net, out var e)) return;
            TelemetryPose(e, 7, clock, mainscript.GlobalFromUnityPos(root), Quaternion.identity, default, player, seat, 4);
            TelemetryPose(e, 8, clock, mainscript.GlobalFromUnityPos(pelvis), Quaternion.identity, default, player, seat, 4);
        }
    }
}

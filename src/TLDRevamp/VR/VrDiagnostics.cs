using System;
using System.Text;

namespace TLDRevamp.VR
{
    /// The diagnostics of the VR contracts, as JSON for the debug bridge.
    ///
    /// The JSON carries states, counts and reasons. It never carries a pose: the contract objects have no pose fields,
    /// so there is nothing here that could be one. The unit test asserts that by reflection over the contract types
    /// and by checking that the rendered text holds no position-shaped numbers.
    public static class VrDiagnostics
    {
        public static string Json(VrContracts c)
        {
            if (c == null) return "{\"error\":\"no VR contracts\"}";
            var b = new StringBuilder();
            b.Append('{');
            b.Append("\"session\":").Append(Session(c.Session)).Append(',');
            b.Append("\"tracking\":").Append(Tracking(c.Tracking)).Append(',');
            b.Append("\"hands\":").Append(Hands(c)).Append(',');
            b.Append("\"ui\":").Append(Ui(c)).Append(',');
            b.Append("\"rig\":").Append(Rig(c.Rig)).Append(',');
            b.Append("\"locomotion\":").Append(Locomotion(c.Locomotion)).Append(',');
            b.Append("\"reconnect\":").Append(Reconnect(c.ReconnectState));
            b.Append('}');
            return b.ToString();
        }

        private static string Session(VrSessionMachine s)
        {
            var b = new StringBuilder();
            b.Append("{\"state\":").Append(Str(s.State.ToString().ToLowerInvariant()));
            b.Append(",\"reason\":").Append(Str(s.Reason.ToString().ToLowerInvariant()));
            b.Append(",\"vrMode\":").Append(Bool(s.VrMode));
            b.Append(",\"error\":").Append(Str(s.Error));
            b.Append(",\"starts\":").Append(s.StartCount);
            b.Append(",\"stops\":").Append(s.StopCount);
            b.Append(",\"failures\":").Append(s.FailureCount);
            b.Append('}');
            return b.ToString();
        }

        private static string Tracking(VrTrackingSet t)
        {
            var b = new StringBuilder();
            b.Append("{\"head\":").Append(One(t.Head));
            b.Append(",\"left\":").Append(One(t.LeftHand));
            b.Append(",\"right\":").Append(One(t.RightHand));
            b.Append('}');
            return b.ToString();
        }

        private static string One(VrTrackingMachine m)
        {
            var b = new StringBuilder();
            b.Append("{\"state\":").Append(Str(m.State.ToString().ToLowerInvariant()));
            b.Append(",\"losses\":").Append(m.LossCount);
            b.Append(",\"recoveries\":").Append(m.RecoveryCount);
            b.Append('}');
            return b.ToString();
        }

        private static string Hands(VrContracts c)
        {
            var b = new StringBuilder();
            b.Append("{\"left\":").Append(OneHand(c.LeftHand));
            b.Append(",\"right\":").Append(OneHand(c.RightHand));
            b.Append('}');
            return b.ToString();
        }

        private static string OneHand(VrHandMachine h)
        {
            var b = new StringBuilder();
            b.Append("{\"action\":").Append(Str(h.Action.ToString().ToLowerInvariant()));
            b.Append(",\"neutral\":").Append(Str(h.Pose.ToString().ToLowerInvariant()));
            b.Append(",\"presses\":").Append(h.Presses);
            b.Append(",\"releases\":").Append(h.Releases);
            b.Append(",\"cancels\":").Append(h.Cancels);
            b.Append('}');
            return b.ToString();
        }

        private static string Ui(VrContracts c)
        {
            var b = new StringBuilder();
            b.Append("{\"left\":").Append(OneUi(c.LeftUi));
            b.Append(",\"right\":").Append(OneUi(c.RightUi));
            b.Append('}');
            return b.ToString();
        }

        private static string OneUi(VrUiRayMachine u)
        {
            var b = new StringBuilder();
            b.Append("{\"state\":").Append(Str(u.State.ToString().ToLowerInvariant()));
            b.Append(",\"activations\":").Append(u.Activations);
            b.Append(",\"cancels\":").Append(u.Cancellations);
            b.Append('}');
            return b.ToString();
        }

        private static string Rig(VrRigPolicy r)
        {
            var b = new StringBuilder();
            b.Append("{\"reason\":").Append(Str(r.PolicyReason));
            b.Append(",\"headFromView\":").Append(Bool(r.HeadMayFollowCamera));
            b.Append(",\"allowBodyMotion\":").Append(Bool(r.AllowGameplayMotion));
            b.Append(",\"seated\":").Append(Bool(r.Seated));
            b.Append('}');
            return b.ToString();
        }

        private static string Locomotion(VrLocomotionPolicy l)
        {
            var b = new StringBuilder();
            b.Append("{\"seated\":").Append(Bool(l.Seated));
            b.Append(",\"driving\":").Append(Bool(l.Driving));
            b.Append(",\"suppressed\":").Append(l.SuppressedFrames);
            b.Append('}');
            return b.ToString();
        }

        private static string Reconnect(VrReconnectState r)
        {
            var b = new StringBuilder();
            b.Append("{\"clean\":").Append(Bool(!r.Any));
            b.Append(",\"seated\":").Append(Bool(r.Seated));
            b.Append(",\"grabbing\":").Append(Bool(r.GrabbingsHeld));
            b.Append(",\"uiPress\":").Append(Bool(r.UiPressOpen));
            b.Append(",\"lookHeld\":").Append(Bool(r.HeadLookHeld));
            b.Append(",\"recenter\":").Append(Bool(r.RecenterPending));
            b.Append(",\"duck\":").Append(Bool(r.DuckToggle));
            b.Append(",\"latched\":").Append(Bool(r.LocomotionLatched));
            b.Append('}');
            return b.ToString();
        }

        internal static string Str(string s)
        {
            var b = new StringBuilder().Append('"');
            foreach (char c in s ?? "")
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default: if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c); break;
                }
            return b.Append('"').ToString();
        }

        internal static string Bool(bool v) { return v ? "true" : "false"; }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TLDRevamp
{
    /// Test tooling: Killenger's Autopilot (beta port, loaded by ModHost — never in the public release) made aware of other
    /// players. The port exposes TLDAutopilot.Plugin.Game.Traffic; this fills it in by reflection (no compile-time link
    /// either way): the owner's velocity for display copies of other players' cars, other players on foot as pedestrians,
    /// and keep-right (home lane +1.8 m) while in a session. Outside a session the hooks return nothing and the lane
    /// setting goes back to the autopilot's own.
    public static class AutopilotBridge
    {
        public const float KeepRightM = 1.8f;
        private static Type _traffic;
        private static FieldInfo _lane;
        private static bool _wasInSession;
        public static string Info = "no autopilot";

        public static void Install()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _traffic = asm.GetType("TLDAutopilot.Plugin.Game.Traffic");
                    if (_traffic != null) break;
                }
                if (_traffic == null) return;
                _traffic.GetField("VelocityOf").SetValue(null, (Func<Transform, Vector3?>)(root => Net.Entities.ProxyVelocity(root)));
                _traffic.GetField("Pedestrians").SetValue(null, (Func<List<Vector3>>)(() => Net.Entities.InSession ? Net.RemotePlayers.OnFootPositions() : null));
                _lane = _traffic.GetField("HomeLaneOffsetM");
                Info = "traffic hooks installed";
                Plugin.Log.LogInfo("AutopilotBridge: " + Info);
            }
            catch (Exception e) { Info = "failed: " + e.Message; Plugin.Log.LogError("AutopilotBridge: " + e); }
        }

        /// Keep right while in a session (NaN = the autopilot's own lane setting).
        public static void Tick()
        {
            if (_lane == null) return;
            bool on = Net.Entities.InSession;
            if (on == _wasInSession) return;
            _wasInSession = on;
            _lane.SetValue(null, on ? KeepRightM : float.NaN);
        }

        public static string Status()
        {
            if (_traffic == null) return "{\"info\":" + Json.Str(Info) + "}";
            object F(string n) { var f = _traffic.GetField(n); return f != null ? f.GetValue(null) : null; }
            return "{\"info\":" + Json.Str(Info) + ",\"lane\":" + Json.Str(Convert.ToString(F("HomeLaneOffsetM"), System.Globalization.CultureInfo.InvariantCulture))
                   + ",\"followed\":" + F("Followed") + ",\"ignored\":" + F("Ignored") + ",\"pedestrians\":" + F("PedestriansAdded") + ",\"last\":" + Json.Str(Convert.ToString(F("Last"))) + "}";
        }
    }
}
